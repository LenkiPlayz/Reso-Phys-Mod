using System.Numerics;

namespace Softbody.Core.Flesh;

public enum FleshColliderKind { Plane, Sphere, Capsule, Box, Cylinder, Cone, Triangle, Mesh }

/// <summary>
/// A collider the flesh is pushed out of, in world space.
/// <list type="bullet">
/// <item>Plane: A = a point, B = the normal.</item>
/// <item>Sphere: A, R.</item>
/// <item>Capsule: segment A..B, R.</item>
/// <item>Box: A = centre, B = half extents, Q = rotation.</item>
/// <item>Cylinder: cap centres A..B, R.</item>
/// <item>Cone: base centre A, tip B, base radius R.</item>
/// <item>Triangle: A, B, C (front = counter-clockwise from (B-A)x(C-A)); TwoSided or front only.</item>
/// <item>Mesh: a shared <see cref="TriangleBvh"/> in the mesh's space, M = mesh to world, MInv = world to mesh;
/// expanded into the triangles near the body at the start of each step.</item>
/// </list>
/// Source tells the debug view where it came from (0 = a world collider, 1 = another user's, 2 = the avatar's own).
/// Ignore (optional): a bit per solver vertex; set bits are points this collider never pushes (self-collision: the parts of
/// the shell the avatar's own collider already touches at rest).
/// </summary>
public struct FleshCollider
{
    public FleshColliderKind Kind;
    public Vector3 A, B, C;
    public float R;
    public Quaternion Q;
    public bool TwoSided;
    public byte Source;
    public ulong[] Ignore;
    /// <summary>0.11.4: how fast the collider moves (world m/s, translation). The solver sweeps it through the substeps
    /// (it arrives where it is given at the end of the frame) and uses it for friction.</summary>
    public Vector3 Velocity;
    public TriangleBvh Mesh;
    public Matrix4x4 M, MInv;
    /// <summary>World bounds, filled by the solver when the shape enters a step (a cheap first test for every pair).</summary>
    public Vector3 BMin, BMax;
    /// <summary>Triangle: the unit front normal and the plane offset (dot(p, Nrm) = D on the plane).</summary>
    public Vector3 Nrm;
    public float D;

    public static FleshCollider Plane(Vector3 point, Vector3 normal) => new() { Kind = FleshColliderKind.Plane, A = point, B = Vector3.Normalize(normal) };
    public static FleshCollider Sphere(Vector3 c, float r, byte source = 0) => new() { Kind = FleshColliderKind.Sphere, A = c, R = r, Source = source };
    public static FleshCollider Capsule(Vector3 a, Vector3 b, float r) => new() { Kind = FleshColliderKind.Capsule, A = a, B = b, R = r };
    public static FleshCollider Box(Vector3 center, Quaternion rotation, Vector3 half) => new() { Kind = FleshColliderKind.Box, A = center, B = half, Q = rotation };
    public static FleshCollider Cylinder(Vector3 a, Vector3 b, float r) => new() { Kind = FleshColliderKind.Cylinder, A = a, B = b, R = r };
    public static FleshCollider Cone(Vector3 baseCenter, Vector3 tip, float r) => new() { Kind = FleshColliderKind.Cone, A = baseCenter, B = tip, R = r };
    public static FleshCollider Triangle(Vector3 a, Vector3 b, Vector3 c, bool twoSided)
    {
        var t = new FleshCollider { Kind = FleshColliderKind.Triangle, A = a, B = b, C = c, TwoSided = twoSided };
        t.SetPlane();
        return t;
    }

    /// <summary>Triangle: recomputes the plane (after the corners changed).</summary>
    public void SetPlane()
    {
        var n = Vector3.Cross(B - A, C - A);
        float l = n.Length();
        Nrm = l > 1e-20f ? n / l : Vector3.Zero;
        D = Vector3.Dot(Nrm, A);
    }
    public static FleshCollider MeshShape(TriangleBvh mesh, Matrix4x4 meshToWorld, bool twoSided)
    {
        Matrix4x4.Invert(meshToWorld, out var inv);
        return new() { Kind = FleshColliderKind.Mesh, Mesh = mesh, M = meshToWorld, MInv = inv, TwoSided = twoSided };
    }

    /// <summary>World bounds of the shape (a mesh: of its whole tree; a plane: everything).</summary>
    public void Bounds(out Vector3 min, out Vector3 max)
    {
        switch (Kind)
        {
            case FleshColliderKind.Sphere: min = A - new Vector3(R); max = A + new Vector3(R); return;
            case FleshColliderKind.Capsule:
            case FleshColliderKind.Cylinder:
            case FleshColliderKind.Cone:
                min = Vector3.Min(A, B) - new Vector3(R); max = Vector3.Max(A, B) + new Vector3(R); return;
            case FleshColliderKind.Box:
            {
                var ax = Vector3.Abs(Vector3.Transform(new Vector3(B.X, 0, 0), Q)) + Vector3.Abs(Vector3.Transform(new Vector3(0, B.Y, 0), Q)) +
                         Vector3.Abs(Vector3.Transform(new Vector3(0, 0, B.Z), Q));
                min = A - ax; max = A + ax; return;
            }
            case FleshColliderKind.Triangle: min = Vector3.Min(A, Vector3.Min(B, C)); max = Vector3.Max(A, Vector3.Max(B, C)); return;
            case FleshColliderKind.Mesh: TransformBox(Mesh.BoundsMin, Mesh.BoundsMax, M, out min, out max); return;
            default: min = new Vector3(float.MinValue); max = new Vector3(float.MaxValue); return;
        }
    }

