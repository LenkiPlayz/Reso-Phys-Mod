using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Fluid;

/// <summary>
/// 0.15.0: what one liquid is like. Every emitter has its own; its particles keep it, and liquids from different emitters
/// share one simulation (they meet, mix and push each other).
/// </summary>
public struct FluidLiquid
{
    /// <summary>0..1: how much each particle takes on its neighbours' motion per substep (XSPH): water 0.02, honey 0.5.</summary>
    public float Viscosity;
    /// <summary>0..1: how strongly it holds together (cohesion and surface tension): water 0.3, slime 0.9.</summary>
    public float Cohesion;
    /// <summary>0..1: how much it sticks to what it touches.</summary>
    public float Adhesion;
    /// <summary>0..1: how much it is slowed sliding along what it touches.</summary>
    public float Friction;
    /// <summary>Relative density (water 1): a heavier liquid sinks below a lighter one.</summary>
    public float Density;
    /// <summary>Multiplier of 9.81 m/s² (world -Y).</summary>
    public float Gravity;
    /// <summary>Per second: how fast its motion dies down.</summary>
    public float Damping;
    /// <summary>Seconds a particle lives (0 = until it is cleared or falls out of the world).</summary>
    public float Lifetime;
    /// <summary>Which collider categories it hits (<see cref="FluidSim.CategoryWorld"/> and the others).</summary>
    public uint CollideMask;
    /// <summary>Linear RGBA; A is the opacity.</summary>
    public Vector4 Color;

    public static FluidLiquid Water => new()
    {
        Viscosity = 0.02f, Cohesion = 0.3f, Adhesion = 0.1f, Friction = 0.1f, Density = 1f, Gravity = 1f, Damping = 0.05f,
        Lifetime = 0f, CollideMask = uint.MaxValue, Color = new Vector4(0.6f, 0.8f, 1f, 0.35f),
    };

    /// <summary>Clamps everything into its valid range.</summary>
    /// <summary>0.25.2: the ranges of Cohesion and Adhesion (0.15.0 to 0.25.1: 1 each).</summary>
    public const float MaxCohesion = 5f, MaxAdhesion = 10f;

    /// <summary>0.25.2: the least viscosity a liquid this cohesive has (0.75 for each unit over 1: cohesion 5 is at least 3).</summary>
    public static float CohesionViscosity(float cohesion) => 0.75f * MathF.Max(0f, cohesion - 1f);

    public void Sanitize()
    {
        static float C(float v, float lo, float hi, float d) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : d;
        Viscosity = C(Viscosity, 0f, 10f, 0.02f);   // 0.19.0: up to 10 (above 1: extra XSPH passes, thicker than honey)
        Viscosity = MathF.Max(Viscosity, CohesionViscosity(C(Cohesion, 0f, MaxCohesion, 0.3f)));
        // 0.25.2: cohesion up to 5 and adhesion up to 10 (from 1). Cohesion is a pull between particles, applied as a force:
        // past about 5 it overshoots against the density solve, and the liquid shook (10) or blew apart (20). Above 1 it also
        // thickens the liquid (CohesionViscosity, a friend of Lenki's idea): without it, cohesion 5 kept shaking. Adhesion only
        // takes away a particle's motion off a surface (up to 2 m/s per unit), so it cannot overshoot: 10 is glue.
        Cohesion = C(Cohesion, 0f, MaxCohesion, 0.3f);
        Adhesion = C(Adhesion, 0f, MaxAdhesion, 0.1f);
        Friction = C(Friction, 0f, 1f, 0.1f);
        Density = C(Density, 0.1f, 10f, 1f);
        Gravity = C(Gravity, -10f, 10f, 1f);
        Damping = C(Damping, 0f, 50f, 0.05f);
        Lifetime = C(Lifetime, 0f, 1e6f, 0f);
        if (!float.IsFinite(Color.X + Color.Y + Color.Z + Color.W)) Color = new Vector4(0.6f, 0.8f, 1f, 0.35f);
    }
}

/// <summary>Settings of one world's fluid simulation (the same for every liquid in it).</summary>
public sealed class FluidSettings
{
    /// <summary>Metres between neighbouring particles at rest (the simulation's resolution; one litre is 1/size³ particles).</summary>
    public float ParticleSize = 0.025f;
    /// <summary>Substeps per frame. 0.21.0: 4 (was 2), with 2 iterations each (was 3): a deep pool no longer boils. 0.25.0: per
    /// 60th of a second, of a fixed length: a frame runs as many as its time holds (see Step).</summary>
    public int Substeps = 4;
    /// <summary>Density iterations per substep.</summary>
    public int Iterations = 2;
    /// <summary>0.21.0: a particle that stays within this share of the spacing of one spot for <see cref="SleepAfter"/> seconds
    /// falls asleep: it stays put, costs (almost) nothing, and still holds up its awake neighbours. A resting pool's particles
    /// jitter in place (about 10 cm/s, but going nowhere), while a slow flow drifts off its spot. 0 turns sleeping off.</summary>
    public float SleepRadius = 0.25f;
    /// <summary>0.21.0: seconds within <see cref="SleepRadius"/> before a particle sleeps.</summary>
    public float SleepAfter = 1f;
    /// <summary>0.21.0: an awake particle faster than this wakes the sleeping ones within its reach. 0.23.0: in particle sizes
    /// per second (20: 0.5 m/s at 2.5 cm), since a liquid's jitter grows with its particles: at 7.5 cm a settled pool's
    /// particles kept passing 0.5 m/s and woke it for ever.</summary>
    public float WakeSpeed = 20f;
    /// <summary>0.23.0: a collider moving faster than this (m/s, whatever the particle size) wakes the sleepers near it.</summary>
    public float ShapeWakeSpeed = 0.5f;
    /// <summary>0.21.0: a collider moving faster than WakeSpeed wakes the sleepers within this many particle sizes of it, so a
    /// hand wakes the water ahead of it before it gets there (the water it pushes moves slower than WakeSpeed, and would
    /// otherwise be pressed against a sleeping wall). Waking on squeeze instead kept a deep pool's bottom awake.</summary>
    public float WakeReach = 8f;
    /// <summary>The most particles the whole simulation holds.</summary>
    public int MaxParticles = 20000;
    /// <summary>Particles below this height are removed.</summary>
    public float KillHeight = -100f;
    /// <summary>Particles outside this box are removed (0.15.1: the plugin keeps it around the emitters, so water that
    /// falls off the world or out of reach is gone instead of stretching everything after it).</summary>
    public Vector3 KillMin = new(-1e6f), KillMax = new(1e6f);
    /// <summary>The fastest a particle may move (m/s; a guard against a collider flinging it).</summary>
    public float MaxSpeed = 30f;
    /// <summary>0.25.0: 0.24.0's solver, kept as an emergency backup in case the new one misbehaves: no divergence pass, and
    /// particles fall asleep one by one instead of a tile at a time (with FluidSurface.Classic, no mesh smoothing either).</summary>
    public bool Classic;

    public FluidSettings Clone() => (FluidSettings)MemberwiseClone();

    public void Sanitize()
    {
        ParticleSize = Math.Clamp(float.IsFinite(ParticleSize) ? ParticleSize : 0.025f, 0.005f, 0.5f);
        Substeps = Math.Clamp(Substeps, 1, 8);
        Iterations = Math.Clamp(Iterations, 1, 10);
        WakeReach = float.IsFinite(WakeReach) ? Math.Clamp(WakeReach, 1f, 100f) : 8f;
        SleepRadius = float.IsFinite(SleepRadius) ? Math.Clamp(SleepRadius, 0f, 2f) : 0.25f;
        SleepAfter = float.IsFinite(SleepAfter) ? Math.Clamp(SleepAfter, 0.05f, 60f) : 1f;
        WakeSpeed = float.IsFinite(WakeSpeed) ? Math.Clamp(WakeSpeed, 0.5f, 2000f) : 20f;
        ShapeWakeSpeed = float.IsFinite(ShapeWakeSpeed) ? Math.Clamp(ShapeWakeSpeed, 0.01f, 50f) : 0.5f;
        MaxParticles = Math.Clamp(MaxParticles, 16, 1_000_000);
        if (!float.IsFinite(KillHeight)) KillHeight = -100f;
        MaxSpeed = Math.Clamp(float.IsFinite(MaxSpeed) ? MaxSpeed : 30f, 1f, 1000f);
    }
}

/// <summary>How an emitter adds liquid.</summary>
public enum FluidEmitMode { Pour, Volume }

/// <summary>One emitter as the simulation sees it this frame (world space).</summary>
public struct FluidEmitterState
{
    public int Source;                // its index in the simulation's sources (from AddSource)
    public FluidEmitMode Mode;
    public bool Active;               // Pour: pouring now
    public Vector3 Position;
    public Quaternion Rotation;       // Pour: along +Z
    public Vector3 Velocity;          // the emitter's own motion (added to what it pours)
    public float Rate;                // Pour: litres per second
    public float Speed;               // Pour: m/s out of the nozzle
    public float NozzleRadius;        // Pour: metres
    public int MaxParticles;          // its own cap
    public float RandomVelocity;      // 0.19.0: m/s, a random push in a random direction for each new particle
}

/// <summary>
/// 0.15.0: a particle liquid, Position Based Fluids (Macklin and Müller 2013, the method NVIDIA FleX and so GWater2 use),
/// engine-free and multithreaded. Each substep: gravity and the liquid's damping, a prediction, a spatial hash grid by
/// counting sort (the particles are reordered by cell, which keeps neighbours close in memory), neighbour lists, then a
/// few Jacobi iterations of the density constraint (only pushing apart: a free surface is not pulled together by it) with
/// a collision projection after each, and finally the velocity from the motion, XSPH viscosity, cohesion (Akinci et al.
/// 2013, a spline force pulling particles at mid range and pushing close ones), and friction and adhesion on colliders.
/// Every particle keeps the liquid of the source that made it: a pair of different liquids uses the mean of their
/// viscosities and a quarter of their cohesion (they barely mix), and a heavier particle pushes a lighter one more.
///
/// Colliders are the shapes the skinned softbody uses (<see cref="FleshCollider"/>, pushed out with
/// <see cref="XpbdSolver.Resolve(ref Vector3, float, in FleshCollider)"/>); mesh colliders are taken as their triangles near
/// the liquid. A coarse grid over the liquid's bounds hands each particle only the shapes near it. Not thread-safe:
/// one caller at a time (the plugin steps it on a worker, one frame behind).
/// </summary>
public sealed class FluidSim
{
    public const uint CategoryWorld = 1, CategoryHead = 2, CategoryBody = 4, CategoryHands = 8;

