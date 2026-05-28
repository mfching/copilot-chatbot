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
    private readonly ILocalShortcutService _localShortcutService;
    private readonly List<ModelChoice> _models = [];
    private readonly Dictionary<ChatSessionView, long> _renderRevisions = [];
    private readonly Dictionary<ChatSessionView, ChatTabContent> _tabContents = [];
    private readonly Dictionary<ChatSessionView, Task> _browserInitializationTasks = [];
    private readonly Dictionary<ChatSessionView, Task> _sessionResumeTasks = [];
    private readonly HashSet<ChatSessionView> _resumedSessions = [];
    private AppSettings _settings;
    private bool _isDarkTheme;
    private bool _showDetailMessages;
    private bool _isRestoringChats;
    private System.Windows.Threading.DispatcherTimer? _themeTimer;
    private readonly object _openChatSaveGate = new();
    private readonly object _openChatWriteGate = new();
    private CancellationTokenSource? _pendingOpenChatSave;
    private Task _lastOpenChatSaveTask = Task.CompletedTask;
    private static readonly TimeSpan OpenChatSaveIdleDelay = TimeSpan.FromMilliseconds(900);

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        _settings = LoadSettingsForStartup();
        _debugLogger.IsEnabled = _settings.EnableDebugLogging;
        _copilot = new CopilotChatService(_settingsStore, PromptForPermissionAsync, PromptForUserInputAsync, _debugLogger);
        _copilot.UsageUpdated += Copilot_UsageUpdated;
        _copilot.SessionPendingChanged += Copilot_SessionPendingChanged;
        _copilot.StatusChanged += Copilot_StatusChanged;
        _localShortcutService = new LocalShortcutService(_copilot, _settingsStore);
        _localShortcutService.StatusChanged += LocalShortcut_StatusChanged;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private AppSettings LoadSettingsForStartup()
    {
        if (!_settingsStore.RequiresSettingsPassword())
        {
            return _settingsStore.Load();
        }

        var passwordWindow = new SettingsPasswordWindow();
        if (passwordWindow.ShowDialog() == true)
        {
            try
            {
                _settingsStore.SetSettingsPassword(passwordWindow.Password);
                return _settingsStore.Load();
            }
            catch (SettingsDecryptionException)
            {
                MessageBox.Show(
                    this,
                    "The settings password was incorrect. The application will start with blank settings for this session.",
                    "Settings password",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        else
        {
            MessageBox.Show(
                this,
                "Settings were not unlocked. The application will start with blank settings for this session.",
                "Settings password",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _settingsStore.UseBlankSettingsForSession();
        return new AppSettings();
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
        UpdateMemoryCheckBox();
        await RefreshModelsAsync(showErrorDialog: false, allowFallback: true);
        await RestoreOpenChatsAsync();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveOpenChatsSafely(force: true, reason: "closing");
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            SaveOpenChatsSafely(force: true, reason: "closed");
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
            UpdateMemoryCheckBox();
            ApplyThemeFromMode();
        }
    }


    private void ChatTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == ChatTabs)
        {
            if (CurrentChat is { } chat)
            {
                SetTabUnreadState(chat, false);
                _ = EnsureSelectedChatReadyAsync(chat);
            }
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

        var promptToSend = prompt;
        var userMessageContent = prompt;
        if (await TryHandleLocalShortcutAsync(chat, prompt) is { } shortcutResult)
        {
            if (string.IsNullOrWhiteSpace(shortcutResult.PromptToSend))
            {
                AddLocalShortcutMessage(chat, shortcutResult.Kind, shortcutResult.Content);
                return;
            }

            promptToSend = shortcutResult.PromptToSend;
            userMessageContent = shortcutResult.UserVisiblePrompt ?? prompt;
        }

        chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.User, Content = userMessageContent });
        RenderChat(chat);
        chat.IsPending = true;
        SetTabBusyIndicator(chat, true);
        GetTabContent(chat)?.SetState(true, false);

        try
        {
            var model = ModelComboBox.SelectedItem as ModelChoice;
            await _copilot.SendAsync(chat, promptToSend, _settings, model, ReasoningComboBox.SelectedItem?.ToString());
            SaveOpenChats();
        }
        catch (Exception ex)
        {
            chat.IsPending = false;
            SetTabBusyIndicator(chat, false);
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
            SetTabBusyIndicator(chat, false);
            GetTabContent(chat)?.SetState(false, chat.IsSessionMissing);
            SaveOpenChats();
        }
    }

    private ChatTabContent? GetTabContent(ChatSessionView chat) =>
        _tabContents.TryGetValue(chat, out var tc) ? tc : null;

    private async Task<LocalShortcutResult?> TryHandleLocalShortcutAsync(ChatSessionView chat, string prompt)
    {
        var result = await _localShortcutService.TryExecuteAsync(chat, prompt);
        if (result is null)
        {
            return null;
        }

        return result;
    }

    private void LocalShortcut_StatusChanged(ChatSessionView chat, string? status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (status is null)
            {
                UpdateStatusBar(chat);
            }
            else
            {
                GetTabContent(chat)?.SetStatus(status);
            }
        });
    }

    private void AddLocalShortcutMessage(ChatSessionView chat, ChatMessageKind kind, string content)
    {
        chat.Messages.Add(new ChatMessage
        {
            Kind = kind,
            Content = content
        });
        RenderChat(chat);
        SaveOpenChats();
    }

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
        Dispatcher.BeginInvoke(() => UsageStatusTextBlock.Text = "Usage: " + usage.ToStatusText());
    }

    private void Copilot_StatusChanged(ChatSessionView chat, string? status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            chat.LastStatus = status;
            GetTabContent(chat)?.SetStatus(status);
            SetTabBusyIndicator(chat, chat.IsPending);
        });
    }

    private void UpdateStatusBar(ChatSessionView? chat)
    {
        if (chat is not null) GetTabContent(chat)?.SetStatus(chat.LastStatus);
    }

    private void Copilot_SessionPendingChanged(ChatSessionView chat, bool isPending)
    {
        Dispatcher.BeginInvoke(() =>
        {
            chat.IsPending = isPending;
            SetTabBusyIndicator(chat, isPending);
            GetTabContent(chat)?.SetState(isPending, chat.IsSessionMissing);
            if (!isPending)
            {
                SetTabUnreadState(chat, !ReferenceEquals(chat, CurrentChat));
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
        await EnsureChatBrowserInitializedAsync(chat);
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
        if (persisted is not null)
        {
            tabContent.SetLoading(true);
        }
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
    private Task EnsureChatBrowserInitializedAsync(ChatSessionView chat)
    {
        if (chat.Browser.CoreWebView2 is not null)
        {
            if (!chat.IsPageInitialized)
            {
                RenderChat(chat);
            }

            GetTabContent(chat)?.SetLoading(false);
            return Task.CompletedTask;
        }

        if (_browserInitializationTasks.TryGetValue(chat, out var existing))
        {
            return existing;
        }

        var task = InitializeChatBrowserAsync(chat);
        _browserInitializationTasks[chat] = task;
        return task;
    }

    private async Task InitializeChatBrowserAsync(ChatSessionView chat)
    {
        try
        {
            GetTabContent(chat)?.SetLoading(true);
            await chat.Browser.EnsureCoreWebView2Async();
            chat.Browser.CoreWebView2.WebMessageReceived += Browser_WebMessageReceived;
            chat.IsPageInitialized = false;
            RenderChat(chat);
            GetTabContent(chat)?.SetLoading(false);
            SaveOpenChats();
        }
        catch (Exception ex)
        {
            GetTabContent(chat)?.SetLoading(false);
            chat.Messages.Add(new ChatMessage
            {
                Kind = ChatMessageKind.Error,
                Content = "Embedded browser initialization failed.\n\n" + ex.Message
            });
            SaveOpenChats();
        }
        finally
        {
            _browserInitializationTasks.Remove(chat);
        }
    }

    private async Task EnsureSelectedChatReadyAsync(ChatSessionView chat)
    {
        await EnsureChatBrowserInitializedAsync(chat);
        RenderChat(chat);
        await EnsureCopilotSessionResumedAsync(chat);
    }

    private Task EnsureCopilotSessionResumedAsync(ChatSessionView chat)
    {
        if (chat.IsSessionMissing || string.IsNullOrWhiteSpace(chat.CopilotSessionId) || _resumedSessions.Contains(chat))
        {
            return Task.CompletedTask;
        }

        if (_sessionResumeTasks.TryGetValue(chat, out var existing))
        {
            return existing;
        }

        var task = ResumeCopilotSessionAsync(chat);
        _sessionResumeTasks[chat] = task;
        return task;
    }

    private async Task ResumeCopilotSessionAsync(ChatSessionView chat)
    {
        try
        {
            GetTabContent(chat)?.SetLoading(true, "Restoring Copilot session...");
            var model = ModelComboBox.SelectedItem as ModelChoice;
            await _copilot.ResumeSessionAsync(chat, _settings, model, ReasoningComboBox.SelectedItem?.ToString());
            _resumedSessions.Add(chat);
        }
        catch (Exception ex)
        {
            MarkSessionMissing(chat, $"Copilot session '{chat.CopilotSessionId}' could not be found or resumed.\n\n{ex.Message}");
        }
        finally
        {
            GetTabContent(chat)?.SetLoading(false);
            _sessionResumeTasks.Remove(chat);
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

            if (CurrentChat is { } selectedChat)
            {
                await EnsureSelectedChatReadyAsync(selectedChat);
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
        SetTabBusyIndicator(chat, false);
        chat.LastStatus = null;
        chat.Messages.Add(new ChatMessage
        {
            Kind = ChatMessageKind.System,
            Content = reason + "\n\nThis restored chat is read-only. Close it to remove it from startup, or start a new chat."
        });
        RenderChat(chat);
        GetTabContent(chat)?.SetState(false, true);
    }

    private void SaveOpenChats(bool force = false, string reason = "autosave")
    {
        if (_isRestoringChats && !force)
        {
            _debugLogger.Log("CHAT-SESSION-SAVE", $"Skipped while restoring | reason={reason}");
            return;
        }

        var state = CaptureOpenChatState();
        QueueOpenChatSave(state, force, reason);
    }

    private PersistedChatState CaptureOpenChatState()
    {
        var sessions = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .Where(chat => chat is not null)
            .Select(chat => ToPersistedSession(chat!))
            .ToList();

        return new PersistedChatState
        {
            Sessions = sessions,
            SelectedSessionId = CurrentChat?.CopilotSessionId
        };
    }

    private void QueueOpenChatSave(PersistedChatState state, bool force, string reason)
    {
        CancellationTokenSource? previous;
        lock (_openChatSaveGate)
        {
            previous = _pendingOpenChatSave;
            _pendingOpenChatSave = force ? null : new CancellationTokenSource();
        }

        previous?.Cancel();
        previous?.Dispose();

        if (force)
        {
            SaveOpenChatStateSynchronously(state, reason, force);
            lock (_openChatSaveGate)
            {
                _lastOpenChatSaveTask = Task.CompletedTask;
            }
            return;
        }

        CancellationTokenSource current;
        lock (_openChatSaveGate)
        {
            current = _pendingOpenChatSave!;
        }

        var saveTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(OpenChatSaveIdleDelay, current.Token);
                await SaveOpenChatStateInBackgroundAsync(state, reason, force, current.Token);
            }
            catch (OperationCanceledException)
            {
                _debugLogger.Log("CHAT-SESSION-SAVE", $"Debounced pending save | reason={reason}");
            }
            catch (Exception ex)
            {
                _debugLogger.Log("CHAT-SESSION-SAVE-ERROR", $"reason={reason} force={force} path={_chatSessionStore.StatePath}\n{ex}");
            }
            finally
            {
                lock (_openChatSaveGate)
                {
                    if (ReferenceEquals(_pendingOpenChatSave, current))
                    {
                        _pendingOpenChatSave = null;
                    }
                }

                current.Dispose();
            }
        }, CancellationToken.None);

        lock (_openChatSaveGate)
        {
            _lastOpenChatSaveTask = saveTask;
        }
    }

    private async Task SaveOpenChatStateInBackgroundAsync(
        PersistedChatState state,
        string reason,
        bool force,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_openChatWriteGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _chatSessionStore.Save(state);
            }
        }, cancellationToken);
        LogOpenChatSave(state, reason, force);
    }

    private void SaveOpenChatStateSynchronously(PersistedChatState state, string reason, bool force)
    {
        lock (_openChatWriteGate)
        {
            _chatSessionStore.Save(state);
        }

        LogOpenChatSave(state, reason, force);
    }

    private void LogOpenChatSave(PersistedChatState state, string reason, bool force)
    {
        _debugLogger.Log(
            "CHAT-SESSION-SAVE",
            $"Saved {state.Sessions.Count} sessions, {state.Sessions.Sum(session => session.Messages.Count)} messages | reason={reason} force={force} path={_chatSessionStore.StatePath}");
    }

    private void SaveOpenChatsSafely(bool force = false, string reason = "autosave")
    {
        try
        {
            SaveOpenChats(force, reason);
        }
        catch (Exception ex)
        {
            _debugLogger.Log("CHAT-SESSION-SAVE-ERROR", $"reason={reason} force={force} path={_chatSessionStore.StatePath}\n{ex}");
        }
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
            Name = "TabTitleTextBlock",
            Text = title,
            FontWeight = tab.Tag is ChatSessionView { HasUnreadResponse: true } ? FontWeights.Bold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleBlock, 0);

        var unreadIndicator = new Ellipse
        {
            Name = "TabUnreadIndicator",
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource("AccentBrush"),
            Visibility = tab.Tag is ChatSessionView { HasUnreadResponse: true }
                ? Visibility.Visible
                : Visibility.Collapsed,
            ToolTip = "Unread response"
        };

        var closeButton = new Button
        {
            Name = "TabCloseButton",
            Content = "x",
            Style = (Style)FindResource("CloseTabButton"),
            ToolTip = "Close session"
        };
        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            _ = CloseTabAsync(tab);
        };

        var busySpinner = CreateTabBusySpinner();
        var typingIndicator = CreateTabTypingIndicator();
        var isPending = tab.Tag is ChatSessionView { IsPending: true };
        var isTyping = tab.Tag is ChatSessionView chat && IsTypingStatus(chat.LastStatus);
        busySpinner.Visibility = isPending && !isTyping ? Visibility.Visible : Visibility.Collapsed;
        typingIndicator.Visibility = isPending && isTyping ? Visibility.Visible : Visibility.Collapsed;
        closeButton.Visibility = isPending ? Visibility.Collapsed : Visibility.Visible;
        closeButton.IsEnabled = !isPending;

        var closeSlot = new Grid
        {
            Name = "TabCloseSlot",
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                typingIndicator,
                busySpinner,
                closeButton
            }
        };
        Grid.SetColumn(unreadIndicator, 1);
        Grid.SetColumn(closeSlot, 2);

        var header = new Grid
        {
            ToolTip = "Double-click to rename",
            Width = 184,
            Children =
            {
                titleBlock,
                unreadIndicator,
                closeSlot
            }
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tab.Header = header;
        tab.MouseDoubleClick -= Tab_MouseDoubleClick;
        tab.MouseDoubleClick += Tab_MouseDoubleClick;
        tab.ContextMenu = BuildTabContextMenu(tab);
    }

    private FrameworkElement CreateTabBusySpinner()
    {
        var rotate = new RotateTransform(0, 9, 9);
        var spinner = new Path
        {
            Name = "TabBusySpinner",
            Width = 18,
            Height = 18,
            Margin = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Data = Geometry.Parse("M 9,1 A 8,8 0 1 1 3.34,3.34"),
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotate,
            ToolTip = "Session is busy"
        };

        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromMilliseconds(850),
            RepeatBehavior = RepeatBehavior.Forever
        });
        return spinner;
    }

    private StackPanel CreateTabTypingIndicator()
    {
        var panel = new StackPanel
        {
            Name = "TabTypingIndicator",
            Orientation = Orientation.Horizontal,
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = "Writing response",
            Visibility = Visibility.Collapsed
        };

        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(i == 0 ? 2 : 1.5, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource("AccentBrush"),
                Opacity = 0.35
            };

            dot.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.35,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(420),
                AutoReverse = true,
                BeginTime = TimeSpan.FromMilliseconds(i * 140),
                RepeatBehavior = RepeatBehavior.Forever
            });
            panel.Children.Add(dot);
        }

        return panel;
    }

    private static bool IsTypingStatus(string? status)
    {
        return status?.Contains("Writing response", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void SetTabBusyIndicator(ChatSessionView chat, bool isBusy)
    {
        var tab = ChatTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, chat));
        if (tab?.Header is not Panel header)
        {
            return;
        }

        var closeSlot = header.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.Name == "TabCloseSlot");
        var typingIndicator = closeSlot?.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Name == "TabTypingIndicator");
        var spinner = closeSlot?.Children
            .OfType<Path>()
            .FirstOrDefault(path => path.Name == "TabBusySpinner");
        var closeButton = closeSlot?.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "TabCloseButton");
        var isTyping = isBusy && IsTypingStatus(chat.LastStatus);

        if (typingIndicator is not null)
        {
            typingIndicator.Visibility = isTyping ? Visibility.Visible : Visibility.Collapsed;
        }

        if (spinner is not null)
        {
            spinner.Visibility = isBusy && !isTyping ? Visibility.Visible : Visibility.Collapsed;
        }

        if (closeButton is not null)
        {
            closeButton.Visibility = isBusy ? Visibility.Collapsed : Visibility.Visible;
            closeButton.IsEnabled = !isBusy;
        }

        if (tab.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "CloseTabMenuItem") is { } closeItem)
        {
            closeItem.IsEnabled = !isBusy;
        }
    }

    private void SetTabUnreadState(ChatSessionView chat, bool hasUnreadResponse)
    {
        chat.HasUnreadResponse = hasUnreadResponse;
        var tab = ChatTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, chat));
        if (tab?.Header is not Panel header)
        {
            return;
        }

        var title = header.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => text.Name == "TabTitleTextBlock");
        if (title is not null)
        {
            title.FontWeight = hasUnreadResponse ? FontWeights.Bold : FontWeights.Normal;
        }

        var indicator = header.Children
            .OfType<Ellipse>()
            .FirstOrDefault(ellipse => ellipse.Name == "TabUnreadIndicator");
        if (indicator is not null)
        {
            indicator.Visibility = hasUnreadResponse ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ContextMenu BuildTabContextMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => RenameTab(tab);
        menu.Items.Add(renameItem);
        var closeItem = new MenuItem { Header = "Close" };
        closeItem.Name = "CloseTabMenuItem";
        closeItem.IsEnabled = tab.Tag is not ChatSessionView { IsPending: true };
        closeItem.Click += (_, _) => _ = CloseTabAsync(tab);
        menu.Items.Add(closeItem);
        return menu;
    }

    private async Task CloseTabAsync(TabItem tab)
    {
        if (tab.Tag is ChatSessionView { IsPending: true })
        {
            return;
        }

        // Remove from UI immediately — avoids showing an empty tab while the session closes
        ChatTabs.Items.Remove(tab);

        // Close the backing session after the UI is already updated
        if (tab.Tag is ChatSessionView chat)
        {
            _renderRevisions.Remove(chat);
            _browserInitializationTasks.Remove(chat);
            _sessionResumeTasks.Remove(chat);
            _resumedSessions.Remove(chat);
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
        var message = TryReadBrowserMessage(e);
        if (message is null)
        {
            return;
        }

        if (message.Type.Equals("openFrame", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(message.Html))
            {
                new IframePreviewWindow(message.Html, _isDarkTheme) { Owner = this }.Show();
            }

            return;
        }

        var id = message.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var sourceChat = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .FirstOrDefault(chat => ReferenceEquals(chat?.Browser.CoreWebView2, sender));
        var chatMessage = (sourceChat ?? CurrentChat)?.Messages.FirstOrDefault(m => m.Id == id);
        if (chatMessage is not null)
        {
            new ResponseWindow(_htmlRenderer, chatMessage, _isDarkTheme) { Owner = this }.Show();
        }
    }

    private static BrowserBridgeMessage? TryReadBrowserMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type))
            {
                var typeValue = type.GetString() ?? "";
                var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var html = root.TryGetProperty("html", out var htmlProp) ? htmlProp.GetString() : null;
                return new BrowserBridgeMessage(typeValue, id, html);
            }

            return root.ValueKind == JsonValueKind.String
                ? new BrowserBridgeMessage("open", root.GetString(), null)
                : null;
        }
        catch
        {
            try
            {
                return new BrowserBridgeMessage("open", e.TryGetWebMessageAsString(), null);
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed record BrowserBridgeMessage(string Type, string? Id, string? Html);

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

        var messages = FilterMessages(chat.Messages).ToList();

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
            var payload = BuildBrowserMessagePatches(messages).ToArray();
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
                        var node = Array.from(m.children).find(function(child) { return child.matches && child.matches(selector); });
                        var oldOpen = null;
                        var openStates = {};
                        if (node) {
                            var oldDetails = node.querySelector('details');
                            oldOpen = oldDetails ? oldDetails.open : null;
                            Array.from(node.querySelectorAll('article[data-mid]')).forEach(function(article) {
                                var details = article.querySelector('details');
                                if (details) openStates[article.dataset.mid] = details.open;
                            });
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
                            var isTurnNode = !!(node.querySelector('.turn-responses') || next.querySelector('.turn-responses'));

                            // For streaming updates, patch in-place so only the active
                            // response body changes and iframe loader stays local.
                            if (curDetails && nextDetails && !isTurnNode) {
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
                                Array.from(node.querySelectorAll('article[data-mid]')).forEach(function(article) {
                                    var details = article.querySelector('details');
                                    if (details && Object.prototype.hasOwnProperty.call(openStates, article.dataset.mid)) {
                                        details.open = openStates[article.dataset.mid];
                                    }
                                });
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

                    Array.from(m.children).forEach(function(node) {
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

    private IEnumerable<BrowserMessagePatch> BuildBrowserMessagePatches(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? currentUser = null;
        var responses = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (message.Kind is ChatMessageKind.User)
            {
                if (currentUser is not null)
                {
                    yield return BuildTurnPatch(currentUser, responses);
                    responses.Clear();
                }

                currentUser = message;
            }
            else if (currentUser is null)
            {
                yield return new BrowserMessagePatch(
                    message.Id,
                    BuildRenderSignature(message),
                    _htmlRenderer.RenderMessageFragment(message, _isDarkTheme));
            }
            else
            {
                responses.Add(message);
            }
        }

        if (currentUser is not null)
        {
            yield return BuildTurnPatch(currentUser, responses);
        }
    }

    private BrowserMessagePatch BuildTurnPatch(ChatMessage userMessage, IReadOnlyList<ChatMessage> responses)
    {
        var signature = string.Join("\n---turn-message---\n",
            responses.Prepend(userMessage).Select(BuildRenderSignature));
        return new BrowserMessagePatch(
            userMessage.Id,
            signature,
            _htmlRenderer.RenderTurnFragment(userMessage, responses, _isDarkTheme));
    }

    private void ShowDetailsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _showDetailMessages = ShowDetailsCheckBox.IsChecked == true;
        RenderCurrentChat();
    }

    private void MemoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var enabled = MemoryCheckBox.IsChecked == true;
        _settings.Permissions.AllowMemoryByDefault = enabled;
        _settingsStore.Save(_settings);
        UpdateMemoryCheckBox();

        if (CurrentChat is { } chat)
        {
            GetTabContent(chat)?.SetStatus(enabled ? "Memory on" : "Memory off");
        }
    }

    private void UpdateMemoryCheckBox()
    {
        MemoryCheckBox.IsChecked = _settings.Permissions.AllowMemoryByDefault;
        MemoryCheckBox.ToolTip = _settings.Permissions.AllowMemoryByDefault
            ? "Copilot memory across sessions is on"
            : "Copilot memory across sessions is off";
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
