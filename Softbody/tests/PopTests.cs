using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Pop;

// 0.34.0: Pop. A mesh cut into pieces the Cloth Blast's way (more pieces than it has triangles where Shred asks for them,
// with its UVs and every other channel kept exactly), and the pieces flying, landing and lying still on the world: a
// plane, thin mesh floors (two-sided and front only) and a thin box, from 30 to 144 frames a second.
// Run with:  dotnet run -c Release --project tests -- pop
namespace Softbody.Tests;

static partial class Program
{
    static void PopTests()
    {
        Console.WriteLine("== Pop (0.34.0)");
        PopCutSquare();
        PopCutPlayer();
        PopLanding();
        PopFlight();
        PopFloors();
        PopCost();
        PopSoftTests();
        PopStrengthTests();
    }

    static PopMesh PopFrom(Vector3[] pos, Vector3[] nrm, int[] tris, Vector2[] uv = null)
    {
        var m = new PopMesh { Positions = pos, Normals = nrm };
        if (uv != null)
        {
            var ch = new Vector4[uv.Length];
            for (int i = 0; i < uv.Length; i++) ch[i] = new Vector4(uv[i].X, uv[i].Y, 0f, 0f);
            m.Uvs.Add(ch);
        }
        m.Submeshes.Add(tris);
        return m;
    }

    static double PopFrontArea(PopPieces p)
    {
        double a = 0;
        foreach (var sub in p.Submeshes)
            for (int k = 0; k + 2 < sub.Length / 2; k += 3)
                a += 0.5 * Vector3.Cross(p.Positions[sub[k + 1]] - p.Positions[sub[k]], p.Positions[sub[k + 2]] - p.Positions[sub[k]]).Length();
        return a;
    }

    /// <summary>Which piece each front vertex belongs to (-1: none).</summary>
    static int[] PopPieceOf(PopPieces p)
    {
        var of = new int[p.FrontVertices];
        Array.Fill(of, -1);
        for (int i = 0; i < p.Count; i++)
            for (int v = p.VertexStart[i]; v < p.VertexStart[i] + p.VertexCount[i]; v++) of[v] = i;
        return of;
    }

