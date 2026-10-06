using System.Diagnostics;
using System.Text.RegularExpressions;
using DLSS5Tool.Core.Components;

namespace DLSS5Tool.Core;

/// <summary>
/// Runs DLSS5-Feeder's 64-bit helper in --test mode from a private staging folder: 300 synthetic DLAA evaluates
/// through the real NGX runtime, the RenoDX add-on and the neural model, with no game involved. The upstream
/// README recommends it as the fifteen-second check of a driver/add-on/model combination.
/// </summary>
public static class SelfTest
{
    public sealed record Result(bool Success, int Succeeded, int Total, string Summary, string LogPath, IReadOnlyList<string> Highlights);

    public static async Task<Result> RunAsync(ComponentStore store, GpuInfo gpu, IProgress<string>? progress = null, bool hideWindow = true, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.SelfTest, "host64");
        Directory.CreateDirectory(dir);
        var nr = gpu.Tier == GpuTier.Rtx50 || !gpu.IsRtx ? "nvngx-dlssnr" : "nvngx-dlssnr-sf";
        var cp = new Progress<ComponentProgress>(p => progress?.Report(p.ToString()));
        var pieces = new (string id, string path, string dest)[]
        {
            ("dlss5-feeder", "host64/dlss5-feed-host64.exe", "dlss5-feed-host64.exe"),
            ("reshade", "ReShade64.dll", "dxgi.dll"),
            ("renodx-dlss5", "renodx-dlss5.addon64", "renodx-dlss5.addon64"),
            (nr, "nvngx_dlssnr.dll", "nvngx_dlssnr.dll"),
            ("nvngx-dlss", "nvngx_dlss.dll", "nvngx_dlss.dll"),
        };
        foreach (var (id, path, dest) in pieces)
        {
            var src = await store.GetFileAsync(id, path, cp, ct).ConfigureAwait(false);
            var target = Path.Combine(dir, dest);
            if (!File.Exists(target) || !Hashing.Matches(target, store.Catalog.Get(id).File(path).Sha256)) File.Copy(src, target, true);
        }
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini)) File.WriteAllText(ini, Planning.Planner.HostReShadeIni);
        var logPath = Path.Combine(dir, "dlss5-feed-host.log");
        try { if (File.Exists(logPath)) File.Delete(logPath); } catch { /* a stale log is fine */ }

        progress?.Report("Running dlss5-feed-host64.exe --test (300 evaluates)...");
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(dir, "dlss5-feed-host64.exe"),
            Arguments = hideWindow ? "--test --hide" : "--test",
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the helper.");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* already gone */ }
            throw new TimeoutException("The helper self-test did not finish within 3 minutes.");
        }
        var text = (File.Exists(logPath) ? File.ReadAllText(logPath) : string.Empty) + "\n" + await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
        var m = Regex.Match(text, @"--test finished: (\d+)/(\d+) evaluates succeeded");
        var highlights = text.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains("--test:", StringComparison.Ordinal) || l.Contains("WARNING", StringComparison.Ordinal) || l.Contains("NGX runtime", StringComparison.Ordinal) || (l.Contains("neural", StringComparison.OrdinalIgnoreCase) && l.Contains("consumer", StringComparison.OrdinalIgnoreCase)))
            .Select(l => Regex.Replace(l, @"^\d\d:\d\d:\d\d\.\d+\s+", string.Empty))
            .Distinct()
            .Take(12)
            .ToList();
        if (!m.Success)
        {
            return new Result(false, 0, 0, $"The helper exited with code {proc.ExitCode} without a result line; read the log.", logPath, highlights);
        }
        var ok = int.Parse(m.Groups[1].Value);
        var total = int.Parse(m.Groups[2].Value);
        var success = total > 0 && ok == total;
        return new Result(success, ok, total, success ? $"{ok}/{total} evaluates succeeded: this driver, add-on and neural model work together on {gpu.Name}." : $"{ok}/{total} evaluates succeeded: this combination does not work on this PC; see the log.", logPath, highlights);
    }
}