    /// <summary>The world-axis box around a box transformed by <paramref name="m"/>.</summary>
    public static void TransformBox(Vector3 lo, Vector3 hi, in Matrix4x4 m, out Vector3 min, out Vector3 max)
    {
        var c = Vector3.Transform(0.5f * (lo + hi), m);
        var e = 0.5f * (hi - lo);
        var ext = new Vector3(
            MathF.Abs(m.M11) * e.X + MathF.Abs(m.M21) * e.Y + MathF.Abs(m.M31) * e.Z,
            MathF.Abs(m.M12) * e.X + MathF.Abs(m.M22) * e.Y + MathF.Abs(m.M32) * e.Z,
            MathF.Abs(m.M13) * e.X + MathF.Abs(m.M23) * e.Y + MathF.Abs(m.M33) * e.Z);
        min = c - ext; max = c + ext;
    }
}

/// <summary>
/// Collisions of the skin points with world shapes. Once per step (on the worker, not the world thread), the shapes near the
/// body are found with bounding boxes: mesh colliders are cut down to their triangles near the body through their cached
/// tree, a grid over the skin points pairs each shape with the points inside its bounds (plus the furthest any point can
/// move this frame), and every substep then only resolves those pairs.
/// </summary>
public sealed partial class XpbdSolver
{
    FleshCollider[] _shapes = Array.Empty<FleshCollider>();
    int _shapeCount;
    int[] _pairPoint = Array.Empty<int>(), _pairShape = Array.Empty<int>();
    int _pairCount;
    readonly List<int> _triHits = new();
    long[] _cellKey = Array.Empty<long>();
    int[] _cellPoint = Array.Empty<int>();
    int _cellCount;
    bool[] _touched = Array.Empty<bool>();
    /// <summary>Shapes near the body in the last step (mesh colliders expanded to their nearby triangles), for the debug view.</summary>
    public ReadOnlySpan<FleshCollider> NearShapes => _shapes.AsSpan(0, _shapeCount);

    public int PairCount => _pairCount;
    /// <summary>Triangles taken from mesh colliders in the last step (at most <see cref="MaxMeshTriangles"/>).</summary>
    public int MeshTriangles { get; private set; }
    public const int MaxMeshTriangles = 20000;
    /// <summary>Tests only: pair every colliding point with every shape near the body (what the grid culling must reproduce).</summary>
    public bool BruteForceCollisions;

    void AddShape(in FleshCollider c)
    {
        if (_shapeCount == _shapes.Length) Array.Resize(ref _shapes, Math.Max(16, _shapes.Length * 2));
        ref var sh = ref _shapes[_shapeCount++];
        sh = c;
        sh.Bounds(out sh.BMin, out sh.BMax);
        if (sh.Velocity != Vector3.Zero)
        {
            var back = -sh.Velocity * _frameDt;
            sh.BMin = Vector3.Min(sh.BMin, sh.BMin + back); sh.BMax = Vector3.Max(sh.BMax, sh.BMax + back);
        }
    }

    void AddPair(int point, int shape)
    {
        if (_pairCount == _pairPoint.Length)
        {
            int n = Math.Max(64, _pairPoint.Length * 2);
            Array.Resize(ref _pairPoint, n); Array.Resize(ref _pairShape, n);
        }
        _pairPoint[_pairCount] = point; _pairShape[_pairCount] = shape; _pairCount++;
    }

    static long CellKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

    float[] _margin = Array.Empty<float>();
    readonly Dictionary<int, int> _triShape = new();
    int _gx, _gy, _gz;
    float _cell;
    Vector3 _gridLo;

    /// <summary>
    /// Finds the shapes near the body and pairs them with the points that can reach them this frame. A point's reach is two
    /// radii (a front-only triangle catches points up to two radii behind it) plus how far it can travel this frame.
    /// Primitives are paired through a grid over the points; mesh colliders through their triangle tree, one small box per
    /// point, each triangle turned into a world-space shape once. Every substep then first checks a pair's bounds against
    /// where the point is now, so most of them cost a few comparisons. For every point its pairs come in collider order,
    /// then triangle order, so the result does not depend on the culling (<see cref="BruteForceCollisions"/> checks that).
    /// </summary>
    float _frameDt;

