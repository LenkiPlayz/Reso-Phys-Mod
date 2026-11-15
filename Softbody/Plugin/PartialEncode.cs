using Elements.Assets;
using Elements.Core;
using Renderite.Shared;

namespace Softbody;

/// <summary>
/// The engine half of a true partial mesh upload (0.9.0 GPU path for skinned meshes). Only depends on Elements.Assets and
/// Renderite.Shared, so the offline tests compile this file and run it on the engine's own encoder.
///
/// Why not <c>Mesh.SetFromMeshX</c>: <c>MeshBufferGenerator.UpdateMeshBufferData</c> starts with
/// <c>EnsureValidVertexAttributes</c>, which reports a layout change for every mesh with bones
/// (<c>data.BoneCount &gt; 0 || mesh.BoneCount &gt; 0</c>), and the caller then sets VertexLayout and every vertex stream on
/// the hint. Here the same pieces are used without that step: the asset's existing <c>MeshBuffer</c> (the shared-memory
/// block the renderer reads) keeps its layout, offsets, index, bone and blendshape regions, <c>FillVertexData</c> rewrites
/// only the flagged streams, and <c>FillMeshBufferDefinition</c> describes the same buffer to the renderer.
/// </summary>
static class PartialEncode
{
    /// <summary>Same as <c>SoftbodyRenderer.UploadPatchSpec.HandshakeName</c> (the tests check they are equal).</summary>
    public const string HandshakeName = @"Local\Softbody.RendererPatch.SkinnedInPlace.v1";

    /// <summary>
    /// 0.30.0: the renderer also takes, in place, a skinned mesh's triangles (the same submeshes with the same index
    /// counts and format: only which vertices they use changes), UVs and colours, along with its vertex streams (an avatar
    /// cloth's tears). Same as the fork's <c>MeshAsset.SoftbodyHandshakeNameV2</c>; the SoftbodyRenderer patch has only v1.
    /// </summary>
    public const string HandshakeNameV2 = @"Local\Softbody.RendererPatch.SkinnedInPlace.v2";

    /// <summary>The streams a GPU-path upload may carry. Anything else (layout, geometry, skin data) is a full upload.</summary>
    public const MeshUploadHint.Flag Streams = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals |
                                               MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.Colors;

    /// <summary>0.30.0: what a v2 renderer takes in place on top of <see cref="Streams"/>, with at least one of positions,
    /// normals or tangents.</summary>
    public const MeshUploadHint.Flag Topology = MeshUploadHint.Flag.Geometry | MeshUploadHint.Flag.Colors |
                                                MeshUploadHint.Flag.UV0s | MeshUploadHint.Flag.UV1s | MeshUploadHint.Flag.UV2s | MeshUploadHint.Flag.UV3s |
                                                MeshUploadHint.Flag.UV4s | MeshUploadHint.Flag.UV5s | MeshUploadHint.Flag.UV6s | MeshUploadHint.Flag.UV7s;

    const MeshUploadHint.Flag Shape = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Tangents;

    static readonly MeshUploadHint.Flag[] UvFlags =
    {
        MeshUploadHint.Flag.UV0s, MeshUploadHint.Flag.UV1s, MeshUploadHint.Flag.UV2s, MeshUploadHint.Flag.UV3s,
        MeshUploadHint.Flag.UV4s, MeshUploadHint.Flag.UV5s, MeshUploadHint.Flag.UV6s, MeshUploadHint.Flag.UV7s,
    };

    /// <summary>0.30.0: flags a topology change on top of the streams: the triangles, and the UVs and colours the mesh has.</summary>
    public static void AddTopology(ref MeshUploadHint hint, MeshX mesh)
    {
        hint[MeshUploadHint.Flag.Geometry] = true;
        if (mesh.HasColors) hint[MeshUploadHint.Flag.Colors] = true;
        for (int ch = 0; ch < Math.Min(mesh.UV_ChannelCount, UvFlags.Length); ch++)
            if (mesh.GetUV_Dimension(ch) > 0) hint[UvFlags[ch]] = true;
    }

