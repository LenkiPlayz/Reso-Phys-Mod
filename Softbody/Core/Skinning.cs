using System.Numerics;

namespace Softbody.Core;

/// <summary>
/// Linear-blend skinning data of a mesh, in the engine's convention (checked in IL, see README "Skinned meshes"):
/// <c>world(v) = sum_i w_i * BoneSlot_i.LocalToGlobal * BindPose_i * v</c>, i.e. a bind pose maps mesh (bind) space to
/// the bone's local space, and the bone slot's global transform maps that to world space. Blendshapes are added to
/// the base positions before skinning.
///
/// Matrices here are System.Numerics row-vector matrices (<c>p' = Vector3.Transform(p, M)</c>), so a skin matrix
/// is <c>S_i = BindPose_i * BoneWorld_i</c> (bind pose applied first). Index <see cref="SlotBone"/> (= BoneCount)
/// is a virtual extra bone: the renderer slot itself. Vertices without any weight are bound to it, and a plain
/// MeshRenderer is the special case "no bones, everything on the slot bone".
/// </summary>
public sealed class SkinData
{
    /// <summary>Bones of the mesh (bind poses); the skin matrix array has BoneCount + 1 entries.</summary>
    public readonly int BoneCount;
    /// <summary>4 bone indices per vertex (0..BoneCount; unused slots point at a bone with weight 0).</summary>
    public readonly int[] VertexBones;
    /// <summary>4 weights per vertex, normalised to sum 1.</summary>
    public readonly float[] VertexWeights;
    public int VertexCount => VertexWeights.Length / 4;
    public int SlotBone => BoneCount;
    public int MatrixCount => BoneCount + 1;
    /// <summary>Vertices that had no usable weight and were bound to the slot bone.</summary>
    public int UnboundVertices { get; private set; }

    SkinData(int boneCount, int[] bones, float[] weights) { BoneCount = boneCount; VertexBones = bones; VertexWeights = weights; }

    /// <summary>
    /// From the mesh's bone bindings (4 per vertex, as Elements.Assets.BoneBinding). Indices outside 0..boneCount-1,
    /// and weights that are not finite or not positive, are dropped; the rest are normalised. Vertices left with no
    /// weight go to the slot bone.
    /// </summary>
    public static SkinData FromBindings(int boneCount, ReadOnlySpan<int> bones4, ReadOnlySpan<float> weights4)
    {
        if (boneCount < 0) throw new ArgumentException("negative bone count");
        int n = Math.Min(bones4.Length, weights4.Length) / 4;
        var b = new int[n * 4]; var w = new float[n * 4];
        int unbound = 0;
        for (int v = 0; v < n; v++)
        {
            int o = v * 4;
            float sum = 0;
            for (int q = 0; q < 4; q++)
            {
                int bi = bones4[o + q]; float wi = weights4[o + q];
                if ((uint)bi >= (uint)boneCount || !float.IsFinite(wi) || !(wi > 0f)) { b[o + q] = boneCount; w[o + q] = 0f; continue; }
                b[o + q] = bi; w[o + q] = wi; sum += wi;
            }
            if (sum > 1e-6f) { float inv = 1f / sum; for (int q = 0; q < 4; q++) w[o + q] *= inv; }
            else { unbound++; b[o] = boneCount; w[o] = 1f; for (int q = 1; q < 4; q++) { b[o + q] = boneCount; w[o + q] = 0f; } }
        }
        return new SkinData(boneCount, b, w) { UnboundVertices = unbound };
    }

    /// <summary>No bones: every vertex follows the slot (the static MeshRenderer case).</summary>
    public static SkinData Rigid(int vertexCount)
    {
        var b = new int[vertexCount * 4]; var w = new float[vertexCount * 4];
        for (int v = 0; v < vertexCount; v++) w[v * 4] = 1f;
        return new SkinData(0, b, w) { UnboundVertices = vertexCount };
    }

