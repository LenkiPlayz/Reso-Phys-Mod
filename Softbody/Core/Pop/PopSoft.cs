using System.Numerics;

namespace Softbody.Core.Pop;

/// <summary>
/// 0.35.0: a cut's pieces as little cloths (DESIGN-pop.md, "Soft pieces"). Each piece gets a few particles spread over
/// it: the points of its surface farthest apart along the surface, about <see cref="Spacing"/> apart and at least 4 (two
/// triangles that can fold; fewer only on a piece with fewer points), each standing for the part of the piece nearest to
/// it along the surface. Two particles whose parts touch keep their
/// distance (an edge: the cloth's stretch); three whose parts meet make a triangle (the air pushes on it); the two corners
/// across an edge that two triangles share keep theirs as far as Stiffness asks (the cloth's bending). The piece's own
/// vertices follow the three particles of the triangle they lie on, each moving and turning them by how near it is, so a
/// piece bends smoothly between its particles and keeps its look: its triangles, UVs and every other channel stay as cut.
/// </summary>
public sealed class PopSoftPieces
{
    /// <summary>Metres between neighbouring particles, about.</summary>
    public float Spacing;
    /// <summary>Per piece, and one more at the end: where its particles, edges, bending pairs and triangles start.</summary>
    public int[] PStart = { 0 }, EStart = { 0 }, BStart = { 0 }, TStart = { 0 };
    /// <summary>Per piece: the middle of its particles by the area each stands for, as cut (its particles' and vertices'
    /// rest places are from there), and the farthest of its vertices from there.</summary>
    public Vector3[] Middle = Array.Empty<Vector3>();
    public float[] Radius = Array.Empty<float>();
    /// <summary>Per piece: its area over its particles' triangles' (the air pushes on all of it: a piece's rim can lie
    /// outside its particles' triangles), 1 to 3.</summary>
    public float[] AirScale = Array.Empty<float>();
    /// <summary>Per particle: its place as cut, from its piece's middle, and the area it stands for (m²).</summary>
    public Vector3[] Rest = Array.Empty<Vector3>();
    public float[] Area = Array.Empty<float>();
    /// <summary>Per particle: the triangles it is a corner of (from IncStart[p] to IncStart[p + 1] in Inc), and the
    /// particle whose turn it takes when it is on none (itself when it is on one).</summary>
    public int[] IncStart = { 0 }, Inc = Array.Empty<int>(), Donor = Array.Empty<int>();
    /// <summary>The edges (stretch) and the bending pairs: two particles each, and their distance as cut.</summary>
    public int[] EA = Array.Empty<int>(), EB = Array.Empty<int>();
    public float[] EL = Array.Empty<float>();
    public int[] BA = Array.Empty<int>(), BB = Array.Empty<int>();
    public float[] BL = Array.Empty<float>();
    /// <summary>Three particles per triangle, wound as the piece's front.</summary>
    public int[] Tri = Array.Empty<int>();
    /// <summary>Per front vertex of the cut: the three particles it follows (SkinP[3v..3v+2]) and how much (adding up to 1).</summary>
    public int[] SkinP = Array.Empty<int>();
    public float[] SkinW = Array.Empty<float>();

    public int Count => Middle.Length;
    public int Particles => Rest.Length;
    public int Triangles => Tri.Length / 3;
    public int Edges => EA.Length;
    public int Bends => BA.Length;
}

/// <summary>0.35.0: builds the soft pieces of a cut (off the world thread, after <see cref="PopCut.Cut"/>).</summary>
public static class PopSoft
{
    /// <summary>Particles a cut gets about at most: the spacing grows until they fit (only a cut of more than a quarter as
    /// many pieces gets more, 4 each).</summary>
    public const int MostParticles = 6000;
    /// <summary>Particles one piece gets at most, and at least (where it has that many points).</summary>
    public const int MostPerPiece = 48, MostAtLeast = 4;
    /// <summary>Metres: the spacing is about a piece's size over <see cref="AcrossAPiece"/>, kept between these (and wider
    /// where <see cref="MostParticles"/> asks for it).</summary>
    public const float SmallestSpacing = 0.01f, LargestSpacing = 0.04f;
    const float AcrossAPiece = 2.5f;
    /// <summary>A piece gets particles until none of its points is further than this times the spacing from one, along
    /// the surface.</summary>
    const float Cover = 0.6f;
    /// <summary>A triangle of particles smaller than this times the spacing squared is left out (a sliver turns badly).</summary>
    const float SmallestTriangle = 0.01f;

