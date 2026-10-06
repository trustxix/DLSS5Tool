namespace DLSS5Tool.Core.Games;

public sealed class GameInfo
{
    public string Name { get; set; } = string.Empty;
    public string Launcher { get; set; } = string.Empty;
    public string InstallDir { get; set; } = string.Empty;
    /// <summary>The executable the launcher declares (Epic, GOG, Xbox) or the best heuristic pick.</summary>
    public string? ExePath { get; set; }
    public string? AppId { get; set; }
    /// <summary>Other plausible executables, best first.</summary>
    public List<ExeCandidate> Candidates { get; set; } = new();
    public bool Installed { get; set; }

    public override string ToString() => $"{Name} [{Launcher}] {ExePath ?? InstallDir}";
}

public sealed record ExeCandidate(string Path, int Score, string Reason, bool Is64Bit, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
