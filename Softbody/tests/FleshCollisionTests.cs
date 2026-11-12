using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.11.1: what SkinnedSoftbody collides with: exact shapes for every collider type, mesh colliders through a cached
// triangle tree, and per-point culling with bounding boxes (checked against pairing everything).
static partial class Program
{
    static void FleshCollisionTests(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh collisions (0.11.1): shapes, mesh trees, bounding-box culling");
        BvhMatchesBruteForce();
        ShapePushOuts();
        CullingMatchesBruteForce(m, torso);
        FleshSelfCollision(m, torso);
        Flesh0113(m, torso);
        FleshSquish(m, torso);
        FleshVolumePreservation(m, torso);
        FleshBoxPress(m, torso);
        FleshStuckTests(m, torso);
    }

    /// <summary>A bumpy 120 x 120 quad floor (28,800 triangles).</summary>
    static (Vector3[] pos, int[] tris) BumpyFloor(int n, float size, Vector3 origin)
    {
        var pos = new Vector3[(n + 1) * (n + 1)]; var tris = new List<int>();
        for (int z = 0; z <= n; z++)
            for (int x = 0; x <= n; x++)
                pos[z * (n + 1) + x] = origin + new Vector3(x * size / n, 0.01f * MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.5f), z * size / n);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int a = z * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                tris.AddRange(new[] { a, c, b, b, c, d }); // counter-clockwise seen from above: the front faces +Y
            }
        return (pos, tris.ToArray());
    }

    static void BvhMatchesBruteForce()
    {
        var (pos, tris) = BumpyFloor(120, 2f, new Vector3(-1, 0, -1));
        var sw = Stopwatch.StartNew();
        var bvh = TriangleBvh.Build(pos, tris);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        var rnd = new Random(7);
        var hits = new List<int>();
        int bad = 0; long found = 0;
        for (int q = 0; q < 300; q++)
        {
            var c = new Vector3((float)rnd.NextDouble() * 2.4f - 1.2f, (float)rnd.NextDouble() * 0.06f - 0.03f, (float)rnd.NextDouble() * 2.4f - 1.2f);
            var e = new Vector3((float)rnd.NextDouble() * 0.2f, (float)rnd.NextDouble() * 0.02f, (float)rnd.NextDouble() * 0.2f);
            hits.Clear(); bvh.Query(c - e, c + e, hits);
            var got = new HashSet<(Vector3, Vector3, Vector3)>(hits.Select(t => (bvh.A[t], bvh.B[t], bvh.C[t])));
            int want = 0;
            for (int t = 0; t < tris.Length; t += 3)
            {
                var a = pos[tris[t]]; var b = pos[tris[t + 1]]; var cc = pos[tris[t + 2]];
                var lo = Vector3.Min(a, Vector3.Min(b, cc)); var hi = Vector3.Max(a, Vector3.Max(b, cc));
                if (lo.X > c.X + e.X || hi.X < c.X - e.X || lo.Y > c.Y + e.Y || hi.Y < c.Y - e.Y || lo.Z > c.Z + e.Z || hi.Z < c.Z - e.Z) continue;
                want++;
                if (!got.Contains((a, b, cc))) bad++;
            }
            if (want != hits.Count) bad++;
            found += hits.Count;
        }
        Console.WriteLine($"  triangle tree: {bvh.TriangleCount} triangles, {bvh.NodeCount} nodes, built in {buildMs:0.0} ms; 300 box queries found {found} triangles");
        Check(bad == 0, "the mesh tree finds exactly the triangles a brute-force box test finds");
    }

    static void ShapePushOuts()
    {
        const float r = 0.05f;
        // cylinder: cap centres (0,0,0)..(0,1,0), radius 0.5
        var a = Vector3.Zero; var b = Vector3.UnitY;
        var x1 = new Vector3(0.53f, 0.5f, 0f); bool h1 = XpbdSolver.PushOutOfCylinder(ref x1, a, b, 0.5f, r);
        var x2 = new Vector3(0.1f, 0.5f, 0f); XpbdSolver.PushOutOfCylinder(ref x2, a, b, 0.5f, r);
        var x3 = new Vector3(0f, 1.02f, 0f); XpbdSolver.PushOutOfCylinder(ref x3, a, b, 0.5f, r);
        var x4 = new Vector3(0.7f, 0.5f, 0f); bool h4 = XpbdSolver.PushOutOfCylinder(ref x4, a, b, 0.5f, r);
        Check(h1 && Near(x1, new Vector3(0.55f, 0.5f, 0f)) && Near(x2, new Vector3(0.55f, 0.5f, 0f)) && Near(x3, new Vector3(0f, 1.05f, 0f)) && !h4,
              "cylinder: pushed out through the side or the cap, whichever is nearer; far points untouched");

        // cone: base (0,0,0) radius 0.5, tip (0,1,0); every point ends exactly r from the surface (checked against a sampled surface)
        var rnd = new Random(3);
        var surf = new List<Vector3>();
        for (int i = 0; i <= 200; i++)
            for (int j = 0; j < 128; j++)
            {
                float t = i / 200f, ang = j * MathF.PI * 2f / 128f;
                surf.Add(new Vector3(MathF.Cos(ang) * 0.5f * (1 - t), t, MathF.Sin(ang) * 0.5f * (1 - t)));   // slant
                surf.Add(new Vector3(MathF.Cos(ang) * 0.5f * t, 0f, MathF.Sin(ang) * 0.5f * t));               // base disc
            }
        float worst = 0; int moved = 0, untouched = 0;
        for (int k = 0; k < 400; k++)
        {
            var p = new Vector3((float)rnd.NextDouble() * 1.4f - 0.7f, (float)rnd.NextDouble() * 1.3f - 0.15f, (float)rnd.NextDouble() * 1.4f - 0.7f);
            var q = p;
            if (!XpbdSolver.PushOutOfCone(ref q, a, b, 0.5f, r)) { untouched++; continue; }
            moved++;
            float d = surf.Min(s => Vector3.Distance(s, q));
            worst = MathF.Max(worst, MathF.Abs(d - r));
        }
        Console.WriteLine($"  cone: {moved} of 400 random points pushed out, distance to the surface off by at most {worst * 1000:0.0} mm (surface sampled every ~5 mm)");
        Check(moved > 50 && untouched > 50 && worst < 0.006f, "cone: a pushed point ends one radius from the cone's surface");

        // triangles: front only (a floor) and two-sided
        var ta = new Vector3(-1, 0, -1); var tb = new Vector3(-1, 0, 1); var tc = new Vector3(1, 0, -1); // front +Y
        var f1 = new Vector3(0f, -0.02f, -0.5f); bool g1 = XpbdSolver.PushOutOfTriangle(ref f1, ta, tb, tc, false, r);
        var f2 = new Vector3(0f, -0.2f, -0.5f); bool g2 = XpbdSolver.PushOutOfTriangle(ref f2, ta, tb, tc, false, r);
        var f3 = new Vector3(0f, -0.02f, -0.5f); XpbdSolver.PushOutOfTriangle(ref f3, ta, tb, tc, true, r);
        Check(g1 && Near(f1, new Vector3(0f, r, -0.5f)) && !g2 && Near(f3, new Vector3(0f, -r, -0.5f)),
              "triangles: a floor (front only) lifts a point that slipped just under it, not one far below; two-sided pushes to the nearer side");
    }

    static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

    static List<FleshCollider> BellyColliders(FleshRig rig, TriangleBvh slab)
    {
        rig.TargetBounds(out var lo, out var hi);
        float yMid = 0.5f * (lo.Y + hi.Y), front = hi.Z;
        var list = new List<FleshCollider>
        {
            FleshCollider.Box(new Vector3(0.0f, yMid + 0.05f, front + 0.04f - 0.012f), Quaternion.Identity, new Vector3(0.03f, 0.03f, 0.04f)),
            FleshCollider.Capsule(new Vector3(-0.1f, yMid - 0.03f, front + 0.02f - 0.01f), new Vector3(0.1f, yMid - 0.03f, front + 0.02f - 0.01f), 0.02f),
            FleshCollider.Cylinder(new Vector3(-0.05f, yMid + 0.1f, front + 0.25f), new Vector3(-0.05f, yMid + 0.1f, front - 0.01f), 0.02f),
            FleshCollider.Cone(new Vector3(0.05f, yMid + 0.1f, front + 0.25f), new Vector3(0.05f, yMid + 0.1f, front - 0.012f), 0.03f),
            // a big tilted slab (a mesh collider) pressing on the lower belly, and a far one that must be culled
            FleshCollider.MeshShape(slab, Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(-1f, yMid - 0.1f, front - 0.008f), false),
            FleshCollider.MeshShape(slab, Matrix4x4.CreateTranslation(-1f, -5f, -1f), false),
        };
        // users' hand spheres around the torso, some touching
        var rnd = new Random(11);
        for (int k = 0; k < 20; k++)
            list.Add(FleshCollider.Sphere(new Vector3((float)rnd.NextDouble() * 0.3f - 0.15f, lo.Y + (float)rnd.NextDouble() * (hi.Y - lo.Y), front + 0.04f - (float)rnd.NextDouble() * 0.03f), 0.04f, source: 1));
        return list;
    }

    static void CullingMatchesBruteForce(FleshModel m, float[] torso)
    {
        var (pos, tris) = BumpyFloor(120, 2f, Vector3.Zero);
        var slab = TriangleBvh.Build(pos, tris);
        var w = new float[m.ShapeNames.Length];
        double pairMs = 0, contactMs = 0, showMs = 0; int fitVerts = 0;
        FleshRig Run(bool brute, out double ms, out int maxPairs, out int maxTris, out int maxNear, out int maxContacts)
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings());
            rig.Solver.BruteForceCollisions = brute;
            rig.Solver.Squish = false; // squish can move a point further than this frame's reach; culling is what is tested here
            // and frictionless: friction's stick/slide threshold turns a 1e-6 m difference into millimetres over a second
            rig.Solver.Friction = 0f;
            // and no let-go (0.26.4): a point let go springs back toward its animated place, further than this frame's
            // reach as well, and the let-go's thresholds turn a tiny difference into a different decision
            rig.Solver.KeepIn = false;
            // and no unbounce (0.26.6): it lowers the velocities the reach is estimated from, and its threshold (leaving
            // faster than the collider) turns a tiny difference into a different velocity
            rig.Solver.NoBounce = false;
            rig.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
            var cols = BellyColliders(rig, slab);
            ms = 0; maxPairs = maxTris = maxNear = maxContacts = 0;
            for (int f = 0; f < 60; f++)
            {
                rig.Solver.Colliders.Clear(); rig.Solver.Colliders.AddRange(cols);
                rig.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, 0.04f * MathF.Sin(f * 0.25f), 0)), w);
                if (f >= 10) { ms += rig.StepMs; if (!brute) { pairMs += rig.Solver.PairMs / 50; contactMs += rig.Solver.ContactMs / 50; showMs += rig.ContactShowMs / 50; fitVerts = Math.Max(fitVerts, rig.FitVertices); } }
                maxPairs = Math.Max(maxPairs, rig.Solver.PairCount); maxTris = Math.Max(maxTris, rig.Solver.MeshTriangles);
                maxNear = Math.Max(maxNear, rig.Solver.NearShapes.Length); maxContacts = Math.Max(maxContacts, rig.Solver.Contacts);
            }
            ms /= 50;
            return rig;
        }
        var culled = Run(false, out var cms, out var cPairs, out var cTris, out var cNear, out var cContacts);
        var brute = Run(true, out var bms, out var bPairs, out _, out _, out _);
        float diff = 0;
        for (int i = 0; i < culled.Solver.N; i++) diff = MathF.Max(diff, Vector3.Distance(culled.Solver.X[i], brute.Solver.X[i]));
        int pts = culled.Surface.SoftCount;
        Console.WriteLine($"  {pts} skin points against a box, capsule, cylinder, cone, 20 user spheres and two 28,800-triangle mesh colliders (one far away), bobbing for 1 s:");
        Console.WriteLine($"    near the body: up to {cNear} shapes ({cTris} mesh triangles), {cPairs} point pairs (pairing everything: {bPairs}), up to {cContacts} points touching");
        Console.WriteLine($"    step {cms:0.00} ms with culling ({pairMs:0.00} ms finding pairs, {contactMs:0.00} ms in the contact passes, {showMs:0.00} ms showing them on up to {fitVerts} render vertices), {bms:0.00} ms pairing everything; positions differ by {diff * 1000:0.000000} mm");
        Check(diff < 1e-5f, "culling with bounding boxes gives the same result as testing every point against every nearby shape (within 0.01 mm)");
        Check(cContacts > 20 && cTris > 0 && cTris < 2000 && cPairs < bPairs / 4, "only the triangles near the body are taken from the mesh colliders, and only nearby points are tested");
        var plain = FleshBuildRig(m, torso, new FleshSettings());
        double pms = 0, plainShow = 0;
        var plainTimes = new List<double>(); var plainShows = new List<double>();
        for (int f = 0; f < 60; f++)
        {
            plain.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, 0.04f * MathF.Sin(f * 0.25f), 0)), w);
            if (f >= 10) { pms += plain.StepMs; plainShow += plain.ContactShowMs / 50; plainTimes.Add(plain.StepMs); plainShows.Add(plain.ContactShowMs); }
        }
        pms /= 50;
        Console.WriteLine($"    the same without any colliders: {pms:0.00} ms ({plainShow:0.00} ms spreading the offsets; collisions add {cms - pms:0.00} ms in this worst case: the whole belly front against a 1.7 cm-triangle wall)");
        Check(cms - pms < 4.0, "even the whole belly pressed against a finely tessellated wall adds only a few ms (on the worker thread)");

        // a usual scene: a floor box and a detailed floor mesh at the feet (far from the soft torso), a table box near the
        // belly and two hands poking it
        var everyday = FleshBuildRig(m, torso, new FleshSettings());
        everyday.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        everyday.TargetBounds(out var lo, out var hi);
        float yMid = 0.5f * (lo.Y + hi.Y);
        var scene = new List<FleshCollider>
        {
            FleshCollider.Box(new Vector3(0, -0.05f, 0), Quaternion.Identity, new Vector3(5f, 0.05f, 5f)),
            FleshCollider.MeshShape(slab, Matrix4x4.CreateTranslation(-1f, 0.001f, -1f), false),
            FleshCollider.Box(new Vector3(0, yMid - 0.12f, hi.Z + 0.3f), Quaternion.Identity, new Vector3(0.6f, 0.02f, 0.3f)),
            FleshCollider.Sphere(new Vector3(0.03f, yMid, hi.Z + 0.045f), 0.05f, source: 1),
            FleshCollider.Sphere(new Vector3(-0.04f, yMid + 0.04f, hi.Z + 0.05f), 0.05f, source: 1),
        };
        // medians: two separate runs' means differ by a few tenths of a millisecond on a busy machine alone
        var dayTimes = new List<double>(); var dayShows = new List<double>();
        for (int f = 0; f < 60; f++)
        {
            everyday.Solver.Colliders.Clear(); everyday.Solver.Colliders.AddRange(scene);
            everyday.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, 0.04f * MathF.Sin(f * 0.25f), 0)), w);
            if (f >= 10) { dayTimes.Add(everyday.StepMs); dayShows.Add(everyday.ContactShowMs); }
        }
        static double Median(List<double> l) { l.Sort(); return 0.5 * (l[(l.Count - 1) / 2] + l[l.Count / 2]); }
        double ems = Median(dayTimes), pmed = Median(plainTimes);
        Console.WriteLine($"    a usual scene (floor box + floor mesh at the feet, a table edge, two hands on the belly): step {ems:0.00} ms (median), collisions add {ems - pmed:0.00} ms " +
                          $"(showing the contacts {Median(dayShows) - Median(plainShows):0.00} ms of it), {everyday.Solver.PairCount} pairs, {everyday.Solver.Contacts} points touching");
        Check(ems - pmed < 0.4 && everyday.Solver.Contacts > 0, "a usual scene costs a fraction of a millisecond, and the hands dent the belly");
    }
}
