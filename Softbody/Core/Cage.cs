using System.Numerics;

namespace Softbody.Core;

/// <summary>
/// A lattice cage over a mesh's bounds, split into tetrahedra, with every render vertex embedded
/// in one tetrahedron by barycentric weights. Everything here is in the mesh's local space.
/// Built once; the simulation and the deformer read it.
/// </summary>
public sealed class Cage
{
    public int CellsX, CellsY, CellsZ;
    public int Nx, Ny, Nz;                 // points per axis
    public Vector3 Origin;                 // local position of point (0,0,0)
    public Vector3 Spacing;                // local distance between neighbouring points per axis
    public float RadiusLocal;              // collision radius of a point, in local units (smallest axis)
    public Vector3[] RestPoints;           // local rest positions of the cage points
    public float[] PointSoftness;          // 0 = firm, 1 = squishy
    public float[] PointBands;             // HueBands.Count weights per point (sum = how strongly it is banded)
    public bool[] Kinematic;               // pinned exactly to the anchor
    public int[] Tets;                     // 4 point indices per tetrahedron
    public int[] Edges;                    // 2 point indices per unique tetrahedron edge
    public int VertexCount;
    public int[] VertexPoints;             // 4 cage point indices per render vertex
    public float[] VertexWeights;          // 4 barycentric weights per render vertex (sum to 1)
    public float[] VertexGain;             // per-vertex softness: how much of the cage motion the vertex follows
    public float MaxWeightAbsSum;          // max over vertices of sum |w| (1 when everything is inside)

    // Smooth embedding (used by the deformer): the cage displacement is interpolated with a
    // quadratic B-spline (C1, so no creases at cell/tet boundaries). The lattice is padded with
    // Ghost layers per side whose displacements are linearly extrapolated each frame, so affine
    // motion (and the rest pose) is reproduced exactly.
    public const int Ghost = 2;
    public int Ex, Ey, Ez;                 // extended grid size per axis (N + 2*Ghost)
    public int[] SplineBase;               // 3 per vertex: extended index (per axis) of the first of its 3 nodes
    public float[] SplineWeights;          // 9 per vertex: 3 B-spline weights per axis (x, y, z)
    public float SplineMaxAbsSum;          // bound on |vertex displacement| / max |point displacement|
    public int ExtIndex(int ei, int ej, int ek) => ei + Ex * (ej + Ey * ek);

    /// <summary>Real lattice indices along an axis that the 3 spline nodes starting at extended index ext0 depend on.</summary>
    public void SplineRealRange(int ext0, int axis, out int lo, out int hi)
    {
        int n = axis == 0 ? Nx : axis == 1 ? Ny : Nz;
        lo = int.MaxValue; hi = int.MinValue;
        for (int e = ext0; e < ext0 + 3; e++)
        {
            int i = e - Ghost;
            int a = i < 0 ? 0 : i >= n ? n - 2 : i;
            int b = i < 0 ? 1 : i >= n ? n - 1 : i;
            lo = Math.Min(lo, a); hi = Math.Max(hi, b);
        }
        lo = Math.Max(lo, 0); hi = Math.Min(hi, n - 1);
    }

    static float GhostAbs(int i, int n) => i >= 0 && i < n ? 1f : (i == -1 || i == n) ? 3f : 5f;
    public Vector3 RestBoundsMin, RestBoundsMax;
    public float MinSoftness, MaxSoftness, MeanSoftness;
    public bool HadSoftnessMap;

    public int PointCount => RestPoints.Length;
    public int TetCount => Tets.Length / 4;
    public int EdgeCount => Edges.Length / 2;
    public int KinematicCount { get { int n = 0; foreach (var k in Kinematic) if (k) n++; return n; } }

    // Sparse lattice: only cells that hold a moving (soft) vertex, plus one ring of cells around them,
    // get points, tets and bodies. Grid nodes outside the cage behave as pinned points (zero
    // displacement), which is exactly what pinned points do, so firm regions cost nothing.
    /// <summary>Vertices at or below this softness never move (the deformer skips them).</summary>
    public const float GainEpsilon = 1e-3f;
    public int[] GridToPoint;              // dense grid node -> cage point index, or -1 (not in the cage)
    public int[] PointExt;                 // cage point -> index in the ghost-padded grid
    public bool[] CellActive;              // per dense cell
    public int ActiveCells, TotalCells;

