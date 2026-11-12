using System.Globalization;
using System.Numerics;
using System.Text;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Flesh;

namespace Softbody;

/// <summary>
/// Skinned softbody (0.11.0): soft flesh on an avatar's SkinnedMeshRenderer, for the painted parts only. The painted
/// region becomes a shell of tetrahedra under the skin (each surface point a real vertex, carrying its own bone weights
/// and blendshapes, the inner layer pinned to the bones), simulated with XPBD and shown as bind-space offsets the
/// renderer skins with everything else (see DESIGN-skinned.md and <see cref="FleshInstance"/>). The tets are rebuilt only
/// when the mesh, the paint or a shell setting changes; every other setting is live.
///
/// Needs the Softbody renderer (the -RendererPath fork, or the SoftbodyRenderer patch). Synced and saved: the settings and
/// the paint (the same dabs and brush as <see cref="Softbody"/>). Everything the simulation does is local to each client.
///
/// 0.13.0: <see cref="HueLayers"/> adds more soft layers to the same mesh, each a separate simulation with its own
/// settings, for the parts painted in its hue (see <see cref="HueLayer"/>).
/// </summary>
[Category(new[] { "Physics" })]
[DefaultUpdateOrder(1000)]
public class SkinnedSoftbody : Component, IPaintable
{
    SyncList<Softbody.PaintDab> IPaintable.PaintDabs => Paint;
    IBrushTarget IPaintable.BrushTarget => _inst;

    // ---- what to deform (changes rebuild)
    /// <summary>The SkinnedMeshRenderer to make soft. Defaults to one on this slot or under it.</summary>
    public readonly SyncRef<SkinnedMeshRenderer> Renderer;
    public readonly Sync<Softbody.Channel> SoftnessChannel;
    public readonly Sync<bool> InvertSoftness;
    /// <summary>What the paint starts from. AllFirm (the default): nothing moves until parts are painted bright.
    /// MeshColors: the mesh's own vertex colours are the softness map.</summary>
    public readonly Sync<Softbody.StartColor> StartAs;
    /// <summary>
    /// 0..1: how much of the softbody is shown, on top of the paint: 1 full, 0.5 half the jiggle and dents, 0 off (the mesh
    /// as animated, nothing simulated). Live and free to change every frame, so fade it (by hand, or drive it) to turn the
    /// softbody on or off smoothly instead of instantly. Every hue layer follows it.
    /// </summary>
    public readonly Sync<float> Simulating;

    // ---- the shell (changes rebuild the tets)
    /// <summary>Soft surface points the painted region is reduced to (50..4000): more = finer motion, more cost.</summary>
    public readonly Sync<int> SimPoints;
    /// <summary>How deep the soft shell goes, as a fraction of the distance from the skin to the bone (0.05..0.95).</summary>
    public readonly Sync<float> Depth;
    /// <summary>Tet layers through the shell (1..3).</summary>
    public readonly Sync<int> Layers;

