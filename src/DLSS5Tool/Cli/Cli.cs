using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Games;
using DLSS5Tool.Core.Install;
using DLSS5Tool.Core.Planning;
using DLSS5Tool.Core.Update;

namespace DLSS5Tool.Cli;

/// <summary>Headless command line: the same engine as the window, for scripts, support and CI.</summary>
public static class Cli
{
    private const string Usage = """
        DLSS 5 Tool - installs DLSS 5 neural rendering into any game.

        Usage:
          DLSS5Tool                              Open the window.
          DLSS5Tool --scan [--json]              List installed games found on this PC.
          DLSS5Tool --analyze <game.exe> [--json]
          DLSS5Tool --plan <game.exe> [--api <api>] [--json]
          DLSS5Tool --install <game.exe> [--api <api>] [--offline] [--ignore-gpu] [--watermark] [--json]
          DLSS5Tool --uninstall <game.exe>
          DLSS5Tool --verify <game.exe> [--json]
          DLSS5Tool --selftest [--show-window]   Run the 64-bit helper self-test (no game needed).
          DLSS5Tool --components                 Show every third-party piece, its version and availability.
          DLSS5Tool --check-update | --update
          DLSS5Tool --version | --help

        Options:
          --api <api>      Override detection: d3d8, d3d9, d3d10, d3d11, d3d12, opengl, vulkan, glide, ddraw.
          --offline        Never download; fail if a component is not cached or embedded.
          --ignore-gpu     Plan/install even without an NVIDIA RTX GPU (testing).
          --out <file>     Also write the output to a file.
          --json           Machine-readable output.

        Exit codes: 0 success, 1 error, 2 not supported / not installed, 3 bad usage.
        """;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int pid);

    private sealed class Output : IDisposable
    {
        private readonly TextWriter? _console;
        private readonly StreamWriter? _file;
        private readonly StringBuilder _buffer = new();

        public Output(string? outFile)
        {
            if (AttachConsole(-1))
            {
                _console = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                _console.WriteLine();
            }
            if (outFile != null)
            {
                var full = Path.GetFullPath(outFile);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _file = new StreamWriter(full, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }

        public void Line(string s = "")
        {
            try { _console?.WriteLine(s); } catch (IOException) { /* parent console went away */ }
            _file?.WriteLine(s);
            _buffer.AppendLine(s);
        }

        public void Dispose()
        {
            _file?.Dispose();
            if (_console != null)
            {
                _console.Flush();
            }
            Log.Debug("CLI output:\n" + _buffer);
        }
    }

    private static readonly string[] Commands = { "scan", "analyze", "plan", "install", "uninstall", "verify", "selftest", "components", "check-update", "update", "version", "help" };
    private static readonly string[] Flags = { "json", "offline", "ignore-gpu", "watermark", "show-window" };

    public static int Run(string[] args)
    {
        var opts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? command = null;
        string? target = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) { target ??= a; continue; }
            var name = a[2..].ToLowerInvariant();
            if (name is "api" or "out")
            {
                if (i + 1 >= args.Length) return Fail(null, $"--{name} needs a value.", 3);
                values[name] = args[++i];
                continue;
            }
            if (Commands.Contains(name))
            {
                if (command != null && command != name) return Fail(null, $"Only one command at a time (--{command} and --{name}).", 3);
                command = name;
                if (name is "analyze" or "plan" or "install" or "uninstall" or "verify")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) target = args[++i];
                }
                continue;
            }
            if (name is "h" or "?") { command = "help"; continue; }
            if (!Flags.Contains(name)) return Fail(null, $"Unknown option: {a}\n\n" + Usage, 3);
            opts.Add(name);
        }
        if (command == null) return Fail(null, "No command given.\n\n" + Usage, 3);

        Output o;
        try { o = new Output(values.GetValueOrDefault("out")); }
        catch (Exception ex) { return Fail(null, "Cannot write --out file: " + ex.Message, 3); }
        using var _ = o;
        var json = opts.Contains("json");
        try
        {
            switch (command)
            {
                case "help": o.Line(Usage); return 0;
                case "version": o.Line("DLSS 5 Tool " + AppPaths.Version); return 0;
                case "scan": return Scan(o, json);
                case "components": return Components(o);
                case "check-update": return CheckUpdate(o, false);
                case "update": return CheckUpdate(o, true);
                case "selftest": return SelfTestCmd(o, !opts.Contains("show-window"), opts.Contains("offline"));
            }
            if (target == null) return Fail(o, "This command needs the path to the game's executable.", 3);
            if (!File.Exists(target)) return Fail(o, "File not found: " + target, 3);
            target = Path.GetFullPath(target);
            GraphicsApi? apiOverride = null;
            if (values.TryGetValue("api", out var apiText))
            {
                if (!GraphicsApiInfo.TryParse(apiText, out var api)) return Fail(o, "Unknown API: " + apiText, 3);
                apiOverride = api;
            }
            return command switch
            {
                "analyze" => Analyze(o, target, json),
                "plan" => Plan(o, target, apiOverride, opts, json, install: false),
                "install" => Plan(o, target, apiOverride, opts, json, install: true),
                "uninstall" => Uninstall(o, target),
                "verify" => Verify(o, target, json),
                _ => Fail(o, Usage, 3),
            };
        }
        catch (OperationCanceledException)
        {
            return Fail(o, "Cancelled.", 1);
        }
        catch (Exception ex)
        {
            Log.Error("CLI command failed", ex);
            var msg = ex.Message;
            if (ex.InnerException != null && ex.InnerException.Message != ex.Message) msg += " (" + ex.InnerException.Message + ")";
            if (Log.FilePath != null) msg += "\nSee " + Log.FilePath;
            return Fail(o, msg, 1);
        }
    }

    private static int Fail(Output? o, string message, int code)
    {
        if (o != null) o.Line("error: " + message);
        else
        {
            using var tmp = new Output(null);
            tmp.Line("error: " + message);
        }
        return code;
    }

    private static int Scan(Output o, bool json)
    {
        var games = GameScanner.ScanAll(s => Log.Info(s));
        foreach (var g in games)
        {
            GameScanner.ResolveExecutable(g);
            g.Installed = IsInstalled(g);
        }
        if (json)
        {
            o.Line(JsonSerializer.Serialize(games.Select(g => new { g.Name, g.Launcher, g.InstallDir, g.ExePath, g.AppId, installed = g.Installed, candidates = g.Candidates.Take(5).Select(c => new { c.Path, c.Score, c.Reason }) }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        o.Line($"{games.Count} game(s) found:");
        foreach (var g in games)
        {
            var installed = g.Installed;
            o.Line($"  {(installed ? "[DLSS5]" : "       ")} {g.Name}  ({g.Launcher})");
            o.Line($"           {g.ExePath ?? g.InstallDir + "  (no executable found)"}");
        }
        return 0;
    }

    private static bool IsInstalled(GameInfo g)
    {
        try
        {
            var dir = g.ExePath != null ? Path.GetDirectoryName(g.ExePath) : null;
            return !string.IsNullOrEmpty(dir) && InstallJournal.TryLoad(dir) != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the install journal for {g.Name}: {ex.Message}");
            return false;
        }
    }

    private static int Analyze(Output o, string exe, bool json)
    {
        var p = GameAnalyzer.Analyze(exe);
        if (json)
        {
            o.Line(JsonSerializer.Serialize(new { p.ExePath, p.GameDir, p.Is64Bit, p.Architecture, apis = p.Apis.Select(a => a.ToString()), primaryApi = p.PrimaryApi.ToString(), p.Evidence, p.HasNativeDlss, p.HasStreamline, p.NativeDlssFiles, p.Dxvk, p.RtxRemix, p.AgilitySdkRedist, reshade = p.ReShade, p.DgVoodooPresent, foreign = p.ForeignFiles, installed = p.IsInstalled }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        PrintProfile(o, p);
        return 0;
    }

    private static void PrintProfile(Output o, GameProfile p)
    {
        o.Line($"Executable : {p.ExePath}");
        o.Line($"Folder     : {p.GameDir}");
        o.Line($"Bitness    : {p.Architecture}");
        o.Line($"Graphics   : {p.ApiSummary}");
        foreach (var e in p.Evidence) o.Line($"             - {e}");
        o.Line($"Native DLSS: {(p.HasNativeDlss ? "yes" : "no")}{(p.HasStreamline ? " (Streamline)" : string.Empty)}{(p.NativeDlssFiles.Count > 0 ? " [" + string.Join(", ", p.NativeDlssFiles) + "]" : string.Empty)}");
        if (p.Dxvk) o.Line("DXVK       : yes (Vulkan underneath)");
        if (p.RtxRemix) o.Line("RTX Remix  : yes");
        if (p.AgilitySdkRedist) o.Line("Agility SDK: D3D12\\D3D12Core.dll present");
        o.Line($"ReShade    : {(p.ReShade == null ? "not installed" : $"{p.ReShade.Version} as {p.ReShade.FileName} (add-on support: {(p.ReShade.HasAddonSupport == null ? "unknown" : p.ReShade.HasAddonSupport.Value ? "yes" : "no")})")}");
        if (p.DgVoodooPresent) o.Line("dgVoodoo2  : present");
        foreach (var f in p.ForeignFiles) o.Line($"Conflict   : {Path.GetFileName(f.Path)} ({f.Product}): {f.Why}");
        o.Line($"DLSS5 Tool : {(p.IsInstalled ? $"installed ({p.Journal!.Route}, {p.Journal.InstalledUtc:yyyy-MM-dd})" : "not installed")}");
    }

    private static int Plan(Output o, string exe, GraphicsApi? apiOverride, HashSet<string> opts, bool json, bool install)
    {
        var store = ComponentStore.Default();
        store.AllowNetwork = !opts.Contains("offline");
        var gpu = GpuInfo.DetectBest();
        var profile = GameAnalyzer.Analyze(exe);
        var plan = Planner.Build(profile, gpu, store.Catalog, new PlanOptions { ApiOverride = apiOverride, IgnoreGpu = opts.Contains("ignore-gpu"), DgVoodooWatermark = opts.Contains("watermark") });
        if (json && !install)
        {
            o.Line(JsonSerializer.Serialize(new { route = plan.Route.ToString(), plan.UnsupportedReason, gpu = gpu.ToString(), api = profile.EffectiveApi.ToString(), plan.ReShadeMode, plan.ReShadeDllName, plan.DgVoodooWrapper, plan.HostDir, plan.NrComponentId, items = plan.Items.Select(i => new { kind = i.Kind.ToString(), i.Target, i.ComponentId, i.ComponentPath, i.Description, i.OnlyIfMissing }), plan.Warnings, plan.Notes, plan.PostInstallSteps }, new JsonSerializerOptions { WriteIndented = true }));
            return plan.IsSupported ? 0 : 2;
        }
        PrintProfile(o, profile);
        o.Line($"GPU        : {gpu}");
        o.Line($"Route      : {plan.RouteDisplay}");
        if (!plan.IsSupported)
        {
            o.Line("Reason     : " + plan.UnsupportedReason);
            return 2;
        }
        o.Line($"ReShade    : {(plan.ReShadeMode == ReShadeMode.VulkanLayer ? "machine-wide Vulkan layer" : plan.ReShadeDllName)}");
        if (plan.DgVoodooWrapper != null) o.Line($"dgVoodoo2  : {plan.DgVoodooWrapper} -> Direct3D 11");
        if (plan.HostDir != null) o.Line($"Helper     : {plan.HostDir}");
        o.Line();
        o.Line("Files:");
        foreach (var i in plan.Items) o.Line($"  {i.Kind,-14} {Shorten(i.Target, profile.GameDir),-60} {i.Description}");
        foreach (var w in plan.Warnings) o.Line("WARNING: " + w);
        foreach (var n in plan.Notes) o.Line("note: " + n);
        if (!install)
        {
            var offline = plan.RequiredComponents.Where(c => !store.IsAvailableOffline(c)).Select(c => store.Catalog.Get(c).Name).ToList();
            o.Line(offline.Count == 0 ? "All components are available offline." : "Will be downloaded: " + string.Join(", ", offline));
            return 0;
        }

        o.Line();
        var installer = new Installer(store);
        if (json)
        {
            var log = new List<string>();
            var journalJson = installer.InstallAsync(plan, new Progress<string>(log.Add)).GetAwaiter().GetResult();
            o.Line(JsonSerializer.Serialize(new { ok = true, route = plan.Route.ToString(), gpu = gpu.ToString(), api = profile.EffectiveApi.ToString(), files = journalJson.Entries.Count, journal = Path.Combine(profile.GameDir, AppPaths.JournalFileName), plan.PostInstallSteps, plan.Warnings, log }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        var progress = new Progress<string>(s => o.Line("  " + s));
        var journal = installer.InstallAsync(plan, progress).GetAwaiter().GetResult();
        o.Line();
        o.Line($"Done: {journal.Entries.Count} file(s) written. Next, in the game:");
        var n2 = 1;
        foreach (var s in plan.PostInstallSteps) o.Line($"  {n2++}. {s}");
        return 0;
    }

    private static int Uninstall(Output o, string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var journal = InstallJournal.TryLoad(dir);
        if (journal == null) return Fail(o, "DLSS 5 Tool has not installed anything next to " + exe, 2);
        var installer = new Installer(ComponentStore.Default());
        installer.UninstallAsync(dir, journal, new Progress<string>(s => o.Line("  " + s))).GetAwaiter().GetResult();
        return 0;
    }

    private static int Verify(Output o, string exe, bool json)
    {
        GpuInfo? gpu = null;
        try { gpu = GpuInfo.DetectBest(); } catch (Exception ex) { Log.Warn("GPU detection failed: " + ex.Message); }
        var results = Verifier.Verify(exe, ComponentCatalog.LoadEmbedded(), gpu);
        if (json)
        {
            o.Line(JsonSerializer.Serialize(results.Select(r => new { status = r.Status.ToString(), r.Text, r.Detail }), new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            foreach (var r in results) o.Line(r.ToString());
            o.Line();
            o.Line($"{results.Count(r => r.Status == VerifyStatus.Ok)} ok, {results.Count(r => r.Status == VerifyStatus.Warn)} warning(s), {results.Count(r => r.Status == VerifyStatus.Fail)} failure(s).");
        }
        return results.Any(r => r.Status == VerifyStatus.Fail) ? 2 : 0;
    }

    private static int SelfTestCmd(Output o, bool hide, bool offline)
    {
        var store = ComponentStore.Default();
        store.AllowNetwork = !offline;
        var gpu = GpuInfo.DetectBest();
        o.Line("GPU: " + gpu);
        var result = SelfTest.RunAsync(store, gpu, new Progress<string>(s => o.Line("  " + s)), hide).GetAwaiter().GetResult();
        o.Line(result.Summary);
        foreach (var h in result.Highlights) o.Line("  " + h);
        o.Line("Log: " + result.LogPath);
        return result.Success ? 0 : 2;
    }

    private static int Components(Output o)
    {
        var store = ComponentStore.Default();
        o.Line($"DLSS 5 Tool {AppPaths.Version}; embedded payload: {(store.HasEmbeddedPayload() ? "yes" : "no")}; cache: {store.CacheRoot}");
        foreach (var c in store.Catalog.Components)
        {
            o.Line($"  {c.Id,-18} {c.Version,-42} {c.Distribution,-9} {(store.IsAvailableOffline(c.Id) ? "available offline" : "needs download"),-18} {c.License}");
        }
        return 0;
    }

    private static int CheckUpdate(Output o, bool apply)
    {
        o.Line($"Current version: {AppPaths.Version}");
        var info = Updater.CheckAsync().GetAwaiter().GetResult();
        if (info == null)
        {
            o.Line("Up to date.");
            return 0;
        }
        o.Line($"Update available: {info.Tag}  {info.HtmlUrl}");
        if (!apply) return 0;
        var path = Updater.DownloadAsync(info, new Progress<string>(s => o.Line("  " + s))).GetAwaiter().GetResult();
        o.Line("Installing the update and exiting.");
        Updater.ApplyAndRestart(path, restartUi: false);
        return 0;
    }

    private static string Shorten(string path, string root)
    {
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "." + path[root.Length..] : path;
    }
}