    public readonly FluidSettings Settings;
    public int Count { get; private set; }
    public Vector3[] X, V;
    public ushort[] Src;
    public float[] Age;
    Vector3[] _p, _dp, _vt, _contactN;
    float[] _lambda, _w, _lv;     // _w: inverse mass (1 / the liquid's relative density); _lv (0.25.0): the divergence pass's
    float[] _rho;                 // 0.20.0: each particle's number density (from the last density iteration)
    Vector3[] _nrm;               // 0.20.0: each particle's surface normal (Akinci: h Σ ∇W / ρ_j; zero inside the liquid)
    int[] _cx, _cy, _cz;          // each particle's grid cell (after the sort)
    bool[] _contact;
    int[] _nb, _nbCount;
    // the grid
    int[] _key, _order, _cellStart, _cellEnd;
    int _tableMask;
    // reorder scratch
    Vector3[] _tX, _tV; ushort[] _tSrc; float[] _tAge;
    // 0.21.0: sleeping. _calm: seconds this particle has stayed within SleepRadius of _anchor (asleep at SleepAfter or more;
    // set to 0 to wake it); _sleep: asleep in this substep
    float[] _calm, _tCalm; bool[] _sleep; Vector3[] _anchor, _tAnchor;
    // 0.25.0: the pressure (lambda) a sleeper had when it fell asleep: it keeps pushing its awake neighbours with it
    float[] _lamSleep, _tLamSleep;

    /// <summary>The liquid of each source (an emitter), by index.</summary>
    public readonly List<FluidLiquid> Liquids = new();
    readonly List<bool> _sourceLive = new();
    readonly List<int> _sourceCount = new();
    readonly List<float> _emitCarry = new();
    readonly List<int> _pourIndex = new();
    readonly Random _rng = new(1234);

    /// <summary>Colliders for the next step, set by the caller (world space; their Source byte is a category bit).</summary>
    public readonly List<FleshCollider> Colliders = new();
    readonly List<FleshCollider> _shapes = new();
    FleshCollider[] _shapeArr = Array.Empty<FleshCollider>();
    readonly List<int> _triHits = new();
    readonly HashSet<long> _occupied = new();
    readonly HashSet<int> _meshTris = new();
    float _occCell;
    // the coarse collider grid
    int[] _sgStart = Array.Empty<int>(), _sgList = Array.Empty<int>();
    int _sgNx, _sgNy, _sgNz; Vector3 _sgMin; float _sgCell;
    readonly List<int> _globalShapes = new();

    // constants of the kernels at this resolution
    float _h, _h2, _poly6, _spikyGrad, _rho0, _eps, _cohesionNorm, _radius, _cohC, _cohH6;
    const int MaxNb = 48;
    /// <summary>The most collider shapes (mesh triangles count one each) near the liquid in one step.</summary>
    public const int MaxShapes = 8000;
    /// <summary>m/s²: the most a particle's speed may grow by (10 g; a splash never needs more, a solver hiccup would).</summary>
    const float MaxGain = 100f;
    /// <summary>0.20.0: m/s² per neighbour at the cohesion spline's peak at Cohesion 1 (0.15.0 to 0.19.0: 1.5). With the
    /// surface boost below, a thin sheet now holds together instead of tearing into blobs.</summary>
    public const float CohesionStrength = 4.5f;
    /// <summary>0.20.0: the strength of Akinci's curvature term (m/s² per unit of normal difference at Cohesion 1): it
    /// flattens bumps and pulls a sheet's edge in. 0.4 already pulled thin sheets apart into single particles.</summary>
    public const float CurvatureStrength = 0.2f;
    /// <summary>0.21.0: how much of the neighbours' relative motion along the line between them is averaged away each substep
    /// (1 = all of it, 0 = none). A deep pool's pressure waves kept it moving at 10 to 45 cm/s for ever, and threw particles
    /// over a tank's walls; with this (and 4 substeps of 2 iterations) it settles to about 3 cm/s. A dam break spreads
    /// about a tenth less far.</summary>
    public const float PressureDamping = 1f;
    /// <summary>0.21.0: the relative density over which a particle counts as pressed (pressure-wave damping acts only where one
    /// of a pair is): a block filled at rest spacing sits at 1, and a RandomVelocity burst of it must fly apart.</summary>
    public const float PressedDensity = 1.01f;
    /// <summary>0.22.0: pressure-wave damping also acts between two surface particles of a liquid (under SurfaceDensity) moving
    /// slower than this (m/s) along the line between them (a splash, a burst or a kicked drop is far faster).</summary>
    public const float SlowPair = 4f;   // 0.23.0: particle sizes per second (0.1 m/s at 2.5 cm)
    /// <summary>0.22.0: under this share of rest density a particle counts as at the surface (for <see cref="SlowPair"/>).</summary>
    public const float SurfaceDensity = 0.9f;
    /// <summary>0.21.0: under this share of rest density (a thin sheet, a puddle, the top layer of a pool) a particle never
    /// sleeps, so a puddle's holes keep closing.</summary>
    public const float SleepDensity = 0.75f;
    /// <summary>0.24.0: with fewer neighbours than this (a sheet, a puddle, a top layer: the body has about 33) it never sleeps.</summary>
    public const int SleepNeighbours = 20;
    public const float SettledGap = 0.01f;
    /// <summary>0.25.0: tiles (see TileSleep): their width in particle sizes; the share of a tile's water that must have kept to
    /// its spot for SleepAfter before the tile sleeps; the share of the water near its surface (within TileBand particle sizes
    /// of its top) that leaves its spot within a frame that makes the tile restless (it wakes its own and its neighbours'
    /// sleepers); the seconds after a tile wakes during which its water's settling does not count as motion.</summary>
    public const float TileSize = 8f, TileStill = 0.7f, TileMoving = 0.05f, TileGrace = 0.25f, TileBand = 4f;

    /// <summary>0.25.0: the divergence pass acts from this share of rest density up (see Velocities).</summary>
    public const float DivDensity = 0.95f;

    public double StepMs { get; private set; }
    public int Contacts { get; private set; }
    public float MeanNeighbours { get; private set; }
    public int ShapeCount => _shapes.Count;
    public int Removed { get; private set; }
    /// <summary>0.21.0: how many particles were awake in the last step (the rest slept).</summary>
    public int Awake { get; private set; }
    /// <summary>0.21.0: whether the last step moved, added or removed any particle (false: the surface can stay as it is).</summary>
    public bool Moved { get; private set; } = true;
    bool _changed = true;
    /// <summary>0.21.0: whether particle i is asleep (tests, debug).</summary>
    public bool IsAsleep(int i) => Settings.SleepRadius > 0f && _calm[i] >= Settings.SleepAfter;
    /// <summary>0.21.0: wakes every particle (the plugin calls it when the liquid's settings change).</summary>
    public void WakeAll() { for (int i = 0; i < Count; i++) _calm[i] = 0f; _changed = true; }

    public FluidSim(FluidSettings settings)
    {
        Settings = settings.Clone();
        Settings.Sanitize();
        int n = Settings.MaxParticles;
        X = new Vector3[n]; V = new Vector3[n]; Src = new ushort[n]; Age = new float[n];
        _p = new Vector3[n]; _dp = new Vector3[n]; _vt = new Vector3[n]; _contactN = new Vector3[n];
        _lambda = new float[n]; _w = new float[n]; _lv = new float[n]; _contact = new bool[n]; _rho = new float[n]; _nrm = new Vector3[n];
        _cx = new int[n]; _cy = new int[n]; _cz = new int[n];
        _nb = new int[n * MaxNb]; _nbCount = new int[n];
        _key = new int[n]; _order = new int[n];
        _tX = new Vector3[n]; _tV = new Vector3[n]; _tSrc = new ushort[n]; _tAge = new float[n];
        _calm = new float[n]; _tCalm = new float[n]; _sleep = new bool[n]; _anchor = new Vector3[n]; _tAnchor = new Vector3[n];
        _lamSleep = new float[n]; _tLamSleep = new float[n];
        int table = 1; while (table < 2 * n) table <<= 1;
        _tableMask = table - 1;
        _cellStart = new int[table]; _cellEnd = new int[table];
        SetupKernels();
    }

    /// <summary>The spacing of particles at rest (metres).</summary>
    public float Spacing => Settings.ParticleSize;
    /// <summary>How far a particle keeps from colliders.</summary>
    public float CollisionRadius => _radius;
    /// <summary>The volume one particle stands for (m³).</summary>
    public float ParticleVolume => Spacing * Spacing * Spacing;

