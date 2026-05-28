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

            var wrapper = $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"margin:0\">" +
                          $"<iframe style=\"width:100%;height:100vh;border:0\" sandbox=\"allow-scripts allow-same-origin\" srcdoc=\"{System.Net.WebUtility.HtmlEncode(_html)}\"></iframe>" +
                          "</body></html>";
            Browser.NavigateToString(wrapper);
        };
    }

    private void ApplyWindowTheme(bool dark)
    {
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#111827" : "#F3F5F8"));
        SetBrush("SurfaceBrush", dark ? "#111827" : "#FFFFFF");
        SetBrush("ControlBrush", dark ? "#1D293D" : "#F8FAFC");
        SetBrush("BorderBrushModern", dark ? "#344054" : "#D0D7DE");
        SetBrush("TextBrush", dark ? "#F8FAFC" : "#1F2328");
        SetBrush("MutedTextBrush", dark ? "#B8C2CC" : "#5D6673");
        SetBrush("AccentBrush", dark ? "#6CB6FF" : "#0A65CC");
    }

    private void SetBrush(string key, string hex)
    {
        if (Resources.Contains(key))
        {
            Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
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
