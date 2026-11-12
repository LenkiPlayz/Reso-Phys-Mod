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
using Softbody.Core.Flesh;

namespace Softbody;

/// <summary>0.13.0: one layer of a SkinnedSoftbody: the hue band it owns (-1: the base layer, the component's own settings
/// and channel) and its settings.</summary>
sealed class FleshLayerSpec
{
    public readonly int Band;
    public readonly FleshSettings Settings;
    /// <summary>The list element a hue layer comes from (its Simulating is read each frame); null for the base layer.</summary>
    public readonly SkinnedSoftbody.HueLayer Source;
    public FleshLayerSpec(int band, FleshSettings settings, SkinnedSoftbody.HueLayer source = null) { Band = band; Settings = settings; Source = source; }
    public string Name => Band < 0 ? "base" : HueBands.Names[Band].ToLowerInvariant();
}

/// <summary>
/// The local simulation behind one running <see cref="SkinnedSoftbody"/> (0.11.0): the tet shell + XPBD flesh of
/// DESIGN-skinned.md on one SkinnedMeshRenderer, shown through the GPU path only (the renderer keeps skinning the mesh,
/// with its blendshapes; this client uploads bind-space positions and normals of the moving vertices in place). It needs
/// the Softbody renderer (the -RendererPath fork, or the SoftbodyRenderer patch), and refuses to start without it.
///
/// Work off the world thread: the tet build (Core.Flesh.FleshRig.Build, a few hundred ms on a big avatar) and every step
/// run on the thread pool. A step started in frame N is collected at the start of frame N+1 (so the wobble is one frame
/// behind the pose, which the renderer applies itself) and uploaded then. Everything that touches the rig from the world
/// thread (colliders, live tuning, the debug view, swapping in a rebuilt rig) happens between steps.
///
/// 0.13.0: several layers, each its own separate rig with its own settings (the base layer, and one per hue layer, see
/// <see cref="FleshLayers"/>), stepped one after another in the same worker task; their outputs are merged into the one
/// upload (<see cref="FleshOutputMerge"/>), and the colliders are searched for once and handed to each.
///
/// 0.14.0, instancing: the uploads go to a private copy of the mesh that this instance hands to its own avatar's
/// StaticMesh (<see cref="PrivateMeshes"/>), so two avatars wearing the same model each run and show their own softbody,
/// and the shared asset is never touched. Without the engine members that needs, one softbody per mesh asset as before.
///
/// Like the lattice softbody: local only, never writes a synced field; the asset's MeshX is never replaced (only the
/// renderer's vertex buffer changes) and the original streams are put back on teardown. Calls come from the world thread
/// with <see cref="SoftbodyRuntime.Gate"/> held (the upload ack and the worker tasks touch only their own fields).
/// </summary>
sealed class FleshInstance : IBrushTarget
{
    public readonly SkinnedMeshRenderer Renderer;
    public readonly Slot Anchor;
    public readonly Slot Owner;
    public readonly StaticMesh Provider;
    public readonly FrooxEngine.Mesh Asset;     // the shared asset (every StaticMesh with this URL has it)
    public readonly string BuildKey;
    /// <summary>What this instance claimed in <see cref="SoftbodyRuntime"/>: its StaticMesh with instancing, else the asset.</summary>
    public readonly object ClaimKey;
    // 0.14.0, instancing: this renderer's own copy of the mesh, handed to its StaticMesh once the renderer has it
    FrooxEngine.Mesh _private;
    volatile bool _privateReady;
    bool _swapped;
    long _privateStartedTs;
    /// <summary>Where the uploads go: the private copy once the provider hands it out, else the shared asset.</summary>
    FrooxEngine.Mesh UploadAsset => _swapped ? _private : Asset;
    public bool Instanced => _private != null;
    public string Name => Anchor.Name;
    public PaintLayer Paint => _paint;
    public ColorProfile Profile { get; }
    Slot IBrushTarget.Anchor => Anchor;

    readonly MeshX _original, _work;
    readonly int _n;
    readonly Vector3[] _restPos, _restNrm;
    readonly Vector4[] _restTan;               // 0.26.3: null when the mesh has no tangents
    bool _tangentsUploaded;                    // 0.26.3: an upload carried tangents (the shared mesh's restore must too)
    readonly int[] _tris;
    readonly bool _origHighPriority;
    // skin
    readonly SkinData _skin;
    readonly Slot[] _boneSlots;
    readonly int _boneListCount;
    readonly Matrix4x4[] _bindPoses, _skinMats, _skinIn, _localSkin;
    readonly Slot _skeletonRoot;
    readonly Vector3[] _heads, _tails;
    readonly float[] _blendW, _blendIn;
    // paint
    readonly PaintLayer _paint;
    readonly SoftnessSource _source;
    readonly bool _invert;
    long _mapPaintVersion = -1, _lastPaintChangeTs;
    bool _colorsDirty;
    string _mapDescription = "";

    /// <summary>0.13.0: one of the separate simulations on this mesh, with its settings, its rig and the work around it.</summary>
    sealed class Layer
    {
        public readonly int Band;                  // the hue band it owns, -1 for the base layer
        public readonly string Name;
        public FleshSettings Settings;
        public FleshRig Rig;
        public Task<(FleshRig rig, string report)> Build;
        public long BuildStartedTs;
        public float[] Soft;                       // the softness map its rig (or the build running) is made from
        public Vector3[] OutPos = Array.Empty<Vector3>(), OutNrm = Array.Empty<Vector3>();
        public Vector4[] OutTan = Array.Empty<Vector4>();   // 0.26.3: empty when the mesh has no tangents
        public bool ResetNext = true, HasOutput;
        public bool Off;                           // its Simulating is 0: not stepped, its region shown at rest
        public readonly Dictionary<Collider, ulong[]> OwnIgnore = new();   // per own bone collider: the points it touches at rest
        public string RigReport = "", RigNote = "building tets", MapDescription = "";
        public double LastBuildMs;
        public int WorldShapes, UserShapes, UserRigShapes, OwnShapes, IgnoredPoints;
        public Layer(FleshLayerSpec spec) { Band = spec.Band; Settings = spec.Settings; Name = spec.Name; }
    }

    readonly Layer[] _layers;
    FleshSettings _settings;                   // the base layer's: the cull distance
    readonly FleshOutputMerge _merge = new();
    readonly BoundingBox _bounds;
    Task _step;
    float _stepDt;
    bool _resultReady, _paused;
    readonly Layer[] _stepLayers;              // the layers the running step steps
    readonly FleshRig[] _stepRigs;
    readonly bool[] _stepReset;
    int _stepCount;
    readonly List<FleshRig> _debugRigs = new();
    int[] _moving = Array.Empty<int>();        // vertices some layer moves
    int[] _shown = Array.Empty<int>();         // vertices currently displaced in the work copy
    bool _forceUpload;
    public string RigReport => _layers.Length == 1 ? _layers[0].RigReport : string.Join("; ", _layers.Select(l => l.Name + ": " + (l.Rig != null ? l.RigReport : l.RigNote ?? l.RigReport)));

    // uploads
    volatile bool _ackPending;
    long _uploadSentAt;
    bool _firstUploadSent;
    public long GpuPartials, GpuInPlaceAcks, GpuRecreatedAcks;
    volatile bool _refused;
    readonly StrongBox<bool> _recreated = new();
    bool _colorsUploaded;
    readonly WorldAssetManager _assetManager;
    public string UploadPath = "none yet";
    public double LastAckLatencyMs;

    // world colliders
    float _proxyTimer;
    User _ownUser;
    int _worldShapes, _userShapes, _userRigShapes, _ownShapes, _ignoredPoints;
    readonly List<ICollider> _overlap = new(), _tracked = new();
    readonly List<Collider> _trackedRig = new();                       // other users' bone colliders near the body
    readonly Dictionary<FrooxEngine.FinalIK.VRIKAvatar, HashSet<Collider>> _rigSets = new();
    readonly HashSet<User> _usersWithRig = new();
    FrooxEngine.FinalIK.VRIKAvatar _ownAvatar;
    readonly List<Collider> _ownRig = new();
    readonly Dictionary<Slot, int> _boneOf = new();
    // 0.12.0: which part of a player each bone collider belongs to (by the VRIK head and hand bones)
    readonly Dictionary<Collider, PlayerPart> _partOf = new();
    // 0.12.0: distance culling
    bool _culled;
    public float CullDistanceNow { get; private set; }
    // 0.11.4: where each collider was last frame, for its velocity (sweep through the substeps, friction)
    Dictionary<object, Vector3> _lastAt = new(), _nowAt = new();   // colliders by reference, users' spheres by their index
    bool _dbgSearch; Vector3 _dbgRayOrigin, _dbgSearchCenter, _dbgSearchSize;

    // brush queries on the shown pose
    long _queryAt;
    Vector3 _queryMin, _queryMax;
    Vector3[] _queryPos;

    // stats
    public long TotalFrames, TotalUploads, Rebuilds;
    public int Warmup = 60;
    public double EmaMainMs, EmaStepMs, LastFrameMs, LastWaitMs, LastBuildMs;
    readonly Window _w = new();
    sealed class Window { public int Frames, Uploads, Idle, Busy; public double MainMs, StepMs, WaitMs, MaxMainMs; public void Clear() { Frames = Uploads = Idle = Busy = 0; MainMs = StepMs = WaitMs = MaxMainMs = 0; } }

