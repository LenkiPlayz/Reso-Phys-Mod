// Offline checks of the live bounce light (GI) on this machine's GPU:
//   sky       no geometry under a uniform sky: every probe sees the sky (1)
//   floor     a grey floor (albedo 0.5) under a uniform sky: looking up 1, looking down 0.5 (the analytic answer)
//   volume    the volume texture holds the probes in Unity's proxy-volume layout; its edge is the plain ambient
//   room      a closed room, red wall left, green wall right, a point light: the walls' colours bounce onto the probes
//   glow      a dark closed room with a glowing orange patch on the floor: light from glowing materials alone
//   spark     a lit room with one tiny, very bright voxel: no flickering blobs across the room, light near it
//   lamp      a black room with one small glowing voxel in mid-air: the probes get its light as it is (the shadow rays), steadily
//   panel     a black room with a 2 x 2 m glowing panel: the probes get its exact light (rays and shadow rays share it, nothing twice)
//   leak      two rooms, a light in one, a thin wall between: the dark room's side of the wall reads no light from the lit room
//             once it reads the volume in front of itself (GiLeak, as Unity's shaders read the volume)
//   shift     re-centring moves the probes with the world
//   time      the full-size grid (32 x 12 x 32 m) over a busy room: GPU time per frame, with and without the shadow rays
//   dotnet run -c Release [-- test ...]
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace Softbody.GI.Tests
{
    static class Program
    {
        delegate bool Scene(Vector3 p, out Vector3 albedo, out Vector3 emission, out Vector3 normal);

        static Device _device;
        static string _hlsl;
        static Dictionary<string, byte[]> _code;
        static int _failed, _passed;

        static int Main(string[] args)
        {
            var which = new HashSet<string>(args.Select(a => a.ToLowerInvariant()));
            bool all = which.Count == 0;
            try
            {
                try { _device = new Device(DriverType.Hardware, DeviceCreationFlags.None, FeatureLevel.Level_11_0); }
                catch { _device = new Device(DriverType.Warp, DeviceCreationFlags.None, FeatureLevel.Level_11_0); Console.WriteLine("(no hardware device: WARP)"); }
                using (var dxgi = _device.QueryInterface<SharpDX.DXGI.Device>())
                    Console.WriteLine($"GPU: {dxgi.Adapter.Description.Description.Trim()} ({_device.FeatureLevel})");
                _hlsl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SoftbodyGI.hlsl"));
                var sw = Stopwatch.StartNew();
                _code = GiCompute.CompileAll(_hlsl);
                Console.WriteLine($"compiled {_code.Count} kernels in {sw.ElapsedMilliseconds} ms");

                if (all || which.Contains("sky")) Sky();
                if (all || which.Contains("floor")) Floor();
                if (all || which.Contains("volume")) VolumeLayout();
                if (all || which.Contains("room")) Room();
                if (all || which.Contains("glow")) Glow();
                if (all || which.Contains("spark")) Spark();
                if (all || which.Contains("lamp")) Lamp();
                if (all || which.Contains("panel")) Panel();
                if (all || which.Contains("leak")) Leak();
                if (all || which.Contains("shift")) Shift();
                if (all || which.Contains("time")) Time();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex);
                _failed++;
            }
            finally { _device?.Dispose(); }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        static void Check(bool ok, string what)
        {
            if (ok) _passed++; else _failed++;
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + what);
        }

        static GiConfig Small() => new GiConfig
        {
            VoxelSize = 0.25f, VoxX = 64, VoxY = 32, VoxZ = 64, ProbeRatio = 2,
            ProbesPerFrame = 32 * 16 * 32, InjectSlices = 64, SlabSlices = 8,
        };

        sealed class Run : IDisposable
        {
            public readonly GiConfig Config;
            public readonly GiCompute Gi;
            public readonly GiPlanner Planner;
            public Vector3 Head;
            public Scene Scene;
            public GiLight[] Lights = new GiLight[0];
            public Vector4[] Sky = GiSky.Uniform(Vector3.One);
            public bool Voxelize = true;
            public GiFramePlan Last;

            public Run(GiConfig c, Vector3 head, Scene scene)
            {
                Config = c; Head = head; Scene = scene;
                Gi = new GiCompute(_device, c, _hlsl, n => _code[n]);
                Planner = new GiPlanner(c);
            }

            public void Frames(int n, int volume = 0)
            {
                for (int f = 0; f < n; f++)
                {
                    var plan = Planner.Next(Head, Voxelize);
                    if (plan.Slab >= 0) Gi.UploadAccum(Slab(Config, plan.Origin, plan.Slab, Scene));
                    Gi.Frame(plan, Lights, Lights.Length, Sky, new[] { new GiWindow(volume, plan.Origin) });
                    Last = plan;
                }
                _device.ImmediateContext.Flush();
            }

            /// <summary>Where the probe nearest a world position sits.</summary>
            public Vector3 ProbeCentre(Vector3 p)
            {
                var c = Config;
                var g = (p - Last.Origin) / c.ProbeSpacing - new Vector3(0.5f);
                return Last.Origin + (new Vector3((float)Math.Round(g.X), (float)Math.Round(g.Y), (float)Math.Round(g.Z)) + new Vector3(0.5f)) * c.ProbeSpacing;
            }

            /// <summary>The probe nearest a world position: (red, green, blue, meta), each (a1x, a1y, a1z, a0).</summary>
            public Vector4[] Probe(Vector4[] probes, Vector3 p)
            {
                var c = Config;
                var g = (p - Last.Origin) / c.ProbeSpacing - new Vector3(0.5f);
                int x = (int)Math.Round(g.X), y = (int)Math.Round(g.Y), z = (int)Math.Round(g.Z);
                if (x < 0 || y < 0 || z < 0 || x >= c.ProbeX || y >= c.ProbeY || z >= c.ProbeZ) throw new ArgumentException("outside the grid: " + p);
                int i = ((z * c.ProbeY + y) * c.ProbeX + x) * 4;
                return new[] { probes[i], probes[i + 1], probes[i + 2], probes[i + 3] };
            }

            public void Dispose() => Gi.Dispose();
        }

        /// <summary>What the voxelize shader would add for one slab: one sample per filled voxel.</summary>
        static uint[] Slab(GiConfig c, Vector3 origin, int slab, Scene scene)
        {
            var a = new uint[c.SlabVoxels * 10];
            if (scene == null) return a;
            int z0 = slab * c.SlabSlices;
            for (int z = 0; z < c.SlabSlices; z++)
                for (int y = 0; y < c.VoxY; y++)
                    for (int x = 0; x < c.VoxX; x++)
                    {
                        var p = origin + (new Vector3(x, y, z0 + z) + new Vector3(0.5f)) * c.VoxelSize;
                        if (!scene(p, out var alb, out var emi, out var n)) continue;
                        int i = ((z * c.VoxY + y) * c.VoxX + x) * 10;
                        a[i] = 1;
                        a[i + 1] = (uint)Math.Round(Clamp01(alb.X) * 255); a[i + 2] = (uint)Math.Round(Clamp01(alb.Y) * 255); a[i + 3] = (uint)Math.Round(Clamp01(alb.Z) * 255);
                        a[i + 4] = (uint)Math.Round(emi.X * 256); a[i + 5] = (uint)Math.Round(emi.Y * 256); a[i + 6] = (uint)Math.Round(emi.Z * 256);
                        a[i + 7] = unchecked((uint)(int)Math.Round(n.X * 64)); a[i + 8] = unchecked((uint)(int)Math.Round(n.Y * 64)); a[i + 9] = unchecked((uint)(int)Math.Round(n.Z * 64));
                    }
            return a;
        }

        static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // the irradiance / pi a probe gives a normal (one channel)
        static float Eval(Vector4 sh, Vector3 n) => sh.X * n.X + sh.Y * n.Y + sh.Z * n.Z + sh.W;
        static Vector3 Eval(Vector4[] probe, Vector3 n) => new Vector3(Eval(probe[0], n), Eval(probe[1], n), Eval(probe[2], n));
        static string F(Vector3 v) => $"({v.X:0.000} {v.Y:0.000} {v.Z:0.000})";

        // ------------------------------------------------------------------------------------------------------------------

        static void Sky()
        {
            Console.WriteLine("sky: no geometry, uniform sky 1");
            using (var r = new Run(Small(), Vector3.Zero, null))
            {
                r.Frames(12);
                var probes = r.Gi.ReadProbes();
                double maxErr = 0; int valid = 0;
                for (int i = 0; i < probes.Length; i += 4)
                {
                    if (probes[i + 3].X < 0.5f) continue;
                    valid++;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        var s = probes[i + ch];
                        maxErr = Math.Max(maxErr, Math.Max(Math.Abs(s.W - 1), Math.Max(Math.Abs(s.X), Math.Max(Math.Abs(s.Y), Math.Abs(s.Z)))));
                    }
                }
                Console.WriteLine($"  {valid} of {probes.Length / 4} probes valid, largest error {maxErr:0.0000}");
                Check(valid == probes.Length / 4, "every probe valid");
                Check(maxErr < 1e-3, "every probe sees exactly the sky");
            }
        }

        static void Floor()
        {
            Console.WriteLine("floor: a grey floor (albedo 0.5) under a uniform sky 1 (analytic: up 1, down 0.5)");
            var c = Small();
            // the head at the grid's middle; the floor is voxel layer 4 (its top 1.25 m above the grid's bottom)
            using (var r = new Run(c, Vector3.Zero, null))
            {
                var origin0 = new GiPlanner(c).Next(Vector3.Zero, false).Origin;
                float floorTop = origin0.Y + 5 * c.VoxelSize;
                r.Scene = (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
                {
                    alb = new Vector3(0.5f); emi = Vector3.Zero; n = Vector3.UnitY;
                    return p.Y < floorTop && p.Y > floorTop - c.VoxelSize;
                };
                r.Frames(150);
                var probes = r.Gi.ReadProbes();
                // probes 0.5 m over the floor near the middle (the grid's sides are 8 m away: rays below 3.6 degrees miss the floor)
                Vector3 up = Vector3.Zero, down = Vector3.Zero, a1 = Vector3.Zero;
                int n0 = 0;
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        var pr = r.Probe(probes, new Vector3(dx * c.ProbeSpacing, floorTop + 0.5f, dz * c.ProbeSpacing));
                        up += Eval(pr, Vector3.UnitY); down += Eval(pr, -Vector3.UnitY);
                        a1 += new Vector3(pr[0].Y, pr[1].Y, pr[2].Y);
                        n0++;
                    }
                up /= n0; down /= n0; a1 /= n0;
                Console.WriteLine($"  up {F(up)}  down {F(down)}  (expected 1 and about 0.52)");
                Check(Math.Abs(up.X - 1f) < 0.05f && Math.Abs(up.Y - 1f) < 0.05f && Math.Abs(up.Z - 1f) < 0.05f, "looking up sees the sky (1)");
                Check(Math.Abs(down.X - 0.52f) < 0.05f && Math.Abs(down.Y - 0.52f) < 0.05f && Math.Abs(down.Z - 0.52f) < 0.05f, "looking down sees the floor lit by the sky (0.5)");
                // probes under the floor are inside: not used
                var under = r.Probe(probes, new Vector3(0f, floorTop - 0.6f, 0f));
                Console.WriteLine($"  a probe under the floor: valid {under[3].X}");
                Check(under[3].X < 0.5f, "a probe under the floor is not used");
            }
        }

        static void VolumeLayout()
        {
            Console.WriteLine("volume: Unity's proxy-volume layout, edges fade to the sky's L1 part (the renderers that read it get no quadratic part)");
            var c = Small();
            using (var r = new Run(c, Vector3.Zero, (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
            {
                // a red box in the middle, so the probes differ
                alb = new Vector3(0.9f, 0.1f, 0.1f); emi = Vector3.Zero; n = Vector3.UnitY;
                return Math.Abs(p.X) < 1f && Math.Abs(p.Z) < 1f && p.Y < -1f && p.Y > -2f;
            }))
            {
                r.Sky = new[]
                {
                    new Vector4(0.0f, 0.2f, 0.0f, 0.8f), new Vector4(0.0f, 0.2f, 0.0f, 0.9f), new Vector4(0.0f, 0.3f, 0.0f, 1.2f),
                    new Vector4(0f, 0f, 0.15f, 0f), new Vector4(0f, 0f, 0.15f, 0f), new Vector4(0f, 0f, 0.15f, 0f), new Vector4(0f, 0f, 0f, 1f),
                };
                r.Frames(30, volume: 1);
                var probes = r.Gi.ReadProbes();
                var vol = r.Gi.ReadVolume(1);
                int W = c.ProbeX, H = c.ProbeY;
                Func<int, int, int, Vector4> texel = (x, y, z) => { int i = ((z * H + y) * 4 * W + x) * 4; return new Vector4(vol[i], vol[i + 1], vol[i + 2], vol[i + 3]); };
                // an inner probe: SHAr = the probe's red as it is (the renderers that read the volume get no quadratic part)
                int px = W / 2, py = H / 2 - 4, pz = c.ProbeZ / 2 + 3;
                int pi = ((pz * H + py) * W + px) * 4;
                var t = texel(px, py, pz);
                var want = probes[pi];
                double err = (t - want).Length();
                var tg = texel(px + W, py, pz); var wg = probes[pi + 1];
                var tb = texel(px + 2 * W, py, pz); var wb = probes[pi + 2];
                var occ = texel(px + 3 * W, py, pz);
                err = Math.Max(err, Math.Max((tg - wg).Length(), (tb - wb).Length()));
                Console.WriteLine($"  inner texel r {t}  probe r {probes[pi]}  (largest difference {err:0.0000})");
                Check(err < 2e-3, "inner texels are the probes");
                Check(occ == Vector4.One, "occlusion is 1");
                var edge = texel(0, py, pz);
                // the sky's L1 part: unity_SHAr with the quadratic part's constant share (0.15 / 3) back in its constant
                var skyL1 = r.Sky[0] + new Vector4(0f, 0f, 0f, r.Sky[3].Z / 3f);
                Console.WriteLine($"  edge texel r {edge}  sky to L1 {skyL1}");
                Check((edge - skyL1).Length() < 2e-3, "the outermost texels are the sky's L1 part");
            }
        }

        static readonly Vector3 White = new Vector3(0.725f, 0.71f, 0.68f), Red = new Vector3(0.63f, 0.065f, 0.05f), Green = new Vector3(0.14f, 0.45f, 0.091f);

        // a closed box, inside x and z -2..2, y 0..4 (around 'at'), walls a voxel thick just outside
        static Scene Box(Vector3 at, float vs, Vector3 left, Vector3 right, Vector3 others, Func<Vector3, Vector3> emission = null) =>
            (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
            {
                var q = p - at;
                alb = others; emi = Vector3.Zero; n = Vector3.UnitY;
                bool inX = q.X > -2f - vs && q.X < 2f + vs, inY = q.Y > -vs && q.Y < 4f + vs, inZ = q.Z > -2f - vs && q.Z < 2f + vs;
                if (!(inX && inY && inZ)) return false;
                if (q.X < -2f) { alb = left; n = Vector3.UnitX; return true; }
                if (q.X > 2f) { alb = right; n = -Vector3.UnitX; return true; }
                if (q.Y < 0f) { n = Vector3.UnitY; if (emission != null) emi = emission(q); return true; }
                if (q.Y > 4f) { n = -Vector3.UnitY; return true; }
                if (q.Z < -2f) { n = Vector3.UnitZ; return true; }
                if (q.Z > 2f) { n = -Vector3.UnitZ; return true; }
                return false;
            };

        static void Room()
        {
            Console.WriteLine("room: closed box, red wall left, green right, a point light under the ceiling");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            using (var r = new Run(c, Vector3.Zero, Box(at, c.VoxelSize, Red, Green, White)))
            {
                r.Lights = new[] { GiLight.Point(at + new Vector3(0f, 3.6f, 0f), 8f, new Vector3(6f), true) };
                r.Sky = GiSky.Uniform(new Vector3(0.3f, 0.4f, 0.6f));   // outside only: the closed room must not see it
                r.Frames(200);
                var probes = r.Gi.ReadProbes();
                var nearRed = Eval(r.Probe(probes, at + new Vector3(-1.5f, 2f, 0f)), -Vector3.UnitX);
                var nearGreen = Eval(r.Probe(probes, at + new Vector3(1.5f, 2f, 0f)), Vector3.UnitX);
                var middleUp = Eval(r.Probe(probes, at + new Vector3(0f, 2f, 0f)), Vector3.UnitY);
                var middleDown = Eval(r.Probe(probes, at + new Vector3(0f, 2f, 0f)), -Vector3.UnitY);
                Console.WriteLine($"  facing the red wall {F(nearRed)}  facing the green wall {F(nearGreen)}");
                Console.WriteLine($"  middle, looking up {F(middleUp)}  looking down {F(middleDown)}");
                Check(nearRed.X > 1.5f * nearRed.Y, "the red wall bounces red");
                Check(nearGreen.Y > 1.5f * nearGreen.X, "the green wall bounces green");
                Check(middleDown.X > middleUp.X * 0.5f && middleDown.X > 0.05f, "the lit floor bounces light up");
                // the sky (blue) stays out: the walls' colours, not the sky's
                Check(middleUp.Z < middleUp.X * 1.3f, "the closed room does not see the blue sky");

                NoBlackSide(r, probes);
            }
        }

        /// <summary>No side of anything goes black: the volume keeps every texel's darkest direction at a fifth of its mean or more
        /// (L1 rings: a probe lit hard from one side works out to less than nothing on the other). Returns the probes' own worst.</summary>
        static double NoBlackSide(Run r, Vector4[] probes)
        {
            var c = r.Config;
            var vol = r.Gi.ReadVolume(0);
            int W = c.ProbeX, H = c.ProbeY;
            double raw = 0, kept = 0;
            for (int i = 0; i < probes.Length; i += 4)
                if (probes[i + 3].X > 0.5f)
                    for (int ch = 0; ch < 3; ch++)
                    {
                        var s = probes[i + ch];
                        if (s.W > 1e-3f) raw = Math.Max(raw, new Vector3(s.X, s.Y, s.Z).Length() / s.W);
                    }
            for (int z = 0; z < c.ProbeZ; z++)
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int ti = ((z * H + y) * 4 * W + x + ch * W) * 4;
                            var s = new Vector4(vol[ti], vol[ti + 1], vol[ti + 2], vol[ti + 3]);
                            if (s.W > 1e-3f) kept = Math.Max(kept, new Vector3(s.X, s.Y, s.Z).Length() / s.W);
                        }
            Console.WriteLine($"  strongest direction against the mean: {raw:0.00} in the probes, {kept:0.00} in the volume (0.8 at most)");
            Check(kept <= 0.81, "no side goes black: every texel's darkest side keeps a fifth of its mean");
            return raw;
        }

        static void Glow()
        {
            Console.WriteLine("glow: a dark closed room, a glowing orange patch (1 x 1 m) on the floor, no lights");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            Func<Vector3, Vector3> glow = q => Math.Abs(q.X) < 0.5f && Math.Abs(q.Z) < 0.5f ? new Vector3(4f, 2f, 0.5f) : Vector3.Zero;
            using (var r = new Run(c, Vector3.Zero, Box(at, c.VoxelSize, White * 0.7f, White * 0.7f, White * 0.7f, glow)))
            {
                r.Sky = GiSky.Uniform(Vector3.Zero);
                r.Frames(150);
                var probes = r.Gi.ReadProbes();
                var near = Eval(r.Probe(probes, at + new Vector3(0f, 0.75f, 0f)), -Vector3.UnitY);
                var far = Eval(r.Probe(probes, at + new Vector3(1.5f, 3.25f, 1.5f)), -Vector3.UnitY);
                Console.WriteLine($"  over the patch, looking down {F(near)}  in a far corner {F(far)}");
                Check(near.X > 0.3f, "the patch lights what is over it");
                Check(near.X > 3f * far.X && far.X > 0f, "the light falls off with distance but reaches the corners");
                Check(near.X > near.Y * 1.5f && near.Y > near.Z * 2f, "it stays orange");
                double raw = NoBlackSide(r, probes);
                Check(raw > 0.85, "(this room needs it: some probes are lit hard from one side)");
            }
        }

        static void Spark()
        {
            Console.WriteLine("spark: a lit white room, one tiny voxel glowing very bright (50) in a floor corner: no flicker across the room");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            Func<Vector3, Vector3> spark = q => Math.Abs(q.X - 1.625f) < 0.125f && Math.Abs(q.Z - 1.625f) < 0.125f ? new Vector3(50f, 8f, 1f) : Vector3.Zero;
            Vector3 farAt = at + new Vector3(-1.25f, 2.25f, -1.25f), nearAt = at + new Vector3(1.75f, 0.25f, 1.75f);
            float[] Track(Func<Vector3, Vector3> glow, out float nearRed)
            {
                using (var r = new Run(c, Vector3.Zero, Box(at, c.VoxelSize, White, White, White, glow)))
                {
                    r.Lights = new[] { GiLight.Point(at + new Vector3(0f, 3.6f, 0f), 8f, new Vector3(6f), true) };
                    r.Frames(200);
                    var reds = new float[60];
                    for (int f = 0; f < reds.Length; f++) { r.Frames(1); reds[f] = r.Probe(r.Gi.ReadProbes(), farAt)[0].W; }
                    nearRed = r.Probe(r.Gi.ReadProbes(), nearAt)[0].W;
                    return reds;
                }
            }
            var plain = Track(null, out float nearPlain);
            var lit = Track(spark, out float nearLit);
            float mean = lit.Average(), flicker = (lit.Max() - lit.Min()) / mean;
            Console.WriteLine($"  across the room, red over 60 frames: {lit.Min():0.000} to {lit.Max():0.000} (mean {mean:0.000}; without the spark {plain.Average():0.000}); by the spark {nearLit:0.000} (without {nearPlain:0.000})");
            Check(flicker < 0.1f, $"no flicker across the room ({flicker:P0} of its light)");
            Check(mean < 1.25f * plain.Average(), "the spark does not light the far side much");
            Check(nearLit > 1.3f * nearPlain, "the spark lights what is next to it");
        }

        static void Lamp()
        {
            Console.WriteLine("lamp: a black closed room, one small glowing voxel (50, 30, 10) in mid-air, no lights: each probe gets its light as it is");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            var lamp = at + new Vector3(0.125f, 2.125f, 0.125f);   // a voxel's centre
            var glow = new Vector3(50f, 30f, 10f);
            var room = Box(at, c.VoxelSize, Vector3.Zero, Vector3.Zero, Vector3.Zero);
            Scene scene = (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
            {
                if (Vector3.Distance(p, lamp) < 0.01f) { alb = Vector3.Zero; emi = glow; n = Vector3.Zero; return true; }   // (no normal: a cube)
                return room(p, out alb, out emi, out n);
            };
            var spots = new[] { lamp + new Vector3(-1.5f, 0.1f, 0.2f), lamp + new Vector3(1.1f, -1.4f, 0.9f), lamp + new Vector3(0.3f, 1.2f, -1.6f) };
            foreach (int rays in new[] { c.GlowRays, 0 })
                using (var r = new Run(c, Vector3.Zero, scene))
                {
                    r.Sky = GiSky.Uniform(Vector3.Zero);
                    r.Gi.GlowRays = rays;
                    r.Frames(150);
                    var track = new float[60];
                    for (int f = 0; f < track.Length; f++) { r.Frames(1); track[f] = r.Probe(r.Gi.ReadProbes(), spots[0])[0].W; }
                    var probes = r.Gi.ReadProbes();
                    double worst = 0, worstWay = 1;
                    var line = new System.Text.StringBuilder();
                    foreach (var s in spots)
                    {
                        var w = lamp - r.ProbeCentre(s);
                        float d2 = w.LengthSquared();
                        w /= (float)Math.Sqrt(d2);
                        // the cube's outline over the distance squared (what a ray or a shadow ray sees of it)
                        float omega = c.VoxelSize * c.VoxelSize * (Math.Abs(w.X) + Math.Abs(w.Y) + Math.Abs(w.Z)) / d2;
                        var want = glow * (omega / (4f * (float)Math.PI));
                        var pr = r.Probe(probes, s);
                        var got = new Vector3(pr[0].W, pr[1].W, pr[2].W);
                        worst = Math.Max(worst, Math.Abs(got.X / want.X - 1));
                        var a1 = new Vector3(pr[0].X, pr[0].Y, pr[0].Z);
                        worstWay = Math.Min(worstWay, a1.Length() > 0 ? Vector3.Dot(Vector3.Normalize(a1), w) : 0);
                        line.Append($" {F(got)} (exact {F(want)})");
                    }
                    float mean = track.Average(), flicker = mean > 0 ? (track.Max() - track.Min()) / mean : 1f;
                    Console.WriteLine($"  {rays} shadow rays:{line}; flicker {flicker:P0}, its way {worstWay:0.00}");
                    if (rays > 0)
                    {
                        Check(worst < 0.12, $"each probe gets the lamp's light within 12% ({worst:P0} off at worst)");
                        Check(worstWay > 0.95, "from the lamp's direction");
                        Check(flicker < 0.08f, $"steadily ({flicker:P0} over 60 frames)");
                    }
                }
        }

        // the solid angle of the rectangle [x1, x2] x [y1, y2] in a plane d away, seen from the point over the plane's (0, 0)
        static double RectSolidAngle(double x1, double x2, double y1, double y2, double d)
        {
            double Corner(double x, double y) => Math.Atan(x * y / (d * Math.Sqrt(x * x + y * y + d * d)));
            return Corner(x2, y2) - Corner(x1, y2) - Corner(x2, y1) + Corner(x1, y1);
        }

        static void Panel()
        {
            Console.WriteLine("panel: a black closed room, a 2 x 2 m glowing panel (3, 2, 1) on one wall, no lights: each probe gets the panel's exact light");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            var glow = new Vector3(3f, 2f, 1f);
            var room = Box(at, c.VoxelSize, Vector3.Zero, Vector3.Zero, Vector3.Zero);
            // the panel: x -1..1, y 1..3 of the wall behind z = 2 (its face at z = 2, turned to -z)
            Scene scene = (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
            {
                bool hit = room(p, out alb, out emi, out n);
                var q = p - at;
                if (hit && q.Z > 2f && Math.Abs(q.X) < 1f && q.Y > 1f && q.Y < 3f) emi = glow;
                return hit;
            };
            var spots = new[] { at + new Vector3(0.25f, 2.25f, 1.25f), at + new Vector3(-0.75f, 1.75f, 0.25f), at + new Vector3(1.25f, 2.75f, -1.25f), at + new Vector3(-1.25f, 0.75f, -1.75f) };
            foreach (int rays in new[] { c.GlowRays, 0 })
                using (var r = new Run(c, Vector3.Zero, scene))
                {
                    r.Sky = GiSky.Uniform(Vector3.Zero);
                    r.Gi.GlowRays = rays;
                    r.Frames(200);
                    var mean = new Vector3[spots.Length];   // over 40 more frames (the probes' blend keeps a little noise)
                    for (int f = 0; f < 40; f++)
                    {
                        r.Frames(1);
                        var pr = r.Gi.ReadProbes();
                        for (int k = 0; k < spots.Length; k++) { var p4 = r.Probe(pr, spots[k]); mean[k] += new Vector3(p4[0].W, p4[1].W, p4[2].W) / 40f; }
                    }
                    var line = new System.Text.StringBuilder();
                    double worstNear = 0, worstAll = 0;
                    for (int k = 0; k < spots.Length; k++)
                    {
                        var pc = r.ProbeCentre(spots[k]) - at;
                        double d = 2.0 - pc.Z;   // to the panel's plane
                        double om = RectSolidAngle(-1 - pc.X, 1 - pc.X, 1 - pc.Y, 3 - pc.Y, d);
                        var want = glow * (float)(om / (4 * Math.PI));
                        double err = Math.Abs(mean[k].X / want.X - 1);
                        worstAll = Math.Max(worstAll, err);
                        if (d < 1.5) worstNear = Math.Max(worstNear, err);
                        line.Append($" {F(mean[k])} (exact {F(want)}, {d:0.##} m away)");
                    }
                    Console.WriteLine($"  {rays} shadow rays:{line}");
                    if (rays > 0) Check(worstAll < 0.1, $"with shadow rays every probe gets the panel's light within 10% ({worstAll:P0} off at worst)");
                    else Check(worstNear < 0.1, $"the rays alone agree near the panel ({worstNear:P0} off; farther the firefly cap holds them back)");
                }
        }

        /// <summary>What a lit shader of the game reads from a volume (ReadVolume) at <paramref name="p"/> for a normal
        /// <paramref name="n"/>, as Unity's SHEvalLinearL0L1_SampleProbeVolume does (trilinear, x kept half a texel inside, the
        /// volume's corner at <paramref name="min"/>): irradiance / pi.</summary>
        static Vector3 Lookup(GiConfig c, float[] vol, Vector3 min, Vector3 p, Vector3 n)
        {
            int W = c.ProbeX, H = c.ProbeY, D = c.ProbeZ;
            var t = (p - min) / c.Size;
            float fx = Math.Clamp(t.X * W - 0.5f, 0f, W - 1f), fy = Math.Clamp(t.Y * H - 0.5f, 0f, H - 1f), fz = Math.Clamp(t.Z * D - 0.5f, 0f, D - 1f);
            int x0 = Math.Min((int)fx, W - 2), y0 = Math.Min((int)fy, H - 2), z0 = Math.Min((int)fz, D - 2);
            float ax = fx - x0, ay = fy - y0, az = fz - z0;
            var e = new float[3];
            for (int ch = 0; ch < 3; ch++)
            {
                var s = Vector4.Zero;
                for (int k = 0; k < 8; k++)
                {
                    int dx = k & 1, dy = (k >> 1) & 1, dz = k >> 2;
                    float w = (dx == 1 ? ax : 1 - ax) * (dy == 1 ? ay : 1 - ay) * (dz == 1 ? az : 1 - az);
                    int i = (((z0 + dz) * H + (y0 + dy)) * 4 * W + (x0 + dx) + ch * W) * 4;
                    s += new Vector4(vol[i], vol[i + 1], vol[i + 2], vol[i + 3]) * w;
                }
                e[ch] = Math.Max(0f, s.X * n.X + s.Y * n.Y + s.Z * n.Z + s.W);
            }
            return new Vector3(e[0], e[1], e[2]);
        }

        // two rooms side by side, inside y 0..4 and z -2..2 around 'at': A at x -4.5..-0.5, B at x 0..4, the wall between two voxels
        // thick (each layer turned to its own room); the other walls a voxel thick just outside
        static Scene TwoRooms(Vector3 at, float vs) => (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
        {
            var q = p - at;
            alb = White; emi = Vector3.Zero; n = Vector3.UnitY;
            bool inX = q.X > -4.5f - vs && q.X < 4f + vs, inY = q.Y > -vs && q.Y < 4f + vs, inZ = q.Z > -2f - vs && q.Z < 2f + vs;
            if (!(inX && inY && inZ)) return false;
            if (q.X > -0.5f && q.X < 0f) { n = q.X < -0.25f ? -Vector3.UnitX : Vector3.UnitX; return true; }
            if (q.X < -4.5f) { n = Vector3.UnitX; return true; }
            if (q.X > 4f) { n = -Vector3.UnitX; return true; }
            if (q.Y < 0f) { n = Vector3.UnitY; return true; }
            if (q.Y > 4f) { n = -Vector3.UnitY; return true; }
            if (q.Z < -2f) { n = Vector3.UnitZ; return true; }
            if (q.Z > 2f) { n = -Vector3.UnitZ; return true; }
            return false;
        };

        static void Leak()
        {
            Console.WriteLine("leak: two rooms, a light in A only, a wall 0.5 m thick between: what the wall's faces read from the volume, as Unity reads it");
            var cfg = new GiConfig();
            Check(GiLeak.ThinAxes(new Vector3(0.5f, 4f, 4f), cfg) == 1 && GiLeak.ThinAxes(new Vector3(8f, 0.25f, 4f), cfg) == 2
                  && GiLeak.ThinAxes(new Vector3(0.3f, 0.3f, 3f), cfg) == 3 && GiLeak.ThinAxes(new Vector3(0.4f, 0.4f, 0.4f), cfg) == 0
                  && GiLeak.ThinAxes(new Vector3(8f, 4f, 4f), cfg) == 0, "thin: walls, floors and beams; not small things, rooms or blocks");
            var mid = new Vector3(-0.25f, 0.5f, 0f);
            Check(GiLeak.Offset(GiLeak.Code(1, mid, new Vector3(2f, 0f, 0f)), cfg.LeakOffset) == new Vector3(0.5f, 0f, 0f)
                  && GiLeak.Offset(GiLeak.Code(1, mid, new Vector3(-2f, 0f, 0f)), cfg.LeakOffset) == new Vector3(-0.5f, 0f, 0f)
                  && GiLeak.Offset(GiLeak.Code(6, mid, new Vector3(1f, -3f, 5f)), 0.5f) == new Vector3(0f, -0.5f, 0.5f)
                  && GiLeak.Offset(GiLeak.None, 0.5f) == Vector3.Zero, "the offset goes toward the head, along the thin axes only");

            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            using (var r = new Run(c, Vector3.Zero, TwoRooms(at, c.VoxelSize)))
            {
                r.Lights = new[] { GiLight.Point(at + new Vector3(-2.5f, 3.5f, 0f), 8f, new Vector3(6f), true) };
                r.Sky = GiSky.Uniform(Vector3.Zero);
                r.Frames(200);
                var vol = r.Gi.ReadVolume(0);
                var min = r.Last.Origin;
                // the wall's bounds: x -0.5..0, the rooms' height and depth and a voxel more
                int axes = GiLeak.ThinAxes(new Vector3(0.5f, 4.5f, 4.5f), c);
                var wallMid = at + new Vector3(-0.25f, 2f, 0f);
                var toB = GiLeak.Offset(GiLeak.Code(axes, wallMid, at + new Vector3(2f, 1.5f, 0f)), c.LeakOffset);
                var toA = GiLeak.Offset(GiLeak.Code(axes, wallMid, at + new Vector3(-2.5f, 1.5f, 0f)), c.LeakOffset);
                Vector3 bPlain = Vector3.Zero, bMoved = Vector3.Zero, aPlain = Vector3.Zero, aMoved = Vector3.Zero, bRoom = Vector3.Zero;
                int n = 0;
                foreach (float y in new[] { 0.6f, 1.4f, 2.2f, 3.1f })
                    foreach (float z in new[] { -1.1f, 0.3f, 1.2f })
                    {
                        var pb = at + new Vector3(0f, y, z);      // B's face of the wall
                        var pa = at + new Vector3(-0.5f, y, z);   // A's face
                        bPlain += Lookup(c, vol, min, pb, Vector3.UnitX);
                        bMoved += Lookup(c, vol, min, pb + toB, Vector3.UnitX);
                        aPlain += Lookup(c, vol, min, pa, -Vector3.UnitX);
                        aMoved += Lookup(c, vol, min, pa + toA, -Vector3.UnitX);
                        bRoom += Lookup(c, vol, min, pb + new Vector3(1.5f, 0f, 0f), Vector3.UnitX);
                        n++;
                    }
                bPlain /= n; bMoved /= n; aPlain /= n; aMoved /= n; bRoom /= n;
                Console.WriteLine($"  the dark room's side: plain {F(bPlain)}, read in front {F(bMoved)} (the dark room 1.5 m out: {F(bRoom)})");
                Console.WriteLine($"  the lit room's side: plain {F(aPlain)}, read in front {F(aMoved)}");
                Check(bPlain.X > 0.02f, "(the plain lookup leaks here: the test shows the problem)");
                Check(bMoved.X < 0.15f * bPlain.X, "read in front of itself, the dark side takes (almost) none of the lit room's light");
                Check(aMoved.X >= 0.95f * aPlain.X, "and the lit side keeps its light");
            }
        }

        static void Shift()
        {
            Console.WriteLine("shift: re-centring by 5 m moves the probes with the world");
            var c = Small();
            var at = new Vector3(0f, -1.5f, 0f);
            using (var r = new Run(c, Vector3.Zero, Box(at, c.VoxelSize, Red, Green, White)))
            {
                r.Lights = new[] { GiLight.Point(at + new Vector3(0f, 3.6f, 0f), 8f, new Vector3(6f), true) };
                r.Frames(150);
                var before = r.Gi.ReadProbes();
                var spots = new[] { at + new Vector3(-1.5f, 2f, 0f), at + new Vector3(1.5f, 2f, 0f), at + new Vector3(0f, 1f, 1f) };
                var was = spots.Select(s => Eval(r.Probe(before, s), Vector3.UnitY)).ToArray();
                var oldOrigin = r.Last.Origin;
                r.Head = new Vector3(5f, 0f, 0f);
                r.Voxelize = false;   // the room is in both grids; nothing new is drawn this frame
                r.Frames(1);
                Console.WriteLine($"  shifted by ({r.Last.ShiftX} {r.Last.ShiftY} {r.Last.ShiftZ}) probes; origin {oldOrigin} -> {r.Last.Origin}");
                Check(r.Last.ShiftX == 10 && r.Last.ShiftY == 0 && r.Last.ShiftZ == 0, "re-centred by 10 probes in x");
                var after = r.Gi.ReadProbes();
                double worst = 0;
                for (int i = 0; i < spots.Length; i++)
                {
                    var now = Eval(r.Probe(after, spots[i]), Vector3.UnitY);
                    worst = Math.Max(worst, (now - was[i]).Length() / Math.Max(1e-3f, was[i].Length()));
                    Console.WriteLine($"  at {spots[i]}: before {F(was[i])} after {F(now)}");
                }
                Check(worst < 0.15, "the probes kept their light where they are in the world");
            }
        }

        static void Time()
        {
            var c = new GiConfig();   // the defaults the fork uses
            Console.WriteLine($"time: {c.VoxX}x{c.VoxY}x{c.VoxZ} voxels of {c.VoxelSize} m ({c.Size.X}x{c.Size.Y}x{c.Size.Z} m), {c.ProbeX}x{c.ProbeY}x{c.ProbeZ} probes, " +
                              $"{c.ProbesPerFrame} traced and {c.InjectSlices} slices lit a frame, a slab of {c.SlabSlices} voxelized a frame");
            var rng = new Random(7);
            var boxes = new List<(Vector3 min, Vector3 max, Vector3 alb, Vector3 emi)>();
            for (int i = 0; i < 60; i++)
            {
                var p = new Vector3((float)rng.NextDouble() * 28f - 14f, -1.6f + (float)rng.NextDouble() * 3f, (float)rng.NextDouble() * 28f - 14f);
                var s = new Vector3(0.3f + (float)rng.NextDouble() * 2f, 0.3f + (float)rng.NextDouble() * 2f, 0.3f + (float)rng.NextDouble() * 2f);
                var a = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble()) * 0.8f;
                var e = i % 10 == 0 ? new Vector3(2f, 1f, 3f) : Vector3.Zero;
                boxes.Add((p - s * 0.5f, p + s * 0.5f, a, e));
            }
            Scene scene = (Vector3 p, out Vector3 alb, out Vector3 emi, out Vector3 n) =>
            {
                alb = White; emi = Vector3.Zero; n = Vector3.UnitY;
                if (p.Y < -1.6f && p.Y > -1.85f) return true;                                   // floor
                if (p.Y > 4.4f && p.Y < 4.65f && Math.Abs(p.X) < 12f) { n = -Vector3.UnitY; return true; }   // a roof over most of it
                if (Math.Abs(p.X) > 15.5f && Math.Abs(p.X) < 15.75f && p.Y < 4.5f) { n = new Vector3(-Math.Sign(p.X), 0, 0); return true; }
                foreach (var b in boxes)
                    if (p.X > b.min.X && p.Y > b.min.Y && p.Z > b.min.Z && p.X < b.max.X && p.Y < b.max.Y && p.Z < b.max.Z)
                    {
                        var d = Vector3.Min(p - b.min, b.max - p);
                        n = d.X < d.Y && d.X < d.Z ? new Vector3(p.X - (b.min.X + b.max.X) * 0.5f > 0 ? 1 : -1, 0, 0)
                          : d.Y < d.Z ? new Vector3(0, p.Y - (b.min.Y + b.max.Y) * 0.5f > 0 ? 1 : -1, 0)
                          : new Vector3(0, 0, p.Z - (b.min.Z + b.max.Z) * 0.5f > 0 ? 1 : -1);
                        alb = b.alb; emi = b.emi;
                        return true;
                    }
                return false;
            };
            using (var r = new Run(c, Vector3.Zero, scene))
            {
                var lights = new List<GiLight> { GiLight.Directional(new Vector3(0.3f, -1f, 0.4f), new Vector3(2.5f, 2.3f, 2f), true) };
                for (int i = 0; i < 12; i++)
                    lights.Add(GiLight.Point(new Vector3(i * 2.4f - 13f, 3.5f, (i % 3) * 8f - 8f), 6f, new Vector3(3f, 2.5f, 2f), i % 2 == 0));
                lights.Add(GiLight.SpotLight(new Vector3(0f, 4f, 0f), -Vector3.UnitY, 10f, 60f, new Vector3(5f), true));
                r.Lights = lights.ToArray();
                r.Sky = GiSky.Uniform(new Vector3(0.25f, 0.3f, 0.4f));
                // the slabs come from the CPU here (building them is the test's cost, not the GPU's): first the whole grid once
                var uploadWatch = new Stopwatch();
                r.Frames(c.Slabs);
                _device.ImmediateContext.Flush();
                r.Voxelize = true;
                var slabs = new uint[c.Slabs][];
                var glowing = r.Gi.ReadGlowList()[0];
                double median = -1;
                foreach (int rays in new[] { c.GlowRays, 0 })
                {
                    r.Gi.GlowRays = rays;
                    var cpu = new Stopwatch();
                    const int frames = 240;
                    var gpu = new List<double>();
                    for (int f = 0; f < frames; f++)
                    {
                        var plan = r.Planner.Next(r.Head, true);
                        if (plan.Slab >= 0) r.Gi.UploadAccum(slabs[plan.Slab] ?? (slabs[plan.Slab] = Slab(c, plan.Origin, plan.Slab, scene)));
                        cpu.Start();
                        r.Gi.Frame(plan, r.Lights, r.Lights.Length, r.Sky, new[] { new GiWindow(0, plan.Origin) });
                        cpu.Stop();
                        r.Last = plan;
                        _device.ImmediateContext.Flush();
                        if (f > 8 && r.Gi.Stats.LastGpuMs > 0) gpu.Add(r.Gi.Stats.LastGpuMs);
                        System.Threading.Thread.Sleep(4);   // let the timestamps come back
                    }
                    gpu.Sort();
                    double med = gpu.Count > 0 ? gpu[gpu.Count / 2] : -1, p95 = gpu.Count > 0 ? gpu[(int)(gpu.Count * 0.95)] : -1;
                    if (rays == c.GlowRays) median = med;
                    Console.WriteLine($"  {rays} shadow rays ({glowing} glowing bricks): GPU per frame median {med:0.000} ms, 95th percentile {p95:0.000} ms ({gpu.Count} timed frames); " +
                                      $"render-thread CPU to issue it: {cpu.Elapsed.TotalMilliseconds / frames:0.000} ms a frame");
                }
                Console.WriteLine($"  every probe is traced every {(double)c.Probes / c.ProbesPerFrame:0.#} frames, every voxel lit every {(double)c.VoxZ / c.InjectSlices:0.#}, voxelized every {c.Slabs}");
                Check(median > 0 && median < 4.0, "under 4 ms of GPU a frame");
            }
        }
    }
}
