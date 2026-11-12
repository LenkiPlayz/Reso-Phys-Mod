using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody;

/// <summary>
/// One tagged slot. Local-only by construction: it creates nothing in the world. It takes the
/// MeshRenderer's StaticMesh asset (a per-client runtime object, never synced or saved), swaps its
/// MeshX for a private copy, and re-uploads only positions and normals of that copy each frame.
/// Other clients load the same asset URL themselves and see the normal static mesh.
/// On teardown the original MeshX is uploaded back.
/// </summary>
sealed class SoftbodyInstance
{
    public readonly Slot Slot;
    public readonly MeshRenderer Renderer;
    public readonly StaticMesh Provider;
    public readonly FrooxEngine.Mesh Asset;
    public readonly string SettingsKey;
    public readonly SoftbodySettings Settings;
    public string Name => Slot.Name;
    public int PointCount => _rig.Cage.PointCount;

    readonly MeshX _original;
    readonly MeshX _work;
    readonly SoftbodyRig _rig;
    readonly int _n;
    readonly bool _origHighPriority;
    readonly Vector3 _restCenterLocal;
    bool _firstUploadSent, _firstUploadAcked;
    volatile bool _ackPending;
    long _uploadSentAt;
    float _groundTimer, _proxyTimer;
    public float LastDisplacement;
    readonly List<ICollider> _overlap = new();
    readonly List<ProxyShape> _proxyList = new();

    // stats (5 s window)
    public readonly Stats Window = new();
    public long TotalFrames, TotalUploads, TotalAcks;
    public int Warmup = 120;
    public double EmaTotalMs;
    public string UploadPath = "none yet";
    public double LastAckLatencyMs;

    public sealed class Stats
    {
        public int Frames, Uploads, Acks, Busy, Idle, Substeps;
        public double SimMs, DeformMs, UploadMs, MaxTotalMs, QueryMs;
        public void Clear() { Frames = Uploads = Acks = Busy = Idle = Substeps = 0; SimMs = DeformMs = UploadMs = MaxTotalMs = QueryMs = 0; }
    }

    const MeshUploadHint.Flag PartialFlags = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic;
    const int MaxVertices = 250_000;

    SoftbodyInstance(Slot slot, MeshRenderer renderer, StaticMesh provider, FrooxEngine.Mesh asset, MeshX original, MeshX work,
                     SoftbodyRig rig, SoftbodySettings settings, string key, AnchorPose anchor)
    {
        Slot = slot; Renderer = renderer; Provider = provider; Asset = asset; _original = original; _work = work;
        _rig = rig; Settings = settings; SettingsKey = key; _n = work.VertexCount;
        _origHighPriority = asset.HighPriorityIntegration;
        _restCenterLocal = 0.5f * (rig.Cage.RestBoundsMin + rig.Cage.RestBoundsMax);
    }

