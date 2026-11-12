using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;

namespace Softbody.Core;

/// <summary>A static collision proxy for something in the world (world space).</summary>
public struct ProxyShape
{
    /// <summary>Stable id of the world collider this stands for, so it keeps its body while it moves.</summary>
    public int Key;
    public bool IsSphere;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 HalfExtents; // box
    public float Radius;        // sphere

    public static ProxyShape Box(Vector3 position, Quaternion rotation, Vector3 halfExtents, int key = 0) =>
        new() { Key = key, Position = position, Rotation = rotation, HalfExtents = halfExtents };

    public static ProxyShape Sphere(Vector3 position, float radius, int key = 0) =>
        new() { Key = key, IsSphere = true, Position = position, Rotation = Quaternion.Identity, Radius = radius };

    public readonly bool SameShape(in ProxyShape o) =>
        IsSphere == o.IsSphere && Vector3.DistanceSquared(HalfExtents, o.HalfExtents) < 1e-8f && MathF.Abs(Radius - o.Radius) < 1e-4f;
}

/// <summary>Mutable parameters read by the Bepu callbacks (the callback structs keep a reference).</summary>
public sealed class SimParams
{
    public Vector3 Gravity = new(0, -9.81f, 0);
    public float LinearDamping = 0.4f;
    public float Friction = 0.8f;
    public bool Collide = true;
    /// <summary>Indexed by BodyHandle.Value: true for the kinematic bodies that stand for world colliders.</summary>
    public bool[] ProxyBodies = new bool[16];

    public bool IsProxy(CollidableReference r) =>
        r.Mobility == CollidableMobility.Kinematic && (uint)r.BodyHandle.Value < (uint)ProxyBodies.Length && ProxyBodies[r.BodyHandle.Value];
}

/// <summary>Cage points never touch each other; only dynamic points touch the ground (static) and
/// world-collider proxies (kinematic bodies that move with their colliders).</summary>
struct CageNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SimParams Params;

    public void Initialize(Simulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b)
    {
        if (!Params.Collide) return false;
        if (a.Mobility == CollidableMobility.Static) return b.Mobility == CollidableMobility.Dynamic;
        if (b.Mobility == CollidableMobility.Static) return a.Mobility == CollidableMobility.Dynamic;
        if (a.Mobility == CollidableMobility.Dynamic && Params.IsProxy(b)) return true;
        if (b.Mobility == CollidableMobility.Dynamic && Params.IsProxy(a)) return true;
        return false; // point-point (any mobility) never collides
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = Params.Friction;
        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30f, 1f);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

/// <summary>Gravity and damping for dynamic points; rotation is locked (zero inverse inertia).</summary>
struct CagePoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public SimParams Params;
    Vector3 _gravityDt;
    float _dampingDt;

    public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _gravityDt = Params.Gravity * dt;
        _dampingDt = MathF.Exp(-Params.LinearDamping * dt);
    }

    public void IntegrateVelocity(int bodyIndex, in RigidPose pose, in BodyInertia localInertia, int workerIndex, ref BodyVelocity velocity)
    {
        if (localInertia.InverseMass <= 0f) return; // kinematic: we set its velocity ourselves
        velocity.Linear = (velocity.Linear + _gravityDt) * _dampingDt;
        velocity.Angular = default;
    }
}

/// <summary>What <see cref="CageSimulation.ApplyTuning"/> did.</summary>
public enum TuneResult
{
    /// <summary>Nothing tunable changed.</summary>
    Unchanged,
    /// <summary>Applied to the running simulation (springs, mass, iterations and/or callback parameters).</summary>
    Applied,
    /// <summary>A structural setting changed (cells, pin, radius, substeps): the cage must be rebuilt.</summary>
    NeedsRebuild,
}

/// <summary>
/// Owns a private Bepu simulation for one cage (world space): one small sphere body per cage
/// point, CenterDistance springs on tet edges, a Volume constraint per tet, and a OneBodyLinearServo
/// pulling each dynamic point to its rest position in the anchor's current frame. Firm points are
/// kinematic and follow the anchor exactly. Single-threaded, no FrooxEngine types.
/// Everything except the structural settings can be re-tuned live with <see cref="ApplyTuning"/>.
/// </summary>
public sealed class CageSimulation : IDisposable
{
    public readonly Cage Cage;
    /// <summary>The clamped settings in effect (tunable fields are updated in place by <see cref="ApplyTuning"/>).</summary>
    public readonly SoftbodySettings Settings;
    public readonly SimParams Params = new();
    public float RadiusWorld { get; private set; }
    public int ResetCount { get; private set; }
    public long SubstepCount { get; private set; }
    public int ServoCount { get; private set; }
    public int EdgeConstraintCount { get; private set; }
    public int VolumeConstraintCount { get; private set; }
    public double RestVolumeWorld { get; private set; }
    public bool HasGround => _hasGround;
    public int ProxyCount => _proxies.Count;
    public string LastResetReason { get; private set; }
    /// <summary>How many times <see cref="ApplyTuning"/> changed the running simulation.</summary>
    public int TuneCount { get; private set; }
    public int SolverIterations => _sim?.Solver.IterationCount ?? 0;

    BufferPool _pool;
    Simulation _sim;
    readonly BodyHandle[] _bodies;
    readonly ConstraintHandle[] _servoHandles;
    readonly OneBodyLinearServo[] _servos;
    readonly Vector3[] _targets;
    // Per-constraint data kept for live re-tuning (ApplyDescription with new springs, same rest state).
    readonly List<ConstraintHandle> _edgeHandles = new();
    readonly List<float> _edgeRest = new(), _edgeSoft = new();
    readonly List<ConstraintHandle> _volHandles = new();
    readonly List<float> _volTarget = new(), _volSoft = new();
    // Untangling: a cage pushed hard (deep presses, weak volume springs, fine cells) can flip tets inside
    // out. Edge springs are just as happy with a mirrored tet, so it would stay folded. Points of tets
    // that are inside out (or squashed nearly flat) get a strong pull back to their rest place until the
    // tet is the right way round again, plus a short hold.
    readonly List<int> _volTets = new();          // start index into Cage.Tets per volume constraint
    readonly List<float> _volRest = new();        // signed rest volume per volume constraint
    float[] _untangleTimer;
    bool[] _boosted;
    Vector3[] _posScratch;
    const float UntangleHold = 0.3f;
    /// <summary>Volume constraints that were inside out or nearly flat at the end of the last step.</summary>
    public int InvertedTets { get; private set; }
    /// <summary>Points currently being pulled back by the untangler.</summary>
    public int UntanglingPoints { get; private set; }
    TypedIndex _pointShape;
    AnchorPose _lastAnchor;
    Vector3 _buildScale;
    float _acc;
    float _cageWorldSize;
    bool _disposed;

