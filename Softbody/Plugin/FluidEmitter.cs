using System.Numerics;
using Elements.Core;
using FrooxEngine;
using Softbody.Core.Fluid;

namespace Softbody;

/// <summary>
/// 0.15.0: pours a liquid from this slot, or fills a block of it. Each emitter has its own liquid (viscosity, cohesion,
/// stickiness, density, colour...), and all emitters in a world share one simulation on each client (see
/// <see cref="FluidWorld"/>), so liquids meet, mix and push each other. Pour sends the liquid out along the slot's +Z
/// (blue arrow) at Speed, Rate litres a second, across a nozzle NozzleRadius wide; Volume fills a VolumeSize block
/// centred on the slot (scaled with it) when it starts and on Refill. It collides with solid world colliders
/// (CharacterCollider) and users' bodies, which push it around (one way).
///
/// Synced and saved: the settings. Local: the particles; every client simulates its own splashes, so two people see the
/// same pour but not the same drops.
/// </summary>
[Category(new[] { "Physics" })]
public class FluidEmitter : Component
{
    public enum EmitMode { Pour, Volume }
    /// <summary>0.16.0: where the world's liquid is simulated. Auto and GPU: on the graphics card when this client's renderer
    /// is the Softbody renderer fork (else on the CPU); CPU: always on the CPU (any emitter set to CPU puts the world's
    /// liquid there).</summary>
    public enum SolverMode { Auto, CPU, GPU }

    // ---- how it adds liquid
    public readonly Sync<EmitMode> Mode;
    /// <summary>Pour: pouring now.</summary>
    public readonly Sync<bool> Pouring;
    /// <summary>Pour: litres per second (0..5000 on the GPU).</summary>
    public readonly Sync<float> Rate;
    /// <summary>Pour: m/s out of the nozzle, along the slot's +Z.</summary>
    public readonly Sync<float> Speed;
    /// <summary>Pour: metres.</summary>
    public readonly Sync<float> NozzleRadius;
    /// <summary>Volume: the block's size in metres (scaled with the slot).</summary>
    public readonly Sync<float3> VolumeSize;
    /// <summary>0.19.0: m/s, a random push in a random direction for every new particle, poured or filled (0 = none): a
    /// Volume with a high one bursts like an explosion of water, a Pour sprays.</summary>
    public readonly Sync<float> RandomVelocity;
    /// <summary>The most particles this emitter keeps (one particle is about 15 ml at the default size; at most 20,000 on
    /// the CPU and 262,144 on the GPU for the whole world).</summary>
    public readonly Sync<int> MaxParticles;

    // ---- the liquid (live)
    /// <summary>0..20: water 0.02, oil 0.1, honey 0.6; above 1 thicker still (0.19.0: goo, mud).</summary>
    public readonly Sync<float> Viscosity;
    /// <summary>0.27.0, GPU only: makes a Viscosity above 1 a real viscosity (an implicit solve, Weiler et al. 2018) instead of
    /// extra smoothing passes. A thick liquid then keeps a spin and a rounded shape, sags and folds instead of slumping, and the
    /// same setting is as thick at every particle size: about 4 is honey, 10 is 0.08 m²/s, 15 (the most it takes) 0.9 m²/s.
    /// Costs GPU time (about 10 ms a frame at 15 for 100,000 particles of it). Off: as before. On the CPU it does nothing.</summary>
    public readonly Sync<bool> ThickLiquid;
    /// <summary>0..5 (0.25.2; was 0..1): how strongly it holds together (surface tension): water 0.3, slime 0.9; above 1 it beads
    /// up and thickens too (a viscosity of at least 0.75 for each unit over 1).</summary>
    public readonly Sync<float> Cohesion;
    /// <summary>0..10 (0.25.2; was 0..1): how much it sticks to what it touches (10 is glue).</summary>
    public readonly Sync<float> Adhesion;
    /// <summary>0..1: how much it is slowed sliding over things.</summary>
    public readonly Sync<float> Friction;
    /// <summary>Relative to water (0.1..10): a lighter liquid floats on a heavier one.</summary>
    public readonly Sync<float> Density;
    /// <summary>Gravity multiplier (-10..10).</summary>
    public readonly Sync<float> Gravity;
    /// <summary>Per second: how fast its motion dies down.</summary>
    public readonly Sync<float> Damping;
    /// <summary>Seconds each particle lasts (0 = until cleared or it falls out of the world).</summary>
    public readonly Sync<float> Lifetime;
    /// <summary>Its colour; alpha is how opaque it is.</summary>
    public readonly Sync<colorX> Color;
    /// <summary>0.27.0, GPU only: spray, foam and bubbles where it splashes, breaks into waves or traps air (Ihmsen et al. 2012),
    /// drawn as small soft discs, whiter than the liquid (0 = none, 1 the usual amount, up to 10). Calm liquid makes none.</summary>
    public readonly Sync<float> Foam;
    /// <summary>0.27.0: seconds its foam lasts (0.1..60; half to 4/3 of it, longer after bigger splashes). Spray and bubbles age a
    /// tenth as fast, until they land or rise to the top and become foam.</summary>
    public readonly Sync<float> FoamLifetime;

