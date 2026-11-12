using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

static partial class Program
{
    /// <summary>"gpu-diag": a 50,000-particle dam break in a 2 m tank, frame by frame: the first particles to get out, and how.</summary>
    static void GpuFluidDiag()
    {
        int target = 100000;
        var g = new GpuScene(capacity: 131072);
        g.Add(Tank(2f, 2f, 2.5f));
        float s = 0.025f, side = MathF.Cbrt(target) * s;
        var r = VolumeRec(1, new Vector3(-1f + side / 2, side / 2, -1f + side / 2), new Vector3(side / 2), FluidLiquid.Water);
        r.MaxParticles = target;
        g.Emitters.Add(r);
        int reported = 0;
        var prevX = new Dictionary<int, Vector3>();
        Vector3[] last = null; Vector3[] lastV = null;
        for (int f = 0; f < 120 && reported < 10; f++)
        {
            g.Frame();
            int n = g.Read(out var x, out var v, out _);
            float vmax = 0, ymax = 0; for (int i = 0; i < n; i++) { vmax = MathF.Max(vmax, v[i].Length()); ymax = MathF.Max(ymax, x[i].Y); }
            if (f % 10 == 0) Console.WriteLine($"  f{f}: highest particle {ymax:0.00} m, fastest {vmax:0.0} m/s");
            int outside = 0;
            for (int i = 0; i < n && reported < 10; i++)
            {
                var p = x[i];
                if (MathF.Abs(p.X) > 1.01f || MathF.Abs(p.Z) > 1.01f || p.Y < -0.01f)
                {
                    outside++;
                    if (outside <= 3)
                    {
                        // the nearest particle last frame (indices change every frame: the sort)
                        string from = "";
                        if (last != null) { int best = -1; float bd = 1e9f; for (int j = 0; j < last.Length; j++) { float d = Vector3.DistanceSquared(last[j], p - v[i] * FluidDt); if (d < bd) { bd = d; best = j; } } if (best >= 0) from = $" (last frame near {last[best]} moving {lastV[best]})"; }
                        Console.WriteLine($"  f{f}: out at {p} moving {v[i]} ({v[i].Length():0.0} m/s){from}; fastest {vmax:0.0} m/s, {g.Rt.Stats.MeanNeighbours:0} neighbours");
                        reported++;
                    }
                }
            }
            last = x; lastV = v;
        }
        g.Dispose();
    }
}
