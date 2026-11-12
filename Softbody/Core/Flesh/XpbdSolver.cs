using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// Extended position based dynamics on the shell's tets, with many substeps and one constraint pass each ("small
/// steps", Macklin et al. 2019). Per tet, the two constraints of Macklin and Müller's Neo-Hookean formulation, offset so
/// both are zero at rest: shape <c>C_D = |F|_F - sqrt(3)</c> (compliance 1/(mu V)) and volume <c>C_H = det F - 1</c>
/// (compliance 1/(lambda V)). With det F = 1, |F|² >= 3 with equality only for a rotation, so together they mean "be a
/// rotated copy of the rest tet", and the volume term pushes inverted tets back out.
///
/// The rest shape is not fixed: <see cref="SetRest"/> is given the animated (skinned, blendshaped) targets every frame,
/// so the elasticity keeps the animated shape and never fights a bent joint or a blendshape. Kinematic vertices are
/// placed on their targets; the others get gravity, a follow spring to their target (stiffer where the paint is
/// firmer), and damping of their motion relative to the target's.
/// </summary>
public sealed partial class XpbdSolver
{
    public readonly int N;
    public readonly Vector3[] X, V;
    readonly Vector3[] _prev;
    readonly float[] _invMass, _mass;
    readonly bool[] _kin;
    readonly float[] _soft;
    readonly int[] _tets;
    readonly float[] _invDm, _vol, _tetDepth, _tetSoft;
    public int TetCount => _tets.Length / 4;
    /// <summary>Per vertex collision radius in world units (0 = does not collide).</summary>
    public readonly float[] Radius;
    /// <summary>0.11.4, per vertex (skin points only): the direction into the body where the animation puts the skin this
    /// frame (unit, zero = none), and how deep below its animated place a contact may push it (world units).</summary>
    public readonly Vector3[] Inward;
    public readonly float[] InwardLimit;
    /// <summary>Contacts squish the flesh (push skin points into the body) instead of sliding them around the collider.</summary>
    public bool Squish = true;
    public readonly List<FleshCollider> Colliders = new();
    /// <summary>After the last step: tets whose current det F is not positive, and tets with a degenerate rest.</summary>
    public int Inverted { get; private set; }
    public int DegenerateRest { get; private set; }
    public int Contacts { get; private set; }
    /// <summary>Last step: ms spent finding the collision pairs, and in the contact passes of all substeps.</summary>
    public double PairMs { get; private set; }
    public double ContactMs { get; private set; }

    const float Sqrt3 = 1.7320508f;

    public XpbdSolver(int n, int[] tets, bool[] kinematic, float[] softness, float[] tetDepth)
    {
        N = n; _tets = tets; _kin = kinematic; _soft = softness; _tetDepth = tetDepth;
        X = new Vector3[n]; V = new Vector3[n]; _prev = new Vector3[n];
        _invMass = new float[n]; _mass = new float[n]; Radius = new float[n];
        Inward = new Vector3[n]; InwardLimit = new float[n];
        int nt = tets.Length / 4;
        _invDm = new float[nt * 9]; _vol = new float[nt]; _tetSoft = new float[nt];
        for (int e = 0; e < nt; e++)
        {
            float s = 0; int c = 0;
            for (int j = 0; j < 4; j++) { int v = tets[e * 4 + j]; if (!kinematic[v]) { s += softness[v]; c++; } }
            _tetSoft[e] = c > 0 ? s / c : 1f;
        }
    }

    public void Reset(ReadOnlySpan<Vector3> x)
    {
        x.CopyTo(X);
        Array.Clear(V);
        ClearContacts();
    }

