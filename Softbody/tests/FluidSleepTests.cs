using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.21.0, Lenki: make larger bodies of fluid cheaper, from the ideas in Box3D (Erin Catto's engine, as in Garry's Mod's
// VPhysics-Box3D). Offline a deep pool never came to rest: under 2 substeps of 3 iterations its pressure waves kept it
// moving at 10 to 45 cm/s (1.6 m/s along the floor), squeezed 10 % and threw particles over a tank's walls. Now 4 substeps of
// 2 iterations and pressure-wave damping calm it; then its particles fall asleep (within a quarter spacing of one spot for a
// second), cost almost nothing and hold up the awake ones like a wall; an awake particle or a shape moving faster than
// 0.5 m/s, a still shape that is gone, or a changed liquid wakes them. With nothing awake the step, the grid and the surface
// are skipped.
static partial class Program
{
    static int Outside(Vector3[] x, int n, float halfW)
    {
        int k = 0;
        for (int i = 0; i < n; i++) if (MathF.Abs(x[i].X) > halfW + 0.02f || MathF.Abs(x[i].Z) > halfW + 0.02f || x[i].Y < -0.02f) k++;
        return k;
    }

    static float Percentile(IEnumerable<float> values, float p)
    {
        var a = values.OrderBy(v => v).ToArray();
        return a.Length == 0 ? 0f : a[Math.Min(a.Length - 1, (int)(a.Length * p))];
    }

    static void FluidSleepTests()
    {
        Console.WriteLine("TEST fluid calm pool and sleeping (0.21.0): a deep pool settles, sleeps, costs little, and wakes when touched, CPU and GPU");
        var water = FluidLiquid.Water;
        float s = 0.025f, w = 0.4f, depth = 0.8f, wall = 1.4f;
        var tank = Tank(w, w, wall);
        var center = new Vector3(0, depth / 2 + 0.01f, 0); var half = new Vector3(w / 2 - 0.01f, depth / 2, w / 2 - 0.01f);

        // ---- CPU
        {
            FluidSim NewPool(out int n0)
            {
                var sim = new FluidSim(new FluidSettings { ParticleSize = s, MaxParticles = 20000 });
                sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
                sim.Colliders.AddRange(tank);
                int src = sim.AddSource(water);
                n0 = sim.FillBox(src, center, Quaternion.Identity, half, Vector3.Zero);
                return sim;
            }
            var pool = NewPool(out int n0);
            // (the first frames, everything awake: a pool filled at rest sleeps from the bottom up within about a second)
            double firstMs = 0;
            Run(pool, 0.25f, f => { if (f >= 1 && f <= 10) firstMs += pool.StepMs / 10; });
            Run(pool, 7.75f);
            float median = Percentile(Enumerable.Range(0, pool.Count).Select(i => pool.V[i].Length()), 0.5f);
            var (dens, _) = Densities(pool);
            int awake = pool.Awake, outside = Outside(pool.X, pool.Count, w / 2);
            double lastMs = 0; for (int f = 0; f < 30; f++) { pool.Step(FluidDt); lastMs += pool.StepMs / 30; }
            Console.WriteLine($"  CPU, {n0} particles {depth} m deep: after 8 s {100.0 * awake / pool.Count:0}% awake, median speed {median * 100:0.0} cm/s, density {dens:0.000}, {outside} out of the tank; step {firstMs:0.0} ms in the first frames, {lastMs:0.0} ms now; surface skipped: {!pool.Moved}");
            Check(outside == 0 && pool.Count == n0, "CPU: a deep pool keeps every particle in its tank (0.20.0 threw some over the walls)");
            Check(dens < 1.04f && median < 0.02f, "CPU: a deep pool settles (density within 4 %, median speed under 2 cm/s)");
            // (its top layer and the thin film on the walls stay awake: they never sleep, see FluidSim.SleepDensity)
            Check(awake < 0.25f * pool.Count && lastMs < 0.5 * firstMs, "CPU: a settled pool falls asleep and its step costs under half as much");

            // something dropped into it wakes it, and joins it
            float top0 = Percentile(Enumerable.Range(0, pool.Count).Select(i => pool.X[i].Y), 0.99f);
            int src0 = 0, before = pool.Count;
            int added = pool.FillBox(src0, new Vector3(0, top0 + 0.3f, 0), Quaternion.Identity, new Vector3(0.08f, 0.08f, 0.08f), new Vector3(0, -2, 0));
            Run(pool, 2f);
            int hovering = 0; for (int i = 0; i < pool.Count; i++) if (pool.X[i].Y > top0 + 0.08f) hovering++;
            float top1 = Percentile(Enumerable.Range(0, pool.Count).Select(i => pool.X[i].Y), 0.99f);
            var (dens1, _) = Densities(pool);
            Console.WriteLine($"  CPU, {added} particles dropped in: after 2 s {hovering} still above the old surface, the surface {(top1 - top0) * 100:0.0} cm higher, density {dens1:0.000}");
            Check(pool.Count == before + added && hovering == 0 && top1 > top0 && dens1 < 1.05f, "CPU: liquid dropped into a sleeping pool wakes it and joins it");

            // a sphere pushed through it
            var sphere = new Vector3(-w / 2 + 0.1f, 0.4f, 0);
            int worstInside = 0;
            for (int f = 0; f < 15; f++)
            {
                sphere.X = -w / 2 + 0.1f + f * FluidDt * 1f;
                pool.Colliders.Clear(); pool.Colliders.AddRange(tank);
                pool.Colliders.Add(new FleshCollider { Kind = FleshColliderKind.Sphere, A = sphere, R = 0.08f, Velocity = new Vector3(1, 0, 0) });
                pool.Step(FluidDt);
                int inside = 0; for (int i = 0; i < pool.Count; i++) if (Vector3.Distance(pool.X[i], sphere) < 0.08f - 0.25f * s) inside++;
                worstInside = Math.Max(worstInside, inside);
            }
            Console.WriteLine($"  CPU, a sphere moved through the pool at 1 m/s: at most {worstInside} particles inside it");
            Check(worstInside == 0, "CPU: a moving collider wakes the sleeping liquid in its way and pushes it");

            // a wall taken away
            var pool2 = NewPool(out _);
            Run(pool2, 6f);
            int asleep2 = pool2.Count - pool2.Awake;
            pool2.Colliders.RemoveAt(1);
            Run(pool2, 2f);
            int out2 = Outside(pool2.X, pool2.Count, w / 2);
            Console.WriteLine($"  CPU, a wall taken away from a pool with {asleep2} sleeping particles: after 2 s {out2} have flowed out");
            Check(asleep2 > pool2.Count / 2 && out2 > pool2.Count / 3, "CPU: a still collider taken away wakes the liquid it held");

            // a changed liquid wakes it
            Run(pool, 4f);
            int asleepBefore = pool.Count - pool.Awake;
            var thick = water; thick.Viscosity = 0.5f;
            pool.SetLiquid(0, thick);
            pool.Step(FluidDt);
            Console.WriteLine($"  CPU, the liquid's settings changed: {asleepBefore} were asleep, {pool.Count - pool.Awake} after");
            Check(asleepBefore > 0 && pool.Awake == pool.Count, "CPU: changing the liquid wakes it");
        }

        // ---- GPU
        {
            GpuScene NewPool()
            {
                var g = new GpuScene(capacity: 32768, particleSize: s);
                g.Add(tank);
                var r = VolumeRec(1, center, half, water); r.MaxParticles = 30000;
                g.Emitters.Add(r);
                return g;
            }
            var g = NewPool();
            g.Run(0.1f, surface: true);
            g.Rt.Sim.Finish();
            double firstMs = g.Rt.Stats.GpuMs;
            g.Run(7.9f, surface: true);
            g.Rt.Sim.Finish();
            int n = g.Read(out var x, out var v, out _);
            float median = Percentile(Enumerable.Range(0, n).Select(i => v[i].Length()), 0.5f);
            int awake = g.Rt.Stats.Awake, outside = Outside(x, n, w / 2);
            int skipped = 0, built = 0;
            for (int f = 0; f < 30; f++) { g.Frame(surface: true); if (g.Rt.Sim.SkippedLastFrame) skipped++; if (g.Rt.Sim.SurfaceBuiltLastFrame) built++; }
            g.Rt.Sim.Finish();
            double lastMs = g.Rt.Stats.GpuMs;
            Console.WriteLine($"  GPU, {n} particles {depth} m deep: after 8 s {100.0 * awake / Math.Max(1, n):0}% awake, median speed {median * 100:0.0} cm/s, {outside} out of the tank; " +
                              $"{lastMs:0.00} ms a frame now ({firstMs:0.00} in the first frames); of 30 more frames {skipped} skipped and {built} rebuilt the surface");
            Check(outside == 0 && n > 7000, "GPU: a deep pool keeps every particle in its tank");
            Check(median < 0.02f && awake < 0.25f * n, "GPU: a deep pool settles and falls asleep");
            Check(awake > 0 || (skipped > 20 && built < 5), "GPU: with everything asleep the substeps and the surface are skipped");

            // something dropped in
            x = null;
            float top0 = 0; { g.Read(out var x0, out _, out _); top0 = Percentile(x0.Select(p => p.Y), 0.99f); }
            var drop = VolumeRec(2, new Vector3(0, top0 + 0.3f, 0), new Vector3(0.08f, 0.08f, 0.08f), water); drop.MaxParticles = 5000;
            g.Emitters.Add(drop);
            g.Run(2f, surface: true);
            int nd = g.Read(out var xd, out _, out _);
            int hovering = xd.Take(nd).Count(p => p.Y > top0 + 0.08f);
            Console.WriteLine($"  GPU, {g.Count(2)} particles dropped in: after 2 s {hovering} still above the old surface");
            Check(g.Count(2) > 100 && hovering == 0, "GPU: liquid dropped into a sleeping pool wakes it and joins it");
            g.Dispose();

            // a wall taken away
            var g2 = NewPool();
            g2.Run(6f);
            g2.Rt.Sim.Finish();
            int asleep2 = g2.Rt.Stats.Count - g2.Rt.Stats.Awake;
            g2.Colliders.RemoveAt(1);
            g2.Run(2f);
            int n2 = g2.Read(out var x2, out _, out _);
            int out2 = Outside(x2, n2, w / 2);
            g2.Dispose();
            Console.WriteLine($"  GPU, a wall taken away from a pool with {asleep2} sleeping particles: after 2 s {out2} have flowed out");
            Check(asleep2 > n2 / 2 && out2 > n2 / 3, "GPU: a still collider taken away wakes the liquid it held");
        }
    }
}

// 0.22.0: the GPU surface keeps its blocks between builds and recomputes the field only near particles that moved since the last
// build (a sleeping pool's surface was rebuilt whole every frame, about a quarter of the frame); the vertex colours and the
// index tail run over what is used instead of the buffers' whole size.
static partial class Program
{
    static void FluidSurfaceCacheTests()
    {
        Console.WriteLine("TEST fluid surface cache (0.22.0): a surface kept between builds equals one rebuilt whole, and costs less");
        var water = FluidLiquid.Water;
        float s = 0.025f, w = 0.8f;
        var g = new GpuScene(capacity: 32768, particleSize: s);
        g.Add(Tank(w, w, 1.4f));
        var r = VolumeRec(1, new Vector3(0, 0.31f, 0), new Vector3(w / 2 - 0.01f, 0.3f, w / 2 - 0.01f), water); r.MaxParticles = 30000;
        g.Emitters.Add(r);
        g.Run(12f, surface: true);   // (an 80 cm pool still sloshes gently at 6 s: its top 20 cm moves over 3 cm/s)
        // settled: how much of the surface each build recomputes
        double stillShare = 0; int ns = 0, stillBricks = 0;
        for (int f = 0; f < 20; f++) { g.Frame(surface: true); if (f >= 10 && g.Rt.Stats.Bricks > 0) { stillShare += (double)g.Rt.Stats.BricksComputed / g.Rt.Stats.Bricks; ns++; stillBricks = g.Rt.Stats.Bricks; } }
        Console.WriteLine($"  GPU, the settled pool's last build: {g.Rt.Stats.BricksComputed} blocks recomputed of {g.Rt.Stats.Bricks} ({g.Rt.Stats.BricksNew} new, {g.Rt.Stats.BricksMoved} marked by particles that moved, the rest the every-8th-build refresh)");
        stillShare /= Math.Max(1, ns);
        var drop = VolumeRec(2, new Vector3(0.25f, 0.9f, 0.25f), new Vector3(0.05f, 0.05f, 0.05f), water); drop.MaxParticles = 5000;
        g.Emitters.Add(drop);
        int worstComputed = 0, bricks = 0; double share = 0; int n = 0;
        for (int f = 0; f < 50; f++)
        {
            g.Frame(surface: true);
            if (f >= 30 && g.Rt.Stats.Bricks > 0) { share += (double)g.Rt.Stats.BricksComputed / g.Rt.Stats.Bricks; n++; worstComputed = Math.Max(worstComputed, g.Rt.Stats.BricksComputed); bricks = g.Rt.Stats.Bricks; }
        }
        g.Rt.Sim.Finish();
        g.Rt.Sim.ReadSurface(out var va, out var ia);
        int awake = g.Rt.Stats.Awake, count = g.Rt.Stats.Count;
        // the same particles (no step), every block recomputed
        g.Rt.Sim.ForgetSurface();
        g.Rt.Sim.Step(0f, null, 0, false, true);
        g.Rt.Sim.Finish();
        g.Rt.Sim.ReadSurface(out var vb, out var ib);
        int fullComputed = g.Rt.Stats.BricksComputed, deep = g.Rt.Stats.BricksDeep, liveBricks = g.Rt.Stats.Bricks;
        // 0.24.0: and every block computed (no narrow band): the same surface
        g.Rt.Sim.Config.SurfaceNarrowBand = false;
        g.Rt.Sim.ForgetSurface();
        g.Rt.Sim.Step(0f, null, 0, false, true);
        g.Rt.Sim.Finish();
        g.Rt.Sim.ReadSurface(out var vc, out _);
        int allComputed = g.Rt.Stats.BricksComputed;
        g.Dispose();
        int nva = va.Length / 10, nvb = vb.Length / 10;
        // each vertex of one against the nearest of the other (their order differs: they are numbered by atomics)
        float Nearest(float[] from, int nf, float[] to, int nt)
        {
            var grid = new Dictionary<(int, int, int), List<Vector3>>();
            (int, int, int) C(Vector3 p) => ((int)MathF.Floor(p.X / 0.02f), (int)MathF.Floor(p.Y / 0.02f), (int)MathF.Floor(p.Z / 0.02f));
            for (int i = 0; i < nt; i++) { var p = new Vector3(to[i * 10], to[i * 10 + 1], to[i * 10 + 2]); var c = C(p); if (!grid.TryGetValue(c, out var l)) grid[c] = l = new(); l.Add(p); }
            float worstD = 0;
            for (int i = 0; i < nf; i++)
            {
                var p = new Vector3(from[i * 10], from[i * 10 + 1], from[i * 10 + 2]); var (cx, cy, cz) = C(p); float best = 1f;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) foreach (var q in l) best = MathF.Min(best, Vector3.Distance(p, q));
                worstD = MathF.Max(worstD, best);
            }
            return worstD;
        }
        float worst = MathF.Max(Nearest(va, nva, vb, nvb), Nearest(vb, nvb, va, nva));
        int nvc = vc.Length / 10;
        float worstBand = MathF.Max(Nearest(vb, nvb, vc, nvc), Nearest(vc, nvc, vb, nvb));
        Console.WriteLine($"  GPU, the narrow band (0.24.0): a whole rebuild computed {fullComputed} of {liveBricks} blocks and marked {deep} deep inside; without it {allComputed}; " +
                          $"{nvb} against {nvc} vertices, worst difference {worstBand * 1000:0.000} mm");
        Check(deep > 0 && fullComputed + deep >= liveBricks * 0.9 && nvb == nvc && worstBand < 1e-4f, "GPU: the narrow band skips the blocks deep inside and draws the same surface");
        Console.WriteLine($"  GPU, {count} particles ({awake} awake) with a splash in one corner: the kept surface has {nva} vertices, rebuilt whole {nvb} (worst difference " +
                          $"{worst * 1000:0.000} mm); while the splash spreads a build recomputed {100 * share / Math.Max(1, n):0}% of {bricks} blocks, the settled pool before it {100 * stillShare:0}% of {stillBricks}, a whole rebuild {fullComputed}");
        // (particles slower than 2 cm/s do not mark their blocks; every block is recomputed every 8 builds: a little lag allowed)
        Check(nva > 1000 && Math.Abs(nva - nvb) <= nvb / 100 && worst < 0.1f * s, "GPU: a surface kept between builds matches one rebuilt whole (within a tenth of a particle size)");
        // (about half: damping every slow pair calmed it to a quarter, but also slowed oil rising through water and water pushed
        // by a hand, so only surface particles' slow pairs are damped)
        Check(ns > 0 && stillShare < 0.65 && fullComputed >= bricks * 0.9, "GPU: a settled pool's build recomputes well under all of its blocks");
    }
}

