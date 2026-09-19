using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using InstantFind.Services;

namespace InstantFind;

public partial class App : Application
{
    static App()
    {
        // Earliest managed entry before OnStartup / MainWindow.
        StartupLog.AppendBootHeader("App.static_ctor");
        StartupLog.Append("StaticCtor.Begin");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ErrorLog.AppendUnhandled(
                "AppDomain.UnhandledException",
                e.ExceptionObject as Exception,
                isTerminating: e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.AppendUnhandled("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        StartupLog.Append("StaticCtor.ExceptionHooksRegistered");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Append("OnStartup.Begin", $"args={e.Args.Length}");

        DispatcherUnhandledException += App_DispatcherUnhandledException;

        try
        {
            SingleInstance.TryRegister();
            StartupLog.Append(
                "OnStartup.SingleInstance",
                $"otherObserved={SingleInstance.OtherInstanceObservedAtStartup}");
        }
        catch (Exception ex)
        {
            ErrorLog.AppendFailure("OnStartup.SingleInstance", ex);
            StartupLog.Append("OnStartup.SingleInstance.Failed", ex.Message);
        }

        base.OnStartup(e);
        StartupLog.Append("OnStartup.BaseComplete", "StartupUri will construct MainWindow");
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.AppendUnhandled("DispatcherUnhandledException", e.Exception);
        // Leave e.Handled=false so WPF still shuts down; log is already on disk.
    }
}
