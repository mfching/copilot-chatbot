using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Web.WebView2.Wpf;

namespace CopilotChatbot;

public partial class ChatTabContent : UserControl
{
    /// <summary>Raised when the user submits a message. Argument is the non-empty prompt text
    /// (already cleared from the TextBox).</summary>
    public event Action<string>? SendRequested;

    /// <summary>Raised when the user clicks Stop.</summary>
    public event Action? StopRequested;

    /// <summary>Raised when the user toggles previous-article auto-collapse for this tab.</summary>
    public event Action<bool>? AutoCollapsePreviousArticleChanged;

    private Storyboard? _beamStoryboard;
    private Storyboard? _dotStoryboard;
    private Storyboard? _loadingStoryboard;
    private bool _updatingAutoCollapseToggle;

    private const string PreviousQuestionScript = """
        (function() {
            var users = Array.from(document.querySelectorAll('article.msg.user'));
            if (!users.length) return;

            var y = window.scrollY || document.documentElement.scrollTop || document.body.scrollTop || 0;
            var positions = users.map(function(el) {
                return { el: el, top: el.getBoundingClientRect().top + y };
            });

            var target = positions[0];
            for (var i = positions.length - 1; i >= 0; i--) {
                if (positions[i].top < y - 8) {
                    target = positions[i];
                    break;
                }
            }

            var details = target.el.querySelector('details');
            if (details) details.open = true;
            window.scrollTo({ top: Math.max(0, target.top - 8), behavior: 'smooth' });
        })()
        """;

    private const string NextQuestionScript = """
        (function() {
            var users = Array.from(document.querySelectorAll('article.msg.user'));
            if (!users.length) return;

            var y = window.scrollY || document.documentElement.scrollTop || document.body.scrollTop || 0;
            var positions = users.map(function(el) {
                return { el: el, top: el.getBoundingClientRect().top + y };
            });

            var target = positions[positions.length - 1];
            for (var i = 0; i < positions.length; i++) {
                if (positions[i].top > y + 8) {
                    target = positions[i];
                    break;
                }
            }

            var details = target.el.querySelector('details');
            if (details) details.open = true;
            window.scrollTo({ top: Math.max(0, target.top - 8), behavior: 'smooth' });
        })()
        """;

    private const string ExpandAllArticlesScript = """
        (function() {
            document.querySelectorAll('article.msg details').forEach(function(details) {
                details.open = true;
            });
        })()
        """;

    private const string CollapseAllArticlesScript = """
        (function() {
            document.querySelectorAll('article.msg details').forEach(function(details) {
                details.open = false;
            });
        })()
        """;

    public ChatTabContent()
    {
        InitializeComponent();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Places the session's WebView2 into this tab's content area.</summary>
    public void SetBrowser(WebView2 browser) => BrowserHost.Content = browser;

    /// <summary>Shows or hides the centered loading overlay while restored chat history is prepared.</summary>
    public void SetLoading(bool isLoading, string? text = null)
    {
        LoadingText.Text = string.IsNullOrWhiteSpace(text) ? "Loading chat history..." : text;
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading) StartLoadingAnimation(); else StopLoadingAnimation();
    }

    /// <summary>Updates Send/Stop/Beam state based on whether the session is thinking
    /// and whether the session is healthy enough to accept input.</summary>
    public void SetState(bool isPending, bool sessionMissing)
    {
        var canSend = !isPending && !sessionMissing;
        SendBtn.IsEnabled = canSend;
        PromptBox.IsEnabled = canSend;
        StopBtn.IsEnabled = isPending;
        StopBtn.Visibility = isPending ? Visibility.Visible : Visibility.Collapsed;
        PromptBeam.Visibility = isPending ? Visibility.Visible : Visibility.Collapsed;
        if (isPending) StartBeamAnimation(); else StopBeamAnimation();

        // Keep the browser's streaming class in sync so popup buttons are disabled while streaming.
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
        {
            var js = isPending
                ? "document.querySelector('main')?.classList.add('streaming')"
                : "document.querySelector('main')?.classList.remove('streaming')";
            _ = browser.ExecuteScriptAsync(js);
        }
    }

