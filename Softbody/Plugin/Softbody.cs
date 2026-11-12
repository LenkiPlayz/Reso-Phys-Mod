using System.Globalization;
using System.Numerics;
using System.Text;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody;

/// <summary>
/// Selective softbody: the vertices of <see cref="Renderer"/>'s StaticMesh follow a lattice cage simulated in
/// a private Bepu simulation, weighted by one vertex-colour channel (0 = firm, 1 = squishy).
///
/// Skinned meshes (0.8.0, experimental): Renderer may be a SkinnedMeshRenderer (an avatar body). The cage then follows
/// the skeleton and the wobble rides on top of the bones' motion. With the SoftbodyRenderer patch in the renderer (0.9.0)
/// the mesh stays GPU-skinned and only bind-space positions and normals are uploaded; without it, each client CPU-skins
/// the mesh itself (blendshape weights included) and shows it as an unskinned mesh, because the stock renderer recreates
/// GPU-skinned meshes on every upload (see SoftbodyInstance and README).
///
/// Synced and saved: only the settings below (normal Sync fields, edited in the inspector). Not synced, not
/// saved: everything the simulation does. Each client simulates locally from these settings and the renderer
/// slot's pose, swapping its own copy of the mesh asset's data; the status fields are RawOutputs (local
/// values that are never networked or saved). Only the focused world simulates.
///
/// Changing Stiffness, Volume, Damping, Follow, Firm, Mass, Gravity, Drag, Friction, Iterations or Collide
/// re-tunes the running cage; Cells, Pin, Radius, Renderer, SoftnessChannel and InvertSoftness rebuild it.
///
/// Paint (0.4.0): <see cref="Paint"/> is a synced list of dabs (painted with the <see cref="SoftbodyBrush"/>) that
/// repaints the mesh's vertex colours on every client without touching the mesh file. The effective colours are
/// the softness map (and are shown by a vertex-colour material at once); the cage follows 0.3 s after the last
/// change, rebuilt in place.
/// </summary>
[Category(new[] { "Physics" })]
// Runs after the default-order (0) components of the frame, such as Animator, so a skinned cage sees this frame's bone
// pose from them as well as from VRIK (-5000). Dynamic bones and ProtoFlux run after all components: one frame behind.
[DefaultUpdateOrder(1000)]
public class Softbody : Component, IPaintable
{
    SyncList<PaintDab> IPaintable.PaintDabs => Paint;
    IBrushTarget IPaintable.BrushTarget => _inst;
    /// <summary>Which part of the vertex colour is the softness map. Value (the default) is HSV
    /// brightness, max(r, g, b): paint with any hue, darker = firmer, brighter = squishier.</summary>
    public enum Channel { Red, Green, Blue, Alpha, Value }

    // ---- what to deform (changes rebuild)
    /// <summary>The MeshRenderer or SkinnedMeshRenderer (with a StaticMesh) to deform. Defaults to the one on this slot (a plain
    /// MeshRenderer first). May be on another slot.</summary>
    public readonly SyncRef<MeshRenderer> Renderer;
    public readonly Sync<Channel> SoftnessChannel;
    /// <summary>Use 1 - channel (paint the firm parts instead of the soft ones).</summary>
    public readonly Sync<bool> InvertSoftness;
    /// <summary>What the paint starts from: the mesh's own vertex colours (white when it has none), all soft
    /// (white: paint black to firm parts up), or all firm (black: paint bright colours to make parts squishy).</summary>
    public enum StartColor { MeshColors, AllSoft, AllFirm }
    public readonly Sync<StartColor> StartAs;

    // ---- cage (changes rebuild)
    /// <summary>Lattice cells along the mesh's longest side, 1..32. Only cells around soft (non-black)
    /// regions get points, so firm parts cost nothing and higher values stay affordable.</summary>
    public readonly Sync<int> Cells;
    /// <summary>Cage points with softness at or below this are pinned to the slot (0..0.5).</summary>
    public readonly Sync<float> Pin;
    /// <summary>Collision sphere per cage point, as a fraction of a cell (0.02..0.5).</summary>
    public readonly Sync<float> Radius;

