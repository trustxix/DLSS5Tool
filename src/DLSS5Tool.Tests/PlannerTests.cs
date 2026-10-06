using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Planning;
using Xunit;

namespace DLSS5Tool.Tests;

public class PlannerTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempDir("plan");
    private readonly ComponentCatalog _catalog = ComponentCatalog.LoadEmbedded();
    private static readonly GpuInfo Rtx50 = new("NVIDIA GeForce RTX 5070 Ti", GpuTier.Rtx50, "616.92");
    private static readonly GpuInfo Rtx40 = new("NVIDIA GeForce RTX 4070", GpuTier.Rtx40, "616.92");
    private static readonly GpuInfo Amd = new("AMD Radeon RX 7800 XT", GpuTier.Unsupported, null);

    public void Dispose() => TestSupport.TryDelete(_dir);

    private GameProfile Profile(bool is64, GraphicsApi api, bool nativeDlss = false, ReShadePresence? reshade = null, List<ForeignFile>? foreign = null, HashSet<GraphicsApi>? apis = null)
    {
        return new GameProfile
        {
            ExePath = Path.Combine(_dir, "Game.exe"),
            GameDir = _dir,
            Is64Bit = is64,
            Architecture = is64 ? "x64" : "x86",
            Apis = apis ?? new HashSet<GraphicsApi> { api },
            PrimaryApi = api,
            HasNativeDlss = nativeDlss,
            ReShade = reshade,
            ForeignFiles = foreign ?? new List<ForeignFile>(),
        };
    }

    private static IEnumerable<string> Targets(InstallPlan p) => p.Items.Select(i => Path.GetFileName(i.Target));

    [Fact]
    public void NativeDlss64_UsesRenoDxOnly()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D12, nativeDlss: true), Rtx50, _catalog);
        Assert.Equal(Route.NativeDlss64, plan.Route);
        Assert.Contains("renodx-dlss5.addon64", Targets(plan));
        Assert.Contains("nvngx_dlssnr.dll", Targets(plan));
        Assert.DoesNotContain("dlss5-feed.addon64", Targets(plan));
        Assert.DoesNotContain("nvngx_dlss.dll", Targets(plan));
        Assert.DoesNotContain(plan.Items, i => i.Kind == PlanItemKind.ReShadePreset);
        Assert.Equal("nvngx-dlssnr", plan.NrComponentId);
        Assert.Equal("dxgi.dll", plan.ReShadeDllName);
    }

    [Fact]
    public void Feeder64_D3D11_FullSet()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11), Rtx50, _catalog);
        Assert.Equal(Route.Feeder64, plan.Route);
        var t = Targets(plan).ToList();
        Assert.Contains("dxgi.dll", t);
        Assert.Contains("dlss5-feed.addon64", t);
        Assert.Contains("DLSS5_Feed.fx", t);
        Assert.Contains("lumenite_Kernel.fx", t);
        Assert.Contains("lumenite_bluenoise256.png", t);
        Assert.Contains("ReShade.fxh", t);
        Assert.Contains("renodx-dlss5.addon64", t);
        Assert.Contains("nvngx_dlssnr.dll", t);
        Assert.Contains("nvngx_dlss.dll", t);
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.ReShadeIni);
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.ReShadePreset);
        Assert.Null(plan.HostDir);
        Assert.Null(plan.DgVoodooWrapper);
        Assert.All(plan.Items.Where(i => i.Kind == PlanItemKind.ComponentFile), i => Assert.StartsWith(_dir, i.Target));
    }

    [Fact]
    public void Feeder32_D3D9_UsesDgVoodooAndHost64()
    {
        var plan = Planner.Build(Profile(false, GraphicsApi.D3D9), Rtx50, _catalog);
        Assert.Equal(Route.Feeder32, plan.Route);
        Assert.Equal("D3D9.dll", plan.DgVoodooWrapper);
        Assert.Equal(Path.Combine(_dir, "host64"), plan.HostDir);
        var items = plan.Items;
        Assert.Contains(items, i => i.ComponentId == "dgvoodoo2" && i.ComponentPath == "MS/x86/D3D9.dll");
        Assert.Contains(items, i => i.Kind == PlanItemKind.DgVoodooConf);
        Assert.Contains(items, i => i.ComponentPath == "dlss5-feed.addon32" && i.Target == Path.Combine(_dir, "dlss5-feed.addon32"));
        Assert.Contains(items, i => i.ComponentPath == "host64/dlss5-feed-host64.exe" && i.Target == Path.Combine(_dir, "host64", "dlss5-feed-host64.exe"));
        Assert.Contains(items, i => i.ComponentPath == "ReShade64.dll" && i.Target == Path.Combine(_dir, "host64", "dxgi.dll"));
        Assert.Contains(items, i => i.ComponentPath == "ReShade32.dll" && i.Target == Path.Combine(_dir, "dxgi.dll"));
        Assert.Contains(items, i => i.ComponentPath == "renodx-dlss5.addon64" && i.Target == Path.Combine(_dir, "host64", "renodx-dlss5.addon64"));
        Assert.Contains(items, i => i.ComponentPath == "nvngx_dlssnr.dll" && i.Target == Path.Combine(_dir, "host64", "nvngx_dlssnr.dll"));
        Assert.Contains(items, i => i.Kind == PlanItemKind.TextFile && i.Target == Path.Combine(_dir, "host64", "ReShade.ini"));
        Assert.Equal("1", items.First(i => i.Kind == PlanItemKind.ReShadeIni).Settings["DepthCopyBeforeClears"]);
    }

    [Fact]
    public void Feeder64_D3D9_UsesX64Wrapper()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D9), Rtx50, _catalog);
        Assert.Equal(Route.Feeder64, plan.Route);
        Assert.Contains(plan.Items, i => i.ComponentPath == "MS/x64/D3D9.dll");
        Assert.Contains(plan.Items, i => i.ComponentPath == "ReShade64.dll" && Path.GetFileName(i.Target) == "dxgi.dll");
    }

    [Fact]
    public void Vulkan64_UsesLayerAndFallbackFiles()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.Vulkan), Rtx50, _catalog);
        Assert.Equal(Route.Feeder64, plan.Route);
        Assert.Equal(ReShadeMode.VulkanLayer, plan.ReShadeMode);
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.VulkanLayer);
        Assert.DoesNotContain(plan.Items, i => i.ComponentId == "reshade");
        Assert.Contains(plan.Items, i => i.ComponentPath == "layer-x64/VkLayer_feed_vk.dll" && i.Target == Path.Combine(_dir, "layer", "VkLayer_feed_vk.dll"));
        Assert.Contains(plan.Warnings, w => w.Contains("Smooth Motion"));
    }

    [Fact]
    public void OpenGL64_UsesOpengl32Name()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.OpenGL), Rtx50, _catalog);
        Assert.Equal("opengl32.dll", plan.ReShadeDllName);
        Assert.Contains(plan.Items, i => i.ComponentPath == "ReShade64.dll" && Path.GetFileName(i.Target) == "opengl32.dll");
    }

    [Fact]
    public void Unsupported_Cases()
    {
        Assert.Equal(Route.Unsupported, Planner.Build(Profile(false, GraphicsApi.D3D12), Rtx50, _catalog).Route);
        Assert.Equal(Route.Unsupported, Planner.Build(Profile(true, GraphicsApi.D3D10), Rtx50, _catalog).Route);
        Assert.Equal(Route.Unsupported, Planner.Build(Profile(true, GraphicsApi.Unknown), Rtx50, _catalog).Route);
        var amd = Planner.Build(Profile(true, GraphicsApi.D3D11), Amd, _catalog);
        Assert.Equal(Route.Unsupported, amd.Route);
        Assert.Contains("RTX", amd.UnsupportedReason);
        Assert.Equal(Route.Feeder64, Planner.Build(Profile(true, GraphicsApi.D3D11), Amd, _catalog, new PlanOptions { IgnoreGpu = true }).Route);
        Assert.Equal(Route.Feeder32, Planner.Build(Profile(false, GraphicsApi.D3D10), Rtx50, _catalog).Route);
    }

    [Fact]
    public void Rtx40_UsesCommunityModel()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11), Rtx40, _catalog);
        Assert.Equal("nvngx-dlssnr-sf", plan.NrComponentId);
        Assert.Contains(plan.Items, i => i.ComponentId == "nvngx-dlssnr-sf");
        Assert.Contains(plan.Warnings, w => w.Contains("RTX 50"));
    }

    [Fact]
    public void ApiOverrideWins()
    {
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D12, apis: new HashSet<GraphicsApi> { GraphicsApi.D3D12, GraphicsApi.Vulkan }), Rtx50, _catalog, new PlanOptions { ApiOverride = GraphicsApi.Vulkan });
        Assert.Equal(ReShadeMode.VulkanLayer, plan.ReShadeMode);
        Assert.Contains(plan.Notes, n => n.Contains("several APIs"));
    }

    [Fact]
    public void ExistingUsableReShadeUnderAnotherNameIsKept()
    {
        var existing = Path.Combine(_dir, "d3d11.dll");
        File.WriteAllText(existing, "x");
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11, reshade: new ReShadePresence(existing, "6.8.0.2155", true)), Rtx50, _catalog);
        Assert.Equal(Route.Feeder64, plan.Route);
        Assert.Equal("d3d11.dll", plan.ReShadeDllName);
        Assert.DoesNotContain(plan.Items, i => i.ComponentId == "reshade");
        Assert.Contains(plan.Notes, n => n.Contains("kept"));
    }

    [Fact]
    public void OldReShadeIsReplaced()
    {
        var existing = Path.Combine(_dir, "dxgi.dll");
        File.WriteAllText(existing, "x");
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11, reshade: new ReShadePresence(existing, "5.9.2", true)), Rtx50, _catalog);
        Assert.Contains(plan.Items, i => i.ComponentId == "reshade" && i.Target == existing);
    }

    [Fact]
    public void ForeignDxgiBlocksInstall()
    {
        File.WriteAllText(Path.Combine(_dir, "dxgi.dll"), "not reshade");
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11), Rtx50, _catalog);
        Assert.Equal(Route.Unsupported, plan.Route);
        Assert.Contains("dxgi.dll", plan.UnsupportedReason);
    }

    [Fact]
    public void ReShadeAsD3d9BlocksDgVoodooRoute()
    {
        var existing = Path.Combine(_dir, "d3d9.dll");
        File.WriteAllText(existing, "x");
        var plan = Planner.Build(Profile(false, GraphicsApi.D3D9, reshade: new ReShadePresence(existing, "6.8.0", true)), Rtx50, _catalog);
        Assert.Equal(Route.Unsupported, plan.Route);
        Assert.Contains("dgVoodoo2", plan.UnsupportedReason);
    }

    [Fact]
    public void ConflictingAddonsAreDisabled()
    {
        var opti = Path.Combine(_dir, "winmm.dll");
        var plan = Planner.Build(Profile(true, GraphicsApi.D3D11, foreign: new List<ForeignFile> { new(opti, "OptiScaler", "test") }), Rtx50, _catalog);
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.DisableFile && i.Target == opti);
        Assert.Equal(PlanItemKind.DisableFile, plan.Items[0].Kind);
    }

    [Fact]
    public void RequiredComponentsCoverEveryFile()
    {
        var plan = Planner.Build(Profile(false, GraphicsApi.Glide), Rtx50, _catalog);
        Assert.Equal(Route.Feeder32, plan.Route);
        foreach (var i in plan.Items.Where(i => i.ComponentId != null))
        {
            var c = _catalog.Get(i.ComponentId!);
            Assert.Contains(c.Files, f => f.Path.Equals(i.ComponentPath, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains(plan.Items, i => i.ComponentPath == "3Dfx/x86/Glide2x.dll");
    }
}
