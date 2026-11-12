using Elements.Core;
using FrooxEngine;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody;

/// <summary>
/// 0.26.0: puffs of gas from this slot: smoke, steam, mist. Each puff leaves along the slot's +Z (blue arrow) at Speed, rises
/// (Buoyancy), takes on the swirling air's motion (Drag, Swirl), grows from StartSize to EndSize and fades out over its
/// Lifetime. Puffs are pushed out of solid colliders and users' bodies, and a moving hand or object carries the gas near it.
/// The puffs are soft discs facing you, drawn with an unlit material in the gas's colour (alpha: each puff's opacity).
/// On the GPU only: it needs the Softbody renderer fork (as the liquid's GPU path); without it the status says so. All gas
/// emitters in a world share its liquid's world (see <see cref="FluidWorld"/>). Synced and saved: the settings. Local: the
/// puffs (each client simulates its own). See DESIGN-gas.md.
/// </summary>
[Category(new[] { "Physics" })]
public class GasEmitter : Component
{
    public readonly Sync<bool> Emitting;
    /// <summary>Puffs a second (0..5000).</summary>
    public readonly Sync<float> Rate;
    /// <summary>Metres a second out of the nozzle, along the slot's +Z.</summary>
    public readonly Sync<float> Speed;
    /// <summary>Metres: how wide the nozzle is.</summary>
    public readonly Sync<float> NozzleRadius;
    /// <summary>0..1: how far each puff's direction strays from the nozzle's.</summary>
    public readonly Sync<float> Spread;
    /// <summary>Seconds each puff lives (0.1..60).</summary>
    public readonly Sync<float> Lifetime;
    /// <summary>m/s² upwards: hot smoke and steam rise; negative for a heavy gas that sinks and spreads along the floor.</summary>
    public readonly Sync<float> Buoyancy;
    /// <summary>1/s: how quickly a puff takes on the air's motion (higher: it slows sooner and follows the swirl and your hand more).</summary>
    public readonly Sync<float> Drag;
    /// <summary>Metres a second: how strongly the air swirls (0: none).</summary>
    public readonly Sync<float> Swirl;
    /// <summary>0.27.0: how strongly things moving through the gas (a hand, an object) leave lasting swirls behind them: small vortex
    /// rings that travel and carry the smoke along for a few seconds (0 = none, as before; 1 the usual amount; up to 10).</summary>
    public readonly Sync<float> Vortices;
    /// <summary>Metres: a puff's width at birth and at the end of its life.</summary>
    public readonly Sync<float> StartSize;
    public readonly Sync<float> EndSize;
    /// <summary>The gas's colour; its alpha is each puff's opacity (low values look best: puffs overlap).</summary>
    public readonly Sync<colorX> Color;

    // ---- what it collides with (as FluidEmitter)
    public readonly Sync<bool> Collide;
    public readonly Sync<bool> CollideHead;
    public readonly Sync<bool> CollideBody;
    public readonly Sync<bool> CollideHands;

    /// <summary>Its puffs at most (Rate is held to MaxPuffs / Lifetime).</summary>
    public readonly Sync<int> MaxPuffs;
    /// <summary>Metres: further than this from every emitter, your client stops simulating (0 = never).</summary>
    public readonly Sync<float> CullDistance;

    // ---- read only
    public readonly RawOutput<string> Status;
    public readonly RawOutput<int> WorldPuffs;

    protected override void OnAwake()
    {
        base.OnAwake();
        Emitting.Value = true;
        Rate.Value = 30f;
        Speed.Value = 0.4f;
        NozzleRadius.Value = 0.03f;
        Spread.Value = 0.15f;
        Lifetime.Value = 5f;
        Buoyancy.Value = 0.5f;
        Drag.Value = 1.5f;
        Swirl.Value = 0.3f;
        Vortices.Value = 0f;
        StartSize.Value = 0.1f;
        EndSize.Value = 0.6f;
        Color.Value = new colorX(0.85f, 0.85f, 0.88f, 0.25f, Renderite.Shared.ColorProfile.Linear);
        Collide.Value = true;
        CollideHead.Value = true; CollideBody.Value = true; CollideHands.Value = true;
        MaxPuffs.Value = 2000;
        CullDistance.Value = 30f;
    }

    FluidWorld _world;

    protected override void OnStart()
    {
        base.OnStart();
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            _world = FluidWorld.For(World);
            _world.RegisterGas(this);
        }
        catch (Exception ex) { SetStatus("error: " + ex.Message, 0); SoftbodyRuntime.Warn($"'{SafeName}' GasEmitter start: {ex}"); }
    }

    protected override void OnDestroy()
    {
        try { _world?.UnregisterGas(this); } catch (Exception ex) { SoftbodyRuntime.Warn($"'{SafeName}' GasEmitter removed: {ex.Message}"); }
        _world = null;
        base.OnDestroy();
    }

    /// <summary>This emitter as a channel record (world thread).</summary>
    internal GasEmitterRecord Record(int id, System.Numerics.Vector3 velocity)
    {
        uint mask = 0;
        if (Collide.Value)
        {
            mask = FluidSim.CategoryWorld;
            if (CollideHead.Value) mask |= FluidSim.CategoryHead;
            if (CollideBody.Value) mask |= FluidSim.CategoryBody;
            if (CollideHands.Value) mask |= FluidSim.CategoryHands;
        }
        var pos = Slot.GlobalPosition; var rot = Slot.GlobalRotation;
        var c = Color.Value.ToProfile(Renderite.Shared.ColorProfile.Linear);
        static float F(float v, float lo, float hi, float d) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : d;
        return new GasEmitterRecord
        {
            Id = id, Active = Enabled && Slot.IsActive ? 1 : 0, Emitting = Emitting.Value ? 1 : 0,
            MaxPuffs = Math.Clamp(MaxPuffs.Value, 1, SoftbodyRuntime.MaxGasPuffs),
            Rate = F(Rate.Value, 0f, 5000f, 30f), Speed = F(Speed.Value, 0f, 50f, 0.4f), NozzleRadius = F(NozzleRadius.Value, 0f, 10f, 0.03f),
            Lifetime = F(Lifetime.Value, 0.1f, 60f, 5f), Buoyancy = F(Buoyancy.Value, -20f, 20f, 0.5f), Drag = F(Drag.Value, 0f, 50f, 1.5f),
            Swirl = F(Swirl.Value, 0f, 10f, 0.3f), StartSize = F(StartSize.Value, 0.005f, 10f, 0.1f), EndSize = F(EndSize.Value, 0.005f, 20f, 0.6f),
            PosX = pos.x, PosY = pos.y, PosZ = pos.z, RotX = rot.x, RotY = rot.y, RotZ = rot.z, RotW = rot.w,
            VelX = velocity.X, VelY = velocity.Y, VelZ = velocity.Z,
            ColR = c.r, ColG = c.g, ColB = c.b, ColA = Math.Clamp(c.a, 0f, 1f),
            CollideMask = mask, Spread = F(Spread.Value, 0f, 1f, 0.15f),
            Vortices = F(Vortices.Value, 0f, 10f, 0f),   // 0.27.0
        };
    }

    // the outputs can only be written from this component's own update
    string _state = "starting";
    int _puffs;

    internal void SetStatus(string state, int puffs) { _state = state; _puffs = puffs; }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try { Set(Status, _state); Set(WorldPuffs, _puffs); } catch { }
    }

    static void Set<T>(RawOutput<T> field, T value)
    {
        try { if (field != null && field.CanWrite) field.Value = value; } catch { }
    }

    string SafeName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }
}
