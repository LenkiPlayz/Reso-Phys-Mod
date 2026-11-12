using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoftbodyManager;

enum UpdateState { NotSetUp, Failed, UpToDate, Available, OtherResonite }

/// <summary>
/// 1.2.0: which of Lenki's builds a pack takes. Stable: the ones Lenki tested (GitHub's latest release). Unstable: the newest
/// too (pre-releases on GitHub: not tested yet, they might break). Kept next to the pack (update-channel.txt); stable when
/// there is none.
/// </summary>
enum Channel { Stable, Unstable }

/// <summary>1.1.0: what an update check found: one release, judged against the pack.</summary>
sealed class UpdateCheck
{
    public UpdateState State;
    /// <summary>The release's manifest (signed by Lenki), when it could be read.</summary>
    public PackManifest Remote;
    /// <summary>Its text, exactly as signed (it becomes the pack's payload\manifest.json).</summary>
    public string RemoteText;
    /// <summary>For the window.</summary>
    public string Text;
    /// <summary>Whether the release has a newer manager than the running one.</summary>
    public bool NewManager;
    /// <summary>1.2.0: an unstable build: the newest release, newer than the stable one (a pre-release on GitHub).</summary>
    public bool Unstable;
    /// <summary>1.2.0: the release is older than the pack, so taking it goes back (from an unstable build to the stable one).</summary>
    public bool Older;
    /// <summary>1.2.0: the release is made for another Resonite renderer than the friend's (whatever its State says).</summary>
    public bool OtherRenderer;
}

/// <summary>1.2.0: what GitHub has for a pack: Lenki's stable release, and the newest release when that is newer (an unstable build).</summary>
sealed class UpdateStatus
{
    public UpdateCheck Stable;
    /// <summary>The newest release, when it is newer than the stable one; null when there is none (or it could not be checked).</summary>
    public UpdateCheck Unstable;
    /// <summary>Why the unstable builds could not be checked; null when they could.</summary>
    public string UnstableProblem;
}

/// <summary>1.2.0: what the window offers, from what GitHub has and the pack's channel.</summary>
sealed class UpdateOffer
{
    /// <summary>The release the Update button takes (the banner), or null.</summary>
    public UpdateCheck Main;
    /// <summary>On the stable channel: an unstable build newer than the pack (Try unstable), or null.</summary>
    public UpdateCheck TryUnstable;
    /// <summary>The stable release, when the pack is newer than it (Back to stable goes back to it), or null.</summary>
    public UpdateCheck BackTo;
    /// <summary>The pack takes unstable builds.</summary>
    public bool OnUnstable;
}

/// <summary>1.2.0: where a pack's updates come from: the releases' downloads, and GitHub's list of the releases (the only place
/// that has the pre-releases, the unstable builds).</summary>
sealed record UpdateSource(string Releases, string Api)
{
    public static UpdateSource For(string repo)
    {
        var releases = Updater.ReleasesUrl(repo);
        return releases == null ? null : new UpdateSource(releases, $"https://api.github.com/repos/{repo}/releases?per_page=30");
    }
}

/// <summary>
/// 1.1.0: updates from GitHub. Lenki publishes each version as a release of a public repository (publish_release.py): its
/// update.json (the manifest, signed) says what is in it, and its other files are the plugin, the patched renderer DLL, the
/// manager and the whole friend pack. The manager reads the latest release's update.json when it opens. Update downloads the
/// files into payload.new and checks each against the signed manifest, then swaps the payload, installs Softbody (on or off,
/// as it was; the old one goes to the backups, as on every install) and gives the renderer fork the new patch.
/// 1.2.0: stable and unstable builds. GitHub's latest release is the stable one (a pre-release never is); the newest release of
/// all, when it is newer, is the unstable one, found in GitHub's list of releases. A pack on the stable channel is offered the
/// stable release (and can try the unstable one), a pack on the unstable channel the newest; either can go back to the stable
/// release when it is newer than that (an older Softbody is installed the same way a newer one is).
/// </summary>
static class Updater
{
    public const string ManagerFile = "SoftbodyManager.exe";
    /// <summary>1.2.0: the pack's channel, next to it ("unstable"; anything else, or no file: stable).</summary>
    public const string ChannelFile = "update-channel.txt";
    const long MaxManifest = 1L << 20, MaxList = 4L << 20, MaxDll = 64L << 20, MaxManager = 400L << 20;
    static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$"),
                          TagPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"),
                          ShaPattern = new("^[0-9a-fA-F]{64}$"), CommitPattern = new("^[0-9a-f]{40}$"),
                          // 1.2.0: a tag's version (v0.27.0) and repack number (v0.27.0-r2), to tell which release is newer
                          TagVersion = new(@"^v?(\d{1,9}(?:\.\d{1,9}){1,3})(?:-r(\d{1,6}))?"), TagRevision = new(@"-r(\d{1,6})$");

