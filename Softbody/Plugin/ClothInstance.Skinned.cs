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

namespace Softbody;

/// <summary>
/// 0.30.0: avatar cloth (DESIGN-cloth.md phase 2), the <see cref="Cloth"/> on a SkinnedMeshRenderer. Only the painted part
/// of the mesh is simulated (<see cref="ClothSkinRegion"/>: the free vertices and the held ring around them); the rest of
/// the mesh stays the renderer's.
///
/// Shown through a private copy of the mesh, as world cloth, but a skinned one: the copy keeps the mesh's bones, weights
/// and blendshapes, except that the vertices the cloth moves are bound fully to one bone (the bone most of them hang from)
/// and have no blendshapes. Each frame the plugin writes their places in that bone's bind space, so the renderer, skinning
/// them by that bone, shows them exactly where the cloth is, while it skins the rest of the body (and the held ring) as
/// before. The uploads are vertex streams only, in place (the Softbody renderer's skinned path, as SkinnedSoftbody), so the
/// skin data goes to the renderer once, with the copy.
///
/// The points follow the animation (their targets: the mesh's vertices with its blendshapes, skinned as the renderer does
/// it), the held ones exactly and the free ones within MaxDistance and in front of the backstop; Inertia sets how much of
/// the body's motion the free ones feel at once. Tears use spare vertices the copy carries, and need the 0.30.0 renderer
/// (which takes their triangles, UVs and colours in place): without it this client's cloth does not tear, and does not
/// replay the tear list.
/// </summary>
sealed partial class ClothInstance
{
    /// <summary>What an avatar cloth adds (null on world cloth).</summary>
    sealed class SkinnedPart
    {
        public SkinnedMeshRenderer Smr;
        public ClothSkinRegion Region;
        public SkinData Skin;
        public Slot[] Bones;
        public int BoneListCount;
        public Matrix4x4[] BindPoses, SkinMats, SkinIn, LocalSkin;
        public float[] BlendW, BlendIn;
        /// <summary>The mesh's own positions (bind space), for the brush.</summary>
        public Vector3[] BasePos;
        public BoundingBox Bounds;
        public bool TearsInPlace;
        public int Spares;
        /// <summary>Stopwatch ticks when the paint's rebuild is due (0: none).</summary>
        public long RebuildAt;
        public float[] RegionBright = Array.Empty<float>();
        public readonly Dictionary<BodyShape, bool[]> Skips = new();
        /// <summary>Per region vertex: the last step's place (bind space of the cloth's bone), for the brush.</summary>
        public Vector3[] Query = Array.Empty<Vector3>();
        public bool QueryValid;
        public volatile bool Refused;
        public long InPlaceAcks, FollowFrames;
        /// <summary>The work copy's triangle submeshes, in order, and the first triangle of each among all of them.</summary>
        public TriangleSubmesh[] TriSubs = Array.Empty<TriangleSubmesh>();
        public int[] TriStart = Array.Empty<int>();
        /// <summary>Render vertices the work copy already has the UVs and colours of.</summary>
        public int Shown;
        public User ExcludeFor;
        public Slot[] Exclude;
        public string BoneName = "?";
    }

    readonly SkinnedPart _sk;
    /// <summary>0.30.0: on a SkinnedMeshRenderer.</summary>
    public bool IsAvatar => _sk != null;

    /// <summary>0.30.0: the rebuild an avatar cloth asks for when paint frees vertices its copy cannot move (exempt from
    /// the rebuild limit: painting is not a fault).</summary>
    public const string PaintRebuild = "the paint freed new parts of the avatar's mesh";
    /// <summary>Seconds after the last paint change before that rebuild (a stroke is many changes).</summary>
    const double PaintRebuildDelay = 0.3;
    /// <summary>The most vertices an avatar mesh may have (as SkinnedSoftbody).</summary>
    public const int MaxSkinnedVertices = 250_000;

    ClothInstance(SkinnedMeshRenderer renderer, Cloth owner, StaticMesh provider, FrooxEngine.Mesh asset, MeshX original, MeshX work, ClothMesh mesh,
                  ClothSolver solver, PaintLayer paint, Slot[] anchors, bool tangents, string key, SkinnedPart sk)
        : this(renderer, owner, provider, asset, original, work, mesh, solver, paint, anchors, tangents, key)
    {
        _sk = sk;
    }

    // ------------------------------------------------------------------ creation

