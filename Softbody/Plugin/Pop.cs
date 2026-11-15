using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Pop;

namespace Softbody;

/// <summary>
/// 0.34.0: Pop (DESIGN-pop.md), a death effect. Press Fire (or call it from ProtoFlux: it is an Action) and the Target's
/// model bursts into pieces that fly out, fall, and lie about until Lifetime is up. Shred picks their size the way the
/// Cloth Blast's does, from a few big chunks (0) to confetti (1). The pieces keep the model's look: its materials and
/// textures, its colours, and the pose it had right then, blendshapes too. Big triangles are cut up, so there can be more
/// pieces than the model has triangles, and the model itself is never changed. The pieces hit the world, never players.
/// With HideTarget the model is hidden while they fly; ShowTarget brings it back early (at a respawn). 0.35.0: the pieces
/// are little cloths that flutter, fold and drape (Stiffness; 1 for 0.34.0's stiff pieces), and stick to what they hit
/// (Adhesion). 0.36.0: MaxStrength makes Strength a range, each piece flying out at its own speed in it.
///
/// Firing writes only a small synced record of the shot: a seed, when, and where the Target was. Every client then cuts
/// the model as it shows there with that seed and flies its own pieces, so a shot costs the session next to nothing, and
/// the hide is each client's own (nothing of it is synced or saved). A client that joins later does not replay old shots.
/// </summary>
[Category(new[] { "Physics" })]
public class Pop : Component
{
    /// <summary>The slot whose model pops: every mesh it shows on it and under it. Filled in with the slot the Pop is
    /// attached on.</summary>
    public readonly SyncRef<Slot> Target;
    /// <summary>0..1: how small the pieces are, from a few big chunks (0) to confetti (1), as the Cloth Blast's Shred.</summary>
    public readonly Sync<float> Shred;
    /// <summary>About this many pieces at most (1..5000): confetti on a big model gets no smaller than its surface split
    /// this many ways.</summary>
    public readonly Sync<int> MaxPieces;
    /// <summary>m/s the pieces fly out at (-50..50; below 0 they fly in). 0.36.0: with MaxStrength, one end of a range.</summary>
    public readonly Sync<float> Strength;
    /// <summary>m/s (-50..50), 0.36.0: the other end of a range from Strength; each piece flies out at its own speed in it
    /// (Strength 2 and this 8: from 2 to 8 m/s, spread evenly). 0: no range, every piece at Strength as before. With a
    /// range Randomness no longer slows the pieces; it still sends them a little sideways and spins them.</summary>
    public readonly Sync<float> MaxStrength;
    /// <summary>0..1: how uneven the burst is (some pieces slower, a little sideways, and spinning; with a MaxStrength
    /// range, only sideways and spinning).</summary>
    public readonly Sync<float> Randomness;
    /// <summary>0..10: how much the air holds the pieces back (stiff ones: per second). 0.35.0: soft ones flutter, glide and
    /// tumble in it as cloth does; it is how thick the air is for them (0: none).</summary>
    public readonly Sync<float> Drag;
    /// <summary>0..1, 0.35.0: how stiff the pieces are, from cloth that drapes like silk (0) to stiff (toward 1); at 1 they
    /// are 0.34.0's stiff pieces.</summary>
    public readonly Sync<float> Stiffness;
    /// <summary>0..10, 0.35.0: how hard the pieces hold on to what they hit, in their own weights (0: not at all; 1 holds
    /// one on a wall, barely; more holds them under a ceiling too).</summary>
    public readonly Sync<float> Adhesion;
    /// <summary>Seconds (0.5..600) the pieces stay; they shrink away in the last second.</summary>
    public readonly Sync<float> Lifetime;
    /// <summary>Hides the Target's model while its pieces fly (on each client, until Lifetime is up or ShowTarget).</summary>
    public readonly Sync<bool> HideTarget;
    /// <summary>The last shot: its seed (0: none yet), written last when it fires.</summary>
    [HideInInspector] public readonly Sync<int> Shot;
    /// <summary>The world time the last shot went off.</summary>
    [HideInInspector] public readonly Sync<double> ShotTime;
    /// <summary>Where the Target was when the last shot went off (its pieces start there, even if it moved since).</summary>
    [HideInInspector] public readonly Sync<float3> ShotAt;
    /// <summary>The shot ShowTarget ended the hide of (the model shows again from that shot back).</summary>
    [HideInInspector] public readonly Sync<int> Shown;
    /// <summary>What the last shot came to on this client (local, never synced).</summary>
    public readonly RawOutput<string> LastFired;

