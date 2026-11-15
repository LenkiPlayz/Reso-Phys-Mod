using System.Numerics;
using Softbody.Core.Cloth;

namespace Softbody.Core.Pop;

/// <summary>
/// 0.34.0: cuts baked meshes into pieces, the Cloth Blast's way. The pieces are the cells of seeded cellular noise
/// (<see cref="ClothBlastMath.Piece"/>), at a size Shred picks in the blast's steps (<see cref="ClothBlastMath.Spacing"/>):
/// from about the whole model's radius (0: a few big pieces) down to confetti (1). A cell whose triangles do not touch
/// (a cell across a hand and a hip) gives a piece for each part. Triangles longer than the pieces are cut first, into a
/// regular grid of smaller ones with every channel carried over from where they sit on the original, so pieces can be
/// smaller than the mesh's own triangles while the source meshes stay as they are. Confetti gets no smaller than
/// <see cref="ConfettiSize"/>, or the surface split MaxPieces ways; when the pieces still come to more than MaxPieces,
/// parts that do not touch stay together, and failing that the pieces grow a step. Plain arithmetic in a fixed order: the
/// same pose and settings give the same pieces on every client.
/// </summary>
public static class PopCut
{
    /// <summary>Metres: the smallest pieces.</summary>
    public const float ConfettiSize = 0.015f;
    /// <summary>Triangles after cutting, at most (a triangle cut into more is cut less).</summary>
    public const int MostTriangles = 400_000;
    /// <summary>Collision points a piece gets at most. A piece with fewer vertices collides with all of them; a bigger one
    /// gets enough that its rim, standing on it, dips no more than <see cref="RimSag"/> between two of them (8 at least).</summary>
    public const int MostPointsPerPiece = 32;
    /// <summary>Collision points a slow piece gets at most (all its vertices, if it has no more).</summary>
    public const int MostSlowPoints = 128;
    /// <summary>Metres: how far a piece's rim may dip between two collision points (the solver keeps 3 mm off).</summary>
    public const float RimSag = 0.003f;
    /// <summary>Metres: how far behind its front face a flake's back face sits (so the two never draw at the same depth).</summary>
    public const float BackOffset = 0.0003f;
    const int MostCuts = 64;
    const int MostTries = 12;
    /// <summary>A piece is about this much of the surface for each (a triangle of side s has 0.433 s squared).</summary>
    const float AreaPerPiece = 0.433f;

    struct SrcTri
    {
        public int Mesh, Sub, A, B, C, N;
    }

    /// <summary>A triangle after cutting: its source triangle and its corners on that triangle's grid (u of A, v of B,
    /// out of the source's N; the rest is C).</summary>
    struct SubTri
    {
        public int Src;
        public byte U0, V0, U1, V1, U2, V2;
    }

