using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core.Pop;

namespace Softbody;

/// <summary>
/// 0.34.0: the mesh of a pop's pieces. <see cref="PopDebris"/> puts it on a local slot (never synced or saved) and hands
/// it the cut once (every channel and submesh, sent in full the first time) and then the pieces' places as they fly (only
/// positions, normals and tangents go up after that). Built on the engine's background mesh thread like any procedural
/// mesh. Attached by hand it shows nothing.
/// </summary>
[Category(new[] { "Physics/Softbody" })]
public class PopDebrisMesh : ProceduralMesh
{
    readonly object _lock = new();
    PopPieces _cut;
    int[] _uvDims = Array.Empty<int>();
    Vector3[] _pos = Array.Empty<Vector3>(), _nrm = Array.Empty<Vector3>();
    Vector4[] _tan = Array.Empty<Vector4>();
    bool _laidOut;
    volatile int _builds;

    /// <summary>How many times the mesh was built (the first in full).</summary>
    internal int Builds => _builds;

    /// <summary>World thread: the pieces it shows (their layout, and where they are now).</summary>
    internal void Setup(PopPieces cut, int[] uvDims, ColorProfile profile)
    {
        lock (_lock)
        {
            _cut = cut;
            _uvDims = uvDims ?? Array.Empty<int>();
            _pos = (Vector3[])cut.Positions.Clone();
            _nrm = (Vector3[])cut.Normals.Clone();
            _tan = (Vector4[])cut.Tangents.Clone();
            _laidOut = false;
        }
        try { if (Profile.Value != profile) Profile.Value = profile; } catch { }
        MarkChangeDirty();
    }

    /// <summary>World thread: where the pieces are now (the solver's mesh as it shows).</summary>
    internal void Submit(Vector3[] pos, Vector3[] nrm, Vector4[] tan)
    {
        lock (_lock)
        {
            if (_cut == null) return;
            int n = Math.Min(_pos.Length, pos.Length);
            Array.Copy(pos, _pos, n);
            Array.Copy(nrm, _nrm, Math.Min(n, nrm.Length));
            Array.Copy(tan, _tan, Math.Min(n, tan.Length));
        }
        MarkChangeDirty();
    }

    protected override void PrepareAssetUpdateData() { }

    protected override void ClearMeshData()
    {
        lock (_lock) _laidOut = false;
    }

