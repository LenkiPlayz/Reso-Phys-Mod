using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody.Tests;

// Offline tests for skinned meshes (0.8.0): a synthetic two-bone arm (rest, rigid motion, elbow bend, consistency of the
// inverse mapping, blendshape stacking, brush on the posed mesh), the engine's skinning convention and mesh encoder, and
// timing on a 60k-vertex body.
static partial class Program
{
    static void SkinTests()
    {
        try { SkinEngineConvention(); }
        catch (Exception ex) { Check(false, "skinning convention check ran: " + ex.GetType().Name + ": " + ex.Message); }
        SkinRest();
        SkinRigidMotion();
        SkinElbowBend();
        try { SkinUploadEncode(); }
        catch (Exception ex) { Check(false, "skinned upload encode check ran: " + ex.GetType().Name + ": " + ex.Message); }
        try { SkinCpuPipeline(); }
        catch (Exception ex) { Check(false, "CPU skinning pipeline check ran: " + ex.GetType().Name + ": " + ex.Message); }
        SkinTiming();
    }

    // ------------------------------------------------------------------ meshes

    sealed class SkinMesh
    {
        public Vector3[] Pos, Nrm; public int[] Tri; public float[] Soft; public int[] Bones; public float[] Weights; public int BoneCount;
        public int VertexCount => Pos.Length;
        public SkinData Skin() => SkinData.FromBindings(BoneCount, Bones, Weights);
    }

    const float Elbow = 0.3f;

