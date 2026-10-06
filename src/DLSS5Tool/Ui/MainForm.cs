using System.Diagnostics;
using DLSS5Tool.Core;
using DLSS5Tool.Core.Analysis;
using DLSS5Tool.Core.Components;
using DLSS5Tool.Core.Games;
using DLSS5Tool.Core.Install;
using DLSS5Tool.Core.Planning;
using DLSS5Tool.Core.Update;

namespace DLSS5Tool.Ui;

/// <summary>One window: games on the left, what will happen on the right, one Install button.</summary>
public sealed class MainForm : Form
{
    private readonly ListView _games = new();
    private readonly TextBox _search = new();
    private readonly Button _rescan = new();
    private readonly Button _addGame = new();
    private readonly ComboBox _exe = new();
    private readonly Button _browse = new();
    private readonly ComboBox _api = new();
    private readonly Label _summary = new();
    private readonly TextBox _plan = new();
    private readonly Button _install = new();
    private readonly Button _uninstall = new();
    private readonly Button _verify = new();
    private readonly Button _openFolder = new();
    private readonly Button _selfTest = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _log = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripStatusLabel _updateLink = new();

    private readonly ComponentStore _store = ComponentStore.Default();
    private List<GameInfo> _all = new();
    private GameInfo? _current;
    private GameProfile? _profile;
    private InstallPlan? _planObj;
    private GpuInfo _gpu = new("detecting...", GpuTier.Unsupported, null);
    private UpdateInfo? _update;
    private bool _busy;
    private bool _suppressEvents;
    private readonly Action<LogLevel, string> _logSink;