    // ---- live tuning (applied to the running cage)
    /// <summary>Hz, edge springs in squishy regions (0.2..60).</summary>
    public readonly Sync<float> Stiffness;
    /// <summary>Hz, volume springs per tetrahedron (0 = 2 x Stiffness, else 0.2..60).</summary>
    public readonly Sync<float> Volume;
    /// <summary>Damping ratio of squishy springs, lower = wobblier (0.01..4).</summary>
    public readonly Sync<float> Damping;
    /// <summary>Hz, how hard squishy parts are pulled back to their place on the slot (0.05..60).</summary>
    public readonly Sync<float> Follow;
    /// <summary>Hz, springs for nearly-firm parts that are still simulated (1..60).</summary>
    public readonly Sync<float> Firm;
    /// <summary>kg, whole cage (0.01..1000).</summary>
    public readonly Sync<float> Mass;
    /// <summary>Gravity multiplier (-10..10).</summary>
    public readonly Sync<float> Gravity;
    /// <summary>Linear damping, 1/s (0..50).</summary>
    public readonly Sync<float> Drag;
    /// <summary>Against the ground and proxies (0..4).</summary>
    public readonly Sync<float> Friction;
    /// <summary>Solver iterations (1..32).</summary>
    public readonly Sync<int> Iterations;
    /// <summary>Collide with the ground (downward ray) and nearby Box/Sphere colliders.</summary>
    public readonly Sync<bool> Collide;
    /// <summary>Auto shut-off: if this softbody's average cost per frame on this client goes above this many
    /// milliseconds, it pauses itself (0.5..50; default 4.5). Change any setting or toggle Enabled to retry.</summary>
    public readonly Sync<float> Budget;

    // ---- debug views (0.10.0): drawn by each client from its own simulation, on a local slot (never synced or saved).
    // The toggles themselves are synced, so everyone in the session with the plugin sees the views while they are on.
    /// <summary>Shows the cage: the cells' edges coloured by softness (blue firm .. orange squishy, grey pinned), the cage
    /// points, and in red the points the untangler is pulling back.</summary>
    public readonly Sync<bool> DebugCells;
    /// <summary>Shows the collision: each simulated point's collision sphere (green, yellow while touching), the Box/Sphere
    /// colliders it reacts to (magenta), the ground under it (cyan) and the box searched for colliders (grey).</summary>
    public readonly Sync<bool> DebugColliders;

    // ---- per hue band (live): multipliers for the parts painted in that hue. 1 = the plain settings above.
    // A colour's hue picks the band (blending between neighbours), its saturation how strongly; grey,
    // white and black use the plain settings. Brightness (Value) still sets how soft each part is.
    /// <summary>Multipliers on Stiffness, Damping and Follow for one hue band.</summary>
    public class HueBand : SyncObject
    {
        public readonly Sync<float> Stiffness;
        public readonly Sync<float> Damping;
        public readonly Sync<float> Follow;
    }
    public readonly HueBand Red;
    public readonly HueBand Orange;
    public readonly HueBand Yellow;
    public readonly HueBand Green;
    public readonly HueBand Blue;
    public readonly HueBand Indigo;
    public readonly HueBand Violet;
    HueBand[] Bands => new[] { Red, Orange, Yellow, Green, Blue, Indigo, Violet };

    // ---- paint (synced, saved): applied in order on top of the mesh's own colours (white when it has none),
    // c = lerp(c, Color, Strength * falloff(distance / Radius)) on the rest mesh. Erasing a dab brings back what
    // was under it. Hidden in the inspector (it can hold thousands of entries); Dabs shows the count.
    /// <summary>One paint dab, in the renderer slot's local space (it moves with the object).</summary>
    public class PaintDab : SyncObject
    {
        /// <summary>Centre, in the renderer slot's local space (rest mesh).</summary>
        public readonly Sync<float3> Position;
        /// <summary>Radius in the renderer slot's local units.</summary>
        public readonly Sync<float> Radius;
        /// <summary>Hue picks the band, brightness the softness (with the default Value channel).</summary>
        public readonly Sync<colorX> Color;
        /// <summary>0..1, how far the colour moves towards this dab's colour at the centre.</summary>
        public readonly Sync<float> Strength;
        /// <summary>0..1: full strength out to Hardness x Radius, then a smooth falloff to the rim.</summary>
        public readonly Sync<float> Hardness;
        /// <summary>A blur dab: softens the colours under it towards their neighbourhood average (Color is ignored).</summary>
        public readonly Sync<bool> Blur;
    }
    [HideInInspector]
    public readonly SyncList<PaintDab> Paint;

