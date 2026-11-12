using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody.Tests;

// Offline tests for the 0.4.0 paint layer (Core/PaintLayer.cs) and the Colors partial upload.
static partial class Program
{
    static void PaintTests()
    {
        PaintFalloffAndOrder();
        PaintIncrementalEqualsFull();
        PaintEraseAndRegional();
        PaintGridEqualsBruteForce();
        PaintRaycast();
        PaintTiming();
        PaintDrivesCage();
        PaintBlur();
        PaintStartFirm();
        try { ColorUploadEncode(); }
        catch (Exception ex) { Console.WriteLine("  colour upload encode could not run offline: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>StartAs = AllFirm: the plugin gives the paint layer an all-black base; painting white makes parts soft.</summary>
    static void PaintStartFirm()
    {
        Console.WriteLine("TEST start all firm: black base (StartAs AllFirm), the top painted white -> only the top is soft");
        var m = Cube(40, 0.6f, gradient: false);
        var black = Enumerable.Repeat(new Vector4(0, 0, 0, 1), m.VertexCount).ToArray();
        var layer = new PaintLayer(m.Pos, black, m.Tri);
        var soft = new float[m.VertexCount]; var bands = new float[m.VertexCount * HueBands.Count];
        layer.ComputeMaps(SoftnessSource.Value, false, soft, bands);
        using var unpainted = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, soft, new SoftbodySettings(), AnchorPose.Identity, bands);
        int movingBefore = unpainted.Deformer.DynamicVertexCount;
        var dabs = new List<Dab>();
        for (float x = -0.3f; x <= 0.3f; x += 0.05f)
            for (float z = -0.3f; z <= 0.3f; z += 0.05f)
                dabs.Add(new Dab(new Vector3(x, 0.6f, z), 0.12f, Vector4.One, 1f, 1f));
        layer.Update(dabs.ToArray());
        layer.ComputeMaps(SoftnessSource.Value, false, soft, bands);
        using var painted = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, soft, new SoftbodySettings { Cells = 8 }, AnchorPose.Identity, bands);
        int topSoft = 0, top = 0, lowPinned = 0, low = 0;
        for (int i = 0; i < painted.Cage.PointCount; i++)
        {
            var p = painted.Cage.RestPoints[i];
            if (p.Y > 0.55f) { top++; if (!painted.Cage.Kinematic[i]) topSoft++; }
            else if (p.Y < 0.3f) { low++; if (painted.Cage.Kinematic[i]) lowPinned++; }
        }
        Console.WriteLine($"  unpainted: {movingBefore} moving verts; painted: {painted.Cage.ActiveCells}/{painted.Cage.TotalCells} cells, " +
                          $"{painted.Cage.PointCount} points, {topSoft}/{top} top points soft, {lowPinned}/{low} low points pinned, {painted.Deformer.DynamicVertexCount} moving verts");
        Check(movingBefore == 0, "an all-black start moves nothing");
        Check(top > 0 && topSoft == top && lowPinned == low, "painting the top white makes just the top soft (the cage only covers the top part)");
    }

    static bool SameColors(ReadOnlySpan<Vector4> a, ReadOnlySpan<Vector4> b, out int firstDiff)
    {
        firstDiff = -1;
        if (a.Length != b.Length) { firstDiff = 0; return false; }
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i].X) != BitConverter.SingleToInt32Bits(b[i].X) || BitConverter.SingleToInt32Bits(a[i].Y) != BitConverter.SingleToInt32Bits(b[i].Y) ||
                BitConverter.SingleToInt32Bits(a[i].Z) != BitConverter.SingleToInt32Bits(b[i].Z) || BitConverter.SingleToInt32Bits(a[i].W) != BitConverter.SingleToInt32Bits(b[i].W))
            { firstDiff = i; return false; }
        return true;
    }

    /// <summary>Random dabs on the cube's surface: centres at (jittered) vertex positions.</summary>
    static Dab[] RandomDabs(TestMesh m, int count, int seed, float rMin = 0.02f, float rMax = 0.08f)
    {
        var rng = new Random(seed);
        var d = new Dab[count];
        for (int i = 0; i < count; i++)
        {
            var p = m.Pos[rng.Next(m.VertexCount)] + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.01f;
            var c = new Vector4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
            d[i] = new Dab(p, rMin + (float)rng.NextDouble() * (rMax - rMin), c, 0.2f + 0.8f * (float)rng.NextDouble(), (float)rng.NextDouble()).Sanitized();
        }
        return d;
    }

    static Vector4[] GradientColors(TestMesh m) =>
        m.Pos.Select(p => new Vector4(p.Y / 0.6f, 1f - p.Y / 0.6f, 0.2f + 0.5f * MathF.Abs(p.X), 1f)).ToArray();

    static void PaintFalloffAndOrder()
    {
        Console.WriteLine("TEST paint dabs: falloff, strength and order of application");
        float[] xs = { 0f, 0.25f, 0.5f, 0.75f, 0.999f, 1f };
        Console.WriteLine("  falloff(x) hardness 0  : " + string.Join(" ", xs.Select(x => $"{x:0.###}->{PaintLayer.Falloff(x, 0f):0.####}")));
        Console.WriteLine("  falloff(x) hardness 0.5: " + string.Join(" ", xs.Select(x => $"{x:0.###}->{PaintLayer.Falloff(x, 0.5f):0.####}")));
        Check(PaintLayer.Falloff(0f, 0f) == 1f && MathF.Abs(PaintLayer.Falloff(0.5f, 0f) - 0.5f) < 1e-6f && PaintLayer.Falloff(1f, 0f) == 0f &&
              PaintLayer.Falloff(0.5f, 0.5f) == 1f && MathF.Abs(PaintLayer.Falloff(0.75f, 0.5f) - 0.5f) < 1e-6f && PaintLayer.Falloff(0.999f, 1f) == 1f,
              "falloff: 1 at the centre and inside the hard core, a smoothstep to 0 at the rim (0.5 half way)");

        // four vertices on a line at 0, r/2, r and 2r from the dab centre (white, no colours)
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(0.05f, 0, 0), new Vector3(0.1f, 0, 0), new Vector3(0.2f, 0, 0) };
        var tri = new[] { 0, 1, 2, 1, 3, 2 };
        var layer = new PaintLayer(pos, ReadOnlySpan<Vector4>.Empty, tri);
        var black = new Dab(Vector3.Zero, 0.1f, new Vector4(0, 0, 0, 1), 0.5f, 0f);
        layer.Update(new[] { black });
        var c = layer.Colors;
        Console.WriteLine($"  white mesh + black dab (r 0.1, strength 0.5, hardness 0): {c[0].X:0.####} at 0, {c[1].X:0.####} at r/2, {c[2].X:0.####} at r, {c[3].X:0.####} at 2r");
        Check(MathF.Abs(c[0].X - 0.5f) < 1e-6f && MathF.Abs(c[1].X - 0.75f) < 1e-6f && c[2].X == 1f && c[3].X == 1f && c[0].W == 1f,
              "c = lerp(c, dab, strength x falloff): 0.5 at the centre, 0.75 at half the radius, untouched at and beyond the rim");

        var red = new Vector4(1, 0, 0, 1); var blue = new Vector4(0, 0, 1, 1);
        Vector4 One(params Dab[] dabs) { var l = new PaintLayer(pos, ReadOnlySpan<Vector4>.Empty, tri); l.Update(dabs); return l.Colors[0]; }
        var rb = One(new Dab(Vector3.Zero, 0.1f, red, 0.5f, 1f), new Dab(Vector3.Zero, 0.1f, blue, 0.5f, 1f));
        var br = One(new Dab(Vector3.Zero, 0.1f, blue, 0.5f, 1f), new Dab(Vector3.Zero, 0.1f, red, 0.5f, 1f));
        var expRb = Vector4.Lerp(Vector4.Lerp(Vector4.One, red, 0.5f), blue, 0.5f);
        Console.WriteLine($"  red then blue (strength 0.5 each): {rb}; blue then red: {br}");
        Check(Vector4.Distance(rb, expRb) < 1e-6f && rb.Z > rb.X && br.X > br.Z, "dabs apply in list order (the later one wins more)");
        var full = One(new Dab(Vector3.Zero, 0.1f, red, 1f, 1f), new Dab(Vector3.Zero, 0.1f, blue, 1f, 1f));
        Check(full == blue, "a full-strength hard dab sets the colour exactly");
    }

    static void PaintIncrementalEqualsFull()
    {
        Console.WriteLine("TEST paint: dabs appended in batches (incremental) give exactly the colours of a full recompute");
        var m = Cube(40);
        var baseCols = GradientColors(m);
        var dabs = RandomDabs(m, PaintLayer.MaxDabs, 7);
        var inc = new PaintLayer(m.Pos, baseCols, m.Tri);
        var rng = new Random(3);
        int n = 0, appendedUpdates = 0;
        while (n < dabs.Length)
        {
            n = Math.Min(dabs.Length, n + 1 + rng.Next(25));
            var r = inc.Update(dabs.AsSpan(0, n));
            if (r.Kind == PaintLayer.UpdateKind.Appended) appendedUpdates++;
        }
        var full = new PaintLayer(m.Pos, baseCols, m.Tri);
        full.RecomputeAll(dabs);
        bool same = SameColors(inc.Colors, full.Colors, out int diff);
        Console.WriteLine($"  {m.VertexCount} verts, {dabs.Length} dabs in {appendedUpdates} appends; identical: {same}{(same ? "" : $" (first difference at vertex {diff})")}");
        Check(same && appendedUpdates > 100, "incremental appends are bit-identical to a full recompute");
        Check(inc.Update(dabs).Kind == PaintLayer.UpdateKind.None, "the same list again changes nothing");
    }

    static void PaintEraseAndRegional()
    {
        Console.WriteLine("TEST paint: erase restores the original colours; removed, inserted and changed dabs recompute only their region");
        var m = Cube(40);
        var baseCols = GradientColors(m);
        var dabs = RandomDabs(m, 600, 11);
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        layer.Update(dabs);
        int changed = 0;
        for (int i = 0; i < m.VertexCount; i++) if (layer.Colors[i] != baseCols[i]) changed++;

        // remove one dab in the middle, several scattered, insert one, change one: each must equal a full recompute
        var list = dabs.ToList();
        var rng = new Random(5);
        bool allSame = true; var kinds = new List<string>();
        for (int step = 0; step < 40; step++)
        {
            int op = step % 4;
            if (op == 0) list.RemoveAt(rng.Next(list.Count));
            else if (op == 1) { for (int k = 0; k < 3; k++) list.RemoveAt(rng.Next(list.Count)); }
            else if (op == 2) list.Insert(rng.Next(list.Count), RandomDabs(m, 1, 100 + step)[0]);
            else { int i = rng.Next(list.Count); var d = list[i]; list[i] = new Dab(d.Position, d.Radius * 1.3f, new Vector4(0, 0, 0, 1), d.Strength, d.Hardness).Sanitized(); }
            var r = layer.Update(list.ToArray());
            if (step < 4) kinds.Add(r.ToString());
            var reference = new PaintLayer(m.Pos, baseCols, m.Tri);
            reference.RecomputeAll(list.ToArray());
            allSame &= SameColors(layer.Colors, reference.Colors, out _);
            allSame &= r.Kind == PaintLayer.UpdateKind.Regional;
        }
        Console.WriteLine($"  {changed} of {m.VertexCount} vertices painted by 600 dabs; first edits: {string.Join("; ", kinds)}");
        Check(allSame, "40 edits (remove 1, remove 3, insert, change) take the regional path and match a full recompute bit for bit");

        // erase everything, one dab at a time from the front, then the rest at once
        for (int k = 0; k < 50; k++) { list.RemoveAt(0); layer.Update(list.ToArray()); }
        layer.Update(ReadOnlySpan<Dab>.Empty);
        bool restored = SameColors(layer.Colors, baseCols, out int diff);
        Console.WriteLine($"  after erasing every dab: original colours restored exactly: {restored}{(restored ? "" : $" (vertex {diff})")}");
        Check(restored && layer.DabCount == 0, "erasing all dabs restores the mesh's own colours exactly");

        var white = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        white.Update(dabs); white.Update(dabs.AsSpan(0, 0));
        Check(white.Colors.ToArray().All(c => c == Vector4.One) && !white.HadBaseColors, "a mesh without colours starts white and goes back to white");
    }

    static void PaintGridEqualsBruteForce()
    {
        Console.WriteLine("TEST paint: the vertex grid gives exactly the brute-force result");
        var m = Cube(40);
        var dabs = RandomDabs(m, 1500, 21, 0.005f, 0.3f);
        var grid = new PaintLayer(m.Pos, GradientColors(m), m.Tri);
        var brute = new PaintLayer(m.Pos, GradientColors(m), m.Tri) { BruteForce = true };
        var sw = Stopwatch.StartNew(); grid.Update(dabs); double gms = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); brute.Update(dabs); double bms = sw.Elapsed.TotalMilliseconds;
        // plus edits through both paths
        var list = dabs.ToList(); list.RemoveAt(700); list.RemoveAt(10); list.Insert(300, dabs[5]);
        grid.Update(list.ToArray()); brute.Update(list.ToArray());
        bool same = SameColors(grid.Colors, brute.Colors, out _);
        Console.WriteLine($"  grid {grid.GridCells} cells of {grid.GridCellSize * 1000:0.0} mm: {gms:0.0} ms; brute force: {bms:0.0} ms; identical after edits: {same}");
        Check(same, "grid and brute force are bit-identical (radii 5..300 mm, incl. regional edits)");
    }

    // brute-force reference: nearest front-face hit, else nearest back-face hit
    static bool BruteRay(TestMesh m, Vector3 o, Vector3 d, float tMax, out float t, out bool front)
    {
        float bf = float.MaxValue, bb = float.MaxValue;
        for (int i = 0; i < m.Tri.Length; i += 3)
        {
            var a = m.Pos[m.Tri[i]]; var b = m.Pos[m.Tri[i + 1]]; var c = m.Pos[m.Tri[i + 2]];
            var e1 = b - a; var e2 = c - a; var pv = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-20f) continue;
            float inv = 1 / det; var s = o - a; float u = Vector3.Dot(s, pv) * inv; if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1); float v = Vector3.Dot(d, q) * inv; if (v < 0 || u + v > 1) continue;
            float tt = Vector3.Dot(e2, q) * inv; if (tt < 0 || tt > tMax) continue;
            if (det > 0) bf = MathF.Min(bf, tt); else bb = MathF.Min(bb, tt);
        }
        front = bf < float.MaxValue; t = front ? bf : bb;
        return t < float.MaxValue;
    }

    static void PaintRaycast()
    {
        Console.WriteLine("TEST paint: ray and closest-point queries on the rest mesh (test cube 0.6 m, y 0..0.6)");
        var m = Cube(20);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        bool h1 = layer.Raycast(new Vector3(0.05f, 0.3f, -2f), Vector3.UnitZ, 5f, out var a);
        Console.WriteLine($"  from the front: hit {h1} t {a.T:0.0000} point {a.Point} normal {a.Normal} front {a.FrontFace}");
        Check(h1 && MathF.Abs(a.T - 1.7f) < 1e-4f && a.FrontFace && Vector3.Distance(a.Normal, -Vector3.UnitZ) < 1e-4f, "a ray from outside hits the near face (front face, t 1.7, normal -Z), not the far one");
        bool h2 = layer.Raycast(new Vector3(0.05f, 0.3f, 0f), Vector3.UnitZ, 5f, out var b);
        Check(h2 && MathF.Abs(b.T - 0.3f) < 1e-4f && !b.FrontFace, "from inside, with no front face on the ray, the back face it meets is returned");
        bool h3 = layer.Raycast(new Vector3(0.1f, 2f, 0.1f), -Vector3.UnitY, 5f, out var c);
        Check(h3 && MathF.Abs(c.Point.Y - 0.6f) < 1e-4f && c.FrontFace, "a ray from above hits the top at y = 0.6");
        Check(!layer.Raycast(new Vector3(2f, 0.3f, -2f), Vector3.UnitZ, 5f, out _) && !layer.Raycast(new Vector3(0, 0.3f, -2f), Vector3.UnitZ, 1.5f, out _),
              "rays that miss, or stop short (tMax), hit nothing");
        bool h4 = layer.ClosestPoint(new Vector3(0.02f, 0.65f, -0.01f), 0.1f, out var cp);
        Check(h4 && MathF.Abs(cp.T - 0.05f) < 1e-4f && MathF.Abs(cp.Point.Y - 0.6f) < 1e-5f && cp.FrontFace && !layer.ClosestPoint(new Vector3(0, 0.75f, 0), 0.1f, out _),
              "closest point: 5 cm above the top it finds the top face within 10 cm, and nothing from 15 cm away");

        // random rays against brute force
        var rng = new Random(9); int agree = 0, hits = 0; const int N = 3000;
        for (int i = 0; i < N; i++)
        {
            var o = new Vector3((float)rng.NextDouble() * 3 - 1.5f, (float)rng.NextDouble() * 2 - 0.7f, (float)rng.NextDouble() * 3 - 1.5f);
            var target = new Vector3((float)rng.NextDouble() * 0.8f - 0.4f, (float)rng.NextDouble() * 0.8f - 0.1f, (float)rng.NextDouble() * 0.8f - 0.4f);
            var d = Vector3.Normalize(target - o);
            bool hb = layer.Raycast(o, d, 5f, out var hit);
            bool rb = BruteRay(m, o, d, 5f, out float tb, out bool fb);
            if (hb) hits++;
            if (hb == rb && (!hb || (MathF.Abs(hit.T - tb) < 1e-5f && hit.FrontFace == fb))) agree++;
        }
        Console.WriteLine($"  {N} random rays: {hits} hits, BVH agrees with brute force on {agree}");
        Check(agree == N, "the BVH raycast agrees with brute force on every random ray (nearest, front faces first)");
        var sw = Stopwatch.StartNew();
        var big = Cube(40); var bl = new PaintLayer(big.Pos, ReadOnlySpan<Vector4>.Empty, big.Tri);
        bl.Raycast(new Vector3(0, 0.3f, -2), Vector3.UnitZ, 5, out _); double build = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (int i = 0; i < 1000; i++) bl.Raycast(new Vector3(0.001f * i - 0.5f, 0.3f, -2), Vector3.Normalize(new Vector3(0.0005f * i, 0, 1)), 5, out _);
        double per = sw.Elapsed.TotalMilliseconds / 1000;
        Console.WriteLine($"  {big.Tri.Length / 3} triangles: BVH built in {build:0.0} ms (first query), {per * 1000:0.0} us per ray");
        Check(per < 0.05, "a ray against a 20k-triangle mesh takes < 50 us");
    }

    static void PaintTiming()
    {
        Console.WriteLine("TEST paint timing (Release, single thread): 10k vertices, 4000 dabs of 20..80 mm");
        var m = Cube(40);
        var baseCols = GradientColors(m);
        var dabs = RandomDabs(m, PaintLayer.MaxDabs, 31);
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        layer.RecomputeAll(dabs); // warm up / JIT
        var full = new List<double>();
        var sw = new Stopwatch();
        for (int k = 0; k < 7; k++) { sw.Restart(); layer.RecomputeAll(dabs); full.Add(sw.Elapsed.TotalMilliseconds); }
        full.Sort();

        // painting: the list grows by 1-2 dabs per frame; each frame is one Update (diff + apply)
        var inc = new PaintLayer(m.Pos, baseCols, m.Tri);
        var per = new List<double>();
        for (int n = 1; n <= dabs.Length; n += 2) { sw.Restart(); inc.Update(dabs.AsSpan(0, n)); per.Add(sw.Elapsed.TotalMilliseconds); }
        per.Sort();
        double avg = per.Average(), p99 = per[(int)(per.Count * 0.99)];

        // erasing like the brush: every dab centred within 5 cm of a surface point (spatially together, anywhere in the list)
        var list = dabs.ToList(); var erase = new List<double>(); var under = new List<int>(); var rngE = new Random(77); int erased = 0;
        for (int k = 0; k < 30; k++)
        {
            var arr = list.ToArray();
            PaintLayer.FindDabsWithin(arr, m.Pos[rngE.Next(m.VertexCount)], 0.05f, under);
            for (int j = under.Count - 1; j >= 0; j--) list.RemoveAt(under[j]);
            erased += under.Count;
            arr = list.ToArray();
            sw.Restart(); inc.Update(arr); erase.Add(sw.Elapsed.TotalMilliseconds);
        }
        erase.Sort();
        // worst case: 5 dabs from all over the mesh at once (the recomputed region spans the whole cube)
        var scattered = new List<double>();
        for (int k = 0; k < 10; k++) { for (int j = 0; j < 5; j++) list.RemoveAt(500 + 300 * j + k); var arr = list.ToArray(); sw.Restart(); inc.Update(arr); scattered.Add(sw.Elapsed.TotalMilliseconds); }
        scattered.Sort();

        var soft = new float[m.VertexCount]; var bands = new float[m.VertexCount * HueBands.Count];
        sw.Restart(); inc.ComputeMaps(SoftnessSource.Value, false, soft, bands); double maps = sw.Elapsed.TotalMilliseconds;
        sw.Restart(); using (var rig = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, soft, new SoftbodySettings(), AnchorPose.Identity, bands)) { }
        double rebuild = sw.Elapsed.TotalMilliseconds + maps;

        Console.WriteLine($"  full recompute of 4000 dabs: median {full[3]:0.00} ms (min {full[0]:0.00})");
        Console.WriteLine($"  painting, 2 new dabs per frame on a list growing to 4000: avg {avg * 1000:0.0} us, p99 {p99 * 1000:0.0} us per frame (diff + apply)");
        Console.WriteLine($"  erasing with a 5 cm brush ({erased / 30.0:0.0} dabs per frame out of ~4000): median {erase[15]:0.00} ms, max {erase[^1]:0.00} ms (regional recompute); " +
                          $"5 dabs scattered over the whole mesh: median {scattered[5]:0.00} ms");
        Console.WriteLine($"  softness + bands from the colours {maps:0.00} ms; cage rebuild after a stroke (maps + rig) {rebuild:0.0} ms, once per stroke");
        Check(p99 < 0.5 && avg < 0.2, "painting costs well under 1 ms per frame (p99 < 0.5 ms) at 10k vertices and up to 4000 dabs");
        Check(full[3] < 30, "a full recompute of 4000 dabs on 10k vertices takes < 30 ms (only on big list changes)");
        Check(erase[15] < 0.5 && scattered[5] < 3, "erasing under the brush takes < 0.5 ms with 4000 dabs (regional, not full); scattered removals < 3 ms");
    }

    static void PaintDrivesCage()
    {
        Console.WriteLine("TEST paint drives the cage: an all-soft cube (no colours), then the top painted black (firm)");
        var m = Cube(40, 0.6f, gradient: false);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        var s = new SoftbodySettings();
        using var before = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, ReadOnlySpan<float>.Empty, s, AnchorPose.Identity);
        // a stroke of hard black dabs over the top face and the top rim of the sides
        var dabs = new List<Dab>();
        for (float x = -0.3f; x <= 0.3f; x += 0.05f)
            for (float z = -0.3f; z <= 0.3f; z += 0.05f)
                dabs.Add(new Dab(new Vector3(x, 0.6f, z), 0.16f, new Vector4(0, 0, 0, 1), 1f, 1f));
        var r = layer.Update(dabs.ToArray());
        var soft = new float[m.VertexCount]; var bands = new float[m.VertexCount * HueBands.Count];
        layer.ComputeMaps(SoftnessSource.Value, false, soft, bands);
        using var after = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, soft, s, AnchorPose.Identity, bands);
        int topBefore = 0, topAfter = 0, topPoints = 0, lowSoft = 0, lowPoints = 0;
        for (int i = 0; i < before.Cage.PointCount; i++)
            if (before.Cage.RestPoints[i].Y > 0.5f && before.Cage.Kinematic[i]) topBefore++;
        for (int i = 0; i < after.Cage.PointCount; i++)
        {
            var p = after.Cage.RestPoints[i];
            if (p.Y > 0.5f) { topPoints++; if (after.Cage.Kinematic[i]) topAfter++; }
            else if (p.Y < 0.25f) { lowPoints++; if (!after.Cage.Kinematic[i] && after.Cage.PointSoftness[i] > 0.9f) lowSoft++; }
        }
        int firmVerts = soft.Count(v => v <= 0.001f);
        Console.WriteLine($"  {r}; {firmVerts} vertices now firm; cage before: {before.Cage.PointCount} points, {topBefore} top ones pinned; " +
                          $"after: {after.Cage.PointCount} points, {topAfter}/{topPoints} top ones pinned, {lowSoft}/{lowPoints} low ones still soft");
        Check(topBefore == 0 && topPoints > 0 && topAfter == topPoints, "painting the top black pins every top cage point (none were pinned before)");
        Check(lowPoints > 0 && lowSoft == lowPoints, "the unpainted bottom stays fully soft");

        // and the rebuilt rig still behaves: shake it, the top must not move against the anchor
        var tgt = new Vector3[after.Cage.PointCount]; var pts = new Vector3[after.Cage.PointCount];
        float topErr = 0;
        for (int f = 0; f < 120; f++)
        {
            var anchor = new AnchorPose(new Vector3(0.15f * MathF.Sin(2 * MathF.PI * 2f * f * Dt), 0, 0), Quaternion.Identity, Vector3.One);
            after.Step(Dt, anchor);
            after.Sim.GetPositions(pts); after.Sim.GetTargets(tgt);
            for (int i = 0; i < pts.Length; i++) if (after.Cage.RestPoints[i].Y > 0.5f) topErr = MathF.Max(topErr, Vector3.Distance(pts[i], tgt[i]));
        }
        Check(topErr < 1e-3f && after.Sim.ResetCount == 0, $"while shaking, the painted-firm top follows the anchor exactly ({topErr * 1000:0.000} mm), no resets");
    }

    // ------------------------------------------------------------------ Colors in a partial upload (engine encoder, offline)

    sealed class KeepAlloc : IBackingBufferAllocator
    {
        public IBackingMemoryBuffer Allocate(int size, object owner) => new ArrayBackingMemoryBuffer(new byte[size]);
    }

    static void ColorUploadEncode()
    {
        Console.WriteLine("TEST colour upload (engine encoder, offline): Positions|Normals|Colors partial vs a full encode");
        var m = Cube(40);
        var mx = new MeshX();
        mx.HasNormals = true; mx.HasColors = true;
        mx.SetVertexCount(m.VertexCount);
        m.Pos.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, m.VertexCount)));
        m.Nrm.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawNormals.AsSpan(0, m.VertexCount)));
        var cols = MemoryMarshal.Cast<color, Vector4>(mx.RawColors.AsSpan(0, m.VertexCount));
        GradientColors(m).CopyTo(cols);
        var sub = mx.AddSubmesh<TriangleSubmesh>();
        for (int t = 0; t < m.Tri.Length; t += 3) sub.AddTriangle(m.Tri[t], m.Tri[t + 1], m.Tri[t + 2]);
        var meta = MeshMetadata.GenerateMetadata(mx);
        var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));
        var alloc = new KeepAlloc();

        MeshBuffer Encode(MeshBuffer buf, ref MeshUploadHint hint)
        {
            buf ??= new MeshBuffer();
            MeshBufferGenerator.UpdateMeshBufferData(mx, buf, alloc, ColorProfile.Linear, ref hint, meta, bounds, null);
            return buf;
        }
        // as the plugin's first upload: full and Dynamic (Dynamic picks float colours; without it the encoder uses half floats)
        MeshUploadHint Full() { var h = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref h, mx); h[MeshUploadHint.Flag.Dynamic] = true; return h; }
        MeshBuffer EncodeFull(MeshBuffer buf = null) { var h = Full(); return Encode(buf, ref h); }

        var withColors = EncodeFull();
        var withoutColors = EncodeFull();
        // paint: change the colours (and nudge positions like a deform frame)
        var layer = new PaintLayer(m.Pos, cols, m.Tri);
        layer.Update(RandomDabs(m, 300, 41));
        layer.Colors.CopyTo(cols);
        var p = MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, m.VertexCount)); p[5] += new Vector3(0, 1e-3f, 0);
        var pnc = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Colors | MeshUploadHint.Flag.Dynamic);
        var pn = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
        var sw = Stopwatch.StartNew();
        Encode(withColors, ref pnc);
        double ms = sw.Elapsed.TotalMilliseconds;
        Encode(withoutColors, ref pn);
        var reference = EncodeFull();
        int vbytes = reference.VertexStride * reference.VertexCount;
        bool sameAsFull = withColors.RawBuffer.Slice(0, vbytes).SequenceEqual(reference.RawBuffer.Slice(0, vbytes));
        bool staleWithout = !withoutColors.RawBuffer.Slice(0, vbytes).SequenceEqual(reference.RawBuffer.Slice(0, vbytes));
        bool widened = pnc[MeshUploadHint.Flag.VertexLayout] || pnc[MeshUploadHint.Flag.Geometry];
        Console.WriteLine($"  stride {reference.VertexStride} B ({string.Join(", ", reference.VertexAttributes.Select(a => $"{a.attribute} {a.format}x{a.dimensions}"))}); " +
                          $"P|N|C partial {ms:0.000} ms; vertex buffer equals a full encode: {sameAsFull}; P|N only leaves old colours: {staleWithout}; hint widened: {widened}");
        Check(sameAsFull && !widened, "a Positions|Normals|Colors partial encodes the same vertex buffer as a full upload, without widening to layout/geometry");
        Check(staleWithout, "and without the Colors flag the old colours stay in the buffer (so the plugin must set it when the paint changes)");
    }
}
