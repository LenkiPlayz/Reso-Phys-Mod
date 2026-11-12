using System.Diagnostics;
using System.Numerics;
using Softbody.Core;

// Offline tests for the Softbody core (cage + Bepu sim + deformer), using the game's BepuPhysics.dll.
//   dotnet run -c Release --project tests
// Exit code 0 = all checks passed.
namespace Softbody.Tests;

static partial class Program
{
    static int _failures;
    const float Dt = 1f / 60f;

    static int Main(string[] args)
    {
        Console.WriteLine($"Bepu: {typeof(BepuPhysics.Simulation).Assembly.Location}");
        Console.WriteLine($"      {System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(BepuPhysics.Simulation).Assembly.Location).ProductVersion}");
        Console.WriteLine();

        int exportArg = Array.IndexOf(args, "flesh-export");
        if (exportArg >= 0 && exportArg + 1 < args.Length) return FleshExport(args[exportArg + 1]);

        if (args.Contains("fluid-pour")) { FluidPourDiag(); return 0; }
        // "gpu-compile": compile every GPU fluid kernel and list its bindings (0.16.0)
        if (args.Contains("gpu-compile")) return GpuCompileAll();
        if (args.Contains("gpu-diag2")) { GpuFluidDiag2(); return 0; }
        if (args.Contains("gpu-diag")) { GpuFluidDiag(); return 0; }
        // "fluid-floor": 12,000 particles on a very thin box floor (0.18.0), and a moving box (0.19.0), CPU and GPU
        if (args.Contains("fluid-floor"))
        {
            FluidMovingBoxTests();
            FluidThinFloorTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "fluid-size": particle size and surface detail (0.17.0) and DropletSize (0.18.0), CPU and GPU
        if (args.Contains("fluid-size"))
        {
            FluidSizeTests();
            FluidDropletTests();
            FluidV019Tests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "fluid-sleep": the calm pool and sleeping (0.21.0), CPU and GPU
        if (args.Contains("fluid-sleep"))
        {
            FluidSleepTests();
            FluidSurfaceCacheTests();
            FluidRoomTests();
            FluidSettleTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "fluid-tension": surface tension and the drawn surface (0.20.0), CPU and GPU
        if (args.Contains("fluid-tension"))
        {
            FluidTensionTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "plugin-check": only the built plugin's and the renderer plugin's checks (0.26.0)
        if (args.Contains("plugin-check"))
        {
            PluginCheck.Run(Check);
            Console.WriteLine();
            RendererCheck.Run(Check);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // 0.27.0: "fluid-0270-cpu" the parts with no GPU (the channel, the gas director's Vortices, the thick liquid's mapping);
        // "foam", "vortex", "thick" each GPU part alone; "fluid-0270" all three
        if (args.Contains("fluid-0270-cpu"))
        {
            Channel0270Tests();
            VortexDirectorTests();
            ThickMappingTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        if (args.Contains("foam") || args.Contains("vortex") || args.Contains("thick") || args.Contains("fluid-0270"))
        {
            bool all = args.Contains("fluid-0270");
            if (all || args.Contains("vortex")) VortexTests();
            if (all || args.Contains("foam")) FoamTests();
            if (all || args.Contains("thick")) ThickLiquidTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "gas": only the gas (0.26.0)
        if (args.Contains("gas"))
        {
            GasTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "smoke": only the smoke shader's quads (0.27.0); "gas-preview <file>": a frame of smoke for SoftbodyShaders' preview
        if (args.Contains("smoke"))
        {
            SmokeShaderTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        if (args.Length >= 2 && args[0] == "gas-preview") return GasPreview(args[1], args.Length >= 3 ? args[2] : "side");
        // "gpu-fluid": only the GPU fluid (0.16.0)
        if (args.Contains("gpu-fluid"))
        {
            GpuFluidTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "fluid": only the particle liquid (0.15.0)
        if (args.Contains("fluid"))
        {
            FluidTests();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh-layers": only the hue layers (0.13.0)
        if (args.Contains("flesh-layers"))
        {
            var lp = FleshModel.DefaultPath();
            if (lp == null || !File.Exists(lp)) { Console.WriteLine("  (skipped: no test model at " + lp + ")"); return 0; }
            FleshLayersTest(FleshModel.Load(lp));
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh-box": only the box press and the culling check (quick iteration on contacts)
        if (args.Contains("flesh-box"))
        {
            var bp = FleshModel.DefaultPath();
            if (bp == null || !File.Exists(bp)) { Console.WriteLine("  (skipped: no test model at " + bp + ")"); return 0; }
            var bm = FleshModel.Load(bp);
            var bt = bm.Paint("Hips", "Spine", "Chest");
            FleshBoxPress(bm, bt);
            FleshSquish(bm, bt);
            CullingMatchesBruteForce(bm, bt);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh-jitter": 0.26.6, skin points flying after sudden movements and collider hits
        if (args.Contains("flesh-jitter"))
        {
            var jp = FleshModel.DefaultPath();
            if (jp == null || !File.Exists(jp)) { Console.WriteLine("  (skipped: no test model at " + jp + ")"); return 0; }
            var jm = FleshModel.Load(jp);
            FleshJitterTests(jm, jm.Paint("Hips", "Spine", "Chest"));
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh-stuck": 0.26.4, skin points stuck on flat boxes the body passed through
        if (args.Contains("flesh-stuck"))
        {
            var sp = FleshModel.DefaultPath();
            if (sp == null || !File.Exists(sp)) { Console.WriteLine("  (skipped: no test model at " + sp + ")"); return 0; }
            var sm = FleshModel.Load(sp);
            FleshStuckTests(sm, sm.Paint("Hips", "Spine", "Chest"));
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh-shading": 0.26.3, the skinned softbody's normals and tangents on blendshapes, the tangent upload, the plugin checks
        if (args.Contains("flesh-shading"))
        {
            FleshShadingTests();
            GpuPathTests();
            Console.WriteLine();
            PluginCheck.Run(Check);
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }
        // "flesh": only the skinned flesh tests (phase 1 of DESIGN-skinned.md)
        if (args.Contains("flesh"))
        {
            FleshTests();
            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        RestIdentity();
        AffineReproduction();
        Smoothness();
        Settle();
        SpherePress();
        StuckProbe();
        SparseCage();
        HsvSoftness();
        HueBandTest();
        Drop();
        Shake();
        Spin();
        LiveTuning();
        LiveTuningSmoke();
        Timing();
        PaintTests();
        SkinTests();
        GpuPathTests();
        LiteBufferTests();
        DebugTests();
        LatticeTests();
        FleshTests();
        FleshShadingTests();
        FluidTests();
        GpuFluidTests();
        FluidSizeTests();
        FluidDropletTests();
        FluidThinFloorTests();
        FluidMovingBoxTests();
        FluidV019Tests();
        FluidTensionTests();
        FluidSleepTests();
        FluidSurfaceCacheTests();
        FluidRoomTests();
        FluidSettleTests();
        GasTests();
        VortexTests();       // 0.27.0
        FoamTests();         // 0.27.0
        ThickLiquidTests();  // 0.27.0
        try { UploadBench.Run(sub => { var m = Cube(sub, 0.6f, true); return (m.Pos, m.Nrm, m.Soft, m.Tri); }); }
        catch (Exception ex) { Console.WriteLine("  upload bench could not run offline: " + ex.GetType().Name + ": " + ex.Message); }
        Console.WriteLine();
        PluginCheck.Run(Check);
        Console.WriteLine();
        RendererCheck.Run(Check);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    // ------------------------------------------------------------------ test mesh
    /// <summary>Subdivided cube, pivot at bottom centre, y in [0, size]; red = softness gradient (bottom firm, top soft).</summary>
    sealed class TestMesh
    {
        public Vector3[] Pos, Nrm; public float[] Soft; public int[] Tri;
        public int VertexCount => Pos.Length;
    }

    static float Smoothstep(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    static TestMesh Cube(int subdiv, float size = 0.6f, bool gradient = true)
    {
        var P = new List<Vector3>(); var N = new List<Vector3>(); var S = new List<float>(); var T = new List<int>();
        float h = size / 2;
        // (normal, u axis, v axis) per face
        var faces = new (Vector3 n, Vector3 u, Vector3 v)[]
        {
            (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
            (Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
        };
        foreach (var (n, u, v) in faces)
        {
            int baseIdx = P.Count;
            for (int j = 0; j <= subdiv; j++)
                for (int i = 0; i <= subdiv; i++)
                {
                    float a = (i / (float)subdiv - 0.5f) * size, b = (j / (float)subdiv - 0.5f) * size;
                    var p = n * h + u * a + v * b + new Vector3(0, h, 0);
                    P.Add(p); N.Add(n);
                    S.Add(gradient ? Smoothstep(0.2f, 0.85f, p.Y / size) : 1f);
                }
            for (int j = 0; j < subdiv; j++)
                for (int i = 0; i < subdiv; i++)
                {
                    int i0 = baseIdx + j * (subdiv + 1) + i, i1 = i0 + 1, i2 = i0 + subdiv + 1, i3 = i2 + 1;
                    T.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
                }
        }
        return new TestMesh { Pos = P.ToArray(), Nrm = N.ToArray(), Soft = S.ToArray(), Tri = T.ToArray() };
    }

    static SoftbodyRig Rig(TestMesh m, SoftbodySettings s, AnchorPose anchor, bool useSoftness = true) =>
        SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, useSoftness ? m.Soft : ReadOnlySpan<float>.Empty, s, anchor);

    static int TopCentre(TestMesh m)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < m.VertexCount; i++)
        {
            var p = m.Pos[i];
            if (m.Nrm[i].Y < 0.9f) continue;
            float d = p.X * p.X + p.Z * p.Z;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    static string Describe(SoftbodyRig r) =>
        $"points {r.Cage.PointCount} ({r.Cage.KinematicCount} pinned), tets {r.Cage.TetCount}, edges {r.Sim.EdgeConstraintCount}, " +
        $"volumes {r.Sim.VolumeConstraintCount}, servos {r.Sim.ServoCount}, verts {r.Deformer.VertexCount} " +
        $"({r.Deformer.DynamicVertexCount} moving, {r.Deformer.NormalVertexCount} normals, {r.Deformer.NormalTriangleCount} tris)";

    // ------------------------------------------------------------------ tests
    static void RestIdentity()
    {
        Console.WriteLine("TEST rest identity (rotated, scaled, translated anchor; no stepping)");
        var m = Cube(20);
        var anchor = new AnchorPose(new Vector3(3, 1, -2), Quaternion.CreateFromYawPitchRoll(0.7f, 0.3f, -0.2f), new Vector3(1.5f, 0.8f, 2f));
        using var rig = Rig(m, new SoftbodySettings(), anchor);
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        rig.Deform(anchor, pos, nrm, out _, out _);
        float pe = 0, ne = 0;
        for (int i = 0; i < pos.Length; i++) { pe = MathF.Max(pe, Vector3.Distance(pos[i], m.Pos[i])); ne = MathF.Max(ne, Vector3.Distance(nrm[i], m.Nrm[i])); }
        Console.WriteLine($"  {Describe(rig)}");
        Console.WriteLine($"  max position error {pe:E2} m, max normal error {ne:E2}");
        Check(pe < 1e-4f, "deformed rest mesh equals the original positions");
        Check(ne < 1e-3f, "recomputed rest normals equal the original normals");
    }

    static readonly AnchorPose IdentityAnchor = new AnchorPose(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Applies a displacement field to the cage points and returns the deformed mesh positions.</summary>
    static Vector3[] DeformWith(TestMesh m, Cage cage, bool smooth, Func<Vector3, Vector3> field, out MeshDeformer def, out Vector3[] nrm)
    {
        def = new MeshDeformer(cage, m.Pos, m.Nrm, m.Tri, smooth);
        var world = new Vector3[cage.PointCount];
        for (int i = 0; i < world.Length; i++) world[i] = field(cage.RestPoints[i]);
        def.UpdateCage(world, IdentityAnchor, out _);
        var pos = (Vector3[])m.Pos.Clone(); nrm = (Vector3[])m.Nrm.Clone();
        def.ApplyToMesh(pos, nrm, out _, out _);
        return pos;
    }

    static void AffineReproduction()
    {
        Console.WriteLine("TEST affine reproduction: the whole (all-soft) cage rotated 15 deg, stretched and moved; smooth embedding must follow exactly");
        var m = Cube(20, 0.6f, gradient: false);
        var cage = Cage.Build(m.Pos, ReadOnlySpan<float>.Empty, new SoftbodySettings());
        var rot = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)), 15f * MathF.PI / 180f);
        Vector3 A(Vector3 p) => Vector3.Transform(p * new Vector3(1.05f, 0.97f, 1f), rot) + new Vector3(0.05f, 0.02f, -0.03f);
        var pos = DeformWith(m, cage, true, A, out var def, out _);
        float err = 0;
        for (int v = 0; v < pos.Length; v++) err = MathF.Max(err, Vector3.Distance(pos[v], A(m.Pos[v])));
        Console.WriteLine($"  cage {cage.Nx}x{cage.Ny}x{cage.Nz}, {def.DynamicVertexCount} moving verts, max error {err:E2} m, spline bound {cage.SplineMaxAbsSum:0.00}");
        Check(err < 1e-4f, "smooth embedding reproduces affine cage motion exactly (incl. vertices near the ghost layers)");
    }

    /// <summary>Largest angle (degrees) between the normals of neighbouring top-face triangles.</summary>
    static (float max, float p99) TopCreases(TestMesh m, Vector3[] pos)
    {
        var tris = new List<int>();
        for (int t = 0; t < m.Tri.Length; t += 3)
            if (m.Nrm[m.Tri[t]].Y > 0.9f && m.Nrm[m.Tri[t + 1]].Y > 0.9f && m.Nrm[m.Tri[t + 2]].Y > 0.9f) tris.Add(t);
        var edgeTris = new Dictionary<long, List<Vector3>>();
        foreach (int t in tris)
        {
            int a = m.Tri[t], b = m.Tri[t + 1], c = m.Tri[t + 2];
            var n = Vector3.Normalize(Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]));
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                long key = x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
                if (!edgeTris.TryGetValue(key, out var l)) edgeTris[key] = l = new List<Vector3>();
                l.Add(n);
            }
        }
        var angles = edgeTris.Values.Where(l => l.Count == 2)
            .Select(l => MathF.Acos(Math.Clamp(Vector3.Dot(l[0], l[1]), -1f, 1f)) * 180f / MathF.PI).OrderBy(x => x).ToArray();
        return (angles[^1], angles[(int)(angles.Length * 0.99)]);
    }

    /// <summary>A sphere collider pressed down into the top of the cube and lifted away again.
    /// updateEvery = 1 is the plugin's behaviour now (proxy pose every frame); 30 mimics the old 0.5 s refresh.</summary>
    static (float delay, float dent, float residual, int resets) PressRun(int updateEvery)
    {
        var m = Cube(30);
        using var rig = Rig(m, new SoftbodySettings(), AnchorPose.Identity);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        int top = TopCentre(m);
        for (int f = 0; f < 60; f++) { rig.Step(Dt, AnchorPose.Identity); }
        rig.Deform(AnchorPose.Identity, pos, nrm, out _, out _);
        float restTop = pos[top].Y;
        const float r = 0.15f, speed = 0.5f;
        float startY = 0.6f + r + 0.1f;
        float contactT = -1, reactT = -1, dent = 0, t = 0;
        ProxyShape[] one = new ProxyShape[1];
        for (int f = 0; f < 300; f++, t += Dt)
        {
            // down for 0.6 s (0.2 m into the top), hold 0.6 s, then up and away
            float y = t < 0.6f ? startY - speed * t : t < 1.2f ? startY - 0.3f : startY - 0.3f + 2f * speed * (t - 1.2f);
            var sphere = ProxyShape.Sphere(new Vector3(0, y, 0), r, key: 1);
            if (f % updateEvery == 0) { one[0] = sphere; rig.Sim.SetProxies(one); }
            rig.Step(Dt, AnchorPose.Identity);
            rig.Deform(AnchorPose.Identity, pos, nrm, out _, out _);
            if (contactT < 0 && y - r < restTop) contactT = t;
            if (reactT < 0 && pos[top].Y < restTop - 0.005f) reactT = t;
            dent = MathF.Max(dent, restTop - pos[top].Y);
        }
        float residual = MathF.Abs(pos[top].Y - restTop);
        return (reactT < 0 || contactT < 0 ? float.NaN : reactT - contactT, dent, residual, rig.Sim.ResetCount);
    }

    /// <summary>A sphere pressed 0.2 m into the top, dragged across it, lifted away, then 3 s to settle.
    /// Returns the largest point offset from its rest target afterwards and the number of inverted tets.</summary>
    /// <param name="depth">how far below the top surface the sphere's centre goes (0.6 m cube, cells 0.15 m)</param>
    /// <param name="tuneAfter">settings applied live after the drag (null = keep)</param>
    static (float residual, int inverted, int resets, int peakInverted) DragProbe(SoftbodySettings s, float depth, float speed,
                                                                                   SoftbodySettings tuneAfter = null, int settleFrames = 180)
    {
        var m = Cube(30);
        using var rig = Rig(m, s, AnchorPose.Identity);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var restSign = TetSigns(rig, rig.Cage.RestPoints);
        var one = new ProxyShape[1];
        const float r = 0.15f;
        int peak = 0;
        float yIn = 0.6f - depth, yTop = 0.6f + r + 0.1f;
        float tDown = (yTop - yIn) / speed, tDrag = 0.6f / speed, tUp = tDown;
        int total = (int)((tDown + tDrag + tUp) / Dt) + 1;
        for (int f = 0; f < total; f++)
        {
            float t = f * Dt;
            // down at x=-0.3 (front edge), drag right across the whole top to x=+0.3, then straight up
            float x = t < tDown ? -0.3f : t < tDown + tDrag ? -0.3f + speed * (t - tDown) : 0.3f;
            float y = t < tDown ? yTop - speed * t : t < tDown + tDrag ? yIn : yIn + speed * (t - tDown - tDrag);
            one[0] = ProxyShape.Sphere(new Vector3(x, y, 0.05f), r, key: 1);
            rig.Sim.SetProxies(one);
            rig.Step(Dt, AnchorPose.Identity);
            peak = Math.Max(peak, CountInverted(rig, restSign));
        }
        rig.Sim.SetProxies(ReadOnlySpan<ProxyShape>.Empty);
        if (tuneAfter != null) rig.ApplyTuning(tuneAfter);
        for (int f = 0; f < settleFrames; f++) rig.Step(Dt, AnchorPose.Identity);
        var pts = new Vector3[rig.Cage.PointCount]; var tgt = new Vector3[rig.Cage.PointCount];
        rig.Sim.GetPositions(pts); rig.Sim.GetTargets(tgt);
        float res = 0;
        for (int i = 0; i < pts.Length; i++) res = MathF.Max(res, Vector3.Distance(pts[i], tgt[i]));
        return (res, CountInverted(rig, restSign), rig.Sim.ResetCount, peak);
    }

    static float[] TetSigns(SoftbodyRig rig, ReadOnlySpan<Vector3> pts)
    {
        var t = rig.Cage.Tets; var s = new float[t.Length / 4];
        for (int i = 0; i < t.Length; i += 4) s[i / 4] = MathF.Sign(Cage.TetVolume(pts, t[i], t[i + 1], t[i + 2], t[i + 3]));
        return s;
    }

    /// <summary>Tets whose orientation flipped against the rest pose (turned inside out).</summary>
    static int CountInverted(SoftbodyRig rig, float[] restSign)
    {
        var p = rig.CagePositions;
        var t = rig.Cage.Tets; int n = 0;
        for (int i = 0; i < t.Length; i += 4)
            if (Cage.TetVolume(p, t[i], t[i + 1], t[i + 2], t[i + 3]) * restSign[i / 4] <= 0f) n++;
        return n;
    }

    static void StuckProbe()
    {
        Console.WriteLine("TEST stuck probe: sphere pushed into the top edge, dragged across, lifted; then 3 s to settle");
        var hi = new SoftbodySettings { Stiffness = 40, Damping = 2, RigidFrequency = 60 };
        bool allClean = true; float lenkiWorst = 0;
        foreach (var (name, s, depth, speed, tune) in new (string, SoftbodySettings, float, float, SoftbodySettings)[]
        {
            ("defaults, 0.2 deep, 0.5 m/s", new SoftbodySettings(), 0.2f, 0.5f, null),
            ("defaults, 0.4 deep, 1.5 m/s", new SoftbodySettings(), 0.4f, 1.5f, null),
            ("stiff 40/damp 2/firm 60, 0.2", hi, 0.2f, 0.5f, null),
            ("stiff 40/damp 2/firm 60, 0.4 fast", hi, 0.4f, 1.5f, null),
            ("defaults, then tuned to stiff", new SoftbodySettings(), 0.4f, 1.5f, hi),
            ("stiff 60/damp 4/firm 60, 0.4 fast", new SoftbodySettings { Stiffness = 60, Damping = 4, RigidFrequency = 60 }, 0.4f, 1.5f, null),
            // Lenki's in-world settings when it got stuck: fine cage, strong edges, very weak volume and firm springs
            ("Lenki: cells 12, stiff 35, vol 1, firm 1", new SoftbodySettings { Cells = 12, Stiffness = 35, VolumeStiffness = 1, RigidFrequency = 1, Damping = 1, Gravity = 0 }, 0.3f, 1.0f, null),
            ("Lenki: same, slow press", new SoftbodySettings { Cells = 12, Stiffness = 35, VolumeStiffness = 1, RigidFrequency = 1, Damping = 1, Gravity = 0 }, 0.3f, 0.3f, null),
            ("cells 12, stiff 15, vol 1, damp 0.15", new SoftbodySettings { Cells = 12, Stiffness = 15, VolumeStiffness = 1, Gravity = 0 }, 0.3f, 1.0f, null),
        })
        {
            var r = DragProbe(s, depth, speed, tune);
            Console.WriteLine($"  {name,-36}: residual {r.residual * 1000,6:0.0} mm, inside-out tets now {r.inverted} (peak {r.peakInverted}), resets {r.resets}");
            allClean &= r.inverted == 0 && r.resets == 0;
            if (s.Cells == 12) lenkiWorst = MathF.Max(lenkiWorst, r.residual);
        }
        Check(allClean, "after every drag the cage settles with no inside-out tets and no resets");
        Check(lenkiWorst < 0.005f, "the fine, weak-volume cage (Lenki's settings) springs fully back (within 5 mm; it used to stay folded ~70 mm)");
    }

    static void SparseCage()
    {
        Console.WriteLine("TEST sparse cage: only one top corner of the cube is soft, cells 8; shaken +-15 cm at 2 Hz for 2 s");
        var m = Cube(30, 0.6f, gradient: false);
        var corner = new Vector3(0.3f, 0.6f, 0.3f);
        var soft = m.Pos.Select(p => 1f - Smoothstep(0.1f, 0.25f, Vector3.Distance(p, corner))).ToArray();
        int softVerts = soft.Count(x => x > Cage.GainEpsilon);
        int cornerV = Enumerable.Range(0, m.VertexCount).OrderBy(v => Vector3.Distance(m.Pos[v], corner)).First();

        (float wobble, double ms, int resets, int points, int moving, int cells, int total) Run(float[] map)
        {
            using var rig = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, map, new SoftbodySettings { Cells = 8 }, AnchorPose.Identity);
            rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
            var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
            float wob = 0; var sw = new System.Diagnostics.Stopwatch();
            for (int f = 0; f < 120; f++)
            {
                float x = 0.15f * MathF.Sin(2 * MathF.PI * 2f * f * Dt);
                var anchor = new AnchorPose(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
                sw.Start();
                rig.Step(Dt, anchor);
                rig.Deform(anchor, pos, nrm, out _, out _);
                sw.Stop();
                wob = MathF.Max(wob, Vector3.Distance(pos[cornerV], m.Pos[cornerV]));
            }
            return (wob, sw.Elapsed.TotalMilliseconds / 120, rig.Sim.ResetCount, rig.Cage.PointCount, rig.Deformer.DynamicVertexCount, rig.Cage.ActiveCells, rig.Cage.TotalCells);
        }
        var sparse = Run(soft);
        var dense = Run(m.Pos.Select(_ => 1f).ToArray()); // everything soft: every cell is in the cage
        Console.WriteLine($"  corner only : {sparse.cells}/{sparse.total} cells, {sparse.points} points, {sparse.moving} moving verts (of {softVerts} soft), " +
                          $"corner wobble {sparse.wobble * 1000:0} mm, {sparse.ms:0.000} ms/frame, resets {sparse.resets}");
        Console.WriteLine($"  all soft    : {dense.cells}/{dense.total} cells, {dense.points} points, {dense.moving} moving verts, {dense.ms:0.000} ms/frame");
        Check(sparse.points < dense.points / 3, "the cage only covers the soft corner (< 1/3 of the full cage's points)");
        Check(sparse.moving <= softVerts, "firm vertices never move (only soft ones are deformed)");
        Check(sparse.wobble > 0.01f && sparse.resets == 0, "the soft corner still wobbles (> 10 mm), no resets");
        Check(sparse.ms < 0.5 * dense.ms, "and costs less than half of the full cage per frame");
    }

    static (float r, float g, float b) Hsv(float h, float s, float v)
    {
        float c = v * s, hp = (h % 1f) * 6f, x = c * (1 - MathF.Abs(hp % 2 - 1)), m = v - c;
        var (r, g, b) = hp < 1 ? (c, x, 0f) : hp < 2 ? (x, c, 0f) : hp < 3 ? (0f, c, x) : hp < 4 ? (0f, x, c) : hp < 5 ? (x, 0f, c) : (c, 0f, x);
        return (r + m, g + m, b + m);
    }

    static void HueBandTest()
    {
        Console.WriteLine("TEST hue bands: left half painted red, right half blue (same brightness gradient); Red x4 stiffer/follow, Blue x0.3");
        float[] W(float r, float g, float b) { var w = new float[HueBands.Count]; HueBands.Weights(r, g, b, w); return w; }
        string Fmt(float[] w) => string.Join(" ", w.Select((x, i) => x > 1e-3f ? $"{HueBands.Names[i]} {x:0.##}" : null).Where(x => x != null));
        var samples = new (string, float, float, float, int)[]
        {
            ("red", 1, 0, 0, 0), ("orange", 1, 0.5f, 0, 1), ("yellow", 1, 1, 0, 2), ("green", 0, 1, 0, 3),
            ("blue", 0, 0, 1, 4), ("indigo", 0.5f, 0, 1, 5), ("violet", 1, 0, 1, 6),
        };
        bool pure = true;
        foreach (var (name, r, g, b, band) in samples)
        {
            var w = W(r, g, b);
            Console.WriteLine($"  {name,-7} ({r:0.#},{g:0.#},{b:0.#}) -> {Fmt(w)}");
            pure &= MathF.Abs(w[band] - 1f) < 1e-4f && MathF.Abs(w.Sum() - 1f) < 1e-4f;
        }
        Check(pure, "each pure rainbow colour lands fully in its own band");
        var dark = W(0.5f, 0, 0); var grey = W(0.5f, 0.5f, 0.5f); var pastel = W(1f, 0.5f, 0.5f);
        Check(MathF.Abs(dark[0] - 1f) < 1e-4f && grey.Sum() == 0f && MathF.Abs(pastel[0] - 0.5f) < 1e-4f,
              "a dark red is still fully Red; grey uses the plain settings; a pastel (half saturated) red is half Red");
        var cyan = W(0, 1, 1);
        Check(MathF.Abs(cyan[3] - 0.5f) < 1e-4f && MathF.Abs(cyan[4] - 0.5f) < 1e-4f, "in-between hues blend their neighbours (cyan = half Green, half Blue)");

        var m = Cube(30);
        var bands = new float[m.VertexCount * HueBands.Count];
        for (int v = 0; v < m.VertexCount; v++) bands[v * HueBands.Count + (m.Pos[v].X < 0 ? 0 : 4)] = 1f;
        var s = new SoftbodySettings();
        s.BandStiffness[0] = 4; s.BandFollow[0] = 4;
        s.BandStiffness[4] = 0.3f; s.BandFollow[4] = 0.3f;
        (float left, float right, int resets) Shake(SoftbodySettings settings, SoftbodySettings tuneAt60 = null)
        {
            using var rig = SoftbodyRig.Create(m.Pos, m.Nrm, m.Tri, m.Soft, settings, AnchorPose.Identity, bands);
            rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
            int np = rig.Cage.PointCount;
            var pts = new Vector3[np]; var tgt = new Vector3[np];
            float left = 0, right = 0;
            for (int f = 0; f < 180; f++)
            {
                if (f == 60 && tuneAt60 != null) Check(rig.ApplyTuning(tuneAt60) == TuneResult.Applied, "changing band multipliers is applied live (no rebuild)");
                float x = 0.15f * MathF.Sin(2 * MathF.PI * 2f * f * Dt);
                rig.Step(Dt, new AnchorPose(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One));
                if (f < 90) continue;
                rig.Sim.GetPositions(pts); rig.Sim.GetTargets(tgt);
                for (int i = 0; i < np; i++)
                {
                    if (rig.Cage.PointSoftness[i] < 0.9f) continue;
                    float e = Vector3.Distance(pts[i], tgt[i]);
                    if (rig.Cage.RestPoints[i].X < -0.1f) left = MathF.Max(left, e);
                    else if (rig.Cage.RestPoints[i].X > 0.1f) right = MathF.Max(right, e);
                }
            }
            return (left, right, rig.Sim.ResetCount);
        }
        var plain = Shake(new SoftbodySettings());
        var banded = Shake(s);
        var live = Shake(new SoftbodySettings(), s);
        Console.WriteLine($"  soft-point lag while shaking: plain L {plain.left * 1000:0} / R {plain.right * 1000:0} mm; banded (red x4, blue x0.3) L {banded.left * 1000:0} / R {banded.right * 1000:0} mm; " +
                          $"tuned live at 1 s: L {live.left * 1000:0} / R {live.right * 1000:0} mm; resets {plain.resets + banded.resets + live.resets}");
        Check(MathF.Abs(plain.left - plain.right) < 0.25f * MathF.Max(plain.left, plain.right), "without band multipliers both halves behave alike");
        Check(banded.left < 0.6f * banded.right, "the stiff red half lags clearly less than the loose blue half");
        Check(live.left < 0.6f * live.right && plain.resets + banded.resets + live.resets == 0, "the same after tuning the bands live, no resets");
    }

    static void HsvSoftness()
    {
        Console.WriteLine("TEST HSV softness: Value (brightness) is the softness, whatever the hue");
        float worst = 0;
        foreach (float v in new[] { 0f, 0.25f, 0.6f, 1f })
            for (int k = 0; k < 24; k++)
                foreach (float sat in new[] { 0f, 0.5f, 1f })
                {
                    var (r, g, b) = Hsv(k / 24f, sat, v);
                    worst = MathF.Max(worst, MathF.Abs(SoftnessMap.FromColor(r, g, b, 1f, SoftnessSource.Value, false) - v));
                }
        Console.WriteLine($"  24 hues x 3 saturations x 4 values: worst error {worst:E1}");
        Check(worst < 1e-5f, "every hue and saturation with the same value gives the same softness");
        Check(SoftnessMap.FromColor(0, 0, 0, 1, SoftnessSource.Value, false) == 0f && SoftnessMap.FromColor(1, 1, 1, 1, SoftnessSource.Value, false) == 1f,
              "black is firm, white is fully squishy");
        Check(MathF.Abs(SoftnessMap.FromColor(0.1f, 0.2f, 0.25f, 1, SoftnessSource.Value, true) - 0.75f) < 1e-6f, "Invert flips it (value 0.25 -> softness 0.75)");
        Check(SoftnessMap.FromColor(0, 1, 0, 1, SoftnessSource.Red, false) == 0f, "the Red channel mode still reads only red (older objects keep working)");
    }

    static void SpherePress()
    {
        Console.WriteLine("TEST sphere press: a 0.3 m sphere collider pushed 0.2 m into the soft top at 0.5 m/s, held, then lifted away");
        var now = PressRun(1);
        var old = PressRun(30);
        Console.WriteLine($"  per-frame proxy (new): reacts {now.delay * 1000:0} ms after contact, dent {now.dent * 1000:0} mm, back to {now.residual * 1000:0.0} mm of rest, resets {now.resets}");
        Console.WriteLine($"  0.5 s proxy refresh (old): reacts {old.delay * 1000:0} ms after contact, dent {old.dent * 1000:0} mm");
        Check(now.delay < 0.05f, "the top starts to dent within 50 ms of the sphere touching it");
        Check(now.dent > 0.1f, "the sphere pushes a clear dent into the top (> 100 mm)");
        Check(now.residual < 0.01f && now.resets == 0, "springs back after the sphere leaves (within 10 mm), no resets");
    }

    static void Smoothness()
    {
        Console.WriteLine("TEST smoothness: a smooth bump pushed into the cage; creases on the top face, linear (old) vs B-spline (new) embedding");
        var m = Cube(40, 0.6f, gradient: false);
        var cage = Cage.Build(m.Pos, ReadOnlySpan<float>.Empty, new SoftbodySettings());
        Vector3 Bump(Vector3 p)
        {
            float s = MathF.Sin(MathF.PI * (p.X + 0.3f) / 0.6f) * MathF.Sin(MathF.PI * (p.Z + 0.3f) / 0.6f);
            return p + new Vector3(0.03f * s, 0.08f * s * (p.Y / 0.6f), 0f);
        }
        var lin = DeformWith(m, cage, false, Bump, out _, out _);
        var smo = DeformWith(m, cage, true, Bump, out _, out _);
        var (lMax, l99) = TopCreases(m, lin);
        var (sMax, s99) = TopCreases(m, smo);
        float lift = 0;
        int top = TopCentre(m);
        lift = smo[top].Y - m.Pos[top].Y;
        Console.WriteLine($"  linear: worst crease {lMax:0.00} deg (99th pct {l99:0.00}); smooth: worst {sMax:0.00} deg (99th pct {s99:0.00}); top centre lift {lift * 1000:0.0} mm (field 80 mm)");
        Check(sMax < 0.5f * lMax, "smooth embedding halves (at least) the worst crease angle");
        Check(sMax < 2.5f, "smooth embedding: no crease sharper than 2.5 deg between neighbouring triangles");
        Check(lift > 0.04f, "the smooth field still carries most of the bump (> 40 of 80 mm)");
    }

    static void Settle()
    {
        Console.WriteLine("TEST settle: gradient cube (bottom firm, top soft) on a floor, 4 s");
        var m = Cube(40);
        var anchor = AnchorPose.Identity;
        using var rig = Rig(m, new SoftbodySettings(), anchor);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        Console.WriteLine($"  {Describe(rig)}");
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        int top = TopCentre(m);
        double vMin = 1, vMax = 1;
        for (int f = 0; f < 240; f++)
        {
            rig.Step(Dt, anchor);
            double v = rig.VolumeRatio(); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
        }
        rig.Deform(anchor, pos, nrm, out _, out _);
        float sag = m.Pos[top].Y - pos[top].Y;
        float speed = rig.Sim.MaxSpeed();
        Console.WriteLine($"  top-centre sag {sag * 1000:0.0} mm, max point speed after 4 s {speed:0.0000} m/s, volume ratio {vMin:0.0000}..{vMax:0.0000}, resets {rig.Sim.ResetCount}");
        Check(rig.Sim.ResetCount == 0, "no explosion (no resets)");
        Check(speed < 0.05f, "settled (max point speed < 0.05 m/s)");
        Check(sag > 0f && sag < 0.1f, "soft top sags a little under gravity (0..100 mm)");
        Check(vMin > 0.85 && vMax < 1.15, "volume within 15% of rest");
    }

    static void Drop()
    {
        Console.WriteLine("TEST drop: all-soft cube (no colours) released 0.4 m above the floor, weak follow servo, 4 s");
        var m = Cube(30);
        var s = new SoftbodySettings { Follow = 0.6f };
        var start = new AnchorPose(new Vector3(0, 0.4f, 0), Quaternion.Identity, Vector3.One);
        var anchor = AnchorPose.Identity; // rest pose: bottom on the floor
        using var rig = Rig(m, s, start, useSoftness: false);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        Console.WriteLine($"  {Describe(rig)}");
        var pts = new Vector3[rig.Cage.PointCount];
        float minY = float.MaxValue, peakSpeed = 0, minHeight = float.MaxValue, maxWidth = 0; double vMin = 1, vMax = 1;
        rig.Sim.GetPositions(pts);
        float restH = pts.Max(p => p.Y) - pts.Min(p => p.Y), restW = pts.Max(p => p.X) - pts.Min(p => p.X);
        for (int f = 0; f < 240; f++)
        {
            rig.Step(Dt, anchor);
            rig.Sim.GetPositions(pts);
            foreach (var p in pts) minY = MathF.Min(minY, p.Y - rig.Sim.RadiusWorld);
            minHeight = MathF.Min(minHeight, pts.Max(p => p.Y) - pts.Min(p => p.Y));
            maxWidth = MathF.Max(maxWidth, pts.Max(p => p.X) - pts.Min(p => p.X));
            peakSpeed = MathF.Max(peakSpeed, rig.Sim.MaxSpeed());
            double v = rig.VolumeRatio(); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
        }
        float speed = rig.Sim.MaxSpeed();
        Console.WriteLine($"  impact squish: cage height down to {100 * minHeight / restH:0.0}% of rest, width up to {100 * maxWidth / restW:0.0}%");
        Console.WriteLine($"  lowest sphere bottom {minY * 1000:0.0} mm (floor = 0), peak speed {peakSpeed:0.00} m/s, final max speed {speed:0.0000} m/s, volume ratio {vMin:0.0000}..{vMax:0.0000}, resets {rig.Sim.ResetCount}");
        Check(rig.Sim.ResetCount == 0, "no explosion (no resets)");
        Check(minY > -0.02f, "does not sink through the floor (< 20 mm penetration)");
        Check(speed < 0.05f, "settled on the floor (max point speed < 0.05 m/s)");
        Check(vMin > 0.8 && vMax < 1.2, "volume within 20% of rest through the impact");
    }

    static void Shake()
    {
        Console.WriteLine("TEST shake: gradient cube, anchor shaken +-15 cm at 2 Hz for 2 s, then held still 2 s");
        var m = Cube(40);
        using var rig = Rig(m, new SoftbodySettings(), AnchorPose.Identity);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        Console.WriteLine($"  {Describe(rig)}");
        int np = rig.Cage.PointCount;
        var pts = new Vector3[np]; var tgt = new Vector3[np];
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        int top = TopCentre(m);
        float kinErr = 0, softLag = 0, topWobble = 0; double vMin = 1, vMax = 1;
        float[] after = new float[4]; float[] marks = { 0.25f, 0.5f, 1f, 2f };
        float stopSpeed = 0;
        for (int f = 0; f < 240; f++)
        {
            float t = f * Dt;
            float x = t < 2f ? 0.15f * MathF.Sin(2 * MathF.PI * 2f * t) : 0f;
            var anchor = new AnchorPose(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
            rig.Step(Dt, anchor);
            rig.Sim.GetPositions(pts); rig.Sim.GetTargets(tgt);
            for (int i = 0; i < np; i++)
            {
                float e = Vector3.Distance(pts[i], tgt[i]);
                if (rig.Cage.Kinematic[i]) kinErr = MathF.Max(kinErr, e);
                else if (rig.Cage.PointSoftness[i] > 0.9f) softLag = MathF.Max(softLag, e);
            }
            double v = rig.VolumeRatio(); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            rig.Deform(anchor, pos, nrm, out _, out _);
            float wob = MathF.Abs(pos[top].X - m.Pos[top].X); // top-centre offset in the slot's frame
            if (t < 2f) topWobble = MathF.Max(topWobble, wob);
            else
            {
                float since = t - 2f + Dt;
                for (int k = 0; k < marks.Length; k++) if (since <= marks[k]) { after[k] = MathF.Max(after[k], wob); break; } // window (marks[k-1], marks[k]]
                if (MathF.Abs(since - 0.5f) < Dt / 2) stopSpeed = rig.Sim.MaxSpeed();
            }
        }
        float speed = rig.Sim.MaxSpeed();
        Console.WriteLine($"  pinned points max error {kinErr * 1000:0.000} mm; soft points max lag {softLag * 1000:0.0} mm");
        Console.WriteLine($"  top-centre wobble (x offset in slot frame): during shake {topWobble * 1000:0.0} mm; after stop " +
                          string.Join(", ", marks.Select((mk, k) => $"{(k == 0 ? 0 : marks[k - 1]):0.##}-{mk:0.##} s: {after[k] * 1000:0.0} mm")));
        Console.WriteLine($"  max point speed 0.5 s after stop {stopSpeed:0.000} m/s, after 2 s {speed:0.0000} m/s; volume ratio {vMin:0.0000}..{vMax:0.0000}; resets {rig.Sim.ResetCount}");
        Check(rig.Sim.ResetCount == 0, "no explosion (no resets)");
        Check(kinErr < 1e-3f, "firm (pinned) points follow the anchor exactly (< 1 mm)");
        Check(softLag > 0.01f, "soft points lag behind the anchor (> 10 mm)");
        Check(topWobble > 0.01f, "visible wobble at the top of the mesh (> 10 mm)");
        Check(after[3] < 0.25f * after[0] + 0.002f && speed < 0.05f, "wobble dies down after the shaking stops (1-2 s window < 25% of the first 0.25 s)");
        Check(vMin > 0.85 && vMax < 1.15, "volume within 15% of rest while shaking");
    }


    static void Spin()
    {
        Console.WriteLine("TEST spin: gradient cube at 2x scale, anchor yawed 90 deg in 0.25 s, then held; plus a 1 m/s carry");
        var m = Cube(30);
        var scale = new Vector3(2f);
        using var rig = Rig(m, new SoftbodySettings(), new AnchorPose(Vector3.Zero, Quaternion.Identity, scale));
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        int np = rig.Cage.PointCount;
        var pts = new Vector3[np]; var tgt = new Vector3[np];
        float kinErr = 0, softLag = 0, lateLag = 0, carryLag = 0;
        for (int f = 0; f < 300; f++)
        {
            float t = f * Dt;
            float yaw = MathF.PI / 2 * Math.Clamp(t / 0.25f, 0f, 1f);
            float x = t > 3f ? (t - 3f) * 1f : 0f; // carry at 1 m/s for the last second
            var anchor = new AnchorPose(new Vector3(x, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), scale);
            rig.Step(Dt, anchor);
            rig.Sim.GetPositions(pts); rig.Sim.GetTargets(tgt);
            for (int i = 0; i < np; i++)
            {
                float e = Vector3.Distance(pts[i], tgt[i]);
                if (rig.Cage.Kinematic[i]) kinErr = MathF.Max(kinErr, e);
                else if (rig.Cage.PointSoftness[i] > 0.9f)
                {
                    if (t < 1f) softLag = MathF.Max(softLag, e);
                    else if (t > 2.5f && t <= 3f) lateLag = MathF.Max(lateLag, e);
                    else if (t > 3.7f) carryLag = MathF.Max(carryLag, e);
                }
            }
        }
        Console.WriteLine($"  pinned max error {kinErr * 1000:0.000} mm; soft lag during the turn {softLag * 1000:0.0} mm, 2.25-2.75 s after it {lateLag * 1000:0.0} mm; steady lag while carried at 1 m/s {carryLag * 1000:0.0} mm; resets {rig.Sim.ResetCount}");
        Check(rig.Sim.ResetCount == 0, "no explosion (no resets)");
        Check(kinErr < 1e-3f, "pinned points follow rotation exactly (< 1 mm)");
        Check(softLag > 0.02f, "soft points lag behind the turn (> 20 mm)");
        Check(lateLag < 0.25f * softLag, "and settle afterwards (< 25% of the lag)");
    }

    // ------------------------------------------------------------------ live tuning

    /// <summary>What one settle / shake / hold episode measured. Wobble = top-centre x offset in the slot frame.</summary>
    sealed class Episode
    {
        public float SoftLag, PeakDuring, First, Late, FinalSpeed;
        public int Crossings;
        public double VMin = 1, VMax = 1;
        public float[] Hold;
        public override string ToString() =>
            $"soft lag {SoftLag * 1000:0.0} mm, wobble during shake {PeakDuring * 1000:0.0} mm; after stop 0-0.25 s {First * 1000:0.0} mm, " +
            $"0.5-1.5 s {Late * 1000:0.0} mm (late/first {Late / MathF.Max(First, 1e-6f):0.00}), {Crossings} zero crossings in 1.5 s; " +
            $"final speed {FinalSpeed:0.0000} m/s; volume {VMin:0.000}..{VMax:0.000}";
    }

    /// <summary>Settle 2 s, shake +-15 cm at 2 Hz for 1 s, hold still 2 s.</summary>
    static Episode RunEpisode(SoftbodyRig rig, TestMesh m)
    {
        var e = new Episode();
        int np = rig.Cage.PointCount, top = TopCentre(m);
        var pts = new Vector3[np]; var tgt = new Vector3[np];
        var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
        for (int f = 0; f < 120; f++) rig.Step(Dt, AnchorPose.Identity);
        var hold = new List<float>();
        for (int f = 0; f < 180; f++)
        {
            float t = f * Dt;
            float x = t < 1f ? 0.15f * MathF.Sin(2 * MathF.PI * 2f * t) : 0f;
            var anchor = new AnchorPose(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One);
            rig.Step(Dt, anchor);
            double v = rig.VolumeRatio(); e.VMin = Math.Min(e.VMin, v); e.VMax = Math.Max(e.VMax, v);
            rig.Deform(anchor, pos, nrm, out _, out _);
            float wob = pos[top].X - m.Pos[top].X;
            if (t < 1f)
            {
                e.PeakDuring = MathF.Max(e.PeakDuring, MathF.Abs(wob));
                rig.Sim.GetPositions(pts); rig.Sim.GetTargets(tgt);
                for (int i = 0; i < np; i++)
                    if (!rig.Cage.Kinematic[i] && rig.Cage.PointSoftness[i] > 0.9f) e.SoftLag = MathF.Max(e.SoftLag, Vector3.Distance(pts[i], tgt[i]));
            }
            else hold.Add(wob);
        }
        e.Hold = hold.ToArray();
        for (int k = 0; k < e.Hold.Length; k++)
        {
            float since = (k + 1) * Dt;
            if (since <= 0.25f) e.First = MathF.Max(e.First, MathF.Abs(e.Hold[k]));
            else if (since > 0.5f && since <= 1.5f) e.Late = MathF.Max(e.Late, MathF.Abs(e.Hold[k]));
            if (k > 0 && since <= 1.5f && MathF.Sign(e.Hold[k]) != MathF.Sign(e.Hold[k - 1]) && MathF.Abs(e.Hold[k] - e.Hold[k - 1]) > 1e-4f) e.Crossings++;
        }
        e.FinalSpeed = rig.Sim.MaxSpeed();
        return e;
    }

    static void LiveTuning()
    {
        Console.WriteLine("TEST live tuning: stiffness 6 -> 18 Hz and damping 0.15 -> 0.6 applied to a running cage (no rebuild), vs a fresh cage");
        var m = Cube(40);
        var baseSettings = new SoftbodySettings();
        var tuned = new SoftbodySettings { Stiffness = 18f, Damping = 0.6f };
        using var live = Rig(m, baseSettings, AnchorPose.Identity);
        live.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var cage = live.Cage; int points = cage.PointCount, edges = live.Sim.EdgeConstraintCount, vols = live.Sim.VolumeConstraintCount;

        var a = RunEpisode(live, m);
        Console.WriteLine($"  A  live cage, defaults        : {a}");

        using (var warm = Rig(m, baseSettings, AnchorPose.Identity)) { warm.Step(Dt, AnchorPose.Identity); warm.ApplyTuning(tuned); } // JIT the tuning path first
        var sw = Stopwatch.StartNew();
        var result = live.ApplyTuning(tuned);
        double tuneMs = sw.Elapsed.TotalMilliseconds;
        var again = live.ApplyTuning(tuned);
        var b = RunEpisode(live, m);
        Console.WriteLine($"  B  same cage after ApplyTuning: {b}");

        using var fresh = Rig(m, tuned, AnchorPose.Identity);
        fresh.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var c = RunEpisode(fresh, m);
        Console.WriteLine($"  C  fresh cage, tuned settings : {c}");

        float maxDiff = 0, maxC = 0;
        for (int k = 0; k < Math.Min(b.Hold.Length, c.Hold.Length); k++) { maxDiff = MathF.Max(maxDiff, MathF.Abs(b.Hold[k] - c.Hold[k])); maxC = MathF.Max(maxC, MathF.Abs(c.Hold[k])); }
        var structural = live.ApplyTuning(new SoftbodySettings { Cells = 5, Stiffness = 18f, Damping = 0.6f });
        Console.WriteLine($"  ApplyTuning: {result} in {tuneMs:0.000} ms (again with the same values: {again}); with cells 4 -> 5: {structural}");
        Console.WriteLine($"  B vs C wobble trace after the stop: max difference {maxDiff * 1000:0.00} mm (C peak {maxC * 1000:0.0} mm); " +
                          $"tunes {live.Sim.TuneCount}, resets {live.Sim.ResetCount}, effective {live.Settings.Stiffness} Hz / {live.Settings.Damping}");

        Check(result == TuneResult.Applied && again == TuneResult.Unchanged, "ApplyTuning applies once, and reports Unchanged for the same values");
        Check(ReferenceEquals(live.Cage, cage) && live.Cage.PointCount == points && live.Sim.EdgeConstraintCount == edges && live.Sim.VolumeConstraintCount == vols,
              "no rebuild: same cage, same points and constraints");
        Check(structural == TuneResult.NeedsRebuild && live.Settings.Cells == 4, "a structural change (cells) is refused and left for a rebuild");
        Check(live.Sim.ResetCount == 0 && fresh.Sim.ResetCount == 0 && b.VMin > 0.85 && b.VMax < 1.15, "no explosion after tuning (no resets, volume within 15%)");
        Check(tuneMs < 1.0, "re-tuning a 5x5x5 cage takes < 1 ms (warm)");
        Check(b.SoftLag < 0.7f * a.SoftLag, "stiffer: soft points lag less while shaking (< 70% of before)");
        Check(b.Late / MathF.Max(b.First, 1e-6f) < 0.6f * (a.Late / MathF.Max(a.First, 1e-6f)), "more damping: the wobble dies faster after the stop");
        Check(maxDiff < 0.15f * maxC + 0.002f, "the live-tuned cage wobbles like a fresh cage built with the same settings");
    }

    static void LiveTuningSmoke()
    {
        Console.WriteLine("TEST live tuning smoke: every tunable changed at once mid-shake (mass, gravity, drag, friction, iterations, follow, firm, volume, collide)");
        var m = Cube(30);
        using var rig = Rig(m, new SoftbodySettings(), AnchorPose.Identity);
        rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
        var cage = rig.Cage;
        double vMin = 1, vMax = 1; float maxSpeed = 0;
        var results = new List<TuneResult>();
        for (int f = 0; f < 360; f++)
        {
            float t = f * Dt;
            if (f == 60) results.Add(rig.ApplyTuning(new SoftbodySettings { Mass = 6f, Gravity = 0.5f, LinearDamping = 1f, Friction = 0.2f, SolverIterations = 4, Follow = 5f, RigidFrequency = 20f, VolumeStiffness = 10f, Collide = false }));
            if (f == 120) results.Add(rig.ApplyTuning(new SoftbodySettings { Mass = 0.5f, Gravity = 2f, LinearDamping = 0f, Friction = 2f, SolverIterations = 16, Follow = 1f, RigidFrequency = 45f, Stiffness = 3f, Damping = 0.05f, Collide = true }));
            if (f == 240) results.Add(rig.ApplyTuning(new SoftbodySettings()));
            float x = t < 4f ? 0.12f * MathF.Sin(2 * MathF.PI * 1.5f * t) : 0f;
            rig.Step(Dt, new AnchorPose(new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One));
            double v = rig.VolumeRatio(); vMin = Math.Min(vMin, v); vMax = Math.Max(vMax, v);
            maxSpeed = MathF.Max(maxSpeed, rig.Sim.MaxSpeed());
        }
        Console.WriteLine($"  results {string.Join(", ", results)}; iterations now {rig.Sim.SolverIterations}; tunes {rig.Sim.TuneCount}; resets {rig.Sim.ResetCount}; " +
                          $"peak speed {maxSpeed:0.00} m/s; volume {vMin:0.000}..{vMax:0.000}");
        Check(results.All(r => r == TuneResult.Applied) && rig.Sim.TuneCount == 3 && ReferenceEquals(rig.Cage, cage), "all three tunings applied live to the same cage");
        Check(rig.Sim.SolverIterations == new SoftbodySettings().SolverIterations, "solver iteration count follows the setting");
        Check(rig.Sim.ResetCount == 0 && float.IsFinite(maxSpeed) && maxSpeed < 10f && vMin > 0.75 && vMax < 1.25, "no explosion through extreme re-tunes (no resets, volume within 25%)");
    }

    static void Timing()
    {
        Console.WriteLine("TEST timing (Release, single thread): per 60 fps frame = Step(1/60) [2 substeps of 1/120] + deform + normals");
        Console.WriteLine("  verts   cells  points  moving  normals |  sim ms (avg/p95)  deform+normals ms (avg/p95)  total ms");
        foreach (var (subdiv, cells, gradient) in new[] { (40, 4, true), (40, 4, false), (40, 6, false), (40, 8, false), (82, 4, false) })
        {
            var m = Cube(subdiv, 0.6f, gradient);
            var s = new SoftbodySettings { Cells = cells };
            using var rig = Rig(m, s, AnchorPose.Identity, useSoftness: gradient);
            rig.Sim.SetGround(Vector3.Zero, Vector3.UnitY);
            var pos = (Vector3[])m.Pos.Clone(); var nrm = (Vector3[])m.Nrm.Clone();
            var simT = new List<double>(); var defT = new List<double>();
            var sw = new Stopwatch();
            for (int f = 0; f < 900; f++)
            {
                float t = f * Dt;
                var anchor = new AnchorPose(new Vector3(0.15f * MathF.Sin(2 * MathF.PI * 1.5f * t), 0, 0.1f * MathF.Sin(2 * MathF.PI * 0.7f * t)),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f * MathF.Sin(t)), Vector3.One);
                sw.Restart(); rig.Step(Dt, anchor); double a = sw.Elapsed.TotalMilliseconds;
                sw.Restart(); rig.Deform(anchor, pos, nrm, out _, out _); double b = sw.Elapsed.TotalMilliseconds;
                if (f >= 300) { simT.Add(a); defT.Add(b); } // skip JIT/tiering warm-up
            }
            static double P95(List<double> l) { var c = l.OrderBy(x => x).ToList(); return c[(int)(c.Count * 0.95)]; }
            Console.WriteLine($"  {m.VertexCount,6}  {cells,4}   {rig.Cage.PointCount,5}  {rig.Deformer.DynamicVertexCount,6}  {rig.Deformer.NormalVertexCount,6}  |" +
                              $"  {simT.Average(),6:0.000} / {P95(simT),6:0.000}      {defT.Average(),6:0.000} / {P95(defT),6:0.000}            {simT.Average() + defT.Average(),6:0.000}" +
                              (gradient ? "   (gradient map)" : "   (all soft)") + (rig.Sim.ResetCount > 0 ? $"  RESETS {rig.Sim.ResetCount}" : ""));
            if (subdiv == 40 && cells == 4)
                Check(simT.Average() + defT.Average() < 3.0, $"~10k verts, 5x5x5 cage ({(gradient ? "gradient" : "all soft")}) under the 3 ms budget");
        }
    }
}
