using System.Diagnostics;
using System.Numerics;
using Softbody.Core;
using Softbody.Core.Flesh;

namespace Softbody.Tests;

// Phase 1 of DESIGN-skinned.md: the tet shell + XPBD flesh, on a real avatar body (Kitavali, converted with
// tools/fbx_to_sbm.py into tests/data; not shipped). Skipped when the file is missing.
static partial class Program
{
    static void FleshTests()
    {
        Console.WriteLine("TEST flesh (tet shell + XPBD) on a real skinned body");
        var path = FleshModel.DefaultPath();
        if (path == null || !File.Exists(path)) { Console.WriteLine("  (skipped: no test model at " + path + ")"); return; }
        var sw = Stopwatch.StartNew();
        var m = FleshModel.Load(path);
        Console.WriteLine($"  model: {m.VertexCount} render vertices ({m.ControlPoints} control points), {m.Tri.Length / 3} triangles, " +
                          $"{m.BoneCount} bones, {m.ShapeNames.Length} blendshapes; loaded in {sw.ElapsedMilliseconds} ms");

        var rest = m.Pose(null, Matrix4x4.Identity);
        float worst = 0;
        for (int b = 0; b < rest.Length; b++)
        {
            var d = rest[b] - Matrix4x4.Identity;
            worst = MathF.Max(worst, MathF.Max(MathF.Abs(d.M11) + MathF.Abs(d.M22) + MathF.Abs(d.M33), d.Translation.Length()));
        }
        Check(worst < 1e-4f, $"the test rig's rest pose gives identity skin matrices (worst {worst:0.0e0})");

        var torso = m.Paint("Hips", "Spine", "Chest");
        FleshBuild(m, torso);
        FleshRest(m, torso);
        FleshGravity(m, torso);
        FleshBend(m, torso);
        FleshBlendshape(m, torso);
        FleshJiggle(m, torso);
        FleshBindRoundTrip(m, torso);
        FleshRuntimePieces(m, torso);
        FleshCollisionTests(m, torso);
        FleshTiming(m, torso);
        FleshLayersTest(m);
    }

