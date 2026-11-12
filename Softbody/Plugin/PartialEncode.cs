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

    /// <summary>The streams a GPU-path upload may carry. Anything else (layout, geometry, skin data) is a full upload.</summary>
    public const MeshUploadHint.Flag Streams = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals |
                                               MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.Colors;

    /// <summary>
    /// Null when <paramref name="buffer"/> can take <paramref name="mesh"/>'s flagged streams in place: the buffer holds data,
    /// has the mesh's vertex count, an attribute for every stream the mesh has (so nothing would be missing from the layout),
    /// and the hint only flags vertex streams the mesh has. Otherwise why not (then a full upload is needed).
    /// </summary>
    public static string CheckLayout(MeshX mesh, MeshBuffer buffer, MeshUploadHint hint)
    {
        if (mesh == null) return "no mesh data";
        if (buffer == null) return "the asset has no mesh buffer yet";
        var data = buffer.Data;
        if (data == null || data.IsDisposed) return "the asset's mesh buffer holds no data";
        if (buffer.VertexCount != mesh.VertexCount) return $"the buffer has {buffer.VertexCount} vertices, the mesh {mesh.VertexCount}";
        if ((hint.Flags & ~(Streams | MeshUploadHint.Flag.Dynamic | MeshUploadHint.Flag.Readable)) != 0 || (hint.Flags & Streams) == 0)
            return "not a vertex-streams-only hint: " + hint;
        if (buffer.TotalBufferLength > data.SizeBytes) return "the buffer is smaller than its layout";
        bool pos = false, nrm = false, tan = false, col = false;
        foreach (var a in buffer.VertexAttributes)
        {
            switch (a.attribute)
            {
                case VertexAttributeType.Position: pos = a.dimensions == 3; break;
                case VertexAttributeType.Normal: nrm = a.dimensions == 3; break;
                case VertexAttributeType.Tangent: tan = a.dimensions == 4; break;
                case VertexAttributeType.Color: col = a.dimensions == 4; break;
            }
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

    /// <summary>Encodes the flagged streams of <paramref name="mesh"/> into the buffer, in the buffer's own formats
    /// (FrooxEngine.Mesh.UpdateMeshData passes target colour profile 0, Linear).</summary>
    public static void Encode(MeshX mesh, MeshBuffer buffer, MeshUploadHint hint) =>
        MeshBufferGenerator.FillVertexData(mesh, buffer, ColorProfile.Linear, hint);

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
