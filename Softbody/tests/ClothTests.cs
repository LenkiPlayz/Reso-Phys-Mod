using System.Diagnostics;
using System.Numerics;
using Softbody.Core;
using Softbody.Core.Cloth;
using Softbody.Core.Flesh;

// 0.29.0: world cloth (DESIGN-cloth.md, "Offline tests"). Run with:  dotnet run -c Release --project tests -- cloth
// ("cloth-cost" also times the solver: a benchmark, so only with no game open).
namespace Softbody.Tests;

static partial class Program
{
    /// <param name="cost">Also time the solver (a benchmark: only with "cloth-cost", never while a game is open).</param>
    static void ClothTests(bool cost = false)
    {
        Console.WriteLine("== Cloth (0.29.0)");
        ClothTopology();
        ClothHangingSheet();
        ClothDrape();
        ClothFlagFrameRates();
        ClothBending();
        ClothMaxDistance();
        ClothTearing();
        ClothRandomSplits();
        ClothOutput();
        if (cost) ClothCost();
    }

    /// <summary>A grid sheet: nx by ny vertices, size metres, in the XY plane (hanging, y up) or XZ (lying, normal +y).
    /// seam: the middle column's vertices are doubled (a UV seam). doubleSided: a back copy with its own vertices.</summary>
    static (Vector3[] pos, int[] tris, Vector2[] uv) ClothGrid(int nx, int ny, float sizeX, float sizeY, bool lying = false, bool seam = false, bool doubleSided = false)
    {
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
        int[,] id = new int[nx, ny], id2 = new int[nx, ny];
        int mid = nx / 2;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float x = sizeX * i / (nx - 1) - 0.5f * sizeX, y = sizeY * j / (ny - 1);
                var p = lying ? new Vector3(x, 0f, y - 0.5f * sizeY) : new Vector3(x, y - sizeY, 0f);
                id[i, j] = pos.Count; pos.Add(p); uv.Add(new Vector2(i / (float)(nx - 1), j / (float)(ny - 1)));
                if (seam && i == mid) { id2[i, j] = pos.Count; pos.Add(p); uv.Add(new Vector2(i / (float)(nx - 1) + 0.5f, j / (float)(ny - 1))); }
                else id2[i, j] = id[i, j];
            }
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                // the right of the seam uses the seam's second copy
                int a = i == mid && seam ? id2[i, j] : id[i, j], d = i == mid && seam ? id2[i, j + 1] : id[i, j + 1];
                int b = id[i + 1, j], c = id[i + 1, j + 1];
                if ((i + j) % 2 == 0) { tris.AddRange(new[] { a, b, c }); tris.AddRange(new[] { a, c, d }); }
                else { tris.AddRange(new[] { a, b, d }); tris.AddRange(new[] { b, c, d }); }
            }
        if (doubleSided)
        {
            int off = pos.Count, nt = tris.Count;
            for (int k = 0; k < off; k++) { pos.Add(pos[k]); uv.Add(uv[k]); }
            for (int k = 0; k < nt; k += 3) { tris.Add(tris[k] + off); tris.Add(tris[k + 2] + off); tris.Add(tris[k + 1] + off); }
        }
        return (pos.ToArray(), tris.ToArray(), uv.ToArray());
    }

    /// <summary>Paint per render vertex: pinned (0) where pin(p) says so, else free (1).</summary>
    static float[] ClothPaint(Vector3[] pos, Func<Vector3, bool> pin)
    {
        var b = new float[pos.Length];
        for (int i = 0; i < pos.Length; i++) b[i] = pin(pos[i]) ? 0f : 1f;
        return b;
    }

    static ClothFrame ClothFrameAt(float dt, Matrix4x4? m = null)
    {
        var f = new ClothFrame { Dt = dt, RendererToWorld = m ?? Matrix4x4.Identity };
        return f;
    }

    static bool ClothFinite(ClothSolver s)
    {
        for (int i = 0; i < s.Mesh.ParticleCount; i++) if (!float.IsFinite(s.X[i].X + s.X[i].Y + s.X[i].Z)) return false;
        return true;
    }

    static float ClothMaxSpeed(ClothSolver s)
    {
        float m = 0f;
        for (int i = 0; i < s.Mesh.ParticleCount; i++) if (s.Kind(i) == 0) m = MathF.Max(m, s.V[i].Length());
        return m;
    }

    static void ClothTopology()
    {
        Console.WriteLine("-- topology: welding, double-sided sheets, the render mapping");
        var (pos, tris, _) = ClothGrid(9, 7, 1f, 1f, seam: true, doubleSided: true);
        var mesh = new ClothMesh(pos, tris);
        Check(mesh.ParticleCount == 9 * 7, $"a seamed, double-sided 9x7 grid welds to 63 particles ({mesh.ParticleCount}; {pos.Length} render vertices)");
        Check(mesh.TriangleCount == 2 * 8 * 6, $"its front and back are simulated once: 96 triangles ({mesh.TriangleCount} of {tris.Length / 3} drawn)");
        Check(mesh.Validate() == null, "the topology is consistent: " + (mesh.Validate() ?? "ok"));
        int back = 0;
        foreach (var sg in mesh.RenderSign) if (sg < 0) back++;
        Check(back == pos.Length / 2, $"the back's vertices are marked turned around ({back} of {pos.Length})");
        var single = new ClothMesh(ClothGrid(5, 5, 1f, 1f).pos, ClothGrid(5, 5, 1f, 1f).tris);
        Check(single.MaxParticles == 25 + 16, $"room for tears: at least 16 more particles ({single.MaxParticles})");
    }

    static void ClothHangingSheet()
    {
        Console.WriteLine("-- a hanging sheet: how much it stretches, and that it comes to rest");
        var (pos, tris, _) = ClothGrid(41, 41, 1f, 1f);
        var mesh = new ClothMesh(pos, tris);
        var s = new ClothSolver(mesh, new ClothSettings());
        s.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
        var f = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 240; k++) s.Step(f);
        float maxStrain = s.MaxStrain;
        // the sheet's length along its middle column
        float len = 0f;
        int col = 20;
        for (int j = 0; j + 1 < 41; j++) len += Vector3.Distance(s.X[j * 41 + col], s.X[(j + 1) * 41 + col]);
        float speed = ClothMaxSpeed(s);
        Console.WriteLine($"   after 4 s: middle column {len:0.0000} m (rest 1 m), most stretched edge {maxStrain:0.0000}x, fastest point {speed:0.0000} m/s, {s.LastStepMs:0.00} ms/frame ({s.StepsLastFrame} steps)");
        Check(ClothFinite(s), "finite");
        Check(len < 1.04f, $"stretches under 4% under its own weight ({(len - 1f) * 100f:0.0}%)");
        Check(maxStrain < 1.08f, $"no edge over 8% longer than at rest ({maxStrain:0.000}x)");
        Check(speed < 0.05f, $"at rest after 4 s (fastest point {speed:0.0000} m/s)");
        Check(s.Pinned == 41 && s.Free == 41 * 40, $"the top row is pinned, the rest free ({s.Pinned} pinned, {s.Free} free)");
    }

    static void ClothDrape()
    {
        Console.WriteLine("-- draping over a ball and onto the floor: never deeper than Thickness, friction holds it");
        var (pos, tris, _) = ClothGrid(31, 31, 1.2f, 1.2f, lying: true);
        var mesh = new ClothMesh(pos, tris);
        var settings = new ClothSettings { Thickness = 0.01f, Friction = 0.8f };
        var s = new ClothSolver(mesh, settings);
        var m = Matrix4x4.CreateTranslation(0f, 1.0f, 0f);
        var ball = new Vector3(0f, 0.5f, 0f);
        const float R = 0.3f;
        var f = ClothFrameAt(1f / 60f, m);
        f.Colliders.Add(FleshCollider.Sphere(ball, R));
        f.Colliders.Add(FleshCollider.Plane(Vector3.Zero, Vector3.UnitY));
        float worst = 0f;
        for (int k = 0; k < 300; k++)
        {
            s.Step(f);
            for (int i = 0; i < mesh.ParticleCount; i++)
            {
                worst = MathF.Max(worst, R - Vector3.Distance(s.X[i], ball));
                worst = MathF.Max(worst, -s.X[i].Y);
            }
        }
        // the middle of the sheet sits on top of the ball
        var top = s.X[15 * 31 + 15];
        Console.WriteLine($"   after 5 s: middle at ({top.X:0.000}, {top.Y:0.000}, {top.Z:0.000}), deepest into the ball or floor {worst * 1000f:0.0} mm, {s.Contacts} points touching, {s.PairCount} pairs");
        Check(ClothFinite(s), "finite");
        Check(worst < 0.5f * settings.Thickness, $"never inside the ball or the floor ({worst * 1000f:0.00} mm)");
        Check(MathF.Abs(top.X) < 0.05f && MathF.Abs(top.Z) < 0.05f && top.Y > ball.Y + R, "the middle stays on top of the ball (friction)");
        Check(ClothMaxSpeed(s) < 0.1f, $"at rest (fastest point {ClothMaxSpeed(s):0.000} m/s)");
    }

    static (float extent, float maxStrain, bool finite) ClothFlagRun(Func<int, float> dtOf, int frames)
    {
        var (pos, tris, _) = ClothGrid(25, 17, 1.2f, 0.8f);
        var mesh = new ClothMesh(pos, tris);
        var s = new ClothSolver(mesh, new ClothSettings { Weight = 150f });
        s.SetPaint(ClothPaint(pos, p => p.X < -0.6f + 1e-4f));
        var f = ClothFrameAt(1f / 60f);
        f.Winds = new[] { new ClothWindSource { Direction = Vector3.UnitX, Strength = 8f, Gusts = 0.3f, GustRate = 0.4f, Turbulence = 0.3f, SwirlSize = 1.5f, Seed = 3 } };
        f.WindCount = 1;
        float extent = 0f, maxStrain = 0f, time = 0f;
        int counted = 0;
        for (int k = 0; k < frames; k++)
        {
            f.Dt = dtOf(k);
            time += f.Dt;
            f.Time = time;
            s.Step(f);
            maxStrain = MathF.Max(maxStrain, s.MaxStrain);
            if (time > 2f)
            {
                float far = float.MinValue;
                for (int i = 0; i < mesh.ParticleCount; i++) far = MathF.Max(far, s.X[i].X);
                extent += far; counted++;
            }
            if (time >= 6f) break;
        }
        return (counted > 0 ? extent / counted : 0f, maxStrain, ClothFinite(s));
    }

    static void ClothFlagFrameRates()
    {
        Console.WriteLine("-- a flag in the wind at 60 fps, and at a frame rate jumping between 30 and 144");
        var steady = ClothFlagRun(_ => 1f / 60f, 100000);
        var rng = new Random(7);
        var jumpy = ClothFlagRun(_ => rng.Next(3) switch { 0 => 1f / 30f, 1 => 1f / 144f, _ => 1f / (30f + 114f * (float)rng.NextDouble()) }, 100000);
        Console.WriteLine($"   60 fps: reaches x = {steady.extent:0.000} m on average, most stretched edge {steady.maxStrain:0.000}x");
        Console.WriteLine($"   30..144 fps: reaches x = {jumpy.extent:0.000} m on average, most stretched edge {jumpy.maxStrain:0.000}x");
        Check(steady.finite && jumpy.finite, "finite at both");
        Check(steady.extent > 0.3f, $"the wind blows it out ({steady.extent:0.000} m from the pole at x = -0.6)");
        Check(MathF.Abs(jumpy.extent - steady.extent) < 0.1f, $"the same look at a jumping frame rate ({jumpy.extent - steady.extent:+0.000;-0.000} m)");
        Check(jumpy.maxStrain < 1.3f && steady.maxStrain < 1.3f, "no blow-ups (no edge past 1.3x)");
    }

    static float ClothCantilever(float bend)
    {
        var (pos, tris, _) = ClothGrid(21, 9, 0.4f, 0.16f, lying: true);
        var mesh = new ClothMesh(pos, tris);
        var s = new ClothSolver(mesh, new ClothSettings { Bend = bend, Weight = 300f });
        s.SetPaint(ClothPaint(pos, p => p.X < -0.2f + 0.021f));
        var f = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 240; k++) s.Step(f);
        float low = 0f;
        for (int i = 0; i < mesh.ParticleCount; i++) low = MathF.Min(low, s.X[i].Y);
        return -low;
    }

    static void ClothBending()
    {
        Console.WriteLine("-- bending: a strip held at one end, lying flat");
        float silk = ClothCantilever(0f), mid = ClothCantilever(0.5f), stiff = ClothCantilever(1f);
        Console.WriteLine($"   the free end droops {silk * 100f:0.0} cm at Bend 0, {mid * 100f:0.0} cm at 0.5, {stiff * 100f:0.0} cm at 1 (0.4 m strip)");
        Check(silk > mid && mid > stiff, "stiffer bends less");
        Check(silk > 0.3f, "Bend 0 hangs almost straight down");
    }

    static void ClothMaxDistance()
    {
        Console.WriteLine("-- MaxDistance: free points stay within brightness x MaxDistance of their rest place");
        var (pos, tris, _) = ClothGrid(11, 11, 0.5f, 0.5f, lying: true);
        var mesh = new ClothMesh(pos, tris);
        var s = new ClothSolver(mesh, new ClothSettings { MaxDistance = 0.1f });
        var paint = new float[pos.Length];
        Array.Fill(paint, 1f);
        s.SetPaint(paint);
        var f = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 120; k++) s.Step(f);
        float far = 0f;
        for (int i = 0; i < mesh.ParticleCount; i++) far = MathF.Max(far, Vector3.Distance(s.X[i], mesh.RestPositions[i]));
        Check(far <= 0.1f + 1e-4f, $"no point further than 10 cm from its rest place ({far * 100f:0.00} cm)");
        Check(far > 0.09f, $"and they do fall that far ({far * 100f:0.00} cm)");
    }

    static void ClothTearing()
    {
        Console.WriteLine("-- tearing: pulled apart by two anchors");
        var (pos, tris, uv) = ClothGrid(21, 13, 1f, 0.6f, seam: true);
        var mesh = new ClothMesh(pos, tris);
        var settings = new ClothSettings { Tearable = true, TearAt = 1.4f, Gravity = 0f };
        var s = new ClothSolver(mesh, settings);
        // the left column holds on anchor 0, the right on anchor 1, which moves away along +x
        int n = mesh.ParticleCount;
        var anchorOf = new int[n]; var local = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.RestPositions[i];
            anchorOf[i] = p.X < -0.5f + 1e-4f ? 0 : p.X > 0.5f - 1e-4f ? 1 : -1;
            local[i] = p;
        }
        s.SetAnchors(anchorOf, local);
        var f = ClothFrameAt(1f / 60f);
        f.AnchorToWorld = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        f.AnchorValid = new[] { true, true };
        f.FindTears = true;
        var list = new List<(int p, int o)>();
        int maxPerFrame = 0, framesWithTears = 0;
        string bad = null;
        for (int k = 0; k < 240; k++)
        {
            f.AnchorToWorld[1] = Matrix4x4.CreateTranslation(0.006f * k, 0f, 0f);
            s.Step(f);
            int done = 0;
            foreach (var (a, b, _) in s.TearCandidates.ToArray())
            {
                // the end with more triangles first, then the lower index; the other end if that cannot split
                int first = mesh.FanSize(a) > mesh.FanSize(b) || (mesh.FanSize(a) == mesh.FanSize(b) && a < b) ? a : b, second = first == a ? b : a;
                if (s.Split(first, second)) { list.Add((first, second)); done++; }
                else if (s.Split(second, first)) { list.Add((second, first)); done++; }
            }
            maxPerFrame = Math.Max(maxPerFrame, done);
            if (done > 0) framesWithTears++;
            bad ??= mesh.Validate();
        }
        Console.WriteLine($"   {list.Count} splits in {framesWithTears} frames (at most {maxPerFrame} in one), {mesh.ParticleCount} particles of {mesh.MaxParticles}, {mesh.RenderVertexCount} render vertices");
        Check(list.Count > 0, "it tears");
        Check(maxPerFrame <= ClothSettings.MaxTearsPerFrame, $"at most {ClothSettings.MaxTearsPerFrame} splits a frame ({maxPerFrame})");
        Check(bad == null, "the mesh stays valid after every frame: " + (bad ?? "ok"));
        Check(ClothFinite(s), "finite");
        // the two halves come apart: some particle near the right anchor is far from every particle near the left one
        bool apart = ClothApart(s);
        Check(apart, "it tore through: the halves are apart");

        // a client that joins later replays the list on a fresh build
        var replay = new ClothMesh(pos, tris);
        int skipped = 0;
        foreach (var (p, o) in list) if (!replay.Split(p, o)) skipped++;
        Check(skipped == 0, $"every recorded split replays ({skipped} skipped)");
        Check(ClothSameTopology(mesh, replay), "replaying the list gives the same mesh, index for index");
        // Mend: a fresh build is the original
        var mended = new ClothMesh(pos, tris);
        Check(mended.ParticleCount == mesh.OriginalParticleCount && mended.RenderVertexCount == pos.Length && mended.Validate() == null, "Mend gives back the original");
        _ = uv;
    }

    static bool ClothApart(ClothSolver s)
    {
        // breadth-first over the stretch edges from a particle at the left anchor: the right anchor's particles unreachable
        int n = s.Mesh.ParticleCount;
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        var a = s.StretchA; var b = s.StretchB;
        for (int k = 0; k < a.Length; k++) { adj[a[k]].Add(b[k]); adj[b[k]].Add(a[k]); }
        int start = -1;
        for (int i = 0; i < n; i++) if (s.AnchorOf(i) == 0) { start = i; break; }
        if (start < 0) return false;
        var seen = new bool[n];
        var q = new Queue<int>();
        q.Enqueue(start); seen[start] = true;
        while (q.Count > 0) { int x = q.Dequeue(); foreach (int y in adj[x]) if (!seen[y]) { seen[y] = true; q.Enqueue(y); } }
        for (int i = 0; i < n; i++) if (s.AnchorOf(i) == 1 && seen[i]) return false;
        return true;
    }

    static bool ClothSameTopology(ClothMesh x, ClothMesh y)
    {
        if (x.ParticleCount != y.ParticleCount || x.RenderVertexCount != y.RenderVertexCount || x.TriangleCount != y.TriangleCount) return false;
        return x.Triangles.SequenceEqual(y.Triangles) && x.RenderTriangles.SequenceEqual(y.RenderTriangles) &&
               x.RenderParticle.SequenceEqual(y.RenderParticle) && x.RenderSource.SequenceEqual(y.RenderSource) && x.RestPositions.SequenceEqual(y.RestPositions);
    }

    static void ClothRandomSplits()
    {
        Console.WriteLine("-- random tears on a seamed, double-sided sheet: always valid, always the same on replay");
        var (pos, tris, _) = ClothGrid(15, 15, 1f, 1f, seam: true, doubleSided: true);
        var mesh = new ClothMesh(pos, tris, 1f);
        var rng = new Random(11);
        var list = new List<(int, int)>();
        string bad = null;
        int tries = 0;
        while (list.Count < 120 && tries++ < 5000)
        {
            int p = rng.Next(mesh.ParticleCount);
            var fan = mesh.Fan(p);
            if (fan.Count == 0) continue;
            int t = fan[rng.Next(fan.Count)];
            int o = mesh.Triangles[3 * t + rng.Next(3)];
            if (o == p) continue;
            if (mesh.Split(p, o)) list.Add((p, o));
            bad ??= mesh.Validate();
        }
        var replay = new ClothMesh(pos, tris, 1f);
        int skipped = 0;
        foreach (var (p, o) in list) if (!replay.Split(p, o)) skipped++;
        Console.WriteLine($"   {list.Count} splits: {mesh.ParticleCount} particles, {mesh.RenderVertexCount} render vertices (from {pos.Length})");
        Check(bad == null, "valid after every split: " + (bad ?? "ok"));
        Check(skipped == 0 && ClothSameTopology(mesh, replay), "the replay is the same mesh");
    }

    static void ClothOutput()
    {
        Console.WriteLine("-- what is drawn: positions at the particles, normals, tangents, the back of a sheet");
        var (pos, tris, uv) = ClothGrid(9, 9, 1f, 1f, lying: true, doubleSided: true);
        var mesh = new ClothMesh(pos, tris);
        var tan = new Vector4[pos.Length];
        Array.Fill(tan, new Vector4(1f, 0f, 0f, 1f));
        var s = new ClothSolver(mesh, new ClothSettings(), default, uv, tan);
        var paint = new float[pos.Length];   // all pinned: the mesh as modelled
        s.SetPaint(paint);
        var m = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(1f, 2f, 3f);
        Matrix4x4.Invert(m, out var inv);
        var f = ClothFrameAt(1f / 60f, m);
        s.Step(f); s.Step(f);
        int nv = mesh.RenderVertexCount;
        var op = new Vector3[nv]; var on = new Vector3[nv]; var ot = new Vector4[nv];
        s.FillOutput(inv, 1f, op, on, ot, out var lo, out var hi);
        float err = 0f;
        for (int v = 0; v < nv; v++) err = MathF.Max(err, Vector3.Distance(op[v], pos[v]));
        Check(err < 1e-4f, $"a pinned sheet is drawn where it was modelled ({err * 1000f:0.000} mm off)");
        // the lying grid's triangles wind so that their normal is -y or +y; the back's normals are the other way
        int half = nv / 2, wrong = 0;
        for (int v = 0; v < half; v++) if (Vector3.Dot(on[v], on[v + half]) > -0.99f) wrong++;
        Check(wrong == 0, $"the back of the sheet faces the other way ({wrong} vertices wrong)");
        int badTan = 0;
        for (int v = 0; v < nv; v++)
        {
            var t3 = new Vector3(ot[v].X, ot[v].Y, ot[v].Z);
            if (MathF.Abs(t3.Length() - 1f) > 1e-3f || MathF.Abs(Vector3.Dot(t3, on[v])) > 1e-3f) badTan++;
        }
        Check(badTan == 0, $"tangents are unit and across the normal ({badTan} not)");
        // the UV's u runs along +x: so do the front's tangents
        float along = 0f;
        for (int v = 0; v < half; v++) along += ot[v].X;
        Check(along / half > 0.99f, $"tangents follow the UVs ({along / half:0.000})");
        Check(lo.X <= -0.5f + 1e-4f && hi.X >= 0.5f - 1e-4f, "the bounds hold the sheet");
    }

    static void ClothCost()
    {
        Console.WriteLine("-- cost: one frame at 60 fps (720 steps/s, 12 steps), one core");
        foreach (int side in new[] { 32, 64, 90 })
        {
            var (pos, tris, _) = ClothGrid(side, side, 2f, 2f);
            var mesh = new ClothMesh(pos, tris);
            var s = new ClothSolver(mesh, new ClothSettings());
            s.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
            var f = ClothFrameAt(1f / 60f);
            f.Winds = new[] { new ClothWindSource { Direction = Vector3.UnitZ, Strength = 5f, Gusts = 0.3f, GustRate = 0.3f, Turbulence = 0.3f, SwirlSize = 1.5f } };
            f.WindCount = 1;
            f.Colliders.Add(FleshCollider.Sphere(new Vector3(0f, -1f, 0.1f), 0.3f));
            for (int k = 0; k < 20; k++) s.Step(f);
            var sw = Stopwatch.StartNew();
            const int frames = 60;
            for (int k = 0; k < frames; k++) s.Step(f);
            double ms = sw.Elapsed.TotalMilliseconds / frames;
            var op = new Vector3[mesh.RenderVertexCount]; var on = new Vector3[mesh.RenderVertexCount];
            sw.Restart();
            for (int k = 0; k < frames; k++) s.FillOutput(Matrix4x4.Identity, 1f, op, on, Span<Vector4>.Empty, out _, out _);
            double outMs = sw.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"   {mesh.ParticleCount,5} points, {mesh.TriangleCount,5} triangles: {ms:0.00} ms per frame, output {outMs:0.00} ms");
            Check(ClothFinite(s), $"{mesh.ParticleCount} points: finite");
        }
    }
}
