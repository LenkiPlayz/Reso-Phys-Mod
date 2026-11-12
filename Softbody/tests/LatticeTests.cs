using System.Numerics;
using Softbody.Core;

namespace Softbody.Tests;

// 0.10.1: what the debug views showed on a winged, tailed avatar (Cells 4, all soft or all firm).
static partial class Program
{
    static void LatticeTests()
    {
        Console.WriteLine("TEST lattice fixes (0.10.1): all-firm cage, floor under the rest shape, soft-weighted bones, no offset amplification");
        AllFirmCage();
        FloorUnderRestShape();
        SoftWeightedBones();
        NoOffsetAmplification();
    }

    /// <summary>Nothing soft (StartAs AllFirm, nothing painted): one pinned cell, not the whole box.</summary>
    static void AllFirmCage()
    {
        var m = Cube(20);
        var firm = new float[m.VertexCount];
        var cage = Cage.Build(m.Pos, firm, new SoftbodySettings { Cells = 12 });
        Console.WriteLine($"  all firm, Cells 12: {cage.PointCount} points ({cage.KinematicCount} pinned), {cage.ActiveCells}/{cage.TotalCells} cells");
        Check(cage.ActiveCells == 1 && cage.PointCount == 8 && cage.KinematicCount == 8,
              "a mesh with nothing soft gets a single pinned cell (it was every cell of the box, all pinned)");
        bool still = true;
        for (int v = 0; v < cage.VertexCount; v++) if (cage.VertexGain[v] != 0f) { still = false; break; }
        Check(still, "and no vertex follows it");
    }

    /// <summary>A soft cube whose slot sits 6 cm into the floor: the floor must not push it out of its rest shape.</summary>
    static void FloorUnderRestShape()
    {
        var m = Cube(24, 0.6f, gradient: false);
        var s = new SoftbodySettings { Gravity = 0f };
        var sunk = new AnchorPose(new Vector3(0, -0.06f, 0), Quaternion.Identity, Vector3.One);
        using var rig = Rig(m, s, sunk);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        for (int f = 0; f < 120; f++) rig.Step(Dt, sunk);
        float worst = MaxOffset(rig);
        Console.WriteLine($"  slot 6 cm into the floor: floor lowered {rig.Sim.GroundDrop * 1000:0.0} mm, worst point offset at rest {worst * 1000:0.00} mm");
        Check(rig.Sim.GroundDrop > 0.06f && worst < 0.002f, "the floor is lowered under the rest shape, so it pushes nothing out of place (< 2 mm)");

        // resting exactly on the floor, with gravity: the floor still holds the sagging bottom up
        var s2 = new SoftbodySettings { Gravity = 3f, Follow = 0.5f, Stiffness = 1f };
        using var rig2 = Rig(m, s2, AnchorPose.Identity);
        rig2.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        for (int f = 0; f < 240; f++) rig2.Step(Dt, AnchorPose.Identity);
        var pos = new Vector3[rig2.Cage.PointCount];
        rig2.Sim.GetPositions(pos);
        float lowest = float.MaxValue;
        for (int i = 0; i < pos.Length; i++) if (!rig2.Cage.Kinematic[i]) lowest = MathF.Min(lowest, pos[i].Y - rig2.Sim.RadiusWorld);
        Console.WriteLine($"  on the floor, 3 g, weak springs: floor lowered {rig2.Sim.GroundDrop * 1000:0.0} mm, lowest sphere bottom {lowest * 1000:0.0} mm");
        Check(rig2.Sim.GroundDrop < 0.002f && lowest > -0.01f, "a body resting on the floor still can't sag through it");
    }

    static float MaxOffset(SoftbodyRig rig)
    {
        int n = rig.Cage.PointCount;
        var pos = new Vector3[n]; var tgt = new Vector3[n];
        rig.Sim.GetPositions(pos); rig.Sim.GetTargets(tgt);
        float worst = 0;
        for (int i = 0; i < n; i++) worst = MathF.Max(worst, Vector3.Distance(pos[i], tgt[i]));
        return worst;
    }