    /// <summary>The User-Agent GitHub asks for: the program and its version, nothing about the person.</summary>
    public static string Agent(Version manager) => "SoftbodyManager/" + (manager == null ? "1" : manager.ToString(3));

    /// <summary>A repository's releases on GitHub (owner/name), or null when there is none or it is malformed.</summary>
    public static string ReleasesUrl(string repo) => repo != null && RepoPattern.IsMatch(repo) ? $"https://github.com/{repo}/releases" : null;

    /// <summary>The latest release's update.json (GitHub sends this address on to the newest release's file).</summary>
    public static string LatestUrl(string releases) => $"{releases}/latest/download/{SignedUpdate.FileName}";

    public static string AssetUrl(string releases, string tag, string name) => $"{releases}/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    // ------------------------------------------------------------------ 1.2.0: the channel

    public static Channel LoadChannel(string packDir)
    {
        try
        {
            var f = Path.Combine(packDir, ChannelFile);
            return File.Exists(f) && File.ReadAllText(f).Trim().Equals("unstable", StringComparison.OrdinalIgnoreCase) ? Channel.Unstable : Channel.Stable;
        }
        catch { return Channel.Stable; }
    }

    public static void SaveChannel(string packDir, Channel channel) =>
        Manager.Retry(() => File.WriteAllText(Path.Combine(packDir, ChannelFile), channel == Channel.Unstable ? "unstable" : "stable"));

    // ------------------------------------------------------------------ checks