    void BuildPairs(float dt, Vector3[] t0, Vector3[] t1)
    {
        _frameDt = dt;
        _shapeCount = 0; _pairCount = 0; MeshTriangles = 0;
        if (Colliders.Count == 0) return;
        if (_margin.Length < N) _margin = new float[N];
        float mMax = 0f;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        int pts = 0;
        for (int i = 0; i < N; i++)
        {
            float r = Radius[i];
            if (!(r > 0f) || _invMass[i] == 0f) continue;
            // the point moves with its own velocity and is pulled along with the animation: whichever is larger, with room
            float m = 2f * r + MathF.Max(1.5f * V[i].Length() * dt, 1.25f * Vector3.Distance(t0[i], t1[i])) + 2e-3f;
            _margin[i] = m;
            mMax = MathF.Max(mMax, m);
            lo = Vector3.Min(lo, X[i]); hi = Vector3.Max(hi, X[i]);
            pts++;
        }
        if (pts == 0 || !float.IsFinite(lo.X + hi.X + lo.Y + hi.Y + lo.Z + hi.Z + mMax)) return;
        var bmin = lo - new Vector3(mMax); var bmax = hi + new Vector3(mMax);
        bool grid = false;

        for (int ci = 0; ci < Colliders.Count; ci++)
        {
            var c = Colliders[ci];
            c.Bounds(out var cmin, out var cmax);
            if (c.Velocity != Vector3.Zero) { var back = -c.Velocity * dt; cmin = Vector3.Min(cmin, cmin + back); cmax = Vector3.Max(cmax, cmax + back); }
            if (cmin.X > bmax.X || cmax.X < bmin.X || cmin.Y > bmax.Y || cmax.Y < bmin.Y || cmin.Z > bmax.Z || cmax.Z < bmin.Z) continue;
            if (c.Kind != FleshColliderKind.Mesh)
            {
                int sh = _shapeCount;
                AddShape(c);
                if (BruteForceCollisions) { PairAll(sh); continue; }
                if (!grid) { BuildGrid(lo, hi, MathF.Max(2f * mMax, 1e-3f), pts); grid = true; }
                PairPrimitive(sh, cmin - new Vector3(mMax), cmax + new Vector3(mMax));
                continue;
            }
            // a mesh collider: its triangles near each point, through the tree
            _triShape.Clear();
            bool flip = c.M.GetDeterminant() < 0f;
            if (BruteForceCollisions)
            {
                FleshCollider.TransformBox(bmin, bmax, c.MInv, out var qmin, out var qmax);
                _triHits.Clear(); c.Mesh.Query(qmin, qmax, _triHits); _triHits.Sort();
                foreach (int t in _triHits) PairAll(AddTriangle(c, t, flip));
                continue;
            }
            for (int i = 0; i < N; i++)
            {
                if (!(Radius[i] > 0f) || _invMass[i] == 0f || Ignored(c.Ignore, i)) continue;
                float m = _margin[i]; var p = X[i];
                if (p.X < cmin.X - m || p.X > cmax.X + m || p.Y < cmin.Y - m || p.Y > cmax.Y + m || p.Z < cmin.Z - m || p.Z > cmax.Z + m) continue;
                FleshCollider.TransformBox(p - new Vector3(m), p + new Vector3(m), c.MInv, out var qmin, out var qmax);
                _triHits.Clear(); c.Mesh.Query(qmin, qmax, _triHits);
                if (_triHits.Count == 0) continue;
                if (_triHits.Count > 1) _triHits.Sort();
                foreach (int t in _triHits)
                {
                    if (!_triShape.TryGetValue(t, out int sh))
                    {
                        if (MeshTriangles >= MaxMeshTriangles) continue;
                        sh = AddTriangle(c, t, flip);
                        _triShape[t] = sh;
                    }
                    // the triangle's own bounds, against this point's reach (the tree's box was the mesh-space one)
                    ref readonly var tri = ref _shapes[sh];
                    if (p.X < tri.BMin.X - m || p.X > tri.BMax.X + m || p.Y < tri.BMin.Y - m || p.Y > tri.BMax.Y + m || p.Z < tri.BMin.Z - m || p.Z > tri.BMax.Z + m) continue;
                    AddPair(i, sh);
                }
            }
        }
    }

    int AddTriangle(in FleshCollider c, int t, bool flip)
    {
        var tri = FleshCollider.Triangle(Vector3.Transform(c.Mesh.A[t], c.M), Vector3.Transform(c.Mesh.B[t], c.M), Vector3.Transform(c.Mesh.C[t], c.M), c.TwoSided);
        tri.Source = c.Source;
        tri.Ignore = c.Ignore;
        tri.Velocity = c.Velocity;
        // a mirrored transform flips the winding: keep the front where the mesh says it is
        if (flip) { (tri.B, tri.C) = (tri.C, tri.B); tri.SetPlane(); }
        AddShape(tri);
        MeshTriangles++;
        return _shapeCount - 1;
    }

    void PairAll(int sh)
    {
        var ign = _shapes[sh].Ignore;
        for (int i = 0; i < N; i++)
            if (Radius[i] > 0f && _invMass[i] != 0f && !Ignored(ign, i)) AddPair(i, sh);
    }

    internal static bool Ignored(ulong[] ignore, int i) => ignore != null && (i >> 6) < ignore.Length && ((ignore[i >> 6] >> (i & 63)) & 1UL) != 0;

    /// <summary>A grid over the colliding points: their cell keys, sorted, so a shape looks only at the cells it touches.</summary>
    void BuildGrid(Vector3 lo, Vector3 hi, float cell, int pts)
    {
        if (_cellKey.Length < pts) { _cellKey = new long[pts]; _cellPoint = new int[pts]; }
        _cellCount = 0; _cell = cell; _gridLo = lo;
        for (int i = 0; i < N; i++)
        {
            if (!(Radius[i] > 0f) || _invMass[i] == 0f) continue;
            var p = (X[i] - lo) / cell;
            _cellKey[_cellCount] = CellKey((int)p.X, (int)p.Y, (int)p.Z);
            _cellPoint[_cellCount] = i;
            _cellCount++;
        }
        Array.Sort(_cellKey, _cellPoint, 0, _cellCount);
        _gx = (int)((hi.X - lo.X) / cell) + 1; _gy = (int)((hi.Y - lo.Y) / cell) + 1; _gz = (int)((hi.Z - lo.Z) / cell) + 1;
    }

    /// <summary>Pairs shape <paramref name="sh"/> with the points whose reach overlaps its bounds (smin..smax already grown by the largest reach).</summary>
    void PairPrimitive(int sh, Vector3 smin, Vector3 smax)
    {
        var bmin = _shapes[sh].BMin; var bmax = _shapes[sh].BMax; var ign = _shapes[sh].Ignore;
        float cell = _cell; var lo = _gridLo;
        int x0 = Math.Max(0, (int)MathF.Floor((smin.X - lo.X) / cell)), x1 = Math.Min(_gx - 1, (int)MathF.Floor((smax.X - lo.X) / cell));
        int y0 = Math.Max(0, (int)MathF.Floor((smin.Y - lo.Y) / cell)), y1 = Math.Min(_gy - 1, (int)MathF.Floor((smax.Y - lo.Y) / cell));
        int z0 = Math.Max(0, (int)MathF.Floor((smin.Z - lo.Z) / cell)), z1 = Math.Min(_gz - 1, (int)MathF.Floor((smax.Z - lo.Z) / cell));
        if (x0 > x1 || y0 > y1 || z0 > z1) return;
        long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        if (cells > _cellCount)
        {
            // a shape larger than the body (a floor, a wall): every point, in point order
            for (int i = 0; i < N; i++)
                if (Radius[i] > 0f && _invMass[i] != 0f && Reaches(i, bmin, bmax, ign)) AddPair(i, sh);
            return;
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
                        if (Reaches(_cellPoint[k], bmin, bmax, ign)) AddPair(_cellPoint[k], sh);
                }
    }

