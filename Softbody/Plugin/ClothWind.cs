using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Cloth;

namespace Softbody;

/// <summary>
/// 0.29.0: wind for every Cloth in the world, blowing along this slot's forward (blue) axis, with gusts and swirls.
/// With Radius 0 it blows everywhere; above 0 it is a fan: full strength within half the radius, fading out at it, and
/// only in front of the slot. Synced settings; each client's cloth feels the same wind (world time is its clock).
/// </summary>
[Category(new[] { "Physics" })]
public class ClothWind : Component
{
    /// <summary>m/s (-50..50). A breeze is 3..5, a strong wind 10..15.</summary>
    public readonly Sync<float> Strength;
    /// <summary>0..1: how much the strength rises and falls.</summary>
    public readonly Sync<float> Gusts;
    /// <summary>Roughly how many gusts a second (0..5).</summary>
    public readonly Sync<float> GustRate;
    /// <summary>0..1: swirls, as a fraction of the strength (what makes a flag flap).</summary>
    public readonly Sync<float> Turbulence;
    /// <summary>Metres: how big the swirls are (0.1..20).</summary>
    public readonly Sync<float> SwirlSize;
    /// <summary>Metres: 0 blows everywhere; above 0 only within this distance, in front of the slot (a fan).</summary>
    public readonly Sync<float> Radius;

    bool _registered;

    protected override void OnAwake()
    {
        base.OnAwake();
        Strength.Value = 5f;
        Gusts.Value = 0.3f;
        GustRate.Value = 0.3f;
        Turbulence.Value = 0.3f;
        SwirlSize.Value = 1.5f;
        Radius.Value = 0f;
    }

    protected override void OnStart()
    {
        base.OnStart();
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            lock (SoftbodyRuntime.Gate) { SoftbodyRuntime.Register(this); _registered = true; }
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"ClothWind OnStart: {ex.Message}"); }
    }

    protected override void OnDestroy()
    {
        Unregister();
        base.OnDestroy();
    }

    public override void OnWorldDestroy()
    {
        Unregister();
        // no base call: the engine's ComponentBase.OnWorldDestroy throws by design (only overrides may run; 0.26.2)
    }

    void Unregister()
    {
        try { lock (SoftbodyRuntime.Gate) { if (_registered) { SoftbodyRuntime.Unregister(this); _registered = false; } } }
        catch { }
    }

    /// <summary>World thread: this wind as the solver sees it; false when it is off (disabled, inactive or still).</summary>
    internal bool TryRead(out ClothWindSource s)
    {
        s = default;
        try
        {
            if (IsRemoved || !Enabled || Slot == null || !Slot.IsActive) return false;
            float strength = Strength.Value;
            if (!float.IsFinite(strength) || strength == 0f) return false;
            var p = Slot.GlobalPosition;
            var f = Slot.LocalDirectionToGlobal(float3.Forward);
            var dir = new Vector3(f.x, f.y, f.z);
            float l = dir.Length();
            if (!(l > 1e-6f) || !float.IsFinite(l)) return false;
            s = new ClothWindSource
            {
                Position = new Vector3(p.x, p.y, p.z),
                Direction = dir / l,
                Strength = Math.Clamp(strength, -50f, 50f),
                Gusts = Clamp(Gusts.Value, 0f, 1f),
                GustRate = Clamp(GustRate.Value, 0f, 5f),
                Turbulence = Clamp(Turbulence.Value, 0f, 1f),
                SwirlSize = Math.Max(0.1f, Clamp(SwirlSize.Value, 0.1f, 20f)),
                Radius = Clamp(Radius.Value, 0f, 10000f),
                Seed = (int)(ReferenceID.Position & 0x3FF),
            };
            return float.IsFinite(s.Position.X + s.Position.Y + s.Position.Z);
        }
        catch { return false; }
    }

    static float Clamp(float v, float lo, float hi) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : lo;
}
