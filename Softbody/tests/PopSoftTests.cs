using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Pop;

// 0.35.0: Pop's soft pieces (each a little cloth: a few particles that fly with the air on them, keep their edges and bend
// as Stiffness lets them, the piece's own vertices following) and Adhesion (pieces hold on to what they touch: a wall, a
// ceiling), soft and stiff.
// Run with:  dotnet run -c Release --project tests -- pop
namespace Softbody.Tests;

static partial class Program
{
    static void PopSoftTests()
    {
        Console.WriteLine("== Pop: soft pieces and adhesion (0.35.0)");
        PopSoftBuild();
        PopSoftSpin();
        PopSoftDrape();
        PopSoftAir();
        PopStick();
        PopSoftFlight();
        PopSoftFloors();
        PopSoftCost();
    }

    /// <summary>A square of n by n vertices, size metres a side, lying flat facing up, then placed.</summary>
    static PopMesh PopSquare(float size, int n, Matrix4x4 place)
    {
        var (pos, tris, uv) = ClothGrid(n, n, size, size, lying: true);
        var nrm = new Vector3[pos.Length];
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, place));
        for (int i = 0; i < pos.Length; i++) { pos[i] = Vector3.Transform(pos[i], place); nrm[i] = up; }
        return PopFrom(pos, nrm, tris, uv);
    }

    static PopPieces PopOne(PopMesh m) => PopCut.Cut(new[] { m }, new PopCutSettings { Shred = 0f, MaxPieces = 1, Seed = 1 });

    /// <summary>Hair: 400 small cards above a capsule's head, none touching another (as in PopCutPlayer).</summary>
    static PopMesh PopHairCards()
    {
        var hp = new List<Vector3>(); var hn = new List<Vector3>(); var ht = new List<int>();
        for (int k = 0; k < 400; k++)
        {
            float a = k * 2.399963f, rr = 0.03f + 0.15f * MathF.Sqrt((k + 0.5f) / 400f);
            var c = new Vector3(rr * MathF.Cos(a), 1.82f + 0.0005f * (k % 7), rr * MathF.Sin(a));
            int b = hp.Count;
            hp.Add(c); hp.Add(c + new Vector3(0.008f, 0f, 0f)); hp.Add(c + new Vector3(0.008f, 0f, 0.008f)); hp.Add(c + new Vector3(0f, 0f, 0.008f));
            for (int j = 0; j < 4; j++) hn.Add(Vector3.UnitY);
            ht.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        return PopFrom(hp.ToArray(), hn.ToArray(), ht.ToArray());
    }

    /// <summary>Soft piece i's worst stretch or squeeze of an edge, and change of a bending pair, each as a fraction of how
    /// long it was as cut.</summary>
    static (float stretch, float bend) PopSoftShape(PopSolver s, int i)
    {
        var S = s.Soft;
        float st = 0f, be = 0f;
        for (int e = S.EStart[i]; e < S.EStart[i + 1]; e++)
            st = MathF.Max(st, MathF.Abs(Vector3.Distance(s.SX[S.EA[e]], s.SX[S.EB[e]]) - S.EL[e]) / MathF.Max(S.EL[e], 1e-6f));
        for (int b = S.BStart[i]; b < S.BStart[i + 1]; b++)
            be = MathF.Max(be, MathF.Abs(Vector3.Distance(s.SX[S.BA[b]], s.SX[S.BB[b]]) - S.BL[b]) / MathF.Max(S.BL[b], 1e-6f));
        return (st, be);
    }

    /// <summary>How many parts soft piece i's particles make, joined through its edges (1: all tied together).</summary>
    static int PopSoftParts(PopSoftPieces S, int i)
    {
        int p0 = S.PStart[i], k = S.PStart[i + 1] - p0;
        var parent = new int[k];
        for (int a = 0; a < k; a++) parent[a] = a;
        int Root(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        for (int e = S.EStart[i]; e < S.EStart[i + 1]; e++)
        {
            int ra = Root(S.EA[e] - p0), rb = Root(S.EB[e] - p0);
            if (ra != rb) parent[ra] = rb;
        }
        int roots = 0;
        for (int a = 0; a < k; a++) if (Root(a) == a) roots++;
        return roots;
    }

    /// <summary>The motion of soft piece i's particles about their middle (J): its spin, and any flapping.</summary>
    static float PopSoftSpinEnergy(PopSolver s, int i)
    {
        var S = s.Soft;
        float m = 0f;
        var vc = Vector3.Zero;
        for (int p = S.PStart[i]; p < S.PStart[i + 1]; p++) { float mp = S.Area[p] * PopSolver.PieceWeight; m += mp; vc += s.SV[p] * mp; }
        vc /= m;
        float e = 0f;
        for (int p = S.PStart[i]; p < S.PStart[i + 1]; p++) e += 0.5f * S.Area[p] * PopSolver.PieceWeight * (s.SV[p] - vc).LengthSquared();
        return e;
    }

    static void PopSoftBuild()
    {
        Console.WriteLine("-- Soft pieces of a 30 cm square (one piece, and confetti) and of a capsule player (Shred 0.8, and confetti)");
        var square = PopSquare(0.3f, 31, Matrix4x4.Identity);
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var body = PopFrom(bp, bn, bt);
        var cases = new (string what, PopPieces cut)[]
        {
            ("the square in one piece", PopOne(square)),
            ("the square in confetti", PopCut.Cut(new[] { square }, new PopCutSettings { Shred = 1f, MaxPieces = 400, Seed = 5 })),
            ("the capsule at Shred 0.8", PopCut.Cut(new[] { body }, new PopCutSettings { Shred = 0.8f, MaxPieces = 1500, Seed = 11 })),
            ("the capsule in confetti", PopCut.Cut(new[] { body }, new PopCutSettings { Shred = 1f, MaxPieces = 1500, Seed = 4 })),
            // more parts than MaxPieces: the cut keeps each cell's cards together in one piece
            ("the capsule and 400 hair cards at MaxPieces 50", PopCut.Cut(new[] { body, PopHairCards() }, new PopCutSettings { Shred = 0f, MaxPieces = 50, Seed = 1 })),
        };
        foreach (var (what, cut) in cases)
        {
            var sw = Stopwatch.StartNew();
            var S = PopSoft.Build(cut);
            double ms = sw.Elapsed.TotalMilliseconds;
            int few = 0, noTri = 0, apart = 0, most = 0;
            for (int i = 0; i < S.Count; i++)
            {
                int k = S.PStart[i + 1] - S.PStart[i];
                most = Math.Max(most, k);
                if (k < 3) few++;
                if (S.TStart[i + 1] == S.TStart[i]) noTri++;
                if (PopSoftParts(S, i) > 1) apart++;
            }
            // the skin: three particles of the vertex's own piece, by weights from 0 to 1 adding up to 1
            int badSkin = 0;
            var of = PopPieceOf(cut);
            for (int v = 0; v < cut.FrontVertices; v++)
            {
                float sum = 0f;
                bool ok = of[v] >= 0;
                for (int j = 0; j < 3 && ok; j++)
                {
                    int p = S.SkinP[3 * v + j];
                    float w = S.SkinW[3 * v + j];
                    ok = w >= 0f && w <= 1f && p >= S.PStart[of[v]] && p < S.PStart[of[v] + 1];
                    sum += w;
                }
                if (!ok || MathF.Abs(sum - 1f) > 1e-4f) badSkin++;
            }
            // as cut, before any step, the soft pieces show the model exactly
            var s = new PopSolver(cut, S);
            s.Step(0f, new Vector3(0f, -9.81f, 0f), null);
            float err = 0f, nerr = 0f;
            for (int v = 0; v < cut.FrontVertices; v++)
            {
                err = MathF.Max(err, Vector3.Distance(s.OutPositions[v], cut.Positions[v]));
                nerr = MathF.Max(nerr, Vector3.Distance(s.OutNormals[v], cut.Normals[v]));
            }
            bool inside = true;
            s.Bounds(out var blo, out var bhi);
            for (int v = 0; v < cut.FrontVertices && inside; v++) inside = Vector3.Min(s.OutPositions[v], blo) == blo && Vector3.Max(s.OutPositions[v], bhi) == bhi;
            Console.WriteLine($"   {what}: {cut.Count} pieces, {S.Particles} particles about {S.Spacing * 100f:0.0} cm apart (up to {most} in a piece), {S.Triangles} triangles, {S.Edges} edges, {S.Bends} bending pairs; built in {ms:0.0} ms");
            Check(few == 0 && noTri == 0, $"{what}: every piece has 3 particles or more and a triangle of them ({few} with fewer, {noTri} with none)");
            Check(apart == 0 && most <= PopSoft.MostPerPiece, $"{what}: each piece's particles are all tied together by its edges ({apart} pieces in parts), {PopSoft.MostPerPiece} at most");
            Check(badSkin == 0, $"{what}: every vertex follows three particles of its own piece, by weights adding up to 1 ({badSkin} not)");
            Check(err < 1e-5f && nerr < 1e-5f, $"{what}: as cut, the soft pieces show the model exactly (worst {err * 1000f:0.0000} mm, normals {nerr:0.0e0})");
            Check(inside, $"{what}: the bounds hold every vertex");
            if (cut.Count > 1000)
                Check(S.Particles <= Math.Max(PopSoft.MostParticles, PopSoft.MostAtLeast * cut.Count) * 5 / 4, $"{what}: about {PopSoft.MostParticles} particles at most, or {PopSoft.MostAtLeast} a piece ({S.Particles} for {cut.Count} pieces)");
        }
        var first = PopSoft.Build(cases[2].cut);
        var again = PopSoft.Build(cases[2].cut);
        bool same = again.Particles == first.Particles && again.Triangles == first.Triangles && again.Bends == first.Bends;
        for (int p = 0; same && p < first.Particles; p++) same = again.Rest[p] == first.Rest[p];
        Check(same, "the same cut gives the same particles");

        // the square cut along a line its weld misses (its two halves 0.05 mm apart): still one piece of cloth
        var (hp, ht, hu) = ClothGrid(16, 31, 0.15f, 0.3f, lying: true);
        var cracked = new List<Vector3>(); var cn = new List<Vector3>(); var ctr = new List<int>();
        for (int side = 0; side < 2; side++)
        {
            int b = cracked.Count;
            foreach (var p in hp) { cracked.Add(p + new Vector3(side == 0 ? -0.075f : 0.075f + 0.00005f, 0f, 0f)); cn.Add(Vector3.UnitY); }
            foreach (int t in ht) ctr.Add(b + t);
        }
        var crack = PopCut.Cut(new[] { PopFrom(cracked.ToArray(), cn.ToArray(), ctr.ToArray()) }, new PopCutSettings { Shred = 0f, MaxPieces = 1, Seed = 1 });
        var cs = PopSoft.Build(crack);
        Check(crack.Count == 1 && PopSoftParts(cs, 0) == 1, $"a piece whose halves only the cut's weld joins is still tied together ({crack.Count} pieces, {PopSoftParts(cs, 0)} parts)");
    }

    static void PopSoftSpin()
    {
        Console.WriteLine("-- A 30 cm soft square (Stiffness 0.3) spinning free: no gravity, no air (90 fps, 2 s)");
        var cut = PopOne(PopSquare(0.3f, 31, Matrix4x4.Identity));
        var s = new PopSolver(cut, PopSoft.Build(cut)) { Drag = 0f, StopBelow = -50f };
        var w = new Vector3(1.5f, 9f, 0.5f);   // mostly about its normal, tipped a little
        var c = s.X[0];
        var vel = new Vector3(0.5f, 0f, 0f);
        for (int p = 0; p < s.SX.Length; p++) s.SV[p] = vel + Vector3.Cross(w, s.SX[p] - c);
        float e0 = PopSoftSpinEnergy(s, 0);
        float worstSt = 0f, worstBe = 0f;
        for (int f = 0; f < 180; f++)
        {
            s.Step(1f / 90f, Vector3.Zero, null);
            var (st, be) = PopSoftShape(s, 0);
            worstSt = MathF.Max(worstSt, st); worstBe = MathF.Max(worstBe, be);
        }
        float e1 = PopSoftSpinEnergy(s, 0);
        float drift = Vector3.Distance(s.X[0], c + vel * 2f);
        Console.WriteLine($"   {s.Soft.Particles} particles; worst edge {worstSt * 100f:0.00}% off, bending pair {worstBe * 100f:0.00}% off; {e1 / e0 * 100f:0}% of its spinning energy left; {drift * 1000f:0.0} mm off its path");
        Check(ClothFiniteArr(s.OutPositions), "finite");
        Check(worstSt < 0.03f && worstBe < 0.05f, $"spinning alone, it keeps its edges and its shape (worst {worstSt * 100f:0.0}% and {worstBe * 100f:0.0}%)");
        Check(e1 > 0.6f * e0, $"the damping within it leaves its spin ({e1 / e0 * 100f:0}% after 2 s)");
        Check(drift < 0.005f, $"and its flight ({drift * 1000f:0.0} mm off)");
    }

    static void PopSoftDrape()
    {
        Console.WriteLine("-- A 30 cm soft square dropped on a table, 10 cm of it over the edge (90 fps, 4 s)");
        var cut = PopOne(PopSquare(0.3f, 31, Matrix4x4.CreateTranslation(0.45f, 0.01f, 0f)));
        var soft = PopSoft.Build(cut);
        var table = FleshCollider.Box(new Vector3(0f, -0.5f, 0f), Quaternion.Identity, new Vector3(0.5f, 0.5f, 0.5f));
        var cols = new List<FleshCollider> { table, FleshCollider.Plane(new Vector3(0f, -1f, 0f), Vector3.UnitY) };
        var droop = new List<float>();
        foreach (float st in new[] { 0f, 0.3f, 0.6f, 1f })
        {
            var s = new PopSolver(cut, soft) { Stiffness = st, StopBelow = -50f };
            for (int f = 0; f < 360; f++) s.Step(1f / 90f, new Vector3(0f, -9.81f, 0f), cols);
            float low = float.MaxValue, inside = 0f, onTop = float.MaxValue;
            for (int v = 0; v < cut.FrontVertices; v++)
            {
                var o = s.OutPositions[v];
                low = MathF.Min(low, o.Y);
                if (o.X < 0.5f && o.Y < 0f) inside = MathF.Max(inside, MathF.Min(0.5f - o.X, -o.Y));
                if (o.X < 0.45f) onTop = MathF.Min(onTop, o.Y);
            }
            droop.Add(-low);
            Console.WriteLine($"   Stiffness {st:0.0}: the free edge hangs {-low * 100f:0.0} cm below the top; on the table its vertices {onTop * 1000f:0.0} mm up at the lowest, at most {inside * 1000f:0.0} mm in it; {(s.Awake == 0 ? "asleep" : "awake")}");
            Check(ClothFiniteArr(s.OutPositions), $"Stiffness {st:0.0}: finite");
            Check(inside < 0.005f && onTop > 0f, $"Stiffness {st:0.0}: it lies on the table, not in it ({inside * 1000f:0.0} mm in at the edge, {onTop * 1000f:0.0} mm up on top)");
        }
        Check(droop[0] > droop[1] && droop[1] > droop[2] && droop[2] >= droop[3] - 0.002f,
              $"the softer, the more it drapes over the edge ({string.Join(", ", droop.Select(d => $"{d * 100f:0.0}"))} cm)");
        Check(droop[0] > 0.05f && droop[3] < 0.01f, $"at Stiffness 0 it hangs down like cloth ({droop[0] * 100f:0.0} cm), at 1 it stays about flat ({droop[3] * 100f:0.0} cm)");
    }

    static void PopSoftAir()
    {
        Console.WriteLine("-- A 20 cm soft square dropped about flat from 3 m, through air (Drag 1) and none (Drag 0), 90 fps");
        var cut = PopOne(PopSquare(0.2f, 21, Matrix4x4.CreateFromYawPitchRoll(0.2f, 0.15f, 0.1f) * Matrix4x4.CreateTranslation(0f, 3f, 0f)));
        var soft = PopSoft.Build(cut);
        var cols = new List<FleshCollider> { FleshCollider.Plane(Vector3.Zero, Vector3.UnitY) };
        var g = new Vector3(0f, -9.81f, 0f);
        var time = new float[2]; var drift = new float[2]; var bent = new float[2];
        for (int k = 0; k < 2; k++)
        {
            var s = new PopSolver(cut, soft) { Drag = k, StopBelow = -50f };
            var start = s.X[0];
            int f = 0;
            for (; f < 900; f++)
            {
                s.Step(1f / 90f, g, cols);
                if (s.Contacts > 0) break;
                bent[k] = MathF.Max(bent[k], PopSoftShape(s, 0).bend);
            }
            time[k] = (f + 1) / 90f;
            drift[k] = new Vector2(s.X[0].X - start.X, s.X[0].Z - start.Z).Length();
            Console.WriteLine($"   Drag {k}: lands after {time[k]:0.00} s, {drift[k] * 100f:0} cm to the side; bent up to {bent[k] * 100f:0.0}% on the way");
        }
        Check(time[0] < 0.85f, $"without air it falls as a stone does ({time[0]:0.00} s from 3 m)");
        Check(time[1] > 1.2f * time[0], $"through air it falls slower ({time[1]:0.00} s against {time[0]:0.00} s)");
        Check(bent[1] > bent[0], $"and the air bends it as it goes ({bent[1] * 100f:0.0}% against {bent[0] * 100f:0.0}%)");
    }

    /// <summary>Pieces thrown at a wall and at a ceiling: with Adhesion they stay where they hit, without it they fall.</summary>
    static void PopStick()
    {
        Console.WriteLine("-- A 20 cm square thrown at a wall (4 m/s) and up at a ceiling (5 m/s), soft and stiff, Adhesion 2 and 0 (no air, 90 fps, 3 s)");
        var g = new Vector3(0f, -9.81f, 0f);
        var floor = FleshCollider.Plane(Vector3.Zero, Vector3.UnitY);
        var wall = FleshCollider.Box(new Vector3(0.6f, 1f, 0f), Quaternion.Identity, new Vector3(0.1f, 1f, 1f));        // its face at x = 0.5
        var ceiling = FleshCollider.Box(new Vector3(0f, 1.6f, 0f), Quaternion.Identity, new Vector3(1f, 0.1f, 1f));     // its face at y = 1.5
        var upright = PopOne(PopSquare(0.2f, 21, Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 1f, 0f)));
        var flat = PopOne(PopSquare(0.2f, 21, Matrix4x4.CreateTranslation(0f, 0.8f, 0f)));
        var cases = new (string what, PopPieces cut, FleshCollider shape, Vector3 thrown)[]
        {
            ("wall", upright, wall, new Vector3(4f, 0f, 0f)),
            ("ceiling", flat, ceiling, new Vector3(0f, 5f, 0f)),
        };
        foreach (var (what, cut, shape, thrown) in cases)
            foreach (bool soft in new[] { true, false })
            {
                var sp = soft ? PopSoft.Build(cut) : null;
                var end = new float[2]; var hit = new float[2]; var awake = new int[2];
                for (int k = 0; k < 2; k++)
                {
                    float adhesion = k == 0 ? 2f : 0f;
                    var s = new PopSolver(cut, sp) { Adhesion = adhesion, Drag = 0f, StopBelow = -50f };
                    s.Carry(thrown);
                    var cols = new List<FleshCollider> { shape, floor };
                    hit[k] = float.NaN;
                    for (int f = 0; f < 270; f++)
                    {
                        s.Step(1f / 90f, g, cols);
                        if (float.IsNaN(hit[k]) && s.Contacts > 0) hit[k] = s.X[0].Y;
                    }
                    end[k] = s.X[0].Y;
                    awake[k] = s.Awake;
                    Check(ClothFiniteArr(s.OutPositions), $"{what}, {(soft ? "soft" : "stiff")}, Adhesion {adhesion:0}: finite");
                }
                string kind = soft ? "soft" : "stiff";
                Console.WriteLine($"   {what}, {kind}: hit at {hit[0] * 100f:0} cm high; after 3 s at {end[0] * 100f:0} cm with Adhesion 2 ({(awake[0] == 0 ? "asleep" : "awake")}), {end[1] * 100f:0} cm without");
                if (what == "wall")
                {
                    Check(end[0] > hit[0] - 0.05f && awake[0] == 0, $"{kind}, Adhesion 2: it sticks to the wall where it hit and lies still there ({(hit[0] - end[0]) * 100f:0.0} cm lower)");
                    Check(end[1] < 0.2f, $"{kind}, Adhesion 0: it slides down the wall to the floor ({end[1] * 100f:0} cm)");
                }
                else
                {
                    Check(end[0] > 1.45f && awake[0] == 0, $"{kind}, Adhesion 2: it sticks to the ceiling and lies still there ({end[0] * 100f:0.0} cm)");
                    Check(end[1] < 0.1f, $"{kind}, Adhesion 0: it falls off the ceiling to the floor ({end[1] * 100f:0} cm)");
                }
            }
    }

    /// <summary>A capsule player popped into soft pieces over a floor: away from its middle, bending as they go, down on
    /// the floor, still.</summary>
    static void PopSoftFlight()
    {
        Console.WriteLine("-- A capsule player pops into soft pieces over a floor (Shred 0.8, Strength 5, Randomness 0.5, and the Pop's Stiffness 0.3, Drag 0.5 and Adhesion 2, 90 fps)");
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var cut = PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 0.8f, MaxPieces = 1500, Seed = 11 });
        var soft = PopSoft.Build(cut);
        var s = new PopSolver(cut, soft) { Stiffness = 0.3f, Drag = 0.5f, Adhesion = 2f, StopBelow = -50f };
        var center = new Vector3(0f, 0.9f, 0f);
        s.Kick(center, 5f, 0.5f, 11);
        var cols = new List<FleshCollider> { FleshCollider.Plane(Vector3.Zero, Vector3.UnitY) };
        var g = new Vector3(0f, -9.81f, 0f);
        s.Step(1f / 90f, g, cols);
        double outward = 0;
        for (int i = 0; i < cut.Count; i++) outward += Vector3.Dot(s.V[i], Vector3.Normalize(s.X[i] - center + new Vector3(1e-6f)));
        outward /= cut.Count;
        var of = PopPieceOf(cut);
        var slow = new bool[cut.Count];
        var bentMost = new float[cut.Count];
        float lowest = float.MaxValue, lowSlow = float.MaxValue;
        int worstV = -1, worstFrame = -1;
        bool boxed = true, bounded = true;
        int frames = 0;
        double ms = 0;
        for (; frames < 90 * 8; frames++)
        {
            for (int i = 0; i < cut.Count; i++) slow[i] = s.V[i].Length() < 1f;
            s.Step(1f / 90f, g, cols);
            ms += s.LastStepMs;
            for (int v = 0; v < cut.FrontVertices; v++)
            {
                float y = s.OutPositions[v].Y;
                if (y < lowest) { lowest = y; worstV = v; worstFrame = frames; }
                if (slow[of[v]] && s.V[of[v]].Length() < 1f) lowSlow = MathF.Min(lowSlow, y);
            }
            for (int i = 0; i < cut.Count; i++) bentMost[i] = MathF.Max(bentMost[i], PopSoftShape(s, i).bend);
            if (frames % 15 == 0)
            {
                // the plugin gathers the world's shapes in the awake pieces' box, and the renderer culls by the bounds:
                // each holds every vertex it should
                s.Bounds(out var blo, out var bhi);
                bool any = s.AwakeBounds(0.1f, out var alo, out var ahi);
                for (int v = 0; v < cut.FrontVertices; v++)
                {
                    var o = s.OutPositions[v];
                    bounded &= Vector3.Min(o, blo) == blo && Vector3.Max(o, bhi) == bhi;
                    if (any && s.V[of[v]] != Vector3.Zero) boxed &= Vector3.Min(o, alo) == alo && Vector3.Max(o, ahi) == ahi;
                }
            }
        }
        int up = 0, bentCount = 0, bentSome = 0, noBends = 0, single = 0;
        for (int i = 0; i < cut.Count; i++)
        {
            float low = float.MaxValue;
            for (int p = soft.PStart[i]; p < soft.PStart[i + 1]; p++) low = MathF.Min(low, s.SX[p].Y);
            if (low > 0.02f) up++;
            if (bentMost[i] > 0.05f) bentCount++;
            if (bentMost[i] > 0.02f) bentSome++;
            if (soft.BStart[i + 1] == soft.BStart[i]) { if (soft.PStart[i + 1] - soft.PStart[i] >= 4) noBends++; else single++; }
        }
        float rest = float.MaxValue;
        for (int v = 0; v < cut.FrontVertices; v++) rest = MathF.Min(rest, s.OutPositions[v].Y);
        s.Step(1f / 90f, g, cols);
        Console.WriteLine($"   {cut.Count} pieces, {soft.Particles} particles; at first {outward:0.0} m/s outward on average; pieces bent by more than 2% on the way: {bentSome}, more than 5%: {bentCount} ({noBends} of 4 particles or more cannot bend, {single} are one triangle); after 8 s {s.Awake} awake, lowest vertex {lowest * 1000f:0.0} mm (slow {lowSlow * 1000f:0.0}, at rest {rest * 1000f:0.0}), {ms / frames:0.00} ms a frame");
        Check(ClothFiniteArr(s.OutPositions), "finite");
        Check(outward > 2.5f, $"the pieces fly away from the middle ({outward:0.0} m/s)");
        Check(bentSome > cut.Count / 3 && noBends == 0, $"and bend as cloth does on the way ({bentSome} of {cut.Count} by more than 2%; every piece of 4 particles or more can bend: {noBends} cannot)");
        Check(lowest > -0.001f && rest > -0.001f, $"no vertex shows in the floor, landing edge first at 6 m/s or lying still (lowest {lowest * 1000f:0.0} mm; slower than 1 m/s {lowSlow * 1000f:0.0} mm; at rest {rest * 1000f:0.0} mm)");
        Func<PopSolver> again = () =>
        {
            var r = new PopSolver(cut, soft) { Stiffness = s.Stiffness, Drag = s.Drag, Adhesion = s.Adhesion, StopBelow = -50f };
            r.Kick(center, 5f, 0.5f, 11);
            r.Step(1f / 90f, g, cols);
            return r;
        };
        if (lowest <= -0.001f) PopSoftWhyLow(cut, again, 1f / 90f, g, cols, worstV, worstFrame);
        Check(up == 0, $"every piece came down ({up} still up)");
        Check(s.Awake <= cut.Count / 20, $"and lies still ({s.Awake} of {cut.Count} awake)");
        Check(!s.Changed || s.Awake > 0, "once all are still, the mesh stops changing (no more uploads)");
        Check(bounded, "the bounds hold every vertex in flight");
        Check(boxed, "the awake pieces' box (where the plugin gathers the world's shapes) holds every flying piece's vertices");
        // the end of the lifetime: the pieces shrink away about their middles, sleeping ones too
        float before = 0f, after = 0f;
        for (int v = 0; v < cut.FrontVertices; v++) if (s.V[of[v]] == Vector3.Zero) before = MathF.Max(before, Vector3.Distance(s.OutPositions[v], s.X[of[v]]));
        s.Shrink = 0.5f;
        s.Step(1f / 90f, g, cols);
        bool rewrote = s.Changed;
        for (int v = 0; v < cut.FrontVertices; v++) if (s.V[of[v]] == Vector3.Zero) after = MathF.Max(after, Vector3.Distance(s.OutPositions[v], s.X[of[v]]));
        Console.WriteLine($"   shrunk to half: the farthest vertex from its piece's middle {before * 1000f:0.0} mm, then {after * 1000f:0.0} mm");
        Check(rewrote && MathF.Abs(after - 0.5f * before) < 0.001f + 0.02f * before, "shrinking at the end of the lifetime rewrites the sleeping pieces too, half the size at Shrink 0.5");
    }

    /// <summary>Soft confetti thrown straight down at 60 m/s (25 cm a step) at thin floors 2 m below.</summary>
    static void PopSoftFloors()
    {
        Console.WriteLine("-- Soft confetti thrown down at 60 m/s at thin floors 2 m below (no air, and air at 30 fps)");
        var (sp, st, su) = ClothGrid(7, 7, 0.3f, 0.3f, lying: true);
        var sn = new Vector3[sp.Length];
        Array.Fill(sn, Vector3.UnitY);
        var cut = PopCut.Cut(new[] { PopFrom(sp, sn, st, su) }, new PopCutSettings { Shred = 1f, MaxPieces = 200, Seed = 2 });
        var soft = PopSoft.Build(cut);
        var (fp, ft, _) = ClothGrid(9, 9, 8f, 8f, lying: true);
        bool up = Vector3.Cross(fp[ft[1]] - fp[ft[0]], fp[ft[2]] - fp[ft[0]]).Y > 0f;
        var place = (up ? Matrix4x4.Identity : Matrix4x4.CreateRotationX(MathF.PI)) * Matrix4x4.CreateTranslation(0f, -2f, 0f);
        var bvh = TriangleBvh.Build(fp, ft);
        var floors = new (string what, FleshCollider c)[]
        {
            ("a two-sided mesh floor", FleshCollider.MeshShape(bvh, place, true)),
            ("a front-only mesh floor", FleshCollider.MeshShape(bvh, place, false)),
            ("a 2 cm box", FleshCollider.Box(new Vector3(0f, -2.01f, 0f), Quaternion.Identity, new Vector3(4f, 0.01f, 4f))),
        };
        foreach (var (what, c) in floors)
            foreach (var (fps, drag) in new[] { (30, 0f), (144, 0f), (30, 1f) })
            {
                var s = new PopSolver(cut, soft) { Drag = drag, Adhesion = 2f, StopBelow = -50f };
                s.Carry(new Vector3(0f, -60f, 0f));
                var cols = new List<FleshCollider> { c };
                float lowest = float.MaxValue, lowP = float.MaxValue;
                for (int k = 0; k < fps * 3; k++)
                {
                    s.Step(1f / fps, new Vector3(0f, -9.81f, 0f), cols);
                    for (int v = 0; v < cut.FrontVertices; v++) lowest = MathF.Min(lowest, s.OutPositions[v].Y);
                    foreach (var x in s.SX) lowP = MathF.Min(lowP, x.Y);
                }
                string how = $"{what}, {fps} fps{(drag > 0f ? ", air" : "")}";
                Console.WriteLine($"   {how}: lowest particle {(lowP + 2f) * 1000f:0.0} mm from the floor, lowest vertex {(lowest + 2f) * 1000f:0.0} mm; {s.Awake} of {cut.Count} awake after 3 s");
                Check(lowP > -2.001f && lowest > -2.004f, $"{how}: no piece goes through ({(lowP + 2f) * 1000f:0.0} mm, vertices {(lowest + 2f) * 1000f:0.0} mm)");
            }
    }

    /// <summary>Plays a soft run again up to the frame its lowest vertex was seen in, and shows that vertex's piece.</summary>
    static void PopSoftWhyLow(PopPieces cut, Func<PopSolver> make, float dt, Vector3 g, List<FleshCollider> cols, int v, int frame)
    {
        var s = make();
        var S = s.Soft;
        int i = PopPieceOf(cut)[v], o = 3 * v, p0 = S.PStart[i];
        float from = Vector3.Distance(cut.Positions[v] - S.Middle[i], S.Rest[S.SkinP[o]]);
        Console.WriteLine($"   why: vertex {v} of piece {i} ({cut.VertexCount[i]} vertices, {S.PStart[i + 1] - p0} particles, {S.TStart[i + 1] - S.TStart[i]} triangles, {S.BStart[i + 1] - S.BStart[i]} bending pairs), lowest in frame {frame}; " +
                          $"it follows particles {S.SkinP[o] - p0}, {S.SkinP[o + 1] - p0}, {S.SkinP[o + 2] - p0} by {S.SkinW[o]:0.00}, {S.SkinW[o + 1]:0.00}, {S.SkinW[o + 2]:0.00}, as cut {from * 100f:0.0} cm from the first");
        for (int f = 0; f <= frame; f++)
        {
            s.Step(dt, g, cols);
            if (f < frame - 4) continue;
            float pmin = float.MaxValue;
            for (int p = p0; p < S.PStart[i + 1]; p++) pmin = MathF.Min(pmin, s.SX[p].Y);
            var (st, be) = PopSoftShape(s, i);
            Console.WriteLine($"     frame {f}: the vertex {s.OutPositions[v].Y * 1000f:0.0} mm; its particles {s.SX[S.SkinP[o]].Y * 1000f:0.0}, {s.SX[S.SkinP[o + 1]].Y * 1000f:0.0}, {s.SX[S.SkinP[o + 2]].Y * 1000f:0.0} mm, " +
                              $"the piece's lowest {pmin * 1000f:0.0} mm; {s.V[i].Length():0.00} m/s; edges {st * 100f:0}% off, bending {be * 100f:0}% off; {(s.V[i] == Vector3.Zero ? "asleep" : "awake")}");
        }
    }

    /// <summary>Soft confetti (MaxPieces 1500) over a 40 m mesh floor of 12,800 triangles.</summary>
    static void PopSoftCost()
    {
        Console.WriteLine("-- Soft and stiff confetti (MaxPieces 1500) over a two-sided mesh floor of 12,800 triangles (Strength 8, Drag 0.5, Adhesion 2 and 10, 90 fps)");
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var cut = PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 1f, MaxPieces = 1500, Seed = 4 });
        PopSoft.Build(cut);
        var sw = Stopwatch.StartNew();
        var soft = PopSoft.Build(cut);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        var (fp, ft, _) = ClothGrid(81, 81, 40f, 40f, lying: true);
        bool up = Vector3.Cross(fp[ft[1]] - fp[ft[0]], fp[ft[2]] - fp[ft[0]]).Y > 0f;
        var place = up ? Matrix4x4.Identity : Matrix4x4.CreateRotationX(MathF.PI);
        var cols = new List<FleshCollider> { FleshCollider.MeshShape(TriangleBvh.Build(fp, ft), place, true) };
        var g = new Vector3(0f, -9.81f, 0f);
        foreach (var (what, sp, adh) in new[] { ("soft", soft, 2f), ("stiff", (PopSoftPieces)null, 2f), ("soft, Adhesion 10", soft, 10f), ("stiff, Adhesion 10", (PopSoftPieces)null, 10f) })
        {
            var s = new PopSolver(cut, sp) { Drag = 0.5f, Adhesion = adh, StopBelow = -50f };
            s.Kick(new Vector3(0f, 0.9f, 0f), 8f, 0.5f, 4);
            double ms = 0, worst = 0;
            float lowest = float.MaxValue;
            int frames = 270, worstV = -1, worstFrame = -1;
            for (int k = 0; k < frames; k++)
            {
                s.Step(1f / 90f, g, cols);
                ms += s.LastStepMs; worst = Math.Max(worst, s.LastStepMs);
                for (int v = 0; v < cut.FrontVertices; v++) if (s.OutPositions[v].Y < lowest) { lowest = s.OutPositions[v].Y; worstV = v; worstFrame = k; }
            }
            Console.WriteLine($"   {what}: {cut.Count} pieces{(sp != null ? $", {sp.Particles} particles (built in {buildMs:0.0} ms)" : "")}; {ms / frames:0.00} ms a frame (worst {worst:0.00}), {s.Awake} awake after 3 s, lowest vertex {lowest * 1000f:0.0} mm");
            Check(lowest > -0.004f, $"{what}: none goes through the mesh floor ({lowest * 1000f:0.0} mm)");
            if (lowest <= -0.004f)
            {
                Func<PopSolver> again = () => { var r = new PopSolver(cut, sp) { Drag = 0.5f, Adhesion = adh, StopBelow = -50f }; r.Kick(new Vector3(0f, 0.9f, 0f), 8f, 0.5f, 4); return r; };
                if (sp != null) PopSoftWhyLow(cut, again, 1f / 90f, g, cols, worstV, worstFrame);
                else PopWhyLow(cut, again, 1f / 90f, g, cols, worstV, worstFrame);
            }
            if (what == "soft") Check(ms / frames < 6.0, $"soft: a frame takes {ms / frames:0.00} ms on average (under 6)");
        }
    }
}
