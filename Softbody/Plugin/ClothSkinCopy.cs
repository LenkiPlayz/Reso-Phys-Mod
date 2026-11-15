using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Softbody.Core.Cloth;

namespace Softbody;

/// <summary>
/// 0.30.0: an avatar cloth's copy of the mesh (see ClothInstance.Skinned.cs): made once, then written each frame and on
/// each tear. Only depends on Elements.Assets and Core, so the offline tests run it on the engine's own MeshX, skinning and
/// encoder.
/// </summary>
static class ClothSkinCopy
{
    /// <summary>
    /// The renderer's copy: the mesh with its moving vertices bound fully to the region's bone and without blendshapes,
    /// and <paramref name="spares"/> more vertices like them at the end (unused until a tear needs them).
    /// </summary>
    public static MeshX Make(MeshX original, ClothSkinRegion region, int spares)
    {
        var work = new MeshX(original);
        var moving = new List<int>(region.MovingCount);
        for (int r = 0; r < region.Count; r++) if (region.Moves[r]) moving.Add(region.Vertices[r]);
        if (moving.Count == 0) return work;
        Rebind(work, moving, region.Bone);
        if (spares > 0)
        {
            int first = work.VertexCount, src = moving[0];
            for (int k = 0; k < spares; k++)
            {
                var d = work.DuplicateVertex(src);
                if (d.Index != first + k) throw new InvalidOperationException($"a spare vertex landed at {d.Index}, expected {first + k}");
            }
            var extra = new List<int>(spares);
            for (int k = 0; k < spares; k++) extra.Add(first + k);
            Rebind(work, extra, region.Bone);
        }
        return work;
    }

    /// <summary>Binds the vertices fully to one bone and clears their blendshape deltas (every frame of every shape).</summary>
    static void Rebind(MeshX work, List<int> verts, int bone)
    {
        var bb = work.RawBoneBindings;
        foreach (int v in verts)
        {
            var b = bb[v];
            b.boneIndex0 = bone; b.weight0 = 1f;
            b.boneIndex1 = bone; b.weight1 = 0f;
            b.boneIndex2 = bone; b.weight2 = 0f;
            b.boneIndex3 = bone; b.weight3 = 0f;
            bb[v] = b;
        }
        foreach (var bs in work.BlendShapes)
        {
            for (int fi = 0; fi < bs.FrameCount; fi++)
            {
                var fr = bs[fi];
                Zero(fr.RawPositions, verts);
                if (bs.HasNormals) Zero(fr.RawNormals, verts);
                if (bs.HasTangents) Zero(fr.RawTangents, verts);
            }
        }
    }

    static void Zero(float3[] a, List<int> verts)
    {
        if (a == null) return;
        foreach (int v in verts) if ((uint)v < (uint)a.Length) a[v] = float3.Zero;
    }

    /// <summary>The mesh's triangle submeshes, in order, and the first triangle of each among all of them (as
    /// <see cref="ClothSkinRegion.MeshTriangle"/> counts them).</summary>
    public static void TriangleSubmeshes(MeshX work, out TriangleSubmesh[] subs, out int[] start)
    {
        var list = new List<TriangleSubmesh>();
        var st = new List<int>();
        int t = 0;
        foreach (var sub in work.Submeshes)
        {
            if (sub is not TriangleSubmesh ts) continue;
            list.Add(ts); st.Add(t);
            t += ts.IndicieCount / 3;
        }
        subs = list.ToArray(); start = st.ToArray();
    }

    /// <summary>A spare vertex looks like the vertex it copies: its UVs (every channel) and colour.</summary>
    public static void CopyLooks(MeshX m, int from, int to)
    {
        int n = m.VertexCount;
        if ((uint)from >= (uint)n || (uint)to >= (uint)n) return;
        for (int ch = 0; ch < m.UV_ChannelCount; ch++)
        {
            switch (m.GetUV_Dimension(ch))
            {
                case 2: { var a = m.GetRawUVs(ch); if (a != null) a[to] = a[from]; break; }
                case 3: { var a = m.GetRawUVs_3D(ch); if (a != null) a[to] = a[from]; break; }
                case 4: { var a = m.GetRawUVs_4D(ch); if (a != null) a[to] = a[from]; break; }
            }
        }
        if (m.HasColors) { var c = m.RawColors; c[to] = c[from]; }
    }

    /// <summary>
    /// The copy takes the cloth's current topology: each render vertex from <paramref name="shown"/> on that is new (a
    /// tear's copy) is a spare vertex, which takes the UVs and colour of the vertex it copies, and every region triangle gets
    /// its corners (the mesh's other triangles never change). Returns how many render vertices the copy now shows.
    /// </summary>
    public static int ApplyTopology(MeshX work, ClothSkinRegion region, ClothMesh mesh, TriangleSubmesh[] subs, int[] start, int shown)
    {
        int want = mesh.RenderVertexCount;
        var src = mesh.RenderSource;
        for (int r = Math.Max(shown, region.Count); r < want; r++)
            CopyLooks(work, region.MeshVertexOf(src[r]), region.MeshVertexOf(r));
        var rt = mesh.RenderTriangles;
        var mt = region.MeshTriangle;
        for (int k = 0; k < mt.Length && 3 * k + 2 < rt.Length; k++)
        {
            int t = mt[k];
            int s = Array.BinarySearch(start, t);
            if (s < 0) s = ~s - 1;
            if (s < 0 || s >= subs.Length) continue;
            int o = 3 * (t - start[s]);
            if (o + 2 >= subs[s].IndicieCount) continue;
            var idx = subs[s].RawIndicies;
            idx[o] = region.MeshVertexOf(rt[3 * k]);
            idx[o + 1] = region.MeshVertexOf(rt[3 * k + 1]);
            idx[o + 2] = region.MeshVertexOf(rt[3 * k + 2]);
        }
        return Math.Max(shown, want);
    }

    /// <summary>The cloth's output (per render vertex, in its bone's bind space) into the copy: the moving region vertices
    /// and the spare ones in use. The held ring keeps the renderer's own skinning.</summary>
    public static void WriteOutput(MeshX work, ClothSkinRegion region, ReadOnlySpan<Vector3> pos, ReadOnlySpan<Vector3> nrm, ReadOnlySpan<Vector4> tan, int count)
    {
        int nv = Math.Min(count, region.Count + region.SpareCount);
        int total = work.VertexCount;
        var wp = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, total));
        var wn = MemoryMarshal.Cast<float3, Vector3>(work.RawNormals.AsSpan(0, total));
        var wt = tan.Length > 0 && work.HasTangents ? MemoryMarshal.Cast<float4, Vector4>(work.RawTangents.AsSpan(0, total)) : Span<Vector4>.Empty;
        for (int r = 0; r < nv; r++)
        {
            if (!region.Written(r)) continue;
            int v = region.MeshVertexOf(r);
            if ((uint)v >= (uint)total) continue;
            wp[v] = pos[r]; wn[v] = nrm[r];
            if (wt.Length > 0 && r < tan.Length) wt[v] = tan[r];
        }
    }
}
