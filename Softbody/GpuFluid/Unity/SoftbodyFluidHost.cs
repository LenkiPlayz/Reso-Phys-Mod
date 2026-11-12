// 0.16.0: the GPU fluid inside the Softbody renderer fork (Renderite.Unity, Unity 2019.4, Direct3D 11). Compiled into the
// fork only (it links this file); the engine-free parts it drives are tested offline.
//
// Main thread, once a frame (RenderingManager.HandleUpdate calls Update): reads the plugin's frame from the shared channel,
// finds the plugin's placeholder surface renderer (by its mesh asset id) and keeps a child of it that draws the GPU's
// surface with the placeholder's own materials, and hands the frame to the render thread. Render thread: steps the GPU fluid
// (GpuFluidRuntime) on Unity's own device and copies the surface straight into the child mesh's vertex and index buffers;
// nothing comes back to the CPU but a few counts, a few frames late. The handshake event tells the plugin it can stop
// simulating on the CPU. 0.18.0: it is opened as soon as the kernels compiled and an empty warm-up step ran on the render
// thread (with the copy into the mesh). In 0.16.0 and 0.17.0 it waited for the first step of a plugin frame, but the plugin
// only sends frames once the handshake exists: neither ever started, and the liquid stayed on the CPU.
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renderite.Unity;
using UnityEngine;
using UnityEngine.Rendering;
using D3DBuffer = SharpDX.Direct3D11.Buffer;

namespace Softbody.FluidGpu
{
    public static class SoftbodyFluidHost
    {
        const string Tag = "[Softbody fluid] ";

        static bool _started, _failed, _warmQueued;
        static FluidChannelIO _io;
        static Task<Dictionary<string, byte[]>> _compile;
        static Dictionary<string, byte[]> _bytecode;
        static EventWaitHandle _handshake;

        // the frame handed to the render thread (one at a time: a frame the render thread has not taken yet is replaced)
        sealed class Job
        {
            public float Dt;
            public bool Reset;
            public FluidEmitterRecord[] Emitters = new FluidEmitterRecord[16];
            public int EmitterCount;
            public FluidColliderRecord[] Colliders = new FluidColliderRecord[256];
            public int ColliderCount;
            public IntPtr VertexBuffer, IndexBuffer;
            public int Capacity;
            public float ParticleSize, SurfaceDetail, DropletSize;
            public bool Classic;   // 0.25.0
            // 0.26.0: the gas, the head its puffs face, and the gas mesh's buffers
            public GasEmitterRecord[] Gas = new GasEmitterRecord[4];
            public int GasCount;
            public System.Numerics.Vector3 Head;
            public IntPtr GasVertexBuffer, GasIndexBuffer;
            public IntPtr FoamVertexBuffer, FoamIndexBuffer;   // 0.27.0: the spray, foam and bubbles' mesh
            // 0.27.0: drawn with the smoke shader (quads), lit from the world's main light (the gas's space; zero: none)
            public bool GasQuads, FoamQuads;
            public System.Numerics.Vector3 TowardsLight;
        }

        static readonly object _lock = new object();
        static readonly Job[] _jobs = { new Job(), new Job() };
        static Job _pending, _running;
        static volatile bool _queued;

        // render thread state
        static GpuFluidRuntime _rt;
        static D3DBuffer _unityVb, _unityIb;
        static IntPtr _unityVbPtr, _unityIbPtr;
        static D3DBuffer _unityGasVb, _unityGasIb;   // 0.26.0
        static IntPtr _unityGasVbPtr, _unityGasIbPtr;
        static D3DBuffer _unityFoamVb, _unityFoamIb; // 0.27.0
        static IntPtr _unityFoamVbPtr, _unityFoamIbPtr;
        static int _rtCapacity;
        static float _rtSize;

        // what the render thread last reported (taken on the main thread)
        static readonly object _statsLock = new object();
        static int _sParticles, _sVertices, _sIndices, _sError, _sFrame;
        static float _sGpuMs, _sCpuMs, _sSize, _sDetailUsed = 1f;
        static bool _sTruncated;
        static Vector3 _sMin, _sMax;
        static int _sGasPuffs;                       // 0.26.0
        static Vector3 _sGasMin = new Vector3(1, 1, 1), _sGasMax = new Vector3(-1, -1, -1);
        static int _sFoam, _sThickIterations, _sThickUnconverged, _sVortices;   // 0.27.0
        static int[] _sPairs = new int[FluidChannel.MaxEmitters * 2];
        static int _sPairCount;
        static bool _ready;