// 0.23.0, Lenki in game on 0.22.0 (a pool poured into a room, the Tori world): "the large puddles seem to bounce up and down no
// matter what", and it ran slower than 0.19.0. A mesh collider reaches the liquid as its triangles near the liquid's bounds;
// as the pool's top moved, wall triangles at the edge of those bounds came and went, and each one that went counted as a still
// collider taken away, which woke everything: the squeezed sleeping pool sprang up (the bounce) and never stayed asleep (paying
// for 4 substeps every frame). Now only the sleepers where that shape was are woken.
static partial class Program
{
    static void FluidRoomTests()
    {
        Console.WriteLine("TEST fluid in a room of triangles (0.23.0): a pool whose walls' triangles come and go with its bounds falls asleep and stays still");
        float s = 0.075f;
        var water = FluidLiquid.Water;
        var all = new List<FluidColliderRecord>();
        void Quad(Vector3 o, Vector3 u, Vector3 v, int nu, int nv)
        {
            for (int i = 0; i < nu; i++) for (int k = 0; k < nv; k++)
            {
                Vector3 a = o + u * i + v * k, b = a + u, c = a + u + v, d = a + v;
                foreach (var (p, q, r) in new[] { (a, b, c), (a, c, d) }) all.Add(Rec(FleshCollider.Triangle(p, q, r, true)));
            }
        }
        Quad(new Vector3(-2, 0, -2), new Vector3(0.5f, 0, 0), new Vector3(0, 0, 0.5f), 8, 8);
        // (walls in 10 cm rows: as the pool's top moves, the rows at the edge of its bounds come and go)
        Quad(new Vector3(-2, 0, -2), new Vector3(0.5f, 0, 0), new Vector3(0, 0.1f, 0), 8, 30);
        Quad(new Vector3(-2, 0, 2), new Vector3(0.5f, 0, 0), new Vector3(0, 0.1f, 0), 8, 30);
        Quad(new Vector3(-2, 0, -2), new Vector3(0, 0, 0.5f), new Vector3(0, 0.1f, 0), 8, 30);
        Quad(new Vector3(2, 0, -2), new Vector3(0, 0, 0.5f), new Vector3(0, 0.1f, 0), 8, 30);
        var g = new GpuScene(capacity: 32768, particleSize: s);
        var r = VolumeRec(1, new Vector3(0, 0.3f, 0), new Vector3(1.9f, 0.28f, 1.9f), water); r.MaxParticles = 30000;
        g.Emitters.Add(r);
        // as the plugin sends a mesh: only its triangles within the liquid's last bounds (and 10 cm)
        bool dropRow = false;   // during the measurement: the wall row above the water comes and goes, as in game
        float rowFrom = 9f;     // (0.25.0: the rows from just under the water's top up: a pool that settles level no longer reaches 0.5 m)
        void Frame()
        {
            g.Colliders.Clear();
            var st = g.Rt.Stats;
            if (!st.HasBounds) g.Colliders.AddRange(all);
            else
            {
                var lo = st.Min - new Vector3(0.1f); var hi = st.Max + new Vector3(0.1f);
                foreach (var c in all)
                    if (!(dropRow && MathF.Min(c.AY, MathF.Min(c.BY, c.CY)) >= rowFrom) && MathF.Max(c.AX, MathF.Max(c.BX, c.CX)) >= lo.X && MathF.Min(c.AX, MathF.Min(c.BX, c.CX)) <= hi.X &&
                        MathF.Max(c.AY, MathF.Max(c.BY, c.CY)) >= lo.Y && MathF.Min(c.AY, MathF.Min(c.BY, c.CY)) <= hi.Y &&
                        MathF.Max(c.AZ, MathF.Max(c.BZ, c.CZ)) >= lo.Z && MathF.Min(c.AZ, MathF.Min(c.BZ, c.CZ)) <= hi.Z) g.Colliders.Add(c);
            }
            g.Frame(surface: true);
        }
        // (25 s: a pool in a closed room sloshes a long while; 0.25.0 lets it settle before it sleeps, at about 18 s)
        for (int f = 0; f < 1500; f++) Frame();
        long boxesBefore = g.Rt.Sim.WakeBoxesAsked;
        g.Rt.Sim.Finish(); rowFrom = g.Rt.Stats.Max.Y - 0.05f;
        var awake = new List<int>(); var tops = new List<float>();
        for (int f = 0; f < 240; f++)
        {
            dropRow = (f / 20) % 2 == 1;
            Frame();
            if (f % 10 == 9)
            {
                g.Rt.Sim.Finish(); awake.Add(g.Rt.Stats.Awake);
                g.Rt.Sim.ReadSurface(out var v, out _);
                double sum = 0; int k = 0;
                for (int i = 0; i < v.Length / 10; i++) if (MathF.Abs(v[i * 10]) < 1f && MathF.Abs(v[i * 10 + 2]) < 1f && v[i * 10 + 4] > 0.5f) { sum += v[i * 10 + 1]; k++; }
                tops.Add(k > 0 ? (float)(sum / k) : 0f);
            }
        }
        int n = g.Rt.Stats.Count;
        long boxes = g.Rt.Sim.WakeBoxesAsked - boxesBefore;
        float topRange = tops.Max() - tops.Min();
        g.Dispose();
        Console.WriteLine($"  GPU, {n} particles of 7.5 cm in a 4 x 4 m room of triangles, from 20 s to 24 s, with the wall row above the water coming and going every third of a second (as the pool's top moves in game): {boxes} times a still triangle went (0.22.0 woke everything for each), " +
                          $"awake {100.0 * awake.Average() / Math.Max(1, n):0}% on average (at most {100.0 * awake.Max() / Math.Max(1, n):0}%), the surface's top moved {topRange * 1000:0.0} mm");
        Check(n > 15000 && boxes > 0 && awake.Max() < 0.5f * n && topRange < 0.2f * s, "GPU: a pool in a room of triangles stays asleep and still while the triangles near its edge come and go");
    }
}

