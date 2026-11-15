using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.29.2 (fork): the thick liquid at other particle sizes. In game, at 7.5 cm particles, ThickLiquid "didn't do much": its
// viscosity is a physical one, the same at every size, while a world built for bigger particles has bigger flows (and XSPH, its
// "off", grows with the particle size). "thick-size" measures it: a dam break and a spinning blob with the scene scaled with the
// particle size (the same particle count), and one scene at three sizes; water, XSPH and the thick liquid with its viscosity
// scaled by (size / 2.5 cm)^p for a few p.
static partial class Program
{
    sealed record SizeRun(float[] Front, float[] Height, int N0, int N, int ItsMedian, int ItsMax, float ItsMean, int Solves, int Unconverged, float TopSpeed, bool Finite, double MsPerFrame);

    /// <summary>
    /// A dam break in the 0.27.0 tank scaled by <paramref name="k"/> (at 1: a 30 x 40 x 30 cm column at one end of a 1.2 x 0.3 m
    /// tank, walls 0.6 m) with particles of size <paramref name="s"/>. The front (the 99th percentile of x, from the left wall) and
    /// the height (the 99th percentile of y) over k, at t sqrt(k) for each t in <paramref name="times"/>: the same moments of a
    /// scene scaled by k, as gravity's time goes as sqrt(k).
    /// </summary>
    static SizeRun DamBreakAt(float k, float s, bool thick, float viscosity, float power, float[] times, int capacity = 16384)
    {
        var g = new ThickScene(capacity, s);
        g.Rt.Director.ThickSizePower = power;
        g.Add(Tank(1.2f * k, 0.3f * k, 0.6f * k));
        g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(-0.45f * k, 0.2f * k, 0f), new Vector3(0.15f * k, 0.2f * k, 0.15f * k), HoneyLike(viscosity)), thick));
        var at = times.Select(t => (int)MathF.Round(t * MathF.Sqrt(k) / FluidDt)).ToArray();
        var front = new float[times.Length]; var height = new float[times.Length];
        float vmax = 0; int unconverged = 0, solves = 0, n0 = 0; long itSum = 0; var its = new List<int>();
        bool finite = true; int n = 0;
        var sw = Stopwatch.StartNew();
        int done = 0;
        for (int f = 1; f <= at[^1]; f++)
        {
            g.Frame();
            var st = g.Rt.Stats;
            if (f > 6) vmax = MathF.Max(vmax, st.MaxSpeed);
            if (f == 6) { g.Rt.Sim.Finish(); n0 = g.Rt.Stats.Count; }
            if (st.IvSolves > 0) { its.Add(st.IvIterationsMax); solves += st.IvSolves; unconverged += st.IvUnconverged; itSum += st.IvIterationsSum; }
            while (done < at.Length && f == at[done])
            {
                var (x, _) = g.Particles();
                n = x.Length;
                finite &= x.All(p => float.IsFinite(p.X + p.Y + p.Z));
                var xs = x.Select(p => p.X).OrderBy(a => a).ToList();
                var ys = x.Select(p => p.Y).OrderBy(a => a).ToList();
                front[done] = xs.Count > 0 ? (xs[(int)(xs.Count * 0.99f)] + 0.6f * k) / k : 0f;
                height[done] = ys.Count > 0 ? ys[(int)(ys.Count * 0.99f)] / k : 0f;
                done++;
            }
        }
        double ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, at[^1]);
        g.Dispose();
        its.Sort();
        return new SizeRun(front, height, n0, n, its.Count > 0 ? its[its.Count / 2] : 0, its.Count > 0 ? its[^1] : 0,
                           solves > 0 ? (float)itSum / solves : 0f, solves, unconverged, vmax, finite, ms);
    }

    /// <summary>A cube of 30 k cm in zero gravity spun at 2 / sqrt(k) rad/s about the vertical: its angular velocity over the start
    /// at t sqrt(k) for each t in <paramref name="times"/>.</summary>
    static (float[] share, int unconverged, int itsMedian) SpinAt(float k, float s, bool thick, float viscosity, float power, float[] times)
    {
        var g = new ThickScene(16384, s);
        g.Rt.Director.ThickSizePower = power;
        var l = HoneyLike(viscosity); l.Gravity = 0f;
        g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(0, 1f * k, 0), new Vector3(0.15f * k), l), thick));
        for (int f = 0; f < 10; f++) g.Frame();
        var (x0, _) = g.Particles();
        var c0 = x0.Aggregate(Vector3.Zero, (a, b) => a + b) / x0.Length;
        var vel = new Vector4[x0.Length];
        float w0 = 2f / MathF.Sqrt(k);
        for (int i = 0; i < x0.Length; i++) { var d = x0[i] - c0; vel[i] = new Vector4(-w0 * d.Z, 0f, w0 * d.X, 0f); }
        g.Rt.Sim.WriteVelocities(vel, x0.Length);
        var at = times.Select(t => (int)MathF.Round(t * MathF.Sqrt(k) / FluidDt)).ToArray();
        var ws = new float[times.Length];
        int done = 0, unconverged = 0; var its = new List<int>();
        for (int f = 1; f <= at[^1]; f++)
        {
            g.Frame();
            unconverged += g.Rt.Stats.IvUnconverged;
            if (g.Rt.Stats.IvSolves > 0) its.Add(g.Rt.Stats.IvIterationsMax);
            while (done < at.Length && f == at[done])
            {
                var (x, v) = g.Particles();
                var cc = x.Aggregate(Vector3.Zero, (a, b) => a + b) / x.Length;
                double L = 0, I = 0;
                for (int i = 0; i < x.Length; i++) { var d = x[i] - cc; L += -d.Z * v[i].X + d.X * v[i].Z; I += d.X * d.X + d.Z * d.Z; }
                ws[done++] = (float)(L / Math.Max(1e-9, I)) / w0;
            }
        }
        g.Dispose();
        its.Sort();
        return (ws, unconverged, its.Count > 0 ? its[its.Count / 2] : 0);
    }

    static string Cm(float s) => $"{s * 100:0.#} cm";

    /// <summary>
    /// 0.29.2 (fork), in "thick": the dam break scaled with the particle size looks the same at 7.5 cm as at 2.5 cm (its front
    /// and height over k at the same t sqrt(k)), which it did not before (0.27.0 to 0.29.1: the same viscosity at every size, so
    /// a world built for 7.5 cm particles ran far runnier), and each solve costs no more iterations than at 2.5 cm.
    /// </summary>
    static void ThickSizeLookTests()
    {
        Console.WriteLine($"  -- the same look at 7.5 cm: the dam break scaled by 3 (the same particle count), front and height over 3 at t sqrt(3) (size power {GpuFluidSim.ThickSizePower})");
        var times = new[] { 0.5f, 1f, 2f, 4f };
        foreach (float v in new[] { 10f, 15f })
        {
            var r25 = DamBreakAt(1f, 0.025f, true, v, GpuFluidSim.ThickSizePower, times);
            var r75 = DamBreakAt(3f, 0.075f, true, v, GpuFluidSim.ThickSizePower, times);
            var old = DamBreakAt(3f, 0.075f, true, v, 0f, times);
            // the deviations from 2.5 cm (over k): front and height at each time; "early" is the first 2 s (scaled), where it moves
            float[] Dev(SizeRun r) => r.Front.Zip(r25.Front, (a, b) => a - b).Concat(r.Height.Zip(r25.Height, (a, b) => a - b)).ToArray();
            float Rms(float[] d) => MathF.Sqrt(d.Sum(x => x * x) / d.Length);
            float EarlyMax(float[] d) => new[] { 0, 1, 2, 4, 5, 6 }.Max(i => MathF.Abs(d[i]));
            float[] now = Dev(r75), was = Dev(old);
            Console.WriteLine($"    V{v:0} 2.5 cm:            {SizeLine(r25, true)}");
            Console.WriteLine($"    V{v:0} 7.5 cm:            {SizeLine(r75, true)}");
            Console.WriteLine($"    V{v:0} 7.5 cm as 0.29.1:  {SizeLine(old, true)}");
            Console.WriteLine($"    V{v:0}: 7.5 cm against 2.5 cm, front and height over k: up to {EarlyMax(now) * 1000:0} mm in the first 2 s, rms {Rms(now) * 1000:0} mm " +
                              $"(0.29.1's mapping: up to {EarlyMax(was) * 1000:0} mm, rms {Rms(was) * 1000:0} mm; its front ran up to {was.Take(3).Max() * 1000:0} mm further)");
            Check(r75.Finite && r75.N == r75.N0 && r75.Unconverged == 0, $"thick size: V{v:0} at 7.5 cm is stable (finite, no particle lost, every solve converged)");
            Check(EarlyMax(now) <= ThickSizeEarly, $"thick size: V{v:0} at 7.5 cm spreads and slumps as at 2.5 cm in the first 2 s (front and height within {ThickSizeEarly * 1000:0} mm over k)");
            Check(Rms(now) <= 0.5f * Rms(was), $"thick size: V{v:0} at 7.5 cm is at least twice as close to 2.5 cm's look as with 0.29.1's mapping (rms over 4 s)");
            Check(was.Take(3).Max() > ThickSizeEarly + 0.02f, $"thick size: V{v:0} at 7.5 cm with 0.29.1's mapping ran clearly further (the test sees the old problem)");
            Check(r75.ItsMedian <= r25.ItsMedian, $"thick size: V{v:0} at 7.5 cm takes no more solver iterations than at 2.5 cm ({r75.ItsMedian} against {r25.ItsMedian})");
        }
    }

    /// <summary>0.29.2: how close the 7.5 cm dam break's front and height (over k) must come to 2.5 cm's in its first 2 s (scaled):
    /// 45 mm is 1.8 particle sizes (with size power 1 the sweep measured at most 35 mm; 0.29.1's mapping 125 mm). Thick grip
    /// (2026-11-14): with the grip and power 1.5, at most 35 mm (Viscosity 15; 10 mm at 10).</summary>
    const float ThickSizeEarly = 0.045f;

    static string SizeLine(SizeRun r, bool thick) =>
        $"front {string.Join(" ", r.Front.Select(a => a.ToString("0.000")))}  height {string.Join(" ", r.Height.Select(a => a.ToString("0.000")))}  " +
        $"n {r.N}/{r.N0}  top {r.TopSpeed:0.00} m/s  {r.MsPerFrame:0.0} ms/frame" + (r.Finite ? "" : "  NOT FINITE") +
        (thick ? $"  its median {r.ItsMedian} max {r.ItsMax} mean {r.ItsMean:0.0} ({r.Solves} solves, {r.Unconverged} capped)" : "");

    static void ThickSizeSweep()
    {
        Console.WriteLine("SWEEP thick liquid at other particle sizes (0.29.2)");
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var sizes = new[] { 0.025f, 0.05f, 0.075f };
        var vs = new[] { 4f, 10f, 15f, 20f };
        var powers = new[] { 1.5f, 2f };
        var times = new[] { 0.5f, 1f, 2f, 4f };
        Console.WriteLine($"  nu at 2.5 cm: {string.Join(", ", vs.Select(v => $"V{v:0} {GpuFluidSim.ThickViscosity(v, 0.025f, 0f):0.0000}"))} m²/s");

        // ---- (b) the scene scaled with the particle size (k = s / 2.5 cm): the same particle count, as a world built for bigger
        // particles; the same look means the same front and height over k at the same t sqrt(k)
        Console.WriteLine($"  -- (b) dam break, the scene scaled with the particle size; front from the left wall and height, over k, at t sqrt(k) for t = {string.Join(", ", times)} s");
        foreach (float s in sizes)
        {
            float k = s / 0.025f;
            Console.WriteLine($"   {Cm(s)} (k {k:0.##}; {at(k)} frames)");
            Console.WriteLine($"    {"water",-22} {SizeLine(DamBreakAt(k, s, false, 0.02f, 0f, times), false)}");
            foreach (float v in vs) Console.WriteLine($"    {$"XSPH V{v:0}",-22} {SizeLine(DamBreakAt(k, s, false, v, 0f, times), false)}");
            foreach (float p in k == 1f ? new[] { 0f } : new[] { 0f }.Concat(powers).ToArray())
                foreach (float v in vs)
                    Console.WriteLine($"    {$"thick V{v:0} p{p:0.#} (nu {GpuFluidSim.ThickViscosity(v, s, p):0.0000})",-22} {SizeLine(DamBreakAt(k, s, true, v, p, times), true)}");
        }

        // ---- the spinning blob, scaled the same way (2 / sqrt(k) rad/s): the share of its spin kept
        var spinTimes = new[] { 0.5f, 1f, 3f };
        Console.WriteLine($"  -- (b) a spinning blob scaled with the particle size; the share of its spin at t sqrt(k) for t = {string.Join(", ", spinTimes)} s");
        foreach (float s in sizes)
        {
            float k = s / 0.025f;
            var cases = new List<(string, bool, float, float)> { ("water", false, 0.02f, 0f), ("XSPH V20", false, 20f, 0f), ("thick V15 p0", true, 15f, 0f) };
            if (k != 1f) foreach (float p in powers) cases.Add(($"thick V15 p{p:0.#}", true, 15f, p));
            foreach (var (name, thick, v, p) in cases)
            {
                var (share, unc, med) = SpinAt(k, s, thick, v, p, spinTimes);
                Console.WriteLine($"    {Cm(s),-7} {name,-16} spin {string.Join(" ", share.Select(a => $"{a * 100:0} %"))}" + (thick ? $"  its median {med}, {unc} capped" : ""));
            }
        }

        // ---- (a) one scene (k = 2: a 60 x 80 x 60 cm column in a 2.4 m tank) at three particle sizes: the same physical scene,
        // coarser
        Console.WriteLine($"  -- (a) one dam break (k = 2) at three particle sizes; front and height over k at t sqrt(2)");
        foreach (float s in sizes)
        {
            int cap = s < 0.03f ? 32768 : 16384;
            Console.WriteLine($"   {Cm(s)}");
            Console.WriteLine($"    {"water",-22} {SizeLine(DamBreakAt(2f, s, false, 0.02f, 0f, times, cap), false)}");
            Console.WriteLine($"    {"XSPH V10",-22} {SizeLine(DamBreakAt(2f, s, false, 10f, 0f, times, cap), false)}");
            foreach (float p in s < 0.03f ? new[] { 0f } : new[] { 0f }.Concat(powers).ToArray())
                foreach (float v in new[] { 10f, 15f })
                    Console.WriteLine($"    {$"thick V{v:0} p{p:0.#} (nu {GpuFluidSim.ThickViscosity(v, s, p):0.0000})",-22} {SizeLine(DamBreakAt(2f, s, true, v, p, times, cap), true)}");
        }

        int at(float k) => (int)MathF.Round(times[^1] * MathF.Sqrt(k) / FluidDt);
    }

    /// <summary>"thick-size-fit": the scaled dam break at 5, 7.5 and 15 cm for powers between 0.5 and 1.25, against 2.5 cm at more
    /// Viscosity values (the first sweep showed 1.5 already too thick at 5 cm).</summary>
    static void ThickSizeFit()
    {
        Console.WriteLine("SWEEP thick liquid: fitting the size power (0.29.2)");
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var times = new[] { 0.5f, 1f, 2f, 4f };
        Console.WriteLine($"  -- (b) dam break, the scene scaled with the particle size; front from the left wall and height, over k, at t sqrt(k) for t = {string.Join(", ", times)} s");
        Console.WriteLine("   2.5 cm (k 1)");
        foreach (float v in Environment.GetCommandLineArgs().Contains("small") ? new[] { 10f, 15f } : new[] { 6f, 8f, 10f, 12f, 13f, 14f, 15f })
            Console.WriteLine($"    {$"thick V{v:0} p0 (nu {GpuFluidSim.ThickViscosity(v, 0.025f, 0f):0.0000})",-22} {SizeLine(DamBreakAt(1f, 0.025f, true, v, 0f, times), true)}");
        bool small = Environment.GetCommandLineArgs().Contains("small");
        foreach (float s in small ? new[] { 0.005f, 0.0125f } : new[] { 0.05f, 0.075f, 0.15f })
        {
            float k = s / 0.025f;
            Console.WriteLine($"   {Cm(s)} (k {k:0.##})");
            var vs = s > 0.1f || small ? new[] { 10f, 15f } : new[] { 6f, 10f, 13f, 15f };
            var ps = small ? new[] { 0f, 0.75f, 1f, 1.25f } : s > 0.1f ? new[] { 0f, 0.5f, 0.75f, 1f, 1.25f, 1.5f } : new[] { 0.5f, 0.75f, 1f, 1.25f };
            foreach (float p in ps)
                foreach (float v in vs)
                    Console.WriteLine($"    {$"thick V{v:0} p{p:0.##} (nu {GpuFluidSim.ThickViscosity(v, s, p):0.0000})",-22} {SizeLine(DamBreakAt(k, s, true, v, p, times), true)}");
        }
    }
}
