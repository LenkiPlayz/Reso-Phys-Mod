using System.Numerics;

namespace Softbody.Core;

/// <summary>
/// Triangle geometry for the debug views (world space, linear RGBA vertex colours): thin lines as triangular
/// prisms, points as octahedra, collision spheres as icosahedra, flat patches. The buffers grow as needed and
/// are reused between builds. Adding stops (and <see cref="Truncated"/> is set) once <see cref="MaxVertices"/>
/// would be passed, so a huge cage can never make a huge mesh.
/// </summary>
public sealed class DebugGeometry
{
    public Vector3[] Positions = new Vector3[1024];
    public Vector4[] Colors = new Vector4[1024];
    public int[] Indices = new int[3072];
    public int VertexCount, IndexCount;
    public int MaxVertices = 150_000;
    /// <summary>Something was left out because of <see cref="MaxVertices"/>.</summary>
    public bool Truncated;

    public void Clear() { VertexCount = 0; IndexCount = 0; Truncated = false; }

    bool Reserve(int verts, int indices)
    {
        if (VertexCount + verts > MaxVertices) { Truncated = true; return false; }
        if (VertexCount + verts > Positions.Length)
        {
            int cap = Math.Max(VertexCount + verts, Positions.Length * 2);
            Array.Resize(ref Positions, cap);
            Array.Resize(ref Colors, cap);
        }
        if (IndexCount + indices > Indices.Length) Array.Resize(ref Indices, Math.Max(IndexCount + indices, Indices.Length * 2));
        return true;
    }