    bool Reaches(int i, Vector3 bmin, Vector3 bmax, ulong[] ignore)
    {
        if (Ignored(ignore, i)) return false;
        float m = _margin[i]; var p = X[i];
        return p.X >= bmin.X - m && p.X <= bmax.X + m && p.Y >= bmin.Y - m && p.Y <= bmax.Y + m && p.Z >= bmin.Z - m && p.Z <= bmax.Z + m;
    }

    /// <summary>Coulomb friction of the skin against colliders (0 = frictionless). Static up to this ratio of tangential to
    /// normal correction, sliding above it at 0.75 of it.</summary>
    public float Friction = 0.8f;
    /// <summary>Also turn contact pushes into the body (see <see cref="SquishInward"/>).</summary>
    public bool InwardPush = true;
    /// <summary>
    /// 0.11.5, per vertex: the skin's outward normal where it touches a collider (the inverse of the collider's normal there:
    /// skin pressed against something takes its shape), and how much it is in contact: 1 while touching, fading over about
    /// 0.1 s after (so a released dent does not snap back to the other shading). The rig blends the render normals toward it.
    /// </summary>
    public Vector3[] ContactNormal = Array.Empty<Vector3>();
    public float[] ContactWeight = Array.Empty<float>();
    Vector3[] _contactSum = Array.Empty<Vector3>();
    bool[] _contactHit = Array.Empty<bool>();
    const float ContactFadeSeconds = 0.08f;

    /// <summary>0.12.1, per vertex: the last two distinct shapes (indices into <see cref="NearShapes"/>) it touched this step,
    /// -1 for none. Valid until the next step.</summary>
    public int[] ContactShapeA = Array.Empty<int>(), ContactShapeB = Array.Empty<int>();
    /// <summary>0.12.1: a skin point inside a box leaves through the face toward the body (see <see cref="Resolve(ref Vector3, float, in FleshCollider, Vector3)"/>).</summary>
    public bool BoxExitInward = true;

    void BeginContacts()
    {
        if (ContactNormal.Length != N)
        {
            ContactNormal = new Vector3[N]; ContactWeight = new float[N]; _contactSum = new Vector3[N]; _contactHit = new bool[N];
            ContactShapeA = new int[N]; ContactShapeB = new int[N];
        }
        Array.Clear(_contactSum); Array.Clear(_contactHit);
        Array.Fill(ContactShapeA, -1); Array.Fill(ContactShapeB, -1);
    }

    void EndContacts(float dt)
    {
        float fade = MathF.Exp(-dt / ContactFadeSeconds);
        for (int i = 0; i < N; i++)
        {
            if (!_contactHit[i]) { ContactWeight[i] *= fade; continue; }
            float l = _contactSum[i].Length();
            if (!(l > 1e-12f)) continue;
            var n = _contactSum[i] / l;
            // half way from last frame's, so a point sliding over an edge between two faces does not flicker
            if (ContactWeight[i] > 0.01f)
            {
                var m = ContactNormal[i] + n;
                float ml = m.Length();
                n = ml > 1e-6f ? m / ml : n;
            }
            ContactNormal[i] = n;
            ContactWeight[i] = 1f;
        }
    }

    /// <summary>Forgets all contacts (a reset).</summary>
    public void ClearContacts() { if (ContactWeight.Length > 0) Array.Clear(ContactWeight); }

    /// <summary>Scales how deep below its animated place a contact may push a skin point (1 = the limit the rig sets).</summary>
    public float DepthScale = 1f;

    /// <summary>0.26.4: a collider never holds a skin point out of the body (see <see cref="KeepInBody"/>).</summary>
    public bool KeepIn = true;
    /// <summary>0.26.4: how far beyond its animated place, out of the body, a collider may still push a skin point, in
    /// collision radii.</summary>
    public float OutwardSlack = 0.5f;
    /// <summary>Skin points let go in the last substep because a collider would have pulled them out of the body.</summary>
    public int LetGo { get; private set; }
    bool[] _letGo = Array.Empty<bool>();

    /// <summary>
    /// 0.26.6: contacts do not fling. A push moves a point out of a collider, and the velocity update then turns that jump
    /// into speed: a point pushed a centimetre in one substep left at several metres a second (Lenki: after sudden moves
    /// and hits a few vertices flew around for a quarter of a second; the logs had skin points up to 0.88 m off). So the
    /// part of the contacts' displacement along their push is left out of the velocity, and a point still heading into the
    /// collider then moves with it along the push (an inelastic contact). Along the surface, friction's drag stays.
    /// </summary>
    public bool NoBounce = true;
    Vector3[] _bouncePush = Array.Empty<Vector3>(), _bounceVel = Array.Empty<Vector3>(), _preContact = Array.Empty<Vector3>();
    bool[] _bounced = Array.Empty<bool>(), _hasPre = Array.Empty<bool>();
    /// <summary>Points whose contact push was kept out of their velocity in the last step (a diagnostic).</summary>
    public int Unbounced { get; private set; }

