using System.Globalization;
using System.Text;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Cloth;

namespace Softbody;

/// <summary>
/// 0.29.0: cloth on a MeshRenderer (world cloth: flags, banners, curtains, tablecloths, blankets, sails), simulated by
/// each client (see DESIGN-cloth.md and <see cref="ClothInstance"/>). It starts all pinned, so nothing moves: paint it
/// with the Softbody brush (white hangs free, black stays put), hold points with Anchors, blow on it with a Cloth Wind,
/// and turn on Tearable to let it rip. Settings apply live; the renderer, StartAs and the anchors rebuild it.
///
/// 0.30.0: also on a SkinnedMeshRenderer (avatar cloth: skirts, capes, coats, scarves, ribbons): the painted part follows
/// the animation and hangs from it, and the rest of the mesh is left to the renderer (see ClothInstance.Skinned.cs).
/// Painting more of it free rebuilds it a moment after the stroke.
///
/// Synced and saved: the settings, the paint and the tears. Each client simulates its own folds; the tears are the same
/// for everyone (one client decides them and the rest replay the list).
///
/// 0.31.0: a <see cref="ClothBlast"/> fired near it adds a <see cref="Blast"/> to the synced Blasts list: a push outward
/// for everyone, and on world cloth the points it frees and the pieces it shreds (as tears). Mend undoes both.
///
/// 0.32.0: WaitForBlast keeps it asleep (the plain mesh: nothing built or simulated) until a Cloth Blast hits it; then it
/// wakes with that blast, and a Mend puts it back to sleep.
///
/// 0.33.0: MendTimer mends it on its own, that many seconds after it tears. DuplicateOnMend: a Mend, or turning the
/// component off, leaves the loose pieces behind as a copy (a <see cref="ClothDebris"/>, on each client its own), while the
/// original comes back whole; CopyLifetime is how long a copy stays.
/// </summary>
[Category(new[] { "Physics" })]
[DefaultUpdateOrder(1000)]
public class Cloth : Component, IPaintable
{
    SyncList<Softbody.PaintDab> IPaintable.PaintDabs => Paint;
    IBrushTarget IPaintable.BrushTarget => _inst;

    /// <summary>What the paint starts from. AllPinned (the default): nothing moves until parts are painted white.
    /// AllFree: all of it hangs free (it falls unless anchored). MeshColors: the mesh's own vertex colours.</summary>
    public enum StartPaint { AllPinned, AllFree, MeshColors }

    // ---- what (changes rebuild)
    /// <summary>The MeshRenderer to make cloth (with a StaticMesh; bake a procedural mesh first). Defaults to one on this
    /// slot or under it.</summary>
    public readonly SyncRef<MeshRenderer> Renderer;
    public readonly Sync<StartPaint> StartAs;
    /// <summary>Slots that hold the points near them (within AnchorRadius when the cloth is built). Put a Grabbable on one to
    /// drag a corner, or two on poles to hang a banner.</summary>
    public readonly SyncRefList<Slot> Anchors;
    /// <summary>Metres: how far from an anchor its points are taken (0.001..1).</summary>
    public readonly Sync<float> AnchorRadius;
    /// <summary>0..1: how much of the simulation is shown: 1 all of it, 0 off (the mesh as modelled, nothing simulated).</summary>
    public readonly Sync<float> Simulating;
    /// <summary>0.32.0: asleep until a Cloth Blast hits it. Until then it is the plain mesh: nothing is built or simulated, so
    /// it costs nothing (a sheet that stays put until it is blown apart). The first blast wakes it, with that blast's push,
    /// freed points and shred; a Mend puts it back to sleep. To paint it, turn this off first.</summary>
    public readonly Sync<bool> WaitForBlast;