    // ---- collisions
    /// <summary>Collide at all: solid world colliders (CharacterCollider) and the users' parts below.</summary>
    public readonly Sync<bool> Collide;
    public readonly Sync<bool> CollideHead;
    /// <summary>Torso, arms, legs and feet.</summary>
    public readonly Sync<bool> CollideBody;
    public readonly Sync<bool> CollideHands;
    /// <summary>Metres: further than this from every emitter, your client stops simulating the liquid (0 = never).</summary>
    public readonly Sync<float> CullDistance;
    /// <summary>0.16.0: CPU or GPU (see <see cref="SolverMode"/>).</summary>
    public readonly Sync<SolverMode> Solver;
    /// <summary>0.17.0: metres between particles at rest (0.19.0: 0.005..0.5; 0.025 is 15 ml a particle, 0.05 is 125 ml). One
    /// size for the whole world: the largest of its emitters'. Changing it starts the world's liquid over (half a second after
    /// the last change, so typing a value does not restart it at every key).</summary>
    public readonly Sync<float> ParticleSize;
    /// <summary>0.17.0: how fine the surface mesh is (0.5..3; 1 is a grid of 0.8 particle sizes, 2 twice as fine, with about
    /// 4x the vertices). The world uses the largest of its emitters'. Live.</summary>
    public readonly Sync<float> SurfaceDetail;
    /// <summary>0.18.0: how big lone droplets, small clusters and thin sheets are drawn (0.5..3; 1 is their own volume, 1.5
    /// half again as wide), fading to the real size in the liquid's body; bigger droplets also join up sooner. The world
    /// uses the largest of its emitters'. Live.</summary>
    public readonly Sync<float> DropletSize;

    // ---- actions everyone sees (counters: a press on one client refills or clears on every client)
    [HideInInspector] public readonly Sync<int> RefillCount;
    [HideInInspector] public readonly Sync<int> ClearCount;

    // ---- status (local)
    public readonly RawOutput<string> Status;
    public readonly RawOutput<int> Particles;
    public readonly RawOutput<int> WorldParticles;
    public readonly RawOutput<float> StepMs;
    public readonly RawOutput<float> SurfaceMs;

    FluidWorld _fluid;

    protected override void OnAwake()
    {
        base.OnAwake();
        var w = FluidLiquid.Water;
        Mode.Value = EmitMode.Pour;
        Pouring.Value = true;
        Rate.Value = 1f;
        Speed.Value = 1.5f;
        NozzleRadius.Value = 0.02f;
        VolumeSize.Value = new float3(0.3f, 0.3f, 0.3f);
        RandomVelocity.Value = 0f;
        MaxParticles.Value = 4000;
        Viscosity.Value = w.Viscosity;
        ThickLiquid.Value = false;
        Cohesion.Value = w.Cohesion;
        Adhesion.Value = w.Adhesion;
        Friction.Value = w.Friction;
        Density.Value = w.Density;
        Gravity.Value = w.Gravity;
        Damping.Value = w.Damping;
        Lifetime.Value = 0f;
        Color.Value = new colorX(w.Color.X, w.Color.Y, w.Color.Z, w.Color.W, Renderite.Shared.ColorProfile.Linear);
        Foam.Value = 0f;
        FoamLifetime.Value = 3f;
        Collide.Value = true;
        CollideHead.Value = true; CollideBody.Value = true; CollideHands.Value = true;
        CullDistance.Value = 30f;
        Solver.Value = SolverMode.Auto;
        ParticleSize.Value = 0.025f;
        SurfaceDetail.Value = 1f;
        DropletSize.Value = 1.5f;
    }

