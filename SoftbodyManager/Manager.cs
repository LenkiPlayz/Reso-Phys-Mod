using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoftbodyManager;

/// <summary>What a friend pack carries (payload\manifest.json next to the manager).</summary>
sealed class PackManifest
{
    public string SoftbodyVersion { get; set; }
    public string SoftbodySha256 { get; set; }
    /// <summary>The data model's compatibility hash: everyone in a session needs the same.</summary>
    public string CompatibilityHash { get; set; }
    /// <summary>The Renderite commit the patched Renderite.Unity.dll was built from: the friend's Resonite must have the same renderer.</summary>
    public string RenderiteCommit { get; set; }
    public string RendererDllSha256 { get; set; }
    public string Made { get; set; }
    /// <summary>1.1.0: the GitHub release this pack is (its tag, e.g. v0.25.0): an update is a later release.</summary>
    public string Tag { get; set; }
    /// <summary>1.1.0: where updates come from: the GitHub repository (owner/name) whose latest release has update.json.</summary>
    public string UpdateRepo { get; set; }
    /// <summary>1.1.0: the manager in this release: its version and the sha256 of SoftbodyManager.exe.</summary>
    public string ManagerVersion { get; set; }
    public string ManagerSha256 { get; set; }
    /// <summary>1.1.0: what is new, in a line or two.</summary>
    public string Notes { get; set; }
    /// <summary>1.2.0: files the renderer fork needs besides the patched DLL (0.27.0: the smoke shader, softbody_smoke.bundle).
    /// They sit next to it in the payload and the release, and go in the fork's Renderite.Renderer_Data\StreamingAssets\Softbody.
    /// None in packs made before. A manager before 1.2.0 skips the list (it does not know it): see Manager.PackLacks.</summary>
    public List<ForkFile> ForkFiles { get; set; }

