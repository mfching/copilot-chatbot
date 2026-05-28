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
    public string? CopilotSessionId { get; set; }
    public bool IsSessionMissing { get; set; }
    public bool IsPageInitialized { get; set; }
    public bool IsPending { get; set; }
    public bool HasUnreadResponse { get; set; }
    public string? SystemPrompt { get; set; }
    public string? LastStatus { get; set; }
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public WebView2 Browser { get; } = new();

    public ChatSessionView(string title)
    {
        Title = title;
    }
}

public sealed class PersistedChatState
{
    public List<PersistedChatSession> Sessions { get; set; } = [];
    public string? SelectedSessionId { get; set; }
}

public sealed class PersistedChatSession
{
    public string Title { get; set; } = "";
    public string? CopilotSessionId { get; set; }
    public string? SystemPrompt { get; set; }
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
}