    // Skinned mode (0.8.0): targets are the cage points' linear-blend-skinned rest positions instead of the anchor's
    // transform of them, interpolated linearly from last frame's to this frame's across the substeps. The edge and
    // volume constraints' rest state follows the skinned shape, so a bending joint is not fought by the springs.
    // Anchor mode is the special case of one rigid bone (the slot) and keeps its own code path unchanged.
    readonly CageSkin _skin;
    Matrix4x4[] _pointMats, _nextMats;        // blended skin matrix per point: of _targets / of _nextTargets
    Matrix4x4[] _pointInv;                    // world offset -> bind offset: rotation part of _pointMats, over the point's scale
    float[] _pointScale, _nextScale;          // weighted average of each point's bones' own scales
    bool[] _singular;                         // blended matrix not invertible: the point gets no offset
    Vector3[] _nextTargets, _prevTargets;
    float[] _volBind;                         // bind-space volume x skin scale^3 per volume constraint
    readonly List<SpringSettings> _edgeSpr = new(), _volSpr = new();
    /// <summary>True when the targets come from skinning (see <see cref="StepSkinned"/>).</summary>
    public bool IsSkinned => _skin != null;
    /// <summary>Skinned mode: constraint rest states re-applied in the last stepped frame (they follow the skinned shape).</summary>
    public int RestUpdates { get; private set; }
    /// <summary>Skinned mode: points whose blended skin matrix could not be inverted (they get no offset).</summary>
    public int SingularPoints { get; private set; }
    /// <summary>Skinned mode: volume constraints whose skinned target is nearly flat (not judged by the untangler).</summary>
    public int DegenerateTets { get; private set; }
    /// <summary>Uniform scale of the skinning at build time (median over points), 1 in anchor mode.</summary>
    public float SkinScale { get; private set; } = 1f;

    bool _hasGround;
    // the floor as found (ray hit) and as placed in the sim: lowered under the body's lowest rest point when needed
    bool _hasRawGround;
    Vector3 _rawGroundPoint, _rawGroundNormal;
    Vector3[] _groundScratch;
    /// <summary>How far the floor was lowered under the body's rest shape on the last step (0 = not at all, 0.10.1).</summary>
    public float GroundDrop { get; private set; }
    StaticHandle _ground;
    TypedIndex _groundShape;
    Vector3 _groundPoint, _groundNormal;
    sealed class ProxyBody
    {
        public BodyHandle Body;
        public TypedIndex Shape;
        public ProxyShape Desc;          // latest pose from the world (the target for this frame's substeps)
        public RigidPose From;           // pose at the end of the last stepped frame
        public bool Seen;
    }
    readonly Dictionary<int, ProxyBody> _proxies = new();
    readonly List<int> _proxyRemove = new();

    /// <summary>Anchor mode: the cage follows one rigid transform (the renderer slot).</summary>
    public CageSimulation(Cage cage, SoftbodySettings settings, in AnchorPose anchor) : this(cage, settings, anchor, null, default) { }

    /// <summary>
    /// Skinned mode: the cage follows a skeleton. <paramref name="skinMatrices"/> are the per-bone skin matrices
    /// (bind space to world, <see cref="SkinData"/>); <paramref name="anchor"/> is the renderer slot, only used for the
    /// scale-change check.
    /// </summary>
    public CageSimulation(Cage cage, SoftbodySettings settings, CageSkin skin, ReadOnlySpan<Matrix4x4> skinMatrices, in AnchorPose anchor)
        : this(cage, settings, anchor, skin ?? throw new ArgumentNullException(nameof(skin)), skinMatrices) { }