    // ---- live
    /// <summary>Young's modulus of the flesh in kPa (soft tissue is roughly 1..50; lower = wobblier).</summary>
    public readonly Sync<float> Stiffness;
    /// <summary>Poisson ratio 0.05..0.49: how strongly it keeps its volume (0.49 = like water).</summary>
    public readonly Sync<float> Volume;
    /// <summary>0..1: how much of the volume a dent pushes in comes back out as a bulge around it (0 = none, 1 = all).</summary>
    public readonly Sync<float> VolumePreservation;
    /// <summary>Extra stiffness toward the bone (0..10): deep flesh is (1 + 3 x this) times stiffer than at the skin.</summary>
    public readonly Sync<float> BoneDensity;
    /// <summary>Hz, how hard soft parts are pulled back to their animated place (0..100).</summary>
    public readonly Sync<float> Follow;
    /// <summary>Per second, how fast the wobble dies down (0..200).</summary>
    public readonly Sync<float> Damping;
    /// <summary>Gravity multiplier (-10..10).</summary>
    public readonly Sync<float> Gravity;
    /// <summary>0..1: how much blendshape changes throw the flesh around (0 = they carry it, only bones make it jiggle).</summary>
    public readonly Sync<float> BlendshapeInertia;
    /// <summary>Solver substeps per frame (1..32).</summary>
    public readonly Sync<int> Substeps;
    /// <summary>Collide at all: solid world colliders (CharacterCollider) and the players below.</summary>
    public readonly Sync<bool> Collide;
    /// <summary>Self-collision: this avatar's own bone colliders (each ignores the flesh it already touches at rest).</summary>
    public readonly Sync<bool> CollideSelf;
    public readonly Sync<bool> SelfHead;
    /// <summary>Torso, arms, legs and feet.</summary>
    public readonly Sync<bool> SelfBody;
    public readonly Sync<bool> SelfHands;
    /// <summary>Other users' bone colliders (near you only: the engine switches them on when users are close).</summary>
    public readonly Sync<bool> CollideOthers;
    public readonly Sync<bool> OthersHead;
    /// <summary>Torso, arms, legs and feet.</summary>
    public readonly Sync<bool> OthersBody;
    public readonly Sync<bool> OthersHands;
    /// <summary>Auto shut-off: if this component's average world-thread cost per frame goes above this many ms, it pauses (0.5..50).</summary>
    public readonly Sync<float> Budget;
    /// <summary>Metres: past this distance from you, this client stops simulating and colliding it and shows it at rest
    /// (0 = never).</summary>
    public readonly Sync<float> CullDistance;

    // ---- 0.13.0: more soft layers, each its own simulation with its own settings, picked by the paint's hue
    /// <summary>The hue a layer owns (the ROYGBIV bands of the paint colour's hue, as for <see cref="Softbody"/>).</summary>
    public enum LayerHue { Red, Orange, Yellow, Green, Blue, Indigo, Violet }

    /// <summary>
    /// 0.13.0: one more soft layer: a separate simulation with its own settings, for the parts painted in its hue. The
    /// paint colour's hue picks the layer and its brightness how soft it is (a colour between two hues is shared by their
    /// layers, so a hue gradient hands one region over to the next). Grey, white and black, and hues no layer owns, stay
    /// with the component's own settings (the base layer). Changing the hue, Enabled or a shell setting rebuilds the tets.
    /// </summary>
    public class HueLayer : SyncObject
    {
        public readonly Sync<LayerHue> Hue;
        /// <summary>Off: as if it were not in the list (its hue goes back to the base layer).</summary>
        public readonly Sync<bool> Enabled;
        /// <summary>0..1, this layer's own Simulating, times the component's: fade it to turn just this layer on or off
        /// smoothly (0: its region as animated, not simulated). Live and free to change every frame.</summary>
        public readonly Sync<float> Simulating;
        public readonly Sync<int> SimPoints;
        public readonly Sync<float> Depth;
        public readonly Sync<int> Layers;
        public readonly Sync<float> Stiffness;
        public readonly Sync<float> Volume;
        public readonly Sync<float> VolumePreservation;
        public readonly Sync<float> BoneDensity;
        public readonly Sync<float> Follow;
        public readonly Sync<float> Damping;
        public readonly Sync<float> Gravity;
        public readonly Sync<float> BlendshapeInertia;
        public readonly Sync<int> Substeps;
        public readonly Sync<bool> Collide;
        public readonly Sync<bool> CollideSelf;
        public readonly Sync<bool> SelfHead;
        public readonly Sync<bool> SelfBody;
        public readonly Sync<bool> SelfHands;
        public readonly Sync<bool> CollideOthers;
        public readonly Sync<bool> OthersHead;
        public readonly Sync<bool> OthersBody;
        public readonly Sync<bool> OthersHands;

