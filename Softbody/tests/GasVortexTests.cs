using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.27.0: vortex particles in the gas (GasEmitter.Vortices), ported from SoftbodyRnD (its RND-REPORT.md, section 3): a hand swiped
// through a still cloud of smoke leaves a wake that keeps stirring it; a lone ring travels on its own; Vortices 0 runs nothing; each
// puff feels the rings by its own emitter's Vortices. "vortex" runs these; "fluid-0270-cpu" runs the director's part.
static partial class Program
{
    sealed class VortexScene
    {
        public readonly GpuFluidRuntime Rt;
        public readonly List<FluidColliderRecord> Colliders = new();
        public readonly List<GasEmitterRecord> Gas = new();
        public Vector3 Head = new(2f, 1f, 0f);
        public VortexScene() { Rt = new GpuFluidRuntime(GpuDevice(), new GpuFluidConfig { Capacity = 4096, ParticleSize = 0.025f }, FluidHlsl()); }
        public void Frame(FluidColliderRecord? extra = null, bool finish = false)
        {
            var cols = Colliders.ToList();
            if (extra.HasValue) cols.Add(extra.Value);
            Rt.Frame(FluidDt, new FluidEmitterRecord[0], 0, cols.ToArray(), cols.Count, false, 1f, 1.5f, Gas.ToArray(), Gas.Count, Head);
            if (finish) Rt.Sim.Finish(); else Rt.Sim.Throttle(3);
        }
        public List<(Vector3 X, Vector3 V, float Age, float Life)> Puffs()
        {
            Rt.Sim.Finish();
            Rt.Sim.ReadGas(out var p, out var v);
            var list = new List<(Vector3, Vector3, float, float)>();
            for (int i = 0; i < p.Length; i++)
                if (p[i].W >= 0f) list.Add((new Vector3(p[i].X, p[i].Y, p[i].Z), new Vector3(v[i].X, v[i].Y, v[i].Z), p[i].W, v[i].W));
            return list;
        }
        public void Dispose() => Rt.Dispose();
    }

