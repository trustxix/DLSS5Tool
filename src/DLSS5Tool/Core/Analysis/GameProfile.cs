using DLSS5Tool.Core.Install;

namespace DLSS5Tool.Core.Analysis;

public sealed record ReShadePresence(string Path, string? Version, bool? HasAddonSupport)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool IsUsable => HasAddonSupport != false && VersionAtLeast(6, 8);

    public bool VersionAtLeast(int major, int minor)
    {
        if (string.IsNullOrWhiteSpace(Version)) return false;
        var parts = Version.Split('.', ',');
        if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var ma) || !int.TryParse(parts[1].Trim(), out var mi)) return false;
        return ma > major || (ma == major && mi >= minor);
    }
}

public sealed record ForeignFile(string Path, string Product, string Why);

/// <summary>Everything the planner needs to know about one executable, gathered read-only.</summary>
public sealed class GameProfile
{
    public string ExePath { get; init; } = string.Empty;
    public string GameDir { get; init; } = string.Empty;
    public bool Is64Bit { get; init; }
    public string Architecture { get; init; } = string.Empty;
    public HashSet<GraphicsApi> Apis { get; init; } = new();
    public List<string> Evidence { get; init; } = new();
    public GraphicsApi PrimaryApi { get; init; }
    public GraphicsApi? ApiOverride { get; set; }
    public GraphicsApi EffectiveApi => ApiOverride ?? PrimaryApi;
    public bool HasNativeDlss { get; init; }
    public bool HasStreamline { get; init; }
    public List<string> NativeDlssFiles { get; init; } = new();
    public bool Dxvk { get; init; }
    public bool RtxRemix { get; init; }
    public bool AgilitySdkRedist { get; init; }
    public ReShadePresence? ReShade { get; init; }
    public bool DgVoodooPresent { get; init; }
    public List<ForeignFile> ForeignFiles { get; init; } = new();
    public InstallJournal? Journal { get; init; }
    public bool IsInstalled => Journal != null;
    /// <summary>Full paths of files a previous DLSS 5 Tool install wrote here; they vanish before a reinstall, so planning treats them as absent.</summary>
    public HashSet<string> OwnedFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsForeignFile(string path) => File.Exists(path) && !OwnedFiles.Contains(Path.GetFullPath(path));

    public string ApiSummary
    {
        get
        {
            var ordered = Apis.OrderByDescending(a => a == EffectiveApi).ThenBy(a => a).Select(a => a.Display()).ToList();
            return ordered.Count == 0 ? "Unknown" : string.Join(", ", ordered);
        }
    }
}
