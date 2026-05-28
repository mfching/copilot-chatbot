using System.Windows;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace CopilotChatbot;

public partial class SettingsPasswordWindow : Window
{
    public string Password => PasswordTextBox.Visibility == Visibility.Visible
        ? PasswordTextBox.Text
        : PasswordBox.Password;

    public SettingsPasswordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void RevealPassword_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordTextBox.Visibility == Visibility.Collapsed)
        {
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            RevealPasswordIcon.Symbol = SymbolRegular.EyeOff20;
            PasswordTextBox.Focus();
            PasswordTextBox.CaretIndex = PasswordTextBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordTextBox.Text;
            PasswordTextBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            RevealPasswordIcon.Symbol = SymbolRegular.Eye20;
            PasswordBox.Focus();
        }
    }
}
