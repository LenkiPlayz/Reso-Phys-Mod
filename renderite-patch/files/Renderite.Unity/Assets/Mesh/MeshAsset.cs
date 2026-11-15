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

        // Softbody fork, v2 (Softbody 0.30.0, avatar cloth): the in-place update also takes the triangles (every submesh keeping
        // its index range), the UV channels and the vertex colours, so a tear in avatar cloth (spare vertices taking over a torn
        // edge) needs no new mesh. The plugin sends those only when it finds this second event.
        public const string SoftbodyHandshakeNameV2 = @"Local\Softbody.RendererPatch.SkinnedInPlace.v2";
        static readonly System.Threading.EventWaitHandle softbodyHandshakeV2;

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

            try
            {
                softbodyHandshakeV2 = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, SoftbodyHandshakeNameV2);
                Debug.Log("[Softbody fork] in-place triangles, UVs and colours of skinned meshes enabled; handshake " + SoftbodyHandshakeNameV2 + " open");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Softbody fork] could not open the Softbody v2 handshake event: " + ex.Message);
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
            softbodyIndexCount = meshBuffer.IndexCount;   // Softbody fork, v2: see IndicesFitInPlace

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

        static readonly MeshUploadHint.Flag[] softbodyUvFlags =
        {
            MeshUploadHint.Flag.UV0s, MeshUploadHint.Flag.UV1s, MeshUploadHint.Flag.UV2s, MeshUploadHint.Flag.UV3s,
            MeshUploadHint.Flag.UV4s, MeshUploadHint.Flag.UV5s, MeshUploadHint.Flag.UV6s, MeshUploadHint.Flag.UV7s,
        };

        /// <summary>An update the in-place path takes: positions, normals or tangents, and with them (v2) colours, UV channels
        /// and triangles; nothing that changes the vertex or submesh layout, the bones or the blendshapes.</summary>
        static bool IsSkinnedInPlace(MeshUploadHint hint)
        {
            if (!(hint[MeshUploadHint.Flag.Positions] || hint[MeshUploadHint.Flag.Normals] || hint[MeshUploadHint.Flag.Tangents]))
                return false;

            return !hint[MeshUploadHint.Flag.VertexLayout] && !hint[MeshUploadHint.Flag.SubmeshLayout]
                && !hint[MeshUploadHint.Flag.BindPoses] && !hint[MeshUploadHint.Flag.BoneWeights] && !hint[MeshUploadHint.Flag.Blendshapes];
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

        /// <summary>Writes the flagged positions / normals / tangents (v2: and colours, UV channels and triangles) from the
        /// buffer into the mesh through Unity's setters. All or nothing: false (nothing written) when the buffer does not hold
        /// them in a format this takes (Float32 positions, normals, tangents and UVs; Float32, half or byte colours), or its
        /// triangles would not fit the mesh's index buffer as it is.</summary>
        unsafe bool TrySetSkinnedStreams(MeshBuffer buffer, UnityEngine.Mesh mesh, MeshUploadHint hint)
        {
            int offset = 0, posOffset = -1, normalOffset = -1, tangentOffset = -1, colorOffset = -1;
            var colorFormat = UnityEngine.Rendering.VertexAttributeFormat.Float32;
            int* uvOffset = stackalloc int[8];
            int* uvDimensions = stackalloc int[8];

            for (int c = 0; c < 8; c++)
            {
                uvOffset[c] = -1;
                uvDimensions[c] = 0;
            }

            for (int i = 0; i < buffer.VertexAttributeCount; i++)
            {
                var attribute = buffer.VertexAttributes[i];
                var format = attribute.format.ToUnity();
                bool float32 = format == UnityEngine.Rendering.VertexAttributeFormat.Float32;

                if (attribute.attribute == VertexAttributeType.Position && float32 && attribute.dimensions == 3)
                    posOffset = offset;
                if (attribute.attribute == VertexAttributeType.Normal && float32 && attribute.dimensions == 3)
                    normalOffset = offset;
                if (attribute.attribute == VertexAttributeType.Tangent && float32 && attribute.dimensions == 4)
                    tangentOffset = offset;

                if (attribute.attribute == VertexAttributeType.Color && attribute.dimensions == 4 && (float32
                    || format == UnityEngine.Rendering.VertexAttributeFormat.Float16 || format == UnityEngine.Rendering.VertexAttributeFormat.UNorm8))
                {
                    colorOffset = offset;
                    colorFormat = format;
                }

                int uv = attribute.attribute - VertexAttributeType.UV0;

                if (uv >= 0 && uv < 8 && float32 && attribute.dimensions >= 2 && attribute.dimensions <= 4)
                {
                    uvOffset[uv] = offset;
                    uvDimensions[uv] = attribute.dimensions;
                }

                offset += attribute.dimensions * FormatSize(attribute.format);
            }

            int stride = offset;
            int count = buffer.VertexCount;

            bool positions = hint[MeshUploadHint.Flag.Positions];
            bool normals = hint[MeshUploadHint.Flag.Normals];
            bool tangents = hint[MeshUploadHint.Flag.Tangents];
            bool colors = hint[MeshUploadHint.Flag.Colors];
            bool triangles = hint[MeshUploadHint.Flag.Geometry];

            if ((positions && posOffset < 0) || (normals && normalOffset < 0) || (tangents && tangentOffset < 0) || (colors && colorOffset < 0))
                return false;

            for (int c = 0; c < 8; c++)
                if (hint[softbodyUvFlags[c]] && uvOffset[c] < 0)
                    return false;

            if (triangles && !IndicesFitInPlace(buffer, mesh))
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
                if (colors)
                    SetColorStream(source, stride, colorOffset, colorFormat, count, mesh);

                for (int c = 0; c < 8; c++)
                {
                    if (!hint[softbodyUvFlags[c]])
                        continue;

                    if (uvDimensions[c] == 2)
                        SetStream<UnityEngine.Vector2>(source, stride, uvOffset[c], count, 8, 4 + c, mesh);
                    else if (uvDimensions[c] == 3)
                        SetStream<UnityEngine.Vector3>(source, stride, uvOffset[c], count, 12, 4 + c, mesh);
                    else
                        SetStream<UnityEngine.Vector4>(source, stride, uvOffset[c], count, 16, 4 + c, mesh);
                }
            }

            if (triangles)
                SetIndicesInPlace(buffer, mesh);

            return true;
        }

        /// <summary>which: 0 positions, 1 normals, 2 tangents, 3 colours (v2), 4 + n UV channel n (v2).</summary>
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
                else if (which == 2)
                    mesh.SetTangents(native);
                else if (which == 3)
                    mesh.SetColors(native);
                else
                    mesh.SetUVs(which - 4, native);
            }
            finally
            {
                native.Dispose();
            }
        }

        /// <summary>v2: the vertex colours through Unity's setter: bytes as Color32, floats as Color, and halves (what the
        /// engine encodes) widened to floats, exactly. Unity keeps the format of the last setter, so a mesh with half colours
        /// changes to float colours on its first in-place colour update.</summary>
        static unsafe void SetColorStream(byte* source, int stride, int offset, UnityEngine.Rendering.VertexAttributeFormat format, int count,
            UnityEngine.Mesh mesh)
        {
            if (format == UnityEngine.Rendering.VertexAttributeFormat.UNorm8)
            {
                SetStream<UnityEngine.Color32>(source, stride, offset, count, 4, 3, mesh);
                return;
            }

            if (format == UnityEngine.Rendering.VertexAttributeFormat.Float32)
            {
                SetStream<UnityEngine.Color>(source, stride, offset, count, 16, 3, mesh);
                return;
            }

            var native = new NativeArray<UnityEngine.Color>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            try
            {
                for (int v = 0; v < count; v++)
                {
                    ushort* h = (ushort*)(source + (long)v * stride + offset);
                    native[v] = new UnityEngine.Color(HalfToFloat(h[0]), HalfToFloat(h[1]), HalfToFloat(h[2]), HalfToFloat(h[3]));
                }

                mesh.SetColors(native);
            }
            finally
            {
                native.Dispose();
            }
        }

        /// <summary>An IEEE half to a float (exact: every half is a float).</summary>
        static unsafe float HalfToFloat(ushort half)
        {
            uint sign = (uint)(half & 0x8000) << 16;
            uint exponent = ((uint)half >> 10) & 0x1f;
            uint mantissa = (uint)half & 0x3ff;
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                    bits = sign;
                else
                {
                    // a subnormal half is a normal float
                    uint e = 113;

                    while ((mantissa & 0x400) == 0)
                    {
                        mantissa <<= 1;
                        e--;
                    }

                    bits = sign | (e << 23) | ((mantissa & 0x3ff) << 13);
                }
            }
            else if (exponent == 31)
                bits = sign | 0x7f800000u | (mantissa << 13);
            else
                bits = sign | ((exponent + 112) << 23) | (mantissa << 13);

            return *(float*)&bits;
        }

        // the index count the mesh's index buffer was last laid out for (UploadIndexBuffer)
        int softbodyIndexCount = -1;

        /// <summary>v2: whether the buffer's triangles go into the mesh's index buffer as it is laid out: the same index format
        /// and count, the same submeshes with the same index ranges and topology. Then only which vertices they use differs
        /// (a tear in avatar cloth moves triangle corners onto spare vertices).</summary>
        bool IndicesFitInPlace(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            if (lastIndexBufferFormat != buffer.IndexBufferFormat || buffer.IndexCount != softbodyIndexCount)
                return false;

            if (buffer.Submeshes == null || mesh.subMeshCount != buffer.SubmeshCount || buffer.Submeshes.Count < buffer.SubmeshCount)
                return false;

            for (int i = 0; i < buffer.SubmeshCount; i++)
            {
                var have = mesh.GetSubMesh(i);
                var want = buffer.Submeshes[i];

                if (have.indexStart != want.indexStart || have.indexCount != want.indexCount || have.topology != want.topology.ToUnity())
                    return false;
            }

            return true;
        }

        /// <summary>v2: the buffer's indices into the mesh's index buffer, laid out as it is (see IndicesFitInPlace).</summary>
        unsafe void SetIndicesInPlace(MeshBuffer buffer, UnityEngine.Mesh mesh)
        {
            var data = buffer.GetRawIndexBufferData();

            fixed (void* ptr = data)
            {
                NativeArray<byte> native;

                if (RenderingManager.IsDebug)
                    native = new NativeArray<byte>(data.ToArray(), Allocator.Persistent);
                else
                    native = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(ptr, data.Length, Allocator.None);

                mesh.SetIndexBufferData(native, 0, 0, data.Length, UPDATE_FLAGS);

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
            // v2: colours, UV channels and triangles (into the index buffer as it is laid out) come the same way.
            if (mesh != null && lastIsSkinned && mesh.isReadable && firstUploadCompleted && IsSkinnedInPlace(uploadHint)
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
