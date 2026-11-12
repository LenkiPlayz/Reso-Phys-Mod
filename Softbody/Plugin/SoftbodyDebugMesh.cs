using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Flesh;

namespace Softbody;

/// <summary>
/// The mesh of a Softbody's debug views (0.10.0). The Softbody component creates it on a local slot (never synced,
/// never saved) when DebugCells or DebugColliders is on, and hands it a snapshot of its simulation a few dozen
/// times per second; the geometry is built on the engine's background mesh thread, like any procedural mesh.
/// SkinnedSoftbody (0.11.0) uses it the same way for its tets and colliders. Attached by hand it shows nothing.
/// </summary>
[Category(new[] { "Physics/Softbody" })]
public class SoftbodyDebugMesh : ProceduralMesh
{
    readonly object _lock = new();
    DebugSnapshot _pending = new(), _building = new();
    // 0.13.0: one snapshot per layer's rig
    List<FleshDebugSnapshot> _fleshPending = new(), _fleshBuilding = new();
    int _fleshPendingCount, _fleshBuildingCount;
    bool _hasPending, _flesh, _fleshBuild;
    readonly DebugView _view = new();
    readonly DebugGeometry _geometry = new();
    TriangleSubmesh _triangles;
    volatile int _lastVertices;
    DebugCounts _lastCounts;

    /// <summary>Vertices in the last built mesh (read on the world thread to pace the updates).</summary>
    internal int LastVertexCount => _lastVertices;

    /// <summary>What the last build drew (written on the mesh thread; a torn read only affects a log line).</summary>
    internal DebugCounts LastCounts { get { lock (_lock) return _lastCounts; } }

    /// <summary>World thread: copies the simulation's current state and asks for a rebuild.</summary>
    internal void Submit(CageSimulation sim, bool cells, bool colliders, bool hasSearch, Vector3 rayOrigin, Vector3 searchCenter, Vector3 searchSize)
    {
        lock (_lock)
        {
            sim.FillDebug(_pending, cells, colliders);
            _pending.HasSearch = hasSearch;
            _pending.RayOrigin = rayOrigin;
            _pending.SearchCenter = searchCenter;
            _pending.SearchSize = searchSize;
            _hasPending = true;
            _flesh = false;
        }
        MarkChangeDirty(); // -> OnChanges -> the procedural asset update (coalesced while one is running)
    }

    /// <summary>World thread, between two flesh steps: copies the flesh rig's state (tets, spheres, colliders).</summary>
    internal void SubmitFlesh(IReadOnlyList<FleshRig> rigs, bool tets, bool colliders, bool hasSearch, Vector3 rayOrigin, Vector3 searchCenter, Vector3 searchSize,
                              bool hasGroundHit, Vector3 groundHit)
    {
        lock (_lock)
        {
            int count = 0;
            foreach (var rig in rigs)
            {
                if (rig == null) continue;
                if (_fleshPending.Count <= count) _fleshPending.Add(new FleshDebugSnapshot());
                var snap = _fleshPending[count++];
                rig.FillDebug(snap, tets, colliders);
                snap.HasSearch = hasSearch;
                snap.RayOrigin = rayOrigin;
                snap.SearchCenter = searchCenter;
                snap.SearchSize = searchSize;
                snap.HasGroundHit = hasGroundHit;
                snap.GroundHit = groundHit;
            }
            _fleshPendingCount = count;
            _hasPending = true;
            _flesh = true;
        }
        MarkChangeDirty();
    }

    protected override void PrepareAssetUpdateData() { }

    protected override void ClearMeshData() => _triangles = null;

    protected override void UpdateMeshData(MeshX meshx)
    {
        lock (_lock)
        {
            if (_hasPending)
            {
                if (_flesh) { (_fleshPending, _fleshBuilding) = (_fleshBuilding, _fleshPending); _fleshBuildingCount = _fleshPendingCount; }
                else (_pending, _building) = (_building, _pending);
                _fleshBuild = _flesh;
                _hasPending = false;
            }
        }
        DebugCounts counts;
        try { counts = _fleshBuild ? FleshDebugView.Build(_fleshBuilding, _fleshBuildingCount, _geometry) : _view.Build(_building, _geometry); }
        catch { _geometry.Clear(); counts = default; }

        bool layout = _triangles == null;
        if (layout)
        {
            meshx.Clear();
            meshx.HasNormals = false;
            meshx.HasTangents = false;
            meshx.HasUV0s = false;
            meshx.HasColors = true;
            _triangles = meshx.AddSubmesh<TriangleSubmesh>();
        }

        int nv = _geometry.VertexCount, ni = _geometry.IndexCount;
        if (nv == 0)
        {
            // nothing to show: one invisible degenerate triangle keeps the mesh valid
            meshx.SetVertexCount(3);
            for (int i = 0; i < 3; i++) { meshx.RawPositions[i] = float3.Zero; meshx.RawColors[i] = new color(0f, 0f, 0f, 0f); }
            _triangles.SetCount(1);
            var idx0 = _triangles.RawIndicies;
            idx0[0] = 0; idx0[1] = 1; idx0[2] = 2;
        }
        else
        {
            meshx.SetVertexCount(nv);
            _geometry.Positions.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float3, Vector3>(meshx.RawPositions.AsSpan(0, nv)));
            _geometry.Colors.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<color, Vector4>(meshx.RawColors.AsSpan(0, nv)));
            _triangles.SetCount(ni / 3);
            _geometry.Indices.AsSpan(0, ni).CopyTo(_triangles.RawIndicies.AsSpan(0, ni));
        }

        // a whole new mesh every time (vertex and triangle counts change with what is shown)
        uploadHint[MeshUploadHint.Flag.VertexLayout] = layout;
        uploadHint[MeshUploadHint.Flag.SubmeshLayout] = true;
        uploadHint[MeshUploadHint.Flag.Geometry] = true;
        uploadHint[MeshUploadHint.Flag.Positions] = true;
        uploadHint[MeshUploadHint.Flag.Colors] = true;

        _lastVertices = nv;
        lock (_lock) _lastCounts = counts;
    }
}
