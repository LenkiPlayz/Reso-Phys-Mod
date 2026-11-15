using System.Numerics;

namespace Softbody.Core.Cloth;

/// <summary>
/// 0.30.0: the part of a skinned mesh that is cloth (avatar cloth, DESIGN-cloth.md phase 2), and how its points follow the
/// animation.
/// <list type="bullet">
/// <item><b>Free</b> vertices are painted bright (0.5 and up), judged per point: a vertex welded to a darker one (a UV seam)
/// is held with it, as the solver holds it.</item>
/// <item><b>Region</b>: the triangles with a free corner, and every vertex at their corners' points (the free ones and the
/// held ring around them, UV-seam copies included). The cloth simulates only this part.</item>
/// <item><b>Moving</b> vertices: the region's free ones when it was built. In the renderer's own copy of the mesh each is
/// bound fully to one bone (<see cref="Bone"/>, the bone most of them hang from) without blendshapes, and the plugin writes
/// its exact place through that bone every frame. The held ring keeps its own weights and blendshapes, so it stays exactly
/// where the renderer puts the rest of the body.</item>
/// <item><b>Spare</b> vertices (after the mesh's own, on the same bone, unused at first) take the copies tears make.</item>
/// </list>
/// The animated place of a point (its target) is its first vertex with the mesh's blendshapes at the renderer's weights,
/// skinned by that vertex's own bones, as the renderer does it.
/// </summary>
public sealed class ClothSkinRegion
{
    public readonly int MeshVertexCount;
    /// <summary>The region's vertices: the mesh vertex of each (ascending).</summary>
    public readonly int[] Vertices;
    /// <summary>Per region vertex: free when built (bound to <see cref="Bone"/> in the renderer's copy).</summary>
    public readonly bool[] Moves;
    /// <summary>The region's triangles: 3 region vertices each, in the mesh's order.</summary>
    public readonly int[] Triangles;
    /// <summary>Per region triangle: its index among the mesh's triangles (all triangle submeshes joined, in order).</summary>
    public readonly int[] MeshTriangle;
    /// <summary>The bone the moving vertices hang from (0..BoneCount-1).</summary>
    public readonly int Bone;
    public readonly int MovingCount;
    /// <summary>Per mesh vertex: moving (see <see cref="Moves"/>).</summary>
    readonly bool[] _movesMesh;
    /// <summary>Per mesh vertex: its point (vertices welded by exact position), and how many points there are.</summary>
    readonly int[] _group;
    readonly int _groups;
    readonly SkinData _skin;

    public int Count => Vertices.Length;
    public bool Empty => Vertices.Length == 0 || Triangles.Length == 0;
    /// <summary>Spare vertices (set by the plugin when it makes the renderer's copy); render vertex Count + k is mesh vertex
    /// MeshVertexCount + k.</summary>
    public int SpareCount { get; set; }

    ClothSkinRegion(int n, int[] vertices, bool[] moves, int[] triangles, int[] meshTriangle, int bone, bool[] movesMesh, int[] group, int groups, SkinData skin)
    {
        MeshVertexCount = n; Vertices = vertices; Moves = moves; Triangles = triangles; MeshTriangle = meshTriangle; Bone = bone;
        _movesMesh = movesMesh; _group = group; _groups = groups; _skin = skin;
        int moving = 0;
        foreach (bool m in moves) if (m) moving++;
        MovingCount = moving;
    }

