using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    public AppSettings Settings { get; }

    public SettingsWindow(SettingsStore store, AppSettings settings, DebugLogger debugLogger)
    {
        InitializeComponent();
        _store = store;
        _debugLogger = debugLogger;
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        GitHubTokenBox.Password = Settings.GitHubToken ?? "";
        SecretsGrid.ItemsSource = Settings.UserSecrets;
        FoldersGrid.ItemsSource = Settings.Permissions.Folders;
        ToolsList.ItemsSource = Settings.Permissions.AllowedTools;
        HostsList.ItemsSource = Settings.Permissions.AllowedHosts;
        SavedRulesGrid.ItemsSource = Settings.Permissions.SavedRules;
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
    }

    private void AddSecret_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SecretEnvBox.Text))
        {
            return;
        }

        Settings.UserSecrets.Add(new UserSecretSetting
        {
            Name = SecretNameBox.Text.Trim(),
            EnvironmentVariable = SecretEnvBox.Text.Trim(),
            EncryptedValue = _store.ProtectSecret(GetSecretValue())
        });
        SecretNameBox.Clear();
        SecretEnvBox.Clear();
        SecretValueBox.Clear();
        SecretValueTextBox.Clear();
    }

    private void RemoveSecret_Click(object sender, RoutedEventArgs e)
    {
        if (SecretsGrid.SelectedItem is UserSecretSetting secret)
        {
            Settings.UserSecrets.Remove(secret);
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

    private void AddTool_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.Permissions.AllowedTools, ToolBox.Text);
        ToolBox.Clear();
    }

    private void ClearTools_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.AllowedTools.Clear();
    }

    private void AddHost_Click(object sender, RoutedEventArgs e)
    {
        AddUnique(Settings.Permissions.AllowedHosts, HostBox.Text);
        HostBox.Clear();
    }

    private void ClearHosts_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.AllowedHosts.Clear();
    }

    private void RemoveSavedRule_Click(object sender, RoutedEventArgs e)
    {
        if (SavedRulesGrid.SelectedItem is PermissionRule rule)
        {
            Settings.Permissions.SavedRules.Remove(rule);
        }
    }

    private void ClearSavedRules_Click(object sender, RoutedEventArgs e)
    {
        Settings.Permissions.SavedRules.Clear();
    }

    private static void AddUnique(ICollection<string> list, string value)
    {
        value = value.Trim();
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
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
        Settings.DefaultSystemPrompt = string.IsNullOrWhiteSpace(SystemPromptBox.Text) ? null : SystemPromptBox.Text;
        Settings.EnableDebugLogging = EnableDebugLoggingCheckBox.IsChecked == true;
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