    public static PopPieces Cut(IReadOnlyList<PopMesh> meshes, PopCutSettings s)
    {
        var res = new PopPieces();
        int subCount = 0;
        var subBase = new int[meshes.Count + 1];
        for (int m = 0; m < meshes.Count; m++) { subBase[m] = subCount; subCount += meshes[m].Submeshes.Count; }
        subBase[meshes.Count] = subCount;

        // the triangles, their area and bounds
        var src = new List<SrcTri>();
        double area = 0;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            var pos = mesh.Positions;
            for (int si = 0; si < mesh.Submeshes.Count; si++)
            {
                var t = mesh.Submeshes[si];
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    int a = t[k], b = t[k + 1], c = t[k + 2];
                    if ((uint)a >= (uint)pos.Length || (uint)b >= (uint)pos.Length || (uint)c >= (uint)pos.Length) continue;
                    var pa = pos[a]; var pb = pos[b]; var pc = pos[c];
                    if (!float.IsFinite(pa.X + pa.Y + pa.Z + pb.X + pb.Y + pb.Z + pc.X + pc.Y + pc.Z)) continue;
                    float ar = 0.5f * Vector3.Cross(pb - pa, pc - pa).Length();
                    if (!(ar > 1e-12f)) continue;
                    src.Add(new SrcTri { Mesh = m, Sub = si, A = a, B = b, C = c, N = 1 });
                    area += ar;
                    lo = Vector3.Min(lo, Vector3.Min(pa, Vector3.Min(pb, pc)));
                    hi = Vector3.Max(hi, Vector3.Max(pa, Vector3.Max(pb, pc)));
                }
            }
        }
        res.SourceTriangles = src.Count;
        res.Area = (float)area;
        for (int k = 0; k < subCount; k++) res.Submeshes.Add(Array.Empty<int>());
        if (src.Count == 0) return res;

        // the sizes: from the model's radius down to confetti
        int most = Math.Clamp(s.MaxPieces, 1, 100_000);
        float big = MathF.Max(0.5f * (hi - lo).Length(), ConfettiSize);
        float small = MathF.Min(MathF.Max(ConfettiSize, MathF.Sqrt((float)area / (most * AreaPerPiece))), big);
        float spacing = ClothBlastMath.Spacing(small, big, s.Shred);
        if (!(spacing > 0f)) spacing = small;

        // the weld (seams count as touching): positions on a grid from the bounds' corner, 21 bits a side
        float extent = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z));
        float weld = MathF.Max(1e-4f, extent / (1 << 20));
        res.WeldFrom = lo; res.WeldSize = weld;

        var subs = new List<SubTri>();
        int[] piece = Array.Empty<int>();
        int pieces = 0, tries = 0;
        float cutTo = 0f;
        var cells = new Dictionary<long, int>();
        var welds = new Dictionary<long, int>();
        var first = new Dictionary<long, int>();
        var rootPiece = new Dictionary<int, int>();
        int[] cellOf = Array.Empty<int>(), parent = Array.Empty<int>();
        int[] cornerWeld = Array.Empty<int>();
        while (true)
        {
            tries++;
            // cut: triangles longer than the pieces, into a grid (fewer cuts if they would come to too many)
            cutTo = spacing;
            long total;
            while (true)
            {
                total = 0;
                for (int k = 0; k < src.Count; k++)
                {
                    var t = src[k];
                    int n = Cuts(meshes[t.Mesh].Positions, t, cutTo);
                    total += (long)n * n;
                }
                if (total <= MostTriangles) break;
                cutTo *= 1.25f;
            }
            subs.Clear();
            bool anyCut = false;
            for (int k = 0; k < src.Count; k++)
            {
                var t = src[k];
                int n = Cuts(meshes[t.Mesh].Positions, t, cutTo);
                t.N = n; src[k] = t;
                if (n > 1) anyCut = true;
                for (int u = 0; u < n; u++)
                    for (int v = 0; u + v < n; v++)
                    {
                        subs.Add(new SubTri { Src = k, U0 = (byte)u, V0 = (byte)v, U1 = (byte)(u + 1), V1 = (byte)v, U2 = (byte)u, V2 = (byte)(v + 1) });
                        if (u + v + 2 <= n)
                            subs.Add(new SubTri { Src = k, U0 = (byte)(u + 1), V0 = (byte)v, U1 = (byte)(u + 1), V1 = (byte)(v + 1), U2 = (byte)u, V2 = (byte)(v + 1) });
                    }
            }
            if (!anyCut) cutTo = 0f;

            // the cells
            int ns = subs.Count;
            if (cellOf.Length < ns) { cellOf = new int[ns]; parent = new int[ns]; piece = new int[ns]; cornerWeld = new int[3 * ns]; }
            cells.Clear();
            for (int k = 0; k < ns; k++)
            {
                var st = subs[k];
                var t = src[st.Src];
                var p = meshes[t.Mesh].Positions;
                var c = (Grid(p, t, st.U0, st.V0) + Grid(p, t, st.U1, st.V1) + Grid(p, t, st.U2, st.V2)) * (1f / 3f);
                long label = ClothBlastMath.Piece(c, spacing, s.Seed);
                if (!cells.TryGetValue(label, out int ci)) { ci = cells.Count; cells.Add(label, ci); }
                cellOf[k] = ci;
            }
            if (cells.Count > most && tries < MostTries) { spacing *= 1.25f; continue; }

            // the parts of each cell that touch (through vertices at the same place)
            welds.Clear(); first.Clear(); rootPiece.Clear();
            for (int k = 0; k < ns; k++)
            {
                parent[k] = k;
                var st = subs[k];
                var t = src[st.Src];
                var p = meshes[t.Mesh].Positions;
                cornerWeld[3 * k] = WeldId(welds, Grid(p, t, st.U0, st.V0), lo, weld);
                cornerWeld[3 * k + 1] = WeldId(welds, Grid(p, t, st.U1, st.V1), lo, weld);
                cornerWeld[3 * k + 2] = WeldId(welds, Grid(p, t, st.U2, st.V2), lo, weld);
            }
            long wn = Math.Max(1, welds.Count);
            for (int k = 0; k < ns; k++)
                for (int j = 0; j < 3; j++)
                {
                    long key = cellOf[k] * wn + cornerWeld[3 * k + j];
                    if (first.TryGetValue(key, out int other)) Union(parent, k, other);
                    else first.Add(key, k);
                }
            pieces = 0;
            for (int k = 0; k < ns; k++)
            {
                int r = Find(parent, k);
                if (!rootPiece.TryGetValue(r, out int pi)) { pi = pieces++; rootPiece.Add(r, pi); }
                piece[k] = pi;
            }
            if (pieces > most)
            {
                // too many parts (hair cards, say): each cell stays one piece
                pieces = cells.Count;
                for (int k = 0; k < ns; k++) piece[k] = cellOf[k];
            }
            break;
        }
        res.Tries = tries;
        res.Spacing = spacing;
        res.CutTo = cutTo;
        Emit(meshes, src, subs, piece, pieces, subBase, res);
        return res;
    }

    /// <summary>How many parts each side of triangle t is cut into: enough that none is longer than cutTo.</summary>
    static int Cuts(Vector3[] p, in SrcTri t, float cutTo)
    {
        var a = p[t.A]; var b = p[t.B]; var c = p[t.C];
        float l = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
        if (!(l > cutTo) || !(cutTo > 0f)) return 1;
        float n = MathF.Ceiling(l / cutTo);
        return n >= MostCuts ? MostCuts : Math.Max(1, (int)n);
    }

    static void Weights(int n, int u, int v, out float wa, out float wb, out float wc)
    {
        if (u == n) { wa = 1f; wb = 0f; wc = 0f; return; }
        if (v == n) { wa = 0f; wb = 1f; wc = 0f; return; }
        if (u == 0 && v == 0) { wa = 0f; wb = 0f; wc = 1f; return; }
        wa = u / (float)n; wb = v / (float)n; wc = 1f - wa - wb;
    }

    static Vector3 Grid(Vector3[] p, in SrcTri t, int u, int v)
    {
        Weights(t.N, u, v, out float wa, out float wb, out float wc);
        return p[t.A] * wa + p[t.B] * wb + p[t.C] * wc;
    }

    static int WeldId(Dictionary<long, int> welds, Vector3 p, Vector3 lo, float weld)
    {
        long key = WeldKey(p, lo, weld);
        if (!welds.TryGetValue(key, out int id)) { id = welds.Count; welds.Add(key, id); }
        return id;
    }

    /// <summary>0.35.0: the weld's cell of p (shared with the soft pieces).</summary>
    internal static long WeldKey(Vector3 p, Vector3 lo, float weld)
    {
        var q = (p - lo) / weld;
        long x = (long)MathF.Floor(q.X) & 0x1FFFFF, y = (long)MathF.Floor(q.Y) & 0x1FFFFF, z = (long)MathF.Floor(q.Z) & 0x1FFFFF;
        return (x << 42) | (y << 21) | z;
    }

    static int Find(int[] parent, int k)
    {
        while (parent[k] != k) { parent[k] = parent[parent[k]]; k = parent[k]; }
        return k;
    }

    static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    /// <summary>The pieces' mesh: piece by piece, its vertices (each grid point once per piece; a source vertex is shared
    /// by the piece's triangles), its collision points and origin; then the back faces.</summary>
    static void Emit(IReadOnlyList<PopMesh> meshes, List<SrcTri> src, List<SubTri> subs, int[] piece, int pieces, int[] subBase, PopPieces res)
    {
        int ns = subs.Count;
        // the triangles by piece (in their order within each)
        var start = new int[pieces + 1];
        for (int k = 0; k < ns; k++) start[piece[k] + 1]++;
        for (int p = 0; p < pieces; p++) start[p + 1] += start[p];
        var order = new int[ns];
        var fill = (int[])start.Clone();
        for (int k = 0; k < ns; k++) order[fill[piece[k]]++] = k;

        int uvCount = 0;
        bool anyTangents = false, anyColors = false;
        foreach (var m in meshes)
        {
            uvCount = Math.Max(uvCount, m.Uvs.Count);
            anyTangents |= m.Tangents != null;
            anyColors |= m.Colors != null;
        }
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tan = new List<Vector4>();
        var col = anyColors ? new List<Vector4>() : null;
        var uvs = new List<Vector4>[uvCount];
        for (int c = 0; c < uvCount; c++) uvs[c] = new List<Vector4>();
        var lists = new List<int>[subBase[meshes.Count]];
        for (int k = 0; k < lists.Length; k++) lists[k] = new List<int>();

        res.Count = pieces;
        res.Origin = new Vector3[pieces]; res.Radius = new float[pieces];
        res.VertexStart = new int[pieces]; res.VertexCount = new int[pieces];
        res.PointStart = new int[pieces]; res.PointCount = new int[pieces]; res.SlowPointCount = new int[pieces];
        var points = new List<Vector3>();
        var at = new Dictionary<long, int>();
        var chosen = new int[MostSlowPoints];
        var near = new List<float>();
        var ptri = new List<int>();
        var weldAt = new Dictionary<(int, int, int), int>();
        var weldOf = new List<int>();
        var edges = new Dictionary<long, int>();
        var rim = new List<bool>();
        var onRim = new HashSet<int>();
        for (int p = 0; p < pieces; p++)
        {
            at.Clear(); ptri.Clear();
            int v0 = pos.Count;
            for (int o = start[p]; o < start[p + 1]; o++)
            {
                var st = subs[order[o]];
                var t = src[st.Src];
                int list = subBase[t.Mesh] + t.Sub;
                int i0 = Vertex(meshes[t.Mesh], st.Src, t, st.U0, st.V0, at, pos, nrm, tan, col, uvs);
                int i1 = Vertex(meshes[t.Mesh], st.Src, t, st.U1, st.V1, at, pos, nrm, tan, col, uvs);
                int i2 = Vertex(meshes[t.Mesh], st.Src, t, st.U2, st.V2, at, pos, nrm, tan, col, uvs);
                lists[list].Add(i0); lists[list].Add(i1); lists[list].Add(i2);
                ptri.Add(i0); ptri.Add(i1); ptri.Add(i2);
            }
            int vc = pos.Count - v0;
            res.VertexStart[p] = v0; res.VertexCount[p] = vc;
            Rim(pos, ptri, v0, vc, weldAt, weldOf, edges, onRim, rim);

            // collision points: the farthest out along the piece's mean normal both ways (the bulge it lies on convex side
            // down, and the rim it lies on the other way up), then each the farthest from those chosen, round the rim
            // first: all of a small piece's vertices, enough of a big one's that its rim dips at most RimSag between them
            // (a rim of radius p with k - 2 points on it sags about 4.93 p / (k - 2) squared)
            var mean = Vector3.Zero; var nsum = Vector3.Zero;
            for (int v = v0; v < v0 + vc; v++) { mean += pos[v]; nsum += nrm[v]; }
            mean /= Math.Max(1, vc);
            float far = 0f;
            for (int v = v0; v < v0 + vc; v++) far = MathF.Max(far, Vector3.DistanceSquared(pos[v], mean));
            far = MathF.Sqrt(far);
            float need = 2f + MathF.Ceiling(MathF.Sqrt(4.93f * far / RimSag));
            int fast = Math.Min(need < MostPointsPerPiece ? Math.Max(8, (int)need) : MostPointsPerPiece, vc);
            int k = Math.Min(MostSlowPoints, vc);
            near.Clear();
            for (int v = v0; v < v0 + vc; v++) near.Add(float.MaxValue);
            int got = 0;
            float nl = nsum.Length();
            if (nl > 1e-3f * vc && fast >= 3)
            {
                var n = nsum / nl;
                int up = v0, down = v0;
                float hu = float.MinValue, hd = float.MaxValue;
                for (int v = v0; v < v0 + vc; v++)
                {
                    float d = Vector3.Dot(pos[v] - mean, n);
                    if (d > hu) { hu = d; up = v; }
                    if (d < hd) { hd = d; down = v; }
                }
                chosen[got++] = up;
                if (down != up) chosen[got++] = down;
            }
            if (got == 0)
            {
                int best = v0; float bd = -1f;
                for (int v = v0; v < v0 + vc; v++) { float d = Vector3.DistanceSquared(pos[v], mean); if (d > bd) { bd = d; best = v; } }
                chosen[got++] = best;
            }
            for (int j = 0; j < got; j++) Closer(pos, near, v0, vc, pos[chosen[j]]);
            for (int round = 0; round < 2 && got < k; round++)
                while (got < k)
                {
                    int next = -1; float bd = 0f;
                    for (int v = v0; v < v0 + vc; v++) if ((round == 1 || rim[v - v0]) && near[v - v0] > bd) { bd = near[v - v0]; next = v; }
                    if (next < 0 || !(bd > 1e-12f)) break;   // the rest (of the rim) sit on points already chosen
                    chosen[got++] = next;
                    Closer(pos, near, v0, vc, pos[next]);
                }
            // the fast ones are the first chosen: about the origin
            int nf = Math.Min(fast, got);
            var origin = Vector3.Zero;
            for (int j = 0; j < nf; j++) origin += pos[chosen[j]];
            origin /= Math.Max(1, nf);
            res.PointStart[p] = points.Count; res.PointCount[p] = nf; res.SlowPointCount[p] = got;
            for (int j = 0; j < got; j++) points.Add(pos[chosen[j]] - origin);
            float r = 0f;
            for (int v = v0; v < v0 + vc; v++) r = MathF.Max(r, Vector3.Distance(pos[v], origin));
            res.Origin[p] = origin; res.Radius[p] = r;
        }
        res.Points = points.ToArray();

        // the back faces: every front vertex again, a little behind, facing the other way
        int f = pos.Count;
        res.FrontVertices = f;
        for (int v = 0; v < f; v++)
        {
            var n = nrm[v];
            pos.Add(pos[v] - n * BackOffset);
            nrm.Add(-n);
            var tv = tan[v];
            tan.Add(new Vector4(tv.X, tv.Y, tv.Z, -tv.W));
            col?.Add(col[v]);
            for (int c = 0; c < uvCount; c++) uvs[c].Add(uvs[c][v]);
        }
        int tris = 0;
        for (int k = 0; k < lists.Length; k++)
        {
            var l = lists[k];
            int front = l.Count;
            tris += front / 3;
            for (int j = 0; j + 2 < front; j += 3) { l.Add(l[j] + f); l.Add(l[j + 2] + f); l.Add(l[j + 1] + f); }
            res.Submeshes[k] = l.ToArray();
        }
        res.Triangles = tris;
        res.Positions = pos.ToArray(); res.Normals = nrm.ToArray(); res.Tangents = tan.ToArray();
        res.HasTangents = anyTangents;
        res.Colors = col?.ToArray();
        for (int c = 0; c < uvCount; c++) res.Uvs.Add(uvs[c].ToArray());
    }

    /// <summary>Which of a piece's vertices are on its rim: on an edge only one of its triangles has, by place (so the
    /// seams the cut leaves inside a piece, where two cut triangles meet, do not count).</summary>
    static void Rim(List<Vector3> pos, List<int> tris, int v0, int vc, Dictionary<(int, int, int), int> weldAt, List<int> weldOf,
                    Dictionary<long, int> edges, HashSet<int> onRim, List<bool> rim)
    {
        weldAt.Clear(); weldOf.Clear(); edges.Clear(); onRim.Clear(); rim.Clear();
        for (int v = v0; v < v0 + vc; v++)
        {
            var q = pos[v] * 1e5f;
            var key = ((int)MathF.Round(q.X), (int)MathF.Round(q.Y), (int)MathF.Round(q.Z));
            if (!weldAt.TryGetValue(key, out int id)) { id = weldAt.Count; weldAt.Add(key, id); }
            weldOf.Add(id);
            rim.Add(false);
        }
        for (int k = 0; k + 2 < tris.Count; k += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = weldOf[tris[k + j] - v0], b = weldOf[tris[k + (j + 1) % 3] - v0];
                if (a == b) continue;
                long e = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                edges[e] = edges.TryGetValue(e, out int n) ? n + 1 : 1;
            }
        foreach (var pair in edges)
            if (pair.Value == 1) { onRim.Add((int)(pair.Key >> 32)); onRim.Add((int)(pair.Key & 0xFFFFFFFFL)); }
        for (int v = 0; v < vc; v++) rim[v] = onRim.Contains(weldOf[v]);
    }

    /// <summary>Each vertex's squared distance to the nearest chosen point, with c chosen too.</summary>
    static void Closer(List<Vector3> pos, List<float> near, int v0, int vc, Vector3 c)
    {
        for (int v = v0; v < v0 + vc; v++)
        {
            float d = Vector3.DistanceSquared(pos[v], c);
            if (d < near[v - v0]) near[v - v0] = d;
        }
    }

    /// <summary>The piece's vertex at grid point (u, v) of source triangle t: a source corner is shared by every triangle
    /// of the piece that has it; any other point by the triangles cut from the same source triangle.</summary>
    static int Vertex(PopMesh m, int srcIndex, in SrcTri t, int u, int v, Dictionary<long, int> at, List<Vector3> pos, List<Vector3> nrm,
                      List<Vector4> tan, List<Vector4> col, List<Vector4>[] uvs)
    {
        int n = t.N;
        long key;
        long mesh = (1L << 62) | ((long)t.Mesh << 32);
        if (u == n) key = mesh | (uint)t.A;
        else if (v == n) key = mesh | (uint)t.B;
        else if (u == 0 && v == 0) key = mesh | (uint)t.C;
        else key = ((long)srcIndex << 16) | ((long)u << 8) | (long)v;
        if (at.TryGetValue(key, out int i)) return i;
        i = pos.Count;
        at.Add(key, i);
        Weights(n, u, v, out float wa, out float wb, out float wc);
        var p = m.Positions;
        pos.Add(p[t.A] * wa + p[t.B] * wb + p[t.C] * wc);
        var face = Vector3.Cross(p[t.B] - p[t.A], p[t.C] - p[t.A]);
        float fl = face.Length();
        face = fl > 1e-20f ? face / fl : Vector3.UnitY;
        if (m.Normals != null && m.Normals.Length == p.Length)
        {
            var nn = m.Normals[t.A] * wa + m.Normals[t.B] * wb + m.Normals[t.C] * wc;
            float l = nn.Length();
            nrm.Add(l > 1e-12f && float.IsFinite(l) ? nn / l : face);
        }
        else nrm.Add(face);
        if (m.Tangents != null && m.Tangents.Length == p.Length)
        {
            var ta = m.Tangents[t.A]; var tb = m.Tangents[t.B]; var tc = m.Tangents[t.C];
            var tt = new Vector3(ta.X, ta.Y, ta.Z) * wa + new Vector3(tb.X, tb.Y, tb.Z) * wb + new Vector3(tc.X, tc.Y, tc.Z) * wc;
            float l = tt.Length();
            tt = l > 1e-12f && float.IsFinite(l) ? tt / l : Vector3.UnitX;
            tan.Add(new Vector4(tt, ta.W < 0f ? -1f : 1f));
        }
        else tan.Add(new Vector4(1f, 0f, 0f, 1f));
        if (col != null)
        {
            if (m.Colors != null && m.Colors.Length == p.Length) col.Add(m.Colors[t.A] * wa + m.Colors[t.B] * wb + m.Colors[t.C] * wc);
            else col.Add(Vector4.One);
        }
        for (int c = 0; c < uvs.Length; c++)
        {
            var ch = c < m.Uvs.Count ? m.Uvs[c] : null;
            uvs[c].Add(ch != null && ch.Length == p.Length ? ch[t.A] * wa + ch[t.B] * wb + ch[t.C] * wc : Vector4.Zero);
        }
        return i;
    }
}
