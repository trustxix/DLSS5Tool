using System.Text;
using DLSS5Tool.Core;
using DLSS5Tool.Core.Games;
using DLSS5Tool.Core.Install;
using Xunit;

namespace DLSS5Tool.Tests;

public class IniFileTests
{
    [Fact]
    public void ParsesSetsAndPreservesComments()
    {
        var ini = IniFile.Parse("; comment\r\n[GENERAL]\r\nA=1\r\nB=2\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n");
        Assert.Equal("1", ini.Get("GENERAL", "A"));
        Assert.Null(ini.Get("GENERAL", "Z"));
        ini.Set("GENERAL", "A", "9");
        ini.Set("GENERAL", "C", "3");
        ini.Set("NEW", "X", "y");
        var text = ini.ToText();
        Assert.StartsWith("; comment", text);
        Assert.Contains("A=9\r\nB=2\r\nC=3\r\n", text);
        Assert.Contains("[NEW]\r\nX=y", text);
        Assert.Equal("36,0,0,0", ini.Get("INPUT", "KeyOverlay"));
    }

    [Fact]
    public void KeysBeforeFirstSectionLiveInEmptySection()
    {
        var ini = IniFile.Parse("Techniques=A@a.fx,B@b.fx\r\nTechniqueSorting=A@a.fx,B@b.fx\r\n\r\n[a.fx]\r\nK=1\r\n");
        Assert.Equal(new[] { "A@a.fx", "B@b.fx" }, ini.GetList(string.Empty, "Techniques"));
        ini.SetList(string.Empty, "Techniques", new[] { "B@b.fx", "A@a.fx" });
        Assert.StartsWith("Techniques=B@b.fx,A@a.fx\r\n", ini.ToText());
        Assert.Equal("1", ini.Get("a.fx", "K"));
        var empty = new IniFile();
        empty.Set(string.Empty, "Techniques", "X");
        empty.Set("S", "k", "v");
        Assert.Equal("Techniques=X\r\n\r\n[S]\r\nk=v\r\n", empty.ToText());
    }

    [Fact]
    public void SetIfMissingAndRemove()
    {
        var ini = IniFile.Parse("[S]\r\nA=1\r\n");
        ini.SetIfMissing("S", "A", "2");
        ini.SetIfMissing("S", "B", "2");
        Assert.Equal("1", ini.Get("S", "A"));
        Assert.Equal("2", ini.Get("S", "B"));
        Assert.True(ini.Remove("S", "A"));
        Assert.Null(ini.Get("S", "A"));
    }

    [Fact]
    public void StripsUtf8Bom()
    {
        var ini = IniFile.Parse("\uFEFFApps=C:\\a.exe\r\n");
        Assert.Equal(new[] { "C:\\a.exe" }, ini.GetList(string.Empty, "Apps"));
    }
}

public class VdfTests
{
    [Fact]
    public void ParsesLibraryFoldersAndManifest()
    {
        var root = VdfNode.Parse("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"730\"\t\t\"1\"\n\t\t}\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n");
        var folders = root["libraryfolders"]!;
        Assert.Equal(2, folders.Children.Count);
        Assert.Equal("C:\\Program Files (x86)\\Steam", folders.Children[0].Get("path"));
        Assert.Equal("D:\\SteamLibrary", folders.Children[1].Get("path"));

        var acf = VdfNode.Parse("\"AppState\"\n{\n\t\"appid\"\t\t\"620\"\n\t\"name\"\t\t\"Portal 2\"\n\t\"installdir\"\t\t\"Portal 2\"\n\t\"StateFlags\"\t\t\"4\"\n}\n");
        Assert.Equal("Portal 2", acf["AppState"]!.Get("installdir"));
        Assert.Equal("620", acf["AppState"]!.Get("appid"));
    }
}

public class PeFileTests
{
    [Fact]
    public void ReadsBitnessAndImportsOfSystemBinaries()
    {
        var pe64 = PeFile.TryRead(TestSupport.Exe64);
        Assert.NotNull(pe64);
        Assert.True(pe64!.Is64Bit);
        Assert.Contains("kernel32.dll", pe64.Imports);
        Assert.Equal(2, pe64.Subsystem);

        if (File.Exists(TestSupport.Exe32))
        {
            var pe32 = PeFile.TryRead(TestSupport.Exe32);
            Assert.NotNull(pe32);
            Assert.False(pe32!.Is64Bit);
        }
    }

    [Fact]
    public void ReadsExports()
    {
        var k32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        var pe = PeFile.TryRead(k32, readExports: true);
        Assert.NotNull(pe);
        Assert.Contains("CreateFileW", pe!.Exports);
    }

    [Fact]
    public void ApiStringScanCountsAsciiAndUtf16()
    {
        var dir = TestSupport.NewTempDir("pe");
        try
        {
            var p = Path.Combine(dir, "blob.bin");
            var bytes = Encoding.ASCII.GetBytes("xxx D3D11.dll yyy ").Concat(Encoding.Unicode.GetBytes("vulkan-1.dll")).Concat(Encoding.ASCII.GetBytes(" xd3d9.dll")).ToArray();
            File.WriteAllBytes(p, bytes);
            var hits = PeFile.ScanApiStrings(p);
            Assert.Equal(1, hits["d3d11.dll"]);
            Assert.Equal(1, hits["vulkan-1.dll"]);
            Assert.False(hits.ContainsKey("d3d9.dll"), "a name embedded in a longer identifier must not count");
        }
        finally { TestSupport.TryDelete(dir); }
    }

