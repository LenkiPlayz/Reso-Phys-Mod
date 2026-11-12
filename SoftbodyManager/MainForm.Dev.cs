namespace SoftbodyManager;

/// <summary>Lenki's view (the Softbody project next to the manager): each addon's switch and install, the renderer fork, the
/// launch options, launch, and the log below. (1.1.0: the same cards and colours as the friend view.)</summary>
sealed partial class MainForm
{
    Label _resonite, _forkText, _launchText, _launchCheck;
    readonly List<AddonRow> _rows = new();
    PillButton _forkButton, _launch;

    sealed class AddonRow
    {
        public LibraryAddon Addon;
        public Label State, Detail;
        public ToggleSwitch Toggle;
        public PillButton Install;
    }

    void BuildDevView()
    {
        Text = "Softbody Manager";
        ClientSize = new Size(720, 860);
        MinimumSize = new Size(600, 520);
        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 12, 18, 4) };
        _column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        _column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_column);

        var title = Lbl("Softbody Manager", 16f, bold: true);
        _resonite = Lbl("", 10f, bold: true);
        _resonite.Margin = new Padding(0, 2, 0, 10);
        _column.Controls.Add(Columns((Stack(title, _resonite), true), (_themeButton, false)));

        foreach (var addon in new[] { Manager.Softbody, Manager.ModLoader })
        {
            var row = new AddonRow
            {
                Addon = addon, State = Lbl("", 10f), Detail = Lbl("", muted: true),
                Toggle = new ToggleSwitch { AccessibleName = addon.Name },
                Install = addon.Installable ? new PillButton { Text = "Install latest build" } : null,
            };
            row.Toggle.Click += (_, _) => Toggle(addon);
            if (row.Install != null) row.Install.Click += (_, _) => Install(addon);
            row.Detail.Margin = new Padding(0, 6, 0, 2);
            var card = new Card();
            card.Add(Columns((Stack(Lbl(addon.Name, 12f, bold: true), row.State), true), (row.Toggle, false)));
            card.Add(row.Detail);
            if (row.Install != null) card.Add(Flow(row.Install));
            _column.Controls.Add(card);
            _rows.Add(row);
        }

        _forkText = Lbl("");
        _forkButton = new PillButton { Text = "Rebuild fork" };
        _forkButton.Click += (_, _) => RebuildFork();
        var forkCard = new Card();
        forkCard.Add(Lbl("Renderer fork", 12f, bold: true));
        forkCard.Add(Lbl($"{_paths.Fork}, started by -RendererPath. It stays as it is when the plugin is off (it only matters to the softbody and " +
                         "the GPU fluid). After a Resonite update, rebuild it.", muted: true));
        forkCard.Add(_forkText);
        forkCard.Add(Flow(_forkButton));
        _column.Controls.Add(forkCard);

        _launchText = Lbl("", muted: true);
        _launchCheck = Lbl("");
        var copy = new PillButton { Text = "Copy launch options" };
        copy.Click += (_, _) => CopyLaunchOptions();
        var launchCard = new Card();
        launchCard.Add(Lbl("Steam launch options", 12f, bold: true));
        launchCard.Add(Lbl("Read only: the manager never changes them.", muted: true));
        launchCard.Add(_launchText);
        launchCard.Add(_launchCheck);
        launchCard.Add(Flow(copy));
        _column.Controls.Add(launchCard);

        _launch = new PillButton { Kind = ButtonKind.Primary, Text = "Launch Resonite", MinimumSize = new Size(150, 38) };
        _launch.Click += (_, _) => Launch();
        var refresh = new PillButton { Text = "Refresh" };
        refresh.Click += (_, _) => RefreshAll(log: true);
        var backups = new PillButton { Text = "Open backups" };
        backups.Click += (_, _) => OpenBackups();
        _column.Controls.Add(Flow(_launch, refresh, backups));

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_scroll);
        _log.Dock = DockStyle.Fill;
        split.Panel2.Padding = new Padding(18, 4, 18, 10);
        split.Panel2.Controls.Add(_log);
        Controls.Add(split);
        Load += (_, _) =>
        {
            try { split.SplitterDistance = Math.Max(200, ClientSize.Height - 150); } catch { }
            ActiveControl = (Control)_rows[0].Install ?? _rows[0].Toggle;   // open at the top
            _scroll.AutoScrollPosition = new Point(0, 0);
        };
    }

    void RefreshDev(bool closed, bool gameOk, bool can)
    {
        var t = Theme.Current;
        SetText(_resonite, !gameOk ? "Resonite was not found: start the manager again and pick its folder." :
                           closed ? "Resonite is closed: changes apply the next time it starts."
                                  : "Resonite is running: close it to change anything (its files are in use).");
        SetColor(_resonite, !gameOk ? t.Danger : closed ? t.Success : t.Warning);

        foreach (var row in _rows)
        {
            var a = row.Addon;
            var state = Manager.StateOf(_paths, a);
            bool on = state is AddonState.On or AddonState.OnWithStaleOff;
            SetText(row.State, state switch { AddonState.NotInstalled => "not installed", _ when on => "On", _ => "Off" });
            SetColor(row.State, state == AddonState.NotInstalled ? t.Muted : on ? t.Success : t.Muted);
            var detail = new List<string>();
            var installed = state == AddonState.NotInstalled ? null : Manager.VersionOf(on ? a.OnPath(_paths) : a.OffPath(_paths));
            if (installed != null) detail.Add($"installed: {Manager.Short(installed)}");
            if (a.Installable)
            {
                var build = Manager.VersionOf(_paths.BuildDll);
                if (build == null) detail.Add("no build found in Softbody\\bin\\Release");
                else
                {
                    string cmp = installed == null ? "" : build > installed ? " (newer: install it)" : build == installed ? " (the same version)" : " (older than the installed one)";
                    detail.Add($"latest build: {Manager.Short(build)}, {File.GetLastWriteTime(_paths.BuildDll):MMM d HH:mm}{cmp}");
                }
            }
            if (_launchOptions != null && state != AddonState.NotInstalled && !Manager.Mentions(_launchOptions, a.FileName))
                detail.Add($"note: your launch options have no -LoadAssembly Libraries/{a.FileName}, so it does not load even when on");
            detail.Add(a.About);
            SetText(row.Detail, string.Join(Environment.NewLine, detail));
            row.Toggle.On = on;
            row.Toggle.Enabled = can && state != AddonState.NotInstalled;
            if (row.Install != null) row.Install.Enabled = can && File.Exists(_paths.BuildDll);
        }

        if (!gameOk) { SetText(_forkText, "Resonite not found"); SetColor(_forkText, t.Danger); _forkButton.Enabled = false; }
        else
        {
            var (ok, text) = Manager.ForkStatus(_paths);
            SetText(_forkText, (ok ? "OK: " : "Attention: ") + text);
            SetColor(_forkText, ok ? t.Success : t.Warning);
            _forkButton.Enabled = can && File.Exists(_paths.ForkScript);
        }

        var (hasPlugin, hasRenderer) = Manager.HasNeeded(_paths, _launchOptions);
        SetText(_launchText, "Now: " + (_launchOptions == null ? "could not read them (Steam's localconfig.vdf)" : _launchOptions.Length == 0 ? "(none set)" : _launchOptions));
        SetText(_launchCheck, hasPlugin && hasRenderer ? "They load Softbody and the renderer fork."
            : "Needed: " + Manager.NeededLaunchOptions(_paths) + Environment.NewLine +
              (_launchOptions == null ? "" : $"(missing: {string.Join(" and ", new[] { hasPlugin ? null : "the plugin", hasRenderer ? null : "the renderer fork" }.Where(x => x != null))}; " +
                                              "Copy launch options, then paste them in Steam: Resonite, Properties, Launch Options)"));
        SetColor(_launchCheck, hasPlugin && hasRenderer ? t.Success : t.Warning);
        _launch.Enabled = gameOk && closed && !_busy;
    }
}
