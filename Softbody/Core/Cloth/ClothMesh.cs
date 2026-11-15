using System.Numerics;

namespace Softbody.Core.Cloth;

/// <summary>
/// 0.29.0: a cloth's topology, and the tear split every client repeats the same way.
/// <list type="bullet">
/// <item><b>Particles</b> are the mesh's vertices welded by exact position (UV seams and the two sides of a double-sided
/// sheet are one particle).</item>
/// <item><b>Simulated triangles</b> are the mesh's triangles over distinct particles, each particle triple once (the back of
/// a double-sided sheet is the same triangle). A triangle whose corners weld together is drawn but not simulated.</item>
/// <item><b>Render vertices and triangles</b> are the mesh's own, in its order. A tear can add render vertices (copies of
/// existing ones, appended in a fixed order) and changes which render vertex a triangle corner uses, but never the number
/// or order of the triangles.</item>
/// </list>
/// <see cref="Split"/> is a tear (FleX and the 2007 PBD paper): a particle's triangles are sorted by which side of a plane
/// through it their centre lies, the plane across the torn edge, and one side moves to a new particle. Every decision uses
/// the rest positions only, with scalar float arithmetic in a fixed order and fixed iteration orders, so a client that
/// replays the same list of splits on the same mesh gets the same particles, triangles and render vertices, index for
/// index. The client that decides the tears records each split as (point, other); the others replay them.
/// 0.31.0: <see cref="Cut"/> is the other way to tear, for a blast's shred: two triangles stop holding together along the
/// edge they share, and the edge's particles separate where that leaves their triangles in groups (as a bowtie). A shred
/// cuts between triangles chosen from the rest centres alone (ClothBlastMath.ShredCuts), so it too is the same everywhere.
/// </summary>
public sealed class ClothMesh
{
    // ---- particles
    int _np;
    readonly int _np0;
    readonly Vector3[] _rest;
    readonly int[] _origin;              // the original particle a split particle comes from (itself for the original ones)
    readonly int[] _parent;              // the particle a split copied (itself for the original ones)
    readonly List<int>[] _fan;           // each particle's simulated triangles, ascending

    // ---- simulated triangles
    readonly int _nt;
    readonly int[] _tri;                 // 3 per triangle: particles (corners change on splits)
    readonly Vector3[] _centroid;        // rest centres (fixed)
    readonly float[] _restArea;          // rest areas (fixed)

    // ---- render
    readonly int _nv0;
    int _nv;
    int[] _rvParticle, _rvSource, _rvUse;
    sbyte[] _rvSign;
    readonly int _nr;
    readonly int[] _rtri;                // 3 per render triangle: render vertices (corners change on splits)
    readonly int[] _rtSim;               // its simulated triangle, -1 when its corners weld together
    readonly sbyte[] _rtSign;            // +1: wound as its simulated triangle, -1: the other way (the back of a sheet)
    readonly int[] _simRStart, _simR;    // each simulated triangle's render triangles

    /// <summary>Counts every change of the topology (a split, or a bowtie separated).</summary>
    public int Version { get; private set; }
    /// <summary>Particles at most (the original ones plus room for tears); a split past it does nothing.</summary>
    public int MaxParticles { get; }
    /// <summary>Splits done since the build (each <see cref="Split"/> that returned true).</summary>
    public int Splits { get; private set; }
    /// <summary>0.31.0: cuts made since the build (each <see cref="Cut"/> that returned true).</summary>
    public int Cuts { get; private set; }
    /// <summary>0.31.0: the most room for tears a build can ask for, as a fraction of the welded particles: 5 (six times as many
    /// particles) is enough to cut every triangle loose (a blast's confetti).</summary>
    public const float MaxGrowth = 5f;
    /// <summary>0.30.0: render vertices at most (avatar cloth: the mesh's own plus its spare vertices). A split that might
    /// need more does nothing (judged from the topology alone, so every client refuses the same splits).</summary>
    public int MaxRenderVertices { get; set; } = int.MaxValue;
    /// <summary>0.30.0: no room for another tear (points or render vertices).</summary>
    public bool Full => _np >= MaxParticles || (MaxRenderVertices < int.MaxValue && _nv >= MaxRenderVertices);

