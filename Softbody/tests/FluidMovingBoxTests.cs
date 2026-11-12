using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.19.0, Lenki in game (0.18.0): "I just made a moveable box collide with the water and it caused about 90% of the
// particles to despawn even though the box isnt anywhere near the size of the puddle."
static partial class Program
{
    /// <summary>The moving box's pose at frame f: swept through the puddle at 2 m/s, then pressed down into it, then lifted.</summary>
    static FleshCollider MovingBox(int f, out Vector3 velocity)
    {
        var half = new Vector3(0.1f);
        Vector3 c;
        if (f < 60) { c = new Vector3(-0.6f + 2f * f * FluidDt, 0.08f, 0f); velocity = new Vector3(2f, 0, 0); }
        else if (f < 90) { c = new Vector3(0.6f, 0.08f - 0.07f * (f - 60) / 30f, 0f); velocity = new Vector3(0, -0.07f / 0.5f, 0); }
        else { c = new Vector3(0.6f, 0.01f + 0.5f * (f - 90) / 60f, 0f); velocity = new Vector3(0, 0.5f, 0); }
        var box = FleshCollider.Box(c, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f), half);
        box.Velocity = velocity;
        return box;
    }

    static void FluidMovingBoxTests()
    {
        Console.WriteLine("TEST fluid and a moving box (0.19.0): a small box swept through a puddle, pressed into it and lifted, removes nothing");
        var water = FluidLiquid.Water;
        foreach (float thickness in new[] { 0.2f, 0.002f })
        {
            var floor = FleshCollider.Box(new Vector3(0, -thickness / 2f, 0), Quaternion.Identity, new Vector3(5f, thickness / 2f, 5f));
            var center = new Vector3(0, 0.06f, 0); var half = new Vector3(0.8f, 0.05f, 0.8f);

            // CPU
            var sim = new FluidSim(new FluidSettings());
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            sim.Colliders.Add(floor);
            int src = sim.AddSource(water);
            int n0 = sim.FillBox(src, center, Quaternion.Identity, half, Vector3.Zero);
            Run(sim, 1f);
            int worstBelow = 0;
            for (int f = 0; f < 150; f++)
            {
                if (sim.Colliders.Count > 1) sim.Colliders.RemoveAt(1);
                sim.Colliders.Add(MovingBox(f, out _));
                sim.Step(FluidDt);
                int below = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].Y < -thickness - 0.01f) below++;
                worstBelow = Math.Max(worstBelow, below);
            }
            sim.Colliders.RemoveAt(1);
            Run(sim, 1f);
            Console.WriteLine($"  CPU, {thickness * 1000:0} mm floor: {n0} particles, a 20 cm box swept, pressed and lifted: {sim.Count} left, at most {worstBelow} below the floor");
            Check(sim.Count == n0 && worstBelow == 0, $"CPU: a small moving box removes no liquid and pushes none through a {thickness * 1000:0} mm floor");

            // GPU
            var g = new GpuScene();
            g.Colliders.Add(Rec(floor));
            g.Emitters.Add(VolumeRec(1, center, half, water));
            g.Frame();
            int g0 = g.Count(1);
            g.Run(1f);
            int gBelow = 0;
            for (int f = 0; f < 150; f++)
            {
                if (g.Colliders.Count > 1) g.Colliders.RemoveAt(1);
                g.Colliders.Add(Rec(MovingBox(f, out _)));
                g.Frame();
                if (f % 10 == 9)
                {
                    int n = g.Read(out var x, out _, out _);
                    int below = 0; for (int i = 0; i < n; i++) if (x[i].Y < -thickness - 0.01f) below++;
                    gBelow = Math.Max(gBelow, below);
                }
            }
            g.Colliders.RemoveAt(1);
            g.Run(1f);
            int gLeft = g.Count(1);
            g.Dispose();
            Console.WriteLine($"  GPU, {thickness * 1000:0} mm floor: {g0} particles, the same box: {gLeft} left, at most {gBelow} below the floor");
            Check(gLeft == g0 && gBelow == 0, $"GPU: a small moving box removes no liquid and pushes none through a {thickness * 1000:0} mm floor");
        }
    }
}
