using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CopilotChatbot.Models;
using CopilotChatbot.Services;
using Microsoft.Win32;

namespace CopilotChatbot;

public partial class SchedulerWindow : Window
{
    private readonly TaskSchedulerService _scheduler;
    private readonly List<ModelChoice> _models;
    private ScheduledTask? _current;

    public SchedulerWindow(TaskSchedulerService scheduler, IEnumerable<ModelChoice>? models = null)
    {
        InitializeComponent();
        _scheduler = scheduler;
        _models = models?.ToList() ?? [];

        // Model dropdown — same source as MainWindow
        CopModel.ItemsSource = _models;
        if (_models.Count == 0)
            CopModel.IsEnabled = false;

        TasksList.ItemsSource = _scheduler.Tasks;
        CronBox.TextChanged += (_, _) => UpdateNextRunsPreview();

        // Auto-fill CWD when exe path changes (req 5)
        PreExe.TextChanged += (_, _) => AutoFillCwd(PreExe, PreCwd);
        PostExe.TextChanged += (_, _) => AutoFillCwd(PostExe, PostCwd);

        if (_scheduler.Tasks.Count > 0) TasksList.SelectedIndex = 0;
        else SetEditorEnabled(false);
    }

    // ── Enable/disable editor ─────────────────────────────────────────────

    private void SetEditorEnabled(bool enabled)
    {
        DetailStack.IsEnabled = enabled;
        RunNowButton.IsEnabled = enabled;
        HistoryButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    // ── List selection ────────────────────────────────────────────────────

    private void TasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = TasksList.SelectedItem as ScheduledTask;
        if (_current is null) { SetEditorEnabled(false); return; }
        SetEditorEnabled(true);
        LoadIntoEditor(_current);
    }

    // ── Per-row inline clone / delete (req 8) ─────────────────────────────

