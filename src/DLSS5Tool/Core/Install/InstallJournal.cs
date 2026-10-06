using System.Text.Json;
using System.Text.Json.Serialization;

namespace DLSS5Tool.Core.Install;

public sealed class JournalEntry
{
    /// <summary>Path relative to the game folder.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    /// <summary>created | replaced | merged | disabled</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    /// <summary>Backup copy, relative to the backup folder (replaced/merged only).</summary>
    [JsonPropertyName("backup")] public string? Backup { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>
/// Record of exactly what one install wrote, kept next to the game as DLSS5Tool-install.json so an uninstall
/// restores the folder byte for byte: created files are deleted, replaced files come back from the backup folder.
/// </summary>
public sealed class InstallJournal
{
    [JsonPropertyName("toolVersion")] public string ToolVersion { get; set; } = string.Empty;
    [JsonPropertyName("exePath")] public string ExePath { get; set; } = string.Empty;
    [JsonPropertyName("route")] public string Route { get; set; } = string.Empty;
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("is64Bit")] public bool Is64Bit { get; set; }
    [JsonPropertyName("installedUtc")] public DateTime InstalledUtc { get; set; }
    [JsonPropertyName("backupDir")] public string BackupDir { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<JournalEntry> Entries { get; set; } = new();
    [JsonPropertyName("vulkanAppsEntry")] public string? VulkanAppsEntry { get; set; }
    [JsonPropertyName("components")] public Dictionary<string, string> Components { get; set; } = new();
    [JsonPropertyName("gpu")] public string? Gpu { get; set; }
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();

    public static string PathFor(string gameDir) => System.IO.Path.Combine(gameDir, AppPaths.JournalFileName);

    public static InstallJournal? TryLoad(string gameDir)
    {
        var p = PathFor(gameDir);
        if (!File.Exists(p)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallJournal>(File.ReadAllText(p), Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"Unreadable journal {p}: {ex.Message}");
            return null;
        }
    }

    public void Save(string gameDir)
    {
        File.WriteAllText(PathFor(gameDir), JsonSerializer.Serialize(this, Options));
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