    public bool IsBuilding { get { foreach (var l in _layers) if (l.Build != null) return true; return false; } }
    bool AnyRig { get { foreach (var l in _layers) if (l.Rig != null) return true; return false; } }
    public int PointCount { get { int c = 0; foreach (var l in _layers) c += l.Rig?.Surface.SoftCount ?? 0; return c; } }
    public int TetCount { get { int c = 0; foreach (var l in _layers) c += l.Rig?.Shell.TetCount ?? 0; return c; } }
    public int MovingVerts => _moving.Length;
    public int VertexCount => _n;
    public int LayerCount => _layers.Length;
    public FleshSettings Settings => _settings;

    public const double RebuildDelay = 0.3;
    const int MaxVertices = 250_000;

    FleshInstance(SkinnedMeshRenderer renderer, Slot owner, StaticMesh provider, FrooxEngine.Mesh asset, MeshX original, MeshX work, string key,
                  PaintLayer paint, int[] tris, SoftbodyInstance.SkinSetup skin, Vector3[] heads, Vector3[] tails, SoftnessSource source, bool invert,
                  FleshLayerSpec[] layers, object claimKey)
    {
        Renderer = renderer; Anchor = renderer.Slot; Owner = owner; Provider = provider; Asset = asset; _original = original; _work = work; ClaimKey = claimKey;
        BuildKey = key; _n = work.VertexCount; _paint = paint; _tris = tris; _source = source; _invert = invert;
        _layers = layers.Select(l => new Layer(l)).ToArray();
        _settings = _layers[0].Settings;
        _stepLayers = new Layer[_layers.Length]; _stepRigs = new FleshRig[_layers.Length]; _stepReset = new bool[_layers.Length];
        Profile = original.Profile;
        _origHighPriority = asset.HighPriorityIntegration;
        try { _assetManager = renderer.World?.AssetManager; } catch { }
        _restPos = MemoryMarshal.Cast<float3, Vector3>(original.RawPositions.AsSpan(0, _n)).ToArray();
        _restNrm = MemoryMarshal.Cast<float3, Vector3>(original.RawNormals.AsSpan(0, _n)).ToArray();
        // 0.26.3: tangents turn with the normals (a normal map needs both), when the mesh has them
        _restTan = original.HasTangents && work.HasTangents ? MemoryMarshal.Cast<float4, Vector4>(original.RawTangents.AsSpan(0, _n)).ToArray() : null;
        _skin = skin.Skin; _boneSlots = skin.Bones; _boneListCount = skin.BoneListCount; _bindPoses = skin.BindPoses; _skinMats = skin.Matrices;
        _skeletonRoot = skin.SkeletonRoot;
        _skinIn = new Matrix4x4[_skin.MatrixCount]; _localSkin = new Matrix4x4[_skin.MatrixCount];
        for (int b = 0; b < _boneSlots.Length; b++) if (_boneSlots[b] != null) _boneOf.TryAdd(_boneSlots[b], b);
        _heads = heads; _tails = tails;
        _blendW = new float[original.BlendShapeCount]; _blendIn = new float[original.BlendShapeCount];
        // bind-space bounds with room for the wobble (the renderer skins the mesh; these only guard the vertex buffer's own bounds)
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in _restPos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        var pad = (hi - lo) * 0.25f;
        _bounds = BoundingBox.FromPoints(F(lo - pad), F(hi + pad));
    }

    // ------------------------------------------------------------------ creation

    public static SoftbodyInstance.CreateResult TryCreate(SkinnedMeshRenderer renderer, Slot owner, SkinnedSoftbody claimant, FleshLayerSpec[] layers,
                                                          Softbody.Channel channel, bool invert, Softbody.StartColor start, string key,
                                                          out FleshInstance inst, out string reason)
    {
        inst = null; reason = null;
        const SoftbodyInstance.CreateResult Wait = SoftbodyInstance.CreateResult.Wait, Fail = SoftbodyInstance.CreateResult.Fail;
        if (layers == null || layers.Length == 0 || layers[0].Band >= 0) { reason = "no base layer"; return Fail; }
        if (renderer == null || renderer.IsRemoved) { reason = "set Renderer to the avatar's SkinnedMeshRenderer"; return Wait; }
        if (renderer.Slot == null || renderer.Slot.IsRemoved) { reason = "the renderer's slot was removed"; return Wait; }
        var target = renderer.Mesh.Target;
        if (target == null) { reason = "the SkinnedMeshRenderer has no mesh"; return Wait; }
        if (target is not StaticMesh provider) { reason = $"mesh provider is {target.GetType().Name}, not StaticMesh"; return Fail; }
        var asset = provider.Asset;
        if (asset == null || asset.LoadState != AssetLoadState.FullyLoaded || asset.Data == null) { reason = "mesh asset still loading"; return Wait; }
        if (Unsafe.SizeOf<float3>() != Unsafe.SizeOf<Vector3>() || Unsafe.SizeOf<color>() != 16) { reason = "unexpected Elements.Core vector layout"; return Fail; }
        var original = asset.Data;
        int n = original.VertexCount;
        if (n <= 0) { reason = "mesh has no vertices"; return Fail; }
        if (n > MaxVertices) { reason = $"mesh has {n} vertices (cap {MaxVertices})"; return Fail; }

        string diag = SoftbodyInstance.SkinDiagnostics(renderer, provider, asset, original);
        SoftbodyRuntime.LogOnce("skin-diag:" + diag, $"'{SoftbodyRuntime.NameOf(claimant)}' skinned mesh check: {diag}");
        int rendererShapes = 0;
        try { rendererShapes = renderer.BlendShapeWeights.Count; } catch { }
        if (original.BoneCount == 0 || !original.HasBoneBindings) { reason = "this mesh has no bones (SkinnedSoftbody follows bones; use Softbody for unskinned meshes) (" + diag + ")"; return Fail; }
        if (original.BlendShapeCount < rendererShapes) { reason = $"the engine's copy has {original.BlendShapeCount} blendshapes but the renderer uses {rendererShapes}; refusing to drive it ({diag})"; return Fail; }
        if (!original.HasNormals) { reason = "this skinned mesh has no normals; SkinnedSoftbody would have to change its vertex layout"; return Fail; }
        if (!SoftbodyRuntime.UseGpuSkinning(out var noGpu))
        {
            reason = "SkinnedSoftbody needs the Softbody renderer (the -RendererPath fork, or the SoftbodyRenderer patch) to update the skinned mesh in place (" + noGpu + ")";
            return Fail;
        }

        // 0.14.0: with instancing each StaticMesh gets its own copy, so only two softbodies on one StaticMesh conflict
        bool instancing = PrivateMeshes.Unavailable == null;
        if (!instancing) SoftbodyRuntime.LogOnce("instancing", "SkinnedSoftbody instancing is not available (" + PrivateMeshes.Unavailable + "): one softbody per mesh asset");
        object claimKey = instancing ? provider : asset;
        if (!SoftbodyRuntime.TryClaim(claimKey, claimant, out var holder))
        {
            reason = instancing
                ? $"this renderer's mesh (its StaticMesh) is already driven by the {holder?.GetType().Name ?? "Softbody"} on '{SoftbodyRuntime.NameOf(holder)}'"
                : $"this mesh asset is already driven by the {holder?.GetType().Name ?? "Softbody"} on '{SoftbodyRuntime.NameOf(holder)}' " +
                  "(two avatars sharing one mesh share one asset; instancing is not available: " + PrivateMeshes.Unavailable + ")";
            return Wait;
        }
        bool created = false;
        try
        {
            if (!instancing && (AssetUploads.IsBusy(asset, out _) || AssetUploads.HasPendingRestore(asset))) { reason = "waiting for a previous upload on this mesh"; return Wait; }
            var tris = new List<int>();
            foreach (var sub in original.Submeshes)
                if (sub is TriangleSubmesh ts) tris.AddRange(new ArraySegment<int>(ts.RawIndicies, 0, ts.IndicieCount));
            if (tris.Count == 0) { reason = "mesh has no triangle submesh"; return Fail; }
            var trisArr = tris.ToArray();

            // the private copy the uploads are encoded from (the asset keeps the original MeshX)
            var work = new MeshX(original);
            var pos = MemoryMarshal.Cast<float3, Vector3>(work.RawPositions.AsSpan(0, n));

            var source = (SoftnessSource)(int)channel;
            ReadOnlySpan<Vector4> baseCols = work.HasColors ? MemoryMarshal.Cast<color, Vector4>(work.RawColors.AsSpan(0, n)) : ReadOnlySpan<Vector4>.Empty;
            if (start != Softbody.StartColor.MeshColors)
            {
                var fill = new Vector4[n];
                Array.Fill(fill, start == Softbody.StartColor.AllFirm ? new Vector4(0f, 0f, 0f, 1f) : Vector4.One);
                baseCols = fill;
            }
            PaintLayer paint;
            try { paint = new PaintLayer(pos, baseCols, trisArr); }
            catch (Exception ex) { reason = "paint layer failed: " + ex.Message; return Fail; }
            var dabs = claimant.ReadDabs(original.Profile);
            if (dabs.Length > 0) paint.Update(dabs);
            paint.ClearDirty();

            SoftbodyInstance.SkinSetup skin;
            try { skin = SoftbodyInstance.ReadSkinSetup(renderer, original, n); }
            catch (Exception ex) { reason = "reading the mesh's bones failed: " + ex.Message; return Fail; }
            if (!SoftbodyInstance.ComputeSkin(renderer.Slot, skin.Bones, skin.BindPoses, skin.Matrices, null)) { reason = "the skeleton's pose is not finite"; return Wait; }
            BonePoints(skin, out var heads, out var tails);

            inst = new FleshInstance(renderer, owner, provider, asset, original, work, key, paint, trisArr, skin, heads, tails, source, invert, layers, claimKey);
            // the colours the work copy shows (StartAs and paint may differ from the mesh's own), on a mesh that has colours
            if (work.HasColors) { paint.Colors.CopyTo(MemoryMarshal.Cast<color, Vector4>(work.RawColors.AsSpan(0, n))); inst._colorsDirty = true; }
            if (instancing)
            {
                try { inst.StartPrivate(); }
                catch (Exception ex) { reason = "making this renderer's own copy of the mesh failed: " + ex.Message; inst = null; return Fail; }
            }
            inst.StartBuild();
            created = true;
            return SoftbodyInstance.CreateResult.Created;
        }
        finally
        {
            if (!created) SoftbodyRuntime.Release(claimKey, claimant);
        }
    }