    static SoftbodyInstance.CreateResult TryCreateSkinned(Cloth owner, SkinnedMeshRenderer renderer, ClothSettings settings, Cloth.StartPaint start, Slot[] anchors,
                                                         float anchorRadius, string key, out ClothInstance inst, out string reason)
    {
        inst = null; reason = null;
        const SoftbodyInstance.CreateResult Wait = SoftbodyInstance.CreateResult.Wait, Fail = SoftbodyInstance.CreateResult.Fail;
        if (renderer.Slot == null || renderer.Slot.IsRemoved) { reason = "the renderer's slot was removed"; return Wait; }
        var target = renderer.Mesh.Target;
        if (target == null) { reason = "the SkinnedMeshRenderer has no mesh"; return Wait; }
        if (target is not StaticMesh provider) { reason = $"the mesh is a {target.GetType().Name}, not a StaticMesh"; return Fail; }
        if (PrivateMeshes.Unavailable != null) { reason = "avatar cloth needs its own copy of the mesh, which is not available in this engine version (" + PrivateMeshes.Unavailable + ")"; return Fail; }
        var asset = provider.Asset;
        if (asset == null || asset.LoadState != AssetLoadState.FullyLoaded || asset.Data == null) { reason = "mesh asset still loading"; return Wait; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>() || Unsafe.SizeOf<color>() != 16 || Unsafe.SizeOf<float4>() != Unsafe.SizeOf<Vector4>()) { reason = "unexpected Elements.Core vector layout"; return Fail; }
        var original = asset.Data;
        int n = original.VertexCount;
        if (n <= 0) { reason = "the mesh has no vertices"; return Fail; }
        if (n > MaxSkinnedVertices) { reason = $"the mesh has {n} vertices (cap {MaxSkinnedVertices})"; return Fail; }
        if (original.BoneCount == 0 || !original.HasBoneBindings) { reason = "this mesh has no bones: put the Cloth on a MeshRenderer instead (world cloth)"; return Fail; }
        int rendererShapes = 0;
        try { rendererShapes = renderer.BlendShapeWeights.Count; } catch { }
        if (original.BlendShapeCount < rendererShapes) { reason = $"the engine's copy has {original.BlendShapeCount} blendshapes but the renderer uses {rendererShapes}; refusing to drive it"; return Fail; }
        if (!original.HasNormals) { reason = "this skinned mesh has no normals"; return Fail; }
        if (!SoftbodyRuntime.UseGpuSkinning(out var noGpu))
        {
            reason = "avatar cloth needs the Softbody renderer (the -RendererPath fork, or the SoftbodyRenderer patch) to update the skinned mesh in place (" + noGpu + ")";
            return Fail;
        }
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
            var bright = new float[n];
            paint.ComputeMaps(SoftnessSource.Value, false, bright, Span<float>.Empty);

            // the skeleton, as the renderer poses it now
            SoftbodyInstance.SkinSetup skin;
            try { skin = SoftbodyInstance.ReadSkinSetup(renderer, original, n); }
            catch (Exception ex) { reason = "reading the mesh's bones failed: " + ex.Message; return Fail; }
            if (!SoftbodyInstance.ComputeSkin(renderer.Slot, skin.Bones, skin.BindPoses, skin.Matrices, null)) { reason = "the skeleton's pose is not finite"; return Wait; }
            var blendW = new float[original.BlendShapeCount];
            ReadBlendWeights(renderer, blendW);

            ClothSkinRegion region;
            try { region = ClothSkinRegion.Build(pos, trisArr, bright, skin.Skin); }
            catch (Exception ex) { reason = "reading the mesh failed: " + ex.Message; return Fail; }
            var sk = new SkinnedPart
            {
                Smr = renderer, Region = region, Skin = skin.Skin, Bones = skin.Bones, BoneListCount = skin.BoneListCount, BindPoses = skin.BindPoses,
                SkinMats = skin.Matrices, SkinIn = new Matrix4x4[skin.Matrices.Length], LocalSkin = new Matrix4x4[skin.Matrices.Length],
                BlendW = blendW, BlendIn = new float[blendW.Length], BasePos = pos, Bounds = BindBounds(pos),
            };
            var boneSlot = region.Bone < skin.Bones.Length ? skin.Bones[region.Bone] : null;
            sk.BoneName = boneSlot?.Name ?? $"#{region.Bone}";

            // nothing free yet: only the brush (the cloth starts with the paint)
            if (region.Empty)
            {
                inst = new ClothInstance(renderer, owner, provider, asset, original, null, null, null, paint, anchors, false, key, sk);
                inst.ReadBlastsAtBuild();
                created = true;
                return SoftbodyInstance.CreateResult.Created;
            }

            // the cloth: the region as the avatar shows it now (its blendshapes applied)
            int nr = region.Count;
            var regionBase = new Vector3[nr];
            for (int r = 0; r < nr; r++) regionBase[r] = pos[region.Vertices[r]];
            var blend = RegionBlendshapes(original, region.Vertices);
            var shaped = region.Shaped(regionBase, blend, blendW);
            ClothMesh mesh;
            try { mesh = new ClothMesh(shaped, region.Triangles); }
            catch (Exception ex) { reason = "reading the painted part failed: " + ex.Message; return Fail; }
            if (mesh.ParticleCount > MaxParticles) { reason = $"the painted part has {mesh.ParticleCount} points after welding (cap {MaxParticles}): paint less of it free, or use a lower-poly mesh"; return Fail; }
            if (mesh.TriangleCount == 0) { reason = "the painted part has no triangles over three different points"; return Fail; }
            int others = SoftbodyRuntime.TotalClothPoints(owner);
            if (others + mesh.ParticleCount > SoftbodyRuntime.MaxClothPoints)
            {
                reason = $"point cap: other cloths already use {others} of {SoftbodyRuntime.MaxClothPoints} cloth points on this client";
                return Wait;
            }
            region.Bind(mesh, regionBase, blend);

            // the solver, with the region's own normals and (when the mesh has them) its tangent frames
            var restNrm = new Vector3[nr];
            var nrmAll = MemoryMarshal.Cast<float3, Vector3>(original.RawNormals.AsSpan(0, n));
            for (int r = 0; r < nr; r++) restNrm[r] = nrmAll[region.Vertices[r]];
            bool tangents = original.HasTangents && original.HasUV0s && original.GetUV_Dimension(0) == 2;
            Vector2[] uv0 = null; Vector4[] restTan = null;
            if (tangents)
            {
                var uvAll = MemoryMarshal.Cast<float2, Vector2>(original.RawUV0s.AsSpan(0, n));
                var tanAll = MemoryMarshal.Cast<float4, Vector4>(original.RawTangents.AsSpan(0, n));
                uv0 = new Vector2[nr]; restTan = new Vector4[nr];
                for (int r = 0; r < nr; r++) { uv0[r] = uvAll[region.Vertices[r]]; restTan[r] = tanAll[region.Vertices[r]]; }
            }
            var solver = new ClothSolver(mesh, settings, restNrm, uv0, restTan);
            sk.RegionBright = new float[nr];
            region.RegionBrightness(bright, sk.RegionBright);
            solver.SetPaint(sk.RegionBright);
            // which side is out: the normals' side facing away from the bone's head, in the bind pose
            var head = Matrix4x4.Invert(skin.BindPoses[region.Bone], out var bindInv) ? bindInv.Translation : Vector3.Zero;
            solver.BackstopSign = ClothSkinRegion.OutwardSign(mesh, head);

            // where it is now
            var bone = skin.Matrices[region.Bone];
            if (!Finite(bone) || !Matrix4x4.Invert(bone, out var w2r)) { reason = "the cloth's bone is not finite"; return Wait; }
            var targets = new Vector3[mesh.OriginalParticleCount];
            region.Targets(skin.Matrices, blendW, targets);

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
                for (int i = 0; i < mesh.ParticleCount; i++)
                {
                    var wp = targets[i];
                    float d2 = Vector3.DistanceSquared(wp, at);
                    if (d2 > r2 || d2 >= best[i]) continue;
                    best[i] = d2; anchorOf[i] = a; anchorLocal[i] = Vector3.Transform(wp, ainv);
                }
            }
            solver.SetAnchors(anchorOf, anchorLocal);

