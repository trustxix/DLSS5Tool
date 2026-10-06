using DLSS5Tool.Core.Analysis;

namespace DLSS5Tool.Core.Planning;

public enum Route
{
    Unsupported,
    /// <summary>64-bit game that already calls DLSS: the RenoDX DLSS 5 add-on hooks those calls directly.</summary>
    NativeDlss64,
    /// <summary>64-bit game without DLSS: DLSS5-Feeder manufactures the DLSS contract in-process.</summary>
    Feeder64,
    /// <summary>32-bit game: the feeder add-on ships frames to the 64-bit helper in host64\.</summary>
    Feeder32,
}

public enum ReShadeMode { LocalDll, VulkanLayer }

public enum PlanItemKind
{
    ComponentFile,
    TextFile,
    ReShadeIni,
    ReShadePreset,
    DgVoodooConf,
    DisableFile,
    VulkanLayer,
}

public sealed class PlanItem
{
    public PlanItemKind Kind { get; init; }
    /// <summary>Absolute destination path.</summary>
    public string Target { get; init; } = string.Empty;
    public string? ComponentId { get; init; }
    public string? ComponentPath { get; init; }
    public string? Text { get; init; }
    public string Description { get; init; } = string.Empty;
    /// <summary>Leave an existing file alone instead of replacing it.</summary>
    public bool OnlyIfMissing { get; init; }
    /// <summary>Extra data for ini/conf edits.</summary>
    public Dictionary<string, string> Settings { get; init; } = new();
}

public sealed class InstallPlan
{
    public GameProfile Profile { get; init; } = null!;
    public GpuInfo Gpu { get; init; } = null!;
    public Route Route { get; init; }
    public string? UnsupportedReason { get; init; }
    public ReShadeMode ReShadeMode { get; init; }
    public string ReShadeDllName { get; init; } = "dxgi.dll";
    public string? DgVoodooWrapper { get; init; }
    public string? HostDir { get; init; }
    public string ConsumerDir { get; init; } = string.Empty;
    public string NrComponentId { get; init; } = "nvngx-dlssnr";
    public int MvProvider { get; init; } = 3;
    public List<PlanItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> PostInstallSteps { get; } = new();

    public bool IsSupported => Route != Route.Unsupported;

    public IEnumerable<string> RequiredComponents => Items.Where(i => i.ComponentId != null).Select(i => i.ComponentId!).Distinct(StringComparer.OrdinalIgnoreCase);

    public string RouteDisplay => Route switch
    {
        Route.NativeDlss64 => "Native DLSS + RenoDX DLSS 5 add-on (64-bit)",
        Route.Feeder64 => "DLSS5-Feeder + RenoDX DLSS 5 add-on (64-bit)",
        Route.Feeder32 => "DLSS5-Feeder 32-bit + 64-bit helper (host64)",
        _ => "Not supported",
    };
}