    static void PopCutSquare()
    {
        Console.WriteLine("-- A 1 m square of two triangles, UVs across it");
        var (pos, tris, uv) = ClothGrid(2, 2, 1f, 1f, lying: true);
        var nrm = new Vector3[pos.Length];
        Array.Fill(nrm, Vector3.UnitY);
        var meshes = new[] { PopFrom(pos, nrm, tris, uv) };
        var counts = new List<int>();
        PopPieces fine = null;
        foreach (float shred in new[] { 0f, 0.5f, 1f })
        {
            var p = PopCut.Cut(meshes, new PopCutSettings { Shred = shred, MaxPieces = 400, Seed = 5 });
            Console.WriteLine($"   Shred {shred:0.0}: {p.Count} pieces about {p.Spacing * 100f:0.0} cm across, {p.Triangles} triangles (cut to {p.CutTo * 100f:0.0} cm), {p.FrontVertices} vertices, {p.Tries} tries");
            counts.Add(p.Count);
            fine = p;
        }
        var q = fine;
        Check(counts[0] < counts[1] && counts[1] < counts[2], $"more Shred, more pieces ({string.Join(", ", counts)})");
        Check(q.Count > 2 && q.Triangles > 2, $"at Shred 1 more pieces than the square has triangles ({q.Count} pieces from 2 triangles), with no new triangles in the source");
        Check(q.Count <= 400, $"no more than MaxPieces ({q.Count} of 400)");
        Check(Math.Abs(PopFrontArea(q) - 1.0) < 1e-4, $"the pieces cover the square exactly ({PopFrontArea(q):0.00000} m²)");
        float uvErr = 0f, nErr = 0f, backErr = 0f;
        for (int v = 0; v < q.FrontVertices; v++)
        {
            var p = q.Positions[v];
            var t = q.Uvs[0][v];
            uvErr = MathF.Max(uvErr, MathF.Max(MathF.Abs(t.X - (p.X + 0.5f)), MathF.Abs(t.Y - (p.Z + 0.5f))));
            nErr = MathF.Max(nErr, Vector3.Distance(q.Normals[v], Vector3.UnitY));
            int b = v + q.FrontVertices;
            backErr = MathF.Max(backErr, Vector3.Distance(q.Normals[b], -Vector3.UnitY) + Vector3.Distance(q.Positions[b], p - Vector3.UnitY * PopCut.BackOffset) +
                                         Vector4.Distance(q.Uvs[0][b], t));
        }
        Check(uvErr < 1e-5f, $"every vertex keeps the UV of where it sits on the original (worst {uvErr:0.0e0})");
        Check(nErr < 1e-5f && backErr < 1e-5f, "normals kept, and each flake's back face is the same just behind, facing the other way");
        Check(q.Submeshes.Count == 1 && q.Submeshes[0].Length == 6 * q.Triangles, "one submesh, the front triangles and then as many back ones");
        var of = PopPieceOf(q);
        bool whole = true, oneEach = true;
        for (int v = 0; v < of.Length; v++) whole &= of[v] >= 0;
        var sub = q.Submeshes[0];
        for (int k = 0; k + 2 < sub.Length / 2; k += 3) oneEach &= of[sub[k]] == of[sub[k + 1]] && of[sub[k]] == of[sub[k + 2]];
        Check(whole && oneEach, "every vertex in one piece, and every triangle in one piece");
        float sum = 0f; int fewPoints = 0;
        for (int i = 0; i < q.Count; i++)
        {
            var s = Vector3.Zero;
            for (int k = 0; k < q.PointCount[i]; k++) s += q.Points[q.PointStart[i] + k];
            sum = MathF.Max(sum, s.Length());
            if (q.PointCount[i] < 3) fewPoints++;
        }
        Check(sum < 1e-4f && fewPoints == 0, $"each piece collides with at least 3 of its points, around its origin ({fewPoints} with fewer)");
        int uncovered = 0;
        for (int i = 0; i < q.Count; i++)
        {
            if (q.VertexCount[i] > PopCut.MostSlowPoints || q.SlowPointCount[i] < q.PointCount[i]) { uncovered++; continue; }
            for (int v = q.VertexStart[i]; v < q.VertexStart[i] + q.VertexCount[i]; v++)
            {
                bool on = false;
                for (int k = 0; k < q.SlowPointCount[i] && !on; k++) on = Vector3.Distance(q.Origin[i] + q.Points[q.PointStart[i] + k], q.Positions[v]) < 1e-5f;
                if (!on) { uncovered++; break; }
            }
        }
        Check(uncovered == 0, $"slow, a piece collides with every one of its vertices (up to 128), the fast points first ({uncovered} pieces not)");
        var again = PopCut.Cut(meshes, new PopCutSettings { Shred = 1f, MaxPieces = 400, Seed = 5 });
        var other = PopCut.Cut(meshes, new PopCutSettings { Shred = 1f, MaxPieces = 400, Seed = 6 });
        bool same = again.Count == q.Count, differs = other.Count != q.Count;
        for (int i = 0; same && i < q.Count; i++) same &= again.Origin[i] == q.Origin[i];
        for (int i = 0; !differs && i < q.Count; i++) differs |= other.Origin[i] != q.Origin[i];
        Check(same && differs, "the same seed cuts the same pieces, another seed others");
    }

