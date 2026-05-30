using System.Windows;
using System.Windows.Input;
using CopilotChatbot.Services;

namespace CopilotChatbot;

public partial class UserInputPromptWindow : Window
{
    private readonly bool _allowFreeform;

    public string Answer { get; private set; } = "";
    public bool WasFreeform { get; private set; } = true;

    public UserInputPromptWindow(UserInputPrompt prompt)
    {
        InitializeComponent();
        Title = FormatDialogTitle(prompt.SessionTitle, "Copilot Question");
        _allowFreeform = prompt.AllowFreeform;
        QuestionTextBlock.Text = prompt.Question;
        ChoicesListBox.ItemsSource = prompt.Choices;
        ChoicesPanel.Visibility = prompt.Choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UseSelectedButton.IsEnabled = prompt.Choices.Count > 0;
        FreeformPanel.Visibility = prompt.AllowFreeform ? Visibility.Visible : Visibility.Collapsed;
        SendAnswerButton.IsEnabled = prompt.AllowFreeform;

        if (prompt.Choices.Count > 0)
        {
            ChoicesListBox.SelectedIndex = 0;
        }

        Loaded += (_, _) =>
        {
            if (_allowFreeform)
            {
                AnswerTextBox.Focus();
            }
            else
            {
                ChoicesListBox.Focus();
            }
        };
    }

    private static string FormatDialogTitle(string? sessionTitle, string dialogTitle) =>
        string.IsNullOrWhiteSpace(sessionTitle)
            ? dialogTitle
            : $"{sessionTitle} - {dialogTitle}";

    private void UseSelected_Click(object sender, RoutedEventArgs e) => UseSelectedChoice();

    private void ChoicesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseSelectedChoice();

    private void SendAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowFreeform)
        {
            UseSelectedChoice();
            return;
        }

        Answer = AnswerTextBox.Text.Trim();
        WasFreeform = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Answer = "";
        WasFreeform = true;
        DialogResult = false;
    }

    private void UseSelectedChoice()
    {
        if (ChoicesListBox.SelectedItem is not string choice)
        {
            return;
        }

        Answer = choice;
        WasFreeform = false;
        DialogResult = true;
    }
}
