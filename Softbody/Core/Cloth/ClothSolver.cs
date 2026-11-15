using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Cloth;

/// <summary>0.29.0: what one cloth frame needs from the world (filled on the world thread, read by the step).</summary>
public sealed class ClothFrame
{
    public float Dt;
    /// <summary>The renderer's slot, renderer-local to world, at the end of this frame.</summary>
    public Matrix4x4 RendererToWorld = Matrix4x4.Identity;
    /// <summary>Each anchor's slot to world at the end of this frame (by anchor index), and whether it is there.</summary>
    public Matrix4x4[] AnchorToWorld = Array.Empty<Matrix4x4>();
    public bool[] AnchorValid = Array.Empty<bool>();
    /// <summary>World colliders and players near the cloth (world space, with their velocities).</summary>
    public readonly List<FleshCollider> Colliders = new();
    /// <summary>0.29.2: players' body shapes near the cloth (the first BodyCount entries; the list is reused). The step
    /// poses their spheres and keeps the ones near the cloth.</summary>
    public readonly List<ClothBody> Bodies = new();
    public int BodyCount;
    public ClothWindSource[] Winds = Array.Empty<ClothWindSource>();
    public int WindCount;
    /// <summary>World time in seconds (the gusts' clock).</summary>
    public double Time;
    /// <summary>World gravity before the cloth's own multiplier.</summary>
    public Vector3 Gravity = new(0f, -9.81f, 0f);
    /// <summary>This client decides the tears: look for edges past TearAt after the step.</summary>
    public bool FindTears;
    /// <summary>The first step after a pause: carry the cloth along with its slot instead of dragging it there.</summary>
    public bool Resumed;
    /// <summary>0.30.0, avatar cloth: where the animation puts each original particle at the end of this frame (world). A
    /// split particle follows the one it came from (<see cref="ClothMesh.Origin"/>). Null for world cloth, whose points
    /// follow <see cref="RendererToWorld"/>.</summary>
    public Vector3[] Targets;
    /// <summary>0.30.0, avatar cloth: the bone the cloth hangs from, bind space to world (its skin matrix), at the end of this
    /// frame. Its motion is the avatar's (see ClothSettings.Inertia) and its scale the cloth's size.</summary>
    public Matrix4x4 BoneToWorld = Matrix4x4.Identity;
    /// <summary>0.31.0: blasts' pushes for this step (the first KickCount entries; the step uses them up and sets KickCount
    /// back to 0).</summary>
    public ClothKick[] Kicks = Array.Empty<ClothKick>();
    public int KickCount;

    /// <summary>0.31.0: adds a kick for the next step.</summary>
    public void AddKick(in ClothKick k)
    {
        if (KickCount == Kicks.Length) Array.Resize(ref Kicks, Math.Max(4, Kicks.Length * 2));
        Kicks[KickCount++] = k;
    }
}

/// <summary>
/// 0.29.0: the cloth simulation (DESIGN-cloth.md): XPBD in small steps of a fixed length (1/StepRate s, one pass over the
/// constraints each), with an accumulator so the frame rate does not change how it behaves. In world space; points painted
/// dark follow the renderer's slot, anchored points follow their anchor, both moved smoothly through the frame.
/// <list type="bullet">
/// <item>Mass by area (Weight x a third of each touching triangle).</item>
/// <item>Stretch: one distance constraint per edge, its rest length fixed at the build. Bending: a distance constraint
/// between the two points across each shared edge.</item>
/// <item>Max distance: with MaxDistance above 0, a free point stays within brightness x MaxDistance of its rest place.</item>
/// <item>Air: per triangle drag along the flow and lift across it, from the velocity relative to the wind (NvCloth's
/// model), clamped so a step never more than halves the relative speed.</item>
/// <item>Collisions: the flesh's collider shapes kept a Thickness away, with friction, inelastic (a push never becomes
/// a bounce).</item>
/// <item>Tears: after the step, the edges past TearAt (weakened near fresh tears by TearSpread), most strained first, at
/// most 8 a frame (<see cref="TearCandidates"/>); the caller splits them with <see cref="Split"/>.</item>
/// </list>
/// 0.30.0, avatar cloth (<see cref="ClothFrame.Targets"/> set): every point has an animated place from the frame; held
/// points follow it, free points stay within brightness x MaxDistance of it and no more than Backstop behind it (along the
/// animated cloth's normal). Each frame the free points are carried along with (1 - Inertia) of the bone's motion; a jump of
/// more than 0.75 m puts the cloth back at the animated pose, and a snap turn is carried whole. On the wearer's own body
/// spheres a point is pushed out no further than its animated place.
/// 0.30.0, both: rest lengths and masses are measured in the world (through the rest space's scale), so cloth on a scaled
/// slot or avatar keeps the size it shows.
/// Not thread-safe: <see cref="Step"/> runs on a worker; everything else between steps.
/// </summary>
public sealed class ClothSolver
{
    public readonly ClothMesh Mesh;
    public ClothSettings Settings { get; private set; }

    // ---- particles (capacity: Mesh.MaxParticles)
    public readonly Vector3[] X, P, V;
    readonly float[] _w, _mass;
    /// <summary>0 free, 1 pinned (follows the renderer's slot), 2 anchored, 3 loose (no triangles: follows the slot).</summary>
    readonly byte[] _kind;
    readonly float[] _bright, _limit, _weak;
    /// <summary>0.31.0: set free by a blast (whatever the paint, the anchors and MaxDistance say).</summary>
    readonly bool[] _released;
    bool _particlePaintDirty;
    readonly int[] _anchor;
    readonly Vector3[] _anchorLocal;
    readonly Vector3[] _t0, _t1;
    float[] _paintBySource = Array.Empty<float>();    // per original render vertex: brightness (1 = free)

    // ---- constraints
    int[] _sA = Array.Empty<int>(), _sB = Array.Empty<int>(); float[] _sL = Array.Empty<float>(); int _ns;
    int[] _bA = Array.Empty<int>(), _bB = Array.Empty<int>(); float[] _bL = Array.Empty<float>(); int _nb;
    int _builtVersion = -1;
    bool _kindsDirty = true;

    // ---- air
    Vector3[] _wind = Array.Empty<Vector3>();
    bool _anyWind;

    // ---- render (tangents)
    readonly float[] _tanA, _tanB;            // per render triangle: T = a (p1 - p0) + b (p2 - p0)
    readonly Vector4[] _restTanBySource;      // per original render vertex, or null
    readonly Vector3[] _restNrmBySource;      // per original render vertex
    Vector3[] _local = Array.Empty<Vector3>(), _pn = Array.Empty<Vector3>(), _tacc = Array.Empty<Vector3>();

    // ---- time
    Matrix4x4 _lastM;
    bool _started;
    float _accum;

    // ---- 0.30.0: avatar cloth (animated targets)
    bool _avatar;
    Vector3[] _tn = Array.Empty<Vector3>();      // per particle: the animated cloth's normal (the backstop's direction)
    /// <summary>0.30.0: +1 or -1, which way the animated normals point (the plugin sets it so they point away from the
    /// body: the backstop keeps points on that side).</summary>
    public float BackstopSign = 1f;
    /// <summary>0.30.0: times an avatar cloth was put back at its animated pose (a jump of more than
    /// <see cref="TeleportDistance"/>, a resume), and snap turns carried whole.</summary>
    public int Resets { get; private set; }
    public int SnapTurns { get; private set; }
    /// <summary>Metres in one frame: past this an avatar cloth starts again from the animated pose.</summary>
    public const float TeleportDistance = 0.75f;
    /// <summary>Radians in one frame (25 degrees): past this the avatar turned faster than a body can (a snap turn), and the
    /// cloth is turned along with it whole.</summary>
    public const float SnapTurn = 0.436f;
    /// <summary>0.30.0: the box around the points (world) after the last step or reset.</summary>
    public Vector3 WorldMin { get; private set; }
    public Vector3 WorldMax { get; private set; }

    // ---- 0.30.0: the rest shape's size in the world
    Matrix4x4 _restL = Matrix4x4.Identity;       // rest space to world, linear part only, that the rest lengths were measured through
    bool _restSet;
    /// <summary>Times the rest lengths and masses were measured again because the cloth's scale changed.</summary>
    public int Remeasures { get; private set; }

    // ---- stats
    public int StepsLastFrame { get; private set; }
    public int Contacts { get; private set; }
    public int ShapeCount => _shapeCount;
    /// <summary>0.33.1: the most pairs (a point and a shape near it) in one run of steps this frame.</summary>
    public int PairCount { get; private set; }
    /// <summary>0.33.1: how many runs this frame's steps went in, the pairs found again before each (more than one only
    /// for points that would get further in the frame than <see cref="MaxReach"/>).</summary>
    public int PairRuns { get; private set; }
    public int MeshTriangles { get; private set; }
    public double LastStepMs { get; private set; }
    /// <summary>0.29.2: of LastStepMs, finding the shapes near the cloth and their pairs (once a run: 0.33.1), and the
    /// contacts (every step).</summary>
    public double PairsMs { get; private set; }
    /// <summary>Of PairsMs, posing the players' body shapes and pairing their spheres.</summary>
    public double BodiesMs { get; private set; }
    public double ContactsMs { get; private set; }
    long _contactTicks;
    public int StretchConstraints => _ns;
    public int BendConstraints => _nb;
    public int Pinned { get; private set; }
    public int Anchored { get; private set; }
    public int Free { get; private set; }
    /// <summary>0.31.0: points a blast set free that the paint or an anchor would hold (counted as free), after the last step.</summary>
    public int Released { get; private set; }
    /// <summary>0.31.0: blasts' pushes applied (each kick once).</summary>
    public int Kicks { get; private set; }
    public float MaxStrain { get; private set; }
    public long Frames { get; private set; }
    /// <summary>Times the cloth was carried with its slot (a teleport, or the first step after a pause).</summary>
    public int Carries { get; private set; }

    /// <summary>Edges past their tear length after the last step, most strained first (only when the frame asked).</summary>
    public readonly List<(int a, int b, float excess)> TearCandidates = new();

