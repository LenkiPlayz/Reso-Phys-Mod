using System.Numerics;
using System.Runtime.CompilerServices;

namespace Softbody.Core;

/// <summary>
/// One paint dab, in the mesh's local (rest) space. <see cref="Color"/> is RGBA in the mesh's own vertex-colour
/// space (the plugin converts the synced colour to the mesh's colour profile first).
/// </summary>
public readonly struct Dab : IEquatable<Dab>
{
    public readonly Vector3 Position;
    public readonly float Radius;
    public readonly Vector4 Color;
    public readonly float Strength;
    public readonly float Hardness;
    /// <summary>A blur dab: instead of moving colours towards <see cref="Color"/> (ignored), each vertex moves
    /// towards the average colour of its surface neighbourhood, which softens transitions.</summary>
    public readonly bool Blur;

    public Dab(Vector3 position, float radius, Vector4 color, float strength, float hardness, bool blur = false)
    {
        Position = position; Radius = radius; Color = color; Strength = strength; Hardness = hardness; Blur = blur;
    }

    /// <summary>A dab with finite values in range (radius &gt; 0, strength and hardness 0..1, colour 0..1);
    /// anything unusable becomes a dab with strength 0, which changes nothing.</summary>
    public Dab Sanitized()
    {
        bool ok = float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z) && float.IsFinite(Radius) && Radius > 0f &&
                  float.IsFinite(Strength) && float.IsFinite(Hardness) &&
                  float.IsFinite(Color.X) && float.IsFinite(Color.Y) && float.IsFinite(Color.Z) && float.IsFinite(Color.W);
        if (!ok) return new Dab(float.IsFinite(Position.X + Position.Y + Position.Z) ? Position : Vector3.Zero, 1e-6f, Vector4.One, 0f, 1f, Blur);
        return new Dab(Position, Radius, Vector4.Clamp(Color, Vector4.Zero, Vector4.One), Math.Clamp(Strength, 0f, 1f), Math.Clamp(Hardness, 0f, 1f), Blur);
    }

    // Bitwise comparison: two dabs are "the same" only if every float is identical (NaN-safe).
    public bool Equals(Dab o) =>
        Bits(Position.X) == Bits(o.Position.X) && Bits(Position.Y) == Bits(o.Position.Y) && Bits(Position.Z) == Bits(o.Position.Z) &&
        Bits(Radius) == Bits(o.Radius) && Bits(Strength) == Bits(o.Strength) && Bits(Hardness) == Bits(o.Hardness) &&
        Bits(Color.X) == Bits(o.Color.X) && Bits(Color.Y) == Bits(o.Color.Y) && Bits(Color.Z) == Bits(o.Color.Z) && Bits(Color.W) == Bits(o.Color.W) &&
        Blur == o.Blur;
    public override bool Equals(object obj) => obj is Dab d && Equals(d);
    public override int GetHashCode() => HashCode.Combine(Position, Radius, Color, Strength, Hardness, Blur);
    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    public override string ToString() => $"{(Blur ? "blur dab" : "dab")} at {Position} r {Radius} colour {Color} strength {Strength} hardness {Hardness}";
}

/// <summary>Where a ray (or a closest-point query) met the rest mesh, in the mesh's local space.</summary>
public struct MeshHit
{
    public float T;            // ray parameter (in units of the ray direction); for closest-point queries the distance
    public int Triangle;       // index of the triangle (its first index is 3 * Triangle)
    public float U, V;         // barycentric weights of the triangle's 2nd and 3rd vertex
    public Vector3 Point;
    public Vector3 Normal;     // geometric normal, unit length (cross(b - a, c - a))
    public bool FrontFace;     // the ray came from the side the normal points to
}

/// <summary>
/// The paint layer of one mesh: effective vertex colours = the mesh's own colours (white when it has none) with
/// the dabs applied in order, c = lerp(c, dab.Color, dab.Strength * falloff(distance / dab.Radius)), on the rest
/// positions. Vertices are bucketed in a uniform grid, so a dab only visits nearby vertices.
///
/// <see cref="Update"/> takes the full current dab list and works out the cheapest exact way to get there from
/// the list it applied last: appended dabs are applied on top (incremental); removed, inserted or changed dabs
/// only recompute the vertices inside those dabs (from the base colour, with every dab in order); anything else
/// is a full recompute. All three give bit-identical colours, because each vertex always sees the same
/// sequence of operations.
///
/// Also answers geometry queries for the brush (ray and closest point against the rest triangles, BVH).
/// No engine types; not thread-safe (the plugin uses it under its gate).
/// </summary>
public sealed class PaintLayer
{
    public const int MaxDabs = 4000;

    readonly Vector3[] _pos;
    Vector3[] _qpos;    // positions the geometry queries (BVH, rays, closest points) use: the rest mesh, or a skinned pose
    readonly Vector4[] _base;
    readonly Vector4[] _col;
    readonly int[] _tris;
    readonly List<Dab> _applied = new();

