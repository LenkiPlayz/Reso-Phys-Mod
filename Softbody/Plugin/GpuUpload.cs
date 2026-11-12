using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;

namespace Softbody;

/// <summary>
/// Sends a true partial upload (vertex streams only) for a FrooxEngine mesh asset, without the engine's widening (see
/// <see cref="PartialEncode"/>). It is FrooxEngine.Mesh.UpdateMeshData minus three steps that do not apply to a vertex-only
/// update of data the asset already shows: <c>EnsureValidVertexAttributes</c> (the widening), the <c>Data</c>/<c>Version</c>
/// bump (the asset's MeshX stays what it was, so SkinnedMeshRenderer does not redo its bone and blendshape setup each
/// frame) and the bone metadata refresh. Three private members are used, by reflection: <c>Mesh.meshBuffer</c>,
/// <c>Mesh.onAssetUpdated</c> (the single pending callback, invoked by <c>Mesh.HandleResult</c>) and the internal
/// <c>RenderSystem.SendAssetUpdate</c>. If any is missing (an engine update), <see cref="Unavailable"/> says so and the
/// plugin uses its CPU path.
/// </summary>
static class GpuUpload
{
    static readonly FieldInfo _meshBuffer = typeof(FrooxEngine.Mesh).GetField("meshBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo _onAssetUpdated = typeof(FrooxEngine.Mesh).GetField("onAssetUpdated", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly MethodInfo _send = typeof(RenderSystem).GetMethod("SendAssetUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                                                                      null, new[] { typeof(AssetCommand) }, null);

    /// <summary>Null when every engine member this needs exists with the expected type; otherwise which one is missing.</summary>
    public static readonly string Unavailable =
        _meshBuffer == null || _meshBuffer.FieldType != typeof(MeshBuffer) ? "FrooxEngine.Mesh.meshBuffer not found" :
        _onAssetUpdated == null || _onAssetUpdated.FieldType != typeof(AssetIntegrated) ? "FrooxEngine.Mesh.onAssetUpdated not found" :
        _send == null ? "FrooxEngine.RenderSystem.SendAssetUpdate(AssetCommand) not found" : null;

    /// <summary>Why a vertex-only upload of <paramref name="data"/> is not possible on <paramref name="asset"/> right now (null: it is).</summary>
    public static string Check(FrooxEngine.Mesh asset, MeshX data, MeshUploadHint hint)
    {
        if (Unavailable != null) return Unavailable;
        if (asset.RenderSystem == null) return "no render system";
        var buffer = (MeshBuffer)_meshBuffer.GetValue(asset);
        if (buffer == null || buffer.Data == null || buffer.Data.IsDisposed)
        {
            // 0.9.3: a mesh loaded from its file has no buffer any more (Mesh.LoadTargetVariant frees it once the renderer has
            // the mesh). Make one for the vertex streams only.
            string made = EnsureBuffer(asset, data);
            if (made != null) return made;
            buffer = (MeshBuffer)_meshBuffer.GetValue(asset);
        }
        string why = PartialEncode.CheckLayout(data, buffer, hint);
        if (why != null) return why;
        if (buffer.Data is not SharedMemoryBlockLease<byte>) return "the mesh buffer is not in shared memory";
        return null;
    }

    /// <summary>
    /// Gives the asset a mesh buffer holding <paramref name="data"/>'s vertex streams and indices, encoded by the engine's own
    /// MeshBufferGenerator (as Mesh.UpdateMeshData does), but from a copy without blendshapes and bones: the GPU path only
    /// ever sends vertex streams from it, and a blendshape section would cost hundreds of MB for an avatar. Nothing is sent
    /// to the renderer here, and the asset's MeshX, metadata and version are not touched. Null on success, else why not.
    /// </summary>
    static string EnsureBuffer(FrooxEngine.Mesh asset, MeshX data)
    {
        try
        {
            var lite = new MeshX(data);
            if (lite.BlendShapeCount > 0) lite.ClearBlendShapes();
            if (lite.BoneCount > 0) lite.ClearBones(true);
            lite.HasBoneBindings = false;
            var buffer = new MeshBuffer();
            var hint = default(MeshUploadHint);
            MeshUploadHintHelper.SetFromMesh(ref hint, lite);
            MeshBufferGenerator.UpdateMeshBufferData(lite, buffer, asset.RenderSystem, (ColorProfile)0, ref hint, MeshMetadata.GenerateMetadata(lite), asset.Bounds, asset);
            if (buffer.Data == null) return "the engine did not allocate a mesh buffer";
            if (buffer.VertexCount != data.VertexCount) return $"the new mesh buffer has {buffer.VertexCount} vertices, the mesh {data.VertexCount}";
            _meshBuffer.SetValue(asset, buffer);
            return null;
        }
        catch (Exception ex) { return "making a mesh buffer failed: " + ex.GetType().Name + ": " + ex.Message; }
    }

    /// <summary>
    /// Encodes the flagged streams of <paramref name="data"/> into the asset's mesh buffer and sends the upload. Returns null
    /// when it was sent (<paramref name="onDone"/> then runs once on the renderer's result), else why not (nothing was sent,
    /// nothing was written). Never more than one upload in flight per asset: callers go through <see cref="AssetUploads"/>.
    /// </summary>
    public static string TrySend(FrooxEngine.Mesh asset, MeshX data, MeshUploadHint hint, BoundingBox bounds, AssetIntegrated onDone)
    {
        string why = Check(asset, data, hint);
        if (why != null) return why;
        var buffer = (MeshBuffer)_meshBuffer.GetValue(asset);
        var lease = (SharedMemoryBlockLease<byte>)buffer.Data;
        PartialEncode.Encode(data, buffer, hint);
        var upload = PartialEncode.Describe(buffer, asset.AssetId, asset.HighPriorityIntegration, hint, bounds);
        upload.buffer = lease.Descriptor;
        _onAssetUpdated.SetValue(asset, onDone);
        _send.Invoke(asset.RenderSystem, new object[] { upload });
        return null;
    }
}
