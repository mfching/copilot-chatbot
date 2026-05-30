using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace CopilotChatbot.Models;

public enum TaskStdinSource
{
    None,
    PreviousOutput,
    CopilotResponse,
    Literal
}

public enum TaskHandoffKind
{
    None,
    File,
    HttpPost,
    NamedPipe
}

public sealed class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New task";
    public bool Enabled { get; set; } = true;
    /// <summary>Standard 5-field cron expression (minute hour day month weekday). Empty/null = manual only.</summary>
    public string? CronExpression { get; set; }

    public TaskCommandSpec? PreCommand { get; set; }
    public TaskCopilotSpec? Copilot { get; set; }
    public TaskCommandSpec? PostCommand { get; set; }
    public TaskHandoffSpec? ExternalHandoff { get; set; }
}

public sealed class TaskCommandSpec
{
    public string Executable { get; set; } = "";
    /// <summary>Argument string template. Supports placeholders: {{copilot_response}}, {{copilot_response_file}}, {{pre_output}}, {{task_name}}, {{run_id}}, {{timestamp}}.</summary>
    public string ArgsTemplate { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public ObservableCollection<TaskEnvVar> EnvVars { get; set; } = [];
    public TaskStdinSource StdinSource { get; set; } = TaskStdinSource.None;
    public string? StdinLiteral { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class TaskEnvVar
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class TaskCopilotSpec
{
    /// <summary>Title of an open chat tab to reuse; if empty or tab not open, a hidden one-shot session is used.</summary>
    public string ChatTabName { get; set; } = "";
    public string? ModelId { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? SystemPrompt { get; set; }
    public string Prompt { get; set; } = "";
    /// <summary>If true and pre-command output exists, append it to the prompt.</summary>
    public bool AppendPreviousOutput { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class TaskHandoffSpec
{
    public TaskHandoffKind Kind { get; set; } = TaskHandoffKind.None;
    /// <summary>File path / URL / pipe name depending on Kind.</summary>
    public string Target { get; set; } = "";
    public bool FileAppend { get; set; }
    public string HttpMethod { get; set; } = "POST";
    public ObservableCollection<TaskEnvVar> HttpHeaders { get; set; } = [];
    /// <summary>Body template; supports the same placeholders as TaskCommandSpec.ArgsTemplate. When blank, the raw Copilot response is sent.</summary>
    public string BodyTemplate { get; set; } = "";
}

public enum TaskRunStatus
{
    Success,
    Skipped,
    Failed,
    Aborted
}

public sealed class TaskRunRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public TaskRunStatus Status { get; set; } = TaskRunStatus.Success;
    public string Trigger { get; set; } = "manual";
    public string? PreOutput { get; set; }
    public string? CopilotResponse { get; set; }
    public string? PostOutput { get; set; }
    public string? HandoffStatus { get; set; }
    public string? Error { get; set; }
}

public static class PlaceholderResolver
{
    public static string Resolve(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template))
            return template ?? "";

        var result = template;
        foreach (var kv in values)
        {
            result = result.Replace("{{" + kv.Key + "}}", kv.Value ?? "", StringComparison.Ordinal);
        }

        // Expand ${ENV_VAR} references at runtime
        result = Regex.Replace(result, @"\$\{([^}]+)\}",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);

        return result;
    }
}