        // main thread state
        static FluidEmitterRecord[] _em;
        static FluidColliderRecord[] _col;
        static int _lastFrame = -1, _owner, _drawing;
        static float _lastTime = -1f;
        static GameObject _go;
        static Mesh _mesh;
        static MeshFilter _filter;
        static MeshRenderer _renderer;
        static MeshRenderable _target;
        static int _targetMeshId = -1;
        static float _searchTimer;
        static Material[] _lastMaterials;
        // 0.26.0: the gas's mesh and the child renderer drawing it with the plugin's gas placeholder's material
        static GasEmitterRecord[] _gas;
        static GameObject _gasGo;
        static Mesh _gasMesh;
        static MeshFilter _gasFilter;
        static MeshRenderer _gasRenderer;
        static MeshRenderable _gasTarget;
        static int _gasTargetMeshId = -1, _gasDrawing;
        static float _gasSearchTimer;
        static Material[] _gasLastMaterials;
        static IntPtr _gasVbPtr, _gasIbPtr;
        // 0.27.0: the spray, foam and bubbles' mesh and the child renderer drawing it, also with the gas placeholder's material
        static GameObject _foamGo;
        static Mesh _foamMesh;
        static MeshFilter _foamFilter;
        static MeshRenderer _foamRenderer;
        static Material[] _foamLastMaterials;
        static IntPtr _foamVbPtr, _foamIbPtr;
        static int _foamDrawing;
        // 0.27.0: the smoke shader (GpuFluid/Shaders/SoftbodySmoke.shader, from the bundle in StreamingAssets/Softbody): its smoke
        // and foam materials, or null (the old discs with the plugin's Unlit material)
        public const string SmokeBundleName = "softbody_smoke.bundle";
        static bool _smokeTried;
        static Material _smokeMat, _foamMat;
        static int _depthOkId;
        static float _lightTimer;
        static System.Numerics.Vector3 _towardsLight;

        // the mesh draws all its indices every frame (the ones past the surface are zero, degenerate): changing its draw
        // range would make Unity upload the mesh's own (empty) copy over what the GPU wrote
        // (0.19.0, Lenki: 400,000 vertices, from 131,072; past them the surface lowers its own detail to fit)
        public const int VertexCapacity = 400000, IndexCapacity = 400000 * 6;
        /// <summary>0.26.0: the gas mesh: 7 vertices and 18 indices for each of GpuFluidConfig.GasCapacity puffs. 0.27.0: a vertex is
        /// position, normal, colour and texcoord0 (GpuFluidSim.GasVertexBytes); as quads for the smoke shader a puff uses 4 and 6.</summary>
        public const int GasPuffCapacity = 32768, GasVertexCapacity = GasPuffCapacity * 7, GasIndexCapacity = GasPuffCapacity * 18;
        /// <summary>0.27.0: the foam mesh: 7 vertices and 18 indices for each of GpuFluidConfig.FoamCapacity spray, foam and bubble
        /// particles (18 MB of vertices; made only once a liquid in a world asks for foam).</summary>
        public const int FoamCapacity = 65536, FoamVertexCapacity = FoamCapacity * 7, FoamIndexCapacity = FoamCapacity * 18;

        static string Hlsl()
        {
            using (var s = typeof(SoftbodyFluidHost).Assembly.GetManifestResourceStream("Softbody.FluidGpu.FluidSim.hlsl"))
            {
                if (s == null) throw new InvalidOperationException("the FluidSim.hlsl resource is missing");
                using (var r = new StreamReader(s)) return r.ReadToEnd();
            }
        }

        /// <summary>Main thread, once a frame.</summary>
        public static void Update()
        {
            if (_failed) return;
            try { UpdateInner(); }
            catch (Exception ex) { _failed = true; Debug.LogError(Tag + "stopped: " + ex); try { _sError = 1; WriteBack(); } catch { } Hide(); }
        }

