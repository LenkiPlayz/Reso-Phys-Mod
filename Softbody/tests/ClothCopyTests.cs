using System.Numerics;
using Softbody.Core.Cloth;

// 0.33.0: what a Mend leaves behind with DuplicateOnMend (DESIGN-cloth.md, "Built in 0.33.0"): the loose pieces (no pinned
// or anchored point holds them), with the held ones hidden. Run with:  dotnet run -c Release --project tests -- cloth-copy
// (or "cloth").
namespace Softbody.Tests;

static partial class Program
{
    static void ClothCopyTests()
    {
        Console.WriteLine("== Cloth copies (0.33.0)");
        CopyCutAcross();
        CopyHeldAndFree();
        CopyBlastShreds();
        CopyHideTriangles();
    }

    /// <summary>The pairs of the hanging grid's triangles whose rest centres are on either side of y = <paramref name="y"/>.</summary>
    static List<(int t, int u)> CopyPairsAcrossY(ClothMesh m, float y)
    {
        var c = m.RestCentroids;
        var list = new List<(int t, int u)>();
        foreach (var (t, u) in m.OriginalNeighbours)
            if ((c[t].Y < y) != (c[u].Y < y)) list.Add((t, u));
        return list;
    }

    /// <summary>A hanging 21 x 21 sheet pinned at its top row, cut across at y = -0.5.</summary>
    static (ClothMesh mesh, ClothSolver solver, int cuts) CopyCutSheet()
    {
        var (pos, tris, _) = ClothGrid(21, 21, 1f, 1f);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s = new ClothSolver(mesh, new ClothSettings());
        s.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
        int cuts = 0;
        foreach (var (t, u) in CopyPairsAcrossY(mesh, -0.5f)) if (s.Cut(t, u)) cuts++;
        return (mesh, s, cuts);
    }