    // ---- status: RawOutput = local to this client, never synced, never saved (written at most 2x per second)
    public readonly RawOutput<string> Status;
    public readonly RawOutput<int> Points;
    public readonly RawOutput<int> Vertices;
    public readonly RawOutput<int> MovingVertices;
    public readonly RawOutput<float> SimMs;
    public readonly RawOutput<float> FrameMs;
    public readonly RawOutput<int> Resets;
    public readonly RawOutput<int> Tunes;
    public readonly RawOutput<int> Dabs;
    public readonly RawOutput<string> Effective;
    public readonly RawOutput<string> LastError;

    // ---- local runtime state (never synced)
    SoftbodyInstance _inst;
    SoftbodySettings _settings;
    string _state = "starting", _error = "", _flushedState, _loggedReason, _runningState = "running";
    string _blockedKey;      // settings key at the time we paused for budget/error; cleared by any setting change
    float _retryTimer;
    double _statusTimer, _statsTimer;
    bool _registered;
    // debug views: a local slot with the debug mesh, an overlay material and a renderer
    Slot _debugSlot;
    SoftbodyDebugMesh _debugMesh;
    float _debugTimer, _debugRetry;
    bool _debugTruncLogged;
    internal string SnapshotJson { get; private set; }
    internal int RunningPointCount => _inst?.PointCount ?? 0;

    // paint mirror: the synced dabs as Core dabs in the running mesh's colour space, cached per element
    bool _paintDirty = true;
    float _paintPoll, _paintCooldown;
    Dab[] _dabBuf = Array.Empty<Dab>();
    RefID[] _dabIds = Array.Empty<RefID>();   // not PaintDab[]: the weaver treats arrays of sync objects as sync members
    RawDab[] _dabRaw = Array.Empty<RawDab>();
    ColorProfile _dabProfile;

    readonly struct RawDab
    {
        public readonly float3 Position; public readonly float Radius; public readonly colorX Color; public readonly float Strength, Hardness;
        public readonly bool Blur;
        public RawDab(PaintDab d) { Position = d.Position.Value; Radius = d.Radius.Value; Color = d.Color.Value; Strength = d.Strength.Value; Hardness = d.Hardness.Value; Blur = d.Blur.Value; }
        public bool Same(in RawDab o) =>
            Blur == o.Blur &&
            Position.x.Equals(o.Position.x) && Position.y.Equals(o.Position.y) && Position.z.Equals(o.Position.z) && Radius.Equals(o.Radius) &&
            Strength.Equals(o.Strength) && Hardness.Equals(o.Hardness) && Color.profile == o.Color.profile &&
            Color.baseColor.r.Equals(o.Color.baseColor.r) && Color.baseColor.g.Equals(o.Color.baseColor.g) &&
            Color.baseColor.b.Equals(o.Color.baseColor.b) && Color.baseColor.a.Equals(o.Color.baseColor.a);
    }