    void SetupKernels()
    {
        float s = Spacing;
        _h = 2f * s; _h2 = _h * _h;
        _poly6 = 315f / (64f * MathF.PI * MathF.Pow(_h, 9));
        _spikyGrad = -45f / (MathF.PI * MathF.Pow(_h, 6));
        _radius = 0.5f * s;
        _cohC = 32f / (MathF.PI * MathF.Pow(_h, 9)); _cohH6 = MathF.Pow(_h, 6) / 64f;
        // rest density and the constraint's stiffness scale from a perfect lattice at the rest spacing
        float rho = 0f; var grad = Vector3.Zero; float sumSq = 0f;
        int k = (int)MathF.Ceiling(_h / s);
        for (int x = -k; x <= k; x++)
            for (int y = -k; y <= k; y++)
                for (int z = -k; z <= k; z++)
                {
                    var d = new Vector3(x, y, z) * s;
                    float r2 = d.LengthSquared();
                    if (r2 >= _h2) continue;
                    rho += Poly6(r2);
                    if (r2 > 0f) { var g = SpikyGrad(d, MathF.Sqrt(r2)); grad += g; sumSq += g.LengthSquared(); }
                }
        _rho0 = rho;
        // relaxation (as a share of the constraint's own stiffness at rest): soft enough to stay stable with few iterations
        _eps = 0.05f * (sumSq + grad.LengthSquared()) / (_rho0 * _rho0);
        // cohesion spline normalisation (its largest value), so Cohesion 1 is a fixed strength
        float cmax = 0f;
        for (int i = 1; i < 100; i++) cmax = MathF.Max(cmax, CohesionSpline(_h * i / 100f));
        _cohesionNorm = cmax > 0f ? 1f / cmax : 0f;
    }

    float Poly6(float r2) { float d = _h2 - r2; return d > 0f ? _poly6 * d * d * d : 0f; }
    Vector3 SpikyGrad(Vector3 d, float r) { if (!(r > 1e-9f) || r >= _h) return Vector3.Zero; float t = _h - r; return d * (_spikyGrad * t * t / r); }

    /// <summary>Akinci's cohesion spline: positive (pull) at mid range, negative (push) when close, 0 beyond h.</summary>
    float CohesionSpline(float r)
    {
        if (r >= _h || r <= 0f) return 0f;
        float t = _h - r;
        float a = t * t * t * r * r * r;
        return 2f * r > _h ? _cohC * a : _cohC * (2f * a - _cohH6);
    }

    // ------------------------------------------------------------------ sources and particles

    /// <summary>Adds a source (an emitter) with its liquid; returns its index (the particles' Src).</summary>
    public int AddSource(FluidLiquid liquid)
    {
        liquid.Sanitize();
        for (int i = 0; i < _sourceLive.Count; i++)
            if (!_sourceLive[i] && _sourceCount[i] == 0) { Liquids[i] = liquid; _sourceLive[i] = true; _emitCarry[i] = 0f; _pourIndex[i] = 0; return i; }
        if (Liquids.Count >= ushort.MaxValue) throw new InvalidOperationException("too many fluid sources");
        Liquids.Add(liquid); _sourceLive.Add(true); _sourceCount.Add(0); _emitCarry.Add(0f); _pourIndex.Add(0);
        return Liquids.Count - 1;
    }

    /// <summary>Changes a source's liquid (live: its particles take it at once).</summary>
    public void SetLiquid(int source, FluidLiquid liquid)
    {
        liquid.Sanitize();
        // 0.21.0: a changed liquid wakes everything (it may flow now)
        if (!Same(Liquids[source], liquid)) WakeAll();
        Liquids[source] = liquid;
    }

    static bool Same(in FluidLiquid a, in FluidLiquid b) =>
        a.Viscosity == b.Viscosity && a.Cohesion == b.Cohesion && a.Adhesion == b.Adhesion && a.Friction == b.Friction && a.Density == b.Density &&
        a.Gravity == b.Gravity && a.Damping == b.Damping && a.Lifetime == b.Lifetime && a.CollideMask == b.CollideMask && a.Color == b.Color;

    /// <summary>Removes a source and all its particles (its index is reused later).</summary>
    public void RemoveSource(int source) { Clear(source); _sourceLive[source] = false; }

    /// <summary>How many particles a source has now.</summary>
    public int CountOf(int source) => source >= 0 && source < _sourceCount.Count ? _sourceCount[source] : 0;

    /// <summary>Adds one particle; false when the simulation is full.</summary>
    public bool Spawn(int source, Vector3 position, Vector3 velocity)
    {
        if (Count >= X.Length || source < 0 || source >= Liquids.Count || !float.IsFinite(position.X + position.Y + position.Z)) return false;
        int i = Count++;
        X[i] = position; V[i] = float.IsFinite(velocity.X + velocity.Y + velocity.Z) ? velocity : Vector3.Zero; Src[i] = (ushort)source; Age[i] = 0f;
        _calm[i] = 0f; _anchor[i] = X[i]; _changed = true;
        _sourceCount[source]++;
        return true;
    }

