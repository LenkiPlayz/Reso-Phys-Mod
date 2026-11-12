using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Renderite.Shared;

namespace SoftbodyRenderer
{
    /// <summary>
    /// What the renderer patch relies on, kept free of Unity, BepInEx and Harmony so the offline tests (net10, the engine's
    /// Renderite.Shared) compile this same file and check it against the installed Renderite.Unity.dll with Cecil.
    ///
    /// The patched method is the upload coroutine of <c>Renderite.Unity.MeshAsset</c> (<c>&lt;Upload&gt;d__21.MoveNext</c>).
    /// Its first state destroys and recreates the Unity mesh when <c>!mesh.isReadable || lastIsSkinned || hint[BindPoses]</c>,
    /// then clears <c>lastIsSkinned</c>; the rest only touches what the hint flags (VertexLayout, the vertex streams,
    /// SubmeshLayout/Geometry, BoneWeights, Blendshapes), plus the submesh/index-format sanitizing and the bounds. So for a
    /// vertex-streams-only hint on a mesh that is still skinned, skipping that recreate (and keeping <c>lastIsSkinned</c>)
    /// is the whole in-place path: the stock code then only re-uploads stream 0 and sets the bounds.
    /// That reading is pinned by <see cref="VerifiedSkeletonHash"/>: the patch refuses to install on any other IL.
    /// </summary>
    public static class UploadPatchSpec
    {
        public const string MeshAssetType = "Renderite.Unity.MeshAsset";
        /// <summary>Name prefix of the compiler-generated upload coroutine nested in MeshAsset (it is <c>&lt;Upload&gt;d__21</c>).</summary>
        public const string CoroutinePrefix = "<Upload>d__";
        public const string StateField = "<>1__state";
        public const string ThisField = "<>4__this";
        public const string LastIsSkinnedField = "lastIsSkinned";
        public const string FirstUploadCompletedField = "firstUploadCompleted";
        public const string MeshField = "mesh";
        public const string UploadDataField = "uploadData";
        public const string MeshBufferField = "meshBuffer";

        /// <summary>Name of the named kernel event the renderer plugin holds open while the patch is active; the engine-side
        /// plugin only uses the GPU path when it exists (Windows: both processes see the session's Local\ namespace).</summary>
        public const string HandshakeName = @"Local\Softbody.RendererPatch.SkinnedInPlace.v1";

        public const MeshUploadHint.Flag VertexStreams =
            MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.Colors |
            MeshUploadHint.Flag.UV0s | MeshUploadHint.Flag.UV1s | MeshUploadHint.Flag.UV2s | MeshUploadHint.Flag.UV3s |
            MeshUploadHint.Flag.UV4s | MeshUploadHint.Flag.UV5s | MeshUploadHint.Flag.UV6s | MeshUploadHint.Flag.UV7s;

        /// <summary>Flags the upload coroutine ignores for an existing mesh: Dynamic only matters when it creates one (MarkDynamic),
        /// Readable is never read by the renderer, Debug only logs.</summary>
        public const MeshUploadHint.Flag Harmless = MeshUploadHint.Flag.Dynamic | MeshUploadHint.Flag.Readable | MeshUploadHint.Flag.Debug;

        /// <summary>Everything that changes more than the vertex data: these uploads always take the stock path.</summary>
        public const MeshUploadHint.Flag Structural =
            MeshUploadHint.Flag.VertexLayout | MeshUploadHint.Flag.SubmeshLayout | MeshUploadHint.Flag.Geometry |
            MeshUploadHint.Flag.BindPoses | MeshUploadHint.Flag.BoneWeights | MeshUploadHint.Flag.Blendshapes;

        /// <summary>True for an upload that only rewrites vertex streams: at least one stream, nothing structural.</summary>
        public static bool IsVertexOnly(MeshUploadHint.Flag flags) =>
            (flags & VertexStreams) != 0 && (flags & ~(VertexStreams | Harmless)) == 0;

        // ------------------------------------------------------------------ IL skeleton

        /// <summary>
        /// FNV-1a 64 of <see cref="Skeleton"/> for the verified <c>MeshAsset/&lt;Upload&gt;d__21.MoveNext</c>
        /// (Renderite.Unity 1.0.0+64bd84d14c68faaddd7347f606b6b830eaae85a7, Resonite 2026.11.6). The offline tests recompute
        /// it from the installed DLL with Cecil and from a reflection decode, so this constant is checked against the real IL.
        /// </summary>
        public const ulong VerifiedSkeletonHash = 0x6945e5eb0eccf8b3UL;

        /// <summary>
        /// One skeleton entry for an instruction, or null when it is not part of the skeleton. The skeleton is every member
        /// reference (fields, methods, constructors) and every int constant, in order: what the method touches and which flag
        /// values it tests. Branches, locals, strings and type tokens are left out, so the same entries come out of Cecil
        /// (offline) and of Harmony's CodeInstructions (System.Reflection operands, at run time).
        /// </summary>
        public static string Entry(string opName, object operand)
        {
            if (opName.StartsWith("ldc.i4", StringComparison.Ordinal))
            {
                if (operand is int i) return "I " + i;
                if (operand is sbyte sb) return "I " + sb;
                if (operand is byte b) return "I " + b;
                string tail = opName.Length > 7 ? opName.Substring(7) : "";   // ldc.i4.N / ldc.i4.m1
                if (tail == "m1" || tail == "M1") return "I -1";
                if (tail.Length == 1 && tail[0] >= '0' && tail[0] <= '8') return "I " + tail;
                return null;
            }
            if (operand is FieldInfo f) return "F " + f.DeclaringType?.Name + "::" + f.Name;
            if (operand is MethodBase m) return "M " + m.DeclaringType?.Name + "::" + m.Name;
            return null;
        }

        /// <summary>The skeleton of a method body given as (opcode name, operand) pairs; members as reflection objects.</summary>
        public static List<string> Skeleton(IEnumerable<KeyValuePair<string, object>> code)
        {
            var list = new List<string>();
            foreach (var kv in code)
            {
                var e = Entry(kv.Key, kv.Value);
                if (e != null) list.Add(e);
            }
            return list;
        }

        /// <summary>The same skeleton from already formatted entries (the Cecil side formats members itself).</summary>
        public static ulong Hash(IEnumerable<string> entries)
        {
            ulong h = 14695981039346656037UL;
            foreach (var e in entries)
            {
                foreach (char c in e) { h ^= c; h *= 1099511628211UL; }
                h ^= '\n'; h *= 1099511628211UL;
            }
            return h;
        }

        public static KeyValuePair<string, object> Op(OpCode op, object operand) => new KeyValuePair<string, object>(op.Name, operand);
    }
}
