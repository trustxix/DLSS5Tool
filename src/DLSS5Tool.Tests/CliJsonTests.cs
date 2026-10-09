using static DLSS5Tool.Cli.Cli;
using Xunit;

namespace DLSS5Tool.Tests;

public class CliJsonTests
{
    [Fact]
    public void Out_does_not_swallow_the_next_flag()
    {
        // "--out --help" must be a usage error, not a file called "--help".
        Assert.Equal(3, Run(new[] { "--version", "--out", "--help" }));
        Assert.False(File.Exists("--help"));
    }

    [Fact]
    public void Json_errors_are_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dlss5tool-clijson-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "out.json");
        try
        {
            var missing = Path.Combine(dir, "nope.exe");
            Assert.Equal(3, Run(new[] { "--analyze", missing, "--json", "--out", file }));
            var text = File.ReadAllText(file);
            Assert.Contains("\"ok\": false", text);
            Assert.Contains("\"code\": 3", text);
            Assert.DoesNotContain("error: ", text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
