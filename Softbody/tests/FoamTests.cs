using System.Numerics;
using System.Runtime.InteropServices;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.27.0: spray, foam and bubbles on the GPU liquid (Ihmsen et al. 2012), ported from SoftbodyRnD (its RND-REPORT.md, section 1):
// a liquid's Foam and FoamLifetime ride on its emitter record; the discs go into their own mesh buffers. Also the channel's 0.27.0
// fields (version 8). "foam" runs these; "fluid-0270-cpu" only the parts that need no GPU.
static partial class Program
{
    sealed class FoamScene
    {
        public readonly GpuFluidRuntime Rt;
        public readonly List<FluidEmitterRecord> Emitters = new();
        public readonly List<FluidColliderRecord> Colliders = new();
        public Vector3 Head = new(0f, 1.2f, 3f);
        /// <summary>0.27.0: drawn as quads for the renderer's smoke shader (else 7-vertex discs).</summary>
        public bool Quads;
        public FoamScene(int capacity = 65536) { Rt = new GpuFluidRuntime(GpuDevice(), new GpuFluidConfig { Capacity = capacity, ParticleSize = 0.025f }, FluidHlsl()); }
        public void Add(IEnumerable<FleshCollider> cs) { foreach (var c in cs) Colliders.Add(Rec(c)); }
        public void Frame(bool finish = false)
        {
            Rt.Frame(FluidDt, Emitters.ToArray(), Emitters.Count, Colliders.ToArray(), Colliders.Count, false, 1f, 1.5f, null, 0, Head, default, false, Quads);
            if (finish) Rt.Sim.Finish(); else Rt.Sim.Throttle(3);
        }
        /// <summary>The live diffuse particles (waiting for the GPU): position, velocity, life left, class (0 spray, 1 foam, 2 bubble), liquid.</summary>
        public List<(Vector3 X, Vector3 V, float Life, uint Type, uint Liquid)> Diffuse()
        {
            Rt.Sim.Finish();
            Rt.Sim.ReadDiffuse(out var p, out var v, out var info);
            var list = new List<(Vector3, Vector3, float, uint, uint)>();
            for (int i = 0; i < p.Length; i++)
                if (p[i].W > 0f) list.Add((new Vector3(p[i].X, p[i].Y, p[i].Z), new Vector3(v[i].X, v[i].Y, v[i].Z), p[i].W, info[i], GpuFluidSim.AsUint(v[i].W)));
            return list;
        }
        public Vector3[] Fluid()
        {
            int n = Rt.Sim.ReadParticles(out var p4, out _);
            var x = new Vector3[n];
            for (int i = 0; i < n; i++) x[i] = new Vector3(p4[i].X, p4[i].Y, p4[i].Z);
            return x;
        }
        public void Dispose() => Rt.Dispose();
    }

    static FluidEmitterRecord Foamy(FluidEmitterRecord r, float foam = 1f, float lifetime = 3f) { r.Foam = foam; r.FoamLifetime = lifetime; return r; }

    /// <summary>Where diffuse particles are against the liquid's top in their column (2.5 cm columns): above (more than 2 cm over
    /// it, or over no liquid), at it, or under it (more than 2 cm), for spray, foam and bubbles.</summary>
    static int[,] FoamPlaces(Vector3[] fluid, List<(Vector3 X, Vector3 V, float Life, uint Type, uint Liquid)> diffuse)
    {
        var top = new Dictionary<(int, int), float>();
        const float c = 0.025f;
        foreach (var p in fluid)
        {
            var k = ((int)MathF.Floor(p.X / c), (int)MathF.Floor(p.Z / c));
            top[k] = top.TryGetValue(k, out var t) ? MathF.Max(t, p.Y) : p.Y;
        }
        var table = new int[3, 3];
        foreach (var d in diffuse)
        {
            var k = ((int)MathF.Floor(d.X.X / c), (int)MathF.Floor(d.X.Z / c));
            int place = !top.TryGetValue(k, out var t) || d.X.Y > t + 0.02f ? 0 : (d.X.Y < t - 0.02f ? 2 : 1);
            table[Math.Min(d.Type, 2u), place]++;
        }
        return table;
    }

