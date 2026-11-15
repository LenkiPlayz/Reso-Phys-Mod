using System.Diagnostics;
using System.Numerics;
using Elements.Core;
using Softbody.Core.Cloth;

// 0.31.0: the Cloth Blast (DESIGN-cloth.md, "Cloth Blast"): cuts, the shred (pieces and confetti, the same on every client),
// the points a blast frees, and its push. Run with:  dotnet run -c Release --project tests -- cloth-blast  (or "cloth").
namespace Softbody.Tests;

static partial class Program
{
    static void ClothBlastTests()
    {
        Console.WriteLine("== Cloth Blast (0.31.0)");
        BlastCutLine();
        BlastSpacing();
        BlastConfetti();
        BlastChunks();
        BlastReplayInterleaved();
        BlastSeamsAndSides();
        BlastRoom();
        BlastRelease();
        BlastKick();
        BlastAvatarKick();
        BlastTearList();
        BlastBigConfetti();
    }

    /// <summary>Pieces: triangles that share a particle move together (union over each particle's fan).</summary>
    static int BlastPieces(ClothMesh m, out int[] pieceOf)
    {
        int nt = m.TriangleCount;
        var parent = new int[nt];
        for (int i = 0; i < nt; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (int p = 0; p < m.ParticleCount; p++)
        {
            var fan = m.Fan(p);
            for (int k = 1; k < fan.Count; k++)
            {
                int a = Find(fan[0]), b = Find(fan[k]);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }
        pieceOf = new int[nt];
        var roots = new HashSet<int>();
        for (int t = 0; t < nt; t++) { pieceOf[t] = Find(t); roots.Add(pieceOf[t]); }
        return roots.Count;
    }

    /// <summary>The pairs of the lying grid's triangles whose rest centres are on either side of z = <paramref name="z"/>
    /// (and between x0 and x1).</summary>
    static List<(int t, int u)> BlastPairsAcross(ClothMesh m, float z, float x0 = float.MinValue, float x1 = float.MaxValue)
    {
        var c = m.RestCentroids;
        var list = new List<(int t, int u)>();
        foreach (var (t, u) in m.OriginalNeighbours)
        {
            bool across = (c[t].Z < z) != (c[u].Z < z);
            float x = 0.5f * (c[t].X + c[u].X);
            if (across && x > x0 && x < x1) list.Add((t, u));
        }
        return list;
    }

    static void BlastCutLine()
    {
        Console.WriteLine("-- cuts: a line of them across the cloth separates it; one that ends inside leaves a slit");
        var (pos, tris, _) = ClothGrid(11, 11, 1f, 1f, lying: true);
        var mesh = new ClothMesh(pos, tris, 1f);
        Check(mesh.OriginalNeighbours.Count == 3 * 10 * 10 - 2 * 10, $"the grid's triangle pairs that share an edge: {mesh.OriginalNeighbours.Count} (280)");
        Check(MathF.Abs(mesh.MeanEdge - (0.1f * (2f + MathF.Sqrt(2f)) / 3f)) < 1e-4f, $"its mean edge on the mesh as built: {mesh.MeanEdge:0.0000} m");
        // the whole line z = 0 (a row of vertices): every pair across it
        var line = BlastPairsAcross(mesh, 0f);
        int made = 0;
        foreach (var (t, u) in line) if (mesh.Cut(t, u)) made++;
        int pieces = BlastPieces(mesh, out _);
        Console.WriteLine($"   {line.Count} pairs across z = 0, {made} cut: {mesh.ParticleCount} particles (from 121), {pieces} pieces");
        Check(made == line.Count && line.Count == 10, "every pair across the line is cut once (10)");
        Check(pieces == 2, $"the sheet is in two halves ({pieces} pieces)");
        Check(mesh.ParticleCount == 121 + 11, $"the line's 11 points are doubled ({mesh.ParticleCount} particles)");
        Check(mesh.Validate() == null, "valid: " + (mesh.Validate() ?? "ok"));
        bool again = false;
        foreach (var (t, u) in line) again |= mesh.Cut(t, u);
        Check(!again, "cutting the same pair again does nothing");

        // a slit: 4 edges in the middle of the line, ends inside the sheet
        var slitMesh = new ClothMesh(pos, tris, 1f);
        var slit = BlastPairsAcross(slitMesh, 0f, -0.2f, 0.2f);
        foreach (var (t, u) in slit) slitMesh.Cut(t, u);
        int slitPieces = BlastPieces(slitMesh, out _);
        Check(slit.Count == 4 && slitMesh.ParticleCount == 121 + 3 && slitPieces == 1,
              $"a slit of 4 edges opens at its 3 inner points and stays one piece ({slitMesh.ParticleCount} particles, {slitPieces} piece)");
        Check(slitMesh.Validate() == null, "valid: " + (slitMesh.Validate() ?? "ok"));
        var lone = new ClothMesh(pos, tris, 1f);
        var one = BlastPairsAcross(lone, 0f, 0.01f, 0.09f);
        bool cut = one.Count == 1 && lone.Cut(one[0].t, one[0].u);
        Check(cut && lone.ParticleCount == 121 && lone.Version == 0, "a single cut inside the sheet records the cut but opens nothing yet");
        Check(!lone.Cut(0, 99), "two triangles that share no edge cannot be cut apart");
    }

    static void BlastSpacing()
    {
        Console.WriteLine("-- the pieces' size from Shred: the radius at 0, the mean edge at 1, in steps of 1.25");
        float e = 0.02f, r = 1f;
        float s0 = ClothBlastMath.Spacing(e, r, 0f), s5 = ClothBlastMath.Spacing(e, r, 0.5f), s9 = ClothBlastMath.Spacing(e, r, 0.9f), s1 = ClothBlastMath.Spacing(e, r, 1f);
        Console.WriteLine($"   mean edge 2 cm, radius 1 m: Shred 0 {s0:0.000} m, 0.5 {s5:0.000} m, 0.9 {s9:0.000} m, 1 {s1:0.000} m");
        Check(s0 <= r && s0 > r / 1.25f, "Shred 0: about the radius");
        Check(s1 == e, "Shred 1: the mean edge");
        Check(s0 > s5 && s5 > s9 && s9 >= s1 && MathF.Abs(s5 - MathF.Sqrt(e * r)) < 0.35f * MathF.Sqrt(e * r), "smaller as Shred rises; at 0.5 about the geometric middle");
        Check(ClothBlastMath.Spacing(0f, r, 0.5f) == 0f && ClothBlastMath.Spacing(e, float.NaN, 0.5f) == 0f, "nothing for a mesh with no edges or a radius that is not finite");
    }

    static void BlastConfetti()
    {
        Console.WriteLine("-- Shred 1: confetti, every triangle on its own (with room for it)");
        var (pos, tris, _) = ClothGrid(9, 9, 1f, 1f, lying: true);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var solver = new ClothSolver(mesh, new ClothSettings());
        var shredder = new ClothShredder();
        var shot = new ClothShred { Center = Vector3.Zero, Radius = 2f, Shred = 1f, Seed = 7 };
        int parts = shredder.PartCount(mesh, 7, shot), cuts = 0;
        for (int p = 0; p < parts; p++) cuts += shredder.Apply(solver, 7, shot, p);
        int pieces = BlastPieces(mesh, out _);
        Console.WriteLine($"   {cuts} cuts in {parts} part(s): {mesh.ParticleCount} particles of {mesh.MaxParticles}, {pieces} pieces, {mesh.TriangleCount} triangles");
        Check(cuts == mesh.OriginalNeighbours.Count, $"every pair is cut ({cuts} of {mesh.OriginalNeighbours.Count})");
        Check(pieces == mesh.TriangleCount && mesh.ParticleCount == 3 * mesh.TriangleCount, "every triangle is a piece of its own, with its own three points");
        Check(mesh.Validate() == null, "valid: " + (mesh.Validate() ?? "ok"));
        Check(mesh.MaxParticles == 81 * 6, $"room: six times the points ({mesh.MaxParticles})");
        // the solver carries on: every new point starts where its parent was, and it steps
        var f = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 30; k++) solver.Step(f);
        Check(ClothFinite(solver) && solver.Free == mesh.ParticleCount, $"it steps (all {solver.Free} points free, finite)");
        Check(solver.BendConstraints == 0 && solver.StretchConstraints == 3 * mesh.TriangleCount, $"no bending left between the pieces, 3 edges each ({solver.StretchConstraints})");
    }

    static void BlastChunks()
    {
        Console.WriteLine("-- Shred 0.5: pieces about sqrt(radius x edge) across inside the radius; the rest stays whole");
        var (pos, tris, _) = ClothGrid(41, 41, 1f, 1f, lying: true);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var solver = new ClothSolver(mesh, new ClothSettings());
        var shot = new ClothShred { Center = new Vector3(0.05f, 0f, -0.03f), Radius = 0.3f, Shred = 0.5f, Seed = 123456 };
        var first = new List<(int t, int u)>();
        var second = new List<(int t, int u)>();
        ClothBlastMath.ShredCuts(mesh, shot, first);
        ClothBlastMath.ShredCuts(new ClothMesh(pos, tris, ClothMesh.MaxGrowth), shot, second);
        Check(first.Count > 0 && first.SequenceEqual(second), $"the same cuts in the same order every time ({first.Count} cuts)");
        // nearest first
        var c = mesh.RestCentroids.ToArray();
        float D(int t) { var d = c[t] - shot.Center; return d.LengthSquared(); }
        bool ordered = true;
        for (int i = 1; i < first.Count; i++)
            if (MathF.Min(D(first[i].t), D(first[i].u)) < MathF.Min(D(first[i - 1].t), D(first[i - 1].u))) { ordered = false; break; }
        Check(ordered, "nearest the centre first");
        var shredder = new ClothShredder();
        int parts = shredder.PartCount(mesh, 1, shot), cuts = 0;
        string bad = null;
        for (int p = 0; p < parts; p++) { cuts += shredder.Apply(solver, 1, shot, p); bad ??= mesh.Validate(); }
        int pieces = BlastPieces(mesh, out var pieceOf);
        // the outer cloth: the piece of a corner triangle
        int outer = pieceOf[0], outerSize = 0, centerOut = 0, centerTris = 0;
        var sizes = new Dictionary<int, int>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            sizes[pieceOf[t]] = sizes.GetValueOrDefault(pieceOf[t]) + 1;
            if (pieceOf[t] == outer) outerSize++;
            if ((c[t] - shot.Center).Length() < 0.15f) { centerTris++; if (pieceOf[t] != outer) centerOut++; }
        }
        int loose = sizes.Count - 1;
        double mean = loose > 0 ? sizes.Where(kv => kv.Key != outer).Average(kv => kv.Value) : 0;
        float spacing = ClothBlastMath.Spacing(mesh.MeanEdge, shot.Radius, shot.Shred);
        Console.WriteLine($"   spacing {spacing:0.000} m: {cuts} cuts in {parts} parts, {pieces} pieces ({loose} loose, {mean:0.0} triangles each on average), " +
                          $"the rest {outerSize} of {mesh.TriangleCount} triangles; {mesh.ParticleCount} particles of {mesh.MaxParticles}");
        Check(bad == null, "valid after every part: " + (bad ?? "ok"));
        Check(loose >= 8 && mean > 4, $"it comes apart into pieces, not confetti ({loose} pieces of {mean:0.0} triangles on average)");
        Check(centerOut == centerTris, $"near the centre nothing is still joined to the rest ({centerOut} of {centerTris} triangles loose)");
        Check(outerSize > mesh.TriangleCount * 0.6, "far from it the cloth is whole");
    }