        protected override void OnAwake()
        {
            base.OnAwake();
            var d = new FleshSettings();
            Hue.Value = LayerHue.Orange;
            Enabled.Value = true;
            Simulating.Value = 1f;
            SimPoints.Value = 400;
            Depth.Value = d.Depth;
            Layers.Value = d.Layers;
            Stiffness.Value = d.Stiffness;
            Volume.Value = d.Volume;
            VolumePreservation.Value = d.VolumePreservation;
            BoneDensity.Value = d.BoneDensity;
            Follow.Value = d.Follow;
            Damping.Value = d.Damping;
            Gravity.Value = d.Gravity;
            BlendshapeInertia.Value = d.BlendshapeInertia;
            Substeps.Value = d.Substeps;
            Collide.Value = true;
            CollideSelf.Value = true; SelfHead.Value = true; SelfBody.Value = true; SelfHands.Value = true;
            CollideOthers.Value = true; OthersHead.Value = true; OthersBody.Value = true; OthersHands.Value = true;
            // 0.26.5: constants only. OnAwake runs on every client that builds the layer (loading it, copying it, or
            // receiving it over the network), before its saved or synced values arrive. 0.13.0 to 0.26.4 picked the hue
            // here from the other layers' current values, which differ between those moments and clients, so a saved
            // layer could come up one hue on (Lenki's friend: saved orange, loaded yellow). A layer someone adds by hand
            // gets a free hue from SkinnedSoftbody.LayersAdded instead, on that user's own client only.
        }

        /// <summary>This layer's settings (the cull distance is the component's).</summary>
        internal FleshSettings Read(FleshSettings component)
        {
            var s = new FleshSettings
            {
                SimPoints = Math.Clamp(SimPoints.Value, 50, 4000),
                Depth = Depth.Value,
                Layers = Layers.Value,
                Stiffness = Stiffness.Value,
                Volume = Volume.Value,
                BoneDensity = BoneDensity.Value,
                Follow = Follow.Value,
                Damping = Damping.Value,
                Gravity = Gravity.Value,
                BlendshapeInertia = BlendshapeInertia.Value,
                Substeps = Substeps.Value,
                Collide = Collide.Value,
                CollideSelf = CollideSelf.Value, SelfHead = SelfHead.Value, SelfBody = SelfBody.Value, SelfHands = SelfHands.Value,
                CollideOthers = CollideOthers.Value, OthersHead = OthersHead.Value, OthersBody = OthersBody.Value, OthersHands = OthersHands.Value,
                VolumePreservation = VolumePreservation.Value,
                CullDistance = component.CullDistance,
            };
            s.Sanitize();
            return s;
        }
    }

    /// <summary>0.13.0: more soft layers, one per hue (a hue in the list twice: the first one). Empty: just the settings above.</summary>
    public readonly SyncList<HueLayer> HueLayers;

    // ---- debug views, drawn by each client from its own simulation on a local slot
    /// <summary>Shows the tets: grey pinned edges, blue..orange by softness on the skin, light blue through the shell, white
    /// lines where a skin point is pulled away from its place.</summary>
    public readonly Sync<bool> DebugTets;
    /// <summary>Shows the collision spheres (green, yellow touching), the colliders (magenta), the ground (cyan).</summary>
    public readonly Sync<bool> DebugColliders;

    // ---- paint (synced, saved): the same dabs as Softbody, in bind space
    [HideInInspector]
    public readonly SyncList<Softbody.PaintDab> Paint;

    // ---- status (RawOutput: local, never synced or saved)
    public readonly RawOutput<string> Status;
    public readonly RawOutput<int> Points;
    public readonly RawOutput<int> Tets;
    public readonly RawOutput<int> MovingVertices;
    public readonly RawOutput<float> StepMs;
    public readonly RawOutput<float> FrameMs;
    public readonly RawOutput<int> Dabs;
    public readonly RawOutput<string> LastError;

    // ---- local runtime state
    FleshInstance _inst;
    FleshSettings _settings;
    FleshLayerSpec[] _specs;
    float[] _gains = Array.Empty<float>();

