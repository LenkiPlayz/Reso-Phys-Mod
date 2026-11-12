using System.Diagnostics;
using System.Numerics;

namespace Softbody.Core.Flesh;

/// <summary>
/// One skinned flesh softbody: built once per (mesh, paint, structural settings), stepped every frame with the
/// renderer's skin matrices (bind space to world, one per bone plus the slot bone) and blendshape weights.
/// <list type="bullet">
/// <item>Targets: every shell vertex is skinned with its surface point's own 4 bones, after that point's blendshape
/// deltas (the inner layers copy them, so a blendshape carries the shell along).</item>
/// <item>The solver gets those targets as its rest shape and its kinematic positions, and simulates the rest.</item>
/// <item>Output: each surface point's world offset from its target, taken back to bind space through its bones'
/// rotation and scale (never an inverse of a blended matrix), blended onto the render vertices by the embedding and
/// scaled by their softness. The renderer adds the blendshapes and skins as usual.</item>
/// </list>
/// </summary>
public sealed class FleshRig
{
    public FleshSettings Settings { get; }
    public SimSurface Surface { get; }
    public TetShell Shell { get; }
    public FleshEmbedding Embedding { get; }
    public XpbdSolver Solver { get; }
    public FleshNormals Normals { get; }
    /// <summary>Unique tet edges, as pairs of shell vertex indices (for the debug view).</summary>
    public int[] TetEdges { get; }
    public int MatrixCount { get; }
    public double BuildMs { get; }

    readonly int _n, _layers;
    readonly int[] _vb; readonly float[] _vw;     // 4 per surface point
    readonly int[][] _bsIdx; readonly Vector3[][] _bsDp;
    readonly Vector3[] _restNow, _t0, _t1;
    readonly Matrix4x4[] _inv;
    readonly Vector3[] _uBind, _vertOff;
    readonly Vector3[] _shift;      // per surface point: rest-space blendshape change this frame that carries the flesh
    float[] _prevW = Array.Empty<float>();
    bool _started;

    public float MaxOffset { get; private set; }
    public double StepMs { get; private set; }
    public int Resets { get; private set; }
    public string LastResetReason { get; private set; } = "";
    public int ActiveShapes { get; private set; }
    /// <summary>World-space targets of the shell vertices after the last step.</summary>
    public ReadOnlySpan<Vector3> Targets => _t1;
    /// <summary>The moving render vertices and their bind-space offsets (same order).</summary>
    public ReadOnlySpan<int> MovingVertices => Embedding.Vertices;
    public ReadOnlySpan<Vector3> BindOffsets => _vertOff;

    FleshRig(FleshSettings s, SimSurface surf, TetShell shell, FleshEmbedding emb, XpbdSolver solver, FleshNormals normals, int[] vb, float[] vw,
             int[][] bsIdx, Vector3[][] bsDp, int matrices, double buildMs)
    {
        Settings = s; Surface = surf; Shell = shell; Embedding = emb; Solver = solver; Normals = normals; _vb = vb; _vw = vw;
        TetEdges = UniqueEdges(shell.Tets);
        _bsIdx = bsIdx; _bsDp = bsDp; MatrixCount = matrices; BuildMs = buildMs;
        _n = shell.OuterCount; _layers = shell.Layers;
        int nv = shell.VertexCount;
        _restNow = new Vector3[nv]; _t0 = new Vector3[nv]; _t1 = new Vector3[nv];
        _inv = new Matrix4x4[matrices];
        _uBind = new Vector3[_n]; _vertOff = new Vector3[emb.Count]; _uShown = new Vector3[_n];
        _shift = new Vector3[_n];
    }

    /// <summary>Null (with the reason) when nothing is painted soft.</summary>
    public static FleshRig Build(FleshInput input, FleshSettings settings, out string report)
    {
        var sw = Stopwatch.StartNew();
        var s = settings.Clone(); s.Sanitize();
        var surf = SimSurface.Build(input.Positions, input.Normals, input.Triangles, input.Softness, s.SoftThreshold, s.SimPoints);
        if (surf.SoftCount == 0) { report = "nothing is painted soft"; return null; }
        var skin = input.Skin;
        int n = surf.Count;
        var vb = new int[n * 4]; var vw = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            int r = surf.WeldRep[surf.SimToWeld[i]];
            for (int q = 0; q < 4; q++) { vb[i * 4 + q] = skin.VertexBones[r * 4 + q]; vw[i * 4 + q] = skin.VertexWeights[r * 4 + q]; }
        }
        var heads = input.BoneHeads ?? Array.Empty<Vector3>(); var tails = input.BoneTails ?? heads;
        var shell = TetShell.Build(surf, vb, vw, heads, tails, s);
        var emb = FleshEmbedding.Build(input.Positions, input.Normals, input.Softness, s.SoftThreshold, surf);

        // blendshape deltas of the surface points (one render vertex per welded vertex)
        int shapes = input.Blendshapes?.Count ?? 0;
        var bsIdx = new int[shapes][]; var bsDp = new Vector3[shapes][];
        var mark = new int[n]; var li = new List<int>(); var ld = new List<Vector3>();
        for (int b = 0; b < shapes; b++)
        {
            input.Blendshapes.GetShape(b, out var vi, out var dp);
            li.Clear(); ld.Clear();
            for (int k = 0; k < vi.Length; k++)
            {
                if ((uint)vi[k] >= (uint)surf.RenderToWeld.Length) continue;
                int p = surf.WeldToSim[surf.RenderToWeld[vi[k]]];
                // 0.26.3: the list also holds vertices a shape only shades (normal or tangent deltas, no move)
                if (p < 0 || mark[p] == b + 1 || dp[k] == Vector3.Zero) continue;
                mark[p] = b + 1;
                li.Add(p); ld.Add(dp[k]);
            }
            bsIdx[b] = li.ToArray(); bsDp[b] = ld.ToArray();
        }