    /// <summary>Blended skin matrix of vertex v (affine: the weights sum to 1).</summary>
    public Matrix4x4 VertexMatrix(ReadOnlySpan<Matrix4x4> skin, int v)
    {
        int o = v * 4;
        return Lbs.Blend(skin, VertexBones[o], VertexWeights[o], VertexBones[o + 1], VertexWeights[o + 1],
                         VertexBones[o + 2], VertexWeights[o + 2], VertexBones[o + 3], VertexWeights[o + 3]);
    }

    /// <summary>CPU skinning of every vertex: dst[v] = skin(src[v]) (what the renderer does, without blendshapes).</summary>
    public void SkinPositions(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<Vector3> src, Span<Vector3> dst)
    {
        int n = Math.Min(VertexCount, Math.Min(src.Length, dst.Length));
        var vb = VertexBones; var vw = VertexWeights;
        for (int v = 0; v < n; v++)
        {
            int o = v * 4;
            var p = src[v];
            // most vertices have 1-2 real influences: skip zero weights
            Vector3 r = Vector3.Transform(p, skin[vb[o]]) * vw[o];
            float w1 = vw[o + 1], w2 = vw[o + 2], w3 = vw[o + 3];
            if (w1 != 0f) r += Vector3.Transform(p, skin[vb[o + 1]]) * w1;
            if (w2 != 0f) r += Vector3.Transform(p, skin[vb[o + 2]]) * w2;
            if (w3 != 0f) r += Vector3.Transform(p, skin[vb[o + 3]]) * w3;
            dst[v] = r;
        }
    }

    /// <summary>
    /// CPU skinning of a whole mesh as the renderer's GPU skinning does it: positions by the blended matrix, normals and
    /// tangents (xyz; w kept) by its linear part, then normalised. Returns the bounds of the skinned positions.
    /// <paramref name="srcTan"/>/<paramref name="dstTan"/> may be null. Splits the work over the thread pool when
    /// <paramref name="parallel"/> (the calling thread waits).
    /// </summary>
    public void SkinMesh(Matrix4x4[] skin, Vector3[] srcPos, Vector3[] srcNrm, Vector4[] srcTan,
                         Vector3[] dstPos, Vector3[] dstNrm, Vector4[] dstTan, bool parallel, out Vector3 min, out Vector3 max)
    {
        int n = VertexCount;
        const int Chunk = 4096;
        int chunks = (n + Chunk - 1) / Chunk;
        var mins = new Vector3[chunks]; var maxs = new Vector3[chunks];
        void Run(int c)
        {
            int start = c * Chunk, end = Math.Min(n, start + Chunk);
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            var vb = VertexBones; var vw = VertexWeights;
            bool tan = srcTan != null && dstTan != null;
            for (int v = start; v < end; v++)
            {
                int o = v * 4;
                var m = skin[vb[o]] * vw[o];
                float w1 = vw[o + 1], w2 = vw[o + 2], w3 = vw[o + 3];
                if (w1 != 0f) m += skin[vb[o + 1]] * w1;
                if (w2 != 0f) m += skin[vb[o + 2]] * w2;
                if (w3 != 0f) m += skin[vb[o + 3]] * w3;
                var p = Vector3.Transform(srcPos[v], m);
                dstPos[v] = p;
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                var nn = Vector3.TransformNormal(srcNrm[v], m);
                float l = nn.LengthSquared();
                dstNrm[v] = l > 1e-20f ? nn / MathF.Sqrt(l) : srcNrm[v];
                if (tan)
                {
                    var t = srcTan[v];
                    var tt = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), m);
                    float lt = tt.LengthSquared();
                    if (lt > 1e-20f) tt /= MathF.Sqrt(lt);
                    dstTan[v] = new Vector4(tt, t.W);
                }
            }
            mins[c] = lo; maxs[c] = hi;
        }
        if (parallel && chunks > 1) Parallel.For(0, chunks, Run);
        else for (int c = 0; c < chunks; c++) Run(c);
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        for (int c = 0; c < chunks; c++) { min = Vector3.Min(min, mins[c]); max = Vector3.Max(max, maxs[c]); }
    }
}