// 0.25.0, Lenki in game on 0.24.0: a settled pool looked like gel (bumpy, frozen) instead of still water, and waves hitting
// sleeping water died "like water hitting almost melted ice". Particles fell asleep one by one while the water around them still
// settled, and the pool kept pits and humps (up to 8 cm at 2.5 cm particles; awake it levels to 2 mm). Now a tile of water falls
// asleep together once it is calm, a sleeper keeps its pressure, and a restless tile wakes the tiles around it ahead of a wave.
// The paddle at 0.5 m/s (no faster than ShapeWakeSpeed) also showed that a slow collider went through sleeping water: now one
// moving at all wakes the water it touches.
static partial class Program
{
    static void FluidSettleTests()
    {
        Console.WriteLine("TEST fluid settling (0.25.0): a sleeping pool is level, and a wave carries into sleeping water, CPU and GPU");
        var water = FluidLiquid.Water;
        float s = 0.025f;

        // ---- a settled pool's top: level (CPU)
        float TopSpread(Func<int, Vector3> pos, Func<int, Vector3> nrm, int nv, float half)
        {
            var ys = new List<float>();
            for (int v = 0; v < nv; v++) { var p = pos(v); if (MathF.Abs(p.X) < half && MathF.Abs(p.Z) < half && nrm(v).Y > 0.3f) ys.Add(p.Y); }
            ys.Sort();
            return ys.Count < 20 ? float.MaxValue : ys[ys.Count * 95 / 100] - ys[ys.Count * 5 / 100];
        }
        {
            var sim = new FluidSim(new FluidSettings { ParticleSize = s, MaxParticles = 20000 });
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            sim.Colliders.AddRange(Tank(0.4f, 0.4f, 1.4f));
            int src = sim.AddSource(water);
            int n0 = sim.FillBox(src, new Vector3(0, 0.41f, 0), Quaternion.Identity, new Vector3(0.19f, 0.4f, 0.19f), Vector3.Zero);
            Run(sim, 25f);
            var surf = new FluidSurface();
            surf.Build(sim.X, sim.Src, sim.Count, sim.Liquids, s);
            float spread = TopSpread(v => surf.Positions[v], v => surf.Normals[v], surf.VertexCount, 0.15f);
            Console.WriteLine($"  CPU, {n0} particles 0.8 m deep after 25 s: {100.0 * sim.Awake / sim.Count:0}% awake, the top's middle within {spread * 1000:0.0} mm (5th to 95th percentile; 0.24.0 froze humps over 50 mm)");
            Check(spread < 0.3f * s && sim.Awake < 0.6f * sim.Count, "CPU: a pool falls asleep level (a tile at a time), not frozen in humps");
        }

        // ---- the frame rate (0.25.0, Lenki in game: "the bouncing gets worse the lower the framerate is"): a pool rests at the same
        // height at 60 fps, at 30 fps and with the frame time jumping about, and sleeps (CPU). 0.25.0 at first ran 4 substeps a
        // frame whatever it took: at 30 fps the pool sat 28 mm lower, 8 % squeezed, and never slept; jumping between 20 and 90 fps
        // it breathed up and down by 24 cm. Now the substeps have a fixed length
        {
            (float top, float swing, float awake) Rest(Func<int, float> frameTime)
            {
                var sim = new FluidSim(new FluidSettings { ParticleSize = s, MaxParticles = 20000 });
                sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
                sim.Colliders.AddRange(Tank(0.4f, 0.4f, 1.4f));
                int src = sim.AddSource(water);
                sim.FillBox(src, new Vector3(0, 0.41f, 0), Quaternion.Identity, new Vector3(0.19f, 0.4f, 0.19f), Vector3.Zero);
                float Top()
                {
                    var ys = new List<float>();
                    for (int i = 0; i < sim.Count; i++) if (MathF.Abs(sim.X[i].X) < 0.1f && MathF.Abs(sim.X[i].Z) < 0.1f) ys.Add(sim.X[i].Y);
                    ys.Sort(); return ys[ys.Count * 95 / 100];
                }
                float t = 0f, lo = float.MaxValue, hi = float.MinValue;
                for (int f = 0; t < 20f; f++)
                {
                    float d = frameTime(f);
                    sim.Step(d); t += MathF.Min(d, 1f / 30f);
                    if (t > 12f) { float y = Top(); lo = MathF.Min(lo, y); hi = MathF.Max(hi, y); }
                }
                return (Top(), hi - lo, (float)sim.Awake / sim.Count);
            }
            var rng = new Random(7);
            var at60 = Rest(f => 1f / 60f);
            var at30 = Rest(f => 1f / 30f);
            var jumpy = Rest(f => 1f / (20f + 70f * (float)rng.NextDouble()));
            Console.WriteLine($"  CPU, the same pool's top at 60 fps {at60.top * 1000:0.0} mm ({100 * at60.awake:0}% awake), at 30 fps {at30.top * 1000:0.0} mm ({100 * at30.awake:0}% awake), " +
                              $"at 20 to 90 fps at random {jumpy.top * 1000:0.0} mm ({100 * jumpy.awake:0}% awake), moving at most {Math.Max(at30.swing, jumpy.swing) * 1000:0.0} mm in its last 8 s");
            Check(MathF.Abs(at30.top - at60.top) < 0.2f * s && MathF.Abs(jumpy.top - at60.top) < 0.2f * s && at30.swing < 0.2f * s && jumpy.swing < 0.2f * s &&
                  at30.awake < 0.1f && jumpy.awake < 0.1f, "CPU: a pool rests at the same height and sleeps whatever the frame rate");
        }

        // ---- a wave pushed into sleeping water (CPU): it reaches the far end as it does through awake water
        float FarRise(bool sleeping)
        {
            var sim = new FluidSim(new FluidSettings { ParticleSize = s, MaxParticles = 20000, SleepRadius = sleeping ? 0.25f : 0f });
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            var tank = Tank(1.6f, 0.3f, 1f);
            sim.Colliders.AddRange(tank);
            int src = sim.AddSource(water);
            sim.FillBox(src, new Vector3(0, 0.16f, 0), Quaternion.Identity, new Vector3(0.79f, 0.15f, 0.14f), Vector3.Zero);
            Run(sim, 20f);
            float Level() { float y = 0; int k = 0; for (int i = 0; i < sim.Count; i++) if (sim.X[i].X > 0.6f) { y = MathF.Max(y, sim.X[i].Y); k++; } return y; }
            float before = Level(), most = before;
            // a paddle at the near end pushes 10 cm in 0.2 s, then stays
            for (int f = 0; f < 150; f++)
            {
                float px = -0.75f + MathF.Min(f, 12) * (0.1f / 12f);
                sim.Colliders.Clear(); sim.Colliders.AddRange(tank);
                sim.Colliders.Add(new FleshCollider { Kind = FleshColliderKind.Box, A = new Vector3(px, 0.2f, 0), B = new Vector3(0.02f, 0.2f, 0.15f), Q = Quaternion.Identity, Velocity = new Vector3(f < 12 ? 0.5f : 0f, 0, 0) });
                sim.Step(FluidDt);
                most = MathF.Max(most, Level());
            }
            return most - before;
        }
        float awakeRise = FarRise(false), sleepRise = FarRise(true);
        Console.WriteLine($"  CPU, a paddle's wave at the far end of a 1.6 m tank: rises {awakeRise * 1000:0.0} mm through awake water, {sleepRise * 1000:0.0} mm through sleeping water");
        Check(awakeRise > 0.005f && sleepRise > 0.5f * awakeRise, "CPU: a wave carries into sleeping water instead of dying against it");
    }
}
