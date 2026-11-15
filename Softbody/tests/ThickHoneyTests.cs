using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// Thick grip (fork, measuring, 2026-11-14): Lenki's friend could not get "super thick honey" out of ThickLiquid. "thick-honey"
// measures the two things that hold it back: the cap (Viscosity held to 15) and the walls (a thick particle on a collider only
// had VelocityB's friction, after the viscosity solve; the next solve spread that over the liquid as a drag, so it slid). Three
// scenes at 2.5 cm: the 0.27.0 dam break (against the no-slip theory, Huppert 1982), a pour onto a floor (does it pile?) and a
// blob on a 30 degree ramp (does it creep or slide?).
static partial class Program
{
    sealed record HoneyCase(string Name, float Viscosity, float Cap, float Grip);

    sealed record HoneyRun(float[] A, float[] B, int N0, int N, int ItsMedian, int ItsMax, float ItsMean, int Unconverged, double GpuMs, bool Finite);

    static ThickScene HoneyScene(HoneyCase c, int capacity = 16384, float size = 0.025f)
    {
        var g = new ThickScene(capacity, size);
        g.Rt.Director.ThickCap = c.Cap;
        g.Rt.Sim.Config.ThickWallGrip = c.Grip;
        return g;
    }

    /// <summary>Runs <paramref name="g"/> to the last of <paramref name="times"/> (seconds), measuring at each with
    /// <paramref name="measure"/> (into A and B); <paramref name="perFrame"/> may change the emitters first.</summary>
    static HoneyRun HoneyRunScene(ThickScene g, float[] times, Func<Vector3[], Vector3[], (float a, float b)> measure, Action<int> perFrame = null)
    {
        var at = times.Select(t => (int)MathF.Round(t / FluidDt)).ToArray();
        var a = new float[times.Length]; var b = new float[times.Length];
        var its = new List<int>(); var gpu = new List<double>();
        long itSum = 0; int solves = 0, unconverged = 0, n0 = 0, n = 0, done = 0; bool finite = true;
        for (int f = 1; f <= at[^1]; f++)
        {
            perFrame?.Invoke(f);
            g.Frame();
            var st = g.Rt.Stats;
            if (f == 6) { g.Rt.Sim.Finish(); n0 = g.Rt.Stats.Count; }
            if (st.IvSolves > 0) { its.Add(st.IvIterationsMax); solves += st.IvSolves; unconverged += st.IvUnconverged; itSum += st.IvIterationsSum; }
            if (f > 30 && st.GpuMs > 0) gpu.Add(st.GpuMs);
            while (done < at.Length && f == at[done])
            {
                var (x, v) = g.Particles();
                n = x.Length;
                finite &= x.All(p => float.IsFinite(p.X + p.Y + p.Z));
                (a[done], b[done]) = x.Length > 0 ? measure(x, v) : (0f, 0f);
                done++;
            }
        }
        its.Sort(); gpu.Sort();
        return new HoneyRun(a, b, n0, n, its.Count > 0 ? its[its.Count / 2] : 0, its.Count > 0 ? its[^1] : 0,
                            solves > 0 ? (float)itSum / solves : 0f, unconverged, gpu.Count > 0 ? gpu[gpu.Count / 2] : 0, finite);
    }

    static float Pct(IEnumerable<float> v, float q) { var s = v.OrderBy(a => a).ToList(); return s.Count > 0 ? s[Math.Min(s.Count - 1, (int)(s.Count * q))] : 0f; }