    static void BlastReplayInterleaved()
    {
        Console.WriteLine("-- a client replaying the tear list (splits and shred parts mixed) gets the same mesh, index for index");
        var (pos, tris, _) = ClothGrid(31, 31, 1f, 1f, lying: true);
        var a = new ClothSolver(new ClothMesh(pos, tris, ClothMesh.MaxGrowth), new ClothSettings());
        var b = new ClothSolver(new ClothMesh(pos, tris, ClothMesh.MaxGrowth), new ClothSettings());
        var sa = new ClothShredder();
        var sb = new ClothShredder();
        var shot1 = new ClothShred { Center = new Vector3(-0.2f, 0f, 0.1f), Radius = 0.25f, Shred = 0.7f, Seed = 99 };
        var shot2 = new ClothShred { Center = new Vector3(0.25f, 0f, -0.2f), Radius = 0.2f, Shred = 1f, Seed = 100 };
        var log = new List<(int x, int y)>();
        var rng = new Random(5);
        // the deciding client: splits here and there between the shred parts
        void RandomSplit()
        {
            for (int tries = 0; tries < 50; tries++)
            {
                int p = rng.Next(a.Mesh.ParticleCount);
                var fan = a.Mesh.Fan(p);
                if (fan.Count == 0) continue;
                int t = fan[rng.Next(fan.Count)], o = a.Mesh.Triangles[3 * t + rng.Next(3)];
                if (o != p && a.Split(p, o)) { log.Add((p, o)); return; }
            }
        }
        int p1 = sa.PartCount(a.Mesh, 99, shot1), p2 = sa.PartCount(a.Mesh, 100, shot2);
        for (int k = 0; k < Math.Max(p1, p2); k++)
        {
            RandomSplit();
            if (k < p1) { sa.Apply(a, 99, shot1, k); log.Add((-99, k)); }
            RandomSplit();
            if (k < p2) { sa.Apply(a, 100, shot2, k); log.Add((-100, k)); }
        }
        RandomSplit();
        // the other client
        int skipped = 0;
        foreach (var (x, y) in log)
        {
            if (ClothBlastMath.IsShredEntry(x, y)) sb.Apply(b, -x, -x == 99 ? shot1 : shot2, y);
            else if (!b.Split(x, y)) skipped++;
        }
        Console.WriteLine($"   {log.Count} entries ({log.Count(e => e.x < 0)} shred parts): {a.Mesh.ParticleCount} particles, {a.Mesh.Cuts} cuts, {a.Mesh.Splits} splits");
        Check(skipped == 0 && ClothSameTopology(a.Mesh, b.Mesh), "the replay is the same mesh");
        Check(a.Mesh.Validate() == null, "valid: " + (a.Mesh.Validate() ?? "ok"));
        Check(!ClothBlastMath.IsShredEntry(3, 5) && !ClothBlastMath.IsShredEntry(0, 0) && ClothBlastMath.IsShredEntry(-ClothBlastMath.MaxId, 7),
              "a split (both 0 or more) is never read as a shred part (-id, part)");
    }

