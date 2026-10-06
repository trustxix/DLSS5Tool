using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DLSS5Tool.Core.Components;

public sealed class ComponentFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
}

public sealed class ComponentSource
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    /// <summary>zip | reshade-setup (a zip appended to a .NET exe) | file</summary>
    [JsonPropertyName("format")] public string Format { get; set; } = "zip";
    [JsonPropertyName("stripPrefix")] public string? StripPrefix { get; set; }
}

public sealed class Component
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("license")] public string License { get; set; } = string.Empty;
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = string.Empty;
    /// <summary>bundled | on-demand</summary>
    [JsonPropertyName("distribution")] public string Distribution { get; set; } = "on-demand";
    [JsonPropertyName("gpuTiers")] public List<string>? GpuTiers { get; set; }
    [JsonPropertyName("source")] public ComponentSource? Source { get; set; }
    [JsonPropertyName("files")] public List<ComponentFile> Files { get; set; } = new();

    public bool IsBundled => Distribution.Equals("bundled", StringComparison.OrdinalIgnoreCase);

    public ComponentFile File(string path)
    {
        var f = Files.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        return f ?? throw new KeyNotFoundException($"Component '{Id}' has no file '{path}'.");
    }
}

public sealed class ComponentCatalog
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("components")] public List<Component> Components { get; set; } = new();

    public Component Get(string id)
    {
        return Components.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"Unknown component '{id}'.");
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static ComponentCatalog Parse(string json)
    {
        var cat = JsonSerializer.Deserialize<ComponentCatalog>(json, JsonOptions)
                  ?? throw new InvalidDataException("components.json is empty.");
        Validate(cat);
        return cat;
    }

    public static ComponentCatalog LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("components.json")
                      ?? throw new InvalidOperationException("components.json is not embedded in this build.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    private static void Validate(ComponentCatalog cat)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cat.Components)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || !ids.Add(c.Id)) throw new InvalidDataException($"Duplicate or empty component id '{c.Id}'.");
            if (c.Files.Count == 0) throw new InvalidDataException($"Component '{c.Id}' lists no files.");
            foreach (var f in c.Files)
            {
                if (f.Sha256.Length != 64) throw new InvalidDataException($"Component '{c.Id}' file '{f.Path}' has no SHA-256.");
                if (c.Source == null && string.IsNullOrWhiteSpace(f.Url)) throw new InvalidDataException($"Component '{c.Id}' file '{f.Path}' has neither a source archive nor a URL.");
                if (f.Path.Contains("..") || System.IO.Path.IsPathRooted(f.Path)) throw new InvalidDataException($"Component '{c.Id}' file path '{f.Path}' is not relative.");
            }
        }
    }
}
