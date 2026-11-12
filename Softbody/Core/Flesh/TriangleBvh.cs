using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// A bounding-box tree over a mesh's triangles, in the mesh's own space. Built once per mesh (a world's MeshCollider can
/// have hundreds of thousands of triangles) and shared by every softbody near it; queried with a box to find the few
/// triangles near a body. Immutable after the build, so it is safe to query from any thread.
/// </summary>
public sealed class TriangleBvh
{
    public readonly Vector3[] A, B, C;       // triangle corners, in tree order
    public int TriangleCount => A.Length;
    public Vector3 BoundsMin => _nodes.Length > 0 ? _nodes[0].Min : Vector3.Zero;
    public Vector3 BoundsMax => _nodes.Length > 0 ? _nodes[0].Max : Vector3.Zero;
    public int NodeCount => _nodes.Length;

    struct Node { public Vector3 Min, Max; public int Left, Start, Count; } // leaf: Count > 0 (Start..Start+Count); else children Left, Left + 1
    readonly Node[] _nodes;
    const int LeafSize = 4;

    TriangleBvh(Vector3[] a, Vector3[] b, Vector3[] c, Node[] nodes) { A = a; B = b; C = c; _nodes = nodes; }

    /// <summary>Degenerate triangles (zero area, non-finite or out-of-range indices) are left out.</summary>
    public static TriangleBvh Build(ReadOnlySpan<Vector3> pos, ReadOnlySpan<int> tris)
    {
        var keep = new List<int>(tris.Length / 3);
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
            if ((uint)i0 >= (uint)pos.Length || (uint)i1 >= (uint)pos.Length || (uint)i2 >= (uint)pos.Length) continue;
            var n = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]);
            float l = n.LengthSquared();
            if (!(l > 1e-24f) || !float.IsFinite(l)) continue;
            keep.Add(t);
        }
        int nt = keep.Count;
        var order = new int[nt];
        var cen = new Vector3[nt]; var lo = new Vector3[nt]; var hi = new Vector3[nt];
        for (int k = 0; k < nt; k++)
        {
            int t = keep[k];
            var a = pos[tris[t]]; var b = pos[tris[t + 1]]; var c = pos[tris[t + 2]];
            lo[k] = Vector3.Min(a, Vector3.Min(b, c)); hi[k] = Vector3.Max(a, Vector3.Max(b, c));
            cen[k] = (a + b + c) / 3f;
            order[k] = k;
        }
        var nodes = new List<Node>(Math.Max(1, 2 * nt / LeafSize + 1));
        var keys = new float[nt];
        if (nt > 0)
        {
            nodes.Add(default);
            var stack = new Stack<(int node, int start, int count)>();
            stack.Push((0, 0, nt));
            while (stack.Count > 0)
            {
                var (ni, start, count) = stack.Pop();
                var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
                var cmin = new Vector3(float.MaxValue); var cmax = new Vector3(float.MinValue);
                for (int k = start; k < start + count; k++)
                {
                    int o = order[k];
                    bmin = Vector3.Min(bmin, lo[o]); bmax = Vector3.Max(bmax, hi[o]);
                    cmin = Vector3.Min(cmin, cen[o]); cmax = Vector3.Max(cmax, cen[o]);
                }
                var node = new Node { Min = bmin, Max = bmax };
                var ext = cmax - cmin;
                if (count <= LeafSize || !(ext.X + ext.Y + ext.Z > 0f))
                {
                    node.Start = start; node.Count = count;
                    nodes[ni] = node;
                    continue;
                }
                int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : ext.Y >= ext.Z ? 1 : 2;
                for (int k = start; k < start + count; k++) { var c = cen[order[k]]; keys[k] = axis == 0 ? c.X : axis == 1 ? c.Y : c.Z; }
                Array.Sort(keys, order, start, count);
                int half = count / 2;
                node.Left = nodes.Count;
                nodes[ni] = node;
                nodes.Add(default); nodes.Add(default);
                stack.Push((node.Left, start, half));
                stack.Push((node.Left + 1, start + half, count - half));
            }
        }
        var A = new Vector3[nt]; var B = new Vector3[nt]; var C = new Vector3[nt];
        for (int k = 0; k < nt; k++)
        {
            int t = keep[order[k]];
            A[k] = pos[tris[t]]; B[k] = pos[tris[t + 1]]; C[k] = pos[tris[t + 2]];
        }
        return new TriangleBvh(A, B, C, nodes.ToArray());
    }

    /// <summary>Adds the indices (tree order) of the triangles whose bounds overlap the box.</summary>
    public void Query(Vector3 min, Vector3 max, List<int> into)
    {
        if (_nodes.Length == 0) return;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (n.Min.X > max.X || n.Max.X < min.X || n.Min.Y > max.Y || n.Max.Y < min.Y || n.Min.Z > max.Z || n.Max.Z < min.Z) continue;
            if (n.Count > 0)
            {
                for (int k = n.Start; k < n.Start + n.Count; k++)
                {
                    var a = A[k]; var b = B[k]; var c = C[k];
                    var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
                    if (lo.X > max.X || hi.X < min.X || lo.Y > max.Y || hi.Y < min.Y || lo.Z > max.Z || hi.Z < min.Z) continue;
                    into.Add(k);
                }
                continue;
            }
            if (sp + 2 > stack.Length) { QuerySlow(n.Left, min, max, into); QuerySlow(n.Left + 1, min, max, into); continue; }
            stack[sp++] = n.Left; stack[sp++] = n.Left + 1;
        }
    }

    void QuerySlow(int ni, Vector3 min, Vector3 max, List<int> into)
    {
        ref readonly var n = ref _nodes[ni];
        if (n.Min.X > max.X || n.Max.X < min.X || n.Min.Y > max.Y || n.Max.Y < min.Y || n.Min.Z > max.Z || n.Max.Z < min.Z) return;
        if (n.Count > 0)
        {
            for (int k = n.Start; k < n.Start + n.Count; k++)
            {
                var lo = Vector3.Min(A[k], Vector3.Min(B[k], C[k])); var hi = Vector3.Max(A[k], Vector3.Max(B[k], C[k]));
                if (lo.X > max.X || hi.X < min.X || lo.Y > max.Y || hi.Y < min.Y || lo.Z > max.Z || hi.Z < min.Z) continue;
                into.Add(k);
            }
            return;
        }
        QuerySlow(n.Left, min, max, into); QuerySlow(n.Left + 1, min, max, into);
    }
}
