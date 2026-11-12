using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Renderite.Shared;
using Renderite.Unity;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityVertexAttributeDescriptor = UnityEngine.Rendering.VertexAttributeDescriptor;
using VertexAttributeFormat = UnityEngine.Rendering.VertexAttributeFormat;
using VertexAttribute = UnityEngine.Rendering.VertexAttribute;

namespace SoftbodyRenderer
{
    /// <summary>
    /// The patch on <c>MeshAsset/&lt;Upload&gt;d__21.MoveNext</c> (Harmony prefix, postfix, finalizer; no IL is rewritten).
    ///
    /// Only uploads that the stock renderer cannot handle are touched: a vertex-streams-only hint
    /// (<see cref="UploadPatchSpec.IsVertexOnly"/>) arriving for a mesh whose last upload was skinned (<c>lastIsSkinned</c>).
    /// The stock code destroys such a mesh and builds a new one from the upload, which has no layout, indices, bones or
    /// blendshapes: the mesh would vanish. FrooxEngine never sends these (it widens any partial upload of a mesh with bones
    /// to a VertexLayout upload); Softbody's GPU path does.
    /// <list type="bullet">
    /// <item>In place: when the Unity mesh's stream-0 layout and vertex count equal the upload's, <c>lastIsSkinned</c> is
    /// cleared for the duration of this MoveNext, so the stock destroy check passes and the stock code re-uploads stream 0
    /// (<c>SetVertexBufferData</c>) into the existing mesh and sets the bounds. Bind poses, bone weights (stored by
    /// <c>SetBoneWeights</c> outside stream 0), blendshapes, submeshes and indices are not touched, because their flags are
    /// absent. The finalizer sets <c>lastIsSkinned</c> back, and the result reports <c>instanceChanged = false</c>.</item>
    /// <item>Refused: when the layout differs (or the mesh is missing), the upload is dropped before it can destroy the mesh,
    /// and the result reports <c>instanceChanged = true</c>. The old mesh stays as it was; the engine-side plugin takes that as
    /// "patch not applicable" and falls back to its CPU path.</item>
    /// </list>
    /// Everything else (every normal upload, skinned or not) runs the stock code unchanged.
    /// </summary>
    static class MeshUploadPatch
    {
        static FieldInfo _state, _this;
        static AccessTools.FieldRef<MeshAsset, bool> _lastIsSkinned, _firstUploadCompleted;
        static AccessTools.FieldRef<MeshAsset, Mesh> _mesh;
        static AccessTools.FieldRef<MeshAsset, MeshUploadData> _uploadData;
        static AccessTools.FieldRef<MeshAsset, MeshBuffer> _meshBuffer;
        static bool _notify;
        static readonly UnityVertexAttributeDescriptor[] _attrs = new UnityVertexAttributeDescriptor[32];

        public static long InPlace, Refused, Errors;
        static bool _loggedFirst;

