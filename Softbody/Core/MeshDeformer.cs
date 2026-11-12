using System.Numerics;

namespace Softbody.Core;

/// <summary>
/// Moves render vertices with the cage and recomputes their normals. Works in the mesh's local
/// space, so firm vertices (softness 0, or carried only by pinned points) are never touched: they
/// already sit at their rest position relative to the slot.
/// </summary>
public sealed class MeshDeformer
{
    readonly Cage _cage;
    readonly Vector3[] _restPos;
    readonly Vector3[] _restNormals;
    readonly Vector3[] _restComputed;   // normalized face-average normal at rest (sign matched to rest normal)
    readonly float[] _sign;             // +1/-1 so winding direction never flips the delta
    readonly int[] _dynVerts;
    readonly int[] _normalVerts;
    readonly int[] _normalTris;         // flattened, 3 per triangle
    readonly Vector3[] _accum;
    readonly Vector3[] _localDisp;
    readonly Vector3[] _ext;            // displacement on the ghost-padded lattice (smooth mode)
    readonly bool _smooth;
    public bool Smooth => _smooth;

    public int VertexCount => _restPos.Length;
    public int DynamicVertexCount => _dynVerts.Length;
    public int NormalVertexCount => _normalVerts.Length;
    public int NormalTriangleCount => _normalTris.Length / 3;
    public ReadOnlySpan<Vector3> RestPositions => _restPos;
    public ReadOnlySpan<Vector3> RestNormals => _restNormals;

    /// <param name="restNormals">May be null; then area-weighted face normals are used as rest normals.</param>
    /// <param name="triangles">All triangle indices (3 per triangle) of every triangle submesh.</param>
    /// <param name="smooth">true (default): quadratic B-spline embedding, no creases. false: the old
    /// piecewise-linear tetrahedral embedding (kept for comparison tests).</param>
    public MeshDeformer(Cage cage, ReadOnlySpan<Vector3> restPositions, ReadOnlySpan<Vector3> restNormals, ReadOnlySpan<int> triangles, bool smooth = true)
    {
        _cage = cage;
        _smooth = smooth;
        int nv = restPositions.Length;
        if (cage.VertexCount != nv) throw new ArgumentException("cage/mesh vertex count mismatch");
        _restPos = restPositions.ToArray();
        _accum = new Vector3[nv];
        _localDisp = new Vector3[cage.PointCount];
        _snapshot = new Vector3[cage.PointCount];
        _ext = new Vector3[cage.Ex * cage.Ey * cage.Ez];

        // Which vertices move: soft enough, and carried by at least one simulated point.
        var dyn = new bool[nv];
        var dynList = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            if (cage.VertexGain[v] <= Cage.GainEpsilon) continue;
            bool moves = false;
            if (smooth)
            {
                cage.SplineRealRange(cage.SplineBase[v * 3], 0, out int x0, out int x1);
                cage.SplineRealRange(cage.SplineBase[v * 3 + 1], 1, out int y0, out int y1);
                cage.SplineRealRange(cage.SplineBase[v * 3 + 2], 2, out int z0, out int z1);
                for (int k = z0; k <= z1 && !moves; k++)
                    for (int j = y0; j <= y1 && !moves; j++)
                        for (int i = x0; i <= x1; i++)
                        {
                            int p = cage.Index(i, j, k);
                            if (p >= 0 && !cage.Kinematic[p]) { moves = true; break; }
                        }
            }
            else
            {
                for (int q = 0; q < 4; q++)
                {
                    int o = v * 4 + q;
                    if (cage.VertexPoints[o] >= 0 && MathF.Abs(cage.VertexWeights[o]) > 1e-6f && !cage.Kinematic[cage.VertexPoints[o]]) { moves = true; break; }
                }
            }
            if (moves) { dyn[v] = true; dynList.Add(v); }
        }
        _dynVerts = dynList.ToArray();

