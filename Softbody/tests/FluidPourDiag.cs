using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Fluid;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// diagnosis of the in-game freeze: pour for 15 s in a few layouts, and time every frame (step and surface)
static partial class Program
{
    static void FluidPourDiag()
    {
        void Scenario(string name, Action<FluidSim> setup)
        {
            var sim = new FluidSim(new FluidSettings());
            sim.Settings.KillMin = new Vector3(-50, 1.5f - 20, -50); sim.Settings.KillMax = new Vector3(50, 51.5f, 50); // as the plugin keeps it
            setup(sim);
            int src = sim.AddSource(FluidLiquid.Water);
            var e = new FluidEmitterState { Source = src, Mode = FluidEmitMode.Pour, Active = true, Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), Rate = 1f, Speed = 1.5f, NozzleRadius = 0.02f, MaxParticles = 4000 };
            var surf = new FluidSurface();
            double worstStep = 0, worstSurf = 0; int worstAt = 0;
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < 900; f++)
            {
                sim.Emit(e, FluidDt);
                sim.Step(FluidDt);
                surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, sim.Spacing);
                if (sim.StepMs + surf.BuildMs > worstStep + worstSurf) { worstStep = sim.StepMs; worstSurf = surf.BuildMs; worstAt = f; }
                if (sw.Elapsed.TotalSeconds > 60) { Console.WriteLine($"  {name}: gave up after {f} frames (60 s)"); break; }
                if (f % 150 == 149)
                {
                    var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                    for (int i = 0; i < sim.Count; i++) { lo = Vector3.Min(lo, sim.X[i]); hi = Vector3.Max(hi, sim.X[i]); }
                    int below = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].Y < -0.01f) below++;
                    Console.WriteLine($"  {name} t={(f + 1) / 60f:0.0}s: {sim.Count} particles ({below} below the floor), bounds {lo} .. {hi}, step {sim.StepMs:0.00} ms, surface {surf.BuildMs:0.00} ms ({surf.Bricks} blocks, {surf.VertexCount} vertices), {sim.ShapeCount} shapes near");
                }
            }
            Console.WriteLine($"  {name}: worst frame {worstAt} (step {worstStep:0.0} ms, surface {worstSurf:0.0} ms)");
        }
        Scenario("box floor", sim => sim.Colliders.Add(FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(50, 0.5f, 50))));
        Scenario("no floor", sim => { });
        Scenario("mesh floor (2 m triangles)", sim =>
        {
            var (pos, tris) = BumpyFloor(40, 80f, new Vector3(-40, 0, -40));
            sim.Colliders.Add(FleshCollider.MeshShape(TriangleBvh.Build(pos, tris), Matrix4x4.Identity, false));
        });
        Scenario("mesh floor (fine, 5 cm)", sim =>
        {
            var (pos, tris) = BumpyFloor(200, 10f, new Vector3(-5, 0, -5));
            sim.Colliders.Add(FleshCollider.MeshShape(TriangleBvh.Build(pos, tris), Matrix4x4.Identity, false));
        });
    }
}
