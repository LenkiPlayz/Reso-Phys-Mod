using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.27.0: the thick liquid (FluidEmitter.ThickLiquid): an implicit viscosity solve (Weiler et al. 2018) instead of XSPH passes,
// ported from SoftbodyRnD (its RND-REPORT.md, section 6). A spinning blob keeps its spin; a thick dam break spreads by its real
// viscosity; Viscosity 20 is held to 15; water with ThickLiquid on runs no solve. "thick" runs these; "fluid-0270-cpu" the mapping.
static partial class Program
{
    sealed class ThickScene
    {
        public readonly GpuFluidRuntime Rt;
        public readonly List<FluidEmitterRecord> Emitters = new();
        public readonly List<FluidColliderRecord> Colliders = new();
        public ThickScene(int capacity = 16384) { Rt = new GpuFluidRuntime(GpuDevice(), new GpuFluidConfig { Capacity = capacity, ParticleSize = 0.025f }, FluidHlsl()); }
        public void Add(IEnumerable<FleshCollider> cs) { foreach (var c in cs) Colliders.Add(Rec(c)); }
        public void Frame(bool finish = false)
        {
            Rt.Frame(FluidDt, Emitters.ToArray(), Emitters.Count, Colliders.ToArray(), Colliders.Count, false, 1f, 1.5f, null, 0, default);
            if (finish) Rt.Sim.Finish(); else Rt.Sim.Throttle(3);
        }
        public (Vector3[] x, Vector3[] v) Particles()
        {
            Rt.Sim.Finish();
            int n = Rt.Sim.ReadParticles(out var p4, out var v4);
            var x = new Vector3[n]; var v = new Vector3[n];
            for (int i = 0; i < n; i++) { x[i] = new Vector3(p4[i].X, p4[i].Y, p4[i].Z); v[i] = new Vector3(v4[i].X, v4[i].Y, v4[i].Z); }
            return (x, v);
        }
        public void Dispose() => Rt.Dispose();
    }

    sealed record ThickCase(string Name, bool Thick, float Viscosity);

    static readonly ThickCase[] ThickCases =
    {
        new("water (Viscosity 0.02)", false, 0.02f),
        new("Viscosity 20, ThickLiquid off (XSPH)", false, 20f),
        new("Viscosity 10, ThickLiquid on", true, 10f),
        new("Viscosity 15, ThickLiquid on", true, 15f),
        new("Viscosity 20, ThickLiquid on (held to 15)", true, 20f),
    };

    static FluidEmitterRecord ThickRec(FluidEmitterRecord r, bool thick) { r.Thick = thick ? 1 : 0; return r; }

    static FluidLiquid HoneyLike(float viscosity)
    {
        var l = FluidLiquid.Water;
        l.Viscosity = viscosity;
        l.Color = new Vector4(0.9f, 0.6f, 0.15f, 0.9f);
        return l;
    }

