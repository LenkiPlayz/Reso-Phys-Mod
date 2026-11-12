using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// Normals (and, since 0.26.3, tangents) for the moved render vertices, in bind space. Each moving vertex looks at the
/// triangles around its (welded) position that face the same way as its own normal: on a double-sided card or a fin, the
/// two sides' vertices share a position but each only follows its own side. The vertex's normal is rotated by how much the
/// average of those triangles' normals turned from rest, so hard edges, seams and the artist's normals keep their shape
/// and only the change from the wobble is added.
///
/// 0.26.3, blendshapes and normal maps:
/// <list type="bullet">
/// <item>With blendshapes on, "rest" is the shape the blendshapes make. The offsets are relative to that shape, so the
/// turn is measured on the triangles as the blendshapes put them (before, it was measured on the mesh without them: on a
/// strongly shaped part a bulge then turned the normals about the wrong axis).</item>
/// <item>The turn is applied to what the renderer would show without the softbody: the vertex's own normal and tangent
/// plus its blendshape deltas. The renderer adds those deltas itself, so they are taken off the output again.</item>
/// <item>The tangent turns with the normal, so the normal map's frame keeps its angle to the normal. Before, the tangent
/// stayed at rest while the normal turned, and the normal map shaded with a skewed frame wherever the flesh moved.</item>
/// </list>
/// </summary>
public sealed class FleshNormals
{
    readonly int[] _tris;            // affected triangles, as local (welded) indices
    readonly Vector3[] _rest;        // local welded rest positions
    readonly int[] _start, _list;    // per moving render vertex (embedding order): its own-side triangles (CSR)
    readonly Vector3[] _restAvg;     // per moving vertex: the normalised sum of those triangles' rest normals (zero: unusable)
    readonly int[] _local;           // per moving vertex: its local welded index
    readonly Vector3[] _cur, _face;
    // 0.26.3: per shape, the local welded vertices it moves and by how much, and the moving vertices whose normal or
    // tangent it changes and by how much (null where it changes none)
    readonly int[][] _shapeLocal, _shapeMove;
    readonly Vector3[][] _shapeDp, _shapeDn, _shapeDt;
    readonly Vector3[] _shaped, _faceShaped;   // local positions with this frame's blendshapes, and their face normals
    readonly Vector3[] _dn, _dt;               // per moving vertex: this frame's blendshape normal and tangent change
    bool _shapesOn;                            // whether the last Compute had a blendshape on here
    public int TriangleCount => _tris.Length / 3;
    /// <summary>0.26.3: blendshapes that move or turn anything here.</summary>
    public int ShapeCount { get; }

    /// <param name="moving">the moving render vertices (the embedding's order)</param>
    public FleshNormals(Vector3[] pos, Vector3[] nrm, int[] tris, int[] renderToWeld, ReadOnlySpan<int> moving)
        : this(pos, nrm, tris, renderToWeld, moving, null) { }

    /// <param name="moving">the moving render vertices (the embedding's order)</param>
    /// <param name="shapes">0.26.3: the mesh's blendshapes (frame 0), or null. Position deltas are used for the vertices
    /// around the moving ones (any render vertex of a welded position will do), normal and tangent deltas for the moving
    /// vertices themselves.</param>
    public FleshNormals(Vector3[] pos, Vector3[] nrm, int[] tris, int[] renderToWeld, ReadOnlySpan<int> moving, BlendshapeDeltas shapes)
    {
        int nw = 0; foreach (int w in renderToWeld) nw = Math.Max(nw, w + 1);
        var movingWeld = new bool[nw];
        foreach (int v in moving) movingWeld[renderToWeld[v]] = true;
        var localOf = new int[nw]; Array.Fill(localOf, -1);
        var rest = new List<Vector3>(); var lt = new List<int>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = renderToWeld[tris[t]], b = renderToWeld[tris[t + 1]], c = renderToWeld[tris[t + 2]];
            if (a == b || b == c || a == c) continue;
            if (!movingWeld[a] && !movingWeld[b] && !movingWeld[c]) continue;
            if (localOf[a] < 0) { localOf[a] = rest.Count; rest.Add(pos[tris[t]]); }
            if (localOf[b] < 0) { localOf[b] = rest.Count; rest.Add(pos[tris[t + 1]]); }
            if (localOf[c] < 0) { localOf[c] = rest.Count; rest.Add(pos[tris[t + 2]]); }
            lt.Add(localOf[a]); lt.Add(localOf[b]); lt.Add(localOf[c]);
        }
        _tris = lt.ToArray(); _rest = rest.ToArray();
        int nl = _rest.Length, nt = _tris.Length / 3;
        _cur = new Vector3[nl]; _face = new Vector3[nt];
        FaceNormals(_rest, _face);

