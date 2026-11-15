using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Cloth;

/// <summary>0.29.2: what a body fit reads: one skinned mesh in its bind space (copied from the engine on the world thread).</summary>
public sealed class BodyMesh
{
    public Vector3[] Positions;
    /// <summary>Per vertex (any length), or null: then they are worked out from the triangles.</summary>
    public Vector3[] Normals;
    public int[] Triangles;
    public SkinData Skin;
    /// <summary>The skin matrices when the fit starts (BoneCount + 1), only for how big a bind unit is in metres; null: the
    /// mesh is in metres.</summary>
    public Matrix4x4[] SkinNow;
}

/// <summary>0.29.2: the sizes a body fit works to, in metres.</summary>
public sealed class BodyFitSettings
{
    /// <summary>Distance between the surface samples.</summary>
    public float Spacing = 0.01f;
    /// <summary>Parts thinner than twice this get no spheres (fingertips, ears' rims, two-sided sheets).</summary>
    public float MinRadius = 0.006f;
    public float MaxRadius = 0.06f;
    /// <summary>How far the skin may stand off the spheres between them.</summary>
    public float Tolerance = 0.005f;
    public int MaxSpheres = 2500;
    /// <summary>Surface samples at most (a finer mesh is sampled more coarsely).</summary>
    public int MaxSamples = 120000;
}

/// <summary>
/// 0.29.2: a body as spheres just inside its skin, for the cloth to rest on. Fitted once per skinned mesh in its bind
/// space (on a worker), then posed every frame by the mesh's own bones, the way the renderer skins the mesh, so the
/// spheres stay inside the skin you see. Bone colliders (capsules on the major bones) leave gaps and are thinner than many
/// avatars; these follow the actual mesh.
/// <para>The fit:</para>
/// <list type="number">
/// <item>Surface samples about <see cref="BodyFitSettings.Spacing"/> apart: the vertices, plus points inside triangles
/// longer than that, one per small cell and normal direction (so the two sides of a thin part both stay).</item>
/// <item>Each sample's largest ball inside the surface that touches it (the "shrinking ball": the smallest of
/// |p-q|^2 / (2 (p-q).n) over the other samples q that face it from across the inside, and no wider than the tangent
/// planes of those that face back at it, so it ends at a slab's far face or a box's edge between samples). A sample counts
/// as across the inside only if p lies behind its own surface, so clothes over skin, or hair over the scalp, do not
/// squeeze the outer layer's balls; a sample with one facing the other way on its own surface nearby (a two-sided sheet)
/// gets none, and neither does one with nothing facing back out behind it (a sheet modelled one-sided: a wing's membrane,
/// a cape).</item>
/// <item>The balls between <see cref="BodyFitSettings.MinRadius"/> and <see cref="BodyFitSettings.MaxRadius"/>
/// (bigger ones are cut down to the largest, which still touches the skin from inside), biggest first, each only where
/// the skin is not already within <see cref="BodyFitSettings.Tolerance"/> of the spheres kept so far.</item>
/// </list>
/// Immutable once built, so the cloth workers share it.
/// </summary>
public sealed class BodyShape
{
    /// <summary>Bones of the mesh; pose with BoneCount + 1 skin matrices (the last is the renderer slot).</summary>
    public readonly int BoneCount;
    /// <summary>Bind space.</summary>
    public readonly Vector3[] Center;
    /// <summary>Bind units.</summary>
    public readonly float[] Radius;
    /// <summary>4 per sphere, the strongest first (the first is the bone the sphere mostly follows).</summary>
    public readonly int[] Bones;
    public readonly float[] Weights;
    public int Count => Radius.Length;

    /// <summary>Surface samples, and how many of them had a ball big enough.</summary>
    public int Samples { get; private init; }
    public int Candidates { get; private init; }
    /// <summary>Metres per bind unit when fitted.</summary>
    public float UnitScale { get; private init; }
    /// <summary>The tolerance it ended up with (metres; raised when there would have been too many spheres).</summary>
    public float ToleranceUsed { get; private init; }
    /// <summary>Spheres left out because there were more than MaxSpheres even so (the smallest).</summary>
    public int Dropped { get; private init; }
    public double FitMs { get; private init; }