    public int GridIndex(int i, int j, int k) => i + Nx * (j + Ny * k);
    /// <summary>Cage point at grid node (i, j, k), or -1 when that node is not part of the (sparse) cage.</summary>
    public int Index(int i, int j, int k) => GridToPoint[GridIndex(i, j, k)];

    // Freudenthal / Kuhn split: 6 tets per cube cell along the 000-111 diagonal. Every cell uses the
    // same diagonal, so neighbouring cells share faces exactly. Corner bits: x=1, y=2, z=4.
    static readonly int[,] Perms = { { 0, 1, 2 }, { 0, 2, 1 }, { 1, 0, 2 }, { 1, 2, 0 }, { 2, 0, 1 }, { 2, 1, 0 } };

    /// <summary>Builds the cage. <paramref name="softness"/> may be empty (then everything is soft).</summary>
    public static Cage Build(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<float> softness, SoftbodySettings settings,
                             ReadOnlySpan<float> vertexBands = default)
    {
        if (vertices.Length == 0) throw new ArgumentException("mesh has no vertices");
        var cage = new Cage { VertexCount = vertices.Length, HadSoftnessMap = softness.Length >= vertices.Length };

        var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
        foreach (var v in vertices)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new ArgumentException("mesh has non-finite vertex positions");
            bmin = Vector3.Min(bmin, v); bmax = Vector3.Max(bmax, v);
        }
        cage.RestBoundsMin = bmin; cage.RestBoundsMax = bmax;
        var size = bmax - bmin;
        float maxExt = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (!(maxExt > 1e-6f)) throw new ArgumentException("mesh bounds are degenerate");

        // Give flat meshes some thickness so cells are never degenerate.
        float minThick = 0.05f * maxExt;
        var lo = bmin; var hi = bmax;
        for (int a = 0; a < 3; a++)
        {
            float s = Get(size, a);
            if (s < minThick) { float pad = 0.5f * (minThick - s); Set(ref lo, a, Get(lo, a) - pad); Set(ref hi, a, Get(hi, a) + pad); }
        }
        size = hi - lo;

        int cells = Math.Max(1, settings.Cells);
        int CellsFor(float s) => Math.Clamp((int)MathF.Round(cells * s / maxExt), 1, cells);
        cage.CellsX = CellsFor(size.X); cage.CellsY = CellsFor(size.Y); cage.CellsZ = CellsFor(size.Z);
        var cellCount = new Vector3(cage.CellsX, cage.CellsY, cage.CellsZ);
        var cell = size / cellCount;
        float minCell = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
        float r = settings.PointRadiusFactor * minCell;
        cage.RadiusLocal = r;

        // Inset the lattice by the point radius so the collision spheres' surfaces sit on the mesh
        // bounds (otherwise the mesh would hover one radius above the ground). Vertices just outside
        // the inset lattice are extrapolated, which is exact at rest because the weights are affine.
        for (int a = 0; a < 3; a++)
        {
            if (Get(size, a) > 4f * r) { Set(ref lo, a, Get(lo, a) + r); Set(ref hi, a, Get(hi, a) - r); }
        }
        cage.Origin = lo;
        cage.Spacing = (hi - lo) / cellCount;
        cage.Nx = cage.CellsX + 1; cage.Ny = cage.CellsY + 1; cage.Nz = cage.CellsZ + 1;
        cage.Ex = cage.Nx + 2 * Ghost; cage.Ey = cage.Ny + 2 * Ghost; cage.Ez = cage.Nz + 2 * Ghost;

