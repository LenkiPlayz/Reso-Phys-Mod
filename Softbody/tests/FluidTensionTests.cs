using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;

namespace Softbody.Tests;

// 0.20.0, Lenki in game (0.19.0, GPU, Cohesion and Adhesion about 0.5 to 1): a puddle's edge broke up into blobs, some
// particles "never merge into the main puddle", and particles sitting on the sheet showed as cones with a dent around them
// ("weird forcefields"). Offline the liquid spread to one particle thick, squeezed to 0.6 of its spacing in the sheet (the
// density constraint only pushes when a particle is over rest density, which a sheet never is) and tore into holes and
// blobs. Surface tension (Akinci: cohesion and curvature, stronger where the liquid is thin) holds the sheet together; the
// drawn surface evens out its particles' layering, and DropletSize no longer inflates a particle with 10 neighbours.
static partial class Program
{
    /// <summary>A settled puddle's particles: the inner holes (empty columns, s wide, with at least 30 of the 48 columns
    /// around them full), the particles outside its largest piece (pieces joined within 1.6 s), and the pieces under 20
    /// particles.</summary>
    static (int holes, int outside, int small) PuddleShape(Vector3[] x, int n, float s)
    {
        var cols = new HashSet<(int, int)>();
        int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            int cx = (int)MathF.Floor(x[i].X / s), cz = (int)MathF.Floor(x[i].Z / s);
            cols.Add((cx, cz));
            x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); z0 = Math.Min(z0, cz); z1 = Math.Max(z1, cz);
        }
        int holes = 0;
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
            {
                if (cols.Contains((cx, cz))) continue;
                int ring = 0;
                for (int dx = -3; dx <= 3; dx++) for (int dz = -3; dz <= 3; dz++) if (cols.Contains((cx + dx, cz + dz))) ring++;
                if (ring >= 30) holes++;
            }
        // pieces: union-find over a grid of 1.6 s cells
        float link = 1.6f * s, link2 = link * link;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / link), (int)MathF.Floor(p.Y / link), (int)MathF.Floor(p.Z / link));
        for (int i = 0; i < n; i++) { var c = Cell(x[i]); if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>(); l.Add(i); }
        for (int i = 0; i < n; i++)
        {
            var (cx, cy, cz) = Cell(x[i]);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                        foreach (int j in l)
                            if (j > i && Vector3.DistanceSquared(x[i], x[j]) < link2) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }
        }
        var size = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) { int r = Find(i); size[r] = size.GetValueOrDefault(r) + 1; }
        int largest = size.Values.Max(), small = size.Values.Count(v => v < 20);
        return (holes, n - largest, small);
    }

    /// <summary>A one-particle sheet (a jittered square lattice, 40 x 40 at the spacing) with 16 particles sitting on it, and
    /// the top of the surface drawn over the sheet's middle: the highest point and the highest point of its underside
    /// (in particle sizes). A cone shows as a high top; a dent through the sheet as a high underside.</summary>
    static Vector3[] SheetWithBumps(float s)
    {
        var rng = new Random(3);
        var pts = new List<Vector3>();
        for (int i = 0; i < 40; i++) for (int k = 0; k < 40; k++)
                pts.Add(new Vector3(i * s, 0.5f * s, k * s) + new Vector3((float)rng.NextDouble() - 0.5f, 0, (float)rng.NextDouble() - 0.5f) * 0.3f * s);
        for (int q = 0; q < 16; q++) { int i = 5 + (q % 4) * 9, k = 5 + (q / 4) * 9; pts.Add(new Vector3((i + 0.5f) * s, (q % 2 == 0 ? 1.2f : 1.5f) * s, (k + 0.5f) * s)); }
        return pts.ToArray();
    }

    static (float top, float under) SheetBumps(Func<int, Vector3> pos, Func<int, Vector3> nrm, int vertices, float s)
    {
        float top = float.MinValue, under = float.MinValue;
        for (int v = 0; v < vertices; v++)
        {
            var p = pos(v); var n = nrm(v);
            if (p.X < 2 * s || p.X > 37 * s || p.Z < 2 * s || p.Z > 37 * s) continue;
            if (n.Y > 0.3f && p.Y > 0.5f * s) top = MathF.Max(top, p.Y / s);
            if (n.Y < -0.3f && p.Y < 0.5f * s) under = MathF.Max(under, p.Y / s);
        }
        return (top, under);
    }

    static void FluidTensionTests()
    {
        Console.WriteLine("TEST fluid surface tension and the drawn surface (0.20.0): a puddle holds together, a bump is no cone, CPU and GPU");
        float s = 0.025f;
        var sticky = FluidLiquid.Water; sticky.Cohesion = 1f; sticky.Adhesion = 1f;
        var floor = FleshCollider.Box(new Vector3(0, -0.05f, 0), Quaternion.Identity, new Vector3(10f, 0.05f, 10f));
        var center = new Vector3(0, 0.35f, 0); var half = new Vector3(0.3f, 0.3f, 0.3f);

        // ---- CPU: a block dropped on a wide floor spreads into a puddle
        {
            var sim = new FluidSim(new FluidSettings { ParticleSize = s });
            sim.Settings.KillMin = new Vector3(-50, -20, -50); sim.Settings.KillMax = new Vector3(50, 50, 50);
            sim.Colliders.Add(floor);
            int src = sim.AddSource(sticky);
            int n0 = sim.FillBox(src, center, Quaternion.Identity, half, Vector3.Zero);
            for (int f = 0; f < 480; f++) sim.Step(FluidDt);
            var (holes, outside, small) = PuddleShape(sim.X, sim.Count, s);
            Console.WriteLine($"  CPU, {n0} particles at Cohesion 1, Adhesion 1, after 8 s on a floor: {holes} holes inside the puddle, {outside} particles outside its main piece, {small} pieces under 20 particles (0.19.0: hundreds of holes and dozens of blobs)");
            // (0.20.0: 39 holes on the CPU, 24 on the GPU; 0.21.0 to 0.23.0: 50 to 124, its sheet fell asleep with its holes open;
            // 0.24.0: 35 and 29; 0.19.0: hundreds)
            Check(sim.Count == n0 && holes <= 60 && outside <= 0.01f * n0 && small <= 10, "CPU: a sticky puddle holds together (few holes, almost no loose blobs)");
        }
        // ---- GPU: the same
        {
            var g = new GpuScene(capacity: 32768, particleSize: s);
            g.Colliders.Add(Rec(floor));
            var r = VolumeRec(1, center, half, sticky); r.MaxParticles = 30000;
            g.Emitters.Add(r);
            g.Run(8f);
            int n = g.Read(out var x, out _, out _);
            var (holes, outside, small) = PuddleShape(x, n, s);
            g.Dispose();
            Console.WriteLine($"  GPU, {n} particles, the same: {holes} holes inside the puddle, {outside} particles outside its main piece, {small} pieces under 20 particles");
            Check(n > 13000 && holes <= 60 && outside <= 0.01f * n && small <= 10, "GPU: a sticky puddle holds together (few holes, almost no loose blobs)");
        }

        // ---- the drawn surface of a sheet with particles sitting on it (frozen: the drawing alone)
        var pts = SheetWithBumps(s);
        float cpuTop, cpuUnder;
        {
            var surf = new FluidSurface();
            surf.Build(pts, new ushort[pts.Length], pts.Length, new List<FluidLiquid> { FluidLiquid.Water }, s);
            (cpuTop, cpuUnder) = SheetBumps(v => surf.Positions[v], v => surf.Normals[v], surf.VertexCount, s);
            var old = new FluidSurface { Smoothing = 0f, DropletFade = 20f };
            old.Build(pts, new ushort[pts.Length], pts.Length, new List<FluidLiquid> { FluidLiquid.Water }, s);
            var (oTop, oUnder) = SheetBumps(v => old.Positions[v], v => old.Normals[v], old.VertexCount, s);
            Console.WriteLine($"  CPU, a sheet with 16 particles on it: the surface's highest point {cpuTop:0.00} particle sizes up, its underside's {cpuUnder:0.00} (0.19.0's drawing: {oTop:0.00} and {oUnder:0.00})");
            Check(cpuTop < 1.4f && cpuTop < oTop - 0.2f && cpuUnder < 0.15f, "CPU: a particle on a sheet is drawn as a low bump, not a cone, and leaves no dent through it");
        }
        {
            using var sim = new GpuFluidSim(GpuDevice(), new GpuFluidConfig { Capacity = 4096 }, FluidHlsl());
            var l = FluidLiquid.Water;
            sim.SetLiquids(new[] { new GpuLiquid { A = new Vector4(l.Viscosity, l.Cohesion, l.Adhesion, l.Friction), B = new Vector4(l.Density, 0f, 0f, 0f), Color = l.Color, Mask = uint.MaxValue, Alive = 1 } }, 1);
            sim.SetShapes(new GpuShapeGrid());
            sim.SetDropletSize(1.5f);
            var spawns = pts.Select(p => new GpuSpawn { Pos = new Vector4(p, GpuFluidSim.AsFloat(0)), Vel = Vector4.Zero }).ToArray();
            sim.Step(0f, spawns, spawns.Length, false, true);
            sim.ReadSurface(out var verts, out _);
            var (gTop, gUnder) = SheetBumps(v => new Vector3(verts[v * 10], verts[v * 10 + 1], verts[v * 10 + 2]),
                                            v => new Vector3(verts[v * 10 + 3], verts[v * 10 + 4], verts[v * 10 + 5]), verts.Length / 10, s);
            Console.WriteLine($"  GPU, the same sheet: the surface's highest point {gTop:0.00} particle sizes up, its underside's {gUnder:0.00}");
            Check(gTop < 1.4f && gUnder < 0.15f && MathF.Abs(gTop - cpuTop) < 0.1f, "GPU: a particle on a sheet is drawn as a low bump, as on the CPU");
        }
    }
}