    CageSimulation(Cage cage, SoftbodySettings settings, in AnchorPose anchor, CageSkin skin, ReadOnlySpan<Matrix4x4> skinMatrices)
    {
        Cage = cage;
        Settings = settings.Clone();
        Settings.Clamp();
        Params.Gravity = Settings.GravityVector;
        Params.LinearDamping = Settings.LinearDamping;
        Params.Friction = Settings.Friction;
        Params.Collide = Settings.Collide;

        _pool = new BufferPool(1 << 16, 16);
        _sim = Simulation.Create(_pool,
            new CageNarrowPhaseCallbacks { Params = Params },
            new CagePoseIntegratorCallbacks { Params = Params },
            new PositionFirstTimestepper(), Settings.SolverIterations, 64, null);

        int np = cage.PointCount;
        _bodies = new BodyHandle[np];
        _servoHandles = new ConstraintHandle[np];
        _servos = new OneBodyLinearServo[np];
        _targets = new Vector3[np];
        _lastAnchor = anchor;
        _buildScale = anchor.Scale;

        // Where the points start: the anchor's transform of the rest lattice, or its skinned pose.
        _skin = skin;
        var worldRest = new Vector3[np];
        Vector3 absScale;
        if (skin == null)
        {
            absScale = Vector3.Abs(anchor.Scale);
            for (int i = 0; i < np; i++) worldRest[i] = anchor.TransformPoint(cage.RestPoints[i]);
        }
        else
        {
            if (skin.PointCount != np) throw new ArgumentException("cage skin / cage point count mismatch");
            _pointMats = new Matrix4x4[np]; _nextMats = new Matrix4x4[np]; _pointInv = new Matrix4x4[np]; _singular = new bool[np];
            _pointScale = new float[np]; _nextScale = new float[np];
            _nextTargets = new Vector3[np]; _prevTargets = new Vector3[np];
            if (!skin.ComputeTargets(skinMatrices, cage.RestPoints, worldRest, _pointMats, _pointScale)) throw new ArgumentException("the skeleton's pose is not finite");
            var scales = new float[np];
            for (int i = 0; i < np; i++) scales[i] = Lbs.LinearScale(_pointMats[i]);
            Array.Sort(scales);
            float s = scales[np / 2];
            if (!(s > 1e-6f) || !float.IsFinite(s)) throw new ArgumentException("the skeleton's pose has no scale");
            SkinScale = s;
            absScale = new Vector3(s);
            InvertPointMatrices();
        }
        float minScale = MathF.Min(absScale.X, MathF.Min(absScale.Y, absScale.Z));
        RadiusWorld = MathF.Max(0.002f, cage.RadiusLocal * minScale);
        var span = (cage.RestBoundsMax - cage.RestBoundsMin) * absScale;
        _cageWorldSize = MathF.Max(0.01f, span.Length());

        var sphere = new Sphere(RadiusWorld);
        _pointShape = _sim.Shapes.Add(sphere);
        var inertia = PointInertia();
        float margin = MathF.Min(0.1f, 2f * RadiusWorld + 0.02f);
        var collidable = new CollidableDescription(_pointShape, margin);
        var activity = new BodyActivityDescription(-1f, 32); // never sleep (targets move under it)

        for (int i = 0; i < np; i++)
        {
            var p = worldRest[i];
            _targets[i] = p;
            var pose = new RigidPose(p);
            var desc = cage.Kinematic[i]
                ? BodyDescription.CreateKinematic(pose, collidable, activity)
                : BodyDescription.CreateDynamic(pose, inertia, collidable, activity);
            _bodies[i] = _sim.Bodies.Add(desc);
        }

        ComputeMultipliers();

        // Edge springs (skip edges between two pinned points: nothing to solve, and Bepu would
        // divide by a zero effective mass).
        for (int e = 0; e < cage.Edges.Length; e += 2)
        {
            int a = cage.Edges[e], b = cage.Edges[e + 1];
            if (cage.Kinematic[a] && cage.Kinematic[b]) continue;
            float s = 0.5f * (cage.PointSoftness[a] + cage.PointSoftness[b]);
            float rest = Vector3.Distance(worldRest[a], worldRest[b]);
            var spring = EdgeSpring(s, a, b);
            _edgeHandles.Add(_sim.Solver.Add(_bodies[a], _bodies[b], new CenterDistanceConstraint(rest, spring)));
            _edgeRest.Add(rest); _edgeSoft.Add(s); _edgeA.Add(a); _edgeB.Add(b); _edgeSpr.Add(spring);
            EdgeConstraintCount++;
        }

        // Volume per tet: keeps the jelly from deflating while it squishes.
        double vol = 0;
        for (int t = 0; t < cage.Tets.Length; t += 4)
        {
            int a = cage.Tets[t], b = cage.Tets[t + 1], c = cage.Tets[t + 2], d = cage.Tets[t + 3];
            vol += Cage.TetVolume(worldRest, a, b, c, d);
            if (cage.Kinematic[a] && cage.Kinematic[b] && cage.Kinematic[c] && cage.Kinematic[d]) continue;
            float s = 0.25f * (cage.PointSoftness[a] + cage.PointSoftness[b] + cage.PointSoftness[c] + cage.PointSoftness[d]);
            var desc = new VolumeConstraint(worldRest[a], worldRest[b], worldRest[c], worldRest[d], VolumeSpring(s, t));
            _volHandles.Add(_sim.Solver.Add(_bodies[a], _bodies[b], _bodies[c], _bodies[d], desc));
            _volTarget.Add(desc.TargetScaledVolume); _volSoft.Add(s); _volSpr.Add(desc.SpringSettings);
            _volTets.Add(t); _volRest.Add(Cage.TetVolume(worldRest, a, b, c, d));
            VolumeConstraintCount++;
        }
        RestVolumeWorld = vol;
        if (skin != null)
        {
            float s3 = SkinScale * SkinScale * SkinScale;
            _volBind = new float[_volTets.Count];
            for (int k = 0; k < _volTets.Count; k++)
            {
                int t = _volTets[k];
                _volBind[k] = Cage.TetVolume(cage.RestPoints, cage.Tets[t], cage.Tets[t + 1], cage.Tets[t + 2], cage.Tets[t + 3]) * s3;
            }
        }
        _untangleTimer = new float[np];
        _boosted = new bool[np];
        _posScratch = new Vector3[np];

        // Servo per dynamic point toward its rest position in the anchor frame.
        for (int i = 0; i < np; i++)
        {
            if (cage.Kinematic[i]) continue;
            _servos[i] = new OneBodyLinearServo
            {
                LocalOffset = Vector3.Zero,
                Target = worldRest[i],
                SpringSettings = ServoSpring(cage.PointSoftness[i], i),
                ServoSettings = new ServoSettings(float.MaxValue, 0f, float.MaxValue),
            };
            _servoHandles[i] = _sim.Solver.Add(_bodies[i], _servos[i]);
            ServoCount++;
        }
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
    static float LogLerp(float a, float b, float t) => MathF.Exp(Lerp(MathF.Log(a), MathF.Log(b), t));

    // Spring settings as a function of softness s (0 = firm, 1 = squishy). Used at build time and by
    // ApplyTuning, so a live-tuned cage gets exactly the constraints a rebuild would give it.
    // Hue-band multipliers per point (1 where the colour is unbanded); see HueBands / SoftbodySettings.Band*.
    float[] _mStiff, _mDamp, _mFollow;
    readonly List<int> _edgeA = new(), _edgeB = new();

    void ComputeMultipliers()
    {
        int np = Cage.PointCount, nb = HueBands.Count;
        _mStiff ??= new float[np]; _mDamp ??= new float[np]; _mFollow ??= new float[np];
        var w = Cage.PointBands;
        for (int i = 0; i < np; i++)
        {
            float sum = 0, ms = 0, md = 0, mf = 0;
            if (w != null && w.Length >= np * nb)
                for (int b = 0; b < nb; b++)
                {
                    float x = w[i * nb + b];
                    sum += x; ms += x * Settings.BandStiffness[b]; md += x * Settings.BandDamping[b]; mf += x * Settings.BandFollow[b];
                }
            float neutral = MathF.Max(0f, 1f - sum);
            _mStiff[i] = ms + neutral; _mDamp[i] = md + neutral; _mFollow[i] = mf + neutral;
        }
    }

    static float Hz(float f) => Math.Clamp(f, 0.05f, 60f);
    static float Ratio(float d) => Math.Clamp(d, 0.01f, 4f);

    SpringSettings EdgeSpring(float s, int a, int b)
    {
        float ms = 0.5f * (_mStiff[a] + _mStiff[b]), md = 0.5f * (_mDamp[a] + _mDamp[b]);
        return new(LogLerp(Settings.RigidFrequency, Hz(Settings.Stiffness * ms), s), Lerp(1f, Ratio(Settings.Damping * md), s));
    }
    SpringSettings ServoSpring(float s, int i) =>
        new(LogLerp(Settings.RigidFrequency, Hz(Settings.Follow * _mFollow[i]), s), Lerp(1f, Ratio(Settings.Damping * _mDamp[i]), s));
    SpringSettings VolumeSpring(float s, int t)
    {
        var tt = Cage.Tets;
        float ms = 0.25f * (_mStiff[tt[t]] + _mStiff[tt[t + 1]] + _mStiff[tt[t + 2]] + _mStiff[tt[t + 3]]);
        float volBase = Settings.VolumeStiffness > 0 ? Settings.VolumeStiffness * ms : 2f * Settings.Stiffness * ms;
        return new(LogLerp(Settings.RigidFrequency, Hz(volBase), s), Lerp(1f, 0.6f, s));
    }

    BodyInertia PointInertia()
    {
        new Sphere(RadiusWorld).ComputeInertia(Settings.Mass / Cage.PointCount, out var inertia);
        inertia.InverseInertiaTensor = default; // lock rotation: points are just positions
        return inertia;
    }

    /// <summary>
    /// Applies new settings to the running simulation without rebuilding the cage: spring frequencies and
    /// damping (edges, volumes, servos via ApplyDescription), point mass, solver iterations, and gravity /
    /// drag / friction / collide (read by the callbacks every step). Positions and velocities are kept.
    /// Returns <see cref="TuneResult.NeedsRebuild"/> (and changes nothing) when a structural setting differs.
    /// </summary>
    public TuneResult ApplyTuning(SoftbodySettings incoming)
    {
        if (_disposed || incoming == null) return TuneResult.NeedsRebuild;
        var s = incoming.Clone();
        s.Clamp();
        if (!s.SameStructure(Settings)) return TuneResult.NeedsRebuild;

        var old = Settings;
        bool springs = s.Stiffness != old.Stiffness || s.VolumeStiffness != old.VolumeStiffness || s.Damping != old.Damping ||
                       s.Follow != old.Follow || s.RigidFrequency != old.RigidFrequency || !s.SameBands(old);
        bool mass = s.Mass != old.Mass;
        bool iterations = s.SolverIterations != old.SolverIterations;
        bool callbacks = s.Gravity != old.Gravity || s.LinearDamping != old.LinearDamping || s.Friction != old.Friction || s.Collide != old.Collide;
        if (!springs && !mass && !iterations && !callbacks) return TuneResult.Unchanged;

        Settings.CopyTunablesFrom(s);
        if (springs)
        {
            ComputeMultipliers();
            for (int k = 0; k < _edgeHandles.Count; k++)
            {
                _edgeSpr[k] = EdgeSpring(_edgeSoft[k], _edgeA[k], _edgeB[k]);
                _sim.Solver.ApplyDescription(_edgeHandles[k], new CenterDistanceConstraint(_edgeRest[k], _edgeSpr[k]));
            }
            for (int k = 0; k < _volHandles.Count; k++)
            {
                _volSpr[k] = VolumeSpring(_volSoft[k], _volTets[k]);
                _sim.Solver.ApplyDescription(_volHandles[k], new VolumeConstraint { TargetScaledVolume = _volTarget[k], SpringSettings = _volSpr[k] });
            }
            for (int i = 0; i < _bodies.Length; i++)
            {
                if (Cage.Kinematic[i]) continue;
                _servos[i].SpringSettings = _boosted[i] ? UntangleSpring() : ServoSpring(Cage.PointSoftness[i], i);
                _sim.Solver.ApplyDescription(_servoHandles[i], _servos[i]);
            }
        }
        if (mass)
        {
            var inertia = PointInertia();
            for (int i = 0; i < _bodies.Length; i++)
                if (!Cage.Kinematic[i]) _sim.Bodies.GetBodyReference(_bodies[i]).SetLocalInertia(inertia);
        }
        if (iterations) _sim.Solver.IterationCount = Settings.SolverIterations;
        Params.Gravity = Settings.GravityVector;
        Params.LinearDamping = Settings.LinearDamping;
        Params.Friction = Settings.Friction;
        Params.Collide = Settings.Collide;
        WakeAll();
        TuneCount++;
        return TuneResult.Applied;
    }

    public int PointCount => _bodies.Length;

    /// <summary>True when the anchor scale drifted enough that rest lengths should be rebuilt.</summary>
    public bool ScaleChanged(in AnchorPose anchor)
    {
        var r = anchor.Scale / _buildScale;
        return MathF.Abs(r.X - 1) > 0.02f || MathF.Abs(r.Y - 1) > 0.02f || MathF.Abs(r.Z - 1) > 0.02f;
    }

    /// <summary>Advances by dt (fixed substeps, anchor interpolated across them). Returns substeps run.</summary>
    public int Step(float dt, in AnchorPose anchor)
    {
        if (_skin != null) throw new InvalidOperationException("a skinned cage is stepped with StepSkinned");
        if (_disposed || !anchor.IsFinite || !float.IsFinite(dt) || dt <= 0f) return 0;
        dt = MathF.Min(dt, 0.1f);
        if (Vector3.Distance(anchor.Position, _lastAnchor.Position) > MathF.Max(5f, 20f * _cageWorldSize))
        {
            Reset(anchor, "teleport");
            return 0;
        }
        float h = Settings.Substep;
        _acc += dt;
        int n = (int)(_acc / h);
        if (n > Settings.MaxSubsteps) { n = Settings.MaxSubsteps; _acc = 0f; }
        else _acc -= n * h;
        if (n == 0) return 0;

        var from = _lastAnchor;
        if (_hasRawGround)
        {
            _groundScratch ??= new Vector3[_bodies.Length];
            for (int i = 0; i < _bodies.Length; i++) _groundScratch[i] = anchor.TransformPoint(Cage.RestPoints[i]);
            PlaceGround(_targets, _groundScratch);
        }
        for (int k = 1; k <= n; k++)
        {
            float f = k / (float)n;
            var a = AnchorPose.Lerp(from, anchor, f);
            UpdateTargets(a, h);
            DriveProxies(f, h);
            _sim.Timestep(h, null);
            SubstepCount++;
        }
        _lastAnchor = anchor;
        foreach (var p in _proxies.Values) p.From = new RigidPose(p.Desc.Position, Quaternion.Normalize(p.Desc.Rotation));
        if (!CheckHealth(out var why)) Reset(anchor, why);
        Untangle(n * h);
        return n;
    }

    void UpdateTargets(in AnchorPose a, float h)
    {
        float invH = 1f / h;
        for (int i = 0; i < _bodies.Length; i++) SetTarget(i, a.TransformPoint(Cage.RestPoints[i]), invH);
    }

    /// <summary>Kinematic points get the velocity that carries them onto the target in one substep; dynamic points' servos aim at it.</summary>
    void SetTarget(int i, Vector3 target, float invH)
    {
        _targets[i] = target;
        if (Cage.Kinematic[i])
        {
            var body = _sim.Bodies.GetBodyReference(_bodies[i]);
            body.Velocity.Linear = (target - body.Pose.Position) * invH;
            body.Velocity.Angular = default;
        }
        else
        {
            _servos[i].Target = target;
            _sim.Solver.ApplyDescription(_servoHandles[i], _servos[i]);
        }
    }

    // ------------------------------------------------------------------ skinned mode

    /// <summary>
    /// Skinned mode: advances by dt. Each point's target is its linear-blend-skinned rest position for
    /// <paramref name="skinMatrices"/> (bones + the slot bone, bind space to world), interpolated linearly from last
    /// stepped frame's target across the substeps; firm points follow it exactly. Before the substeps, the edge and
    /// volume constraints take the skinned shape as their rest state. A jump of the whole skeleton far larger than the
    /// cage (teleport, respawn) resets instead. Returns the substeps run.
    /// </summary>
    public int StepSkinned(float dt, ReadOnlySpan<Matrix4x4> skinMatrices)
    {
        if (_skin == null) throw new InvalidOperationException("an anchor cage is stepped with Step");
        if (_disposed || !float.IsFinite(dt) || dt <= 0f) return 0;
        dt = MathF.Min(dt, 0.1f);
        if (!_skin.ComputeTargets(skinMatrices, Cage.RestPoints, _nextTargets, _nextMats, _nextScale)) return 0; // a non-finite pose: skip the frame
        double jump = 0;
        for (int i = 0; i < _targets.Length; i++) jump += Vector3.Distance(_nextTargets[i], _targets[i]);
        jump /= Math.Max(1, _targets.Length);
        if (jump > MathF.Max(5f, 20f * _cageWorldSize))
        {
            AdoptNextTargets();
            UpdateRestState(force: true);
            ResetToTargets("teleport");
            return 0;
        }
        float h = Settings.Substep;
        _acc += dt;
        int n = (int)(_acc / h);
        if (n > Settings.MaxSubsteps) { n = Settings.MaxSubsteps; _acc = 0f; }
        else _acc -= n * h;
        if (n == 0) return 0;

        _targets.AsSpan().CopyTo(_prevTargets);
        RestUpdates = UpdateRestState(force: false, _nextTargets);
        PlaceGround(_prevTargets, _nextTargets);
        float invH = 1f / h;
        for (int k = 1; k <= n; k++)
        {
            float f = k / (float)n;
            for (int i = 0; i < _bodies.Length; i++) SetTarget(i, Vector3.Lerp(_prevTargets[i], _nextTargets[i], f), invH);
            DriveProxies(f, h);
            _sim.Timestep(h, null);
            SubstepCount++;
        }
        AdoptNextTargets();
        foreach (var p in _proxies.Values) p.From = new RigidPose(p.Desc.Position, Quaternion.Normalize(p.Desc.Rotation));
        if (!CheckHealth(out var why)) ResetToTargets(why);
        Untangle(n * h);
        return n;
    }

    /// <summary>Skinned mode: puts every point on its skinned target for this pose, at rest.</summary>
    public void ResetSkinned(ReadOnlySpan<Matrix4x4> skinMatrices, string reason = "reset")
    {
        if (_disposed || _skin == null) return;
        if (!_skin.ComputeTargets(skinMatrices, Cage.RestPoints, _nextTargets, _nextMats, _nextScale)) return;
        AdoptNextTargets();
        UpdateRestState(force: true);
        ResetToTargets(reason);
    }

    void AdoptNextTargets()
    {
        _nextTargets.AsSpan().CopyTo(_targets);
        (_pointMats, _nextMats) = (_nextMats, _pointMats);
        (_pointScale, _nextScale) = (_nextScale, _pointScale);
        InvertPointMatrices();
    }

    void InvertPointMatrices()
    {
        int singular = 0;
        for (int i = 0; i < _pointMats.Length; i++)
        {
            _singular[i] = !Lbs.TryInvertSimilar(_pointMats[i], _pointScale[i], out _pointInv[i]);
            if (_singular[i] && !Cage.Kinematic[i]) singular++;
        }
        SingularPoints = singular;
    }

    int UpdateRestState(bool force) => UpdateRestState(force, _targets);

    /// <summary>Skinned mode: edge lengths and tet volumes of the skinned targets become the constraints' rest state
    /// (only where they changed, so a skeleton moving rigidly costs nothing here). Returns how many were re-applied.</summary>
    int UpdateRestState(bool force, Vector3[] t)
    {
        int updates = 0;
        for (int k = 0; k < _edgeHandles.Count; k++)
        {
            float d = Vector3.Distance(t[_edgeA[k]], t[_edgeB[k]]);
            if (!force && MathF.Abs(d - _edgeRest[k]) <= 2e-4f * d) continue;
            _edgeRest[k] = d;
            _sim.Solver.ApplyDescription(_edgeHandles[k], new CenterDistanceConstraint(d, _edgeSpr[k]));
            updates++;
        }
        var tets = Cage.Tets;
        for (int k = 0; k < _volHandles.Count; k++)
        {
            int q = _volTets[k];
            var desc = new VolumeConstraint(t[tets[q]], t[tets[q + 1]], t[tets[q + 2]], t[tets[q + 3]], _volSpr[k]);
            _volRest[k] = Cage.TetVolume(t, tets[q], tets[q + 1], tets[q + 2], tets[q + 3]);
            if (!force && MathF.Abs(desc.TargetScaledVolume - _volTarget[k]) <= 5e-4f * MathF.Abs(desc.TargetScaledVolume)) continue;
            _volTarget[k] = desc.TargetScaledVolume;
            _sim.Solver.ApplyDescription(_volHandles[k], desc);
            updates++;
        }
        return updates;
    }

    /// <summary>Puts every point on its current target (_targets), at rest.</summary>
    void ResetToTargets(string reason)
    {
        for (int i = 0; i < _bodies.Length; i++)
        {
            var target = _targets[i];
            var body = _sim.Bodies.GetBodyReference(_bodies[i]);
            body.Pose.Position = target;
            body.Pose.Orientation = Quaternion.Identity;
            body.Velocity.Linear = default;
            body.Velocity.Angular = default;
            if (!Cage.Kinematic[i])
            {
                _servos[i].Target = target;
                _sim.Solver.ApplyDescription(_servoHandles[i], _servos[i]);
            }
            body.Awake = true;
        }
        _acc = 0;
        ResetCount++;
        LastResetReason = reason;
    }

    /// <summary>
    /// Skinned mode: each point's offset from its skinned target, mapped back to bind (mesh) space through the rotation
    /// part of its blended skin matrix and its bones' scale: <c>offset_bind = R^T (position - target) / s</c> (0.10.1; the
    /// plain inverse <c>L^-1</c> amplified offsets where the blend collapses). Firm points and points whose matrix is
    /// singular get exactly zero. The renderer skins (rest + offset) again, so the shown surface is about
    /// skinned(rest) + the simulated world offset.
    /// </summary>
    public void GetBindOffsets(Span<Vector3> dst)
    {
        if (_skin == null) throw new InvalidOperationException("only a skinned cage has bind offsets");
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (Cage.Kinematic[i] || _singular[i]) { dst[i] = Vector3.Zero; continue; }
            var d = _sim.Bodies.GetBodyReference(_bodies[i]).Pose.Position - _targets[i];
            dst[i] = Vector3.TransformNormal(d, _pointInv[i]);
        }
    }

