using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;

namespace DLSS5Tool.Core.Components;

public sealed class ComponentProgress
{
    public string Message { get; init; } = string.Empty;
    public double? Fraction { get; init; }
    public override string ToString() => Fraction is { } f ? $"{Message} ({f:P0})" : Message;
}

/// <summary>
/// Materialises component files on disk and hands back their paths. Resolution order per file:
/// 1. user override folder (%LOCALAPPDATA%\DLSS5Tool\local\&lt;id&gt;\&lt;path&gt;),
/// 2. the payload embedded in this executable,
/// 3. the verified cache (%LOCALAPPDATA%\DLSS5Tool\cache\&lt;id&gt;-&lt;version&gt;\&lt;path&gt;),
/// 4. a download from the component's pinned source URL.
/// Every file is checked against its SHA-256 from components.json before it is used.
/// </summary>
public sealed class ComponentStore
{
    private static readonly HttpClient Http = CreateHttp();

    public ComponentCatalog Catalog { get; }
    public string CacheRoot { get; }
    public string OverrideRoot { get; }
    public bool AllowNetwork { get; set; } = true;
    public Func<Stream?> PayloadOpener { get; set; } = OpenEmbeddedPayload;

    public ComponentStore(ComponentCatalog catalog, string? cacheRoot = null, string? overrideRoot = null)
    {
        Catalog = catalog;
        CacheRoot = cacheRoot ?? AppPaths.Cache;
        OverrideRoot = overrideRoot ?? AppPaths.LocalOverrides;
    }

