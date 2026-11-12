using System.Diagnostics;
using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// The outer surface of the flesh shell: the painted region of the mesh, welded and reduced to a simulation resolution.
/// <list type="number">
/// <item>Weld: render vertices split on UV or normal seams share a position; they become one vertex, or the sim tears.</item>
/// <item>Region: the welded triangles touching a soft vertex. Their non-soft vertices are the pinned ring around it.</item>
/// <item>Reduce: half-edge collapses, shortest edge first, until the soft vertex count reaches the target. A collapse
/// removes a vertex and keeps its neighbour where it is, so every sim vertex is a real mesh vertex and takes that
/// vertex's own bone weights and blendshape deltas: nothing is averaged across body parts.</item>
/// </list>
/// </summary>
public sealed class SimSurface
{
    // ---- weld (all render vertices)
    public int WeldCount { get; private set; }
    /// <summary>Render vertex to welded vertex.</summary>
    public int[] RenderToWeld { get; private set; }
    /// <summary>One render vertex of each welded vertex (its skin weights and blendshape deltas stand for all).</summary>
    public int[] WeldRep { get; private set; }

    // ---- the sim surface
    public int Count { get; private set; }
    public int SoftCount { get; private set; }
    public int[] SimToWeld { get; private set; }
    /// <summary>Welded vertex to sim vertex, -1 when it is not on the sim surface.</summary>
    public int[] WeldToSim { get; private set; }
    public Vector3[] Pos { get; private set; }
    /// <summary>Outward normals (from the render normals), smoothed over the sim surface.</summary>
    public Vector3[] Nrm { get; private set; }
    public float[] Soft { get; private set; }
    public bool[] Pinned { get; private set; }
    public int[] Tris { get; private set; }
    public int TriCount => Tris.Length / 3;
    /// <summary>Mean edge length of the sim surface, and per vertex the mean of its edges.</summary>
    public float MeanEdge { get; private set; }
    public float[] VertexEdge { get; private set; }
    /// <summary>Sim vertex neighbours (CSR).</summary>
    public int[] NbrStart { get; private set; }
    public int[] Nbr { get; private set; }

    // ---- report
    public int RegionVertices { get; private set; }
    public int RegionSoftVertices { get; private set; }
    public int RegionTriangles { get; private set; }
    public int Collapses { get; private set; }
    public double WeldMs { get; private set; }
    public double ReduceMs { get; private set; }

    public static SimSurface Build(Vector3[] pos, Vector3[] nrm, int[] tris, float[] soft, float threshold, int targetSoft)
    {
        var s = new SimSurface();
        var sw = Stopwatch.StartNew();
        int n = pos.Length;

        // ---- weld by exact position (importers split vertices without moving them)
        var map = new Dictionary<Vector3, int>(n);
        var r2w = new int[n]; var rep = new List<int>(n);
        for (int v = 0; v < n; v++)
        {
            var k = pos[v];
            if (k.X == 0f) k.X = 0f; if (k.Y == 0f) k.Y = 0f; if (k.Z == 0f) k.Z = 0f; // -0 and +0 hash differently
            if (!map.TryGetValue(k, out int w)) { w = rep.Count; map.Add(k, w); rep.Add(v); }
            r2w[v] = w;
        }
        int nw = rep.Count;
        var wPos = new Vector3[nw]; var wNrm = new Vector3[nw]; var wSoft = new float[nw];
        for (int w = 0; w < nw; w++) wPos[w] = pos[rep[w]];
        for (int v = 0; v < n; v++)
        {
            int w = r2w[v];
            if (nrm != null && v < nrm.Length && float.IsFinite(nrm[v].X)) wNrm[w] += nrm[v];
            float sv = soft != null && v < soft.Length && float.IsFinite(soft[v]) ? soft[v] : 0f;
            if (sv > wSoft[w]) wSoft[w] = sv;
        }
        s.WeldCount = nw; s.RenderToWeld = r2w; s.WeldRep = rep.ToArray();
        s.WeldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();

        // ---- region: welded triangles with a soft vertex
        var regionOf = new int[nw]; Array.Fill(regionOf, -1);
        var rw = new List<int>(); var rt = new List<int>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = r2w[tris[t]], b = r2w[tris[t + 1]], c = r2w[tris[t + 2]];
            if (a == b || b == c || a == c) continue;
            if (!(wSoft[a] > threshold || wSoft[b] > threshold || wSoft[c] > threshold)) continue;
            // (no stackalloc in this loop: stack memory from a loop is only freed when the method returns)
            if (regionOf[a] < 0) { regionOf[a] = rw.Count; rw.Add(a); }
            if (regionOf[b] < 0) { regionOf[b] = rw.Count; rw.Add(b); }
            if (regionOf[c] < 0) { regionOf[c] = rw.Count; rw.Add(c); }
            rt.Add(regionOf[a]); rt.Add(regionOf[b]); rt.Add(regionOf[c]);
        }
        int nr = rw.Count;
        var rPos = new Vector3[nr]; var rPinned = new bool[nr];
        int softCount = 0;
        for (int i = 0; i < nr; i++) { rPos[i] = wPos[rw[i]]; rPinned[i] = !(wSoft[rw[i]] > threshold); if (!rPinned[i]) softCount++; }
        s.RegionVertices = nr; s.RegionSoftVertices = softCount; s.RegionTriangles = rt.Count / 3;