        static void UpdateInner()
        {
            if (!_started)
            {
                _started = true;
                if (RenderingManager.Instance == null || AssetIntegrator._dx11device == null || !RenderingManager.Instance.AssetIntegrator.RenderThreadProcessingEnabled)
                {
                    Debug.Log(Tag + "not Direct3D 11: the GPU fluid stays off (the plugin simulates on the CPU)");
                    _failed = true;
                    return;
                }
                _io = FluidChannelIO.Open();
                string hlsl = Hlsl();
                _compile = Task.Run(() => GpuFluidSim.CompileAll(hlsl));
                Debug.Log(Tag + "compiling the GPU fluid's kernels");
            }
            if (_bytecode == null)
            {
                if (!_compile.IsCompleted) return;
                if (_compile.IsFaulted) { _failed = true; Debug.LogError(Tag + "compiling failed: " + _compile.Exception); return; }
                _bytecode = _compile.Result;
                Debug.Log(Tag + "kernels compiled");
            }
            if (!_warmQueued)
            {
                // the warm-up: an empty step on the render thread, then the handshake (see the top)
                _warmQueued = true;
                EnsureMesh();
                lock (_lock)
                {
                    var job = _pending ?? (_jobs[0] != _running ? _jobs[0] : _jobs[1]);
                    job.Dt = 0f; job.Reset = false; job.EmitterCount = 0; job.ColliderCount = 0;
                    job.VertexBuffer = _vbPtr; job.IndexBuffer = _ibPtr;
                    job.Capacity = 65536; job.ParticleSize = 0.025f; job.SurfaceDetail = 1f; job.DropletSize = 1.5f;   // (0.25.0: the warm-up at the smallest room a world gets)
                    _pending = job;
                }
                if (!_queued)
                {
                    _queued = true;
                    RenderingManager.Instance.AssetIntegrator.EnqueueRenderThreadProcessing(RenderThread);
                }
                Debug.Log(Tag + "warming up on the render thread");
            }
            if (!_io.TryReadFrame(out var h, ref _em, ref _col, ref _gas)) { WriteBack(); return; }
            if (h.Active == 0) { Hide(); WriteBack(); return; }
            bool reset = h.Owner != _owner;
            if (reset) { _owner = h.Owner; _lastFrame = -1; _lastTime = -1f; }
            if (h.Frame == _lastFrame) { KeepDrawing(); WriteBack(); return; }
            float dt = _lastTime >= 0f ? h.Time - _lastTime : h.Dt;
            if (!(dt > 0f) || dt > 0.25f) dt = h.Dt;
            _lastFrame = h.Frame; _lastTime = h.Time;

            EnsureMesh();
            FindTarget(h.MeshAssetId);
            KeepDrawing();
            if (_mesh == null) { WriteBack(); return; }
            // 0.26.0: the gas's mesh, made when a world first sends gas. 0.27.0: the foam's, when a liquid first asks for foam; it
            // draws with the gas placeholder's material too (the plugin makes that placeholder for foam as well)
            bool foam = false;
            for (int i = 0; i < h.EmitterCount; i++) if (_em[i].Active != 0 && _em[i].Foam > 0f) { foam = true; break; }
            bool gasNeeded = h.GasEmitterCount > 0 || _gasMesh != null, foamNeeded = foam || _foamMesh != null;
            if (gasNeeded || foamNeeded) { EnsureSmoke(); FindGasTarget(h.GasMeshAssetId); }
            if (gasNeeded) { EnsureGasMesh(); KeepDrawingGas(); KeepGasBounds(h.GasEmitterCount); }
            if (foamNeeded) { EnsureFoamMesh(); KeepDrawingFoam(); }
            if (gasNeeded && _smokeMat != null) KeepTowardsLight();

            // the frame for the render thread
            lock (_lock)
            {
                // a frame the render thread has not taken yet is replaced (its time carried over)
                bool fresh = _pending == null;
                var job = _pending ?? (_jobs[0] != _running ? _jobs[0] : _jobs[1]);
                job.Dt = (fresh ? 0f : job.Dt) + dt;
                job.Reset = (fresh ? false : job.Reset) | reset;
                if (job.Emitters.Length < h.EmitterCount) job.Emitters = new FluidEmitterRecord[h.EmitterCount];
                Array.Copy(_em, job.Emitters, h.EmitterCount);
                job.EmitterCount = h.EmitterCount;
                if (job.Colliders.Length < h.ColliderCount) job.Colliders = new FluidColliderRecord[h.ColliderCount];
                Array.Copy(_col, job.Colliders, h.ColliderCount);
                job.ColliderCount = h.ColliderCount;
                job.VertexBuffer = _vbPtr; job.IndexBuffer = _ibPtr;
                job.Capacity = Math.Max(256, Math.Min(h.MaxParticles, GpuFluidSim.MaxCapacity));
                job.ParticleSize = h.ParticleSize > 0.0049f && h.ParticleSize < 0.5001f ? h.ParticleSize : 0.025f;
                job.SurfaceDetail = h.SurfaceDetail >= 0.5f && h.SurfaceDetail <= 3f ? h.SurfaceDetail : 1f;
                job.DropletSize = h.DropletSize >= 0.5f && h.DropletSize <= 3f ? h.DropletSize : 1.5f;
                job.Classic = (h.Flags & 1) != 0;
                if (job.Gas.Length < h.GasEmitterCount) job.Gas = new GasEmitterRecord[h.GasEmitterCount];
                if (h.GasEmitterCount > 0) Array.Copy(_gas, job.Gas, h.GasEmitterCount);
                job.GasCount = h.GasEmitterCount;
                job.Head = new System.Numerics.Vector3(h.HeadX, h.HeadY, h.HeadZ);
                job.GasVertexBuffer = _gasVbPtr; job.GasIndexBuffer = _gasIbPtr;
                job.FoamVertexBuffer = _foamVbPtr; job.FoamIndexBuffer = _foamIbPtr;
                job.GasQuads = _smokeMat != null; job.FoamQuads = _foamMat != null;
                job.TowardsLight = _towardsLight;
                _pending = job;
            }
            if (!_queued)
            {
                _queued = true;
                RenderingManager.Instance.AssetIntegrator.EnqueueRenderThreadProcessing(RenderThread);
            }
            WriteBack();
        }

        static IntPtr _vbPtr, _ibPtr;

