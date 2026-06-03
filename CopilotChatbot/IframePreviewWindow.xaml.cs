using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CopilotChatbot;

public partial class IframePreviewWindow : Window
{
    private readonly string _html;
    private readonly bool _isDark;

    public IframePreviewWindow(string html, bool isDark = false)
    {
        InitializeComponent();
        _html = html;
        _isDark = isDark;
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
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html|All files (*.*)|*.*",
            FileName = $"preview-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, _html);
        }
    }
}
