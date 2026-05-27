using System.Diagnostics;
using System.Text.Json;
using System.Windows;
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
            EncryptedValue = _store.ProtectSecret(SecretValueBox.Password)
        });
        SecretNameBox.Clear();
        SecretEnvBox.Clear();
        SecretValueBox.Clear();
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.GitHubToken = GitHubTokenBox.Password;
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
