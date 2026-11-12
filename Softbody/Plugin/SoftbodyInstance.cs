using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using Renderite.Shared;
using Softbody.Core;

namespace Softbody;

/// <summary>
/// The local simulation behind one running Softbody component. Local-only by construction: it creates
/// nothing in the world and writes no synced field. It takes the MeshRenderer's StaticMesh asset (a per-client
/// runtime object, never synced or saved), swaps its MeshX for a private copy, and re-uploads only positions
/// and normals of that copy each frame (plus colours when the paint changed). On teardown the original MeshX
/// is uploaded back.
///
/// Paint (0.4.0): the component's synced dabs are applied to the mesh's own colours by a <see cref="PaintLayer"/>.
/// The effective colours are the softness map and are written into the private copy, so a vertex-colour
/// material shows the paint. A paint change only re-uploads colours at once; the cage is rebuilt in place
/// (same mesh copy, no restore) once the paint has been still for <see cref="RebuildDelay"/>.
/// All calls come from the world thread with <see cref="SoftbodyRuntime.Gate"/> held (except the upload ack).
///
/// Skinned meshes (0.8.0, experimental): on a SkinnedMeshRenderer whose mesh has bones, the cage follows the skeleton.
/// Each frame the bones' skin matrices (BoneSlot.LocalToGlobal * BindPose, the engine's own convention) give every cage
/// point a skinned target; the simulated offsets are mapped back to bind space and added to the soft vertices there.
/// Two ways to show that:
/// <list type="bullet">
/// <item>GPU path (0.9.0, needs the SoftbodyRenderer patch in the renderer): the private copy keeps all of its skin data;
/// each frame the soft vertices' bind-space positions and normals are written into it and sent as a true partial upload
/// (<see cref="GpuUpload"/>: vertex streams only, no layout widening). The patched renderer updates the existing skinned
/// Unity mesh in place, so Unity adds the blendshapes and skins it on the GPU. No CPU skinning, and no upload at all while
/// the jelly is still. The asset keeps its original MeshX (only the renderer's vertex buffer changes), and the restore puts
/// the original's streams back the same way. If a partial upload comes back with a new mesh instance (the renderer did not
/// apply it in place), this instance switches to the CPU path.</item>
/// <item>CPU path (0.8.0): Renderite.Unity.MeshAsset.Upload destroys and recreates the Unity mesh on every upload after a
/// skinned one, so this client CPU-skins the whole mesh (bind-space base + blendshapes from the renderer's weights, skinned
/// into the renderer slot's space, like the GPU) and uploads it as an unskinned mesh: the private copy has no bones and no
/// blendshape frames (but keeps the blendshapes' names, so the synced BlendShapeWeights list is never resized), so partial
/// uploads keep working as for static meshes. When the renderer reports a new mesh instance (the first upload and the
/// restore), the asset's references are re-notified so every renderer re-binds.</item>
/// </list>
/// </summary>
sealed class SoftbodyInstance : IBrushTarget
{
    Slot IBrushTarget.Anchor => Anchor;
    public readonly MeshRenderer Renderer;
    /// <summary>The renderer as a SkinnedMeshRenderer when the cage follows its bones; null for plain meshes (and for a
    /// skinned renderer whose mesh has no bones, which renders on its slot like a static mesh).</summary>
    public readonly SkinnedMeshRenderer Skinned;
    /// <summary>The renderer's slot: the mesh's vertices live in its frame, so the cage is anchored to it.</summary>
    public readonly Slot Anchor;
    /// <summary>The Softbody component's slot (its colliders are ignored, like the renderer slot's).</summary>
    public readonly Slot Owner;
    public readonly StaticMesh Provider;
    public readonly FrooxEngine.Mesh Asset;
    public readonly string BuildKey;
    public string Name => Anchor.Name;
    public int PointCount => _rig.Cage.PointCount;
    public SoftbodySettings Settings => _rig.Settings;
    public string RebuildReason { get; private set; }
    /// <summary>The paint layer (effective colours, rest mesh queries for the brush).</summary>
    public PaintLayer Paint => _paint;
    /// <summary>Colour space of the mesh's vertex colours; dab colours are converted to it.</summary>
    public ColorProfile Profile { get; }

    /// <summary>Seconds without paint changes before the cage is rebuilt from the new softness.</summary>
    public const double RebuildDelay = 0.3;

    readonly MeshX _original;
    readonly MeshX _work;
    SoftbodyRig _rig;
    readonly int _n;
    readonly bool _origHighPriority;
    readonly Vector3 _restCenterLocal;
    string _mapDescription;
    readonly PaintLayer _paint;
    readonly int[] _tris;
    readonly Vector3[] _restNormalsIn;      // the mesh's own normals (null when it had none)
    readonly SoftnessSource _source;
    readonly bool _invert;
    float[] _soft, _bands;                   // the maps the current cage was built from (null = no map: all soft)
    long _cagePaintVersion, _lastPaintChangeTs;
    bool _colorsInWork, _needFullUpload, _forceUpload;
    Vector3 _lastBmin, _lastBmax;
    bool _firstUploadSent, _firstUploadAcked, _paused;
    volatile bool _ackPending;
    long _uploadSentAt;
    float _groundTimer, _proxyTimer;
    public float LastDisplacement;
    readonly List<ICollider> _overlap = new();
    readonly List<ProxyShape> _proxyList = new();
    // debug views (0.10.0): the last ground ray origin and collider search box, world space
    bool _dbgSearch;
    Vector3 _dbgRayOrigin, _dbgSearchCenter, _dbgSearchSize;
    readonly List<ICollider> _tracked = new();                       // found by the last overlap query
    readonly Dictionary<ICollider, int> _proxyKeys = new(ReferenceEqualityComparer.Instance);
    int _nextProxyKey = 1;

    // skinned (null / unused for plain meshes)
    readonly SkinData _skin;
    readonly Slot[] _boneSlots;          // per mesh bone: the renderer's Bones entry (null: missing, follows the renderer slot)
    readonly int _boneListCount;         // the renderer's Bones count when built (a change rebuilds)
    readonly Matrix4x4[] _bindPoses;     // per mesh bone, Core convention
    readonly Matrix4x4[] _skinMats;      // per frame: bones + the slot bone, bind space to world
    readonly Slot _skeletonRoot;         // colliders under it (the avatar's own) are ignored
    public bool IsSkinned => _skin != null;
    public int BoneCount => _skin?.BoneCount ?? 0;
    /// <summary>How a skinned instance shows the wobble (Static for plain meshes and skinned renderers without bones).</summary>
    public enum SkinPath { Static, Gpu, Cpu }
    public SkinPath Path { get; private set; }
    readonly string _cpuReason;                      // why a skinned instance started on the CPU path (null: GPU)
    string _demotedReason;                           // set when a GPU instance fell back to the CPU path
    volatile bool _demote;                           // an ack said the renderer recreated the mesh on a GPU-path partial upload
    MeshX _assetData;                                // the MeshX the asset must hold while we drive it (_original until SetFromMeshX(_work))
    public long GpuPartials, GpuInPlaceAcks, GpuRecreatedAcks;
    bool _gpuColorsUploaded;                         // the GPU path re-encoded colours (the restore must put the original's back)
    // CPU skinning: bind-space base (rest + wobble), the pose in the renderer slot's space, blendshapes, output buffers
    Vector3[] _basePos, _baseNrm;
    Vector4[] _restTan;
    BlendshapeDeltas _blend;
    float[] _blendW, _lastBlendW;
    readonly Matrix4x4[] _localSkin, _lastSkin;
    Vector3[] _outPos, _outNrm;
    Vector4[] _outTan;
    Vector3[] _mixPos, _mixNrm; Vector4[] _mixTan;   // base + blendshape deltas (only while a blendshape is active)
    Vector3[] _queryPos;                             // GPU path: the shown pose for brush queries (CPU-skinned on demand, 10 Hz max)
    readonly WorldAssetManager _assetManager;        // to re-bind renderers when the renderer recreated the mesh instance
    public int ActiveBlendshapes { get; private set; }
    public long InstanceChanges;
    public double LastSkinMeshMs;
    // brush queries on the shown pose (renderer-local space), refreshed at most 10 Hz while the brush aims near it
    long _queryAt;
    Vector3 _queryMin, _queryMax;
    public double LastBrushPoseMs;
    public long BrushPoseRefreshes;

    // stats
    public readonly Stats Window = new();
    public long TotalFrames, TotalUploads, TotalAcks, ColorUploads, CageRebuilds;
    public double LastRebuildMs, PaintMs;
    public PaintLayer.UpdateResult LastPaintUpdate;
    public int Warmup = 120;
    public double EmaTotalMs, EmaSimMs;
    public double LastFrameMs;
    public string UploadPath = "none yet";
    public double LastAckLatencyMs;

    public sealed class Stats
    {
        public int Frames, Uploads, Acks, Busy, Idle, Substeps, ColorUploads, PaintUpdates, Rebuilds;
        public long RestUpdates;
        public double SimMs, DeformMs, UploadMs, MaxTotalMs, QueryMs, PaintMs, RebuildMs, SkinMs;
        public void Clear() { Frames = Uploads = Acks = Busy = Idle = Substeps = ColorUploads = PaintUpdates = Rebuilds = 0; RestUpdates = 0; SimMs = DeformMs = UploadMs = MaxTotalMs = QueryMs = PaintMs = RebuildMs = SkinMs = 0; }
    }

