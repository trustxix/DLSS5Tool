using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSS5Tool.Core.Update;

public sealed record UpdateInfo(string Tag, Version Version, string ExeUrl, string? SumsUrl, string Notes, string HtmlUrl);

/// <summary>
/// Self-update from GitHub Releases: compares the latest release tag with this build, downloads the single-file
/// executable, verifies it against the SHA256SUMS.txt asset, swaps it into place and restarts.
/// </summary>
public static class Updater
{
    public const string AssetName = "DLSS5Tool.exe";
    public const string SumsAssetName = "SHA256SUMS.txt";
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(AppPaths.UserAgent);
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    public static Version Current => ParseVersion(AppPaths.Version) ?? new Version(0, 0, 0);

    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    public static string ReleasesApi => $"https://api.github.com/repos/{AppPaths.RepoOwner}/{AppPaths.RepoName}/releases/latest";

    /// <summary>Returns the newer release, or null when this build is current (or the check fails quietly).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = ParseVersion(tag);
        if (version == null) return null;
        string? exeUrl = null, sumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? string.Empty;
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                if (name.Equals(SumsAssetName, StringComparison.OrdinalIgnoreCase)) sumsUrl = url;
            }
        }
        if (exeUrl == null) return null;
        var info = new UpdateInfo(tag, version, exeUrl, sumsUrl, root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty, root.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty);
        return version > Current ? info : null;
    }

    /// <summary>Downloads and verifies the new executable; returns its path without installing it.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.Root, "update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, AssetName + ".new");
        progress?.Report($"Downloading DLSS 5 Tool {update.Tag}...");
        try
        {
            using (var resp = await Http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} for {update.ExeUrl}", null, resp.StatusCode);
                await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
                if (resp.Content.Headers.ContentLength is { } expectedLength && fs.Length != expectedLength)
                {
                    throw new IOException($"The update download ended early ({fs.Length} of {expectedLength} bytes).");
                }
            }
            if (update.SumsUrl != null)
            {
                var sums = await Http.GetStringAsync(update.SumsUrl, ct).ConfigureAwait(false);
                var expected = sums.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(AssetName, StringComparison.OrdinalIgnoreCase)).Select(l => l.Split(' ', '\t')[0].TrimStart('*')).FirstOrDefault();
                if (expected == null) throw new InvalidDataException("SHA256SUMS.txt does not list " + AssetName + ".");
                var actual = Hashing.Sha256File(dest);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The downloaded update does not match the published SHA-256 (expected {expected}, got {actual}).");
                }
                progress?.Report("Checksum verified.");
            }
            else
            {
                progress?.Report("No SHA256SUMS.txt on this release; the download could not be checksum-verified.");
            }
            var pe = PeFile.TryRead(dest);
            if (pe == null || !pe.Is64Bit) throw new InvalidDataException("The downloaded update is not a 64-bit Windows executable.");
            return dest;
        }
        catch
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Swaps the running executable for the downloaded one and restarts. Does not return on success.</summary>
    public static void ApplyAndRestart(string newExe, bool restartUi)
    {
        var exe = AppPaths.ExePath;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        if (restartUi)
        {
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
        }
        Environment.Exit(0);
    }

    public static void CleanupAfterUpdate()
    {
        var old = AppPaths.ExePath + ".old";
        for (var i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(200); }
        }
    }
}
