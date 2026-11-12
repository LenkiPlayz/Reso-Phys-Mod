namespace SoftbodyManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Updater.CleanUp(Environment.ProcessPath);   // 1.1.0: what a self-update left (the replaced manager)
        // --snapshot <png>: draws the window (with the real, read-only status) to a picture and exits; for checking the layout.
        // 1.1.0: --theme dark|light (for the picture only), --details (open), --show-update or --up-to-date (made up; nothing is fetched),
        // --game <folder> and --launch-options <text> (instead of the real ones). 1.2.0: --unstable-offer (an unstable build to try)
        // and --on-unstable (the pack on unstable builds, newer than the stable one): made up too
        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            MainForm.Offline = true;
            int themeAt = Array.IndexOf(args, "--theme");
            if (themeAt > 0 && themeAt + 1 < args.Length) Theme.Current = args[themeAt + 1] == "light" ? Theme.LightTheme : Theme.DarkTheme;
            MainForm.OpenDetails = args.Contains("--details");
            int gameAt = Array.IndexOf(args, "--game"), launchAt = Array.IndexOf(args, "--launch-options");
            if (gameAt > 0 && gameAt + 1 < args.Length) MainForm.SnapshotGame = args[gameAt + 1];
            if (launchAt > 0 && launchAt + 1 < args.Length) MainForm.SnapshotLaunchOptions = args[launchAt + 1];
            // the made-up releases go by the pack's own version (the payload next to the exe), so the picture adds up
            var here = PackManifest.Load(Path.Combine(AppContext.BaseDirectory, "payload"));
            var hv = Version.TryParse(here?.SoftbodyVersion, out var parsed) ? parsed : new Version(9, 9, 8);
            string have = $"{hv.Major}.{hv.Minor}.{Math.Max(0, hv.Build)}", next = $"{hv.Major}.{hv.Minor + 1}.0",
                   before = hv.Build > 0 ? $"{hv.Major}.{hv.Minor}.{hv.Build - 1}" : $"{hv.Major}.{Math.Max(0, hv.Minor - 1)}.0";
            UpdateCheck Made(UpdateState state, string version, bool unstable = false, bool older = false, string notes = null) => new()
            {
                State = state, Unstable = unstable, Older = older,
                Remote = new PackManifest { SoftbodyVersion = version, Tag = "v" + version, Notes = notes },
                Text = state != UpdateState.UpToDate ? $"Softbody {version} is out (an example)." :
                       unstable ? $"You have Lenki's newest build (v{version}: Softbody {version}, an unstable build)." :
                       older ? $"Your pack (Softbody {have}) is newer than Lenki's stable build (v{version})." :
                       $"You have Lenki's latest stable build (v{version}: Softbody {version}).",
            };
            const string exampleNotes = "An example of the notes Lenki writes.";
            if (args.Contains("--up-to-date")) MainForm.Pretend = new UpdateStatus { Stable = Made(UpdateState.UpToDate, have) };
            if (args.Contains("--show-update"))
            {
                MainForm.Pretend = new UpdateStatus { Stable = Made(UpdateState.Available, next, notes: exampleNotes) };
                MainForm.Pretend.Stable.NewManager = true;
            }
            if (args.Contains("--unstable-offer"))
                MainForm.Pretend = new UpdateStatus { Stable = Made(UpdateState.UpToDate, have), Unstable = Made(UpdateState.Available, next, unstable: true, notes: exampleNotes) };
            if (args.Contains("--on-unstable"))
            {
                MainForm.PretendChannel = Channel.Unstable;
                MainForm.Pretend = new UpdateStatus { Stable = Made(UpdateState.UpToDate, before, older: true), Unstable = Made(UpdateState.UpToDate, have, unstable: true) };
            }
            using var form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
            return;
        }
        Application.Run(new MainForm());
    }
}
