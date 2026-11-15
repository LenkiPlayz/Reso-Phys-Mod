using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Pop;

namespace Softbody;

/// <summary>
/// 0.34.0: a Pop's meshes as they look at the shot (DESIGN-pop.md). <see cref="Capture"/> (world thread) takes what a
/// renderer shows right then: its mesh (the shared asset's data, never a softbody's or cloth's own copy), its bones' pose,
/// its blendshape weights and its materials, a material per submesh as the renderer draws them. <see cref="Cut"/> (any
/// thread) turns all of it into the meshes in the world, as the renderer would draw them (blendshapes, then skinning),
/// and cuts them into pieces.
/// </summary>
static class PopBake
{
    /// <summary>Meshes a pop takes at most.</summary>
    public const int MostRenderers = 64;
    /// <summary>Vertices a pop takes at most, from all its meshes together (a mesh that would go past it is left whole).</summary>
    public const int MostVertices = 600_000;

    /// <summary>One renderer as it was at the shot. The mesh's arrays are the shared asset's own (read only).</summary>
    internal sealed class Source
    {
        public MeshRenderer Renderer;
        public int VertexCount;
        public float3[] Positions, Normals;
        public float4[] Tangents;
        public color[] Colors;
        public ColorProfile Profile;
        /// <summary>Per UV channel: its size (2 to 4; 0: none) and its array.</summary>
        public readonly List<(int Dim, Array Data)> Uvs = new();
        /// <summary>The triangles each material draws, with the material and its property block (or null).</summary>
        public readonly List<int[]> Submeshes = new();
        public readonly List<IAssetProvider<Material>> Materials = new();
        public readonly List<IAssetProvider<MaterialPropertyBlock>> Blocks = new();
        public BoneBinding[] Bindings;
        public int BoneCount;
        /// <summary>Bind (or mesh) space to the world, a matrix per bone and the renderer's slot last.</summary>
        public Matrix4x4[] Skin;
        public readonly List<(float W, float3[] Dp, float3[] Dn, float3[] Dt)> Shapes = new();
        /// <summary>Added to every position (where the model was when it fired, against where it is now).</summary>
        public Vector3 Offset;
    }

    /// <summary>What a pop came to (off the world thread).</summary>
    internal sealed class Result
    {
        public PopPieces Pieces;
        /// <summary>0.35.0: the pieces as little cloths (null: stiff pieces).</summary>
        public PopSoftPieces Soft;
        public PopSolver Solver;
        /// <summary>Per UV channel of the pieces' mesh: its size (2 to 4).</summary>
        public int[] UvDims = Array.Empty<int>();
        public ColorProfile Profile = ColorProfile.sRGB;
        public Vector3 Middle;
        public float Height;
        public double BakeMs, CutMs, SoftMs;
        public string Failed;
    }

