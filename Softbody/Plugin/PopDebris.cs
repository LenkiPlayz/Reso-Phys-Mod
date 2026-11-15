using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.Core.Pop;

namespace Softbody;

/// <summary>
/// 0.34.0: a Pop's pieces on this client (DESIGN-pop.md). A <see cref="Pop"/> makes one on a local slot (never synced or
/// saved) the first time it goes off here and hands it each shot: the model as it showed then is cut into pieces off the
/// world thread (with the shot's seed, so every client cuts alike), and the pieces fly from a slot of their own under this
/// one, with the model's materials, until the Pop's Lifetime is up. They hit the world, never players. 0.35.0: below
/// Stiffness 1 each piece is a little cloth (made off the world thread with the cut), and with Adhesion they hold on.
///
/// With HideTarget the model is hidden on this client while its pieces fly, by local drives on its renderers' Enabled (or,
/// for a renderer whose Enabled something else drives, on its slot's Active, when the renderer's slot holds nothing else).
/// Each field is put back as it was before its drive lets go of it, so nothing of the hide reaches the others. The hide ends
/// with the Lifetime, or with the Pop's ShowTarget. Once nothing is left, its slot goes. Attached by hand it does nothing.
/// </summary>
[Category(new[] { "Physics/Softbody" })]
public class PopDebris : Component
{
    /// <summary>What it is doing (this client only).</summary>
    public readonly RawOutput<string> Status;
    /// <summary>The drives that hide the popped models' renderers (local, as the slot it is on).</summary>
    protected readonly SyncList<FieldDrive<bool>> _hidden;

    static readonly Vector3 Gravity = new(0f, -9.81f, 0f);
    /// <summary>Seconds the pieces take to shrink away at the end of their lifetime (at most half of it).</summary>
    const float ShrinkTime = 1f;
    /// <summary>Frames between the pieces' mesh being first built and the model being hidden (its trip to the renderer: a
    /// frame with both rather than one with neither).</summary>
    const int HideAfterFrames = 2;
    /// <summary>Metres around the awake pieces (and where they head this frame) in which the world's shapes are gathered.</summary>
    const float ShapePad = 0.5f;
    /// <summary>Shots in flight at once; a shot past it takes the place of the oldest.</summary>
    const int MostFlights = 8;

    /// <summary>What a Pop hands over for one shot (world thread).</summary>
    internal sealed class Order
    {
        public int Seed;
        public string Name;
        public Slot Target;
        public List<PopBake.Source> Sources;
        public PopCutSettings Settings;
        public float Strength, Randomness, Drag, Lifetime;
        /// <summary>0.36.0: with Strength, the range each piece picks its own strength in (0: none).</summary>
        public float MaxStrength;
        /// <summary>0.35.0: below 1 the pieces are little cloths (1: stiff, as in 0.34.0); how hard they hold on to what
        /// they hit.</summary>
        public float Stiffness = 1f, Adhesion;
        public Vector3 Velocity;
        public bool Hide;
        /// <summary>Seconds since the shot went off (it counts toward the Lifetime).</summary>
        public double Age;
        public int Vertices, Skipped;
    }

    sealed class Flight
    {
        public Order O;
        public Task<PopBake.Result> Cutting;
        public PopBake.Result R;
        public Slot Slot;
        public PopDebrisMesh Mesh;
        public bool Hide;
        public double Age;
        public int BuiltFrames;
        public MeshRenderer[] Renderers;
        public Slot[] Exclude;
        public readonly FluidColliders Colliders = new();
        public readonly List<FleshCollider> Shapes = new();
        public double StepMs;
        public bool Reported;
    }

    sealed class Hide
    {
        public MeshRenderer Renderer;
        public Sync<bool> Field;
        public bool OnSlot;
        public FieldDrive<bool> Drive;
        /// <summary>The value to put back: the field's own when the drive took it, or what reached it since (from the
        /// others, or from something on this client).</summary>
        public bool Original;
        public bool Holding, Writing;
        public SyncFieldEvent<bool> Watch;
    }