        // triangles around each local vertex
        var deg = new int[nl + 1];
        foreach (int l in _tris) deg[l + 1]++;
        for (int i = 0; i < nl; i++) deg[i + 1] += deg[i];
        var around = new int[_tris.Length]; var fill = (int[])deg.Clone();
        for (int t = 0; t < nt; t++) for (int k = 0; k < 3; k++) around[fill[_tris[t * 3 + k]]++] = t;

        _local = new int[moving.Length]; _restAvg = new Vector3[moving.Length];
        var start = new List<int> { 0 }; var list = new List<int>();
        for (int k = 0; k < moving.Length; k++)
        {
            int v = moving[k];
            int l = localOf[renderToWeld[v]];
            _local[k] = l;
            var own = nrm != null && v < nrm.Length ? nrm[v] : Vector3.Zero;
            var sum = Vector3.Zero;
            for (int q = deg[l]; q < deg[l + 1]; q++)
            {
                int t = around[q];
                if (own != Vector3.Zero && Vector3.Dot(_face[t], own) <= 0f) continue; // the other side of a card or fin
                list.Add(t); sum += _face[t];
            }
            start.Add(list.Count);
            float len = sum.Length();
            _restAvg[k] = len > 1e-20f ? sum / len : Vector3.Zero;
        }
        _start = start.ToArray(); _list = list.ToArray();