    static void PopCutPlayer()
    {
        Console.WriteLine("-- A capsule player (and hair: 400 loose cards)");
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var body = PopFrom(bp, bn, bt);
        // the hair: small quads above the head, none touching another
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
        var hair = PopFrom(hp.ToArray(), hn.ToArray(), ht.ToArray());
        var meshes = new[] { body, hair };
        var big = PopCut.Cut(meshes, new PopCutSettings { Shred = 0f, MaxPieces = 1500, Seed = 1 });
        var small = PopCut.Cut(meshes, new PopCutSettings { Shred = 1f, MaxPieces = 1500, Seed = 1 });
        Console.WriteLine($"   {bt.Length / 3} + {ht.Count / 3} triangles; Shred 0: {big.Count} pieces about {big.Spacing * 100f:0} cm across; Shred 1: {small.Count} pieces about {small.Spacing * 100f:0.0} cm across ({small.Tries} tries)");
        int hairPieces = 0;
        var of = PopPieceOf(big);
        var seen = new HashSet<int>();
        for (int v = 0; v < big.FrontVertices; v++) if (big.Positions[v].Y > 1.81f && seen.Add(of[v])) hairPieces++;
        int bodyPieces = big.Count - hairPieces;
        Check(bodyPieces >= 2 && bodyPieces <= 60, $"Shred 0: the body in a few big pieces ({bodyPieces})");
        Check(hairPieces >= 400 && hairPieces <= 420, $"and each loose hair card flies on its own ({hairPieces} pieces from 400 cards)");
        Check(small.Count > 200 && small.Count <= 1500, $"Shred 1: confetti, no more than MaxPieces ({small.Count} of 1500)");
        Check(big.Submeshes.Count == 2 && small.Submeshes.Count == 2, "a submesh for each source mesh's material, in order");
        int most = 0, fewest = int.MaxValue;
        for (int i = 0; i < big.Count; i++) most = Math.Max(most, big.PointCount[i]);
        for (int i = 0; i < small.Count; i++) if (small.VertexCount[i] >= 8) fewest = Math.Min(fewest, small.PointCount[i]);
        Check(most > 8 && most <= PopCut.MostPointsPerPiece && fewest >= 8, $"big pieces collide with more points (up to {most} at Shred 0), confetti with 8");

        Console.WriteLine("-- Two capsules 1 cm apart");
        var (lp, ln, lt) = CapsuleMesh(0.2f, 1.6f, 0.2f);
        var (rp, rn, rt) = CapsuleMesh(0.2f, 1.6f, 0.2f);
        for (int i = 0; i < lp.Length; i++) lp[i] += new Vector3(-0.205f, 0f, 0f);
        for (int i = 0; i < rp.Length; i++) rp[i] += new Vector3(0.205f, 0f, 0f);
        var two = PopCut.Cut(new[] { PopFrom(lp, ln, lt), PopFrom(rp, rn, rt) }, new PopCutSettings { Shred = 0f, MaxPieces = 1500, Seed = 3 });
        int across = 0;
        for (int i = 0; i < two.Count; i++)
        {
            bool l = false, r = false;
            for (int v = two.VertexStart[i]; v < two.VertexStart[i] + two.VertexCount[i]; v++) { if (two.Positions[v].X < 0f) l = true; else r = true; }
            if (l && r) across++;
        }
        Check(across == 0, $"a piece never takes in parts that do not touch ({two.Count} pieces, {across} across both)");
    }

    /// <summary>One flat piece dropped tilted onto a floor: it lands on a corner, tips over and lies flat, never in the
    /// floor, spinning no faster than a stiff plate can.</summary>
    static void PopLanding()
    {
        Console.WriteLine("-- A 12 cm square flake dropped tilted at 5 m/s onto a floor (90 fps)");
        var (pos, tris, uv) = ClothGrid(4, 4, 0.12f, 0.12f, lying: true);
        var tilt = Quaternion.CreateFromYawPitchRoll(0.3f, 0.6f, 0.35f);
        var nrm = new Vector3[pos.Length];
        for (int i = 0; i < pos.Length; i++) { pos[i] = Vector3.Transform(pos[i], tilt) + new Vector3(0f, 0.3f, 0f); nrm[i] = Vector3.Transform(Vector3.UnitY, tilt); }
        var cut = PopCut.Cut(new[] { PopFrom(pos, nrm, tris, uv) }, new PopCutSettings { Shred = 0f, MaxPieces = 1, Seed = 1 });
        Check(cut.Count == 1, $"one piece ({cut.Count}; {cut.VertexCount[0]} vertices, {cut.PointCount[0]} collision points)");
        var s = new PopSolver(cut) { StopBelow = -50f };
        s.Carry(new Vector3(0f, -5f, 0f));
        var cols = new List<FleshCollider> { FleshCollider.Plane(Vector3.Zero, Vector3.UnitY) };
        float lowest = float.MaxValue, fastest = 0f;
        for (int f = 0; f < 270; f++)
        {
            s.Step(1f / 90f, new Vector3(0f, -9.81f, 0f), cols);
            fastest = MathF.Max(fastest, s.W[0].Length());
            for (int v = 0; v < cut.FrontVertices; v++) lowest = MathF.Min(lowest, s.OutPositions[v].Y);
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int v = 0; v < cut.FrontVertices; v++) { lo = MathF.Min(lo, s.OutPositions[v].Y); hi = MathF.Max(hi, s.OutPositions[v].Y); }
        Console.WriteLine($"   spun at most {fastest:0.0} rad/s; lowest vertex {lowest * 1000f:0.0} mm; after 3 s its vertices {lo * 1000f:0.0} to {hi * 1000f:0.0} mm high, {(s.Awake == 0 ? "asleep" : "awake")}");
        Check(lowest > -0.0005f, $"never in the floor (lowest vertex {lowest * 1000f:0.0} mm)");
        Check(lo > 0.0015f && hi < 0.0045f, $"lies flat on it, 3 mm off ({lo * 1000f:0.0} to {hi * 1000f:0.0} mm)");
        Check(fastest < 100f && s.Awake == 0, $"spins no faster than a stiff plate landing on a corner can ({fastest:0} rad/s), and lies still");
    }

