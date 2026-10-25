using System.Windows;
using System.Windows.Threading;
using ParadisResize.Core.Util;
using ParadisResize.Services;
using ParadisResize.UI;

namespace ParadisResize;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Initialize();
        Log.Info($"Paradis Resize starting (v{AppInfo.Version}) on {Environment.OSVersion.VersionString}, .NET {Environment.Version}, " +
                 $"{Environment.ProcessorCount} CPU threads, {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_000_000:N0} MB memory");

        CoreLog.Info = Log.Info;
        CoreLog.Warn = Log.Warn;
        CoreLog.Error = Log.Error;

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) window.LoadFromCommandLine(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Paradis Resize exiting");
        Log.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        Dialogs.Error(MainWindow, "Something went wrong",
            "Paradis Resize hit an unexpected problem. Your original images are untouched.\n\n" +
            $"Technical details are in:\n{Log.Directory}");
    }
}