    public const string FileName = "manifest.json";
    static readonly Regex ForkFileName = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*(?:\.[A-Za-z0-9_-]+)+$"),
                          DeviceName = new(@"^(?:CON|PRN|AUX|NUL|COM\d|LPT\d)$", RegexOptions.IgnoreCase), Sha = new("^[0-9a-fA-F]{64}$");
    static readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase)
        { "Softbody.dll", "Renderite.Unity.dll", "SoftbodyManager.exe", FileName, SignedUpdate.FileName };

    /// <summary>1.2.0: what is wrong with the list of renderer files (null: nothing; no list is fine). A name is a plain file
    /// name with an extension (no folders), not one the pack's other files use.</summary>
    public string ForkFilesProblem()
    {
        if (ForkFiles == null) return null;
        if (ForkFiles.Count > 16) return "lists too many renderer files";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ForkFiles)
        {
            if (f?.Name == null || f.Name.Length > 64 || !ForkFileName.IsMatch(f.Name) || DeviceName.IsMatch(f.Name.Split('.')[0]) || Taken.Contains(f.Name) ||
                f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                return "lists a renderer file with an unusable name";
            if (f.Sha256 == null || !Sha.IsMatch(f.Sha256)) return $"lacks the checksum of {f.Name}";
            if (!seen.Add(f.Name)) return $"lists {f.Name} twice";
        }
        return null;
    }

    public static PackManifest Load(string payloadDir)
    {
        var f = Path.Combine(payloadDir, FileName);
        return File.Exists(f) ? Parse(File.ReadAllText(f)) : null;
    }
    public static PackManifest Parse(string json) => JsonSerializer.Deserialize<PackManifest>(json);
    public void Save(string payloadDir) =>
        File.WriteAllText(Path.Combine(payloadDir, FileName), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>1.2.0: a file the renderer fork needs besides the patched DLL: its name (the same in the payload, the release and
/// the fork's StreamingAssets\Softbody folder) and its sha256.</summary>
sealed class ForkFile
{
    public string Name { get; set; }
    public string Sha256 { get; set; }
}

/// <summary>
/// Where everything is. Two setups:
/// <list type="bullet">
/// <item>Lenki's (dev): the Testresolink folder holds the Softbody project (its build), RendererSoftbody (the fork, rebuilt by
/// make_softbody_renderer.py) and parked_addons (backups and their log).</item>
/// <item>A friend's (pack): the manager sits next to a payload folder (Softbody.dll, the patched Renderite.Unity.dll,
/// manifest.json); the fork is made inside their Resonite folder (Resonite\RendererSoftbody) and backups go to
/// Resonite\SoftbodyBackups.</item>
/// </list>
/// </summary>
sealed class Paths
{
    public string Game, SteamRoot;
    public string BuildDll;       // the Softbody.dll to install
    public string Parked;         // backups (Libraries\...) and their README.txt log
    public string Fork;           // the renderer fork's folder (-RendererPath points at its Renderite.Renderer.exe)
    public string ForkScript;     // dev: make_softbody_renderer.py
    public string PayloadDir;     // pack: the payload folder
    public PackManifest Pack;     // pack: what it carries
    public string Base;           // dev: Testresolink; pack: the manager's folder

    public bool IsPack => Pack != null;
    public string Libraries => Path.Combine(Game, "Libraries");
    public string ParkedLibraries => Path.Combine(Parked, "Libraries");
    public string ParkedLog => Path.Combine(Parked, "README.txt");
    public string PackRendererDll => PayloadDir == null ? null : Path.Combine(PayloadDir, "Renderite.Unity.dll");
    /// <summary>1.2.0: one of the pack's renderer files (in the payload).</summary>
    public string PackFile(string name) => PayloadDir == null ? null : Path.Combine(PayloadDir, name);
    public const string RendererDll = @"Renderite.Renderer_Data\Managed\Renderite.Unity.dll";
    /// <summary>1.2.0: where the renderer files go in the fork (the patched renderer loads the smoke shader's bundle from there).</summary>
    public const string ForkFilesDir = @"Renderite.Renderer_Data\StreamingAssets\Softbody";

    public static Paths ForDev(string baseDir, string game, string steam) => new()
    {
        Base = baseDir, Game = game, SteamRoot = steam,
        BuildDll = Path.Combine(baseDir, "Softbody", "bin", "Release", "net10.0", "Softbody.dll"),
        Parked = Path.Combine(baseDir, "parked_addons"),
        Fork = Path.Combine(baseDir, "RendererSoftbody"),
        ForkScript = Path.Combine(baseDir, "make_softbody_renderer.py"),
    };

    public static Paths ForPack(string packDir, PackManifest manifest, string game, string steam) => new()
    {
        Base = packDir, Game = game, SteamRoot = steam, Pack = manifest,
        PayloadDir = Path.Combine(packDir, "payload"),
        BuildDll = Path.Combine(packDir, "payload", "Softbody.dll"),
        Parked = Path.Combine(game, "SoftbodyBackups"),
        Fork = Path.Combine(game, "RendererSoftbody"),
    };

    /// <summary>A friend pack's payload next to the manager (or one folder up), else null.</summary>
    public static string FindPack(string start)
    {
        for (var d = new DirectoryInfo(start); d != null && d.FullName.Length > 3; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "payload", PackManifest.FileName))) return d.FullName;
        return null;
    }

    /// <summary>The Testresolink folder: the first folder at or above <paramref name="start"/> that holds Softbody\Softbody.csproj.</summary>
    public static string FindBase(string start)
    {
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Softbody", "Softbody.csproj"))) return d.FullName;
        return null;
    }

    /// <summary>Steam's folder (from the registry, else the default install).</summary>
    public static string FindSteam()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        var def = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(def) ? def : null;
    }

    /// <summary>Resonite's folder: in whichever Steam library has it.</summary>
    public static string FindGame(string steamRoot)
    {
        var libraries = new List<string>();
        if (steamRoot != null)
        {
            libraries.Add(steamRoot);
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    libraries.Add(Unescape(m.Groups[1].Value));
        }
        foreach (var lib in libraries)
        {
            var game = Path.Combine(lib, "steamapps", "common", "Resonite");
            if (File.Exists(Path.Combine(game, "Resonite.exe"))) return game;
        }
        return null;
    }

    internal static string Unescape(string vdf) => vdf.Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\u0001", "\\");
}

enum AddonState { NotInstalled, On, Off, OnWithStaleOff }

/// <summary>An addon Resonite loads from its Libraries folder through a -LoadAssembly launch option.</summary>
sealed class LibraryAddon
{
    public string Name, FileName, About;
    public bool Installable;   // Softbody: "Install" copies it from the build (dev) or the pack
    public string OnPath(Paths p) => Path.Combine(p.Libraries, FileName);
    public string OffPath(Paths p) => Path.Combine(p.Libraries, FileName + ".off");
}

/// <summary>
/// What the manager does, without any UI. Turning an addon off renames Libraries\X.dll to X.dll.off (and back): Resonite's
/// -LoadAssembly then finds nothing at its path and skips it without a word, so the Steam launch options never change.
/// Installing copies the build (or the pack's DLL) in, after moving the one it replaces to the backups folder and logging it,
/// the way every install so far was done. The renderer fork is made by copying the installed Resonite renderer and swapping
/// in the patched Renderite.Unity.dll, when that was built for exactly this renderer.
/// </summary>
static class Manager
{
    public const string ResoniteAppId = "2519830";
    internal const string ForkMarker = "softbody_fork.txt";
    static readonly HashSet<string> ForkSkip = new(StringComparer.OrdinalIgnoreCase)
        { "BepInEx", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "uDesktopDuplication.log" };