    /// <summary>A capsule player popped over a floor: away from its middle, down on the floor, still.</summary>
    static void PopFlight()
    {
        Console.WriteLine("-- A capsule player pops over a floor (Shred 0.8, Strength 5, Randomness 0.5, 90 fps)");
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var cut = PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 0.8f, MaxPieces = 1500, Seed = 11 });
        var s = new PopSolver(cut) { StopBelow = -50f };
        var center = new Vector3(0f, 0.9f, 0f);
        s.Kick(center, 5f, 0.5f, 11);
        var cols = new List<FleshCollider> { FleshCollider.Plane(Vector3.Zero, Vector3.UnitY) };
        var g = new Vector3(0f, -9.81f, 0f);
        s.Step(1f / 90f, g, cols);
        double outward = 0;
        for (int i = 0; i < cut.Count; i++) outward += Vector3.Dot(s.V[i], Vector3.Normalize(s.X[i] - center + new Vector3(1e-6f)));
        outward /= cut.Count;
        // the plugin gathers the world's shapes in the awake pieces' box (with where they head)
        bool boxed = s.AwakeBounds(0.1f, out var alo, out var ahi);
        for (int i = 0; i < cut.Count && boxed; i++)
            boxed = Vector3.Min(s.X[i], alo) == alo && Vector3.Max(s.X[i], ahi) == ahi;
        float lowest = float.MaxValue, lowSlow = float.MaxValue;
        int frames = 0, worstV = -1, worstFrame = -1;
        double ms = 0;
        var of = PopPieceOf(cut);
        var slow = new bool[cut.Count];
        for (; frames < 90 * 6; frames++)
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
        }
        int up = 0;
        for (int i = 0; i < cut.Count; i++) if (s.X[i].Y > cut.Radius[i] + 0.02f) up++;
        float rest = float.MaxValue;
        for (int v = 0; v < cut.FrontVertices; v++) rest = MathF.Min(rest, s.OutPositions[v].Y);
        Check(rest > 0.002f, $"at rest every vertex lies on the floor, none in it (lowest {rest * 1000f:0.0} mm)");
        s.Step(1f / 90f, g, cols);
        Console.WriteLine($"   {cut.Count} pieces; at first {outward:0.0} m/s outward on average; after 6 s {s.Awake} awake, lowest vertex {lowest * 1000f:0.0} mm, {ms / frames:0.00} ms a frame");
        Check(ClothFiniteArr(s.OutPositions), "finite");
        Check(outward > 2.5f, $"the pieces fly away from the middle ({outward:0.0} m/s)");
        Check(lowest > -0.015f, $"a piece landing fast may dip a corner between its collision points into the floor for a moment, no more than 15 mm ({lowest * 1000f:0.0} mm)");
        Check(lowSlow > -0.001f, $"a piece slower than 1 m/s has none of its vertices in the floor ({lowSlow * 1000f:0.0} mm)");
        if (lowest <= -0.015f || lowSlow <= -0.001f)
            PopWhyLow(cut, () => { var r = new PopSolver(cut) { StopBelow = -50f }; r.Kick(center, 5f, 0.5f, 11); r.Step(1f / 90f, g, cols); return r; }, 1f / 90f, g, cols, worstV, worstFrame);
        Check(up == 0, $"every piece came down ({up} still up)");
        Check(s.Awake <= cut.Count / 20, $"and lies still ({s.Awake} of {cut.Count} awake)");
        Check(!s.Changed, "once all are still, the mesh stops changing (no more uploads)");
        bool emptyAtRest = s.Awake > 0 || !s.AwakeBounds(0.1f, out _, out _);
        Check(boxed && emptyAtRest, "the awake pieces' box (where the plugin gathers the world's shapes) holds every flying piece, and is empty once all lie still");
        // the end of the lifetime: the pieces shrink away about their middles, sleeping ones too (measured on those: a
        // sleeping piece's speed is exactly zero, and it shows where it is)
        float before = 0f, after = 0f;
        for (int v = 0; v < cut.FrontVertices; v++)
            if (s.V[of[v]] == Vector3.Zero) before = MathF.Max(before, Vector3.Distance(s.OutPositions[v], s.X[of[v]]));
        s.Shrink = 0.5f;
        s.Step(1f / 90f, g, cols);
        bool rewrote = s.Changed;
        for (int v = 0; v < cut.FrontVertices; v++)
            if (s.V[of[v]] == Vector3.Zero) after = MathF.Max(after, Vector3.Distance(s.OutPositions[v], s.X[of[v]]));
        Console.WriteLine($"   shrunk to half: the farthest vertex from its piece's middle {before * 1000f:0.0} mm, then {after * 1000f:0.0} mm");
        Check(rewrote && MathF.Abs(after - 0.5f * before) < 0.001f + 0.02f * before, "shrinking at the end of the lifetime rewrites the sleeping pieces too, half the size at Shrink 0.5");
    }

    static bool ClothFiniteArr(Vector3[] a)
    {
        foreach (var v in a) if (!float.IsFinite(v.X + v.Y + v.Z)) return false;
        return true;
    }

    /// <summary>Confetti thrown straight down at 60 m/s (25 cm a step) at thin floors 2 m below.</summary>
    static void PopFloors()
    {
        Console.WriteLine("-- Confetti thrown down at 60 m/s at thin floors 2 m below");
        var (sp, st, su) = ClothGrid(7, 7, 0.3f, 0.3f, lying: true);
        var sn = new Vector3[sp.Length];
        Array.Fill(sn, Vector3.UnitY);
        var cut = PopCut.Cut(new[] { PopFrom(sp, sn, st, su) }, new PopCutSettings { Shred = 1f, MaxPieces = 200, Seed = 2 });
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
            foreach (int fps in new[] { 30, 144 })
            {
                var s = new PopSolver(cut) { StopBelow = -50f };
                s.Carry(new Vector3(0f, -60f, 0f));
                var cols = new List<FleshCollider> { c };
                float lowest = float.MaxValue;
                for (int k = 0; k < fps * 2; k++)
                {
                    s.Step(1f / fps, new Vector3(0f, -9.81f, 0f), cols);
                    for (int v = 0; v < cut.FrontVertices; v++) lowest = MathF.Min(lowest, s.OutPositions[v].Y);
                }
                Console.WriteLine($"   {what}, {fps} fps: lowest vertex {(lowest + 2f) * 1000f:0.0} mm from the floor, {s.Awake} of {cut.Count} awake after 2 s");
                if (s.Awake > 0 && Environment.GetEnvironmentVariable("POP_DEBUG") == "1")
                {
                    int shown = 0;
                    for (int i = 0; i < cut.Count && shown < 3; i++)
                    {
                        if (s.V[i] == Vector3.Zero && s.W[i] == Vector3.Zero) continue;
                        shown++;
                        for (int f = 0; f < 4; f++)
                        {
                            s.Step(1f / fps, new Vector3(0f, -9.81f, 0f), cols);
                            Console.WriteLine($"     piece {i}: {(s.X[i].Y + 2f) * 1000f:0.000} mm up, v {s.V[i].X:0.0000} {s.V[i].Y:0.0000} {s.V[i].Z:0.0000}, w {s.W[i].Length():0.000}, still {s.StillFor(i):0.000} s, contacts {s.Contacts}, steps {s.StepsLastFrame}");
                        }
                    }
                }
                Check(lowest > -2.004f, $"{what}, {fps} fps: no piece goes through ({(lowest + 2f) * 1000f:0.0} mm)");
            }
    }

    /// <summary>Confetti (MaxPieces 1500) over a 40 m mesh floor of 12,800 triangles.</summary>
    static void PopCost()
    {
        Console.WriteLine("-- Confetti (MaxPieces 1500) over a mesh floor of 12,800 triangles (Strength 8, 90 fps)");
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        var cut = PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 1f, MaxPieces = 1500, Seed = 4 });
        var sw = Stopwatch.StartNew();
        cut = PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 1f, MaxPieces = 1500, Seed = 4 });
        double cutMs = sw.Elapsed.TotalMilliseconds;
        var (fp, ft, _) = ClothGrid(81, 81, 40f, 40f, lying: true);
        bool up = Vector3.Cross(fp[ft[1]] - fp[ft[0]], fp[ft[2]] - fp[ft[0]]).Y > 0f;
        var place = up ? Matrix4x4.Identity : Matrix4x4.CreateRotationX(MathF.PI);
        var cols = new List<FleshCollider> { FleshCollider.MeshShape(TriangleBvh.Build(fp, ft), place, true) };
        var s = new PopSolver(cut) { StopBelow = -50f };
        s.Kick(new Vector3(0f, 0.9f, 0f), 8f, 0.5f, 4);
        double ms = 0, worst = 0, pairs = 0;
        float lowest = float.MaxValue;
        int frames = 270, worstV = -1, worstFrame = -1;
        var g = new Vector3(0f, -9.81f, 0f);
        for (int k = 0; k < frames; k++)
        {
            s.Step(1f / 90f, g, cols);
            ms += s.LastStepMs; worst = Math.Max(worst, s.LastStepMs); pairs = Math.Max(pairs, s.PairCount);
            for (int v = 0; v < cut.FrontVertices; v++) if (s.OutPositions[v].Y < lowest) { lowest = s.OutPositions[v].Y; worstV = v; worstFrame = k; }
        }
        Console.WriteLine($"   cut in {cutMs:0.0} ms ({cut.Count} pieces, {cut.FrontVertices * 2} vertices with the back faces); {ms / frames:0.00} ms a frame (worst {worst:0.00}), at most {pairs:0} pairs, {s.Awake} awake after 3 s");
        Check(lowest > -0.004f, $"none goes through the mesh floor ({lowest * 1000f:0.0} mm)");
        if (lowest <= -0.004f)
            PopWhyLow(cut, () => { var r = new PopSolver(cut) { StopBelow = -50f }; r.Kick(new Vector3(0f, 0.9f, 0f), 8f, 0.5f, 4); return r; }, 1f / 90f, g, cols, worstV, worstFrame);
        Check(ms / frames < 6.0, $"a frame takes {ms / frames:0.00} ms on average (under 6)");
    }

    /// <summary>Plays a run again up to the frame its lowest vertex was seen in, and shows that vertex's piece.</summary>
    static void PopWhyLow(PopPieces cut, Func<PopSolver> make, float dt, Vector3 g, List<FleshCollider> cols, int v, int frame)
    {
        var s = make();
        int i = PopPieceOf(cut)[v];
        Console.WriteLine($"   why: vertex {v} of piece {i} ({cut.VertexCount[i]} vertices, {cut.PointCount[i]} points, radius {cut.Radius[i] * 100f:0.0} cm), lowest in frame {frame}");
        for (int f = 0; f <= frame; f++)
        {
            s.Step(dt, g, cols);
            if (f < frame - 5) continue;
            var q = s.Q[i];
            float pmin = float.MaxValue, vmin = float.MaxValue;
            for (int k = 0; k < cut.PointCount[i]; k++) pmin = MathF.Min(pmin, (s.X[i] + Vector3.Transform(cut.Points[cut.PointStart[i] + k], q)).Y);
            for (int u = cut.VertexStart[i]; u < cut.VertexStart[i] + cut.VertexCount[i]; u++) vmin = MathF.Min(vmin, s.OutPositions[u].Y);
            Console.WriteLine($"     frame {f}: the vertex {s.OutPositions[v].Y * 1000f:0.0} mm, the piece's lowest vertex {vmin * 1000f:0.0} mm, its lowest point {pmin * 1000f:0.0} mm (after the step), at {s.X[i].Y * 1000f:0.0} mm high, {s.V[i].Length():0.00} m/s, {s.W[i].Length():0.0} rad/s, {s.StepsLastFrame} steps, {s.Contacts} contacts");
        }
    }
}
