using System.Numerics;
using Softbody.Core;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// 0.13.0: hue layers, separate simulations with their own settings on one mesh, picked by the paint's hue.
static partial class Program
{
    static void FleshLayersTest(FleshModel m)
    {
        Console.WriteLine("TEST flesh hue layers (0.13.0): separate simulations on one mesh, picked by the paint's hue");

        // ---- the split, on single colours
        (float r, float g, float b)[] cols =
        {
            (1f, 1f, 1f),      // white: base
            (1f, 0.5f, 0f),    // orange (30 degrees)
            (1f, 0.75f, 0f),   // 45 degrees: half orange, half yellow (yellow owned by nobody: the base)
            (0f, 0f, 0f),      // black: firm everywhere
            (0.5f, 0.25f, 0f), // dark orange: the orange layer, half soft
            (1f, 0f, 0f),      // red: no layer owns it, the base
            (0f, 1f, 0f),      // green
        };
        int n = cols.Length;
        float[] Soft(SoftnessSource src) { var a = new float[n]; for (int v = 0; v < n; v++) a[v] = SoftnessMap.FromColor(cols[v].r, cols[v].g, cols[v].b, 1f, src, false); return a; }
        var bands = new float[n * HueBands.Count];
        for (int v = 0; v < n; v++) HueBands.Weights(cols[v].r, cols[v].g, cols[v].b, bands.AsSpan(v * HueBands.Count, HueBands.Count));
        var bright = Soft(SoftnessSource.Value);
        var maps = FleshLayers.Split(Soft(SoftnessSource.Value), bright, bands, new[] { -1, 1 });
        bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;
        Console.WriteLine("  base / orange layer softness: " + string.Join(", ", Enumerable.Range(0, n).Select(v => $"({cols[v].r:0.##},{cols[v].g:0.##},{cols[v].b:0.##}) {maps[0][v]:0.##}/{maps[1][v]:0.##}")));
        Check(Near(maps[0][0], 1f) && Near(maps[1][0], 0f) && Near(maps[0][1], 0f) && Near(maps[1][1], 1f) &&
              Near(maps[0][2], 0.5f) && Near(maps[1][2], 0.5f) && Near(maps[0][3], 0f) && Near(maps[1][3], 0f) &&
              Near(maps[0][4], 0f) && Near(maps[1][4], 0.5f) && Near(maps[0][5], 1f) && Near(maps[1][5], 0f),
              "the paint's hue picks the layer and its brightness how soft: white and unowned hues stay with the base, a colour between two hues is shared");
        var redBase = FleshLayers.Split(Soft(SoftnessSource.Red), bright, bands, new[] { -1, 3 });
        Check(Near(redBase[0][6], 0f) && Near(redBase[1][6], 1f) && Near(redBase[0][5], 1f),
              "with a Red channel base, green paint still goes to a green layer (the layers read brightness, not the base's channel)");
        var twice = FleshLayers.Split(Soft(SoftnessSource.Value), bright, bands, new[] { -1, 1, 1 });
        Check(Near(twice[1][1], 1f) && twice[2].All(x => x == 0f), "a hue owned by two layers goes to the first");

        // ---- two layers on the body: a white belly (stiff) and an orange chest (wobbly), blending through a saturation
        // gradient where they meet
        var belly = m.Paint("Hips", "Spine"); var chest = m.Paint("Chest");
        int nv = m.VertexCount;
        var bb = new float[nv * HueBands.Count]; var br = new float[nv]; var bs = new float[nv];
        int gradient = 0;
        for (int v = 0; v < nv; v++)
        {
            float a = belly[v], c = chest[v], sum = a + c;
            if (!(sum > 0f)) continue;
            float sat = c / sum;                       // 0 white .. 1 orange
            if (sat > 0.05f && sat < 0.95f) gradient++;
            float val = MathF.Max(a, c);
            // white to orange at the same brightness: (1, 1 - sat/2, 1 - sat) x val
            float r = val, g = val * (1f - 0.5f * sat), b = val * (1f - sat);
            br[v] = val; bs[v] = val;
            HueBands.Weights(r, g, b, bb.AsSpan(v * HueBands.Count, HueBands.Count));
        }
        var body = FleshLayers.Split(bs, br, bb, new[] { -1, 1, 3 });  // base, orange, and a green layer nobody painted
        var sBase = new FleshSettings { Stiffness = 20f, Follow = 3f };
        var sOrange = new FleshSettings { Stiffness = 1f, Follow = 0.6f, SimPoints = 400, Damping = 1f };
        var rigBase = FleshBuildRig(m, body[0], sBase);
        var rigOrange = FleshBuildRig(m, body[1], sOrange);
        var rigGreen = FleshRig.Build(Input(m, body[2]), new FleshSettings(), out var greenReport);
        var mvB = rigBase.MovingVertices.ToArray(); var mvO = rigOrange.MovingVertices.ToArray();
        var setB = mvB.ToHashSet(); var setO = mvO.ToHashSet();
        int shared = setB.Count(setO.Contains);
        Console.WriteLine($"  belly (white, base) {rigBase.Surface.SoftCount} points, {mvB.Length} moving vertices; chest (orange) {rigOrange.Surface.SoftCount} points, " +
                          $"{mvO.Length} moving vertices; {shared} moved by both ({gradient} vertices painted in the gradient); green: {(rigGreen == null ? "idle (" + greenReport + ")" : "built?")}");
        Check(rigBase != null && rigOrange != null && mvB.Length > 0 && mvO.Length > 0 && shared > 0 && shared < Math.Min(mvB.Length, mvO.Length) / 2,
              "each layer gets its own region (the gradient where they meet is moved by both)");
        Check(rigGreen == null, "a layer whose hue is not painted anywhere stays idle");

        // jiggle both with the same motion, each with its own settings
        var w = new float[m.ShapeNames.Length];
        var posB = new Vector3[mvB.Length]; var nrmB = new Vector3[mvB.Length];
        var posO = new Vector3[mvO.Length]; var nrmO = new Vector3[mvO.Length];
        double offB = 0, offO = 0;
        for (int f = 0; f < 50; f++)
        {
            var pose = m.Pose(null, Matrix4x4.CreateTranslation(0, f < 10 ? 0.05f * f / 10f : 0.05f, 0));
            rigBase.Step(Dt, pose, w); rigOrange.Step(Dt, pose, w);
            if (f >= 10) { offB += rigBase.MaxOffset; offO += rigOrange.MaxOffset; }
        }
        rigBase.FillOutput(m.Pos, m.Nrm, posB, nrmB);
        rigOrange.FillOutput(m.Pos, m.Nrm, posO, nrmO);
        Console.WriteLine($"  a 5 cm lift: the stiff belly lags by up to {offB / 40 * 1000:0.0} mm on average, the wobbly chest {offO / 40 * 1000:0.0} mm");
        Check(offO > 1.5 * offB, "the layers run separately with their own settings: the softer, looser chest swings further than the stiff belly");

        // the merge: one layer's vertices exactly its output; shared ones the sum of both offsets
        var merge = new FleshOutputMerge();
        merge.Begin(nv);
        merge.Add(mvB, posB, nrmB, m.Pos, m.Nrm);
        merge.Add(mvO, posO, nrmO, m.Pos, m.Nrm);
        merge.End(m.Nrm);
        var idxB = new Dictionary<int, int>(); for (int k = 0; k < mvB.Length; k++) idxB[mvB[k]] = k;
        var idxO = new Dictionary<int, int>(); for (int k = 0; k < mvO.Length; k++) idxO[mvO[k]] = k;
        int exact = 0, single = 0; float worstSum = 0, worstLen = 0;
        foreach (int v in merge.Vertices)
        {
            bool inB = idxB.TryGetValue(v, out int kb), inO = idxO.TryGetValue(v, out int ko);
            if (inB != inO)
            {
                single++;
                var p = inB ? posB[kb] : posO[ko]; var q = inB ? nrmB[kb] : nrmO[ko];
                if (merge.Position(v) == p && merge.Normal(v) == q) exact++;
                continue;
            }
            var want = m.Pos[v] + (posB[kb] - m.Pos[v]) + (posO[ko] - m.Pos[v]);
            worstSum = MathF.Max(worstSum, Vector3.Distance(merge.Position(v), want));
            worstLen = MathF.Max(worstLen, MathF.Abs(merge.Normal(v).Length() - m.Nrm[v].Length()));
        }
        Console.WriteLine($"  merged: {merge.Vertices.Length} vertices ({single} moved by one layer, {merge.Shared} by both); shared ones off the sum of offsets by {worstSum * 1e6:0.0} um, normals' length by {worstLen:0.0e0}");
        Check(merge.Vertices.Length == setB.Union(setO).Count() && exact == single && merge.Shared == shared && worstSum < 1e-5f && worstLen < 1e-4f,
              "the upload merges the layers: a vertex one layer moves shows exactly that layer, a shared one the sum of both");

        // Simulating: what is shown is that fraction of the same simulation (the simulation itself is not changed)
        var full = FleshBuildRig(m, belly, new FleshSettings());
        var half = FleshBuildRig(m, belly, new FleshSettings());
        float worstHalf = 0f, peak = 0f;
        for (int f = 0; f < 30; f++)
        {
            var pose = m.Pose(null, Matrix4x4.CreateTranslation(0, 0.05f * MathF.Sin(f * 0.3f), 0));
            full.OutputGain = 1f; half.OutputGain = 0.5f;
            full.Step(Dt, pose, w); half.Step(Dt, pose, w);
            var a = full.BindOffsets; var h = half.BindOffsets;
            for (int k = 0; k < a.Length; k++) { worstHalf = MathF.Max(worstHalf, Vector3.Distance(h[k], 0.5f * a[k])); peak = MathF.Max(peak, a[k].Length()); }
        }
        Console.WriteLine($"  Simulating 0.5: shown offsets off half the full ones by {worstHalf * 1e6:0.0} um at most (full offsets up to {peak * 1000:0.0} mm)");
        Check(peak > 1e-3f && worstHalf < 1e-6f, "Simulating scales what is shown, not the simulation: at 0.5 exactly half of the same jiggle");
    }
}
