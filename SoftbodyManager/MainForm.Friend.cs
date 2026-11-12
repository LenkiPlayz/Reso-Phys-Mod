namespace SoftbodyManager;

/// <summary>
/// 1.1.0: the friend pack's view: what a friend needs, top to bottom. The head (and the light/dark switch), whether Resonite
/// is closed, an update banner when Lenki has a new release, the plugin's one switch, the setup checklist (it folds away once
/// everything is done), Launch Resonite, and the rest (paths, checksums, RML, the log) under Details. 1.2.0: under the banner,
/// the unstable builds: Try unstable when there is one, Back to stable when on one; and on one, hard to miss: an UNSTABLE badge
/// next to the title, the card tinted with a warning sign, and "unstable build" in the window's title.
/// </summary>
sealed partial class MainForm
{
    Label _title, _subtitle, _statusText, _updateLine;
    Badge _unstableBadge;
    Label _channelIcon;
    StatusDot _statusDot;
    Card _updateCard;
    Label _updateHead, _updateNotes;
    PillButton _updateButton;
    Card _channelCard;
    Label _channelHead, _channelNotes;
    PillButton _channelButton;
    Label _pluginState, _pluginText;
    ToggleSwitch _pluginSwitch;
    Label _setupHead, _setupCount;
    PillButton _setupFold;
    TableLayoutPanel _steps;
    Step _stepInstall, _stepFork, _stepLaunch;
    bool _showSteps;
    PillButton _launchButton, _detailsButton, _checkButton;
    Card _detailsCard;
    bool _detailsOpen;
    Label _packInfo, _forkInfo, _launchInfo, _rmlText;
    PillButton _rmlButton;
    TableLayoutPanel _rmlRow;
    string _busyText;

    sealed class Step
    {
        public StepIcon Icon;
        public Label Title, Text;
        public PillButton Button;
    }

