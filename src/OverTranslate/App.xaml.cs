using System.Threading;
using System.Windows;
using NLog;
using OverTranslate.Services;
using OverTranslate.Models;
using System.Threading.Tasks;
using Velopack;
using OverTranslate.Views;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Translation;

namespace OverTranslate;

public partial class App
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string ActivateEventName = "OverTranslate_Activate_9F3C7B2E";

    private EventWaitHandle? _activateEvent;

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unhandled UI exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.ExceptionObject as Exception, "Unhandled domain exception");
            LogManager.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unobserved Task exception");
            ex.SetObserved();
        };
        _activateEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, ActivateEventName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show the window and exit
            _activateEvent.Set();
            _activateEvent.Dispose();
            Shutdown();
            return;
        }

        // Background thread listens for activation signals from future launch attempts
        new Thread(MonitorActivationRequests) { IsBackground = true }.Start();

        ThemeService.Apply(SettingsService.Instance.Current.Theme);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.InitializeApp();

        _ = CheckForUpdateAsync();
    }

    private static async Task CheckForUpdateAsync()
    {
        var info = await UpdateService.CheckAsync();
        if (info is null) return;
        Current.Dispatcher.Invoke(() => new UpdateWindow(info).Show());
    }

    private void MonitorActivationRequests()
    {
        try
        {
            while (_activateEvent!.WaitOne())
            {
                if (Dispatcher.HasShutdownStarted) break;
                Dispatcher.Invoke(ShowOrActivateShell);
            }
        }
        catch { /* event disposed on exit — normal shutdown */ }
    }

    internal static void ShowOrActivateShell() => ShellWindow.ShowOrActivate(ShellPage.Translation);

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        base.OnExit(e);
    }
}
