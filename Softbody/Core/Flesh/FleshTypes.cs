using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// Everything the skinned (flesh) softbody needs from a mesh, engine-free. Positions, normals and bone points are in
/// the mesh's bind space (what the renderer skins). Softness is the per-render-vertex gain from the paint layer (0 firm,
/// 1 fully soft), the same values the lattice softbody uses.
/// </summary>
public sealed class FleshInput
{
    public Vector3[] Positions;
    public Vector3[] Normals;
    public int[] Triangles;
    public float[] Softness;
    public SkinData Skin;
    /// <summary>Optional (null = no blendshapes).</summary>
    public BlendshapeDeltas Blendshapes;
    /// <summary>Per bone (Skin.BoneCount entries): head and tail of the bone in bind space, for the bone distance.</summary>
    public Vector3[] BoneHeads, BoneTails;

    public int VertexCount => Positions.Length;
}

/// <summary>Settings of the flesh softbody. Structural ones (marked) rebuild the tets; the rest are live.</summary>
public sealed class FleshSettings
{
    /// <summary>Structural. Target number of soft surface points the painted region is reduced to.</summary>
    public int SimPoints = 800;
    /// <summary>Structural. How deep the shell goes, as a fraction of the distance from the skin to the bone (0.05..0.95).</summary>
    public float Depth = 0.5f;
    /// <summary>Structural. Tet layers through the shell (1..3).</summary>
    public int Layers = 1;
    /// <summary>Structural. Softness above this is soft; the ring of vertices around the soft ones is pinned.</summary>
    public float SoftThreshold = Cage.GainEpsilon;

    /// <summary>Young's modulus of the flesh in kPa (real soft tissue is roughly 1..50).</summary>
    public float Stiffness = 8f;
    /// <summary>Poisson ratio (0.05..0.49): how strongly the flesh keeps its volume.</summary>
    public float Volume = 0.45f;
    /// <summary>Extra stiffness toward the bone: deep tets are (1 + 3 x BoneDensity) times stiffer than skin-level ones.</summary>
    public float BoneDensity = 0.5f;
    /// <summary>Kg per cubic metre.</summary>
    public float Density = 1000f;
    /// <summary>Follow spring frequency in Hz at full softness (pulls each surface point toward its animated place).</summary>
    public float Follow = 1.5f;
    /// <summary>Damping of the motion relative to the animation, per second.</summary>
    public float Damping = 4f;
    /// <summary>
    /// How much a blendshape change throws the flesh around (0..1). 0: blendshapes carry the flesh with them and only
    /// bone motion makes it jiggle (a "hide" or "fold" shape does not fling it); 1: the flesh lags behind blendshapes too.
    /// </summary>
    public float BlendshapeInertia = 0f;
    /// <summary>Multiplier of 9.81 m/s² (world -Y).</summary>
    public float Gravity = 1f;
    /// <summary>XPBD substeps per frame (one constraint pass each).</summary>
    public int Substeps = 8;
    /// <summary>World metres: when the animated shape jumps further than this in one frame (a respawn, a teleport), the flesh
    /// is put back on it at rest instead of being flung.</summary>
    public float TeleportDistance = 0.75f;
    /// <summary>Collide with the ground and nearby world colliders (the plugin gathers them; the solver only pushes out).</summary>
    public bool Collide = true;
    /// <summary>0.12.0: the avatar's own colliders (self-collision), and which parts of it: head, body (torso, arms, legs),
    /// hands.</summary>
    public bool CollideSelf = true, SelfHead = true, SelfBody = true, SelfHands = true;
    /// <summary>0.12.0: other users' colliders, and which parts of them.</summary>
    public bool CollideOthers = true, OthersHead = true, OthersBody = true, OthersHands = true;
    /// <summary>
    /// 0.12.0, 0..1: how much of the volume a dent pushes in comes back out as a bulge in the skin around it (0 = none,
    /// 1 = all of it), like squeezing something soft.
    /// </summary>
    public float VolumePreservation = 0.5f;
    /// <summary>0.12.0, metres: past this distance from the local user the simulation and its collisions stop and the skin
    /// rests (0 = never). Each client decides for itself.</summary>
    public float CullDistance = 20f;