    /// <summary>Removes every particle of a source.</summary>
    public void Clear(int source)
    {
        int w = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Src[i] == source) continue;
            if (w != i) { X[w] = X[i]; V[w] = V[i]; Src[w] = Src[i]; Age[w] = Age[i]; _calm[w] = _calm[i]; _anchor[w] = _anchor[i]; _lamSleep[w] = _lamSleep[i]; }
            w++;
        }
        if (w != Count) _changed = true;
        Count = w;
        if (source >= 0 && source < _sourceCount.Count) _sourceCount[source] = 0;
    }

    /// <summary>0.15.2: removes the oldest particles of a source beyond <paramref name="max"/> (a lowered MaxParticles takes
    /// effect at once). Returns how many it removed.</summary>
    public int Trim(int source, int max)
    {
        if (source < 0 || source >= _sourceCount.Count) return 0;
        int have = _sourceCount[source], extra = have - Math.Max(0, max);
        if (extra <= 0) return 0;
        // the age the extra ones are older than: the extra-th largest age of this source
        var ages = new float[have]; int k = 0;
        for (int i = 0; i < Count; i++) if (Src[i] == source) ages[k++] = Age[i];
        Array.Sort(ages);
        float cutoff = ages[have - extra];
        int removed = 0, w = 0;
        for (int i = 0; i < Count; i++)
        {
            if (Src[i] == source && removed < extra && Age[i] >= cutoff) { removed++; continue; }
            if (w != i) { X[w] = X[i]; V[w] = V[i]; Src[w] = Src[i]; Age[w] = Age[i]; _calm[w] = _calm[i]; _anchor[w] = _anchor[i]; _lamSleep[w] = _lamSleep[i]; }
            w++;
        }
        if (w != Count) _changed = true;
        Count = w;
        _sourceCount[source] -= removed;
        return removed;
    }

    /// <summary>Fills an oriented box with particles at the rest spacing (a little jitter, so it does not stack like a
    /// crystal), at rest or moving with <paramref name="velocity"/>. Returns how many it added (it stops at the caps).</summary>
    public int FillBox(int source, Vector3 center, Quaternion rotation, Vector3 half, Vector3 velocity, int max = int.MaxValue, float randomVelocity = 0f)
    {
        float s = Spacing;
        int nx = Math.Max(1, (int)(2f * half.X / s)), ny = Math.Max(1, (int)(2f * half.Y / s)), nz = Math.Max(1, (int)(2f * half.Z / s));
        int added = 0;
        for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    if (added >= max) return added;
                    var local = new Vector3((x + 0.5f) * s - half.X, (y + 0.5f) * s - half.Y, (z + 0.5f) * s - half.Z) + Jitter(0.05f * s);
                    if (!Spawn(source, center + Vector3.Transform(local, rotation), velocity + RandomBall(randomVelocity))) return added;
                    added++;
                }
        return added;
    }

    /// <summary>0.19.0: a random velocity, even over the ball of that speed (0 = none).</summary>
    Vector3 RandomBall(float speed)
    {
        if (!(speed > 0f) || !float.IsFinite(speed)) return Vector3.Zero;
        Vector3 v;
        do v = new Vector3((float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1), (float)(_rng.NextDouble() * 2 - 1));
        while (v.LengthSquared() > 1f);
        return v * speed;
    }

    Vector3 Jitter(float a) => new((float)(_rng.NextDouble() * 2 - 1) * a, (float)(_rng.NextDouble() * 2 - 1) * a, (float)(_rng.NextDouble() * 2 - 1) * a);

    /// <summary>Pours from an emitter for <paramref name="dt"/> seconds: particles across the nozzle's disc (a sunflower
    /// spiral, so a stream is round and even), leaving at its speed along its +Z plus its own motion. The first ones are
    /// placed back along the stream by how far into the frame they would have left, so a fast stream is not in clumps.</summary>
    public int Emit(in FluidEmitterState e, float dt)
    {
        if (e.Mode != FluidEmitMode.Pour || !e.Active || !(dt > 0f) || e.Source < 0 || e.Source >= Liquids.Count) return 0;
        float perSecond = MathF.Max(0f, e.Rate) * 0.001f / ParticleVolume;
        float want = _emitCarry[e.Source] + perSecond * dt;
        int n = (int)want;
        _emitCarry[e.Source] = want - n;
        if (n <= 0) return 0;
        int cap = e.MaxParticles > 0 ? e.MaxParticles : int.MaxValue;
        var dir = Vector3.Transform(Vector3.UnitZ, e.Rotation);
        var ax = Vector3.Transform(Vector3.UnitX, e.Rotation); var ay = Vector3.Transform(Vector3.UnitY, e.Rotation);
        float radius = MathF.Max(0f, e.NozzleRadius), speed = MathF.Max(0f, e.Speed);
        var vel = dir * speed + e.Velocity;
        int added = 0;
        for (int k = 0; k < n; k++)
        {
            if (_sourceCount[e.Source] >= cap) { _emitCarry[e.Source] = 0f; break; }
            int idx = _pourIndex[e.Source]++;
            // a sunflower spiral over the disc
            float t = (idx % 64 + 0.5f) / 64f, ang = idx * 2.39996323f;
            float rr = radius * MathF.Sqrt(t);
            var off = ax * (rr * MathF.Cos(ang)) + ay * (rr * MathF.Sin(ang));
            float back = speed * dt * (k + 0.5f) / n;
            if (!Spawn(e.Source, e.Position + off + dir * back, vel + RandomBall(e.RandomVelocity))) break;
            added++;
        }
        return added;
    }

    // ------------------------------------------------------------------ the step

    public void Step(float dt)
    {
        var sw = Stopwatch.StartNew();
        Removed = 0;
        if (!(dt > 0f) || !float.IsFinite(dt)) { StepMs = 0; return; }
        // 0.25.0: substeps of a fixed length (Substeps in a 60th of a second), as many as the frame's time holds, at most a 30th
        // of a second's worth (a slower frame runs slow; its excess is dropped, not owed). With 4 substeps per frame whatever the
        // frame took, a substep at 30 fps was twice as long: gravity squeezed the water 4 times as far each substep, a pool sat
        // 8 % over rest density instead of 1.7 % and 28 mm lower, never slept, and with the frame rate changing as it does in
        // game it breathed up and down by up to 24 cm (Lenki: "the bouncing gets worse the lower the framerate is")
        int sub = Settings.Substeps;
        float h = 1f / (60f * sub);
        _acc = MathF.Min(_acc + dt, (2 * sub + 1) * h);
        int steps = Math.Min(2 * sub, (int)MathF.Floor(_acc / h + 1e-3f));
        _acc = Math.Clamp(_acc - steps * h, 0f, h);
        dt = steps * h;   // the time this frame simulates
        _frameSim = dt;
        PrepareShapes(MathF.Max(dt, h));
        WakeNearColliders();
        bool sleeping = Settings.SleepRadius > 0f;
        int awake = 0, unsettled = 0;
        float settledFor = 0.5f * Settings.SleepAfter;
        for (int i = 0; i < Count; i++) if (!sleeping || _calm[i] < Settings.SleepAfter) { awake++; if (_calm[i] < settledFor) unsettled++; }
        Awake = awake;
        // 0.25.0: with tiles, the only water awake is a few stragglers that have all stayed put for half the sleep time: nothing
        // to do either (a drop on a wall or a thin edge never sleeps, so its puddle's holes keep closing, and kept a whole sleeping
        // pool simulating every frame). (Not "none left its spot this frame": a small blob drifting slower than the sleep radius a
        // frame stopped dead.)
        if (Tiles && awake > 0 && unsettled == 0 && awake <= Math.Max(64, Count / 50) && !_changed) awake = 0;
        // 0.21.0: nothing awake: nothing moves this step (Box3D's sleeping islands; the grid and the neighbours are not even
        // rebuilt). Particles were only added, removed or woken by the calls above, which leave something awake.
        Moved = (awake > 0 && steps > 0) || _changed;
        _changed = false;
        if (awake > 0 && steps > 0)
        {
            // 0.21.0: the slow effects (viscosity, cohesion and curvature, adhesion) every `period` substeps with that many
            // substeps' time, so a frame gets as much of them as 0.20.0's 2 substeps gave (0.25.0: counted across frames)
            int period = Math.Max(1, sub / 2);
            for (int s = 0; s < steps; s++) Substep(h, (++_substeps) % period == 0, h * period);
            if (Tiles) TileSleep(dt);
        }
        else { Contacts = 0; MeanNeighbours = 0f; }
        int before = Count;
        Age_Kill(dt);
        if (Count != before) Moved = true;
        StepMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Milliseconds per phase over the last steps (predict, grid, neighbours, shape grid, density, collide, velocities).</summary>
    public readonly double[] PhaseMs = new double[7];

    /// <summary>0.21.0: wakes the sleeping particles near a collider moving faster than WakeSpeed, and near every still
    /// collider of the last step that is gone or moved without a velocity (a floor taken away from under a sleeping pool).</summary>
    void WakeNearColliders()
    {
        if (!(Settings.SleepRadius > 0f) || Count == 0) { _lastStill.Clear(); return; }
        float wake2 = Settings.ShapeWakeSpeed * Settings.ShapeWakeSpeed, reach = Settings.WakeReach * Spacing, stillReach = _h + _radius;
        _wakeBoxes.Clear();
        _stillNow.Clear();
        foreach (var c in Colliders)
        {
            if (c.Kind == FleshColliderKind.Plane) { _stillNow[StillKey(c)] = (new Vector3(float.MinValue), new Vector3(float.MaxValue)); continue; }
            if (c.Kind == FleshColliderKind.Mesh && c.Mesh == null) continue;
            c.Bounds(out var lo, out var hi);
            // (0.25.0: a user's part wakes as far as a fast collider: players could not wake a pool, as the plugin sent their
            // bones without a velocity (it now tracks them); and anything slower, even slowly, wakes the water it touches: a
            // paddle at 0.5 m/s, no faster than ShapeWakeSpeed, went through a sleeping pool without moving it)
            float v2 = c.Velocity.LengthSquared();
            if (v2 > SlowShape2)
            {
                var back = c.Velocity * (1f / 30f);
                float r = v2 > wake2 || c.Source != 0 ? reach : stillReach;
                _wakeBoxes.Add((Vector3.Min(lo, lo - back) - new Vector3(r), Vector3.Max(hi, hi - back) + new Vector3(r)));
            }
            else if (!IsMoving(c)) _stillNow[StillKey(c)] = (lo, hi);
        }
        int moving = _wakeBoxes.Count;
        foreach (var kv in _lastStill)
            if (!_stillNow.ContainsKey(kv.Key)) _wakeBoxes.Add((kv.Value.lo - new Vector3(stillReach), kv.Value.hi + new Vector3(stillReach)));
        (_lastStill, _stillNow) = (_stillNow, _lastStill);
        if (_wakeBoxes.Count == 0) return;
        // (0.25.0: where a still collider went, with tiles, with the grace a tile's wake gives: its settling is not motion; a wall
        // row coming and going above a room's pool woke its edge, whose settling then woke the tiles around it)
        float woken = Tiles ? -TileGrace : 0f;
        float after = Settings.SleepAfter;
        for (int i = 0; i < Count; i++)
        {
            if (_calm[i] < after) continue;
            var p = X[i];
            for (int b = 0; b < _wakeBoxes.Count; b++)
            {
                var (lo, hi) = _wakeBoxes[b];
                if (p.X >= lo.X && p.X <= hi.X && p.Y >= lo.Y && p.Y <= hi.Y && p.Z >= lo.Z && p.Z <= hi.Z) { _calm[i] = b < moving ? 0f : woken; _anchor[i] = p; break; }
            }
        }
    }

    readonly List<(Vector3 lo, Vector3 hi)> _wakeBoxes = new();
    Dictionary<long, (Vector3 lo, Vector3 hi)> _lastStill = new(), _stillNow = new();

    static long StillKey(in FleshCollider c)
    {
        var h = new HashCode();
        h.Add((int)c.Kind); h.Add(c.A); h.Add(c.B); h.Add(c.C); h.Add(c.R); h.Add(c.Q); h.Add(c.TwoSided);
        if (c.Mesh != null) { h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Mesh)); h.Add(c.M); }
        return h.ToHashCode() ^ ((long)(int)c.Kind << 40);
    }

    void Substep(float dt, bool slow = true, float slowDt = -1f)
    {
        if (!(slowDt > 0f)) slowDt = dt;
        var t = Stopwatch.StartNew();
        double Lap() { double e = t.Elapsed.TotalMilliseconds; t.Restart(); return e; }
        int n = Count;
        if (n == 0) { Contacts = 0; MeanNeighbours = 0; return; }
        var g = new Vector3(0f, -9.81f, 0f);
        var liquids = Liquids;
        bool sleeping = Settings.SleepRadius > 0f; float after = Settings.SleepAfter;
        // predict
        For(n, (a, b) =>
        {
            for (int i = a; i < b; i++)
            {
                if (sleeping && _calm[i] >= after) { V[i] = Vector3.Zero; _p[i] = X[i]; _contact[i] = false; _contactN[i] = Vector3.Zero; continue; }
                ref readonly var l = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(liquids)[Src[i]];
                var v = V[i] + g * (l.Gravity * dt);
                v *= MathF.Exp(-l.Damping * dt);
                V[i] = v;
                _p[i] = X[i] + v * dt;
                _contact[i] = false; _contactN[i] = Vector3.Zero;
            }
        });
        PhaseMs[0] += Lap();
        BuildGrid(n);
        for (int i = 0; i < n; i++) _sleep[i] = sleeping && _calm[i] >= after;
        PhaseMs[1] += Lap();
        Neighbours(n); PhaseMs[2] += Lap();
        BuildShapeGrid(n); PhaseMs[3] += Lap();
        for (int it = 0; it < Settings.Iterations; it++)
        {
            Lambdas(n);
            Deltas(n);
            For(n, (a, b) => { for (int i = a; i < b; i++) _p[i] += _dp[i]; });
            PhaseMs[4] += Lap();
            Collide(n, it == Settings.Iterations - 1);
            PhaseMs[5] += Lap();
        }
        Velocities(n, dt, slow, slowDt); PhaseMs[6] += Lap();
    }

    /// <summary>Computes the grid key of every predicted position, counting-sorts the particles by it (reordering all
    /// their arrays) and records each key's range.</summary>
    void BuildGrid(int n)
    {
        float inv = 1f / _h;
        for (int i = 0; i < n; i++) _key[i] = Hash(_p[i], inv);
        int table = _tableMask + 1;
        Array.Clear(_cellStart, 0, table);
        for (int i = 0; i < n; i++) _cellStart[_key[i]]++;
        int sum = 0;
        for (int c = 0; c < table; c++) { int k = _cellStart[c]; _cellStart[c] = sum; sum += k; }
        // _cellEnd as the placing cursor: afterwards it is each key's end
        Array.Copy(_cellStart, _cellEnd, table);
        for (int i = 0; i < n; i++) _order[_cellEnd[_key[i]]++] = i;
        for (int j = 0; j < n; j++)
        {
            int i = _order[j];
            _tX[j] = X[i]; _tV[j] = V[i]; _tSrc[j] = Src[i]; _tAge[j] = Age[i]; _dp[j] = _p[i]; _tCalm[j] = _calm[i]; _tAnchor[j] = _anchor[i]; _tLamSleep[j] = _lamSleep[i];
        }
        (X, _tX) = (_tX, X); (V, _tV) = (_tV, V); (Src, _tSrc) = (_tSrc, Src); (Age, _tAge) = (_tAge, Age); (_p, _dp) = (_dp, _p); (_calm, _tCalm) = (_tCalm, _calm); (_anchor, _tAnchor) = (_tAnchor, _anchor); (_lamSleep, _tLamSleep) = (_tLamSleep, _lamSleep);
        var liquids = Liquids;
        for (int j = 0; j < n; j++)
        {
            _w[j] = 1f / liquids[Src[j]].Density;
            var q = _p[j];
            _cx[j] = (int)MathF.Floor(q.X * inv); _cy[j] = (int)MathF.Floor(q.Y * inv); _cz[j] = (int)MathF.Floor(q.Z * inv);
        }
    }

    int Hash(Vector3 p, float inv)
    {
        int x = (int)MathF.Floor(p.X * inv), y = (int)MathF.Floor(p.Y * inv), z = (int)MathF.Floor(p.Z * inv);
        return HashCell(x, y, z);
    }

    int HashCell(int x, int y, int z) => (int)(((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791)) & (uint)_tableMask);

    void Neighbours(int n)
    {
        float wake2 = Settings.WakeSpeed * Spacing; wake2 *= wake2;
        var liquids = Liquids;
        long total = 0;
        object totalLock = new();
        For(n, (a, b) =>
        {
            long local = 0;
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _nbCount[i] = 0; continue; }
                var p = _p[i];
                bool waker = V[i].LengthSquared() > wake2;
                ushort si = Src[i]; float di = liquids[si].Density;
                int cx = _cx[i], cy = _cy[i], cz = _cz[i];
                int cnt = 0, baseIdx = i * MaxNb;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int qx = cx + dx, qy = cy + dy, qz = cz + dz;
                            int k = HashCell(qx, qy, qz);
                            for (int j = _cellStart[k], e = _cellEnd[k]; j < e; j++)
                            {
                                if (j == i || _cx[j] != qx || _cy[j] != qy || _cz[j] != qz) continue;
                                if (Vector3.DistanceSquared(p, _p[j]) >= _h2) continue;
                                if (waker && _sleep[j]) _calm[j] = 0f; // awake from the next substep on
                                // 0.21.0: beside a liquid of another density it stays awake (and so does that one): oil rising
                                // through water starts too slowly to keep it from sleeping
                                if (Src[j] != si && liquids[Src[j]].Density != di) { _calm[i] = 0f; if (_sleep[j]) _calm[j] = 0f; }
                                if (cnt < MaxNb) _nb[baseIdx + cnt++] = j;
                            }
                        }
                _nbCount[i] = cnt;
                local += cnt;
            }
            lock (totalLock) total += local;
        });
        MeanNeighbours = n > 0 ? (float)total / n : 0f;
    }

    /// <summary>The density constraint per particle (number density, the same rest density for every liquid, so they
    /// pack alike) and its Lagrange multiplier. The corrections are shared by inverse mass as in any position based
    /// dynamics: a light particle (oil) gives way more than a heavy one (water), and under gravity that is buoyancy.</summary>
    void Lambdas(int n)
    {
        For(n, (a, b) =>
        {
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _lambda[i] = 0f; _rho[i] = _rho0; continue; }
                var p = _p[i];
                float rho = Poly6(0f);
                var gi = Vector3.Zero; float sum = 0f;
                int baseIdx = i * MaxNb, cnt = _nbCount[i];
                for (int q = 0; q < cnt; q++)
                {
                    int j = _nb[baseIdx + q];
                    var d = p - _p[j];
                    float r2 = d.LengthSquared();
                    rho += Poly6(r2);
                    var gr = SpikyGrad(d, MathF.Sqrt(r2));
                    gi += gr;
                    if (!_sleep[j]) sum += _w[j] * gr.LengthSquared(); // 0.21.0: a sleeper does not give way
                }
                _rho[i] = rho;
                float c = rho / _rho0 - 1f;
                if (!(c > 0f)) { _lambda[i] = 0f; continue; } // only pushes apart: a free surface is not pulled in
                float denom = (sum + _w[i] * gi.LengthSquared()) / (_rho0 * _rho0) + _eps;
                _lambda[i] = -c / denom;
            }
        });
    }

    void Deltas(int n)
    {
        float maxStep = 0.5f * Spacing; // never further than half a spacing per iteration: nothing tunnels through a thin floor
        For(n, (a, b) =>
        {
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _dp[i] = Vector3.Zero; continue; }
                var p = _p[i];
                float li = _lambda[i];
                var dp = Vector3.Zero;
                int baseIdx = i * MaxNb, cnt = _nbCount[i];
                for (int q = 0; q < cnt; q++)
                {
                    int j = _nb[baseIdx + q];
                    // 0.21.0: a sleeper is a wall: it does not give way (it is left out of this particle's denominator), so
                    // this particle takes the whole correction itself. 0.25.0: its own lambda once; 0.21.0 to 0.24.0 added it
                    // twice, which pushed water lying on sleeping water out twice too far, and it bobbed (twice as fast: the
                    // bounce Lenki saw on a sleeping pool)
                    // (0.25.0: with tiles, a sleeper pushes with the pressure it had when it fell asleep: without it, the water
                    // beside or on top of a sleeper got half its push and sank into it, a pool squeezed to 6 % over rest density
                    // in pits; and woken, the frozen water settled back with a jolt that woke the water around it)
                    float lsum = li + (_sleep[j] ? (Tiles ? _lamSleep[j] : 0f) : _lambda[j]);
                    if (lsum == 0f) continue;
                    var d = p - _p[j];
                    dp += SpikyGrad(d, d.Length()) * lsum;
                }
                dp *= _w[i] / _rho0;
                float l2 = dp.LengthSquared();
                if (l2 > maxStep * maxStep) dp *= maxStep / MathF.Sqrt(l2);
                _dp[i] = dp;
            }
        });
    }

    // ------------------------------------------------------------------ colliders

    /// <summary>The colliders near the liquid for this frame: meshes as their triangles inside its bounds (with room for a
    /// frame's motion), everything else as given; the shapes' bounds are filled in.</summary>
    void PrepareShapes(float frameDt)
    {
        _shapes.Clear();
        _shapeArr = Array.Empty<FleshCollider>();
        if (Count == 0 || Colliders.Count == 0) return;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        float vmax = 0f;
        for (int i = 0; i < Count; i++) { lo = Vector3.Min(lo, X[i]); hi = Vector3.Max(hi, X[i]); vmax = MathF.Max(vmax, V[i].LengthSquared()); }
        float pad = _h + MathF.Min(MathF.Sqrt(vmax), Settings.MaxSpeed) * frameDt + _radius;
        lo -= new Vector3(pad); hi += new Vector3(pad);
        // the cells (about 25 cm) that hold particles, for taking mesh triangles only near the liquid
        _occupied.Clear();
        _occCell = MathF.Max(4f * _h, 0.25f);
        float invOcc = 1f / _occCell;
        for (int i = 0; i < Count; i++)
        {
            var q = X[i];
            int cx = (int)MathF.Floor(q.X * invOcc), cy = (int)MathF.Floor(q.Y * invOcc), cz = (int)MathF.Floor(q.Z * invOcc);
            _occupied.Add(((long)((cx + (1 << 20)) & 0x1FFFFF) << 42) | ((long)((cy + (1 << 20)) & 0x1FFFFF) << 21) | (long)((cz + (1 << 20)) & 0x1FFFFF));
        }
        // 0.19.0: moving shapes (users' parts, anything that moved since the last update) first, still ones after, and the
        // grid's lists before the global one in Collide: a floor has the last say over a box pressed into the liquid
        for (int pass = 0; pass < 2; pass++)
        foreach (var c0 in Colliders)
        {
            if (IsMoving(c0) != (pass == 0)) continue;
            var c = c0;
            if (c.Kind == FleshColliderKind.Mesh)
            {
                if (c.Mesh == null) continue;
                // 0.15.1: only the triangles near particles (cells of the liquid, not the box around all of it: a stream
                // falling onto a finely tessellated floor took every triangle under the whole stream)
                bool flip = c.M.GetDeterminant() < 0f;
                _meshTris.Clear();
                foreach (long key in _occupied)
                {
                    int cx = (int)(key >> 42) - (1 << 20), cy = (int)((key >> 21) & 0x1FFFFF) - (1 << 20), cz = (int)(key & 0x1FFFFF) - (1 << 20);
                    var cmin = new Vector3(cx, cy, cz) * _occCell - new Vector3(pad); var cmax = cmin + new Vector3(_occCell + 2f * pad);
                    FleshCollider.TransformBox(cmin, cmax, c.MInv, out var qmin, out var qmax);
                    _triHits.Clear(); c.Mesh.Query(qmin, qmax, _triHits);
                    foreach (int t in _triHits) _meshTris.Add(t);
                    if (_meshTris.Count >= MaxShapes) break;
                }
                foreach (int t in _meshTris)
                {
                    if (_shapes.Count >= MaxShapes) break;
                    var tri = FleshCollider.Triangle(Vector3.Transform(c.Mesh.A[t], c.M), Vector3.Transform(c.Mesh.B[t], c.M), Vector3.Transform(c.Mesh.C[t], c.M), c.TwoSided);
                    if (flip) { (tri.B, tri.C) = (tri.C, tri.B); tri.SetPlane(); }
                    tri.Source = c.Source; tri.Velocity = c.Velocity;
                    tri.Bounds(out tri.BMin, out tri.BMax);
                    _shapes.Add(tri);
                }
                continue;
            }
            c.Bounds(out c.BMin, out c.BMax);
            if (c.Kind != FleshColliderKind.Plane && !Overlap(c.BMin, c.BMax, lo, hi)) continue;
            _shapes.Add(c);
        }
        _shapeArr = _shapes.ToArray();
    }

    static bool IsMoving(in FleshCollider c) => c.Source != 0 || c.Velocity.LengthSquared() > SlowShape2;
    /// <summary>0.25.0: a collider faster than this (2 cm/s, squared) wakes the sleeping water it touches.</summary>
    const float SlowShape2 = 4e-4f;

    static bool Overlap(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;

    /// <summary>A coarse grid over the predicted positions' bounds; each shape is listed in the cells its bounds (plus a
    /// particle's reach) overlap. Planes go to a list every particle checks.</summary>
    void BuildShapeGrid(int n)
    {
        _globalShapes.Clear();
        _sgNx = _sgNy = _sgNz = 0;
        if (_shapes.Count == 0) return;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        float move2 = 0f;
        for (int i = 0; i < n; i++) { lo = Vector3.Min(lo, _p[i]); hi = Vector3.Max(hi, _p[i]); move2 = MathF.Max(move2, Vector3.DistanceSquared(_p[i], X[i])); }
        float reach = _radius + _h + MathF.Sqrt(move2);
        lo -= new Vector3(reach); hi += new Vector3(reach);
        var size = hi - lo;
        float cell = MathF.Max(4f * _h, MathF.Cbrt(MathF.Max(size.X * size.Y * size.Z, 1e-9f) / 32768f));
        _sgCell = cell; _sgMin = lo;
        _sgNx = Math.Max(1, (int)(size.X / cell) + 1); _sgNy = Math.Max(1, (int)(size.Y / cell) + 1); _sgNz = Math.Max(1, (int)(size.Z / cell) + 1);
        int cells = _sgNx * _sgNy * _sgNz;
        if (_sgStart.Length < cells + 1) _sgStart = new int[cells + 1];
        Array.Clear(_sgStart, 0, cells + 1);
        // two passes: count, then fill
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                int sum = 0;
                for (int c = 0; c < cells; c++) { int k = _sgStart[c]; _sgStart[c] = sum; sum += k; }
                _sgStart[cells] = sum;
                if (_sgList.Length < sum) _sgList = new int[sum];
            }
            var fill = pass == 1 ? (int[])_sgStart.Clone() : null;
            for (int s = 0; s < _shapes.Count; s++)
            {
                var c = _shapes[s];
                if (c.Kind == FleshColliderKind.Plane) { if (pass == 0) _globalShapes.Add(s); continue; }
                var a = Vector3.Max(c.BMin - new Vector3(reach), lo); var b = Vector3.Min(c.BMax + new Vector3(reach), hi);
                if (a.X > b.X || a.Y > b.Y || a.Z > b.Z) continue;
                int x0 = Cell(a.X - lo.X, _sgNx), x1 = Cell(b.X - lo.X, _sgNx), y0 = Cell(a.Y - lo.Y, _sgNy), y1 = Cell(b.Y - lo.Y, _sgNy), z0 = Cell(a.Z - lo.Z, _sgNz), z1 = Cell(b.Z - lo.Z, _sgNz);
                // a shape over a big part of the grid (a large floor triangle) is checked by every particle instead
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 512) { if (pass == 0) _globalShapes.Add(s); continue; }
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int ci = (z * _sgNy + y) * _sgNx + x;
                            if (pass == 0) _sgStart[ci]++;
                            else _sgList[fill[ci]++] = s;
                        }
            }
        }
    }

    int Cell(float d, int count) => Math.Clamp((int)(d / _sgCell), 0, count - 1);

    void Collide(int n, bool last)
    {
        if (_shapes.Count == 0) { if (last) Contacts = 0; return; }
        var shapes = _shapeArr;
        int contacts = 0;
        object cl = new();
        var liquids = Liquids;
        For(n, (a, b) =>
        {
            int local = 0;
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) continue;
                uint mask = liquids[Src[i]].CollideMask;
                var p = _p[i];
                var before = p;
                // a particle inside a box leaves the way it came (where it was at the start of the substep), so one pressed
                // into the seam between a floor and a wall goes back into the tank, not out under the wall
                var back = X[i] - p;
                float bl = back.Length();
                var prefer = bl > 1e-6f ? back / bl : Vector3.Zero;
                var x0 = X[i];
                int x = Cell(p.X - _sgMin.X, _sgNx), y = Cell(p.Y - _sgMin.Y, _sgNy), z = Cell(p.Z - _sgMin.Z, _sgNz);
                int ci = (z * _sgNy + y) * _sgNx + x;
                for (int q = _sgStart[ci], e = _sgStart[ci + 1]; q < e; q++) Push(ref p, x0, shapes[_sgList[q]], mask, prefer);
                foreach (int s in _globalShapes) Push(ref p, x0, shapes[s], mask, prefer);
                if (p != before)
                {
                    _p[i] = p;
                    // 0.15.2: any iteration's push counts (the first one moves a resting particle out, and the last one
                    // then finds it clear: friction and adhesion never applied to water lying on a floor)
                    var d = p - before; float l = d.Length();
                    _contact[i] = true; _contactN[i] = l > 1e-9f ? d / l : Vector3.UnitY;
                }
                if (last && _contact[i]) local++;
            }
            if (last) lock (cl) contacts += local;
        });
        if (last) Contacts = contacts;
    }

    void Push(ref Vector3 p, Vector3 x0, in FleshCollider c, uint mask, Vector3 prefer)
    {
        uint cat = c.Source == 0 ? CategoryWorld : c.Source;
        if ((cat & mask) == 0) return;
        float r = _radius;
        if (c.Kind != FleshColliderKind.Plane)
        {
            // the bounds of this substep's whole motion, so a fast particle still meets the triangle it crossed
            var lo = Vector3.Min(p, x0); var hi = Vector3.Max(p, x0);
            if (hi.X < c.BMin.X - r || lo.X > c.BMax.X + r || hi.Y < c.BMin.Y - r || lo.Y > c.BMax.Y + r || hi.Z < c.BMin.Z - r || lo.Z > c.BMax.Z + r) return;
        }
        if (c.Kind == FleshColliderKind.Triangle && Swept(ref p, x0, c, r)) return;
        if (c.Kind == FleshColliderKind.Box && BoxSwept(ref p, x0, c, r)) return;
        XpbdSolver.Resolve(ref p, r, c, prefer);
    }

    static float Comp(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>
    /// 0.18.0: a swept box. A particle whose path this substep (from where it started, x0, to p) went into the box itself or
    /// clean through it is put back on the face it came in by (the last slab it entered), keeping its sliding motion.
    /// 0.16.0 only caught particles that stopped inside: on a very thin box floor (Lenki's, 2 mm), a pile's pressure pushed
    /// particles wholly through it in one substep, where the plain push no longer saw them, and 12,000 particles sank through.
    /// A particle that started inside the box is left to the plain push.
    /// </summary>
    static bool BoxSwept(ref Vector3 p, Vector3 x0, in FleshCollider c, float r)
    {
        var qi = Quaternion.Conjugate(c.Q);
        var o = Vector3.Transform(x0 - c.A, qi);
        var local = Vector3.Transform(p - c.A, qi);
        var half = Vector3.Abs(c.B);
        var d = local - o;
        float tmin = -1e-7f, tmax = 1f, side = 1f;
        int axis = -1;
        for (int a = 0; a < 3; a++)
        {
            float oa = Comp(o, a), da = Comp(d, a), ha = Comp(half, a);
            if (MathF.Abs(da) < 1e-12f) { if (MathF.Abs(oa) >= ha) return false; continue; }
            float t1 = (-ha - oa) / da, t2 = (ha - oa) / da;
            float tn = MathF.Min(t1, t2), tf = MathF.Max(t1, t2);
            if (tn > tmin) { tmin = tn; axis = a; side = da < 0f ? 1f : -1f; }
            if (tf < tmax) tmax = tf;
            if (tmin > tmax) return false;
        }
        if (axis < 0) return false;
        float h = Comp(half, axis) + r;
        if (axis == 0) local.X = side * h; else if (axis == 1) local.Y = side * h; else local.Z = side * h;
        p = c.A + Vector3.Transform(local, c.Q);
        return true;
    }

    /// <summary>
    /// 0.15.1: a particle that crossed a triangle's front during this substep (from in front of it to less than a radius
    /// in front, through the triangle) is stopped at the surface, keeping its sliding motion. The plain push only sees
    /// particles within two radii of the plane, and a stream poured from a metre and a half moves further than that per
    /// substep: it fell straight through mesh floors (single-sided triangles), to the kill height.
    /// </summary>
    static bool Swept(ref Vector3 p, Vector3 x0, in FleshCollider c, float r)
    {
        var n = c.Nrm;
        if (n == Vector3.Zero) return false;
        float s0 = Vector3.Dot(x0, n) - c.D, s1 = Vector3.Dot(p, n) - c.D;
        float sign = 1f;
        if (!(s0 >= 0.5f * r && s1 < r))
        {
            if (!c.TwoSided || !(s0 <= -0.5f * r && s1 > -r)) return false;
            sign = -1f; s0 = -s0; s1 = -s1; // from behind, on a two-sided triangle
        }
        float denom = s0 - s1;
        float t = denom > 1e-9f ? Math.Clamp((s0 - r) / denom, 0f, 1f) : 0f;
        var q = x0 + (p - x0) * t;
        if (!OverTriangle(q, c, r)) return false;
        p += n * (sign * (r - s1));
        return true;
    }

    /// <summary>Whether q, projected onto the triangle's plane, is inside it or within <paramref name="margin"/> of it.</summary>
    static bool OverTriangle(Vector3 q, in FleshCollider c, float margin)
    {
        var n = c.Nrm;
        return Side(q, c.A, c.B, n, margin) && Side(q, c.B, c.C, n, margin) && Side(q, c.C, c.A, n, margin);
    }

    static bool Side(Vector3 q, Vector3 a, Vector3 b, Vector3 n, float margin)
    {
        var e = b - a;
        float l = e.Length();
        if (!(l > 1e-9f)) return true;
        return Vector3.Dot(Vector3.Cross(n, e), q - a) >= -margin * l;
    }

    // ------------------------------------------------------------------ velocities

    void Velocities(int n, float dt, bool slow, float slowDt)
    {
        float invDt = 1f / dt, maxV = Settings.MaxSpeed, frameDt = _frameSim;
        float sleepR2 = Settings.SleepRadius * Spacing; sleepR2 *= sleepR2;
        var liquids = Liquids;
        // the motion of this substep (into _dp, as the velocity before viscosity)
        float gain = MaxGain * dt;
        For(n, (a, b) =>
        {
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _dp[i] = Vector3.Zero; continue; }
                var v = (_p[i] - X[i]) * invDt;
                // water hitting something does not bounce off it: the push out of a collider leaves no outward speed
                if (_contact[i])
                {
                    float vn = Vector3.Dot(v, _contactN[i]);
                    if (vn > 0f) v -= _contactN[i] * (vn * 0.9f);
                }
                // a squeezed corner must not fling particles: speed grows by at most MaxGain per second
                float was = V[i].Length(), now = v.Length();
                float cap = MathF.Min(maxV, was + gain);
                if (!(now <= cap)) v = float.IsFinite(now) && now > 0f ? v * (cap / now) : Vector3.Zero;
                _dp[i] = v;
            }
        });
        // 0.25.0: the divergence pass (Bender and Koschier's divergence-free SPH, as a velocity-level step of the same density
        // constraint): where neighbours move towards each other inside the liquid, that squeezing part of their motion is
        // taken out, as the position step takes out the squeeze itself. Waves and flow move the water without squeezing it and
        // keep going; the jitter that kept a resting pool from ever settling (pushed apart, falling back, pushed apart) is
        // squeezing, and stops. Only inside the liquid (as DFSPH: at least SleepNeighbours neighbours), so drops and splashes
        // still land and merge.
        if (!Settings.Classic)
            {
                For(n, (a, b) =>
                {
                    for (int i = a; i < b; i++)
                    {
                        // (and only from DivDensity of rest density up, where coming closer is squeezing: well under it, as in a
                        // kicked drop that cohesion pulls back together, the pass undid cohesion; at rest density exactly it left
                        // half a resting pool's jitter, and a 12,000-particle pool took 10 s longer to sleep)
                        if (_sleep[i] || _nbCount[i] < SleepNeighbours || _rho[i] < DivDensity * _rho0) { _lv[i] = 0f; continue; }
                        var p = _p[i]; var v = _dp[i];
                        float cdot = 0f, sum = 0f; var gi = Vector3.Zero;
                        int baseIdx = i * MaxNb, cnt = _nbCount[i];
                        for (int q = 0; q < cnt; q++)
                        {
                            int j = _nb[baseIdx + q];
                            if (Src[j] != Src[i]) continue;   // (within one liquid: oil has to slide up past water)
                            var d = p - _p[j];
                            var gr = SpikyGrad(d, d.Length());
                            cdot += Vector3.Dot(gr, v - _dp[j]);
                            gi += gr;
                            if (!_sleep[j]) sum += _w[j] * gr.LengthSquared();
                        }
                        cdot /= _rho0;
                        if (!(cdot > 0f)) { _lv[i] = 0f; continue; }   // only squeezing is taken out
                        _lv[i] = -cdot / ((sum + _w[i] * gi.LengthSquared()) / (_rho0 * _rho0) + _eps);
                    }
                });
                For(n, (a, b) =>
                {
                    for (int i = a; i < b; i++)
                    {
                        if (_sleep[i]) { _vt[i] = _dp[i]; continue; }
                        var p = _p[i]; float li = _lv[i];
                        var dv = Vector3.Zero;
                        int baseIdx = i * MaxNb, cnt = _nbCount[i];
                        for (int q = 0; q < cnt; q++)
                        {
                            int j = _nb[baseIdx + q];
                            if (Src[j] != Src[i]) continue;
                            float lsum = li + (_sleep[j] ? 0f : _lv[j]);   // a sleeper is a wall, as in the position step
                            if (lsum == 0f) continue;
                            var d = p - _p[j];
                            dv += SpikyGrad(d, d.Length()) * lsum;
                        }
                        _vt[i] = _dp[i] + dv * (_w[i] / _rho0);
                    }
                });
                (_dp, _vt) = (_vt, _dp);
            }
        // 0.19.0: viscosity above 1 takes more XSPH passes (each at most a full average of the neighbours' velocities), all but
        // the last here, the last with cohesion and the colliders below
        float maxVisc = 0f;
        for (int l = 0; l < liquids.Count; l++) if (_sourceLive[l] || _sourceCount[l] > 0) maxVisc = MathF.Max(maxVisc, liquids[l].Viscosity);
        int passes = Math.Clamp((int)MathF.Ceiling(maxVisc), 1, 10);
        float vs = 1f / passes;
        // 0.21.0: only on the slow substeps (see Step)
        for (int pass = 0; slow && pass < passes - 1; pass++)
        {
            For(n, (a, b) =>
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(liquids);
                for (int i = a; i < b; i++)
                {
                    if (_sleep[i]) { _vt[i] = Vector3.Zero; continue; }
                    ref readonly var li = ref span[Src[i]];
                    var v = _dp[i]; var p = _p[i];
                    var visc = Vector3.Zero; float wsum = 0f;
                    int baseIdx = i * MaxNb, cnt = _nbCount[i];
                    for (int q = 0; q < cnt; q++)
                    {
                        int j = _nb[baseIdx + q];
                        float w = Poly6(Vector3.DistanceSquared(p, _p[j]));
                        visc += (_dp[j] - v) * (w * 0.5f * (li.Viscosity + span[Src[j]].Viscosity) * vs);
                        wsum += w;
                    }
                    _vt[i] = wsum > 0f ? v + visc / (wsum + Poly6(0f)) : v;
                }
            });
            (_dp, _vt) = (_vt, _dp);
        }
        // 0.20.0: surface tension (Akinci et al. 2013), before the last viscosity pass, the same order as the GPU's kernels.
        // First each particle's surface normal, n_i = h Σ ∇W_ij / ρ_j (zero inside the liquid, longest at its surface).
        float rhoFloor = 0.1f * _rho0;
        if (slow)
        For(n, (a, b) =>
        {
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _nrm[i] = Vector3.Zero; continue; }
                var p = _p[i]; var nn = Vector3.Zero;
                int baseIdx = i * MaxNb, cnt = _nbCount[i];
                for (int q = 0; q < cnt; q++)
                {
                    int j = _nb[baseIdx + q];
                    var d = p - _p[j];
                    nn += SpikyGrad(d, d.Length()) / MathF.Max(_rho[j], rhoFloor);
                }
                _nrm[i] = nn * _h;
            }
        });
        // Then cohesion (attraction only) plus the curvature term, both scaled by K_ij = 2ρ0 / (ρi + ρj): stronger where the
        // liquid is thin (a sheet, an edge, a bump), so a sheet holds together and its bumps sink back in. Before 0.20.0
        // cohesion alone (and at a third of this strength) let a puddle spread to one particle thick and tear into blobs,
        // and a particle on top of it never merged.
        float coh = CohesionStrength * _cohesionNorm, curvK = CurvatureStrength, kFloor = 0.2f * _rho0;
        if (slow)
        {
        For(n, (a, b) =>
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(liquids);
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { _vt[i] = Vector3.Zero; continue; }
                ref readonly var li = ref span[Src[i]];
                var v = _dp[i];
                var p = _p[i]; var ni = _nrm[i]; float rhoi = _rho[i];
                var pull = Vector3.Zero; var curv = Vector3.Zero;
                int baseIdx = i * MaxNb, cnt = _nbCount[i];
                for (int q = 0; q < cnt; q++)
                {
                    int j = _nb[baseIdx + q];
                    var d = p - _p[j];
                    float r = d.Length();
                    if (!(r > 1e-9f)) continue;
                    float gamma = Src[i] == Src[j] ? li.Cohesion : 0.25f * MathF.Min(li.Cohesion, span[Src[j]].Cohesion);
                    if (!(gamma > 0f)) continue;
                    float k = gamma * 2f * _rho0 / MathF.Max(rhoi + _rho[j], kFloor);
                    float cs = CohesionSpline(r);
                    if (cs > 0f) pull -= d * (k * cs / r);
                    if (!_sleep[j]) curv -= (ni - _nrm[j]) * k; // a sleeper's normal is not known
                }
                _vt[i] = v + (pull * coh + curv * curvK) * slowDt;
            }
        });
        (_dp, _vt) = (_vt, _dp);
        }
        For(n, (a, b) =>
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(liquids);
            for (int i = a; i < b; i++)
            {
                if (_sleep[i]) { V[i] = Vector3.Zero; continue; } // X stays (its _p is X)
                ref readonly var li = ref span[Src[i]];
                var v = _dp[i];
                var p = _p[i];
                var visc = Vector3.Zero; var squeeze = Vector3.Zero; float wsum = 0f;
                // (a surface particle: under SurfaceDensity, but not a thin sheet under SleepDensity, whose holes close slowly)
                bool pressed = _rho[i] > PressedDensity * _rho0, surfaceI = _rho[i] < SurfaceDensity * _rho0 && _rho[i] >= SleepDensity * _rho0;
                int baseIdx = i * MaxNb, cnt = _nbCount[i];
                for (int q = 0; q < cnt; q++)
                {
                    int j = _nb[baseIdx + q];
                    var d = p - _p[j];
                    float r2 = d.LengthSquared();
                    float w = Poly6(r2);
                    var rel = _dp[j] - v;
                    if (slow) visc += rel * (w * 0.5f * (li.Viscosity + span[Src[j]].Viscosity) * vs);
                    // 0.21.0: pressure-wave damping, every substep: the neighbours' relative motion along the line between
                    // them (squeezing together or apart), never the sideways part (flow, swirls, shear); only where one of
                    // them is pressed (over rest density): a splash or a kicked drop flies apart freely; and only within one
                    // liquid: oil has to slide up past water
                    // (0.22.0: and between two of its surface particles moving slowly against each other: a pool's top layer is
                    // not pressed, and jittered at up to 20 cm/s; inside the liquid that would slow buoyancy and pushed water)
                    if (r2 > 1e-12f && Src[j] == Src[i])
                    {
                        var dn = d * (1f / MathF.Sqrt(r2)); float rn = Vector3.Dot(rel, dn);
                        if (pressed || _sleep[j] || _rho[j] > PressedDensity * _rho0 ||
                            (surfaceI && _rho[j] < SurfaceDensity * _rho0 && _rho[j] >= SleepDensity * _rho0 && MathF.Abs(rn) < SlowPair * Spacing)) squeeze += dn * (rn * w);
                    }
                    wsum += w;
                }
                if (wsum > 0f) v += (visc + squeeze * PressureDamping) / (wsum + Poly6(0f));
                // colliders: friction against the surface's own motion, and adhesion pulling it back onto it
                if (_contact[i])
                {
                    var nrm = _contactN[i];
                    float vn = Vector3.Dot(v, nrm);
                    var vt = v - nrm * vn;
                    v = nrm * vn + vt * MathF.Exp(-MathF.Max(li.Friction, MathF.Min(li.Viscosity, 1f)) * 30f * dt);
                    if (slow && li.Adhesion > 0f && vn > 0f) v -= nrm * MathF.Min(vn, li.Adhesion * 2f);
                }
                X[i] = p;
                V[i] = v;
                // 0.21.0: how long it has stayed near its spot (a woken sleeper starts again from 0, from where it is)
                // (a thin sheet or the top layer, under SleepDensity of rest density, never sleeps: a puddle's holes close
                // slower than any sleep radius, and sleeping froze them open: 111 holes against 40 awake)
                // (0.24.0: nor with under SleepNeighbours neighbours: cohesion squeezes a one-particle sheet to rest density, and
                // the density test alone let it sleep with its holes open: 106 to 124 holes against 36)
                // (0.25.0: with tiles, nothing sleeps on its own: a tile puts its water to sleep together, see TileSleep)
                // (0.25.0: just woken by its tile (negative): a grace of TileGrace, in which it follows its spot; its settling into
                // the water around it is not motion: counted as such, a woken tile woke the tiles around it, and on over the pool)
                if (_calm[i] < 0f) { _calm[i] = MathF.Min(_calm[i] + dt, 0f); _anchor[i] = p; }
                else if (_calm[i] > 0f && Vector3.DistanceSquared(p, _anchor[i]) < sleepR2)
                {
                    _calm[i] += dt;
                    if (Tiles || !(_rho[i] >= SleepDensity * _rho0 && _nbCount[i] >= SleepNeighbours)) _calm[i] = MathF.Min(_calm[i], Settings.SleepAfter - SettledGap);
                }
                // (0.25.0: with tiles, one just woken or new (0) starts a frame on, so it does not count as having left its spot
                // this frame: a woken tile's water counted as moving, made it restless, and the wake swept the pool a tile a frame)
                else { _calm[i] = _calm[i] <= 0f && Tiles ? dt + frameDt : dt; _anchor[i] = p; }
            }
        });
    }

    bool Tiles => !Settings.Classic && Settings.SleepRadius > 0f;

    // ------------------------------------------------------------------ 0.25.0: tiles

    struct Tile { public int Count, Still, Moved, Asleep, Band, Frame; public float Top; public bool Restless, Settled; }
    readonly Dictionary<long, Tile> _tiles = new();
    int _tileFrame;
    // 0.25.0: the fixed substeps: the frame time not yet simulated, the substeps so far (for the slow effects' turn), and the
    // time the current frame simulates
    float _acc, _frameSim; long _substeps;

    static long TileKey(Vector3 p, float inv) => ((long)(int)MathF.Floor(p.X * inv) << 32) | (uint)(int)MathF.Floor(p.Z * inv);
    static long TileKey(int tx, int tz) => ((long)tx << 32) | (uint)tz;

    bool Sleepable(int i) => _rho[i] >= SleepDensity * _rho0 && _nbCount[i] >= SleepNeighbours;

    /// <summary>0.25.0: water sleeps a tile at a time: a column of the liquid TileSize particle sizes square, top to bottom.
    /// A tile's water falls asleep together once TileStill of it has kept to its spot (within SleepRadius) for SleepAfter and
    /// the tiles around it are settled too; a tile where TileMoving of the water left its spot within a frame is restless and
    /// wakes its own and its neighbours' sleepers, so a wave wakes the water ahead of it before it gets there.
    /// Particle by particle (0.21.0 to 0.24.0), the water froze while the water around it was still settling: from the bottom
    /// up it kept pits up to 8 cm deep (2.5 cm particles), and from the top down a lid with 15 cm holes, where the same pool
    /// awake levels to 2 mm: the "gel" look. A tile freezes the pool as it is when it is calm. These are the blocks of
    /// regional time stepping (Goswami and Batty 2014) with sleep as the slowest step, and Box2D's sleeping islands as columns.
    /// Motion is judged by place, not speed: the position solve turns a 1 mm correction into 24 cm/s, so speeds in resting
    /// water are noise, and a tile's freezing jolted its neighbours "fast" (tried: they woke it again every frame).</summary>
    void TileSleep(float dt)
    {
        float inv = 1f / (TileSize * Spacing), after = Settings.SleepAfter, still = after - 1.5f * SettledGap, moved = dt * 1.001f;
        int n = Count;
        _tileFrame++;
        foreach (var k in _tiles.Keys.ToArray())
        {
            ref var t = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_tiles, k);
            t.Count = 0; t.Still = 0; t.Moved = 0; t.Asleep = 0; t.Band = 0; t.Top = float.MinValue;
        }
        // its surface: the top of its liquid's body (not a drop or a thin sheet above it)
        for (int i = 0; i < n; i++)
        {
            if (!(_calm[i] >= after) && !Sleepable(i)) continue;
            ref var t = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_tiles, TileKey(X[i], inv), out bool had);
            if (!had) t.Top = float.MinValue;
            t.Top = MathF.Max(t.Top, X[i].Y);
        }
        float band = TileBand * Spacing;
        for (int i = 0; i < n; i++)
        {
            ref var t = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_tiles, TileKey(X[i], inv), out _);
            t.Frame = _tileFrame;
            float c = _calm[i];
            // (motion counts near the surface only, where it shows: deep down the packing sometimes slips into a closer one, a
            // shift of a few millimetres through a third of the pool, which woke the tiles around it every few seconds)
            bool near = X[i].Y > t.Top - band;
            if (near) t.Band++;
            if (c >= after) { t.Asleep++; continue; }
            if (near && c > 0f && c <= moved) t.Moved++;   // left its spot this frame
            // (stillness only of the water that can sleep: a thin sheet or a lone drop never does, and its jitter on a sleeping
            // tile kept that tile and so its neighbours from ever counting as settled)
            if (!Sleepable(i)) continue;
            t.Count++;
            if (c >= still) t.Still++;
        }
        foreach (var k in _tiles.Keys.ToArray())
        {
            ref var t = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_tiles, k);
            if (t.Frame != _tileFrame) { _tiles.Remove(k); continue; }
            t.Restless = t.Moved >= Math.Max(3, TileMoving * t.Band);
            t.Settled = t.Still >= TileStill * t.Count;   // (all asleep: settled)
        }
        for (int i = 0; i < n; i++)
        {
            int tx = (int)MathF.Floor(X[i].X * inv), tz = (int)MathF.Floor(X[i].Z * inv);
            bool restless = false, settled = true;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_tiles.TryGetValue(TileKey(tx + dx, tz + dz), out var u))
                    {
                        if (u.Restless) restless = true;
                        if (!u.Settled) settled = false;
                    }
            if (_calm[i] >= after) { if (restless) { _calm[i] = -TileGrace; _anchor[i] = X[i]; } continue; }
            // (a thin sheet or a lone drop never sleeps: Sleepable)
            if (!restless && settled && Sleepable(i)) { _calm[i] = after; _lamSleep[i] = _lambda[i]; }
        }
    }

    void Age_Kill(float dt)
    {
        int w = 0;
        float kill = Settings.KillHeight;
        var kmin = Settings.KillMin; var kmax = Settings.KillMax;
        var liquids = Liquids;
        for (int i = 0; i < Count; i++)
        {
            float age = Age[i] + dt;
            var x = X[i];
            float life = liquids[Src[i]].Lifetime;
            bool dead = !(x.Y > kill) || !float.IsFinite(x.X + x.Y + x.Z) || (life > 0f && age > life) ||
                        x.X < kmin.X || x.Y < kmin.Y || x.Z < kmin.Z || x.X > kmax.X || x.Y > kmax.Y || x.Z > kmax.Z;
            if (dead) { _sourceCount[Src[i]]--; Removed++; continue; }
            if (w != i) { X[w] = x; V[w] = V[i]; Src[w] = Src[i]; _calm[w] = _calm[i]; _anchor[w] = _anchor[i]; _lamSleep[w] = _lamSleep[i]; }
            Age[w] = age;
            w++;
        }
        Count = w;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The density of particle i relative to its liquid's rest density, at its current position (tests, debug).</summary>
    public float RelativeDensity(int i)
    {
        float rho = Poly6(0f);
        for (int j = 0; j < Count; j++)
        {
            if (j == i) continue;
            float r2 = Vector3.DistanceSquared(X[i], X[j]);
            if (r2 < _h2) rho += liquids_mass(j) * Poly6(r2);
        }
        return rho / (_rho0 * liquids_mass(i));
    }

    float liquids_mass(int j) => 1f;

    static void For(int n, Action<int, int> body)
    {
        const int chunk = 512;
        if (n <= chunk) { body(0, n); return; }
        int parts = (n + chunk - 1) / chunk;
        Parallel.For(0, parts, k => body(k * chunk, Math.Min(n, (k + 1) * chunk)));
    }
}
