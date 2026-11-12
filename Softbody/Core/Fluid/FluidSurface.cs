using System.Diagnostics;
using System.Numerics;

namespace Softbody.Core.Fluid;

/// <summary>
/// 0.15.0: the liquid's surface as a triangle mesh, rebuilt from the particles each time it is shown.
///
/// The field is Zhu and Bridson's (2005): at every grid point, the distance to the kernel-weighted mean of the particles
/// near it, minus a radius. A lone drop is then a round bead, and a pool has a flat top half a spacing above its top layer
/// instead of a lumpy one. The mesh is surface nets: one vertex per grid cell the surface crosses (the mean of the
/// crossings on its edges), and a quad across every grid edge that crosses, which gives smooth, even triangles without
/// marching cubes' tables. Normals come from the field's gradient, colours from the nearby liquids' colours by the same
/// weights.
///
/// 0.15.1: the grid is sparse. Grid points lie on one fixed lattice in world space and exist only in 4 x 4 x 4 blocks near
/// particles (found through a hashed grid of the particles), so the cost follows the number of particles, never how far
/// they have spread: a stream falling a long way, or water spread across a big floor, used to stretch one dense grid over
/// all of it (and the world thread waited for it: the 0.15.0 freeze).
/// </summary>
public sealed class FluidSurface
{
    /// <summary>Grid cell, as a share of the particle size.</summary>
    public float CellScale = 0.8f;
    /// <summary>How far a particle reaches into the field, in particle sizes.</summary>
    public float KernelScale = 2f;
    /// <summary>The radius of a lone drop, in particle sizes.</summary>
    public float RadiusScale = 0.6f;
    /// <summary>The most blocks of grid points (each 4 x 4 x 4); beyond them the rest of the liquid is not shown.</summary>
    public int MaxBricks = 24000;
    /// <summary>0.18.0: how big a particle with few neighbours (a lone droplet, a small cluster, a thin sheet) is drawn, as a
    /// share of its own size (1 = its own volume), fading to its own size at 20 neighbours (the liquid's body). Whatever it is,
    /// no particle is drawn smaller than the grid can show.</summary>
    public float DropletSize = 1.5f;
    /// <summary>0.20.0: the neighbour count at which DropletSize has faded to a particle's own size (0.18.0 to 0.19.0: 20).
    /// A particle on top of a sheet has about 10, so at 20 it was drawn big: a cone with a dent around it.</summary>
    public float DropletFade = 12f;
    /// <summary>0.20.0: how far each particle is drawn towards the mean of its neighbours within R (0 = where it is; Yu and
    /// Turk 2013 use 0.9). It evens out the particles' layering and lone bumps, which showed as ripples, lines and cones.</summary>
    public float Smoothing = 0.5f;
    /// <summary>0.25.0: passes of weighted Laplacian smoothing over the finished mesh (Löschner, Böttcher, Jeske and Bender
    /// 2023): each vertex moves towards the mean of the vertices it shares an edge with, and so does its normal, by how
    /// surrounded by liquid it is (none with MeshSmoothFrom particles within the kernel radius or fewer, fully from
    /// MeshSmoothFull): a pool's surface goes glassy, while drops, splashes and thin sheets keep their shape. 0: off.</summary>
    public int MeshSmoothing = 4;
    public const float MeshSmoothFrom = 4f, MeshSmoothFull = 14f;
    /// <summary>0.25.0: Taubin's two steps (in, then back out a little more), so the smoothing does not shrink the liquid.</summary>
    public const float TaubinIn = 0.5f, TaubinOut = -0.53f;
    /// <summary>0.25.0: the classic solver (0.24.0's, an emergency backup): no mesh smoothing.</summary>
    public bool Classic;