    /// <param name="positions">The mesh's vertices (bind space).</param>
    /// <param name="triangles">All its triangle submeshes joined, in order.</param>
    /// <param name="bright">The paint's brightness per vertex (1 free, under 0.5 held).</param>
    /// <param name="skin">The mesh's skin weights (BoneCount above 0).</param>
    public static ClothSkinRegion Build(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> triangles, ReadOnlySpan<float> bright, SkinData skin)
    {
        int n = positions.Length;
        if (bright.Length < n) throw new ArgumentException("one brightness per vertex");
        if (skin == null || skin.VertexCount < n) throw new ArgumentException("one skin binding per vertex");
        var group = Weld(positions, out int groups);
        var free = GroupFree(group, groups, bright);

        // the triangles with a free corner, and their corners' points
        int nt = triangles.Length / 3;
        var inRegion = new bool[groups];
        var regionTris = new List<int>();
        for (int t = 0; t < nt; t++)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) throw new ArgumentException($"triangle {t} has a vertex index out of range");
            if (!free[group[a]] && !free[group[b]] && !free[group[c]]) continue;
            regionTris.Add(t);
            inRegion[group[a]] = inRegion[group[b]] = inRegion[group[c]] = true;
        }
        var local = new int[n];
        Array.Fill(local, -1);
        var verts = new List<int>();
        for (int v = 0; v < n; v++)
            if (inRegion[group[v]]) { local[v] = verts.Count; verts.Add(v); }
        var tris = new int[regionTris.Count * 3];
        for (int k = 0; k < regionTris.Count; k++)
        {
            int t = regionTris[k];
            tris[3 * k] = local[triangles[3 * t]]; tris[3 * k + 1] = local[triangles[3 * t + 1]]; tris[3 * k + 2] = local[triangles[3 * t + 2]];
        }
        var moves = new bool[verts.Count];
        var movesMesh = new bool[n];
        for (int r = 0; r < verts.Count; r++) moves[r] = movesMesh[verts[r]] = free[group[verts[r]]];

        // the bone: the most weight over the moving vertices (else over the region, else the first)
        int nb = skin.BoneCount;
        int bone = 0;
        if (nb > 0)
        {
            var sum = new double[nb];
            bool any = false;
            for (int pass = 0; pass < 2 && !any; pass++)
            {
                Array.Clear(sum);
                for (int r = 0; r < verts.Count; r++)
                {
                    if (pass == 0 && !moves[r]) continue;
                    int o = verts[r] * 4;
                    for (int q = 0; q < 4; q++)
                    {
                        int bi = skin.VertexBones[o + q];
                        float w = skin.VertexWeights[o + q];
                        if ((uint)bi < (uint)nb && w > 0f) { sum[bi] += w; any = true; }
                    }
                }
            }
            if (any)
                for (int b = 1; b < nb; b++) if (sum[b] > sum[bone]) bone = b;
        }
        return new ClothSkinRegion(n, verts.ToArray(), moves, tris, regionTris.ToArray(), bone, movesMesh, group, groups, skin);
    }

    /// <summary>Vertices welded by exact position (-0 and +0 the same; each NaN its own), as <see cref="ClothMesh"/> does.</summary>
    static int[] Weld(ReadOnlySpan<Vector3> positions, out int groups)
    {
        int n = positions.Length;
        var group = new int[n];
        var weld = new Dictionary<(int, int, int), int>(n);
        int count = 0;
        for (int v = 0; v < n; v++)
        {
            var p = positions[v];
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z)) { group[v] = count++; continue; }
            var k = (Bits(p.X), Bits(p.Y), Bits(p.Z));
            if (!weld.TryGetValue(k, out int g)) { g = count++; weld[k] = g; }
            group[v] = g;
        }
        groups = count;
        return group;
    }

    static int Bits(float f) => BitConverter.SingleToInt32Bits(f == 0f ? 0f : f);

    /// <summary>Per point: free (its darkest vertex at 0.5 or brighter; a brightness that is not finite counts as 1).</summary>
    static bool[] GroupFree(int[] group, int groups, ReadOnlySpan<float> bright)
    {
        var min = new float[groups];
        Array.Fill(min, float.MaxValue);
        for (int v = 0; v < group.Length; v++)
        {
            float b = bright[v];
            if (!float.IsFinite(b)) b = 1f;
            if (b < min[group[v]]) min[group[v]] = b;
        }
        var free = new bool[groups];
        for (int g = 0; g < groups; g++) free[g] = min[g] >= 0.5f;
        return free;
    }

    /// <summary>True when <paramref name="bright"/> (per mesh vertex) frees a vertex that is not moving: the renderer's copy
    /// cannot show it move, so the cloth must be rebuilt.</summary>
    public bool FreesNew(ReadOnlySpan<float> bright)
    {
        if (bright.Length < MeshVertexCount) return false;
        var free = GroupFree(_group, _groups, bright);
        for (int v = 0; v < MeshVertexCount; v++)
            if (free[_group[v]] && !_movesMesh[v]) return true;
        return false;
    }

    /// <summary>The mesh vertex of a render vertex of the cloth (a region vertex, or a spare one for a tear's copy).</summary>
    public int MeshVertexOf(int renderVertex) => renderVertex < Vertices.Length ? Vertices[renderVertex] : MeshVertexCount + (renderVertex - Vertices.Length);

    /// <summary>Whether the plugin writes this render vertex every frame: a moving region vertex, or a spare one.</summary>
    public bool Written(int renderVertex) => renderVertex >= Vertices.Length || Moves[renderVertex];

    /// <summary>Per region vertex: brightness from the paint's per mesh vertex.</summary>
    public void RegionBrightness(ReadOnlySpan<float> meshBright, Span<float> dst)
    {
        for (int r = 0; r < Vertices.Length; r++)
        {
            int v = Vertices[r];
            dst[r] = v < meshBright.Length ? meshBright[v] : 1f;
        }
    }

    // ------------------------------------------------------------------ targets

    int[] _particleVertex = Array.Empty<int>();     // per original particle: its first region vertex
    Vector3[] _base = Array.Empty<Vector3>(), _work = Array.Empty<Vector3>();
    BlendshapeDeltas _blend;

    /// <summary>
    /// Ties the region to its cloth: <paramref name="mesh"/> was built from the region's vertices (in order) at
    /// <paramref name="regionBase"/> (bind space, without blendshapes), and <paramref name="blend"/> holds the blendshape
    /// deltas per region vertex (null: none).
    /// </summary>
    public void Bind(ClothMesh mesh, ReadOnlySpan<Vector3> regionBase, BlendshapeDeltas blend)
    {
        if (regionBase.Length != Vertices.Length) throw new ArgumentException("one base position per region vertex");
        int np = mesh.OriginalParticleCount;
        _particleVertex = new int[np];
        Array.Fill(_particleVertex, -1);
        var rp = mesh.RenderParticle;
        for (int r = 0; r < Vertices.Length && r < rp.Length; r++)
        {
            int p = rp[r];
            if ((uint)p < (uint)np && _particleVertex[p] < 0) _particleVertex[p] = r;
        }
        for (int p = 0; p < np; p++) if (_particleVertex[p] < 0) _particleVertex[p] = 0;
        _base = regionBase.ToArray();
        _work = new Vector3[_base.Length];
        _blend = blend;
    }

    /// <summary>Per original particle: the region vertex its target follows.</summary>
    public ReadOnlySpan<int> ParticleVertex => _particleVertex;

    /// <summary>
    /// The animated places (world) of the cloth's original particles: each one's vertex with the blendshapes at
    /// <paramref name="blendWeights"/>, skinned by its own bones with <paramref name="skin"/> (bind space to world, BoneCount
    /// + 1). Call <see cref="Bind"/> first.
    /// </summary>
    public void Targets(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<float> blendWeights, Span<Vector3> dst)
    {
        var src = (ReadOnlySpan<Vector3>)_base;
        if (_blend != null && blendWeights.Length > 0)
        {
            _base.AsSpan().CopyTo(_work);
            if (_blend.Apply(blendWeights, _work, Span<Vector3>.Empty, Span<Vector4>.Empty) > 0) src = _work;
        }
        int np = _particleVertex.Length;
        for (int p = 0; p < np && p < dst.Length; p++)
        {
            int r = _particleVertex[p];
            dst[p] = Vector3.Transform(src[r], _skin.VertexMatrix(skin, Vertices[r]));
        }
    }

    /// <summary>The region's positions (bind space) with the blendshapes at <paramref name="blendWeights"/>: the cloth's rest
    /// shape, as the avatar shows it when the cloth is built.</summary>
    public Vector3[] Shaped(ReadOnlySpan<Vector3> regionBase, BlendshapeDeltas blend, ReadOnlySpan<float> blendWeights)
    {
        var pos = regionBase.ToArray();
        if (blend != null && blendWeights.Length > 0) blend.Apply(blendWeights, pos, Span<Vector3>.Empty, Span<Vector4>.Empty);
        return pos;
    }

    /// <summary>
    /// Which way the cloth's normals face, judged in the bind pose: +1 when most points' normals (the simulated triangles'
    /// winding, from <paramref name="mesh"/>'s rest shape) point away from <paramref name="boneOrigin"/> (the bone's head,
    /// bind space, inside the body), else -1. The backstop keeps points on that side.
    /// </summary>
    public static float OutwardSign(ClothMesh mesh, Vector3 boneOrigin)
    {
        var rest = mesh.RestPositions;
        var acc = new Vector3[mesh.ParticleCount];
        var tri = mesh.Triangles;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
            var nn = Vector3.Cross(rest[b] - rest[a], rest[c] - rest[a]);
            acc[a] += nn; acc[b] += nn; acc[c] += nn;
        }
        double vote = 0;
        for (int i = 0; i < mesh.ParticleCount; i++)
        {
            var nn = acc[i];
            float l = nn.Length();
            if (!(l > 1e-20f)) continue;
            var d = rest[i] - boneOrigin;
            float dl = d.Length();
            if (!(dl > 1e-12f)) continue;
            vote += Vector3.Dot(nn / l, d / dl);
        }
        return vote < 0 ? -1f : 1f;
    }

    /// <summary>
    /// Per sphere of a body shape fitted to this same mesh: whether it was fitted against the cloth (its centre within 1.1
    /// times its radius plus <paramref name="slack"/>, bind units, of a moving vertex). Those would hold the cloth where it
    /// was modelled, so they are left out; the body's own spheres further in stay.
    /// </summary>
    public const float SphereReach = 1.1f;

    public bool[] SpheresAgainstCloth(ReadOnlySpan<Vector3> meshPositions, ReadOnlySpan<Vector3> centers, ReadOnlySpan<float> radii, float slack)
    {
        int ns = Math.Min(centers.Length, radii.Length);
        var skip = new bool[ns];
        if (MovingCount == 0 || ns == 0) return skip;
        // a grid over the moving vertices
        float maxR = 0f;
        for (int s = 0; s < ns; s++) if (float.IsFinite(radii[s])) maxR = MathF.Max(maxR, radii[s]);
        float cell = MathF.Max(1e-4f, maxR * SphereReach + slack);
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int r = 0; r < Vertices.Length; r++)
        {
            if (!Moves[r]) continue;
            var p = meshPositions[Vertices[r]];
            if (!float.IsFinite(p.X + p.Y + p.Z)) continue;
            var key = ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
            list.Add(Vertices[r]);
        }
        for (int s = 0; s < ns; s++)
        {
            var c = centers[s];
            float reach = radii[s] * SphereReach + slack;
            if (!float.IsFinite(c.X + c.Y + c.Z + reach)) continue;
            float r2 = reach * reach;
            int x0 = (int)MathF.Floor((c.X - reach) / cell), x1 = (int)MathF.Floor((c.X + reach) / cell);
            int y0 = (int)MathF.Floor((c.Y - reach) / cell), y1 = (int)MathF.Floor((c.Y + reach) / cell);
            int z0 = (int)MathF.Floor((c.Z - reach) / cell), z1 = (int)MathF.Floor((c.Z + reach) / cell);
            for (int x = x0; x <= x1 && !skip[s]; x++)
                for (int y = y0; y <= y1 && !skip[s]; y++)
                    for (int z = z0; z <= z1 && !skip[s]; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out var list)) continue;
                        foreach (int v in list)
                            if (Vector3.DistanceSquared(meshPositions[v], c) <= r2) { skip[s] = true; break; }
                    }
        }
        return skip;
    }
}