    // uniform grid over the rest vertices (CSR: the vertices of cell c are _cellVerts[_cellStart[c] .. _cellStart[c + 1]])
    Vector3 _gridOrigin;
    float _cell, _invCell;
    int _gx, _gy, _gz;
    int[] _cellStart, _cellVerts;

    bool[] _mark;
    readonly List<int> _affected = new();

    // Blur support: rest vertex normals (so a blur never mixes the two sides of a thin part) and the mean
    // vertex spacing on the surface (the neighbourhood is never smaller than that).
    Vector3[] _nrm;
    float _spacing;
    readonly List<int> _blurIdx = new(), _nbr = new();
    readonly List<float> _blurW = new();
    readonly List<Vector4> _blurAvg = new();
    /// <summary>Neighbourhood radius a blur dab of radius r averages over.</summary>
    public float BlurKernel(float r) => MathF.Max(0.35f * r, 1.5f * _spacing);

    /// <summary>Test hook: apply dabs by visiting every vertex instead of the grid (same results, slower).</summary>
    public bool BruteForce;

    public int VertexCount => _pos.Length;
    public int TriangleCount => _tris.Length / 3;
    public bool HadBaseColors { get; }
    public ReadOnlySpan<Vector3> RestPositions => _pos;
    public ReadOnlySpan<Vector4> BaseColors => _base;
    public ReadOnlySpan<Vector4> Colors => _col;
    public ReadOnlySpan<int> Triangles => _tris;
    public int DabCount => _applied.Count;
    public Dab AppliedDab(int i) => _applied[i];
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }
    public float GridCellSize => _cell;
    public int GridCells => _gx * _gy * _gz;

    /// <summary>Lowest and highest vertex index whose colour changed since <see cref="ClearDirty"/> (Max &lt; Min = none).</summary>
    public int DirtyMin { get; private set; } = int.MaxValue;
    public int DirtyMax { get; private set; } = -1;
    public bool IsDirty => DirtyMax >= DirtyMin;
    /// <summary>Counts every change of the effective colours.</summary>
    public long Version { get; private set; }
    public void ClearDirty() { DirtyMin = int.MaxValue; DirtyMax = -1; }

    /// <param name="restPositions">mesh-local rest positions</param>
    /// <param name="baseColors">the mesh's own vertex colours (empty = none: white)</param>
    /// <param name="triangles">3 indices per triangle (for the brush's ray queries and the grid's cell size)</param>
    public PaintLayer(ReadOnlySpan<Vector3> restPositions, ReadOnlySpan<Vector4> baseColors, ReadOnlySpan<int> triangles)
    {
        int n = restPositions.Length;
        if (n == 0) throw new ArgumentException("mesh has no vertices");
        _pos = restPositions.ToArray();
        _qpos = _pos;
        _base = new Vector4[n];
        HadBaseColors = baseColors.Length >= n;
        for (int i = 0; i < n; i++)
        {
            var c = HadBaseColors ? baseColors[i] : Vector4.One;
            if (!float.IsFinite(c.X + c.Y + c.Z + c.W)) c = Vector4.One;
            _base[i] = c;
        }
        _col = (Vector4[])_base.Clone();
        var tl = new List<int>(triangles.Length);
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
            tl.Add(a); tl.Add(b); tl.Add(c);
        }
        _tris = tl.ToArray();

        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in _pos)
        {
            if (!float.IsFinite(p.X + p.Y + p.Z)) throw new ArgumentException("mesh has non-finite vertex positions");
            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
        }
        BoundsMin = lo; BoundsMax = hi;
        BuildGrid();

        _nrm = new Vector3[n];
        double area = 0;
        for (int t = 0; t < _tris.Length; t += 3)
        {
            int a = _tris[t], b = _tris[t + 1], c = _tris[t + 2];
            var fn = Vector3.Cross(_pos[b] - _pos[a], _pos[c] - _pos[a]);
            _nrm[a] += fn; _nrm[b] += fn; _nrm[c] += fn;
            area += 0.5 * fn.Length();
        }
        for (int v = 0; v < n; v++) { float l = _nrm[v].Length(); _nrm[v] = l > 1e-20f ? _nrm[v] / l : Vector3.Zero; }
        _spacing = area > 0 ? MathF.Sqrt((float)(area / n)) : _cell * 0.5f;
    }

    // ------------------------------------------------------------------ grid

    void BuildGrid()
    {
        int n = _pos.Length;
        var ext = BoundsMax - BoundsMin;
        float maxExt = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
        double area = 0;
        for (int t = 0; t < _tris.Length; t += 3)
            area += 0.5 * Vector3.Cross(_pos[_tris[t + 1]] - _pos[_tris[t]], _pos[_tris[t + 2]] - _pos[_tris[t]]).Length();
        // About two vertex spacings per cell on the surface (a few vertices per non-empty cell).
        float cell = area > 0 ? 2f * MathF.Sqrt((float)(area / n)) : maxExt / MathF.Max(1f, MathF.Cbrt(n));
        cell = MathF.Max(cell, MathF.Max(maxExt / 512f, 1e-6f));
        long maxCells = Math.Max(64L, 8L * n);
        while (true)
        {
            _gx = (int)(ext.X / cell) + 1; _gy = (int)(ext.Y / cell) + 1; _gz = (int)(ext.Z / cell) + 1;
            if ((long)_gx * _gy * _gz <= maxCells) break;
            cell *= 1.26f;
        }
        _cell = cell; _invCell = 1f / cell; _gridOrigin = BoundsMin;
        int cells = _gx * _gy * _gz;
        _cellStart = new int[cells + 1];
        var cellOf = new int[n];
        for (int v = 0; v < n; v++) { int c = CellOf(_pos[v]); cellOf[v] = c; _cellStart[c + 1]++; }
        for (int c = 0; c < cells; c++) _cellStart[c + 1] += _cellStart[c];
        _cellVerts = new int[n];
        var fill = new int[cells];
        for (int v = 0; v < n; v++) { int c = cellOf[v]; _cellVerts[_cellStart[c] + fill[c]++] = v; }
    }

    int CellOf(Vector3 p)
    {
        var g = (p - _gridOrigin) * _invCell;
        int x = Math.Clamp((int)g.X, 0, _gx - 1), y = Math.Clamp((int)g.Y, 0, _gy - 1), z = Math.Clamp((int)g.Z, 0, _gz - 1);
        return x + _gx * (y + _gy * z);
    }

    // ------------------------------------------------------------------ dabs

    /// <summary>Falloff of a dab at distance fraction x = distance / radius (0..1): 1 inside the hard core
    /// (x &lt;= hardness), then a smoothstep down to 0 at the rim.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Falloff(float x, float hardness)
    {
        if (x <= hardness) return 1f;
        float t = (x - hardness) / (1f - hardness);
        if (t >= 1f) return 0f;
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>What a dab does to one colour at squared distance d2 (the one place the blend is computed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Blend(ref Vector4 c, in Dab d, float d2, float r2, float invR)
    {
        if (!(d2 < r2)) return false;
        float w = d.Strength * Falloff(MathF.Sqrt(d2) * invR, d.Hardness);
        if (!(w > 0f)) return false;
        c += (d.Color - c) * w;
        return true;
    }

    void Touch(int v)
    {
        if (v < DirtyMin) DirtyMin = v;
        if (v > DirtyMax) DirtyMax = v;
    }

    /// <summary>Applies one (sanitized) dab to the current colours; only marked vertices when <paramref name="mask"/> is given.</summary>
    int ApplyDab(in Dab d, bool[] mask)
    {
        if (!(d.Strength > 0f) || !(d.Radius > 0f)) return 0;
        if (d.Blur) return ApplyBlur(d, mask);
        float r2 = d.Radius * d.Radius, invR = 1f / d.Radius;
        int touched = 0;
        if (BruteForce)
        {
            for (int v = 0; v < _pos.Length; v++)
            {
                if (mask != null && !mask[v]) continue;
                if (Blend(ref _col[v], d, Vector3.DistanceSquared(_pos[v], d.Position), r2, invR)) { Touch(v); touched++; }
            }
            return touched;
        }
        var lo = (d.Position - new Vector3(d.Radius) - _gridOrigin) * _invCell;
        var hi = (d.Position + new Vector3(d.Radius) - _gridOrigin) * _invCell;
        if (hi.X < 0 || hi.Y < 0 || hi.Z < 0 || lo.X >= _gx || lo.Y >= _gy || lo.Z >= _gz)
        {
            // Entirely outside the grid's box. Vertices are clamped into the border cells, and every vertex lies
            // inside the box, so nothing can be within the radius.
            return 0;
        }
        int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, _gx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, _gx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, _gy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, _gy - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, _gz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, _gz - 1);
        var cs = _cellStart; var cv = _cellVerts; var pos = _pos; var col = _col;
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = _gx * (y + _gy * z);
                int s = cs[row + x0], e = cs[row + x1 + 1];   // cells x0..x1 of this row are contiguous in CSR
                for (int k = s; k < e; k++)
                {
                    int v = cv[k];
                    if (mask != null && !mask[v]) continue;
                    if (Blend(ref col[v], d, Vector3.DistanceSquared(pos[v], d.Position), r2, invR)) { Touch(v); touched++; }
                }
            }
        return touched;
    }

    /// <summary>Indices (ascending) of the vertices closer than r to c: grid cells, or every vertex when <see cref="BruteForce"/>.</summary>
    void Gather(Vector3 c, float r, List<int> into)
    {
        into.Clear();
        float r2 = r * r;
        if (BruteForce)
        {
            for (int v = 0; v < _pos.Length; v++) if (Vector3.DistanceSquared(_pos[v], c) < r2) into.Add(v);
            return;
        }
        var lo = (c - new Vector3(r) - _gridOrigin) * _invCell;
        var hi = (c + new Vector3(r) - _gridOrigin) * _invCell;
        if (hi.X < 0 || hi.Y < 0 || hi.Z < 0 || lo.X >= _gx || lo.Y >= _gy || lo.Z >= _gz) return;
        int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, _gx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, _gx - 1);
        int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, _gy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, _gy - 1);
        int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, _gz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, _gz - 1);
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = _gx * (y + _gy * z);
                for (int k = _cellStart[row + x0], e = _cellStart[row + x1 + 1]; k < e; k++)
                {
                    int v = _cellVerts[k];
                    if (Vector3.DistanceSquared(_pos[v], c) < r2) into.Add(v);
                }
            }
        into.Sort(); // the same order as brute force, so both sum in the same order and give identical bits
    }

    /// <summary>
    /// A blur dab: every vertex within the radius moves (by strength x falloff) towards the weighted average of
    /// the current colours within <see cref="BlurKernel"/> of it (tent weights; neighbours facing the other way,
    /// such as the far side of a thin part, are left out). All averages are taken from the colours before this
    /// dab and written afterwards, so the result does not depend on visiting order.
    /// </summary>
    int ApplyBlur(in Dab d, bool[] mask)
    {
        float invR = 1f / d.Radius;
        float k = BlurKernel(d.Radius), invK = 1f / k;
        Gather(d.Position, d.Radius, _blurIdx);
        _blurW.Clear(); _blurAvg.Clear();
        int n = 0;
        for (int i = 0; i < _blurIdx.Count; i++)
        {
            int v = _blurIdx[i];
            float w = mask != null && !mask[v] ? 0f
                : d.Strength * Falloff(MathF.Sqrt(Vector3.DistanceSquared(_pos[v], d.Position)) * invR, d.Hardness);
            if (!(w > 0f)) { _blurIdx[i] = -1; _blurW.Add(0f); _blurAvg.Add(default); continue; }
            Gather(_pos[v], k, _nbr);
            Vector4 sum = default; float ws = 0f;
            var nv = _nrm[v];
            foreach (int j in _nbr)
            {
                if (Vector3.Dot(nv, _nrm[j]) < -0.2f) continue;
                float wj = 1f - MathF.Sqrt(Vector3.DistanceSquared(_pos[j], _pos[v])) * invK;
                if (!(wj > 0f)) continue;
                sum += _col[j] * wj; ws += wj;
            }
            _blurW.Add(w);
            _blurAvg.Add(ws > 0f ? sum / ws : _col[v]);
            n++;
        }
        if (n == 0) return 0;
        int touched = 0;
        for (int i = 0; i < _blurIdx.Count; i++)
        {
            int v = _blurIdx[i];
            if (v < 0) continue;
            _col[v] += (_blurAvg[i] - _col[v]) * _blurW[i];
            Touch(v); touched++;
        }
        return touched;
    }

    public enum UpdateKind { None, Appended, Regional, Full }

    public readonly struct UpdateResult
    {
        public readonly UpdateKind Kind;
        public readonly int Dabs;        // dabs applied
        public readonly int Vertices;    // vertex colours changed (Regional/Full: recomputed)
        public UpdateResult(UpdateKind kind, int dabs, int vertices) { Kind = kind; Dabs = dabs; Vertices = vertices; }
        public override string ToString() => $"{Kind} ({Dabs} dabs, {Vertices} vertices)";
    }

    /// <summary>Brings the effective colours to <paramref name="dabs"/> (the whole current list, in order; at most
    /// <see cref="MaxDabs"/> are used). The dabs must already be sanitized (<see cref="Dab.Sanitized"/>).</summary>
    public UpdateResult Update(ReadOnlySpan<Dab> dabs)
    {
        if (dabs.Length > MaxDabs) dabs = dabs.Slice(0, MaxDabs);
        int oldN = _applied.Count, newN = dabs.Length;
        int p = 0;
        while (p < oldN && p < newN && _applied[p].Equals(dabs[p])) p++;
        if (p == oldN)
        {
            if (newN == oldN) return new UpdateResult(UpdateKind.None, 0, 0);
            int touched = 0;
            for (int i = p; i < newN; i++) { touched += ApplyDab(dabs[i], null); _applied.Add(dabs[i]); }
            Version++;
            return new UpdateResult(UpdateKind.Appended, newN - p, touched);
        }
        int s = 0;
        while (s < oldN - p && s < newN - p && _applied[oldN - 1 - s].Equals(dabs[newN - 1 - s])) s++;
        // The changed dabs: those outside a common subsequence of the old and the new list. A vertex none of them
        // reaches sees the same dabs in the same order before and after, so only the changed dabs' vertices need work.
        DiffMiddle(dabs, p, oldN - s, newN - s);
        UpdateResult res;
        // A blur dab reads its neighbours' colours, so a vertex that no changed dab reaches can still change:
        // the regional shortcut is only exact for plain dabs.
        bool anyBlur = false;
        foreach (var d in dabs) if (d.Blur) { anyBlur = true; break; }
        if (!anyBlur) foreach (var d in _changed) if (d.Blur) { anyBlur = true; break; }
        if (anyBlur || newN == 0 || _changed.Count > 256 || _changed.Count > newN / 2 + 1)
            res = RecomputeAll(dabs);
        else
            res = Recompute(dabs);
        _applied.Clear();
        foreach (var d in dabs) _applied.Add(d);
        Version++;
        return res;
    }

    readonly List<Dab> _changed = new();

    /// <summary>Fills <see cref="_changed"/> for the middle parts old[p..oldEnd) and new[p..newEnd): a pure removal (the new
    /// part is a subsequence of the old one, e.g. erasing) or a pure insertion only lists the removed or inserted dabs;
    /// anything else lists both middle parts whole.</summary>
    void DiffMiddle(ReadOnlySpan<Dab> dabs, int p, int oldEnd, int newEnd)
    {
        _changed.Clear();
        int no = oldEnd - p, nn = newEnd - p;
        if (nn <= no)
        {
            int j = p;
            for (int i = p; i < oldEnd; i++)
                if (j < newEnd && _applied[i].Equals(dabs[j])) j++;
                else _changed.Add(_applied[i]);
            if (j == newEnd) return;
            _changed.Clear();
        }
        if (no <= nn)
        {
            int i = p;
            for (int j = p; j < newEnd; j++)
                if (i < oldEnd && _applied[i].Equals(dabs[j])) i++;
                else _changed.Add(dabs[j]);
            if (i == oldEnd) return;
            _changed.Clear();
        }
        for (int i = p; i < oldEnd; i++) _changed.Add(_applied[i]);
        for (int j = p; j < newEnd; j++) _changed.Add(dabs[j]);
    }

    /// <summary>Every vertex from its base colour with all dabs in order.</summary>
    public UpdateResult RecomputeAll(ReadOnlySpan<Dab> dabs)
    {
        if (dabs.Length > MaxDabs) dabs = dabs.Slice(0, MaxDabs);
        _base.AsSpan().CopyTo(_col);
        DirtyMin = 0; DirtyMax = _pos.Length - 1;
        foreach (var d in dabs) ApplyDab(d, null);
        _applied.Clear();
        foreach (var d in dabs) _applied.Add(d);
        Version++;
        return new UpdateResult(UpdateKind.Full, dabs.Length, _pos.Length);
    }

    /// <summary>Only the vertices inside the changed dabs (<see cref="_changed"/>) are recomputed, from their base colour
    /// with every dab of the new list in order.</summary>
    UpdateResult Recompute(ReadOnlySpan<Dab> dabs)
    {
        _mark ??= new bool[_pos.Length];
        _affected.Clear();
        var rlo = new Vector3(float.MaxValue); var rhi = new Vector3(float.MinValue);
        void MarkSphere(in Dab d)
        {
            if (!(d.Radius > 0f)) return;
            float r2 = d.Radius * d.Radius;
            // every vertex the dab could have touched (whatever its strength)
            var lo = (d.Position - new Vector3(d.Radius) - _gridOrigin) * _invCell;
            var hi = (d.Position + new Vector3(d.Radius) - _gridOrigin) * _invCell;
            if (hi.X < 0 || hi.Y < 0 || hi.Z < 0 || lo.X >= _gx || lo.Y >= _gy || lo.Z >= _gz) return;
            int x0 = Math.Clamp((int)MathF.Floor(lo.X), 0, _gx - 1), x1 = Math.Clamp((int)MathF.Floor(hi.X), 0, _gx - 1);
            int y0 = Math.Clamp((int)MathF.Floor(lo.Y), 0, _gy - 1), y1 = Math.Clamp((int)MathF.Floor(hi.Y), 0, _gy - 1);
            int z0 = Math.Clamp((int)MathF.Floor(lo.Z), 0, _gz - 1), z1 = Math.Clamp((int)MathF.Floor(hi.Z), 0, _gz - 1);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int row = _gx * (y + _gy * z);
                    for (int k = _cellStart[row + x0], e = _cellStart[row + x1 + 1]; k < e; k++)
                    {
                        int v = _cellVerts[k];
                        if (_mark[v] || !(Vector3.DistanceSquared(_pos[v], d.Position) < r2)) continue;
                        _mark[v] = true; _affected.Add(v);
                        rlo = Vector3.Min(rlo, _pos[v]); rhi = Vector3.Max(rhi, _pos[v]);
                    }
                }
        }
        foreach (var d in _changed) MarkSphere(d);
        if (_affected.Count == 0) return new UpdateResult(UpdateKind.Regional, 0, 0);

        foreach (int v in _affected) { _col[v] = _base[v]; Touch(v); }
        int applied = 0;
        foreach (var d in dabs)
        {
            // only dabs whose box overlaps the affected region can reach an affected vertex
            var dlo = d.Position - new Vector3(d.Radius); var dhi = d.Position + new Vector3(d.Radius);
            if (dhi.X < rlo.X || dhi.Y < rlo.Y || dhi.Z < rlo.Z || dlo.X > rhi.X || dlo.Y > rhi.Y || dlo.Z > rhi.Z) continue;
            ApplyDab(d, _mark);
            applied++;
        }
        foreach (int v in _affected) _mark[v] = false;
        return new UpdateResult(UpdateKind.Regional, applied, _affected.Count);
    }

    /// <summary>Indices (ascending) of the dabs whose centre is closer than <paramref name="radius"/> to <paramref name="center"/>.</summary>
    public static void FindDabsWithin(ReadOnlySpan<Dab> dabs, Vector3 center, float radius, List<int> into)
    {
        into.Clear();
        float r2 = radius * radius;
        for (int i = 0; i < dabs.Length; i++)
            if (Vector3.DistanceSquared(dabs[i].Position, center) < r2) into.Add(i);
    }

    // ------------------------------------------------------------------ softness

    /// <summary>Softness (one per vertex) and hue-band weights (<see cref="HueBands.Count"/> per vertex) from the effective colours.</summary>
    public void ComputeMaps(SoftnessSource source, bool invert, Span<float> softness, Span<float> bands)
    {
        int n = _pos.Length;
        if (softness.Length < n) throw new ArgumentException("softness buffer too small");
        bool doBands = bands.Length >= n * HueBands.Count;
        for (int v = 0; v < n; v++)
        {
            var c = _col[v];
            softness[v] = SoftnessMap.FromColor(c.X, c.Y, c.Z, c.W, source, invert);
            if (doBands) HueBands.Weights(c.X, c.Y, c.Z, bands.Slice(v * HueBands.Count, HueBands.Count));
        }
    }

    // ------------------------------------------------------------------ geometry queries (BVH over the rest triangles)

    struct Node { public Vector3 Min, Max; public int Start, Count, Right; } // leaf: Count > 0 (triangles _bvhTris[Start..Start+Count)); inner: left = this + 1, right = Right
    Node[] _nodes;
    int _nodeCount;
    int[] _bvhTris;
    Vector3[] _centroid;

    void EnsureBvh()
    {
        if (_nodes != null) return;
        int nt = _tris.Length / 3;
        _bvhTris = new int[nt];
        _centroid = new Vector3[nt];
        for (int t = 0; t < nt; t++)
        {
            _bvhTris[t] = t;
            _centroid[t] = (_qpos[_tris[3 * t]] + _qpos[_tris[3 * t + 1]] + _qpos[_tris[3 * t + 2]]) / 3f;
        }
        _nodes = new Node[Math.Max(1, 2 * nt)];
        _nodeCount = 0;
        if (nt > 0) BuildNode(0, nt);
    }

    int BuildNode(int start, int end)
    {
        int idx = _nodeCount++;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        var clo = new Vector3(float.MaxValue); var chi = new Vector3(float.MinValue);
        for (int i = start; i < end; i++)
        {
            int t = _bvhTris[i];
            for (int k = 0; k < 3; k++) { var p = _qpos[_tris[3 * t + k]]; lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            clo = Vector3.Min(clo, _centroid[t]); chi = Vector3.Max(chi, _centroid[t]);
        }
        _nodes[idx].Min = lo; _nodes[idx].Max = hi;
        int count = end - start;
        if (count <= 4)
        {
            _nodes[idx].Start = start; _nodes[idx].Count = count;
            return idx;
        }
        var ce = chi - clo;
        int axis = ce.X >= ce.Y && ce.X >= ce.Z ? 0 : ce.Y >= ce.Z ? 1 : 2;
        float split = 0.5f * (Axis(clo, axis) + Axis(chi, axis));
        int mid = start;
        for (int i = start; i < end; i++)
            if (Axis(_centroid[_bvhTris[i]], axis) < split) { (_bvhTris[i], _bvhTris[mid]) = (_bvhTris[mid], _bvhTris[i]); mid++; }
        if (mid == start || mid == end) mid = start + count / 2; // all centroids on one side (degenerate): split by count
        BuildNode(start, mid);
        _nodes[idx].Right = BuildNode(mid, end);
        _nodes[idx].Count = 0;
        return idx;
    }

    static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    static float SafeInv(float d) => MathF.Abs(d) > 1e-30f ? 1f / d : (BitConverter.SingleToInt32Bits(d) < 0 ? -1e30f : 1e30f);

    static bool RayBox(Vector3 o, Vector3 invD, Vector3 min, Vector3 max, float tMax, out float tEnter)
    {
        var t0 = (min - o) * invD; var t1 = (max - o) * invD;
        var tn = Vector3.Min(t0, t1); var tf = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(tn.X, tn.Y), MathF.Max(tn.Z, 0f));
        float exit = MathF.Min(MathF.Min(tf.X, tf.Y), MathF.Min(tf.Z, tMax));
        tEnter = enter;
        return enter <= exit;
    }

    /// <summary>
    /// Nearest hit of the ray origin + t * dir (0 &lt;= t &lt;= tMax) with the rest triangles. Front faces (seen from the
    /// side their normal points to) win: a back face is only returned when the ray meets no front face at all.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 dir, float tMax, out MeshHit hit)
    {
        hit = default;
        EnsureBvh();
        if (_nodeCount == 0 || !(dir.LengthSquared() > 0f) || !float.IsFinite(origin.X + origin.Y + origin.Z + dir.X + dir.Y + dir.Z)) return false;
        // no infinities: 0 * inf would be NaN for rays that start exactly on a box face (axis-aligned rays on vertex rows)
        var invD = new Vector3(SafeInv(dir.X), SafeInv(dir.Y), SafeInv(dir.Z));
        float bestFront = tMax, bestAny = tMax;
        int frontTri = -1, anyTri = -1;
        float fu = 0, fv = 0, au = 0, av = 0;
        Span<int> stack = stackalloc int[128];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref var nd = ref _nodes[ni];
            if (!RayBox(origin, invD, nd.Min, nd.Max, bestFront, out _)) continue;
            if (nd.Count > 0)
            {
                for (int i = nd.Start; i < nd.Start + nd.Count; i++)
                {
                    int t = _bvhTris[i];
                    if (!RayTriangle(origin, dir, t, out float tt, out float u, out float v, out bool front)) continue;
                    if (tt < 0f || tt > tMax) continue;
                    if (front) { if (frontTri < 0 ? tt <= bestFront : tt < bestFront) { bestFront = tt; frontTri = t; fu = u; fv = v; } }
                    else if (anyTri < 0 ? tt <= bestAny : tt < bestAny) { bestAny = tt; anyTri = t; au = u; av = v; }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                int left = ni + 1, right = nd.Right;
                bool hl = RayBox(origin, invD, _nodes[left].Min, _nodes[left].Max, bestFront, out float el);
                bool hr = RayBox(origin, invD, _nodes[right].Min, _nodes[right].Max, bestFront, out float er);
                // nearer child last, so it is popped first
                if (hl && hr) { if (el <= er) { stack[sp++] = right; stack[sp++] = left; } else { stack[sp++] = left; stack[sp++] = right; } }
                else if (hl) stack[sp++] = left;
                else if (hr) stack[sp++] = right;
            }
        }
        int tri = frontTri >= 0 ? frontTri : anyTri;
        if (tri < 0) return false;
        bool isFront = frontTri >= 0;
        hit.T = isFront ? bestFront : bestAny;
        hit.Triangle = tri;
        hit.U = isFront ? fu : au; hit.V = isFront ? fv : av;
        hit.Point = origin + dir * hit.T;
        hit.Normal = TriangleNormal(tri);
        hit.FrontFace = isFront;
        return true;
    }

    bool RayTriangle(Vector3 o, Vector3 d, int t, out float tt, out float u, out float v, out bool front)
    {
        tt = u = v = 0; front = false;
        var a = _qpos[_tris[3 * t]]; var b = _qpos[_tris[3 * t + 1]]; var c = _qpos[_tris[3 * t + 2]];
        var e1 = b - a; var e2 = c - a;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);           // = -dot(d, cross(e1, e2)): > 0 when the ray meets the front side
        if (MathF.Abs(det) < 1e-20f) return false;
        float inv = 1f / det;
        var s = o - a;
        u = Vector3.Dot(s, pv) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, e1);
        v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        tt = Vector3.Dot(e2, q) * inv;
        front = det > 0f;
        return float.IsFinite(tt);
    }

    Vector3 TriangleNormal(int t)
    {
        var a = _qpos[_tris[3 * t]]; var b = _qpos[_tris[3 * t + 1]]; var c = _qpos[_tris[3 * t + 2]];
        var n = Vector3.Cross(b - a, c - a);
        float l = n.Length();
        return l > 0 ? n / l : Vector3.UnitY;
    }

    /// <summary>The closest point of the rest triangles to <paramref name="p"/>, if one is within <paramref name="maxDist"/>.
    /// hit.T is the distance; FrontFace says whether p is on the side the normal points to.</summary>
    public bool ClosestPoint(Vector3 p, float maxDist, out MeshHit hit)
    {
        hit = default;
        EnsureBvh();
        if (_nodeCount == 0 || !float.IsFinite(p.X + p.Y + p.Z) || !(maxDist > 0f)) return false;
        float best2 = maxDist * maxDist;
        int bestTri = -1; Vector3 bestPt = default; float bu = 0, bv = 0;
        Span<int> stack = stackalloc int[128];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref var nd = ref _nodes[ni];
            var q = Vector3.Clamp(p, nd.Min, nd.Max);
            if (Vector3.DistanceSquared(p, q) > best2) continue;
            if (nd.Count > 0)
            {
                for (int i = nd.Start; i < nd.Start + nd.Count; i++)
                {
                    int t = _bvhTris[i];
                    var cp = ClosestOnTriangle(p, t, out float u, out float v);
                    float d2 = Vector3.DistanceSquared(p, cp);
                    if (d2 <= best2) { best2 = d2; bestTri = t; bestPt = cp; bu = u; bv = v; }
                }
            }
            else if (sp + 2 <= stack.Length)
            {
                int left = ni + 1, right = nd.Right;
                // visit the nearer child first (pushed last)
                float dl = Vector3.DistanceSquared(p, Vector3.Clamp(p, _nodes[left].Min, _nodes[left].Max));
                float dr = Vector3.DistanceSquared(p, Vector3.Clamp(p, _nodes[right].Min, _nodes[right].Max));
                if (dl < dr) { stack[sp++] = right; stack[sp++] = left; }
                else { stack[sp++] = left; stack[sp++] = right; }
            }
        }
        if (bestTri < 0) return false;
        hit.T = MathF.Sqrt(best2);
        hit.Triangle = bestTri; hit.U = bu; hit.V = bv; hit.Point = bestPt;
        hit.Normal = TriangleNormal(bestTri);
        hit.FrontFace = Vector3.Dot(p - bestPt, hit.Normal) >= 0f;
        return true;
    }

    /// <summary>Closest point on triangle t to p (Ericson, Real-Time Collision Detection 5.1.5), with barycentrics of b and c.</summary>
    Vector3 ClosestOnTriangle(Vector3 p, int t, out float u, out float v)
    {
        var a = _qpos[_tris[3 * t]]; var b = _qpos[_tris[3 * t + 1]]; var c = _qpos[_tris[3 * t + 2]];
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { u = 0; v = 0; return a; }
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { u = 1; v = 0; return b; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float w = d1 / (d1 - d3); u = w; v = 0; return a + w * ab; }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { u = 0; v = 1; return c; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float w = d2 / (d2 - d6); u = 0; v = w; return a + w * ac; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            u = 1f - w; v = w;
            return b + w * (c - b);
        }
        float denom = 1f / (va + vb + vc);
        u = vb * denom; v = vc * denom;
        return a + ab * u + ac * v;
    }

    // ------------------------------------------------------------------ posed queries (skinned meshes)

    /// <summary>The positions ray and closest-point queries run against (the rest mesh unless a pose was set).</summary>
    public ReadOnlySpan<Vector3> QueryPositions => _qpos;
    public bool HasQueryPose => !ReferenceEquals(_qpos, _pos);

    /// <summary>
    /// Ray and closest-point queries run against these positions from now on (e.g. a skinned mesh's CPU-skinned pose),
    /// while paint stays on the rest mesh. The BVH keeps its topology and only its boxes are refitted. Map a hit back
    /// to the rest mesh with <see cref="RestPoint"/>.
    /// </summary>
    public void SetQueryPositions(ReadOnlySpan<Vector3> posed)
    {
        if (posed.Length < _pos.Length) throw new ArgumentException("query positions: one per vertex");
        if (ReferenceEquals(_qpos, _pos)) _qpos = new Vector3[_pos.Length];
        posed.Slice(0, _pos.Length).CopyTo(_qpos);
        Refit();
    }

    /// <summary>Queries run against the rest mesh again.</summary>
    public void ResetQueryPositions()
    {
        if (ReferenceEquals(_qpos, _pos)) return;
        _qpos = _pos;
        Refit();
    }

    /// <summary>Recomputes every BVH box from the query positions (children have larger indices than their parent).</summary>
    void Refit()
    {
        if (_nodes == null) { EnsureBvh(); return; }
        for (int ni = _nodeCount - 1; ni >= 0; ni--)
        {
            ref var nd = ref _nodes[ni];
            if (nd.Count > 0)
            {
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                for (int i = nd.Start; i < nd.Start + nd.Count; i++)
                {
                    int t = _bvhTris[i];
                    var a = _qpos[_tris[3 * t]]; var b = _qpos[_tris[3 * t + 1]]; var c = _qpos[_tris[3 * t + 2]];
                    lo = Vector3.Min(lo, Vector3.Min(a, Vector3.Min(b, c))); hi = Vector3.Max(hi, Vector3.Max(a, Vector3.Max(b, c)));
                }
                nd.Min = lo; nd.Max = hi;
            }
            else
            {
                ref var l = ref _nodes[ni + 1]; ref var r = ref _nodes[nd.Right];
                nd.Min = Vector3.Min(l.Min, r.Min); nd.Max = Vector3.Max(l.Max, r.Max);
            }
        }
    }

    /// <summary>The hit's place on the rest mesh: the same triangle and barycentric weights on the rest positions.</summary>
    public Vector3 RestPoint(in MeshHit hit)
    {
        int t = hit.Triangle;
        if ((uint)t >= (uint)(_tris.Length / 3)) return hit.Point;
        float w0 = 1f - hit.U - hit.V;
        return _pos[_tris[3 * t]] * w0 + _pos[_tris[3 * t + 1]] * hit.U + _pos[_tris[3 * t + 2]] * hit.V;
    }

    /// <summary>The three vertex indices of triangle t.</summary>
    public (int A, int B, int C) TriangleVertices(int t) => (_tris[3 * t], _tris[3 * t + 1], _tris[3 * t + 2]);

    /// <summary>The effective colour at a hit (interpolated over the hit triangle).</summary>
    public Vector4 SampleColor(in MeshHit hit)
    {
        int t = hit.Triangle;
        if ((uint)t >= (uint)(_tris.Length / 3)) return Vector4.One;
        float w0 = 1f - hit.U - hit.V;
        return _col[_tris[3 * t]] * w0 + _col[_tris[3 * t + 1]] * hit.U + _col[_tris[3 * t + 2]] * hit.V;
    }
}
