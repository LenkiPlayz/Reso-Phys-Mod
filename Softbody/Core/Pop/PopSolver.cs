using System.Diagnostics;
using System.Numerics;
using Softbody.Core.Flesh;

namespace Softbody.Core.Pop;

/// <summary>
/// 0.34.0: a pop's pieces in flight. Each piece is stiff: a place, a turn, their speeds, and the points on its surface
/// that it collides with: up to <see cref="PopCut.MostPointsPerPiece"/> spread over it while it is fast, all its vertices
/// (up to <see cref="PopCut.MostSlowPoints"/>) once it is slow. A step moves each piece, then tests its points against
/// the world's shapes: a triangle or a box against each point's whole way that step, so a fast piece cannot step over a
/// thin floor. A point that hit something moves the piece as a stiff body (XPBD, Müller et al. 2020, "Detailed Rigid Body
/// Simulation with Extended Position Based Dynamics"): it gets out exactly, the piece moving and turning by how hard it
/// is to turn about that point. With friction and no bounce; a piece goes to sleep once it lies still. Players' bodies
/// are left out: a pop is not a blanket.
/// The pairs (a piece and a shape near it) are found once per run of steps, as the cloth's are since 0.33.1: each piece
/// looks along its own way (at most MaxReach a run, more runs when it would get further), and only the shapes in the 1 m
/// cells the pieces look in are gathered. A frame shows the pieces between their last two steps.
/// 0.35.0: soft pieces (PopSolverSoft.cs): with a <see cref="PopSoftPieces"/> every piece is a little cloth instead, its
/// particles colliding the same way. And <see cref="Adhesion"/>: what a piece touched holds on to it, soft or stiff.
/// </summary>
public sealed partial class PopSolver
{
    public const int StepRate = 240;
    public const int MaxStepsPerFrame = 8;
    /// <summary>Metres a piece looks ahead for shapes in one run of steps, at most (as the cloth's MaxReach).</summary>
    public const float MaxReach = 2f;
    public const int MaxPairRuns = 4;
    public const int MaxMeshTriangles = 20000;
    public const float NearCell = 1f;
    const int NearMostCells = 64;
    const int NearQueries = 512;
    const int MostCellsASide = 6;
    const float SleepSpeed = 0.05f, SleepSpin = 0.4f, SleepAfter = 0.4f;
    /// <summary>The contacts again while a point was pushed more than <see cref="Settled"/> (a turn about a corner can
    /// swing another point into the floor; a flake that came down flat and deep settles over a few), at most this many
    /// times a step.</summary>
    const int ContactPasses = 8;
    const float Settled = 2e-5f;
    /// <summary>Metres a second: a piece slower than this collides with its slow points (all its vertices, up to
    /// <see cref="PopCut.MostSlowPoints"/>), so one lying or sliding on something has none in it.</summary>
    const float SlowSpeed = 1.5f;
    /// <summary>Metres: a step that would take a point further than this into a triangle or a box stops the piece where
    /// it first touches (shallower ones the contacts push out).</summary>
    const float TouchDeep = 0.01f;
    /// <summary>Rounds of the no-bounce pass over a piece's touching points.</summary>
    const int StopPasses = 4;

    /// <summary>Radians a second a piece spins at most.</summary>
    const float MaxSpin = 120f;
    /// <summary>Per second: how fast a piece's spin dies away while it touches something (debris does not roll on).</summary>
    const float ContactSpinDrag = 20f;

    public readonly PopPieces Pieces;
    /// <summary>Per piece: its origin's place, its turn (from the pose it was cut in), and their speeds.</summary>
    public readonly Vector3[] X, V, W;
    public readonly Quaternion[] Q;
    readonly Vector3[] _x0;
    readonly Quaternion[] _q0;
    readonly bool[] _asleep, _dirty;
    readonly float[] _still;
    /// <summary>The mesh as it shows this frame (front vertices, then the back faces'), for the plugin to upload.</summary>
    public readonly Vector3[] OutPositions, OutNormals;
    public readonly Vector4[] OutTangents;
    readonly Vector3[] _restPos, _restNrm;
    readonly Vector4[] _restTan;
    readonly Matrix4x4[] _invInertia;

    /// <summary>Per second: how fast the air slows the pieces down, and their spin. 0.35.0: on soft pieces, how thick the air
    /// is for them instead (1: air on a sheet of <see cref="PieceWeight"/>), pushing on each of their triangles.</summary>
    public float Drag = 1f, AngularDrag = 0.5f;
    public float Friction = 0.6f;
    /// <summary>0.35.0, soft pieces: 0 drapes like silk, toward 1 stiff (the cloth's Bend).</summary>
    public float Stiffness = 0.3f;
    /// <summary>0.35.0: how hard a piece holds on to what it touched, in its own weights (0: not at all; 1 holds it on a
    /// wall or under a ceiling, but barely). It holds within <see cref="AdhesionReach"/> of it.</summary>
    public float Adhesion;
    /// <summary>Metres beyond its thickness: how far from a surface a piece that touched it still holds on.</summary>
    public const float AdhesionReach = 0.01f;
    /// <summary>m/s²: Adhesion's unit (a weight).</summary>
    public const float GravityG = 9.81f;
    /// <summary>A step's hold from Adhesion (metres along a surface, and back to it).</summary>
    float _grip;
    readonly bool[] _stuck;
    /// <summary>Metres: how far a piece's points keep off a surface.</summary>
    public float Thickness = 0.003f;
    /// <summary>0..1: the pieces' size (the plugin shrinks them away at the end of their lifetime).</summary>
    public float Shrink = 1f;
    /// <summary>A piece that falls below this (world height) stops (the plugin sets it well under where it popped).</summary>
    public float StopBelow = float.MinValue;
    float _shown = 1f, _accum;
    bool _first = true;

    // ---- stats
    public int Awake { get; private set; }
    public int StepsLastFrame { get; private set; }
    public int PairCount { get; private set; }
    public int PairRuns { get; private set; }
    public int ShapeCount => _shapeCount;
    public int MeshTriangles { get; private set; }
    public int Contacts { get; private set; }
    public double LastStepMs { get; private set; }
    public double PairsMs { get; private set; }
    /// <summary>Whether the mesh changed this frame (and wants uploading).</summary>
    public bool Changed { get; private set; }
    /// <summary>Tests: how long piece i has lain still.</summary>
    public float StillFor(int i) => _still[i];