    public MainForm()
    {
        Text = $"DLSS 5 Tool {AppPaths.Version}";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BuildLayout();
        _logSink = (level, msg) =>
        {
            if (level == LogLevel.Debug || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => AppendLog(msg)); } catch { /* closing */ }
        };
        Log.AddSink(_logSink);
        Load += async (_, _) => await OnLoadAsync();
        FormClosed += (_, _) => Log.RemoveSink(_logSink);
    }

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380, FixedPanel = FixedPanel.Panel1 };
        Controls.Add(split);

        // Left: games
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _search.PlaceholderText = "Search games...";
        _search.Dock = DockStyle.Fill;
        _search.TextChanged += (_, _) => FillGames();
        left.Controls.Add(_search, 0, 0);
        _games.View = View.Details;
        _games.FullRowSelect = true;
        _games.HideSelection = false;
        _games.MultiSelect = false;
        _games.Dock = DockStyle.Fill;
        _games.Columns.Add("Game", 200);
        _games.Columns.Add("Launcher", 90);
        _games.Columns.Add("DLSS 5", 60);
        _games.SelectedIndexChanged += async (_, _) => await OnGameSelectedAsync();
        left.Controls.Add(_games, 0, 1);
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _rescan.Text = "Rescan";
        _rescan.AutoSize = true;
        _rescan.Click += async (_, _) => await ScanAsync();
        _addGame.Text = "Add game (.exe)...";
        _addGame.AutoSize = true;
        _addGame.Click += (_, _) => AddGame();
        leftButtons.Controls.Add(_rescan);
        leftButtons.Controls.Add(_addGame);
        left.Controls.Add(leftButtons, 0, 2);
        split.Panel1.Controls.Add(left);

        // Right: details
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Padding = new Padding(8) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < 4; i++) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        right.Controls.Add(new Label { Text = "Executable", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        _exe.DropDownStyle = ComboBoxStyle.DropDownList;
        _exe.Dock = DockStyle.Fill;
        _exe.SelectedIndexChanged += async (_, _) => { if (!_suppressEvents) await AnalyzeAsync(); };
        right.Controls.Add(_exe, 1, 0);
        _browse.Text = "Browse...";
        _browse.AutoSize = true;
        _browse.Click += (_, _) => BrowseExe();
        right.Controls.Add(_browse, 2, 0);

        right.Controls.Add(new Label { Text = "Graphics API", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        _api.DropDownStyle = ComboBoxStyle.DropDownList;
        _api.Width = 260;
        _api.Items.Add("Auto-detect");
        foreach (var a in new[] { GraphicsApi.D3D12, GraphicsApi.D3D11, GraphicsApi.D3D10, GraphicsApi.D3D9, GraphicsApi.D3D8, GraphicsApi.DDraw, GraphicsApi.OpenGL, GraphicsApi.Vulkan, GraphicsApi.Glide }) _api.Items.Add(new ApiItem(a));
        _api.SelectedIndex = 0;
        _api.SelectedIndexChanged += (_, _) => { if (!_suppressEvents) RebuildPlan(); };
        right.Controls.Add(_api, 1, 1);

        _summary.AutoSize = true;
        _summary.Dock = DockStyle.Fill;
        _summary.Margin = new Padding(0, 8, 0, 8);
        right.Controls.Add(_summary, 0, 2);
        right.SetColumnSpan(_summary, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _install.Text = "Install DLSS 5";
        _install.AutoSize = true;
        _install.Font = new Font(Font, FontStyle.Bold);
        _install.Click += async (_, _) => await InstallAsync();
        _uninstall.Text = "Uninstall";
        _uninstall.AutoSize = true;
        _uninstall.Click += async (_, _) => await UninstallAsync();
        _verify.Text = "Verify";
        _verify.AutoSize = true;
        _verify.Click += async (_, _) => await VerifyAsync();
        _openFolder.Text = "Open game folder";
        _openFolder.AutoSize = true;
        _openFolder.Click += (_, _) => { if (_profile != null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_profile.GameDir}\"") { UseShellExecute = true }); };
        _selfTest.Text = "Self-test this PC";
        _selfTest.AutoSize = true;
        _selfTest.Click += async (_, _) => await SelfTestAsync();
        actions.Controls.AddRange(new Control[] { _install, _uninstall, _verify, _openFolder, _selfTest });
        right.Controls.Add(actions, 0, 3);
        right.SetColumnSpan(actions, 3);

        _plan.Multiline = true;
        _plan.ReadOnly = true;
        _plan.ScrollBars = ScrollBars.Vertical;
        _plan.Dock = DockStyle.Fill;
        _plan.Font = new Font("Consolas", 9f);
        _plan.WordWrap = true;
        right.Controls.Add(_plan, 0, 4);
        right.SetColumnSpan(_plan, 3);

        _progress.Dock = DockStyle.Fill;
        _progress.Height = 14;
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        right.Controls.Add(_progress, 0, 5);
        right.SetColumnSpan(_progress, 3);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font("Consolas", 8.5f);
        right.Controls.Add(_log, 0, 6);
        right.SetColumnSpan(_log, 3);
        split.Panel2.Controls.Add(right);

        _status.Text = "Ready";
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _updateLink.IsLink = true;
        _updateLink.Visible = false;
        _updateLink.Click += async (_, _) => await ApplyUpdateAsync();
        _statusStrip.Items.Add(_status);
        _statusStrip.Items.Add(_updateLink);
        Controls.Add(_statusStrip);

        SetDetailsEnabled(false);
    }

    private sealed record ApiItem(GraphicsApi Api)
    {
        public override string ToString() => Api.Display();
    }

    // ------------------------------------------------------------------ lifecycle

    private async Task OnLoadAsync()
    {
        _gpu = await Task.Run(GpuInfo.DetectBest);
        AppendLog("GPU: " + _gpu);
        if (!_gpu.IsRtx) AppendLog("No NVIDIA RTX GPU detected. DLSS 5 needs one; installs are blocked on this PC.");
        await ScanAsync();
        _ = CheckUpdateAsync();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, "Scanning launchers...");
        try
        {
            _all = await Task.Run(() =>
            {
                var games = GameScanner.ScanAll(s => Log.Info(s));
                foreach (var g in games)
                {
                    GameScanner.ResolveExecutable(g);
                    g.Installed = g.ExePath != null && InstallJournal.TryLoad(Path.GetDirectoryName(g.ExePath)!) != null;
                }
                return games;
            });
            FillGames();
            _status.Text = $"{_all.Count} game(s) found. Select one.";
        }
        catch (Exception ex)
        {
            AppendLog("Scan failed: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FillGames()
    {
        var filter = _search.Text.Trim();
        _games.BeginUpdate();
        _games.Items.Clear();
        foreach (var g in _all)
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !g.Launcher.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem(new[] { g.Name, g.Launcher, g.Installed ? "yes" : string.Empty }) { Tag = g };
            if (g.ExePath == null) item.ForeColor = SystemColors.GrayText;
            _games.Items.Add(item);
        }
        _games.EndUpdate();
        _games.Columns[0].Width = -2;
    }

    private async Task OnGameSelectedAsync()
    {
        if (_games.SelectedItems.Count == 0) return;
        _current = (GameInfo)_games.SelectedItems[0].Tag!;
        _suppressEvents = true;
        _exe.Items.Clear();
        foreach (var c in _current.Candidates.Take(12)) _exe.Items.Add(new ExeItem(c.Path, _current.InstallDir));
        if (_current.ExePath != null && !_current.Candidates.Any(c => c.Path.Equals(_current.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            _exe.Items.Insert(0, new ExeItem(_current.ExePath, _current.InstallDir));
        }
        if (_exe.Items.Count > 0)
        {
            var idx = 0;
            for (var i = 0; i < _exe.Items.Count; i++)
            {
                if (((ExeItem)_exe.Items[i]!).Path.Equals(_current.ExePath ?? string.Empty, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            _exe.SelectedIndex = idx;
        }
        _api.SelectedIndex = 0;
        _suppressEvents = false;
        await AnalyzeAsync();
    }

    private sealed record ExeItem(string Path, string Root)
    {
        public override string ToString() => Path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? Path[Root.Length..].TrimStart('\\') : Path;
    }

    private async Task AnalyzeAsync()
    {
        if (_exe.SelectedItem is not ExeItem item) { SetDetailsEnabled(false); return; }
        if (_busy) return;
        SetBusy(true, "Analyzing " + Path.GetFileName(item.Path) + "...");
        try
        {
            _profile = await Task.Run(() => GameAnalyzer.Analyze(item.Path));
            if (_current != null) _current.ExePath = item.Path;
            RebuildPlan();
        }
        catch (Exception ex)
        {
            _profile = null;
            _planObj = null;
            _plan.Text = "Could not analyze this executable: " + ex.Message;
            SetDetailsEnabled(false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildPlan()
    {
        if (_profile == null) return;
        var api = _api.SelectedItem is ApiItem a ? a.Api : (GraphicsApi?)null;
        _planObj = Planner.Build(_profile, _gpu, _store.Catalog, new PlanOptions { ApiOverride = api });
        RenderPlan();
    }

    private void RenderPlan()
    {
        if (_profile == null || _planObj == null) return;
        var p = _profile;
        var plan = _planObj;
        _summary.Text = $"{p.Architecture}  |  {p.ApiSummary}  |  native DLSS: {(p.HasNativeDlss ? "yes" : "no")}  |  GPU: {_gpu.Name}" +
                        (p.ReShade != null ? $"  |  ReShade {p.ReShade.Version} present" : string.Empty) +
                        (p.IsInstalled ? $"  |  DLSS 5 installed {p.Journal!.InstalledUtc:yyyy-MM-dd}" : string.Empty);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Route: " + plan.RouteDisplay);
        if (!plan.IsSupported)
        {
            sb.AppendLine();
            sb.AppendLine(plan.UnsupportedReason);
        }
        else
        {
            if (plan.ReShadeMode == ReShadeMode.VulkanLayer) sb.AppendLine("ReShade: machine-wide Vulkan layer (needs one administrator prompt)");
            else sb.AppendLine("ReShade: " + plan.ReShadeDllName + " next to the executable");
            if (plan.DgVoodooWrapper != null) sb.AppendLine("dgVoodoo2: " + plan.DgVoodooWrapper + " translating to Direct3D 11");
            if (plan.HostDir != null) sb.AppendLine("64-bit helper: host64\\");
            sb.AppendLine("Neural model: " + _store.Catalog.Get(plan.NrComponentId).Version);
            sb.AppendLine();
            sb.AppendLine("Files to write:");
            foreach (var i in plan.Items) sb.AppendLine("  " + Shorten(i.Target, p.GameDir) + "   " + i.Description);
            var downloads = plan.RequiredComponents.Where(c => !_store.IsAvailableOffline(c)).Select(c => _store.Catalog.Get(c).Name + " " + _store.Catalog.Get(c).Version).ToList();
            sb.AppendLine();
            sb.AppendLine(downloads.Count == 0 ? "All components are available offline." : "Downloaded on install (verified by SHA-256): " + string.Join("; ", downloads));
        }
        foreach (var w in plan.Warnings) sb.AppendLine("WARNING: " + w);
        foreach (var n in plan.Notes) sb.AppendLine("Note: " + n);
        foreach (var e in p.Evidence) sb.AppendLine("Detection: " + e);
        _plan.Text = sb.ToString();
        SetDetailsEnabled(true);
    }

    private static string Shorten(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? "." + path[root.Length..] : path;

    // ------------------------------------------------------------------ actions

    private async Task InstallAsync()
    {
        if (_planObj == null || _profile == null || _busy) return;
        if (!_planObj.IsSupported)
        {
            MessageBox.Show(this, _planObj.UnsupportedReason, "Not supported", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SetBusy(true, "Installing...");
        try
        {
            var plan = _planObj;
            var installer = new Installer(_store);
            var progress = new Progress<string>(s => _status.Text = s);
            await Task.Run(() => installer.InstallAsync(plan, progress));
            var steps = string.Join(Environment.NewLine, plan.PostInstallSteps.Select((s, i) => $"{i + 1}. {s}"));
            await ReanalyzeAsync();
            _plan.Text = "Installed. Next, in the game:" + Environment.NewLine + steps + Environment.NewLine + Environment.NewLine + _plan.Text;
            _status.Text = "Installed.";
        }
        catch (Exception ex)
        {
            Log.Error("Install failed", ex);
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ReanalyzeAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UninstallAsync()
    {
        if (_profile?.Journal == null || _busy) return;
        SetBusy(true, "Uninstalling...");
        try
        {
            var dir = _profile.GameDir;
            var journal = _profile.Journal;
            var installer = new Installer(_store);
            await Task.Run(() => installer.UninstallAsync(dir, journal, new Progress<string>(s => _status.Text = s)));
            await ReanalyzeAsync();
            _status.Text = "Uninstalled.";
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall failed", ex);
            MessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task VerifyAsync()
    {
        if (_profile == null || _busy) return;
        SetBusy(true, "Verifying...");
        try
        {
            var exe = _profile.ExePath;
            var gpu = _gpu;
            var results = await Task.Run(() => Verifier.Verify(exe, _store.Catalog, gpu));
            _plan.Text = string.Join(Environment.NewLine, results.Select(r => r.ToString())) + Environment.NewLine + Environment.NewLine +
                         $"{results.Count(r => r.Status == VerifyStatus.Ok)} ok, {results.Count(r => r.Status == VerifyStatus.Warn)} warning(s), {results.Count(r => r.Status == VerifyStatus.Fail)} failure(s).";
            _status.Text = "Verified.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SelfTestAsync()
    {
        if (_busy) return;
        SetBusy(true, "Running the helper self-test...");
        try
        {
            var gpu = _gpu;
            var result = await Task.Run(() => SelfTest.RunAsync(_store, gpu, new Progress<string>(s => _status.Text = s), hideWindow: true));
            _plan.Text = result.Summary + Environment.NewLine + string.Join(Environment.NewLine, result.Highlights) + Environment.NewLine + "Log: " + result.LogPath;
            _status.Text = result.Success ? "Self-test passed." : "Self-test failed.";
        }
        catch (Exception ex)
        {
            Log.Error("Self-test failed", ex);
            MessageBox.Show(this, ex.Message, "Self-test", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReanalyzeAsync()
    {
        if (_profile == null) return;
        var exe = _profile.ExePath;
        _profile = await Task.Run(() => GameAnalyzer.Analyze(exe));
        if (_current != null)
        {
            _current.Installed = _profile.IsInstalled;
            FillGames();
            foreach (ListViewItem it in _games.Items)
            {
                if (ReferenceEquals(it.Tag, _current)) { it.Selected = true; break; }
            }
        }
        RebuildPlan();
    }

    private void AddGame()
    {
        using var dlg = new OpenFileDialog { Title = "Select the game's executable", Filter = "Executables (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var dir = Path.GetDirectoryName(dlg.FileName)!;
        var g = new GameInfo { Name = Path.GetFileNameWithoutExtension(dlg.FileName), Launcher = "Manual", InstallDir = dir, ExePath = dlg.FileName };
        g.Candidates = ExeLocator.FindCandidates(dir, dlg.FileName);
        g.Installed = InstallJournal.TryLoad(dir) != null;
        _all.Insert(0, g);
        _search.Text = string.Empty;
        FillGames();
        if (_games.Items.Count > 0) _games.Items[0].Selected = true;
    }

    private void BrowseExe()
    {
        if (_current == null) return;
        using var dlg = new OpenFileDialog { Title = "Select the executable ReShade should attach to", Filter = "Executables (*.exe)|*.exe", InitialDirectory = _current.InstallDir };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _suppressEvents = true;
        _exe.Items.Insert(0, new ExeItem(dlg.FileName, _current.InstallDir));
        _exe.SelectedIndex = 0;
        _suppressEvents = false;
        _ = AnalyzeAsync();
    }

    // ------------------------------------------------------------------ updates

    private async Task CheckUpdateAsync()
    {
        try
        {
            _update = await Updater.CheckAsync();
            if (_update != null)
            {
                _updateLink.Text = $"Update {_update.Tag} available - click to install";
                _updateLink.Visible = true;
                AppendLog($"Update available: {_update.Tag} ({_update.HtmlUrl})");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_update == null || _busy) return;
        SetBusy(true, "Downloading update...");
        try
        {
            var path = await Task.Run(() => Updater.DownloadAsync(_update, new Progress<string>(s => _status.Text = s)));
            Updater.ApplyAndRestart(path, restartUi: true);
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _progress.Visible = busy;
        _rescan.Enabled = !busy;
        _addGame.Enabled = !busy;
        _games.Enabled = !busy;
        _selfTest.Enabled = !busy;
        if (status != null) _status.Text = status;
        SetDetailsEnabled(!busy && _profile != null);
        UseWaitCursor = busy;
    }

    private void SetDetailsEnabled(bool enabled)
    {
        _exe.Enabled = enabled && !_busy;
        _browse.Enabled = enabled && !_busy && _current != null;
        _api.Enabled = enabled && !_busy;
        _install.Enabled = enabled && !_busy && _planObj is { IsSupported: true };
        _uninstall.Enabled = enabled && !_busy && _profile?.Journal != null;
        _verify.Enabled = enabled && !_busy && _profile?.Journal != null;
        _openFolder.Enabled = enabled && _profile != null;
    }

    private void AppendLog(string message)
    {
        if (_log.TextLength > 200_000) _log.Clear();
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}