    /// <summary>Updates the activity status bar text and pulsing dot.</summary>
    public void SetStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            ActivityText.Text = "Ready";
            ActivityDot.Opacity = 0.4;
            StopDotAnimation();
        }
        else
        {
            ActivityText.Text = status;
            ActivityDot.Opacity = 1.0;
            StartDotAnimation();
        }
    }

    public void SetAutoCollapsePreviousArticle(bool enabled)
    {
        _updatingAutoCollapseToggle = true;
        try
        {
            AutoCollapsePreviousBtn.IsChecked = enabled;
            AutoCollapsePreviousBtn.ToolTip = enabled
                ? "Auto-collapse previous message on send is on"
                : "Auto-collapse previous message on send is off";
        }
        finally
        {
            _updatingAutoCollapseToggle = false;
        }
    }

    /// <summary>Stops all running animations (call before the tab is closed).</summary>
    public void Cleanup()
    {
        StopBeamAnimation();
        StopDotAnimation();
        StopLoadingAnimation();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void PromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            TryRaiseSend();
        }
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e) => TryRaiseSend();

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;   // disable immediately to prevent double-click
        StopRequested?.Invoke();
    }

    private void ScrollToBottom_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync("window.scrollTo(0, document.documentElement.scrollHeight)");
    }

    private void PreviousQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync(PreviousQuestionScript);
    }

    private void NextQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync(NextQuestionScript);
    }

    private void ScrollToTop_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync("window.scrollTo(0, 0)");
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync(ExpandAllArticlesScript);
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserHost.Content is WebView2 { CoreWebView2: not null } browser)
            _ = browser.ExecuteScriptAsync(CollapseAllArticlesScript);
    }

    private void AutoCollapsePrevious_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingAutoCollapseToggle)
        {
            return;
        }

        var enabled = AutoCollapsePreviousBtn.IsChecked == true;
        AutoCollapsePreviousBtn.ToolTip = enabled
            ? "Auto-collapse previous message on send is on"
            : "Auto-collapse previous message on send is off";
        AutoCollapsePreviousArticleChanged?.Invoke(enabled);
    }

    private void TryRaiseSend()
    {
        var prompt = PromptBox.Text;
        if (string.IsNullOrWhiteSpace(prompt)) return;
        PromptBox.Clear();
        SendRequested?.Invoke(prompt);
    }

    // ── Beam animation ────────────────────────────────────────────────────────

    private void StartBeamAnimation()
    {
        double w = PromptBox.ActualWidth;
        double h = PromptBox.ActualHeight;
        if (w <= 0 || h <= 0) return;

        const double strokeThickness = 2.0;
        const double dashLengthPx = 60.0;
        double perimeterPx = 2.0 * (w + h);
        double perimeterUnits = perimeterPx / strokeThickness;
        double dashUnits = dashLengthPx / strokeThickness;
        double gapUnits = perimeterUnits - dashUnits;

        PromptBeam.StrokeDashArray = new DoubleCollection { dashUnits, gapUnits };
        PromptBeam.StrokeDashOffset = 0;

        _beamStoryboard?.Stop();
        _beamStoryboard = new Storyboard();

        var offsetAnim = new DoubleAnimation
        {
            From = 0,
            To = -perimeterUnits,
            Duration = new Duration(TimeSpan.FromSeconds(3.5)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(offsetAnim, PromptBeam);
        Storyboard.SetTargetProperty(offsetAnim, new PropertyPath(Shape.StrokeDashOffsetProperty));
        _beamStoryboard.Children.Add(offsetAnim);

        var colorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0x45, 0x45), KeyTime.FromTimeSpan(TimeSpan.Zero)));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0x80, 0x20), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xDC, 0x14, 0x3C), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0x00, 0x00), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));
        colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0xFF, 0x45, 0x45), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.8))));
        Storyboard.SetTarget(colorAnim, BeamBrush);
        Storyboard.SetTargetProperty(colorAnim, new PropertyPath(SolidColorBrush.ColorProperty));
        _beamStoryboard.Children.Add(colorAnim);

        _beamStoryboard.Begin(this);
    }

    private void StopBeamAnimation()
    {
        _beamStoryboard?.Stop();
        _beamStoryboard = null;
    }

    // ── Activity dot animation ────────────────────────────────────────────────

    private void StartDotAnimation()
    {
        if (_dotStoryboard != null) return;
        _dotStoryboard = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.25,
            Duration = new Duration(TimeSpan.FromSeconds(0.65)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, ActivityDot);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
        _dotStoryboard.Children.Add(anim);
        _dotStoryboard.Begin(this);
    }

    private void StopDotAnimation()
    {
        _dotStoryboard?.Stop();
        _dotStoryboard = null;
    }

    private void StartLoadingAnimation()
    {
        if (_loadingStoryboard != null) return;
        _loadingStoryboard = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(0.9)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, LoadingSpinner);
        Storyboard.SetTargetProperty(anim, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        _loadingStoryboard.Children.Add(anim);
        _loadingStoryboard.Begin(this);
    }

    private void StopLoadingAnimation()
    {
        _loadingStoryboard?.Stop();
        _loadingStoryboard = null;
    }
}