    /// <summary>The pieces in flight: stiff ones, or (0.35.0) soft ones when <paramref name="soft"/> is given (built from
    /// the same pieces).</summary>
    public PopSolver(PopPieces pieces, PopSoftPieces soft = null)
    {
        Pieces = pieces;
        int n = pieces.Count;
        X = new Vector3[n]; V = new Vector3[n]; W = new Vector3[n]; Q = new Quaternion[n];
        _x0 = new Vector3[n]; _q0 = new Quaternion[n];
        _asleep = new bool[n]; _dirty = new bool[n]; _still = new float[n]; _stuck = new bool[n];
        for (int i = 0; i < n; i++)
        {
            X[i] = _x0[i] = pieces.Origin[i];
            Q[i] = _q0[i] = Quaternion.Identity;
        }
        int f = pieces.FrontVertices;
        _restPos = new Vector3[f]; _restNrm = new Vector3[f]; _restTan = new Vector4[f];
        for (int i = 0; i < n; i++)
            for (int v = pieces.VertexStart[i], e = v + pieces.VertexCount[i]; v < e; v++)
            {
                _restPos[v] = pieces.Positions[v] - pieces.Origin[i];
                _restNrm[v] = pieces.Normals[v];
                _restTan[v] = pieces.Tangents[v];
            }
        OutPositions = (Vector3[])pieces.Positions.Clone();
        OutNormals = (Vector3[])pieces.Normals.Clone();
        OutTangents = (Vector4[])pieces.Tangents.Clone();
        _pairStart = new int[n + 1];
        _lo = new Vector3[n]; _hi = new Vector3[n]; _stamp = new int[n];
        _invInertia = Inertias(pieces);
        if (soft != null && soft.Count == n) SoftInit(soft);
    }

    /// <summary>The pop's push: every piece away from <paramref name="center"/> at about <paramref name="strength"/> m/s.
    /// Randomness 0..1 makes it uneven (each piece up to half slower, a little sideways) and spins the pieces (small
    /// ones faster). 0.36.0: given <paramref name="maxStrength"/> (0: none), each piece flies at its own speed between
    /// the two instead, picked evenly, and Randomness no longer slows it: it still sends it a little sideways and spins
    /// it (by its own speed). The same seed gives the same push.</summary>
    public void Kick(Vector3 center, float strength, float randomness, int seed, float maxStrength = 0f)
    {
        if (!float.IsFinite(strength) || !float.IsFinite(center.X + center.Y + center.Z)) return;
        float rnd = float.IsFinite(randomness) ? Math.Clamp(randomness, 0f, 1f) : 0f;
        // 0.36.0: a range, in either order (the same speeds both ways); none at 0 or at Strength itself
        bool range = float.IsFinite(maxStrength) && maxStrength != 0f && maxStrength != strength;
        float low = range ? MathF.Min(strength, maxStrength) : strength, span = range ? MathF.Abs(maxStrength - strength) : 0f;
        for (int i = 0; i < Pieces.Count; i++)
        {
            uint h = Hash((uint)i, (uint)seed);
            var dir = X[i] - center;
            float l = dir.Length();
            dir = l > 1e-6f ? dir / l : RandomUnit(h);
            dir += RandomUnit(Mix(h ^ 0x9E3779B9u)) * (0.6f * rnd);
            float dl = dir.Length();
            dir = dl > 1e-6f ? dir / dl : Vector3.UnitY;
            float u = Unit(Mix(h ^ 0x85EBCA6Bu));
            float speed = range ? low + span * u : strength * (1f - 0.5f * rnd * u);
            V[i] += dir * speed;
            float spin = rnd * MathF.Abs(range ? speed : strength) * 0.5f / MathF.Max(Pieces.Radius[i], 0.03f);
            var dw = RandomUnit(Mix(h ^ 0xC2B2AE35u)) * (spin * (0.5f + Unit(Mix(h ^ 0x27D4EB2Fu))));
            W[i] += dw;
            if (_soft != null) SoftKick(i, dir * speed, dw);
            _asleep[i] = false; _still[i] = 0f;
        }
        if (_soft != null) SoftSpeeds();
    }

    /// <summary>Adds the same velocity to every piece (what the popped thing was moving at).</summary>
    public void Carry(Vector3 velocity)
    {
        if (!float.IsFinite(velocity.X + velocity.Y + velocity.Z)) return;
        for (int i = 0; i < Pieces.Count; i++) V[i] += velocity;
        if (_soft == null) return;
        for (int p = 0; p < SV.Length; p++) SV[p] += velocity;
        SoftSpeeds();
    }

