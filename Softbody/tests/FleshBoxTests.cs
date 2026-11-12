using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.12.1: a box pressed into the belly (Lenki's screenshots: a bowed, creased patch with a gap, and spikes at the box's edge).
static partial class Program
{
    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var axis = Vector3.Cross(a, b); float w = 1f + Vector3.Dot(a, b);
        if (w < 1e-6f) return Quaternion.CreateFromAxisAngle(MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, MathF.PI);
        return Quaternion.Normalize(new Quaternion(axis, w));
    }

    static void FleshBoxPress(FleshModel m, float[] torso)
    {
        Console.WriteLine("TEST flesh box press (0.12.1): a 10 cm box face pressed 1 cm into the belly (deep shell)");
        var w = new float[m.ShapeNames.Length];
        (float flat, float gap, float nErr, int patch, float edgeSide, float edgeMax) Run(bool boxExit, bool project, float shift, float press = 0.01f, int only = 0)
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f, Stiffness = 2f, Follow = 0.5f, VolumePreservation = 0f, Depth = 0.9f });
            rig.Solver.BoxExitInward = boxExit; rig.CloseContactGap = project && only != 2; rig.FlattenContacts = project && only != 1; rig.FitContacts = project && only == 0;
            var pose = m.Pose(null, Matrix4x4.Identity);
            rig.Step(Dt, pose, w);
            int i0 = -1; float best = float.MinValue;
            for (int i = 0; i < rig.Surface.Count; i++)
            {
                if (rig.Shell.Kinematic[i] || rig.Solver.Inward[i] == Vector3.Zero) continue;
                var t = rig.Targets[i];
                // the soft middle of the belly: fully soft, away from the pinned border
                if (MathF.Abs(t.X) > 0.03f || rig.Surface.Soft[i] < 0.9f || rig.Surface.BorderDistance[i] <= 3) continue;
                if (t.Z > best) { best = t.Z; i0 = i; }
            }
            var p0 = rig.Targets[i0]; var nOut = -rig.Solver.Inward[i0];
            var q = FromTo(Vector3.UnitZ, nOut);
            var tx = Vector3.Transform(Vector3.UnitX, q); var ty = Vector3.Transform(Vector3.UnitY, q);
            var half = new Vector3(0.05f, 0.05f, 0.1f);
            float r0 = rig.Solver.Radius[i0];
            Vector3 center = p0 + nOut * (half.Z + r0 + 0.02f) + tx * shift;
            for (int f = 0; f < 60; f++)
            {
                float pen = -0.02f + (0.02f + press) * Ease(f / 30f);
                var prev = center;
                center = p0 + nOut * (half.Z + r0 - pen) + tx * shift;
                var box = FleshCollider.Box(center, q, half);
                box.Velocity = (center - prev) / Dt;
                rig.Solver.Colliders.Clear(); rig.Solver.Colliders.Add(box);
                rig.Step(Dt, pose, w);
            }
            int k = rig.MovingVertices.Length;
            var outPos = new Vector3[k]; var outNrm = new Vector3[k];
            rig.FillOutput(m.Pos, m.Nrm, outPos, outNrm);
            var face = center - nOut * half.Z;
            double sum = 0, sum2 = 0, err = 0; int n = 0;
            float edgeSide = 0, edgeMax = 0;
            for (int i = 0; i < k; i++)
            {
                // chosen by where the vertex is at rest, so every variant measures the same vertices
                var rest = m.Pos[rig.MovingVertices[i]];
                var rel = rest - face;
                float lx = Vector3.Dot(rel, tx), ly = Vector3.Dot(rel, ty);
                float restGap = Vector3.Dot(face - rest, nOut);   // < 0: at rest it would be inside the box
                var p = outPos[i];
                float gap = Vector3.Dot(face - p, nOut);           // 0 = on the face; > 0 in front of it; < 0 inside the box
                if (MathF.Abs(lx) < half.X - 0.01f && MathF.Abs(ly) < half.Y - 0.01f && restGap < -0.002f)
                {
                    sum += gap; sum2 += gap * gap; n++;
                    err += MathF.Acos(Math.Clamp(Vector3.Dot(outNrm[i], nOut), -1f, 1f)) * 180f / MathF.PI;
                }
                // just past the box's side edge, where the box (shifted sideways) crosses the belly
                if (shift != 0f && lx < -half.X && lx > -half.X - 0.03f && MathF.Abs(ly) < half.Y && restGap > -0.03f && restGap < 0.02f)
                {
                    var off = p - rest;
                    float side = (off - nOut * Vector3.Dot(off, nOut)).Length();
                    edgeSide = MathF.Max(edgeSide, side); edgeMax = MathF.Max(edgeMax, off.Length());
                }
            }
            if (shift != 0f)
            {
                // spikes: how much the mesh's own edges near the box's edge are stretched or squashed
                var idx = new Dictionary<int, int>();
                for (int i = 0; i < k; i++) idx[rig.MovingVertices[i]] = i;
                float worst = 0f; edgeSide = 0f;
                for (int t = 0; t + 2 < m.Tri.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int va = m.Tri[t + e], vb2 = m.Tri[t + (e + 1) % 3];
                        var ra = m.Pos[va]; var rb = m.Pos[vb2];
                        var mid = 0.5f * (ra + rb) - face;
                        float mx = Vector3.Dot(mid, tx), my = Vector3.Dot(mid, ty), mg = Vector3.Dot(face - 0.5f * (ra + rb), nOut);
                        if (MathF.Abs(mx + half.X) > 0.03f || MathF.Abs(my) > half.Y || mg < -0.04f || mg > 0.02f) continue;
                        var pa = idx.TryGetValue(va, out int ia) ? outPos[ia] : ra;
                        var pb = idx.TryGetValue(vb2, out int ib) ? outPos[ib] : rb;
                        float rl = Vector3.Distance(ra, rb);
                        if (rl < 1e-5f) continue;
                        // a spike is an edge stretched (or squashed) by millimetres; a 0.4 mm edge in a navel doubling is not visible
                        float st = MathF.Abs(Vector3.Distance(pa, pb) - rl);
                        worst = MathF.Max(worst, st);
                        if (rl > 2e-3f) edgeSide = MathF.Max(edgeSide, MathF.Abs(Vector3.Distance(pa, pb) / rl - 1f));
                    }
                edgeMax = worst;
            }
            float mean = n > 0 ? (float)(sum / n) : 0, sd = n > 0 ? (float)Math.Sqrt(Math.Max(0, sum2 / n - mean * mean)) : 0;
            return (sd, mean, n > 0 ? (float)(err / n) : 0, n, edgeSide, edgeMax);
        }
        var before = Run(false, false, 0f);
        var after = Run(true, true, 0f);
        Console.WriteLine($"  flat patch, 0.12.0: {before.patch} render vertices, bowed by {before.flat * 1000:0.00} mm (std), gap {before.gap * 1000:0.00} mm, normals off the face by {before.nErr:0.0} deg");
        Console.WriteLine($"  flat patch, 0.12.1: {after.patch} render vertices, bowed by {after.flat * 1000:0.00} mm (std), gap {after.gap * 1000:0.00} mm, normals off the face by {after.nErr:0.0} deg");
        Check(before.gap > 1e-3f && MathF.Abs(after.gap) < 0.5e-3f && after.flat < 0.6f * before.flat && after.nErr <= before.nErr,
              "the skin pressed against a box is shown flat against its face: no gap and not sunk into it (within half a millimetre), much less bowed, shading as flat");
        var eb = Run(false, false, 0.05f, 0.02f);
        var ex = Run(true, false, 0.05f, 0.02f);
        var ep = Run(false, true, 0.05f, 0.02f);
        var ea = Run(true, true, 0.05f, 0.02f);
        Console.WriteLine($"  box edge across the belly, pressed 2 cm: the mesh's edges near it change length by up to {eb.edgeMax * 1000:0.0} mm (0.12.0), " +
                          $"{ex.edgeMax * 1000:0.0} mm (exit toward the body), {ep.edgeMax * 1000:0.0} mm (gap closed and flattened), {ea.edgeMax * 1000:0.0} mm (both, 0.12.1); " +
                          $"edges over 2 mm stretched by up to {eb.edgeSide * 100:0} %, {ex.edgeSide * 100:0} %, {ep.edgeSide * 100:0} %, {ea.edgeSide * 100:0} %");
        if (Environment.GetEnvironmentVariable("FLESH_DIAG") == "1")
        {
            var g1 = Run(true, true, 0.05f, 0.02f, 1); var g2 = Run(true, true, 0.05f, 0.02f, 2);
            var g3 = Run(false, true, 0.05f, 0.02f, 1); var g4 = Run(false, true, 0.05f, 0.02f, 2);
            Console.WriteLine($"    diag edge: exit+gap only {g1.edgeMax * 1000:0.0} mm {g1.edgeSide * 100:0} %, exit+flatten only {g2.edgeMax * 1000:0.0} mm {g2.edgeSide * 100:0} %, gap only {g3.edgeMax * 1000:0.0} mm {g3.edgeSide * 100:0} %, flatten only {g4.edgeMax * 1000:0.0} mm {g4.edgeSide * 100:0} %");
            var f1 = Run(true, true, 0f, 0.01f, 1); var f2 = Run(true, true, 0f, 0.01f, 2);
            Console.WriteLine($"    diag flat: gap only bowed {f1.flat * 1000:0.00} gap {f1.gap * 1000:0.00}; flatten only bowed {f2.flat * 1000:0.00} gap {f2.gap * 1000:0.00}");
        }
        // (the worst ratio is in a 3 cm deep navel the box's edge crosses: its floor is flattened a few mm, its walls not)
        Check(ea.edgeMax <= eb.edgeMax + 0.5e-3f,
              "no spikes at a box's edge: the gap is closed and the skin flattened smoothly, no edge near it changes length by more than before");
    }
}
