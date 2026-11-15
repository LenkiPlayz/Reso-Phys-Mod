using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Pop;

/// <summary>
/// 0.35.0: soft pieces (DESIGN-pop.md, "Soft pieces"). Each piece is a little cloth (<see cref="PopSoftPieces"/>): its
/// particles fall and fly as the cloth's points do, pushed by the air on their triangles (drag and lift, the cloth's
/// formula), keep their edges' length (two passes a step, softer when squeezed: it buckles rather than squashes) and bend
/// as Stiffness lets them (the cloth's bending pairs, its Bend scale), and Stiffness pulls each piece back toward its own
/// shape too (shape matching, Müller et al. 2005: none at 0, stiff toward 1). Each particle collides as a stiff piece's
/// point does (its whole way each step, friction, no bounce), and with Adhesion holds on to what it touched. In flight a
/// piece keeps its spin (only the air turns it), the motion within it dies down (its flapping, not its flight or its spin:
/// Müller et al. 2007), and a piece that lies still sleeps. On screen each vertex follows its three particles (its place
/// among them, its way off them turned as they turn), kept out of what the piece is near.
/// </summary>
public sealed partial class PopSolver
{
    /// <summary>kg/m²: what a soft piece weighs (a heavy cloth: felt, thin leather).</summary>
    public const float PieceWeight = 1f;
    const float DragCoefficient = 1f, LiftCoefficient = 0.5f;
    /// <summary>Per second: how fast the motion within a soft piece dies down.</summary>
    const float InnerDamping = 3f;
    const int SoftStretchPasses = 2;
    /// <summary>How hard a squeezed edge pushes back, as a fraction of how hard a stretched one pulls (the cloth's).</summary>
    const float SoftCompression = 0.1f;

    PopSoftPieces _soft;
    /// <summary>0.35.0: the soft pieces' particles (null: stiff pieces).</summary>
    public PopSoftPieces Soft => _soft;
    /// <summary>0.35.0, soft pieces: each particle's place and speed.</summary>
    public Vector3[] SX = Array.Empty<Vector3>(), SV = Array.Empty<Vector3>();
    Vector3[] _sp, _sPush, _xr;
    float[] _sw, _sm;
    bool[] _sHit, _sStuck;
    Quaternion[] _tq, _pq;
    Matrix4x4[] _pm;
    Vector3[] _tE1, _tE2, _tN;
    float[] _vmax = Array.Empty<float>(), _softRadius = Array.Empty<float>(), _softPad = Array.Empty<float>();
    Quaternion[] _smq = Array.Empty<Quaternion>();
    /// <summary>Per front vertex of a sleeping soft piece: how far it was moved out of what the piece lies on.</summary>
    Vector3[] _vfix = Array.Empty<Vector3>();

    /// <summary>0.35.0: XPBD compliance of a soft piece's bending pairs (m/N) at a Stiffness: the cloth's Bend scale for a
    /// sheet as heavy as <see cref="PieceWeight"/> (as much bending under its own weight), 0 at 1.</summary>
    public static float BendCompliance(float stiffness)
    {
        float s = float.IsFinite(stiffness) ? Math.Clamp(stiffness, 0f, 1f) : 0.3f;
        return s >= 1f ? 0f : MathF.Pow(10f, 2f - 7f * s) * (0.2f / PieceWeight);
    }

    /// <summary>0.35.0: how far each step a soft piece is pulled back toward its own shape at a Stiffness (shape matching):
    /// none at 0, all the way (stiff) at 1, ten times as far every 2/7 between. A piece held at one end then sags about
    /// 1.7e-4 m over this (gravity's way in a step from rest): about 5 cm at 0.3, 1 cm at 0.5, 2 mm at 0.7.</summary>
    public static float ShapeMatch(float stiffness)
    {
        float s = float.IsFinite(stiffness) ? Math.Clamp(stiffness, 0f, 1f) : 0.3f;
        return s <= 0f ? 0f : s >= 1f ? 1f : MathF.Pow(10f, -3.5f * (1f - s));
    }