    /// <param name="restNormals">Per original render vertex (empty: from the rest triangles).</param>
    /// <param name="uv0">Per original render vertex, for the tangents (empty: no tangents).</param>
    /// <param name="restTangents">Per original render vertex (empty: no tangents).</param>
    public ClothSolver(ClothMesh mesh, ClothSettings settings, ReadOnlySpan<Vector3> restNormals = default, ReadOnlySpan<Vector2> uv0 = default,
                       ReadOnlySpan<Vector4> restTangents = default)
    {
        Mesh = mesh;
        Settings = (settings ?? new ClothSettings()).Clone();
        Settings.Sanitize();
        int cap = mesh.MaxParticles;
        X = new Vector3[cap]; P = new Vector3[cap]; V = new Vector3[cap];
        _w = new float[cap]; _mass = new float[cap]; _kind = new byte[cap];
        _bright = new float[cap]; _limit = new float[cap]; _weak = new float[cap];
        _released = new bool[cap];
        _anchor = new int[cap]; _anchorLocal = new Vector3[cap];
        _t0 = new Vector3[cap]; _t1 = new Vector3[cap];
        Array.Fill(_anchor, -1);
        Array.Fill(_bright, 1f);
        int nv0 = mesh.OriginalRenderVertexCount;
        _paintBySource = new float[nv0];
        Array.Fill(_paintBySource, 1f);

        _restNrmBySource = new Vector3[nv0];
        if (restNormals.Length == nv0) restNormals.CopyTo(_restNrmBySource);
        else RestNormalsFromTriangles();

        int nr = mesh.RenderTriangleCount;
        if (uv0.Length == nv0 && restTangents.Length == nv0)
        {
            _restTanBySource = restTangents.ToArray();
            _tanA = new float[nr]; _tanB = new float[nr];
            var rt = mesh.RenderTriangles; var src = mesh.RenderSource;
            for (int r = 0; r < nr; r++)
            {
                Vector2 u0 = uv0[src[rt[3 * r]]], u1 = uv0[src[rt[3 * r + 1]]], u2 = uv0[src[rt[3 * r + 2]]];
                float du1 = u1.X - u0.X, dv1 = u1.Y - u0.Y, du2 = u2.X - u0.X, dv2 = u2.Y - u0.Y;
                float det = du1 * dv2 - du2 * dv1;
                if (!(MathF.Abs(det) > 1e-12f) || !float.IsFinite(det)) continue;
                _tanA[r] = dv2 / det; _tanB[r] = -dv1 / det;
            }
        }
        RebuildConstraints();
    }

    public bool HasTangents => _tanA != null;

