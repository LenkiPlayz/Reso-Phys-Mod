using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;
using Softbody.Core.Cloth;
using Softbody.Core.Flesh;
using Softbody.Core.Fluid;

namespace Softbody;

/// <summary>
/// 0.29.0: the local simulation behind one running <see cref="Cloth"/> on a MeshRenderer (world cloth, DESIGN-cloth.md).
///
/// Shown through a private copy of the mesh (<see cref="PrivateMeshes"/>, as SkinnedSoftbody's instancing): a dynamic mesh
/// asset of this client that the renderer's StaticMesh hands out instead of the shared one, so the shared asset and every
/// other object using it are never touched, and two copies of the same cloth each run on their own. Each frame the copy
/// gets the cloth's positions, normals and tangents through the engine's own upload (no renderer fork); a tear sends the
/// whole mesh, with its new vertex count and triangles, which the renderer applies to the same Unity mesh.
///
/// The step (and the shape it outputs) runs on the thread pool: started in frame N, collected at the start of frame N+1
/// and shown then. Everything else (tears, paint, settings, colliders, the upload) happens on the world thread between
/// two steps, with <see cref="SoftbodyRuntime.Gate"/> held.
///
/// Tears: one client decides (<see cref="DecidesTears"/>), splits the strained edges and appends each split to the
/// component's synced Tears list; every other client applies the list in order (see <see cref="ClothMesh.Split"/>), and
/// one that joins later replays it at its build. A list that got shorter (Mend) rebuilds the cloth from scratch.
/// </summary>
sealed partial class ClothInstance : IBrushTarget
{
    public readonly MeshRenderer Renderer;
    public readonly Slot Anchor;
    public readonly StaticMesh Provider;
    public readonly FrooxEngine.Mesh Asset;     // the shared asset
    public readonly Cloth Owner;
    public readonly string BuildKey;
    public object ClaimKey => Provider;
    public string Name => _copyName ?? Anchor.Name;
    public ColorProfile Profile { get; }
    public PaintLayer Paint => _paint;
    Slot IBrushTarget.Anchor => Anchor;

    /// <summary>Welded points a cloth may have (the mesh can have more render vertices).</summary>
    public const int MaxParticles = 8000;
    /// <summary>0.31.0: points tears and a blast's shred may add to a world cloth: up to 5 times as many as it has (enough to
    /// cut every triangle loose), but no more than this (0.29.0 to 0.30.0: a quarter more).</summary>
    public const int MaxTearPoints = 24000;
    /// <summary>0.31.0: seconds after a blast arrives in which the client deciding the tears may start its shred (so an old
    /// blast never shreds later, say when Tearable is turned on).</summary>
    const double FreshBlast = 2.0;

    readonly MeshX _original, _work;
    readonly ClothMesh _mesh;
    readonly ClothSolver _solver;
    readonly PaintLayer _paint;
    readonly Slot[] _anchorSlots;
    readonly int _nv0;
    readonly bool _tangents;
    readonly WorldAssetManager _assetManager;

    // the private copy
    FrooxEngine.Mesh _private;
    volatile bool _privateReady;
    bool _swapped;
    long _privateStartedTs;

    // the step (worker)
    Task _step;
    readonly ClothFrame _frame = new();
    float _gainIn = 1f;
    Matrix4x4 _worldToRendererIn;
    Vector3[] _outPos = Array.Empty<Vector3>(), _outNrm = Array.Empty<Vector3>();
    Vector4[] _outTan = Array.Empty<Vector4>();
    Vector3 _outMin, _outMax;
    int _outVersion = -1, _outCount;
    // for the brush (copied on the world thread after each step: the worker may be writing _outPos while the brush reads)
    Vector3[] _queryCopy;
    bool _queryValid;

    // the work mesh
    int _workVersion;
    volatile bool _ackPending;
    long _uploadSentAt;

    // tears
    int _appliedTears;
    double _pendingSince = -1;
    public int TearsApplied { get; private set; }
    public int TearsSkipped { get; private set; }
    public int TearsMade { get; private set; }
    bool _roomWarned;

    // 0.31.0: blasts
    readonly HashSet<int> _blastsSeen = new(), _blastsPresent = new();
    readonly Dictionary<int, double> _blastSeenAt = new();
    readonly List<PendingKick> _pendingKicks = new();
    readonly List<Vector4> _releases = new();
    readonly ClothShredder _shredder = new();
    public int ShredPartsMade { get; private set; }
    public int ShredPartsApplied { get; private set; }
    public int ShredCuts { get; private set; }
    double _shredWaitSince = -1;

    /// <summary>A blast's push waiting for the next step, as it came (the renderer slot's space).</summary>
    struct PendingKick
    {
        public Vector3 Local;
        public float Radius, Strength, Randomness;
        public int Seed;
    }

    // state
    bool _resumeNext, _culled, _off, _offShown, _paused;
    readonly FluidColliders _colliders = new();
    readonly List<ClothWindSource> _winds = new();
    public string RebuildReason, FailReason;
    public long TotalFrames;
    public double LastFrameMs, LastWaitMs, EmaMainMs, EmaStepMs;
    /// <summary>0.29.1: the minute and 10 s averages the Budget pause goes by (see BudgetWatch).</summary>
    public readonly BudgetWatch Watch = new();
    public long Uploads, FullUploads, BusyFrames, InstanceChanges;
    public int Warmup => 30;
    double _statsMain, _statsStep;
    int _statsFrames;

    // 0.30.0: an avatar cloth waiting for paint has no mesh or solver yet
    public int PointCount => _mesh?.ParticleCount ?? 0;
    public int TriangleCount => _mesh?.TriangleCount ?? 0;
    public int PinnedCount => _solver?.Pinned ?? 0;
    public int AnchoredCount => _solver?.Anchored ?? 0;

    ClothInstance(MeshRenderer renderer, Cloth owner, StaticMesh provider, FrooxEngine.Mesh asset, MeshX original, MeshX work, ClothMesh mesh,
                  ClothSolver solver, PaintLayer paint, Slot[] anchors, bool tangents, string key)
    {
        Renderer = renderer; Anchor = renderer.Slot; Owner = owner; Provider = provider; Asset = asset;
        _original = original; _work = work; _mesh = mesh; _solver = solver; _paint = paint; _anchorSlots = anchors;
        _nv0 = original.VertexCount; _tangents = tangents; BuildKey = key;
        Profile = original.Profile;
        try { _assetManager = renderer.World?.AssetManager; } catch { }
        _workVersion = mesh?.Version ?? 0;
    }