    static void VortexTests()
    {
        Console.WriteLine("TEST vortex particles (0.27.0): lasting swirls in the gas");
        VortexDirectorTests();
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var floor = FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f));
        var times = new[] { 0.25f, 0.5f, 1f, 2f, 3f };

        // ---- a 10 cm hand swiped at 1.5 m/s through a still cloud of smoke (no rise, no curl noise; the emitter stops after 1 s):
        // the horizontal rms speed of the puffs near its path afterwards. (In a rising plume the effect lasts about half a second:
        // new smoke replaces the stirred smoke, whatever stirred it. SoftbodyRnD measured both.)
        // farVortices: a second, faint emitter far away with that Vortices (the cloud's own stays at cloudVortices)
        (float[] hrms, int[] near, int[] all, int rings, int vortices) Cloud(float cloudVortices, bool hand, float farVortices = 0f)
        {
            var g = new VortexScene();
            g.Colliders.Add(Rec(floor));
            var cloud = GasRec(8, new Vector3(0, 0.5f, 0), NozzleUp, rate: 2000f, life: 14f, buoyancy: 0f, swirl: 0f);
            cloud.Speed = 0.3f; cloud.Spread = 1f; cloud.Vortices = cloudVortices;
            g.Gas.Add(cloud);
            if (farVortices > 0f) { var far = GasRec(9, new Vector3(1.6f, 0.5f, 1.6f), NozzleUp, rate: 5f, life: 14f, buoyancy: 0f, swirl: 0f); far.Vortices = farVortices; g.Gas.Add(far); }
            for (int f = 0; f < 240; f++)
            {
                if (f == 60) { var r = g.Gas[0]; r.Emitting = 0; g.Gas[0] = r; }
                g.Frame();
            }
            for (int f = 0; f < 48; f++)
            {
                FluidColliderRecord? rec = hand ? new FluidColliderRecord { Kind = 1, Category = FluidChannel.CategoryHands, AX = -0.6f + f * 1.5f * FluidDt, AY = 0.6f, AZ = 0f, R = 0.1f, QW = 1f, VelX = 1.5f } : null;
                g.Frame(rec);
            }
            var hr = new float[times.Length]; var n = new int[times.Length]; var all = new int[times.Length];
            int done = 0, vortices = 0;
            for (int f = 1; f <= (int)MathF.Round(times[^1] / FluidDt); f++)
            {
                g.Frame();
                if (done < times.Length && f == (int)MathF.Round(times[done] / FluidDt))
                {
                    var puffs = g.Puffs().Where(p => p.X.X < 1.2f).ToList();   // (the cloud's own, not the far emitter's)
                    var near = puffs.Where(p => p.X.X > -0.4f && p.X.X < 0.8f && MathF.Abs(p.X.Y - 0.6f) < 0.25f && MathF.Abs(p.X.Z) < 0.25f).ToList();
                    n[done] = near.Count; all[done] = puffs.Count;
                    hr[done] = near.Count > 0 ? MathF.Sqrt(near.Average(p => p.V.X * p.V.X + p.V.Z * p.V.Z)) : 0f;
                    if (done == 0) vortices = g.Rt.Stats.Vortices;
                    done++;
                }
            }
            int rings = g.Rt.Sim.VortexRings;
            g.Dispose();
            return (hr, n, all, rings, vortices);
        }
        var still = Cloud(0f, false);
        var plain = Cloud(0f, true);
        var vort = Cloud(1f, true);
        var deaf = Cloud(0f, true, farVortices: 1f);
        string Row((float[] hrms, int[] near, int[] all, int rings, int vortices) r) => string.Join(", ", times.Select((t, k) => $"{t:0.##} s: {r.hrms[k]:0.000} ({r.near[k]} of {r.all[k]})"));
        Console.WriteLine("  a hand swiped through a still cloud; horizontal rms speed of the puffs near its path, m/s (puffs near of all):");
        Console.WriteLine($"    no hand:                    {Row(still)}");
        Console.WriteLine($"    hand, Vortices 0:           {Row(plain)} ({plain.rings} rings)");
        Console.WriteLine($"    hand, Vortices 1:           {Row(vort)} ({vort.rings} rings, {vort.vortices} vortex particles at 0.25 s)");
        Console.WriteLine($"    hand, Vortices 0 beside one at 1: {Row(deaf)} ({deaf.rings} rings)");
        Check(plain.rings == 0 && plain.vortices == 0, "vortex: with Vortices 0 no vortex particle is made or run");
        Check(vort.rings > 0 && vort.vortices > 0, "vortex: a hand moving through the gas sheds vortex rings");
        Check(vort.hrms[2] > 1.3f * plain.hrms[2] && vort.hrms[3] > 1.5f * plain.hrms[3],
              "vortex: 1 and 2 s after a swipe its wake still stirs a still cloud clearly more than without (1.3x and 1.5x)");
        Check(vort.near[4] < plain.near[4], "vortex: the rings carry smoke along the hand's path (fewer puffs left near it 3 s later)");
        Check(deaf.rings > 0 && deaf.hrms[2] < plain.hrms[2] + 0.4f * (vort.hrms[2] - plain.hrms[2]),
              "vortex: puffs feel the rings by their own emitter's Vortices (a cloud at 0 beside an emitter at 1 is stirred about as without)");

        // ---- a lone ring in still air travels along its axis and keeps its shape
        {
            var g = new VortexScene();
            g.Colliders.Add(Rec(floor));
            // a faint emitter far away keeps the gas pass running, with Vortices on (the vortices run with the gas)
            var keep = GasRec(9, new Vector3(5f, 0.2f, 5f), NozzleUp, rate: 5f, life: 1f, swirl: 0f);
            keep.Vortices = 1f;
            g.Gas.Add(keep);
            for (int f = 0; f < 5; f++) g.Frame();
            const float R = 0.15f, gamma = 0.3f;
            g.Rt.Sim.QueueVortexRing(new Vector3(0, 1f, 0), Vector3.UnitX, R, gamma);
            var centers = new List<Vector3>(); var radii = new List<float>();
            for (int f = 0; f < 60; f++)
            {
                g.Frame(finish: true);
                if (f % 15 == 14)
                {
                    g.Rt.Sim.ReadVortices(out var vp, out _);
                    var live = vp.Where(p => p.W >= 0f).Select(p => new Vector3(p.X, p.Y, p.Z)).ToList();
                    var c = live.Count > 0 ? live.Aggregate(Vector3.Zero, (a, b) => a + b) / live.Count : Vector3.Zero;
                    centers.Add(c);
                    radii.Add(live.Count > 0 ? live.Average(p => new Vector2(p.Y - c.Y, p.Z - c.Z).Length()) : 0f);
                }
            }
            float speed = (centers[^1].X - centers[0].X) / (45 * FluidDt);
            // a thick-core ring (core = half the radius) is slower than Kelvin's thin one, U = gamma / (4 pi R) (ln(8 R / a) - 1/4)
            float kelvin = gamma / (4 * MathF.PI * R) * (MathF.Log(8f / 0.5f) - 0.25f);
            Console.WriteLine($"  a lone ring (radius 15 cm, circulation 0.3 m²/s, 8 blobs): moves {speed:0.000} m/s along its axis (Kelvin's thin ring with that core: {kelvin:0.000}), " +
                              $"radius {string.Join(" / ", radii.Select(r => $"{r * 100:0.0}"))} cm at 0.25 s steps");
            Check(speed > 0.2f * kelvin && speed < 1.5f * kelvin && radii.All(r => MathF.Abs(r - R) < 0.3f * R),
                  "vortex: a vortex ring travels along its axis on its own and keeps its size");
            g.Dispose();
        }
    }

    /// <summary>The gas director carries each emitter's Vortices to its puffs' parameters, and the strongest kept one to the
    /// simulation; a gone emitter's strength stays a minute with its slot (its last puffs still swirl).</summary>
    static void VortexDirectorTests()
    {
        var d = new GpuGasDirector();
        var a = GasRec(1, new Vector3(0, 1, 0), NozzleUp, rate: 60f); a.Vortices = 0f;
        var b = GasRec(2, new Vector3(1, 1, 0), NozzleUp, rate: 60f); b.Vortices = 2.5f;
        d.Sync(new[] { a, b }, 2, FluidDt);
        Check(d.Vortices == 2.5f && d.Emitters[0].D.X == 0f && d.Emitters[1].D.X == 2.5f,
              $"gas director: each emitter's Vortices reaches its puffs ({d.Emitters[0].D.X}, {d.Emitters[1].D.X}), and the strongest the simulation ({d.Vortices})");
        for (int k = 0; k < 60; k++) d.Sync(new[] { a }, 1, 0.5f);   // b gone for 30 s
        float kept = d.Vortices;
        for (int k = 0; k < 70; k++) d.Sync(new[] { a }, 1, 0.5f);   // and 65 s
        Check(kept == 2.5f && d.Vortices == 0f, $"gas director: a gone emitter's Vortices stays while its last puffs can live ({kept} after 30 s, {d.Vortices} after 65 s)");
        b.Vortices = float.NaN; d.Sync(new[] { b }, 1, FluidDt);
        Check(d.Vortices == 0f, "gas director: a NaN Vortices counts as 0");
    }
}
