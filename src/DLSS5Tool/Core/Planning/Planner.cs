using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;

namespace DLSS5Tool.Core.Planning;

public sealed class PlanOptions
{
    public GraphicsApi? ApiOverride { get; set; }
    /// <summary>Plan even when no RTX GPU is detected (CI, testing).</summary>
    public bool IgnoreGpu { get; set; }
    public int MvProvider { get; set; } = 3;
    public bool DgVoodooWatermark { get; set; }
}

/// <summary>
/// Turns a GameProfile into the exact list of files and edits. Pure: touches nothing on disk,
/// so the result can be shown to the user before anything happens.
/// </summary>
public static class Planner
{
    public const string ReShadeIniTemplate =
        "[ADDON]\r\nAddonPath=.\\\r\n\r\n[DEPTH]\r\nDepthCopyBeforeClears=0\r\n\r\n[GENERAL]\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\r\nIntermediateCachePath=\r\nNoDebugInfo=1\r\nNoEffectCache=0\r\nNoReloadOnInit=0\r\nPerformanceMode=0\r\nPreprocessorDefinitions=\r\nPresetPath=.\\ReShadePreset.ini\r\nPresetShortcutKeys=\r\nPresetShortcutPaths=\r\nPresetTransitionDuration=1000\r\nSkipLoadingDisabledEffects=0\r\nStartupPresetPath=\r\n\r\n[INPUT]\r\nForceShortcutModifiers=1\r\nInputProcessing=2\r\nKeyEffects=222,0,0,0\r\nKeyOverlay=36,0,0,0\r\nKeyReload=0,0,0,0\r\nKeyScreenshot=220,0,0,0\r\n\r\n[OVERLAY]\r\nTutorialProgress=4\r\n";

    public const string HostReShadeIni = "[GENERAL]\r\nEffectSearchPaths=.\\\r\nTextureSearchPaths=.\\\r\n";

    /// <summary>True when a ReShade proxy with this file name hooks the API the plan targets (OpenGL needs opengl32.dll; everything
    /// else, including the dgVoodoo2 wrapper routes, presents through DXGI).</summary>
    internal static bool ProxyFits(string reshadeFileName, GraphicsApi planned)
    {
        var hooks = GraphicsApiInfo.FromModuleName(reshadeFileName);
        if (hooks == GraphicsApi.Unknown) return false;
        return planned == GraphicsApi.OpenGL ? hooks == GraphicsApi.OpenGL : hooks.IsDxgi();
    }

    private static readonly string[] LumeniteShaders =
    {
        "Shaders/lumenite_Kernel.fx", "Shaders/lumenite_QuantMotion.fx", "Shaders/lumenite_AnamorphicBloom.fx", "Shaders/lumenite_LSAO.fx",
        "Shaders/lumenite_QuantAO.fx", "Shaders/lumenite_RTAO.fx", "Shaders/lumenite_SSSR.fx", "Shaders/lumenite_TRAA.fx",
    };

    private static readonly string[] LumeniteIncludes =
    {
        "Shaders/include/lumenite_ColorManagement.fxh", "Shaders/include/lumenite_Compute.fxh", "Shaders/include/lumenite_Helpers.fxh", "Shaders/include/lumenite_Projections.fxh",
    };