    /// <summary>1.2.0: reads the stable release and the newest one (when it is newer: an unstable build) and judges both.</summary>
    public static async Task<UpdateStatus> CheckAllAsync(HttpClient http, UpdateSource source, string publicKey, PackManifest local, string officialCommit,
                                                         Version manager, CancellationToken ct)
    {
        var status = new UpdateStatus { Stable = await CheckAsync(http, source?.Releases, publicKey, local, officialCommit, manager, ct) };
        if (status.Stable.State == UpdateState.NotSetUp) return status;
        try
        {
            var newest = Newest(await GetText(http, source.Api, MaxList, ct, api: true));
            var stable = status.Stable.Remote;
            if (newest == null || newest.Tag == stable?.Tag) return status;
            // only a release newer than the stable one is an unstable build (going by its tag; its signed manifest decides the rest)
            if (stable != null && Compare(TagRank(newest.Tag).Value, RankOf(stable)) <= 0) return status;
            string json;
            try { json = await GetText(http, AssetUrl(source.Releases, newest.Tag, SignedUpdate.FileName), MaxManifest, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { status.UnstableProblem = $"{newest.Tag} has no {SignedUpdate.FileName}"; return status; }
            var check = Judge(json, publicKey, local, officialCommit, manager, unstable: true);
            // a release whose signed manifest is another release's (an old update.json put up again under a new tag): not taken
            if (check.Remote != null && check.Remote.Tag != newest.Tag)
                check = Failed($"The unstable build {newest.Tag} carries the manifest of {check.Remote.Tag}: not taking it.");
            status.Unstable = check;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status.UnstableProblem = "GitHub did not answer in time"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            status.UnstableProblem = ex.Message;
        }
        return status;
    }

    /// <summary>Reads the latest (stable) release and decides whether it is an update for this pack.</summary>
    public static async Task<UpdateCheck> CheckAsync(HttpClient http, string releases, string publicKey, PackManifest local, string officialCommit,
                                                      Version manager, CancellationToken ct)
    {
        if (releases == null || publicKey == null)
            return new UpdateCheck { State = UpdateState.NotSetUp, Text = "This manager has no update source built in: new versions come as packs from Lenki." };
        string json;
        try { json = await GetText(http, LatestUrl(releases), MaxManifest, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return Failed("Lenki has not published a release with updates yet."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failed("Could not check for updates: GitHub did not answer in time."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { return Failed("Could not check for updates: " + ex.Message); }
        return Judge(json, publicKey, local, officialCommit, manager);
    }

    /// <summary>The decision, from a release's update.json text (<paramref name="unstable"/>: the newest release, newer than the stable one).</summary>
    public static UpdateCheck Judge(string updateJson, string publicKey, PackManifest local, string officialCommit, Version manager, bool unstable = false)
    {
        string what = unstable ? "the unstable build" : "the latest release";
        SignedUpdate signed;
        try { signed = SignedUpdate.FromJson(updateJson); } catch { signed = null; }
        if (signed == null) return Failed($"{Capital(what)}'s update.json is not readable: not taking it.");
        if (!signed.IsSignedBy(publicKey)) return Failed($"{Capital(what)} is not signed with Lenki's key: not taking anything from it.");
        PackManifest remote;
        try { remote = PackManifest.Parse(signed.Manifest); } catch { remote = null; }
        var problem = Problem(remote);
        if (problem != null) return Failed($"{Capital(what)}'s manifest {problem}: not taking it.");

        var check = new UpdateCheck { Remote = remote, RemoteText = signed.Manifest, Unstable = unstable };
        check.NewManager = remote.ManagerSha256 != null && manager != null && Version.TryParse(remote.ManagerVersion, out var mv) && Norm(mv) > Norm(manager);
        check.OtherRenderer = officialCommit != null && !string.Equals(remote.RenderiteCommit, officialCommit, StringComparison.OrdinalIgnoreCase);
        bool localKnown = local != null && Version.TryParse(local.SoftbodyVersion, out _);
        if (local != null && remote.Tag == local.Tag)
        {
            check.State = UpdateState.UpToDate;
            check.Text = unstable ? $"You have Lenki's newest build ({remote.Tag}: Softbody {remote.SoftbodyVersion}, an unstable build)."
                                  : $"You have Lenki's latest stable build ({remote.Tag}: Softbody {remote.SoftbodyVersion}).";
        }
        else if (localKnown && Compare(RankOf(remote), RankOf(local)) < 0)
        {
            check.State = UpdateState.UpToDate;
            check.Older = true;
            check.Text = unstable ? $"Your pack (Softbody {local.SoftbodyVersion}) is newer than Lenki's newest build ({remote.Tag})."
                                  : $"Your pack (Softbody {local.SoftbodyVersion}) is newer than Lenki's stable build ({remote.Tag}).";
        }
        else if (check.OtherRenderer)
        {
            check.State = UpdateState.OtherResonite;
            check.Text = $"{(unstable ? "The unstable build " : "")}Softbody {remote.SoftbodyVersion} is out, but made for another Resonite version (renderer " +
                         $"{Ten(remote.RenderiteCommit)}; yours is {Ten(officialCommit)}). If Steam has a Resonite update waiting, let it update; " +
                         "otherwise wait for Lenki's next release.";
        }
        else
        {
            check.State = UpdateState.Available;
            check.Text = $"{(unstable ? "The unstable build " : "")}Softbody {remote.SoftbodyVersion} is out ({remote.Tag}; you have {local?.SoftbodyVersion ?? "?"})." +
                         (unstable ? " It is not tested yet." : "") +
                         (string.IsNullOrWhiteSpace(remote.Notes) ? "" : " New: " + remote.Notes.Trim());
        }
        return check;
    }

    /// <summary>
    /// 1.2.0: what the window offers. On the stable channel: the stable release (Update), and an unstable build newer than the
    /// pack (Try unstable). On the unstable channel: the newest release (Update). On either: when the pack is newer than the
    /// stable release (an unstable build, or one Lenki took back out of stable), going back to the stable release (Back to stable).
    /// </summary>
    public static UpdateOffer Offer(UpdateStatus s, Channel channel)
    {
        var offer = new UpdateOffer { OnUnstable = channel == Channel.Unstable };
        if (s == null) return offer;
        UpdateCheck stable = s.Stable, unstable = s.Unstable;
        static UpdateCheck In(UpdateCheck c, UpdateState state) => c?.State == state ? c : null;
        offer.Main = channel == Channel.Unstable
            ? In(unstable, UpdateState.Available) ?? In(stable, UpdateState.Available) ?? In(unstable, UpdateState.OtherResonite) ?? In(stable, UpdateState.OtherResonite)
            : In(stable, UpdateState.Available) ?? In(stable, UpdateState.OtherResonite);
        if (stable?.Older == true) offer.BackTo = stable;
        else if (channel == Channel.Stable) offer.TryUnstable = In(unstable, UpdateState.Available);
        return offer;
    }

    /// <summary>1.2.0: GitHub's list of releases (its API's JSON): the newest one with an update.json, pre-releases included
    /// (drafts are not); null when there is none. Newest: the highest version in the tag (v0.27.0-r2 after v0.27.0), then the
    /// latest published.</summary>
    public static ReleaseInfo Newest(string releasesJson)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub's list of releases is not a list");
        ReleaseInfo best = null;
        (Version, int) bestRank = default;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            if (r.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String) continue;
            var tag = t.GetString();
            if (!TagPattern.IsMatch(tag) || TagRank(tag) is not { } rank) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
                !assets.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) &&
                                                  n.ValueKind == JsonValueKind.String && n.GetString() == SignedUpdate.FileName)) continue;
            bool pre = r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            var published = r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(pa.GetString(), out var when) ? when : DateTimeOffset.MinValue;
            int order = best == null ? 1 : Compare(rank, bestRank);
            if (order > 0 || (order == 0 && published > best.Published)) { best = new ReleaseInfo(tag, pre, published); bestRank = rank; }
        }
        return best;
    }

    /// <summary>A tag's version and repack number (v0.27.0-r2: 0.27.0, 2); null when it has no version.</summary>
    static (Version, int)? TagRank(string tag)
    {
        var m = tag == null ? null : TagVersion.Match(tag);
        if (m is not { Success: true } || !Version.TryParse(m.Groups[1].Value, out var v)) return null;
        return (Norm(v), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0);
    }

    /// <summary>A pack's or release's place in line: its Softbody version, then its tag's repack number.</summary>
    static (Version, int) RankOf(PackManifest m)
    {
        var r = m.Tag == null ? null : TagRevision.Match(m.Tag);
        return (Norm(Version.Parse(m.SoftbodyVersion)), r is { Success: true } ? int.Parse(r.Groups[1].Value) : 0);
    }

    static int Compare((Version v, int r) a, (Version v, int r) b) => a.v != b.v ? a.v.CompareTo(b.v) : a.r.CompareTo(b.r);

    /// <summary>What is wrong with a release's manifest (null: nothing).</summary>
    static string Problem(PackManifest m)
    {
        if (m == null) return "is not readable";
        if (m.Tag == null || !TagPattern.IsMatch(m.Tag)) return "has no usable tag";
        if (!Version.TryParse(m.SoftbodyVersion, out _)) return "has no Softbody version";
        if (m.SoftbodySha256 == null || !ShaPattern.IsMatch(m.SoftbodySha256) || m.RendererDllSha256 == null || !ShaPattern.IsMatch(m.RendererDllSha256))
            return "lacks the files' checksums";
        if (m.RenderiteCommit == null || !CommitPattern.IsMatch(m.RenderiteCommit)) return "does not say which Resonite renderer it is for";
        if (m.ManagerSha256 != null && !ShaPattern.IsMatch(m.ManagerSha256)) return "has a broken checksum for the manager";
        return null;
    }

    static UpdateCheck Failed(string why) => new() { State = UpdateState.Failed, Text = why };

    /// <summary>
    /// Downloads a release's files: the plugin and the patched renderer DLL into &lt;pack&gt;\payload.new (with the signed manifest)
    /// and, with a <paramref name="managerTarget"/> (the running exe's path + ".new"), the manager. Each file is checked against
    /// the signed manifest's sha256 before it gets its name; nothing in use is touched. Returns payload.new.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, string releases, UpdateCheck check, string packDir, string managerTarget,
                                                   IProgress<string> progress, CancellationToken ct)
    {
        var m = check.Remote;
        var dir = Path.Combine(packDir, "payload.new");
        if (Directory.Exists(dir)) Manager.Retry(() => Directory.Delete(dir, true));   // a download that stopped half way (ours: the name says so)
        Directory.CreateDirectory(dir);
        await Fetch(http, AssetUrl(releases, m.Tag, "Softbody.dll"), Path.Combine(dir, "Softbody.dll"), m.SoftbodySha256, MaxDll, progress, ct);
        await Fetch(http, AssetUrl(releases, m.Tag, "Renderite.Unity.dll"), Path.Combine(dir, "Renderite.Unity.dll"), m.RendererDllSha256, MaxDll, progress, ct);
        if (managerTarget != null)
            await Fetch(http, AssetUrl(releases, m.Tag, ManagerFile), managerTarget, m.ManagerSha256, MaxManager, progress, ct);
        File.WriteAllText(Path.Combine(dir, PackManifest.FileName), check.RemoteText, new UTF8Encoding(false));
        return dir;
    }