    // ---- the fabric (live)
    /// <summary>g/m²: silk is under 100, shirting 100..150, denim 250..350.</summary>
    public readonly Sync<float> Weight;
    /// <summary>0: doesn't stretch; 1: rubbery.</summary>
    public readonly Sync<float> Stretch;
    /// <summary>0: drapes like silk; 1: stiff like leather.</summary>
    public readonly Sync<float> Bend;
    /// <summary>0..1: how fast motion dies down.</summary>
    public readonly Sync<float> Damping;
    /// <summary>Air drag per triangle (0..5).</summary>
    public readonly Sync<float> Drag;
    /// <summary>Air lift per triangle (0..5).</summary>
    public readonly Sync<float> Lift;
    /// <summary>Gravity multiplier (-10..10).</summary>
    public readonly Sync<float> Gravity;
    public readonly Sync<float> Friction;
    /// <summary>Metres: how close it gets to colliders.</summary>
    public readonly Sync<float> Thickness;
    /// <summary>Steps per second (240..1440): more is stiffer and steadier, and costs more.</summary>
    public readonly Sync<int> StepRate;
    /// <summary>Metres, 0 = no limit: a free point stays within its brightness times this of its rest place (on an avatar,
    /// of where the animation puts it).</summary>
    public readonly Sync<float> MaxDistance;
    /// <summary>0.30.0, avatar cloth (0..1): how much of the body's motion the free cloth feels late, as real weight. 0 moves
    /// it with the body at once (stiff and light), 1 leaves all of it behind to swing back. A snap turn is always carried
    /// whole, and a jump of more than 0.75 m (a teleport, a respawn) puts the cloth back where the animation has it.</summary>
    public readonly Sync<float> Inertia;
    /// <summary>0.30.0, avatar cloth: metres a free point may go behind the body's surface as animated (the cloth's own place
    /// on the moving avatar) before it is held there, so it stays out of the body. 0 = no backstop.</summary>
    public readonly Sync<float> Backstop;
    /// <summary>Solid world colliders (CharacterCollider ones).</summary>
    public readonly Sync<bool> Collide;
    /// <summary>Players' bodies and hands too (with Collide). 0.29.2: as spheres fitted just inside their avatars' skinned
    /// meshes, moving with their bones (their bone colliders while those are being built, or when a mesh can't be read).</summary>
    public readonly Sync<bool> CollidePlayers;

    // ---- tearing
    public readonly Sync<bool> Tearable;
    /// <summary>An edge tears once it is longer than this times its rest length (1.05..10).</summary>
    public readonly Sync<float> TearAt;
    /// <summary>0..1: how much a fresh tear weakens the edges next to it, so a rip runs along a line instead of fraying.</summary>
    public readonly Sync<float> TearSpread;
    /// <summary>0.33.0: seconds after it first tears (or a blast hits it) until it mends itself, 0 = never (0..3600). The
    /// client that decides the tears mends it, for everyone.</summary>
    public readonly Sync<float> MendTimer;
    /// <summary>0.33.0, world cloth: a Mend (the button or MendTimer), or turning this component off, leaves the loose pieces
    /// behind as a copy (every piece no pinned or anchored point holds), while the original comes back whole. Each client
    /// keeps its own copy, which falls, settles and stays for CopyLifetime; never synced or saved.</summary>
    public readonly Sync<bool> DuplicateOnMend;
    /// <summary>0.33.0: seconds a left-behind copy stays, 0 = until the world closes (0..3600). At most 4 copies of one cloth
    /// at a time (a fifth removes the oldest); removing the Cloth removes its copies.</summary>
    public readonly Sync<float> CopyLifetime;

    // ---- running it
    /// <summary>Auto shut-off: if this component's world-thread cost per frame, averaged over a minute, goes above this many ms
    /// (or over 4 times it for 10 s), it pauses (0.5..50). 0.29.1: a lag spike no longer does.</summary>
    public readonly Sync<float> Budget;
    /// <summary>Metres: past this distance from you it stops moving on this client (0 = never).</summary>
    public readonly Sync<float> CullDistance;
    /// <summary>Shows the cloth's edges: dark pinned, yellow at an anchor, white free, orange to red as they stretch toward
    /// tearing; with DebugColliders, the colliders near it.</summary>
    public readonly Sync<bool> DebugCloth;
    public readonly Sync<bool> DebugColliders;

    // ---- synced, saved
    [HideInInspector]
    public readonly SyncList<Softbody.PaintDab> Paint;
    /// <summary>The tears, in order: (the point split, the other end of its torn edge). Mend empties it. 0.31.0: also a
    /// blast's shred, a part at a time: (-the blast's Id, the part).</summary>
    [HideInInspector]
    public readonly SyncFieldList<int2> Tears;

    /// <summary>0.31.0: one blast that hit this cloth (a <see cref="ClothBlast"/> fired near it), as the client that fired
    /// it saw it, in the renderer slot's local space (it moves with the cloth). Every client pushes its cloth once when the
    /// blast arrives, frees the points whose modelled place is within the radius (Release), and the client that decides
    /// the tears shreds it (Shred, on a Tearable cloth).</summary>
    public class Blast : SyncObject
    {
        /// <summary>1..2^30, picked by the client that fired it; 0 while its values have not arrived.</summary>
        public readonly Sync<int> Id;
        /// <summary>Centre, in the renderer slot's local space.</summary>
        public readonly Sync<float3> Position;
        /// <summary>In the renderer slot's local units.</summary>
        public readonly Sync<float> Radius;
        /// <summary>m/s outward at the centre.</summary>
        public readonly Sync<float> Strength;
        /// <summary>0..1.</summary>
        public readonly Sync<float> Randomness;
        public readonly Sync<bool> Release;
        /// <summary>0..1.</summary>
        public readonly Sync<float> Shred;
    }

