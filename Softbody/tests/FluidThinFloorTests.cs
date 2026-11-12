using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.18.0, Lenki in game (0.17.0, CPU): "after a few seconds of there being 12000 particles, they suddenly sunk through the
// floor", on a "very very thin box collider". A particle pushed down further in one substep than the box is thick ends up
// wholly below it, where the box test no longer sees it; the layer above loses its support and the pile follows.
static partial class Program
{
    static void FluidThinFloorTests()
    {
        Console.WriteLine("TEST fluid on a very thin box floor (0.18.0): 12,000 particles must stay on it, CPU and GPU");
        var water = FluidLiquid.Water;
        foreach (float size in new[] { 0.025f, 0.1f })
        {
            // a 2 mm thick floor, 20 m wide; a block of 12,000 particles dropped onto it from a little above
            var floor = FleshCollider.Box(new Vector3(0, -0.001f, 0), Quaternion.Identity, new Vector3(10f, 0.001f, 10f));
            int side = (int)MathF.Round(MathF.Cbrt(12000));
            var half = new Vector3(side * size / 2f, 12000f / (side * side) * size / 2f, side * size / 2f);
            var center = new Vector3(0, half.Y + 0.3f, 0);

            // CPU
            var sim = new FluidSim(new FluidSettings { ParticleSize = size });
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            sim.Colliders.Add(floor);
            int src = sim.AddSource(water);
            int n0 = sim.FillBox(src, center, Quaternion.Identity, half, Vector3.Zero);
            int worstBelow = 0, firstFrame = -1;
            for (int f = 0; f < 600; f++)
            {
                sim.Step(FluidDt);
                int below = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].Y < -0.01f) below++;
                if (below > 0 && firstFrame < 0) firstFrame = f;
                worstBelow = Math.Max(worstBelow, below);
            }
            int cpuLeft = sim.Count;
            Console.WriteLine($"  CPU, {size * 100:0.#} cm particles: {n0} dropped onto a 2 mm box floor, after 10 s {cpuLeft} left, at most {worstBelow} below the floor" +
                              (firstFrame >= 0 ? $" (first at frame {firstFrame})" : ""));
            Check(worstBelow == 0 && cpuLeft == n0, $"CPU: {n0} particles of {size * 100:0.#} cm stay on a very thin box floor");

            // GPU
            var g = new GpuScene(capacity: 16384, particleSize: size);
            g.Colliders.Add(Rec(floor));
            var r = VolumeRec(1, center, half, water); r.MaxParticles = 20000;
            g.Emitters.Add(r);
            g.Frame();
            int g0 = g.Count(1);
            int gWorst = 0, gFirst = -1;
            for (int f = 0; f < 600; f++)
            {
                g.Frame();
                if (f % 20 == 19)
                {
                    int n = g.Read(out var x, out _, out _);
                    int below = 0; for (int i = 0; i < n; i++) if (x[i].Y < -0.01f) below++;
                    if (below > 0 && gFirst < 0) gFirst = f;
                    gWorst = Math.Max(gWorst, below);
                }
            }
            int gLeft = g.Count(1);
            g.Dispose();
            Console.WriteLine($"  GPU, {size * 100:0.#} cm particles: {g0} dropped onto the same floor, after 10 s {gLeft} left, at most {gWorst} below it" +
                              (gFirst >= 0 ? $" (first by frame {gFirst})" : ""));
            Check(gWorst == 0 && gLeft == g0 && g0 > 11000, $"GPU: {g0} particles of {size * 100:0.#} cm stay on a very thin box floor");
        }
    }
}