    /// <summary>Where point i was before its first contact this substep (for <see cref="NoBounce"/>).</summary>
    void NotePreContact(int i, Vector3 before)
    {
        if (!NoBounce) return;
        if (_hasPre.Length != N) { _hasPre = new bool[N]; _preContact = new Vector3[N]; _bounced = new bool[N]; _bouncePush = new Vector3[N]; _bounceVel = new Vector3[N]; }
        if (_hasPre[i]) return;
        _hasPre[i] = true; _preContact[i] = before;
    }

    /// <summary>Records an accepted contact's push for <see cref="NoBounce"/>.</summary>
    void NoteBounce(int i, Vector3 pushed, Vector3 colliderVelocity)
    {
        if (!NoBounce || _bounced.Length != N) return;
        if (!_bounced[i]) { _bounced[i] = true; _bouncePush[i] = Vector3.Zero; }
        _bouncePush[i] += pushed;
        _bounceVel[i] = colliderVelocity;
    }

    /// <summary>The velocity point i leaves this substep with: never faster along its contacts' push than the collider
    /// moves along it, unless it was that fast without this substep's contact displacement. A point the depth limit held
    /// back, or one a retreating collider let go of, keeps its own speed (an earlier try gave every pushed point the
    /// collider's speed, which dragged skin out after a hand pulling away and drove held points deeper).</summary>
    Vector3 Unbounce(int i, Vector3 v, float h)
    {
        if (_hasPre.Length != N || !_hasPre[i]) return v;
        _hasPre[i] = false;
        if (!_bounced[i]) return v;
        _bounced[i] = false;
        float l = _bouncePush[i].Length();
        if (!(l > 1e-9f)) return v;
        var n = _bouncePush[i] / l;
        float vn = Vector3.Dot(v, n), cn = Vector3.Dot(_bounceVel[i], n);
        if (!(vn > cn)) return v;
        float free = vn - Vector3.Dot(X[i] - _preContact[i], n) / h;
        float keep = MathF.Max(cn, free);
        if (!(keep < vn)) return v;
        _unbouncedThisStep++;
        return v - n * (vn - keep);
    }
    int _unbouncedThisStep;

    /// <summary>One pass over the pairs, at substep fraction <paramref name="a"/> (0..1) of a substep of length
    /// <paramref name="h"/>; returns how many points were pushed.</summary>
    int SolveContacts(Vector3[] target, float a, float h)
    {
        if (_touched.Length < N) _touched = new bool[N];
        if (_letGo.Length < N) _letGo = new bool[N];
        int touched = 0, letGo = 0;
        for (int k = 0; k < _pairCount; k++)
        {
            int i = _pairPoint[k];
            ref readonly var c = ref _shapes[_pairShape[k]];
            float r = Radius[i], reach = 2f * r;
            var p = X[i];
            if (p.X < c.BMin.X - reach || p.X > c.BMax.X + reach || p.Y < c.BMin.Y - reach || p.Y > c.BMax.Y + reach || p.Z < c.BMin.Z - reach || p.Z > c.BMax.Z + reach) continue;
            var before = X[i];
            // a moving collider is where it was at the start of the frame plus the part of the frame done so far: test the
            // point in the collider's frame-end position by shifting it the other way
            var lag = c.Velocity * (_frameDt * (1f - a));
            var shifted = X[i] + lag;
            if (!Resolve(ref shifted, r, c, BoxExitInward ? Inward[i] : Vector3.Zero)) continue;
            NotePreContact(i, before);
            X[i] = shifted - lag;
            var pushed = X[i] - before;
            if (Friction > 0f) ApplyFriction(i, before, c.Velocity * h);
            if (Squish)
            {
                if (InwardPush) SquishInward(i, before, target);
                // never deeper than the shell: a collider pressing further goes through the skin there. The point only
                // goes in as far as the shell allows and is not pushed sideways at all (pushed out of a ball it cannot
                // escape downward, it would be shoved around the ball every substep, plowing and folding the skin)
                var d = Inward[i];
                float lim = InwardLimit[i] * DepthScale;
                if (d != Vector3.Zero && Vector3.Dot(X[i] - target[i], d) > lim)
                {
                    float was = Vector3.Dot(before - target[i], d);
                    X[i] = before + d * MathF.Max(0f, lim - was);
                }
            }
            if (KeepIn && KeepInBody(i, before, pushed, lag, target[i], c))
            {
                // let go: no contact to shade or count
                if (!_letGo[i]) { _letGo[i] = true; letGo++; }
                continue;
            }
            float pl = pushed.Length();
            if (pl > 1e-9f)
            {
                _contactSum[i] -= pushed / pl; _contactHit[i] = true;
                int sh = _pairShape[k];
                if (ContactShapeA[i] != sh && ContactShapeB[i] != sh) { ContactShapeB[i] = ContactShapeA[i]; ContactShapeA[i] = sh; }
                NoteBounce(i, pushed, c.Velocity);
            }
            if (!_touched[i]) { _touched[i] = true; touched++; }
        }
        for (int k = 0; k < _pairCount; k++) { _touched[_pairPoint[k]] = false; _letGo[_pairPoint[k]] = false; }
        LetGo = letGo;
        return touched;
    }