    string LogName
    {
        get { try { return Slot?.Name ?? "?"; } catch { return "?"; } }
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnAwake()
    {
        base.OnAwake();
        var d = new SoftbodySettings();
        SoftnessChannel.Value = Channel.Value;
        InvertSoftness.Value = false;
        StartAs.Value = StartColor.MeshColors;
        Cells.Value = d.Cells;
        Pin.Value = d.RigidThreshold;
        Radius.Value = d.PointRadiusFactor;
        Stiffness.Value = d.Stiffness;
        Volume.Value = d.VolumeStiffness;
        Damping.Value = d.Damping;
        Follow.Value = d.Follow;
        Firm.Value = d.RigidFrequency;
        Mass.Value = d.Mass;
        Gravity.Value = d.Gravity;
        Drag.Value = d.LinearDamping;
        Friction.Value = d.Friction;
        Iterations.Value = d.SolverIterations;
        Collide.Value = d.Collide;
        Budget.Value = SoftbodyRuntime.DefaultBudgetMs;
        DebugCells.Value = false;
        DebugColliders.Value = false;
        foreach (var b in Bands) { b.Stiffness.Value = 1f; b.Damping.Value = 1f; b.Follow.Value = 1f; }
    }

    protected override void OnAttach()
    {
        base.OnAttach();
        try
        {
            // Only on the client that attaches the component (then it syncs like any reference).
            Renderer.Target = Slot.GetComponent<MeshRenderer>(r => r.GetType() == typeof(MeshRenderer)) ?? Slot.GetComponent<MeshRenderer>();
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' OnAttach: {ex.Message}"); }
    }

    protected override void OnStart()
    {
        base.OnStart();
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            lock (SoftbodyRuntime.Gate)
            {
                SoftbodyRuntime.Register(this);
                _registered = true;
                _settings = ReadSettings();
                _state = "waiting to start";
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch (Exception ex) { Report(ex, "OnStart"); }
    }

    protected override void OnChanges()
    {
        base.OnChanges();
        try
        {
            lock (SoftbodyRuntime.Gate)
            {
                _paintDirty = true; // cheap to re-check: the dab list is compared element by element
                _settings = ReadSettings();
                if (!Enabled)
                {
                    Stop("disabled");
                    _blockedKey = null;
                    _state = "disabled";
                }
                else if (_inst != null)
                {
                    if (BuildKey(_settings) != _inst.BuildKey)
                    {
                        Stop("settings changed: rebuilding");
                        _retryTimer = 0;
                        _state = "rebuilding";
                    }
                    else
                    {
                        var r = _inst.ApplyTuning(_settings);
                        if (r == TuneResult.Applied) SoftbodyRuntime.Log($"'{LogName}' tuned live: {_inst.Settings}");
                        else if (r == TuneResult.NeedsRebuild) { Stop("settings changed: rebuilding"); _retryTimer = 0; _state = "rebuilding"; }
                    }
                }
                else if (_blockedKey != null && _blockedKey != FullKey(_settings))
                {
                    _blockedKey = null; // a setting changed: try again
                    _retryTimer = 0;
                    _state = "retrying";
                }
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch (Exception ex) { Report(ex, "OnChanges"); }
    }

    float _bandPoll;

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try
        {
            // Edits inside the hue-band groups are nested sync objects; make sure they reach OnChanges.
            _bandPoll -= World.Time.Delta;
            if (_bandPoll <= 0f)
            {
                _bandPoll = 0.5f;
                if (_settings != null && !ReadSettings().SameBands(_settings)) MarkChangeDirty();
            }
            lock (SoftbodyRuntime.Gate) Frame();
        }
        catch (Exception ex)
        {
            Report(ex, "frame");
        }
    }

    protected override void OnEnabled()
    {
        base.OnEnabled();
        _retryTimer = 0;
    }

    protected override void OnDisabled()
    {
        base.OnDisabled();
        Guarded("disabled", () => { _blockedKey = null; _state = "disabled"; });
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        _retryTimer = 0;
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();
        Guarded("slot inactive", () => _state = "paused: slot inactive");
    }

    protected override void OnDestroy()
    {
        Guarded("component removed", () =>
        {
            if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; }
        });
        base.OnDestroy();
    }

    public override void OnWorldDestroy()
    {
        Guarded("world closed", () =>
        {
            if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; }
        });
        // no base call: the engine's ComponentBase.OnWorldDestroy throws by design (only overrides may run; 0.26.2)
    }

    public override void OnFocusChanged(FrooxEngine.World.WorldFocus focus)
    {
        // no base call: the engine's ComponentBase.OnFocusChanged throws by design (only overrides may run; 0.26.2)
        if (focus != FrooxEngine.World.WorldFocus.Focused)
            Guarded("world lost focus", () => _state = "paused: world not focused (only the focused world simulates)");
        else _retryTimer = 0;
    }

    /// <summary>Stops (restoring the mesh) under the gate, then runs <paramref name="after"/>; never throws.</summary>
    void Guarded(string why, Action after)
    {
        try
        {
            lock (SoftbodyRuntime.Gate)
            {
                Stop(why);
                after?.Invoke();
                UpdateSnapshot();
            }
        }
        catch (Exception ex)
        {
            try { SoftbodyRuntime.Warn($"'{LogName}' {why}: {ex}"); } catch { }
        }
    }

    // ------------------------------------------------------------------ per frame (Gate held)

    void Frame()
    {
        float dt = World.Time.Delta;
        if (!float.IsFinite(dt) || dt <= 0) return;
        dt = MathF.Min(dt, 0.1f);

        if (World.Focus != FrooxEngine.World.WorldFocus.Focused)
        {
            if (_inst != null) Stop("world not focused");
            _state = "paused: world not focused (only the focused world simulates)";
            ThrottledFlush(dt);
            return;
        }

        if (_inst == null)
        {
            if (_blockedKey == null)
            {
                _retryTimer -= dt;
                if (_retryTimer <= 0) TryStart();
            }
            if (_inst == null) { ThrottledFlush(dt); return; }
        }

        if (!PaintFrame(dt)) { ThrottledFlush(dt); return; }

        var result = _inst.Frame(World, dt);
        switch (result)
        {
            case SoftbodyInstance.FrameResult.Gone:
                Stop("renderer, mesh or its slot was removed");
                _state = "waiting: the renderer, its mesh or its slot was removed";
                _retryTimer = 1f;
                break;
            case SoftbodyInstance.FrameResult.Rebuild:
                string why = _inst.RebuildReason ?? "mesh changed";
                Stop(why + ": rebuilding");
                _state = "rebuilding: " + why;
                _retryTimer = 0;
                break;
            case SoftbodyInstance.FrameResult.Paused:
                _state = "paused: renderer disabled or its slot inactive";
                DebugFrame(dt, running: false);
                break;
            case SoftbodyInstance.FrameResult.Failed:
                Block(_inst.FailReason ?? "stopped");
                break;
            default:
                if (_inst.TotalFrames > _inst.Warmup && _inst.EmaTotalMs > BudgetMs)
                    Block($"paused: over budget ({_inst.EmaTotalMs:0.00} ms per frame on average > {BudgetMs:0.##} ms). " +
                          "Raise Budget, lower Cells or Iterations, or change any setting (or toggle Enabled) to retry");
                else if (_inst.TotalFrames > _inst.Warmup && _inst.LastFrameMs > 100)
                    Block($"paused: a single frame took {_inst.LastFrameMs:0} ms. Change any setting (or toggle Enabled) to retry");
                else _state = _runningState = _inst.RunningState;
                if (_inst != null) DebugFrame(dt, running: true);
                break;
        }

        _statsTimer += dt;
        if (_statsTimer >= SoftbodyRuntime.StatsInterval)
        {
            if (_inst != null)
            {
                var line = _inst.StatsLine(_statsTimer, out var json);
                SoftbodyRuntime.Log(line);
                UpdateSnapshot(json);
            }
            else UpdateSnapshot();
            _statsTimer = 0;
        }
        ThrottledFlush(dt);
    }

    // ------------------------------------------------------------------ debug views (Gate held)

    /// <summary>Keeps the debug views in step with the toggles and the running simulation.</summary>
    void DebugFrame(float dt, bool running)
    {
        bool cells = DebugCells.Value, colliders = DebugColliders.Value;
        if ((!cells && !colliders) || _inst == null) { DestroyDebug(); return; }
        if (_debugRetry > 0f) { _debugRetry -= dt; return; }
        try
        {
            if (_debugSlot == null || _debugSlot.IsRemoved || _debugMesh == null || _debugMesh.IsRemoved) CreateDebug();
            if (_debugSlot.ActiveSelf != running) _debugSlot.ActiveSelf = running;
            if (!running) return;
            _debugTimer -= dt;
            if (_debugTimer > 0f) return;
            _inst.SubmitDebug(_debugMesh, cells, colliders);
            // at most 30 updates per second, and at most ~40 MB/s of vertex data to the renderer (28 bytes per vertex)
            _debugTimer = MathF.Max(1f / 30f, _debugMesh.LastVertexCount * 28f / 40e6f);
            var counts = _debugMesh.LastCounts;
            if (counts.Truncated && !_debugTruncLogged)
            {
                _debugTruncLogged = true;
                SoftbodyRuntime.Log($"'{LogName}' debug view is too big to show all of it ({counts.Vertices} vertices): some collision spheres or cells are left out");
            }
        }
        catch (Exception ex)
        {
            SoftbodyRuntime.Warn($"'{LogName}' debug view failed, trying again in 5 s: {ex}");
            DestroyDebug();
            _debugRetry = 5f;
        }
    }

    void CreateDebug()
    {
        DestroyDebug();
        var slot = World.RootSlot.AddLocalSlot("Softbody debug view (local): " + LogName, false);
        var mesh = slot.AttachComponent<SoftbodyDebugMesh>();
        var mat = slot.AttachComponent<OverlayUnlitMaterial>();
        mat.BlendMode.Value = BlendMode.Alpha;
        mat.UseVertexColors.Value = true;
        mat.Sidedness.Value = Sidedness.Double;
        mat.ZWrite.Value = ZWrite.Off;
        mat.FrontTintColor.Value = colorX.White;
        mat.BehindTintColor.Value = new colorX(1f, 1f, 1f, 0.35f); // dimmer where it is behind (inside) something
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = mat;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _debugSlot = slot;
        _debugMesh = mesh;
        _debugTimer = 0f;
        _debugTruncLogged = false;
    }

    void DestroyDebug()
    {
        var slot = _debugSlot;
        _debugSlot = null;
        _debugMesh = null;
        if (slot == null) return;
        try { if (!slot.IsRemoved && !slot.IsDisposed) slot.Destroy(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' removing the debug view: {ex.Message}"); }
    }

    /// <summary>Paint: brings the running mesh's colours to the synced dabs, and rebuilds the cage in place once the
    /// paint has been still for <see cref="SoftbodyInstance.RebuildDelay"/>. False when it had to stop.</summary>
    bool PaintFrame(float dt)
    {
        _paintPoll -= dt;
        _paintCooldown -= dt;
        // Expensive updates (full recomputes after erasing with many blur dabs) are rate-limited so they take at
        // most ~20% of the time; cheap ones (painting, plain erasing) run every frame.
        if ((_paintDirty || _paintPoll <= 0f) && _paintCooldown <= 0f)
        {
            _paintDirty = false;
            _paintPoll = 1f; // safety net in case a list change ever bypasses OnChanges
            _inst.UpdatePaint(ReadDabs(_inst.Profile));
            _paintCooldown = _inst.PaintMs > 4.0 ? MathF.Min(0.5f, (float)_inst.PaintMs * 0.005f) : 0f;
        }
        if (_inst.CageIsStale && _inst.SecondsSincePaintChange >= SoftbodyInstance.RebuildDelay)
        {
            int others = SoftbodyRuntime.TotalPoints(this);
            var r = _inst.RebuildCage(SoftbodyRuntime.MaxTotalPoints - others, out var why);
            if (r == SoftbodyInstance.RebuildResult.Done)
                SoftbodyRuntime.Log($"'{LogName}' cage rebuilt from the paint in {_inst.LastRebuildMs:0.0} ms: {_inst.Describe()}");
            else if (r == SoftbodyInstance.RebuildResult.Failed)
            {
                Stop("repainted: " + why);
                _state = "waiting: " + why;
                _retryTimer = 1f;
                return false;
            }
        }
        return true;
    }

    /// <summary>The synced dabs (at most <see cref="PaintLayer.MaxDabs"/>) as Core dabs with colours in <paramref name="profile"/>.
    /// Elements whose values did not change since the last call are not converted again.</summary>
    internal ReadOnlySpan<Dab> ReadDabs(ColorProfile profile)
    {
        int n = Math.Min(Paint.Count, PaintLayer.MaxDabs);
        if (_dabBuf.Length < n)
        {
            int cap = Math.Max(n, Math.Min(PaintLayer.MaxDabs, _dabBuf.Length * 2 + 16));
            Array.Resize(ref _dabBuf, cap); Array.Resize(ref _dabIds, cap); Array.Resize(ref _dabRaw, cap);
        }
        if (profile != _dabProfile) { Array.Clear(_dabIds); _dabProfile = profile; }
        for (int i = 0; i < n; i++)
        {
            var e = Paint[i];
            if (e == null) { _dabIds[i] = default; _dabBuf[i] = new Dab(Vector3.Zero, 1e-6f, Vector4.One, 0f, 1f); continue; }
            var raw = new RawDab(e);
            var id = e.ReferenceID;
            if (_dabIds[i] == id && raw.Same(_dabRaw[i])) continue;
            _dabIds[i] = id; _dabRaw[i] = raw;
            var c = raw.Color.ToProfile(profile);
            _dabBuf[i] = new Dab(new Vector3(raw.Position.x, raw.Position.y, raw.Position.z), raw.Radius, new Vector4(c.r, c.g, c.b, c.a), raw.Strength, raw.Hardness, raw.Blur).Sanitized();
        }
        return _dabBuf.AsSpan(0, n);
    }

    /// <summary>The running simulation, for the brush (Gate held). Null when not running on this client.</summary>
    internal SoftbodyInstance PaintTarget => _inst;

    /// <summary>Removes every paint dab (the mesh's own colours come back). Also a button in the inspector and
    /// callable over ResoniteLink (callSyncMethod ClearPaint).</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void ClearPaint()
    {
        try { Paint.Clear(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' ClearPaint: {ex.Message}"); }
    }

    /// <summary>Spawns a Softbody brush in front of whoever presses this (an inspector button; also callable over ResoniteLink).</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void SpawnBrush()
    {
        try { SoftbodyBrush.SpawnFor(World); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' SpawnBrush: {ex.Message}"); }
    }

    void TryStart()
    {
        var s = _settings ??= ReadSettings();
        // The sparse cage's point count is only known once it is built, so the cap is checked there.
        int others = SoftbodyRuntime.TotalPoints(this);
        SoftbodyInstance.CreateResult res;
        SoftbodyInstance inst = null;
        string reason;
        if (others >= SoftbodyRuntime.MaxTotalPoints)
        {
            res = SoftbodyInstance.CreateResult.Wait;
            reason = $"point cap: other softbodies already use {others} of {SoftbodyRuntime.MaxTotalPoints} cage points on this client";
        }
        else
        {
            try
            {
                res = SoftbodyInstance.TryCreate(Renderer.Target, Slot, this, s, SoftnessChannel.Value, InvertSoftness.Value, StartAs.Value, BuildKey(s),
                                                 SoftbodyRuntime.MaxTotalPoints - others, out inst, out reason);
            }
            catch (Exception ex) { res = SoftbodyInstance.CreateResult.Fail; reason = "create threw " + ex.GetType().Name + ": " + ex.Message; }
        }

        switch (res)
        {
            case SoftbodyInstance.CreateResult.Created:
                _inst = inst;
                _runningState = inst.RunningState;
                _state = _runningState;
                _error = "";
                _loggedReason = null;
                _statsTimer = 0;
                SoftbodyRuntime.Log($"'{LogName}' started on renderer '{inst.Name}': {inst.Describe()}; settings {s}");
                break;
            case SoftbodyInstance.CreateResult.Wait:
                _state = "waiting: " + reason;
                _retryTimer = 1f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Log($"'{LogName}' waiting: {reason}"); }
                break;
            default:
                _state = "error: " + reason;
                _error = reason;
                _retryTimer = 5f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Warn($"'{LogName}' cannot start: {reason}"); }
                break;
        }
        UpdateSnapshot();
    }

    /// <summary>Stops and restores the mesh. Safe to call when not running. Gate held.</summary>
    void Stop(string why)
    {
        DestroyDebug();
        if (_inst == null) return;
        var inst = _inst;
        _inst = null;
        try { inst.Teardown(restore: true); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' teardown: {ex.Message}"); }
        SoftbodyRuntime.Release(inst.Asset, this);
        SoftbodyRuntime.Log($"'{LogName}' stopped: {why}");
    }

    /// <summary>
    /// Stops, restores the mesh, and stays stopped on this client until a setting changes or Enabled is toggled.
    /// Never writes Enabled (or any synced field): the pause is local and shown in Status.
    /// </summary>
    void Block(string state, bool warn = true)
    {
        Stop("paused (see Status)");
        _blockedKey = FullKey(_settings ?? ReadSettings());
        _state = state;
        if (warn) SoftbodyRuntime.Warn($"'{LogName}' {state}");
    }

    void Report(Exception ex, string where)
    {
        try
        {
            lock (SoftbodyRuntime.Gate)
            {
                _error = $"{where}: {ex.GetType().Name}: {ex.Message}";
                SoftbodyRuntime.Warn($"'{LogName}' {where} threw: {ex}");
                Block("error: " + _error + ". Change any setting (or toggle Enabled) to retry", warn: false);
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ settings

    SoftbodySettings ReadSettings()
    {
        var s = new SoftbodySettings
        {
            Cells = Cells.Value,
            RigidThreshold = Pin.Value,
            PointRadiusFactor = Radius.Value,
            Stiffness = Stiffness.Value,
            VolumeStiffness = Volume.Value,
            Damping = Damping.Value,
            Follow = Follow.Value,
            RigidFrequency = Firm.Value,
            Mass = Mass.Value,
            Gravity = Gravity.Value,
            LinearDamping = Drag.Value,
            Friction = Friction.Value,
            SolverIterations = Iterations.Value,
            Collide = Collide.Value,
        };
        var bands = Bands;
        for (int i = 0; i < bands.Length && i < HueBands.Count; i++)
        {
            s.BandStiffness[i] = bands[i].Stiffness.Value;
            s.BandDamping[i] = bands[i].Damping.Value;
            s.BandFollow[i] = bands[i].Follow.Value;
        }
        s.Clamp();
        return s;
    }

    /// <summary>Everything whose change needs a new cage: renderer, softness map, and the structural settings.</summary>
    string BuildKey(SoftbodySettings s) =>
        $"renderer={Renderer.Target?.ReferenceID} channel={SoftnessChannel.Value} invert={InvertSoftness.Value} start={StartAs.Value} {s.StructureKey}";

    string FullKey(SoftbodySettings s) => BuildKey(s) + " | " + s + " | dabs " + Paint.Count + " | budget " + BudgetMs.ToString("R");

    /// <summary>The auto shut-off threshold in effect (the Budget field, clamped to 0.5..50 ms).</summary>
    float BudgetMs => float.IsFinite(Budget.Value) && Budget.Value > 0f ? Math.Clamp(Budget.Value, 0.5f, 50f) : SoftbodyRuntime.DefaultBudgetMs;

    // ------------------------------------------------------------------ status (RawOutputs: local only)

    void ThrottledFlush(float dt)
    {
        _statusTimer -= dt;
        if (_statusTimer > 0 && _state == _flushedState) return;
        _statusTimer = 0.5;
        FlushStatus();
    }

    /// <summary>Writes the RawOutputs. Only allowed from this component's own update/change/start callbacks.</summary>
    void FlushStatus()
    {
        _flushedState = _state;
        Set(Status, _state);
        Set(LastError, _error);
        Set(Effective, _settings?.ToString() ?? "");
        var i = _inst;
        Set(Points, i?.PointCount ?? 0);
        Set(Vertices, i?.VertexCount ?? 0);
        Set(MovingVertices, i?.MovingVerts ?? 0);
        Set(SimMs, i != null ? (float)Math.Round(i.EmaSimMs, 3) : 0f);
        Set(FrameMs, i != null ? (float)Math.Round(i.EmaTotalMs, 3) : 0f);
        Set(Resets, i?.Resets ?? 0);
        Set(Tunes, i?.Tunes ?? 0);
        int dabs = 0;
        try { dabs = Paint.Count; } catch { }
        Set(Dabs, dabs);
    }

    static void Set<T>(RawOutput<T> field, T value)
    {
        try { if (field != null && field.CanWrite) field.Value = value; } catch { }
    }

    void UpdateSnapshot(string stats = null)
    {
        try
        {
            var sb = new StringBuilder("{");
            sb.Append("\"name\":").Append(StatsServer.Json(LogName));
            sb.Append(",\"refId\":").Append(StatsServer.Json(ReferenceID.ToString()));
            sb.Append(",\"world\":").Append(StatsServer.Json(World?.Name));
            sb.Append(",\"state\":").Append(StatsServer.Json(_state));
            sb.Append(",\"lastError\":").Append(StatsServer.Json(_error));
            sb.Append(",\"renderer\":").Append(StatsServer.Json(_inst?.Name));
            try { sb.Append(",\"dabs\":").Append(Paint.Count); } catch { }
            if (stats != null) sb.Append(',').Append(stats);
            else if (_inst != null)
            {
                var ic = CultureInfo.InvariantCulture;
                sb.Append(",\"points\":").Append(_inst.PointCount).Append(",\"verts\":").Append(_inst.VertexCount)
                  .Append(",\"avgFrameMs\":").Append(_inst.EmaTotalMs.ToString("0.000", ic)).Append(",\"note\":\"full stats every 5 s\"");
            }
            sb.Append(",\"effective\":").Append(StatsServer.Json(_settings?.ToString()));
            sb.Append('}');
            SnapshotJson = sb.ToString();
        }
        catch { }
    }
}