    public Vector3[] Positions = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>();
    public Vector4[] Colors = Array.Empty<Vector4>();
    public int[] Indices = Array.Empty<int>();
    public int VertexCount { get; private set; }
    public int IndexCount { get; private set; }
    public float Cell { get; private set; }
    public double BuildMs { get; private set; }
    public int Bricks { get; private set; }
    /// <summary>The last build ran out of blocks (some liquid was left out).</summary>
    public bool Truncated { get; private set; }
    /// <summary>0.19.0: how many times it was built (to take each build's result once).</summary>
    public int Builds { get; private set; }
    /// <summary>0.21.0: whether a build now would differ from the last one with the same particles (its settings or the
    /// particle size changed, or it was never built).</summary>
    public bool SettingsChanged(float particleSize) =>
        Builds == 0 || CellScale != _bCell || KernelScale != _bKernel || RadiusScale != _bRadius || MaxBricks != _bBricks ||
        DropletSize != _bDroplet || DropletFade != _bFade || Smoothing != _bSmooth || particleSize != _bSize ||
        MeshSmoothing != _bMeshSmooth || Classic != _bClassic;
    float _bCell, _bKernel, _bRadius, _bDroplet, _bFade, _bSmooth, _bSize; int _bBricks, _bMeshSmooth; bool _bClassic;

    /// <summary>
    /// 0.19.0: the detail to build at next, from how the last build at <paramref name="used"/> fared: lower (just enough) when it
    /// ran out of blocks, back up a tenth at a time towards <paramref name="requested"/> while under 40 % of them. A big thin
    /// sheet at SurfaceDetail 3 needs more blocks than there are, and the blocks that missed out left holes in a patchwork.
    /// </summary>
    public float NextDetail(float requested, float used)
    {
        if (!(used > 0f) || used > requested) used = requested;
        if (Truncated) return MathF.Max(0.25f, used * 0.8f);
        if (Bricks < 0.4f * MaxBricks && used < requested) return MathF.Min(requested, used * 1.1f);
        return used;
    }

    const int B = 4, B3 = B * B * B;
    const float Far = 1e9f;

    // the particles in a hashed grid of cells R wide
    int[] _pKey = Array.Empty<int>(), _pOrder = Array.Empty<int>(), _hStart = Array.Empty<int>(), _hEnd = Array.Empty<int>();
    int[] _pcx = Array.Empty<int>(), _pcy = Array.Empty<int>(), _pcz = Array.Empty<int>();
    float[] _prad = Array.Empty<float>();
    Vector3[] _xs = Array.Empty<Vector3>();
    int _hMask;
    // the blocks
    readonly Dictionary<long, int> _brick = new();
    int[] _bx = Array.Empty<int>(), _by = Array.Empty<int>(), _bz = Array.Empty<int>();
    float[] _phi = Array.Empty<float>();
    int[] _cellVert = Array.Empty<int>();
    // per block results
    List<Vector3>[] _vPos = Array.Empty<List<Vector3>>(), _vNrm = Array.Empty<List<Vector3>>();
    List<int>[] _vCell = Array.Empty<List<int>>(), _quads = Array.Empty<List<int>>();
    int[] _vBase = Array.Empty<int>();
    // 0.25.0: mesh smoothing: each vertex's weight and its (up to) six neighbours; the passes' other buffers
    float[] _vw = Array.Empty<float>();
    int[] _vNbr = Array.Empty<int>();
    Vector3[] _sPos = Array.Empty<Vector3>(), _sNrm = Array.Empty<Vector3>();

    static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    int Hash(int x, int y, int z) => (int)(((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791)) & (uint)_hMask);