/// <summary>
/// Blendshape deltas for CPU evaluation, stored sparsely (only vertices a shape moves). The engine's own CPU blendshape
/// rule (Elements.Assets.MeshX.TransformVertexByBlendshapes, used by SkinnedMeshPositioner): frame 0 only,
/// <c>p += w * dp</c>, <c>n += w * dn</c>, <c>t.xyz += w * dt</c>, weights straight from the renderer's BlendShapeWeights.
/// </summary>
public sealed class BlendshapeDeltas
{
    public int Count => _idx.Length;
    /// <summary>Stored (vertex, shape) deltas in all.</summary>
    public long StoredDeltas { get; }
    readonly int[][] _idx;
    readonly Vector3[][] _dp, _dn, _dt;

    /// <param name="frame">per shape (read one at a time): frame-0 position deltas (one per vertex; null = the shape has no
    /// frame), normal deltas and tangent deltas (either may be null).</param>
    public BlendshapeDeltas(int count, int vertexCount, Func<int, (Vector3[] pos, Vector3[] nrm, Vector3[] tan)> frame)
    {
        int nb = count;
        _idx = new int[nb][]; _dp = new Vector3[nb][]; _dn = new Vector3[nb][]; _dt = new Vector3[nb][];
        long stored = 0;
        var idx = new List<int>();
        for (int b = 0; b < nb; b++)
        {
            var (p, nrm, tan) = frame(b);
            idx.Clear();
            for (int v = 0; v < vertexCount; v++)
            {
                bool any = p != null && v < p.Length && p[v] != Vector3.Zero;
                if (!any && nrm != null && v < nrm.Length && nrm[v] != Vector3.Zero) any = true;
                if (!any && tan != null && v < tan.Length && tan[v] != Vector3.Zero) any = true;
                if (any) idx.Add(v);
            }
            _idx[b] = idx.ToArray();
            _dp[b] = new Vector3[idx.Count];
            if (nrm != null) _dn[b] = new Vector3[idx.Count];
            if (tan != null) _dt[b] = new Vector3[idx.Count];
            for (int k = 0; k < idx.Count; k++)
            {
                int v = idx[k];
                if (p != null && v < p.Length) _dp[b][k] = p[v];
                if (nrm != null && v < nrm.Length) _dn[b][k] = nrm[v];
                if (tan != null && v < tan.Length) _dt[b][k] = tan[v];
            }
            stored += idx.Count;
        }
        StoredDeltas = stored;
    }

    BlendshapeDeltas(int[][] idx, Vector3[][] dp) : this(idx, dp, new Vector3[idx.Length][], new Vector3[idx.Length][]) { }

    BlendshapeDeltas(int[][] idx, Vector3[][] dp, Vector3[][] dn, Vector3[][] dt)
    {
        _idx = idx; _dp = dp; _dn = dn; _dt = dt;
        long stored = 0; foreach (var a in idx) stored += a.Length;
        StoredDeltas = stored;
    }

    /// <summary>
    /// 0.26.3: position, normal and tangent deltas, given sparsely per shape: the vertices and their deltas, each array as
    /// long as the vertex list (positions null = all zero; normals or tangents null = the shape has none of that kind).
    /// </summary>
    public static BlendshapeDeltas FromSparse(int count, Func<int, (int[] vertices, Vector3[] pos, Vector3[] nrm, Vector3[] tan)> shape)
    {
        var idx = new int[count][]; var dp = new Vector3[count][]; var dn = new Vector3[count][]; var dt = new Vector3[count][];
        for (int b = 0; b < count; b++)
        {
            var (v, p, n, t) = shape(b);
            idx[b] = v ?? Array.Empty<int>();
            int len = idx[b].Length;
            if ((p != null && p.Length != len) || (n != null && n.Length != len) || (t != null && t.Length != len))
                throw new ArgumentException("vertex and delta counts differ");
            dp[b] = p ?? new Vector3[len]; dn[b] = n; dt[b] = t;
        }
        return new BlendshapeDeltas(idx, dp, dn, dt);
    }