        // Which cells the cage needs: those holding a vertex that can move, dilated by one cell so the
        // soft region is anchored to (pinned) points in its firm surroundings and every spline node a
        // soft vertex reads is a real point. With no soft vertex at all (all firm, nothing painted yet), keep
        // only the centre cell, all pinned: the softbody still runs, so paint can start it, but costs nothing.
        int nCells = cage.CellsX * cage.CellsY * cage.CellsZ;
        cage.TotalCells = nCells;
        bool hadMap = softness.Length >= vertices.Length;
        var softCell = new bool[nCells];
        bool anySoft = false;
        for (int v = 0; v < vertices.Length; v++)
        {
            float s = hadMap ? softness[v] : 1f;
            if (!float.IsFinite(s)) s = 1f;
            if (s <= GainEpsilon) continue;
            var cc = (vertices[v] - cage.Origin) / cage.Spacing;
            int ci = Math.Clamp((int)MathF.Floor(cc.X), 0, cage.CellsX - 1);
            int cj = Math.Clamp((int)MathF.Floor(cc.Y), 0, cage.CellsY - 1);
            int ck = Math.Clamp((int)MathF.Floor(cc.Z), 0, cage.CellsZ - 1);
            softCell[CellIndex(cage, ci, cj, ck)] = true;
            anySoft = true;
        }
        cage.CellActive = new bool[nCells];
        if (!anySoft)
            cage.CellActive[CellIndex(cage, cage.CellsX / 2, cage.CellsY / 2, cage.CellsZ / 2)] = true;
        else
        for (int k = 0; k < cage.CellsZ; k++)
            for (int j = 0; j < cage.CellsY; j++)
                for (int i = 0; i < cage.CellsX; i++)
                {
                    if (!softCell[CellIndex(cage, i, j, k)]) continue;
                    for (int dk = -1; dk <= 1; dk++)
                        for (int dj = -1; dj <= 1; dj++)
                            for (int di = -1; di <= 1; di++)
                            {
                                int a = i + di, b = j + dj, c = k + dk;
                                if (a < 0 || b < 0 || c < 0 || a >= cage.CellsX || b >= cage.CellsY || c >= cage.CellsZ) continue;
                                cage.CellActive[CellIndex(cage, a, b, c)] = true;
                            }
                }

        // Points: the corners of the active cells, numbered compactly.
        cage.GridToPoint = new int[cage.Nx * cage.Ny * cage.Nz];
        Array.Fill(cage.GridToPoint, -1);
        var restList = new List<Vector3>();
        var extList = new List<int>();
        for (int k = 0; k < cage.CellsZ; k++)
            for (int j = 0; j < cage.CellsY; j++)
                for (int i = 0; i < cage.CellsX; i++)
                {
                    if (!cage.CellActive[CellIndex(cage, i, j, k)]) continue;
                    cage.ActiveCells++;
                    for (int bits = 0; bits < 8; bits++)
                    {
                        int a = i + (bits & 1), b = j + ((bits >> 1) & 1), c = k + ((bits >> 2) & 1);
                        int g = cage.GridIndex(a, b, c);
                        if (cage.GridToPoint[g] >= 0) continue;
                        cage.GridToPoint[g] = restList.Count;
                        restList.Add(cage.Origin + cage.Spacing * new Vector3(a, b, c));
                        extList.Add(cage.ExtIndex(a + Ghost, b + Ghost, c + Ghost));
                    }
                }
        cage.RestPoints = restList.ToArray();
        cage.PointExt = extList.ToArray();
        int np = cage.RestPoints.Length;

        // Tetrahedra and unique edges of the active cells.
        cage.Tets = new int[cage.ActiveCells * 6 * 4];
        var edgeSet = new HashSet<long>();
        var edges = new List<int>();
        int t = 0;
        for (int k = 0; k < cage.CellsZ; k++)
            for (int j = 0; j < cage.CellsY; j++)
                for (int i = 0; i < cage.CellsX; i++)
                    for (int p = 0; p < 6; p++)
                    {
                        if (!cage.CellActive[CellIndex(cage, i, j, k)]) break;
                        int ea = 1 << Perms[p, 0], eb = 1 << Perms[p, 1];
                        int c0 = Corner(cage, i, j, k, 0), c1 = Corner(cage, i, j, k, ea), c2 = Corner(cage, i, j, k, ea | eb), c3 = Corner(cage, i, j, k, 7);
                        cage.Tets[t++] = c0; cage.Tets[t++] = c1; cage.Tets[t++] = c2; cage.Tets[t++] = c3;
                        AddEdge(c0, c1); AddEdge(c0, c2); AddEdge(c0, c3); AddEdge(c1, c2); AddEdge(c1, c3); AddEdge(c2, c3);
                    }
        cage.Edges = edges.ToArray();

        void AddEdge(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            if (edgeSet.Add(((long)a << 32) | (uint)b)) { edges.Add(a); edges.Add(b); }
        }

