using System.Numerics;
using SharpDX.Direct3D;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;
using Softbody.FluidGpu;
using D3DDevice = SharpDX.Direct3D11.Device;

namespace Softbody.Tests;

// 0.16.0: the GPU fluid (the renderer fork's side), run on this machine's GPU through the same runtime the renderer uses,
// fed the same records the plugin writes into the shared channel. The checks are the CPU version's.
static partial class Program
{
    static D3DDevice _gpu;
    static string _gpuName;

    static D3DDevice GpuDevice()
    {
        if (_gpu != null) return _gpu;
        try { _gpu = new D3DDevice(DriverType.Hardware, SharpDX.Direct3D11.DeviceCreationFlags.None, FeatureLevel.Level_11_0); }
        catch { _gpu = new D3DDevice(DriverType.Warp, SharpDX.Direct3D11.DeviceCreationFlags.None, FeatureLevel.Level_11_0); }
        try { using var dx = _gpu.QueryInterface<SharpDX.DXGI.Device>(); _gpuName = dx.Adapter.Description.Description.Trim(); } catch { _gpuName = "?"; }
        return _gpu;
    }

    static FluidColliderRecord Rec(in FleshCollider c)
    {
        return new FluidColliderRecord
        {
            Kind = (int)c.Kind, Category = c.Source, AX = c.A.X, AY = c.A.Y, AZ = c.A.Z, BX = c.B.X, BY = c.B.Y, BZ = c.B.Z,
            CX = c.C.X, CY = c.C.Y, CZ = c.C.Z, R = c.R, QX = c.Q.X, QY = c.Q.Y, QZ = c.Q.Z, QW = c.Q.W, TwoSided = c.TwoSided ? 1 : 0,
            VelX = c.Velocity.X, VelY = c.Velocity.Y, VelZ = c.Velocity.Z,
        };
    }

    static FluidEmitterRecord EmitterRec(int id, int mode, Vector3 pos, Quaternion rot, FluidLiquid l)
    {
        return new FluidEmitterRecord
        {
            Id = id, Mode = mode, Pouring = 1, Active = 1, MaxParticles = 20000, Rate = 1f, Speed = 1.5f, NozzleRadius = 0.02f,
            PosX = pos.X, PosY = pos.Y, PosZ = pos.Z, RotX = rot.X, RotY = rot.Y, RotZ = rot.Z, RotW = rot.W,
            Viscosity = l.Viscosity, Cohesion = l.Cohesion, Adhesion = l.Adhesion, Friction = l.Friction, Density = l.Density,
            Gravity = l.Gravity, Damping = l.Damping, Lifetime = l.Lifetime, CollideMask = l.CollideMask,
            ColR = l.Color.X, ColG = l.Color.Y, ColB = l.Color.Z, ColA = l.Color.W,
        };
    }

    static FluidEmitterRecord VolumeRec(int id, Vector3 center, Vector3 half, FluidLiquid l)
    {
        var r = EmitterRec(id, 1, center, Quaternion.Identity, l);
        r.HalfX = half.X; r.HalfY = half.Y; r.HalfZ = half.Z;
        return r;
    }

    sealed class GpuScene
    {
        public readonly GpuFluidRuntime Rt;
        public readonly List<FluidEmitterRecord> Emitters = new();
        public readonly List<FluidColliderRecord> Colliders = new();
        public GpuScene(int capacity = 65536, float particleSize = 0.025f) { Rt = new GpuFluidRuntime(GpuDevice(), new GpuFluidConfig { Capacity = capacity, ParticleSize = particleSize }, FluidHlsl()); }
        public void Add(IEnumerable<FleshCollider> cs) { foreach (var c in cs) Colliders.Add(Rec(c)); }
        // at most 3 frames queued on the GPU, as a game's swap chain keeps it
        public void Frame(bool surface = false, float detail = 1f, float droplet = 1.5f) { Rt.Frame(FluidDt, Emitters.ToArray(), Emitters.Count, Colliders.ToArray(), Colliders.Count, surface, detail, droplet); Rt.Sim.Throttle(3); }
        public void Run(float seconds, Action<int> perFrame = null, bool surface = false)
        {
            int frames = (int)(seconds / FluidDt);
            for (int f = 0; f < frames; f++) { perFrame?.Invoke(f); Frame(surface); }
        }
        public int Read(out Vector3[] x, out Vector3[] v, out uint[] src)
        {
            int n = Rt.Sim.ReadParticles(out var p4, out var v4);
            x = new Vector3[n]; v = new Vector3[n]; src = new uint[n];
            for (int i = 0; i < n; i++) { x[i] = new Vector3(p4[i].X, p4[i].Y, p4[i].Z); v[i] = new Vector3(v4[i].X, v4[i].Y, v4[i].Z); src[i] = GpuFluidSim.AsUint(p4[i].W); }
            return n;
        }
        public int Count(int id) { Rt.Sim.Finish(); return Rt.CountOf(id); }
        public void Dispose() => Rt.Dispose();
    }

