using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Web.WebView2.Core;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;


namespace CopilotChatbot;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore = new();
    private readonly ChatSessionStore _chatSessionStore = new();
    private readonly HtmlRenderer _htmlRenderer = new();
    private readonly DebugLogger _debugLogger = new();
    private readonly CopilotChatService _copilot;
    private readonly List<ModelChoice> _models = [];
    private readonly Dictionary<ChatSessionView, long> _renderRevisions = [];
    private readonly Dictionary<ChatSessionView, ChatTabContent> _tabContents = [];
    private AppSettings _settings;
    private bool _isDarkTheme;
    private bool _showDetailMessages;
    private bool _isRestoringChats;
    private System.Windows.Threading.DispatcherTimer? _themeTimer;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        _settings = _settingsStore.Load();
        _debugLogger.IsEnabled = _settings.EnableDebugLogging;
        _copilot = new CopilotChatService(_settingsStore, PromptForPermissionAsync, PromptForUserInputAsync, _debugLogger);
        _copilot.UsageUpdated += Copilot_UsageUpdated;
        _copilot.SessionPendingChanged += Copilot_SessionPendingChanged;
        _copilot.StatusChanged += Copilot_StatusChanged;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void LoadWindowIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
        }
        catch
        {
            // The executable icon is still embedded via ApplicationIcon; window startup should never fail over chrome artwork.
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyThemeFromMode();
        await RefreshModelsAsync(showErrorDialog: false, allowFallback: true);
        await RestoreOpenChatsAsync();
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            await _copilot.DisposeAsync();
        }
        catch
        {
            // App shutdown should not crash if the SDK process already exited.
        }
        finally
        {
            // Force-terminate: .NET EventCounter background threads from the SDK
            // keep the process alive indefinitely after the window closes.
            Environment.Exit(0);
        }
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e) => _ = AddChatAsync();

    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentChat is { } chat)
        {
            chat.Messages.Clear();
            chat.IsPageInitialized = false;
            RenderCurrentChat();
            SaveOpenChats();
        }
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(showErrorDialog: true, allowFallback: false);

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = _settings.Theme switch
        {
            AppThemeMode.Light        => AppThemeMode.Dark,
            AppThemeMode.Dark         => AppThemeMode.System,
            AppThemeMode.System       => AppThemeMode.FollowTheSun,
            AppThemeMode.FollowTheSun => AppThemeMode.Light,
            _                         => AppThemeMode.Light,
        };
        _settingsStore.Save(_settings);
        ApplyThemeFromMode();
    }

    private async void SessionInfoButton_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = await _copilot.GetCapabilitiesSnapshotAsync(CurrentChat);
        var window = new SessionInfoWindow(snapshot, this);
        window.Show();
    }

    private void SessionPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentChat is not { } chat) return;
        var window = new SessionSystemPromptWindow(chat, _settings.DefaultSystemPrompt, this);
        window.PromptApplied += _ => SaveOpenChats();
        window.Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settingsStore, _settings, _debugLogger) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = window.Settings;
            _settingsStore.Save(_settings);
            _debugLogger.IsEnabled = _settings.EnableDebugLogging;
            ApplyThemeFromMode();
        }
    }


    private void ChatTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == ChatTabs)
        {
            RenderCurrentChat();
            SaveOpenChats();
        }
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelComboBox.SelectedItem is not ModelChoice model)
        {
            return;
        }

        _settings.SelectedModelId = model.Id;
        ReasoningComboBox.ItemsSource = model.SupportsReasoningEffort
            ? (model.ReasoningEfforts.Count > 0 ? model.ReasoningEfforts : ["low", "medium", "high", "xhigh"])
            : Array.Empty<string>();
        ReasoningComboBox.IsEnabled = model.SupportsReasoningEffort;
        ReasoningComboBox.SelectedItem = model.DefaultReasoningEffort ?? _settings.SelectedReasoningEffort;
        _settingsStore.Save(_settings);
    }

    private void ReasoningComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.SelectedReasoningEffort = ReasoningComboBox.SelectedItem?.ToString();
        _settingsStore.Save(_settings);
    }

    private async Task SendChatAsync(ChatSessionView chat, string prompt)
    {
        if (chat.IsSessionMissing || string.IsNullOrWhiteSpace(prompt)) return;

        chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.User, Content = prompt });
        RenderChat(chat);
        chat.IsPending = true;
        GetTabContent(chat)?.SetState(true, false);

        try
        {
            var model = ModelComboBox.SelectedItem as ModelChoice;
            await _copilot.SendAsync(chat, prompt, _settings, model, ReasoningComboBox.SelectedItem?.ToString());
            SaveOpenChats();
        }
        catch (Exception ex)
        {
            chat.IsPending = false;
            chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.Error, Content = ex.Message });
            RenderChat(chat);
            GetTabContent(chat)?.SetState(false, chat.IsSessionMissing);
            SaveOpenChats();
        }
    }

    private async Task StopChatAsync(ChatSessionView chat)
    {
        if (!chat.IsPending) return;

        try
        {
            await _copilot.AbortAsync(chat);
            chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.System, Content = "Operation interrupted." });
            RenderChat(chat);
        }
        catch (Exception ex)
        {
            chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.Error, Content = "Failed to interrupt operation.\n\n" + ex.Message });
            RenderChat(chat);
        }
        finally
        {
            chat.IsPending = false;
            GetTabContent(chat)?.SetState(false, chat.IsSessionMissing);
        }
    }

    private ChatTabContent? GetTabContent(ChatSessionView chat) =>
        _tabContents.TryGetValue(chat, out var tc) ? tc : null;

    private async Task RefreshModelsAsync(bool showErrorDialog, bool allowFallback)
    {
        try
        {
            RefreshModelsButton.IsEnabled = false;
            ConnectionStatusTextBlock.Text = "Copilot: connecting...";
            ModelStatusTextBlock.Text = "Models: refreshing from Copilot SDK...";
            _models.Clear();
            _models.AddRange(await _copilot.ListModelsAsync(_settings));
            if (_models.Count == 0)
            {
                if (allowFallback)
                {
                    UseFallbackModels("The Copilot SDK returned an empty model list.");
                }
                else
                {
                    ApplyModelChoices();
                    ModelStatusTextBlock.Text = "Models: SDK returned 0 models";
                }
            }
            else
            {
                ApplyModelChoices();
                ModelStatusTextBlock.Text = $"Models: {_models.Count} loaded from Copilot SDK";
            }

            await RefreshRuntimeStatusAsync();
        }
        catch (Exception ex)
        {
            ConnectionStatusTextBlock.Text = "Copilot: unavailable";
            ModelStatusTextBlock.Text = "Models: refresh failed";
            if (allowFallback)
            {
                UseFallbackModels("Model refresh failed. Using fallback model ids until the SDK can be queried successfully.\n\n" + ex.Message);
            }
            if (showErrorDialog)
            {
                MessageBox.Show(this, ex.Message, "Model refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async Task RefreshRuntimeStatusAsync()
    {
        try
        {
            var status = await _copilot.GetRuntimeStatusAsync(_settings);
            var auth = status.IsAuthenticated
                ? $"auth {status.Login} ({status.AuthType})"
                : $"not authenticated: {status.Message}";
            ConnectionStatusTextBlock.Text = $"Copilot: CLI {status.CliVersion}, protocol {status.ProtocolVersion}, {auth}";
        }
        catch (Exception ex)
        {
            ConnectionStatusTextBlock.Text = "Copilot: status unavailable - " + ex.Message;
        }
    }

    private void Copilot_UsageUpdated(CopilotUsageStatus usage)
    {
        Dispatcher.Invoke(() => UsageStatusTextBlock.Text = "Usage: " + usage.ToStatusText());
    }

    private void Copilot_StatusChanged(ChatSessionView chat, string? status)
    {
        Dispatcher.Invoke(() =>
        {
            chat.LastStatus = status;
            GetTabContent(chat)?.SetStatus(status);
        });
    }

    private void UpdateStatusBar(ChatSessionView? chat)
    {
        if (chat is not null) GetTabContent(chat)?.SetStatus(chat.LastStatus);
    }

    private void Copilot_SessionPendingChanged(ChatSessionView chat, bool isPending)
    {
        Dispatcher.Invoke(() =>
        {
            chat.IsPending = isPending;
            GetTabContent(chat)?.SetState(isPending, chat.IsSessionMissing);
            if (!isPending)
            {
                RenderChat(chat);
                SaveOpenChats();
            }
        });
    }

    private void ApplyModelChoices()
    {
        ModelComboBox.ItemsSource = null;
        ModelComboBox.ItemsSource = _models;
        ModelComboBox.SelectedItem = _models.FirstOrDefault(m => m.Id == _settings.SelectedModelId) ?? _models.FirstOrDefault();
        ModelComboBox.IsEnabled = _models.Count > 0;
    }

    private void UseFallbackModels(string reason)
    {
        _models.Clear();
        _models.AddRange(GetFallbackModels());
        ApplyModelChoices();

        if (CurrentChat is { } chat)
        {
            chat.Messages.Add(new ChatMessage
            {
                Kind = ChatMessageKind.System,
                Content = reason + "\n\nThese fallback entries are only startup defaults; Refresh Models will replace them with the live SDK list once Copilot CLI starts correctly."
            });
        }
    }

    private static IReadOnlyList<ModelChoice> GetFallbackModels() =>
    [
        new() { Id = "gpt-5", Name = "GPT-5", SupportsReasoningEffort = true, ReasoningEfforts = ["low", "medium", "high", "xhigh"], DefaultReasoningEffort = "medium", IsFallback = true },
        new() { Id = "gpt-4.1", Name = "GPT-4.1", IsFallback = true },
        new() { Id = "claude-sonnet-4.5", Name = "Claude Sonnet 4.5", IsFallback = true }
    ];

    private async Task AddChatAsync(PersistedChatSession? persisted = null, bool select = true)
    {
        var chat = CreateChatTabItem(persisted, select);
        await InitializeChatBrowserAsync(chat);
    }

    /// <summary>
    /// Synchronously creates the <see cref="ChatSessionView"/> and its <see cref="TabItem"/> and
    /// adds them to the tab strip.  Does <em>not</em> initialize the embedded browser — call
    /// <see cref="InitializeChatBrowserAsync"/> afterwards.
    /// </summary>
    private ChatSessionView CreateChatTabItem(PersistedChatSession? persisted, bool select)
    {
        var chat = new ChatSessionView(string.IsNullOrWhiteSpace(persisted?.Title) ? $"Chat {ChatTabs.Items.Count + 1}" : persisted!.Title)
        {
            CopilotSessionId = persisted?.CopilotSessionId,
            IsSessionMissing = persisted?.IsSessionMissing == true,
            SystemPrompt = persisted is null
                ? (string.IsNullOrWhiteSpace(_settings.DefaultSystemPrompt) ? null : _settings.DefaultSystemPrompt)
                : persisted.SystemPrompt
        };
        if (persisted is not null)
        {
            foreach (var message in persisted.Messages)
            {
                chat.Messages.Add(new ChatMessage
                {
                    Id = string.IsNullOrWhiteSpace(message.Id) ? Guid.NewGuid().ToString("N") : message.Id,
                    Kind = message.Kind,
                    Content = message.Content,
                    CreatedAt = message.CreatedAt == default ? DateTimeOffset.Now : message.CreatedAt,
                    CompletedAt = message.CompletedAt
                });
            }
        }
        chat.Messages.CollectionChanged += ChatMessages_CollectionChanged;

        var tabContent = new ChatTabContent();
        tabContent.SetBrowser(chat.Browser);
        tabContent.SendRequested += prompt => _ = SendChatAsync(chat, prompt);
        tabContent.StopRequested += () => _ = StopChatAsync(chat);
        _tabContents[chat] = tabContent;
        tabContent.SetState(chat.IsPending, chat.IsSessionMissing);

        var tab = new TabItem { Content = tabContent, Tag = chat };
        SetTabHeader(tab, chat.Title);
        ChatTabs.Items.Add(tab);
        if (select)
        {
            ChatTabs.SelectedItem = tab;
        }
        ChatTabs.UpdateLayout();

        chat.Browser.DefaultBackgroundColor = _isDarkTheme
            ? System.Drawing.Color.FromArgb(255, 17, 24, 39)
            : System.Drawing.Color.White;

        return chat;
    }

    /// <summary>
    /// Initializes the embedded WebView2 browser for <paramref name="chat"/> and performs
    /// the first render.  Safe to call after <see cref="CreateChatTabItem"/>.
    /// </summary>
    private async Task InitializeChatBrowserAsync(ChatSessionView chat)
    {
        try
        {
            await chat.Browser.EnsureCoreWebView2Async();
            chat.Browser.CoreWebView2.WebMessageReceived += Browser_WebMessageReceived;
            RenderChat(chat);
            SaveOpenChats();
        }
        catch (Exception ex)
        {
            chat.Messages.Add(new ChatMessage
            {
                Kind = ChatMessageKind.Error,
                Content = "Embedded browser initialization failed.\n\n" + ex.Message
            });
            SaveOpenChats();
        }
    }

    private async Task RestoreOpenChatsAsync()
    {
        var hadPersistedState = _chatSessionStore.Exists;
        var state = _chatSessionStore.Load();
        if (!hadPersistedState)
        {
            await AddChatAsync();
            return;
        }

        var tabs = new List<TabItem>();
        _isRestoringChats = true;
        try
        {
            // Phase 1: Create all tab shells synchronously so the tab strip is fully
            // populated before any slow browser-initialization awaits begin.
            foreach (var saved in state.Sessions)
            {
                CreateChatTabItem(saved, select: false);
            }
            tabs = ChatTabs.Items.OfType<TabItem>().ToList();

            // Phase 2: Immediately select the first non-missing tab so the user sees
            // the correct tab highlighted before browsers begin initializing.
            ActivateFirstRestoredChat(tabs);

            // Phase 3: Initialize the embedded browser for each tab.  The currently
            // selected tab goes first so its content appears as soon as possible;
            // the remaining tabs are initialized sequentially afterwards.
            var selectedChat = CurrentChat;
            foreach (var tab in tabs.OrderBy(t => ReferenceEquals(t.Tag as ChatSessionView, selectedChat) ? 0 : 1))
            {
                if (tab.Tag is ChatSessionView chat)
                    await InitializeChatBrowserAsync(chat);
            }

            // Phase 4: Resume Copilot sessions now that all browsers are ready.
            foreach (var tab in tabs)
            {
                if (tab.Tag is not ChatSessionView chat || chat.IsSessionMissing)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
                {
                    MarkSessionMissing(chat, "This saved chat does not have a Copilot session id.");
                    continue;
                }

                try
                {
                    var model = ModelComboBox.SelectedItem as ModelChoice;
                    await _copilot.ResumeSessionAsync(chat, _settings, model, ReasoningComboBox.SelectedItem?.ToString());
                }
                catch (Exception ex)
                {
                    MarkSessionMissing(chat, $"Copilot session '{chat.CopilotSessionId}' could not be found or resumed.\n\n{ex.Message}");
                }
            }
        }
        finally
        {
            _isRestoringChats = false;
        }

        // Final activation: only switch away if the selected tab was marked missing during resume.
        if (CurrentChat?.IsSessionMissing == true)
            ActivateFirstRestoredChat(tabs);
        else
        {
            UpdateInputState();
            UpdateStatusBar(CurrentChat);
        }
        SaveOpenChats();
    }

    private void ActivateFirstRestoredChat(IReadOnlyList<TabItem> tabs)
    {
        var startupTab = tabs.FirstOrDefault(tab => (tab.Tag as ChatSessionView)?.IsSessionMissing == false)
            ?? tabs.FirstOrDefault();
        if (startupTab is null)
        {
            UpdateInputState();
            UpdateStatusBar(null);
            return;
        }

        var startupIndex = ChatTabs.Items.IndexOf(startupTab);
        if (startupIndex >= 0)
        {
            // Setting SelectedIndex fires ChatTabs_SelectionChanged which renders the tab and
            // updates status. If the index is unchanged (tab was already auto-selected by WPF
            // when it was first added), SelectionChanged does not fire, so call the updates
            // explicitly below to ensure the correct enabled/disabled input state is applied
            // after all sessions have finished resuming or been marked missing.
            ChatTabs.SelectedIndex = startupIndex;
            startupTab.BringIntoView();
            ChatTabs.UpdateLayout();
        }

        UpdateInputState();
        UpdateStatusBar(CurrentChat);
    }

    private void MarkSessionMissing(ChatSessionView chat, string reason)
    {
        chat.IsSessionMissing = true;
        chat.IsPending = false;
        chat.LastStatus = null;
        chat.Messages.Add(new ChatMessage
        {
            Kind = ChatMessageKind.System,
            Content = reason + "\n\nThis restored chat is read-only. Close it to remove it from startup, or start a new chat."
        });
        RenderChat(chat);
        GetTabContent(chat)?.SetState(false, true);
    }

    private void SaveOpenChats()
    {
        if (_isRestoringChats)
        {
            return;
        }

        var sessions = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .Where(chat => chat is not null)
            .Select(chat => ToPersistedSession(chat!))
            .ToList();
        _chatSessionStore.Save(new PersistedChatState
        {
            Sessions = sessions,
            SelectedSessionId = CurrentChat?.CopilotSessionId
        });
    }

    private static PersistedChatSession ToPersistedSession(ChatSessionView chat) =>
        new()
        {
            Title = chat.Title,
            CopilotSessionId = chat.CopilotSessionId,
            SystemPrompt = chat.SystemPrompt,
            IsSessionMissing = chat.IsSessionMissing,
            Messages = chat.Messages.Select(message => new PersistedChatMessage
            {
                Id = message.Id,
                Kind = message.Kind,
                Content = message.Content,
                CreatedAt = message.CreatedAt,
                CompletedAt = message.CompletedAt
            }).ToList()
        };

    private void SetTabHeader(TabItem tab, string title)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            MinWidth = 128,
            MaxWidth = 178,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new Button
        {
            Content = "x",
            Style = (Style)FindResource("CloseTabButton"),
            ToolTip = "Close session"
        };
        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            _ = CloseTabAsync(tab);
        };

        tab.Header = new DockPanel
        {
            LastChildFill = false,
            ToolTip = "Double-click to rename",
            MinWidth = 172,
            Children =
            {
                titleBlock,
                closeButton
            }
        };
        tab.MouseDoubleClick -= Tab_MouseDoubleClick;
        tab.MouseDoubleClick += Tab_MouseDoubleClick;
        tab.ContextMenu = BuildTabContextMenu(tab);
    }

    private ContextMenu BuildTabContextMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => RenameTab(tab);
        menu.Items.Add(renameItem);
        var closeItem = new MenuItem { Header = "Close" };
        closeItem.Click += (_, _) => _ = CloseTabAsync(tab);
        menu.Items.Add(closeItem);
        return menu;
    }

    private async Task CloseTabAsync(TabItem tab)
    {
        // Remove from UI immediately — avoids showing an empty tab while the session closes
        ChatTabs.Items.Remove(tab);

        // Close the backing session after the UI is already updated
        if (tab.Tag is ChatSessionView chat)
        {
            _renderRevisions.Remove(chat);
            if (_tabContents.TryGetValue(chat, out var tabContent))
            {
                tabContent.Cleanup();
                _tabContents.Remove(chat);
            }
            try { await _copilot.CloseSessionAsync(chat); } catch { /* ignore on close */ }
        }
        SaveOpenChats();
        UpdateInputState();
        UpdateStatusBar(CurrentChat);
    }

    private void Tab_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TabItem tab)
        {
            e.Handled = true;
            RenameTab(tab);
        }
    }

    private void RenameTab(TabItem tab)
    {
        if (tab.Tag is not ChatSessionView chat)
        {
            return;
        }

        var dialog = new RenameTabWindow(chat.Title) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            chat.Title = dialog.TabTitle;
            SetTabHeader(tab, chat.Title);
            SaveOpenChats();
        }
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ChatTabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals((t.Tag as ChatSessionView)?.Messages, sender))?.Tag is ChatSessionView chat)
        {
            RenderChat(chat);
            SaveOpenChats();
        }
    }

    private void Browser_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var id = TryReadOpenMessageId(e);
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var sourceChat = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .FirstOrDefault(chat => ReferenceEquals(chat?.Browser.CoreWebView2, sender));
        var message = (sourceChat ?? CurrentChat)?.Messages.FirstOrDefault(m => m.Id == id);
        if (message is not null)
        {
            new ResponseWindow(_htmlRenderer, message, _isDarkTheme) { Owner = this }.Show();
        }
    }

    private static string? TryReadOpenMessageId(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var type) &&
                type.GetString()?.Equals("open", StringComparison.OrdinalIgnoreCase) == true &&
                root.TryGetProperty("id", out var id))
            {
                return id.GetString();
            }

            return root.ValueKind == JsonValueKind.String ? root.GetString() : null;
        }
        catch
        {
            try
            {
                return e.TryGetWebMessageAsString();
            }
            catch
            {
                return null;
            }
        }
    }

    private Task<PermissionPromptDecision> PromptForPermissionAsync(PermissionPrompt prompt)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            var window = new PermissionPromptWindow(prompt) { Owner = this };
            return window.ShowDialog() == true ? window.Decision : PermissionPromptDecision.Deny;
        }).Task;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Task<UserInputPromptResult> PromptForUserInputAsync(UserInputPrompt prompt)
    {
        // Use TaskCompletionSource so the background caller can await the result
        // without any dependency on WPF's DispatcherOperation.Task edge cases.
        var tcs = new TaskCompletionSource<UserInputPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var window = new UserInputPromptWindow(prompt);
                // No Owner: WebView2's out-of-process HWND interferes with owned-window z-order.
                // Topmost="True" is set in XAML; SetForegroundWindow forces keyboard focus.
                window.Loaded += (_, _) =>
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(window);
                    SetForegroundWindow(helper.Handle);
                };
                bool? result = window.ShowDialog();
                tcs.SetResult(result == true
                    ? new UserInputPromptResult(window.Answer, window.WasFreeform)
                    : new UserInputPromptResult("", true));
            }
            catch (Exception ex)
            {
                _debugLogger.Log("PROMPT-DIALOG-ERROR", ex.ToString());
                tcs.SetResult(new UserInputPromptResult("", true));
            }
        });
        return tcs.Task;
    }

    private ChatSessionView? CurrentChat => (ChatTabs.SelectedItem as TabItem)?.Tag as ChatSessionView;

    private void UpdateInputState()
    {
        if (CurrentChat is { } chat)
            GetTabContent(chat)?.SetState(chat.IsPending, chat.IsSessionMissing);
    }

    private void RenderCurrentChat()
    {
        if (CurrentChat is { } chat)
        {
            RenderChat(chat);
        }
    }

    private void RenderChat(ChatSessionView chat)
    {
        var revision = NextRenderRevision(chat);
        _ = RenderChatAsync(chat, revision);
    }

    private static readonly IReadOnlySet<ChatMessageKind> DetailMessageKinds = new HashSet<ChatMessageKind>
    {
        ChatMessageKind.Reasoning,
        ChatMessageKind.Tool,
        ChatMessageKind.Intent
    };

    private IEnumerable<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages) =>
        _showDetailMessages ? messages : messages.Where(m => !DetailMessageKinds.Contains(m.Kind));

    private async Task RenderChatAsync(ChatSessionView chat, long revision)
    {
        if (chat.Browser.CoreWebView2 is null)
        {
            return;
        }
        if (!IsLatestRender(chat, revision))
        {
            return;
        }

        var messages = FilterMessages(chat.Messages);

        if (!chat.IsPageInitialized)
        {
            chat.IsPageInitialized = true;
            chat.Browser.NavigateToString(_htmlRenderer.RenderDocument(messages, _isDarkTheme));
            return;
        }

        // Incremental update: patch message nodes in-place (append/update/reorder/remove)
        // and preserve the scroll position.
        try
        {
            var payload = messages.Select(m => new BrowserMessagePatch(
                m.Id,
                BuildRenderSignature(m),
                _htmlRenderer.RenderMessageFragment(m, _isDarkTheme))).ToArray();
            var jsonPatch = System.Text.Json.JsonSerializer.Serialize(payload);
            if (!IsLatestRender(chat, revision))
            {
                return;
            }
            await chat.Browser.ExecuteScriptAsync($$"""
                (function() {
                    var el = document.documentElement;
                    var atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 40;
                    var savedY = el.scrollTop;
                    var m = document.querySelector('main');
                    if (!m) return;

                    var patches = {{jsonPatch}};
                    var desired = new Set();

                    function upsertNode(patch) {
                        var id = patch.Id;
                        desired.add(id);
                        var selector = 'article[data-mid=\"' + CSS.escape(id) + '\"]';
                        var node = m.querySelector(selector);
                        var oldOpen = null;
                        if (node) {
                            var oldDetails = node.querySelector('details');
                            oldOpen = oldDetails ? oldDetails.open : null;
                        }

                        if (!node) {
                            var tpl = document.createElement('template');
                            tpl.innerHTML = patch.Html;
                            node = tpl.content.firstElementChild;
                            if (!node) return null;
                            node.dataset.renderSig = patch.Signature;
                            m.appendChild(node);
                        } else if (node.dataset.renderSig !== patch.Signature) {
                            var repl = document.createElement('template');
                            repl.innerHTML = patch.Html;
                            var next = repl.content.firstElementChild;
                            if (!next) return node;
                            var curDetails = node.querySelector('details');
                            var nextDetails = next.querySelector('details');

                            // For streaming updates, patch in-place so only the active
                            // response body changes and iframe loader stays local.
                            if (curDetails && nextDetails) {
                                node.className = next.className;
                                node.id = next.id;
                                node.dataset.mid = next.dataset.mid || id;
                                node.dataset.renderSig = patch.Signature;

                                var curSummary = curDetails.querySelector('summary.head');
                                var nextSummary = nextDetails.querySelector('summary.head');
                                if (curSummary && nextSummary) {
                                    curSummary.innerHTML = nextSummary.innerHTML;
                                }

                                var curBody = curDetails.querySelector('.frame-body');
                                var nextBody = nextDetails.querySelector('.frame-body');
                                if (curBody && nextBody) {
                                    curBody.innerHTML = nextBody.innerHTML;
                                }

                                if (oldOpen !== null) {
                                    curDetails.open = oldOpen;
                                }
                            } else {
                                next.dataset.renderSig = patch.Signature;
                                node.replaceWith(next);
                                node = next;
                                if (oldOpen !== null) {
                                    var replacedDetails = node.querySelector('details');
                                    if (replacedDetails) replacedDetails.open = oldOpen;
                                }
                            }
                        }
                        return node;
                    }

                    patches.forEach(function(patch, idx) {
                        var node = upsertNode(patch);
                        if (!node) return;
                        var expected = m.children[idx] || null;
                        if (node !== expected) {
                            m.insertBefore(node, expected);
                        }
                    });

                    Array.from(m.querySelectorAll('article[data-mid]')).forEach(function(node) {
                        if (!desired.has(node.dataset.mid)) {
                            node.remove();
                        }
                    });

                    document.querySelectorAll('iframe').forEach(f => {
                        try { f.style.height = Math.max(40, f.contentWindow.document.documentElement.scrollHeight + 10) + 'px'; } catch {}
                    });
                    el.scrollTop = atBottom ? el.scrollHeight : savedY;
                })()
            """);
        }
        catch
        {
            // Swallow: CoreWebView2 may not be ready during rapid streaming
        }
    }

    private long NextRenderRevision(ChatSessionView chat)
    {
        var next = _renderRevisions.GetValueOrDefault(chat) + 1;
        _renderRevisions[chat] = next;
        return next;
    }

    private bool IsLatestRender(ChatSessionView chat, long revision) =>
        _renderRevisions.GetValueOrDefault(chat) == revision;

    private static string BuildRenderSignature(ChatMessage message)
    {
        var completed = message.CompletedAt?.ToUnixTimeMilliseconds().ToString() ?? "";
        return string.Join("|",
            message.Kind.ToString(),
            message.Content,
            message.CreatedAt.ToUnixTimeMilliseconds().ToString(),
            completed);
    }

    private sealed record BrowserMessagePatch(string Id, string Signature, string Html);

    private void ShowDetailsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _showDetailMessages = ShowDetailsCheckBox.IsChecked == true;
        RenderCurrentChat();
    }

    private void ApplyThemeFromMode()
    {
        bool dark = _settings.Theme switch
        {
            AppThemeMode.Light        => false,
            AppThemeMode.Dark         => true,
            AppThemeMode.System       => IsSystemDark(),
            AppThemeMode.FollowTheSun => IsFollowTheSunDark(),
            _                      => false,
        };

        if (_settings.Theme == AppThemeMode.FollowTheSun)
            StartFollowTheSunTimer();
        else
            StopThemeTimer();

        ApplyTheme(dark);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        var (symbol, tooltip) = _settings.Theme switch
        {
            AppThemeMode.Light        => (SymbolRegular.WeatherSunny20,  "Theme: Light — click for Dark"),
            AppThemeMode.Dark         => (SymbolRegular.WeatherMoon20,   "Theme: Dark — click for System"),
            AppThemeMode.System       => (SymbolRegular.Desktop20,       "Theme: System — click for Follow the Sun"),
            AppThemeMode.FollowTheSun => (SymbolRegular.Clock20,         "Theme: Follow the Sun — click for Light"),
            _                         => (SymbolRegular.WeatherSunny20,  "Theme: Light"),
        };
        ThemeIcon.Symbol = symbol;
        ThemeButton.ToolTip = tooltip;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static bool IsFollowTheSunDark()
    {
        var hour = DateTime.Now.Hour;
        return hour < 7 || hour >= 19;
    }

    private void StartFollowTheSunTimer()
    {
        if (_themeTimer is not null) return;
        _themeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _themeTimer.Tick += (_, _) =>
        {
            if (_settings.Theme == AppThemeMode.FollowTheSun)
                ApplyTheme(IsFollowTheSunDark());
        };
        _themeTimer.Start();
    }

    private void StopThemeTimer()
    {
        _themeTimer?.Stop();
        _themeTimer = null;
    }

    private void ApplyTheme(bool dark)
    {
        _isDarkTheme = dark;
        SwapWpfUiTheme(dark);
        SetBrush("SurfaceBrush", dark ? "#111827" : "#FFFFFF");
        SetBrush("PanelBrush", dark ? "#172033" : "#FAFBFC");
        SetBrush("PageBrush", dark ? "#0B1220" : "#F3F5F8");
        SetBrush("ControlBrush", dark ? "#1D293D" : "#F8FAFC");
        SetBrush("ControlHoverBrush", dark ? "#26344D" : "#FFFFFF");
        SetBrush("BorderBrushModern", dark ? "#344054" : "#D0D7DE");
        SetBrush("TextBrush", dark ? "#F8FAFC" : "#1F2328");
        SetBrush("MutedTextBrush", dark ? "#B8C2CC" : "#5D6673");
        SetBrush("AccentBrush", dark ? "#6CB6FF" : "#0A65CC");
        SetBrush("AccentSoftBrush", dark ? "#132B4D" : "#E7F1FF");
        SetBrush("DisabledControlBrush", dark ? "#263244" : "#E5E7EB");
        SetBrush("DisabledBorderBrush", dark ? "#39465A" : "#D1D5DB");
        SetBrush("DisabledTextBrush", dark ? "#7F8A99" : "#8A94A3");
        // Update WebView2 default background colour for chrome rendered before content,
        // then inject the CSS variable update script so all open tabs repaint instantly
        // without a full reload (scroll position and <details> open state are preserved).
        var bgColor = dark
            ? System.Drawing.Color.FromArgb(255, 17, 24, 39)
            : System.Drawing.Color.White;
        var themeScript = _htmlRenderer.GetThemeUpdateScript(dark);
        foreach (var tab in ChatTabs.Items.OfType<TabItem>())
        {
            if (tab.Tag is ChatSessionView c)
            {
                c.Browser.DefaultBackgroundColor = bgColor;
                if (c.IsPageInitialized)
                    _ = c.Browser.ExecuteScriptAsync(themeScript);
            }
        }
    }

    private static void SwapWpfUiTheme(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("/Resources/Theme/", StringComparison.OrdinalIgnoreCase) == true);
        if (theme is not null)
        {
            theme.Source = new Uri(
                $"pack://application:,,,/Wpf.Ui;component/Resources/Theme/{(dark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute);
        }
    }

    private void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Resources[key] = brush;
        Application.Current.Resources[key] = brush;
    }
}