    /// <summary>A tube along +x from 0 to 0.6 m, radius 5 cm (61 x 24 = 1464 vertices). Bone 0 is the upper arm (joint at the
    /// origin), bone 1 the forearm (joint at the elbow, x = 0.3); weights blend linearly over x = 0.24..0.36. Softness is 0
    /// up to the elbow (firm upper arm) and rises (smoothstep) to 1 by x = 0.40 (soft forearm).</summary>
    static SkinMesh Arm(int rings = 60, int segs = 24)
    {
        const float L = 0.6f, r = 0.05f;
        var P = new List<Vector3>(); var N = new List<Vector3>(); var S = new List<float>(); var B = new List<int>(); var W = new List<float>(); var T = new List<int>();
        for (int i = 0; i <= rings; i++)
        {
            float x = L * i / rings;
            float wl = Math.Clamp((x - 0.24f) / 0.12f, 0f, 1f);
            for (int j = 0; j < segs; j++)
            {
                float a = 2 * MathF.PI * j / segs;
                var n = new Vector3(0, MathF.Cos(a), MathF.Sin(a));
                P.Add(new Vector3(x, 0, 0) + r * n); N.Add(n);
                S.Add(x <= Elbow ? 0f : Smoothstep(Elbow, 0.40f, x));
                B.Add(0); B.Add(1); B.Add(0); B.Add(0);
                W.Add(1 - wl); W.Add(wl); W.Add(0f); W.Add(0f);
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = i * segs + j, b = i * segs + (j + 1) % segs, c = a + segs, d = b + segs;
                T.Add(a); T.Add(b); T.Add(c); T.Add(b); T.Add(d); T.Add(c);
            }
        return new SkinMesh { Pos = P.ToArray(), Nrm = N.ToArray(), Soft = S.ToArray(), Bones = B.ToArray(), Weights = W.ToArray(), Tri = T.ToArray(), BoneCount = 2 };
    }

    static readonly Matrix4x4 LowerBind = Matrix4x4.CreateTranslation(-Elbow, 0, 0);

    /// <summary>Skin matrices of the arm: the whole skeleton placed by g (row-vector), the elbow bent about z.
    /// Upper bind pose = identity, forearm bind pose = translation(-elbow); the slot bone is g.</summary>
    static Matrix4x4[] ArmPose(in Matrix4x4 g, float elbowRad) =>
        new[] { g, LowerBind * Matrix4x4.CreateRotationZ(elbowRad) * Matrix4x4.CreateTranslation(Elbow, 0, 0) * g, g };

    static SoftbodySettings ArmSettings() => new() { Cells = 12, Gravity = 0f, Collide = false };

    static SoftbodyRig ArmRig(SkinMesh m, SoftbodySettings s, Matrix4x4[] pose, SkinData skin = null) =>
        SoftbodyRig.CreateSkinned(m.Pos, m.Nrm, m.Tri, m.Soft, s, skin ?? m.Skin(), pose, AnchorPose.Identity);

    static float MaxDiff(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b)
    {
        float m = 0; for (int i = 0; i < a.Length; i++) m = MathF.Max(m, Vector3.Distance(a[i], b[i])); return m;
    }

    static string DescribeSkin(SoftbodyRig r) =>
        $"points {r.Cage.PointCount} ({r.Cage.KinematicCount} pinned), {r.CageSkin.BonesUsed} of {r.CageSkin.MatrixCount} skin matrices used by the cage, " +
        $"{r.CageSkin.InheritedPoints} points with inherited weights, edges {r.Sim.EdgeConstraintCount}, volumes {r.Sim.VolumeConstraintCount}, " +
        $"verts {r.Deformer.VertexCount} ({r.Deformer.DynamicVertexCount} moving), skin scale {r.Sim.SkinScale:0.###}";

    static Vector3 V3(in float3 f) => new(f.x, f.y, f.z);
    static float3 F3(in Vector3 v) => new(v.X, v.Y, v.Z);

    // ------------------------------------------------------------------ the engine's convention

    /// <summary>The plugin's conversion (Plugin/SkinMatrices.cs) and Core's LBS give exactly what the engine's own CPU skinning
    /// (Elements.Assets.BoneBinding.TransformPosition: sum w_i * boneTransform_i * BindPose_i * p, plus (1 - sum w) * p) gives.</summary>
    static void SkinEngineConvention()
    {
        Console.WriteLine("TEST skinning convention: Core LBS + the plugin's matrix conversion vs the engine's own CPU skinning (BoneBinding.TransformPosition)");
        var m = Arm(20, 12);
        int n = m.VertexCount;
        var mx = new MeshX();
        mx.SetVertexCount(n);
        for (int v = 0; v < n; v++) mx.RawPositions[v] = F3(m.Pos[v]);
        mx.HasBoneBindings = true;
        var b0 = mx.AddBone("upper"); var b1 = mx.AddBone("lower");
        // non-trivial bind poses (any invertible matrices), built with the engine's own float4x4 helpers
        b0.BindPose = float4x4.Transform(new float3(0.02f, -0.01f, 0.03f), floatQ.Euler(5f, -8f, 3f), new float3(1f, 1f, 1f));
        b1.BindPose = float4x4.Transform(new float3(-Elbow, 0.01f, 0f), floatQ.Euler(0f, 4f, -6f), new float3(0.9f, 0.9f, 0.9f));
        var bb = mx.RawBoneBindings;
        for (int v = 0; v < n; v++)
        {
            int o = v * 4;
            bb[v] = new BoneBinding { boneIndex0 = m.Bones[o], weight0 = m.Weights[o], boneIndex1 = m.Bones[o + 1], weight1 = m.Weights[o + 1] };
        }
        bb[0] = default; // one unweighted vertex: the engine leaves it at its position in the target space
        // the pose (bone -> target space), with the engine's matrix product (parent * child)
        var upperT = float4x4.Transform(new float3(0.1f, 0.9f, -0.2f), floatQ.Euler(10f, 40f, -5f), new float3(1.2f, 1.2f, 1.2f));
        var lowerLocal = float4x4.Transform(new float3(Elbow, 0f, 0f), floatQ.Euler(0f, 0f, 75f), new float3(1f, 1f, 1f));
        var lowerT = upperT * lowerLocal;
        var boneT = new[] { upperT, lowerT };

        var engine = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            var p = mx.RawPositions[v];
            engine[v] = V3(bb[v].TransformPosition(mx, in p, boneT));
        }
        var bi = new int[n * 4]; var bw = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            var b = bb[v]; int o = v * 4;
            bi[o] = b.boneIndex0; bi[o + 1] = b.boneIndex1; bi[o + 2] = b.boneIndex2; bi[o + 3] = b.boneIndex3;
            bw[o] = b.weight0; bw[o + 1] = b.weight1; bw[o + 2] = b.weight2; bw[o + 3] = b.weight3;
        }
        var skin = SkinData.FromBindings(2, bi, bw);
        // target space = the renderer slot: its own skin matrix (the slot bone) is the identity there
        var mats = new[] { SkinMatrices.Skin(SkinMatrices.ToNumerics(b0.BindPose), upperT), SkinMatrices.Skin(SkinMatrices.ToNumerics(b1.BindPose), lowerT), Matrix4x4.Identity };
        var ours = new Vector3[n];
        skin.SkinPositions(mats, m.Pos, ours);
        float err = MaxDiff(engine, ours);
        // and the conversion itself: Vector3.Transform(p, ToNumerics(M)) == M.TransformPoint3x4(p)
        var q = new float3(0.3f, -0.7f, 1.1f);
        var viaEngine = V3(lowerT.TransformPoint3x4(in q));
        var viaOurs = Vector3.Transform(V3(q), SkinMatrices.ToNumerics(lowerT));
        Console.WriteLine($"  {n} vertices, 2 bones (+1 unweighted vertex): max difference {err:E2} m; float4x4 point transform vs converted: {Vector3.Distance(viaEngine, viaOurs):E2}; " +
                          $"unweighted vertices bound to the slot bone: {skin.UnboundVertices}");
        Check(err < 2e-5f && Vector3.Distance(viaEngine, viaOurs) < 1e-5f && skin.UnboundVertices == 1,
              "skin matrix = BoneSlot.LocalToGlobal * BindPose (bind pose first), blended linearly: identical to the engine's CPU skinning, incl. an unweighted vertex");
    }

    // ------------------------------------------------------------------ the arm

    static void SkinRest()
    {
        Console.WriteLine("TEST skinned rest: the two-bone arm at its bind pose (placed and turned as a whole), no motion");
        var m = Arm();
        var g = Matrix4x4.CreateFromYawPitchRoll(0.5f, 0.2f, -0.1f) * Matrix4x4.CreateTranslation(1f, 1.2f, -0.5f);
        var pose = ArmPose(g, 0f);
        using var rig = ArmRig(m, ArmSettings(), pose);
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        rig.DeformSkinned(pos, nrm, out _, out _);
        float e0 = MaxDiff(pos, m.Pos), n0 = MaxDiff(nrm, m.Nrm);
        for (int f = 0; f < 120; f++) rig.StepSkinned(Dt, pose);
        rig.DeformSkinned(pos, nrm, out _, out _);
        float e1 = MaxDiff(pos, m.Pos), n1 = MaxDiff(nrm, m.Nrm);
        Console.WriteLine($"  {DescribeSkin(rig)}");
        Console.WriteLine($"  before stepping: max position error {e0:E2} m, normal {n0:E2}; after 2 s at rest: {e1:E2} m, normal {n1:E2}; resets {rig.Sim.ResetCount}");
        Check(e0 == 0f && n0 < 1e-3f, "skinned rest: the deformed mesh is exactly the rest mesh before stepping");
        Check(e1 < 1e-5f && n1 < 1e-3f && rig.Sim.ResetCount == 0, "skinned rest: after 2 s with the bones at the bind pose the mesh still equals the rest mesh (< 0.01 mm)");
    }

    static void SkinRigidMotion()
    {
        Console.WriteLine("TEST skinned rigid motion: the whole skeleton moves 0.6 m and turns 90 deg in 1 s, then holds 3 s");
        var m = Arm();
        using var rig = ArmRig(m, ArmSettings(), ArmPose(Matrix4x4.Identity, 0f));
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        var firm = Enumerable.Range(0, m.VertexCount).Where(v => m.Soft[v] <= Cage.GainEpsilon).ToArray();
        bool firmExact = true, pinnedZero = true;
        float maxDuring = 0, final = 0, maxVert = 0;
        var axis = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f));
        for (int f = 0; f < 240; f++)
        {
            float t = f * Dt, k = Smoothstep(0f, 1f, t);
            var g = Matrix4x4.CreateFromAxisAngle(axis, k * MathF.PI / 2) * Matrix4x4.CreateTranslation(k * new Vector3(0.4f, 0.2f, -0.4f));
            rig.StepSkinned(Dt, ArmPose(g, 0f));
            float d = rig.DeformSkinned(pos, nrm, out _, out _);
            maxDuring = MathF.Max(maxDuring, d);
            foreach (int v in firm) if (pos[v] != m.Pos[v]) { firmExact = false; break; }
            var disp = rig.Deformer.Displacements;
            for (int p = 0; p < rig.Cage.PointCount; p++) if (rig.Cage.Kinematic[p] && disp[p] != Vector3.Zero) { pinnedZero = false; break; }
            if (f == 239) { final = d; maxVert = MaxDiff(pos, m.Pos); }
        }
        Console.WriteLine($"  {firm.Length} firm vertices; max cage offset while moving {maxDuring * 1000:0.0} mm (bind space); after 3 s hold {final * 1000:0.000} mm " +
                          $"(largest vertex offset {maxVert * 1000:0.000} mm); resets {rig.Sim.ResetCount}");
        Check(firmExact && pinnedZero, "skinned rigid motion: firm vertices never move (bit-identical to the rest mesh every frame) and pinned points have exactly zero offset");
        Check(maxDuring > 1e-3f, "skinned rigid motion: the soft forearm lags behind while the skeleton moves");
        Check(final < 5e-4f && maxVert < 5e-4f && rig.Sim.ResetCount == 0, "skinned rigid motion: once settled the bind-space offsets go back to ~0 (< 0.5 mm), no resets");
    }

    static void SkinElbowBend()
    {
        Console.WriteLine("TEST skinned elbow bend: the soft forearm bends 90 deg in 0.3 s, then holds 5 s (firm upper arm; default, very wobbly settings)");
        var m = Arm(); var skin = m.Skin();
        using var rig = ArmRig(m, ArmSettings(), ArmPose(Matrix4x4.Identity, 0f), skin);
        Console.WriteLine($"  {DescribeSkin(rig)}");
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        int frames = (int)MathF.Round(5.3f / Dt), probe = (int)MathF.Round(0.45f / Dt);
        float maxOff = 0, final = 0; int peakFrame = 0, maxInverted = 0, maxUntangling = 0, maxDegenerate = 0, maxSingular = 0, restUpdates = 0;
        var timeline = new List<string>();
        Matrix4x4[] pose = null;
        bool firmExact = true;
        var firm = Enumerable.Range(0, m.VertexCount).Where(v => m.Soft[v] <= Cage.GainEpsilon).ToArray();
        float windowMax = 0;
        for (int f = 0; f <= frames; f++)
        {
            float t = f * Dt;
            pose = ArmPose(Matrix4x4.Identity, MathF.PI / 2 * Smoothstep(0f, 0.3f, t));
            rig.StepSkinned(Dt, pose);
            restUpdates = Math.Max(restUpdates, rig.Sim.RestUpdates);
            float d = rig.DeformSkinned(pos, nrm, out _, out _);
            if (d > maxOff) { maxOff = d; peakFrame = f; }
            windowMax = MathF.Max(windowMax, d);
            if (f % 24 == 23) { timeline.Add($"{windowMax * 1000:0.0}"); windowMax = 0; }
            maxInverted = Math.Max(maxInverted, rig.Sim.InvertedTets);
            maxUntangling = Math.Max(maxUntangling, rig.Sim.UntanglingPoints);
            maxDegenerate = Math.Max(maxDegenerate, rig.Sim.DegenerateTets);
            maxSingular = Math.Max(maxSingular, rig.Sim.SingularPoints);
            foreach (int v in firm) if (pos[v] != m.Pos[v]) { firmExact = false; break; }
            if (f == probe)
            {
                Console.WriteLine($"  at t = {t:0.00} s (0.15 s after the bend, mid-wobble):");
                SkinConsistency(rig, m, skin, pose, pos);
                SkinBlendshape(m, skin, pose, pos);
                SkinBrush(m, skin, pose, pos);
            }
            final = d;
        }
        Console.WriteLine($"  max cage offset {maxOff * 1000:0.0} mm at t = {peakFrame * Dt:0.00} s; per 0.4 s (mm): {string.Join(" ", timeline)}");
        Console.WriteLine($"  final offset {final * 1000:0.000} mm; resets {rig.Sim.ResetCount}; inverted tets max {maxInverted} (end {rig.Sim.InvertedTets}), untangling points max {maxUntangling} " +
                          $"(end {rig.Sim.UntanglingPoints}); flat skinned tets max {maxDegenerate}; singular points max {maxSingular}; constraint rest updates per frame max {restUpdates}");
        Check(maxOff > 5e-3f, "elbow bend: the soft forearm lags and wobbles (> 5 mm in bind space)");
        Check(final < 1e-3f && rig.Sim.ResetCount == 0, "elbow bend: then settles back to ~0 offset in bind space (< 1 mm) with no resets");
        Check(rig.Sim.InvertedTets == 0 && rig.Sim.UntanglingPoints == 0 && maxInverted <= rig.Sim.VolumeConstraintCount / 10, "elbow bend: no inverted-tet build-up (none at the end, never more than 10%)");
        Check(firmExact, "elbow bend: firm (upper arm) vertices never move");
    }

    /// <summary>What the renderer shows (CPU skinning of rest + bind-space offset, as the GPU does) against skinned rest + the
    /// simulated world-space offsets interpolated the same way: equal where the skin is rigid, close near the joint.</summary>
    static void SkinConsistency(SoftbodyRig rig, SkinMesh m, SkinData skin, Matrix4x4[] pose, Vector3[] basePos)
    {
        int nv = m.VertexCount, np = rig.Cage.PointCount;
        var shown = new Vector3[nv]; skin.SkinPositions(pose, basePos, shown);
        var skinnedRest = new Vector3[nv]; skin.SkinPositions(pose, m.Pos, skinnedRest);
        var world = new Vector3[np]; var targets = new Vector3[np];
        rig.Sim.GetPositions(world); rig.Sim.GetTargets(targets);
        var offW = new Vector3[np];
        for (int p = 0; p < np; p++) offW[p] = world[p] - targets[p];
        var def = new MeshDeformer(rig.Cage, m.Pos, m.Nrm, m.Tri);
        def.SetDisplacements(offW, out _);
        var posW = (Vector3[])m.Pos.Clone(); var nrmW = (Vector3[])m.Nrm.Clone();
        def.ApplyToMesh(posW, nrmW, out _, out _);
        float maxErr = 0, maxWob = 0, rigidErr = 0, rigidWob = 0; var errs = new List<float>();
        for (int v = 0; v < nv; v++)
        {
            if (rig.Cage.VertexGain[v] <= Cage.GainEpsilon) continue;
            var expected = skinnedRest[v] + (posW[v] - m.Pos[v]);
            float e = Vector3.Distance(shown[v], expected), w = Vector3.Distance(shown[v], skinnedRest[v]);
            errs.Add(e); maxErr = MathF.Max(maxErr, e); maxWob = MathF.Max(maxWob, w);
            if (m.Pos[v].X >= 0.5f) { rigidErr = MathF.Max(rigidErr, e); rigidWob = MathF.Max(rigidWob, w); }
        }
        errs.Sort();
        Console.WriteLine($"    consistency: shown = skin(rest + bind offset) vs skinned rest + world offset: max {maxErr * 1000:0.000} mm, p99 {errs[(int)(errs.Count * 0.99)] * 1000:0.000} mm, " +
                          $"against a wobble of up to {maxWob * 1000:0.0} mm; on the rigid forearm (x >= 0.5) {rigidErr * 1000:0.0000} mm of {rigidWob * 1000:0.0} mm");
        Check(rigidErr < 1e-5f && rigidWob > 1e-3f, "consistency: where the skin is rigid, the shown surface equals skinned rest + the simulated world offset (float precision)");
        Check(maxErr < 0.1f * maxWob, "consistency: everywhere (incl. the elbow blend) it is within 10% of the wobble");
    }

    /// <summary>Blendshapes are added to the base positions before skinning: skin(rest + offset + delta). LBS is affine, so the
    /// blendshape's and the wobble's effects stack additively and neither changes the other.</summary>
    static void SkinBlendshape(SkinMesh m, SkinData skin, Matrix4x4[] pose, Vector3[] basePos)
    {
        int nv = m.VertexCount;
        var delta = new Vector3[nv];
        for (int v = 0; v < nv; v++) delta[v] = 0.01f * Smoothstep(0.4f, 0.5f, m.Pos[v].X) * m.Nrm[v]; // a 1 cm forearm bulge
        Vector3[] Sk(Vector3[] a, Vector3[] b) { var src = new Vector3[nv]; for (int v = 0; v < nv; v++) src[v] = a[v] + (b?[v] ?? default); var dst = new Vector3[nv]; skin.SkinPositions(pose, src, dst); return dst; }
        var A = Sk(basePos, delta); var B = Sk(basePos, null); var C = Sk(m.Pos, delta); var D = Sk(m.Pos, null);
        float e1 = 0, e2 = 0, blend = 0, wob = 0;
        for (int v = 0; v < nv; v++)
        {
            e1 = MathF.Max(e1, Vector3.Distance(A[v] - B[v], C[v] - D[v]));
            e2 = MathF.Max(e2, Vector3.Distance(A[v] - C[v], B[v] - D[v]));
            blend = MathF.Max(blend, Vector3.Distance(C[v], D[v])); wob = MathF.Max(wob, Vector3.Distance(B[v], D[v]));
        }
        Console.WriteLine($"    blendshape: a {blend * 1000:0.0} mm bulge with a {wob * 1000:0.0} mm wobble: the bulge's effect changes by {e1 * 1000:0.0000} mm with the wobble, " +
                          $"the wobble's by {e2 * 1000:0.0000} mm with the bulge");
        Check(e1 < 1e-5f && e2 < 1e-5f && blend > 5e-3f && wob > 1e-3f, "blendshapes stack additively with the wobble: skin(rest + offset + delta) = skin(rest) + effect(offset) + effect(delta)");
    }

    /// <summary>The brush on a skinned mesh: rays hit the CPU-skinned pose (BVH refitted), the hit maps back to bind space with
    /// the hit triangle's barycentric coordinates.</summary>
    static void SkinBrush(SkinMesh m, SkinData skin, Matrix4x4[] pose, Vector3[] basePos)
    {
        int nv = m.VertexCount;
        var posed = new Vector3[nv]; skin.SkinPositions(pose, basePos, posed);
        var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
        layer.Raycast(Vector3.Zero, Vector3.UnitX, 1f, out _); // builds the BVH on the rest mesh
        var sw = Stopwatch.StartNew();
        layer.SetQueryPositions(posed);
        double refitMs = sw.Elapsed.TotalMilliseconds;
        // the triangle on top of the forearm nearest to (0.5, 0.05, 0) at rest, a point inside it on the posed mesh
        int tri = -1; float best = float.MaxValue;
        for (int t = 0; t < m.Tri.Length / 3; t++)
        {
            var c = (m.Pos[m.Tri[3 * t]] + m.Pos[m.Tri[3 * t + 1]] + m.Pos[m.Tri[3 * t + 2]]) / 3f;
            float d = Vector3.Distance(c, new Vector3(0.5f, 0.05f, 0f));
            if (d < best) { best = d; tri = t; }
        }
        var (ia, ib, ic) = layer.TriangleVertices(tri);
        const float la = 0.2f, lb = 0.3f, lc = 0.5f;
        var P = la * posed[ia] + lb * posed[ib] + lc * posed[ic];
        var nrm = Vector3.Normalize(Vector3.Cross(posed[ib] - posed[ia], posed[ic] - posed[ia]));
        var origin = P + 0.3f * nrm;
        var expected = la * m.Pos[ia] + lb * m.Pos[ib] + lc * m.Pos[ic];
        bool hit = layer.Raycast(origin, -0.6f * nrm, 1f, out var h);
        var bind = hit ? layer.RestPoint(h) : new Vector3(float.NaN);
        float err = hit ? Vector3.Distance(bind, expected) : float.PositiveInfinity;
        // what 0.7.0 did: the same ray against the rest mesh
        layer.ResetQueryPositions();
        bool restHit = layer.Raycast(origin, -0.6f * nrm, 1f, out var hr);
        string restText = restHit ? $"{Vector3.Distance(hr.Point, expected) * 1000:0} mm away" : "nothing";
        layer.SetQueryPositions(posed);
        // refit vs rebuild: 500 rays towards random posed vertices
        var fresh = new PaintLayer(posed, ReadOnlySpan<Vector4>.Empty, m.Tri);
        var rng = new Random(7);
        int mismatch = 0, hits = 0;
        for (int r = 0; r < 500; r++)
        {
            int rt = rng.Next(m.Tri.Length / 3);
            float u = 0.1f + 0.4f * (float)rng.NextDouble(), w2 = 0.1f + 0.4f * (float)rng.NextDouble();
            var target = (1 - u - w2) * posed[m.Tri[3 * rt]] + u * posed[m.Tri[3 * rt + 1]] + w2 * posed[m.Tri[3 * rt + 2]];
            var o = target + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.8f;
            bool a1 = layer.Raycast(o, (target - o) * 1.5f, 1f, out var x1), a2 = fresh.Raycast(o, (target - o) * 1.5f, 1f, out var x2);
            if (a1) hits++;
            if (a1 != a2 || (a1 && (x1.Triangle != x2.Triangle || MathF.Abs(x1.T - x2.T) > 1e-5f))) mismatch++;
        }
        // dab radius on a scaled skeleton: the skinning's scale at the hit
        var scaled = ArmPose(Matrix4x4.CreateScale(1.5f), MathF.PI / 3);
        float s = Lbs.LinearScale(skin.VertexMatrix(scaled, ia));
        Console.WriteLine($"    brush: ray at a point on the bent forearm hits triangle {h.Triangle} (target {tri}), maps to bind space {err * 1000:0.0000} mm from the true bind point; " +
                          $"the same ray on the rest mesh hits {restText}; refit {refitMs:0.000} ms ({nv} verts); refitted BVH vs rebuilt: {mismatch} of 500 rays differ ({hits} hit); " +
                          $"skin scale at the hit on a 1.5x skeleton {s:0.0000}");
        Check(hit && h.Triangle == tri && err < 1e-4f, "brush: a ray hitting the skinned forearm maps to the right bind-space position (< 0.1 mm)");
        Check(mismatch == 0 && hits > 100, "brush: the refitted BVH gives exactly the hits of a rebuilt one (500 rays)");
        Check(MathF.Abs(s - 1.5f) < 1e-3f, "brush: the local scale at the hit (dab radius = world radius / scale) follows a scaled skeleton");
    }

    // ------------------------------------------------------------------ the engine's encoder with bones and blendshapes

    static MeshX SkinnedMeshX(SkinMesh m, int blendshapes, bool colors = true)
    {
        int n = m.VertexCount;
        var mx = new MeshX();
        mx.HasNormals = true; mx.HasColors = colors;
        mx.SetVertexCount(n);
        m.Pos.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, n)));
        m.Nrm.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawNormals.AsSpan(0, n)));
        if (colors) for (int v = 0; v < n; v++) mx.RawColors[v] = new color(m.Soft[v], m.Soft[v], m.Soft[v], 1f);
        var sub = mx.AddSubmesh<TriangleSubmesh>();
        for (int t = 0; t < m.Tri.Length; t += 3) sub.AddTriangle(m.Tri[t], m.Tri[t + 1], m.Tri[t + 2]);
        mx.HasBoneBindings = true;
        for (int b = 0; b < m.BoneCount; b++) mx.AddBone("bone" + b).BindPose = float4x4.Translation(new float3(-0.1f * b, 0f, 0f));
        var bb = mx.RawBoneBindings;
        for (int v = 0; v < n; v++)
        {
            int o = v * 4;
            bb[v] = new BoneBinding { boneIndex0 = m.Bones[o], weight0 = m.Weights[o], boneIndex1 = m.Bones[o + 1], weight1 = m.Weights[o + 1],
                                      boneIndex2 = m.Bones[o + 2], weight2 = m.Weights[o + 2], boneIndex3 = m.Bones[o + 3], weight3 = m.Weights[o + 3] };
        }
        for (int k = 0; k < blendshapes; k++)
        {
            var bs = mx.AddBlendShape("shape" + k);
            bs.HasNormals = true;
            var fr = bs.AddFrame(1f);
            for (int v = 0; v < n; v++)
                if (v % (k + 2) == 0) { fr.SetPositionDelta(v, F3(0.01f * m.Nrm[v])); fr.SetNormalDelta(v, new float3(0f, 0.01f, 0f)); }
        }
        return mx;
    }

    static void SkinUploadEncode()
    {
        Console.WriteLine("TEST skinned upload (engine encoder, offline): what a partial upload does to a skinned mesh, and the plugin's unskinned copy");
        var m = Arm();
        var mx = SkinnedMeshX(m, 3);
        var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));
        var alloc = new KeepAlloc();
        MeshUploadHint FullOf(MeshX x) { var h = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref h, x); h[MeshUploadHint.Flag.Dynamic] = true; return h; }
        MeshBuffer Encode(MeshX x, MeshBuffer buf, ref MeshUploadHint hint)
        {
            buf ??= new MeshBuffer();
            MeshBufferGenerator.UpdateMeshBufferData(x, buf, alloc, ColorProfile.Linear, ref hint, MeshMetadata.GenerateMetadata(x), bounds, null);
            return buf;
        }
        static bool Skin(in MeshUploadHint h) => h[MeshUploadHint.Flag.BindPoses] || h[MeshUploadHint.Flag.BoneWeights] || h[MeshUploadHint.Flag.Blendshapes];
        static bool Widened(in MeshUploadHint h) => h[MeshUploadHint.Flag.VertexLayout] || h[MeshUploadHint.Flag.Geometry] || h[MeshUploadHint.Flag.SubmeshLayout];

        // 1. the original avatar mesh: a full upload carries the skin; a Positions|Normals partial is widened by the engine to
        //    VertexLayout + every vertex stream (EnsureValidVertexAttributes treats any mesh with bones as changed), but carries
        //    no skin data. The renderer (Renderite.Unity.MeshAsset.Upload, IL) destroys and recreates a mesh on every upload
        //    after a skinned one, so such a partial would leave a new mesh without indices, bones or blendshapes.
        var full = FullOf(mx);
        var buf = Encode(mx, null, ref full);
        var pn = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
        Encode(mx, buf, ref pn);
        Console.WriteLine($"  original: full hint has skin data {Skin(full)} ({buf.BoneCount} bones, {buf.BlendshapeBufferCount} blendshape buffers); " +
                          $"a P|N partial becomes VertexLayout {pn[MeshUploadHint.Flag.VertexLayout]}, Colors {pn[MeshUploadHint.Flag.Colors]}, skin data {Skin(pn)}");
        Check(Skin(full) && pn[MeshUploadHint.Flag.VertexLayout] && !Skin(pn),
              "IL fact: a partial upload of a mesh with bones is widened to VertexLayout and carries no skin, so partial uploads cannot keep a GPU-skinned mesh (the renderer recreates it)");

        // 2. the plugin's private copy for CPU skinning: no bones, no bindings, blendshape names kept but no frames
        var copy = new MeshX(mx);
        copy.ClearBones(false);
        copy.HasBoneBindings = false;
        foreach (var bs in copy.BlendShapes) bs.ClearFrames();
        var cf = FullOf(copy);
        cf[MeshUploadHint.Flag.BindPoses] = false; cf[MeshUploadHint.Flag.BoneWeights] = false; cf[MeshUploadHint.Flag.Blendshapes] = false;
        var cbuf = Encode(copy, null, ref cf);
        var p = MemoryMarshal.Cast<float3, Vector3>(copy.RawPositions.AsSpan(0, m.VertexCount));
        for (int v = 0; v < m.VertexCount; v++) p[v] += new Vector3(0, 0.01f * m.Soft[v], 0);
        var cpn = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
        var sw = Stopwatch.StartNew();
        Encode(copy, cbuf, ref cpn);
        double ms = sw.Elapsed.TotalMilliseconds;
        var refHint = FullOf(copy);
        var reference = Encode(copy, null, ref refHint);
        int vbytes = reference.VertexStride * reference.VertexCount;
        bool vertexSame = cbuf.RawBuffer.Slice(0, vbytes).SequenceEqual(reference.RawBuffer.Slice(0, vbytes));
        // a fresh buffer (allocation widens the hint to SetFromMesh): still no bone weights and no blendshape buffers
        var fresh = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
        var fbuf = Encode(copy, null, ref fresh);
        Console.WriteLine($"  CPU-skin copy: {copy.BoneCount} bones, {copy.BlendShapeCount} of {mx.BlendShapeCount} blendshapes kept (no frames); first upload: skin data {Skin(cf)}, " +
                          $"{cbuf.BoneCount} bones, {cbuf.BlendshapeBufferCount} blendshape buffers; then P|N partial {ms:0.000} ms, widened {Widened(cpn)}, vertex buffer equals a full encode {vertexSame}; " +
                          $"a reallocating encode: BoneWeights {fresh[MeshUploadHint.Flag.BoneWeights]}, {fbuf.BlendshapeBufferCount} blendshape buffers");
        Check(copy.BoneCount == 0 && !copy.HasBoneBindings && copy.BlendShapeCount == mx.BlendShapeCount,
              "the plugin's copy has no bones but the same blendshape count (the renderer's synced BlendShapeWeights list is never resized by SetupBlendShapes)");
        // what makes the renderer treat an upload as skinned (MeshAsset.Upload): BindPoses flagged (recreate now), bone weights with
        // bones, or blendshapes with blendshape buffers (recreate on the next upload)
        static bool RendererSkins(in MeshUploadHint h, MeshBuffer b) =>
            h[MeshUploadHint.Flag.BindPoses] || (h[MeshUploadHint.Flag.BoneWeights] && b.BoneCount > 0) || (h[MeshUploadHint.Flag.Blendshapes] && b.BlendshapeBufferCount > 0);
        Console.WriteLine($"  first upload hint after encode: BindPoses {cf[MeshUploadHint.Flag.BindPoses]}, BoneWeights {cf[MeshUploadHint.Flag.BoneWeights]}, " +
                          $"Blendshapes {cf[MeshUploadHint.Flag.Blendshapes]} (the encoder re-applies SetFromMesh when it allocates the buffer)");
        Check(!RendererSkins(cf, cbuf) && cbuf.BoneCount == 0 && cbuf.BlendshapeBufferCount == 0 && !Widened(cpn) && !RendererSkins(cpn, cbuf) && vertexSame &&
              !RendererSkins(fresh, fbuf),
              "its uploads never carry skin data (so the renderer keeps the mesh instance and partial Positions|Normals uploads work as for static meshes)");

        // 3. the private copy (new MeshX(original)) keeps the skin to read from
        var copy2 = new MeshX(mx);
        bool copySame = copy2.BoneCount == mx.BoneCount && copy2.BlendShapeCount == mx.BlendShapeCount && copy2.HasBoneBindings &&
                        copy2.GetBone(1).BindPose.Equals(mx.GetBone(1).BindPose) &&
                        copy2.GetBlendShape(0)[0].GetPositionDelta(0).Equals(mx.GetBlendShape(0)[0].GetPositionDelta(0));
        Check(copySame, "new MeshX(original) copies bones, bind poses and blendshape frames (read before they are stripped)");
    }

    /// <summary>The plugin's CPU path (Core BlendshapeDeltas + SkinData.SkinMesh, matrices from Plugin/SkinMatrices.cs) against the
    /// engine's own CPU blendshapes + skinning (MeshX.TransformVertexByBlendshapes then TransformVertexByBones, as
    /// SkinnedMeshPositioner uses them).</summary>
    static void SkinCpuPipeline()
    {
        Console.WriteLine("TEST CPU skinning pipeline: blendshapes + skin of positions, normals and tangents vs the engine's own CPU path");
        var m = Arm(30, 16);
        int n = m.VertexCount;
        var mx = SkinnedMeshX(m, 3);
        mx.HasTangents = true;
        for (int v = 0; v < n; v++) mx.RawTangents[v] = new float4(1f, 0f, 0f, 1f);
        var upperT = float4x4.Transform(new float3(0.1f, 0.9f, -0.2f), floatQ.Euler(10f, 40f, -5f), new float3(1.1f, 1.1f, 1.1f));
        var lowerT = upperT * float4x4.Transform(new float3(Elbow, 0f, 0f), floatQ.Euler(0f, 0f, 60f), new float3(1f, 1f, 1f));
        var boneT = new[] { upperT, lowerT };
        var weights = new float[] { 0.7f, 0f, 0.35f };
        var ePos = new Vector3[n]; var eNrm = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            var pp = mx.RawPositions[v]; var nn = mx.RawNormals[v]; var tt = mx.RawTangents[v];
            mx.TransformVertexByBlendshapes(v, ref pp, ref nn, ref tt, weights);
            mx.TransformVertexByBones(v, ref pp, ref nn, ref tt, boneT);
            ePos[v] = V3(pp); eNrm[v] = Vector3.Normalize(V3(nn));
        }
        // ours
        var skin = m.Skin();
        var mats = new[] { SkinMatrices.Skin(SkinMatrices.ToNumerics(mx.GetBone(0).BindPose), upperT), SkinMatrices.Skin(SkinMatrices.ToNumerics(mx.GetBone(1).BindPose), lowerT), Matrix4x4.Identity };
        var shapes = mx.BlendShapes.ToList();
        var blend = new BlendshapeDeltas(shapes.Count, n, b =>
        {
            var f = shapes[b][0];
            return (f.RawPositions.Take(n).Select(x => V3(x)).ToArray(), shapes[b].HasNormals ? f.RawNormals.Take(n).Select(x => V3(x)).ToArray() : null, null);
        });
        var bp = (Vector3[])m.Pos.Clone(); var bn = (Vector3[])m.Nrm.Clone(); var bt = Enumerable.Repeat(new Vector4(1, 0, 0, 1), n).ToArray();
        int active = blend.Apply(weights, bp, bn, bt);
        var oPos = new Vector3[n]; var oNrm = new Vector3[n]; var oTan = new Vector4[n];
        skin.SkinMesh(mats, bp, bn, bt, oPos, oNrm, oTan, parallel: false, out var lo, out var hi);
        var pPos = new Vector3[n]; var pNrm = new Vector3[n]; var pTan = new Vector4[n];
        skin.SkinMesh(mats, bp, bn, bt, pPos, pNrm, pTan, parallel: true, out _, out _);
        float ep = MaxDiff(ePos, oPos), en = MaxDiff(eNrm, oNrm), par = MaxDiff(oPos, pPos);
        Console.WriteLine($"  {n} vertices, {active} active blendshapes, 2 bones: positions differ by {ep:E2} m, normals by {en:E2}; parallel vs serial {par:E2}; " +
                          $"{blend.StoredDeltas} sparse deltas stored for {shapes.Count} shapes");
        Check(ep < 2e-5f && en < 1e-4f && par == 0f && active == 2,
              "CPU blendshapes (frame 0, delta x weight) + skinning of positions and normals equal the engine's own CPU path; the parallel split gives identical results");
    }

    // ------------------------------------------------------------------ timing on a 60k-vertex body

    /// <summary>Tube from a to b (radius r, rings x segs vertices); weight(t along the tube) gives (bone0, w0, bone1, w1); soft(t).</summary>
    static void Tube(SkinMesh into, List<Vector3> P, List<Vector3> N, List<float> S, List<int> B, List<float> W, List<int> T,
                     Vector3 a, Vector3 b, float r, int rings, int segs, Func<float, (int, float, int, float)> weight, Func<float, float> soft)
    {
        var axis = Vector3.Normalize(b - a);
        var e1 = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var e2 = Vector3.Cross(axis, e1);
        int base0 = P.Count;
        for (int i = 0; i <= rings; i++)
        {
            float t = i / (float)rings;
            var c = Vector3.Lerp(a, b, t);
            var (b0, w0, b1, w1) = weight(t);
            float s = soft(t);
            for (int j = 0; j < segs; j++)
            {
                float ang = 2 * MathF.PI * j / segs;
                var n = MathF.Cos(ang) * e1 + MathF.Sin(ang) * e2;
                P.Add(c + r * n); N.Add(n); S.Add(s);
                B.Add(b0); B.Add(b1); B.Add(0); B.Add(0); W.Add(w0); W.Add(w1); W.Add(0f); W.Add(0f);
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int p0 = base0 + i * segs + j, p1 = base0 + i * segs + (j + 1) % segs, p2 = p0 + segs, p3 = p1 + segs;
                T.Add(p0); T.Add(p2); T.Add(p1); T.Add(p1); T.Add(p2); T.Add(p3);
            }
    }

    // bones: 0 hips, 1 chest, 2 head, 3 L upper arm, 4 L forearm, 5 R upper arm, 6 R forearm
    static readonly Vector3[] BodyJoints =
    {
        new(0, 1.0f, 0), new(0, 1.25f, 0), new(0, 1.5f, 0), new(-0.15f, 1.4f, 0), new(-0.45f, 1.4f, 0), new(0.15f, 1.4f, 0), new(0.45f, 1.4f, 0),
    };

    /// <summary>A ~60k-vertex synthetic avatar in T-pose: torso, head and two arms (tubes), 7 bones; only the left forearm is soft.</summary>
    static SkinMesh Body()
    {
        var m = new SkinMesh { BoneCount = 7 };
        var P = new List<Vector3>(); var N = new List<Vector3>(); var S = new List<float>(); var B = new List<int>(); var W = new List<float>(); var T = new List<int>();
        static (int, float, int, float) Blend(int lo, int hi, float t, float t0, float t1) { float w = Math.Clamp((t - t0) / (t1 - t0), 0f, 1f); return (lo, 1 - w, hi, w); }
        // torso y 0.9..1.5: hips -> chest around y 1.15..1.30
        Tube(m, P, N, S, B, W, T, new(0, 0.9f, 0), new(0, 1.5f, 0), 0.15f, 150, 120, t => Blend(0, 1, 0.9f + 0.6f * t, 1.15f, 1.30f), t => 0f);
        Tube(m, P, N, S, B, W, T, new(0, 1.5f, 0), new(0, 1.78f, 0), 0.1f, 60, 80, t => (2, 1f, 2, 0f), t => 0f);
        // arms 0.15..0.85 from the centre, elbows at 0.45: upper -> fore over 0.42..0.48; the left forearm soft from 0.47 on
        Tube(m, P, N, S, B, W, T, new(-0.15f, 1.4f, 0), new(-0.85f, 1.4f, 0), 0.045f, 200, 90, t => Blend(3, 4, 0.15f + 0.7f * t, 0.42f, 0.48f), t => Smoothstep(0.46f, 0.52f, 0.15f + 0.7f * t));
        Tube(m, P, N, S, B, W, T, new(0.15f, 1.4f, 0), new(0.85f, 1.4f, 0), 0.045f, 200, 90, t => Blend(5, 6, 0.15f + 0.7f * t, 0.42f, 0.48f), t => 0f);
        m.Pos = P.ToArray(); m.Nrm = N.ToArray(); m.Soft = S.ToArray(); m.Bones = B.ToArray(); m.Weights = W.ToArray(); m.Tri = T.ToArray();
        return m;
    }

    /// <summary>A walk: the root moves forward 1.2 m/s with a bob and sway, both arms swing (shoulder and elbow).</summary>
    static void BodyPose(float t, Matrix4x4[] skin)
    {
        var g = Matrix4x4.CreateRotationY(0.08f * MathF.Sin(2 * MathF.PI * t)) * Matrix4x4.CreateTranslation(0, 0.02f * MathF.Sin(4 * MathF.PI * t), 1.2f * t);
        var J = BodyJoints;
        var w = new Matrix4x4[7];
        w[0] = Matrix4x4.CreateTranslation(J[0]) * g;
        w[1] = Matrix4x4.CreateRotationX(0.05f * MathF.Sin(2 * MathF.PI * t)) * Matrix4x4.CreateTranslation(J[1] - J[0]) * w[0];
        w[2] = Matrix4x4.CreateTranslation(J[2] - J[1]) * w[1];
        float swing = 1.0f + 0.3f * MathF.Sin(2 * MathF.PI * t), elbow = 0.2f + 0.3f * (1 + MathF.Sin(2 * MathF.PI * t + 1f));
        w[3] = Matrix4x4.CreateRotationZ(swing) * Matrix4x4.CreateRotationY(0.3f * MathF.Sin(2 * MathF.PI * t)) * Matrix4x4.CreateTranslation(J[3] - J[1]) * w[1];
        w[4] = Matrix4x4.CreateRotationZ(elbow) * Matrix4x4.CreateTranslation(J[4] - J[3]) * w[3];
        w[5] = Matrix4x4.CreateRotationZ(-swing) * Matrix4x4.CreateRotationY(-0.3f * MathF.Sin(2 * MathF.PI * t)) * Matrix4x4.CreateTranslation(J[5] - J[1]) * w[1];
        w[6] = Matrix4x4.CreateRotationZ(-elbow) * Matrix4x4.CreateTranslation(J[6] - J[5]) * w[5];
        for (int b = 0; b < 7; b++) skin[b] = Matrix4x4.CreateTranslation(-J[b]) * w[b];
        skin[7] = g;
    }

    static void SkinTiming()
    {
        Console.WriteLine("TEST skinned timing (Release): ~60k-vertex body, 7 bones, soft left forearm, walking with swinging arms, 2 blendshapes active;");
        Console.WriteLine("  per frame as the plugin does it: pose, cage step, bind-space deform (soft vertices), blendshapes + CPU skin of every vertex, engine P|N encode");
        var m = Body(); var skin = m.Skin();
        int n = m.VertexCount;
        int soft = m.Soft.Count(x => x > Cage.GainEpsilon);
        Console.WriteLine($"  {n} vertices, {m.Tri.Length / 3} triangles, {soft} soft ({100.0 * soft / n:0}%)");
        // two face-like blendshapes on the head (about 2400 vertices each)
        var blend = new BlendshapeDeltas(2, n, b =>
        {
            var d = new Vector3[n];
            for (int v = 0; v < n; v++) if (m.Pos[v].Y > 1.55f && (v % 2 == b)) d[v] = 0.005f * m.Nrm[v];
            return (d, null, null);
        });
        var weights = new[] { 0.5f, 0.3f };
        // the unskinned copy the plugin uploads (bones stripped), for the engine encoder
        var mx = SkinnedMeshX(m, 0);
        mx.ClearBones(false); mx.HasBoneBindings = false;
        var meta = MeshMetadata.GenerateMetadata(mx);
        var ebounds = BoundingBox.FromPoints(new float3(-1, 0, -1), new float3(1, 2, 1));
        var alloc = new KeepAlloc(); var ebuf = new MeshBuffer();
        var efull = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref efull, mx); efull[MeshUploadHint.Flag.Dynamic] = true;
        MeshBufferGenerator.UpdateMeshBufferData(mx, ebuf, alloc, ColorProfile.Linear, ref efull, meta, ebounds, null);
        var mxPos = MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, n)).ToArray();

        Console.WriteLine("  fps cells points moving | step ms  rest upd  deform ms | blend+skin all ms (parallel / serial)  copy+encode ms | total ms (p95) | brush refit ms | max offset  resets");
        var pose = new Matrix4x4[8];
        foreach (var (fps, cells) in new[] { (60, 16), (90, 16), (60, 24) })
        {
            float dt = 1f / fps;
            BodyPose(0f, pose);
            var st = new SoftbodySettings { Cells = cells, Gravity = 0.3f, Collide = false };
            using var rig = SoftbodyRig.CreateSkinned(m.Pos, m.Nrm, m.Tri, m.Soft, st, skin, pose, AnchorPose.Identity);
            var basePos = (Vector3[])m.Pos.Clone(); var baseNrm = (Vector3[])m.Nrm.Clone();
            var mixPos = new Vector3[n]; var mixNrm = new Vector3[n];
            var outPos = new Vector3[n]; var outNrm = new Vector3[n]; var serPos = new Vector3[n]; var serNrm = new Vector3[n];
            var layer = new PaintLayer(m.Pos, ReadOnlySpan<Vector4>.Empty, m.Tri);
            layer.Raycast(Vector3.Zero, Vector3.UnitX, 1f, out _);
            var stepT = new List<double>(); var defT = new List<double>(); var skinT = new List<double>(); var serT = new List<double>();
            var encT = new List<double>(); var totT = new List<double>(); var brushT = new List<double>();
            long restUpd = 0; float maxOff = 0;
            var sw = new Stopwatch();
            int frames = 6 * fps, warm = 2 * fps;
            for (int f = 0; f < frames; f++)
            {
                BodyPose(f * dt, pose);
                sw.Restart(); rig.StepSkinned(dt, pose); double a = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); float d = rig.UpdateCageSkinned(out _); rig.ApplyToMesh(basePos, baseNrm, out _, out _); rig.CommitSnapshot(); double b = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                basePos.AsSpan().CopyTo(mixPos); baseNrm.AsSpan().CopyTo(mixNrm);
                blend.Apply(weights, mixPos, mixNrm, Span<Vector4>.Empty);
                skin.SkinMesh(pose, mixPos, mixNrm, null, outPos, outNrm, null, parallel: true, out _, out _);
                double c = sw.Elapsed.TotalMilliseconds;
                double c2 = 0;
                if (f % 10 == 0) { sw.Restart(); skin.SkinMesh(pose, mixPos, mixNrm, null, serPos, serNrm, null, parallel: false, out _, out _); c2 = sw.Elapsed.TotalMilliseconds; }
                sw.Restart();
                outPos.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, n)));
                outNrm.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawNormals.AsSpan(0, n)));
                var h = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
                MeshBufferGenerator.UpdateMeshBufferData(mx, ebuf, alloc, ColorProfile.Linear, ref h, meta, ebounds, null);
                double e = sw.Elapsed.TotalMilliseconds;
                if (f >= warm)
                {
                    stepT.Add(a); defT.Add(b); skinT.Add(c); encT.Add(e); totT.Add(a + b + c + e); if (c2 > 0) serT.Add(c2);
                    restUpd += rig.Sim.RestUpdates; maxOff = MathF.Max(maxOff, d);
                }
                if (f % (fps / 10) == 0 && f >= warm) { sw.Restart(); layer.SetQueryPositions(outPos); brushT.Add(sw.Elapsed.TotalMilliseconds); }
            }
            static double P95(List<double> l) { var c = l.OrderBy(x => x).ToList(); return c[(int)(c.Count * 0.95)]; }
            double total = totT.Average();
            Console.WriteLine($"  {fps,3} {cells,5} {rig.Cage.PointCount,6} {rig.Deformer.DynamicVertexCount,6} | {stepT.Average(),6:0.000}  {(double)restUpd / stepT.Count,7:0}  {defT.Average(),8:0.000} | " +
                              $"{skinT.Average(),9:0.000} / {serT.Average(),6:0.000}                    {encT.Average(),6:0.000}     | {total,6:0.000} ({P95(totT):0.000}) | " +
                              $"{brushT.Average(),8:0.00}      | {maxOff * 1000,6:0.0} mm  {rig.Sim.ResetCount}");
            if (cells == 16)
                Check(total < 4.5 && rig.Sim.ResetCount == 0,
                      $"60k-vertex skinned body, soft forearm, {fps} fps: {total:0.00} ms per frame for the whole CPU-skinned pipeline (budget 4.5 ms), no resets");
        }
        var sw2 = Stopwatch.StartNew();
        var smx = SkinnedMeshX(m, 0);
        for (int i = 0; i < 5; i++) { var bm = smx.CalculateBoneMetadata(); smx.CalculateApproximateBoneBounds(bm); }
        double boneMetaMs = sw2.Elapsed.TotalMilliseconds / 5;
        sw2.Restart(); var copy = new MeshX(smx); double copyMs = sw2.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  the renderer re-uploads the whole vertex buffer per upload: {ebuf.VertexStride * ebuf.VertexCount / 1024.0 / 1024.0:0.00} MB ({ebuf.VertexStride} B x {ebuf.VertexCount}); " +
                          $"for comparison, bone metadata + approximate bone bounds of the skinned mesh {boneMetaMs:0.00} ms (not needed: the copy has no bones); private MeshX copy at start {copyMs:0.0} ms (once)");
    }
}