            // the renderer's own copy: the moving vertices on the bone, with spare ones for tears when the renderer can show them
            sk.TearsInPlace = SoftbodyRuntime.RendererTakesTrianglesInPlace();
            sk.Spares = sk.TearsInPlace ? Math.Clamp(nr / 2, 64, 20000) : 0;
            MeshX work;
            try { work = ClothSkinCopy.Make(original, region, sk.Spares); }
            catch (Exception ex) { reason = "making the avatar's copy of the mesh failed: " + ex.Message; return Fail; }
            region.SpareCount = sk.Spares;
            mesh.MaxRenderVertices = nr + sk.Spares;
            ClothSkinCopy.TriangleSubmeshes(work, out sk.TriSubs, out sk.TriStart);

            inst = new ClothInstance(renderer, owner, provider, asset, original, work, mesh, solver, paint, anchors, tangents && work.HasTangents, key, sk);
            // 0.31.0: blasts that came before (on an avatar, only pushed when they come)
            inst.ReadBlastsAtBuild();
            sk.Shown = mesh.RenderVertexCount;
            // tears made before this client built it (only when this renderer can show them: every client that does has the
            // same spares, so the list replays the same everywhere)
            if (sk.TearsInPlace) inst.ReplayTears();
            else inst._appliedTears = owner.Tears.Count;
            if (inst._mesh.Version != inst._workVersion) inst.ApplyTopologySkinned();
            // the cloth where the animation puts it, in place
            var f = inst._frame;
            f.Targets = targets;
            f.BoneToWorld = bone;
            f.RendererToWorld = SkinMatrices.ToNumerics(renderer.Slot.LocalToGlobal);
            solver.ResetToTargets(f);
            inst.Output(w2r, 1f);
            inst.WriteSkinnedStreams();
            inst.StartPrivateSkinned();
            created = true;
            return SoftbodyInstance.CreateResult.Created;
        }
        finally
        {
            if (!created) { SoftbodyRuntime.Release(provider, owner); inst = null; }
        }
    }

    /// <summary>Bind-space bounds of the mesh with room for the cloth (the renderer skins it; these only guard the vertex
    /// buffer's own bounds).</summary>
    static BoundingBox BindBounds(Vector3[] pos)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in pos) if (float.IsFinite(p.X + p.Y + p.Z)) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        if (lo.X > hi.X) lo = hi = Vector3.Zero;
        var pad = (hi - lo) * 0.25f + new Vector3(0.05f);
        lo -= pad; hi += pad;
        return BoundingBox.FromPoints(new float3(lo.X, lo.Y, lo.Z), new float3(hi.X, hi.Y, hi.Z));
    }

    /// <summary>Frame-0 position deltas of the mesh's blendshapes for the region's vertices (indexed by region vertex).</summary>
    static BlendshapeDeltas RegionBlendshapes(MeshX m, int[] verts)
    {
        var shapes = m.BlendShapes.ToList();
        return BlendshapeDeltas.FromPositions(shapes.Count, b =>
        {
            var bs = shapes[b];
            if (bs.FrameCount == 0) return (null, null);
            var p = bs[0].RawPositions;
            if (p == null) return (null, null);
            var vi = new List<int>(); var dp = new List<Vector3>();
            for (int r = 0; r < verts.Length; r++)
            {
                int v = verts[r];
                if (v >= p.Length) continue;
                var d = new Vector3(p[v].x, p[v].y, p[v].z);
                if (d == Vector3.Zero) continue;
                vi.Add(r); dp.Add(d);
            }
            return (vi.ToArray(), dp.ToArray());
        });
    }

    static void ReadBlendWeights(SkinnedMeshRenderer smr, float[] dst)
    {
        if (dst.Length == 0) return;
        var list = smr.BlendShapeWeights;
        int count = Math.Min(list.Count, dst.Length);
        for (int i = 0; i < dst.Length; i++)
        {
            float w = i < count ? list[i] : 0f;
            dst[i] = float.IsFinite(w) ? w : 0f;
        }
    }

    void StartPrivateSkinned()
    {
        _ackPending = true;
        _privateStartedTs = Stopwatch.GetTimestamp();
        _private = PrivateMeshes.Create(Renderer.World, _work, _sk.Bounds, Provider, _ => _privateReady = true, dynamic: true);
    }

    // ------------------------------------------------------------------ the frame (world thread, Gate held)

    FrameResult FrameSkinned(World world, float dt, float gain, SoftbodyDebugMesh debug, bool debugColliders)
    {
        var sk = _sk;
        if (Anchor.IsRemoved || Renderer.IsRemoved || Provider.IsRemoved) { WaitStep(); return FrameResult.Gone; }
        if (!ReferenceEquals(Renderer.Mesh.Target, Provider)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (Asset.LoadState != AssetLoadState.FullyLoaded || !ReferenceEquals(Asset.Data, _original)) { RebuildReason = "the mesh asset was reloaded"; return FrameResult.Rebuild; }
        if (BonesChangedSkinned()) { RebuildReason = "the renderer's bones changed"; return FrameResult.Rebuild; }
        if (sk.RebuildAt != 0 && Stopwatch.GetTimestamp() >= sk.RebuildAt) { WaitStep(); RebuildReason = PaintRebuild; return FrameResult.Rebuild; }
        if (_mesh == null) { _paused = false; return FrameResult.Ok; }     // waiting for paint
        if (!_swapped)
        {
            if (_privateReady)
            {
                bool ok;
                try { ok = SwapIn(); }
                catch (Exception ex) { FailReason = "stopped: the cloth's own copy of the avatar's mesh could not stand in for the shared one (" + ex.Message + "); the mesh was left untouched"; return FrameResult.Failed; }
                if (!ok) { RebuildReason = "the renderer's mesh changed before the cloth's own copy was ready"; return FrameResult.Rebuild; }
            }
            else
            {
                if (Elapsed(_privateStartedTs) > 30) { FailReason = "stopped: the renderer did not take the cloth's own copy of the avatar's mesh within 30 s; the mesh was left untouched"; return FrameResult.Failed; }
                return FrameResult.Ok;
            }
        }
        if (!ReferenceEquals(PrivateMeshes.Held(Provider), _private)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (sk.Refused) { FailReason ??= "stopped: the renderer did not update the avatar's mesh in place (is the Softbody renderer running?); the shared mesh was left untouched"; return FrameResult.Failed; }
        if (!Anchor.IsActive || !Renderer.Enabled) { WaitStep(); _paused = true; _resumeNext = true; return FrameResult.Paused; }
        _paused = false;

        long t0 = Stopwatch.GetTimestamp();
        WaitStep();
        LastWaitMs = Ms(Stopwatch.GetTimestamp() - t0);

        // 0.31.0: new blasts (on an avatar, only their push)
        BlastsFrame(world, out _);

        // tears: only where this renderer shows them (see TryCreateSkinned); the list is still followed so a Mend is seen
        if (sk.TearsInPlace) { if (!TearsFrame(world, out var shorter)) { RebuildReason = shorter; return FrameResult.Rebuild; } }
        else _appliedTears = Owner.Tears.Count;

        // show the last step
        UploadSkinned();

        _gainIn = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 1f;

        // this frame's pose
        if (!SoftbodyInstance.ComputeSkin(Anchor, sk.Bones, sk.BindPoses, sk.SkinMats, null)) { _resumeNext = true; Count(t0, 0); return FrameResult.Ok; }
        ReadBlendWeights(sk.Smr, sk.BlendW);
        var bone = sk.SkinMats[sk.Region.Bone];
        if (!Finite(bone) || !Matrix4x4.Invert(bone, out var w2r)) { _resumeNext = true; Count(t0, 0); return FrameResult.Ok; }
        bool follow = FollowOnly(world, bone);

        var f = _frame;
        f.Dt = dt;
        f.BoneToWorld = bone;
        f.RendererToWorld = SkinMatrices.ToNumerics(Anchor.LocalToGlobal);
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
        f.FindTears = !follow && sk.TearsInPlace && _solver.Settings.Tearable && DecidesTearsSkinned(world) && !_mesh.Full;
        if (follow)
        {
            f.Colliders.Clear(); f.BodyCount = 0; f.WindCount = 0;
            f.KickCount = 0; _pendingKicks.Clear();
            sk.FollowFrames++;
        }
        else
        {
            GatherColliders(world, dt);
            GatherWinds(world);
            TakeKicks(f, f.RendererToWorld);
        }
        if (debug != null) debug.SubmitCloth(_solver, debugColliders);

        Array.Copy(sk.SkinMats, sk.SkinIn, sk.SkinMats.Length);
        Array.Copy(sk.BlendW, sk.BlendIn, sk.BlendW.Length);
        var solver = _solver;
        var region = sk.Region;
        var skinIn = sk.SkinIn; var blendIn = sk.BlendIn;
        float gainIn = _gainIn;
        _worldToRendererIn = w2r;
        _step = Task.Run(() =>
        {
            region.Targets(skinIn, blendIn, f.Targets);
            if (follow) solver.ResetToTargets(f, count: false);
            else solver.Step(f);
            int nv = solver.Mesh.RenderVertexCount;
            if (_outPos.Length < nv)
            {
                int cap = Math.Max(nv, _outPos.Length + _outPos.Length / 4);
                _outPos = new Vector3[cap]; _outNrm = new Vector3[cap];
                if (_tangents) _outTan = new Vector4[cap];
            }
            solver.FillOutput(w2r, follow ? 1f : gainIn, _outPos.AsSpan(0, nv), _outNrm.AsSpan(0, nv), _tangents ? _outTan.AsSpan(0, nv) : Span<Vector4>.Empty, out _outMin, out _outMax);
            _outCount = nv;
            _outVersion = solver.Mesh.Version;
        });
        Count(t0, follow ? 0 : _solver.LastStepMs);
        return FrameResult.Ok;
    }

    /// <summary>
    /// Simulating 0, or further than CullDistance from this client's head: the cloth only follows the animation (each point
    /// at its target, nothing stepped). Its vertices are on one bone in the renderer's copy, so they are still written every
    /// frame. It starts again from the animated pose.
    /// </summary>
    bool FollowOnly(World world, in Matrix4x4 bone)
    {
        _off = !(_gainIn > 0f);
        bool far = false;
        float cull = _solver.Settings.CullDistance;
        if (!_off && cull > 0f)
        {
            float3? head = null;
            try { head = world.LocalUser?.Root?.HeadPosition; } catch { }
            if (head.HasValue)
            {
                var lo = _solver.WorldMin; var hi = _solver.WorldMax;
                var c = lo.X <= hi.X ? 0.5f * (lo + hi) : bone.Translation;
                float radius = lo.X <= hi.X ? 0.5f * (hi - lo).Length() : 0f;
                var h = new Vector3(head.Value.x, head.Value.y, head.Value.z);
                float d = Vector3.Distance(c, h) - radius;
                far = _culled ? d > 0.9f * cull : d > cull;
            }
        }
        bool follow = _off || far;
        if (follow) { _culled = far; _resumeNext = true; }
        else if (_culled) _culled = false;
        return follow;
    }

    bool BonesChangedSkinned()
    {
        var sk = _sk;
        var list = sk.Smr.Bones;
        if (list.Count != sk.BoneListCount) return true;
        for (int b = 0; b < sk.Bones.Length; b++)
        {
            var s = b < list.Count ? list[b] : null;
            if (s != null && s.IsRemoved) s = null;
            if (!ReferenceEquals(s, sk.Bones[b])) return true;
        }
        return false;
    }

    /// <summary>Avatar cloth: the wearer decides the tears (they see their own cloth best); with no wearer (an avatar standing
    /// in the world), the world cloth's choice (<see cref="DecidesTears"/>).</summary>
    bool DecidesTearsSkinned(World world)
    {
        User wearer = null;
        try { wearer = Anchor.ActiveUser; } catch { }
        if (wearer != null && !wearer.IsRemoved) return ReferenceEquals(wearer, world.LocalUser);
        return DecidesTears(world);
    }

    // ------------------------------------------------------------------ topology and the upload (world thread)

    /// <summary>
    /// The work copy takes the cloth's current topology: each new render vertex (a tear's copy) is a spare vertex, which takes
    /// the UVs and colours of the vertex it copies, and every region triangle gets its corners (the mesh's other triangles
    /// never change).
    /// </summary>
    void ApplyTopologySkinned()
    {
        var sk = _sk;
        sk.Shown = ClothSkinCopy.ApplyTopology(_work, sk.Region, _mesh, sk.TriSubs, sk.TriStart, sk.Shown);
        _workVersion = _mesh.Version;
    }

    /// <summary>The last output into the work copy: the moving region vertices and the spare ones in use (the held ring keeps
    /// the renderer's own skinning).</summary>
    void WriteSkinnedStreams() =>
        ClothSkinCopy.WriteOutput(_work, _sk.Region, _outPos, _outNrm, _tangents ? _outTan : ReadOnlySpan<Vector4>.Empty, _outCount);

    void UploadSkinned()
    {
        var sk = _sk;
        if (_ackPending || !_swapped || _outVersion < 0) { if (_ackPending) BusyFrames++; return; }
        bool topology = _outVersion != _workVersion;
        if (topology && _outVersion != _mesh.Version) return;     // made before the latest tears: wait for the next step
        if (topology) ApplyTopologySkinned();
        WriteSkinnedStreams();
        var hint = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
        if (_tangents && _work.HasTangents) hint[MeshUploadHint.Flag.Tangents] = true;
        if (topology) PartialEncode.AddTopology(ref hint, _work);
        var why = GpuUpload.Check(_private, _work, hint);
        if (why != null) { FailReason = "stopped: an in-place upload is not possible on the avatar's mesh (" + why + "); nothing was sent"; sk.Refused = true; return; }
        _ackPending = true;
        _uploadSentAt = Stopwatch.GetTimestamp();
        string res;
        try { res = AssetUploads.TryUploadPartial(_private, _work, hint, sk.Bounds, OnAckSkinned); }
        catch { _ackPending = false; throw; }
        if (res != null)
        {
            _ackPending = false;
            if (res == AssetUploads.Busy) { BusyFrames++; return; }
            FailReason = "stopped: the in-place upload could not be sent (" + res + ")";
            sk.Refused = true;
            return;
        }
        Uploads++;
        if (topology) FullUploads++;
    }

    void OnAckSkinned(bool instanceChanged)
    {
        // an in-place upload must come back in place; a new mesh instance means the renderer recreated it without its bones
        if (instanceChanged)
        {
            Interlocked.Increment(ref InstanceChanges);
            _sk.Refused = true;
            try { _assetManager?.QueueAssetCreated(Provider); } catch { }
        }
        else if (Interlocked.Increment(ref _sk.InPlaceAcks) == 1) SoftbodyRuntime.NoteGpuConfirmed();
        _ackPending = false;
    }

    // ------------------------------------------------------------------ colliders (world thread)

    /// <summary>
    /// The wearer's own body: their spheres hold a point out no further than its target (<see cref="ClothBody.Own"/>), and
    /// those fitted against the cloth itself on this same mesh are left out (they would hold it where it was modelled).
    /// </summary>
    void MarkOwnBodies()
    {
        User wearer = null;
        try { wearer = Anchor.ActiveUser; } catch { }
        for (int k = 0; k < _frame.BodyCount; k++)
        {
            var body = _frame.Bodies[k];
            if (wearer != null && ReferenceEquals(body.User, wearer)) body.Own = true;
            if (ReferenceEquals(body.Renderer, Renderer) && body.Shape != null) body.SkipSphere = SkipsFor(body.Shape);
        }
    }

    bool[] SkipsFor(BodyShape shape)
    {
        var sk = _sk;
        if (sk.Skips.TryGetValue(shape, out var skip)) return skip;
        if (sk.Skips.Count > 4) sk.Skips.Clear();
        // the spheres were fitted to the shared mesh, so its bind space: the same as the region's (BodyShapes reads the
        // shared asset's data, never the cloth's copy)
        float slack = shape.UnitScale > 1e-6f ? 0.01f / shape.UnitScale : 0.01f;
        skip = sk.Region.SpheresAgainstCloth(sk.BasePos, shape.Center, shape.Radius, slack);
        sk.Skips[shape] = skip;
        return skip;
    }

    /// <summary>The colliders the cloth ignores: its own slots (and anchors), and on an avatar the wearer's whole user root
    /// (their bone colliders and grab colliders would hold the cloth out; their body is the body shape).</summary>
    Slot[] ExcludeSlotsSkinned()
    {
        var sk = _sk;
        User wearer = null;
        try { wearer = Anchor.ActiveUser; } catch { }
        if (sk.Exclude != null && ReferenceEquals(sk.ExcludeFor, wearer)) return sk.Exclude;
        var list = new List<Slot>(ExcludeSlots());
        try { var root = wearer?.Root?.Slot; if (root != null && !root.IsRemoved) list.Add(root); } catch { }
        sk.ExcludeFor = wearer;
        return sk.Exclude = list.ToArray();
    }

    // ------------------------------------------------------------------ paint and brush

    PaintLayer.UpdateResult UpdatePaintSkinned(ReadOnlySpan<Dab> dabs)
    {
        var r = _paint.Update(dabs);
        if (r.Kind == PaintLayer.UpdateKind.None) return r;
        var bright = new float[_nv0];
        _paint.ComputeMaps(SoftnessSource.Value, false, bright, Span<float>.Empty);
        _paint.ClearDirty();
        var sk = _sk;
        // vertices freed outside the copy's moving set need a new copy: rebuild a moment after the last change
        if (sk.Region.FreesNew(bright)) sk.RebuildAt = Stopwatch.GetTimestamp() + (long)(PaintRebuildDelay * Stopwatch.Frequency);
        else sk.RebuildAt = 0;
        if (_solver != null)
        {
            WaitStep();
            sk.Region.RegionBrightness(bright, sk.RegionBright);
            _solver.SetPaint(sk.RegionBright);
        }
        return r;
    }

    void PrepareBrushQuerySkinned()
    {
        var sk = _sk;
        if (!SoftbodyInstance.ComputeSkin(Anchor, sk.Bones, sk.BindPoses, sk.LocalSkin, SkinMatrices.ToNumerics(Anchor.GlobalToLocal))) return;
        _queryCopy ??= new Vector3[_nv0];
        sk.Skin.SkinPositions(sk.LocalSkin, sk.BasePos, _queryCopy);
        // the cloth's moving vertices where the renderer shows them (the last step's places, on the cloth's bone)
        if (sk.QueryValid && _mesh != null)
        {
            var bm = sk.LocalSkin[sk.Region.Bone];
            var verts = sk.Region.Vertices; var moves = sk.Region.Moves;
            int count = Math.Min(verts.Length, sk.Query.Length);
            for (int r = 0; r < count; r++)
                if (moves[r]) _queryCopy[verts[r]] = Vector3.Transform(sk.Query[r], bm);
        }
        _paint.SetQueryPositions(_queryCopy);
    }

    float BindScaleSkinned(in MeshHit hit)
    {
        var sk = _sk;
        if (!_paint.HasQueryPose) return 1f;
        var (a, b, c) = _paint.TriangleVertices(hit.Triangle);
        float s = (Lbs.LinearScale(sk.Skin.VertexMatrix(sk.LocalSkin, a)) + Lbs.LinearScale(sk.Skin.VertexMatrix(sk.LocalSkin, b)) +
                   Lbs.LinearScale(sk.Skin.VertexMatrix(sk.LocalSkin, c))) / 3f;
        return float.IsFinite(s) && s > 1e-6f ? s : 1f;
    }

    /// <summary>After each step: the region's places, for the brush.</summary>
    void KeepQuerySkinned()
    {
        var sk = _sk;
        int nr = sk.Region.Count;
        if (sk.Query.Length < nr) sk.Query = new Vector3[nr];
        Array.Copy(_outPos, sk.Query, Math.Min(nr, _outCount));
        sk.QueryValid = true;
    }

    // ------------------------------------------------------------------ status

    string RunningStateSkinned()
    {
        var sk = _sk;
        if (_mesh == null) return "waiting for paint: nothing on this mesh hangs free yet. Paint the parts that should move white with the Softbody brush";
        if (!_swapped) return "starting: making the cloth's own copy of the avatar's mesh";
        if (_paused) return "paused: renderer disabled or its slot inactive";
        if (_off) return "off: Simulating is 0 (the cloth follows the body as modelled)";
        if (_culled) return $"following the body only: further than CullDistance ({_solver.Settings.CullDistance:0.#} m) from you";
        string tears = _mesh.Splits > 0 ? $", {_mesh.Splits} tears" : "";
        string room = sk.TearsInPlace ? (_mesh.Full ? " (no room for more tears: Mend)" : "")
                    : _solver.Settings.Tearable ? " (no tears: they need the 0.30.0 Softbody renderer)" : "";
        return $"running: {_mesh.ParticleCount} points ({_solver.Free} free, {_solver.Pinned} held, {_solver.Anchored} anchored), {_mesh.TriangleCount} triangles on bone '{sk.BoneName}'{tears}{room}{BlastsState()}{PlayersState()}";
    }

    string DescribeSkinned()
    {
        var sk = _sk;
        var region = sk.Region;
        if (_mesh == null) return $"avatar cloth on {_nv0} vertices, nothing painted free yet (waiting for paint)";
        return $"avatar cloth: {region.Count} of {_nv0} vertices ({region.MovingCount} moving, on bone '{sk.BoneName}'), {_mesh.ParticleCount} points, {_mesh.TriangleCount} triangles, " +
               $"{_solver.StretchConstraints} edges, {_solver.BendConstraints} bending pairs, {_anchorSlots.Length} anchors, backstop side {(_solver.BackstopSign > 0 ? "+" : "-")}, " +
               $"tangents {(_tangents ? "yes" : "no")}, " +
               (sk.TearsInPlace ? $"{sk.Spares} spare vertices for tears, {TearsApplied} tears replayed" + (TearsSkipped > 0 ? $" ({TearsSkipped} skipped)" : "")
                                : "no tears (the renderer is older than 0.30.0)");
    }

    string StatsLineSkinned(double seconds)
    {
        var ic = CultureInfo.InvariantCulture;
        int frames = Math.Max(1, _statsFrames);
        if (_mesh == null) { _statsMain = 0; _statsStep = 0; _statsFrames = 0; return $"'{Name}' avatar cloth: waiting for paint"; }
        string line = string.Format(ic, "'{0}' avatar cloth: {1} frames in {2:0.0} s, main {3:0.000} ms/frame, step {4:0.000} ms ({5} steps), {6} contacts, {22} pairs ({23} runs), {7} shapes, max strain {8:0.000}, " +
                                        "uploads {9} ({10} with tears, {11} in place), busy {12}, tears {13} made / {14} applied / {15} skipped, points {16}, resets {17}, snap turns {18}, " +
                                        "follow-only frames {19}, players shaped {20} ({21} spheres near)",
            Name, _statsFrames, seconds, _statsMain / frames, _statsStep / frames, _solver.StepsLastFrame, _solver.Contacts, _solver.ShapeCount, _solver.MaxStrain,
            Uploads, FullUploads, Interlocked.Read(ref _sk.InPlaceAcks), BusyFrames, TearsMade, TearsApplied, TearsSkipped, _mesh.ParticleCount, _solver.Resets, _solver.SnapTurns,
            _sk.FollowFrames, _bodyStats.Shaped, _solver.BodySpheres, _solver.PairCount, _solver.PairRuns);
        _statsMain = 0; _statsStep = 0; _statsFrames = 0;
        return line;
    }
}
