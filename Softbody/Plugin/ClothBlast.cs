using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Cloth;

namespace Softbody;

/// <summary>
/// 0.31.0: a blast for cloth (DESIGN-cloth.md, "Cloth Blast"). Press Fire and every Cloth within Radius of this slot is
/// pushed outward from it (up to Strength m/s at the centre, less toward the edge). With Release, the points it reaches
/// that the paint or an anchor held (or MaxDistance kept near their place) are let go; with Shred, a Tearable cloth comes
/// apart there, from a few big pieces up to confetti. Release and Shred go by where the cloth was modelled (its mesh as
/// built), so they act on world cloth that still hangs near its modelled place; avatar cloth only gets the push.
///
/// Firing adds one synced <see cref="Cloth.Blast"/> to each cloth it reaches, so everyone sees the same blast, a client
/// that joins later sees the same freed points and pieces, and the cloth's Mend undoes it.
///
/// 0.32.0: Target picks the one cloth it hits (empty: every Cloth within Radius). Attached on a cloth's slot, or under it,
/// the blast fills Target in with that cloth. A Cloth with WaitForBlast sleeps until a blast hits it, then wakes with it.
/// </summary>
[Category(new[] { "Physics" })]
public class ClothBlast : Component
{
    /// <summary>0.32.0: the one cloth this blast hits (empty: every Cloth within Radius). Filled in with the Cloth on this
    /// slot, or the nearest one above it, when the blast is attached. It still has to be within Radius.</summary>
    public readonly SyncRef<Cloth> Target;
    /// <summary>Metres (0.05..20): how far it reaches.</summary>
    public readonly Sync<float> Radius;
    /// <summary>m/s outward at the centre (-50..50; below 0 it pulls in), falling off smoothly to 0 at the radius.</summary>
    public readonly Sync<float> Strength;
    /// <summary>0..1: how uneven the push is (patches of the cloth get more or less, a little sideways).</summary>
    public readonly Sync<float> Randomness;
    /// <summary>Lets go of the points it reaches (painted dark, at an anchor, or kept near their place by MaxDistance), as
    /// they were modelled. They stay free until the cloth is mended.</summary>
    public readonly Sync<bool> Release;
    /// <summary>0..1, on a Tearable cloth: tears it apart within the radius, from a few big pieces (low) to confetti (1).
    /// 0: only what the push overstretches tears.</summary>
    public readonly Sync<float> Shred;
    /// <summary>What the last Fire on this client hit (local, never synced).</summary>
    public readonly RawOutput<string> LastFired;

    protected override void OnAwake()
    {
        base.OnAwake();
        Radius.Value = 1f;
        Strength.Value = 8f;
        Randomness.Value = 0.3f;
        Release.Value = true;
        Shred.Value = 0.5f;
    }

    /// <summary>0.32.0: attached on a cloth's slot (or under one), the blast is that cloth's own: Target is set to it.</summary>
    protected override void OnAttach()
    {
        base.OnAttach();
        try
        {
            int guard = 0;
            for (var s = Slot; s != null && guard < 256; s = s.Parent, guard++)
            {
                var c = s.GetComponent<Cloth>();
                if (c != null) { Target.Target = c; break; }
                if (s.IsRootSlot) break;
            }
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Cloth Blast OnAttach: {ex.Message}"); }
    }

    /// <summary>Fires the blast (a button in the inspector; like the cloth's Mend, an Action other things can call). Runs on
    /// the client that presses it, which adds the blast to each cloth it reaches; the rest is synced from there.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void Fire()
    {
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            string result;
            lock (SoftbodyRuntime.Gate) result = FireNow();
            try { if (LastFired != null && LastFired.CanWrite) LastFired.Value = result; } catch { }
            SoftbodyRuntime.Log($"'{SlotName}' Cloth Blast fired: {result}");
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{SlotName}' Cloth Blast Fire: {ex.Message}"); }
    }

    string SlotName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }

