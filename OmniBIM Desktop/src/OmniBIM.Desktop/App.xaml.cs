using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OmniBIM.Desktop;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "omnibim-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(CrashLogPath, $"DispatcherUnhandledException:\n{args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            File.WriteAllText(CrashLogPath, $"AppDomain UnhandledException:\n{args.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            File.AppendAllText(CrashLogPath, $"\n\nUnobservedTaskException:\n{args.Exception}");
        };
    }
}
