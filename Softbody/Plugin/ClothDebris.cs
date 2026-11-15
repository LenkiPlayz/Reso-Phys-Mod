using Elements.Core;
using FrooxEngine;

namespace Softbody;

/// <summary>
/// 0.33.0: a torn cloth's loose pieces, left behind on this client when its <see cref="Cloth"/> (with DuplicateOnMend) is
/// mended or turned off, while the original comes back whole. The Cloth puts it on a local slot (never synced or saved)
/// where the cloth is, with a StaticMesh of the cloth's mesh and a MeshRenderer with its materials, and hands it its running
/// simulation (see ClothInstance.Copy.cs): the pieces fall and settle, then stay as they are until CopyLifetime is up, when
/// the slot is removed. Attached by hand it does nothing.
/// </summary>
[Category(new[] { "Physics/Softbody" })]
public class ClothDebris : Component
{
    /// <summary>What it is and how long it stays (this client only).</summary>
    public readonly RawOutput<string> Status;

    ClothInstance _inst;
    float _lifetime;
    double _age, _statusTimer;
    string _from = "?", _flushed;
    bool _ended;

    /// <summary>Its StaticMesh has not taken over yet: the original's StaticMesh still holds the torn copy of the mesh, so its
    /// Cloth must not start again yet (Gate held).</summary>
    internal bool HandingOver => !_ended && _inst != null && _inst.CopyHandingOver;

    /// <summary>
    /// Leaves <paramref name="inst"/> behind (world thread, Gate held): a local slot where the cloth is, with its own
    /// StaticMesh of the same mesh, a MeshRenderer with the same materials (shown once the StaticMesh takes over) and this
    /// component, which runs it from now on. Null, with why, when nothing is left (avatar cloth, nothing loose, or it
    /// failed); then the caller takes the instance down as usual.
    /// </summary>
    internal static ClothDebris Leave(ClothInstance inst, float lifetime, out string why)
    {
        if (!inst.CanLeaveCopy(out why)) return null;
        if (!inst.FindLoose(out var keep, out var loose, out int pieces))
        {
            why = "nothing loose to leave behind (every piece is held by a pinned or anchored point)";
            return null;
        }
        var renderer = inst.Renderer;
        Slot slot = null;
        try
        {
            var src = renderer.Slot;
            slot = renderer.World.RootSlot.AddLocalSlot("Cloth copy (local): " + inst.Name, false);
            slot.GlobalPosition = src.GlobalPosition;
            slot.GlobalRotation = src.GlobalRotation;
            slot.GlobalScale = src.GlobalScale;
            var mesh = slot.AttachComponent<StaticMesh>();
            mesh.URL.Value = inst.Provider.URL.Value;
            mesh.Readable.Value = inst.Provider.Readable.Value;
            var r = slot.AttachComponent<MeshRenderer>();
            r.Enabled = false;     // shown when its StaticMesh takes over the cloth's copy of the mesh
            r.Mesh.Target = mesh;
            for (int i = 0; i < renderer.Materials.Count; i++) r.Materials.Add(renderer.Materials[i]);
            for (int i = 0; i < renderer.MaterialPropertyBlocks.Count; i++) r.MaterialPropertyBlocks.Add(renderer.MaterialPropertyBlocks[i]);
            r.ShadowCastMode.Value = renderer.ShadowCastMode.Value;
            r.MotionVectorMode.Value = renderer.MotionVectorMode.Value;
            r.SortingOrder.Value = renderer.SortingOrder.Value;
            var d = slot.AttachComponent<ClothDebris>();
            d._lifetime = lifetime;
            d._from = inst.Name;
            inst.BecomeCopy(slot, mesh, r, keep, loose, pieces);
            d._inst = inst;
            why = $"left a copy behind: {Plural(pieces, "loose piece")}, {Plural(loose.Length, "point")}, " +
                  (lifetime > 0f ? $"for {lifetime:0.#} s" : "until the world closes");
            d.Show();
            return d;
        }
        catch (Exception ex)
        {
            why = "making the copy failed: " + ex.Message;
            try { if (slot != null && !slot.IsRemoved) slot.Destroy(); } catch { }
            return null;
        }
    }

    static string Plural(int n, string what) => n == 1 ? "1 " + what : $"{n} {what}s";

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try { lock (SoftbodyRuntime.Gate) Frame(); }
        catch (Exception ex) { try { lock (SoftbodyRuntime.Gate) End("it failed: " + ex.GetType().Name + ": " + ex.Message, warn: true); } catch { } }
    }

    void Frame()
    {
        if (_ended) return;
        float dt = World.Time.Delta;
        if (!float.IsFinite(dt) || dt < 0f) dt = 0f;
        dt = MathF.Min(dt, 0.1f);
        if (_inst == null)
        {
            // attached by hand: nothing to run
            _statusTimer -= dt;
            if (_statusTimer <= 0) { _statusTimer = 1; Set("does nothing: a Cloth with DuplicateOnMend makes these on its own"); }
            return;
        }
        _age += dt;
        if (_lifetime > 0f && _age >= _lifetime) { End("its CopyLifetime is up"); return; }
        var res = _inst.CopyFrame(World, dt, out var why);
        if (res != ClothInstance.CopyResult.Ok) { End(why ?? "it stopped", warn: res == ClothInstance.CopyResult.Failed); return; }
        _statusTimer -= dt;
        if (_statusTimer <= 0) { _statusTimer = 0.5; Show(); }
    }

    void Show()
    {
        var i = _inst;
        if (i == null) return;
        string left = _lifetime > 0f ? $"{Math.Max(0, _lifetime - _age):0} s left" : "stays until the world closes";
        Set($"copy of the cloth on '{_from}' (this client only): {Plural(i.CopyPieces, "piece")}, {Plural(i.CopyPoints, "point")}; {i.CopyState}; {left}");
    }

    void Set(string s)
    {
        if (s == _flushed) return;
        _flushed = s;
        try { if (Status != null && Status.CanWrite) Status.Value = s; } catch { }
    }

    /// <summary>Takes it away (Gate held): the cloth's copy of the mesh is let go (an original still waiting gets its shared
    /// mesh back) and the slot is removed.</summary>
    internal void End(string why, bool warn = false)
    {
        if (_ended) return;
        TearDown();
        string line = $"Cloth copy of '{_from}' removed: {why}";
        if (warn) SoftbodyRuntime.Warn(line); else SoftbodyRuntime.Log(line);
        try { if (Slot != null && !Slot.IsRemoved && !Slot.IsDisposed) Slot.Destroy(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"removing the cloth copy of '{_from}': {ex.Message}"); }
    }

    void TearDown()
    {
        _ended = true;
        var inst = _inst;
        _inst = null;
        if (inst == null) return;
        try { inst.Teardown(); }
        catch (Exception ex) { SoftbodyRuntime.Warn($"cloth copy of '{_from}' teardown: {ex.Message}"); }
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