    public static readonly LibraryAddon Softbody = new()
    {
        Name = "Softbody plugin", FileName = "Softbody.dll", Installable = true,
        About = "SkinnedSoftbody, Softbody, FluidEmitter. Off: you can join public sessions (your data model matches everyone's).",
    };
    public static readonly LibraryAddon ModLoader = new()
    {
        Name = "ResoniteModLoader", FileName = "ResoniteModLoader.dll",
        About = "RML and every mod in rml_mods. Off: plain Resonite.",
    };

    // ------------------------------------------------------------------ state

    /// <summary>Resonite's processes that are running (empty when it is closed).</summary>
    public static List<string> RunningResonite()
    {
        var found = new List<string>();
        foreach (var name in new[] { "Resonite", "Renderite.Renderer" })
        {
            try { foreach (var p in Process.GetProcessesByName(name)) { found.Add(name); p.Dispose(); } }
            catch { }
        }
        return found.Distinct().ToList();
    }

    public static AddonState StateOf(Paths p, LibraryAddon a)
    {
        bool on = File.Exists(a.OnPath(p)), off = File.Exists(a.OffPath(p));
        return on && off ? AddonState.OnWithStaleOff : on ? AddonState.On : off ? AddonState.Off : AddonState.NotInstalled;
    }

    /// <summary>
    /// A DLL's version, from its file-version resource: Resonite's weaver stamps the weave time over the assembly version
    /// of Libraries\Softbody.dll on its first start (0.18.0 reads as 2026.11.11.192 there), but leaves the file version
    /// alone. The assembly version only when there is no file version. Null if it is neither.
    /// </summary>
    public static Version VersionOf(string dll)
    {
        try
        {
            if (dll == null || !File.Exists(dll)) return null;
            var info = FileVersionInfo.GetVersionInfo(dll);
            var text = (info.ProductVersion ?? info.FileVersion)?.Split('+')[0].Trim();
            if (Version.TryParse(text, out var v)) return v;
            return AssemblyName.GetAssemblyName(dll).Version;
        }
        catch { return null; }
    }

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string Short(Version v) => v == null ? "?" : v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    // ------------------------------------------------------------------ on / off

    /// <summary>Turns an addon off (Resonite must be closed). Returns what was done.</summary>
    public static string TurnOff(Paths p, LibraryAddon a)
    {
        RequireClosed();
        var state = StateOf(p, a);
        if (state == AddonState.Off) return $"{a.Name} is already off.";
        if (state == AddonState.NotInstalled) return $"{a.Name} is not installed ({a.OnPath(p)} does not exist).";
        string note = "";
        if (state == AddonState.OnWithStaleOff) note = " " + Park(p, a.OffPath(p), a.FileName, "an older switched-off copy");
        Retry(() => File.Move(a.OnPath(p), a.OffPath(p)));
        return $"{a.Name} is off: {a.FileName} renamed to {a.FileName}.off.{note}";
    }

    /// <summary>Turns an addon on (Resonite must be closed). Returns what was done.</summary>
    public static string TurnOn(Paths p, LibraryAddon a)
    {
        RequireClosed();
        var state = StateOf(p, a);
        if (state == AddonState.On) return $"{a.Name} is already on.";
        if (state == AddonState.NotInstalled) return $"{a.Name} is not installed" + (a.Installable ? ": use Install." : ".");
        if (state == AddonState.OnWithStaleOff)
            return $"{a.Name} is on. " + Park(p, a.OffPath(p), a.FileName, "an older switched-off copy");
        Retry(() => File.Move(a.OffPath(p), a.OnPath(p)));
        return $"{a.Name} is on: {a.FileName}.off renamed back to {a.FileName}.";
    }

    // ------------------------------------------------------------------ install

