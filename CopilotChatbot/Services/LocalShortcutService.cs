using System.IO;
using CopilotChatbot.Models;

namespace CopilotChatbot.Services;

public interface ILocalShortcutService
{
    event Action<ChatSessionView, string?>? StatusChanged;

    Task<LocalShortcutResult?> TryExecuteAsync(ChatSessionView chat, string prompt);
}

public sealed class LocalShortcutService : ILocalShortcutService
{
    private readonly CopilotChatService _copilot;
    private readonly SettingsStore _settingsStore;
    private readonly IReadOnlyList<LocalShortcut> _shortcuts;

    public LocalShortcutService(CopilotChatService copilot, SettingsStore settingsStore)
    {
        _copilot = copilot;
        _settingsStore = settingsStore;
        _shortcuts = CreateShortcuts();
    }

    public event Action<ChatSessionView, string?>? StatusChanged;

    public async Task<LocalShortcutResult?> TryExecuteAsync(ChatSessionView chat, string prompt)
    {
        if (!TryCreateInvocation(chat, prompt, out var invocation))
        {
            return null;
        }

        var shortcut = _shortcuts.FirstOrDefault(shortcut => shortcut.Matches(invocation.Command));
        return shortcut is null
            ? null
            : await shortcut.Execute(invocation);
    }

    private IReadOnlyList<LocalShortcut> CreateShortcuts() =>
    [
        new(
            "/mcp",
            "Show MCP servers and tools for the current session.",
            ExecuteMcpShortcutAsync),
        new(
            "/cwd",
            "Show the working directory used by the Copilot CLI.",
            ExecuteCwdShortcutAsync),
        new(
            "/env",
            "Show Copilot-related environment details.",
            ExecuteEnvShortcutAsync),
        new(
            "/memory",
            "Show or change long-term memory permission.",
            ExecuteMemoryShortcutAsync),
        new(
            "/plan",
            "Ask Copilot to create an implementation plan before coding.",
            ExecutePlanShortcutAsync)
    ];

    private static bool TryCreateInvocation(ChatSessionView chat, string prompt, out LocalShortcutInvocation invocation)
    {
        var trimmed = prompt.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            invocation = default!;
            return false;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            invocation = default!;
            return false;
        }

