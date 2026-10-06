using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DLSS5Tool.Core.Games;

/// <summary>
/// Finds installed games from every launcher that leaves a machine-readable trace:
/// Steam, Epic, GOG, Ubisoft Connect, EA app, Battle.net, Xbox app, Rockstar, Amazon Games.
/// Read-only; never touches the launchers themselves.
/// </summary>
public static class GameScanner
{
    public static List<GameInfo> ScanAll(Action<string>? status = null)
    {
        var all = new List<GameInfo>();
        void Run(string launcher, Func<IEnumerable<GameInfo>> scan)
        {
            status?.Invoke($"Scanning {launcher}...");
            try
            {
                var found = scan().ToList();
                Log.Info($"{launcher}: {found.Count} game(s)");
                all.AddRange(found);
            }
            catch (Exception ex)
            {
                Log.Warn($"{launcher} scan failed: {ex.Message}");
            }
        }

        Run("Steam", ScanSteam);
        Run("Epic Games", ScanEpic);
        Run("GOG", ScanGog);
        Run("Ubisoft Connect", ScanUbisoft);
        Run("EA", ScanEa);
        Run("Battle.net", ScanBattleNet);
        Run("Xbox", ScanXbox);
        Run("Rockstar", ScanRockstar);
        Run("Amazon Games", ScanAmazon);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GameInfo>();
        foreach (var g in all)
        {
            if (string.IsNullOrWhiteSpace(g.InstallDir) || !Directory.Exists(g.InstallDir)) continue;
            var key = Path.GetFullPath(g.InstallDir).TrimEnd('\\').ToLowerInvariant();
            if (!seen.Add(key)) continue;
            result.Add(g);
        }
        return result.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fills ExePath/Candidates for a game whose launcher did not declare an executable.</summary>
    public static void ResolveExecutable(GameInfo game)
    {
        if (game.Candidates.Count > 0) return;
        game.Candidates = ExeLocator.FindCandidates(game.InstallDir, game.ExePath);
        if (game.ExePath == null || !File.Exists(game.ExePath))
        {
            game.ExePath = game.Candidates.FirstOrDefault()?.Path;
        }
    }

    // ---------------------------------------------------------------- Steam

    public static IEnumerable<GameInfo> ScanSteam()
    {
        var steamPath = ReadReg(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadReg(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadReg(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (steamPath == null) yield break;
        steamPath = steamPath.Replace('/', '\\');
        var libraries = new List<string> { steamPath };
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var root = VdfNode.Parse(File.ReadAllText(vdfPath));
            var folders = root["libraryfolders"] ?? root["LibraryFolders"];
            if (folders != null)
            {
                foreach (var lib in folders.Children)
                {
                    var p = lib.Get("path") ?? lib.Value;
                    if (!string.IsNullOrWhiteSpace(p)) libraries.Add(p.Replace("\\\\", "\\").Replace('/', '\\'));
                }
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                VdfNode? state;
                try { state = VdfNode.Parse(File.ReadAllText(acf))["AppState"]; } catch { continue; }
                if (state == null) continue;
                var name = state.Get("name") ?? string.Empty;
                var dir = state.Get("installdir");
                var appId = state.Get("appid");
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (IsSteamTool(name)) continue;
                var full = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(full) || !seen.Add(full)) continue;
                yield return new GameInfo { Name = name, Launcher = "Steam", InstallDir = full, AppId = appId };
            }
        }
    }

    private static bool IsSteamTool(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("redistributable") || n.Contains("steamworks") || n.StartsWith("proton") || n.Contains("steam linux runtime")
               || n.Contains("steamvr") || n.Contains(" sdk") || n.EndsWith(" tool") || n.EndsWith(" tools") || n.Contains("dedicated server") || n.Contains("soundtrack")
               || n.Contains("workshop tool") || n.Contains("editor") || n.Contains("benchmark");
    }

    // ---------------------------------------------------------------- Epic

    public static IEnumerable<GameInfo> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(item)); } catch { continue; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
                var loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                if (string.IsNullOrWhiteSpace(loc)) continue;
                var name = r.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                var exe = r.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                var appName = r.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                var mainApp = r.TryGetProperty("MainGameAppName", out var ma) ? ma.GetString() : null;
                if (!string.IsNullOrEmpty(mainApp) && !string.IsNullOrEmpty(appName) && mainApp != appName) continue; // DLC
                yield return new GameInfo
                {
                    Name = name ?? Path.GetFileName(loc),
                    Launcher = "Epic Games",
                    InstallDir = loc,
                    AppId = appName,
                    ExePath = string.IsNullOrWhiteSpace(exe) ? null : Path.GetFullPath(Path.Combine(loc, exe)),
                };
            }
        }
    }

    // ---------------------------------------------------------------- GOG

    public static IEnumerable<GameInfo> ScanGog()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var games = hklm.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games == null) continue;
            foreach (var id in games.GetSubKeyNames())
            {
                using var k = games.OpenSubKey(id);
                if (k == null) continue;
                var depends = k.GetValue("dependsOn") as string;
                if (!string.IsNullOrWhiteSpace(depends)) continue; // DLC
                var path = k.GetValue("path") as string;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var name = k.GetValue("gameName") as string ?? Path.GetFileName(path);
                var exe = k.GetValue("exe") as string;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    var exeFile = k.GetValue("exeFile") as string;
                    if (!string.IsNullOrWhiteSpace(exeFile)) exe = Path.Combine(path, exeFile);
                }
                yield return new GameInfo { Name = name, Launcher = "GOG", InstallDir = path, AppId = id, ExePath = exe != null && File.Exists(exe) ? exe : null };
            }
        }
    }

    // ---------------------------------------------------------------- Ubisoft

    public static IEnumerable<GameInfo> ScanUbisoft()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var installs = hklm.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (installs == null) yield break;
        using var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            var dir = k?.GetValue("InstallDir") as string;
            if (string.IsNullOrWhiteSpace(dir)) continue;
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            string? name = null;
            using (var u = uninstall?.OpenSubKey("Uplay Install " + id))
            {
                name = u?.GetValue("DisplayName") as string;
            }
            yield return new GameInfo { Name = name ?? Path.GetFileName(dir), Launcher = "Ubisoft Connect", InstallDir = dir, AppId = id };
        }
    }

    // ---------------------------------------------------------------- EA

    public static IEnumerable<GameInfo> ScanEa()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        foreach (var root in new[] { @"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts\EA Games", @"SOFTWARE\Electronic Arts" })
        {
            using var k = hklm.OpenSubKey(root);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var g = k.OpenSubKey(sub);
                var dir = g?.GetValue("Install Dir") as string ?? g?.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                var name = g?.GetValue("DisplayName") as string ?? sub;
                yield return new GameInfo { Name = name, Launcher = "EA", InstallDir = dir.TrimEnd('\\') };
            }
        }
        foreach (var g in ScanUninstallEntries(e => (e.publisher ?? string.Empty).Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) && !(e.name ?? string.Empty).Contains("EA app", StringComparison.OrdinalIgnoreCase), "EA"))
        {
            yield return g;
        }
    }

    // ---------------------------------------------------------------- Battle.net

    public static IEnumerable<GameInfo> ScanBattleNet()
    {
        return ScanUninstallEntries(e => (e.uninstall ?? string.Empty).Contains("Battle.net", StringComparison.OrdinalIgnoreCase)
                                         && !(e.name ?? string.Empty).Equals("Battle.net", StringComparison.OrdinalIgnoreCase), "Battle.net");
    }

    // ---------------------------------------------------------------- Rockstar

    public static IEnumerable<GameInfo> ScanRockstar()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var k = hklm.OpenSubKey(@"SOFTWARE\Rockstar Games");
        if (k == null) yield break;
        foreach (var sub in k.GetSubKeyNames())
        {
            if (sub.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || sub.Contains("Social Club", StringComparison.OrdinalIgnoreCase)) continue;
            using var g = k.OpenSubKey(sub);
            var dir = g?.GetValue("InstallFolder") as string ?? g?.GetValue("InstallFolderSteam") as string;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            yield return new GameInfo { Name = sub, Launcher = "Rockstar", InstallDir = dir.TrimEnd('\\') };
        }
    }

    // ---------------------------------------------------------------- Amazon

    public static IEnumerable<GameInfo> ScanAmazon()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var uninstall = hkcu.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall == null) yield break;
        foreach (var sub in uninstall.GetSubKeyNames())
        {
            if (!sub.StartsWith("AmazonGames/", StringComparison.OrdinalIgnoreCase)) continue;
            using var k = uninstall.OpenSubKey(sub);
            var dir = k?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var name = k?.GetValue("DisplayName") as string ?? sub["AmazonGames/".Length..];
            yield return new GameInfo { Name = name, Launcher = "Amazon Games", InstallDir = dir.TrimEnd('\\') };
        }
    }

    // ---------------------------------------------------------------- Xbox

    public static IEnumerable<GameInfo> ScanXbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            string root;
            try { root = drive.RootDirectory.FullName; } catch { continue; }
            var roots = new List<string>();
            var gamingRoot = Path.Combine(root, ".GamingRoot");
            if (File.Exists(gamingRoot))
            {
                foreach (var folder in ParseGamingRoot(gamingRoot)) roots.Add(Path.Combine(root, folder));
            }
            roots.Add(Path.Combine(root, "XboxGames"));
            foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(r)) continue;
                foreach (var gameDir in Directory.EnumerateDirectories(r))
                {
                    var content = Path.Combine(gameDir, "Content");
                    if (!Directory.Exists(content)) continue;
                    var config = Path.Combine(content, "MicrosoftGame.config");
                    string? exe = null;
                    string? name = null;
                    if (File.Exists(config))
                    {
                        try
                        {
                            var x = XDocument.Load(config);
                            var exeName = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
                            if (!string.IsNullOrWhiteSpace(exeName)) exe = Path.Combine(content, exeName.Replace('/', '\\'));
                            name = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value;
                            if (string.IsNullOrWhiteSpace(name)) name = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                        }
                        catch { /* malformed config: fall back to folder name */ }
                    }
                    if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) name = null;
                    yield return new GameInfo
                    {
                        Name = name ?? Path.GetFileName(gameDir),
                        Launcher = "Xbox",
                        InstallDir = content,
                        ExePath = exe != null && File.Exists(exe) ? exe : null,
                    };
                }
            }
        }
    }

    /// <summary>.GamingRoot: "RGBX", a 32-bit count, then length-prefixed UTF-16 folder names.</summary>
    internal static List<string> ParseGamingRoot(string path)
    {
        var list = new List<string>();
        try
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 8 || b[0] != 'R' || b[1] != 'G' || b[2] != 'B' || b[3] != 'X') return list;
            var count = BitConverter.ToInt32(b, 4);
            var pos = 8;
            for (var i = 0; i < count && pos + 4 <= b.Length; i++)
            {
                var len = BitConverter.ToInt32(b, pos);
                pos += 4;
                if (len <= 0 || pos + len * 2 > b.Length) break;
                var s = Encoding.Unicode.GetString(b, pos, len * 2).TrimEnd('\0');
                pos += len * 2;
                if (s.Length > 0) list.Add(s);
            }
            if (list.Count == 0)
            {
                var s = Encoding.Unicode.GetString(b, 8, b.Length - 8).Trim('\0', '\r', '\n', ' ');
                if (s.Length > 0 && s.Length < 200 && s.IndexOfAny(Path.GetInvalidPathChars()) < 0) list.Add(s);
            }
        }
        catch { /* unreadable: ignore */ }
        return list;
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<GameInfo> ScanUninstallEntries(Func<(string? name, string? publisher, string? uninstall, string? location), bool> filter, string launcher)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(sub);
                if (k == null) continue;
                var e = (name: k.GetValue("DisplayName") as string, publisher: k.GetValue("Publisher") as string, uninstall: k.GetValue("UninstallString") as string, location: k.GetValue("InstallLocation") as string);
                if (string.IsNullOrWhiteSpace(e.name) || string.IsNullOrWhiteSpace(e.location)) continue;
                if (!filter(e)) continue;
                var dir = e.location.Trim('"').TrimEnd('\\');
                if (!Directory.Exists(dir)) continue;
                yield return new GameInfo { Name = e.name, Launcher = launcher, InstallDir = dir };
            }
        }
    }

    private static string? ReadReg(RegistryHive hive, string subKey, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var k = baseKey.OpenSubKey(subKey);
            return k?.GetValue(value) as string;
        }
        catch
        {
            return null;
        }
    }
}
