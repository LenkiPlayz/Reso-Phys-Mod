using System.Diagnostics;
using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// How each soft render vertex follows the shell. A render vertex keeps its own exact skinned and blendshaped position
/// (the renderer does that) and only adds an offset, blended from nearby sim surface points with a smooth (Wendland C2)
/// kernel of about 1.75 local edge lengths, normalised. Points facing the other way (the other side of a gap, a leg
/// against the tail) are left out. Offsets are low-frequency, so this is smooth where piecewise-linear tet
/// barycentrics would crease (the 0.2.1 lesson). The softness gain is blended the same way from the points' softness.
/// </summary>
public sealed class FleshEmbedding
{
    /// <summary>Render vertices that move, their softness gain, and CSR weights over sim surface points.</summary>
    public int[] Vertices { get; private set; }
    public float[] Gain { get; private set; }
    public int[] Start { get; private set; }
    public int[] Index { get; private set; }
    public float[] Weight { get; private set; }
    /// <summary>
    /// 0.12.1, per embedded vertex, bind space: where the vertex sits relative to the blend of its sim points' rest
    /// positions (the same weights). Every offset is a smooth blend of the points', so this is the part of the rest shape
    /// between the points that the offsets never change, the surface's own roundness: on a round belly about 4 mm outward
    /// of a flat patch of points. <see cref="FleshRig"/> takes it out where the skin is pressed flat.
    /// </summary>
    public Vector3[] Detail { get; private set; }
    /// <summary>0.12.1, per embedded vertex: its unit rest normal in bind space (zero if the mesh has none).</summary>
    public Vector3[] RestNormal { get; private set; }
    public int Count => Vertices.Length;
    public int Orphans { get; private set; }
    public double BuildMs { get; private set; }

    const int MaxWeights = 16;
    const float Support = 1.75f;

    public static FleshEmbedding Build(Vector3[] pos, Vector3[] nrm, float[] soft, float threshold, SimSurface s)
    {
        var sw = Stopwatch.StartNew();
        var e = new FleshEmbedding();
        // grid of the sim points
        float maxH = 0;
        for (int j = 0; j < s.Count; j++) maxH = MathF.Max(maxH, Support * s.VertexEdge[j]);
        float cell = MathF.Max(maxH, 1e-6f);
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int j = 0; j < s.Count; j++)
        {
            var c = Cell(s.Pos[j], cell);
            if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
            l.Add(j);
        }

        var verts = new List<int>(); var gain = new List<float>(); var start = new List<int> { 0 }; var detail = new List<Vector3>(); var restN = new List<Vector3>();
        var idx = new List<int>(); var wts = new List<float>();
        Span<int> ci = stackalloc int[MaxWeights]; Span<float> cw = stackalloc float[MaxWeights];
        int orphans = 0;
        for (int v = 0; v < pos.Length; v++)
        {
            float g = soft != null && v < soft.Length ? soft[v] : 0f;
            if (!(g > threshold)) continue;
            var p = pos[v];
            var n = nrm != null && v < nrm.Length ? nrm[v] : Vector3.Zero;
            var c0 = Cell(p, cell);
            int count = 0; float sum = 0;
            int nearest = -1; float nearestD = float.MaxValue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((c0.Item1 + dx, c0.Item2 + dy, c0.Item3 + dz), out var l)) continue;
                        foreach (int j in l)
                        {
                            float d = Vector3.Distance(p, s.Pos[j]);
                            // only points facing clearly away (the other side of a gap) are left out: a detail whose normals turn sharply must
                            // still pick the same points as its neighbours, or the offsets jump between them and fold small triangles
                            float facing = n == Vector3.Zero ? 1f : Smooth(-0.6f, -0.1f, Vector3.Dot(Vector3.Normalize(n), s.Nrm[j]));
                            if (facing > 0f && d < nearestD) { nearestD = d; nearest = j; }
                            float hj = Support * s.VertexEdge[j];
                            if (!(d < hj) || !(facing > 0f)) continue;
                            float r = d / hj;
                            float w = (1 - r) * (1 - r) * (1 - r) * (1 - r) * (4 * r + 1) * facing;
                            if (!(w > 1e-6f)) continue;
                            if (count < MaxWeights) { ci[count] = j; cw[count] = w; count++; }
                            else
                            {
                                int m = 0; for (int k = 1; k < MaxWeights; k++) if (cw[k] < cw[m]) m = k;
                                if (w > cw[m]) { ci[m] = j; cw[m] = w; }
                            }
                        }
                    }
            for (int k = 0; k < count; k++) sum += cw[k];
            if (!(sum > 0f))
            {
                // nothing within reach: the nearest point facing the same way, if it is close
                if (nearest < 0 || nearestD > 3f * Support * s.VertexEdge[nearest]) { orphans++; continue; }
                ci[0] = nearest; cw[0] = 1f; count = 1; sum = 1f;
            }
            // the gain is the sim points' softness through the same kernel: a smooth field, so a sharp paint edge does not
            // multiply the smooth offsets by a jumpy factor (that folds small triangles along the edge)
            float sg = 0f; var blend = Vector3.Zero;
            for (int k = 0; k < count; k++) { idx.Add(ci[k]); wts.Add(cw[k] / sum); sg += s.Soft[ci[k]] * (cw[k] / sum); blend += s.Pos[ci[k]] * (cw[k] / sum); }
            verts.Add(v); gain.Add(Math.Clamp(sg, 0f, 1f));
            float nl = n.Length();
            detail.Add(p - blend); restN.Add(nl > 1e-12f && float.IsFinite(nl) ? n / nl : Vector3.Zero);
            start.Add(idx.Count);
        }
        e.Vertices = verts.ToArray(); e.Gain = gain.ToArray(); e.Start = start.ToArray(); e.Index = idx.ToArray(); e.Weight = wts.ToArray();
        e.Detail = detail.ToArray(); e.RestNormal = restN.ToArray();
        e.Orphans = orphans;
        e.BuildMs = sw.Elapsed.TotalMilliseconds;
        return e;
    }

    static (int, int, int) Cell(Vector3 p, float cell) =>
        ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0f, 1f); return t * t * (3 - 2 * t); }

    /// <summary>offset[k] = gain x the blend of the points' offsets, for the k-th embedded vertex.</summary>
    public void Interpolate(ReadOnlySpan<Vector3> pointOffsets, Span<Vector3> vertexOffsets)
    {
        for (int k = 0; k < Vertices.Length; k++)
        {
            Vector3 acc = Vector3.Zero;
            for (int q = Start[k]; q < Start[k + 1]; q++) acc += pointOffsets[Index[q]] * Weight[q];
            vertexOffsets[k] = acc * Gain[k];
        }
    }
}