        /// <summary>Verifies the coroutine's IL and installs the patch. Returns null on success, else why it was not installed.</summary>
        public static string Install(Harmony harmony, bool notifyRenderers, out string detail)
        {
            detail = null;
            _notify = notifyRenderers;
            Type coroutine = null;
            foreach (var t in typeof(MeshAsset).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                if (t.Name.StartsWith(UploadPatchSpec.CoroutinePrefix, StringComparison.Ordinal) && typeof(System.Collections.IEnumerator).IsAssignableFrom(t))
                    coroutine = t;
            if (coroutine == null) return "MeshAsset has no <Upload> coroutine";
            var moveNext = AccessTools.Method(coroutine, "MoveNext");
            _state = AccessTools.Field(coroutine, UploadPatchSpec.StateField);
            _this = AccessTools.Field(coroutine, UploadPatchSpec.ThisField);
            if (moveNext == null || _state == null || _this == null || _state.FieldType != typeof(int) || _this.FieldType != typeof(MeshAsset))
                return "the <Upload> coroutine does not have the expected MoveNext / state / this members";
            foreach (var (name, type) in new[] {
                         (UploadPatchSpec.LastIsSkinnedField, typeof(bool)), (UploadPatchSpec.FirstUploadCompletedField, typeof(bool)),
                         (UploadPatchSpec.MeshField, typeof(Mesh)), (UploadPatchSpec.UploadDataField, typeof(MeshUploadData)),
                         (UploadPatchSpec.MeshBufferField, typeof(MeshBuffer)) })
            {
                var f = AccessTools.Field(typeof(MeshAsset), name);
                if (f == null || f.FieldType != type || f.IsStatic) return $"MeshAsset.{name} is missing or not a {type.Name}";
            }
            _lastIsSkinned = AccessTools.FieldRefAccess<MeshAsset, bool>(UploadPatchSpec.LastIsSkinnedField);
            _firstUploadCompleted = AccessTools.FieldRefAccess<MeshAsset, bool>(UploadPatchSpec.FirstUploadCompletedField);
            _mesh = AccessTools.FieldRefAccess<MeshAsset, Mesh>(UploadPatchSpec.MeshField);
            _uploadData = AccessTools.FieldRefAccess<MeshAsset, MeshUploadData>(UploadPatchSpec.UploadDataField);
            _meshBuffer = AccessTools.FieldRefAccess<MeshAsset, MeshBuffer>(UploadPatchSpec.MeshBufferField);

            // The IL must be the build this patch was verified against (see UploadPatchSpec): the in-place path reuses the
            // stock code, so a renderer update that changes what the coroutine does must not be patched blindly.
            var original = PatchProcessor.GetOriginalInstructions(moveNext, out ILGenerator _);
            var code = new List<KeyValuePair<string, object>>(original.Count);
            foreach (var ci in original) code.Add(UploadPatchSpec.Op(ci.opcode, ci.operand));
            var skeleton = UploadPatchSpec.Skeleton(code);
            ulong hash = UploadPatchSpec.Hash(skeleton);
            detail = $"{coroutine.Name}.MoveNext: {original.Count} instructions, skeleton {skeleton.Count} entries, hash {hash:x16}";
            if (hash != UploadPatchSpec.VerifiedSkeletonHash)
                return $"MeshAsset.Upload IL differs from the verified renderer build (skeleton hash {hash:x16}, verified {UploadPatchSpec.VerifiedSkeletonHash:x16}); not patching";

            harmony.Patch(moveNext,
                prefix: new HarmonyMethod(typeof(MeshUploadPatch), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(MeshUploadPatch), nameof(Postfix)),
                transpiler: null,
                finalizer: new HarmonyMethod(typeof(MeshUploadPatch), nameof(Finalizer)),
                ilmanipulator: null);
            return null;
        }

        static bool Prefix(object __instance, ref bool __result, out MeshAsset __state)
        {
            __state = null;
            try
            {
                if ((int)_state.GetValue(__instance) != 0) return true;           // only the first state decides; later states are stock
                if (!(_this.GetValue(__instance) is MeshAsset asset) || !_lastIsSkinned(asset)) return true;
                var data = _uploadData(asset);
                var buffer = _meshBuffer(asset);
                if (data == null || buffer == null) return true;                   // stock: nothing to do / its own exception
                if (!UploadPatchSpec.IsVertexOnly(data.uploadHint.Flags)) return true; // every normal (skinned) upload: stock

                string why = CheckInPlace(asset, buffer);
                if (why == null)
                {
                    _lastIsSkinned(asset) = false;   // the stock destroy check is "!isReadable || lastIsSkinned || hint[BindPoses]"
                    __state = asset;
                    return true;
                }
                // 0.9.1: Unity re-arranges a skinned mesh's vertex data into several streams (position/normal/tangent apart from
                // the UVs), so stream 0 no longer matches the engine's single interleaved layout. Repack the upload into the
                // mesh's own streams instead of refusing.
                string repack = null;
                try { repack = RepackInPlace(asset, data, buffer); }
                catch (Exception ex) { repack = "repacking threw " + ex.GetType().Name + ": " + ex.Message; }
                if (repack == null)
                {
                    try { Finish(asset, data, instanceChanged: false); }
                    catch (Exception ex) { Errors++; if (Errors <= 5) Plugin.Log?.LogError("finishing the repacked upload failed: " + ex); }
                    __result = false;
                    return false;
                }
                why += "; repacking: " + repack;
                try { Refuse(asset, data, why); }
                catch (Exception ex) { Errors++; if (Errors <= 5) Plugin.Log?.LogError("refusing the upload failed: " + ex); }
                __result = false;                    // coroutine finished: the AssetIntegrator dequeues it (never the stock path here)
                return false;
            }
            catch (Exception ex)
            {
                Errors++;
                if (Errors <= 5) Plugin.Log?.LogError("MeshAsset.Upload prefix failed (stock upload runs): " + ex);
                return true;
            }
        }

        static void Postfix(MeshAsset __state)
        {
            if (__state == null) return;
            InPlace++;
            try
            {
                // SetVertexBufferData was called with DontNotifyMeshUsers; tell the SkinnedMeshRenderer the source changed.
                if (_notify) _mesh(__state)?.MarkModified();
            }
            catch (Exception ex) { Errors++; if (Errors <= 5) Plugin.Log?.LogError("MarkModified failed: " + ex.Message); }
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                var m = _mesh(__state);
                Plugin.Log?.LogInfo($"first in-place skinned vertex upload: mesh asset {__state.AssetId}, {(m != null ? m.vertexCount : 0)} vertices, " +
                                    $"{(m != null ? m.bindposes.Length : 0)} bind poses, {(m != null ? m.blendShapeCount : 0)} blendshapes kept");
            }
        }

