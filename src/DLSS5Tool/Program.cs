using DLSS5Tool.Core;
using DLSS5Tool.Core.Install;
using DLSS5Tool.Core.Update;
using DLSS5Tool.Ui;

namespace DLSS5Tool;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == VulkanLayer.ElevatedSwitch)
        {
            return VulkanLayer.ExecuteFromFile(args[1]);
        }

        Log.Init();
        Updater.CleanupAfterUpdate();

        var cliArgs = args.Where(a => !a.Equals("--updated", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cliArgs.Length > 0)
        {
            return Cli.Cli.Run(cliArgs);
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            MessageBox.Show(e.Exception.Message + (Log.FilePath != null ? "\n\nDetails: " + Log.FilePath : string.Empty), "DLSS 5 Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Unhandled exception", ex);
            else Log.Error("Unhandled exception: " + e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Application.Run(new MainForm());
        return 0;
    }
}
