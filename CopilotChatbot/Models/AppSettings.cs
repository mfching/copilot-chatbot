using System.Collections.ObjectModel;

namespace CopilotChatbot.Models;

public enum AppThemeMode { Light, Dark, System, FollowTheSun }

public sealed class AppSettings
{
    public string? GitHubToken { get; set; }
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public ObservableCollection<UserSecretSetting> UserSecrets { get; set; } = [];
    public string? SelectedModelId { get; set; }
    public string? SelectedReasoningEffort { get; set; }
    public PermissionSettings Permissions { get; set; } = new();
    public string? DefaultSystemPrompt { get; set; }
    public bool EnableDebugLogging { get; set; }
    public string? WorkingDirectory { get; set; }
    public ObservableCollection<string> AgentDirectories { get; set; } = [];
    public ObservableCollection<string> SkillDirectories { get; set; } = [];
}

public sealed class UserSecretSetting
{
    public string Name { get; set; } = "";
    public string EnvironmentVariable { get; set; } = "";
    public string EncryptedValue { get; set; } = "";
}

public sealed class PermissionSettings
{
    public ObservableCollection<FolderPermission> Folders { get; set; } = [];
    public ObservableCollection<string> AllowedTools { get; set; } = [];
    public ObservableCollection<string> AllowedHosts { get; set; } = [];
    public ObservableCollection<PermissionRule> SavedRules { get; set; } = [];
    public bool AllowMcpByDefault { get; set; } = false;
    public bool AllowCustomToolsByDefault { get; set; } = false;
    public bool AllowMemoryByDefault { get; set; } = false;
}

public sealed class PermissionRule
{
    public string Kind { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Command { get; set; } = "";
    public string CommandIdentifiers { get; set; } = "";
    public string Host { get; set; } = "";

    public string Summary
    {
        get
        {
            var parts = new[] { ToolName, Host, FileName, CommandIdentifiers, Command }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            return string.Join(" | ", parts);
        }
    }
}

public sealed class FolderPermission
{
    public string Path { get; set; } = "";
    public bool CanWrite { get; set; }

    public string Access => CanWrite ? "Read/Write" : "Read";
}

public sealed class ModelChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsFallback { get; init; }
    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) || Name == Id ? Id : $"{Name} ({Id})";
            var costText = BillingMultiplier is null ? "" : $" - {CostText}";
            return $"{name}{costText}{(IsFallback ? " - fallback" : "")}";
        }
    }
    public bool SupportsReasoningEffort { get; init; }
    public IReadOnlyList<string> ReasoningEfforts { get; init; } = [];
    public string? DefaultReasoningEffort { get; init; }
    public double? BillingMultiplier { get; init; }
    public string CostText => BillingMultiplier is null ? "Cost unavailable" : $"Cost x{BillingMultiplier:0.##}";
}

public sealed record CopilotRuntimeStatus(
    bool IsConnected,
    string CliVersion,
    int ProtocolVersion,
    bool IsAuthenticated,
    string Login,
    string AuthType,
    string Message);

public sealed record CopilotUsageStatus(
    string Model,
    double? InputTokens,
    double? OutputTokens,
    double? ReasoningTokens,
    double? Cost,
    double? UsedRequests,
    double? EntitlementRequests,
    double? RemainingPercentage,
    DateTimeOffset? ResetDate)
{
    public string ToStatusText()
    {
        var tokenText = $"Tokens in/out/reasoning: {Format(InputTokens)}/{Format(OutputTokens)}/{Format(ReasoningTokens)}";
        var requestText = UsedRequests is null || EntitlementRequests is null
            ? "Requests: n/a"
            : $"Requests: {UsedRequests:0.##}/{EntitlementRequests:0.##}";
        var remainingText = RemainingPercentage is null ? "remaining n/a" : $"{RemainingPercentage:0.#}% remaining";
        var resetText = ResetDate is null ? "" : $", resets {ResetDate:yyyy-MM-dd}";
        var costText = Cost is null ? "cost n/a" : $"cost {Cost:0.####}";
        return $"{Model}: {tokenText}; {requestText}, {remainingText}{resetText}; {costText}";
    }

    private static string Format(double? value) => value is null ? "n/a" : value.Value.ToString("0.##");
}
