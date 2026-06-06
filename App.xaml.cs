using Microsoft.UI.Xaml;

namespace PocketReader;

public partial class App : Application
{
    public App()
    {
        // DB init happens lazily in DatabaseService's ctor — avoid doing it twice at startup.
        this.InitializeComponent();
        this.UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:O}  {e.Message}\n{e.Exception}\n\n");
        }
        catch { }

        // Survive unexpected per-item/UI errors instead of closing the whole app.
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new MainWindow();
        m_window.Activate();
    }

    private Window m_window;
}
