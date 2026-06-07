using System.Collections.Specialized;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;


namespace CopilotChatbot;

public partial class MainWindow : Window
{
    private const string AppTitle = "Copilot Chatbot";
    private const double MinTabHeaderWidth = 184;
    private const double TabStripChromeWidth = 32;
    private readonly SettingsStore _settingsStore = new();
    private readonly ChatSessionStore _chatSessionStore = new();
    private readonly HtmlRenderer _htmlRenderer = new();
    private readonly DebugLogger _debugLogger = new();
    private readonly CopilotChatService _copilot;
    private readonly ILocalShortcutService _localShortcutService;
    private readonly TaskSchedulerService _taskScheduler;
    private readonly List<ModelChoice> _models = [];
    private readonly Dictionary<ChatSessionView, long> _renderRevisions = [];
    private readonly Dictionary<ChatSessionView, ChatTabContent> _tabContents = [];
    private readonly Dictionary<ChatSessionView, Task> _browserInitializationTasks = [];
    private readonly Dictionary<ChatSessionView, Task> _sessionResumeTasks = [];
    private readonly Dictionary<string, TaskCompletionSource<PermissionPromptDecision>> _pendingPermissionPrompts = [];
    private readonly Dictionary<string, TaskCompletionSource<UserInputPromptResult>> _pendingUserInputPrompts = [];
    private readonly Dictionary<string, ChatSessionView> _pendingPromptChats = [];
    private readonly Dictionary<ChatSessionView, HashSet<string>> _forceClosedArticleIds = [];
    private readonly HashSet<ChatSessionView> _scrollToBottomAfterInitialRender = [];
    private readonly HashSet<ChatSessionView> _resumedSessions = [];
    private readonly List<ChatProjectView> _projects = [];
    private AppSettings _settings;
    private bool _isDarkTheme;
    private bool _showDetailMessages;
    private bool _isRestoringChats;
    private bool _updatingModelControls;
    private System.Windows.Threading.DispatcherTimer? _themeTimer;
    private WinForms.NotifyIcon? _notifyIcon;
    private Window? _activeResponseWindow;
    private SessionInfoWindow? _sessionInfoWindow;
    private readonly List<ResponseWindow> _responseWindows = [];
    private bool _isOpeningSessionInfoWindow;