    void SoftInit(PopSoftPieces s)
    {
        _soft = s;
        int np = s.Particles, nt = s.Triangles, n = Pieces.Count;
        SX = new Vector3[np]; SV = new Vector3[np]; _sp = new Vector3[np]; _sPush = new Vector3[np]; _xr = new Vector3[np];
        _sw = new float[np]; _sm = new float[np]; _sHit = new bool[np]; _sStuck = new bool[np];
        _pq = new Quaternion[np]; _pm = new Matrix4x4[np];
        _tq = new Quaternion[nt]; _tE1 = new Vector3[nt]; _tE2 = new Vector3[nt]; _tN = new Vector3[nt];
        _vmax = new float[n]; _softRadius = new float[n]; _softPad = new float[n];
        _smq = new Quaternion[n]; _vfix = new Vector3[Pieces.FrontVertices];
        for (int i = 0; i < n; i++)
        {
            X[i] = _x0[i] = s.Middle[i];
            _smq[i] = Quaternion.Identity;
            for (int p = s.PStart[i]; p < s.PStart[i + 1]; p++)
            {
                SX[p] = _sp[p] = s.Middle[i] + s.Rest[p];
                float m = MathF.Max(s.Area[p], 1e-8f) * PieceWeight;
                _sm[p] = m; _sw[p] = 1f / m;
                _pq[p] = Quaternion.Identity;
            }
            // the vertices from the piece's middle (its particles' rest places are too)
            for (int v = Pieces.VertexStart[i], e = v + Pieces.VertexCount[i]; v < e; v++) _restPos[v] = Pieces.Positions[v] - s.Middle[i];
            // a vertex keeps its distance to each of its particles (turned with it), so bent any way it is no further
            // from the piece's middle than its farthest particle and this
            float pad = 0f;
            for (int v = Pieces.VertexStart[i], e = v + Pieces.VertexCount[i]; v < e; v++)
                for (int j = 3 * v; j < 3 * v + 3; j++)
                    if (s.SkinW[j] > 0f) pad = MathF.Max(pad, Vector3.Distance(_restPos[v], s.Rest[s.SkinP[j]]));
            _softPad[i] = pad + Thickness;
            SoftReach(i);
        }
        for (int t = 0; t < nt; t++)
        {
            _tq[t] = Quaternion.Identity;
            Frame(s.Rest[s.Tri[3 * t]], s.Rest[s.Tri[3 * t + 1]], s.Rest[s.Tri[3 * t + 2]], out _tE1[t], out _tE2[t], out _tN[t]);
        }
    }

    /// <summary>How far soft piece i's vertices can be from its middle (X) now.</summary>
    void SoftReach(int i)
    {
        float far = 0f;
        var c = X[i];
        for (int p = _soft.PStart[i]; p < _soft.PStart[i + 1]; p++) far = MathF.Max(far, Vector3.DistanceSquared(SX[p], c));
        _softRadius[i] = MathF.Sqrt(far) + _softPad[i];
    }

    /// <summary>A triangle's frame: along its first edge, across it, and its normal. False when it has no area.</summary>
    static bool Frame(Vector3 a, Vector3 b, Vector3 c, out Vector3 e1, out Vector3 e2, out Vector3 n)
    {
        e1 = b - a;
        n = Vector3.Cross(b - a, c - a);
        float l1 = e1.Length(), ln = n.Length();
        if (!(l1 > 1e-12f) || !(ln > 1e-20f) || !float.IsFinite(l1 + ln)) { e1 = Vector3.UnitX; e2 = Vector3.UnitY; n = Vector3.UnitZ; return false; }
        e1 /= l1; n /= ln;
        e2 = Vector3.Cross(n, e1);
        return true;
    }

    /// <summary>A piece's push (and spin) on its particles.</summary>
    void SoftKick(int i, Vector3 dv, Vector3 dw)
    {
        var c = X[i];
        for (int p = _soft.PStart[i]; p < _soft.PStart[i + 1]; p++) SV[p] += dv + Vector3.Cross(dw, SX[p] - c);
    }

    /// <summary>Each soft piece's speed (its middle's) and its fastest particle's.</summary>
    void SoftSpeeds()
    {
        for (int i = 0; i < Pieces.Count; i++)
        {
            float m = 0f, v2 = 0f;
            var vc = Vector3.Zero;
            for (int p = _soft.PStart[i]; p < _soft.PStart[i + 1]; p++)
            {
                m += _sm[p]; vc += SV[p] * _sm[p];
                v2 = MathF.Max(v2, SV[p].LengthSquared());
            }
            if (m > 0f) V[i] = vc / m;
            _vmax[i] = MathF.Sqrt(v2);
        }
    }