    /// <summary>Each layer's Simulating this frame (in the order of <see cref="_specs"/>): the component's, times the hue
    /// layer's own.</summary>
    float[] LayerGains(float global)
    {
        var specs = _specs;
        if (specs == null) return null;
        if (_gains.Length != specs.Length) _gains = new float[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            float g = 1f;
            try { var src = specs[i].Source; if (src != null && !src.IsRemoved) g = src.Simulating.Value; } catch { }
            _gains[i] = global * (float.IsFinite(g) ? Math.Clamp(g, 0f, 1f) : 1f);
        }
        return _gains;
    }
    string _layerSig;
    float _layerPoll;
    string _state = "starting", _error = "", _flushedState, _loggedReason;
    string _blockedKey;
    float _retryTimer, _bandPoll;
    double _statusTimer, _statsTimer;
    bool _registered;
    Slot _debugSlot;
    SoftbodyDebugMesh _debugMesh;
    float _debugTimer, _debugRetry;
    readonly DabMirror _dabs = new();
    bool _paintDirty = true;
    float _paintPoll;
    internal string SnapshotJson { get; private set; }
    internal int RunningPointCount => _inst?.PointCount ?? 0;
    internal bool IsRunning => _inst != null;

    string LogName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnAwake()
    {
        base.OnAwake();
        var d = new FleshSettings();
        SoftnessChannel.Value = Softbody.Channel.Value;
        InvertSoftness.Value = false;
        StartAs.Value = Softbody.StartColor.AllFirm;
        Simulating.Value = 1f;
        SimPoints.Value = d.SimPoints;
        Depth.Value = d.Depth;
        Layers.Value = d.Layers;
        Stiffness.Value = d.Stiffness;
        Volume.Value = d.Volume;
        VolumePreservation.Value = d.VolumePreservation;
        BoneDensity.Value = d.BoneDensity;
        Follow.Value = d.Follow;
        Damping.Value = d.Damping;
        Gravity.Value = d.Gravity;
        BlendshapeInertia.Value = d.BlendshapeInertia;
        Substeps.Value = d.Substeps;
        Collide.Value = true;
        CollideSelf.Value = true; SelfHead.Value = true; SelfBody.Value = true; SelfHands.Value = true;
        CollideOthers.Value = true; OthersHead.Value = true; OthersBody.Value = true; OthersHands.Value = true;
        CullDistance.Value = d.CullDistance;
        Budget.Value = SoftbodyRuntime.DefaultBudgetMs;
        DebugTets.Value = false;
        DebugColliders.Value = false;
        HueLayers.ElementsAdded += LayersAdded;
    }

    bool _startedHere;

