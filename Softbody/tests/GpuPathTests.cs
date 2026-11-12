using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;
using Softbody.Core;
using SoftbodyRenderer;

namespace Softbody.Tests;

// Offline tests for the 0.9.0 GPU path (skinned meshes stay GPU-skinned): the engine-side true partial upload
// (Plugin/PartialEncode.cs) on the engine's own MeshBufferGenerator, and its cost on the 60k-vertex body.
// What happens in the renderer (Unity) cannot run offline; RendererCheck checks the patch against the renderer's IL instead.
static partial class Program
{
    static void GpuPathTests()
    {
        try { GpuPartialUpload(); }
        catch (Exception ex) { Check(false, "GPU-path partial upload check ran: " + ex.GetType().Name + ": " + ex.Message); }
        try { GpuPathTiming(); }
        catch (Exception ex) { Check(false, "GPU-path timing ran: " + ex.GetType().Name + ": " + ex.Message); }
    }

    const MeshUploadHint.Flag PN = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals;

    /// <summary>Decodes one float attribute (Float32 x dims) of every vertex from a mesh buffer's vertex region.</summary>
    static float[] DecodeAttribute(MeshBuffer buf, VertexAttributeType type, out int dims)
    {
        int idx = buf.VertexAttributes.FindIndex(a => a.attribute == type);
        if (idx < 0) throw new InvalidOperationException("no " + type + " attribute");
        var a = buf.VertexAttributes[idx];
        if (a.format != VertexAttributeFormat.Float32) throw new InvalidOperationException(type + " is " + a.format);
        dims = a.dimensions;
        int off = buf.VertexAttributeOffset(idx), stride = buf.VertexStride, n = buf.VertexCount;
        var raw = buf.GetRawVertexBufferData();
        var r = new float[n * dims];
        for (int v = 0; v < n; v++)
            for (int d = 0; d < dims; d++)
                r[v * dims + d] = BitConverter.ToSingle(raw.Slice(v * stride + off + 4 * d, 4));
        return r;
    }

