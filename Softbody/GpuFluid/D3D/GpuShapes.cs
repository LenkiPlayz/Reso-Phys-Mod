// 0.16.0: the GPU fluid's colliders: the plugin's collider records turned into the shader's shapes, and the coarse grid that
// pairs them with particles (the CPU version's BuildShapeGrid, over the liquid's bounds as last read back). netstandard2.0.
#nullable disable
using System;
using System.Numerics;

namespace Softbody.FluidGpu
{
    public sealed class GpuShapeGrid
    {
        public GpuShape[] Shapes = new GpuShape[256];
        public int ShapeCount;
        public uint[] Globals = new uint[64];
        public int GlobalCount;
        /// <summary>Cell starts (Cells + 1), then the lists.</summary>
        public uint[] Grid = new uint[1024];
        public int GridLength, Cells, Nx, Ny, Nz;
        public Vector3 Min;
        public float Cell;
        public int MaxShapes = 8192;

        Vector3[] _bmin = new Vector3[256], _bmax = new Vector3[256];
        int[] _fill = new int[1024];

        public void Clear() { ShapeCount = 0; GlobalCount = 0; GridLength = 0; Cells = Nx = Ny = Nz = 0; }

        static Vector4 V4(Vector3 v, float w) => new Vector4(v.X, v.Y, v.Z, w);

        /// <summary>Adds a collider record (kinds as <see cref="FluidChannel"/>); false when full or unusable.</summary>
        public bool Add(in FluidColliderRecord r)
        {
            if (ShapeCount >= MaxShapes) return false;
            var a = new Vector3(r.AX, r.AY, r.AZ); var b = new Vector3(r.BX, r.BY, r.BZ); var c = new Vector3(r.CX, r.CY, r.CZ);
            var q = new Quaternion(r.QX, r.QY, r.QZ, r.QW);
            var vel = new Vector3(r.VelX, r.VelY, r.VelZ);
            if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(vel) || float.IsNaN(r.R) || float.IsInfinity(r.R)) return false;
            if (!Finite(vel)) vel = Vector3.Zero;
            var s = new GpuShape();
            Vector3 lo, hi;
            switch (r.Kind)
            {
                case FluidChannel.KindPlane:
                {
                    float l = b.Length();
                    if (!(l > 1e-9f)) return false;
                    b /= l;
                    lo = new Vector3(float.MinValue); hi = new Vector3(float.MaxValue);
                    break;
                }
                case FluidChannel.KindSphere: lo = a - new Vector3(r.R); hi = a + new Vector3(r.R); break;
                case FluidChannel.KindCapsule:
                case FluidChannel.KindCylinder:
                case FluidChannel.KindCone:
                    lo = Vector3.Min(a, b) - new Vector3(r.R); hi = Vector3.Max(a, b) + new Vector3(r.R); break;
                case FluidChannel.KindBox:
                {
                    float ql = q.Length();
                    q = ql > 1e-9f ? Quaternion.Normalize(q) : Quaternion.Identity;
                    b = Vector3.Abs(b);
                    var ext = Vector3.Abs(Vector3.Transform(new Vector3(b.X, 0, 0), q)) + Vector3.Abs(Vector3.Transform(new Vector3(0, b.Y, 0), q)) +
                              Vector3.Abs(Vector3.Transform(new Vector3(0, 0, b.Z), q));
                    lo = a - ext; hi = a + ext;
                    break;
                }
                case FluidChannel.KindTriangle:
                {
                    var n = Vector3.Cross(b - a, c - a);
                    float l = n.Length();
                    if (!(l > 1e-20f)) return false;
                    n /= l;
                    s.N = V4(n, Vector3.Dot(n, a));
                    lo = Vector3.Min(a, Vector3.Min(b, c)); hi = Vector3.Max(a, Vector3.Max(b, c));
                    break;
                }
                default: return false;
            }
            s.A = V4(a, r.R);
            s.B = V4(b, r.Kind);
            s.C = V4(c, r.TwoSided != 0 ? 1f : 0f);
            s.Q = new Vector4(q.X, q.Y, q.Z, q.W);
            s.V = V4(vel, GpuFluidSim.AsFloat(r.Category == 0 ? FluidChannel.CategoryWorld : r.Category));
            s.BMin = V4(lo, 0f); s.BMax = V4(hi, 0f);
            if (Shapes.Length <= ShapeCount)
            {
                int cap = Math.Min(MaxShapes, Math.Max(ShapeCount + 1, Shapes.Length * 2));
                Array.Resize(ref Shapes, cap); Array.Resize(ref _bmin, cap); Array.Resize(ref _bmax, cap);
            }
            Shapes[ShapeCount] = s; _bmin[ShapeCount] = lo; _bmax[ShapeCount] = hi;
            ShapeCount++;
            return true;
        }