    static void BlastSeamsAndSides()
    {
        Console.WriteLine("-- a seamed, double-sided sheet: both sides come apart together, and it replays");
        var (pos, tris, _) = ClothGrid(21, 21, 1f, 1f, lying: true, seam: true, doubleSided: true);
        var shot = new ClothShred { Center = new Vector3(0.02f, 0f, 0.01f), Radius = 0.3f, Shred = 0.8f, Seed = 4242 };
        ClothMesh Run()
        {
            var m = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
            var s = new ClothSolver(m, new ClothSettings());
            var sh = new ClothShredder();
            int parts = sh.PartCount(m, 1, shot);
            for (int p = 0; p < parts; p++) sh.Apply(s, 1, shot, p);
            return m;
        }
        var x = Run(); var y = Run();
        int pieces = BlastPieces(x, out _);
        // every render triangle's corners are on its simulated triangle's points (Validate), front and back alike
        Console.WriteLine($"   {x.Cuts} cuts: {x.ParticleCount} particles, {x.RenderVertexCount} render vertices (from {pos.Length}), {pieces} pieces");
        Check(x.Validate() == null, "valid: " + (x.Validate() ?? "ok"));
        Check(pieces > 3, "it comes apart");
        Check(ClothSameTopology(x, y), "the same mesh on another client");
    }

