using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.11.2: self-collision with the avatar's own bone colliders. Each collider ignores the skin points it already touches in
// the bind pose (a bit per point, computed once per tet build); everything else collides.
static partial class Program
{
    sealed class BoneCapsule { public int Bone; public Vector3 A, B; public float R; public ulong[] Ignore; public string Name; }

    /// <summary>VRIKAvatar-like capsules from each bone's head to its tail, in bind space (= the test model's world at rest).</summary>
    static List<BoneCapsule> AvatarCapsules(FleshModel m)
    {
        var list = new List<BoneCapsule>();
        void Add(string bone, float r)
        {
            int b = m.Bone(bone);
            if (b < 0) return;
            list.Add(new BoneCapsule { Bone = b, A = m.Heads[b], B = m.Tails[b], R = r, Name = bone });
        }
        Add("Hips", 0.05f); Add("Spine", 0.05f); Add("Chest", 0.055f); Add("Neck", 0.03f);
        Add("L.UpperArm", 0.022f); Add("L.Elbow", 0.02f); Add("R.UpperArm", 0.022f); Add("R.Elbow", 0.02f);
        Add("Left leg", 0.035f); Add("L.LowerLeg", 0.03f); Add("Right leg", 0.035f); Add("R.LowerLeg", 0.03f);
        return list;
    }

    static void FleshSelfCollision(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh self-collision (0.11.2): own bone colliders, minus what they touch at rest");
        var w = new float[m.ShapeNames.Length];
        var caps = AvatarCapsules(m);
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        rig.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        int ignoredPairs = 0, withMask = 0;
        foreach (var c in caps)
        {
            c.Ignore = rig.TouchingAtRest(FleshCollider.Capsule(c.A, c.B, c.R));
            if (c.Ignore == null) continue;
            withMask++;
            foreach (var bits in c.Ignore) ignoredPairs += System.Numerics.BitOperations.PopCount(bits);
        }
        Console.WriteLine($"  {caps.Count} bone capsules; {withMask} touch the soft shell at rest, {ignoredPairs} point pairs ignored " +
                          $"({string.Join(", ", caps.Where(c => c.Ignore != null).Select(c => c.Name))})");

        // the capsules in world space, from each bone's skin matrix (bind space to world)
        List<FleshCollider> World(Matrix4x4[] skin, bool masks, Func<BoneCapsule, bool> which = null)
        {
            var l = new List<FleshCollider>();
            foreach (var c in caps)
            {
                if (which != null && !which(c)) continue;
                var col = FleshCollider.Capsule(Vector3.Transform(c.A, skin[c.Bone]), Vector3.Transform(c.B, skin[c.Bone]), c.R);
                col.Source = 2;
                if (masks) col.Ignore = c.Ignore;
                l.Add(col);
            }
            return l;
        }
        (float offset, int contacts) RunRest(bool masks)
        {
            var r = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
            var pose = m.Pose(null, Matrix4x4.Identity);
            r.Step(Dt, pose, w);
            float worst = 0; int touching = 0;
            for (int f = 0; f < 60; f++)
            {
                r.Solver.Colliders.Clear(); r.Solver.Colliders.AddRange(World(pose, masks));
                r.Step(Dt, pose, w);
                worst = MathF.Max(worst, r.MaxOffset); touching = Math.Max(touching, r.Solver.Contacts);
            }
            return (worst, touching);
        }
        var bare = RunRest(false);
        var masked = RunRest(true);
        Console.WriteLine($"  at rest with the own capsules: without masks the flesh is pushed {bare.offset * 1000:0.0} mm ({bare.contacts} points); " +
                          $"with them {masked.offset * 1000:0.000} mm ({masked.contacts} points)");
        Check(withMask > 0 && bare.offset > 2e-3f, "the avatar's own torso capsules sit inside its flesh (without masks they would push it out)");
        Check(masked.offset < 1e-4f && masked.contacts == 0, "with the at-rest masks, the own colliders leave the resting flesh alone");

        // an arm swung down against the body presses into the flesh; the torso's own capsules still do not
        int upper = m.Bone("L.UpperArm");
        float side = MathF.Sign(m.Heads[upper].X);
        Matrix4x4.Invert(m.Bind[upper], out var inv);
        var localAxis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, inv));
        (float offset, int contacts) RunArm(Func<BoneCapsule, bool> which, float degrees)
        {
            var r = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
            r.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
            float worst = 0; int touching = 0;
            for (int f = 0; f < 90; f++)
            {
                float ang = -side * Ease(f / 30f) * degrees * MathF.PI / 180f;
                var pose = m.Pose(new Dictionary<int, Quaternion> { [upper] = Quaternion.CreateFromAxisAngle(localAxis, ang) }, Matrix4x4.Identity);
                r.Solver.Colliders.Clear(); r.Solver.Colliders.AddRange(World(pose, true, which));
                r.Step(Dt, pose, w);
                if (f >= 30) { worst = MathF.Max(worst, r.MaxOffset); touching = Math.Max(touching, r.Solver.Contacts); }
            }
            return (worst, touching);
        }
        bool IsArm(BoneCapsule c) => c.Name.StartsWith("L.");
        float angle = 0; (float offset, int contacts) arm = default;
        foreach (float deg in new[] { 60f, 75f, 90f, 105f })
        {
            arm = RunArm(_ => true, deg);
            angle = deg;
            if (arm.contacts > 0) break;
        }
        var torsoOnly = RunArm(c => !IsArm(c), angle);
        Console.WriteLine($"  left arm swung down {angle:0} deg against the body: pressed in {arm.offset * 1000:0.0} mm, {arm.contacts} points touching; " +
                          $"the same pose with only the torso and leg capsules: {torsoOnly.offset * 1000:0.000} mm, {torsoOnly.contacts} points");
        Check(arm.contacts > 0 && arm.offset > 1e-3f, "an arm brought against the body presses into its flesh (self-collision)");
        Check(torsoOnly.contacts == 0, "and it is the arm doing it: the body's own capsules still ignore the flesh they sit in");
    }
}
