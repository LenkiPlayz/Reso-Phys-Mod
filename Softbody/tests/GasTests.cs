using System.Numerics;
using System.Runtime.InteropServices;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.26.0: the gas (DESIGN-gas.md): puffs of smoke on this machine's GPU through the renderer's runtime, fed the records the
// plugin writes (GasEmitter.Record), and the director that turns those records into puffs (engine-free).
static partial class Program
{
    /// <summary>A gas emitter's record as the plugin writes it, its nozzle (+Z) turned by <paramref name="rot"/>.</summary>
    static GasEmitterRecord GasRec(int id, Vector3 pos, Quaternion rot, float rate = 300f, float life = 2f, float buoyancy = 0.6f, float swirl = 0.3f,
                                   uint mask = FluidChannel.CategoryWorld | FluidChannel.CategoryHands, int maxPuffs = 20000)
        => new GasEmitterRecord
        {
            Id = id, Active = 1, Emitting = 1, MaxPuffs = maxPuffs, Rate = rate, Speed = 0.6f, NozzleRadius = 0.03f, Lifetime = life,
            Buoyancy = buoyancy, Drag = 1.5f, Swirl = swirl, StartSize = 0.1f, EndSize = 0.5f,
            PosX = pos.X, PosY = pos.Y, PosZ = pos.Z, RotX = rot.X, RotY = rot.Y, RotZ = rot.Z, RotW = rot.W,
            ColR = 0.8f, ColG = 0.8f, ColB = 0.85f, ColA = 0.3f, CollideMask = mask, Spread = 0.15f,
        };