    /// <summary>Relative number density at a sample of particles (rest = 1), on the CPU from the read-back positions.</summary>
    static (float mean, float max) GpuDensities(Vector3[] x, float s, int sample = 400)
    {
        float h = 2f * s, h2 = h * h;
        float poly6 = 315f / (64f * MathF.PI * MathF.Pow(h, 9));
        float W(float r2) { float d = h2 - r2; return d > 0f ? poly6 * d * d * d : 0f; }
        float rho0 = 0f; int k = 2;
        for (int a = -k; a <= k; a++) for (int b = -k; b <= k; b++) for (int c = -k; c <= k; c++) rho0 += W(new Vector3(a, b, c).LengthSquared() * s * s);
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < x.Length; i++)
        {
            var key = ((int)MathF.Floor(x[i].X / h), (int)MathF.Floor(x[i].Y / h), (int)MathF.Floor(x[i].Z / h));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
            l.Add(i);
        }
        double sum = 0; float max = 0; int n = 0;
        int stride = Math.Max(1, x.Length / sample);
        for (int i = 0; i < x.Length; i += stride)
        {
            float rho = W(0f);
            var c = ((int)MathF.Floor(x[i].X / h), (int)MathF.Floor(x[i].Y / h), (int)MathF.Floor(x[i].Z / h));
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l))
                            foreach (int j in l) if (j != i) rho += W(Vector3.DistanceSquared(x[i], x[j]));
            float d = rho / rho0; sum += d; max = MathF.Max(max, d); n++;
        }
        return (n > 0 ? (float)(sum / n) : 0, max);
    }

    static void GpuFluidTests()
    {
        Console.WriteLine("TEST GPU fluid (0.16.0): the renderer fork's Direct3D 11 version, from the plugin's records");
        var dev = GpuDevice();
        Console.WriteLine($"  device: {_gpuName}, feature level {dev.FeatureLevel}");
        var water = FluidLiquid.Water;
        float s = 0.025f, vol = s * s * s;

        // ---- a dam break in a tank
        {
            var g = new GpuScene();
            g.Add(Tank(0.6f, 0.4f, 0.5f));
            g.Emitters.Add(VolumeRec(1, new Vector3(-0.15f, 0.15f, 0f), new Vector3(0.15f, 0.15f, 0.2f), water));
            g.Frame();
            int n0 = g.Count(1);
            g.Run(3f);
            int n = g.Read(out var x, out var v, out _);
            int outside = 0; float top = 0, meanSpeed = 0, maxSpeed = 0;
            for (int i = 0; i < n; i++)
            {
                var p = x[i];
                if (MathF.Abs(p.X) > 0.31f || MathF.Abs(p.Z) > 0.21f || p.Y < -0.01f || p.Y > 0.6f) outside++;
                top = MathF.Max(top, p.Y); meanSpeed += v[i].Length() / n; maxSpeed = MathF.Max(maxSpeed, v[i].Length());
            }
            var (mean, max) = GpuDensities(x, s);
            float expected = n0 * vol / (0.6f * 0.4f);
            var st = g.Rt.Stats;
            Console.WriteLine($"  dam break: {n0} particles in a 60 x 40 cm tank, after 3 s: {n} left, {outside} outside, density {mean:0.00} of rest on average ({max:0.00} at most), " +
                              $"moving {meanSpeed:0.000} m/s on average (fastest {maxSpeed:0.00}), top {top * 100:0.0} cm (flat: {expected * 100:0.0} cm); {st.MeanNeighbours:0} neighbours each, GPU {st.GpuMs:0.00} ms a frame");
            Check(n == n0 && outside == 0, "GPU: a dam break stays in its tank: no particle lost or through a wall");
            Check(mean > 0.85f && mean < 1.15f && max < 1.6f, "GPU: the water keeps its volume (density near rest, nothing squashed)");
            Check(meanSpeed < 0.1f && top > 0.6f * expected && top < 1.5f * expected, "GPU: it settles into a layer about as deep as the water's volume over the floor");
            g.Dispose();
        }

        // ---- the pour rate
        {
            var g = new GpuScene();
            var e = EmitterRec(1, 0, new Vector3(0, 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), water);
            e.Rate = 2f; e.Speed = 1f; e.NozzleRadius = 0.03f;
            g.Emitters.Add(e);
            g.Run(1f);
            int n = g.Count(1);
            float litres = n * vol * 1000f;
            Console.WriteLine($"  pouring 2 L/s for 1 s: {n} particles, {litres:0.00} L");
            Check(MathF.Abs(litres - 2f) < 0.05f, "GPU: an emitter pours its rate in litres per second");
            g.Dispose();
        }

        // ---- honey against water
        float Spread(float viscosity)
        {
            var g = new GpuScene();
            g.Colliders.Add(Rec(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f))));
            var l = water; l.Viscosity = viscosity;
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.12f, 0), new Vector3(0.1f), l));
            g.Run(1f);
            int n = g.Read(out var x, out _, out _);
            float r = 0; for (int i = 0; i < n; i++) r += new Vector2(x[i].X, x[i].Z).Length();
            g.Dispose();
            return r / Math.Max(1, n);
        }
        float sWater = Spread(0.02f), sHoney = Spread(0.6f);
        Console.WriteLine($"  a 20 cm block dropped on a floor, after 1 s: water spread {sWater * 100:0.0} cm on average from the middle, honey {sHoney * 100:0.0} cm");
        Check(sHoney < 0.8f * sWater, "GPU: a viscous liquid (honey) flows slower than water");

        // ---- cohesion (straight through the simulation: the particles start with random velocities)
        float BlobSize(float cohesion)
        {
            using var sim = new GpuFluidSim(GpuDevice(), new GpuFluidConfig { Capacity = 4096 }, FluidHlsl());
            var l = water; l.Gravity = 0f; l.Cohesion = cohesion; l.Damping = 0f;
            var liq = new[] { new GpuLiquid { A = new Vector4(l.Viscosity, l.Cohesion, l.Adhesion, l.Friction), B = new Vector4(l.Density, l.Gravity, l.Damping, 0), Color = l.Color, Mask = uint.MaxValue, Alive = 1 } };
            sim.SetLiquids(liq, 1);
            sim.SetShapes(new GpuShapeGrid());
            var rng = new Random(5);
            var spawns = new List<GpuSpawn>();
            for (int y = 0; y < 4; y++) for (int z = 0; z < 4; z++) for (int x = 0; x < 4; x++)
                    {
                        var p = new Vector3((x + 0.5f) * s - 0.05f, (y + 0.5f) * s - 0.05f, (z + 0.5f) * s - 0.05f);
                        var v = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.6f;
                        spawns.Add(new GpuSpawn { Pos = new Vector4(p, GpuFluidSim.AsFloat(0)), Vel = new Vector4(v, 0) });
                    }
            sim.Step(FluidDt, spawns.ToArray(), spawns.Count, false, false);
            for (int f = 1; f < 60; f++) sim.Step(FluidDt, null, 0, false, false);
            int n = sim.ReadParticles(out var pos, out _);
            var c = Vector3.Zero; for (int i = 0; i < n; i++) c += new Vector3(pos[i].X, pos[i].Y, pos[i].Z); c /= Math.Max(1, n);
            float r = 0; for (int i = 0; i < n; i++) r += Vector3.Distance(new Vector3(pos[i].X, pos[i].Y, pos[i].Z), c);
            return r / Math.Max(1, n);
        }
        float loose = BlobSize(0f), held = BlobSize(0.8f);
        Console.WriteLine($"  a floating 10 cm blob kicked apart, after 1 s: {loose * 100:0.0} cm from its middle on average without cohesion, {held * 100:0.0} cm with cohesion 0.8");
        Check(held < 0.4f * loose, "GPU: a cohesive liquid holds together as a drop");

        // ---- oil floats on water
        {
            var g = new GpuScene();
            g.Add(Tank(0.3f, 0.3f, 0.6f));
            var oilL = water; oilL.Density = 0.6f; oilL.Color = new Vector4(0.9f, 0.7f, 0.1f, 0.8f);
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.075f, 0), new Vector3(0.15f, 0.075f, 0.15f), oilL));
            g.Emitters.Add(VolumeRec(2, new Vector3(0, 0.225f, 0), new Vector3(0.15f, 0.075f, 0.15f), water));
            g.Frame();
            g.Run(6f); // (0.21.0: 6 s; it overturns more slowly, and at 4 s the two were level)
            int n = g.Read(out var x, out _, out var src);
            uint oilSrc = src.Length > 0 ? src.Min() : 0;
            double yw = 0, yo = 0; int nw = 0, no = 0;
            for (int i = 0; i < n; i++) { if (src[i] == oilSrc) { yo += x[i].Y; no++; } else { yw += x[i].Y; nw++; } }
            Console.WriteLine($"  oil (density 0.6) under water in a tank, after 6 s: oil at {yo / Math.Max(1, no) * 100:0.0} cm on average, water at {yw / Math.Max(1, nw) * 100:0.0} cm ({no} + {nw})");
            Check(no > 0 && nw > 0 && yo / no > yw / nw, "GPU: a lighter liquid rises above a heavier one (oil floats on water)");
            g.Dispose();
        }

        // ---- no particle inside a solid box
        {
            var g = new GpuScene();
            g.Colliders.Add(Rec(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(2f, 0.1f, 2f))));
            var block = FleshCollider.Box(new Vector3(0, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), new Vector3(0.1f, 0.1f, 0.1f));
            g.Colliders.Add(Rec(block));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.45f, 0), new Vector3(0.12f, 0.1f, 0.12f), water));
            g.Run(2f);
            int n = g.Read(out var x, out _, out _);
            int inside = 0;
            for (int i = 0; i < n; i++)
            {
                var local = Vector3.Transform(x[i] - block.A, Quaternion.Conjugate(block.Q));
                if (MathF.Abs(local.X) < block.B.X - 0.003f && MathF.Abs(local.Y) < block.B.Y - 0.003f && MathF.Abs(local.Z) < block.B.Z - 0.003f) inside++;
            }
            Console.WriteLine($"  water poured over a box on a floor, after 2 s: {inside} of {n} particles inside the box");
            Check(n > 0 && inside == 0, "GPU: the liquid flows around solid colliders, never through them");
            g.Dispose();
        }

        // ---- a hand pushes the water
        {
            var g = new GpuScene();
            g.Add(Tank(0.8f, 0.3f, 0.4f));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.06f, 0), new Vector3(0.4f, 0.06f, 0.15f), water));
            g.Run(1f);
            int walls = g.Colliders.Count;
            float pushedX = 0; int near = 0, inside = 0;
            for (int f = 0; f < 45; f++)
            {
                var hand = new Vector3(-0.3f + 0.6f * f / 45f, 0.05f, 0f);
                var sphere = FleshCollider.Sphere(hand, 0.05f, (byte)FluidSim.CategoryHands);
                sphere.Velocity = new Vector3(0.6f / (45f * FluidDt), 0, 0);
                if (g.Colliders.Count > walls) g.Colliders.RemoveAt(walls);
                g.Colliders.Add(Rec(sphere));
                g.Frame();
                if (f == 30)
                {
                    int n = g.Read(out var x, out var v, out _);
                    for (int i = 0; i < n; i++)
                    {
                        float d = Vector3.Distance(x[i], hand);
                        if (d < 0.05f - 0.003f) inside++;
                        if (d < 0.12f && x[i].X > hand.X) { pushedX += v[i].X; near++; }
                    }
                }
            }
            Console.WriteLine($"  a 10 cm hand sphere swept through a shallow tank at 0.8 m/s: {inside} particles inside it, the water just ahead moving {pushedX / Math.Max(1, near):0.00} m/s along with it");
            Check(inside == 0 && near > 0 && pushedX / near > 0.05f, "GPU: a user's hand pushes the water ahead of it (one way)");
            g.Dispose();
        }

        // ---- friction (straight through the simulation: the block starts sliding)
        float Slide(float friction)
        {
            using var sim = new GpuFluidSim(GpuDevice(), new GpuFluidConfig { Capacity = 4096 }, FluidHlsl());
            var l = water; l.Friction = friction; l.Viscosity = 0.02f;
            sim.SetLiquids(new[] { new GpuLiquid { A = new Vector4(l.Viscosity, l.Cohesion, l.Adhesion, l.Friction), B = new Vector4(l.Density, l.Gravity, l.Damping, 0), Color = l.Color, Mask = uint.MaxValue, Alive = 1 } }, 1);
            var shapes = new GpuShapeGrid();
            shapes.Add(Rec(FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(50, 0.5f, 50))));
            shapes.Build(new Vector3(-1, -0.2f, -1), new Vector3(5, 1, 1), 0.1f, 2 * s);
            sim.SetShapes(shapes);
            var spawns = new List<GpuSpawn>();
            for (int y = 0; y < 4; y++) for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++)
                        spawns.Add(new GpuSpawn { Pos = new Vector4((x + 0.5f) * s - 0.1f, (y + 0.5f) * s, (z + 0.5f) * s - 0.1f, GpuFluidSim.AsFloat(0)), Vel = new Vector4(2f, 0, 0, 0) });
            sim.Step(FluidDt, spawns.ToArray(), spawns.Count, false, false);
            for (int f = 1; f < 90; f++) sim.Step(FluidDt, null, 0, false, false);
            int n = sim.ReadParticles(out var pos, out _);
            float xs = 0; for (int i = 0; i < n; i++) xs += pos[i].X; return xs / Math.Max(1, n);
        }
        float none = Slide(0f), full = Slide(1f);
        Console.WriteLine($"  a block of water sliding on a floor at 2 m/s, after 1.5 s: {none * 100:0} cm with no friction, {full * 100:0} cm with Friction 1");
        Check(full < 0.5f * none, "GPU: Friction slows water sliding over a surface");

        // ---- the emitters: caps, a lowered cap, ClearLiquid, Refill, removal
        {
            var g = new GpuScene();
            g.Colliders.Add(Rec(FleshCollider.Box(new Vector3(0, -0.5f, 0), Quaternion.Identity, new Vector3(50, 0.5f, 50))));
            var pour = EmitterRec(1, 0, new Vector3(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), water);
            pour.Rate = 5f; pour.Speed = 1f; pour.NozzleRadius = 0.03f; pour.MaxParticles = 100;
            var volm = VolumeRec(2, new Vector3(2, 0.2f, 0), new Vector3(0.1f), water); volm.MaxParticles = 4000;
            g.Emitters.Add(pour); g.Emitters.Add(volm);
            void Frames(int n) { for (int f = 0; f < n; f++) g.Frame(); }
            Frames(180);
            int capped = g.Count(1), filled = g.Count(2);
            pour.MaxParticles = 40; g.Emitters[0] = pour;
            Frames(10);
            int trimmed = g.Count(1);
            volm.ClearCount++; g.Emitters[1] = volm;
            Frames(2);
            int cleared = g.Count(2);
            Frames(30);
            int stillClear = g.Count(2);
            volm.RefillCount++; volm.PosX = 3; g.Emitters[1] = volm;
            Frames(2);
            int refilled = g.Count(2);
            g.Emitters.RemoveAt(0);
            Frames(2);
            int gone = g.Count(1);
            int total = g.Read(out _, out _, out _);
            Console.WriteLine($"  emitters: pouring 5 L/s with MaxParticles 100 for 3 s keeps {capped}; MaxParticles lowered to 40 keeps {trimmed}; a 20 cm volume fills {filled}, " +
                              $"ClearLiquid leaves {cleared} (and {stillClear} after half a second), Refill at a new place gives {refilled}; a removed emitter leaves {gone} ({total} particles in all)");
            Check(capped == 100 && trimmed == 40, "GPU: MaxParticles caps an emitter (pouring stops at it, and lowering it removes the share over it)");
            Check(filled == 512 && cleared == 0 && stillClear == 0 && refilled == 512, "GPU: a Volume emitter fills its block, ClearLiquid empties it (it stays empty), and Refill fills it again");
            Check(gone == 0 && total == 512, "GPU: an emitter that is gone takes its liquid with it");
            g.Dispose();
        }

        // ---- a stream onto a single-sided mesh floor, and into nothing
        {
            var g = new GpuScene();
            var (fpos, ftris) = BumpyFloor(40, 80f, new Vector3(-40, 0, -40));
            for (int t = 0; t < ftris.Length; t += 3)
                g.Colliders.Add(Rec(FleshCollider.Triangle(fpos[ftris[t]], fpos[ftris[t + 1]], fpos[ftris[t + 2]], false)));
            var e = EmitterRec(1, 0, new Vector3(0, 1.5f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f), water);
            g.Emitters.Add(e);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cpuMs = new List<double>();
            var colliders = g.Colliders.ToArray(); var emitters = g.Emitters.ToArray();
            for (int f = 0; f < 300; f++) { g.Rt.Frame(FluidDt, emitters, emitters.Length, colliders, colliders.Length, true); g.Rt.Sim.Throttle(3); if (f > 10) cpuMs.Add(g.Rt.CpuMs); }
            cpuMs.Sort();
            double worstCpu = cpuMs[^1], medianCpu = cpuMs[cpuMs.Count / 2];
            int n = g.Read(out var x, out _, out _);
            int below = 0; for (int i = 0; i < n; i++) if (x[i].Y < -0.01f) below++;
            Console.WriteLine($"  pouring 1 L/s from 1.5 m onto a single-sided mesh floor ({g.Colliders.Count} triangles) for 5 s: {n} particles, {below} below the floor; " +
                              $"CPU {medianCpu:0.00} ms a frame (worst {worstCpu:0.00}) with {g.Rt.Shapes.ShapeCount} triangles near the liquid, GPU {g.Rt.Stats.GpuMs:0.00} ms");
            Check(medianCpu < 1.0, "GPU: preparing a frame with a big mesh floor stays cheap on the render thread");
            Check(n > 300 && below == 0, "GPU: a stream poured from a height lands on a mesh floor (single-sided triangles) instead of falling through it");
            g.Dispose();

            var g2 = new GpuScene();
            g2.Emitters.Add(e);
            for (int f = 0; f < 600; f++) g2.Frame(surface: f % 10 == 0);
            int n2 = g2.Read(out var x2, out _, out _);
            float lowest = 0; for (int i = 0; i < n2; i++) lowest = MathF.Min(lowest, x2[i].Y);
            Console.WriteLine($"  pouring into nothing for 10 s: {n2} particles kept (the rest removed below {lowest:0.0} m), {g2.Rt.Stats.Bricks} surface blocks");
            // the kill line is at -18.5 m; a particle past it is removed in the next substep's Predict, so one falling at about
            // 20 m/s can be read up to a substep's fall (0.17 m) below it (0.20.0's run read -18.7; 0.19.0's -18.6 was luck)
            Check(lowest > -18.5f - 0.25f && n2 > 0, "GPU: liquid falling away is removed 20 m below its emitter");
            g2.Dispose();
        }

        // ---- the surface
        {
            var g = new GpuScene();
            g.Add(Tank(0.6f, 0.4f, 0.5f));
            g.Emitters.Add(VolumeRec(1, new Vector3(0, 0.08f, 0), new Vector3(0.3f, 0.08f, 0.2f), water));
            g.Run(1.5f);
            g.Frame(surface: true);
            g.Rt.Sim.ReadSurface(out var verts, out var idx);
            int n = g.Read(out var x, out _, out _);
            int nv = verts.Length / 10, tris = idx.Length / 3;
            Vector3 P(int v) => new(verts[v * 10], verts[v * 10 + 1], verts[v * 10 + 2]);
            Vector3 N(int v) => new(verts[v * 10 + 3], verts[v * 10 + 4], verts[v * 10 + 5]);
            int up = 0, total = 0, outward = 0; float topY = 0; int topN = 0;
            for (int v = 0; v < nv; v++)
            {
                var p = P(v);
                if (MathF.Abs(p.X) < 0.2f && MathF.Abs(p.Z) < 0.12f && p.Y > 0.05f) { total++; if (N(v).Y > 0.8f) up++; topY += p.Y; topN++; }
            }
            for (int t = 0; t < tris; t++)
            {
                int a = (int)idx[3 * t], b = (int)idx[3 * t + 1], c = (int)idx[3 * t + 2];
                var fn = Vector3.Cross(P(b) - P(a), P(c) - P(a));
                if (Vector3.Dot(fn, N(a) + N(b) + N(c)) > 0f) outward++;
            }
            float waterTop = 0; for (int i = 0; i < n; i++) waterTop = MathF.Max(waterTop, x[i].Y);
            var col = nv > 0 ? new Vector4(verts[6], verts[7], verts[8], verts[9]) : Vector4.Zero;
            Console.WriteLine($"  surface of a settled pool ({n} particles): {nv} vertices, {tris} triangles, {g.Rt.Stats.Bricks} blocks; {(total > 0 ? 100f * up / total : 0):0}% of the top's normals face up, " +
                              $"top at {(topN > 0 ? topY / topN * 100 : 0):0.0} cm (highest particle {waterTop * 100:0.0} cm); {100f * outward / Math.Max(1, tris):0.0}% wound outward; a vertex colour {col}");
            Check(nv > 100 && total > 0 && up > 0.8f * total, "GPU: the surface of a still pool is flat on top (its normals face up)");
            Check(tris > 0 && outward > 0.97f * tris, "GPU: the surface's triangles are wound outward, matching their normals");
            Check(topN > 0 && MathF.Abs(topY / topN - waterTop) < 1.5f * s, "GPU: the surface sits on the water (within a particle or so of the top layer)");
            Check(MathF.Abs(col.X - water.Color.X) < 0.01f && MathF.Abs(col.W - water.Color.W) < 0.01f, "GPU: the surface takes the liquid's colour");
            g.Dispose();
        }
        {
            var g = new GpuScene();
            var l = water; l.Gravity = 0f;
            g.Emitters.Add(VolumeRec(1, Vector3.Zero, new Vector3(0.04f), l));
            g.Frame(surface: true);
            g.Rt.Sim.ReadSurface(out var verts, out var idx);
            var edges = new Dictionary<(uint, uint), int>();
            for (int t = 0; t + 2 < idx.Length; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    uint a = idx[t + e], b = idx[t + (e + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            int open = edges.Values.Count(c => c != 2);
            Console.WriteLine($"  a lone drop: {verts.Length / 10} vertices, {idx.Length / 3} triangles, {open} edges not shared by exactly two");
            Check(verts.Length / 10 > 20 && open == 0, "GPU: a drop's surface is closed");
            g.Dispose();
        }

        // ---- the shared channel, both ends (under a test name: never the game's)
        {
            check_sizes();
            void check_sizes()
            {
                int es = System.Runtime.InteropServices.Marshal.SizeOf<FluidEmitterRecord>(), cs = System.Runtime.InteropServices.Marshal.SizeOf<FluidColliderRecord>();
                int hs = System.Runtime.InteropServices.Marshal.SizeOf<FluidHeader>(), bs = System.Runtime.InteropServices.Marshal.SizeOf<FluidBack>();
                Check(es <= FluidChannel.EmitterSize && cs <= FluidChannel.ColliderSize && hs <= FluidChannel.HeaderSize && bs <= 256,
                      $"the channel's records fit their blocks (emitter {es} bytes, collider {cs}, header {hs}, back {bs})");
            }
            string name = @"Local\Softbody.Fluid.Test." + Environment.ProcessId;
            using var plugin = FluidChannelIO.Open(name);
            using var renderer = FluidChannelIO.Open(name);
            var g = new GpuScene();
            var floor = Rec(FleshCollider.Box(new Vector3(0, -0.1f, 0), Quaternion.Identity, new Vector3(1f, 0.1f, 1f)));
            var em = new[] { VolumeRec(77, new Vector3(0, 0.15f, 0), new Vector3(0.1f), water) };
            FluidEmitterRecord[] rem = null; FluidColliderRecord[] rcol = null;
            int read = 0, owner = 12345;
            var pairs = new int[FluidChannel.MaxEmitters * 2];
            for (int f = 0; f < 60; f++)
            {
                plugin.WriteFrame(new FluidHeader { Frame = f + 1, Active = 1, Owner = owner, Dt = FluidDt, Time = f * FluidDt, MaxParticles = 65536 }, em, 1, new[] { floor }, 1);
                if (!renderer.TryReadFrame(out var h, ref rem, ref rcol)) continue;
                read++;
                g.Rt.Frame(h.Dt, rem, h.EmitterCount, rcol, h.ColliderCount, false);
                g.Rt.Sim.Throttle(3);
                pairs[0] = rem[0].Id; pairs[1] = g.Rt.CountOf(rem[0].Id);
                renderer.WriteBack(new FluidBack { Frame = h.Frame, Ready = 1, Owner = h.Owner, Particles = g.Rt.Stats.Count }, pairs, 1);
            }
            g.Rt.Sim.Finish();
            var back = new int[FluidChannel.MaxEmitters * 2];
            bool got = plugin.TryReadBack(out var b, back);
            int below = g.Read(out var x, out _, out _) > 0 ? x.Count(p => p.Y < -0.01f) : -1;
            Console.WriteLine($"  the channel: {read} of 60 frames read whole, the renderer reports frame {b.Frame}, owner {b.Owner}, {b.Particles} particles, emitter {back[0]} has {back[1]}; {below} below the floor");
            Check(read == 60 && got && b.Frame == 60 && b.Owner == owner && back[0] == 77 && back[1] == 512 && below == 0,
                  "GPU: the plugin's frames reach the renderer whole, its liquid lands on the colliders sent with them, and the counts come back per emitter");
            g.Dispose();
        }

        // ---- timing
        foreach (int target in new[] { 10000, 20000, 50000, 100000 })
        {
            var g = new GpuScene(capacity: 131072);
            g.Add(Tank(2f, 2f, 4f)); // walls high enough that a splash cannot go over them (only a leak counts; 100k climbs to 2.6 m)
            float side = MathF.Cbrt(target) * s;
            var r = VolumeRec(1, new Vector3(-1f + side / 2, side / 2, -1f + side / 2), new Vector3(side / 2), water);
            r.MaxParticles = target;
            g.Emitters.Add(r);
            var gpu = new List<double>(); var cpu = new List<double>();
            for (int f = 0; f < 120; f++)
            {
                g.Frame(surface: true);
                g.Rt.Sim.Finish();
                if (f >= 30) { gpu.Add(g.Rt.Stats.GpuMs); cpu.Add(g.Rt.CpuMs); }
            }
            gpu.Sort(); cpu.Sort();
            int n = g.Read(out var x, out _, out _);
            int outside = 0; for (int i = 0; i < n; i++) if (MathF.Abs(x[i].X) > 1.01f || MathF.Abs(x[i].Z) > 1.01f || x[i].Y < -0.01f) outside++;
            Console.WriteLine($"  {n} particles splashing in a 2 m tank (4 m walls), with the surface every frame: GPU median {gpu[gpu.Count / 2]:0.00} ms, 95th {gpu[(int)(gpu.Count * 0.95)]:0.00} ms; " +
                              $"issuing it {cpu[cpu.Count / 2]:0.00} ms on the CPU; {g.Rt.Stats.Vertices} vertices, {g.Rt.Stats.Bricks} blocks; {outside} outside");
            Check(outside == 0, $"GPU: a violent splash of {n} particles stays in its tank (none out under the walls)");
            if (target == 20000)
            {
                // (generous: 0.8 to 3 ms on the RTX 4070, depending on its clocks, which drop while these tests idle it)
                Check(gpu[gpu.Count / 2] < 8.0, "GPU: 20,000 particles and their surface take a few ms of GPU a frame");
            }
            g.Dispose();
        }
    }
}