    /// <summary>
    /// Installs <paramref name="source"/> as the addon and turns it on (Resonite must be closed). Whatever it replaces (on or
    /// switched off) is moved to the backups' Libraries folder as X.dll.&lt;version&gt;.bak, and the install is logged in the
    /// backups' README.txt. Returns what was done.
    /// </summary>
    public static string Install(Paths p, LibraryAddon a, string source, DateTime now)
    {
        RequireClosed();
        if (!File.Exists(source)) throw new InvalidOperationException($"there is nothing to install at {source}" + (p.IsPack ? "" : ": build the Softbody project first"));
        var newVersion = VersionOf(source) ?? throw new InvalidOperationException($"{source} is not a .NET assembly");
        if (p.IsPack && p.Pack.SoftbodySha256 != null && !string.Equals(Sha256(source), p.Pack.SoftbodySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the pack's Softbody.dll does not match its manifest (damaged download?)");
        Directory.CreateDirectory(p.Libraries);
        var notes = new List<string>();
        string replaced = null;
        if (File.Exists(a.OnPath(p))) { replaced = Short(VersionOf(a.OnPath(p))); notes.Add(Park(p, a.OnPath(p), a.FileName, "the one it replaces")); }
        if (File.Exists(a.OffPath(p))) { replaced ??= Short(VersionOf(a.OffPath(p))); notes.Add(Park(p, a.OffPath(p), a.FileName, "the switched-off one it replaces")); }
        File.Copy(source, a.OnPath(p), overwrite: false);
        var attrs = File.GetAttributes(a.OnPath(p));
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(a.OnPath(p), attrs & ~FileAttributes.ReadOnly); // the engine rewrites it on its first start
        string sha = Sha256(a.OnPath(p));
        if (sha != Sha256(source)) throw new IOException("the installed copy does not match its source (sha256 differs)");
        if (!p.IsPack)
        {
            // an untouched copy of what was installed (the engine rewrites the one in Libraries on its first start), so a
            // friend pack can carry exactly this build later (make_friend_pack.py)
            Directory.CreateDirectory(p.ParkedLibraries);
            File.Copy(source, Path.Combine(p.ParkedLibraries, $"{a.FileName}.{Short(newVersion)}.installed"), overwrite: true);
        }
        string from = p.IsPack ? "from a friend pack" : "with the Softbody manager";
        string log = $"{now:yyyy-MM-dd HH:mm} {Short(newVersion)} installed into Libraries\\{a.FileName} {from} (sha256 {sha[..8]}..., " +
                     "before the first-start weave" + (p.Pack?.CompatibilityHash != null ? $"; compatibility hash {p.Pack.CompatibilityHash}" : "") + ")" +
                     (replaced != null ? $", replacing {replaced}." : ".");
        if (notes.Count > 0) log += " " + string.Join(" ", notes);
        AppendLog(p, log);
        return $"Installed {a.Name} {Short(newVersion)} (sha256 {sha[..8]}...). " + string.Join(" ", notes);
    }

    /// <summary>Moves a file to the backups' Libraries folder as Name.&lt;version&gt;.bak (with a time if that name is taken).</summary>
    static string Park(Paths p, string file, string name, string what)
    {
        Directory.CreateDirectory(p.ParkedLibraries);
        var v = VersionOf(file);
        string baseName = $"{name}.{(v != null ? Short(v) : "unknown")}";
        string target = Path.Combine(p.ParkedLibraries, baseName + ".bak");
        if (File.Exists(target)) target = Path.Combine(p.ParkedLibraries, $"{baseName}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        for (int k = 2; File.Exists(target); k++) target = Path.Combine(p.ParkedLibraries, $"{baseName}.{DateTime.Now:yyyyMMdd-HHmmss}-{k}.bak");
        Retry(() => File.Move(file, target));
        string shown = Path.Combine(Path.GetFileName(p.Parked), "Libraries", Path.GetFileName(target));
        return $"{char.ToUpper(what[0]) + what[1..]} ({(v != null ? Short(v) : "unknown version")}) is kept as {shown}.";
    }

    internal static void AppendLog(Paths p, string line)
    {
        Directory.CreateDirectory(p.Parked);
        File.AppendAllText(p.ParkedLog, line + Environment.NewLine, new UTF8Encoding(false));
    }

    /// <summary>
    /// 1.1.0: runs a file step again for a few seconds when Windows refuses it at first. Right after files are written, a virus
    /// scanner or the search indexer often holds them open for a moment, and moving or deleting them (or their folder) then
    /// fails with "access denied": the fork's folder rename failed that way about one time in seven in the tests.
    /// </summary>
    internal static void Retry(Action step)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { step(); return; }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 12)
            {
                Thread.Sleep(Math.Min(100 << Math.Min(attempt, 4), 1000));
            }
        }
    }

    /// <summary>Only for the offline tests, which work on a fake game folder while the real Resonite may be running.</summary>
    public static bool IgnoreRunningForTests { get; set; }

    internal static void RequireClosed()
    {
        if (IgnoreRunningForTests) return;
        var running = RunningResonite();
        if (running.Count > 0) throw new InvalidOperationException("Resonite is running (" + string.Join(", ", running) + "): close it first, its files are in use");
    }

    // ------------------------------------------------------------------ the renderer fork

    /// <summary>The Renderite commit a Renderite.Unity.dll was built from (its informational version), or null.</summary>
    public static string CommitOf(string dll)
    {
        try
        {
            if (dll == null || !File.Exists(dll)) return null;
            var m = Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(dll)), @"1\.0\.0\+([0-9a-f]{40})");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static string OfficialCommit(Paths p) => CommitOf(Path.Combine(p.Game, "Renderer", Paths.RendererDll));

    static string Ten(string commit) => commit == null ? "?" : commit[..Math.Min(10, commit.Length)];

    /// <summary>Whether the fork still matches the installed renderer (a Resonite update needs a new fork), and why. 1.2.0: a
    /// pack's fork also needs the renderer files the pack lists (0.27.0: the smoke shader).</summary>
    public static (bool ok, string text) ForkStatus(Paths p)
    {
        var (ok, text, _) = ForkCheck(p);
        return (ok, text);
    }