    /// <summary>0.31.0: the blasts that hit this cloth, in the order they came. Mend empties it (with the tears).</summary>
    [HideInInspector]
    public readonly SyncList<Blast> Blasts;

    /// <summary>0.31.0: most blasts one cloth keeps (each one's freed points stay free until a Mend).</summary>
    public const int MaxBlasts = 128;

    // ---- status (RawOutput: local, never synced or saved)
    public readonly RawOutput<string> Status;
    public readonly RawOutput<int> Points;
    public readonly RawOutput<int> Triangles;
    public readonly RawOutput<int> Torn;
    public readonly RawOutput<float> StepMs;
    public readonly RawOutput<float> FrameMs;
    public readonly RawOutput<int> Dabs;
    public readonly RawOutput<string> LastError;

    // ---- local runtime state
    ClothInstance _inst;
    ClothSettings _settings;
    string _state = "starting", _error = "", _flushedState, _loggedReason, _blockedKey, _tunedSig;
    float _retryTimer, _paintPoll;
    double _statusTimer, _statsTimer;
    bool _registered, _paintDirty = true;
    /// <summary>0.32.0: this client saw it asleep (WaitForBlast, no blast yet, world in focus) since it last ran, so the
    /// blasts that wake it are new here: they push, and the client that decides tears shreds them.</summary>
    bool _slept;
    /// <summary>0.33.0, MendTimer: the world time the deciding client first saw it torn or blasted (-1: not damaged).</summary>
    double _damagedSince = -1;
    /// <summary>0.33.0: the copies this cloth left behind on this client, oldest first.</summary>
    readonly List<ClothDebris> _copies = new();
    /// <summary>0.33.0: most copies of one cloth at a time (a new one removes the oldest).</summary>
    public const int MaxCopies = 4;
    /// <summary>0.33.0: CopyLifetime's default, in seconds.</summary>
    public const float DefaultCopyLifetime = 30f;
    Slot _debugSlot;
    SoftbodyDebugMesh _debugMesh;
    float _debugTimer, _debugRetry;
    readonly DabMirror _dabs = new();
    internal string SnapshotJson { get; private set; }
    internal int RunningPointCount => _inst?.PointCount ?? 0;
    internal bool IsRunning => _inst != null;

    string LogName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnAwake()
    {
        base.OnAwake();
        var d = new ClothSettings();
        StartAs.Value = StartPaint.AllPinned;
        AnchorRadius.Value = 0.05f;
        Simulating.Value = 1f;
        WaitForBlast.Value = false;
        Weight.Value = d.Weight;
        Stretch.Value = d.Stretch;
        Bend.Value = d.Bend;
        Damping.Value = d.Damping;
        Drag.Value = d.Drag;
        Lift.Value = d.Lift;
        Gravity.Value = d.Gravity;
        Friction.Value = d.Friction;
        Thickness.Value = d.Thickness;
        StepRate.Value = d.StepRate;
        MaxDistance.Value = d.MaxDistance;
        Inertia.Value = d.Inertia;
        Backstop.Value = d.Backstop;
        Collide.Value = true;
        CollidePlayers.Value = true;
        Tearable.Value = false;
        TearAt.Value = d.TearAt;
        TearSpread.Value = d.TearSpread;
        MendTimer.Value = 0f;
        DuplicateOnMend.Value = false;
        CopyLifetime.Value = DefaultCopyLifetime;
        Budget.Value = SoftbodyRuntime.DefaultBudgetMs;
        CullDistance.Value = d.CullDistance;
        DebugCloth.Value = false;
        DebugColliders.Value = false;
    }

    protected override void OnAttach()
    {
        base.OnAttach();
        try
        {
            MeshRenderer pick = null;
            foreach (var r in Slot.GetComponentsInChildren<MeshRenderer>())
            {
                if (r == null) continue;
                if (r.GetType() == typeof(MeshRenderer)) { pick = r; break; }
                pick ??= r;
            }
            Renderer.Target = pick;
            // 0.30.0: on an avatar the free cloth stays near where the animation puts it
            if (pick is SkinnedMeshRenderer) MaxDistance.Value = AvatarMaxDistance;
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' Cloth OnAttach: {ex.Message}"); }
    }

    /// <summary>0.30.0: MaxDistance when the Cloth is attached to a SkinnedMeshRenderer.</summary>
    public const float AvatarMaxDistance = 0.15f;

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
                _paintDirty = true;
                ApplySettings();
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch (Exception ex) { Report(ex, "OnChanges"); }
    }

