using System.Numerics;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Cloth;

// 0.30.0: avatar cloth (DESIGN-cloth.md phase 2) and the world cloth's size fix. Run with:  dotnet run -c Release --project tests -- avatar-cloth
// (also part of "cloth"). CPU only, a few seconds.
namespace Softbody.Tests;

static partial class Program
{
    static void AvatarClothTests()
    {
        Console.WriteLine("== Avatar cloth (0.30.0)");
        AvatarRegion();
        AvatarSpheresAndSide();
        AvatarCopyRoundTrip();
        AvatarTearsInPlace();
        AvatarFollowsPinned();
        AvatarInertia();
        AvatarSnapTurnAndTeleport();
        AvatarBackstop();
        AvatarOwnBody();
        ClothScaledRenderer();
        ClothRenderRoom();
        AvatarForkHandshake();
    }

    /// <summary>The plugin sends avatar tears in place only when it finds the fork's v2 event: the two names must match.</summary>
    static void AvatarForkHandshake()
    {
        Console.WriteLine("-- the renderer fork's v2 handshake");
        var dir = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "TestDataDir")?.Value;
        var src = dir == null ? null : Path.GetFullPath(Path.Combine(dir, @"..\..\..\RenderiteSrc\Renderite.Unity\Assets\Mesh\MeshAsset.cs"));
        if (src == null || !File.Exists(src)) { Console.WriteLine("   (skipped: no fork source at " + src + ")"); return; }
        var text = File.ReadAllText(src);
        Check(text.Contains("SoftbodyHandshakeNameV2 = @\"" + PartialEncode.HandshakeNameV2 + "\""),
              "the fork opens the event the plugin looks for (" + PartialEncode.HandshakeNameV2 + ")");
        Check(text.Contains("SoftbodyHandshakeName = @\"" + PartialEncode.HandshakeName + "\""), "and still the v1 one (" + PartialEncode.HandshakeName + ")");
    }

    // ------------------------------------------------------------------ the test skirt

    /// <summary>
    /// A skirt: a flared tube around the y axis (radius 0.2 m at the hem, y = 0, to 0.15 m at the waist, y = 0.3), 24
    /// segments and 10 rows, with a UV seam (the first column again at the end), outward winding. Two bones: the hips (at the
    /// waist) and a leg; the lower rows blend toward the leg. One blendshape flares the hem 3 cm out. Rows 8 and 9 are
    /// painted held, the rest free.
    /// </summary>
    sealed class Skirt
    {
        public const int Cols = 24, Rows = 10, Stride = Cols + 1;
        public Vector3[] Pos, Nrm;
        public Vector2[] Uv;
        public int[] Tri;
        public int[] Bones4;
        public float[] Weights4;
        public Vector3[] Flare;
        public float4x4[] BindPoseX;
        public Matrix4x4[] BindPoses;
        public SkinData Skin;
        public float[] Bright;
        public int VertexCount => Pos.Length;
        public static int Id(int i, int j) => j * Stride + i;
    }

    static Skirt MakeSkirt(Func<int, int, bool> held = null)
    {
        held ??= (i, j) => j >= 8;
        var k = new Skirt();
        int n = Skirt.Stride * Skirt.Rows;
        k.Pos = new Vector3[n]; k.Nrm = new Vector3[n]; k.Uv = new Vector2[n]; k.Flare = new Vector3[n];
        k.Bones4 = new int[n * 4]; k.Weights4 = new float[n * 4]; k.Bright = new float[n];
        for (int j = 0; j < Skirt.Rows; j++)
            for (int i = 0; i < Skirt.Stride; i++)
            {
                int v = Skirt.Id(i, j);
                float y = 0.3f * j / (Skirt.Rows - 1);
                float r = 0.15f + 0.05f * (1f - y / 0.3f);
                float th = 2f * MathF.PI * (i % Skirt.Cols) / Skirt.Cols;
                var radial = new Vector3(MathF.Cos(th), 0f, MathF.Sin(th));
                k.Pos[v] = radial * r + new Vector3(0f, y, 0f);
                k.Nrm[v] = Vector3.Normalize(radial * 0.3f + new Vector3(0f, 0.05f, 0f));
                k.Uv[v] = new Vector2(i / (float)Skirt.Cols, j / (float)(Skirt.Rows - 1));
                float leg = 0.5f * (1f - y / 0.3f);
                k.Bones4[v * 4] = 0; k.Weights4[v * 4] = 1f - leg;
                k.Bones4[v * 4 + 1] = 1; k.Weights4[v * 4 + 1] = leg;
                if (j <= 2) k.Flare[v] = radial * 0.03f * (1f - j / 3f);
                k.Bright[v] = held(i % Skirt.Cols, j) ? 0f : 1f;
            }
        var tri = new List<int>();
        for (int j = 0; j + 1 < Skirt.Rows; j++)
            for (int i = 0; i < Skirt.Cols; i++)
            {
                int a = Skirt.Id(i, j), b = Skirt.Id(i + 1, j), c = Skirt.Id(i + 1, j + 1), d = Skirt.Id(i, j + 1);
                tri.AddRange(new[] { a, c, b }); tri.AddRange(new[] { a, d, c });     // outward
            }
        k.Tri = tri.ToArray();
        // bind poses (bind space to bone space): any invertible ones; the hips' head at the waist's centre
        k.BindPoseX = new[]
        {
            float4x4.Transform(new float3(0f, 0.3f, 0f), floatQ.Euler(0f, 20f, 0f), float3.One).Inverse,
            float4x4.Transform(new float3(0.08f, 0.2f, 0.01f), floatQ.Euler(5f, -10f, 15f), new float3(1.1f, 1.1f, 1.1f)).Inverse,
        };
        k.BindPoses = k.BindPoseX.Select(b => SkinMatrices.ToNumerics(b)).ToArray();
        k.Skin = SkinData.FromBindings(2, k.Bones4, k.Weights4);
        return k;
    }

    /// <summary>Bone to world for a pose: the whole avatar moved by <paramref name="root"/> (world, engine order), the leg
    /// swung by <paramref name="legDeg"/> about its own x axis. In the rest pose (root identity, leg 0) every skin matrix is
    /// the identity: the skirt stands in the world as modelled.</summary>
    static float4x4[] SkirtPose(Skirt k, float4x4 root, float legDeg = 0f)
    {
        var hips = root * k.BindPoseX[0].Inverse;
        var leg = root * k.BindPoseX[1].Inverse * float4x4.Transform(float3.Zero, floatQ.Euler(legDeg, 0f, 0f), float3.One);
        return new[] { hips, leg };
    }

    static Matrix4x4[] SkinOf(Skirt k, float4x4[] boneT)
    {
        var m = new Matrix4x4[3];
        for (int b = 0; b < 2; b++) m[b] = SkinMatrices.Skin(k.BindPoses[b], boneT[b]);
        m[2] = Matrix4x4.Identity;
        return m;
    }

    static BlendshapeDeltas SkirtBlend(Skirt k, int[] verts)
    {
        return BlendshapeDeltas.FromPositions(1, _ =>
        {
            var vi = new List<int>(); var d = new List<Vector3>();
            for (int r = 0; r < verts.Length; r++) if (k.Flare[verts[r]] != Vector3.Zero) { vi.Add(r); d.Add(k.Flare[verts[r]]); }
            return (vi.ToArray(), d.ToArray());
        });
    }

    /// <summary>The avatar cloth as the plugin builds it (ClothInstance.TryCreateSkinned), from the skirt's paint, in the
    /// rest pose with the flare at <paramref name="flare"/>.</summary>
    sealed class AvatarRig
    {
        public Skirt K;
        public ClothSkinRegion Region;
        public ClothMesh Mesh;
        public ClothSolver Solver;
        public ClothFrame F;
        public float[] BlendW;
        public Matrix4x4[] SkinM;

        /// <summary>One frame: the pose, its targets, a step (as FrameSkinned and its worker).</summary>
        public void Step(float4x4[] boneT, float dt = 1f / 60f)
        {
            SkinM = SkinOf(K, boneT);
            Region.Targets(SkinM, BlendW, F.Targets);
            F.BoneToWorld = SkinM[Region.Bone];
            F.Dt = dt;
            Solver.Step(F);
        }

        public Vector3 Target(int i) => F.Targets[Mesh.Origin(i)];
    }

    static AvatarRig MakeAvatar(Skirt k, ClothSettings s, float flare = 0f, int spares = 0, float4x4? start = null)
    {
        var region = ClothSkinRegion.Build(k.Pos, k.Tri, k.Bright, k.Skin);
        int nr = region.Count;
        var regionBase = new Vector3[nr];
        for (int r = 0; r < nr; r++) regionBase[r] = k.Pos[region.Vertices[r]];
        var blend = SkirtBlend(k, region.Vertices);
        var blendW = new[] { flare };
        var shaped = region.Shaped(regionBase, blend, blendW);
        var mesh = new ClothMesh(shaped, region.Triangles);
        region.Bind(mesh, regionBase, blend);
        var nrm = region.Vertices.Select(v => k.Nrm[v]).ToArray();
        var solver = new ClothSolver(mesh, s, nrm);
        var rb = new float[nr];
        region.RegionBrightness(k.Bright, rb);
        solver.SetPaint(rb);
        var head = Matrix4x4.Invert(k.BindPoses[region.Bone], out var bi) ? bi.Translation : Vector3.Zero;
        solver.BackstopSign = ClothSkinRegion.OutwardSign(mesh, head);
        region.SpareCount = spares;
        mesh.MaxRenderVertices = nr + spares;
        var rig = new AvatarRig { K = k, Region = region, Mesh = mesh, Solver = solver, BlendW = blendW, F = new ClothFrame { Targets = new Vector3[mesh.OriginalParticleCount] } };
        rig.SkinM = SkinOf(k, SkirtPose(k, start ?? float4x4.Identity));
        region.Targets(rig.SkinM, blendW, rig.F.Targets);
        rig.F.BoneToWorld = rig.SkinM[region.Bone];
        solver.ResetToTargets(rig.F);
        return rig;
    }

    static float4x4 Move(float x, float y = 0f, float z = 0f, float yawDeg = 0f) =>
        float4x4.Transform(new float3(x, y, z), floatQ.Euler(0f, yawDeg, 0f), float3.One);

    // ------------------------------------------------------------------ the region

    static void AvatarRegion()
    {
        Console.WriteLine("-- the region: the free part, the held ring, the bone");
        var k = MakeSkirt();
        var region = ClothSkinRegion.Build(k.Pos, k.Tri, k.Bright, k.Skin);
        int moving = 0, ring = 0;
        bool movesRight = true, ringRight = true;
        for (int r = 0; r < region.Count; r++)
        {
            int v = region.Vertices[r], j = v / Skirt.Stride;
            if (region.Moves[r]) { moving++; movesRight &= j <= 7; }
            else { ring++; ringRight &= j == 8; }
        }
        Check(moving == 8 * Skirt.Stride && movesRight, $"rows 0..7 move (with their seam copies): {moving} vertices");
        Check(ring == Skirt.Stride && ringRight, $"row 8 is the held ring around them: {ring} vertices; row 9 is left out");
        Check(region.Triangles.Length / 3 == 8 * Skirt.Cols * 2, $"the triangles with a free corner: {region.Triangles.Length / 3} (8 strips of 48)");
        Check(region.Bone == 0, $"the bone most of the moving vertices hang from is the hips ({region.Bone})");
        bool meshTri = true;
        for (int t = 0; t < region.MeshTriangle.Length && meshTri; t++)
        {
            int mt = region.MeshTriangle[t];
            for (int c = 0; c < 3; c++) meshTri &= region.Vertices[region.Triangles[3 * t + c]] == k.Tri[3 * mt + c];
        }
        Check(meshTri, "each region triangle names the mesh triangle it is, corner for corner");
        Check(!region.FreesNew(k.Bright), "the paint it was built from frees nothing new");
        var more = (float[])k.Bright.Clone();
        more[Skirt.Id(3, 8)] = 1f;
        Check(region.FreesNew(more), "freeing a ring vertex asks for a rebuild (its copy cannot move it)");
        var less = (float[])k.Bright.Clone();
        less[Skirt.Id(3, 2)] = 0f;
        Check(!region.FreesNew(less), "holding a moving vertex does not (the cloth just holds it)");
        // a UV seam: the seam copy painted dark holds its twin too (one point, held)
        var seam = (float[])k.Bright.Clone();
        seam[Skirt.Id(Skirt.Cols, 3)] = 0f;
        var r2 = ClothSkinRegion.Build(k.Pos, k.Tri, seam, k.Skin);
        int twin = Array.IndexOf(r2.Vertices, Skirt.Id(0, 3)), copy = Array.IndexOf(r2.Vertices, Skirt.Id(Skirt.Cols, 3));
        Check(twin >= 0 && copy >= 0 && !r2.Moves[twin] && !r2.Moves[copy], "a vertex welded to a dark seam copy is held with it");
        Check(r2.MeshVertexOf(r2.Count) == k.VertexCount && r2.MeshVertexOf(r2.Count + 5) == k.VertexCount + 5 && r2.MeshVertexOf(0) == r2.Vertices[0],
              "render vertices past the region are the spare vertices after the mesh's own");
        var none = ClothSkinRegion.Build(k.Pos, k.Tri, new float[k.VertexCount], k.Skin);
        Check(none.Empty && none.MovingCount == 0, "nothing painted free: an empty region (the cloth waits for paint)");
    }

    static void AvatarSpheresAndSide()
    {
        Console.WriteLine("-- which side is out, and which of the mesh's own body spheres were fitted against the cloth");
        var k = MakeSkirt();
        var rig = MakeAvatar(k, new ClothSettings());
        Check(rig.Solver.BackstopSign > 0f, "outward-wound skirt: the normals' side away from the hips is out (+1)");
        var flipped = (int[])k.Tri.Clone();
        for (int t = 0; t < flipped.Length; t += 3) (flipped[t + 1], flipped[t + 2]) = (flipped[t + 2], flipped[t + 1]);
        var rf = ClothSkinRegion.Build(k.Pos, flipped, k.Bright, k.Skin);
        var mf = new ClothMesh(rf.Vertices.Select(v => k.Pos[v]).ToArray(), rf.Triangles);
        var head = Matrix4x4.Invert(k.BindPoses[0], out var bi) ? bi.Translation : Vector3.Zero;
        Check(ClothSkinRegion.OutwardSign(mf, head) < 0f, "inward-wound: -1");
        // a ball just inside the skirt (fitted between it and the legs), one well inside, one beside the held waist
        var centers = new[] { new Vector3(0.14f, 0.1f, 0f), new Vector3(0.03f, 0.1f, 0f), new Vector3(0.11f, 0.33f, 0f) };
        var radii = new[] { 0.04f, 0.04f, 0.02f };
        var skip = rig.Region.SpheresAgainstCloth(k.Pos, centers, radii, 0.01f);
        Check(skip[0] && !skip[1] && !skip[2], $"left out: only the ball against the moving cloth ({string.Join(", ", skip)})");
    }

    // ------------------------------------------------------------------ the renderer's copy, on the engine's own MeshX

    static MeshX SkirtMeshX(Skirt k)
    {
        int n = k.VertexCount;
        var mx = new MeshX();
        mx.HasNormals = true; mx.HasColors = true; mx.HasTangents = true;
        mx.SetVertexCount(n);
        k.Pos.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawPositions.AsSpan(0, n)));
        k.Nrm.CopyTo(MemoryMarshal.Cast<float3, Vector3>(mx.RawNormals.AsSpan(0, n)));
        mx.SetHasUV(0, true); mx.SetHasUV(1, true);
        for (int v = 0; v < n; v++)
        {
            mx.RawUV0s[v] = new float2(k.Uv[v].X, k.Uv[v].Y);
            mx.RawUV1s[v] = new float2(0.5f * k.Uv[v].X, 0.25f + k.Uv[v].Y);
            mx.RawColors[v] = new color(k.Uv[v].X, k.Uv[v].Y, 0.5f, 1f);
            mx.RawTangents[v] = new float4(1f, 0f, 0f, 1f);
        }
        // two submeshes: the top three strips are the waistband's material
        var skirt = mx.AddSubmesh<TriangleSubmesh>();
        var band = mx.AddSubmesh<TriangleSubmesh>();
        int split = 6 * Skirt.Cols * 2 * 3;
        for (int t = 0; t < k.Tri.Length; t += 3) (t < split ? skirt : band).AddTriangle(k.Tri[t], k.Tri[t + 1], k.Tri[t + 2]);
        mx.HasBoneBindings = true;
        for (int b = 0; b < 2; b++) mx.AddBone("bone" + b).BindPose = k.BindPoseX[b];
        var bb = mx.RawBoneBindings;
        for (int v = 0; v < n; v++)
            bb[v] = new BoneBinding { boneIndex0 = k.Bones4[v * 4], weight0 = k.Weights4[v * 4], boneIndex1 = k.Bones4[v * 4 + 1], weight1 = k.Weights4[v * 4 + 1] };
        var bs = mx.AddBlendShape("flare");
        bs.HasNormals = true;
        var fr = bs.AddFrame(1f);
        for (int v = 0; v < n; v++) if (k.Flare[v] != Vector3.Zero) { fr.SetPositionDelta(v, F3(k.Flare[v])); fr.SetNormalDelta(v, new float3(0f, 0.01f, 0f)); }
        return mx;
    }

    /// <summary>The order of the skirt's triangles in the MeshX (submesh by submesh) is the skirt's own.</summary>
    static void AvatarCopyRoundTrip()
    {
        Console.WriteLine("-- the renderer's copy: moving vertices on one bone, written in its space, shown exactly where the cloth is (engine skinning)");
        var k = MakeSkirt();
        var mx = SkirtMeshX(k);
        var rig = MakeAvatar(k, new ClothSettings { MaxDistance = 0.15f }, flare: 0f, spares: 64, start: Move(0f, 1f));
        var region = rig.Region;
        var work = ClothSkinCopy.Make(mx, region, 64);
        int n = k.VertexCount;
        Check(work.VertexCount == n + 64, $"64 spare vertices after the mesh's own ({work.VertexCount - n})");
        bool bound = true, kept = true, deltas = true, heldDeltas = true;
        var shape = work.GetBlendShape(0)[0];
        var shape0 = mx.GetBlendShape(0)[0];
        for (int v = 0; v < work.VertexCount; v++)
        {
            int r = v < n ? Array.IndexOf(region.Vertices, v) : -1;
            bool moves = v >= n || (r >= 0 && region.Moves[r]);
            var b = work.RawBoneBindings[v];
            if (moves)
            {
                bound &= b.boneIndex0 == region.Bone && b.weight0 == 1f && b.weight1 == 0f && b.weight2 == 0f && b.weight3 == 0f;
                deltas &= shape.GetPositionDelta(v).Equals(float3.Zero) && shape.GetNormalDelta(v).Equals(float3.Zero);
            }
            else
            {
                var o = mx.RawBoneBindings[v];
                kept &= b.boneIndex0 == o.boneIndex0 && b.weight0 == o.weight0 && b.boneIndex1 == o.boneIndex1 && b.weight1 == o.weight1;
                heldDeltas &= shape.GetPositionDelta(v).Equals(shape0.GetPositionDelta(v));
            }
        }
        Check(bound, "every moving and spare vertex is bound fully to the cloth's bone");
        Check(deltas, "and has no blendshape deltas (the cloth's targets carry the blendshapes)");
        Check(kept && heldDeltas, "every other vertex keeps its weights and blendshapes");

        // a second of walking with the leg swinging and the flare at 0.6, then the output written into the copy
        rig.BlendW[0] = 0.6f;
        float4x4[] pose = null;
        for (int f = 0; f < 60; f++)
        {
            pose = SkirtPose(k, Move(0.02f * f, 1f, 0f, 2f * f), 25f * MathF.Sin(f * 0.15f));
            rig.Step(pose);
        }
        var s = rig.Solver;
        int nv = rig.Mesh.RenderVertexCount;
        var outPos = new Vector3[nv]; var outNrm = new Vector3[nv]; var outTan = new Vector4[nv];
        var bone = rig.SkinM[region.Bone];
        Check(Matrix4x4.Invert(bone, out var w2r), "the bone's skin matrix inverts");
        s.FillOutput(w2r, 1f, outPos, outNrm, outTan, out _, out _);
        ClothSkinCopy.WriteOutput(work, region, outPos, outNrm, outTan, nv);
        // the engine's own CPU skinning of the copy (what the renderer does on the GPU)
        float worst = 0f, worstHeld = 0f;
        for (int r = 0; r < region.Count; r++)
        {
            int v = region.Vertices[r];
            var p = work.RawPositions[v];
            var shown = V3(work.RawBoneBindings[v].TransformPosition(work, in p, pose));
            int particle = rig.Mesh.RenderParticle[r];
            if (region.Moves[r]) worst = MathF.Max(worst, Vector3.Distance(shown, s.X[particle]));
            else
            {
                // the held ring: the renderer skins it with its own weights; the cloth holds it at its target (blendshapes 0 there)
                var p0 = mx.RawPositions[v];
                var shownHeld = V3(mx.RawBoneBindings[v].TransformPosition(mx, in p0, pose));
                worstHeld = MathF.Max(worstHeld, Vector3.Distance(shownHeld, s.X[particle]));
            }
        }
        Console.WriteLine($"   after 1 s: the shown moving vertices are within {worst * 1e6f:0.0} um of the cloth, the held ring within {worstHeld * 1e6f:0.0} um");
        Check(worst < 2e-5f, "the renderer shows each moving vertex exactly where the cloth put it");
        Check(worstHeld < 2e-5f, "and the held ring exactly where the cloth holds it (the seam does not open)");
        bool normalsUnit = true;
        for (int r = 0; r < region.Count; r++) if (region.Moves[r]) normalsUnit &= MathF.Abs(work.RawNormals[region.Vertices[r]].Magnitude - 1f) < 1e-3f;
        Check(normalsUnit, "their normals are written, unit length");
    }

    static void AvatarTearsInPlace()
    {
        Console.WriteLine("-- tears on an avatar: spare vertices, the triangles and the looks, sent in place (engine encoder)");
        var k = MakeSkirt();
        var mx = SkirtMeshX(k);
        var rig = MakeAvatar(k, new ClothSettings(), spares: 64);
        var region = rig.Region;
        var work = ClothSkinCopy.Make(mx, region, 64);
        ClothSkinCopy.TriangleSubmeshes(work, out var subs, out var start);
        int n = k.VertexCount;
        // what the renderer has: the copy, as uploaded once
        var alloc = new KeepAlloc();
        var bounds = BoundingBox.FromPoints(new float3(-1, -1, -1), new float3(1, 1, 1));
        var load = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref load, work);
        var buf = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(work, buf, alloc, ColorProfile.Linear, ref load, MeshMetadata.GenerateMetadata(work), bounds, null);
        var nonRegion = new List<(int sub, int o, int a, int b, int c)>();
        var inRegion = new HashSet<int>(region.MeshTriangle);
        for (int s = 0; s < subs.Length; s++)
            for (int t = 0; t < subs[s].IndicieCount / 3; t++)
                if (!inRegion.Contains(start[s] + t)) nonRegion.Add((s, 3 * t, subs[s].RawIndicies[3 * t], subs[s].RawIndicies[3 * t + 1], subs[s].RawIndicies[3 * t + 2]));
        // tear a line across the middle (the particles of row 4, from their row-3 neighbours)
        int splits = 0;
        for (int i = 0; i < Skirt.Cols; i += 2)
        {
            int p = rig.Mesh.RenderParticle[Array.IndexOf(region.Vertices, Skirt.Id(i, 4))];
            int o = rig.Mesh.RenderParticle[Array.IndexOf(region.Vertices, Skirt.Id(i, 3))];
            if (rig.Solver.Split(p, o)) splits++;
        }
        int nv = rig.Mesh.RenderVertexCount;
        Check(splits > 0 && nv > region.Count && nv <= region.Count + 64, $"{splits} splits made {nv - region.Count} new render vertices, within the 64 spares");
        int shown = ClothSkinCopy.ApplyTopology(work, region, rig.Mesh, subs, start, region.Count);
        Check(shown == nv, "the copy now shows every render vertex");
        bool spareUsed = false, inRange = true, looks = true;
        foreach (var sub in subs)
            for (int q = 0; q < sub.IndicieCount; q++) { int v = sub.RawIndicies[q]; inRange &= v < n + 64; spareUsed |= v >= n; }
        var src = rig.Mesh.RenderSource;
        for (int r = region.Count; r < nv; r++)
        {
            int to = region.MeshVertexOf(r), from = region.MeshVertexOf(src[r]);
            looks &= work.RawUV0s[to].Equals(work.RawUV0s[from]) && work.RawUV1s[to].Equals(work.RawUV1s[from]) && work.RawColors[to].Equals(work.RawColors[from]);
        }
        bool untouched = nonRegion.All(t => subs[t.sub].RawIndicies[t.o] == t.a && subs[t.sub].RawIndicies[t.o + 1] == t.b && subs[t.sub].RawIndicies[t.o + 2] == t.c);
        Check(spareUsed && inRange, "the torn triangles use spare vertices; no index past them");
        Check(looks, "each spare in use has the UVs (both channels) and colour of the vertex it copies");
        Check(untouched, "the mesh's other triangles are untouched");

        // the in-place upload: streams, triangles, UVs and colours into the buffer the renderer already has
        var outPos = new Vector3[nv]; var outNrm = new Vector3[nv]; var outTan = new Vector4[nv];
        Matrix4x4.Invert(rig.F.BoneToWorld, out var w2r);
        rig.Solver.FillOutput(w2r, 1f, outPos, outNrm, outTan, out _, out _);
        ClothSkinCopy.WriteOutput(work, region, outPos, outNrm, outTan, nv);
        var hint = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Tangents);
        PartialEncode.AddTopology(ref hint, work);
        Check(hint[MeshUploadHint.Flag.Geometry] && hint[MeshUploadHint.Flag.UV0s] && hint[MeshUploadHint.Flag.UV1s] && hint[MeshUploadHint.Flag.Colors] && !hint[MeshUploadHint.Flag.UV2s],
              "the topology hint: Geometry, the UV channels it has, Colors");
        string why = PartialEncode.CheckLayout(work, buf, hint);
        Check(why == null, "the buffer takes it in place: " + (why ?? "ok"));
        var beforeV = buf.GetRawVertexBufferData().ToArray();
        var beforeI = buf.GetRawIndexBufferData().ToArray();
        PartialEncode.Encode(work, buf, hint);
        var refHint = default(MeshUploadHint); MeshUploadHintHelper.SetFromMesh(ref refHint, work);
        var refBuf = new MeshBuffer();
        MeshBufferGenerator.UpdateMeshBufferData(work, refBuf, alloc, ColorProfile.Linear, ref refHint, MeshMetadata.GenerateMetadata(work), bounds, null);
        bool idx = buf.IndexBufferFormat == refBuf.IndexBufferFormat && buf.GetRawIndexBufferData().SequenceEqual(refBuf.GetRawIndexBufferData());
        bool idxChanged = !beforeI.AsSpan().SequenceEqual(buf.GetRawIndexBufferData());
        Check(idx && idxChanged, $"its index buffer was rewritten, byte for byte a full encode's ({buf.IndexCount} indices, {buf.IndexBufferFormat})");
        bool same = buf.GetRawVertexBufferData().SequenceEqual(refBuf.GetRawVertexBufferData());
        var (changed, _) = DiffColumns(buf, beforeV, buf.GetRawVertexBufferData());
        Check(same, $"and so is its whole vertex region (sent in place: {string.Join(", ", changed.OrderBy(a => a))})");
        Check(changed.Contains(VertexAttributeType.UV0) && changed.Contains(VertexAttributeType.Color) &&
              !changed.Contains(VertexAttributeType.BoneWeights) && !changed.Contains(VertexAttributeType.BoneIndicies),
              "the spares' UVs and colours went with it; the bone weights were left alone");
        var bareHint = new MeshUploadHint(MeshUploadHint.Flag.Geometry | MeshUploadHint.Flag.UV0s);
        Check(PartialEncode.CheckLayout(work, buf, bareHint) != null, "triangles alone are refused (the renderer's skinned path needs a vertex stream with them)");
        // the renderer's in-place path reads Float32 UVs only (the engine encodes them so): a buffer saying otherwise is refused
        int uvAt = buf.VertexAttributes.FindIndex(a => a.attribute == VertexAttributeType.UV1);
        var uvWas = buf.VertexAttributes[uvAt];
        buf.VertexAttributes[uvAt] = new VertexAttributeDescriptor(uvWas.attribute, VertexAttributeFormat.Half16, uvWas.dimensions);
        string uvWhy = PartialEncode.CheckLayout(work, buf, hint);
        buf.VertexAttributes[uvAt] = uvWas;
        Check(uvWhy != null && uvWhy.Contains("UV1"), "UVs the renderer cannot read in place are refused: " + uvWhy);
        subs[0].AddTriangle(0, 1, 2);
        Check(PartialEncode.CheckIndices(work, buf) != null, "a submesh with a different index count is refused (a full upload's job)");
    }

    // ------------------------------------------------------------------ the solver on an avatar

    static void AvatarFollowsPinned()
    {
        Console.WriteLine("-- held points follow the animation exactly; free ones hang from it");
        var k = MakeSkirt();
        var rig = MakeAvatar(k, new ClothSettings { MaxDistance = 0.15f }, start: Move(0f, 1f));
        float worst = 0f, far = 0f;
        for (int f = 0; f < 120; f++)
        {
            rig.BlendW[0] = 0.5f + 0.5f * MathF.Sin(f * 0.1f);
            rig.Step(SkirtPose(k, Move(0.01f * f, 1f, 0f, 1.5f * f), 30f * MathF.Sin(f * 0.12f)));
            for (int i = 0; i < rig.Mesh.ParticleCount; i++)
            {
                float d = Vector3.Distance(rig.Solver.X[i], rig.Target(i));
                if (rig.Solver.Kind(i) == 1) worst = MathF.Max(worst, d);
                else far = MathF.Max(far, d);
            }
        }
        Console.WriteLine($"   2 s of walking, turning and a swinging leg: held points within {worst * 1e6f:0.0} um of their targets, free ones within {far * 100f:0.0} cm");
        Check(ClothFinite(rig.Solver), "finite");
        Check(worst < 1e-5f, "held points sit on their targets (blendshapes included)");
        Check(far <= 0.15f + 1e-3f, "free points stay within MaxDistance of theirs");
        Check(rig.Solver.Resets == 1, $"no reset on the way (just the first: {rig.Solver.Resets})");
    }

    static float Lag(AvatarRig rig, Vector3 dir)
    {
        double sum = 0; int c = 0;
        for (int i = 0; i < rig.Mesh.ParticleCount; i++)
            if (rig.Solver.Kind(i) == 0) { sum += Vector3.Dot(rig.Target(i) - rig.Solver.X[i], dir); c++; }
        return (float)(sum / Math.Max(1, c));
    }

    static void AvatarInertia()
    {
        Console.WriteLine("-- Inertia: how much of the body's motion the free cloth feels late");
        var k = MakeSkirt();
        // the most the free cloth falls behind (on average) during a sudden walk: it swings back like a pendulum and
        // forward again, so the peak, not the lag at one moment. No backstop here: it would hold the front panel.
        float Run(float inertia)
        {
            var rig = MakeAvatar(k, new ClothSettings { Inertia = inertia, MaxDistance = 0.3f, Backstop = 0f }, start: Move(0f, 1f));
            for (int f = 0; f < 60; f++) rig.Step(SkirtPose(k, Move(0f, 1f)));
            float rest = Lag(rig, Vector3.UnitX), peak = 0f;
            // a sudden walk at 1.5 m/s along +x for half a second
            for (int f = 1; f <= 30; f++)
            {
                rig.Step(SkirtPose(k, Move(1.5f * f / 60f, 1f)));
                peak = MathF.Max(peak, Lag(rig, Vector3.UnitX) - rest);
            }
            return peak;
        }
        float l0 = Run(0f), l75 = Run(0.75f), l1 = Run(1f);
        Console.WriteLine($"   free cloth behind the body at most, in half a second's walk: {l0 * 100f:0.00} cm at Inertia 0, {l75 * 100f:0.00} cm at 0.75, {l1 * 100f:0.00} cm at 1");
        Check(l0 < 0.005f, "Inertia 0: it moves with the body");
        Check(l0 < l75 && l75 < l1, "more Inertia, more of it is left behind");
        Check(l1 > 0.01f, "Inertia 1: it trails like real cloth");
    }

    static void AvatarSnapTurnAndTeleport()
    {
        Console.WriteLine("-- a snap turn is carried whole; a teleport starts it again where the animation is");
        var k = MakeSkirt();
        var rig = MakeAvatar(k, new ClothSettings { Inertia = 1f, MaxDistance = 0.3f }, start: Move(0f, 1f));
        for (int f = 0; f < 90; f++) rig.Step(SkirtPose(k, Move(0f, 1f)));
        float Off()
        {
            double sum = 0; int c = 0;
            for (int i = 0; i < rig.Mesh.ParticleCount; i++) if (rig.Solver.Kind(i) == 0) { sum += Vector3.Distance(rig.Solver.X[i], rig.Target(i)); c++; }
            return (float)(sum / c);
        }
        float before = Off();
        rig.Step(SkirtPose(k, Move(0f, 1f, 0f, 45f)));
        float afterSnap = Off();
        Check(rig.Solver.SnapTurns == 1, $"45 degrees in a frame counts as a snap turn ({rig.Solver.SnapTurns})");
        Check(afterSnap < before + 0.01f, $"the cloth turned with it, even at Inertia 1 (off its targets by {before * 100f:0.0} cm before, {afterSnap * 100f:0.0} cm after)");
        for (int f = 0; f < 30; f++) rig.Step(SkirtPose(k, Move(0f, 1f, 0f, 45f)));
        int resets = rig.Solver.Resets;
        rig.Step(SkirtPose(k, Move(3f, 1f, 0f, 45f)));
        float afterJump = Off();
        Check(rig.Solver.Resets == resets + 1, "a 3 m jump in a frame resets it");
        Check(afterJump < 0.02f, $"to where the animation has it ({afterJump * 100f:0.00} cm off on average)");
        Check(ClothFinite(rig.Solver) && ClothMaxSpeed(rig.Solver) < 1f, $"and nothing flies ({ClothMaxSpeed(rig.Solver):0.00} m/s)");
    }

    /// <summary>The animated cloth's outward normal at each particle (from the targets, as the solver's backstop).</summary>
    static Vector3[] TargetNormals(AvatarRig rig)
    {
        var m = rig.Mesh; var nn = new Vector3[m.ParticleCount];
        var tri = m.Triangles;
        for (int t = 0; t < m.TriangleCount; t++)
        {
            int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
            var x = Vector3.Cross(rig.Target(b) - rig.Target(a), rig.Target(c) - rig.Target(a));
            nn[a] += x; nn[b] += x; nn[c] += x;
        }
        for (int i = 0; i < nn.Length; i++) nn[i] = nn[i].LengthSquared() > 0f ? Vector3.Normalize(nn[i]) * rig.Solver.BackstopSign : Vector3.Zero;
        return nn;
    }

    static void AvatarBackstop()
    {
        Console.WriteLine("-- the backstop: free cloth pushed into the body goes no further than Backstop behind its animated place");
        var k = MakeSkirt();
        float Run(float backstop)
        {
            var rig = MakeAvatar(k, new ClothSettings { Backstop = backstop, MaxDistance = 0.15f, Collide = false }, start: Move(0f, 1f));
            rig.F.Gravity = new Vector3(-9.81f * 3f, -9.81f, 0f);       // a hard push from +x toward the axis
            for (int f = 0; f < 120; f++) rig.Step(SkirtPose(k, Move(0f, 1f)));
            var nn = TargetNormals(rig);
            float deepest = 0f;
            for (int i = 0; i < rig.Mesh.ParticleCount; i++)
                if (rig.Solver.Kind(i) == 0) deepest = MathF.Min(deepest, Vector3.Dot(rig.Solver.X[i] - rig.Target(i), nn[i]));
            return -deepest;
        }
        float with = Run(0.02f), without = Run(0f);
        Console.WriteLine($"   deepest behind the animated surface: {with * 100f:0.00} cm with a 2 cm backstop, {without * 100f:0.00} cm without");
        Check(with <= 0.02f + 2e-3f, "a 2 cm backstop holds it there");
        Check(without > with + 0.01f, "without it the cloth goes into the body");
    }

    static void AvatarOwnBody()
    {
        Console.WriteLine("-- the wearer's own body: their spheres push the cloth no further out than the animation puts it");
        var k = MakeSkirt();
        // thick thighs under a tight skirt: a body 1 cm wider than the skirt's middle, standing where the skirt does
        var (bp, bn, bt) = CapsuleMesh(0.0f, 0.3f, 0.175f, seg: 48, capRows: 8, bodyRows: 16);
        var shape = BodyShape.Fit(new BodyMesh { Positions = bp, Normals = bn, Triangles = bt, Skin = TwoBones(bp, 0f, oneBone: true) });
        (float mean, float spheres) Run(bool own)
        {
            var rig = MakeAvatar(k, new ClothSettings { MaxDistance = 0.15f, Backstop = 0f, Thickness = 0.005f }, start: Move(0f, 1f));
            var body = new ClothBody();
            rig.F.Bodies.Add(body); rig.F.BodyCount = 1;
            var at = new[] { Matrix4x4.CreateTranslation(0f, 1f, 0f), Matrix4x4.CreateTranslation(0f, 1f, 0f), Matrix4x4.CreateTranslation(0f, 1f, 0f) };
            for (int f = 0; f < 120; f++)
            {
                body.Set(shape, at, at, 1f / 60f, (byte)Core.Fluid.FluidSim.CategoryBody);
                body.Own = own;
                rig.Step(SkirtPose(k, Move(0f, 1f)));
            }
            // the free points of the middle rows: how far out they were pushed past their targets
            double sum = 0; int c = 0;
            for (int i = 0; i < rig.Mesh.ParticleCount; i++)
            {
                if (rig.Solver.Kind(i) != 0) continue;
                var t = rig.Target(i);
                if (t.Y < 1.1f || t.Y > 1.2f) continue;
                var radialT = new Vector2(t.X, t.Z); var radialX = new Vector2(rig.Solver.X[i].X, rig.Solver.X[i].Z);
                sum += radialX.Length() - radialT.Length(); c++;
            }
            return ((float)(sum / Math.Max(1, c)), rig.Solver.BodySpheres);
        }
        var (other, sp) = Run(false);
        var (own, _) = Run(true);
        Console.WriteLine($"   the middle of the skirt pushed out by {other * 100f:0.00} cm by someone else's body ({sp} spheres near), {own * 100f:0.00} cm by the wearer's own");
        Check(sp > 0, "the body's spheres are near the cloth");
        Check(other > 0.005f, "someone else's thighs push the skirt out");
        Check(own < 0.003f, "the wearer's own do not puff it out past where the avatar has it");
    }

    // ------------------------------------------------------------------ world cloth (0.30.0 fixes)

    static void ClothScaledRenderer()
    {
        Console.WriteLine("-- world cloth on a scaled slot hangs at its scaled size (0.29.x pulled it to its unscaled size)");
        var (pos, tris, _) = ClothGrid(21, 21, 1f, 1f);
        var mesh = new ClothMesh(pos, tris);
        var s = new ClothSolver(mesh, new ClothSettings());
        s.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
        var f = ClothFrameAt(1f / 60f, Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0f, 3f, 0f));
        for (int k = 0; k < 240; k++) s.Step(f);
        float len = 0f;
        for (int j = 0; j + 1 < 21; j++) len += Vector3.Distance(s.X[j * 21 + 10], s.X[(j + 1) * 21 + 10]);
        float width = Vector3.Distance(s.X[20 * 21], s.X[20 * 21 + 20]);
        Console.WriteLine($"   a 1 m sheet at scale 2: middle column {len:0.000} m, top edge {width:0.000} m");
        Check(len > 1.95f && len < 2.08f, "it hangs 2 m long");
        Check(MathF.Abs(width - 2f) < 1e-3f, "its pinned top edge is 2 m wide");
        // and when the slot is scaled while it runs, it is measured again
        f.RendererToWorld = Matrix4x4.CreateScale(1f) * Matrix4x4.CreateTranslation(0f, 3f, 0f);
        int before = s.Remeasures;
        for (int k = 0; k < 240; k++) s.Step(f);
        len = 0f;
        for (int j = 0; j + 1 < 21; j++) len += Vector3.Distance(s.X[j * 21 + 10], s.X[(j + 1) * 21 + 10]);
        Check(s.Remeasures == before + 1 && len > 0.97f && len < 1.04f, $"scaled back to 1, it is measured once more and hangs 1 m ({len:0.000} m)");
    }

    static void ClothRenderRoom()
    {
        Console.WriteLine("-- a cap on render vertices (an avatar's spares): tears stop at it, the same way every time");
        var (pos, tris, _) = ClothGrid(15, 15, 1f, 1f, seam: true);
        (int splits, int nv, int[] tri) Run()
        {
            var mesh = new ClothMesh(pos, tris);
            mesh.MaxRenderVertices = pos.Length + 40;
            var rnd = new Random(1234);
            int ok = 0;
            for (int k = 0; k < 400; k++)
            {
                int p = rnd.Next(mesh.ParticleCount);
                if (mesh.FanSize(p) < 2) continue;
                var tri = mesh.Triangles;
                int t = rnd.Next(mesh.TriangleCount);
                int other = tri[3 * t] == p ? tri[3 * t + 1] : tri[3 * t];
                if (mesh.Split(p, other)) ok++;
                if (mesh.RenderVertexCount > mesh.MaxRenderVertices) return (-1, mesh.RenderVertexCount, null);
            }
            return (ok, mesh.RenderVertexCount, mesh.RenderTriangles.ToArray());
        }
        var a = Run(); var b = Run();
        Console.WriteLine($"   {a.splits} splits fit in 40 spare vertices ({a.nv - pos.Length} used)");
        Check(a.splits > 0 && a.nv <= pos.Length + 40, "never past the cap");
        Check(a.splits == b.splits && a.tri.AsSpan().SequenceEqual(b.tri), "and the same tears every time (every client agrees)");
    }
}