    public string Describe() =>
        $"{_rig.Cage.PointCount} points ({_rig.Cage.KinematicCount} pinned, cage {_rig.Cage.Nx}x{_rig.Cage.Ny}x{_rig.Cage.Nz}), " +
        $"{_n} verts ({_rig.Deformer.DynamicVertexCount} moving, {_rig.Deformer.NormalVertexCount} with normals), " +
        $"{_rig.Sim.EdgeConstraintCount} edges, {_rig.Sim.VolumeConstraintCount} volumes, {_rig.Sim.ServoCount} servos; " +
        $"softness map {(_rig.Cage.HadSoftnessMap ? $"red {_rig.Cage.MinSoftness:0.##}..{_rig.Cage.MaxSoftness:0.##} (mean {_rig.Cage.MeanSoftness:0.##})" : "none (all soft)")}";

    public int Resets => _rig.Sim.ResetCount;
    public string LastResetReason => _rig.Sim.LastResetReason;
    public bool HasGround => _rig.Sim.HasGround;
    public int ProxyCount => _rig.Sim.ProxyCount;
    public int MovingVerts => _rig.Deformer.DynamicVertexCount;
    public int VertexCount => _n;

    // ------------------------------------------------------------------ creation

    public enum CreateResult { Created, Wait, Fail }

    public static CreateResult TryCreate(Slot slot, SoftbodySettings settings, string key, out SoftbodyInstance inst, out string reason)
    {
        inst = null; reason = null;
        var renderer = slot.GetComponent<MeshRenderer>(r => r.GetType() == typeof(MeshRenderer), false);
        if (renderer == null) { reason = "no plain MeshRenderer on the tagged slot (SkinnedMeshRenderer is not supported yet)"; return CreateResult.Wait; }
        var target = renderer.Mesh.Target;
        if (target == null) { reason = "the MeshRenderer has no mesh"; return CreateResult.Wait; }
        if (target is not StaticMesh provider)
        {
            reason = $"mesh provider is {target.GetType().Name}, not StaticMesh (procedural meshes rebuild their own asset; bake it first)";
            return CreateResult.Fail;
        }
        var asset = provider.Asset;
        if (asset == null || asset.LoadState != AssetLoadState.FullyLoaded || asset.Data == null) { reason = "mesh asset still loading"; return CreateResult.Wait; }
        if (AssetUploads.IsBusy(asset, out _) || AssetUploads.HasPendingRestore(asset)) { reason = "waiting for a previous upload on this mesh"; return CreateResult.Wait; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>() || Unsafe.SizeOf<color>() != 16)
        {
            reason = "unexpected Elements.Core vector layout"; return CreateResult.Fail;
        }

        var original = asset.Data;
        int n = original.VertexCount;
        if (n <= 0) { reason = "mesh has no vertices"; return CreateResult.Fail; }
        if (n > MaxVertices) { reason = $"mesh has {n} vertices (cap {MaxVertices})"; return CreateResult.Fail; }

        var tris = new List<int>();
        foreach (var sub in original.Submeshes)
            if (sub is TriangleSubmesh ts)
                tris.AddRange(new ArraySegment<int>(ts.RawIndicies, 0, ts.IndicieCount));
        if (tris.Count == 0) { reason = "mesh has no triangle submesh"; return CreateResult.Fail; }

        // Private deep copy; the original MeshX is kept untouched for the restore.
        var work = new MeshX(original);
        bool hadNormals = work.HasNormals;
        if (!hadNormals) work.HasNormals = true;
        var pos = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n));
        var nrm = MemoryMarshal.Cast<float3, Vector3>(work.RawNormals.AsSpan(0, n));
        float[] soft = null;
        if (work.HasColors)
        {
            soft = new float[n];
            var cols = work.RawColors;
            for (int i = 0; i < n; i++) soft[i] = cols[i].r;
        }

        var anchor = ReadAnchor(slot);
        if (!anchor.IsFinite) { reason = "slot transform is not finite"; return CreateResult.Wait; }
        SoftbodyRig rig;
        try
        {
            rig = SoftbodyRig.Create(pos, hadNormals ? nrm : ReadOnlySpan<Vector3>.Empty, tris.ToArray(), soft ?? ReadOnlySpan<float>.Empty, settings, anchor);
        }
        catch (Exception ex) { reason = "cage build failed: " + ex.Message; return CreateResult.Fail; }
        if (!hadNormals) rig.Deformer.RestNormals.CopyTo(nrm);