    /// <summary>A bar: left half soft on bone 0, right half firm on bone 1. Points in the boundary cells follow bone 0.</summary>
    static void SoftWeightedBones()
    {
        var m = Cube(20, 0.6f, gradient: false);
        int n = m.VertexCount;
        var soft = new float[n]; var bones = new int[n * 4]; var weights = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            bool left = m.Pos[v].X < 0f;
            soft[v] = left ? 1f : 0f;
            bones[v * 4] = left ? 0 : 1; weights[v * 4] = 1f;
        }
        var skin = SkinData.FromBindings(2, bones, weights);
        var cage = Cage.Build(m.Pos, soft, new SoftbodySettings { Cells = 4 });
        var cs = CageSkin.Build(cage, skin);
        // which points carry soft vertices, and which carry both soft and firm ones (the boundary)
        var hasSoft = new bool[cage.PointCount]; var hasFirm = new bool[cage.PointCount];
        for (int v = 0; v < n; v++)
            for (int q = 0; q < 4; q++)
            {
                int p = cage.VertexPoints[v * 4 + q];
                if (p < 0 || !(cage.VertexWeights[v * 4 + q] > 0f)) continue;
                if (cage.VertexGain[v] > Cage.GainEpsilon) hasSoft[p] = true; else hasFirm[p] = true;
            }
        int mixed = 0, toBone0 = 0;
        for (int p = 0; p < cage.PointCount; p++)
        {
            if (!hasSoft[p] || !hasFirm[p]) continue;
            mixed++;
            float w0 = 0;
            for (int q = 0; q < 4; q++) if (cs.PointBones[p * 4 + q] == 0) w0 += cs.PointWeights[p * 4 + q];
            if (w0 > 0.99f) toBone0++;
        }
        Console.WriteLine($"  half soft (bone 0), half firm (bone 1): {toBone0} of the {mixed} points carrying both soft and firm vertices follow only the soft half's bone");
        Check(mixed > 0 && toBone0 == mixed, "cage points follow the bones of the vertices that move with them, not the firm neighbours sharing their cells");
    }

    /// <summary>A point blended 50/50 between a bone at rest and one turned 170 degrees: its matrix nearly collapses.</summary>
    static void NoOffsetAmplification()
    {
        float scale = 1.7f;
        var a = Matrix4x4.CreateScale(scale);
        var b = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 170f * MathF.PI / 180f);
        var blend = a * 0.5f + b * 0.5f; blend.M44 = 1f;
        var d = new Vector3(0.05f, 0.02f, 0.03f);
        Lbs.TryInvertLinear(blend, out var plain);
        bool ok = Lbs.TryInvertSimilar(blend, scale, out var sim);
        float before = Vector3.TransformNormal(d, plain).Length() * scale / d.Length();
        float after = Vector3.TransformNormal(d, sim).Length() * scale / d.Length();
        Console.WriteLine($"  bones 170 deg apart, 50/50: world offset gain in bind space x{before:0.0} with the plain inverse, x{after:0.00} now");
        Check(ok && before > 5f && MathF.Abs(after - 1f) < 1e-3f, "offsets are mapped through the blend's rotation and the bones' scale: no amplification");

        var rigid = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromYawPitchRoll(0.4f, -0.7f, 1.1f) * Matrix4x4.CreateTranslation(1, 2, 3);
        Lbs.TryInvertLinear(rigid, out var p1);
        Lbs.TryInvertSimilar(rigid, scale, out var p2);
        float diff = (Vector3.TransformNormal(d, p1) - Vector3.TransformNormal(d, p2)).Length();
        Check(diff < 1e-6f, "for a single bone (rotation x scale) it is the exact inverse, as before");
    }
}