    /// <summary>
    /// 0.26.4, let go of skin points a collider holds on the wrong side. A collider can only push a skin point out of the
    /// body, or shove it far along the skin, when it is inside the body there: the body's animated place went through it
    /// (a wall walked into and back out of, a seat sat through), or it went through the skin (a box pressed past the
    /// shell's limit, then pulled back out). A thin box is a wall from both sides, so those points stayed on the old side,
    /// pushed back there every substep, and the flesh stretched away from the mesh (offline: 7 to 15 cm, still there a
    /// second later) until a move fast enough to jump a point past the box in one substep. Contacts that press into the
    /// body (their push within 60 degrees of the skin's inward direction) keep the depth limit's rules; for the others:
    /// <list type="bullet">
    /// <item>out of the body: a contact that leaves point <paramref name="i"/> further out than <see cref="OutwardSlack"/>
    /// radii beyond its animated place loses the part that took it out there, and, when it pushed mostly outward, its part
    /// along the skin too (as the depth limit does: the point passes through rather than being shoved around inside);</item>
    /// <item>across: the point's animated place is more than a radius behind the surface the point was pushed out of, and
    /// outside the collider (see <see cref="Across"/>): the collider is between them, so no push at all.</item>
    /// </list>
    /// <paramref name="pushed"/> is the collider's own push (before friction), <paramref name="lag"/> how far the collider
    /// still moves this frame (its surface is tested where it ends the frame, like the target). True when it let go.
    /// </summary>
    bool KeepInBody(int i, Vector3 before, Vector3 pushed, Vector3 lag, Vector3 target, in FleshCollider c)
    {
        var d = Inward[i];
        if (d == Vector3.Zero) return false;
        float pl = pushed.Length();
        if (!(pl > 1e-9f)) return false;
        var nc = pushed / pl;
        if (Vector3.Dot(nc, d) >= 0.5f) return false;        // pressing into the body: a dent
        float r = Radius[i];
        // 0.26.6: the avatar's own bone colliders do not plow. They move with the body, so they reach where the skin
        // belongs only in real self-contact (an arm pressed against the side); a sideways or outward push on skin whose
        // animated place they do not reach only happens where the flesh lags behind the body after a sudden move, and
        // there it wedged points between two bone capsules (offline, after a 0.5 m snap: a point held 9 cm off between
        // the Hips and a leg capsule for good). Pressing into the body is still a dent, as above.
        if (OwnNoPlow && c.Source == 2)
        {
            var at = target;
            if (!Resolve(ref at, r, c)) { X[i] = before; return true; }
        }
        // out of the body: how far out of its animated place (along the outward normal, -d), before and after this contact
        float was = Vector3.Dot(target - before, d), now = Vector3.Dot(target - X[i], d);
        float slack = OutwardSlack * r;
        if (LetGoOutward && now > slack && now > was)
        {
            var move = X[i] - before;
            float ml = move.Length();
            if (ml > 1e-12f)
            {
                float outward = now - was;
                var along = move + d * outward;                   // the part along the skin
                float t = Math.Clamp((outward / ml - 0.3f) / 0.4f, 0f, 1f);
                float alongKept = 1f - t * t * (3f - 2f * t);     // all of it below 0.3 of the move outward, none above 0.7
                X[i] = before - d * MathF.Max(0f, slack - was) + along * alongKept;
                return true;
            }
        }
        // across: the surface the point was pushed out of, where the collider ends the frame, against the animated place
        if (!LetGoAcross) return false;
        var surface = before + pushed + lag - nc * r;
        if (!(-Vector3.Dot(target - surface, nc) > r)) return false;
        if (!Across(target, c)) return false;
        X[i] = before;
        return true;
    }

    /// <summary>Tests only: each of <see cref="KeepInBody"/>'s two rules on its own.</summary>
    internal bool LetGoOutward = true, LetGoAcross = true;
    /// <summary>0.26.6: the avatar's own bone colliders (Source 2) press the flesh in, but push it along or out of the body
    /// only where they reach the skin's animated place (see <see cref="KeepInBody"/>).</summary>
    public bool OwnNoPlow = true;

    /// <summary>
    /// Whether an animated place <paramref name="p"/> already found behind the pushing surface means the collider is
    /// between it and the point: outside a solid (behind its far side or around it), or straight behind a two-sided
    /// triangle (a sheet). Inside a solid is an ordinary contact, however deep (a pressed ball pushes the skin around its
    /// rim sideways; past the depth limit the dent rules already let it through the skin). A plane's back and a mesh's
    /// front-only face are the inside of a solid too: a front-only face already lets through a point more than two radii
    /// behind it, and each face's own plane is no measure of the solid behind a bumpy mesh.
    /// </summary>
    static bool Across(Vector3 p, in FleshCollider c)
    {
        switch (c.Kind)
        {
            case FleshColliderKind.Plane: return false;
            case FleshColliderKind.Triangle:
            {
                if (!c.TwoSided) return false;
                var onPlane = p - c.Nrm * (Vector3.Dot(p, c.Nrm) - c.D);
                return Vector3.DistanceSquared(ClosestOnTriangle(onPlane, c.A, c.B, c.C), onPlane) < 1e-10f;
            }
            default:
            {
                var y = p;
                return !Resolve(ref y, 0f, c);
            }
        }
    }

    /// <summary>
    /// 0.11.4: a collider pushes a skin point out along its own surface normal, which near the rim of a pressing ball is
    /// mostly sideways: the points slid around the ball, bunched up at its edge and folded the skin between them. Real flesh
    /// is pushed in. So the push is turned into the body, along the skin's animated inward direction, as far as it takes to
    /// clear the collider's surface there (depth / cos of the angle between the two: on a convex shape that always clears
    /// it). It keeps the collider's own push when the inward direction would not clear it (a collider beside the skin rather
    /// than against it) or would take the point deeper than the shell (0.8 of its thickness below its animated place).
    /// </summary>
    /// <summary>
    /// 0.11.4, Coulomb friction (position based, as in Macklin et al. 2014): the point's motion this substep relative to the
    /// collider's, minus its part along the contact normal, is cancelled while it is small next to the push (static: the
    /// skin grips the collider and moves with it, so a ball pressing in drags the skin down with it instead of shoving it
    /// sideways), and reduced by 0.75 of the friction ratio times the push when larger (sliding).
    /// </summary>
    void ApplyFriction(int i, Vector3 before, Vector3 colliderStep)
    {
        var push = X[i] - before;
        float depth = push.Length();
        if (!(depth > 1e-9f)) return;
        var n = push / depth;
        var rel = (before - _prev[i]) - colliderStep;
        var tang = rel - n * Vector3.Dot(rel, n);
        float t = tang.Length();
        if (!(t > 1e-12f)) return;
        if (t <= Friction * depth) X[i] -= tang;
        else X[i] -= tang * (0.75f * Friction * depth / t);
    }