    public static PopSoftPieces Build(PopPieces p, int most = MostParticles)
    {
        int n = p.Count, f = p.FrontVertices;
        var res = new PopSoftPieces();
        var pos = p.Positions;

        // each piece's front triangles
        var pieceOf = new int[f];
        for (int i = 0; i < n; i++)
            for (int v = p.VertexStart[i], e = v + p.VertexCount[i]; v < e; v++) pieceOf[v] = i;
        var tStart = new int[n + 1];
        foreach (var sub in p.Submeshes)
            for (int k = 0; k + 2 < sub.Length / 2; k += 3) tStart[pieceOf[sub[k]] + 1]++;
        for (int i = 0; i < n; i++) tStart[i + 1] += tStart[i];
        var ptri = new int[3 * tStart[n]];
        var fill = (int[])tStart.Clone();
        foreach (var sub in p.Submeshes)
            for (int k = 0; k + 2 < sub.Length / 2; k += 3)
            {
                int at = 3 * fill[pieceOf[sub[k]]]++;
                ptri[at] = sub[k]; ptri[at + 1] = sub[k + 1]; ptri[at + 2] = sub[k + 2];
            }

        // the spacing: a few particles across a piece, no more than the cap
        double area = 0;
        for (int t = 0; t < tStart[n]; t++) area += Area(pos[ptri[3 * t]], pos[ptri[3 * t + 1]], pos[ptri[3 * t + 2]]);
        float cap = MathF.Sqrt((float)area / (0.866f * Math.Max(1, most)));
        float s = MathF.Max(cap, Math.Clamp(p.Spacing / AcrossAPiece, SmallestSpacing, LargestSpacing));
        if (!(s > 0f) || !float.IsFinite(s)) s = LargestSpacing;
        res.Spacing = s;

        res.PStart = new int[n + 1]; res.EStart = new int[n + 1]; res.BStart = new int[n + 1]; res.TStart = new int[n + 1];
        res.Middle = new Vector3[n]; res.Radius = new float[n]; res.AirScale = new float[n];
        res.SkinP = new int[3 * f]; res.SkinW = new float[3 * f];
        var rest = new List<Vector3>(); var parea = new List<float>();
        var ea = new List<int>(); var eb = new List<int>(); var el = new List<float>();
        var ba = new List<int>(); var bb = new List<int>(); var bl = new List<float>();
        var tri = new List<int>();

        // scratch, per piece
        var weld = new Dictionary<long, int>();
        var weldFrom = p.WeldFrom;
        float weldSize = p.WeldSize;
        if (!(weldSize > 0f) || !float.IsFinite(weldSize))
        {
            // pieces not from PopCut.Cut: a weld as it would make
            var blo = new Vector3(float.MaxValue); var bhi = new Vector3(float.MinValue);
            for (int v = 0; v < f; v++) { blo = Vector3.Min(blo, pos[v]); bhi = Vector3.Max(bhi, pos[v]); }
            float ext = f > 0 ? MathF.Max(bhi.X - blo.X, MathF.Max(bhi.Y - blo.Y, bhi.Z - blo.Z)) : 0f;
            weldFrom = f > 0 ? blo : Vector3.Zero; weldSize = MathF.Max(1e-4f, ext / (1 << 20));
        }
        var nodeOf = new List<int>(); var nodePos = new List<Vector3>();
        var nodeEdges = new List<(int A, int B)>(); var edgeSet = new HashSet<long>();
        var seeds = new List<int>();
        var pEdges = new List<(int A, int B)>(); var pEdgeSet = new HashSet<long>(); var ringPairs = new HashSet<long>();
        var pTris = new List<(int A, int B, int C)>(); var pTriSet = new HashSet<long>();
        var opposite = new Dictionary<long, int>();
        var pq = new PriorityQueue<int, float>();
        float[] nArea = Array.Empty<float>(), dist = Array.Empty<float>(), adjLen = Array.Empty<float>();
        int[] owner = Array.Empty<int>(), adjStart = Array.Empty<int>(), adj = Array.Empty<int>();
        float[] kArea = new float[MostPerPiece + 1];
        var kRest = new Vector3[MostPerPiece + 1];
        int[] incStart = new int[MostPerPiece + 2], inc = Array.Empty<int>(), nbStart = new int[MostPerPiece + 2], nbs = Array.Empty<int>();

        for (int i = 0; i < n; i++)
        {
            int v0 = p.VertexStart[i], vc = p.VertexCount[i], t0 = tStart[i], t1 = tStart[i + 1];
            int pBase = rest.Count;
            res.PStart[i] = pBase; res.EStart[i] = ea.Count; res.BStart[i] = ba.Count; res.TStart[i] = tri.Count / 3;

            // the nodes: the piece's vertices welded by place as the cut welds them (UV seams, and parts the cut found
            // touching, meet there)
            weld.Clear(); nodeOf.Clear(); nodePos.Clear();
            for (int v = v0; v < v0 + vc; v++)
            {
                long key = PopCut.WeldKey(pos[v], weldFrom, weldSize);
                if (!weld.TryGetValue(key, out int id)) { id = nodePos.Count; weld.Add(key, id); nodePos.Add(pos[v]); }
                nodeOf.Add(id);
            }
            int nn = nodePos.Count;
            if (nArea.Length < nn) { int c = Math.Max(nn, 2 * nArea.Length); nArea = new float[c]; dist = new float[c]; owner = new int[c]; adjStart = new int[c + 1]; }
            Array.Clear(nArea, 0, nn);
            nodeEdges.Clear(); edgeSet.Clear();
            float pieceArea = 0f;
            for (int t = t0; t < t1; t++)
            {
                int a = nodeOf[ptri[3 * t] - v0], b = nodeOf[ptri[3 * t + 1] - v0], c = nodeOf[ptri[3 * t + 2] - v0];
                float ar = Area(pos[ptri[3 * t]], pos[ptri[3 * t + 1]], pos[ptri[3 * t + 2]]);
                pieceArea += ar;
                nArea[a] += ar / 3f; nArea[b] += ar / 3f; nArea[c] += ar / 3f;
                AddEdge(nodeEdges, edgeSet, a, b); AddEdge(nodeEdges, edgeSet, b, c); AddEdge(nodeEdges, edgeSet, c, a);
            }
            // the nodes' neighbours, with how far
            Array.Clear(adjStart, 0, nn + 1);
            foreach (var (a, b) in nodeEdges) { adjStart[a + 1]++; adjStart[b + 1]++; }
            for (int u = 0; u < nn; u++) adjStart[u + 1] += adjStart[u];
            if (adj.Length < adjStart[nn]) { int c = Math.Max(adjStart[nn], 2 * adj.Length); adj = new int[c]; adjLen = new float[c]; }
            {
                var at = new int[nn];
                Array.Copy(adjStart, at, nn);
                foreach (var (a, b) in nodeEdges)
                {
                    float d = Vector3.Distance(nodePos[a], nodePos[b]);
                    adj[at[a]] = b; adjLen[at[a]++] = d;
                    adj[at[b]] = a; adjLen[at[b]++] = d;
                }
            }

            // the particles: the nodes farthest apart along the surface, the first one on the rim (the farthest from the
            // piece's middle), until every node is within Cover spacings of one (each part of a piece that does not touch
            // the rest, hair cards together in one piece, gets its own)
            for (int u = 0; u < nn; u++) { dist[u] = float.PositiveInfinity; owner[u] = -1; }
            seeds.Clear();
            var mid = Vector3.Zero;
            for (int u = 0; u < nn; u++) mid += nodePos[u];
            mid /= Math.Max(1, nn);
            int next = 0;
            float fd = -1f;
            for (int u = 0; u < nn; u++) { float d = Vector3.DistanceSquared(nodePos[u], mid); if (d > fd) { fd = d; next = u; } }
            int need = Math.Min(MostAtLeast, nn);
            while (nn > 0)
            {
                seeds.Add(next);
                Spread(next, seeds.Count - 1, adjStart, adj, adjLen, dist, owner, pq);
                if (seeds.Count >= MostPerPiece || seeds.Count >= nn) break;
                int far = -1; float fdist = -1f;
                for (int u = 0; u < nn; u++) if (dist[u] > fdist) { fdist = dist[u]; far = u; }
                if (far < 0 || !(fdist > 0f)) break;
                if (seeds.Count >= need && fdist <= Cover * s) break;
                next = far;
            }
            // nodes no particle reached (parts of the piece that touch the rest nowhere, past the most particles a piece
            // gets: hair cards the cut kept in one piece) go with the nearest particle
            for (int u = 0; u < nn; u++)
            {
                if (owner[u] >= 0) continue;
                int best = 0; float bd = float.MaxValue;
                for (int a = 0; a < seeds.Count; a++) { float d = Vector3.DistanceSquared(nodePos[u], nodePos[seeds[a]]); if (d < bd) { bd = d; best = a; } }
                owner[u] = best;
            }

            // their edges and triangles; a piece whose parts meet nowhere three at a time (a strip) gets its best triangle,
            // and one whose particles all lie on a line another particle first
            for (int attempt = 0; ; attempt++)
            {
                int k = seeds.Count;
                pEdges.Clear(); pEdgeSet.Clear(); pTris.Clear(); pTriSet.Clear();
                foreach (var (a, b) in nodeEdges)
                {
                    int oa = owner[a], ob = owner[b];
                    if (oa == ob || oa < 0 || ob < 0) continue;
                    AddEdge(pEdges, pEdgeSet, oa, ob);
                }
                for (int t = t0; t < t1; t++)
                {
                    int oa = owner[nodeOf[ptri[3 * t] - v0]], ob = owner[nodeOf[ptri[3 * t + 1] - v0]], oc = owner[nodeOf[ptri[3 * t + 2] - v0]];
                    if (oa < 0 || ob < 0 || oc < 0 || oa == ob || ob == oc || oc == oa) continue;
                    if (!pTriSet.Add(TriKey(oa, ob, oc))) continue;
                    if (!(Area(nodePos[seeds[oa]], nodePos[seeds[ob]], nodePos[seeds[oc]]) > SmallestTriangle * s * s)) continue;
                    pTris.Add((oa, ob, oc));
                }
                if (pTris.Count > 0 || k < 3) break;
                // the biggest triangle of particles
                int ba0 = -1, bb0 = -1, bc0 = -1;
                float best = 0f;
                for (int a = 0; a < k; a++)
                    for (int b = a + 1; b < k; b++)
                        for (int c = b + 1; c < k; c++)
                        {
                            float ar = Area(nodePos[seeds[a]], nodePos[seeds[b]], nodePos[seeds[c]]);
                            if (ar > best) { best = ar; ba0 = a; bb0 = b; bc0 = c; }
                        }
                float span = 0f;
                for (int a = 0; a < k; a++) for (int b = a + 1; b < k; b++) span = MathF.Max(span, Vector3.Distance(nodePos[seeds[a]], nodePos[seeds[b]]));
                if (best > 1e-3f * span * span || attempt > 0 || k >= nn || k >= MostPerPiece)
                {
                    if (ba0 >= 0 && best > 1e-12f)
                    {
                        // wound as the piece's front: by the normals of the nodes there
                        var nrm = Vector3.Cross(nodePos[seeds[bb0]] - nodePos[seeds[ba0]], nodePos[seeds[bc0]] - nodePos[seeds[ba0]]);
                        if (Vector3.Dot(nrm, FrontNormal(p, v0, vc)) < 0f) (bb0, bc0) = (bc0, bb0);
                        pTris.Add((ba0, bb0, bc0));
                        AddEdge(pEdges, pEdgeSet, ba0, bb0); AddEdge(pEdges, pEdgeSet, bb0, bc0); AddEdge(pEdges, pEdgeSet, bc0, ba0);
                    }
                    break;
                }
                // all on a line: the node farthest from it too
                int s0 = 0, s1 = 1;
                float sd = -1f;
                for (int a = 0; a < k; a++) for (int b = a + 1; b < k; b++) { float d = Vector3.DistanceSquared(nodePos[seeds[a]], nodePos[seeds[b]]); if (d > sd) { sd = d; s0 = a; s1 = b; } }
                var la = nodePos[seeds[s0]]; var ld = nodePos[seeds[s1]] - la;
                float ll = ld.LengthSquared();
                int off = -1; float od = -1f;
                for (int u = 0; u < nn; u++)
                {
                    var r = nodePos[u] - la;
                    var perp = ll > 0f ? r - ld * (Vector3.Dot(r, ld) / ll) : r;
                    float d = perp.LengthSquared();
                    if (d > od) { od = d; off = u; }
                }
                if (off < 0 || !(od > 0f)) break;
                seeds.Add(off);
                Spread(off, seeds.Count - 1, adjStart, adj, adjLen, dist, owner, pq);
            }

            // a piece holds together: particles its edges leave apart (parts of it that touch nowhere) are tied to the
            // nearest particle of another part
            LinkParticles(seeds, nodePos, pEdges, pEdgeSet);

            // the particles: places, areas (a part with next to no area still weighs a little), the piece's middle
            int kk = seeds.Count;
            float sumA = 0f;
            for (int a = 0; a < kk; a++) kArea[a] = 0f;
            for (int u = 0; u < nn; u++) if (owner[u] >= 0) kArea[owner[u]] += nArea[u];
            for (int a = 0; a < kk; a++) sumA += kArea[a];
            float floor = 0.05f * MathF.Max(sumA, 1e-8f) / Math.Max(1, kk);
            var middle = Vector3.Zero;
            float wsum = 0f;
            for (int a = 0; a < kk; a++)
            {
                kArea[a] = MathF.Max(kArea[a], floor);
                kRest[a] = nodePos[seeds[a]];
                middle += kRest[a] * kArea[a];
                wsum += kArea[a];
            }
            middle = wsum > 0f ? middle / wsum : mid;
            res.Middle[i] = middle;
            for (int a = 0; a < kk; a++) { rest.Add(kRest[a] - middle); parea.Add(kArea[a]); }
            float rad = 0f;
            for (int v = v0; v < v0 + vc; v++) rad = MathF.Max(rad, Vector3.Distance(pos[v], middle));
            res.Radius[i] = rad;

            // edges and triangles
            foreach (var (a, b) in pEdges) { ea.Add(pBase + a); eb.Add(pBase + b); el.Add(Vector3.Distance(kRest[a], kRest[b])); }
            float triArea = 0f;
            foreach (var (a, b, c) in pTris)
            {
                tri.Add(pBase + a); tri.Add(pBase + b); tri.Add(pBase + c);
                triArea += Area(kRest[a], kRest[b], kRest[c]);
            }
            res.AirScale[i] = triArea > 0f ? Math.Clamp(pieceArea / triArea, 1f, 3f) : 1f;

            // the particles' triangles and neighbours (this piece's, local)
            Array.Clear(incStart, 0, kk + 2);
            foreach (var (a, b, c) in pTris) { incStart[a + 1]++; incStart[b + 1]++; incStart[c + 1]++; }
            for (int a = 0; a < kk; a++) incStart[a + 1] += incStart[a];
            if (inc.Length < incStart[kk]) inc = new int[Math.Max(incStart[kk], 2 * inc.Length)];
            {
                var at = new int[kk];
                Array.Copy(incStart, at, kk);
                for (int t = 0; t < pTris.Count; t++) { var (a, b, c) = pTris[t]; inc[at[a]++] = t; inc[at[b]++] = t; inc[at[c]++] = t; }
            }
            Array.Clear(nbStart, 0, kk + 2);
            foreach (var (a, b) in pEdges) { nbStart[a + 1]++; nbStart[b + 1]++; }
            for (int a = 0; a < kk; a++) nbStart[a + 1] += nbStart[a];
            if (nbs.Length < nbStart[kk]) nbs = new int[Math.Max(nbStart[kk], 2 * nbs.Length)];
            {
                var at = new int[kk];
                Array.Copy(nbStart, at, kk);
                foreach (var (a, b) in pEdges) { nbs[at[a]++] = b; nbs[at[b]++] = a; }
            }

            // bending: the two corners across each edge two triangles share (as the cloth's); a particle on no triangle (a
            // strip's) bends through its neighbours that lie across it from each other
            opposite.Clear();
            foreach (var (a, b, c) in pTris)
            {
                Across(opposite, a, b, c, ba, bb, bl, kRest, pBase);
                Across(opposite, b, c, a, ba, bb, bl, kRest, pBase);
                Across(opposite, c, a, b, ba, bb, bl, kRest, pBase);
            }
            for (int j = 0; j < kk; j++)
            {
                if (incStart[j + 1] > incStart[j]) continue;
                int made = 0;
                for (int x = nbStart[j]; x < nbStart[j + 1] && made < 2; x++)
                    for (int y = x + 1; y < nbStart[j + 1] && made < 2; y++)
                    {
                        int a = nbs[x], c = nbs[y];
                        var da = kRest[a] - kRest[j]; var dc = kRest[c] - kRest[j];
                        float la2 = da.Length() * dc.Length();
                        if (!(la2 > 0f) || Vector3.Dot(da, dc) / la2 > -0.5f) continue;
                        ba.Add(pBase + a); bb.Add(pBase + c); bl.Add(Vector3.Distance(kRest[a], kRest[c]));
                        made++;
                    }
            }
            // particles enough to bend and still no bending pair (its triangles share no edge, or a particle hangs off
            // one by a single edge): it bends at each particle instead, its neighbours there keeping their distance
            if (ba.Count == res.BStart[i] && kk >= 4)
            {
                ringPairs.Clear();
                for (int j = 0; j < kk; j++)
                    for (int x = nbStart[j]; x < nbStart[j + 1]; x++)
                        for (int y = x + 1; y < nbStart[j + 1]; y++)
                        {
                            int a = Math.Min(nbs[x], nbs[y]), c = Math.Max(nbs[x], nbs[y]);
                            long key = ((long)a << 32) | (uint)c;
                            if (pEdgeSet.Contains(key) || !ringPairs.Add(key)) continue;
                            ba.Add(pBase + a); bb.Add(pBase + c); bl.Add(Vector3.Distance(kRest[a], kRest[c]));
                        }
            }

            // the skin: each vertex follows the three particles of the triangle it lies on best, among its own particle's,
            // else its neighbours', else the piece's
            for (int v = v0; v < v0 + vc; v++)
            {
                int own = owner[nodeOf[v - v0]];
                if (own < 0) own = 0;
                var q = pos[v];
                int bt = -1;
                float score = float.NegativeInfinity, bu = 1f, bv = 0f, bw = 0f;
                for (int x = incStart[own]; x < incStart[own + 1]; x++) Better(pTris[inc[x]], inc[x], q, kRest, ref bt, ref score, ref bu, ref bv, ref bw);
                if (bt < 0)
                    for (int y = nbStart[own]; y < nbStart[own + 1]; y++)
                    {
                        int nb = nbs[y];
                        for (int x = incStart[nb]; x < incStart[nb + 1]; x++) Better(pTris[inc[x]], inc[x], q, kRest, ref bt, ref score, ref bu, ref bv, ref bw);
                    }
                if (bt < 0) for (int t = 0; t < pTris.Count; t++) Better(pTris[t], t, q, kRest, ref bt, ref score, ref bu, ref bv, ref bw);
                int o = 3 * v;
                if (bt < 0)
                {
                    res.SkinP[o] = res.SkinP[o + 1] = res.SkinP[o + 2] = pBase + own;
                    res.SkinW[o] = 1f; res.SkinW[o + 1] = 0f; res.SkinW[o + 2] = 0f;
                    continue;
                }
                var (ta, tb, tc) = pTris[bt];
                float wa = MathF.Max(bu, 0f), wb = MathF.Max(bv, 0f), wc = MathF.Max(bw, 0f), sum = wa + wb + wc;
                if (!(sum > 1e-6f)) { wa = 1f; wb = wc = 0f; sum = 1f; }
                res.SkinP[o] = pBase + ta; res.SkinP[o + 1] = pBase + tb; res.SkinP[o + 2] = pBase + tc;
                res.SkinW[o] = wa / sum; res.SkinW[o + 1] = wb / sum; res.SkinW[o + 2] = wc / sum;
            }
        }
        res.PStart[n] = rest.Count; res.EStart[n] = ea.Count; res.BStart[n] = ba.Count; res.TStart[n] = tri.Count / 3;
        res.Rest = rest.ToArray(); res.Area = parea.ToArray();
        res.EA = ea.ToArray(); res.EB = eb.ToArray(); res.EL = el.ToArray();
        res.BA = ba.ToArray(); res.BB = bb.ToArray(); res.BL = bl.ToArray();
        res.Tri = tri.ToArray();

        // every particle's triangles (all pieces), and whose turn a particle on none takes: the nearest one's on one, in
        // its piece
        int np = res.Rest.Length, nt = res.Tri.Length / 3;
        res.IncStart = new int[np + 1];
        for (int t = 0; t < 3 * nt; t++) res.IncStart[res.Tri[t] + 1]++;
        for (int a = 0; a < np; a++) res.IncStart[a + 1] += res.IncStart[a];
        res.Inc = new int[res.IncStart[np]];
        {
            var at = (int[])res.IncStart.Clone();
            for (int t = 0; t < nt; t++) for (int c = 0; c < 3; c++) res.Inc[at[res.Tri[3 * t + c]]++] = t;
        }
        res.Donor = new int[np];
        for (int i = 0; i < n; i++)
            for (int a = res.PStart[i]; a < res.PStart[i + 1]; a++)
            {
                res.Donor[a] = a;
                if (res.IncStart[a + 1] > res.IncStart[a]) continue;
                float bd = float.MaxValue;
                for (int b = res.PStart[i]; b < res.PStart[i + 1]; b++)
                {
                    if (res.IncStart[b + 1] == res.IncStart[b]) continue;
                    float d = Vector3.DistanceSquared(res.Rest[a], res.Rest[b]);
                    if (d < bd) { bd = d; res.Donor[a] = b; }
                }
            }
        return res;
    }