    public int ParticleCount => _np;
    public int OriginalParticleCount => _np0;
    public int TriangleCount => _nt;
    public int RenderVertexCount => _nv;
    public int OriginalRenderVertexCount => _nv0;
    public int RenderTriangleCount => _nr;
    public ReadOnlySpan<Vector3> RestPositions => _rest.AsSpan(0, _np);
    public ReadOnlySpan<int> Triangles => _tri;
    public ReadOnlySpan<float> RestAreas => _restArea;
    public ReadOnlySpan<int> RenderParticle => _rvParticle.AsSpan(0, _nv);
    /// <summary>For each render vertex, the original render vertex it copies (itself for the original ones): its UVs,
    /// colours and other data.</summary>
    public ReadOnlySpan<int> RenderSource => _rvSource.AsSpan(0, _nv);
    /// <summary>For each render vertex, +1 when its triangles are wound as their simulated triangle, -1 when the other way
    /// (its normal is turned around).</summary>
    public ReadOnlySpan<sbyte> RenderSign => _rvSign.AsSpan(0, _nv);
    public ReadOnlySpan<int> RenderTriangles => _rtri;
    public ReadOnlySpan<int> RenderTriangleSim => _rtSim;
    /// <summary>0.31.0: each simulated triangle's centre on the mesh as built (fixed; a blast's shred goes by these).</summary>
    public ReadOnlySpan<Vector3> RestCentroids => _centroid;
    /// <summary>0.31.0: the mean length of the simulated triangles' edges on the mesh as built (mesh units).</summary>
    public float MeanEdge { get; }
    /// <summary>The original particle <paramref name="p"/> was split from (itself for an original one).</summary>
    public int Origin(int p) => _origin[p];
    /// <summary>The particle <paramref name="p"/> was split from (itself for an original one): its state is p's start.</summary>
    public int Parent(int p) => _parent[p];
    public int FanSize(int p) => _fan[p].Count;
    public IReadOnlyList<int> Fan(int p) => _fan[p];