    protected override void OnStart()
    {
        base.OnStart();
        try
        {
            SoftbodyRuntime.EnsureInitialized();
            _fluid = FluidWorld.For(World);
            _fluid.Register(this);
        }
        catch (Exception ex) { SetStatus("error: " + ex.Message, 0, 0, 0, 0); SoftbodyRuntime.Warn($"'{SafeName}' FluidEmitter start: {ex}"); }
    }

    protected override void OnDestroy()
    {
        try { _fluid?.Unregister(this); } catch (Exception ex) { SoftbodyRuntime.Warn($"'{SafeName}' FluidEmitter removed: {ex.Message}"); }
        _fluid = null;
        base.OnDestroy();
    }

    public override void OnWorldDestroy()
    {
        try { _fluid?.Unregister(this); } catch { }
        _fluid = null;
        // no base call: the engine's ComponentBase.OnWorldDestroy throws by design (only overrides may run; 0.26.2)
    }

    /// <summary>Volume: fills the block again (on every client). Pour: nothing. An inspector button, and callable over ResoniteLink.</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void Refill()
    {
        try { RefillCount.Value++; } catch (Exception ex) { SoftbodyRuntime.Warn($"'{SafeName}' Refill: {ex.Message}"); }
    }

    /// <summary>Removes this emitter's liquid (on every client).</summary>
    [SyncMethod(typeof(Action), new string[0])]
    public void ClearLiquid()
    {
        try { ClearCount.Value++; } catch (Exception ex) { SoftbodyRuntime.Warn($"'{SafeName}' ClearLiquid: {ex.Message}"); }
    }

    /// <summary>This emitter's liquid, from its fields.</summary>
    internal FluidLiquid ReadLiquid()
    {
        uint mask = 0;
        if (Collide.Value)
        {
            mask = FluidSim.CategoryWorld;
            if (CollideHead.Value) mask |= FluidSim.CategoryHead;
            if (CollideBody.Value) mask |= FluidSim.CategoryBody;
            if (CollideHands.Value) mask |= FluidSim.CategoryHands;
        }
        var c = Color.Value.ToProfile(Renderite.Shared.ColorProfile.Linear);
        var l = new FluidLiquid
        {
            Viscosity = Viscosity.Value, Cohesion = Cohesion.Value, Adhesion = Adhesion.Value, Friction = Friction.Value, Density = Density.Value,
            Gravity = Gravity.Value, Damping = Damping.Value, Lifetime = Lifetime.Value, CollideMask = mask,
            Color = new Vector4(c.r, c.g, c.b, c.a),
        };
        l.Sanitize();
        return l;
    }

    // the status the fluid runtime last reported (0.15.2: a RawOutput can only be written from its own component's
    // update, so the runtime leaves it here and OnCommonUpdate shows it)
    string _state = "starting";
    int _mine, _all;
    float _stepMs, _surfaceMs;

    internal void SetStatus(string state, int mine, int all, float stepMs, float surfaceMs)
    {
        _state = state; _mine = mine; _all = all; _stepMs = stepMs; _surfaceMs = surfaceMs;
    }

    protected override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        try
        {
            Set(Status, _state);
            Set(Particles, _mine);
            Set(WorldParticles, _all);
            Set(StepMs, (float)Math.Round(_stepMs, 3));
            Set(SurfaceMs, (float)Math.Round(_surfaceMs, 3));
        }
        catch { }
    }

    static void Set<T>(RawOutput<T> field, T value)
    {
        try { if (field != null && field.CanWrite) field.Value = value; } catch { }
    }

    string SafeName { get { try { return Slot?.Name ?? "?"; } catch { return "?"; } } }
}