    /// <summary>The renderer as it is now (world thread). Null, with why, when there is nothing of it to pop.</summary>
    internal static Source Capture(MeshRenderer r, Vector3 offset, out string why)
    {
        why = null;
        var asset = PrivateMeshes.SharedOf(r.Mesh.Asset);
        if (asset == null || asset.LoadState == AssetLoadState.Failed || asset.LoadState == AssetLoadState.Unloaded || asset.Data == null)
        {
            why = "its mesh is not loaded";
            return null;
        }
        var m = asset.Data;
        int n = m.VertexCount;
        if (n <= 0 || m.RawPositions == null || m.RawPositions.Length < n) { why = "its mesh is empty"; return null; }
        var s = new Source { Renderer = r, VertexCount = n, Offset = offset, Profile = m.Profile };
        s.Positions = m.RawPositions;
        s.Normals = m.HasNormals ? m.RawNormals : null;
        s.Tangents = m.HasTangents ? m.RawTangents : null;
        s.Colors = m.HasColors ? m.RawColors : null;
        for (int c = 0; c < m.UV_ChannelCount && c < 8; c++)
        {
            int dim = m.GetUV_Dimension(c);
            Array a = dim switch { 2 => m.GetRawUVs(c), 3 => m.GetRawUVs_3D(c), 4 => m.GetRawUVs_4D(c), _ => null };
            s.Uvs.Add((a == null ? 0 : dim, a));
        }
        // a material per submesh, as the renderer draws them: a material past the last submesh draws the last submesh again
        // (an outline or overlay), and a submesh past the last material is not drawn
        int subs = m.SubmeshCount, mats = r.Materials.Count;
        for (int i = 0; i < mats && subs > 0; i++)
        {
            if (m.GetSubmesh(Math.Min(i, subs - 1)) is not TriangleSubmesh ts) continue;
            var raw = ts.RawIndicies;
            int count = Math.Min(ts.IndicieCount, raw?.Length ?? 0);
            count -= count % 3;
            var idx = new int[count];
            if (count > 0) Array.Copy(raw, idx, count);
            s.Submeshes.Add(idx);
            s.Materials.Add(r.Materials[i]);
            s.Blocks.Add(i < r.MaterialPropertyBlocks.Count ? r.MaterialPropertyBlocks[i] : null);
        }
        if (s.Submeshes.Count == 0) { why = "it draws no triangles (no material, or no triangle submesh)"; return null; }

        // the pose: a skinned mesh by its bones (a mesh without bones, or without a binding for every vertex, follows its
        // slot, as the renderer does)
        var smr = r as SkinnedMeshRenderer;
        int nb = smr != null ? m.BoneCount : 0;
        if (nb > 0)
        {
            var bind = m.RawBoneBindings;
            if (bind == null || bind.Length < n) nb = 0;
            else s.Bindings = bind;
        }
        var bones = new Slot[nb];
        var poses = new Matrix4x4[nb];
        for (int b = 0; b < nb; b++)
        {
            poses[b] = SkinMatrices.ToNumerics(m.GetBone(b).BindPose);
            var bs = b < smr.Bones.Count ? smr.Bones[b] : null;
            bones[b] = bs != null && !bs.IsRemoved ? bs : null;
        }
        s.BoneCount = nb;
        s.Skin = new Matrix4x4[nb + 1];
        if (!SoftbodyInstance.ComputeSkin(r.Slot, bones, poses, s.Skin, null)) { why = "its pose is not finite"; return null; }

        // the blendshapes it shows (the engine's rule: frame 0, weighted)
        if (smr != null && m.BlendShapeCount > 0)
        {
            var wl = smr.BlendShapeWeights;
            int count = Math.Min(wl.Count, m.BlendShapeCount);
            for (int i = 0; i < count; i++)
            {
                float w = wl[i];
                if (!float.IsFinite(w) || MathF.Abs(w) <= 1e-6f) continue;
                var bs = m.GetBlendShape(i);
                if (bs == null || bs.FrameCount == 0) continue;
                var f = bs[0];
                s.Shapes.Add((w, f.RawPositions, bs.HasNormals ? f.RawNormals : null, bs.HasTangents ? f.RawTangents : null));
            }
        }
        return s;
    }