    readonly List<Flight> _flights = new();
    readonly List<Hide> _hides = new();
    readonly HashSet<MeshRenderer> _want = new();
    Pop _owner;
    string _name = "?", _flushed;
    double _statusTimer;
    int _unhidable;
    bool _ended;

    /// <summary>A runner for <paramref name="owner"/> on a new local slot (world thread, Gate held).</summary>
    internal static PopDebris Make(Pop owner, string name)
    {
        var slot = owner.World.RootSlot.AddLocalSlot("Pop (local): " + name, false);
        var d = slot.AttachComponent<PopDebris>();
        d._owner = owner;
        d._name = name;
        return d;
    }

    internal bool Ended => _ended || IsRemoved;

    /// <summary>Whether this runner hides <paramref name="r"/> now, and whether by its slot's Active.</summary>
    internal bool Holds(MeshRenderer r, out bool onSlot)
    {
        foreach (var h in _hides)
            if (ReferenceEquals(h.Renderer, r)) { onSlot = h.OnSlot; return true; }
        onSlot = false;
        return false;
    }

    /// <summary>Starts a shot (world thread, Gate held): its cut goes off the world thread at once.</summary>
    internal void Start(Order o)
    {
        if (_ended) return;
        while (_flights.Count >= MostFlights) EndFlight(_flights[0], "a newer shot took its place");
        var f = new Flight
        {
            O = o,
            Hide = o.Hide,
            Age = o.Age,
            Renderers = o.Sources.Select(s => s.Renderer).Distinct().ToArray(),
            Exclude = o.Target != null ? new[] { o.Target } : Array.Empty<Slot>(),
        };
        var sources = o.Sources;
        var settings = o.Settings;
        bool soft = o.Stiffness < 1f;
        f.Cutting = Task.Run(() => PopBake.Cut(sources, settings, soft));
        _flights.Add(f);
        _statusTimer = 0;
    }