    /// <summary>One frame: the steps it holds (StepRate a second, at most MaxStepsPerFrame), then the mesh as it shows.</summary>
    public void Step(float dt, Vector3 gravity, List<FleshCollider> colliders)
    {
        long t0 = Stopwatch.GetTimestamp();
        dt = float.IsFinite(dt) ? Math.Clamp(dt, 0f, 0.1f) : 0f;
        float h = 1f / StepRate;
        _accum += dt;
        int steps = (int)(_accum / h);
        if (steps > MaxStepsPerFrame) { steps = MaxStepsPerFrame; _accum = 0f; }
        else _accum -= steps * h;
        StepsLastFrame = steps;
        PairCount = 0; PairRuns = 0; PairsMs = 0; Contacts = 0;
        if (!float.IsFinite(gravity.X + gravity.Y + gravity.Z)) gravity = Vector3.Zero;
        _grip = float.IsFinite(Adhesion) && Adhesion > 0f ? MathF.Min(Adhesion, 100f) * GravityG * h * h : 0f;
        if (steps > 0)
        {
            int runs = RunsFor(steps, h, colliders);
            PairRuns = runs;
            int done = 0;
            for (int run = 0; run < runs; run++)
            {
                int take = (steps - done) / (runs - run);
                long tp = Stopwatch.GetTimestamp();
                BuildPairs(colliders, gravity, take * h);
                PairsMs += (Stopwatch.GetTimestamp() - tp) * 1000.0 / Stopwatch.Frequency;
                if (_pairCount > PairCount) PairCount = _pairCount;
                for (int k = 0; k < take; k++) Substep(h, gravity);
                done += take;
            }
        }
        int awake = 0;
        for (int i = 0; i < Pieces.Count; i++) if (!_asleep[i]) awake++;
        Awake = awake;
        WriteMesh(Math.Clamp(_accum / h, 0f, 1f));
        LastStepMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    int RunsFor(int steps, float h, List<FleshCollider> colliders)
    {
        if (steps <= 1 || colliders == null || colliders.Count == 0) return 1;
        float v2 = 0f;
        for (int i = 0; i < Pieces.Count; i++)
        {
            if (_asleep[i]) continue;
            float l2 = _soft != null ? _vmax[i] * _vmax[i] : V[i].LengthSquared();
            if (l2 > v2) v2 = l2;
        }
        float go = 1.5f * MathF.Sqrt(v2) * steps * h;
        if (!float.IsFinite(go) || go <= MaxReach) return 1;
        int most = Math.Min(steps, MaxPairRuns);
        float want = MathF.Ceiling(go / MaxReach);
        return want >= most ? most : (int)want;
    }

    // ------------------------------------------------------------------ a step

    readonly Vector3[] _from = new Vector3[PopCut.MostSlowPoints], _to = new Vector3[PopCut.MostSlowPoints],
                       _nrm = new Vector3[PopCut.MostSlowPoints], _tn = new Vector3[PopCut.MostSlowPoints];
    readonly float[] _tt = new float[PopCut.MostSlowPoints];
    readonly bool[] _hit = new bool[PopCut.MostSlowPoints];

    void Substep(float h, Vector3 gravity)
    {
        var g = gravity * h;
        float damp = MathF.Exp(-MathF.Max(Drag, 0f) * h), spinDamp = MathF.Exp(-MathF.Max(AngularDrag, 0f) * h);
        float r = Thickness, invH = 1f / h, spinTouch = MathF.Exp(-ContactSpinDrag * h);
        var P = Pieces;
        for (int i = 0; i < P.Count; i++)
        {
            if (_asleep[i]) continue;
            if (_soft != null) { SoftSubstep(i, h, g, invH); continue; }
            var x0 = X[i]; var q0 = Q[i];
            var v = (V[i] + g) * damp;
            var w = W[i] * spinDamp;
            var x = x0 + v * h;
            var q = Turn(q0, w, h);
            // the place the contacts start from (where it first touches, when it would have gone into something)
            var xr = x; var qr = q;
            bool touched = false;
            int ps = _pairStart[i], pe = _pairStart[i + 1];
            int kc = V[i].LengthSquared() < SlowSpeed * SlowSpeed ? P.SlowPointCount[i] : P.PointCount[i];
            if (pe > ps)
            {
                // this step's box for the piece, to pass over the shapes it is nowhere near this step
                var rad = new Vector3(P.Radius[i] + 2f * r);
                var blo = Vector3.Min(x0, x) - rad; var bhi = Vector3.Max(x0, x) + rad;
                for (int k = 0; k < kc; k++) { _from[k] = x0 + Vector3.Transform(P.Points[P.PointStart[i] + k], q0); _hit[k] = false; }
                float first = FirstTouch(i, ps, pe, blo, bhi, x, q, r, kc);
                if (first < 1f)
                {
                    x = xr = Vector3.Lerp(x0, x, first);
                    q = qr = Quaternion.Slerp(q0, q, first);
                    // the points that touch there touch (no bounce for them either)
                    for (int k = 0; k < kc; k++)
                        if (_tt[k] <= first + 1e-4f) { _hit[k] = true; _nrm[k] = _tn[k]; touched = true; }
                }
                bool settled = false;
                for (int pass = 0; pass < ContactPasses; pass++)
                {
                    float most = Touch(i, ps, pe, blo, bhi, ref x, ref q, r, P.PointStart[i], kc, P.Points, true, true);
                    if (most > 0f) touched = true;
                    if (!(most > Settled)) { settled = true; break; }
                    blo = Vector3.Min(blo, x - rad); bhi = Vector3.Max(bhi, x + rad);
                }
                // 0.35.0: adhesion: a piece that touched something holds on to it while it is near
                if (touched) _stuck[i] = true;
                else if (_grip > 0f && _stuck[i] && Hold(i, ps, pe, ref x, ref q, r, kc)) { touched = true; settled = false; }
                // 0.35.0: friction and adhesion turn the piece too (a point's hold along a surface swings the others): then
                // pushes out without them till none moves, so that no point ends the step through a surface (the next
                // step's sweep starts where it ends; on a two-sided triangle a point past the middle goes out the back)
                for (int pass = 0; !settled && pass < ContactPasses; pass++)
                {
                    float most = Touch(i, ps, pe, blo, bhi, ref x, ref q, r, P.PointStart[i], kc, P.Points, true, false);
                    if (most > 0f) touched = true;
                    if (!(most > Settled)) settled = true;
                    blo = Vector3.Min(blo, x - rad); bhi = Vector3.Max(bhi, x + rad);
                }
            }
            else _stuck[i] = false;
            // the speeds it had, with what the contacts did to it
            var nv = v + (x - xr) * invH;
            var nw = w + Spin(qr, q, invH);
            if (touched)
            {
                // no bounce: a point that touched something stops moving into it or away from it
                for (int pass = 0; pass < StopPasses; pass++)
                {
                    float most = 0f;
                    for (int k = 0; k < kc; k++)
                    {
                        if (!_hit[k]) continue;
                        var arm = Vector3.Transform(P.Points[P.PointStart[i] + k], q);
                        float vn = Vector3.Dot(nv + Vector3.Cross(nw, arm), _nrm[k]);
                        if (vn == 0f) continue;
                        Push(i, q, arm, _nrm[k], -vn, ref nv, ref nw);
                        most = MathF.Max(most, MathF.Abs(vn));
                    }
                    if (!(most > 1e-4f)) break;
                }
                nw *= spinTouch;
                if (nv.LengthSquared() < SleepSpeed * SleepSpeed && nw.LengthSquared() < SleepSpin * SleepSpin) _still[i] += h;
                else _still[i] = 0f;
                Contacts++;
            }
            else _still[i] = 0f;
            float spin = nw.Length();
            if (spin > MaxSpin) nw *= MaxSpin / spin;
            bool bad = !float.IsFinite(x.X + x.Y + x.Z + q.X + q.Y + q.Z + q.W + nv.X + nv.Y + nv.Z + nw.X + nw.Y + nw.Z);
            if (bad) { x = x0; q = q0; nv = nw = Vector3.Zero; }
            bool sleeps = _still[i] > SleepAfter;
            if (!bad && sleeps && P.VertexCount[i] > kc)
            {
                // a big piece lying still: every vertex out of what it lies on (more than its slow points), once; the
                // place only
                var all = new Vector3(float.MaxValue);
                for (int pass = 0; pass < ContactPasses; pass++)
                    if (!(Touch(i, ps, pe, -all, all, ref x, ref q, r, -1, 0, null, false, false) > Settled)) break;
            }
            _x0[i] = x0; _q0[i] = q0;
            X[i] = x; Q[i] = q; V[i] = nv; W[i] = nw;
            if (bad || sleeps || x.Y < StopBelow)
            {
                _asleep[i] = true; _dirty[i] = true;
                V[i] = W[i] = Vector3.Zero;
                _x0[i] = X[i]; _q0[i] = Q[i];
            }
        }
    }

    /// <summary>How far along this step (0..1) piece i first touches a triangle or a box it would otherwise go into or
    /// through: 1 when it touches none. Each collision point's way, from where it was to where it would be.</summary>
    float FirstTouch(int i, int ps, int pe, Vector3 blo, Vector3 bhi, Vector3 x, Quaternion q, float r, int kc)
    {
        var P = Pieces;
        int k0 = P.PointStart[i];
        float first = 1f;
        bool made = false;
        for (int k = 0; k < kc; k++) _tt[k] = 2f;
        for (int e = ps; e < pe; e++)
        {
            ref readonly var c = ref _shapes[_pairShape[e]];
            if (c.Kind != FleshColliderKind.Triangle && c.Kind != FleshColliderKind.Box) continue;
            if (bhi.X < c.BMin.X || blo.X > c.BMax.X || bhi.Y < c.BMin.Y || blo.Y > c.BMax.Y || bhi.Z < c.BMin.Z || blo.Z > c.BMax.Z) continue;
            if (!made) { made = true; for (int k = 0; k < kc; k++) _to[k] = x + Vector3.Transform(P.Points[k0 + k], q); }
            for (int k = 0; k < kc; k++)
            {
                var a = _from[k]; var p = _to[k];
                if (MathF.Max(a.X, p.X) + r < c.BMin.X || MathF.Min(a.X, p.X) - r > c.BMax.X || MathF.Max(a.Y, p.Y) + r < c.BMin.Y ||
                    MathF.Min(a.Y, p.Y) - r > c.BMax.Y || MathF.Max(a.Z, p.Z) + r < c.BMin.Z || MathF.Min(a.Z, p.Z) - r > c.BMax.Z) continue;
                float t = c.Kind == FleshColliderKind.Triangle ? TriangleTime(a, p, r, c, out var n, out float deep) : BoxTime(a, p, r, c, out n, out deep);
                if (!(deep > TouchDeep)) continue;   // the contacts push it out
                if (t < _tt[k]) { _tt[k] = t; _tn[k] = n; }
                if (t < first) first = t;
            }
        }
        return first;
    }

    /// <summary>One pass of piece i's contacts with its shapes: each point (its collision points, or with k0 = -1 all its
    /// vertices) pushed out and the piece moved as a stiff body for it. Swept: each point's way from where it was this
    /// step, with friction when <paramref name="rub"/>; otherwise its way out from the piece's middle (a vertex on the other
    /// side of a floor than its piece's middle went through it). Returns how far the furthest moved point went, friction's
    /// hold included (0: none touched).</summary>
    float Touch(int i, int ps, int pe, Vector3 blo, Vector3 bhi, ref Vector3 x, ref Quaternion q, float r, int k0, int kc, Vector3[] pts, bool swept, bool rub)
    {
        var P = Pieces;
        float mu = Friction, most = 0f;
        int v0 = P.VertexStart[i];
        if (k0 < 0) kc = P.VertexCount[i];
        for (int e = ps; e < pe; e++)
        {
            ref readonly var c = ref _shapes[_pairShape[e]];
            if (bhi.X < c.BMin.X || blo.X > c.BMax.X || bhi.Y < c.BMin.Y || blo.Y > c.BMax.Y || bhi.Z < c.BMin.Z || blo.Z > c.BMax.Z) continue;
            for (int k = 0; k < kc; k++)
            {
                var o = k0 < 0 ? _restPos[v0 + k] : pts[k0 + k];
                var p = x + Vector3.Transform(o, q);
                var a = swept ? _from[k] : x;
                if (MathF.Max(a.X, p.X) + r < c.BMin.X || MathF.Min(a.X, p.X) - r > c.BMax.X || MathF.Max(a.Y, p.Y) + r < c.BMin.Y ||
                    MathF.Min(a.Y, p.Y) - r > c.BMax.Y || MathF.Max(a.Z, p.Z) + r < c.BMin.Z || MathF.Min(a.Z, p.Z) - r > c.BMax.Z) continue;
                var t = p;
                bool hit = c.Kind switch
                {
                    FleshColliderKind.Triangle => CrossedTriangle(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    FleshColliderKind.Box => CrossedBox(ref t, a, r, c) || XpbdSolver.Resolve(ref t, r, c),
                    _ => XpbdSolver.Resolve(ref t, r, c),
                };
                if (!hit) continue;
                var push = t - p;
                float depth = push.Length();
                if (!(depth > 1e-9f)) continue;
                var nrm = push / depth;
                if (swept && rub && (mu > 0f || _grip > 0f))
                {
                    // Coulomb friction (position based): this step's way along the surface; 0.35.0: and adhesion's grip
                    var rel = p - a;
                    var tang = rel - nrm * Vector3.Dot(rel, nrm);
                    float tl = tang.Length();
                    float hold = mu * depth + _grip;
                    if (tl > 1e-12f) t -= tl <= hold ? tang : tang * (hold / tl);
                }
                // the piece moves as a stiff body would for that point to get there (XPBD, Müller et al. 2020)
                Correct(i, ref x, ref q, p, t - p);
                float moved = Vector3.Distance(t, p);
                if (moved > most) most = moved;
                if (k0 >= 0) { _hit[k] = true; _nrm[k] = nrm; }
            }
        }
        return most;
    }

    /// <summary>
    /// 0.35.0: adhesion on a stiff piece that touched something before but touches nothing this step: each of its
    /// collision points within <see cref="AdhesionReach"/> (beyond its thickness) of a shape is pulled back to the nearest
    /// and held along it, each by at most this step's grip (Adhesion times the piece's weight). False when no point is that
    /// near: it let go.
    /// </summary>
    bool Hold(int i, int ps, int pe, ref Vector3 x, ref Quaternion q, float r, int kc)
    {
        var P = Pieces;
        int k0 = P.PointStart[i];
        float rr = r + AdhesionReach;
        bool held = false;
        for (int k = 0; k < kc; k++)
        {
            var p = x + Vector3.Transform(P.Points[k0 + k], q);
            float gap = float.MaxValue;
            var bn = Vector3.Zero;
            for (int e = ps; e < pe; e++)
            {
                ref readonly var c = ref _shapes[_pairShape[e]];
                if (p.X + rr < c.BMin.X || p.X - rr > c.BMax.X || p.Y + rr < c.BMin.Y || p.Y - rr > c.BMax.Y || p.Z + rr < c.BMin.Z || p.Z - rr > c.BMax.Z) continue;
                var t = p;
                if (!XpbdSolver.Resolve(ref t, rr, c)) continue;
                var push = t - p;
                float l = push.Length();
                if (!(l > 1e-9f)) continue;
                float g = AdhesionReach - l;   // how far off the surface it is, beyond its thickness
                if (g < gap) { gap = g; bn = push / l; }
            }
            if (gap == float.MaxValue) continue;
            if (gap > 0f)
            {
                Correct(i, ref x, ref q, p, -bn * MathF.Min(gap, _grip));
                p = x + Vector3.Transform(P.Points[k0 + k], q);
            }
            var rel = p - _from[k];
            var tang = rel - bn * Vector3.Dot(rel, bn);
            float tl = tang.Length();
            if (tl > 1e-12f) Correct(i, ref x, ref q, p, -(tl <= _grip ? tang : tang * (0.75f * _grip / tl)));
            _hit[k] = true; _nrm[k] = bn;
            held = true;
        }
        if (!held) _stuck[i] = false;
        return held;
    }

    /// <summary>Piece i's inverse inertia in the world (turned by q) times v.</summary>
    Vector3 InvInertia(int i, Quaternion q, Vector3 v)
    {
        var b = Vector3.Transform(v, Quaternion.Conjugate(q));
        return Vector3.Transform(Vector3.TransformNormal(b, _invInertia[i]), q);
    }

    /// <summary>Moves piece i (unit mass) so that its point at p moves by d, as a stiff body: the push along d at that
    /// point, shared between moving and turning by how hard the piece is to turn about it.</summary>
    void Correct(int i, ref Vector3 x, ref Quaternion q, Vector3 p, Vector3 d)
    {
        float l = d.Length();
        if (!(l > 1e-12f)) return;
        var n = d / l;
        var rn = Vector3.Cross(p - x, n);
        var irn = InvInertia(i, q, rn);
        float lambda = l / (1f + Vector3.Dot(rn, irn));
        x += n * lambda;
        var turn = irn * lambda;
        q = Quaternion.Normalize(q + new Quaternion(turn.X, turn.Y, turn.Z, 0f) * q * 0.5f);
    }

    /// <summary>Changes the speed of piece i's point at arm (from its middle) along n by dv, as a stiff body.</summary>
    void Push(int i, Quaternion q, Vector3 arm, Vector3 n, float dv, ref Vector3 v, ref Vector3 w)
    {
        var rn = Vector3.Cross(arm, n);
        var irn = InvInertia(i, q, rn);
        float p = dv / (1f + Vector3.Dot(rn, irn));
        v += n * p;
        w += irn * p;
    }

    static Quaternion Turn(Quaternion q, Vector3 w, float h)
    {
        float a = w.Length() * h;
        if (!(a > 1e-9f) || !float.IsFinite(a)) return q;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(w / (a / h), a) * q);
    }

    static Vector3 Spin(Quaternion q0, Quaternion q1, float invH)
    {
        var d = q1 * Quaternion.Conjugate(q0);
        if (d.W < 0f) d = Quaternion.Negate(d);
        var axis = new Vector3(d.X, d.Y, d.Z);
        float sn = axis.Length();
        if (!(sn > 1e-9f)) return Vector3.Zero;
        float angle = 2f * MathF.Atan2(sn, d.W);
        return axis * (angle / sn * invH);
    }

    /// <summary>Each piece's inverse inertia about its middle (unit mass shared by its collision points, plus a little all
    /// round, so a sliver of a piece does not spin up about its own length).</summary>
    static Matrix4x4[] Inertias(PopPieces p)
    {
        var res = new Matrix4x4[p.Count];
        for (int i = 0; i < p.Count; i++)
        {
            int k0 = p.PointStart[i], kc = Math.Max(1, p.PointCount[i]);
            float m = 1f / kc;
            float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            for (int k = 0; k < p.PointCount[i]; k++)
            {
                var o = p.Points[k0 + k];
                xx += m * (o.Y * o.Y + o.Z * o.Z); yy += m * (o.X * o.X + o.Z * o.Z); zz += m * (o.X * o.X + o.Y * o.Y);
                xy -= m * o.X * o.Y; xz -= m * o.X * o.Z; yz -= m * o.Y * o.Z;
            }
            float e = 0.05f * MathF.Max(p.Radius[i] * p.Radius[i], 1e-6f);
            var I = new Matrix4x4(xx + e, xy, xz, 0f, xy, yy + e, yz, 0f, xz, yz, zz + e, 0f, 0f, 0f, 0f, 1f);
            res[i] = Matrix4x4.Invert(I, out var inv) && float.IsFinite(inv.M11 + inv.M22 + inv.M33) ? inv : Matrix4x4.Identity * (1f / e);
        }
        return res;
    }

    /// <summary>The way from <paramref name="from"/> to <paramref name="x"/> went through triangle c (two-sided: either way;
    /// front only: from the front) where it meets the plane, or within r of it: x goes back to the side it came from, r off
    /// the plane (the cloth's, 0.33.1).</summary>
    static bool CrossedTriangle(ref Vector3 x, Vector3 from, float r, in FleshCollider c)
    {
        float s0 = Vector3.Dot(from, c.Nrm) - c.D, s1 = Vector3.Dot(x, c.Nrm) - c.D;
        float side;
        if (s0 >= 0f) { if (!(s1 < 0f)) return false; side = 1f; }
        else { if (!c.TwoSided || !(s1 > 0f)) return false; side = -1f; }
        var at = from + (x - from) * (s0 / (s0 - s1));
        var cp = XpbdSolver.ClosestOnTriangle(at, c.A, c.B, c.C);
        if (!(Vector3.DistanceSquared(at, cp) < r * r)) return false;
        x -= c.Nrm * (s1 - side * r);
        return true;
    }

    /// <summary>The way went into box c (r bigger all round) from outside it, or from within the r it keeps off: x goes
    /// back out through the face it came in by (or was at), straight out, so it keeps its way along that face. A way that
    /// starts deeper in is left to the nearest face (Resolve).</summary>
    static bool CrossedBox(ref Vector3 x, Vector3 from, float r, in FleshCollider c)
    {
        var inv = Quaternion.Conjugate(c.Q);
        var a = Vector3.Transform(from - c.A, inv);
        var b = Vector3.Transform(x - c.A, inv);
        var hb = c.B + new Vector3(r);
        float ix = hb.X - MathF.Abs(a.X), iy = hb.Y - MathF.Abs(a.Y), iz = hb.Z - MathF.Abs(a.Z);
        int axis;
        float face;
        if (ix > 0f && iy > 0f && iz > 0f)
        {
            // started within r of it (a piece resting on it starts right at r): out through the face it is at
            if (MathF.Min(ix, MathF.Min(iy, iz)) > r) return false;
            axis = ix <= iy && ix <= iz ? 0 : iy <= iz ? 1 : 2;
            float s = Axis(a, axis), hh = Axis(hb, axis);
            face = s >= 0f ? hh : -hh;
            float e = Axis(b, axis);
            if (face > 0f ? e >= face : e <= face) return false;   // ends outside that face
        }
        else
        {
            float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity;
            axis = -1; face = 0f;
            for (int k = 0; k < 3; k++)
            {
                float s = Axis(a, k), e = Axis(b, k), hh = Axis(hb, k);
                float d = e - s;
                if (MathF.Abs(d) < 1e-12f)
                {
                    if (s <= -hh || s >= hh) return false;   // never within this slab
                    continue;
                }
                float ta = (-hh - s) / d, tb = (hh - s) / d;
                float tin = MathF.Min(ta, tb), tout = MathF.Max(ta, tb);
                if (tin > t0) { t0 = tin; axis = k; face = d > 0f ? -hh : hh; }
                if (tout < t1) t1 = tout;
            }
            if (axis < 0 || !(t0 < t1) || t0 > 1f || !(t1 > 0f)) return false;   // misses it, stops short, or moves away
        }
        if (axis == 0) b.X = face; else if (axis == 1) b.Y = face; else b.Z = face;
        x = c.A + Vector3.Transform(b, c.Q);
        return true;
    }

    /// <summary>How far along the way from <paramref name="from"/> to <paramref name="to"/> (0..1) it is r off triangle c,
    /// on the side it came from, when it goes through the triangle (2: it does not), and how far past it it would end.</summary>
    static float TriangleTime(Vector3 from, Vector3 to, float r, in FleshCollider c, out Vector3 n, out float deep)
    {
        float s0 = Vector3.Dot(from, c.Nrm) - c.D, s1 = Vector3.Dot(to, c.Nrm) - c.D;
        n = s0 >= 0f ? c.Nrm : -c.Nrm;
        deep = MathF.Abs(s1);
        if (s0 >= 0f) { if (!(s1 < 0f)) return 2f; }
        else if (!c.TwoSided || !(s1 > 0f)) return 2f;
        var at = from + (to - from) * (s0 / (s0 - s1));
        var cp = XpbdSolver.ClosestOnTriangle(at, c.A, c.B, c.C);
        if (!(Vector3.DistanceSquared(at, cp) < r * r)) return 2f;
        float a0 = MathF.Abs(s0), a1 = MathF.Abs(s1);
        return a0 <= r ? 0f : (a0 - r) / (a0 + a1);
    }

    /// <summary>How far along the way (0..1) it is r off box c, when it goes into the box from outside (2: it does not),
    /// and how far it would get in.</summary>
    static float BoxTime(Vector3 from, Vector3 to, float r, in FleshCollider c, out Vector3 n, out float deep)
    {
        n = Vector3.UnitY; deep = 0f;
        var inv = Quaternion.Conjugate(c.Q);
        var a = Vector3.Transform(from - c.A, inv);
        var b = Vector3.Transform(to - c.A, inv);
        if (!Slab(a, b, c.B, out float t0, out int axis, out float sign) || !(t0 >= 0f)) return 2f;   // misses it, or starts in it
        deep = (1f - t0) * Vector3.Distance(a, b);
        if (!Slab(a, b, c.B + new Vector3(r), out float tr, out _, out _)) tr = t0;
        n = Vector3.Transform(axis == 0 ? new Vector3(sign, 0f, 0f) : axis == 1 ? new Vector3(0f, sign, 0f) : new Vector3(0f, 0f, sign), c.Q);
        return Math.Clamp(tr, 0f, t0);
    }

    /// <summary>Where along a to b (box space) the way goes into the box of half size hb (t0, the face's axis and side);
    /// false when it misses it or stops short.</summary>
    static bool Slab(Vector3 a, Vector3 b, Vector3 hb, out float t0, out int axis, out float sign)
    {
        t0 = float.NegativeInfinity; axis = -1; sign = 1f;
        float t1 = float.PositiveInfinity;
        for (int k = 0; k < 3; k++)
        {
            float s = Axis(a, k), e = Axis(b, k), hh = Axis(hb, k), d = e - s;
            if (MathF.Abs(d) < 1e-12f)
            {
                if (s <= -hh || s >= hh) return false;
                continue;
            }
            float ta = (-hh - s) / d, tb = (hh - s) / d;
            float tin = MathF.Min(ta, tb);
            if (tin > t0) { t0 = tin; axis = k; sign = d > 0f ? -1f : 1f; }
            t1 = MathF.Min(t1, MathF.Max(ta, tb));
        }
        return axis >= 0 && t0 < t1 && t0 <= 1f && t1 > 0f;
    }

    static float Axis(Vector3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;

    // ------------------------------------------------------------------ the mesh as it shows

    void WriteMesh(float alpha)
    {
        float s = float.IsFinite(Shrink) ? Math.Clamp(Shrink, 0f, 1f) : 1f;
        bool all = _first || s != _shown;
        _first = false;
        Changed = false;
        var P = Pieces;
        int f = P.FrontVertices;
        float back = PopCut.BackOffset * s;
        for (int i = 0; i < P.Count; i++)
        {
            if (_asleep[i] && !_dirty[i] && !all) continue;
            _dirty[i] = false;
            Changed = true;
            if (_soft != null) { SoftWrite(i, alpha, s, back, f); continue; }
            var x = _asleep[i] ? X[i] : Vector3.Lerp(_x0[i], X[i], alpha);
            var q = _asleep[i] ? Q[i] : Quaternion.Slerp(_q0[i], Q[i], alpha);
            var m = Matrix4x4.CreateFromQuaternion(q);
            for (int v = P.VertexStart[i], e = v + P.VertexCount[i]; v < e; v++)
            {
                var p = x + Vector3.TransformNormal(_restPos[v] * s, m);
                var n = Vector3.TransformNormal(_restNrm[v], m);
                var t = _restTan[v];
                var tt = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), m);
                OutPositions[v] = p; OutNormals[v] = n; OutTangents[v] = new Vector4(tt, t.W);
                OutPositions[v + f] = p - n * back; OutNormals[v + f] = -n; OutTangents[v + f] = new Vector4(tt, -t.W);
            }
            Changed = true;
        }
        _shown = s;
    }