    static void BlastRoom()
    {
        Console.WriteLine("-- no room: the shred stops where the room runs out (points, or an avatar's spare vertices), still valid");
        var (pos, tris, _) = ClothGrid(15, 15, 1f, 1f, lying: true);
        var mesh = new ClothMesh(pos, tris);             // the 0.29.0 room: a quarter more
        var solver = new ClothSolver(mesh, new ClothSettings());
        var sh = new ClothShredder();
        var shot = new ClothShred { Center = Vector3.Zero, Radius = 2f, Shred = 1f, Seed = 3 };
        int parts = sh.PartCount(mesh, 3, shot), cuts = 0;
        for (int p = 0; p < parts; p++) cuts += sh.Apply(solver, 3, shot, p);
        Console.WriteLine($"   {cuts} of {sh.CutsOf(mesh, 3, shot).Count} cuts made: {mesh.ParticleCount} of {mesh.MaxParticles} particles");
        Check(mesh.Full && mesh.ParticleCount == mesh.MaxParticles, "it fills the room and stops");
        Check(mesh.Validate() == null, "valid: " + (mesh.Validate() ?? "ok"));
        var m2 = new ClothMesh(pos, tris, ClothMesh.MaxGrowth) { MaxRenderVertices = pos.Length + 40 };
        var s2 = new ClothSolver(m2, new ClothSettings());
        var sh2 = new ClothShredder();
        int parts2 = sh2.PartCount(m2, 3, shot);
        for (int p = 0; p < parts2; p++) sh2.Apply(s2, 3, shot, p);
        Check(m2.RenderVertexCount <= pos.Length + 40 && m2.Validate() == null, $"with 40 spare vertices it stops at {m2.RenderVertexCount - pos.Length}, valid");
    }

