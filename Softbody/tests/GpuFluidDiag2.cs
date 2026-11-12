using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

static partial class Program
{
    /// <summary>"gpu-diag2": Lenki's volume (5 m, 10 cm particles, viscosity 40, cohesion 2, density 10) on a floor: how many it fills and keeps.</summary>
    static void GpuFluidDiag2()
    {
        var l = FluidLiquid.Water; l.Viscosity = 40f; l.Cohesion = 2f; l.Density = 10f;
        var g = new GpuScene(capacity: 131072, particleSize: 0.1f);
        g.Colliders.Add(Rec(FleshCollider.Box(new Vector3(0, -0.05f, 0), Quaternion.Identity, new Vector3(50f, 0.05f, 50f))));
        var r = VolumeRec(1, new Vector3(0, 3f, 0), new Vector3(2.5f), l); r.MaxParticles = 131072;
        g.Emitters.Add(r);
        for (int f = 0; f < 240; f++)
        {
            g.Frame(surface: true);
            if (f < 5 || f % 30 == 0)
            {
                g.Rt.Sim.Finish();
                var st = g.Rt.Stats;
                Console.WriteLine($"  f{f}: emitter {g.Rt.CountOf(1)}, world {st.Count}, spawned {st.Spawned}, removed {st.Removed}, director spawns {g.Rt.Director.SpawnCount}, max speed {st.MaxSpeed:0.0}, bounds {st.Min}..{st.Max}, vertices {st.Vertices}, truncated {st.SurfaceTruncated}");
            }
        }
        g.Dispose();
    }
}