    BodyShape(int boneCount, Vector3[] center, float[] radius, int[] bones, float[] weights)
    {
        BoneCount = boneCount; Center = center; Radius = radius; Bones = bones; Weights = weights;
    }

    public static readonly BodyShape Empty = new(0, Array.Empty<Vector3>(), Array.Empty<float>(), Array.Empty<int>(), Array.Empty<float>());

    // ------------------------------------------------------------------ posing (cloth worker)

    /// <summary>World centres and radii for these skin matrices (BoneCount + 1 of them). <paramref name="boneScale"/> is
    /// scratch of at least BoneCount + 1. A radius grows with its bones' scale, blended like the centre.</summary>
    public void Pose(ReadOnlySpan<Matrix4x4> skin, Span<float> boneScale, Span<Vector3> center, Span<float> radius)
    {
        int nb = BoneCount + 1;
        for (int b = 0; b < nb; b++) boneScale[b] = Lbs.LinearScale(skin[b]);
        for (int i = 0; i < Radius.Length; i++)
        {
            int o = i * 4;
            var m = Lbs.Blend(skin, Bones[o], Weights[o], Bones[o + 1], Weights[o + 1], Bones[o + 2], Weights[o + 2], Bones[o + 3], Weights[o + 3]);
            center[i] = Vector3.Transform(Center[i], m);
            radius[i] = Radius[i] * (Weights[o] * boneScale[Bones[o]] + Weights[o + 1] * boneScale[Bones[o + 1]] +
                                     Weights[o + 2] * boneScale[Bones[o + 2]] + Weights[o + 3] * boneScale[Bones[o + 3]]);
        }
    }

    /// <summary>World centres only.</summary>
    public void PoseCenters(ReadOnlySpan<Matrix4x4> skin, Span<Vector3> center)
    {
        for (int i = 0; i < Radius.Length; i++)
        {
            int o = i * 4;
            var m = Lbs.Blend(skin, Bones[o], Weights[o], Bones[o + 1], Weights[o + 1], Bones[o + 2], Weights[o + 2], Bones[o + 3], Weights[o + 3]);
            center[i] = Vector3.Transform(Center[i], m);
        }
    }

    // ------------------------------------------------------------------ the fit (worker)

    /// <summary>Tests: the surface samples (bind space), each one's ball (bind units; 0: none) and whether it was a
    /// candidate.</summary>
    public sealed class Report
    {
        public Vector3[] Points, Normals;
        public float[] Ball;
        public bool[] Candidate, Under;
    }