    /// <summary>
    /// Puts a downloaded release in place (Resonite must be closed): the pack's payload becomes the downloaded one (the old one
    /// is kept as payload.previous), Softbody is installed from it unless that version is in already (and switched off again if
    /// it was off), and the renderer fork, if there is one, gets the new patched DLL (or is made again from the installed
    /// renderer, when Resonite updated since). Returns the pack's new paths and what was done. 1.2.0: the release may be older
    /// than the pack (back to stable): it goes in the same way.
    /// </summary>
    public static (Paths paths, List<string> done) Apply(Paths p, string downloaded, Action<string> progress)
    {
        Manager.RequireClosed();
        if (!p.IsPack) throw new InvalidOperationException("updates are for friend packs");
        var manifest = PackManifest.Load(downloaded) ?? throw new InvalidOperationException("the download has no manifest");
        string payload = Path.Combine(p.Base, "payload"), previous = Path.Combine(p.Base, "payload.previous");
        if (Directory.Exists(previous)) Manager.Retry(() => Directory.Delete(previous, true));   // the one before last (ours: the name says so)
        Manager.Retry(() => Directory.Move(payload, previous));
        try { Manager.Retry(() => Directory.Move(downloaded, payload)); }
        catch { Manager.Retry(() => Directory.Move(previous, payload)); throw; }
        var np = Paths.ForPack(p.Base, PackManifest.Load(payload), p.Game, p.SteamRoot);
        var done = new List<string> { $"The pack is now {manifest.Tag} (Softbody {manifest.SoftbodyVersion}); the previous one is kept as payload.previous." };

        var a = Manager.Softbody;
        var state = Manager.StateOf(np, a);
        bool wasOn = state is AddonState.On or AddonState.OnWithStaleOff;
        var installed = state == AddonState.NotInstalled ? null : Manager.VersionOf(wasOn ? a.OnPath(np) : a.OffPath(np));
        var fresh = Manager.VersionOf(np.BuildDll);
        if (installed == null || installed != fresh)
        {
            done.Add(Manager.Install(np, a, np.BuildDll, DateTime.Now));
            if (state == AddonState.Off) done.Add("It was off before the update, so off again: " + Manager.TurnOff(np, a));
        }
        else done.Add($"Softbody {Manager.Short(fresh)} is installed already.");

        var forkDll = Path.Combine(np.Fork, Paths.RendererDll);
        if (!Directory.Exists(np.Fork))
            done.Add("There is no renderer fork yet: press Set up renderer fork when you want it.");
        else if (!File.Exists(Path.Combine(np.Fork, Manager.ForkMarker)))
            done.Add($"The renderer fork at {np.Fork} was not made by the manager: left as it is.");
        else if (File.Exists(forkDll) && Manager.CommitOf(forkDll) == Manager.OfficialCommit(np))
            done.Add(Manager.UpdateForkDll(np));
        else
        {
            progress?.Invoke("Resonite's renderer changed since the fork was made: making the fork again ...");
            done.Add(Manager.SetUpFork(np, progress));
        }
        Manager.AppendLog(np, $"{DateTime.Now:yyyy-MM-dd HH:mm} updated the pack to {manifest.Tag} (Softbody {manifest.SoftbodyVersion}, renderer patch " +
                              $"{manifest.RendererDllSha256[..8]}) from GitHub; the previous payload is kept as payload.previous.");
        return (np, done);
    }