    /// <summary>The pieces' bounds as they show (for the renderer's culling).</summary>
    public void Bounds(out Vector3 lo, out Vector3 hi)
    {
        lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
        for (int i = 0; i < Pieces.Count; i++)
        {
            var r = new Vector3(RadiusOf(i));
            lo = Vector3.Min(lo, X[i] - r); hi = Vector3.Max(hi, X[i] + r);
        }
        if (Pieces.Count == 0) { lo = hi = Vector3.Zero; }
    }

    /// <summary>How far piece i's vertices are from its place at most (a soft piece's, bent any way, from its middle).</summary>
    float RadiusOf(int i) => _soft != null ? _softRadius[i] : Pieces.Radius[i];

    /// <summary>The bounds of the pieces still awake, with where they would get to in <paramref name="ahead"/> seconds at
    /// their speed (the plugin gathers the world's shapes there; sleeping pieces need none). False when every piece
    /// sleeps.</summary>
    public bool AwakeBounds(float ahead, out Vector3 lo, out Vector3 hi)
    {
        lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
        bool any = false;
        for (int i = 0; i < Pieces.Count; i++)
        {
            if (_asleep[i]) continue;
            var r = new Vector3(RadiusOf(i) + (_soft != null ? _vmax[i] * ahead : 0f));
            var to = X[i] + V[i] * ahead;
            lo = Vector3.Min(lo, Vector3.Min(X[i], to) - r); hi = Vector3.Max(hi, Vector3.Max(X[i], to) + r);
            any = true;
        }
        if (!any || !float.IsFinite(lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z)) { lo = hi = Vector3.Zero; return false; }
        return true;
    }