    /// <summary>The 0.27.0 dam break (a 30 x 40 x 30 cm column at one end of a 1.2 x 0.3 m tank): the front from the left wall
    /// (99th percentile of x + 0.6) and the height (99th percentile of y).</summary>
    static HoneyRun HoneyDam(HoneyCase c, float[] times)
    {
        var g = HoneyScene(c);
        g.Add(Tank(1.2f, 0.3f, 0.6f));
        g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(-0.45f, 0.2f, 0f), new Vector3(0.15f, 0.2f, 0.15f), HoneyLike(c.Viscosity)), true));
        var r = HoneyRunScene(g, times, (x, _) => (Pct(x.Select(p => p.X), 0.99f) + 0.6f, Pct(x.Select(p => p.Y), 0.99f)));
        g.Dispose();
        return r;
    }

    /// <summary>A pour straight down from 50 cm (4 L/s through an 8 cm nozzle at 1 m/s) onto a wide floor for
    /// <paramref name="pourFor"/> seconds: the heap's height (99th percentile of y of the particles slower than 0.5 m/s: not the
    /// falling stream) and its radius (90th percentile of the distance from the stream's axis).</summary>
    static HoneyRun HoneyPour(HoneyCase c, float pourFor, float[] times)
    {
        var g = HoneyScene(c);
        g.Add(new[] { FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f)) });
        var e = ThickRec(EmitterRec(1, 0, new Vector3(0, 0.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), HoneyLike(c.Viscosity)), true);
        e.Rate = 4f; e.NozzleRadius = 0.04f; e.Speed = 1f;
        g.Emitters.Add(e);
        int stopAt = (int)MathF.Round(pourFor / FluidDt);
        var r = HoneyRunScene(g, times,
            (x, v) => (Pct(x.Where((p, i) => v[i].Length() < 0.5f).Select(p => p.Y), 0.99f), Pct(x.Select(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z)), 0.9f)),
            f => { if (f == stopAt) { var q = g.Emitters[0]; q.Pouring = 0; g.Emitters[0] = q; } });
        g.Dispose();
        return r;
    }

    /// <summary>A 20 cm cube dropped onto a 30 degree ramp: how far its middle and its lowest tenth have gone down the slope (along
    /// it, from where the middle was at 0.5 s).</summary>
    static HoneyRun HoneyRamp(HoneyCase c, float[] times)
    {
        var g = HoneyScene(c);
        float a = 30f * MathF.PI / 180f;
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -a);   // top's normal (sin a, cos a, 0): downhill is +x
        g.Add(new[] { FleshCollider.Box(new Vector3(0, -0.1f, 0), rot, new Vector3(2f, 0.1f, 0.6f)) });
        var down = new Vector3(MathF.Cos(a), -MathF.Sin(a), 0);
        // the cube's middle 0.2 m above the ramp's top at x = -0.8 (the top is y = -x tan a there)
        var mid = new Vector3(-0.8f, 0.8f * MathF.Tan(a), 0) + Vector3.Transform(Vector3.UnitY, rot) * 0.2f;
        g.Emitters.Add(ThickRec(VolumeRec(1, mid, new Vector3(0.1f), HoneyLike(c.Viscosity)), true));
        float start = float.NaN;
        var r = HoneyRunScene(g, times, (x, _) =>
        {
            var along = x.Select(p => Vector3.Dot(p, down)).ToList();
            float m = along.Average();
            if (float.IsNaN(start)) start = m;
            return (m - start, Pct(along, 0.9f) - start);
        });
        g.Dispose();
        return r;
    }

    /// <summary>Huppert (1982): a 2D viscous gravity current on a no-slip floor, a fixed area q per width: its front x_N(t) =
    /// 1.411 (g q³ t / (3 nu))^(1/5).</summary>
    static float HuppertFront(float nu, float q, float t) => 1.411f * MathF.Pow(9.81f * q * q * q * t / (3f * nu), 0.2f);

    static void ThickHoneyMeasure()
    {
        GpuDevice();
        Console.WriteLine($"MEASURE thick honey (thick grip, fork): the cap and the walls; device {_gpuName}");
        var cases = new[]
        {
            new HoneyCase("V10 today", 10f, 15f, 0f),
            new HoneyCase("V15 today", 15f, 15f, 0f),
            new HoneyCase("V20, cap raised", 20f, 20f, 0f),
            new HoneyCase("V10 + grip 8", 10f, 15f, 8f),
            new HoneyCase("V15 + grip 2", 15f, 15f, 2f),
            new HoneyCase("V15 + grip 8", 15f, 15f, 8f),
            new HoneyCase("V15 + grip 32", 15f, 15f, 32f),
            new HoneyCase("V20 + grip 8", 20f, 20f, 8f),
        };
        if (Environment.GetCommandLineArgs().Contains("quick")) cases = new[] { cases[1], cases[5] };
        string Its(HoneyRun r) => $"its median {r.ItsMedian} max {r.ItsMax} mean {r.ItsMean:0.0}{(r.Unconverged > 0 ? $" ({r.Unconverged} capped)" : "")}, GPU {r.GpuMs:0.00} ms, n {r.N}/{r.N0}{(r.Finite ? "" : " NOT FINITE")}";
        string Row(float[] v) => string.Join(" ", v.Select(a => a.ToString("0.000")));

        var tDam = new[] { 0.5f, 1f, 2f, 4f, 8f };
        Console.WriteLine($"  -- dam break, front from the left wall (m) at {string.Join(", ", tDam)} s; then the height");
        foreach (var nu in new[] { 15f, 20f })
            Console.WriteLine($"    no-slip theory (Huppert) V{nu:0} (nu {GpuFluidSim.KinematicViscosity(nu):0.###}): front {string.Join(" ", tDam.Select(t => MathF.Max(0.3f, HuppertFront(GpuFluidSim.KinematicViscosity(nu), 0.12f, t)).ToString("0.000")))}");
        foreach (var c in cases)
        {
            var r = HoneyDam(c, tDam);
            Console.WriteLine($"    {c.Name,-18} front {Row(r.A)}  height {Row(r.B)}  {Its(r)}");
        }

        var tPour = new[] { 2f, 4f, 6f, 10f };
        Console.WriteLine($"  -- a 4 s pour onto a floor (16 L), pile height (m) at {string.Join(", ", tPour)} s; then its radius (90 %)");
        foreach (var c in cases)
        {
            var r = HoneyPour(c, 4f, tPour);
            Console.WriteLine($"    {c.Name,-18} height {Row(r.A)}  radius {Row(r.B)}  {Its(r)}");
        }

        var tRamp = new[] { 0.5f, 1f, 2f, 4f, 8f };
        Console.WriteLine($"  -- a 20 cm cube on a 30 degree ramp, how far its middle went down the slope (m) at {string.Join(", ", tRamp)} s; then its front tenth");
        foreach (var c in cases)
        {
            var r = HoneyRamp(c, tRamp);
            Console.WriteLine($"    {c.Name,-18} middle {Row(r.A)}  front {Row(r.B)}  {Its(r)}");
        }
    }

    /// <summary>A 20 cm cube settled for 1 s on a 1 x 0.6 m platform (top at y = 0), which then moves along +x at
    /// <paramref name="speed"/> m/s: at each of <paramref name="times"/> (from the start), where the cube's middle is against the
    /// platform's (x; it starts on the platform's middle) and its mean speed along x, of the particles still on the platform.</summary>
    static HoneyRun HoneyPlatform(HoneyCase c, float speed, float[] times)
    {
        var g = HoneyScene(c);
        const float startMove = 1f;
        float t = 0f;
        float PlatformX(float at) => -0.6f + speed * MathF.Max(0f, at - startMove);
        FluidColliderRecord Platform(float at)
        {
            var box = FleshCollider.Box(new Vector3(PlatformX(at), -0.05f, 0), Quaternion.Identity, new Vector3(0.5f, 0.05f, 0.3f));
            box.Velocity = at > startMove ? new Vector3(speed, 0, 0) : Vector3.Zero;
            return Rec(box);
        }
        g.Colliders.Add(Platform(0f));
        g.Add(new[] { FleshCollider.Box(new Vector3(0, -1.1f, 0), Quaternion.Identity, new Vector3(4f, 0.1f, 4f)) });   // a floor far below
        g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(-0.6f, 0.11f, 0), new Vector3(0.1f), HoneyLike(c.Viscosity)), true));
        var r = HoneyRunScene(g, times, (x, v) =>
        {
            var on = Enumerable.Range(0, x.Length).Where(i => x[i].Y > -0.05f).ToList();
            if (on.Count < x.Length / 2) return (float.NaN, float.NaN);   // fallen off
            return (on.Average(i => x[i].X) - PlatformX(t), on.Average(i => v[i].X));
        }, f => { t = f * FluidDt; g.Colliders[0] = Platform(t); });
        g.Dispose();
        return r;
    }

    /// <summary>Thick grip (fork, 2026-11-14): the grip a thick liquid gets on colliders by default.</summary>
    static float ThickGripDefault => new GpuFluidConfig().ThickWallGrip;

    /// <summary>
    /// Thick grip (fork, 2026-11-14), in "thick": a thick liquid grips the shapes it touches, inside its viscosity solve. Until then it
    /// slid along floors and down slopes like a block on ice (only VelocityB's friction, after the solve, which the next solve spread
    /// over the liquid as a mild drag), so even Viscosity 15 looked thin, and 20 slid further still. Now it creeps down a ramp, its
    /// dam break follows the no-slip theory, and it rides along on a moving shape.
    /// </summary>
    static void ThickGripTests()
    {
        Console.WriteLine($"  -- the grip (thick grip, fork): a thick liquid holds on to the shapes it touches (grip {ThickGripDefault})");
        var off15 = new HoneyCase("V15, grip off (0.29.2)", 15f, 15f, 0f);
        var on15 = new HoneyCase("V15", 15f, GpuFluidSim.ThickViscosityCap, ThickGripDefault);
        var on20 = new HoneyCase("V20", 20f, GpuFluidSim.ThickViscosityCap, ThickGripDefault);
        string Its(HoneyRun r) => $"its median {r.ItsMedian} max {r.ItsMax}{(r.Unconverged > 0 ? $" ({r.Unconverged} not converged)" : "")}, n {r.N}/{r.N0}{(r.Finite ? "" : " NOT FINITE")}";
        string Row(float[] v) => string.Join(" ", v.Select(a => a.ToString("0.000")));

        // a 20 cm cube on a 30 degree ramp: how far its middle went down the slope
        var tr = new[] { 0.5f, 1f, 2f, 4f, 8f };
        var rOff = HoneyRamp(off15, tr); var rOn = HoneyRamp(on15, tr); var r20 = HoneyRamp(on20, tr);
        Console.WriteLine($"    a 20 cm cube on a 30 degree ramp, its middle down the slope (m) at {string.Join(", ", tr)} s:");
        foreach (var (c, r) in new[] { (off15, rOff), (on15, rOn), (on20, r20) }) Console.WriteLine($"      {c.Name,-24} {Row(r.A)}  {Its(r)}");
        Check(rOn.Finite && rOn.N == rOn.N0 && r20.Finite && r20.N == r20.N0, "thick grip: on a ramp it is stable (finite, no particle lost)");
        Check(rOn.A[3] <= 0.25f, "thick grip: at Viscosity 15 a 20 cm cube creeps down a 30 degree ramp (at most 25 cm in 4 s)");
        Check(rOff.A[3] >= 4f * rOn.A[3], "thick grip: without the grip it slid at least 4 times as far (the test sees the old problem)");
        Check(r20.A[3] <= rOn.A[3] + 0.005f, "thick grip: Viscosity 20 creeps no faster than 15");
        Check(rOn.A[4] > rOn.A[3] + 0.03f, "thick grip: it still flows: it creeps on between 4 and 8 s (the grip does not glue it)");

        // the 0.27.0 dam break against the no-slip theory
        var td = new[] { 0.5f, 1f, 2f, 4f };
        var dOff = HoneyDam(off15, td); var dOn = HoneyDam(on15, td); var d20 = HoneyDam(on20, td);
        float theory = HuppertFront(GpuFluidSim.KinematicViscosity(15f), 0.12f, 4f);
        Console.WriteLine($"    the dam break, its front from the left wall (m) at {string.Join(", ", td)} s (no-slip theory at 4 s: {theory:0.000} at 15):");
        foreach (var (c, r) in new[] { (off15, dOff), (on15, dOn), (on20, d20) }) Console.WriteLine($"      {c.Name,-24} {Row(r.A)}  height {Row(r.B)}  {Its(r)}");
        Check(dOn.A[3] >= 0.7f * theory && dOn.A[3] <= 1.05f * theory, "thick grip: the Viscosity 15 dam break's front at 4 s is within -30 % and +5 % of the no-slip theory (Huppert 1982)");
        Check(dOff.A[3] > 1.15f * theory, "thick grip: without the grip it ran over 15 % past the theory (the test sees the old problem)");
        Check(d20.A[3] < dOn.A[3] - 0.03f, "thick grip: Viscosity 20 spreads less than 15 (no longer held to 15)");
        Check(d20.Finite && d20.N == d20.N0 && d20.Unconverged == 0 && d20.ItsMedian <= 80, "thick grip: Viscosity 20 is stable, every solve converges, at most 80 iterations a solve (median)");

        // a blob on a moving platform rides along (the grip is against the shape's own motion)
        var tp = new[] { 1f, 1.5f, 2.5f, 3.5f };
        const float speed = 0.4f;
        var pOff = HoneyPlatform(off15, speed, tp); var pOn = HoneyPlatform(on15, speed, tp);
        Console.WriteLine($"    a 20 cm cube on a platform moving at {speed} m/s from 1 s, its middle against the platform's (m) at {string.Join(", ", tp)} s; then its speed (m/s):");
        foreach (var (c, r) in new[] { (off15, pOff), (on15, pOn) }) Console.WriteLine($"      {c.Name,-24} {Row(r.A)}  speed {Row(r.B)}  {Its(r)}");
        Check(float.IsFinite(pOn.A[3]) && MathF.Abs(pOn.A[3] - pOn.A[0]) <= 0.05f && pOn.B[3] >= 0.8f * speed,
              "thick grip: a thick blob on a moving platform rides along (within 5 cm of its place, at 80 % of its speed or more)");
        Check(!float.IsFinite(pOff.A[3]) || pOff.A[3] - pOff.A[0] < -0.15f, "thick grip: without the grip the platform slid away under it (the test sees the old problem)");
    }

    /// <summary>Thick grip: "thick-size-grip" fits the size power again with the grip on (no slip is scale-free, so similarity's
    /// 1.5 may fit now; 0.29.2 fitted 1 without it): the scaled dam break's front and height (over k) against 2.5 cm's, the rms
    /// over 4 s and the most in the first 2 s, at Viscosity 6 to 20. "small": 1.25 cm.</summary>
    static void ThickSizeGripFit()
    {
        GpuDevice();
        Console.WriteLine($"FIT the thick size power with the grip ({ThickGripDefault}); device {_gpuName}");
        var times = new[] { 0.5f, 1f, 2f, 4f };
        var vs = new[] { 6f, 10f, 15f, 20f };
        var refs = vs.ToDictionary(v => v, v => DamBreakAt(1f, 0.025f, true, v, 1f, times));
        foreach (var v in vs) Console.WriteLine($"  2.5 cm V{v:0}: {SizeLine(refs[v], true)}");
        bool small = Environment.GetCommandLineArgs().Contains("small");
        foreach (var s in small ? new[] { 0.0125f } : new[] { 0.05f, 0.075f, 0.15f })
        {
            float k = s / 0.025f;
            foreach (var p in new[] { 1f, 1.25f, 1.5f })
            {
                double sum = 0; int n = 0; float early = 0; var per = new List<string>();
                foreach (var v in vs)
                {
                    var r = DamBreakAt(k, s, true, v, p, times);
                    var d = r.Front.Zip(refs[v].Front, (a, b) => a - b).Concat(r.Height.Zip(refs[v].Height, (a, b) => a - b)).ToArray();
                    float rms = MathF.Sqrt(d.Sum(x => x * x) / d.Length);
                    float em = new[] { 0, 1, 2, 4, 5, 6 }.Max(i => MathF.Abs(d[i]));
                    sum += d.Sum(x => x * x); n += d.Length; early = MathF.Max(early, em);
                    per.Add($"V{v:0} rms {rms * 1000:0} early {em * 1000:0} front4 {r.Front[3] - refs[v].Front[3]:+0.000;-0.000} its {r.ItsMedian}{(r.Unconverged > 0 ? $" ({r.Unconverged} unconv)" : "")}");
                }
                Console.WriteLine($"  {Cm(s)} p{p:0.##}: rms {Math.Sqrt(sum / n) * 1000:0} mm, early max {early * 1000:0} mm | {string.Join("; ", per)}");
            }
        }
    }

    /// <summary>Thick grip: "thick-cost": the GPU time a frame of a big dam break (a 1.2 x 0.7 x 0.8 m block, about 43,000
    /// particles at 2.5 cm) as water and as a thick liquid at Viscosity 10, 15 and 20, with the grip; the first 0.5 s left out.</summary>
    static void ThickCostMeasure()
    {
        GpuDevice();
        Console.WriteLine($"MEASURE thick cost (grip {ThickGripDefault}); device {_gpuName}");
        foreach (var (name, thick, v) in new[] { ("water", false, 0.02f), ("thick V10", true, 10f), ("thick V15", true, 15f), ("thick V20", true, 20f) })
        {
            var g = new ThickScene(65536);
            g.Add(Tank(2.4f, 0.8f, 1f));
            var e = ThickRec(VolumeRec(1, new Vector3(-0.55f, 0.36f, 0f), new Vector3(0.6f, 0.35f, 0.4f), HoneyLike(v)), thick);
            e.MaxParticles = 65536;
            g.Emitters.Add(e);
            var gpu = new List<double>(); var its = new List<int>(); int n = 0, unconverged = 0;
            for (int f = 1; f <= 240; f++)
            {
                g.Frame();
                var st = g.Rt.Stats;
                if (f > 30 && st.GpuMs > 0) gpu.Add(st.GpuMs);
                if (st.IvSolves > 0) { its.Add(st.IvIterationsMax); unconverged += st.IvUnconverged; }
                n = Math.Max(n, st.Count);
            }
            gpu.Sort(); its.Sort();
            Console.WriteLine($"  {name,-10} {n} particles: GPU median {gpu[gpu.Count / 2]:0.00} ms, 95th {gpu[(int)(gpu.Count * 0.95)]:0.00} ms" +
                              (thick ? $"; iterations median {its[its.Count / 2]}, max {its[^1]}, {unconverged} not converged" : ""));
            g.Dispose();
        }
    }
}
