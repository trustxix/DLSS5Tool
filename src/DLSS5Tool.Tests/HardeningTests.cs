using DLSS5Tool.Core;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Install;
using Xunit;

namespace DLSS5Tool.Tests;

public class HardeningTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dlss5tool-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Uninstall_never_deletes_outside_the_game_folder()
    {
        var root = TempDir();
        try
        {
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(game);
            var outside = Path.Combine(root, "outside.txt");
            File.WriteAllText(outside, "keep me");
            var inside = Path.Combine(game, "ours.txt");
            File.WriteAllText(inside, "ours");
            var journal = new InstallJournal { BackupDir = Path.Combine(AppPaths.BackupDirName, "x") };
            journal.Entries.Add(new JournalEntry { Path = Path.Combine("..", "outside.txt"), Action = "created" });
            journal.Entries.Add(new JournalEntry { Path = "ours.txt", Action = "created" });
            journal.Save(game);

            await new Installer(ComponentStore.Default()).UninstallAsync(game, journal);

            Assert.True(File.Exists(outside));
            Assert.False(File.Exists(inside));
            Assert.False(File.Exists(InstallJournal.PathFor(game)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Uninstall_restores_over_a_read_only_file()
    {
        var root = TempDir();
        try
        {
            var game = Path.Combine(root, "game");
            var backupRel = Path.Combine(AppPaths.BackupDirName, "stamp");
            Directory.CreateDirectory(Path.Combine(game, backupRel));
            var target = Path.Combine(game, "dxgi.dll");
            File.WriteAllText(target, "ours");
            File.SetAttributes(target, FileAttributes.ReadOnly);
            var backup = Path.Combine(game, backupRel, "dxgi.dll");
            File.WriteAllText(backup, "original");
            File.SetAttributes(backup, FileAttributes.ReadOnly);
            var journal = new InstallJournal { BackupDir = backupRel };
            journal.Entries.Add(new JournalEntry { Path = "dxgi.dll", Action = "replaced", Backup = "dxgi.dll" });
            journal.Save(game);

            await new Installer(ComponentStore.Default()).UninstallAsync(game, journal);

            Assert.Equal("original", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(game, AppPaths.BackupDirName)));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PeFile_returns_null_for_damaged_executables()
    {
        var root = TempDir();
        try
        {
            var junk = Path.Combine(root, "junk.exe");
            // MZ header pointing the PE header far beyond the end of the file, then a few random bytes.
            var bytes = new byte[128];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, 0x3C);
            File.WriteAllBytes(junk, bytes);
            Assert.Null(PeFile.TryRead(junk));

            var truncated = Path.Combine(root, "short.exe");
            File.WriteAllBytes(truncated, new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
            Assert.Null(PeFile.TryRead(truncated));

            var empty = Path.Combine(root, "empty.exe");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            Assert.Null(PeFile.TryRead(empty));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