    const MeshUploadHint.Flag PartialFlags = MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals | MeshUploadHint.Flag.Dynamic;
    const int MaxVertices = 250_000;

    SoftbodyInstance(MeshRenderer renderer, Slot owner, StaticMesh provider, FrooxEngine.Mesh asset, MeshX original, MeshX work,
                     SoftbodyRig rig, string key, string mapDescription, PaintLayer paint, int[] tris, Vector3[] restNormals,
                     SoftnessSource source, bool invert, float[] soft, float[] bands, SkinSetup skin)
    {
        Renderer = renderer; Anchor = renderer.Slot; Owner = owner; Provider = provider; Asset = asset; _original = original; _work = work;
        _rig = rig; BuildKey = key; _n = work.VertexCount; _mapDescription = mapDescription;
        _paint = paint; _tris = tris; _restNormalsIn = restNormals; _source = source; _invert = invert; _soft = soft; _bands = bands;
        _cagePaintVersion = paint.Version;
        Profile = original.Profile;
        _origHighPriority = asset.HighPriorityIntegration;
        _restCenterLocal = 0.5f * (rig.Cage.RestBoundsMin + rig.Cage.RestBoundsMax);
        try { _assetManager = renderer.World?.AssetManager; } catch { }
        _assetData = original;
        Path = SkinPath.Static;
        if (skin != null)
        {
            Skinned = skin.Renderer; _skin = skin.Skin; _boneSlots = skin.Bones; _boneListCount = skin.BoneListCount;
            _bindPoses = skin.BindPoses; _skinMats = skin.Matrices; _skeletonRoot = skin.SkeletonRoot;
            _localSkin = new Matrix4x4[_skin.MatrixCount]; _lastSkin = new Matrix4x4[_skin.MatrixCount];
            _cpuReason = skin.CpuReason;
            if (skin.Gpu) Path = SkinPath.Gpu;
            else InitCpuSkin(skin);
        }
        UpdateRunningState();
    }
    long _blendBytes;

    /// <summary>The CPU path's per-frame buffers, from a <see cref="PrepareCpuSkin"/>'d setup.</summary>
    void InitCpuSkin(SkinSetup skin)
    {
        Path = SkinPath.Cpu;
        _basePos = skin.BasePos; _baseNrm = skin.BaseNrm; _restTan = skin.RestTan; _blend = skin.Blend;
        _blendW = new float[skin.BlendCount]; _lastBlendW = new float[skin.BlendCount];
        _outPos = new Vector3[_n]; _outNrm = new Vector3[_n];
        if (_restTan != null && _work.HasTangents) _outTan = new Vector4[_n];
        _blendBytes = skin.BlendBytes;
    }

    /// <summary>What the component's Status shows while this runs.</summary>
    public string RunningState { get; private set; } = "running";
    void UpdateRunningState() =>
        RunningState = !IsSkinned ? "running" : $"running (skinned, {BoneCount} bones, {(Path == SkinPath.Gpu ? "GPU-skinned" : "CPU-skinned")})";

    /// <summary>What a skinned instance needs from the renderer and its mesh.</summary>
    internal sealed class SkinSetup
    {
        public SkinnedMeshRenderer Renderer;
        public SkinData Skin;
        public Slot[] Bones;
        public int BoneListCount;
        public Matrix4x4[] BindPoses;
        public Matrix4x4[] Matrices;
        public Slot SkeletonRoot;
        // GPU path (0.9.0): the private copy keeps its skin data and the renderer skins it; otherwise why not
        public bool Gpu;
        public string CpuReason;
        // CPU skinning
        public Vector3[] BasePos, BaseNrm;       // bind space: rest + wobble (the deformer writes the moving vertices)
        public Vector4[] RestTan;                // the mesh's tangents (null without)
        public BlendshapeDeltas Blend;           // frame-0 deltas of the mesh's blendshapes
        public int BlendCount;
        public long BlendBytes;
    }

    /// <summary>
    /// Turns the private copy into the unskinned mesh the renderer will show: no bones, no bone bindings and no blendshape
    /// frames (the blendshapes themselves stay, so the renderer's synced weight list keeps its size). Bind-space rest data
    /// and sparse blendshape deltas are kept on the side for the per-frame CPU skinning.
    /// </summary>
    static void PrepareCpuSkin(SkinSetup skin, MeshX original, MeshX work, SoftbodyRig rig, Vector3[] restNormals, int n)
    {
        skin.BasePos = MemoryMarshal.Cast<float3, Vector3>(original.RawPositions.AsSpan(0, n)).ToArray();
        skin.BaseNrm = restNormals != null ? (Vector3[])restNormals.Clone() : rig.Deformer.RestNormals.ToArray();
        skin.RestTan = original.HasTangents ? MemoryMarshal.Cast<float4, Vector4>(original.RawTangents.AsSpan(0, n)).ToArray() : null;
        var shapes = original.BlendShapes.ToList();
        skin.BlendCount = shapes.Count;
        skin.Blend = new BlendshapeDeltas(shapes.Count, n, b =>
        {
            var bs = shapes[b];
            if (bs.FrameCount == 0) return (null, null, null);
            var f = bs[0];
            static Vector3[] Arr(float3[] a, int n) => a == null || a.Length < n ? null : MemoryMarshal.Cast<float3, Vector3>(a.AsSpan(0, n)).ToArray();
            return (Arr(f.RawPositions, n), bs.HasNormals ? Arr(f.RawNormals, n) : null, bs.HasTangents ? Arr(f.RawTangents, n) : null);
        });
        skin.BlendBytes = skin.Blend.StoredDeltas * 40;
        work.ClearBones(false);
        work.HasBoneBindings = false;
        foreach (var bs in work.BlendShapes) bs.ClearFrames();
        if (work.BoneCount != 0 || work.HasBoneBindings || work.BlendShapeCount != original.BlendShapeCount)
            throw new InvalidOperationException("the private copy could not be made unskinned");
    }

    public string Describe() =>
        $"{_rig.Cage.PointCount} points ({_rig.Cage.KinematicCount} pinned, cage {_rig.Cage.Nx}x{_rig.Cage.Ny}x{_rig.Cage.Nz}, " +
        $"{_rig.Cage.ActiveCells}/{_rig.Cage.TotalCells} cells around soft parts), " +
        $"{_n} verts ({_rig.Deformer.DynamicVertexCount} moving, {_rig.Deformer.NormalVertexCount} with normals), " +
        $"{_rig.Sim.EdgeConstraintCount} edges, {_rig.Sim.VolumeConstraintCount} volumes, {_rig.Sim.ServoCount} servos; softness map {_mapDescription}; " +
        $"paint {_paint.DabCount} dabs" + SkinDescription();

    string SkinDescription()
    {
        if (!IsSkinned) return Renderer is SkinnedMeshRenderer ? "; skinned renderer, but its mesh has no bones: follows the renderer slot (blendshapes still apply)" : "";
        int missing = 0; foreach (var b in _boneSlots) if (b == null) missing++;
        string how = Path == SkinPath.Gpu
            ? "GPU-skinned by the renderer (SoftbodyRenderer patch): bind-space partial uploads, skin data kept"
            : "CPU-skinned on this client, EXPERIMENTAL" + (_demotedReason != null ? "; fell back from the GPU path: " + _demotedReason : _cpuReason != null ? "; " + _cpuReason : "");
        string blend = _original.BlendShapeCount == 0 ? "no blendshapes" :
                       Path == SkinPath.Gpu ? $"{_original.BlendShapeCount} blendshapes applied by the renderer" :
                       $"{_original.BlendShapeCount} blendshapes applied on the CPU ({_blendBytes / 1024} KB of deltas)";
        return $"; skinned ({how}): {_skin.BoneCount} bones ({_rig.CageSkin.BonesUsed} move the cage" +
               $"{(missing > 0 ? $", {missing} not assigned: they follow the renderer slot" : "")}), {_skin.UnboundVertices} unweighted verts, " +
               $"{_rig.CageSkin.InheritedPoints} cage points with inherited weights, skeleton root '{_skeletonRoot?.Name ?? "-"}', skin scale {_rig.Sim.SkinScale:0.###}, " +
               blend + $"{(_original.HasTangents ? ", tangents" : "")}";
    }

    public int Resets => _rig.Sim.ResetCount;
    public int Tunes => _rig.Sim.TuneCount;
    public string LastResetReason => _rig.Sim.LastResetReason;
    public bool HasGround => _rig.Sim.HasGround;
    public int ProxyCount => _rig.Sim.ProxyCount;
    public int MovingVerts => _rig.Deformer.DynamicVertexCount;
    public int VertexCount => _n;

    /// <summary>Live re-tune (no rebuild). NeedsRebuild when a structural setting changed.</summary>
    public TuneResult ApplyTuning(SoftbodySettings settings) => _rig.ApplyTuning(settings);

    // ------------------------------------------------------------------ creation

    public enum CreateResult { Created, Wait, Fail }