        // Embed every vertex: pick the cell (clamped), sort the in-cell coordinates, and the
        // Freudenthal tet containing the point follows directly with closed-form weights.
        int nv = vertices.Length;
        cage.VertexPoints = new int[nv * 4];
        cage.VertexWeights = new float[nv * 4];
        cage.VertexGain = new float[nv];
        cage.SplineBase = new int[nv * 3];
        cage.SplineWeights = new float[nv * 9];
        var accS = new double[np]; var accW = new double[np];
        const int NB = HueBands.Count;
        bool hasBands = vertexBands.Length >= vertices.Length * NB;
        var accB = hasBands ? new double[np * NB] : null;
        float maxAbs = 1f, splineMax = 1f, sMin = 1f, sMax = 0f; double sSum = 0;
        for (int v = 0; v < nv; v++)
        {
            float s = cage.HadSoftnessMap ? Math.Clamp(softness[v], 0f, 1f) : 1f;
            if (!float.IsFinite(s)) s = 1f;
            cage.VertexGain[v] = s;
            sMin = MathF.Min(sMin, s); sMax = MathF.Max(sMax, s); sSum += s;

            var c = (vertices[v] - cage.Origin) / cage.Spacing;
            int ci = Math.Clamp((int)MathF.Floor(c.X), 0, cage.CellsX - 1);
            int cj = Math.Clamp((int)MathF.Floor(c.Y), 0, cage.CellsY - 1);
            int ck = Math.Clamp((int)MathF.Floor(c.Z), 0, cage.CellsZ - 1);
            float u0 = c.X - ci, u1 = c.Y - cj, u2 = c.Z - ck;
            // sort axes by coordinate, descending
            int a0 = 0, a1 = 1, a2 = 2;
            float U(int ax) => ax == 0 ? u0 : ax == 1 ? u1 : u2;
            if (U(a0) < U(a1)) (a0, a1) = (a1, a0);
            if (U(a1) < U(a2)) (a1, a2) = (a2, a1);
            if (U(a0) < U(a1)) (a0, a1) = (a1, a0);
            float ua = U(a0), ub = U(a1), uc = U(a2);
            int b0 = 0, b1 = 1 << a0, b2 = b1 | (1 << a1), b3 = 7;
            int o = v * 4;
            if (!cage.CellActive[CellIndex(cage, ci, cj, ck)])
            {
                // Firm vertex outside the sparse cage: never moves, carries no softness to any point.
                for (int q = 0; q < 4; q++) { cage.VertexPoints[o + q] = -1; cage.VertexWeights[o + q] = 0f; }
            }
            else
            {
            cage.VertexPoints[o] = Corner(cage, ci, cj, ck, b0);
            cage.VertexPoints[o + 1] = Corner(cage, ci, cj, ck, b1);
            cage.VertexPoints[o + 2] = Corner(cage, ci, cj, ck, b2);
            cage.VertexPoints[o + 3] = Corner(cage, ci, cj, ck, b3);
            cage.VertexWeights[o] = 1f - ua;
            cage.VertexWeights[o + 1] = ua - ub;
            cage.VertexWeights[o + 2] = ub - uc;
            cage.VertexWeights[o + 3] = uc;
            float abs = 0;
            for (int q = 0; q < 4; q++)
            {
                float w = cage.VertexWeights[o + q];
                abs += MathF.Abs(w);
                if (w > 0)
                {
                    int pq = cage.VertexPoints[o + q];
                    accS[pq] += w * s; accW[pq] += w;
                    if (accB != null) for (int bb = 0; bb < NB; bb++) accB[pq * NB + bb] += w * vertexBands[v * NB + bb];
                }
            }
            maxAbs = MathF.Max(maxAbs, abs);
            }

            // Quadratic B-spline weights per axis around the nearest node. The weights sum to 1 and
            // reproduce linear functions for any offset, so clamping the node never breaks rest identity.
            float absProd = 1f;
            for (int a = 0; a < 3; a++)
            {
                int n = a == 0 ? cage.Nx : a == 1 ? cage.Ny : cage.Nz;
                float u = Get(c, a);
                int i0 = Math.Clamp((int)MathF.Floor(u + 0.5f), -1, n);
                float tt = u - i0;
                float w0 = 0.5f * (0.5f - tt) * (0.5f - tt), w1 = 0.75f - tt * tt, w2 = 0.5f * (0.5f + tt) * (0.5f + tt);
                cage.SplineBase[v * 3 + a] = i0 - 1 + Ghost;
                cage.SplineWeights[v * 9 + 3 * a] = w0;
                cage.SplineWeights[v * 9 + 3 * a + 1] = w1;
                cage.SplineWeights[v * 9 + 3 * a + 2] = w2;
                absProd *= MathF.Abs(w0) * GhostAbs(i0 - 1, n) + MathF.Abs(w1) * GhostAbs(i0, n) + MathF.Abs(w2) * GhostAbs(i0 + 1, n);
            }
            splineMax = MathF.Max(splineMax, absProd);
        }
        cage.MaxWeightAbsSum = maxAbs;
        cage.SplineMaxAbsSum = splineMax;
        cage.MinSoftness = sMin; cage.MaxSoftness = sMax; cage.MeanSoftness = (float)(sSum / nv);