        static bool Finite(Vector3 v) => !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z));

        /// <summary>
        /// The grid over the box the particles can reach this frame (<paramref name="lo"/>..<paramref name="hi"/>, already
        /// padded); each shape is listed in the cells its bounds plus <paramref name="reach"/> overlap; planes and shapes
        /// over more than 512 cells go to the list every particle checks.
        /// </summary>
        public void Build(Vector3 lo, Vector3 hi, float reach, float h)
        {
            GlobalCount = 0; Cells = Nx = Ny = Nz = 0; GridLength = 0;
            if (ShapeCount == 0) return;
            var size = hi - lo;
            if (!(size.X >= 0f && size.Y >= 0f && size.Z >= 0f) || !Finite(size)) { lo = Vector3.Zero; hi = Vector3.Zero; size = Vector3.Zero; }
            float cell = Math.Max(4f * h, (float)Math.Pow(Math.Max(size.X * size.Y * size.Z, 1e-9f) / 32768f, 1.0 / 3.0));
            Cell = cell; Min = lo;
            Nx = Math.Max(1, (int)(size.X / cell) + 1); Ny = Math.Max(1, (int)(size.Y / cell) + 1); Nz = Math.Max(1, (int)(size.Z / cell) + 1);
            Cells = Nx * Ny * Nz;
            if (Grid.Length < Cells + 1) Grid = new uint[Math.Max(Cells + 1, Grid.Length * 2)];
            Array.Clear(Grid, 0, Cells + 1);
            if (_fill.Length < Cells + 1) _fill = new int[Grid.Length];
            int total = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    uint sum = 0;
                    for (int c = 0; c < Cells; c++) { uint k = Grid[c]; Grid[c] = sum; _fill[c] = (int)sum; sum += k; }
                    Grid[Cells] = sum;
                    total = (int)sum;
                    GridLength = Cells + 1 + total;
                    if (Grid.Length < GridLength) Array.Resize(ref Grid, Math.Max(GridLength, Grid.Length * 2));
                }
                for (int s = 0; s < ShapeCount; s++)
                {
                    int kind = (int)Shapes[s].B.W;
                    if (kind == FluidChannel.KindPlane) { if (pass == 0) AddGlobal(s); continue; }
                    var a = Vector3.Max(_bmin[s] - new Vector3(reach), lo); var b = Vector3.Min(_bmax[s] + new Vector3(reach), hi);
                    if (a.X > b.X || a.Y > b.Y || a.Z > b.Z) continue;
                    int x0 = CellOf(a.X - lo.X, Nx), x1 = CellOf(b.X - lo.X, Nx), y0 = CellOf(a.Y - lo.Y, Ny), y1 = CellOf(b.Y - lo.Y, Ny), z0 = CellOf(a.Z - lo.Z, Nz), z1 = CellOf(b.Z - lo.Z, Nz);
                    if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 512) { if (pass == 0) AddGlobal(s); continue; }
                    for (int z = z0; z <= z1; z++)
                        for (int y = y0; y <= y1; y++)
                            for (int x = x0; x <= x1; x++)
                            {
                                int ci = (z * Ny + y) * Nx + x;
                                if (pass == 0) Grid[ci]++;
                                else Grid[Cells + 1 + _fill[ci]++] = (uint)s;
                            }
                }
            }
        }

        void AddGlobal(int s)
        {
            if (Globals.Length <= GlobalCount) Array.Resize(ref Globals, Globals.Length * 2);
            Globals[GlobalCount++] = (uint)s;
        }

        int CellOf(float d, int count) { int c = (int)(d / Cell); return c < 0 ? 0 : (c >= count ? count - 1 : c); }

        /// <summary>Whether a shape's bounds (plus pad) overlap the box (planes always do).</summary>
        public static bool Overlaps(in FluidColliderRecord r, Vector3 lo, Vector3 hi)
        {
            if (r.Kind == FluidChannel.KindPlane) return true;
            Vector3 a = new Vector3(r.AX, r.AY, r.AZ), b = new Vector3(r.BX, r.BY, r.BZ), c = new Vector3(r.CX, r.CY, r.CZ);
            Vector3 mn, mx;
            switch (r.Kind)
            {
                case FluidChannel.KindSphere: mn = a - new Vector3(r.R); mx = a + new Vector3(r.R); break;
                case FluidChannel.KindBox: { float e = Math.Abs(b.X) + Math.Abs(b.Y) + Math.Abs(b.Z); mn = a - new Vector3(e); mx = a + new Vector3(e); break; }
                case FluidChannel.KindTriangle: mn = Vector3.Min(a, Vector3.Min(b, c)); mx = Vector3.Max(a, Vector3.Max(b, c)); break;
                default: mn = Vector3.Min(a, b) - new Vector3(r.R); mx = Vector3.Max(a, b) + new Vector3(r.R); break;
            }
            return mn.X <= hi.X && mx.X >= lo.X && mn.Y <= hi.Y && mx.Y >= lo.Y && mn.Z <= hi.Z && mx.Z >= lo.Z;
        }
    }
}
