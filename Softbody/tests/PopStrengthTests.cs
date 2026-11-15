using System.Numerics;
using Softbody.Core.Flesh;
using Softbody.Core.Pop;

// 0.36.0: Pop's MaxStrength, a range for Strength: each piece flies out at its own speed in it, picked evenly from the
// shot's seed (so the same on every client), stiff pieces and soft.
// Run with:  dotnet run -c Release --project tests -- pop-strength   ("pop" runs these too)
namespace Softbody.Tests;

static partial class Program
{
    static void PopStrengthTests()
    {
        Console.WriteLine("== Pop: a range of strengths (0.36.0)");
        PopStrengthRange();
        PopStrengthFlight();
    }

    static readonly Vector3 PopMiddle = new(0f, 0.9f, 0f);

    /// <summary>The capsule player PopFlight pops (Shred 0.8, seed 11).</summary>
    static PopPieces PopStrengthCapsule()
    {
        var (bp, bn, bt) = CapsuleMesh(0.2f, 1.6f, 0.2f, seg: 48, capRows: 12, bodyRows: 48);
        return PopCut.Cut(new[] { PopFrom(bp, bn, bt) }, new PopCutSettings { Shred = 0.8f, MaxPieces = 1500, Seed = 11 });
    }

    /// <summary>The pieces straight after a kick from the middle (nothing stepped yet).</summary>
    static PopSolver PopKicked(PopPieces cut, PopSoftPieces soft, float strength, float randomness, float maxStrength, int seed = 11)
    {
        var s = new PopSolver(cut, soft) { StopBelow = -50f };
        s.Kick(PopMiddle, strength, randomness, seed, maxStrength);
        return s;
    }

    static float[] PopSpeeds(PopSolver s)
    {
        var v = new float[s.Pieces.Count];
        for (int i = 0; i < v.Length; i++) v[i] = s.V[i].Length();
        return v;
    }