    /// <summary>Phase 2 pieces the plugin uses: normals of the moved vertices, teleports, live tuning, colliders, the debug view.</summary>
    static void FleshRuntimePieces(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        var w = new float[m.ShapeNames.Length];
        int k = rig.MovingVertices.Length;
        var outPos = new Vector3[k]; var outNrm = new Vector3[k];
        rig.Step(Dt, m.Pose(null, Matrix4x4.Identity), w);
        rig.FillOutput(m.Pos, m.Nrm, outPos, outNrm);
        float worstN = 0, worstP = 0;
        for (int i = 0; i < k; i++)
        {
            int v = rig.MovingVertices[i];
            worstN = MathF.Max(worstN, Vector3.Distance(outNrm[i], m.Nrm[v]));
            worstP = MathF.Max(worstP, Vector3.Distance(outPos[i], m.Pos[v]));
        }
        Check(worstN < 1e-3f && worstP < 1e-4f, $"at rest the output is the mesh's own positions and normals (worst {worstP * 1000:0.000} mm, normal {worstN:0.0000})");
        for (int f = 0; f < 8; f++) rig.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, 0.08f * Ease(f / 7f), 0)), w);
        rig.FillOutput(m.Pos, m.Nrm, outPos, outNrm);
        bool unit = true;
        var turns = new float[k];
        for (int i = 0; i < k; i++)
        {
            int v = rig.MovingVertices[i];
            unit &= MathF.Abs(outNrm[i].Length() - 1f) < 1e-3f;
            turns[i] = MathF.Acos(Math.Clamp(Vector3.Dot(outNrm[i], Vector3.Normalize(m.Nrm[v])), -1f, 1f)) * 180f / MathF.PI;
        }
        var sorted = turns.OrderBy(t => t).ToArray();
        int worstI = Array.IndexOf(turns, sorted[^1]);
        Console.WriteLine($"  normals at the peak of the jump: turned by median {sorted[k / 2]:0.0}, 99th {sorted[(int)(k * 0.99)]:0.0}, max {sorted[^1]:0.0} deg " +
                          $"({turns.Count(t => t > 45f)} of {k} over 45 deg; the worst at softness {torso[rig.MovingVertices[worstI]]:0.00}, offset {rig.BindOffsets[worstI].Length() * 1000:0.0} mm); " +
                          $"{rig.Normals.TriangleCount} triangles recomputed");
        if (Environment.GetEnvironmentVariable("FLESH_DIAG") == "1")
        {
            var surf = rig.Surface;
            foreach (int i in Enumerable.Range(0, k).OrderByDescending(i => turns[i]).Take(8))
            {
                int v = rig.MovingVertices[i];
                int near = 0; float nd = float.MaxValue;
                for (int j = 0; j < surf.Count; j++) { float d = Vector3.Distance(surf.Pos[j], m.Pos[v]); if (d < nd) { nd = d; near = j; } }
                float thick = rig.Shell.Thickness[near] / surf.VertexEdge[near];
                // local edge length of the render mesh around v
                float le = 0; int ce = 0;
                for (int t = 0; t < m.Tri.Length; t += 3)
                    if (m.Tri[t] == v || m.Tri[t + 1] == v || m.Tri[t + 2] == v) { le += Vector3.Distance(m.Pos[m.Tri[t]], m.Pos[m.Tri[t + 1]]); ce++; }
                Console.WriteLine($"    turn {turns[i]:0} deg at ({m.Pos[v].X:0.000},{m.Pos[v].Y:0.000},{m.Pos[v].Z:0.000}) render edge {le / Math.Max(1, ce) * 1000:0.0} mm, " +
                                  $"offset {rig.BindOffsets[i].Length() * 1000:0.0} mm; nearest sim point {nd * 1000:0.0} mm away, shell {thick:0.00} edges, " +
                                  $"its offset {rig.Offset(near).Length() * 1000:0.0} mm, normal y {m.Nrm[v].Y:0.00} z {m.Nrm[v].Z:0.00}");
            }
        }
        Check(unit && sorted[k / 2] > 0.05f && sorted[(int)(k * 0.99)] < 30f && turns.Count(t => t > 60f) <= k / 200,
              "the moved vertices get unit normals that follow the wobble (99 % turn under 30 deg, at most 0.5 % over 60 deg in a 3 g hop)");

        // a teleport: put back at rest instead of flung
        rig.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(3f, 0.08f, 0)), w);
        Console.WriteLine($"  teleport 3 m: resets {rig.Resets} ({rig.LastResetReason}), offset after {rig.MaxOffset * 1000:0.000} mm");
        Check(rig.Resets == 1 && rig.MaxOffset < 1e-4f, "a teleport puts the flesh back at rest on the new pose");

        // live tuning keeps the structure
        rig.Tune(new FleshSettings { Stiffness = 30f, SimPoints = 50, Gravity = 0f });
        Check(rig.Settings.Stiffness == 30f && rig.Settings.SimPoints == new FleshSettings().SimPoints, "live tuning takes stiffness but not the structural settings");

        // a box under the body pushes the skin points out
        var boxRig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        var pose = m.Pose(null, Matrix4x4.Identity);
        boxRig.Step(Dt, pose, w);
        boxRig.TargetBounds(out var lo, out var hi);
        var front = new Vector3(0, 0.5f * (lo.Y + hi.Y), hi.Z);   // the belly side
        boxRig.Solver.Colliders.Add(FleshCollider.Box(front + new Vector3(0, 0, 0.05f - 0.012f), Quaternion.Identity, new Vector3(0.04f, 0.04f, 0.05f)));
        int contacts = 0;
        for (int f = 0; f < 30; f++) { boxRig.Step(Dt, pose, w); contacts = Math.Max(contacts, boxRig.Solver.Contacts); }
        Console.WriteLine($"  a box pressed 12 mm into the belly: {contacts} skin points touching, pushed in by up to {boxRig.MaxOffset * 1000:0.0} mm");
        Check(contacts > 0 && boxRig.MaxOffset > 5e-3f, "a box collider dents the flesh");
        var x = new Vector3(0.01f, 0.0f, 0.0f);
        bool pushed = XpbdSolver.PushOutOfBox(ref x, Vector3.Zero, Quaternion.Identity, new Vector3(0.05f), 0.01f);
        Check(pushed && MathF.Abs(x.X - 0.06f) < 1e-5f, "a point inside a box leaves through the nearest face");

        // the debug view
        var snap = new FleshDebugSnapshot();
        boxRig.FillDebug(snap, tets: true, colliders: true);
        var g = new DebugGeometry();
        var counts = FleshDebugView.Build(snap, g);
        Console.WriteLine($"  debug view: {counts.Lines} tet edges, {counts.Offsets} offset lines, {counts.Spheres} spheres ({counts.Touching} touching), {counts.Proxies} colliders, {counts.Vertices} vertices");
        Check(counts.Lines == boxRig.TetEdges.Length / 2 && counts.Spheres > 0 && counts.Touching > 0 && counts.Proxies == 1 && !counts.Truncated,
              "the debug view draws every tet edge, the skin points' spheres (some touching) and the box");
    }

    static FleshInput Input(FleshModel m, float[] soft) => new()
    {
        Positions = m.Pos, Normals = m.Nrm, Triangles = m.Tri, Softness = soft,
        Skin = m.Skin, Blendshapes = m.Blend, BoneHeads = m.Heads, BoneTails = m.Tails,
    };

    static FleshRig FleshBuildRig(FleshModel m, float[] soft, FleshSettings s, bool print = false)
    {
        var rig = FleshRig.Build(Input(m, soft), s, out var report);
        if (print) Console.WriteLine("  " + report);
        // SOFTBODY_KEEPIN=0: the whole suite without 0.26.4's let-go, to compare against; =out or =across: only that rule
        var keepIn = Environment.GetEnvironmentVariable("SOFTBODY_KEEPIN");
        if (keepIn == "0") rig.Solver.KeepIn = false;
        else if (keepIn == "out") rig.Solver.LetGoAcross = false;
        else if (keepIn == "across") rig.Solver.LetGoOutward = false;
        // SOFTBODY_0266=0: without 0.26.6's no-bounce contacts and inertia cap
        if (Environment.GetEnvironmentVariable("SOFTBODY_0266") == "0") { rig.Solver.NoBounce = false; rig.MaxInertialSpeed = 0f; rig.Solver.OwnNoPlow = false; }
        return rig;
    }

    static void FleshBuild(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings(), print: true);
        var surf = rig.Surface;
        // weld: render vertices of one control point always weld; different control points rarely share a position
        var cpOfWeld = new Dictionary<int, int>(); int mixed = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            int w = surf.RenderToWeld[v];
            if (cpOfWeld.TryGetValue(w, out int cp)) { if (cp != m.Cp[v]) mixed++; } else cpOfWeld[w] = m.Cp[v];
        }
        int distinctCp = m.Cp.Distinct().Count();
        Console.WriteLine($"  weld: {m.VertexCount} render vertices -> {surf.WeldCount} welded ({distinctCp} control points used, {mixed} render vertices joined across control points)");
        Check(surf.WeldCount <= distinctCp && surf.WeldCount >= distinctCp * 0.99, "welding gives one vertex per control point (UV and normal seams joined)");
        Check(surf.SoftCount <= 800 && surf.SoftCount >= 600, $"the painted torso is reduced to about the target ({surf.RegionSoftVertices} soft -> {surf.SoftCount} soft points, target 800)");

        int flipped = 0;
        for (int t = 0; t < surf.Tris.Length; t += 3)
        {
            int a = surf.Tris[t], b = surf.Tris[t + 1], c = surf.Tris[t + 2];
            var fn = Vector3.Cross(surf.Pos[b] - surf.Pos[a], surf.Pos[c] - surf.Pos[a]);
            var vn = surf.Nrm[a] + surf.Nrm[b] + surf.Nrm[c];
            // the winding may be either way round; count triangles disagreeing with the majority
            if (Vector3.Dot(fn, vn) < 0f) flipped++;
        }
        int tris = surf.TriCount;
        int minority = Math.Min(flipped, tris - flipped);
        Check(minority <= tris / 100, $"the reduced surface stays consistent: {minority} of {tris} triangles face against their vertex normals");

        var sh = rig.Shell;
        int closer = 0;
        for (int i = 0; i < sh.OuterCount; i++)
        {
            var inner = sh.Rest[sh.Layers * sh.OuterCount + i];
            if (Vector3.Distance(inner, sh.BonePoint[i]) < Vector3.Distance(surf.Pos[i], sh.BonePoint[i]) - 1e-7f || sh.Thickness[i] <= 0.1f * surf.VertexEdge[i]) closer++;
        }
        float minT = sh.Thickness.Min(), meanT = sh.Thickness.Average();
        Console.WriteLine($"  shell: thickness mean {meanT * 1000:0.0} mm, min {minT * 1000:0.00} mm, surface edge {surf.MeanEdge * 1000:0.0} mm");
        Check(closer == sh.OuterCount, $"every inner point is closer to its bone than its skin point (or the shell is a thin membrane there: {sh.Thickness.Count(t => t <= 0.1f * surf.MeanEdge)} points)");
        Check(sh.DroppedTets <= sh.TetCount / 100, $"tets kept after thinning: {sh.DroppedTets} dropped of {sh.TetCount + sh.DroppedTets}");
        int nonPositive = 0;
        for (int k = 0; k < sh.TetCount; k++)
        {
            var t = sh.Tets.AsSpan(k * 4, 4);
            if (!(TetShell.SignedVolume(sh.Rest[t[0]], sh.Rest[t[1]], sh.Rest[t[2]], sh.Rest[t[3]]) > 0f)) nonPositive++;
        }
        Check(nonPositive == 0, "every kept tet has a positive bind-space volume");

        // embedding: weights are a partition of unity, so a constant offset is reproduced exactly, and a linear one closely
        var emb = rig.Embedding;
        float worstSum = 0;
        for (int k = 0; k < emb.Count; k++)
        {
            float sum = 0; for (int q = emb.Start[k]; q < emb.Start[k + 1]; q++) sum += emb.Weight[q];
            worstSum = MathF.Max(worstSum, MathF.Abs(sum - 1f));
        }
        Check(worstSum < 1e-4f && emb.Orphans == 0, $"every soft render vertex is embedded with weights summing to 1 ({emb.Count} vertices, {emb.Orphans} orphans)");
        var A = new Matrix4x4(0.02f, 0.01f, 0, 0, -0.01f, 0.03f, 0.005f, 0, 0, 0.01f, 0.02f, 0, 0, 0, 0, 1);
        var field = new Vector3[surf.Count];
        for (int j = 0; j < surf.Count; j++) field[j] = Vector3.TransformNormal(surf.Pos[j], A);
        var outp = new Vector3[emb.Count];
        emb.Interpolate(field, outp);
        double err = 0; float worstErr = 0;
        for (int k = 0; k < emb.Count; k++)
        {
            var want = Vector3.TransformNormal(m.Pos[emb.Vertices[k]], A) * emb.Gain[k];
            float e = Vector3.Distance(want, outp[k]);
            err += e * e; worstErr = MathF.Max(worstErr, e);
        }
        float rms = (float)Math.Sqrt(err / emb.Count), scale = 0.04f * surf.MeanEdge; // |A| ~ 0.04
        Console.WriteLine($"  embedding of a linear offset field: rms error {rms / scale:0.00}, worst {worstErr / scale:0.00} (in |A| x edge length)");
        Check(rms < 0.25f * scale, "a smooth offset field reaches the render vertices smoothly (rms error under a quarter of the field's change over one edge)");
    }

    static void FleshRest(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        var pose = m.Pose(null, Matrix4x4.Identity);
        var w = new float[m.ShapeNames.Length];
        float worst = 0; int inverted = 0;
        for (int f = 0; f < 120; f++) { rig.Step(Dt, pose, w); worst = MathF.Max(worst, rig.MaxOffset); inverted = Math.Max(inverted, rig.Solver.Inverted); }
        Console.WriteLine($"  at rest, no gravity, 2 s: worst offset {worst * 1000:0.000} mm, inverted tets {inverted}");
        Check(worst < 1e-4f && inverted == 0, "standing still, the flesh stays exactly in its animated shape (no drift, no jitter)");
    }

    static void FleshGravity(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings());
        var pose = m.Pose(null, Matrix4x4.Identity);
        var w = new float[m.ShapeNames.Length];
        float last = 0, drift = 0;
        for (int f = 0; f < 240; f++)
        {
            rig.Step(Dt, pose, w);
            if (f >= 210) drift = MathF.Max(drift, MathF.Abs(rig.MaxOffset - last));
            last = rig.MaxOffset;
        }
        Console.WriteLine($"  gravity, 4 s: sag {last * 1000:0.0} mm, still changing by {drift * 1000:0.000} mm/frame, volume x{rig.Solver.VolumeRatio():0.000}");
        Check(last > 1e-4f && last < 0.02f && drift < 1e-4f, "under gravity the flesh sags a little and settles");
    }

    static float Ease(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3 - 2 * t); }

    static void FleshBend(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        int spine = m.Bone("Spine"), chest = m.Bone("Chest");
        var w = new float[m.ShapeNames.Length];
        float peak = 0; int inverted = 0, degenerate = 0;
        for (int f = 0; f < 150; f++)
        {
            float a = Ease(f / 30f) * 25f * MathF.PI / 180f;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, a);
            var pose = m.Pose(new Dictionary<int, Quaternion> { [spine] = q, [chest] = q }, Matrix4x4.Identity);
            rig.Step(Dt, pose, w);
            peak = MathF.Max(peak, rig.MaxOffset);
            inverted = Math.Max(inverted, rig.Solver.Inverted); degenerate = Math.Max(degenerate, rig.Solver.DegenerateRest);
        }
        float end = rig.MaxOffset;
        Console.WriteLine($"  spine and chest bent 25 deg each in 0.5 s, held 2 s: peak lag {peak * 1000:0.0} mm, after {end * 1000:0.000} mm, inverted {inverted}, degenerate rest {degenerate}");
        Check(peak > 5e-4f, "a fast bend makes the flesh lag behind (it is simulated)");
        Check(end < 1e-3f && inverted == 0, "once the pose is held the flesh comes back to the bent shape: it does not fight the joint");
    }

    static void FleshBlendshape(FleshModel m, float[] torso)
    {
        // the blendshape that moves the most soft torso vertices
        int best = -1, bestCount = 0;
        for (int s = 0; s < m.ShapeNames.Length; s++)
        {
            int c = 0;
            for (int k = 0; k < m.ShapeVerts[s].Length; k++)
                if (torso[m.ShapeVerts[s][k]] > 0.5f && m.ShapeDeltas[s][k].Length() > 1e-3f) c++;
            if (c > bestCount) { bestCount = c; best = s; }
        }
        if (best < 0) { Console.WriteLine("  (no blendshape moves the torso: blendshape check skipped)"); return; }
        foreach (float inertia in new[] { 0f, 1f })
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f, BlendshapeInertia = inertia });
            var pose = m.Pose(null, Matrix4x4.Identity);
            var w = new float[m.ShapeNames.Length];
            rig.Step(Dt, pose, w);
            var before = rig.Targets.ToArray();
            float peak = 0;
            for (int f = 0; f < 180; f++)
            {
                w[best] = Ease(f / 18f);
                rig.Step(Dt, pose, w);
                peak = MathF.Max(peak, rig.MaxOffset);
            }
            float moved = 0;
            var after = rig.Targets;
            for (int i = 0; i < rig.Shell.OuterCount; i++) moved = MathF.Max(moved, Vector3.Distance(before[i], after[i]));
            Console.WriteLine($"  blendshape \"{Ascii(m.ShapeNames[best])}\" ({bestCount} soft torso vertices) on in 0.3 s, inertia {inertia}: targets moved up to {moved * 1000:0.0} mm, " +
                              $"peak lag {peak * 1000:0.0} mm, after 3 s {rig.MaxOffset * 1000:0.000} mm, inverted {rig.Solver.Inverted}");
            if (inertia == 0f)
                Check(moved > 2e-3f && peak < 1e-3f && rig.Solver.Inverted == 0, "by default the shell is carried by a blendshape: no lag, no fling");
            else
                Check(peak > 5e-3f && rig.MaxOffset < 0.05f * peak && rig.Solver.Inverted == 0, "with blendshape inertia it lags behind the blendshape and comes to rest in the new shape");
        }
    }

    static string Ascii(string s) => new(s.Select(c => c < 128 ? c : '?').ToArray());

    static void FleshJiggle(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        var w = new float[m.ShapeNames.Length];
        float peak = 0, vMin = 1, vMax = 1;
        float[] trace = new float[150];
        for (int f = 0; f < 150; f++)
        {
            float y = 0.08f * Ease(f / 7f); // up 8 cm in about 0.12 s, then stop
            rig.Step(Dt, m.Pose(null, Matrix4x4.CreateTranslation(0, y, 0)), w);
            peak = MathF.Max(peak, rig.MaxOffset);
            trace[f] = rig.MaxOffset;
            float vr = rig.Solver.VolumeRatio(); vMin = MathF.Min(vMin, vr); vMax = MathF.Max(vMax, vr);
        }
        Console.WriteLine($"  jump 8 cm up in 0.12 s: peak {peak * 1000:0.0} mm, then {trace[30] * 1000:0.0} / {trace[60] * 1000:0.0} / {trace[149] * 1000:0.00} mm at 0.5 / 1 / 2.5 s, " +
                          $"volume x{vMin:0.000}..x{vMax:0.000}");
        Check(peak > 2e-3f && trace[149] < 0.02f * peak, "a jump makes the flesh jiggle, and it settles again (under 2 % of the peak after 2.5 s)");
        Check(vMin > 0.97f && vMax < 1.03f, "the jiggling flesh keeps its volume within 3 %");
    }

    static void FleshBindRoundTrip(FleshModel m, float[] torso)
    {
        var rig = FleshBuildRig(m, torso, new FleshSettings { Gravity = 0f });
        var w = new float[m.ShapeNames.Length];
        int spine = m.Bone("Spine");
        Matrix4x4[] pose = null;
        for (int f = 0; f < 8; f++)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Ease(f / 8f) * 0.5f);
            pose = m.Pose(new Dictionary<int, Quaternion> { [spine] = q }, Matrix4x4.CreateTranslation(0, 0.05f * f / 8f, 0) *
                          Matrix4x4.CreateFromYawPitchRoll(0.3f, 0, 0) * Matrix4x4.CreateScale(1.7f));
            rig.Step(Dt, pose, w);
        }
        // skin rest+offset and rest, and compare their difference with the world offset blended directly
        var posed = m.Pos.ToArray();
        rig.AddBindOffsets(posed);
        var a = new Vector3[m.VertexCount]; var b = new Vector3[m.VertexCount];
        m.Skin.SkinPositions(pose, posed, a); m.Skin.SkinPositions(pose, m.Pos, b);
        var worldPts = new Vector3[rig.Surface.Count];
        for (int i = 0; i < worldPts.Length; i++) worldPts[i] = rig.Offset(i);
        var direct = new Vector3[rig.Embedding.Count];
        rig.Embedding.Interpolate(worldPts, direct);
        double err = 0, mag = 0;
        var mv = rig.MovingVertices;
        for (int k = 0; k < mv.Length; k++)
        {
            var got = a[mv[k]] - b[mv[k]];
            err += (got - direct[k]).LengthSquared(); mag += direct[k].LengthSquared();
        }
        float rel = mag > 0 ? (float)Math.Sqrt(err / mag) : 0f;
        Console.WriteLine($"  bind-space offsets, re-skinned (avatar scaled 1.7, turned, spine bent): {rel * 100:0.0} % rms error vs the world offsets ({Math.Sqrt(mag / mv.Length) * 1000:0.0} mm rms)");
        Check(mag > 0 && rel < 0.05f, "offsets mapped to bind space come back as the same world offsets after skinning");
    }

    static void FleshTiming(FleshModel m, float[] torso)
    {
        foreach (int pts in new[] { 400, 800, 1500 })
        {
            var rig = FleshBuildRig(m, torso, new FleshSettings { SimPoints = pts });
            var w = new float[m.ShapeNames.Length];
            int spine = m.Bone("Spine");
            var times = new List<double>();
            for (int f = 0; f < 200; f++)
            {
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f * MathF.Sin(f * 0.1f));
                rig.Step(Dt, m.Pose(new Dictionary<int, Quaternion> { [spine] = q }, Matrix4x4.CreateTranslation(0, 0.05f * MathF.Sin(f * 0.2f), 0)), w);
                if (f >= 40) times.Add(rig.StepMs);
            }
            times.Sort();
            Console.WriteLine($"  {rig.Surface.SoftCount} soft points, {rig.Shell.VertexCount} shell vertices, {rig.Shell.TetCount} tets, {rig.Settings.Substeps} substeps: " +
                              $"median {times[times.Count / 2]:0.00} ms, 95th {times[(int)(times.Count * 0.95)]:0.00} ms per frame; build {rig.BuildMs:0} ms");
            if (pts == 800) Check(times[times.Count / 2] < 4.5, "800 soft points step within the default 4.5 ms budget");
        }
    }
}
