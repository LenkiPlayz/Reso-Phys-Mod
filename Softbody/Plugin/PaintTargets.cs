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
    /// <summary>0.28.0: how the brush names the renderer's material at this index (0 = the first): "#1 Body".</summary>
    string MaterialName(int index);
}

/// <summary>0.28.0: names of a renderer's materials, for the brush's menu and status.</summary>
static class BrushMaterials
{
    const int MaxName = 28;

    /// <summary>"#n name": n counts from 1; the name is the material's slot name (with its type when several materials share
    /// that slot), or just its type when the slot has no name.</summary>
    public static string Name(SyncAssetList<Material> list, int index)
    {
        string label = "#" + (index + 1);
        try
        {
            if (list == null || index < 0 || index >= list.Count) return label;
            if (list[index] is not Component c) return label;
            string type = c.GetType().Name;
            string name = c.Slot?.Name;
            if (string.IsNullOrWhiteSpace(name)) return label + " " + type;
            name = name.Trim();
            if (name.Length > MaxName) name = name.Substring(0, MaxName - 1) + "…";
            for (int i = 0; i < list.Count; i++)
                if (i != index && list[i] is Component o && !ReferenceEquals(o, c) && ReferenceEquals(o.Slot, c.Slot)) return $"{label} {name} ({type})";
            return label + " " + name;
        }
        catch { return label; }
    }
}

/// <summary>0.28.0: where the brush's mirror image of a dab went. Top-level (not nested in the brush), so the engine doesn't
/// register it as a data-model type: it is never synced or saved, and it must not change the compatibility hash.</summary>
enum BrushMirror { Off, NotSymmetric, NoSurface, Placed }