        invocation = new LocalShortcutInvocation(
            chat,
            trimmed,
            parts[0].ToLowerInvariant(),
            GetArgumentText(trimmed, parts[0]),
            parts.Skip(1).ToArray());
        return true;
    }

    private async Task<LocalShortcutResult> ExecuteMcpShortcutAsync(LocalShortcutInvocation invocation)
    {
        StatusChanged?.Invoke(invocation.Chat, "Inspecting MCP servers...");
        try
        {
            var snapshot = await _copilot.GetCapabilitiesSnapshotAsync(invocation.Chat);
            return new LocalShortcutResult(ChatMessageKind.System, FormatMcpShortcut(snapshot));
        }
        catch (Exception ex)
        {
            return new LocalShortcutResult(ChatMessageKind.Error, "Failed to inspect MCP servers.\n\n" + ex.Message);
        }
        finally
        {
            StatusChanged?.Invoke(invocation.Chat, null);
        }
    }

    private Task<LocalShortcutResult> ExecuteCwdShortcutAsync(LocalShortcutInvocation invocation)
    {
        var settings = _settingsStore.Load();
        var changed = false;
        string? previous = null;

        if (!string.IsNullOrWhiteSpace(invocation.ArgumentText))
        {
            previous = ResolveEffectiveWorkingDirectory(settings);
            var target = ResolveRequestedWorkingDirectory(invocation.ArgumentText, previous);
            if (!Directory.Exists(target))
            {
                return Task.FromResult(new LocalShortcutResult(
                    ChatMessageKind.Error,
                    $"Working directory was not changed because the folder does not exist.\n\nRequested: {target}"));
            }

            settings.WorkingDirectory = target;
            _settingsStore.Save(settings);
            Directory.SetCurrentDirectory(target);
            changed = true;
        }

        var configured = settings.WorkingDirectory;
        var effective = ResolveEffectiveWorkingDirectory(settings);
        var lines = new List<string>
        {
            "Working directory",
            "",
            $"- Effective: {effective}",
            $"- Configured: {(string.IsNullOrWhiteSpace(configured) ? "(not set)" : configured)}",
            $"- Process: {Directory.GetCurrentDirectory()}",
            $"- Exists: {Directory.Exists(effective)}"
        };
        if (changed)
        {
            lines.Insert(2, "- Updated: yes");
            lines.Insert(3, $"- Previous: {previous}");
        }

        return Task.FromResult(new LocalShortcutResult(ChatMessageKind.System, string.Join("\n", lines)));
    }

    private Task<LocalShortcutResult> ExecuteEnvShortcutAsync(LocalShortcutInvocation invocation)
    {
        var settings = _settingsStore.Load();
        var lines = new List<string>
        {
            "Environment",
            "",
            $"- User profile: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}",
            $"- App data: {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}",
            $"- Temp: {Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)}",
            $"- Working directory: {ResolveEffectiveWorkingDirectory(settings)}",
            $"- GitHub token setting: {(string.IsNullOrWhiteSpace(settings.GitHubToken) ? "not configured" : "configured")}"
        };

        AddEnvironmentValue(lines, "PATH");
        AddEnvironmentValue(lines, "GH_TOKEN", redact: true);
        AddEnvironmentValue(lines, "GITHUB_TOKEN", redact: true);

        if (settings.UserSecrets.Count > 0)
        {
            lines.Add("");
            lines.Add($"Configured user secrets ({settings.UserSecrets.Count})");
            foreach (var secret in settings.UserSecrets.OrderBy(secret => secret.Name, StringComparer.OrdinalIgnoreCase))
            {
                var envName = string.IsNullOrWhiteSpace(secret.EnvironmentVariable) ? "(no env var)" : secret.EnvironmentVariable;
                lines.Add($"- {secret.Name}: {envName}");
            }
        }

        return Task.FromResult(new LocalShortcutResult(ChatMessageKind.System, string.Join("\n", lines)));
    }

    private Task<LocalShortcutResult> ExecuteMemoryShortcutAsync(LocalShortcutInvocation invocation)
    {
        var settings = _settingsStore.Load();
        var argument = invocation.ArgumentText.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(argument))
        {
            return Task.FromResult(new LocalShortcutResult(
                ChatMessageKind.System,
                FormatMemoryStatus(settings.Permissions.AllowMemoryByDefault, changed: false)));
        }

        bool enabled;
        if (argument is "on" or "enable" or "enabled" or "true")
        {
            enabled = true;
        }
        else if (argument is "off" or "disable" or "disabled" or "false")
        {
            enabled = false;
        }
        else
        {
            return Task.FromResult(new LocalShortcutResult(
                ChatMessageKind.Error,
                "Usage: /memory [on|off]\n\nRun /memory without an argument to show the current state."));
        }

        settings.Permissions.AllowMemoryByDefault = enabled;
        _settingsStore.Save(settings);
        return Task.FromResult(new LocalShortcutResult(
            ChatMessageKind.System,
            FormatMemoryStatus(enabled, changed: true)));
    }

    private Task<LocalShortcutResult> ExecutePlanShortcutAsync(LocalShortcutInvocation invocation)
    {
        var task = string.IsNullOrWhiteSpace(invocation.ArgumentText)
            ? "the implementation request from the current conversation context"
            : invocation.ArgumentText.Trim();

        var prompt = $"""
Create an implementation plan before coding.

Task:
{task}

Instructions:
- Inspect the relevant project context first if needed.
- Identify the concrete files, services, UI surfaces, and data flows likely to change.
- Present a concise implementation plan before making code changes.
- Include any assumptions, risks, or open questions that could affect the implementation.
- Do not start coding until the plan is clear.
""";

        var visiblePrompt = string.IsNullOrWhiteSpace(invocation.ArgumentText)
            ? "/plan"
            : "/plan " + invocation.ArgumentText.Trim();

        return Task.FromResult(LocalShortcutResult.ForPrompt(prompt, visiblePrompt));
    }

    private static string FormatMemoryStatus(bool enabled, bool changed)
    {
        var lines = new List<string>
        {
            "Memory",
            "",
            $"- State: {(enabled ? "on" : "off")}",
            "- Scope: all sessions",
            "- Behavior: " + (enabled
                ? "memory permission requests are approved automatically"
                : "memory permission requests are rejected automatically")
        };

        if (changed)
        {
            lines.Insert(2, "- Updated: yes");
        }

        return string.Join("\n", lines);
    }

    private static void AddEnvironmentValue(ICollection<string> lines, string name, bool redact = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"- {name}: (not set)");
            return;
        }

        lines.Add($"- {name}: {(redact ? "(set)" : value)}");
    }

    private static string ResolveEffectiveWorkingDirectory(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.WorkingDirectory) && Directory.Exists(settings.WorkingDirectory)
            ? settings.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string ResolveRequestedWorkingDirectory(string argumentText, string baseDirectory)
    {
        var path = TrimPathArgument(argumentText);
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            path.StartsWith("~" + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Path.GetFullPath(path, baseDirectory);
    }

    private static string TrimPathArgument(string argumentText)
    {
        var path = argumentText.Trim();
        if (path.Length >= 2 &&
            ((path[0] == '"' && path[^1] == '"') ||
             (path[0] == '\'' && path[^1] == '\'')))
        {
            path = path[1..^1].Trim();
        }

        return path;
    }

    private static string GetArgumentText(string prompt, string command)
    {
        return prompt.Length <= command.Length
            ? ""
            : prompt[command.Length..].Trim();
    }

    private static string FormatMcpShortcut(SessionCapabilitiesSnapshot snapshot)
    {
        if (snapshot.McpServers.Count == 0)
        {
            return "MCP servers\n\nNo MCP servers registered.";
        }

        var lines = new List<string>
        {
            $"MCP servers ({snapshot.McpServers.Count})",
            ""
        };

        foreach (var server in snapshot.McpServers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"- {server.Name} [{server.Status}]");
            if (server.Tools.Count == 0)
            {
                lines.Add("  - No tools reported");
            }
            else
            {
                foreach (var tool in server.Tools.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"  - {tool}");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private sealed class LocalShortcut
    {
        private readonly Func<LocalShortcutInvocation, Task<LocalShortcutResult>> _execute;
        private readonly HashSet<string> _commands;

        public LocalShortcut(
            string name,
            string description,
            Func<LocalShortcutInvocation, Task<LocalShortcutResult>> execute,
            params string[] aliases)
        {
            Name = NormalizeName(name);
            Description = description;
            _execute = execute;
            _commands = aliases
                .Prepend(Name)
                .Select(NormalizeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string Name { get; }
        public string Description { get; }

        public bool Matches(string command) => _commands.Contains(NormalizeName(command));

        public Task<LocalShortcutResult> Execute(LocalShortcutInvocation invocation) => _execute(invocation);

        private static string NormalizeName(string name)
        {
            var normalized = name.Trim().ToLowerInvariant();
            return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
        }
    }

    private sealed record LocalShortcutInvocation(
        ChatSessionView Chat,
        string RawPrompt,
        string Command,
        string ArgumentText,
        IReadOnlyList<string> Arguments);
}

public sealed record LocalShortcutResult(
    ChatMessageKind Kind,
    string Content,
    string? PromptToSend = null,
    string? UserVisiblePrompt = null)
{
    public static LocalShortcutResult ForPrompt(string promptToSend, string userVisiblePrompt) =>
        new(ChatMessageKind.User, "", promptToSend, userVisiblePrompt);
}
