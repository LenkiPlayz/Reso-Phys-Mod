// Live bounce light (GI) for the renderer fork: what the GPU side (GiCompute) and the Unity side share, with no Unity and no
// Direct3D in it (netstandard2.0, System.Numerics), so the offline tests drive it the way the fork does.
//
// The grid: VoxX x VoxY x VoxZ voxels of VoxelSize around the head, and a probe every ProbeRatio voxels over the same box (the
// probes are the volume Resonite's lit shaders read per pixel). GiPlanner decides each frame's share of the work: re-centring
// (in whole probes), which slab of voxels the voxelize shader redraws, which voxels are lit again, which probes trace rays.
#nullable disable
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Softbody.GI
{
    public sealed class GiConfig
    {
        /// <summary>A voxel's edge (m).</summary>
        public float VoxelSize = 0.25f;
        /// <summary>Voxels along x, y, z: multiples of 4 (bricks) and of ProbeRatio.</summary>
        public int VoxX = 128, VoxY = 48, VoxZ = 128;
        /// <summary>Probe spacing in voxels.</summary>
        public int ProbeRatio = 2;
        /// <summary>Probes that trace their rays each frame (32 rays each).</summary>
        public int ProbesPerFrame = 4096;
        /// <summary>Voxel slices (z) lit again each frame.</summary>
        public int InjectSlices = 16;
        /// <summary>Voxel slices (z) the voxelize shader draws each frame: a multiple of 4.</summary>
        public int SlabSlices = 8;
        /// <summary>The least share of a new result a probe takes (it follows a big change faster).</summary>
        public float MinBlend = 0.15f;
        /// <summary>How much glowing materials light their surroundings (1: as they glow).</summary>
        public float EmissionScale = 1f;
        /// <summary>A probe that sees more than this share of back faces is inside something and is not used.</summary>
        public float BackFaceShare = 0.3f;
        /// <summary>How far a probe's rays go (m); past the grid they see the sky.</summary>
        public float RayLength = 100f;
        /// <summary>Probes at the volume's edge that fade to the world's plain ambient.</summary>
        public int EdgeFade = 2;
        public int MaxLights = 64;
        /// <summary>Shadow rays a probe aims at glowing voxels each time it traces (0 to 8; 0: only its 32 rays see the glow).</summary>
        public int GlowRays = 4;
        /// <summary>The most glowing 4 x 4 x 4 bricks a probe weighs when it picks where to aim them (a random run of them when more
        /// glow).</summary>
        public int GlowBricks = 1024;
        /// <summary>How far in front of itself a thin renderer (a wall, a floor) reads the bounce light, toward the head (m; 0: off).
        /// A probe spacing puts both probe layers it reads on the head's side (GiLeak).</summary>
        public float LeakOffset = 0.5f;
        /// <summary>A renderer is thin along a world axis its bounds span at most this much (m) ...</summary>
        public float ThinMax = 0.6f;
        /// <summary>... when they span at least this along another (m): smaller things keep the plain lookup.</summary>
        public float LongMin = 1f;

        public float ProbeSpacing => VoxelSize * ProbeRatio;
        public int ProbeX => VoxX / ProbeRatio;
        public int ProbeY => VoxY / ProbeRatio;
        public int ProbeZ => VoxZ / ProbeRatio;
        public int Voxels => VoxX * VoxY * VoxZ;
        public int Probes => ProbeX * ProbeY * ProbeZ;
        public int Slabs => VoxZ / SlabSlices;
        public int SlabVoxels => VoxX * VoxY * SlabSlices;
        public Vector3 Size => new Vector3(VoxX, VoxY, VoxZ) * VoxelSize;

        public GiConfig Clone() => (GiConfig)MemberwiseClone();

        public void Validate()
        {
            if (!(VoxelSize > 0.01f && VoxelSize < 10f)) throw new ArgumentException("VoxelSize out of range");
            if (ProbeRatio < 1 || ProbeRatio > 8) throw new ArgumentException("ProbeRatio out of range");
            foreach (int d in new[] { VoxX, VoxY, VoxZ })
                if (d < 8 || d > 512 || d % 4 != 0 || d % ProbeRatio != 0) throw new ArgumentException("voxel counts must be 8 to 512, multiples of 4 and of ProbeRatio");
            if (SlabSlices < 4 || SlabSlices % 4 != 0 || VoxZ % SlabSlices != 0) throw new ArgumentException("SlabSlices must be a multiple of 4 that divides VoxZ");
            if ((long)Voxels > 64L * 65535) throw new ArgumentException("too many voxels for one dispatch");
            if ((long)SlabVoxels * 10 > 64L * 65535) throw new ArgumentException("the slab is too big");
            if (ProbesPerFrame < 1 || ProbesPerFrame > 65535) throw new ArgumentException("ProbesPerFrame out of range");
            if (InjectSlices < 1) throw new ArgumentException("InjectSlices out of range");
            if (MaxLights < 1 || MaxLights > 1024) throw new ArgumentException("MaxLights out of range");
            if (GlowRays < 0 || GlowRays > 8) throw new ArgumentException("GlowRays must be 0 to 8");
            if (GlowBricks < 32 || GlowBricks > 1 << 20) throw new ArgumentException("GlowBricks out of range");
            if (!(LeakOffset >= 0f && LeakOffset <= 4f)) throw new ArgumentException("LeakOffset must be 0 to 4 m");
            if (!(LongMin > 0f && ThinMax >= 0f && ThinMax < LongMin)) throw new ArgumentException("ThinMax must be under LongMin");
        }
    }

    /// <summary>
    /// Light leaks through thin walls: Unity's proxy volume knows nothing of walls, so a surface between two probe layers takes up to
    /// half its light from the layer on the wall's far side. A thin renderer (thin along a world axis and long along another: a wall,
    /// a floor, a table top) reads the volume LeakOffset in front of itself instead, toward the head, through its block's
    /// unity_ProbeVolumeWorldToObject (a translation), which every lit shader of the game applies when unity_ProbeVolumeParams.y is 1
    /// (a read-only scan of the game's shaders, 2026-11-14). A plane is only ever seen from the head's side, so that side's light is
    /// the light it should get. Small things, rooms and big blocks keep the plain lookup; so do walls at an angle to the world's axes.
    /// </summary>
    public static class GiLeak
    {
        /// <summary>The code for no offset (each axis's direction + 1, in base 3: x + 3 y + 9 z).</summary>
        public const int None = 13;

        /// <summary>The axes (bit 0 x, 1 y, 2 z) a renderer whose world bounds have this size is thin along, when it is also long
        /// along another; 0 for the rest (and when LeakOffset is 0).</summary>
        public static int ThinAxes(Vector3 size, GiConfig c)
        {
            if (!(c.LeakOffset > 0f) || !(Math.Max(size.X, Math.Max(size.Y, size.Z)) >= c.LongMin)) return 0;
            int axes = 0;
            if (size.X <= c.ThinMax) axes |= 1;
            if (size.Y <= c.ThinMax) axes |= 2;
            if (size.Z <= c.ThinMax) axes |= 4;
            return axes;
        }

        /// <summary>The offset's code for a renderer thin along <paramref name="axes"/> whose bounds' centre is
        /// <paramref name="mid"/>, seen from <paramref name="head"/>: along each thin axis, toward the head's side.</summary>
        public static int Code(int axes, Vector3 mid, Vector3 head)
        {
            int sx = (axes & 1) == 0 ? 0 : head.X >= mid.X ? 1 : -1;
            int sy = (axes & 2) == 0 ? 0 : head.Y >= mid.Y ? 1 : -1;
            int sz = (axes & 4) == 0 ? 0 : head.Z >= mid.Z ? 1 : -1;
            return (sx + 1) + 3 * (sy + 1) + 9 * (sz + 1);
        }

        /// <summary>The offset (m) a code stands for, <paramref name="d"/> along each axis it moves on.</summary>
        public static Vector3 Offset(int code, float d) => new Vector3(code % 3 - 1, code / 3 % 3 - 1, code / 9 - 1) * d;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Int4
    {
        public int X, Y, Z, W;
        public Int4(int x, int y, int z, int w) { X = x; Y = y; Z = z; W = w; }
    }

    /// <summary>A light as the GPU lights voxels with it (64 bytes, GiLight in SoftbodyGI.hlsl).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GiLight
    {
        public Vector4 PosRange;   // xyz: position; w: range
        public Vector4 DirType;    // xyz: the way it shines; w: 0 directional, 1 point, 2 spot
        public Vector4 Color;      // rgb: linear colour * intensity as the shaders get it; w: 1 when it casts shadows
        public Vector4 Spot;       // x: cos of the outer angle; y: cos of where the fall-off starts; z: 1 / range^2

        public static GiLight Directional(Vector3 shines, Vector3 color, bool shadows) => new GiLight
        {
            DirType = new Vector4(Vector3.Normalize(shines), 0f),
            Color = new Vector4(color, shadows ? 1f : 0f),
        };

        public static GiLight Point(Vector3 pos, float range, Vector3 color, bool shadows) => new GiLight
        {
            PosRange = new Vector4(pos, range),
            DirType = new Vector4(0f, -1f, 0f, 1f),
            Color = new Vector4(color, shadows ? 1f : 0f),
            Spot = new Vector4(-1f, 1f, 1f / (range * range), 0f),
        };

        /// <summary>A spot light; <paramref name="angle"/> is Unity's spotAngle (the full cone, degrees). Unity's default spot cookie
        /// fades over most of the cone; here it fades from a quarter of the angle in.</summary>
        public static GiLight SpotLight(Vector3 pos, Vector3 shines, float range, float angle, Vector3 color, bool shadows)
        {
            float outer = (float)Math.Cos(Math.Min(179f, Math.Max(1f, angle)) * 0.5f * Math.PI / 180.0);
            float inner = (float)Math.Cos(Math.Min(179f, Math.Max(1f, angle)) * 0.125f * Math.PI / 180.0);
            return new GiLight
            {
                PosRange = new Vector4(pos, range),
                DirType = new Vector4(Vector3.Normalize(shines), 2f),
                Color = new Vector4(color, shadows ? 1f : 0f),
                Spot = new Vector4(outer, inner, 1f / (range * range), 0f),
            };
        }
    }

    /// <summary>The world's plain ambient: Unity's per-object light probe constants (unity_SHAr, SHAg, SHAb, SHBr, SHBg, SHBb, SHC),
    /// which ShadeSH9 evaluates to irradiance / pi for a normal, and the sky radiance they stand for.</summary>
    public static class GiSky
    {
        /// <summary>A uniform ambient c (SphericalHarmonicsL2.AddAmbientLight): ShadeSH9 gives c for every normal.</summary>
        public static Vector4[] Uniform(Vector3 c) => new[]
        {
            new Vector4(0f, 0f, 0f, c.X), new Vector4(0f, 0f, 0f, c.Y), new Vector4(0f, 0f, 0f, c.Z),
            Vector4.Zero, Vector4.Zero, Vector4.Zero, new Vector4(0f, 0f, 0f, 1f),
        };

        /// <summary>The radiance a ray sees from the sky, as the same polynomial: the irradiance's band 1 times 3/2, band 2 times 4
        /// (the cosine lobe's 2/3 and 1/4 undone). SHA.w holds band 0 minus band 2's constant, so it loses 3 more of that.</summary>
        public static Vector4[] RadianceFromValue(Vector4[] v)
        {
            var r = new Vector4[7];
            for (int c = 0; c < 3; c++)
            {
                var a = v[c];
                var b = v[3 + c];
                r[c] = new Vector4(a.X * 1.5f, a.Y * 1.5f, a.Z * 1.5f, a.W - b.Z);
                r[3 + c] = b * 4f;
            }
            r[6] = new Vector4(v[6].X * 4f, v[6].Y * 4f, v[6].Z * 4f, 1f);
            return r;
        }

        /// <summary>ShadeSH9 on the CPU.</summary>
        public static Vector3 Evaluate(Vector4[] p, Vector3 n)
        {
            var n1 = new Vector4(n, 1f);
            var vb = new Vector4(n.X * n.Y, n.Y * n.Z, n.Z * n.Z, n.Z * n.X);
            float vc = n.X * n.X - n.Y * n.Y;
            return new Vector3(
                Vector4.Dot(p[0], n1) + Vector4.Dot(p[3], vb) + p[6].X * vc,
                Vector4.Dot(p[1], n1) + Vector4.Dot(p[4], vb) + p[6].Y * vc,
                Vector4.Dot(p[2], n1) + Vector4.Dot(p[5], vb) + p[6].Z * vc);
        }
    }

    /// <summary>One frame's share of the work.</summary>
    public sealed class GiFramePlan
    {
        public int Frame;
        /// <summary>The first frame, or a jump farther than the grid: everything starts over.</summary>
        public bool Reset;
        /// <summary>Re-centred (in probes): the new grid's cell c was the old grid's cell c + Shift.</summary>
        public int ShiftX, ShiftY, ShiftZ;
        /// <summary>The world corner of the grid (voxels and probes).</summary>
        public Vector3 Origin;
        /// <summary>The slab the voxelize shader drew this frame (-1: none).</summary>
        public int Slab = -1;
        public int InjectFirst, InjectCount;
        public int ProbeFirst, ProbeCount;
        /// <summary>This frame's random turn of the probes' rays (rows).</summary>
        public Vector4 Rot0, Rot1, Rot2;
        public bool HasShift => ShiftX != 0 || ShiftY != 0 || ShiftZ != 0;
    }

    public sealed class GiPlanner
    {
        readonly GiConfig _c;
        readonly Random _rng = new Random(20261113);
        int _cx, _cy, _cz;   // the grid's corner, in probe cells (world = cell * spacing)
        bool _have;
        int _slab, _inject, _probe, _frame;

        /// <summary>Set while the renderers move over to the volume of the last re-centring: no new re-centring until then.</summary>
        public bool HoldShift;

        public GiPlanner(GiConfig config) { _c = config; }

        public bool HasGrid => _have;
        public Vector3 Origin => new Vector3(_cx, _cy, _cz) * _c.ProbeSpacing;

        /// <summary>Where the slab's voxels are: its world corner and size.</summary>
        public void SlabBox(int slab, out Vector3 min, out Vector3 size)
        {
            min = Origin + new Vector3(0f, 0f, slab * _c.SlabSlices * _c.VoxelSize);
            size = new Vector3(_c.VoxX, _c.VoxY, _c.SlabSlices) * _c.VoxelSize;
        }

        /// <summary>The next frame's work, for a head at <paramref name="head"/>. With <paramref name="voxelize"/> the caller draws the
        /// returned slab (plan.Slab, in plan.Origin's grid) into the voxelize buffer before the GPU runs the plan.</summary>
        public GiFramePlan Next(Vector3 head, bool voxelize)
        {
            var p = new GiFramePlan { Frame = _frame++ };
            float s = _c.ProbeSpacing;
            int tx = (int)Math.Floor(head.X / s + 0.5) - _c.ProbeX / 2;
            int ty = (int)Math.Floor(head.Y / s + 0.5) - _c.ProbeY / 2;
            int tz = (int)Math.Floor(head.Z / s + 0.5) - _c.ProbeZ / 2;
            if (!_have)
            {
                _have = true; p.Reset = true;
                _cx = tx; _cy = ty; _cz = tz;
            }
            else if (!HoldShift)
            {
                int dx = tx - _cx, dy = ty - _cy, dz = tz - _cz;
                if (Math.Abs(dx) > _c.ProbeX / 4 || Math.Abs(dy) > _c.ProbeY / 4 || Math.Abs(dz) > _c.ProbeZ / 4)
                {
                    if (Math.Abs(dx) >= _c.ProbeX || Math.Abs(dy) >= _c.ProbeY || Math.Abs(dz) >= _c.ProbeZ) p.Reset = true;
                    else { p.ShiftX = dx; p.ShiftY = dy; p.ShiftZ = dz; }
                    _cx = tx; _cy = ty; _cz = tz;
                }
            }
            p.Origin = Origin;
            if (voxelize) { p.Slab = _slab; _slab = (_slab + 1) % _c.Slabs; }
            p.InjectCount = Math.Min(_c.InjectSlices, _c.VoxZ);
            p.InjectFirst = _inject;
            _inject = (_inject + p.InjectCount) % _c.VoxZ;
            p.ProbeCount = Math.Min(_c.ProbesPerFrame, _c.Probes);
            p.ProbeFirst = _probe;
            _probe = (_probe + p.ProbeCount) % _c.Probes;
            // a uniformly random turn (Shoemake)
            double u1 = _rng.NextDouble(), u2 = _rng.NextDouble() * 2 * Math.PI, u3 = _rng.NextDouble() * 2 * Math.PI;
            var q = new Quaternion((float)(Math.Sqrt(1 - u1) * Math.Sin(u2)), (float)(Math.Sqrt(1 - u1) * Math.Cos(u2)),
                                   (float)(Math.Sqrt(u1) * Math.Sin(u3)), (float)(Math.Sqrt(u1) * Math.Cos(u3)));
            var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
            p.Rot0 = new Vector4(m.M11, m.M12, m.M13, 0f);
            p.Rot1 = new Vector4(m.M21, m.M22, m.M23, 0f);
            p.Rot2 = new Vector4(m.M31, m.M32, m.M33, 0f);
            return p;
        }
    }

    /// <summary>A volume the GPU writes this frame: which one, and the world corner of its window (the shaders' unity_ProbeVolumeMin).</summary>
    public struct GiWindow
    {
        public int Volume;
        public Vector3 Origin;
        public GiWindow(int volume, Vector3 origin) { Volume = volume; Origin = origin; }
    }

    public sealed class GiStats
    {
        /// <summary>The GPU's time for a frame's work (smoothed), and the last one measured (ms; read a few frames late).</summary>
        public double GpuMs, LastGpuMs;
        public int Frames;
    }
}