    static void ThickLiquidTests()
    {
        Console.WriteLine("TEST thick liquid (0.27.0): the implicit viscosity solve against XSPH");
        ThickMappingTests();
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");

        // ---- a 30 cm cube in zero gravity spun at 2 rad/s about the vertical: its angular velocity (L / I about its centre) later.
        // Viscosity resists shear, not rigid rotation, so a real one keeps the spin; XSPH smooths it away.
        {
            Console.WriteLine("  -- a spinning blob: a 30 cm cube, no gravity, 2 rad/s about the vertical");
            var times = new[] { 0.5f, 1f, 3f };
            var w = new Dictionary<string, float[]>();
            foreach (var c in new[] { ThickCases[0], ThickCases[1], ThickCases[3] })
            {
                var g = new ThickScene();
                var l = HoneyLike(c.Viscosity); l.Gravity = 0f;
                g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(0, 1f, 0), new Vector3(0.15f), l), c.Thick));
                for (int f = 0; f < 10; f++) g.Frame();
                var (x0, _) = g.Particles();
                var c0 = x0.Aggregate(Vector3.Zero, (a, b) => a + b) / x0.Length;
                var vel = new Vector4[x0.Length];
                const float w0 = 2f;
                for (int i = 0; i < x0.Length; i++) { var d = x0[i] - c0; vel[i] = new Vector4(-w0 * d.Z, 0f, w0 * d.X, 0f); }
                g.Rt.Sim.WriteVelocities(vel, x0.Length);
                var ws = new float[times.Length];
                int done = 0, unconverged = 0;
                for (int f = 1; f <= (int)MathF.Round(times[^1] / FluidDt); f++)
                {
                    g.Frame();
                    unconverged += g.Rt.Stats.IvUnconverged;
                    if (done < times.Length && f == (int)MathF.Round(times[done] / FluidDt))
                    {
                        var (x, v) = g.Particles();
                        var cc = x.Aggregate(Vector3.Zero, (a, b) => a + b) / x.Length;
                        double L = 0, I = 0;
                        for (int i = 0; i < x.Length; i++) { var d = x[i] - cc; L += -d.Z * v[i].X + d.X * v[i].Z; I += d.X * d.X + d.Z * d.Z; }
                        ws[done++] = (float)(L / Math.Max(1e-9, I));
                    }
                }
                w[c.Name] = ws;
                Console.WriteLine($"    {c.Name,-44} {x0.Length} particles: {string.Join(", ", times.Select((t, k) => $"{t:0.#} s {ws[k]:0.00} rad/s ({ws[k] / w0 * 100:0} %)"))}" +
                                  (c.Thick ? $"; {unconverged} solves stopped at the cap" : ""));
                g.Dispose();
            }
            var water = w[ThickCases[0].Name]; var xsph = w[ThickCases[1].Name]; var thick = w[ThickCases[3].Name];
            Check(thick[^1] > 0.8f * water[^1] && thick[^1] > xsph[^1] + 0.5f, "thick: a ThickLiquid blob keeps spinning about as water does (XSPH at 20 passes stops it)");
            Check(thick[^1] < 2.2f, "thick: the solve does not spin it up (at most 10 % over the start)");
        }

        // ---- a 30 x 40 x 30 cm column at one end of a 1.2 m tank: its front (the 99th percentile of x, from -0.6) at 4 s
        {
            Console.WriteLine("  -- a thick dam break: a 30 x 40 x 30 cm column in a 1.2 x 0.3 m tank; the front at 4 s");
            var front = new Dictionary<string, float>();
            foreach (var c in ThickCases)
            {
                var g = new ThickScene();
                g.Add(Tank(1.2f, 0.3f, 0.6f));
                g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(-0.45f, 0.2f, 0f), new Vector3(0.15f, 0.2f, 0.15f), HoneyLike(c.Viscosity)), c.Thick));
                for (int f = 0; f < 6; f++) g.Frame();
                g.Rt.Sim.Finish();
                int n0 = g.Rt.Stats.Count;
                float vmax = 0; int unconverged = 0, solves = 0; var its = new List<int>();
                for (int f = 1; f <= 240; f++)
                {
                    g.Frame();
                    var st = g.Rt.Stats;
                    vmax = MathF.Max(vmax, st.MaxSpeed);
                    if (st.IvSolves > 0) { its.Add(st.IvIterationsMax); solves += st.IvSolves; unconverged += st.IvUnconverged; }
                }
                var (xe, _) = g.Particles();
                bool finite = xe.All(p => float.IsFinite(p.X + p.Y + p.Z));
                var xs = xe.Select(p => p.X).OrderBy(a => a).ToList();
                front[c.Name] = xs.Count > 0 ? xs[(int)(xs.Count * 0.99f)] : 0f;
                its.Sort();
                Console.WriteLine($"    {c.Name,-44} front {front[c.Name]:+0.000;-0.000} m; {xe.Length} of {n0} particles left, finite {finite}, top speed {vmax:0.00} m/s" +
                                  (c.Thick ? $"; solver iterations median {(its.Count > 0 ? its[its.Count / 2] : 0)}, max {(its.Count > 0 ? its[^1] : 0)}, {unconverged} of {solves} solves stopped at the cap" : ""));
                if (c.Thick)
                    Check(finite && xe.Length == n0 && vmax < 10f && unconverged == 0, $"thick: {c.Name} is stable (finite, no particle lost, no runaway speed, every solve converged)");
                if (c == ThickCases[3]) Check(its.Count > 0 && its[its.Count / 2] <= 45, "thick: Viscosity 15 takes about 31 solver iterations (at most 45)");
                g.Dispose();
            }
            float f15 = front[ThickCases[3].Name], f20 = front[ThickCases[4].Name], fx = front[ThickCases[1].Name], f10 = front[ThickCases[2].Name];
            Check(f15 < fx - 0.05f && f15 < f10, "thick: a Viscosity 15 thick liquid spreads less than XSPH's 20 passes and less than Viscosity 10");
            Check(MathF.Abs(f20 - f15) < 0.03f, "thick: Viscosity 20 with ThickLiquid on is held to 15 (the same spread)");
        }

        // ---- water with ThickLiquid on: no solve, the same motion (two runs with it off give the GPU's run-to-run noise)
        {
            Console.WriteLine("  -- water with ThickLiquid off (twice) and on: a dam break, 4 s");
            var runs = new List<(string name, float[] front, int solves)>();
            var times = new[] { 0.1f, 0.2f, 0.3f, 1f, 4f };
            foreach (var (name, on) in new[] { ("off", false), ("off again", false), ("on", true) })
            {
                var g = new ThickScene();
                g.Add(Tank(1.2f, 0.3f, 0.6f));
                g.Emitters.Add(ThickRec(VolumeRec(1, new Vector3(-0.45f, 0.2f, 0f), new Vector3(0.15f, 0.2f, 0.15f), FluidLiquid.Water), on));
                for (int f = 0; f < 6; f++) g.Frame();
                var fr = new float[times.Length];
                int done = 0, solves = 0;
                for (int f = 1; f <= 240; f++)
                {
                    g.Frame(finish: true);
                    solves += g.Rt.Stats.IvSolves;
                    if (done < times.Length && f == (int)MathF.Round(times[done] / FluidDt))
                    {
                        var (x, _) = g.Particles();
                        var xs = x.Select(p => p.X).OrderBy(a => a).ToList();
                        fr[done++] = xs[(int)(xs.Count * 0.99f)];
                    }
                }
                runs.Add((name, fr, solves));
                Console.WriteLine($"    {name,-10} front {string.Join(", ", times.Select((t, k) => $"{t:0.#} s {fr[k]:+0.000;-0.000}"))} m; solves {solves}");
                g.Dispose();
            }
            float noise = runs[0].front.Zip(runs[1].front, (a, b) => MathF.Abs(a - b)).Max();
            float diff = runs[0].front.Zip(runs[2].front, (a, b) => MathF.Abs(a - b)).Max();
            Console.WriteLine($"    front: off against off again differs by up to {noise * 1000:0.0} mm, off against on by up to {diff * 1000:0.0} mm");
            Check(runs[2].solves == 0, "thick: water with ThickLiquid on runs no solve (Viscosity 1 and under stays XSPH)");
            Check(diff <= Math.Max(2f * noise, 0.01f), "thick: water moves the same with ThickLiquid on (within the run-to-run noise)");
        }
    }

    /// <summary>The director gives a liquid its kinematic viscosity only with ThickLiquid on and Viscosity over 1, held to 15.</summary>
    static void ThickMappingTests()
    {
        float Nu(float viscosity, bool thick, float cohesion = 0.3f)
        {
            var d = new GpuFluidDirector(16);
            var l = HoneyLike(viscosity); l.Cohesion = cohesion;
            var r = ThickRec(VolumeRec(1, Vector3.Zero, new Vector3(0.1f), l), thick);
            d.Sync(new[] { r }, 1, FluidDt, 1, new GpuFluidStats(), 0.025f);
            return d.Liquids[0].D.Y;
        }
        float n10 = Nu(10f, true), n15 = Nu(15f, true), n20 = Nu(20f, true), n4 = Nu(4f, true);
        Console.WriteLine($"  the mapping (ThickLiquid on): Viscosity 4 -> {n4:0.0000} m²/s, 10 -> {n10:0.000}, 15 -> {n15:0.000}, 20 -> {n20:0.000} (held to 15)");
        Check(MathF.Abs(n10 - 0.0785f) < 0.002f && MathF.Abs(n15 - 0.886f) < 0.01f && n20 == n15 && n4 > 0.003f && n4 < 0.006f,
              "thick mapping: 10 is 0.08 m²/s, 15 is 0.9, 20 is held to 15, about 4 is honey (0.004)");
        Check(Nu(20f, false) == 0f && Nu(0.9f, true) == 0f && Nu(1f, true) == 0f, "thick mapping: ThickLiquid off, or Viscosity 1 and under, leaves the liquid on XSPH");
        Check(Nu(0.5f, true, cohesion: 5f) > 0f, "thick mapping: cohesion's thickening (above 1) counts as Viscosity, as with XSPH");
    }
}