    /// <summary>
    /// Builds the topology from a mesh's vertex positions and its triangles (all triangle submeshes joined, in order).
    /// <paramref name="growth"/>: room for new particles, as a fraction of the welded ones (at least 16; 0.31.0: up to
    /// <see cref="MaxGrowth"/>), and no more than <paramref name="maxExtra"/> of them.
    /// </summary>
    public ClothMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> triangles, float growth = 0.25f, int maxExtra = int.MaxValue)
    {
        _nv0 = _nv = positions.Length;
        _nr = triangles.Length / 3;
        // weld by exact position (-0 and +0 are the same; each NaN its own)
        _rvParticle = new int[Math.Max(4, _nv)];
        var weld = new Dictionary<(int, int, int), int>(_nv);
        var firstOf = new List<int>(_nv);
        for (int v = 0; v < _nv; v++)
        {
            var p = positions[v];
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) { _rvParticle[v] = firstOf.Count; firstOf.Add(v); continue; }
            var k = (Bits(p.X), Bits(p.Y), Bits(p.Z));
            if (!weld.TryGetValue(k, out int pi)) { pi = firstOf.Count; weld[k] = pi; firstOf.Add(v); }
            _rvParticle[v] = pi;
        }
        _np0 = _np = firstOf.Count;
        float g = float.IsFinite(growth) ? Math.Clamp(growth, 0f, MaxGrowth) : 0f;
        MaxParticles = _np0 + Math.Max(16, (int)Math.Min((long)(_np0 * g), Math.Max(0, maxExtra)));
        _rest = new Vector3[MaxParticles];
        _origin = new int[MaxParticles];
        _parent = new int[MaxParticles];
        _fan = new List<int>[MaxParticles];
        for (int i = 0; i < _np0; i++) { _rest[i] = positions[firstOf[i]]; _origin[i] = i; _parent[i] = i; }

        // simulated triangles: distinct particles, each triple once
        _rtri = triangles.Slice(0, _nr * 3).ToArray();
        _rtSim = new int[_nr];
        _rtSign = new sbyte[_nr];
        var tri = new List<int>(_nr * 3);
        var seen = new Dictionary<(int, int, int), int>(_nr);
        for (int r = 0; r < _nr; r++)
        {
            int va = _rtri[3 * r], vb = _rtri[3 * r + 1], vc = _rtri[3 * r + 2];
            if ((uint)va >= (uint)_nv || (uint)vb >= (uint)_nv || (uint)vc >= (uint)_nv) throw new ArgumentException($"triangle {r} has a vertex index out of range");
            int a = _rvParticle[va], b = _rvParticle[vb], c = _rvParticle[vc];
            if (a == b || b == c || a == c) { _rtSim[r] = -1; _rtSign[r] = 1; continue; }
            var key = Sorted(a, b, c);
            if (!seen.TryGetValue(key, out int t))
            {
                t = tri.Count / 3;
                seen[key] = t;
                tri.Add(a); tri.Add(b); tri.Add(c);
                _rtSign[r] = 1;
            }
            else _rtSign[r] = SameWinding(tri[3 * t], tri[3 * t + 1], tri[3 * t + 2], a, b, c) ? (sbyte)1 : (sbyte)-1;
            _rtSim[r] = t;
        }
        _tri = tri.ToArray();
        _nt = _tri.Length / 3;

        // render vertices: uses, sign (from the first triangle that uses each), source
        _rvSource = new int[_rvParticle.Length];
        _rvUse = new int[_rvParticle.Length];
        _rvSign = new sbyte[_rvParticle.Length];
        for (int v = 0; v < _nv; v++) _rvSource[v] = v;
        for (int r = 0; r < _nr; r++)
            for (int k = 0; k < 3; k++)
            {
                int v = _rtri[3 * r + k];
                if (_rvUse[v]++ == 0) _rvSign[v] = _rtSign[r];
            }
        for (int v = 0; v < _nv; v++) if (_rvSign[v] == 0) _rvSign[v] = 1;

        // fans, rest centres and areas (0.31.0: a new particle's fan is made when a split makes it)
        for (int i = 0; i < _np0; i++) _fan[i] = new List<int>(6);
        _centroid = new Vector3[_nt];
        _restArea = new float[_nt];
        double edges = 0;
        for (int t = 0; t < _nt; t++)
        {
            int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
            _fan[a].Add(t); _fan[b].Add(t); _fan[c].Add(t);
            Vector3 pa = _rest[a], pb = _rest[b], pc = _rest[c];
            // scalar, fixed order: every client gets the same bits
            _centroid[t] = new Vector3((pa.X + pb.X + pc.X) / 3f, (pa.Y + pb.Y + pc.Y) / 3f, (pa.Z + pb.Z + pc.Z) / 3f);
            _restArea[t] = 0.5f * Vector3.Cross(pb - pa, pc - pa).Length();
            edges += Length(pa, pb) + Length(pb, pc) + Length(pc, pa);
        }
        MeanEdge = _nt > 0 ? (float)(edges / (3.0 * _nt)) : 0f;

        // each simulated triangle's render triangles
        _simRStart = new int[_nt + 1];
        for (int r = 0; r < _nr; r++) if (_rtSim[r] >= 0) _simRStart[_rtSim[r] + 1]++;
        for (int t = 0; t < _nt; t++) _simRStart[t + 1] += _simRStart[t];
        _simR = new int[_simRStart[_nt]];
        var fill = (int[])_simRStart.Clone();
        for (int r = 0; r < _nr; r++) if (_rtSim[r] >= 0) _simR[fill[_rtSim[r]]++] = r;
    }

    static int Bits(float f) => BitConverter.SingleToInt32Bits(f == 0f ? 0f : f);

    /// <summary>Scalar, fixed order (the same bits on every client).</summary>
    static float Length(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    /// <summary>Whether (a, b, c) is (x, y, z) turned (an even permutation), over the same three particles.</summary>
    static bool SameWinding(int x, int y, int z, int a, int b, int c) =>
        (a == x && b == y) || (a == y && b == z) || (a == z && b == x);

    /// <summary>The render triangles of simulated triangle <paramref name="t"/>.</summary>
    public ReadOnlySpan<int> RenderTrianglesOf(int t) => _simR.AsSpan(_simRStart[t], _simRStart[t + 1] - _simRStart[t]);

    /// <summary>Whether particles <paramref name="p"/> and <paramref name="q"/> share a simulated triangle.</summary>
    public bool Adjacent(int p, int q)
    {
        if ((uint)p >= (uint)_np || (uint)q >= (uint)_np || p == q) return false;
        var f = _fan[p];
        for (int k = 0; k < f.Count; k++)
        {
            int t = f[k];
            if (_tri[3 * t] == q || _tri[3 * t + 1] == q || _tri[3 * t + 2] == q) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ tearing

    readonly List<int> _side = new(), _keep = new(), _ring = new(), _newParticles = new();

    /// <summary>Particles a split made, in the order it made them (valid until the next split).</summary>
    public IReadOnlyList<int> LastNewParticles => _newParticles;

    /// <summary>
    /// Tears at particle <paramref name="p"/> across its edge to <paramref name="other"/>: its triangles whose rest centre
    /// lies on <paramref name="other"/>'s side of the plane through p (normal along the edge) move to a new particle. Then
    /// p, the new particle and their neighbours are checked for bowties (a particle joining two fans that no longer share
    /// an edge there) and separated. False, and nothing changed, when the two are not neighbours, a side would be empty,
    /// or there is no room for another particle.
    /// </summary>
    public bool Split(int p, int other)
    {
        _newParticles.Clear();
        if (_np >= MaxParticles || !Adjacent(p, other)) return false;
        var fan = _fan[p];
        if (fan.Count < 2) return false;
        if (MaxRenderVertices < int.MaxValue && (long)_nv + RoomNeeded(p) > MaxRenderVertices) return false;
        Vector3 rp = _rest[p], ro = _rest[other];
        float nx = ro.X - rp.X, ny = ro.Y - rp.Y, nz = ro.Z - rp.Z;
        _side.Clear(); _keep.Clear();
        for (int k = 0; k < fan.Count; k++)
        {
            int t = fan[k];
            var c = _centroid[t];
            float d = (c.X - rp.X) * nx;
            d += (c.Y - rp.Y) * ny;
            d += (c.Z - rp.Z) * nz;
            if (d > 0f) _side.Add(t); else _keep.Add(t);
        }
        if (_side.Count == 0 || _keep.Count == 0) return false;

        // the neighbours to check for bowties afterwards: every corner of p's triangles, ascending, once
        _ring.Clear();
        for (int k = 0; k < fan.Count; k++)
        {
            int t = fan[k];
            for (int j = 0; j < 3; j++) { int x = _tri[3 * t + j]; if (x != p) _ring.Add(x); }
        }
        _ring.Sort();

        int q = NewParticle(p);
        MoveTriangles(p, q, _side);
        Separate(p);
        Separate(q);
        int last = -1;
        foreach (int x in _ring)
        {
            if (x == last) continue;
            last = x;
            Separate(x);
        }
        Splits++;
        Version++;
        return true;
    }

    /// <summary>0.30.0: the most render vertices a split at <paramref name="p"/> can add: each render triangle corner at p
    /// (twice: the split, then the bowtie check of p and its new copy) and at each of its neighbours (their bowtie checks)
    /// is copied at most once.</summary>
    long RoomNeeded(int p)
    {
        long room = 2L * CornerUses(p);
        _ring.Clear();
        var fan = _fan[p];
        for (int k = 0; k < fan.Count; k++)
        {
            int t = fan[k];
            for (int j = 0; j < 3; j++) { int x = _tri[3 * t + j]; if (x != p) _ring.Add(x); }
        }
        _ring.Sort();
        int last = -1;
        foreach (int x in _ring)
        {
            if (x == last) continue;
            last = x;
            room += CornerUses(x);
        }
        _ring.Clear();
        return room;
    }

    /// <summary>Render triangle corners at particle x (each render triangle of a simulated triangle in x's fan has one).</summary>
    int CornerUses(int x)
    {
        int uses = 0;
        var fan = _fan[x];
        for (int k = 0; k < fan.Count; k++) { int t = fan[k]; uses += _simRStart[t + 1] - _simRStart[t]; }
        return uses;
    }

    int NewParticle(int from)
    {
        int q = _np++;
        _rest[q] = _rest[from];
        _origin[q] = _origin[from];
        _parent[q] = from;
        (_fan[q] ??= new List<int>(4)).Clear();
        _newParticles.Add(q);
        return q;
    }

    // ------------------------------------------------------------------ 0.31.0: cuts (a blast's shred)

    // pairs of simulated triangles cut apart along the edge they shared (lower index in the high half)
    readonly HashSet<long> _cuts = new();
    List<(int t, int u)> _neighbours;

    static long PairKey(int t, int u) => t < u ? ((long)t << 32) | (uint)u : ((long)u << 32) | (uint)t;

    /// <summary>0.31.0: whether triangles <paramref name="t"/> and <paramref name="u"/> were cut apart.</summary>
    public bool IsCut(int t, int u) => _cuts.Count > 0 && _cuts.Contains(PairKey(t, u));

    /// <summary>
    /// 0.31.0: cuts the cloth between simulated triangles <paramref name="t"/> and <paramref name="u"/> along the edge they
    /// share: they no longer hold together there. Each of the edge's two particles is then checked as for a bowtie: where its
    /// triangles now fall into groups joined by no uncut edge, every group but the first gets a new particle. So a line of
    /// cuts across the cloth separates it along that line, and a line that ends inside it leaves a slit. Topology only (no
    /// positions), so every client that makes the same cuts in the same order gets the same mesh. False, and nothing
    /// changed, when the two do not share an edge now, it is already cut, or there is no room.
    /// </summary>
    public bool Cut(int t, int u)
    {
        _newParticles.Clear();
        if ((uint)t >= (uint)_nt || (uint)u >= (uint)_nt || t == u) return false;
        if (_np >= MaxParticles) return false;
        int a = -1, b = -1;
        for (int k = 0; k < 3; k++)
        {
            int x = _tri[3 * t + k];
            if (x != _tri[3 * u] && x != _tri[3 * u + 1] && x != _tri[3 * u + 2]) continue;
            if (a < 0) a = x; else b = x;
        }
        if (b < 0) return false;
        long key = PairKey(t, u);
        if (_cuts.Contains(key)) return false;
        // each separation copies each render corner at the particle at most once
        if (MaxRenderVertices < int.MaxValue && (long)_nv + CornerUses(a) + CornerUses(b) > MaxRenderVertices) return false;
        _cuts.Add(key);
        Cuts++;
        if (a > b) (a, b) = (b, a);
        Separate(a);
        Separate(b);
        return true;
    }

    /// <summary>
    /// 0.31.0: the pairs of simulated triangles that share an edge on the mesh as built (by their corners' original particles,
    /// which splits and cuts never change), each pair once as (lower, higher), in ascending order.
    /// </summary>
    public IReadOnlyList<(int t, int u)> OriginalNeighbours
    {
        get
        {
            if (_neighbours != null) return _neighbours;
            var keys = new long[3 * _nt];
            var owner = new int[3 * _nt];
            for (int t = 0; t < _nt; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = _origin[_tri[3 * t + k]], b = _origin[_tri[3 * t + (k + 1) % 3]];
                    if (a > b) (a, b) = (b, a);
                    keys[3 * t + k] = ((long)a << 32) | (uint)b;
                    owner[3 * t + k] = t;
                }
            Array.Sort(keys, owner);
            var list = new List<(int t, int u)>(3 * _nt / 2);
            for (int i = 0; i < keys.Length;)
            {
                int j = i;
                while (j < keys.Length && keys[j] == keys[i]) j++;
                if (j - i > 1)
                {
                    Array.Sort(owner, i, j - i);
                    for (int x = i; x < j; x++)
                        for (int y = x + 1; y < j; y++)
                            if (owner[x] != owner[y]) list.Add((owner[x], owner[y]));
                }
                i = j;
            }
            list.Sort((p, q) => p.t != q.t ? p.t.CompareTo(q.t) : p.u.CompareTo(q.u));
            // two triangles share at most one edge, but an edge of a non-manifold mesh can hold more than two
            int w = 0;
            for (int i = 0; i < list.Count; i++) if (w == 0 || list[i] != list[w - 1]) list[w++] = list[i];
            list.RemoveRange(w, list.Count - w);
            return _neighbours = list;
        }
    }

    /// <summary>
    /// Separates a bowtie at <paramref name="x"/>: its triangles fall into groups that share an edge at x within a group
    /// and none across (0.31.0: an edge that was cut joins nothing). The group with x's lowest triangle keeps x; every other
    /// group, in the order of its lowest triangle, gets a new particle (while there is room).
    /// </summary>
    void Separate(int x)
    {
        var fan = _fan[x];
        int n = fan.Count;
        if (n < 2) return;
        Span<int> parent = n <= 64 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        bool cuts = _cuts.Count > 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (ShareOtherCorner(fan[i], fan[j], x) && !(cuts && _cuts.Contains(PairKey(fan[i], fan[j])))) Union(parent, i, j);
        int root0 = Find(parent, 0);
        bool split = false;
        for (int i = 1; i < n; i++) if (Find(parent, i) != root0) { split = true; break; }
        if (!split) return;
        // groups in the order of their lowest triangle (the fan is ascending, so the order of first appearance)
        var roots = new List<int>();
        for (int i = 0; i < n; i++) { int r = Find(parent, i); if (r != root0 && !roots.Contains(r)) roots.Add(r); }
        var moves = new List<List<int>>(roots.Count);
        foreach (int r in roots)
        {
            var g = new List<int>();
            for (int i = 0; i < n; i++) if (Find(parent, i) == r) g.Add(fan[i]);
            moves.Add(g);
        }
        foreach (var g in moves)
        {
            if (_np >= MaxParticles) break;
            int y = NewParticle(x);
            MoveTriangles(x, y, g);
        }
        Version++;
    }

    bool ShareOtherCorner(int t, int u, int x)
    {
        for (int a = 0; a < 3; a++)
        {
            int pa = _tri[3 * t + a];
            if (pa == x) continue;
            if (_tri[3 * u] == pa || _tri[3 * u + 1] == pa || _tri[3 * u + 2] == pa) return true;
        }
        return false;
    }

    static int Find(Span<int> parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    static void Union(Span<int> parent, int a, int b)
    {
        a = Find(parent, a); b = Find(parent, b);
        if (a == b) return;
        if (a < b) parent[b] = a; else parent[a] = b;
    }

    readonly List<(int v, int moved)> _touched = new();

    /// <summary>
    /// Moves simulated triangles <paramref name="moving"/> (ascending, all in x's fan) from particle x to particle y, with
    /// their render triangles: a render vertex at x that only moving triangles use goes to y as it is; one that others use
    /// too is copied (a new render vertex at y, appended in ascending order of the vertex it copies).
    /// </summary>
    void MoveTriangles(int x, int y, List<int> moving)
    {
        // render vertices at x used by the moving triangles, and how often
        _touched.Clear();
        foreach (int t in moving)
            foreach (int r in RenderTrianglesOf(t))
                for (int k = 0; k < 3; k++)
                {
                    int v = _rtri[3 * r + k];
                    if (_rvParticle[v] != x) continue;
                    int at = -1;
                    for (int i = 0; i < _touched.Count; i++) if (_touched[i].v == v) { at = i; break; }
                    if (at < 0) _touched.Add((v, 1));
                    else _touched[at] = (v, _touched[at].moved + 1);
                }
        _touched.Sort((a, b) => a.v.CompareTo(b.v));
        foreach (var (v, moved) in _touched)
        {
            if (moved >= _rvUse[v]) { _rvParticle[v] = y; continue; }
            int w = NewRenderVertex(v, y);
            _rvUse[w] = moved;
            _rvUse[v] -= moved;
            foreach (int t in moving)
                foreach (int r in RenderTrianglesOf(t))
                    for (int k = 0; k < 3; k++)
                        if (_rtri[3 * r + k] == v) _rtri[3 * r + k] = w;
        }
        // the simulated triangles and the fans
        foreach (int t in moving)
            for (int k = 0; k < 3; k++)
                if (_tri[3 * t + k] == x) _tri[3 * t + k] = y;
        var fx = _fan[x];
        int keep = 0, m = 0;
        for (int i = 0; i < fx.Count; i++)
        {
            int t = fx[i];
            while (m < moving.Count && moving[m] < t) m++;
            if (m < moving.Count && moving[m] == t) continue;
            fx[keep++] = t;
        }
        fx.RemoveRange(keep, fx.Count - keep);
        var fy = _fan[y];
        fy.AddRange(moving);
        fy.Sort();
    }

    int NewRenderVertex(int copyOf, int particle)
    {
        if (_nv == _rvParticle.Length)
        {
            int cap = Math.Max(16, _rvParticle.Length + _rvParticle.Length / 2);
            Array.Resize(ref _rvParticle, cap); Array.Resize(ref _rvSource, cap); Array.Resize(ref _rvUse, cap); Array.Resize(ref _rvSign, cap);
        }
        int w = _nv++;
        _rvParticle[w] = particle;
        _rvSource[w] = _rvSource[copyOf];
        _rvSign[w] = _rvSign[copyOf];
        _rvUse[w] = 0;
        return w;
    }

    /// <summary>Tests: whether the topology is consistent (fans match the triangles, render corners match their simulated
    /// triangles, use counts add up); null when it is, else what is wrong.</summary>
    public string Validate()
    {
        for (int p = 0; p < _np; p++)
        {
            var f = _fan[p];
            for (int k = 0; k < f.Count; k++)
            {
                int t = f[k];
                if (k > 0 && f[k - 1] >= t) return $"particle {p}'s fan is not ascending";
                if (_tri[3 * t] != p && _tri[3 * t + 1] != p && _tri[3 * t + 2] != p) return $"particle {p}'s fan holds triangle {t}, which does not use it";
            }
        }
        for (int t = 0; t < _nt; t++)
            for (int k = 0; k < 3; k++)
            {
                int p = _tri[3 * t + k];
                if ((uint)p >= (uint)_np) return $"triangle {t} uses particle {p} of {_np}";
                if (!_fan[p].Contains(t)) return $"triangle {t} is missing from particle {p}'s fan";
            }
        var use = new int[_nv];
        for (int r = 0; r < _nr; r++)
        {
            int t = _rtSim[r];
            for (int k = 0; k < 3; k++)
            {
                int v = _rtri[3 * r + k];
                if ((uint)v >= (uint)_nv) return $"render triangle {r} uses vertex {v} of {_nv}";
                use[v]++;
                if (t >= 0)
                {
                    int p = _rvParticle[v];
                    if (_tri[3 * t] != p && _tri[3 * t + 1] != p && _tri[3 * t + 2] != p) return $"render triangle {r}'s corner {k} is at particle {p}, not on its simulated triangle {t}";
                }
            }
        }
        for (int v = 0; v < _nv; v++) if (use[v] != _rvUse[v]) return $"render vertex {v} is used {use[v]} times, counted {_rvUse[v]}";
        return null;
    }
}
