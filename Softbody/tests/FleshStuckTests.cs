using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.26.4: Lenki: the skin points get stuck on the mostly flat boxes Resonite's colliders are made of, pulling the softbody
// away from the mesh, and only let go when it moves very fast. A thin box is a wall from both sides: once the body's
// animated place has passed through it, the points stay on the old side and are pushed back there every substep.
static partial class Program
{
    /// <summary>One run: the body moves by <paramref name="body"/>, against one collider from <paramref name="shape"/>; then
    /// measured after the last frame. Returns how far the skin points are from where the animation puts them (the largest,
    /// and how many over 5 mm), and the furthest any was held out of the body during the run (along its outward normal).</summary>
    static (float restMax, int stuck, float pullMax, int resets, float heldMax) StuckRun(FleshModel m, float[] torso, float[] w, bool keepIn,
                                                                           Func<int, Matrix4x4> body, Func<int, Matrix4x4, FleshCollider> shape, int frames)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        StuckSetup(rig, keepIn);
        float pull = 0f, held = 0f;
        string worst = "";
        for (int f = 0; f < frames; f++)
        {
            var pose = body(f);
            rig.Solver.Colliders.Clear();
            if (shape != null) rig.Solver.Colliders.Add(shape(f, pose));
            rig.Step(Dt, m.Pose(null, pose), w);
            var x = rig.Solver.X; var t = rig.Targets; var touching = rig.Solver.ContactShapeA; var cn = rig.Solver.ContactNormal;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                var d = rig.Solver.Inward[i];
                if (rig.Shell.Kinematic[i] || d == Vector3.Zero) continue;
                float o = -Vector3.Dot(x[i] - t[i], d);
                pull = MathF.Max(pull, o);
                // held out by a collider: pushed by one this frame (and not let go), and not into the body (a point
                // springing back out of a dent and pressed in again is not held out)
                if (i < touching.Length && touching[i] >= 0 && o > held)
                {
                    float press = Vector3.Dot(-cn[i], d);
                    if (press < 0.5f)
                    {
                        held = o;
                        worst = $"frame {f}, point {i}: {o * 1000:0.0} mm out, inward ({d.X:0.00},{d.Y:0.00},{d.Z:0.00}), pushed along ({-cn[i].X:0.00},{-cn[i].Y:0.00},{-cn[i].Z:0.00}) " +
                                $"(into the body {press:0.00}), target ({t[i].X:0.000},{t[i].Y:0.000},{t[i].Z:0.000}), at ({x[i].X:0.000},{x[i].Y:0.000},{x[i].Z:0.000})";
                    }
                }
            }
        }
        if (Environment.GetEnvironmentVariable("FLESH_DIAG") == "1" && worst != "") Console.WriteLine("    worst held: " + worst);
        float max = 0f; int stuck = 0;
        bool diag = Environment.GetEnvironmentVariable("FLESH_DIAG") == "1";
        for (int i = 0; i < rig.Surface.Count; i++)
        {
            if (rig.Shell.Kinematic[i] || !(rig.Solver.Radius[i] > 0f)) continue;
            var u = rig.Offset(i);
            float o = u.Length();
            max = MathF.Max(max, o);
            if (o > 0.005f)
            {
                stuck++;
                if (diag && stuck <= 6)
                {
                    var d = rig.Solver.Inward[i]; float ui = Vector3.Dot(u, d);
                    Console.WriteLine($"    stuck point {i}: target ({rig.Targets[i].X:0.000},{rig.Targets[i].Y:0.000},{rig.Targets[i].Z:0.000}) inward ({d.X:0.00},{d.Y:0.00},{d.Z:0.00}) " +
                                      $"offset {o * 1000:0.0} mm: {ui * 1000:0.0} into the body, {(u - d * ui).Length() * 1000:0.0} out or along the skin");
                }
            }
        }
        return (max, stuck, pull, rig.Resets, held);
    }

    static void StuckSetup(FleshRig rig, bool keepIn) { rig.Solver.KeepIn = keepIn; }

    static void FleshStuckTests(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh stuck on flat boxes (0.26.4): boards the body passes through, a box pulled back out of the belly");
        var w = new float[m.ShapeNames.Length];
        var probe = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        probe.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        probe.TargetBounds(out var lo, out var hi);
        var mid = 0.5f * (lo + hi);
        Console.WriteLine($"  torso targets {(hi - lo).X * 100:0} x {(hi - lo).Y * 100:0} x {(hi - lo).Z * 100:0} cm; skin point radius about {MedianRadius(probe) * 1000:0.0} mm");

        // every case as 0.26.3 did it (the points stay stuck) and with 0.26.4's let-go; the let-go one must recover fully
        // and hold the skin out of the body by no more than a centimetre at any time
        // ("held out": the furthest out of the body a point was while a collider pushed it, not into the body, that frame;
        // what is left with 0.26.4 is mostly points pushed out through a board's face overshooting for a moment)
        bool allFixed = true, allWorseBefore = true;
        void Judge(string what, (float restMax, int stuck, float pullMax, int resets, float heldMax) before,
                   (float restMax, int stuck, float pullMax, int resets, float heldMax) after, float alone)
        {
            Console.WriteLine($"  {what}: held out of the body by up to {before.heldMax * 1000:0.0} mm (0.26.3), {after.heldMax * 1000:0.0} mm (0.26.4); " +
                              $"any skin out by up to {before.pullMax * 1000:0.0} / {after.pullMax * 1000:0.0} mm ({alone * 1000:0.0} with no collider); " +
                              $"1 s after: {before.stuck} skin points still over 5 mm from the mesh, up to {before.restMax * 1000:0.0} mm (0.26.3), " +
                              $"{after.stuck}, up to {after.restMax * 1000:0.0} mm (0.26.4)");
            allFixed &= after.stuck == 0 && after.restMax < 5e-3f && after.resets == 0;
            allWorseBefore &= after.heldMax < 0.25f * before.heldMax && after.pullMax < 0.5f * before.pullMax;
        }

        // A: a board 5 cm in front of the belly; the body walks 12 cm forward through it, waits, walks back and stands still 1 s
        float Walk(int f) => f < 40 ? 0.12f * Ease(f / 39f) : f < 50 ? 0.12f : f < 90 ? 0.12f * (1f - Ease((f - 50) / 39f)) : 0f;
        float walkAlone = StuckRun(m, torso, w, true, f => Matrix4x4.CreateTranslation(0, 0, Walk(f)), null, 150).pullMax;
        foreach (float thick in new[] { 0.01f, 0.04f })
        {
            var board = FleshCollider.Box(new Vector3(mid.X, mid.Y, hi.Z + 0.05f + 0.5f * thick), Quaternion.Identity, new Vector3(0.5f, 0.5f, 0.5f * thick));
            Judge($"walk through a {thick * 100:0} cm board and back",
                  StuckRun(m, torso, w, false, f => Matrix4x4.CreateTranslation(0, 0, Walk(f)), (f, p) => board, 150),
                  StuckRun(m, torso, w, true, f => Matrix4x4.CreateTranslation(0, 0, Walk(f)), (f, p) => board, 150), walkAlone);
        }

        // B: a hand-sized box pressed 6 cm into the belly (past the shell's limit, so it goes through the skin), pulled back out
        int i0 = -1; float best = float.MinValue;
        for (int i = 0; i < probe.Surface.Count; i++)
        {
            if (probe.Shell.Kinematic[i] || probe.Solver.Inward[i] == Vector3.Zero) continue;
            var t = probe.Targets[i];
            if (MathF.Abs(t.X - mid.X) > 0.03f || probe.Surface.Soft[i] < 0.9f || probe.Surface.BorderDistance[i] <= 3) continue;
            if (t.Z > best) { best = t.Z; i0 = i; }
        }
        var p0 = probe.Targets[i0]; var nOut = -probe.Solver.Inward[i0];
        var q = FromTo(Vector3.UnitZ, nOut);
        var half = new Vector3(0.04f, 0.04f, 0.02f);
        float r0 = probe.Solver.Radius[i0];
        float Press(int f) => f < 30 ? -0.05f + 0.11f * Ease(f / 29f) : f < 40 ? 0.06f : f < 70 ? 0.06f - 0.11f * Ease((f - 40) / 29f) : -0.05f;
        FleshCollider Hand(int f, Matrix4x4 pose)
        {
            var c = p0 + nOut * (half.Z + r0 - Press(f));
            var prev = p0 + nOut * (half.Z + r0 - Press(Math.Max(0, f - 1)));
            var box = FleshCollider.Box(c, q, half);
            box.Velocity = (c - prev) / Dt;
            return box;
        }
        Judge("a box pressed 6 cm in and pulled back out",
              StuckRun(m, torso, w, false, f => Matrix4x4.Identity, Hand, 130),
              StuckRun(m, torso, w, true, f => Matrix4x4.Identity, Hand, 130), 0f);

        // C: a 2 cm seat 3 cm under the hips; the body sits 10 cm down through it and stands back up
        var seat = FleshCollider.Box(new Vector3(mid.X, lo.Y - 0.03f - 0.01f, mid.Z), Quaternion.Identity, new Vector3(0.5f, 0.01f, 0.5f));
        Matrix4x4 Sit(int f) => Matrix4x4.CreateTranslation(0, -Walk(f) * (0.10f / 0.12f), 0);
        Judge("sit down through a 2 cm seat and stand up",
              StuckRun(m, torso, w, false, Sit, (f, p) => seat, 150),
              StuckRun(m, torso, w, true, Sit, (f, p) => seat, 150), StuckRun(m, torso, w, true, Sit, null, 150).pullMax);
        // F: an arm (a 4 cm capsule across the body) swept sideways through the belly, from beside it to the other side
        var armA = new Vector3(0, 0, -0.15f); var armB = new Vector3(0, 0, 0.15f);
        float Sweep(int f) => f < 80 ? -0.20f + 0.40f * Ease(f / 79f) : 0.20f;
        FleshCollider Arm(int f, Matrix4x4 pose)
        {
            var c = new Vector3(mid.X + Sweep(f), mid.Y, hi.Z - 0.02f);
            var arm = FleshCollider.Capsule(c + armA, c + armB, 0.04f);
            arm.Velocity = new Vector3(Sweep(f) - Sweep(Math.Max(0, f - 1)), 0, 0) / Dt;
            return arm;
        }
        Judge("an arm swept sideways through the belly",
              StuckRun(m, torso, w, false, f => Matrix4x4.Identity, Arm, 140),
              StuckRun(m, torso, w, true, f => Matrix4x4.Identity, Arm, 140), 0f);

        Check(allFixed, "skin points never stay stuck on a collider the body passed through, or one pulled back out of it: 1 s later they are all back on the mesh");
        Check(allWorseBefore, "colliders hold the skin out of the body for a moment at most a quarter as far as 0.26.3 (and any skin is out at most half as far)");

        // D: an everyday press, a box 12 mm into the belly and held: nothing is let go, the same to the bit as 0.26.3
        {
            var pa = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f }); pa.Solver.KeepIn = false;
            var pb = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
            var rest = m.Pose(null, Matrix4x4.Identity);
            pa.Step(Dt, rest, w); pb.Step(Dt, rest, w);
            var dent = FleshCollider.Box(new Vector3(mid.X, mid.Y, hi.Z + 0.05f - 0.012f), Quaternion.Identity, new Vector3(0.04f, 0.04f, 0.05f));
            int let = 0, contacts = 0;
            for (int f = 0; f < 30; f++)
            {
                foreach (var rg in new[] { pa, pb }) { rg.Solver.Colliders.Clear(); rg.Solver.Colliders.Add(dent); rg.Step(Dt, rest, w); }
                let = Math.Max(let, pb.Solver.LetGo); contacts = Math.Max(contacts, pb.Solver.Contacts);
            }
            float diff = 0f;
            for (int i = 0; i < pa.Solver.N; i++) diff = MathF.Max(diff, Vector3.Distance(pa.Solver.X[i], pb.Solver.X[i]));
            Console.WriteLine($"  a box pressed 12 mm into the belly: {contacts} points touching, {let} let go, differs from 0.26.3 by {diff * 1000:0.000} mm");
            Check(contacts > 0 && let == 0 && diff == 0f, "an ordinary dent is untouched: nothing let go, bit-identical to 0.26.3");
        }

        // E: a hand ball (5 cm) pressed 15 mm and slid 12 cm across the belly and back: how much of the sliding changes
        {
            var pa = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f }); pa.Solver.KeepIn = false;
            var pb = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
            var rest = m.Pose(null, Matrix4x4.Identity);
            pa.Step(Dt, rest, w); pb.Step(Dt, rest, w);
            const float R = 0.05f;
            Vector3 Ball(int f) => p0 + nOut * (R + r0 - 0.015f * Ease(f / 15f)) + Vector3.UnitX * (0.06f * MathF.Sin(MathF.PI * 2f * Math.Max(0, f - 15) / 90f));
            int let = 0; float diff = 0f, dmax = 0f, side = 0f, outw = 0f;
            for (int f = 0; f < 120; f++)
            {
                var ball = FleshCollider.Sphere(Ball(f), R, source: 1);
                ball.Velocity = (Ball(f) - Ball(Math.Max(0, f - 1))) / Dt;
                foreach (var rg in new[] { pa, pb }) { rg.Solver.Colliders.Clear(); rg.Solver.Colliders.Add(ball); rg.Step(Dt, rest, w); }
                let = Math.Max(let, pb.Solver.LetGo);
                for (int i = 0; i < pa.Solver.N; i++) diff = MathF.Max(diff, Vector3.Distance(pa.Solver.X[i], pb.Solver.X[i]));
                dmax = MathF.Max(dmax, pb.MaxOffset);
                // how far 0.26.3's sliding held points from their animated place other than into the body
                for (int i = 0; i < pa.Surface.Count; i++)
                {
                    var d = pa.Solver.Inward[i];
                    if (pa.Shell.Kinematic[i] || d == Vector3.Zero) continue;
                    var u = pa.Offset(i); float ui = Vector3.Dot(u, d);
                    side = MathF.Max(side, (u - d * MathF.Max(0f, ui)).Length());
                    outw = MathF.Max(outw, -ui);
                }
            }
            Console.WriteLine($"  a 5 cm ball pressed 15 mm and slid across the belly: dented up to {dmax * 1000:0.0} mm, at most {let} points let go in a substep, " +
                              $"differs from 0.26.3 by up to {diff * 1000:0.00} mm; 0.26.3 moved points up to {side * 1000:0.0} mm other than into the body " +
                              $"(up to {outw * 1000:0.0} mm out of it)");
            Check(diff < 2e-3f, "a ball sliding across the belly dents it as before (within 2 mm)");
        }
    }

    static float MedianRadius(FleshRig rig)
    {
        var rs = new List<float>();
        for (int i = 0; i < rig.Surface.Count; i++) if (rig.Solver.Radius[i] > 0f) rs.Add(rig.Solver.Radius[i]);
        rs.Sort();
        return rs.Count > 0 ? rs[rs.Count / 2] : 0f;
    }
}