    public MainWindow()
    {
        InitializeComponent();
        SetTabHeaderContentWidth(MinTabHeaderWidth);
        LoadWindowIcon();
        _settings = LoadSettingsForStartup();
        _settings.CommandLineGitHubToken = App.CommandLineGitHubToken;
        ApplyTrayNotificationSetting();
        _debugLogger.IsEnabled = _settings.EnableDebugLogging;
        _copilot = new CopilotChatService(_settingsStore, PromptForPermissionAsync, PromptForUserInputAsync, _debugLogger);
        _copilot.UsageUpdated += Copilot_UsageUpdated;
        _copilot.SessionPendingChanged += Copilot_SessionPendingChanged;
        _copilot.StatusChanged += Copilot_StatusChanged;
        _copilot.ChatUpdated += Copilot_ChatUpdated;
        _localShortcutService = new LocalShortcutService(_copilot, _settingsStore);
        _localShortcutService.StatusChanged += LocalShortcut_StatusChanged;
        _taskScheduler = new TaskSchedulerService(
            _copilot, _settingsStore, _debugLogger,
            () => _settings,
            title => ChatTabs.Items.OfType<TabItem>()
                .Select(t => t.Tag as ChatSessionView)
                .FirstOrDefault(c => c is not null && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)));
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
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
            _notifyIcon?.Dispose();
        }
        catch
        {
            // App shutdown should not crash if the SDK process already exited.
        }
        finally
        {
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
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
        if (_sessionInfoWindow is not null)
        {
            BringWindowToFront(_sessionInfoWindow);
            return;
        }

        if (_isOpeningSessionInfoWindow)
        {
            return;
        }

        _isOpeningSessionInfoWindow = true;
        try
        {
            var snapshot = await _copilot.GetCapabilitiesSnapshotAsync(CurrentChat);
            if (_sessionInfoWindow is not null)
            {
                _sessionInfoWindow.UpdateSnapshot(snapshot);
                BringWindowToFront(_sessionInfoWindow);
                return;
            }

            var window = new SessionInfoWindow(snapshot, this);
            _sessionInfoWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_sessionInfoWindow, window))
                {
                    _sessionInfoWindow = null;
                }
            };
            window.Show();
        }
        finally
        {
            _isOpeningSessionInfoWindow = false;
        }
    }

    private void SchedulerButton_Click(object sender, RoutedEventArgs e)
    {
        new SchedulerWindow(_taskScheduler, _models) { Owner = this }.Show();
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
            _settings.CommandLineGitHubToken = App.CommandLineGitHubToken;
            _settingsStore.Save(_settings);
            _debugLogger.IsEnabled = _settings.EnableDebugLogging;
            UpdateMemoryCheckBox();
            ApplyTrayNotificationSetting();
            ApplyThemeFromMode();
            _ = ApplySettingsToLiveSessionsAsync();
        }
    }

    private async Task ApplySettingsToLiveSessionsAsync()
    {
        try
        {
            await _copilot.ApplySettingsEnvironmentAsync(_settings);
            UpdateInputState();
        }
        catch (Exception ex)
        {
            _debugLogger.Log("APPLY-SETTINGS-SESSIONS-ERROR", ex.ToString());
        }
    }


    private void ChatTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == ChatTabs)
        {
            if (ChatTabs.SelectedItem is TabItem { Tag: ChatProjectView project })
            {
                var child = GetSessionTabs(project.Id)
                    .FirstOrDefault(tab => tab.Visibility == Visibility.Visible);
                if (child is not null)
                {
                    ChatTabs.SelectedItem = child;
                }
                return;
            }

            if (CurrentChat is { } chat)
            {
                SetTabUnreadState(chat, false);
                UpdateModelControlsForChat(chat);
                _ = EnsureSelectedChatReadyAsync(chat);
            }
            UpdateWindowTitle();
            SaveOpenChats();
        }
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelControls)
        {
            return;
        }

        if (ModelComboBox.SelectedItem is not ModelChoice model)
        {
            return;
        }

        if (CurrentChat is { IsPending: true })
        {
            UpdateModelControlsForChat(CurrentChat);
            return;
        }

        if (CurrentChat is { } chat)
        {
            chat.SelectedModelId = model.Id;
        }

        _settings.SelectedModelId = model.Id;
        ReasoningComboBox.ItemsSource = model.SupportsReasoningEffort
            ? (model.ReasoningEfforts.Count > 0 ? model.ReasoningEfforts : ["low", "medium", "high", "xhigh"])
            : Array.Empty<string>();
        var reasoningEffort = CurrentChat?.SelectedReasoningEffort ?? _settings.SelectedReasoningEffort ?? model.DefaultReasoningEffort;
        ReasoningComboBox.SelectedItem = reasoningEffort;
        if (CurrentChat is { } currentChat)
        {
            currentChat.SelectedReasoningEffort = ReasoningComboBox.SelectedItem?.ToString();
        }
        ReasoningComboBox.IsEnabled = model.SupportsReasoningEffort && CurrentChat is not { IsPending: true };
        _settingsStore.Save(_settings);

        _ = UpdateCurrentSessionSettingsAsync();
    }

    private void ReasoningComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingModelControls)
        {
            return;
        }

        if (CurrentChat is { IsPending: true })
        {
            UpdateModelControlsForChat(CurrentChat);
            return;
        }

        if (CurrentChat is { } chat)
        {
            chat.SelectedReasoningEffort = ReasoningComboBox.SelectedItem?.ToString();
        }

        _settings.SelectedReasoningEffort = ReasoningComboBox.SelectedItem?.ToString();
        _settingsStore.Save(_settings);

        _ = UpdateCurrentSessionSettingsAsync();
    }

    private async Task UpdateCurrentSessionSettingsAsync()
    {
        if (CurrentChat is { } chat && !chat.IsSessionMissing && !string.IsNullOrWhiteSpace(chat.CopilotSessionId))
        {
            if (string.IsNullOrWhiteSpace(_settings.SelectedModelId))
            {
                return;
            }

            try
            {
                await _copilot.UpdateSessionSettingsAsync(
                    chat,
                    _settings.SelectedModelId,
                    _settings.SelectedReasoningEffort);
            }
            catch (Exception ex)
            {
                _debugLogger.Log("UPDATE-SESSION-SETTINGS-ERROR", ex.Message);
            }
        }
    }

    private async Task SendChatAsync(ChatSessionView chat, string prompt)
    {
        if (chat.IsSessionMissing || string.IsNullOrWhiteSpace(prompt)) return;

        StagePreviousArticleAutoCollapse(chat);

        var promptToSend = prompt;
        var userMessageContent = prompt;
        if (await TryHandleLocalShortcutAsync(chat, prompt) is { } shortcutResult)
        {
            if (string.IsNullOrWhiteSpace(shortcutResult.PromptToSend))
            {
                chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.User, Content = prompt });
                if (shortcutResult.ResetSessionUiState)
                {
                    CancelPendingPromptsForChat(chat);
                    chat.IsPending = false;
                    chat.HasPendingUserInput = false;
                    chat.LastStatus = null;
                    SetTabBusyIndicator(chat, false);
                    GetTabContent(chat)?.SetState(false, chat.IsSessionMissing);
                }
                AddLocalShortcutMessage(chat, shortcutResult.Kind, shortcutResult.Content, shortcutResult.PromptState);
                return;
            }

            promptToSend = shortcutResult.PromptToSend;
            userMessageContent = shortcutResult.UserVisiblePrompt ?? prompt;
        }

        var model = ModelComboBox.SelectedItem as ModelChoice;
        var reasoningEffort = ReasoningComboBox.SelectedItem?.ToString();

        chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.User, Content = userMessageContent });
        chat.Messages.Add(new ChatMessage { Kind = ChatMessageKind.System, Content = BuildRequestSettingsMessage(model, reasoningEffort) });
        RenderChat(chat);
        chat.IsPending = true;
        SetTabBusyIndicator(chat, true);
        GetTabContent(chat)?.SetState(true, false);

        try
        {
            await _copilot.SendAsync(chat, promptToSend, _settings, model, reasoningEffort);
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

    private string BuildRequestSettingsMessage(ModelChoice? model, string? reasoningEffort)
    {
        var modelName = model is null
            ? _settings.SelectedModelId
            : string.IsNullOrWhiteSpace(model.Name) || string.Equals(model.Name, model.Id, StringComparison.Ordinal)
                ? model.Id
                : $"{model.Name} ({model.Id})";
        var reasoning = model?.SupportsReasoningEffort == false
            ? "not supported"
            : string.IsNullOrWhiteSpace(reasoningEffort)
                ? "default"
                : reasoningEffort;

        return $"Request settings\n\nModel: {modelName}\nReasoning strength: {reasoning}";
    }

    private async Task StopChatAsync(ChatSessionView chat)
    {
        if (!chat.IsPending) return;

        var cancelledPrompt = CancelPendingPromptsForChat(chat);

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
            if (cancelledPrompt)
            {
                SetChatInputRequired(chat, false);
            }
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

    private void AddLocalShortcutMessage(ChatSessionView chat, ChatMessageKind kind, string content, ChatPromptState? promptState = null)
    {
        chat.Messages.Add(new ChatMessage
        {
            Kind = kind,
            Content = content,
            Prompt = promptState
        });
        if (promptState is not null)
        {
            SetChatInputRequired(chat, true);
        }
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
            if (ReferenceEquals(chat, CurrentChat))
            {
                UpdateModelControlsForChat(chat);
            }
            if (!isPending)
            {
                SetTabUnreadState(chat, !ReferenceEquals(chat, CurrentChat));
                RenderChat(chat);
                SaveOpenChats();
            }
        });
    }

    private void Copilot_ChatUpdated(ChatSessionView chat)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RenderChat(chat);
            SaveOpenChats();
        });
    }

    private void ApplyModelChoices()
    {
        ModelComboBox.ItemsSource = null;
        ModelComboBox.ItemsSource = _models;
        UpdateModelControlsForChat(CurrentChat);
    }

    private void UpdateModelControlsForChat(ChatSessionView? chat)
    {
        _updatingModelControls = true;
        try
        {
            var selectedModelId = chat?.SelectedModelId ?? _settings.SelectedModelId;
            var model = _models.FirstOrDefault(m => m.Id == selectedModelId) ?? _models.FirstOrDefault();
            ModelComboBox.SelectedItem = model;

            var reasoningOptions = model?.SupportsReasoningEffort == true
                ? (model.ReasoningEfforts.Count > 0 ? model.ReasoningEfforts : ["low", "medium", "high", "xhigh"])
                : Array.Empty<string>();
            ReasoningComboBox.ItemsSource = reasoningOptions;

            var selectedReasoning = chat?.SelectedReasoningEffort ?? _settings.SelectedReasoningEffort ?? model?.DefaultReasoningEffort;
            ReasoningComboBox.SelectedItem = reasoningOptions.Contains(selectedReasoning) ? selectedReasoning : model?.DefaultReasoningEffort;

            var isBusy = chat?.IsPending == true;
            ModelComboBox.IsEnabled = _models.Count > 0 && !isBusy;
            ReasoningComboBox.IsEnabled = model?.SupportsReasoningEffort == true && !isBusy;
        }
        finally
        {
            _updatingModelControls = false;
        }
    }

    private ModelChoice? GetSelectedModelForChat(ChatSessionView chat)
    {
        return _models.FirstOrDefault(model => model.Id == chat.SelectedModelId)
            ?? ModelComboBox.SelectedItem as ModelChoice
            ?? _models.FirstOrDefault(model => model.Id == _settings.SelectedModelId)
            ?? _models.FirstOrDefault();
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

    private async Task AddChatAsync(PersistedChatSession? persisted = null, bool select = true, string? projectId = null)
    {
        var chat = CreateChatTabItem(persisted, select, projectId);
        await EnsureChatBrowserInitializedAsync(chat);
    }

    private ChatProjectView EnsureProject(string? projectId, string? name = null, bool? isCollapsed = null)
    {
        projectId = string.IsNullOrWhiteSpace(projectId)
            ? PersistedChatProject.DefaultProjectId
            : projectId;

        if (_projects.FirstOrDefault(project => project.Id == projectId) is { } existing)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name;
            }
            if (isCollapsed.HasValue)
            {
                existing.IsCollapsed = isCollapsed.Value;
            }
            EnsureProjectHeader(existing);
            return existing;
        }

        var project = new ChatProjectView(
            projectId,
            string.IsNullOrWhiteSpace(name)
                ? projectId == PersistedChatProject.DefaultProjectId ? "Default" : "Project"
                : name,
            isCollapsed == true);
        _projects.Add(project);
        EnsureProjectHeader(project);
        return project;
    }

    private void EnsureProjectHeader(ChatProjectView project)
    {
        if (ChatTabs.Items.OfType<TabItem>().Any(tab => ReferenceEquals(tab.Tag, project)))
        {
            UpdateProjectHeader(project);
            return;
        }

        var tab = new TabItem
        {
            Tag = project,
            Content = new Grid(),
            Focusable = false
        };
        SetProjectHeader(tab, project);
        ChatTabs.Items.Add(tab);
    }

    private void SetProjectHeader(TabItem tab, ChatProjectView project)
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = SymbolRegular.Folder20,
            FontSize = 15,
            Foreground = (Brush)FindResource("DisabledTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(icon, 0);

        var unreadIndicator = new Ellipse
        {
            Name = "ProjectUnreadIndicator",
            Width = 7,
            Height = 7,
            Margin = new Thickness(6, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource("AccentBrush"),
            ToolTip = "Unread response in project",
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(unreadIndicator, 2);

        var busySpinner = CreateProjectBusySpinner();
        Grid.SetColumn(busySpinner, 3);

        var toggleIcon = new Path
        {
            Name = "ProjectToggleIcon",
            Width = 10,
            Height = 10,
            Data = GetProjectToggleGeometry(project.IsCollapsed),
            Stroke = (Brush)FindResource("DisabledTextBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toggleButton = new Border
        {
            Name = "ProjectToggleButton",
            Width = 22,
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(11),
            Child = toggleIcon,
            ToolTip = project.IsCollapsed ? "Expand project" : "Collapse project",
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        toggleButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ToggleProjectCollapsed(project);
        };
        Grid.SetColumn(toggleButton, 4);

        var newChatButton = CreateProjectNewChatButton(project);
        Grid.SetColumn(newChatButton, 4);
        Grid.SetColumn(toggleButton, 5);

        var title = new TextBlock
        {
            Name = "ProjectTitleTextBlock",
            Text = project.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("DisabledTextBrush"),
            Opacity = 0.86,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = project.Name
        };
        Grid.SetColumn(title, 1);

        var header = new Grid
        {
            ToolTip = project.Name,
            Margin = new Thickness(0, 4, 0, 2),
            Cursor = Cursors.Hand,
            Children = { icon, title, unreadIndicator, busySpinner, newChatButton, toggleButton }
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left ||
                IsFromNamedElement(e.OriginalSource, "ProjectToggleButton") ||
                IsFromNamedElement(e.OriginalSource, "ProjectNewChatButton"))
            {
                return;
            }

            e.Handled = true;
            ToggleProjectCollapsed(project);
        };
        header.SetResourceReference(WidthProperty, "TabHeaderContentWidth");
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tab.Header = header;
        tab.ContextMenu = BuildProjectContextMenu(project);
        UpdateProjectHeader(project);
    }

    private void UpdateProjectHeader(ChatProjectView project)
    {
        var tab = GetProjectTab(project.Id);
        if (tab?.Header is not Panel header)
        {
            return;
        }

        var toggleButton = header.Children.OfType<Border>().FirstOrDefault(border => border.Name == "ProjectToggleButton");
        if (toggleButton is not null)
        {
            toggleButton.ToolTip = project.IsCollapsed ? "Expand project" : "Collapse project";
            if (toggleButton.Child is Path toggleIcon)
            {
                toggleIcon.Data = GetProjectToggleGeometry(project.IsCollapsed);
            }
        }

        if (header.Children.OfType<TextBlock>().FirstOrDefault(text => text.Name == "ProjectTitleTextBlock") is { } title)
        {
            title.Text = project.Name;
            title.ToolTip = project.Name;
        }

        var hasUnread = project.IsCollapsed && GetProjectChats(project.Id).Any(chat => chat.HasUnreadResponse);
        var isBusy = project.IsCollapsed && GetProjectChats(project.Id).Any(chat => chat.IsPending || chat.HasPendingUserInput);

        if (header.Children.OfType<Ellipse>().FirstOrDefault(ellipse => ellipse.Name == "ProjectUnreadIndicator") is { } unread)
        {
            unread.Visibility = hasUnread ? Visibility.Visible : Visibility.Collapsed;
        }

        if (header.Children.OfType<Path>().FirstOrDefault(path => path.Name == "ProjectBusySpinner") is { } spinner)
        {
            spinner.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        header.ToolTip = project.Name;
        tab.ContextMenu = BuildProjectContextMenu(project);
    }

    private static Geometry GetProjectToggleGeometry(bool isCollapsed) =>
        Geometry.Parse(isCollapsed ? "M 3 1 L 7 5 L 3 9" : "M 1 3 L 5 7 L 9 3");

    private FrameworkElement CreateProjectNewChatButton(ChatProjectView project)
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = SymbolRegular.AddCircle20,
            FontSize = 16,
            Foreground = (Brush)FindResource("DisabledTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = new Border
        {
            Name = "ProjectNewChatButton",
            Width = 22,
            Height = 22,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(11),
            Child = icon,
            ToolTip = "New chat in project",
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _ = AddChatAsync(projectId: project.Id);
        };
        return button;
    }

    private static bool IsFromNamedElement(object source, string name)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (current is FrameworkElement { Name: var currentName } && currentName == name)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void TabStripSplitter_DragDelta(object sender, DragDeltaEventArgs e) => UpdateTabHeaderWidthFromSplitter(sender);

    private void TabStripSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        UpdateTabHeaderWidthFromSplitter(sender);
        SaveOpenChats();
    }

    private void UpdateTabHeaderWidthFromSplitter(object sender)
    {
        if (sender is GridSplitter splitter && VisualTreeHelper.GetParent(splitter) is Grid grid && grid.ColumnDefinitions.Count > 0)
        {
            SetTabHeaderContentWidth(Math.Max(MinTabHeaderWidth, grid.ColumnDefinitions[0].ActualWidth - TabStripChromeWidth));
        }
    }

    private void SetTabHeaderContentWidth(double width)
    {
        width = Math.Max(MinTabHeaderWidth, width);
        Resources["TabHeaderContentWidth"] = width;
        Resources["TabStripColumnWidth"] = new GridLength(width + TabStripChromeWidth);
    }

    private double GetTabHeaderContentWidth() =>
        Resources["TabHeaderContentWidth"] is double width ? Math.Max(MinTabHeaderWidth, width) : MinTabHeaderWidth;

    private FrameworkElement CreateProjectBusySpinner()
    {
        var rotate = new RotateTransform(0, 7, 7);
        var spinner = new Path
        {
            Name = "ProjectBusySpinner",
            Width = 14,
            Height = 14,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Data = Geometry.Parse("M 7,1 A 6,6 0 1 1 2.76,2.76"),
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = rotate,
            ToolTip = "Project has an active session",
            Visibility = Visibility.Collapsed
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

    private void ToggleProjectCollapsed(ChatProjectView project)
    {
        project.IsCollapsed = !project.IsCollapsed;
        ApplyProjectCollapsedState(project);
        SaveOpenChats();
    }

    private void ApplyProjectCollapsedState(ChatProjectView project)
    {
        foreach (var tab in GetSessionTabs(project.Id))
        {
            tab.Visibility = project.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        UpdateProjectHeader(project);
        if (project.IsCollapsed && CurrentChat is { } current && current.ProjectId == project.Id)
        {
            SelectFirstVisibleSession();
        }
    }

    private void InsertSessionTab(TabItem tab, string projectId)
    {
        var projectTab = GetProjectTab(projectId);
        if (projectTab is null)
        {
            ChatTabs.Items.Add(tab);
            return;
        }

        var insertIndex = ChatTabs.Items.IndexOf(projectTab) + 1;
        while (insertIndex < ChatTabs.Items.Count &&
               ChatTabs.Items[insertIndex] is TabItem { Tag: ChatSessionView session } &&
               session.ProjectId == projectId)
        {
            insertIndex++;
        }

        ChatTabs.Items.Insert(insertIndex, tab);
    }

    private void MoveProjectToIndex(ChatProjectView project, int targetIndex)
    {
        var currentIndex = _projects.IndexOf(project);
        if (currentIndex < 0 || _projects.Count <= 1)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _projects.Count - 1);
        if (targetIndex == currentIndex)
        {
            return;
        }

        var projectTab = GetProjectTab(project.Id);
        if (projectTab is null)
        {
            return;
        }

        var selectedItem = ChatTabs.SelectedItem;
        var block = new List<TabItem> { projectTab };
        block.AddRange(GetSessionTabs(project.Id).ToList());

        foreach (var tab in block)
        {
            ChatTabs.Items.Remove(tab);
        }

        _projects.RemoveAt(currentIndex);
        _projects.Insert(targetIndex, project);

        var insertIndex = GetProjectBlockInsertIndex(targetIndex);
        foreach (var tab in block)
        {
            ChatTabs.Items.Insert(insertIndex++, tab);
        }

        if (selectedItem is not null && ChatTabs.Items.Contains(selectedItem))
        {
            ChatTabs.SelectedItem = selectedItem;
        }
        else
        {
            SelectFirstVisibleSession();
        }

        RefreshProjectTabContextMenus();
        SaveOpenChats();
    }

    private int GetProjectBlockInsertIndex(int projectIndex)
    {
        if (projectIndex <= 0)
        {
            return 0;
        }

        var previousProject = _projects[projectIndex - 1];
        var previousProjectTab = GetProjectTab(previousProject.Id);
        if (previousProjectTab is null)
        {
            return ChatTabs.Items.Count;
        }

        var insertIndex = ChatTabs.Items.IndexOf(previousProjectTab) + 1;
        while (insertIndex < ChatTabs.Items.Count &&
               ChatTabs.Items[insertIndex] is TabItem { Tag: ChatSessionView session } &&
               session.ProjectId == previousProject.Id)
        {
            insertIndex++;
        }

        return insertIndex;
    }

    private void RefreshProjectTabContextMenus()
    {
        foreach (var project in _projects)
        {
            if (GetProjectTab(project.Id) is { } tab)
            {
                tab.ContextMenu = BuildProjectContextMenu(project);
            }
        }
    }

    private IEnumerable<TabItem> GetSessionTabs(string projectId) =>
        ChatTabs.Items.OfType<TabItem>()
            .Where(tab => tab.Tag is ChatSessionView chat && chat.ProjectId == projectId);

    private void MoveSessionToProjectIndex(TabItem tab, ChatSessionView chat, int targetIndex)
    {
        var sessionTabs = GetSessionTabs(chat.ProjectId).ToList();
        var currentIndex = sessionTabs.IndexOf(tab);
        if (currentIndex < 0 || sessionTabs.Count <= 1)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, sessionTabs.Count - 1);
        if (targetIndex == currentIndex)
        {
            return;
        }

        var projectTab = GetProjectTab(chat.ProjectId);
        if (projectTab is null)
        {
            return;
        }

        ChatTabs.Items.Remove(tab);
        sessionTabs.RemoveAt(currentIndex);

        if (targetIndex <= 0)
        {
            var projectIndex = ChatTabs.Items.IndexOf(projectTab);
            ChatTabs.Items.Insert(projectIndex + 1, tab);
        }
        else if (targetIndex >= sessionTabs.Count)
        {
            var insertIndex = ChatTabs.Items.IndexOf(projectTab) + 1;
            while (insertIndex < ChatTabs.Items.Count &&
                   ChatTabs.Items[insertIndex] is TabItem { Tag: ChatSessionView session } &&
                   session.ProjectId == chat.ProjectId)
            {
                insertIndex++;
            }
            ChatTabs.Items.Insert(insertIndex, tab);
        }
        else
        {
            var targetTab = sessionTabs[targetIndex];
            var insertIndex = ChatTabs.Items.IndexOf(targetTab);
            ChatTabs.Items.Insert(insertIndex, tab);
        }

        ChatTabs.SelectedItem = tab;
        tab.BringIntoView();
        ChatTabs.UpdateLayout();
        RefreshSessionTabContextMenus();
        SaveOpenChats();
    }

    private IEnumerable<ChatSessionView> GetProjectChats(string projectId) =>
        ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .Where(chat => chat is not null && chat.ProjectId == projectId)!;

    private TabItem? GetProjectTab(string projectId) =>
        ChatTabs.Items.OfType<TabItem>()
            .FirstOrDefault(tab => tab.Tag is ChatProjectView project && project.Id == projectId);

    private bool IsProjectCollapsed(string projectId) =>
        _projects.FirstOrDefault(project => project.Id == projectId)?.IsCollapsed == true;

    private string GetSelectedProjectId()
    {
        if (CurrentChat?.ProjectId is { } currentProjectId)
        {
            return currentProjectId;
        }

        if (ChatTabs.SelectedItem is TabItem { Tag: ChatProjectView project })
        {
            return project.Id;
        }

        return PersistedChatProject.DefaultProjectId;
    }

    private void SelectFirstVisibleSession()
    {
        var next = ChatTabs.Items.OfType<TabItem>()
            .FirstOrDefault(tab => tab.Visibility == Visibility.Visible && tab.Tag is ChatSessionView);
        if (next is not null)
        {
            ChatTabs.SelectedItem = next;
        }
    }

    /// <summary>
    /// Synchronously creates the <see cref="ChatSessionView"/> and its <see cref="TabItem"/> and
    /// adds them to the tab strip.  Does <em>not</em> initialize the embedded browser — call
    /// <see cref="InitializeChatBrowserAsync"/> afterwards.
    /// </summary>
    private ChatSessionView CreateChatTabItem(PersistedChatSession? persisted, bool select, string? projectId = null)
    {
        var effectiveProjectId = EnsureProject(projectId ?? persisted?.ProjectId ?? GetSelectedProjectId()).Id;
        var chat = new ChatSessionView(string.IsNullOrWhiteSpace(persisted?.Title) ? $"Chat {ChatTabs.Items.OfType<TabItem>().Count(tab => tab.Tag is ChatSessionView) + 1}" : persisted!.Title)
        {
            ProjectId = effectiveProjectId,
            CopilotSessionId = persisted?.CopilotSessionId,
            IsSessionMissing = persisted?.IsSessionMissing == true && string.IsNullOrWhiteSpace(persisted.CopilotSessionId),
            SystemPrompt = persisted is null
                ? (string.IsNullOrWhiteSpace(_settings.DefaultSystemPrompt) ? null : _settings.DefaultSystemPrompt)
                : persisted.SystemPrompt,
            SelectedModelId = string.IsNullOrWhiteSpace(persisted?.SelectedModelId) ? _settings.SelectedModelId : persisted!.SelectedModelId,
            SelectedReasoningEffort = string.IsNullOrWhiteSpace(persisted?.SelectedReasoningEffort) ? _settings.SelectedReasoningEffort : persisted!.SelectedReasoningEffort,
            AutoCollapsePreviousArticle = persisted is null
                ? _settings.DefaultAutoCollapsePreviousArticle
                : persisted.AutoCollapsePreviousArticle
        };
        if (persisted?.IsSessionMissing == true && !string.IsNullOrWhiteSpace(persisted.CopilotSessionId))
        {
            _debugLogger.Log("SESSION-RESTORE", $"Retrying previously missing session '{persisted.CopilotSessionId}' for '{chat.Title}'.");
        }
        if (persisted is not null)
        {
            if (persisted.Messages.Count > 0)
            {
                _scrollToBottomAfterInitialRender.Add(chat);
            }

            foreach (var message in persisted.Messages)
            {
                if (message.Prompt is { IsAnswered: false } stalePrompt)
                {
                    stalePrompt.IsAnswered = true;
                    stalePrompt.Answer = "Prompt expired";
                    message.CompletedAt ??= DateTimeOffset.Now;
                }

                chat.Messages.Add(new ChatMessage
                {
                    Id = string.IsNullOrWhiteSpace(message.Id) ? Guid.NewGuid().ToString("N") : message.Id,
                    Kind = message.Kind,
                    Content = message.Content,
                    CreatedAt = message.CreatedAt == default ? DateTimeOffset.Now : message.CreatedAt,
                    CompletedAt = message.CompletedAt,
                    Prompt = message.Prompt,
                    IframeHeights = message.IframeHeights ?? []
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
        tabContent.AutoCollapsePreviousArticleChanged += enabled =>
        {
            chat.AutoCollapsePreviousArticle = enabled;
            SaveOpenChats();
        };
        _tabContents[chat] = tabContent;
        tabContent.SetAutoCollapsePreviousArticle(chat.AutoCollapsePreviousArticle);
        tabContent.SetState(chat.IsPending, chat.IsSessionMissing);

        var tab = new TabItem { Content = tabContent, Tag = chat };
        SetTabHeader(tab, chat.Title);
        InsertSessionTab(tab, effectiveProjectId);
        tab.ContextMenu = BuildTabContextMenu(tab);
        RefreshSessionTabContextMenus();
        tab.Visibility = IsProjectCollapsed(effectiveProjectId) ? Visibility.Collapsed : Visibility.Visible;
        if (select)
        {
            ChatTabs.SelectedItem = tab;
            UpdateModelControlsForChat(chat);
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
        if (chat.IsSessionMissing)
        {
            _debugLogger.Log("SESSION-RESTORE", $"Skipped '{chat.Title}' because it is marked missing and has no retryable session id.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
        {
            _debugLogger.Log("SESSION-RESTORE", $"Skipped '{chat.Title}' because it has no saved Copilot session id.");
            return Task.CompletedTask;
        }

        if (_resumedSessions.Contains(chat))
        {
            _debugLogger.Log("SESSION-RESTORE", $"Skipped '{chat.Title}' because it is already resumed.");
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
            var model = GetSelectedModelForChat(chat);
            _debugLogger.Log("SESSION-RESTORE", $"Resuming '{chat.Title}' session '{chat.CopilotSessionId}' | model={model?.Id ?? _settings.SelectedModelId} | reasoning={chat.SelectedReasoningEffort ?? _settings.SelectedReasoningEffort ?? "default"}");
            await _copilot.ResumeSessionAsync(chat, _settings, model, chat.SelectedReasoningEffort);
            _resumedSessions.Add(chat);
            _debugLogger.Log("SESSION-RESTORE", $"Resumed '{chat.Title}' session '{chat.CopilotSessionId}'.");
        }
        catch (Exception ex)
        {
            _debugLogger.Log("SESSION-RESTORE-ERROR", $"Failed to resume '{chat.Title}' session '{chat.CopilotSessionId}'.\n{ex}");
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
        if (state.TabHeaderWidth > 0)
        {
            SetTabHeaderContentWidth(state.TabHeaderWidth);
        }
        if (!hadPersistedState)
        {
            await AddChatAsync();
            return;
        }

        var tabs = new List<TabItem>();
        _isRestoringChats = true;
        try
        {
            var projects = state.Projects.Count == 0
                ? new List<PersistedChatProject> { new() }
                : state.Projects;
            if (projects.All(project => project.Id != PersistedChatProject.DefaultProjectId))
            {
                projects.Insert(0, new PersistedChatProject());
            }

            foreach (var projectId in state.Sessions
                         .Select(session => string.IsNullOrWhiteSpace(session.ProjectId) ? PersistedChatProject.DefaultProjectId : session.ProjectId!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(projectId => projects.All(project => !project.Id.Equals(projectId, StringComparison.OrdinalIgnoreCase))))
            {
                projects.Add(new PersistedChatProject { Id = projectId, Name = projectId });
            }

            foreach (var project in projects)
            {
                EnsureProject(project.Id, project.Name, project.IsCollapsed);
            }

            // Phase 1: Create all tab shells synchronously so the tab strip is fully
            // populated before any slow browser-initialization awaits begin.
            foreach (var saved in state.Sessions.OrderBy(session =>
                         _projects.FindIndex(project => project.Id == (string.IsNullOrWhiteSpace(session.ProjectId)
                             ? PersistedChatProject.DefaultProjectId
                             : session.ProjectId))))
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
        var startupTab = tabs.FirstOrDefault(tab => tab.Visibility == Visibility.Visible && (tab.Tag as ChatSessionView)?.IsSessionMissing == false)
            ?? tabs.FirstOrDefault(tab => tab.Visibility == Visibility.Visible && tab.Tag is ChatSessionView);
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
        _debugLogger.Log("SESSION-MISSING", $"{chat.Title} | session={chat.CopilotSessionId ?? "(none)"} | {reason}");
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

        var sessions = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .Where(chat => chat is not null)
            .Select(chat => ToPersistedSession(chat!))
            .ToList();
        var state = new PersistedChatState
        {
            Projects = _projects.Select(project => new PersistedChatProject
            {
                Id = project.Id,
                Name = project.Name,
                IsCollapsed = project.IsCollapsed
            }).ToList(),
            Sessions = sessions,
            SelectedSessionId = CurrentChat?.CopilotSessionId,
            TabHeaderWidth = GetTabHeaderContentWidth()
        };

        _chatSessionStore.Save(state);
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
            ProjectId = chat.ProjectId,
            CopilotSessionId = chat.CopilotSessionId,
            SystemPrompt = chat.SystemPrompt,
            SelectedModelId = chat.SelectedModelId,
            SelectedReasoningEffort = chat.SelectedReasoningEffort,
            AutoCollapsePreviousArticle = chat.AutoCollapsePreviousArticle,
            IsSessionMissing = chat.IsSessionMissing && string.IsNullOrWhiteSpace(chat.CopilotSessionId),
            Messages = chat.Messages.Select(message => new PersistedChatMessage
            {
                Id = message.Id,
                Kind = message.Kind,
                Content = message.Content,
                CreatedAt = message.CreatedAt,
                CompletedAt = message.CompletedAt,
                Prompt = message.Prompt,
                IframeHeights = message.IframeHeights ?? []
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
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = title
        };
        titleBlock.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                e.Handled = true;
                RenameTab(tab);
            }
        };
        Grid.SetColumn(titleBlock, 1);

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
        var inputRequiredIndicator = CreateTabInputRequiredIndicator();
        var isPending = tab.Tag is ChatSessionView { IsPending: true };
        var inputRequired = tab.Tag is ChatSessionView { HasPendingUserInput: true };
        var isTyping = tab.Tag is ChatSessionView chat && IsTypingStatus(chat.LastStatus);
        inputRequiredIndicator.Visibility = inputRequired ? Visibility.Visible : Visibility.Collapsed;
        busySpinner.Visibility = isPending && !isTyping && !inputRequired ? Visibility.Visible : Visibility.Collapsed;
        typingIndicator.Visibility = isPending && isTyping && !inputRequired ? Visibility.Visible : Visibility.Collapsed;
        closeButton.Visibility = isPending || inputRequired ? Visibility.Collapsed : Visibility.Visible;
        closeButton.IsEnabled = !isPending && !inputRequired;

        var closeSlot = new Grid
        {
            Name = "TabCloseSlot",
            Width = 22,
            Height = 22,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                inputRequiredIndicator,
                typingIndicator,
                busySpinner,
                closeButton
            }
        };
        Grid.SetColumn(unreadIndicator, 2);
        Grid.SetColumn(closeSlot, 3);

        var header = new Grid
        {
            ToolTip = title,
            Children =
            {
                titleBlock,
                unreadIndicator,
                closeSlot
            }
        };
        header.SetResourceReference(WidthProperty, "TabHeaderContentWidth");
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(21) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tab.Header = header;
        tab.MouseDoubleClick -= Tab_MouseDoubleClick;
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

    private FrameworkElement CreateTabInputRequiredIndicator()
    {
        return new Border
        {
            Name = "TabInputRequiredIndicator",
            Width = 20,
            Height = 20,
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F97316")),
            ToolTip = "Input required",
            Child = new TextBlock
            {
                Text = "?",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            Visibility = Visibility.Collapsed
        };
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
        var inputRequiredIndicator = closeSlot?.Children
            .OfType<Border>()
            .FirstOrDefault(border => border.Name == "TabInputRequiredIndicator");
        var closeButton = closeSlot?.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Name == "TabCloseButton");
        var isTyping = isBusy && IsTypingStatus(chat.LastStatus);
        var inputRequired = chat.HasPendingUserInput;

        if (inputRequiredIndicator is not null)
        {
            inputRequiredIndicator.Visibility = inputRequired ? Visibility.Visible : Visibility.Collapsed;
        }

        if (typingIndicator is not null)
        {
            typingIndicator.Visibility = isTyping && !inputRequired ? Visibility.Visible : Visibility.Collapsed;
        }

        if (spinner is not null)
        {
            spinner.Visibility = isBusy && !isTyping && !inputRequired ? Visibility.Visible : Visibility.Collapsed;
        }

        if (closeButton is not null)
        {
            closeButton.Visibility = isBusy || inputRequired ? Visibility.Collapsed : Visibility.Visible;
            closeButton.IsEnabled = !isBusy && !inputRequired;
        }

        if (tab.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "CloseTabMenuItem") is { } closeItem)
        {
            closeItem.IsEnabled = !isBusy && !inputRequired;
        }

        if (_projects.FirstOrDefault(project => project.Id == chat.ProjectId) is { } project)
        {
            UpdateProjectHeader(project);
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

        if (_projects.FirstOrDefault(project => project.Id == chat.ProjectId) is { } project)
        {
            UpdateProjectHeader(project);
        }
    }

    private ContextMenu BuildTabContextMenu(TabItem tab)
    {
        var menu = new ContextMenu();
        var renameItem = new MenuItem
        {
            Header = "Rename",
            Icon = CreateMenuIcon(SymbolRegular.CircleEdit20)
        };
        renameItem.Click += (_, _) => RenameTab(tab);
        menu.Items.Add(renameItem);
        var newProjectItem = new MenuItem
        {
            Header = "New project...",
            Icon = CreateMenuIcon(SymbolRegular.FolderAdd16)
        };
        newProjectItem.Click += (_, _) => CreateProject();
        menu.Items.Add(newProjectItem);
        if (tab.Tag is ChatSessionView chat)
        {
            var moveItem = new MenuItem
            {
                Header = "Move to project",
                Icon = CreateMenuIcon(SymbolRegular.FolderArrowRight20)
            };
            foreach (var project in _projects)
            {
                var projectItem = new MenuItem
                {
                    Header = project.Name,
                    Icon = CreateMenuIcon(SymbolRegular.Folder16),
                    IsChecked = chat.ProjectId == project.Id
                };
                projectItem.Click += (_, _) => MoveSessionToProject(tab, chat, project.Id);
                moveItem.Items.Add(projectItem);
            }
            menu.Items.Add(moveItem);

            menu.Items.Add(new Separator());

            var sessionTabs = GetSessionTabs(chat.ProjectId).ToList();
            var sessionIndex = sessionTabs.IndexOf(tab);
            var canMoveUp = sessionIndex > 0;
            var canMoveDown = sessionIndex >= 0 && sessionIndex < sessionTabs.Count - 1;

            var moveTopItem = new MenuItem
            {
                Header = "Move to top",
                IsEnabled = canMoveUp,
                Icon = CreateMenuIcon(SymbolRegular.ChevronDoubleUp20)
            };
            moveTopItem.Click += (_, _) => MoveSessionToProjectIndex(tab, chat, 0);
            menu.Items.Add(moveTopItem);

            var moveUpItem = new MenuItem
            {
                Header = "Move up",
                IsEnabled = canMoveUp,
                Icon = CreateMenuIcon(SymbolRegular.ChevronCircleUp20)
            };
            moveUpItem.Click += (_, _) => MoveSessionToProjectIndex(tab, chat, sessionIndex - 1);
            menu.Items.Add(moveUpItem);

            var moveDownItem = new MenuItem
            {
                Header = "Move down",
                IsEnabled = canMoveDown,
                Icon = CreateMenuIcon(SymbolRegular.ChevronCircleDown20)
            };
            moveDownItem.Click += (_, _) => MoveSessionToProjectIndex(tab, chat, sessionIndex + 1);
            menu.Items.Add(moveDownItem);

            var moveBottomItem = new MenuItem
            {
                Header = "Move to bottom",
                IsEnabled = canMoveDown,
                Icon = CreateMenuIcon(SymbolRegular.ChevronDoubleDown20)
            };
            moveBottomItem.Click += (_, _) => MoveSessionToProjectIndex(tab, chat, sessionTabs.Count - 1);
            menu.Items.Add(moveBottomItem);
        }
        var closeItem = new MenuItem
        {
            Header = "Close",
            Icon = CreateMenuIcon(SymbolRegular.Dismiss20)
        };
        closeItem.Name = "CloseTabMenuItem";
        closeItem.IsEnabled = tab.Tag is not ChatSessionView { IsPending: true } &&
                              tab.Tag is not ChatSessionView { HasPendingUserInput: true };
        closeItem.Click += (_, _) => _ = CloseTabAsync(tab);
        menu.Items.Add(closeItem);
        return menu;
    }

    private ContextMenu BuildProjectContextMenu(ChatProjectView project)
    {
        var menu = new ContextMenu();
        var collapseItem = new MenuItem
        {
            Header = project.IsCollapsed ? "Expand" : "Collapse",
            Icon = CreateMenuIcon(project.IsCollapsed ? SymbolRegular.ChevronCircleRight20 : SymbolRegular.ChevronCircleDown20)
        };
        collapseItem.Click += (_, _) => ToggleProjectCollapsed(project);
        menu.Items.Add(collapseItem);

        var newChatItem = new MenuItem
        {
            Header = "New chat in project",
            Icon = CreateMenuIcon(SymbolRegular.Chat16)
        };
        newChatItem.Click += (_, _) => _ = AddChatAsync(projectId: project.Id);
        menu.Items.Add(newChatItem);

        var newProjectItem = new MenuItem
        {
            Header = "New project...",
            Icon = CreateMenuIcon(SymbolRegular.FolderAdd16)
        };
        newProjectItem.Click += (_, _) => CreateProject();
        menu.Items.Add(newProjectItem);

        var renameItem = new MenuItem
        {
            Header = "Rename project...",
            Icon = CreateMenuIcon(SymbolRegular.CircleEdit20)
        };
        renameItem.Click += (_, _) => RenameProject(project);
        menu.Items.Add(renameItem);

        menu.Items.Add(new Separator());

        var projectIndex = _projects.IndexOf(project);
        var canMoveUp = projectIndex > 0;
        var canMoveDown = projectIndex >= 0 && projectIndex < _projects.Count - 1;

        var moveTopItem = new MenuItem
        {
            Header = "Move to top",
            IsEnabled = canMoveUp,
            Icon = CreateMenuIcon(SymbolRegular.ChevronDoubleUp20)
        };
        moveTopItem.Click += (_, _) => MoveProjectToIndex(project, 0);
        menu.Items.Add(moveTopItem);

        var moveUpItem = new MenuItem
        {
            Header = "Move up",
            IsEnabled = canMoveUp,
            Icon = CreateMenuIcon(SymbolRegular.ChevronCircleUp20)
        };
        moveUpItem.Click += (_, _) => MoveProjectToIndex(project, projectIndex - 1);
        menu.Items.Add(moveUpItem);

        var moveDownItem = new MenuItem
        {
            Header = "Move down",
            IsEnabled = canMoveDown,
            Icon = CreateMenuIcon(SymbolRegular.ChevronCircleDown20)
        };
        moveDownItem.Click += (_, _) => MoveProjectToIndex(project, projectIndex + 1);
        menu.Items.Add(moveDownItem);

        var moveBottomItem = new MenuItem
        {
            Header = "Move to bottom",
            IsEnabled = canMoveDown,
            Icon = CreateMenuIcon(SymbolRegular.ChevronDoubleDown20)
        };
        moveBottomItem.Click += (_, _) => MoveProjectToIndex(project, _projects.Count - 1);
        menu.Items.Add(moveBottomItem);
        return menu;
    }

    private FrameworkElement CreateMenuIcon(SymbolRegular symbol)
    {
        return new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = symbol,
            FontSize = 16,
            Width = 18,
            Height = 18,
            Foreground = (Brush)FindResource("MutedTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void CreateProject()
    {
        var dialog = new RenameTabWindow("", "New Project", "Project name", "Create") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var project = EnsureProject(Guid.NewGuid().ToString("N"), dialog.TabTitle, isCollapsed: false);
        RefreshSessionTabContextMenus();
        SaveOpenChats();
        GetProjectTab(project.Id)?.BringIntoView();
    }

    private void RenameProject(ChatProjectView project)
    {
        var dialog = new RenameTabWindow(project.Name, "Rename Project", "Project name", "Rename") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        project.Name = dialog.TabTitle;
        UpdateProjectHeader(project);
        RefreshSessionTabContextMenus();
        UpdateWindowTitle();
        SaveOpenChats();
    }

    private void RefreshSessionTabContextMenus()
    {
        foreach (var tab in ChatTabs.Items.OfType<TabItem>())
        {
            if (tab.Tag is ChatSessionView)
            {
                tab.ContextMenu = BuildTabContextMenu(tab);
            }
        }
    }

    private void MoveSessionToProject(TabItem tab, ChatSessionView chat, string projectId)
    {
        if (chat.ProjectId == projectId)
        {
            return;
        }

        ChatTabs.Items.Remove(tab);
        chat.ProjectId = projectId;
        InsertSessionTab(tab, projectId);
        tab.Visibility = IsProjectCollapsed(projectId) ? Visibility.Collapsed : Visibility.Visible;
        if (tab.Visibility == Visibility.Visible)
        {
            ChatTabs.SelectedItem = tab;
        }
        else
        {
            SelectFirstVisibleSession();
        }
        RefreshSessionTabContextMenus();
        SaveOpenChats();
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
            _forceClosedArticleIds.Remove(chat);
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
            tab.ContextMenu = BuildTabContextMenu(tab);
            UpdateWindowTitle();
            SaveOpenChats();
        }
    }

    private void ExportChatHistory(ChatSessionView chat)
    {
        if (chat.IsPending || chat.IsSessionMissing)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{MakeSafeFileName(chat.Title)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var extension = IOPath.GetExtension(dialog.FileName);
        var content = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(ToPersistedSession(chat), new JsonSerializerOptions { WriteIndented = true })
            : BuildMarkdownChatHistory(chat);

        IOFile.WriteAllText(dialog.FileName, content, Encoding.UTF8);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = IOPath.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        var safe = new string(chars).Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(safe) ? "chat-history" : safe;
    }

    private static string BuildMarkdownChatHistory(ChatSessionView chat)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {chat.Title}");
        builder.AppendLine();
        builder.AppendLine($"Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        if (!string.IsNullOrWhiteSpace(chat.CopilotSessionId))
        {
            builder.AppendLine($"Session: {chat.CopilotSessionId}");
        }
        builder.AppendLine();

        foreach (var message in chat.Messages)
        {
            builder.AppendLine($"## {message.Kind} - {message.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}");
            if (message.CompletedAt is { } completedAt)
            {
                builder.AppendLine($"Completed: {completedAt:yyyy-MM-dd HH:mm:ss zzz}");
                builder.AppendLine();
            }

            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private void ChatMessages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ChatTabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals((t.Tag as ChatSessionView)?.Messages, sender))?.Tag is ChatSessionView chat)
        {
            if (chat.IsApplyingBufferedUpdates)
            {
                return;
            }

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
            var html = DecodeBridgeHtml(message);
            if (!string.IsNullOrWhiteSpace(html))
            {
                new IframePreviewWindow(html, _isDarkTheme) { Owner = this }.Show();
            }

            return;
        }

        var sourceChat = ChatTabs.Items.OfType<TabItem>()
            .Select(tab => tab.Tag as ChatSessionView)
            .FirstOrDefault(chat => ReferenceEquals(chat?.Browser.CoreWebView2, sender));

        if (message.Type.Equals("saveHistory", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceChat is not null)
            {
                ExportChatHistory(sourceChat);
            }

            return;
        }

        if (message.Type.Equals("copyUserMessage", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceChat is not null && !string.IsNullOrWhiteSpace(message.Id))
            {
                CopyUserMessageToClipboard(sourceChat, message.Id);
            }

            return;
        }

        if (message.Type.Equals("deleteMessage", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceChat is not null && !string.IsNullOrWhiteSpace(message.Id))
            {
                DeleteChatMessage(sourceChat, message.Id);
            }

            return;
        }

        if (message.Type.Equals("iframeHeightChanged", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceChat is not null &&
                !string.IsNullOrWhiteSpace(message.Id) &&
                !string.IsNullOrWhiteSpace(message.FrameKey) &&
                double.TryParse(message.Value, out var iframeHeight))
            {
                UpdateIframeHeight(sourceChat, message.Id, message.FrameKey, iframeHeight);
            }

            return;
        }

        if (message.Type.Equals("promptResponse", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceChat is not null && !string.IsNullOrWhiteSpace(message.Id))
            {
                HandlePromptResponse(sourceChat, message.Id, message.Value ?? "", message.Mode ?? "choice");
            }

            return;
        }

        var id = message.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var chatMessage = (sourceChat ?? CurrentChat)?.Messages.FirstOrDefault(m => m.Id == id);
        if (chatMessage is not null)
        {
            ShowResponseWindow(chatMessage);
        }
    }

    private void ShowResponseWindow(ChatMessage message)
    {
        var window = new ResponseWindow(_htmlRenderer, message, _isDarkTheme, () => _isDarkTheme) { Owner = this };
        _responseWindows.Add(window);
        window.Closed += (_, _) => _responseWindows.Remove(window);
        window.Show();
    }

    private void CopyUserMessageToClipboard(ChatSessionView chat, string messageId)
    {
        var message = chat.Messages.FirstOrDefault(m => m.Id == messageId && m.Kind == ChatMessageKind.User);
        if (message is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(message.Content);
            GetTabContent(chat)?.SetStatus("Copied user message");
        }
        catch (Exception ex)
        {
            _debugLogger.Log("COPY-USER-MESSAGE-ERROR", ex.Message);
            GetTabContent(chat)?.SetStatus("Failed to copy user message");
        }
    }

    private void DeleteChatMessage(ChatSessionView chat, string messageId)
    {
        if (chat.IsPending)
        {
            GetTabContent(chat)?.SetStatus("Cannot delete while Copilot is responding");
            return;
        }

        var index = chat.Messages.Select((message, i) => new { message, i })
            .FirstOrDefault(item => item.message.Id == messageId)?.i ?? -1;
        if (index < 0)
        {
            return;
        }

        var message = chat.Messages[index];
        if (message.Kind is not (ChatMessageKind.User or ChatMessageKind.Assistant))
        {
            return;
        }

        var deleteCount = message.Kind == ChatMessageKind.User
            ? CountTurnMessages(chat.Messages, index)
            : 1;
        var confirmText = message.Kind == ChatMessageKind.User && deleteCount > 1
            ? $"Delete this user message and {deleteCount - 1} response article{(deleteCount == 2 ? "" : "s")} under it?"
            : "Delete this message article?";

        if (MessageBox.Show(this, confirmText, "Delete message", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        for (var i = 0; i < deleteCount; i++)
        {
            chat.Messages.RemoveAt(index);
        }

        RenderChat(chat);
        SaveOpenChats();
    }

    private static int CountTurnMessages(IList<ChatMessage> messages, int userIndex)
    {
        var count = 1;
        for (var i = userIndex + 1; i < messages.Count; i++)
        {
            if (messages[i].Kind == ChatMessageKind.User)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private void UpdateIframeHeight(ChatSessionView chat, string messageId, string frameKey, double height)
    {
        if (chat.Messages.FirstOrDefault(message => message.Id == messageId) is not { } message)
        {
            return;
        }

        height = Math.Clamp(height, 80, 5000);
        message.IframeHeights ??= [];
        if (message.IframeHeights.TryGetValue(frameKey, out var previous) && Math.Abs(previous - height) < 2)
        {
            return;
        }

        message.IframeHeights[frameKey] = height;
        SaveOpenChats();
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
                var htmlBase64 = root.TryGetProperty("htmlBase64", out var htmlBase64Prop) ? htmlBase64Prop.GetString() : null;
                var value = root.TryGetProperty("value", out var valueProp) ? valueProp.GetString() : null;
                var mode = root.TryGetProperty("mode", out var modeProp) ? modeProp.GetString() : null;
                var frameKey = root.TryGetProperty("frameKey", out var frameKeyProp) ? frameKeyProp.GetString() : null;
                return new BrowserBridgeMessage(typeValue, id, html, htmlBase64, value, mode, frameKey);
            }

            return root.ValueKind == JsonValueKind.String
                ? new BrowserBridgeMessage("open", root.GetString(), null, null, null, null, null)
                : null;
        }
        catch
        {
            try
            {
                return new BrowserBridgeMessage("open", e.TryGetWebMessageAsString(), null, null, null, null, null);
            }
            catch
            {
                return null;
            }
        }
    }

    private static string? DecodeBridgeHtml(BrowserBridgeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.HtmlBase64))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(message.HtmlBase64));
            }
            catch
            {
                return message.Html;
            }
        }

        return message.Html;
    }

    private sealed record BrowserBridgeMessage(string Type, string? Id, string? Html, string? HtmlBase64, string? Value, string? Mode, string? FrameKey);

    private sealed record AgentPromptSubmission(HashSet<string> EnabledAgentNames, string DefaultAgentName);

    private Task<PermissionPromptDecision> PromptForPermissionAsync(ChatSessionView chat, PermissionPrompt prompt)
    {
        var promptId = $"permission-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<PermissionPromptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(() =>
        {
            _pendingPermissionPrompts[promptId] = tcs;
            _pendingPromptChats[promptId] = chat;
            AddPromptMessage(chat, promptId, BuildPermissionPromptArticleText(prompt), new ChatPromptState
            {
                Type = "permission",
                Choices = ["Deny", "AllowOnce", "AllowForSession", "SaveToSettings"],
                AllowFreeform = false
            });
            ShowUserResponseNotification("Permission required", BuildPermissionNotificationText(prompt));
        });
        return tcs.Task;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Task<UserInputPromptResult> PromptForUserInputAsync(ChatSessionView chat, UserInputPrompt prompt)
    {
        var tcs = new TaskCompletionSource<UserInputPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptId = $"ask-user-{Guid.NewGuid():N}";
        Dispatcher.BeginInvoke(() =>
        {
            _pendingUserInputPrompts[promptId] = tcs;
            _pendingPromptChats[promptId] = chat;
            AddPromptMessage(chat, promptId, prompt.Question, new ChatPromptState
            {
                Type = "user-input",
                Choices = prompt.Choices.ToList(),
                AllowFreeform = prompt.AllowFreeform
            });
            ShowUserResponseNotification("Copilot needs your response", BuildUserInputNotificationText(prompt));
        });
        return tcs.Task;
    }

    private void AddPromptMessage(ChatSessionView chat, string promptId, string content, ChatPromptState promptState)
    {
        chat.Messages.Add(new ChatMessage
        {
            Id = promptId,
            Kind = ChatMessageKind.Prompt,
            Content = content,
            Prompt = promptState
        });
        SetChatInputRequired(chat, true);
        RenderChat(chat);
        SaveOpenChats();
    }

    private async void HandlePromptResponse(ChatSessionView chat, string promptId, string value, string mode)
    {
        var message = chat.Messages.FirstOrDefault(message => message.Id == promptId);
        if (message?.Prompt is not { IsAnswered: false } promptState)
        {
            return;
        }

        value = value.Trim();
        if (promptState.Type.Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAgentPromptResponseAsync(chat, message, promptState, value);
            return;
        }

        promptState.IsAnswered = true;
        promptState.Answer = value;
        promptState.WasFreeform = mode.Equals("freeform", StringComparison.OrdinalIgnoreCase);
        message.CompletedAt = DateTimeOffset.Now;

        if (_pendingPermissionPrompts.Remove(promptId, out var permissionTcs))
        {
            permissionTcs.TrySetResult(ParsePermissionDecision(value));
        }
        else if (_pendingUserInputPrompts.Remove(promptId, out var inputTcs))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                StagePreviousArticleAutoCollapse(chat);
            }

            inputTcs.TrySetResult(new UserInputPromptResult(value, promptState.WasFreeform));
        }

        _pendingPromptChats.Remove(promptId);
        SetChatInputRequired(chat, HasPendingPrompt(chat));
        RenderChat(chat);
        SaveOpenChats();
    }

    private async Task HandleAgentPromptResponseAsync(ChatSessionView chat, ChatMessage message, ChatPromptState promptState, string value)
    {
        try
        {
            var request = ParseAgentPromptValue(value);
            var result = await _copilot.ApplyAgentSelectionAsync(chat, request.EnabledAgentNames, request.DefaultAgentName);
            promptState.IsAnswered = true;
            promptState.WasFreeform = false;
            promptState.AgentOptions = promptState.AgentOptions
                .Select(option => option with { IsEnabled = request.EnabledAgentNames.Contains(option.Name, StringComparer.OrdinalIgnoreCase) })
                .ToList();
            promptState.DefaultAgentName = result.DefaultAgentName;
            promptState.Answer = FormatAgentPromptAnswer(result);
            message.CompletedAt = DateTimeOffset.Now;
            _pendingPromptChats.Remove(message.Id);
            SetChatInputRequired(chat, HasPendingPrompt(chat));
            await RefreshSessionInfoWindowAsync(chat);
            RenderChat(chat);
            SaveOpenChats();
        }
        catch (Exception ex)
        {
            chat.Messages.Add(new ChatMessage
            {
                Kind = ChatMessageKind.Error,
                Content = "Failed to apply agent settings.\n\n" + ex.Message
            });
            _pendingPromptChats.Remove(message.Id);
            SetChatInputRequired(chat, HasPendingPrompt(chat));
            RenderChat(chat);
            SaveOpenChats();
        }
    }

    private async Task RefreshSessionInfoWindowAsync(ChatSessionView chat)
    {
        if (_sessionInfoWindow is null)
        {
            return;
        }

        try
        {
            var snapshot = await _copilot.GetCapabilitiesSnapshotAsync(chat);
            _sessionInfoWindow.UpdateSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _debugLogger.Log("SESSION-INFO-REFRESH-ERROR", ex.Message);
        }
    }

    private static AgentPromptSubmission ParseAgentPromptValue(string value)
    {
        using var doc = JsonDocument.Parse(value);
        var root = doc.RootElement;
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("enabled", out var enabledProp) && enabledProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in enabledProp.EnumerateArray())
            {
                if (item.GetString() is { Length: > 0 } agentName)
                {
                    enabled.Add(agentName);
                }
            }
        }

        var defaultAgent = root.TryGetProperty("defaultAgent", out var defaultProp)
            ? defaultProp.GetString() ?? ""
            : "";

        return new AgentPromptSubmission(enabled, defaultAgent);
    }

    private static string FormatAgentPromptAnswer(AgentApplyResult result)
    {
        var selected = string.IsNullOrWhiteSpace(result.DefaultAgentName)
            ? "(default agent)"
            : result.DefaultAgentName;
        return $"{result.EnabledCount} enabled; default: {selected}";
    }

    private void SetChatInputRequired(ChatSessionView chat, bool isRequired)
    {
        chat.HasPendingUserInput = isRequired;
        SetTabBusyIndicator(chat, chat.IsPending);
    }

    private static bool HasPendingPrompt(ChatSessionView chat) =>
        chat.Messages.Any(message => message.Prompt is { IsAnswered: false });

    private bool CancelPendingPromptsForChat(ChatSessionView chat)
    {
        var promptIds = _pendingPromptChats
            .Where(pair => ReferenceEquals(pair.Value, chat))
            .Select(pair => pair.Key)
            .ToArray();

        if (promptIds.Length == 0)
        {
            return false;
        }

        foreach (var promptId in promptIds)
        {
            _pendingPromptChats.Remove(promptId);

            if (_pendingPermissionPrompts.Remove(promptId, out var permissionTcs))
            {
                permissionTcs.TrySetResult(PermissionPromptDecision.Deny);
            }

            if (_pendingUserInputPrompts.Remove(promptId, out var inputTcs))
            {
                inputTcs.TrySetResult(new UserInputPromptResult("", WasFreeform: false));
            }

            var message = chat.Messages.FirstOrDefault(message =>
                message.Id == promptId &&
                message.Prompt is { IsAnswered: false });
            if (message?.Prompt is { } promptState)
            {
                promptState.IsAnswered = true;
                promptState.Answer = "Cancelled";
                message.CompletedAt = DateTimeOffset.Now;
            }
        }

        return true;
    }

    private static PermissionPromptDecision ParsePermissionDecision(string value) =>
        Enum.TryParse<PermissionPromptDecision>(value, ignoreCase: true, out var decision)
            ? decision
            : PermissionPromptDecision.Deny;

    private void InitializeTrayNotifications()
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        try
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "Copilot Chatbot",
                Visible = true
            };
            _notifyIcon.BalloonTipClicked += (_, _) => ActivateUserResponseWindow();
            _notifyIcon.Click += (_, _) => ActivateUserResponseWindow();
        }
        catch (Exception ex)
        {
            _debugLogger.Log("TRAY-NOTIFICATION-INIT-ERROR", ex.Message);
        }
    }

    private void ApplyTrayNotificationSetting()
    {
        if (_settings.EnableTrayNotifications)
        {
            InitializeTrayNotifications();
            return;
        }

        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    private static System.Drawing.Icon? CreateTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using var icon = new System.Drawing.Icon(resource.Stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // Fall through to the executable icon.
        }

        return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
               ?? System.Drawing.SystemIcons.Application;
    }

    private void ShowUserResponseNotification(string title, string message, Window? responseWindow = null)
    {
        if (responseWindow is not null)
        {
            TrackActiveResponseWindow(responseWindow);
        }
        else
        {
            _activeResponseWindow = this;
        }

        try
        {
            if (_notifyIcon is null)
            {
                if (_settings.EnableTrayNotifications)
                {
                    InitializeTrayNotifications();
                }
            }

            if (_notifyIcon is null)
            {
                _debugLogger.Log("TRAY-NOTIFICATION-SKIPPED", "NotifyIcon is not available or tray notifications are disabled.");
                return;
            }

            _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = TruncateForBalloon(message);
            _notifyIcon.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(8000);
        }
        catch (Exception ex)
        {
            _debugLogger.Log("TRAY-NOTIFICATION-SHOW-ERROR", ex.Message);
        }
    }

    private void TrackActiveResponseWindow(Window responseWindow)
    {
        _activeResponseWindow = responseWindow;
        responseWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeResponseWindow, responseWindow))
            {
                _activeResponseWindow = null;
            }
        };
    }

    private void ActivateUserResponseWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var window = _activeResponseWindow ?? this;
            BringWindowToFront(window);
        });
    }

    private static void BringWindowToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;

        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            SetForegroundWindow(helper.Handle);
        }
    }

    private static string BuildPermissionNotificationText(PermissionPrompt prompt)
    {
        var scope = !string.IsNullOrWhiteSpace(prompt.Host)
            ? $"Network host: {prompt.Host}"
            : !string.IsNullOrWhiteSpace(prompt.ToolName)
                ? $"Tool: {prompt.ToolName}"
                : !string.IsNullOrWhiteSpace(prompt.FileName)
                    ? $"File or folder: {prompt.FileName}"
                    : prompt.Commands.Count > 0
                        ? $"Shell commands: {string.Join(", ", prompt.Commands.Select(command => command.Identifier))}"
                        : !string.IsNullOrWhiteSpace(prompt.Command)
                            ? "Shell command access"
                            : "Copilot requested permission.";

        return string.IsNullOrWhiteSpace(prompt.SessionTitle)
            ? scope
            : $"{prompt.SessionTitle}: {scope}";
    }

    private static string BuildUserInputNotificationText(UserInputPrompt prompt)
    {
        var suffix = prompt.Choices.Count > 0
            ? $" ({prompt.Choices.Count} choice{(prompt.Choices.Count == 1 ? "" : "s")})"
            : prompt.AllowFreeform
                ? " (freeform answer)"
                : "";
        var text = prompt.Question + suffix;

        return string.IsNullOrWhiteSpace(prompt.SessionTitle)
            ? text
            : $"{prompt.SessionTitle}: {text}";
    }

    private static string BuildPermissionPromptArticleText(PermissionPrompt prompt)
    {
        var lines = new List<string>
        {
            "Copilot requested permission.",
            "",
            $"Kind: {prompt.Kind}"
        };

        if (!string.IsNullOrWhiteSpace(prompt.ToolName))
            lines.Add($"Tool: {prompt.ToolName}");
        if (!string.IsNullOrWhiteSpace(prompt.FileName))
            lines.Add($"File or folder: {prompt.FileName}");
        if (!string.IsNullOrWhiteSpace(prompt.Host))
            lines.Add($"Network host: {prompt.Host}");
        if (prompt.Commands.Count > 0)
        {
            lines.Add("Shell commands:");
            lines.AddRange(prompt.Commands.Select(command =>
                $"  - {command.Identifier}{(command.ReadOnly ? " (read-only)" : "")}"));
        }
        if (!string.IsNullOrWhiteSpace(prompt.Command))
            lines.Add($"Command: {prompt.Command}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string TruncateForBalloon(string text)
    {
        const int maxLength = 240;
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private ChatSessionView? CurrentChat => (ChatTabs.SelectedItem as TabItem)?.Tag as ChatSessionView;

    private void UpdateWindowTitle()
    {
        if (CurrentChat is not { } chat)
        {
            Title = AppTitle;
            return;
        }

        var sessionTitle = string.IsNullOrWhiteSpace(chat.Title) ? "Chat" : chat.Title;
        if (string.Equals(chat.ProjectId, PersistedChatProject.DefaultProjectId, StringComparison.OrdinalIgnoreCase))
        {
            Title = $"{AppTitle} - {sessionTitle}";
            return;
        }

        var projectName = _projects.FirstOrDefault(project =>
                string.Equals(project.Id, chat.ProjectId, StringComparison.OrdinalIgnoreCase))
            ?.Name;
        projectName = string.IsNullOrWhiteSpace(projectName) ? chat.ProjectId : projectName;
        Title = $"{AppTitle} - {projectName} : {sessionTitle}";
    }

    private void UpdateInputState()
    {
        if (CurrentChat is { } chat)
        {
            GetTabContent(chat)?.SetState(chat.IsPending, chat.IsSessionMissing);
            UpdateModelControlsForChat(chat);
        }
        else
        {
            UpdateModelControlsForChat(null);
        }
        UpdateWindowTitle();
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

    private void StagePreviousArticleAutoCollapse(ChatSessionView chat)
    {
        if (!chat.AutoCollapsePreviousArticle)
        {
            return;
        }

        if (GetLastTopLevelArticleId(FilterMessages(chat.Messages)) is not { } articleId)
        {
            return;
        }

        if (!_forceClosedArticleIds.TryGetValue(chat, out var ids))
        {
            ids = [];
            _forceClosedArticleIds[chat] = ids;
        }

        ids.Add(articleId);
    }

    private static string? GetLastTopLevelArticleId(IEnumerable<ChatMessage> messages)
    {
        string? currentUserId = null;
        string? lastArticleId = null;

        foreach (var message in messages)
        {
            if (message.Kind is ChatMessageKind.User)
            {
                if (currentUserId is not null)
                {
                    lastArticleId = currentUserId;
                }

                currentUserId = message.Id;
            }
            else if (currentUserId is null)
            {
                lastArticleId = message.Id;
            }
        }

        return currentUserId ?? lastArticleId;
    }

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
            var shouldScrollToBottom = _scrollToBottomAfterInitialRender.Remove(chat);
            if (shouldScrollToBottom)
            {
                ScrollToBottomAfterNextNavigation(chat);
            }

            chat.Browser.NavigateToString(_htmlRenderer.RenderDocument(messages, _isDarkTheme));
            return;
        }

        // Incremental update: patch message nodes in-place (append/update/reorder/remove)
        // and preserve the scroll position.
        try
        {
            var payload = BuildBrowserMessagePatches(chat, messages).ToArray();
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
                                    if (typeof initLiveFrames === 'function') initLiveFrames(curBody);
                                }

                                if (oldOpen !== null) {
                                    curDetails.open = next.hasAttribute('data-force-closed') ? false : oldOpen;
                                }
                            } else {
                                next.dataset.renderSig = patch.Signature;
                                node.replaceWith(next);
                                node = next;
                                if (oldOpen !== null) {
                                    var replacedDetails = node.querySelector('details');
                                    if (replacedDetails) replacedDetails.open = next.hasAttribute('data-force-closed') ? false : oldOpen;
                                }
                                Array.from(node.querySelectorAll('article[data-mid]')).forEach(function(article) {
                                    var details = article.querySelector('details');
                                    if (details && Object.prototype.hasOwnProperty.call(openStates, article.dataset.mid)) {
                                        details.open = article.hasAttribute('data-force-closed') ? false : openStates[article.dataset.mid];
                                    }
                                });
                            }
                        }

                        if (patch.ForceClosed) {
                            var forceClosedDetails = node.querySelector('details');
                            if (forceClosedDetails) forceClosedDetails.open = false;
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

                    if (typeof initLiveFrames === 'function') initLiveFrames(m);
                    document.querySelectorAll('iframe.live-iframe').forEach(f => {
                        if (typeof autoSizeLiveIframe === 'function') autoSizeLiveIframe(f, false);
                    });
                    el.scrollTop = atBottom ? el.scrollHeight : savedY;
                })()
            """);
            ClearAppliedForceClosedArticleIds(chat, payload);
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

    private static void ScrollToBottomAfterNextNavigation(ChatSessionView chat)
    {
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            chat.Browser.NavigationCompleted -= Handler;
            _ = chat.Browser.ExecuteScriptAsync("""
                requestAnimationFrame(() => {
                  requestAnimationFrame(() => window.scrollTo(0, document.documentElement.scrollHeight));
                });
                """);
        }

        chat.Browser.NavigationCompleted += Handler;
    }

    private static string BuildRenderSignature(ChatMessage message)
    {
        var completed = message.CompletedAt?.ToUnixTimeMilliseconds().ToString() ?? "";
        return string.Join("|",
            message.Kind.ToString(),
            message.Content,
            message.CreatedAt.ToUnixTimeMilliseconds().ToString(),
            completed,
            string.Join(",", (message.IframeHeights ?? []).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value:0}")));
    }

    private void ClearAppliedForceClosedArticleIds(ChatSessionView chat, IReadOnlyList<BrowserMessagePatch> payload)
    {
        if (!_forceClosedArticleIds.TryGetValue(chat, out var ids))
        {
            return;
        }

        foreach (var id in payload.Where(patch => patch.ForceClosed).Select(patch => patch.Id))
        {
            ids.Remove(id);
        }

        if (ids.Count == 0)
        {
            _forceClosedArticleIds.Remove(chat);
        }
    }

    private sealed record BrowserMessagePatch(string Id, string Signature, string Html, bool ForceClosed);

    private IEnumerable<BrowserMessagePatch> BuildBrowserMessagePatches(ChatSessionView chat, IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? currentUser = null;
        var responses = new List<ChatMessage>();
        _forceClosedArticleIds.TryGetValue(chat, out var forceClosedIds);

        foreach (var message in messages)
        {
            if (message.Kind is ChatMessageKind.User)
            {
                if (currentUser is not null)
                {
                    yield return BuildTurnPatch(currentUser, responses, forceClosedIds);
                    responses.Clear();
                }

                currentUser = message;
            }
            else if (currentUser is null)
            {
                yield return new BrowserMessagePatch(
                    message.Id,
                    BuildRenderSignature(message),
                    _htmlRenderer.RenderMessageFragment(message, _isDarkTheme),
                    forceClosedIds?.Contains(message.Id) == true);
            }
            else
            {
                responses.Add(message);
            }
        }

        if (currentUser is not null)
        {
            yield return BuildTurnPatch(currentUser, responses, forceClosedIds);
        }
    }

    private BrowserMessagePatch BuildTurnPatch(ChatMessage userMessage, IReadOnlyList<ChatMessage> responses, IReadOnlySet<string>? forceClosedIds)
    {
        var signature = string.Join("\n---turn-message---\n",
            responses.Prepend(userMessage).Select(BuildRenderSignature));
        return new BrowserMessagePatch(
            userMessage.Id,
            signature,
            _htmlRenderer.RenderTurnFragment(userMessage, responses, _isDarkTheme),
            forceClosedIds?.Contains(userMessage.Id) == true);
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

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == AppThemeMode.System &&
            e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.Invoke(ApplyThemeFromMode);
        }
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

        foreach (var responseWindow in _responseWindows.ToArray())
        {
            responseWindow.ApplyWindowTheme(dark);
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

    private sealed class ChatProjectView(string id, string name, bool isCollapsed)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public bool IsCollapsed { get; set; } = isCollapsed;
    }
}
