using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using Xunit;

namespace DLSS5Tool.Tests;

public class AnalyzerTests
{
    [Fact]
    public void Unity_player_is_treated_as_direct3d11_even_though_it_imports_opengl32()
    {
        // glu32.dll imports opengl32.dll, exactly like UnityPlayer.dll does for its optional GL Core backend.
        var exeSrc = Path.Combine(Environment.SystemDirectory, "charmap.exe");
        var dllSrc = Path.Combine(Environment.SystemDirectory, "glu32.dll");
        if (!File.Exists(exeSrc) || !File.Exists(dllSrc)) return; // fixture not available on this Windows build

        var root = Path.Combine(Path.GetTempPath(), "dlss5tool-unity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Game_Data"));
        try
        {
            var exe = Path.Combine(root, "Game.exe");
            File.Copy(exeSrc, exe);
            File.Copy(dllSrc, Path.Combine(root, "UnityPlayer.dll"));

            var profile = GameAnalyzer.Analyze(exe);

            Assert.Contains(GraphicsApi.OpenGL, profile.Apis);
            Assert.Contains(GraphicsApi.D3D11, profile.Apis);
            Assert.Equal(GraphicsApi.D3D11, profile.PrimaryApi);
            Assert.Contains(profile.Evidence, e => e.Contains("Unity", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