    /// <summary>ShowTarget (world thread, Gate held): the shot with this seed, and every one before it, stops hiding.
    /// <paramref name="current"/> is the Pop's last shot (a seed of a shot not here that is the last one: all of them).</summary>
    internal void Show(int seed, int current)
    {
        int at = -1;
        for (int i = _flights.Count - 1; i >= 0; i--) if (_flights[i].O.Seed == seed) { at = i; break; }
        if (at < 0)
        {
            if (seed != current) return;
            at = _flights.Count - 1;
        }
        for (int i = 0; i <= at; i++) _flights[i].Hide = false;
        try { Hides(); } catch (Exception ex) { SoftbodyRuntime.Warn($"Pop of '{_name}': showing the model: {ex.Message}"); }
    }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try { lock (SoftbodyRuntime.Gate) Frame(); }
        catch (Exception ex)
        {
            try { lock (SoftbodyRuntime.Gate) End("it failed: " + ex.GetType().Name + ": " + ex.Message, warn: true); } catch { }
        }
    }

    void Frame()
    {
        if (_ended) return;
        float dt = World.Time.Delta;
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        dt = MathF.Min(dt, 0.1f);
        if (_owner == null)
        {
            // attached by hand: nothing to run
            _statusTimer -= dt;
            if (_statusTimer <= 0) { _statusTimer = 1; Set("does nothing: a Pop makes these on its own when it goes off"); }
            return;
        }
        bool focused = World.Focus == FrooxEngine.World.WorldFocus.Focused;
        for (int i = 0; i < _flights.Count; i++)
        {
            var f = _flights[i];
            f.Age += dt;
            if (f.Age >= f.O.Lifetime)
            {
                if (f.Cutting != null) Report(f, "nothing: its Lifetime was up before the cut was done", warn: false);
                EndFlight(f, null);
                i--;
                continue;
            }
            if (f.Cutting != null)
            {
                if (!f.Cutting.IsCompleted) continue;
                if (!Launch(f)) { i--; continue; }
            }
            if (f.Mesh == null) continue;
            if (f.Mesh.Builds > 0 && f.BuiltFrames < HideAfterFrames) f.BuiltFrames++;
            Fly(f, dt, focused);
        }
        Hides();
        _statusTimer -= dt;
        if (_statusTimer <= 0) { _statusTimer = 0.5; ShowStatus(); }
        if (_flights.Count == 0 && _hides.Count == 0) End("its pieces are gone");
    }

    /// <summary>The cut is back: the pieces' slot, mesh and renderer, and their push. False when the shot came to nothing
    /// (it is gone from the list then).</summary>
    bool Launch(Flight f)
    {
        PopBake.Result r = null;
        string fail = null;
        try { r = f.Cutting.Result; }
        catch (Exception ex) { fail = "cutting it failed: " + (ex.InnerException ?? ex).Message; }
        f.Cutting = null;
        if (fail == null && (r == null || r.Failed != null)) fail = r?.Failed ?? "the cut came to nothing";
        if (fail != null || r.Pieces == null || r.Solver == null)
        {
            Report(f, "nothing: " + (fail ?? "the cut came to nothing"), warn: fail != null && fail.StartsWith("cutting it failed"));
            EndFlight(f, null, quiet: true);
            return false;
        }
        f.R = r;
        var o = f.O;
        Slot slot = null;
        try
        {
            slot = Slot.AddLocalSlot("Pieces of " + o.Name, false);
            var mesh = slot.AttachComponent<PopDebrisMesh>();
            var mr = slot.AttachComponent<MeshRenderer>();
            mr.Mesh.Target = mesh;
            // the model's materials, one per submesh in the cut's order (each renderer's in its own order), with their
            // property blocks lined up (a null where a material had none)
            bool blocks = o.Sources.Any(s => s.Blocks.Any(b => b != null));
            foreach (var s in o.Sources)
                for (int k = 0; k < s.Materials.Count; k++)
                {
                    mr.Materials.Add(s.Materials[k]);
                    if (blocks) mr.MaterialPropertyBlocks.Add(s.Blocks[k]);
                }
            var first = o.Sources[0].Renderer;
            if (first != null && !first.IsRemoved)
            {
                mr.ShadowCastMode.Value = first.ShadowCastMode.Value;
                mr.MotionVectorMode.Value = first.MotionVectorMode.Value;
                mr.SortingOrder.Value = first.SortingOrder.Value;
            }
            mesh.Setup(r.Pieces, r.UvDims, r.Profile);
            f.Slot = slot;
            f.Mesh = mesh;
        }
        catch (Exception ex)
        {
            try { if (slot != null && !slot.IsRemoved) slot.Destroy(); } catch { }
            Report(f, "nothing: making its pieces failed: " + ex.Message, warn: true);
            EndFlight(f, null, quiet: true);
            return false;
        }
        var sol = r.Solver;
        sol.Drag = o.Drag;
        sol.Stiffness = o.Stiffness;
        sol.Adhesion = o.Adhesion;
        sol.StopBelow = r.Middle.Y - 50f;
        sol.Kick(r.Middle, o.Strength, o.Randomness, o.Seed, o.MaxStrength);
        sol.Carry(o.Velocity);
        string soft = r.Soft != null ? $", soft ({r.Soft.Particles} points about {r.Soft.Spacing * 100f:0.0} cm apart)" : ", stiff";
        Report(f, $"{Plural(r.Pieces.Count, "piece")}{soft} from {Plural(o.Sources.Count, "mesh", "meshes")} ({o.Vertices} vertices" +
                  (o.Skipped > 0 ? $", {o.Skipped} left out" : "") + $"), cut in {r.BakeMs + r.CutMs + r.SoftMs:0} ms", warn: false);
        return true;
    }

    void Fly(Flight f, float dt, bool focused)
    {
        var sol = f.R.Solver;
        float shrinkTime = MathF.Min(ShrinkTime, 0.5f * f.O.Lifetime);
        float left = (float)(f.O.Lifetime - f.Age);
        sol.Shrink = shrinkTime > 0f ? Math.Clamp(left / shrinkTime, 0f, 1f) : 1f;
        // in a world in the background nobody sees them: they wait (their time still runs out)
        if (!focused) return;
        f.Shapes.Clear();
        if (sol.AwakeBounds(dt + 0.1f, out var lo, out var hi))
        {
            var pad = new Vector3(ShapePad);
            f.Colliders.Exclude = f.Exclude;
            f.Colliders.Gather(World, lo - pad, hi + pad, dt, FluidSim.CategoryWorld, f.Shapes);
        }
        sol.Step(dt, Gravity, f.Shapes);
        f.StepMs = f.StepMs <= 0 ? sol.LastStepMs : f.StepMs + (sol.LastStepMs - f.StepMs) * 0.1;
        if (sol.Changed) f.Mesh.Submit(sol.OutPositions, sol.OutNormals, sol.OutTangents);
    }

    void EndFlight(Flight f, string why, bool quiet = false)
    {
        _flights.Remove(f);
        try { if (f.Slot != null && !f.Slot.IsRemoved && !f.Slot.IsDisposed) f.Slot.Destroy(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"Pop of '{_name}': removing its pieces: {ex.Message}"); }
        f.Slot = null; f.Mesh = null;
        if (!quiet && why != null) SoftbodyRuntime.Log($"Pop of '{f.O.Name}': pieces removed: {why}");
    }

    // ------------------------------------------------------------------ hiding the model (this client only)

    /// <summary>The drives as the flights want them: every renderer of a flight that hides and shows its pieces (and of
    /// one still cutting, while a shot before it hides that renderer: a second shot keeps the first one's hide), held
    /// false; the rest put back and let go.</summary>
    void Hides()
    {
        _want.Clear();
        // HideTarget turned off while the pieces fly: the model comes back (a Pop that is gone leaves its hides as they are)
        bool hideOn = _owner == null || _owner.IsRemoved || _owner.HideTarget.Value;
        foreach (var f in _flights)
        {
            if (!f.Hide || !hideOn) continue;
            bool shows = f.Mesh != null && f.BuiltFrames >= HideAfterFrames;
            foreach (var r in f.Renderers)
            {
                if (r == null || r.IsRemoved) continue;
                if (shows || Holds(r, out _)) _want.Add(r);
            }
        }
        for (int i = _hides.Count - 1; i >= 0; i--)
        {
            var h = _hides[i];
            if (h.Renderer.IsRemoved || h.Field.IsRemoved || h.Field.IsDisposed || !_want.Contains(h.Renderer)) { Release(h); _hides.RemoveAt(i); }
        }
        _unhidable = 0;
        foreach (var r in _want)
            if (!Holds(r, out _) && !Take(r)) _unhidable++;
        foreach (var h in _hides)
        {
            if (!h.Drive.IsLinkValid) continue;
            if (!h.Holding) { h.Holding = true; h.Original = h.Field.Value; }
            if (h.Field.Value)
            {
                h.Writing = true;
                try { h.Field.Value = false; } finally { h.Writing = false; }
            }
        }
    }

    /// <summary>A drive on the renderer's Enabled, or on its slot's Active when something else drives its Enabled and the
    /// slot holds nothing else. False when neither is free (it stays shown).</summary>
    bool Take(MeshRenderer r)
    {
        Sync<bool> field = null;
        bool onSlot = false;
        if (!r.EnabledField.IsLinked) field = r.EnabledField;
        else if (r.Slot != null && r.Slot.ChildrenCount == 0 && !r.Slot.ActiveSelf_Field.IsLinked) { field = r.Slot.ActiveSelf_Field; onSlot = true; }
        if (field == null) return false;
        FieldDrive<bool> d = null;
        try
        {
            d = _hidden.Add();
            d.Target = field;
            if (!ReferenceEquals(d.Target, field)) { _hidden.Remove(d); return false; }
        }
        catch (Exception ex)
        {
            try { if (d != null) _hidden.Remove(d); } catch { }
            SoftbodyRuntime.LogOnce("pop-hide-failed", $"Pop: hiding a renderer failed: {ex.Message}");
            return false;
        }
        var h = new Hide { Renderer = r, Field = field, OnSlot = onSlot, Drive = d, Original = field.Value };
        h.Watch = f => { if (!h.Writing) h.Original = f.Value; };
        field.OnValueChange += h.Watch;
        _hides.Add(h);
        return true;
    }

    /// <summary>Puts the field back as it was (through the drive, so it stays on this client: let go while it holds false,
    /// the session's host would send false to everyone) and lets go of it.</summary>
    void Release(Hide h)
    {
        try { h.Field.OnValueChange -= h.Watch; } catch { }
        try
        {
            if (h.Holding && h.Drive.IsLinkValid && !h.Field.IsRemoved && h.Field.Value != h.Original)
            {
                h.Writing = true;
                try { h.Field.Value = h.Original; } finally { h.Writing = false; }
            }
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"Pop of '{_name}': putting a renderer back: {ex.Message}"); }
        try { if (!h.Drive.IsRemoved) h.Drive.Target = null; } catch { }
        try { if (!h.Drive.IsRemoved) _hidden.Remove(h.Drive); } catch { }
    }

    // ------------------------------------------------------------------ status

    void Report(Flight f, string what, bool warn)
    {
        if (f.Reported) return;
        f.Reported = true;
        string line = $"'{f.O.Name}' popped: {what}";
        if (warn) SoftbodyRuntime.Warn("Pop " + line); else SoftbodyRuntime.Log("Pop " + line);
        try { if (_owner != null && !_owner.IsRemoved) _owner.Reported(f.O.Seed, what); } catch { }
    }

    void ShowStatus()
    {
        if (_flights.Count == 0) { Set($"pop of '{_name}' (this client only): nothing left"); return; }
        int pieces = 0, awake = 0, cutting = 0;
        double ms = 0, left = 0;
        foreach (var f in _flights)
        {
            left = Math.Max(left, f.O.Lifetime - f.Age);
            if (f.R?.Solver == null) { cutting++; continue; }
            pieces += f.R.Pieces.Count;
            awake += f.R.Solver.Awake;
            ms += f.StepMs;
        }
        string s = $"pop of '{_name}' (this client only): {Plural(pieces, "piece")}, {awake} moving, {ms:0.00} ms a frame";
        if (cutting > 0) s += $", {cutting} being cut";
        if (_hides.Count > 0)
        {
            // a drive holds its field once the engine grants it (the next update); until then the model still shows
            int held = _hides.Count(h => h.Holding);
            s += $"; hiding {Plural(held, "renderer")}" + (held < _hides.Count ? $" ({_hides.Count - held} waiting for their drive)" : "");
        }
        if (_unhidable > 0) s += $"; {Plural(_unhidable, "renderer")} could not be hidden (its Enabled is driven and its slot holds more)";
        s += $"; {Math.Max(0, left):0} s left";
        Set(s);
    }

    static string Plural(int n, string what, string many = null) => n == 1 ? "1 " + what : $"{n} {many ?? what + "s"}";

    void Set(string s)
    {
        if (s == _flushed) return;
        _flushed = s;
        try { if (Status != null && Status.CanWrite) Status.Value = s; } catch { }
    }

    // ------------------------------------------------------------------ the end

    /// <summary>Takes it all away (Gate held): every hidden renderer put back, the pieces and the slot removed.</summary>
    internal void End(string why, bool warn = false)
    {
        if (_ended) return;
        TearDown();
        string line = $"Pop of '{_name}' ended on this client: {why}";
        if (warn) SoftbodyRuntime.Warn(line); else SoftbodyRuntime.Log(line);
        try { if (Slot != null && !Slot.IsRemoved && !Slot.IsDisposed) Slot.Destroy(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"removing the pop of '{_name}': {ex.Message}"); }
    }

    void TearDown()
    {
        _ended = true;
        foreach (var h in _hides) Release(h);
        _hides.Clear();
        foreach (var f in _flights.ToArray()) EndFlight(f, null, quiet: true);
        _flights.Clear();
    }

    protected override void OnDestroy()
    {
        try { lock (SoftbodyRuntime.Gate) TearDown(); } catch { }
        base.OnDestroy();
    }

    public override void OnWorldDestroy()
    {
        try { lock (SoftbodyRuntime.Gate) TearDown(); } catch { }
        // no base call: the engine's ComponentBase.OnWorldDestroy throws by design (only overrides may run; 0.26.2)
    }
}