    void SquishInward(int i, Vector3 before, Vector3[] target)
    {
        var d = Inward[i];
        if (d == Vector3.Zero) return;
        var push = X[i] - before;
        float depth = push.Length();
        if (!(depth > 1e-9f)) return;
        var nc = push / depth;
        float cos = Vector3.Dot(d, nc);
        // 0.11.5: a smooth blend of the collider's push and the skin's inward direction instead of a switch at 70 degrees:
        // mostly inward where the skin faces the collider, the collider's own push where it grazes it
        float t = Math.Clamp((cos - 0.1f) / 0.5f, 0f, 1f);
        float beta = 0.9f * t * t * (3f - 2f * t);
        if (!(beta > 0f)) return;
        var dir = Vector3.Normalize(nc * (1f - beta) + d * beta);
        float along = Vector3.Dot(dir, nc);
        if (along < 0.2f) return;
        var xs = before + dir * (depth / along);
        if (Vector3.Dot(xs - target[i], d) > InwardLimit[i] * DepthScale) return;
        X[i] = xs;
    }

    /// <summary>Moves a sphere of radius r at x out of the shape. True when it was inside.</summary>
    public static bool Resolve(ref Vector3 x, float r, in FleshCollider c) => Resolve(ref x, r, c, Vector3.Zero);

    /// <summary>The same, with the direction a point inside a box should rather leave by (0.12.1: the skin's inward
    /// direction, so a skin point inside a box leaves through the face toward the body, not a side face near an edge).</summary>
    public static bool Resolve(ref Vector3 x, float r, in FleshCollider c, Vector3 prefer)
    {
        switch (c.Kind)
        {
            case FleshColliderKind.Plane:
            {
                float d = Vector3.Dot(x - c.A, c.B) - r;
                if (d < 0f) { x -= c.B * d; return true; }
                return false;
            }
            case FleshColliderKind.Sphere: return PushOut(ref x, c.A, c.R + r);
            case FleshColliderKind.Capsule: return PushOut(ref x, TetShell.ClosestOnSegment(c.A, c.B, x), c.R + r);
            case FleshColliderKind.Box: return PushOutOfBox(ref x, c.A, c.Q, c.B, r, prefer);
            case FleshColliderKind.Cylinder: return PushOutOfCylinder(ref x, c.A, c.B, c.R, r);
            case FleshColliderKind.Cone: return PushOutOfCone(ref x, c.A, c.B, c.R, r);
            case FleshColliderKind.Triangle:
            {
                // the plane first: clearly in front, or behind beyond reach, means no contact
                float s = Vector3.Dot(x, c.Nrm) - c.D;
                if (s >= r || s <= (c.TwoSided ? -r : -2f * r)) return false;
                return PushOutOfTriangle(ref x, c.A, c.B, c.C, c.TwoSided, r);
            }
            default: return false;
        }
    }

    static bool PushOut(ref Vector3 x, Vector3 c, float r)
    {
        var d = x - c; float l2 = d.LengthSquared();
        if (!(l2 < r * r)) return false;
        float l = MathF.Sqrt(l2);
        x = l > 1e-9f ? c + d * (r / l) : c + Vector3.UnitY * r;
        return true;
    }

    /// <summary>A sphere of radius r at x out of an oriented box (also from inside: through the nearest face).</summary>
    public static bool PushOutOfBox(ref Vector3 x, Vector3 center, Quaternion q, Vector3 half, float r, Vector3 prefer = default)
    {
        var local = Vector3.Transform(x - center, Quaternion.Conjugate(q));
        if (MathF.Abs(local.X) >= half.X + r || MathF.Abs(local.Y) >= half.Y + r || MathF.Abs(local.Z) >= half.Z + r) return false;
        var clamped = Vector3.Clamp(local, -half, half);
        var d = local - clamped;
        float l2 = d.LengthSquared();
        if (l2 > 1e-18f)
        {
            if (!(l2 < r * r)) return false;
            local = clamped + d * (r / MathF.Sqrt(l2));
        }
        else
        {
            // inside: through the nearest face, or through the face the preferred direction points out of when that is
            // nearly as near (within 2 radii: a point just inside an edge leaves toward the body rather than sideways, but
            // a point deep inside the box is never carried through it)
            float px = half.X - MathF.Abs(local.X), py = half.Y - MathF.Abs(local.Y), pz = half.Z - MathF.Abs(local.Z);
            int axis = px <= py && px <= pz ? 0 : py <= pz ? 1 : 2;
            float side = (axis == 0 ? local.X : axis == 1 ? local.Y : local.Z) < 0 ? -1f : 1f;
            if (prefer != Vector3.Zero)
            {
                var pl = Vector3.Transform(prefer, Quaternion.Conjugate(q));
                float ax = MathF.Abs(pl.X), ay = MathF.Abs(pl.Y), az = MathF.Abs(pl.Z);
                int pa = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
                float pv = pa == 0 ? pl.X : pa == 1 ? pl.Y : pl.Z;
                if (MathF.Abs(pv) > 0.5f)
                {
                    float ps = pv < 0 ? -1f : 1f;
                    float exit = (pa == 0 ? half.X - ps * local.X : pa == 1 ? half.Y - ps * local.Y : half.Z - ps * local.Z);
                    if (exit <= MathF.Min(px, MathF.Min(py, pz)) + 2f * r) { axis = pa; side = ps; }
                }
            }
            if (axis == 0) local.X = side * (half.X + r);
            else if (axis == 1) local.Y = side * (half.Y + r);
            else local.Z = side * (half.Z + r);
        }
        x = center + Vector3.Transform(local, q);
        return true;
    }

