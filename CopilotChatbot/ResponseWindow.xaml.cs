using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Win32;

namespace CopilotChatbot;

public partial class ResponseWindow : Window
{
    private readonly ChatMessage _message;
    private readonly string _html;
    private readonly Func<bool>? _isDarkThemeResolver;

    public ResponseWindow(HtmlRenderer renderer, ChatMessage message, bool isDark = false, Func<bool>? isDarkThemeResolver = null)
    {
        InitializeComponent();
        _message = message;
        _isDarkThemeResolver = isDarkThemeResolver;
        _html = renderer.RenderStandalone(message, isDark);
        ApplyWindowTheme(isDark);
        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme(ResolveCurrentTheme());
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        Loaded += async (_, _) =>
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.DefaultBackgroundColor = isDark
                ? System.Drawing.Color.FromArgb(255, 17, 24, 39)
                : System.Drawing.Color.White;
            Browser.NavigateToString(_html);
        };
    }

    public void ApplyWindowTheme(bool dark)
    {
        var page = dark ? "#0B1220" : "#F3F5F8";
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(page));
        ApplyNativeTitleBarTheme(dark);
    }

    private bool ResolveCurrentTheme() => _isDarkThemeResolver?.Invoke() ?? IsSystemDark();

    private void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.BeginInvoke(() => ApplyWindowTheme(ResolveCurrentTheme()));
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyNativeTitleBarTheme(bool dark)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmWindowAttributeUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private const int DwmWindowAttributeUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

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