    /// <summary>One step of soft piece i. <paramref name="g"/> is gravity times the step.</summary>
    void SoftSubstep(int i, float h, Vector3 g, float invH)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        if (p1 <= p0) { _asleep[i] = true; return; }
        if (Drag > 0f && float.IsFinite(Drag)) SoftAir(i, h);
        // its spin now, the air's turn included: in flight nothing else turns it
        var spin0 = SoftSpin(i);
        for (int p = p0; p < p1; p++)
        {
            var v = SV[p] + g;
            SV[p] = v;
            _sp[p] = SX[p];
            SX[p] += v * h;
        }
        // stretch: two passes, an edge squeezed shorter pushing back only softly
        int e0 = S.EStart[i], e1 = S.EStart[i + 1];
        for (int pass = 0; pass < SoftStretchPasses; pass++)
            for (int e = e0; e < e1; e++)
            {
                int ia = S.EA[e], ib = S.EB[e];
                float wa = _sw[ia], wb = _sw[ib];
                var d = SX[ia] - SX[ib];
                float len = d.Length();
                if (!(len > 1e-9f)) continue;
                float c = len - S.EL[e];
                float dl = -c / (wa + wb);
                if (c < 0f) dl *= SoftCompression;
                var corr = d * (dl / len);
                SX[ia] += corr * wa; SX[ib] -= corr * wb;
            }
        // bending, as soft as Stiffness asks
        float atB = BendCompliance(Stiffness) / (h * h);
        for (int b = S.BStart[i], be = S.BStart[i + 1]; b < be; b++)
        {
            int ia = S.BA[b], ib = S.BB[b];
            float wa = _sw[ia], wb = _sw[ib];
            var d = SX[ia] - SX[ib];
            float len = d.Length();
            if (!(len > 1e-9f)) continue;
            float dl = -(len - S.BL[b]) / (wa + wb + atB);
            var corr = d * (dl / len);
            SX[ia] += corr * wa; SX[ib] -= corr * wb;
        }
        // and back toward its own shape, as far as Stiffness asks
        float match = ShapeMatch(Stiffness);
        if (match > 0f) SoftShape(i, match);
        bool touched = SoftContacts(i, _pairStart[i], _pairStart[i + 1]);
        // the speeds, and no bounce off what they touched
        for (int p = p0; p < p1; p++)
        {
            var v = (SX[p] - _sp[p]) * invH;
            if (_sHit[p])
            {
                float l = _sPush[p].Length();
                if (l > 1e-12f)
                {
                    var nrm = _sPush[p] / l;
                    float vn = Vector3.Dot(v, nrm);
                    if (vn > 0f) v -= nrm * vn;
                }
            }
            SV[p] = v;
        }
        SoftDamp(i, h, !touched, spin0);