    void BuildFriendView()
    {
        Text = "Softbody Manager";
        ClientSize = new Size(620, 700);
        MinimumSize = new Size(560, 360);

        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 14, 20, 6) };
        _column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        _column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_column);
        Controls.Add(_scroll);

        // the head
        _title = Lbl("Softbody", 18f, bold: true);
        _title.Margin = new Padding(0, 0, 0, 0);
        _unstableBadge = new Badge { Text = "UNSTABLE", Visible = false };   // 1.2.0: on an unstable build
        var titleRow = Columns((_title, false), (_unstableBadge, false), (new Label { AutoSize = true, Text = "" }, true));
        _subtitle = Lbl("", muted: true);
        _subtitle.Margin = new Padding(1, 0, 0, 10);
        _column.Controls.Add(Columns((Stack(titleRow, _subtitle), true), (_themeButton, false)));

        // Resonite closed or running, and (under it) a quiet line about updates
        _statusDot = new StatusDot();
        _statusText = Lbl("");
        _column.Controls.Add(Columns((_statusDot, false), (_statusText, true)));
        _statusDot.Anchor = AnchorStyles.Top;
        _updateLine = Lbl("", muted: true);
        _updateLine.Margin = new Padding(21, 0, 0, 8);
        _column.Controls.Add(_updateLine);

        // the update banner (only with an update)
        _updateHead = Lbl("", 11.5f, bold: true);
        _updateNotes = Lbl("", muted: true);
        _updateButton = new PillButton { Kind = ButtonKind.Primary, Text = "Update", MinimumSize = new Size(96, 36) };
        _updateButton.Click += (_, _) => RunUpdate(Offer.Main, UpdateKind.Update);
        _updateCard = new Card { Visible = false, Margin = new Padding(0, 4, 0, 12) };
        _updateCard.Add(Columns((Stack(_updateHead, _updateNotes), true), (_updateButton, false)));
        _column.Controls.Add(_updateCard);

        // 1.2.0: the unstable builds: the offer to try one, or (on one) the way back to stable
        _channelHead = Lbl("", 10.5f, bold: true);
        _channelNotes = Lbl("", muted: true);
        _channelButton = new PillButton { Kind = ButtonKind.Secondary, MinimumSize = new Size(96, 34) };
        _channelButton.Click += (_, _) =>
        {
            var o = Offer;
            if (o.OnUnstable || o.BackTo != null) BackToStable();
            else if (o.TryUnstable != null) RunUpdate(o.TryUnstable, UpdateKind.TryUnstable);
        };
        // a warning sign (Segoe MDL2 Assets' Warning; a bold "!" where that font is missing), shown on an unstable build
        var sign = new Font("Segoe MDL2 Assets", 15f);
        bool glyph = sign.Name == "Segoe MDL2 Assets";
        if (!glyph) { sign.Dispose(); sign = new Font("Segoe UI Semibold", 15f); }
        _channelIcon = new Label { AutoSize = true, Text = glyph ? "" : "!", Font = sign, Tag = "warning", Margin = new Padding(0, 3, 12, 0), Visible = false, UseMnemonic = false };
        _channelCard = new Card { Visible = false, Margin = new Padding(0, 0, 0, 12) };
        _channelCard.Add(Columns((_channelIcon, false), (Stack(_channelHead, _channelNotes), true), (_channelButton, false)));
        _channelIcon.Anchor = AnchorStyles.Top;
        _column.Controls.Add(_channelCard);

        // the plugin: one switch
        var pluginTitle = Lbl("Softbody plugin", 12f, bold: true);
        _pluginState = Lbl("", 10f);
        _pluginSwitch = new ToggleSwitch { AccessibleName = "Softbody plugin" };
        _pluginSwitch.Click += (_, _) => Toggle(Manager.Softbody);
        _pluginText = Lbl("", muted: true);
        _pluginText.Margin = new Padding(0, 6, 0, 2);
        var pluginCard = new Card { Margin = new Padding(0, 4, 0, 12) };
        pluginCard.Add(Columns((Stack(pluginTitle, _pluginState), true), (_pluginSwitch, false)));
        pluginCard.Add(_pluginText);
        _column.Controls.Add(pluginCard);

        // the setup checklist
        _setupHead = Lbl("Set up once", 12f, bold: true);
        _setupCount = Lbl("", muted: true);
        _setupCount.Margin = new Padding(10, 5, 0, 0);
        _setupFold = new PillButton { Kind = ButtonKind.Quiet, Text = "Show steps", Visible = false };
        _setupFold.Click += (_, _) => { _showSteps = !_showSteps; RefreshAll(log: false); };
        _stepInstall = NewStep("1", () => Install(Manager.Softbody));
        _stepFork = NewStep("2", SetUpFork);
        _stepLaunch = NewStep("3", CopyLaunchOptions);
        _steps = Stack(Row(_stepInstall), Row(_stepFork), Row(_stepLaunch));
        _steps.Margin = new Padding(0, 6, 0, 0);
        var setupCard = new Card();
        setupCard.Add(Columns((_setupHead, false), (_setupCount, true), (_setupFold, false)));
        _setupHead.Anchor = AnchorStyles.Left;
        _setupCount.Dock = DockStyle.None; _setupCount.Anchor = AnchorStyles.Left;
        setupCard.Add(_steps);
        _column.Controls.Add(setupCard);

        // launch, and the details behind a button
        _launchButton = new PillButton { Kind = ButtonKind.Primary, Text = "Launch Resonite", MinimumSize = new Size(150, 38) };
        _launchButton.Click += (_, _) => Launch();
        _detailsButton = new PillButton { Kind = ButtonKind.Quiet, Text = "Details" };
        _detailsButton.Click += (_, _) => ShowDetails(!_detailsOpen);
        var bottom = Columns((_launchButton, false), (new Label { AutoSize = true, Text = "" }, true), (_detailsButton, false));
        bottom.Margin = new Padding(0, 0, 0, 12);
        _column.Controls.Add(bottom);

        // the details
        _packInfo = Lbl("", muted: true);
        _forkInfo = Lbl("", muted: true);
        _launchInfo = Lbl("", muted: true);
        foreach (var l in new[] { _packInfo, _forkInfo, _launchInfo }) l.Margin = new Padding(0, 2, 0, 8);
        _rmlText = Lbl("");
        _rmlButton = new PillButton();
        _rmlButton.Click += (_, _) => Toggle(Manager.ModLoader);
        _rmlRow = Columns((_rmlText, true), (_rmlButton, false));
        _rmlText.Dock = DockStyle.None; _rmlText.Anchor = AnchorStyles.Left;
        _checkButton = new PillButton { Text = "Check for updates" };
        _checkButton.Click += async (_, _) => await CheckForUpdates(log: true);
        var backups = new PillButton { Text = "Open backups" };
        backups.Click += (_, _) => OpenBackups();
        var refresh = new PillButton { Text = "Refresh" };
        refresh.Click += (_, _) => RefreshAll(log: true);
        _log.MinimumSize = new Size(0, 150);
        _log.Margin = new Padding(0, 8, 0, 0);
        _detailsCard = new Card { Visible = false };
        _detailsCard.Add(Lbl("Details", 12f, bold: true));
        _detailsCard.Add(_packInfo);
        _detailsCard.Add(_forkInfo);
        _detailsCard.Add(_launchInfo);
        _detailsCard.Add(_rmlRow);
        _detailsCard.Add(Flow(_checkButton, backups, refresh));
        _detailsCard.Add(_log);
        _column.Controls.Add(_detailsCard);

        Load += (_, _) =>
        {
            ActiveControl = _launchButton;
            if (OpenDetails) ShowDetails(true);
            FitHeight();
            if (StartPosition == FormStartPosition.CenterScreen) CenterToScreen();
            _column.SizeChanged += (_, _) => FitHeight();
            if (!Offline) _ = CheckForUpdates(log: true);
        };
    }

    Step NewStep(string number, Action act)
    {
        var s = new Step { Icon = new StepIcon(number), Title = Lbl("", 10.5f, bold: true), Text = Lbl("", muted: true), Button = new PillButton() };
        s.Title.Margin = new Padding(0, 4, 0, 0);
        s.Text.Margin = new Padding(0, 1, 0, 2);
        s.Button.Click += (_, _) => act();
        return s;
    }

    static TableLayoutPanel Row(Step s)
    {
        var row = Columns((s.Icon, false), (Stack(s.Title, s.Text), true), (s.Button, false));
        s.Icon.Anchor = AnchorStyles.Top;
        s.Button.Anchor = AnchorStyles.Top;
        row.Margin = new Padding(0, 4, 0, 6);
        return row;
    }

    void ShowDetails(bool open)
    {
        _detailsOpen = open;
        SetVisible(_detailsCard, open);
        SetText(_detailsButton, open ? "Hide details" : "Details");
        if (open) { RefreshAll(log: false); _scroll.ScrollControlIntoView(_detailsCard); }
    }

    /// <summary>While something runs, the status line shows its latest step (the log is under Details).</summary>
    void ShowBusy(string line)
    {
        _busyText = line.Trim();
        if (_statusText != null) SetText(_statusText, "Working: " + _busyText);
    }

    void RefreshFriend(bool closed, bool gameOk, bool can)
    {
        var t = Theme.Current;
        var pack = _paths.Pack;
        SetText(_subtitle, $"Soft avatars, soft objects and liquid for Resonite  ·  {pack.Tag ?? "pack " + pack.SoftbodyVersion}");

        // Resonite
        if (_busy) { SetText(_statusText, "Working: " + (_busyText ?? "...")); _statusDot.Color = t.Accent; }
        else if (!gameOk) { SetText(_statusText, "Resonite was not found: start the manager again and pick its folder."); _statusDot.Color = t.Danger; }
        else if (closed) { SetText(_statusText, "Resonite is closed: changes apply the next time it starts."); _statusDot.Color = t.Success; }
        else { SetText(_statusText, "Resonite is running: close it to change anything."); _statusDot.Color = t.Warning; }
        if (!_busy) _busyText = null;

        ShowUpdate(can);

        // the plugin
        var a = Manager.Softbody;
        var state = Manager.StateOf(_paths, a);
        bool on = state is AddonState.On or AddonState.OnWithStaleOff, none = state == AddonState.NotInstalled;
        var installed = none ? null : Manager.VersionOf(on ? a.OnPath(_paths) : a.OffPath(_paths));
        var packVersion = Manager.VersionOf(_paths.BuildDll);
        _pluginSwitch.On = on;
        _pluginSwitch.Enabled = can && !none;
        SetText(_pluginState, none ? "Not installed yet" : (on ? "On" : "Off") + (installed != null ? $"  ·  version {Manager.Short(installed)}" : ""));
        SetColor(_pluginState, none ? t.Muted : on ? t.Success : t.Muted);
        string text = none ? "Install it with the steps below."
                    : on ? "Soft avatars, soft objects and liquid work. Turn it off to join public sessions."
                         : "Plain Resonite: you can join public sessions. Turn it on for soft avatars, soft objects and liquid.";
        if (!none && _launchOptions != null && !Manager.Mentions(_launchOptions, a.FileName)) text += " Steam's launch options don't load it yet (step 3).";
        if (!none && gameOk && !closed) text += " Close Resonite to switch it.";
        SetText(_pluginText, text);

        // the setup checklist
        bool installOk = installed != null && packVersion != null && installed == packVersion;
        SetStep(_stepInstall, installOk ? StepState.Done : installed == null ? StepState.Todo : StepState.Attention,
            installOk ? $"Softbody {Manager.Short(installed)} is installed"
                      : $"Install Softbody {Manager.Short(packVersion)}" + (installed != null ? $" (you have {Manager.Short(installed)})" : ""),
            installOk ? "" : "Copies the plugin into Resonite's Libraries folder. Whatever it replaces is kept in SoftbodyBackups.",
            installOk ? "Reinstall" : "Install", can && File.Exists(_paths.BuildDll), installOk);

        var (forkOk, forkText) = gameOk ? Manager.ForkStatus(_paths) : (false, "Resonite was not found");
        string mismatch = gameOk ? Manager.PackRendererMismatch(_paths) : "Resonite was not found";
        bool forkThere = Directory.Exists(_paths.Fork);
        SetStep(_stepFork, forkOk ? StepState.Done : !forkThere && mismatch == null ? StepState.Todo : StepState.Attention,
            forkOk ? "The renderer fork is ready" : forkThere ? "Set up the renderer fork again" : "Set up the renderer fork",
            forkOk ? "" :
            mismatch != null ? Capital(mismatch) + "." :
            forkThere ? Capital(forkText) + "." :
            "Copies your Resonite renderer (about 220 MB) and puts the Softbody patch in the copy: soft avatars and the GPU liquid need it. Your Resonite itself is not changed.",
            forkOk ? "Set up again" : "Set up", can && mismatch == null, forkOk);

        var (hasPlugin, hasRenderer) = Manager.HasNeeded(_paths, _launchOptions);
        bool launchOk = hasPlugin && hasRenderer;
        SetStep(_stepLaunch, launchOk ? StepState.Done : StepState.Todo,
            launchOk ? "Steam starts Resonite with Softbody" : "Put the launch options in Steam",
            launchOk ? "" : (_launchOptions == null ? "Steam's settings could not be read; copy them anyway. " : "") +
                            "Copy them, then in Steam: right-click Resonite, Properties, General, Launch Options, and paste.",
            launchOk ? "Copy again" : "Copy", !_busy, launchOk);

        int done = (installOk ? 1 : 0) + (forkOk ? 1 : 0) + (launchOk ? 1 : 0);
        bool all = done == 3, showSteps = !all || _showSteps;
        SetText(_setupHead, all ? "All set up" : "Set up once");
        SetColor(_setupHead, all ? t.Success : t.Text);
        SetText(_setupCount, all ? "" : $"{done} of 3 done");
        SetVisible(_steps, showSteps);
        SetVisible(_setupFold, all);
        SetText(_setupFold, showSteps ? "Hide steps" : "Show steps");

        _launchButton.Enabled = gameOk && closed && !_busy;

        // the details
        SetText(_packInfo, $"This pack: {pack.Tag ?? "Softbody " + pack.SoftbodyVersion}, made {pack.Made}. Compatibility hash {pack.CompatibilityHash ?? "?"}: " +
                           $"everyone in a session together needs the same.{Environment.NewLine}Pack folder: {_paths.Base}{Environment.NewLine}" +
                           $"Resonite: {_paths.Game}{Environment.NewLine}Manager {ManagerVersion.ToString(3)}. Updates: " +
                           (_channel == Channel.Unstable ? "unstable builds too (Lenki's newest, not all tested)." : "stable builds (the ones Lenki tested)."));
        SetText(_forkInfo, $"Renderer fork: {_paths.Fork}{Environment.NewLine}{(forkOk ? "OK: " : "")}{Capital(forkText)}.");
        SetText(_launchInfo, "Launch options now: " + (_launchOptions == null ? "(could not read them)" : _launchOptions.Length == 0 ? "(none)" : _launchOptions) +
                             Environment.NewLine + "They need: " + Manager.NeededLaunchOptions(_paths));
        var rml = Manager.StateOf(_paths, Manager.ModLoader);
        SetVisible(_rmlRow, rml != AddonState.NotInstalled);
        if (rml != AddonState.NotInstalled)
        {
            bool rmlOn = rml is AddonState.On or AddonState.OnWithStaleOff;
            SetText(_rmlText, $"ResoniteModLoader: {(rmlOn ? "on" : "off")}");
            SetText(_rmlButton, rmlOn ? "Turn off" : "Turn on");
            _rmlButton.Enabled = can;
        }
        _checkButton.Enabled = !_checking && !_busy && !Offline;
    }

    void SetStep(Step s, StepState state, string title, string text, string button, bool enabled, bool quiet)
    {
        s.Icon.State = state;
        SetText(s.Title, title);
        SetText(s.Text, text);
        SetVisible(s.Text, text.Length > 0);
        SetText(s.Button, button);
        var kind = quiet ? ButtonKind.Quiet : ButtonKind.Secondary;
        if (s.Button.Kind != kind) { s.Button.Kind = kind; s.Button.Invalidate(); }
        s.Button.Enabled = enabled;
    }

    void ShowUpdate(bool can)
    {
        var t = Theme.Current;
        var s = _status ?? Pretend;
        var offer = Offer;

        // the banner: the release Update takes (the stable one; on the unstable channel the newest)
        var u = _checking ? null : offer.Main;
        SetVisible(_updateCard, u != null);
        if (u != null)
        {
            var m = u.Remote;
            string notes;
            if (u.State == UpdateState.Available)
            {
                SetText(_updateHead, u.Unstable ? $"Unstable build: Softbody {m.SoftbodyVersion} is out" : $"Softbody {m.SoftbodyVersion} is out");
                notes = (u.Unstable ? "Not tested yet. " : "") + (string.IsNullOrWhiteSpace(m.Notes) ? "" : m.Notes.Trim() + " ") +
                        (u.NewManager ? "The manager updates itself too. " : "") + (can || _busy ? "" : "Close Resonite to update.");
                _updateCard.Edge = t.Accent;
            }
            else
            {
                SetText(_updateHead, $"Softbody {m.SoftbodyVersion} is out, for another Resonite version");
                notes = u.Text;
                _updateCard.Edge = t.Warning;
            }
            notes = notes.Trim();
            SetText(_updateNotes, notes);
            SetVisible(_updateNotes, notes.Length > 0);
            SetVisible(_updateButton, u.State == UpdateState.Available);
            _updateButton.Enabled = can;
            _updateCard.Invalidate();
        }

        // 1.2.0: the unstable builds: Back to stable when on them (or on a build newer than the stable one), else Try unstable.
        // On them it stays in sight (also while a check runs) and is hard to miss: the badge, the tinted card, the window's title.
        bool back = offer.OnUnstable || offer.BackTo != null, tryIt = !_checking && !back && offer.TryUnstable != null;
        SetVisible(_channelCard, back || tryIt);
        SetVisible(_unstableBadge, back);
        SetVisible(_channelIcon, back);
        SetText(this, back ? "Softbody Manager (unstable build)" : "Softbody Manager");
        if (_channelCard.Tinted != back)
        {
            _channelCard.Tinted = back;
            Recolor(_channelCard, _channelCard.FillColor);
        }
        if (back)
        {
            var b = offer.BackTo?.Remote;
            SetText(_channelHead, offer.OnUnstable ? "You're on unstable builds" : $"You have an unstable build (Softbody {_paths.Pack.SoftbodyVersion})");
            string text = offer.OnUnstable ? "Lenki's newest builds, before they're tested. If something breaks, tell Lenki and go back to stable"
                                           : "It's newer than Lenki's stable build, and not tested yet. You can go back to stable";
            if (b != null)
                text += offer.BackTo.OtherRenderer ? $". The stable build (Softbody {b.SoftbodyVersion}) is made for another Resonite version: you keep this one until the next."
                                                   : $": that installs Softbody {b.SoftbodyVersion}.";
            else text += ". You have the stable build now; the next unstable build comes as an update.";
            SetText(_channelNotes, text.Trim());
            SetText(_channelButton, "Back to stable");
            _channelButton.Enabled = !_checking && (b == null || offer.BackTo.OtherRenderer ? !_busy : can);
            SetColor(_channelIcon, t.Warning);
            _channelCard.Edge = t.Warning;
            _channelCard.Invalidate();
        }
        else if (tryIt)
        {
            var m = offer.TryUnstable.Remote;
            SetText(_channelHead, $"Unstable build: Softbody {m.SoftbodyVersion}");
            SetText(_channelNotes, "Lenki's newest, not tested yet: it might break. " + (string.IsNullOrWhiteSpace(m.Notes) ? "" : "New: " + m.Notes.Trim() + " ") +
                                   "You can go back to stable any time.");
            SetText(_channelButton, "Try unstable");
            _channelButton.Enabled = can;
            _channelCard.Edge = null;
            _channelCard.Invalidate();
        }

        // the quiet line under Resonite's state: what the check found, when no card says it
        bool warn = false;
        string line;
        if (_checking) line = "Checking for updates ...";
        else if (Offline && Pretend == null)
            line = $"No update check here (updates from {Releases ?? "nowhere"}; Lenki's key {(BuiltIn.UpdateKey != null ? "built in" : "missing")}).";
        else if (s?.Stable == null || s.Stable.State == UpdateState.NotSetUp) line = "";
        else if (s.Stable.State == UpdateState.Failed && !(offer.OnUnstable && s.Unstable?.State is UpdateState.Available or UpdateState.UpToDate))
        { line = s.Stable.Text; warn = true; }
        else if (offer.OnUnstable && s.UnstableProblem != null) { line = $"Could not check for unstable builds: {s.UnstableProblem}."; warn = true; }
        else if (offer.OnUnstable && s.Unstable?.State == UpdateState.Failed) { line = s.Unstable.Text; warn = true; }
        else if (u != null) line = "";
        else if (offer.OnUnstable) line = $"You have Lenki's newest build ({_paths.Pack.Tag ?? "Softbody " + _paths.Pack.SoftbodyVersion}).";
        else if (offer.BackTo != null) line = "";
        else line = s.Stable.Text;
        SetText(_updateLine, line);
        SetVisible(_updateLine, line.Length > 0);
        SetColor(_updateLine, warn ? t.Warning : t.Muted);
    }
}
