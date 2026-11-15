using System.Diagnostics;
using System.Numerics;
using Softbody.Core;

namespace Softbody.Tests;

// 0.28.0: offline tests for mirrored dabs, dabs limited to one material, and the paint layer's mirror planes and
// rest-mesh queries (Core/PaintLayer.cs). The test cube's six faces have their own vertices, so a face can be a material.
static partial class Program
{
    static void MirrorPaintTests()
    {
        MirrorDabUnion();
        MirrorCentreLine();
        MirrorAndMaterialExactness();
        MaterialMask();
        MaterialQueries();
        RestQueriesUnderPose();
        MirrorPlanes();
    }

    /// <summary>Per-face materials for the test cube: the +X face 1, the -X face 2, the others 0.</summary>
    static int[] FaceMaterials(TestMesh m, int subdiv)
    {
        int perFace = 2 * subdiv * subdiv;
        var mats = new int[m.Tri.Length / 3];
        for (int t = 0; t < mats.Length; t++) { int face = t / perFace; mats[t] = face == 0 ? 1 : face == 1 ? 2 : 0; }
        return mats;
    }

    static int[] VertexMaterials(TestMesh m, int[] triMats)
    {
        var vm = new int[m.VertexCount];
        for (int t = 0; t < triMats.Length; t++) { vm[m.Tri[3 * t]] = triMats[t]; vm[m.Tri[3 * t + 1]] = triMats[t]; vm[m.Tri[3 * t + 2]] = triMats[t]; }
        return vm;
    }

    static Vector3 FlipX(Vector3 p) => new(-p.X, p.Y, p.Z);

    /// <summary>The definition, vertex by vertex: each dab in order at the distance to its nearer centre, only on its material.</summary>
    static Vector4[] ReferencePaint(TestMesh m, Vector4[] baseCols, int[] vertexMat, Dab[] dabs)
    {
        var col = (Vector4[])baseCols.Clone();
        foreach (var d in dabs)
            for (int v = 0; v < m.VertexCount; v++)
            {
                if (d.Material >= 0 && vertexMat[v] != d.Material) continue;
                float d2 = Vector3.DistanceSquared(m.Pos[v], d.Position);
                if (d.Mirrored) d2 = MathF.Min(d2, Vector3.DistanceSquared(m.Pos[v], d.MirrorPosition));
                if (!(d2 < d.Radius * d.Radius)) continue;
                float w = d.Strength * PaintLayer.Falloff(MathF.Sqrt(d2) / d.Radius, d.Hardness);
                if (w > 0f) col[v] += (d.Color - col[v]) * w;
            }
        return col;
    }

