using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CopilotChatbot.Models;
using NCrontab;

namespace CopilotChatbot.Services;

public sealed class TaskSchedulerService : IDisposable
{
    private const int MaxCapturedBytes = 1024 * 1024; // 1 MB cap per stage
    private const int MaxRunsPerTaskOnDisk = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly HttpClient HttpClient = new();

    private readonly CopilotChatService _copilot;
    private readonly SettingsStore _settingsStore;
    private readonly DebugLogger _logger;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<string, ChatSessionView?> _findChatByTitle;
    private readonly string _runsRoot;

    private readonly System.Threading.Timer _timer;
    private readonly ConcurrentDictionary<string, DateTime> _lastFireUtc = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();

    public ObservableCollection<ScheduledTask> Tasks { get; } = new();

    public event Action<TaskRunRecord>? RunStarted;
    public event Action<TaskRunRecord>? RunCompleted;

    public TaskSchedulerService(
        CopilotChatService copilot,
        SettingsStore settingsStore,
        DebugLogger logger,
        Func<AppSettings> getSettings,
        Func<string, ChatSessionView?> findChatByTitle)
    {
        _copilot = copilot;
        _settingsStore = settingsStore;
        _logger = logger;
        _getSettings = getSettings;
        _findChatByTitle = findChatByTitle;

        _runsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CopilotChatbot", "scheduler-runs");
        Directory.CreateDirectory(_runsRoot);

        // Load tasks from settings
        var settings = _getSettings();
        foreach (var task in settings.ScheduledTasks)
            Tasks.Add(task);

        _timer = new System.Threading.Timer(OnTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    public void SaveTasks()
    {
        var settings = _getSettings();
        settings.ScheduledTasks = new ObservableCollection<ScheduledTask>(Tasks);
        _settingsStore.Save(settings);
    }

    public DateTime? GetNextOccurrenceUtc(ScheduledTask task)
    {
        if (string.IsNullOrWhiteSpace(task.CronExpression)) return null;
        try
        {
            return CrontabSchedule.Parse(task.CronExpression).GetNextOccurrence(DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<DateTime> GetNextOccurrencesUtc(string? cron, int count)
    {
        if (string.IsNullOrWhiteSpace(cron)) return Array.Empty<DateTime>();
        try
        {
            var schedule = CrontabSchedule.Parse(cron);
            return schedule.GetNextOccurrences(DateTime.UtcNow, DateTime.UtcNow.AddDays(7))
                .Take(count).ToList();
        }
        catch
        {
            return Array.Empty<DateTime>();
        }
    }

    public IReadOnlyList<TaskRunRecord> LoadHistory(string taskId, int max = 50)
    {
        var dir = Path.Combine(_runsRoot, SafeFileName(taskId));
        if (!Directory.Exists(dir)) return Array.Empty<TaskRunRecord>();

        var files = Directory.GetFiles(dir, "*.json")
            .OrderByDescending(f => f)
            .Take(max);

        var records = new List<TaskRunRecord>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var rec = JsonSerializer.Deserialize<TaskRunRecord>(json, JsonOptions);
                if (rec is not null) records.Add(rec);
            }
            catch { /* skip corrupt */ }
        }
        return records;
    }

    private void OnTick(object? state)
    {
        var nowUtc = DateTime.UtcNow;
        foreach (var task in Tasks.ToArray())
        {
            if (!task.Enabled || string.IsNullOrWhiteSpace(task.CronExpression)) continue;
            if (_running.ContainsKey(task.Id)) continue;

            CrontabSchedule schedule;
            try { schedule = CrontabSchedule.Parse(task.CronExpression); }
            catch { continue; }

            var lastFire = _lastFireUtc.TryGetValue(task.Id, out var last) ? last : nowUtc.AddMinutes(-1);
            var next = schedule.GetNextOccurrence(lastFire);
            if (next <= nowUtc)
            {
                _lastFireUtc[task.Id] = nowUtc;
                _ = Task.Run(() => RunAsync(task, "scheduled", CancellationToken.None));
            }
        }
    }

    public async Task<TaskRunRecord> RunAsync(ScheduledTask task, string trigger, CancellationToken cancellationToken)
    {
        var record = new TaskRunRecord
        {
            TaskId = task.Id,
            TaskName = task.Name,
            StartedAt = DateTimeOffset.Now,
            Trigger = trigger,
            Status = TaskRunStatus.Success
        };

        if (!_running.TryAdd(task.Id, 1))
        {
            record.Status = TaskRunStatus.Skipped;
            record.Error = "Task is already running; skipping this trigger.";
            record.FinishedAt = DateTimeOffset.Now;
            PersistRecord(record);
            RunCompleted?.Invoke(record);
            return record;
        }

        RunStarted?.Invoke(record);
        _logger.Log("TASK-RUN", $"Start [{task.Name}] trigger={trigger} run={record.RunId}");

        string? tempResponseFile = null;
        try
        {
            // --- Step 1: pre-command ---
            string? preOutput = null;
            if (task.PreCommand is { } pre && !string.IsNullOrWhiteSpace(pre.Executable))
            {
                var preCtx = BuildContext(record, copilotResponse: null, copilotResponseFile: null, preOutput: null);
                preOutput = await RunProcessAsync(pre, preCtx, copilotResponse: null, cancellationToken);
                record.PreOutput = preOutput;
            }

            // --- Step 2: optional Copilot turn ---
            string? copilotResponse = null;
            if (task.Copilot is { } cop && !string.IsNullOrWhiteSpace(cop.Prompt))
            {
                var boundChat = string.IsNullOrWhiteSpace(cop.ChatTabName)
                    ? null
                    : _findChatByTitle(cop.ChatTabName);
                var settings = _getSettings();
                var (resp, copErr) = await _copilot.RunTaskTurnAsync(cop, boundChat, preOutput, settings, cancellationToken);
                if (copErr is not null)
                    throw new InvalidOperationException("Copilot step failed: " + copErr);
                copilotResponse = resp;
                record.CopilotResponse = copilotResponse;
            }

            // --- Step 3: post-command ---
            if (task.PostCommand is { } post && !string.IsNullOrWhiteSpace(post.Executable))
            {
                if (copilotResponse is not null && NeedsTempFile(post.ArgsTemplate))
                {
                    tempResponseFile = Path.Combine(Path.GetTempPath(), $"copilotresp-{record.RunId}.txt");
                    await File.WriteAllTextAsync(tempResponseFile, copilotResponse, cancellationToken);
                }
                var postCtx = BuildContext(record, copilotResponse, tempResponseFile, preOutput);
                var postOutput = await RunProcessAsync(post, postCtx, copilotResponse, cancellationToken);
                record.PostOutput = postOutput;
            }

            // --- Step 4: external handoff ---
            if (task.ExternalHandoff is { Kind: not TaskHandoffKind.None } handoff)
            {
                if (copilotResponse is not null && tempResponseFile is null && NeedsTempFile(handoff.BodyTemplate + " " + handoff.Target))
                {
                    tempResponseFile = Path.Combine(Path.GetTempPath(), $"copilotresp-{record.RunId}.txt");
                    await File.WriteAllTextAsync(tempResponseFile, copilotResponse, cancellationToken);
                }
                var handoffCtx = BuildContext(record, copilotResponse, tempResponseFile, preOutput);
                record.HandoffStatus = await ExecuteHandoffAsync(handoff, handoffCtx, copilotResponse, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            record.Status = TaskRunStatus.Aborted;
            record.Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            record.Status = TaskRunStatus.Failed;
            record.Error = ex.Message;
            _logger.Log("TASK-RUN-ERROR", $"[{task.Name}] {ex}");
        }
        finally
        {
            record.FinishedAt = DateTimeOffset.Now;
            if (tempResponseFile is not null)
            {
                try { File.Delete(tempResponseFile); } catch { }
            }
            _running.TryRemove(task.Id, out _);
            PersistRecord(record);
            _logger.Log("TASK-RUN", $"End [{task.Name}] status={record.Status}");
            RunCompleted?.Invoke(record);
        }

        return record;
    }

    private static bool NeedsTempFile(string? template) =>
        !string.IsNullOrEmpty(template) && template.Contains("{{copilot_response_file}}", StringComparison.Ordinal);

    private static Dictionary<string, string?> BuildContext(
        TaskRunRecord record, string? copilotResponse, string? copilotResponseFile, string? preOutput) =>
        new()
        {
            ["copilot_response"] = copilotResponse ?? "",
            ["copilot_response_file"] = copilotResponseFile ?? "",
            ["pre_output"] = preOutput ?? "",
            ["task_name"] = record.TaskName,
            ["run_id"] = record.RunId,
            ["timestamp"] = record.StartedAt.ToString("yyyy-MM-ddTHH:mm:ssK")
        };

    private async Task<string> RunProcessAsync(
        TaskCommandSpec spec,
        IReadOnlyDictionary<string, string?> ctx,
        string? copilotResponse,
        CancellationToken cancellationToken)
    {
        var args = PlaceholderResolver.Resolve(spec.ArgsTemplate, ctx);

        var psi = new ProcessStartInfo
        {
            FileName = spec.Executable,
            Arguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "" : spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = spec.StdinSource != TaskStdinSource.None,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var ev in spec.EnvVars)
        {
            if (!string.IsNullOrWhiteSpace(ev.Name))
                psi.Environment[ev.Name] = PlaceholderResolver.Resolve(ev.Value, ctx);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {spec.Executable}");

        if (spec.StdinSource != TaskStdinSource.None)
        {
            var stdin = spec.StdinSource switch
            {
                TaskStdinSource.CopilotResponse => copilotResponse ?? "",
                TaskStdinSource.PreviousOutput => ctx["pre_output"] ?? "",
                TaskStdinSource.Literal => PlaceholderResolver.Resolve(spec.StdinLiteral ?? "", ctx),
                _ => ""
            };
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }

        var stdoutTask = ReadCappedAsync(proc.StandardOutput, cancellationToken);
        var stderrTask = ReadCappedAsync(proc.StandardError, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds > 0 ? spec.TimeoutSeconds : 120));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new TimeoutException($"Command '{spec.Executable}' exceeded {spec.TimeoutSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = new StringBuilder();
        if (!string.IsNullOrEmpty(stdout)) combined.Append(stdout);
        if (!string.IsNullOrEmpty(stderr))
        {
            if (combined.Length > 0) combined.AppendLine();
            combined.AppendLine("[stderr]");
            combined.Append(stderr);
        }
        if (proc.ExitCode != 0)
        {
            if (combined.Length > 0) combined.AppendLine();
            combined.Append($"[exit code: {proc.ExitCode}]");
            throw new InvalidOperationException($"Command '{spec.Executable}' exited with code {proc.ExitCode}.\n{combined}");
        }
        return combined.ToString();
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (sb.Length + read > MaxCapturedBytes)
            {
                sb.Append(buffer, 0, MaxCapturedBytes - sb.Length);
                sb.Append("\n[...truncated...]");
                // drain rest
                while (await reader.ReadAsync(buffer, ct) > 0) { }
                break;
            }
            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private async Task<string> ExecuteHandoffAsync(
        TaskHandoffSpec handoff,
        IReadOnlyDictionary<string, string?> ctx,
        string? copilotResponse,
        CancellationToken cancellationToken)
    {
        var target = PlaceholderResolver.Resolve(handoff.Target, ctx);
        var body = string.IsNullOrWhiteSpace(handoff.BodyTemplate)
            ? (copilotResponse ?? "")
            : PlaceholderResolver.Resolve(handoff.BodyTemplate, ctx);

        switch (handoff.Kind)
        {
            case TaskHandoffKind.File:
                if (handoff.FileAppend)
                    await File.AppendAllTextAsync(target, body, cancellationToken);
                else
                    await File.WriteAllTextAsync(target, body, cancellationToken);
                return $"file:{(handoff.FileAppend ? "appended" : "written")} {target}";

            case TaskHandoffKind.HttpPost:
            {
                var method = string.IsNullOrWhiteSpace(handoff.HttpMethod) ? "POST" : handoff.HttpMethod.ToUpperInvariant();
                using var req = new HttpRequestMessage(new HttpMethod(method), target)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                foreach (var h in handoff.HttpHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(h.Name))
                        req.Headers.TryAddWithoutValidation(h.Name, PlaceholderResolver.Resolve(h.Value, ctx));
                }
                using var resp = await HttpClient.SendAsync(req, cancellationToken);
                return $"http:{(int)resp.StatusCode} {resp.ReasonPhrase}";
            }

            case TaskHandoffKind.NamedPipe:
            {
                await using var pipe = new NamedPipeClientStream(".", target, PipeDirection.Out);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.ConnectAsync(connectCts.Token);
                var bytes = Encoding.UTF8.GetBytes(body);
                await pipe.WriteAsync(bytes, cancellationToken);
                return $"pipe:wrote {bytes.Length} bytes to {target}";
            }

            default:
                return "none";
        }
    }

    private void PersistRecord(TaskRunRecord record)
    {
        try
        {
            var dir = Path.Combine(_runsRoot, SafeFileName(record.TaskId));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{record.StartedAt:yyyyMMdd-HHmmss}-{record.RunId[..8]}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(record, JsonOptions));

            // prune
            var files = Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).Skip(MaxRunsPerTaskOnDisk);
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("TASK-RUN-PERSIST-ERROR", ex.Message);
        }
    }

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public void Dispose() => _timer.Dispose();
}
