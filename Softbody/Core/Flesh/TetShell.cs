using System.Diagnostics;
using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// The soft shell under the sim surface: each surface point gets a partner moved inward toward its own bones (a fraction
/// <see cref="FleshSettings.Depth"/> of the way, never past the bone, smoothed so the inner surface is calm), with
/// <see cref="FleshSettings.Layers"/> layers in between. Every surface triangle x layer is a prism, split into 3 tets
/// with each side face's diagonal picked from the two outer indices (low bottom to high top), so neighbouring prisms
/// always agree. The innermost layer and the pinned ring are kinematic; the rest is simulated.
///
/// Shell vertex i is layer <c>i / OuterCount</c> of outer point <c>i % OuterCount</c> (layer 0 = the skin).
/// </summary>
public sealed class TetShell
{
    public int OuterCount { get; private set; }
    public int Layers { get; private set; }
    public int VertexCount => OuterCount * (Layers + 1);
    /// <summary>Bind-space rest positions of all shell vertices.</summary>
    public Vector3[] Rest { get; private set; }
    public bool[] Kinematic { get; private set; }
    /// <summary>Per outer point: the shell thickness (bind space) and the point on its bones it was pushed toward.</summary>
    public float[] Thickness { get; private set; }
    public Vector3[] BonePoint { get; private set; }
    /// <summary>4 vertex indices per tet, ordered so the bind-space volume is positive.</summary>
    public int[] Tets { get; private set; }
    public int TetCount => Tets.Length / 4;
    /// <summary>Per tet, 0 at the skin .. 1 at the bone side (its layer's middle depth).</summary>
    public float[] TetDepth { get; private set; }

    public int ThinningRounds { get; private set; }
    public int DroppedTets { get; private set; }
    public double BuildMs { get; private set; }

    public int Outer(int shellVertex) => shellVertex % OuterCount;
    public int Layer(int shellVertex) => shellVertex / OuterCount;

    /// <param name="bones">4 bone indices per outer point (Skin.BoneCount = no bone)</param>
    /// <param name="weights">4 weights per outer point</param>
    public static TetShell Build(SimSurface s, int[] bones, float[] weights, Vector3[] heads, Vector3[] tails, FleshSettings fs)
    {
        var sw = Stopwatch.StartNew();
        var sh = new TetShell { OuterCount = s.Count, Layers = fs.Layers };
        int n = s.Count, L = fs.Layers;

        // ---- how far each point may go: toward the closest point on its own (weighted) bones
        var h = new float[n]; var cap = new float[n]; var bp = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var p = s.Pos[i];
            Vector3 c = Vector3.Zero; float wsum = 0;
            for (int q = 0; q < 4; q++)
            {
                int b = bones[i * 4 + q]; float w = weights[i * 4 + q];
                if (!(w > 0f) || (uint)b >= (uint)heads.Length) continue;
                c += ClosestOnSegment(heads[b], tails[b], p) * w; wsum += w;
            }
            float fallback = 2f * s.MeanEdge;
            if (!(wsum > 0f)) { bp[i] = p - s.Nrm[i] * fallback; cap[i] = fallback; h[i] = fs.Depth * fallback; continue; }
            c /= wsum; bp[i] = c;
            var d = c - p; float dist = d.Length();
            // only the part of the way to the bone that lies under the skin counts: a point whose bone lies beside it
            // (a flap, fluff, an ear) gets a thin shell instead of one pushed sideways. Moving inward by h < 2 x along
            // always brings the point closer to its bone.
            float along = MathF.Max(0f, Vector3.Dot(d, -s.Nrm[i]));
            float floor = 0.05f * s.VertexEdge[i];
            cap[i] = MathF.Max(floor, MathF.Min(0.9f * dist, 1.5f * along));
            h[i] = MathF.Max(floor, fs.Depth * along);
            if (!(dist > 1e-7f)) { cap[i] = fallback; h[i] = fs.Depth * fallback; }
        }
        // smooth the thickness so the inner surface does not copy the bone layout cell by cell
        var tmp = new float[n];
        for (int it = 0; it < 4; it++)
        {
            for (int i = 0; i < n; i++)
            {
                float acc = 0; int cnt = 0;
                for (int k = s.NbrStart[i]; k < s.NbrStart[i + 1]; k++) { acc += h[s.Nbr[k]]; cnt++; }
                tmp[i] = MathF.Min(cap[i], cnt > 0 ? 0.5f * h[i] + 0.5f * acc / cnt : h[i]);
            }
            (h, tmp) = (tmp, h);
        }
        // and long thin tets are hard to solve: at most 4 edge lengths per layer
        for (int i = 0; i < n; i++) h[i] = MathF.Min(h[i], 4f * L * s.VertexEdge[i]);

