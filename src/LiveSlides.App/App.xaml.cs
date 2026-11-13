using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LiveSlides.App;

public partial class App : Application
{
    // LIVESLIDES_DATA overrides the folder: lets a second, fully separate instance (own settings,
    // own remote port) run for testing next to the one being used for a show.
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("LIVESLIDES_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSlides");

    /// <summary>%LOCALAPPDATA%\LiveSlides\errors.log — every unexpected error is appended here.</summary>
    public static string ErrorLogPath { get; } = Path.Combine(DataFolder, "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // A show app must never close in the middle of a show because of one bad file:
        // unexpected errors are logged and reported in the status bar, and the app keeps running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogError(ex);
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;

        var message = $"Errore: {e.Exception.Message} (dettagli in {ErrorLogPath})";
        if (MainWindow?.DataContext is MainViewModel viewModel)
            viewModel.StatusMessage = message;
        else
            MessageBox.Show(message, "LiveSlides", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never throw
        }
    }
}
