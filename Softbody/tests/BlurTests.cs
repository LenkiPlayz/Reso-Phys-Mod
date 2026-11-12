using System.Diagnostics;
using System.Numerics;
using Softbody.Core;

namespace Softbody.Tests;

// Offline tests for blur dabs (Core/PaintLayer.cs, Dab.Blur): the brush's Blur mode.
static partial class Program
{
    static void PaintBlur()
    {
        BlurSoftensEdge();
        BlurKeepsRamp();
        BlurExactness();
        BlurTiming();
    }

    static IEnumerable<int> TopFace(TestMesh m) => Enumerable.Range(0, m.VertexCount).Where(v => m.Nrm[v].Y > 0.9f);

    /// <summary>Largest colour step (red channel) between top-face vertices that are neighbours along x near z = 0.</summary>
    static float MaxStepAcrossX(TestMesh m, ReadOnlySpan<Vector4> col, float step)
    {
        var top = TopFace(m).Where(v => MathF.Abs(m.Pos[v].Z) < 0.1f).ToArray();
        float worst = 0;
        foreach (int a in top)
            foreach (int b in top)
            {
                if (b <= a) continue;
                var d = m.Pos[b] - m.Pos[a];
                if (MathF.Abs(d.Z) > 1e-4f || MathF.Abs(MathF.Abs(d.X) - step) > 1e-4f) continue;
                worst = MathF.Max(worst, MathF.Abs(col[a].X - col[b].X));
            }
        return worst;
    }

    static Dab[] BlurLine(float x, float y, float r, float strength, int passes)
    {
        var list = new List<Dab>();
        for (int pass = 0; pass < passes; pass++)
            for (float z = -0.25f; z <= 0.25f + 1e-4f; z += 0.3f * r)
                list.Add(new Dab(new Vector3(x, y, z), r, Vector4.Zero, strength, 0.3f, blur: true).Sanitized());
        return list.ToArray();
    }

