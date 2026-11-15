using System.Numerics;
using Softbody.Core.Cloth;
using Softbody.Core.Flesh;

// 0.33.1: pieces that fly fast (a strong blast at low Drag) near players, and over a mesh floor (the death effect: no
// players). In game (2026-11-14) a blast at Drag 0.02 next to a player dropped the game to about 10 fps: with one reach
// for all the points, set by the fastest one, every point paired with every body sphere near the cloth, in every step.
// Run with:  dotnet run -c Release --project tests -- cloth-fast   (or "cloth").
namespace Softbody.Tests;

static partial class Program
{
    static void ClothFastTests()
    {
        Console.WriteLine("== Fast pieces (0.33.1)");
        var (bp, bn, bt) = CapsuleMesh(0f, 1.2f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var player = BodyShape.Fit(new BodyMesh { Positions = bp, Normals = bn, Triangles = bt, Skin = TwoBones(bp, 0f, oneBone: true) });
        // before 0.33.1: 3,935,227 pairs at most in a frame at 144 fps and 5,073,678 at 10 fps (230 ms a frame) next to the
        // players; 77,036 and 838,866 (68 ms a frame) over the floor
        FastPieces("next to six players (CollidePlayers)", f => FastPlayers(f, player), new Vector3(0f, -0.1f, 0f), floorY: -1.65f, perPoint144: 50, perPoint10: 10);
        FastPieces("over a mesh floor, no players (the death effect)", FastMeshFloor, new Vector3(0f, -0.4f, 0f), floorY: -0.3f, perPoint144: 10, perPoint10: 10);
        FastThinFloor();
    }

    /// <summary>A small sheet thrown straight down at 100 m/s (14 cm a step) at a thin floor of triangles 3 m below,
    /// two-sided and front only, at 30 and at 144 frames a second. It stays above the floor (before 0.33.1 a triangle was
    /// tested only where a step ended, within the sheet's 1 cm, so a point faster than about 14 m/s stepped over it); and
    /// at 30 fps, where 4 runs of steps a frame do not look as far as it gets, it slows to what they reach (80 m/s).</summary>
    static void FastThinFloor()
    {
        Console.WriteLine("-- A small sheet thrown down at 100 m/s at a thin floor of triangles 3 m below");
        var (fp, ft, _) = ClothGrid(9, 9, 8f, 8f, lying: true);
        bool up = Vector3.Cross(fp[ft[1]] - fp[ft[0]], fp[ft[2]] - fp[ft[0]]).Y > 0f;
        var place = (up ? Matrix4x4.Identity : Matrix4x4.CreateRotationX(MathF.PI)) * Matrix4x4.CreateTranslation(0f, -3f, 0f);
        var floor = TriangleBvh.Build(fp, ft);
        float cap = ClothSolver.MaxReach / (1.5f * 6f / 720f);     // 4 runs of 6 steps (30 fps: 24 steps a frame)
        foreach (bool twoSided in new[] { true, false })
            foreach (int fps in new[] { 30, 144 })
            {
                var (pos, tris, _) = ClothGrid(5, 5, 0.2f, 0.2f, lying: true);
                var mesh = new ClothMesh(pos, tris);
                var s = new ClothSolver(mesh, new ClothSettings { Drag = 0f, Friction = 0.1f });
                var f = ClothFrameAt(1f / fps);
                f.Colliders.Add(FleshCollider.MeshShape(floor, place, twoSided));
                s.Step(f);
                for (int i = 0; i < mesh.ParticleCount; i++) s.V[i] = new Vector3(0f, -100f, 0f);
                s.Step(f);
                float first = ClothMaxSpeed(s);
                int runs = s.PairRuns;
                for (int k = 0; k < fps; k++) s.Step(f);
                float lowest = float.MaxValue;
                for (int i = 0; i < mesh.ParticleCount; i++) lowest = MathF.Min(lowest, s.X[i].Y);
                string what = $"{(twoSided ? "two-sided" : "front only")}, {fps} fps";
                Console.WriteLine($"   {what}: {runs} runs in the first frame, then {first:0.0} m/s; a second later the lowest point is {lowest + 3f:0.000} m above the floor");
                Check(ClothFinite(s) && lowest > -3.02f, $"{what}: it stays above the floor ({lowest + 3f:0.000} m)");
                if (fps == 30) Check(runs == ClothSolver.MaxPairRuns && first < cap + 1f && first > cap - 5f, $"{what}: {ClothSolver.MaxPairRuns} runs, and it slows to about {cap:0} m/s ({first:0.0})");
                else Check(runs == 2 && first > 99f, $"{what}: 2 runs, and at 100 m/s it flies on ({first:0.0} m/s)");
            }
    }

    /// <summary>Six still players: one under the sheet (its head 5 cm below it), five in a ring 2.5 m away; a floor plane at
    /// their feet.</summary>
    static void FastPlayers(ClothFrame f, BodyShape shape)
    {
        var spots = new List<Vector3> { new(0f, -1.45f, 0f) };
        for (int k = 0; k < 5; k++) { float a = k * MathF.Tau / 5f; spots.Add(new Vector3(2.5f * MathF.Cos(a), -1.45f, 2.5f * MathF.Sin(a))); }
        foreach (var at in spots)
        {
            var skin = Identity(2);
            for (int i = 0; i < skin.Length; i++) skin[i] = Matrix4x4.CreateTranslation(at);
            var b = new ClothBody();
            b.Set(shape, skin, skin, 1f / 144f, 4);
            f.Bodies.Add(b);
        }
        f.BodyCount = f.Bodies.Count;
        f.Colliders.Add(FleshCollider.Plane(new Vector3(0f, -1.65f, 0f), Vector3.UnitY));
        Console.WriteLine($"   {spots.Count} players of {shape.Count} spheres each");
    }

    /// <summary>A 40 m floor of 0.5 m triangles (12,800), 30 cm under the sheet.</summary>
    static void FastMeshFloor(ClothFrame f)
    {
        var (fp, ft, _) = ClothGrid(81, 81, 40f, 40f, lying: true);
        f.Colliders.Add(FleshCollider.MeshShape(TriangleBvh.Build(fp, ft), Matrix4x4.CreateTranslation(0f, -0.3f, 0f), true));
        Console.WriteLine($"   a floor of {ft.Length / 3} triangles");
    }

    /// <summary>A 1.6 m sheet (33 x 33 points) settles for half a second, then a blast shreds its middle and pushes it at up
    /// to 50 m/s (uneven: up to about 80), with Drag 0.02: a second at 144 frames a second, then one at 10 (as the game
    /// slowed to).</summary>
    static void FastPieces(string what, Action<ClothFrame> setup, Vector3 blast, float floorY, int perPoint144, int perPoint10)
    {
        Console.WriteLine($"-- A shredded sheet blasted at 50 m/s with Drag 0.02, {what}");
        var (pos, tris, _) = ClothGrid(33, 33, 1.6f, 1.6f, lying: true);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s = new ClothSolver(mesh, new ClothSettings { Drag = 0.02f, Friction = 0.1f, Tearable = true });
        var f = ClothFrameAt(1f / 144f);
        setup(f);
        for (int k = 0; k < 72; k++) s.Step(f);
        int resting = s.Contacts;
        var shredder = new ClothShredder();
        var shot = new ClothShred { Center = Vector3.Zero, Radius = 0.6f, Shred = 1f, Seed = 99 };
        int parts = shredder.PartCount(mesh, 99, shot), cuts = 0;
        for (int p = 0; p < parts; p++) cuts += shredder.Apply(s, 99, shot, p);
        f.Kicks = new[] { new ClothKick { Center = blast, Radius = 1f, Speed = 50f, Randomness = 0.5f, Seed = 7 } };
        f.KickCount = 1;
        var (pairs144, ms144, worst144, fastest) = FastRun(s, f, 1f / 144f, 144);
        var (pairs10, ms10, worst10, _) = FastRun(s, f, 0.1f, 10);
        int n = mesh.ParticleCount;
        Console.WriteLine($"   {cuts} cuts, {n} points; {resting} touching something before the blast; fastest point {fastest:0} m/s");
        Console.WriteLine($"   at 144 fps: at most {pairs144} pairs in a frame ({pairs144 / (float)n:0.0} a point), {ms144:0.00} ms a frame (worst {worst144:0.00})");
        Console.WriteLine($"   at 10 fps: at most {pairs10} pairs in a frame ({pairs10 / (float)n:0.0} a point), {ms10:0.00} ms a frame (worst {worst10:0.00}); {s.ShapeCount} shapes near ({s.BodySpheres} body spheres, {s.MeshTriangles} mesh triangles)");
        Check(ClothFinite(s), "finite");
        Check(fastest > 20f, $"the pieces fly (fastest point {fastest:0} m/s)");
        Check(pairs144 <= perPoint144 * n, $"at 144 fps a frame pairs the points with at most {perPoint144} shapes each on average ({pairs144} pairs; before 0.33.1 every point paired with every shape near the cloth)");
        Check(pairs10 <= perPoint10 * n, $"at 10 fps too: at most {perPoint10} each ({pairs10} pairs; the frame's 0.1 s no longer widens the reach)");
        // nothing went through the floor where there is one (the 40 m mesh floor, or the plane)
        float lowest = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (mesh.FanSize(i) == 0) continue;
            var p = s.X[i];
            if (MathF.Abs(p.X) < 19.5f && MathF.Abs(p.Z) < 19.5f) lowest = MathF.Min(lowest, p.Y);
        }
        Check(lowest > floorY - 0.02f, $"no piece went through the floor (lowest point over it {lowest - floorY:0.000} m above it)");
    }