    public static InstallPlan Build(GameProfile p, GpuInfo gpu, ComponentCatalog catalog, PlanOptions? options = null)
    {
        options ??= new PlanOptions();
        if (options.ApiOverride != null) p.ApiOverride = options.ApiOverride;
        var api = p.EffectiveApi;

        string? reason = null;
        var route = Route.Unsupported;

        if (!gpu.IsRtx && !options.IgnoreGpu)
        {
            reason = $"DLSS needs an NVIDIA RTX GPU; this PC reports \"{gpu.Name}\".";
        }
        else if (p.RtxRemix)
        {
            reason = "This game runs under RTX Remix. DLSS 5 goes into the Remix runtime (rtx.conf), not through ReShade; this tool does not handle that route.";
        }
        else if (api == GraphicsApi.Unknown)
        {
            reason = "The graphics API could not be detected from the executable. Pick it manually (Direct3D 9/10/11/12, OpenGL or Vulkan) and plan again.";
        }
        else if (p.Is64Bit)
        {
            if (p.HasNativeDlss && api is GraphicsApi.D3D12 or GraphicsApi.D3D11) route = Route.NativeDlss64;
            else if (api is GraphicsApi.D3D11 or GraphicsApi.D3D12 or GraphicsApi.OpenGL or GraphicsApi.Vulkan or GraphicsApi.D3D9) route = Route.Feeder64;
            else if (api == GraphicsApi.D3D10) reason = "64-bit Direct3D 10 needs DLSS5-Feeder's 64-bit helper mode, which is still in beta upstream and not shipped by this tool.";
            else reason = $"{api.Display()} has no 64-bit wrapper path.";
        }
        else
        {
            if (api is GraphicsApi.D3D11 or GraphicsApi.D3D10 or GraphicsApi.OpenGL or GraphicsApi.Vulkan || api.IsLegacyDirectX()) route = Route.Feeder32;
            else reason = $"32-bit {api.Display()} is not supported by the 32-bit feeder (Direct3D 10/11, OpenGL, Vulkan and the Direct3D 8/9 wrapper path are).";
        }

        var usesDgVoodoo = route != Route.Unsupported && api.IsLegacyDirectX();
        var reshadeMode = api == GraphicsApi.Vulkan ? ReShadeMode.VulkanLayer : ReShadeMode.LocalDll;
        var reshadeName = api == GraphicsApi.OpenGL ? "opengl32.dll" : "dxgi.dll";

        if (route != Route.Unsupported && reshadeMode == ReShadeMode.LocalDll)
        {
            if (p.ReShade != null && !p.ReShade.FileName.Equals(reshadeName, StringComparison.OrdinalIgnoreCase))
            {
                if (usesDgVoodoo && p.ReShade.FileName.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "ReShade is already installed here as d3d9.dll, but dgVoodoo2 needs that file name. Uninstall that ReShade (or rename it to dxgi.dll) and try again.";
                    route = Route.Unsupported;
                }
                else if (ProxyFits(p.ReShade.FileName, api))
                {
                    // Same API, different proxy name (d3d11.dll instead of dxgi.dll): reuse what is there.
                    reshadeName = p.ReShade.FileName;
                }
                // Otherwise the existing ReShade hooks the wrong API (opengl32.dll on a Direct3D 11 game); it is disabled below
                // and the proper proxy is written, because a ReShade that loads first blocks the one that would do the work.
            }
            var target = Path.Combine(p.GameDir, reshadeName);
            if (route != Route.Unsupported && p.IsForeignFile(target) && (p.ReShade == null || !p.ReShade.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))
            {
                var product = GameAnalyzer.ProductName(target);
                reason = $"{reshadeName} next to the game is not ReShade ({(product.Length == 0 ? "no version info" : product)}). Move it away, or find out what needs it, then try again.";
                route = Route.Unsupported;
            }
        }

        var hostDir = route == Route.Feeder32 ? Path.Combine(p.GameDir, "host64") : null;
        var consumerDir = hostDir ?? p.GameDir;
        var nrComponent = gpu.Tier == GpuTier.Rtx50 || !gpu.IsRtx ? "nvngx-dlssnr" : "nvngx-dlssnr-sf";

        var plan = new InstallPlan
        {
            Profile = p,
            Gpu = gpu,
            Route = route,
            UnsupportedReason = reason,
            ReShadeMode = reshadeMode,
            ReShadeDllName = reshadeName,
            DgVoodooWrapper = usesDgVoodoo ? WrapperFor(api) : null,
            HostDir = hostDir,
            ConsumerDir = consumerDir,
            NrComponentId = nrComponent,
            MvProvider = options.MvProvider,
        };
        if (route == Route.Unsupported) return plan;

        var g = p.GameDir;
        var shaders = Path.Combine(g, "reshade-shaders", "Shaders");
        var textures = Path.Combine(g, "reshade-shaders", "Textures");
        var items = plan.Items;

        // 1. Conflicting add-ons and proxies are switched off reversibly (renamed), never deleted.
        foreach (var f in p.ForeignFiles)
        {
            items.Add(new PlanItem { Kind = PlanItemKind.DisableFile, Target = f.Path, Description = $"Disable {Path.GetFileName(f.Path)} ({f.Why})" });
            plan.Warnings.Add($"{Path.GetFileName(f.Path)} will be renamed to .disabled-by-dlss5tool: {f.Why}.");
        }

        // 2. ReShade.
        if (reshadeMode == ReShadeMode.VulkanLayer)
        {
            items.Add(new PlanItem { Kind = PlanItemKind.VulkanLayer, Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReShade"), Description = "Register ReShade's machine-wide Vulkan layer and add this game to ReShadeApps.ini (asks for administrator rights once)" });
            if (p.ReShade != null) plan.Warnings.Add($"A local {p.ReShade.FileName} ReShade is also present; the Vulkan layer is what will load for a Vulkan game.");
        }
        else
        {
            var target = Path.Combine(g, reshadeName);
            var existingHere = p.ReShade != null && p.ReShade.Path.Equals(target, StringComparison.OrdinalIgnoreCase);
            var keep = existingHere && p.ReShade!.IsUsable;
            if (keep)
            {
                plan.Notes.Add($"ReShade {p.ReShade!.Version} with add-on support is already installed as {reshadeName}; kept.");
            }
            else
            {
                if (existingHere)
                {
                    plan.Notes.Add($"Existing ReShade {p.ReShade!.Version} ({(p.ReShade.HasAddonSupport == false ? "no add-on support" : "too old")}) is replaced; the original is backed up.");
                }
                else if (p.ReShade != null)
                {
                    var hooks = GraphicsApiInfo.FromModuleName(p.ReShade.FileName);
                    plan.Warnings.Add($"{p.ReShade.FileName} is a ReShade for {(hooks == GraphicsApi.Unknown ? "another API" : hooks.Display())}, but this game renders with {api.Display()}. It is disabled (renamed) so it cannot load ahead of {reshadeName}, and restored on uninstall.");
                    items.Add(new PlanItem { Kind = PlanItemKind.DisableFile, Target = p.ReShade.Path, Description = $"Disable {p.ReShade.FileName} (ReShade for the wrong API)" });
                }
                items.Add(Component("reshade", p.Is64Bit ? "ReShade64.dll" : "ReShade32.dll", target, $"ReShade 6.8 ({(p.Is64Bit ? "64" : "32")}-bit) as {reshadeName}"));
            }
        }

        // 3. dgVoodoo2 in front of Direct3D 8/9/7 and Glide.
        if (usesDgVoodoo)
        {
            foreach (var (src, name) in WrapperFiles(api, p.Is64Bit))
            {
                var dest = Path.Combine(g, name);
                if (p.IsForeignFile(dest) && !GameAnalyzer.ProductName(dest).Contains("dgVoodoo", StringComparison.OrdinalIgnoreCase))
                {
                    plan.Warnings.Add($"{name} next to the game is not dgVoodoo ({GameAnalyzer.ProductName(dest)}); it is backed up and replaced by dgVoodoo's.");
                }
                items.Add(Component("dgvoodoo2", src, dest, $"dgVoodoo2 {name} ({(p.Is64Bit ? "x64" : "x86")}) translating {api.Display()} to Direct3D 11"));
            }
            items.Add(new PlanItem
            {
                Kind = PlanItemKind.DgVoodooConf,
                Target = Path.Combine(g, "dgVoodoo.conf"),
                ComponentId = "dgvoodoo2",
                ComponentPath = "dgVoodoo.conf",
                Description = "dgVoodoo.conf: OutputAPI=d3d11_fl11_0, DisableAndPassThru=false, VideoCard=internal3D, VRAM=1GB, watermark " + (options.DgVoodooWatermark ? "on" : "off"),
                Settings = { ["dgVoodooWatermark"] = options.DgVoodooWatermark ? "true" : "false" },
            });
            items.Add(Component("dgvoodoo2", "dgVoodooCpl.exe", Path.Combine(g, "dgVoodooCpl.exe"), "dgVoodoo control panel (optional tuning)"));
        }

        // 4. The add-ons and NVIDIA runtimes.
        if (route == Route.NativeDlss64)
        {
            items.Add(Component("renodx-dlss5", "renodx-dlss5.addon64", Path.Combine(g, "renodx-dlss5.addon64"), "RenoDX DLSS 5 Neural Rendering add-on"));
            items.Add(Component(nrComponent, "nvngx_dlssnr.dll", Path.Combine(g, "nvngx_dlssnr.dll"), "DLSS Neural Rendering model (" + catalog.Get(nrComponent).Version + ")"));
        }
        else
        {
            var addon = p.Is64Bit ? "dlss5-feed.addon64" : "dlss5-feed.addon32";
            items.Add(Component("dlss5-feeder", addon, Path.Combine(g, addon), "DLSS5-Feeder add-on (" + (p.Is64Bit ? "64" : "32") + "-bit)"));
            items.Add(Component("dlss5-feeder", "reshade-shaders/Shaders/DLSS5_Feed.fx", Path.Combine(shaders, "DLSS5_Feed.fx"), "DLSS5_Feed.fx companion effect"));
            items.Add(Component("dlss5-feeder", "Verify-DLSS5Feeder.ps1", Path.Combine(g, "Verify-DLSS5Feeder.ps1"), "upstream verification script (for support reports)"));
            foreach (var h in new[] { "ReShade.fxh", "ReShadeUI.fxh", "DrawText.fxh" })
            {
                if (FindUnder(shaders, h, p)) { plan.Notes.Add($"{h} already present under reshade-shaders; kept."); continue; }
                items.Add(Component("reshade-shaders", h, Path.Combine(shaders, h), "ReShade framework header " + h));
            }
            foreach (var s in LumeniteShaders) items.Add(Component("lumenitefx", s, Path.Combine(shaders, Path.GetFileName(s)), "LumeniteFX " + Path.GetFileName(s)));
            foreach (var s in LumeniteIncludes) items.Add(Component("lumenitefx", s, Path.Combine(shaders, "include", Path.GetFileName(s)), "LumeniteFX include " + Path.GetFileName(s)));
            items.Add(Component("lumenitefx", "Textures/lumenite_bluenoise256.png", Path.Combine(textures, "lumenite_bluenoise256.png"), "LumeniteFX blue-noise texture"));
            items.Add(Component("lumenitefx", "LICENSE.md", Path.Combine(g, "reshade-shaders", "LumeniteFX-LICENSE.md"), "LumeniteFX license"));
            items.Add(Component("lumenitefx", "NOTICE", Path.Combine(g, "reshade-shaders", "LumeniteFX-NOTICE.txt"), "LumeniteFX notice"));

            if (route == Route.Feeder32)
            {
                items.Add(Component("dlss5-feeder", "host64/dlss5-feed-host64.exe", Path.Combine(hostDir!, "dlss5-feed-host64.exe"), "64-bit helper process"));
                items.Add(Component("reshade", "ReShade64.dll", Path.Combine(hostDir!, "dxgi.dll"), "ReShade 6.8 (64-bit) for the helper, as host64\\dxgi.dll"));
                items.Add(new PlanItem { Kind = PlanItemKind.TextFile, Target = Path.Combine(hostDir!, "ReShade.ini"), Text = HostReShadeIni, OnlyIfMissing = true, Description = "host64\\ReShade.ini (minimal)" });
            }
            if (api == GraphicsApi.Vulkan)
            {
                var layerSrc = p.Is64Bit ? "layer-x64" : "layer-x86";
                foreach (var f in catalog.Get("dlss5-feeder").Files.Where(f => f.Path.StartsWith(layerSrc + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(Component("dlss5-feeder", f.Path, Path.Combine(g, "layer", Path.GetFileName(f.Path)), "Vulkan fallback layer " + Path.GetFileName(f.Path)));
                }
            }

            items.Add(Component("renodx-dlss5", "renodx-dlss5.addon64", Path.Combine(consumerDir, "renodx-dlss5.addon64"), "RenoDX DLSS 5 Neural Rendering add-on" + (hostDir != null ? " (in host64)" : string.Empty)));
            items.Add(Component(nrComponent, "nvngx_dlssnr.dll", Path.Combine(consumerDir, "nvngx_dlssnr.dll"), "DLSS Neural Rendering model (" + catalog.Get(nrComponent).Version + ")"));
            items.Add(Component("nvngx-dlss", "nvngx_dlss.dll", Path.Combine(consumerDir, "nvngx_dlss.dll"), "DLSS Super Resolution runtime (" + catalog.Get("nvngx-dlss").Version + ")", onlyIfMissing: true));
        }

        // 5. Configuration files.
        items.Add(new PlanItem
        {
            Kind = PlanItemKind.ReShadeIni,
            Target = Path.Combine(g, "ReShade.ini"),
            Description = "ReShade.ini: add-on path, shader search paths" + (usesDgVoodoo ? ", depth copy before clears" : string.Empty),
            Settings = { ["DepthCopyBeforeClears"] = usesDgVoodoo ? "1" : "0", ["Feeder"] = route == Route.NativeDlss64 ? "0" : "1" },
        });
        if (route != Route.NativeDlss64)
        {
            items.Add(new PlanItem
            {
                Kind = PlanItemKind.ReShadePreset,
                Target = Path.Combine(g, "ReShadePreset.ini"),
                Description = $"ReShadePreset.ini: LUMENITE Kernel then DLSS 5 Feed enabled in that order, DLSS5_MV_PROVIDER={options.MvProvider}",
                Settings = { ["MvProvider"] = options.MvProvider.ToString() },
            });
        }

        // 6. Warnings and the in-game steps.
        if (gpu.IsRtx && gpu.Tier != GpuTier.Rtx50)
        {
            plan.Warnings.Add($"{gpu.Name}: NVIDIA's reference neural model only runs on RTX 50. The community build for RTX 20/30/40 (310.8.SF-v2) is used instead; expect a noticeably higher frame cost.");
        }
        if (!gpu.IsRtx) plan.Warnings.Add("No RTX GPU detected (planning forced); DLSS will not run on this machine.");
        if (p.AgilitySdkRedist) plan.Warnings.Add("The game ships a D3D12 Agility SDK folder (D3D12\\D3D12Core.dll). If dlss5-feed.log reports D3D12CreateDevice 0x887E0003, rename that folder to D3D12_off and relaunch.");
        if (route == Route.Feeder64 && p.HasNativeDlss) plan.Notes.Add("The game ships its own DLSS runtime, but this API goes through the feeder; the feeder keeps its own private session.");
        if (api == GraphicsApi.Vulkan) plan.Warnings.Add("Vulkan: turn NVIDIA Smooth Motion off for this game (NVIDIA app / Profile Inspector); the two cannot work together on Vulkan.");
        if (api == GraphicsApi.OpenGL) plan.Notes.Add("OpenGL: on a hybrid laptop force the game onto the NVIDIA GPU (Windows Settings > Display > Graphics).");
        if (p.Apis.Count > 1) plan.Notes.Add($"The executable touches several APIs ({p.ApiSummary}); planned for {api.Display()}. Change it in the API box if the game runs on another one.");
        if (route == Route.Feeder32 && api == GraphicsApi.Vulkan) plan.Notes.Add("32-bit Vulkan (DXVK): DLSS runs at native size; the work-resolution slider is fixed at 100%.");

        plan.PostInstallSteps.Add("Launch the game. Press Home for the ReShade overlay and check there are no compile errors.");
        if (route == Route.NativeDlss64)
        {
            plan.PostInstallSteps.Add("In the game's own settings turn DLSS Super Resolution (or DLAA) on; the add-on needs the game's DLSS calls.");
            plan.PostInstallSteps.Add("ReShade overlay > Add-ons tab > DLSS 5 Neural Rendering: tick \"Enable DLSS Neural Rendering\".");
        }
        else
        {
            plan.PostInstallSteps.Add("Both techniques are already ticked, in this order: LUMENITE: Kernel 2.0 above DLSS 5 Feed.");
            if (route == Route.Feeder32)
            {
                plan.PostInstallSteps.Add("The neural panel lives in the helper: ReShade overlay > Add-ons > DLSS 5 Feed > \"Show the DLSS 5 panel in-game\", then tick \"Enable DLSS Neural Rendering\".");
            }
            else
            {
                plan.PostInstallSteps.Add("ReShade overlay > Add-ons tab > DLSS 5 Neural Rendering: tick \"Enable DLSS Neural Rendering\".");
            }
            plan.PostInstallSteps.Add("Turn the game's MSAA/SSAA off.");
            plan.PostInstallSteps.Add("dlss5-feed.log next to the game (host64\\dlss5-feed-host.log for 32-bit) should say \"feature ready\" and \"frame N delivered\".");
        }
        if (usesDgVoodoo && !options.DgVoodooWatermark) plan.PostInstallSteps.Add("dgVoodoo's watermark is off. If nothing seems to happen, enable dgVoodooWatermark=true in dgVoodoo.conf to confirm dgVoodoo is active.");
        if (api == GraphicsApi.Vulkan) plan.PostInstallSteps.Add("If dlss5-feed.log says the Vulkan interop entry points are missing, launch through layer\\run-with-feed-layer" + (p.Is64Bit ? string.Empty : "32") + ".bat \"<game exe>\".");

        return plan;
    }

    private static PlanItem Component(string id, string path, string target, string description, bool onlyIfMissing = false) => new()
    {
        Kind = PlanItemKind.ComponentFile,
        ComponentId = id,
        ComponentPath = path,
        Target = target,
        Description = description,
        OnlyIfMissing = onlyIfMissing,
    };

    private static string WrapperFor(GraphicsApi api) => api switch
    {
        GraphicsApi.D3D9 => "D3D9.dll",
        GraphicsApi.D3D8 => "D3D8.dll",
        GraphicsApi.DDraw => "DDraw.dll",
        GraphicsApi.Glide => "Glide2x.dll",
        _ => "D3D9.dll",
    };

    private static IEnumerable<(string src, string name)> WrapperFiles(GraphicsApi api, bool is64)
    {
        var arch = is64 ? "x64" : "x86";
        switch (api)
        {
            case GraphicsApi.D3D9:
                yield return ($"MS/{arch}/D3D9.dll", "D3D9.dll");
                break;
            case GraphicsApi.D3D8:
                yield return ("MS/x86/D3D8.dll", "D3D8.dll");
                break;
            case GraphicsApi.DDraw:
                yield return ("MS/x86/DDraw.dll", "DDraw.dll");
                yield return ("MS/x86/D3DImm.dll", "D3DImm.dll");
                break;
            case GraphicsApi.Glide:
                yield return ("3Dfx/x86/Glide.dll", "Glide.dll");
                yield return ("3Dfx/x86/Glide2x.dll", "Glide2x.dll");
                yield return ("3Dfx/x86/Glide3x.dll", "Glide3x.dll");
                break;
        }
    }

    private static bool FindUnder(string dir, string fileName, GameProfile p)
    {
        if (!Directory.Exists(dir)) return false;
        try { return Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).Any(p.IsForeignFile); }
        catch { return false; }
    }
}