    int V(Vector3 p, Vector4 c) { Positions[VertexCount] = p; Colors[VertexCount] = c; return VertexCount++; }
    void T(int a, int b, int c) { Indices[IndexCount++] = a; Indices[IndexCount++] = b; Indices[IndexCount++] = c; }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    static Vector3 Perpendicular(Vector3 d)
    {
        var a = MathF.Abs(d.X) < 0.6f ? Vector3.UnitX : MathF.Abs(d.Y) < 0.6f ? Vector3.UnitY : Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(d, a));
    }

    /// <summary>A line from a to b: a three-sided prism of the given radius (6 vertices, 6 triangles).</summary>
    public bool Line(Vector3 a, Vector3 b, float radius, Vector4 color)
    {
        var d = b - a;
        float len = d.Length();
        if (!(len > 1e-7f) || !Finite(a) || !Finite(b) || !(radius > 0f)) return false;
        if (!Reserve(6, 18)) return false;
        d /= len;
        var u = Perpendicular(d) * radius;
        var w = Vector3.Cross(d, u);
        int s = VertexCount;
        for (int k = 0; k < 3; k++)
        {
            float ang = k * (2f * MathF.PI / 3f);
            var off = MathF.Cos(ang) * u + MathF.Sin(ang) * w;
            V(a + off, color);
            V(b + off, color);
        }
        for (int k = 0; k < 3; k++)
        {
            int n = (k + 1) % 3;
            int a0 = s + 2 * k, b0 = a0 + 1, a1 = s + 2 * n, b1 = a1 + 1;
            T(a0, b0, b1);
            T(a0, b1, a1);
        }
        return true;
    }

    /// <summary>A small marker: an octahedron (6 vertices, 8 triangles).</summary>
    public bool Octahedron(Vector3 c, float r, Vector4 color)
    {
        if (!Finite(c) || !(r > 0f) || !Reserve(6, 24)) return false;
        int s = VertexCount;
        V(c + new Vector3(r, 0, 0), color); V(c - new Vector3(r, 0, 0), color);
        V(c + new Vector3(0, r, 0), color); V(c - new Vector3(0, r, 0), color);
        V(c + new Vector3(0, 0, r), color); V(c - new Vector3(0, 0, r), color);
        T(s + 0, s + 2, s + 4); T(s + 2, s + 1, s + 4); T(s + 1, s + 3, s + 4); T(s + 3, s + 0, s + 4);
        T(s + 2, s + 0, s + 5); T(s + 1, s + 2, s + 5); T(s + 3, s + 1, s + 5); T(s + 0, s + 3, s + 5);
        return true;
    }

    static readonly Vector3[] IcoVerts = BuildIcoVerts();
    static readonly int[] IcoTris =
    {
        0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
        3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
    };

    static Vector3[] BuildIcoVerts()
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new[]
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < v.Length; i++) v[i] = Vector3.Normalize(v[i]);
        return v;
    }

    /// <summary>A sphere: an icosahedron (12 vertices, 20 triangles) whose vertices lie on the sphere.</summary>
    public bool Sphere(Vector3 c, float r, Vector4 color)
    {
        if (!Finite(c) || !(r > 0f) || !Reserve(12, IcoTris.Length)) return false;
        int s = VertexCount;
        foreach (var v in IcoVerts) V(c + v * r, color);
        for (int i = 0; i < IcoTris.Length; i += 3) T(s + IcoTris[i], s + IcoTris[i + 1], s + IcoTris[i + 2]);
        return true;
    }

    /// <summary>A flat square of half size <paramref name="half"/> through c, facing n (4 vertices, 2 triangles).</summary>
    public bool Patch(Vector3 c, Vector3 n, float half, Vector4 color)
    {
        if (!Finite(c) || !(n.LengthSquared() > 1e-12f) || !(half > 0f) || !Reserve(4, 6)) return false;
        n = Vector3.Normalize(n);
        var u = Perpendicular(n) * half;
        var w = Vector3.Cross(n, u);
        int s = VertexCount;
        V(c - u - w, color); V(c + u - w, color); V(c + u + w, color); V(c - u + w, color);
        T(s, s + 1, s + 2); T(s, s + 2, s + 3);
        return true;
    }

    /// <summary>The twelve edges of a box (centre, rotation, half extents).</summary>
    public void WireBox(Vector3 c, Quaternion q, Vector3 half, float lineRadius, Vector4 color)
    {
        Span<Vector3> k = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            k[i] = c + Vector3.Transform(new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z), q);
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0) Line(k[i], k[i | bit], lineRadius, color);
    }

    /// <summary>A circle in the plane of the two (unit, perpendicular) axes, as line segments.</summary>
    public void WireCircle(Vector3 c, Vector3 a, Vector3 b, float r, int segments, float lineRadius, Vector4 color)
    {
        var prev = c + a * r;
        for (int i = 1; i <= segments; i++)
        {
            float ang = i * (2f * MathF.PI / segments);
            var p = c + (MathF.Cos(ang) * a + MathF.Sin(ang) * b) * r;
            Line(prev, p, lineRadius, color);
            prev = p;
        }
    }

    /// <summary>Three great circles of a sphere.</summary>
    public void WireSphere(Vector3 c, float r, float lineRadius, Vector4 color)
    {
        WireCircle(c, Vector3.UnitX, Vector3.UnitY, r, 24, lineRadius, color);
        WireCircle(c, Vector3.UnitY, Vector3.UnitZ, r, 24, lineRadius, color);
        WireCircle(c, Vector3.UnitZ, Vector3.UnitX, r, 24, lineRadius, color);
    }
}

/// <summary>
/// What the debug views show, copied from a running <see cref="CageSimulation"/> on the world thread
/// (<see cref="CageSimulation.FillDebug"/>) so the geometry can be built on another thread.
/// </summary>
public sealed class DebugSnapshot
{
    public bool Cells, Colliders;
    /// <summary>The cage (immutable once built; a rebuild makes a new one).</summary>
    public Cage Cage;
    public Vector3[] Points = Array.Empty<Vector3>();
    /// <summary>Where each point wants to be (its rest place on the slot or the skeleton).</summary>
    public Vector3[] Targets = Array.Empty<Vector3>();
    public bool[] Untangling = Array.Empty<bool>();
    public int PointCount;
    /// <summary>World radius of a point's collision sphere, and world size of a cell (the smallest axis).</summary>
    public float RadiusWorld, CellWorld;
    public bool Collide;
    public bool HasGround;
    /// <summary>The floor as the simulation uses it (lowered under the rest shape when needed) and as the ray found it.</summary>
    public Vector3 GroundPoint, GroundNormal, GroundHit;
    public float GroundDrop;
    public readonly List<ProxyShape> Proxies = new();
    /// <summary>The ground ray's origin and the collider search box (set by the plugin; HasSearch false offline).</summary>
    public bool HasSearch;
    public Vector3 RayOrigin, SearchCenter, SearchSize;
}

/// <summary>What one <see cref="DebugView.Build"/> drew.</summary>
public struct DebugCounts
{
    public int Lines, Points, Offsets, Spheres, Touching, Proxies, Vertices, Triangles;
    public bool Truncated;
}