        // Normals: every vertex of a triangle that has a moving vertex; accumulate over all of
        // those vertices' triangles so their normals are complete.
        int nt = triangles.Length / 3;
        var isNormal = new bool[nv];
        for (int t = 0; t < nt; t++)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) continue;
            if (dyn[a] || dyn[b] || dyn[c]) { isNormal[a] = isNormal[b] = isNormal[c] = true; }
        }
        var normalVerts = new List<int>();
        for (int v = 0; v < nv; v++) if (isNormal[v]) normalVerts.Add(v);
        _normalVerts = normalVerts.ToArray();
        var ntris = new List<int>();
        for (int t = 0; t < nt; t++)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) continue;
            if (isNormal[a] || isNormal[b] || isNormal[c]) { ntris.Add(a); ntris.Add(b); ntris.Add(c); }
        }
        _normalTris = ntris.ToArray();

        // Rest face-average normals for every vertex (needed as fallback rest normals too).
        var restComputed = new Vector3[nv];
        for (int t = 0; t < nt; t++)
        {
            int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
            if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) continue;
            var n = Vector3.Cross(_restPos[b] - _restPos[a], _restPos[c] - _restPos[a]);
            restComputed[a] += n; restComputed[b] += n; restComputed[c] += n;
        }
        bool haveRest = restNormals.Length >= nv;
        _restNormals = new Vector3[nv];
        _sign = new float[nv];
        _restComputed = new Vector3[nv];
        for (int v = 0; v < nv; v++)
        {
            var rc = SafeNormalize(restComputed[v], Vector3.UnitY);
            var rn = haveRest ? restNormals[v] : rc;
            if (!(rn.LengthSquared() > 1e-12f) || !float.IsFinite(rn.X + rn.Y + rn.Z)) rn = rc;
            rn = SafeNormalize(rn, rc);
            _restNormals[v] = rn;
            _sign[v] = Vector3.Dot(rn, rc) >= 0 ? 1f : -1f;
            _restComputed[v] = rc * _sign[v];
        }
    }

    static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float l = v.LengthSquared();
        return l > 1e-20f && float.IsFinite(l) ? v / MathF.Sqrt(l) : fallback;
    }

    /// <summary>
    /// Writes deformed positions and normals of moving vertices (others are left untouched, so
    /// the buffers must start out holding the rest mesh). Returns the largest cage displacement.
    /// </summary>
    public float Deform(ReadOnlySpan<Vector3> cageWorld, in AnchorPose anchor, Span<Vector3> positions, Span<Vector3> normals,
                        out Vector3 boundsMin, out Vector3 boundsMax)
    {
        float disp = UpdateCage(cageWorld, anchor, out _);
        ApplyToMesh(positions, normals, out boundsMin, out boundsMax);
        CommitSnapshot();
        return disp;
    }

    readonly Vector3[] _snapshot;   // cage displacement last applied to the mesh that was shown
    float _maxDisp;

    /// <summary>
    /// Step 1 (cheap, per cage point): cage displacement in the mesh's local frame. maxChange is the
    /// largest change against the last committed snapshot, so callers can skip mesh work when idle.
    /// </summary>
    public float UpdateCage(ReadOnlySpan<Vector3> cageWorld, in AnchorPose anchor, out float maxChange)
    {
        var rest = _cage.RestPoints;
        float maxSq = 0, changeSq = 0;
        var inv = Quaternion.Conjugate(anchor.Rotation);
        var invScale = Vector3.One / anchor.Scale;
        for (int i = 0; i < rest.Length; i++)
        {
            var local = Vector3.Transform(cageWorld[i] - anchor.Position, inv) * invScale;
            var d = local - rest[i];
            _localDisp[i] = d;
            maxSq = MathF.Max(maxSq, d.LengthSquared());
            changeSq = MathF.Max(changeSq, Vector3.DistanceSquared(d, _snapshot[i]));
        }
        maxChange = MathF.Sqrt(changeSq);
        _maxDisp = MathF.Sqrt(maxSq);
        return _maxDisp;
    }

    /// <summary>
    /// Step 1 for a skinned cage: the displacements are given directly, already in the mesh's bind space
    /// (<see cref="CageSimulation.GetBindOffsets"/>). Same bookkeeping as <see cref="UpdateCage"/>.
    /// </summary>
    public float SetDisplacements(ReadOnlySpan<Vector3> localDisp, out float maxChange)
    {
        float maxSq = 0, changeSq = 0;
        for (int i = 0; i < _localDisp.Length; i++)
        {
            var d = localDisp[i];
            _localDisp[i] = d;
            maxSq = MathF.Max(maxSq, d.LengthSquared());
            changeSq = MathF.Max(changeSq, Vector3.DistanceSquared(d, _snapshot[i]));
        }
        maxChange = MathF.Sqrt(changeSq);
        _maxDisp = MathF.Sqrt(maxSq);
        return _maxDisp;
    }

    /// <summary>The cage displacement in the mesh's frame from the last UpdateCage / SetDisplacements.</summary>
    public ReadOnlySpan<Vector3> Displacements => _localDisp;

    /// <summary>Marks the current cage displacement as shown (call after the mesh was uploaded).</summary>
    public void CommitSnapshot() => _localDisp.AsSpan().CopyTo(_snapshot);

    /// <summary>Step 2: moves the vertices and recomputes their normals from the last UpdateCage.</summary>
    public void ApplyToMesh(Span<Vector3> positions, Span<Vector3> normals, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        var gain = _cage.VertexGain;
        var disp = _localDisp; var restPos = _restPos;
        if (_smooth)
        {
            FillExtended();
            var e = _ext; var sb = _cage.SplineBase; var sw = _cage.SplineWeights;
            int ex = _cage.Ex, exy = _cage.Ex * _cage.Ey;
            foreach (int v in _dynVerts)
            {
                int o3 = v * 3, o9 = v * 9;
                int bx = sb[o3], by = sb[o3 + 1], bz = sb[o3 + 2];
                float wx0 = sw[o9], wx1 = sw[o9 + 1], wx2 = sw[o9 + 2];
                Vector3 d = default;
                for (int c = 0; c < 3; c++)
                {
                    float wz = sw[o9 + 6 + c];
                    int zoff = exy * (bz + c) + bx;
                    Vector3 dy = default;
                    for (int b = 0; b < 3; b++)
                    {
                        int row = zoff + ex * (by + b);
                        dy += sw[o9 + 3 + b] * (e[row] * wx0 + e[row + 1] * wx1 + e[row + 2] * wx2);
                    }
                    d += wz * dy;
                }
                positions[v] = restPos[v] + d * gain[v];
            }
        }
        else
        {
            var vp = _cage.VertexPoints; var vw = _cage.VertexWeights;
            foreach (int v in _dynVerts)
            {
                int o = v * 4;
                var d = disp[vp[o]] * vw[o] + disp[vp[o + 1]] * vw[o + 1] + disp[vp[o + 2]] * vw[o + 2] + disp[vp[o + 3]] * vw[o + 3];
                positions[v] = restPos[v] + d * gain[v];
            }
        }

        if (normals.Length >= restPos.Length)
        {
            var acc = _accum;
            foreach (int v in _normalVerts) acc[v] = default;
            var tris = _normalTris;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                var pa = positions[a];
                var n = Vector3.Cross(positions[b] - pa, positions[c] - pa);
                acc[a] += n; acc[b] += n; acc[c] += n;
            }
            foreach (int v in _normalVerts)
            {
                var computed = SafeNormalize(acc[v], _restComputed[v] * _sign[v]) * _sign[v];
                // Apply the change of the face-average normal to the original normal, so smoothing
                // or hard edges authored in the mesh survive and there is no pop at rest.
                normals[v] = SafeNormalize(_restNormals[v] + (computed - _restComputed[v]), _restNormals[v]);
            }
        }

        float grow = _maxDisp * (_smooth ? _cage.SplineMaxAbsSum : _cage.MaxWeightAbsSum);
        var ev = new Vector3(grow);
        boundsMin = _cage.RestBoundsMin - ev;
        boundsMax = _cage.RestBoundsMax + ev;
    }

    /// <summary>Copies the cage displacement into the ghost-padded grid and extrapolates the ghost
    /// layers linearly, axis by axis (so affine displacement fields stay exactly affine).</summary>
    void FillExtended()
    {
        var c = _cage; var e = _ext; var disp = _localDisp;
        const int G = Cage.Ghost;
        int nx = c.Nx, ny = c.Ny, nz = c.Nz, ex = c.Ex, ey = c.Ey, exy = ex * ey;
        // Grid nodes outside the sparse cage are firm: zero displacement, like pinned points.
        Array.Clear(e);
        var pext = c.PointExt;
        for (int p = 0; p < pext.Length; p++) e[pext[p]] = disp[p];
        for (int k = G; k < G + nz; k++)
            for (int j = G; j < G + ny; j++)
                Extrapolate(e, ex * (j + ey * k), 1, nx);
        for (int k = G; k < G + nz; k++)
            for (int i = 0; i < ex; i++)
                Extrapolate(e, i + exy * k, ex, ny);
        for (int j = 0; j < ey; j++)
            for (int i = 0; i < ex; i++)
                Extrapolate(e, i + ex * j, exy, nz);
    }

    static void Extrapolate(Vector3[] e, int start, int stride, int n)
    {
        const int G = Cage.Ghost;
        var a0 = e[start + stride * G]; var a1 = e[start + stride * (G + 1)];
        e[start + stride * (G - 1)] = 2f * a0 - a1;
        e[start + stride * (G - 2)] = 3f * a0 - 2f * a1;
        var b0 = e[start + stride * (G + n - 1)]; var b1 = e[start + stride * (G + n - 2)];
        e[start + stride * (G + n)] = 2f * b0 - b1;
        e[start + stride * (G + n + 1)] = 3f * b0 - 2f * b1;
    }
}
