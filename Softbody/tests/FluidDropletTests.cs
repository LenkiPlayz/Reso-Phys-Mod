using System.Numerics;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.18.0: FluidEmitter.DropletSize (Lenki: "a particle combine radius so small droplets aren't entirely invisible or tiny in
// comparison"). A particle with few neighbours is drawn bigger; the liquid's body keeps its size; nothing vanishes.
static partial class Program
{
    /// <summary>24 lone particles 1 m apart, each somewhere awkward between the grid points.</summary>
    static Vector3[] LoneParticles()
    {
        var rng = new Random(7);
        var x = new Vector3[24];
        for (int i = 0; i < x.Length; i++)
            x[i] = new Vector3(i % 6, 2f + i / 6, 0f) + new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * 0.05f;
        return x;
    }

    /// <summary>For each particle: how many surface vertices lie within 3 particle sizes of it, and their mean distance.</summary>
    static (int fewest, float meanRadius) DropShapes(Vector3[] particles, Func<int, Vector3> vertex, int vertexCount, float s)
    {
        int fewest = int.MaxValue; double sum = 0; int n = 0;
        foreach (var p in particles)
        {
            int k = 0; double d = 0;
            for (int v = 0; v < vertexCount; v++) { float dist = Vector3.Distance(vertex(v), p); if (dist < 3f * s) { k++; d += dist; } }
            fewest = Math.Min(fewest, k);
            if (k > 0) { sum += d / k; n++; }
        }
        return (fewest, n > 0 ? (float)(sum / n) : 0f);
    }

    static void FluidDropletTests()
    {
        Console.WriteLine("TEST fluid DropletSize (0.18.0): lone droplets are drawn, and bigger, the body keeps its size, CPU and GPU");
        var water = FluidLiquid.Water;
        float s = 0.025f, cell = 0.8f * s;
        var lone = LoneParticles();

        // ---- CPU: lone particles
        (int, float) CpuLone(float droplet)
        {
            var surf = new FluidSurface { DropletSize = droplet };
            var src = new ushort[lone.Length];
            surf.Build(lone, src, lone.Length, new List<FluidLiquid> { water }, s);
            return DropShapes(lone, v => surf.Positions[v], surf.VertexCount, s);
        }
        var (c1n, c1r) = CpuLone(1f);
        var (c2n, c2r) = CpuLone(2f);
        Console.WriteLine($"  CPU, 24 lone particles at the default detail (grid {cell * 100:0.0} cm): with DropletSize 1 each has at least {c1n} vertices, " +
                          $"{c1r * 100:0.00} cm out on average; with 2, at least {c2n}, {c2r * 100:0.00} cm (a particle's own sphere is {0.6f * s * 100:0.00} cm)");
        Check(c1n >= 6 && c2n >= 6, "CPU: no lone droplet vanishes between the grid points");
        // (the vertices of a sphere this small on this grid sit a little inside it: their mean distance reads under its radius)
        Check(c1r > 0.4f * s && c2r > 1.6f * c1r && c2r < 2.4f * c1r, "CPU: DropletSize makes lone droplets bigger (about 2x at 2)");

        // ---- CPU: a small cluster grows, the body does not
        {
            var cl = new[] { new Vector3(0, 1, 0), new Vector3(s, 1, 0), new Vector3(0.5f * s, 1 + 0.87f * s, 0), new Vector3(0.5f * s, 1 + 0.29f * s, 0.82f * s) };
            float Size(float d)
            {
                var surf = new FluidSurface { DropletSize = d };
                surf.Build(cl, new ushort[4], 4, new List<FluidLiquid> { water }, s);
                var c = (cl[0] + cl[1] + cl[2] + cl[3]) / 4f; float r = 0;
                for (int v = 0; v < surf.VertexCount; v++) r += Vector3.Distance(surf.Positions[v], c);
                return surf.VertexCount > 0 ? r / surf.VertexCount : 0f;
            }
            var sim = new FluidSim(new FluidSettings());
            sim.Colliders.AddRange(Tank(0.6f, 0.4f, 0.5f));
            int src = sim.AddSource(water);
            sim.FillBox(src, new Vector3(0, 0.08f, 0), Quaternion.Identity, new Vector3(0.3f, 0.08f, 0.2f), Vector3.Zero);
            Run(sim, 1.5f);
            float Top(float d)
            {
                var surf = new FluidSurface { DropletSize = d };
                surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
                float y = 0; int n = 0;
                for (int v = 0; v < surf.VertexCount; v++) { var p = surf.Positions[v]; if (MathF.Abs(p.X) < 0.2f && MathF.Abs(p.Z) < 0.12f && p.Y > 0.05f) { y += p.Y; n++; } }
                return n > 0 ? y / n : 0f;
            }
            float k1 = Size(1f), k2 = Size(2f), t1 = Top(1f), t2 = Top(2f);
            Console.WriteLine($"  CPU, a cluster of 4 particles: {k1 * 100:0.00} cm across its middle at DropletSize 1, {k2 * 100:0.00} cm at 2; a settled pool's top at {t1 * 100:0.00} cm, and {t2 * 100:0.00} cm at 2");
            Check(k2 > 1.3f * k1, "CPU: a small cluster of particles is drawn bigger with DropletSize");
            Check(t1 > 0 && MathF.Abs(t2 - t1) < 0.2f * s, "CPU: the liquid's body keeps its size (a pool's top stays put)");
        }

        // ---- GPU: lone particles (straight through the simulation, frozen in place)
        (int, float) GpuLone(float droplet)
        {
            using var sim = new GpuFluidSim(GpuDevice(), new GpuFluidConfig { Capacity = 1024 }, FluidHlsl());
            var l = water; l.Gravity = 0f;
            sim.SetLiquids(new[] { new GpuLiquid { A = new Vector4(l.Viscosity, l.Cohesion, l.Adhesion, l.Friction), B = new Vector4(l.Density, 0f, 0f, 0f), Color = l.Color, Mask = uint.MaxValue, Alive = 1 } }, 1);
            sim.SetShapes(new GpuShapeGrid());
            sim.SetDropletSize(droplet);
            var spawns = lone.Select(p => new GpuSpawn { Pos = new Vector4(p, GpuFluidSim.AsFloat(0)), Vel = Vector4.Zero }).ToArray();
            sim.Step(0f, spawns, spawns.Length, false, true);
            sim.ReadSurface(out var verts, out _);
            return DropShapes(lone, v => new Vector3(verts[v * 10], verts[v * 10 + 1], verts[v * 10 + 2]), verts.Length / 10, s);
        }
        var (g1n, g1r) = GpuLone(1f);
        var (g2n, g2r) = GpuLone(2f);
        Console.WriteLine($"  GPU, the same lone particles: with DropletSize 1 each has at least {g1n} vertices, {g1r * 100:0.00} cm out on average; with 2, at least {g2n}, {g2r * 100:0.00} cm");
        Check(g1n >= 6 && g2n >= 6, "GPU: no lone droplet vanishes between the grid points");
        Check(g1r > 0.4f * s && g2r > 1.6f * g1r && g2r < 2.4f * g1r && MathF.Abs(g1r - c1r) < 0.15f * s && MathF.Abs(g2r - c2r) < 0.15f * s, "GPU: DropletSize makes lone droplets bigger, as on the CPU");

        // ---- GPU: the body keeps its size
        {
            var g = new GpuScene();
            g.Add(Tank(0.6f, 0.4f, 0.5f));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.08f, 0), new Vector3(0.3f, 0.08f, 0.2f), water));
            g.Run(1.5f);
            float Top(float d)
            {
                g.Frame(surface: true, droplet: d);
                g.Rt.Sim.ReadSurface(out var v, out _);
                float y = 0; int n = 0;
                for (int i = 0; i < v.Length / 10; i++) { float px = v[i * 10], py = v[i * 10 + 1], pz = v[i * 10 + 2]; if (MathF.Abs(px) < 0.2f && MathF.Abs(pz) < 0.12f && py > 0.05f) { y += py; n++; } }
                return n > 0 ? y / n : 0f;
            }
            float t1 = Top(1f), t2 = Top(2f);
            g.Dispose();
            Console.WriteLine($"  GPU, the settled pool's top at {t1 * 100:0.00} cm with DropletSize 1, {t2 * 100:0.00} cm with 2");
            Check(t1 > 0 && MathF.Abs(t2 - t1) < 0.2f * s, "GPU: the liquid's body keeps its size (a pool's top stays put)");
        }
    }
}