    /// <summary>
    /// 0.26.5: a layer added by hand takes the first hue the other layers do not use, starting at orange (pure red is the
    /// colour a Red channel base layer is often painted with), as an ordinary synced change. Only on the client of the
    /// user who added it, and only once this component has started here: loading, copying or receiving layers never
    /// changes their hue (that all happens before OnStart, or arrives from another user).
    /// </summary>
    void LayersAdded(SyncElementList<HueLayer> list, int start, int count)
    {
        try
        {
            if (!_startedHere) return;
            var local = World?.LocalUser;
            if (local == null) return;
            for (int i = start; i < start + count && i < list.Count; i++)
            {
                var layer = list[i];
                if (layer == null || layer.ReferenceID.User != local.AllocationID) continue;
                var used = new bool[HueBands.Count];
                for (int j = 0; j < list.Count; j++)
                {
                    var other = list[j];
                    if (j == i || other == null) continue;
                    int h = (int)other.Hue.Value;
                    if (h >= 0 && h < used.Length) used[h] = true;
                }
                int own = (int)layer.Hue.Value;
                if (own >= 0 && own < used.Length && !used[own]) continue;
                for (int k = 0; k < used.Length; k++)
                {
                    int h = (k + (int)LayerHue.Orange) % used.Length;
                    if (!used[h]) { layer.Hue.Value = (LayerHue)h; break; }
                }
            }
        }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("layers-added:" + ex.Message, $"'{LogName}' picking a new layer's hue: {ex.Message}"); }
    }

    protected override void OnAttach()
    {
        base.OnAttach();
        try { Renderer.Target = Slot.GetComponent<SkinnedMeshRenderer>() ?? Slot.GetComponentInChildren<SkinnedMeshRenderer>(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' SkinnedSoftbody OnAttach: {ex.Message}"); }
    }

    protected override void OnStart()
    {
        base.OnStart();
        _startedHere = true;
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            lock (SoftbodyRuntime.Gate)
            {
                SoftbodyRuntime.Register(this);
                _registered = true;
                _settings = ReadSettings();
                _specs = ReadLayers(_settings);
                _layerSig = LayerSignature();
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

    /// <summary>Reads the settings and layers and hands them to the running simulation, or restarts it when the tets must
    /// be rebuilt (Gate held).</summary>
    void ApplySettings()
    {
        var before = _specs;
        _settings = ReadSettings();
        _specs = ReadLayers(_settings);
        _layerSig = LayerSignature();
        if (!Enabled) { Stop("disabled"); _blockedKey = null; _state = "disabled"; }
        else if (_inst != null)
        {
            if (BuildKey(_specs) != _inst.BuildKey) { Stop("settings changed: rebuilding"); _retryTimer = 0; _state = "rebuilding"; }
            // only when a setting the simulation takes changed: tuning waits for the running step, and a field that is
            // driven every frame (Simulating, or a fade on anything else that is not a setting) must not stall the world thread
            else if (before == null || DescribeLayers(before) != DescribeLayers(_specs)) _inst.Tune(_specs);
        }
        else if (_blockedKey != null && _blockedKey != FullKey(_specs)) { _blockedKey = null; _retryTimer = 0; _state = "retrying"; }
    }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try { lock (SoftbodyRuntime.Gate) Frame(); }
        catch (Exception ex) { Report(ex, "frame"); }
    }

    protected override void OnEnabled() { base.OnEnabled(); _retryTimer = 0; }
    protected override void OnDisabled() { base.OnDisabled(); Guarded("disabled", () => { _blockedKey = null; _state = "disabled"; }); }
    protected override void OnActivated() { base.OnActivated(); _retryTimer = 0; }
    protected override void OnDeactivated() { base.OnDeactivated(); Guarded("slot inactive", () => _state = "paused: slot inactive"); }

    protected override void OnDestroy()
    {
        Guarded("component removed", () => { if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; } });
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

    void Guarded(string why, Action after)
    {
        try { lock (SoftbodyRuntime.Gate) { Stop(why); after?.Invoke(); UpdateSnapshot(); } }
        catch (Exception ex) { try { SoftbodyRuntime.Warn($"'{LogName}' {why}: {ex}"); } catch { } }
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
        // changes inside the layers' list elements (OnChanges may not see them all)
        _layerPoll -= dt;
        if (_layerPoll <= 0f)
        {
            _layerPoll = 0.5f;
            if (LayerSignature() != _layerSig) ApplySettings();
        }
        if (_inst == null)
        {
            if (_blockedKey == null) { _retryTimer -= dt; if (_retryTimer <= 0) TryStart(); }
            if (_inst == null) { ThrottledFlush(dt); return; }
        }

        PaintFrame(dt);

        // the debug view: the instance hands it a snapshot between two steps
        SoftbodyDebugMesh debug = null;
        bool tets = DebugTets.Value, colliders = DebugColliders.Value;
        if (tets || colliders) debug = DebugTarget(dt);
        else DestroyDebug();

        float sim = float.IsFinite(Simulating.Value) ? Math.Clamp(Simulating.Value, 0f, 1f) : 1f;
        var result = _inst.Frame(World, dt, debug, tets, colliders, sim, LayerGains(sim));
        switch (result)
        {
            case FleshInstance.FrameResult.Gone:
                Stop("renderer, mesh or its slot was removed");
                _state = "waiting: the renderer, its mesh or its slot was removed";
                _retryTimer = 1f;
                break;
            case FleshInstance.FrameResult.Rebuild:
                string why = _inst.RebuildReason ?? "mesh changed";
                if (TooManyRebuilds())
                {
                    Block($"paused: it had to rebuild {MaxAutoRebuilds} times within a minute ({why}), so something keeps changing the renderer under it. Change any setting (or toggle Enabled) to retry");
                    _autoRebuilds = 0;
                    break;
                }
                Stop(why + ": rebuilding");
                _state = "rebuilding: " + why;
                _retryTimer = 0;
                break;
            case FleshInstance.FrameResult.Paused:
                _state = "paused: renderer disabled or its slot inactive";
                if (_debugSlot != null && _debugSlot.ActiveSelf) _debugSlot.ActiveSelf = false;
                break;
            case FleshInstance.FrameResult.Failed:
                Block(_inst.FailReason ?? "stopped");
                break;
            default:
                if (_debugSlot != null && !_debugSlot.ActiveSelf) _debugSlot.ActiveSelf = true;
                if (_inst.TotalFrames > _inst.Warmup && _inst.EmaMainMs > BudgetMs)
                    Block($"paused: over budget ({_inst.EmaMainMs:0.00} ms per frame on the world thread > {BudgetMs:0.##} ms). Raise Budget, lower SimPoints or Substeps, or change any setting to retry");
                else if (_inst.TotalFrames > _inst.Warmup && _inst.LastFrameMs > 150)
                    Block($"paused: a single frame took {_inst.LastFrameMs:0} ms. Change any setting (or toggle Enabled) to retry");
                else _state = _inst.RunningState;
                break;
        }

        _statsTimer += dt;
        if (_statsTimer >= SoftbodyRuntime.StatsInterval)
        {
            if (_inst != null) { var line = _inst.StatsLine(_statsTimer, out var json); SoftbodyRuntime.Log(line); UpdateSnapshot(json); }
            else UpdateSnapshot();
            _statsTimer = 0;
        }
        ThrottledFlush(dt);
    }

    // 0.26.1: rebuilds the instance asks for by itself (the mesh, its asset or the bones changed under it). One that keeps
    // asking stops with a status instead of rebuilding forever (0.14.0 to 0.26.0 rebuilt every 2 s, see PrivateMeshes).
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
        if (_paintDirty || _paintPoll <= 0f)
        {
            _paintDirty = false;
            _paintPoll = 1f;
            _inst.UpdatePaint(ReadDabs(_inst.Profile));
        }
        _inst.FlushPaintColors();
        if (_inst.WantsRebuild)
        {
            _inst.RebuildFromPaint();
            SoftbodyRuntime.Log($"'{LogName}' rebuilding the tets from the paint");
        }
    }

    internal ReadOnlySpan<Dab> ReadDabs(ColorProfile profile) => _dabs.Read(Paint, profile);

    /// <summary>Removes every paint dab. Also a button in the inspector and callable over ResoniteLink.</summary>
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

    // ------------------------------------------------------------------ debug view (Gate held)

    /// <summary>The debug mesh to fill this frame, or null (paced to at most 30 updates and ~40 MB of vertices a second).</summary>
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
        var slot = World.RootSlot.AddLocalSlot("SkinnedSoftbody debug view (local): " + LogName, false);
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
        var specs = _specs ??= ReadLayers(s);
        int others = SoftbodyRuntime.TotalFleshPoints(this), need = 0;
        foreach (var l in specs) need += l.Settings.SimPoints;
        if (others + need > SoftbodyRuntime.MaxFleshPoints)
        {
            _state = $"waiting: point cap: other SkinnedSoftbodies already use {others} of {SoftbodyRuntime.MaxFleshPoints} flesh points on this client";
            _retryTimer = 1f;
            return;
        }
        SoftbodyInstance.CreateResult res;
        FleshInstance inst = null;
        string reason;
        try
        {
            res = FleshInstance.TryCreate(Renderer.Target, Slot, this, specs, SoftnessChannel.Value, InvertSoftness.Value, StartAs.Value, BuildKey(specs), out inst, out reason);
        }
        catch (Exception ex) { res = SoftbodyInstance.CreateResult.Fail; reason = "create threw " + ex.GetType().Name + ": " + ex.Message; }
        switch (res)
        {
            case SoftbodyInstance.CreateResult.Created:
                _inst = inst;
                _state = inst.RunningState;
                _error = ""; _loggedReason = null; _statsTimer = 0;
                SoftbodyRuntime.Log($"'{LogName}' SkinnedSoftbody started on renderer '{inst.Name}': {inst.Describe()}; settings {DescribeLayers(specs)}");
                break;
            case SoftbodyInstance.CreateResult.Wait:
                _state = "waiting: " + reason;
                _retryTimer = 1f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Log($"'{LogName}' SkinnedSoftbody waiting: {reason}"); }
                break;
            default:
                _state = "error: " + reason;
                _error = reason;
                _retryTimer = 5f;
                if (_loggedReason != reason) { _loggedReason = reason; SoftbodyRuntime.Warn($"'{LogName}' SkinnedSoftbody cannot start: {reason}"); }
                break;
        }
        UpdateSnapshot();
    }

    void Stop(string why)
    {
        DestroyDebug();
        if (_inst == null) return;
        var inst = _inst;
        _inst = null;
        try { inst.Teardown(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{LogName}' teardown: {ex.Message}"); }
        SoftbodyRuntime.Release(inst.ClaimKey, this);
        SoftbodyRuntime.Log($"'{LogName}' SkinnedSoftbody stopped: {why}");
    }

    void Block(string state, bool warn = true)
    {
        Stop("paused (see Status)");
        _blockedKey = FullKey(_specs ?? ReadLayers(_settings ?? ReadSettings()));
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
                SoftbodyRuntime.Warn($"'{LogName}' SkinnedSoftbody {where} threw: {ex}");
                Block("error: " + _error + ". Change any setting (or toggle Enabled) to retry", warn: false);
                UpdateSnapshot();
                FlushStatus();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ settings

    FleshSettings ReadSettings()
    {
        var s = new FleshSettings
        {
            SimPoints = Math.Clamp(SimPoints.Value, 50, 4000),
            Depth = Depth.Value,
            Layers = Layers.Value,
            Stiffness = Stiffness.Value,
            Volume = Volume.Value,
            BoneDensity = BoneDensity.Value,
            Follow = Follow.Value,
            Damping = Damping.Value,
            Gravity = Gravity.Value,
            BlendshapeInertia = BlendshapeInertia.Value,
            Substeps = Substeps.Value,
            Collide = Collide.Value,
            CollideSelf = CollideSelf.Value, SelfHead = SelfHead.Value, SelfBody = SelfBody.Value, SelfHands = SelfHands.Value,
            CollideOthers = CollideOthers.Value, OthersHead = OthersHead.Value, OthersBody = OthersBody.Value, OthersHands = OthersHands.Value,
            VolumePreservation = VolumePreservation.Value,
            CullDistance = CullDistance.Value,
        };
        s.Sanitize();
        return s;
    }

    /// <summary>0.13.0: the base layer (these settings) and every enabled hue layer, a hue at most once.</summary>
    FleshLayerSpec[] ReadLayers(FleshSettings s)
    {
        var list = new List<FleshLayerSpec> { new FleshLayerSpec(-1, s) };
        try
        {
            foreach (var l in HueLayers)
            {
                if (l == null || !l.Enabled.Value) continue;
                int band = (int)l.Hue.Value;
                if (band < 0 || band >= HueBands.Count || list.Exists(x => x.Band == band)) continue;
                list.Add(new FleshLayerSpec(band, l.Read(s), l));
            }
        }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("hue-layers:" + ex.Message, $"'{LogName}' reading HueLayers: {ex.Message}"); }
        return list.ToArray();
    }

    /// <summary>Every value of the hue layers (a change in one is caught by the poll in <see cref="Frame"/>).</summary>
    string LayerSignature()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var l in HueLayers) if (l != null) sb.Append(l.Enabled.Value ? '+' : '-').Append(l.Hue.Value).Append(' ').Append(l.Read(_settings ?? new FleshSettings())).Append(';');
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string DescribeLayers(FleshLayerSpec[] specs) =>
        specs.Length == 1 ? specs[0].Settings.ToString() : string.Join(" | ", specs.Select(l => l.Name + ": " + l.Settings));

    string BuildKey(FleshLayerSpec[] specs) =>
        $"renderer={Renderer.Target?.ReferenceID} channel={SoftnessChannel.Value} invert={InvertSoftness.Value} start={StartAs.Value} {specs[0].Settings.StructureKey}" +
        string.Concat(specs.Skip(1).Select(l => $" | {l.Name} {l.Settings.StructureKey}"));

    string FullKey(FleshLayerSpec[] specs) => BuildKey(specs) + " | " + DescribeLayers(specs) + " | dabs " + Paint.Count + " | budget " + BudgetMs.ToString("R", CultureInfo.InvariantCulture);

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
        Set(Tets, i?.TetCount ?? 0);
        Set(MovingVertices, i?.MovingVerts ?? 0);
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

    void UpdateSnapshot(string stats = null)
    {
        try
        {
            var sb = new StringBuilder("{");
            sb.Append("\"name\":").Append(StatsServer.Json(LogName));
            sb.Append(",\"type\":\"SkinnedSoftbody\"");
            sb.Append(",\"refId\":").Append(StatsServer.Json(ReferenceID.ToString()));
            sb.Append(",\"world\":").Append(StatsServer.Json(World?.Name));
            sb.Append(",\"state\":").Append(StatsServer.Json(_state));
            sb.Append(",\"lastError\":").Append(StatsServer.Json(_error));
            sb.Append(",\"renderer\":").Append(StatsServer.Json(_inst?.Name));
            try { sb.Append(",\"dabs\":").Append(Paint.Count); } catch { }
            if (stats != null) sb.Append(',').Append(stats);
            sb.Append(",\"effective\":").Append(StatsServer.Json(_specs != null ? DescribeLayers(_specs) : _settings?.ToString()));
            sb.Append('}');
            SnapshotJson = sb.ToString();
        }
        catch { }
    }
}