    public static CreateResult TryCreate(MeshRenderer renderer, Slot owner, Softbody claimant, SoftbodySettings settings,
                                         Softbody.Channel channel, bool invert, Softbody.StartColor start, string key, int pointBudget,
                                         out SoftbodyInstance inst, out string reason)
    {
        inst = null; reason = null;
        if (renderer == null || renderer.IsRemoved) { reason = "set Renderer to a MeshRenderer or SkinnedMeshRenderer that uses a StaticMesh"; return CreateResult.Wait; }
        if (renderer.GetType() != typeof(MeshRenderer) && renderer is not SkinnedMeshRenderer)
        { reason = $"{renderer.GetType().Name} is not supported (MeshRenderer or SkinnedMeshRenderer only)"; return CreateResult.Fail; }
        if (renderer.Slot == null || renderer.Slot.IsRemoved) { reason = "the renderer's slot was removed"; return CreateResult.Wait; }
        var target = renderer.Mesh.Target;
        if (target == null) { reason = "the MeshRenderer has no mesh"; return CreateResult.Wait; }
        if (target is not StaticMesh provider)
        {
            reason = $"mesh provider is {target.GetType().Name}, not StaticMesh (procedural meshes rebuild their own asset; bake it first)";
            return CreateResult.Fail;
        }
        var asset = provider.Asset;
        if (asset == null || asset.LoadState != AssetLoadState.FullyLoaded || asset.Data == null) { reason = "mesh asset still loading"; return CreateResult.Wait; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>() || Unsafe.SizeOf<color>() != 16) { reason = "unexpected Elements.Core vector layout"; return CreateResult.Fail; }

        var original = asset.Data;
        int n = original.VertexCount;
        if (n <= 0) { reason = "mesh has no vertices"; return CreateResult.Fail; }
        if (n > MaxVertices) { reason = $"mesh has {n} vertices (cap {MaxVertices})"; return CreateResult.Fail; }

        // 0.9.1 safety: on a SkinnedMeshRenderer, every upload Softbody sends is built from the engine's MeshX. If that copy
        // lacks the skin data the renderer's mesh has, the first upload would replace the skinned mesh with one that has no
        // bones or blendshapes (a frozen avatar, and the renderer spamming SetBlendShapeWeight errors every frame). Refuse.
        if (renderer is SkinnedMeshRenderer smrCheck)
        {
            string diag = SkinDiagnostics(smrCheck, provider, asset, original);
            SoftbodyRuntime.LogOnce("skin-diag:" + diag, $"'{SafeName(claimant)}' skinned mesh check: {diag}");
            int rendererShapes = 0;
            try { rendererShapes = smrCheck.BlendShapeWeights.Count; } catch { }
            if (original.BoneCount == 0 || !original.HasBoneBindings)
            {
                reason = "refusing this skinned mesh: the engine's copy of it has no bone data (" + diag + "). Driving it would replace " +
                         "the skinned mesh in the renderer (frozen avatar, lost blendshapes), so Softbody stays off for it";
                return CreateResult.Fail;
            }
            if (original.BlendShapeCount < rendererShapes)
            {
                reason = $"refusing this skinned mesh: the engine's copy has {original.BlendShapeCount} blendshapes but the renderer uses " +
                         $"{rendererShapes} ({diag}); uploading it would drop blendshapes";
                return CreateResult.Fail;
            }
            if (original.BlendShapeCount > 0 && !SoftbodyRuntime.UseGpuSkinning(out var noGpu))
            {
                reason = "refusing this skinned mesh: it has blendshapes and the GPU path is not available (" + noGpu + "). The CPU path " +
                         "would leave the renderer's mesh without blendshape frames (SetBlendShapeWeight errors every frame). Install " +
                         "the SoftbodyRenderer patch (BepisLoader + RenderiteHook + --hookfxr-enable)";
                return CreateResult.Fail;
            }
        }

        if (!SoftbodyRuntime.TryClaim(asset, claimant, out var holder))
        {
            reason = $"this mesh asset is already driven by the {holder?.GetType().Name ?? "Softbody"} on '{SoftbodyRuntime.NameOf(holder)}' (every StaticMesh with the same URL shares one asset; " +
                     "give this one its own mesh URL)";
            return CreateResult.Wait;
        }
        bool created = false;
        try
        {
            if (AssetUploads.IsBusy(asset, out _) || AssetUploads.HasPendingRestore(asset)) { reason = "waiting for a previous upload on this mesh"; return CreateResult.Wait; }

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
            var trisArr = tris.ToArray();

            // Paint: the mesh's own colours (white without any) with the component's dabs applied.
            var source = (SoftnessSource)(int)channel;
            PaintLayer paint;
            ReadOnlySpan<Vector4> baseCols = work.HasColors ? MemoryMarshal.Cast<color, Vector4>(work.RawColors.AsSpan(0, n)) : ReadOnlySpan<Vector4>.Empty;
            if (start != Softbody.StartColor.MeshColors)
            {
                // StartAs: begin from a plain white (all soft) or black (all firm) mesh instead of its own colours
                var fill = new Vector4[n];
                Array.Fill(fill, start == Softbody.StartColor.AllFirm ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One);
                baseCols = fill;
            }
            try { paint = new PaintLayer(pos, baseCols, trisArr); }
            catch (Exception ex) { reason = "paint layer failed: " + ex.Message; return CreateResult.Fail; }
            var dabs = claimant.ReadDabs(original.Profile);
            if (dabs.Length > 0) paint.Update(dabs);
            paint.ClearDirty(); // the work copy gets the effective colours right here
            float[] soft = null, bands = null;
            string map = "none (no vertex colours: all soft)";
            if (work.HasColors || paint.DabCount > 0 || start != Softbody.StartColor.MeshColors)
            {
                if (!work.HasColors) work.HasColors = true;
                paint.Colors.CopyTo(MemoryMarshal.Cast<color, Vector4>(work.RawColors.AsSpan(0, n)));
                soft = new float[n];
                bands = new float[n * HueBands.Count];
                paint.ComputeMaps(source, invert, soft, bands);
                map = DescribeMap(soft, channel, invert, paint.DabCount);
            }

            var anchor = ReadAnchor(renderer.Slot);
            if (!anchor.IsFinite) { reason = "the renderer's slot transform is not finite"; return CreateResult.Wait; }

            // Skinned: the mesh's bones, bind poses and weights (a skinned renderer without bones renders on its slot: static path).
            SkinSetup skin = null;
            if (renderer is SkinnedMeshRenderer smr && original.BoneCount > 0 && original.HasBoneBindings)
            {
                try { skin = ReadSkinSetup(smr, original, n); }
                catch (Exception ex) { reason = "reading the mesh's bones failed: " + ex.Message; return CreateResult.Fail; }
                if (!ComputeSkin(renderer.Slot, skin.Bones, skin.BindPoses, skin.Matrices, null))
                { reason = "the skeleton's pose is not finite"; return CreateResult.Wait; }
            }

            SoftbodyRig rig;
            try
            {
                rig = skin != null
                    ? SoftbodyRig.CreateSkinned(pos, hadNormals ? nrm : ReadOnlySpan<Vector3>.Empty, trisArr, soft ?? ReadOnlySpan<float>.Empty, settings,
                                                skin.Skin, skin.Matrices, anchor, bands ?? ReadOnlySpan<float>.Empty)
                    : SoftbodyRig.Create(pos, hadNormals ? nrm : ReadOnlySpan<Vector3>.Empty, trisArr, soft ?? ReadOnlySpan<float>.Empty, settings, anchor,
                                         bands ?? ReadOnlySpan<float>.Empty);
            }
            catch (Exception ex) { reason = "cage build failed: " + ex.Message; return CreateResult.Fail; }
            if (rig.Cage.PointCount > pointBudget)
            {
                reason = $"point cap: this cage needs {rig.Cage.PointCount} points but only {Math.Max(0, pointBudget)} are left of {SoftbodyRuntime.MaxTotalPoints} on this client (lower Cells)";
                rig.Dispose();
                return CreateResult.Wait;
            }
            if (!hadNormals) rig.Deformer.RestNormals.CopyTo(nrm);
            var restNormals = hadNormals ? MemoryMarshal.Cast<float3, Vector3>(original.RawNormals.AsSpan(0, n)).ToArray() : null;

            if (skin != null)
            {
                // GPU path (0.9.0) when the renderer is patched: the copy keeps its bones, bind poses, weights and blendshapes.
                skin.Gpu = SoftbodyRuntime.UseGpuSkinning(out skin.CpuReason);
                if (!skin.Gpu) SoftbodyRuntime.LogOnce("cpu-path:" + skin.CpuReason, "skinned: CPU path (" + skin.CpuReason + ")");
                if (skin.Gpu)
                {
                    // 0.9.2: keep the original vertex layout. Paint colours are not shown on a skinned mesh without vertex colours
                    // (the softness still follows the paint); a mesh without normals is not driven at all.
                    if (!hadNormals) { reason = "this skinned mesh has no normals; Softbody would have to change its vertex layout"; return CreateResult.Fail; }
                    if (work.HasColors && !original.HasColors) work.HasColors = false;
                }
                // CPU skinning (see the class comment): the GPU must see an unskinned mesh, or the stock renderer recreates it on
                // every upload. The copy loses its bones and its blendshape frames but keeps the blendshapes themselves (names,
                // count), so the SkinnedMeshRenderer's synced BlendShapeWeights list is never resized (SetupBlendShapes would).
                if (!skin.Gpu)
                {
                    try { PrepareCpuSkin(skin, original, work, rig, restNormals, n); }
                    catch (Exception ex) { rig.Dispose(); reason = "preparing CPU skinning failed: " + ex.Message; return CreateResult.Fail; }
                }
            }

            inst = new SoftbodyInstance(renderer, owner, provider, asset, original, work, rig, key, map, paint, trisArr, restNormals, source, invert, soft, bands, skin);
            created = true;
            return CreateResult.Created;
        }
        finally
        {
            if (!created) SoftbodyRuntime.Release(asset, claimant);
        }
    }