        // Runs after the postfix, and also when MoveNext threw.
        static void Finalizer(MeshAsset __state)
        {
            if (__state != null) _lastIsSkinned(__state) = true;
        }

        /// <summary>Null when the upload can go into the existing mesh as it is; otherwise why not.</summary>
        static string CheckInPlace(MeshAsset asset, MeshBuffer buffer)
        {
            var mesh = _mesh(asset);
            if (mesh == null) return "the asset has no Unity mesh";
            if (!mesh.isReadable) return "the Unity mesh is not readable";
            if (!_firstUploadCompleted(asset)) return "the first upload has not completed";
            if (buffer.Data == null) return "the upload carries no buffer";
            if (mesh.vertexCount != buffer.VertexCount) return $"vertex count {buffer.VertexCount} != the mesh's {mesh.vertexCount}";
            if ((long)buffer.VertexStride * buffer.VertexCount > buffer.Data.SizeBytes) return "the buffer is smaller than its vertex data";
            // Stream 0 must be exactly the layout UpdateVertexLayout built from this buffer (same order, formats and dimensions):
            // then the stock SetVertexBufferData(stream 0) lands every byte where it belongs. Attributes in other streams (Unity
            // keeps skin weights apart from the vertex streams the renderer declared) are not touched by it.
            int n = mesh.GetVertexAttributes(_attrs);
            if (n > _attrs.Length) return $"the mesh has {n} vertex attributes";
            var engine = buffer.VertexAttributes;
            int count = buffer.VertexAttributeCount, k = 0;
            for (int i = 0; i < n; i++)
            {
                var u = _attrs[i];
                if (u.stream != 0) continue;
                if (k >= count) return $"stream 0 has more attributes than the upload ({u.attribute} after {count})";
                var e = engine[k++];
                if (u.attribute != Renderite.Unity.Helper.ToUnity(e.attribute) || u.format != Renderite.Unity.Helper.ToUnity(e.format) || u.dimension != e.dimensions)
                    return $"vertex attribute {k - 1} differs: mesh {u.attribute} {u.format}x{u.dimension}, upload {e.attribute} {e.format}x{e.dimensions}";
            }
            if (k != count) return $"stream 0 has {k} attributes, the upload {count}";
            return null;
        }

        static int FormatSize(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                default: return 1;
            }
        }

        static bool Flagged(MeshUploadHint hint, VertexAttribute a)
        {
            switch (a)
            {
                case VertexAttribute.Position: return hint[MeshUploadHint.Flag.Positions];
                case VertexAttribute.Normal: return hint[MeshUploadHint.Flag.Normals];
                case VertexAttribute.Tangent: return hint[MeshUploadHint.Flag.Tangents];
                case VertexAttribute.Color: return hint[MeshUploadHint.Flag.Colors];
                case VertexAttribute.TexCoord0: return hint[MeshUploadHint.Flag.UV0s];
                case VertexAttribute.TexCoord1: return hint[MeshUploadHint.Flag.UV1s];
                case VertexAttribute.TexCoord2: return hint[MeshUploadHint.Flag.UV2s];
                case VertexAttribute.TexCoord3: return hint[MeshUploadHint.Flag.UV3s];
                default: return false;
            }
        }

