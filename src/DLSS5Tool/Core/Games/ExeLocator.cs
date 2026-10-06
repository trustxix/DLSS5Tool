using System.Text.RegularExpressions;

namespace DLSS5Tool.Core.Games;

/// <summary>
/// Finds the executable ReShade must sit next to. Launchers rarely say which one it is, so every .exe in the
/// install tree is scored: Unreal shipping binaries and Unity players rank first, redistributables and crash
/// handlers last. The caller always gets the full ranked list so the user can override.
/// </summary>
public static class ExeLocator
{
    private static readonly Regex Blocklist = new(
        @"(^|[\\_\- .])(unins\w*|setup|install\w*|vc_?redist\w*|vcredist\w*|dxsetup|dxwebsetup|directx|dotnet\w*|ndp\d+|oalinst|physx\w*|" +
        @"crash\w*|report\w*|handler|easyanticheat\w*|eac_?\w*|battleye|beclient|beservice|bepinex|unitycrashhandler\w*|ue4prereq\w*|ueprereq\w*|" +
        @"uplay\w*|ubisoft\w*|steam\w*|epic\w*|gog\w*|galaxy\w*|origin\w*|eadesktop\w*|benchmark\w*|config\w*|settings|options|update\w*|patch\w*|" +
        @"helper\w*|service|tool\w*|editor|server|dedicated\w*|redist\w*|cefsharp\w*|java\w*|python\w*|node|nvidia\w*|activation|register|readme|manual|" +
        @"modman\w*|modorganizer|mo2|mod|mods|diag\w*|support\w*|cleanup|uninstall\w*|bootstrap\w*|updater|wallpaper\w*|screensaver|7z\w*|unrar|" +
        @"gameoverlay\w*|overlay\w*|anticheat\w*|xgameruntime|gamingservices\w*|wtf\w*|glview|viewer|fxtool|graphicsconfig|modtool\w*)([\\_\- .]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CamelBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex UnrealBuild = new(@"-(Win64|Win32|WinGDK|Windows)-(Shipping|Test|Debug|DebugGame|Development)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] SkipDirs =
    {
        "redist", "_commonredist", "redistributables", "_redist", "support", "tools", "installers", "installer", "_installer", "__installer",
        "directx", "vcredist", "easyanticheat", "dotnet", "prerequisites", "engine", "mods", "modding", "sdk", "docs", "manual", "soundtrack",
        "extras", "bonus", "wallpapers", "benchmark", "dgvoodoo", "host64", "layer", "dlss5tool-backup", "backup", "backups", "crashreport",
        "crashreportclient", "thirdparty", "third_party", "node_modules",
    };

    public static List<ExeCandidate> FindCandidates(string installDir, string? declaredExe = null, int maxDepth = 4)
    {
        var list = new List<ExeCandidate>();
        if (!Directory.Exists(installDir)) return list;
        var folderToken = Normalize(Path.GetFileName(installDir.TrimEnd('\\', '/')));

        foreach (var exe in EnumerateExes(installDir, maxDepth))
        {
            long size;
            try { size = new FileInfo(exe).Length; } catch { continue; }
            var pe = PeFile.TryRead(exe);
            if (pe == null) continue;
            if (pe.Subsystem != 2 && pe.Subsystem != 3) continue; // GUI or console only
            var name = Path.GetFileNameWithoutExtension(exe);
            var rel = Path.GetRelativePath(installDir, exe);
            var depth = rel.Count(c => c == '\\' || c == '/');
            var reasons = new List<string>();
            var score = 0;

            if (UnrealBuild.IsMatch(name)) { score += 60; reasons.Add("Unreal game binary"); }
            else if (Regex.IsMatch(rel, @"[\\/]Binaries[\\/]Win(64|32)[\\/]", RegexOptions.IgnoreCase)) { score += 30; reasons.Add("Unreal binaries folder"); }
            var dataDir = Path.Combine(Path.GetDirectoryName(exe)!, name + "_Data");
            if (Directory.Exists(dataDir)) { score += 45; reasons.Add("Unity player (" + name + "_Data)"); }
            if (File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "UnityPlayer.dll")) && !Directory.Exists(dataDir)) { score -= 20; }
            if (depth == 0) { score += 15; reasons.Add("in the game's root folder"); }
            var nameToken = Normalize(name);
            if (nameToken.Length >= 3 && (folderToken.Contains(nameToken) || nameToken.Contains(folderToken))) { score += 30; reasons.Add("name matches the game folder"); }
            if (size > 1 << 20) score += 10;
            score += (int)Math.Min(25, size / (10L << 20) * 5);
            if (pe.Is64Bit) score += 8;
            if (Blocklist.IsMatch(name) || Blocklist.IsMatch(CamelBoundary.Replace(name, " "))) { score -= 100; reasons.Add("looks like a helper/redistributable"); }
            if (name.EndsWith("launcher", StringComparison.OrdinalIgnoreCase) || name.StartsWith("launch", StringComparison.OrdinalIgnoreCase)) { score -= 40; reasons.Add("launcher"); }
            if (pe.Subsystem == 3) { score -= 30; reasons.Add("console application"); }
            if (pe.AllImports.Any(i => GraphicsApiInfo.FromModuleName(i) != GraphicsApi.Unknown)) { score += 25; reasons.Add("imports a graphics API"); }
            if (declaredExe != null && string.Equals(Path.GetFullPath(declaredExe), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) { score += 100; reasons.Insert(0, "declared by the launcher"); }

            list.Add(new ExeCandidate(exe, score, string.Join(", ", reasons), pe.Is64Bit, size));
        }

        return list.OrderByDescending(c => c.Score).ThenByDescending(c => c.Size).ToList();
    }

    public static string? Best(string installDir, string? declaredExe = null)
    {
        if (declaredExe != null && File.Exists(declaredExe)) return declaredExe;
        return FindCandidates(installDir, declaredExe).FirstOrDefault()?.Path;
    }

    private static IEnumerable<string> EnumerateExes(string root, int maxDepth)
    {
        var pending = new Stack<(string dir, int depth)>();
        pending.Push((root, 0));
        var count = 0;
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); } catch { continue; }
            foreach (var f in files)
            {
                if (++count > 400) yield break;
                yield return f;
            }
            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var s in subs)
            {
                var leaf = Path.GetFileName(s).ToLowerInvariant();
                if (SkipDirs.Contains(leaf) || leaf.StartsWith('.') || leaf.StartsWith("__")) continue;
                pending.Push((s, depth + 1));
            }
        }
    }

    private static string Normalize(string s)
    {
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }
}
