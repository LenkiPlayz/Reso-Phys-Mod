using System.Numerics;

namespace Softbody.Core;

/// <summary>Cage + private simulation + deformer for one mesh. What both the plugin and the tests drive.
/// Anchor mode (a plain mesh on a slot) or skinned mode (a skinned mesh: the cage follows the skeleton and the
/// deformer writes bind-space offsets that the renderer skins again).</summary>
public sealed class SoftbodyRig : IDisposable
{
    public readonly Cage Cage;
    public readonly CageSimulation Sim;
    public readonly MeshDeformer Deformer;
    /// <summary>Skinned mode only (null otherwise): the mesh's skin and the cage points' skin weights.</summary>
    public readonly SkinData Skin;
    public readonly CageSkin CageSkin;
    readonly Vector3[] _cageWorld;
    readonly Vector3[] _bindOffsets;

    SoftbodyRig(Cage cage, CageSimulation sim, MeshDeformer deformer, SkinData skin = null, CageSkin cageSkin = null)
    {
        Cage = cage; Sim = sim; Deformer = deformer; Skin = skin; CageSkin = cageSkin;
        _cageWorld = new Vector3[cage.PointCount];
        if (skin != null) _bindOffsets = new Vector3[cage.PointCount];
    }

    public static SoftbodyRig Create(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<int> triangles,
                                     ReadOnlySpan<float> softness, SoftbodySettings settings, in AnchorPose anchor,
                                     ReadOnlySpan<float> hueBands = default)
    {
        var s = settings.Clone();
        s.Clamp();
        var cage = Cage.Build(positions, softness, s, hueBands);
        var deformer = new MeshDeformer(cage, positions, normals, triangles);
        var sim = new CageSimulation(cage, s, anchor);
        return new SoftbodyRig(cage, sim, deformer);
    }

    /// <summary>
    /// Skinned mode. <paramref name="positions"/> are the mesh's bind-space (base) positions; <paramref name="skinMatrices"/>
    /// the current pose (<see cref="SkinData.MatrixCount"/> matrices, bind space to world); <paramref name="anchor"/> the
    /// renderer slot (scale-change check only).
    /// </summary>
    public static SoftbodyRig CreateSkinned(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<int> triangles,
                                            ReadOnlySpan<float> softness, SoftbodySettings settings, SkinData skin,
                                            ReadOnlySpan<Matrix4x4> skinMatrices, in AnchorPose anchor, ReadOnlySpan<float> hueBands = default)
    {
        if (skin == null) throw new ArgumentNullException(nameof(skin));
        if (skin.VertexCount != positions.Length) throw new ArgumentException("skin/mesh vertex count mismatch");
        var s = settings.Clone();
        s.Clamp();
        var cage = Cage.Build(positions, softness, s, hueBands);
        var deformer = new MeshDeformer(cage, positions, normals, triangles);
        var cageSkin = CageSkin.Build(cage, skin);
        var sim = new CageSimulation(cage, s, cageSkin, skinMatrices, anchor);
        return new SoftbodyRig(cage, sim, deformer, skin, cageSkin);
    }

    public bool IsSkinned => Skin != null;

    public int Step(float dt, in AnchorPose anchor) => Sim.Step(dt, anchor);

    /// <summary>Skinned mode: advances the cage for this pose (see <see cref="CageSimulation.StepSkinned"/>).</summary>
    public int StepSkinned(float dt, ReadOnlySpan<Matrix4x4> skinMatrices) => Sim.StepSkinned(dt, skinMatrices);

    /// <summary>The clamped settings in effect.</summary>
    public SoftbodySettings Settings => Sim.Settings;

    /// <summary>Live re-tuning without a rebuild (see <see cref="CageSimulation.ApplyTuning"/>).</summary>
    public TuneResult ApplyTuning(SoftbodySettings settings) => Sim.ApplyTuning(settings);

    public ReadOnlySpan<Vector3> CagePositions { get { Sim.GetPositions(_cageWorld); return _cageWorld; } }

    /// <summary>Deforms into the buffers (which must hold the rest mesh initially). Returns max cage displacement.</summary>
    public float Deform(in AnchorPose anchor, Span<Vector3> positions, Span<Vector3> normals, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        if (IsSkinned) return DeformSkinned(positions, normals, out boundsMin, out boundsMax);
        Sim.GetPositions(_cageWorld);
        return Deformer.Deform(_cageWorld, anchor, positions, normals, out boundsMin, out boundsMax);
    }

    /// <summary>Skinned mode: writes the bind-space base positions (rest + offset) and normals. Returns max cage offset.</summary>
    public float DeformSkinned(Span<Vector3> positions, Span<Vector3> normals, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        float d = UpdateCageSkinned(out _);
        Deformer.ApplyToMesh(positions, normals, out boundsMin, out boundsMax);
        Deformer.CommitSnapshot();
        return d;
    }

    /// <summary>Cheap part: cage displacement in the mesh frame; maxChange is against what was last committed.</summary>
    public float UpdateCage(in AnchorPose anchor, out float maxChange)
    {
        if (IsSkinned) return UpdateCageSkinned(out maxChange);
        Sim.GetPositions(_cageWorld);
        return Deformer.UpdateCage(_cageWorld, anchor, out maxChange);
    }

    /// <summary>Skinned mode, cheap part: the points' bind-space offsets into the deformer.</summary>
    public float UpdateCageSkinned(out float maxChange)
    {
        Sim.GetBindOffsets(_bindOffsets);
        return Deformer.SetDisplacements(_bindOffsets, out maxChange);
    }

    public void ApplyToMesh(Span<Vector3> positions, Span<Vector3> normals, out Vector3 boundsMin, out Vector3 boundsMax) =>
        Deformer.ApplyToMesh(positions, normals, out boundsMin, out boundsMax);

    public void CommitSnapshot() => Deformer.CommitSnapshot();

    public double VolumeRatio()
    {
        Sim.GetPositions(_cageWorld);
        return Sim.RestVolumeWorld != 0 ? Cage.TotalVolume(_cageWorld) / Sim.RestVolumeWorld : 1;
    }

    public void Dispose() => Sim.Dispose();
}
