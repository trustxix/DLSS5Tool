namespace DLSS5Tool.Core;

public enum GraphicsApi
{
    Unknown = 0,
    Glide,
    DDraw,
    D3D8,
    D3D9,
    D3D10,
    D3D11,
    D3D12,
    OpenGL,
    Vulkan,
}

public static class GraphicsApiInfo
{
    /// <summary>Import/module names that identify each API, lower-case.</summary>
    public static readonly (string module, GraphicsApi api)[] ModuleMap =
    {
        ("vulkan-1.dll", GraphicsApi.Vulkan),
        ("d3d12.dll", GraphicsApi.D3D12),
        ("d3d11.dll", GraphicsApi.D3D11),
        ("dxgi.dll", GraphicsApi.D3D11),
        ("d3d10_1.dll", GraphicsApi.D3D10),
        ("d3d10.dll", GraphicsApi.D3D10),
        ("d3d10core.dll", GraphicsApi.D3D10),
        ("d3d9.dll", GraphicsApi.D3D9),
        ("d3d8.dll", GraphicsApi.D3D8),
        ("ddraw.dll", GraphicsApi.DDraw),
        ("opengl32.dll", GraphicsApi.OpenGL),
        ("glide2x.dll", GraphicsApi.Glide),
        ("glide3x.dll", GraphicsApi.Glide),
        ("glide.dll", GraphicsApi.Glide),
    };

    public static GraphicsApi FromModuleName(string module)
    {
        var m = module.ToLowerInvariant();
        foreach (var (name, api) in ModuleMap)
        {
            if (m == name) return api;
        }
        return GraphicsApi.Unknown;
    }

    public static bool TryParse(string text, out GraphicsApi api)
    {
        switch (text.Trim().ToLowerInvariant().Replace("directx", "d3d").Replace("dx", "d3d"))
        {
            case "d3d8": api = GraphicsApi.D3D8; return true;
            case "d3d9": api = GraphicsApi.D3D9; return true;
            case "d3d10": api = GraphicsApi.D3D10; return true;
            case "d3d11": api = GraphicsApi.D3D11; return true;
            case "d3d12": api = GraphicsApi.D3D12; return true;
            case "dxgi": api = GraphicsApi.D3D11; return true;
            case "ddraw": case "d3d7": api = GraphicsApi.DDraw; return true;
            case "opengl": case "gl": api = GraphicsApi.OpenGL; return true;
            case "vulkan": case "vk": api = GraphicsApi.Vulkan; return true;
            case "glide": api = GraphicsApi.Glide; return true;
            default: api = GraphicsApi.Unknown; return false;
        }
    }

    public static string Display(this GraphicsApi api) => api switch
    {
        GraphicsApi.Glide => "3dfx Glide",
        GraphicsApi.DDraw => "DirectDraw / Direct3D 7",
        GraphicsApi.D3D8 => "Direct3D 8",
        GraphicsApi.D3D9 => "Direct3D 9",
        GraphicsApi.D3D10 => "Direct3D 10",
        GraphicsApi.D3D11 => "Direct3D 11",
        GraphicsApi.D3D12 => "Direct3D 12",
        GraphicsApi.OpenGL => "OpenGL",
        GraphicsApi.Vulkan => "Vulkan",
        _ => "Unknown",
    };

    public static bool IsLegacyDirectX(this GraphicsApi api) =>
        api is GraphicsApi.D3D9 or GraphicsApi.D3D8 or GraphicsApi.DDraw or GraphicsApi.Glide;

    public static bool IsDxgi(this GraphicsApi api) =>
        api is GraphicsApi.D3D10 or GraphicsApi.D3D11 or GraphicsApi.D3D12;

    /// <summary>
    /// ReShade's own preference order when an executable touches several APIs: Vulkan over DXGI, DXGI over D3D9,
    /// everything over OpenGL. DXVK-style wrappers are handled by the analyzer before this is consulted.
    /// </summary>
    public static GraphicsApi PickPrimary(IEnumerable<GraphicsApi> apis)
    {
        var set = apis.ToHashSet();
        if (set.Contains(GraphicsApi.Vulkan)) return GraphicsApi.Vulkan;
        if (set.Contains(GraphicsApi.D3D12)) return GraphicsApi.D3D12;
        if (set.Contains(GraphicsApi.D3D11)) return GraphicsApi.D3D11;
        if (set.Contains(GraphicsApi.D3D10)) return GraphicsApi.D3D10;
        if (set.Contains(GraphicsApi.D3D9)) return GraphicsApi.D3D9;
        if (set.Contains(GraphicsApi.D3D8)) return GraphicsApi.D3D8;
        if (set.Contains(GraphicsApi.DDraw)) return GraphicsApi.DDraw;
        if (set.Contains(GraphicsApi.OpenGL)) return GraphicsApi.OpenGL;
        if (set.Contains(GraphicsApi.Glide)) return GraphicsApi.Glide;
        return GraphicsApi.Unknown;
    }
}