    /// <summary>Rest matrices, volumes and masses from these positions (the current targets).</summary>
    public void SetRest(ReadOnlySpan<Vector3> rest, float density)
    {
        Array.Clear(_mass);
        int degenerate = 0;
        for (int e = 0; e < _vol.Length; e++)
        {
            int o = e * 4;
            var r0 = rest[_tets[o]];
            var c1 = rest[_tets[o + 1]] - r0; var c2 = rest[_tets[o + 2]] - r0; var c3 = rest[_tets[o + 3]] - r0;
            var x23 = Vector3.Cross(c2, c3);
            float det = Vector3.Dot(c1, x23);
            float scale = c1.Length() * c2.Length() * c3.Length();
            if (!(det > 1e-5f * scale) || !float.IsFinite(det)) { _vol[e] = 0f; degenerate++; continue; }
            var x31 = Vector3.Cross(c3, c1); var x12 = Vector3.Cross(c1, c2);
            float r = 1f / det;
            int m = e * 9;
            _invDm[m] = x23.X * r; _invDm[m + 1] = x23.Y * r; _invDm[m + 2] = x23.Z * r;
            _invDm[m + 3] = x31.X * r; _invDm[m + 4] = x31.Y * r; _invDm[m + 5] = x31.Z * r;
            _invDm[m + 6] = x12.X * r; _invDm[m + 7] = x12.Y * r; _invDm[m + 8] = x12.Z * r;
            float vol = det / 6f;
            _vol[e] = vol;
            float q = density * vol * 0.25f;
            _mass[_tets[o]] += q; _mass[_tets[o + 1]] += q; _mass[_tets[o + 2]] += q; _mass[_tets[o + 3]] += q;
        }
        for (int i = 0; i < N; i++) _invMass[i] = _kin[i] || !(_mass[i] > 0f) ? 0f : 1f / _mass[i];
        DegenerateRest = degenerate;
    }

