using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core.Fluid;

namespace Softbody;

/// <summary>
/// 0.15.0: the mesh of a world's liquid surface. The fluid runtime creates it on a local slot (never synced, never saved)
/// and hands it each new surface (<see cref="FluidSurface"/>, world space, with the liquids' colours as vertex colours);
/// the mesh is built on the engine's background mesh thread like any procedural mesh. Attached by hand it shows nothing.
/// </summary>
[Category(new[] { "Physics/Softbody" })]
public class FluidSurfaceMesh : ProceduralMesh
{
    readonly object _lock = new();
    Vector3[] _pos = Array.Empty<Vector3>(), _nrm = Array.Empty<Vector3>();
    Vector4[] _col = Array.Empty<Vector4>();
    int[] _idx = Array.Empty<int>();
    int _nv, _ni;
    TriangleSubmesh _triangles;
    volatile int _lastVertices;

    /// <summary>Vertices in the last built mesh.</summary>
    internal int LastVertexCount => _lastVertices;

    /// <summary>World thread: copies a surface and asks for a rebuild.</summary>
    internal void Submit(FluidSurface s)
    {
        lock (_lock)
        {
            int nv = s.VertexCount, ni = s.IndexCount;
            if (_pos.Length < nv) { int cap = Math.Max(nv, _pos.Length * 2); _pos = new Vector3[cap]; _nrm = new Vector3[cap]; _col = new Vector4[cap]; }
            if (_idx.Length < ni) _idx = new int[Math.Max(ni, _idx.Length * 2)];
            Array.Copy(s.Positions, _pos, nv); Array.Copy(s.Normals, _nrm, nv); Array.Copy(s.Colors, _col, nv); Array.Copy(s.Indices, _idx, ni);
            _nv = nv; _ni = ni;
        }
        MarkChangeDirty();
    }

    protected override void PrepareAssetUpdateData() { }

    protected override void ClearMeshData() => _triangles = null;

    protected override void UpdateMeshData(MeshX meshx)
    {
        bool layout = _triangles == null;
        if (layout)
        {
            meshx.Clear();
            meshx.HasNormals = true;
            meshx.HasTangents = false;
            meshx.HasUV0s = false;
            meshx.HasColors = true;
            _triangles = meshx.AddSubmesh<TriangleSubmesh>();
        }
        lock (_lock)
        {
            int nv = _nv, ni = _ni - _ni % 3;
            if (nv == 0 || ni == 0)
            {
                // nothing to show: one invisible degenerate triangle keeps the mesh valid
                meshx.SetVertexCount(3);
                for (int i = 0; i < 3; i++) { meshx.RawPositions[i] = float3.Zero; meshx.RawNormals[i] = float3.Up; meshx.RawColors[i] = new color(0f, 0f, 0f, 0f); }
                _triangles.SetCount(1);
                var idx0 = _triangles.RawIndicies;
                idx0[0] = 0; idx0[1] = 1; idx0[2] = 2;
            }
            else
            {
                meshx.SetVertexCount(nv);
                _pos.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float3, Vector3>(meshx.RawPositions.AsSpan(0, nv)));
                _nrm.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float3, Vector3>(meshx.RawNormals.AsSpan(0, nv)));
                _col.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<color, Vector4>(meshx.RawColors.AsSpan(0, nv)));
                _triangles.SetCount(ni / 3);
                _idx.AsSpan(0, ni).CopyTo(_triangles.RawIndicies.AsSpan(0, ni));
            }
            _lastVertices = nv;
        }
        // a whole new mesh every time (the surface's vertex and triangle counts change)
        uploadHint[MeshUploadHint.Flag.VertexLayout] = layout;
        uploadHint[MeshUploadHint.Flag.SubmeshLayout] = true;
        uploadHint[MeshUploadHint.Flag.Geometry] = true;
        uploadHint[MeshUploadHint.Flag.Positions] = true;
        uploadHint[MeshUploadHint.Flag.Normals] = true;
        uploadHint[MeshUploadHint.Flag.Colors] = true;
    }
}