    /// <summary>Builds the mesh from the first <paramref name="count"/> particles.</summary>
    public void Build(ReadOnlySpan<Vector3> x, ReadOnlySpan<ushort> src, int count, IReadOnlyList<FluidLiquid> liquids, float particleSize)
    {
        var sw = Stopwatch.StartNew();
        VertexCount = 0; IndexCount = 0; Bricks = 0; Truncated = false; Builds++;
        _bCell = CellScale; _bKernel = KernelScale; _bRadius = RadiusScale; _bBricks = MaxBricks; _bDroplet = DropletSize; _bFade = DropletFade; _bSmooth = Smoothing; _bSize = particleSize;
        if (count <= 0) { BuildMs = sw.Elapsed.TotalMilliseconds; return; }
        float R = KernelScale * particleSize, rad = RadiusScale * particleSize, cell = CellScale * particleSize;
        Cell = cell;
        float R2 = R * R, invR = 1f / R, invCell = 1f / cell;
        var xs = x.Slice(0, count).ToArray(); var ss = src.Slice(0, count).ToArray();
        var colors = new Vector4[liquids.Count];
        for (int l = 0; l < colors.Length; l++) colors[l] = liquids[l].Color;

        // ---- the particles in a hashed grid (cells R wide), counting-sorted
        int table = 1; while (table < 2 * count) table <<= 1;
        _hMask = table - 1;
        if (_hStart.Length < table) { _hStart = new int[table]; _hEnd = new int[table]; }
        if (_pKey.Length < count) { _pKey = new int[count]; _pOrder = new int[count]; _pcx = new int[count]; _pcy = new int[count]; _pcz = new int[count]; }
        Array.Clear(_hStart, 0, table);
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            var p = xs[i];
            if (!float.IsFinite(p.X + p.Y + p.Z)) { _pKey[i] = -1; continue; }
            int cx = (int)MathF.Floor(p.X * invR), cy = (int)MathF.Floor(p.Y * invR), cz = (int)MathF.Floor(p.Z * invR);
            _pcx[i] = cx; _pcy[i] = cy; _pcz[i] = cz;
            int k = Hash(cx, cy, cz);
            _pKey[i] = k; _hStart[k]++; valid++;
        }
        int sum = 0;
        for (int c = 0; c < table; c++) { int k = _hStart[c]; _hStart[c] = sum; sum += k; }
        Array.Copy(_hStart, _hEnd, table);
        for (int i = 0; i < count; i++) if (_pKey[i] >= 0) _pOrder[_hEnd[_pKey[i]]++] = i;
        var hStart = _hStart; var hEnd = _hEnd; var order = _pOrder; var pcx = _pcx; var pcy = _pcy; var pcz = _pcz;

