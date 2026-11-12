using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.17.0: FluidEmitter.ParticleSize and SurfaceDetail, on the CPU and the GPU.
static partial class Program
{
    static void FluidSizeTests()
    {
        Console.WriteLine("TEST fluid particle size and surface detail (0.17.0), CPU and GPU");
        var water = FluidLiquid.Water;

        // ---- CPU: 5 cm particles
        {
            var sim = new FluidSim(new FluidSettings { ParticleSize = 0.05f });
            sim.Colliders.AddRange(Tank(1.2f, 0.8f, 1.0f));
            int src = sim.AddSource(water);
            int n0 = sim.FillBox(src, new Vector3(-0.3f, 0.3f, 0f), Quaternion.Identity, new Vector3(0.3f, 0.3f, 0.4f), Vector3.Zero);
            Run(sim, 3f);
            int outside = 0; for (int i = 0; i < sim.Count; i++) { var p = sim.X[i]; if (MathF.Abs(p.X) > 0.62f || MathF.Abs(p.Z) > 0.42f || p.Y < -0.01f) outside++; }
            var (mean, max) = Densities(sim);
            var pour = new FluidSim(new FluidSettings { ParticleSize = 0.05f });
            int ps = pour.AddSource(water);
            var e = new FluidEmitterState { Source = ps, Mode = FluidEmitMode.Pour, Active = true, Position = new Vector3(0, 2, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Rate = 4f, Speed = 1f, NozzleRadius = 0.06f };
            // (1.5 s: longer, and the first drops would fall past the 20 m below an emitter where liquid is removed)
            Run(pour, 1.5f, f => pour.Emit(e, FluidDt));
            float litres = pour.Count * pour.ParticleVolume * 1000f, want = 4f * (int)(1.5f / FluidDt) * FluidDt;
            Console.WriteLine($"  CPU, 5 cm particles (125 ml each): a {n0 * sim.ParticleVolume * 1000:0} L dam break in a 1.2 x 0.8 m tank keeps {sim.Count} of {n0}, {outside} outside, density {mean:0.00} ({max:0.00} at most), " +
                              $"{sim.MeanNeighbours:0} neighbours, step {sim.StepMs:0.00} ms; pouring 4 L/s for 1.5 s gives {litres:0.00} L in {pour.Count} particles ({want:0.00} L poured)");
            Check(sim.Count == n0 && outside == 0 && mean > 0.85f && mean < 1.15f && max < 1.6f, "CPU: 5 cm particles keep the water in its tank at its rest density");
            Check(MathF.Abs(litres - want) < 0.15f, "CPU: with 5 cm particles an emitter still pours its rate in litres");
        }

        // ---- CPU: surface detail 2 against 1
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.6f, 0.4f, 0.5f));
            int src = sim.AddSource(water);
            sim.FillBox(src, new Vector3(0, 0.08f, 0), Quaternion.Identity, new Vector3(0.3f, 0.08f, 0.2f), Vector3.Zero);
            Run(sim, 1.5f);
            var s1 = new FluidSurface(); s1.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            var s2 = new FluidSurface { CellScale = 0.8f / 2f, MaxBricks = 24000 * 4 }; s2.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
            int up = 0, total = 0;
            for (int v = 0; v < s2.VertexCount; v++) { var p = s2.Positions[v]; if (MathF.Abs(p.X) < 0.2f && MathF.Abs(p.Z) < 0.12f && p.Y > 0.05f) { total++; if (s2.Normals[v].Y > 0.8f) up++; } }
            var dsim = new FluidSim(new FluidSettings());
            var l = water; l.Gravity = 0f;
            int ds = dsim.AddSource(l);
            dsim.FillBox(ds, Vector3.Zero, Quaternion.Identity, new Vector3(0.04f), Vector3.Zero);
            var drop = new FluidSurface { CellScale = 0.4f };
            drop.Build(dsim.X, dsim.Src, dsim.Count, dsim.Liquids, dsim.Spacing);
            int open = OpenEdges(drop.Indices, drop.IndexCount);
            float ratio = (float)s2.VertexCount / Math.Max(1, s1.VertexCount);
            Console.WriteLine($"  CPU, a settled pool's surface at detail 1: {s1.VertexCount} vertices in {s1.BuildMs:0.0} ms; at detail 2: {s2.VertexCount} ({ratio:0.0}x) in {s2.BuildMs:0.0} ms, " +
                              $"{(total > 0 ? 100f * up / total : 0):0}% of the top's normals up; a drop at detail 2: {drop.VertexCount} vertices, {open} open edges");
            Check(ratio > 2.5f && ratio < 6f && !s2.Truncated, "CPU: SurfaceDetail 2 gives a surface about 4x as fine");
            Check(total > 0 && up > 0.8f * total && open == 0 && drop.VertexCount > 50, "CPU: a finer surface is still flat on a pool and closed around a drop");
        }

        // ---- GPU: 5 cm particles
        {
            var g = new GpuScene(particleSize: 0.05f);
            g.Add(Tank(1.2f, 0.8f, 1.0f));
            g.Emitters.Add(VolumeRec(1, new Vector3(-0.3f, 0.3f, 0f), new Vector3(0.3f, 0.3f, 0.4f), water));
            g.Frame();
            int n0 = g.Count(1);
            g.Run(3f);
            int n = g.Read(out var x, out _, out _);
            int outside = 0; for (int i = 0; i < n; i++) { var p = x[i]; if (MathF.Abs(p.X) > 0.62f || MathF.Abs(p.Z) > 0.42f || p.Y < -0.01f) outside++; }
            var (mean, max) = GpuDensities(x, 0.05f);
            g.Dispose();
            var g2 = new GpuScene(particleSize: 0.05f);
            var e = EmitterRec(1, 0, new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), water);
            e.Rate = 4f; e.Speed = 1f; e.NozzleRadius = 0.06f;
            g2.Emitters.Add(e);
            g2.Run(1.5f);
            int pn = g2.Count(1);
            float litres = pn * 0.05f * 0.05f * 0.05f * 1000f, want = 4f * (int)(1.5f / FluidDt) * FluidDt;
            g2.Dispose();
            Console.WriteLine($"  GPU, 5 cm particles: the dam break keeps {n} of {n0}, {outside} outside, density {mean:0.00} ({max:0.00} at most); pouring 4 L/s for 1.5 s gives {litres:0.00} L in {pn} particles ({want:0.00} L poured)");
            Check(n == n0 && n0 > 2000 && outside == 0 && mean > 0.85f && mean < 1.15f && max < 1.6f, "GPU: 5 cm particles keep the water in its tank at its rest density");
            Check(MathF.Abs(litres - want) < 0.15f, "GPU: with 5 cm particles an emitter still pours its rate in litres");
        }

        // ---- GPU: surface detail 2 against 1 (live, the same liquid)
        {
            var g = new GpuScene();
            g.Add(Tank(0.6f, 0.4f, 0.5f));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.08f, 0), new Vector3(0.3f, 0.08f, 0.2f), water));
            g.Frame();
            int before = g.Count(1);
            g.Run(1.5f);
            g.Frame(surface: true, detail: 1f);
            g.Rt.Sim.ReadSurface(out var v1, out _);
            g.Frame(surface: true, detail: 2f);
            g.Rt.Sim.ReadSurface(out var v2, out _);
            int after = g.Count(1);
            bool truncated = g.Rt.Stats.SurfaceTruncated;
            int up = 0, total = 0;
            for (int v = 0; v < v2.Length / 10; v++)
            {
                float px = v2[v * 10], py = v2[v * 10 + 1], pz = v2[v * 10 + 2];
                if (MathF.Abs(px) < 0.2f && MathF.Abs(pz) < 0.12f && py > 0.05f) { total++; if (v2[v * 10 + 4] > 0.8f) up++; }
            }
            g.Dispose();
            var d = new GpuScene();
            var l = water; l.Gravity = 0f;
            d.Emitters.Add(VolumeRec(1, Vector3.Zero, new Vector3(0.04f), l));
            d.Frame(surface: true, detail: 2f);
            d.Rt.Sim.ReadSurface(out var dv, out var di);
            int open = OpenEdges(di.Select(u => (int)u).ToArray(), di.Length);
            d.Dispose();
            float ratio = (float)(v2.Length / 10) / Math.Max(1, v1.Length / 10);
            Console.WriteLine($"  GPU, the pool's surface at detail 1: {v1.Length / 10} vertices; at detail 2: {v2.Length / 10} ({ratio:0.0}x), {(total > 0 ? 100f * up / total : 0):0}% of the top's normals up, " +
                              $"{after} of {before} particles kept through the change; a drop at detail 2: {dv.Length / 10} vertices, {open} open edges");
            Check(ratio > 2.5f && ratio < 6f && !truncated && after == before && before > 0, "GPU: SurfaceDetail 2 gives a surface about 4x as fine, live (the liquid stays)");
            Check(total > 0 && up > 0.8f * total && open == 0 && dv.Length / 10 > 50, "GPU: a finer surface is still flat on a pool and closed around a drop");
        }
    }

    static int OpenEdges(int[] idx, int count)
    {
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = idx[t + e], b = idx[t + (e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        return edges.Values.Count(c => c != 2);
    }
}
