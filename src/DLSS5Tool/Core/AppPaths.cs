using System.Reflection;

namespace DLSS5Tool.Core;

/// <summary>Where the tool keeps its own state. Everything lives under %LOCALAPPDATA%\DLSS5Tool.</summary>
public static class AppPaths
{
    public const string RepoOwner = "trustxix";
    public const string RepoName = "DLSS5Tool";
    public const string JournalFileName = "DLSS5Tool-install.json";
    public const string BackupDirName = "DLSS5Tool-backup";
    public const string UserAgent = "DLSS5Tool (+https://github.com/trustxix/DLSS5Tool)";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLSS5Tool");

    public static string Cache => Path.Combine(Root, "cache");
    public static string LocalOverrides => Path.Combine(Root, "local");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Stage => Path.Combine(Root, "stage");
    public static string SelfTest => Path.Combine(Root, "selftest");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DLSS5Tool.exe");

    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            }
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Stage);
    }
}