    /// <summary>Fits the spheres. Throws on input that is not a mesh (no positions, bad skin); an empty shape when nothing
    /// is thick enough.</summary>
    public static BodyShape Fit(BodyMesh mesh, BodyFitSettings settings = null, Report report = null)
    {
        long t0 = Stopwatch.GetTimestamp();
        var s = settings ?? new BodyFitSettings();
        var pos = mesh.Positions ?? throw new ArgumentException("no positions");
        var tris = mesh.Triangles ?? throw new ArgumentException("no triangles");
        var skin = mesh.Skin ?? throw new ArgumentException("no skin");
        int nv = pos.Length;
        if (skin.VertexCount < nv) throw new ArgumentException("fewer bone weights than vertices");
        int nb = skin.BoneCount;

        float unit = UnitOf(mesh, nv);
        float inv = 1f / unit;
        float spacing = s.Spacing * inv, rmin = s.MinRadius * inv, rmax = s.MaxRadius * inv;

        var nrm = VertexNormals(pos, mesh.Normals, tris);
        var samples = new SampleSet(nb);
        spacing = Sample(pos, nrm, tris, skin, spacing, s.MaxSamples, samples);
        int ns = samples.Count;

        // the balls (the samples on a layer under another first: they take no part)
        var grid = new PointGrid(samples.P, ns, 0.5f * rmax);
        var under = new bool[ns];
        var bvh = TriangleBvh.Build(pos, tris);
        var hits = new List<int>();
        for (int i = 0; i < ns; i++) under[i] = Covered(bvh, samples.P[i], samples.N[i], 3f * spacing, hits);
        var ball = new float[ns];
        for (int i = 0; i < ns; i++) ball[i] = under[i] ? 0f : Ball(samples, grid, under, i, rmax, spacing);

        var cand = new List<int>();
        // how far in a part's far side may be: 8 of the biggest balls (half a metre: a body's depth), or across the mesh
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int i = 0; i < ns; i++) { lo = Vector3.Min(lo, samples.P[i]); hi = Vector3.Max(hi, samples.P[i]); }
        float reach = ns > 0 ? MathF.Min(Vector3.Distance(lo, hi), 8f * rmax) : 0f;
        float step = 0.5f * rmax;
        for (int i = 0; i < ns; i++)
        {
            if (!(ball[i] >= rmin)) continue;
            if (Closed(samples, grid, i, reach, 2f * spacing, step)) cand.Add(i);
            else ball[i] = 0f;   // one-sided: air behind it
        }
        if (report != null)
        {
            report.Points = samples.P[..ns]; report.Normals = samples.N[..ns]; report.Ball = ball; report.Under = under;
            report.Candidate = new bool[ns];
            foreach (int i in cand) report.Candidate[i] = true;
        }
        // biggest first; the index keeps it deterministic
        cand.Sort((a, b) => ball[a] != ball[b] ? ball[b].CompareTo(ball[a]) : a.CompareTo(b));