    public void Step(float dt, Vector3[] t0, Vector3[] t1, FleshSettings s)
    {
        if (!(dt > 0f)) return;
        int ns = s.Substeps;
        float h = dt / ns, h2 = h * h;
        var g = new Vector3(0f, -9.81f * s.Gravity, 0f);
        float damp = MathF.Exp(-s.Damping * h);
        float E = s.Stiffness * 1000f, nu = s.Volume;
        float muBase = E / (2f * (1f + nu)), lamBase = E * nu / ((1f + nu) * (1f - 2f * nu));
        float invDt = 1f / dt;
        float twoPiF = 2f * MathF.PI * s.Follow;
        int nt = _vol.Length;
        int contacts = 0;
        _unbouncedThisStep = 0;
        long tp = System.Diagnostics.Stopwatch.GetTimestamp();
        BeginContacts();
        BuildPairs(dt, t0, t1);
        PairMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ContactMs = 0;

        for (int step = 1; step <= ns; step++)
        {
            float a = (float)step / ns;
            // predict
            for (int i = 0; i < N; i++)
            {
                var tgt = Vector3.Lerp(t0[i], t1[i], a);
                _prev[i] = X[i];
                if (_invMass[i] == 0f) { X[i] = tgt; continue; }
                V[i] += g * h;
                X[i] += V[i] * h;
            }
            // tets
            for (int e = 0; e < nt; e++)
            {
                float vol = _vol[e];
                if (!(vol > 0f)) continue;
                float stiff = (1f + 3f * s.BoneDensity * _tetDepth[e]) / MathF.Max(0.0225f, _tetSoft[e] * _tetSoft[e]);
                SolveTet(e, 1f / (muBase * stiff * vol * h2), 1f / (lamBase * stiff * vol * h2));
            }
            // follow springs
            if (twoPiF > 0f)
                for (int i = 0; i < N; i++)
                {
                    if (_invMass[i] == 0f) continue;
                    float w = twoPiF / MathF.Max(0.1f, _soft[i]);
                    float k = w * w * h2;
                    var tgt = Vector3.Lerp(t0[i], t1[i], a);
                    X[i] += (tgt - X[i]) * (k / (1f + k));
                }
            // collisions: only the point/shape pairs found at the start of the step
            if (_pairCount > 0)
            {
                long tc = System.Diagnostics.Stopwatch.GetTimestamp();
                contacts = SolveContacts(t1, a, h);
                ContactMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            // velocities, damped relative to the animation (0.26.6: without the fling of this substep's contact pushes)
            for (int i = 0; i < N; i++)
            {
                var vt = (t1[i] - t0[i]) * invDt;
                if (_invMass[i] == 0f) { V[i] = vt; continue; }
                var v = (X[i] - _prev[i]) / h;
                if (NoBounce) v = Unbounce(i, v, h);
                V[i] = vt + (v - vt) * damp;
            }
        }
        Contacts = contacts;
        Unbounced = _unbouncedThisStep;
        EndContacts(dt);
        Inverted = CountInverted();
    }

    void SolveTet(int e, float alphaD, float alphaH)
    {
        int o = e * 4;
        int i0 = _tets[o], i1 = _tets[o + 1], i2 = _tets[o + 2], i3 = _tets[o + 3];
        float w0 = _invMass[i0], w1 = _invMass[i1], w2 = _invMass[i2], w3 = _invMass[i3];
        if (w0 + w1 + w2 + w3 == 0f) return;
        int mo = e * 9;
        float m00 = _invDm[mo], m01 = _invDm[mo + 1], m02 = _invDm[mo + 2];
        float m10 = _invDm[mo + 3], m11 = _invDm[mo + 4], m12 = _invDm[mo + 5];
        float m20 = _invDm[mo + 6], m21 = _invDm[mo + 7], m22 = _invDm[mo + 8];

        for (int pass = 0; pass < 2; pass++)
        {
            var x0 = X[i0];
            var e1 = X[i1] - x0; var e2 = X[i2] - x0; var e3 = X[i3] - x0;
            var f0 = e1 * m00 + e2 * m10 + e3 * m20;
            var f1 = e1 * m01 + e2 * m11 + e3 * m21;
            var f2 = e1 * m02 + e2 * m12 + e3 * m22;
            float C; Vector3 p0, p1, p2; float alpha;
            if (pass == 0)
            {
                // shape: |F| - sqrt(3), gradient F / |F|
                float rr = MathF.Sqrt(f0.LengthSquared() + f1.LengthSquared() + f2.LengthSquared());
                if (!(rr > 1e-9f)) continue;
                C = rr - Sqrt3;
                float inv = 1f / rr;
                p0 = f0 * inv; p1 = f1 * inv; p2 = f2 * inv;
                alpha = alphaD;
            }
            else
            {
                // volume: det F - 1, gradient = the cofactor columns
                p0 = Vector3.Cross(f1, f2); p1 = Vector3.Cross(f2, f0); p2 = Vector3.Cross(f0, f1);
                C = Vector3.Dot(f0, p0) - 1f;
                alpha = alphaH;
            }
            var g1 = p0 * m00 + p1 * m01 + p2 * m02;
            var g2 = p0 * m10 + p1 * m11 + p2 * m12;
            var g3 = p0 * m20 + p1 * m21 + p2 * m22;
            var g0 = -(g1 + g2 + g3);
            float denom = w0 * g0.LengthSquared() + w1 * g1.LengthSquared() + w2 * g2.LengthSquared() + w3 * g3.LengthSquared() + alpha;
            if (!(denom > 1e-20f)) continue;
            float dl = -C / denom;
            if (!float.IsFinite(dl)) continue;
            if (w0 != 0f) X[i0] += g0 * (w0 * dl);
            if (w1 != 0f) X[i1] += g1 * (w1 * dl);
            if (w2 != 0f) X[i2] += g2 * (w2 * dl);
            if (w3 != 0f) X[i3] += g3 * (w3 * dl);
        }
    }

    int CountInverted()
    {
        int bad = 0;
        for (int e = 0; e < _vol.Length; e++)
        {
            if (!(_vol[e] > 0f)) continue;
            int o = e * 4;
            var x0 = X[_tets[o]];
            float det = Vector3.Dot(X[_tets[o + 1]] - x0, Vector3.Cross(X[_tets[o + 2]] - x0, X[_tets[o + 3]] - x0));
            if (!(det > 0f)) bad++;
        }
        return bad;
    }

    /// <summary>Sum of current tet volumes over the sum of rest volumes (1 = volume kept).</summary>
    public float VolumeRatio()
    {
        double cur = 0, rest = 0;
        for (int e = 0; e < _vol.Length; e++)
        {
            if (!(_vol[e] > 0f)) continue;
            int o = e * 4;
            var x0 = X[_tets[o]];
            cur += Vector3.Dot(X[_tets[o + 1]] - x0, Vector3.Cross(X[_tets[o + 2]] - x0, X[_tets[o + 3]] - x0)) / 6.0;
            rest += _vol[e];
        }
        return rest > 0 ? (float)(cur / rest) : 1f;
    }

    public bool IsKinematic(int i) => _invMass[i] == 0f;
}