    // ------------------------------------------------------------------ pairs

    FleshCollider[] _shapes = Array.Empty<FleshCollider>();
    int _shapeCount;
    readonly Vector3[] _lo, _hi;
    readonly int[] _stamp;
    int _stampGen;
    readonly int[] _pairStart;
    int[] _pairShape = Array.Empty<int>(), _tmpPiece = Array.Empty<int>(), _tmpShape = Array.Empty<int>();
    int _pairCount;
    readonly List<int> _triHits = new();
    readonly Dictionary<long, int> _cellHead = new();
    int[] _entryNext = Array.Empty<int>(), _entryPiece = Array.Empty<int>();
    int _entries;
    bool _nearAll;

    static long CellKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

    void AddShape(in FleshCollider c)
    {
        if (_shapeCount == _shapes.Length) Array.Resize(ref _shapes, Math.Max(16, _shapes.Length * 2));
        ref var sh = ref _shapes[_shapeCount++];
        sh = c;
        sh.Bounds(out sh.BMin, out sh.BMax);
    }

    void AddPair(int piece, int shape)
    {
        if (_pairCount == _tmpPiece.Length)
        {
            int c = Math.Max(64, _tmpPiece.Length * 2);
            Array.Resize(ref _tmpPiece, c); Array.Resize(ref _tmpShape, c);
        }
        _tmpPiece[_pairCount] = piece; _tmpShape[_pairCount] = shape; _pairCount++;
    }

