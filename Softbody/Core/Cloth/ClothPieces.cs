namespace Softbody.Core.Cloth;

/// <summary>
/// 0.33.0: the pieces a torn cloth is in, for Cloth.DuplicateOnMend: a Mend (or turning the Cloth off) leaves the loose
/// pieces behind as a copy, while the original comes back whole. A piece is the triangles joined by shared points; it is
/// loose when none of its points is pinned or anchored (a free piece, or one a blast set free). The held pieces are the
/// original's, which is back in their place, so the copy hides them.
/// </summary>
public static class ClothPieces
{
    /// <summary>
    /// Which simulated triangles are in a loose piece (<paramref name="keepSim"/>, one per triangle), the loose points, and
    /// how many loose pieces. <paramref name="kind"/>: a point's kind as the solver has it (1 pinned and 2 anchored hold their
    /// piece). False when nothing is loose.
    /// </summary>
    public static bool FindLoose(ClothMesh mesh, Func<int, byte> kind, out bool[] keepSim, out int[] looseParticles, out int pieces)
    {
        keepSim = null; looseParticles = null; pieces = 0;
        int np = mesh.ParticleCount, nt = mesh.TriangleCount;
        var tri = mesh.Triangles;
        var root = new int[np];
        for (int i = 0; i < np; i++) root[i] = i;
        int Find(int x)
        {
            while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; }
            return x;
        }
        void Join(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) root[Math.Max(a, b)] = Math.Min(a, b);
        }
        var used = new bool[np];
        for (int t = 0; t < nt && 3 * t + 2 < tri.Length; t++)
        {
            int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
            if ((uint)a >= (uint)np || (uint)b >= (uint)np || (uint)c >= (uint)np) continue;
            used[a] = used[b] = used[c] = true;
            Join(a, b);
            Join(a, c);
        }
        var held = new bool[np];
        for (int i = 0; i < np; i++)
        {
            if (!used[i]) continue;
            byte k = kind(i);
            if (k == 1 || k == 2) held[Find(i)] = true;
        }
        var loose = new List<int>();
        var counted = new bool[np];
        for (int i = 0; i < np; i++)
        {
            if (!used[i]) continue;
            int r = Find(i);
            if (held[r]) continue;
            loose.Add(i);
            if (!counted[r]) { counted[r] = true; pieces++; }
        }
        if (loose.Count == 0) { pieces = 0; return false; }
        keepSim = new bool[nt];
        for (int t = 0; t < nt && 3 * t + 2 < tri.Length; t++)
        {
            int a = tri[3 * t];
            keepSim[t] = (uint)a < (uint)np && used[a] && !held[Find(a)];
        }
        looseParticles = loose.ToArray();
        return true;
    }

    /// <summary>
    /// Hides the render triangles that are not in a loose piece: all three corners become the first one, so the triangle is
    /// empty. <paramref name="indices"/>: one submesh's indices (3 per triangle), whose first triangle is render triangle
    /// <paramref name="firstTriangle"/>; <paramref name="renderTriangleSim"/>: each render triangle's simulated triangle
    /// (-1: its corners weld together, hidden too). Returns how many it hid.
    /// </summary>
    public static int HideHeld(ReadOnlySpan<int> renderTriangleSim, ReadOnlySpan<bool> keepSim, Span<int> indices, int firstTriangle)
    {
        int hidden = 0;
        for (int k = 0, r = firstTriangle; k + 2 < indices.Length; k += 3, r++)
        {
            int sim = (uint)r < (uint)renderTriangleSim.Length ? renderTriangleSim[r] : -1;
            if (sim >= 0 && sim < keepSim.Length && keepSim[sim]) continue;
            indices[k + 1] = indices[k];
            indices[k + 2] = indices[k];
            hidden++;
        }
        return hidden;
    }
}
