using System.Text.RegularExpressions;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;

namespace DLSS5Tool.Core.Install;

public enum VerifyStatus { Ok, Warn, Fail }

public sealed record VerifyResult(VerifyStatus Status, string Text, string? Detail = null)
{
    public override string ToString() => $"[{Status.ToString().ToUpperInvariant(),-4}] {Text}" + (Detail == null ? string.Empty : "\n       " + Detail);
}

/// <summary>Static checks of an installed game folder plus a read of the runtime logs when the game has been launched.</summary>
public static class Verifier
{
    public static List<VerifyResult> Verify(string exePath, ComponentCatalog catalog, GpuInfo? gpu = null)
    {
        var r = new List<VerifyResult>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        var journal = InstallJournal.TryLoad(dir);
        if (journal == null)
        {
            r.Add(new(VerifyStatus.Fail, "This game was not installed by DLSS 5 Tool (no DLSS5Tool-install.json next to the executable)."));
            return r;
        }
        r.Add(new(VerifyStatus.Ok, $"Installed {journal.InstalledUtc:yyyy-MM-dd} by DLSS 5 Tool {journal.ToolVersion}: route {journal.Route}, {journal.Api}, {(journal.Is64Bit ? "64" : "32")}-bit."));

        var missing = journal.Entries.Where(e => e.Action is "created" or "replaced" or "merged" && !File.Exists(Path.Combine(dir, e.Path))).Select(e => e.Path).ToList();
        if (missing.Count > 0) r.Add(new(VerifyStatus.Fail, $"{missing.Count} installed file(s) are missing: {string.Join(", ", missing)}", "Reinstall to put them back."));
        else r.Add(new(VerifyStatus.Ok, $"All {journal.Entries.Count} journaled files are present."));

        var changed = journal.Entries.Where(e => e.Action == "created" && e.Sha256 != null && File.Exists(Path.Combine(dir, e.Path)) && !e.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !Hashing.Matches(Path.Combine(dir, e.Path), e.Sha256)).Select(e => e.Path).ToList();
        if (changed.Count > 0) r.Add(new(VerifyStatus.Warn, "Modified since install: " + string.Join(", ", changed)));

        var is32 = !journal.Is64Bit;
        var feeder = !journal.Route.Equals("NativeDlss64", StringComparison.OrdinalIgnoreCase);
        var consumerDir = is32 ? Path.Combine(dir, "host64") : dir;

        // ReShade
        if (journal.Api.Equals("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            var (dllOk, registered, listed, version) = VulkanLayer.Inspect(exePath, journal.Is64Bit);
            r.Add(new(dllOk ? VerifyStatus.Ok : VerifyStatus.Fail, dllOk ? $"ReShade Vulkan layer {version} with add-on support is installed machine-wide." : "ReShade's machine-wide Vulkan layer is missing or lacks add-on support.", VulkanLayer.DefaultRoot));
            r.Add(new(registered ? VerifyStatus.Ok : VerifyStatus.Fail, registered ? "Vulkan layer is registered in the registry." : "Vulkan layer is not registered (HKLM\\SOFTWARE\\Khronos\\Vulkan\\ImplicitLayers)."));
            r.Add(new(listed ? VerifyStatus.Ok : VerifyStatus.Fail, listed ? "Game is listed in ReShadeApps.ini." : "Game is not listed in ReShadeApps.ini; ReShade will not load for it."));
        }
        else
        {
            ReShadePresence? rs = null;
            foreach (var n in new[] { "dxgi.dll", "opengl32.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "d3d10.dll", "ddraw.dll", "dinput8.dll" })
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p) && GameAnalyzer.ProductName(p).Equals("ReShade", StringComparison.OrdinalIgnoreCase))
                {
                    var ex = PeFile.TryRead(p, readExports: true)?.Exports;
                    rs = new ReShadePresence(p, GameAnalyzer.FileVersion(p), ex == null ? null : ex.Contains("ReShadeRegisterAddon"));
                    break;
                }
            }
            if (rs == null) r.Add(new(VerifyStatus.Fail, "No ReShade DLL next to the executable."));
            else if (!rs.IsUsable) r.Add(new(VerifyStatus.Fail, $"ReShade {rs.Version} as {rs.FileName} is too old or has no add-on support.", rs.Path));
            else r.Add(new(VerifyStatus.Ok, $"ReShade {rs.Version} with add-on support as {rs.FileName}."));
            var pe = PeFile.TryRead(exePath);
            if (rs != null && pe != null)
            {
                var rpe = PeFile.TryRead(rs.Path);
                if (rpe != null && rpe.Is64Bit != pe.Is64Bit) r.Add(new(VerifyStatus.Fail, $"ReShade DLL is {(rpe.Is64Bit ? "64" : "32")}-bit but the game is {(pe.Is64Bit ? "64" : "32")}-bit; it will never load."));
            }
        }

        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) r.Add(new(VerifyStatus.Fail, "ReShade.ini is missing."));
        else
        {
            var f = IniFile.Load(ini);
            var addonPath = f.Get("ADDON", "AddonPath");
            r.Add(new(addonPath == ".\\" || addonPath == "." ? VerifyStatus.Ok : VerifyStatus.Warn, "ReShade.ini [ADDON] AddonPath=" + (addonPath ?? "(unset)")));
            var esp = f.Get("GENERAL", "EffectSearchPaths") ?? string.Empty;
            r.Add(new(esp.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase) ? VerifyStatus.Ok : VerifyStatus.Warn, "ReShade.ini EffectSearchPaths=" + esp));
            if (Regex.IsMatch(File.ReadAllText(ini), "DLSS5_MV_PROVIDER", RegexOptions.IgnoreCase)) r.Add(new(VerifyStatus.Warn, "ReShade.ini mentions DLSS5_MV_PROVIDER; that key only counts inside ReShadePreset.ini's [DLSS5_Feed.fx] section."));
        }

        if (feeder)
        {
            var shaders = Path.Combine(dir, "reshade-shaders", "Shaders");
            foreach (var s in new[] { "DLSS5_Feed.fx", "lumenite_Kernel.fx", "ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh" })
            {
                var ok = Directory.Exists(shaders) && Directory.EnumerateFiles(shaders, s, SearchOption.AllDirectories).Any();
                r.Add(new(ok ? VerifyStatus.Ok : VerifyStatus.Fail, (ok ? "Shader present: " : "Shader missing: ") + s));
            }
            var preset = Path.Combine(dir, "ReShadePreset.ini");
            if (File.Exists(ini))
            {
                var pp = IniFile.Load(ini).Get("GENERAL", "PresetPath");
                if (!string.IsNullOrWhiteSpace(pp)) preset = Path.IsPathRooted(pp) ? pp : Path.GetFullPath(Path.Combine(dir, pp));
            }
            if (!File.Exists(preset)) r.Add(new(VerifyStatus.Fail, "ReShadePreset.ini is missing: " + preset));
            else
            {
                var pf = IniFile.Load(preset);
                var techniques = pf.GetList(string.Empty, "Techniques");
                var provIdx = techniques.FindIndex(t => t.StartsWith("Lumenite_", StringComparison.OrdinalIgnoreCase));
                var feedIdx = techniques.FindIndex(t => t.Equals("DLSS5_Feed@DLSS5_Feed.fx", StringComparison.OrdinalIgnoreCase));
                if (provIdx < 0 || feedIdx < 0) r.Add(new(VerifyStatus.Fail, "ReShadePreset.ini does not enable both the LumeniteFX provider and DLSS5_Feed.", "Techniques=" + string.Join(",", techniques)));
                else if (provIdx > feedIdx) r.Add(new(VerifyStatus.Fail, "The motion-vector provider must be listed above DLSS5_Feed in ReShadePreset.ini."));
                else r.Add(new(VerifyStatus.Ok, "Preset enables the provider above DLSS5_Feed."));
                var defs = pf.GetList("DLSS5_Feed.fx", "PreprocessorDefinitions");
                var prov = defs.FirstOrDefault(d => d.StartsWith("DLSS5_MV_PROVIDER", StringComparison.OrdinalIgnoreCase));
                r.Add(new(prov != null ? VerifyStatus.Ok : VerifyStatus.Fail, "[DLSS5_Feed.fx] " + (prov ?? "DLSS5_MV_PROVIDER is not set (the feed would read the wrong texture)")));
            }

            var addon = Path.Combine(dir, is32 ? "dlss5-feed.addon32" : "dlss5-feed.addon64");
            r.Add(new(File.Exists(addon) ? VerifyStatus.Ok : VerifyStatus.Fail, (File.Exists(addon) ? "Feeder add-on present: " : "Feeder add-on missing: ") + Path.GetFileName(addon)));
            var wrong = Path.Combine(dir, is32 ? "dlss5-feed.addon64" : "dlss5-feed.addon32");
            if (File.Exists(wrong)) r.Add(new(VerifyStatus.Warn, Path.GetFileName(wrong) + " (other architecture) is also present; remove it."));
            if (is32)
            {
                foreach (var n in new[] { "dlss5-feed-host64.exe", "dxgi.dll", "ReShade.ini" })
                {
                    var p = Path.Combine(dir, "host64", n);
                    r.Add(new(File.Exists(p) ? VerifyStatus.Ok : VerifyStatus.Fail, (File.Exists(p) ? "host64\\" : "host64\\ missing: ") + n));
                }
            }
        }

        foreach (var n in new[] { "renodx-dlss5.addon64", "nvngx_dlssnr.dll" }.Concat(feeder ? new[] { "nvngx_dlss.dll" } : Array.Empty<string>()))
        {
            var p = Path.Combine(consumerDir, n);
            if (!File.Exists(p)) { r.Add(new(VerifyStatus.Fail, "Missing: " + Path.GetRelativePath(dir, p))); continue; }
            var detail = string.Empty;
            if (n == "nvngx_dlssnr.dll")
            {
                var h = Hashing.Sha256File(p);
                var known = catalog.Components.FirstOrDefault(c => c.Files.Any(f => f.Path.Equals(n, StringComparison.OrdinalIgnoreCase) && f.Sha256.Equals(h, StringComparison.OrdinalIgnoreCase)));
                detail = known != null ? $"build {known.Version}" : "unknown build (not one this tool ships)";
            }
            r.Add(new(VerifyStatus.Ok, $"{Path.GetRelativePath(dir, p)} present" + (detail.Length > 0 ? " (" + detail + ")" : string.Empty)));
        }
        foreach (var n in new[] { "deep-fried-chicken.addon64", "renodx-dlss.addon64" })
        {
            if (File.Exists(Path.Combine(consumerDir, n))) r.Add(new(VerifyStatus.Fail, n + " is also present: two neural add-ons never work together."));
        }
        foreach (var n in new[] { "winmm.dll", "version.dll", "dbghelp.dll", "winhttp.dll", "wininet.dll", "OptiScaler.dll", "OptiScaler.asi" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p) && GameAnalyzer.ProductName(p).Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)) r.Add(new(VerifyStatus.Fail, "OptiScaler is active as " + n + "; it takes over the NGX calls and the neural pass never runs."));
        }

        var dc = Path.Combine(dir, "d3dcompiler_47.dll");
        if (File.Exists(dc))
        {
            var v = GameAnalyzer.FileVersion(dc) ?? string.Empty;
            if (v.StartsWith("6.3.")) r.Add(new(VerifyStatus.Warn, $"Windows 8.1-era d3dcompiler_47.dll ({v}) next to the game cannot compile the neural pass; rename it so System32's copy is used."));
        }
        if (File.Exists(Path.Combine(dir, "D3D12", "D3D12Core.dll"))) r.Add(new(VerifyStatus.Warn, "D3D12 Agility SDK folder present; if the session never opens with 0x887E0003, rename D3D12\\ to D3D12_off."));

        if (journal.Api.Equals("D3D9", StringComparison.OrdinalIgnoreCase) || journal.Api.Equals("D3D8", StringComparison.OrdinalIgnoreCase) || journal.Api.Equals("DDraw", StringComparison.OrdinalIgnoreCase) || journal.Api.Equals("Glide", StringComparison.OrdinalIgnoreCase))
        {
            var conf = Path.Combine(dir, "dgVoodoo.conf");
            if (!File.Exists(conf)) r.Add(new(VerifyStatus.Fail, "dgVoodoo.conf is missing."));
            else
            {
                var t = File.ReadAllText(conf);
                var pass = Regex.Match(t, @"(?im)^\s*DisableAndPassThru\s*=\s*(\w+)").Groups[1].Value;
                r.Add(new(pass.Equals("false", StringComparison.OrdinalIgnoreCase) ? VerifyStatus.Ok : VerifyStatus.Fail, "dgVoodoo.conf DisableAndPassThru=" + pass));
                var outApi = Regex.Match(t, @"(?im)^\s*OutputAPI\s*=\s*(\S+)").Groups[1].Value;
                r.Add(new(outApi.StartsWith("d3d11", StringComparison.OrdinalIgnoreCase) || outApi.StartsWith("d3d12", StringComparison.OrdinalIgnoreCase) ? VerifyStatus.Ok : VerifyStatus.Fail, "dgVoodoo.conf OutputAPI=" + outApi));
            }
        }

        gpu ??= GpuInfo.DetectBest();
        r.Add(new(gpu.IsRtx ? VerifyStatus.Ok : VerifyStatus.Fail, "GPU: " + gpu));
        if (gpu.IsRtx && gpu.Tier != GpuTier.Rtx50) r.Add(new(VerifyStatus.Warn, "RTX 20/30/40: the neural model needs the community build (310.8.SF-v2); NVIDIA's reference build only creates the feature on RTX 50."));

        // Runtime logs
        var feedLog = Path.Combine(dir, "dlss5-feed.log");
        if (feeder)
        {
            if (!File.Exists(feedLog)) r.Add(new(VerifyStatus.Warn, "dlss5-feed.log not found yet: launch the game once, then verify again for a runtime verdict."));
            else
            {
                var text = SafeRead(feedLog);
                var first = text.Split('\n').FirstOrDefault(l => l.Contains("dlss5-feed", StringComparison.OrdinalIgnoreCase))?.Trim();
                if (first != null) r.Add(new(VerifyStatus.Ok, "dlss5-feed.log: " + Truncate(first, 120)));
                r.Add(new(text.Contains("feature ready", StringComparison.OrdinalIgnoreCase) ? VerifyStatus.Ok : VerifyStatus.Warn, text.Contains("feature ready", StringComparison.OrdinalIgnoreCase) ? "Feeder created its DLSS feature (\"feature ready\")." : "No \"feature ready\" line in dlss5-feed.log yet."));
                r.Add(new(Regex.IsMatch(text, @"frame \d+ delivered") ? VerifyStatus.Ok : VerifyStatus.Warn, Regex.IsMatch(text, @"frame \d+ delivered") ? "Frames are being delivered." : "No \"frame N delivered\" line yet."));
                var probe = Regex.Matches(text, @"MV probe[^\n]*?(\d+)% non-zero").LastOrDefault();
                if (probe != null && probe.Groups[1].Value == "0") r.Add(new(VerifyStatus.Warn, "The last motion-vector probe saw 0% non-zero vectors: the provider is not feeding DLSS (check the technique order and DLSS5_MV_PROVIDER)."));
                foreach (var line in text.Split('\n').Where(l => l.Contains("failure:", StringComparison.OrdinalIgnoreCase) || l.Contains("CRASH RECORDED", StringComparison.OrdinalIgnoreCase)).TakeLast(3))
                {
                    r.Add(new(VerifyStatus.Fail, "dlss5-feed.log: " + Truncate(line.Trim(), 160)));
                }
            }
        }
        var reshadeLog = Path.Combine(is32 ? Path.Combine(dir, "host64") : dir, "ReShade.log");
        if (File.Exists(reshadeLog))
        {
            var text = SafeRead(reshadeLog);
            if (text.Contains("evaluation succeeded", StringComparison.OrdinalIgnoreCase) || text.Contains("Neural Rendering is live", StringComparison.OrdinalIgnoreCase)) r.Add(new(VerifyStatus.Ok, "ReShade.log: the neural pass ran."));
            if (text.Contains("X3020", StringComparison.OrdinalIgnoreCase)) r.Add(new(VerifyStatus.Fail, "ReShade.log reports a shader compile error (X3020); a provider that does not build on ReShade 6.8 is enabled."));
            if (text.Contains("0xbad00001", StringComparison.OrdinalIgnoreCase)) r.Add(new(VerifyStatus.Fail, "ReShade.log: feature 18 create failed with 0xbad00001; this nvngx_dlssnr.dll build does not run on this GPU."));
            if (Regex.IsMatch(text, @"Failed to load add-on|could not be loaded", RegexOptions.IgnoreCase)) r.Add(new(VerifyStatus.Warn, "ReShade.log mentions an add-on that failed to load; read the log for the name."));
        }
        return r;
    }

    private static string SafeRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