    // ------------------------------------------------------------------ creation

    public static SoftbodyInstance.CreateResult TryCreate(Cloth owner, MeshRenderer renderer, ClothSettings settings, Cloth.StartPaint start, Slot[] anchors,
                                                          float anchorRadius, string key, out ClothInstance inst, out string reason)
    {
        inst = null; reason = null;
        const SoftbodyInstance.CreateResult Wait = SoftbodyInstance.CreateResult.Wait, Fail = SoftbodyInstance.CreateResult.Fail;
        if (renderer == null || renderer.IsRemoved) { reason = "set Renderer to the cloth's MeshRenderer"; return Wait; }
        // 0.30.0: avatar cloth
        if (renderer is SkinnedMeshRenderer smr) return TryCreateSkinned(owner, smr, settings, start, anchors, anchorRadius, key, out inst, out reason);
        if (renderer.GetType() != typeof(MeshRenderer)) { reason = $"{renderer.GetType().Name} is not supported (a MeshRenderer or a SkinnedMeshRenderer only)"; return Fail; }
        if (renderer.Slot == null || renderer.Slot.IsRemoved) { reason = "the renderer's slot was removed"; return Wait; }
        var target = renderer.Mesh.Target;
        if (target == null) { reason = "the MeshRenderer has no mesh"; return Wait; }
        if (target is not StaticMesh provider)
        {
            reason = $"the mesh is a {target.GetType().Name}, not a StaticMesh: a procedural mesh makes its own asset, so bake it first (its Bake Mesh button)";
            return Fail;
        }
        if (PrivateMeshes.Unavailable != null) { reason = "Cloth needs its own copy of the mesh, which is not available in this engine version (" + PrivateMeshes.Unavailable + ")"; return Fail; }
        var asset = provider.Asset;
        if (asset == null || asset.LoadState != AssetLoadState.FullyLoaded || asset.Data == null) { reason = "mesh asset still loading"; return Wait; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>() || Unsafe.SizeOf<color>() != 16 || Unsafe.SizeOf<float4>() != Unsafe.SizeOf<Vector4>()) { reason = "unexpected Elements.Core vector layout"; return Fail; }
        var original = asset.Data;
        int n = original.VertexCount;
        if (n <= 0) { reason = "the mesh has no vertices"; return Fail; }
        if (original.BoneCount > 0 && original.HasBoneBindings) { reason = "this mesh has bones: put the Cloth on its SkinnedMeshRenderer (avatar cloth)"; return Fail; }

        if (!SoftbodyRuntime.TryClaim(provider, owner, out var holder))
        {
            reason = $"this renderer's mesh (its StaticMesh) is already driven by the {holder?.GetType().Name ?? "Softbody"} on '{SoftbodyRuntime.NameOf(holder)}'";
            return Wait;
        }
        bool created = false;
        try
        {
            var tris = new List<int>();
            var triMats = new List<int>();
            int si = 0;
            foreach (var sub in original.Submeshes)
            {
                if (sub is TriangleSubmesh ts)
                {
                    tris.AddRange(new ArraySegment<int>(ts.RawIndicies, 0, ts.IndicieCount));
                    for (int k = 0; k < ts.IndicieCount / 3; k++) triMats.Add(si);
                }
                si++;
            }
            if (tris.Count == 0) { reason = "the mesh has no triangles"; return Fail; }
            var trisArr = tris.ToArray();
            var pos = MemoryMarshal.Cast<float3, Vector3>(original.RawPositions.AsSpan(0, n)).ToArray();

            ClothMesh mesh;
            // 0.31.0: room for a blast to shred it to confetti (five times its points, at most MaxTearPoints more)
            try { mesh = new ClothMesh(pos, trisArr, ClothMesh.MaxGrowth, MaxTearPoints); }
            catch (Exception ex) { reason = "reading the mesh failed: " + ex.Message; return Fail; }
            if (mesh.ParticleCount > MaxParticles) { reason = $"the mesh has {mesh.ParticleCount} points after welding (cap {MaxParticles}): use a lower-poly mesh"; return Fail; }
            if (mesh.TriangleCount == 0) { reason = "the mesh has no triangles over three different points"; return Fail; }
            int others = SoftbodyRuntime.TotalClothPoints(owner);
            if (others + mesh.ParticleCount > SoftbodyRuntime.MaxClothPoints)
            {
                reason = $"point cap: other cloths already use {others} of {SoftbodyRuntime.MaxClothPoints} cloth points on this client";
                return Wait;
            }

            // paint: what it starts from, then the component's dabs
            ReadOnlySpan<Vector4> baseCols = ReadOnlySpan<Vector4>.Empty;
            if (start == Cloth.StartPaint.MeshColors && original.HasColors) baseCols = MemoryMarshal.Cast<color, Vector4>(original.RawColors.AsSpan(0, n));
            else if (start != Cloth.StartPaint.MeshColors)
            {
                var fill = new Vector4[n];
                Array.Fill(fill, start == Cloth.StartPaint.AllPinned ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One);
                baseCols = fill;
            }
            PaintLayer paint;
            try { paint = new PaintLayer(pos, baseCols, trisArr, triMats.ToArray()); }
            catch (Exception ex) { reason = "paint layer failed: " + ex.Message; return Fail; }
            var dabs = owner.ReadDabs(original.Profile);
            if (dabs.Length > 0) paint.Update(dabs);
            paint.ClearDirty();

            // the solver, with the mesh's own normals and (when it has them) its tangent frames
            ReadOnlySpan<Vector3> restNrm = original.HasNormals ? MemoryMarshal.Cast<float3, Vector3>(original.RawNormals.AsSpan(0, n)) : ReadOnlySpan<Vector3>.Empty;
            bool tangents = original.HasTangents && original.HasUV0s && original.GetUV_Dimension(0) == 2;
            ReadOnlySpan<Vector2> uv0 = tangents ? MemoryMarshal.Cast<float2, Vector2>(original.RawUV0s.AsSpan(0, n)) : ReadOnlySpan<Vector2>.Empty;
            ReadOnlySpan<Vector4> restTan = tangents ? MemoryMarshal.Cast<float4, Vector4>(original.RawTangents.AsSpan(0, n)) : ReadOnlySpan<Vector4>.Empty;
            var solver = new ClothSolver(mesh, settings, restNrm, uv0, restTan);
            var bright = new float[n];
            paint.ComputeMaps(SoftnessSource.Value, false, bright, Span<float>.Empty);
            solver.SetPaint(bright);

            // where it is now
            var m = SkinMatrices.ToNumerics(renderer.Slot.LocalToGlobal);
            if (!Finite(m) || !Matrix4x4.Invert(m, out var inv)) { reason = "the renderer's slot transform is not finite"; return Wait; }

            // anchors: the points within AnchorRadius of each anchor slot (the nearest anchor wins)
            var anchorOf = new int[mesh.ParticleCount]; Array.Fill(anchorOf, -1);
            var anchorLocal = new Vector3[mesh.ParticleCount];
            var best = new float[mesh.ParticleCount]; Array.Fill(best, float.MaxValue);
            float r2 = anchorRadius * anchorRadius;
            for (int a = 0; a < anchors.Length; a++)
            {
                var s = anchors[a];
                if (s == null || s.IsRemoved) continue;
                var am = SkinMatrices.ToNumerics(s.LocalToGlobal);
                if (!Finite(am) || !Matrix4x4.Invert(am, out var ainv)) continue;
                var at = am.Translation;
                var rest = mesh.RestPositions;
                for (int i = 0; i < mesh.ParticleCount; i++)
                {
                    var wp = Vector3.Transform(rest[i], m);
                    float d2 = Vector3.DistanceSquared(wp, at);
                    if (d2 > r2 || d2 >= best[i]) continue;
                    best[i] = d2; anchorOf[i] = a; anchorLocal[i] = Vector3.Transform(wp, ainv);
                }
            }
            solver.SetAnchors(anchorOf, anchorLocal);

            // the copy the uploads are made from (the asset keeps the original MeshX); it always has normals
            var work = new MeshX(original);
            if (!work.HasNormals) work.HasNormals = true;

            inst = new ClothInstance(renderer, owner, provider, asset, original, work, mesh, solver, paint, anchors, tangents, key);
            // 0.31.0: blasts that came before (the points they freed; no push)
            inst.ReadBlastsAtBuild();
            // tears made before this client built it (saved with the world, or made before it joined; 0.31.0: blasts' shreds)
            inst.ReplayTears();
            if (inst._mesh.Version != inst._workVersion) inst.ApplyTopology();
            // the mesh as modelled (with any tears), in place
            solver.Reset(m);
            inst._frame.RendererToWorld = m;
            inst.Output(inv, 1f);
            inst.WriteStreams();
            inst.StartPrivate();
            created = true;
            return SoftbodyInstance.CreateResult.Created;
        }
        finally
        {
            if (!created) { SoftbodyRuntime.Release(provider, owner); inst = null; }
        }
    }