    string FireNow()
    {
        if (IsRemoved || Slot == null) return "nothing: the blast was removed";
        if (!Enabled) return "nothing: the Cloth Blast is disabled";
        var gp = Slot.GlobalPosition;
        var center = new Vector3(gp.x, gp.y, gp.z);
        if (!float.IsFinite(center.X + center.Y + center.Z)) return "nothing: the slot's position is not finite";
        float radius = Clamp(Radius.Value, 0.05f, 20f, 1f);
        float strength = Clamp(Strength.Value, -50f, 50f, 0f);
        float randomness = Clamp(Randomness.Value, 0f, 1f, 0f);
        float shred = Clamp(Shred.Value, 0f, 1f, 0f);
        bool release = Release.Value;

        var cloths = new List<Cloth>();
        // 0.32.0: a Target (a reference that is set, even to a cloth that is gone) is the only cloth it may hit
        bool targeted = Target.Value != RefID.Null;
        if (targeted)
        {
            var t = Target.Target;
            if (t == null || t.IsRemoved) return "nothing: the Target cloth is gone";
            cloths.Add(t);
        }
        else SoftbodyRuntime.CollectCloths(World, cloths);
        int hit = 0, full = 0, notTearable = 0, avatars = 0, woke = 0;
        float targetAway = -1f;
        foreach (var c in cloths)
        {
            if (c.IsRemoved || !c.Enabled) continue;
            var r = c.Renderer.Target;
            if (r == null || r.IsRemoved || r.Slot == null || r.Slot.IsRemoved) continue;
            if (!c.WorldBox(out var lo, out var hi)) continue;
            float away = BoxDistance(center, lo, hi);
            if (!(away <= radius)) { if (targeted) targetAway = away; continue; }
            if (c.Blasts.Count >= Cloth.MaxBlasts) { full++; continue; }
            bool asleep = c.Asleep;
            var rs = r.Slot;
            float scale = MeanScale(rs.GlobalScale);
            if (!(scale > 1e-6f) || !float.IsFinite(scale)) continue;
            var b = c.Blasts.Add();
            b.Position.Value = rs.GlobalPointToLocal(gp);
            b.Radius.Value = radius / scale;
            b.Strength.Value = strength;
            b.Randomness.Value = randomness;
            b.Release.Value = release;
            b.Shred.Value = shred;
            b.Id.Value = NewId(c);
            hit++;
            if (asleep) woke++;
            if (r is SkinnedMeshRenderer) avatars++;
            else if (shred > 0f && !c.Tearable.Value) notTearable++;
        }
        if (targeted && hit == 0)
        {
            if (full > 0) return $"nothing: the Target cloth already took {Cloth.MaxBlasts} blasts (Mend it to blast it again)";
            if (targetAway >= 0f) return $"nothing: the Target cloth is {targetAway:0.##} m away, past Radius ({radius:0.##} m)";
            return "nothing: the Target cloth is disabled, or its renderer or mesh is missing";
        }
        string s = hit == 0 ? "no cloth within " + radius.ToString("0.##") + " m" : hit == 1 ? "1 cloth" : hit + " cloths";
        if (woke > 0) s += $" ({woke} woken up)";
        if (avatars > 0) s += $" ({avatars} on an avatar: pushed only)";
        if (notTearable > 0) s += $" ({notTearable} not Tearable: not shredded)";
        if (full > 0) s += $"; {full} already took {Cloth.MaxBlasts} blasts (Mend it to blast it again)";
        return s;
    }

    /// <summary>A blast id not yet on the cloth (1..2^30).</summary>
    static int NewId(Cloth c)
    {
        var used = new HashSet<int>();
        try { foreach (var b in c.Blasts) used.Add(b.Id.Value); } catch { }
        for (int k = 0; k < 64; k++)
        {
            int id = Random.Shared.Next(1, ClothBlastMath.MaxId + 1);
            if (!used.Contains(id)) return id;
        }
        return Random.Shared.Next(1, ClothBlastMath.MaxId + 1);
    }

    static float BoxDistance(Vector3 p, Vector3 lo, Vector3 hi)
    {
        var q = Vector3.Clamp(p, Vector3.Min(lo, hi), Vector3.Max(lo, hi));
        return Vector3.Distance(p, q);
    }

    static float MeanScale(float3 s) => (MathF.Abs(s.x) + MathF.Abs(s.y) + MathF.Abs(s.z)) / 3f;

    static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
}