    static float Area(Vector3 a, Vector3 b, Vector3 c) => 0.5f * Vector3.Cross(b - a, c - a).Length();

    static void AddEdge(List<(int A, int B)> list, HashSet<long> set, int a, int b)
    {
        if (a == b) return;
        if (a > b) (a, b) = (b, a);
        if (set.Add(((long)a << 32) | (uint)b)) list.Add((a, b));
    }

    /// <summary>Ties the parts of a piece's particles that no edge joins, each to the nearest particle of another part (a
    /// round at a time, as Borůvka's), until one part is left. At most 48 particles: cheap.</summary>
    static void LinkParticles(List<int> seeds, List<Vector3> nodePos, List<(int A, int B)> edges, HashSet<long> set)
    {
        int k = seeds.Count;
        if (k < 2) return;
        var parent = new int[k];
        for (int a = 0; a < k; a++) parent[a] = a;
        foreach (var (a, b) in edges) Union(parent, a, b);
        float[] best = null;
        int[] ba = null, bb = null;
        while (true)
        {
            int parts = 0;
            for (int a = 0; a < k; a++) if (Find(parent, a) == a) parts++;
            if (parts <= 1) return;
            best ??= new float[k]; ba ??= new int[k]; bb ??= new int[k];
            for (int a = 0; a < k; a++) { best[a] = float.MaxValue; ba[a] = -1; }
            for (int a = 0; a < k; a++)
            {
                int ra = Find(parent, a);
                for (int b = a + 1; b < k; b++)
                {
                    int rb = Find(parent, b);
                    if (ra == rb) continue;
                    float d = Vector3.DistanceSquared(nodePos[seeds[a]], nodePos[seeds[b]]);
                    if (d < best[ra]) { best[ra] = d; ba[ra] = a; bb[ra] = b; }
                    if (d < best[rb]) { best[rb] = d; ba[rb] = a; bb[rb] = b; }
                }
            }
            for (int r = 0; r < k; r++)
            {
                if (ba[r] < 0) continue;
                AddEdge(edges, set, ba[r], bb[r]);
                Union(parent, ba[r], bb[r]);
            }
        }
    }

