using System.Windows;
using CopilotChatbot.Services;

namespace CopilotChatbot;

public partial class PermissionPromptWindow : Window
{
    public PermissionPromptDecision Decision { get; private set; } = PermissionPromptDecision.Deny;

    public PermissionPromptWindow(PermissionPrompt prompt)
    {
        InitializeComponent();
        Title = FormatDialogTitle(prompt.SessionTitle, "Permission Request");
        KindTextBlock.Text = $"Permission type: {prompt.Kind}";
        ScopeTextBlock.Text = GetScopeText(prompt);
        DetailsTextBox.Text = BuildDetails(prompt);
    }

    private static string FormatDialogTitle(string? sessionTitle, string dialogTitle) =>
        string.IsNullOrWhiteSpace(sessionTitle)
            ? dialogTitle
            : $"{sessionTitle} - {dialogTitle}";

    private static string GetScopeText(PermissionPrompt prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt.Host))
            return $"Network host: {prompt.Host}";
        if (!string.IsNullOrWhiteSpace(prompt.ToolName))
            return $"Tool: {prompt.ToolName}";
        if (!string.IsNullOrWhiteSpace(prompt.FileName))
            return $"File or folder: {prompt.FileName}";
        if (prompt.Commands.Count > 0)
            return $"Shell commands: {string.Join(", ", prompt.Commands.Select(command => command.Identifier))}";
        if (!string.IsNullOrWhiteSpace(prompt.Command))
            return "Shell command access";
        return "The Copilot SDK requested access outside the current whitelist.";
    }

    private static string BuildDetails(PermissionPrompt prompt)
    {
        var commandIdentifiers = prompt.Commands.Count == 0
            ? ""
            : string.Join(Environment.NewLine, prompt.Commands.Select(command =>
                $"  - {command.Identifier}{(command.ReadOnly ? " (read-only)" : "")}"));

        return string.Join(Environment.NewLine, new[]
        {
            $"Kind: {prompt.Kind}",
            $"Tool: {prompt.ToolName}",
            $"File: {prompt.FileName}",
            $"Host: {prompt.Host}",
            prompt.Commands.Count == 0 ? "" : $"Commands:{Environment.NewLine}{commandIdentifiers}",
            $"Command: {prompt.Command}"
        }.Where(line => !line.EndsWith(": ", StringComparison.Ordinal)));
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => CloseWith(PermissionPromptDecision.Deny);

    private void AllowOnce_Click(object sender, RoutedEventArgs e) => CloseWith(PermissionPromptDecision.AllowOnce);

    private void AllowSession_Click(object sender, RoutedEventArgs e) => CloseWith(PermissionPromptDecision.AllowForSession);

    private void SaveSetting_Click(object sender, RoutedEventArgs e) => CloseWith(PermissionPromptDecision.SaveToSettings);

    private void CloseWith(PermissionPromptDecision decision)
    {
        Decision = decision;
        DialogResult = true;
    }
}
