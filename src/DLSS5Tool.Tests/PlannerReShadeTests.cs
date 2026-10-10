using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Install;
using DLSS5Tool.Core.Planning;
using Xunit;

namespace DLSS5Tool.Tests;

public class PlannerReShadeTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempDir("plan-reshade");
    private readonly ComponentCatalog _catalog = ComponentCatalog.LoadEmbedded();
    private static readonly GpuInfo Rtx50 = new("NVIDIA GeForce RTX 5070 Ti", GpuTier.Rtx50, "616.92");

    public void Dispose() => TestSupport.TryDelete(_dir);

    private GameProfile Profile(GraphicsApi api, string reshadeFile)
    {
        var path = Path.Combine(_dir, reshadeFile);
        File.WriteAllText(path, "not really reshade, just a file");
        return new GameProfile
        {
            ExePath = Path.Combine(_dir, "Game.exe"),
            GameDir = _dir,
            Is64Bit = true,
            Architecture = "x64",
            Apis = new HashSet<GraphicsApi> { api },
            PrimaryApi = api,
            ReShade = new ReShadePresence(path, "6.8.0.2155", true),
            ForeignFiles = new List<ForeignFile>(),
        };
    }

    [Fact]
    public void Reshade_for_the_wrong_api_is_disabled_and_the_right_proxy_is_written()
    {
        // Nuclear Option: a ReShade left behind as opengl32.dll on a Unity game that renders with Direct3D 11.
        var plan = Planner.Build(Profile(GraphicsApi.D3D11, "opengl32.dll"), Rtx50, _catalog);

        Assert.True(plan.IsSupported, plan.UnsupportedReason);
        Assert.Equal("dxgi.dll", plan.ReShadeDllName);
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.ComponentFile && i.ComponentId == "reshade" && Path.GetFileName(i.Target).Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Items, i => i.Kind == PlanItemKind.DisableFile && Path.GetFileName(i.Target).Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("opengl32.dll") && w.Contains("disabled"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("kept"));
    }

    [Fact]
    public void Reshade_with_a_different_but_matching_proxy_name_is_kept()
    {
        var plan = Planner.Build(Profile(GraphicsApi.D3D11, "d3d11.dll"), Rtx50, _catalog);

        Assert.True(plan.IsSupported, plan.UnsupportedReason);
        Assert.Equal("d3d11.dll", plan.ReShadeDllName);
        Assert.DoesNotContain(plan.Items, i => i.ComponentId == "reshade");
        Assert.DoesNotContain(plan.Items, i => i.Kind == PlanItemKind.DisableFile);
        Assert.Contains(plan.Notes, n => n.Contains("kept"));
    }

    [Fact]
    public void Proxy_fit_rules()
    {
        Assert.True(Planner.ProxyFits("dxgi.dll", GraphicsApi.D3D11));
        Assert.True(Planner.ProxyFits("d3d11.dll", GraphicsApi.D3D12));
        Assert.True(Planner.ProxyFits("dxgi.dll", GraphicsApi.D3D9));      // dgVoodoo2 presents through Direct3D 11
        Assert.True(Planner.ProxyFits("opengl32.dll", GraphicsApi.OpenGL));
        Assert.False(Planner.ProxyFits("opengl32.dll", GraphicsApi.D3D11));
        Assert.False(Planner.ProxyFits("dxgi.dll", GraphicsApi.OpenGL));
        Assert.False(Planner.ProxyFits("d3d9.dll", GraphicsApi.D3D11));
    }

    [Fact]
    public void Running_game_is_detected_by_process_name()
    {
        // This very test host is running; a game whose exe shares its name must be reported.
        var self = Environment.ProcessPath!;
        Assert.NotNull(Installer.RunningProcessFor(self));
        Assert.Null(Installer.RunningProcessFor(Path.Combine(_dir, "surely-not-running-" + Guid.NewGuid().ToString("N") + ".exe")));
    }
}