    static bool Finite(in Matrix4x4 m) =>
        float.IsFinite(m.M11 + m.M12 + m.M13 + m.M21 + m.M22 + m.M23 + m.M31 + m.M32 + m.M33 + m.M41 + m.M42 + m.M43);

    void StartPrivate()
    {
        _ackPending = true;
        _privateStartedTs = Stopwatch.GetTimestamp();
        _private = PrivateMeshes.Create(Renderer.World, _work, Bounds(), Provider, _ => _privateReady = true, dynamic: true);
    }

    bool SwapIn()
    {
        PrivateMeshes.StandIn(_private, Asset);
        if (!PrivateMeshes.Swap(Provider, Asset, _private)) { PrivateMeshes.Retire(_private); return false; }
        _swapped = true;
        _ackPending = false;
        try { _assetManager?.QueueAssetCreated(Provider); } catch { }
        return true;
    }

    // ------------------------------------------------------------------ the frame (world thread, Gate held)

    public enum FrameResult { Ok, Gone, Rebuild, Paused, Failed }

    public FrameResult Frame(World world, float dt, float gain, SoftbodyDebugMesh debug, bool debugColliders)
    {
        if (_sk != null) return FrameSkinned(world, dt, gain, debug, debugColliders);
        if (Anchor.IsRemoved || Renderer.IsRemoved || Provider.IsRemoved) { WaitStep(); return FrameResult.Gone; }
        if (!ReferenceEquals(Renderer.Mesh.Target, Provider)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (Asset.LoadState != AssetLoadState.FullyLoaded || !ReferenceEquals(Asset.Data, _original)) { RebuildReason = "the mesh asset was reloaded"; return FrameResult.Rebuild; }
        if (!_swapped)
        {
            if (_privateReady)
            {
                bool ok;
                try { ok = SwapIn(); }
                catch (Exception ex) { FailReason = "stopped: the cloth's own copy of the mesh could not stand in for the shared one (" + ex.Message + "); the mesh was left untouched"; return FrameResult.Failed; }
                if (!ok) { RebuildReason = "the renderer's mesh changed before the cloth's own copy was ready"; return FrameResult.Rebuild; }
            }
            else
            {
                if (Elapsed(_privateStartedTs) > 30) { FailReason = "stopped: the renderer did not take the cloth's own copy of the mesh within 30 s; the mesh was left untouched"; return FrameResult.Failed; }
                return FrameResult.Ok;
            }
        }
        if (!ReferenceEquals(PrivateMeshes.Held(Provider), _private)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (!Anchor.IsActive || !Renderer.Enabled) { WaitStep(); _paused = true; _resumeNext = true; return FrameResult.Paused; }
        _paused = false;

        long t0 = Stopwatch.GetTimestamp();
        WaitStep();
        long tw = Stopwatch.GetTimestamp();
        LastWaitMs = Ms(tw - t0);

        // 0.31.0: new blasts (their push, the points they free); a blast gone (a Mend) rebuilds it
        if (!BlastsFrame(world, out var gone)) { RebuildReason = gone; return FrameResult.Rebuild; }

        // tears: replay the list (made elsewhere), then this client's own
        if (!TearsFrame(world, out var shorter)) { RebuildReason = shorter; return FrameResult.Rebuild; }

        // show the last step
        Upload();

        _gainIn = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 1f;
        if (Culled(world)) { _pendingKicks.Clear(); Count(t0, 0); return FrameResult.Ok; }

        // this frame's inputs
        var m = SkinMatrices.ToNumerics(Anchor.LocalToGlobal);
        if (!Finite(m) || !Matrix4x4.Invert(m, out var inv)) { _resumeNext = true; Count(t0, 0); return FrameResult.Ok; }
        var f = _frame;
        TakeKicks(f, m);
        f.Dt = dt;
        f.RendererToWorld = m;
        f.Resumed = _resumeNext;
        _resumeNext = false;
        if (f.AnchorToWorld.Length != _anchorSlots.Length) { f.AnchorToWorld = new Matrix4x4[_anchorSlots.Length]; f.AnchorValid = new bool[_anchorSlots.Length]; }
        for (int a = 0; a < _anchorSlots.Length; a++)
        {
            var s = _anchorSlots[a];
            bool ok = s != null && !s.IsRemoved;
            if (ok) { var am = SkinMatrices.ToNumerics(s.LocalToGlobal); ok = Finite(am); if (ok) f.AnchorToWorld[a] = am; }
            f.AnchorValid[a] = ok;
        }
        try { f.Time = world.Time.WorldTime; } catch { f.Time = 0; }
        f.FindTears = _solver.Settings.Tearable && DecidesTears(world) && _mesh.ParticleCount < _mesh.MaxParticles;
        GatherColliders(world, dt);
        GatherWinds(world);
        if (debug != null)
        {
            debug.SubmitCloth(_solver, debugColliders);
        }

        StartStep(f, inv, _gainIn);
        Count(t0, _solver.LastStepMs);
        return FrameResult.Ok;
    }

    /// <summary>Starts a step on the thread pool, with its shape output in the space <paramref name="w2r"/> takes the world
    /// to (collected by <see cref="WaitStep"/> next frame).</summary>
    void StartStep(ClothFrame f, Matrix4x4 w2r, float gainIn)
    {
        var solver = _solver;
        _worldToRendererIn = w2r;
        _step = Task.Run(() =>
        {
            solver.Step(f);
            int nv = solver.Mesh.RenderVertexCount;
            if (_outPos.Length < nv)
            {
                int cap = Math.Max(nv, _outPos.Length + _outPos.Length / 4);
                _outPos = new Vector3[cap]; _outNrm = new Vector3[cap];
                if (_tangents) _outTan = new Vector4[cap];
            }
            solver.FillOutput(w2r, gainIn, _outPos.AsSpan(0, nv), _outNrm.AsSpan(0, nv), _tangents ? _outTan.AsSpan(0, nv) : Span<Vector4>.Empty, out _outMin, out _outMax);
            _outCount = nv;
            _outVersion = solver.Mesh.Version;
        });
    }

    void Count(long t0, double stepMs)
    {
        double main = Ms(Stopwatch.GetTimestamp() - t0);
        LastFrameMs = main;
        TotalFrames++;
        if (TotalFrames > Warmup)
        {
            EmaMainMs = EmaMainMs <= 0 ? main : EmaMainMs + 0.05 * (main - EmaMainMs);
            EmaStepMs = EmaStepMs <= 0 ? stepMs : EmaStepMs + 0.05 * (stepMs - EmaStepMs);
            Watch.Add(main);
        }
        _statsMain += main; _statsStep += stepMs; _statsFrames++;
    }

    /// <summary>Waits for the running step (if any) and takes its result: the shape for the brush.</summary>
    void WaitStep()
    {
        var s = _step;
        if (s == null) return;
        _step = null;
        try { s.Wait(); }
        catch (AggregateException ex) { throw ex.InnerException ?? ex; }
        if (_outVersion >= 0 && _sk != null) KeepQuerySkinned();
        else if (_outVersion >= 0)
        {
            _queryCopy ??= new Vector3[_nv0];
            Array.Copy(_outPos, _queryCopy, Math.Min(_nv0, _outCount));
            _queryValid = true;
        }
    }

    /// <summary>The cloth's shape from the solver as it is now (no step), into the output buffers.</summary>
    void Output(in Matrix4x4 worldToRenderer, float gain)
    {
        int nv = _mesh.RenderVertexCount;
        if (_outPos.Length < nv) { _outPos = new Vector3[nv]; _outNrm = new Vector3[nv]; if (_tangents) _outTan = new Vector4[nv]; }
        _solver.FillOutput(worldToRenderer, gain, _outPos.AsSpan(0, nv), _outNrm.AsSpan(0, nv), _tangents ? _outTan.AsSpan(0, nv) : Span<Vector4>.Empty, out _outMin, out _outMax);
        _outCount = nv;
        _outVersion = _mesh.Version;
    }

    bool Culled(World world)
    {
        _off = !(_gainIn > 0f);
        if (_off)
        {
            // Simulating 0: the mesh as modelled, nothing stepped; it starts again from there
            if (!_offShown)
            {
                _offShown = true;
                var m = SkinMatrices.ToNumerics(FrameSlot.LocalToGlobal);
                if (Finite(m) && Matrix4x4.Invert(m, out var inv)) { _solver.Reset(m); Output(inv, 0f); }
            }
            return true;
        }
        if (_offShown) { _offShown = false; _resumeNext = true; }
        float cull = _solver.Settings.CullDistance;
        bool far = false;
        if (cull > 0f)
        {
            float3? head = null;
            try { head = world.LocalUser?.Root?.HeadPosition; } catch { }
            if (head.HasValue)
            {
                var c = 0.5f * (_outMin + _outMax);
                var wc = Vector3.Transform(c, SkinMatrices.ToNumerics(FrameSlot.LocalToGlobal));
                var h = new Vector3(head.Value.x, head.Value.y, head.Value.z);
                float radius = 0.5f * (_outMax - _outMin).Length();
                far = Vector3.Distance(wc, h) - radius > cull;
            }
        }
        if (far) { if (!_culled) _culled = true; _resumeNext = true; return true; }
        if (_culled) { _culled = false; _resumeNext = true; }
        return false;
    }

    // ------------------------------------------------------------------ tears (world thread, between steps)

    /// <summary>
    /// Who decides the tears: the host; when the host is a headless server (which doesn't simulate cloth), the other user
    /// with the lowest allocation number. Every client works this out the same way, from the user list.
    /// </summary>
    internal static bool DecidesTears(World world)
    {
        try
        {
            var host = world.HostUser;
            var local = world.LocalUser;
            if (host == null || local == null) return world.IsAuthority;
            if (host.HeadDevice != HeadOutputDevice.Headless) return ReferenceEquals(host, local);
            User pick = null;
            foreach (var u in world.AllUsers)
            {
                if (u == null || u.IsRemoved || ReferenceEquals(u, host)) continue;
                if (pick == null || u.AllocationID < pick.AllocationID) pick = u;
            }
            return ReferenceEquals(pick, local);
        }
        catch { return world.IsAuthority; }
    }

    /// <summary>Tears made before this client built the cloth (all of the list).</summary>
    void ReplayTears()
    {
        var list = Owner.Tears;
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            var t = list[i];
            if (t.x == t.y) { TearsSkipped++; continue; }     // never filled in
            // 0.31.0: a part of a blast's shred
            if (ClothBlastMath.IsShredEntry(t.x, t.y))
            {
                if (ApplyShredPart(-t.x, t.y)) ShredPartsApplied++;
                else TearsSkipped++;
                continue;
            }
            if (_solver.Split(t.x, t.y)) TearsApplied++;
            else TearsSkipped++;
        }
        _appliedTears = count;
        if (TearsSkipped > 0) SoftbodyRuntime.Warn($"'{Name}': {TearsSkipped} of {count} saved tears did not fit this mesh and were skipped");
    }

    /// <summary>False (with why) when the list got shorter than what was applied: the cloth must be rebuilt.</summary>
    bool TearsFrame(World world, out string why)
    {
        why = null;
        var list = Owner.Tears;
        int count = list.Count;
        if (count < _appliedTears) { why = count == 0 ? "mended" : "the tear list got shorter"; return false; }
        bool changed = false;
        // made elsewhere (or by this client before it was the one deciding)
        while (_appliedTears < count)
        {
            var t = list[_appliedTears];
            if (t.x == t.y)
            {
                // an element whose value has not arrived yet: wait for it a little
                double now = world.Time.WorldTime;
                if (_pendingSince < 0) _pendingSince = now;
                if (now - _pendingSince < 2.0) break;
                TearsSkipped++;
            }
            else if (ClothBlastMath.IsShredEntry(t.x, t.y))
            {
                // 0.31.0: a part of a blast's shred (its blast arrives first; give it a moment if it has not)
                if (_sk == null && !HasBlast(-t.x))
                {
                    double now = world.Time.WorldTime;
                    if (_shredWaitSince < 0) _shredWaitSince = now;
                    if (now - _shredWaitSince < 2.0) break;
                }
                _shredWaitSince = -1;
                if (ApplyShredPart(-t.x, t.y)) { ShredPartsApplied++; changed = true; }
                else
                {
                    TearsSkipped++;
                    SoftbodyRuntime.LogOnce("cloth-shred-skip:" + Name, $"'{Name}': a blast's shred ({-t.x}, part {t.y}) could not be applied here and was skipped");
                }
            }
            else if (_solver.Split(t.x, t.y)) { TearsApplied++; changed = true; }
            else
            {
                TearsSkipped++;
                SoftbodyRuntime.LogOnce("cloth-tear-skip:" + Name, $"'{Name}': a synced tear ({t.x}, {t.y}) did not fit this cloth's mesh and was skipped");
            }
            _pendingSince = -1;
            _appliedTears++;
        }
        // this client's own, from the last step
        if (_appliedTears == count && _solver.TearCandidates.Count > 0 && _solver.Settings.Tearable && (_sk != null ? DecidesTearsSkinned(world) : DecidesTears(world)))
        {
            foreach (var (a, b, _) in _solver.TearCandidates)
            {
                // 0.30.0: also out of render vertices (an avatar cloth's spare vertices)
                if (_mesh.Full)
                {
                    if (!_roomWarned) { _roomWarned = true; SoftbodyRuntime.Log($"'{Name}': no room for more tears ({_mesh.ParticleCount}/{_mesh.MaxParticles} points, {_mesh.RenderVertexCount} vertices); Mend to start over"); }
                    break;
                }
                // the end with more triangles first (then the lower index); the other end when that one cannot split
                int first = _mesh.FanSize(a) > _mesh.FanSize(b) || (_mesh.FanSize(a) == _mesh.FanSize(b) && a < b) ? a : b;
                int second = first == a ? b : a;
                int p = -1, o = -1;
                if (_solver.Split(first, second)) { p = first; o = second; }
                else if (_solver.Split(second, first)) { p = second; o = first; }
                if (p < 0) continue;
                var e = list.Add();
                e.Value = new int2(p, o);
                _appliedTears++;
                TearsMade++;
                changed = true;
            }
            _solver.TearCandidates.Clear();
        }
        // 0.31.0: this client's own shred parts (world cloth)
        if (_sk == null && _appliedTears == list.Count && _solver.Settings.Tearable && DecidesTears(world) && MakeShredParts(world, list)) changed = true;
        if (changed) _queryValid = false;
        return true;
    }

    // ------------------------------------------------------------------ 0.31.0: blasts (world thread, between steps)

    static bool Filled(Cloth.Blast b, out int id)
    {
        id = 0;
        if (b == null) return false;
        id = b.Id.Value;
        return id > 0 && b.Radius.Value > 0f;
    }

    /// <summary>The blasts that came before this client built the cloth: the points they freed (world cloth); no push.</summary>
    void ReadBlastsAtBuild()
    {
        try { foreach (var b in Owner.Blasts) if (Filled(b, out int id)) _blastsSeen.Add(id); }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("cloth-blasts-read:" + ex.Message, $"'{Name}' reading Blasts: {ex.Message}"); }
        if (_sk == null && _solver != null) UpdateReleases();
    }