        float tol = s.Tolerance * inv;
        List<int> kept = null;
        var covered = new bool[ns];
        var isCand = new bool[ns];
        foreach (int i in cand) isCand[i] = true;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            kept = Cover(samples, grid, cand, ball, isCand, covered, tol);
            if (kept.Count <= s.MaxSpheres) break;
            if (attempt < 4) tol *= 1.6f;
        }
        int dropped = Math.Max(0, kept.Count - s.MaxSpheres);
        if (dropped > 0) kept.RemoveRange(s.MaxSpheres, dropped);

        int n = kept.Count;
        var center = new Vector3[n]; var radius = new float[n]; var bones = new int[n * 4]; var weights = new float[n * 4];
        for (int k = 0; k < n; k++)
        {
            int i = kept[k];
            center[k] = samples.P[i] - samples.N[i] * ball[i];
            radius[k] = ball[i];
            for (int q = 0; q < 4; q++) { bones[k * 4 + q] = samples.B[i * 4 + q]; weights[k * 4 + q] = samples.W[i * 4 + q]; }
        }
        return new BodyShape(nb, center, radius, bones, weights)
        {
            Samples = ns, Candidates = cand.Count, UnitScale = unit, ToleranceUsed = tol * unit, Dropped = dropped,
            FitMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency,
        };
    }

    /// <summary>Metres per bind unit: the middle scale of the skin matrices of the bones that carry weight.</summary>
    static float UnitOf(BodyMesh mesh, int nv)
    {
        var now = mesh.SkinNow;
        var skin = mesh.Skin;
        if (now == null || now.Length < skin.MatrixCount) return 1f;
        var used = new bool[skin.MatrixCount];
        for (int v = 0; v < nv; v++)
            for (int q = 0; q < 4; q++)
                if (skin.VertexWeights[v * 4 + q] > 0f) used[skin.VertexBones[v * 4 + q]] = true;
        var scales = new List<float>();
        for (int b = 0; b < used.Length; b++)
        {
            if (!used[b]) continue;
            float sc = Lbs.LinearScale(now[b]);
            if (float.IsFinite(sc) && sc > 0f) scales.Add(sc);
        }
        if (scales.Count == 0) return 1f;
        scales.Sort();
        float mid = scales[scales.Count / 2];
        return mid > 1e-6f && mid < 1e6f ? mid : 1f;
    }

    static Vector3[] VertexNormals(Vector3[] pos, Vector3[] given, int[] tris)
    {
        int nv = pos.Length;
        var n = new Vector3[nv];
        bool have = given != null && given.Length >= nv;
        if (have)
            for (int v = 0; v < nv; v++) n[v] = given[v];
        else
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv) continue;
                var f = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);   // area weighted
                n[a] += f; n[b] += f; n[c] += f;
            }
        for (int v = 0; v < nv; v++)
        {
            float l = n[v].Length();
            n[v] = l > 1e-20f && float.IsFinite(l) ? n[v] / l : Vector3.Zero;
        }
        return n;
    }

    /// <summary>Surface samples with their normals and bone weights (4 each, strongest first).</summary>
    sealed class SampleSet
    {
        public Vector3[] P = new Vector3[1024], N = new Vector3[1024];
        public int[] B = new int[4096];
        public float[] W = new float[4096];
        public int Count;
        readonly int _slotBone;
        public SampleSet(int boneCount) { _slotBone = boneCount; }

        public int Add(Vector3 p, Vector3 n)
        {
            if (Count == P.Length)
            {
                int c = P.Length * 2;
                Array.Resize(ref P, c); Array.Resize(ref N, c); Array.Resize(ref B, c * 4); Array.Resize(ref W, c * 4);
            }
            P[Count] = p; N[Count] = n;
            for (int q = 0; q < 4; q++) { B[Count * 4 + q] = _slotBone; W[Count * 4 + q] = 0f; }
            return Count++;
        }
    }

    static long CellKey(Vector3 p, float cell, int octant)
    {
        long x = (long)MathF.Floor(p.X / cell), y = (long)MathF.Floor(p.Y / cell), z = (long)MathF.Floor(p.Z / cell);
        return ((x & 0xFFFFF) << 43) | ((y & 0xFFFFF) << 23) | ((z & 0xFFFFF) << 3) | (long)octant;
    }

    static int Octant(Vector3 n) => (n.X < 0f ? 1 : 0) | (n.Y < 0f ? 2 : 0) | (n.Z < 0f ? 4 : 0);

    /// <summary>Fills the samples; returns the spacing it used (wider than asked on a fine mesh over a big area).</summary>
    static float Sample(Vector3[] pos, Vector3[] nrm, int[] tris, SkinData skin, float spacing, int maxSamples, SampleSet into)
    {
        int nv = pos.Length;
        var used = new bool[nv];
        double area = 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv) continue;
            used[a] = used[b] = used[c] = true;
            area += 0.5 * Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]).Length();
        }
        // a fine mesh over a big area: coarser cells, so the count stays near the cap (two layers and both sides counted)
        double est = area / ((double)spacing * spacing);
        if (est * 2.0 > maxSamples) spacing *= (float)Math.Sqrt(est * 2.0 / maxSamples);
        var seen = new Dictionary<long, int>();
        bool Take(Vector3 p, Vector3 n)
        {
            if (into.Count >= maxSamples || !(n.LengthSquared() > 0.25f) || !float.IsFinite(p.X + p.Y + p.Z)) return false;
            long key = CellKey(p, spacing, Octant(n));
            if (seen.ContainsKey(key)) return false;
            seen[key] = into.Count;
            return true;
        }
        // the vertices first: their weights are exact
        for (int v = 0; v < nv; v++)
        {
            if (!used[v] || !Take(pos[v], nrm[v])) continue;
            int k = into.Add(pos[v], nrm[v]);
            CopyWeights(skin, v, into, k);
        }
        // points inside long triangles
        Span<int> mb = stackalloc int[12];
        Span<float> mw = stackalloc float[12];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            if ((uint)a >= (uint)nv || (uint)b >= (uint)nv || (uint)c >= (uint)nv) continue;
            float longest = MathF.Max(Vector3.Distance(pos[a], pos[b]), MathF.Max(Vector3.Distance(pos[b], pos[c]), Vector3.Distance(pos[c], pos[a])));
            if (!(longest > spacing) || !float.IsFinite(longest)) continue;
            int steps = Math.Min(64, (int)MathF.Ceiling(longest / spacing));
            var face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
            float fl = face.Length();
            if (!(fl > 1e-20f)) continue;
            face /= fl;
            for (int i = 0; i <= steps; i++)
                for (int j = 0; i + j <= steps; j++)
                {
                    if ((i == 0 && j == 0) || i == steps || j == steps) continue;   // the corners are vertices
                    float u = i / (float)steps, w = j / (float)steps, x = 1f - u - w;
                    var p = pos[a] * x + pos[b] * u + pos[c] * w;
                    var n = nrm[a] * x + nrm[b] * u + nrm[c] * w;
                    float nl = n.Length();
                    n = nl > 1e-6f ? n / nl : face;
                    if (!Take(p, n)) continue;
                    int k = into.Add(p, n);
                    int cnt = 0;
                    Merge(skin, a, x, mb, mw, ref cnt); Merge(skin, b, u, mb, mw, ref cnt); Merge(skin, c, w, mb, mw, ref cnt);
                    Strongest(mb, mw, cnt, into, k);
                }
            if (into.Count >= maxSamples) break;
        }
        return spacing;
    }

    static void CopyWeights(SkinData skin, int v, SampleSet into, int k)
    {
        Span<int> mb = stackalloc int[4];
        Span<float> mw = stackalloc float[4];
        int cnt = 0;
        for (int q = 0; q < 4; q++)
        {
            float w = skin.VertexWeights[v * 4 + q];
            if (!(w > 0f)) continue;
            mb[cnt] = skin.VertexBones[v * 4 + q]; mw[cnt] = w; cnt++;
        }
        Strongest(mb, mw, cnt, into, k);
    }

    static void Merge(SkinData skin, int v, float f, Span<int> mb, Span<float> mw, ref int cnt)
    {
        if (!(f > 0f)) return;
        for (int q = 0; q < 4; q++)
        {
            float w = skin.VertexWeights[v * 4 + q] * f;
            if (!(w > 0f)) continue;
            int b = skin.VertexBones[v * 4 + q];
            int at = -1;
            for (int e = 0; e < cnt; e++) if (mb[e] == b) { at = e; break; }
            if (at >= 0) mw[at] += w;
            else if (cnt < mb.Length) { mb[cnt] = b; mw[cnt] = w; cnt++; }
        }
    }

    /// <summary>The 4 strongest of cnt weights, normalised, strongest first (none: the slot bone).</summary>
    static void Strongest(Span<int> mb, Span<float> mw, int cnt, SampleSet into, int k)
    {
        // a few entries: selection sort
        for (int x = 0; x < cnt; x++)
        {
            int best = x;
            for (int y = x + 1; y < cnt; y++) if (mw[y] > mw[best]) best = y;
            if (best != x) { (mb[x], mb[best]) = (mb[best], mb[x]); (mw[x], mw[best]) = (mw[best], mw[x]); }
        }
        int take = Math.Min(4, cnt);
        float sum = 0f;
        for (int q = 0; q < take; q++) sum += mw[q];
        if (!(sum > 1e-8f)) { into.W[k * 4] = 1f; return; }
        for (int q = 0; q < take; q++) { into.B[k * 4 + q] = mb[q]; into.W[k * 4 + q] = mw[q] / sum; }
    }

    /// <summary>
    /// The largest ball inside the surface that touches sample i (at most rmax). Each sample q across the inside from p
    /// bounds it twice over: the ball may not hold q (|p-q|^2 / (2 (p-q).n)), and where q's surface faces back at p (p
    /// behind q's plane, the two normals at least 60 degrees apart) it may not cross q's tangent plane either
    /// ((q-p).n_q / (1 - n_p.n_q): half a slab's thickness, or the distance to a box's edge, however the samples on the far
    /// side happen to lie; on a round part it is the radius, as before). Samples on a layer under another (<paramref
    /// name="under"/>: skin under clothes, a scalp under hair) bound nothing, so the outer layer's balls are not squeezed by
    /// what is inside it. A sample facing the other way on p's own surface nearby (a two-sided sheet, or two parts pressed
    /// together) means no ball.
    /// </summary>
    static float Ball(SampleSet s, PointGrid grid, bool[] under, int i, float rmax, float spacing)
    {
        var p = s.P[i]; var n = s.N[i];
        var P = s.P; var N = s.N;
        float same = 0.25f * spacing, same2 = same * same;
        float near = 2f * spacing, near2 = near * near;
        float local2 = 2.25f * spacing * spacing;
        int x0, x1, y0, y1, z0, z1;
        // a sample facing the other way on p's own surface nearby: a two-sided sheet (its two sides' samples need not
        // line up)
        grid.Range(p, near, out x0, out x1, out y0, out y1, out z0, out z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!grid.Cell(x, y, z, out int start, out int count)) continue;
                    for (int k = start; k < start + count; k++)
                    {
                        int j = grid.Index[k];
                        if (j == i || !(Vector3.Dot(N[j], n) < -0.5f)) continue;
                        var d = p - P[j];
                        if (d.LengthSquared() < near2 && MathF.Abs(Vector3.Dot(d, n)) < same) return 0f;
                    }
                }
        var c = p - n * rmax;
        float best = rmax;
        // a little wider than the ball, so a sample on a cell's edge is never missed through rounding
        grid.Range(c, rmax * 1.01f + same, out x0, out x1, out y0, out y1, out z0, out z1);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!grid.Cell(x, y, z, out int start, out int count)) continue;
                    for (int k = start; k < start + count; k++)
                    {
                        int j = grid.Index[k];
                        if (j == i || under[j]) continue;
                        var d = p - P[j];
                        float d2 = d.LengthSquared();
                        if (d2 < same2) continue;                     // the same spot (a split vertex)
                        float dn = Vector3.Dot(d, n);
                        if (!(dn > 0f)) continue;                     // on or above p's tangent plane: never inside the ball
                        float qd = Vector3.Dot(N[j], d);              // above 0: p in front of q's own surface
                        float l = MathF.Sqrt(d2);
                        float r = d2 / (2f * dn);
                        // q's plane as well, where q faces at least 60 degrees away from p (the far face, or the side past an
                        // edge; p exactly on that plane is on the edge itself: no ball), and only if the ball would touch that
                        // plane right at q: further off, a body's surface has curved away from it (a surface facing about the
                        // same way as p is p's own layer curving round, or one under it: its plane says nothing either)
                        if (qd <= 0.01f * l)
                        {
                            float den = 1f - Vector3.Dot(N[j], n);
                            if (den >= 0.5f)
                            {
                                float rp = MathF.Max(0f, -qd) / den;
                                if (rp < r && Vector3.DistanceSquared(p - n * rp + N[j] * rp, P[j]) <= local2) r = rp;
                            }
                        }
                        if (r < best) best = r;
                    }
                }
        return best;
    }

    /// <summary>Whether a sample lies on a layer under another (skin under clothes, a scalp under hair, an arm in a sleeve,
    /// a piece of the mesh pushed into another): the first triangle straight out from it, within reach, faces the same way,
    /// so going out it leaves another part's inside there.</summary>
    static bool Covered(TriangleBvh bvh, Vector3 x, Vector3 n, float reach, List<int> hits)
    {
        if (!(n.LengthSquared() > 0.25f)) return false;
        float eps = 1e-3f * reach;
        var o = x + n * eps; var e = x + n * reach;
        hits.Clear();
        bvh.Query(Vector3.Min(o, e) - new Vector3(eps), Vector3.Max(o, e) + new Vector3(eps), hits);
        float first = float.MaxValue;
        bool leaves = false;
        foreach (int t in hits)
        {
            var a = bvh.A[t]; var e1 = bvh.B[t] - a; var e2 = bvh.C[t] - a;
            var pv = Vector3.Cross(n, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-20f) continue;
            float id = 1f / det;
            var tv = o - a;
            float u = Vector3.Dot(tv, pv) * id;
            if (u < -1e-4f || u > 1.0001f) continue;   // a hair over the edges: no ray slips through between two triangles
            var qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(n, qv) * id;
            if (v < -1e-4f || u + v > 1.0001f) continue;
            float d = Vector3.Dot(e2, qv) * id;
            if (!(d > 0f) || d > reach || d >= first) continue;
            first = d;
            leaves = Vector3.Dot(Vector3.Cross(e1, e2), n) > 0f;
        }
        return leaves;
    }

    /// <summary>Whether the part behind sample i is closed: some sample behind p, within reach and near the line in along
    /// -n (within <paramref name="lateral"/> plus a fifth of the way in, as the far side may lie at a slant), faces back out
    /// (as a limb's far side, or a body's back, does). A sheet modelled one-sided (a wing's membrane, a cape, a skirt) has
    /// none: what lies behind it is air, and it gets no ball.</summary>
    static bool Closed(SampleSet s, PointGrid grid, int i, float reach, float lateral, float step)
    {
        var p = s.P[i]; var n = s.N[i];
        var P = s.P; var N = s.N;
        float half = 0.5f * step;
        for (float t = half; t - half < reach; t += step)
        {
            grid.Range(p - n * t, half + lateral + 0.2f * (t + half), out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!grid.Cell(x, y, z, out int start, out int count)) continue;
                        for (int k = start; k < start + count; k++)
                        {
                            int j = grid.Index[k];
                            if (!(Vector3.Dot(N[j], n) < -0.3f)) continue;
                            var d = p - P[j];
                            float along = Vector3.Dot(d, n);
                            if (!(along > 0f) || along > reach) continue;
                            float lat = lateral + 0.2f * along;
                            if (d.LengthSquared() - along * along < lat * lat) return true;
                        }
                    }
        }
        return false;
    }

    /// <summary>Greedy cover: the candidates biggest first, each kept unless its sample is already within tol of the
    /// spheres kept so far; a kept sphere covers every candidate within its radius plus tol.</summary>
    static List<int> Cover(SampleSet s, PointGrid grid, List<int> cand, float[] ball, bool[] isCand, bool[] covered, float tol)
    {
        Array.Clear(covered);
        var kept = new List<int>();
        var P = s.P; var N = s.N;
        foreach (int i in cand)
        {
            if (covered[i]) continue;
            kept.Add(i);
            float r = ball[i];
            var c = P[i] - N[i] * r;
            float reach = r + tol, reach2 = reach * reach;
            grid.Range(c, reach, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!grid.Cell(x, y, z, out int start, out int count)) continue;
                        for (int k = start; k < start + count; k++)
                        {
                            int j = grid.Index[k];
                            if (isCand[j] && !covered[j] && Vector3.DistanceSquared(P[j], c) <= reach2) covered[j] = true;
                        }
                    }
            covered[i] = true;
        }
        return kept;
    }

    /// <summary>Points sorted into cubic cells, found through an open-addressing table (key -> run in Index).</summary>
    sealed class PointGrid
    {
        public readonly int[] Index;
        readonly float _cell;
        readonly long[] _key;
        readonly int[] _start, _count;
        readonly int _mask;

        public PointGrid(Vector3[] p, int n, float cell)
        {
            _cell = cell > 1e-12f && float.IsFinite(cell) ? cell : 1f;
            var keys = new long[n];
            Index = new int[n];
            for (int i = 0; i < n; i++) { keys[i] = Key(Fl(p[i].X), Fl(p[i].Y), Fl(p[i].Z)); Index[i] = i; }
            Array.Sort(keys, Index);
            int unique = 0;
            for (int i = 0; i < n; i++) if (i == 0 || keys[i] != keys[i - 1]) unique++;
            int cap = 16;
            while (cap < unique * 2) cap <<= 1;
            _mask = cap - 1;
            _key = new long[cap]; _start = new int[cap]; _count = new int[cap];
            Array.Fill(_key, long.MinValue);
            for (int i = 0; i < n;)
            {
                int j = i;
                while (j < n && keys[j] == keys[i]) j++;
                int h = Slot(keys[i]);
                while (_key[h] != long.MinValue) h = (h + 1) & _mask;
                _key[h] = keys[i]; _start[h] = i; _count[h] = j - i;
                i = j;
            }
        }

        int Fl(float v) => (int)MathF.Floor(v / _cell);
        static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);
        int Slot(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 40) & _mask;

        public void Range(Vector3 c, float r, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
        {
            x0 = Fl(c.X - r); x1 = Fl(c.X + r);
            y0 = Fl(c.Y - r); y1 = Fl(c.Y + r);
            z0 = Fl(c.Z - r); z1 = Fl(c.Z + r);
        }

        public bool Cell(int x, int y, int z, out int start, out int count)
        {
            long key = Key(x, y, z);
            int h = Slot(key);
            while (true)
            {
                long k = _key[h];
                if (k == key) { start = _start[h]; count = _count[h]; return true; }
                if (k == long.MinValue) { start = count = 0; return false; }
                h = (h + 1) & _mask;
            }
        }
    }
}

