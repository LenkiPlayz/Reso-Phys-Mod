using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>A copy of what the flesh debug view draws, taken between two steps (see <see cref="FleshRig.FillDebug"/>).</summary>
public sealed class FleshDebugSnapshot
{
    public bool Tets, Colliders;
    public Vector3[] X = Array.Empty<Vector3>(), Targets = Array.Empty<Vector3>();
    public int Count, OuterCount, Layers;
    // per rig, never changed after the build: shared, not copied
    public bool[] Kinematic = Array.Empty<bool>();
    public float[] Soft = Array.Empty<float>(), Radius = Array.Empty<float>();
    public int[] Edges = Array.Empty<int>();
    public float LineRadius;
    public readonly List<FleshCollider> ColliderList = new();
    public bool HasSearch, HasGroundHit;
    public Vector3 RayOrigin, GroundHit, SearchCenter, SearchSize;
}

/// <summary>
/// Builds the flesh debug geometry. Tets: every tet edge, grey where both ends are pinned (the inner layer and the ring),
/// blue to orange by softness on the skin, light blue through the shell; white lines from where a skin point should be
/// to where it is, when it is noticeably off. Colliders: each simulated skin point's collision sphere (green, yellow
/// while touching), the shapes near the body it can be pushed out of (world colliders magenta, mesh colliders as their
/// nearby triangles, users' colliders teal) and the box searched for colliders (grey).
/// </summary>
public static class FleshDebugView
{
    static readonly Vector4 Through = new(0.45f, 0.7f, 1f, 0.45f);
    /// <summary>Users' colliders (the dynamic bone spheres on heads, bodies, limbs and hands).</summary>
    public static readonly Vector4 UserCollider = new(0.3f, 1f, 0.9f, 0.9f);
    /// <summary>The avatar's own colliders (self-collision).</summary>
    public static readonly Vector4 OwnCollider = new(1f, 0.85f, 0.4f, 0.8f);

    public static DebugCounts Build(FleshDebugSnapshot s, DebugGeometry g) => Build(new[] { s }, 1, g);

    /// <summary>0.13.0: several layers' rigs into one view; the colliders (the same for every layer) drawn once, from the
    /// first.</summary>
    public static DebugCounts Build(IReadOnlyList<FleshDebugSnapshot> list, int count, DebugGeometry g)
    {
        g.Clear();
        var c = new DebugCounts();
        for (int i = 0; i < count && i < list.Count; i++) Append(list[i], g, ref c, i == 0);
        c.Vertices = g.VertexCount; c.Triangles = g.IndexCount / 3; c.Truncated = g.Truncated;
        return c;
    }

    static void Append(FleshDebugSnapshot s, DebugGeometry g, ref DebugCounts c, bool shapes)
    {
        float lr = MathF.Max(1e-4f, s.LineRadius);
        int n = s.OuterCount;
        if (s.Colliders)
        {
            if (shapes) foreach (var col in s.ColliderList)
            {
                var color = col.Source == 1 ? UserCollider : col.Source == 2 ? OwnCollider : DebugView.Proxy;
                switch (col.Kind)
                {
                    case FleshColliderKind.Plane:
                        g.Patch(col.A, col.B, s.HasSearch ? 0.5f * MathF.Max(s.SearchSize.X, s.SearchSize.Z) : 0.5f, DebugView.Ground);
                        break;
                    case FleshColliderKind.Sphere: g.WireSphere(col.A, col.R, lr, color); break;
                    case FleshColliderKind.Box: g.WireBox(col.A, col.Q, col.B, lr, color); break;
                    case FleshColliderKind.Capsule:
                        g.WireSphere(col.A, col.R, lr, color); g.WireSphere(col.B, col.R, lr, color);
                        g.Line(col.A, col.B, lr, color);
                        break;
                    case FleshColliderKind.Cylinder:
                    case FleshColliderKind.Cone:
                    {
                        var ax = col.B - col.A; float len = ax.Length();
                        var u = len > 1e-9f ? ax / len : Vector3.UnitY;
                        var p1 = Vector3.Normalize(Vector3.Cross(u, MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
                        var p2 = Vector3.Cross(u, p1);
                        g.WireCircle(col.A, p1, p2, col.R, 16, lr, color);
                        if (col.Kind == FleshColliderKind.Cylinder) g.WireCircle(col.B, p1, p2, col.R, 16, lr, color);
                        for (int k = 0; k < 4; k++)
                        {
                            float ang = k * MathF.PI * 0.5f;
                            var rim = (p1 * MathF.Cos(ang) + p2 * MathF.Sin(ang)) * col.R;
                            g.Line(col.A + rim, col.Kind == FleshColliderKind.Cylinder ? col.B + rim : col.B, lr, color);
                        }
                        break;
                    }
                    case FleshColliderKind.Triangle:
                        g.Line(col.A, col.B, lr * 0.7f, color); g.Line(col.B, col.C, lr * 0.7f, color); g.Line(col.C, col.A, lr * 0.7f, color);
                        break;
                }
                c.Proxies++;
            }
            if (shapes && s.HasSearch) g.WireBox(s.SearchCenter, Quaternion.Identity, 0.5f * s.SearchSize, lr, DebugView.Search);
            for (int i = 0; i < n && i < s.Count; i++)
            {
                float r = i < s.Radius.Length ? s.Radius[i] : 0f;
                if (!(r > 0f)) continue;
                bool touch = Touching(s, s.X[i], r * 1.1f);
                if (!g.Sphere(s.X[i], r, touch ? DebugView.SphereTouch : DebugView.SphereFree)) break;
                c.Spheres++; if (touch) c.Touching++;
            }
        }
        if (s.Tets)
        {
            var e = s.Edges;
            for (int k = 0; k + 1 < e.Length; k += 2)
            {
                int a = e[k], b = e[k + 1];
                if (a >= s.Count || b >= s.Count) continue;
                bool pinned = s.Kinematic[a] && s.Kinematic[b];
                Vector4 col;
                if (pinned) col = DebugView.Firm;
                else if (a < n && b < n)
                {
                    float soft = 0.5f * (s.Soft[a] + s.Soft[b]);
                    col = Vector4.Lerp(DebugView.Hard, DebugView.Soft, Math.Clamp(soft, 0f, 1f));
                }
                else col = Through;
                if (!g.Line(s.X[a], s.X[b], lr, col)) break;
                c.Lines++;
            }
            for (int i = 0; i < n && i < s.Count; i++)
            {
                if (s.Kinematic[i]) continue;
                float r = i < s.Radius.Length ? s.Radius[i] : 0f;
                float limit = MathF.Max(0.25f * r, 0.002f);
                if (Vector3.DistanceSquared(s.X[i], s.Targets[i]) < limit * limit) continue;
                if (!g.Line(s.Targets[i], s.X[i], lr * 0.8f, DebugView.Offset)) break;
                c.Offsets++;
            }
        }
    }

    static bool Touching(FleshDebugSnapshot s, Vector3 p, float r)
    {
        foreach (var col in s.ColliderList)
        {
            var x = p;
            if (XpbdSolver.Resolve(ref x, r, col)) return true;
        }
        return false;
    }
}