        // 0.26.3: the blendshapes in local and moving-vertex indices
        int ns = shapes?.Count ?? 0;
        _shapeLocal = new int[ns][]; _shapeMove = new int[ns][];
        _shapeDp = new Vector3[ns][]; _shapeDn = new Vector3[ns][]; _shapeDt = new Vector3[ns][];
        var kOf = new Dictionary<int, int>(moving.Length);
        for (int k = 0; k < moving.Length; k++) kOf.TryAdd(moving[k], k);
        var seen = new int[nl];
        var li = new List<int>(); var ld = new List<Vector3>();
        var mi = new List<int>(); var mn = new List<Vector3>(); var mt = new List<Vector3>();
        int used = 0;
        for (int b = 0; b < ns; b++)
        {
            shapes.GetShape(b, out var vi, out var dp, out var dn, out var dt);
            li.Clear(); ld.Clear(); mi.Clear(); mn.Clear(); mt.Clear();
            for (int j = 0; j < vi.Length; j++)
            {
                int v = vi[j];
                if ((uint)v >= (uint)renderToWeld.Length) continue;
                int l = localOf[renderToWeld[v]];
                var d = j < dp.Length ? dp[j] : Vector3.Zero;
                if (l >= 0 && seen[l] != b + 1 && d != Vector3.Zero && float.IsFinite(d.X + d.Y + d.Z)) { seen[l] = b + 1; li.Add(l); ld.Add(d); }
                var n = dn != null && j < dn.Length ? dn[j] : Vector3.Zero;
                var t = dt != null && j < dt.Length ? dt[j] : Vector3.Zero;
                if (!float.IsFinite(n.X + n.Y + n.Z)) n = Vector3.Zero;
                if (!float.IsFinite(t.X + t.Y + t.Z)) t = Vector3.Zero;
                if ((n != Vector3.Zero || t != Vector3.Zero) && kOf.TryGetValue(v, out int k)) { mi.Add(k); mn.Add(n); mt.Add(t); }
            }
            if (li.Count > 0) { _shapeLocal[b] = li.ToArray(); _shapeDp[b] = ld.ToArray(); }
            if (mi.Count > 0) { _shapeMove[b] = mi.ToArray(); _shapeDn[b] = mn.ToArray(); _shapeDt[b] = mt.ToArray(); }
            if (li.Count > 0 || mi.Count > 0) used++;
        }
        ShapeCount = used;
        _shaped = used > 0 ? new Vector3[nl] : Array.Empty<Vector3>();
        _faceShaped = used > 0 ? new Vector3[nt] : Array.Empty<Vector3>();
        _dn = used > 0 ? new Vector3[moving.Length] : Array.Empty<Vector3>();
        _dt = used > 0 ? new Vector3[moving.Length] : Array.Empty<Vector3>();
    }

    void FaceNormals(Vector3[] p, Vector3[] dst)
    {
        for (int t = 0; t < dst.Length; t++)
        {
            int a = _tris[t * 3], b = _tris[t * 3 + 1], c = _tris[t * 3 + 2];
            dst[t] = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
        }
    }

    /// <summary>The normalised sum of moving vertex k's own-side face normals (zero when unusable).</summary>
    Vector3 Average(Vector3[] face, int k)
    {
        var sum = Vector3.Zero;
        for (int q = _start[k]; q < _start[k + 1]; q++) sum += face[_list[q]];
        float len = sum.Length();
        return len > 1e-20f && float.IsFinite(len) ? sum / len : Vector3.Zero;
    }

    /// <summary>0.26.3: this frame's blendshapes here (the shaped local positions, and the moving vertices' normal and
    /// tangent changes). False when none is on.</summary>
    bool SetShapes(ReadOnlySpan<float> weights)
    {
        _shapesOn = false;
        if (ShapeCount == 0) return false;
        for (int b = 0; b < _shapeLocal.Length && b < weights.Length; b++)
        {
            float w = weights[b];
            if (!float.IsFinite(w) || MathF.Abs(w) <= 1e-6f) continue;
            var li = _shapeLocal[b]; var mi = _shapeMove[b];
            if (li == null && mi == null) continue;
            if (!_shapesOn) { Array.Copy(_rest, _shaped, _rest.Length); Array.Clear(_dn); Array.Clear(_dt); _shapesOn = true; }
            if (li != null) { var dp = _shapeDp[b]; for (int j = 0; j < li.Length; j++) _shaped[li[j]] += dp[j] * w; }
            if (mi != null)
            {
                var dn = _shapeDn[b]; var dt = _shapeDt[b];
                for (int j = 0; j < mi.Length; j++) { _dn[mi[j]] += dn[j] * w; _dt[mi[j]] += dt[j] * w; }
            }
        }
        return _shapesOn;
    }

    /// <summary>Displaced positions and turned normals of the moving vertices (bind space, embedding order), with no
    /// blendshapes on and no tangents.</summary>
    public void Compute(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> offsets, ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm,
                        Span<Vector3> outPos, Span<Vector3> outNrm) =>
        Compute(moving, offsets, restPos, restNrm, default, default, outPos, outNrm, default);

    /// <summary>
    /// Displaced positions, turned normals and (0.26.3) turned tangents of the moving vertices, in bind space and the
    /// embedding's order, as the renderer must get them: it adds the blendshapes at <paramref name="weights"/> itself, then
    /// skins. <paramref name="restTan"/> and <paramref name="outTan"/> may be empty (no tangents); a tangent keeps its w.
    /// </summary>
    public void Compute(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> offsets, ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm,
                        ReadOnlySpan<Vector4> restTan, ReadOnlySpan<float> weights, Span<Vector3> outPos, Span<Vector3> outNrm, Span<Vector4> outTan)
    {
        bool shaped = SetShapes(weights);
        var basePos = shaped ? _shaped : _rest;
        Array.Copy(basePos, _cur, basePos.Length);
        for (int k = 0; k < moving.Length; k++)
        {
            outPos[k] = restPos[moving[k]] + offsets[k];
            _cur[_local[k]] = basePos[_local[k]] + offsets[k];
        }
        FaceNormals(_cur, _face);
        if (shaped) FaceNormals(_shaped, _faceShaped);
        bool tan = restTan.Length > 0 && outTan.Length >= moving.Length;
        for (int k = 0; k < moving.Length; k++)
        {
            int v = moving[k];
            var n = restNrm.Length > 0 ? restNrm[v] : _restAvg[k];
            var t = tan ? restTan[v] : default;
            outNrm[k] = n;
            if (tan) outTan[k] = t;
            if (_restAvg[k] == Vector3.Zero) continue;             // no usable own-side triangles: rest, as the renderer has it
            var r = shaped ? Average(_faceShaped, k) : _restAvg[k];
            if (r == Vector3.Zero) continue;
            var c = Average(_face, k);
            if (c == Vector3.Zero) continue;
            // the rotation taking the rest average to the current one, applied to what the renderer would show without us
            float w = 1f + Vector3.Dot(r, c);
            if (w < 1e-6f) continue;
            var q4 = Quaternion.Normalize(new Quaternion(Vector3.Cross(r, c), w));
            var dn = shaped ? _dn[k] : Vector3.Zero;
            outNrm[k] = Vector3.Transform(n + dn, q4) - dn;
            if (tan)
            {
                var dt = shaped ? _dt[k] : Vector3.Zero;
                outTan[k] = new Vector4(Vector3.Transform(new Vector3(t.X, t.Y, t.Z) + dt, q4) - dt, t.W);
            }
        }
    }

    /// <summary>
    /// 0.26.3: turns moving vertex <paramref name="k"/>'s output from the last <see cref="Compute(ReadOnlySpan{int}, ReadOnlySpan{Vector3}, ReadOnlySpan{Vector3}, ReadOnlySpan{Vector3}, ReadOnlySpan{Vector4}, ReadOnlySpan{float}, Span{Vector3}, Span{Vector3}, Span{Vector4})"/>
    /// toward a bind-space direction (the contact shading: the result's direction is the unit normal blended
    /// <paramref name="alpha"/> of the way to <paramref name="dir"/>), measured on what the renderer will show (the output
    /// plus this frame's blendshape deltas). The tangent turns with it; <paramref name="outTan"/> may be empty.
    /// </summary>
    public void TurnToward(int k, Vector3 dir, float alpha, Span<Vector3> outNrm, Span<Vector4> outTan)
    {
        var dn = _shapesOn ? _dn[k] : Vector3.Zero;
        var f = outNrm[k] + dn;
        float fl = f.Length();
        if (!(fl > 1e-12f) || !float.IsFinite(fl)) return;
        var a = f / fl;
        var m = a * (1f - alpha) + dir * alpha;
        float ml = m.Length();
        if (!(ml > 1e-12f) || !float.IsFinite(ml)) return;
        m /= ml;
        outNrm[k] = m * fl - dn;
        if (k >= outTan.Length) return;
        float w = 1f + Vector3.Dot(a, m);
        if (w < 1e-6f) return;
        var q4 = Quaternion.Normalize(new Quaternion(Vector3.Cross(a, m), w));
        var dt = _shapesOn ? _dt[k] : Vector3.Zero;
        var t = outTan[k];
        outTan[k] = new Vector4(Vector3.Transform(new Vector3(t.X, t.Y, t.Z) + dt, q4) - dt, t.W);
    }
}
