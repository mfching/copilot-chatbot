using System.IO;
using System.Windows;
using System.Windows.Media;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Win32;

namespace CopilotChatbot;

public partial class ResponseWindow : Window
{
    private readonly ChatMessage _message;
    private readonly string _html;
    private readonly bool _isDark;

    public ResponseWindow(HtmlRenderer renderer, ChatMessage message, bool isDark = false)
    {
        InitializeComponent();
        _message = message;
        _isDark = isDark;
        _html = renderer.RenderStandalone(message, isDark);
        ApplyWindowTheme(isDark);
        Loaded += async (_, _) =>
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.DefaultBackgroundColor = isDark
                ? System.Drawing.Color.FromArgb(255, 17, 24, 39)
                : System.Drawing.Color.White;
            Browser.NavigateToString(_html);
        };
    }

    private void ApplyWindowTheme(bool dark)
    {
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#111827" : "#F3F5F8"));
        SetBrush("SurfaceBrush",       dark ? "#111827" : "#FFFFFF");
        SetBrush("ControlBrush",       dark ? "#1D293D" : "#F8FAFC");
        SetBrush("BorderBrushModern",  dark ? "#344054" : "#D0D7DE");
        SetBrush("TextBrush",          dark ? "#F8FAFC" : "#1F2328");
        SetBrush("MutedTextBrush",     dark ? "#B8C2CC" : "#5D6673");
        SetBrush("AccentBrush",        dark ? "#6CB6FF" : "#0A65CC");
        SetBrush("AccentSoftBrush",    dark ? "#132B4D" : "#E7F1FF");
    }

    private void SetBrush(string key, string hex)
    {
        if (Resources.Contains(key))
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_message.Content);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html|Markdown/Text (*.md)|*.md|All files (*.*)|*.*",
            FileName = $"response-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, Path.GetExtension(dialog.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase) ? _html : _message.Content);
        }
    }
}