    /// <summary>0.26.3: one shape's stored deltas, with its normal and tangent deltas (each null when the shape has none;
    /// shared, do not modify).</summary>
    public void GetShape(int shape, out int[] vertices, out Vector3[] positionDeltas, out Vector3[] normalDeltas, out Vector3[] tangentDeltas)
    {
        vertices = _idx[shape]; positionDeltas = _dp[shape]; normalDeltas = _dn[shape]; tangentDeltas = _dt[shape];
    }

    /// <summary>Position deltas only, given sparsely per shape (vertex indices and their deltas; null = the shape moves nothing).</summary>
    public static BlendshapeDeltas FromPositions(int count, Func<int, (int[] vertices, Vector3[] deltas)> shape)
    {
        var idx = new int[count][]; var dp = new Vector3[count][];
        for (int b = 0; b < count; b++)
        {
            var (v, d) = shape(b);
            idx[b] = v ?? Array.Empty<int>(); dp[b] = d ?? Array.Empty<Vector3>();
            if (idx[b].Length != dp[b].Length) throw new ArgumentException("vertex and delta counts differ");
        }
        return new BlendshapeDeltas(idx, dp);
    }

    /// <summary>The stored position deltas of one shape: the vertices it moves and their frame-0 deltas (shared, do not modify).</summary>
    public void GetShape(int shape, out int[] vertices, out Vector3[] positionDeltas)
    {
        vertices = _idx[shape]; positionDeltas = _dp[shape];
    }

    /// <summary>Adds the weighted deltas of every shape with a non-zero weight. Returns how many shapes were active.</summary>
    public int Apply(ReadOnlySpan<float> weights, Span<Vector3> pos, Span<Vector3> nrm, Span<Vector4> tan)
    {
        int active = 0;
        for (int b = 0; b < _idx.Length && b < weights.Length; b++)
        {
            float w = weights[b];
            if (!float.IsFinite(w) || MathF.Abs(w) <= 1e-6f) continue;
            active++;
            var idx = _idx[b]; var dp = _dp[b]; var dn = _dn[b]; var dt = _dt[b];
            for (int k = 0; k < idx.Length; k++)
            {
                int v = idx[k];
                pos[v] += dp[k] * w;
                if (dn != null && nrm.Length > 0) nrm[v] += dn[k] * w;
                if (dt != null && tan.Length > 0) { var d = dt[k] * w; tan[v] += new Vector4(d, 0f); }
            }
        }
        return active;
    }
}

/// <summary>Small linear-blend-skinning helpers (row-vector System.Numerics matrices).</summary>
public static class Lbs
{
    public static Matrix4x4 Blend(ReadOnlySpan<Matrix4x4> skin, int b0, float w0, int b1, float w1, int b2, float w2, int b3, float w3)
    {
        var m = skin[b0] * w0;
        if (w1 != 0f) m += skin[b1] * w1;
        if (w2 != 0f) m += skin[b2] * w2;
        if (w3 != 0f) m += skin[b3] * w3;
        m.M14 = 0f; m.M24 = 0f; m.M34 = 0f; m.M44 = 1f;
        return m;
    }

