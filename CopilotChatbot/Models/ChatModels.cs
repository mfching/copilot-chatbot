using System.Collections.ObjectModel;
using Microsoft.Web.WebView2.Wpf;

namespace CopilotChatbot.Models;

public enum ChatMessageKind
{
    User,
    Assistant,
    Reasoning,
    Intent,
    Tool,
    Prompt,
    Error,
    System
}

public sealed class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ChatMessageKind Kind { get; init; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Set when the response turn is fully received. Null while streaming or for user messages.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    public ChatPromptState? Prompt { get; set; }
    public Dictionary<string, double> IframeHeights { get; set; } = [];
}

public sealed class ChatPromptState
{
    public string Type { get; set; } = "";
    public List<string> Choices { get; set; } = [];
    public bool AllowFreeform { get; set; }
    public bool IsAnswered { get; set; }
    public string Answer { get; set; } = "";
    public bool WasFreeform { get; set; }
}

public sealed record McpServerInfo(string Name, string Status, IReadOnlyList<string> Tools);
public sealed record AgentInfo(string Name, string Status, string Source);
public sealed record SkillInfo(string Name, string? Description);
public sealed record SessionCapabilitiesSnapshot(
    IReadOnlyList<McpServerInfo> McpServers,
    IReadOnlyList<AgentInfo> Agents,
    IReadOnlyList<SkillInfo> Skills);

public sealed class ChatSessionView
{
    public string Title { get; set; }
    public string ProjectId { get; set; } = PersistedChatProject.DefaultProjectId;
    public string? CopilotSessionId { get; set; }
    public bool IsSessionMissing { get; set; }
    public bool IsPageInitialized { get; set; }
    public bool IsPending { get; set; }
    public bool HasUnreadResponse { get; set; }
    public bool HasPendingUserInput { get; set; }
    public bool IsApplyingBufferedUpdates { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SelectedModelId { get; set; }
    public string? SelectedReasoningEffort { get; set; }
    public string? LastStatus { get; set; }
    public bool AutoCollapsePreviousArticle { get; set; }
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public WebView2 Browser { get; } = new();

    public ChatSessionView(string title)
    {
        Title = title;
    }
}

public sealed class PersistedChatState
{
    public List<PersistedChatProject> Projects { get; set; } = [];
    public List<PersistedChatSession> Sessions { get; set; } = [];
    public string? SelectedSessionId { get; set; }
    public double TabHeaderWidth { get; set; }
}

public sealed class PersistedChatProject
{
    public const string DefaultProjectId = "default";

    public string Id { get; set; } = DefaultProjectId;
    public string Name { get; set; } = "Default";
    public bool IsCollapsed { get; set; }
}

public sealed class PersistedChatSession
{
    public string Title { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? CopilotSessionId { get; set; }
    public string? SystemPrompt { get; set; }
    public string? SelectedModelId { get; set; }
    public string? SelectedReasoningEffort { get; set; }
    public bool AutoCollapsePreviousArticle { get; set; }
    public bool IsSessionMissing { get; set; }
    public List<PersistedChatMessage> Messages { get; set; } = [];
}

public sealed class PersistedChatMessage
{
    public string Id { get; set; } = "";
    public ChatMessageKind Kind { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ChatPromptState? Prompt { get; set; }
    public Dictionary<string, double> IframeHeights { get; set; } = [];
}
