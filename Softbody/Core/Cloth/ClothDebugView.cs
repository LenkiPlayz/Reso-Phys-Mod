using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Cloth;

/// <summary>0.29.0: a copy of what a cloth's debug view draws, taken between two steps.</summary>
public sealed class ClothDebugSnapshot
{
    public Vector3[] Points = Array.Empty<Vector3>();
    public byte[] Kinds = Array.Empty<byte>();
    public int PointCount;
    public int[] EdgeA = Array.Empty<int>(), EdgeB = Array.Empty<int>();
    public float[] EdgeStrain = Array.Empty<float>();
    public int EdgeCount;
    /// <summary>The tear length (times rest), 0 when the cloth cannot tear.</summary>
    public float TearAt;
    public float Thickness;
    public bool Colliders;
    public readonly List<FleshCollider> Shapes = new();
}

/// <summary>
/// 0.29.0: the cloth's debug view (Cloth.DebugCloth). Every edge as a thin line: dark grey where both ends are pinned
/// (they follow the cloth's slot), yellow at an anchor, white where it hangs free, turning orange and then red as it
/// stretches toward its tear length (or toward 1.5 times its rest length when it cannot tear). With colliders: the shapes
/// near the cloth in magenta (players' in cyan).
/// </summary>
public static class ClothDebugView
{
    static readonly Vector4 PinnedColor = new(0.25f, 0.25f, 0.28f, 1f), AnchorColor = new(1f, 0.85f, 0.1f, 1f),
                            FreeColor = new(0.95f, 0.95f, 0.95f, 1f), Strained = new(1f, 0.55f, 0.1f, 1f), Tearing = new(1f, 0.1f, 0.1f, 1f),
                            WorldShape = new(1f, 0.2f, 0.9f, 0.8f), UserShape = new(0.2f, 0.9f, 1f, 0.8f);

    /// <summary>Most edges drawn (a line is 6 vertices; the debug mesh stops at 150,000).</summary>
    public const int MaxEdges = 20000;

    public static DebugCounts Build(ClothDebugSnapshot s, DebugGeometry g)
    {
        g.Clear();
        var c = new DebugCounts();
        if (s == null || s.PointCount == 0) return c;
        // line thickness from the mesh's own edge length
        float sum = 0f; int cnt = 0;
        for (int k = 0; k < s.EdgeCount && k < 256; k++) { sum += Vector3.Distance(s.Points[s.EdgeA[k]], s.Points[s.EdgeB[k]]); cnt++; }
        float lr = MathF.Max(2e-4f, cnt > 0 ? 0.04f * sum / cnt : 0.002f);
        lr = MathF.Min(lr, 0.01f);
        float tearAt = s.TearAt > 1f ? s.TearAt : 1.5f;
        int edges = Math.Min(s.EdgeCount, MaxEdges);
        for (int k = 0; k < edges; k++)
        {
            int a = s.EdgeA[k], b = s.EdgeB[k];
            byte ka = s.Kinds[a], kb = s.Kinds[b];
            Vector4 color;
            if (ka == 2 || kb == 2) color = AnchorColor;
            else if (ka != 0 && kb != 0) color = PinnedColor;
            else
            {
                float t = Math.Clamp((s.EdgeStrain[k] - 1f) / (tearAt - 1f), 0f, 1f);
                color = t < 0.5f ? Vector4.Lerp(FreeColor, Strained, t * 2f) : Vector4.Lerp(Strained, Tearing, t * 2f - 1f);
            }
            if (g.Line(s.Points[a], s.Points[b], lr, color)) c.Lines++;
        }
        if (s.Colliders)
            foreach (var col in s.Shapes)
            {
                var color = col.Source == 0 ? WorldShape : UserShape;
                switch (col.Kind)
                {
                    case FleshColliderKind.Sphere: g.WireSphere(col.A, col.R, lr, color); break;
                    case FleshColliderKind.Box: g.WireBox(col.A, col.Q, col.B, lr, color); break;
                    case FleshColliderKind.Capsule:
                        g.WireSphere(col.A, col.R, lr, color); g.WireSphere(col.B, col.R, lr, color);
                        g.Line(col.A, col.B, lr, color);
                        break;
                    case FleshColliderKind.Cylinder:
                    case FleshColliderKind.Cone:
                        g.Line(col.A, col.B, lr, color);
                        g.WireSphere(col.A, col.R, lr, color);
                        break;
                    case FleshColliderKind.Triangle:
                        g.Line(col.A, col.B, lr * 0.7f, color); g.Line(col.B, col.C, lr * 0.7f, color); g.Line(col.C, col.A, lr * 0.7f, color);
                        break;
                }
                c.Proxies++;
            }
        c.Vertices = g.VertexCount;
        c.Triangles = g.IndexCount / 3;
        c.Truncated = g.Truncated || s.EdgeCount > edges;
        return c;
    }
}