    /// <summary>Determinant of the linear (3x3) part.</summary>
    public static float LinearDeterminant(in Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>Uniform scale of the linear part: cbrt(|det|).</summary>
    public static float LinearScale(in Matrix4x4 m) => MathF.Cbrt(MathF.Abs(LinearDeterminant(m)));

    /// <summary>
    /// Inverse of the linear (3x3) part as a matrix with zero translation, so <c>Vector3.TransformNormal(d, inv)</c>
    /// maps a world-space offset back to bind space. False (and identity) when the part is singular or not finite:
    /// a blended matrix can collapse at a strongly bent joint, and such a point then gets no offset.
    /// </summary>
    public static bool TryInvertLinear(in Matrix4x4 m, out Matrix4x4 inv)
    {
        float a = m.M11, b = m.M12, c = m.M13, d = m.M21, e = m.M22, f = m.M23, g = m.M31, h = m.M32, i = m.M33;
        float A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        float det = a * A + b * B + c * C;
        // relative to the matrix's own size, so tiny (millimetre-scale) rigs are not rejected
        float s = MathF.Max(MathF.Abs(a) + MathF.Abs(b) + MathF.Abs(c), MathF.Max(MathF.Abs(d) + MathF.Abs(e) + MathF.Abs(f), MathF.Abs(g) + MathF.Abs(h) + MathF.Abs(i)));
        if (!float.IsFinite(det) || !(s > 0f) || MathF.Abs(det) < 1e-6f * s * s * s) { inv = Matrix4x4.Identity; return false; }
        float r = 1f / det;
        inv = new Matrix4x4(
            A * r, -(b * i - c * h) * r, (b * f - c * e) * r, 0f,
            B * r, (a * i - c * g) * r, -(a * f - c * d) * r, 0f,
            C * r, -(a * h - b * g) * r, (a * e - b * d) * r, 0f,
            0f, 0f, 0f, 1f);
        return float.IsFinite(inv.M11 + inv.M22 + inv.M33 + inv.M12 + inv.M13 + inv.M21 + inv.M23 + inv.M31 + inv.M32);
    }

    /// <summary>
    /// 0.10.1: maps a world offset back to bind space through the closest rotation to the linear part (its polar
    /// factor) and the given uniform scale: <c>inv = R^T / scale</c>. Unlike <see cref="TryInvertLinear"/> this never
    /// amplifies: a point blended between bones that are turned far apart (a hanging tail and the legs) has a shrunken
    /// matrix, whose plain inverse would blow a few centimetres of world offset up to tens of centimetres in bind space.
    /// False (and identity) when the part is singular, mirrored or not finite.
    /// </summary>
    public static bool TryInvertSimilar(in Matrix4x4 m, float scale, out Matrix4x4 inv)
    {
        inv = Matrix4x4.Identity;
        if (!(scale > 0f) || !float.IsFinite(scale)) return false;
        double x11 = m.M11, x12 = m.M12, x13 = m.M13, x21 = m.M21, x22 = m.M22, x23 = m.M23, x31 = m.M31, x32 = m.M32, x33 = m.M33;
        double size = Math.Max(Math.Abs(x11) + Math.Abs(x12) + Math.Abs(x13), Math.Max(Math.Abs(x21) + Math.Abs(x22) + Math.Abs(x23), Math.Abs(x31) + Math.Abs(x32) + Math.Abs(x33)));
        if (!(size > 0) || !double.IsFinite(size)) return false;
        // normalise, then Higham's scaled Newton iteration X <- (g X + X^-T / g) / 2 towards the orthogonal polar factor
        x11 /= size; x12 /= size; x13 /= size; x21 /= size; x22 /= size; x23 /= size; x31 /= size; x32 /= size; x33 /= size;
        for (int it = 0; it < 20; it++)
        {
            double c11 = x22 * x33 - x23 * x32, c12 = -(x21 * x33 - x23 * x31), c13 = x21 * x32 - x22 * x31;
            double c21 = -(x12 * x33 - x13 * x32), c22 = x11 * x33 - x13 * x31, c23 = -(x11 * x32 - x12 * x31);
            double c31 = x12 * x23 - x13 * x22, c32 = -(x11 * x23 - x13 * x21), c33 = x11 * x22 - x12 * x21;
            double det = x11 * c11 + x12 * c12 + x13 * c13;
            if (!(det > 1e-6)) return false; // singular (collapsed blend) or mirrored
            // X^-T = cofactor matrix / det
            double i11 = c11 / det, i12 = c12 / det, i13 = c13 / det, i21 = c21 / det, i22 = c22 / det, i23 = c23 / det, i31 = c31 / det, i32 = c32 / det, i33 = c33 / det;
            double fx = Math.Sqrt(x11 * x11 + x12 * x12 + x13 * x13 + x21 * x21 + x22 * x22 + x23 * x23 + x31 * x31 + x32 * x32 + x33 * x33);
            double fi = Math.Sqrt(i11 * i11 + i12 * i12 + i13 * i13 + i21 * i21 + i22 * i22 + i23 * i23 + i31 * i31 + i32 * i32 + i33 * i33);
            double g = Math.Sqrt(fi / fx);
            if (!double.IsFinite(g) || !(g > 0)) return false;
            double n11 = 0.5 * (g * x11 + i11 / g), n12 = 0.5 * (g * x12 + i12 / g), n13 = 0.5 * (g * x13 + i13 / g);
            double n21 = 0.5 * (g * x21 + i21 / g), n22 = 0.5 * (g * x22 + i22 / g), n23 = 0.5 * (g * x23 + i23 / g);
            double n31 = 0.5 * (g * x31 + i31 / g), n32 = 0.5 * (g * x32 + i32 / g), n33 = 0.5 * (g * x33 + i33 / g);
            double change = Math.Abs(n11 - x11) + Math.Abs(n12 - x12) + Math.Abs(n13 - x13) + Math.Abs(n21 - x21) + Math.Abs(n22 - x22) +
                            Math.Abs(n23 - x23) + Math.Abs(n31 - x31) + Math.Abs(n32 - x32) + Math.Abs(n33 - x33);
            x11 = n11; x12 = n12; x13 = n13; x21 = n21; x22 = n22; x23 = n23; x31 = n31; x32 = n32; x33 = n33;
            if (change < 1e-10) break;
        }
        double r = 1.0 / scale;
        // the inverse of a rotation is its transpose
        inv = new Matrix4x4(
            (float)(x11 * r), (float)(x21 * r), (float)(x31 * r), 0f,
            (float)(x12 * r), (float)(x22 * r), (float)(x32 * r), 0f,
            (float)(x13 * r), (float)(x23 * r), (float)(x33 * r), 0f,
            0f, 0f, 0f, 1f);
        return float.IsFinite(inv.M11 + inv.M12 + inv.M13 + inv.M21 + inv.M22 + inv.M23 + inv.M31 + inv.M32 + inv.M33);
    }

    public static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M21) && float.IsFinite(m.M22) &&
        float.IsFinite(m.M23) && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);

    /// <summary>The anchor's transform as a row-vector matrix (scale, then rotation, then translation).</summary>
    public static Matrix4x4 FromAnchor(in AnchorPose a) =>
        Matrix4x4.CreateScale(a.Scale) * Matrix4x4.CreateFromQuaternion(a.Rotation) * Matrix4x4.CreateTranslation(a.Position);
}

