using System.Diagnostics;
using System.Numerics;
using Softbody.Core;
using Softbody.Core.Cloth;
using Softbody.Core.Flesh;

// 0.29.2: players' body shapes for the cloth (spheres fitted inside a skinned mesh) and the grip on players.
// Run with:  dotnet run -c Release --project tests -- body
namespace Softbody.Tests;

static partial class Program
{
    static void BodyShapeTests(bool explore = false)
    {
        Console.WriteLine("== Body shapes for the cloth (0.29.2)");
        _bodyVerbose = explore;
        BodyLimb();
        BodyBentLimb();
        BodyThinParts();
        BodyNestedLayers();
        BodyGripOnSlope();
        BodyTurntable();
        BodyTeleport();
        var path = FleshModel.DefaultPath();
        if (path == null || !File.Exists(path)) { Console.WriteLine("  (the avatar checks skipped: no test model at " + path + ")"); return; }
        var model = FleshModel.Load(path);
        if (explore) BodyExplore(model);
        BodyAvatar(model);
        BodyBlanketOnAvatar(model);
    }

    // ------------------------------------------------------------------ test meshes

    /// <summary>A capsule along +Y from (0,y0,0) to (0,y1,0) of radius R (smooth normals, outward winding).</summary>
    static (Vector3[] pos, Vector3[] nrm, int[] tris) CapsuleMesh(float y0, float y1, float R, int seg = 32, int capRows = 8, int bodyRows = 16)
    {
        var rows = new List<(float y, float rr, float ny)>();
        for (int k = 1; k <= capRows; k++) { float lat = -MathF.PI / 2 + MathF.PI / 2 * k / capRows; rows.Add((y0 + R * MathF.Sin(lat), R * MathF.Cos(lat), MathF.Sin(lat))); }
        for (int k = 1; k < bodyRows; k++) rows.Add((y0 + (y1 - y0) * k / bodyRows, R, 0f));
        for (int k = 0; k < capRows; k++) { float lat = MathF.PI / 2 * k / capRows; rows.Add((y1 + R * MathF.Sin(lat), R * MathF.Cos(lat), MathF.Sin(lat))); }
        var pos = new List<Vector3> { new(0f, y0 - R, 0f) }; var nrm = new List<Vector3> { -Vector3.UnitY };
        foreach (var (y, rr, ny) in rows)
        {
            float nr = MathF.Sqrt(MathF.Max(0f, 1f - ny * ny));
            for (int j = 0; j < seg; j++)
            {
                float f = 2f * MathF.PI * j / seg;
                pos.Add(new Vector3(rr * MathF.Cos(f), y, rr * MathF.Sin(f)));
                nrm.Add(new Vector3(nr * MathF.Cos(f), ny, nr * MathF.Sin(f)));
            }
        }
        int top = pos.Count;
        pos.Add(new Vector3(0f, y1 + R, 0f)); nrm.Add(Vector3.UnitY);
        var tris = new List<int>();
        int Ring(int row, int j) => 1 + row * seg + (j % seg);
        for (int j = 0; j < seg; j++) tris.AddRange(new[] { 0, Ring(0, j), Ring(0, j + 1) });
        for (int row = 0; row + 1 < rows.Count; row++)
            for (int j = 0; j < seg; j++)
            {
                int a = Ring(row, j), b = Ring(row, j + 1), c = Ring(row + 1, j + 1), d = Ring(row + 1, j);
                tris.AddRange(new[] { a, c, b }); tris.AddRange(new[] { a, d, c });
            }
        int last = rows.Count - 1;
        for (int j = 0; j < seg; j++) tris.AddRange(new[] { top, Ring(last, j + 1), Ring(last, j) });
        return (pos.ToArray(), nrm.ToArray(), tris.ToArray());
    }