    /// <summary>Steps <paramref name="frames"/> frames of <paramref name="dt"/>: the most pairs in a frame, the mean and the
    /// worst step time, and the fastest point.</summary>
    static (int most, double ms, double worst, float fastest) FastRun(ClothSolver s, ClothFrame f, float dt, int frames)
    {
        f.Dt = dt;
        int most = 0, worstAt = 0, runs = 0; double ms = 0, worst = 0, pairsMs = 0, bodiesMs = 0, contactsMs = 0; float fastest = 0f;
        string worstWhat = "";
        for (int k = 0; k < frames; k++)
        {
            s.Step(f);
            most = Math.Max(most, s.PairCount); runs = Math.Max(runs, s.PairRuns);
            ms += s.LastStepMs; pairsMs += s.PairsMs; bodiesMs += s.BodiesMs; contactsMs += s.ContactsMs;
            if (s.LastStepMs > worst) { worst = s.LastStepMs; worstAt = k; worstWhat = $"pairs {s.PairsMs:0.00} (bodies {s.BodiesMs:0.00}), contacts {s.ContactsMs:0.00}, {s.PairCount} pairs"; }
            fastest = MathF.Max(fastest, ClothMaxSpeed(s));
        }
        Console.WriteLine($"   {frames} frames of {dt * 1000f:0.0} ms: a frame on average finds pairs in {pairsMs / frames:0.00} ms (bodies {bodiesMs / frames:0.00}), contacts {contactsMs / frames:0.00} ms, up to {runs} runs a frame; the worst, frame {worstAt}: {worst:0.00} ms ({worstWhat}); fastest point {fastest:0} m/s");
        return (most, ms / frames, worst, fastest);
    }
}