    /// <summary>
    /// Null when <paramref name="buffer"/> can take <paramref name="mesh"/>'s flagged streams in place: the buffer holds data,
    /// has the mesh's vertex count, an attribute for every stream the mesh has (so nothing would be missing from the layout),
    /// and the hint only flags vertex streams the mesh has, held in formats the renderer reads in place (0.30.0: Float32,
    /// and colours also as halves or bytes). Otherwise why not (then a full upload is needed).
    /// </summary>
    public static string CheckLayout(MeshX mesh, MeshBuffer buffer, MeshUploadHint hint)
    {
        if (mesh == null) return "no mesh data";
        if (buffer == null) return "the asset has no mesh buffer yet";
        var data = buffer.Data;
        if (data == null || data.IsDisposed) return "the asset's mesh buffer holds no data";
        if (buffer.VertexCount != mesh.VertexCount) return $"the buffer has {buffer.VertexCount} vertices, the mesh {mesh.VertexCount}";
        bool topology = (hint.Flags & (Topology & ~Streams)) != 0;
        if ((hint.Flags & ~(Streams | Topology | MeshUploadHint.Flag.Dynamic | MeshUploadHint.Flag.Readable)) != 0 || (hint.Flags & Streams) == 0 ||
            (topology && (hint.Flags & Shape) == 0))
            return "not a vertex-streams-only hint: " + hint;
        if (buffer.TotalBufferLength > data.SizeBytes) return "the buffer is smaller than its layout";
        bool pos = false, nrm = false, tan = false, col = false;
        // 0.30.0: the formats the renderer's in-place path reads (Float32, and for colours also halves or bytes). With any
        // other it would fall back to a full upload, which a streams-only hint cannot make.
        bool posF = false, nrmF = false, tanF = false, colF = false;
        Span<int> uvDims = stackalloc int[UvFlags.Length];
        Span<bool> uvF = stackalloc bool[UvFlags.Length];
        foreach (var a in buffer.VertexAttributes)
        {
            bool f32 = a.format == VertexAttributeFormat.Float32;
            switch (a.attribute)
            {
                case VertexAttributeType.Position: pos = a.dimensions == 3; posF = f32; break;
                case VertexAttributeType.Normal: nrm = a.dimensions == 3; nrmF = f32; break;
                case VertexAttributeType.Tangent: tan = a.dimensions == 4; tanF = f32; break;
                case VertexAttributeType.Color:
                    col = a.dimensions == 4;
                    colF = f32 || a.format == VertexAttributeFormat.Half16 || a.format == VertexAttributeFormat.UNorm8;
                    break;
                default:
                    int ch = a.attribute - VertexAttributeType.UV0;
                    if ((uint)ch < (uint)uvDims.Length) { uvDims[ch] = a.dimensions; uvF[ch] = f32; }
                    break;
            }
        }
        // 0.30.0: UVs and triangles (a v2 renderer's in-place topology)
        for (int ch = 0; ch < UvFlags.Length; ch++)
        {
            if (!hint[UvFlags[ch]]) continue;
            int dim = ch < mesh.UV_ChannelCount ? mesh.GetUV_Dimension(ch) : 0;
            if (dim == 0) return $"UV{ch} flagged, but the mesh has none";
            if (uvDims[ch] != dim) return $"the mesh's UV{ch} ({dim}D) is not in the buffer's layout";
            if (!uvF[ch]) return $"the buffer's UV{ch} is not Float32 (the renderer takes Float32 UVs in place)";
        }
        if (hint[MeshUploadHint.Flag.Positions] && pos && !posF) return "the buffer's positions are not Float32";
        if (hint[MeshUploadHint.Flag.Normals] && nrm && !nrmF) return "the buffer's normals are not Float32";
        if (hint[MeshUploadHint.Flag.Tangents] && tan && !tanF) return "the buffer's tangents are not Float32";
        if (hint[MeshUploadHint.Flag.Colors] && col && !colF) return "the buffer's colours are not Float32, halves or bytes";
        if (hint[MeshUploadHint.Flag.Geometry])
        {
            string why = CheckIndices(mesh, buffer);
            if (why != null) return why;
        }
        if (!pos) return "the buffer has no 3D positions";
        if (mesh.HasNormals && !nrm) return "the mesh has normals the buffer's layout lacks";
        if (mesh.HasTangents && !tan) return "the mesh has tangents the buffer's layout lacks";
        if (mesh.HasColors && !col) return "the mesh has vertex colours the buffer's layout lacks";
        if (hint[MeshUploadHint.Flag.Normals] && !mesh.HasNormals) return "normals flagged, but the mesh has none";
        if (hint[MeshUploadHint.Flag.Tangents] && !mesh.HasTangents) return "tangents flagged, but the mesh has none";
        if (hint[MeshUploadHint.Flag.Colors] && !mesh.HasColors) return "colours flagged, but the mesh has none";
        return null;
    }

    /// <summary>
    /// 0.30.0: null when <paramref name="buffer"/>'s index layout holds <paramref name="mesh"/>'s triangles as they are
    /// now: the same submeshes with the same index counts, and an index format wide enough for every vertex. Then only
    /// which vertices the triangles use changes, which a v2 renderer takes in place.
    /// </summary>
    public static string CheckIndices(MeshX mesh, MeshBuffer buffer)
    {
        var subs = buffer.Submeshes;
        if (subs == null || buffer.SubmeshCount != mesh.SubmeshCount) return $"the buffer has {buffer.SubmeshCount} submeshes, the mesh {mesh.SubmeshCount}";
        int i = 0, total = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (i >= subs.Count) return "the buffer has fewer submesh descriptors than it says";
            if (subs[i].indexCount != sub.IndicieCount) return $"submesh {i} has {sub.IndicieCount} indices, the buffer {subs[i].indexCount}";
            total += sub.IndicieCount;
            i++;
        }
        if (buffer.IndexCount != total) return $"the buffer has {buffer.IndexCount} indices, the mesh {total}";
        if (buffer.IndexBufferFormat == IndexBufferFormat.UInt16 && mesh.VertexCount > 65536) return "16-bit indices for more than 65536 vertices";
        return null;
    }

    /// <summary>Encodes the flagged streams of <paramref name="mesh"/> into the buffer, in the buffer's own formats
    /// (FrooxEngine.Mesh.UpdateMeshData passes target colour profile 0, Linear), and 0.30.0 its triangles when Geometry is
    /// flagged (the engine's own index encoder, into the buffer's existing index layout).</summary>
    public static void Encode(MeshX mesh, MeshBuffer buffer, MeshUploadHint hint)
    {
        MeshBufferGenerator.FillVertexData(mesh, buffer, ColorProfile.Linear, hint);
        if (hint[MeshUploadHint.Flag.Geometry]) MeshBufferGenerator.FillIndexData(mesh, buffer);
    }

    /// <summary>The upload message for the buffer, as FrooxEngine.Mesh.UpdateMeshData builds it (minus the shared-memory
    /// descriptor, which only exists in the engine).</summary>
    public static MeshUploadData Describe(MeshBuffer buffer, int assetId, bool highPriority, MeshUploadHint hint, BoundingBox bounds)
    {
        var u = new MeshUploadData
        {
            assetId = assetId,
            highPriority = highPriority,
            uploadHint = hint,
            bounds = bounds,
        };
        buffer.FillMeshBufferDefinition(u);
        return u;
    }
}
