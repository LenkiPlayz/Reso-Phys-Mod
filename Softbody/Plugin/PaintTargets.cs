using System.Numerics;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody;

/// <summary>A component the <see cref="SoftbodyBrush"/> can paint: <see cref="Softbody"/> and <see cref="SkinnedSoftbody"/>.</summary>
interface IPaintable
{
    /// <summary>The synced dab list (renderer-slot local space; skinned: bind space).</summary>
    SyncList<Softbody.PaintDab> PaintDabs { get; }
    /// <summary>The running simulation on this client, or null.</summary>
    IBrushTarget BrushTarget { get; }
    void ClearPaint();
    bool IsRemoved { get; }
    Slot Slot { get; }
    World World { get; }
}

/// <summary>What the brush needs from a running simulation: its paint layer and where its mesh is shown.</summary>
interface IBrushTarget
{
    /// <summary>The renderer's slot (the mesh's own space).</summary>
    Slot Anchor { get; }
    PaintLayer Paint { get; }
    ColorProfile Profile { get; }
    /// <summary>Skinned: makes the paint layer's queries run on the posed mesh (renderer-local), for a brush between the two points.</summary>
    void PrepareBrushQuery(Vector3 localFrom, Vector3 localTo);
    /// <summary>Where a hit is on the rest (bind-space) mesh: dabs are stored there.</summary>
    Vector3 BindPoint(in MeshHit hit);
    /// <summary>Renderer-local units per bind-space unit at the hit.</summary>
    float BindScale(in MeshHit hit);
}
