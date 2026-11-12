using System.Numerics;

namespace Softbody.Core.Fluid;

/// <summary>One emitter as the plugin reads it this frame (world space; engine-free).</summary>
public struct FluidEmitterInput
{
    public int Id;                    // stable per emitter
    public FluidEmitMode Mode;
    public bool Active;               // enabled and its slot active
    public bool Pouring;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 VolumeHalf;        // Volume: half the block, already scaled with the slot
    public float Rate, Speed, NozzleRadius;
    public float RandomVelocity;      // 0.19.0
    public int MaxParticles;
    public int RefillCount, ClearCount;
    public FluidLiquid Liquid;
}

/// <summary>
/// 0.15.2: what the emitters do to the simulation each frame, engine-free (so it is tested offline; in 0.15.1 it lived in
/// the plugin's runtime). Every emitter gets a source with its liquid (kept live); an emitter that is gone takes its
/// liquid with it. Volume emitters fill their block when they first run and whenever RefillCount changes (the old liquid
/// goes first); ClearCount changes remove an emitter's liquid; MaxParticles is a live cap (a lowered one removes the
/// oldest particles over it, and a pouring emitter stops at it). Pouring emitters are listed for <see cref="FluidSim.Emit"/>
/// with their own motion. Liquid further than 50 m from every emitter (20 m below the lowest) is removed.
/// </summary>
public sealed class FluidDirector
{
    sealed class Entry
    {
        public int Source = -1;
        public int Refills, Clears;
        public bool Filled, HasLast;
        public Vector3 LastPos;
        public int Seen;
    }

    public readonly FluidSim Sim;
    readonly Dictionary<int, Entry> _entries = new();
    readonly List<int> _gone = new();
    int _frame;

    /// <summary>The pouring emitters of the last <see cref="Sync"/>, for <see cref="FluidSim.Emit"/>.</summary>
    public readonly List<FluidEmitterState> Pours = new();
    /// <summary>The union of the emitters' collide masks in the last <see cref="Sync"/>.</summary>
    public uint CollideMask { get; private set; }
    public int Refilled { get; private set; }

    public FluidDirector(FluidSim sim) { Sim = sim; }

    /// <summary>How many particles an emitter has now (0 if it has none or is unknown).</summary>
    public int CountOf(int id) => _entries.TryGetValue(id, out var e) && e.Source >= 0 ? Sim.CountOf(e.Source) : 0;

    public bool Has(int id) => _entries.ContainsKey(id);

    /// <summary>Brings the simulation in line with this frame's emitters. Call between steps (never while one runs).</summary>
    public void Sync(IReadOnlyList<FluidEmitterInput> emitters, float dt)
    {
        _frame++;
        Pours.Clear();
        Refilled = 0;
        uint mask = 0;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int k = 0; k < emitters.Count; k++)
        {
            var e = emitters[k];
            if (!_entries.TryGetValue(e.Id, out var en))
            {
                en = new Entry { Source = Sim.AddSource(e.Liquid), Refills = e.RefillCount, Clears = e.ClearCount };
                _entries[e.Id] = en;
            }
            en.Seen = _frame;
            Sim.SetLiquid(en.Source, e.Liquid);
            mask |= e.Liquid.CollideMask;
            lo = Vector3.Min(lo, e.Position); hi = Vector3.Max(hi, e.Position);
            var vel = en.HasLast && dt > 0f ? (e.Position - en.LastPos) / dt : Vector3.Zero;
            if (!(vel.LengthSquared() < 400f)) vel = Vector3.Zero; // a teleport is not a throw
            en.LastPos = e.Position; en.HasLast = true;
            int cap = Math.Max(1, e.MaxParticles);
            // a cleared Volume emitter stays empty until Refill (it counts as filled)
            if (e.ClearCount != en.Clears) { Sim.Clear(en.Source); en.Clears = e.ClearCount; }
            if (e.Mode == FluidEmitMode.Volume)
            {
                if (e.Active && (!en.Filled || e.RefillCount != en.Refills))
                {
                    Sim.Clear(en.Source);
                    Sim.FillBox(en.Source, e.Position, e.Rotation, Vector3.Abs(e.VolumeHalf), vel, cap, e.RandomVelocity);
                    en.Filled = true; en.Refills = e.RefillCount;
                    Refilled++;
                }
            }
            else
            {
                en.Refills = e.RefillCount;
                if (e.Active && e.Pouring)
                    Pours.Add(new FluidEmitterState
                    {
                        Source = en.Source, Mode = FluidEmitMode.Pour, Active = true, Position = e.Position, Rotation = e.Rotation, Velocity = vel,
                        Rate = e.Rate, Speed = e.Speed, NozzleRadius = e.NozzleRadius, MaxParticles = cap, RandomVelocity = e.RandomVelocity,
                    });
            }
            Sim.Trim(en.Source, cap);
        }
        // emitters that are gone take their liquid with them
        _gone.Clear();
        foreach (var (id, en) in _entries) if (en.Seen != _frame) _gone.Add(id);
        foreach (int id in _gone) { Sim.RemoveSource(_entries[id].Source); _entries.Remove(id); }
        CollideMask = mask;
        if (lo.X <= hi.X)
        {
            Sim.Settings.KillMin = lo - new Vector3(50f, 20f, 50f);
            Sim.Settings.KillMax = hi + new Vector3(50f, 50f, 50f);
        }
    }

    /// <summary>One frame of simulation: the pouring, then the step.</summary>
    public void Step(float dt)
    {
        foreach (var p in Pours) Sim.Emit(p, dt);
        Sim.Step(dt);
    }
}