    /// <summary>
    /// 0.32.0: the cloth slept (WaitForBlast) until these blasts came, so on this client they are new, as if they had come
    /// while it ran: each one pushes at the next step, and the client that decides tears starts their shred (they count as
    /// arriving now).
    /// </summary>
    internal void TakeBlastsAsNew(World world)
    {
        double now = WorldTime(world);
        try
        {
            foreach (var b in Owner.Blasts)
            {
                if (!Filled(b, out int id) || _blastSeenAt.ContainsKey(id)) continue;
                _blastsSeen.Add(id);
                _blastSeenAt[id] = now;
                var p = b.Position.Value;
                _pendingKicks.Add(new PendingKick
                {
                    Local = new Vector3(p.x, p.y, p.z), Radius = b.Radius.Value, Strength = b.Strength.Value,
                    Randomness = b.Randomness.Value, Seed = id,
                });
            }
        }
        catch (Exception ex) { SoftbodyRuntime.LogOnce("cloth-blasts-wake:" + ex.Message, $"'{Name}' waking with Blasts: {ex.Message}"); }
    }

    /// <summary>
    /// New blasts: each one's push waits for the next step (it is dropped if the cloth is not stepped then), and on world
    /// cloth the points it frees are freed at once. False, with why, when a blast went away (a Mend): world cloth is then
    /// rebuilt as modelled; avatar cloth only forgets it.
    /// </summary>
    bool BlastsFrame(World world, out string why)
    {
        why = null;
        var list = Owner.Blasts;
        int count = list.Count;
        if (count == 0 && _blastsSeen.Count == 0) return true;
        bool releases = false;
        _blastsPresent.Clear();
        double now = WorldTime(world);
        foreach (var b in list)
        {
            if (!Filled(b, out int id)) continue;
            _blastsPresent.Add(id);
            if (!_blastsSeen.Add(id)) continue;
            _blastSeenAt[id] = now;
            var p = b.Position.Value;
            _pendingKicks.Add(new PendingKick
            {
                Local = new Vector3(p.x, p.y, p.z), Radius = b.Radius.Value, Strength = b.Strength.Value,
                Randomness = b.Randomness.Value, Seed = id,
            });
            if (_sk == null && b.Release.Value) releases = true;
        }
        if (_blastsPresent.Count < _blastsSeen.Count)
        {
            if (_sk == null) { why = count == 0 ? "mended" : "a blast was removed"; return false; }
            _blastsSeen.IntersectWith(_blastsPresent);
        }
        if (releases && _solver != null) UpdateReleases();
        return true;
    }