        // ---- reduce
        var red = new Reducer(rPos, rPinned, rt.ToArray());
        red.Run(Math.Max(1, targetSoft), softCount);
        s.Collapses = red.Collapses;

        // ---- compact
        var r2s = new int[nr]; Array.Fill(r2s, -1);
        var simToWeld = new List<int>();
        for (int i = 0; i < nr; i++) if (!red.Dead[i]) { r2s[i] = simToWeld.Count; simToWeld.Add(rw[i]); }
        int ns = simToWeld.Count;
        var st = new List<int>(red.Tris.Length);
        for (int t = 0; t < red.TriAlive.Length; t++)
        {
            if (!red.TriAlive[t]) continue;
            st.Add(r2s[red.Tris[t * 3]]); st.Add(r2s[red.Tris[t * 3 + 1]]); st.Add(r2s[red.Tris[t * 3 + 2]]);
        }
        s.Count = ns; s.SimToWeld = simToWeld.ToArray(); s.Tris = st.ToArray();
        s.WeldToSim = new int[nw]; Array.Fill(s.WeldToSim, -1);
        s.Pos = new Vector3[ns]; s.Soft = new float[ns]; s.Pinned = new bool[ns];
        int simSoft = 0;
        for (int i = 0; i < ns; i++)
        {
            int w = s.SimToWeld[i];
            s.WeldToSim[w] = i; s.Pos[i] = wPos[w]; s.Soft[i] = wSoft[w]; s.Pinned[i] = !(wSoft[w] > threshold);
            if (!s.Pinned[i]) simSoft++;
        }
        s.SoftCount = simSoft;
        s.BuildNeighbours();
        s.BuildNormals(wNrm);
        s.EaseBorder(BorderRings);
        s.ReduceMs = sw.Elapsed.TotalMilliseconds;
        return s;
    }

    /// <summary>Rings of sim points over which softness eases in from the pinned border.</summary>
    public const int BorderRings = 3;
    /// <summary>Per sim point: how many neighbour steps it is from the pinned ring (0 = pinned, capped at BorderRings + 1).</summary>
    public int[] BorderDistance { get; private set; }

    /// <summary>
    /// Attachment zones: a point next to the pinned ring keeps only part of its painted softness (smoothstep over
    /// <paramref name="rings"/> neighbour steps). A hard paint edge then still gets a gentle physical transition; without
    /// it a point lagging a centimetre sits one edge from a pinned one, and that strain folds the small details between.
    /// </summary>
    void EaseBorder(int rings)
    {
        var dist = new int[Count];
        Array.Fill(dist, rings + 1);
        var q = new Queue<int>();
        for (int i = 0; i < Count; i++) if (Pinned[i]) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            if (dist[i] >= rings) continue;
            for (int k = NbrStart[i]; k < NbrStart[i + 1]; k++)
            {
                int j = Nbr[k];
                if (dist[j] > dist[i] + 1) { dist[j] = dist[i] + 1; q.Enqueue(j); }
            }
        }
        for (int i = 0; i < Count; i++)
        {
            if (Pinned[i] || dist[i] > rings) continue;
            float t = (float)dist[i] / (rings + 1);
            Soft[i] *= t * t * (3f - 2f * t);
        }
        BorderDistance = dist;
    }

    void BuildNeighbours()
    {
        var sets = new HashSet<int>[Count];
        for (int i = 0; i < Count; i++) sets[i] = new HashSet<int>();
        for (int t = 0; t < Tris.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = Tris[t + k], b = Tris[t + (k + 1) % 3];
                sets[a].Add(b); sets[b].Add(a);
            }
        NbrStart = new int[Count + 1];
        for (int i = 0; i < Count; i++) NbrStart[i + 1] = NbrStart[i] + sets[i].Count;
        Nbr = new int[NbrStart[Count]];
        VertexEdge = new float[Count];
        double sum = 0; long edges = 0;
        for (int i = 0; i < Count; i++)
        {
            int o = NbrStart[i]; float acc = 0;
            foreach (int j in sets[i]) { Nbr[o++] = j; float l = Vector3.Distance(Pos[i], Pos[j]); acc += l; if (j > i) { sum += l; edges++; } }
            VertexEdge[i] = sets[i].Count > 0 ? acc / sets[i].Count : 0f;
        }
        MeanEdge = edges > 0 ? (float)(sum / edges) : 0f;
        for (int i = 0; i < Count; i++) if (!(VertexEdge[i] > 0f)) VertexEdge[i] = MeanEdge;
    }

    void BuildNormals(Vector3[] weldNormals)
    {
        Nrm = new Vector3[Count];
        // the face normals give the shape; the render normals only decide which side is outside
        var face = new Vector3[Count];
        for (int t = 0; t < Tris.Length; t += 3)
        {
            int a = Tris[t], b = Tris[t + 1], c = Tris[t + 2];
            var fn = Vector3.Cross(Pos[b] - Pos[a], Pos[c] - Pos[a]);
            face[a] += fn; face[b] += fn; face[c] += fn;
        }
        for (int i = 0; i < Count; i++)
        {
            var r = weldNormals[SimToWeld[i]];
            var f = face[i];
            if (r.LengthSquared() > 1e-20f && Vector3.Dot(f, r) < 0f) f = -f;      // winding opposite to the normals
            if (f.LengthSquared() < 1e-24f) f = r;
            Nrm[i] = f.LengthSquared() > 1e-24f ? Vector3.Normalize(f) : Vector3.UnitY;
        }
        // two smoothing passes, so the inner surface does not copy every bump
        var tmp = new Vector3[Count];
        for (int it = 0; it < 2; it++)
        {
            for (int i = 0; i < Count; i++)
            {
                var acc = Nrm[i];
                for (int k = NbrStart[i]; k < NbrStart[i + 1]; k++) acc += Nrm[Nbr[k]];
                tmp[i] = acc.LengthSquared() > 1e-24f ? Vector3.Normalize(acc) : Nrm[i];
            }
            (Nrm, tmp) = (tmp, Nrm);
        }
    }

    /// <summary>Half-edge collapse on an indexed triangle soup that may be open or non-manifold in places.</summary>
    sealed class Reducer
    {
        readonly Vector3[] _p;
        readonly bool[] _pinned;
        public readonly int[] Tris;
        public readonly bool[] TriAlive;
        public readonly bool[] Dead;
        readonly List<int>[] _vt;
        readonly int[] _mark; int _stamp;
        readonly int[] _cnt;
        public int Collapses;
        const int MaxValence = 14;

        public Reducer(Vector3[] p, bool[] pinned, int[] tris)
        {
            _p = p; _pinned = pinned; Tris = tris;
            int n = p.Length, nt = tris.Length / 3;
            TriAlive = new bool[nt]; Dead = new bool[n];
            _vt = new List<int>[n];
            for (int i = 0; i < n; i++) _vt[i] = new List<int>(6);
            for (int t = 0; t < nt; t++)
            {
                TriAlive[t] = true;
                _vt[tris[t * 3]].Add(t); _vt[tris[t * 3 + 1]].Add(t); _vt[tris[t * 3 + 2]].Add(t);
            }
            _mark = new int[n]; _cnt = new int[n];
        }

        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public void Run(int targetSoft, int soft)
        {
            var q = new PriorityQueue<long, float>();
            var seen = new HashSet<long>();
            for (int t = 0; t < TriAlive.Length; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = Tris[t * 3 + k], b = Tris[t * 3 + (k + 1) % 3];
                    long key = Key(a, b);
                    if (seen.Add(key)) q.Enqueue(key, Vector3.DistanceSquared(_p[a], _p[b]));
                }
            while (soft > targetSoft && q.TryDequeue(out long key, out _))
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                if (Dead[a] || Dead[b]) continue;
                bool ba = IsBoundary(a), bb = IsBoundary(b);
                bool done;
                if (ba == bb) done = TryCollapse(a, b, ba) || TryCollapse(b, a, bb);
                else if (ba) done = TryCollapse(b, a, false);   // keep the boundary vertex, remove the interior one
                else done = TryCollapse(a, b, false);
                if (!done) continue;
                int kept = Dead[a] ? b : a, removed = Dead[a] ? a : b;
                if (!_pinned[removed]) soft--;
                Collapses++;
                Neighbours(kept);
                for (int i = 0; i < _nbrCount; i++)
                {
                    int w = _nbr[i];
                    q.Enqueue(Key(kept, w), Vector3.DistanceSquared(_p[kept], _p[w]));
                }
            }
        }

        int[] _nbr = new int[64]; int _nbrCount;

        /// <summary>Fills _nbr with the neighbours of v and _cnt[w] with how many live triangles share edge (v, w).</summary>
        void Neighbours(int v)
        {
            _stamp++; _nbrCount = 0;
            foreach (int t in _vt[v])
                for (int k = 0; k < 3; k++)
                {
                    int w = Tris[t * 3 + k];
                    if (w == v) continue;
                    if (_mark[w] != _stamp)
                    {
                        _mark[w] = _stamp; _cnt[w] = 0;
                        if (_nbrCount == _nbr.Length) Array.Resize(ref _nbr, _nbr.Length * 2);
                        _nbr[_nbrCount++] = w;
                    }
                    _cnt[w]++;
                }
        }

        /// <summary>True when v has an open or non-manifold edge.</summary>
        bool IsBoundary(int v)
        {
            Neighbours(v);
            for (int i = 0; i < _nbrCount; i++) if (_cnt[_nbr[i]] != 2) return true;
            return false;
        }

        readonly List<int> _tmp = new();

        bool TryCollapse(int u, int v, bool uBoundary)
        {
            if (_pinned[u] != _pinned[v]) return false;
            // edge use and the link condition
            Neighbours(u);
            if (_mark[v] != _stamp) return false; // no longer an edge
            int shared = _cnt[v];
            if (shared > 2 || shared == 0) return false;
            if (uBoundary && shared != 1) return false;   // a boundary vertex only slides along its boundary
            for (int i = 0; i < _nbrCount; i++) if (_cnt[_nbr[i]] > 2) return false; // non-manifold star
            var uN = _tmp; uN.Clear();
            for (int i = 0; i < _nbrCount; i++) uN.Add(_nbr[i]);
            Neighbours(v);
            int common = 0, union = _nbrCount;
            foreach (int w in uN) { if (w == v) continue; if (_mark[w] == _stamp) common++; else union++; }
            if (common != shared) return false;
            if (union - 1 > MaxValence) return false;
            // the triangles that move must not flip or get much worse
            foreach (int t in _vt[u])
            {
                int i0 = Tris[t * 3], i1 = Tris[t * 3 + 1], i2 = Tris[t * 3 + 2];
                if (i0 == v || i1 == v || i2 == v) continue;
                var a = _p[i0]; var b = _p[i1]; var c = _p[i2];
                var n0 = Vector3.Cross(b - a, c - a);
                if (i0 == u) a = _p[v]; else if (i1 == u) b = _p[v]; else c = _p[v];
                var n1 = Vector3.Cross(b - a, c - a);
                float l0 = n0.Length(), l1 = n1.Length();
                if (!(l1 > 1e-14f) || Vector3.Dot(n0, n1) < 0.5f * l0 * l1) return false;
                float q1 = Quality(a, b, c, l1);
                if (q1 < 0.2f)
                {
                    var a0 = _p[i0]; var b0 = _p[i1]; var c0 = _p[i2];
                    if (q1 < Quality(a0, b0, c0, l0)) return false;
                }
            }
            // do it
            foreach (int t in _vt[u].ToArray())
            {
                int o = t * 3;
                if (Tris[o] == v || Tris[o + 1] == v || Tris[o + 2] == v)
                {
                    TriAlive[t] = false;
                    for (int k = 0; k < 3; k++) { int w = Tris[o + k]; if (w != u) _vt[w].Remove(t); }
                }
                else
                {
                    for (int k = 0; k < 3; k++) if (Tris[o + k] == u) Tris[o + k] = v;
                    _vt[v].Add(t);
                }
            }
            _vt[u].Clear();
            Dead[u] = true;
            return true;
        }

        /// <summary>1 for an equilateral triangle, toward 0 for slivers.</summary>
        static float Quality(Vector3 a, Vector3 b, Vector3 c, float twiceArea)
        {
            float e = (b - a).LengthSquared() + (c - b).LengthSquared() + (a - c).LengthSquared();
            return e > 0f ? 2f * MathF.Sqrt(3f) * twiceArea / e : 0f;
        }
    }
}
