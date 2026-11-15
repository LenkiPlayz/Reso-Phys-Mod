// Live bounce light (GI) for the renderer fork: the Unity side, with nothing of Resonite in it, so the SoftbodyShaders editor
// project runs the same code (GiSceneTest). SoftbodyGIHost (fork only) adds what is Resonite's: the world, its lights, its
// renderers, and the render thread.
//
// Main thread (Tick, once a frame): plans the frame (GiPlanner), draws the planned slab of the world into the voxelize buffer (a
// camera of its own with SoftbodyGIVoxelize as the replacement shader), packs the world's ambient and its lights, and hands the
// frame to the render thread. Render thread (GiGpu): GiCompute's share of the frame on Unity's own Direct3D 11 device, then copies
// the written volumes into Unity's render textures, which the lit shaders read per pixel as a light probe proxy volume.
//
// Which volume the renderers read, and where its window is: each renderer's block holds all of it (the switch
// unity_ProbeVolumeParams, the volume unity_ProbeVolumeSH, its corner, size and matrix), so a new window means a pass over every
// renderer (the host's sweep), and the old window is kept written until that pass is done. Shader globals do not work: Unity binds
// its own probe volume (a grey default) for every draw, so only a block's volume is read (editor test C8 to C9, 2026-11-14).
// A world's own MaterialPropertyBlocks override a renderer's block, so they get the same values (PrepareShared, Apply).
// The block also zeroes the ambient's quadratic part (unity_SHB*, unity_SHC), which the shaders would add on top of the volume:
// the volume holds the whole light (to L1). Renderers that do not reach into the window get no block, so far away the world looks
// exactly as without GI (Covers).
// Thin renderers (walls, floors: GiLeak) read the volume half a metre in front of themselves, toward the head, through their
// block's unity_ProbeVolumeWorldToObject (one shared block per direction), so they take no light from the far side of a wall;
// FollowHead turns them when the head crosses their middle.
//
// Plain UnityEngine and C# 7.3 (the 2019.4 editor compiles it too; build_gi.ps1 strips the #nullable line for it).
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DResource = SharpDX.Direct3D11.Resource;
using D3DTexture3D = SharpDX.Direct3D11.Texture3D;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace Softbody.GI
{
    /// <summary>One frame's work for the render thread.</summary>
    public sealed class GiJob
    {
        public int Id;
        public GiFramePlan Plan;
        public readonly GiLight[] Lights;
        public int LightCount;
        public readonly NVector4[] Sky = new NVector4[7];
        public readonly List<GiWindow> Windows = new List<GiWindow>(2);
        public IntPtr AccumPtr;
        public readonly IntPtr[] VolumePtr = new IntPtr[2];
        public int GlowRays;
        public GiJob(int maxLights) { Lights = new GiLight[maxLights]; }
    }

    public sealed class GiUnity : IDisposable
    {
        public const string Tag = "[Softbody GI] ";

        /// <summary>True while the voxel camera renders: the fork's mirrors, portals and Gaussian splats skip that camera.</summary>
        public static bool Voxelizing { get; private set; }

        public readonly GiConfig Config;
        public GiPlanner Planner { get; private set; }
        /// <summary>Draws a slab every this many planned frames.</summary>
        public int VoxelizeEvery = 1;
        /// <summary>Shadow rays a probe aims at glowing voxels (0 to 8; 0: only its rays see the glow). From the next frame on.</summary>
        public int GlowRays;

        static readonly int IdParams = Shader.PropertyToID("unity_ProbeVolumeParams");
        static readonly int IdSH = Shader.PropertyToID("unity_ProbeVolumeSH");
        static readonly int IdMin = Shader.PropertyToID("unity_ProbeVolumeMin");
        static readonly int IdSizeInv = Shader.PropertyToID("unity_ProbeVolumeSizeInv");
        static readonly int IdW2O = Shader.PropertyToID("unity_ProbeVolumeWorldToObject");
        // the ambient's quadratic part, which the shaders add on top of the volume (a block's values win: editor test C12, 2026-11-14)
        static readonly int IdSHBr = Shader.PropertyToID("unity_SHBr");
        static readonly int IdSHBg = Shader.PropertyToID("unity_SHBg");
        static readonly int IdSHBb = Shader.PropertyToID("unity_SHBb");
        static readonly int IdSHC = Shader.PropertyToID("unity_SHC");
        // 1 in a world's own block once the GI wrote into it (a block cannot drop a value again, so those get the plain ones back)
        static readonly int IdMark = Shader.PropertyToID("_SoftbodyGIMark");
        static readonly int IdSlabMin = Shader.PropertyToID("_SoftbodyGISlabMin");
        static readonly int IdSlabDim = Shader.PropertyToID("_SoftbodyGISlabDim");

        readonly Shader _voxelize;
        readonly Dictionary<string, byte[]> _bytecode;
        readonly Func<SharpDX.Direct3D11.Device> _device;
        readonly Action<Action> _enqueue;
        readonly GiGpu _gpu = new GiGpu();
        GameObject _camGo;
        Camera _cam;
        RenderTexture _rt;
        ComputeBuffer _accum;
        readonly RenderTexture[] _vol = new RenderTexture[2];
        IntPtr _accumPtr;
        readonly IntPtr[] _volPtr = new IntPtr[2];
        int _side;
        bool _disposed;

        // the render thread's jobs (one waits at most; the main thread skips a frame while it does)
        readonly object _lock = new object();
        readonly GiJob[] _jobs = new GiJob[2];
        GiJob _pending, _running;
        bool _queued;
        int _nextId = 1, _doneId;
        volatile Exception _failure;
        int _voxelizeCount;

        // what the renderers read: the volume shown and its window; the one being made (after a re-centring or a fresh start); and
        // the one shown before, written until a pass over every renderer has moved them all over
        int _shown = -1, _making = -1, _old = -1;
        NVector3 _shownOrigin, _makingOrigin, _oldOrigin;
        int _makingJob;
        bool _on, _wantOn = true;
        // every epoch any GiUnity hands out is new (the host stamps renderers with it, and may make a new GiUnity)
        static int s_epoch;
        int _epoch = ++s_epoch;
        // one block per lookup offset (GiLeak's codes 0 to 26; None: the plain lookup), each filled once an epoch
        readonly MaterialPropertyBlock[] _blocks = new MaterialPropertyBlock[27];
        readonly int[] _blocksEpoch = new int[27];
        readonly MaterialPropertyBlock _tmp = new MaterialPropertyBlock();

        // the thin renderers given a lookup offset this epoch (FollowHead keeps them turned to the head)
        struct Thin
        {
            public Renderer Renderer;
            public int PerMaterial, Axes, Code;
            public NVector3 Mid;
        }
        Thin[] _thin = new Thin[64];
        int _thinCount, _thinEpoch = -1, _thinRefresh;
        readonly Dictionary<int, int> _thinAt = new Dictionary<int, int>();   // a renderer's instance id: its place in _thin
        /// <summary>Bounds of thin renderers checked again each frame (they may move).</summary>
        public int ThinRefreshPerFrame = 32;
        public int ThinCount => _thinCount;
        /// <summary>Thin renderers turned to the head's other side so far.</summary>
        public int Turns;

        // lights, sorted by how much they matter here
        float[] _score = new float[64];
        GiLight[] _cand = new GiLight[64];

        // timings (ms, smoothed)
        public double MainMs, VoxelizeMs;
        public int LightsUsed, LightsSeen;

        /// <param name="voxelize">The voxelize shader (Hidden/Softbody/GIVoxelize).</param>
        /// <param name="bytecode">SoftbodyGI.hlsl's kernels, compiled (GiCompute.CompileAll).</param>
        /// <param name="device">Unity's Direct3D 11 device (asked on the render thread).</param>
        /// <param name="enqueue">Runs an action on the render thread, after what the main thread has drawn so far.</param>
        public GiUnity(GiConfig config, Shader voxelize, Dictionary<string, byte[]> bytecode,
                       Func<SharpDX.Direct3D11.Device> device, Action<Action> enqueue)
        {
            Config = config.Clone();
            Config.Validate();
            GlowRays = Config.GlowRays;
            _voxelize = voxelize ?? throw new ArgumentNullException(nameof(voxelize));
            _bytecode = bytecode ?? throw new ArgumentNullException(nameof(bytecode));
            _device = device;
            _enqueue = enqueue;
            Planner = new GiPlanner(Config);
            for (int i = 0; i < _jobs.Length; i++) _jobs[i] = new GiJob(Config.MaxLights);
            try { Create(); }
            catch { Dispose(); throw; }
        }

        void Create()
        {
            var c = Config;
            _side = Math.Max(c.VoxX, Math.Max(c.VoxY, c.SlabSlices));
            _camGo = new GameObject("Softbody GI voxel camera") { hideFlags = HideFlags.HideAndDontSave };
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(_camGo);   // (the editor's tests run outside play mode)
            _cam = _camGo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.Nothing;
            _cam.renderingPath = RenderingPath.Forward;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.allowDynamicResolution = false;
            _cam.useOcclusionCulling = false;
            _cam.depthTextureMode = DepthTextureMode.None;
            _rt = new RenderTexture(_side * 2, _side * 2, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = "Softbody GI voxelize target", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1, useMipMap = false,
            };
            _rt.Create();
            _cam.targetTexture = _rt;
            _accum = new ComputeBuffer(c.SlabVoxels * 10, 4, ComputeBufferType.Structured) { name = "Softbody GI slab" };
            _accum.SetData(new uint[c.SlabVoxels * 10]);
            for (int i = 0; i < 2; i++)
            {
                _vol[i] = new RenderTexture(4 * c.ProbeX, c.ProbeY, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "Softbody GI volume " + i, hideFlags = HideFlags.HideAndDontSave,
                    dimension = TextureDimension.Tex3D, volumeDepth = c.ProbeZ, enableRandomWrite = true,
                    useMipMap = false, autoGenerateMips = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                    antiAliasing = 1,
                };
                _vol[i].Create();
            }
            // (each waits for the render thread once)
            _accumPtr = _accum.GetNativeBufferPtr();
            for (int i = 0; i < 2; i++) _volPtr[i] = _vol[i].GetNativeTexturePtr();
            if (_accumPtr == IntPtr.Zero || _volPtr[0] == IntPtr.Zero || _volPtr[1] == IntPtr.Zero)
                throw new InvalidOperationException("Unity gave no native buffer or texture (not Direct3D 11?)");
        }

        // ------------------------------------------------------------------ the frame (main thread)

        /// <summary>Set by the render thread when the GPU side failed (the host stops GI and turns the renderers back).</summary>
        public Exception Failure => _failure;
        public GiStats GpuStats => _gpu.Stats;
        public double RenderThreadMs => _gpu.CpuMs;
        public int Epoch => _epoch;
        /// <summary>The renderers should read the volume (it holds a written window).</summary>
        public bool On => _on;
        public int Shown => _shown;
        public NVector3 ShownOrigin => _shownOrigin;
        public bool Migrating => _old >= 0;
        public RenderTexture Volume(int i) => _vol[i];

        /// <summary>Tests: runs <paramref name="a"/> on the render thread with the GPU side (not at all before its first frame).</summary>
        public void OnRenderThread(Action<GiCompute> a)
        {
            var gpu = _gpu;
            _enqueue(() => { if (!_disposed && gpu.Compute != null) a(gpu.Compute); });
        }

        /// <summary>Turns the bounce light off (the renderers go back to the plain ambient, through the host's sweep) or on again
        /// (a fresh start: the renderers come back once its first window is written).</summary>
        public void SetWanted(bool on)
        {
            if (on == _wantOn) return;
            _wantOn = on;
            if (!on) SetOn(false);
            else Restart();
        }

        /// <summary>Another world, or on again: everything starts over. The renderers go back to the plain ambient until the new
        /// start's first window is written (so they never read another world's light).</summary>
        public void Restart()
        {
            Planner = new GiPlanner(Config);
            _making = -1;
            _old = -1;
            SetOn(false);
        }

        void SetOn(bool on)
        {
            if (_on == on) return;
            _on = on;
            _epoch = ++s_epoch;
        }

        /// <summary>
        /// Main thread, once a frame: takes in what the render thread finished, then plans the next frame, draws its slab and
        /// queues it (unless the last one is still waiting). <paramref name="hide"/> (may be null) is called with true just before
        /// the voxel camera draws and with false after (the host hides the avatars).
        /// </summary>
        public void Tick(Vector3 head, int cullingMask, IList<Light> lights, Action<bool> hide)
        {
            if (_disposed) return;
            long t0 = Stopwatch.GetTimestamp();
            int done;
            lock (_lock) done = _doneId;

            // a window that is now in its volume
            if (_making >= 0 && done >= _makingJob)
            {
                if (_on && _shown >= 0) { _old = _shown; _oldOrigin = _shownOrigin; }
                _shown = _making; _shownOrigin = _makingOrigin;
                _making = -1;
                // every renderer's block changes: a new epoch (the host's sweep), and on if it was off
                _epoch = ++s_epoch;
                if (_wantOn) _on = true;
            }
            Planner.HoldShift = _making >= 0 || _old >= 0;

            if (!_wantOn) { Time(t0); return; }
            GiJob job;
            lock (_lock)
            {
                if (_pending != null) { Time(t0); return; }
                job = _jobs[0] != _running ? _jobs[0] : _jobs[1];
            }

            bool voxelize = (_voxelizeCount++ % Math.Max(1, VoxelizeEvery)) == 0;
            var plan = Planner.Next(new NVector3(head.x, head.y, head.z), voxelize);
            if (plan.Slab >= 0)
            {
                long v0 = Stopwatch.GetTimestamp();
                Planner.SlabBox(plan.Slab, out var min, out _);
                RenderSlab(new Vector3(min.X, min.Y, min.Z), cullingMask, hide, null);
                double vms = Ms(v0);
                VoxelizeMs = VoxelizeMs <= 0 ? vms : 0.95 * VoxelizeMs + 0.05 * vms;
            }

            job.Id = _nextId++;
            job.Plan = plan;
            if (plan.Reset || plan.HasShift)
            {
                // a new window: made in the volume nobody reads
                _making = _shown < 0 ? 0 : 1 - _shown;
                if (_old >= 0 && _making == _old) _old = -1;   // (cannot happen: no re-centring while one is kept)
                _makingOrigin = plan.Origin;
                _makingJob = job.Id;
            }
            job.Windows.Clear();
            if (_shown >= 0 && _on) job.Windows.Add(new GiWindow(_shown, _shownOrigin));   // (off: nobody reads it)
            if (_old >= 0) job.Windows.Add(new GiWindow(_old, _oldOrigin));
            if (_making >= 0) job.Windows.Add(new GiWindow(_making, _makingOrigin));
            PackAmbient(RenderSettings.ambientProbe, job.Sky);
            job.LightCount = GatherLights(lights, job.Lights, head);
            job.AccumPtr = _accumPtr;
            job.GlowRays = Math.Max(0, Math.Min(GlowRays, 8));
            job.VolumePtr[0] = _volPtr[0];
            job.VolumePtr[1] = _volPtr[1];

            bool queue;
            lock (_lock)
            {
                _pending = job;
                queue = !_queued;
                _queued = true;
            }
            if (queue) _enqueue(RenderThread);
            Time(t0);
        }

        void Time(long t0)
        {
            double ms = Ms(t0);
            MainMs = MainMs <= 0 ? ms : 0.95 * MainMs + 0.05 * ms;
        }

        static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        /// <summary>The host's sweep finished a pass over every renderer that it started at <paramref name="epoch"/>. PerRenderer:
        /// once a whole pass ran at the current epoch, no renderer reads the old window any more.</summary>
        public void PassFinished(int epoch)
        {
            if (epoch == _epoch && _old >= 0) _old = -1;
        }

        Vector4 SizeInv
        {
            get
            {
                var s = Config.Size;
                return new Vector4(1f / s.X, 1f / s.Y, 1f / s.Z, 0f);
            }
        }

        Vector4 Switch => new Vector4(1f, 0f, 1f / (4f * Config.ProbeX), 0f);
        // the same, read through unity_ProbeVolumeWorldToObject (y = 1: Unity's "local space" lookup)
        Vector4 SwitchMoved => new Vector4(1f, 1f, 1f / (4f * Config.ProbeX), 0f);

        /// <summary>How far thin renderers read the volume in front of themselves (m; 0: off). A change sends every renderer a new
        /// block (a new epoch).</summary>
        public float LeakOffset
        {
            get { return Config.LeakOffset; }
            set
            {
                float v = Math.Max(0f, Math.Min(value, 4f));
                if (v == Config.LeakOffset) return;
                Config.LeakOffset = v;
                _epoch = ++s_epoch;
            }
        }

        static NVector3 N(Vector3 v) => new NVector3(v.x, v.y, v.z);

        // ------------------------------------------------------------------ the renderers' blocks (main thread)

        /// <summary>This epoch's values into a block: the volume, and no quadratic part (the volume holds all the light there is, and
        /// the sky's quadratic part on top would be wrong indoors: on the faces it dims it took the bounce light below nothing,
        /// editor test 2026-11-14).</summary>
        public void Fill(MaterialPropertyBlock b) => Fill(b, true);

        void Fill(MaterialPropertyBlock b, bool use, int code = GiLeak.None)
        {
            b.SetFloat(IdMark, 1f);
            if (!use || !_on || _shown < 0)
            {
                // the plain ambient: no volume, and the world's own quadratic part (a world's block may hold our zeros)
                b.SetVector(IdParams, Vector4.zero);
                var sh = RenderSettings.ambientProbe;
                for (int c = 0; c < 3; c++) b.SetVector(c == 0 ? IdSHBr : c == 1 ? IdSHBg : IdSHBb, new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]));
                b.SetVector(IdSHC, new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f));
                return;
            }
            // a thin renderer (GiLeak): read in front of itself, toward the head
            bool moved = code != GiLeak.None && Config.LeakOffset > 0f;
            b.SetVector(IdParams, moved ? SwitchMoved : Switch);
            b.SetTexture(IdSH, _vol[_shown]);
            b.SetVector(IdMin, new Vector4(_shownOrigin.X, _shownOrigin.Y, _shownOrigin.Z, 0f));
            b.SetVector(IdSizeInv, SizeInv);
            if (moved)
            {
                var o = GiLeak.Offset(code, Config.LeakOffset);
                b.SetMatrix(IdW2O, Matrix4x4.Translate(new Vector3(o.X, o.Y, o.Z)));
            }
            else b.SetMatrix(IdW2O, Matrix4x4.identity);
            b.SetVector(IdSHBr, Vector4.zero); b.SetVector(IdSHBg, Vector4.zero); b.SetVector(IdSHBb, Vector4.zero);
            b.SetVector(IdSHC, new Vector4(0f, 0f, 0f, 1f));
        }

        /// <summary>Whether a renderer with these world bounds reaches into the window the renderers read, past its fading edge. The
        /// ones that do not keep the world's plain ambient (no block): beyond the window the volume only repeats the sky's
        /// linear part, without the quadratic part the plain ambient has.</summary>
        public bool Covers(Bounds b)
        {
            if (_shown < 0) return false;
            var c = Config;
            float m = c.EdgeFade * c.ProbeSpacing;
            var s = c.Size;
            var lo = b.min; var hi = b.max;
            return hi.x > _shownOrigin.X + m && lo.x < _shownOrigin.X + s.X - m
                && hi.y > _shownOrigin.Y + m && lo.y < _shownOrigin.Y + s.Y - m
                && hi.z > _shownOrigin.Z + m && lo.z < _shownOrigin.Z + s.Z - m;
        }

        /// <summary>A renderer of the GI world: its own block (none when off or <paramref name="inside"/> is false), and its
        /// per-material blocks (a world's MaterialPropertyBlocks, which override its own) get this epoch's values too.
        /// <paramref name="perMaterial"/>: how many per-material blocks it may have (Resonite's LastPropertyBlockCount).</summary>
        public void Apply(Renderer r, int perMaterial, bool inside = true) => Apply(r, perMaterial, inside, GiLeak.None);

        void Apply(Renderer r, int perMaterial, bool inside, int code)
        {
            if (r == null) return;
            bool use = _on && _shown >= 0 && inside;
            if (use)
            {
                var block = _blocks[code];
                if (block == null) _blocks[code] = block = new MaterialPropertyBlock();
                if (_blocksEpoch[code] != _epoch) { block.Clear(); Fill(block, true, code); _blocksEpoch[code] = _epoch; }
                r.SetPropertyBlock(block);
            }
            else r.SetPropertyBlock(null);
            for (int m = 0; m < perMaterial; m++)
            {
                r.GetPropertyBlock(_tmp, m);
                if (_tmp.isEmpty) continue;
                if (!use && _tmp.GetFloat(IdMark) != 1f) continue;   // never had ours
                Fill(_tmp, use, code);
                r.SetPropertyBlock(_tmp, m);
            }
        }

        /// <summary>The host's sweep: a renderer with world bounds <paramref name="b"/>, the head at <paramref name="head"/>. Its block
        /// as Apply gives it (none when it does not reach into the window), and for a thin one (GiLeak) the lookup offset toward the
        /// head, which FollowHead keeps turned to the head's side.</summary>
        public void ApplyAt(Renderer r, int perMaterial, Bounds b, Vector3 head)
        {
            if (r == null) return;
            bool inside = Covers(b);
            int axes = inside && _on && _shown >= 0 ? GiLeak.ThinAxes(N(b.size), Config) : 0;
            var mid = N(b.center);
            int code = axes != 0 ? GiLeak.Code(axes, mid, N(head)) : GiLeak.None;
            Apply(r, perMaterial, inside, code);
            if (axes != 0) Track(r, perMaterial, axes, mid, code);
        }

        void Track(Renderer r, int perMaterial, int axes, NVector3 mid, int code)
        {
            if (_thinEpoch != _epoch) ClearThin();
            int id = r.GetInstanceID(), i;
            if (!_thinAt.TryGetValue(id, out i))
            {
                if (_thinCount == _thin.Length) Array.Resize(ref _thin, _thin.Length * 2);
                i = _thinCount++;
                _thinAt[id] = i;
            }
            _thin[i] = new Thin { Renderer = r, PerMaterial = perMaterial, Axes = axes, Code = code, Mid = mid };
        }

        void ClearThin()
        {
            Array.Clear(_thin, 0, _thinCount);
            _thinCount = 0;
            _thinRefresh = 0;
            _thinAt.Clear();
            _thinEpoch = _epoch;
        }

        void RemoveThin(int i)
        {
            var gone = _thin[i].Renderer;
            if ((object)gone != null) _thinAt.Remove(gone.GetInstanceID());
            int last = --_thinCount;
            if (i != last)
            {
                _thin[i] = _thin[last];
                var moved = _thin[i].Renderer;
                if ((object)moved != null) _thinAt[moved.GetInstanceID()] = i;
            }
            _thin[last] = default(Thin);
        }

        /// <summary>
        /// Main thread, once a frame after the sweep: a thin renderer whose middle the head crossed reads the volume on the head's new
        /// side (a few float tests each; a block only for those that turn), and a few thin renderers' bounds are checked again (they
        /// may move or turn).
        /// </summary>
        public void FollowHead(Vector3 head)
        {
            if (_thinEpoch != _epoch) { if (_thinCount > 0) ClearThin(); else _thinEpoch = _epoch; return; }
            var h = N(head);
            for (int i = 0; i < _thinCount; i++)
            {
                int code = GiLeak.Code(_thin[i].Axes, _thin[i].Mid, h);
                if (code == _thin[i].Code) continue;
                var r = _thin[i].Renderer;
                if (r == null) { RemoveThin(i--); continue; }   // (destroyed)
                _thin[i].Code = code;
                Apply(r, _thin[i].PerMaterial, true, code);
                Turns++;
            }
            int n = Math.Min(_thinCount, ThinRefreshPerFrame);
            for (int k = 0; k < n && _thinCount > 0; k++)
            {
                if (_thinRefresh >= _thinCount) _thinRefresh = 0;
                int i = _thinRefresh++;
                var r = _thin[i].Renderer;
                if (r == null) { RemoveThin(i); continue; }
                var b = r.bounds;
                bool inside = Covers(b);
                int axes = inside ? GiLeak.ThinAxes(N(b.size), Config) : 0;
                var mid = N(b.center);
                if (axes != 0 && axes == _thin[i].Axes) { _thin[i].Mid = mid; continue; }   // (the loop above turns it if need be)
                // thin another way, no longer thin, or out of the window: its block again
                int code = axes != 0 ? GiLeak.Code(axes, mid, h) : GiLeak.None;
                Apply(r, _thin[i].PerMaterial, inside, code);
                if (axes != 0) { _thin[i].Axes = axes; _thin[i].Mid = mid; _thin[i].Code = code; }
                else RemoveThin(i);
            }
        }

        /// <summary>A world's shared MaterialPropertyBlock, just before the fork sets it on a renderer of the GI world (the renderer
        /// keeps a copy, so the copy has this epoch's values; the sweep sets the plain ones on a renderer outside the window).</summary>
        public void PrepareShared(MaterialPropertyBlock b) => PrepareShared(b, null);

        /// <summary>The same, for <paramref name="r"/>: a thin renderer's copy gets its lookup offset too.</summary>
        public void PrepareShared(MaterialPropertyBlock b, Renderer r)
        {
            if (b == null) return;
            if (_on && _shown >= 0)
            {
                int code = GiLeak.None, i;
                if ((object)r != null && _thinEpoch == _epoch && _thinAt.TryGetValue(r.GetInstanceID(), out i)) code = _thin[i].Code;
                Fill(b, true, code);
            }
            else if (b.GetFloat(IdMark) == 1f) Fill(b, false);
        }

        // ------------------------------------------------------------------ voxelizing (main thread)

        /// <summary>Draws the world's slab with its corner at <paramref name="min"/> into the slab buffer.</summary>
        void RenderSlab(Vector3 min, int cullingMask, Action<bool> hide, Action extra)
        {
            var c = Config;
            float vs = c.VoxelSize, m = vs;
            var size = new Vector3(c.VoxX, c.VoxY, c.SlabSlices) * vs;
            Shader.SetGlobalVector(IdSlabMin, new Vector4(min.x, min.y, min.z, 1f / vs));
            Shader.SetGlobalVector(IdSlabDim, new Vector4(c.VoxX, c.VoxY, c.SlabSlices, _side));
            // an orthographic camera around the slab (it only culls: the shader places every triangle itself)
            _camGo.transform.SetPositionAndRotation(new Vector3(min.x + size.x * 0.5f, min.y + size.y * 0.5f, min.z - m - 0.01f), Quaternion.identity);
            _cam.orthographicSize = size.y * 0.5f + m;
            _cam.aspect = (size.x + 2f * m) / (size.y + 2f * m);
            _cam.nearClipPlane = 0.01f;
            _cam.farClipPlane = size.z + 2f * m + 0.01f;
            _cam.cullingMask = cullingMask;
            var shadows = QualitySettings.shadows;
            bool hidden = false;
            try
            {
                QualitySettings.shadows = ShadowQuality.Disable;
                if (hide != null) { hide(true); hidden = true; }
                extra?.Invoke();
                Graphics.SetRandomWriteTarget(1, _accum, false);
                Voxelizing = true;
                _cam.RenderWithShader(_voxelize, "RenderType");
            }
            finally
            {
                Voxelizing = false;
                Graphics.ClearRandomWriteTargets();
                if (hidden) hide(false);
                QualitySettings.shadows = shadows;
            }
        }

        // ------------------------------------------------------------------ the self-test (main thread)

        static readonly Vector3 Far = new Vector3(0f, -20000f, 0f);
        AsyncGPUReadbackRequest _testRequest;
        bool _testing;
        Mesh _testMesh;
        Material _testMat;

        /// <summary>Draws a 1 m white square (4 x 4 voxels, 2 x 2 pixels each: 64 samples) far below the world into the slab buffer,
        /// on <paramref name="layer"/>, which only this camera draws (the renderer's Temp layer), and reads the buffer back. Call
        /// before the first Tick; then TestResult each frame until it says done.</summary>
        public void StartSelfTest(int layer)
        {
            if (layer < 0 || layer > 31) layer = 31;
            GiShaderWatch.Watch();
            var c = Config;
            float vs = c.VoxelSize;
            var min = Far - new Vector3(c.VoxX, c.VoxY, c.SlabSlices) * (vs * 0.5f);
            _testMesh = new Mesh { name = "Softbody GI self-test", hideFlags = HideFlags.HideAndDontSave };
            float z = 0.5f * vs;   // the middle of a slice
            _testMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, z), new Vector3(-0.5f, 0.5f, z), new Vector3(0.5f, 0.5f, z), new Vector3(0.5f, -0.5f, z) };
            _testMesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            _testMesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            _testMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _testMesh.bounds = new Bounds(new Vector3(0f, 0f, z), new Vector3(1f, 1f, 0.1f));
            _testMat = new Material(_voxelize) { name = "Softbody GI self-test", hideFlags = HideFlags.HideAndDontSave };
            _testMat.SetColor("_Color", Color.white);
            var mesh = _testMesh; var mat = _testMat; var cam = _cam;
            RenderSlab(min, 1 << layer, null, () => Graphics.DrawMesh(mesh, Matrix4x4.Translate(Far), mat, layer, cam, 0, null,
                ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null));
            _testRequest = AsyncGPUReadback.Request(_accum);
            _testing = true;
        }

        /// <summary>The editor: waits for the self-test's read-back.</summary>
        public void WaitSelfTest() { if (_testing) _testRequest.WaitForCompletion(); }

        /// <summary>Null while the read-back is on its way; then true (the shader voxelizes) or false, with what was seen.</summary>
        public bool? TestResult(out string result)
        {
            result = null;
            if (!_testing || !_testRequest.done) return null;
            _testing = false;
            try
            {
                if (_testRequest.hasError) { result = "the read-back failed"; return false; }
                var data = _testRequest.GetData<uint>();
                long count = 0, albedo = 0, nx = 0, ny = 0, nz = 0, emission = 0;
                for (int i = 0; i + 9 < data.Length; i += 10)
                {
                    if (data[i] == 0) continue;
                    count += data[i];
                    albedo += data[i + 1] + data[i + 2] + data[i + 3];
                    emission += data[i + 4] + data[i + 5] + data[i + 6];
                    nx += (int)data[i + 7]; ny += (int)data[i + 8]; nz += (int)data[i + 9];
                }
                bool ok = count >= 48 && count <= 100 && albedo == count * 3 * 255 && emission == 0 && Math.Abs(nz) == count * 64
                          && nx == 0 && ny == 0 && !GiShaderWatch.Tripped;
                result = $"test square: {count} samples (64 expected), albedo {(count > 0 ? albedo / (3.0 * count) : 0):0.#} (255), normal z {(count > 0 ? (double)nz / count : 0):0.#} (-64)"
                         + (GiShaderWatch.Tripped ? "; the renderer logged \"" + GiShaderWatch.TrippedBy + "\"" : "");
                return ok;
            }
            finally
            {
                Destroy(_testMesh); Destroy(_testMat);
                _testMesh = null; _testMat = null;
            }
        }

        // ------------------------------------------------------------------ the world's ambient and lights

        /// <summary>Unity's shader constants for an ambient probe (unity_SHAr, SHAg, SHAb, SHBr, SHBg, SHBb, SHC).</summary>
        public static void PackAmbient(SphericalHarmonicsL2 sh, NVector4[] o)
        {
            for (int c = 0; c < 3; c++)
            {
                o[c] = new NVector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]);
                o[c + 3] = new NVector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]);
            }
            o[6] = new NVector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f);
        }

        /// <summary>A Unity light as the GPU lights voxels with it (its colour as the shaders get it). False: it lights nothing here
        /// (off, black, or an area light).</summary>
        public static bool ToGiLight(Light l, out GiLight g)
        {
            g = default(GiLight);
            if (l == null || !l.isActiveAndEnabled || !(l.intensity > 0f)) return false;
            Color c = GraphicsSettings.lightsUseLinearIntensity ? l.color.linear * l.intensity : (l.color * l.intensity).linear;
            var col = new NVector3(c.r, c.g, c.b);
            if (!(col.X + col.Y + col.Z > 1e-5f) || float.IsInfinity(col.X + col.Y + col.Z)) return false;
            bool shadows = l.shadows != LightShadows.None;
            var t = l.transform;
            var f = t.forward;
            var p = t.position;
            switch (l.type)
            {
                case LightType.Directional:
                    g = GiLight.Directional(new NVector3(f.x, f.y, f.z), col, shadows);
                    return true;
                case LightType.Point:
                    if (!(l.range > 0.01f)) return false;
                    g = GiLight.Point(new NVector3(p.x, p.y, p.z), l.range, col, shadows);
                    return true;
                case LightType.Spot:
                    if (!(l.range > 0.01f)) return false;
                    g = GiLight.SpotLight(new NVector3(p.x, p.y, p.z), new NVector3(f.x, f.y, f.z), l.range, l.spotAngle, col, shadows);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The lights that reach the grid, the ones that matter most first (directional, then bright and near the head),
        /// at most MaxLights.</summary>
        int GatherLights(IList<Light> lights, GiLight[] dst, Vector3 head)
        {
            int n = 0, seen = 0;
            if (lights != null)
            {
                var gmin = Planner.Origin;
                var gmax = gmin + Config.Size;
                if (_score.Length < lights.Count) { _score = new float[lights.Count]; _cand = new GiLight[lights.Count]; }
                for (int i = 0; i < lights.Count; i++)
                {
                    if (!ToGiLight(lights[i], out var g)) continue;
                    seen++;
                    float lum = 0.2126f * g.Color.X + 0.7152f * g.Color.Y + 0.0722f * g.Color.Z;
                    float score;
                    if (g.DirType.W < 0.5f) score = 1e30f + lum;
                    else
                    {
                        float r = g.PosRange.W;
                        float dx = Math.Max(0f, Math.Max(gmin.X - g.PosRange.X, g.PosRange.X - gmax.X));
                        float dy = Math.Max(0f, Math.Max(gmin.Y - g.PosRange.Y, g.PosRange.Y - gmax.Y));
                        float dz = Math.Max(0f, Math.Max(gmin.Z - g.PosRange.Z, g.PosRange.Z - gmax.Z));
                        if (dx * dx + dy * dy + dz * dz >= r * r) continue;   // its light ends before the grid
                        float hx = g.PosRange.X - head.x, hy = g.PosRange.Y - head.y, hz = g.PosRange.Z - head.z;
                        score = lum * r * r / (r * r + hx * hx + hy * hy + hz * hz);
                    }
                    _score[n] = -score;
                    _cand[n] = g;
                    n++;
                }
                if (n > 1) Array.Sort(_score, _cand, 0, n);
            }
            int used = Math.Min(n, dst.Length);
            Array.Copy(_cand, dst, used);
            LightsUsed = used;
            LightsSeen = seen;
            return used;
        }

        // ------------------------------------------------------------------ the render thread

        void RenderThread()
        {
            GiJob job;
            lock (_lock) { job = _pending; _pending = null; _queued = false; _running = job; }
            if (job == null) { lock (_lock) _running = null; return; }
            try
            {
                if (_failure == null && !_disposed)
                {
                    _gpu.Run(_device(), Config, _bytecode, job);
                    lock (_lock) _doneId = job.Id;
                }
            }
            catch (Exception ex) { _failure = ex; }
            finally { lock (_lock) _running = null; }
        }

        // ------------------------------------------------------------------

        static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>Main thread. The render thread's half is let go on the render thread (Unity's objects stay alive until then:
        /// the GPU side holds its own references to them).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var gpu = _gpu;
            try { _enqueue?.Invoke(() => gpu.Dispose()); } catch { }
            if (_accum != null) { _accum.Release(); _accum = null; }
            for (int i = 0; i < 2; i++) { if (_vol[i] != null) { _vol[i].Release(); Destroy(_vol[i]); _vol[i] = null; } }
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            Destroy(_camGo); _camGo = null; _cam = null;
            Destroy(_testMesh); Destroy(_testMat);
        }
    }

    /// <summary>The render thread's half: GiCompute on Unity's device, and the copies into Unity's volumes.</summary>
    sealed class GiGpu : IDisposable
    {
        GiCompute _gi;
        IntPtr _accumPtr;
        readonly D3DTexture3D[] _vol = new D3DTexture3D[2];
        readonly IntPtr[] _volPtr = new IntPtr[2];
        public double CpuMs;
        public GiStats Stats => _gi?.Stats;
        /// <summary>Render thread only.</summary>
        public GiCompute Compute => _gi;

        public void Run(SharpDX.Direct3D11.Device device, GiConfig config, Dictionary<string, byte[]> bytecode, GiJob job)
        {
            long t0 = Stopwatch.GetTimestamp();
            if (_gi == null)
            {
                _gi = new GiCompute(device, config, null, name => bytecode[name]);
                _accumPtr = IntPtr.Zero;
                Debug.Log(GiUnity.Tag + $"GPU side running: {config.VoxX}x{config.VoxY}x{config.VoxZ} voxels of {config.VoxelSize * 100:0.#} cm, "
                          + $"{config.ProbeX}x{config.ProbeY}x{config.ProbeZ} probes, on {device.FeatureLevel}");
            }
            if (job.AccumPtr != _accumPtr)
            {
                System.Runtime.InteropServices.Marshal.AddRef(job.AccumPtr);
                _gi.UseAccum(new D3DBuffer(job.AccumPtr));   // takes the reference over; zeroes the buffer
                _accumPtr = job.AccumPtr;
            }
            for (int i = 0; i < 2; i++)
                if (job.VolumePtr[i] != _volPtr[i])
                {
                    _vol[i]?.Dispose();
                    _vol[i] = null;
                    _vol[i] = WrapVolume(job.VolumePtr[i], i);
                    _volPtr[i] = job.VolumePtr[i];
                }
            _gi.GlowRays = job.GlowRays;
            _gi.Frame(job.Plan, job.Lights, job.LightCount, job.Sky, job.Windows);
            for (int k = 0; k < job.Windows.Count; k++)
            {
                int v = job.Windows[k].Volume;
                if (v >= 0 && v < 2 && _vol[v] != null) _gi.CopyVolume(v, _vol[v]);
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            CpuMs = CpuMs <= 0 ? ms : 0.95 * CpuMs + 0.05 * ms;
        }

        D3DTexture3D WrapVolume(IntPtr p, int i)
        {
            if (p == IntPtr.Zero) throw new InvalidOperationException("no native texture for volume " + i);
            System.Runtime.InteropServices.Marshal.AddRef(p);
            using (var res = new D3DResource(p))
            {
                if (res.Dimension != SharpDX.Direct3D11.ResourceDimension.Texture3D)
                    throw new InvalidOperationException($"Unity's volume {i} is a {res.Dimension}, not a 3D texture");
                var tex = res.QueryInterface<D3DTexture3D>();
                var d = tex.Description;
                var f = d.Format;
                bool formatOk = f == SharpDX.DXGI.Format.R16G16B16A16_Float || f == SharpDX.DXGI.Format.R16G16B16A16_Typeless;
                if (d.Width != _gi.VolumeWidth || d.Height != _gi.VolumeHeight || d.Depth != _gi.VolumeDepth || !formatOk || d.MipLevels != 1)
                {
                    tex.Dispose();
                    throw new InvalidOperationException($"Unity's volume {i} is {d.Width}x{d.Height}x{d.Depth} {f} ({d.MipLevels} mips), not "
                                                        + $"{_gi.VolumeWidth}x{_gi.VolumeHeight}x{_gi.VolumeDepth} R16G16B16A16");
                }
                Debug.Log(GiUnity.Tag + $"volume {i}: {d.Width}x{d.Height}x{d.Depth} {f}, {d.Usage}, {d.BindFlags}");
                return tex;
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < 2; i++) { _vol[i]?.Dispose(); _vol[i] = null; _volPtr[i] = IntPtr.Zero; }
            _gi?.Dispose();
            _gi = null;
            _accumPtr = IntPtr.Zero;
        }
    }

    /// <summary>Watches the log for the renderer's "could not make this shader program" lines (as SmokeSelfTest does for the
    /// smoke: a shader bundle from a newer editor than the game's renderer).</summary>
    public static class GiShaderWatch
    {
        static volatile bool _tripped;
        static volatile string _by = "";
        static bool _hooked;
        public static bool Tripped => _tripped;
        public static string TrippedBy => _by;

        public static void Watch()
        {
            _by = "";
            _tripped = false;
            if (_hooked) return;
            _hooked = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void Stop()
        {
            if (!_hooked) return;
            _hooked = false;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_tripped || condition == null) return;
            if (condition.IndexOf("D3D shader create error", StringComparison.Ordinal) < 0
                && condition.IndexOf("ShaderProgram is unsupported", StringComparison.Ordinal) < 0) return;
            _by = condition.Length > 160 ? condition.Substring(0, 160) : condition.Trim();
            _tripped = true;
        }
    }
}