    static double WorldTime(World world) { try { return world.Time.WorldTime; } catch { return 0; } }

    /// <summary>The release spheres of every blast so far (the renderer slot's space, which is the mesh's own).</summary>
    void UpdateReleases()
    {
        _releases.Clear();
        foreach (var b in Owner.Blasts)
        {
            if (!Filled(b, out _) || !b.Release.Value) continue;
            var p = b.Position.Value;
            float r = b.Radius.Value;
            if (!float.IsFinite(p.x + p.y + p.z) || !float.IsFinite(r)) continue;
            _releases.Add(new Vector4(p.x, p.y, p.z, r));
        }
        _solver.SetReleases(CollectionsMarshal.AsSpan(_releases));
    }

    /// <summary>The new blasts' pushes for this step, in the world: their centre and radius through the renderer's slot as it
    /// is now (it may have moved since the blast).</summary>
    void TakeKicks(ClothFrame f, in Matrix4x4 rendererToWorld)
    {
        f.KickCount = 0;
        if (_pendingKicks.Count == 0) return;
        var m = rendererToWorld;
        float scale = (new Vector3(m.M11, m.M12, m.M13).Length() + new Vector3(m.M21, m.M22, m.M23).Length() + new Vector3(m.M31, m.M32, m.M33).Length()) / 3f;
        foreach (var k in _pendingKicks)
        {
            var c = Vector3.Transform(k.Local, m);
            float r = k.Radius * scale;
            if (!float.IsFinite(c.X + c.Y + c.Z) || !(r > 0f) || !float.IsFinite(r) || !float.IsFinite(k.Strength)) continue;
            f.AddKick(new ClothKick
            {
                Center = c, Radius = r, Speed = Math.Clamp(k.Strength, -50f, 50f),
                Randomness = float.IsFinite(k.Randomness) ? Math.Clamp(k.Randomness, 0f, 1f) : 0f, Seed = k.Seed,
            });
        }
        _pendingKicks.Clear();
    }

