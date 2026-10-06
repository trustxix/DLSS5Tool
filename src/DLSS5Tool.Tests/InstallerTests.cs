using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Games;
using DLSS5Tool.Core.Install;
using DLSS5Tool.Core.Planning;
using Microsoft.Win32;
using Xunit;

namespace DLSS5Tool.Tests;

public class InstallerTests : IDisposable
{
    private readonly string _root = TestSupport.NewTempDir("inst");
    private readonly ComponentStore _store;
    private static readonly GpuInfo Rtx50 = new("NVIDIA GeForce RTX 5070 Ti", GpuTier.Rtx50, "616.92");

    public InstallerTests()
    {
        _store = TestSupport.FakeStore(_root);
    }

    public void Dispose() => TestSupport.TryDelete(_root);

    private string MakeGame(string name, bool is64)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, name + ".exe");
        File.Copy(is64 ? TestSupport.Exe64 : TestSupport.Exe32, exe);
        return exe;
    }

    private static string[] Snapshot(string dir) => Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).OrderBy(p => p).ToArray();

    [Fact]
    public async Task Feeder64_InstallThenUninstall_RestoresFolder()
    {
        var exe = MakeGame("Game64", true);
        var dir = Path.GetDirectoryName(exe)!;
        var before = Snapshot(dir);

        var profile = GameAnalyzer.Analyze(exe);
        Assert.True(profile.Is64Bit);
        var plan = Planner.Build(profile, Rtx50, _store.Catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D11 });
        Assert.Equal(Route.Feeder64, plan.Route);

        var installer = new Installer(_store);
        var journal = await installer.InstallAsync(plan);

        Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "dlss5-feed.addon64")));
        Assert.True(File.Exists(Path.Combine(dir, "renodx-dlss5.addon64")));
        Assert.True(File.Exists(Path.Combine(dir, "nvngx_dlssnr.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "nvngx_dlss.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "reshade-shaders", "Shaders", "DLSS5_Feed.fx")));
        Assert.True(File.Exists(Path.Combine(dir, "reshade-shaders", "Shaders", "lumenite_Kernel.fx")));
        Assert.True(File.Exists(Path.Combine(dir, "reshade-shaders", "Shaders", "include", "lumenite_Helpers.fxh")));
        Assert.True(File.Exists(Path.Combine(dir, "reshade-shaders", "Textures", "lumenite_bluenoise256.png")));
        Assert.True(File.Exists(Path.Combine(dir, AppPaths.JournalFileName)));

        var ini = IniFile.Load(Path.Combine(dir, "ReShade.ini"));
        Assert.Equal(".\\", ini.Get("ADDON", "AddonPath"));
        Assert.Equal(".\\reshade-shaders\\Shaders\\**", ini.Get("GENERAL", "EffectSearchPaths"));
        Assert.Equal("0", ini.Get("DEPTH", "DepthCopyBeforeClears"));
        var preset = IniFile.Load(Path.Combine(dir, "ReShadePreset.ini"));
        Assert.Equal(new[] { "Lumenite_Kernel@lumenite_Kernel.fx", "DLSS5_Feed@DLSS5_Feed.fx" }, preset.GetList(string.Empty, "Techniques"));
        Assert.Equal("DLSS5_MV_PROVIDER=3", preset.Get("DLSS5_Feed.fx", "PreprocessorDefinitions"));

        Assert.All(journal.Entries, e => Assert.Equal("created", e.Action));
        Assert.Equal("test-" + ComponentCatalog.LoadEmbedded().Get("dlss5-feeder").Version, journal.Components["dlss5-feeder"]);

        // A second analysis must not mistake our own nvngx_dlss.dll for native DLSS.
        var again = GameAnalyzer.Analyze(exe);
        Assert.True(again.IsInstalled);
        Assert.False(again.HasNativeDlss);

        var results = Verifier.Verify(exe, _store.Catalog, Rtx50);
        Assert.DoesNotContain(results, r => r.Status == VerifyStatus.Fail && !r.Text.Contains("ReShade", StringComparison.Ordinal));

        await installer.UninstallAsync(dir, InstallJournal.TryLoad(dir)!);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public async Task ExistingConfigsAreMergedAndRestored()
    {
        var exe = MakeGame("Merge64", true);
        var dir = Path.GetDirectoryName(exe)!;
        var iniText = "[GENERAL]\r\nEffectSearchPaths=.\\custom\\**\r\nPresetPath=.\\MyPreset.ini\r\nNoDebugInfo=1\r\n";
        File.WriteAllText(Path.Combine(dir, "ReShade.ini"), iniText);
        var presetText = "Techniques=Clarity@Clarity.fx,DLSS5_Feed@DLSS5_Feed.fx\r\nTechniqueSorting=Clarity@Clarity.fx\r\n\r\n[Clarity.fx]\r\nStrength=1\r\n";
        File.WriteAllText(Path.Combine(dir, "MyPreset.ini"), presetText);
        var before = Snapshot(dir);

        var plan = Planner.Build(GameAnalyzer.Analyze(exe), Rtx50, _store.Catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D11 });
        var installer = new Installer(_store);
        var journal = await installer.InstallAsync(plan);

        var ini = IniFile.Load(Path.Combine(dir, "ReShade.ini"));
        Assert.Equal(".\\reshade-shaders\\Shaders\\**", ini.Get("GENERAL", "EffectSearchPaths"));
        Assert.Equal(".\\MyPreset.ini", ini.Get("GENERAL", "PresetPath"));
        Assert.Equal("1", ini.Get("GENERAL", "NoDebugInfo"));
        Assert.Equal(".\\", ini.Get("ADDON", "AddonPath"));
        var preset = IniFile.Load(Path.Combine(dir, "MyPreset.ini"));
        Assert.Equal(new[] { "Clarity@Clarity.fx", "Lumenite_Kernel@lumenite_Kernel.fx", "DLSS5_Feed@DLSS5_Feed.fx" }, preset.GetList(string.Empty, "Techniques"));
        Assert.Equal(new[] { "Clarity@Clarity.fx", "Lumenite_Kernel@lumenite_Kernel.fx", "DLSS5_Feed@DLSS5_Feed.fx", "DLSS5_Feed_Debug@DLSS5_Feed.fx" }, preset.GetList(string.Empty, "TechniqueSorting"));
        Assert.Equal("1", preset.Get("Clarity.fx", "Strength"));
        Assert.Contains(journal.Entries, e => e.Path == "ReShade.ini" && e.Action == "merged" && e.Backup != null);
        Assert.Contains(journal.Entries, e => e.Path == "MyPreset.ini" && e.Action == "merged");
        Assert.False(File.Exists(Path.Combine(dir, "ReShadePreset.ini")));

        await installer.UninstallAsync(dir, journal);
        Assert.Equal(before, Snapshot(dir));
        Assert.Equal(iniText, File.ReadAllText(Path.Combine(dir, "ReShade.ini")));
        Assert.Equal(presetText, File.ReadAllText(Path.Combine(dir, "MyPreset.ini")));
    }

    [Fact]
    public async Task Feeder32_D3D9_WritesHostAndDgVoodoo()
    {
        if (!File.Exists(TestSupport.Exe32)) return;
        var exe = MakeGame("Game32", false);
        var dir = Path.GetDirectoryName(exe)!;
        File.WriteAllText(Path.Combine(dir, "winmm.dll"), "fake optiscaler");
        var before = Snapshot(dir);

        var profile = GameAnalyzer.Analyze(exe);
        Assert.False(profile.Is64Bit);
        // winmm.dll without OptiScaler version info is not flagged; simulate a flagged one through the profile.
        profile.ForeignFiles.Add(new ForeignFile(Path.Combine(dir, "winmm.dll"), "OptiScaler", "test"));
        var plan = Planner.Build(profile, Rtx50, _store.Catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D9 });
        Assert.Equal(Route.Feeder32, plan.Route);

        var installer = new Installer(_store);
        var journal = await installer.InstallAsync(plan);

        Assert.True(File.Exists(Path.Combine(dir, "D3D9.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "dlss5-feed.addon32")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "dlss5-feed-host64.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "renodx-dlss5.addon64")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "nvngx_dlssnr.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "host64", "nvngx_dlss.dll")));
        Assert.Equal(Planner.HostReShadeIni, File.ReadAllText(Path.Combine(dir, "host64", "ReShade.ini")));
        Assert.False(File.Exists(Path.Combine(dir, "winmm.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "winmm.dll.disabled-by-dlss5tool")));
        var conf = File.ReadAllText(Path.Combine(dir, "dgVoodoo.conf"));
        Assert.Contains("OutputAPI                            = d3d11_fl11_0", conf);
        Assert.Contains("VRAM                                = 1GB", conf);
        Assert.Contains("dgVoodooWatermark                   = false", conf);
        Assert.Contains("DisableAndPassThru                  = false", conf);
        Assert.Equal("1", IniFile.Load(Path.Combine(dir, "ReShade.ini")).Get("DEPTH", "DepthCopyBeforeClears"));

        await installer.UninstallAsync(dir, journal);
        Assert.Equal(before, Snapshot(dir));
        Assert.Equal("fake optiscaler", File.ReadAllText(Path.Combine(dir, "winmm.dll")));
    }

    [Fact]
    public async Task ReinstallOverExistingInstallIsClean()
    {
        var exe = MakeGame("Twice64", true);
        var dir = Path.GetDirectoryName(exe)!;
        var before = Snapshot(dir);
        var installer = new Installer(_store);
        var plan1 = Planner.Build(GameAnalyzer.Analyze(exe), Rtx50, _store.Catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D12 });
        await installer.InstallAsync(plan1);
        var plan2 = Planner.Build(GameAnalyzer.Analyze(exe), Rtx50, _store.Catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D12 });
        Assert.True(plan2.Profile.IsInstalled);
        var journal2 = await installer.InstallAsync(plan2);
        // The first install was removed before the second one ran, so nothing was replaced and no backup folder lingers.
        Assert.False(Directory.Exists(Path.Combine(dir, AppPaths.BackupDirName)));
        Assert.All(journal2.Entries, e => Assert.Equal("created", e.Action));
        Assert.True(File.Exists(Path.Combine(dir, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "renodx-dlss5.addon64")));
        await installer.UninstallAsync(dir, journal2);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public async Task MissingComponentRollsBack()
    {
        var exe = MakeGame("Rollback64", true);
        var dir = Path.GetDirectoryName(exe)!;
        var before = Snapshot(dir);
        var (catalog, _) = TestSupport.FakeComponents();
        var emptyStore = new ComponentStore(catalog, Path.Combine(_root, "cache2"), Path.Combine(_root, "local2")) { AllowNetwork = false, PayloadOpener = () => null };
        var plan = Planner.Build(GameAnalyzer.Analyze(exe), Rtx50, catalog, new PlanOptions { ApiOverride = GraphicsApi.D3D11 });
        await Assert.ThrowsAsync<ComponentUnavailableException>(() => new Installer(emptyStore).InstallAsync(plan));
        Assert.Equal(before, Snapshot(dir));
    }
}

public class ComponentStoreTests : IDisposable
{
    private readonly string _root = TestSupport.NewTempDir("store");

    public void Dispose() => TestSupport.TryDelete(_root);

    [Fact]
    public async Task PayloadFilesAreExtractedAndVerified()
    {
        var store = TestSupport.FakeStore(_root);
        Assert.True(store.HasEmbeddedPayload());
        Assert.True(store.IsAvailableOffline("dlss5-feeder"));
        var p = await store.GetFileAsync("dlss5-feeder", "dlss5-feed.addon64");
        Assert.True(File.Exists(p));
        Assert.StartsWith("fake dlss5-feeder/dlss5-feed.addon64", File.ReadAllText(p));
        var all = await store.GetAllAsync("reshade");
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task OverrideFolderWins()
    {
        var store = TestSupport.FakeStore(_root);
        var ov = Path.Combine(store.OverrideRoot, "nvngx-dlss", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(ov)!);
        File.WriteAllText(ov, "user supplied");
        var p = await store.GetFileAsync("nvngx-dlss", "nvngx_dlss.dll");
        Assert.Equal(ov, p);
    }

    [Fact]
    public async Task CorruptCacheIsRefreshedFromPayload()
    {
        var store = TestSupport.FakeStore(_root);
        var p = await store.GetFileAsync("renodx-dlss5", "renodx-dlss5.addon64");
        File.WriteAllText(p, "corrupted");
        var p2 = await store.GetFileAsync("renodx-dlss5", "renodx-dlss5.addon64");
        Assert.Equal(p, p2);
        Assert.StartsWith("fake renodx-dlss5", File.ReadAllText(p2));
    }

    [Fact]
    public void RealCatalogIsValidAndComplete()
    {
        var cat = ComponentCatalog.LoadEmbedded();
        foreach (var id in new[] { "reshade", "dlss5-feeder", "reshade-shaders", "dgvoodoo2", "lumenitefx", "renodx-dlss5", "nvngx-dlssnr", "nvngx-dlssnr-sf", "nvngx-dlss" })
        {
            var c = cat.Get(id);
            Assert.NotEmpty(c.Files);
            Assert.All(c.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            if (c.Source != null) Assert.StartsWith("https://", c.Source.Url);
            else Assert.All(c.Files, f => Assert.StartsWith("https://", f.Url));
        }
        Assert.Contains(cat.Get("nvngx-dlssnr").GpuTiers!, t => t == "rtx50");
        Assert.Contains(cat.Get("nvngx-dlssnr-sf").GpuTiers!, t => t == "rtx40");
    }
}

public class ExeLocatorTests : IDisposable
{
    private readonly string _root = TestSupport.NewTempDir("exe");

    public void Dispose() => TestSupport.TryDelete(_root);

    [Fact]
    public void PrefersShippingBinaryOverHelpers()
    {
        var game = Path.Combine(_root, "Cool Game");
        Directory.CreateDirectory(Path.Combine(game, "CoolGame", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(game, "Engine", "Extras", "Redist"));
        File.Copy(TestSupport.Exe64, Path.Combine(game, "CoolGame.exe"));
        File.Copy(TestSupport.Exe64, Path.Combine(game, "CoolGame", "Binaries", "Win64", "CoolGame-Win64-Shipping.exe"));
        File.Copy(TestSupport.Exe64, Path.Combine(game, "CoolGame", "Binaries", "Win64", "CrashReportClient.exe"));
        File.Copy(TestSupport.Exe64, Path.Combine(game, "UnityCrashHandler64.exe"));
        var c = ExeLocator.FindCandidates(game);
        Assert.Equal("CoolGame-Win64-Shipping.exe", c[0].Name);
        Assert.Equal("CoolGame.exe", c[1].Name);
        Assert.True(c.Last().Score < 0);
    }

    [Fact]
    public void DeclaredExeAlwaysWins()
    {
        var game = Path.Combine(_root, "Declared");
        Directory.CreateDirectory(game);
        File.Copy(TestSupport.Exe64, Path.Combine(game, "Big.exe"));
        File.Copy(TestSupport.Exe64, Path.Combine(game, "Launcher.exe"));
        var c = ExeLocator.FindCandidates(game, Path.Combine(game, "Launcher.exe"));
        Assert.Equal("Launcher.exe", c[0].Name);
        Assert.Contains("declared by the launcher", c[0].Reason);
    }
}

public class VulkanLayerTests : IDisposable
{
    private readonly string _root = TestSupport.NewTempDir("vk");
    private readonly string _regRoot = @"Software\DLSS5ToolTests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        TestSupport.TryDelete(_root);
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\DLSS5ToolTests", false); } catch { /* ignore */ }
    }

    [Fact]
    public void InstallsAndRemovesWithoutTouchingHklm()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        foreach (var n in new[] { "ReShade32.dll", "ReShade64.dll", "ReShade32.json", "ReShade64.json" }) File.WriteAllText(Path.Combine(src, n), n);
        var layerRoot = Path.Combine(_root, "ProgramData", "ReShade");
        var exe = Path.Combine(_root, "Game", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(_root, "existing.txt"), "x");

        var spec = new VulkanLayerSpec
        {
            Action = "install", ExePath = exe, Is64Bit = true,
            Dll32 = Path.Combine(src, "ReShade32.dll"), Dll64 = Path.Combine(src, "ReShade64.dll"),
            Json32 = Path.Combine(src, "ReShade32.json"), Json64 = Path.Combine(src, "ReShade64.json"),
            LayerRoot = layerRoot, RegistryRoot = _regRoot,
        };
        File.WriteAllText(Path.Combine(_root, "apps-seed.ini"), "Apps=C:\\old\\game.exe,C:\\other\\x.exe\r\n");
        Directory.CreateDirectory(layerRoot);
        File.Copy(Path.Combine(_root, "apps-seed.ini"), Path.Combine(layerRoot, "ReShadeApps.ini"));
        VulkanLayer.Execute(spec);

        Assert.True(File.Exists(Path.Combine(layerRoot, "ReShade64.dll")));
        Assert.True(File.Exists(Path.Combine(layerRoot, "ReShade32.json")));
        var apps = IniFile.Load(Path.Combine(layerRoot, "ReShadeApps.ini")).GetList(string.Empty, "Apps");
        Assert.Equal(new[] { "C:\\other\\x.exe", exe }, apps);
        using (var k = Registry.CurrentUser.OpenSubKey(_regRoot + @"\Software\Khronos\Vulkan\ImplicitLayers"))
        {
            Assert.NotNull(k);
            Assert.Equal(0, k!.GetValue(Path.Combine(layerRoot, "ReShade64.json")));
        }

        VulkanLayer.Execute(new VulkanLayerSpec { Action = "remove", ExePath = exe, Is64Bit = true, LayerRoot = layerRoot, RegistryRoot = _regRoot });
        apps = IniFile.Load(Path.Combine(layerRoot, "ReShadeApps.ini")).GetList(string.Empty, "Apps");
        Assert.Equal(new[] { "C:\\other\\x.exe" }, apps);
    }
}
