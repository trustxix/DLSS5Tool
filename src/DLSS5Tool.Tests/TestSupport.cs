using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DLSS5Tool.Core;
using DLSS5Tool.Core.Components;

namespace DLSS5Tool.Tests;

/// <summary>Temp folders, fake executables and a fake component payload shared by the tests.</summary>
public static class TestSupport
{
    private static readonly string[] GuiStubs = { "charmap.exe", "rundll32.exe", "write.exe", "notepad.exe", "explorer.exe", "mstsc.exe" };

    /// <summary>A real 64-bit GUI executable shipped with Windows (the exact one varies by Windows build).</summary>
    public static string Exe64 => Pick(Environment.GetFolderPath(Environment.SpecialFolder.System));

    /// <summary>A real 32-bit GUI executable from SysWOW64, or null on a machine without it.</summary>
    public static string Exe32 => Pick(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));

    private static string Pick(string dir)
    {
        foreach (var n in GuiStubs)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(dir, GuiStubs[0]);
    }

    public static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DLSS5Tool.Tests", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    /// <summary>A catalog whose ids and file paths mirror the real components.json, with tiny fake contents.</summary>
    public static (ComponentCatalog catalog, byte[] payloadZip) FakeComponents()
    {
        var real = ComponentCatalog.LoadEmbedded();
        var files = new Dictionary<string, byte[]>();
        var sb = new StringBuilder();
        sb.Append("{\"schemaVersion\":1,\"components\":[");
        var first = true;
        foreach (var c in real.Components)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(c.Id));
            sb.Append(",\"name\":").Append(JsonSerializer.Serialize(c.Name));
            sb.Append(",\"version\":").Append(JsonSerializer.Serialize("test-" + c.Version));
            sb.Append(",\"license\":\"test\",\"homepage\":\"https://example.invalid\",\"distribution\":\"bundled\"");
            if (c.GpuTiers != null) sb.Append(",\"gpuTiers\":").Append(JsonSerializer.Serialize(c.GpuTiers));
            sb.Append(",\"source\":{\"url\":\"https://example.invalid/").Append(c.Id).Append(".zip\",\"sha256\":\"").Append(new string('0', 64)).Append("\",\"format\":\"zip\"}");
            sb.Append(",\"files\":[");
            var f1 = true;
            foreach (var f in c.Files)
            {
                var content = Encoding.UTF8.GetBytes($"fake {c.Id}/{f.Path}\n" + (f.Path.EndsWith(".conf") ? FakeDgVoodooConf : string.Empty));
                files[c.Id + "/" + f.Path] = content;
                if (!f1) sb.Append(',');
                f1 = false;
                sb.Append("{\"path\":").Append(JsonSerializer.Serialize(f.Path)).Append(",\"sha256\":\"").Append(Hashing.Sha256(content)).Append("\"}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        var catalog = ComponentCatalog.Parse(sb.ToString());
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in files)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(content);
            }
        }
        return (catalog, ms.ToArray());
    }

    public const string FakeDgVoodooConf = "Version = 0x287\r\n[General]\r\nOutputAPI                            = bestavailable\r\nAdapters                             = all\r\n\r\n[DirectX]\r\nDisableAndPassThru                  = false\r\nVideoCard                           = internal3D\r\nVRAM                                = 256\r\ndgVoodooWatermark                   = true\r\n\r\n[Debug]\r\nInfo                                = enable\r\n";

    public static ComponentStore FakeStore(string root)
    {
        var (catalog, zip) = FakeComponents();
        var store = new ComponentStore(catalog, Path.Combine(root, "cache"), Path.Combine(root, "local"))
        {
            AllowNetwork = false,
            PayloadOpener = () => new MemoryStream(zip, false),
        };
        return store;
    }
}