    bool HasBlast(int id)
    {
        foreach (var b in Owner.Blasts) if (Filled(b, out int x) && x == id) return true;
        return false;
    }

    /// <summary>Blast <paramref name="id"/>'s shred, in the cloth's rest space; false when there is no such blast.</summary>
    bool TryShred(int id, out ClothShred shot)
    {
        shot = default;
        foreach (var b in Owner.Blasts)
        {
            if (!Filled(b, out int x) || x != id) continue;
            var p = b.Position.Value;
            shot = new ClothShred { Center = new Vector3(p.x, p.y, p.z), Radius = b.Radius.Value, Shred = b.Shred.Value, Seed = id };
            return true;
        }
        return false;
    }

    /// <summary>Part <paramref name="part"/> of blast <paramref name="id"/>'s shred (a tear-list entry), in the order every
    /// client makes it. False when it cannot be applied here (no such blast, or avatar cloth).</summary>
    bool ApplyShredPart(int id, int part)
    {
        if (_sk != null || _solver == null || !TryShred(id, out var shot)) return false;
        ShredCuts += _shredder.Apply(_solver, id, shot, part);
        return true;
    }

    /// <summary>
    /// The client that decides the tears shreds: the next part of each blast that shreds (one part of each a frame, so a big
    /// shred spreads from the centre over a few frames), added to the tear list for the others. A shred starts only within
    /// <see cref="FreshBlast"/> of its blast arriving here; once started (here or by whoever decided before), it carries on.
    /// </summary>
    bool MakeShredParts(World world, SyncFieldList<int2> list)
    {
        bool changed = false;
        double now = WorldTime(world);
        foreach (var b in Owner.Blasts)
        {
            if (!Filled(b, out int id) || !(b.Shred.Value > 0f)) continue;
            int made = _shredder.PartsMade(id);
            if (made == 0 && !(_blastSeenAt.TryGetValue(id, out double seen) && now - seen < FreshBlast)) continue;
            if (!TryShred(id, out var shot)) continue;
            if (made >= _shredder.PartCount(_mesh, id, shot)) continue;
            if (_mesh.Full)
            {
                if (!_roomWarned) { _roomWarned = true; SoftbodyRuntime.Log($"'{Name}': no room for more tears ({_mesh.ParticleCount}/{_mesh.MaxParticles} points, {_mesh.RenderVertexCount} vertices); Mend to start over"); }
                break;
            }
            ShredCuts += _shredder.Apply(_solver, id, shot, made);
            var e = list.Add();
            e.Value = new int2(-id, made);
            _appliedTears++;
            ShredPartsMade++;
            changed = true;
        }
        return changed;
    }

    /// <summary>0.31.0: where the simulated cloth is in the world (after the last step); false before it has one.</summary>
    public bool TryWorldBox(out Vector3 lo, out Vector3 hi)
    {
        lo = hi = default;
        if (_solver == null) return false;
        lo = _solver.WorldMin; hi = _solver.WorldMax;
        return lo.X <= hi.X && float.IsFinite(lo.X + lo.Y + lo.Z + hi.X + hi.Y + hi.Z) && (lo != Vector3.Zero || hi != Vector3.Zero);
    }

    /// <summary>The work mesh takes the cloth's current topology: the render vertices tears added (copies of the vertices
    /// they come from, appended in order) and every triangle's corners.</summary>
    void ApplyTopology()
    {
        var src = _mesh.RenderSource;
        int want = _mesh.RenderVertexCount;
        for (int v = _work.VertexCount; v < want; v++)
        {
            var dup = _work.DuplicateVertex(src[v]);
            if (dup.Index != v) throw new InvalidOperationException($"a copied vertex landed at {dup.Index}, expected {v}");
        }
        var rt = _mesh.RenderTriangles;
        int off = 0;
        foreach (var sub in _work.Submeshes)
        {
            if (sub is not TriangleSubmesh ts) continue;
            int cnt = ts.IndicieCount;
            rt.Slice(off, cnt).CopyTo(ts.RawIndicies.AsSpan(0, cnt));
            off += cnt;
        }
        _workVersion = _mesh.Version;
    }

