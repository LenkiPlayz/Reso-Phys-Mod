using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// dotnet run -c Release --project tests -- flesh-export <file>: the torso shell on the test model and the frame of
// its biggest jiggle during the jump test, for tools/flesh_preview.py.
static partial class Program
{
    static int FleshExport(string file)
    {
        var m = FleshModel.Load(FleshModel.DefaultPath());
        var torso = m.Paint("Hips", "Spine", "Chest");
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f }, print: true);
        var w = new float[m.ShapeNames.Length];
        float best = -1; Vector3[] posed = null, still = null, simPeak = null; int bestFrame = 0;
        for (int f = 0; f < 60; f++)
        {
            float y = 0.08f * Ease(f / 7f);
            var pose = m.Pose(null, Matrix4x4.CreateTranslation(0, y, 0));
            rig.Step(Dt, pose, w);
            if (rig.MaxOffset <= best) continue;
            best = rig.MaxOffset; bestFrame = f;
            var bind = m.Pos.ToArray(); rig.AddBindOffsets(bind);
            posed = new Vector3[m.VertexCount]; still = new Vector3[m.VertexCount];
            m.Skin.SkinPositions(pose, bind, posed); m.Skin.SkinPositions(pose, m.Pos, still);
            simPeak = new Vector3[rig.Surface.Count];
            for (int i = 0; i < simPeak.Length; i++) simPeak[i] = rig.Solver.X[i];
        }
        var s = rig.Surface; var sh = rig.Shell;
        using var o = new BinaryWriter(File.Create(file));
        void V3(Vector3 v) { o.Write(v.X); o.Write(v.Y); o.Write(v.Z); }
        o.Write("SBF1".ToCharArray());
        o.Write(m.VertexCount); o.Write(m.Tri.Length / 3); o.Write(s.Count); o.Write(s.TriCount); o.Write(sh.Layers); o.Write(m.BoneCount);
        o.Write(bestFrame); o.Write(best);
        foreach (var p in m.Pos) V3(p);
        foreach (var t in m.Tri) o.Write(t);
        foreach (var v in torso) o.Write(v);
        for (int i = 0; i < s.Count; i++) V3(s.Pos[i]);
        for (int i = 0; i < s.Count; i++) V3(sh.Rest[sh.Layers * s.Count + i]);
        for (int i = 0; i < s.Count; i++) o.Write(s.Pinned[i] ? 1 : 0);
        foreach (var t in s.Tris) o.Write(t);
        foreach (var h in m.Heads) V3(h);
        foreach (var t in m.Tails) V3(t);
        foreach (var p in posed) V3(p);
        for (int v = 0; v < m.VertexCount; v++) o.Write(Vector3.Distance(posed[v], still[v]));
        foreach (var p in simPeak) V3(p);
        Console.WriteLine($"  wrote {file}: peak jiggle {best * 1000:0.0} mm at frame {bestFrame}");
        return 0;
    }
}