    void ApplySettings()
    {
        _settings = ReadSettings();
        if (!Enabled) { Stop("disabled", leaveCopy: true); _blockedKey = null; _state = "disabled"; return; }
        if (_inst != null)
        {
            if (BuildKey() != _inst.BuildKey) { Stop("settings changed: rebuilding"); _retryTimer = 0; _state = "rebuilding"; }
            else
            {
                // only when a setting the simulation takes changed (a driven field that is not one must not stall the world thread)
                string sig = _settings.ToString();
                if (sig != _tunedSig) { _tunedSig = sig; _inst.Tune(_settings); }
            }
        }
        else if (_blockedKey != null && _blockedKey != FullKey()) { _blockedKey = null; _retryTimer = 0; _state = "retrying"; }
    }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try { lock (SoftbodyRuntime.Gate) Frame(); }
        catch (Exception ex) { Report(ex, "frame"); }
    }

    protected override void OnEnabled() { base.OnEnabled(); _retryTimer = 0; }
    protected override void OnDisabled() { base.OnDisabled(); Guarded("disabled", () => { _blockedKey = null; _state = "disabled"; }, leaveCopy: true); }
    protected override void OnActivated() { base.OnActivated(); _retryTimer = 0; }
    protected override void OnDeactivated() { base.OnDeactivated(); Guarded("slot inactive", () => _state = "paused: slot inactive"); }

    protected override void OnDestroy()
    {
        Guarded("component removed", () =>
        {
            if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; }
            EndCopies("its Cloth was removed");
        });
        base.OnDestroy();
    }

    public override void OnWorldDestroy()
    {
        Guarded("world closed", () => { if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; } });
        // no base call: the engine's ComponentBase.OnWorldDestroy throws by design (only overrides may run; 0.26.2)
    }

    public override void OnFocusChanged(FrooxEngine.World.WorldFocus focus)
    {
        // no base call: the engine's ComponentBase.OnFocusChanged throws by design (only overrides may run; 0.26.2)
        if (focus != FrooxEngine.World.WorldFocus.Focused) Guarded("world lost focus", () => _state = "paused: world not focused (only the focused world simulates)");
        else _retryTimer = 0;
    }

    void Guarded(string why, Action after, bool leaveCopy = false)
    {
        try { lock (SoftbodyRuntime.Gate) { Stop(why, leaveCopy); after?.Invoke(); UpdateSnapshot(); } }
        catch (Exception ex) { try { SoftbodyRuntime.Warn($"'{LogName}' {why}: {ex}"); } catch { } }
    }

    // ------------------------------------------------------------------ per frame (Gate held)

    void Frame()
    {
        float dt = World.Time.Delta;
        if (!float.IsFinite(dt) || dt <= 0) return;
        dt = MathF.Min(dt, 0.1f);
        // 0.33.0: the lists are synced, so this runs in a world out of focus too
        MendTimerFrame();
        if (World.Focus != FrooxEngine.World.WorldFocus.Focused)
        {
            if (_inst != null) Stop("world not focused");
            // 0.32.0: a blast that comes while this user is in another world is an old one by the time they are back
            _slept = false;
            _state = "paused: world not focused (only the focused world simulates)";
            ThrottledFlush(dt);
            return;
        }
        // 0.32.0: asleep until a blast hits it (the renderer keeps its own mesh; nothing is built)
        if (WaitForBlast.Value && !HasBlast())
        {
            // 0.33.0: back to sleep after a Mend (or WaitForBlast turned on): DuplicateOnMend leaves the loose pieces behind
            if (_inst != null) Stop("asleep until a Cloth Blast hits it", leaveCopy: true);
            _slept = true;
            _retryTimer = 0;
            _blockedKey = null;   // it starts fresh when it wakes (sleeping is a restart, like a changed setting)
            _state = "asleep: waiting for a Cloth Blast (nothing is simulated until one hits it)";
            ThrottledFlush(dt);
            return;
        }
        if (_inst == null)
        {
            // 0.33.0: a copy being left behind still holds the renderer's mesh (for a frame or two)
            if (CopyHandingOver())
            {
                _state = "mending: leaving the torn copy behind";
                ThrottledFlush(dt);
                return;
            }
            if (_blockedKey == null) { _retryTimer -= dt; if (_retryTimer <= 0) TryStart(); }
            if (_inst == null) { ThrottledFlush(dt); return; }
        }

        PaintFrame(dt);

        SoftbodyDebugMesh debug = null;
        bool showCloth = DebugCloth.Value, showColliders = DebugColliders.Value;
        if (showCloth || showColliders) debug = DebugTarget(dt);
        else DestroyDebug();

        float sim = float.IsFinite(Simulating.Value) ? Math.Clamp(Simulating.Value, 0f, 1f) : 1f;
        var result = _inst.Frame(World, dt, sim, debug, showColliders);
        switch (result)
        {
            case ClothInstance.FrameResult.Gone:
                Stop("renderer, mesh or its slot was removed");
                _state = "waiting: the renderer, its mesh or its slot was removed";
                _retryTimer = 1f;
                break;
            case ClothInstance.FrameResult.Rebuild:
                string why = _inst.RebuildReason ?? "mesh changed";
                // a Mend, and 0.30.0 an avatar cloth taking new paint, are asked for: never counted against the limit
                if (why != "mended" && why != ClothInstance.PaintRebuild && TooManyRebuilds())
                {
                    Block($"paused: it had to rebuild {MaxAutoRebuilds} times within a minute ({why}). Change any setting (or toggle Enabled) to retry");
                    _autoRebuilds = 0;
                    break;
                }
                // 0.33.0: a Mend leaves the loose pieces behind (DuplicateOnMend)
                Stop(why + ": rebuilding", leaveCopy: why == "mended");
                _state = "rebuilding: " + why;
                _retryTimer = 0;
                break;
            case ClothInstance.FrameResult.Paused:
                _state = "paused: renderer disabled or its slot inactive";
                if (_debugSlot != null && _debugSlot.ActiveSelf) _debugSlot.ActiveSelf = false;
                break;
            case ClothInstance.FrameResult.Failed:
                Block(_inst.FailReason ?? "stopped");
                break;
            default:
                if (_debugSlot != null && !_debugSlot.ActiveSelf) _debugSlot.ActiveSelf = true;
                // 0.29.1: only a sustained overrun pauses it (BudgetWatch: a minute's average over Budget, or 10 s over 4x);
                // one long frame or a lag spike no longer does
                string over = _inst.Watch.Verdict(BudgetMs);
                if (over != null)
                    Block($"paused: {over}. Raise Budget, use a lower-poly mesh or a lower StepRate, or change any setting to retry");
                else _state = _inst.RunningState + _inst.Watch.Note(BudgetMs) + CopiesNote();
                break;
        }

        _statsTimer += dt;
        if (_statsTimer >= SoftbodyRuntime.StatsInterval)
        {
            if (_inst != null) SoftbodyRuntime.Log(_inst.StatsLine(_statsTimer));
            UpdateSnapshot();
            _statsTimer = 0;
        }
        ThrottledFlush(dt);
    }

    const int MaxAutoRebuilds = 6;
    int _autoRebuilds;
    long _autoRebuildsSince;

    bool TooManyRebuilds()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_autoRebuilds == 0 || (now - _autoRebuildsSince) / (double)System.Diagnostics.Stopwatch.Frequency > 60.0) { _autoRebuilds = 0; _autoRebuildsSince = now; }
        return ++_autoRebuilds >= MaxAutoRebuilds;
    }

    void PaintFrame(float dt)
    {
        _paintPoll -= dt;
        if (!_paintDirty && _paintPoll > 0f) return;
        _paintDirty = false;
        _paintPoll = 1f;
        _inst.UpdatePaint(ReadDabs(_inst.Profile));
    }

    internal ReadOnlySpan<Dab> ReadDabs(ColorProfile profile) => _dabs.Read(Paint, profile);

    /// <summary>Removes every paint dab (back to StartAs). Also a button in the inspector.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void ClearPaint()
    {
        try { Paint.Clear(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' ClearPaint: {ex.Message}"); }
    }

    /// <summary>Spawns a Softbody brush in front of whoever presses this.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void SpawnBrush()
    {
        try { SoftbodyBrush.SpawnFor(World); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' SpawnBrush: {ex.Message}"); }
    }

    /// <summary>Puts the cloth back together (empties the tear list; every client rebuilds it as modelled). 0.31.0: also
    /// forgets the blasts, so the points they freed are held again. 0.32.0: with WaitForBlast it goes back to sleep.
    /// 0.33.0: MendTimer calls it too; with DuplicateOnMend each client leaves the loose pieces behind as a copy.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void Mend()
    {
        try { Tears.Clear(); Blasts.Clear(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' Mend: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ 0.33.0: MendTimer and the copies left behind (Gate held)

    float MendAfter => float.IsFinite(MendTimer.Value) ? Math.Clamp(MendTimer.Value, 0f, 3600f) : 0f;
    float CopyLifetimeValue => float.IsFinite(CopyLifetime.Value) ? Math.Clamp(CopyLifetime.Value, 0f, 3600f) : DefaultCopyLifetime;

    /// <summary>MendTimer: the client that decides the tears mends the cloth that many seconds after it first saw it torn or
    /// blasted (for everyone: the emptied lists sync).</summary>
    void MendTimerFrame()
    {
        float after = MendAfter;
        if (!(after > 0f) || !Damaged() || !DecidesMend()) { _damagedSince = -1; return; }
        double now;
        try { now = World.Time.WorldTime; } catch { return; }
        if (_damagedSince < 0 || now < _damagedSince) { _damagedSince = now; return; }
        if (now - _damagedSince < after) return;
        _damagedSince = -1;
        SoftbodyRuntime.Log($"'{LogName}' MendTimer: mending it {after:0.#} s after it tore");
        Mend();
    }

    /// <summary>Torn, or hit by a blast.</summary>
    bool Damaged()
    {
        try { if (Tears.Count > 0) return true; } catch { }
        return HasBlast();
    }

    /// <summary>The client that decides the tears (an avatar cloth: its wearer) also mends it.</summary>
    bool DecidesMend()
    {
        try
        {
            if (Renderer.Target is SkinnedMeshRenderer smr && smr.Slot != null)
            {
                var wearer = smr.Slot.ActiveUser;
                if (wearer != null && !wearer.IsRemoved) return ReferenceEquals(wearer, World.LocalUser);
            }
        }
        catch { }
        return ClothInstance.DecidesTears(World);
    }

    /// <summary>DuplicateOnMend: leaves the stopping cloth's loose pieces behind as a copy (this client only). False when
    /// nothing was left; then the caller takes the cloth down as usual.</summary>
    bool LeaveCopy(ClothInstance inst, out string note)
    {
        note = null;
        try
        {
            if (IsRemoved || IsDestroyed || World == null || World.IsDestroyed || World.Focus != FrooxEngine.World.WorldFocus.Focused) return false;
            var copy = ClothDebris.Leave(inst, CopyLifetimeValue, out note);
            if (copy == null) return false;
            _copies.RemoveAll(c => c == null || c.IsRemoved);
            while (_copies.Count >= MaxCopies)
            {
                var old = _copies[0];
                _copies.RemoveAt(0);
                try { old.End($"a newer copy of the same cloth (at most {MaxCopies})"); } catch { }
            }
            _copies.Add(copy);
            return true;
        }
        catch (Exception ex) { note = "leaving a copy failed: " + ex.Message; return false; }
    }

    /// <summary>A copy this cloth left still holds the renderer's mesh: the cloth must not start again yet.</summary>
    bool CopyHandingOver()
    {
        foreach (var c in _copies) if (c != null && !c.IsRemoved && c.HandingOver) return true;
        return false;
    }

    void EndCopies(string why)
    {
        foreach (var c in _copies) { try { if (c != null && !c.IsRemoved) c.End(why); } catch { } }
        _copies.Clear();
    }

    string CopiesNote()
    {
        int n = 0;
        foreach (var c in _copies) if (c != null && !c.IsRemoved) n++;
        return n == 0 ? "" : n == 1 ? "; 1 copy left behind" : $"; {n} copies left behind";
    }

    /// <summary>0.32.0: a blast with its values in (one that wakes a cloth with WaitForBlast).</summary>
    bool HasBlast()
    {
        try { foreach (var b in Blasts) if (b != null && b.Id.Value > 0 && b.Radius.Value > 0f) return true; }
        catch { }
        return false;
    }

    /// <summary>0.32.0 (world thread, Gate held): WaitForBlast and no blast yet, so nothing runs (the Cloth Blast reports
    /// the cloths it wakes).</summary>
    internal bool Asleep => WaitForBlast.Value && _inst == null && !HasBlast();

    /// <summary>0.31.0 (world thread, Gate held): roughly where the cloth is in the world: its simulation's box when it runs
    /// here, else its mesh's bounds through the renderer's slot. For a blast to find the cloths near it.</summary>
    internal bool WorldBox(out System.Numerics.Vector3 lo, out System.Numerics.Vector3 hi)
    {
        lo = hi = default;
        try
        {
            if (_inst != null && _inst.TryWorldBox(out lo, out hi)) return true;
            var r = Renderer.Target;
            if (r == null || r.IsRemoved || r.Slot == null) return false;
            var data = (r.Mesh.Target as StaticMesh)?.Asset?.Data;
            if (data == null || data.VertexCount == 0) return false;
            var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
            var raw = data.RawPositions;
            for (int i = 0; i < data.VertexCount; i++)
            {
                var p = new System.Numerics.Vector3(raw[i].x, raw[i].y, raw[i].z);
                if (!float.IsFinite(p.X + p.Y + p.Z)) continue;
                min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p);
            }
            if (min.X > max.X) return false;
            global::Softbody.Core.Flesh.FleshCollider.TransformBox(min, max, SkinMatrices.ToNumerics(r.Slot.LocalToGlobal), out lo, out hi);
            return float.IsFinite(lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ debug view (Gate held)

    SoftbodyDebugMesh DebugTarget(float dt)
    {
        if (_debugRetry > 0f) { _debugRetry -= dt; return null; }
        try
        {
            if (_debugSlot == null || _debugSlot.IsRemoved || _debugMesh == null || _debugMesh.IsRemoved) CreateDebug();
            _debugTimer -= dt;
            if (_debugTimer > 0f) return null;
            _debugTimer = MathF.Max(1f / 30f, _debugMesh.LastVertexCount * 28f / 40e6f);
            return _debugMesh;
        }
        catch (Exception ex)
        {
            SoftbodyRuntime.Warn($"'{LogName}' debug view failed, trying again in 5 s: {ex}");
            DestroyDebug();
            _debugRetry = 5f;
            return null;
        }
    }

    void CreateDebug()
    {
        DestroyDebug();
        var slot = World.RootSlot.AddLocalSlot("Cloth debug view (local): " + LogName, false);
        var mesh = slot.AttachComponent<SoftbodyDebugMesh>();
        var mat = slot.AttachComponent<OverlayUnlitMaterial>();
        mat.BlendMode.Value = BlendMode.Alpha;
        mat.UseVertexColors.Value = true;
        mat.Sidedness.Value = Sidedness.Double;
        mat.ZWrite.Value = ZWrite.Off;
        mat.FrontTintColor.Value = colorX.White;
        mat.BehindTintColor.Value = new colorX(1f, 1f, 1f, 0.35f);
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = mat;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _debugSlot = slot; _debugMesh = mesh; _debugTimer = 0f;
    }

    void DestroyDebug()
    {
        var slot = _debugSlot;
        _debugSlot = null; _debugMesh = null;
        if (slot == null) return;
        try { if (!slot.IsRemoved && !slot.IsDisposed) slot.Destroy(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' removing the debug view: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ start / stop

    void TryStart()
    {
        var s = _settings ??= ReadSettings();
        SoftbodyInstance.CreateResult res;
        ClothInstance inst = null;
        string reason;
        try
        {
            res = ClothInstance.TryCreate(this, Renderer.Target, s, StartAs.Value, ReadAnchors(), AnchorRadiusValue, BuildKey(), out inst, out reason);
        }
        catch (Exception ex) { res = SoftbodyInstance.CreateResult.Fail; reason = "create threw " + ex.GetType().Name + ": " + ex.Message; }
        switch (res)
        {
            case SoftbodyInstance.CreateResult.Created:
                _inst = inst;
                // 0.32.0: woken by its first blasts, which are new here (a client that loads it already blasted is not)
                bool woke = _slept;
                _slept = false;
                if (woke) inst.TakeBlastsAsNew(World);
                _tunedSig = s.ToString();
                _state = inst.RunningState;
                _error = ""; _loggedReason = null; _statsTimer = 0;
                SoftbodyRuntime.Log($"'{LogName}' Cloth started on renderer '{inst.Name}': {inst.Describe()}; settings {s}");
                break;
            case SoftbodyInstance.CreateResult.Wait:
                _state = "waiting: " + reason;
                _retryTimer = 1f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Log($"'{LogName}' Cloth waiting: {reason}"); }
                break;
            default:
                _state = "error: " + reason;
                _error = reason;
                _retryTimer = 5f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Warn($"'{LogName}' Cloth cannot start: {reason}"); }
                break;
        }
        UpdateSnapshot();
    }

    /// <summary>Takes the running cloth down. 0.33.0: with <paramref name="leaveCopy"/> (a Mend, or turned off) and
    /// DuplicateOnMend, its loose pieces are left behind as a copy instead, and the renderer gets its mesh back from that.</summary>
    void Stop(string why, bool leaveCopy = false)
    {
        DestroyDebug();
        if (_inst == null) return;
        var inst = _inst;
        _inst = null;
        string note = null;
        bool left = leaveCopy && DuplicateOnMend.Value && LeaveCopy(inst, out note);
        if (!left)
        {
            try { inst.Teardown(); }
            catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' teardown: {ex.Message}"); }
        }
        SoftbodyRuntime.Release(inst.ClaimKey, this);
        SoftbodyRuntime.Log($"'{LogName}' Cloth stopped: {why}" + (note != null ? $" ({note})" : ""));
    }

    void Block(string state, bool warn = true)
    {
        Stop("paused (see Status)");
        _blockedKey = FullKey();
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
                SoftbodyRuntime.Warn($"'{LogName}' Cloth {where} threw: {ex}");
                Block("error: " + _error + ". Change any setting (or toggle Enabled) to retry", warn: false);
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ settings

    ClothSettings ReadSettings()
    {
        var s = new ClothSettings
        {
            Weight = Weight.Value,
            Stretch = Stretch.Value,
            Bend = Bend.Value,
            Damping = Damping.Value,
            Drag = Drag.Value,
            Lift = Lift.Value,
            Gravity = Gravity.Value,
            Friction = Friction.Value,
            Thickness = Thickness.Value,
            StepRate = StepRate.Value,
            MaxDistance = MaxDistance.Value,
            Inertia = Inertia.Value,
            Backstop = Backstop.Value,
            Collide = Collide.Value,
            Tearable = Tearable.Value,
            TearAt = TearAt.Value,
            TearSpread = TearSpread.Value,
            CullDistance = CullDistance.Value,
        };
        s.Sanitize();
        return s;
    }

    Slot[] ReadAnchors()
    {
        var list = new List<Slot>();
        try { foreach (var s in Anchors) list.Add(s); }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("cloth-anchors:" + ex.Message, $"'{LogName}' reading Anchors: {ex.Message}"); }
        return list.ToArray();
    }

    float AnchorRadiusValue => float.IsFinite(AnchorRadius.Value) ? Math.Clamp(AnchorRadius.Value, 0.001f, 1f) : 0.05f;

    string BuildKey()
    {
        var sb = new StringBuilder();
        sb.Append("renderer=").Append(Renderer.Target?.ReferenceID).Append(" start=").Append(StartAs.Value)
          .Append(" radius=").Append(AnchorRadiusValue.ToString("R", CultureInfo.InvariantCulture)).Append(" anchors=");
        try { foreach (var s in Anchors) sb.Append(s?.ReferenceID.ToString() ?? "null").Append(','); } catch { }
        return sb.ToString();
    }

    string FullKey() => BuildKey() + " | " + (_settings ?? ReadSettings()) + " | dabs " + Paint.Count + " | tears " + Tears.Count +
                        " | budget " + BudgetMs.ToString("R", CultureInfo.InvariantCulture) + " | players " + CollidePlayers.Value;

    float BudgetMs => float.IsFinite(Budget.Value) && Budget.Value > 0f ? Math.Clamp(Budget.Value, 0.5f, 50f) : SoftbodyRuntime.DefaultBudgetMs;

    // ------------------------------------------------------------------ status

    void ThrottledFlush(float dt)
    {
        _statusTimer -= dt;
        if (_statusTimer > 0 && _state == _flushedState) return;
        _statusTimer = 0.5;
        FlushStatus();
    }

    void FlushStatus()
    {
        _flushedState = _state;
        Set(Status, _state);
        Set(LastError, _error);
        var i = _inst;
        Set(Points, i?.PointCount ?? 0);
        Set(Triangles, i?.TriangleCount ?? 0);
        int torn = 0;
        try { torn = Tears.Count; } catch { }
        Set(Torn, torn);
        Set(StepMs, i != null ? (float)Math.Round(i.EmaStepMs, 3) : 0f);
        Set(FrameMs, i != null ? (float)Math.Round(i.EmaMainMs, 3) : 0f);
        int dabs = 0;
        try { dabs = Paint.Count; } catch { }
        Set(Dabs, dabs);
    }

    static void Set<T>(RawOutput<T> field, T value)
    {
        try { if (field != null && field.CanWrite) field.Value = value; } catch { }
    }

    void UpdateSnapshot()
    {
        try
        {
            var sb = new StringBuilder("{");
            sb.Append("\"name\":").Append(StatsServer.Json(LogName));
            sb.Append(",\"type\":\"Cloth\"");
            sb.Append(",\"refId\":").Append(StatsServer.Json(ReferenceID.ToString()));
            sb.Append(",\"world\":").Append(StatsServer.Json(World?.Name));
            sb.Append(",\"state\":").Append(StatsServer.Json(_state));
            sb.Append(",\"lastError\":").Append(StatsServer.Json(_error));
            sb.Append(",\"renderer\":").Append(StatsServer.Json(_inst?.Name));
            try { sb.Append(",\"dabs\":").Append(Paint.Count); } catch { }
            try { sb.Append(",\"tears\":").Append(Tears.Count); } catch { }
            if (_inst != null) sb.Append(",\"points\":").Append(_inst.PointCount).Append(",\"stepMs\":").Append(_inst.EmaStepMs.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"settings\":").Append(StatsServer.Json(_settings?.ToString()));
            sb.Append('}');
            SnapshotJson = sb.ToString();
        }
        catch { }
    }
}
