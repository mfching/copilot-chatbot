using System.Windows;
using System.Windows.Input;

namespace CopilotChatbot;

public partial class RenameTabWindow : Window
{
    public string TabTitle => TitleTextBox.Text.Trim();

    public RenameTabWindow(string currentTitle)
        : this(currentTitle, "Rename Tab", "Tab name", "Rename")
    {
    }

    public RenameTabWindow(string currentTitle, string windowTitle, string fieldLabel, string actionText)
    {
        InitializeComponent();
        Title = windowTitle;
        FieldLabelTextBlock.Text = fieldLabel;
        ActionButton.Tag = actionText;
        TitleTextBox.Text = currentTitle;
        TitleTextBox.SelectAll();
        TitleTextBox.Focus();
        TitleTextBox.KeyDown += TitleTextBox_KeyDown;
    }

    private void TitleTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Rename_Click(sender, e);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TabTitle))
        {
            MessageBox.Show(this, "Enter a tab name.", "Rename Tab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
