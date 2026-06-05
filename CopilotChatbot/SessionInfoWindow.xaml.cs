using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CopilotChatbot.Models;

namespace CopilotChatbot;

/// <summary>Converts a status string to a coloured dot brush.</summary>
public sealed class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").ToLowerInvariant();
        if (s is "running" or "connected" or "active" or "ok" or "loaded" or "ready" or "enabled")
            return new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));   // green
        if (s is "error" or "failed" or "disconnected" or "disabled" or "crashed")
            return new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));   // red
        if (s is "loading" or "starting" or "pending" or "warning" or "connecting")
            return new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));   // amber
        return new SolidColorBrush(Color.FromRgb(0xA0, 0xAB, 0xB4));        // grey
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Collapses a TextBlock whose text is null or empty.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ─── Tiny view-model wrappers so ItemsControl can bind the Tools sub-list ─────

public sealed class McpServerVm
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public IReadOnlyList<string> Tools { get; init; } = [];
}

// ──────────────────────────────────────────────────────────────────────────────

public partial class SessionInfoWindow : Window
{
    public SessionInfoWindow(SessionCapabilitiesSnapshot snapshot, Window owner)
    {
        Owner = owner;
        InitializeComponent();
        Populate(snapshot);
    }

    public void UpdateSnapshot(SessionCapabilitiesSnapshot snapshot)
    {
        Populate(snapshot);
    }

    private void Populate(SessionCapabilitiesSnapshot snap)
    {
        McpEmptyText.Visibility = Visibility.Collapsed;
        AgentsEmptyText.Visibility = Visibility.Collapsed;
        SkillsEmptyText.Visibility = Visibility.Collapsed;
        McpList.ItemsSource = null;
        AgentsList.ItemsSource = null;
        SkillsList.ItemsSource = null;

        // Subtitle
        SubtitleText.Text =
            $"{snap.McpServers.Count} MCP server{(snap.McpServers.Count == 1 ? "" : "s")}  ·  " +
            $"{snap.Agents.Count} agent{(snap.Agents.Count == 1 ? "" : "s")}  ·  " +
            $"{snap.Skills.Count} skill{(snap.Skills.Count == 1 ? "" : "s")}";

        // ── MCP Servers ─────────────────────────────────────────────────────
        McpHeader.Text = $"MCP Servers  ({snap.McpServers.Count})";
        if (snap.McpServers.Count == 0)
        {
            McpEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            McpList.ItemsSource = snap.McpServers.Select(s => new McpServerVm
            {
                Name = s.Name,
                Status = s.Status,
                Tools = s.Tools
            }).ToList();
        }

        // ── Agents / Extensions ─────────────────────────────────────────────
        AgentsHeader.Text = $"Agents  ({snap.Agents.Count})";
        if (snap.Agents.Count == 0)
            AgentsEmptyText.Visibility = Visibility.Visible;
        else
            AgentsList.ItemsSource = snap.Agents;

        // ── Skills ──────────────────────────────────────────────────────────
        SkillsHeader.Text = $"Skills  ({snap.Skills.Count})";
        if (snap.Skills.Count == 0)
            SkillsEmptyText.Visibility = Visibility.Visible;
        else
            SkillsList.ItemsSource = snap.Skills;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
