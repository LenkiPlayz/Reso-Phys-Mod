using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;

namespace Softbody.Tests;

/// <summary>
/// Times the engine-side half of a mesh upload offline: Elements.Assets.MeshBufferGenerator.UpdateMeshBufferData,
/// the exact call FrooxEngine.Mesh.SetFromMeshX makes before it sends MeshUploadData to the renderer.
/// Compares a full re-encode with the Positions|Normals partial hint the mod uses. Not measured here:
/// the IPC message and the renderer's Mesh.SetVertexBufferData (it re-uploads the whole interleaved
/// vertex buffer, stride x vertexCount bytes, but no index buffer when Geometry is not flagged).
/// </summary>
static class UploadBench
{
    sealed class Alloc : IBackingBufferAllocator
    {
        public IBackingMemoryBuffer Allocate(int size, object owner) => new ArrayBackingMemoryBuffer(new byte[size]);
    }

    public static void Run(Func<int, (Vector3[] pos, Vector3[] nrm, float[] soft, int[] tri)> makeMesh)
    {
        Console.WriteLine("TEST upload encode (engine side, offline): MeshBufferGenerator.UpdateMeshBufferData per frame");
        Console.WriteLine("  verts   layout                       stride  vertex buf  index buf |  full ms   partial P+N ms");
        foreach (var subdiv in new[] { 40, 82 })
            foreach (bool rich in new[] { false, true })
            {
                var (pos, nrm, soft, tri) = makeMesh(subdiv);
                var mx = BuildMeshX(pos, nrm, soft, tri, rich);
                var buffer = new MeshBuffer();
                var alloc = new Alloc();
                var meta = MeshMetadata.GenerateMetadata(mx);
                var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));

                var first = default(MeshUploadHint);
                MeshUploadHintHelper.SetFromMesh(ref first, mx);
                MeshBufferGenerator.UpdateMeshBufferData(mx, buffer, alloc, ColorProfile.Linear, ref first, meta, bounds, null);
                int vbytes = buffer.VertexStride * buffer.VertexCount;

                double Time(bool partial)
                {
                    var sw = new Stopwatch(); var list = new List<double>();
                    var p = MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, mx.VertexCount));
                    for (int f = 0; f < 400; f++)
                    {
                        p[f % p.Length].Y += 1e-4f; // touch the data like a deform would
                        MeshUploadHint h;
                        if (partial) h = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
                        else { h = default; MeshUploadHintHelper.SetFromMesh(ref h, mx); }
                        sw.Restart();
                        MeshBufferGenerator.UpdateMeshBufferData(mx, buffer, alloc, ColorProfile.Linear, ref h, meta, bounds, null);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        if (f >= 100) list.Add(ms);
                        if (partial && f == 399 && (h[MeshUploadHint.Flag.VertexLayout] || h[MeshUploadHint.Flag.Geometry]))
                            Console.WriteLine("  !! partial hint was widened by the encoder: " + h);
                    }
                    return list.Average();
                }
                double full = Time(false), part = Time(true);
                Console.WriteLine($"  {mx.VertexCount,6}  {(rich ? "pos nrm tan col uv0" : "pos nrm col (spawn script)"),-27} {buffer.VertexStride,5}  {vbytes / 1024.0,7:0} KB  {buffer.IndexBufferLength / 1024.0,6:0} KB |  {full,6:0.000}   {part,6:0.000}");
            }
    }

    static MeshX BuildMeshX(Vector3[] pos, Vector3[] nrm, float[] soft, int[] tri, bool rich)
    {
        var mx = new MeshX();
        mx.HasNormals = true;
        mx.HasColors = true;
        if (rich) { mx.HasTangents = true; mx.SetHasUV(0, true); }
        mx.SetVertexCount(pos.Length);
        var P = MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, pos.Length));
        var N = MemoryMarshal.Cast<float3, Vector3>(mx.RawNormals.AsSpan(0, pos.Length));
        pos.CopyTo(P); nrm.CopyTo(N);
        var C = mx.RawColors;
        for (int i = 0; i < pos.Length; i++) C[i] = new color(soft[i], 1 - soft[i], 0.2f, 1f);
        if (rich)
        {
            var T = mx.RawTangents; var U = mx.RawUV0s;
            for (int i = 0; i < pos.Length; i++) { T[i] = new float4(1, 0, 0, 1); U[i] = new float2(pos[i].X, pos[i].Z); }
        }
        var sub = mx.AddSubmesh<TriangleSubmesh>();
        for (int t = 0; t < tri.Length; t += 3) sub.AddTriangle(tri[t], tri[t + 1], tri[t + 2]);
        return mx;
    }
}
