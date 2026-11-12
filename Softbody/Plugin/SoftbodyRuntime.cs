using System.Globalization;
using System.Text;
using Elements.Core;
using FrooxEngine;

namespace Softbody;

/// <summary>
/// Per-client, engine-wide state shared by all Softbody components: logging, the per-frame budget, the total
/// cage-point cap, which component drives which mesh asset, the restore pump, and the loopback stats endpoint.
/// Initialized lazily by the first Softbody component that starts (no platform connector or mod loader needed).
/// Everything that touches component or instance state takes <see cref="Gate"/>.
/// </summary>
static class SoftbodyRuntime
{
    public const string Version = "0.27.0";
    public const string TypeName = "[Softbody]Softbody.Softbody";
    public const string SkinnedTypeName = "[Softbody]Softbody.SkinnedSoftbody";
    /// <summary>Cap on the flesh (SkinnedSoftbody) surface points simulated on this client, all components together.</summary>
    public const int MaxFleshPoints = 12000;
    /// <summary>0.15.0: the most liquid particles one world simulates on this client (all emitters together).</summary>
    public const int MaxFluidParticles = 20000;
    /// <summary>0.16.0: a world's particles at most when the renderer fork simulates the liquid on the GPU (0.25.0: 524288,
    /// from 262144; the renderer makes room for the world's emitters' MaxParticles together, in steps, see FluidWorld).</summary>
    public const int MaxGpuFluidParticles = 524288;
    /// <summary>0.26.0: the gas's puffs at most in a world (the renderer's ring: past it the oldest go first).</summary>
    public const int MaxGasPuffs = 32768;
    /// <summary>0.25.0: run the liquid on 0.24.0's solver, kept as an emergency backup in case the new one misbehaves (particles
    /// fall asleep one by one, no divergence pass, no mesh smoothing): while a file named Softbody.classic sits next to this
    /// plugin's DLL (checked every 2 seconds; the liquid wakes up and carries on). This client's own choice: the liquid is
    /// simulated by each client anyway, and a synced setting would have made 0.25.0 unable to share a session with 0.19.0 to
    /// 0.24.0.</summary>
    public static bool ClassicSolver
    {
        get
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _classicCheckedAt > 2 * System.Diagnostics.Stopwatch.Frequency)
            {
                _classicCheckedAt = now;
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(typeof(SoftbodyRuntime).Assembly.Location);
                    _classic = !string.IsNullOrEmpty(dir) && System.IO.File.Exists(System.IO.Path.Combine(dir, "Softbody.classic"));
                }
                catch { _classic = false; }
            }
            return _classic;
        }
    }
    static long _classicCheckedAt = long.MinValue / 2;
    static bool _classic;

    /// <summary>0.17.0: the range of FluidEmitter.ParticleSize (metres) and SurfaceDetail.</summary>
    public const float MinParticleSize = 0.005f, MaxParticleSize = 0.5f, MinSurfaceDetail = 0.5f, MaxSurfaceDetail = 3f;
    /// <summary>0.18.0: the range of FluidEmitter.DropletSize.</summary>
    public const float MinDropletSize = 0.5f, MaxDropletSize = 3f;
    public const string BrushTypeName = "[Softbody]Softbody.SoftbodyBrush";
    public const int MaxTotalPoints = 4000;
    public const double StatsInterval = 5.0;

    public static readonly object Gate = new();
    /// <summary>Default for each Softbody's Budget field (its auto shut-off threshold); SOFTBODY_BUDGET_MS overrides it.</summary>
    public static float DefaultBudgetMs { get; private set; } = 4.5f;
    public static double BudgetMs => DefaultBudgetMs;

    static bool _initialized;
    static volatile bool _shutdown;
    static readonly HashSet<Softbody> _components = new();
    static readonly HashSet<SkinnedSoftbody> _flesh = new();
    // claims: a mesh asset (Softbody, and SkinnedSoftbody without instancing) or a StaticMesh (instanced SkinnedSoftbody, 0.14.0)
    static readonly Dictionary<object, object> _claims = new(ReferenceEqualityComparer.Instance);

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
        }
        try
        {
            if (float.TryParse(Environment.GetEnvironmentVariable("SOFTBODY_BUDGET_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0)
                DefaultBudgetMs = Math.Clamp(b, 0.5f, 50f);
            try { Engine.Current.OnShutdown += OnShutdown; }
            catch (Exception ex) { Warn("could not subscribe to engine shutdown: " + ex.Message); }
            StatsServer.Start(StatusJson);
            Task.Run(RestorePump);
            Log($"Softbody {Version} plugin ready: components {TypeName} (lattice, MeshRenderer or SkinnedMeshRenderer) and {SkinnedTypeName} (tet shell + XPBD, SkinnedMeshRenderer), paint brush {BrushTypeName}. Default budget {BudgetMs} ms per softbody per frame (each component's Budget field), cap {MaxTotalPoints} cage points and {MaxFleshPoints} flesh points in total. " +
                $"Skinned meshes: {GpuSkinModeDescription()}.");
        }
        catch (Exception ex) { Warn("runtime init failed: " + ex); }
    }

    static void OnShutdown()
    {
        _shutdown = true;
        StatsServer.Stop();
    }

    // Restores can still be waiting for a renderer ack after their world stopped updating (closed or unfocused).
    static async Task RestorePump()
    {
        while (!_shutdown)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                if (AssetUploads.PendingRestoreCount > 0) lock (Gate) AssetUploads.ProcessRestores();
                if (PrivateMeshes.PendingUnloads > 0) PrivateMeshes.ProcessUnloads();
            }
            catch (Exception ex) { Warn("restore pump: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ registry (call with Gate held)

    public static void Register(Softbody c) => _components.Add(c);
    public static void Unregister(Softbody c) => _components.Remove(c);
    public static void Register(SkinnedSoftbody c) => _flesh.Add(c);
    public static void Unregister(SkinnedSoftbody c) => _flesh.Remove(c);

    /// <summary>The Softbody and SkinnedSoftbody components of <paramref name="world"/> running on this client (paint targets for the brush).</summary>
    public static void CollectPaintTargets(World world, List<IPaintable> into)
    {
        into.Clear();
        foreach (var c in _components)
            if (c.PaintTarget != null && ReferenceEquals(c.World, world) && !c.IsRemoved) into.Add(c);
        foreach (var c in _flesh)
            if (((IPaintable)c).BrushTarget != null && ReferenceEquals(c.World, world) && !c.IsRemoved) into.Add(c);
    }

    /// <summary>Flesh surface points simulated by every SkinnedSoftbody on this client but <paramref name="except"/>.</summary>
    public static int TotalFleshPoints(SkinnedSoftbody except)
    {
        int n = 0;
        foreach (var c in _flesh) if (c != except) n += c.RunningPointCount;
        return n;
    }

    public static int TotalPoints(Softbody except)
    {
        int n = 0;
        foreach (var c in _components) if (c != except) n += c.RunningPointCount;
        return n;
    }

    /// <summary>One Softbody per mesh asset (the asset is shared engine-wide by every StaticMesh with the same URL), and one
    /// SkinnedSoftbody per StaticMesh (0.14.0: each has its own copy of the mesh; per asset without instancing).</summary>
    public static bool TryClaim(object key, Component owner, out Component holder)
    {
        holder = null;
        if (key == null) return false;
        if (_claims.TryGetValue(key, out var h) && !ReferenceEquals(h, owner) && IsAlive(h)) { holder = h as Component; return false; }
        _claims[key] = owner;
        return true;
    }

    public static void Release(object key, Component owner)
    {
        if (key != null && _claims.TryGetValue(key, out var h) && ReferenceEquals(h, owner)) _claims.Remove(key);
    }

    static bool IsAlive(object o)
    {
        try
        {
            return o switch
            {
                Softbody c => !c.IsDestroyed && c.World != null && !c.World.IsDestroyed && c.RunningPointCount > 0,
                SkinnedSoftbody f => !f.IsDestroyed && f.World != null && !f.World.IsDestroyed && f.IsRunning,
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>The slot name of a component, for messages (never throws).</summary>
    public static string NameOf(Component c)
    {
        try { return c?.Slot?.Name ?? "?"; } catch { return "?"; }
    }

    // ------------------------------------------------------------------ skinned meshes: GPU path or CPU path (0.9.0)

    enum GpuSkinMode { Auto, Off, Probe }
    static GpuSkinMode? _gpuMode;
    static long _handshakeCheckedAt;
    static bool _handshakeSeen;
    static int _gpuConfirmed, _gpuFailed;
    static readonly HashSet<string> _loggedOnce = new();

    static GpuSkinMode Mode
    {
        get
        {
            if (_gpuMode.HasValue) return _gpuMode.Value;
            var v = (Environment.GetEnvironmentVariable("SOFTBODY_GPU_SKIN") ?? "").Trim().ToLowerInvariant();
            _gpuMode = v is "off" or "cpu" or "0" or "false" ? GpuSkinMode.Off : v is "probe" or "force" or "on" ? GpuSkinMode.Probe : GpuSkinMode.Auto;
            return _gpuMode.Value;
        }
    }

    static string GpuSkinModeDescription() => Mode switch
    {
        GpuSkinMode.Off => "CPU path (SOFTBODY_GPU_SKIN=off)",
        GpuSkinMode.Probe => "GPU path without the renderer handshake (SOFTBODY_GPU_SKIN=probe), CPU path if the renderer recreates the mesh",
        _ => GpuUpload.Unavailable != null ? "CPU path (" + GpuUpload.Unavailable + ")" :
             RendererPatchPresent() ? "GPU path (SoftbodyRenderer patch found in the renderer)" : "CPU path until the SoftbodyRenderer patch is found in the renderer",
    };

    /// <summary>True when the renderer runs the SoftbodyRenderer patch: it holds a named event open while patched (Windows).
    /// Checked at most every 2 s.</summary>
    internal static bool RendererPatchPresent()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_handshakeCheckedAt != 0 && now - _handshakeCheckedAt < 2 * System.Diagnostics.Stopwatch.Frequency) return _handshakeSeen;
        _handshakeCheckedAt = now;
        bool seen = false;
        try
        {
            if (OperatingSystem.IsWindows() && System.Threading.EventWaitHandle.TryOpenExisting(PartialEncode.HandshakeName, out var h))
            {
                h.Dispose();
                seen = true;
            }
        }
        catch { }
        _handshakeSeen = seen;
        return seen;
    }

    /// <summary>
    /// Whether a new skinned Softbody should use the GPU path: the renderer keeps skinning the mesh and the plugin only
    /// uploads bind-space positions and normals. Needs the SoftbodyRenderer patch (or SOFTBODY_GPU_SKIN=probe), the engine
    /// members of <see cref="GpuUpload"/>, and no earlier evidence in this session that the renderer does not apply such
    /// uploads in place. Gate held.
    /// </summary>
    public static bool UseGpuSkinning(out string why)
    {
        why = null;
        if (Mode == GpuSkinMode.Off) { why = "SOFTBODY_GPU_SKIN=off"; return false; }
        if (GpuUpload.Unavailable != null) { why = "engine members missing: " + GpuUpload.Unavailable; return false; }
        if (Volatile.Read(ref _gpuFailed) >= 2 && Volatile.Read(ref _gpuConfirmed) == 0) { why = "the renderer recreated the mesh on vertex-only uploads twice this session"; return false; }
        if (Mode == GpuSkinMode.Probe) return true;
        if (!RendererPatchPresent()) { why = "no SoftbodyRenderer patch detected in the renderer"; return false; }
        return true;
    }

    /// <summary>An instance's first GPU-path partial upload came back in place (instanceChanged false).</summary>
    public static void NoteGpuConfirmed()
    {
        Interlocked.Increment(ref _gpuConfirmed);
        LogOnce("gpu-ok", "skinned: GPU path (renderer patch active)");
    }

    /// <summary>The renderer recreated the mesh on a GPU-path partial upload: that instance falls back to the CPU path.</summary>
    public static void NoteGpuFailed() => Interlocked.Increment(ref _gpuFailed);

    public static void LogOnce(string key, string s)
    {
        lock (_loggedOnce) if (!_loggedOnce.Add(key)) return;
        Log(s);
    }

    // ------------------------------------------------------------------ logging

    public static void Log(string s)
    {
        try { UniLog.Log("[Softbody] " + s, false); } catch { }
    }

    public static void Warn(string s)
    {
        try { UniLog.Warning("[Softbody] " + s, false); } catch { }
    }

    // ------------------------------------------------------------------ stats (stats-server thread)

    static string StatusJson()
    {
        lock (Gate)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"ok\":true,\"plugin\":\"Softbody " + Version + "\"");
            sb.Append(",\"componentType\":").Append(StatsServer.Json(TypeName));
            sb.Append(",\"skinnedComponentType\":").Append(StatsServer.Json(SkinnedTypeName));
            sb.Append(",\"maxFleshPoints\":").Append(MaxFleshPoints);
            sb.Append(",\"totalFleshPoints\":").Append(TotalFleshPoints(null));
            sb.Append(",\"budgetMs\":").Append(BudgetMs.ToString(ic));
            sb.Append(",\"maxTotalPoints\":").Append(MaxTotalPoints);
            sb.Append(",\"totalPoints\":").Append(TotalPoints(null));
            sb.Append(",\"pendingRestores\":").Append(AssetUploads.PendingRestoreCount);
            sb.Append(",\"skinnedPath\":").Append(StatsServer.Json(GpuSkinModeDescription()));
            sb.Append(",\"claimedAssets\":").Append(_claims.Count);
            sb.Append(",\"softbodies\":[");
            bool first = true;
            foreach (var c in _components)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(c.SnapshotJson ?? "{}");
            }
            sb.Append("],\"skinnedSoftbodies\":[");
            first = true;
            foreach (var c in _flesh)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(c.SnapshotJson ?? "{}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
