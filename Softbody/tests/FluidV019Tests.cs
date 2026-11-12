using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.19.0, Lenki's round of requests: a random velocity for pouring and volumes ("an explosion of water"), the limits raised
// (rate, particle size, viscosity), and a surface that does not break apart when it runs out of room at SurfaceDetail 3.
static partial class Program
{
    static void FluidV019Tests()
    {
        Console.WriteLine("TEST fluid 0.19.0: RandomVelocity, higher limits, the surface lowering its own detail, CPU and GPU");
        var water = FluidLiquid.Water;
        float s = 0.025f;

        // ---- RandomVelocity on a Volume
        static (float mean, float max, float drift) Speeds(IList<Vector3> v)
        {
            float sum = 0, max = 0; var net = Vector3.Zero;
            foreach (var x in v) { float l = x.Length(); sum += l; max = MathF.Max(max, l); net += x; }
            return (sum / Math.Max(1, v.Count), max, (net / Math.Max(1, v.Count)).Length());
        }
        {
            var sim = new FluidSim(new FluidSettings());
            int src = sim.AddSource(water);
            sim.FillBox(src, Vector3.Zero, Quaternion.Identity, new Vector3(0.15f), Vector3.Zero, int.MaxValue, 4f);
            var (mean, max, drift) = Speeds(sim.V.Take(sim.Count).ToList());
            var g = new GpuScene();
            var l = water; l.Gravity = 0f; l.Damping = 0f;
            var r = VolumeRec(1, Vector3.Zero, new Vector3(0.15f), l); r.RandomVelocity = 4f;
            g.Emitters.Add(r);
            g.Frame();
            int n = g.Read(out _, out var gv, out _);
            var (gmean, gmax, gdrift) = Speeds(gv);
            g.Run(0.5f);
            g.Read(out var gx, out _, out _);
            float spread = gx.Max(p => p.Length());
            g.Dispose();
            Console.WriteLine($"  RandomVelocity 4 m/s on a 30 cm volume: CPU {mean:0.00} m/s on average (at most {max:0.00}, net drift {drift:0.00}); " +
                              $"GPU {gmean:0.00} (at most {gmax:0.00}, drift {gdrift:0.00}), and after half a second it has burst {spread:0.00} m out");
            Check(mean > 2.6f && mean < 3.4f && max <= 4.01f && drift < 0.3f, "CPU: RandomVelocity gives every new particle a random push up to that speed (3/4 of it on average, every way alike)");
            // (read after the first step: its pressure pushes some particles past the speed they were given; 0.21.0's pressure-wave
            // damping takes a little off where the burst squeezes the block in its first substeps: 2.29 on average)
            Check(gmean > 2.0f && gmean < 3.4f && gmax < 6f && gdrift < 0.3f && spread > 1.2f, "GPU: RandomVelocity bursts a Volume like an explosion of water");
        }

        // ---- viscosity above 1
        float Spread(float viscosity, bool gpu)
        {
            var floor = FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(3f, 0.1f, 3f));
            var l = water; l.Viscosity = viscosity;
            Vector3[] x;
            if (!gpu)
            {
                var sim = new FluidSim(new FluidSettings());
                sim.Colliders.Add(floor);
                int src = sim.AddSource(l);
                sim.FillBox(src, new Vector3(0, 0.12f, 0), Quaternion.Identity, new Vector3(0.1f), Vector3.Zero);
                Run(sim, 1f);
                x = sim.X.Take(sim.Count).ToArray();
            }
            else
            {
                var g = new GpuScene();
                g.Colliders.Add(Rec(floor));
                g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.12f, 0), new Vector3(0.1f), l));
                g.Run(1f);
                g.Read(out x, out _, out _);
                g.Dispose();
            }
            return x.Average(p => new Vector2(p.X, p.Z).Length());
        }
        // shear in a floating 20 cm cube (top half moving one way, bottom half the other): how much is left after 0.1 s
        float Shear(float viscosity, bool gpu)
        {
            var l = water; l.Viscosity = viscosity; l.Gravity = 0f; l.Damping = 0f; l.Cohesion = 0f;
            var pts = new List<(Vector3 p, Vector3 v)>();
            for (int y = 0; y < 8; y++) for (int z = 0; z < 8; z++) for (int xx = 0; xx < 8; xx++)
                    {
                        var p = new Vector3((xx + 0.5f) * s - 0.1f, (y + 0.5f) * s - 0.1f, (z + 0.5f) * s - 0.1f);
                        pts.Add((p, new Vector3(p.Y > 0 ? 1f : -1f, 0, 0)));
                    }
            Vector3[] x, v;
            if (!gpu)
            {
                var sim = new FluidSim(new FluidSettings());
                int src = sim.AddSource(l);
                foreach (var (p, vel) in pts) sim.Spawn(src, p, vel);
                Run(sim, 0.1f);
                x = sim.X.Take(sim.Count).ToArray(); v = sim.V.Take(sim.Count).ToArray();
            }
            else
            {
                using var sim = new GpuFluidSim(GpuDevice(), new GpuFluidConfig { Capacity = 4096 }, FluidHlsl());
                sim.SetLiquids(new[] { new GpuLiquid { A = new Vector4(l.Viscosity, 0f, l.Adhesion, l.Friction), B = new Vector4(1f, 0f, 0f, 0f), Color = l.Color, Mask = uint.MaxValue, Alive = 1 } }, 1);
                sim.SetShapes(new GpuShapeGrid());
                sim.SetViscosityPasses((int)MathF.Ceiling(viscosity));
                var sp = pts.Select(q => new GpuSpawn { Pos = new Vector4(q.p, GpuFluidSim.AsFloat(0)), Vel = new Vector4(q.v, 0) }).ToArray();
                sim.Step(FluidDt, sp, sp.Length, false, false);
                for (int f = 1; f < (int)(0.1f / FluidDt); f++) sim.Step(FluidDt, null, 0, false, false);
                int n = sim.ReadParticles(out var p4, out var v4);
                x = p4.Take(n).Select(q => new Vector3(q.X, q.Y, q.Z)).ToArray(); v = v4.Take(n).Select(q => new Vector3(q.X, q.Y, q.Z)).ToArray();
            }
            float cy = x.Average(p => p.Y);
            float top = 0, bottom = 0; int nt = 0, nb = 0;
            for (int i = 0; i < x.Length; i++) { if (x[i].Y > cy) { top += v[i].X; nt++; } else { bottom += v[i].X; nb++; } }
            return MathF.Abs(top / Math.Max(1, nt) - bottom / Math.Max(1, nb)) / 2f;
        }
        {
            float c1 = Spread(1f, false), c5 = Spread(5f, false), g1 = Spread(1f, true), g20 = Spread(20f, true);
            float sc1 = Shear(1f, false), sc5 = Shear(5f, false), sg1 = Shear(1f, true), sg5 = Shear(5f, true), sg20 = Shear(20f, true);
            Console.WriteLine($"  shear left after 0.1 s in a floating 20 cm cube (started at 1 m/s): CPU {sc1:0.000} m/s at viscosity 1, {sc5:0.000} at 5; GPU {sg1:0.000} at 1, {sg5:0.000} at 5, {sg20:0.000} at 20. " +
                              $"A 20 cm block dropped on a floor spreads to CPU {c1 * 100:0.0} / {c5 * 100:0.0} cm (1 / 5), GPU {g1 * 100:0.0} / {g20 * 100:0.0} cm (1 / 20)");
            Check(sc5 < 0.7f * sc1 && float.IsFinite(c5), "CPU: viscosity above 1 is thicker still: shear dies down faster (and it stays stable)");
            Check(sg5 < 0.7f * sg1 && sg20 < sg5 && float.IsFinite(g20) && g20 <= g1, "GPU: viscosity above 1 is thicker still, up to 20: shear dies down faster (and it stays stable)");
        }

        // ---- the GPU pours 500 L/s
        {
            var g = new GpuScene(capacity: 262144);
            var e = EmitterRec(1, 0, new Vector3(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), water);
            e.Rate = 500f; e.Speed = 2f; e.NozzleRadius = 0.3f; e.MaxParticles = 262144;
            g.Emitters.Add(e);
            g.Run(0.25f);
            int n = g.Count(1);
            float litres = n * s * s * s * 1000f, want = 500f * (int)(0.25f / FluidDt) * FluidDt;
            g.Dispose();
            Console.WriteLine($"  GPU pouring 500 L/s for a quarter of a second: {litres:0.0} L in {n} particles ({want:0.0} L poured)");
            Check(MathF.Abs(litres - want) < 0.02f * want, "GPU: an emitter pours up to thousands of litres a second");
        }

        // ---- 30 cm particles
        {
            var floor = FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(30f, 0.5f, 30f));
            var sim = new FluidSim(new FluidSettings { ParticleSize = 0.3f });
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            sim.Colliders.Add(floor);
            int src = sim.AddSource(water);
            int n0 = sim.FillBox(src, new Vector3(0, 1.6f, 0), Quaternion.Identity, new Vector3(1.5f), Vector3.Zero);
            Run(sim, 3f);
            var (mean, max) = Densities(sim);
            int below = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].Y < -0.01f) below++;
            var g = new GpuScene(particleSize: 0.3f);
            g.Colliders.Add(Rec(floor));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 1.6f, 0), new Vector3(1.5f), water));
            g.Frame();
            int g0 = g.Count(1);
            g.Run(3f);
            int gn = g.Read(out var gx, out _, out _);
            int gBelow = gx.Count(p => p.Y < -0.01f);
            var (gm, gmax) = GpuDensities(gx, 0.3f);
            g.Dispose();
            Console.WriteLine($"  30 cm particles (27 L each): a 3 m block on a floor keeps CPU {sim.Count} of {n0} ({below} below, density {mean:0.00}), GPU {gn} of {g0} ({gBelow} below, density {gm:0.00})");
            Check(sim.Count == n0 && below == 0 && mean > 0.85f && mean < 1.15f, "CPU: 30 cm particles behave like the default ones");
            Check(gn == g0 && gBelow == 0 && gm > 0.85f && gm < 1.15f, "GPU: 30 cm particles behave like the default ones");
        }

        // ---- the surface lowers its own detail instead of breaking apart (a big thin sheet at SurfaceDetail 3)
        {
            var g = new GpuScene(capacity: 65536);
            g.Colliders.Add(Rec(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(10f, 0.1f, 10f))));
            var l = water; l.Cohesion = 0f;
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.0125f, 0), new Vector3(3.2f, 0.0125f, 2.4f), l));
            int lowestUsed = 100; bool truncatedAtEnd = true; float used = 3f;
            for (int f = 0; f < 90; f++)
            {
                g.Frame(surface: true, detail: 3f);
                if (f >= 60) { g.Rt.Sim.Finish(); used = g.Rt.Sim.SurfaceDetailUsed; truncatedAtEnd = g.Rt.Stats.SurfaceTruncated; }
            }
            int n = g.Read(out var x, out _, out _);
            g.Rt.Sim.ReadSurface(out var verts, out _);
            // how much of the sheet has surface over it: particles with a vertex within 2 particle sizes
            var cells = new HashSet<(int, int)>();
            for (int v = 0; v < verts.Length / 10; v++) cells.Add(((int)MathF.Floor(verts[v * 10] / 0.05f), (int)MathF.Floor(verts[v * 10 + 2] / 0.05f)));
            int covered = x.Count(p => cells.Contains(((int)MathF.Floor(p.X / 0.05f), (int)MathF.Floor(p.Z / 0.05f))));
            g.Dispose();
            Console.WriteLine($"  GPU, a {n}-particle sheet over 6.4 x 4.8 m at SurfaceDetail 3: built at {used:0.00}, {(truncatedAtEnd ? "still" : "no longer")} out of room, {verts.Length / 10} vertices, " +
                              $"{100f * covered / Math.Max(1, n):0}% of the particles under the surface");
            Check(used < 3f && !truncatedAtEnd && covered > 0.95f * n, "GPU: a surface that does not fit lowers its own detail (just enough) instead of breaking into pieces");

            // the CPU's rule: a build that ran out of blocks comes down, then climbs back while it fits
            var sim = new FluidSim(new FluidSettings { MaxParticles = 30000 });
            int src = sim.AddSource(l);
            sim.FillBox(src, new Vector3(0, 0.0125f, 0), Quaternion.Identity, new Vector3(2.4f, 0.0125f, 1.6f), Vector3.Zero);
            var surf = new FluidSurface { MaxBricks = 24000 };
            float cu = 3f; int builds = 0;
            do { surf.CellScale = 0.8f / cu; surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing); cu = surf.NextDetail(3f, cu); builds++; }
            while (surf.Truncated && builds < 20);
            Console.WriteLine($"  CPU, a {sim.Count}-particle sheet at SurfaceDetail 3 with 24,000 blocks: fits at {cu:0.00} after {builds} builds ({surf.Bricks} blocks, {surf.VertexCount} vertices)");
            Check(!surf.Truncated && cu < 3f && builds < 20, "CPU: a surface that does not fit lowers its own detail instead of leaving holes");
        }
    }
}