    static int Find(int[] parent, int k)
    {
        while (parent[k] != k) { parent[k] = parent[parent[k]]; k = parent[k]; }
        return k;
    }

    static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
    }

    static long TriKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return ((long)a << 42) | ((long)b << 21) | (long)c;
    }

    /// <summary>Edge a-b of a triangle whose third corner is c: the second triangle on it makes a bending pair of the two
    /// third corners.</summary>
    static void Across(Dictionary<long, int> opposite, int a, int b, int c, List<int> ba, List<int> bb, List<float> bl, Vector3[] rest, int pBase)
    {
        if (a > b) (a, b) = (b, a);
        long key = ((long)a << 32) | (uint)b;
        if (!opposite.TryGetValue(key, out int other)) { opposite.Add(key, c); return; }
        if (other < 0 || other == c) return;
        opposite[key] = -1;   // a third triangle on the same edge adds nothing more
        ba.Add(pBase + other); bb.Add(pBase + c); bl.Add(Vector3.Distance(rest[other], rest[c]));
    }

    /// <summary>Spreads particle k's part from node <paramref name="seed"/>: every node nearer to it along the surface than
    /// to any particle before (Dijkstra over the piece's edges).</summary>
    static void Spread(int seed, int k, int[] adjStart, int[] adj, float[] adjLen, float[] dist, int[] owner, PriorityQueue<int, float> pq)
    {
        dist[seed] = 0f; owner[seed] = k;
        pq.Clear();
        pq.Enqueue(seed, 0f);
        while (pq.TryDequeue(out int u, out float du))
        {
            if (du > dist[u]) continue;
            for (int e = adjStart[u]; e < adjStart[u + 1]; e++)
            {
                int w = adj[e];
                float nd = du + adjLen[e];
                if (nd < dist[w]) { dist[w] = nd; owner[w] = k; pq.Enqueue(w, nd); }
            }
        }
    }

    /// <summary>Whether triangle t of particles holds q better (its smallest barycentric weight, q laid onto its plane,
    /// the highest so far).</summary>
    static void Better((int A, int B, int C) t, int index, Vector3 q, Vector3[] rest, ref int best, ref float score, ref float bu, ref float bv, ref float bw)
    {
        var a = rest[t.A]; var e0 = rest[t.B] - a; var e1 = rest[t.C] - a; var d = q - a;
        float d00 = Vector3.Dot(e0, e0), d01 = Vector3.Dot(e0, e1), d11 = Vector3.Dot(e1, e1);
        float d20 = Vector3.Dot(d, e0), d21 = Vector3.Dot(d, e1);
        float den = d00 * d11 - d01 * d01;
        if (!(den > 1e-20f)) return;
        float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den, u = 1f - v - w;
        float sc = MathF.Min(u, MathF.Min(v, w));
        if (!(sc > score)) return;
        score = sc; best = index; bu = u; bv = v; bw = w;
    }

    /// <summary>The piece's mean normal (its vertices' normals added up).</summary>
    static Vector3 FrontNormal(PopPieces p, int v0, int vc)
    {
        var s = Vector3.Zero;
        for (int v = v0; v < v0 + vc; v++) s += p.Normals[v];
        return s;
    }
}
