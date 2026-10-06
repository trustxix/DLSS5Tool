using System.Diagnostics;
using DLSS5Tool.Core.Install;

namespace DLSS5Tool.Core.Analysis;

public static class GameAnalyzer
{
    private static readonly string[] ReShadeHostNames = { "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll", "d3d10.dll", "ddraw.dll", "d3d8.dll", "dinput8.dll" };
    private static readonly string[] ProxyNames = { "winmm.dll", "version.dll", "dbghelp.dll", "winhttp.dll", "wininet.dll", "d3d12.dll", "dxgi.dll", "d3d11.dll", "d3d9.dll", "optiscaler.dll", "optiscaler.asi", "nvngx.dll", "_nvngx.dll", "nvngx.dll_dlssnr.dll" };
    private static readonly string[] DlssRuntimeNames = { "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "sl.dlss.dll", "sl.common.dll" };

    public static GameProfile Analyze(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath)) throw new FileNotFoundException("Executable not found.", exePath);
        var dir = Path.GetDirectoryName(exePath)!;
        var pe = PeFile.TryRead(exePath) ?? throw new InvalidDataException($"{Path.GetFileName(exePath)} is not a valid Windows executable.");

        var journal = InstallJournal.TryLoad(dir);
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (journal != null)
        {
            foreach (var e in journal.Entries) ours.Add(Path.GetFullPath(Path.Combine(dir, e.Path)));
        }

        var apis = new HashSet<GraphicsApi>();
        var evidence = new List<string>();

        void AddFromImports(string label, IEnumerable<string> imports)
        {
            foreach (var imp in imports)
            {
                var api = GraphicsApiInfo.FromModuleName(imp);
                if (api == GraphicsApi.Unknown) continue;
                if (apis.Add(api)) evidence.Add($"{label} imports {imp}");
            }
        }

        AddFromImports(Path.GetFileName(exePath), pe.AllImports);

        // Engines that load their renderer at run time: look at the DLLs next to the executable.
        if (apis.Count == 0 || (apis.Count == 1 && apis.Contains(GraphicsApi.D3D9) && pe.AllImports.Contains("d3d9.dll")))
        {
            foreach (var dll in EnumerateEngineDlls(dir))
            {
                var dpe = PeFile.TryRead(dll);
                if (dpe == null) continue;
                AddFromImports(Path.GetFileName(dll), dpe.AllImports);
            }
        }