    /// <summary>Skinned mode: blended skin matrix of point i for the current targets.</summary>
    public Matrix4x4 PointMatrix(int i) => _pointMats[i];

    /// <summary>World bounding box of the current targets.</summary>
    public void TargetBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        foreach (var t in _targets) { min = Vector3.Min(min, t); max = Vector3.Max(max, t); }
    }

    /// <summary>Strong, critically damped pull used while a point's tet is inside out.</summary>
    SpringSettings UntangleSpring() =>
        new(Math.Clamp(1.5f * MathF.Max(Settings.Stiffness, Settings.Follow), 8f, 60f), 1f);

    float[] _invTime;
    /// <summary>A tet must stay inside out this long before its points are pulled back, so the brief
    /// squashes of an impact or a deep press are left alone and only real folds get untangled.</summary>
    const float UntangleAfter = 0.15f;

    void Untangle(float dt)
    {
        if (_disposed || _volTets.Count == 0 || dt <= 0f) return;
        _invTime ??= new float[_volTets.Count];
        var pos = _posScratch;
        for (int i = 0; i < _bodies.Length; i++) pos[i] = _sim.Bodies.GetBodyReference(_bodies[i]).Pose.Position;
        var tets = Cage.Tets;
        int inverted = 0, degenerate = 0;
        for (int k = 0; k < _volTets.Count; k++)
        {
            int t = _volTets[k];
            float rest = _volRest[k];
            // skinned: a tet that the skinned pose itself squashes nearly flat (a strongly bent joint) cannot be judged
            if (_volBind != null && MathF.Abs(rest) < 0.05f * MathF.Abs(_volBind[k])) { _invTime[k] = 0f; degenerate++; continue; }
            float v = Cage.TetVolume(pos, tets[t], tets[t + 1], tets[t + 2], tets[t + 3]);
            // inside out (or flat to within 2% of its rest volume)
            if (v * MathF.Sign(rest) > 0.02f * MathF.Abs(rest)) { _invTime[k] = 0f; continue; }
            inverted++;
            _invTime[k] += dt;
            if (_invTime[k] < UntangleAfter) continue;
            for (int q = 0; q < 4; q++)
            {
                int p = tets[t + q];
                if (!Cage.Kinematic[p]) _untangleTimer[p] = UntangleHold;
            }
        }
        InvertedTets = inverted;
        DegenerateTets = degenerate;
        int boosted = 0;
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (Cage.Kinematic[i]) continue;
            bool want = _untangleTimer[i] > 0f;
            if (want) { _untangleTimer[i] -= dt; boosted++; }
            if (want == _boosted[i]) continue;
            _boosted[i] = want;
            _servos[i].SpringSettings = want ? UntangleSpring() : ServoSpring(Cage.PointSoftness[i], i);
            _sim.Solver.ApplyDescription(_servoHandles[i], _servos[i]);
        }
        UntanglingPoints = boosted;
    }

    /// <summary>False (with the reason) when a point is not finite or flew far away from its target.</summary>
    bool CheckHealth(out string reason)
    {
        float limit = 10f * _cageWorldSize + 2f;
        float limitSq = limit * limit;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var p = _sim.Bodies.GetBodyReference(_bodies[i]).Pose.Position;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                reason = "non-finite position";
                return false;
            }
            if (Vector3.DistanceSquared(p, _targets[i]) > limitSq)
            {
                reason = "point flew away";
                return false;
            }
        }
        reason = null;
        return true;
    }

    /// <summary>Teleports every point to its rest pose in the anchor frame and zeroes velocities
    /// (skinned mode: to its current skinned target; the anchor is ignored).</summary>
    public void Reset(in AnchorPose anchor, string reason = "reset")
    {
        if (_skin != null) { if (!_disposed) ResetToTargets(reason); return; }
        if (_disposed || !anchor.IsFinite) return;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var target = anchor.TransformPoint(Cage.RestPoints[i]);
            _targets[i] = target;
            var body = _sim.Bodies.GetBodyReference(_bodies[i]);
            body.Pose.Position = target;
            body.Pose.Orientation = Quaternion.Identity;
            body.Velocity.Linear = default;
            body.Velocity.Angular = default;
            if (!Cage.Kinematic[i])
            {
                _servos[i].Target = target;
                _sim.Solver.ApplyDescription(_servoHandles[i], _servos[i]);
            }
            body.Awake = true;
        }
        _lastAnchor = anchor;
        _acc = 0;
        ResetCount++;
        LastResetReason = reason;
    }

    public void GetPositions(Span<Vector3> dst)
    {
        for (int i = 0; i < _bodies.Length; i++) dst[i] = _sim.Bodies.GetBodyReference(_bodies[i]).Pose.Position;
    }

    public void GetTargets(Span<Vector3> dst) => _targets.AsSpan().CopyTo(dst);

    /// <summary>World size of a cell along its smallest axis (the collision radius is a fraction of it).</summary>
    public float CellWorld => RadiusWorld / MathF.Max(Settings.PointRadiusFactor, 1e-3f);

    /// <summary>Copies what the debug views show (point positions, untangling, collision radius, ground and
    /// world-collider proxies) into <paramref name="s"/>, reusing its buffers. Leaves the plugin's search fields alone.</summary>
    public void FillDebug(DebugSnapshot s, bool cells, bool colliders)
    {
        s.Cells = cells;
        s.Colliders = colliders;
        s.Cage = Cage;
        int n = _bodies.Length;
        if (s.Points.Length < n) s.Points = new Vector3[n];
        if (s.Untangling.Length < n) s.Untangling = new bool[n];
        if (s.Targets.Length < n) s.Targets = new Vector3[n];
        s.PointCount = n;
        if (_disposed) { s.PointCount = 0; s.Proxies.Clear(); s.HasGround = false; return; }
        GetPositions(s.Points);
        _targets.AsSpan(0, n).CopyTo(s.Targets);
        if (_boosted != null) Array.Copy(_boosted, s.Untangling, Math.Min(n, _boosted.Length));
        else Array.Clear(s.Untangling, 0, n);
        s.RadiusWorld = RadiusWorld;
        s.CellWorld = CellWorld;
        s.Collide = Params.Collide;
        s.HasGround = _hasGround && Params.Collide;
        s.GroundPoint = _groundPoint;
        s.GroundNormal = _groundNormal;
        s.GroundHit = _hasRawGround ? _rawGroundPoint : _groundPoint;
        s.GroundDrop = s.HasGround ? GroundDrop : 0f;
        s.Proxies.Clear();
        if (Params.Collide) foreach (var p in _proxies.Values) s.Proxies.Add(p.Desc);
    }

    public Vector3 GetVelocity(int i) => _sim.Bodies.GetBodyReference(_bodies[i]).Velocity.Linear;

    public float MaxSpeed()
    {
        float m = 0;
        for (int i = 0; i < _bodies.Length; i++) m = MathF.Max(m, _sim.Bodies.GetBodyReference(_bodies[i]).Velocity.Linear.Length());
        return m;
    }

    // ------------------------------------------------------------------ statics

    /// <summary>
    /// The floor under the cage: a large thin box whose top face passes through point with the given normal. 0.10.1: the
    /// floor never pushes the body out of its rest shape. If a simulated point's target (where the slot or the skeleton
    /// puts it) is closer to the floor than its collision radius, the floor is lowered under it (<see cref="GroundDrop"/>),
    /// re-checked every step; it still stops parts from sagging or swinging through it.
    /// </summary>
    public void SetGround(Vector3 point, Vector3 normal)
    {
        if (_disposed) return;
        if (!(normal.LengthSquared() > 1e-8f) || !float.IsFinite(point.X + point.Y + point.Z)) { ClearGround(); return; }
        _rawGroundPoint = point;
        _rawGroundNormal = Vector3.Normalize(normal);
        _hasRawGround = true;
        PlaceGround(_targets, null);
    }

    /// <summary>Places the floor under the lowest simulated point of the targets in <paramref name="a"/> (and <paramref name="b"/>).</summary>
    void PlaceGround(ReadOnlySpan<Vector3> a, Vector3[] b)
    {
        if (!_hasRawGround || _disposed) return;
        var p = _rawGroundPoint; var n = _rawGroundNormal;
        float lowest = float.MaxValue;
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (Cage.Kinematic[i]) continue; // pinned points never touch the floor
            if (i < a.Length) lowest = MathF.Min(lowest, Vector3.Dot(a[i] - p, n));
            if (b != null) lowest = MathF.Min(lowest, Vector3.Dot(b[i] - p, n));
        }
        float clearance = RadiusWorld * 1.02f + 1e-3f;
        float drop = lowest < float.MaxValue && float.IsFinite(lowest) && lowest < clearance ? clearance - lowest : 0f;
        GroundDrop = drop;
        PlaceGroundStatic(p - n * drop, n);
    }

    void PlaceGroundStatic(Vector3 point, Vector3 normal)
    {
        if (_hasGround && Vector3.DistanceSquared(point, _groundPoint) < 1e-6f && Vector3.Dot(normal, _groundNormal) > 0.99999f) return;
        const float half = 1f;
        var rot = FromTo(Vector3.UnitY, normal);
        var center = point - normal * half;
        if (!_hasGround)
        {
            _groundShape = _sim.Shapes.Add(new Box(400f, 2f * half, 400f));
            _ground = _sim.Statics.Add(new StaticDescription(center, rot, new CollidableDescription(_groundShape, 0.1f)));
            _hasGround = true;
        }
        else
        {
            _sim.Statics.ApplyDescription(_ground, new StaticDescription(center, rot, new CollidableDescription(_groundShape, 0.1f)));
        }
        _groundPoint = point; _groundNormal = normal;
        WakeAll();
    }

    public void ClearGround()
    {
        _hasRawGround = false;
        GroundDrop = 0f;
        if (_disposed || !_hasGround) return;
        _sim.Statics.Remove(_ground);
        _sim.Shapes.Remove(_groundShape);
        _hasGround = false;
    }

    /// <summary>
    /// Updates the world-collider proxies. Call every frame with their current poses: each proxy is a
    /// kinematic body (matched by <see cref="ProxyShape.Key"/>) that the next <see cref="Step"/> moves
    /// smoothly from its last pose to this one, with matching velocity, so cage points are pushed the
    /// same frame a collider moves into them. Proxies not listed are removed.
    /// </summary>
    public void SetProxies(ReadOnlySpan<ProxyShape> shapes)
    {
        if (_disposed) return;
        foreach (var p in _proxies.Values) p.Seen = false;
        foreach (var s in shapes)
        {
            if (!ValidProxy(s)) continue;
            if (_proxies.TryGetValue(s.Key, out var p) && !p.Desc.SameShape(s)) { RemoveProxy(s.Key, p); p = null; }
            if (p == null)
            {
                var shape = s.IsSphere ? _sim.Shapes.Add(new Sphere(s.Radius))
                                       : _sim.Shapes.Add(new Box(2f * MathF.Max(s.HalfExtents.X, 1e-4f), 2f * MathF.Max(s.HalfExtents.Y, 1e-4f), 2f * MathF.Max(s.HalfExtents.Z, 1e-4f)));
                var pose = new RigidPose(s.Position, Quaternion.Normalize(s.Rotation));
                var body = _sim.Bodies.Add(BodyDescription.CreateKinematic(pose, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(-1f, 32)));
                MarkProxy(body, true);
                p = new ProxyBody { Body = body, Shape = shape, From = pose };
                _proxies[s.Key] = p;
            }
            // A jump much larger than the cage (teleport, respawn) is applied directly rather than swept.
            if (Vector3.Distance(s.Position, p.From.Position) > MathF.Max(2f, 4f * _cageWorldSize))
            {
                var body = _sim.Bodies.GetBodyReference(p.Body);
                p.From = new RigidPose(s.Position, Quaternion.Normalize(s.Rotation));
                body.Pose = p.From;
                body.Velocity = default;
            }
            p.Desc = s;
            p.Seen = true;
        }
        _proxyRemove.Clear();
        foreach (var kv in _proxies) if (!kv.Value.Seen) _proxyRemove.Add(kv.Key);
        foreach (var key in _proxyRemove) RemoveProxy(key, _proxies[key]);
    }

    static bool ValidProxy(in ProxyShape s) =>
        float.IsFinite(s.Position.X + s.Position.Y + s.Position.Z) &&
        (s.IsSphere ? s.Radius > 1e-4f && float.IsFinite(s.Radius) : float.IsFinite(s.HalfExtents.X + s.HalfExtents.Y + s.HalfExtents.Z));

    void RemoveProxy(int key, ProxyBody p)
    {
        MarkProxy(p.Body, false);
        _sim.Bodies.Remove(p.Body);
        _sim.Shapes.Remove(p.Shape);
        _proxies.Remove(key);
    }

    void MarkProxy(BodyHandle h, bool on)
    {
        var arr = Params.ProxyBodies;
        if (h.Value >= arr.Length)
        {
            if (!on) return;
            Array.Resize(ref arr, Math.Max(h.Value + 1, arr.Length * 2));
            Params.ProxyBodies = arr;
        }
        arr[h.Value] = on;
    }

    /// <summary>Per substep: velocity that carries each proxy to its interpolated pose at fraction f of the frame.</summary>
    void DriveProxies(float f, float h)
    {
        if (_proxies.Count == 0) return;
        float invH = 1f / h;
        foreach (var p in _proxies.Values)
        {
            var body = _sim.Bodies.GetBodyReference(p.Body);
            var targetPos = Vector3.Lerp(p.From.Position, p.Desc.Position, f);
            var targetRot = Quaternion.Slerp(p.From.Orientation, Quaternion.Normalize(p.Desc.Rotation), f);
            body.Velocity.Linear = (targetPos - body.Pose.Position) * invH;
            body.Velocity.Angular = AngularVelocity(body.Pose.Orientation, targetRot, invH);
        }
    }

    static Vector3 AngularVelocity(Quaternion from, Quaternion to, float invH)
    {
        var d = Quaternion.Normalize(to * Quaternion.Conjugate(from));
        if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
        var v = new Vector3(d.X, d.Y, d.Z);
        float s = v.Length();
        if (s < 1e-6f) return 2f * v * invH;
        return v / s * (2f * MathF.Atan2(s, d.W)) * invH;
    }

    void WakeAll()
    {
        for (int i = 0; i < _bodies.Length; i++) { var b = _sim.Bodies.GetBodyReference(_bodies[i]); b.Awake = true; }
    }

    static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float d = Vector3.Dot(from, to);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1f + d));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sim?.Dispose(); } catch { }
        try { _pool?.Clear(); } catch { }
        _sim = null; _pool = null;
    }
}