    void BuildPairs(List<FleshCollider> colliders, Vector3 gravity, float simDt)
    {
        int n = Pieces.Count;
        _shapeCount = 0; _pairCount = 0; MeshTriangles = 0;
        Array.Clear(_pairStart);
        if (colliders == null || colliders.Count == 0 || n == 0) return;
        float r = Thickness;
        float m0 = 2f * r + 0.5f * gravity.Length() * simDt * simDt + 0.01f;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        int awake = 0;
        for (int i = 0; i < n; i++)
        {
            if (_asleep[i]) { _lo[i] = new Vector3(float.MaxValue); _hi[i] = new Vector3(float.MinValue); continue; }
            if (_soft != null) SoftBox(i, simDt, m0);
            else
            {
                var x = X[i];
                var d = V[i] * (1.5f * simDt);
                float go = d.Length();
                if (go > MaxReach && float.IsFinite(go))
                {
                    // no further than it looks (see ClothSolver.BuildPairs): only past MaxPairRuns runs
                    float k = MaxReach / go;
                    d *= k; V[i] *= k;
                }
                var pad = new Vector3(Pieces.Radius[i] + m0);
                _lo[i] = Vector3.Min(x, x + d) - pad; _hi[i] = Vector3.Max(x, x + d) + pad;
            }
            if (!float.IsFinite(_lo[i].X + _lo[i].Y + _lo[i].Z + _hi[i].X + _hi[i].Y + _hi[i].Z)) { _lo[i] = new Vector3(float.MaxValue); _hi[i] = new Vector3(float.MinValue); continue; }
            lo = Vector3.Min(lo, _lo[i]); hi = Vector3.Max(hi, _hi[i]);
            awake++;
        }
        if (awake == 0) return;
        BuildNear(n);

        foreach (var c in colliders)
        {
            c.Bounds(out var cmin, out var cmax);
            if (cmin.X > hi.X || cmax.X < lo.X || cmin.Y > hi.Y || cmax.Y < lo.Y || cmin.Z > hi.Z || cmax.Z < lo.Z) continue;
            if (c.Kind != FleshColliderKind.Mesh) { if (Near(cmin, cmax)) AddShape(c); continue; }
            if (c.Mesh == null) continue;
            _triHits.Clear();
            if (!_nearAll && _cellHead.Count <= NearQueries)
                foreach (var key in _cellHead.Keys)
                {
                    var a = CellCorner(key) * NearCell;
                    FleshCollider.TransformBox(a, a + new Vector3(NearCell), c.MInv, out var qa, out var qb);
                    c.Mesh.Query(qa, qb, _triHits);
                }
            else
            {
                FleshCollider.TransformBox(lo, hi, c.MInv, out var qa, out var qb);
                c.Mesh.Query(qa, qb, _triHits);
            }
            _triHits.Sort();
            bool flip = c.M.GetDeterminant() < 0f;
            int last = -1;
            foreach (int t in _triHits)
            {
                if (t == last) continue;
                last = t;
                if (MeshTriangles >= MaxMeshTriangles) break;
                var tri = FleshCollider.Triangle(Vector3.Transform(c.Mesh.A[t], c.M), Vector3.Transform(c.Mesh.B[t], c.M), Vector3.Transform(c.Mesh.C[t], c.M), c.TwoSided);
                tri.Source = c.Source;
                if (flip) { (tri.B, tri.C) = (tri.C, tri.B); tri.SetPlane(); }
                AddShape(tri);
                if (!Near(_shapes[_shapeCount - 1].BMin, _shapes[_shapeCount - 1].BMax)) { _shapeCount--; continue; }
                MeshTriangles++;
            }
        }

        // each shape with the pieces that look where it is
        for (int sh = 0; sh < _shapeCount; sh++)
        {
            var smin = _shapes[sh].BMin; var smax = _shapes[sh].BMax;
            if (++_stampGen == int.MaxValue) { Array.Clear(_stamp); _stampGen = 1; }
            if (!_nearAll && CellsOf(smin, smax, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1))
            {
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!_cellHead.TryGetValue(CellKey(x, y, z), out int e)) continue;
                            for (; e >= 0; e = _entryNext[e])
                            {
                                int i = _entryPiece[e];
                                if (_stamp[i] == _stampGen) continue;
                                _stamp[i] = _stampGen;
                                if (Overlaps(i, smin, smax)) AddPair(i, sh);
                            }
                        }
            }
            else
                for (int i = 0; i < n; i++) if (!_asleep[i] && Overlaps(i, smin, smax)) AddPair(i, sh);
        }

        // the pairs by piece
        for (int k = 0; k < _pairCount; k++) _pairStart[_tmpPiece[k] + 1]++;
        for (int i = 0; i < n; i++) _pairStart[i + 1] += _pairStart[i];
        if (_pairShape.Length < _pairCount) _pairShape = new int[Math.Max(_pairCount, _pairShape.Length * 2)];
        if (_fill.Length < n) _fill = new int[n];
        Array.Copy(_pairStart, _fill, n);
        for (int k = 0; k < _pairCount; k++) _pairShape[_fill[_tmpPiece[k]]++] = _tmpShape[k];
    }

    int[] _fill = Array.Empty<int>();

    bool Overlaps(int i, Vector3 smin, Vector3 smax)
    {
        var a = _lo[i]; var b = _hi[i];
        return a.X <= smax.X && b.X >= smin.X && a.Y <= smax.Y && b.Y >= smin.Y && a.Z <= smax.Z && b.Z >= smin.Z;
    }

    static Vector3 CellCorner(long key)
    {
        int x = (int)(key >> 42) & 0x1FFFFF, y = (int)(key >> 21) & 0x1FFFFF, z = (int)key & 0x1FFFFF;
        // back from 21 bits, signed
        if ((x & 0x100000) != 0) x -= 0x200000;
        if ((y & 0x100000) != 0) y -= 0x200000;
        if ((z & 0x100000) != 0) z -= 0x200000;
        return new Vector3(x, y, z);
    }

    /// <summary>The cells box a..b covers, when that is at most NearMostCells and finite.</summary>
    static bool CellsOf(Vector3 a, Vector3 b, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
    {
        const float inv = 1f / NearCell;
        float fx0 = MathF.Floor(a.X * inv), fy0 = MathF.Floor(a.Y * inv), fz0 = MathF.Floor(a.Z * inv);
        float fx1 = MathF.Floor(b.X * inv), fy1 = MathF.Floor(b.Y * inv), fz1 = MathF.Floor(b.Z * inv);
        x0 = x1 = y0 = y1 = z0 = z1 = 0;
        if (!((fx1 - fx0 + 1f) * (fy1 - fy0 + 1f) * (fz1 - fz0 + 1f) <= NearMostCells)) return false;
        x0 = (int)fx0; x1 = (int)fx1; y0 = (int)fy0; y1 = (int)fy1; z0 = (int)fz0; z1 = (int)fz1;
        return true;
    }

    /// <summary>The 1 m cells each awake piece's box covers, each with the pieces in it (a box more than MostCellsASide
    /// a side, or not finite: everything counts as near).</summary>
    void BuildNear(int n)
    {
        _cellHead.Clear();
        _entries = 0;
        _nearAll = false;
        const float inv = 1f / NearCell;
        for (int i = 0; i < n; i++)
        {
            if (_asleep[i] || _lo[i].X > _hi[i].X) continue;
            var a = _lo[i] * inv; var b = _hi[i] * inv;
            float fx0 = MathF.Floor(a.X), fy0 = MathF.Floor(a.Y), fz0 = MathF.Floor(a.Z), fx1 = MathF.Floor(b.X), fy1 = MathF.Floor(b.Y), fz1 = MathF.Floor(b.Z);
            if (!(fx1 - fx0 < MostCellsASide && fy1 - fy0 < MostCellsASide && fz1 - fz0 < MostCellsASide)) { _nearAll = true; return; }
            for (int x = (int)fx0; x <= (int)fx1; x++)
                for (int y = (int)fy0; y <= (int)fy1; y++)
                    for (int z = (int)fz0; z <= (int)fz1; z++)
                    {
                        long key = CellKey(x, y, z);
                        if (_entries == _entryNext.Length) { int c = Math.Max(256, _entryNext.Length * 2); Array.Resize(ref _entryNext, c); Array.Resize(ref _entryPiece, c); }
                        _entryNext[_entries] = _cellHead.TryGetValue(key, out int head) ? head : -1;
                        _entryPiece[_entries] = i;
                        _cellHead[key] = _entries++;
                    }
        }
    }

    /// <summary>The box a..b is in a cell some piece looks in (true for a box too big to look up).</summary>
    bool Near(Vector3 a, Vector3 b)
    {
        if (_nearAll) return true;
        if (!CellsOf(a, b, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)) return true;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    if (_cellHead.ContainsKey(CellKey(x, y, z))) return true;
        return false;
    }

    // ------------------------------------------------------------------ randomness (the same everywhere)

    static uint Mix(uint h)
    {
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h;
    }

    static uint Hash(uint i, uint seed) => Mix(i * 0x9E3779B1u ^ Mix(seed + 0x632BE59Bu));

    /// <summary>0..1.</summary>
    static float Unit(uint h) => (h >> 8) * (1f / 16777216f);

    static Vector3 RandomUnit(uint h)
    {
        float z = 2f * Unit(h) - 1f;
        float a = 6.2831853f * Unit(Mix(h ^ 0x165667B1u));
        float s = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(s * MathF.Cos(a), s * MathF.Sin(a), z);
    }
}
