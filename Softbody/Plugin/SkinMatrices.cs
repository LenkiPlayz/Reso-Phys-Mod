using System.Numerics;
using Elements.Core;

namespace Softbody;

/// <summary>
/// The engine's matrices to Core's. Elements.Core.float4x4 is column-vector (<c>p' = M p</c>, as
/// <c>float4x4.TransformPoint3x4</c>: translation in m03/m13/m23); Core uses row-vector System.Numerics matrices
/// (<c>p' = Vector3.Transform(p, N)</c>, translation in M41..M43), so N is the transpose of M and a product
/// <c>A * B</c> (B applied first) becomes <c>N(B) * N(A)</c>.
/// Only depends on Elements.Core, so the offline tests compile this file and check it against the engine's own
/// CPU skinning (Elements.Assets.BoneBinding.TransformPosition).
/// </summary>
static class SkinMatrices
{
    public static Matrix4x4 ToNumerics(in float4x4 m) => new(
        m.m00, m.m10, m.m20, m.m30,
        m.m01, m.m11, m.m21, m.m31,
        m.m02, m.m12, m.m22, m.m32,
        m.m03, m.m13, m.m23, m.m33);

    /// <summary>Skin matrix of one bone, as the renderer skins: <c>BoneSlot.LocalToGlobal * BindPose</c> (bind space to
    /// world). <paramref name="bindPose"/> is already converted with <see cref="ToNumerics"/>.</summary>
    public static Matrix4x4 Skin(in Matrix4x4 bindPose, in float4x4 boneToWorld) => bindPose * ToNumerics(boneToWorld);
}