    /// <summary>A nozzle pointing up (+Z turned to +Y).</summary>
    static readonly Quaternion NozzleUp = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);

    sealed class GasScene
    {
        public readonly GpuFluidRuntime Rt;
        public readonly List<FluidEmitterRecord> Emitters = new();
        public readonly List<FluidColliderRecord> Colliders = new();
        public readonly List<GasEmitterRecord> Gas = new();
        public Vector3 Head = new(2f, 1f, 0f);
        /// <summary>0.27.0: drawn as quads for the renderer's smoke shader, lit from TowardsLight (zero: no main light).</summary>
        public bool Quads;
        public Vector3 TowardsLight;
        public GasScene() { Rt = new GpuFluidRuntime(GpuDevice(), new GpuFluidConfig { Capacity = 4096, ParticleSize = 0.025f }, FluidHlsl()); }
        public void Add(IEnumerable<FleshCollider> cs) { foreach (var c in cs) Colliders.Add(Rec(c)); }
        public void Frame(FluidColliderRecord? extra = null)
        {
            var cols = Colliders.ToList();
            if (extra.HasValue) cols.Add(extra.Value);
            Rt.Frame(FluidDt, Emitters.ToArray(), Emitters.Count, cols.ToArray(), cols.Count, false, 1f, 1.5f, Gas.ToArray(), Gas.Count, Head, TowardsLight, Quads, false);
            Rt.Sim.Throttle(3);
        }
        public void Run(float seconds, Func<int, FluidColliderRecord?> extra = null, Action<int> every = null)
        {
            int frames = (int)MathF.Round(seconds / FluidDt);
            for (int f = 0; f < frames; f++) { Frame(extra?.Invoke(f)); every?.Invoke(f); }
        }
        /// <summary>The live puffs (waiting for the GPU).</summary>
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

    static void GasTests()
    {
        Console.WriteLine("TEST gas (0.26.0): puffs of smoke on the GPU, from the plugin's records");
        GasDirectorTests();
        GasChannelTests();
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var floor = FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f));
        var slab = FleshCollider.Box(new Vector3(0, 1.05f, 0), Quaternion.Identity, new Vector3(0.3f, 0.05f, 0.3f));
        static bool InSlab(Vector3 p) => MathF.Abs(p.X) < 0.3f && MathF.Abs(p.Z) < 0.3f && p.Y > 1.0f && p.Y < 1.1f;

        // ---- a column of smoke under a slab: it rises, lives its lifetime, never goes into the slab, and is drawn
        {
            var g = new GasScene();
            g.Add(new[] { floor, slab });
            g.Gas.Add(GasRec(7, new Vector3(0, 0.2f, 0), NozzleUp));   // 300 a second for 2 s (0.8 to 1.2 of it): about 600 alive
            int insideMax = 0;
            var gpuMs = new List<double>();
            g.Run(4f, every: f =>
            {
                if (f % 30 == 29) insideMax = Math.Max(insideMax, g.Puffs().Count(p => InSlab(p.X)));
                if (f >= 150) gpuMs.Add(g.Rt.Stats.GpuMs);
            });
            var puffs = g.Puffs();
            var st = g.Rt.Stats;
            int live = puffs.Count;
            float meanY = live > 0 ? puffs.Average(p => p.X.Y) : 0f, top = live > 0 ? puffs.Max(p => p.X.Y) : 0f;
            gpuMs.Sort();
            double ms = gpuMs.Count > 0 ? gpuMs[gpuMs.Count / 2] : -1;
            Console.WriteLine($"  a column at 300 puffs a second for 2 s: {live} alive ({st.GasPuffs} by the GPU's count), mean height {meanY:0.00} m, highest {top:0.00} m, " +
                              $"at most {insideMax} inside the slab, bounds y {st.GasMin.Y:0.00}..{st.GasMax.Y:0.00}, GPU {ms:0.000} ms a frame (median, with no liquid)");
            Check(live > 480 && live < 720 && Math.Abs(st.GasPuffs - live) <= Math.Max(3, live / 50),
                  "gas: an emitter keeps Rate x Lifetime puffs alive (each lives its own lifetime), and the GPU counts them");
            Check(meanY > 0.45f && top > 0.8f, "gas: the smoke rises from its nozzle (speed and buoyancy)");
            Check(insideMax == 0, "gas: no puff goes into a solid collider (a slab over the column)");
            Check(st.HasGasBounds && st.GasMin.Y > -0.01f && st.GasMax.Y >= top - 0.01f && st.GasMax.Y <= top + 0.01f, "gas: the GPU reports the gas's bounds (for the colliders the plugin gathers)");
            Check(ms >= 0 && ms < 2.0, "gas: 600 puffs and their discs cost well under 2 ms of GPU a frame");

            // drawn: a disc of 7 vertices and 18 indices a puff, its centre in the gas's colour and opacity, its rim clear, facing the head
            // (0.27.0: 14 floats a vertex: position, normal, colour, texcoord0)
            g.Rt.Sim.ReadGasMesh(out var verts, out var idx);
            int drawn = g.Rt.Stats.GasDrawn, vf = GpuFluidSim.GasVertexFloats;
            bool finite = verts.All(float.IsFinite);
            uint maxIdx = idx.Length > 0 ? idx.Max() : 0;
            int badFan = 0, badAlpha = 0, badFacing = 0;
            for (int t = 0; t + 2 < idx.Length; t += 3) if (idx[t] % 7 != 0 || idx[t + 1] / 7 != idx[t] / 7 || idx[t + 2] / 7 != idx[t] / 7) badFan++;
            for (int d = 0; d < drawn; d++)
            {
                int c = d * 7 * vf;
                var centre = new Vector3(verts[c], verts[c + 1], verts[c + 2]);
                var normal = new Vector3(verts[c + 3], verts[c + 4], verts[c + 5]);
                if (!(verts[c + 9] > 0f && verts[c + 9] <= 0.3001f)) badAlpha++;
                for (int k = 1; k < 7; k++) if (verts[c + k * vf + 9] != 0f) badAlpha++;
                var toHead = Vector3.Normalize(g.Head - centre);
                if (Vector3.Dot(normal, toHead) < 0.999f) badFacing++;
            }
            Console.WriteLine($"  drawn: {drawn} puffs of {live}: {verts.Length / vf} vertices, {idx.Length} indices (top {maxIdx}), finite {finite}, {badFan} bad fan triangles, {badAlpha} bad alphas, {badFacing} not facing the head");
            Check(drawn >= live * 95 / 100 && drawn <= live + 3 && verts.Length == drawn * 7 * vf && idx.Length == drawn * 18 && maxIdx < drawn * 7 && finite && badFan == 0,
                  "gas: each live puff is drawn as a disc of 7 vertices and 6 triangles (one fan, its own vertices)");
            Check(badAlpha == 0 && badFacing == 0, "gas: a disc's centre has the gas's colour and opacity, its rim is clear, and it faces the local user's head");

            // stopped: the puffs live out their lives (still kept out of the slab), then none are left or drawn
            var r = g.Gas[0]; r.Emitting = 0; g.Gas[0] = r;
            int insideAfter = 0;
            g.Run(2.6f, every: f => { if (f % 15 == 14) insideAfter = Math.Max(insideAfter, g.Puffs().Count(p => InSlab(p.X))); });
            int left = g.Puffs().Count;
            g.Rt.Sim.ReadGasMesh(out var v2, out var i2);
            Console.WriteLine($"  stopped: {left} puffs left after 2.6 s ({g.Rt.Stats.GasPuffs} by the GPU, {g.Rt.Stats.GasDrawn} drawn, {i2.Length} indices), at most {insideAfter} inside the slab meanwhile");
            Check(left == 0 && g.Rt.Stats.GasPuffs == 0 && g.Rt.Stats.GasDrawn == 0 && i2.Length == 0 && !g.Rt.Stats.HasGasBounds,
                  "gas: once an emitter stops, its puffs fade out over their lifetime and none are left or drawn");
            Check(insideAfter == 0, "gas: a stopped emitter's last puffs still collide (its collide mask is kept while they live)");
            g.Dispose();
        }

        // ---- a hand swept through the column carries the smoke with it (moving shapes stir the air); without it the smoke stays put
        {
            float Sweep(bool hand)
            {
                var g = new GasScene();
                g.Add(new[] { floor });
                g.Gas.Add(GasRec(7, new Vector3(0, 0.2f, 0), NozzleUp, life: 5f));
                g.Run(4f);
                // a 10 cm hand at 0.6 m, from x = -0.6 at 1.5 m/s
                g.Run(0.75f, f => hand ? new FluidColliderRecord { Kind = 1, Category = FluidChannel.CategoryHands, AX = -0.6f + f * 1.5f * FluidDt, AY = 0.6f, AZ = 0f, R = 0.1f, QW = 1f, VelX = 1.5f } : null);
                var near = g.Puffs().Where(p => MathF.Abs(p.X.Y - 0.6f) < 0.15f && MathF.Abs(p.X.Z) < 0.15f).ToList();
                float vx = near.Count > 0 ? near.Average(p => p.V.X) : 0f;
                Console.WriteLine($"  {(hand ? "with" : "without")} a hand swept through it: {near.Count} puffs at 0.6 m, mean x-speed {vx:0.00} m/s");
                g.Dispose();
                return vx;
            }
            float withHand = Sweep(true), withoutHand = Sweep(false);
            Check(withHand > 0.15f && withHand - withoutHand > 0.15f, "gas: a hand swept through smoke carries it along");
        }

        // ---- a heavy gas (negative buoyancy) sinks and spreads on the floor, never through it
        {
            var g = new GasScene();
            g.Add(new[] { floor });
            g.Gas.Add(GasRec(8, new Vector3(0, 0.5f, 0), Quaternion.Identity, rate: 200f, life: 3f, buoyancy: -1.5f));
            int belowMax = 0;
            g.Run(2.5f, every: f => { if (f % 15 == 14) belowMax = Math.Max(belowMax, g.Puffs().Count(p => p.X.Y < -0.005f)); });
            var puffs = g.Puffs();
            float meanY = puffs.Count > 0 ? puffs.Average(p => p.X.Y) : 1f;
            float spread = puffs.Count > 0 ? puffs.Max(p => new Vector2(p.X.X, p.X.Z).Length()) : 0f;
            Console.WriteLine($"  a heavy gas from 0.5 m: {puffs.Count} puffs, mean height {meanY:0.00} m, spread to {spread:0.00} m, at most {belowMax} below the floor");
            Check(puffs.Count > 0 && meanY < 0.3f && belowMax == 0, "gas: a heavy gas (negative Buoyancy) sinks and lies on the floor, never through it");
            g.Dispose();
        }

        // ---- a moving emitter's new puffs take on its motion
        {
            var g = new GasScene();
            var r = GasRec(9, new Vector3(0, 1f, 0), NozzleUp, swirl: 0f);
            r.VelX = 1f;
            g.Gas.Add(r);
            g.Run(1f);
            var young = g.Puffs().Where(p => p.Age < 0.2f).ToList();
            float vx = young.Count > 0 ? young.Average(p => p.V.X) : 0f;
            Console.WriteLine($"  an emitter moving at 1 m/s: {young.Count} puffs younger than 0.2 s, mean x-speed {vx:0.00} m/s");
            Check(young.Count > 20 && vx > 0.6f && vx < 1.05f, "gas: a moving emitter's puffs leave with its motion (and slow with the air's drag)");
            g.Dispose();
        }

        // ---- gas and liquid in one world: the gas's pass leaves the liquid's collisions as they were
        {
            var g = new GasScene();
            g.Add(Tank(0.4f, 0.4f, 0.4f));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.15f, 0), new Vector3(0.1f), FluidLiquid.Water));
            g.Gas.Add(GasRec(7, new Vector3(0.1f, 0.05f, 0.1f), NozzleUp));
            g.Run(2f);
            g.Rt.Sim.Finish();
            int n = g.Rt.Sim.ReadParticles(out var p4, out _);
            float low = n > 0 ? Enumerable.Range(0, n).Min(i => p4[i].Y) : -1f;
            int below = Enumerable.Range(0, n).Count(i => p4[i].Y < -0.001f);
            int puffs = g.Puffs().Count;
            Console.WriteLine($"  water with smoke: {n} particles, lowest at {low * 100:0.00} cm, {below} below the floor; {puffs} puffs");
            Check(n == 512 && below == 0 && low < 0.02f && puffs > 300, "gas: liquid and gas run together; the liquid still rests on the floor at its own collision radius");
            g.Dispose();
        }

        SmokeShaderTests();   // 0.27.0
    }

    /// <summary>0.27.0: the smoke shader's quads (FluidSim.hlsl GasDrawQuads): their shape, their order (far to near from the head),
    /// and the light and sky reaching each puff through the smoke.</summary>
    static void SmokeShaderTests()
    {
        Console.WriteLine("TEST smoke shader (0.27.0): the gas as quads, far to near from the head, shaded through itself");
        var floor = FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f));
        int vf = GpuFluidSim.GasVertexFloats;
        // a column of smoke (300 puffs a second for 3 s); the mesh after `seconds`
        (float[] V, uint[] I, int Drawn, int Live, double Ms, Vector3 Head) Column(Vector3 towardsLight, float seconds = 3f)
        {
            var g = new GasScene { Quads = true, TowardsLight = towardsLight };
            g.Add(new[] { floor });
            g.Gas.Add(GasRec(7, new Vector3(0, 0.2f, 0), NozzleUp, rate: 300f, life: 3f));
            var ms = new List<double>();
            g.Run(seconds, every: f => { if (f >= 120) ms.Add(g.Rt.Stats.GpuMs); });
            int live = g.Puffs().Count;
            g.Rt.Sim.ReadGasMesh(out var v, out var idx);
            int drawn = g.Rt.Stats.GasDrawn;
            bool quads = g.Rt.Sim.GasQuads;
            ms.Sort();
            var head = g.Head;
            g.Dispose();
            if (!quads) return (new float[0], new uint[0], 0, live, -1, head);
            return (v, idx, drawn, live, ms.Count > 0 ? ms[ms.Count / 2] : -1, head);
        }
        // each quad's centre, radius, light, sky (from its first vertex)
        List<(Vector3 C, float R, float Lit, float Sky)> Quads((float[] V, uint[] I, int Drawn, int Live, double Ms, Vector3 Head) m)
        {
            var list = new List<(Vector3, float, float, float)>();
            for (int k = 0; k < m.Drawn; k++) { int c = k * 4 * vf; list.Add((new Vector3(m.V[c], m.V[c + 1], m.V[c + 2]), m.V[c + 3], m.V[c + 4], m.V[c + 5])); }
            return list;
        }

        // ---- lit from above: the shape of each quad, the order, and the shading
        var up = Column(Vector3.UnitY);
        {
            bool sizes = up.V.Length == up.Drawn * 4 * vf && up.I.Length == up.Drawn * 6, finite = up.V.All(float.IsFinite);
            var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            int badQuad = 0, badIndex = 0;
            for (int k = 0; sizes && k < up.Drawn; k++)
            {
                int c = k * 4 * vf;
                for (int q = 0; q < 4; q++)
                {
                    int v = c + q * vf;
                    bool same = up.V[v] == up.V[c] && up.V[v + 1] == up.V[c + 1] && up.V[v + 2] == up.V[c + 2] && up.V[v + 3] == up.V[c + 3] && up.V[v + 4] == up.V[c + 4];
                    bool ranges = up.V[v + 3] > 0.01f && up.V[v + 3] < 0.26f && up.V[v + 4] > 0f && up.V[v + 4] <= 1f && up.V[v + 5] > 0f && up.V[v + 5] <= 1f &&
                                  up.V[v + 9] > 0f && up.V[v + 9] <= 0.3001f && up.V[v + 12] >= 0f && up.V[v + 12] < 1f && up.V[v + 13] >= 0f && up.V[v + 13] <= 1f;
                    bool corner = up.V[v + 10] == corners[q].X && up.V[v + 11] == corners[q].Y;
                    if (!same || !ranges || !corner) { badQuad++; break; }
                }
            }
            for (int j = 0; sizes && j < up.I.Length; j++) if (up.I[j] / 4 != j / 6) badIndex++;
            Console.WriteLine($"  lit from above: {up.Drawn} quads of {up.Live} live puffs, sizes right {sizes}, finite {finite}, {badQuad} bad quads, {badIndex} indices outside their quad, GPU {up.Ms:0.000} ms a frame (median)");
            Check(up.Drawn >= up.Live * 95 / 100 && up.Drawn <= up.Live + 3 && up.Live > 700 && sizes && finite && badQuad == 0 && badIndex == 0,
                  "smoke shader: each live puff is a quad of 4 vertices holding its centre, radius, light, sky, colour, corner, random number and age");
            Check(up.Ms >= 0 && up.Ms < 2.0, "smoke shader: 900 puffs, their density grid, order and shading cost well under 2 ms of GPU a frame");

            var qs = Quads(up);
            int outOfOrder = 0;
            for (int k = 0; k + 1 < qs.Count; k++)
            {
                float d0 = Vector3.Distance(qs[k].C, up.Head), d1 = Vector3.Distance(qs[k + 1].C, up.Head);
                if (d1 > d0 + 0.01f * (1f + d0)) outOfOrder++;
            }
            Console.WriteLine($"  order: {outOfOrder} of {Math.Max(0, qs.Count - 1)} neighbours nearer than the next one by more than a bucket");
            Check(qs.Count > 0 && outOfOrder == 0, "smoke shader: the quads are written far to near from the head (they blend in order)");

            var byHeight = qs.OrderBy(q => q.C.Y).ToList();
            int third = byHeight.Count / 3;
            float litLow = byHeight.Take(third).Average(q => q.Lit), litHigh = byHeight.Skip(2 * third).Average(q => q.Lit);
            float skyLow = byHeight.Take(third).Average(q => q.Sky), skyHigh = byHeight.Skip(2 * third).Average(q => q.Sky);
            Console.WriteLine($"  shading, light from above: the lowest third gets {litLow:0.00} of the light and {skyLow:0.00} of the sky, the highest third {litHigh:0.00} and {skyHigh:0.00}");
            Check(litLow < litHigh - 0.05f && skyLow < skyHigh - 0.05f, "smoke shader: smoke under more smoke gets less of a light from above, and less of the sky");
        }

        // ---- lit from below: the other way round; no light: all of it
        {
            var below = Quads(Column(-Vector3.UnitY));
            var byHeight = below.OrderBy(q => q.C.Y).ToList();
            int third = byHeight.Count / 3;
            float litLow = third > 0 ? byHeight.Take(third).Average(q => q.Lit) : 0f, litHigh = third > 0 ? byHeight.Skip(2 * third).Average(q => q.Lit) : 0f;
            var none = Quads(Column(Vector3.Zero));
            bool allLit = none.Count > 0 && none.All(q => q.Lit == 1f);
            Console.WriteLine($"  light from below: the lowest third gets {litLow:0.00}, the highest {litHigh:0.00}; with no main light all of {none.Count} get all of it: {allLit}");
            Check(third > 0 && litHigh < litLow - 0.05f, "smoke shader: the shading follows the light's direction (lit from below, the top of the column is the darker)");
            Check(allLit, "smoke shader: with no main light no puff is shaded for one");
        }

        // ---- discs, then quads: the quads replace the discs (no old disc indices are left past them)
        {
            var g = new GasScene();
            g.Add(new[] { floor });
            g.Gas.Add(GasRec(7, new Vector3(0, 0.2f, 0), NozzleUp, rate: 300f, life: 2f));
            g.Run(1.5f);
            g.Puffs();
            int discs = g.Rt.Stats.GasDrawn;
            g.Quads = true;
            g.Run(0.5f);
            g.Puffs();
            int quads = g.Rt.Stats.GasDrawn;
            var tail = g.Rt.Sim.ReadGasIndices(Math.Max(discs * 18, quads * 6));
            int stale = 0;
            for (int j = quads * 6; j < tail.Length; j++) if (tail[j] != 0) stale++;
            Console.WriteLine($"  discs then quads: {discs} discs drawn, then {quads} quads, {stale} non-zero indices past the quads (up to the discs' {discs * 18})");
            Check(discs > 300 && quads > 300 && g.Rt.Sim.GasQuads && stale == 0, "smoke shader: switching to quads leaves nothing of the discs drawn");
            g.Dispose();
        }
    }

    /// <summary>A frame of smoke as quads for SoftbodyShaders' preview (build.ps1 -Preview): a column lit by a low sun from one side,
    /// seen from beside it ("side") or against the sun ("back"), with the camera, light and sky the preview renders it with.</summary>
    static int GasPreview(string path, string view)
    {
        var floor = FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(3f, 0.1f, 3f));
        var sun = Vector3.Normalize(new Vector3(-0.7f, 0.55f, 0.35f));
        var look = new Vector3(0f, 1.1f, 0f);
        var head = view == "back" ? look + Vector3.Normalize(new Vector3(sun.X, 0f, sun.Z)) * -3.2f + new Vector3(0, -0.2f, 0) : new Vector3(2.6f, 1.3f, 1.2f);
        var g = new GasScene { Quads = true, TowardsLight = sun, Head = head };
        g.Add(new[] { floor });
        var r = GasRec(7, new Vector3(0, 0.05f, 0), NozzleUp, rate: 160f, life: 6f, buoyancy: 0.45f, swirl: 0.35f, maxPuffs: 4000);
        r.StartSize = 0.18f; r.EndSize = 0.9f; r.ColR = 0.82f; r.ColG = 0.83f; r.ColB = 0.86f; r.ColA = 0.35f; r.Spread = 0.25f;
        g.Gas.Add(r);
        g.Run(6f);
        g.Puffs();
        g.Rt.Sim.ReadGasMesh(out var v, out var idx);
        int n = g.Rt.Stats.GasDrawn;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(0x534D4B31); w.Write(n);
            void V(Vector3 x) { w.Write(x.X); w.Write(x.Y); w.Write(x.Z); }
            V(head); V(look); V(sun);
            V(new Vector3(1f, 0.95f, 0.88f)); w.Write(1.1f);                                                    // the sun's colour, intensity
            V(new Vector3(0.42f, 0.52f, 0.72f)); V(new Vector3(0.33f, 0.36f, 0.42f)); V(new Vector3(0.16f, 0.15f, 0.13f));   // sky, horizon, ground
            w.Write(0f); w.Write(1);                                                                            // the floor's height; a depth texture
            for (int k = 0; k < n * 4 * GpuFluidSim.GasVertexFloats; k++) w.Write(v[k]);
            for (int k = 0; k < n * 6; k++) w.Write((int)idx[k]);
        }
        Console.WriteLine($"gas-preview: {n} puffs ({view}) written to {path}");
        g.Dispose();
        return n > 0 ? 0 : 1;
    }

    /// <summary>The director: puffs at an emitter's rate (held to MaxPuffs / Lifetime), its slot kept a minute after it goes.</summary>
    static void GasDirectorTests()
    {
        var d = new GpuGasDirector();
        var recs = new[] { GasRec(1, new Vector3(0, 1, 0), NozzleUp, rate: 5000f, life: 2f, maxPuffs: 100) };
        int spawned = 0;
        for (int f = 0; f < 60; f++) { d.Sync(recs, 1, FluidDt); spawned += d.SpawnCount; }
        Check(spawned >= 48 && spawned <= 52, $"gas director: a rate over MaxPuffs / Lifetime is held to it ({spawned} puffs in 1 s for 100 over 2 s)");
        recs[0].Rate = 120f; recs[0].MaxPuffs = 20000;
        spawned = 0;
        int nanSpawns = 0, slot = -1;
        for (int f = 0; f < 60; f++)
        {
            d.Sync(recs, 1, FluidDt); spawned += d.SpawnCount;
            for (int k = 0; k < d.SpawnCount; k++)
            {
                var p = d.Spawns[2 * k]; var v = d.Spawns[2 * k + 1];
                if (!(float.IsFinite(p.X + p.Y + p.Z + v.X + v.Y + v.Z + v.W))) nanSpawns++;
                slot = (int)GpuFluidSim.AsUint(p.W);
            }
        }
        Check(spawned >= 118 && spawned <= 122 && nanSpawns == 0 && slot == 0, $"gas director: an emitter adds its rate's puffs a second ({spawned} at 120), all finite, in its own slot");
        // stopped and inactive emitters add nothing but keep what their puffs hit
        recs[0].Emitting = 0;
        d.Sync(recs, 1, FluidDt);
        bool stoppedNone = d.SpawnCount == 0, maskKept = d.CollideMask == (FluidChannel.CategoryWorld | FluidChannel.CategoryHands);
        // a bad position adds nothing (and throws nothing)
        recs[0].Emitting = 1; recs[0].PosX = float.NaN;
        d.Sync(recs, 1, FluidDt);
        bool nanNone = d.SpawnCount == 0;
        Check(stoppedNone && maskKept && nanNone, "gas director: a stopped emitter adds no puffs but its puffs keep colliding; a bad position adds none");
        // a gone emitter's slot is kept for a minute (its last puffs keep its look), then reused
        var other = new[] { GasRec(2, new Vector3(1, 1, 0), NozzleUp, rate: 600f) };
        d.Sync(other, 1, FluidDt);
        int otherSlot = d.SpawnCount > 0 ? (int)GpuFluidSim.AsUint(d.Spawns[0].W) : -1;
        for (int f = 0; f < 61 * 60; f++) d.Sync(other, 1, FluidDt);
        var third = new[] { other[0], GasRec(3, new Vector3(2, 1, 0), NozzleUp, rate: 600f) };
        d.Sync(third, 2, FluidDt);
        int thirdSlot = -1;
        for (int k = 0; k < d.SpawnCount; k++) { var p = d.Spawns[2 * k]; if (p.X > 1.5f) thirdSlot = (int)GpuFluidSim.AsUint(p.W); }
        Check(otherSlot == 1 && thirdSlot == 0 && d.CollideMask == (FluidChannel.CategoryWorld | FluidChannel.CategoryHands),
              $"gas director: a new emitter gets a free slot ({otherSlot}) while a gone one's is kept, and the gone one's slot is reused after a minute ({thirdSlot})");
    }

    /// <summary>The channel carries the gas emitters, the gas renderer's mesh and the head, whole.</summary>
    static void GasChannelTests()
    {
        int gs = Marshal.SizeOf<GasEmitterRecord>(), hs = Marshal.SizeOf<FluidHeader>(), bs = Marshal.SizeOf<FluidBack>();
        Check(gs == FluidChannel.GasEmitterSize && hs <= FluidChannel.HeaderSize && bs <= 256 &&
              FluidChannel.GasEmittersOffset >= FluidChannel.CollidersOffset + FluidChannel.MaxColliders * FluidChannel.ColliderSize &&
              FluidChannel.TotalSize == FluidChannel.GasEmittersOffset + FluidChannel.MaxGasEmitters * FluidChannel.GasEmitterSize,
              $"the channel's gas block holds {FluidChannel.MaxGasEmitters} records of {gs} bytes after the colliders (header {hs} bytes, back {bs})");
        string name = @"Local\Softbody.Fluid.GasTest." + Environment.ProcessId;
        using var plugin = FluidChannelIO.Open(name);
        using var renderer = FluidChannelIO.Open(name);
        var gas = new[] { GasRec(41, new Vector3(1, 2, 3), NozzleUp, rate: 30f), GasRec(42, new Vector3(-1, 0.5f, 0), Quaternion.Identity, buoyancy: -1f) };
        gas[1].ColR = 0.1f; gas[1].ColA = 0.05f; gas[1].CollideMask = FluidChannel.CategoryWorld;
        plugin.WriteFrame(new FluidHeader { Frame = 5, Active = 1, Owner = 99, Dt = FluidDt, Time = 0.1f, MaxParticles = 65536, MeshAssetId = -1, GasMeshAssetId = 1234, HeadX = 0.5f, HeadY = 1.7f, HeadZ = -2f },
                          new FluidEmitterRecord[0], 0, new FluidColliderRecord[0], 0, gas, 2);
        FluidEmitterRecord[] rem = null; FluidColliderRecord[] rcol = null; GasEmitterRecord[] rgas = null;
        bool got = renderer.TryReadFrame(out var h, ref rem, ref rcol, ref rgas);
        bool same = got && h.GasEmitterCount == 2 && rgas[0].Id == 41 && rgas[0].Rate == 30f && rgas[0].PosZ == 3f && rgas[1].Id == 42 && rgas[1].Buoyancy == -1f &&
                    rgas[1].ColR == 0.1f && rgas[1].ColA == 0.05f && rgas[1].CollideMask == FluidChannel.CategoryWorld && rgas[1].Spread == 0.15f;
        Check(same && h.GasMeshAssetId == 1234 && h.HeadX == 0.5f && h.HeadY == 1.7f && h.HeadZ == -2f && h.EmitterCount == 0 && h.Active == 1,
              "the channel carries a world's gas emitters, its gas renderer's mesh and the local user's head, whole (a world with only gas)");
    }
}