    /// <summary>1.2.0: the renderer files the fork lacks (or has another version of), when that is all it lacks: it matches the
    /// installed renderer and has the Softbody patch. Repair puts them in (UpdateForkFiles). Empty otherwise.</summary>
    public static List<string> RepairNeeded(Paths p) => ForkCheck(p).lacks;

    static (bool ok, string text, List<string> lacks) ForkCheck(Paths p)
    {
        var none = new List<string>();
        var official = OfficialCommit(p);
        var forkDll = Path.Combine(p.Fork, Paths.RendererDll);
        if (official == null) return (false, "could not read the installed renderer's version", none);
        if (!File.Exists(forkDll))
            return (false, p.IsPack ? "not set up yet (setup step 2)" : "no fork at " + p.Fork + " (build it with Rebuild fork)", none);
        var fork = CommitOf(forkDll);
        bool patched = Contains(forkDll, "Softbody");
        if (fork != official)
            return (false, $"out of date: Resonite's renderer is {Ten(official)}, the fork {Ten(fork)}. " +
                           (p.IsPack ? "Your Resonite updated: take -RendererPath out of the launch options until Lenki puts out an update for it, then press Update (a mismatched renderer may not start)"
                                     : "Rebuild it before starting Resonite with -RendererPath (a mismatched renderer may not start)"), none);
        if (!patched) return (false, "matches, but the Softbody patch is missing: set it up again", none);
        string fine = $"matches Resonite's renderer ({Ten(official)}), Softbody patch in";
        if (!p.IsPack) return (true, fine, none);
        if (p.Pack.ForkFilesProblem() is { } bad) return (false, $"the pack's manifest {bad}: get the pack again from Lenki", none);
        var lacks = ForkLacks(p);
        if (lacks.Count > 0) return (false, $"{fine}, but it lacks {Describe(lacks)}: press Repair", lacks);
        var listed = ListedForkFiles(p).Select(f => f.Name).ToList();
        return (true, listed.Count > 0 ? $"{fine}, with {Describe(listed)}" : fine, none);
    }

    /// <summary>A pack: whether its patched renderer was built for this Resonite (null: yes; else why not).</summary>
    public static string PackRendererMismatch(Paths p)
    {
        if (!p.IsPack) return "not a pack";
        var official = OfficialCommit(p);
        if (official == null) return "could not read your Resonite renderer's version";
        if (!File.Exists(p.PackRendererDll)) return "the pack has no Renderite.Unity.dll";
        var packCommit = CommitOf(p.PackRendererDll);
        if (packCommit != official || (p.Pack.RenderiteCommit != null && p.Pack.RenderiteCommit != official))
            return $"this pack was made for another Resonite version (renderer {Ten(packCommit ?? p.Pack.RenderiteCommit)}, yours is {Ten(official)}): " +
                   "wait for Lenki's update for your Resonite version, then press Update";
        if (p.Pack.RendererDllSha256 != null && !string.Equals(Sha256(p.PackRendererDll), p.Pack.RendererDllSha256, StringComparison.OrdinalIgnoreCase))
            return "the pack's Renderite.Unity.dll does not match its manifest (damaged download?)";
        if (p.Pack.ForkFilesProblem() is { } bad) return $"the pack's manifest {bad} (damaged download?): get the pack again from Lenki";
        return null;
    }

    /// <summary>
    /// A pack: makes the renderer fork (Resonite must be closed). Copies the installed renderer (Resonite\Renderer, without
    /// BepInEx or Doorstop files) to a new folder, swaps in the pack's patched Renderite.Unity.dll and marks it as ours, then
    /// puts it at Resonite\RendererSoftbody, replacing an older fork of ours (never a folder we did not make). Only when the
    /// patched DLL was built for exactly this renderer. 1.2.0: the pack's renderer files go in too (0.27.0: the smoke shader);
    /// one the pack lacks is left out and said (Repair puts it in later). Returns what was done.
    /// </summary>
    public static string SetUpFork(Paths p, Action<string> progress)
    {
        RequireClosed();
        var why = PackRendererMismatch(p);
        if (why != null) throw new InvalidOperationException(why);
        var official = Path.Combine(p.Game, "Renderer");
        if (Directory.Exists(p.Fork) && !File.Exists(Path.Combine(p.Fork, ForkMarker)))
            throw new InvalidOperationException($"{p.Fork} exists but was not made by this manager: move it away first");
        var fresh = p.Fork + ".new";
        if (Directory.Exists(fresh)) Retry(() => Directory.Delete(fresh, true));   // a set-up that stopped half way (ours: the name says so)
        long bytes = 0; int files = 0;
        CopyTree(official, fresh, ref bytes, ref files, progress);
        File.Copy(p.PackRendererDll, Path.Combine(fresh, Paths.RendererDll), overwrite: true);
        if (!string.Equals(Sha256(Path.Combine(fresh, Paths.RendererDll)), Sha256(p.PackRendererDll), StringComparison.OrdinalIgnoreCase))
            throw new IOException("the patched renderer did not copy correctly");
        var (put, lacking) = CopyForkFiles(p, fresh);
        File.WriteAllText(Path.Combine(fresh, ForkMarker),
            $"Made by the Softbody manager from {official} on {DateTime.Now:yyyy-MM-dd HH:mm}, with the Softbody-patched Renderite.Unity.dll " +
            $"(Renderite commit {OfficialCommit(p)}, MIT, see the pack's LICENSE-Renderite.txt). Safe to delete; the manager can make it again.\r\n");
        if (Directory.Exists(p.Fork)) Retry(() => Directory.Delete(p.Fork, true));  // an older fork of ours (it has the marker)
        Retry(() => Directory.Move(fresh, p.Fork));
        AppendLog(p, $"{DateTime.Now:yyyy-MM-dd HH:mm} renderer fork made at {p.Fork} from a friend pack ({files} files, {bytes / 1048576.0:0} MB, Renderite {Ten(OfficialCommit(p))}" +
                     (put.Count > 0 ? $"; with {string.Join(", ", put)}" : "") + (lacking.Count > 0 ? $"; without {string.Join(", ", lacking)}, which the pack lacks" : "") + ").");
        return $"The renderer fork is set up at {p.Fork} ({files} files, {bytes / 1048576.0:0} MB)" + (put.Count > 0 ? $", with {Describe(put)}." : ".") +
               (lacking.Count > 0 ? $" It lacks {Describe(lacking)}: the pack does not have {(lacking.Count == 1 ? "it" : "them")} yet (Repair gets {(lacking.Count == 1 ? "it" : "them")})." : "");
    }