    protected override void UpdateMeshData(MeshX meshx)
    {
        lock (_lock)
        {
            var cut = _cut;
            int n = cut?.Positions.Length ?? 0;
            if (cut == null || n == 0)
            {
                // nothing to show: one invisible degenerate triangle keeps the mesh valid
                meshx.Clear();
                meshx.HasNormals = true;
                var t = meshx.AddSubmesh<TriangleSubmesh>();
                meshx.SetVertexCount(3);
                for (int i = 0; i < 3; i++) { meshx.RawPositions[i] = float3.Zero; meshx.RawNormals[i] = float3.Up; }
                t.SetCount(1);
                t.RawIndicies[0] = 0; t.RawIndicies[1] = 0; t.RawIndicies[2] = 0;
                _laidOut = false;
                Full(0, false, false);
                return;
            }
            bool layout = !_laidOut || meshx.VertexCount != n;
            if (layout)
            {
                meshx.Clear();
                meshx.HasNormals = true;
                meshx.HasTangents = cut.HasTangents;
                meshx.HasColors = cut.Colors != null;
                for (int c = 0; c < cut.Uvs.Count && c < _uvDims.Length; c++)
                {
                    switch (_uvDims[c])
                    {
                        case 3: meshx.SetHasUV_3D(c, true, false); break;
                        case 4: meshx.SetHasUV_4D(c, true, false); break;
                        default: meshx.SetHasUV(c, true, false); break;
                    }
                }
                meshx.SetVertexCount(n);
                if (cut.Colors != null) cut.Colors.AsSpan(0, n).CopyTo(MemoryMarshal.Cast<color, Vector4>(meshx.RawColors.AsSpan(0, n)));
                for (int c = 0; c < cut.Uvs.Count && c < _uvDims.Length; c++)
                {
                    var src = cut.Uvs[c];
                    switch (_uvDims[c])
                    {
                        case 3: { var a = meshx.GetRawUVs_3D(c); for (int v = 0; v < n; v++) a[v] = new float3(src[v].X, src[v].Y, src[v].Z); break; }
                        case 4: { var a = meshx.GetRawUVs_4D(c); for (int v = 0; v < n; v++) a[v] = new float4(src[v].X, src[v].Y, src[v].Z, src[v].W); break; }
                        default: { var a = meshx.GetRawUVs(c); for (int v = 0; v < n; v++) a[v] = new float2(src[v].X, src[v].Y); break; }
                    }
                }
                foreach (var list in cut.Submeshes)
                {
                    var ts = meshx.AddSubmesh<TriangleSubmesh>();
                    if (list.Length < 3)
                    {
                        // a material whose submesh got no pieces: one degenerate triangle keeps the materials in line
                        ts.SetCount(1);
                        ts.RawIndicies[0] = 0; ts.RawIndicies[1] = 0; ts.RawIndicies[2] = 0;
                        continue;
                    }
                    ts.SetCount(list.Length / 3);
                    list.AsSpan(0, list.Length - list.Length % 3).CopyTo(ts.RawIndicies.AsSpan());
                }
                _laidOut = true;
            }
            _pos.AsSpan(0, n).CopyTo(MemoryMarshal.Cast<float3, Vector3>(meshx.RawPositions.AsSpan(0, n)));
            _nrm.AsSpan(0, n).CopyTo(MemoryMarshal.Cast<float3, Vector3>(meshx.RawNormals.AsSpan(0, n)));
            if (cut.HasTangents) _tan.AsSpan(0, n).CopyTo(MemoryMarshal.Cast<float4, Vector4>(meshx.RawTangents.AsSpan(0, n)));
            if (layout) Full(Math.Min(cut.Uvs.Count, _uvDims.Length), cut.HasTangents, cut.Colors != null);
            else
            {
                // the pieces moved: their places only (every other channel and the triangles stay as they went up)
                uploadHint[MeshUploadHint.Flag.Positions] = true;
                uploadHint[MeshUploadHint.Flag.Normals] = true;
                uploadHint[MeshUploadHint.Flag.Tangents] = cut.HasTangents;
                uploadHint[MeshUploadHint.Flag.Dynamic] = true;
            }
        }
        _builds++;
    }

    /// <summary>A whole new mesh: layout, triangles and every channel.</summary>
    void Full(int uvs, bool tangents, bool colors)
    {
        uploadHint[MeshUploadHint.Flag.VertexLayout] = true;
        uploadHint[MeshUploadHint.Flag.SubmeshLayout] = true;
        uploadHint[MeshUploadHint.Flag.Geometry] = true;
        uploadHint[MeshUploadHint.Flag.Positions] = true;
        uploadHint[MeshUploadHint.Flag.Normals] = true;
        uploadHint[MeshUploadHint.Flag.Tangents] = tangents;
        uploadHint[MeshUploadHint.Flag.Colors] = colors;
        for (int c = 0; c < uvs; c++) uploadHint[UvFlag(c)] = true;
        uploadHint[MeshUploadHint.Flag.Dynamic] = true;
    }

    static MeshUploadHint.Flag UvFlag(int c) => c switch
    {
        0 => MeshUploadHint.Flag.UV0s,
        1 => MeshUploadHint.Flag.UV1s,
        2 => MeshUploadHint.Flag.UV2s,
        3 => MeshUploadHint.Flag.UV3s,
        4 => MeshUploadHint.Flag.UV4s,
        5 => MeshUploadHint.Flag.UV5s,
        6 => MeshUploadHint.Flag.UV6s,
        _ => MeshUploadHint.Flag.UV7s,
    };
}