/// <summary>The synced dabs as Core dabs in a mesh's colour space, converting only the elements that changed.</summary>
sealed class DabMirror
{
    Dab[] _buf = Array.Empty<Dab>();
    RefID[] _ids = Array.Empty<RefID>();
    Raw[] _raw = Array.Empty<Raw>();
    ColorProfile _profile;

    readonly struct Raw
    {
        public readonly float3 Position; public readonly float Radius; public readonly colorX Color; public readonly float Strength, Hardness; public readonly bool Blur;
        public Raw(Softbody.PaintDab d) { Position = d.Position.Value; Radius = d.Radius.Value; Color = d.Color.Value; Strength = d.Strength.Value; Hardness = d.Hardness.Value; Blur = d.Blur.Value; }
        public bool Same(in Raw o) =>
            Blur == o.Blur && Position.Equals(o.Position) && Radius.Equals(o.Radius) && Strength.Equals(o.Strength) && Hardness.Equals(o.Hardness) &&
            Color.profile == o.Color.profile && Color.baseColor.Equals(o.Color.baseColor);
    }

    public ReadOnlySpan<Dab> Read(SyncList<Softbody.PaintDab> list, ColorProfile profile)
    {
        int n = Math.Min(list.Count, PaintLayer.MaxDabs);
        if (_buf.Length < n)
        {
            int cap = Math.Max(n, Math.Min(PaintLayer.MaxDabs, _buf.Length * 2 + 16));
            Array.Resize(ref _buf, cap); Array.Resize(ref _ids, cap); Array.Resize(ref _raw, cap);
        }
        if (profile != _profile) { Array.Clear(_ids); _profile = profile; }
        for (int i = 0; i < n; i++)
        {
            var e = list[i];
            if (e == null) { _ids[i] = default; _buf[i] = new Dab(Vector3.Zero, 1e-6f, Vector4.One, 0f, 1f); continue; }
            var raw = new Raw(e);
            var id = e.ReferenceID;
            if (_ids[i] == id && raw.Same(_raw[i])) continue;
            _ids[i] = id; _raw[i] = raw;
            var c = raw.Color.ToProfile(profile);
            _buf[i] = new Dab(new Vector3(raw.Position.x, raw.Position.y, raw.Position.z), raw.Radius, new Vector4(c.r, c.g, c.b, c.a), raw.Strength, raw.Hardness, raw.Blur).Sanitized();
        }
        return _buf.AsSpan(0, n);
    }
}