/// <summary>
/// Skin weights of the cage points: every point gets the bone weights of the vertices it carries, accumulated the same
/// way as softness (embedding weight x bone weight), keeping the 4 strongest and normalising. Points that carry no
/// vertex inherit from their neighbours along cage edges. Each frame a point's target is its linear-blend-skinned
/// rest position, and its blended matrix maps simulated world offsets back to bind space.
/// </summary>
public sealed class CageSkin
{
    /// <summary>Skin matrices expected per frame (bones + the slot bone).</summary>
    public readonly int MatrixCount;
    public readonly int[] PointBones;     // 4 per point
    public readonly float[] PointWeights; // 4 per point, sum 1
    /// <summary>Points that carried no vertex and inherited their weights from neighbours.</summary>
    public int InheritedPoints { get; private set; }
    /// <summary>Distinct bones used by the cage (weight &gt; 0 on some point).</summary>
    public int BonesUsed { get; private set; }
    public int PointCount => PointWeights.Length / 4;

    CageSkin(int matrixCount, int[] b, float[] w) { MatrixCount = matrixCount; PointBones = b; PointWeights = w; }

    const int K = 16; // distinct bones kept per point while accumulating

    public static CageSkin Build(Cage cage, SkinData skin)
    {
        if (skin.VertexCount != cage.VertexCount) throw new ArgumentException("skin/cage vertex count mismatch");
        int np = cage.PointCount, nb = skin.MatrixCount;
        var accB = new int[np * K]; var accW = new double[np * K]; var cnt = new int[np];
        void Add(int p, int bone, double w)
        {
            int o = p * K, c = cnt[p];
            for (int k = 0; k < c; k++) if (accB[o + k] == bone) { accW[o + k] += w; return; }
            if (c < K) { accB[o + c] = bone; accW[o + c] = w; cnt[p] = c + 1; return; }
            int min = 0; for (int k = 1; k < K; k++) if (accW[o + k] < accW[o + min]) min = k;
            if (w > accW[o + min]) { accB[o + min] = bone; accW[o + min] = w; }
        }
        var vp = cage.VertexPoints; var vw = cage.VertexWeights; var sb = skin.VertexBones; var sw = skin.VertexWeights;
        // 0.10.1: a point follows the bones of the vertices that actually move with the cage (weighted by how soft
        // they are), so a soft belly's points follow the spine and not the firm arm or tail whose vertices share
        // their cells. Points that carry only firm vertices fall back to all of them.
        var gain = cage.VertexGain;
        var softSupport = new bool[np];
        for (int v = 0; v < cage.VertexCount; v++)
        {
            if (gain == null || !(gain[v] > Cage.GainEpsilon)) continue;
            for (int q = 0; q < 4; q++) { int p = vp[v * 4 + q]; if (p >= 0 && vw[v * 4 + q] > 0f) softSupport[p] = true; }
        }
        for (int v = 0; v < cage.VertexCount; v++)
        {
            int o = v * 4;
            float g = gain == null ? 1f : gain[v];
            for (int q = 0; q < 4; q++)
            {
                int p = vp[o + q]; float e = vw[o + q];
                if (p < 0 || !(e > 0f)) continue;
                double f = softSupport[p] ? Math.Max(0f, g) : 1.0;
                if (!(f > 0.0)) continue;
                for (int j = 0; j < 4; j++) { float bw = sw[o + j]; if (bw > 0f) Add(p, sb[o + j], (double)e * bw * f); }
            }
        }

        var pb = new int[np * 4]; var pw = new float[np * 4];
        var known = new bool[np];
        for (int p = 0; p < np; p++) known[p] = Finish(p, accB, accW, cnt[p], pb, pw, nb);

        // Points without vertices: the average of their known neighbours' weights, spreading outwards.
        int inherited = 0;
        var tmpB = new int[K]; var tmpW = new double[K];
        for (int iter = 0; iter < 32; iter++)
        {
            var newly = new List<int>();
            for (int p = 0; p < np; p++) cnt[p] = known[p] ? -1 : 0;
            for (int e = 0; e < cage.Edges.Length; e += 2)
            {
                int a = cage.Edges[e], b = cage.Edges[e + 1];
                if (!known[a] && known[b]) for (int q = 0; q < 4; q++) if (pw[b * 4 + q] > 0f) Add(a, pb[b * 4 + q], pw[b * 4 + q]);
                if (!known[b] && known[a]) for (int q = 0; q < 4; q++) if (pw[a * 4 + q] > 0f) Add(b, pb[a * 4 + q], pw[a * 4 + q]);
            }
            bool any = false;
            for (int p = 0; p < np; p++)
            {
                if (known[p]) continue;
                if (cnt[p] <= 0) { any = true; continue; }
                if (Finish(p, accB, accW, cnt[p], pb, pw, nb)) { newly.Add(p); inherited++; }
                else any = true;
            }
            foreach (var p in newly) known[p] = true;
            if (!any || newly.Count == 0) break;
        }
        // anything still unknown (a cage island without any vertex): the slot bone
        for (int p = 0; p < np; p++)
            if (!known[p]) { pb[p * 4] = skin.SlotBone; pw[p * 4] = 1f; for (int q = 1; q < 4; q++) { pb[p * 4 + q] = skin.SlotBone; pw[p * 4 + q] = 0f; } }

        var cs = new CageSkin(nb, pb, pw) { InheritedPoints = inherited };
        var used = new bool[nb];
        for (int i = 0; i < pw.Length; i++) if (pw[i] > 0f) used[pb[i]] = true;
        int u = 0; foreach (var x in used) if (x) u++;
        cs.BonesUsed = u;
        return cs;
    }