        /// <summary>
        /// Writes the upload's positions and normals (and tangents, if flagged) into the existing mesh through Unity's own
        /// setters (SetVertices / SetNormals / SetTangents), which place them correctly whatever stream split Unity chose for
        /// this skinned mesh and leave bind poses, bone weights and blendshapes alone. Only Float32 attributes are handled; any
        /// other flagged attribute refuses. Null on success, else why not.
        /// </summary>
        static unsafe string RepackInPlace(MeshAsset asset, MeshUploadData data, MeshBuffer buffer)
        {
            var mesh = _mesh(asset);
            if (mesh == null) return "no Unity mesh";
            if (!mesh.isReadable) return "the Unity mesh is not readable";
            if (!_firstUploadCompleted(asset)) return "the first upload has not completed";
            int vc = buffer.VertexCount;
            if (mesh.vertexCount != vc) return $"vertex count {vc} != the mesh's {mesh.vertexCount}";
            int ustride = buffer.VertexStride;
            var hint = data.uploadHint;
            if (hint[MeshUploadHint.Flag.Colors] || hint[MeshUploadHint.Flag.UV0s] || hint[MeshUploadHint.Flag.UV1s] ||
                hint[MeshUploadHint.Flag.UV2s] || hint[MeshUploadHint.Flag.UV3s])
                return "only positions, normals and tangents are applied in place on a skinned mesh";

            int off = 0, posOff = -1, nrmOff = -1, tanOff = -1;
            var engine = buffer.VertexAttributes;
            for (int i = 0; i < buffer.VertexAttributeCount; i++)
            {
                var e = engine[i];
                var fmt = Renderite.Unity.Helper.ToUnity(e.format);
                var at = Renderite.Unity.Helper.ToUnity(e.attribute);
                bool f32 = fmt == VertexAttributeFormat.Float32;
                if (at == VertexAttribute.Position && f32 && e.dimensions == 3) posOff = off;
                if (at == VertexAttribute.Normal && f32 && e.dimensions == 3) nrmOff = off;
                if (at == VertexAttribute.Tangent && f32 && e.dimensions == 4) tanOff = off;
                off += e.dimensions * FormatSize(fmt);
            }
            if (off != ustride) return $"the upload's attributes add up to {off} bytes, its stride is {ustride}";
            if ((long)ustride * vc > buffer.Data.SizeBytes) return "the buffer is smaller than its vertex data";
            bool wantPos = hint[MeshUploadHint.Flag.Positions], wantNrm = hint[MeshUploadHint.Flag.Normals], wantTan = hint[MeshUploadHint.Flag.Tangents];
            if (wantPos && posOff < 0) return "the upload's positions are not Float32x3";
            if (wantNrm && nrmOff < 0) return "the upload's normals are not Float32x3";
            if (wantTan && tanOff < 0) return "the upload's tangents are not Float32x4";
            if (!wantPos && !wantNrm && !wantTan) return "nothing to apply";

            fixed (byte* src = buffer.GetRawVertexBufferData())
            {
                if (wantPos) SetFromBuffer<Vector3>(mesh, src, ustride, posOff, vc, 12, 0);
                if (wantNrm) SetFromBuffer<Vector3>(mesh, src, ustride, nrmOff, vc, 12, 1);
                if (wantTan) SetFromBuffer<Vector4>(mesh, src, ustride, tanOff, vc, 16, 2);
            }
            InPlace++;
            if (!_loggedRepack)
            {
                _loggedRepack = true;
                Plugin.Log?.LogInfo($"first in-place skinned vertex upload (Unity setters): mesh asset {asset.AssetId}, {mesh.vertexCount} vertices, " +
                                    $"{mesh.bindposes.Length} bind poses, {mesh.blendShapeCount} blendshapes kept");
            }
            return null;
        }

        static unsafe void SetFromBuffer<T>(Mesh mesh, byte* src, int stride, int offset, int count, int size, int which) where T : struct
        {
            var arr = new NativeArray<T>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                byte* dst = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(arr);
                for (int v = 0; v < count; v++) UnsafeUtility.MemCpy(dst + (long)v * size, src + (long)v * stride + offset, size);
                if (which == 0) mesh.SetVertices(arr);
                else if (which == 1) mesh.SetNormals(arr);
                else mesh.SetTangents(arr);
            }
            finally { arr.Dispose(); }
        }
        static bool _loggedRepack;

        /// <summary>Ends the coroutine for an upload this patch handled itself, like the stock tail (result to the engine).</summary>
        static void Finish(MeshAsset asset, MeshUploadData data, bool instanceChanged)
        {
            int assetId = asset.AssetId;
            _meshBuffer(asset) = null;
            _uploadData(asset) = null;
            try { PackerMemoryPool.Instance.Return(data); }
            catch (Exception ex) { Plugin.Log?.LogWarning("returning the upload's data to the pool failed: " + ex.Message); }
            RenderingManager.Instance.SendAssetUpdate(new MeshUploadResult { assetId = assetId, instanceChanged = instanceChanged });
        }

        /// <summary>Drops the upload without touching the mesh, finishing it like the coroutine's own tail does.</summary>
        static void Refuse(MeshAsset asset, MeshUploadData data, string why)
        {
            Refused++;
            if (Refused <= 5 || Refused % 100 == 0)
                Plugin.Log?.LogWarning($"refused a vertex-only upload on skinned mesh asset {asset.AssetId} ({data.uploadHint}): {why}. " +
                                       "The mesh is kept as it was (nothing applied); Softbody stops for it.");
            int assetId = asset.AssetId;
            // the asset first, so its next upload is accepted whatever happens below (Handle throws while uploadData is set)
            _meshBuffer(asset) = null;
            _uploadData(asset) = null;
            try { PackerMemoryPool.Instance.Return(data); }
            catch (Exception ex) { Plugin.Log?.LogWarning("returning the refused upload's data to the pool failed: " + ex.Message); }
            RenderingManager.Instance.SendAssetUpdate(new MeshUploadResult { assetId = assetId, instanceChanged = true });
        }
    }
}
