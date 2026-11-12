using Renderite.Shared;
using SharpDX.Direct3D11;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Renderite.Unity
{
    public class MeshAsset : Asset
    {
        static List<string> blendshapeNames = new List<string>();

        // Softbody fork: the Softbody plugin (engine side) turns on its GPU path for skinned meshes only when this renderer can
        // update them in place. It checks for this named event; the fork holds it open for the renderer's lifetime.
        public const string SoftbodyHandshakeName = @"Local\Softbody.RendererPatch.SkinnedInPlace.v1";
        static readonly System.Threading.EventWaitHandle softbodyHandshake;

        static MeshAsset()
        {
            try
            {
                softbodyHandshake = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, SoftbodyHandshakeName);
                Debug.Log("[Softbody fork] in-place skinned vertex updates enabled; handshake " + SoftbodyHandshakeName + " open");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Softbody fork] could not open the Softbody handshake event: " + ex.Message);
            }
        }

        public static string GetBlendshapeName(int index)
        {
            while (blendshapeNames.Count <= index)
                blendshapeNames.Add(blendshapeNames.Count.ToString());

            return blendshapeNames[index];
        }

        // Essentially don't let Unity do anything
        public const UnityEngine.Rendering.MeshUpdateFlags UPDATE_FLAGS =
            UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds
            | UnityEngine.Rendering.MeshUpdateFlags.DontResetBoneBounds
            | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices
            | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers
            ;

        public UnityEngine.Mesh Mesh => mesh;

        public void Handle(MeshUploadData uploadData)
        {
            if (this.uploadData != null)
                throw new InvalidOperationException("Cannot handle upload data, because previous upload was not processed yet!");

            this.meshBuffer = ExtractMeshBuffer(uploadData);
            this.uploadData = uploadData;

            AssetIntegrator.EnqueueProcessing(Upload(), uploadData.highPriority);
        }

        public void Handle(MeshUnload unload)
        {
            Unload();

            // Ensure it's removed from the manager
            RenderingManager.Instance.Meshes.RemoveAsset(this);

            PackerMemoryPool.Instance.Return(unload);
        }

        MeshBuffer ExtractMeshBuffer(MeshUploadData uploadData)
        {
            var buffer = new MeshBuffer(uploadData);

            if(!uploadData.buffer.IsEmpty)
                buffer.Data = RenderingManager.Instance.SharedMemory.AccessSlice(uploadData.buffer);

            return buffer;
        }

        UnityEngine.Mesh mesh;
        MeshBuffer meshBuffer;

        MeshUploadData uploadData;

        bool firstUploadCompleted;
        IndexBufferFormat? lastIndexBufferFormat;
        bool lastIsSkinned;

        void UpdateVertexLayout(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            var layout = new UnityEngine.Rendering.VertexAttributeDescriptor[buffer.VertexAttributeCount];

            for (int i = 0; i < buffer.VertexAttributeCount; i++)
            {
                var attribute = buffer.VertexAttributes[i];

                layout[i] = new UnityEngine.Rendering.VertexAttributeDescriptor(
                    attribute.attribute.ToUnity(),
                    attribute.format.ToUnity(),
                    attribute.dimensions, 0);
            }

            mesh.SetVertexBufferParams(buffer.VertexCount, layout);
        }

        void UpdateIndexLayout(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            mesh.SetIndexBufferParams(buffer.IndexCount, buffer.IndexBufferFormat.ToUnity());
        }

        void UpdateSubmeshLayout(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            if (mesh.subMeshCount != buffer.SubmeshCount)
                SanitizeSubmeshes();

            mesh.subMeshCount = buffer.SubmeshCount;

            for (int i = 0; i < buffer.SubmeshCount; i++)
            {
                var submesh = buffer.Submeshes[i];

                mesh.SetSubMesh(i, new UnityEngine.Rendering.SubMeshDescriptor()
                {
                    baseVertex = 0,
                    bounds = submesh.bounds.ToUnity(),
                    firstVertex = 0,
                    indexStart = submesh.indexStart,
                    indexCount = submesh.indexCount,
                    topology = submesh.topology.ToUnity(),
                    vertexCount = buffer.VertexCount,
                }, UPDATE_FLAGS);
            }
        }

        unsafe void UploadVertexBuffer(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            var data = meshBuffer.GetRawVertexBufferData();

            fixed (void* ptr = data)
            {
                NativeArray<byte> native;

                // Converting existing data to native array doesn't work in editor so we use this as a workaround
                // It's slower, but will allow for testing in the editor
                if (RenderingManager.IsDebug)
                    native = new NativeArray<byte>(data.ToArray(), Allocator.Persistent);
                else
                    native = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(ptr, data.Length, Allocator.None);

                mesh.SetVertexBufferData(native, 0, 0, data.Length, 0, UPDATE_FLAGS);

                native.Dispose();
            }
        }

        unsafe void UploadIndexBuffer(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            UpdateIndexLayout(meshBuffer, mesh);

            try
            {
                var data = meshBuffer.GetRawIndexBufferData();

                fixed (void* ptr = data)
                {
                    NativeArray<byte> native;

                    // Converting existing data to native array doesn't work in editor so we use this as a workaround
                    // It's slower, but will allow for testing in the editor
                    if (RenderingManager.IsDebug)
                        native = new NativeArray<byte>(data.ToArray(), Allocator.Persistent);
                    else
                        native = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(ptr, data.Length, Allocator.None);

                    mesh.SetIndexBufferData(native, 0, 0, data.Length, UPDATE_FLAGS);

                    native.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{GetHashCode()}] Exception uploading index buffer. MeshBuffer: {buffer.IndexCount}, " +
                    $"Submeshes: {buffer.SubmeshCount}, Mesh: {mesh.indexFormat}.\n" +
                    $"" + string.Join("\n", buffer.Submeshes.Select(s => s.ToString())));
                throw;
            }
        }

        unsafe void UploadBonesBuffer(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            if (buffer.BindPosesBufferLength == 0)
            {
                mesh.bindposes = null;
                return;
            }

            // Bind poses
            var poses = buffer.GetBindPosesBuffer<Matrix4x4>();
            var unity = mesh.bindposes;

            if (unity?.Length != poses.Length)
                unity = new UnityEngine.Matrix4x4[poses.Length];

            poses.CopyTo(unity);

            mesh.bindposes = unity;

            // Bones
            var boneCounts = buffer.GetBoneCountsBuffer();
            var boneWeights = MemoryMarshal.Cast<Renderite.Shared.BoneWeight, BoneWeight1>(buffer.GetBoneWeightsBuffer());

            fixed (void* countsPtr = boneCounts)
            fixed (void* weightsPtr = boneWeights)
            {
                NativeArray<byte> nativeCounts;
                NativeArray<BoneWeight1> nativeWeights;

                if(RenderingManager.IsDebug)
                {
                    nativeCounts = new NativeArray<byte>(boneCounts.ToArray(), Allocator.Persistent);
                    nativeWeights = new NativeArray<BoneWeight1>(boneWeights.ToArray(), Allocator.Persistent);
                }
                else
                {
                    nativeCounts = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(countsPtr, boneCounts.Length, Allocator.None);
                    nativeWeights = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<BoneWeight1>(weightsPtr, boneWeights.Length, Allocator.None);
                }

                mesh.SetBoneWeights(nativeCounts, nativeWeights);

                nativeCounts.Dispose();
                nativeWeights.Dispose();
            }
        }

        // ---- Softbody fork: in-place vertex updates of skinned meshes

        static bool IsSkinnedStreamsOnly(MeshUploadHint hint)
        {
            if (!(hint[MeshUploadHint.Flag.Positions] || hint[MeshUploadHint.Flag.Normals] || hint[MeshUploadHint.Flag.Tangents]))
                return false;

            return !hint[MeshUploadHint.Flag.VertexLayout] && !hint[MeshUploadHint.Flag.SubmeshLayout] && !hint[MeshUploadHint.Flag.Geometry]
                && !hint[MeshUploadHint.Flag.BindPoses] && !hint[MeshUploadHint.Flag.BoneWeights] && !hint[MeshUploadHint.Flag.Blendshapes]
                && !hint[MeshUploadHint.Flag.Colors] && !hint[MeshUploadHint.Flag.UV0s] && !hint[MeshUploadHint.Flag.UV1s]
                && !hint[MeshUploadHint.Flag.UV2s] && !hint[MeshUploadHint.Flag.UV3s] && !hint[MeshUploadHint.Flag.UV4s]
                && !hint[MeshUploadHint.Flag.UV5s] && !hint[MeshUploadHint.Flag.UV6s] && !hint[MeshUploadHint.Flag.UV7s];
        }

        static int FormatSize(VertexAttributeFormat format)
        {
            switch (format.ToUnity())
            {
                case UnityEngine.Rendering.VertexAttributeFormat.Float32:
                case UnityEngine.Rendering.VertexAttributeFormat.UInt32:
                case UnityEngine.Rendering.VertexAttributeFormat.SInt32:
                    return 4;
                case UnityEngine.Rendering.VertexAttributeFormat.UInt8:
                case UnityEngine.Rendering.VertexAttributeFormat.SInt8:
                case UnityEngine.Rendering.VertexAttributeFormat.UNorm8:
                case UnityEngine.Rendering.VertexAttributeFormat.SNorm8:
                    return 1;
                default:
                    return 2;
            }
        }

        /// <summary>Writes the flagged positions / normals / tangents from the buffer into the mesh through Unity's setters.
        /// False (nothing written) when the buffer does not hold them as Float32.</summary>
        unsafe bool TrySetSkinnedStreams(MeshBuffer buffer, UnityEngine.Mesh mesh, MeshUploadHint hint)
        {
            int offset = 0, posOffset = -1, normalOffset = -1, tangentOffset = -1;

            for (int i = 0; i < buffer.VertexAttributeCount; i++)
            {
                var attribute = buffer.VertexAttributes[i];
                bool float32 = attribute.format.ToUnity() == UnityEngine.Rendering.VertexAttributeFormat.Float32;

                if (attribute.attribute == VertexAttributeType.Position && float32 && attribute.dimensions == 3)
                    posOffset = offset;
                if (attribute.attribute == VertexAttributeType.Normal && float32 && attribute.dimensions == 3)
                    normalOffset = offset;
                if (attribute.attribute == VertexAttributeType.Tangent && float32 && attribute.dimensions == 4)
                    tangentOffset = offset;

                offset += attribute.dimensions * FormatSize(attribute.format);
            }

            int stride = offset;
            int count = buffer.VertexCount;

            bool positions = hint[MeshUploadHint.Flag.Positions];
            bool normals = hint[MeshUploadHint.Flag.Normals];
            bool tangents = hint[MeshUploadHint.Flag.Tangents];

            if ((positions && posOffset < 0) || (normals && normalOffset < 0) || (tangents && tangentOffset < 0))
                return false;

            var data = buffer.GetRawVertexBufferData();

            if ((long)stride * count > data.Length)
                return false;

            fixed (byte* source = data)
            {
                if (positions)
                    SetStream<UnityEngine.Vector3>(source, stride, posOffset, count, 12, 0, mesh);
                if (normals)
                    SetStream<UnityEngine.Vector3>(source, stride, normalOffset, count, 12, 1, mesh);
                if (tangents)
                    SetStream<UnityEngine.Vector4>(source, stride, tangentOffset, count, 16, 2, mesh);
            }

            return true;
        }

        static unsafe void SetStream<T>(byte* source, int stride, int offset, int count, int size, int which, UnityEngine.Mesh mesh)
            where T : struct
        {
            var native = new NativeArray<T>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            try
            {
                byte* target = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(native);

                for (int v = 0; v < count; v++)
                    UnsafeUtility.MemCpy(target + (long)v * size, source + (long)v * stride + offset, size);

                if (which == 0)
                    mesh.SetVertices(native);
                else if (which == 1)
                    mesh.SetNormals(native);
                else
                    mesh.SetTangents(native);
            }
            finally
            {
                native.Dispose();
            }
        }

        void SanitizeSubmeshes()
        {
            // Clean out previous submeshes first
            // This is important, because setting subMeshCount on Unity will force the submesh bounds to be
            // recalculated. Since they might not be in a good state, this can cause a crash
            // So before we change the count, we essentially give it fully empty submeshes, so it has nothing
            // to recalculate when we adjust the count
            // As of right now, I don't know of any method to set the submesh count without forcing the
            // bounds recalculation, so this ugly hack is necessary.
            for (int i = 0; i < mesh.subMeshCount; i++)
                mesh.SetSubMesh(i, new UnityEngine.Rendering.SubMeshDescriptor()
                {
                    baseVertex = 0,
                    bounds = new UnityEngine.Bounds(UnityEngine.Vector3.zero, UnityEngine.Vector3.zero),
                    firstVertex = 0,
                    indexStart = 0,
                    indexCount = 0,
                    topology = MeshTopology.Triangles,
                    vertexCount = 0
                }, UPDATE_FLAGS);
        }

        IEnumerator Upload()
        {
            // if it was destroyed, stop the upload
            if (meshBuffer == null)
                yield break;

            if (uploadData == null)
                throw new InvalidOperationException("Cannot run Upload when uploadData is not assigned!");

            var uploadHint = uploadData.uploadHint;

            if (uploadHint[MeshUploadHint.Flag.Debug] || RenderingManager.IsDebug)
                Debug.Log($"Uploading Mesh {AssetId} (first: {!firstUploadCompleted}): {meshBuffer}\nHint: " + uploadHint +
                    "\nBounds: " + uploadData.bounds);

            // Softbody fork: a vertex-streams-only update (positions / normals / tangents) of a skinned mesh goes into the
            // existing mesh instead of destroying it. Unity's own setters place the data whatever stream split Unity chose for
            // the skinned mesh, and bind poses, bone weights, blendshapes, submeshes and indices are left as they are.
            if (mesh != null && lastIsSkinned && mesh.isReadable && firstUploadCompleted && IsSkinnedStreamsOnly(uploadHint)
                && meshBuffer.VertexCount == mesh.vertexCount && TrySetSkinnedStreams(meshBuffer, mesh, uploadHint))
            {
                meshBuffer = null;

                PackerMemoryPool.Instance.Return(uploadData);
                uploadData = null;

                var inPlace = new MeshUploadResult();
                inPlace.assetId = AssetId;
                inPlace.instanceChanged = false;

                RenderingManager.Instance.SendAssetUpdate(inPlace);
                yield break;
            }

            if (mesh != null && (!mesh.isReadable || lastIsSkinned || uploadHint[MeshUploadHint.Flag.BindPoses]))
            {
                if (mesh)
                    UnityEngine.Object.Destroy(mesh);

                mesh = null;
            }

            lastIsSkinned = false;

            bool instanceChanged = false;

            if (mesh == null)
            {
                mesh = new UnityEngine.Mesh();
                instanceChanged = true;

                if (uploadHint[MeshUploadHint.Flag.Dynamic])
                    mesh.MarkDynamic();

                if (RenderingManager.IsDebug)
                    mesh.name = $"AssetId: {AssetId}";
            }

            if (uploadHint[MeshUploadHint.Flag.VertexLayout])
                UpdateVertexLayout(meshBuffer, mesh);

            // Allow the upload to be paused at this point. However only on the first upload. If we are mutating
            // a mesh repeatedly, we need to update the vertices & indexes in tandem
            if (!firstUploadCompleted)
                yield return null;

            // Assign the vertex buffer data
            if (uploadHint.AnyVertexStreams)
                UploadVertexBuffer(meshBuffer, mesh);

            if (!firstUploadCompleted)
                yield return null;

            // There's an issue. On first upload, if we don't do index buffer first, then some meshes will not work right
            // specifically skinned ones.
            // However on repreated uploads, if submesh count changes, then updating index first will then get messed up
            // be submesh layout update and will be in a corrupted state, so we need to swap the order
            if (mesh.subMeshCount != meshBuffer.SubmeshCount)
                SanitizeSubmeshes();

            bool indexFormatChanged = lastIndexBufferFormat != meshBuffer.IndexBufferFormat;
            lastIndexBufferFormat = meshBuffer.IndexBufferFormat;

            if (indexFormatChanged)
                SanitizeSubmeshes();

            if (firstUploadCompleted && !indexFormatChanged)
            {
                if (uploadHint[MeshUploadHint.Flag.SubmeshLayout])
                    UpdateSubmeshLayout(meshBuffer, mesh);

                if (uploadHint[MeshUploadHint.Flag.Geometry] || uploadHint[MeshUploadHint.Flag.SubmeshLayout])
                    UploadIndexBuffer(meshBuffer, mesh);
            }
            else
            {
                if (uploadHint[MeshUploadHint.Flag.Geometry] || uploadHint[MeshUploadHint.Flag.SubmeshLayout])
                    UploadIndexBuffer(meshBuffer, mesh);

                if (uploadHint[MeshUploadHint.Flag.SubmeshLayout])
                    UpdateSubmeshLayout(meshBuffer, mesh);
            }

            if (uploadHint[MeshUploadHint.Flag.BoneWeights] && (meshBuffer.BoneCount > 0 || (mesh.bindposes?.Length ?? 0) > 0))
            {
                if (!firstUploadCompleted)
                    yield return null;

                lastIsSkinned = true;

                UploadBonesBuffer(meshBuffer, mesh);
            }

            mesh.bounds = uploadData.bounds.ToUnity();

            if (uploadHint[MeshUploadHint.Flag.Blendshapes] && meshBuffer.BlendshapeBufferCount > 0)
            {
                yield return null;

                lastIsSkinned = true;

                if ((mesh.bindposes?.Length ?? 0) == 0)
                {
                    // We need to generate a fake bone. This is due to Unity bug
                    var bindposes = new UnityEngine.Matrix4x4[1];
                    bindposes[0] = UnityEngine.Matrix4x4.identity;

                    mesh.bindposes = bindposes;

                    // Generate fake bones
                    var boneCounts = new NativeArray<byte>(meshBuffer.VertexCount, Allocator.Temp);
                    var boneWeights = new NativeArray<BoneWeight1>(meshBuffer.VertexCount, Allocator.Temp);

                    for (int i = 0; i < meshBuffer.VertexCount; i++)
                    {
                        boneCounts[i] = 1;
                        boneWeights[i] = new BoneWeight1()
                        {
                            boneIndex = 0,
                            weight = 0
                        };
                    }

                    mesh.SetBoneWeights(boneCounts, boneWeights);

                    boneCounts.Dispose();
                    boneWeights.Dispose();
                }

                // We need to clear any existing blendshapes
                if (mesh.blendShapeCount > 0)
                {
                    mesh.ClearBlendShapes();
                    yield return null;
                }

                UnityEngine.Vector3[] posStaging;
                UnityEngine.Vector3[] norStaging = null;
                UnityEngine.Vector3[] tanStaging = null;

                posStaging = new UnityEngine.Vector3[meshBuffer.VertexCount];

                int offset = 0;

                for (int i = 0; i < meshBuffer.BlendshapeBufferCount; i++)
                {
                    var blendshape = meshBuffer.BlendshapeBuffers[i];
                    var name = GetBlendshapeName(blendshape.blendshapeIndex);

                    var buffer = meshBuffer.GetBlendshapeBuffer<Vector3>();

                    void ExtractData(Span<UnityEngine.Vector3> source, UnityEngine.Vector3[] target)
                    {
                        source.Slice(offset, meshBuffer.VertexCount).CopyTo(target.AsSpan());
                        offset += meshBuffer.VertexCount;
                    }

                    ExtractData(buffer, posStaging);

                    var normals = blendshape.dataFlags.HasFlag(BlendshapeDataFlags.Normals);
                    var tangents = blendshape.dataFlags.HasFlag(BlendshapeDataFlags.Tangets);

                    if (normals)
                    {
                        if (norStaging == null)
                            norStaging = new UnityEngine.Vector3[meshBuffer.VertexCount];

                        ExtractData(buffer, norStaging);
                    }

                    if (tangents)
                    {
                        if (tanStaging == null)
                            tanStaging = new UnityEngine.Vector3[meshBuffer.VertexCount];

                        ExtractData(buffer, tanStaging);
                    }

                    mesh.AddBlendShapeFrame(name, blendshape.frameWeight, posStaging,
                        normals ? norStaging : null,
                        tangents ? tanStaging : null);

                    yield return null;
                }
            }

            // don't keep the generated mesh data around, since this mesh shouldn't be updated often
            // or at all after the first upload
            meshBuffer = null;

            firstUploadCompleted = true;

            PackerMemoryPool.Instance.Return(uploadData);
            uploadData = null;

            // Send it only after everything is completed, because the engine can potentially send another mesh upload
            // after this is sent fast enough that there will be a race condition
            var result = new MeshUploadResult();

            result.assetId = AssetId;
            result.instanceChanged = instanceChanged;

            RenderingManager.Instance.SendAssetUpdate(result);
        }

        public void Unload()
        {
            AssetIntegrator.EnqueueProcessing(Destroy, true);
        }

        void Destroy()
        {
            if (mesh != null)
                UnityEngine.Object.Destroy(mesh);

            mesh = null;
            meshBuffer = null;
        }
    }
}
