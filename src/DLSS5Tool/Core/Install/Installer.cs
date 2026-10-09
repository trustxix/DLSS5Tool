using System.Text.RegularExpressions;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Planning;

namespace DLSS5Tool.Core.Install;

/// <summary>
/// Executes an InstallPlan with a journal: every file written is recorded, every file replaced is backed up first,
/// so UninstallAsync puts the game folder back exactly as it was.
/// </summary>
public sealed class Installer
{
    private readonly ComponentStore _store;

    public Installer(ComponentStore store)
    {
        _store = store;
    }

    public async Task<InstallJournal> InstallAsync(InstallPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!plan.IsSupported) throw new InvalidOperationException(plan.UnsupportedReason ?? "This game is not supported.");
        var p = plan.Profile;
        var g = p.GameDir;

        if (p.Journal != null)
        {
            Report(progress, "A previous DLSS 5 Tool install is present; removing it first so the new one starts clean.");
            await UninstallAsync(g, p.Journal, progress, ct).ConfigureAwait(false);
        }

        Report(progress, "Fetching components...");
        var files = new Dictionary<(string, string), string>();
        var componentProgress = new Progress<ComponentProgress>(cp => Report(progress, cp.ToString()));
        foreach (var item in plan.Items.Where(i => i.ComponentId != null))
        {
            ct.ThrowIfCancellationRequested();
            var key = (item.ComponentId!, item.ComponentPath!);
            if (files.ContainsKey(key)) continue;
            files[key] = await _store.GetFileAsync(item.ComponentId!, item.ComponentPath!, componentProgress, ct).ConfigureAwait(false);
        }
        var needsLayer = plan.Items.Any(i => i.Kind == PlanItemKind.VulkanLayer);
        Dictionary<string, string>? reshadeFiles = null;
        if (needsLayer)
        {
            reshadeFiles = await _store.GetAllAsync("reshade", componentProgress, ct).ConfigureAwait(false);
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRel = Path.Combine(AppPaths.BackupDirName, stamp);
        var backupDir = Path.Combine(g, backupRel);
        var journal = new InstallJournal
        {
            ToolVersion = AppPaths.Version,
            ExePath = p.ExePath,
            Route = plan.Route.ToString(),
            Api = p.EffectiveApi.ToString(),
            Is64Bit = p.Is64Bit,
            InstalledUtc = DateTime.UtcNow,
            BackupDir = backupRel,
            Gpu = plan.Gpu.ToString(),
        };
        foreach (var id in plan.RequiredComponents) journal.Components[id] = _store.Catalog.Get(id).Version;
        if (needsLayer) journal.Components["reshade"] = _store.Catalog.Get("reshade").Version;

        var ctx = new Context(g, backupDir, journal);

        try
        {
            foreach (var item in plan.Items)
            {
                ct.ThrowIfCancellationRequested();
                Report(progress, item.Description);
                switch (item.Kind)
                {
                    case PlanItemKind.ComponentFile:
                        ctx.WriteFile(item.Target, item.OnlyIfMissing, dest => File.Copy(files[(item.ComponentId!, item.ComponentPath!)], dest, true));
                        break;
                    case PlanItemKind.TextFile:
                        ctx.WriteFile(item.Target, item.OnlyIfMissing, dest => File.WriteAllText(dest, item.Text ?? string.Empty, new System.Text.UTF8Encoding(false)));
                        break;
                    case PlanItemKind.ReShadeIni:
                        ApplyReShadeIni(ctx, item);
                        break;
                    case PlanItemKind.ReShadePreset:
                        ApplyPreset(ctx, item, plan);
                        break;
                    case PlanItemKind.DgVoodooConf:
                        ApplyDgVoodooConf(ctx, item, files[(item.ComponentId!, item.ComponentPath!)], plan);
                        break;
                    case PlanItemKind.DisableFile:
                        ctx.DisableFile(item.Target);
                        break;
                    case PlanItemKind.VulkanLayer:
                        InstallVulkanLayer(plan, reshadeFiles!, progress);
                        journal.VulkanAppsEntry = p.ExePath;
                        break;
                }
            }
            journal.Notes.AddRange(plan.Notes);
            journal.Save(g);
            var backedUp = journal.Entries.Count(e => e.Backup != null);
            Report(progress, backedUp > 0
                ? $"Installed. {journal.Entries.Count} file(s) written; {backedUp} original(s) kept in {backupRel}."
                : $"Installed. {journal.Entries.Count} file(s) written; nothing existing was touched.");
            return journal;
        }
        catch
        {
            Report(progress, "Install failed; rolling back what was written...");
            try
            {
                journal.Save(g);
                await UninstallAsync(g, journal, progress, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rb)
            {
                Log.Error("Rollback failed", rb);
            }
            throw;
        }
    }

    public Task UninstallAsync(string gameDir, InstallJournal journal, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        gameDir = Path.GetFullPath(gameDir);
        var gameRoot = gameDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var backupDir = Path.GetFullPath(Path.Combine(gameDir, string.IsNullOrWhiteSpace(journal.BackupDir) ? AppPaths.BackupDirName : journal.BackupDir));
        var backupRoot = backupDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var problems = new List<string>();
        // Backups are only ever read from the game's own backup folder, whatever a damaged journal says.
        var backupsUsable = backupRoot.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase);
        if (!backupsUsable) problems.Add($"backup folder '{journal.BackupDir}' is outside the game folder; originals were not restored");
        foreach (var e in Enumerable.Reverse(journal.Entries))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(e.Path)) { problems.Add("journal entry without a path; skipped"); continue; }
            try
            {
                var full = Path.GetFullPath(Path.Combine(gameDir, e.Path));
                var insideGame = full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase);
                switch (e.Action)
                {
                    case "created":
                        // Only ever delete inside the game folder.
                        if (!insideGame) { problems.Add($"{e.Path} is outside the game folder; not deleted"); break; }
                        if (File.Exists(full)) { ClearReadOnly(full); File.Delete(full); }
                        break;
                    case "replaced":
                    case "merged":
                        var backup = backupsUsable && !string.IsNullOrWhiteSpace(e.Backup) ? Path.GetFullPath(Path.Combine(backupDir, e.Backup)) : null;
                        if (backup != null && !backup.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)) { problems.Add($"{e.Path}: backup path escapes the backup folder; left as is"); break; }
                        if (backup != null && File.Exists(backup))
                        {
                            // Outside the game folder (a ReShade preset path can point there) we only ever overwrite a file that still exists.
                            if (!insideGame && !File.Exists(full)) { problems.Add($"{e.Path} is outside the game folder and no longer exists; not recreated"); break; }
                            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                            if (File.Exists(full)) ClearReadOnly(full);
                            File.Copy(backup, full, true);
                            ClearReadOnly(backup);
                            File.Delete(backup);
                        }
                        else
                        {
                            problems.Add($"no backup for {e.Path}; left as is");
                        }
                        break;
                    case "disabled":
                        var disabled = e.Note != null ? Path.GetFullPath(Path.Combine(gameDir, e.Note)) : full + Context.DisabledSuffix;
                        if (!insideGame || !disabled.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase)) { problems.Add($"{e.Path} is outside the game folder; not restored"); break; }
                        if (File.Exists(disabled) && !File.Exists(full)) File.Move(disabled, full);
                        else if (File.Exists(disabled)) problems.Add($"{e.Path} was recreated by something else; the original is still at {Path.GetFileName(disabled)}");
                        break;
                    default:
                        problems.Add($"{e.Path}: unknown journal action '{e.Action}'; skipped");
                        break;
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{e.Path}: {ex.Message}");
            }
        }

        var createdReShadeIni = journal.Entries.Any(e => e.Action == "created" && e.Path.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));
        var generated = new List<string> { "dlss5-feed.cfg", "dlss5-feed.log", Path.Combine("host64", "dlss5-feed-host.log"), Path.Combine("host64", "ReShade.log"), Path.Combine("host64", "ReShadePreset.ini"), Path.Combine("host64", "dlss5-feed.cfg") };
        if (createdReShadeIni) generated.AddRange(new[] { "ReShade.log", "ReShadePreset.ini" });
        foreach (var rel in generated)
        {
            var full = Path.Combine(gameDir, rel);
            try { if (File.Exists(full)) File.Delete(full); } catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        foreach (var rel in new[] { Path.Combine("reshade-shaders", "Shaders", "include"), Path.Combine("reshade-shaders", "Shaders"), Path.Combine("reshade-shaders", "Textures"), "reshade-shaders", "host64", "layer" })
        {
            DeleteIfEmpty(Path.Combine(gameDir, rel));
        }

        if (journal.VulkanAppsEntry != null)
        {
            Report(progress, "Removing the game from ReShade's Vulkan app list (administrator rights)...");
            var code = VulkanLayer.Run(new VulkanLayerSpec { Action = "remove", ExePath = journal.VulkanAppsEntry, Is64Bit = journal.Is64Bit }, progress);
            if (code != 0) problems.Add("the game could not be removed from ReShadeApps.ini (exit code " + code + "); ReShade's Vulkan layer will still load for it");
        }

        try
        {
            var jp = InstallJournal.PathFor(gameDir);
            if (File.Exists(jp)) File.Delete(jp);
        }
        catch (Exception ex) { problems.Add("journal: " + ex.Message); }
        DeleteIfEmpty(backupDir);
        DeleteIfEmpty(Path.Combine(gameDir, AppPaths.BackupDirName));

        foreach (var pr in problems) Log.Warn("Uninstall: " + pr);
        Report(progress, problems.Count == 0 ? "Uninstalled; the game folder is back to its previous state." : $"Uninstalled with {problems.Count} note(s): {string.Join("; ", problems)}");
        return Task.CompletedTask;
    }

    /// <summary>Games and launchers sometimes mark files read-only; a replace or delete must not trip over that.</summary>
    internal static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { /* the following operation reports the real error */ }
    }

    private static void DeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* leave it */ }
    }

    private static void Report(IProgress<string>? progress, string message)
    {
        Log.Info(message);
        progress?.Report(message);
    }

    // ------------------------------------------------------------------ configuration edits

    private static void ApplyReShadeIni(Context ctx, PlanItem item)
    {
        var path = item.Target;
        var depthClears = item.Settings.GetValueOrDefault("DepthCopyBeforeClears", "0");
        var exists = File.Exists(path);
        var ini = exists ? IniFile.Load(path) : IniFile.Parse(Planner.ReShadeIniTemplate.Replace("DepthCopyBeforeClears=0", "DepthCopyBeforeClears=" + depthClears));
        var before = ini.ToText();
        ini.Set("ADDON", "AddonPath", ".\\");
        ini.Set("GENERAL", "EffectSearchPaths", ".\\reshade-shaders\\Shaders\\**");
        ini.Set("GENERAL", "TextureSearchPaths", ".\\reshade-shaders\\Textures\\**");
        ini.SetIfMissing("GENERAL", "PresetPath", ".\\ReShadePreset.ini");
        if (depthClears == "1") ini.Set("DEPTH", "DepthCopyBeforeClears", "1");
        var after = ini.ToText();
        if (exists && before.TrimEnd() == after.TrimEnd())
        {
            Log.Info("ReShade.ini already has the required keys; kept.");
            return;
        }
        ctx.WriteFile(path, false, dest => File.WriteAllText(dest, after, new System.Text.UTF8Encoding(false)), exists ? "merged" : "created");
    }

    private static void ApplyPreset(Context ctx, PlanItem item, InstallPlan plan)
    {
        var provider = plan.MvProvider == 4 ? "Lumenite_QuantMotion@lumenite_QuantMotion.fx" : "Lumenite_Kernel@lumenite_Kernel.fx";
        const string feed = "DLSS5_Feed@DLSS5_Feed.fx";
        const string debug = "DLSS5_Feed_Debug@DLSS5_Feed.fx";
        var def = "DLSS5_MV_PROVIDER=" + plan.MvProvider;

        var path = item.Target;
        var iniPath = Path.Combine(plan.Profile.GameDir, "ReShade.ini");
        if (File.Exists(iniPath))
        {
            var presetPath = IniFile.Load(iniPath).Get("GENERAL", "PresetPath");
            if (!string.IsNullOrWhiteSpace(presetPath))
            {
                path = Path.IsPathRooted(presetPath) ? presetPath : Path.GetFullPath(Path.Combine(plan.Profile.GameDir, presetPath));
            }
        }

        var exists = File.Exists(path);
        if (!exists)
        {
            var text = "Techniques=" + provider + "," + feed + "\r\n" +
                       "TechniqueSorting=" + provider + "," + feed + "," + debug + "\r\n\r\n" +
                       "[DLSS5_Feed.fx]\r\nDEBUG_VIEW=0\r\nMV_SCALE=1.000000\r\nMV_SIGN=1.000000,1.000000\r\n" +
                       "PreprocessorDefinitions=" + def + "\r\n";
            ctx.WriteFile(path, false, dest => File.WriteAllText(dest, text, new System.Text.UTF8Encoding(false)));
            return;
        }

        var ini = IniFile.Load(path);
        var before = ini.ToText();
        foreach (var key in new[] { "Techniques", "TechniqueSorting" })
        {
            var list = ini.GetList(string.Empty, key)
                .Where(t => !t.Equals(provider, StringComparison.OrdinalIgnoreCase) && !t.Equals(feed, StringComparison.OrdinalIgnoreCase) && !t.StartsWith("DLSS5_Feed_Debug@", StringComparison.OrdinalIgnoreCase))
                .ToList();
            list.Add(provider);
            list.Add(feed);
            if (key == "TechniqueSorting") list.Add(debug);
            ini.SetList(string.Empty, key, list);
        }
        var defs = ini.GetList("DLSS5_Feed.fx", "PreprocessorDefinitions").Where(d => !Regex.IsMatch(d, @"^DLSS5_MV_PROVIDER\s*=", RegexOptions.IgnoreCase)).ToList();
        defs.Add(def);
        ini.SetList("DLSS5_Feed.fx", "PreprocessorDefinitions", defs);
        var after = ini.ToText();
        if (before.TrimEnd() == after.TrimEnd())
        {
            Log.Info("ReShadePreset.ini already correct; kept.");
            return;
        }
        ctx.WriteFile(path, false, dest => File.WriteAllText(dest, after, new System.Text.UTF8Encoding(false)), "merged");
    }

    private static void ApplyDgVoodooConf(Context ctx, PlanItem item, string sourceConf, InstallPlan plan)
    {
        var path = item.Target;
        var exists = File.Exists(path);
        var text = exists ? File.ReadAllText(path) : File.ReadAllText(sourceConf);
        var before = text;
        text = ConfSet(text, "General", "OutputAPI", "d3d11_fl11_0");
        text = ConfSet(text, "DirectX", "DisableAndPassThru", "false");
        text = ConfSet(text, "DirectX", "VideoCard", "internal3D");
        text = ConfSet(text, "DirectX", "VRAM", "1GB");
        text = ConfSet(text, "DirectX", "dgVoodooWatermark", item.Settings.GetValueOrDefault("dgVoodooWatermark", "false"));
        if (plan.Profile.EffectiveApi == GraphicsApi.Glide)
        {
            text = ConfSet(text, "Glide", "3DfxWatermark", item.Settings.GetValueOrDefault("dgVoodooWatermark", "false"));
            text = ConfSet(text, "Glide", "3DfxSplashScreen", "false");
        }
        if (exists && before == text)
        {
            Log.Info("dgVoodoo.conf already has the required values; kept.");
            return;
        }
        ctx.WriteFile(path, false, dest => File.WriteAllText(dest, text), exists ? "merged" : "created");
    }

    /// <summary>Sets Key = Value inside [Section] of a dgVoodoo.conf, preserving the file's column alignment.</summary>
    internal static string ConfSet(string text, string section, string key, string value)
    {
        var secMatch = Regex.Match(text, @"(?im)^\s*\[" + Regex.Escape(section) + @"\]\s*$");
        if (!secMatch.Success)
        {
            return text.TrimEnd() + "\r\n\r\n[" + section + "]\r\n" + key + " = " + value + "\r\n";
        }
        var start = secMatch.Index + secMatch.Length;
        var next = Regex.Match(text[start..], @"(?im)^\s*\[[^\]]+\]\s*$");
        var end = next.Success ? start + next.Index : text.Length;
        var body = text[start..end];
        var keyMatch = Regex.Match(body, @"(?im)^(\s*" + Regex.Escape(key) + @"\s*=\s*)(.*?)\s*$");
        string newBody;
        if (keyMatch.Success)
        {
            newBody = body[..keyMatch.Index] + keyMatch.Groups[1].Value + value + body[(keyMatch.Index + keyMatch.Length)..];
        }
        else
        {
            newBody = body.TrimEnd() + "\r\n" + key + " = " + value + "\r\n\r\n";
        }
        return text[..start] + newBody + text[end..];
    }

    private static void InstallVulkanLayer(InstallPlan plan, Dictionary<string, string> reshadeFiles, IProgress<string>? progress)
    {
        var spec = new VulkanLayerSpec
        {
            Action = "install",
            ExePath = plan.Profile.ExePath,
            Is64Bit = plan.Profile.Is64Bit,
            Dll32 = reshadeFiles["ReShade32.dll"],
            Dll64 = reshadeFiles["ReShade64.dll"],
            Json32 = reshadeFiles["ReShade32.json"],
            Json64 = reshadeFiles["ReShade64.json"],
        };
        var code = VulkanLayer.Run(spec, progress);
        if (code == 1223) throw new InvalidOperationException("The administrator prompt was declined; ReShade's Vulkan layer could not be registered for this game.");
        if (code != 0) throw new InvalidOperationException($"Registering ReShade's Vulkan layer failed (exit code {code}). See {AppPaths.Stage} for details.");
    }

    // ------------------------------------------------------------------ journaled file writes

    private sealed class Context
    {
        public const string DisabledSuffix = ".disabled-by-dlss5tool";
        private readonly string _gameDir;
        private readonly string _backupDir;
        private readonly InstallJournal _journal;

        public Context(string gameDir, string backupDir, InstallJournal journal)
        {
            _gameDir = gameDir;
            _backupDir = backupDir;
            _journal = journal;
        }

        private string Rel(string full) => Path.GetRelativePath(_gameDir, Path.GetFullPath(full));

        public void WriteFile(string target, bool onlyIfMissing, Action<string> write, string? action = null)
        {
            var existed = File.Exists(target);
            if (existed && onlyIfMissing)
            {
                Log.Info($"{Rel(target)} already present; kept.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string? backupRel = null;
            string? backupPath = null;
            if (existed)
            {
                backupRel = Rel(target);
                backupPath = Path.Combine(_backupDir, backupRel);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(target, backupPath, true);
            }
            var tmp = target + ".dlss5tool-tmp";
            FileAttributes? originalAttributes = null;
            if (existed) { try { originalAttributes = File.GetAttributes(target); } catch { /* reported by the move if it matters */ } }
            try
            {
                write(tmp);
                if (existed) ClearReadOnly(target);
                File.Move(tmp, target, true);
            }
            catch
            {
                // The original is untouched: drop the half-written temp file, put its attributes back and remove the now pointless backup copy.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                try { if (originalAttributes is { } a && File.Exists(target)) File.SetAttributes(target, a); } catch { /* best effort */ }
                try { if (backupPath != null && File.Exists(backupPath)) { ClearReadOnly(backupPath); File.Delete(backupPath); } } catch { /* best effort */ }
                throw;
            }
            _journal.Entries.Add(new JournalEntry
            {
                Path = Rel(target),
                Action = action ?? (existed ? "replaced" : "created"),
                Backup = backupRel,
                Sha256 = Hashing.Sha256File(target),
            });
        }

        public void DisableFile(string target)
        {
            if (!File.Exists(target)) return;
            var disabled = target + DisabledSuffix;
            if (File.Exists(disabled)) File.Delete(disabled);
            File.Move(target, disabled);
            _journal.Entries.Add(new JournalEntry { Path = Rel(target), Action = "disabled", Note = Rel(disabled) });
        }
    }
}