/// <summary>
/// 0.29.2: one body shape near a cloth this frame, posed by its bones (filled on the world thread, read by the step).
/// Skin and Last are the cloth's own copies, so the world thread can pose the next frame while the step runs.
/// </summary>
public sealed class ClothBody
{
    public BodyShape Shape;
    /// <summary>Skin matrices (bind space to world, BoneCount + 1) at the end of this frame, and a frame earlier.</summary>
    public Matrix4x4[] Skin = Array.Empty<Matrix4x4>(), Last = Array.Empty<Matrix4x4>();
    /// <summary>Last is valid; LastDt is the seconds between the two.</summary>
    public bool HasLast;
    public float LastDt;
    /// <summary>Like FleshCollider.Source (not 0: a player).</summary>
    public byte Source = 4;
    /// <summary>Per bone (BoneCount + 1), or null: spheres that mostly follow a bone marked here are left out (the
    /// cloth's own anchors).</summary>
    public bool[] SkipBone;
    /// <summary>0.30.0, per sphere or null: spheres left out (avatar cloth: those fitted against the cloth itself).</summary>
    public bool[] SkipSphere;
    /// <summary>0.30.0: the wearer's own body (avatar cloth). A point is pushed out of these spheres no further than the
    /// animation puts it, so a tight skirt over thick thighs is not puffed out, while a leg lifted into it still pushes.</summary>
    public bool Own;
    /// <summary>0.30.0: who and which skinned mesh renderer these spheres belong to (the plugin's objects; for avatar
    /// cloth to find the wearer's own).</summary>
    public object User, Renderer;

    public void Set(BodyShape shape, ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<Matrix4x4> last, float lastDt, byte source)
    {
        Shape = shape;
        if (Skin.Length != skin.Length) Skin = new Matrix4x4[skin.Length];
        skin.CopyTo(Skin);
        HasLast = last.Length == skin.Length && lastDt > 1e-4f && float.IsFinite(lastDt);
        if (HasLast)
        {
            if (Last.Length != last.Length) Last = new Matrix4x4[last.Length];
            last.CopyTo(Last);
        }
        LastDt = lastDt;
        Source = source;
        SkipBone = null;
        SkipSphere = null;
        Own = false;
        User = Renderer = null;
    }
}