        // Point softness = weighted mean of the vertices it carries. Points that carry nothing
        // (empty interior cells) inherit from neighbours, then fall back to the mesh mean.
        cage.PointSoftness = new float[np];
        for (int p = 0; p < np; p++) cage.PointSoftness[p] = accW[p] > 1e-6 ? (float)(accS[p] / accW[p]) : float.NaN;
        for (int iter = 0; iter < 16; iter++)
        {
            bool any = false;
            var next = (float[])cage.PointSoftness.Clone();
            var sum = new float[np]; var cnt = new int[np];
            for (int e = 0; e < cage.Edges.Length; e += 2)
            {
                int a = cage.Edges[e], b = cage.Edges[e + 1];
                if (!float.IsNaN(cage.PointSoftness[b])) { sum[a] += cage.PointSoftness[b]; cnt[a]++; }
                if (!float.IsNaN(cage.PointSoftness[a])) { sum[b] += cage.PointSoftness[a]; cnt[b]++; }
            }
            for (int p = 0; p < np; p++)
                if (float.IsNaN(cage.PointSoftness[p])) { if (cnt[p] > 0) next[p] = sum[p] / cnt[p]; else any = true; }
            cage.PointSoftness = next;
            if (!any) break;
        }
        cage.Kinematic = new bool[np];
        for (int p = 0; p < np; p++)
        {
            if (float.IsNaN(cage.PointSoftness[p])) cage.PointSoftness[p] = cage.MeanSoftness;
            cage.Kinematic[p] = cage.PointSoftness[p] <= settings.RigidThreshold;
        }

        // Hue-band weights per point, the same way (weighted mean of carried vertices, neighbours fill gaps).
        cage.PointBands = new float[np * NB];
        if (accB != null)
        {
            var known = new bool[np];
            for (int p = 0; p < np; p++)
            {
                if (!(accW[p] > 1e-6)) continue;
                known[p] = true;
                for (int bb = 0; bb < NB; bb++) cage.PointBands[p * NB + bb] = (float)(accB[p * NB + bb] / accW[p]);
            }
            for (int iter = 0; iter < 16; iter++)
            {
                var sum = new float[np * NB]; var cnt = new int[np];
                for (int e = 0; e < cage.Edges.Length; e += 2)
                {
                    int a = cage.Edges[e], b = cage.Edges[e + 1];
                    if (known[b]) { cnt[a]++; for (int bb = 0; bb < NB; bb++) sum[a * NB + bb] += cage.PointBands[b * NB + bb]; }
                    if (known[a]) { cnt[b]++; for (int bb = 0; bb < NB; bb++) sum[b * NB + bb] += cage.PointBands[a * NB + bb]; }
                }
                bool any = false;
                var newly = new List<int>();
                for (int p = 0; p < np; p++)
                {
                    if (known[p]) continue;
                    if (cnt[p] == 0) { any = true; continue; }
                    for (int bb = 0; bb < NB; bb++) cage.PointBands[p * NB + bb] = sum[p * NB + bb] / cnt[p];
                    newly.Add(p);
                }
                foreach (var p in newly) known[p] = true;
                if (!any) break;
            }
        }
        return cage;
    }

    static int CellIndex(Cage c, int i, int j, int k) => i + c.CellsX * (j + c.CellsY * k);

    static int Corner(Cage c, int i, int j, int k, int bits) =>
        c.Index(i + (bits & 1), j + ((bits >> 1) & 1), k + ((bits >> 2) & 1));

    static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    static void Set(ref Vector3 v, int a, float x) { if (a == 0) v.X = x; else if (a == 1) v.Y = x; else v.Z = x; }

    /// <summary>Signed volume of tetrahedron t for the given point positions.</summary>
    public static float TetVolume(ReadOnlySpan<Vector3> pts, int a, int b, int c, int d) =>
        Vector3.Dot(Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]), pts[d] - pts[a]) / 6f;

    public double TotalVolume(ReadOnlySpan<Vector3> pts)
    {
        double v = 0;
        for (int t = 0; t < Tets.Length; t += 4) v += TetVolume(pts, Tets[t], Tets[t + 1], Tets[t + 2], Tets[t + 3]);
        return v;
    }
}