    static void BlurSoftensEdge()
    {
        Console.WriteLine("TEST blur: a hard black|white edge on the top face, blurred along it (3 passes of a 8 cm brush)");
        var m = Cube(40);
        float step = 0.6f / 40f;
        var baseCols = m.Pos.Select(p => p.X < 0 ? new Vector4(0, 0, 0, 1) : Vector4.One).ToArray();
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        float before = MaxStepAcrossX(m, layer.Colors, step);
        var dabs = BlurLine(0f, 0.6f, 0.08f, 1f, 3);
        layer.Update(dabs);
        float after = MaxStepAcrossX(m, layer.Colors, step);
        float k = layer.BlurKernel(0.08f);
        // vertices further than radius from every dab centre must keep their colour exactly
        bool farSame = true; int far = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            bool reach = dabs.Any(d => Vector3.Distance(d.Position, m.Pos[v]) < d.Radius);
            if (reach) continue;
            far++;
            farSame &= layer.Colors[v] == baseCols[v];
        }
        // the edge becomes a monotonic ramp from black to white along x at z = 0
        var row = TopFace(m).Where(v => MathF.Abs(m.Pos[v].Z) < 1e-3f).OrderBy(v => m.Pos[v].X).ToArray();
        bool monotonic = true;
        for (int i = 1; i < row.Length; i++) monotonic &= layer.Colors[row[i]].X >= layer.Colors[row[i - 1]].X - 1e-5f;
        var mid = row.Where(v => MathF.Abs(m.Pos[v].X) < 0.06f).Select(v => $"{m.Pos[v].X * 100:0.0}cm:{layer.Colors[v].X:0.00}");
        Console.WriteLine($"  blur neighbourhood {k * 100:0.0} cm; largest step between neighbouring vertices {before:0.00} -> {after:0.00}");
        Console.WriteLine($"  across the edge at z=0: {string.Join("  ", mid)}");
        Check(after < 0.25f * before, "the hard edge becomes a soft ramp (largest neighbour step < 1/4 of before)");
        Check(monotonic, "and the ramp goes smoothly from black to white (monotonic, no ringing)");
        Check(farSame && far > 0, $"vertices outside the brush keep their colour exactly ({far} checked)");
    }

    static void BlurKeepsRamp()
    {
        Console.WriteLine("TEST blur: an existing smooth gradient (linear along x) is left as it is");
        var m = Cube(40);
        var baseCols = m.Pos.Select(p => new Vector4(0.5f + p.X, 0.5f + p.X, 0.5f + p.X, 1f)).ToArray();
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        layer.Update(new[] { new Dab(new Vector3(0, 0.6f, 0), 0.1f, Vector4.Zero, 1f, 0.5f, blur: true).Sanitized() });
        float worst = 0;
        foreach (int v in TopFace(m))
        {
            var p = m.Pos[v];
            if (MathF.Abs(p.X) > 0.15f || MathF.Abs(p.Z) > 0.15f) continue; // away from the cube's edges
            worst = MathF.Max(worst, MathF.Abs(layer.Colors[v].X - baseCols[v].X));
        }
        Console.WriteLine($"  largest change inside the brush: {worst:0.0000} (gradient spans 0.2..0.8)");
        Check(worst < 0.01f, "a linear gradient under a blur dab changes by less than 0.01");
    }

    static void BlurExactness()
    {
        Console.WriteLine("TEST blur exactness: mixed paint and blur dabs, incremental vs full, erase, grid vs brute force");
        var m = Cube(30);
        var baseCols = GradientColors(m);
        var rng = new Random(7);
        var dabs = RandomDabs(m, 600, 11).Select(d => rng.NextDouble() < 0.35
            ? new Dab(d.Position, d.Radius, d.Color, d.Strength, d.Hardness, blur: true).Sanitized() : d).ToArray();
        int blurCount = dabs.Count(d => d.Blur);

        var inc = new PaintLayer(m.Pos, baseCols, m.Tri);
        for (int n = 0; n < dabs.Length; n += 1 + rng.Next(12)) inc.Update(dabs.AsSpan(0, n));
        inc.Update(dabs);
        var full = new PaintLayer(m.Pos, baseCols, m.Tri);
        full.RecomputeAll(dabs);
        bool same = SameColors(inc.Colors, full.Colors, out int diff1);

        // erase: drop every fifth dab (the update must notice the blur dabs and recompute fully)
        var kept = dabs.Where((_, i) => i % 5 != 0).ToArray();
        var r = inc.Update(kept);
        var fresh = new PaintLayer(m.Pos, baseCols, m.Tri);
        fresh.RecomputeAll(kept);
        bool eraseSame = SameColors(inc.Colors, fresh.Colors, out int diff2);

        var brute = new PaintLayer(m.Pos, baseCols, m.Tri) { BruteForce = true };
        brute.RecomputeAll(dabs);
        bool bruteSame = SameColors(full.Colors, brute.Colors, out int diff3);

        Console.WriteLine($"  {dabs.Length} dabs ({blurCount} blur): incremental vs full {(same ? "identical" : "differ at " + diff1)}; " +
                          $"after erasing {dabs.Length - kept.Length}: {r} -> {(eraseSame ? "identical" : "differ at " + diff2)}; grid vs brute force {(bruteSame ? "identical" : "differ at " + diff3)}");
        Check(same, "with blur dabs, painting incrementally gives exactly the colours of a full recompute");
        Check(eraseSame && r.Kind == PaintLayer.UpdateKind.Full, "erasing with blur dabs present recomputes fully and exactly");
        Check(bruteSame, "blur via the grid gives exactly the brute-force colours");
    }

    static void BlurTiming()
    {
        Console.WriteLine("TEST blur timing (Release, single thread), ~10k vertices");
        var m = Cube(40);
        var baseCols = GradientColors(m);
        var blur = RandomDabs(m, 2000, 21, 0.03f, 0.06f).Select(d => new Dab(d.Position, d.Radius, d.Color, d.Strength, d.Hardness, blur: true).Sanitized()).ToArray();
        var layer = new PaintLayer(m.Pos, baseCols, m.Tri);
        layer.RecomputeAll(blur.AsSpan(0, 50)); // warm up
        var sw = Stopwatch.StartNew();
        layer.RecomputeAll(blur);
        double fullMs = sw.Elapsed.TotalMilliseconds;
        var inc = new PaintLayer(m.Pos, baseCols, m.Tri);
        var per = new List<double>();
        for (int n = 1; n <= 400; n++)
        {
            sw.Restart();
            inc.Update(blur.AsSpan(0, n));
            per.Add(sw.Elapsed.TotalMilliseconds);
        }
        per.Sort();
        Console.WriteLine($"  full recompute of 2000 blur dabs: {fullMs:0.0} ms; one more blur dab (a painting frame): median {per[per.Count / 2]:0.000} ms, p99 {per[(int)(per.Count * 0.99)]:0.000} ms");
        Check(per[(int)(per.Count * 0.99)] < 1.0, "a blur dab costs well under 1 ms while painting");
        Check(fullMs < 200, "a full recompute with 2000 blur dabs stays under 200 ms (only after erase/undo with blur present)");
    }
}