    static void BlastRelease()
    {
        Console.WriteLine("-- Release: the held points within the radius (as modelled) are free from then on");
        var (pos, tris, _) = ClothGrid(21, 21, 1f, 1f);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s = new ClothSolver(mesh, new ClothSettings());
        // the top row pinned by the paint, the left column on an anchor
        s.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
        int n = mesh.ParticleCount;
        var anchorOf = new int[n]; var local = new Vector3[n];
        for (int i = 0; i < n; i++) { var p = mesh.RestPositions[i]; anchorOf[i] = p.X < -0.5f + 1e-4f ? 0 : -1; local[i] = p; }
        s.SetAnchors(anchorOf, local);
        var f = ClothFrameAt(1f / 60f);
        f.AnchorToWorld = new[] { Matrix4x4.Identity };
        f.AnchorValid = new[] { true };
        for (int k = 0; k < 30; k++) s.Step(f);
        int pinnedBefore = s.Pinned, anchoredBefore = s.Anchored;
        int At(float x, float y)
        {
            for (int i = 0; i < n; i++) { var p = mesh.RestPositions[i]; if (MathF.Abs(p.X - x) < 1e-4f && MathF.Abs(p.Y - y) < 1e-4f) return i; }
            return -1;
        }
        int corner = At(0.5f, 0f), freed = At(0.35f, 0f), held = At(0f, 0f), left = At(-0.5f, -1f), leftHeld = At(-0.5f, -0.5f);
        // a blast at the top right corner (radius 0.32: the top row from x = 0.2 on) and one at the bottom left one
        // (radius 0.27: the left column up to y = -0.75); no grid point is near either sphere's edge
        s.SetReleases(new[] { new Vector4(0.5f, 0f, 0f, 0.32f), new Vector4(-0.5f, -1f, 0f, 0.27f) });
        Check(s.Kind(corner) == 0 && s.Kind(freed) == 0 && s.Kind(left) == 0 && s.Kind(held) == 1 && s.Kind(leftHeld) == 2,
              "the top right and the anchored bottom left are free; the top middle is still pinned, the left side further up still anchored");
        Console.WriteLine($"   {s.Released} points let go: pinned {pinnedBefore} -> {s.Pinned}, anchored {anchoredBefore} -> {s.Anchored}");
        Check(s.Released == 13 && s.Pinned == pinnedBefore - 7 && s.Anchored == anchoredBefore - 6,
              "7 pinned points and 6 anchored ones (13; the points in the spheres that were free already are not counted)");
        var cornerRest = mesh.RestPositions[corner];
        for (int k = 0; k < 60; k++) s.Step(f);
        Console.WriteLine($"   a second later the freed corner is {Vector3.Distance(s.X[corner], cornerRest) * 1000f:0.0} mm from its place " +
                          "(this sheet hangs in its own plane, so it can only shear down a little)");
        Check(Vector3.Distance(s.X[held], mesh.RestPositions[held]) < 1e-5f, "the still pinned top middle stays put");
        // a tear at a freed point makes freed points
        bool split = false;
        foreach (int t in mesh.Fan(freed).ToArray())
        {
            for (int j = 0; j < 3 && !split; j++) { int q = mesh.Triangles[3 * t + j]; if (q != freed) split = s.Split(freed, q); }
            if (split) break;
        }
        int made = mesh.LastNewParticles.Count;
        bool allFree = split && made > 0;
        foreach (int q in mesh.LastNewParticles) allFree &= s.IsReleased(q) && s.Kind(q) == 0;
        Check(allFree, $"the points a tear makes from a freed one are free too ({made} new)");
        s.SetReleases(ReadOnlySpan<Vector4>.Empty);
        Check(s.Kind(corner) == 1 && s.Kind(left) == 2 && s.Released == 0, "an empty list holds them all again (as the rebuild after a Mend does)");
        Check(ClothFinite(s), "finite");

        // MaxDistance no longer holds a freed point near its place: a lying sheet held within 2 cm, its right part freed.
        // A huge sphere, so the fold line is straight (x between 0 and 0.05): cloth cannot fold along a curved line.
        var (lp, lt, _) = ClothGrid(21, 21, 1f, 1f, lying: true);
        var lm = new ClothMesh(lp, lt, ClothMesh.MaxGrowth);
        var ls = new ClothSolver(lm, new ClothSettings { MaxDistance = 0.02f });
        ls.SetReleases(new[] { new Vector4(100.025f, 0f, 0f, 100f) });
        var lf = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 120; k++) ls.Step(lf);
        int edge = -1, inner = -1, freedPoints = 0;
        for (int i = 0; i < lm.ParticleCount; i++)
        {
            if (ls.IsReleased(i)) freedPoints++;
            var p = lm.RestPositions[i];
            if (MathF.Abs(p.Z) > 1e-4f) continue;
            if (MathF.Abs(p.X - 0.5f) < 1e-4f) edge = i;
            if (MathF.Abs(p.X + 0.25f) < 1e-4f) inner = i;
        }
        Console.WriteLine($"   a lying sheet held within 2 cm, {freedPoints} points freed: its freed edge hangs to {ls.X[edge].Y:0.000} m, " +
                          $"a held point is at {ls.X[inner].Y:0.0000} m");
        Check(freedPoints == 10 * 21, "the 10 columns from x = 0.05 on are freed (210 points)");
        Check(ls.X[edge].Y < -0.1f && ls.X[inner].Y > -0.0201f, "the freed part hangs down past MaxDistance; the rest stays within it");
        Check(ClothFinite(ls), "finite");
    }

    static void BlastKick()
    {
        Console.WriteLine("-- the push: outward from the centre, strongest there, nothing past the radius; each kick once");
        var (pos, tris, _) = ClothGrid(31, 31, 1f, 1f, lying: true);
        ClothSolver Sheet()
        {
            var m = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
            var sv = new ClothSolver(m, new ClothSettings { Gravity = 0f, Drag = 0f, Lift = 0f });
            sv.Step(ClothFrameAt(1f / 60f));
            return sv;
        }
        var s = Sheet();
        int n = s.Mesh.ParticleCount, mid = -1, corner = -1;
        for (int i = 0; i < n; i++)
        {
            var p = s.Mesh.RestPositions[i];
            if (MathF.Abs(p.X) < 1e-4f && MathF.Abs(p.Z) < 1e-4f) mid = i;
            if (MathF.Abs(p.X - 0.5f) < 1e-4f && MathF.Abs(p.Z - 0.5f) < 1e-4f) corner = i;
        }
        var f = ClothFrameAt(1f / 60f);
        var kick = new ClothKick { Center = new Vector3(0f, -0.1f, 0f), Radius = 0.5f, Speed = 8f, Seed = 1 };
        // the momentum the kick gives, less a frame of damping (the edges share it out over the sheet; XPBD keeps it)
        float given = 0f, mass = 0f;
        for (int i = 0; i < n; i++) { given += s.Mass(i) * ClothBlastMath.Kick(kick, s.X[i]).Y; mass += s.Mass(i); }
        float damp = MathF.Exp(-new ClothSettings().DampingRate / 60f);
        float Up(ClothSolver sv) { float m = 0f; for (int i = 0; i < n; i++) m += sv.Mass(i) * sv.V[i].Y; return m; }
        f.AddKick(kick);
        s.Step(f);
        float up1 = Up(s), vMid = s.V[mid].Y, vCorner = s.V[corner].Length();
        Console.WriteLine($"   after one frame: momentum up {up1:0.0000} kg m/s (the kick gave {given:0.0000}; the sheet weighs {mass:0.000} kg); " +
                          $"the middle rises at {vMid:0.00} m/s, the corner (0.71 m out) moves at {vCorner:0.000} m/s");
        Check(f.KickCount == 0 && s.Kicks == 1, "the step used the kick up (KickCount back to 0)");
        Check(given > 0f && MathF.Abs(up1 - given * damp) < 0.03f * given, "the sheet gets the kick's momentum, upward");
        Check(vMid > 1f && vCorner < 0.15f * vMid, "the middle rises most; the corner, past the radius, barely moves");
        s.Step(f);
        float up2 = Up(s);
        Check(MathF.Abs(up2 - up1 * damp) < 0.03f * up1, $"no second push without a new kick (momentum {up2:0.0000})");

        // pulled in (Strength below 0), and uneven (Randomness), still finite
        var s2 = Sheet();
        var f2 = ClothFrameAt(1f / 60f);
        f2.AddKick(new ClothKick { Center = new Vector3(0f, -0.1f, 0f), Radius = 0.5f, Speed = -8f, Seed = 1 });
        s2.Step(f2);
        Check(MathF.Abs(Up(s2) + up1) < 0.03f * up1 && s2.V[mid].Y < 0f, $"a negative Strength pulls in (the middle at {s2.V[mid].Y:0.00} m/s)");
        var s3 = Sheet();
        var f3 = ClothFrameAt(1f / 60f);
        f3.AddKick(new ClothKick { Center = new Vector3(0f, -0.1f, 0f), Radius = 0.5f, Speed = 8f, Randomness = 1f, Seed = 77 });
        s3.Step(f3);
        float diff = 0f, sum = 0f;
        for (int i = 0; i < n; i++) { diff += (s3.V[i] - s.V[i]).Length(); sum += s3.V[i].Y; }
        Check(ClothFinite(s3) && diff > 1f && sum > 0f, $"Randomness 1 changes it (by {diff:0.0} m/s summed) and it still goes up overall");
        // held points take no push
        var s4 = new ClothSolver(new ClothMesh(pos, tris), new ClothSettings { Gravity = 0f });
        s4.SetPaint(ClothPaint(pos, _ => true));
        var f4 = ClothFrameAt(1f / 60f);
        f4.AddKick(kick);
        s4.Step(f4);
        Check(ClothMaxSpeedAll(s4) == 0f, "a pinned sheet does not move");
        // the field itself
        var at = ClothBlastMath.Kick(kick, new Vector3(0f, 0.15f, 0f));
        var past = ClothBlastMath.Kick(kick, new Vector3(0f, 0.41f, 0f));
        Check(at.Y > 0f && MathF.Abs(at.X) + MathF.Abs(at.Z) < 1e-5f && past == Vector3.Zero, "outward along the line from the centre; zero past the radius");
    }

    static float ClothMaxSpeedAll(ClothSolver s)
    {
        float m = 0f;
        for (int i = 0; i < s.Mesh.ParticleCount; i++) m = MathF.Max(m, s.V[i].Length());
        return m;
    }

    static void BlastAvatarKick()
    {
        Console.WriteLine("-- avatar cloth: a blast from the side shakes the free skirt, and MaxDistance still holds it near the body");
        var k = MakeSkirt();
        var rig = MakeAvatar(k, new ClothSettings { MaxDistance = 0.15f, Backstop = 0.02f });
        var pose = SkirtPose(k, float4x4.Identity);
        for (int i = 0; i < 30; i++) rig.Step(pose);
        int n = rig.Mesh.ParticleCount;
        // the skirt's middle: the mean of its targets
        var mid = Vector3.Zero;
        for (int i = 0; i < n; i++) mid += rig.Target(i);
        mid /= n;
        float before = 0f;
        for (int i = 0; i < n; i++) if (rig.Solver.Kind(i) == 0) before = MathF.Max(before, Vector3.Distance(rig.Solver.X[i], rig.Target(i)));
        // from the side. A skirt held at the waist cannot fly off as a whole (its edges neither stretch nor shear, and the
        // side facing the blast meets the backstop), so the push mostly crumples it: which way a free sheet goes is
        // BlastKick's job, this one checks that the push gets to avatar cloth and that MaxDistance still holds.
        rig.F.AddKick(new ClothKick { Center = mid - new Vector3(0.6f, 0f, 0f), Radius = 1.5f, Speed = 10f, Seed = 2 });
        rig.Step(pose);
        float fastest = 0f;
        for (int i = 0; i < n; i++) if (rig.Solver.Kind(i) == 0) fastest = MathF.Max(fastest, rig.Solver.V[i].Length());
        float most = 0f;
        for (int f = 0; f < 20; f++)
        {
            rig.Step(pose);
            for (int i = 0; i < n; i++) if (rig.Solver.Kind(i) == 0) most = MathF.Max(most, Vector3.Distance(rig.Solver.X[i], rig.Target(i)));
        }
        Console.WriteLine($"   the fastest free point right after: {fastest:0.00} m/s; furthest from its place over the next 20 frames {most * 100f:0.0} cm (before {before * 100f:0.0} cm)");
        Check(rig.Solver.Kicks == 1 && fastest > 1f, "the push gets to the free skirt");
        Check(most <= 0.15f + 1e-3f && most > before + 0.02f, "out to MaxDistance (15 cm), no further");
        Check(ClothFinite(rig.Solver), "finite");
    }

    static void BlastTearList()
    {
        Console.WriteLine("-- the tear list entries: a shred part is (-id, part), never mistaken for a split; ids fit");
        Check(ClothBlastMath.IsShredEntry(-1, 0) && ClothBlastMath.IsShredEntry(-ClothBlastMath.MaxId, 1000), "(-1, 0) and (-2^30, 1000) are shred parts");
        Check(!ClothBlastMath.IsShredEntry(0, 0) && !ClothBlastMath.IsShredEntry(5, 2) && !ClothBlastMath.IsShredEntry(-3, -1), "(0, 0) (not filled in), a split and (-3, -1) are not");
        Check(ClothBlastMath.PartCount(0) == 0 && ClothBlastMath.PartCount(1) == 1 && ClothBlastMath.PartCount(ClothBlastMath.CutsPerPart) == 1 &&
              ClothBlastMath.PartCount(ClothBlastMath.CutsPerPart + 1) == 2, $"parts of {ClothBlastMath.CutsPerPart} cuts");
    }

    static void BlastBigConfetti()
    {
        Console.WriteLine("-- a big sheet to confetti: how long each part takes on the world thread (one part a frame)");
        var (pos, tris, _) = ClothGrid(51, 51, 1.5f, 1.5f, lying: true);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth, 24000);
        var solver = new ClothSolver(mesh, new ClothSettings());
        var sh = new ClothShredder();
        var shot = new ClothShred { Center = Vector3.Zero, Radius = 3f, Shred = 1f, Seed = 11 };
        var sw = Stopwatch.StartNew();
        int parts = sh.PartCount(mesh, 11, shot);
        double listMs = sw.Elapsed.TotalMilliseconds, worst = 0;
        int cuts = 0;
        for (int p = 0; p < parts; p++)
        {
            long t0 = Stopwatch.GetTimestamp();
            cuts += sh.Apply(solver, 11, shot, p);
            worst = Math.Max(worst, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
        long tc = Stopwatch.GetTimestamp();
        solver.Step(ClothFrameAt(1f / 60f));
        double stepMs = (Stopwatch.GetTimestamp() - tc) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"   {mesh.OriginalParticleCount} points, {mesh.TriangleCount} triangles: the cut list {listMs:0.0} ms, {parts} parts, the slowest {worst:0.0} ms; " +
                          $"{cuts} cuts, {mesh.ParticleCount} particles; the first step after {stepMs:0.0} ms (rebuilds the edges)");
        Check(mesh.ParticleCount == 3 * mesh.TriangleCount && mesh.Validate() == null, "all confetti, valid");
        Check(worst < 50.0, "no part takes long (under 50 ms even here)");
    }
}