    public FleshSettings Clone() => (FleshSettings)MemberwiseClone();

    /// <summary>The settings whose change needs new tets.</summary>
    public string StructureKey => FormattableString.Invariant($"points={SimPoints} depth={Depth:R} layers={Layers} threshold={SoftThreshold:R}");

    /// <summary>Copies the settings that a running rig takes live (everything but the structural ones).</summary>
    public void CopyLiveFrom(FleshSettings o)
    {
        Stiffness = o.Stiffness; Volume = o.Volume; BoneDensity = o.BoneDensity; Density = o.Density; Follow = o.Follow;
        Damping = o.Damping; Gravity = o.Gravity; BlendshapeInertia = o.BlendshapeInertia; Substeps = o.Substeps;
        TeleportDistance = o.TeleportDistance; Collide = o.Collide;
        CollideSelf = o.CollideSelf; SelfHead = o.SelfHead; SelfBody = o.SelfBody; SelfHands = o.SelfHands;
        CollideOthers = o.CollideOthers; OthersHead = o.OthersHead; OthersBody = o.OthersBody; OthersHands = o.OthersHands;
        VolumePreservation = o.VolumePreservation; CullDistance = o.CullDistance;
    }

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"points {SimPoints}, depth {Depth:0.##}, layers {Layers}, stiffness {Stiffness:0.##} kPa, volume {Volume:0.###}, bone density {BoneDensity:0.##}, follow {Follow:0.##} Hz, damping {Damping:0.##}/s, gravity {Gravity:0.##}, blendshape inertia {BlendshapeInertia:0.##}, substeps {Substeps}, collide {(Collide ? "on" : "off")}, self {Parts(CollideSelf, SelfHead, SelfBody, SelfHands)}, others {Parts(CollideOthers, OthersHead, OthersBody, OthersHands)}, volume preservation {VolumePreservation:0.##}, cull {(CullDistance > 0f ? CullDistance.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m" : "off")}");

    static string Parts(bool on, bool head, bool body, bool hands) =>
        !on ? "off" : string.Join("+", new[] { head ? "head" : null, body ? "body" : null, hands ? "hands" : null }.Where(x => x != null)) is var p && p.Length > 0 ? p : "none";

    /// <summary>Clamps everything into its valid range.</summary>
    public void Sanitize()
    {
        SimPoints = Math.Clamp(SimPoints, 16, 20000);
        Depth = Math.Clamp(float.IsFinite(Depth) ? Depth : 0.5f, 0.05f, 0.95f);
        Layers = Math.Clamp(Layers, 1, 3);
        SoftThreshold = Math.Clamp(float.IsFinite(SoftThreshold) ? SoftThreshold : Cage.GainEpsilon, 1e-4f, 0.9f);
        Stiffness = Math.Clamp(float.IsFinite(Stiffness) ? Stiffness : 8f, 0.05f, 5000f);
        Volume = Math.Clamp(float.IsFinite(Volume) ? Volume : 0.45f, 0.05f, 0.49f);
        BoneDensity = Math.Clamp(float.IsFinite(BoneDensity) ? BoneDensity : 0.5f, 0f, 10f);
        Density = Math.Clamp(float.IsFinite(Density) ? Density : 1000f, 10f, 20000f);
        Follow = Math.Clamp(float.IsFinite(Follow) ? Follow : 1.5f, 0f, 100f);
        Damping = Math.Clamp(float.IsFinite(Damping) ? Damping : 4f, 0f, 200f);
        BlendshapeInertia = Math.Clamp(float.IsFinite(BlendshapeInertia) ? BlendshapeInertia : 0f, 0f, 1f);
        VolumePreservation = Math.Clamp(float.IsFinite(VolumePreservation) ? VolumePreservation : 0.5f, 0f, 1f);
        CullDistance = Math.Clamp(float.IsFinite(CullDistance) ? CullDistance : 20f, 0f, 10000f);
        Gravity = Math.Clamp(float.IsFinite(Gravity) ? Gravity : 1f, -10f, 10f);
        Substeps = Math.Clamp(Substeps, 1, 32);
        TeleportDistance = Math.Clamp(float.IsFinite(TeleportDistance) ? TeleportDistance : 0.75f, 0.05f, 100f);
    }
}