        inst = new SoftbodyInstance(slot, renderer, provider, asset, original, work, rig, settings, key, anchor);
        return CreateResult.Created;
    }

    static Vector3 V(in float3 f) => new(f.x, f.y, f.z);
    static float3 F(in Vector3 v) => new(v.X, v.Y, v.Z);

    static AnchorPose ReadAnchor(Slot s)
    {
        var p = s.GlobalPosition; var q = s.GlobalRotation; var sc = s.GlobalScale;
        return new AnchorPose(V(p), new Quaternion(q.x, q.y, q.z, q.w), V(sc));
    }

    // ------------------------------------------------------------------ per frame

    public enum FrameResult { Ok, Paused, Rebuild, Gone }

    /// <summary>Runs on the world thread from PhysicsManager.PostUpdate (after the engine's own physics step).</summary>
    public FrameResult Frame(World world, float dt)
    {
        if (Slot.IsRemoved || Renderer.IsRemoved || Provider.IsRemoved) return FrameResult.Gone;
        if (!ReferenceEquals(Renderer.Mesh.Target, Provider) || !ReferenceEquals(Provider.Asset, Asset)) return FrameResult.Rebuild;
        if (Asset.LoadState != AssetLoadState.FullyLoaded) return FrameResult.Rebuild;
        if (_firstUploadAcked && !ReferenceEquals(Asset.Data, _work)) return FrameResult.Rebuild; // engine reloaded the asset
        if (!Slot.IsActive || !Renderer.Enabled) return FrameResult.Paused;

        var anchor = ReadAnchor(Slot);
        if (!anchor.IsFinite) return FrameResult.Paused;
        if (_rig.Sim.ScaleChanged(anchor)) return FrameResult.Rebuild;

        long t0 = Stopwatch.GetTimestamp();
        WorldQueries(world, anchor, dt);
        long t1 = Stopwatch.GetTimestamp();
        int steps = _rig.Step(dt, anchor);
        long t2 = Stopwatch.GetTimestamp();
        Window.Substeps += steps;
        Window.QueryMs += Ms(t1 - t0);
        Window.SimMs += Ms(t2 - t1);
        Window.Frames++;
        TotalFrames++;

        double deformMs = 0, uploadMs = 0;

        if (_ackPending)
        {
            Window.Busy++; // renderer has not taken the previous frame yet; never queue a second upload
            if (Elapsed(_uploadSentAt) > 5 && UploadPath != "stalled") { UploadPath = "stalled"; SoftbodyMod.LogWarn($"{Name}: renderer has not acknowledged an upload for 5 s; uploads paused"); }
        }
        else
        {
            long d0 = Stopwatch.GetTimestamp();
            float disp = _rig.UpdateCage(anchor, out float change);
            // The mesh lives in the slot's frame: if the cage did not change shape in that frame since
            // the last upload (at rest, or carried rigidly), the GPU copy is already right.
            bool idle = _firstUploadSent && change < 2e-5f;
            long d1 = d0;
            if (idle) Window.Idle++;
            else
            {
                var pos = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
                var nrm = MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
                _rig.ApplyToMesh(pos, nrm, out var bmin, out var bmax);
                d1 = Stopwatch.GetTimestamp();
                deformMs = Ms(d1 - d0);

                MeshUploadHint hint;
                if (!_firstUploadSent)
                {
                    // First upload: full (layout may differ if we added normals), marked dynamic.
                    hint = default;
                    MeshUploadHintHelper.SetFromMesh(ref hint, _work);
                    hint[MeshUploadHint.Flag.Dynamic] = true;
                    hint[MeshUploadHint.Flag.Readable] = Provider.Readable.Value;
                    Asset.HighPriorityIntegration = true;
                }
                else hint = new MeshUploadHint(PartialFlags);

                var bounds = BoundingBox.FromPoints(F(bmin), F(bmax));
                _ackPending = true;
                _uploadSentAt = Stopwatch.GetTimestamp();
                bool started;
                try { started = AssetUploads.TryUpload(Asset, _work, hint, bounds, OnAck); }
                catch { _ackPending = false; throw; }
                if (!started) { _ackPending = false; Window.Busy++; }
                else
                {
                    UploadPath = _firstUploadSent ? "partial: Positions|Normals (+Dynamic), no index/layout upload" : "full (first upload)";
                    _firstUploadSent = true;
                    _rig.CommitSnapshot();
                    LastDisplacement = disp;
                    Window.Uploads++; TotalUploads++;
                }
                uploadMs = Ms(Stopwatch.GetTimestamp() - d1);
            }
            if (idle) deformMs = Ms(Stopwatch.GetTimestamp() - d0);
        }
        Window.DeformMs += deformMs;
        Window.UploadMs += uploadMs;
        double total = Ms(Stopwatch.GetTimestamp() - t0);
        Window.MaxTotalMs = Math.Max(Window.MaxTotalMs, total);
        if (TotalFrames > Warmup) EmaTotalMs = EmaTotalMs <= 0 ? total : EmaTotalMs + 0.05 * (total - EmaTotalMs);
        LastFrameMs = total;
        return FrameResult.Ok;
    }

    public double LastFrameMs;

    void OnAck(bool instanceChanged)
    {
        LastAckLatencyMs = Elapsed(_uploadSentAt) * 1000;
        _firstUploadAcked = true;
        Interlocked.Increment(ref _acksRaw);
        _ackPending = false;
        if (UploadPath == "stalled") UploadPath = "resumed";
    }
    long _acksRaw;
    public long TakeAcks() { long a = Interlocked.Exchange(ref _acksRaw, 0); TotalAcks += a; return a; }

    // ------------------------------------------------------------------ world colliders -> statics in our sim

    void WorldQueries(World world, in AnchorPose anchor, float dt)
    {
        if (!Settings.Collide) return;
        var physics = world.Physics;
        var center = anchor.TransformPoint(_restCenterLocal);
        var ext = (_rig.Cage.RestBoundsMax - _rig.Cage.RestBoundsMin) * Vector3.Abs(anchor.Scale);

        _groundTimer -= dt;
        if (_groundTimer <= 0)
        {
            _groundTimer = 0.25f;
            var origin = F(center);
            var down = new float3(0, -1, 0);
            var hit = physics.RaycastOne(in origin, in down, 50f, IsWorldCollider, false, null);
            if (hit.HasValue) _rig.Sim.SetGround(V(hit.Value.Point), V(hit.Value.Normal));
            else _rig.Sim.ClearGround();
        }

        _proxyTimer -= dt;
        if (_proxyTimer <= 0)
        {
            _proxyTimer = 0.5f;
            _overlap.Clear();
            var o = F(center);
            var rot = floatQ.Identity;
            var size = F(ext * 1.5f + new Vector3(0.3f));
            physics.BoxOverlap(in o, in rot, in size, _overlap, IsWorldCollider, false);
            _proxyList.Clear();
            foreach (var c in _overlap)
            {
                if (_proxyList.Count >= 32) break;
                if (c is BoxCollider bc)
                {
                    var s = bc.Slot;
                    var half = V(bc.Size.Value) * Vector3.Abs(V(s.GlobalScale)) * 0.5f;
                    var p = V(s.LocalPointToGlobal(bc.Offset.Value));
                    var q = s.GlobalRotation;
                    _proxyList.Add(ProxyShape.Box(p, new Quaternion(q.x, q.y, q.z, q.w), half));
                }
                else if (c is SphereCollider sc)
                {
                    var s = sc.Slot;
                    var gs = Vector3.Abs(V(s.GlobalScale));
                    float r = sc.Radius.Value * MathF.Max(gs.X, MathF.Max(gs.Y, gs.Z));
                    _proxyList.Add(ProxyShape.Sphere(V(s.LocalPointToGlobal(sc.Offset.Value)), r));
                }
                // Mesh, convex hull, capsule, cylinder and cone colliders are not proxied; the ground ray covers floors.
            }
            _rig.Sim.SetProxies(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_proxyList));
        }
    }

    bool IsWorldCollider(ICollider c)
    {
        if (c == null || c.IsRemoved) return false;
        var type = c.ColliderType;
        if (type != ColliderType.Static && type != ColliderType.Active) return false;
        if (c is not Component comp || comp.Slot == null) return false;
        return !comp.Slot.IsChildOf(Slot, true); // never collide with our own colliders
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>Disposes the sim and puts the original mesh back (now, or when the renderer acks our last upload).</summary>
    public void Teardown(bool restore)
    {
        try { _rig.Dispose(); } catch { }
        if (restore && _firstUploadSent)
        {
            try { AssetUploads.QueueRestore(Asset, _original, _work, Provider.Readable.Value, _origHighPriority); }
            catch (Exception ex) { SoftbodyMod.LogWarn($"{Name}: queueing restore failed: {ex.Message}"); }
        }
        else if (_firstUploadSent)
        {
            try { Asset.HighPriorityIntegration = _origHighPriority; } catch { }
        }
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Elapsed(long ts) => (Stopwatch.GetTimestamp() - ts) / (double)Stopwatch.Frequency;
}