    // ------------------------------------------------------------------ 1.2.0: the fork's renderer files (0.27.0: the smoke shader)

    /// <summary>Renderer files in words (0.27.0's one is the smoke shader).</summary>
    public static string Describe(IReadOnlyList<string> names) =>
        names.Count == 1 && names[0].Contains("smoke", StringComparison.OrdinalIgnoreCase) ? $"the smoke shader ({names[0]})"
        : (names.Count == 1 ? "the renderer file " : "the renderer files ") + string.Join(", ", names);

    /// <summary>The pack's renderer files (none when the list is damaged: ForkStatus and PackRendererMismatch say so).</summary>
    static List<ForkFile> ListedForkFiles(Paths p) =>
        p.Pack?.ForkFiles == null || p.Pack.ForkFilesProblem() != null ? new List<ForkFile>() : p.Pack.ForkFiles.ToList();

    /// <summary>For the window (it asks every two seconds): whether a file is there with this checksum; one the running renderer
    /// holds open is taken as there.</summary>
    static bool Matches(string file, string sha)
    {
        try { return File.Exists(file) && Same(Sha256(file), sha); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The renderer files the pack's manifest lists that its payload lacks (or has damaged). A manager before 1.2.0 that applied
    /// an update with them did not know them, so it did not download them: the manager it swapped in gets them on its first start
    /// (Updater.FetchPackFilesAsync).
    /// </summary>
    public static List<string> PackLacks(Paths p) => ListedForkFiles(p).Where(f => !Matches(p.PackFile(f.Name), f.Sha256)).Select(f => f.Name).ToList();

    /// <summary>The renderer files the pack lists that the fork lacks (or has another version of).</summary>
    static List<string> ForkLacks(Paths p) =>
        ListedForkFiles(p).Where(f => !Matches(Path.Combine(p.Fork, Paths.ForkFilesDir, f.Name), f.Sha256)).Select(f => f.Name).ToList();

    /// <summary>Copies the pack's renderer files into a fork being made (at <paramref name="forkRoot"/>): the ones it put in, and
    /// the ones the pack lacks.</summary>
    static (List<string> put, List<string> lacking) CopyForkFiles(Paths p, string forkRoot)
    {
        List<string> put = new(), lacking = new();
        foreach (var f in ListedForkFiles(p))
        {
            var source = p.PackFile(f.Name);
            if (!File.Exists(source) || !Same(Sha256(source), f.Sha256)) { lacking.Add(f.Name); continue; }
            var target = Path.Combine(forkRoot, Paths.ForkFilesDir, f.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, overwrite: true);
            if (!Same(Sha256(target), f.Sha256)) throw new IOException($"{f.Name} did not copy correctly");
            put.Add(f.Name);
        }
        return (put, lacking);
    }

    /// <summary>
    /// Makes the fork's renderer files (Renderite.Renderer_Data\StreamingAssets\Softbody) the pack's (Resonite must be closed):
    /// puts in each one the pack lists (the version it replaces is kept in the backups' Renderer folder), and moves the ones the
    /// pack does not list there too: they came with another release (going back to a release without the smoke shader takes it
    /// out). Only in the manager's own fork, when it matches the installed renderer. Returns what was done, or null when there was
    /// nothing to do.
    /// </summary>
    public static string UpdateForkFiles(Paths p)
    {
        RequireClosed();
        if (!p.IsPack) throw new InvalidOperationException("renderer files are for friend packs");
        if (p.Pack.ForkFilesProblem() is { } bad) throw new InvalidOperationException($"the pack's manifest {bad} (damaged download?): get the pack again from Lenki");
        if (!File.Exists(Path.Combine(p.Fork, ForkMarker))) throw new InvalidOperationException($"{p.Fork} was not made by this manager: left as it is");
        var dll = Path.Combine(p.Fork, Paths.RendererDll);
        if (!File.Exists(dll) || CommitOf(dll) != OfficialCommit(p)) throw new InvalidOperationException("the renderer fork is out of date: set it up again");
        var dir = Path.Combine(p.Fork, Paths.ForkFilesDir);
        var listed = ListedForkFiles(p);
        List<string> put = new(), replaced = new(), lacking = new(), taken = new();
        foreach (var f in listed)
        {
            string source = p.PackFile(f.Name), target = Path.Combine(dir, f.Name);
            if (File.Exists(target) && Same(Sha256(target), f.Sha256)) continue;
            if (!File.Exists(source) || !Same(Sha256(source), f.Sha256)) { lacking.Add(f.Name); continue; }
            Directory.CreateDirectory(dir);
            if (File.Exists(target)) replaced.Add(KeepForkFile(p, target, move: false));
            Retry(() => File.Copy(source, target, overwrite: true));
            if (!Same(Sha256(target), f.Sha256)) throw new IOException($"{f.Name} did not copy correctly");
            put.Add(f.Name);
        }
        var kept = new List<string>();
        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (listed.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                kept.Add(KeepForkFile(p, file, move: true));
                taken.Add(name);
            }
        if (put.Count + taken.Count + lacking.Count == 0) return null;
        var said = new List<string>();
        var logged = new List<string>();
        if (put.Count > 0)
        {
            said.Add($"The renderer fork has {Describe(put)} now" + (replaced.Count > 0 ? $"; the version it replaced is kept as {string.Join(", ", replaced)}." : "."));
            logged.Add($"{string.Join(", ", put)} put in" + (replaced.Count > 0 ? $" (replaced: kept as {string.Join(", ", replaced)})" : ""));
        }
        if (taken.Count > 0)
        {
            said.Add($"{Cap(Describe(taken))} came with another release: taken out of the renderer fork, kept as {string.Join(", ", kept)}.");
            logged.Add($"{string.Join(", ", taken)} taken out (not in this release; kept as {string.Join(", ", kept)})");
        }
        if (lacking.Count > 0)
        {
            said.Add($"The renderer fork still lacks {Describe(lacking)}: the pack does not have {(lacking.Count == 1 ? "it" : "them")} yet (Repair gets {(lacking.Count == 1 ? "it" : "them")}).");
            logged.Add($"{string.Join(", ", lacking)} not put in (the pack lacks {(lacking.Count == 1 ? "it" : "them")})");
        }
        AppendLog(p, $"{DateTime.Now:yyyy-MM-dd HH:mm} renderer fork's files ({Paths.ForkFilesDir}): {string.Join("; ", logged)}.");
        return string.Join(" ", said);
    }

    /// <summary>Keeps one of the fork's renderer files in the backups' Renderer folder as name.&lt;sha8&gt;.bak (once for each
    /// content); <paramref name="move"/>: it leaves the fork. Returns where it is kept.</summary>
    static string KeepForkFile(Paths p, string file, bool move)
    {
        var keep = Path.Combine(p.Parked, "Renderer");
        Directory.CreateDirectory(keep);
        string name = Path.GetFileName(file), sha = Sha256(file), bak;
        bool have = false;
        for (int k = 1; ; k++)
        {
            bak = Path.Combine(keep, k == 1 ? $"{name}.{sha[..8]}.bak" : $"{name}.{sha[..8]}.{k}.bak");
            if (!File.Exists(bak)) break;
            if (Same(Sha256(bak), sha)) { have = true; break; }   // this same content is kept already
        }
        var target = bak;
        if (move) { if (have) Retry(() => File.Delete(file)); else Retry(() => File.Move(file, target)); }
        else if (!have) File.Copy(file, target);
        return Path.Combine(Path.GetFileName(p.Parked), "Renderer", Path.GetFileName(target));
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>
    /// 1.1.0 (updates): puts the pack's patched Renderite.Unity.dll into the existing fork (Resonite must be closed), when the
    /// fork is the manager's and still matches the installed renderer; the DLL it replaces is kept in the backups' Renderer
    /// folder. Returns what was done.
    /// </summary>
    public static string UpdateForkDll(Paths p)
    {
        RequireClosed();
        var why = PackRendererMismatch(p);
        if (why != null) throw new InvalidOperationException(why);
        if (!File.Exists(Path.Combine(p.Fork, ForkMarker))) throw new InvalidOperationException($"{p.Fork} was not made by this manager: left as it is");
        var dll = Path.Combine(p.Fork, Paths.RendererDll);
        if (!File.Exists(dll) || CommitOf(dll) != OfficialCommit(p)) throw new InvalidOperationException("the renderer fork is out of date: set it up again");
        string oldSha = Sha256(dll), newSha = Sha256(p.PackRendererDll);
        if (string.Equals(oldSha, newSha, StringComparison.OrdinalIgnoreCase)) return $"The renderer fork has this patch already ({newSha[..8]}).";
        var keep = Path.Combine(p.Parked, "Renderer");
        Directory.CreateDirectory(keep);
        var bak = Path.Combine(keep, $"Renderite.Unity.dll.{oldSha[..8]}.bak");
        if (!File.Exists(bak)) File.Copy(dll, bak);
        Retry(() => File.Copy(p.PackRendererDll, dll, overwrite: true));
        if (!string.Equals(Sha256(dll), newSha, StringComparison.OrdinalIgnoreCase)) throw new IOException("the patched renderer did not copy correctly");
        string shown = Path.Combine(Path.GetFileName(p.Parked), "Renderer", Path.GetFileName(bak));
        AppendLog(p, $"{DateTime.Now:yyyy-MM-dd HH:mm} renderer fork: patched Renderite.Unity.dll {newSha[..8]} put in, replacing {oldSha[..8]} (kept as {shown}).");
        return $"The renderer fork has the new patch ({newSha[..8]}); the old one is kept as {shown}.";
    }

    static void CopyTree(string from, string to, ref long bytes, ref int files, Action<string> progress)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            if (ForkSkip.Contains(Path.GetFileName(f))) continue;
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            bytes += new FileInfo(f).Length;
            if (++files % 200 == 0) progress?.Invoke($"  copied {files} files ({bytes / 1048576.0:0} MB)");
        }
        foreach (var d in Directory.GetDirectories(from))
        {
            if (ForkSkip.Contains(Path.GetFileName(d))) continue;
            CopyTree(d, Path.Combine(to, Path.GetFileName(d)), ref bytes, ref files, progress);
        }
    }

    static bool Contains(string file, string text)
    {
        try { return Encoding.Latin1.GetString(File.ReadAllBytes(file)).Contains(text, StringComparison.Ordinal); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ launch options

    /// <summary>The launch options this setup needs (to paste into Steam: Resonite, Properties, Launch Options).</summary>
    public static string NeededLaunchOptions(Paths p) =>
        $"-LoadAssembly Libraries/{Softbody.FileName} -RendererPath \"{Path.Combine(p.Fork, "Renderite.Renderer.exe")}\"";

    /// <summary>Whether the current launch options already have what this setup needs.</summary>
    public static (bool plugin, bool renderer) HasNeeded(Paths p, string current)
    {
        if (current == null) return (false, false);
        bool plugin = Mentions(current, Softbody.FileName);
        var exe = Path.Combine(p.Fork, "Renderite.Renderer.exe").Replace('/', '\\');
        bool renderer = current.Replace('/', '\\').Contains(exe, StringComparison.OrdinalIgnoreCase) && current.Contains("-RendererPath", StringComparison.OrdinalIgnoreCase);
        return (plugin, renderer);
    }

    /// <summary>Resonite's Steam launch options, read from the most recently used Steam account's localconfig.vdf
    /// (never written). Null when they cannot be found.</summary>
    public static string ReadLaunchOptions(string steamRoot)
    {
        try
        {
            if (steamRoot == null) return null;
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata)) return null;
            var files = Directory.GetDirectories(userdata).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
                                 .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            foreach (var f in files)
            {
                var opts = LaunchOptionsIn(File.ReadAllText(f), ResoniteAppId);
                if (opts != null) return opts;
            }
            return files.Count > 0 ? "" : null;
        }
        catch { return null; }
    }

    /// <summary>The LaunchOptions value inside an app's block of a localconfig.vdf text (null if absent).</summary>
    public static string LaunchOptionsIn(string vdf, string appId)
    {
        foreach (Match start in Regex.Matches(vdf, "\"" + appId + "\"\\s*\\{"))
        {
            int depth = 0, i = start.Index + start.Length - 1;
            int end = -1;
            bool inString = false;
            for (; i < vdf.Length; i++)
            {
                char c = vdf[i];
                if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { end = i; break; }
            }
            if (end < 0) continue;
            var block = vdf.Substring(start.Index, end - start.Index);
            var m = Regex.Match(block, "\"LaunchOptions\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return Paths.Unescape(m.Groups[1].Value);
        }
        return null;
    }

    /// <summary>Whether the launch options load an addon (it only loads if they point at it).</summary>
    public static bool Mentions(string launchOptions, string fileName) =>
        launchOptions != null && launchOptions.Replace('\\', '/').Contains("Libraries/" + fileName, StringComparison.OrdinalIgnoreCase);
}
