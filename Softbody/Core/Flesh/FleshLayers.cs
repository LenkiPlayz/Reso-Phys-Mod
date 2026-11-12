using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// 0.13.0, hue layers: one SkinnedSoftbody can run several separate flesh simulations on one mesh, each with its own
/// settings. Layer 0 is the base layer (the component's own settings, softness from its channel); every other layer owns
/// one ROYGBIV hue band (<see cref="HueBands"/>): the parts painted in that hue are its region, and their brightness is
/// how soft they are. A colour between two band centres is shared between their layers (the band weights blend), so a
/// hue gradient where two regions meet hands one over to the other smoothly instead of pinning a seam between them.
/// Grey, white and black, and hues no layer owns, stay with the base layer.
/// </summary>
public static class FleshLayers
{
    /// <summary>
    /// The softness map of each layer. <paramref name="layerBands"/>: the hue band each layer owns, -1 for the base layer
    /// (only the first base layer gets anything, and a band owned twice goes to the first layer that owns it).
    /// <paramref name="baseSoft"/> is the base channel's softness, <paramref name="bright"/> the colours' brightness (HSV
    /// value) and <paramref name="bands"/> the 7 band weights per vertex (summing to the saturation). A layer's softness is
    /// brightness x its band's weight; the base keeps its own softness times the part of the colour no layer owns.
    /// </summary>
    public static float[][] Split(ReadOnlySpan<float> baseSoft, ReadOnlySpan<float> bright, ReadOnlySpan<float> bands, ReadOnlySpan<int> layerBands)
    {
        int n = baseSoft.Length, count = layerBands.Length;
        const int NB = HueBands.Count;
        var owner = new int[NB];
        Array.Fill(owner, -1);
        int baseLayer = -1;
        for (int l = 0; l < count; l++)
        {
            int b = layerBands[l];
            if (b < 0) { if (baseLayer < 0) baseLayer = l; }
            else if (b < NB && owner[b] < 0) owner[b] = l;
        }
        var maps = new float[count][];
        for (int l = 0; l < count; l++) maps[l] = new float[n];
        bool haveBands = bands.Length >= n * NB && bright.Length >= n;
        bool anyOwned = false;
        for (int b = 0; b < NB; b++) anyOwned |= owner[b] >= 0;
        for (int v = 0; v < n; v++)
        {
            float claimed = 0f;
            if (haveBands && anyOwned)
            {
                float br = Clamp01(bright[v]);
                for (int b = 0; b < NB; b++)
                {
                    int l = owner[b];
                    if (l < 0) continue;
                    float w = bands[v * NB + b];
                    if (!(w > 0f)) continue;
                    claimed += w;
                    maps[l][v] += br * w;
                }
            }
            if (baseLayer >= 0) maps[baseLayer][v] = Clamp01(baseSoft[v]) * MathF.Max(0f, 1f - claimed);
        }
        for (int l = 0; l < count; l++)
            if (l != baseLayer)
            {
                var m = maps[l];
                for (int v = 0; v < n; v++) m[v] = Clamp01(m[v]);
            }
        return maps;
    }

    static float Clamp01(float x) => float.IsFinite(x) ? Math.Clamp(x, 0f, 1f) : 0f;

    /// <summary>Whether two softness maps are the same (a layer whose map did not change keeps its tets).</summary>
    public static bool Same(float[] a, float[] b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
}

/// <summary>
/// 0.13.0: merges what several flesh rigs show on one mesh. A vertex moved by one layer gets exactly that layer's
/// position and normal; a vertex several layers move (a hue gradient between two regions) gets the sum of their offsets
/// from rest, and the sum of their normals' changes, renormalised to the rest normal's length. 0.26.3: tangents the same
/// way as normals (their xyz renormalised to the rest tangent's, w kept).
/// </summary>
public sealed class FleshOutputMerge
{
    Vector3[] _pos = Array.Empty<Vector3>(), _nrm = Array.Empty<Vector3>();
    Vector4[] _tan = Array.Empty<Vector4>();
    int[] _mark = Array.Empty<int>(), _hits = Array.Empty<int>(), _verts = Array.Empty<int>();
    int _stamp, _count;