    static void FoamTests()
    {
        Console.WriteLine("TEST foam (0.27.0): spray, foam and bubbles on the GPU liquid");
        Channel0270Tests();
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var water = FluidLiquid.Water;

        // ---- a dam break in a 1.6 m tank (Foam 1): the wave makes spray, foam and bubbles where it hits the far wall and falls back
        {
            var g = new FoamScene();
            g.Add(Tank(1.6f, 0.5f, 1.0f));
            g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(-0.55f, 0.3f, 0f), new Vector3(0.25f, 0.3f, 0.25f), water)));
            int peak = 0, peakSpray = 0, peakFoam = 0, peakBubbles = 0, seen = -1;
            int[,] places = null; int nAt = 0;
            // the mesh at 1.5 s, while the splash is alive (at 10 s there is nothing left to draw)
            int drawnAt = -1, badFacing = 0, badIndex = 0; bool finite = true, sizesOk = false;
            var timeline = new List<string>();
            for (int f = 0; f < 600; f++)
            {
                g.Frame();
                var st = g.Rt.Stats;
                if (st.Frame != seen)
                {
                    seen = st.Frame;
                    if (st.DiffuseLive > peak) { peak = st.DiffuseLive; peakFoam = st.DiffuseFoam; peakBubbles = st.DiffuseBubbles; }
                    peakSpray = Math.Max(peakSpray, st.DiffuseSpray);
                    if (f % 30 == 0 && f <= 240) timeline.Add($"{f * FluidDt:0.0}s {st.DiffuseSpray}/{st.DiffuseFoam}/{st.DiffuseBubbles}");
                }
                if (f == 90)
                {
                    var fl = g.Fluid(); var d = g.Diffuse(); places = FoamPlaces(fl, d); nAt = d.Count;
                    // a disc of 7 vertices and 18 indices each (disc k's triangles use only its own 7 vertices), facing the head
                    // (0.27.0: 14 floats a vertex, as the gas's)
                    g.Rt.Sim.ReadDiffuseMesh(out var verts, out var idx);
                    drawnAt = g.Rt.Stats.DiffuseDrawn;
                    sizesOk = verts.Length == drawnAt * 7 * GpuFluidSim.GasVertexFloats && idx.Length == drawnAt * 18;
                    finite = verts.All(float.IsFinite);
                    for (int k = 0; sizesOk && k < drawnAt; k++)
                    {
                        int c = k * 7 * GpuFluidSim.GasVertexFloats;
                        var centre = new Vector3(verts[c], verts[c + 1], verts[c + 2]);
                        var n = new Vector3(verts[c + 3], verts[c + 4], verts[c + 5]);
                        if (Vector3.Dot(n, Vector3.Normalize(g.Head - centre)) < 0.999f) badFacing++;
                    }
                    for (int j = 0; sizesOk && j < idx.Length; j++)
                        if (idx[j] / 7 != j / 18) badIndex++;
                }
            }
            var dd = g.Diffuse();
            int outside = dd.Count(d => MathF.Abs(d.X.X) > 0.81f || MathF.Abs(d.X.Z) > 0.26f || d.X.Y < -0.005f);
            int liveEnd = g.Rt.Stats.DiffuseLive, askedEnd = g.Rt.Stats.DiffuseAsked;
            Console.WriteLine($"  dam break, Foam 1: spray / foam / bubbles alive {string.Join(", ", timeline)}; at most {peak} alive ({peakFoam} foam, {peakBubbles} bubbles then; spray at most {peakSpray}); " +
                              $"at 10 s {liveEnd} alive, {askedEnd} asked for in that frame; {outside} outside the tank or under its floor");
            if (places != null)
            {
                string Row(int t) => $"{places[t, 0]} above / {places[t, 1]} at / {places[t, 2]} under";
                Console.WriteLine($"    at 1.5 s ({nAt} alive), against the liquid's top in their column: spray {Row(0)}; foam {Row(1)}; bubbles {Row(2)}");
                int sprayN = places[0, 0] + places[0, 1] + places[0, 2], bubN = places[2, 0] + places[2, 1] + places[2, 2];
                Check(sprayN == 0 || places[0, 0] >= 0.5f * sprayN, "foam: any spray is mostly above the liquid (or over none)");
                Check(bubN == 0 || places[2, 2] >= 0.5f * bubN, "foam: bubbles are mostly under the liquid's top");
            }
            Check(peak > 200 && peakSpray > 0 && peakFoam > 0 && peakBubbles > 0, "foam: a dam break makes spray, foam and bubbles");
            Check(outside == 0, "foam: no diffuse particle leaves the tank or goes under its floor");
            Check(liveEnd < peak / 4 && askedEnd < 50, "foam: once the water calms down it makes (almost) no more, and the foam dies away");

            Console.WriteLine($"    the mesh at 1.5 s: {drawnAt} discs drawn of {nAt} alive, sizes right {sizesOk}, finite {finite}, {badFacing} not facing the head, " +
                              $"{badIndex} indices outside their own disc; at 10 s {g.Rt.Stats.DiffuseDrawn} drawn");
            Check(nAt > 100 && sizesOk && finite && badFacing == 0 && badIndex == 0 && Math.Abs(drawnAt - nAt) <= Math.Max(3, nAt / 50),
                  "foam: each live diffuse particle is drawn as a 7-vertex disc facing the head");
            Check(g.Rt.Stats.DiffuseDrawn <= Math.Max(3, liveEnd + liveEnd / 50), "foam: once the foam is gone nothing is left drawn");
            g.Dispose();
        }

        // ---- a calm pool makes nothing, and once asleep the foam's kernels are skipped
        {
            var g = new FoamScene();
            g.Add(Tank(0.8f, 0.5f, 0.5f));
            g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(0, 0.1f, 0), new Vector3(0.4f, 0.1f, 0.25f), water)));
            int seen = -1; double asked = 0;
            for (int f = 0; f < 900; f++)
            {
                g.Frame();
                var st = g.Rt.Stats;
                if (st.Frame != seen) { seen = st.Frame; asked += st.DiffuseAsked; }
            }
            var st2 = g.Rt.Stats;
            Console.WriteLine($"  a calm pool with Foam 1 ({st2.Count} particles) for 15 s: {asked:0} diffuse asked for, {st2.DiffuseLive} alive at the end; {st2.Awake} awake; foam kernels ran in the last frame: {g.Rt.Sim.DiffuseRanLastFrame}");
            Check(asked < 20 && st2.DiffuseLive == 0, "foam: a calm pool makes no foam");
            Check(!g.Rt.Sim.DiffuseRanLastFrame, "foam: with nothing alive and the water asleep, the foam's kernels are skipped");
            g.Dispose();
        }

        // ---- 0.27.0, the smoke shader: the same dam break drawn as quads (4 vertices that all hold the particle's centre, the four
        // corners, its radius, unshaded light and sky, its colour and opacity, a random number of its own; 6 indices into its own quad)
        {
            var g = new FoamScene { Quads = true };
            g.Add(Tank(1.6f, 0.5f, 1.0f));
            g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(-0.55f, 0.3f, 0f), new Vector3(0.25f, 0.3f, 0.25f), water)));
            for (int f = 0; f < 90; f++) g.Frame();
            int alive = g.Diffuse().Count;
            g.Rt.Sim.ReadDiffuseMesh(out var verts, out var idx);
            int drawn = g.Rt.Stats.DiffuseDrawn, vf = GpuFluidSim.GasVertexFloats;
            bool sizes = g.Rt.Sim.FoamQuads && verts.Length == drawn * 4 * vf && idx.Length == drawn * 6, finite = verts.All(float.IsFinite);
            int badQuad = 0, badIndex = 0;
            var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            for (int k = 0; sizes && k < drawn; k++)
            {
                int c = k * 4 * vf;
                for (int q = 0; q < 4; q++)
                {
                    int v = c + q * vf;
                    bool same = verts[v] == verts[c] && verts[v + 1] == verts[c + 1] && verts[v + 2] == verts[c + 2];
                    bool shade = verts[v + 3] > 0f && verts[v + 3] < 0.05f && verts[v + 4] == 1f && verts[v + 5] == 1f && verts[v + 9] > 0f && verts[v + 9] <= 1f;
                    bool corner = verts[v + 10] == corners[q].X && verts[v + 11] == corners[q].Y && verts[v + 12] >= 0f && verts[v + 12] < 1f;
                    if (!same || !shade || !corner) { badQuad++; break; }
                }
            }
            for (int j = 0; sizes && j < idx.Length; j++) if (idx[j] / 4 != j / 6) badIndex++;
            Console.WriteLine($"  the dam break as quads for the smoke shader at 1.5 s: {drawn} drawn of {alive} alive, sizes right {sizes}, finite {finite}, {badQuad} bad quads, {badIndex} indices outside their own quad");
            Check(alive > 100 && sizes && finite && badQuad == 0 && badIndex == 0 && Math.Abs(drawn - alive) <= Math.Max(3, alive / 50),
                  "foam: for the smoke shader each live diffuse particle is a quad of 4 vertices holding its centre, radius, colour and corner");
            g.Dispose();
        }

        // ---- a stream poured from 0.8 m into a shallow pool foams where it lands
        {
            var g = new FoamScene();
            g.Add(Tank(0.8f, 0.8f, 0.5f));
            g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(0, 0.04f, 0), new Vector3(0.4f, 0.04f, 0.4f), water)));
            for (int f = 0; f < 60; f++) g.Frame();   // the pool settles for 1 s
            int pool = g.Rt.Stats.Count;
            var e = Foamy(EmitterRec(2, 0, new Vector3(0, 0.8f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), water));
            e.Rate = 4f; e.Speed = 2f; e.NozzleRadius = 0.035f;
            g.Emitters.Add(e);
            for (int f = 0; f < 120; f++) g.Frame();
            var d = g.Diffuse(); var fl = g.Fluid();
            var pl = FoamPlaces(fl, d);
            int near = d.Count(p => new Vector2(p.X.X, p.X.Z).Length() < 0.2f);
            Console.WriteLine($"  pouring 4 L/s from 0.8 m into a {pool}-particle pool 8 cm deep for 2 s: {d.Count} diffuse ({d.Count(p => p.Type == 0)} spray, " +
                              $"{d.Count(p => p.Type == 1)} foam, {d.Count(p => p.Type == 2)} bubbles), {near} within 20 cm of where it lands; " +
                              $"above / at / under the top: spray {pl[0, 0]}/{pl[0, 1]}/{pl[0, 2]}, foam {pl[1, 0]}/{pl[1, 1]}/{pl[1, 2]}, bubbles {pl[2, 0]}/{pl[2, 1]}/{pl[2, 2]}");
            Check(d.Count > 50 && near > 0.6f * d.Count, "foam: a stream poured from a height foams where it lands");
            // (spray is rare at 2.5 cm: none in the dam break's look at 1.5 s, a few here)
            int pourSpray = pl[0, 0] + pl[0, 1] + pl[0, 2];
            Check(pourSpray == 0 || pl[0, 0] >= 0.5f * pourSpray, "foam: any spray from the pour is mostly above the liquid (or over none)");
            g.Dispose();
        }

        // ---- per liquid: two dam breaks side by side, one with Foam 1 and one with Foam 0: only the first makes any
        {
            var g = new FoamScene();
            foreach (var c in Tank(1.6f, 0.5f, 1.0f)) g.Colliders.Add(Rec(Shifted(c, new Vector3(0, 0, -0.6f))));
            foreach (var c in Tank(1.6f, 0.5f, 1.0f)) g.Colliders.Add(Rec(Shifted(c, new Vector3(0, 0, 0.6f))));
            g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(-0.55f, 0.3f, -0.6f), new Vector3(0.25f, 0.3f, 0.25f), water)));
            g.Emitters.Add(Foamy(VolumeRec(2, new Vector3(-0.55f, 0.3f, 0.6f), new Vector3(0.25f, 0.3f, 0.25f), water), foam: 0f));
            for (int f = 0; f < 90; f++) g.Frame();
            var d = g.Diffuse();
            int fromFoamy = d.Count(p => p.Liquid == 0 && p.X.Z < 0f), fromPlain = d.Count(p => p.Liquid != 0 || p.X.Z > 0f);
            Console.WriteLine($"  two dam breaks side by side at 1.5 s, Foam 1 and Foam 0: {fromFoamy} diffuse from the first, {fromPlain} from the second");
            Check(fromFoamy > 100 && fromPlain == 0, "foam: each liquid's own Foam: one with Foam 0 makes none beside one that foams");
            g.Dispose();
        }

        // ---- FoamLifetime: the same dam break with 1 s and 6 s; the life left is looked at every half second (by 4 s a 1 s foam
        // is gone, so a look only then would see nothing)
        {
            int Alive(float lifetime)
            {
                var g = new FoamScene();
                g.Add(Tank(1.6f, 0.5f, 1.0f));
                g.Emitters.Add(Foamy(VolumeRec(1, new Vector3(-0.55f, 0.3f, 0f), new Vector3(0.25f, 0.3f, 0.25f), water), lifetime: lifetime));
                int looked = 0; float maxLife = 0f;
                for (int f = 1; f <= 240; f++)
                {
                    g.Frame();
                    if (f % 30 != 0) continue;
                    var d = g.Diffuse();
                    looked += d.Count;
                    foreach (var p in d) maxLife = MathF.Max(maxLife, p.Life);
                }
                g.Rt.Sim.Finish();
                int n = g.Rt.Stats.DiffuseLive;
                Console.WriteLine($"  dam break with FoamLifetime {lifetime:0} s: {n} alive at 4 s; looking every 0.5 s ({looked} seen), the longest left {maxLife:0.00} s");
                Check(looked > 100 && maxLife > 0.4f * lifetime && maxLife <= lifetime * 4f / 3f + 0.01f,
                      $"foam: a particle lives FoamLifetime {lifetime:0} s x 0.5 to 4/3 (none has more left; some newborn have most of it)");
                g.Dispose();
                return n;
            }
            int shortLived = Alive(1f), longLived = Alive(6f);
            Check(longLived > 2 * shortLived, "foam: a longer FoamLifetime keeps more foam alive");
        }

        // ---- Foam 0 everywhere: nothing allocated past a token, nothing run
        {
            var g = new FoamScene();
            g.Add(Tank(1.6f, 0.5f, 1.0f));
            g.Emitters.Add(VolumeRec(1, new Vector3(-0.55f, 0.3f, 0f), new Vector3(0.25f, 0.3f, 0.25f), water));
            for (int f = 0; f < 90; f++) g.Frame();
            g.Rt.Sim.Finish();
            var st = g.Rt.Stats;
            Console.WriteLine($"  Foam 0: the same dam break, {st.DiffuseLive} diffuse, {g.Rt.Sim.DiffuseSlots} slots, foam kernels ran {g.Rt.Sim.DiffuseRanLastFrame}");
            Check(st.DiffuseLive == 0 && !g.Rt.Sim.DiffuseRanLastFrame && g.Rt.Sim.DiffuseSlots == 256, "foam: with Foam 0 nothing is made or run, and its buffers stay a token");
            g.Dispose();
        }
    }

    /// <summary>A collider moved by <paramref name="d"/> (a box's or sphere's B and C are not points).</summary>
    static FleshCollider Shifted(FleshCollider c, Vector3 d)
    {
        c.A += d;
        if (c.Kind is FleshColliderKind.Capsule or FleshColliderKind.Cylinder or FleshColliderKind.Cone or FleshColliderKind.Triangle) c.B += d;
        if (c.Kind == FleshColliderKind.Triangle) c.C += d;
        return c;
    }

    /// <summary>0.27.0: the channel (version 8) carries the new fields whole: an emitter's Foam, FoamLifetime and Thick, a gas emitter's
    /// Vortices, and the renderer's foam count, thick-liquid iterations and vortex count; the records keep their sizes.</summary>
    static void Channel0270Tests()
    {
        int es = Marshal.SizeOf<FluidEmitterRecord>(), gs = Marshal.SizeOf<GasEmitterRecord>(), bs = Marshal.SizeOf<FluidBack>(), hs = Marshal.SizeOf<FluidHeader>();
        Check(FluidChannel.Version == 8 && es <= FluidChannel.EmitterSize && gs == FluidChannel.GasEmitterSize && bs <= 256 && hs <= FluidChannel.HeaderSize,
              $"channel version 8: an emitter record {es} of {FluidChannel.EmitterSize} bytes, a gas record {gs}, the renderer's block {bs} of 256, the header {hs}");
        string name = @"Local\Softbody.Fluid.Test0270." + Environment.ProcessId;
        using var plugin = FluidChannelIO.Open(name);
        using var renderer = FluidChannelIO.Open(name);
        var em = new[] { Foamy(VolumeRec(5, new Vector3(1, 2, 3), new Vector3(0.1f), FluidLiquid.Water), foam: 2.5f, lifetime: 7f) };
        em[0].Thick = 1;
        var gas = new[] { GasRec(6, new Vector3(0, 1, 0), NozzleUp) };
        gas[0].Vortices = 1.5f;
        plugin.WriteFrame(new FluidHeader { Frame = 3, Active = 1, Owner = 7, Dt = FluidDt, Time = 0.05f, MaxParticles = 65536, MeshAssetId = 11, GasMeshAssetId = 12 },
                          em, 1, new FluidColliderRecord[0], 0, gas, 1);
        FluidEmitterRecord[] rem = null; FluidColliderRecord[] rcol = null; GasEmitterRecord[] rgas = null;
        bool got = renderer.TryReadFrame(out var h, ref rem, ref rcol, ref rgas);
        Check(got && h.Version == 8 && rem[0].Id == 5 && rem[0].Foam == 2.5f && rem[0].FoamLifetime == 7f && rem[0].Thick == 1 && rem[0].RandomVelocity == em[0].RandomVelocity &&
              rgas[0].Vortices == 1.5f && rgas[0].Spread == 0.15f,
              "the channel carries an emitter's Foam, FoamLifetime and Thick and a gas emitter's Vortices, whole");
        renderer.WriteBack(new FluidBack { Ready = 1, Owner = 7, FoamParticles = 1234, FoamDrawing = 1, ThickIterations = 31, ThickUnconverged = 0, Vortices = 24, GasPuffs = 99 }, new int[0], 0);
        bool back = plugin.TryReadBack(out var b, null);
        Check(back && b.FoamParticles == 1234 && b.FoamDrawing == 1 && b.ThickIterations == 31 && b.Vortices == 24 && b.GasPuffs == 99,
              "the renderer's block carries the foam count, the thick liquid's iterations and the vortex count back");
        // a frame of the old version is not one: a 0.26.x plugin and a 0.27.0 renderer (or the other way round) ignore each other
        var old = new FluidHeader { Frame = 4, Active = 1, Owner = 7 };
        plugin.WriteFrame(old, em, 1, new FluidColliderRecord[0], 0);
        using (var raw = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name))
        using (var view = raw.CreateViewAccessor(0, FluidChannel.HeaderSize))
            view.Write(FluidChannel.HeaderOffset + 4, 7);   // the version field, as a 0.26.x plugin writes it
        Check(!renderer.TryReadFrame(out _, ref rem, ref rcol, ref rgas), "a version 7 frame (0.26.x) is not read by a 0.27.0 renderer");
    }
}