    /// <summary>Seconds: a shot older than this when it reaches a client is not played there (one that joined later, or a
    /// world loaded again); one this much from the future neither.</summary>
    const double OldestShot = 2.0, NewestShot = -1.0;
    /// <summary>Metres the Target may move in one frame and still count as moving (more is a teleport, not a speed).</summary>
    const float JumpDistance = 2f;
    /// <summary>m/s: the most the pieces carry of the Target's own speed.</summary>
    const float MostCarry = 20f;
    /// <summary>Seconds the Target's speed is smoothed over.</summary>
    const float SpeedSmoothing = 0.1f;

    PopDebris _runner;
    int _seen, _shownSeen;
    Slot _tracked;
    Vector3 _last, _velocity;

    protected override void OnAwake()
    {
        base.OnAwake();
        Shred.Value = 0.7f;
        MaxPieces.Value = 1500;
        Strength.Value = 4f;
        MaxStrength.Value = 0f;
        Randomness.Value = 0.5f;
        Drag.Value = 0.5f;
        Stiffness.Value = 0.3f;
        Adhesion.Value = 2f;
        Lifetime.Value = 8f;
        HideTarget.Value = true;
        // a shot (or a show) from any client: played once the update that brought it is done, so the record is whole;
        // this works with the Pop's slot inactive too (its OnCommonUpdate then does not run)
        Shot.OnValueChange += _ => Later(CheckShot);
        Shown.OnValueChange += _ => Later(CheckShown);
    }

    /// <summary>Attached on a slot, the Pop pops that slot's model: Target is set to it.</summary>
    protected override void OnAttach()
    {
        base.OnAttach();
        try { Target.Target = Slot; }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Pop OnAttach: {ex.Message}"); }
    }

    /// <summary>Fires the pop (a button in the inspector, and an Action other things can call). Runs on the client that
    /// calls it, which writes the shot; every client plays it from there.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void Fire()
    {
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            string result;
            lock (SoftbodyRuntime.Gate) result = FireNow();
            Say(result);
            SoftbodyRuntime.Log($"'{SlotName}' Pop fired: {result}");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Pop Fire: {ex.Message}"); }
    }

    /// <summary>Shows the Target's model again on every client (a button, and an Action: call it at the respawn), while
    /// the pieces still fly.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void ShowTarget()
    {
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            string result;
            lock (SoftbodyRuntime.Gate)
            {
                int s = Shot.Value;
                if (IsRemoved) result = "nothing: the Pop was removed";
                else if (s == 0) result = "nothing: it has not fired yet";
                else if (Shown.Value == s) result = "nothing: the model already shows";
                else { Shown.Value = s; result = "the model shows again on every client"; }
            }
            Say("ShowTarget: " + result);
            SoftbodyRuntime.Log($"'{SlotName}' Pop ShowTarget: {result}");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Pop ShowTarget: {ex.Message}"); }
    }