    /// <summary>
    /// Swaps in a downloaded manager (<paramref name="exe"/> + ".new"): the running one is renamed to .old (Windows lets a running
    /// program be renamed, not deleted) and the new one takes its name. The next start deletes the .old. Returns whether it swapped.
    /// </summary>
    public static bool SwapManager(string exe)
    {
        string fresh = exe + ".new", old = exe + ".old";
        if (!File.Exists(fresh)) return false;
        if (File.Exists(old)) Manager.Retry(() => File.Delete(old));   // the one before last (ours)
        Manager.Retry(() => File.Move(exe, old));
        try { Manager.Retry(() => File.Move(fresh, exe)); }
        catch { Manager.Retry(() => File.Move(old, exe)); throw; }
        return true;
    }

    /// <summary>Deletes what a self-update leaves next to the manager: the replaced one (.old) and an unused download (.new).</summary>
    public static void CleanUp(string exe)
    {
        if (exe == null) return;
        foreach (var f in new[] { exe + ".old", exe + ".new", exe + ".new.part" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>A small text file (<paramref name="api"/>: from GitHub's API, which asks for its own headers and has a limit of 60
    /// requests an hour from one network without signing in).</summary>
    static async Task<string> GetText(HttpClient http, string url, long max, CancellationToken ct, bool api = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (api)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (api && (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) &&
            resp.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0")
            throw new HttpRequestException("GitHub's limit for checks from your network (60 an hour) is used up: it works again within the hour",
                                           null, resp.StatusCode);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > max) throw new InvalidDataException("the file is far too big");
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var mem = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            mem.Write(buf, 0, n);
            if (mem.Length > max) throw new InvalidDataException("the file is far too big");
        }
        return Encoding.UTF8.GetString(mem.ToArray());
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="target"/> (through a .part file), refusing it unless its sha256
    /// is <paramref name="sha256"/>.</summary>
    static async Task Fetch(HttpClient http, string url, string target, string sha256, long max, IProgress<string> progress, CancellationToken ct)
    {
        string name = Path.GetFileName(new Uri(url).LocalPath), part = target + ".part";
        progress?.Report($"downloading {name} ...");
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"the release has no {name}");
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > max) throw new InvalidDataException($"{name} is far too big");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0, nextReport = 8L << 20;
            await using (var s = await resp.Content.ReadAsStreamAsync(ct))
            await using (var f = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > max) throw new InvalidDataException($"{name} is far too big");
                    hash.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                    if (total >= nextReport) { progress?.Report($"  {name}: {total >> 20} MB"); nextReport += 8L << 20; }
                }
            }
            var got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the downloaded {name} does not match Lenki's signed manifest (damaged or changed on the way): nothing was changed");
            if (File.Exists(target)) Manager.Retry(() => File.Delete(target));   // left by a download that stopped half way (ours)
            Manager.Retry(() => File.Move(part, target));
            progress?.Report($"  {name}: {(total < 1048576 ? $"{total / 1024} KB" : $"{total / 1048576.0:0.0} MB")}, checksum OK");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    static string Ten(string commit) => commit == null ? "?" : commit[..Math.Min(10, commit.Length)];
    static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

/// <summary>1.2.0: a release in GitHub's list: its tag, whether it is a pre-release (an unstable build), when it came out.</summary>
sealed record ReleaseInfo(string Tag, bool Prerelease, DateTimeOffset Published);