    [Fact]
    public void NonPeReturnsNull()
    {
        var dir = TestSupport.NewTempDir("pe2");
        try
        {
            var p = Path.Combine(dir, "x.exe");
            File.WriteAllText(p, "not a pe");
            Assert.Null(PeFile.TryRead(p));
        }
        finally { TestSupport.TryDelete(dir); }
    }
}

public class GpuInfoTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", GpuTier.Rtx50)]
    [InlineData("NVIDIA GeForce RTX 4090", GpuTier.Rtx40)]
    [InlineData("NVIDIA GeForce RTX 3080 Ti", GpuTier.Rtx30)]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER", GpuTier.Rtx20)]
    [InlineData("NVIDIA GeForce GTX 1080", GpuTier.Unsupported)]
    [InlineData("NVIDIA RTX A6000", GpuTier.Unsupported)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuTier.Unsupported)]
    [InlineData("Microsoft Remote Display Adapter", GpuTier.Unsupported)]
    [InlineData("NVIDIA GeForce RTX 5090 Laptop GPU", GpuTier.Rtx50)]
    public void ClassifiesAdapters(string name, GpuTier tier)
    {
        Assert.Equal(tier, GpuInfo.Classify(name));
    }

    [Fact]
    public void ConvertsRegistryDriverVersion()
    {
        Assert.Equal("616.92", GpuInfo.ToPublicDriverVersion("32.0.16.1692"));
        Assert.Equal("561.09", GpuInfo.ToPublicDriverVersion("32.0.15.6109"));
        Assert.Null(GpuInfo.ToPublicDriverVersion(null));
    }

    [Fact]
    public void BestPrefersRtxOverIgpu()
    {
        var best = GpuInfo.Best(new[] { new GpuInfo("AMD Radeon(TM) Graphics", GpuTier.Unsupported, "1.0"), new GpuInfo("NVIDIA GeForce RTX 4080", GpuTier.Rtx40, "616.92") });
        Assert.Equal(GpuTier.Rtx40, best.Tier);
    }
}

public class GraphicsApiTests
{
    [Fact]
    public void PrimaryFollowsReShadePreferences()
    {
        Assert.Equal(GraphicsApi.Vulkan, GraphicsApiInfo.PickPrimary(new[] { GraphicsApi.D3D12, GraphicsApi.Vulkan }));
        Assert.Equal(GraphicsApi.D3D11, GraphicsApiInfo.PickPrimary(new[] { GraphicsApi.D3D9, GraphicsApi.D3D11 }));
        Assert.Equal(GraphicsApi.D3D9, GraphicsApiInfo.PickPrimary(new[] { GraphicsApi.OpenGL, GraphicsApi.D3D9 }));
        Assert.Equal(GraphicsApi.Unknown, GraphicsApiInfo.PickPrimary(Array.Empty<GraphicsApi>()));
    }

    [Theory]
    [InlineData("d3d11", GraphicsApi.D3D11)]
    [InlineData("DX12", GraphicsApi.D3D12)]
    [InlineData("DirectX9", GraphicsApi.D3D9)]
    [InlineData("vulkan", GraphicsApi.Vulkan)]
    [InlineData("opengl", GraphicsApi.OpenGL)]
    public void ParsesApiNames(string text, GraphicsApi expected)
    {
        Assert.True(GraphicsApiInfo.TryParse(text, out var api));
        Assert.Equal(expected, api);
    }
}

public class DgVoodooConfTests
{
    [Fact]
    public void ConfSetReplacesAndAppends()
    {
        var t = TestSupport.FakeDgVoodooConf;
        t = Installer.ConfSet(t, "General", "OutputAPI", "d3d11_fl11_0");
        t = Installer.ConfSet(t, "DirectX", "VRAM", "1GB");
        t = Installer.ConfSet(t, "DirectX", "NewKey", "x");
        t = Installer.ConfSet(t, "Glide", "3DfxWatermark", "false");
        Assert.Contains("OutputAPI                            = d3d11_fl11_0", t);
        Assert.Contains("VRAM                                = 1GB", t);
        Assert.Contains("NewKey = x", t);
        Assert.Contains("[Glide]\r\n3DfxWatermark = false", t);
        Assert.DoesNotContain("= bestavailable", t);
        Assert.Contains("Adapters                             = all", t);
    }
}

public class GamingRootTests
{
    [Fact]
    public void ParsesGamingRootFile()
    {
        var dir = TestSupport.NewTempDir("gr");
        try
        {
            var p = Path.Combine(dir, ".GamingRoot");
            var name = "XboxGames";
            var bytes = new List<byte>(Encoding.ASCII.GetBytes("RGBX"));
            bytes.AddRange(BitConverter.GetBytes(1));
            bytes.AddRange(BitConverter.GetBytes(name.Length + 1));
            bytes.AddRange(Encoding.Unicode.GetBytes(name + "\0"));
            File.WriteAllBytes(p, bytes.ToArray());
            Assert.Equal(new[] { "XboxGames" }, GameScanner.ParseGamingRoot(p));
        }
        finally { TestSupport.TryDelete(dir); }
    }
}