    /// <summary>A box with flat faces (each face its own vertices), outward winding, subdivided n times per edge.</summary>
    static (Vector3[] pos, Vector3[] nrm, int[] tris) BoxMesh(Vector3 half, int n = 8)
    {
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tris = new List<int>();
        void Face(Vector3 normal, Vector3 u, Vector3 v)
        {
            // u x v = normal: (B-A)x(C-A) of (a, b, c) below is u x v
            int start = pos.Count;
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float s = 2f * i / n - 1f, t = 2f * j / n - 1f;
                    pos.Add(normal * Dot(normal, half) + u * (s * Dot(Vector3.Abs(u), half)) + v * (t * Dot(Vector3.Abs(v), half)));
                    nrm.Add(normal);
                }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = start + j * (n + 1) + i, b = a + 1, d = a + (n + 1), c = d + 1;
                    tris.AddRange(new[] { a, b, c }); tris.AddRange(new[] { a, c, d });
                }
        }
        static float Dot(Vector3 a, Vector3 b) => MathF.Abs(Vector3.Dot(a, b));
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ); Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX); Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY); Face(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        return (pos.ToArray(), nrm.ToArray(), tris.ToArray());
    }

    /// <summary>Two bones split at y = joint (a smooth 10 cm blend), or one bone.</summary>
    static SkinData TwoBones(Vector3[] pos, float joint, bool oneBone = false)
    {
        int n = pos.Length;
        var b = new int[n * 4]; var w = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            float t = oneBone ? 0f : Math.Clamp((pos[v].Y - (joint - 0.05f)) / 0.1f, 0f, 1f);
            t = t * t * (3f - 2f * t);
            b[v * 4] = 0; w[v * 4] = 1f - t; b[v * 4 + 1] = 1; w[v * 4 + 1] = t;
        }
        return SkinData.FromBindings(2, b, w);
    }

    static Matrix4x4[] Identity(int bones)
    {
        var m = new Matrix4x4[bones + 1];
        Array.Fill(m, Matrix4x4.Identity);
        return m;
    }

    static float CapsuleSdf(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector3.Distance(p, a + ab * t) - r;
    }

    /// <summary>The farthest any of the points stands off the spheres (|q - c| - r, the smallest over the spheres), and the
    /// 99th percentile.</summary>
    static (float max, float p99) Gaps(ReadOnlySpan<Vector3> c, ReadOnlySpan<float> r, IReadOnlyList<Vector3> points)
    {
        var g = new float[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            float best = float.MaxValue;
            for (int i = 0; i < c.Length; i++) best = MathF.Min(best, Vector3.Distance(points[k], c[i]) - r[i]);
            g[k] = best;
        }
        if (g.Length == 0) return (0f, 0f);
        Array.Sort(g);
        return (g[^1], g[(int)(0.99 * (g.Length - 1))]);
    }

    static Vector3 RandomOnCapsule(Random rng, float y0, float y1, float R)
    {
        // by area: the side, and the two caps (a sphere's worth)
        double side = 2 * Math.PI * R * (y1 - y0), caps = 4 * Math.PI * R * R;
        if (rng.NextDouble() * (side + caps) < side)
        {
            float f = (float)(rng.NextDouble() * 2 * Math.PI);
            return new Vector3(R * MathF.Cos(f), y0 + (float)rng.NextDouble() * (y1 - y0), R * MathF.Sin(f));
        }
        Vector3 d;
        do d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1); while (d.LengthSquared() > 1f || d.LengthSquared() < 1e-4f);
        d = Vector3.Normalize(d) * R;
        return d.Y >= 0 ? new Vector3(d.X, y1 + d.Y, d.Z) : new Vector3(d.X, y0 + d.Y, d.Z);
    }

    static (Vector3[] c, float[] r) Posed(BodyShape shape, Matrix4x4[] skin)
    {
        var c = new Vector3[shape.Count]; var r = new float[shape.Count]; var scale = new float[shape.BoneCount + 1];
        shape.Pose(skin, scale, c, r);
        return (c, r);
    }

    // ------------------------------------------------------------------ simple shapes

    static void BodyLimb()
    {
        Console.WriteLine("-- a limb (a 5 cm capsule, 40 cm long): spheres inside it, no gaps on its surface");
        const float R = 0.05f;
        var (pos, nrm, tris) = CapsuleMesh(0f, 0.4f, R);
        var shape = BodyShape.Fit(new BodyMesh { Positions = pos, Normals = nrm, Triangles = tris, Skin = TwoBones(pos, 0.2f) });
        var (c, r) = Posed(shape, Identity(2));
        float stick = float.MinValue;
        for (int i = 0; i < shape.Count; i++) stick = MathF.Max(stick, CapsuleSdf(c[i], Vector3.Zero, new Vector3(0f, 0.4f, 0f), R) + r[i]);
        var rng = new Random(1);
        var pts = new List<Vector3>();
        for (int k = 0; k < 4000; k++) pts.Add(RandomOnCapsule(rng, 0f, 0.4f, R));
        var (gap, gap99) = Gaps(c, r, pts);
        Console.WriteLine($"   {shape.Count} spheres (radius {r.Min() * 100:0.0}..{r.Max() * 100:0.0} cm) from {shape.Samples} samples in {shape.FitMs:0.0} ms; " +
                          $"sticks out at most {stick * 1000:0.0} mm; the skin stands off them at most {gap * 1000:0.0} mm (99%: {gap99 * 1000:0.0} mm)");
        Check(shape.Count > 3 && shape.Count < 200, $"a handful of spheres ({shape.Count})");
        Check(stick < 0.002f, $"no sphere sticks out of the limb by more than 2 mm ({stick * 1000:0.00} mm)");
        Check(gap < 0.0075f, $"no gap: the skin is never more than 7.5 mm off the spheres ({gap * 1000:0.0} mm)");
        Check(r.Min() > 0.045f, $"the limb is filled by spheres its own size, not small ones ({r.Min() * 100:0.00} cm)");
    }

    static void BodyBentLimb()
    {
        Console.WriteLine("-- the limb bent 90 degrees, moved, turned and scaled: the spheres follow the skin");
        const float R = 0.05f;
        var (pos, nrm, tris) = CapsuleMesh(0f, 0.4f, R);
        var skin = TwoBones(pos, 0.2f);
        var shape = BodyShape.Fit(new BodyMesh { Positions = pos, Normals = nrm, Triangles = tris, Skin = skin });
        var joint = new Vector3(0f, 0.2f, 0f);
        var bend = Matrix4x4.CreateTranslation(-joint) * Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(joint);
        var world = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromYawPitchRoll(0.7f, 0.3f, -0.2f) * Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var m = new[] { world, bend * world, world };
        var (c, r) = Posed(shape, m);
        // the skin, posed the same way (linear blend skinning, as the renderer does)
        var rng = new Random(2);
        var pts = new List<Vector3>();
        var skinned = new Vector3[pos.Length];
        skin.SkinPositions(m, pos, skinned);
        // points on the posed surface: random points in random triangles, skinned through their corners
        for (int k = 0; k < 6000; k++)
        {
            int t = rng.Next(tris.Length / 3) * 3;
            float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            pts.Add(skinned[tris[t]] * (1f - u - v) + skinned[tris[t + 1]] * u + skinned[tris[t + 2]] * v);
        }
        var (gap, gap99) = Gaps(c, r, pts);
        // inside the bent limb: the two halves as capsules (in world), except where the bend squeezes the skin
        var a0 = Vector3.Transform(Vector3.Zero, world); var j = Vector3.Transform(joint, world); var a1 = Vector3.Transform(new Vector3(0f, 0.4f, 0f), bend * world);
        float stickRigid = float.MinValue, stickJoint = float.MinValue;
        for (int i = 0; i < shape.Count; i++)
        {
            float sd = MathF.Min(CapsuleSdf(c[i], a0, j, 1.5f * R), CapsuleSdf(c[i], j, a1, 1.5f * R)) + r[i];
            bool rigid = shape.Weights[i * 4] > 0.999f;
            if (rigid) stickRigid = MathF.Max(stickRigid, sd); else stickJoint = MathF.Max(stickJoint, sd);
        }
        Console.WriteLine($"   the skin stands off the spheres at most {gap * 1000:0.0} mm (99%: {gap99 * 1000:0.0} mm); spheres stick out of the two halves " +
                          $"by at most {stickRigid * 1000:0.0} mm (on one bone) and {stickJoint * 1000:0.0} mm (at the bend)");
        Check(stickRigid < 0.003f, $"spheres on one bone move with it exactly ({stickRigid * 1000:0.00} mm out)");
        Check(stickJoint < 0.015f, $"spheres at the bend stay within 1.5 cm of the skin ({stickJoint * 1000:0.0} mm out)");
        Check(gap99 < 0.012f, $"the posed skin stays on the spheres: 99% within 12 mm ({gap99 * 1000:0.0} mm)");
        Check(MathF.Abs(r.Max() - 1.5f * shape.Radius.Max()) < 1e-4f, "the radii grow with the skeleton's scale (1.5 x)");
    }

    static void BodyThinParts()
    {
        Console.WriteLine("-- thin parts and sharp edges: a two-sided sheet gets no spheres, a 1 cm slab none that stick out, a 2 cm finger 1 cm ones, a box none past its edges");
        var (gp, gt, _) = ClothGrid(16, 16, 0.3f, 0.3f, lying: true, doubleSided: true);
        var sheet = BodyShape.Fit(new BodyMesh { Positions = gp, Triangles = gt, Skin = TwoBones(gp, 0f, oneBone: true) });
        Check(sheet.Count == 0, $"a two-sided sheet: no spheres ({sheet.Count})");
        var slabHalf = new Vector3(0.15f, 0.005f, 0.15f);
        var (sp, sn, st) = BoxMesh(slabHalf, 16);
        var slabRep = new BodyShape.Report();
        var slab = BodyShape.Fit(new BodyMesh { Positions = sp, Normals = sn, Triangles = st, Skin = TwoBones(sp, 0f, oneBone: true) }, null, slabRep);
        float slabStick = BoxStick(slab, slabHalf);
        if (_bodyVerbose) WorstInBox("slab", slab, slabRep, slabHalf);
        Check(slabStick < 0.002f, $"a 1 cm slab: {slab.Count} spheres (only along its rim, where 1 cm samples can't see it is thinner), none sticking out more than 2 mm ({slabStick * 1000:0.00} mm)");
        var (fp, fn, ft) = CapsuleMesh(0f, 0.08f, 0.01f, seg: 24, capRows: 6, bodyRows: 10);
        var finger = BodyShape.Fit(new BodyMesh { Positions = fp, Normals = fn, Triangles = ft, Skin = TwoBones(fp, 0f, oneBone: true) });
        float rmax = finger.Count > 0 ? finger.Radius.Max() : 0f;
        Check(finger.Count > 0 && rmax < 0.0105f && finger.Radius.Min() > 0.006f, $"a 2 cm finger: {finger.Count} spheres of about 1 cm ({finger.Radius.DefaultIfEmpty().Min() * 100:0.00}..{rmax * 100:0.00} cm)");
        var boxHalf = new Vector3(0.15f, 0.1f, 0.12f);
        var (bp, bn, bt) = BoxMesh(boxHalf, 12);
        var boxRep = new BodyShape.Report();
        var box = BodyShape.Fit(new BodyMesh { Positions = bp, Normals = bn, Triangles = bt, Skin = TwoBones(bp, 0f, oneBone: true) }, null, boxRep);
        float stick = BoxStick(box, boxHalf);
        if (_bodyVerbose) WorstInBox("box", box, boxRep, boxHalf);
        Check(box.Count > 0 && stick < 0.002f, $"a box with sharp edges: {box.Count} spheres, none sticking out of it ({stick * 1000:0.00} mm)");
    }

    /// <summary>Diagnostics: the spheres reaching furthest out of a centred box, and the samples they came from.</summary>
    static void WorstInBox(string what, BodyShape shape, BodyShape.Report rep, Vector3 half)
    {
        var (c, r) = Posed(shape, Identity(shape.BoneCount));
        foreach (int i in Enumerable.Range(0, shape.Count).OrderByDescending(i => { var q = Vector3.Abs(c[i]) + new Vector3(r[i]) - half; return MathF.Max(q.X, MathF.Max(q.Y, q.Z)); }).Take(3))
        {
            var q = Vector3.Abs(c[i]) + new Vector3(r[i]) - half;
            int from = -1;
            for (int k = 0; k < rep.Points.Length; k++)
                if (rep.Candidate[k] && Vector3.DistanceSquared(rep.Points[k] - rep.Normals[k] * rep.Ball[k], c[i]) < 1e-10f) { from = k; break; }
            string src = from < 0 ? "?" : $"sample {rep.Points[from]} normal {rep.Normals[from]}";
            Console.WriteLine($"     {what}: sphere at {c[i]} r {r[i] * 1000:0.0} mm out by {MathF.Max(q.X, MathF.Max(q.Y, q.Z)) * 1000:0.00} mm, from {src}");
        }
    }

    /// <summary>How far the spheres reach out of a centred box (the most; below 0 when all are inside).</summary>
    static float BoxStick(BodyShape shape, Vector3 half)
    {
        var (c, r) = Posed(shape, Identity(shape.BoneCount));
        float stick = float.MinValue;
        for (int i = 0; i < shape.Count; i++)
        {
            var q = Vector3.Abs(c[i]) + new Vector3(r[i]) - half;
            stick = MathF.Max(stick, MathF.Max(q.X, MathF.Max(q.Y, q.Z)));
        }
        return shape.Count == 0 ? 0f : stick;
    }

    static void BodyNestedLayers()
    {
        Console.WriteLine("-- a sleeve 1 cm over an arm (one mesh): the sleeve's spheres are not squeezed by the arm under it");
        var (ap, an, at) = CapsuleMesh(0f, 0.4f, 0.05f);
        var (sp, sn, st) = CapsuleMesh(0f, 0.4f, 0.06f);
        var pos = ap.Concat(sp).ToArray(); var nrm = an.Concat(sn).ToArray();
        var tris = at.Concat(st.Select(i => i + ap.Length)).ToArray();
        var sleeveRep = new BodyShape.Report();
        var shape = BodyShape.Fit(new BodyMesh { Positions = pos, Normals = nrm, Triangles = tris, Skin = TwoBones(pos, 0.2f) }, null, sleeveRep);
        if (_bodyVerbose)
        {
            int arm = 0, armUnder = 0, sleeve = 0, sleeveUnder = 0, sleeveCand = 0; float sleeveBall = 0f;
            for (int k = 0; k < sleeveRep.Points.Length; k++)
            {
                var q = sleeveRep.Points[k];
                float rad = MathF.Sqrt(q.X * q.X + q.Z * q.Z), y = q.Y;
                float dist = y < 0f ? q.Length() : y > 0.4f ? (q - new Vector3(0f, 0.4f, 0f)).Length() : rad;
                if (dist < 0.055f) { arm++; if (sleeveRep.Under[k]) armUnder++; else if (arm - armUnder < 4) Console.WriteLine($"     arm sample not under: {q} normal {sleeveRep.Normals[k]}"); }
                else { sleeve++; if (sleeveRep.Under[k]) sleeveUnder++; if (sleeveRep.Candidate[k]) { sleeveCand++; sleeveBall += sleeveRep.Ball[k]; } }
            }
            Console.WriteLine($"     arm samples {arm} ({armUnder} under the sleeve); sleeve samples {sleeve} ({sleeveUnder} under something, {sleeveCand} with a ball, {sleeveBall / Math.Max(1, sleeveCand) * 100:0.0} cm on average)");
        }
        var (c, r) = Posed(shape, Identity(2));
        var rng = new Random(3);
        var pts = new List<Vector3>();
        for (int k = 0; k < 3000; k++) pts.Add(RandomOnCapsule(rng, 0f, 0.4f, 0.06f));
        var (gap, _) = Gaps(c, r, pts);
        float stick = float.MinValue;
        for (int i = 0; i < shape.Count; i++) stick = MathF.Max(stick, CapsuleSdf(c[i], Vector3.Zero, new Vector3(0f, 0.4f, 0f), 0.06f) + r[i]);
        Console.WriteLine($"   {shape.Count} spheres; the sleeve stands off them at most {gap * 1000:0.0} mm; they stick out of it at most {stick * 1000:0.0} mm");
        Check(gap < 0.0075f, $"the sleeve rests on spheres (at most 7.5 mm off them: {gap * 1000:0.0} mm)");
        Check(stick < 0.002f, $"none sticks out of the sleeve ({stick * 1000:0.00} mm)");
    }

    // ------------------------------------------------------------------ friction on players

    /// <summary>A small cloth patch resting on a 45 degree slope, as a world collider and as a player's: Friction 0.8 alone
    /// holds up to 38.7 degrees, so on the world it slides; a player's shape grips it.</summary>
    static void BodyGripOnSlope()
    {
        Console.WriteLine("-- grip: a patch on a 45 degree slope slides off a world collider but stays on a player");
        float Slide(byte source)
        {
            var (pos, tris, _) = ClothGrid(7, 7, 0.12f, 0.12f, lying: true);
            var mesh = new ClothMesh(pos, tris);
            var s = new ClothSolver(mesh, new ClothSettings { Thickness = 0.01f, Friction = 0.8f, Drag = 0f, Lift = 0f });
            var tilt = Matrix4x4.CreateRotationZ(MathF.PI / 4);
            // the slope: a big box whose top face runs down towards +x
            var box = FleshCollider.Box(Vector3.Transform(new Vector3(0f, -0.25f, 0f), tilt), Quaternion.CreateFromRotationMatrix(tilt), new Vector3(1f, 0.25f, 1f));
            box.Source = source;
            var f = ClothFrameAt(1f / 60f, Matrix4x4.CreateTranslation(0f, 0.0099f, 0f) * tilt);   // already touching: no fall onto it
            f.Colliders.Add(box);
            for (int k = 0; k < 30; k++) s.Step(f);          // settle half a second
            var start = Centroid(s);
            for (int k = 0; k < 120; k++) s.Step(f);         // then two seconds
            return Vector3.Distance(Centroid(s), start);
        }
        float world = Slide(0), player = Slide((byte)Core.Fluid.FluidSim.CategoryBody);
        Console.WriteLine($"   in 2 s it slid {world * 100:0.0} cm on the world collider and {player * 100:0.00} cm on the player's");
        Check(world > 0.2f, $"Friction alone does not hold it at 45 degrees ({world * 100:0.0} cm)");
        Check(player < 0.01f, $"a player's shape grips it ({player * 100:0.00} cm)");
    }

    static Vector3 Centroid(ClothSolver s)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < s.Mesh.ParticleCount; i++) sum += s.X[i];
        return sum / s.Mesh.ParticleCount;
    }

    /// <summary>A patch on top of a turning dome (a player's shape turning on the spot): it turns along with the surface
    /// under it; with the spheres' own velocities left out (as the bones' centres hardly move) it stays behind.</summary>
    static void BodyTurntable()
    {
        Console.WriteLine("-- riding along: a patch on a turning body turns with it (each sphere's own velocity)");
        var (bp, bn, bt) = CapsuleMesh(0f, 0.3f, 0.15f, seg: 48, capRows: 12, bodyRows: 12);
        var shape = BodyShape.Fit(new BodyMesh { Positions = bp, Normals = bn, Triangles = bt, Skin = TwoBones(bp, 0f, oneBone: true) });
        (float turned, float body) Run(bool velocities)
        {
            // a patch 16 cm across on top of the dome (its top at y = 0.45), resting there for a second, then the body turns
            // at 1.5 rad/s for 2 s; how far the patch turned: the line between its left and right edge's middles
            var (pos, tris, _) = ClothGrid(9, 9, 0.16f, 0.16f, lying: true);
            var mesh = new ClothMesh(pos, tris);
            var s = new ClothSolver(mesh, new ClothSettings { Thickness = 0.01f, Friction = 0.8f });
            var f = ClothFrameAt(1f / 60f, Matrix4x4.CreateTranslation(0f, 0.461f, 0f));
            var body = new ClothBody();
            f.Bodies.Add(body); f.BodyCount = 1;
            float angle = 0f, last = 0f, w = 1.5f, start = 0f;
            int left = -1, right = -1;
            // CreateRotationY turns +x towards -z, so an angle around the axis is atan2(-z, x)
            float Heading() { var d = s.X[right] - s.X[left]; return MathF.Atan2(-d.Z, d.X); }
            for (int k = 0; k < 60 + 120; k++)
            {
                if (k >= 60) angle += w / 60f;
                var now = new[] { Matrix4x4.CreateRotationY(angle), Matrix4x4.CreateRotationY(angle), Matrix4x4.CreateRotationY(angle) };
                var before = new[] { Matrix4x4.CreateRotationY(last), Matrix4x4.CreateRotationY(last), Matrix4x4.CreateRotationY(last) };
                body.Set(shape, now, velocities ? before : ReadOnlySpan<Matrix4x4>.Empty, 1f / 60f, (byte)Core.Fluid.FluidSim.CategoryBody);
                last = angle;
                s.Step(f);
                if (k == 0)
                {
                    float bl = float.MaxValue, br = float.MaxValue;
                    for (int i = 0; i < mesh.ParticleCount; i++)
                    {
                        float dl = Vector3.DistanceSquared(s.X[i], new Vector3(-0.08f, 0.461f, 0f)), dr = Vector3.DistanceSquared(s.X[i], new Vector3(0.08f, 0.461f, 0f));
                        if (dl < bl) { bl = dl; left = i; }
                        if (dr < br) { br = dr; right = i; }
                    }
                }
                if (k == 59) start = Heading();
            }
            float turned = Heading() - start;
            // the body turned 3 rad, less than half a turn more than pi: unwrap to the nearest
            while (turned < angle - MathF.PI) turned += 2f * MathF.PI;
            while (turned > angle + MathF.PI) turned -= 2f * MathF.PI;
            return (turned, angle);
        }
        var (with, body1) = Run(true);
        var (without, _) = Run(false);
        Console.WriteLine($"   the body turned {body1:0.00} rad; the patch {with:0.00} rad with the spheres' velocities, {without:0.00} rad without");
        Check(with > 0.8f * body1, $"with them it rides along (at least 80%: {with / body1 * 100:0}%)");
        Check(without < 0.5f * body1, $"without them it would stay behind ({without / body1 * 100:0}%)");
    }

    static void BodyTeleport()
    {
        Console.WriteLine("-- a body that jumps 3 m in a frame (a teleport) does not fling the cloth");
        var (bp, bn, bt) = CapsuleMesh(0f, 0.3f, 0.15f, seg: 32, capRows: 8, bodyRows: 8);
        var shape = BodyShape.Fit(new BodyMesh { Positions = bp, Normals = bn, Triangles = bt, Skin = TwoBones(bp, 0f, oneBone: true) });
        var (pos, tris, _) = ClothGrid(11, 11, 0.4f, 0.4f, lying: true);
        var s = new ClothSolver(new ClothMesh(pos, tris), new ClothSettings());
        var f = ClothFrameAt(1f / 60f, Matrix4x4.CreateTranslation(0f, 0.47f, 0f));
        var body = new ClothBody();
        f.Bodies.Add(body); f.BodyCount = 1;
        var here = Identity(2); var there = new[] { Matrix4x4.CreateTranslation(0.05f, -3f, 0f), Matrix4x4.CreateTranslation(0.05f, -3f, 0f), Matrix4x4.CreateTranslation(0.05f, -3f, 0f) };
        float fastest = 0f;
        for (int k = 0; k < 90; k++)
        {
            if (k < 60) body.Set(shape, here, here, 1f / 60f, 4);
            else if (k == 60) body.Set(shape, there, here, 1f / 60f, 4);   // jumped away
            else if (k == 61) body.Set(shape, here, there, 1f / 60f, 4);   // and back
            else body.Set(shape, here, here, 1f / 60f, 4);
            s.Step(f);
            fastest = MathF.Max(fastest, ClothMaxSpeed(s));
        }
        Check(ClothFinite(s), "finite");
        Check(fastest < 3f, $"the cloth never flies off (fastest point {fastest:0.00} m/s)");
    }

    // ------------------------------------------------------------------ a real avatar (the local test model)

    static void BodyExplore(FleshModel m)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in m.Pos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        Console.WriteLine($"   model: {m.VertexCount} vertices, {m.Tri.Length / 3} triangles, {m.BoneCount} bones, box {lo} .. {hi}");
        for (int b = 0; b < m.BoneCount; b++) Console.WriteLine($"     {b,3} {m.BoneNames[b]} parent {m.Parent[b]} head {m.Heads[b]} tail {m.Tails[b]}");
        // what lies behind a few samples, along the way in
        AvatarShape(m);
        var rep = _avatarReport;
        int cands = rep.Candidate.Count(x => x), withBall = rep.Ball.Count(b => b > 0f);
        Console.WriteLine($"   probe: {rep.Points.Length} samples, {withBall} with a ball, {cands} candidates");
        void Probe(string what, Func<Vector3, bool> where, Func<Vector3, float> score)
        {
            int at = -1; float bs = float.MinValue;
            for (int i = 0; i < rep.Points.Length; i++) if (where(rep.Points[i]) && score(rep.Points[i]) > bs) { bs = score(rep.Points[i]); at = i; }
            if (at < 0) { Console.WriteLine($"   probe {what}: none"); return; }
            var p = rep.Points[at]; var n = rep.Normals[at];
            Console.WriteLine($"   probe {what}: sample {p} normal {n} ball {rep.Ball[at] * 100:0.0} cm candidate {rep.Candidate[at]}");
            var hits = new List<(float along, float lat, float facing)>();
            for (int j = 0; j < rep.Points.Length; j++)
            {
                var d = p - rep.Points[j];
                float along = Vector3.Dot(d, n);
                if (!(along > 0.001f)) continue;
                float lat = MathF.Sqrt(MathF.Max(0f, d.LengthSquared() - along * along));
                if (lat < 0.02f) hits.Add((along, lat, Vector3.Dot(rep.Normals[j], n)));
            }
            foreach (var h in hits.OrderBy(h => h.along).Take(4)) Console.WriteLine($"      behind {h.along * 100:0.0} cm, {h.lat * 100:0.0} cm off the line, facing {h.facing:0.00}");
        }
        Probe("chest front", q => MathF.Abs(q.X) < 0.01f && q.Y > 0.40f && q.Y < 0.44f, q => q.Z);
        Probe("belly front", q => MathF.Abs(q.X) < 0.01f && q.Y > 0.34f && q.Y < 0.38f, q => q.Z);
        Probe("hips side", q => q.Y > 0.30f && q.Y < 0.34f && MathF.Abs(q.Z - 0.02f) < 0.01f && q.X > 0f, q => -MathF.Abs(q.X - 0.06f));
        Probe("head top", q => MathF.Abs(q.X) < 0.01f && MathF.Abs(q.Z - 0.02f) < 0.02f, q => q.Y);
        Probe("upper leg front", q => q.Y > 0.18f && q.Y < 0.22f && q.X > 0.02f && q.X < 0.08f, q => q.Z);
    }

    static bool _bodyVerbose;
    static BodyShape _avatarShape;
    static BodyShape.Report _avatarReport;

    static BodyShape AvatarShape(FleshModel m)
    {
        if (_avatarShape != null) return _avatarShape;
        _avatarReport = new BodyShape.Report();
        _avatarShape = BodyShape.Fit(new BodyMesh { Positions = m.Pos, Normals = m.Nrm, Triangles = m.Tri, Skin = m.Skin, SkinNow = m.Pose(null, Matrix4x4.Identity) }, null, _avatarReport);
        return _avatarShape;
    }

    /// <summary>Random points on the model's surface for these skin matrices (each blended through its corners).</summary>
    static List<Vector3> SurfacePoints(FleshModel m, Matrix4x4[] skin, int count, int seed, out List<int> triOf) => SurfacePoints(m, skin, count, seed, out triOf, out _);

    static List<Vector3> SurfacePoints(FleshModel m, Matrix4x4[] skin, int count, int seed, out List<int> triOf, out List<Vector3> normals)
    {
        var skinned = new Vector3[m.VertexCount];
        m.Skin.SkinPositions(skin, m.Pos, skinned);
        int nt = m.Tri.Length / 3;
        var area = new double[nt];
        double sum = 0;
        for (int t = 0; t < nt; t++)
        {
            var a = m.Pos[m.Tri[3 * t]]; var b = m.Pos[m.Tri[3 * t + 1]]; var c = m.Pos[m.Tri[3 * t + 2]];
            sum += 0.5 * Vector3.Cross(b - a, c - a).Length();
            area[t] = sum;
        }
        var rng = new Random(seed);
        var pts = new List<Vector3>(); triOf = new List<int>(); normals = new List<Vector3>();
        for (int k = 0; k < count; k++)
        {
            int t = Array.BinarySearch(area, rng.NextDouble() * sum);
            if (t < 0) t = ~t;
            t = Math.Min(t, nt - 1);
            float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
            if (u + v > 1f) { u = 1f - u; v = 1f - v; }
            var ta = skinned[m.Tri[3 * t]]; var tb = skinned[m.Tri[3 * t + 1]]; var tc = skinned[m.Tri[3 * t + 2]];
            pts.Add(ta * (1f - u - v) + tb * u + tc * v);
            triOf.Add(t);
            var fn = Vector3.Cross(tb - ta, tc - ta);
            normals.Add(fn.LengthSquared() > 1e-20f ? Vector3.Normalize(fn) : Vector3.Zero);
        }
        return pts;
    }

    /// <summary>Whether a triangle is on a part thick enough for spheres: its corners' nearest samples were candidates.</summary>
    static bool[] ThickTriangles(FleshModel m, BodyShape.Report rep) => ThickTriangles(m, rep, out _);

    static bool[] ThickTriangles(FleshModel m, BodyShape.Report rep, out bool[] thickV)
    {
        // nearest sample of each vertex, through a coarse grid
        const float cell = 0.02f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < rep.Points.Length; i++)
        {
            var p = rep.Points[i];
            var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(i);
        }
        thickV = new bool[m.VertexCount];
        for (int v = 0; v < m.VertexCount; v++)
        {
            var p = m.Pos[v];
            int x = (int)MathF.Floor(p.X / cell), y = (int)MathF.Floor(p.Y / cell), z = (int)MathF.Floor(p.Z / cell);
            float best = float.MaxValue; int bi = -1;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                    foreach (int i in l)
                    {
                        if (Vector3.Dot(rep.Normals[i], m.Nrm[v]) < 0.5f) continue;   // the same side
                        float d = Vector3.DistanceSquared(rep.Points[i], p);
                        if (d < best) { best = d; bi = i; }
                    }
            thickV[v] = bi >= 0 && rep.Candidate[bi];
        }
        int nt = m.Tri.Length / 3;
        var thick = new bool[nt];
        for (int t = 0; t < nt; t++) thick[t] = thickV[m.Tri[3 * t]] && thickV[m.Tri[3 * t + 1]] && thickV[m.Tri[3 * t + 2]];
        return thick;
    }

    static void BodyAvatar(FleshModel m)
    {
        Console.WriteLine("-- the test avatar: spheres inside its skin, few gaps on the parts thick enough, in the bind pose and posed");
        var shape = AvatarShape(m);
        var rep = _avatarReport;
        int cand = rep.Candidate.Count(x => x);
        Console.WriteLine($"   {shape.Count} spheres (radius {shape.Radius.Min() * 100:0.0}..{shape.Radius.Max() * 100:0.0} cm) from {shape.Samples} samples ({cand} thick enough), " +
                          $"tolerance {shape.ToleranceUsed * 1000:0.0} mm, {shape.Dropped} left out, fitted in {shape.FitMs:0} ms");
        Check(shape.Count > 100 && shape.Count <= 2500, $"a few hundred to a few thousand spheres ({shape.Count})");
        Check(shape.FitMs < 10000, $"fitted within 10 s on one core ({shape.FitMs:0} ms)");
        var thick = ThickTriangles(m, rep);
        Measure("bind pose", m, shape, m.Pose(null, Matrix4x4.Identity), thick, 0.012f, 0.006f);

        // bend the elbows and knees (whatever the model calls them) and lift the arms
        var rot = new Dictionary<int, Quaternion>();
        for (int b = 0; b < m.BoneCount; b++)
        {
            string n = m.BoneNames[b].ToLowerInvariant();
            if (n.Contains("wing")) continue;   // the wings' own "lowerarm" bones: folding them pushes them through the body
            if (n.Contains("forearm") || n.Contains("lowerarm") || n.Contains("lower_arm") || n.Contains("elbow")) rot[b] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
            else if (n.Contains("calf") || n.Contains("shin") || n.Contains("lowerleg") || n.Contains("lower_leg") || n.Contains("knee")) rot[b] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f);
        }
        Console.WriteLine($"   bending {rot.Count} bones ({string.Join(", ", rot.Keys.Select(b => m.BoneNames[b]))})");
        Measure("posed", m, shape, m.Pose(rot, Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(1f, 0f, -2f)), thick, 0.02f, 0.015f);
    }

    /// <summary>Gaps (thick parts only) and how far spheres stick out of the skin, for these skin matrices.</summary>
    static void Measure(string what, FleshModel m, BodyShape shape, Matrix4x4[] skin, bool[] thickTri, float gapLimit, float stickLimit)
    {
        var (c, r) = Posed(shape, skin);
        var pts = SurfacePoints(m, skin, 60000, 7, out var triOf, out var pn);
        // only skin you can see: going straight out from a skin point, the first surface met (within 5 cm) must not face
        // the same way, or the point is inside another piece of the mesh (overlapping parts, a tuft over the body)
        var skinnedNow = new Vector3[m.VertexCount];
        m.Skin.SkinPositions(skin, m.Pos, skinnedNow);
        var bvh = TriangleBvh.Build(skinnedNow, m.Tri);
        var hidden = new bool[pts.Count];
        var hitList = new List<int>();
        for (int k = 0; k < pts.Count; k++) hidden[k] = Hidden(bvh, pts[k], pn[k], 0.05f, hitList);
        var thickPts = new List<Vector3>();
        for (int k = 0; k < pts.Count; k++) if (thickTri[triOf[k]] && !hidden[k]) thickPts.Add(pts[k]);
        var (gap, gap99) = Gaps(c, r, thickPts);
        // sticking out: the deepest any skin point facing away from a sphere is inside it (r - |q - c|), per sphere; the
        // 98th percentile (skin facing the sphere is another part's, overlapping this one: a fur tuft, a limb posed into it)
        var depth = new float[shape.Count];
        var grid = new Dictionary<(int, int, int), List<int>>();
        const float cell = 0.05f;
        for (int k = 0; k < pts.Count; k++)
        {
            var p = pts[k];
            var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(k);
        }
        var worstAt = new int[shape.Count];
        for (int i = 0; i < shape.Count; i++)
        {
            int x0 = (int)MathF.Floor((c[i].X - r[i]) / cell), x1 = (int)MathF.Floor((c[i].X + r[i]) / cell);
            int y0 = (int)MathF.Floor((c[i].Y - r[i]) / cell), y1 = (int)MathF.Floor((c[i].Y + r[i]) / cell);
            int z0 = (int)MathF.Floor((c[i].Z - r[i]) / cell), z1 = (int)MathF.Floor((c[i].Z + r[i]) / cell);
            float d = 0f; int at = -1;
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                if (grid.TryGetValue((x, y, z), out var l))
                    foreach (int k in l)
                    {
                        if (hidden[k]) continue;
                        var off = pts[k] - c[i];
                        float dist = off.Length();
                        float e = r[i] - dist;
                        if (!(e > d)) continue;
                        if (dist > 1e-6f && Vector3.Dot(pn[k], off) < 0.3f * dist) continue;
                        d = e; at = k;
                    }
            depth[i] = d; worstAt[i] = at;
        }
        if (_bodyVerbose && what == "bind pose" && _avatarReport != null)
        {
            // the worst sphere: the sample it came from, and why the skin poking into it did not bound its ball
            int wi = Enumerable.Range(0, shape.Count).OrderByDescending(i => depth[i]).First();
            var rep = _avatarReport;
            int src = -1;
            for (int k = 0; k < rep.Points.Length; k++)
                if (rep.Candidate[k] && Vector3.DistanceSquared(rep.Points[k] - rep.Normals[k] * rep.Ball[k], c[wi]) < 1e-8f) { src = k; break; }
            var x = pts[worstAt[wi]]; var xn = pn[worstAt[wi]];
            Console.WriteLine($"     worst: sphere {c[wi]} r {r[wi] * 100:0.00} cm from sample {(src < 0 ? "?" : rep.Points[src] + " normal " + rep.Normals[src])}; skin {x} normal {xn} inside by {depth[wi] * 1000:0.0} mm");
            if (src >= 0)
            {
                var p0 = rep.Points[src]; var n0 = rep.Normals[src];
                for (int j = 0; j < rep.Points.Length; j++)
                {
                    if (Vector3.DistanceSquared(rep.Points[j], x) > 0.012f * 0.012f) continue;
                    var d = p0 - rep.Points[j];
                    float dn = Vector3.Dot(d, n0), qd = Vector3.Dot(rep.Normals[j], d), l = d.Length();
                    string why = !(dn > 0f) ? "above p's plane" : $"bounds at {d.LengthSquared() / (2f * dn) * 100:0.00} cm unless on a layer under another (qd {qd * 1000:0.0} mm of {l * 1000:0.0})";
                    Console.WriteLine($"       sample {rep.Points[j]} normal {rep.Normals[j]} {Vector3.Distance(rep.Points[j], x) * 1000:0.0} mm from the skin point: {why}");
                }
            }
        }
        if (_bodyVerbose)
            foreach (int i in Enumerable.Range(0, shape.Count).OrderByDescending(i => depth[i]).Take(8))
            {
                int k = worstAt[i];
                if (k < 0) continue;
                int tv = m.Tri[3 * triOf[k]];
                string pb = m.BoneNames[Math.Min(m.Skin.VertexBones[tv * 4], m.BoneCount - 1)];
                float facing = Vector3.Dot(pn[k], Vector3.Normalize(pts[k] - c[i]));
                Console.WriteLine($"     sphere on {m.BoneNames[Math.Min(shape.Bones[i * 4], m.BoneCount - 1)]} r {r[i] * 100:0.0} cm: skin of {pb} {depth[i] * 1000:0.0} mm inside it, facing {facing:0.00} (1: away from the sphere)");
            }
        var sorted = depth.OrderBy(x => x).ToArray();
        float stick98 = sorted[(int)(0.98 * (sorted.Length - 1))], stickMax = sorted[^1];
        Console.WriteLine($"   {what}: {hidden.Count(h => h)} of the {pts.Count} skin points are hidden inside other parts; on the thick parts ({thickPts.Count} seen) the skin stands off the spheres at most {gap * 1000:0.0} mm " +
                          $"(99%: {gap99 * 1000:0.0} mm); spheres reach outside the skin by up to {stickMax * 1000:0.0} mm (98% of them: {stick98 * 1000:0.0} mm)");
        Check(gap99 < gapLimit, $"{what}: 99% of the thick skin within {gapLimit * 1000:0} mm of the spheres ({gap99 * 1000:0.0} mm)");
        Check(stick98 < stickLimit, $"{what}: 98% of the spheres within {stickLimit * 1000:0} mm of being inside the skin ({stick98 * 1000:0.0} mm)");
    }

    /// <summary>Whether a skin point is inside another piece of the mesh: the first triangle straight out from it (within
    /// reach) faces the same way, so the ray leaves a solid there.</summary>
    static bool Hidden(TriangleBvh bvh, Vector3 x, Vector3 n, float reach, List<int> hits)
    {
        if (!(n.LengthSquared() > 0.25f)) return false;
        var o = x + n * 1e-4f; var e = x + n * reach;
        hits.Clear();
        bvh.Query(Vector3.Min(o, e) - new Vector3(1e-4f), Vector3.Max(o, e) + new Vector3(1e-4f), hits);
        float first = float.MaxValue; bool exits = false;
        foreach (int t in hits)
        {
            var a = bvh.A[t]; var e1 = bvh.B[t] - a; var e2 = bvh.C[t] - a;
            var pv = Vector3.Cross(n, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-14f) continue;
            float id = 1f / det;
            var tv = o - a;
            float u = Vector3.Dot(tv, pv) * id;
            if (u < -1e-4f || u > 1.0001f) continue;   // a hair over the edges: no ray slips through between two triangles
            var qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(n, qv) * id;
            if (v < -1e-4f || u + v > 1.0001f) continue;
            float s = Vector3.Dot(e2, qv) * id;
            if (!(s > 0f) || s > reach || s >= first) continue;
            first = s;
            exits = Vector3.Dot(Vector3.Cross(e1, e2), n) > 0f;
        }
        return exits;
    }

    static int MainBone(FleshModel m, int v)
    {
        int best = 0;
        for (int q = 1; q < 4; q++) if (m.Skin.VertexWeights[v * 4 + q] > m.Skin.VertexWeights[v * 4 + best]) best = q;
        return m.Skin.VertexBones[v * 4 + best];
    }

    /// <summary>The avatar lying on its back on a floor, a blanket dropped over it: it comes to rest on the body (not inside
    /// it), and costs a few ms a frame.</summary>
    static void BodyBlanketOnAvatar(FleshModel m)
    {
        Console.WriteLine("-- a blanket dropped on the avatar lying on its back: it rests on the skin you see");
        var shape = AvatarShape(m);
        ThickTriangles(m, _avatarReport, out var thickV);
        // which way the model faces: its toes point forward (the lowest points, compared with the whole body's middle)
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in m.Pos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        float feetZ = 0f; int feet = 0;
        foreach (var p in m.Pos) if (p.Y < lo.Y + 0.03f * (hi.Y - lo.Y)) { feetZ += p.Z; feet++; }
        float midZ = 0.5f * (lo.Z + hi.Z);
        bool facesPlusZ = feet > 0 && feetZ / feet > midZ;
        // lie it down: front up, at a height where its back is on the floor (y = 0)
        var lie = Matrix4x4.CreateRotationX(facesPlusZ ? -MathF.PI / 2 : MathF.PI / 2);
        // its back on the floor: the tail (straight back from the hips, so straight down once it lies on its back) and the
        // wings go through the floor
        float back = float.MaxValue;
        for (int v = 0; v < m.VertexCount; v++)
        {
            string bn = m.BoneNames[Math.Min(MainBone(m, v), m.BoneCount - 1)].ToLowerInvariant();
            if (bn.Contains("tail") || bn.Contains("wing")) continue;
            back = MathF.Min(back, Vector3.Transform(m.Pos[v], lie).Y);
        }
        var center = Vector3.Transform(0.5f * (lo + hi), lie);
        var world = lie * Matrix4x4.CreateTranslation(-center.X, -back, -center.Z);
        var skin = m.Pose(null, world);
        // the body's top surface: for the depth check
        var skinned = new Vector3[m.VertexCount];
        m.Skin.SkinPositions(skin, m.Pos, skinned);
        var nrm = new Vector3[m.VertexCount];
        for (int v = 0; v < m.VertexCount; v++) nrm[v] = Vector3.Normalize(Vector3.TransformNormal(m.Nrm[v], m.Skin.VertexMatrix(skin, v)));
        float top = skinned.Max(p => p.Y);

        var (pos, tris, _) = ClothGrid(31, 49, 1.0f, 1.6f, lying: true);
        var mesh = new ClothMesh(pos, tris);
        var settings = new ClothSettings { Thickness = 0.01f, Friction = 0.8f, Weight = 400f };
        var s = new ClothSolver(mesh, settings);
        var f = ClothFrameAt(1f / 60f, Matrix4x4.CreateTranslation(0f, top + 0.05f, 0f));
        f.Colliders.Add(FleshCollider.Plane(Vector3.Zero, Vector3.UnitY));
        var body = new ClothBody();
        f.Bodies.Add(body); f.BodyCount = 1;
        int frames = 240;
        double pairsMs = 0, contactsMs = 0, bodiesMs = 0, stepMs = 0;
        for (int k = 0; k < frames; k++)
        {
            body.Set(shape, skin, skin, 1f / 60f, (byte)Core.Fluid.FluidSim.CategoryBody);
            s.Step(f);
            if (k >= frames / 2) { pairsMs += s.PairsMs; contactsMs += s.ContactsMs; bodiesMs += s.BodiesMs; stepMs += s.LastStepMs; }
        }
        // the last 2 s: settled, and the code warmed up
        double ms = stepMs / (frames - frames / 2);
        pairsMs /= frames - frames / 2; contactsMs /= frames - frames / 2; bodiesMs /= frames - frames / 2;
        // how deep cloth points are inside the skin: the nearest skin vertex (on the same side) and its normal
        int near = 0, inside = 0; float deepest = 0f;
        for (int i = 0; i < mesh.ParticleCount; i++)
        {
            var p = s.X[i];
            if (p.Y < 0.02f) continue;   // on the floor
            int best = -1; float bd = float.MaxValue;
            for (int v = 0; v < m.VertexCount; v++)
            {
                float d = Vector3.DistanceSquared(p, skinned[v]);
                if (d < bd) { bd = d; best = v; }
            }
            if (bd > 0.05f * 0.05f) continue;   // not on the body
            if (!thickV[best]) continue;        // a part too thin for spheres (the wings)
            near++;
            float depth = -Vector3.Dot(p - skinned[best], nrm[best]);
            if (depth > 0.01f)
            {
                inside++;
                if (_bodyVerbose) Console.WriteLine($"     inside by {depth * 1000:0.0} mm at {p} near {m.BoneNames[Math.Min(MainBone(m, best), m.BoneCount - 1)]}");
            }
            deepest = MathF.Max(deepest, depth);
        }
        // the same blanket on the floor alone, for what the body costs
        var s0 = new ClothSolver(new ClothMesh(pos, tris), settings);
        var f0 = ClothFrameAt(1f / 60f, Matrix4x4.CreateTranslation(0f, top + 0.05f, 0f));
        f0.Colliders.Add(FleshCollider.Plane(Vector3.Zero, Vector3.UnitY));
        double pairs0 = 0, contacts0 = 0, step0 = 0;
        for (int k = 0; k < frames; k++)
        {
            s0.Step(f0);
            if (k >= frames / 2) { pairs0 += s0.PairsMs; contacts0 += s0.ContactsMs; step0 += s0.LastStepMs; }
        }
        double ms0 = step0 / (frames - frames / 2);
        pairs0 /= frames - frames / 2; contacts0 /= frames - frames / 2;
        Console.WriteLine($"   the same blanket on the floor alone: {ms0:0.00} ms a frame (pairs {pairs0:0.00} ms, contacts {contacts0:0.00} ms); with the body {s.PairCount} pairs: " +
                          $"finding them {pairsMs:0.00} ms (posing and pairing the body {bodiesMs:0.00} ms), the contacts {contactsMs:0.00} ms a frame (the last 2 s)");
        Console.WriteLine($"   after {frames / 60} s: {near} points lie on the body, {inside} of them more than 1 cm inside the skin (deepest {deepest * 1000:0.0} mm); " +
                          $"{s.BodySpheres} body spheres near; {ms:0.00} ms a frame ({s.StepsLastFrame} steps), fastest point {ClothMaxSpeed(s):0.00} m/s");
        Check(ClothFinite(s), "finite");
        Check(near > 100, $"the blanket lies on the body ({near} points within 5 cm of its skin, the wings and other thin parts left out)");
        Check(inside <= near / 100, $"at most 1% of them more than 1 cm inside the skin ({inside})");
        Check(ClothMaxSpeed(s) < 0.3f, $"settling down (fastest point {ClothMaxSpeed(s):0.00} m/s)");
    }
}
