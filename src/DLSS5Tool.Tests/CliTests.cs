using static DLSS5Tool.Cli.Cli;
using Xunit;

namespace DLSS5Tool.Tests;

public class CliTests
{
    [Fact]
    public void Help_and_version_exit_zero()
    {
        Assert.Equal(0, Run(new[] { "--help" }));
        Assert.Equal(0, Run(new[] { "--version" }));
    }

    [Fact]
    public void Unknown_option_is_a_usage_error()
    {
        Assert.Equal(3, Run(new[] { "--scan", "--jsn" }));
        Assert.Equal(3, Run(new[] { "--bogus" }));
    }

    [Fact]
    public void Two_commands_are_a_usage_error()
    {
        Assert.Equal(3, Run(new[] { "--scan", "--components" }));
    }

    [Fact]
    public void Missing_value_or_target_is_a_usage_error()
    {
        Assert.Equal(3, Run(new[] { "--plan", "--api" }));
        Assert.Equal(3, Run(new[] { "--analyze" }));
        Assert.Equal(3, Run(new[] { "--analyze", Path.Combine(Path.GetTempPath(), "dlss5tool-does-not-exist.exe") }));
    }

    [Fact]
    public void Out_file_gets_its_directory_created()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dlss5tool-cli-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "nested", "out.txt");
        try
        {
            Assert.Equal(0, Run(new[] { "--version", "--out", file }));
            Assert.Contains("DLSS 5 Tool", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