    /// <summary>Which vertex attributes own the bytes that differ between two copies of a buffer, and how many differing bytes
    /// lie outside the vertex region (index, bone, bind pose and blendshape data).</summary>
    static (HashSet<VertexAttributeType> attrs, int outside) DiffColumns(MeshBuffer buf, byte[] before, ReadOnlySpan<byte> after)
    {
        var attrs = new HashSet<VertexAttributeType>(); int outside = 0;
        int vbytes = buf.VertexStride * buf.VertexCount;
        var starts = Enumerable.Range(0, buf.VertexAttributeCount).Select(buf.VertexAttributeOffset).ToArray();
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == after[i]) continue;
            if (i >= vbytes) { outside++; continue; }
            int inV = i % buf.VertexStride, k = starts.Length - 1;
            while (k > 0 && starts[k] > inV) k--;
            attrs.Add(buf.VertexAttributes[k].attribute);
        }
        return (attrs, outside);
    }

    static bool SameDefinition(MeshUploadData a, MeshUploadData b) =>
        a.vertexCount == b.vertexCount && a.boneCount == b.boneCount && a.boneWeightCount == b.boneWeightCount && a.indexBufferFormat == b.indexBufferFormat &&
        a.vertexAttributes.SequenceEqual(b.vertexAttributes) && a.submeshes.SequenceEqual(b.submeshes) && a.blendshapeBuffers.SequenceEqual(b.blendshapeBuffers);

    static bool SameSkin(MeshX a, MeshX b, out string what)
    {
        what = null;
        if (a.BoneCount != b.BoneCount || a.HasBoneBindings != b.HasBoneBindings || a.BlendShapeCount != b.BlendShapeCount) { what = "counts"; return false; }
        for (int i = 0; i < a.BoneCount; i++)
            if (!a.GetBone(i).BindPose.Equals(b.GetBone(i).BindPose) || a.GetBone(i).Name != b.GetBone(i).Name) { what = "bone " + i; return false; }
        for (int v = 0; v < a.VertexCount; v++)
        {
            var x = a.RawBoneBindings[v]; var y = b.RawBoneBindings[v];
            if (x.boneIndex0 != y.boneIndex0 || x.boneIndex1 != y.boneIndex1 || x.boneIndex2 != y.boneIndex2 || x.boneIndex3 != y.boneIndex3 ||
                x.weight0 != y.weight0 || x.weight1 != y.weight1 || x.weight2 != y.weight2 || x.weight3 != y.weight3) { what = "binding " + v; return false; }
        }
        for (int s = 0; s < a.BlendShapeCount; s++)
        {
            var sa = a.GetBlendShape(s); var sb = b.GetBlendShape(s);
            if (sa.Name != sb.Name || sa.FrameCount != sb.FrameCount) { what = "blendshape " + s; return false; }
            for (int f = 0; f < sa.FrameCount; f++)
                for (int v = 0; v < a.VertexCount; v++)
                    if (!sa[f].GetPositionDelta(v).Equals(sb[f].GetPositionDelta(v)) || !sa[f].GetNormalDelta(v).Equals(sb[f].GetNormalDelta(v)))
                    { what = $"blendshape {s} frame {f} vertex {v}"; return false; }
        }
        return true;
    }

    /// <summary>
    /// The GPU path's upload on a skinned mesh, end to end on the engine side: the asset as the engine loaded it (a full
    /// skinned encode), the plugin's private copy with all its skin data, bind-space wobble written into it, then
    /// PartialEncode (FillVertexData on the asset's buffer + FillMeshBufferDefinition): the hint, the buffer and the message.
    /// </summary>
    static void GpuPartialUpload()
    {
        Console.WriteLine("TEST GPU path, engine side (offline, engine encoder): a true partial upload on a skinned mesh, skin data kept");
        var m = Arm();
        int n = m.VertexCount;
        var original = SkinnedMeshX(m, 3);
        original.HasTangents = true; original.SetHasUV(0, true);
        for (int v = 0; v < n; v++) { original.RawTangents[v] = new float4(1, 0, 0, 1); original.RawUV0s[v] = new float2(m.Pos[v].X, m.Pos[v].Y); }
        var meta = MeshMetadata.GenerateMetadata(original);
        var alloc = new KeepAlloc();
        var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));
        // 1. what the asset holds after the engine loaded it: a full encode with skin data (no Dynamic: an asset load)
        var loadHint = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref loadHint, original);
        var buf = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(original, buf, alloc, ColorProfile.Linear, ref loadHint, meta, bounds, null);
        var loaded = buf.RawBuffer.ToArray();
        var loadDef = PartialEncode.Describe(buf, 7, false, loadHint, bounds);
        Console.WriteLine($"  asset as loaded: {n} verts, stride {buf.VertexStride} B ({string.Join(", ", buf.VertexAttributes.Select(a => $"{a.attribute} {a.format}x{a.dimensions}"))}), " +
                          $"{buf.BoneCount} bones, {buf.BlendshapeBufferCount} blendshape buffers, buffer {buf.TotalBufferLength} B; load hint skin data {loadHint[MeshUploadHint.Flag.BindPoses]}");

        // 2. the private copy keeps everything; the bent, wobbling arm writes rest + bind-space offset into it
        var work = new MeshX(original);
        var skin = m.Skin();
        using var rig = ArmRig(m, ArmSettings(), ArmPose(Matrix4x4.Identity, 0f), skin);
        Matrix4x4[] pose = null;
        for (int f = 0; f <= (int)MathF.Round(0.45f / Dt); f++) { pose = ArmPose(Matrix4x4.Identity, MathF.PI / 2 * Smoothstep(0f, 0.3f, f * Dt)); rig.StepSkinned(Dt, pose); }
        var wp = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n));
        var wn = MemoryMarshal.Cast<float3, Vector3>(work.RawNormals.AsSpan(0, n));
        rig.UpdateCageSkinned(out _);
        rig.ApplyToMesh(wp, wn, out var bmin, out var bmax);
        // the same field from the cage's bind-space offsets, through an independent deformer
        var offsets = new Vector3[rig.Cage.PointCount]; rig.Sim.GetBindOffsets(offsets);
        var def = new MeshDeformer(rig.Cage, m.Pos, m.Nrm, m.Tri);
        def.SetDisplacements(offsets, out _);
        var expPos = (Vector3[])m.Pos.Clone(); var expNrm = (Vector3[])m.Nrm.Clone();
        def.ApplyToMesh(expPos, expNrm, out _, out _);
        int moved = 0; float maxOff = 0;
        for (int v = 0; v < n; v++) { float d = Vector3.Distance(expPos[v], m.Pos[v]); if (d > 0) moved++; maxOff = MathF.Max(maxOff, d); }

        // 3. the GPU path's upload
        var hint = new MeshUploadHint(PN);
        string why = PartialEncode.CheckLayout(work, buf, hint);
        var sw = Stopwatch.StartNew();
        PartialEncode.Encode(work, buf, hint);
        double encMs = sw.Elapsed.TotalMilliseconds;
        var u = PartialEncode.Describe(buf, 7, true, hint, BoundingBox.FromPoints(F3(bmin), F3(bmax)));
        var (cols, outside) = DiffColumns(buf, loaded, buf.RawBuffer);
        var decP = DecodeAttribute(buf, VertexAttributeType.Position, out _);
        var decN = DecodeAttribute(buf, VertexAttributeType.Normal, out _);
        bool posExact = true, nrmExact = true;
        for (int v = 0; v < n && posExact; v++)
            posExact = decP[3 * v] == expPos[v].X && decP[3 * v + 1] == expPos[v].Y && decP[3 * v + 2] == expPos[v].Z;
        for (int v = 0; v < n && nrmExact; v++)
            nrmExact = decN[3 * v] == expNrm[v].X && decN[3 * v + 1] == expNrm[v].Y && decN[3 * v + 2] == expNrm[v].Z;
        // a full encode of the copy with the asset's own load hint: the whole vertex region must be byte-identical
        var refHint = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref refHint, work);
        var refBuf = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(work, refBuf, alloc, ColorProfile.Linear, ref refHint, MeshMetadata.GenerateMetadata(work), bounds, null);
        int vbytes = buf.VertexStride * n;
        bool sameAsFull = refBuf.VertexStride == buf.VertexStride && buf.RawBuffer.Slice(0, vbytes).SequenceEqual(refBuf.RawBuffer.Slice(0, vbytes));
        // what the GPU then shows: skin(bind-space buffer positions) with this pose = the CPU path's skin(rest + offset)
        var bindDecoded = new Vector3[n]; for (int v = 0; v < n; v++) bindDecoded[v] = new Vector3(decP[3 * v], decP[3 * v + 1], decP[3 * v + 2]);
        var gpuShown = new Vector3[n]; skin.SkinPositions(pose, bindDecoded, gpuShown);
        var cpuShown = new Vector3[n]; skin.SkinPositions(pose, expPos, cpuShown);
        float shownDiff = MaxDiff(gpuShown, cpuShown);

        Console.WriteLine($"  bind-space wobble: {moved} of {n} vertices moved, up to {maxOff * 1000:0.0} mm; P|N partial encode {encMs:0.000} ms; CheckLayout: {why ?? "ok"}");
        Console.WriteLine($"  sent hint {u.uploadHint} (renderer-side IsVertexOnly {UploadPatchSpec.IsVertexOnly(u.uploadHint.Flags)}); bytes changed in: " +
                          $"{string.Join(", ", cols.OrderBy(c => c))}; outside the vertex region: {outside}; vertex region equals a full encode of the copy: {sameAsFull}");
        Check(why == null && u.uploadHint.Flags == PN && UploadPatchSpec.IsVertexOnly(u.uploadHint.Flags) && (u.uploadHint.Flags & UploadPatchSpec.Structural) == 0,
              "GPU path: the skinned mesh's partial upload hint is exactly Positions|Normals (no VertexLayout, Geometry, SubmeshLayout, BindPoses, BoneWeights or Blendshapes)");
        Check(cols.SetEquals(new[] { VertexAttributeType.Position, VertexAttributeType.Normal }) && outside == 0,
              "GPU path: only the position and normal bytes of the buffer change; tangents, colours, UVs, indices, bones, bind poses and blendshape data stay as the asset loaded them");
        Check(posExact && nrmExact && moved > 0,
              "GPU path: the encoded positions are exactly rest + bind-space offset (the cage's bind offsets through the spline field), the normals the deformer's (bit-exact)");
        Check(sameAsFull, "GPU path: the whole vertex region equals a full encode of the copy (nothing stale, nothing else touched)");
        Check(!buf.VertexAttributes.Any(a => a.attribute is VertexAttributeType.BoneWeights or VertexAttributeType.BoneIndicies) && buf.BoneCount > 0 && buf.BoneWeightsBufferLength > 0,
              "the engine never puts bone weights in the vertex layout (they travel in the buffer's separate bone region, which the renderer hands to SetBoneWeights)");
        Check(SameDefinition(u, loadDef), "GPU path: the upload message describes the same buffer as the full skinned upload (vertex attributes, submeshes, index format, bone counts, blendshape buffers)");
        Check(SameSkin(work, original, out var skinWhat), "GPU path: the private MeshX copy keeps all of its skin data (bones, bind poses, bone bindings, blendshape frames) unchanged" +
              (skinWhat != null ? " - differs: " + skinWhat : ""));
        Check(shownDiff == 0f, $"GPU path: skinning the uploaded bind-space positions gives exactly the CPU path's shown mesh (max diff {shownDiff:E1} m)");

        // why the engine's own SetFromMeshX cannot do this: its encoder widens the same hint on a mesh with bones
        var engineHint = new MeshUploadHint(PN);
        var engineBuf = new MeshBuffer();
        var eh0 = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref eh0, original);
        MeshBufferGenerator.UpdateMeshBufferData(original, engineBuf, alloc, ColorProfile.Linear, ref eh0, meta, bounds, null);
        MeshBufferGenerator.UpdateMeshBufferData(work, engineBuf, alloc, ColorProfile.Linear, ref engineHint, meta, bounds, null);
        Console.WriteLine($"  for comparison, Mesh.SetFromMeshX's encoder turns the same P|N hint into: {engineHint} (renderer-side IsVertexOnly {UploadPatchSpec.IsVertexOnly(engineHint.Flags)})");
        Check(engineHint[MeshUploadHint.Flag.VertexLayout] && !UploadPatchSpec.IsVertexOnly(engineHint.Flags),
              "and the engine's own encoder (Mesh.SetFromMeshX) widens it to VertexLayout + every stream, so the plugin encodes the partial itself");

        // 4. paint: colours change -> Positions|Normals|Colors, still only those columns
        var before2 = buf.RawBuffer.ToArray();
        for (int v = 0; v < n; v += 3) work.RawColors[v] = new color(0.1f, 0.9f, 0.3f, 1f);
        var hc = new MeshUploadHint(PN | MeshUploadHint.Flag.Colors);
        string whyC = PartialEncode.CheckLayout(work, buf, hc);
        PartialEncode.Encode(work, buf, hc);
        var (cols2, outside2) = DiffColumns(buf, before2, buf.RawBuffer);
        var refHint2 = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref refHint2, work);
        var refBuf2 = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(work, refBuf2, alloc, ColorProfile.Linear, ref refHint2, MeshMetadata.GenerateMetadata(work), bounds, null);
        bool same2 = buf.RawBuffer.Slice(0, vbytes).SequenceEqual(refBuf2.RawBuffer.Slice(0, vbytes));
        Console.WriteLine($"  paint: P|N|C partial changes {string.Join(", ", cols2.OrderBy(c => c))} ({outside2} bytes outside the vertex region); equals a full encode: {same2}");
        Check(whyC == null && UploadPatchSpec.IsVertexOnly(hc.Flags) && outside2 == 0 && cols2.Contains(VertexAttributeType.Color) &&
              !cols2.Contains(VertexAttributeType.Tangent) && !cols2.Contains(VertexAttributeType.UV0) && same2,
              "GPU path with paint: Positions|Normals|Colors re-encodes the colours too, nothing else, and still equals a full encode");

        // 5. a copy that gained a vertex stream (StartAs / paint on a mesh without colours): needs one full upload
        var bare = SkinnedMeshX(m, 3, colors: false);
        var bareHint = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref bareHint, bare);
        var bareBuf = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(bare, bareBuf, alloc, ColorProfile.Linear, ref bareHint, MeshMetadata.GenerateMetadata(bare), bounds, null);
        var grown = new MeshX(bare); grown.HasColors = true;
        string whyGrown = PartialEncode.CheckLayout(grown, bareBuf, new MeshUploadHint(PN));
        string whyWide = PartialEncode.CheckLayout(work, buf, new MeshUploadHint(PN | MeshUploadHint.Flag.VertexLayout));
        string whyCount = PartialEncode.CheckLayout(SkinnedMeshX(Arm(30, 16), 3), buf, new MeshUploadHint(PN));
        Console.WriteLine($"  layout checks: copy gained colours -> \"{whyGrown}\"; a VertexLayout hint -> \"{whyWide}\"; another vertex count -> \"{whyCount}\"");
        Check(whyGrown != null && whyWide != null && whyCount != null,
              "GPU path: a copy whose layout grew (colours added), a structural hint or a vertex-count change is refused (the plugin sends a full upload instead)");

        // 6. the in-place restore: the original's streams back -> the buffer is exactly what the engine loaded
        var hr = new MeshUploadHint(PN | MeshUploadHint.Flag.Colors);
        PartialEncode.Encode(original, buf, hr);
        bool restored = buf.RawBuffer.SequenceEqual(loaded);
        Check(PartialEncode.CheckLayout(original, buf, hr) == null && restored, "GPU path restore: re-encoding the original's Positions|Normals|Colors in place gives back the loaded buffer byte for byte");

        // 6b. 0.26.3: the skinned softbody also sends tangents (turned with its normals): Positions|Normals|Tangents in place
        for (int v = 0; v < n; v += 2) { var t = work.RawTangents[v]; work.RawTangents[v] = new float4(t.y, -t.x, t.z, t.w); }
        var ht = new MeshUploadHint(PN | MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.Colors);
        string whyT = PartialEncode.CheckLayout(work, buf, ht);
        var before3 = buf.RawBuffer.ToArray();
        PartialEncode.Encode(work, buf, ht);
        var (cols3, outside3) = DiffColumns(buf, before3, buf.RawBuffer);
        var refHint3 = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref refHint3, work);
        var refBuf3 = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(work, refBuf3, alloc, ColorProfile.Linear, ref refHint3, MeshMetadata.GenerateMetadata(work), bounds, null);
        bool same3 = buf.RawBuffer.Slice(0, vbytes).SequenceEqual(refBuf3.RawBuffer.Slice(0, vbytes));
        Console.WriteLine($"  tangents too (0.26.3): P|N|T|C partial changes {string.Join(", ", cols3.OrderBy(c => c))} ({outside3} bytes outside the vertex region); equals a full encode: {same3}");
        Check(whyT == null && UploadPatchSpec.IsVertexOnly(ht.Flags) && outside3 == 0 && cols3.Contains(VertexAttributeType.Tangent) && !cols3.Contains(VertexAttributeType.UV0) && same3,
              "GPU path with tangents (the skinned softbody since 0.26.3): Positions|Normals|Tangents re-encodes the tangents in place, nothing else, and still equals a full encode");
        var hr2 = new MeshUploadHint(PN | MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.Colors);
        PartialEncode.Encode(original, buf, hr2);
        Check(buf.RawBuffer.SequenceEqual(loaded), "GPU path restore with tangents: re-encoding the original's Positions|Normals|Tangents|Colors in place gives back the loaded buffer byte for byte");

        // 7. the renderer-side filter and the handshake name the two plugins share
        var full = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref full, original);
        var cases = new (MeshUploadHint.Flag f, bool expect)[]
        {
            (PN, true), (PN | MeshUploadHint.Flag.Dynamic, true), (MeshUploadHint.Flag.Colors | MeshUploadHint.Flag.Readable, true), (PN | MeshUploadHint.Flag.Tangents | MeshUploadHint.Flag.UV0s, true),
            (engineHint.Flags, false), (full.Flags, false), (PN | MeshUploadHint.Flag.BoneWeights, false), (PN | MeshUploadHint.Flag.Blendshapes, false),
            (PN | MeshUploadHint.Flag.BindPoses, false), (PN | MeshUploadHint.Flag.Geometry, false), (PN | MeshUploadHint.Flag.SubmeshLayout, false), (MeshUploadHint.Flag.Dynamic, false), (0, false),
        };
        var bad = cases.Where(c => UploadPatchSpec.IsVertexOnly(c.f) != c.expect).Select(c => c.f.ToString()).ToList();
        Check(bad.Count == 0, "renderer patch filter (UploadPatchSpec.IsVertexOnly): in place only for vertex streams (+Dynamic/Readable); any layout, geometry or skin flag takes the stock path" +
              (bad.Count > 0 ? " - wrong for: " + string.Join("; ", bad) : ""));
        Check(PartialEncode.HandshakeName == UploadPatchSpec.HandshakeName, "the engine plugin looks for the same handshake event the renderer plugin opens (" + UploadPatchSpec.HandshakeName + ")");
    }

    /// <summary>The GPU path's per-frame engine-side cost on the 60k-vertex body (cage, bind-space deform of the soft vertices,
    /// P|N encode into the skinned asset's buffer), next to the CPU path's skinning of every vertex; and no uploads while the
    /// body is carried at constant speed (the GPU follows the pose by itself).</summary>
    static void GpuPathTiming()
    {
        Console.WriteLine("TEST GPU path timing (Release): ~60k-vertex body, 7 bones, soft left forearm, walking with swinging arms");
        var m = Body(); var skin = m.Skin();
        int n = m.VertexCount;
        var original = SkinnedMeshX(m, 2);
        var meta = MeshMetadata.GenerateMetadata(original);
        var ebounds = BoundingBox.FromPoints(new float3(-1, 0, -1), new float3(1, 2, 1));
        var alloc = new KeepAlloc(); var buf = new MeshBuffer();
        var load = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref load, original);
        MeshBufferGenerator.UpdateMeshBufferData(original, buf, alloc, ColorProfile.Linear, ref load, meta, ebounds, null);
        var work = new MeshX(original);
        var wp = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n)).ToArray();
        var wn = MemoryMarshal.Cast<float3, Vector3>(work.RawNormals.AsSpan(0, n)).ToArray();
        var pose = new Matrix4x4[8];
        BodyPose(0f, pose);
        var st = new SoftbodySettings { Cells = 16, Gravity = 0.3f, Collide = false };
        using var rig = SoftbodyRig.CreateSkinned(m.Pos, m.Nrm, m.Tri, m.Soft, st, skin, pose, AnchorPose.Identity);
        var cpuPos = new Vector3[n]; var cpuNrm = new Vector3[n];
        var stepT = new List<double>(); var defT = new List<double>(); var encT = new List<double>(); var totT = new List<double>(); var cpuT = new List<double>();
        var sw = new Stopwatch();
        var hint = new MeshUploadHint(PN);
        const int fps = 60; float dt = 1f / fps;
        for (int f = 0; f < 6 * fps; f++)
        {
            BodyPose(f * dt, pose);
            sw.Restart(); rig.StepSkinned(dt, pose); double a = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); rig.UpdateCageSkinned(out _); rig.ApplyToMesh(wp, wn, out _, out _); rig.CommitSnapshot(); double b = sw.Elapsed.TotalMilliseconds;
            wp.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n)));   // (the plugin deforms in the copy directly: not timed)
            wn.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(work.RawNormals.AsSpan(0, n)));
            sw.Restart(); PartialEncode.Encode(work, buf, hint); double e = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); skin.SkinMesh(pose, wp, wn, null, cpuPos, cpuNrm, null, parallel: true, out _, out _); double c = sw.Elapsed.TotalMilliseconds;
            if (f >= 2 * fps) { stepT.Add(a); defT.Add(b); encT.Add(e); totT.Add(a + b + e); cpuT.Add(a + b + c + e); }
        }
        static double P95(List<double> l) { var c = l.OrderBy(x => x).ToList(); return c[(int)(c.Count * 0.95)]; }
        Console.WriteLine($"  {n} vertices, {rig.Deformer.DynamicVertexCount} soft; per frame: step {stepT.Average():0.000} ms, bind-space deform {defT.Average():0.000} ms, " +
                          $"P|N encode into the skinned buffer {encT.Average():0.000} ms -> GPU path {totT.Average():0.000} ms (p95 {P95(totT):0.000}); " +
                          $"the CPU path also skins all {n} vertices: {cpuT.Average():0.000} ms (+ blendshapes, + its own encode)");
        Check(totT.Average() < cpuT.Average() && totT.Average() < 2.0,
              $"GPU path: {totT.Average():0.00} ms per frame on the 60k-vertex body, no CPU skinning of the whole mesh (CPU path {cpuT.Average():0.00} ms)");

        // 0.26.3: the skinned softbody also sends tangents; what that adds to the engine-side encode on this body (printed only)
        var tanMesh = new MeshX(original); tanMesh.HasTangents = true;
        for (int v = 0; v < n; v++) tanMesh.RawTangents[v] = new float4(1, 0, 0, 1);
        var tbuf = new MeshBuffer(); var tload = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref tload, tanMesh);
        MeshBufferGenerator.UpdateMeshBufferData(tanMesh, tbuf, alloc, ColorProfile.Linear, ref tload, MeshMetadata.GenerateMetadata(tanMesh), ebounds, null);
        var pnT = new List<double>(); var pntT = new List<double>();
        var hPN = new MeshUploadHint(PN); var hPNT = new MeshUploadHint(PN | MeshUploadHint.Flag.Tangents);
        for (int r = 0; r < 140; r++)
        {
            sw.Restart(); PartialEncode.Encode(tanMesh, tbuf, hPN); double x1 = sw.Elapsed.TotalMilliseconds;
            sw.Restart(); PartialEncode.Encode(tanMesh, tbuf, hPNT); double x2 = sw.Elapsed.TotalMilliseconds;
            if (r >= 20) { pnT.Add(x1); pntT.Add(x2); }
        }
        Console.WriteLine($"  on a copy with tangents ({tbuf.VertexStride} B a vertex): P|N encode {pnT.Average():0.000} ms, P|N|T (the skinned softbody since 0.26.3) {pntT.Average():0.000} ms a frame");

        // carried at constant speed: the bind-space wobble settles, so the GPU path stops uploading while the pose keeps changing
        var rp = new Matrix4x4[8];
        for (int k = 0; k < 8; k++) rp[k] = Matrix4x4.Identity;
        using var rig2 = SoftbodyRig.CreateSkinned(m.Pos, m.Nrm, m.Tri, m.Soft, st, skin, rp, AnchorPose.Identity);
        int uploadsLastSecond = 0; float maxChangeLast = 0, maxOffLast = 0;
        var perSecond = new List<int>(); int thisSecond = 0;
        for (int f = 0; f < 8 * fps; f++)
        {
            var g = Matrix4x4.CreateTranslation(0, 0, 1.2f * f * dt);
            for (int k = 0; k < 8; k++) rp[k] = g;
            rig2.StepSkinned(dt, rp);
            float off = rig2.UpdateCageSkinned(out float change);
            if (change >= 2e-5f) { rig2.CommitSnapshot(); thisSecond++; if (f >= 7 * fps) uploadsLastSecond++; }
            if (f >= 7 * fps) { maxChangeLast = MathF.Max(maxChangeLast, change); maxOffLast = MathF.Max(maxOffLast, off); }
            if (f % fps == fps - 1) { perSecond.Add(thisSecond); thisSecond = 0; }
        }
        Console.WriteLine($"  carried at 1.2 m/s for 8 s (the pose changes every frame): GPU-path uploads per second {string.Join(" ", perSecond)} " +
                          $"(the jelly settles into a steady {maxOffLast * 1000:0.0} mm lag; last second's largest change {maxChangeLast * 1000:0.0000} mm); the CPU path re-skins and uploads every frame");
        Check(uploadsLastSecond <= fps / 10, "GPU path: once the jelly has settled, moving the whole body uploads nothing (the renderer follows the bones)");
    }
}