/// <summary>
/// Builds the debug views' geometry from a <see cref="DebugSnapshot"/>.
///   Cells: the lattice (the cells' edges, not the tetrahedra's diagonals) coloured by softness, blue (firm) to
///   orange (squishy), pinned parts grey; cage points as markers; points the untangler is pulling back in red; a white
///   line from where a point wants to be to where it is, when it is pushed or sagging away from it.
///   Colliders: each simulated point's collision sphere (green, yellow while touching something), the
///   world-collider proxies (magenta wireframes), the ground patch under the cage with its ray (cyan), and the
///   box searched for colliders (grey).
/// Not thread-safe: one builder per thread (the plugin builds on the engine's mesh thread).
/// </summary>
public sealed class DebugView
{
    Cage _latticeFor;
    int[] _lattice = Array.Empty<int>();
    int _latticeCount;

    public static readonly Vector4 Firm = new(0.55f, 0.6f, 0.68f, 0.35f);
    public static readonly Vector4 Hard = new(0.15f, 0.45f, 1f, 0.9f);
    public static readonly Vector4 Soft = new(1f, 0.55f, 0.08f, 0.9f);
    public static readonly Vector4 Untangle = new(1f, 0.08f, 0.08f, 1f);
    public static readonly Vector4 SphereFree = new(0.2f, 1f, 0.35f, 0.22f);
    public static readonly Vector4 SphereTouch = new(1f, 0.85f, 0.1f, 0.55f);
    public static readonly Vector4 Proxy = new(1f, 0.2f, 0.9f, 1f);
    public static readonly Vector4 Ground = new(0.1f, 0.9f, 1f, 0.16f);
    public static readonly Vector4 GroundLine = new(0.1f, 0.9f, 1f, 0.8f);
    public static readonly Vector4 Search = new(0.75f, 0.75f, 0.75f, 0.3f);
    public static readonly Vector4 Offset = new(1f, 1f, 1f, 0.75f);

    /// <summary>Index pairs of the lattice edges of the last cage (cached per cage).</summary>
    public ReadOnlySpan<int> LatticeEdges(Cage cage)
    {
        if (!ReferenceEquals(cage, _latticeFor))
        {
            _latticeFor = cage;
            _latticeCount = 0;
            if (_lattice.Length < cage.Edges.Length) _lattice = new int[cage.Edges.Length];
            var half = 0.5f * cage.Spacing;
            var rest = cage.RestPoints;
            for (int e = 0; e < cage.Edges.Length; e += 2)
            {
                int a = cage.Edges[e], b = cage.Edges[e + 1];
                var d = Vector3.Abs(rest[b] - rest[a]);
                int axes = (d.X > half.X ? 1 : 0) + (d.Y > half.Y ? 1 : 0) + (d.Z > half.Z ? 1 : 0);
                if (axes != 1) continue; // a face or body diagonal of a cell
                _lattice[_latticeCount++] = a;
                _lattice[_latticeCount++] = b;
            }
        }
        return _lattice.AsSpan(0, _latticeCount);
    }

    static Vector4 SoftColor(float s) => Vector4.Lerp(Hard, Soft, Math.Clamp(s, 0f, 1f));

