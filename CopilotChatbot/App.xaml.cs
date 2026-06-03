using System.Windows;
using System.Windows.Threading;

namespace CopilotChatbot;

public partial class App : Application
{
    public static string? CommandLineGitHubToken { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        CommandLineGitHubToken = TryReadGitHubToken(e.Args);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static string? TryReadGitHubToken(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--ghtoken=", StringComparison.OrdinalIgnoreCase))
            {
                return CleanToken(arg["--ghtoken=".Length..]);
            }

            if (arg.Equals("--ghtoken", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return CleanToken(args[i + 1]);
            }
        }

        return null;
    }

    private static string? CleanToken(string? token)
    {
        token = token?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Unhandled application error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
