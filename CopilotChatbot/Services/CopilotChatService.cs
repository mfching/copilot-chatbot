using System.Collections.Concurrent;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CopilotChatbot.Models;
using GitHub.Copilot.SDK;
using System.Management.Automation.Language;

namespace CopilotChatbot.Services;

public sealed class CopilotChatService : IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly Func<ChatSessionView, PermissionPrompt, Task<PermissionPromptDecision>> _permissionPrompt;
    private readonly Func<ChatSessionView, UserInputPrompt, Task<UserInputPromptResult>> _userInputPrompt;
    private readonly DebugLogger _logger;
    private CopilotClient? _client;
    private readonly ConcurrentDictionary<ChatSessionView, CopilotSession> _sessions = [];
    private readonly ConcurrentDictionary<string, byte> _sessionPermissionApprovals = [];
    private readonly ConcurrentQueue<ChatUpdate> _pendingChatUpdates = new();
    private readonly ConcurrentDictionary<ChatSessionView, ResponseBufferOptions> _responseBufferOptions = [];
    private readonly Dictionary<string, McpServerConfig> _mcpServerConfigs = new();
    private readonly HashSet<string> _mcpServerNames = new(StringComparer.OrdinalIgnoreCase);
    private int _chatUpdateFlushScheduled;
    private volatile SessionCapabilitiesSnapshot _capabilitiesSnapshot = new([], [], []);
    private volatile CopilotUsageStatus? _lastUsage;
    private const int MinResponseBufferIntervalMs = 500;
    private const int MaxResponseBufferIntervalMs = 2000;
    public SessionCapabilitiesSnapshot CapabilitiesSnapshot => _capabilitiesSnapshot;
    public CopilotUsageStatus? LastUsage => _lastUsage;
    public event Action<CopilotUsageStatus>? UsageUpdated;
    public event Action<ChatSessionView, bool>? SessionPendingChanged;
    public event Action<ChatSessionView, string?>? StatusChanged;
    public event Action<ChatSessionView>? ChatUpdated;

    public CopilotChatService(
        SettingsStore settingsStore,
        Func<ChatSessionView, PermissionPrompt, Task<PermissionPromptDecision>> permissionPrompt,
        Func<ChatSessionView, UserInputPrompt, Task<UserInputPromptResult>> userInputPrompt,
        DebugLogger logger)
    {
        _settingsStore = settingsStore;
        _permissionPrompt = permissionPrompt;
        _userInputPrompt = userInputPrompt;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ModelChoice>> ListModelsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientAsync(settings, cancellationToken);
        var models = await client.ListModelsAsync(cancellationToken);
        return models
            .OrderBy(model => model.Name ?? model.Id)
            .Select(model => new ModelChoice
            {
                Id = model.Id ?? "",
                Name = model.Name ?? "",
                SupportsReasoningEffort = model.Capabilities?.Supports?.ReasoningEffort == true,
                ReasoningEfforts = model.SupportedReasoningEfforts?.ToArray() ?? [],
                DefaultReasoningEffort = model.DefaultReasoningEffort,
                BillingMultiplier = model.Billing?.Multiplier
            })
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .ToArray();
    }

    public async Task<CopilotRuntimeStatus> GetRuntimeStatusAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var client = await EnsureClientAsync(settings, cancellationToken);
        var status = await client.GetStatusAsync(cancellationToken);
        var auth = await client.GetAuthStatusAsync(cancellationToken);

        return new CopilotRuntimeStatus(
            client.State == ConnectionState.Connected,
            status.Version ?? "unknown",
            status.ProtocolVersion,
            auth.IsAuthenticated,
            auth.Login ?? "",
            auth.AuthType ?? "",
            auth.StatusMessage ?? "");
    }

    public async Task SendAsync(ChatSessionView chat, string prompt, AppSettings settings, ModelChoice? model, string? reasoningEffort)
    {
        SetResponseBufferOptions(chat, settings);
        var session = await EnsureSessionAsync(chat, settings, model, reasoningEffort);
        _logger.LogBlock("USER-SEND", prompt);
        SetPending(chat, true);
        try
        {
            await session.SendAsync(new MessageOptions { Prompt = prompt });
        }
        catch
        {
            SetPending(chat, false);
            throw;
        }
    }

    public async Task UpdateSessionSettingsAsync(ChatSessionView chat, string modelId, string? reasoningEffort)
    {
        if (_sessions.TryGetValue(chat, out var session))
        {
            _logger.Log("UPDATE-SETTINGS", $"Updating session {chat.CopilotSessionId} to model={modelId} | reasoning={reasoningEffort ?? "default"}");
            await session.SetModelAsync(modelId, reasoningEffort);
        }
    }

    public async Task AbortAsync(ChatSessionView chat)
    {
        if (_sessions.TryGetValue(chat, out var session))
        {
            _logger.Log("ABORT", $"User aborted session {chat.CopilotSessionId}");
            await session.AbortAsync();
        }

        SetPending(chat, false);
    }

    public async Task<SessionCapabilitiesSnapshot> GetCapabilitiesSnapshotAsync(ChatSessionView? chat, CancellationToken cancellationToken = default)
    {
        if (chat is null || !_sessions.TryGetValue(chat, out var session))
        {
            return _capabilitiesSnapshot;
        }

        try
        {
            var result = await session.Rpc.Agent.ListAsync(cancellationToken);
            var customAgents = result.Agents
                .Select(agent => new AgentInfo(
                    string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName,
                    "loaded",
                    GetString(agent, "Source") ?? "custom"))
                .Where(agent => !string.IsNullOrWhiteSpace(agent.Name) && agent.Name != "?")
                .ToList();

            if (customAgents.Count > 0)
            {
                var current = _capabilitiesSnapshot;
                _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(
                    current.McpServers,
                    MergeAgents(current.Agents, customAgents),
                    current.Skills);
            }
        }
        catch (Exception ex)
        {
            _logger.Log("AGENTS-LIST-ERROR", ex.Message);
        }

        return _capabilitiesSnapshot;
    }

    public async Task CloseSessionAsync(ChatSessionView chat)
    {
        if (_sessions.TryRemove(chat, out var session))
        {
            try
            {
                await session.DisposeAsync();
            }
            catch
            {
                // Closing a tab should not fail if the CLI already dropped the session.
            }
        }
    }

    /// <summary>
    /// Returns the most recently cached usage status, or fetches it by sending a minimal
    /// probe to a temporary session that is invisible to the user's chat.
    /// Returns (status, errorMessage) — errorMessage is non-null when the probe failed.
    /// </summary>
    public async Task<(CopilotUsageStatus? Status, string? Error)> FetchUsageAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_lastUsage is not null)
            return (_lastUsage, null);

        CopilotSession? probe = null;
        try
        {
            var client = await EnsureClientAsync(settings, cancellationToken);
            var token = await ResolveGitHubTokenAsync(settings.EffectiveGitHubToken);
            probe = await client.CreateSessionAsync(new SessionConfig
            {
                Model = settings.SelectedModelId,
                GitHubToken = token,
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll
            });

            var tcs = new TaskCompletionSource<CopilotUsageStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.On(evt =>
            {
                _logger.Log("FETCH-USAGE-EVENT", evt.GetType().Name);
                if (evt is AssistantUsageEvent usage)
                {
                    var status = ToUsageStatus(usage.Data);
                    _lastUsage = status;
                    UsageUpdated?.Invoke(status);
                    tcs.TrySetResult(status);
                }
            });

            _logger.Log("FETCH-USAGE", "Sending probe message...");
            await probe.SendAsync(new MessageOptions { Prompt = "ok" });
            _logger.Log("FETCH-USAGE", $"SendAsync returned. TCS completed: {tcs.Task.IsCompleted}");

            if (!tcs.Task.IsCompleted)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await tcs.Task.WaitAsync(timeoutCts.Token); }
                catch (OperationCanceledException) { }
            }

            _logger.Log("FETCH-USAGE", $"Done. _lastUsage is {(_lastUsage is null ? "null" : _lastUsage.Model)}");
        }
        catch (Exception ex)
        {
            _logger.Log("FETCH-USAGE-ERROR", ex.ToString());
            return (_lastUsage, ex.Message);
        }
        finally
        {
            if (probe is not null)
            {
                try { await probe.DisposeAsync(); } catch { }
            }
        }

        return (_lastUsage, null);
    }

    /// <summary>
    /// Finds an open chat tab by title. Returns null if not found or session not started.
    /// </summary>
    public ChatSessionView? FindLiveChatByTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return _sessions.Keys.FirstOrDefault(c =>
            string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs one Copilot turn for a scheduled task. If <paramref name="boundChat"/> has a live session,
    /// it reuses that session (messages are visible in the chat tab). Otherwise creates a hidden one-shot
    /// session using the supplied system prompt / model / reasoning settings.
    /// Returns the assistant's final message text, or null on timeout/error.
    /// </summary>
    public async Task<(string? Response, string? Error)> RunTaskTurnAsync(
        Models.TaskCopilotSpec spec,
        ChatSessionView? boundChat,
        string? previousOutput,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var fullPrompt = spec.AppendPreviousOutput && !string.IsNullOrWhiteSpace(previousOutput)
            ? spec.Prompt + "\n\n" + previousOutput
            : spec.Prompt;

        var timeout = TimeSpan.FromSeconds(spec.TimeoutSeconds > 0 ? spec.TimeoutSeconds : 300);

        if (boundChat is not null && _sessions.TryGetValue(boundChat, out var liveSession))
        {
            return await RunBoundTurnAsync(liveSession, boundChat, spec, fullPrompt, settings, timeout, cancellationToken);
        }

        return await RunHiddenTurnAsync(spec, fullPrompt, settings, timeout, cancellationToken);
    }

    private async Task<(string? Response, string? Error)> RunBoundTurnAsync(
        CopilotSession session,
        ChatSessionView chat,
        Models.TaskCopilotSpec spec,
        string fullPrompt,
        AppSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = session.On(evt =>
        {
            if (evt is AssistantMessageEvent msg && !string.IsNullOrWhiteSpace(msg.Data.Content))
            {
                tcs.TrySetResult(msg.Data.Content);
            }
        });

        try
        {
            var model = !string.IsNullOrWhiteSpace(spec.ModelId)
                ? new ModelChoice { Id = spec.ModelId!, Name = spec.ModelId! }
                : null;
            await SendAsync(chat, fullPrompt, settings, model, spec.ReasoningEffort);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var response = await tcs.Task.WaitAsync(cts.Token);
            return (response, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "Timed out waiting for Copilot response.");
        }
        catch (Exception ex)
        {
            _logger.Log("TASK-COPILOT-ERROR", ex.ToString());
            return (null, ex.Message);
        }
        finally
        {
            (subscription as IDisposable)?.Dispose();
        }
    }

    private async Task<(string? Response, string? Error)> RunHiddenTurnAsync(
        Models.TaskCopilotSpec spec,
        string fullPrompt,
        AppSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CopilotSession? probe = null;
        try
        {
            var client = await EnsureClientAsync(settings, cancellationToken);
            var token = await ResolveGitHubTokenAsync(settings.EffectiveGitHubToken);
            probe = await client.CreateSessionAsync(new SessionConfig
            {
                Model = !string.IsNullOrWhiteSpace(spec.ModelId) ? spec.ModelId : settings.SelectedModelId,
                ReasoningEffort = string.IsNullOrWhiteSpace(spec.ReasoningEffort) ? null : spec.ReasoningEffort,
                GitHubToken = token,
                Streaming = true,
                SystemMessage = string.IsNullOrWhiteSpace(spec.SystemPrompt) ? null : new SystemMessageConfig { Content = spec.SystemPrompt },
                OnPermissionRequest = PermissionHandler.ApproveAll
            });

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            probe.On(evt =>
            {
                if (evt is AssistantMessageEvent msg && !string.IsNullOrWhiteSpace(msg.Data.Content))
                {
                    tcs.TrySetResult(msg.Data.Content);
                }
            });

            await probe.SendAsync(new MessageOptions { Prompt = fullPrompt });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var response = await tcs.Task.WaitAsync(cts.Token);
                return (response, null);
            }
            catch (OperationCanceledException)
            {
                return (null, "Timed out waiting for Copilot response.");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("TASK-COPILOT-HIDDEN-ERROR", ex.ToString());
            return (null, ex.Message);
        }
        finally
        {
            if (probe is not null)
            {
                try { await probe.DisposeAsync(); } catch { }
            }
        }
    }

    public async Task ResumeSessionAsync(ChatSessionView chat, AppSettings settings, ModelChoice? model, string? reasoningEffort)
    {
        SetResponseBufferOptions(chat, settings);
        if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
        {
            throw new InvalidOperationException("The saved chat does not have a Copilot session id.");
        }

        if (_sessions.TryGetValue(chat, out _))
        {
            return;
        }

        var client = await EnsureClientAsync(settings);
        await client.GetSessionMetadataAsync(chat.CopilotSessionId);
        var token = await ResolveGitHubTokenAsync(settings.EffectiveGitHubToken);
        var customAgents = LoadAgentsFromDirectories(settings);
        if (customAgents?.Count > 0)
        {
            var agentInfos = customAgents
                .Select(a => new AgentInfo(a.DisplayName ?? a.Name, "loaded", "file"))
                .ToList();
            _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(_capabilitiesSnapshot.McpServers, agentInfos, _capabilitiesSnapshot.Skills);
        }

        var session = await client.ResumeSessionAsync(chat.CopilotSessionId, new ResumeSessionConfig
        {
            Model = model?.Id ?? settings.SelectedModelId,
            ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
            Streaming = true,
            GitHubToken = token,
            SystemMessage = string.IsNullOrWhiteSpace(chat.SystemPrompt) ? null : new SystemMessageConfig { Content = chat.SystemPrompt },
            McpServers = _mcpServerConfigs.Count > 0 ? _mcpServerConfigs : null,
            SkillDirectories = GetEffectiveSkillDirectories(settings),
            CustomAgents = customAgents,
            OnPermissionRequest = async (request, _) => await EvaluatePermissionAsync(chat, request, _settingsStore.Load()),
            OnUserInputRequest = async (request, _) =>
            {
                try { return await EvaluateUserInputAsync(chat, request); }
                catch (Exception ex)
                {
                    _logger.Log("ASK-USER-FATAL", ex.ToString());
                    return new UserInputResponse { Answer = "", WasFreeform = true };
                }
            }
        });

        chat.CopilotSessionId = session.SessionId;
        chat.IsSessionMissing = false;
        _logger.Log("SESSION", $"Resumed session {session.SessionId} | model={model?.Id ?? settings.SelectedModelId} | reasoning={reasoningEffort ?? "default"}");
        session.On(evt => HandleEvent(chat, evt));
        _sessions[chat] = session;
    }

    private async Task<CopilotClient> EnsureClientAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return _client;
        }

        var token = await ResolveGitHubTokenAsync(settings.EffectiveGitHubToken);
        var env = CreateProcessEnvironment();
        foreach (var secret in settings.UserSecrets.Where(s => !string.IsNullOrWhiteSpace(s.EnvironmentVariable)))
        {
            env[secret.EnvironmentVariable] = _settingsStore.UnprotectSecret(secret.EncryptedValue);
        }

        // Inject the GitHub token into the child process environment so the builtin
        // github-mcp-server (and any gh-based tools) can authenticate to the GitHub API.
        // The Copilot CLI's GitHubToken option only authenticates the chat API; MCP servers
        // spawned as subprocesses rely on GH_TOKEN / GITHUB_TOKEN environment variables.
        if (!string.IsNullOrWhiteSpace(token))
        {
            env["GH_TOKEN"] = token;
            env["GITHUB_TOKEN"] = token;
        }

        var bundledCliPath = ResolveBundledCliPath();
        var cwd = !string.IsNullOrWhiteSpace(settings.WorkingDirectory) && Directory.Exists(settings.WorkingDirectory)
            ? settings.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var client = new CopilotClient(new CopilotClientOptions
        {
            CliPath = bundledCliPath,
            GitHubToken = token,
            Environment = env,
            Cwd = cwd
        });

        try
        {
            await client.StartAsync(cancellationToken);
            _client = client;
            await LoadUserMcpConfigAsync(client, cwd, cancellationToken);
            return _client;
        }
        catch
        {
            await DisposeClientQuietlyAsync(client);
            throw;
        }
    }

    // Reads MCP server configs from known locations and registers each server with the SDK.
    // Supported root keys: "mcpServers" (Copilot CLI format) and "servers" (VS Code mcp.json format).
    // Searched paths are loaded first-wins by server name; existing entries are not replaced.
    private async Task LoadUserMcpConfigAsync(
        CopilotClient client,
        string cwd,
        CancellationToken cancellationToken)
    {
        var candidatePaths = new[]
        {
            // User-level
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "mcp-config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitHub Copilot", "mcp-config.json"),
            // Project-level (VS Code Copilot / GitHub Copilot CLI standard — takes precedence)
            Path.Combine(cwd, ".github", "copilot", "mcp.json"),
            Path.Combine(cwd, ".copilot", "mcp-config.json"),
        };

        await EnsureDefaultGitHubMcpServerConfigAsync(candidatePaths[0], cancellationToken);

        foreach (var path in candidatePaths.Where(File.Exists).Distinct())
            await LoadMcpConfigFileAsync(client, path, cancellationToken);

        // Built-in servers last as a fallback. Existing user/project entries win by name.
        await LoadBuiltinMcpServersAsync(client, cancellationToken);
    }

    private async Task EnsureDefaultGitHubMcpServerConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            var root = File.Exists(configPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken)) as JsonObject
                : [];
            if (root is null)
            {
                _logger.Log("MCP-CONFIG-ERROR", $"Cannot add default GitHub MCP server because {configPath} is not a JSON object.");
                return;
            }

            var servers = root["mcpServers"] as JsonObject;
            if (servers is null)
            {
                servers = [];
                root["mcpServers"] = servers;
            }

            if (servers.ContainsKey("github-mcp-server"))
            {
                return;
            }

            servers["github-mcp-server"] = CreateDefaultGitHubMcpServerNode();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(
                configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            _logger.Log("MCP-CONFIG", $"Added default GitHub MCP server to {configPath}");
        }
        catch (Exception ex)
        {
            _logger.Log("MCP-CONFIG-ERROR", $"Failed to add default GitHub MCP server to {configPath}: {ex.Message}");
        }
    }

    private static JsonObject CreateDefaultGitHubMcpServerNode()
    {
        return new JsonObject
        {
            ["type"] = "http",
            ["url"] = "https://api.githubcopilot.com/mcp/readonly",
            ["headers"] = new JsonObject
            {
                ["Authorization"] = "Bearer $GITHUB_TOKEN"
            },
            ["tools"] = new JsonArray("*")
        };
    }

    private async Task LoadBuiltinMcpServersAsync(CopilotClient client, CancellationToken cancellationToken)
    {
        const string resourceName = "CopilotChatbot.Assets.builtin-mcp-servers.json";
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        await using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.Log("MCP-CONFIG-ERROR", $"Embedded resource '{resourceName}' not found.");
            return;
        }
        using var reader = new System.IO.StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken);
        await LoadMcpConfigJsonAsync(client, json, "(built-in)", cancellationToken);
    }

    private async Task LoadMcpConfigFileAsync(
        CopilotClient client,
        string configPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            await LoadMcpConfigJsonAsync(client, json, configPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Log("MCP-CONFIG-ERROR", $"Failed to load {configPath}: {ex.Message}");
        }
    }

    private async Task LoadMcpConfigJsonAsync(
        CopilotClient client,
        string json,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            // Support both "mcpServers" (GitHub Copilot CLI) and "servers" (VS Code .vscode/mcp.json)
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) &&
                !doc.RootElement.TryGetProperty("servers", out servers))
                return;

            foreach (var server in servers.EnumerateObject())
            {
                var name = server.Name;
                var cfg = server.Value;
                try
                {
                    var serverConfig = CreateMcpServerConfig(cfg);

                    if (_mcpServerNames.Contains(name))
                    {
                        _logger.Log("MCP-CONFIG", $"Skipped MCP server '{name}' from {source} because it was already loaded.");
                        continue;
                    }

                    if (serverConfig is McpServerConfig typedServerConfig)
                        _mcpServerConfigs[name] = typedServerConfig;
                    _mcpServerNames.Add(name);
                    await TryAddMcpServerAsync(client, name, serverConfig, source, cancellationToken);
                    _logger.Log("MCP-CONFIG", $"Loaded MCP server '{name}' from {source}");
                    UpsertMcpServerSnapshot(name, "registered");
                }
                catch (Exception ex)
                {
                    _logger.Log("MCP-CONFIG-ERROR", $"Failed to register MCP server '{name}': {ex.Message}");
                    UpsertMcpServerSnapshot(name, "error");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("MCP-CONFIG-ERROR", $"Failed to parse MCP config from {source}: {ex.Message}");
        }
    }

    private static object CreateMcpServerConfig(JsonElement cfg)
    {
        var type = cfg.TryGetProperty("type", out var typeProp)
            ? typeProp.GetString()
            : null;

        return type?.ToLowerInvariant() switch
        {
            "http" => CreateMcpHttpServerConfig(cfg),
            //"sse" => CreateMcpSseServerConfig(cfg),
            "sse" => CreateMcpHttpServerConfig(cfg),
            "stdio" => CreateMcpStdioServerConfig(cfg),
            _ when cfg.TryGetProperty("url", out _) => CreateMcpHttpServerConfig(cfg),
            _ => CreateMcpStdioServerConfig(cfg),
        };
    }

    private static McpHttpServerConfig CreateMcpHttpServerConfig(JsonElement cfg)
    {
        var httpCfg = new McpHttpServerConfig
        {
            Url = cfg.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString() ?? ""
                : ""
        };
        if (cfg.TryGetProperty("headers", out var headersProp))
            httpCfg.Headers = CreateStringDictionary(headersProp);
        if (cfg.TryGetProperty("timeout", out var timeoutProp) && timeoutProp.TryGetInt32(out var timeout))
            httpCfg.Timeout = timeout;
        ApplyMcpTools(cfg, httpCfg.Tools);
        return httpCfg;
    }

    private static McpSseServerConfig CreateMcpSseServerConfig(JsonElement cfg)
    {
        var sseCfg = new McpSseServerConfig
        {
            Url = cfg.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString() ?? ""
                : ""
        };
        if (cfg.TryGetProperty("headers", out var headersProp))
            sseCfg.Headers = CreateStringDictionary(headersProp);
        if (cfg.TryGetProperty("timeout", out var timeoutProp) && timeoutProp.TryGetInt32(out var timeout))
            sseCfg.Timeout = timeout;
        ApplyMcpTools(cfg, sseCfg.Tools);
        return sseCfg;
    }

    private static McpStdioServerConfig CreateMcpStdioServerConfig(JsonElement cfg)
    {
        var stdio = new McpStdioServerConfig
        {
            Command = cfg.TryGetProperty("command", out var cmd)
                ? cmd.GetString() ?? ""
                : "",
        };
        if (cfg.TryGetProperty("args", out var argsProp))
            stdio.Args = argsProp.EnumerateArray()
                .Select(a => a.GetString() ?? "")
                .ToList();
        if (cfg.TryGetProperty("env", out var envProp))
            stdio.Env = envProp.EnumerateObject()
                .ToDictionary(e => e.Name, e => e.Value.GetString() ?? "");
        if (cfg.TryGetProperty("cwd", out var cwdProp))
            stdio.Cwd = cwdProp.GetString();
        ApplyMcpTools(cfg, stdio.Tools);
        return stdio;
    }

    private static void ApplyMcpTools(JsonElement cfg, IList<string> tools)
    {
        if (cfg.TryGetProperty("tools", out var toolsProp))
        {
            foreach (var t in toolsProp.EnumerateArray())
            {
                var toolName = t.GetString();
                if (!string.IsNullOrWhiteSpace(toolName))
                    tools.Add(toolName);
            }
        }
        if (!tools.Any())
            tools.Add("*");
    }

    private static Dictionary<string, string> CreateStringDictionary(JsonElement obj)
        => obj.EnumerateObject()
            .ToDictionary(e => e.Name, e => e.Value.GetString() ?? "");

    private async Task TryAddMcpServerAsync(
        CopilotClient client,
        string name,
        object config,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.Rpc.Mcp.Config.AddAsync(name, config, cancellationToken);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log("MCP-CONFIG", $"Did not replace MCP server '{name}' from {source} because it is already registered in the SDK.");
        }
    }

    private async Task<CopilotSession> EnsureSessionAsync(ChatSessionView chat, AppSettings settings, ModelChoice? model, string? reasoningEffort)
    {
        if (_sessions.TryGetValue(chat, out var existing))
        {
            return existing;
        }

        var client = await EnsureClientAsync(settings);
        var token = await ResolveGitHubTokenAsync(settings.EffectiveGitHubToken);
        var customAgents = LoadAgentsFromDirectories(settings);
        if (customAgents?.Count > 0)
        {
            var agentInfos = customAgents
                .Select(a => new AgentInfo(a.DisplayName ?? a.Name, "loaded", "file"))
                .ToList();
            _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(_capabilitiesSnapshot.McpServers, agentInfos, _capabilitiesSnapshot.Skills);
        }
        var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model?.Id ?? settings.SelectedModelId,
            ReasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
            Streaming = true,
            GitHubToken = token,
            SystemMessage = string.IsNullOrWhiteSpace(chat.SystemPrompt) ? null : new SystemMessageConfig { Content = chat.SystemPrompt },
            McpServers = _mcpServerConfigs.Count > 0 ? _mcpServerConfigs : null,
            SkillDirectories = GetEffectiveSkillDirectories(settings),
            CustomAgents = customAgents,
            OnPermissionRequest = async (request, _) => await EvaluatePermissionAsync(chat, request, _settingsStore.Load()),
            OnUserInputRequest = async (request, _) =>
            {
                try { return await EvaluateUserInputAsync(chat, request); }
                catch (Exception ex)
                {
                    _logger.Log("ASK-USER-FATAL", ex.ToString());
                    return new UserInputResponse { Answer = "", WasFreeform = true };
                }
            }
        });

        chat.CopilotSessionId = session.SessionId;
        _logger.Log("SESSION", $"Created session {session.SessionId} | model={model?.Id ?? settings.SelectedModelId} | reasoning={reasoningEffort ?? "default"}");
        session.On(evt => HandleEvent(chat, evt));
        _sessions[chat] = session;
        return session;
    }

    private async Task<UserInputResponse> EvaluateUserInputAsync(ChatSessionView chat, UserInputRequest request)
    {
        var prompt = new UserInputPrompt(
            request.Question ?? "",
            request.Choices?.ToArray() ?? [],
            request.AllowFreeform != false,
            chat.Title);

        _logger.Log("ASK-USER-REQUEST", $"Thread={System.Threading.Thread.CurrentThread.IsThreadPoolThread} IsBackground={System.Threading.Thread.CurrentThread.IsBackground} ManagedId={System.Threading.Thread.CurrentThread.ManagedThreadId} | Question: {prompt.Question} | Choices: [{string.Join(", ", prompt.Choices)}] | AllowFreeform: {prompt.AllowFreeform}");

        try
        {
            var response = await _userInputPrompt(chat, prompt);
            _logger.Log("ASK-USER-RESPONSE", $"Answer: {(string.IsNullOrWhiteSpace(response.Answer) ? "(empty/cancelled)" : response.Answer)} | WasFreeform: {response.WasFreeform}");

            if (!string.IsNullOrWhiteSpace(response.Answer))
            {
                AddOrUpdate(
                    chat,
                    ChatMessageKind.User,
                    response.Answer,
                    $"ask-user-answer-{Guid.NewGuid():N}");
            }

            return new UserInputResponse
            {
                Answer = response.Answer ?? "",
                WasFreeform = response.WasFreeform
            };
        }
        catch (Exception ex)
        {
            _logger.Log("ASK-USER-ERROR", ex.ToString());
            return new UserInputResponse { Answer = "", WasFreeform = true };
        }
    }

    private async Task<PermissionRequestResult> EvaluatePermissionAsync(ChatSessionView chat, PermissionRequest request, AppSettings settings)
    {
        var prompt = ToPermissionPrompt(request) with { SessionTitle = chat.Title };

        if (prompt.Kind.Equals("memory", StringComparison.OrdinalIgnoreCase) &&
            !settings.Permissions.AllowMemoryByDefault)
        {
            _logger.Log("PERMISSION-AUTO", "Kind=memory | rejected because memory is disabled");
            return new PermissionRequestResult { Kind = PermissionRequestResultKind.Rejected };
        }

        if (TryCreatePromptForMissingCommands(prompt, settings, out var missingCommandPrompt))
        {
            if (missingCommandPrompt.Commands.Count == 0)
            {
                _logger.Log("PERMISSION-AUTO", $"Kind={prompt.Kind} Tool={prompt.ToolName} File={prompt.FileName} Host={prompt.Host} Commands={FormatCommandIdentifiers(prompt.Commands)} | auto-approved by command whitelist");
                return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };
            }

            prompt = missingCommandPrompt;
        }

        var key = BuildPermissionKey(prompt);

        if (IsAllowedBySettings(prompt, settings) || IsAllowedForSession(prompt) || _sessionPermissionApprovals.ContainsKey(key))
        {
            _logger.Log("PERMISSION-AUTO", $"Kind={prompt.Kind} Tool={prompt.ToolName} File={prompt.FileName} Host={prompt.Host} Commands={FormatCommandIdentifiers(prompt.Commands)} | auto-approved");
            return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };
        }

        _logger.Log("PERMISSION-REQUEST", $"Kind={prompt.Kind} Tool={prompt.ToolName} File={prompt.FileName} Host={prompt.Host} Commands={FormatCommandIdentifiers(prompt.Commands)} Command={prompt.Command}");
        var decision = await _permissionPrompt(chat, prompt);
        _logger.Log("PERMISSION-DECISION", $"Kind={prompt.Kind} Tool={prompt.ToolName} | Decision={decision}");

        if (decision == PermissionPromptDecision.AllowForSession)
        {
            SaveSessionPermissionApproval(prompt);
        }
        else if (decision == PermissionPromptDecision.SaveToSettings)
        {
            SavePermissionApproval(prompt, settings);
            _settingsStore.Save(settings);
        }

        return new PermissionRequestResult
        {
            Kind = decision != PermissionPromptDecision.Deny
                ? PermissionRequestResultKind.Approved
                : PermissionRequestResultKind.Rejected
        };
    }

    private static readonly HashSet<string> _implicitlyApprovedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // GitHub API hosts used by the builtin github-mcp-server.
        // These are implicitly trusted when the user is already authenticated via GitHub token.
        "api.githubcopilot.com",
        "api.github.com",
        "github.com",
        "uploads.github.com",
        "objects.githubusercontent.com",
    };

    private static bool IsAllowedBySettings(PermissionPrompt prompt, AppSettings settings)
    {
        var kind = prompt.Kind;
        var tool = prompt.ToolName ?? "";
        var file = prompt.FileName ?? "";
        var host = prompt.Host ?? "";
        var command = prompt.Command ?? "";
        var commandIdentifiers = prompt.Commands.Select(command => command.Identifier).ToArray();

        if (settings.Permissions.SavedRules.Any(rule => RuleMatches(rule, kind, tool, file, command, host, commandIdentifiers)))
            return true;

        if (commandIdentifiers.Length > 0)
        {
            if (AllCommandIdentifiersAllowed(commandIdentifiers, settings.Permissions.SavedRules, kind, tool, file, host))
                return true;
        }

        // Read-only file access is allowed by default
        if (kind.Equals("read", StringComparison.OrdinalIgnoreCase))
            return true;

        // MCP and custom tools may be allowed globally via settings
        if (IsMcpPermissionKind(kind) && settings.Permissions.AllowMcpByDefault)
            return true;
        if (IsCustomToolPermissionKind(kind) && settings.Permissions.AllowCustomToolsByDefault)
            return true;
        if (kind.Equals("memory", StringComparison.OrdinalIgnoreCase) && settings.Permissions.AllowMemoryByDefault)
            return true;

        if ((IsCustomToolPermissionKind(kind) || IsMcpPermissionKind(kind)) &&
            settings.Permissions.AllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (kind.Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            // Well-known GitHub API hosts are implicitly approved — the builtin github-mcp-server
            // always calls these, and the user is already authenticated via GitHub token.
            if (_implicitlyApprovedHosts.Contains(host))
                return true;

            if (settings.Permissions.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        if (kind.Equals("write", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file))
        {
            var full = Path.GetFullPath(file);
            return settings.Permissions.Folders.Any(rule =>
            {
                if (string.IsNullOrWhiteSpace(rule.Path))
                    return false;
                var root = Path.GetFullPath(rule.Path);
                var underRoot = full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                                full.Equals(root, StringComparison.OrdinalIgnoreCase);
                return underRoot && rule.CanWrite;
            });
        }

        return false;
    }

    private bool TryCreatePromptForMissingCommands(PermissionPrompt prompt, AppSettings settings, out PermissionPrompt missingPrompt)
    {
        missingPrompt = prompt;
        if (prompt.Commands.Count == 0)
            return false;

        var missingCommands = prompt.Commands
            .Where(command => !IsCommandAllowed(command, prompt, settings))
            .ToArray();

        missingPrompt = prompt with { Commands = missingCommands };
        return true;
    }

    private bool IsCommandAllowed(ShellCommandPermission command, PermissionPrompt prompt, AppSettings settings)
    {
        var singleCommandPrompt = prompt with
        {
            Command = null,
            Commands = [command]
        };

        return IsAllowedBySettings(singleCommandPrompt, settings) ||
               IsAllowedForSession(singleCommandPrompt) ||
               _sessionPermissionApprovals.ContainsKey(BuildPermissionKey(singleCommandPrompt));
    }

    private static PermissionPrompt ToPermissionPrompt(PermissionRequest request)
    {
        var url = GetString(request, "Url");
        var commands = GetShellCommands(request);
        return new PermissionPrompt(
            GetString(request, "Kind") ?? "unknown",
            GetString(request, "ToolName"),
            GetString(request, "FileName") ?? GetString(request, "Path"),
            GetString(request, "FullCommandText"),
            TryGetHost(GetString(request, "Host")) ?? TryGetHost(url),
            commands);
    }

    private static string BuildUserInputPromptMessage(UserInputPrompt prompt)
    {
        var choices = prompt.Choices.Count == 0
            ? ""
            : "\n\nSuggested choices:\n" + string.Join("\n", prompt.Choices.Select(choice => "- " + choice));
        var freeform = prompt.AllowFreeform ? "\n\nFreeform answer is allowed." : "";
        return "Copilot asked for input:\n\n" + prompt.Question + choices + freeform;
    }

    private static void SavePermissionApproval(PermissionPrompt prompt, AppSettings settings)
    {
        if (prompt.Kind.Equals("url", StringComparison.OrdinalIgnoreCase) &&
            AddUnique(settings.Permissions.AllowedHosts, prompt.Host))
        {
            return;
        }

        if ((IsCustomToolPermissionKind(prompt.Kind) ||
             IsMcpPermissionKind(prompt.Kind)) &&
            AddUnique(settings.Permissions.AllowedTools, prompt.ToolName))
        {
            return;
        }

        if (prompt.Kind.Equals("write", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(prompt.FileName))
        {
            var folder = GetPermissionFolder(prompt.FileName);
            if (!settings.Permissions.Folders.Any(rule =>
                    rule.CanWrite &&
                    NormalizePath(rule.Path).Equals(NormalizePath(folder), StringComparison.OrdinalIgnoreCase)))
            {
                settings.Permissions.Folders.Add(new FolderPermission { Path = folder, CanWrite = true });
            }
            return;
        }

        if (prompt.Commands.Count > 0)
        {
            foreach (var command in prompt.Commands)
            {
                var commandRule = new PermissionRule
                {
                    Kind = prompt.Kind,
                    ToolName = prompt.ToolName ?? "",
                    FileName = prompt.FileName ?? "",
                    CommandIdentifiers = command.Identifier,
                    Host = prompt.Host ?? ""
                };

                if (!settings.Permissions.SavedRules.Any(existing =>
                        RuleMatches(existing, commandRule.Kind, commandRule.ToolName, commandRule.FileName, "", commandRule.Host, [command.Identifier])))
                {
                    settings.Permissions.SavedRules.Add(commandRule);
                }
            }
            return;
        }

        var exactRule = new PermissionRule
        {
            Kind = prompt.Kind,
            ToolName = prompt.ToolName ?? "",
            FileName = prompt.FileName ?? "",
            Command = prompt.Command ?? "",
            CommandIdentifiers = FormatCommandIdentifiers(prompt.Commands),
            Host = prompt.Host ?? ""
        };

        if (!settings.Permissions.SavedRules.Any(existing =>
                RuleMatches(existing, exactRule.Kind, exactRule.ToolName, exactRule.FileName, exactRule.Command, exactRule.Host, prompt.Commands.Select(command => command.Identifier))))
        {
            settings.Permissions.SavedRules.Add(exactRule);
        }
    }

    private static bool RuleMatches(PermissionRule rule, string kind, string tool, string file, string command, string host, IEnumerable<string> commandIdentifiers)
    {
        if (!rule.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            return false;

        return ValueMatches(rule.ToolName, tool) &&
               PathMatches(rule.FileName, file) &&
               ValueMatches(rule.Command, command) &&
               CommandIdentifiersMatch(rule.CommandIdentifiers, commandIdentifiers) &&
               ValueMatches(rule.Host, host);
    }

    private static bool AllCommandIdentifiersAllowed(
        IReadOnlyCollection<string> commandIdentifiers,
        IEnumerable<PermissionRule> rules,
        string kind,
        string tool,
        string file,
        string host)
    {
        return commandIdentifiers.All(commandIdentifier =>
            rules.Any(rule => RuleMatches(rule, kind, tool, file, "", host, [commandIdentifier])));
    }

    private bool IsAllowedForSession(PermissionPrompt prompt)
    {
        if (prompt.Commands.Count == 0)
            return false;

        return prompt.Commands.All(command =>
            _sessionPermissionApprovals.ContainsKey(BuildPermissionKey(prompt with
            {
                Command = null,
                Commands = [command]
            })));
    }

    private void SaveSessionPermissionApproval(PermissionPrompt prompt)
    {
        if (prompt.Commands.Count == 0)
        {
            _sessionPermissionApprovals.TryAdd(BuildPermissionKey(prompt), 0);
            return;
        }

        foreach (var command in prompt.Commands)
        {
            _sessionPermissionApprovals.TryAdd(BuildPermissionKey(prompt with
            {
                Command = null,
                Commands = [command]
            }), 0);
        }
    }

    private static bool ValueMatches(string ruleValue, string requestedValue)
        => string.IsNullOrWhiteSpace(ruleValue) ||
           ruleValue.Equals(requestedValue, StringComparison.OrdinalIgnoreCase);

    private static bool IsMcpPermissionKind(string kind)
        => kind.Equals("mcp", StringComparison.OrdinalIgnoreCase);

    private static bool IsCustomToolPermissionKind(string kind)
        => kind.Equals("custom-tool", StringComparison.OrdinalIgnoreCase) ||
           kind.Equals("custom_tool", StringComparison.OrdinalIgnoreCase) ||
           kind.Equals("customtool", StringComparison.OrdinalIgnoreCase);

    private static bool CommandIdentifiersMatch(string ruleValue, IEnumerable<string> requestedValues)
    {
        var requested = requestedValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeCommandIdentifier)
            .ToArray();
        if (requested.Length == 0)
            return string.IsNullOrWhiteSpace(ruleValue);
        if (string.IsNullOrWhiteSpace(ruleValue))
            return true;

        var allowed = ruleValue
            .Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeCommandIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requested.All(allowed.Contains);
    }

    private static bool PathMatches(string rulePath, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(rulePath))
            return true;
        if (string.IsNullOrWhiteSpace(requestedPath))
            return false;

        return NormalizePath(rulePath).Equals(NormalizePath(requestedPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AddUnique(ICollection<string> collection, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || collection.Contains(value, StringComparer.OrdinalIgnoreCase))
            return false;

        collection.Add(value.Trim());
        return true;
    }

    private static string GetPermissionFolder(string fileName)
    {
        var full = NormalizePath(fileName);
        if (Directory.Exists(full))
            return full;

        return Path.GetDirectoryName(full) ?? full;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string BuildPermissionKey(PermissionPrompt prompt)
        => string.Join("|", new[]
        {
            NormalizeKey(prompt.Kind),
            NormalizeKey(prompt.ToolName),
            NormalizeKey(prompt.FileName),
            NormalizeKey(prompt.Commands.Count > 0 ? FormatCommandIdentifiers(prompt.Commands) : prompt.Command),
            NormalizeKey(prompt.Host)
        });

    private static IReadOnlyList<ShellCommandPermission> GetShellCommands(PermissionRequest request)
    {
        if (request is not PermissionRequestShell shell)
            return [];

        var commands = shell.Commands
            .Where(command => !string.IsNullOrWhiteSpace(command.Identifier))
            .Select(command => new ShellCommandPermission(command.Identifier, command.ReadOnly))
            .DistinctBy(command => NormalizeCommandIdentifier(command.Identifier))
            .ToArray();

        var powerShellCommands = GetPowerShellCommandIdentifiers(shell, commands);
        if (powerShellCommands.Count > 0)
            return powerShellCommands;

        return commands;
    }

    private static IReadOnlyList<ShellCommandPermission> GetPowerShellCommandIdentifiers(
        PermissionRequestShell shell,
        IReadOnlyList<ShellCommandPermission> sdkCommands)
    {
        var fullCommandText = shell.FullCommandText ?? "";
        var sdkHasPowerShellHost = sdkCommands.Any(command => IsPowerShellHostIdentifier(command.Identifier));
        var script = TryExtractPowerShellScript(fullCommandText, sdkHasPowerShellHost);
        if (string.IsNullOrWhiteSpace(script))
            return [];

        var defaultReadOnly = sdkCommands.Count > 0 &&
                              sdkCommands.All(command => command.ReadOnly) &&
                              !shell.HasWriteFileRedirection;

        return ExtractPowerShellCommandIdentifiers(script)
            .Where(identifier => !IsPowerShellHostIdentifier(identifier))
            .Select(identifier => new ShellCommandPermission(
                identifier,
                !shell.HasWriteFileRedirection && (defaultReadOnly || IsKnownReadOnlyPowerShellCommand(identifier))))
            .DistinctBy(command => NormalizeCommandIdentifier(command.Identifier))
            .ToArray();
    }

    private static string? TryExtractPowerShellScript(string fullCommandText, bool sdkHasPowerShellHost)
    {
        if (string.IsNullOrWhiteSpace(fullCommandText))
            return null;

        var tokens = TokenizeCommandLine(fullCommandText);
        if (tokens.Count == 0)
            return null;

        if (IsPowerShellHostIdentifier(tokens[0]))
        {
            for (var i = 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Equals("-Command", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("-CommandWithArgs", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("/c", StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1 < tokens.Count
                        ? string.Join(" ", tokens.Skip(i + 1))
                        : null;
                }
            }

            return null;
        }

        if (sdkHasPowerShellHost || LooksLikePowerShellCommand(fullCommandText))
            return fullCommandText;

        return null;
    }

    private static IReadOnlyList<string> ExtractPowerShellCommandIdentifiers(string script)
    {
        var ast = Parser.ParseInput(script, out _, out _);
        return ast
            .FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
            .OfType<CommandAst>()
            .Select(command => command.GetCommandName())
            .Where(commandName => !string.IsNullOrWhiteSpace(commandName))
            .Select(commandName => commandName!)
            .ToArray();
    }

    private static IReadOnlyList<string> TokenizeCommandLine(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var ch in commandLine)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static bool LooksLikePowerShellCommand(string value)
        => ExtractPowerShellCommandIdentifiers(value)
            .Any(identifier => identifier.Contains('-', StringComparison.Ordinal) &&
                               IsKnownPowerShellVerb(identifier.Split('-', 2)[0]));

    private static bool IsPowerShellHostIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var fileName = identifier.Trim().Trim('"', '\'');
        try
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
        }
        catch
        {
            fileName = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;
        }

        return fileName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownReadOnlyPowerShellCommand(string identifier)
    {
        var verb = identifier.Split('-', 2)[0];
        return verb.Equals("Get", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("ConvertFrom", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("ConvertTo", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Select", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Where", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Sort", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Measure", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Format", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Compare", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Resolve", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Join", StringComparison.OrdinalIgnoreCase) ||
               verb.Equals("Split", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownPowerShellVerb(string verb)
        => verb.Equals("Get", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Set", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("New", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Remove", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Copy", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Move", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Rename", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Invoke", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Start", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Stop", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Restart", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Read", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Out", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Export", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Import", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("ConvertFrom", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("ConvertTo", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Select", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Where", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Sort", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Measure", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Format", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Compare", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Group", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Resolve", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Join", StringComparison.OrdinalIgnoreCase) ||
           verb.Equals("Split", StringComparison.OrdinalIgnoreCase);

    private static string FormatCommandIdentifiers(IEnumerable<ShellCommandPermission> commands)
        => string.Join("; ", commands
            .Select(command => command.Identifier)
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string NormalizeCommandIdentifier(string value)
        => value.Trim().ToUpperInvariant();

    private static string NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private void HandleEvent(ChatSessionView chat, SessionEvent evt)
    {
        switch (evt)
        {
            case AssistantIntentEvent intent:
                _logger.Log("INTENT", intent.Data.Intent);
                AddOrUpdate(chat, ChatMessageKind.Intent, intent.Data.Intent, "intent");
                break;
            case AssistantReasoningDeltaEvent delta:
                // Deltas are too noisy to log — only log the complete reasoning block below.
                StatusChanged?.Invoke(chat, "Reasoning\u2026");
                AddOrUpdate(chat, ChatMessageKind.Reasoning, delta.Data.DeltaContent, $"reason-{delta.Data.ReasoningId}", append: true);
                break;
            case AssistantReasoningEvent reasoning:
                _logger.LogBlock("REASONING", reasoning.Data.Content);
                AddOrUpdate(chat, ChatMessageKind.Reasoning, reasoning.Data.Content, $"reason-{reasoning.Data.ReasoningId}");
                break;
            case AssistantMessageDeltaEvent delta:
                StatusChanged?.Invoke(chat, "Writing response\u2026");
                AddOrUpdate(chat, ChatMessageKind.Assistant, delta.Data.DeltaContent, $"msg-{delta.Data.MessageId}", append: true);
                break;
            case AssistantMessageEvent message:
                _logger.LogBlock("ASSISTANT", message.Data.Content);
                if (IsBackgroundAgentStillProcessingMessage(message.Data.Content))
                {
                    RemoveMessage(chat, $"msg-{message.Data.MessageId}");
                    StatusChanged?.Invoke(chat, NormalizeActivityStatus(message.Data.Content));
                    break;
                }

                if (!string.IsNullOrWhiteSpace(message.Data.ReasoningText))
                    _logger.LogBlock("REASONING-INLINE", message.Data.ReasoningText);
                AddOrUpdate(chat, ChatMessageKind.Assistant, message.Data.Content, $"msg-{message.Data.MessageId}");
                if (!string.IsNullOrWhiteSpace(message.Data.ReasoningText))
                {
                    AddOrUpdate(chat, ChatMessageKind.Reasoning, message.Data.ReasoningText, $"reason-msg-{message.Data.MessageId}");
                }
                break;
            case ToolExecutionStartEvent tool:
            {
                var extra = string.Join("\n", GetAllStringProperties(tool.Data)
                    .Where(kv => !kv.Key.Equals("ToolCallId", StringComparison.OrdinalIgnoreCase)
                              && !kv.Key.Equals("ToolName",   StringComparison.OrdinalIgnoreCase))
                    .Select(kv => $"{kv.Key}: {kv.Value}"));
                var description = string.IsNullOrEmpty(extra)
                    ? $"Running: {tool.Data.ToolName}"
                    : $"Running: {tool.Data.ToolName}\n\n{extra}";
                _logger.LogBlock("TOOL-START", description);
                StatusChanged?.Invoke(chat, $"Running tool: {tool.Data.ToolName}");
                AddOrUpdate(chat, ChatMessageKind.Tool, description, $"tool-{tool.Data.ToolCallId}");
                break;
            }
            case ToolExecutionCompleteEvent tool:
            {
                if (tool.Data.Success)
                {
                    _logger.Log("TOOL-DONE", $"✓ {tool.Data.ToolCallId}: completed");
                    StatusChanged?.Invoke(chat, "Thinking\u2026");
                    AddOrUpdate(chat, ChatMessageKind.Tool,
                        $"\u2713 {tool.Data.ToolCallId}: completed",
                        $"tool-{tool.Data.ToolCallId}");
                }
                else
                {
                    var details = string.Join("\n", GetAllStringProperties(tool.Data)
                        .Where(kv => !kv.Key.Equals("Success",    StringComparison.OrdinalIgnoreCase)
                                  && !kv.Key.Equals("ToolCallId", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => $"{kv.Key}: {kv.Value}"));
                    var message = string.IsNullOrEmpty(details)
                        ? $"\u2717 {tool.Data.ToolCallId}: failed"
                        : $"\u2717 {tool.Data.ToolCallId}: failed\n\n{details}";
                    _logger.LogBlock("TOOL-FAILED", message);
                    AddOrUpdate(chat, ChatMessageKind.Tool, message, $"tool-{tool.Data.ToolCallId}");
                }
                break;
            }
            case SessionErrorEvent error:
            {
                var errExtra = string.Join("\n", GetAllStringProperties(error.Data)
                    .Where(kv => !kv.Key.Equals("Message", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => $"{kv.Key}: {kv.Value}"));
                var errMessage = string.IsNullOrEmpty(errExtra)
                    ? error.Data.Message
                    : $"{error.Data.Message}\n\n{errExtra}";
                _logger.LogBlock("ERROR", errMessage);
                AddOrUpdate(chat, ChatMessageKind.Error, errMessage, $"err-{evt.Id}");
                SetPending(chat, false);
                break;
            }
            case SessionMcpServersLoadedEvent mcpLoaded:
            {
                // Use reflection since the exact collection property name may differ by SDK version
                var dataProp = mcpLoaded.Data.GetType().GetProperty("Servers", BindingFlags.Instance | BindingFlags.Public)
                    ?? mcpLoaded.Data.GetType().GetProperty("McpServers", BindingFlags.Instance | BindingFlags.Public);
                var serverList = dataProp?.GetValue(mcpLoaded.Data) as IEnumerable;
                var mcpList = serverList?.Cast<object>().Select(s =>
                {
                    var name = GetString(s, "Name") ?? "?";
                    var status = GetString(s, "Status") ?? "?";
                    var toolsProp = s.GetType().GetProperty("Tools", BindingFlags.Instance | BindingFlags.Public);
                    var toolNames = new List<string>();
                    if (toolsProp?.GetValue(s) is IEnumerable toolList)
                        toolNames.AddRange(toolList.Cast<object>()
                            .Select(t => GetString(t, "Name") ?? GetString(t, "ToolName") ?? "?"));
                    return new McpServerInfo(name, status, toolNames);
                }).ToList() ?? [];
                _logger.LogBlock("MCP-LOADED", string.Join("\n", mcpList.Select(s =>
                    s.Tools.Count == 0 ? $"  {s.Name} [{s.Status}]" : $"  {s.Name} [{s.Status}] tools: {string.Join(", ", s.Tools)}")));
                if (mcpList.Count > 0)
                {
                    _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(mcpList, _capabilitiesSnapshot.Agents, _capabilitiesSnapshot.Skills);
                }
                break;
            }
            case SessionCustomAgentsUpdatedEvent customAgentsUpdated:
            {
                var customAgents = customAgentsUpdated.Data.Agents.Select(a =>
                {
                    var name = a.DisplayName ?? a.Name ?? "?";
                    var status = "loaded";
                    var source = a.Source ?? "file";
                    return new AgentInfo(name, status, source);
                }).ToList();
                _logger.Log("CUSTOM-AGENTS-UPDATED", string.Join(", ", customAgents.Select(a => $"{a.Name} [{a.Status}]")));
                if (customAgents.Count > 0)
                {
                    _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(
                        _capabilitiesSnapshot.McpServers,
                        MergeAgents(_capabilitiesSnapshot.Agents, customAgents),
                        _capabilitiesSnapshot.Skills);
                }
                break;
            }
            case SessionExtensionsLoadedEvent extLoaded:
            {
                var dataProp = extLoaded.Data.GetType().GetProperty("Extensions", BindingFlags.Instance | BindingFlags.Public)
                    ?? extLoaded.Data.GetType().GetProperty("Agents", BindingFlags.Instance | BindingFlags.Public);
                var extList2 = dataProp?.GetValue(extLoaded.Data) as IEnumerable;
                var agents = extList2?.Cast<object>().Select(e =>
                {
                    var name = GetString(e, "DisplayName") ?? GetString(e, "Name") ?? "?";
                    var status = GetString(e, "Status") ?? "?";
                    var source = GetString(e, "Source") ?? "";
                    return new AgentInfo(name, status, source);
                }).ToList() ?? [];
                _logger.Log("AGENTS-LOADED", string.Join(", ", agents.Select(a => $"{a.Name} [{a.Status}]")));
                _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(
                    _capabilitiesSnapshot.McpServers,
                    MergeAgents(_capabilitiesSnapshot.Agents, agents),
                    _capabilitiesSnapshot.Skills);
                break;
            }
            case SessionSkillsLoadedEvent skillsLoaded:
            {
                var dataProp = skillsLoaded.Data.GetType().GetProperty("Skills", BindingFlags.Instance | BindingFlags.Public);
                var skillList2 = dataProp?.GetValue(skillsLoaded.Data) as IEnumerable;
                var skills = skillList2?.Cast<object>().Select(s =>
                {
                    var name = GetString(s, "Name") ?? GetString(s, "Id") ?? "?";
                    var description = GetString(s, "Description") ?? GetString(s, "DisplayName");
                    return new SkillInfo(name, description);
                }).ToList() ?? [];
                _logger.Log("SKILLS-LOADED", string.Join(", ", skills.Select(s => s.Name)));
                _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(_capabilitiesSnapshot.McpServers, _capabilitiesSnapshot.Agents, skills);
                break;
            }
            case SessionMcpServerStatusChangedEvent mcpStatus:
            {
                var name = GetString(mcpStatus.Data, "ServerName") ?? GetString(mcpStatus.Data, "Name") ?? "?";
                var status = GetString(mcpStatus.Data, "Status") ?? "?";
                var msg = GetString(mcpStatus.Data, "StatusMessage") ?? GetString(mcpStatus.Data, "Error") ?? "";
                _logger.Log("MCP-STATUS", string.IsNullOrEmpty(msg) ? $"{name}: {status}" : $"{name}: {status} — {msg}");
                UpsertMcpServerSnapshot(name, status);
                break;
            }
            case SessionIdleEvent:
                _logger.Log("IDLE", "Session became idle");
                SetPending(chat, false);
                break;
            case AssistantUsageEvent usage:
                var usageStatus = ToUsageStatus(usage.Data);
                _lastUsage = usageStatus;
                _logger.Log("USAGE", usageStatus.ToStatusText());
                UsageUpdated?.Invoke(usageStatus);
                break;
        }
    }

    private void SetPending(ChatSessionView chat, bool isPending)
    {
        if (!isPending)
        {
            FlushChatUpdatesOnDispatcher();
        }

        App.Current.Dispatcher.BeginInvoke(() =>
        {
            chat.IsPending = isPending;
            if (!isPending)
            {
                // Stamp completion time on all messages in the current response turn
                // (everything after the last user message that was not yet stamped).
                var now = DateTimeOffset.Now;
                var lastUserIdx = -1;
                for (var i = chat.Messages.Count - 1; i >= 0; i--)
                {
                    if (chat.Messages[i].Kind == ChatMessageKind.User) { lastUserIdx = i; break; }
                }
                foreach (var msg in chat.Messages.Skip(lastUserIdx + 1))
                {
                    if (msg.CompletedAt is null)
                        msg.CompletedAt = now;
                }
            }
            SessionPendingChanged?.Invoke(chat, isPending);
            if (!isPending)
                StatusChanged?.Invoke(chat, null);
        });
    }

    private static bool IsBackgroundAgentStillProcessingMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("background agent", StringComparison.OrdinalIgnoreCase)
            && content.Contains("still processing", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeActivityStatus(string content)
    {
        var status = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (status.Contains("  ", StringComparison.Ordinal))
        {
            status = status.Replace("  ", " ");
        }

        return status;
    }

    private static CopilotUsageStatus ToUsageStatus(AssistantUsageData data)
    {
        var quota = data.QuotaSnapshots?.Values.FirstOrDefault();
        return new CopilotUsageStatus(
            data.Model ?? "unknown model",
            data.InputTokens,
            data.OutputTokens,
            data.ReasoningTokens,
            data.Cost,
            quota?.UsedRequests,
            quota?.EntitlementRequests,
            quota?.RemainingPercentage,
            quota?.ResetDate);
    }

    private void AddOrUpdate(ChatSessionView chat, ChatMessageKind kind, string? content, string key, bool append = false)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        _pendingChatUpdates.Enqueue(ChatUpdate.Upsert(chat, kind, content, key, append));
        ScheduleChatUpdateFlush(GetResponseBufferDelay(chat));
    }

    private void RemoveMessage(ChatSessionView chat, string key)
    {
        _pendingChatUpdates.Enqueue(ChatUpdate.Delete(chat, key));
        ScheduleChatUpdateFlush(TimeSpan.Zero);
    }

    private void ScheduleChatUpdateFlush(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            FlushChatUpdatesOnDispatcher();
            return;
        }

        if (Interlocked.Exchange(ref _chatUpdateFlushScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            FlushChatUpdatesOnDispatcher();
        });
    }

    private void FlushChatUpdatesOnDispatcher()
    {
        App.Current.Dispatcher.BeginInvoke(FlushPendingChatUpdatesOnUi);
    }

    private void FlushPendingChatUpdatesOnUi()
    {
        var changedChats = new HashSet<ChatSessionView>();
        while (_pendingChatUpdates.TryDequeue(out var update))
        {
            update.Chat.IsApplyingBufferedUpdates = true;
            changedChats.Add(update.Chat);
            if (update.IsRemove)
            {
                var existing = update.Chat.Messages.FirstOrDefault(m => m.Id == update.Key);
                if (existing is not null)
                {
                    update.Chat.Messages.Remove(existing);
                }

                continue;
            }

            var existingMessage = update.Chat.Messages.FirstOrDefault(m => m.Id == update.Key);
            if (existingMessage is null)
            {
                update.Chat.Messages.Add(new ChatMessage
                {
                    Id = update.Key,
                    Kind = update.Kind,
                    Content = update.Content
                });
            }
            else
            {
                existingMessage.Content = update.Append
                    ? existingMessage.Content + update.Content
                    : update.Content;
            }
        }

        foreach (var chat in changedChats)
        {
            chat.IsApplyingBufferedUpdates = false;
            ChatUpdated?.Invoke(chat);
        }

        Interlocked.Exchange(ref _chatUpdateFlushScheduled, 0);
        if (_pendingChatUpdates.TryPeek(out var nextUpdate))
        {
            ScheduleChatUpdateFlush(GetResponseBufferDelay(nextUpdate.Chat));
        }
    }

    private sealed class McpSseServerConfig
    {
        [JsonPropertyName("type")]
        public string Type => "sse";

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("headers")]
        public IDictionary<string, string>? Headers { get; set; }

        [JsonPropertyName("tools")]
        public IList<string> Tools { get; } = [];

        [JsonPropertyName("timeout")]
        public int? Timeout { get; set; }
    }

    private void SetResponseBufferOptions(ChatSessionView chat, AppSettings settings)
    {
        var interval = Math.Clamp(
            settings.ResponseBufferIntervalMs <= 0 ? 1000 : settings.ResponseBufferIntervalMs,
            MinResponseBufferIntervalMs,
            MaxResponseBufferIntervalMs);
        _responseBufferOptions[chat] = new ResponseBufferOptions(
            settings.EnableResponseBuffering,
            TimeSpan.FromMilliseconds(interval));
    }

    private TimeSpan GetResponseBufferDelay(ChatSessionView chat)
    {
        return _responseBufferOptions.TryGetValue(chat, out var options) && options.Enabled
            ? options.Interval
            : TimeSpan.Zero;
    }

    private sealed record ChatUpdate(
        ChatSessionView Chat,
        ChatMessageKind Kind,
        string Content,
        string Key,
        bool Append,
        bool IsRemove)
    {
        public static ChatUpdate Upsert(ChatSessionView chat, ChatMessageKind kind, string content, string key, bool append)
            => new(chat, kind, content, key, append, false);

        public static ChatUpdate Delete(ChatSessionView chat, string key)
            => new(chat, ChatMessageKind.System, "", key, false, true);
    }

    private sealed record ResponseBufferOptions(bool Enabled, TimeSpan Interval);

    private void UpsertMcpServerSnapshot(string name, string status, IReadOnlyList<string>? tools = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "?")
        {
            return;
        }

        var current = _capabilitiesSnapshot;
        var servers = current.McpServers.ToList();
        var index = servers.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var existing = servers[index];
            servers[index] = new McpServerInfo(name, status, tools ?? existing.Tools);
        }
        else
        {
            servers.Add(new McpServerInfo(name, status, tools ?? []));
        }

        _capabilitiesSnapshot = new SessionCapabilitiesSnapshot(servers, current.Agents, current.Skills);
    }

    private static IReadOnlyList<AgentInfo> MergeAgents(IEnumerable<AgentInfo> existing, IEnumerable<AgentInfo> incoming)
    {
        var merged = existing
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Name) && agent.Name != "?")
            .ToDictionary(agent => agent.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var agent in incoming.Where(agent => !string.IsNullOrWhiteSpace(agent.Name) && agent.Name != "?"))
        {
            merged[agent.Name] = agent;
        }

        return merged.Values
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryGetHost(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;
    }

    private static string? GetString(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?.GetValue(source)?.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> GetAllStringProperties(object source)
        => source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p =>
            {
                try { return new KeyValuePair<string, string?>(p.Name, p.GetValue(source)?.ToString()); }
                catch { return new KeyValuePair<string, string?>(p.Name, null); }
            })
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!));

    private static async Task<string?> ResolveGitHubTokenAsync(string? settingsToken)
    {
        if (!string.IsNullOrWhiteSpace(settingsToken))
            return settingsToken;

        // Fall back to the token stored by the GitHub CLI (gh auth login).
        // This is the same credential source that `gh` commands use.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var token = output.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> CreateProcessEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        EnsureEnvironmentValue(env, "SystemRoot", Environment.GetEnvironmentVariable("SystemRoot"));
        EnsureEnvironmentValue(env, "WINDIR", Environment.GetEnvironmentVariable("WINDIR"));
        EnsureEnvironmentValue(env, "PATH", Environment.GetEnvironmentVariable("PATH"));
        EnsureEnvironmentValue(env, "TEMP", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        EnsureEnvironmentValue(env, "TMP", Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        return env;
    }

    private static void EnsureEnvironmentValue(IDictionary<string, string> env, string key, string? value)
    {
        if (!env.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
        {
            env[key] = value;
        }
    }

    private static string? ResolveBundledCliPath()
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var runtimeId = RuntimeInformation.RuntimeIdentifier;
        var preferredPath = Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeId, "native", executable);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        var runtimesDirectory = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (!Directory.Exists(runtimesDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(runtimesDirectory, executable, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    public async ValueTask DisposeAsync()
    {
        _sessions.Clear();
        if (_client is not null)
        {
            await DisposeClientQuietlyAsync(_client);
            _client = null;
        }
    }

    private IList<string>? GetEffectiveSkillDirectories(AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Include .github/skills from the process working directory if it exists
        var processCwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(processCwd, ".github", "skills")))
            AddUniquePath(dirs, seen, Path.Combine(processCwd, ".github", "skills"));
        // Include .github/skills from settings.WorkingDirectory if set and exists
        if (!string.IsNullOrWhiteSpace(settings.WorkingDirectory) &&
            Directory.Exists(Path.Combine(settings.WorkingDirectory, ".github", "skills")))
            AddUniquePath(dirs, seen, Path.Combine(settings.WorkingDirectory, ".github", "skills"));
        // Add ~/.copilot/skills only if it exists
        if (Directory.Exists(Path.Combine(home, ".copilot", "skills")))
            AddUniquePath(dirs, seen, Path.Combine(home, ".copilot", "skills"));
        foreach (var d in settings.SkillDirectories)
            if (Directory.Exists(d)) AddUniquePath(dirs, seen, d);
        if (dirs.Count > 0)
            _logger.Log("SKILL-DIRS", string.Join(", ", dirs));
        return dirs.Count > 0 ? dirs : null;
    }

    private IList<CustomAgentConfig>? LoadAgentsFromDirectories(AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Include .github/agents from the process working directory if it exists
        var processCwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(processCwd, ".github", "agents")))
            AddUniquePath(dirs, seen, Path.Combine(processCwd, ".github", "agents"));
        // Include .github/agents from settings.WorkingDirectory if set and exists
        if (!string.IsNullOrWhiteSpace(settings.WorkingDirectory) &&
            Directory.Exists(Path.Combine(settings.WorkingDirectory, ".github", "agents")))
            AddUniquePath(dirs, seen, Path.Combine(settings.WorkingDirectory, ".github", "agents"));
        // Add ~/.copilot/agents only if it exists
        if (Directory.Exists(Path.Combine(home, ".copilot", "agents")))
            AddUniquePath(dirs, seen, Path.Combine(home, ".copilot", "agents"));
        foreach (var d in settings.AgentDirectories)
            if (Directory.Exists(d)) AddUniquePath(dirs, seen, d);
        var agents = new List<CustomAgentConfig>();
        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                var agent = ParseAgentFile(file);
                if (agent != null) agents.Add(agent);
            }
        }
        if (agents.Count > 0)
            _logger.Log("AGENTS-FROM-DIRS", string.Join(", ", agents.Select(a => a.Name)));
        return agents.Count > 0 ? agents : null;
    }

    private CustomAgentConfig? ParseAgentFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var name = Path.GetFileNameWithoutExtension(path);
            string? description = null;
            string? displayName = null;
            List<string>? tools = null;
            var prompt = content;

            if (content.StartsWith("---", StringComparison.Ordinal))
            {
                var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    var frontmatter = content[3..end].Trim();
                    prompt = content[(end + 4)..].TrimStart();
                    foreach (var line in frontmatter.Split('\n'))
                    {
                        var colon = line.IndexOf(':');
                        if (colon < 0) continue;
                        var key = line[..colon].Trim().ToLowerInvariant();
                        var val = line[(colon + 1)..].Trim().Trim('"', '\'');
                        switch (key)
                        {
                            case "name": name = val; break;
                            case "description": description = val; break;
                            case "displayname": displayName = val; break;
                            case "tools": tools = ParseYamlStringList(val); break;
                        }
                    }
                }
            }
            return new CustomAgentConfig
            {
                Name = name,
                DisplayName = displayName ?? name,
                Description = description,
                Tools = tools!,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? "" : prompt,
            };
        }
        catch (Exception ex)
        {
            _logger.Log("AGENT-LOAD-ERROR", $"{path}: {ex.Message}");
            return null;
        }
    }

    private static List<string>? ParseYamlStringList(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('[') || !value.EndsWith(']')) return null;
        return [.. value[1..^1].Split(',').Select(s => s.Trim().Trim('"', '\'', ' ')).Where(s => !string.IsNullOrEmpty(s))];
    }

    private static void AddUniquePath(List<string> list, HashSet<string> seen, string path)
    {
        if (seen.Add(path)) list.Add(path);
    }

    private static void AddDirIfExists(List<string> list, string path)
    {
        if (Directory.Exists(path)) list.Add(path);
    }

    private static async Task DisposeClientQuietlyAsync(CopilotClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
            // The Copilot CLI may already be gone, especially after a native startup failure.
        }
    }
}

public enum PermissionPromptDecision
{
    Deny,
    AllowOnce,
    AllowForSession,
    SaveToSettings
}

public sealed record PermissionPrompt(
    string Kind,
    string? ToolName,
    string? FileName,
    string? Command,
    string? Host,
    IReadOnlyList<ShellCommandPermission> Commands,
    string? SessionTitle = null);

public sealed record ShellCommandPermission(string Identifier, bool ReadOnly);

public sealed record UserInputPrompt(string Question, IReadOnlyList<string> Choices, bool AllowFreeform, string? SessionTitle = null);

public sealed record UserInputPromptResult(string Answer, bool WasFreeform);