    /// <summary>The kick with a range: every piece at its own speed in it, spread evenly; either order the same; none at 0
    /// or at Strength itself (the kick as before); Randomness no longer slows them; the same seed, the same push; inward
    /// too; soft pieces as fast as stiff ones, whole.</summary>
    static void PopStrengthRange()
    {
        Console.WriteLine("-- A capsule player's pieces kicked at Strength 2, MaxStrength 8 (Shred 0.8, Randomness 0.5)");
        var cut = PopStrengthCapsule();
        int n = cut.Count;
        var s = PopKicked(cut, null, 2f, 0.5f, 8f);
        var v = PopSpeeds(s);
        float lo = v.Min(), hi = v.Max(), mean = v.Average();
        var quarters = new int[4];
        foreach (float x in v) quarters[Math.Clamp((int)((x - 2f) / 1.5f), 0, 3)]++;
        string shares = string.Join(", ", quarters.Select(q => $"{100.0 * q / n:0}%"));
        Console.WriteLine($"   {n} pieces: {lo:0.00} to {hi:0.00} m/s, {mean:0.00} on average; each quarter of the range {shares}");
        Check(lo >= 2f - 1e-4f && hi <= 8f + 1e-4f, $"every piece flies out between 2 and 8 m/s ({lo:0.000} to {hi:0.000})");
        Check(lo < 2.25f && hi > 7.75f && MathF.Abs(mean - 5f) < 0.35f, $"over the whole range ({lo:0.00} to {hi:0.00}, {mean:0.00} on average)");
        Check(quarters.All(q => q > n * 0.15 && q < n * 0.35), $"spread evenly over it (each quarter of it {shares})");
        int outward = 0;
        for (int i = 0; i < n; i++) if (Vector3.Dot(s.V[i], s.X[i] - PopMiddle) > 0f) outward++;
        Check(outward == n, $"all of them out from the middle ({outward} of {n})");

        // either order, the same push; the same seed, the same push (every client); another seed, other speeds
        var back = PopKicked(cut, null, 8f, 0.5f, 2f);
        var again = PopKicked(cut, null, 2f, 0.5f, 8f);
        var other = PopKicked(cut, null, 2f, 0.5f, 8f, seed: 12);
        bool same = true, sameAgain = true;
        int differ = 0;
        for (int i = 0; i < n; i++)
        {
            same &= back.V[i] == s.V[i] && back.W[i] == s.W[i];
            sameAgain &= again.V[i] == s.V[i] && again.W[i] == s.W[i];
            if (MathF.Abs(other.V[i].Length() - v[i]) > 0.1f) differ++;
        }
        Check(same, "Strength 8 and MaxStrength 2 give the same push as 2 and 8");
        Check(sameAgain && differ > n / 2, $"the same seed gives the same push (every client), another seed another ({differ} of {n} pieces at another speed)");

        // no range: MaxStrength 0, or Strength itself, is the kick as it was (Randomness 0.5 slows by up to a quarter)
        var none = PopKicked(cut, null, 5f, 0.5f, 0f);
        var equal = PopKicked(cut, null, 5f, 0.5f, 5f);
        var plain = new PopSolver(cut) { StopBelow = -50f };
        plain.Kick(PopMiddle, 5f, 0.5f, 11);
        bool asBefore = true;
        for (int i = 0; i < n; i++)
            asBefore &= none.V[i] == plain.V[i] && none.W[i] == plain.W[i] && equal.V[i] == plain.V[i] && equal.W[i] == plain.W[i];
        var vn = PopSpeeds(none);
        Console.WriteLine($"   no range (Strength 5, MaxStrength 0): {vn.Min():0.00} to {vn.Max():0.00} m/s");
        Check(asBefore, "MaxStrength 0, or Strength's own value, is no range: the push is the one without it");
        Check(vn.Min() >= 3.75f - 1e-4f && vn.Max() <= 5f + 1e-4f && vn.Min() < 4f,
              $"which Randomness 0.5 slows by up to a quarter, as before ({vn.Min():0.00} to {vn.Max():0.00} m/s)");

        // with a range Randomness no longer slows the pieces, but still sends them sideways and spins them
        var calm = PopKicked(cut, null, 2f, 0f, 8f);
        var wild = PopKicked(cut, null, 2f, 1f, 8f);
        bool sameSpeeds = true;
        int turned = 0, spun = 0;
        for (int i = 0; i < n; i++)
        {
            float a = calm.V[i].Length(), b = wild.V[i].Length();
            sameSpeeds &= MathF.Abs(a - b) < 1e-4f * MathF.Max(1f, a);
            if (Vector3.Distance(calm.V[i], wild.V[i]) > 0.05f) turned++;
            if (calm.W[i] == Vector3.Zero && wild.W[i].Length() > 0.1f) spun++;
        }
        Check(sameSpeeds, "with a range, every piece flies at the same speed at Randomness 0 and 1");
        Check(turned > n * 0.9 && spun > n * 0.9, $"Randomness still sends them sideways ({turned} of {n}) and spins them ({spun} of {n}; none at 0)");

        // inward: both ends below 0
        var inward = PopKicked(cut, null, -2f, 0.5f, -8f);
        int toward = 0;
        float ilo = float.MaxValue, ihi = 0f;
        for (int i = 0; i < n; i++)
        {
            float sp = inward.V[i].Length();
            ilo = MathF.Min(ilo, sp); ihi = MathF.Max(ihi, sp);
            if (Vector3.Dot(inward.V[i], inward.X[i] - PopMiddle) < 0f) toward++;
        }
        Check(toward == n && ilo >= 2f - 1e-4f && ihi <= 8f + 1e-4f, $"Strength -2, MaxStrength -8: every piece flies in, at 2 to 8 m/s ({toward} of {n}; {ilo:0.00} to {ihi:0.00})");

        // soft pieces: each as fast as the stiff one, all its particles at its speed (Randomness 0: no spin to add)
        var soft = PopSoft.Build(cut);
        var sk = PopKicked(cut, soft, 2f, 0f, 8f);
        float worst = 0f;
        for (int i = 0; i < n; i++) worst = MathF.Max(worst, MathF.Abs(sk.V[i].Length() - calm.V[i].Length()));
        bool whole = true;
        for (int i = 0; i < n; i++)
            for (int p = soft.PStart[i]; p < soft.PStart[i + 1]; p++) whole &= Vector3.Distance(sk.SV[p], sk.V[i]) < 1e-4f;
        Check(worst < 1e-4f, $"soft pieces fly at the same speeds as stiff ones (at most {worst * 1000f:0.000} mm/s apart)");
        Check(whole, "each soft piece's particles all start at its speed: it flies off whole, not pulled apart");
    }