    /// <summary>
    /// 0.14.0: makes this renderer's own copy of the mesh, from the work copy (the mesh as it is, with this paint's colours),
    /// uploaded in full. The work copy is its data from now on (the partial uploads only ever send its vertex streams, as
    /// for the shared asset). Until the renderer has it nothing else is sent and the work copy is not written (the upload
    /// counts as in flight), then <see cref="SwapIn"/> hands it to the StaticMesh.
    /// </summary>
    void StartPrivate()
    {
        _ackPending = true;
        _privateStartedTs = Stopwatch.GetTimestamp();
        _private = PrivateMeshes.Create(Renderer.World, _work, _bounds, Provider, _ => _privateReady = true);
        UploadPath = "making this renderer's own copy of the mesh";
    }

    /// <summary>World thread: the provider hands out the private copy from now on, and its renderers pick it up. False when the
    /// provider does not hold the shared asset any more; throws when the copy could not be made to stand in for it.</summary>
    bool SwapIn()
    {
        // 0.26.1: first the copy must count as the shared asset (loaded, its URL, relocated to it), or the provider hands out
        // nothing and drops it on its next asset update (see PrivateMeshes.StandIn)
        PrivateMeshes.StandIn(_private, Asset);
        if (!PrivateMeshes.Swap(Provider, Asset, _private)) { PrivateMeshes.Retire(_private); return false; }
        _swapped = true;
        _ackPending = false;
        _forceUpload = true;
        _colorsDirty = _work.HasColors;
        try { _assetManager?.QueueAssetCreated(Provider); } catch { }
        UploadPath = "own copy of the mesh (instanced), waiting for the first step";
        return true;
    }

    /// <summary>Bind-space bone heads (from the bind poses) and tails (the mean of the child bones' heads, else the parent
    /// chain continued by half a bone), for the shell's "toward the bone" direction.</summary>
    static void BonePoints(SoftbodyInstance.SkinSetup skin, out Vector3[] heads, out Vector3[] tails)
    {
        int nb = skin.BindPoses.Length;
        heads = new Vector3[nb]; tails = new Vector3[nb];
        for (int b = 0; b < nb; b++) heads[b] = Matrix4x4.Invert(skin.BindPoses[b], out var inv) ? inv.Translation : Vector3.Zero;
        var index = new Dictionary<Slot, int>();
        for (int b = 0; b < nb; b++) if (skin.Bones[b] != null && !index.ContainsKey(skin.Bones[b])) index[skin.Bones[b]] = b;
        var parent = new int[nb]; Array.Fill(parent, -1);
        for (int b = 0; b < nb; b++)
        {
            var s = skin.Bones[b]?.Parent;
            for (int guard = 0; s != null && guard < 256; guard++, s = s.Parent)
                if (index.TryGetValue(s, out int p)) { parent[b] = p; break; }
        }
        var sum = new Vector3[nb]; var cnt = new int[nb];
        for (int b = 0; b < nb; b++) if (parent[b] >= 0) { sum[parent[b]] += heads[b]; cnt[parent[b]]++; }
        for (int b = 0; b < nb; b++)
        {
            if (cnt[b] > 0) tails[b] = sum[b] / cnt[b];
            else if (parent[b] >= 0) tails[b] = heads[b] + (heads[b] - heads[parent[b]]) * 0.5f;
            else tails[b] = heads[b];
        }
    }

    // ------------------------------------------------------------------ the rig build (thread pool)

    /// <summary>Starts building tets from the current paint, on the thread pool, for every layer whose softness map changed
    /// (0.13.0: each layer its own). The running rigs keep going meanwhile.</summary>
    void StartBuild()
    {
        var soft = new float[_n];
        bool useMap = _paint.HadBaseColors || _paint.DabCount > 0;
        float[] bright = null, bands = null;
        if (useMap)
        {
            bands = new float[_n * HueBands.Count];
            _paint.ComputeMaps(_source, _invert, soft, bands);
            if (_layers.Length > 1)
            {
                bright = new float[_n];
                if (_source == SoftnessSource.Value && !_invert) Array.Copy(soft, bright, _n);
                else _paint.ComputeMaps(SoftnessSource.Value, false, bright, Span<float>.Empty);
            }
        }
        else Array.Fill(soft, 1f);
        _mapPaintVersion = _paint.Version;
        float[][] maps;
        if (_layers.Length == 1) maps = new[] { soft };
        else
        {
            var lb = new int[_layers.Length];
            for (int i = 0; i < lb.Length; i++) lb[i] = _layers[i].Band;
            maps = FleshLayers.Split(soft, bright ?? Array.Empty<float>(), bands ?? Array.Empty<float>(), lb);
        }
        for (int i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            if (layer.Build == null && layer.Soft != null && FleshLayers.Same(layer.Soft, maps[i])) continue; // unchanged: keeps its tets
            StartLayerBuild(layer, maps[i]);
        }
        _mapDescription = string.Join("; ", _layers.Select(l => (_layers.Length > 1 ? l.Name + " " : "") + l.MapDescription)) + $", {_paint.DabCount} paint dabs";
    }

    void StartLayerBuild(Layer layer, float[] soft)
    {
        layer.Soft = soft;
        layer.MapDescription = DescribeMap(layer, soft);
        var s = layer.Settings.Clone();
        var original = _original; var restPos = _restPos; var restNrm = _restNrm; var tris = _tris; var skin = _skin; var heads = _heads; var tails = _tails;
        layer.BuildStartedTs = Stopwatch.GetTimestamp();
        layer.Build = Task.Run(() =>
        {
            var blend = RegionBlendshapes(original, soft, tris, s.SoftThreshold);
            var input = new FleshInput { Positions = restPos, Normals = restNrm, Triangles = tris, Softness = soft, Skin = skin, Blendshapes = blend, BoneHeads = heads, BoneTails = tails };
            var rig = FleshRig.Build(input, s, out var report);
            return (rig, report);
        });
    }

    /// <summary>Blendshape deltas (frame 0), only for the painted region and the ring around it: positions for the shell
    /// and the normals' shape, and 0.26.3 normal and tangent deltas for the normals and tangents of the moving vertices.</summary>
    static BlendshapeDeltas RegionBlendshapes(MeshX m, float[] soft, int[] tris, float threshold)
    {
        int n = soft.Length;
        var region = new bool[n];
        for (int t = 0; t + 2 < tris.Length; t += 3)
            if (soft[tris[t]] > threshold || soft[tris[t + 1]] > threshold || soft[tris[t + 2]] > threshold)
                region[tris[t]] = region[tris[t + 1]] = region[tris[t + 2]] = true;
        var verts = new List<int>();
        for (int v = 0; v < n; v++) if (region[v]) verts.Add(v);
        var shapes = m.BlendShapes.ToList();
        return BlendshapeDeltas.FromSparse(shapes.Count, b =>
        {
            var bs = shapes[b];
            if (bs.FrameCount == 0) return (null, null, null, null);
            var f = bs[0];
            var p = f.RawPositions;
            var nr = bs.HasNormals ? f.RawNormals : null;
            var tg = bs.HasTangents ? f.RawTangents : null;
            if (p == null && nr == null && tg == null) return (null, null, null, null);
            var vi = new List<int>(); var dp = new List<Vector3>(); var dn = new List<Vector3>(); var dt = new List<Vector3>();
            foreach (int v in verts)
            {
                var d = p != null && v < p.Length ? new Vector3(p[v].x, p[v].y, p[v].z) : Vector3.Zero;
                var dnv = nr != null && v < nr.Length ? new Vector3(nr[v].x, nr[v].y, nr[v].z) : Vector3.Zero;
                var dtv = tg != null && v < tg.Length ? new Vector3(tg[v].x, tg[v].y, tg[v].z) : Vector3.Zero;
                if (d == Vector3.Zero && dnv == Vector3.Zero && dtv == Vector3.Zero) continue;
                vi.Add(v); dp.Add(d); dn.Add(dnv); dt.Add(dtv);
            }
            return (vi.ToArray(), dp.ToArray(), nr != null ? dn.ToArray() : null, tg != null ? dt.ToArray() : null);
        });
    }