    /// <summary>The vertices some layer moves, in the order they were first added.</summary>
    public ReadOnlySpan<int> Vertices => _verts.AsSpan(0, _count);
    public Vector3 Position(int v) => _pos[v];
    public Vector3 Normal(int v) => _nrm[v];
    /// <summary>0.26.3: the merged tangent (only meaningful when the layers were added with tangents).</summary>
    public Vector4 Tangent(int v) => _tan[v];
    /// <summary>Vertices more than one layer moved in the last merge.</summary>
    public int Shared { get; private set; }

    public void Begin(int vertexCount)
    {
        if (_mark.Length != vertexCount)
        {
            _pos = new Vector3[vertexCount]; _nrm = new Vector3[vertexCount]; _tan = new Vector4[vertexCount];
            _mark = new int[vertexCount]; _hits = new int[vertexCount]; _verts = new int[vertexCount];
            _stamp = 0;
        }
        if (++_stamp == int.MaxValue) { Array.Clear(_mark); _stamp = 1; }
        _count = 0;
        Shared = 0;
    }

    public void Add(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> outPos, ReadOnlySpan<Vector3> outNrm, ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm) =>
        Add(moving, outPos, outNrm, default, restPos, restNrm, default);

    /// <summary>0.26.3: with tangents (<paramref name="outTan"/> and <paramref name="restTan"/> may be empty: no tangents).</summary>
    public void Add(ReadOnlySpan<int> moving, ReadOnlySpan<Vector3> outPos, ReadOnlySpan<Vector3> outNrm, ReadOnlySpan<Vector4> outTan,
                    ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm, ReadOnlySpan<Vector4> restTan)
    {
        int k = Math.Min(moving.Length, Math.Min(outPos.Length, outNrm.Length));
        bool tan = outTan.Length >= k && restTan.Length > 0;
        for (int i = 0; i < k; i++)
        {
            int v = moving[i];
            if ((uint)v >= (uint)_mark.Length) continue;
            if (_mark[v] != _stamp)
            {
                _mark[v] = _stamp; _hits[v] = 1;
                _pos[v] = outPos[i]; _nrm[v] = outNrm[i];
                _tan[v] = tan ? outTan[i] : default;
                _verts[_count++] = v;
                continue;
            }
            if (_hits[v]++ == 1) Shared++;
            _pos[v] += outPos[i] - restPos[v];
            _nrm[v] += outNrm[i] - restNrm[v];
            if (tan && v < restTan.Length) _tan[v] += outTan[i] - restTan[v];
        }
    }

    public void End(ReadOnlySpan<Vector3> restNrm) => End(restNrm, default);

    /// <summary>0.26.3: with the rest tangents (empty: no tangents).</summary>
    public void End(ReadOnlySpan<Vector3> restNrm, ReadOnlySpan<Vector4> restTan)
    {
        if (Shared == 0) return;
        for (int i = 0; i < _count; i++)
        {
            int v = _verts[i];
            if (_hits[v] < 2) continue;
            float l = _nrm[v].Length(), want = restNrm[v].Length();
            if (l > 1e-12f && float.IsFinite(l)) _nrm[v] *= (want > 1e-12f ? want : 1f) / l;
            else _nrm[v] = restNrm[v];
            if (v >= restTan.Length) continue;
            var r = restTan[v];
            var t = new Vector3(_tan[v].X, _tan[v].Y, _tan[v].Z);
            float tl = t.Length(), tw = new Vector3(r.X, r.Y, r.Z).Length();
            _tan[v] = tl > 1e-12f && float.IsFinite(tl) ? new Vector4(t * ((tw > 1e-12f ? tw : 1f) / tl), r.W) : r;
        }
    }
}