        // ---- tets, with the orientation each would have in an ideal (flat) prism
        var tris = s.Tris; int nt = tris.Length / 3;
        var tets = new List<int>(nt * 3 * L); var tetTri = new List<int>(nt * 3 * L); var tetLayer = new List<int>(nt * 3 * L);
        for (int t = 0; t < nt; t++)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            Sort3(ref a, ref b, ref c);
            for (int l = 0; l < L; l++)
            {
                int o0 = l * n, o1 = (l + 1) * n;
                // bottom (a,b,c) at layer l, top (a',b',c') at layer l+1; side diagonals run from the lower outer index at
                // the bottom to the higher one at the top
                Add(tets, a + o0, b + o0, c + o0, c + o1); tetTri.Add(t); tetLayer.Add(l);
                Add(tets, a + o0, b + o0, b + o1, c + o1); tetTri.Add(t); tetLayer.Add(l);
                Add(tets, a + o0, a + o1, b + o1, c + o1); tetTri.Add(t); tetLayer.Add(l);
            }
        }
        var T = tets.ToArray(); int ntet = T.Length / 4;
        // orient by the flat prism: shell vertices pushed along the face normal by a tiny step
        // 0.11.3: the four corners live outside the loop. A stackalloc inside it grew the stack by 48 bytes per tet until the
        // method returned, and 4000 points (about 24,000 tets) overflowed the worker thread's stack and took Resonite down.
        Span<Vector3> corners = stackalloc Vector3[4];
        for (int k = 0; k < ntet; k++)
        {
            int t = tetTri[k];
            var pa = s.Pos[tris[t * 3]]; var pb = s.Pos[tris[t * 3 + 1]]; var pc = s.Pos[tris[t * 3 + 2]];
            var fn = Vector3.Cross(pb - pa, pc - pa);
            var avg = s.Nrm[tris[t * 3]] + s.Nrm[tris[t * 3 + 1]] + s.Nrm[tris[t * 3 + 2]];
            if (Vector3.Dot(fn, avg) < 0f) fn = -fn;
            fn = fn.LengthSquared() > 0f ? Vector3.Normalize(fn) : Vector3.UnitY;
            float step = 0.1f * s.MeanEdge;
            for (int j = 0; j < 4; j++) { int v = T[k * 4 + j]; corners[j] = s.Pos[v % n] - fn * step * (v / n); }
            if (SignedVolume(corners[0], corners[1], corners[2], corners[3]) < 0f) (T[k * 4 + 2], T[k * 4 + 3]) = (T[k * 4 + 3], T[k * 4 + 2]);
        }

        // ---- place the layers; thin the shell where a tet comes out inverted or flat, and retry
        var rest = new Vector3[n * (L + 1)];
        var bad = new bool[ntet];
        int rounds = 0, badCount = 0;
        for (; rounds < 12; rounds++)
        {
            for (int l = 0; l <= L; l++)
                for (int i = 0; i < n; i++) rest[l * n + i] = s.Pos[i] - s.Nrm[i] * (h[i] * l / L);
            badCount = 0;
            var thin = new bool[n];
            for (int k = 0; k < ntet; k++)
            {
                int i0 = T[k * 4], i1 = T[k * 4 + 1], i2 = T[k * 4 + 2], i3 = T[k * 4 + 3];
                float vol = SignedVolume(rest[i0], rest[i1], rest[i2], rest[i3]);
                float e = (s.VertexEdge[i0 % n] + s.VertexEdge[i1 % n] + s.VertexEdge[i2 % n] + s.VertexEdge[i3 % n]) * 0.25f;
                float minVol = 0.01f * e * e * MathF.Min(e, MathF.Max(1e-6f, (h[i0 % n] + h[i3 % n]) * 0.5f / L)) / 6f;
                bad[k] = !(vol > minVol);
                if (!bad[k]) continue;
                badCount++;
                thin[i0 % n] = thin[i1 % n] = thin[i2 % n] = thin[i3 % n] = true;
            }
            if (badCount == 0) break;
            for (int i = 0; i < n; i++) if (thin[i]) h[i] *= 0.7f;
        }
        // whatever is still bad is dropped (its points keep their follow springs and the other tets)
        var keep = new List<int>(ntet * 4); var depth = new List<float>(ntet);
        int dropped = 0;
        for (int k = 0; k < ntet; k++)
        {
            if (bad[k]) { dropped++; continue; }
            for (int j = 0; j < 4; j++) keep.Add(T[k * 4 + j]);
            depth.Add((tetLayer[k] + 0.5f) / L);
        }

        var kin = new bool[n * (L + 1)];
        for (int i = 0; i < n; i++)
        {
            kin[L * n + i] = true;
            if (s.Pinned[i]) for (int l = 0; l <= L; l++) kin[l * n + i] = true;
        }
        sh.Rest = rest; sh.Kinematic = kin; sh.Thickness = h; sh.BonePoint = bp;
        sh.Tets = keep.ToArray(); sh.TetDepth = depth.ToArray();
        sh.ThinningRounds = rounds; sh.DroppedTets = dropped;
        sh.BuildMs = sw.Elapsed.TotalMilliseconds;
        return sh;
    }

    static void Add(List<int> l, int a, int b, int c, int d) { l.Add(a); l.Add(b); l.Add(c); l.Add(d); }

    static void Sort3(ref int a, ref int b, ref int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
    }

    public static float SignedVolume(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        Vector3.Dot(b - a, Vector3.Cross(c - a, d - a)) / 6f;

    public static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        if (!(l2 > 1e-20f)) return a;
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f);
        return a + ab * t;
    }
}
