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
        Application.Run(new MainForm());
        return 0;
    }
}