    /// <summary>Keeps the 4 strongest accumulated bones of point p, normalised. False when there is no weight.</summary>
    static bool Finish(int p, int[] accB, double[] accW, int count, int[] pb, float[] pw, int nb)
    {
        int o = p * K;
        // partial selection sort: the 4 largest to the front
        int keep = Math.Min(4, count);
        for (int a = 0; a < keep; a++)
        {
            int best = a;
            for (int k = a + 1; k < count; k++) if (accW[o + k] > accW[o + best]) best = k;
            if (best != a) { (accB[o + a], accB[o + best]) = (accB[o + best], accB[o + a]); (accW[o + a], accW[o + best]) = (accW[o + best], accW[o + a]); }
        }
        double sum = 0;
        for (int a = 0; a < keep; a++) sum += accW[o + a];
        if (!(sum > 1e-12)) return false;
        for (int q = 0; q < 4; q++)
        {
            if (q < keep && accW[o + q] > 0) { pb[p * 4 + q] = accB[o + q]; pw[p * 4 + q] = (float)(accW[o + q] / sum); }
            else { pb[p * 4 + q] = q < keep ? accB[o + q] : (q > 0 ? pb[p * 4] : nb - 1); pw[p * 4 + q] = 0f; }
        }
        return true;
    }

    /// <summary>Blended skin matrix of point p.</summary>
    public Matrix4x4 PointMatrix(ReadOnlySpan<Matrix4x4> skin, int p)
    {
        int o = p * 4;
        return Lbs.Blend(skin, PointBones[o], PointWeights[o], PointBones[o + 1], PointWeights[o + 1],
                         PointBones[o + 2], PointWeights[o + 2], PointBones[o + 3], PointWeights[o + 3]);
    }

