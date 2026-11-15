using System.Numerics;

namespace Softbody.Core.Cloth;

/// <summary>
/// 0.31.0: a blast's push for one step, in the world (<see cref="ClothFrame.Kicks"/>): every free point within
/// <see cref="Radius"/> of <see cref="Center"/> gets up to <see cref="Speed"/> m/s more, outward from the centre.
/// </summary>
public struct ClothKick
{
    public Vector3 Center;
    public float Radius;
    /// <summary>m/s at the centre, falling off smoothly to 0 at Radius.</summary>
    public float Speed;
    /// <summary>0..1: how uneven it is (the speed and direction vary over the cloth in patches about a third of the radius
    /// across, so neighbouring points move together and nothing is torn by the noise alone).</summary>
    public float Randomness;
    public int Seed;
}

/// <summary>
/// 0.31.0: what a blast shreds, in the cloth's rest space (the mesh's own, the renderer's slot for world cloth): the cloth
/// around the triangles whose rest centre is within <see cref="Radius"/> of <see cref="Center"/> comes apart into pieces
/// about <see cref="ClothBlastMath.Spacing"/> across (with <see cref="Shred"/> 1, every triangle on its own).
/// </summary>
public struct ClothShred
{
    public Vector3 Center;
    public float Radius;
    /// <summary>0..1: 0 nothing; up to 1, from a few big pieces to confetti.</summary>
    public float Shred;
    public int Seed;
}

/// <summary>
/// 0.31.0: the blast's maths (DESIGN-cloth.md, "Cloth Blast"). The shred's cuts are worked out from the mesh as built and
/// the blast's synced numbers alone, with scalar arithmetic in a fixed order (no SIMD dot products, no Pow or Log), so
/// every client works out the same cuts in the same order.
/// </summary>
public static class ClothBlastMath
{
    /// <summary>Cuts per part of a shred. The client that decides the tears makes one part a frame (nearest the centre
    /// first), so a big shred spreads out over a few frames, and each part is one entry in the synced tear list.</summary>
    public const int CutsPerPart = 256;
    /// <summary>A blast's id is 1..MaxId, so (-id, part) is a tear-list entry no split can be (a split's are both 0 or more).</summary>
    public const int MaxId = 1 << 30;
    /// <summary>The pieces' size steps: each is this much bigger than the last (from the mesh's mean edge up to the radius).</summary>
    const float Step = 1.25f;

    public static int PartCount(int cuts) => cuts <= 0 ? 0 : (cuts + CutsPerPart - 1) / CutsPerPart;

    /// <summary>A tear-list entry that is a shred part (-id, part), not a split (point, other).</summary>
    public static bool IsShredEntry(int x, int y) => x < 0 && y >= 0;

    /// <summary>Whether rest point <paramref name="p"/> is in the sphere (xyz centre, w radius). Scalar.</summary>
    public static bool InSphere(Vector3 p, Vector4 s)
    {
        float dx = p.X - s.X, dy = p.Y - s.Y, dz = p.Z - s.Z;
        return dx * dx + dy * dy + dz * dz <= s.W * s.W;
    }

    /// <summary>
    /// The pieces' size (mesh units) for <paramref name="shred"/> 0..1: in steps of 1.25 from the radius (0) down to the
    /// mesh's mean edge (1). Multiplications only, so the same on every client.
    /// </summary>
    public static float Spacing(float meanEdge, float radius, float shred)
    {
        if (!(meanEdge > 0f) || !float.IsFinite(meanEdge) || !float.IsFinite(radius)) return 0f;
        int k = 0;
        float s = meanEdge;
        while (k < 100 && s * Step <= radius) { s *= Step; k++; }
        float f = 1f - (float.IsFinite(shred) ? Math.Clamp(shred, 0f, 1f) : 0f);
        int want = (int)(f * k + 0.5f);
        float sp = meanEdge;
        for (int i = 0; i < want; i++) sp *= Step;
        return sp;
    }