    string SlotName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }

    string FireNow()
    {
        if (IsRemoved || Slot == null) return "nothing: the Pop was removed";
        if (!Enabled) return "nothing: the Pop is disabled";
        var t = Target.Target;
        if (t == null || t.IsRemoved) return "nothing: no Target (the slot whose model pops)";
        var gp = t.GlobalPosition;
        if (!float.IsFinite(gp.x + gp.y + gp.z)) return "nothing: the Target's position is not finite";
        int seed = NewSeed();
        ShotTime.Value = World.Time.WorldTime;
        ShotAt.Value = gp;
        Shot.Value = seed;   // last: every client plays the shot once its seed changes
        return $"fired at '{NameOf(t)}': every client cuts its model and flies the pieces";
    }

    int NewSeed()
    {
        for (int k = 0; k < 64; k++)
        {
            int s = Random.Shared.Next(1, int.MaxValue);
            if (s != Shot.Value && s != Shown.Value) return s;
        }
        return Shot.Value == int.MaxValue - 1 ? 1 : Shot.Value + 1;
    }

    void Later(Action a)
    {
        try { World?.RunSynchronously(a, false, null, false); }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("pop-later", "Pop: could not queue a shot: " + ex.Message); }
    }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try
        {
            // the change events are the way in; this catches anything they missed
            if (Shot.Value != _seen) CheckShot();
            if (Shown.Value != _shownSeen) CheckShown();
            Track();
        }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("pop-update", $"'{SlotName}' Pop update: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ a shot, on each client

    void CheckShot()
    {
        try
        {
            string result;
            lock (SoftbodyRuntime.Gate) result = CheckShotNow();
            if (result == null) return;
            Say(result);
            SoftbodyRuntime.Log($"'{SlotName}' Pop: {result}");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Pop: playing a shot failed: {ex.Message}"); }
    }

    /// <summary>Plays the last shot if it is new and recent (Gate held); null when there is nothing to say.</summary>
    string CheckShotNow()
    {
        if (IsRemoved || IsDisposed || World == null) return null;
        int seed = Shot.Value;
        if (seed == _seen) return null;
        _seen = seed;
        if (seed == 0) return null;
        double age = World.Time.WorldTime - ShotTime.Value;
        if (!(age > NewestShot && age < OldestShot)) return null;   // an old shot: a client that joined later, a world loaded again
        SoftbodyRuntime.EnsureInitialized();
        return StartShot(seed, Math.Max(0.0, age));
    }

    string StartShot(int seed, double age)
    {
        var t = Target.Target;
        if (t == null || t.IsRemoved) return "nothing: no Target (the slot whose model pops)";
        var now = t.GlobalPosition;
        var at = ShotAt.Value;
        // where the model was when it fired against where it is now: a model sent away at once still pops where it was
        var offset = new Vector3(at.x - now.x, at.y - now.y, at.z - now.z);
        if (!float.IsFinite(offset.X + offset.Y + offset.Z)) offset = Vector3.Zero;
        var runner = _runner != null && !_runner.Ended ? _runner : null;
        var list = new List<MeshRenderer>();
        t.GetComponentsInChildren(list, r => true, false, false, s => true);
        var sources = new List<PopBake.Source>();
        var why = new List<string>();
        int vertices = 0, skipped = 0;
        foreach (var r in list)
        {
            if (!Shows(r, runner)) continue;
            if (sources.Count >= PopBake.MostRenderers) { skipped++; continue; }
            var s = PopBake.Capture(r, offset, out var no);
            if (s == null) { if (why.Count < 3) why.Add($"'{NameOf(r.Slot)}': {no}"); continue; }
            if (vertices + s.VertexCount > PopBake.MostVertices) { skipped++; continue; }
            vertices += s.VertexCount;
            sources.Add(s);
        }
        if (sources.Count == 0)
            return $"nothing: '{NameOf(t)}' shows no mesh to pop" + (why.Count > 0 ? " (" + string.Join("; ", why) + ")" : "");
        if (runner == null) _runner = runner = PopDebris.Make(this, NameOf(t));
        runner.Start(new PopDebris.Order
        {
            Seed = seed,
            Name = NameOf(t),
            Target = t,
            Sources = sources,
            Settings = new PopCutSettings
            {
                Shred = Clamp(Shred.Value, 0f, 1f, 0.7f),
                MaxPieces = Math.Clamp(MaxPieces.Value, 1, 5000),
                Seed = seed,
            },
            Strength = Clamp(Strength.Value, -50f, 50f, 0f),
            MaxStrength = Clamp(MaxStrength.Value, -50f, 50f, 0f),
            Randomness = Clamp(Randomness.Value, 0f, 1f, 0f),
            Drag = Clamp(Drag.Value, 0f, 10f, 0.5f),
            Stiffness = Clamp(Stiffness.Value, 0f, 1f, 0.3f),
            Adhesion = Clamp(Adhesion.Value, 0f, 10f, 0f),
            Lifetime = Clamp(Lifetime.Value, 0.5f, 600f, 8f),
            Velocity = ReferenceEquals(_tracked, t) ? _velocity : Vector3.Zero,
            Hide = HideTarget.Value && Shown.Value != seed,
            Age = age,
            Vertices = vertices,
            Skipped = skipped,
        });
        return $"cutting '{NameOf(t)}' on this client ({sources.Count} {(sources.Count == 1 ? "mesh" : "meshes")}, {vertices} vertices" +
               (skipped > 0 ? $", {skipped} left out: over {PopBake.MostRenderers} meshes or {PopBake.MostVertices} vertices" : "") + ")";
    }

    /// <summary>Whether a renderer shows its mesh now (or would, but for this Pop's own hide).</summary>
    static bool Shows(MeshRenderer r, PopDebris runner)
    {
        if (r == null || r.IsRemoved || r.Slot == null || r.Slot.IsRemoved) return false;
        bool onSlot = false;
        bool held = runner != null && runner.Holds(r, out onSlot);
        if (!r.Enabled && !(held && !onSlot)) return false;
        var s = r.Slot;
        if (s.IsActive) return true;
        return held && onSlot && (s.Parent == null || s.Parent.IsActive);
    }

    void CheckShown()
    {
        try
        {
            lock (SoftbodyRuntime.Gate)
            {
                if (IsRemoved || IsDisposed) return;
                int s = Shown.Value;
                if (s == _shownSeen) return;
                _shownSeen = s;
                if (s != 0 && _runner != null && !_runner.Ended) _runner.Show(s, Shot.Value);
            }
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Pop: showing the model failed: {ex.Message}"); }
    }

    /// <summary>The runner's word on a shot it cut (Gate held).</summary>
    internal void Reported(int seed, string what)
    {
        if (seed == Shot.Value) Say(what);
    }

    // ------------------------------------------------------------------ the Target's speed (each client its own)

    void Track()
    {
        float dt = World.Time.Delta;
        var t = Target.Target;
        if (t == null || t.IsRemoved || !float.IsFinite(dt) || dt <= 1e-4f) { _tracked = null; _velocity = Vector3.Zero; return; }
        var gp = t.GlobalPosition;
        var p = new Vector3(gp.x, gp.y, gp.z);
        if (!float.IsFinite(p.X + p.Y + p.Z)) { _tracked = null; _velocity = Vector3.Zero; return; }
        if (ReferenceEquals(_tracked, t))
        {
            var d = p - _last;
            if (d.LengthSquared() > JumpDistance * JumpDistance) _velocity = Vector3.Zero;
            else
            {
                float k = 1f - MathF.Exp(-dt / SpeedSmoothing);
                _velocity += (d / dt - _velocity) * k;
                float l = _velocity.Length();
                if (l > MostCarry) _velocity *= MostCarry / l;
                if (!float.IsFinite(_velocity.X + _velocity.Y + _velocity.Z)) _velocity = Vector3.Zero;
            }
        }
        else _velocity = Vector3.Zero;
        _tracked = t;
        _last = p;
    }

    void Say(string s)
    {
        try { if (LastFired != null && LastFired.CanWrite) LastFired.Value = s; } catch { }
    }

    static string NameOf(Slot s)
    {
        try { var n = s?.Name; return string.IsNullOrEmpty(n) ? "?" : n; } catch { return "?"; }
    }

    static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}