    /// <summary>Axis frame of x around the segment a..b: height along it from a, distance from it, and the direction out.</summary>
    static void AxisFrame(Vector3 x, Vector3 a, Vector3 b, out Vector3 u, out float len, out float h, out float rho, out Vector3 dir)
    {
        var ab = b - a; len = ab.Length();
        u = len > 1e-9f ? ab / len : Vector3.UnitY;
        var d = x - a;
        h = Vector3.Dot(d, u);
        var radial = d - u * h;
        rho = radial.Length();
        if (rho > 1e-9f) dir = radial / rho;
        else { var t = MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY; dir = Vector3.Normalize(Vector3.Cross(u, t)); }
    }

    /// <summary>A sphere out of a solid cylinder with cap centres a, b and radius R.</summary>
    public static bool PushOutOfCylinder(ref Vector3 x, Vector3 a, Vector3 b, float R, float r)
    {
        AxisFrame(x, a, b, out var u, out float len, out float h, out float rho, out var dir);
        // the cross-section (rho, h) against the rectangle [0, R] x [0, len]
        float ch = Math.Clamp(h, 0f, len), cr = MathF.Min(rho, R);
        bool inside = h > 0f && h < len && rho < R;
        float nh, nr;
        if (!inside)
        {
            float dh = h - ch, dr = rho - cr;
            float d2 = dh * dh + dr * dr;
            if (!(d2 < r * r)) return false;
            float d = MathF.Sqrt(d2);
            if (d > 1e-9f) { nh = ch + dh * (r / d); nr = cr + dr * (r / d); }
            else { nh = ch; nr = R + r; }
        }
        else
        {
            float side = R - rho, bottom = h, top = len - h;
            if (side <= bottom && side <= top) { nh = h; nr = R + r; }
            else if (bottom <= top) { nh = -r; nr = rho; }
            else { nh = len + r; nr = rho; }
        }
        x = a + u * nh + dir * nr;
        return true;
    }

    /// <summary>A sphere out of a solid cone (base centre a, tip b, base radius R). Exact: the cone is round, so the closest
    /// point lies in the plane through the axis and x.</summary>
    public static bool PushOutOfCone(ref Vector3 x, Vector3 a, Vector3 b, float R, float r)
    {
        AxisFrame(x, a, b, out var u, out float H, out float h, out float rho, out var dir);
        if (!(H > 1e-9f) || !(R > 0f)) return false;
        var q = new Vector2(rho, h);
        // the cross-section is the triangle (0,0) (R,0) (0,H); its surface edges are the base and the slant
        var cb = ClosestOnSegment2(new Vector2(0, 0), new Vector2(R, 0), q);
        var cs = ClosestOnSegment2(new Vector2(R, 0), new Vector2(0, H), q);
        bool inside = h > 0f && h < H && rho < R * (1f - h / H);
        float db = Vector2.DistanceSquared(q, cb), ds = Vector2.DistanceSquared(q, cs);
        Vector2 cp = db <= ds ? cb : cs;
        Vector2 n2;
        if (inside)
        {
            n2 = db <= ds ? new Vector2(0, -1) : Vector2.Normalize(new Vector2(H, R));
            q = cp + n2 * r;
        }
        else
        {
            float d = MathF.Sqrt(MathF.Min(db, ds));
            if (!(d < r)) return false;
            if (d > 1e-9f) q = cp + (q - cp) * (r / d);
            else q = cp + (db <= ds ? new Vector2(0, -1) : Vector2.Normalize(new Vector2(H, R))) * r;
        }
        x = a + u * q.Y + dir * MathF.Max(0f, q.X);
        return true;
    }

    static Vector2 ClosestOnSegment2(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        if (!(l2 > 1e-20f)) return a;
        return a + ab * Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f);
    }

    /// <summary>
    /// A sphere out of a triangle. Two-sided: pushed away from the closest point. Front only: pushed to the front side,
    /// also from a little behind it (up to two radii, so a point that slipped under a floor comes back up), but never
    /// pulled through from further behind.
    /// </summary>
    public static bool PushOutOfTriangle(ref Vector3 x, Vector3 a, Vector3 b, Vector3 c, bool twoSided, float r)
    {
        var cp = ClosestOnTriangle(x, a, b, c);
        var d = x - cp;
        float d2 = d.LengthSquared();
        var n = Vector3.Cross(b - a, c - a);
        float nl = n.Length();
        if (!(nl > 1e-20f)) return false;
        n /= nl;
        float s = Vector3.Dot(x - a, n);
        if (twoSided || s >= 0f)
        {
            if (!(d2 < r * r)) return false;
            float dl = MathF.Sqrt(d2);
            x = dl > 1e-9f ? cp + d * (r / dl) : cp + n * (s >= 0f ? r : -r);
            return true;
        }
        // behind a front-only triangle: only straight under it, and not too deep
        if (s < -2f * r) return false;
        var onPlane = x - n * s;
        if (Vector3.DistanceSquared(onPlane, cp) > 1e-10f) return false;
        x = onPlane + n * r;
        return true;
    }

    /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
