using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.11.4: a ball pressed into the belly. Pushing skin points out along the ball's normal slid them around it and folded
// the skin at its rim (Lenki's screenshot); pushing them into the body squishes instead.
static partial class Program
{
    static void FleshSquish(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh squish (0.11.4/0.11.5): a 6 cm ball pressed 3 cm into the belly");
        var w = new float[m.ShapeNames.Length];
        float dentErr = 0, dentMax = 0; int dentVerts = 0;
        float dentGap = 0, dentDeep = 0; int dentPressed = 0;
        bool closeGap = true, softMiddle = false; float press = 0.03f, depth = 0.5f;
        (float slide, float maxSlide, float inward, int touching, int flips, int inverted, float worstTurn) Run(bool squish, float friction, bool inwardPush,
            float depthScale = 1f, float volume = 0.45f, float stiffness = 2f, bool contactNormals = true)
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f, Stiffness = stiffness, Follow = 0.5f, Volume = volume, VolumePreservation = 0f, Depth = depth });
            rig.ContactNormals = contactNormals; rig.CloseContactGap = rig.FlattenContacts = rig.FitContacts = closeGap;
            rig.Solver.Squish = squish; rig.Solver.Friction = friction; rig.Solver.InwardPush = inwardPush; rig.Solver.DepthScale = depthScale;
            var pose = m.Pose(null, Matrix4x4.Identity);
            rig.Step(Dt, pose, w);
            // the most forward soft skin point near the middle of the belly, and its outward direction
            int i0 = -1; float best = float.MinValue;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                if (rig.Shell.Kinematic[i] || rig.Solver.Inward[i] == Vector3.Zero) continue;
                var t = rig.Targets[i];
                if (MathF.Abs(t.X) > 0.03f || (softMiddle ? rig.Surface.Soft[i] < 0.9f || rig.Surface.BorderDistance[i] <= 3 : t.Y > 0.3f)) continue;
                if (t.Z > best) { best = t.Z; i0 = i; }
            }
            var p0 = rig.Targets[i0]; var nOut = -rig.Solver.Inward[i0];
            const float R = 0.06f;
            float r0 = rig.Solver.Radius[i0];
            int inverted = 0;
            Vector3 center = p0 + nOut * (R + r0 + 0.02f);
            for (int f = 0; f < 60; f++)
            {
                float pen = -0.02f + (0.02f + press) * Ease(f / 30f);
                var prev = center;
                center = p0 + nOut * (R + r0 - pen);
                rig.Solver.Colliders.Clear();
                var ball = FleshCollider.Sphere(center, R);
                ball.Velocity = (center - prev) / Dt;
                rig.Solver.Colliders.Add(ball);
                rig.Step(Dt, pose, w);
                inverted = Math.Max(inverted, rig.Solver.Inverted);
            }
            float sumT = 0, sumN = 0, maxT = 0; int touching = 0;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                if (rig.Shell.Kinematic[i]) continue;
                if (Vector3.Distance(rig.Solver.X[i], center) > R + rig.Solver.Radius[i] + 2e-3f) continue;
                touching++;
                var u = rig.Offset(i); var d = rig.Solver.Inward[i];
                float un = Vector3.Dot(u, d);
                float ut = (u - d * un).Length();
                sumN += MathF.Abs(un); sumT += ut; maxT = MathF.Max(maxT, ut);
            }
            int k = rig.MovingVertices.Length;
            var outPos = new Vector3[k]; var outNrm = new Vector3[k];
            rig.FillOutput(m.Pos, m.Nrm, outPos, outNrm);
            // lighting in the dent: how far the render normals of the vertices touching the ball are from the ball's shape
            // (skin against a ball faces its centre)
            double errSum = 0; float errMax = 0; int dv = 0;
            for (int q = 0; q < k; q++)
            {
                if (Vector3.Distance(outPos[q], center) > R + 3e-3f) continue;
                var ideal = Vector3.Normalize(center - outPos[q]);
                float e = MathF.Acos(Math.Clamp(Vector3.Dot(outNrm[q], ideal), -1f, 1f)) * 180f / MathF.PI;
                errSum += e; errMax = MathF.Max(errMax, e); dv++;
            }
            dentErr = dv > 0 ? (float)(errSum / dv) : 0; dentMax = errMax; dentVerts = dv;
            // 0.12.1: how far the pressed skin is shown from the ball's surface (> 0 a gap, < 0 inside the ball)
            double gs = 0; float deep = 0; int gn = 0;
            var ca = rig.ContactAlpha;
            for (int q = 0; q < k && q < ca.Length; q++)
            {
                if (!(ca[q] > 0.9f)) continue;
                float g = Vector3.Distance(outPos[q], center) - R;
                gs += g; deep = MathF.Min(deep, g); gn++;
            }
            dentGap = gn > 0 ? (float)(gs / gn) : 0; dentDeep = deep; dentPressed = gn;
            if (Environment.GetEnvironmentVariable("FLESH_DIAG") == "1" && press < 0.02f)
                Console.WriteLine($"    diag fit: {rig.FitVertices} vertices measured, mean {rig.FitMean * 1000:0.00} mm, largest move {rig.FitMax * 1000:0.00} mm; shown {dentGap * 1000:0.00} mm");
            int flips = 0; float worstTurn = 0;
            for (int q = 0; q < k; q++)
            {
                var n0 = Vector3.Normalize(m.Nrm[rig.MovingVertices[q]]);
                float turn = MathF.Acos(Math.Clamp(Vector3.Dot(outNrm[q], n0), -1f, 1f)) * 180f / MathF.PI;
                worstTurn = MathF.Max(worstTurn, turn);
                if (turn > 75f) flips++;
            }
            return (sumN > 0 ? sumT / sumN : 0, maxT, sumN / Math.Max(1, touching), touching, flips, inverted, worstTurn);
        }
        void Show(string label, (float slide, float maxSlide, float inward, int touching, int flips, int inverted, float worstTurn) r) =>
            Console.WriteLine($"  {label,-38} {r.touching,3} touching, sideways {r.slide:0.00} x inward (max {r.maxSlide * 1000,5:0.0} mm), dent {r.inward * 1000,4:0.0} mm, " +
                              $"{r.flips} normals over 75 deg (worst {r.worstTurn:0}), {r.inverted} inverted tets");
        // the shape of the dent (render normals from the geometry alone); the lighting is checked after
        var slide = Run(false, 0f, false, contactNormals: false);
        var inwardOnly = Run(true, 0f, true, contactNormals: false);
        var fricOnly = Run(true, 0.8f, false, contactNormals: false);
        var both = Run(true, 0.8f, true, contactNormals: false);
        float plainErr = dentErr, plainMax = dentMax;
        Show("A pushed out along the ball (0.11.3):", slide);
        Show("D inward push, no friction:", inwardOnly);
        Show("B friction + depth limit (0.11.4):", fricOnly);
        Show("C friction + inward push:", both);
        var withCn = Run(true, 0.8f, true);
        Console.WriteLine($"  lighting in the dent ({dentVerts} render vertices touching the ball): normals off the ball's shape by {plainErr:0.0} deg on average ({plainMax:0} at worst) " +
                          $"from the squeezed geometry alone, {dentErr:0.0} ({dentMax:0}) blended toward the inverse of the ball's normal (0.11.5)");
        Check(dentErr < 0.5f * plainErr && dentMax < plainMax, "a dent is lit like the ball pressing it: render normals follow the collider's shape (the inverse of its normal)");
        // pressed 1 cm into a deep shell, within its depth limit (pressed 3 cm the ball goes through the skin, by design)
        // and where the skin is fully soft (the lower belly the rest of this test uses is partly firm: a ball always sinks
        // into what is shown of partly firm skin, by its firmness)
        press = 0.01f; depth = 0.9f; softMiddle = true;
        Run(true, 0.8f, true);
        float gapClosed = dentGap, deepClosed = dentDeep; int pressedClosed = dentPressed;
        closeGap = false; Run(true, 0.8f, true); closeGap = true; press = 0.03f; depth = 0.5f; softMiddle = false;
        Console.WriteLine($"  a ball pressed 1 cm: the pressed skin ({pressedClosed} render vertices fully in contact) is shown {dentGap * 1000:0.0} mm from its surface on average (0.12.0, deepest {dentDeep * 1000:0.0}), " +
                          $"{gapClosed * 1000:0.0} mm with the gap closed and the skin flattened (0.12.1, deepest {deepClosed * 1000:0.0})");
        Check(MathF.Abs(gapClosed) < 0.5e-3f && deepClosed > -1e-3f && deepClosed >= dentDeep,
              "the skin around a ball is shown against it (within half a millimetre on average), without sinking into it anywhere");
        var squish = both;
        if (Environment.GetEnvironmentVariable("FLESH_DIAG") == "1")
        {
            foreach (float ds in new[] { 0.5f, 0.75f })
                Show($"C, depth x{ds}:", Run(true, 0.8f, true, ds));
            Show("C, volume 0.3:", Run(true, 0.8f, true, 1f, 0.3f));
            Show("C, stiffness 8:", Run(true, 0.8f, true, 1f, 0.45f, 8f));
            Show("C, friction 2:", Run(true, 2f, true));
        }
        Check(squish.maxSlide < 0.5f * slide.maxSlide, "pressed skin grips the ball and goes in with it instead of being shoved around it (sideways slide halved at least)");
        Check(squish.inverted <= 3 && slide.inverted > 20 && squish.worstTurn < slide.worstTurn,
              "and the skin around the dent does not fold: next to no inverted tets (the old push turned dozens inside out), normals turn less");
    }

    /// <summary>0.12.0: the volume a dent pushes in comes back out as a bulge in the skin around it.</summary>
    static void FleshVolumePreservation(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh volume preservation (0.12.0): the same ball, the skin around the dent");
        var w = new float[m.ShapeNames.Length];
        (Vector3[] x, Vector3[] inward, bool[] touching, float dent, float bulge) Run(float vp)
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f, Stiffness = 2f, Follow = 0.5f, VolumePreservation = vp });
            var pose = m.Pose(null, Matrix4x4.Identity);
            rig.Step(Dt, pose, w);
            int i0 = -1; float best = float.MinValue;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                if (rig.Shell.Kinematic[i] || rig.Solver.Inward[i] == Vector3.Zero) continue;
                var t = rig.Targets[i];
                if (MathF.Abs(t.X) > 0.03f || t.Y > 0.3f) continue;
                if (t.Z > best) { best = t.Z; i0 = i; }
            }
            var p0 = rig.Targets[i0]; var nOut = -rig.Solver.Inward[i0];
            const float R = 0.06f;
            float r0 = rig.Solver.Radius[i0];
            Vector3 center = p0 + nOut * (R + r0 + 0.02f);
            for (int f = 0; f < 90; f++)
            {
                float pen = -0.02f + 0.05f * Ease(f / 30f);
                var prev = center;
                center = p0 + nOut * (R + r0 - pen);
                rig.Solver.Colliders.Clear();
                var ball = FleshCollider.Sphere(center, R);
                ball.Velocity = (center - prev) / Dt;
                rig.Solver.Colliders.Add(ball);
                rig.Step(Dt, pose, w);
            }
            var touch = new bool[rig.Surface.Count];
            for (int i = 0; i < touch.Length; i++) touch[i] = rig.Solver.ContactWeight.Length > i && rig.Solver.ContactWeight[i] > 0.99f;
            return (rig.Solver.X.Take(rig.Surface.Count).ToArray(), rig.Solver.Inward.Take(rig.Surface.Count).ToArray(), touch, rig.DentVolume, rig.BulgeVolume);
        }
        var off = Run(0f);
        var on = Run(1f);
        // the skin around the dent: points not touching the ball, within 3.5 cm of a touching one
        double outward = 0; float most = 0; int n = 0;
        for (int i = 0; i < off.x.Length; i++)
        {
            if (off.touching[i] || on.touching[i] || off.inward[i] == Vector3.Zero) continue;
            bool near = false;
            for (int j = 0; j < off.x.Length && !near; j++) near = off.touching[j] && Vector3.Distance(off.x[i], off.x[j]) < 0.035f;
            if (!near) continue;
            float o = Vector3.Dot(on.x[i] - off.x[i], -off.inward[i]);
            outward += o; most = MathF.Max(most, o); n++;
        }
        outward /= Math.Max(1, n);
        Console.WriteLine($"  dent volume {on.dent * 1e6:0.0} cm3; with VolumePreservation 1 the {n} skin points around it rise {outward * 1000:0.00} mm on average (up to {most * 1000:0.0} mm), " +
                          $"bulge {on.bulge * 1e6:0.0} cm3 ({(on.dent > 0 ? on.bulge / on.dent : 0):0.00} of the dent); with 0, bulge {off.bulge * 1e6:0.0} cm3");
        Check(off.bulge == 0f && on.bulge > 0.5f * on.dent && on.bulge < 1.2f * on.dent, "with VolumePreservation 1 about the dent's volume comes back out around it (none with 0)");
        Check(outward > 1e-3f, "the skin around the dent rises: squeezing it in pushes the edges out");
    }
}