    private void TaskCloneButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScheduledTask task) return;
        if (ReferenceEquals(task, _current)) ApplyEditorTo(_current);
        var json = JsonSerializer.Serialize(task);
        var copy = JsonSerializer.Deserialize<ScheduledTask>(json)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = task.Name + " (copy)";
        _scheduler.Tasks.Add(copy);
        _scheduler.SaveTasks();
        TasksList.SelectedItem = copy;
        e.Handled = true; // don't let the click also change selection unexpectedly
    }

    private void TaskDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScheduledTask task) return;
        if (MessageBox.Show($"Delete task '{task.Name}'?", "Confirm delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _scheduler.Tasks.Remove(task);
        _scheduler.SaveTasks();
        if (ReferenceEquals(task, _current)) { _current = null; SetEditorEnabled(false); }
        e.Handled = true;
    }

    // ── Load / save task ↔ editor form ────────────────────────────────────

    private void LoadIntoEditor(ScheduledTask task)
    {
        NameBox.Text = task.Name;
        EnabledCheck.IsChecked = task.Enabled;
        CronBox.Text = task.CronExpression ?? "";

        // Pre-command
        var pre = task.PreCommand ?? new TaskCommandSpec();
        PreEnabled.IsChecked = task.PreCommand is not null;
        PreExe.Text = pre.Executable;
        PreArgs.Text = pre.ArgsTemplate;
        PreCwd.Text = pre.WorkingDirectory ?? "";
        PreStdin.SelectedIndex = pre.StdinSource == TaskStdinSource.Literal ? 1 : 0;
        PreStdinLiteral.Text = pre.StdinLiteral ?? "";
        PreTimeout.Text = pre.TimeoutSeconds.ToString();

        // Copilot
        var cop = task.Copilot ?? new TaskCopilotSpec();
        CopilotEnabled.IsChecked = task.Copilot is not null;
        CopTab.Text = cop.ChatTabName;
        // Model dropdown
        CopModel.SelectedItem = _models.FirstOrDefault(m => m.Id == cop.ModelId);
        // Reasoning — populated via CopModel_SelectionChanged; set after
        CopReasoning.SelectedItem = cop.ReasoningEffort;
        CopSystem.Text = cop.SystemPrompt ?? "";
        CopPrompt.Text = cop.Prompt;
        CopAppendPrev.IsChecked = cop.AppendPreviousOutput;
        CopTimeout.Text = cop.TimeoutSeconds.ToString();

        // Post-command
        var post = task.PostCommand ?? new TaskCommandSpec();
        PostEnabled.IsChecked = task.PostCommand is not null;
        PostExe.Text = post.Executable;
        PostArgs.Text = post.ArgsTemplate;
        PostCwd.Text = post.WorkingDirectory ?? "";
        PostStdin.SelectedIndex = post.StdinSource switch
        {
            TaskStdinSource.CopilotResponse => 1,
            TaskStdinSource.PreviousOutput  => 2,
            TaskStdinSource.Literal         => 3,
            _                               => 0
        };
        PostStdinLiteral.Text = post.StdinLiteral ?? "";
        PostTimeout.Text = post.TimeoutSeconds.ToString();

        // Handoff
        var h = task.ExternalHandoff ?? new TaskHandoffSpec();
        HandoffKind.SelectedIndex = h.Kind switch
        {
            TaskHandoffKind.File      => 1,
            TaskHandoffKind.HttpPost  => 2,
            TaskHandoffKind.NamedPipe => 3,
            _                         => 0
        };
        HandoffTarget.Text = h.Target;
        HandoffAppend.IsChecked = h.FileAppend;
        HandoffMethod.Text = string.IsNullOrWhiteSpace(h.HttpMethod) ? "POST" : h.HttpMethod;
        HandoffBody.Text = h.BodyTemplate;

        UpdateHandoffMethodEnabled();
        UpdateNextRunsPreview();
    }

    private void ApplyEditorTo(ScheduledTask task)
    {
        task.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Untitled" : NameBox.Text.Trim();
        task.Enabled = EnabledCheck.IsChecked == true;
        task.CronExpression = string.IsNullOrWhiteSpace(CronBox.Text) ? null : CronBox.Text.Trim();

        task.PreCommand = PreEnabled.IsChecked == true ? new TaskCommandSpec
        {
            Executable        = PreExe.Text.Trim(),
            ArgsTemplate      = PreArgs.Text,
            WorkingDirectory  = NullIfBlank(PreCwd.Text),
            StdinSource       = PreStdin.SelectedIndex == 1 ? TaskStdinSource.Literal : TaskStdinSource.None,
            StdinLiteral      = PreStdinLiteral.Text,
            TimeoutSeconds    = ParseInt(PreTimeout.Text, 120)
        } : null;

        task.Copilot = CopilotEnabled.IsChecked == true ? new TaskCopilotSpec
        {
            ChatTabName         = CopTab.Text.Trim(),
            ModelId             = (CopModel.SelectedItem as ModelChoice)?.Id,
            ReasoningEffort     = CopReasoning.SelectedItem?.ToString(),
            SystemPrompt        = NullIfBlank(CopSystem.Text),
            Prompt              = CopPrompt.Text,
            AppendPreviousOutput = CopAppendPrev.IsChecked == true,
            TimeoutSeconds      = ParseInt(CopTimeout.Text, 300)
        } : null;

        task.PostCommand = PostEnabled.IsChecked == true ? new TaskCommandSpec
        {
            Executable       = PostExe.Text.Trim(),
            ArgsTemplate     = PostArgs.Text,
            WorkingDirectory = NullIfBlank(PostCwd.Text),
            StdinSource      = PostStdin.SelectedIndex switch
            {
                1 => TaskStdinSource.CopilotResponse,
                2 => TaskStdinSource.PreviousOutput,
                3 => TaskStdinSource.Literal,
                _ => TaskStdinSource.None
            },
            StdinLiteral   = PostStdinLiteral.Text,
            TimeoutSeconds = ParseInt(PostTimeout.Text, 120)
        } : null;

        var kind = HandoffKind.SelectedIndex switch
        {
            1 => TaskHandoffKind.File,
            2 => TaskHandoffKind.HttpPost,
            3 => TaskHandoffKind.NamedPipe,
            _ => TaskHandoffKind.None
        };
        task.ExternalHandoff = kind == TaskHandoffKind.None ? null : new TaskHandoffSpec
        {
            Kind         = kind,
            Target       = HandoffTarget.Text.Trim(),
            FileAppend   = HandoffAppend.IsChecked == true,
            HttpMethod   = string.IsNullOrWhiteSpace(HandoffMethod.Text) ? "POST" : HandoffMethod.Text.Trim(),
            BodyTemplate = HandoffBody.Text
        };
    }

    // ── Cron validation + next-runs preview (req 1) ───────────────────────

    private void UpdateNextRunsPreview()
    {
        if (string.IsNullOrWhiteSpace(CronBox.Text))
        {
            CronValidation.Text = "";
            NextRunsText.Text = "(no cron expression — manual only)";
            return;
        }
        var runs = _scheduler.GetNextOccurrencesUtc(CronBox.Text, 5);
        if (runs.Count == 0)
        {
            CronValidation.Text = "⚠ Invalid cron expression";
            CronValidation.Foreground = new SolidColorBrush(Colors.OrangeRed);
            NextRunsText.Text = "";
            return;
        }
        CronValidation.Text = "✓ Valid";
        CronValidation.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71)); // MediumSeaGreen
        NextRunsText.Text = "Next: " + string.Join("  |  ",
            runs.Select(d => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
    }
    // ── Handoff kind → HTTP method enable/disable ───────────────────────────────

    private void HandoffKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateHandoffMethodEnabled();

    private void UpdateHandoffMethodEnabled()
    {
        if (HandoffMethod is null) return;
        HandoffMethod.IsEnabled = HandoffKind.SelectedIndex == 2; // index 2 = HTTP
    }

    // ── Cron quick-pick shortcuts ───────────────────────────────────────────────

    private void CronShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string cron)
            CronBox.Text = cron;
    }
    // ── Model → reasoning dropdown cascade (req 3) ────────────────────────

    private void CopModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CopModel.SelectedItem is not ModelChoice model)
        {
            CopReasoning.ItemsSource = null;
            CopReasoning.IsEnabled = false;
            return;
        }
        CopReasoning.ItemsSource = model.SupportsReasoningEffort
            ? (model.ReasoningEfforts.Count > 0
                ? (IEnumerable<string>)model.ReasoningEfforts
                : ["low", "medium", "high", "xhigh"])
            : Array.Empty<string>();
        CopReasoning.IsEnabled = model.SupportsReasoningEffort;
    }

    // ── Exe / folder browse (req 4 + 5) ──────────────────────────────────

    private void PreExeBrowse_Click(object sender, RoutedEventArgs e)  => BrowseExecutable(PreExe,  PreCwd);
    private void PostExeBrowse_Click(object sender, RoutedEventArgs e) => BrowseExecutable(PostExe, PostCwd);
    private void PreCwdBrowse_Click(object sender, RoutedEventArgs e)  => BrowseFolder(PreCwd);
    private void PostCwdBrowse_Click(object sender, RoutedEventArgs e) => BrowseFolder(PostCwd);

    private static void BrowseExecutable(TextBox exeBox, TextBox cwdBox)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select executable",
            Filter = "Executables and scripts (*.exe;*.bat;*.cmd;*.ps1;*.sh;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.sh;*.py|All files (*.*)|*.*"
        };
        if (File.Exists(exeBox.Text))
            dlg.InitialDirectory = Path.GetDirectoryName(exeBox.Text);
        if (dlg.ShowDialog() != true) return;
        exeBox.Text = dlg.FileName;
        // Default CWD to exe's folder when CWD is empty (req 5)
        if (string.IsNullOrWhiteSpace(cwdBox.Text))
            cwdBox.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
    }

    private static void BrowseFolder(TextBox cwdBox)
    {
        var dlg = new OpenFolderDialog { Title = "Select working directory" };
        if (Directory.Exists(cwdBox.Text)) dlg.InitialDirectory = cwdBox.Text;
        if (dlg.ShowDialog() == true) cwdBox.Text = dlg.FolderName;
    }

    // Auto-fill CWD from exe path when CWD is empty
    private static void AutoFillCwd(TextBox exeBox, TextBox cwdBox)
    {
        if (!string.IsNullOrWhiteSpace(cwdBox.Text)) return;
        var dir = Path.GetDirectoryName(exeBox.Text.Trim());
        if (!string.IsNullOrEmpty(dir)) cwdBox.Text = dir;
    }

    // ── Action buttons ────────────────────────────────────────────────────

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var t = new ScheduledTask { Name = "New task " + (_scheduler.Tasks.Count + 1) };
        _scheduler.Tasks.Add(t);
        _scheduler.SaveTasks();
        TasksList.SelectedItem = t;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        ApplyEditorTo(_current);
        _scheduler.SaveTasks();
        StatusText.Text = "Saved.";
        var idx = TasksList.SelectedIndex;
        TasksList.Items.Refresh();
        TasksList.SelectedIndex = idx;
    }

    private async void RunNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        ApplyEditorTo(_current);
        _scheduler.SaveTasks();
        StatusText.Text = "Running…";
        RunNowButton.IsEnabled = false;
        try
        {
            var rec = await _scheduler.RunAsync(_current, "manual", CancellationToken.None);
            StatusText.Text = $"Run {rec.Status} at {rec.FinishedAt:HH:mm:ss}" +
                (rec.Error is null ? "" : " — " + rec.Error);
        }
        finally { RunNowButton.IsEnabled = true; }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        new SchedulerHistoryWindow(_scheduler, _current) { Owner = this }.Show();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var i) && i > 0 ? i : fallback;
}