        if (apis.Count == 0)
        {
            var hints = PeFile.ScanApiStrings(exePath);
            var unity = Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(unity))
            {
                foreach (var kv in PeFile.ScanApiStrings(unity)) hints[kv.Key] = hints.GetValueOrDefault(kv.Key) + kv.Value;
            }
            foreach (var kv in hints.OrderByDescending(k => k.Value))
            {
                var api = GraphicsApiInfo.FromModuleName(kv.Key);
                if (api == GraphicsApi.Unknown) continue;
                if (apis.Add(api)) evidence.Add($"binary mentions {kv.Key} ({kv.Value}x)");
            }
        }

        var agility = File.Exists(Path.Combine(dir, "D3D12", "D3D12Core.dll"));
        if (agility && apis.Add(GraphicsApi.D3D12)) evidence.Add("ships a D3D12 Agility SDK redistributable (D3D12\\D3D12Core.dll)");

        var dxvk = false;
        foreach (var n in new[] { "d3d9.dll", "dxgi.dll", "d3d11.dll", "d3d10core.dll", "d3d8.dll" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p) && ProductName(p).Contains("DXVK", StringComparison.OrdinalIgnoreCase))
            {
                dxvk = true;
                evidence.Add($"{n} is DXVK (renders through Vulkan)");
            }
        }
        if (dxvk) apis.Add(GraphicsApi.Vulkan);

        var remix = File.Exists(Path.Combine(dir, ".trex", "NvRemixBridge.exe"));
        if (remix) evidence.Add("RTX Remix runtime present (.trex)");

        var primary = dxvk ? GraphicsApi.Vulkan : GraphicsApiInfo.PickPrimary(apis);

        var dlssFiles = new List<string>();
        var nativeDlss = false;
        var streamline = false;
        foreach (var f in FindFiles(dir, DlssRuntimeNames, 4))
        {
            if (ours.Contains(Path.GetFullPath(f))) continue;
            var name = Path.GetFileName(f).ToLowerInvariant();
            dlssFiles.Add(Path.GetRelativePath(dir, f));
            if (name is "nvngx_dlss.dll" or "sl.dlss.dll") nativeDlss = true;
            if (name is "sl.interposer.dll") streamline = true;
        }

        ReShadePresence? reshade = null;
        foreach (var n in ReShadeHostNames)
        {
            var p = Path.Combine(dir, n);
            if (!File.Exists(p) || ours.Contains(Path.GetFullPath(p))) continue;
            if (!ProductName(p).Equals("ReShade", StringComparison.OrdinalIgnoreCase)) continue;
            var ver = FileVersion(p);
            var exports = PeFile.TryRead(p, readExports: true)?.Exports;
            bool? addons = exports == null ? null : exports.Contains("ReShadeRegisterAddon") && exports.Contains("ReShadeUnregisterAddon");
            reshade = new ReShadePresence(p, ver, addons);
            break;
        }

        var dgv = false;
        foreach (var n in new[] { "D3D9.dll", "D3D8.dll", "DDraw.dll", "Glide2x.dll", "Glide3x.dll" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p) && !ours.Contains(Path.GetFullPath(p)) && ProductName(p).Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase)) { dgv = true; break; }
        }

        var foreign = new List<ForeignFile>();
        foreach (var n in ProxyNames)
        {
            var p = Path.Combine(dir, n);
            if (!File.Exists(p) || ours.Contains(Path.GetFullPath(p))) continue;
            var product = ProductName(p);
            if (product.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase))
            {
                foreign.Add(new ForeignFile(p, product, "OptiScaler captures every NGX call in the process; it cannot coexist with the DLSS 5 add-on"));
            }
            else if (n.Equals("optiscaler.dll", StringComparison.OrdinalIgnoreCase) || n.Equals("optiscaler.asi", StringComparison.OrdinalIgnoreCase) || n.Equals("nvngx.dll_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            {
                foreign.Add(new ForeignFile(p, product.Length == 0 ? "OptiScaler" : product, "leftover OptiScaler file"));
            }
            else if (product.Contains("Special K", StringComparison.OrdinalIgnoreCase))
            {
                foreign.Add(new ForeignFile(p, product, "Special K is loaded as a proxy; it is known to conflict with ReShade add-ons"));
            }
        }
        foreach (var n in new[] { "deep-fried-chicken.addon64", "renodx-dlss.addon64" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p) && !ours.Contains(Path.GetFullPath(p)))
            {
                foreign.Add(new ForeignFile(p, n, "another neural-rendering add-on; only one may be loaded"));
            }
        }

        return new GameProfile
        {
            ExePath = exePath,
            GameDir = dir,
            Is64Bit = pe.Is64Bit,
            Architecture = pe.Architecture,
            Apis = apis,
            Evidence = evidence,
            PrimaryApi = primary,
            HasNativeDlss = nativeDlss,
            HasStreamline = streamline,
            NativeDlssFiles = dlssFiles,
            Dxvk = dxvk,
            RtxRemix = remix,
            AgilitySdkRedist = agility,
            ReShade = reshade,
            DgVoodooPresent = dgv,
            ForeignFiles = foreign,
            Journal = journal,
            OwnedFiles = ours,
        };
    }

    /// <summary>DLLs next to the executable, then those in its immediate subfolders (Source engine keeps its renderer in bin\).</summary>
    private static IEnumerable<string> EnumerateEngineDlls(string dir)
    {
        var files = new List<string>();
        try { files.AddRange(Directory.GetFiles(dir, "*.dll")); } catch { yield break; }
        try
        {
            foreach (var sub in Directory.GetDirectories(dir).OrderByDescending(s => Path.GetFileName(s).StartsWith("bin", StringComparison.OrdinalIgnoreCase)).Take(12))
            {
                var leaf = Path.GetFileName(sub);
                if (leaf.Equals(AppPaths.BackupDirName, StringComparison.OrdinalIgnoreCase) || leaf.Equals("host64", StringComparison.OrdinalIgnoreCase)) continue;
                try { files.AddRange(Directory.GetFiles(sub, "*.dll")); } catch { /* unreadable subfolder */ }
            }
        }
        catch { /* unreadable folder */ }
        var count = 0;
        foreach (var f in files.OrderByDescending(f => Path.GetFileName(f).StartsWith("Unity", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("Engine", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("shaderapi", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            if (GraphicsApiInfo.FromModuleName(name) != GraphicsApi.Unknown) continue; // a local d3d9.dll etc. is a wrapper, not the engine
            // Wrappers and add-ons say nothing about what the engine itself renders with (Valve ships dxvk_d3d9.dll for Proton; it is unused on Windows).
            if (name.StartsWith("reshade") || name.StartsWith("nvngx") || name.StartsWith("sl.") || name.StartsWith("dlss5") || name.StartsWith("renodx") || name.StartsWith("dxvk") || name.StartsWith("dgvoodoo") || name.StartsWith("d3d8to9")) continue;
            long size;
            try { size = new FileInfo(f).Length; } catch { continue; }
            if (size > 96L * 1024 * 1024) continue;
            if (++count > 80) yield break;
            yield return f;
        }
    }

    private static IEnumerable<string> FindFiles(string root, string[] names, int maxDepth)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string, int)>();
        pending.Push((root, 0));
        var dirs = 0;
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            if (++dirs > 3000) yield break;
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                if (wanted.Contains(Path.GetFileName(f))) yield return f;
            }
            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var s in subs)
            {
                var leaf = Path.GetFileName(s);
                if (leaf.Equals(AppPaths.BackupDirName, StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push((s, depth + 1));
            }
        }
    }

    public static string ProductName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string? FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch { return null; }
    }
}