        // 0.18.0: each particle's radius in the field, from how many neighbours it has (see DropletSize)
        if (_prad.Length < count) _prad = new float[Math.Max(count, _prad.Length * 2)];
        var prad = _prad;
        float boost = float.IsFinite(DropletSize) ? Math.Clamp(DropletSize, 0.5f, 3f) : 1.5f, floorR = 0.9f * cell;
        float fade = float.IsFinite(DropletFade) ? Math.Clamp(DropletFade, 1f, 40f) : 12f;
        var keys = _pKey;
        Parallel.For(0, (count + 255) / 256, chunk =>
        {
            int a = chunk * 256, e2 = Math.Min(count, a + 256);
            for (int i = a; i < e2; i++)
            {
                if (keys[i] < 0) { prad[i] = rad; continue; }
                var p = xs[i];
                int n = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int qx = pcx[i] + dx, qy = pcy[i] + dy, qz = pcz[i] + dz;
                            int k = Hash(qx, qy, qz);
                            for (int q = hStart[k], e = hEnd[k]; q < e; q++)
                            {
                                int j = order[q];
                                if (j == i || pcx[j] != qx || pcy[j] != qy || pcz[j] != qz) continue;
                                if (Vector3.DistanceSquared(p, xs[j]) < R2) n++;
                            }
                        }
                float t = Math.Clamp(n / fade, 0f, 1f);
                t = t * t * (3f - 2f * t);
                prad[i] = MathF.Max(rad * (boost + (1f - boost) * t), floorR * (1f - t));
            }
        });

        // 0.20.0: the particles as drawn, each moved towards its neighbours' mean (weights 1 - (r/R)³). They stay in the hashed
        // grid of where they really are: a drawn particle is at most about half a spacing from it, so the field misses only
        // weights near the kernel's edge.
        float smooth = float.IsFinite(Smoothing) ? Math.Clamp(Smoothing, 0f, 1f) : 0.5f;
        if (smooth > 0f)
        {
            if (_xs.Length < count) _xs = new Vector3[Math.Max(count, _xs.Length * 2)];
            var sm = _xs; var orig = xs;
            Parallel.For(0, (count + 255) / 256, chunk =>
            {
                int a = chunk * 256, e2 = Math.Min(count, a + 256);
                for (int i = a; i < e2; i++)
                {
                    var p = orig[i];
                    if (keys[i] < 0) { sm[i] = p; continue; }
                    var acc = Vector3.Zero; float ws = 0f;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int qx = pcx[i] + dx, qy = pcy[i] + dy, qz = pcz[i] + dz;
                                int k = Hash(qx, qy, qz);
                                for (int q = hStart[k], e = hEnd[k]; q < e; q++)
                                {
                                    int j = order[q];
                                    if (pcx[j] != qx || pcy[j] != qy || pcz[j] != qz) continue;
                                    float r2 = Vector3.DistanceSquared(p, orig[j]);
                                    if (r2 >= R2) continue;
                                    float u = MathF.Sqrt(r2) * invR, w = 1f - u * u * u;
                                    acc += orig[j] * w; ws += w;
                                }
                            }
                    sm[i] = ws > 0f ? Vector3.Lerp(p, acc / ws, smooth) : p;
                }
            });
            Array.Copy(sm, xs, count);
        }

        // ---- the blocks near particles: from each occupied cell of that grid (not each particle), the blocks its cell
        // plus R overlaps
        _brick.Clear();
        int nb = 0;
        float reach = R + cell;
        for (int c = 0; c < table && !Truncated; c++)
        {
            int s0 = hStart[c], s1 = hEnd[c];
            if (s0 == s1) continue;
            // a hashed cell can hold several real cells: take the bounds of its particles
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            for (int q = s0; q < s1; q++) { var p = xs[order[q]]; lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            if (hi.X - lo.X > 8f * R || hi.Y - lo.Y > 8f * R || hi.Z - lo.Z > 8f * R)
            {
                // a collision of far-apart cells: each particle on its own
                for (int q = s0; q < s1 && !Truncated; q++) { var p = xs[order[q]]; AddBricks(p, p, reach, invCell, ref nb); }
                continue;
            }
            AddBricks(lo, hi, reach, invCell, ref nb);
        }
        Bricks = nb;
        if (_phi.Length < nb * B3) { int cap = Math.Max(nb, _phi.Length / B3 * 2); _phi = new float[cap * B3]; _cellVert = new int[cap * B3]; }

        // ---- the field at every point of every block, in parallel
        var phi = _phi; var bxs = _bx; var bys = _by; var bzs = _bz;
        Parallel.For(0, nb, b =>
        {
            int ox = bxs[b] * B, oy = bys[b] * B, oz = bzs[b] * B;
            for (int l = 0; l < B3; l++)
            {
                int lx = l % B, ly = (l / B) % B, lz = l / (B * B);
                var p = new Vector3(ox + lx, oy + ly, oz + lz) * cell;
                int gx = (int)MathF.Floor(p.X * invR), gy = (int)MathF.Floor(p.Y * invR), gz = (int)MathF.Floor(p.Z * invR);
                float ws = 0f, wr = 0f; var wx = Vector3.Zero;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int qx = gx + dx, qy = gy + dy, qz = gz + dz;
                            int k = Hash(qx, qy, qz);
                            for (int q = hStart[k], e = hEnd[k]; q < e; q++)
                            {
                                int i = order[q];
                                if (pcx[i] != qx || pcy[i] != qy || pcz[i] != qz) continue;
                                float r2 = Vector3.DistanceSquared(p, xs[i]);
                                if (r2 >= R2) continue;
                                float t = 1f - r2 / R2;
                                float w = t * t * t;
                                ws += w; wx += xs[i] * w; wr += prad[i] * w;
                            }
                        }
                // Zhu and Bridson's field with the particles' own radii (0.18.0), weighted like their positions
                phi[b * B3 + l] = ws > 1e-6f ? Vector3.Distance(p, wx / ws) - wr / ws : Far;
            }
        });

        // ---- surface nets: a vertex in every cell the surface crosses (each block its own cells), in parallel
        if (_vPos.Length < nb)
        {
            int cap = Math.Max(nb, _vPos.Length * 2);
            Array.Resize(ref _vPos, cap); Array.Resize(ref _vNrm, cap); Array.Resize(ref _vCell, cap); Array.Resize(ref _quads, cap); Array.Resize(ref _vBase, cap);
        }
        var cellVert = _cellVert;
        Parallel.For(0, nb, b =>
        {
            var vp = _vPos[b] ??= new List<Vector3>(); var vn = _vNrm[b] ??= new List<Vector3>(); var vc = _vCell[b] ??= new List<int>();
            vp.Clear(); vn.Clear(); vc.Clear();
            int ox = bxs[b] * B, oy = bys[b] * B, oz = bzs[b] * B;
            Span<float> v = stackalloc float[8];
            for (int l = 0; l < B3; l++)
            {
                cellVert[b * B3 + l] = -1;
                int lx = l % B, ly = (l / B) % B, lz = l / (B * B);
                int ix = ox + lx, iy = oy + ly, iz = oz + lz;
                int inside = 0; bool far = false;
                for (int k = 0; k < 8; k++)
                {
                    float f = PhiAt(ix + (k & 1), iy + ((k >> 1) & 1), iz + ((k >> 2) & 1), b, ox, oy, oz);
                    v[k] = f;
                    if (f >= Far) far = true;
                    if (f < 0f) inside |= 1 << k;
                }
                if (inside == 0 || inside == 255) continue;
                var acc = Vector3.Zero; int crossings = 0;
                for (int e = 0; e < 12; e++)
                {
                    int a = EdgeA[e], bb = EdgeB[e];
                    bool ia = (inside & (1 << a)) != 0, ib = (inside & (1 << bb)) != 0;
                    if (ia == ib) continue;
                    float fa = v[a], fb = v[bb];
                    float t = fa >= Far || fb >= Far ? 0.5f : (fa == fb ? 0.5f : Math.Clamp(fa / (fa - fb), 0f, 1f));
                    var pa = new Vector3(a & 1, (a >> 1) & 1, (a >> 2) & 1); var pb = new Vector3(bb & 1, (bb >> 1) & 1, (bb >> 2) & 1);
                    acc += pa + (pb - pa) * t;
                    crossings++;
                }
                var wp = (new Vector3(ix, iy, iz) + acc / crossings) * cell;
                Vector3 n;
                if (far) n = Vector3.UnitY;
                else
                {
                    float gx = v[1] - v[0] + v[3] - v[2] + v[5] - v[4] + v[7] - v[6];
                    float gy = v[2] - v[0] + v[3] - v[1] + v[6] - v[4] + v[7] - v[5];
                    float gz = v[4] - v[0] + v[5] - v[1] + v[6] - v[2] + v[7] - v[3];
                    var g = new Vector3(gx, gy, gz);
                    float gl = g.Length();
                    n = gl > 1e-9f && float.IsFinite(gl) ? g / gl : Vector3.UnitY;
                }
                cellVert[b * B3 + l] = vp.Count;
                vp.Add(wp); vn.Add(n); vc.Add(l);
            }
        });
        // global vertex numbers: each block's vertices after the blocks before it
        int total = 0;
        for (int b = 0; b < nb; b++) { _vBase[b] = total; total += _vPos[b].Count; }
        var vbase = _vBase;

        // ---- a quad across every grid edge the surface crosses, between the four cells around it
        Parallel.For(0, nb, b =>
        {
            var quads = _quads[b] ??= new List<int>();
            quads.Clear();
            int ox = bxs[b] * B, oy = bys[b] * B, oz = bzs[b] * B;
            for (int l = 0; l < B3; l++)
            {
                int lx = l % B, ly = (l / B) % B, lz = l / (B * B);
                int ix = ox + lx, iy = oy + ly, iz = oz + lz;
                float f0 = phi[b * B3 + l];
                bool in0 = f0 < 0f;
                if ((PhiAt(ix + 1, iy, iz, b, ox, oy, oz) < 0f) != in0)
                    Quad(quads, (ix, iy - 1, iz - 1), (ix, iy, iz - 1), (ix, iy, iz), (ix, iy - 1, iz), in0);
                if ((PhiAt(ix, iy + 1, iz, b, ox, oy, oz) < 0f) != in0)
                    Quad(quads, (ix - 1, iy, iz - 1), (ix - 1, iy, iz), (ix, iy, iz), (ix, iy, iz - 1), in0);
                if ((PhiAt(ix, iy, iz + 1, b, ox, oy, oz) < 0f) != in0)
                    Quad(quads, (ix - 1, iy - 1, iz), (ix, iy - 1, iz), (ix, iy, iz), (ix - 1, iy, iz), in0);
            }
        });

        // ---- gather into the output arrays, with each vertex's colour from the liquids near it
        int ni = 0;
        for (int b = 0; b < nb; b++) ni += _quads[b].Count;
        if (Positions.Length < total) { int cap = Math.Max(total, Positions.Length * 2); Positions = new Vector3[cap]; Normals = new Vector3[cap]; Colors = new Vector4[cap]; }
        if (Indices.Length < ni) Indices = new int[Math.Max(ni, Indices.Length * 2)];
        int w = 0;
        for (int b = 0; b < nb; b++)
        {
            _vPos[b].CopyTo(Positions, vbase[b]); _vNrm[b].CopyTo(Normals, vbase[b]);
            _quads[b].CopyTo(Indices, w); w += _quads[b].Count;
        }
        var pos = Positions; var cols = Colors;
        if (_vw.Length < total) _vw = new float[Math.Max(total, _vw.Length * 2)];
        var vw = _vw;
        Parallel.For(0, (total + 255) / 256, chunk =>
        {
            int a = chunk * 256, e2 = Math.Min(total, a + 256);
            for (int vi = a; vi < e2; vi++)
            {
                var p = pos[vi];
                int gx = (int)MathF.Floor(p.X * invR), gy = (int)MathF.Floor(p.Y * invR), gz = (int)MathF.Floor(p.Z * invR);
                var c = Vector4.Zero; float ws = 0f; int near = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int qx = gx + dx, qy = gy + dy, qz = gz + dz;
                            int k = Hash(qx, qy, qz);
                            for (int q = hStart[k], e = hEnd[k]; q < e; q++)
                            {
                                int i = order[q];
                                if (pcx[i] != qx || pcy[i] != qy || pcz[i] != qz) continue;
                                float r2 = Vector3.DistanceSquared(p, xs[i]);
                                if (r2 >= R2) continue;
                                float t = 1f - r2 / R2;
                                float wt = t * t * t;
                                near++;
                                int si = ss[i];
                                c += (si < colors.Length ? colors[si] : Vector4.One) * wt; ws += wt;
                            }
                        }
                cols[vi] = ws > 1e-9f ? c / ws : new Vector4(0.6f, 0.8f, 1f, 0.35f);
                vw[vi] = Math.Clamp((near - MeshSmoothFrom) / (MeshSmoothFull - MeshSmoothFrom), 0f, 1f);
            }
        });
        if (!Classic && MeshSmoothing > 0 && total > 0) SmoothMesh(total, nb, bxs, bys, bzs, phi, cellVert, vbase);
        _bMeshSmooth = MeshSmoothing; _bClassic = Classic;
        VertexCount = total; IndexCount = ni;
        BuildMs = sw.Elapsed.TotalMilliseconds;

        // the field at a lattice point (Far where no block is: that is away from the liquid)
        float PhiAt(int ix, int iy, int iz, int ownBrick, int ox, int oy, int oz)
        {
            int lx = ix - ox, ly = iy - oy, lz = iz - oz;
            if ((uint)lx < B && (uint)ly < B && (uint)lz < B) return phi[ownBrick * B3 + (lz * B + ly) * B + lx];
            int bx = FloorDiv(ix), by = FloorDiv(iy), bz = FloorDiv(iz);
            if (!_brick.TryGetValue(Key(bx, by, bz), out int ob)) return Far;
            return phi[ob * B3 + ((iz - bz * B) * B + (iy - by * B)) * B + (ix - bx * B)];
        }

        // the global number of a cell's vertex, or -1
        int CellVertex(int cx, int cy, int cz)
        {
            int bx = FloorDiv(cx), by = FloorDiv(cy), bz = FloorDiv(cz);
            if (!_brick.TryGetValue(Key(bx, by, bz), out int ob)) return -1;
            int local = cellVert[ob * B3 + ((cz - bz * B) * B + (cy - by * B)) * B + (cx - bx * B)];
            return local < 0 ? -1 : vbase[ob] + local;
        }

        void Quad(List<int> into, (int, int, int) a, (int, int, int) b, (int, int, int) c, (int, int, int) d, bool flip)
        {
            int va = CellVertex(a.Item1, a.Item2, a.Item3), vb = CellVertex(b.Item1, b.Item2, b.Item3);
            int vc = CellVertex(c.Item1, c.Item2, c.Item3), vd = CellVertex(d.Item1, d.Item2, d.Item3);
            if (va < 0 || vb < 0 || vc < 0 || vd < 0) return;
            if (flip) { into.Add(va); into.Add(vb); into.Add(vc); into.Add(va); into.Add(vc); into.Add(vd); }
            else { into.Add(va); into.Add(vc); into.Add(vb); into.Add(va); into.Add(vd); into.Add(vc); }
        }
    }

    static int FloorDiv(int v) => v >= 0 ? v / B : -((-v + B - 1) / B);

    /// <summary>0.25.0: weighted Laplacian smoothing (see MeshSmoothing). A vertex's neighbours are the vertices of the six cells
    /// next to its own whose shared face the surface crosses (they share an edge of the mesh); the face test keeps the two sides of
    /// a thin sheet, one cell apart, from being pulled into each other.</summary>
    void SmoothMesh(int total, int nb, int[] bxs, int[] bys, int[] bzs, float[] phi, int[] cellVert, int[] vbase)
    {
        if (_vNbr.Length < total * 6) _vNbr = new int[Math.Max(total * 6, _vNbr.Length * 2)];
        if (_sPos.Length < total) { int cap = Math.Max(total, _sPos.Length * 2); _sPos = new Vector3[cap]; _sNrm = new Vector3[cap]; }
        var nbr = _vNbr;
        Parallel.For(0, nb, b =>
        {
            var vc = _vCell[b];
            int ox = bxs[b] * B, oy = bys[b] * B, oz = bzs[b] * B, v0 = vbase[b];
            for (int k = 0; k < vc.Count; k++)
            {
                int l = vc[k];
                int ix = ox + l % B, iy = oy + (l / B) % B, iz = oz + l / (B * B);
                for (int d = 0; d < 6; d++)
                {
                    int axis = d >> 1, sgn = (d & 1) == 0 ? -1 : 1;
                    int jx = ix + (axis == 0 ? sgn : 0), jy = iy + (axis == 1 ? sgn : 0), jz = iz + (axis == 2 ? sgn : 0);
                    int u = Vertex(jx, jy, jz);
                    if (u >= 0)
                    {
                        // the face between the two cells: its four lattice points, on the far side of this cell along the axis
                        int fx = ix + (axis == 0 && sgn > 0 ? 1 : 0), fy = iy + (axis == 1 && sgn > 0 ? 1 : 0), fz = iz + (axis == 2 && sgn > 0 ? 1 : 0);
                        int ins = 0;
                        for (int c = 0; c < 4; c++)
                        {
                            int a1 = c & 1, a2 = c >> 1;
                            int px = fx + (axis == 0 ? 0 : a1), py = fy + (axis == 1 ? 0 : (axis == 0 ? a1 : a2)), pz = fz + (axis == 2 ? 0 : a2);
                            if (Phi(px, py, pz) < 0f) ins++;
                        }
                        if (ins == 0 || ins == 4) u = -1;
                    }
                    nbr[(v0 + k) * 6 + d] = u;
                }
            }
        });
        Vector3[] a = Positions, na = Normals, t = _sPos, nt = _sNrm;
        var w = _vw;
        for (int it = 0; it < MeshSmoothing; it++)
        {
            var ca = a; var cna = na; var ct = t; var cnt = nt;
            // Taubin's smoothing: every other pass steps back a little further than the one before stepped in, so bumps go and
            // the volume stays (plain passes shrank a pool by 1 % in 4 and rounded it away from the walls); normals only go in
            float kPos = it % 2 == 0 ? TaubinIn : TaubinOut;
            Parallel.For(0, (total + 255) / 256, chunk =>
            {
                int s0 = chunk * 256, e0 = Math.Min(total, s0 + 256);
                for (int v = s0; v < e0; v++)
                {
                    float wv = w[v];
                    var sum = Vector3.Zero; var nsum = Vector3.Zero; int c = 0;
                    if (wv > 0f)
                        for (int d = 0; d < 6; d++)
                        {
                            int u = nbr[v * 6 + d];
                            if (u < 0) continue;
                            sum += ca[u]; nsum += cna[u]; c++;
                        }
                    if (c == 0) { ct[v] = ca[v]; cnt[v] = cna[v]; continue; }
                    ct[v] = ca[v] + (sum / c - ca[v]) * (kPos * wv);
                    var nn = cna[v] + (nsum / c - cna[v]) * (TaubinIn * wv);
                    float l = nn.Length();
                    cnt[v] = l > 1e-9f ? nn / l : cna[v];
                }
            });
            (a, t) = (t, a); (na, nt) = (nt, na);
        }
        if (!ReferenceEquals(a, Positions)) { Array.Copy(a, Positions, total); Array.Copy(na, Normals, total); }

        int Vertex(int cx, int cy, int cz)
        {
            int bx = FloorDiv(cx), by = FloorDiv(cy), bz = FloorDiv(cz);
            if (!_brick.TryGetValue(Key(bx, by, bz), out int ob)) return -1;
            int local = cellVert[ob * B3 + ((cz - bz * B) * B + (cy - by * B)) * B + (cx - bx * B)];
            return local < 0 ? -1 : vbase[ob] + local;
        }

        float Phi(int px, int py, int pz)
        {
            int bx = FloorDiv(px), by = FloorDiv(py), bz = FloorDiv(pz);
            if (!_brick.TryGetValue(Key(bx, by, bz), out int ob)) return Far;
            return phi[ob * B3 + ((pz - bz * B) * B + (py - by * B)) * B + (px - bx * B)];
        }
    }

    /// <summary>Adds the blocks whose points lie within <paramref name="reach"/> of the box lo..hi.</summary>
    void AddBricks(Vector3 lo, Vector3 hi, float reach, float invCell, ref int nb)
    {
        int x0 = FloorDiv((int)MathF.Floor((lo.X - reach) * invCell)), x1 = FloorDiv((int)MathF.Floor((hi.X + reach) * invCell));
        int y0 = FloorDiv((int)MathF.Floor((lo.Y - reach) * invCell)), y1 = FloorDiv((int)MathF.Floor((hi.Y + reach) * invCell));
        int z0 = FloorDiv((int)MathF.Floor((lo.Z - reach) * invCell)), z1 = FloorDiv((int)MathF.Floor((hi.Z + reach) * invCell));
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    long k = Key(x, y, z);
                    if (_brick.ContainsKey(k)) continue;
                    if (nb >= MaxBricks) { Truncated = true; return; }
                    if (_bx.Length <= nb) { int cap = Math.Max(256, _bx.Length * 2); Array.Resize(ref _bx, cap); Array.Resize(ref _by, cap); Array.Resize(ref _bz, cap); }
                    _bx[nb] = x; _by[nb] = y; _bz[nb] = z;
                    _brick[k] = nb++;
                }
    }

    // the 12 edges of a cell, as pairs of corner indices (corner k = x + 2y + 4z)
    static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
    static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };
}