    /// <summary>The meshes in the world as they were drawn, cut into pieces, and their flight set up (any thread); 0.35.0:
    /// with <paramref name="soft"/> each piece as a little cloth.</summary>
    internal static Result Cut(List<Source> sources, PopCutSettings settings, bool soft)
    {
        var res = new Result();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var meshes = new List<PopMesh>(sources.Count);
        int uvCount = 0;
        bool profile = false;
        foreach (var s in sources)
        {
            meshes.Add(Bake(s));
            uvCount = Math.Max(uvCount, s.Uvs.Count);
            if (!profile && s.Colors != null) { res.Profile = s.Profile; profile = true; }
        }
        res.UvDims = new int[uvCount];
        foreach (var s in sources)
            for (int c = 0; c < s.Uvs.Count; c++) res.UvDims[c] = Math.Max(res.UvDims[c], s.Uvs[c].Dim);
        for (int c = 0; c < uvCount; c++) if (res.UvDims[c] < 2) res.UvDims[c] = 2;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        res.BakeMs = (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        var pieces = PopCut.Cut(meshes, settings);
        res.CutMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (pieces.Count == 0) { res.Failed = "nothing to cut (no triangle with an area)"; return res; }
        res.Pieces = pieces;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int v = 0; v < pieces.FrontVertices; v++) { lo = Vector3.Min(lo, pieces.Positions[v]); hi = Vector3.Max(hi, pieces.Positions[v]); }
        res.Middle = 0.5f * (lo + hi);
        res.Height = hi.Y - lo.Y;
        if (soft)
        {
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            res.Soft = PopSoft.Build(pieces);
            res.SoftMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        res.Solver = new PopSolver(pieces, res.Soft);
        return res;
    }

    /// <summary>One mesh in the world as the renderer drew it: blendshapes on the mesh, then skinned (or moved with its
    /// slot), then moved by the offset.</summary>
    static PopMesh Bake(Source s)
    {
        int n = s.VertexCount;
        var pos = MemoryMarshal.Cast<float3, Vector3>(s.Positions.AsSpan(0, n)).ToArray();
        bool hasN = s.Normals != null && s.Normals.Length >= n;
        var nrm = hasN ? MemoryMarshal.Cast<float3, Vector3>(s.Normals.AsSpan(0, n)).ToArray() : new Vector3[n];
        bool hasT = s.Tangents != null && s.Tangents.Length >= n;
        var tan = hasT ? MemoryMarshal.Cast<float4, Vector4>(s.Tangents.AsSpan(0, n)).ToArray() : null;
        foreach (var (w, dp, dn, dt) in s.Shapes)
        {
            if (dp != null)
                for (int v = 0, k = Math.Min(n, dp.Length); v < k; v++) pos[v] += new Vector3(dp[v].x, dp[v].y, dp[v].z) * w;
            if (hasN && dn != null)
                for (int v = 0, k = Math.Min(n, dn.Length); v < k; v++) nrm[v] += new Vector3(dn[v].x, dn[v].y, dn[v].z) * w;
            if (hasT && dt != null)
                for (int v = 0, k = Math.Min(n, dt.Length); v < k; v++) tan[v] += new Vector4(dt[v].x * w, dt[v].y * w, dt[v].z * w, 0f);
        }
        SkinData skin;
        if (s.BoneCount > 0)
        {
            var bi = new int[n * 4]; var bw = new float[n * 4];
            for (int v = 0; v < n; v++)
            {
                var b = s.Bindings[v]; int o = v * 4;
                bi[o] = b.boneIndex0; bi[o + 1] = b.boneIndex1; bi[o + 2] = b.boneIndex2; bi[o + 3] = b.boneIndex3;
                bw[o] = b.weight0; bw[o + 1] = b.weight1; bw[o + 2] = b.weight2; bw[o + 3] = b.weight3;
            }
            skin = SkinData.FromBindings(s.BoneCount, bi, bw);
        }
        else skin = SkinData.Rigid(n);
        var outPos = new Vector3[n]; var outNrm = new Vector3[n]; var outTan = hasT ? new Vector4[n] : null;
        skin.SkinMesh(s.Skin, pos, nrm, tan, outPos, outNrm, outTan, parallel: n >= 16384, out _, out _);
        if (s.Offset != Vector3.Zero) for (int v = 0; v < n; v++) outPos[v] += s.Offset;

        var mesh = new PopMesh { Positions = outPos, Normals = hasN ? outNrm : null, Tangents = outTan };
        if (s.Colors != null && s.Colors.Length >= n) mesh.Colors = MemoryMarshal.Cast<color, Vector4>(s.Colors.AsSpan(0, n)).ToArray();
        foreach (var (dim, data) in s.Uvs) mesh.Uvs.Add(Uv(dim, data, n));
        mesh.Submeshes.AddRange(s.Submeshes);
        return mesh;
    }

    static Vector4[] Uv(int dim, Array data, int n)
    {
        if (data == null || data.Length < n) return null;
        var o = new Vector4[n];
        switch (data)
        {
            case float2[] a2: for (int v = 0; v < n; v++) o[v] = new Vector4(a2[v].x, a2[v].y, 0f, 0f); break;
            case float3[] a3: for (int v = 0; v < n; v++) o[v] = new Vector4(a3[v].x, a3[v].y, a3[v].z, 0f); break;
            case float4[] a4: for (int v = 0; v < n; v++) o[v] = new Vector4(a4[v].x, a4[v].y, a4[v].z, a4[v].w); break;
            default: return null;
        }
        return o;
    }
}