    static float MaxDiff(ReadOnlySpan<Vector4> a, ReadOnlySpan<Vector4> b)
    {
        float worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var d = Vector4.Abs(a[i] - b[i]);
            worst = MathF.Max(worst, MathF.Max(MathF.Max(d.X, d.Y), MathF.Max(d.Z, d.W)));
        }
        return worst;
    }

    /// <summary>Random dabs: some mirrored across x = 0 (the mirror centre a little off, as snapping leaves it), some limited to a material.</summary>
    static Dab[] MixedDabs(TestMesh m, int count, int seed, float mirroredShare, float materialShare, float blurShare = 0f, float rMin = 0.02f, float rMax = 0.12f)
    {
        var rng = new Random(seed);
        var d = new Dab[count];
        for (int i = 0; i < count; i++)
        {
            var p = m.Pos[rng.Next(m.VertexCount)] + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.01f;
            var c = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
            bool mirrored = rng.NextDouble() < mirroredShare;
            var mp = FlipX(p) + new Vector3((float)rng.NextDouble() - 0.5f, 0f, (float)rng.NextDouble() - 0.5f) * 0.004f;
            int mat = rng.NextDouble() < materialShare ? rng.Next(3) : -1;
            bool blur = rng.NextDouble() < blurShare;
            d[i] = new Dab(p, rMin + (float)rng.NextDouble() * (rMax - rMin), c, 0.2f + 0.8f * (float)rng.NextDouble(), (float)rng.NextDouble(), blur, mirrored, mp, mat).Sanitized();
        }
        return d;
    }

    static void MirrorDabUnion()
    {
        Console.WriteLine("TEST 0.28.0 mirrored dabs: each vertex is painted once, at its distance to the nearer of the two centres");
        var m = Cube(30);
        var baseCols = GradientColors(m);
        var dabs = MixedDabs(m, 400, 41, mirroredShare: 0.7f, materialShare: 0f);
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        layer.Update(dabs);
        var reference = ReferencePaint(m, baseCols, new int[m.VertexCount], dabs);
        float diff = MaxDiff(layer.Colors, reference);
        var brute = new PaintLayer(m.Pos, baseCols, m.Tri) { BruteForce = true };
        brute.Update(dabs);
        bool same = SameColors(layer.Colors, brute.Colors, out _);
        int mirrored = dabs.Count(x => x.Mirrored);
        Console.WriteLine($"  {m.VertexCount} verts, {dabs.Length} dabs ({mirrored} mirrored): largest difference from the definition {diff:0.#######}; grid = brute force: {same}");
        Check(diff < 1e-5f, "mirrored dabs match the definition (nearer centre, strength x falloff), vertex by vertex");
        Check(same, "the grid's two passes give exactly the brute-force colours (no vertex painted twice, none missed)");
        // both halves really paint: the +X and -X faces get the same change from dabs mirrored across x = 0
        var one = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        var left = new Vector3(-0.3f, 0.3f, 0.05f);
        one.Update(new[] { new Dab(left, 0.08f, new Vector4(0, 0, 0, 1), 1f, 0.5f, false, true, FlipX(left), -1) });
        int painted = 0, matched = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (one.Colors[v] == Vector4.One) continue;
            painted++;
            int twin = Array.FindIndex(m.Pos, q => Vector3.DistanceSquared(q, FlipX(m.Pos[v])) < 1e-10f);
            if (twin >= 0 && Vector4.Distance(one.Colors[twin], one.Colors[v]) < 1e-5f) matched++;
        }
        Console.WriteLine($"  one black dab on the -X face mirrored to the +X face: {painted} vertices painted, {matched} match their mirror twin");
        Check(painted > 20 && matched == painted, "a mirrored dab paints both sides alike");
    }

    static void MirrorCentreLine()
    {
        Console.WriteLine("TEST 0.28.0 the centre line: a dab on the mirror plane is painted once, not twice");
        var m = Cube(30);
        var on = new Vector3(0f, 0.6f, 0f);
        var plain = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        plain.Update(new[] { new Dab(on, 0.1f, new Vector4(0, 0, 0, 1), 0.5f, 0f) });
        var mirroredOn = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        mirroredOn.Update(new[] { new Dab(on, 0.1f, new Vector4(0, 0, 0, 1), 0.5f, 0f, false, true, FlipX(on), -1) });
        Check(SameColors(plain.Colors, mirroredOn.Colors, out _), "a mirrored dab whose two centres meet on the plane gives exactly the plain dab's colours");

        // 2 cm off the plane: the spheres overlap; the vertex on the plane is 2 cm from both centres and gets one dab's worth
        var near = new Vector3(0.02f, 0.6f, 0f);
        var both = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        both.Update(new[] { new Dab(near, 0.1f, new Vector4(0, 0, 0, 1), 0.5f, 0f, false, true, FlipX(near), -1) });
        int centre = Array.FindIndex(m.Pos, q => Vector3.DistanceSquared(q, on) < 1e-10f);
        float once = 1f - 0.5f * PaintLayer.Falloff(0.2f, 0f); // white moved towards black by w once: 1 - w
        float twice = once * once;                              // by w twice: (1 - w)^2
        float got = both.Colors[centre].X;
        Console.WriteLine($"  dab 2 cm off the plane (r 10 cm, strength 0.5): centre-line vertex {got:0.######}; once {once:0.######}, twice would be {twice:0.######}");
        Check(centre >= 0 && MathF.Abs(got - once) < 1e-6f, "where the two halves overlap, a vertex gets one dab's worth (the nearer centre), not two");
    }

    static void MirrorAndMaterialExactness()
    {
        Console.WriteLine("TEST 0.28.0 mirrored and per-material dabs: appends, regional edits and the grid stay bit-identical to a full recompute");
        const int sub = 30;
        var m = Cube(sub);
        var mats = FaceMaterials(m, sub);
        var baseCols = GradientColors(m);
        var dabs = MixedDabs(m, 1200, 43, mirroredShare: 0.5f, materialShare: 0.4f);
        var inc = new PaintLayer(m.Pos, baseCols, m.Tri, mats);
        var rng = new Random(13);
        int n = 0, appends = 0;
        while (n < dabs.Length) { n = Math.Min(dabs.Length, n + 1 + rng.Next(25)); if (inc.Update(dabs.AsSpan(0, n)).Kind == PaintLayer.UpdateKind.Appended) appends++; }
        var full = new PaintLayer(m.Pos, baseCols, m.Tri, mats);
        full.RecomputeAll(dabs);
        bool sameInc = SameColors(inc.Colors, full.Colors, out _);
        float defDiff = MaxDiff(full.Colors, ReferencePaint(m, baseCols, VertexMaterials(m, mats), dabs));
        Console.WriteLine($"  {dabs.Length} dabs ({dabs.Count(d => d.Mirrored)} mirrored, {dabs.Count(d => d.Material >= 0)} on one material) in {appends} appends: " +
                          $"identical {sameInc}; largest difference from the definition {defDiff:0.#######}");
        Check(sameInc && appends > 30, "appending mirrored and per-material dabs is bit-identical to a full recompute");
        Check(defDiff < 1e-5f, "per-material dabs only change their material's vertices, as the definition says");

        // edits like the brush makes: erase (remove), split a mirrored dab into one half, change a dab's material, insert
        var list = dabs.ToList();
        bool allSame = true, allRegional = true;
        for (int step = 0; step < 40; step++)
        {
            int op = step % 4, i = rng.Next(list.Count);
            if (op == 1) { int j = list.FindIndex(i, x => x.Mirrored); i = j >= 0 ? j : list.FindIndex(x => x.Mirrored); } // a mirrored dab to split
            var d = list[i];
            if (op == 0) list.RemoveAt(i);
            else if (op == 1) list[i] = new Dab(d.MirrorPosition, d.Radius, d.Color, d.Strength, d.Hardness, false, false, default, d.Material);
            else if (op == 2) list[i] = new Dab(d.Position, d.Radius, d.Color, d.Strength, d.Hardness, false, !d.Mirrored, FlipX(d.Position), (d.Material + 2) % 4 - 1);
            else list.Insert(i, MixedDabs(m, 1, 300 + step, 0.5f, 0.5f)[0]);
            var r = inc.Update(list.ToArray());
            var refl = new PaintLayer(m.Pos, baseCols, m.Tri, mats);
            refl.RecomputeAll(list.ToArray());
            bool same = SameColors(inc.Colors, refl.Colors, out int at);
            if (!same || r.Kind != PaintLayer.UpdateKind.Regional)
                Console.WriteLine($"  edit {step} (op {op}, {d}): {r}; identical {same}{(same ? "" : $" (vertex {at}: {inc.Colors[at]} vs {refl.Colors[at]})")}");
            allSame &= same;
            allRegional &= r.Kind == PaintLayer.UpdateKind.Regional;
        }
        Check(allSame && allRegional, "40 edits (erase, split a mirrored dab, flip mirror and material, insert) take the regional path and match a full recompute bit for bit");

        // the grid against brute force, with mirrored blur dabs on one material too
        var withBlur = MixedDabs(m, 600, 47, mirroredShare: 0.5f, materialShare: 0.4f, blurShare: 0.25f, rMin: 0.01f, rMax: 0.25f);
        var grid = new PaintLayer(m.Pos, baseCols, m.Tri, mats);
        var brute = new PaintLayer(m.Pos, baseCols, m.Tri, mats) { BruteForce = true };
        grid.Update(withBlur); brute.Update(withBlur);
        var edited = withBlur.ToList(); edited.RemoveAt(100); edited.RemoveAt(5); edited.Insert(50, withBlur[7]);
        grid.Update(edited.ToArray()); brute.Update(edited.ToArray());
        bool sameGrid = SameColors(grid.Colors, brute.Colors, out _);
        Console.WriteLine($"  grid vs brute force with {withBlur.Count(d => d.Blur)} blur dabs ({withBlur.Count(d => d.Blur && d.Mirrored)} mirrored, {withBlur.Count(d => d.Blur && d.Material >= 0)} on one material): identical {sameGrid}");
        Check(sameGrid, "grid and brute force are bit-identical for mirrored and per-material dabs, blur included");
    }

    static void MaterialMask()
    {
        Console.WriteLine("TEST 0.28.0 a dab on one material: only that material's vertices change, and its blur averages only that material");
        const int sub = 20;
        var m = Cube(sub);
        var mats = FaceMaterials(m, sub);
        var vm = VertexMaterials(m, mats);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri, mats);
        Check(layer.MaterialCount == 3, $"three materials found (MaterialCount {layer.MaterialCount})");
        layer.Update(new[] { new Dab(new Vector3(0, 0.3f, 0), 1f, new Vector4(0, 0, 0, 1), 1f, 1f, false, false, default, 1) });
        int black = 0, wrong = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            bool isBlack = layer.Colors[v] == new Vector4(0, 0, 0, 1);
            if (isBlack) black++;
            if (isBlack != (vm[v] == 1) || (!isBlack && layer.Colors[v] != Vector4.One)) wrong++;
        }
        Console.WriteLine($"  a black dab covering the cube, on material 1 (the +X face): {black} vertices black, {wrong} wrong");
        Check(black == (sub + 1) * (sub + 1) && wrong == 0, "every +X face vertex is black, every other vertex untouched");

        // blur: the -X face red, the rest blue; a big blur on material 2 keeps the -X face red, a blur on any material mixes its edges
        var cols = m.Pos.Select((p, v) => vm[v] == 2 ? new Vector4(1, 0, 0, 1) : new Vector4(0, 0, 1, 1)).ToArray();
        var big = new Dab(new Vector3(-0.3f, 0.3f, 0f), 0.5f, Vector4.One, 1f, 1f, true, false, default, 2);
        var onMat = new PaintLayer(m.Pos, cols, m.Tri, mats); onMat.Update(new[] { big });
        var any = new PaintLayer(m.Pos, cols, m.Tri, mats); any.Update(new[] { new Dab(big.Position, big.Radius, Vector4.One, 1f, 1f, true) });
        float driftOn = 0, driftAny = 0; int changedOther = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (vm[v] == 2) { driftOn = MathF.Max(driftOn, MathF.Abs(onMat.Colors[v].Z)); driftAny = MathF.Max(driftAny, MathF.Abs(any.Colors[v].Z)); }
            else if (onMat.Colors[v] != cols[v]) changedOther++;
        }
        Console.WriteLine($"  blur on material 2: most blue in the red face {driftOn:0.####} (blur on any material: {driftAny:0.####}); other faces changed: {changedOther}");
        Check(driftOn < 1e-6f && driftAny > 0.05f && changedOther == 0, "a blur on one material neither takes colour from nor gives it to the others");
    }

    static void MaterialQueries()
    {
        Console.WriteLine("TEST 0.28.0 rays and closest points on one material: the other materials are as if not there");
        const int sub = 10;
        var m = Cube(sub);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri, FaceMaterials(m, sub));
        var o = new Vector3(-2f, 0.3f, 0.05f);
        bool hAny = layer.Raycast(o, Vector3.UnitX, 5f, out var a);
        bool h1 = layer.Raycast(o, Vector3.UnitX, 5f, 1, out var b);
        bool h0 = layer.Raycast(o, Vector3.UnitX, 5f, 0, out _);
        Console.WriteLine($"  ray along +X: any {a.T:0.###} (material {layer.TriangleMaterial(a.Triangle)}), material 1 {b.T:0.###} (front {b.FrontFace}), material 0 hit {h0}");
        Check(hAny && MathF.Abs(a.T - 1.7f) < 1e-4f && layer.TriangleMaterial(a.Triangle) == 2, "unlimited, the ray stops at the -X face (material 2)");
        Check(h1 && MathF.Abs(b.T - 2.3f) < 1e-4f && layer.TriangleMaterial(b.Triangle) == 1 && !h0,
              "on material 1 it goes through the -X face to the +X face; on material 0 (top, bottom, front, back) it meets nothing");
        bool c2 = layer.ClosestPoint(new Vector3(0.25f, 0.3f, 0f), 0.6f, 2, out var c);
        Check(c2 && MathF.Abs(c.T - 0.55f) < 1e-4f && layer.TriangleMaterial(c.Triangle) == 2, "the closest point on material 2 skips the nearer +X face");
    }

    static void RestQueriesUnderPose()
    {
        Console.WriteLine("TEST 0.28.0 rest-mesh queries while a pose is set (skinned meshes): mirroring works on the resting shape");
        var m = Cube(10);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        var shift = new Vector3(1f, 0f, 0f);
        layer.SetQueryPositions(m.Pos.Select(p => p + shift).ToArray());
        var restPoint = new Vector3(0.3f, 0.3f, 0.1f);
        bool posed = layer.ClosestPoint(restPoint, 0.05f, out _);
        bool rest = layer.ClosestRestPoint(restPoint, 0.05f, -1, out var h);
        var shown = layer.QueryPoint(h);
        Console.WriteLine($"  posed query near the rest point: {posed}; rest query: {rest} at {h.Point}; shown at {shown}");
        Check(!posed && rest && Vector3.Distance(h.Point, restPoint) < 1e-5f && Vector3.Distance(shown, restPoint + shift) < 1e-5f,
              "ClosestRestPoint finds the rest surface, and QueryPoint maps it onto the pose");
        var plane = layer.Mirror(0);
        bool mirrored = layer.MirrorPoint(restPoint, plane, 0.05f, -1, out var mh);
        Check(MathF.Abs(plane.Offset) < 1e-6f && plane.Score > 0.99f && mirrored && Vector3.Distance(mh.Point, FlipX(restPoint)) < 1e-5f,
              $"the mirror plane is measured on the rest mesh, not the pose ({plane})");
        layer.ResetQueryPositions();
        Check(layer.ClosestPoint(restPoint, 0.05f, out _), "after the pose is cleared, queries run on the rest mesh again");
    }

    static void MirrorPlanes()
    {
        Console.WriteLine("TEST 0.28.0 mirror planes: through the origin, or the middle of the bounds, and a score for how symmetric");
        const int sub = 20;
        var m = Cube(sub); // x, z in -0.3..0.3, y in 0..0.6
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri, FaceMaterials(m, sub));
        var px = layer.Mirror(0); var py = layer.Mirror(1); var pz = layer.Mirror(2);
        Console.WriteLine($"  cube: X {px}; Y {py}; Z {pz}");
        Check(MathF.Abs(px.Offset) < 1e-6f && px.Score > 0.99f && MathF.Abs(pz.Offset) < 1e-6f && pz.Score > 0.99f, "the cube is symmetric across x = 0 and z = 0 (planes through the origin)");
        Check(MathF.Abs(py.Offset - 0.3f) < 1e-5f && py.Score > 0.99f, "across Y its origin is at the bottom, so the plane goes through the middle (y = 0.3)");

        var p = new Vector3(0.3f, 0.35f, 0.12f);
        bool any = layer.MirrorPoint(p, px, 0.05f, -1, out var h);
        bool on2 = layer.MirrorPoint(p, px, 0.05f, 2, out var h2);
        bool on1 = layer.MirrorPoint(p, px, 0.05f, 1, out _);
        Check(any && Vector3.Distance(h.Point, FlipX(p)) < 1e-5f && on2 && layer.TriangleMaterial(h2.Triangle) == 2 && !on1,
              "a point on the +X face mirrors onto the -X face (material 2), and finds nothing on material 1");
        var off = new Vector3(-0.31f, 0.35f, 0.12f);  // 1 cm outside the -X face: snaps onto the +X face
        Check(layer.MirrorPoint(off, px, 0.05f, -1, out var hs) && MathF.Abs(hs.Point.X - 0.3f) < 1e-5f && hs.T > 0.009f && hs.T < 0.011f,
              "a mirror image 1 cm off the surface snaps onto it");

        var shifted = Cube(sub); for (int i = 0; i < shifted.VertexCount; i++) shifted.Pos[i] += new Vector3(0.5f, 0f, 0f);
        var sx = new PaintLayer(shifted.Pos, ReadOnlySpan<Vector4>.Empty, shifted.Tri).Mirror(0);
        Check(MathF.Abs(sx.Offset - 0.5f) < 1e-5f && sx.Score > 0.99f, $"a cube moved to x = 0.5 mirrors across its middle ({sx})");

        // a cube with a block stuck to its +X side (more vertices than the cube): not symmetric across X
        var block = Cube(30, 0.3f);
        var pos = m.Pos.Concat(block.Pos.Select(q => q + new Vector3(0.45f, 0.15f, 0f))).ToArray();
        var tri = m.Tri.Concat(block.Tri.Select(i => i + m.VertexCount)).ToArray();
        var lop = new PaintLayer(pos, ReadOnlySpan<Vector4>.Empty, tri).Mirror(0);
        Console.WriteLine($"  cube with a block on one side: X {lop}");
        Check(lop.Score < SoftbodyBrushMinSymmetry, $"a lopsided model scores under the brush's {SoftbodyBrushMinSymmetry:P0}, so the brush won't mirror on it");

        var bigCube = Cube(60);
        var bl = new PaintLayer(bigCube.Pos, ReadOnlySpan<Vector4>.Empty, bigCube.Tri);
        bl.SetQueryPositions(bigCube.Pos.Select(q => q * 1.01f).ToArray()); // a pose: the rest BVH is built too
        var sw = Stopwatch.StartNew();
        var bp = bl.Mirror(0);
        double ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int i = 0; i < 1000; i++) bl.MirrorPoint(bigCube.Pos[(i * 37) % bigCube.VertexCount], bp, 0.02f, -1, out _);
        double per = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  {bigCube.Tri.Length / 3} triangles: first mirror plane (rest BVH + {PaintLayer.SymmetrySamples} samples x up to 2 planes) {ms:0.0} ms; 1000 mirrored dabs {per:0.0} ms");
        Check(ms < 300 && per < 50, "measuring the plane takes < 300 ms once, and mirroring a dab well under 50 us");
    }

    /// <summary>The brush's threshold (SoftbodyBrush.MinSymmetry), copied: the test project does not reference the plugin.</summary>
    const float SoftbodyBrushMinSymmetry = 0.6f;
}