    /// <summary>
    /// Which piece rest point <paramref name="p"/> falls in: the nearest of the points scattered one in each cube of side
    /// <paramref name="spacing"/> (cellular noise), as that cube's packed coordinates.
    /// </summary>
    public static long Piece(Vector3 p, float spacing, int seed)
    {
        float fx = p.X / spacing, fy = p.Y / spacing, fz = p.Z / spacing;
        if (!float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz)) return 0;
        int cx = (int)MathF.Floor(fx), cy = (int)MathF.Floor(fy), cz = (int)MathF.Floor(fz);
        float best = float.MaxValue;
        long label = 0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx = cx + dx, gy = cy + dy, gz = cz + dz;
                    uint h = Hash(gx, gy, gz, seed);
                    float ox = gx + Unit(h), oy = gy + Unit(Mix(h ^ 0x68E31DA4u)), oz = gz + Unit(Mix(h ^ 0xB5297A4Du));
                    float ex = fx - ox, ey = fy - oy, ez = fz - oz;
                    float d2 = ex * ex + ey * ey + ez * ez;
                    if (d2 < best) { best = d2; label = Pack(gx, gy, gz); }
                }
        return label;
    }

    /// <summary>
    /// A shred's cuts, in the order they are made: every pair of triangles that shared an edge on the mesh as built, one of
    /// them with its rest centre within the radius, in different pieces (with Shred 1, every such pair), nearest the centre
    /// first (then by triangle). Pairs that no longer share an edge when their turn comes are skipped by
    /// <see cref="ClothMesh.Cut"/>, the same way everywhere.
    /// </summary>
    public static int ShredCuts(ClothMesh mesh, in ClothShred shot, List<(int t, int u)> into)
    {
        into.Clear();
        float r = shot.Radius;
        var c = shot.Center;
        if (!(shot.Shred > 0f) || !(r > 0f) || !float.IsFinite(r) || !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z)) return 0;
        bool confetti = shot.Shred >= 1f;
        float spacing = confetti ? 0f : Spacing(mesh.MeanEdge, r, shot.Shred);
        if (!confetti && !(spacing > 0f)) return 0;
        var cen = mesh.RestCentroids;
        int nt = mesh.TriangleCount;
        long[] piece = confetti ? null : new long[nt];
        bool[] known = confetti ? null : new bool[nt];
        float r2 = r * r;
        var found = new List<(float d2, int t, int u)>();
        foreach (var (t, u) in mesh.OriginalNeighbours)
        {
            float dt = Dist2(cen[t], c), du = Dist2(cen[u], c);
            if (dt > r2 && du > r2) continue;
            if (!confetti)
            {
                if (!known[t]) { piece[t] = Piece(cen[t], spacing, shot.Seed); known[t] = true; }
                if (!known[u]) { piece[u] = Piece(cen[u], spacing, shot.Seed); known[u] = true; }
                if (piece[t] == piece[u]) continue;
            }
            found.Add((MathF.Min(dt, du), t, u));
        }
        found.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.t != b.t ? a.t.CompareTo(b.t) : a.u.CompareTo(b.u));
        foreach (var e in found) into.Add((e.t, e.u));
        return into.Count;
    }

    static float Dist2(Vector3 p, Vector3 c)
    {
        float dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The velocity a kick adds at world point <paramref name="x"/> (zero outside its radius).</summary>
    public static Vector3 Kick(in ClothKick k, Vector3 x)
    {
        float r = k.Radius;
        if (!(r > 0f) || !float.IsFinite(r) || !float.IsFinite(k.Speed)) return Vector3.Zero;
        var d = x - k.Center;
        float d2 = d.LengthSquared(), r2 = r * r;
        if (!(d2 < r2)) return Vector3.Zero;
        float q = 1f - d2 / r2;
        float speed = k.Speed * q * q;
        float dist = MathF.Sqrt(d2);
        var dir = dist > 1e-4f * r ? d / dist : SeedDirection(k.Seed);
        float rnd = float.IsFinite(k.Randomness) ? Math.Clamp(k.Randomness, 0f, 1f) : 0f;
        if (rnd > 0f)
        {
            var p = d / MathF.Max(r / 3f, 0.05f);
            speed *= 1f + 0.6f * rnd * Noise(p, k.Seed);
            var tilt = new Vector3(Noise(p, k.Seed + 101), Noise(p, k.Seed + 202), Noise(p, k.Seed + 303));
            var nd = dir + tilt * (0.6f * rnd);
            float l = nd.Length();
            if (l > 1e-4f && float.IsFinite(l)) dir = nd / l;
        }
        return dir * speed;
    }

    /// <summary>A direction from the seed (the push at the very centre of a blast).</summary>
    static Vector3 SeedDirection(int seed)
    {
        uint h = Hash(seed, 7, 13, 0x5EED);
        float z = Unit(h) * 2f - 1f, a = Unit(Mix(h ^ 0x9E3779B9u)) * MathF.Tau;
        float s = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(s * MathF.Cos(a), z, s * MathF.Sin(a));
    }

    /// <summary>Smooth value noise, -1..1, varying over about one unit.</summary>
    static float Noise(Vector3 p, int seed)
    {
        float fx = MathF.Floor(p.X), fy = MathF.Floor(p.Y), fz = MathF.Floor(p.Z);
        if (!float.IsFinite(fx + fy + fz)) return 0f;
        int ix = (int)fx, iy = (int)fy, iz = (int)fz;
        float tx = Smooth(p.X - fx), ty = Smooth(p.Y - fy), tz = Smooth(p.Z - fz);
        float x00 = Lerp(Lattice(ix, iy, iz, seed), Lattice(ix + 1, iy, iz, seed), tx);
        float x10 = Lerp(Lattice(ix, iy + 1, iz, seed), Lattice(ix + 1, iy + 1, iz, seed), tx);
        float x01 = Lerp(Lattice(ix, iy, iz + 1, seed), Lattice(ix + 1, iy, iz + 1, seed), tx);
        float x11 = Lerp(Lattice(ix, iy + 1, iz + 1, seed), Lattice(ix + 1, iy + 1, iz + 1, seed), tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    static float Lattice(int x, int y, int z, int seed) => Unit(Hash(x, y, z, seed)) * 2f - 1f;
    static float Smooth(float t) => t * t * (3f - 2f * t);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static uint Mix(uint h)
    {
        unchecked
        {
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    static uint Hash(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = Mix((uint)seed * 0x9E3779B9u + 0x632BE5ABu);
            h = Mix(h ^ (uint)x * 0x85EBCA6Bu);
            h = Mix(h ^ (uint)y * 0xC2B2AE35u);
            h = Mix(h ^ (uint)z * 0x27D4EB2Fu);
            return h;
        }
    }

    /// <summary>0..1 (24 bits, exact as a float).</summary>
    static float Unit(uint h) => (h >> 8) * (1f / 16777216f);

    static long Pack(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
}

/// <summary>
/// 0.31.0: one cloth's shreds: each blast's cuts (worked out once, by its id), and how many of its parts were made.
/// </summary>
public sealed class ClothShredder
{
    readonly Dictionary<int, List<(int t, int u)>> _cuts = new();
    readonly Dictionary<int, int> _parts = new();

    /// <summary>The cuts of blast <paramref name="id"/>, in order (see <see cref="ClothBlastMath.ShredCuts"/>).</summary>
    public List<(int t, int u)> CutsOf(ClothMesh mesh, int id, in ClothShred shot)
    {
        if (_cuts.TryGetValue(id, out var list)) return list;
        list = new List<(int t, int u)>();
        ClothBlastMath.ShredCuts(mesh, shot, list);
        _cuts[id] = list;
        return list;
    }

    public int PartCount(ClothMesh mesh, int id, in ClothShred shot) => ClothBlastMath.PartCount(CutsOf(mesh, id, shot).Count);

    /// <summary>The parts of blast <paramref name="id"/> made so far (the next one to make).</summary>
    public int PartsMade(int id) => _parts.TryGetValue(id, out var p) ? p : 0;

    /// <summary>Makes part <paramref name="part"/> of blast <paramref name="id"/>'s cuts on the solver, in order; how many
    /// cuts it made (the rest no longer apply, or there was no room).</summary>
    public int Apply(ClothSolver solver, int id, in ClothShred shot, int part)
    {
        var cuts = CutsOf(solver.Mesh, id, shot);
        if (part < 0) return 0;
        int from = (int)Math.Min((long)part * ClothBlastMath.CutsPerPart, cuts.Count), to = Math.Min(cuts.Count, from + ClothBlastMath.CutsPerPart);
        int made = 0;
        for (int i = from; i < to; i++) if (solver.Cut(cuts[i].t, cuts[i].u)) made++;
        if (part + 1 > PartsMade(id)) _parts[id] = part + 1;
        return made;
    }

    public void Clear() { _cuts.Clear(); _parts.Clear(); }
}