        // the piece as a whole: its middle, its speed, its fastest particle; still, or lost
        bool bad = false;
        for (int p = p0; p < p1 && !bad; p++)
        {
            var x = SX[p]; var v = SV[p];
            bad = !float.IsFinite(x.X + x.Y + x.Z + v.X + v.Y + v.Z);
        }
        if (bad)
            for (int p = p0; p < p1; p++)
            {
                var a = _sp[p];
                SX[p] = float.IsFinite(a.X + a.Y + a.Z) ? a : S.Middle[i] + S.Rest[p];
                SV[p] = Vector3.Zero;
            }
        float m = 0f, v2 = 0f;
        var xc = Vector3.Zero; var vc = Vector3.Zero;
        for (int p = p0; p < p1; p++)
        {
            float mp = _sm[p];
            m += mp; xc += SX[p] * mp; vc += SV[p] * mp;
            v2 = MathF.Max(v2, SV[p].LengthSquared());
        }
        if (m > 0f) { X[i] = xc / m; V[i] = vc / m; }
        _vmax[i] = MathF.Sqrt(v2);
        SoftReach(i);
        if (touched)
        {
            Contacts++;
            if (v2 < SleepSpeed * SleepSpeed) _still[i] += h; else _still[i] = 0f;
        }
        else _still[i] = 0f;
        if (bad || _still[i] > SleepAfter || X[i].Y < StopBelow)
        {
            _asleep[i] = true; _dirty[i] = true;
            for (int p = p0; p < p1; p++) { SV[p] = Vector3.Zero; _sp[p] = SX[p]; }
            V[i] = W[i] = Vector3.Zero;
            _vmax[i] = 0f;
            SoftSettle(i);
        }
    }

    /// <summary>Soft piece i's angular momentum about its middle.</summary>
    Vector3 SoftSpin(int i)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        float m = 0f;
        var xc = Vector3.Zero; var vc = Vector3.Zero;
        for (int p = p0; p < p1; p++) { float mp = _sm[p]; m += mp; xc += SX[p] * mp; vc += SV[p] * mp; }
        if (!(m > 0f)) return Vector3.Zero;
        xc /= m; vc /= m;
        var L = Vector3.Zero;
        for (int p = p0; p < p1; p++) L += Vector3.Cross(SX[p] - xc, SV[p] - vc) * _sm[p];
        return L;
    }

    /// <summary>Pulls soft piece i the fraction <paramref name="alpha"/> of the way back to its own shape: as cut, turned
    /// and moved as it lies best now (shape matching, Müller et al. 2005; the turn found from the last one, as in Müller et
    /// al. 2016, "A Robust Method to Extract the Rotational Part of Deformations").</summary>
    void SoftShape(int i, float alpha)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        float m = 0f;
        var c = Vector3.Zero;
        for (int p = p0; p < p1; p++) { m += _sm[p]; c += SX[p] * _sm[p]; }
        if (!(m > 0f)) return;
        c /= m;
        Vector3 a0 = Vector3.Zero, a1 = Vector3.Zero, a2 = Vector3.Zero;
        for (int p = p0; p < p1; p++)
        {
            var d = (SX[p] - c) * _sm[p];
            var r = S.Rest[p];
            a0 += d * r.X; a1 += d * r.Y; a2 += d * r.Z;
        }
        var q = _smq[i];
        for (int it = 0; it < 4; it++)
        {
            var r0 = Vector3.Transform(Vector3.UnitX, q); var r1 = Vector3.Transform(Vector3.UnitY, q); var r2 = Vector3.Transform(Vector3.UnitZ, q);
            var om = Vector3.Cross(r0, a0) + Vector3.Cross(r1, a1) + Vector3.Cross(r2, a2);
            float den = MathF.Abs(Vector3.Dot(r0, a0) + Vector3.Dot(r1, a1) + Vector3.Dot(r2, a2)) + 1e-20f;
            om /= den;
            float w = om.Length();
            if (!(w > 1e-7f) || !float.IsFinite(w)) break;
            q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(om / w, w) * q);
        }
        if (!float.IsFinite(q.X + q.Y + q.Z + q.W)) return;
        _smq[i] = q;
        for (int p = p0; p < p1; p++)
        {
            var goal = c + Vector3.Transform(S.Rest[p], q);
            SX[p] += (goal - SX[p]) * alpha;
        }
    }

    /// <summary>A soft piece that has just gone to sleep: each of its vertices put out of what it lies on, once (while it
    /// moves only its particles collide), as a stiff piece's are.</summary>
    void SoftSettle(int i)
    {
        var S = _soft;
        int v0 = Pieces.VertexStart[i], ve = v0 + Pieces.VertexCount[i];
        for (int v = v0; v < ve; v++) _vfix[v] = Vector3.Zero;
        int ps = _pairStart[i], pe = _pairStart[i + 1];
        if (pe <= ps) return;
        SoftPose(i, 1f);
        for (int v = v0; v < ve; v++)
        {
            // from the particle it follows most to it
            var pos = SoftVertex(v, out _);
            _vfix[v] = SoftOut(ps, pe, SX[SoftAnchor(v)], pos) - pos;
        }
    }

    /// <summary>A point x out of the shapes from ps to pe: no triangle or box between a (outside them) and it, and its
    /// thickness off each.</summary>
    Vector3 SoftOut(int ps, int pe, Vector3 a, Vector3 x)
    {
        float r = Thickness;
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool moved = false;
            for (int e = ps; e < pe; e++)
            {
                ref readonly var c = ref _shapes[_pairShape[e]];
                if (MathF.Max(a.X, x.X) + r < c.BMin.X || MathF.Min(a.X, x.X) - r > c.BMax.X || MathF.Max(a.Y, x.Y) + r < c.BMin.Y ||
                    MathF.Min(a.Y, x.Y) - r > c.BMax.Y || MathF.Max(a.Z, x.Z) + r < c.BMin.Z || MathF.Min(a.Z, x.Z) - r > c.BMax.Z) continue;
                var t = x;
                bool hit = c.Kind switch
                {
                    FleshColliderKind.Triangle => CrossedTriangle(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    FleshColliderKind.Box => CrossedBox(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    _ => XpbdSolver.Resolve(ref t, r, c),
                };
                if (hit && float.IsFinite(t.X + t.Y + t.Z)) { x = t; moved = true; }
            }
            if (!moved) break;
        }
        return x;
    }

    /// <summary>The air on soft piece i's triangles, as on the cloth's (ClothSolver.Aero): drag against the way each goes
    /// as much as it faces it, lift across it. Drag is how thick the air is (1: real air on a sheet of PieceWeight).</summary>
    void SoftAir(int i, float h)
    {
        var S = _soft;
        float rho = 1.2f * Drag * S.AirScale[i];
        for (int t = S.TStart[i], te = S.TStart[i + 1]; t < te; t++)
        {
            int ia = S.Tri[3 * t], ib = S.Tri[3 * t + 1], ic = S.Tri[3 * t + 2];
            var x0 = SX[ia];
            var cr = Vector3.Cross(SX[ib] - x0, SX[ic] - x0);
            float twoA = cr.Length();
            if (!(twoA > 1e-10f)) continue;
            var nrm = cr / twoA;
            var vrel = (SV[ia] + SV[ib] + SV[ic]) * (1f / 3f);
            float sp2 = vrel.LengthSquared();
            if (!(sp2 > 1e-8f)) continue;
            float sp = MathF.Sqrt(sp2);
            var d = vrel / sp;
            float cos = Vector3.Dot(nrm, d);
            float q = 0.25f * rho * twoA * sp2;              // dynamic pressure x area
            var force = d * (-DragCoefficient * q * MathF.Abs(cos)) - (nrm - d * cos) * (LiftCoefficient * q * cos);
            float cap = 0.5f * sp;
            AirPush(ia, force, h, cap); AirPush(ib, force, h, cap); AirPush(ic, force, h, cap);
        }
    }

    void AirPush(int p, Vector3 force, float h, float cap)
    {
        var dv = force * (_sw[p] * h / 3f);
        float l2 = dv.LengthSquared();
        if (l2 > cap * cap) dv *= cap / MathF.Sqrt(l2);
        if (float.IsFinite(dv.X + dv.Y + dv.Z)) SV[p] += dv;
    }

    /// <summary>Soft piece i's particles against its shapes: each pushed out (its whole way this step for a triangle or a
    /// box), with friction and adhesion's grip; then adhesion on the ones that touched before and touch nothing now. True
    /// when any touched or held.</summary>
    bool SoftContacts(int i, int ps, int pe)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        float r = Thickness, mu = Friction, grip = _grip;
        bool touched = false;
        for (int p = p0; p < p1; p++) { _sHit[p] = false; _sPush[p] = Vector3.Zero; }
        if (pe <= ps)
        {
            for (int p = p0; p < p1; p++) _sStuck[p] = false;   // nothing near to hold on to
            return false;
        }
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int p = p0; p < p1; p++) { lo = Vector3.Min(lo, Vector3.Min(_sp[p], SX[p])); hi = Vector3.Max(hi, Vector3.Max(_sp[p], SX[p])); }
        var pad = new Vector3(r + AdhesionReach);
        lo -= pad; hi += pad;
        for (int e = ps; e < pe; e++)
        {
            ref readonly var c = ref _shapes[_pairShape[e]];
            if (hi.X < c.BMin.X || lo.X > c.BMax.X || hi.Y < c.BMin.Y || lo.Y > c.BMax.Y || hi.Z < c.BMin.Z || lo.Z > c.BMax.Z) continue;
            for (int p = p0; p < p1; p++)
            {
                var a = _sp[p]; var x = SX[p];
                if (MathF.Max(a.X, x.X) + r < c.BMin.X || MathF.Min(a.X, x.X) - r > c.BMax.X || MathF.Max(a.Y, x.Y) + r < c.BMin.Y ||
                    MathF.Min(a.Y, x.Y) - r > c.BMax.Y || MathF.Max(a.Z, x.Z) + r < c.BMin.Z || MathF.Min(a.Z, x.Z) - r > c.BMax.Z) continue;
                var t = x;
                bool hit = c.Kind switch
                {
                    FleshColliderKind.Triangle => CrossedTriangle(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    FleshColliderKind.Box => CrossedBox(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    _ => XpbdSolver.Resolve(ref t, r, c),
                };
                if (!hit) continue;
                var push = t - x;
                float depth = push.Length();
                if (!(depth > 1e-9f)) continue;
                var nrm = push / depth;
                if (mu > 0f || grip > 0f)
                {
                    // Coulomb friction (position based) over this step's way along the surface, with adhesion's grip on
                    // top (the cloth's: sliding, it slows by a little less than holds it)
                    var rel = x - a;
                    var tang = rel - nrm * Vector3.Dot(rel, nrm);
                    float tl = tang.Length();
                    if (tl > 1e-12f) t -= tl <= mu * depth + grip ? tang : tang * ((0.75f * mu * depth + grip) / tl);
                }
                SX[p] = t;
                _sHit[p] = true; _sPush[p] += nrm; _sStuck[p] = true;
                touched = true;
            }
        }
        if (grip > 0f)
            for (int p = p0; p < p1; p++)
            {
                if (!_sStuck[p] || _sHit[p]) continue;
                if (HoldParticle(p, ps, pe, r, grip)) touched = true;
                else _sStuck[p] = false;
            }
        return touched;
    }

    /// <summary>Adhesion on a particle that touched something before and touches nothing this step: within
    /// <see cref="AdhesionReach"/> beyond its thickness of a shape, it is pulled back to the nearest and held along it, each
    /// by at most the step's grip. False when no shape is that near (it let go).</summary>
    bool HoldParticle(int p, int ps, int pe, float r, float grip)
    {
        var x = SX[p];
        float rr = r + AdhesionReach, gap = float.MaxValue;
        var bn = Vector3.Zero;
        for (int e = ps; e < pe; e++)
        {
            ref readonly var c = ref _shapes[_pairShape[e]];
            if (x.X + rr < c.BMin.X || x.X - rr > c.BMax.X || x.Y + rr < c.BMin.Y || x.Y - rr > c.BMax.Y || x.Z + rr < c.BMin.Z || x.Z - rr > c.BMax.Z) continue;
            var t = x;
            if (!XpbdSolver.Resolve(ref t, rr, c)) continue;
            var push = t - x;
            float l = push.Length();
            if (!(l > 1e-9f)) continue;
            float g = AdhesionReach - l;   // how far off the surface it is, beyond its thickness
            if (g < gap) { gap = g; bn = push / l; }
        }
        if (gap == float.MaxValue) return false;
        if (gap > 0f) x -= bn * MathF.Min(gap, grip);
        var rel = x - _sp[p];
        var tang = rel - bn * Vector3.Dot(rel, bn);
        float tl = tang.Length();
        if (tl > 1e-12f) x -= tl <= grip ? tang : tang * (0.75f * grip / tl);
        SX[p] = x;
        return true;
    }

    /// <summary>Damps the motion within soft piece i: each particle's speed toward what the piece's flight and spin as a
    /// whole give it there (Müller et al. 2007, "Position Based Dynamics", 3.5), so its flapping dies down while it keeps
    /// flying and turning.</summary>
    void SoftDamp(int i, float h, bool keep, Vector3 spin0)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        if (p1 - p0 < 2) return;
        float k = 1f - MathF.Exp(-InnerDamping * h);
        float m = 0f;
        var xc = Vector3.Zero; var vc = Vector3.Zero;
        for (int p = p0; p < p1; p++) { float mp = _sm[p]; m += mp; xc += SX[p] * mp; vc += SV[p] * mp; }
        if (!(m > 0f)) return;
        xc /= m; vc /= m;
        var L = Vector3.Zero;
        float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        for (int p = p0; p < p1; p++)
        {
            float mp = _sm[p];
            var r = SX[p] - xc;
            L += Vector3.Cross(r, SV[p] - vc) * mp;
            xx += mp * (r.Y * r.Y + r.Z * r.Z); yy += mp * (r.X * r.X + r.Z * r.Z); zz += mp * (r.X * r.X + r.Y * r.Y);
            xy -= mp * r.X * r.Y; xz -= mp * r.X * r.Z; yz -= mp * r.Y * r.Z;
        }
        float e = 0.001f * (xx + yy + zz) / 3f + 1e-15f;   // a strip of particles has none about its own length
        var I = new Matrix4x4(xx + e, xy, xz, 0f, xy, yy + e, yz, 0f, xz, yz, zz + e, 0f, 0f, 0f, 0f, 1f);
        if (!Matrix4x4.Invert(I, out var inv)) return;
        if (keep)
        {
            // in flight: the spin its straight moves this step lost goes back (a step moves each particle along a chord
            // of its turn, a little short of it)
            var dw = Vector3.TransformNormal(spin0 - L, inv);
            if (float.IsFinite(dw.X + dw.Y + dw.Z) && dw.LengthSquared() < 2500f)
            {
                for (int p = p0; p < p1; p++) SV[p] += Vector3.Cross(dw, SX[p] - xc);
                L = spin0;
            }
        }
        var w = Vector3.TransformNormal(L, inv);
        if (!float.IsFinite(w.X + w.Y + w.Z)) return;
        for (int p = p0; p < p1; p++)
        {
            var dv = vc + Vector3.Cross(w, SX[p] - xc) - SV[p];
            SV[p] += dv * k;
        }
    }

    /// <summary>Soft piece i's box for finding shapes: its particles and where they head (each no further than MaxReach,
    /// as a stiff piece's).</summary>
    void SoftBox(int i, float simDt, float m0)
    {
        var S = _soft;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        for (int p = S.PStart[i]; p < S.PStart[i + 1]; p++)
        {
            var x = SX[p];
            var d = SV[p] * (1.5f * simDt);
            float go = d.Length();
            if (go > MaxReach && float.IsFinite(go))
            {
                float k = MaxReach / go;
                d *= k; SV[p] *= k;
            }
            lo = Vector3.Min(lo, Vector3.Min(x, x + d)); hi = Vector3.Max(hi, Vector3.Max(x, x + d));
        }
        var pad = new Vector3(m0);
        _lo[i] = lo - pad; _hi[i] = hi + pad;
    }

    /// <summary>Soft piece i's particles as they show (between their last two steps, unless it sleeps), each triangle's
    /// turn from how it was cut, each particle's (its triangles' together, or its donor's), and each particle's matrix
    /// from its rest place to now. Returns the particles' middle.</summary>
    Vector3 SoftPose(int i, float alpha)
    {
        var S = _soft;
        int p0 = S.PStart[i], p1 = S.PStart[i + 1];
        bool still = _asleep[i];
        var mid = Vector3.Zero;
        float m = 0f;
        for (int p = p0; p < p1; p++)
        {
            var x = still ? SX[p] : Vector3.Lerp(_sp[p], SX[p], alpha);
            _xr[p] = x;
            mid += x * _sm[p]; m += _sm[p];
        }
        mid = m > 0f ? mid / m : X[i];
        for (int t = S.TStart[i], te = S.TStart[i + 1]; t < te; t++)
        {
            // a triangle folded flat keeps the turn it had
            if (!Frame(_xr[S.Tri[3 * t]], _xr[S.Tri[3 * t + 1]], _xr[S.Tri[3 * t + 2]], out var e1, out var e2, out var n)) continue;
            var a = _tE1[t]; var b = _tE2[t]; var c = _tN[t];
            var rx = e1 * a.X + e2 * b.X + n * c.X;
            var ry = e1 * a.Y + e2 * b.Y + n * c.Y;
            var rz = e1 * a.Z + e2 * b.Z + n * c.Z;
            var q = Quaternion.CreateFromRotationMatrix(new Matrix4x4(rx.X, rx.Y, rx.Z, 0f, ry.X, ry.Y, ry.Z, 0f, rz.X, rz.Y, rz.Z, 0f, 0f, 0f, 0f, 1f));
            if (float.IsFinite(q.X + q.Y + q.Z + q.W)) _tq[t] = Quaternion.Normalize(q);
        }
        for (int p = p0; p < p1; p++)
        {
            int a0 = S.IncStart[p], a1 = S.IncStart[p + 1];
            if (a1 == a0) continue;
            var first = _tq[S.Inc[a0]];
            var sum = first;
            for (int k = a0 + 1; k < a1; k++)
            {
                var q = _tq[S.Inc[k]];
                sum += Quaternion.Dot(q, first) < 0f ? Quaternion.Negate(q) : q;
            }
            if (sum.LengthSquared() > 1e-12f) _pq[p] = Quaternion.Normalize(sum);
        }
        for (int p = p0; p < p1; p++)
        {
            if (S.IncStart[p + 1] == S.IncStart[p]) _pq[p] = _pq[S.Donor[p]];
            var mm = Matrix4x4.CreateFromQuaternion(_pq[p]);
            mm.Translation = _xr[p] - Vector3.TransformNormal(S.Rest[p], mm);
            _pm[p] = mm;
        }
        return mid;
    }

    /// <summary>Front vertex v of a soft piece where its three particles put it (after <see cref="SoftPose"/>): at its
    /// place among them as they are now, and its way off that place (out of their plane, or past their rim) turned as they
    /// turn, by M (their turns blended by its weights). A piece that crumples as it lands so moves each vertex only as far
    /// as its particles go, not by the turns of particles centimetres away.</summary>
    Vector3 SoftVertex(int v, out Matrix4x4 M)
    {
        var S = _soft;
        int o = 3 * v, a = S.SkinP[o], b = S.SkinP[o + 1], c = S.SkinP[o + 2];
        float wa = S.SkinW[o], wb = S.SkinW[o + 1], wc = S.SkinW[o + 2];
        M = _pm[a] * wa + _pm[b] * wb + _pm[c] * wc;
        var at = _xr[a] * wa + _xr[b] * wb + _xr[c] * wc;
        var from = S.Rest[a] * wa + S.Rest[b] * wb + S.Rest[c] * wc;
        return at + Vector3.TransformNormal(_restPos[v] - from, M);
    }

    /// <summary>The particle front vertex v follows most (it collides, so lies outside everything).</summary>
    int SoftAnchor(int v)
    {
        var S = _soft;
        int o = 3 * v, j = o;
        if (S.SkinW[o + 1] > S.SkinW[j]) j = o + 1;
        if (S.SkinW[o + 2] > S.SkinW[j]) j = o + 2;
        return S.SkinP[j];
    }

    /// <summary>Soft piece i as it shows: each vertex moved and turned by its three particles, and kept out of what the
    /// piece is near (only its particles collide: a vertex past their rim, as a piece lands edge first, would show in the
    /// floor; a sleeping piece's were put out once), shrunk about the piece's middle by s.</summary>
    void SoftWrite(int i, float alpha, float s, float back, int f)
    {
        var S = _soft;
        if (S.PStart[i + 1] <= S.PStart[i]) return;
        bool still = _asleep[i];
        int ps = _pairStart[i], pe = _pairStart[i + 1];
        bool near = !still && pe > ps;
        var mid = SoftPose(i, alpha);
        for (int v = Pieces.VertexStart[i], ve = v + Pieces.VertexCount[i]; v < ve; v++)
        {
            var pos = SoftVertex(v, out var M);
            if (still) pos += _vfix[v];
            else if (near) pos = SoftOut(ps, pe, _xr[SoftAnchor(v)], pos);
            if (s != 1f) pos = mid + (pos - mid) * s;
            var nn = Vector3.TransformNormal(_restNrm[v], M);
            float nl = nn.Length();
            nn = nl > 1e-12f ? nn / nl : _restNrm[v];
            var t4 = _restTan[v];
            var t3 = new Vector3(t4.X, t4.Y, t4.Z);
            var tt = Vector3.TransformNormal(t3, M);
            float tl = tt.Length();
            tt = tl > 1e-12f ? tt / tl : t3;
            OutPositions[v] = pos; OutNormals[v] = nn; OutTangents[v] = new Vector4(tt, t4.W);
            OutPositions[v + f] = pos - nn * back; OutNormals[v + f] = -nn; OutTangents[v + f] = new Vector4(tt, -t4.W);
        }
    }
}
