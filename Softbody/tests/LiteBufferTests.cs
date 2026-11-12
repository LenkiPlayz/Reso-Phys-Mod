using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;

namespace Softbody.Tests;

// 0.9.3: a mesh loaded from its file has no mesh buffer left on the engine side (Mesh.LoadTargetVariant frees it once the
// renderer has the mesh). The GPU path then makes its own buffer from a copy without blendshapes and bones (GpuUpload.
// EnsureBuffer). These checks run the same steps offline with the engine's encoder.
static partial class Program
{
    static void LiteBufferTests()
    {
        Console.WriteLine("TEST GPU path on a mesh whose buffer was freed: a vertex-streams buffer from a copy without blendshapes and bones");
        var m = Arm();
        int n = m.VertexCount;
        var original = SkinnedMeshX(m, 3);
        original.HasTangents = true; original.SetHasUV(0, true);
        for (int v = 0; v < n; v++) { original.RawTangents[v] = new float4(1, 0, 0, 1); original.RawUV0s[v] = new float2(m.Pos[v].X, m.Pos[v].Y); }
        var alloc = new KeepAlloc();
        var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));

        // the same steps as GpuUpload.EnsureBuffer
        var lite = new MeshX(original);
        if (lite.BlendShapeCount > 0) lite.ClearBlendShapes();
        if (lite.BoneCount > 0) lite.ClearBones(true);
        lite.HasBoneBindings = false;
        var buffer = new MeshBuffer();
        var hint = default(MeshUploadHint);
        MeshUploadHintHelper.SetFromMesh(ref hint, lite);
        MeshBufferGenerator.UpdateMeshBufferData(lite, buffer, alloc, (ColorProfile)0, ref hint, MeshMetadata.GenerateMetadata(lite), bounds, null);
        Console.WriteLine($"  original: {original.BoneCount} bones, {original.BlendShapeCount} blendshapes; lite buffer: {buffer.VertexCount} verts, stride {buffer.VertexStride} B " +
                          $"({string.Join(", ", buffer.VertexAttributes.Select(a => $"{a.attribute} {a.format}x{a.dimensions}"))}), {buffer.BoneCount} bones, " +
                          $"{buffer.BlendshapeBufferCount} blendshape buffers, {buffer.TotalBufferLength} B");
        Check(original.BoneCount > 0 && original.BlendShapeCount > 0, "the test mesh is skinned and has blendshapes");
        Check(buffer.Data != null && buffer.VertexCount == n && buffer.BoneCount == 0 && buffer.BlendshapeBufferCount == 0,
              "the new buffer holds every vertex, and no bones or blendshape data");

        // the private copy keeps all its skin data; its vertex-only upload must fit the lite buffer
        var work = new MeshX(original);
        var ph = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
        string why = PartialEncode.CheckLayout(work, buffer, ph);
        Check(why == null, "a Positions|Normals upload of the full (skinned) copy fits the lite buffer" + (why != null ? ": " + why : ""));

        // move the copy and encode: the positions in the buffer are exactly the copy's
        var wp = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n));
        for (int v = 0; v < n; v++) wp[v] += new Vector3(0.01f * MathF.Sin(v), 0.02f, 0f);
        PartialEncode.Encode(work, buffer, ph);
        var posAttr = buffer.VertexAttributes.First(a => a.attribute == VertexAttributeType.Position);
        int off = 0;
        foreach (var a in buffer.VertexAttributes) { if (a.attribute == VertexAttributeType.Position) break; off += a.dimensions * (a.format == VertexAttributeFormat.Float32 ? 4 : 2); }
        var raw = buffer.RawBuffer.ToArray();
        float worst = 0;
        for (int v = 0; v < n; v++)
        {
            int at = v * buffer.VertexStride + off;
            var p = new Vector3(BitConverter.ToSingle(raw, at), BitConverter.ToSingle(raw, at + 4), BitConverter.ToSingle(raw, at + 8));
            worst = MathF.Max(worst, Vector3.Distance(p, wp[v]));
        }
        Console.WriteLine($"  positions encoded into the lite buffer: largest difference {worst:E1} (position format {posAttr.format}x{posAttr.dimensions})");
        Check(posAttr.format == VertexAttributeFormat.Float32 && worst == 0f, "the moved positions land in the lite buffer exactly (Float32, as the renderer patch requires)");
        Check(original.BoneCount > 0 && original.BlendShapeCount > 0 && work.BoneCount == original.BoneCount && work.BlendShapeCount == original.BlendShapeCount,
              "the asset's MeshX and the private copy keep their bones and blendshapes (only the throwaway copy was stripped)");
    }
}
