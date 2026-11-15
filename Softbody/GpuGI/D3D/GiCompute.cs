// Live bounce light (GI): the GPU side, on the renderer's own Direct3D 11 device (SharpDX 4.2), with SoftbodyGI.hlsl compiled
// at run time like the fluid's kernels (HlslCompiler). It owns the voxels, their bricks, the probes and two probe volumes (the
// textures Resonite's lit shaders read per pixel through unity_ProbeVolumeSH); Frame runs one frame's share of the work, as
// GiPlanner decided it, and never waits for the GPU. It runs on whatever thread owns the device context (the renderer's render
// thread; the offline tests' own thread). netstandard2.0: the renderer fork and the offline tests compile the same file.
#nullable disable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using Softbody.FluidGpu;
using Format = SharpDX.DXGI.Format;
using Device = SharpDX.Direct3D11.Device;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Softbody.GI
{
    public sealed class GiCompute : IDisposable
    {
        public static readonly string[] KernelNames = { "ClearAccum", "Resolve", "BuildBricks", "ClearGlow", "ListGlow", "Inject", "TraceProbes", "WriteVolume", "ShiftVoxels", "ShiftProbes" };

        const int Group = 64;

        // GiParams in SoftbodyGI.hlsl (480 bytes)
        [StructLayout(LayoutKind.Sequential)]
        struct Params
        {
            public Vector4 VoxOrigin;
            public Int4 VoxDim, SlabOrigin, SlabDim;
            public Vector4 ProbeOrigin;
            public Int4 ProbeDim, ProbeRange, InjectRange, Shift;
            public Vector4 Misc, Rot0, Rot1, Rot2;
            public Vector4 SkyRad0, SkyRad1, SkyRad2, SkyRad3, SkyRad4, SkyRad5, SkyRad6;
            public Vector4 SkyVal0, SkyVal1, SkyVal2, SkyVal3, SkyVal4, SkyVal5, SkyVal6;
            public Vector4 WinOrigin;
            public Int4 WinDim;
            public Int4 Glow;
        }

        sealed class Kernel
        {
            public string Name;
            public ComputeShader Shader;
            public List<HlslBinding> Bindings;
        }

        sealed class Res
        {
            public D3DBuffer Buffer;
            public UnorderedAccessView Uav;
            public ShaderResourceView Srv;
            public int Elements, Stride;
            public void Dispose() { Uav?.Dispose(); Srv?.Dispose(); Buffer?.Dispose(); Uav = null; Srv = null; Buffer = null; }
        }

        sealed class Slot
        {
            public Query Disjoint, T0, T1;
            public bool Pending;
        }

        public readonly GiConfig Config;
        readonly Device _device;
        readonly DeviceContext _ctx;
        readonly Dictionary<string, Kernel> _kernels = new Dictionary<string, Kernel>();
        Res _accum, _bricks, _brickGlow, _glowList, _lights;
        readonly Res[] _vox = new Res[2], _probes = new Res[2];
        int _voxCur, _probeCur;
        bool _ownAccum = true;
        readonly Texture3D[] _vol = new Texture3D[2];
        readonly UnorderedAccessView[] _volUav = new UnorderedAccessView[2];
        UnorderedAccessView _volTarget;
        D3DBuffer _cb;
        Params _p;
        readonly UnorderedAccessView[] _uavs = new UnorderedAccessView[8];
        readonly ShaderResourceView[] _srvs = new ShaderResourceView[8];
        readonly Slot[] _ring = new Slot[4];
        int _ringNext;
        GiLight[] _lightScratch;

        public readonly GiStats Stats = new GiStats();

        /// <summary>Shadow rays a probe aims at glowing voxels (0 to 8; 0: only the rays see the glow). Starts at Config.GlowRays;
        /// set it on the device's thread, between frames.</summary>
        public int GlowRays;

        /// <summary>The volumes' size in texels: 4 * ProbeX wide (SHAr, SHAg, SHAb, occlusion side by side), ProbeY high, ProbeZ deep.</summary>
        public int VolumeWidth => 4 * Config.ProbeX;
        public int VolumeHeight => Config.ProbeY;
        public int VolumeDepth => Config.ProbeZ;
        public Texture3D Volume(int i) => _vol[i];
        /// <summary>The buffer the voxelize shader adds a slab into: 10 uints a voxel (count, albedo rgb * 255, emission rgb * 256,
        /// normal xyz * 64), in the slab's own order ((z * VoxY + y) * VoxX + x).</summary>
        public D3DBuffer AccumBuffer => _accum?.Buffer;

        public GiCompute(Device device, GiConfig config, string hlsl, Func<string, byte[]> cachedBytecode = null)
        {
            Config = config.Clone();
            Config.Validate();
            _device = device;
            _ctx = device.ImmediateContext;
            try
            {
                foreach (var name in KernelNames)
                {
                    var code = cachedBytecode?.Invoke(name) ?? HlslCompiler.Compile(hlsl, name, "SoftbodyGI.hlsl");
                    _kernels[name] = new Kernel { Name = name, Shader = new ComputeShader(device, code), Bindings = HlslCompiler.Reflect(code) };
                }
                var c = Config;
                _accum = Structured(c.SlabVoxels * 10, 4, true);
                _vox[0] = Structured(c.Voxels, 16, true);
                _vox[1] = Structured(c.Voxels, 16, true);
                _bricks = Structured(c.Voxels / 64, 4, true);
                _brickGlow = Structured(c.Voxels / 64, 16, true);
                _glowList = Structured(c.Voxels / 64 + 1, 4, true);
                GlowRays = c.GlowRays;
                _probes[0] = Structured(c.Probes * 4, 16, true);
                _probes[1] = Structured(c.Probes * 4, 16, true);
                _lights = Structured(c.MaxLights, Marshal.SizeOf(typeof(GiLight)), false, dynamic: true);
                _lightScratch = new GiLight[c.MaxLights];
                for (int i = 0; i < 2; i++)
                {
                    _vol[i] = new Texture3D(device, new Texture3DDescription
                    {
                        Width = VolumeWidth, Height = VolumeHeight, Depth = VolumeDepth, MipLevels = 1,
                        Format = Format.R16G16B16A16_Float, Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource, CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None,
                    });
                    _volUav[i] = new UnorderedAccessView(device, _vol[i]);
                    _ctx.ClearUnorderedAccessView(_volUav[i], new SharpDX.Mathematics.Interop.RawVector4(0f, 0f, 0f, 0f));
                }
                int cbBytes = Marshal.SizeOf(typeof(Params));
                if (cbBytes % 16 != 0) throw new InvalidOperationException("GiParams is " + cbBytes + " bytes");
                _cb = new D3DBuffer(device, new BufferDescription(cbBytes, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
                for (int i = 0; i < _ring.Length; i++)
                    _ring[i] = new Slot
                    {
                        Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint }),
                        T0 = new Query(device, new QueryDescription { Type = QueryType.Timestamp }),
                        T1 = new Query(device, new QueryDescription { Type = QueryType.Timestamp }),
                    };
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Compiles every kernel (to compile off the render thread).</summary>
        public static Dictionary<string, byte[]> CompileAll(string hlsl)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var name in KernelNames) d[name] = HlslCompiler.Compile(hlsl, name, "SoftbodyGI.hlsl");
            return d;
        }

        /// <summary>Uses Unity's ComputeBuffer (the voxelize shader's random-write target) as the slab buffer: a structured buffer of
        /// uints, SlabVoxels * 10 long. The caller wraps it with its own reference; this takes the wrapper over.</summary>
        public void UseAccum(D3DBuffer unityBuffer)
        {
            var d = unityBuffer.Description;
            int elements = d.SizeInBytes / 4;
            if (elements < Config.SlabVoxels * 10) throw new ArgumentException($"the slab buffer holds {elements} uints, not {Config.SlabVoxels * 10}");
            if ((d.OptionFlags & ResourceOptionFlags.BufferStructured) == 0 || d.StructureByteStride != 4)
                throw new ArgumentException($"the slab buffer is not a structured buffer of uints ({d.OptionFlags}, stride {d.StructureByteStride})");
            if ((d.BindFlags & BindFlags.UnorderedAccess) == 0 || (d.BindFlags & BindFlags.ShaderResource) == 0)
                throw new ArgumentException($"the slab buffer cannot be read and written by compute ({d.BindFlags})");
            if (_ownAccum) _accum?.Dispose();
            else if (_accum != null && _accum.Buffer != unityBuffer) { _accum.Uav?.Dispose(); _accum.Srv?.Dispose(); _accum.Buffer?.Dispose(); }
            _accum = new Res { Buffer = unityBuffer, Elements = elements, Stride = 4 };
            _accum.Srv = new ShaderResourceView(_device, unityBuffer, new ShaderResourceViewDescription
            {
                Format = Format.Unknown, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Buffer,
                Buffer = new ShaderResourceViewDescription.BufferResource { FirstElement = 0, ElementCount = elements },
            });
            _accum.Uav = new UnorderedAccessView(_device, unityBuffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown, Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = elements, Flags = UnorderedAccessViewBufferFlags.None },
            });
            _ownAccum = false;
            Zero(_accum);
        }

        Res Structured(int elements, int stride, bool uav, bool dynamic = false)
        {
            var r = new Res { Elements = Math.Max(1, elements), Stride = stride };
            var bind = BindFlags.ShaderResource | (uav ? BindFlags.UnorderedAccess : BindFlags.None);
            r.Buffer = new D3DBuffer(_device, new BufferDescription(r.Elements * stride, dynamic ? ResourceUsage.Dynamic : ResourceUsage.Default, bind,
                dynamic ? CpuAccessFlags.Write : CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, stride));
            r.Srv = new ShaderResourceView(_device, r.Buffer, new ShaderResourceViewDescription
            {
                Format = Format.Unknown, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Buffer,
                Buffer = new ShaderResourceViewDescription.BufferResource { FirstElement = 0, ElementCount = r.Elements },
            });
            if (uav)
            {
                r.Uav = new UnorderedAccessView(_device, r.Buffer, new UnorderedAccessViewDescription
                {
                    Format = Format.Unknown, Dimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = r.Elements, Flags = UnorderedAccessViewBufferFlags.None },
                });
                Zero(r);   // a new buffer's contents are undefined in Direct3D 11
            }
            return r;
        }

        void Zero(Res r) => _ctx.ClearUnorderedAccessView(r.Uav, new SharpDX.Mathematics.Interop.RawInt4(0, 0, 0, 0));

        // ------------------------------------------------------------------ one frame

        /// <summary>
        /// One frame's work: re-centring (or starting over), the slab the voxelize shader drew (plan.Slab), lighting voxels again,
        /// tracing probes, and writing the volumes in <paramref name="windows"/>. <paramref name="skyValue"/>: the world's ambient as
        /// Unity's 7 probe constants (unity_SHAr ... unity_SHC). Restores the compute stage as it found it.
        /// </summary>
        public void Frame(GiFramePlan plan, GiLight[] lights, int lightCount, Vector4[] skyValue, IList<GiWindow> windows)
        {
            var c = Config;
            lightCount = Math.Max(0, Math.Min(lightCount, Math.Min(c.MaxLights, lights?.Length ?? 0)));
            SaveState();
            try
            {
                ReadTimings();
                var slot = _ring[_ringNext];
                bool timed = !slot.Pending;
                if (timed) { _ctx.Begin(slot.Disjoint); _ctx.End(slot.T0); }

                var sky = GiSky.RadianceFromValue(skyValue);
                _p.VoxOrigin = new Vector4(plan.Origin, c.VoxelSize);
                _p.VoxDim = new Int4(c.VoxX, c.VoxY, c.VoxZ, c.Voxels);
                _p.ProbeOrigin = new Vector4(plan.Origin, c.ProbeSpacing);
                _p.ProbeDim = new Int4(c.ProbeX, c.ProbeY, c.ProbeZ, c.Probes);
                _p.Misc = new Vector4(c.MinBlend, c.EmissionScale, c.BackFaceShare, c.RayLength);
                _p.Rot0 = plan.Rot0; _p.Rot1 = plan.Rot1; _p.Rot2 = plan.Rot2;
                _p.SkyRad0 = sky[0]; _p.SkyRad1 = sky[1]; _p.SkyRad2 = sky[2]; _p.SkyRad3 = sky[3]; _p.SkyRad4 = sky[4]; _p.SkyRad5 = sky[5]; _p.SkyRad6 = sky[6];
                _p.SkyVal0 = skyValue[0]; _p.SkyVal1 = skyValue[1]; _p.SkyVal2 = skyValue[2]; _p.SkyVal3 = skyValue[3];
                _p.SkyVal4 = skyValue[4]; _p.SkyVal5 = skyValue[5]; _p.SkyVal6 = skyValue[6];
                _p.WinDim = new Int4(c.ProbeX, c.ProbeY, c.ProbeZ, 0);
                _p.WinOrigin = new Vector4(plan.Origin, c.EdgeFade);
                _p.ProbeRange = new Int4(plan.ProbeFirst, plan.ProbeCount, plan.Frame, 0);
                _p.Glow = new Int4(Math.Max(0, Math.Min(GlowRays, 8)), c.GlowBricks, 0, 0);
                _p.InjectRange = new Int4(plan.InjectFirst, plan.InjectCount, lightCount, 0);
                _p.Shift = default;
                SetSlab(0, c.VoxZ);

                if (lightCount > 0)
                {
                    Array.Copy(lights, _lightScratch, lightCount);
                    var box = _ctx.MapSubresource(_lights.Buffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
                    try { Utilities.Write(box.DataPointer, _lightScratch, 0, lightCount); }
                    finally { _ctx.UnmapSubresource(_lights.Buffer, 0); }
                }

                bool bricks = false;   // some bricks changed: the list of glowing ones is made again
                if (plan.Reset)
                {
                    Zero(_vox[0]); Zero(_vox[1]); Zero(_probes[0]); Zero(_probes[1]); Zero(_bricks); Zero(_brickGlow); Zero(_glowList);
                }
                else if (plan.HasShift)
                {
                    // the voxels move by Shift * ProbeRatio, the probes by Shift; then every brick again
                    int r = c.ProbeRatio;
                    _voxCur ^= 1;
                    _p.Shift = new Int4(plan.ShiftX * r, plan.ShiftY * r, plan.ShiftZ * r, 0);
                    UpdateParams();
                    Run("ShiftVoxels", c.Voxels);
                    _probeCur ^= 1;
                    _p.Shift = new Int4(plan.ShiftX, plan.ShiftY, plan.ShiftZ, 0);
                    UpdateParams();
                    Run("ShiftProbes", c.Probes);
                    _p.Shift = default;
                    UpdateParams();
                    Run("BuildBricks", c.Voxels / 64);
                    bricks = true;
                }

                if (plan.Slab >= 0 && plan.Slab < c.Slabs)
                {
                    SetSlab(plan.Slab * c.SlabSlices, c.SlabSlices);
                    UpdateParams();
                    Run("Resolve", c.SlabVoxels);
                    Run("ClearAccum", c.SlabVoxels * 10);
                    Run("BuildBricks", c.SlabVoxels / 64);
                    bricks = true;
                }

                if (bricks)
                {
                    RunGroups("ClearGlow", 1);
                    Run("ListGlow", c.Voxels / 64);
                }

                UpdateParams();
                Run("Inject", c.VoxX * c.VoxY * plan.InjectCount);
                RunGroups("TraceProbes", plan.ProbeCount);

                if (windows != null)
                    foreach (var w in windows)
                    {
                        if (w.Volume < 0 || w.Volume > 1) continue;
                        _p.WinOrigin = new Vector4(w.Origin, c.EdgeFade);
                        UpdateParams();
                        _volTarget = _volUav[w.Volume];
                        Bind(_kernels["WriteVolume"]);
                        _ctx.Dispatch((c.ProbeX + 3) / 4, (c.ProbeY + 3) / 4, (c.ProbeZ + 3) / 4);
                        _volTarget = null;
                    }

                if (timed)
                {
                    _ctx.End(slot.T1);
                    _ctx.End(slot.Disjoint);
                    slot.Pending = true;
                    _ringNext = (_ringNext + 1) % _ring.Length;
                }
                Stats.Frames++;
            }
            finally { RestoreState(); }
        }

        /// <summary>Copies volume <paramref name="i"/> (as the last Frame wrote it) into <paramref name="dest"/>: a texture of the same size
        /// and an R16G16B16A16 format (Unity's render texture the shaders read). A copy, not a write through a view of Unity's
        /// texture: binding that as an unordered access view would unbind it from the pixel shaders behind Unity's back.</summary>
        public void CopyVolume(int i, SharpDX.Direct3D11.Resource dest) => _ctx.CopyResource(_vol[i], dest);

        void SetSlab(int z0, int slices)
        {
            var c = Config;
            _p.SlabOrigin = new Int4(0, 0, z0, 0);
            _p.SlabDim = new Int4(c.VoxX, c.VoxY, slices, c.VoxX * c.VoxY * slices);
        }

        void UpdateParams()
        {
            var box = _ctx.MapSubresource(_cb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Write(box.DataPointer, ref _p); }
            finally { _ctx.UnmapSubresource(_cb, 0); }
        }

        void ReadTimings()
        {
            foreach (var s in _ring)
            {
                if (!s.Pending || !_ctx.IsDataAvailable(s.Disjoint, AsynchronousFlags.DoNotFlush)) continue;
                var dj = _ctx.GetData<QueryDataTimestampDisjoint>(s.Disjoint, AsynchronousFlags.DoNotFlush);
                if (!dj.Disjoint && dj.Frequency > 0 && _ctx.IsDataAvailable(s.T0, AsynchronousFlags.DoNotFlush) && _ctx.IsDataAvailable(s.T1, AsynchronousFlags.DoNotFlush))
                {
                    long t0 = _ctx.GetData<long>(s.T0, AsynchronousFlags.DoNotFlush), t1 = _ctx.GetData<long>(s.T1, AsynchronousFlags.DoNotFlush);
                    double ms = (t1 - t0) * 1000.0 / dj.Frequency;
                    Stats.GpuMs = Stats.GpuMs <= 0 ? ms : 0.9 * Stats.GpuMs + 0.1 * ms;
                    Stats.LastGpuMs = ms;
                }
                s.Pending = false;
            }
        }

        // ------------------------------------------------------------------ dispatching

        ShaderResourceView SrvOf(string name)
        {
            switch (name)
            {
                case "Accum": return _accum.Srv;
                case "VoxelsIn": return _vox[_voxCur ^ 1].Srv;
                case "ProbesIn": return _probes[_probeCur ^ 1].Srv;
                case "Lights": return _lights.Srv;
                default: throw new InvalidOperationException("no shader resource " + name);
            }
        }

        UnorderedAccessView UavOf(string name)
        {
            switch (name)
            {
                case "AccumRW": return _accum.Uav;
                case "Voxels": return _vox[_voxCur].Uav;
                case "Bricks": return _bricks.Uav;
                case "BrickGlow": return _brickGlow.Uav;
                case "GlowList": return _glowList.Uav;
                case "Probes": return _probes[_probeCur].Uav;
                case "Volume": return _volTarget ?? throw new InvalidOperationException("no volume to write");
                default: throw new InvalidOperationException("no unordered access resource " + name);
            }
        }

        void Bind(Kernel k)
        {
            Array.Clear(_uavs, 0, _uavs.Length);
            Array.Clear(_srvs, 0, _srvs.Length);
            foreach (var b in k.Bindings)
            {
                if (b.Type == HlslBindType.CBuffer) continue;
                if (b.Slot >= 8) throw new InvalidOperationException($"{k.Name}: {b.Name} in slot {b.Slot}");
                if (b.IsUav) _uavs[b.Slot] = UavOf(b.Name);
                else if (b.IsSrv) _srvs[b.Slot] = SrvOf(b.Name);
            }
            var cs = _ctx.ComputeShader;
            cs.Set(k.Shader);
            cs.SetConstantBuffer(0, _cb);
            // the UAVs first: Direct3D 11 drops an SRV whose buffer is still bound as a UAV
            cs.SetUnorderedAccessViews(0, _uavs);
            cs.SetShaderResources(0, _srvs);
        }

        void Run(string kernel, int threads)
        {
            Bind(_kernels[kernel]);
            _ctx.Dispatch(Math.Max(1, (threads + Group - 1) / Group), 1, 1);
        }

        void RunGroups(string kernel, int groups)
        {
            Bind(_kernels[kernel]);
            _ctx.Dispatch(Math.Max(1, groups), 1, 1);
        }

        // the compute stage as the renderer had it (Unity's own state cache stays right)
        ComputeShader _savedShader;
        D3DBuffer[] _savedCb;
        ShaderResourceView[] _savedSrv;
        UnorderedAccessView[] _savedUav;

        void SaveState()
        {
            var cs = _ctx.ComputeShader;
            _savedShader = cs.Get();
            _savedCb = cs.GetConstantBuffers(0, 1);
            _savedSrv = cs.GetShaderResources(0, 8);
            _savedUav = cs.GetUnorderedAccessViews(0, 8);
        }

        void RestoreState()
        {
            var cs = _ctx.ComputeShader;
            cs.Set(_savedShader);
            cs.SetConstantBuffers(0, _savedCb);
            cs.SetShaderResources(0, _savedSrv);
            cs.SetUnorderedAccessViews(0, _savedUav);
            _savedShader?.Dispose(); _savedShader = null;
            foreach (var x in _savedCb) x?.Dispose();
            foreach (var x in _savedSrv) x?.Dispose();
            foreach (var x in _savedUav) x?.Dispose();
        }

        // ------------------------------------------------------------------ tests and debugging (these wait for the GPU)

        /// <summary>Writes the slab buffer from the CPU (what the voxelize shader adds, for the offline tests).</summary>
        public void UploadAccum(uint[] data)
        {
            if (data.Length < Config.SlabVoxels * 10) throw new ArgumentException("one slab is " + Config.SlabVoxels * 10 + " uints");
            _ctx.UpdateSubresource(data, _accum.Buffer);
        }

        public Vector4[] ReadProbes() => ReadAll<Vector4>(_probes[_probeCur].Buffer, Config.Probes * 4, 16);

        public uint[] ReadVoxels()
        {
            var v = ReadAll<Int4>(_vox[_voxCur].Buffer, Config.Voxels, 16);
            var r = new uint[v.Length * 4];
            for (int i = 0; i < v.Length; i++) { r[4 * i] = (uint)v[i].X; r[4 * i + 1] = (uint)v[i].Y; r[4 * i + 2] = (uint)v[i].Z; r[4 * i + 3] = (uint)v[i].W; }
            return r;
        }

        public uint[] ReadBricks() => ReadAll<uint>(_bricks.Buffer, Config.Voxels / 64, 4);

        /// <summary>Per brick: the glow-weighted world centre of its glowing voxels (xyz) and their glow added up (w).</summary>
        public Vector4[] ReadBrickGlow() => ReadAll<Vector4>(_brickGlow.Buffer, Config.Voxels / 64, 16);

        /// <summary>[0]: how many bricks glow; then their indices.</summary>
        public uint[] ReadGlowList() => ReadAll<uint>(_glowList.Buffer, Config.Voxels / 64 + 1, 4);

        T[] ReadAll<T>(D3DBuffer src, int count, int stride) where T : struct
        {
            using (var staging = new D3DBuffer(_device, new BufferDescription(count * stride, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read, ResourceOptionFlags.None, 0)))
            {
                _ctx.CopySubresourceRegion(src, 0, new ResourceRegion(0, 0, 0, count * stride, 1, 1), staging, 0);
                var box = _ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    var r = new T[count];
                    Utilities.Read(box.DataPointer, r, 0, count);
                    return r;
                }
                finally { _ctx.UnmapSubresource(staging, 0); }
            }
        }

        /// <summary>A volume's texels as floats (4 * VolumeWidth * VolumeHeight * VolumeDepth: x fastest, then y, then z).</summary>
        public float[] ReadVolume(int i)
        {
            var d = _vol[i].Description;
            d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CpuAccessFlags = CpuAccessFlags.Read;
            using (var staging = new Texture3D(_device, d))
            {
                _ctx.CopyResource(_vol[i], staging);
                var box = _ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                try
                {
                    int w = d.Width, h = d.Height, dep = d.Depth;
                    var r = new float[4 * w * h * dep];
                    var row = new ushort[4 * w];
                    for (int z = 0; z < dep; z++)
                        for (int y = 0; y < h; y++)
                        {
                            Utilities.Read(box.DataPointer + z * box.SlicePitch + y * box.RowPitch, row, 0, row.Length);
                            for (int k = 0; k < row.Length; k++) r[((z * h + y) * w) * 4 + k] = HalfToFloat(row[k]);
                        }
                    return r;
                }
                finally { _ctx.UnmapSubresource(staging, 0); }
            }
        }

        static float HalfToFloat(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 31, m = h & 1023;
            float v = e == 0 ? m / 1024f * (float)Math.Pow(2, -14) : e == 31 ? float.PositiveInfinity : (1f + m / 1024f) * (float)Math.Pow(2, e - 15);
            return s != 0 ? -v : v;
        }

        public void Dispose()
        {
            foreach (var k in _kernels.Values) k.Shader?.Dispose();
            _kernels.Clear();
            if (_ownAccum) _accum?.Dispose();
            else { _accum?.Uav?.Dispose(); _accum?.Srv?.Dispose(); _accum?.Buffer?.Dispose(); }
            _accum = null;
            _bricks?.Dispose(); _brickGlow?.Dispose(); _glowList?.Dispose(); _lights?.Dispose();
            for (int i = 0; i < 2; i++)
            {
                _vox[i]?.Dispose(); _probes[i]?.Dispose();
                _volUav[i]?.Dispose(); _vol[i]?.Dispose();
                _volUav[i] = null; _vol[i] = null;
            }
            _cb?.Dispose(); _cb = null;
            foreach (var s in _ring) { s?.Disjoint?.Dispose(); s?.T0?.Dispose(); s?.T1?.Dispose(); }
        }
    }
}
