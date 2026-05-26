using System.IO;
using System.Windows;
using CopilotChatbot.Models;
using Microsoft.Win32;

namespace CopilotChatbot;

public partial class SessionSystemPromptWindow : Window
{
    private readonly ChatSessionView _chat;
    private readonly string? _globalDefault;

    /// <summary>Raised when the user clicks Apply so the main window can react.</summary>
    public event Action<string?>? PromptApplied;

    public SessionSystemPromptWindow(ChatSessionView chat, string? globalDefault, Window owner)
    {
        Owner = owner;
        _chat = chat;
        _globalDefault = globalDefault;
        InitializeComponent();

        SubtitleText.Text = $"Tab: {chat.Title}";
        PromptTextBox.Text = chat.SystemPrompt ?? globalDefault ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load system prompt",
            Filter = "Text files (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                PromptTextBox.Text = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read file:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PromptTextBox.Text = "";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var text = PromptTextBox.Text.Trim();
        _chat.SystemPrompt = string.IsNullOrEmpty(text) ? null : text;
        PromptApplied?.Invoke(_chat.SystemPrompt);
    }
}