    /// <summary>A capsule player popped over a floor with a wide range, against one strength (the range's middle), stiff
    /// and soft: with the range the pieces land from near the feet to far off, and all come down and lie still, none in
    /// the floor.</summary>
    static void PopStrengthFlight()
    {
        Console.WriteLine("-- A capsule player pops over a floor at Strength 1 to 10, against 5.5 alone (Shred 0.8, Randomness 0.5, 90 fps; soft: Stiffness 0.3, Drag 0.5, Adhesion 2)");
        var cut = PopStrengthCapsule();
        var soft = PopSoft.Build(cut);
        int n = cut.Count;
        var cols = new List<FleshCollider> { FleshCollider.Plane(Vector3.Zero, Vector3.UnitY) };
        var g = new Vector3(0f, -9.81f, 0f);
        foreach (var (kind, sp) in new[] { ("stiff", (PopSoftPieces)null), ("soft", soft) })
        {
            var near = new float[2];
            var far = new float[2];
            for (int k = 0; k < 2; k++)
            {
                var s = sp == null ? new PopSolver(cut) { StopBelow = -50f }
                                   : new PopSolver(cut, sp) { Stiffness = 0.3f, Drag = 0.5f, Adhesion = 2f, StopBelow = -50f };
                if (k == 0) s.Kick(PopMiddle, 1f, 0.5f, 11, 10f);
                else s.Kick(PopMiddle, 5.5f, 0.5f, 11);
                float lowest = float.MaxValue;
                for (int f = 0; f < 90 * 8; f++)
                {
                    s.Step(1f / 90f, g, cols);
                    for (int v = 0; v < cut.FrontVertices; v++) lowest = MathF.Min(lowest, s.OutPositions[v].Y);
                }
                float rest = float.MaxValue;
                for (int v = 0; v < cut.FrontVertices; v++) rest = MathF.Min(rest, s.OutPositions[v].Y);
                var r = new float[n];
                int up = 0;
                for (int i = 0; i < n; i++)
                {
                    r[i] = new Vector2(s.X[i].X, s.X[i].Z).Length();
                    if (sp == null) { if (s.X[i].Y > cut.Radius[i] + 0.02f) up++; }
                    else
                    {
                        float low = float.MaxValue;
                        for (int p = sp.PStart[i]; p < sp.PStart[i + 1]; p++) low = MathF.Min(low, s.SX[p].Y);
                        if (low > 0.02f) up++;
                    }
                }
                Array.Sort(r);
                near[k] = r[n / 10];
                far[k] = r[n * 9 / 10];
                string what = $"{kind}, {(k == 0 ? "1 to 10 m/s" : "5.5 m/s")}";
                Console.WriteLine($"   {what}: after 8 s nine in ten lie {near[k]:0.00} to {far[k]:0.00} m from the middle, {s.Awake} awake, lowest vertex {lowest * 1000f:0.0} mm (at rest {rest * 1000f:0.0})");
                Check(ClothFiniteArr(s.OutPositions), $"{what}: finite");
                Check(up == 0 && s.Awake <= n / 20, $"{what}: every piece came down ({up} still up) and lies still ({s.Awake} of {n} awake)");
                if (sp == null) Check(rest > 0.002f, $"{what}: at rest every vertex lies on the floor, none in it (lowest {rest * 1000f:0.0} mm)");
                else Check(lowest > -0.001f && rest > -0.001f, $"{what}: no vertex shows in the floor, landing or lying still (lowest {lowest * 1000f:0.0} mm, at rest {rest * 1000f:0.0})");
            }
            Check(near[0] < near[1] && far[0] > far[1] && far[0] - near[0] > 1.3f * (far[1] - near[1]),
                  $"{kind}: with the range they lie from nearer to farther off ({near[0]:0.00} to {far[0]:0.00} m, against {near[1]:0.00} to {far[1]:0.00})");
        }
    }
}
