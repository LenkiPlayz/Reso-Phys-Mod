using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoftbodyManager;

// Offline checks of the manager's logic against fake folders (never the real game).
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  [ok] " : "  [FAIL] ") + what); if (!ok) failures++; }

// the real Resonite may be running meanwhile (the guard itself is checked at the end)
Manager.IgnoreRunningForTests = true;
var root = Path.Combine(Path.GetTempPath(), "softbody-manager-test-" + Guid.NewGuid().ToString("N")[..8]);
var game = Path.Combine(root, "Resonite");
Directory.CreateDirectory(game);
File.WriteAllText(Path.Combine(game, "Resonite.exe"), "fake");
var p = Paths.ForDev(Path.Combine(root, "Testresolink"), game, null);
Directory.CreateDirectory(p.Libraries);
Directory.CreateDirectory(Path.GetDirectoryName(p.BuildDll));
string commitA = new string('a', 40), commitB = new string('b', 40);
void RendererDll(string path, string commit, bool patched)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    File.WriteAllText(path, "junk 1.0.0+" + commit + " junk" + (patched ? " Softbody.FluidGpu" : ""));
}
try
{
    Console.WriteLine("TEST dev setup (Lenki's)");
    var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
    string older = Path.Combine(runtime, "System.Runtime.dll"), newer = typeof(Paths).Assembly.Location;
    var vOld = Manager.VersionOf(older); var vNew = Manager.VersionOf(newer);
    Console.WriteLine($"  stand-ins: installed {vOld}, build {vNew}");
    var a = Manager.Softbody;

    Check(Manager.StateOf(p, a) == AddonState.NotInstalled, "nothing installed: NotInstalled");
    File.Copy(older, a.OnPath(p));
    Check(Manager.StateOf(p, a) == AddonState.On, "Libraries\\Softbody.dll: On");
    Console.WriteLine("  " + Manager.TurnOff(p, a));
    Check(Manager.StateOf(p, a) == AddonState.Off && File.Exists(a.OffPath(p)) && !File.Exists(a.OnPath(p)), "turned off: renamed to Softbody.dll.off");
    Manager.TurnOff(p, a);
    Check(Manager.StateOf(p, a) == AddonState.Off, "turning off twice changes nothing");
    Console.WriteLine("  " + Manager.TurnOn(p, a));
    Check(Manager.StateOf(p, a) == AddonState.On && Manager.Sha256(a.OnPath(p)) == Manager.Sha256(older), "turned on again: the same file back");

    File.Copy(newer, p.BuildDll);
    Console.WriteLine("  " + Manager.Install(p, a, p.BuildDll, new DateTime(2026, 11, 10, 22, 30, 0)));
    var parked = Directory.GetFiles(p.ParkedLibraries).Select(Path.GetFileName).ToList();
    Check(Manager.StateOf(p, a) == AddonState.On && Manager.Sha256(a.OnPath(p)) == Manager.Sha256(newer), "install: the build is in Libraries\\Softbody.dll, on");
    Check(parked.Contains($"Softbody.dll.{Manager.Short(vOld)}.bak"), $"install: the replaced one is parked as Softbody.dll.{Manager.Short(vOld)}.bak");
    var log = File.ReadAllText(p.ParkedLog);
    Check(log.Contains("2026-11-10 22:30") && log.Contains("installed into Libraries\\Softbody.dll with the Softbody manager") && log.Contains("replacing"),
          "install: logged in parked_addons\\README.txt");
    var pristine = Path.Combine(p.ParkedLibraries, $"Softbody.dll.{Manager.Short(vNew)}.installed");
    Check(File.Exists(pristine) && Manager.Sha256(pristine) == Manager.Sha256(newer), "install: an untouched copy kept as Softbody.dll.<version>.installed (for friend packs)");

    Manager.TurnOff(p, a);
    Manager.Install(p, a, p.BuildDll, DateTime.Now);
    parked = Directory.GetFiles(p.ParkedLibraries).Select(Path.GetFileName).ToList();
    Check(Manager.StateOf(p, a) == AddonState.On && !File.Exists(a.OffPath(p)) && parked.Count(x => x.EndsWith(".bak")) == 2, "install while off: on again, the switched-off copy parked under its own name");

    File.Copy(older, a.OffPath(p));
    Check(Manager.StateOf(p, a) == AddonState.OnWithStaleOff, "an on copy and an old .off: OnWithStaleOff");
    Manager.TurnOff(p, a);
    Check(Manager.StateOf(p, a) == AddonState.Off && Manager.Sha256(a.OffPath(p)) == Manager.Sha256(newer), "turning off keeps the current one and parks the old .off");
    Manager.TurnOn(p, a);

    bool threw = false;
    try { Manager.Install(p, a, Path.Combine(root, "nope.dll"), DateTime.Now); } catch (InvalidOperationException) { threw = true; }
    Check(threw && Manager.StateOf(p, a) == AddonState.On, "installing a build that is not there is refused, the installed one stays");

    RendererDll(Path.Combine(p.Game, "Renderer", Paths.RendererDll), commitA, false);
    Check(!Manager.ForkStatus(p).ok, "no fork: attention");
    RendererDll(Path.Combine(p.Fork, Paths.RendererDll), commitA, true);
    Check(Manager.ForkStatus(p).ok, "fork at the installed renderer's commit, patched: OK");
    RendererDll(Path.Combine(p.Game, "Renderer", Paths.RendererDll), commitB, false);
    var fs = Manager.ForkStatus(p);
    Check(!fs.ok && fs.text.StartsWith("out of date"), "Resonite updated its renderer: the fork is out of date");

    var vdf = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"apps\"\n\t\t{\n\t\t\t\"440\"\n\t\t\t{\n\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"\n\t\t\t}\n" +
              "\t\t\t\"2519830\"\n\t\t\t{\n\t\t\t\t\"LastPlayed\"\t\t\"1\"\n\t\t\t\t\"LaunchOptions\"\t\t\"-LoadAssembly Libraries/ResoniteModLoader.dll -LoadAssembly Libraries/Softbody.dll " +
              "-RendererPath \\\"C:\\\\Users\\\\x\\\\RendererSoftbody\\\\Renderite.Renderer.exe\\\"\"\n\t\t\t}\n\t\t}\n\t}\n}\n";
    var opts = Manager.LaunchOptionsIn(vdf, "2519830");
    Check(opts != null && opts.Contains("-LoadAssembly Libraries/Softbody.dll") && opts.Contains("\"C:\\Users\\x\\RendererSoftbody\\Renderite.Renderer.exe\"") && !opts.Contains("-novid"),
          "launch options: Resonite's, unescaped, not another game's");
    Check(Manager.Mentions(opts, "Softbody.dll") && Manager.Mentions(opts, "ResoniteModLoader.dll") && !Manager.Mentions(opts, "Other.dll"), "which addons the launch options load");
    Check(Manager.LaunchOptionsIn(vdf, "999") == null, "an app without launch options: none");

    // ------------------------------------------------------------------ a friend's pack
    Console.WriteLine("TEST friend pack");
    var game2 = Path.Combine(root, "FriendSteam", "steamapps", "common", "Resonite");
    Directory.CreateDirectory(game2);
    File.WriteAllText(Path.Combine(game2, "Resonite.exe"), "fake");
    // their renderer: some files, and BepInEx / Doorstop files that must not be copied
    var renderer = Path.Combine(game2, "Renderer");
    RendererDll(Path.Combine(renderer, Paths.RendererDll), commitA, false);
    File.WriteAllText(Path.Combine(renderer, "Renderite.Renderer.exe"), "exe");
    File.WriteAllText(Path.Combine(renderer, "UnityPlayer.dll"), new string('x', 5000));
    Directory.CreateDirectory(Path.Combine(renderer, "MonoBleedingEdge", "EmbedRuntime"));
    File.WriteAllText(Path.Combine(renderer, "MonoBleedingEdge", "EmbedRuntime", "mono.dll"), "mono");
    Directory.CreateDirectory(Path.Combine(renderer, "BepInEx", "plugins"));
    File.WriteAllText(Path.Combine(renderer, "BepInEx", "plugins", "x.dll"), "bep");
    File.WriteAllText(Path.Combine(renderer, "winhttp.dll"), "doorstop");
    // the pack
    var packDir = Path.Combine(root, "SoftbodyFriendPack");
    var payload = Path.Combine(packDir, "payload");
    Directory.CreateDirectory(payload);
    File.Copy(newer, Path.Combine(payload, "Softbody.dll"));
    RendererDll(Path.Combine(payload, "Renderite.Unity.dll"), commitA, true);
    var manifest = new PackManifest
    {
        SoftbodyVersion = Manager.Short(vNew), SoftbodySha256 = Manager.Sha256(Path.Combine(payload, "Softbody.dll")), CompatibilityHash = "TESTHASH==",
        RenderiteCommit = commitA, RendererDllSha256 = Manager.Sha256(Path.Combine(payload, "Renderite.Unity.dll")), Made = "2026-11-10 22:45",
    };
    manifest.Save(payload);
    Check(Paths.FindPack(packDir) == packDir && Paths.FindPack(Path.Combine(packDir, "payload")) == packDir, "the manager finds the pack next to it");
    var f = Paths.ForPack(packDir, PackManifest.Load(payload), game2, null);
    Check(f.IsPack && f.Pack.CompatibilityHash == "TESTHASH==" && f.Fork == Path.Combine(game2, "RendererSoftbody") && f.Parked == Path.Combine(game2, "SoftbodyBackups"),
          "pack paths: the fork and the backups inside their Resonite folder");

    Console.WriteLine("  " + Manager.Install(f, a, f.BuildDll, DateTime.Now));
    Check(Manager.StateOf(f, a) == AddonState.On && File.ReadAllText(f.ParkedLog).Contains("from a friend pack") && File.ReadAllText(f.ParkedLog).Contains("TESTHASH=="),
          "pack install: Softbody on, logged in SoftbodyBackups with the compatibility hash");

    Check(Manager.PackRendererMismatch(f) == null, "the pack's renderer was built for their renderer");
    Console.WriteLine("  " + Manager.SetUpFork(f, null));
    var forkFiles = Directory.GetFiles(f.Fork, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(f.Fork, x)).ToList();
    Check(File.Exists(Path.Combine(f.Fork, "Renderite.Renderer.exe")) && File.Exists(Path.Combine(f.Fork, "MonoBleedingEdge", "EmbedRuntime", "mono.dll")) &&
          !forkFiles.Any(x => x.StartsWith("BepInEx")) && !File.Exists(Path.Combine(f.Fork, "winhttp.dll")),
          $"fork: their renderer copied, without BepInEx and Doorstop ({forkFiles.Count} files)");
    Check(Manager.Sha256(Path.Combine(f.Fork, Paths.RendererDll)) == manifest.RendererDllSha256 && Manager.ForkStatus(f).ok,
          "fork: the patched Renderite.Unity.dll swapped in, status OK");
    Check(Manager.Sha256(Path.Combine(renderer, Paths.RendererDll)) != manifest.RendererDllSha256, "their own Renderer folder is untouched");
    Console.WriteLine("  " + Manager.SetUpFork(f, null));
    Check(Manager.ForkStatus(f).ok && !Directory.Exists(f.Fork + ".new"), "setting it up again replaces the manager's own fork");

    // their Resonite updated: the pack no longer fits, nothing is touched
    RendererDll(Path.Combine(renderer, Paths.RendererDll), commitB, false);
    var why = Manager.PackRendererMismatch(f);
    threw = false;
    try { Manager.SetUpFork(f, null); } catch (InvalidOperationException) { threw = true; }
    Check(why != null && why.Contains("another Resonite version") && threw, "a pack for another Resonite version is refused: " + why);
    Check(!Manager.ForkStatus(f).ok && Manager.ForkStatus(f).text.Contains("take -RendererPath out"), "and the fork shows out of date, telling them what to do");
    RendererDll(Path.Combine(renderer, Paths.RendererDll), commitA, false);

    // a folder at RendererSoftbody that the manager did not make is never deleted
    Directory.Delete(f.Fork, true);
    Directory.CreateDirectory(f.Fork);
    File.WriteAllText(Path.Combine(f.Fork, "theirs.txt"), "keep me");
    threw = false;
    try { Manager.SetUpFork(f, null); } catch (InvalidOperationException) { threw = true; }
    Check(threw && File.Exists(Path.Combine(f.Fork, "theirs.txt")), "a RendererSoftbody folder the manager did not make is left alone");

    // a damaged pack is refused
    File.AppendAllText(Path.Combine(payload, "Renderite.Unity.dll"), "x");
    Check(Manager.PackRendererMismatch(f)?.Contains("damaged") == true, "a damaged patched renderer is refused");
    File.WriteAllBytes(Path.Combine(payload, "Softbody.dll"), File.ReadAllBytes(older));
    threw = false;
    try { Manager.Install(f, a, f.BuildDll, DateTime.Now); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "a damaged Softbody.dll is refused");

    // launch options for the friend
    var needed = Manager.NeededLaunchOptions(f);
    Console.WriteLine("  needed: " + needed);
    Check(needed == $"-LoadAssembly Libraries/Softbody.dll -RendererPath \"{Path.Combine(game2, "RendererSoftbody", "Renderite.Renderer.exe")}\"", "the launch options a friend needs");
    Check(Manager.HasNeeded(f, needed) == (true, true) && Manager.HasNeeded(f, "") == (false, false) &&
          Manager.HasNeeded(f, "-LoadAssembly Libraries/Softbody.dll") == (true, false), "whether their launch options already have them");

    // ------------------------------------------------------------------ 1.1.0: updates from GitHub
    Console.WriteLine("TEST updates");
    var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()), strangerPub = Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo());

    // a friend's Resonite, and their pack at release v1 (Softbody: the test assembly): installed, the fork made, switched off
    var game3 = Path.Combine(root, "U", "steamapps", "common", "Resonite");
    Directory.CreateDirectory(game3);
    File.WriteAllText(Path.Combine(game3, "Resonite.exe"), "fake");
    var renderer3 = Path.Combine(game3, "Renderer");
    RendererDll(Path.Combine(renderer3, Paths.RendererDll), commitA, false);
    File.WriteAllText(Path.Combine(renderer3, "Renderite.Renderer.exe"), "exe");
    var packU = Path.Combine(root, "UPack");
    var payloadU = Path.Combine(packU, "payload");
    Directory.CreateDirectory(payloadU);
    File.Copy(newer, Path.Combine(payloadU, "Softbody.dll"));
    File.WriteAllText(Path.Combine(payloadU, "Renderite.Unity.dll"), "junk 1.0.0+" + commitA + " junk Softbody patch v1");
    new PackManifest
    {
        SoftbodyVersion = Manager.Short(vNew), SoftbodySha256 = Manager.Sha256(Path.Combine(payloadU, "Softbody.dll")), CompatibilityHash = "HASH1==",
        RenderiteCommit = commitA, RendererDllSha256 = Manager.Sha256(Path.Combine(payloadU, "Renderite.Unity.dll")), Made = "2026-11-11 18:00",
        Tag = "v1.0.0", UpdateRepo = "someone/softbody-downloads", ManagerVersion = "1.1.0",
    }.Save(payloadU);
    var pu = Paths.ForPack(packU, PackManifest.Load(payloadU), game3, null);
    Manager.Install(pu, a, pu.BuildDll, DateTime.Now);
    Manager.SetUpFork(pu, null);
    Manager.TurnOff(pu, a);
    var forkV1 = Manager.Sha256(Path.Combine(pu.Fork, Paths.RendererDll));

    // release v2: a newer Softbody (System.Runtime.dll stands in), a new renderer patch, a new manager
    var rel = Path.Combine(root, "release-v2");
    Directory.CreateDirectory(rel);
    File.Copy(older, Path.Combine(rel, "Softbody.dll"));
    File.WriteAllText(Path.Combine(rel, "Renderite.Unity.dll"), "junk 1.0.0+" + commitA + " junk Softbody patch v2");
    File.WriteAllText(Path.Combine(rel, "SoftbodyManager.exe"), "the manager, version 9.9.9");
    string ManifestText(string tag, string dir, string commit, string notes) => JsonSerializer.Serialize(new PackManifest
    {
        SoftbodyVersion = Manager.Short(Manager.VersionOf(Path.Combine(dir, "Softbody.dll"))), SoftbodySha256 = Manager.Sha256(Path.Combine(dir, "Softbody.dll")),
        CompatibilityHash = "HASH2==", RenderiteCommit = commit, RendererDllSha256 = Manager.Sha256(Path.Combine(dir, "Renderite.Unity.dll")), Made = "2026-11-12 12:00",
        Tag = tag, UpdateRepo = "someone/softbody-downloads", ManagerVersion = "9.9.9", ManagerSha256 = Manager.Sha256(Path.Combine(dir, "SoftbodyManager.exe")), Notes = notes,
    }, new JsonSerializerOptions { WriteIndented = true });
    var text2 = ManifestText("v2.0.0", rel, commitA, "softer jelly");
    var json2 = SignedUpdate.Sign(text2, ec).ToJson();
    var mgr = new Version(1, 1, 0);

    var j = Updater.Judge(json2, pub, pu.Pack, commitA, mgr);
    Check(j.State == UpdateState.Available && j.NewManager && j.Remote.Tag == "v2.0.0" && j.Text.Contains("softer jelly"), "a newer signed release: offered, with a new manager: " + j.Text);
    Check(Updater.Judge(json2, strangerPub, pu.Pack, commitA, mgr).State == UpdateState.Failed, "signed with another key: refused");
    var forged = SignedUpdate.FromJson(json2);
    forged.Manifest = forged.Manifest.Replace("softer jelly", "softer jellx");
    Check(Updater.Judge(forged.ToJson(), pub, pu.Pack, commitA, mgr).State == UpdateState.Failed, "a manifest changed after signing: refused");
    Check(Updater.Judge("{ not json", pub, pu.Pack, commitA, mgr).State == UpdateState.Failed && Updater.Judge(json2, null, pu.Pack, commitA, mgr).State == UpdateState.Failed,
          "unreadable, or no key to check it with: refused");
    Check(Updater.Judge(new SignedUpdate { Manifest = text2, Signature = "" }.ToJson(), pub, pu.Pack, commitA, mgr).State == UpdateState.Failed, "unsigned: refused");
    var noSums = SignedUpdate.Sign(text2.Replace(Manager.Sha256(Path.Combine(rel, "Softbody.dll")), "nope"), ec).ToJson();
    Check(Updater.Judge(noSums, pub, pu.Pack, commitA, mgr).State == UpdateState.Failed, "signed, but without the files' checksums: refused");
    var other = Updater.Judge(json2, pub, pu.Pack, commitB, mgr);
    Check(other.State == UpdateState.OtherResonite, "made for another Resonite renderer: not offered: " + other.Text);
    Check(Updater.Judge(json2, pub, new PackManifest { SoftbodyVersion = "99.0.0", Tag = "v99" }, commitA, mgr).State == UpdateState.UpToDate, "a release older than the pack: not offered");
    Check(!Updater.Judge(json2, pub, pu.Pack, commitA, new Version(9, 9, 9, 0)).NewManager, "the same manager version: no new manager");
    Check(Updater.ReleasesUrl("someone/softbody-downloads") == "https://github.com/someone/softbody-downloads/releases" &&
          Updater.ReleasesUrl("bad repo/x") == null && Updater.ReleasesUrl("a/b/c") == null && Updater.ReleasesUrl(null) == null, "repository names are checked");

    // a stand-in for GitHub on localhost: latest/download sends on to the newest release's file, as github.com does
    var routes = new Dictionary<string, (int status, byte[] body, string location)>();
    var headers = new Dictionary<string, (string name, string value)>();   // 1.2.0: a header to send with a route's answer (under the routes' lock)
    void Release(string tag, string dir, string updateJson)
    {
        lock (routes)
        {
            routes["/o/r/releases/latest/download/update.json"] = (302, null, $"/o/r/releases/download/{tag}/update.json");
            routes[$"/o/r/releases/download/{tag}/update.json"] = (200, Encoding.UTF8.GetBytes(updateJson), null);
            foreach (var name in new[] { "Softbody.dll", "Renderite.Unity.dll", "SoftbodyManager.exe" })
                routes[$"/o/r/releases/download/{tag}/{name}"] = (200, File.ReadAllBytes(Path.Combine(dir, name)), null);
        }
    }
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var server = new HttpListener();
    server.Prefixes.Add($"http://localhost:{port}/");
    server.Start();
    _ = Task.Run(async () =>
    {
        while (server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await server.GetContextAsync(); } catch { break; }
            (int status, byte[] body, string location) route;
            bool known;
            lock (routes) known = routes.TryGetValue(ctx.Request.Url.AbsolutePath, out route);
            if (!known) ctx.Response.StatusCode = 404;
            else
            {
                ctx.Response.StatusCode = route.status;
                if (route.location != null) ctx.Response.RedirectLocation = route.location;
                (string name, string value) header;
                bool hasHeader;
                lock (routes) hasHeader = headers.TryGetValue(ctx.Request.Url.AbsolutePath, out header);
                if (hasHeader) ctx.Response.Headers.Add(header.name, header.value);
                if (route.body != null) { ctx.Response.ContentLength64 = route.body.Length; ctx.Response.OutputStream.Write(route.body); }
            }
            ctx.Response.Close();
        }
    });
    try
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Updater.Agent(mgr));
        var releases = $"http://localhost:{port}/o/r/releases";
        var none = await Updater.CheckAsync(http, releases, pub, pu.Pack, commitA, mgr, CancellationToken.None);
        Check(none.State == UpdateState.Failed && none.Text.Contains("not published"), "no release yet: it says so: " + none.Text);
        Release("v2.0.0", rel, json2);
        var found2 = await Updater.CheckAsync(http, releases, pub, pu.Pack, commitA, mgr, CancellationToken.None);
        Check(found2.State == UpdateState.Available && found2.Remote.Tag == "v2.0.0", "the latest release found through the redirect");

        // a file changed on the way: refused, the pack untouched
        lock (routes) routes["/o/r/releases/download/v2.0.0/Renderite.Unity.dll"] = (200, Encoding.UTF8.GetBytes("junk 1.0.0+" + commitA + " evil"), null);
        threw = false;
        try { await Updater.DownloadAsync(http, releases, found2, packU, null, null, CancellationToken.None); } catch (InvalidDataException) { threw = true; }
        Check(threw && PackManifest.Load(payloadU).Tag == "v1.0.0" && !Directory.GetFiles(packU, "*.part", SearchOption.AllDirectories).Any(),
              "a download that does not match the signed manifest is refused; the pack stays as it was");
        Release("v2.0.0", rel, json2);

        var exeU = Path.Combine(packU, "SoftbodyManager.exe");
        File.WriteAllText(exeU, "the manager, version 1.1.0");
        var downloaded = await Updater.DownloadAsync(http, releases, found2, packU, exeU + ".new", null, CancellationToken.None);
        Check(Manager.Sha256(Path.Combine(downloaded, "Softbody.dll")) == Manager.Sha256(Path.Combine(rel, "Softbody.dll")) &&
              File.ReadAllText(Path.Combine(downloaded, "manifest.json")) == text2 && File.Exists(exeU + ".new"), "downloaded: the files, and the signed manifest word for word");

        var (pu2, done) = Updater.Apply(pu, downloaded, null);
        foreach (var line in done) Console.WriteLine("  " + line);
        Check(pu2.Pack.Tag == "v2.0.0" && PackManifest.Load(Path.Combine(packU, "payload.previous")).Tag == "v1.0.0" && !Directory.Exists(Path.Combine(packU, "payload.new")),
              "applied: the payload is v2, v1 kept as payload.previous");
        Check(Manager.StateOf(pu2, a) == AddonState.Off && Manager.Sha256(a.OffPath(pu2)) == Manager.Sha256(Path.Combine(rel, "Softbody.dll")),
              "Softbody v2 installed, and still off, as it was");
        Check(Manager.Sha256(Path.Combine(pu2.Fork, Paths.RendererDll)) == Manager.Sha256(Path.Combine(rel, "Renderite.Unity.dll")) &&
              File.Exists(Path.Combine(pu2.Parked, "Renderer", $"Renderite.Unity.dll.{forkV1[..8]}.bak")) && Manager.ForkStatus(pu2).ok,
              "the fork has the new patch; the old one is kept in SoftbodyBackups\\Renderer");
        var logU = File.ReadAllText(pu2.ParkedLog);
        Check(logU.Contains("updated the pack to v2.0.0") && logU.Contains("renderer fork: patched Renderite.Unity.dll"), "the update is logged");
        Check(Updater.SwapManager(exeU) && File.ReadAllText(exeU) == "the manager, version 9.9.9" && File.ReadAllText(exeU + ".old") == "the manager, version 1.1.0",
              "the manager swapped: the new one in its place, the old one as .old");
        Updater.CleanUp(exeU);
        Check(!File.Exists(exeU + ".old") && File.Exists(exeU), "the next start deletes the .old");

        var again = await Updater.CheckAsync(http, releases, pub, pu2.Pack, commitA, new Version(9, 9, 9), CancellationToken.None);
        Check(again.State == UpdateState.UpToDate, "after the update: up to date: " + again.Text);

        // Resonite updated its renderer and Lenki made a release for it: the update makes the fork again from the new renderer
        RendererDll(Path.Combine(renderer3, Paths.RendererDll), commitB, false);
        var rel3 = Path.Combine(root, "release-v3");
        Directory.CreateDirectory(rel3);
        File.Copy(Path.Combine(rel, "Softbody.dll"), Path.Combine(rel3, "Softbody.dll"));
        File.WriteAllText(Path.Combine(rel3, "Renderite.Unity.dll"), "junk 1.0.0+" + commitB + " junk Softbody patch v3");
        File.WriteAllText(Path.Combine(rel3, "SoftbodyManager.exe"), "the manager, version 9.9.9");
        var text3 = ManifestText("v2.0.0-r2", rel3, commitB, "for the new Resonite");
        Release("v2.0.0-r2", rel3, SignedUpdate.Sign(text3, ec).ToJson());
        var stale = await Updater.CheckAsync(http, releases, pub, pu2.Pack, commitA, new Version(9, 9, 9), CancellationToken.None);
        Check(stale.State == UpdateState.OtherResonite, "before their Resonite updates: the new release is not offered");
        var found3 = await Updater.CheckAsync(http, releases, pub, pu2.Pack, commitB, new Version(9, 9, 9), CancellationToken.None);
        Check(found3.State == UpdateState.Available && !found3.NewManager, "after: offered (the same Softbody, a new tag)");
        var (pu3, done3) = Updater.Apply(pu2, await Updater.DownloadAsync(http, releases, found3, packU, null, null, CancellationToken.None), null);
        foreach (var line in done3) Console.WriteLine("  " + line);
        Check(done3.Any(x => x.Contains("installed already")) && Manager.ForkStatus(pu3).ok && Manager.CommitOf(Path.Combine(pu3.Fork, Paths.RendererDll)) == commitB,
              "the same Softbody is not installed again; the fork is made again for the new renderer");

        // ------------------------------------------------------------------ 1.2.0: stable and unstable builds
        Console.WriteLine("TEST stable and unstable builds");
        string List(params (string tag, bool pre, bool draft, string[] assets, string published)[] rs) =>
            JsonSerializer.Serialize(rs.Select(r => new Dictionary<string, object>
            {
                ["tag_name"] = r.tag, ["prerelease"] = r.pre, ["draft"] = r.draft, ["published_at"] = r.published,
                ["assets"] = r.assets.Select(n => new Dictionary<string, object> { ["name"] = n, ["browser_download_url"] = "https://example.invalid/" + n }).ToArray(),
            }));
        string[] withUpdate = { "update.json", "Softbody.dll" }, without = { "Softbody.dll" };
        var newest = Updater.Newest(List(("v1.0.0", false, false, withUpdate, "2026-11-10T10:00:00Z"), ("v2.0.0", true, false, withUpdate, "2026-11-11T10:00:00Z"),
                                         ("v3.0.0", true, true, withUpdate, "2026-11-12T10:00:00Z"), ("v2.5.0", true, false, without, "2026-11-12T11:00:00Z"),
                                         ("nightly", true, false, withUpdate, "2026-11-12T12:00:00Z")));
        Check(newest?.Tag == "v2.0.0" && newest.Prerelease, "GitHub's list: the newest release with an update.json, pre-releases in; drafts and tags without a version out");
        Check(Updater.Newest(List(("v2.0.0-r2", true, false, withUpdate, "2026-11-11T10:00:00Z"), ("v2.0.0", true, false, withUpdate, "2026-11-12T10:00:00Z")))?.Tag == "v2.0.0-r2",
              "a repack (v2.0.0-r2) is newer than v2.0.0, whatever the dates");
        Check(Updater.Newest("[]") == null, "no releases: none");
        threw = false;
        try { Updater.Newest("{\"message\": \"Not Found\"}"); } catch (InvalidDataException) { threw = true; }
        Check(threw, "an answer that is not a list: refused");
        Check(UpdateSource.For("someone/softbody-downloads")?.Api == "https://api.github.com/repos/someone/softbody-downloads/releases?per_page=30" &&
              UpdateSource.For("bad repo/x") == null, "the list's address, from the repository");

        UpdateCheck C(UpdateState s, bool older = false, bool unstable = false) => new() { State = s, Older = older, Unstable = unstable, Remote = new PackManifest { Tag = "x" } };
        UpdateCheck upToDate = C(UpdateState.UpToDate), avail = C(UpdateState.Available), uAvail = C(UpdateState.Available, unstable: true), sOlder = C(UpdateState.UpToDate, older: true);
        var o = Updater.Offer(new UpdateStatus { Stable = upToDate, Unstable = uAvail }, Channel.Stable);
        Check(o.Main == null && o.TryUnstable == uAvail && o.BackTo == null && !o.OnUnstable, "stable channel, up to date: the unstable build is there to try");
        o = Updater.Offer(new UpdateStatus { Stable = avail, Unstable = uAvail }, Channel.Stable);
        Check(o.Main == avail && o.TryUnstable == uAvail, "stable channel: Update takes the stable release, the unstable one is still there to try");
        o = Updater.Offer(new UpdateStatus { Stable = avail, Unstable = uAvail }, Channel.Unstable);
        Check(o.Main == uAvail && o.TryUnstable == null && o.OnUnstable, "unstable channel: Update takes the newest");
        o = Updater.Offer(new UpdateStatus { Stable = sOlder, Unstable = C(UpdateState.UpToDate, unstable: true) }, Channel.Unstable);
        Check(o.Main == null && o.BackTo == sOlder, "on the newest unstable build: nothing to update; Back to stable goes back to the stable release");
        o = Updater.Offer(new UpdateStatus { Stable = sOlder }, Channel.Stable);
        Check(o.Main == null && o.BackTo == sOlder && o.TryUnstable == null, "stable channel on a build newer than stable (one Lenki took back): Back to stable offered");
        o = Updater.Offer(new UpdateStatus { Stable = avail, Unstable = C(UpdateState.OtherResonite, unstable: true) }, Channel.Unstable);
        Check(o.Main == avail, "unstable channel, the newest made for another Resonite: Update takes the stable release meanwhile");
        o = Updater.Offer(new UpdateStatus { Stable = upToDate }, Channel.Unstable);
        Check(o.Main == null && o.BackTo == null && o.OnUnstable, "unstable channel with nothing newer than stable: only the switch back");
        Check(Updater.Offer(null, Channel.Stable).Main == null, "no check yet: nothing offered");

        var chanDir = Path.Combine(root, "chan");
        Directory.CreateDirectory(chanDir);
        Check(Updater.LoadChannel(chanDir) == Channel.Stable, "no channel file: stable");
        Updater.SaveChannel(chanDir, Channel.Unstable);
        Check(Updater.LoadChannel(chanDir) == Channel.Unstable && File.ReadAllText(Path.Combine(chanDir, Updater.ChannelFile)) == "unstable", "unstable is kept in update-channel.txt");
        File.WriteAllText(Path.Combine(chanDir, Updater.ChannelFile), "garbage");
        Check(Updater.LoadChannel(chanDir) == Channel.Stable, "anything else in it: stable");

        var jOlder = Updater.Judge(json2, pub, new PackManifest { SoftbodyVersion = "99.0.0", Tag = "v99.0.0" }, commitB, mgr);
        Check(jOlder.State == UpdateState.UpToDate && jOlder.Older && jOlder.OtherRenderer, "a release older than the pack: Older (and made for another renderer: said too)");
        var v2 = PackManifest.Parse(text2);
        Check(Updater.Judge(json2, pub, new PackManifest { SoftbodyVersion = v2.SoftbodyVersion, Tag = v2.Tag + "-r2" }, commitA, mgr).Older,
              "the same Softbody, but the pack is its repack (-r2): the release is older");

        // a friend on the stable release tries the unstable one, goes back, and then Lenki makes the unstable one stable
        var game4 = Path.Combine(root, "C", "steamapps", "common", "Resonite");
        Directory.CreateDirectory(game4);
        File.WriteAllText(Path.Combine(game4, "Resonite.exe"), "fake");
        var renderer4 = Path.Combine(game4, "Renderer");
        RendererDll(Path.Combine(renderer4, Paths.RendererDll), commitA, false);
        File.WriteAllText(Path.Combine(renderer4, "Renderite.Renderer.exe"), "exe");
        string ReleaseDir(string name, string softbody, string patch)
        {
            var d = Path.Combine(root, name);
            Directory.CreateDirectory(d);
            File.Copy(softbody, Path.Combine(d, "Softbody.dll"));
            File.WriteAllText(Path.Combine(d, "Renderite.Unity.dll"), "junk 1.0.0+" + commitA + " junk Softbody patch " + patch);
            File.WriteAllText(Path.Combine(d, "SoftbodyManager.exe"), "the manager, version 9.9.9");
            return d;
        }
        var relS = ReleaseDir("release-stable", newer, "stable");
        var relU = ReleaseDir("release-unstable", older, "unstable");
        string tagS = "v" + Manager.Short(vNew), tagU = "v" + Manager.Short(vOld);
        var textS = ManifestText(tagS, relS, commitA, "the tested one");
        var textU = ManifestText(tagU, relU, commitA, "smoke swirls");
        string jsonS = SignedUpdate.Sign(textS, ec).ToJson(), jsonU = SignedUpdate.Sign(textU, ec).ToJson();
        void Upload(string tag, string dir, string updateJson)
        {
            lock (routes)
            {
                routes[$"/o/s/releases/download/{tag}/update.json"] = (200, Encoding.UTF8.GetBytes(updateJson), null);
                foreach (var name in new[] { "Softbody.dll", "Renderite.Unity.dll", "SoftbodyManager.exe" })
                    routes[$"/o/s/releases/download/{tag}/{name}"] = (200, File.ReadAllBytes(Path.Combine(dir, name)), null);
            }
        }
        void Latest(string tag) { lock (routes) routes["/o/s/releases/latest/download/update.json"] = (302, null, $"/o/s/releases/download/{tag}/update.json"); }
        void Listed(string json) { lock (routes) routes["/api/repos/o/s/releases"] = (200, Encoding.UTF8.GetBytes(json), null); }
        var source = new UpdateSource($"http://localhost:{port}/o/s/releases", $"http://localhost:{port}/api/repos/o/s/releases?per_page=30");
        Upload(tagS, relS, jsonS);
        Upload(tagU, relU, jsonU);
        Latest(tagS);
        Listed(List((tagU, true, false, withUpdate, "2026-11-12T10:00:00Z"), (tagS, false, false, withUpdate, "2026-11-11T10:00:00Z")));

        // their pack: the stable release, installed (on), the fork made
        var pack4 = Path.Combine(root, "SPack");
        var payload4 = Path.Combine(pack4, "payload");
        Directory.CreateDirectory(payload4);
        foreach (var name in new[] { "Softbody.dll", "Renderite.Unity.dll" }) File.Copy(Path.Combine(relS, name), Path.Combine(payload4, name));
        File.WriteAllText(Path.Combine(payload4, PackManifest.FileName), textS);
        var p4 = Paths.ForPack(pack4, PackManifest.Load(payload4), game4, null);
        Manager.Install(p4, a, p4.BuildDll, DateTime.Now);
        Manager.SetUpFork(p4, null);
        var mgr12 = new Version(1, 2, 0);
        var ct = CancellationToken.None;

        Check((await Updater.CheckAsync(http, source.Releases, pub, p4.Pack, commitA, new Version(1, 1, 0), ct)).State == UpdateState.UpToDate,
              "a 1.1.0 manager (it reads only the latest release) never sees the pre-release");
        var st = await Updater.CheckAllAsync(http, source, pub, p4.Pack, commitA, mgr12, ct);
        var offer4 = Updater.Offer(st, Updater.LoadChannel(pack4));
        Check(st.Stable.State == UpdateState.UpToDate && st.Unstable?.State == UpdateState.Available && st.Unstable.Unstable && st.Unstable.Remote.Tag == tagU &&
              offer4.Main == null && offer4.TryUnstable == st.Unstable, "on the stable release: up to date, and the unstable build is there to try: " + st.Unstable?.Text);

        // Try unstable
        var (p5, _) = Updater.Apply(p4, await Updater.DownloadAsync(http, source.Releases, offer4.TryUnstable, pack4, null, null, ct), null);
        Updater.SaveChannel(pack4, Channel.Unstable);
        Check(p5.Pack.Tag == tagU && Manager.StateOf(p5, a) == AddonState.On && Manager.Sha256(a.OnPath(p5)) == Manager.Sha256(older) &&
              File.ReadAllText(Path.Combine(p5.Fork, Paths.RendererDll)).Contains("patch unstable"), "tried the unstable build: it is installed (on), its patch in the fork");
        st = await Updater.CheckAllAsync(http, source, pub, p5.Pack, commitA, mgr12, ct);
        offer4 = Updater.Offer(st, Updater.LoadChannel(pack4));
        Check(offer4.OnUnstable && offer4.Main == null && offer4.BackTo == st.Stable && st.Stable.Older && st.Unstable?.State == UpdateState.UpToDate,
              "on the unstable build: up to date; Back to stable goes back to the stable release: " + st.Stable.Text);

        // Back to stable: the older release goes in the same way
        var (p6, done6) = Updater.Apply(p5, await Updater.DownloadAsync(http, source.Releases, offer4.BackTo, pack4, null, null, ct), null);
        Updater.SaveChannel(pack4, Channel.Stable);
        foreach (var line in done6) Console.WriteLine("  " + line);
        Check(p6.Pack.Tag == tagS && Manager.StateOf(p6, a) == AddonState.On && Manager.Sha256(a.OnPath(p6)) == Manager.Sha256(newer) &&
              File.Exists(Path.Combine(p6.ParkedLibraries, $"Softbody.dll.{Manager.Short(vOld)}.bak")) &&
              File.ReadAllText(Path.Combine(p6.Fork, Paths.RendererDll)).Contains("patch stable") && Manager.ForkStatus(p6).ok,
              "back to stable: the stable Softbody in, the unstable one kept in the backups, the stable patch in the fork");
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        offer4 = Updater.Offer(st, Updater.LoadChannel(pack4));
        Check(!offer4.OnUnstable && offer4.BackTo == null && offer4.Main == null && offer4.TryUnstable?.Remote.Tag == tagU, "on stable again: the unstable build is there to try again");

        // the list goes wrong: the stable check is unharmed, and it says why
        lock (routes)
        {
            routes["/api/repos/o/s/releases"] = (403, Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded\"}"), null);
            headers["/api/repos/o/s/releases"] = ("x-ratelimit-remaining", "0");
        }
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        Check(st.Stable.State == UpdateState.UpToDate && st.Unstable == null && st.UnstableProblem?.Contains("60 an hour") == true,
              "GitHub's limit is used up: the stable check still works, and it says so: " + st.UnstableProblem);
        lock (routes) headers.Remove("/api/repos/o/s/releases");
        Listed("<html>not JSON</html>");
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        Check(st.Stable.State == UpdateState.UpToDate && st.Unstable == null && st.UnstableProblem != null, "a list that is not JSON: said; the stable check unharmed");
        lock (routes) routes.Remove("/api/repos/o/s/releases");
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        Check(st.Stable.State == UpdateState.UpToDate && st.Unstable == null && st.UnstableProblem != null, "no list at all (404): said; the stable check unharmed");

        // pre-releases that must not be taken
        Upload("v99.0.0", relS, jsonS);
        Listed(List(("v99.0.0", true, false, withUpdate, "2026-11-13T10:00:00Z"), (tagS, false, false, withUpdate, "2026-11-11T10:00:00Z")));
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        Check(st.Unstable?.State == UpdateState.Failed && st.Unstable.Text.Contains("carries the manifest of") && Updater.Offer(st, Channel.Unstable).Main == null &&
              Updater.Offer(st, Channel.Stable).TryUnstable == null, "a pre-release that carries another release's signed manifest: refused");
        Upload("v98.0.0", relU, new SignedUpdate { Manifest = textU.Replace(tagU, "v98.0.0"), Signature = "" }.ToJson());
        Listed(List(("v98.0.0", true, false, withUpdate, "2026-11-13T10:00:00Z"), (tagS, false, false, withUpdate, "2026-11-11T10:00:00Z")));
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        Check(st.Unstable?.State == UpdateState.Failed && Updater.Offer(st, Channel.Stable).TryUnstable == null, "an unsigned pre-release: refused, nothing to try");

        // Lenki makes the unstable build stable (no new upload): everyone gets it, a 1.1.0 manager too
        Latest(tagU);
        Listed(List((tagU, false, false, withUpdate, "2026-11-12T10:00:00Z"), (tagS, false, false, withUpdate, "2026-11-11T10:00:00Z")));
        st = await Updater.CheckAllAsync(http, source, pub, p6.Pack, commitA, mgr12, ct);
        offer4 = Updater.Offer(st, Updater.LoadChannel(pack4));
        Check(st.Unstable == null && offer4.Main?.Remote.Tag == tagU && !offer4.Main.Unstable && offer4.TryUnstable == null,
              "made stable: Update offers it on the stable channel, nothing unstable is left");
        var old110 = await Updater.CheckAsync(http, source.Releases, pub, p6.Pack, commitA, new Version(1, 1, 0), ct);
        Check(old110.State == UpdateState.Available && old110.Remote.Tag == tagU, "a 1.1.0 manager gets it too");
    }
    finally { server.Stop(); server.Close(); }

    // the guard: refused while the real Resonite runs, allowed when it is closed
    Console.WriteLine("TEST the running guard");
    Manager.IgnoreRunningForTests = false;
    bool running = Manager.RunningResonite().Count > 0;
    bool refused = false;
    try { Manager.TurnOff(p, a); } catch (InvalidOperationException) { refused = true; }
    Check(running ? refused && Manager.StateOf(p, a) == AddonState.On : !refused && Manager.StateOf(p, a) == AddonState.Off,
          running ? "Resonite is running: switching is refused and nothing moves" : "Resonite is closed: switching works");
}
finally
{
    Manager.IgnoreRunningForTests = false;
    try { Directory.Delete(root, true); } catch { }
}
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
