using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.11.3: the stack overflow that took Resonite down (a stackalloc in a per-tet loop), and collision radii that follow the
// animated, blendshaped skin.
static partial class Program
{
    static void Flesh0113(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh 0.11.3: big builds on a small stack, collision radii from the animated skin");
        // 4000 points, 3 layers (about 70,000 tets) on a 256 KB stack: 0.11.2 used ~48 bytes of stack per tet here
        FleshRig big = null; Exception err = null;
        var th = new Thread(() =>
        {
            try { big = FleshRig.Build(Input(m, torso), new FleshSettings { SimPoints = 4000, Layers = 3 }, out _); }
            catch (Exception ex) { err = ex; }
        }, 256 * 1024);
        th.Start(); th.Join();
        Console.WriteLine($"  4000 points, 3 layers on a 256 KB thread: {(big != null ? $"{big.Surface.SoftCount} soft points, {big.Shell.TetCount} tets, built in {big.BuildMs:0} ms" : "failed: " + err?.Message)}");
        Check(big != null && big.Shell.TetCount > 30000, "a big shell builds on a small stack (no stack memory per tet: the crash at Layers 0 / SimPoints 4000)");

        var w = new float[m.ShapeNames.Length];
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        rig.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        var surf = rig.Surface;
        float Expected(int i, ReadOnlySpan<Vector3> t)
        {
            float sum = 0; int c = 0;
            for (int k = surf.NbrStart[i]; k < surf.NbrStart[i + 1]; k++) { sum += Vector3.Distance(t[i], t[surf.Nbr[k]]); c++; }
            return 0.5f * sum / c;
        }
        float worst = 0; int colliding = 0, lonely = 0;
        for (int i = 0; i < surf.Count; i++)
        {
            if (rig.Shell.Kinematic[i]) { worst = MathF.Max(worst, rig.Solver.Radius[i]); continue; }
            if (surf.NbrStart[i + 1] == surf.NbrStart[i]) { lonely++; continue; } // no triangle left after the reduction
            colliding++;
            worst = MathF.Max(worst, MathF.Abs(rig.Solver.Radius[i] - Expected(i, rig.Targets)));
        }
        Console.WriteLine($"  radius vs half the animated neighbour spacing: worst difference {worst * 1e6:0.000} um over {colliding} points ({lonely} without neighbours keep theirs)");
        Check(colliding > 0 && worst < 1e-6f, "each simulated skin point's radius is half its mean distance to its neighbours where the animation puts them");
        var restR = rig.Solver.Radius.ToArray();

        // the avatar scaled up: radii scale with it
        rig.Step(Dt, m.Pose(null, Matrix4x4.CreateScale(1.7f)), w);
        rig.Step(Dt, m.Pose(null, Matrix4x4.CreateScale(1.7f)), w);
        double ratio = 0; int n = 0;
        for (int i = 0; i < surf.Count; i++) if (restR[i] > 0) { ratio += rig.Solver.Radius[i] / restR[i]; n++; }
        ratio /= n;
        // the blendshape that moves the most soft torso vertices, on
        int best = -1, bestCount = 0;
        for (int s = 0; s < m.ShapeNames.Length; s++)
        {
            int c = 0;
            for (int k = 0; k < m.ShapeVerts[s].Length; k++) if (torso[m.ShapeVerts[s][k]] > 0.5f && m.ShapeDeltas[s][k].Length() > 1e-3f) c++;
            if (c > bestCount) { bestCount = c; best = s; }
        }
        var r2 = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        r2.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        var before = r2.Solver.Radius.ToArray();
        var wb = new float[m.ShapeNames.Length]; wb[best] = 1f;
        r2.Step(Dt, m.Pose(null, Matrix4x4.Identity), wb);
        int changed = 0; float most = 0;
        for (int i = 0; i < surf.Count; i++)
        {
            if (!(before[i] > 0f)) continue;
            float d = MathF.Abs(r2.Solver.Radius[i] - before[i]) / before[i];
            if (d > 0.05f) changed++;
            most = MathF.Max(most, d);
        }
        Console.WriteLine($"  radii: x{ratio:0.000} on an avatar scaled x1.7; blendshape \"{Ascii(m.ShapeNames[best])}\" changes {changed} points' radius by over 5 % (up to {most * 100:0} %)");
        Check(MathF.Abs((float)ratio - 1.7f) < 0.01f, "collision radii scale with the avatar");
        Check(changed > 10, "collision radii follow a blendshape that moves the skin");
    }
}