    float[] _boneScale;

    /// <summary>Skinned targets of all points (and their blended matrices, and optionally their scale: the weighted
    /// average of their bones' own uniform scales, which a blend of turned bones does not shrink). False when a matrix
    /// or target is not finite.</summary>
    public bool ComputeTargets(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<Vector3> rest, Span<Vector3> targets, Span<Matrix4x4> pointMats,
                               Span<float> pointScales = default)
    {
        if (skin.Length < MatrixCount) throw new ArgumentException($"expected {MatrixCount} skin matrices, got {skin.Length}");
        bool ok = true;
        bool scales = !pointScales.IsEmpty;
        if (scales)
        {
            if (_boneScale == null || _boneScale.Length < skin.Length) _boneScale = new float[skin.Length];
            for (int b = 0; b < skin.Length; b++) _boneScale[b] = Lbs.LinearScale(skin[b]);
        }
        for (int p = 0; p < rest.Length; p++)
        {
            var m = PointMatrix(skin, p);
            pointMats[p] = m;
            if (scales)
            {
                int o = p * 4;
                float sc = 0f;
                for (int q = 0; q < 4; q++) sc += PointWeights[o + q] * _boneScale[PointBones[o + q]];
                pointScales[p] = sc;
            }
            var t = Vector3.Transform(rest[p], m);
            targets[p] = t;
            if (!float.IsFinite(t.X + t.Y + t.Z)) ok = false;
        }
        return ok;
    }
}
