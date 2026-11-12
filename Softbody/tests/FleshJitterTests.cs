using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.26.6: Lenki, in game on 0.26.5: after sudden movements or collider hits a few skin points fly around for about a
// quarter of a second. The logs agree: with only the avatar's own bone colliders near, the worst skin point was up to
// 0.88 m off its place (0.26.2: up to 4.3 m), with up to 53 inverted tets at the same moments.
static partial class Program
{
    sealed class JitterResult
    {
        public float WorstMm, WorstSpeed; public int WorstInverted, FarFrames, Resets; public float SettleS = float.NaN; public float RestMm;
        public override string ToString() =>
            $"worst {WorstMm,6:0.0} mm, fastest point {WorstSpeed,5:0.0} m/s off the animation, {WorstInverted,3} inverted tets at most, " +
            $"{FarFrames,2} frames with a point over 10 cm off, settled {(float.IsNaN(SettleS) ? "never" : $"after {SettleS:0.00} s")}, {RestMm:0.0} mm left at the end";
    }

    /// <summary>One sudden event against the torso (own bone capsules with their at-rest masks around it, gravity on):
    /// <paramref name="pose"/> per frame, plus extra colliders per frame. The event starts at frame 20.</summary>
    static JitterResult JitterRun(FleshModel m, float[] torso, float[] w, FleshSettings settings, Func<int, Matrix4x4[]> pose,
                                  Func<int, IEnumerable<FleshCollider>> extra, bool keepIn, int frames = 110, Action<FleshRig> setup = null, bool trace = false)
    {
        var caps = AvatarCapsules(m);
        var rig = FleshBuildRig(m, torso, settings);
        rig.Solver.KeepIn = keepIn;
        setup?.Invoke(rig);
        var rest = m.Pose(null, Matrix4x4.Identity);
        rig.Step(Dt, rest, w);
        foreach (var c in caps) c.Ignore = rig.TouchingAtRest(FleshCollider.Capsule(c.A, c.B, c.R));
        var res = new JitterResult();
        var prev = (Vector3[])rig.Solver.X.Clone();
        var prevT = rig.Targets.ToArray();
        float baseline = 0f;
        for (int f = 0; f < frames; f++)
        {
            var p = pose(f);
            rig.Solver.Colliders.Clear();
            foreach (var c in caps)
            {
                var col = FleshCollider.Capsule(Vector3.Transform(c.A, p[c.Bone]), Vector3.Transform(c.B, p[c.Bone]), c.R);
                col.Source = 2; col.Ignore = c.Ignore;
                rig.Solver.Colliders.Add(col);
            }
            if (extra != null) rig.Solver.Colliders.AddRange(extra(f));
            rig.Step(Dt, p, w);
            var x = rig.Solver.X; var t = rig.Targets;
            float speed = 0f;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                if (rig.Shell.Kinematic[i]) continue;
                // how fast the point moves relative to where the animation takes it
                float v = Vector3.Distance(x[i] - prev[i], t[i] - prevT[i]) / Dt;
                if (float.IsFinite(v)) speed = MathF.Max(speed, v);
            }
            Array.Copy(x, prev, prev.Length); t.CopyTo(prevT);
            float off = rig.MaxOffset * 1000f;
            if (trace && f >= 18 && (f < 60 || f % 10 == 9))
                Console.WriteLine($"      frame {f,3}: max {off,6:0.0} mm, fastest {speed,5:0.0} m/s, {rig.Solver.Contacts,3} contacts, {rig.Solver.LetGo,2} let go, " +
                                  $"{rig.Solver.Unbounced,3} unbounced, {rig.Carried,4} carried, {rig.Solver.Inverted,3} inverted");
            if (trace && f == frames - 1)
            {
                int worst = -1; float wo = 0f;
                for (int i = 0; i < rig.Surface.Count; i++) { if (rig.Shell.Kinematic[i]) continue; float o = rig.Offset(i).Length(); if (o > wo) { wo = o; worst = i; } }
                if (worst >= 0)
                {
                    var u = rig.Offset(worst); var d = rig.Solver.Inward[worst];
                    float near = float.MaxValue; string nearName = "";
                    foreach (var c in caps)
                    {
                        var a = Vector3.Transform(c.A, p[c.Bone]); var b = Vector3.Transform(c.B, p[c.Bone]);
                        float dist = Vector3.Distance(x[worst], TetShell.ClosestOnSegment(a, b, x[worst])) - c.R;
                        if (dist < near) { near = dist; nearName = c.Name + (XpbdSolver.Ignored(c.Ignore, worst) ? " (ignored at rest)" : ""); }
                    }
                    Console.WriteLine($"      worst at the end: point {worst}, {wo * 1000:0.0} mm off ({Vector3.Dot(u, d) * 1000:0.0} into the body), " +
                                      $"soft {rig.Surface.Soft[worst]:0.00}, touching {rig.Solver.ContactShapeA[worst] >= 0}, nearest capsule {nearName} at {near * 1000:0.0} mm");
                    // every capsule near it: how far the point and its animated place are from the surface (< 0 inside)
                    float rr = rig.Solver.Radius[worst];
                    foreach (var c in caps)
                    {
                        var a = Vector3.Transform(c.A, p[c.Bone]); var b = Vector3.Transform(c.B, p[c.Bone]);
                        float dp = Vector3.Distance(x[worst], TetShell.ClosestOnSegment(a, b, x[worst])) - c.R;
                        float dt2 = Vector3.Distance(t[worst], TetShell.ClosestOnSegment(a, b, t[worst])) - c.R;
                        if (dp > 3f * rr && dt2 > 3f * rr) continue;
                        var push = Vector3.Normalize(x[worst] - TetShell.ClosestOnSegment(a, b, x[worst]));
                        Console.WriteLine($"        {c.Name,-11} point {dp * 1000,6:0.0} mm, its place {dt2 * 1000,6:0.0} mm from the surface; masked {XpbdSolver.Ignored(c.Ignore, worst)}; " +
                                          $"push along the inward direction {Vector3.Dot(push, d):0.00}, place behind the push {-Vector3.Dot(t[worst] - x[worst], push) * 1000:0.0} mm");
                    }
                    Console.WriteLine($"        radius {rr * 1000:0.0} mm; offset {u.X * 1000:0.0}, {u.Y * 1000:0.0}, {u.Z * 1000:0.0} mm; inward {d.X:0.00}, {d.Y:0.00}, {d.Z:0.00}");
                }
            }
            if (f < 20) { baseline = MathF.Max(baseline, off); continue; }
            res.WorstMm = MathF.Max(res.WorstMm, off);
            res.WorstSpeed = MathF.Max(res.WorstSpeed, speed);
            res.WorstInverted = Math.Max(res.WorstInverted, rig.Solver.Inverted);
            if (off > 100f) res.FarFrames++;
            res.RestMm = off;
        }
        res.Resets = rig.Resets;
        return res;
    }

    static void FleshJitterTests(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh jitter (0.26.6): sudden movements and collider hits, own bone colliders around the torso");
        var w = new float[m.ShapeNames.Length];
        var s = new FleshSettings();
        int upper = m.Bone("L.UpperArm");
        float side = MathF.Sign(m.Heads[upper].X);
        Matrix4x4.Invert(m.Bind[upper], out var inv);
        var armAxis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, inv));
        int spine = m.Bone("Spine"), chest = m.Bone("Chest");
        var probe = FleshBuildRig(m, torso, new FleshSettings());
        probe.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        probe.TargetBounds(out var lo, out var hi);
        var mid = 0.5f * (lo + hi);

        // the events (all start at frame 20)
        Matrix4x4[] Rest(int f) => m.Pose(null, Matrix4x4.Identity);
        Matrix4x4[] ArmSlam(int f)
        {
            float a = f < 20 ? 0f : f < 22 ? (f - 19) / 2f : f < 50 ? 1f : f < 52 ? 1f - (f - 49) / 2f : 0f;
            return m.Pose(new Dictionary<int, Quaternion> { [upper] = Quaternion.CreateFromAxisAngle(armAxis, -side * a * 100f * MathF.PI / 180f) }, Matrix4x4.Identity);
        }
        Matrix4x4[] Bend(int f)
        {
            float a = f < 20 ? 0f : f < 50 ? 1f : 0f;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, a * 30f * MathF.PI / 180f);
            return m.Pose(new Dictionary<int, Quaternion> { [spine] = q, [chest] = q }, Matrix4x4.Identity);
        }
        var head = m.Heads[m.Bone("Neck")];
        Matrix4x4[] SnapTurn(int f)
        {
            int turns = f < 20 ? 0 : f < 50 ? 1 : 2;
            var r = Matrix4x4.CreateTranslation(-head.X, 0, -head.Z) * Matrix4x4.CreateRotationY(turns * MathF.PI / 4f) * Matrix4x4.CreateTranslation(head.X, 0, head.Z);
            return m.Pose(null, r);
        }
        Matrix4x4[] Snap(int f) => m.Pose(null, Matrix4x4.CreateTranslation(0, 0, f < 20 ? 0f : f < 50 ? 0.5f : 0f));
        // a hand (5 cm sphere) hitting the belly at 4 m/s, 4 cm deep, and straight back out
        var front = new Vector3(mid.X, mid.Y, hi.Z);
        // the ball's centre ahead of the belly's front: 20 cm, in at 6.7 cm a frame to 1 cm (4 cm into the skin), one
        // frame there, and back out at the same speed
        float Gap(int f) => f < 20 ? 0.20f : f <= 23 ? MathF.Max(0.01f, 0.20f - (f - 20) * 4f * Dt) : f <= 24 ? 0.01f : MathF.Min(0.20f, 0.01f + (f - 24) * 4f * Dt);
        Vector3 HandAt(int f) => front + new Vector3(0, 0, Gap(f));
        IEnumerable<FleshCollider> Hand(int f)
        {
            var c = HandAt(f); var sp = FleshCollider.Sphere(c, 0.05f, source: 1);
            sp.Velocity = (c - HandAt(Math.Max(0, f - 1))) / Dt;
            yield return sp;
        }

        var events = new (string name, Func<int, Matrix4x4[]> pose, Func<int, IEnumerable<FleshCollider>> extra)[]
        {
            ("arm slammed into the body (100 deg in 2 frames)", ArmSlam, null),
            ("spine and chest bent 30 deg in one frame", Bend, null),
            ("a hand hitting the belly at 4 m/s", Rest, Hand),
            ("snap turn 45 deg (twice)", SnapTurn, null),
            ("locomotion snap 0.5 m and back", Snap, null),
        };
        void Old(FleshRig r) { r.Solver.NoBounce = false; r.MaxInertialSpeed = 0f; r.Solver.OwnNoPlow = false; }
        var variants = new (string name, bool keepIn, Action<FleshRig> setup)[]
        {
            ("0.26.3", false, Old),
            ("0.26.5", true, Old),
            ("no bounce only", true, r => { r.MaxInertialSpeed = 0f; r.Solver.OwnNoPlow = false; }),
            ("speed cap only", true, r => { r.Solver.NoBounce = false; r.Solver.OwnNoPlow = false; }),
            ("own no-plow only", true, r => { r.Solver.NoBounce = false; r.MaxInertialSpeed = 0f; }),
            ("0.26.6", true, null),
        };
        var results = new Dictionary<(string, string), JitterResult>();
        foreach (var e in events)
        {
            Console.WriteLine($"  {e.name}:");
            foreach (var v in variants)
            {
                bool trace = Environment.GetEnvironmentVariable("FLESH_DIAG") == "1" && e.name.StartsWith("locomotion") && (v.name == "0.26.3" || v.name == "0.26.6");
                if (trace) Console.WriteLine($"    trace, {v.name}:");
                var res = JitterRun(m, torso, w, s, e.pose, e.extra, v.keepIn, setup: v.setup, trace: trace);
                results[(e.name, v.name)] = res;
                Console.WriteLine($"    {v.name,-15} {res}");
            }
        }
        // the resting offset with the own capsules around (gravity sags the soft belly a little)
        float rest = JitterRun(m, torso, w, s, Rest, null, true).RestMm;
        string snap = events[4].name;
        var oldSnap = results[(snap, "0.26.3")]; var newSnap = results[(snap, "0.26.6")];
        Check(oldSnap.FarFrames > 10 && oldSnap.RestMm > rest + 20f,
              $"(0.26.3 flung the skin after a 0.5 m snap: {oldSnap.FarFrames} frames with a point over 10 cm off, and {oldSnap.RestMm:0.0} mm still off a second later)");
        Check(newSnap.FarFrames == 0 && newSnap.WorstMm < 0.5f * oldSnap.WorstMm && newSnap.WorstSpeed < 0.5f * oldSnap.WorstSpeed && newSnap.RestMm < rest + 2f,
              "after a 0.5 m snap no skin point gets 10 cm off, the worst is under half of 0.26.3's and half as fast, and it settles back to rest");
        bool none = true; var worse = new List<string>();
        foreach (var e in events)
        {
            var o = results[(e.name, "0.26.3")]; var n = results[(e.name, "0.26.6")];
            if (n.WorstMm > 1.25f * o.WorstMm + 1f || n.WorstSpeed > o.WorstSpeed + 0.1f || n.WorstInverted > o.WorstInverted + 2 || n.RestMm > rest + 2f)
            { none = false; worse.Add(e.name); }
        }
        Check(none, "no sudden event is much worse than 0.26.3 (worst at most a quarter more, no faster, no more inverted tets than 2 extra) and all settle back to rest" +
                    (worse.Count > 0 ? " - not: " + string.Join("; ", worse) : ""));
        Check(results[(events[0].name, "0.26.6")].WorstMm > rest + 5f, "an arm slammed against the body still dents it (self-collision stays)");
    }
}