    // ------------------------------------------------------------------ the upload (world thread)

    void WriteStreams()
    {
        int nv = _outCount;
        _outPos.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, nv)));
        _outNrm.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, nv)));
        if (_tangents && _work.HasTangents) _outTan.AsSpan(0, nv).CopyTo(MemoryMarshal.Cast<float4, Vector4>(_work.RawTangents.AsSpan(0, nv)));
    }

    BoundingBox Bounds()
    {
        var pad = (_outMax - _outMin) * 0.05f + new Vector3(0.01f);
        var lo = _outMin - pad; var hi = _outMax + pad;
        return BoundingBox.FromPoints(new float3(lo.X, lo.Y, lo.Z), new float3(hi.X, hi.Y, hi.Z));
    }

    void Upload()
    {
        if (_ackPending || !_swapped || _outVersion < 0) { if (_ackPending) BusyFrames++; return; }
        bool topology = _outVersion != _workVersion;
        if (topology && _outVersion != _mesh.Version) return;     // made before the latest tears: wait for the next step
        if (topology) { ApplyTopology(); if (_copySlot != null) HideHeld(); }
        if (_work.VertexCount != _outCount) return;
        WriteStreams();
        bool full = topology || _forceFull;
        MeshUploadHint hint;
        if (full)
        {
            hint = default;
            MeshUploadHintHelper.SetFromMesh(ref hint, _work);
            hint[MeshUploadHint.Flag.Dynamic] = true;
            hint[MeshUploadHint.Flag.Readable] = Provider.Readable.Value;
        }
        else
        {
            hint = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic);
            if (_tangents && _work.HasTangents) hint[MeshUploadHint.Flag.Tangents] = true;
        }
        _ackPending = true;
        _uploadSentAt = Stopwatch.GetTimestamp();
        bool started;
        try { started = AssetUploads.TryUpload(_private, _work, hint, Bounds(), OnAck); }
        catch { _ackPending = false; throw; }
        if (!started) { _ackPending = false; BusyFrames++; return; }
        _forceFull = false;
        Uploads++;
        if (full) FullUploads++;
    }

    void OnAck(bool instanceChanged)
    {
        // the renderer made a new mesh instance: its renderers keep the old one until the engine re-sends their mesh
        if (instanceChanged) { Interlocked.Increment(ref InstanceChanges); try { _assetManager?.QueueAssetCreated(Shower); } catch { } }
        _ackPending = false;
    }

    // ------------------------------------------------------------------ colliders and wind (world thread)

    readonly HashSet<User> _bodyUsers = new();
    BodyShapes.Stats _bodyStats;

    void GatherColliders(World world, float dt)
    {
        var s = _solver.Settings;
        _frame.Colliders.Clear();
        _frame.BodyCount = 0;
        _bodyUsers.Clear();
        _bodyStats = default;
        if (!s.Collide) return;
        uint mask = FluidSim.CategoryWorld;
        bool players = _copySlot != null ? _copyPlayers : Owner.CollidePlayers.Value;
        if (players) mask |= FluidSim.CategoryHead | FluidSim.CategoryBody | FluidSim.CategoryHands;
        // around where the cloth is (its last output, in the world; 0.30.0: an avatar cloth's output is in its bone's space,
        // so the last step's own world box)
        Vector3 lo, hi;
        if (_sk != null) { lo = _solver.WorldMin; hi = _solver.WorldMax; if (lo.X > hi.X) lo = hi = _sk.SkinMats[_sk.Region.Bone].Translation; }
        else FleshCollider.TransformBox(_outMin, _outMax, SkinMatrices.ToNumerics(FrameSlot.LocalToGlobal), out lo, out hi);
        var pad = new Vector3(0.25f);
        // 0.30.0: an avatar cloth ignores its wearer's own colliders (their body is the body shape), but not their spheres
        _colliders.Exclude = _sk != null ? ExcludeSlotsSkinned() : ExcludeSlots();
        // 0.29.2: players as spheres fitted inside their avatars' skin; their bone colliders only until those are ready
        if (players)
        {
            try
            {
                BodyShapes.For(world).Collect(world, lo - pad, hi + pad, ExcludeSlots(), _frame, _bodyUsers, out _bodyStats);
                if (_sk != null) MarkOwnBodies();
            }
            catch (Exception ex)
            {
                _frame.BodyCount = 0; _bodyUsers.Clear(); _bodyStats = default;
                SoftbodyRuntime.LogOnce("bodyshapes-failed", "cloth: players' body shapes failed, using their bone colliders: " + ex);
            }
        }
        _colliders.BodyUsers = _bodyUsers;
        _colliders.Gather(world, lo - pad, hi + pad, dt, mask, _frame.Colliders);
    }

    /// <summary>The players part of the status: how many are body shapes, being built, or on bone colliders (and why).</summary>
    string PlayersState()
    {
        var b = _bodyStats;
        if (b.Shaped + b.Building + b.Fallback == 0) return "";
        var parts = new List<string>();
        if (b.Shaped > 0) parts.Add($"{b.Shaped} as body shapes ({_solver.BodySpheres} spheres near)");
        if (b.Building > 0) parts.Add($"{b.Building} being shaped");
        if (b.Fallback > 0) parts.Add($"{b.Fallback} on bone colliders ({b.FallbackReason})");
        return "; players: " + string.Join(", ", parts);
    }

    Slot[] _exclude;
    Slot[] ExcludeSlots()
    {
        if (_exclude != null) return _exclude;
        var list = new List<Slot> { Anchor };
        foreach (var a in _anchorSlots) if (a != null) list.Add(a);
        return _exclude = list.ToArray();
    }

    void GatherWinds(World world)
    {
        SoftbodyRuntime.CollectWinds(world, _winds);
        if (_frame.Winds.Length < _winds.Count) _frame.Winds = new ClothWindSource[Math.Max(4, _winds.Count)];
        for (int i = 0; i < _winds.Count; i++) _frame.Winds[i] = _winds[i];
        _frame.WindCount = _winds.Count;
    }

    // ------------------------------------------------------------------ settings and paint (world thread)

    public void Tune(ClothSettings s)
    {
        WaitStep();
        _solver?.SetSettings(s);
    }

    public PaintLayer.UpdateResult UpdatePaint(ReadOnlySpan<Dab> dabs)
    {
        if (_sk != null) return UpdatePaintSkinned(dabs);
        var r = _paint.Update(dabs);
        if (r.Kind != PaintLayer.UpdateKind.None)
        {
            WaitStep();
            var bright = new float[_nv0];
            _paint.ComputeMaps(SoftnessSource.Value, false, bright, Span<float>.Empty);
            _solver.SetPaint(bright);
            _paint.ClearDirty();
        }
        return r;
    }

    // ------------------------------------------------------------------ brush

    long _queryAt;

    public void PrepareBrushQuery(Vector3 localFrom, Vector3 localTo)
    {
        long now = Stopwatch.GetTimestamp();
        if (_queryAt != 0 && (now - _queryAt) < Stopwatch.Frequency / 10) return;
        _queryAt = now;
        if (_sk != null) { PrepareBrushQuerySkinned(); return; }
        if (!_queryValid || _queryCopy == null) return;
        _paint.SetQueryPositions(_queryCopy);
    }

    public Vector3 BindPoint(in MeshHit hit) => _paint.HasQueryPose ? _paint.RestPoint(hit) : hit.Point;
    public float BindScale(in MeshHit hit) => _sk != null ? BindScaleSkinned(hit) : 1f;
    public string MaterialName(int index) => BrushMaterials.Name(Renderer?.Materials, index);

    // ------------------------------------------------------------------ teardown

    public void Teardown()
    {
        try { _step?.Wait(1000); } catch { }
        _step = null;
        if (_private == null) return;
        if (_copyRenderer != null) { try { if (!_copyRenderer.IsRemoved) _copyRenderer.Enabled = false; } catch { } }
        try
        {
            if (_swapped && PrivateMeshes.Swap(Provider, _private, Asset)) { try { _assetManager?.QueueAssetCreated(Provider); } catch { } }
            // 0.33.0: a copy's own StaticMesh (left with the shared mesh as it goes, like the original's)
            if (_copyProvider != null) PrivateMeshes.Swap(_copyProvider, _private, Asset);
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{Name}': giving the renderer its shared mesh back: {ex.Message}"); }
        PrivateMeshes.Retire(_private);
        PrivateMeshes.UnloadLater(_private);
        _private = null;
    }

    // ------------------------------------------------------------------ status

    public string RunningState
    {
        get
        {
            if (_sk != null) return RunningStateSkinned();
            if (!_swapped) return "starting: making the cloth's own copy of the mesh";
            if (_paused) return "paused: renderer disabled or its slot inactive";
            if (_off) return "off: Simulating is 0 (the mesh as modelled)";
            if (_culled) return $"paused: further than CullDistance ({_solver.Settings.CullDistance:0.#} m) from you";
            string tears = _mesh.Splits > 0 ? $", {_mesh.Splits} tears" : "";
            string room = _mesh.ParticleCount >= _mesh.MaxParticles ? " (no room for more tears: Mend)" : "";
            return $"running: {_mesh.ParticleCount} points ({_solver.Free} free, {_solver.Pinned} pinned, {_solver.Anchored} anchored), {_mesh.TriangleCount} triangles{tears}{room}{BlastsState()}{PlayersState()}";
        }
    }

    /// <summary>0.31.0: the blasts part of the status.</summary>
    string BlastsState()
    {
        int n = _blastsSeen.Count;
        if (n == 0 || _mesh == null || _solver == null) return "";
        string s = n == 1 ? ", 1 blast" : $", {n} blasts";
        if (_sk != null) return s + " (on an avatar only the push)";
        var parts = new List<string>(2);
        if (_solver.Released > 0) parts.Add($"{_solver.Released} points freed");
        if (_mesh.Cuts > 0) parts.Add($"{_mesh.Cuts} cuts");
        return parts.Count > 0 ? s + " (" + string.Join(", ", parts) + ")" : s;
    }

    public string Describe() => _sk != null ? DescribeSkinned() :
        $"{_mesh.ParticleCount} points from {_nv0} vertices, {_mesh.TriangleCount} triangles ({_mesh.RenderTriangleCount} drawn), {_solver.StretchConstraints} edges, " +
        $"{_solver.BendConstraints} bending pairs, {_anchorSlots.Length} anchors, tangents {(_tangents ? "yes" : "no")}, {TearsApplied} tears replayed" +
        (TearsSkipped > 0 ? $" ({TearsSkipped} skipped)" : "") +
        (_blastsSeen.Count > 0 ? $", {_blastsSeen.Count} blasts before ({_solver.Released} points freed, {ShredPartsApplied} shred parts replayed, {_mesh.Cuts} cuts)" : "") +
        $", room for {_mesh.MaxParticles - _mesh.ParticleCount} more points";

    public string StatsLine(double seconds)
    {
        if (_sk != null) return StatsLineSkinned(seconds);
        var ic = CultureInfo.InvariantCulture;
        int frames = Math.Max(1, _statsFrames);
        // 0.33.1: the pairs too (each a point and a shape near it, gone through every step; the most in one run of steps),
        // and the runs the last frame's steps went in
        string line = string.Format(ic, "'{0}' cloth: {1} frames in {2:0.0} s, main {3:0.000} ms/frame, step {4:0.000} ms ({5} steps), {6} contacts, {25} pairs ({26} runs), {7} shapes, max strain {8:0.000}, " +
                                        "uploads {9} ({10} full), busy {11}, tears {12} made / {13} applied / {14} skipped, points {15}/{16}, players shaped {17} ({18} spheres near), " +
                                        "blasts {19} ({20} pushes, {21} points freed, shred parts {22} made / {23} applied, {24} cuts)",
            Name, _statsFrames, seconds, _statsMain / frames, _statsStep / frames, _solver.StepsLastFrame, _solver.Contacts, _solver.ShapeCount, _solver.MaxStrain,
            Uploads, FullUploads, BusyFrames, TearsMade, TearsApplied, TearsSkipped, _mesh.ParticleCount, _mesh.MaxParticles, _bodyStats.Shaped, _solver.BodySpheres,
            _blastsSeen.Count, _solver.Kicks, _solver.Released, ShredPartsMade, ShredPartsApplied, _mesh.Cuts, _solver.PairCount, _solver.PairRuns);
        _statsMain = 0; _statsStep = 0; _statsFrames = 0;
        return line;
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Elapsed(long ts) => (Stopwatch.GetTimestamp() - ts) / (double)Stopwatch.Frequency;
}