    public DebugCounts Build(DebugSnapshot s, DebugGeometry g)
    {
        g.Clear();
        var counts = new DebugCounts();
        var cage = s.Cage;
        int n = cage == null ? 0 : Math.Min(s.PointCount, Math.Min(cage.PointCount, s.Points.Length));
        float cell = s.CellWorld > 0f && float.IsFinite(s.CellWorld) ? s.CellWorld : 0.05f;
        float line = Math.Clamp(cell * 0.02f, 0.0008f, 0.01f);

        if (s.Colliders)
        {
            // the few big things first, so they survive truncation
            foreach (var p in s.Proxies)
            {
                if (p.IsSphere) g.WireSphere(p.Position, p.Radius, line * 1.5f, Proxy);
                else g.WireBox(p.Position, Quaternion.Normalize(p.Rotation), p.HalfExtents, line * 1.5f, Proxy);
                counts.Proxies++;
            }
            if (s.HasGround)
            {
                float half = 0.25f;
                if (n > 0)
                {
                    Vector3 lo = s.Points[0], hi = s.Points[0];
                    for (int i = 1; i < n; i++) { lo = Vector3.Min(lo, s.Points[i]); hi = Vector3.Max(hi, s.Points[i]); }
                    half = Math.Clamp(0.75f * (hi - lo).Length(), 0.1f, 5f);
                }
                // the floor where the simulation has it: lowered under the body's rest shape when that reaches into it
                var gp = s.GroundPoint + s.GroundNormal * 0.002f; // just above, so it does not z-fight with the real floor
                g.Patch(gp, s.GroundNormal, half, Ground);
                if (s.HasSearch) g.Line(s.RayOrigin, s.GroundHit, line, GroundLine);
                g.Octahedron(s.GroundHit, line * 4f, GroundLine);
                if (s.GroundDrop > 1e-3f) g.Line(s.GroundHit, s.GroundPoint, line, GroundLine);
            }
            if (s.HasSearch) g.WireBox(s.SearchCenter, Quaternion.Identity, 0.5f * s.SearchSize, line, Search);
        }

        if (s.Cells && n > 0)
        {
            var edges = LatticeEdges(cage);
            for (int e = 0; e < edges.Length; e += 2)
            {
                int a = edges[e], b = edges[e + 1];
                if (a >= n || b >= n) continue;
                Vector4 c;
                if (Flag(s.Untangling, a) || Flag(s.Untangling, b)) c = Untangle;
                else if (cage.Kinematic[a] && cage.Kinematic[b]) c = Firm;
                else c = SoftColor(0.5f * (cage.PointSoftness[a] + cage.PointSoftness[b]));
                if (g.Line(s.Points[a], s.Points[b], line, c)) counts.Lines++;
            }
            // white: how far each simulated point is from where it wants to be (only when that is a visible amount)
            float minOffset = MathF.Max(0.25f * s.RadiusWorld, 0.002f);
            if (s.Targets.Length >= n)
                for (int i = 0; i < n; i++)
                {
                    if (cage.Kinematic[i] || Vector3.Distance(s.Points[i], s.Targets[i]) < minOffset) continue;
                    if (g.Line(s.Targets[i], s.Points[i], line * 0.7f, Offset)) counts.Offsets++;
                }
            for (int i = 0; i < n; i++)
            {
                bool ok;
                if (Flag(s.Untangling, i)) ok = g.Octahedron(s.Points[i], cell * 0.12f, Untangle);
                else if (cage.Kinematic[i]) ok = g.Octahedron(s.Points[i], cell * 0.05f, Firm);
                else ok = g.Octahedron(s.Points[i], cell * 0.08f, SoftColor(cage.PointSoftness[i]));
                if (ok) counts.Points++;
            }
        }

        if (s.Colliders && n > 0 && s.Collide)
        {
            // only simulated points collide (pinned ones follow the slot exactly)
            float r = s.RadiusWorld;
            for (int i = 0; i < n; i++)
            {
                if (cage.Kinematic[i]) continue;
                bool touch = Touching(s, s.Points[i], r);
                if (g.Sphere(s.Points[i], r, touch ? SphereTouch : SphereFree)) { counts.Spheres++; if (touch) counts.Touching++; }
            }
        }

        counts.Vertices = g.VertexCount;
        counts.Triangles = g.IndexCount / 3;
        counts.Truncated = g.Truncated;
        return counts;
    }

    static bool Flag(bool[] a, int i) => (uint)i < (uint)a.Length && a[i];

    /// <summary>Whether a point's collision sphere touches (or is pushed into) the ground or a proxy, with a 10% margin.</summary>
    public static bool Touching(DebugSnapshot s, Vector3 p, float r)
    {
        float reach = r * 1.1f;
        if (s.HasGround && Vector3.Dot(p - s.GroundPoint, s.GroundNormal) < reach) return true;
        foreach (var x in s.Proxies)
        {
            if (x.IsSphere)
            {
                if (Vector3.Distance(p, x.Position) < x.Radius + reach) return true;
            }
            else
            {
                var q = Vector3.Transform(p - x.Position, Quaternion.Conjugate(Quaternion.Normalize(x.Rotation)));
                var closest = Vector3.Clamp(q, -x.HalfExtents, x.HalfExtents);
                if (Vector3.Distance(q, closest) < reach) return true;
            }
        }
        return false;
    }
}
