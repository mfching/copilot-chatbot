using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Win32;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace CopilotChatbot;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly DebugLogger _debugLogger;
    private readonly ICollectionView _secretsView;
    private readonly ICollectionView _toolsView;
    private readonly ICollectionView _hostsView;
    private readonly ICollectionView _savedRulesView;
    private bool _updatingSecretEditor;
    public AppSettings Settings { get; }

    public SettingsWindow(SettingsStore store, AppSettings settings, DebugLogger debugLogger)
    {
        InitializeComponent();
        _store = store;
        _debugLogger = debugLogger;
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        SortUserSecrets(Settings.UserSecrets);
        SortStringCollection(Settings.Permissions.AllowedTools);
        SortStringCollection(Settings.Permissions.AllowedHosts);
        SortSavedRules(Settings.Permissions.SavedRules);
        GitHubTokenBox.Password = Settings.GitHubToken ?? "";
        SecretsGrid.ItemsSource = Settings.UserSecrets;
        _secretsView = CollectionViewSource.GetDefaultView(SecretsGrid.ItemsSource);
        _secretsView.SortDescriptions.Add(new SortDescription(nameof(UserSecretSetting.Name), ListSortDirection.Ascending));
        _secretsView.SortDescriptions.Add(new SortDescription(nameof(UserSecretSetting.EnvironmentVariable), ListSortDirection.Ascending));
        _secretsView.Filter = SecretMatchesFilter;
        FoldersGrid.ItemsSource = Settings.Permissions.Folders;
        ToolsList.ItemsSource = Settings.Permissions.AllowedTools;
        _toolsView = CollectionViewSource.GetDefaultView(ToolsList.ItemsSource);
        _toolsView.SortDescriptions.Add(new SortDescription("", ListSortDirection.Ascending));
        _toolsView.Filter = ToolMatchesFilter;
        HostsList.ItemsSource = Settings.Permissions.AllowedHosts;
        _hostsView = CollectionViewSource.GetDefaultView(HostsList.ItemsSource);
        _hostsView.SortDescriptions.Add(new SortDescription("", ListSortDirection.Ascending));
        _hostsView.Filter = HostMatchesFilter;
        SavedRulesGrid.ItemsSource = Settings.Permissions.SavedRules;
        _savedRulesView = CollectionViewSource.GetDefaultView(SavedRulesGrid.ItemsSource);
        _savedRulesView.SortDescriptions.Add(new SortDescription(nameof(PermissionRule.Kind), ListSortDirection.Ascending));
        _savedRulesView.SortDescriptions.Add(new SortDescription(nameof(PermissionRule.Summary), ListSortDirection.Ascending));
        _savedRulesView.Filter = SavedRuleMatchesFilter;
        AllowMcpCheckBox.IsChecked = Settings.Permissions.AllowMcpByDefault;
        AllowCustomToolsCheckBox.IsChecked = Settings.Permissions.AllowCustomToolsByDefault;
        SystemPromptBox.Text = Settings.DefaultSystemPrompt ?? "";
        EnableDebugLoggingCheckBox.IsChecked = Settings.EnableDebugLogging;
        WorkingDirectoryBox.Text = Settings.WorkingDirectory ?? "";
        if (_store.HasActiveSettingsPassword)
        {
            SettingsPasswordBox.Password = _store.SettingsPasswordForSession;
            SettingsPasswordHelpText.Text = "A settings password is active from startup. Saved GitHub credentials and user secrets will use it unless you enter a new password here.";
        }
        AgentDirsList.ItemsSource = Settings.AgentDirectories;
        SkillDirsList.ItemsSource = Settings.SkillDirectories;
        LogPathTextBlock.Text = _debugLogger.CurrentLogPath;
        ThemeComboBox.ItemsSource = new[] { "Light", "Dark", "System", "Follow the sun" };
        ThemeComboBox.SelectedIndex = (int)Settings.Theme;
        ResponseBufferingCheckBox.IsChecked = Settings.EnableResponseBuffering;
        ResponseBufferIntervalSlider.Value = Math.Clamp(Settings.ResponseBufferIntervalMs <= 0 ? 1000 : Settings.ResponseBufferIntervalMs, 500, 2000);
        TrayNotificationsCheckBox.IsChecked = Settings.EnableTrayNotifications;
        DefaultAutoCollapseCheckBox.IsChecked = Settings.DefaultAutoCollapsePreviousArticle;
        UpdateResponseBufferControls();
    }

    private void AddSecret_Click(object sender, RoutedEventArgs e)
    {
        if (UpsertSecretFromEditor())
            ClearSecretEditor();
    }

    private void RemoveSecret_Click(object sender, RoutedEventArgs e)
    {
        if (SecretsGrid.SelectedItem is UserSecretSetting secret)
        {
            Settings.UserSecrets.Remove(secret);
            _secretsView.Refresh();
            ClearSecretEditor();
        }
    }

    private void SecretDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: UserSecretSetting secret })
        {
            Settings.UserSecrets.Remove(secret);
            _secretsView.Refresh();
            if (ReferenceEquals(SecretsGrid.SelectedItem, secret))
                ClearSecretEditor();
        }
    }

    private void SecretsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSecretEditor)
        {
            return;
        }

        if (SecretsGrid.SelectedItem is not UserSecretSetting secret)
        {
            return;
        }

        _updatingSecretEditor = true;
        try
        {
            SecretNameBox.Text = secret.Name;
            SecretEnvBox.Text = secret.EnvironmentVariable;
            SecretValueBox.Password = _store.UnprotectSecret(secret.EncryptedValue);
            SecretValueTextBox.Text = SecretValueBox.Password;
            SecretValueTextBox.Visibility = Visibility.Collapsed;
            SecretValueBox.Visibility = Visibility.Visible;
            RevealSecretIcon.Symbol = SymbolRegular.Eye20;
        }
        finally
        {
            _updatingSecretEditor = false;
        }
    }

    private bool UpsertSecretFromEditor()
    {
        var envName = SecretEnvBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(envName))
        {
            return false;
        }

        var displayName = SecretNameBox.Text.Trim();
        var value = GetSecretValue();
        var selected = SecretsGrid.SelectedItem as UserSecretSetting;
        var existing = selected is not null && Settings.UserSecrets.Contains(selected)
            ? selected
            : Settings.UserSecrets.FirstOrDefault(secret =>
                string.Equals(secret.EnvironmentVariable, envName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Settings.UserSecrets.Add(new UserSecretSetting
            {
                Name = displayName,
                EnvironmentVariable = envName,
                EncryptedValue = _store.ProtectSecret(value)
            });
        }
        else
        {
            existing.Name = displayName;
            existing.EnvironmentVariable = envName;
            existing.EncryptedValue = _store.ProtectSecret(value);
        }

        SortUserSecrets(Settings.UserSecrets);
        _secretsView.Refresh();
        return true;
    }

    private bool CommitPendingSecretEditor()
    {
        if (string.IsNullOrWhiteSpace(SecretNameBox.Text) &&
            string.IsNullOrWhiteSpace(SecretEnvBox.Text) &&
            string.IsNullOrWhiteSpace(GetSecretValue()))
        {
            return false;
        }

        return UpsertSecretFromEditor();
    }

    private void ClearSecretEditor()
    {
        _updatingSecretEditor = true;
        try
        {
            SecretsGrid.SelectedItem = null;
            SecretNameBox.Clear();
            SecretEnvBox.Clear();
            SecretValueBox.Clear();
            SecretValueTextBox.Clear();
            SecretValueTextBox.Visibility = Visibility.Collapsed;
            SecretValueBox.Visibility = Visibility.Visible;
            RevealSecretIcon.Symbol = SymbolRegular.Eye20;
        }
        finally
        {
            _updatingSecretEditor = false;
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FolderPathBox.Text))
        {
            Settings.Permissions.Folders.Add(new FolderPermission { Path = FolderPathBox.Text.Trim(), CanWrite = FolderWriteBox.IsChecked == true });
            FolderPathBox.Clear();
            FolderWriteBox.IsChecked = false;
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersGrid.SelectedItem is FolderPermission folder)
        {
            Settings.Permissions.Folders.Remove(folder);
        }
    }

    private void FolderDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FolderPermission folder })
        {
            Settings.Permissions.Folders.Remove(folder);
        }
    }

    private void AddTool_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.Permissions.AllowedTools, ToolBox.Text);
        SortStringCollection(Settings.Permissions.AllowedTools);
        _toolsView.Refresh();
        ToolBox.Clear();
    }

    private void ClearTools_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.AllowedTools.Clear();
        _toolsView.Refresh();
    }

    private void ToolDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string tool })
        {
            Settings.Permissions.AllowedTools.Remove(tool);
            _toolsView.Refresh();
        }
    }

    private void ToolFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _toolsView.Refresh();
    }

    private void AddHost_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.Permissions.AllowedHosts, HostBox.Text);
        SortStringCollection(Settings.Permissions.AllowedHosts);
        _hostsView.Refresh();
        HostBox.Clear();
    }

    private void ClearHosts_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.AllowedHosts.Clear();
        _hostsView.Refresh();
    }

    private void HostDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string host })
        {
            Settings.Permissions.AllowedHosts.Remove(host);
            _hostsView.Refresh();
        }
    }

    private void HostFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _hostsView.Refresh();
    }

    private void RemoveSavedRule_Click(object sender, RoutedEventArgs e)
    {
        if (SavedRulesGrid.SelectedItem is PermissionRule rule)
        {
            Settings.Permissions.SavedRules.Remove(rule);
            _savedRulesView.Refresh();
        }
    }

    private void SavedRuleDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PermissionRule rule })
        {
            Settings.Permissions.SavedRules.Remove(rule);
            _savedRulesView.Refresh();
        }
    }

    private void ClearSavedRules_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.SavedRules.Clear();
        _savedRulesView.Refresh();
    }

    private void SecretFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _secretsView.Refresh();
    }

    private void SavedRuleFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _savedRulesView.Refresh();
    }

    private static void AddUnique(ICollection<string> list, string value)
    {
        value = value.Trim();
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }

    private bool HostMatchesFilter(object item)
    {
        if (item is not string host)
        {
            return false;
        }

        var filter = HostFilterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(filter) ||
               host.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool ToolMatchesFilter(object item)
    {
        if (item is not string tool)
        {
            return false;
        }

        var filter = ToolFilterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(filter) ||
               tool.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool SecretMatchesFilter(object item)
    {
        if (item is not UserSecretSetting secret)
        {
            return false;
        }

        var filter = SecretFilterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(filter) ||
               secret.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               secret.EnvironmentVariable.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool SavedRuleMatchesFilter(object item)
    {
        if (item is not PermissionRule rule)
        {
            return false;
        }

        var filter = SavedRuleFilterBox.Text.Trim();
        return string.IsNullOrWhiteSpace(filter) ||
               rule.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               rule.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static void SortStringCollection(ObservableCollection<string> values)
    {
        var sorted = values
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < sorted.Length; i++)
        {
            if (!string.Equals(values[i], sorted[i], StringComparison.Ordinal))
            {
                values[i] = sorted[i];
            }
        }
    }

    private static void SortUserSecrets(ObservableCollection<UserSecretSetting> values)
    {
        SortCollection(values, Comparer<UserSecretSetting>.Create((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            return byName != 0
                ? byName
                : StringComparer.OrdinalIgnoreCase.Compare(left.EnvironmentVariable, right.EnvironmentVariable);
        }));
    }

    private static void SortSavedRules(ObservableCollection<PermissionRule> values)
    {
        SortCollection(values, Comparer<PermissionRule>.Create((left, right) =>
        {
            var byKind = StringComparer.OrdinalIgnoreCase.Compare(left.Kind, right.Kind);
            return byKind != 0
                ? byKind
                : StringComparer.OrdinalIgnoreCase.Compare(left.Summary, right.Summary);
        }));
    }

    private static void SortCollection<T>(ObservableCollection<T> values, IComparer<T> comparer)
        where T : class
    {
        var sorted = values.Order(comparer).ToArray();
        for (var targetIndex = 0; targetIndex < sorted.Length; targetIndex++)
        {
            if (ReferenceEquals(values[targetIndex], sorted[targetIndex]))
            {
                continue;
            }

            var currentIndex = values.IndexOf(sorted[targetIndex]);
            if (currentIndex >= 0)
            {
                values.Move(currentIndex, targetIndex);
            }
        }
    }

    private void RevealSecretButton_Click(object sender, RoutedEventArgs e)
    {
        if (SecretValueTextBox.Visibility == Visibility.Collapsed)
        {
            SecretValueTextBox.Text = SecretValueBox.Password;
            SecretValueBox.Visibility = Visibility.Collapsed;
            SecretValueTextBox.Visibility = Visibility.Visible;
            RevealSecretIcon.Symbol = SymbolRegular.EyeOff20;
        }
        else
        {
            SecretValueBox.Password = SecretValueTextBox.Text;
            SecretValueTextBox.Visibility = Visibility.Collapsed;
            SecretValueBox.Visibility = Visibility.Visible;
            RevealSecretIcon.Symbol = SymbolRegular.Eye20;
        }
    }

    private void RevealGitHubTokenButton_Click(object sender, RoutedEventArgs e)
    {
        if (GitHubTokenTextBox.Visibility == Visibility.Collapsed)
        {
            GitHubTokenTextBox.Text = GitHubTokenBox.Password;
            GitHubTokenBox.Visibility = Visibility.Collapsed;
            GitHubTokenTextBox.Visibility = Visibility.Visible;
            RevealGitHubTokenIcon.Symbol = SymbolRegular.EyeOff20;
        }
        else
        {
            GitHubTokenBox.Password = GitHubTokenTextBox.Text;
            GitHubTokenTextBox.Visibility = Visibility.Collapsed;
            GitHubTokenBox.Visibility = Visibility.Visible;
            RevealGitHubTokenIcon.Symbol = SymbolRegular.Eye20;
        }
    }

    private void RevealSettingsPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPasswordTextBox.Visibility == Visibility.Collapsed)
        {
            SettingsPasswordTextBox.Text = SettingsPasswordBox.Password;
            SettingsPasswordBox.Visibility = Visibility.Collapsed;
            SettingsPasswordTextBox.Visibility = Visibility.Visible;
            RevealSettingsPasswordIcon.Symbol = SymbolRegular.EyeOff20;
        }
        else
        {
            SettingsPasswordBox.Password = SettingsPasswordTextBox.Text;
            SettingsPasswordTextBox.Visibility = Visibility.Collapsed;
            SettingsPasswordBox.Visibility = Visibility.Visible;
            RevealSettingsPasswordIcon.Symbol = SymbolRegular.Eye20;
        }
    }

    private void RevealSecretGridButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not UserSecretSetting secret ||
            FindVisualParent<DataGridRow>(button) is not { } row ||
            FindVisualChild<TextBlock>(row, "SecretGridValueText") is not { } valueText)
        {
            return;
        }

        var isRevealed = button.Tag as string == "revealed";
        if (isRevealed)
        {
            valueText.Text = "********";
            valueText.Foreground = (Brush)FindResource("MutedTextBrush");
            SetRevealButtonIcon(button, SymbolRegular.Eye20);
            button.Tag = null;
            return;
        }

        valueText.Text = _store.UnprotectSecret(secret.EncryptedValue);
        valueText.Foreground = (Brush)FindResource("TextBrush");
        SetRevealButtonIcon(button, SymbolRegular.EyeOff20);
        button.Tag = "revealed";
    }

    private void ResponseBufferingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateResponseBufferControls();
    }

    private void ResponseBufferIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateResponseBufferControls();
    }

    private void UpdateResponseBufferControls()
    {
        if (ResponseBufferIntervalSlider is null || ResponseBufferIntervalText is null)
        {
            return;
        }

        var enabled = ResponseBufferingCheckBox.IsChecked == true;
        ResponseBufferIntervalSlider.IsEnabled = enabled;
        ResponseBufferIntervalText.Text = $"{(int)ResponseBufferIntervalSlider.Value} ms";
        ResponseBufferIntervalText.Opacity = enabled ? 1.0 : 0.55;
    }

    private static void SetRevealButtonIcon(Button button, SymbolRegular symbol)
    {
        if (button.Content is Wpf.Ui.Controls.SymbolIcon icon)
        {
            icon.Symbol = symbol;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match)
            {
                return match;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
            {
                return element;
            }

            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private string GetGitHubToken()
    {
        return GitHubTokenTextBox.Visibility == Visibility.Visible
            ? GitHubTokenTextBox.Text
            : GitHubTokenBox.Password;
    }

    private string GetSettingsPassword()
    {
        return SettingsPasswordTextBox.Visibility == Visibility.Visible
            ? SettingsPasswordTextBox.Text
            : SettingsPasswordBox.Password;
    }

    private string GetSecretValue()
    {
        return SecretValueTextBox.Visibility == Visibility.Visible
            ? SecretValueTextBox.Text
            : SecretValueBox.Password;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settingsPassword = GetSettingsPassword();
        if (string.IsNullOrEmpty(settingsPassword))
        {
            _store.ClearSettingsPassword();
        }
        else
        {
            _store.SetSettingsPassword(settingsPassword);
        }

        Settings.GitHubToken = GetGitHubToken();
        Settings.WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? null : WorkingDirectoryBox.Text.Trim();
        Settings.Permissions.AllowMcpByDefault = AllowMcpCheckBox.IsChecked == true;
        Settings.Permissions.AllowCustomToolsByDefault = AllowCustomToolsCheckBox.IsChecked == true;
        CommitPendingSecretEditor();
        SortUserSecrets(Settings.UserSecrets);
        SortStringCollection(Settings.Permissions.AllowedTools);
        SortStringCollection(Settings.Permissions.AllowedHosts);
        SortSavedRules(Settings.Permissions.SavedRules);
        Settings.DefaultSystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        Settings.EnableDebugLogging = EnableDebugLoggingCheckBox.IsChecked == true;
        Settings.EnableResponseBuffering = ResponseBufferingCheckBox.IsChecked == true;
        Settings.EnableTrayNotifications = TrayNotificationsCheckBox.IsChecked == true;
        Settings.DefaultAutoCollapsePreviousArticle = DefaultAutoCollapseCheckBox.IsChecked == true;
        Settings.ResponseBufferIntervalMs = Math.Clamp((int)ResponseBufferIntervalSlider.Value, 500, 2000);
        Settings.Theme = (AppThemeMode)(ThemeComboBox.SelectedIndex >= 0 ? ThemeComboBox.SelectedIndex : 2);
        DialogResult = true;
    }

    private void BrowseWorkingDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Working Directory",
            InitialDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : WorkingDirectoryBox.Text
        };
        if (dialog.ShowDialog() == true)
            WorkingDirectoryBox.Text = dialog.FolderName;
    }

    private void BrowseAgentDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Agent Folder" };
        if (dialog.ShowDialog() == true)
            AgentDirBox.Text = dialog.FolderName;
    }

    private void AddAgentDir_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.AgentDirectories, AgentDirBox.Text);
        AgentDirBox.Clear();
    }

    private void RemoveAgentDir_Click(object sender, RoutedEventArgs e)
    {
        if (AgentDirsList.SelectedItem is string dir)
            Settings.AgentDirectories.Remove(dir);
    }

    private void BrowseSkillDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Skill Folder" };
        if (dialog.ShowDialog() == true)
            SkillDirBox.Text = dialog.FolderName;
    }

    private void AddSkillDir_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.SkillDirectories, SkillDirBox.Text);
        SkillDirBox.Clear();
    }

    private void RemoveSkillDir_Click(object sender, RoutedEventArgs e)
    {
        if (SkillDirsList.SelectedItem is string dir)
            Settings.SkillDirectories.Remove(dir);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("explorer.exe", _debugLogger.LogDirectory);
        }
        catch
        {
            // Best-effort; ignore if explorer can't open.
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