        static void EnsureMesh()
        {
            if (_mesh != null) return;
            _mesh = new Mesh { name = "Softbody GPU fluid" };
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.SetVertexBufferParams(VertexCapacity,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, 0));
            _mesh.SetIndexBufferParams(IndexCapacity, IndexFormat.UInt32);
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            _mesh.SetVertexBufferData(new float[VertexCapacity * 10], 0, 0, VertexCapacity * 10, 0, flags);
            _mesh.SetIndexBufferData(new uint[IndexCapacity], 0, 0, IndexCapacity, flags);
            _mesh.subMeshCount = 1;
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, IndexCapacity, MeshTopology.Triangles), flags);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e4f, 1e4f, 1e4f));
            _mesh.UploadMeshData(false);
            // (these wait for the render thread once; the buffers stay for the mesh's lifetime)
            _vbPtr = _mesh.GetNativeVertexBufferPtr(0);
            _ibPtr = _mesh.GetNativeIndexBufferPtr();
            Debug.Log(Tag + $"surface mesh ready ({VertexCapacity} vertices, {IndexCapacity} indices)");
        }

        static void FindTarget(int meshId)
        {
            bool alive = _target != null && _target.Renderer != null && _target.Filter != null && _target.Filter.sharedMesh != null;
            if (alive && meshId == _targetMeshId) return;
            _searchTimer -= Time.unscaledDeltaTime;
            if (meshId == _targetMeshId && _searchTimer > 0f) return;
            _searchTimer = 0.5f;
            _targetMeshId = meshId;
            _target = null;
            if (meshId < 0 || !RenderingManager.Instance.Meshes.TryGetAsset(meshId, out var asset) || asset.Mesh == null) return;
            var want = asset.Mesh;
            foreach (var space in RenderingManager.Instance.RenderSpaces)
            {
                var meshes = space.Meshes;
                for (int i = 0; i < meshes.RenderableCount; i++)
                {
                    var r = meshes[i];
                    if (r?.Filter != null && r.Filter.sharedMesh == want && r.Renderer != null) { _target = r; break; }
                }
                if (_target != null) break;
            }
            if (_target != null) Debug.Log(Tag + "drawing with the plugin's fluid renderer (mesh asset " + meshId + ")");
        }

        static void KeepDrawing()
        {
            if (_mesh == null) return;
            if (_target == null || _target.Renderer == null)
            {
                if (_go != null && _go.activeSelf) _go.SetActive(false);
                _drawing = 0;
                return;
            }
            if (_go == null)
            {
                _go = new GameObject("Softbody GPU fluid");
                _filter = _go.AddComponent<MeshFilter>();
                _renderer = _go.AddComponent<MeshRenderer>();
                _filter.sharedMesh = _mesh;
            }
            var parent = _target.Renderer.transform;
            if (_go.transform.parent != parent)
            {
                _go.transform.SetParent(parent, false);
                _go.transform.localPosition = Vector3.zero; _go.transform.localRotation = Quaternion.identity; _go.transform.localScale = Vector3.one;
            }
            _go.layer = parent.gameObject.layer;
            var mats = _target.Renderer.sharedMaterials;
            if (!SameMaterials(mats, _lastMaterials)) { _renderer.sharedMaterials = mats; _lastMaterials = mats; }
            _renderer.shadowCastingMode = _target.Renderer.shadowCastingMode;
            _renderer.receiveShadows = _target.Renderer.receiveShadows;
            _renderer.sortingOrder = _target.Renderer.sortingOrder;
            bool show = _target.Renderer.gameObject.activeInHierarchy;
            if (_go.activeSelf != show) _go.SetActive(show);
            _drawing = show ? 1 : 0;
        }

        static bool SameMaterials(Material[] a, Material[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Hide()
        {
            if (_go != null && _go.activeSelf) _go.SetActive(false);
            _drawing = 0;
            if (_gasGo != null && _gasGo.activeSelf) _gasGo.SetActive(false);
            _gasDrawing = 0;
            if (_foamGo != null && _foamGo.activeSelf) _foamGo.SetActive(false);
            _foamDrawing = 0;
        }

        // ------------------------------------------------------------------ 0.26.0: the gas's mesh and renderer (as the surface's)

        static void EnsureGasMesh()
        {
            if (_gasMesh != null) return;
            _gasMesh = new Mesh { name = "Softbody GPU gas" };
            _gasMesh.indexFormat = IndexFormat.UInt32;
            // (0.27.0: plus texcoord0, the smoke shader's corner, random number and age: GpuFluidSim.GasVertexBytes)
            _gasMesh.SetVertexBufferParams(GasVertexCapacity,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 0));
            _gasMesh.SetIndexBufferParams(GasIndexCapacity, IndexFormat.UInt32);
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            _gasMesh.SetVertexBufferData(new float[GasVertexCapacity * GpuFluidSim.GasVertexFloats], 0, 0, GasVertexCapacity * GpuFluidSim.GasVertexFloats, 0, flags);
            _gasMesh.SetIndexBufferData(new uint[GasIndexCapacity], 0, 0, GasIndexCapacity, flags);
            _gasMesh.subMeshCount = 1;
            _gasMesh.SetSubMesh(0, new SubMeshDescriptor(0, GasIndexCapacity, MeshTopology.Triangles), flags);
            _gasMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e4f, 1e4f, 1e4f));
            _gasMesh.UploadMeshData(false);
            _gasVbPtr = _gasMesh.GetNativeVertexBufferPtr(0);
            _gasIbPtr = _gasMesh.GetNativeIndexBufferPtr();
            Debug.Log(Tag + $"gas mesh ready ({GasVertexCapacity} vertices, {GasIndexCapacity} indices)");
        }

        static void FindGasTarget(int meshId)
        {
            bool alive = _gasTarget != null && _gasTarget.Renderer != null && _gasTarget.Filter != null && _gasTarget.Filter.sharedMesh != null;
            if (alive && meshId == _gasTargetMeshId) return;
            _gasSearchTimer -= Time.unscaledDeltaTime;
            if (meshId == _gasTargetMeshId && _gasSearchTimer > 0f) return;
            _gasSearchTimer = 0.5f;
            _gasTargetMeshId = meshId;
            _gasTarget = null;
            if (meshId < 0 || !RenderingManager.Instance.Meshes.TryGetAsset(meshId, out var asset) || asset.Mesh == null) return;
            var want = asset.Mesh;
            foreach (var space in RenderingManager.Instance.RenderSpaces)
            {
                var meshes = space.Meshes;
                for (int i = 0; i < meshes.RenderableCount; i++)
                {
                    var r = meshes[i];
                    if (r?.Filter != null && r.Filter.sharedMesh == want && r.Renderer != null) { _gasTarget = r; break; }
                }
                if (_gasTarget != null) break;
            }
            if (_gasTarget != null) Debug.Log(Tag + "drawing the gas with the plugin's gas renderer (mesh asset " + meshId + ")");
        }

        static void KeepDrawingGas()
        {
            if (_gasMesh == null) return;
            if (_gasTarget == null || _gasTarget.Renderer == null)
            {
                if (_gasGo != null && _gasGo.activeSelf) _gasGo.SetActive(false);
                _gasDrawing = 0;
                return;
            }
            if (_gasGo == null)
            {
                _gasGo = new GameObject("Softbody GPU gas");
                _gasFilter = _gasGo.AddComponent<MeshFilter>();
                _gasRenderer = _gasGo.AddComponent<MeshRenderer>();
                _gasFilter.sharedMesh = _gasMesh;
            }
            var parent = _gasTarget.Renderer.transform;
            if (_gasGo.transform.parent != parent)
            {
                _gasGo.transform.SetParent(parent, false);
                _gasGo.transform.localPosition = Vector3.zero; _gasGo.transform.localRotation = Quaternion.identity; _gasGo.transform.localScale = Vector3.one;
            }
            _gasGo.layer = parent.gameObject.layer;
            // 0.27.0: the smoke shader's material when it loaded, else the placeholder's own (Unlit: the old discs)
            var mats = _smokeMat != null ? _smokeMats : _gasTarget.Renderer.sharedMaterials;
            if (!SameMaterials(mats, _gasLastMaterials)) { _gasRenderer.sharedMaterials = mats; _gasLastMaterials = mats; }
            _gasRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _gasRenderer.receiveShadows = false;
            _gasRenderer.sortingOrder = _gasTarget.Renderer.sortingOrder;
            bool show = _gasTarget.Renderer.gameObject.activeInHierarchy;
            if (_gasGo.activeSelf != show) _gasGo.SetActive(show);
            _gasDrawing = show ? 1 : 0;
        }

        // ------------------------------------------------------------------ 0.27.0: the spray, foam and bubbles' mesh (as the gas's)

        static void EnsureFoamMesh()
        {
            if (_foamMesh != null) return;
            _foamMesh = new Mesh { name = "Softbody GPU foam" };
            _foamMesh.indexFormat = IndexFormat.UInt32;
            _foamMesh.SetVertexBufferParams(FoamVertexCapacity,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, 0),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 0));
            _foamMesh.SetIndexBufferParams(FoamIndexCapacity, IndexFormat.UInt32);
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            _foamMesh.SetVertexBufferData(new float[FoamVertexCapacity * GpuFluidSim.GasVertexFloats], 0, 0, FoamVertexCapacity * GpuFluidSim.GasVertexFloats, 0, flags);
            _foamMesh.SetIndexBufferData(new uint[FoamIndexCapacity], 0, 0, FoamIndexCapacity, flags);
            _foamMesh.subMeshCount = 1;
            _foamMesh.SetSubMesh(0, new SubMeshDescriptor(0, FoamIndexCapacity, MeshTopology.Triangles), flags);
            _foamMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e4f, 1e4f, 1e4f));
            _foamMesh.UploadMeshData(false);
            _foamVbPtr = _foamMesh.GetNativeVertexBufferPtr(0);
            _foamIbPtr = _foamMesh.GetNativeIndexBufferPtr();
            Debug.Log(Tag + $"foam mesh ready ({FoamVertexCapacity} vertices, {FoamIndexCapacity} indices)");
        }

        /// <summary>Under the gas placeholder's renderer, with its material (the plugin makes that placeholder for foam too), drawn
        /// just after the gas (sorting order + 1).</summary>
        static void KeepDrawingFoam()
        {
            if (_foamMesh == null) return;
            if (_gasTarget == null || _gasTarget.Renderer == null)
            {
                if (_foamGo != null && _foamGo.activeSelf) _foamGo.SetActive(false);
                _foamDrawing = 0;
                return;
            }
            if (_foamGo == null)
            {
                _foamGo = new GameObject("Softbody GPU foam");
                _foamFilter = _foamGo.AddComponent<MeshFilter>();
                _foamRenderer = _foamGo.AddComponent<MeshRenderer>();
                _foamFilter.sharedMesh = _foamMesh;
            }
            var parent = _gasTarget.Renderer.transform;
            if (_foamGo.transform.parent != parent)
            {
                _foamGo.transform.SetParent(parent, false);
                _foamGo.transform.localPosition = Vector3.zero; _foamGo.transform.localRotation = Quaternion.identity; _foamGo.transform.localScale = Vector3.one;
            }
            _foamGo.layer = parent.gameObject.layer;
            var mats = _foamMat != null ? _foamMats : _gasTarget.Renderer.sharedMaterials;   // (0.27.0: as the gas's)
            if (!SameMaterials(mats, _foamLastMaterials)) { _foamRenderer.sharedMaterials = mats; _foamLastMaterials = mats; }
            _foamRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _foamRenderer.receiveShadows = false;
            _foamRenderer.sortingOrder = _gasTarget.Renderer.sortingOrder + 1;
            bool show = _gasTarget.Renderer.gameObject.activeInHierarchy;
            if (_foamGo.activeSelf != show) _foamGo.SetActive(show);
            _foamDrawing = show ? 1 : 0;
        }


        // ------------------------------------------------------------------ 0.27.0: the smoke shader

        static Material[] _smokeMats, _foamMats;

        /// <summary>Loads the smoke shader's bundle once (StreamingAssets/Softbody/softbody_smoke.bundle, which make_softbody_renderer.py
        /// copies in): its smoke and foam materials. Without it (or if it fails) the gas and foam keep the plugin's Unlit discs.</summary>
        static void EnsureSmoke()
        {
            if (_smokeTried) return;
            _smokeTried = true;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "Softbody", SmokeBundleName);
                if (!File.Exists(path)) { Debug.Log(Tag + "no smoke shader at " + path + ": the smoke and foam keep the plugin's Unlit material"); return; }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { Debug.LogWarning(Tag + "could not load the smoke shader bundle " + path + ": the smoke keeps the Unlit material"); return; }
                Material smoke = null, foam = null;
                foreach (var m in bundle.LoadAllAssets<Material>())
                {
                    if (string.Equals(m.name, "SoftbodySmoke", StringComparison.OrdinalIgnoreCase)) smoke = m;
                    else if (string.Equals(m.name, "SoftbodyFoam", StringComparison.OrdinalIgnoreCase)) foam = m;
                }
                if (smoke == null || smoke.shader == null || !smoke.shader.isSupported)
                {
                    Debug.LogWarning(Tag + "the smoke shader bundle has no usable smoke material (" + (smoke?.shader == null ? "none" : smoke.shader.name + " not supported") + "): the smoke keeps the Unlit material");
                    return;
                }
                _smokeMat = new Material(smoke) { name = "Softbody smoke" };
                _smokeMats = new[] { _smokeMat };
                if (foam != null && foam.shader != null && foam.shader.isSupported) { _foamMat = new Material(foam) { name = "Softbody foam" }; _foamMats = new[] { _foamMat }; }
                _depthOkId = Shader.PropertyToID("_SoftbodyDepthOK");
                Camera.onPreRender += OnCameraPreRender;
                Debug.Log(Tag + "smoke shader loaded (" + smoke.shader.name + "; foam " + (_foamMat != null ? "too" : "keeps the Unlit material") + ")");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(Tag + "the smoke shader failed to load (the smoke keeps the Unlit material): " + ex.Message);
                _smokeMat = null; _foamMat = null; _smokeMats = null; _foamMats = null;
            }
        }

        /// <summary>Before each camera renders: whether it renders a depth texture, for the smoke's soft edges at surfaces.</summary>
        static void OnCameraPreRender(Camera cam)
        {
            if (cam == null) return;
            bool depth = (cam.depthTextureMode & DepthTextureMode.Depth) != 0 || cam.actualRenderingPath == RenderingPath.DeferredShading;
            Shader.SetGlobalFloat(_depthOkId, depth ? 1f : 0f);
        }

        /// <summary>Towards the world's main directional light (the brightest one lighting the gas's layer) in the gas's space, for the
        /// smoke's shading through itself; Unity lights the smoke shader with that light too (ForwardBase's main light). Twice a second.</summary>
        static void KeepTowardsLight()
        {
            _lightTimer -= Time.unscaledDeltaTime;
            if (_lightTimer > 0f) return;
            _lightTimer = 0.5f;
            _towardsLight = default;
            if (_gasTarget == null || _gasTarget.Renderer == null) return;
            var space = _gasTarget.Renderer.transform;
            Light best = null;
            float bestI = 0f;
            foreach (var l in Light.GetLights(LightType.Directional, space.gameObject.layer))
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                var c = l.color;
                float i = l.intensity * Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (i > bestI) { bestI = i; best = l; }
            }
            if (best == null) return;
            var d = space.InverseTransformDirection(-best.transform.forward);
            if (!(d.sqrMagnitude > 1e-8f)) return;
            d.Normalize();
            _towardsLight = new System.Numerics.Vector3(d.x, d.y, d.z);
        }

        /// <summary>The gas mesh's bounds: where the GPU last saw puffs, and the gas emitters, 3 m round (the readback is a few frames
        /// old). So Unity picks the lights near the smoke for it, and skips it out of view (0.26.0: 10 km everywhere).</summary>
        static void KeepGasBounds(int gasCount)
        {
            if (_gasMesh == null) return;
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            lock (_statsLock) { if (_sGasMin.x <= _sGasMax.x) { lo = _sGasMin; hi = _sGasMax; } }
            for (int i = 0; _gas != null && i < gasCount && i < _gas.Length; i++)
            {
                if (_gas[i].Active == 0) continue;
                var p = new Vector3(_gas[i].PosX, _gas[i].PosY, _gas[i].PosZ);
                if (float.IsNaN(p.x + p.y + p.z) || float.IsInfinity(p.x + p.y + p.z)) continue;
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            if (!(lo.x <= hi.x)) { _gasMesh.bounds = new Bounds(Vector3.zero, Vector3.one); return; }
            _gasMesh.bounds = new Bounds((lo + hi) * 0.5f, hi - lo + new Vector3(6f, 6f, 6f));
        }

        static void WriteBack()
        {
            if (_io == null) return;
            var b = new FluidBack();
            int pairs;
            lock (_statsLock)
            {
                b.Frame = _sFrame; b.Ready = _ready && !_failed ? 1 : 0; b.Particles = _sParticles;
                b.BoundsMinX = _sMin.x; b.BoundsMinY = _sMin.y; b.BoundsMinZ = _sMin.z;
                b.BoundsMaxX = _sMax.x; b.BoundsMaxY = _sMax.y; b.BoundsMaxZ = _sMax.z;
                b.StepMs = _sGpuMs; b.CpuMs = _sCpuMs; b.SurfaceVertices = _sVertices; b.Error = _sError;
                b.ParticleSize = _sSize; b.SurfaceTruncated = _sTruncated ? 1 : 0; b.SurfaceDetailUsed = _sDetailUsed;
                b.GasPuffs = _sGasPuffs;
                b.GasMinX = _sGasMin.x; b.GasMinY = _sGasMin.y; b.GasMinZ = _sGasMin.z;
                b.GasMaxX = _sGasMax.x; b.GasMaxY = _sGasMax.y; b.GasMaxZ = _sGasMax.z;
                b.FoamParticles = _sFoam; b.ThickIterations = _sThickIterations; b.ThickUnconverged = _sThickUnconverged; b.Vortices = _sVortices;
                pairs = _sPairCount;
                Array.Copy(_sPairs, _backPairs, pairs * 2);
            }
            b.Owner = _owner; b.Drawing = _drawing; b.GasDrawing = _gasDrawing; b.FoamDrawing = _foamDrawing;
            _io.WriteBack(b, _backPairs, pairs);
        }

        static readonly int[] _backPairs = new int[FluidChannel.MaxEmitters * 2];

        // ------------------------------------------------------------------ the render thread

        static void RenderThread()
        {
            Job job;
            lock (_lock) { job = _pending; _pending = null; _queued = false; _running = job; }
            if (job == null || _failed) { lock (_lock) _running = null; return; }
            try
            {
                var device = AssetIntegrator._dx11device;
                // a new particle size (or cap) is a new simulation: the liquid starts over
                if (_rt == null || _rtCapacity != job.Capacity || Math.Abs(_rtSize - job.ParticleSize) > 1e-5f)
                {
                    _rt?.Dispose();
                    _rt = null;
                    _rt = new GpuFluidRuntime(device, new GpuFluidConfig { Capacity = job.Capacity, ParticleSize = job.ParticleSize, VertexCapacity = VertexCapacity, IndexCapacity = IndexCapacity }, null, name => _bytecode[name]);
                    _rtCapacity = job.Capacity; _rtSize = job.ParticleSize;
                    Debug.Log(Tag + $"GPU fluid running ({job.Capacity} particles at most, {job.ParticleSize * 100:0.#} cm each) on {device.FeatureLevel}");
                }
                if (job.Reset) _rt.Reset();
                if (job.VertexBuffer != _unityVbPtr) { _unityVb = Wrap(job.VertexBuffer); _unityVbPtr = job.VertexBuffer; CheckUsage(_unityVb, "vertex"); }
                if (job.IndexBuffer != _unityIbPtr) { _unityIb = Wrap(job.IndexBuffer); _unityIbPtr = job.IndexBuffer; CheckUsage(_unityIb, "index"); }
                _rt.Sim.Config.Classic = job.Classic;   // 0.25.0: live (everything wakes when it changes)
                if (job.GasVertexBuffer != _unityGasVbPtr) { _unityGasVb = Wrap(job.GasVertexBuffer); _unityGasVbPtr = job.GasVertexBuffer; CheckUsage(_unityGasVb, "gas vertex"); }
                if (job.GasIndexBuffer != _unityGasIbPtr) { _unityGasIb = Wrap(job.GasIndexBuffer); _unityGasIbPtr = job.GasIndexBuffer; CheckUsage(_unityGasIb, "gas index"); }
                if (job.FoamVertexBuffer != _unityFoamVbPtr) { _unityFoamVb = Wrap(job.FoamVertexBuffer); _unityFoamVbPtr = job.FoamVertexBuffer; CheckUsage(_unityFoamVb, "foam vertex"); }
                if (job.FoamIndexBuffer != _unityFoamIbPtr) { _unityFoamIb = Wrap(job.FoamIndexBuffer); _unityFoamIbPtr = job.FoamIndexBuffer; CheckUsage(_unityFoamIb, "foam index"); }
                _rt.Frame(job.Dt, job.Emitters, job.EmitterCount, job.Colliders, job.ColliderCount, true, job.SurfaceDetail, job.DropletSize, job.Gas, job.GasCount, job.Head,
                          job.TowardsLight, job.GasQuads, job.FoamQuads);
                var ctx = device.ImmediateContext;
                if (_unityVb != null && _unityIb != null)
                {
                    int vb = Math.Min(_unityVb.Description.SizeInBytes, _rt.Sim.VertexBuffer.Description.SizeInBytes);
                    int ib = Math.Min(_unityIb.Description.SizeInBytes, _rt.Sim.IndexBuffer.Description.SizeInBytes);
                    ctx.CopySubresourceRegion(_rt.Sim.VertexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, vb, 1, 1), _unityVb, 0);
                    ctx.CopySubresourceRegion(_rt.Sim.IndexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, ib, 1, 1), _unityIb, 0);
                }
                // 0.26.0: the gas
                if (_unityGasVb != null && _unityGasIb != null)
                {
                    int gvb = Math.Min(_unityGasVb.Description.SizeInBytes, _rt.Sim.GasVertexBuffer.Description.SizeInBytes);
                    int gib = Math.Min(_unityGasIb.Description.SizeInBytes, _rt.Sim.GasIndexBuffer.Description.SizeInBytes);
                    ctx.CopySubresourceRegion(_rt.Sim.GasVertexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, gvb, 1, 1), _unityGasVb, 0);
                    ctx.CopySubresourceRegion(_rt.Sim.GasIndexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, gib, 1, 1), _unityGasIb, 0);
                }
                // 0.27.0: the spray, foam and bubbles. Once the foam mesh exists the simulation keeps full-size foam buffers (a new
                // simulation, after a new particle size, has only a token until a liquid asks again: a copy of that would leave the old
                // discs drawn past it), and they are copied every frame, as the gas's
                if (_unityFoamVb != null && _unityFoamIb != null)
                {
                    _rt.Sim.EnsureFoamBuffers();
                    int fvb = Math.Min(_unityFoamVb.Description.SizeInBytes, _rt.Sim.DiffuseVertexBuffer.Description.SizeInBytes);
                    int fib = Math.Min(_unityFoamIb.Description.SizeInBytes, _rt.Sim.DiffuseIndexBuffer.Description.SizeInBytes);
                    ctx.CopySubresourceRegion(_rt.Sim.DiffuseVertexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, fvb, 1, 1), _unityFoamVb, 0);
                    ctx.CopySubresourceRegion(_rt.Sim.DiffuseIndexBuffer, 0, new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, fib, 1, 1), _unityFoamIb, 0);
                }
                var st = _rt.Stats;
                lock (_statsLock)
                {
                    _sFrame = _lastFrame; _sParticles = st.Count; _sVertices = st.Vertices; _sIndices = st.Indices;
                    _sMin = new Vector3(st.Min.X, st.Min.Y, st.Min.Z); _sMax = new Vector3(st.Max.X, st.Max.Y, st.Max.Z);
                    _sGpuMs = (float)st.GpuMs; _sCpuMs = (float)_rt.CpuMs;
                    _sSize = _rt.Sim.Spacing; _sTruncated = st.SurfaceTruncated; _sDetailUsed = _rt.Sim.SurfaceDetailUsed;
                    _sGasPuffs = st.GasPuffs;
                    _sFoam = st.DiffuseLive; _sThickIterations = st.IvIterationsMax; _sThickUnconverged = st.IvUnconverged; _sVortices = st.Vortices;   // 0.27.0
                    if (st.HasGasBounds) { _sGasMin = new Vector3(st.GasMin.X, st.GasMin.Y, st.GasMin.Z); _sGasMax = new Vector3(st.GasMax.X, st.GasMax.Y, st.GasMax.Z); }
                    else { _sGasMin = new Vector3(1, 1, 1); _sGasMax = new Vector3(-1, -1, -1); }
                    int n = 0;
                    foreach (int id in _rt.Director.Ids)
                    {
                        if (n >= FluidChannel.MaxEmitters) break;
                        _sPairs[2 * n] = id; _sPairs[2 * n + 1] = _rt.CountOf(id); n++;
                    }
                    _sPairCount = n;
                }
                if (!_ready)
                {
                    _ready = true;
                    try { _handshake = new EventWaitHandle(false, EventResetMode.ManualReset, FluidChannel.HandshakeName); }
                    catch (Exception ex) { Debug.LogWarning(Tag + "could not open the handshake: " + ex.Message); }
                    Debug.Log(Tag + "ready; handshake " + FluidChannel.HandshakeName + " open");
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                lock (_statsLock) _sError = 2;
                Debug.LogError(Tag + "the GPU fluid failed and stops (the plugin goes back to the CPU): " + ex);
                try { _handshake?.Dispose(); } catch { }
                _handshake = null;
            }
            finally { lock (_lock) _running = null; }
        }

        // a wrapper that holds its own reference to Unity's buffer (so disposing it never frees Unity's)
        static D3DBuffer Wrap(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            System.Runtime.InteropServices.Marshal.AddRef(p);
            return new D3DBuffer(p);
        }

        static void CheckUsage(D3DBuffer b, string what)
        {
            if (b == null) return;
            var d = b.Description;
            if (d.Usage != SharpDX.Direct3D11.ResourceUsage.Default)
                throw new InvalidOperationException($"Unity's {what} buffer is {d.Usage}, not Default: the GPU cannot copy the surface into it");
            Debug.Log(Tag + $"{what} buffer: {d.SizeInBytes} bytes, {d.Usage}, {d.BindFlags}");
        }
    }
}