        int nv = shell.VertexCount;
        var soft = new float[nv];
        for (int i = 0; i < nv; i++) soft[i] = surf.Soft[i % n];
        var solver = new XpbdSolver(nv, shell.Tets, shell.Kinematic, soft, shell.TetDepth);
        var normals = new FleshNormals(input.Positions, input.Normals, input.Triangles, surf.RenderToWeld, emb.Vertices, input.Blendshapes);
        var rig = new FleshRig(s, surf, shell, emb, solver, normals, vb, vw, bsIdx, bsDp, skin.MatrixCount, sw.Elapsed.TotalMilliseconds);
        report = $"{surf.RegionSoftVertices} soft of {surf.RegionVertices} region vertices -> {surf.SoftCount} soft + {surf.Count - surf.SoftCount} pinned points, " +
                 $"{shell.TetCount} tets in {shell.Layers} layer(s) ({shell.DroppedTets} dropped, {shell.ThinningRounds} thinning rounds), " +
                 $"{emb.Count} render vertices embedded ({emb.Orphans} orphans); built in {rig.BuildMs:0.0} ms " +
                 $"(weld {surf.WeldMs:0.0}, reduce {surf.ReduceMs:0.0}, shell {shell.BuildMs:0.0}, embed {emb.BuildMs:0.0})";
        return rig;
    }

    static int[] UniqueEdges(int[] tets)
    {
        var set = new HashSet<long>();
        var list = new List<int>();
        for (int e = 0; e + 3 < tets.Length; e += 4)
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                {
                    int a = tets[e + i], b = tets[e + j];
                    if (a > b) (a, b) = (b, a);
                    if (set.Add(((long)a << 32) | (uint)b)) { list.Add(a); list.Add(b); }
                }
        return list.ToArray();
    }

    /// <summary>
    /// 0.11.3: each simulated skin point's collision radius is half the mean distance to its neighbours where the animation
    /// puts them this frame, so it follows the skinning's scale and stretch and the blendshapes (a blendshape that swells
    /// a part grows its collision spheres, one that pulls fluff in shrinks them). Pinned and inner points do not collide.
    /// </summary>
    // ---- volume preservation (0.12.0)
    float[] _bulge = Array.Empty<float>(), _bulgeGoal = Array.Empty<float>(), _ringW = Array.Empty<float>();
    int[] _ring = Array.Empty<int>();
    readonly Queue<int> _bfs = new();
    /// <summary>Volume pushed in by dents in the last step, and pushed back out as a bulge (cubic metres).</summary>
    public float DentVolume { get; private set; }
    public float BulgeVolume { get; private set; }
    const int BulgeRings = 3;
    static readonly float[] RingWeight = { 0f, 1f, 0.6f, 0.3f };
    const float BulgeEase = 0.05f;

    /// <summary>
    /// 0.12.0, volume preservation: after a step, the volume the touching skin points were pushed in (each one's depth
    /// below its animated place times its share of the surface, about its spacing squared) is handed to the free skin points
    /// up to three neighbour rings around the dents (weights 1, 0.6, 0.3), scaled by VolumePreservation, as an outward
    /// offset of their animated place; each is capped at half its shell's thickness. Applied the next frame (eased over
    /// about 0.05 s) by moving their targets out, so the follow springs and the rest shape both carry the bulge: squeezing
    /// the flesh in pushes the edges of the dent out.
    /// </summary>
    void MeasureDents()
    {
        if (_bulgeGoal.Length != _n) { _bulge = new float[_n]; _bulgeGoal = new float[_n]; _ringW = new float[_n]; _ring = new int[_n]; }
        Array.Clear(_bulgeGoal);
        DentVolume = 0f;
        float vp = Settings.VolumePreservation;
        if (!(vp > 0f)) return;
        var cw = Solver.ContactWeight; var x = Solver.X; var inward = Solver.Inward; var r = Solver.Radius;
        if (cw.Length < _n) return;
        Array.Fill(_ring, -1);
        _bfs.Clear();
        float vol = 0f;
        for (int i = 0; i < _n; i++)
        {
            if (cw[i] < 0.99f || Shell.Kinematic[i] || inward[i] == Vector3.Zero) continue;
            float depth = Vector3.Dot(x[i] - _t1[i], inward[i]);
            if (depth > 0f) vol += depth * 4f * r[i] * r[i];
            _ring[i] = 0;
            _bfs.Enqueue(i);
        }
        DentVolume = vol;
        if (!(vol > 0f)) return;
        // rings around the dents, through the sim surface's neighbours
        float wsum = 0f;
        while (_bfs.Count > 0)
        {
            int i = _bfs.Dequeue();
            if (_ring[i] >= BulgeRings) continue;
            for (int k = Surface.NbrStart[i]; k < Surface.NbrStart[i + 1]; k++)
            {
                int j = Surface.Nbr[k];
                if (_ring[j] >= 0) continue;
                _ring[j] = _ring[i] + 1;
                _bfs.Enqueue(j);
                if (Shell.Kinematic[j] || inward[j] == Vector3.Zero) continue;
                float w = RingWeight[_ring[j]] * 4f * r[j] * r[j];
                _ringW[j] = w;
                wsum += w;
            }
        }
        if (!(wsum > 0f)) return;
        float s = vp * vol / wsum;
        for (int j = 0; j < _n; j++)
        {
            if (_ring[j] <= 0 || Shell.Kinematic[j] || inward[j] == Vector3.Zero) continue;
            float cap = 0.5f * MathF.Max(0f, Vector3.Dot(_t1[_layers * _n + j] - _t1[j], inward[j]));
            _bulgeGoal[j] = MathF.Min(RingWeight[_ring[j]] * s, cap);
        }
    }

    /// <summary>Moves the free skin points' animated places out by their (eased) bulge. Returns nothing; sets BulgeVolume.</summary>
    void ApplyBulge(float dt)
    {
        if (_bulge.Length != _n) return;
        float ease = 1f - MathF.Exp(-dt / BulgeEase);
        float vol = 0f;
        var inward = Solver.Inward; var r = Solver.Radius;
        for (int j = 0; j < _n; j++)
        {
            float b = _bulge[j] + (_bulgeGoal[j] - _bulge[j]) * ease;
            if (b < 1e-6f) b = 0f;
            _bulge[j] = b;
            if (b == 0f || inward[j] == Vector3.Zero) continue;
            _t1[j] -= inward[j] * b;
            vol += b * 4f * r[j] * r[j];
        }
        BulgeVolume = vol;
    }

    sbyte[] _triSign;           // per sim triangle: +1 when its winding faces out of the body
    Vector3[] _nrmAcc;

    /// <summary>
    /// 0.11.4: each skin point's direction into the body where the animation puts the skin this frame (area-weighted
    /// triangle normals of the animated surface, oriented by the mesh's own normals at build time), and how deep a
    /// contact may push it: half the animated shell's thickness there (so it never flattens or passes the inner layer).
    /// </summary>
    void UpdateInward()
    {
        var tris = Surface.Tris;
        if (_triSign == null)
        {
            _triSign = new sbyte[tris.Length / 3];
            for (int t = 0; t < _triSign.Length; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                var fn = Vector3.Cross(Surface.Pos[b] - Surface.Pos[a], Surface.Pos[c] - Surface.Pos[a]);
                _triSign[t] = (sbyte)(Vector3.Dot(fn, Surface.Nrm[a] + Surface.Nrm[b] + Surface.Nrm[c]) >= 0f ? 1 : -1);
            }
            _nrmAcc = new Vector3[_n];
        }
        Array.Clear(_nrmAcc);
        for (int t = 0; t < _triSign.Length; t++)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            var fn = Vector3.Cross(_t1[b] - _t1[a], _t1[c] - _t1[a]) * _triSign[t];
            _nrmAcc[a] += fn; _nrmAcc[b] += fn; _nrmAcc[c] += fn;
        }
        var inward = Solver.Inward; var limit = Solver.InwardLimit;
        for (int i = 0; i < _n; i++)
        {
            float l = _nrmAcc[i].Length();
            if (Shell.Kinematic[i] || !(l > 1e-20f) || !float.IsFinite(l)) { inward[i] = Vector3.Zero; limit[i] = 0f; continue; }
            inward[i] = -_nrmAcc[i] / l;
            // half as deep as the animated shell is thick there (outer to innermost layer): deeper dents flatten the tets
            // under the contact until their neighbours turn inside out (offline: 0.8 gave 22 inverted tets under a ball, 0.4
            // gave one)
            limit[i] = 0.5f * MathF.Max(0f, Vector3.Dot(_t1[_layers * _n + i] - _t1[i], inward[i]));
        }
    }

    void UpdateRadii()
    {
        var nb = Surface.Nbr; var start = Surface.NbrStart; var r = Solver.Radius;
        double scale = 0; int scaled = 0;
        for (int i = 0; i < Solver.N; i++)
        {
            if (i >= _n || Shell.Kinematic[i]) { r[i] = 0f; continue; }
            float sum = 0f; int c = 0;
            var p = _t1[i];
            for (int k = start[i]; k < start[i + 1]; k++) { sum += Vector3.Distance(p, _t1[nb[k]]); c++; }
            float rr = c > 0 ? 0.5f * sum / c : r[i];
            r[i] = float.IsFinite(rr) ? rr : 0f;
            float edge = Surface.VertexEdge[i];
            if (r[i] > 0f && edge > 1e-9f) { scale += r[i] / (0.5f * edge); scaled++; }
        }
        _sizeScale = scaled > 0 && double.IsFinite(scale) ? (float)(scale / scaled) : 1f;
    }

    /// <summary>
    /// 0.26.6: the fastest the animation may fling the flesh, in metres a second for an avatar at its modelled size
    /// (scaled with it; 0 = no limit). Any faster motion of a point's animated place (a snap turn, a locomotion snap, a
    /// tracking jump short of <see cref="FleshSettings.TeleportDistance"/>) carries the flesh along instead of leaving it
    /// behind to whip back, as <see cref="CarryBlendshapes"/> does for blendshapes. Real bodies stay well under it.
    /// </summary>
    public float MaxInertialSpeed = 5f;
    float _sizeScale = 1f;
    /// <summary>Points carried along in the last step because their animated place moved faster than MaxInertialSpeed.</summary>
    public int Carried { get; private set; }

    void CapInertia(float dt)
    {
        Carried = 0;
        if (!(MaxInertialSpeed > 0f) || !(dt > 0f)) return;
        float speed = MaxInertialSpeed * MathF.Max(_sizeScale, 1e-3f);
        float cap = speed * dt;
        float cap2 = cap * cap;
        var x = Solver.X;
        for (int i = 0; i < _t1.Length; i++)
        {
            var d = _t1[i] - _t0[i];
            float l2 = d.LengthSquared();
            if (!(l2 > cap2)) continue;
            var carry = d * (1f - cap / MathF.Sqrt(l2));
            x[i] += carry; _t0[i] += carry;
            Carried++;
        }
        if (Carried == 0) return;
        // the avatar's own bone colliders move with the body: they are carried the same way, so they sweep through the
        // frame no faster than the flesh can follow (swept the whole jump, they plowed through the carried flesh and left
        // points stuck on their wrong side)
        var cols = Solver.Colliders;
        for (int k = 0; k < cols.Count; k++)
        {
            var c = cols[k];
            if (c.Source != 2) continue;
            float v = c.Velocity.Length();
            if (!(v > speed)) continue;
            c.Velocity *= speed / v;
            cols[k] = c;
        }
    }

    /// <summary>Takes the live settings (stiffness, damping, ...) from <paramref name="s"/>; call between steps.</summary>
    public void Tune(FleshSettings s)
    {
        var c = s.Clone(); c.Sanitize();
        Settings.CopyLiveFrom(c);
    }

    void ComputeTargets(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<float> weights, Vector3[] dst)
    {
        Array.Copy(Shell.Rest, _restNow, _restNow.Length);
        int active = 0;
        for (int b = 0; b < _bsIdx.Length && b < weights.Length; b++)
        {
            float w = weights[b];
            if (!float.IsFinite(w) || MathF.Abs(w) <= 1e-6f) continue;
            active++;
            var idx = _bsIdx[b]; var dp = _bsDp[b];
            for (int k = 0; k < idx.Length; k++)
            {
                var d = dp[k] * w;
                for (int l = 0; l <= _layers; l++) _restNow[l * _n + idx[k]] += LayerDelta(d, idx[k], l);
            }
        }
        ActiveShapes = active;
        int nv = dst.Length;
        for (int i = 0; i < nv; i++)
        {
            int o = (i % _n) * 4;
            var p = _restNow[i];
            var t = Vector3.Transform(p, skin[_vb[o]]) * _vw[o];
            float w1 = _vw[o + 1], w2 = _vw[o + 2], w3 = _vw[o + 3];
            if (w1 != 0f) t += Vector3.Transform(p, skin[_vb[o + 1]]) * w1;
            if (w2 != 0f) t += Vector3.Transform(p, skin[_vb[o + 2]]) * w2;
            if (w3 != 0f) t += Vector3.Transform(p, skin[_vb[o + 3]]) * w3;
            dst[i] = t;
        }
    }

    /// <summary>Puts the flesh exactly on its targets, at rest.</summary>
    public void Reset(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<float> blendWeights)
    {
        if (skin.Length < MatrixCount) throw new ArgumentException($"expected {MatrixCount} skin matrices, got {skin.Length}");
        ComputeTargets(skin, blendWeights, _t1);
        Array.Copy(_t1, _t0, _t1.Length);
        Solver.SetRest(_t1, Settings.Density);
        Solver.Reset(_t1);
        UpdateRadii();
        UpdateInward();
        if (_bulge.Length == _n) { Array.Clear(_bulge); Array.Clear(_bulgeGoal); }
        Array.Clear(_vertOff);
        if (_vAlpha.Length > 0) Array.Clear(_vAlpha);
        _touchedCount = 0;
        if (_hasShape.Length > 0) { Array.Clear(_hasShape); Array.Clear(_lastShape); }
        MaxOffset = 0f;
        _prevW = blendWeights.ToArray();
        _started = true;
    }

    public void Step(float dt, ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<float> blendWeights)
    {
        var sw = Stopwatch.StartNew();
        if (!_started) Reset(skin, blendWeights);
        if (skin.Length < MatrixCount) throw new ArgumentException($"expected {MatrixCount} skin matrices, got {skin.Length}");
        Array.Copy(_t1, _t0, _t1.Length);
        ComputeTargets(skin, blendWeights, _t1);
        float jump = 0f;
        for (int i = 0; i < _t1.Length; i++) jump = MathF.Max(jump, Vector3.DistanceSquared(_t0[i], _t1[i]));
        if (!(jump <= Settings.TeleportDistance * Settings.TeleportDistance))
        {
            // a respawn or teleport: start again at rest on the new pose rather than flinging the flesh across the world
            Resets++; LastResetReason = float.IsFinite(jump) ? $"the body jumped {MathF.Sqrt(jump):0.00} m in one frame" : "the pose was not finite";
            Reset(skin, blendWeights);
            StepMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        CarryBlendshapes(skin, blendWeights);
        CapInertia(dt);
        UpdateRadii();
        UpdateInward();
        ApplyBulge(dt);
        Solver.SetRest(_t1, Settings.Density);
        Solver.Step(dt, _t0, _t1, Settings);
        MeasureDents();
        KeepContactShapes();

        // world offsets of the surface points, back to bind space through their bones' rotation and scale
        for (int b = 0; b < MatrixCount; b++)
            if (!Lbs.TryInvertSimilar(skin[b], Lbs.LinearScale(skin[b]), out _inv[b])) _inv[b] = Matrix4x4.Identity;
        float max = 0f;
        var x = Solver.X;
        for (int i = 0; i < _n; i++)
        {
            var u = x[i] - _t1[i];
            // shown at the collider's surface rather than a collision radius in front of it (see CloseContactGap)
            if (CloseContactGap && i < Solver.ContactWeight.Length && Solver.ContactWeight[i] > 0f)
            {
                var cn = Solver.ContactNormal[i];
                float face = Facing(-Vector3.Dot(cn, Solver.Inward[i]));
                if (face > 0f) u += cn * (ContactGap(i) * Solver.ContactWeight[i] * face);
            }
            float l = u.LengthSquared();
            if (l > max) max = l;
            int o = i * 4;
            var ub = Vector3.TransformNormal(u, _inv[_vb[o]]) * _vw[o];
            if (_vw[o + 1] != 0f) ub += Vector3.TransformNormal(u, _inv[_vb[o + 1]]) * _vw[o + 1];
            if (_vw[o + 2] != 0f) ub += Vector3.TransformNormal(u, _inv[_vb[o + 2]]) * _vw[o + 2];
            if (_vw[o + 3] != 0f) ub += Vector3.TransformNormal(u, _inv[_vb[o + 3]]) * _vw[o + 3];
            _uBind[i] = ub;
            _uShown[i] = u;
        }
        MaxOffset = MathF.Sqrt(max);
        long show0 = sw.ElapsedTicks;
        ContactNormalsToBind();
        Embedding.Interpolate(_uBind, _vertOff);
        ContactBlend();
        if (FitContacts) FitToContacts(skin);
        if (OutputGain < 1f) ScaleOutput(OutputGain);
        ContactShowMs = (sw.ElapsedTicks - show0) * 1000.0 / Stopwatch.Frequency;
        StepMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// With BlendshapeInertia below 1, the part of this frame's target change that comes from blendshape weights moves
    /// the flesh (and the previous targets) along with it, so it adds no velocity: only bone motion makes it jiggle.
    /// </summary>
    /// <summary>
    /// 0.11.4: how a skin point's blendshape delta moves layer <paramref name="layer"/> of the shell under it. The outward
    /// part (away from the bone, along the point's outward normal) fades out toward the inner layer, so a blendshape that
    /// inflates a part (a belly) thickens its soft shell instead of carrying a thin shell outward; inward and sideways parts
    /// are copied, so deflating or folding shapes still carry the whole shell and never turn it inside out.
    /// </summary>
    Vector3 LayerDelta(Vector3 d, int point, int layer)
    {
        if (layer == 0) return d;
        var n = Surface.Nrm[point];
        float outward = Vector3.Dot(d, n);
        return outward > 0f ? d - n * (outward * layer / _layers) : d;
    }

    void CarryBlendshapes(ReadOnlySpan<Matrix4x4> skin, ReadOnlySpan<float> weights)
    {
        float carry = 1f - Settings.BlendshapeInertia;
        if (_prevW.Length != weights.Length) { var nw = new float[weights.Length]; Array.Copy(_prevW, nw, Math.Min(_prevW.Length, nw.Length)); _prevW = nw; }
        bool any = false;
        if (carry > 0f)
            for (int b = 0; b < _bsIdx.Length && b < weights.Length; b++)
            {
                float w = float.IsFinite(weights[b]) ? weights[b] : 0f, w0 = float.IsFinite(_prevW[b]) ? _prevW[b] : 0f;
                float dw = (w - w0) * carry;
                if (MathF.Abs(dw) <= 1e-7f) continue;
                if (!any) { Array.Clear(_shift); any = true; }
                var idx = _bsIdx[b]; var dp = _bsDp[b];
                for (int k = 0; k < idx.Length; k++) _shift[idx[k]] += dp[k] * dw;
            }
        weights.CopyTo(_prevW);
        if (!any) return;
        var x = Solver.X;
        for (int i = 0; i < _t1.Length; i++)
        {
            int p = i % _n;
            var d = _shift[p];
            if (d == Vector3.Zero) continue;
            d = LayerDelta(d, p, i / _n);
            int o = p * 4;
            var m = Vector3.TransformNormal(d, skin[_vb[o]]) * _vw[o];
            if (_vw[o + 1] != 0f) m += Vector3.TransformNormal(d, skin[_vb[o + 1]]) * _vw[o + 1];
            if (_vw[o + 2] != 0f) m += Vector3.TransformNormal(d, skin[_vb[o + 2]]) * _vw[o + 2];
            if (_vw[o + 3] != 0f) m += Vector3.TransformNormal(d, skin[_vb[o + 3]]) * _vw[o + 3];
            x[i] += m; _t0[i] += m;
        }
    }

    /// <summary>Adds the bind-space offsets to the moving vertices of a bind-space position array.</summary>
    public void AddBindOffsets(Span<Vector3> positions)
    {
        var v = Embedding.Vertices;
        for (int k = 0; k < v.Length; k++) positions[v[k]] += _vertOff[k];
    }

    /// <summary>0.11.5: blend the render normals of skin pressed against a collider toward the collider's shape.</summary>
    public bool ContactNormals = true;
    Vector3[] _contactBind = Array.Empty<Vector3>();
    float[] _contactW = Array.Empty<float>();
    float[] _contactFace = Array.Empty<float>();   // 0.12.1: how squarely each touching point's collider faces it (Facing)
    bool _anyContact;

    /// <summary>The solver's contact normals (world) of the skin points, in bind space through their bones' rotation.</summary>
    void ContactNormalsToBind()
    {
        if (_contactBind.Length != _n) { _contactBind = new Vector3[_n]; _contactW = new float[_n]; _contactFace = new float[_n]; }
        var cw = Solver.ContactWeight; var cn = Solver.ContactNormal;
        _anyContact = false;
        for (int i = 0; i < _n; i++)
        {
            float w = i < cw.Length ? cw[i] : 0f;
            if (!(w > 1e-3f)) { _contactW[i] = 0f; continue; }
            _anyContact = true;
            var n = cn[i];
            int o = i * 4;
            var nb = Vector3.TransformNormal(n, _inv[_vb[o]]) * _vw[o];
            if (_vw[o + 1] != 0f) nb += Vector3.TransformNormal(n, _inv[_vb[o + 1]]) * _vw[o + 1];
            if (_vw[o + 2] != 0f) nb += Vector3.TransformNormal(n, _inv[_vb[o + 2]]) * _vw[o + 2];
            if (_vw[o + 3] != 0f) nb += Vector3.TransformNormal(n, _inv[_vb[o + 3]]) * _vw[o + 3];
            float l = nb.Length();
            if (!(l > 1e-12f)) { _contactW[i] = 0f; continue; }
            _contactBind[i] = nb / l; _contactW[i] = w;
            _contactFace[i] = Facing(-Vector3.Dot(n, Solver.Inward[i]));
        }
    }

    /// <summary>
    /// Bind-space positions and normals of the moving render vertices (embedding order) after the last step. 0.11.5: where
    /// the skin is pressed against a collider its normals are blended toward the collider's shape (the inverse of the
    /// collider's normal, spread onto the render vertices like the offsets, so the blend fades out smoothly around a dent):
    /// a dent is shaded like the thing pressing it, instead of with the twisted normals of the squeezed triangles.
    /// </summary>
    public void FillOutput(ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm, Span<Vector3> outPos, Span<Vector3> outNrm) =>
        FillOutput(restPos, restNrm, default, outPos, outNrm, default);

    /// <summary>
    /// 0.26.3: the same with tangents (<paramref name="restTan"/> and <paramref name="outTan"/> may be empty), measured on the
    /// shape the last step's blendshape weights make and written as the renderer must get them (it adds the blendshapes
    /// itself; see <see cref="FleshNormals"/>). Tangents turn with their normals, so a normal map keeps its frame.
    /// </summary>
    public void FillOutput(ReadOnlySpan<Vector3> restPos, ReadOnlySpan<Vector3> restNrm, ReadOnlySpan<Vector4> restTan,
                           Span<Vector3> outPos, Span<Vector3> outNrm, Span<Vector4> outTan)
    {
        Normals.Compute(Embedding.Vertices, _vertOff, restPos, restNrm, restTan, _prevW, outPos, outNrm, outTan);
        if (!ContactNormals || !_anyContact || _vAlpha.Length != Embedding.Count) return; // nothing touching: nothing to blend
        for (int t = 0; t < _touchedCount; t++)
        {
            int k = _touched[t];
            float alpha = _vAlpha[k];
            if (!(alpha > 0f)) continue;
            Normals.TurnToward(k, _vContact[k], alpha, outNrm, outTan);
        }
    }

    /// <summary>0.12.1: take the rest shape's curve out of the skin where it is pressed (see <see cref="ContactBlend"/>).</summary>
    public bool FlattenContacts = true;
    Vector3[] _vContact = Array.Empty<Vector3>();
    float[] _vAlpha = Array.Empty<float>();
    /// <summary>Per moving render vertex after a step: how much of it is in contact (0..1; empty before any contact).</summary>
    public ReadOnlySpan<float> ContactAlpha => _vAlpha;

    /// <summary>
    /// Per moving render vertex after a step: its contact normal (the touching points' bind-space contact normals through
    /// the embedding's weights; toward the collider) and how much of it is in contact (0..1), for the shading blend in
    /// <see cref="FillOutput"/>. 0.12.1: and where it is in contact, the rest shape's roundness between the points
    /// (<see cref="FleshEmbedding.Detail"/>, its part along the contact normal) is taken out of its offset, so it lies on
    /// the blend of the pressed points. The points are pressed onto the collider but the render surface between them kept
    /// the belly's roundness, so a box face pressed flat showed a bowed patch, bulging into the box once the gap was
    /// closed. Only what stands out toward the collider (a navel stays a dimple), and only where the collider faces the
    /// skin (<see cref="Facing"/>, per touching point): pushed sideways, at a rim or a box's side, nothing is flattened
    /// (there the contact normal lies along the skin and would pull vertices together). A fixed, smooth field faded by the contact weight,
    /// so it cannot put steps at a box's edge the way snapping each vertex onto the collider did.
    /// </summary>
    void ContactBlend()
    {
        var e = Embedding;
        if (_vAlpha.Length != e.Count) { _vAlpha = new float[e.Count]; _vContact = new Vector3[e.Count]; _vFlat = new Vector3[e.Count]; }
        // last step's touched vertices are cleared, this step's found through the points touching
        for (int t = 0; t < _touchedCount; t++) _vAlpha[_touched[t]] = 0f;
        _touchedCount = 0;
        if (!_anyContact || _contactW.Length != _n) return;
        FindTouchedVertices();
        var det = e.Detail;
        for (int t = 0; t < _touchedCount; t++)
        {
            int k = _touched[t];
            Vector3 acc = Vector3.Zero; float ws = 0f, wf = 0f;
            for (int q = e.Start[k]; q < e.Start[k + 1]; q++)
            {
                int j = e.Index[q];
                float w = e.Weight[q] * _contactW[j];
                if (w > 0f) { acc += _contactBind[j] * w; ws += w; wf += w * _contactFace[j]; }
            }
            float l = acc.Length();
            if (!(ws > 1e-3f) || !(l > 1e-12f)) { _vAlpha[k] = 0f; continue; }
            var n = acc / l;
            float alpha = MathF.Min(1f, ws);
            _vContact[k] = n; _vAlpha[k] = alpha; _vFlat[k] = Vector3.Zero;
            if (!FlattenContacts || det == null) continue;
            float dn = Vector3.Dot(det[k], n);
            if (!(dn > 0f)) continue;
            // fully inside a pressed patch it is flattened; toward its rim, where the skin bends around the collider's
            // edge, much less (squared). Weighted by how squarely the collider faces the touching points (a smooth field
            // through the same weights: gating by each vertex's own normal flattened a navel's floor but not its walls)
            float af = MathF.Min(1f, wf);
            float f = af * af * e.Gain[k];
            if (f > 0f) { _vFlat[k] = n * (dn * f); _vertOff[k] -= _vFlat[k]; }
        }
    }

    // 0.12.1: the render vertices each sim point weighs into (the embedding's weights reversed, built on first contact),
    // so a contact only visits the vertices around it rather than every moving vertex
    int[] _revStart, _revIdx;
    int[] _touched = Array.Empty<int>(), _touchStamp = Array.Empty<int>();
    int _touchedCount, _stamp;

    void FindTouchedVertices()
    {
        var e = Embedding;
        if (_revStart == null)
        {
            var count = new int[_n + 1];
            for (int q = 0; q < e.Index.Length; q++) count[e.Index[q] + 1]++;
            for (int j = 0; j < _n; j++) count[j + 1] += count[j];
            var idx = new int[e.Index.Length]; var fill = (int[])count.Clone();
            for (int k = 0; k < e.Count; k++)
                for (int q = e.Start[k]; q < e.Start[k + 1]; q++) idx[fill[e.Index[q]]++] = k;
            _revStart = count; _revIdx = idx;
            _touched = new int[e.Count]; _touchStamp = new int[e.Count];
        }
        if (++_stamp == int.MaxValue) { Array.Clear(_touchStamp); _stamp = 1; }
        for (int j = 0; j < _n; j++)
        {
            if (!(_contactW[j] > 0f)) continue;
            for (int q = _revStart[j]; q < _revStart[j + 1]; q++)
            {
                int k = _revIdx[q];
                if (_touchStamp[k] == _stamp) continue;
                _touchStamp[k] = _stamp; _touched[_touchedCount++] = k;
            }
        }
    }

    /// <summary>0.12.1: correct what is shown of a pressed patch by how far it is from the collider (see <see cref="FitToContacts"/>).</summary>
    public bool FitContacts = true;

    /// <summary>
    /// The component's Simulating (0..1): what is shown is this fraction of what the simulation does, the offsets and the
    /// shading blend toward colliders alike, so fading it turns the softbody on or off smoothly. The simulation itself runs
    /// at full strength meanwhile (at 0 the instance stops stepping it). Set between steps.
    /// </summary>
    public float OutputGain = 1f;

    void ScaleOutput(float g)
    {
        g = float.IsFinite(g) ? Math.Clamp(g, 0f, 1f) : 1f;
        for (int k = 0; k < _vertOff.Length; k++) _vertOff[k] *= g;
        for (int t = 0; t < _touchedCount; t++) _vAlpha[_touched[t]] *= g;
    }
    Vector3[] _vFlat = Array.Empty<Vector3>();      // per moving vertex: what ContactBlend flattened away (bind)
    Vector3[] _uShown = Array.Empty<Vector3>();     // per point: its shown world offset (with the gap closed)
    float[] _fit = Array.Empty<float>(), _fitW = Array.Empty<float>();
    /// <summary>Last step: render vertices measured against their collider, and the largest correction (metres).</summary>
    public int FitVertices { get; private set; }
    /// <summary>Last step: milliseconds spent spreading the offsets onto the render vertices, with the contact shading,
    /// flattening and fit (0.12.1).</summary>
    public double ContactShowMs { get; private set; }
    public float FitMax { get; private set; }
    /// <summary>Last step: the mean signed distance the measured vertices were found at (metres, &lt; 0 inside).</summary>
    public float FitMean { get; private set; }

    /// <summary>
    /// 0.12.1: closing the points' gap and flattening the rest shape's roundness makes a patch pressed by a flat face lie on
    /// it, but against a round collider the blend between the points sags into it (on a ball pressed 1 cm, 2.6 mm on
    /// average). So, last, each render vertex in contact is placed where it will be shown (approximately: the blend of the
    /// points' shown places, plus its own rest detail turned by the bones, so without its own blendshape detail) and its
    /// signed distance from the shape its most weighted touching point touched is measured. Those distances are averaged
    /// onto the points through the embedding's weights and spread back the same way, so the correction is smooth, a
    /// low-frequency fit that cannot put steps between neighbouring vertices (correcting each vertex by its own distance
    /// was flatter still, but stretched the skin at a box's edge by millimetres more); it moves the vertex along its contact
    /// normal, at most one collision radius, faded by its contact weight. A gap only counts where a vertex is fully in
    /// contact (toward a patch's rim the skin really leaves the collider), sinking in everywhere; vertices found deeper
    /// than two radii inside (a collider pressing past the shell's limit goes through the skin there, by design) are left
    /// out of it.
    /// </summary>
    void FitToContacts(ReadOnlySpan<Matrix4x4> skin)
    {
        FitVertices = 0; FitMax = 0f; FitMean = 0f;
        double sdSum = 0, sdW = 0;
        var e = Embedding;
        if (!_anyContact || _vAlpha.Length != e.Count) return;
        var rad = Solver.Radius;
        if (_hasShape.Length != _n) return;
        if (_fit.Length != _n) { _fit = new float[_n]; _fitW = new float[_n]; }
        Array.Clear(_fit); Array.Clear(_fitW);
        var det = e.Detail;
        for (int ti = 0; ti < _touchedCount; ti++)
        {
            int k = _touched[ti];
            float alpha = _vAlpha[k];
            if (!(alpha > 0.05f)) continue;
            // the most weighted point touching something this step, and where the vertex is shown
            int jb = -1; float wb = 0f;
            Vector3 t = Vector3.Zero, u = Vector3.Zero;
            for (int q = e.Start[k]; q < e.Start[k + 1]; q++)
            {
                int j = e.Index[q]; float w = e.Weight[q];
                t += _t1[j] * w; u += _uShown[j] * w;
                if (_hasShape[j] && w * _contactW[j] > wb) { wb = w * _contactW[j]; jb = j; }
            }
            if (jb < 0) continue;
            var d = det[k] - _vFlat[k];
            int o = jb * 4;
            var dw = Vector3.TransformNormal(d, skin[_vb[o]]) * _vw[o];
            if (_vw[o + 1] != 0f) dw += Vector3.TransformNormal(d, skin[_vb[o + 1]]) * _vw[o + 1];
            if (_vw[o + 2] != 0f) dw += Vector3.TransformNormal(d, skin[_vb[o + 2]]) * _vw[o + 2];
            if (_vw[o + 3] != 0f) dw += Vector3.TransformNormal(d, skin[_vb[o + 3]]) * _vw[o + 3];
            var p = t + dw + u * e.Gain[k];
            float r = rad[jb], reach = 3f * r;
            var y = p;
            // the true distance (no preferred exit face: that choice jumps, and this must be smooth across the mesh)
            if (!XpbdSolver.Resolve(ref y, reach, _lastShape[jb])) continue;           // further out than the reach: not against it
            float sd = reach - Vector3.Distance(p, y);                                 // > 0 in front of it, < 0 inside
            float keep = Math.Clamp((sd + 2f * r) / r, 0f, 1f);                        // fades out from 1 to 2 radii inside
            if (!(keep > 0f) || !float.IsFinite(sd)) continue;
            // in front of it only counts where the vertex is fully in contact: toward a patch's rim the skin is leaving the
            // collider, that gap is real. Inside it always counts.
            float full = Math.Clamp((alpha - 0.75f) / 0.2f, 0f, 1f);
            float wk = alpha * keep * (sd > 0f ? full * full * (3f - 2f * full) : 1f);
            if (!(wk > 0f)) continue;
            FitVertices++; sdSum += sd * wk; sdW += wk;
            for (int q = e.Start[k]; q < e.Start[k + 1]; q++)
            {
                int j = e.Index[q]; float w = e.Weight[q] * wk;
                _fit[j] += w * sd; _fitW[j] += w;
            }
        }
        if (FitVertices == 0) return;
        FitMean = (float)(sdSum / sdW);
        for (int j = 0; j < _n; j++) if (_fitW[j] > 1e-6f) _fit[j] /= _fitW[j];
        for (int t = 0; t < _touchedCount; t++)
        {
            int k = _touched[t];
            float alpha = _vAlpha[k];
            if (!(alpha > 0.05f)) continue;
            float c = 0f, cw = 0f, r = 0f;
            for (int q = e.Start[k]; q < e.Start[k + 1]; q++)
            {
                int j = e.Index[q];
                if (!(_fitW[j] > 1e-6f)) continue;
                float w = e.Weight[q] * MathF.Min(1f, _fitW[j] / 0.5f);   // points with few measured vertices count less
                c += _fit[j] * w; cw += w; r += rad[j] * w;
            }
            if (!(cw > 1e-6f)) continue;
            float lim = r / cw;
            float move = Math.Clamp(c / cw, -lim, lim) * alpha * MathF.Min(1f, cw) * e.Gain[k];
            if (MathF.Abs(move) > FitMax) FitMax = MathF.Abs(move);
            _vertOff[k] += _vContact[k] * move;
        }
    }

    /// <summary>
    /// 0.12.1: close the gap between a pressed skin point and the collider in what is shown. The simulation keeps each skin
    /// point its collision radius away from colliders (so a collider cannot slip between sparse points), which showed as a
    /// gap of that radius between the skin and, say, a box pressed against it. The shown offset of a touching point moves
    /// that radius toward the collider, along its contact normal and scaled by its contact weight and by how squarely the
    /// collider faces it (<see cref="Facing"/>: a collider beside the skin would pull it sideways); the embedding spreads it
    /// onto the render vertices like every offset, so it is smooth and fades out around the patch. (Snapping each render
    /// vertex onto the collider instead was flatter, but put steps across small triangles at a box's edge: spikes.) The
    /// render surface between the points is then flattened (<see cref="ContactBlend"/>) and fitted to the collider
    /// (<see cref="FitToContacts"/>).
    /// </summary>
    public bool CloseContactGap = true;

    /// <summary>0.12.1: how much a contact whose normal makes cosine <paramref name="cos"/> with the skin's outward normal
    /// counts as pressing on the skin rather than beside it: 0 below 0.3 (beyond 73 degrees), 1 above 0.7 (within 46).</summary>
    static float Facing(float cos) { float t = Math.Clamp((cos - 0.3f) / 0.4f, 0f, 1f); return t * t * (3f - 2f * t); }

    /// <summary>How far skin point i is from the surface of the shape it touched this step (kept from its last contact while
    /// the contact fades): its distance out of a shape found by pushing it out with twice its radius as the reach.</summary>
    float ContactGap(int i)
    {
        if (i >= _hasShape.Length || !_hasShape[i]) return 0f;
        float reach = 2f * Solver.Radius[i];
        var x = Solver.X[i]; var y = x;
        if (!XpbdSolver.Resolve(ref y, reach, _lastShape[i])) return 0f;
        return Math.Clamp(reach - Vector3.Distance(x, y), 0f, Solver.Radius[i]);
    }

    FleshCollider[] _lastShape = Array.Empty<FleshCollider>();
    bool[] _hasShape = Array.Empty<bool>();

    /// <summary>
    /// The collider each skin point last touched, kept while its contact fades: a point resting exactly one radius from
    /// a collider is not pushed every step, so the solver's per-step record of it comes and goes (copied, since the
    /// solver's list of nearby shapes is rebuilt every step).
    /// </summary>
    void KeepContactShapes()
    {
        if (_lastShape.Length != _n) { _lastShape = new FleshCollider[_n]; _hasShape = new bool[_n]; }
        var ca = Solver.ContactShapeA; var shapes = Solver.NearShapes; var cw = Solver.ContactWeight;
        for (int i = 0; i < _n; i++)
        {
            int sh = i < ca.Length ? ca[i] : -1;
            if (sh >= 0 && sh < shapes.Length) { _lastShape[i] = shapes[sh]; _hasShape[i] = true; }
            else if (!(i < cw.Length && cw[i] > 1e-3f)) { if (_hasShape[i]) { _hasShape[i] = false; _lastShape[i] = default; } }
        }
    }


    /// <summary>World bounds of the animated shell (its targets) after the last step.</summary>
    public void TargetBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        foreach (var t in _t1) { min = Vector3.Min(min, t); max = Vector3.Max(max, t); }
    }

    /// <summary>
    /// Self-collision (0.11.2): the simulated skin points a collider of the avatar's own already touches at rest, as a bit
    /// per solver vertex (null when it touches none). <paramref name="bindShape"/> is the collider in the mesh's bind space;
    /// a point counts as touching within <paramref name="tolerance"/> times its radius (half its spacing). The solver then
    /// never pairs those points with that collider, so the avatar's own body colliders do not push its flesh out, while a
    /// limb that was clear of a part at rest still presses into it.
    /// </summary>
    public ulong[] TouchingAtRest(in FleshCollider bindShape, float tolerance = 1.5f) => Touching(bindShape, tolerance, Shell.Rest, 1f);

    /// <summary>The same against where the skin points are animated to now (world space; for a collider on no bone).</summary>
    public ulong[] TouchingAtTargets(in FleshCollider worldShape, float tolerance = 1.5f)
    {
        float scale = 0f; int n = 0;
        for (int i = 0; i < _n; i++) if (Solver.Radius[i] > 0f) { scale += Solver.Radius[i] / (0.5f * Surface.VertexEdge[i]); n++; }
        return Touching(worldShape, tolerance, _t1, n > 0 ? scale / n : 1f);
    }

    ulong[] Touching(in FleshCollider shape, float tolerance, Vector3[] at, float radiusScale)
    {
        ulong[] bits = null;
        for (int i = 0; i < _n; i++)
        {
            if (Shell.Kinematic[i]) continue;
            var x = at[i];
            float r = 0.5f * Surface.VertexEdge[i] * tolerance * radiusScale;
            if (!XpbdSolver.Resolve(ref x, r, shape)) continue;
            bits ??= new ulong[(Solver.N + 63) / 64];
            bits[i >> 6] |= 1UL << (i & 63);
        }
        return bits;
    }

    /// <summary>Copies what the debug view draws (call between steps).</summary>
    public void FillDebug(FleshDebugSnapshot s, bool tets, bool colliders)
    {
        s.Tets = tets; s.Colliders = colliders;
        int nv = Solver.N;
        if (s.X.Length < nv) { s.X = new Vector3[nv]; s.Targets = new Vector3[nv]; }
        Array.Copy(Solver.X, s.X, nv); Array.Copy(_t1, s.Targets, nv);
        s.Count = nv; s.OuterCount = _n; s.Layers = _layers;
        s.Kinematic = Shell.Kinematic; s.Edges = TetEdges; s.Radius = Solver.Radius;
        s.Soft = Surface.Soft;
        // line thickness from the world length of the tet edges
        double sum = 0; int cnt = 0;
        for (int e = 0; e + 1 < TetEdges.Length && cnt < 512; e += 2, cnt++) sum += Vector3.Distance(_t1[TetEdges[e]], _t1[TetEdges[e + 1]]);
        s.LineRadius = cnt > 0 ? (float)(sum / cnt) * 0.025f : 0.001f;
        s.ColliderList.Clear();
        foreach (var c in Solver.NearShapes) s.ColliderList.Add(c);
    }

    /// <summary>World offset (position minus target) of shell vertex i.</summary>
    public Vector3 Offset(int i) => Solver.X[i] - _t1[i];
}