    string DescribeMap(Layer layer, float[] soft)
    {
        float lo = 1, hi = 0; int softCount = 0;
        foreach (var v in soft) { lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); if (v > layer.Settings.SoftThreshold) softCount++; }
        string from = layer.Band < 0 ? $"{_source.ToString().ToLowerInvariant()}{(_invert ? " inverted" : "")}" : $"{layer.Name} hue x brightness";
        return $"{from} {lo:0.##}..{hi:0.##}, {softCount} soft vertices";
    }

    /// <summary>World thread: takes a layer's finished build. Returns false when it failed (FailReason set).</summary>
    bool AdoptBuild(Layer layer)
    {
        var task = layer.Build;
        layer.Build = null;
        layer.LastBuildMs = Ms(Stopwatch.GetTimestamp() - layer.BuildStartedTs);
        LastBuildMs = layer.LastBuildMs;
        string which = _layers.Length > 1 ? $"the {layer.Name} layer's " : "the ";
        if (task.IsFaulted) { FailReason = $"building {which}tets failed: " + (task.Exception?.InnerException?.Message ?? "?"); return false; }
        var (rig, report) = task.Result;
        WaitStep();
        layer.RigReport = report;
        layer.OwnIgnore.Clear();
        layer.HasOutput = false;
        layer.Off = false;
        Rebuilds++;
        if (rig == null)
        {
            layer.Rig = null;
            layer.OutPos = layer.OutNrm = Array.Empty<Vector3>();
            layer.OutTan = Array.Empty<Vector4>();
            layer.RigNote = "idle: " + report + (layer.Band < 0
                ? " (paint the parts that should move with the Softbody brush; StartAs AllFirm starts all firm)"
                : $" (paint the parts for this layer in {layer.Name}: the hue picks the layer, the brightness how soft)");
        }
        else
        {
            rig.Tune(layer.Settings);
            layer.Rig = rig;
            layer.OutPos = new Vector3[rig.MovingVertices.Length];
            layer.OutNrm = new Vector3[rig.MovingVertices.Length];
            layer.OutTan = _restTan != null ? new Vector4[rig.MovingVertices.Length] : Array.Empty<Vector4>();
            layer.ResetNext = true;
            layer.RigNote = null;
        }
        RecomputeShown();
        return true;
    }

    /// <summary>The vertices some layer moves now; ones no layer moves any more go back to rest.</summary>
    void RecomputeShown()
    {
        var keep = new HashSet<int>();
        foreach (var l in _layers) if (l.Rig != null) foreach (int v in l.Rig.MovingVertices) keep.Add(v);
        var pos = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
        var nrm = MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
        var tan = WorkTangents();
        foreach (int v in _shown) if (!keep.Contains(v)) { pos[v] = _restPos[v]; nrm[v] = _restNrm[v]; if (tan.Length > 0) tan[v] = _restTan[v]; _forceUpload = true; }
        _moving = keep.ToArray();
        Array.Sort(_moving);
        _shown = _culled ? Array.Empty<int>() : _moving;
    }

    void UnshowAll()
    {
        if (_shown.Length == 0) return;
        var pos = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
        var nrm = MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
        var tan = WorkTangents();
        foreach (int v in _shown) { pos[v] = _restPos[v]; nrm[v] = _restNrm[v]; if (tan.Length > 0) tan[v] = _restTan[v]; }
        _shown = Array.Empty<int>();
        _forceUpload = true;
    }

    /// <summary>0.26.3: the work copy's tangents (empty when the mesh has none).</summary>
    Span<Vector4> WorkTangents() =>
        _restTan != null && _work.HasTangents ? MemoryMarshal.Cast<float4, Vector4>(_work.RawTangents.AsSpan(0, _n)) : Span<Vector4>.Empty;

    // ------------------------------------------------------------------ settings

    /// <summary>Live settings, one per layer (in the order the instance was made with; a different set of layers rebuilds).</summary>
    public void Tune(FleshLayerSpec[] layers)
    {
        WaitStep();
        for (int i = 0; i < _layers.Length && i < layers.Length; i++)
        {
            _layers[i].Settings = layers[i].Settings;
            _layers[i].Rig?.Tune(layers[i].Settings);
        }
        _settings = _layers[0].Settings;
    }

    // ------------------------------------------------------------------ per frame (world thread)

    public enum FrameResult { Ok, Paused, Rebuild, Gone, Failed }
    public string FailReason, RebuildReason;

    void WaitStep()
    {
        var t = _step;
        if (t == null) return;
        _step = null;
        try { t.Wait(); }
        catch (AggregateException ex) { throw new InvalidOperationException("flesh step failed: " + ex.InnerException?.Message, ex.InnerException); }
        for (int i = 0; i < _stepCount; i++) if (ReferenceEquals(_stepLayers[i].Rig, _stepRigs[i])) _stepLayers[i].HasOutput = true;
        _resultReady = true;
    }

    /// <summary>Simulating (0..1) this frame, handed in each frame by the component (it may be driven: changing it
    /// costs nothing, unlike the other settings, which wait for the running step): the component's, and per layer the
    /// component's times the hue layer's own (0.18.0).</summary>
    float _simulating = 1f;
    float[] _gain = Array.Empty<float>();
    bool _off;

    public FrameResult Frame(World world, float dt, SoftbodyDebugMesh debug, bool debugTets, bool debugColliders, float simulating = 1f, float[] layerGains = null)
    {
        _simulating = float.IsFinite(simulating) ? Math.Clamp(simulating, 0f, 1f) : 1f;
        if (_gain.Length != _layers.Length) _gain = new float[_layers.Length];
        for (int i = 0; i < _layers.Length; i++)
        {
            float g = layerGains != null && i < layerGains.Length ? layerGains[i] : _simulating;
            _gain[i] = float.IsFinite(g) ? Math.Clamp(g, 0f, 1f) : _simulating;
        }
        if (Anchor.IsRemoved || Renderer.IsRemoved || Provider.IsRemoved) { WaitStep(); return FrameResult.Gone; }
        if (!ReferenceEquals(Renderer.Mesh.Target, Provider) || !ReferenceEquals(Provider.Asset, _swapped ? _private : Asset)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        if (Asset.LoadState != AssetLoadState.FullyLoaded || !ReferenceEquals(Asset.Data, _original)) { RebuildReason = "the mesh asset was reloaded"; return FrameResult.Rebuild; }
        if (_private != null)
        {
            // 0.14.0: hand the own copy to the StaticMesh once the renderer has it; a provider that let go of it rebuilds
            if (!_swapped && _privateReady)
            {
                bool swapped;
                try { swapped = SwapIn(); }
                catch (Exception ex)
                {
                    FailReason = "stopped: this renderer's own copy of the mesh could not stand in for the shared one (" + ex.Message + "); the avatar's mesh was left untouched";
                    return FrameResult.Failed;
                }
                if (!swapped) { RebuildReason = "the renderer's mesh changed before its own copy was ready"; return FrameResult.Rebuild; }
            }
            if (!_swapped && !_privateReady && Elapsed(_privateStartedTs) > 30)
            {
                FailReason = "stopped: the renderer did not take this renderer's own copy of the mesh within 30 s (instancing); the avatar's mesh was left untouched";
                return FrameResult.Failed;
            }
            if (_swapped && !ReferenceEquals(PrivateMeshes.Held(Provider), _private)) { RebuildReason = "the renderer's mesh changed"; return FrameResult.Rebuild; }
        }
        if (BonesChanged()) { RebuildReason = "the renderer's bones changed"; return FrameResult.Rebuild; }
        if (_refused) { FailReason ??= "stopped: the renderer did not update this skinned mesh in place (the Softbody renderer is not running?); the avatar's mesh was left untouched"; return FrameResult.Failed; }
        if (!Anchor.IsActive || !Renderer.Enabled) { WaitStep(); _paused = true; return FrameResult.Paused; }

        long t0 = Stopwatch.GetTimestamp();
        // collect last frame's step
        WaitStep();
        long tw = Stopwatch.GetTimestamp();
        LastWaitMs = Ms(tw - t0);
        foreach (var l in _layers)
            if (l.Build != null && l.Build.IsCompleted && !AdoptBuild(l)) return FrameResult.Failed;
        if (!AnyRig) { _w.Frames++; TotalFrames++; UploadIfNeeded(); return FrameResult.Ok; }
        if (Culled(world)) { _w.Frames++; TotalFrames++; UploadIfNeeded(); return FrameResult.Ok; }

        // this frame's pose
        if (!SoftbodyInstance.ComputeSkin(Anchor, _boneSlots, _bindPoses, _skinMats, null)) { _paused = true; return FrameResult.Paused; }
        ReadBlendWeights();
        if (_paused) { _paused = false; foreach (var l in _layers) l.ResetNext = true; }

        GatherColliders(world, dt);
        if (debug != null)
        {
            _debugRigs.Clear();
            foreach (var l in _layers) if (l.Rig != null) _debugRigs.Add(l.Rig);
            debug.SubmitFlesh(_debugRigs, debugTets, debugColliders, _dbgSearch, _dbgRayOrigin, _dbgSearchCenter, _dbgSearchSize, false, default);
        }

        // show the result of the last step
        UploadIfNeeded();

        // start this frame's step
        Array.Copy(_skinMats, _skinIn, _skinMats.Length);
        Array.Copy(_blendW, _blendIn, _blendW.Length);
        _stepDt = dt;
        int count = 0;
        double stepMs = 0;
        for (int i = 0; i < _layers.Length; i++)
        {
            var l = _layers[i];
            if (l.Rig == null) continue;
            // a layer whose Simulating is 0 is not stepped: its region shows at rest (once), and starts again from rest
            if (!(_gain[i] > 0f))
            {
                if (!l.Off) { l.Off = true; RestOutput(l); }
                continue;
            }
            if (l.Off) { l.Off = false; l.ResetNext = true; }
            _stepLayers[count] = l; _stepRigs[count] = l.Rig; _stepReset[count] = l.ResetNext;
            l.ResetNext = false;
            l.Rig.OutputGain = _gain[i];
            stepMs += l.Rig.StepMs;
            count++;
        }
        _stepCount = count;
        var layers = _stepLayers; var rigs = _stepRigs; var resets = _stepReset;
        var restPos = _restPos; var restNrm = _restNrm; var restTan = _restTan; var skinIn = _skinIn; var blendIn = _blendIn; float stepDt = dt;
        _step = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                var rig = rigs[i];
                if (resets[i]) rig.Reset(skinIn, blendIn); else rig.Step(stepDt, skinIn, blendIn);
                // 0.26.3: measured on the blendshaped shape, tangents turned with the normals
                rig.FillOutput(restPos, restNrm, restTan, layers[i].OutPos, layers[i].OutNrm, layers[i].OutTan);
            }
        });

        double main = Ms(Stopwatch.GetTimestamp() - t0);
        LastFrameMs = main;
        _w.Frames++; TotalFrames++;
        _w.MainMs += main; _w.WaitMs += LastWaitMs; _w.MaxMainMs = Math.Max(_w.MaxMainMs, main); _w.StepMs += stepMs;
        if (TotalFrames > Warmup)
        {
            EmaMainMs = EmaMainMs <= 0 ? main : EmaMainMs + 0.05 * (main - EmaMainMs);
            EmaStepMs = EmaStepMs <= 0 ? stepMs : EmaStepMs + 0.05 * (stepMs - EmaStepMs);
        }
        return FrameResult.Ok;
    }

    /// <summary>
    /// 0.12.0, distance culling: past CullDistance from the local user's head (measured to the skeleton root, which moves with
    /// the body), this client stops simulating and colliding this softbody, and shows its skin at rest. It resumes inside
    /// 90 % of the distance (so it does not flicker at the edge), starting again at rest. Each client decides for itself.
    /// </summary>
    bool Culled(World world)
    {
        // Simulating 0: off, the same way (the skin at rest, nothing stepped), until it goes above 0 again
        float most = 0f;
        foreach (var g in _gain) most = MathF.Max(most, g);
        _off = !(most > 0f);
        if (_off)
        {
            if (!_culled) { _culled = true; _resultReady = false; UnshowAll(); }
            return true;
        }
        float cull = _settings.CullDistance;
        if (!(cull > 0f)) { if (_culled) Uncull(); return false; }
        float3? head = null;
        try { head = world.LocalUser?.Root?.HeadPosition; } catch { }
        if (head == null) { if (_culled) Uncull(); return false; }
        var at = (_skeletonRoot != null && !_skeletonRoot.IsRemoved ? _skeletonRoot : Anchor).GlobalPosition;
        float d = Vector3.Distance(V(head.Value), V(at));
        CullDistanceNow = d;
        bool far = _culled ? d > 0.9f * cull : d > cull;
        if (far)
        {
            if (!_culled)
            {
                _culled = true;
                _resultReady = false;
                UnshowAll(); // the skin goes back to rest (uploaded once)
            }
            return true;
        }
        if (_culled) Uncull();
        return false;
    }

    /// <summary>A layer turned off shows its region at rest: its output is the rest mesh (merged like any layer's).</summary>
    void RestOutput(Layer l)
    {
        var mv = l.Rig.MovingVertices;
        for (int k = 0; k < mv.Length && k < l.OutPos.Length; k++) { l.OutPos[k] = _restPos[mv[k]]; l.OutNrm[k] = _restNrm[mv[k]]; }
        if (_restTan != null) for (int k = 0; k < mv.Length && k < l.OutTan.Length; k++) l.OutTan[k] = _restTan[mv[k]];
        l.HasOutput = true;
        _forceUpload = true;
    }

    void Uncull()
    {
        _culled = false;
        foreach (var l in _layers) l.ResetNext = true;
        _shown = _moving;
    }

    bool BonesChanged()
    {
        var list = Renderer.Bones;
        if (list.Count != _boneListCount) return true;
        for (int b = 0; b < _boneSlots.Length; b++)
        {
            var s = b < list.Count ? list[b] : null;
            if (s != null && s.IsRemoved) s = null;
            if (!ReferenceEquals(s, _boneSlots[b])) return true;
        }
        return false;
    }

    void ReadBlendWeights()
    {
        if (_blendW.Length == 0) return;
        var list = Renderer.BlendShapeWeights;
        int count = Math.Min(list.Count, _blendW.Length);
        for (int i = 0; i < _blendW.Length; i++)
        {
            float w = i < count ? list[i] : 0f;
            _blendW[i] = float.IsFinite(w) ? w : 0f;
        }
    }

    /// <summary>Writes the last step's positions and normals into the work copy and sends them, when they changed and the
    /// renderer has taken the previous upload.</summary>
    void UploadIfNeeded()
    {
        if (_ackPending) { _w.Busy++; return; }
        bool changed = _forceUpload || _colorsDirty || !_firstUploadSent;
        var pos = MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n));
        var nrm = MemoryMarshal.Cast<float3, Vector3>(_work.RawNormals.AsSpan(0, _n));
        var tan = WorkTangents();   // 0.26.3: empty without tangents
        if (_resultReady)
        {
            _merge.Begin(_n);
            float edge = float.MaxValue;
            bool any = false;
            foreach (var l in _layers)
            {
                if (l.Rig == null || !l.HasOutput) continue;
                _merge.Add(l.Rig.MovingVertices, l.OutPos, l.OutNrm, l.OutTan, _restPos, _restNrm, _restTan);
                edge = MathF.Min(edge, l.Rig.Surface.MeanEdge);
                any = true;
            }
            if (any)
            {
                _merge.End(_restNrm, _restTan);
                float eps = 1e-3f * edge, eps2 = eps * eps;
                foreach (int v in _merge.Vertices)
                {
                    var p = _merge.Position(v); var nn = _merge.Normal(v);
                    // 0.26.3: a change of shading alone counts too (a blendshape turning a held dent's normals)
                    if (!changed && (Vector3.DistanceSquared(pos[v], p) > eps2 || Vector3.DistanceSquared(nrm[v], nn) > 1e-6f)) changed = true;
                    pos[v] = p; nrm[v] = nn;
                    if (tan.Length > 0) tan[v] = _merge.Tangent(v);
                }
            }
            _resultReady = false;
        }
        if (!changed) { _w.Idle++; return; }

        var hint = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
        bool tangents = tan.Length > 0;
        if (tangents) hint[MeshUploadHint.Flag.Tangents] = true;
        bool colors = _colorsDirty && _work.HasColors;
        if (colors) hint[MeshUploadHint.Flag.Colors] = true;
        var asset = UploadAsset;
        var why = GpuUpload.Check(asset, _work, hint);
        if (why != null) { FailReason = "stopped: a vertex-only upload is not possible on this mesh (" + why + "); nothing was sent"; _refused = true; return; }
        if (!_firstUploadSent) asset.HighPriorityIntegration = true;
        var b = _bounds;
        _ackPending = true;
        _uploadSentAt = Stopwatch.GetTimestamp();
        string res;
        try { res = AssetUploads.TryUploadPartial(asset, _work, hint, b, OnAck); }
        catch { _ackPending = false; throw; }
        if (res != null)
        {
            _ackPending = false;
            if (res == AssetUploads.Busy) { _w.Busy++; return; }
            FailReason = "stopped: the vertex-only upload could not be sent (" + res + ")";
            _refused = true;
            return;
        }
        GpuPartials++; TotalUploads++; _w.Uploads++;
        _firstUploadSent = true;
        _forceUpload = false;
        if (colors) { _colorsDirty = false; _colorsUploaded = true; }
        if (tangents) _tangentsUploaded = true;
        UploadPath = (_swapped ? "own copy of the mesh (instanced), " : "") + "GPU-skinned, true partial: Positions|Normals" +
                     (tangents ? "|Tangents" : "") + (colors ? "|Colors" : "") + ", skin data kept";
    }

    void OnAck(bool instanceChanged)
    {
        // a vertex-only upload must come back in place; a new mesh instance means the renderer recreated it
        if (instanceChanged)
        {
            Interlocked.Increment(ref GpuRecreatedAcks);
            _recreated.Value = true;
            _refused = true;
            try { _assetManager?.QueueAssetCreated(Provider); } catch { }
        }
        else if (Interlocked.Increment(ref GpuInPlaceAcks) == 1) SoftbodyRuntime.NoteGpuConfirmed();
        LastAckLatencyMs = Ms(Stopwatch.GetTimestamp() - _uploadSentAt);
        _ackPending = false;
    }

    // ------------------------------------------------------------------ paint

    public PaintLayer.UpdateResult UpdatePaint(ReadOnlySpan<Dab> dabs)
    {
        var r = _paint.Update(dabs);
        if (r.Kind != PaintLayer.UpdateKind.None)
        {
            _lastPaintChangeTs = Stopwatch.GetTimestamp();
            if (_work.HasColors && _paint.IsDirty && !_ackPending)
            {
                int lo = _paint.DirtyMin, hi = _paint.DirtyMax;
                _paint.Colors.Slice(lo, hi - lo + 1).CopyTo(MemoryMarshal.Cast<color, Vector4>(_work.RawColors.AsSpan(0, _n)).Slice(lo, hi - lo + 1));
                _paint.ClearDirty();
                _colorsDirty = true;
            }
        }
        return r;
    }

    /// <summary>Paint colours that could not be written while an upload was in flight (call each frame).</summary>
    public void FlushPaintColors()
    {
        if (!_paint.IsDirty || _ackPending) return;
        if (!_work.HasColors)
        {
            _paint.ClearDirty();
            SoftbodyRuntime.LogOnce("flesh-nocolors:" + Name, $"'{Name}': this skinned mesh has no vertex colours, so the paint is not shown on it (it still sets the softness)");
            return;
        }
        int lo = _paint.DirtyMin, hi = _paint.DirtyMax;
        _paint.Colors.Slice(lo, hi - lo + 1).CopyTo(MemoryMarshal.Cast<color, Vector4>(_work.RawColors.AsSpan(0, _n)).Slice(lo, hi - lo + 1));
        _paint.ClearDirty();
        _colorsDirty = true;
    }

    /// <summary>The paint changed since the tets were built (or are being built), and has been still for <see cref="RebuildDelay"/>.</summary>
    public bool WantsRebuild => !IsBuilding && _paint.Version != _mapPaintVersion &&
                                (_lastPaintChangeTs == 0 || Elapsed(_lastPaintChangeTs) >= RebuildDelay);

    public void RebuildFromPaint() => StartBuild();

    // ------------------------------------------------------------------ colliders

    /// <summary>
    /// What the flesh collides with: only solid things.
    /// <list type="bullet">
    /// <item>World colliders with CharacterCollider on (what users stand on and bump into; floors included), Static or
    /// Active, not part of this avatar.</item>
    /// <item>Other users' bone colliders (0.11.2): the capsules, head sphere and hand/foot boxes VRIKAvatar puts on every
    /// major bone, which the engine only enables while users are close (far away there is only the user's single big
    /// collider, which is never used). They are recognised exactly, through the avatar's own list of them. Users without
    /// them fall back to the engine's dynamic-bone spheres.</item>
    /// <item>This avatar's own bone colliders (self-collision, 0.11.2), enabled or not: each one ignores the skin points it
    /// already touches in the mesh's bind pose (computed once per tet build, a bit per point), so the torso's own capsules
    /// never push its flesh out, while an arm that is clear of the chest at rest presses into it.</item>
    /// </list>
    /// Found with a box around the body: the world's broadphase 10 times a second; shapes are rebuilt every frame from their
    /// slots, so moving ones push the same frame. The solver then culls them per point with bounding boxes on its worker.
    /// </summary>
    void GatherColliders(World world, float dt)
    {
        _dbgSearch = false;
        _worldShapes = _userShapes = _userRigShapes = _ownShapes = 0;
        _ignoredPoints = 0;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (var l in _layers)
        {
            l.WorldShapes = l.UserShapes = l.UserRigShapes = l.OwnShapes = l.IgnoredPoints = 0;
            if (l.Rig == null) continue;
            l.Rig.Solver.Colliders.Clear();
            if (!l.Settings.Collide) continue;
            l.Rig.TargetBounds(out var a, out var b);
            lo = Vector3.Min(lo, a); hi = Vector3.Max(hi, b);
            any = true;
        }
        if (!any) return;
        if (!float.IsFinite(lo.X + hi.X + lo.Y + hi.Y + lo.Z + hi.Z)) return;
        var center = 0.5f * (lo + hi);
        // room for anything moving at up to ~5 m/s between two searches
        var size = (hi - lo) + new Vector3(1.0f);
        _dbgSearch = true; _dbgRayOrigin = center; _dbgSearchCenter = center; _dbgSearchSize = size;

        _proxyTimer -= dt;
        if (_proxyTimer <= 0f)
        {
            _proxyTimer = 0.1f;
            try { _ownUser = Anchor.ActiveUser; } catch { _ownUser = null; }
            RefreshOwnRig();
            _overlap.Clear();
            var o = F(center); var rot = floatQ.Identity; var fs = F(size);
            world.Physics.BoxOverlap(in o, in rot, in fs, _overlap, IsCandidate, false);
            _tracked.Clear(); _trackedRig.Clear(); _rigSets.Clear(); _usersWithRig.Clear();
            if (_ownAvatar != null && _ownRig.Count > 0 && _ownUser != null) _usersWithRig.Add(_ownUser);
            foreach (var c in _overlap)
            {
                if (_tracked.Count + _trackedRig.Count >= 96) break;
                if (c.CharacterCollider) { if (!_tracked.Contains(c)) _tracked.Add(c); }
                else if (c is Collider col && IsUsersBoneCollider(col) && !_trackedRig.Contains(col)) _trackedRig.Add(col);
            }
        }
        var half = 0.5f * size;
        for (int li = 0; li < _layers.Length; li++)
        {
            var l = _layers[li];
            if (l.Rig == null || !l.Settings.Collide) continue;
            var list = l.Rig.Solver.Colliders;
            var st = l.Settings;
            foreach (var c in _tracked)
                if (c is Collider col && Usable(col) && c.CharacterCollider && AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), 0, null, list))
                { l.WorldShapes++; Track(col, list, dt); }
            if (st.CollideOthers)
                foreach (var col in _trackedRig)
                    if (Usable(col) && Allowed(_partOf.GetValueOrDefault(col, PlayerPart.Body), st.OthersHead, st.OthersBody, st.OthersHands) &&
                        AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), 1, null, list))
                    { l.UserRigShapes++; Track(col, list, dt); }
            // the avatar's own bone colliders, enabled or not (they are only for this simulation)
            foreach (var col in _ownRig)
            {
                if (col.IsRemoved || col.Slot == null) continue;
                if (!st.CollideSelf || !Allowed(PartOf(col, _ownAvatar), st.SelfHead, st.SelfBody, st.SelfHands)) continue;
                var ign = OwnIgnore(l, col);
                if (ign != null) foreach (var w in ign) l.IgnoredPoints += System.Numerics.BitOperations.PopCount(w);
                if (AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), 2, ign, list)) { l.OwnShapes++; Track(col, list, dt); }
            }
            int first = list.Count;
            l.UserShapes = UserColliderReader.Collect(world, _ownUser, _usersWithRig, center - half, center + half, list, st);
            // users' spheres: matched to last frame's by their order (stable while nobody joins or leaves), per layer (each
            // layer's toggles can leave out different ones)
            for (int k = first; k < list.Count; k++) Track((li, k - first), list, dt, k);
            _worldShapes += l.WorldShapes; _userShapes += l.UserShapes; _userRigShapes += l.UserRigShapes; _ownShapes += l.OwnShapes;
            _ignoredPoints += l.IgnoredPoints;
        }
        (_lastAt, _nowAt) = (_nowAt, _lastAt);
        _nowAt.Clear();
    }

    /// <summary>Gives the last added shape (or shape <paramref name="index"/>) its velocity from where it was last frame. A jump
    /// over half a metre (a teleport, a respawn) counts as standing still.</summary>
    void Track(object key, List<FleshCollider> list, float dt, int index = -1)
    {
        if (index < 0) index = list.Count - 1;
        var sh = list[index];
        var at = sh.Kind == FleshColliderKind.Mesh ? sh.M.Translation : sh.A;
        _nowAt[key] = at;
        if (dt > 0f && _lastAt.TryGetValue(key, out var was) && Vector3.DistanceSquared(at, was) < 0.25f)
        {
            sh.Velocity = (at - was) / dt;
            list[index] = sh;
        }
    }

    static bool Usable(Collider col) => !col.IsRemoved && col.Enabled && col.Slot != null && col.Slot.IsActive;

    /// <summary>For the broadphase: Static or Active, not this avatar's, and either solid (CharacterCollider) or part of a
    /// user (a bone collider is then checked exactly).</summary>
    bool IsCandidate(ICollider c)
    {
        if (c == null || c.IsRemoved) return false;
        var type = c.ColliderType;
        if (type != ColliderType.Static && type != ColliderType.Active) return false;
        if (c is not Component comp || comp.Slot == null) return false;
        if (comp.Slot.IsChildOf(Anchor, true) || (Owner != null && comp.Slot.IsChildOf(Owner, true)) ||
            (_skeletonRoot != null && comp.Slot.IsChildOf(_skeletonRoot, true))) return false;
        if (_ownAvatar != null && comp.Slot.IsChildOf(_ownAvatar.Slot, true)) return false;
        return c.CharacterCollider || comp.Slot.ActiveUserRoot != null;
    }

    /// <summary>One of the bone colliders a VRIKAvatar keeps for its avatar (not its single simplified collider).</summary>
    bool IsUsersBoneCollider(Collider col)
    {
        var av = col.Slot.GetComponentInParents<FrooxEngine.FinalIK.VRIKAvatar>();
        if (av == null) return false;
        if (!_rigSets.TryGetValue(av, out var set))
        {
            var list = new List<Collider>();
            AvatarRigColliders.Collect(av, list);
            set = new HashSet<Collider>(list);
            _rigSets[av] = set;
            if (set.Count > 0) { try { var u = av.Slot.ActiveUser; if (u != null) _usersWithRig.Add(u); } catch { } }
        }
        if (!set.Contains(col)) return false;
        PartOf(col, av);
        return true;
    }

    /// <summary>Which part of a player a bone collider is: Head under the VRIK head bone, Hands under a hand bone (fingers
    /// included), Body for everything else (torso, arms, legs, feet). Cached per collider.</summary>
    PlayerPart PartOf(Collider col, FrooxEngine.FinalIK.VRIKAvatar av)
    {
        if (_partOf.TryGetValue(col, out var part)) return part;
        part = PlayerPart.Body;
        try
        {
            var refs = av?.IK.Target?.Solver?.BoneReferences;
            Slot head = refs?.head.Target, left = refs?.leftHand.Target, right = refs?.rightHand.Target;
            for (var s = col.Slot; s != null; s = s.Parent)
            {
                if (head != null && ReferenceEquals(s, head)) { part = PlayerPart.Head; break; }
                if ((left != null && ReferenceEquals(s, left)) || (right != null && ReferenceEquals(s, right))) { part = PlayerPart.Hands; break; }
                if (av != null && ReferenceEquals(s, av.Slot)) break;
            }
        }
        catch { part = PlayerPart.Body; }
        if (_partOf.Count > 1024) _partOf.Clear();
        _partOf[col] = part;
        return part;
    }

    internal static bool Allowed(PlayerPart part, bool head, bool body, bool hands) =>
        part switch { PlayerPart.Head => head, PlayerPart.Hands => hands, _ => body };

    /// <summary>This avatar's VRIKAvatar and its bone colliders (re-read when their count changes).</summary>
    void RefreshOwnRig()
    {
        try
        {
            if (_ownAvatar == null || _ownAvatar.IsRemoved)
            {
                _ownAvatar = Anchor.GetComponentInParents<FrooxEngine.FinalIK.VRIKAvatar>();
                if (_ownAvatar == null && _skeletonRoot != null) _ownAvatar = _skeletonRoot.GetComponentInParents<FrooxEngine.FinalIK.VRIKAvatar>();
                if (_ownAvatar == null) _ownAvatar = Anchor.ActiveUserRoot?.GetRegisteredComponent<FrooxEngine.FinalIK.VRIKAvatar>();
                _ownRig.Clear();
            }
            if (_ownAvatar == null) { _ownRig.Clear(); return; }
            var list = new List<Collider>();
            AvatarRigColliders.Collect(_ownAvatar, list);
            if (list.Count != _ownRig.Count) { _ownRig.Clear(); _ownRig.AddRange(list); }
        }
        catch { _ownAvatar = null; _ownRig.Clear(); }
    }

    /// <summary>
    /// The skin points this avatar's own collider touches at rest, cached until the tets are rebuilt. The collider is taken
    /// into the mesh's bind space through the bone it sits on (its slot relative to that bone, then the bone's inverse bind
    /// pose), so the answer does not depend on the pose the avatar is in now. A collider on no bone uses the current pose.
    /// </summary>
    ulong[] OwnIgnore(Layer layer, Collider col)
    {
        if (layer.OwnIgnore.TryGetValue(col, out var bits)) return bits;
        bits = null;
        try
        {
            int bone = -1; Slot boneSlot = null;
            for (var sl = col.Slot; sl != null && bone < 0; sl = sl.Parent)
                if (_boneOf.TryGetValue(sl, out int b)) { bone = b; boneSlot = sl; }
            var list = new List<FleshCollider>(1);
            if (bone >= 0 && Matrix4x4.Invert(SkinMatrices.ToNumerics(boneSlot.LocalToGlobal), out var boneInv) && Matrix4x4.Invert(_bindPoses[bone], out var bindInv))
            {
                var toBind = SkinMatrices.ToNumerics(col.Slot.LocalToGlobal) * boneInv * bindInv;
                if (AddShape(col, toBind, 2, null, list)) bits = layer.Rig.TouchingAtRest(list[0]);
            }
            else if (AddShape(col, SkinMatrices.ToNumerics(col.Slot.LocalToGlobal), 2, null, list)) bits = layer.Rig.TouchingAtTargets(list[0]);
        }
        catch { bits = null; }
        layer.OwnIgnore[col] = bits;
        return bits;
    }

    /// <summary>The collider's shape, its local space taken through <paramref name="m"/> (its slot's LocalToGlobal for world
    /// space). False when it has no shape (yet).</summary>
    internal static bool AddShape(Collider col, in Matrix4x4 m, byte source, ulong[] ignore, List<FleshCollider> into)
    {
        var off = V(col.Offset.Value);
        var center = Vector3.Transform(off, m);
        var ax = Vector3.TransformNormal(Vector3.UnitX, m); var ay = Vector3.TransformNormal(Vector3.UnitY, m); var az = Vector3.TransformNormal(Vector3.UnitZ, m);
        float sx = ax.Length(), sy = ay.Length(), sz = az.Length();
        if (!(sy > 1e-12f)) return false;
        var axis = ay / sy;
        float side = 0.5f * (sx + sz);
        FleshCollider shape;
        switch (col)
        {
            case BoxCollider bc:
            {
                var rot = Matrix4x4.Decompose(m, out _, out var q, out _) ? q : Quaternion.Identity;
                shape = FleshCollider.Box(center, rot, V(bc.Size.Value) * new Vector3(sx, sy, sz) * 0.5f);
                break;
            }
            case SphereCollider sp:
                shape = FleshCollider.Sphere(center, sp.Radius.Value * MathF.Max(sx, MathF.Max(sy, sz)));
                break;
            case CapsuleCollider cc:
            {
                float r = cc.Radius.Value * side, h = MathF.Max(0f, cc.Height.Value - 2f * cc.Radius.Value) * 0.5f * sy;
                shape = FleshCollider.Capsule(center - axis * h, center + axis * h, r);
                break;
            }
            case CylinderCollider cy:
            {
                float h = cy.Height.Value * 0.5f * sy;
                shape = FleshCollider.Cylinder(center - axis * h, center + axis * h, cy.Radius.Value * side);
                break;
            }
            case ConeCollider cn:
            {
                float h = cn.Height.Value * 0.5f * sy;
                shape = FleshCollider.Cone(center - axis * h, center + axis * h, cn.Radius.Value * side);
                break;
            }
            case TriangleCollider tc:
                shape = FleshCollider.Triangle(Vector3.Transform(V(tc.A.Value) + off, m), Vector3.Transform(V(tc.B.Value) + off, m),
                                               Vector3.Transform(V(tc.C.Value) + off, m), twoSided: true);
                break;
            case MeshCollider mc:
                if (!MeshShape(mc.Mesh.Asset, off, m, mc.Sidedness.Value == MeshColliderSidedness.DualSided, out shape)) return false;
                break;
            case ConvexHullCollider ch:
                // the hull of a mesh is approximated by the mesh itself (outward faces): exact for convex meshes
                if (!MeshShape(ch.Mesh.Asset, off, m, false, out shape)) return false;
                break;
            default:
                return false;
        }
        shape.Source = source;
        shape.Ignore = ignore;
        into.Add(shape);
        return true;
    }

    static bool MeshShape(FrooxEngine.Mesh asset, Vector3 off, in Matrix4x4 m, bool twoSided, out FleshCollider shape)
    {
        shape = default;
        var data = asset?.Data;
        if (data == null || data.VertexCount == 0) return false;
        var bvh = MeshBvhCache.Get(data);
        if (bvh == null || bvh.TriangleCount == 0) return false;
        shape = FleshCollider.MeshShape(bvh, Matrix4x4.CreateTranslation(off) * m, twoSided);
        return true;
    }

    // ------------------------------------------------------------------ brush (IBrushTarget)

    public void PrepareBrushQuery(Vector3 localFrom, Vector3 localTo)
    {
        long now = Stopwatch.GetTimestamp();
        if (_queryAt != 0 && (now - _queryAt) < Stopwatch.Frequency / 10) return;
        if (_queryAt != 0)
        {
            var pad = 0.25f * (_queryMax - _queryMin) + new Vector3(0.25f);
            if (!SegmentHitsBox(localFrom, localTo, _queryMin - pad, _queryMax + pad)) return;
        }
        if (!SoftbodyInstance.ComputeSkin(Anchor, _boneSlots, _bindPoses, _localSkin, SkinMatrices.ToNumerics(Anchor.GlobalToLocal))) return;
        _queryPos ??= new Vector3[_n];
        _skin.SkinPositions(_localSkin, MemoryMarshal.Cast<float3, Vector3>(_work.RawPositions.AsSpan(0, _n)), _queryPos);
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var p in _queryPos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        _paint.SetQueryPositions(_queryPos);
        _queryMin = lo; _queryMax = hi;
        _queryAt = Stopwatch.GetTimestamp();
    }

    public Vector3 BindPoint(in MeshHit hit) => _paint.HasQueryPose ? _paint.RestPoint(hit) : hit.Point;

    public float BindScale(in MeshHit hit)
    {
        if (!_paint.HasQueryPose) return 1f;
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

    public void Teardown()
    {
        try { _step?.Wait(1000); } catch { }
        _step = null;
        // a build still running finishes on its own and is dropped
        foreach (var l in _layers) l.Build = null;
        if (_private != null)
        {
            // 0.14.0, instanced: the shared asset was never touched. The StaticMesh gets it back (and its renderers with it),
            // and the copy is unloaded once they have moved over.
            try
            {
                // a provider that let go of the copy itself (destroyed, another URL or variant) released its request on the
                // shared asset through the copy's relocation (0.26.1), as for any asset it frees: nothing to put back then
                if (_swapped && PrivateMeshes.Swap(Provider, _private, Asset)) { try { _assetManager?.QueueAssetCreated(Provider); } catch { } }
            }
            catch (Exception ex) { SoftbodyRuntime.Warn($"'{Name}': giving the renderer its shared mesh back: {ex.Message}"); }
            PrivateMeshes.Retire(_private);
            PrivateMeshes.UnloadLater(_private);
            _private = null;
            return;
        }
        if (_firstUploadSent && Interlocked.Read(ref GpuInPlaceAcks) == 0 && !_ackPending)
        {
            try { Asset.HighPriorityIntegration = _origHighPriority; } catch { }
            return;
        }
        if (!_firstUploadSent) return;
        var am = _assetManager; var provider = Provider;
        MeshUploadHint? partial = null;
        if (!_recreated.Value)
        {
            var h = new MeshUploadHint(MeshUploadHint.Flag.Positions | MeshUploadHint.Flag.Normals);
            if (_colorsUploaded && _original.HasColors) h[MeshUploadHint.Flag.Colors] = true;
            if (_tangentsUploaded && _original.HasTangents) h[MeshUploadHint.Flag.Tangents] = true;   // 0.26.3
            partial = h;
        }
        try
        {
            AssetUploads.QueueRestore(Asset, _original, _original, Provider.Readable.Value, _origHighPriority,
                                      changed => { if (changed) { try { am?.QueueAssetCreated(provider); } catch { } } }, partial, _recreated);
        }
        catch (Exception ex) { SoftbodyRuntime.Warn($"'{Name}': queueing restore failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ status

    public string RunningState
    {
        get
        {
            if (_layers.Length == 1)
            {
                var r = _layers[0].Rig;
                return r == null ? (_layers[0].Build != null ? "building tets…" : _layers[0].RigNote ?? "idle") :
                    _culled ? Culled() :
                    $"running: {r.Surface.SoftCount} soft points, {r.Shell.TetCount} tets, {r.MovingVertices.Length} moving vertices" +
                    (_layers[0].Build != null ? " (rebuilding from the paint…)" : "");
            }
            string Each(Layer l) => l.Rig != null ? $"{l.Name} {l.Rig.Surface.SoftCount} points, {l.Rig.Shell.TetCount} tets" + (l.Off ? " (off: Simulating 0)" : "") + (l.Build != null ? " (rebuilding)" : "") :
                                   l.Build != null ? $"{l.Name} building" : $"{l.Name} {l.RigNote ?? "idle"}";
            if (!AnyRig) return (IsBuilding ? "building tets: " : "") + string.Join("; ", _layers.Select(Each));
            if (_culled) return Culled();
            return $"running {_layers.Length} layers: " + string.Join("; ", _layers.Select(Each)) + $"; {_moving.Length} moving vertices";
        }
    }

    string Culled() => _off ? (_layers.Length > 1 && _simulating > 0f ? "off: every layer's Simulating is 0 (the mesh as animated, nothing simulated)"
                                                                      : "off: Simulating is 0 (the mesh as animated, nothing simulated)") :
        $"culled: {CullDistanceNow:0.#} m from you (CullDistance {_settings.CullDistance:0.#} m); resting, not simulated";

    public string Describe() =>
        $"{_n} verts, {_skin.BoneCount} bones, {_original.BlendShapeCount} blendshapes, skeleton root '{_skeletonRoot?.Name ?? "-"}'; " +
        (_private != null ? "instanced (its own copy of the mesh); " : "") + $"softness map {_mapDescription}; " +
        (AnyRig ? RigReport : string.Join("; ", _layers.Select(l => l.RigNote ?? "building")));

    public string StatsLine(double seconds, out string json)
    {
        var w = _w;
        int f = Math.Max(1, w.Frames);
        float maxOff = 0f; int contacts = 0, letGo = 0, inverted = 0, active = 0, resets = 0, near = 0, tris = 0, pairs = 0; string resetWhy = null;
        foreach (var l in _layers)
        {
            var r = l.Rig;
            if (r == null) continue;
            maxOff = MathF.Max(maxOff, r.MaxOffset); contacts += r.Solver.Contacts; letGo += r.Solver.LetGo; inverted += r.Solver.Inverted; active = Math.Max(active, r.ActiveShapes);
            resets += r.Resets; if (r.Resets > 0) resetWhy = r.LastResetReason;
            near += r.Solver.NearShapes.Length; tris += r.Solver.MeshTriangles; pairs += r.Solver.PairCount;
        }
        bool anyRig = AnyRig;
        string line = $"'{Name}' (flesh): {RunningState} | per frame: world thread {w.MainMs / f:0.000} ms (waiting for the step {w.WaitMs / f:0.000}), " +
                      $"step {w.StepMs / f:0.000} ms on a worker, max {w.MaxMainMs:0.00} ms | {w.Frames} frames / {seconds:0.0} s: {w.Uploads} uploads, {w.Busy} busy, {w.Idle} idle | " +
                      $"GPU path: {GpuPartials} partial uploads, {Interlocked.Read(ref GpuInPlaceAcks)} applied in place, {Interlocked.Read(ref GpuRecreatedAcks)} recreated (last ack {LastAckLatencyMs:0.0} ms) | " +
                      (anyRig ? $"max offset {maxOff * 1000:0.0} mm, {contacts} contacts ({letGo} let go: a collider on the wrong side), {inverted} inverted tets, {active} blendshapes active, resets {resets}" +
                                     (resetWhy != null ? $" ({resetWhy})" : "") : "no rig") +
                      $" | colliders: {_worldShapes} world, {_userRigShapes} users' bone colliders + {_userShapes} users' spheres, {_ownShapes} own bone colliders " +
                      $"({_ignoredPoints} point pairs ignored as touching at rest), {near} near the body ({tris} mesh triangles), " +
                      $"{pairs} point pairs | rebuilds {Rebuilds} (last {LastBuildMs:0} ms), paint {_paint.DabCount} dabs" +
                      (_layers.Length > 1 ? $" | {_layers.Length} layers, {_merge.Shared} vertices shared by two" : "");
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("\"kind\":\"flesh\"");
        sb.Append(",\"points\":").Append(PointCount);
        sb.Append(",\"tets\":").Append(TetCount);
        sb.Append(",\"verts\":").Append(_n);
        sb.Append(",\"movingVerts\":").Append(MovingVerts);
        sb.Append(",\"mainMs\":").Append((w.MainMs / f).ToString("0.000", ic));
        sb.Append(",\"waitMs\":").Append((w.WaitMs / f).ToString("0.000", ic));
        sb.Append(",\"stepMs\":").Append((w.StepMs / f).ToString("0.000", ic));
        sb.Append(",\"avgFrameMs\":").Append(EmaMainMs.ToString("0.000", ic));
        sb.Append(",\"uploads\":").Append(w.Uploads);
        sb.Append(",\"gpuInPlaceAcks\":").Append(Interlocked.Read(ref GpuInPlaceAcks));
        sb.Append(",\"gpuRecreatedAcks\":").Append(Interlocked.Read(ref GpuRecreatedAcks));
        sb.Append(",\"layers\":").Append(_layers.Length);
        sb.Append(",\"maxOffsetMm\":").Append((maxOff * 1000).ToString("0.0", ic));
        sb.Append(",\"contacts\":").Append(contacts);
        sb.Append(",\"letGo\":").Append(letGo);
        sb.Append(",\"invertedTets\":").Append(inverted);
        sb.Append(",\"resets\":").Append(resets);
        sb.Append(",\"worldColliders\":").Append(_worldShapes);
        sb.Append(",\"userColliders\":").Append(_userShapes);
        sb.Append(",\"userBoneColliders\":").Append(_userRigShapes);
        sb.Append(",\"ownBoneColliders\":").Append(_ownShapes);
        sb.Append(",\"selfIgnoredPairs\":").Append(_ignoredPoints);
        sb.Append(",\"nearShapes\":").Append(near);
        sb.Append(",\"meshTriangles\":").Append(tris);
        sb.Append(",\"pairs\":").Append(pairs);
        sb.Append(",\"rebuilds\":").Append(Rebuilds);
        sb.Append(",\"lastBuildMs\":").Append(LastBuildMs.ToString("0", ic));
        sb.Append(",\"rig\":").Append(StatsServer.Json(RigReport));
        sb.Append(",\"softnessMap\":").Append(StatsServer.Json(_mapDescription));
        sb.Append(",\"paintDabs\":").Append(_paint.DabCount);
        sb.Append(",\"settings\":").Append(StatsServer.Json(string.Join(" | ", _layers.Select(l => (_layers.Length > 1 ? l.Name + ": " : "") + l.Settings))));
        json = sb.ToString();
        w.Clear();
        return line;
    }

    static Vector3 V(in float3 f) => new(f.x, f.y, f.z);
    static float3 F(in Vector3 v) => new(v.X, v.Y, v.Z);
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double Elapsed(long ts) => (Stopwatch.GetTimestamp() - ts) / (double)Stopwatch.Frequency;
}
