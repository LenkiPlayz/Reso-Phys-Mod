using System.Diagnostics;

namespace SoftbodyManager;

/// <summary>
/// The manager's one window, in one of two views. Lenki's (the Softbody project next to it, MainForm.Dev.cs): an on/off
/// switch per addon, install the latest build, rebuild the fork, launch options, launch. A friend pack's (payload\manifest.json
/// next to it, MainForm.Friend.cs; 1.1.0): the plugin's switch, a setup checklist that folds away when done, updates from
/// Lenki's GitHub releases, and the rest under Details. 1.1.0: dark by default, light on request (remembered). 1.2.0: stable
/// and unstable builds (Try unstable, Back to stable).
/// </summary>
sealed partial class MainForm : Form
{
    Paths _paths;   // 1.1.0: an update swaps in the new pack's
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.None };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    bool _busy;
    string _launchOptions;
    /// <summary>What the window scrolls in (the friend view's column, or the dev view's).</summary>
    Panel _scroll;
    TableLayoutPanel _column;
    PillButton _themeButton;

    // 1.1.0: updates (friend packs); 1.2.0: both channels
    UpdateStatus _status;
    Channel _channel;
    bool _checking;
    /// <summary>No update check (the --snapshot picture).</summary>
    public static bool Offline;
    /// <summary>The --snapshot picture's made-up updates (--show-update, --up-to-date, --unstable-offer, --on-unstable).</summary>
    public static UpdateStatus Pretend;
    /// <summary>The --snapshot picture's channel (--on-unstable).</summary>
    public static Channel? PretendChannel;
    /// <summary>The --snapshot picture with the details open (--details).</summary>
    public static bool OpenDetails;
    /// <summary>The --snapshot picture of another Resonite folder and launch options (--game, --launch-options): the states the
    /// real ones are not in.</summary>
    public static string SnapshotGame, SnapshotLaunchOptions;
    static Version ManagerVersion => typeof(MainForm).Assembly.GetName().Version;
    static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Updater.Agent(ManagerVersion));
        return http;
    }

    /// <summary>Where updates come from: the pack's repository, else the one this manager was built with.</summary>
    UpdateSource Source => UpdateSource.For(_paths.Pack?.UpdateRepo ?? BuiltIn.UpdateRepo);
    string Releases => Source?.Releases;

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterScreen;
        _paths = FindPaths();
        _channel = PretendChannel ?? (_paths.IsPack ? Updater.LoadChannel(_paths.Base) : Channel.Stable);
        _themeButton = new PillButton { Kind = ButtonKind.Quiet, MinimumSize = new Size(36, 34), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(8, 0, 0, 0) };
        _themeButton.Click += (_, _) => ToggleTheme();
        if (_paths.IsPack) BuildFriendView(); else BuildDevView();
        _timer.Tick += (_, _) => { if (!_busy) RefreshAll(log: false); };
        _timer.Start();
        Log($"game: {_paths.Game ?? "(not found)"}");
        Log(_paths.IsPack ? $"friend pack: {_paths.Base} ({_paths.Pack.Tag ?? "Softbody " + _paths.Pack.SoftbodyVersion}, made {_paths.Pack.Made})" : $"project: {_paths.Base}");
        ApplyTheme();
        RefreshAll(log: false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this, Theme.Current.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ThemeScrollBars();
    }

    // 1.1.0: the friend view's window fits its content (it grows with the details, up to the screen), until it is resized by hand
    bool _userSized;
    Size _sizeAtResizeBegin;

    protected override void OnResizeBegin(EventArgs e) { base.OnResizeBegin(e); _sizeAtResizeBegin = Size; }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); if (Size != _sizeAtResizeBegin) _userSized = true; }

    void FitHeight()
    {
        if (_userSized || WindowState != FormWindowState.Normal || _column == null) return;
        var area = Screen.FromControl(this).WorkingArea;
        int frame = Height - ClientSize.Height;
        int want = _column.Height + _scroll.Padding.Vertical;
        int h = Math.Max(MinimumSize.Height - frame, Math.Min(want, area.Height - frame));
        if (h == ClientSize.Height) return;
        ClientSize = new Size(ClientSize.Width, h);
        if (!Offline && Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
    }

    /// <summary>A friend pack next to the manager, else Lenki's project folder; Resonite through Steam (or picked by hand).</summary>
    static Paths FindPaths()
    {
        var steam = Paths.FindSteam();
        var game = SnapshotGame ?? Paths.FindGame(steam);
        var packDir = Paths.FindPack(AppContext.BaseDirectory);
        if (game == null)
        {
            using var pick = new FolderBrowserDialog { Description = "Where is Resonite installed? (the folder with Resonite.exe)", UseDescriptionForTitle = true };
            while (pick.ShowDialog() == DialogResult.OK)
            {
                if (File.Exists(Path.Combine(pick.SelectedPath, "Resonite.exe"))) { game = pick.SelectedPath; break; }
                MessageBox.Show("That folder has no Resonite.exe.", "Softbody Manager");
            }
            game ??= packDir == null ? @"F:\SteamLibrary\steamapps\common\Resonite" : Path.Combine(Path.GetTempPath(), "Resonite-not-found");
        }
        if (packDir != null)
        {
            var manifest = PackManifest.Load(Path.Combine(packDir, "payload"));
            if (manifest != null) return Paths.ForPack(packDir, manifest, game, steam);
        }
        var baseDir = Paths.FindBase(AppContext.BaseDirectory) ?? Paths.FindBase(Environment.CurrentDirectory) ?? AppContext.BaseDirectory;
        return Paths.ForDev(baseDir, game, steam);
    }

    // ------------------------------------------------------------------ the theme

    void ToggleTheme()
    {
        Theme.Current = Theme.Current.Dark ? Theme.LightTheme : Theme.DarkTheme;
        Theme.Remember(Theme.Current);
        ApplyTheme();
        ThemeScrollBars();
        RefreshAll(log: false);
    }

    void ApplyTheme()
    {
        var t = Theme.Current;
        BackColor = t.Window;
        ForeColor = t.Text;
        Recolor(this, t.Window);
        // the switch shows where it goes: a sun in the dark, a moon in the light
        bool glyphs = _themeButton.Font.Name == "Segoe MDL2 Assets" || TrySymbolFont();
        _themeButton.Text = glyphs ? (t.Dark ? "\uE706" : "\uE708") : (t.Dark ? "Light" : "Dark");
        _themeButton.AccessibleName = t.Dark ? "Switch to light mode" : "Switch to dark mode";
        Theme.TitleBar(this, t.Dark);
        Invalidate(true);
    }

    bool TrySymbolFont()
    {
        var f = new Font("Segoe MDL2 Assets", 11f);
        if (f.Name != "Segoe MDL2 Assets") { f.Dispose(); return false; }
        _themeButton.Font = f;
        return true;
    }

    void ThemeScrollBars()
    {
        bool dark = Theme.Current.Dark;
        if (_scroll != null) Theme.ScrollBars(_scroll, dark);
        Theme.ScrollBars(_log, dark);
    }

    /// <summary>Gives every control the theme's colours; <paramref name="surface"/> is what it sits on (the window, or a card).</summary>
    static void Recolor(Control parent, Color surface)
    {
        var t = Theme.Current;
        foreach (Control c in parent.Controls)
        {
            var inner = surface;
            switch (c)
            {
                case Card card: card.BackColor = surface; inner = card.FillColor; card.Invalidate(); break;
                case TextBox box: box.BackColor = t.Input; box.ForeColor = t.Text; break;
                case Label l:
                    l.BackColor = surface;
                    l.ForeColor = (l.Tag as string) switch { "muted" => t.Muted, "warning" => t.Warning, _ => t.Text };
                    break;
                default: c.BackColor = surface; c.ForeColor = t.Text; c.Invalidate(); break;
            }
            Recolor(c, inner);
        }
    }

    // ------------------------------------------------------------------ building blocks

    static Label Lbl(string text, float size = 9.75f, bool bold = false, bool muted = false) => new()
    {
        Text = text, AutoSize = true, Tag = muted ? "muted" : null, Margin = new Padding(0, 2, 0, 2), UseMnemonic = false,
        Font = bold ? new Font("Segoe UI Semibold", size) : new Font("Segoe UI", size),
    };

    /// <summary>A row of cells; the filled ones share what the others leave.</summary>
    static TableLayoutPanel Columns(params (Control c, bool fill)[] cells)
    {
        var row = new TableLayoutPanel { ColumnCount = cells.Length, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
        for (int i = 0; i < cells.Length; i++)
        {
            row.ColumnStyles.Add(cells[i].fill ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
            if (cells[i].fill) cells[i].c.Dock = DockStyle.Fill; else cells[i].c.Anchor = AnchorStyles.None;
            row.Controls.Add(cells[i].c, i, 0);
        }
        return row;
    }

    /// <summary>Controls top to bottom, as wide as their cell.</summary>
    static TableLayoutPanel Stack(params Control[] items)
    {
        var s = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
        s.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in items) { c.Dock = DockStyle.Fill; s.Controls.Add(c); }
        return s;
    }

    static FlowLayoutPanel Flow(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        f.Controls.AddRange(items);
        return f;
    }

    static void SetText(Control c, string text) { if (c.Text != text) c.Text = text; }
    // what each control was last asked to be: before the window shows, every control reads as hidden (Visible is the
    // effective state), so comparing with it would skip hiding one that is meant to start hidden
    readonly Dictionary<Control, bool> _wantVisible = new();

    void SetVisible(Control c, bool visible)
    {
        if (_wantVisible.TryGetValue(c, out var was) && was == visible) return;
        _wantVisible[c] = visible;
        c.Visible = visible;
    }
    static void SetColor(Control c, Color color) { if (c.ForeColor != color) c.ForeColor = color; }

    /// <summary>A message's first letter in capitals (Manager's texts start in lower case, to follow a colon).</summary>
    static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    // ------------------------------------------------------------------ state

    void RefreshAll(bool log)
    {
        var running = Manager.RunningResonite();
        bool closed = running.Count == 0;
        bool gameOk = File.Exists(Path.Combine(_paths.Game, "Resonite.exe"));
        if (log || _launchOptions == null) _launchOptions = SnapshotLaunchOptions ?? Manager.ReadLaunchOptions(_paths.SteamRoot);
        bool can = gameOk && closed && !_busy;
        if (_paths.IsPack) RefreshFriend(closed, gameOk, can); else RefreshDev(closed, gameOk, can);
        if (log) Log("refreshed");
    }

    // ------------------------------------------------------------------ actions

    void Toggle(LibraryAddon a)
    {
        try
        {
            var state = Manager.StateOf(_paths, a);
            bool on = state is AddonState.On or AddonState.OnWithStaleOff;
            Log(on ? Manager.TurnOff(_paths, a) : Manager.TurnOn(_paths, a));
        }
        catch (Exception ex) { Log("could not: " + ex.Message); MessageBox.Show(this, ex.Message, "Softbody Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshAll(log: false);
    }

    void Install(LibraryAddon a)
    {
        try
        {
            var build = Manager.VersionOf(_paths.BuildDll);
            var installedFile = File.Exists(a.OnPath(_paths)) ? a.OnPath(_paths) : a.OffPath(_paths);
            var installed = Manager.VersionOf(installedFile);
            string msg = $"Install {a.Name} {Manager.Short(build)}" + (_paths.IsPack ? " from this pack" : $" (built {File.GetLastWriteTime(_paths.BuildDll):MMM d HH:mm})") +
                         " and turn it on?" +
                         (installed != null ? $"\n\nThe installed {Manager.Short(installed)} is kept in {Path.GetFileName(_paths.Parked)}\\Libraries." : "") +
                         "\n\nTo be in a session together, everyone needs this same version.";
            if (MessageBox.Show(this, msg, "Install", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Log(Manager.Install(_paths, a, _paths.BuildDll, DateTime.Now));
        }
        catch (Exception ex) { Log("install failed: " + ex.Message); MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshAll(log: false);
    }

    async void SetUpFork()
    {
        if (MessageBox.Show(this, $"Make the renderer fork?\n\nIt copies your Resonite renderer (about 220 MB) to {_paths.Fork} and puts the Softbody " +
                                  "patch in the copy. Your Resonite itself is not changed.", "Renderer fork", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _busy = true; RefreshAll(log: false);
        Log("making the renderer fork ...");
        try
        {
            var progress = new Progress<string>(Log);
            IProgress<string> report = progress;
            var done = await Task.Run(() => Manager.SetUpFork(_paths, s => report.Report(s)));
            Log(done);
        }
        catch (Exception ex) { Log("could not: " + ex.Message); MessageBox.Show(this, ex.Message, "Renderer fork", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _busy = false; RefreshAll(log: true); }
    }

    async void RebuildFork()
    {
        if (MessageBox.Show(this, "Rebuild the renderer fork with make_softbody_renderer.py?\n\nIt copies Resonite's renderer to RendererSoftbody and swaps in the " +
                                  "patched Renderite.Unity.dll (it needs Python, git, the .NET SDK and RenderiteSrc at the installed renderer's commit).",
                            "Rebuild fork", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (Manager.RunningResonite().Count > 0) { Log("close Resonite first"); return; }
        _busy = true; RefreshAll(log: false);
        Log("running make_softbody_renderer.py ...");
        try
        {
            var psi = new ProcessStartInfo("python", $"\"{_paths.ForkScript}\"")
            {
                WorkingDirectory = _paths.Base, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            var output = proc.StandardOutput.ReadToEndAsync();
            var errors = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            foreach (var line in ((await output) + (await errors)).Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log("  " + line.TrimEnd());
            Log(proc.ExitCode == 0 ? "the fork is rebuilt" : $"the rebuild stopped (exit {proc.ExitCode}): see above");
        }
        catch (Exception ex) { Log("could not run it: " + ex.Message); }
        finally { _busy = false; RefreshAll(log: false); }
    }

    void CopyLaunchOptions()
    {
        var text = Manager.NeededLaunchOptions(_paths);
        // keep whatever else is in them (another mod's -LoadAssembly, say), add only what is missing
        var current = _launchOptions ?? "";
        var (hasPlugin, hasRenderer) = Manager.HasNeeded(_paths, current);
        if (current.Length > 0 && !(hasPlugin && hasRenderer) && !current.Contains("-RendererPath", StringComparison.OrdinalIgnoreCase))
            text = (current.Trim() + " " + (hasPlugin ? "" : $"-LoadAssembly Libraries/{Manager.Softbody.FileName} ") +
                    $"-RendererPath \"{Path.Combine(_paths.Fork, "Renderite.Renderer.exe")}\"").Trim();
        try
        {
            Clipboard.SetText(text);
            Log("copied: " + text);
            MessageBox.Show(this, "Copied:\n\n" + text + "\n\nIn Steam: right-click Resonite, Properties, General, Launch Options: replace what is there with this " +
                                  "(paste), then close the window.", "Launch options", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Log("could not copy: " + ex.Message); }
    }

    void Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + Manager.ResoniteAppId) { UseShellExecute = true });
            Log("asked Steam to start Resonite (with your launch options)");
        }
        catch (Exception ex) { Log("could not start it: " + ex.Message); }
    }

    void OpenBackups()
    {
        try { Directory.CreateDirectory(_paths.ParkedLibraries); Process.Start(new ProcessStartInfo("explorer.exe", _paths.ParkedLibraries)); }
        catch (Exception ex) { Log("could not open it: " + ex.Message); }
    }

    // ------------------------------------------------------------------ 1.1.0: updates; 1.2.0: stable and unstable builds

    /// <summary>What the window offers now (from the real check, or the picture's made-up one).</summary>
    UpdateOffer Offer => Updater.Offer(_status ?? Pretend, _channel);

    async Task CheckForUpdates(bool log)
    {
        if (!_paths.IsPack || _checking || Offline) return;
        _checking = true;
        RefreshAll(log: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _status = await Updater.CheckAllAsync(Http, Source, BuiltIn.UpdateKey, _paths.Pack, Manager.OfficialCommit(_paths), ManagerVersion, cts.Token);
        }
        catch (Exception ex) { _status = new UpdateStatus { Stable = new UpdateCheck { State = UpdateState.Failed, Text = "Could not check for updates: " + ex.Message } }; }
        finally { _checking = false; }
        RefreshAll(log: false);
        if (log)
        {
            Log("updates: " + _status.Stable.Text);
            if (_status.Unstable != null) Log("unstable builds: " + _status.Unstable.Text);
            else if (_status.UnstableProblem != null) Log("unstable builds could not be checked: " + _status.UnstableProblem);
            else if (_status.Stable.State != UpdateState.NotSetUp) Log("unstable builds: none newer than the stable one");
        }
    }

    enum UpdateKind { Update, TryUnstable, BackToStable }

    /// <summary>1.2.0: Back to stable: the stable release goes in when the pack is newer than it; then the pack takes stable builds again.</summary>
    void BackToStable()
    {
        var back = Offer.BackTo;
        if (back == null)
        {
            SetChannel(Channel.Stable);
            Log("you get stable builds again (you have the stable build already)");
            RefreshAll(log: false);
            return;
        }
        if (back.OtherRenderer)
        {
            SetChannel(Channel.Stable);
            Log($"you get stable builds again; the stable build ({back.Remote.Tag}) is made for another Resonite version, so Softbody {_paths.Pack.SoftbodyVersion} stays");
            MessageBox.Show(this, $"You get stable builds again.\n\nThe stable build (Softbody {back.Remote.SoftbodyVersion}) is made for another Resonite version, so " +
                                  $"you keep Softbody {_paths.Pack.SoftbodyVersion} until Lenki's next stable build (Update offers it).",
                            "Back to stable", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshAll(log: false);
            return;
        }
        RunUpdate(back, UpdateKind.BackToStable);
    }

    void SetChannel(Channel channel)
    {
        _channel = channel;
        if (Offline) return;
        try { Updater.SaveChannel(_paths.Base, channel); }
        catch (Exception ex) { Log("could not keep the choice for next time: " + ex.Message); }
    }

    async void RunUpdate(UpdateCheck check, UpdateKind kind)
    {
        if (check?.Remote == null || _busy) return;
        if (kind != UpdateKind.BackToStable && check.State != UpdateState.Available) return;
        var m = check.Remote;
        var exe = Environment.ProcessPath;
        // the manager updates itself only where it belongs: next to the pack's payload (and only to a newer one)
        bool withManager = check.NewManager && exe != null &&
                           string.Equals(Path.GetDirectoryName(Path.GetFullPath(exe)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.Base)),
                                         StringComparison.OrdinalIgnoreCase);
        string have = _paths.Pack.SoftbodyVersion, backups = Path.GetFileName(_paths.Parked);
        string download = $"It downloads about {(withManager ? "50 MB (with the new manager)" : "1 MB")} and checks every file against Lenki's signature.";
        string notes = string.IsNullOrWhiteSpace(m.Notes) ? "" : "\n\nNew: " + m.Notes.Trim();
        string msg = kind switch
        {
            UpdateKind.TryUnstable =>
                $"Try the unstable build, Softbody {m.SoftbodyVersion}?\n\nIt is Lenki's newest build and not tested yet: things may break. You can go back to " +
                $"the stable build any time (Back to stable).{notes}\n\n{download} It installs Softbody {m.SoftbodyVersion} (your {have} is kept in {backups}) " +
                "and puts its patch in your renderer fork." + (withManager ? " Then the manager restarts." : "") +
                "\n\nTo be in a session together, everyone needs the same version: friends on the stable build can't join you, nor you them, until you " +
                "have the same one again.",
            UpdateKind.BackToStable =>
                $"Go back to the stable build, Softbody {m.SoftbodyVersion}?\n\nIt installs Softbody {m.SoftbodyVersion} in place of {have} ({have} is kept in " +
                $"{backups}) and puts the stable patch in your renderer fork. {download}\n\nSettings that only {have} has are dropped from what you load " +
                $"in {m.SoftbodyVersion}.\n\nTo be in a session together, everyone needs the same version.",
            _ =>
                $"Update to Softbody {m.SoftbodyVersion} ({m.Tag})?" + (check.Unstable ? " It is an unstable build: not tested yet." : "") + notes +
                $"\n\n{download} It installs Softbody {m.SoftbodyVersion} (the old one is kept in {backups}) and puts the new patch in your renderer fork." +
                (withManager ? " Then the manager restarts." : "") +
                "\n\nTo be in a session together, everyone needs this same version.",
        };
        string title = kind switch { UpdateKind.TryUnstable => "Try unstable", UpdateKind.BackToStable => "Back to stable", _ => "Update" };
        if (MessageBox.Show(this, msg, title, MessageBoxButtons.YesNo, kind == UpdateKind.Update ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _busy = true; RefreshAll(log: false);
        bool restart = false, applied = false;
        Log($"{(kind == UpdateKind.BackToStable ? "going back to" : "updating to")} {m.Tag}{(check.Unstable ? " (an unstable build)" : "")} ...");
        try
        {
            var progress = new Progress<string>(Log);
            IProgress<string> report = progress;
            var downloaded = await Updater.DownloadAsync(Http, Releases, check, _paths.Base, withManager ? exe + ".new" : null, progress, CancellationToken.None);
            if (Manager.RunningResonite().Count > 0) throw new InvalidOperationException($"Resonite was started meanwhile: close it and press {title} again");
            var current = _paths;
            var (np, done) = await Task.Run(() => Updater.Apply(current, downloaded, s => report.Report(s)));
            _paths = np;
            applied = true;
            foreach (var line in done) Log(line);
            if (kind == UpdateKind.TryUnstable) SetChannel(Channel.Unstable);
            if (kind == UpdateKind.BackToStable) SetChannel(Channel.Stable);
            if (withManager) restart = Updater.SwapManager(exe);
            Log((restart ? $"now on {m.Tag}; the manager is new too: it restarts" : $"now on {m.Tag}") +
                (kind == UpdateKind.TryUnstable ? " (you get unstable builds now)" : kind == UpdateKind.BackToStable ? " (you get stable builds again)" : ""));
        }
        catch (Exception ex)
        {
            Log((applied ? "the manager's own update stopped: " : "it stopped: ") + ex.Message);
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _busy = false; RefreshAll(log: false); }
        if (restart)
        {
            MessageBox.Show(this, $"Now on Softbody {m.SoftbodyVersion}. The manager restarts now to finish its own update.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = _paths.Base }); Close(); }
            catch (Exception ex) { Log("could not restart it: " + ex.Message + " (start it again yourself)"); }
        }
        else await CheckForUpdates(log: false);
    }

    void Log(string line)
    {
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (_busy && _paths != null && _paths.IsPack) ShowBusy(line);
    }
}
