using Microsoft.UI.Xaml;

namespace CardForge;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogUnhandled("XAML", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogUnhandled("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogUnhandled("Task", e.Exception);
            e.SetObserved();
        };
    }

    static void LogUnhandled(string source, Exception error)
    {
        try
        {
            Directory.CreateDirectory(Services.AppPaths.Logs);
            File.AppendAllText(Path.Combine(Services.AppPaths.Logs, "app.log"),
                $"{DateTime.Now:O} [{source}] {error}\n\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Main = new MainWindow();
        Main.Activate();
    }
}