    internal static SkinSetup ReadSkinSetup(SkinnedMeshRenderer smr, MeshX mesh, int n)
    {
        int nb = mesh.BoneCount;
        var bindings = mesh.RawBoneBindings;
        if (bindings == null || bindings.Length < n) throw new InvalidOperationException("the mesh has fewer bone bindings than vertices");
        var bi = new int[n * 4]; var bw = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            var b = bindings[v]; int o = v * 4;
            bi[o] = b.boneIndex0; bi[o + 1] = b.boneIndex1; bi[o + 2] = b.boneIndex2; bi[o + 3] = b.boneIndex3;
            bw[o] = b.weight0; bw[o + 1] = b.weight1; bw[o + 2] = b.weight2; bw[o + 3] = b.weight3;
        }
        var setup = new SkinSetup
        {
            Renderer = smr,
            Skin = SkinData.FromBindings(nb, bi, bw),
            Bones = new Slot[nb],
            BoneListCount = smr.Bones.Count,
            BindPoses = new Matrix4x4[nb],
            Matrices = new Matrix4x4[nb + 1],
        };
        for (int b = 0; b < nb; b++)
        {
            setup.BindPoses[b] = SkinMatrices.ToNumerics(mesh.GetBone(b).BindPose);
            var s = b < smr.Bones.Count ? smr.Bones[b] : null;
            setup.Bones[b] = s != null && !s.IsRemoved ? s : null;
        }
        setup.SkeletonRoot = CommonAncestor(setup.Bones, smr.World?.RootSlot);
        return setup;
    }

    /// <summary>The lowest slot that has every assigned bone under it (null when that is the world root, or no bone is assigned).</summary>
    internal static Slot CommonAncestor(Slot[] bones, Slot worldRoot)
    {
        Slot lca = null;
        foreach (var b in bones)
        {
            if (b == null) continue;
            if (lca == null) { lca = b; continue; }
            while (lca != null && !b.IsChildOf(lca, true)) lca = lca.Parent;
        }
        return lca == null || ReferenceEquals(lca, worldRoot) ? null : lca;
    }

    /// <summary>
    /// The skin matrices for the bones' current pose: <c>BoneSlot.LocalToGlobal * BindPose</c> per bone (a missing bone
    /// follows the renderer slot, like the engine's own CPU skinning), plus the slot bone (the renderer slot itself) for
    /// unweighted vertices. With <paramref name="globalToSpace"/> they map into that space instead of world.
    /// </summary>
    internal static bool ComputeSkin(Slot rendererSlot, Slot[] bones, Matrix4x4[] bindPoses, Span<Matrix4x4> dst, Matrix4x4? globalToSpace)
    {
        var slotL2G = rendererSlot.LocalToGlobal;
        var slot = SkinMatrices.ToNumerics(slotL2G);
        for (int b = 0; b < bones.Length; b++)
        {
            var s = bones[b];
            var m = s != null && !s.IsRemoved ? SkinMatrices.Skin(bindPoses[b], s.LocalToGlobal) : bindPoses[b] * slot;
            dst[b] = globalToSpace.HasValue ? m * globalToSpace.Value : m;
        }
        dst[bones.Length] = globalToSpace.HasValue ? slot * globalToSpace.Value : slot;
        for (int b = 0; b <= bones.Length; b++) if (!Lbs.IsFinite(dst[b])) return false;
        return true;
    }

    /// <summary>True when the renderer's Bones list no longer matches what this cage was built from.</summary>
    bool BonesChanged()
    {
        var list = Skinned.Bones;
        if (list.Count != _boneListCount) return true;
        for (int b = 0; b < _boneSlots.Length; b++)
        {
            var s = b < list.Count ? list[b] : null;
            if (s != null && s.IsRemoved) s = null;
            if (!ReferenceEquals(s, _boneSlots[b])) return true;
        }
        return false;
    }

    static string DescribeMap(float[] soft, Softbody.Channel channel, bool invert, int dabs)
    {
        float lo = 1, hi = 0; double sum = 0;
        foreach (var v in soft) { lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); sum += v; }
        return $"{channel.ToString().ToLowerInvariant()}{(invert ? " inverted" : "")} {lo:0.##}..{hi:0.##} (mean {sum / Math.Max(1, soft.Length):0.##})" +
               (dabs > 0 ? $", {dabs} paint dabs" : "");
    }

    static string SafeName(Softbody c)
    {
        try { return c?.Slot?.Name ?? "?"; } catch { return "?"; }
    }

    static Vector3 V(in float3 f) => new(f.x, f.y, f.z);
    static float3 F(in Vector3 v) => new(v.X, v.Y, v.Z);

    internal static AnchorPose ReadAnchor(Slot s)
    {
        var p = s.GlobalPosition; var q = s.GlobalRotation; var sc = s.GlobalScale;
        return new AnchorPose(V(p), new Quaternion(q.x, q.y, q.z, q.w), V(sc));
    }

    // ------------------------------------------------------------------ per frame

    public enum FrameResult { Ok, Paused, Rebuild, Gone, Failed }
    /// <summary>Why <see cref="FrameResult.Failed"/> was returned (the component pauses this Softbody and shows it).</summary>
    public string FailReason;

    /// <summary>What the engine and the renderer know about a skinned mesh (0.9.1 diagnostics; logged once per distinct result).</summary>
    internal static string SkinDiagnostics(SkinnedMeshRenderer smr, StaticMesh provider, FrooxEngine.Mesh asset, MeshX m)
    {
        var sb = new StringBuilder();
        try
        {
            sb.Append($"engine MeshX {m.VertexCount} verts, {m.BoneCount} bones, bone bindings {m.HasBoneBindings}, {m.BlendShapeCount} blendshapes, " +
                      $"normals {m.HasNormals}, tangents {m.HasTangents}, colours {m.HasColors}, uv0 {m.HasUV0s}");
        }
        catch (Exception ex) { sb.Append("engine MeshX unreadable: " + ex.Message); }
        try
        {
            var md = asset.Metadata;
            if (md != null) sb.Append($"; asset metadata {md.VertexCount} verts, {md.BoneCount} bones, bone bindings {md.HasBoneBindings}, {md.BlendshapeCount} blendshapes");
        }
        catch { }
        try { sb.Append($"; bone metadata {asset.BoneMetadata?.Count ?? -1}"); } catch { }
        try { sb.Append($"; renderer {smr.Bones.Count} bone slots, {smr.BlendShapeWeights.Count} blendshape weights"); } catch { }
        try { sb.Append($"; mesh {provider.URL.Value} readable {provider.Readable.Value}, asset version {asset.Version}"); } catch { }
        return sb.ToString();
    }

    /// <summary>Runs on the world thread from the component's OnCommonUpdate.</summary>
    public FrameResult Frame(World world, float dt)
    {
        if (Anchor.IsRemoved || Renderer.IsRemoved || Provider.IsRemoved) return FrameResult.Gone;
        if (!ReferenceEquals(Renderer.Mesh.Target, Provider) || !ReferenceEquals(Provider.Asset, Asset)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (Asset.LoadState != AssetLoadState.FullyLoaded) { RebuildReason = "the mesh asset is reloading"; return FrameResult.Rebuild; }
        if (_firstUploadAcked && !ReferenceEquals(Asset.Data, _assetData)) { RebuildReason = "the engine reloaded the mesh asset"; return FrameResult.Rebuild; }
        if (!Anchor.IsActive || !Renderer.Enabled) { _paused = true; return FrameResult.Paused; }

        var anchor = ReadAnchor(Anchor);
        if (!anchor.IsFinite) { _paused = true; return FrameResult.Paused; }
        if (_rig.Sim.ScaleChanged(anchor)) { RebuildReason = "the renderer slot's scale changed"; return FrameResult.Rebuild; }
        long t0 = Stopwatch.GetTimestamp();
        if (IsSkinned)
        {
            if (BonesChanged()) { RebuildReason = "the renderer's bones changed"; return FrameResult.Rebuild; }
            // the bones' pose this frame (VRIK and other early updates have already run: see README, frame order):
            // world space for the cage, the renderer slot's space for the mesh the renderer shows
            if (!ComputeSkin(Anchor, _boneSlots, _bindPoses, _skinMats, null) ||
                !ComputeSkin(Anchor, _boneSlots, _bindPoses, _localSkin, SkinMatrices.ToNumerics(Anchor.GlobalToLocal)))
            { _paused = true; return FrameResult.Paused; }
            Window.SkinMs += Ms(Stopwatch.GetTimestamp() - t0);
        }
        if (_paused)
        {
            _paused = false;
            if (IsSkinned) _rig.Sim.ResetSkinned(_skinMats, "resumed after a pause");
            else _rig.Sim.Reset(anchor, "resumed after a pause");
        }

        WorldQueries(world, anchor, dt);
        long t1 = Stopwatch.GetTimestamp();
        int steps = IsSkinned ? _rig.StepSkinned(dt, _skinMats) : _rig.Step(dt, anchor);
        long t2 = Stopwatch.GetTimestamp();
        if (IsSkinned) Window.RestUpdates += _rig.Sim.RestUpdates;
        Window.Substeps += steps;
        Window.QueryMs += Ms(t1 - t0);
        double simMs = Ms(t2 - t1);
        Window.SimMs += simMs;
        Window.Frames++;
        TotalFrames++;

        double deformMs = 0, uploadMs = 0;

        if (_ackPending)
        {
            Window.Busy++; // renderer has not taken the previous frame yet; never queue a second upload
            if (Elapsed(_uploadSentAt) > 5 && UploadPath != "stalled") { UploadPath = "stalled"; SoftbodyRuntime.Warn($"'{Name}': renderer has not acknowledged an upload for 5 s; uploads paused"); }
        }
        else
        {
            if (_demote && Path == SkinPath.Gpu)
            {
                if (_refusedByRenderer)
                {
                    _demote = false;
                    FailReason = "stopped: the SoftbodyRenderer patch refused to update this skinned mesh in place (see its log); " +
                                 "the avatar's mesh was left untouched";
                    return FrameResult.Failed;
                }
                if (_original.BlendShapeCount > 0)
                {
                    _demote = false;
                    FailReason = "stopped: the renderer did not update this skinned mesh in place, and with blendshapes the CPU path " +
                                 "would break them; the original mesh is restored. Check the SoftbodyRenderer log";
                    return FrameResult.Failed;
                }
                DemoteToCpu();
            }
            long d0 = Stopwatch.GetTimestamp();
            // Paint colours into the private copy (only here: no upload is reading it now).
            if (_paint.IsDirty)
            {
                bool layout = !_work.HasColors; // first paint on a mesh without vertex colours: the vertex layout grows
                if (layout && Path == SkinPath.Gpu)
                {
                    // 0.9.2: a skinned mesh keeps its layout (a full upload would replace it in the renderer); the paint still
                    // drives the softness, it just is not shown as colours on this mesh.
                    _paint.ClearDirty();
                    SoftbodyRuntime.LogOnce("gpu-nocolors:" + Name, $"'{Name}': this skinned mesh has no vertex colours, so the paint is not shown on it (it still sets the softness)");
                    goto paintDone;
                }
                if (layout) _work.HasColors = true;
                int lo = layout ? 0 : _paint.DirtyMin, hi = layout ? _n - 1 : _paint.DirtyMax;
                var dst = MemoryMarshal.Cast<color, Vector4>(_work.RawColors.AsSpan(0, _n));
                _paint.Colors.Slice(lo, hi - lo + 1).CopyTo(dst.Slice(lo, hi - lo + 1));
                _paint.ClearDirty();
                _colorsInWork = true;
                if (layout) _needFullUpload = true;
            }
            paintDone:
            float disp = IsSkinned ? _rig.UpdateCageSkinned(out float change) : _rig.UpdateCage(anchor, out change);
            // The mesh lives in the slot's frame: if the cage did not change shape in that frame since the last upload (at
            // rest, or carried rigidly), the GPU copy is already right. Skinned, GPU path: the same in bind space (the renderer
            // follows the pose and the blendshapes itself). Skinned, CPU path: also when the pose or a blendshape weight
            // changed, since the shown mesh is the skinned one.
            bool cageChanged = !_firstUploadSent || _forceUpload || change >= 2e-5f;
            bool shapeChanged = cageChanged;
            if (Path == SkinPath.Cpu && !shapeChanged) shapeChanged = ReadBlendWeights() || !_localSkin.AsSpan().SequenceEqual(_lastSkin);
            else if (Path == SkinPath.Cpu) ReadBlendWeights();
            bool idle = !shapeChanged && !_colorsInWork && !_needFullUpload;
            long d1 = d0;
            if (idle) Window.Idle++;
            else
            {
                if (Path == SkinPath.Cpu)
                {
                    if (shapeChanged)
                    {
                        if (cageChanged) _rig.ApplyToMesh(_basePos, _baseNrm, out _, out _); // bind space: only the moving vertices
                        long k0 = Stopwatch.GetTimestamp();
                        ComposeSkinned(out _lastBmin, out _lastBmax);
                        LastSkinMeshMs = Ms(Stopwatch.GetTimestamp() - k0);
                    }
                }
                else if (shapeChanged)
                {
                    // static mesh: the slot's frame. GPU path: bind space (rest + bind-space offset x softness; the renderer skins it).
                    var pos = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
                    var nrm = MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
                    _rig.ApplyToMesh(pos, nrm, out _lastBmin, out _lastBmax);
                }
                d1 = Stopwatch.GetTimestamp();
                deformMs = Ms(d1 - d0);

                MeshUploadHint hint = default;
                bool full = !_firstUploadSent || _needFullUpload;
                bool colors = full || _colorsInWork;
                bool gpuPartial = false;
                string gpuFullReason = null;
                if (Path == SkinPath.Gpu && _needFullUpload)
                {
                    FailReason = "stopped: this skinned mesh would need a full upload (which replaces it in the renderer); nothing was sent";
                    return FrameResult.Failed;
                }
                if (Path == SkinPath.Gpu && !_needFullUpload)
                {
                    // GPU path: a true partial upload of the moved streams; the asset's buffer keeps its layout, indices and skin
                    // data. The first one carries the colours too when the copy has them (StartAs / paint may differ from the mesh's).
                    colors = (!_firstUploadSent || _colorsInWork) && _work.HasColors;
                    hint = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
                    if (colors) hint[MeshUploadHint.Flag.Colors] = true;
                    gpuFullReason = GpuUpload.Check(Asset, _work, hint);
                    if (gpuFullReason == null) { gpuPartial = true; full = false; }
                    else
                    {
                        FailReason = "stopped: this skinned mesh would need a full upload (" + gpuFullReason + "), which replaces it in the renderer; nothing was sent";
                        return FrameResult.Failed;
                    }
                }
                if (gpuPartial) { }
                else if (full)
                {
                    // Full upload (the first one, or the vertex layout grew): layout may differ if we added normals or colours, marked dynamic.
                    hint = default;
                    MeshUploadHintHelper.SetFromMesh(ref hint, _work);
                    hint[MeshUploadHint.Flag.Dynamic] = true;
                    hint[MeshUploadHint.Flag.Readable] = Provider.Readable.Value;
                    if (Path == SkinPath.Cpu)
                    {
                        // never skin data: the renderer would recreate the mesh on every later upload (see README, skinned meshes)
                        hint[MeshUploadHint.Flag.BindPoses] = false; hint[MeshUploadHint.Flag.BoneWeights] = false; hint[MeshUploadHint.Flag.Blendshapes] = false;
                    }
                    Asset.HighPriorityIntegration = true;
                }
                else
                {
                    hint = new MeshUploadHint(PartialFlags);
                    if (colors) hint[MeshUploadHint.Flag.Colors] = true;
                    if (Path == SkinPath.Cpu && _outTan != null) hint[MeshUploadHint.Flag.Tangents] = true;
                }

                var bounds = BoundingBox.FromPoints(F(_lastBmin), F(_lastBmax));
                _ackPending = true;
                _uploadSentAt = Stopwatch.GetTimestamp();
                bool started;
                if (gpuPartial)
                {
                    string why;
                    if (!_firstUploadSent) Asset.HighPriorityIntegration = true;
                    try { why = AssetUploads.TryUploadPartial(Asset, _work, hint, bounds, changed => OnAck(changed, true)); }
                    catch { _ackPending = false; throw; }
                    started = why == null;
                    if (!started && why != AssetUploads.Busy)
                    {
                        _needFullUpload = true;
                        SoftbodyRuntime.Warn($"'{Name}': GPU-path partial upload not possible ({why}); sending a full one");
                    }
                }
                else
                {
                    try { started = AssetUploads.TryUpload(Asset, _work, hint, bounds, changed => OnAck(changed, false)); }
                    catch { _ackPending = false; throw; }
                    if (started) _assetData = _work; // SetFromMeshX made the private copy the asset's data
                }
                if (!started) { _ackPending = false; Window.Busy++; }
                else
                {
                    UploadPath = !_firstUploadSent ? "full (first upload)" : full ? "full (vertex colours added for paint)" :
                                 colors ? "partial: Positions|Normals|Colors (+Dynamic), no index/layout upload" : "partial: Positions|Normals (+Dynamic), no index/layout upload";
                    if (Path == SkinPath.Cpu)
                        UploadPath = !full ? $"CPU-skinned, partial: Positions|Normals{(_outTan != null ? "|Tangents" : "")}{(colors ? "|Colors" : "")} (+Dynamic), no skin data" :
                                     "CPU-skinned, full without skin data (" + (!_firstUploadSent ? "first upload" : _fallbackUploadPending ? "fell back from the GPU path" : "layout grew") + ")";
                    else if (Path == SkinPath.Gpu)
                    {
                        UploadPath = gpuPartial
                            ? $"GPU-skinned, true partial: Positions|Normals{(colors ? "|Colors" : "")}, skin data kept, no layout/index upload"
                            : $"GPU-skinned, full with skin data ({gpuFullReason ?? "vertex colours added for paint"})";
                        if (gpuPartial) GpuPartials++;
                        if (colors) _gpuColorsUploaded = true;
                    }
                    _fallbackUploadPending = false;
                    _firstUploadSent = true;
                    _needFullUpload = false;
                    _forceUpload = false;
                    if (colors) { _colorsInWork = false; ColorUploads++; Window.ColorUploads++; }
                    if (cageChanged) { _rig.CommitSnapshot(); LastDisplacement = disp; }
                    if (Path == SkinPath.Cpu && shapeChanged) { _localSkin.AsSpan().CopyTo(_lastSkin); _blendW.AsSpan().CopyTo(_lastBlendW); }
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
        if (TotalFrames > Warmup)
        {
            EmaTotalMs = EmaTotalMs <= 0 ? total : EmaTotalMs + 0.05 * (total - EmaTotalMs);
            EmaSimMs = EmaSimMs <= 0 ? simMs : EmaSimMs + 0.05 * (simMs - EmaSimMs);
        }
        LastFrameMs = total;
        return FrameResult.Ok;
    }

    /// <summary>Reads the renderer's blendshape weights (synced fields, read only). True when one changed since the last upload.</summary>
    bool ReadBlendWeights()
    {
        if (_blendW.Length == 0) return false;
        var list = Skinned.BlendShapeWeights;
        int count = Math.Min(list.Count, _blendW.Length);
        bool changed = false;
        int active = 0;
        for (int i = 0; i < _blendW.Length; i++)
        {
            float w = i < count ? list[i] : 0f;
            if (!float.IsFinite(w)) w = 0f;
            _blendW[i] = w;
            if (MathF.Abs(w) > 1e-6f) active++;
            if (w != _lastBlendW[i]) changed = true;
        }
        ActiveBlendshapes = active;
        return changed;
    }

    /// <summary>
    /// The mesh the renderer shows, written into the private copy: base (bind space, rest + wobble) plus the active
    /// blendshapes, CPU-skinned into the renderer slot's space (positions, normals, tangents), like the GPU would.
    /// </summary>
    void ComposeSkinned(out Vector3 min, out Vector3 max)
    {
        Vector3[] p = _basePos, nn = _baseNrm; Vector4[] t = _restTan;
        if (ActiveBlendshapes > 0)
        {
            _mixPos ??= new Vector3[_n]; _mixNrm ??= new Vector3[_n];
            _basePos.AsSpan().CopyTo(_mixPos); _baseNrm.AsSpan().CopyTo(_mixNrm);
            if (_restTan != null) { _mixTan ??= new Vector4[_n]; _restTan.AsSpan().CopyTo(_mixTan); }
            _blend.Apply(_blendW, _mixPos, _mixNrm, _restTan != null ? _mixTan : Span<Vector4>.Empty);
            p = _mixPos; nn = _mixNrm; t = _restTan != null ? _mixTan : null;
        }
        _skin.SkinMesh(_localSkin, p, nn, _outTan != null ? t : null, _outPos, _outNrm, _outTan, parallel: _n >= 16384, out min, out max);
        _outPos.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n)));
        _outNrm.AsSpan().CopyTo(MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n)));
        if (_outTan != null) _outTan.AsSpan().CopyTo(MemoryMarshal.Cast<float4, Vector4>(_work.RawTangents.AsSpan(0, _n)));
    }

    /// <summary>
    /// GPU path to CPU path (world thread, no upload in flight): the renderer answered a vertex-only upload with a new mesh
    /// instance, so it does not keep this mesh skinned while we change its vertices. Strip the copy's skin data as 0.8.0 does
    /// and send a full unskinned upload next; the ack's re-bind (QueueAssetCreated) already went out.
    /// </summary>
    void DemoteToCpu()
    {
        _demote = false;
        _demotedReason = "the renderer recreated the mesh on a vertex-only upload (the SoftbodyRenderer patch did not apply it in place)";
        SoftbodyRuntime.NoteGpuFailed();
        SoftbodyRuntime.Warn($"'{Name}': skinned: {_demotedReason}; switching this Softbody to the CPU path");
        var setup = new SkinSetup { Skin = _skin };
        PrepareCpuSkin(setup, _original, _work, _rig, _restNormalsIn, _n);
        InitCpuSkin(setup);
        Array.Clear(_lastSkin);
        _needFullUpload = true;
        _forceUpload = true;
        _fallbackUploadPending = true;
        UploadPath = "falling back to the CPU path";
        UpdateRunningState();
    }
    bool _fallbackUploadPending;
    volatile bool _refusedByRenderer;   // the SoftbodyRenderer patch refused a vertex-only upload (its mesh is untouched)
    readonly StrongBox<bool> _recreated = new();   // shared with a queued in-place restore: a recreated mesh needs a full one

    /// <summary>Makes every renderer of this mesh pick up the renderer's new mesh instance (WorldAssetManager.QueueAssetCreated:
    /// a concurrent queue, safe from the renderer-message thread; the asset provider re-notifies its references next frame).</summary>
    void Rebind()
    {
        Interlocked.Increment(ref InstanceChanges);
        try { _assetManager?.QueueAssetCreated(Provider); } catch { }
    }

    void OnAck(bool instanceChanged, bool gpuPartial)
    {
        // The renderer made a new mesh instance (it always does after a skinned upload, e.g. our first upload over the
        // original avatar mesh): renderers keep the old one until the engine re-sends their mesh. Queue that (thread-safe).
        bool refused = gpuPartial && instanceChanged && SoftbodyRuntime.RendererPatchPresent();
        if (instanceChanged && !refused) Rebind();
        if (refused) { Interlocked.Increment(ref GpuRecreatedAcks); _refusedByRenderer = true; _demote = true; }
        else if (gpuPartial)
        {
            // GPU path: a vertex-only upload must come back in place. A new instance means the renderer did not apply it in
            // place (no SoftbodyRenderer patch, or it refused this mesh): fall back to the CPU path on the next frame.
            if (instanceChanged) { Interlocked.Increment(ref GpuRecreatedAcks); _recreated.Value = true; _demote = true; }
            else if (Interlocked.Increment(ref GpuInPlaceAcks) == 1) SoftbodyRuntime.NoteGpuConfirmed();
        }
        LastAckLatencyMs = Elapsed(_uploadSentAt) * 1000;
        _firstUploadAcked = true;
        Interlocked.Increment(ref _acksRaw);
        _ackPending = false;
        if (UploadPath == "stalled") UploadPath = "resumed";
    }
    long _acksRaw;
    public long TakeAcks() { long a = Interlocked.Exchange(ref _acksRaw, 0); TotalAcks += a; return a; }

    // ------------------------------------------------------------------ paint

    /// <summary>Brings the effective colours to the component's current dabs (already in this mesh's colour space).
    /// Colours are uploaded on the next free frame; the cage follows after <see cref="RebuildDelay"/>.</summary>
    public PaintLayer.UpdateResult UpdatePaint(ReadOnlySpan<Dab> dabs)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = _paint.Update(dabs);
        double ms = Ms(Stopwatch.GetTimestamp() - t0);
        if (r.Kind != PaintLayer.UpdateKind.None)
        {
            _lastPaintChangeTs = Stopwatch.GetTimestamp();
            LastPaintUpdate = r;
            PaintMs = ms;
            Window.PaintUpdates++;
            Window.PaintMs += ms;
        }
        return r;
    }

    /// <summary>The paint changed since the cage was built.</summary>
    public bool CageIsStale => _paint.Version != _cagePaintVersion;
    public double SecondsSincePaintChange => _lastPaintChangeTs == 0 ? double.MaxValue : Elapsed(_lastPaintChangeTs);

    public enum RebuildResult { Done, Unchanged, Busy, Failed }

    /// <summary>
    /// Rebuilds the cage in place from the effective colours: same mesh copy and asset, no restore, the jelly starts
    /// again from rest. Busy while an upload is in flight (the copy is reset to the rest mesh, so it must be idle).
    /// </summary>
    public RebuildResult RebuildCage(int pointBudget, out string reason)
    {
        reason = null;
        if (_ackPending) return RebuildResult.Busy;
        long t0 = Stopwatch.GetTimestamp();
        long version = _paint.Version;
        float[] soft = null, bands = null;
        bool useMap = _paint.HadBaseColors || _paint.DabCount > 0;
        if (useMap)
        {
            soft = new float[_n];
            bands = new float[_n * HueBands.Count];
            _paint.ComputeMaps(_source, _invert, soft, bands);
        }
        bool same = (soft == null && _soft == null) ||
                    (soft != null && _soft != null && soft.AsSpan().SequenceEqual(_soft) && bands.AsSpan().SequenceEqual(_bands));
        if (same) { _cagePaintVersion = version; return RebuildResult.Unchanged; }

        var anchor = ReadAnchor(Anchor);
        if (!anchor.IsFinite) return RebuildResult.Busy;
        if (IsSkinned && !ComputeSkin(Anchor, _boneSlots, _bindPoses, _skinMats, null)) return RebuildResult.Busy;
        SoftbodyRig rig;
        try
        {
            rig = IsSkinned
                ? SoftbodyRig.CreateSkinned(_paint.RestPositions, _restNormalsIn ?? ReadOnlySpan<Vector3>.Empty, _tris, soft ?? ReadOnlySpan<float>.Empty,
                                            _rig.Settings, _skin, _skinMats, anchor, bands ?? ReadOnlySpan<float>.Empty)
                : SoftbodyRig.Create(_paint.RestPositions, _restNormalsIn ?? ReadOnlySpan<Vector3>.Empty, _tris, soft ?? ReadOnlySpan<float>.Empty,
                                     _rig.Settings, anchor, bands ?? ReadOnlySpan<float>.Empty);
        }
        catch (Exception ex) { reason = "cage rebuild failed: " + ex.Message; return RebuildResult.Failed; }
        if (rig.Cage.PointCount > pointBudget)
        {
            reason = $"point cap: the repainted cage needs {rig.Cage.PointCount} points but only {Math.Max(0, pointBudget)} are left of {SoftbodyRuntime.MaxTotalPoints} on this client (lower Cells)";
            rig.Dispose();
            return RebuildResult.Failed;
        }
        var old = _rig;
        _rig = rig;
        try { old.Dispose(); } catch { }
        // The new cage moves a different set of vertices: start the copy (skinned, CPU path: the bind-space base; GPU path: the
        // copy itself, which is in bind space) from the rest mesh again.
        var pos = Path == SkinPath.Cpu ? _basePos.AsSpan() : MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
        var nrm = Path == SkinPath.Cpu ? _baseNrm.AsSpan() : MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
        _paint.RestPositions.CopyTo(pos);
        if (_restNormalsIn != null) _restNormalsIn.CopyTo(nrm);
        else rig.Deformer.RestNormals.CopyTo(nrm);
        _forceUpload = true;
        _soft = soft; _bands = bands;
        _cagePaintVersion = version;
        if (soft != null) _mapDescription = DescribeMap(soft, (Softbody.Channel)(int)_source, _invert, _paint.DabCount);
        CageRebuilds++;
        LastRebuildMs = Ms(Stopwatch.GetTimestamp() - t0);
        Window.Rebuilds++;
        Window.RebuildMs += LastRebuildMs;
        return RebuildResult.Done;
    }

    // ------------------------------------------------------------------ world colliders -> statics in our sim

    void WorldQueries(World world, in AnchorPose anchor, float dt)
    {
        _dbgSearch = false;
        if (!_rig.Settings.Collide) return; // live setting
        var physics = world.Physics;
        Vector3 center, ext;
        if (IsSkinned)
        {
            // a skinned cage is wherever the bones put it (the renderer slot is often far away, at the avatar root)
            _rig.Sim.TargetBounds(out var lo, out var hi);
            center = 0.5f * (lo + hi);
            ext = hi - lo + new Vector3(2f * _rig.Sim.RadiusWorld);
        }
        else
        {
            center = anchor.TransformPoint(_restCenterLocal);
            ext = (_rig.Cage.RestBoundsMax - _rig.Cage.RestBoundsMin) * Vector3.Abs(anchor.Scale);
        }
        _dbgSearch = true;
        _dbgRayOrigin = center;
        _dbgSearchCenter = center;
        _dbgSearchSize = ext * 1.5f + new Vector3(1.2f);

        _groundTimer -= dt;
        if (_groundTimer <= 0)
        {
            _groundTimer = 0.1f;
            var origin = F(center);
            var down = new float3(0, -1, 0);
            var hit = physics.RaycastOne(in origin, in down, 50f, IsWorldCollider, false, null);
            // 0.10.1: only something floor-like counts (the ground is one infinite plane in the cage's simulation, so a
            // steep surface or the curve of a ball under the body would tilt the whole floor)
            if (hit.HasValue && hit.Value.Normal.y >= 0.6f) _rig.Sim.SetGround(V(hit.Value.Point), V(hit.Value.Normal));
            else _rig.Sim.ClearGround();
        }

        // Discovery (10 Hz): which Box/Sphere colliders are near enough to matter. The search box has
        // a 0.6 m margin so something moving at up to ~6 m/s is already tracked when it arrives.
        _proxyTimer -= dt;
        if (_proxyTimer <= 0)
        {
            _proxyTimer = 0.1f;
            _overlap.Clear();
            var o = F(center);
            var rot = floatQ.Identity;
            var size = F(ext * 1.5f + new Vector3(1.2f));
            physics.BoxOverlap(in o, in rot, in size, _overlap, IsWorldCollider, false);
            _tracked.Clear();
            foreach (var c in _overlap)
            {
                if (_tracked.Count >= 32) break;
                if (c is BoxCollider || c is SphereCollider) _tracked.Add(c);
                // Mesh, convex hull, capsule, cylinder and cone colliders are not proxied; the ground ray covers floors.
            }
            if (_proxyKeys.Count > 0)
            {
                List<ICollider> stale = null;
                foreach (var c in _proxyKeys.Keys) if (!_tracked.Contains(c)) (stale ??= new()).Add(c);
                if (stale != null) foreach (var c in stale) _proxyKeys.Remove(c);
            }
        }

        // Tracking (every frame): current poses of the tracked colliders, so the cage reacts the same frame.
        _proxyList.Clear();
        foreach (var c in _tracked)
        {
            if (c.IsRemoved || c is not Component comp || !comp.Enabled || comp.Slot == null || !comp.Slot.IsActive) continue;
            if (!_proxyKeys.TryGetValue(c, out int key)) _proxyKeys[c] = key = _nextProxyKey++;
            if (c is BoxCollider bc)
            {
                var s = bc.Slot;
                var half = V(bc.Size.Value) * Vector3.Abs(V(s.GlobalScale)) * 0.5f;
                var p = V(s.LocalPointToGlobal(bc.Offset.Value));
                var q = s.GlobalRotation;
                _proxyList.Add(ProxyShape.Box(p, new Quaternion(q.x, q.y, q.z, q.w), half, key));
            }
            else if (c is SphereCollider sc)
            {
                var s = sc.Slot;
                var gs = Vector3.Abs(V(s.GlobalScale));
                float r = sc.Radius.Value * MathF.Max(gs.X, MathF.Max(gs.Y, gs.Z));
                _proxyList.Add(ProxyShape.Sphere(V(s.LocalPointToGlobal(sc.Offset.Value)), r, key));
            }
        }
        _rig.Sim.SetProxies(CollectionsMarshal.AsSpan(_proxyList));
    }

    /// <summary>Debug views: hands the debug mesh a copy of the simulation's current state (world thread).</summary>
    public void SubmitDebug(SoftbodyDebugMesh mesh, bool cells, bool colliders) =>
        mesh.Submit(_rig.Sim, cells, colliders, _dbgSearch, _dbgRayOrigin, _dbgSearchCenter, _dbgSearchSize);

    bool IsWorldCollider(ICollider c)
    {
        if (c == null || c.IsRemoved) return false;
        var type = c.ColliderType;
        if (type != ColliderType.Static && type != ColliderType.Active) return false;
        if (c is not Component comp || comp.Slot == null) return false;
        // never collide with our own colliders (on or under the renderer's slot or the component's slot, or the skeleton's)
        return !comp.Slot.IsChildOf(Anchor, true) && !(Owner != null && comp.Slot.IsChildOf(Owner, true)) &&
               !(_skeletonRoot != null && comp.Slot.IsChildOf(_skeletonRoot, true));
    }

    // ------------------------------------------------------------------ brush queries (skinned pose)

    /// <summary>
    /// For the brush (Gate held): makes the paint layer's ray/closest-point queries run on the mesh as it is shown.
    /// Skinned: the shown mesh is the CPU-skinned copy itself (renderer-local, wobble and blendshapes included); it is
    /// copied into the query geometry at most 10 times a second and only while the brush's ray or tip
    /// (<paramref name="localFrom"/>..<paramref name="localTo"/>, renderer-local) passes near the mesh; the BVH is
    /// refitted, not rebuilt. Plain meshes query the rest mesh (as before).
    /// </summary>
    public void PrepareBrushQuery(Vector3 localFrom, Vector3 localTo)
    {
        if (!IsSkinned) return;
        long now = Stopwatch.GetTimestamp();
        if (_queryAt != 0 && (now - _queryAt) < Stopwatch.Frequency / 10) return;
        if (_queryAt != 0)
        {
            var pad = 0.25f * (_queryMax - _queryMin) + new Vector3(0.25f);
            if (!SegmentHitsBox(localFrom, localTo, _queryMin - pad, _queryMax + pad)) return;
        }
        if (!_firstUploadSent) return; // the copy holds the shown (CPU-skinned) mesh from the first upload on
        if (Path == SkinPath.Gpu)
        {
            // GPU path: the copy is in bind space and the renderer skins it, so skin it here for the queries (positions only,
            // no blendshapes: they rarely move painted parts much), with this frame's pose
            _queryPos ??= new Vector3[_n];
            _skin.SkinPositions(_localSkin, MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n)), _queryPos);
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            foreach (var p in _queryPos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            _paint.SetQueryPositions(_queryPos);
            _queryMin = lo; _queryMax = hi;
        }
        else
        {
            var shown = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
            _paint.SetQueryPositions(shown);
            _queryMin = _lastBmin; _queryMax = _lastBmax;
        }
        _queryAt = Stopwatch.GetTimestamp();
        LastBrushPoseMs = Ms(_queryAt - now);
        BrushPoseRefreshes++;
    }

    /// <summary>Where a brush hit is on the rest (bind-space) mesh: dabs are stored there. Plain meshes: the hit point itself.</summary>
    public Vector3 BindPoint(in MeshHit hit) => IsSkinned && _paint.HasQueryPose ? _paint.RestPoint(hit) : hit.Point;

    /// <summary>Renderer-local units per bind-space unit at a brush hit (the skinning's scale there; 1 for plain meshes),
    /// so a dab's radius matches the brush's world radius on a scaled or stretched skeleton.</summary>
    public float BindScale(in MeshHit hit)
    {
        if (!IsSkinned || !_paint.HasQueryPose || _localSkin == null) return 1f;
        var (a, b, c) = _paint.TriangleVertices(hit.Triangle);
        float s = (Lbs.LinearScale(_skin.VertexMatrix(_localSkin, a)) + Lbs.LinearScale(_skin.VertexMatrix(_localSkin, b)) +
                   Lbs.LinearScale(_skin.VertexMatrix(_localSkin, c))) / 3f;
        return float.IsFinite(s) && s > 1e-6f ? s : 1f;
    }

    static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
    {
        var d = b - a;
        float t0 = 0f, t1 = 1f;
        for (int k = 0; k < 3; k++)
        {
            float o = k == 0 ? a.X : k == 1 ? a.Y : a.Z, dd = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            float lo = k == 0 ? min.X : k == 1 ? min.Y : min.Z, hi = k == 0 ? max.X : k == 1 ? max.Y : max.Z;
            if (MathF.Abs(dd) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float ta = (lo - o) / dd, tb = (hi - o) / dd;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ teardown

    /// <summary>Disposes the sim and puts the original mesh back (now, or when the renderer acks our last upload).</summary>
    public void Teardown(bool restore)
    {
        try { _rig.Dispose(); } catch { }
        if (restore && _firstUploadSent && Path == SkinPath.Gpu && Interlocked.Read(ref GpuInPlaceAcks) == 0)
        {
            // 0.9.2: no vertex-only upload was applied by the renderer, so its skinned mesh is exactly as before: send nothing.
            try { Asset.HighPriorityIntegration = _origHighPriority; } catch { }
        }
        else if (restore && _firstUploadSent)
        {
            var am = _assetManager; var provider = Provider;
            // GPU path with the asset still holding the original MeshX: only its vertex streams changed in the renderer, so put
            // the original's back the same way (no new mesh instance, no flicker) unless the renderer already recreated it
            MeshUploadHint? partial = null;
            if (Path == SkinPath.Gpu && ReferenceEquals(_assetData, _original) && !_recreated.Value)
            {
                var h = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
                if (_gpuColorsUploaded && _original.HasColors) h[MeshUploadHint.Flag.Colors] = true;
                partial = h;
            }
            try
            {
                AssetUploads.QueueRestore(Asset, _original, _assetData, Provider.Readable.Value, _origHighPriority,
                                          changed => { if (changed) { try { am?.QueueAssetCreated(provider); } catch { } } }, partial, _recreated);
            }
            catch (Exception ex) { SoftbodyRuntime.Warn($"'{Name}': queueing restore failed: {ex.Message}"); }
        }
        else if (_firstUploadSent)
        {
            try { Asset.HighPriorityIntegration = _origHighPriority; } catch { }
        }
    }

    // ------------------------------------------------------------------ stats

    /// <summary>The 5 s log line; also returns the JSON snapshot for the stats endpoint. Clears the window.</summary>
    public string StatsLine(double seconds, out string json)
    {
        var w = Window;
        long acks = TakeAcks();
        int f = Math.Max(1, w.Frames);
        string line =
            $"'{Name}': {PointCount} points, {_n} verts ({MovingVerts} moving) | per frame: " +
            $"sim {w.SimMs / f:0.000} ms ({(double)w.Substeps / f:0.0} substeps), world queries {w.QueryMs / f:0.000} ms, " +
            (IsSkinned ? $"skin {w.SkinMs / f:0.000} ms ({_skin.BoneCount} bones; {(double)w.RestUpdates / f:0} constraint rest updates per frame, " +
                         $"{_rig.Sim.SingularPoints} singular points, {_rig.Sim.DegenerateTets} flat tets, {_rig.Sim.InvertedTets} inverted), " : "") +
            (Path == SkinPath.Gpu || GpuPartials > 0 ? $"GPU path: {GpuPartials} true partial uploads, {Interlocked.Read(ref GpuInPlaceAcks)} applied in place, " +
                                                       $"{Interlocked.Read(ref GpuRecreatedAcks)} recreated by the renderer{(Path == SkinPath.Cpu ? " (fell back to CPU)" : "")}, " : "") +
            $"deform+normals {w.DeformMs / f:0.000} ms, upload {w.UploadMs / f:0.000} ms, max frame {w.MaxTotalMs:0.00} ms, avg {EmaTotalMs:0.000} ms | " +
            $"{w.Frames} frames / {seconds:0.0} s: {w.Uploads} uploads, {acks} acked (last ack {LastAckLatencyMs:0.0} ms), " +
            $"{w.Busy} skipped (renderer busy), {w.Idle} idle | path: {UploadPath} | " +
            $"ground {(HasGround ? "yes" : "no")}, proxies {ProxyCount}, resets {Resets}{(Resets > 0 ? $" ({LastResetReason})" : "")}, tunes {Tunes}, " +
            $"cage displacement {LastDisplacement * 1000:0.0} mm | paint {_paint.DabCount} dabs: {w.PaintUpdates} updates ({(w.PaintUpdates > 0 ? w.PaintMs / w.PaintUpdates : 0):0.000} ms each, last {LastPaintUpdate}), " +
            $"{w.ColorUploads} colour uploads, {w.Rebuilds} cage rebuilds{(w.Rebuilds > 0 ? $" ({w.RebuildMs / w.Rebuilds:0.0} ms each)" : "")}";

        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("\"points\":").Append(PointCount);
        sb.Append(",\"verts\":").Append(_n);
        sb.Append(",\"movingVerts\":").Append(MovingVerts);
        sb.Append(",\"simMs\":").Append((w.SimMs / f).ToString("0.000", ic));
        sb.Append(",\"queryMs\":").Append((w.QueryMs / f).ToString("0.000", ic));
        sb.Append(",\"deformMs\":").Append((w.DeformMs / f).ToString("0.000", ic));
        sb.Append(",\"uploadMs\":").Append((w.UploadMs / f).ToString("0.000", ic));
        sb.Append(",\"avgFrameMs\":").Append(EmaTotalMs.ToString("0.000", ic));
        sb.Append(",\"maxFrameMs\":").Append(w.MaxTotalMs.ToString("0.00", ic));
        sb.Append(",\"substepsPerFrame\":").Append(((double)w.Substeps / f).ToString("0.00", ic));
        sb.Append(",\"windowSeconds\":").Append(seconds.ToString("0.0", ic));
        sb.Append(",\"frames\":").Append(w.Frames);
        sb.Append(",\"uploads\":").Append(w.Uploads);
        sb.Append(",\"acks\":").Append(acks);
        sb.Append(",\"lastAckMs\":").Append(LastAckLatencyMs.ToString("0.0", ic));
        sb.Append(",\"busySkips\":").Append(w.Busy);
        sb.Append(",\"idleSkips\":").Append(w.Idle);
        sb.Append(",\"uploadPath\":").Append(StatsServer.Json(UploadPath));
        sb.Append(",\"ground\":").Append(HasGround ? "true" : "false");
        sb.Append(",\"proxies\":").Append(ProxyCount);
        sb.Append(",\"resets\":").Append(Resets);
        sb.Append(",\"tunes\":").Append(Tunes);
        sb.Append(",\"solverIterations\":").Append(_rig.Sim.SolverIterations);
        sb.Append(",\"displacementMm\":").Append((LastDisplacement * 1000).ToString("0.0", ic));
        sb.Append(",\"softnessMap\":").Append(StatsServer.Json(_mapDescription));
        sb.Append(",\"paintDabs\":").Append(_paint.DabCount);
        sb.Append(",\"paintUpdates\":").Append(w.PaintUpdates);
        sb.Append(",\"paintMs\":").Append((w.PaintUpdates > 0 ? w.PaintMs / w.PaintUpdates : 0).ToString("0.000", ic));
        sb.Append(",\"lastPaintUpdate\":").Append(StatsServer.Json(LastPaintUpdate.ToString()));
        sb.Append(",\"colorUploads\":").Append(w.ColorUploads);
        sb.Append(",\"cageRebuilds\":").Append(CageRebuilds);
        sb.Append(",\"lastRebuildMs\":").Append(LastRebuildMs.ToString("0.0", ic));
        sb.Append(",\"skinned\":").Append(IsSkinned ? "true" : "false");
        if (IsSkinned)
        {
            sb.Append(",\"bones\":").Append(_skin.BoneCount);
            sb.Append(",\"skinMs\":").Append((w.SkinMs / f).ToString("0.000", ic));
            sb.Append(",\"restUpdatesPerFrame\":").Append(((double)w.RestUpdates / f).ToString("0.0", ic));
            sb.Append(",\"singularPoints\":").Append(_rig.Sim.SingularPoints);
            sb.Append(",\"degenerateTets\":").Append(_rig.Sim.DegenerateTets);
            sb.Append(",\"invertedTets\":").Append(_rig.Sim.InvertedTets);
            sb.Append(",\"skinPath\":").Append(StatsServer.Json(Path == SkinPath.Gpu ? "gpu" : "cpu"));
            sb.Append(",\"gpuPartialUploads\":").Append(GpuPartials);
            sb.Append(",\"gpuInPlaceAcks\":").Append(Interlocked.Read(ref GpuInPlaceAcks));
            sb.Append(",\"gpuRecreatedAcks\":").Append(Interlocked.Read(ref GpuRecreatedAcks));
            sb.Append(",\"brushPoseMs\":").Append(LastBrushPoseMs.ToString("0.000", ic));
            sb.Append(",\"brushPoseRefreshes\":").Append(BrushPoseRefreshes);
        }
        sb.Append(",\"settings\":").Append(StatsServer.Json(Settings.ToString()));
        json = sb.ToString();
        w.Clear();
        return line;
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Elapsed(long ts) => (Stopwatch.GetTimestamp() - ts) / (double)Stopwatch.Frequency;
}