    static void CopyCutAcross()
    {
        Console.WriteLine("-- A hanging sheet cut across: its bottom half is loose; the top half, pinned, is the original's");
        var (pos, tris, _) = ClothGrid(21, 21, 1f, 1f);
        var whole = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var ws = new ClothSolver(whole, new ClothSettings());
        ws.SetPaint(ClothPaint(pos, p => p.Y > -1e-4f));
        Check(!ClothPieces.FindLoose(whole, i => ws.Kind(i), out _, out _, out int none) && none == 0,
              "uncut: nothing is loose (it hangs from its pinned top row), so nothing would be left behind");

        var (mesh, s, cuts) = CopyCutSheet();
        bool any = ClothPieces.FindLoose(mesh, i => s.Kind(i), out var keep, out var loose, out int pieces);
        Check(any, $"cut across ({cuts} cuts): something is loose");
        if (!any) return;
        var c = mesh.RestCentroids;
        bool bottom = true;
        int kept = 0;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            if (keep[t]) kept++;
            if (keep[t] != (c[t].Y < -0.5f)) bottom = false;
        }
        Check(pieces == 1 && bottom && kept == mesh.TriangleCount / 2, $"one loose piece: exactly the bottom half's triangles ({kept} of {mesh.TriangleCount})");
        bool free = true;
        foreach (int p in loose) if (mesh.RestPositions[p].Y > -0.5f + 1e-4f || s.Kind(p) != 0) free = false;
        Check(free && loose.Length == 21 * 11, $"its points are the bottom half's, all free ({loose.Length}: 21 x 11, the cut line's own copies included)");
    }

    static void CopyHeldAndFree()
    {
        Console.WriteLine("-- What holds a piece: a pinned or an anchored point; a sheet with neither is loose as a whole");
        var (pos, tris, _) = ClothGrid(11, 11, 1f, 1f, lying: true);
        var m1 = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s1 = new ClothSolver(m1, new ClothSettings());
        bool all = ClothPieces.FindLoose(m1, i => s1.Kind(i), out var k1, out var l1, out int p1);
        Check(all && p1 == 1 && l1.Length == m1.ParticleCount && k1.All(x => x),
              $"all free and uncut (a blanket): the whole sheet is one loose piece ({l1?.Length ?? 0} points)");

        var m2 = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s2 = new ClothSolver(m2, new ClothSettings());
        int cuts = 0;
        foreach (var (t, u) in BlastPairsAcross(m2, 0f)) if (s2.Cut(t, u)) cuts++;
        int n = m2.ParticleCount, far = -1;
        for (int i = 0; i < n; i++)
        {
            var r = m2.RestPositions[i];
            if (MathF.Abs(r.X - 0.5f) < 1e-4f && MathF.Abs(r.Z - 0.5f) < 1e-4f) far = i;
        }
        var anchorOf = new int[n];
        Array.Fill(anchorOf, -1);
        if (far >= 0) anchorOf[far] = 0;
        s2.SetAnchors(anchorOf, new Vector3[n]);
        bool one = ClothPieces.FindLoose(m2, i => s2.Kind(i), out _, out var l2, out int p2);
        bool near = one;
        if (one) foreach (int p in l2) if (m2.RestPositions[p].Z > 1e-4f) near = false;
        Check(far >= 0 && s2.Kind(far) == 2 && one && p2 == 1 && near,
              $"cut in two ({cuts} cuts), a corner of the far half anchored: the near half is the one loose piece");
        s2.SetPaint(ClothPaint(pos, p => MathF.Abs(p.X + 0.5f) < 1e-4f && MathF.Abs(p.Z + 0.5f) < 1e-4f));
        Check(!ClothPieces.FindLoose(m2, i => s2.Kind(i), out _, out _, out _), "and a pinned corner in the near half: nothing is loose");
    }

    static void CopyBlastShreds()
    {
        Console.WriteLine("-- The effect sheet: all pinned, a blast frees and shreds its middle: the shreds are loose, the rest is the original's");
        var (pos, tris, _) = ClothGrid(41, 41, 1f, 1f, lying: true);
        var mesh = new ClothMesh(pos, tris, ClothMesh.MaxGrowth);
        var s = new ClothSolver(mesh, new ClothSettings());
        s.SetPaint(ClothPaint(pos, _ => true));
        var center = new Vector3(0.05f, 0f, -0.03f);
        s.SetReleases(new[] { new Vector4(center, 0.3f) });
        var shredder = new ClothShredder();
        var shot = new ClothShred { Center = center, Radius = 0.3f, Shred = 0.5f, Seed = 99 };
        int parts = shredder.PartCount(mesh, 99, shot), cuts = 0;
        for (int p = 0; p < parts; p++) cuts += shredder.Apply(s, 99, shot, p);
        bool any = ClothPieces.FindLoose(mesh, i => s.Kind(i), out var keep, out var loose, out int pieces);
        int total = BlastPieces(mesh, out _);
        Console.WriteLine($"   {cuts} cuts: {total} pieces, {pieces} of them loose ({(loose?.Length ?? 0)} points)");
        Check(any && pieces > 1 && pieces < total, "several loose pieces, and not all of them (the frame, and pieces reaching past the freed sphere, are held)");
        bool freed = any;
        if (any) foreach (int p in loose) if (!s.IsReleased(p) || s.Kind(p) != 0) freed = false;
        Check(freed, "every loose point is one the blast freed (anything else on an all-pinned sheet holds its piece)");
        // the loose pieces fall, the held ones stay: what the copy shows is the part that came off
        var f = ClothFrameAt(1f / 60f);
        for (int k = 0; k < 30; k++) s.Step(f);
        float lowestHeld = float.MaxValue, highestLoose = float.MinValue;
        var isLoose = new HashSet<int>(loose ?? Array.Empty<int>());
        for (int i = 0; i < mesh.ParticleCount; i++)
        {
            if (mesh.FanSize(i) == 0) continue;
            if (isLoose.Contains(i)) highestLoose = MathF.Max(highestLoose, s.X[i].Y);
            else if (s.Kind(i) == 1) lowestHeld = MathF.Min(lowestHeld, s.X[i].Y);
        }
        Console.WriteLine($"   half a second later: loose points at most {highestLoose:0.000} m, pinned ones at least {lowestHeld:0.000} m");
        Check(highestLoose < -0.05f && MathF.Abs(lowestHeld) < 1e-5f && ClothFinite(s), "the loose pieces have fallen; the pinned sheet is in place");
    }

    static void CopyHideTriangles()
    {
        Console.WriteLine("-- Hiding the held pieces: their triangles become empty; the loose ones keep their corners");
        var (mesh, s, _) = CopyCutSheet();
        if (!ClothPieces.FindLoose(mesh, i => s.Kind(i), out var keep, out _, out _)) { Check(false, "the cut sheet has a loose piece"); return; }
        var idx = mesh.RenderTriangles.ToArray();
        var before = (int[])idx.Clone();
        var sim = mesh.RenderTriangleSim;
        // as two submeshes: the first 300 triangles, then the rest
        int hidden = ClothPieces.HideHeld(sim, keep, idx.AsSpan(0, 900), 0) + ClothPieces.HideHeld(sim, keep, idx.AsSpan(900), 300);
        int expect = 0;
        bool ok = true;
        for (int r = 0; r < mesh.RenderTriangleCount; r++)
        {
            bool show = sim[r] >= 0 && keep[sim[r]];
            if (!show) expect++;
            if (show) ok &= idx[3 * r] == before[3 * r] && idx[3 * r + 1] == before[3 * r + 1] && idx[3 * r + 2] == before[3 * r + 2];
            else ok &= idx[3 * r] == before[3 * r] && idx[3 * r + 1] == before[3 * r] && idx[3 * r + 2] == before[3 * r];
        }
        Check(ok && hidden == expect && hidden == mesh.RenderTriangleCount / 2,
              $"the top half's {hidden} triangles are empty, the bottom half's {mesh.RenderTriangleCount - hidden} untouched (across two submeshes)");
        var once = (int[])idx.Clone();
        int again = ClothPieces.HideHeld(sim, keep, idx.AsSpan(0, 900), 0) + ClothPieces.HideHeld(sim, keep, idx.AsSpan(900), 300);
        Check(again == hidden && idx.AsSpan().SequenceEqual(once), "hiding again changes nothing more (the same triangles, still empty)");
    }
}