    void RestNormalsFromTriangles()
    {
        var rest = Mesh.RestPositions; var rt = Mesh.RenderTriangles; var src = Mesh.RenderSource; var rp = Mesh.RenderParticle;
        var acc = new Vector3[Mesh.ParticleCount];
        var tri = Mesh.Triangles;
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
            var n = Vector3.Cross(rest[b] - rest[a], rest[c] - rest[a]);
            acc[a] += n; acc[b] += n; acc[c] += n;
        }
        var sign = Mesh.RenderSign;
        for (int v = 0; v < Mesh.OriginalRenderVertexCount; v++)
        {
            var n = acc[rp[v]];
            float l = n.Length();
            _restNrmBySource[v] = l > 1e-20f ? n / l * sign[v] : Vector3.UnitY;
        }
        _ = rt; _ = src;
    }

    // ------------------------------------------------------------------ settings, paint, anchors (between steps)

    public void SetSettings(ClothSettings s)
    {
        s = (s ?? new ClothSettings()).Clone();
        s.Sanitize();
        bool masses = s.Weight != Settings.Weight;
        bool limits = s.MaxDistance != Settings.MaxDistance;
        Settings = s;
        if (masses) ComputeMasses();
        if (masses || limits) _kindsDirty = true;
    }

    /// <summary>The paint's brightness per original render vertex (1 = free, under 0.5 pinned); a particle takes the
    /// darkest of its render vertices.</summary>
    public void SetPaint(ReadOnlySpan<float> bySource)
    {
        if (bySource.Length != _paintBySource.Length) throw new ArgumentException("one brightness per original render vertex");
        bySource.CopyTo(_paintBySource);
        ParticlePaint();
        _kindsDirty = true;
    }

    void ParticlePaint()
    {
        _particlePaintDirty = false;
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++) _bright[i] = float.MaxValue;
        var rp = Mesh.RenderParticle; var src = Mesh.RenderSource;
        for (int v = 0; v < rp.Length; v++)
        {
            float b = _paintBySource[src[v]];
            if (!float.IsFinite(b)) b = 1f;
            int p = rp[v];
            if (b < _bright[p]) _bright[p] = b;
        }
        for (int i = 0; i < n; i++) if (_bright[i] == float.MaxValue) _bright[i] = 1f;
    }

    /// <summary>Anchors: for each particle the anchor holding it (-1 none) and where it sits in that anchor's space.</summary>
    public void SetAnchors(ReadOnlySpan<int> anchorOf, ReadOnlySpan<Vector3> local)
    {
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++)
        {
            _anchor[i] = i < anchorOf.Length ? anchorOf[i] : -1;
            _anchorLocal[i] = i < local.Length ? local[i] : Vector3.Zero;
        }
        _kindsDirty = true;
    }

    /// <summary>
    /// 0.31.0: blasts that set the cloth free: every point whose rest place (the mesh as built, its own space) is within one
    /// of these spheres (xyz centre, w radius, mesh units) is free from now on, whatever the paint, the anchors and
    /// MaxDistance say. The whole list each time (it only grows, until a Mend rebuilds the cloth). Rest places only, so
    /// every client frees the same points.
    /// </summary>
    public void SetReleases(ReadOnlySpan<Vector4> spheres)
    {
        int n = Mesh.ParticleCount;
        var rest = Mesh.RestPositions;
        bool changed = false;
        for (int i = 0; i < n; i++)
        {
            bool r = false;
            for (int k = 0; k < spheres.Length; k++)
                if (ClothBlastMath.InSphere(rest[i], spheres[k])) { r = true; break; }
            if (_released[i] != r) { _released[i] = r; changed = true; }
        }
        if (changed) _kindsDirty = true;
    }

    public bool IsReleased(int i) => _released[i];
    /// <summary>A point's kind as of now (between steps: after a tear or new paint, worked out here as the next step would).</summary>
    public byte Kind(int i) { BringUpToDate(); return _kind[i]; }
    public float Brightness(int i) { if (_particlePaintDirty) ParticlePaint(); return _bright[i]; }
    /// <summary>A point's mass (kg; 0 for one with no triangles), as of now.</summary>
    public float Mass(int i) { BringUpToDate(); return _mass[i]; }

    void BringUpToDate()
    {
        if (Mesh.Version != _builtVersion) RebuildConstraints();
        if (_kindsDirty) UpdateKinds();
    }
    public int AnchorOf(int i) => _anchor[i];
    public ReadOnlySpan<int> StretchA => _sA.AsSpan(0, _ns);
    public ReadOnlySpan<int> StretchB => _sB.AsSpan(0, _ns);
    public ReadOnlySpan<float> StretchRest => _sL.AsSpan(0, _ns);

    void UpdateKinds()
    {
        _kindsDirty = false;
        if (_particlePaintDirty) ParticlePaint();
        int n = Mesh.ParticleCount, pinned = 0, anchored = 0, free = 0, released = 0;
        float md = Settings.MaxDistance;
        for (int i = 0; i < n; i++)
        {
            byte k;
            bool rel = _released[i];
            // 0.31.0: a blast set it free (counted when the paint or an anchor would hold it)
            if (rel)
            {
                k = _mass[i] > 0f ? (byte)0 : (byte)3;
                if (_anchor[i] >= 0 || _bright[i] < 0.5f) released++;
            }
            else if (_anchor[i] >= 0) k = 2;
            else if (!(_mass[i] > 0f)) k = 3;
            else if (_bright[i] < 0.5f) k = 1;
            else k = 0;
            _kind[i] = k;
            _w[i] = k == 0 ? 1f / _mass[i] : 0f;
            _limit[i] = k == 0 && md > 0f && !rel ? md * Math.Clamp(_bright[i], 0f, 1f) : 0f;
            if (k == 0) free++; else if (k == 2) anchored++; else pinned++;
        }
        Pinned = pinned; Anchored = anchored; Free = free; Released = released;
    }

    void ComputeMasses()
    {
        int n = Mesh.ParticleCount;
        Array.Clear(_mass, 0, n);
        var tri = Mesh.Triangles; var rest = Mesh.RestPositions;
        float kgPerM2 = Settings.Weight / 1000f;
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            // 0.30.0: the area in the world (the rest space's scale), not in the mesh's own units
            int ia = tri[3 * t], ib = tri[3 * t + 1], ic = tri[3 * t + 2];
            float area = 0.5f * Vector3.Cross(Vector3.TransformNormal(rest[ib] - rest[ia], _restL), Vector3.TransformNormal(rest[ic] - rest[ia], _restL)).Length();
            float m = area * kgPerM2 / 3f;
            _mass[ia] += m; _mass[ib] += m; _mass[ic] += m;
        }
        // a sliver of no area still moves (the lightest real particle's mass)
        float min = float.MaxValue;
        for (int i = 0; i < n; i++) if (_mass[i] > 0f && _mass[i] < min) min = _mass[i];
        if (min == float.MaxValue) min = 1e-4f;
        for (int i = 0; i < n; i++) if (Mesh.FanSize(i) > 0 && !(_mass[i] > 0f)) _mass[i] = min;
    }

    /// <summary>The edges (stretch) and the pairs across shared edges (bending), from the current topology; and the masses.</summary>
    public void RebuildConstraints()
    {
        _builtVersion = Mesh.Version;
        var tri = Mesh.Triangles;
        int nt = Mesh.TriangleCount;
        // every triangle edge once, in a fixed order (by its lower, then higher particle)
        var keys = new long[3 * nt];
        var opp = new int[3 * nt];
        for (int t = 0; t < nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3], c = tri[3 * t + (k + 2) % 3];
                if (a > b) (a, b) = (b, a);
                keys[3 * t + k] = ((long)a << 32) | (uint)b;
                opp[3 * t + k] = c;
            }
        Array.Sort(keys, opp);
        _ns = 0; _nb = 0;
        if (_sA.Length < keys.Length) { _sA = new int[keys.Length]; _sB = new int[keys.Length]; _sL = new float[keys.Length]; }
        if (_bA.Length < keys.Length / 2 + 1) { int c = keys.Length / 2 + 1; _bA = new int[c]; _bB = new int[c]; _bL = new float[c]; }
        var rest = Mesh.RestPositions;
        for (int i = 0; i < keys.Length;)
        {
            int j = i;
            while (j < keys.Length && keys[j] == keys[i]) j++;
            int a = (int)(keys[i] >> 32), b = (int)(keys[i] & 0xFFFFFFFF);
            _sA[_ns] = a; _sB[_ns] = b; _sL[_ns] = RestLength(rest, a, b); _ns++;
            if (j - i == 2 && opp[i] != opp[i + 1])
            {
                int c = opp[i], d = opp[i + 1];
                _bA[_nb] = c; _bB[_nb] = d; _bL[_nb] = RestLength(rest, c, d); _nb++;
            }
            i = j;
        }
        ComputeMasses();
        _kindsDirty = true;
    }

    /// <summary>0.30.0: a rest length in the world: the mesh's rest edge through the rest space's scale.</summary>
    float RestLength(ReadOnlySpan<Vector3> rest, int a, int b) => Vector3.TransformNormal(rest[a] - rest[b], _restL).Length();

    /// <summary>
    /// 0.30.0: the rest shape's size in the world comes from <paramref name="restToWorld"/> (the renderer's slot for world
    /// cloth, the bone's skin matrix for avatar cloth). Rest lengths and masses are measured through its linear part, so a
    /// slot or avatar scaled up gets cloth as big as it shows (0.29.x measured them in the mesh's own units: a sheet on a slot
    /// scaled 2x pulled itself to half its size). Measured again only when that scale changes by more than about 0.1 %
    /// (a turn alone changes nothing).
    /// </summary>
    public void SetRestFrame(in Matrix4x4 restToWorld)
    {
        var l = new Matrix4x4(restToWorld.M11, restToWorld.M12, restToWorld.M13, 0f,
                              restToWorld.M21, restToWorld.M22, restToWorld.M23, 0f,
                              restToWorld.M31, restToWorld.M32, restToWorld.M33, 0f,
                              0f, 0f, 0f, 1f);
        if (!float.IsFinite(l.M11 + l.M12 + l.M13 + l.M21 + l.M22 + l.M23 + l.M31 + l.M32 + l.M33) || !(MathF.Abs(l.GetDeterminant()) > 1e-18f)) return;
        if (_restSet && SameMetric(l, _restL)) return;
        bool first = !_restSet;
        _restL = l;
        _restSet = true;
        if (first && IsIdentityMetric(l)) return;     // as built
        for (int k = 0; k < _ns; k++) _sL[k] = RestLength(Mesh.RestPositions, _sA[k], _sB[k]);
        for (int k = 0; k < _nb; k++) _bL[k] = RestLength(Mesh.RestPositions, _bA[k], _bB[k]);
        ComputeMasses();
        _kindsDirty = true;
        Remeasures++;
    }

    /// <summary>Whether two linear maps give the same lengths and angles (the dot products of their rows, which a turn
    /// after them leaves alone), to 0.2 % of the squared scale.</summary>
    static bool SameMetric(in Matrix4x4 a, in Matrix4x4 b)
    {
        Vector3 a0 = new(a.M11, a.M12, a.M13), a1 = new(a.M21, a.M22, a.M23), a2 = new(a.M31, a.M32, a.M33);
        Vector3 b0 = new(b.M11, b.M12, b.M13), b1 = new(b.M21, b.M22, b.M23), b2 = new(b.M31, b.M32, b.M33);
        float tol = 2e-3f * MathF.Max(1e-30f, (b0.LengthSquared() + b1.LengthSquared() + b2.LengthSquared()) / 3f);
        return MathF.Abs(a0.LengthSquared() - b0.LengthSquared()) <= tol && MathF.Abs(a1.LengthSquared() - b1.LengthSquared()) <= tol &&
               MathF.Abs(a2.LengthSquared() - b2.LengthSquared()) <= tol && MathF.Abs(Vector3.Dot(a0, a1) - Vector3.Dot(b0, b1)) <= tol &&
               MathF.Abs(Vector3.Dot(a0, a2) - Vector3.Dot(b0, b2)) <= tol && MathF.Abs(Vector3.Dot(a1, a2) - Vector3.Dot(b1, b2)) <= tol;
    }

    static bool IsIdentityMetric(in Matrix4x4 l) => SameMetric(l, Matrix4x4.Identity) && SameMetric(Matrix4x4.Identity, l);

    // ------------------------------------------------------------------ placing it

    /// <summary>Every particle at its rest place in the world (the mesh as modelled), still.</summary>
    public void Reset(in Matrix4x4 rendererToWorld, ClothFrame anchors = null)
    {
        SetRestFrame(rendererToWorld);
        if (_kindsDirty) UpdateKinds();
        int n = Mesh.ParticleCount;
        var rest = Mesh.RestPositions;
        for (int i = 0; i < n; i++)
        {
            var x = Vector3.Transform(rest[i], rendererToWorld);
            int a = _anchor[i];
            if (a >= 0 && anchors != null && a < anchors.AnchorValid.Length && anchors.AnchorValid[a]) x = Vector3.Transform(_anchorLocal[i], anchors.AnchorToWorld[a]);
            X[i] = P[i] = _t0[i] = _t1[i] = x;
            V[i] = Vector3.Zero;
        }
        _lastM = rendererToWorld;
        _started = true;
        _accum = 0f;
        UpdateWorldBounds();
    }

    /// <summary>0.30.0, avatar cloth: every particle where the animation puts it (<see cref="ClothFrame.Targets"/>; anchored
    /// ones at their anchor), still. <paramref name="count"/>: a reset (a jump, a resume), not a frame that only follows the
    /// animation (culled or off).</summary>
    public void ResetToTargets(ClothFrame f, bool count = true)
    {
        SetRestFrame(f.BoneToWorld);
        if (_kindsDirty) UpdateKinds();
        _avatar = f.Targets != null;
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++)
        {
            Vector3 x;
            int a = _anchor[i];
            if (a >= 0 && a < f.AnchorValid.Length && f.AnchorValid[a]) x = Vector3.Transform(_anchorLocal[i], f.AnchorToWorld[a]);
            else x = TargetOf(f.Targets, i, X[i]);
            X[i] = P[i] = _t0[i] = _t1[i] = x;
            V[i] = Vector3.Zero;
        }
        _lastM = f.BoneToWorld;
        _started = true;
        _accum = 0f;
        if (count) Resets++;
        if (_avatar && Settings.Backstop > 0f) TargetNormals(n);
        UpdateWorldBounds();
    }

    /// <summary>Particle i's animated place: its original particle's target (when there is a finite one), else
    /// <paramref name="fallback"/>.</summary>
    Vector3 TargetOf(Vector3[] targets, int i, Vector3 fallback)
    {
        if (targets == null) return fallback;
        int o = Mesh.Origin(i);
        if ((uint)o >= (uint)targets.Length) return fallback;
        var t = targets[o];
        return float.IsFinite(t.X + t.Y + t.Z) ? t : fallback;
    }

    /// <summary>
    /// 0.30.0, avatar cloth: moves the free points along with <paramref name="part"/> of the bone's motion from
    /// <paramref name="from"/> to <paramref name="to"/> (bind space to world), as a turn about the bone's origin and a move of
    /// it, and turns their velocities with it. This is Inertia: the cloth is simulated as if partly in the avatar's own space
    /// (Unity's cloth, NvCloth and Chaos do the same; Chaos defaults to 0.75 of the motion felt). A turn faster than
    /// <see cref="SnapTurn"/> in one frame is a snap turn, carried whole.
    /// The frame's start moves with them: the held points' places and the free points' animated ones (which the substeps
    /// run from toward this frame's targets, for MaxDistance and the backstop). Otherwise the held edge came the whole way
    /// during the substeps while the carried cloth was already there, and dragged it back every frame: at Inertia 0 a skirt
    /// trailed 2 cm behind a walk, and a snap turn tore it 16 cm off its targets.
    /// </summary>
    void CarryPart(in Matrix4x4 from, in Matrix4x4 to, float part)
    {
        if (!Matrix4x4.Invert(from, out var inv)) return;
        var d = inv * to;                    // world to world: where a point on the bone a frame ago is now
        if (!TurnOf(d, out var turn)) return;
        float angle = 2f * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), MathF.Abs(turn.W));
        bool snap = angle > SnapTurn;
        if (snap) { part = 1f; SnapTurns++; }
        if (!(part > 0f)) return;
        var q = part >= 1f ? turn : Quaternion.Slerp(Quaternion.Identity, turn, part);
        var o0 = from.Translation;
        var move = (to.Translation - o0) * part;
        if (!float.IsFinite(move.X + move.Y + move.Z + q.X + q.Y + q.Z + q.W)) return;
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++)
        {
            _t0[i] = o0 + move + Vector3.Transform(_t0[i] - o0, q);
            X[i] = o0 + move + Vector3.Transform(X[i] - o0, q);
            P[i] = X[i];
            V[i] = Vector3.Transform(V[i], q);
        }
    }

    /// <summary>The turn of a world-to-world map (its rows normalized, so a change of scale is left out); false when it is
    /// not usable.</summary>
    static bool TurnOf(in Matrix4x4 t, out Quaternion q)
    {
        q = Quaternion.Identity;
        var r0 = new Vector3(t.M11, t.M12, t.M13); var r1 = new Vector3(t.M21, t.M22, t.M23); var r2 = new Vector3(t.M31, t.M32, t.M33);
        float l0 = r0.Length(), l1 = r1.Length(), l2 = r2.Length();
        if (!(l0 > 1e-6f && l1 > 1e-6f && l2 > 1e-6f) || !float.IsFinite(l0 + l1 + l2)) return false;
        r0 /= l0; r1 /= l1; r2 /= l2;
        q = Quaternion.CreateFromRotationMatrix(new Matrix4x4(r0.X, r0.Y, r0.Z, 0f, r1.X, r1.Y, r1.Z, 0f, r2.X, r2.Y, r2.Z, 0f, 0f, 0f, 0f, 1f));
        if (!float.IsFinite(q.X + q.Y + q.Z + q.W)) return false;
        float len = q.Length();
        if (!(len > 1e-6f)) return false;
        q = Quaternion.Normalize(q);
        if (q.W < 0f) q = Quaternion.Negate(q);
        return true;
    }

    /// <summary>0.30.0: the animated cloth's normal at each point, from the targets at the end of the frame (the simulated
    /// triangles' winding, times <see cref="BackstopSign"/>); zero where there is none.</summary>
    void TargetNormals(int n)
    {
        if (_tn.Length < Mesh.MaxParticles) _tn = new Vector3[Mesh.MaxParticles];
        Array.Clear(_tn, 0, n);
        var tri = Mesh.Triangles;
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            int ia = tri[3 * t], ib = tri[3 * t + 1], ic = tri[3 * t + 2];
            var c = Vector3.Cross(_t1[ib] - _t1[ia], _t1[ic] - _t1[ia]);
            _tn[ia] += c; _tn[ib] += c; _tn[ic] += c;
        }
        float sign = BackstopSign < 0f ? -1f : 1f;
        for (int i = 0; i < n; i++)
        {
            float l = _tn[i].Length();
            _tn[i] = l > 1e-20f && float.IsFinite(l) ? _tn[i] * (sign / l) : Vector3.Zero;
        }
    }

    void UpdateWorldBounds()
    {
        int n = Mesh.ParticleCount;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            if (Mesh.FanSize(i) == 0) continue;
            var x = X[i];
            if (!float.IsFinite(x.X + x.Y + x.Z)) continue;
            lo = Vector3.Min(lo, x); hi = Vector3.Max(hi, x);
        }
        if (lo.X > hi.X) lo = hi = Vector3.Zero;
        WorldMin = lo; WorldMax = hi;
    }

    /// <summary>Moves the whole cloth rigidly from one placement of its slot to another, and stops it.</summary>
    public void Carry(in Matrix4x4 from, in Matrix4x4 to)
    {
        if (!Matrix4x4.Invert(from, out var inv)) return;
        var m = inv * to;
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++)
        {
            X[i] = Vector3.Transform(X[i], m);
            P[i] = X[i];
            _t0[i] = Vector3.Transform(_t0[i], m);
            V[i] = Vector3.Zero;
        }
        Carries++;
    }

    static bool Jumped(in Matrix4x4 a, in Matrix4x4 b) =>
        !(Vector3.DistanceSquared(a.Translation, b.Translation) < 4f) || !float.IsFinite(b.M11 + b.M22 + b.M33 + b.M41 + b.M42 + b.M43);

    // ------------------------------------------------------------------ tearing (between steps)

    /// <summary>A tear at <paramref name="p"/> across its edge to <paramref name="other"/> (see <see cref="ClothMesh.Split"/>);
    /// the new particles start where, and as, the particles they were split from.</summary>
    public bool Split(int p, int other)
    {
        if (!Mesh.Split(p, other)) return false;
        TakeNewParticles(weak: true);
        _weak[p] = 1f;
        if ((uint)other < (uint)Mesh.ParticleCount) _weak[other] = MathF.Max(_weak[other], 1f);
        return true;
    }

    /// <summary>0.31.0: a cut between two triangles (see <see cref="ClothMesh.Cut"/>); new particles start where, and as,
    /// the particles they were separated from. A shred's cuts do not weaken the edges near them (the pieces are cut whole).</summary>
    public bool Cut(int t, int u)
    {
        if (!Mesh.Cut(t, u)) return false;
        if (Mesh.LastNewParticles.Count > 0) TakeNewParticles(weak: false);
        return true;
    }

    /// <summary>The particles the last split or cut made take their parent's state; the paint per particle is worked out
    /// again before it is next needed (0.31.0: not after every split: a shred makes hundreds of them in a frame).</summary>
    void TakeNewParticles(bool weak)
    {
        foreach (int q in Mesh.LastNewParticles)
        {
            int s = Mesh.Parent(q);
            X[q] = X[s]; P[q] = P[s]; V[q] = V[s]; _t0[q] = _t0[s]; _t1[q] = _t1[s];
            _anchor[q] = _anchor[s]; _anchorLocal[q] = _anchorLocal[s];
            _released[q] = _released[s];
            if (_tn.Length > q && _tn.Length > s) _tn[q] = _tn[s];
            _weak[q] = weak ? 1f : _weak[s];
        }
        _particlePaintDirty = true;
        _kindsDirty = true;
    }

    /// <summary>Forgets how fresh tears weakened the edges near them.</summary>
    public void ClearWeakness() => Array.Clear(_weak);

    // ------------------------------------------------------------------ the frame (worker)

    public void Step(ClothFrame f)
    {
        long t0 = Stopwatch.GetTimestamp();
        var s = Settings;
        if (Mesh.Version != _builtVersion) RebuildConstraints();
        if (_kindsDirty) UpdateKinds();
        int n = Mesh.ParticleCount;
        var m1 = f.RendererToWorld;
        _avatar = f.Targets != null;
        if (_avatar)
        {
            // 0.30.0, avatar cloth: in the bone's frame, partly (Inertia)
            var b1 = f.BoneToWorld;
            SetRestFrame(b1);
            if (_kindsDirty) UpdateKinds();
            if (!_started || f.Resumed || Teleported(_lastM, b1)) ResetToTargets(f);
            else CarryPart(_lastM, b1, 1f - s.Inertia);
        }
        else
        {
            SetRestFrame(m1);
            if (_kindsDirty) UpdateKinds();
            if (!_started) Reset(m1, f);
            else if (f.Resumed || Jumped(_lastM, m1)) Carry(_lastM, m1);
        }
        // 0.31.0: blasts' pushes (on the free points, after any points a blast set free became free)
        if (f.KickCount > 0) ApplyKicks(f);

        // where the held points go, and the free points' rest (or animated) places (for MaxDistance and the backstop)
        var rest = Mesh.RestPositions;
        var targets = f.Targets;
        for (int i = 0; i < n; i++)
        {
            int a = _anchor[i];
            if (a >= 0 && a < f.AnchorValid.Length && f.AnchorValid[a]) _t1[i] = Vector3.Transform(_anchorLocal[i], f.AnchorToWorld[a]);
            else if (a >= 0) _t1[i] = _t0[i];      // an anchor that is not there this frame: held where it was
            else if (targets != null) _t1[i] = TargetOf(targets, i, _t0[i]);
            else _t1[i] = Vector3.Transform(rest[i], m1);
        }
        if (_avatar && s.Backstop > 0f) TargetNormals(n);

        float dt = MathF.Min(MathF.Max(f.Dt, 0f), 0.1f);
        WindPerTriangle(f);

        // 0.33.1: the steps first: the pairs look as far ahead as the points move in the time this frame simulates (at
        // most MaxStepsPerFrame steps), not the frame's own time (up to 0.1 s once the game slows down)
        float h = 1f / s.StepRate;
        _accum += dt;
        int steps = (int)(_accum / h);
        if (steps > ClothSettings.MaxStepsPerFrame) { steps = ClothSettings.MaxStepsPerFrame; _accum = 0f; }
        else if (steps < 1) { steps = 1; _accum = MathF.Max(_accum - h, -h); }
        else _accum -= steps * h;
        StepsLastFrame = steps;
        _frameDt = dt;

        // 0.33.1: the steps in runs, the pairs found again before each. A point looks for shapes along its way, at most
        // MaxReach: when the fastest would get further in this frame's steps (a blast at low frame rates, 33 ms of steps a
        // frame), the steps go in more runs, up to MaxPairRuns
        int runs = PairRunsFor(f, steps, h);
        PairRuns = runs;
        PairCount = 0; PairsMs = 0; BodiesMs = 0;
        _contactTicks = 0;

        var g = f.Gravity * s.Gravity * h;
        float damp = MathF.Exp(-s.DampingRate * h);
        float h2 = h * h;
        float atS = s.StretchCompliance / h2, atB = s.BendCompliance / h2;
        int contacts = 0, done = 0;
        for (int run = 0; run < runs; run++)
        {
            int take = (steps - done) / (runs - run);
            long tp = Stopwatch.GetTimestamp();
            BuildPairs(f, dt, take * h);
            PairsMs += (Stopwatch.GetTimestamp() - tp) * 1000.0 / Stopwatch.Frequency;
            if (_pairCount > PairCount) PairCount = _pairCount;
            for (int k = 0; k < take; k++, done++)
                contacts = Substep(h, (done + 1) / (float)steps, g, damp, atS, atB);
        }
        Contacts = contacts;
        ContactsMs = _contactTicks * 1000.0 / Stopwatch.Frequency;

        TearCandidates.Clear();
        MaxStrain = MeasureStrain(f.FindTears && s.Tearable);

        for (int i = 0; i < n; i++) _t0[i] = _t1[i];
        _lastM = _avatar ? f.BoneToWorld : m1;
        UpdateWorldBounds();
        Frames++;
        LastStepMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>0.31.0: each kick adds its push to every free point's velocity (each kick once: KickCount goes back to 0).</summary>
    void ApplyKicks(ClothFrame f)
    {
        int n = Mesh.ParticleCount;
        int count = Math.Min(f.KickCount, f.Kicks.Length);
        f.KickCount = 0;
        for (int k = 0; k < count; k++)
        {
            var kick = f.Kicks[k];
            if (!(kick.Radius > 0f) || !float.IsFinite(kick.Center.X + kick.Center.Y + kick.Center.Z)) continue;
            for (int i = 0; i < n; i++)
            {
                if (_w[i] == 0f) continue;
                var dv = ClothBlastMath.Kick(kick, X[i]);
                if (dv != Vector3.Zero && float.IsFinite(dv.X + dv.Y + dv.Z)) V[i] += dv;
            }
            Kicks++;
        }
    }

    /// <summary>0.30.0, avatar cloth: the bone jumped more than <see cref="TeleportDistance"/> (or is not finite).</summary>
    static bool Teleported(in Matrix4x4 a, in Matrix4x4 b) =>
        !(Vector3.DistanceSquared(a.Translation, b.Translation) < TeleportDistance * TeleportDistance) ||
        !float.IsFinite(b.M11 + b.M22 + b.M33 + b.M41 + b.M42 + b.M43);

    float _frameDt;

    /// <summary>Passes over the edges per step (see the stretch loop in <see cref="Substep"/>).</summary>
    public const int StretchPasses = 2;
    /// <summary>How hard a squeezed edge pushes back, as a fraction of how hard a stretched one pulls.</summary>
    public const float CompressionScale = 0.1f;

    int Substep(float h, float a, Vector3 g, float damp, float atS, float atB)
    {
        int n = Mesh.ParticleCount;
        var X = this.X; var P = this.P; var V = this.V; var w = _w;
        if (_anyWind || Settings.Drag > 0f || Settings.Lift > 0f) Aero(h);
        float invH = 1f / h;
        for (int i = 0; i < n; i++)
        {
            if (w[i] == 0f)
            {
                P[i] = X[i];
                X[i] = Vector3.Lerp(_t0[i], _t1[i], a);
                V[i] = (X[i] - P[i]) * invH;
                continue;
            }
            var v = (V[i] + g) * damp;
            V[i] = v;
            P[i] = X[i];
            X[i] += v * h;
        }

        // stretch: two passes, and a squeezed edge pushes back only softly. With one pass, and edges that push back as hard
        // as they pull, a fine sheet (41 by 41 points and up) buckled in its own plane soon after it was let go: its
        // triangles folded over each other and it never came to rest (more steps made it worse). Real cloth buckles
        // rather than compresses anyway.
        for (int pass = 0; pass < StretchPasses; pass++)
            for (int k = 0; k < _ns; k++)
            {
                int ia = _sA[k], ib = _sB[k];
                float wa = w[ia], wb = w[ib], ws = wa + wb;
                if (ws == 0f) continue;
                var d = X[ia] - X[ib];
                float len = d.Length();
                if (!(len > 1e-9f)) continue;
                float c = len - _sL[k];
                float dl = -c / (ws + atS);
                if (c < 0f) dl *= CompressionScale;
                var corr = d * (dl / len);
                X[ia] += corr * wa; X[ib] -= corr * wb;
            }
        // bending
        for (int k = 0; k < _nb; k++)
        {
            int ia = _bA[k], ib = _bB[k];
            float wa = w[ia], wb = w[ib], ws = wa + wb;
            if (ws == 0f) continue;
            var d = X[ia] - X[ib];
            float len = d.Length();
            if (!(len > 1e-9f)) continue;
            float dl = -(len - _bL[k]) / (ws + atB);
            var corr = d * (dl / len);
            X[ia] += corr * wa; X[ib] -= corr * wb;
        }
        // max distance
        if (Settings.MaxDistance > 0f)
            for (int i = 0; i < n; i++)
            {
                float lim = _limit[i];
                if (!(lim > 0f)) continue;
                var target = Vector3.Lerp(_t0[i], _t1[i], a);
                var d = X[i] - target;
                float l2 = d.LengthSquared();
                if (l2 > lim * lim) X[i] = target + d * (lim / MathF.Sqrt(l2));
            }
        // 0.30.0, avatar cloth: the backstop (no further behind the animated place than Backstop, along the animated normal)
        float bs = Settings.Backstop;
        if (_avatar && bs > 0f)
            for (int i = 0; i < n; i++)
            {
                if (w[i] == 0f) continue;
                var nn = _tn[i];
                if (nn == Vector3.Zero) continue;
                var target = Vector3.Lerp(_t0[i], _t1[i], a);
                float d = Vector3.Dot(X[i] - target, nn);
                if (d < -bs) X[i] += nn * (-bs - d);
            }
        int touched = 0;
        if (_pairCount > 0)
        {
            long tc = Stopwatch.GetTimestamp();
            touched = SolveContacts(h, a);
            _contactTicks += Stopwatch.GetTimestamp() - tc;
        }
        for (int i = 0; i < n; i++)
        {
            if (w[i] == 0f) continue;
            var v = (X[i] - P[i]) * invH;
            if (_hit[i])
            {
                // inelastic: no faster away from the collider than the collider itself
                _hit[i] = false;
                float l = _push[i].Length();
                if (l > 1e-12f)
                {
                    var nrm = _push[i] / l;
                    float vn = Vector3.Dot(v - _hitVel[i], nrm);
                    if (vn > 0f) v -= nrm * vn;
                }
            }
            V[i] = v;
        }
        return touched;
    }

    // ------------------------------------------------------------------ air

    void WindPerTriangle(ClothFrame f)
    {
        int nt = Mesh.TriangleCount;
        if (_wind.Length < nt) _wind = new Vector3[nt];
        _anyWind = f.WindCount > 0;
        if (!_anyWind) { Array.Clear(_wind, 0, nt); return; }
        var tri = Mesh.Triangles;
        var winds = f.Winds.AsSpan(0, Math.Min(f.WindCount, f.Winds.Length));
        for (int t = 0; t < nt; t++)
        {
            var c = (X[tri[3 * t]] + X[tri[3 * t + 1]] + X[tri[3 * t + 2]]) * (1f / 3f);
            _wind[t] = ClothWindField.Sample(winds, c, f.Time);
        }
    }

    void Aero(float h)
    {
        var s = Settings;
        float cd = s.Drag, cl = s.Lift;
        if (cd <= 0f && cl <= 0f) return;
        float rho = ClothSettings.AirDensity;
        var tri = Mesh.Triangles;
        var w = _w;
        int nt = Mesh.TriangleCount;
        for (int t = 0; t < nt; t++)
        {
            int ia = tri[3 * t], ib = tri[3 * t + 1], ic = tri[3 * t + 2];
            if (w[ia] + w[ib] + w[ic] == 0f) continue;
            var x0 = X[ia];
            var cr = Vector3.Cross(X[ib] - x0, X[ic] - x0);
            float twoA = cr.Length();
            if (!(twoA > 1e-10f)) continue;
            var nrm = cr / twoA;
            var vrel = (V[ia] + V[ib] + V[ic]) * (1f / 3f) - _wind[t];
            float sp2 = vrel.LengthSquared();
            if (!(sp2 > 1e-8f)) continue;
            float sp = MathF.Sqrt(sp2);
            var d = vrel / sp;
            float cos = Vector3.Dot(nrm, d);
            float q = 0.25f * rho * twoA * sp2;              // dynamic pressure x area
            var force = d * (-cd * q * MathF.Abs(cos)) - (nrm - d * cos) * (cl * q * cos);
            float cap = 0.5f * sp;
            Push(ia, force, h, cap); Push(ib, force, h, cap); Push(ic, force, h, cap);
        }
    }

    void Push(int i, Vector3 force, float h, float cap)
    {
        float wi = _w[i];
        if (wi == 0f) return;
        var dv = force * (wi * h / 3f);
        float l2 = dv.LengthSquared();
        if (l2 > cap * cap) dv *= cap / MathF.Sqrt(l2);
        V[i] += dv;
    }

    // ------------------------------------------------------------------ collisions

    FleshCollider[] _shapes = Array.Empty<FleshCollider>();
    int _shapeCount;
    int[] _pairP = Array.Empty<int>(), _pairS = Array.Empty<int>();
    int _pairCount;
    readonly List<int> _triHits = new();
    long[] _cellKey = Array.Empty<long>();
    int[] _cellPoint = Array.Empty<int>();
    int _cellCount;
    bool[] _hit = Array.Empty<bool>();
    Vector3[] _push = Array.Empty<Vector3>(), _hitVel = Array.Empty<Vector3>();
    public const int MaxMeshTriangles = 20000;
    /// <summary>Shapes near the cloth this frame (mesh colliders cut down to their triangles near it), for the debug view.</summary>
    public ReadOnlySpan<FleshCollider> NearShapes => _shapes.AsSpan(0, _shapeCount);

    // 0.30.0: per shape, a sphere of the wearer's own body (avatar cloth)
    bool[] _shapeOwn = Array.Empty<bool>();

    void AddShape(in FleshCollider c, float dt, bool own = false)
    {
        if (_shapeCount == _shapes.Length) Array.Resize(ref _shapes, Math.Max(16, _shapes.Length * 2));
        if (_shapeOwn.Length < _shapes.Length) Array.Resize(ref _shapeOwn, _shapes.Length);
        _shapeOwn[_shapeCount] = own;
        ref var sh = ref _shapes[_shapeCount++];
        sh = c;
        sh.Bounds(out sh.BMin, out sh.BMax);
        if (sh.Velocity != Vector3.Zero)
        {
            var back = -sh.Velocity * dt;
            sh.BMin = Vector3.Min(sh.BMin, sh.BMin + back); sh.BMax = Vector3.Max(sh.BMax, sh.BMax + back);
        }
    }

    void AddPair(int p, int s)
    {
        if (_pairCount == _pairP.Length)
        {
            int c = Math.Max(64, _pairP.Length * 2);
            Array.Resize(ref _pairP, c); Array.Resize(ref _pairS, c);
        }
        _pairP[_pairCount] = p; _pairS[_pairCount] = s; _pairCount++;
    }

    static long CellKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

    /// <summary>0.33.1: metres a point looks ahead for shapes in one run of steps, at most. When a point would get further
    /// in a frame, the frame's steps go in more runs (<see cref="MaxPairRuns"/> at most); a point that would still get
    /// further in a run slows to this (see <see cref="BuildPairs"/>).</summary>
    public const float MaxReach = 1f;
    /// <summary>0.33.1: the most runs a frame's steps go in, the pairs found again before each.</summary>
    public const int MaxPairRuns = 4;

    /// <summary>0.33.1: as many runs as the fastest free point needs to get no further in one than MaxReach (with the
    /// margin BuildPairs looks ahead with), at most MaxPairRuns and one a step.</summary>
    int PairRunsFor(ClothFrame f, int steps, float h)
    {
        if (steps <= 1 || !Settings.Collide || (f.Colliders.Count == 0 && f.BodyCount == 0)) return 1;
        float v2 = 0f;
        int n = Mesh.ParticleCount;
        for (int i = 0; i < n; i++)
        {
            if (_w[i] == 0f) continue;
            float l2 = V[i].LengthSquared();
            if (l2 > v2) v2 = l2;
        }
        float go = 1.5f * MathF.Sqrt(v2) * steps * h;
        if (!float.IsFinite(go) || go <= MaxReach) return 1;
        int most = Math.Min(steps, MaxPairRuns);
        float want = MathF.Ceiling(go / MaxReach);
        return want >= most ? most : (int)want;
    }
    /// <summary>0.33.1: per point, how far it looks for shapes this frame, and the box it looks in: along its way, not all
    /// round (see <see cref="BuildPairs"/>).</summary>
    float[] _reach = Array.Empty<float>();
    Vector3[] _reachLo = Array.Empty<Vector3>(), _reachHi = Array.Empty<Vector3>(), _reachD = Array.Empty<Vector3>();

    /// <summary>Once a frame: the shapes near the cloth, each paired with the free points that can reach it this frame
    /// (through a grid over the points; a shape larger than the cloth goes through every point). <paramref name="dt"/>:
    /// the frame's time (how far the colliders move); <paramref name="simDt"/>: the time its steps simulate (how far the
    /// points move).</summary>
    void BuildPairs(ClothFrame f, float dt, float simDt)
    {
        _shapeCount = 0; _pairCount = 0; MeshTriangles = 0;
        int n = Mesh.ParticleCount;
        if (_hit.Length < Mesh.MaxParticles) { _hit = new bool[Mesh.MaxParticles]; _push = new Vector3[Mesh.MaxParticles]; _hitVel = new Vector3[Mesh.MaxParticles]; }
        if (_reach.Length < Mesh.MaxParticles) { _reach = new float[Mesh.MaxParticles]; _reachLo = new Vector3[Mesh.MaxParticles]; _reachHi = new Vector3[Mesh.MaxParticles]; _reachD = new Vector3[Mesh.MaxParticles]; }
        BodySpheres = 0;
        if (!Settings.Collide || (f.Colliders.Count == 0 && f.BodyCount == 0)) return;
        float r = Settings.Thickness;
        // 0.33.1: each point looks as far as it can get this frame (its own speed, and gravity), at most MaxReach, and only
        // along its way. With one reach for all, set by the fastest point, a blast at low Drag paired every point with every
        // shape near the cloth (every body sphere of the players around it): millions of pairs, gone through in every step
        // (about 10 fps); and a piece flying away from a player still looked back into them
        float m0 = 2f * r + 0.5f * (f.Gravity * Settings.Gravity).Length() * simDt * simDt + 0.01f;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        var pad = new Vector3(m0);
        float m = m0;
        int pts = 0;
        for (int i = 0; i < n; i++)
        {
            if (_w[i] == 0f) continue;
            var x = X[i];
            lo = Vector3.Min(lo, x); hi = Vector3.Max(hi, x);
            var d = V[i] * (1.5f * simDt);
            float go = d.Length();
            if (go > MaxReach && float.IsFinite(go))
            {
                // no further than it looks: a point that would get further in this run than MaxReach even in MaxPairRuns
                // runs (faster than about 80 m/s at 30 fps and below, 240 at 90) slows to that, so it never flies through
                // a shape it did not look for (a mesh floor)
                float k = MaxReach / go;
                d *= k; V[i] *= k; go = MaxReach;
            }
            if (go <= MaxReach) { var e = x + d; _reachLo[i] = Vector3.Min(x, e) - pad; _reachHi[i] = Vector3.Max(x, e) + pad; }
            else { go = MaxReach; d = Vector3.Zero; _reachLo[i] = x - new Vector3(m0 + MaxReach); _reachHi[i] = x + new Vector3(m0 + MaxReach); }   // not finite: all round
            _reachD[i] = d;
            float mi = m0 + go;
            _reach[i] = mi;
            if (mi > m) m = mi;
            pts++;
        }
        if (pts == 0) return;
        if (!float.IsFinite(m + lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z)) return;
        var bmin = lo - new Vector3(m); var bmax = hi + new Vector3(m);
        BuildNear(n, pts);
        foreach (var c in f.Colliders)
        {
            c.Bounds(out var cmin, out var cmax);
            if (c.Velocity != Vector3.Zero) { var back = -c.Velocity * dt; cmin = Vector3.Min(cmin, cmin + back); cmax = Vector3.Max(cmax, cmax + back); }
            if (cmin.X > bmax.X || cmax.X < bmin.X || cmin.Y > bmax.Y || cmax.Y < bmin.Y || cmin.Z > bmax.Z || cmax.Z < bmin.Z) continue;
            if (c.Kind != FleshColliderKind.Mesh) { if (Near(cmin, cmax)) AddShape(c, dt); continue; }
            if (c.Mesh == null) continue;
            _triHits.Clear();
            // 0.33.1: the triangles in the cells the points look in (the cloth's whole box, once its pieces are far apart,
            // can hold a whole map's floor)
            if (!_nearAll && _nearCells.Count <= NearQueries)
                foreach (var (x, y, z) in _nearCells)
                {
                    var a = new Vector3(x, y, z) * NearCell;
                    FleshCollider.TransformBox(a, a + new Vector3(NearCell), c.MInv, out var qa, out var qb);
                    c.Mesh.Query(qa, qb, _triHits);
                }
            else
            {
                FleshCollider.TransformBox(bmin, bmax, c.MInv, out var qmin, out var qmax);
                c.Mesh.Query(qmin, qmax, _triHits);
            }
            _triHits.Sort();
            bool flip = c.M.GetDeterminant() < 0f;
            int last = -1;
            foreach (int t in _triHits)
            {
                if (t == last) continue;                 // in more than one cell
                last = t;
                if (MeshTriangles >= MaxMeshTriangles) break;
                var tri = FleshCollider.Triangle(Vector3.Transform(c.Mesh.A[t], c.M), Vector3.Transform(c.Mesh.B[t], c.M), Vector3.Transform(c.Mesh.C[t], c.M), c.TwoSided);
                tri.Source = c.Source; tri.Velocity = c.Velocity;
                if (flip) { (tri.B, tri.C) = (tri.C, tri.B); tri.SetPlane(); }
                AddShape(tri, dt);
                if (!Near(_shapes[_shapeCount - 1].BMin, _shapes[_shapeCount - 1].BMax)) { _shapeCount--; continue; }
                MeshTriangles++;
            }
        }
        int bodyFrom = _shapeCount;
        long tb = Stopwatch.GetTimestamp();
        for (int b = 0; b < f.BodyCount && b < f.Bodies.Count; b++) AddBody(f.Bodies[b], bmin, bmax, dt);
        if (_shapeCount > bodyFrom) PairSpheres(bodyFrom, _shapeCount, n, m0, dt);
        BodiesMs += (Stopwatch.GetTimestamp() - tb) * 1000.0 / Stopwatch.Frequency;
        if (bodyFrom == 0) return;

        // the grid over the free points
        float cell = MathF.Max(2f * m, 0.05f);
        if (_cellKey.Length < pts) { _cellKey = new long[pts]; _cellPoint = new int[pts]; }
        _cellCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (_w[i] == 0f) continue;
            var q = (X[i] - lo) / cell;
            _cellKey[_cellCount] = CellKey((int)q.X, (int)q.Y, (int)q.Z);
            _cellPoint[_cellCount] = i;
            _cellCount++;
        }
        Array.Sort(_cellKey, _cellPoint, 0, _cellCount);
        int gx = (int)((hi.X - lo.X) / cell) + 1, gy = (int)((hi.Y - lo.Y) / cell) + 1, gz = (int)((hi.Z - lo.Z) / cell) + 1;
        for (int sh = 0; sh < bodyFrom; sh++)
        {
            var smin = _shapes[sh].BMin - new Vector3(m); var smax = _shapes[sh].BMax + new Vector3(m);
            int x0 = Math.Max(0, (int)MathF.Floor((smin.X - lo.X) / cell)), x1 = Math.Min(gx - 1, (int)MathF.Floor((smax.X - lo.X) / cell));
            int y0 = Math.Max(0, (int)MathF.Floor((smin.Y - lo.Y) / cell)), y1 = Math.Min(gy - 1, (int)MathF.Floor((smax.Y - lo.Y) / cell));
            int z0 = Math.Max(0, (int)MathF.Floor((smin.Z - lo.Z) / cell)), z1 = Math.Min(gz - 1, (int)MathF.Floor((smax.Z - lo.Z) / cell));
            if (x0 > x1 || y0 > y1 || z0 > z1) continue;
            long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (cells > _cellCount)
            {
                for (int i = 0; i < n; i++) if (_w[i] != 0f && Reaches(i, sh)) AddPair(i, sh);
                continue;
            }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = CellKey(x, y, z);
                        int k = Array.BinarySearch(_cellKey, 0, _cellCount, key);
                        if (k < 0) continue;
                        while (k > 0 && _cellKey[k - 1] == key) k--;
                        for (; k < _cellCount && _cellKey[k] == key; k++)
                            if (Reaches(_cellPoint[k], sh)) AddPair(_cellPoint[k], sh);
                    }
        }
    }

    // ---- 0.33.1: the coarse cells the points look in this frame. Once a cloth's pieces fly far apart (a blast at low Drag:
    // 40 m in a second), its box holds everything around: every sphere of every player in it, a whole floor's triangles.
    // A shape goes in only if its box is in a cell some point looks in.

    /// <summary>0.33.1: metres, the side of the cells that say which shapes are near the cloth's points at all.</summary>
    public const float NearCell = 1f;
    /// <summary>A shape spanning more cells than this is not looked up (the points' own test decides).</summary>
    const int NearMostCells = 64;
    /// <summary>A mesh collider is searched cell by cell up to this many cells, else in the cloth's whole box.</summary>
    const int NearQueries = 512;
    long[] _nearKey = Array.Empty<long>();
    int[] _nearStamp = Array.Empty<int>();
    int _nearGen;
    bool _nearAll;
    readonly List<(int x, int y, int z)> _nearCells = new();

    void BuildNear(int n, int pts)
    {
        _nearCells.Clear();
        _nearAll = false;
        int cap = 64;
        while (cap < 4 * pts) cap <<= 1;
        if (_nearKey.Length < cap) { _nearKey = new long[cap]; _nearStamp = new int[cap]; _nearGen = 0; }
        if (++_nearGen == int.MaxValue) { Array.Clear(_nearStamp); _nearGen = 1; }
        int mask = _nearKey.Length - 1, room = _nearKey.Length / 2;
        const float inv = 1f / NearCell;
        for (int i = 0; i < n; i++)
        {
            if (_w[i] == 0f) continue;
            var a = _reachLo[i] * inv; var b = _reachHi[i] * inv;
            int x0 = (int)MathF.Floor(a.X), x1 = (int)MathF.Floor(b.X), y0 = (int)MathF.Floor(a.Y), y1 = (int)MathF.Floor(b.Y), z0 = (int)MathF.Floor(a.Z), z1 = (int)MathF.Floor(b.Z);
            // a reach box is at most about a metre: anything wider is not finite or out of range (everything counts as near)
            if ((long)x1 - x0 > 3 || (long)y1 - y0 > 3 || (long)z1 - z0 > 3 || x1 < x0 || y1 < y0 || z1 < z0) { _nearAll = true; return; }
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = CellKey(x, y, z);
                        int h = SphSlot(key, mask);
                        while (_nearStamp[h] == _nearGen && _nearKey[h] != key) h = (h + 1) & mask;
                        if (_nearStamp[h] == _nearGen) continue;
                        if (_nearCells.Count >= room) { _nearAll = true; return; }
                        _nearStamp[h] = _nearGen; _nearKey[h] = key;
                        _nearCells.Add((x, y, z));
                    }
        }
    }

    /// <summary>0.33.1: the box from <paramref name="a"/> to <paramref name="b"/> is in a cell some point looks in this frame
    /// (true for a box too big to look up).</summary>
    bool Near(Vector3 a, Vector3 b)
    {
        if (_nearAll) return true;
        const float inv = 1f / NearCell;
        a *= inv; b *= inv;
        float fx0 = MathF.Floor(a.X), fy0 = MathF.Floor(a.Y), fz0 = MathF.Floor(a.Z);
        float fx1 = MathF.Floor(b.X), fy1 = MathF.Floor(b.Y), fz1 = MathF.Floor(b.Z);
        if (!((fx1 - fx0 + 1f) * (fy1 - fy0 + 1f) * (fz1 - fz0 + 1f) <= NearMostCells)) return true;
        int x0 = (int)fx0, x1 = (int)fx1, y0 = (int)fy0, y1 = (int)fy1, z0 = (int)fz0, z1 = (int)fz1;
        int mask = _nearKey.Length - 1;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = CellKey(x, y, z);
                    int h = SphSlot(key, mask);
                    while (_nearStamp[h] == _nearGen)
                    {
                        if (_nearKey[h] == key) return true;
                        h = (h + 1) & mask;
                    }
                }
        return false;
    }

    /// <summary>0.33.1: shape <paramref name="sh"/>'s box overlaps the box point <paramref name="i"/> looks in this frame.</summary>
    bool Reaches(int i, int sh)
    {
        ref readonly var c = ref _shapes[sh];
        var a = _reachLo[i]; var b = _reachHi[i];
        return a.X <= c.BMax.X && b.X >= c.BMin.X && a.Y <= c.BMax.Y && b.Y >= c.BMin.Y && a.Z <= c.BMax.Z && b.Z >= c.BMin.Z;
    }

    // 0.29.2: the pairs for players' body spheres (hundreds of small shapes): the spheres go into the cells their reach
    // overlaps, and each point looks in the cells its way this frame crosses (0.33.1; its own cell, for a point at rest),
    // rather than each sphere searching the points' grid
    long[] _sphKey = Array.Empty<long>();
    int[] _sphHead = Array.Empty<int>(), _sphNext = Array.Empty<int>(), _sphItem = Array.Empty<int>();
    /// <summary>0.33.1: per body sphere (counted from the first), the first cell it went in.</summary>
    int[] _sphX = Array.Empty<int>(), _sphY = Array.Empty<int>(), _sphZ = Array.Empty<int>();

    /// <summary>0.33.1: metres a point looks ahead for players' body spheres in a frame, at most. A piece flying faster
    /// mostly passes through players, finding the spheres further on a frame later; looking as far as MaxReach took each
    /// point next to a body through hundreds of spheres (25 ms in the frame a blast went off on a player).</summary>
    public const float MaxBodyReach = 0.15f;

    /// <summary>The pairs for the body spheres from <paramref name="from"/> to <paramref name="to"/>: <paramref name="m0"/>
    /// is the points' base reach (see <see cref="BuildPairs"/>), <paramref name="dt"/> the frame's time.</summary>
    void PairSpheres(int from, int to, int n, float m0, float dt)
    {
        float ext = 0f;
        for (int sh = from; sh < to; sh++)
        {
            var e = _shapes[sh].BMax - _shapes[sh].BMin;
            ext = MathF.Max(ext, MathF.Max(e.X, MathF.Max(e.Y, e.Z)));
        }
        // each sphere's box, grown by the base reach, spans at most 3 cells a side
        float cell = MathF.Max(0.05f, 0.5f * (ext + 2f * m0));
        if (!float.IsFinite(cell)) return;
        float inv = 1f / cell;
        int count = to - from, most = count * 27;
        int cap = 64;
        while (cap < 2 * most) cap <<= 1;
        if (_sphKey.Length < cap) { _sphKey = new long[cap]; _sphHead = new int[cap]; }
        if (_sphNext.Length < most) { _sphNext = new int[most]; _sphItem = new int[most]; }
        if (_sphX.Length < count) { _sphX = new int[count]; _sphY = new int[count]; _sphZ = new int[count]; }
        Array.Fill(_sphKey, long.MinValue, 0, cap);
        int mask = cap - 1, entries = 0;
        var mv = new Vector3(m0);
        for (int sh = from; sh < to; sh++)
        {
            var a = (_shapes[sh].BMin - mv) * inv; var b = (_shapes[sh].BMax + mv) * inv;
            int x0 = (int)MathF.Floor(a.X), x1 = (int)MathF.Floor(b.X), y0 = (int)MathF.Floor(a.Y), y1 = (int)MathF.Floor(b.Y), z0 = (int)MathF.Floor(a.Z), z1 = (int)MathF.Floor(b.Z);
            _sphX[sh - from] = x0; _sphY[sh - from] = y0; _sphZ[sh - from] = z0;
            for (int x = x0; x <= x1 && x <= x0 + 2; x++)
                for (int y = y0; y <= y1 && y <= y0 + 2; y++)
                    for (int z = z0; z <= z1 && z <= z0 + 2; z++)
                    {
                        long key = CellKey(x, y, z);
                        int h = SphSlot(key, mask);
                        while (_sphKey[h] != long.MinValue && _sphKey[h] != key) h = (h + 1) & mask;
                        if (_sphKey[h] == long.MinValue) { _sphKey[h] = key; _sphHead[h] = -1; }
                        _sphItem[entries] = sh; _sphNext[entries] = _sphHead[h]; _sphHead[h] = entries; entries++;
                    }
        }
        for (int i = 0; i < n; i++)
        {
            if (_w[i] == 0f) continue;
            var p = X[i];
            // its way this frame, as far as MaxBodyReach, and the box it looks in around that
            var d = _reachD[i];
            float dl = d.Length();
            if (dl > MaxBodyReach) { d *= MaxBodyReach / dl; dl = MaxBodyReach; }
            var wlo = Vector3.Min(p, p + d); var whi = Vector3.Max(p, p + d);
            var qa = wlo * inv; var qb = whi * inv;
            int px0 = (int)MathF.Floor(qa.X), px1 = (int)MathF.Floor(qb.X), py0 = (int)MathF.Floor(qa.Y), py1 = (int)MathF.Floor(qb.Y), pz0 = (int)MathF.Floor(qa.Z), pz1 = (int)MathF.Floor(qb.Z);
            if ((long)px1 - px0 > 4 || (long)py1 - py0 > 4 || (long)pz1 - pz0 > 4 || px1 < px0 || py1 < py0 || pz1 < pz0) continue;   // not finite
            var blo = wlo - mv; var bhi = whi + mv;
            float far = m0 + dl;
            for (int x = px0; x <= px1; x++)
                for (int y = py0; y <= py1; y++)
                    for (int z = pz0; z <= pz1; z++)
                    {
                        long key = CellKey(x, y, z);
                        int h = SphSlot(key, mask);
                        while (_sphKey[h] != long.MinValue && _sphKey[h] != key) h = (h + 1) & mask;
                        if (_sphKey[h] == long.MinValue) continue;
                        for (int e = _sphHead[h]; e >= 0; e = _sphNext[e])
                        {
                            int sh = _sphItem[e], k = sh - from;
                            // once per sphere: in the first cell that both it and this point's way are in
                            if (x != Math.Max(_sphX[k], px0) || y != Math.Max(_sphY[k], py0) || z != Math.Max(_sphZ[k], pz0)) continue;
                            ref readonly var c = ref _shapes[sh];
                            if (blo.X > c.BMax.X || bhi.X < c.BMin.X || blo.Y > c.BMax.Y || bhi.Y < c.BMin.Y || blo.Z > c.BMax.Z || bhi.Z < c.BMin.Z) continue;
                            // within reach of the sphere anywhere along this frame's sweep (not just of its box's corners)
                            var end = c.A; var start = end - c.Velocity * dt;
                            var seg = end - start;
                            float t = seg.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(p - start, seg) / seg.LengthSquared(), 0f, 1f) : 0f;
                            float reach = c.R + far;
                            if (Vector3.DistanceSquared(p, start + seg * t) <= reach * reach) AddPair(i, sh);
                        }
                    }
        }
    }

    static int SphSlot(long key, int mask) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 40) & mask;

    // ---- 0.29.2: players' body shapes

    /// <summary>Body spheres near the cloth in the last step.</summary>
    public int BodySpheres { get; private set; }
    /// <summary>A body sphere moves at most this fast (m/s) as far as friction and the push are concerned; one that jumps
    /// more than half a metre in a frame (a teleport) counts as still.</summary>
    public const float MaxBodySpeed = 5f;
    /// <summary>m/s² per unit of Friction: a point resting on a player holds on against this much sideways pull on top of
    /// the friction its weight gives (0.8 friction: 3 m/s², about a third of gravity), so a blanket stays on a body's
    /// slopes and is carried along when the body moves, unless pulled harder. Only on players' shapes.</summary>
    public const float BodyGrip = 3.75f;

    Vector3[] _bodyC = Array.Empty<Vector3>(), _bodyL = Array.Empty<Vector3>(), _boneTurn = Array.Empty<Vector3>();
    float[] _bodyR = Array.Empty<float>(), _boneScale = Array.Empty<float>();

    /// <summary>Poses a body shape's spheres (and where they were a frame ago, for their velocity: each sphere moves on its
    /// own, so a turning body drags the cloth along with its surface, not just its bones' centres; and each turns with its
    /// bones, so even one on the axis of a turn carries the cloth on it round) and adds the ones near the cloth.</summary>
    void AddBody(ClothBody body, Vector3 bmin, Vector3 bmax, float dt)
    {
        var shape = body.Shape;
        if (shape == null || shape.Count == 0 || body.Skin.Length < shape.BoneCount + 1) return;
        int n = shape.Count, nb = shape.BoneCount + 1;
        if (_bodyC.Length < n) { _bodyC = new Vector3[n]; _bodyL = new Vector3[n]; _bodyR = new float[n]; }
        if (_boneScale.Length < nb) { _boneScale = new float[nb]; _boneTurn = new Vector3[nb]; }
        shape.Pose(body.Skin, _boneScale, _bodyC, _bodyR);
        bool moving = body.HasLast && body.Last.Length == body.Skin.Length;
        float inv = moving ? 1f / body.LastDt : 0f;
        if (moving)
        {
            shape.PoseCenters(body.Last, _bodyL);
            for (int b = 0; b < nb; b++) _boneTurn[b] = TurnRate(body.Last[b], body.Skin[b], inv);
        }
        var skip = body.SkipBone;
        var skipSphere = body.SkipSphere != null && body.SkipSphere.Length >= n ? body.SkipSphere : null;
        bool own = body.Own && _avatar;
        var bones = shape.Bones; var wts = shape.Weights;
        float thick = Settings.Thickness;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            if (skip != null && skip[bones[o]]) continue;
            if (skipSphere != null && skipSphere[i]) continue;
            var c = _bodyC[i];
            float rad = _bodyR[i];
            if (!(rad > 0f) || !float.IsFinite(rad + c.X + c.Y + c.Z)) continue;
            Vector3 v = Vector3.Zero, w = Vector3.Zero;
            if (moving)
            {
                var d = c - _bodyL[i];
                float l2 = d.LengthSquared();
                if (l2 < 0.25f && float.IsFinite(l2))
                {
                    v = d * inv;
                    float s2 = v.LengthSquared();
                    if (s2 > MaxBodySpeed * MaxBodySpeed) v *= MaxBodySpeed / MathF.Sqrt(s2);
                    w = _boneTurn[bones[o]] * wts[o] + _boneTurn[bones[o + 1]] * wts[o + 1] + _boneTurn[bones[o + 2]] * wts[o + 2] + _boneTurn[bones[o + 3]] * wts[o + 3];
                    // its surface no faster than MaxBodySpeed either
                    float ws = w.Length() * (rad + thick);
                    if (!float.IsFinite(ws)) w = Vector3.Zero;
                    else if (ws > MaxBodySpeed) w *= MaxBodySpeed / ws;
                }
            }
            var lo = c - new Vector3(rad); var hi = c + new Vector3(rad);
            var back = -v * dt;
            lo = Vector3.Min(lo, lo + back); hi = Vector3.Max(hi, hi + back);
            if (lo.X > bmax.X || hi.X < bmin.X || lo.Y > bmax.Y || hi.Y < bmin.Y || lo.Z > bmax.Z || hi.Z < bmin.Z) continue;
            if (!Near(lo, hi)) continue;                  // 0.33.1: no piece of the cloth anywhere near it
            var sh = FleshCollider.Sphere(c, rad, body.Source);
            sh.Velocity = v;
            sh.Omega = w;
            AddShape(sh, dt, own);
            BodySpheres++;
        }
    }

    /// <summary>How fast (world rad/s) a bone turned going from <paramref name="last"/> to <paramref name="now"/> (bind to
    /// world, row vectors) in 1 / <paramref name="invDt"/> seconds; 0 when either is unusable.</summary>
    static Vector3 TurnRate(in Matrix4x4 last, in Matrix4x4 now, float invDt)
    {
        if (!Matrix4x4.Invert(last, out var li)) return Vector3.Zero;
        var t = li * now;                    // world to world: where a point on the bone a frame ago is now
        var r0 = new Vector3(t.M11, t.M12, t.M13); var r1 = new Vector3(t.M21, t.M22, t.M23); var r2 = new Vector3(t.M31, t.M32, t.M33);
        float l0 = r0.Length(), l1 = r1.Length(), l2 = r2.Length();
        if (!(l0 > 1e-6f && l1 > 1e-6f && l2 > 1e-6f) || !float.IsFinite(l0 + l1 + l2)) return Vector3.Zero;
        r0 /= l0; r1 /= l1; r2 /= l2;        // the turn alone (a bone that also grew)
        var q = Quaternion.CreateFromRotationMatrix(new Matrix4x4(r0.X, r0.Y, r0.Z, 0f, r1.X, r1.Y, r1.Z, 0f, r2.X, r2.Y, r2.Z, 0f, 0f, 0f, 0f, 1f));
        if (q.W < 0f) q = Quaternion.Negate(q);
        var axis = new Vector3(q.X, q.Y, q.Z);
        float sn = axis.Length();
        if (!(sn > 1e-9f) || !float.IsFinite(sn + q.W)) return Vector3.Zero;
        float angle = 2f * MathF.Atan2(sn, q.W);
        return axis * (angle / sn * invDt);
    }

    /// <summary>0.33.1: the way from <paramref name="from"/> to <paramref name="x"/> went through triangle c (two-sided:
    /// either way; front only: from the front) where it meets the plane, or within r of it: x goes back to the side it came
    /// from, r off the plane. Two-sided, the nearest side (where Resolve pushes a point to) is the wrong one for a point
    /// that went through.</summary>
    static bool CrossedTriangle(ref Vector3 x, Vector3 from, float r, in FleshCollider c)
    {
        float s0 = Vector3.Dot(from, c.Nrm) - c.D, s1 = Vector3.Dot(x, c.Nrm) - c.D;
        float side;
        if (s0 >= 0f) { if (!(s1 < 0f)) return false; side = 1f; }
        else { if (!c.TwoSided || !(s1 > 0f)) return false; side = -1f; }
        var at = from + (x - from) * (s0 / (s0 - s1));
        var cp = XpbdSolver.ClosestOnTriangle(at, c.A, c.B, c.C);
        if (!(Vector3.DistanceSquared(at, cp) < r * r)) return false;
        x -= c.Nrm * (s1 - side * r);
        return true;
    }

    int SolveContacts(float h, float a)
    {
        float r = Settings.Thickness, reach = 2f * r, mu = Settings.Friction;
        float grip = BodyGrip * mu * h * h;
        int touched = 0;
        for (int k = 0; k < _pairCount; k++)
        {
            int i = _pairP[k];
            int si = _pairS[k];
            ref readonly var c = ref _shapes[si];
            var p = X[i];
            bool tri = c.Kind == FleshColliderKind.Triangle;
            if (tri)
            {
                // 0.33.1: a triangle is tested against the point's whole way this step (from P), not only where it ends:
                // a point faster than about 14 m/s (1 cm thick) stepped over a thin one (a mesh floor) between two steps
                var q = P[i];
                var lo = Vector3.Min(p, q); var hi = Vector3.Max(p, q);
                if (hi.X < c.BMin.X - reach || lo.X > c.BMax.X + reach || hi.Y < c.BMin.Y - reach || lo.Y > c.BMax.Y + reach || hi.Z < c.BMin.Z - reach || lo.Z > c.BMax.Z + reach) continue;
            }
            else if (p.X < c.BMin.X - reach || p.X > c.BMax.X + reach || p.Y < c.BMin.Y - reach || p.Y > c.BMax.Y + reach || p.Z < c.BMin.Z - reach || p.Z > c.BMax.Z + reach) continue;
            // a moving collider is tested where it ends the frame: shift the point the other way by what is left of it
            var lag = c.Velocity * (_frameDt * (1f - a));
            var shifted = p + lag;
            bool hit;
            float keep = _shapeOwn[si] && c.Kind == FleshColliderKind.Sphere ? Vector3.Distance(_t1[i], c.A) - r : float.MaxValue;
            if (tri) hit = CrossedTriangle(ref shifted, P[i] + lag, r, c) || XpbdSolver.Resolve(ref shifted, r, c);
            else if (keep < c.R)
            {
                // 0.30.0: the wearer's own body pushes a point out no further than its animated place (at the end of the
                // frame, where the sphere is tested): the sphere shrinks to that place when the animation puts the cloth
                // inside it (a tight skirt over thick thighs), and does nothing when the place is deep inside
                if (!(keep > 1e-4f)) continue;
                var cs = c;
                cs.R = keep;
                hit = XpbdSolver.Resolve(ref shifted, r, cs);
            }
            else hit = XpbdSolver.Resolve(ref shifted, r, c);
            if (!hit) continue;
            var before = p;
            X[i] = shifted - lag;
            var push = X[i] - before;
            float depth = push.Length();
            if (!(depth > 1e-9f)) continue;
            var nrm = push / depth;
            // how fast the collider's surface moves where the point is (0.29.2: a turning body sphere's turn too)
            var cv = c.Velocity;
            if (c.Omega != Vector3.Zero) cv += Vector3.Cross(c.Omega, shifted - c.A);
            if (mu > 0f)
            {
                // Coulomb friction (position based): this step's motion along the surface, relative to the collider. 0.29.2:
                // a player's shape also grips (once per point and step): BodyGrip on top of what the point's weight gives,
                // holding it still, and slowing it by as much while it slides, so it catches again.
                var rel = (before - P[i]) - cv * h;
                var tang = rel - nrm * Vector3.Dot(rel, nrm);
                float tl = tang.Length();
                float g = c.Source != 0 && !_hit[i] ? grip : 0f;
                if (tl > 1e-12f) X[i] -= tl <= mu * depth + g ? tang : tang * ((0.75f * mu * depth + g) / tl);
            }
            if (!_hit[i]) { _hit[i] = true; _push[i] = Vector3.Zero; touched++; }
            _push[i] += nrm;
            _hitVel[i] = cv;
        }
        return touched;
    }

    // ------------------------------------------------------------------ strain and tears

    float MeasureStrain(bool tears)
    {
        float max = 0f;
        var s = Settings;
        float extra = s.TearAt - 1f, spread = s.TearSpread;
        for (int k = 0; k < _ns; k++)
        {
            int ia = _sA[k], ib = _sB[k];
            float l0 = _sL[k];
            if (!(l0 > 1e-9f)) continue;
            float strain = Vector3.Distance(X[ia], X[ib]) / l0;
            if (strain > max) max = strain;
            if (!tears) continue;
            float at = 1f + extra * (1f - spread * MathF.Max(_weak[ia], _weak[ib]));
            float excess = strain - at;
            if (!(excess > 0f)) continue;
            // the most strained few, most strained first
            int pos = TearCandidates.Count;
            while (pos > 0 && TearCandidates[pos - 1].excess < excess) pos--;
            if (pos >= ClothSettings.MaxTearsPerFrame) continue;
            TearCandidates.Insert(pos, (ia, ib, excess));
            if (TearCandidates.Count > ClothSettings.MaxTearsPerFrame) TearCandidates.RemoveAt(TearCandidates.Count - 1);
        }
        return max;
    }

    // ------------------------------------------------------------------ output (worker, after the step)

    /// <summary>
    /// The render mesh in the renderer's space: each render vertex at its particle, normals from the simulated triangles
    /// (turned around on the back of a sheet), tangents from each triangle's UV directions (when the mesh has them). With
    /// <paramref name="gain"/> below 1 the shape is blended back toward the mesh as modelled. Spans sized
    /// <see cref="ClothMesh.RenderVertexCount"/> (<paramref name="tan"/> may be empty).
    /// </summary>
    public void FillOutput(in Matrix4x4 worldToRenderer, float gain, Span<Vector3> pos, Span<Vector3> nrm, Span<Vector4> tan, out Vector3 min, out Vector3 max)
    {
        int n = Mesh.ParticleCount, nv = Mesh.RenderVertexCount;
        if (_local.Length < Mesh.MaxParticles) { _local = new Vector3[Mesh.MaxParticles]; _pn = new Vector3[Mesh.MaxParticles]; }
        var rest = Mesh.RestPositions;
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        bool blend = gain < 1f;
        for (int i = 0; i < n; i++)
        {
            Vector3 l;
            if (_avatar)
            {
                // 0.30.0, avatar cloth: blended toward (and, when not finite, put at) the animated place
                var x = blend ? _t1[i] + (X[i] - _t1[i]) * gain : X[i];
                l = Vector3.Transform(x, worldToRenderer);
                if (!float.IsFinite(l.X + l.Y + l.Z)) l = Vector3.Transform(_t1[i], worldToRenderer);
                if (!float.IsFinite(l.X + l.Y + l.Z)) l = rest[i];
            }
            else
            {
                l = Vector3.Transform(X[i], worldToRenderer);
                if (blend) l = rest[i] + (l - rest[i]) * gain;
                if (!float.IsFinite(l.X + l.Y + l.Z)) l = rest[i];
            }
            _local[i] = l;
            if (Mesh.FanSize(i) > 0) { min = Vector3.Min(min, l); max = Vector3.Max(max, l); }
            _pn[i] = Vector3.Zero;
        }
        if (min.X > max.X) { min = max = Vector3.Zero; }
        var tri = Mesh.Triangles;
        for (int t = 0; t < Mesh.TriangleCount; t++)
        {
            int ia = tri[3 * t], ib = tri[3 * t + 1], ic = tri[3 * t + 2];
            var c = Vector3.Cross(_local[ib] - _local[ia], _local[ic] - _local[ia]);
            _pn[ia] += c; _pn[ib] += c; _pn[ic] += c;
        }
        var rp = Mesh.RenderParticle; var sign = Mesh.RenderSign; var src = Mesh.RenderSource;
        for (int v = 0; v < nv; v++)
        {
            int p = rp[v];
            pos[v] = _local[p];
            var nn = _pn[p];
            float l = nn.Length();
            nrm[v] = l > 1e-20f ? nn * (sign[v] / l) : _restNrmBySource[src[v]];
        }
        if (_tanA == null || tan.Length < nv) return;
        if (_tacc.Length < nv) _tacc = new Vector3[Math.Max(nv, _tacc.Length * 2)];
        Array.Clear(_tacc, 0, nv);
        var rt = Mesh.RenderTriangles;
        for (int r = 0; r < Mesh.RenderTriangleCount; r++)
        {
            float ta = _tanA[r], tb = _tanB[r];
            if (ta == 0f && tb == 0f) continue;
            int v0 = rt[3 * r], v1 = rt[3 * r + 1], v2 = rt[3 * r + 2];
            var p0 = pos[v0];
            var tt = (pos[v1] - p0) * ta + (pos[v2] - p0) * tb;
            _tacc[v0] += tt; _tacc[v1] += tt; _tacc[v2] += tt;
        }
        for (int v = 0; v < nv; v++)
        {
            var nn = nrm[v];
            var rtan = _restTanBySource[src[v]];
            var t = _tacc[v];
            t -= nn * Vector3.Dot(t, nn);
            float l = t.Length();
            if (!(l > 1e-12f))
            {
                t = new Vector3(rtan.X, rtan.Y, rtan.Z);
                t -= nn * Vector3.Dot(t, nn);
                l = t.Length();
                if (!(l > 1e-12f)) { t = MathF.Abs(nn.X) < 0.9f ? Vector3.Cross(nn, Vector3.UnitX) : Vector3.Cross(nn, Vector3.UnitY); l = t.Length(); }
            }
            t /= l;
            tan[v] = new Vector4(t, rtan.W < 0f ? -1f : 1f);
        }
    }

    // ------------------------------------------------------------------ debug view

    /// <summary>World thread, between steps: copies what the debug view draws.</summary>
    public void FillDebug(ClothDebugSnapshot snap, bool colliders)
    {
        int n = Mesh.ParticleCount;
        if (snap.Points.Length < n) { snap.Points = new Vector3[Mesh.MaxParticles]; snap.Kinds = new byte[Mesh.MaxParticles]; }
        Array.Copy(X, snap.Points, n);
        Array.Copy(_kind, snap.Kinds, n);
        snap.PointCount = n;
        if (snap.EdgeA.Length < _ns) { snap.EdgeA = new int[_ns]; snap.EdgeB = new int[_ns]; snap.EdgeStrain = new float[_ns]; }
        Array.Copy(_sA, snap.EdgeA, _ns); Array.Copy(_sB, snap.EdgeB, _ns);
        for (int k = 0; k < _ns; k++)
        {
            float l0 = _sL[k];
            snap.EdgeStrain[k] = l0 > 1e-9f ? Vector3.Distance(X[_sA[k]], X[_sB[k]]) / l0 : 1f;
        }
        snap.EdgeCount = _ns;
        snap.TearAt = Settings.Tearable ? Settings.TearAt : 0f;
        snap.Thickness = Settings.Thickness;
        snap.Shapes.Clear();
        if (colliders) for (int i = 0; i < _shapeCount && i < 512; i++) snap.Shapes.Add(_shapes[i]);
        snap.Colliders = colliders;
    }
}