    public static ComponentStore Default() => new(ComponentCatalog.LoadEmbedded());

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(AppPaths.UserAgent);
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return h;
    }

    private static Stream? OpenEmbeddedPayload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
    }

    public bool HasEmbeddedPayload()
    {
        using var s = PayloadOpener();
        return s != null;
    }

    public string CacheDir(Component c) => Path.Combine(CacheRoot, c.Id + "-" + Sanitize(c.Version));

    private static string Sanitize(string s)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    /// <summary>True when every file of the component is already available without network access.</summary>
    public bool IsAvailableOffline(string componentId)
    {
        var c = Catalog.Get(componentId);
        foreach (var f in c.Files)
        {
            if (FindOverride(c, f) != null) continue;
            if (Hashing.Matches(Path.Combine(CacheDir(c), f.Path), f.Sha256)) continue;
            if (!PayloadHas(c, f)) return false;
        }
        return true;
    }

    /// <summary>Returns the on-disk path of one verified component file, fetching it first if necessary.</summary>
    public async Task<string> GetFileAsync(string componentId, string relativePath, IProgress<ComponentProgress>? progress = null, CancellationToken ct = default)
    {
        var c = Catalog.Get(componentId);
        var f = c.File(relativePath);
        var paths = await GetFilesAsync(c, new[] { f }, progress, ct).ConfigureAwait(false);
        return paths[f.Path];
    }

    /// <summary>Materialises every file of a component and returns path-by-relative-path.</summary>
    public Task<Dictionary<string, string>> GetAllAsync(string componentId, IProgress<ComponentProgress>? progress = null, CancellationToken ct = default)
    {
        var c = Catalog.Get(componentId);
        return GetFilesAsync(c, c.Files, progress, ct);
    }

    private async Task<Dictionary<string, string>> GetFilesAsync(Component c, IReadOnlyList<ComponentFile> wanted, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<ComponentFile>();
        var cacheDir = CacheDir(c);

        foreach (var f in wanted)
        {
            var ov = FindOverride(c, f);
            if (ov != null) { result[f.Path] = ov; continue; }
            var cached = Path.Combine(cacheDir, f.Path);
            if (Hashing.Matches(cached, f.Sha256)) { result[f.Path] = cached; continue; }
            missing.Add(f);
        }
        if (missing.Count == 0) return result;

        // Embedded payload first: no network, instant.
        var stillMissing = new List<ComponentFile>();
        foreach (var f in missing)
        {
            if (TryExtractFromPayload(c, f, Path.Combine(cacheDir, f.Path)))
            {
                result[f.Path] = Path.Combine(cacheDir, f.Path);
            }
            else
            {
                stillMissing.Add(f);
            }
        }
        if (stillMissing.Count == 0) return result;

        if (!AllowNetwork)
        {
            throw new ComponentUnavailableException(c, stillMissing, "network access is disabled and the files are not cached");
        }

        if (c.Source != null)
        {
            var archive = await DownloadSourceAsync(c, progress, ct).ConfigureAwait(false);
            ExtractFromArchive(c, archive, c.Files, cacheDir);
        }
        else
        {
            foreach (var f in stillMissing)
            {
                var dest = Path.Combine(cacheDir, f.Path);
                await DownloadToAsync(f.Url!, dest, f.Sha256, $"Downloading {c.Name}: {Path.GetFileName(f.Path)}", progress, ct).ConfigureAwait(false);
            }
        }

        foreach (var f in stillMissing)
        {
            var p = Path.Combine(cacheDir, f.Path);
            if (!Hashing.Matches(p, f.Sha256))
            {
                throw new ComponentUnavailableException(c, new[] { f }, "the downloaded file does not match its pinned SHA-256");
            }
            result[f.Path] = p;
        }
        return result;
    }

    private string? FindOverride(Component c, ComponentFile f)
    {
        var p = Path.Combine(OverrideRoot, c.Id, f.Path);
        if (!System.IO.File.Exists(p)) return null;
        if (Hashing.Matches(p, f.Sha256)) return p;
        Log.Warn($"Override file {p} does not match the pinned hash for {c.Id}/{f.Path}; using it anyway because the user placed it there.");
        return p;
    }

    private bool PayloadHas(Component c, ComponentFile f)
    {
        using var s = PayloadOpener();
        if (s == null) return false;
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, false);
        return zip.GetEntry(c.Id + "/" + f.Path) != null;
    }

    private bool TryExtractFromPayload(Component c, ComponentFile f, string dest)
    {
        using var s = PayloadOpener();
        if (s == null) return false;
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, false);
        var entry = zip.GetEntry(c.Id + "/" + f.Path);
        if (entry == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        using (var es = entry.Open())
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            es.CopyTo(fs);
        }
        if (!Hashing.Matches(tmp, f.Sha256))
        {
            System.IO.File.Delete(tmp);
            Log.Error($"Embedded payload entry {c.Id}/{f.Path} failed its hash check; falling back to download.");
            return false;
        }
        System.IO.File.Move(tmp, dest, true);
        return true;
    }

    private async Task<string> DownloadSourceAsync(Component c, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        var src = c.Source!;
        var name = Path.GetFileName(new Uri(src.Url).LocalPath);
        var dest = Path.Combine(CacheRoot, "downloads", c.Id + "-" + Sanitize(c.Version) + "-" + name);
        if (Hashing.Matches(dest, src.Sha256)) return dest;
        await DownloadToAsync(src.Url, dest, src.Sha256, $"Downloading {c.Name} {c.Version}", progress, ct).ConfigureAwait(false);
        return dest;
    }

    private static async Task DownloadToAsync(string url, string dest, string expectedSha256, string label, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        Log.Info($"GET {url}");
        progress?.Report(new ComponentProgress { Message = label });
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = DateTime.UtcNow;
            int n;
            while ((n = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new ComponentProgress { Message = $"{label} - {done / 1048576.0:F1} MB" + (total is { } t ? $" of {t / 1048576.0:F1} MB" : string.Empty), Fraction = total is > 0 ? (double)done / total.Value : null });
                }
            }
        }
        var actual = Hashing.Sha256File(tmp);
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            System.IO.File.Delete(tmp);
            throw new InvalidDataException($"Download of {url} does not match its pinned SHA-256 (expected {expectedSha256}, got {actual}). Refusing to use it.");
        }
        System.IO.File.Move(tmp, dest, true);
        progress?.Report(new ComponentProgress { Message = label + " - done", Fraction = 1 });
    }

    /// <summary>Extracts the listed files from a zip (or the zip appended to a ReShade setup exe) into destDir.</summary>
    public static void ExtractFromArchive(Component c, string archivePath, IEnumerable<ComponentFile> files, string destDir)
    {
        using var raw = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Stream zipStream = raw;
        if (c.Source!.Format.Equals("reshade-setup", StringComparison.OrdinalIgnoreCase))
        {
            zipStream = SliceAppendedZip(raw);
        }
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, false);
        var prefix = c.Source.StripPrefix ?? string.Empty;
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.EndsWith('/')) continue;
            if (prefix.Length > 0 && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n = n[prefix.Length..];
            byName[n] = e;
        }
        foreach (var f in files)
        {
            if (!byName.TryGetValue(f.Path.Replace('\\', '/'), out var entry))
            {
                throw new InvalidDataException($"{Path.GetFileName(archivePath)} has no entry '{f.Path}' (component {c.Id}).");
            }
            var dest = Path.Combine(destDir, f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            using (var es = entry.Open())
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                es.CopyTo(fs);
            }
            var actual = Hashing.Sha256File(tmp);
            if (!actual.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                System.IO.File.Delete(tmp);
                throw new InvalidDataException($"{f.Path} inside {Path.GetFileName(archivePath)} does not match its pinned SHA-256 (expected {f.Sha256}, got {actual}).");
            }
            System.IO.File.Move(tmp, dest, true);
        }
    }

    /// <summary>ReShade's setup is a .NET exe with a plain zip appended; the installer itself locates it by the PK signature.</summary>
    public static MemoryStream SliceAppendedZip(Stream exe)
    {
        var all = new byte[exe.Length];
        exe.Position = 0;
        var read = 0;
        while (read < all.Length)
        {
            var n = exe.Read(all, read, all.Length - read);
            if (n <= 0) break;
            read += n;
        }
        // Find the first local file header whose following bytes look like a real entry (same heuristic as ReShade's setup).
        for (var i = 0; i + 30 < all.Length; i++)
        {
            if (all[i] != 0x50 || all[i + 1] != 0x4B || all[i + 2] != 0x03 || all[i + 3] != 0x04) continue;
            var nonZero = false;
            for (var j = 4; j < 30; j++) if (all[i + j] != 0) { nonZero = true; break; }
            if (!nonZero) continue;
            var ms = new MemoryStream(all, i, all.Length - i, false, true);
            try
            {
                using var probe = new ZipArchive(ms, ZipArchiveMode.Read, true);
                if (probe.GetEntry("ReShade64.dll") == null) continue;
            }
            catch (InvalidDataException)
            {
                continue;
            }
            return new MemoryStream(all, i, all.Length - i, false, true);
        }
        throw new InvalidDataException("No embedded ReShade archive found inside the setup executable.");
    }
}

public sealed class ComponentUnavailableException : Exception
{
    public Component Component { get; }
    public IReadOnlyList<ComponentFile> Files { get; }

    public ComponentUnavailableException(Component component, IReadOnlyList<ComponentFile> files, string reason)
        : base($"{component.Name} ({component.Version}) is not available: {reason}. Missing: {string.Join(", ", files.Select(f => f.Path))}. Source: {component.Source?.Url ?? component.Homepage}")
    {
        Component = component;
        Files = files;
    }
}
