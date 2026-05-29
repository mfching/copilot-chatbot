using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CopilotChatbot.Models;
using CopilotChatbot.Services;

namespace CopilotChatbot;

public partial class SchedulerHistoryWindow : Window
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly TaskSchedulerService _scheduler;
    private readonly ScheduledTask _task;

    public SchedulerHistoryWindow(TaskSchedulerService scheduler, ScheduledTask task)
    {
        InitializeComponent();
        _scheduler = scheduler;
        _task = task;
        HeaderText.Text = $"History: {task.Name}";
        LoadHistory();
    }

    private void LoadHistory()
    {
        var records = _scheduler.LoadHistory(_task.Id);
        RunsList.ItemsSource = records;
        NoRunsPlaceholder.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (records.Count > 0) RunsList.SelectedIndex = 0;
        else DetailBox.Text = "(no runs yet)";
    }

    private void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunsList.SelectedItem is TaskRunRecord rec)
        {
            DetailBox.Text = JsonSerializer.Serialize(rec, PrettyJson);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadHistory();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
