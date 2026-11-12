// 0.16.0: the GPU fluid's Direct3D 11 driver (SharpDX 4.2, the renderer's own). It owns the buffers, compiles and runs the
// kernels of FluidSim.hlsl in order, and reads the counts back a few frames late without ever waiting for the GPU. It runs on
// whatever thread owns the device context (the renderer's render thread; the offline tests' own thread). netstandard2.0.
#nullable disable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using Format = SharpDX.DXGI.Format;
using Device = SharpDX.Direct3D11.Device;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Softbody.FluidGpu
{
    public sealed class GpuFluidConfig
    {
        public float ParticleSize = 0.025f;
        /// <summary>0.21.0: 4 substeps of 2 iterations (0.16.0 to 0.20.0: 2 of 3): a deep pool no longer boils.</summary>
        public int Substeps = 4, Iterations = 2;
        /// <summary>0.21.0: sleeping (as the CPU's FluidSettings): within SleepRadius particle sizes of one spot for SleepAfter
        /// seconds; woken by an awake particle or a shape faster than WakeSpeed. SleepRadius 0 turns it off.</summary>
        public float SleepRadius = 0.25f, SleepAfter = 1f;
        /// <summary>0.23.0: particle sizes per second (as FluidSettings.WakeSpeed); a shape wakes sleepers at ShapeWakeSpeed m/s.</summary>
        public float WakeSpeed = 20f, ShapeWakeSpeed = 0.5f;
        /// <summary>0.22.0: keep the surface's blocks between builds and recompute only those near particles that moved (false:
        /// every block every build, as before).</summary>
        public bool SurfaceCache = true;
        /// <summary>0.24.0: blocks only deep particles reach get no field (inside: no surface there). False: every block.</summary>
        public bool SurfaceNarrowBand = true;
        /// <summary>0.25.0: 0.24.0's solver, kept as an emergency backup in case the new one misbehaves: no divergence pass, and
        /// particles fall asleep one by one instead of a tile at a time (no mesh smoothing either).</summary>
        public bool Classic;
        /// <summary>0.25.0: passes of mesh smoothing (as FluidSurface.MeshSmoothing; 0: off; not with the classic solver).</summary>
        public int MeshSmoothing = 4;
        /// <summary>0.26.0: the gas's puffs at most (a ring: past it the oldest go first).</summary>
        public int GasCapacity = 32768;
        /// <summary>0.27.0: spray, foam and bubbles (Ihmsen et al. 2012) at most (a ring: past it the oldest go first), and the most
        /// made in one frame. Their buffers are made the first time a liquid asks for foam (a token before).</summary>
        public int FoamCapacity = 65536, FoamSpawnsPerFrame = 2048;
        /// <summary>0.27.0: vortex particles in the gas at most, how long each lives and how fast it fades (seconds).</summary>
        public int VortexCapacity = 512;
        public float VortexLife = 6f, VortexDecay = 2.5f;
        /// <summary>0.27.0: the thick liquid's implicit viscosity solve (Weiler et al. 2018) stops at |r| &lt;= tolerance x |b|
        /// (SPlisHSPlasH's default 0.01), or after this many iterations. Its buffers are made the first time a liquid is thick.</summary>
        public float ImplicitTolerance = 0.01f;
        public int ImplicitMaxIterations = 150;
        /// <summary>0.27.0: each solve starts from this substep's velocities plus this share of the last solve's change. With the 0.01
        /// tolerance a full warm start (SPlisHSPlasH's 1) fed back what each solve leaves of a rigid spin: a spinning thick blob spun
        /// up 3x in 3 s (SoftbodyRnD). Half keeps the spin (x1.01) for the same iterations as a 10x tighter tolerance. 0: none.</summary>
        public float ImplicitWarmStart = 0.5f;
        public int Capacity = 65536;
        public int MaxSources = 1024;
        public int MaxShapes = 8192;
        public int VertexCapacity = 400000;
        public int IndexCapacity = 400000 * 6;
        /// <summary>Surface blocks at SurfaceDetail 1 (a finer surface gets detail^3 times as many, up to 8x).</summary>
        public int MaxBricks = 32768;
        public float MaxSpeed = 30f;
        public float MaxGain = 100f;
        // the surface, as the CPU's FluidSurface
        public float CellScale = 0.8f, KernelScale = 2f, RadiusScale = 0.6f;

        public GpuFluidConfig Clone() => (GpuFluidConfig)MemberwiseClone();
    }

    /// <summary>0.26.0: a gas emitter as the kernels read it (FluidSim.hlsl GasEmitter).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuGasEmitter
    {
        public Vector4 A;       // buoyancy, drag, swirl, spread
        public Vector4 B;       // start size, end size, lifetime, collide mask (uint bits)
        public Vector4 Color;   // linear RGBA (A: opacity)
        public Vector4 D;       // 0.27.0: x how strongly its puffs feel the vortex particles (its Vortices; 0: not at all), yzw unused
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuLiquid
    {
        public Vector4 A;       // viscosity, cohesion, adhesion, friction
        public Vector4 B;       // density, gravity, damping, lifetime
        public Vector4 Color;   // linear RGBA
        public uint Mask, Alive;
        /// <summary>How many of its particles go this frame (a lowered MaxParticles), with Step's trim.</summary>
        public uint Trim;
        public uint Pad;
        /// <summary>0.27.0: x its foam amount (0: none), y its kinematic viscosity for the implicit solve (m²/s; 0: XSPH, as before),
        /// z its foam's lifetime (seconds), w unused.</summary>
        public Vector4 D;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuShape
    {
        public Vector4 A, B, C, Q, V, N, BMin, BMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GpuSpawn
    {
        public Vector4 Pos;     // w: source (uint bits)
        public Vector4 Vel;
    }

    /// <summary>What a frame's readback says (a few frames old).</summary>
    public sealed class GpuFluidStats
    {
        public int Frame = -1;
        public int Count, Vertices, Indices, Contacts, Bricks, Removed, Spawned;
        /// <summary>The surface ran out of vertices, indices or blocks (part of the liquid is not shown).</summary>
        public bool SurfaceTruncated;
        /// <summary>0.19.0: what the last surface needed (past the caps too), for lowering its detail to fit.</summary>
        public int VerticesNeeded, IndicesNeeded, BricksNeeded;
        public float MeanNeighbours, MaxSpeed;
        public Vector3 Min, Max;
        public uint[] SourceCounts = new uint[0];
        public double GpuMs;
        /// <summary>0.21.0: particles awake in that frame (the rest slept).</summary>
        public int Awake;
        /// <summary>0.25.0: awake particles not still for half the sleep time (with tiles).</summary>
        public int Unsettled;
        /// <summary>0.26.0: the gas's live puffs, the puffs drawn, and its bounds.</summary>
        public int GasPuffs, GasDrawn;
        public Vector3 GasMin, GasMax;
        public bool HasGasBounds => GasPuffs > 0 && GasMin.X <= GasMax.X;
        /// <summary>0.22.0: surface blocks whose field that build recomputed (the rest kept theirs).</summary>
        public int BricksComputed;
        /// <summary>0.22.0: of those, new blocks (not kept) and blocks marked by particles that moved (diagnostics).</summary>
        public int BricksNew, BricksMoved;
        /// <summary>0.24.0: blocks deep inside the liquid that a build marked "inside" instead of computing (the narrow band).</summary>
        public int BricksDeep;
        /// <summary>0.27.0: spray, foam and bubbles: live (and as each), drawn, asked for that frame (past the budget too), the fluid
        /// particles that made some, and their bounds.</summary>
        public int DiffuseLive, DiffuseSpray, DiffuseFoam, DiffuseBubbles, DiffuseDrawn, DiffuseAsked, DiffuseMakers;
        public Vector3 DiffuseMin, DiffuseMax;
        public bool HasDiffuseBounds => DiffuseLive > 0 && DiffuseMin.X <= DiffuseMax.X;
        /// <summary>0.27.0: the gas's live vortex particles.</summary>
        public int Vortices;
        /// <summary>0.27.0: the thick liquid's implicit solves that frame: how many, the most and the total iterations, and how many
        /// stopped at the iteration cap before converging.</summary>
        public int IvSolves, IvIterationsMax, IvIterationsSum, IvUnconverged;
        public bool HasBounds => Count > 0 && Min.X <= Max.X;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct Bits { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public uint U0x, U0y, U0z, U0w;
        public uint U1x, U1y, U1z, U1w;
        public uint U2x, U2y, U2z, U2w;
        public uint U3x, U3y, U3z, U3w;
        public Vector4 F0, F1, F2, F3, KMin, KMax, SgMin, GMin, SurfP, Grav;
        public Vector4 Sleep;                // 0.21.0
        public uint U4x, U4y, U4z, U4w;      // 0.21.0 (z, w: 0.22.0)
        public Vector4 Surf2;                // 0.22.0
        public uint U5x, U5y, U5z, U5w;      // 0.23.0
        public uint G0x, G0y, G0z, G0w;      // 0.26.0 gas
        public Vector4 G1, G2;               // 0.26.0 gas
        public uint D0x, D0y, D0z, D0w;      // 0.27.0 spray, foam and bubbles
        public Vector4 D1;                   // 0.27.0 spray, foam and bubbles
        public uint V0x, V0y, V0z, V0w;      // 0.27.0 vortex particles
        public Vector4 V1;                   // 0.27.0 vortex particles
        public Vector4 I0;                   // 0.27.0 implicit viscosity: dt, tolerance, 0.01 h², the warm start's share
        public uint I1x, I1y, I1z, I1w;      // 0.27.0 implicit viscosity: unused, most iterations, unused, unused
        public Vector4 S0;                   // 0.27.0 smoke shader: towards the main light (0: none), w 1: the gas as quads
        public Vector4 S1;                   // 0.27.0 smoke shader: the density grid's cell, self-shadow strength, 1: foam as quads, unused
    }

    public sealed class GpuFluidSim : IDisposable
    {
        public static readonly string[] KernelNames =
        {
            "ClearCounts", "SpawnParticles", "ParticleArgs", "SubstepBegin", "Predict", "KeysFromPos",
            "ClearCells", "CountCells", "ScanBlocks", "ScanSums", "AddSums", "Scatter", "Reorder", "SetCount",
            "Neighbours", "Lambdas", "Deltas", "ApplyDeltas", "Collide", "VelocityA", "Viscosity", "Normals", "Tension", "VelocityB", "Settle", "ReorderAnchor", "DivLambdas", "DivDeltas", "ClearTiles", "TileTop", "TileCount", "TileDecide", "TileApply", "WakeInBoxes", "FrameEnd",
            "SurfaceBegin", "ClearBricks", "MarkBricks", "MarkDirty", "MarkShallow", "SurfaceArgs", "ColorArgs", "TailArgs", "Radii", "Field", "CellVertices", "VertexColors", "SmoothNbrs", "SmoothIn", "SmoothOut", "SmoothWrite", "Quads", "ClearTail",
            "GasClear", "GasBegin", "GasSpawn", "GasUpdate", "GasDraw", "GasTail",
            "GasSplat", "GasBucketScan", "GasDrawQuads", "GasTailQuads",                                   // 0.27.0 the smoke shader's quads
            "DiffuseBegin", "DiffuseEmit", "DiffuseHead", "DiffuseUpdate", "DiffuseDraw", "DiffuseTail",   // 0.27.0 spray, foam, bubbles
            "VortClear", "VortSpawn", "VortVelocity", "VortMove",                                          // 0.27.0 vortex particles
            "IvFrameBegin", "IvBegin", "IvDiagonal", "IvStart", "IvMatVec", "IvResidual", "IvInit", "IvAlpha", "IvUpdate", "IvBeta",
            "IvDirection", "IvFinish", "ReorderIvGuess",                                                   // 0.27.0 thick liquid
        };

        public static float AsFloat(uint u) { var b = new Bits { U = u }; return b.F; }
        public static uint AsUint(float f) { var b = new Bits { F = f }; return b.U; }

        const int Group = 128, ScanSpan = 1024, MaxNb = 48, StateSize = 64, TileTable = 65536;   // (0.27.0: 64, from 40: 26 the vortices, 40-55 the foam, 60-63 the thick liquid)
        /// <summary>0.25.0: the most particles (0.16.0 to 0.24.0: 262144).</summary>
        public const int MaxCapacity = 524288;

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

        public readonly GpuFluidConfig Config;
        readonly Device _device;
        readonly DeviceContext _ctx;
        readonly Dictionary<string, Kernel> _kernels = new Dictionary<string, Kernel>();
        readonly Dictionary<string, Res> _res = new Dictionary<string, Res>();
        Res _pos, _vel, _pred, _pos2, _vel2, _pred2;
        D3DBuffer _cb;
        Params _p;
        readonly int _table, _blocks;
        int _brickTable, _maxBricks;
        float _detail = 1f, _detailUsed = 1f, _droplet = 1.5f;
        int _viscPasses = 1;
        Vector3 _latticeCenter;
        readonly UnorderedAccessView[] _uavs = new UnorderedAccessView[8];
        readonly ShaderResourceView[] _srvs = new ShaderResourceView[8];
        int _shapeCount, _globalCount, _sgCells, _sgListCap;
        bool _haveShapes;
        int _frame;

        // the readback ring
        sealed class Slot
        {
            public D3DBuffer State, Counts;
            public Query Done, Disjoint, T0, T1;
            public int Frame = -1;
            public bool Pending, Timed;
            public float DiffuseScale = 1f;   // 0.27.0: the foam budget's scale in that frame
        }
        readonly Slot[] _ring = new Slot[8];
        int _ringNext;
        public readonly GpuFluidStats Stats = new GpuFluidStats();

        public float Spacing => Config.ParticleSize;
        public float CollisionRadius { get; private set; }
        public float H { get; private set; }
        public int Frame => _frame;
        public int SourceCount { get; private set; }
        /// <summary>The surface's vertices (40 bytes each: position, normal, colour) and 32-bit indices, for the renderer to copy.</summary>
        public D3DBuffer VertexBuffer => _res["Verts"].Buffer;
        public D3DBuffer IndexBuffer => _res["Indices"].Buffer;
        /// <summary>0.26.0: the gas's vertices (7 a puff) and 32-bit indices (18 a puff), for the renderer to copy. 0.27.0: a vertex is
        /// GasVertexBytes (position, normal, colour, texcoord0), and with the smoke shader a puff is a quad (4 and 6).</summary>
        public D3DBuffer GasVertexBuffer => _res["GasVerts"].Buffer;
        /// <summary>0.27.0: the gas's and the foam's vertex size (position, normal, colour rgba, texcoord0 xyzw: 14 floats).</summary>
        public const int GasVertexBytes = 56, GasVertexFloats = 14;
        public D3DBuffer GasIndexBuffer => _res["GasIndices"].Buffer;
        public Vector3 LatticeOrigin { get; private set; }
        public float SurfaceCell { get; private set; }

        public GpuFluidSim(Device device, GpuFluidConfig config, string hlsl, Func<string, byte[]> cachedBytecode = null)
        {
            Config = config.Clone();
            if (Config.Capacity < 256) Config.Capacity = 256;
            if (Config.Capacity > MaxCapacity) Config.Capacity = MaxCapacity;
            _device = device;
            _ctx = device.ImmediateContext;
            // (the cell table: twice the particles, at most 2^19 entries: the scan takes up to ScanSpan blocks of ScanSpan; cells
            // hold about 8 particles each, so even at 2^19 for MaxCapacity it is far from full)
            int t = 1; while (t < 2 * Config.Capacity && t < (1 << 19)) t <<= 1;
            _table = t;
            _blocks = (t + 1 + ScanSpan - 1) / ScanSpan;
            _maxBricks = Math.Max(256, Config.MaxBricks);
            int bt = 1; while (bt < 2 * _maxBricks) bt <<= 1;
            _brickTable = bt;
            try
            {
                foreach (var name in KernelNames)
                {
                    var code = cachedBytecode?.Invoke(name) ?? HlslCompiler.Compile(hlsl, name);
                    _kernels[name] = new Kernel { Name = name, Shader = new ComputeShader(device, code), Bindings = HlslCompiler.Reflect(code) };
                }
                Allocate();
                SetupConstants();
                for (int i = 0; i < _ring.Length; i++)
                {
                    _ring[i] = new Slot
                    {
                        State = Staging(StateSize * 4), Counts = Staging(Config.MaxSources * 4),
                        Done = new Query(device, new QueryDescription { Type = QueryType.Event }),
                        Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint }),
                        T0 = new Query(device, new QueryDescription { Type = QueryType.Timestamp }),
                        T1 = new Query(device, new QueryDescription { Type = QueryType.Timestamp }),
                    };
                }
                Reset();
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Compiles every kernel (for a cache, or to compile off the render thread).</summary>
        public static Dictionary<string, byte[]> CompileAll(string hlsl)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var name in KernelNames) d[name] = HlslCompiler.Compile(hlsl, name);
            return d;
        }

        D3DBuffer Staging(int bytes) =>
            new D3DBuffer(_device, new BufferDescription(bytes, ResourceUsage.Staging, BindFlags.None, CpuAccessFlags.Read, ResourceOptionFlags.None, 0));

        Res Structured(string name, int elements, int stride, bool uav, bool dynamic = false)
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
                r.Uav = new UnorderedAccessView(_device, r.Buffer, new UnorderedAccessViewDescription
                {
                    Format = Format.Unknown, Dimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = r.Elements, Flags = UnorderedAccessViewBufferFlags.None },
                });
            if (r.Uav != null) Zero(r);
            if (name != null) { if (_res.TryGetValue(name, out var old)) old.Dispose(); _res[name] = r; }
            return r;
        }

        // 0.19.0: a new buffer's contents are undefined in Direct3D 11 (a rebuilt simulation can get the old one's memory): the
        // surface's index buffer past its end then drew old triangles over the new vertices (Lenki: long spikes after changing a
        // setting), so every buffer starts as zeros
        void Zero(Res r) => _ctx.ClearUnorderedAccessView(r.Uav, new SharpDX.Mathematics.Interop.RawInt4(0, 0, 0, 0));

        Res Raw(string name, int bytes, bool indirect = false)
        {
            bytes = (Math.Max(4, bytes) + 3) & ~3;
            var r = new Res { Elements = bytes / 4, Stride = 4 };
            r.Buffer = new D3DBuffer(_device, new BufferDescription(bytes, ResourceUsage.Default, BindFlags.UnorderedAccess | (indirect ? BindFlags.None : BindFlags.ShaderResource),
                CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews | (indirect ? ResourceOptionFlags.DrawIndirectArguments : ResourceOptionFlags.None), 0));
            r.Uav = new UnorderedAccessView(_device, r.Buffer, new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless, Dimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new UnorderedAccessViewDescription.BufferResource { FirstElement = 0, ElementCount = r.Elements, Flags = UnorderedAccessViewBufferFlags.Raw },
            });
            Zero(r);
            if (_res.TryGetValue(name, out var old)) old.Dispose();   // (0.27.0: the foam's are made again at full size)
            _res[name] = r;
            return r;
        }

        void Allocate()
        {
            int n = Config.Capacity;
            _pos = Structured(null, n, 16, true); _vel = Structured(null, n, 16, true); _pred = Structured(null, n, 16, true);
            _pos2 = Structured(null, n, 16, true); _vel2 = Structured(null, n, 16, true); _pred2 = Structured(null, n, 16, true);
            BindParticles();
            Structured("Delta", n, 16, true); Structured("Vtmp", n, 16, true); Structured("Contact", n, 16, true); Structured("Lambda", n, 4, true);
            Structured("Keys", n, 4, true); Structured("Order", n, 4, true);
            Structured("Nbr", n * MaxNb, 4, true); Structured("NbCount", n, 4, true);
            Structured("Rad", n, 4, true);
            Structured("Rho", n, 4, true); Structured("Nrm", n, 16, true); Structured("Smooth", n, 16, true); // 0.20.0
            Structured("Anchor", n, 16, true); Structured("Anchor2", n, 16, true); // 0.21.0
            Structured("TileKeys", TileTable, 4, true); Structured("TileData", TileTable * 8, 4, true); Structured("TileFlags", TileTable, 4, true); // 0.25.0
            Structured("LambdaV", n, 4, true); // 0.25.0
            Structured("CellCount", _table + 2, 4, true); Structured("CellCursor", _table + 2, 4, true);
            Structured("BlockSums", ScanSpan, 4, true);
            Structured("State", StateSize, 4, true);
            Structured("SourceCounts", Config.MaxSources, 4, true);
            Structured("TrimLeft", Config.MaxSources, 4, true);
            Raw("Args", 48, indirect: true);   // 0 particles, 12 surface blocks, 24 vertices (0.22.0), 36 the index tail (0.22.0)
            Structured("Liquids", Config.MaxSources, Marshal.SizeOf(typeof(GpuLiquid)), false, dynamic: true);
            Structured("WakeBoxes", 2 * MaxWakeBoxes, 16, false, dynamic: true);   // 0.23.0
            Structured("MovingShapes", Config.MaxShapes, 4, false, dynamic: true); // 0.25.0
            Structured("Shapes", Config.MaxShapes, Marshal.SizeOf(typeof(GpuShape)), false, dynamic: true);
            _sgListCap = 65536;
            Structured("ShapeGrid", 32768 + 1 + _sgListCap, 4, false, dynamic: true);
            Structured("GlobalShapes", Config.MaxShapes, 4, false, dynamic: true);
            Structured("Spawns", n, Marshal.SizeOf(typeof(GpuSpawn)), false, dynamic: true);
            AllocateBricks();
            Raw("Verts", Config.VertexCapacity * 40);
            // 0.26.0: the gas
            int gc = Math.Max(256, Config.GasCapacity);
            Structured("GasPos", gc, 16, true); Structured("GasVel", gc, 16, true); Structured("GasEmit", gc, 4, true);
            Raw("GasVerts", gc * 7 * GasVertexBytes); Raw("GasIndices", gc * 18 * 4);
            Structured("GasDensity", GasDensityTable, 4, true); Structured("GasBuckets", 2 * GasBuckets, 4, true);   // 0.27.0 smoke shader
            Structured("GasEmitters", MaxGasEmitters, Marshal.SizeOf(typeof(GpuGasEmitter)), false, dynamic: true);
            Structured("GasSpawns", MaxGasSpawns * 2, 16, false, dynamic: true);
            // 0.27.0: spray, foam and bubbles, and the thick liquid's solve: a token of memory until a liquid asks for them
            AllocateDiffuse(256);
            AllocateImplicit(256);
            Structured("IvPartial", 2 * (n / Group + 2), 4, true);
            _res["IvDone"] = Structured("IvScalars", 16, 4, true);   // (IvDone: the same buffer, read-only; see Bind)
            // 0.27.0: vortex particles (a few hundred: always there)
            int vc = VortexSlots;
            Structured("VortPos", vc, 16, true); Structured("VortStr", vc, 16, true); Structured("VortVel", vc, 16, true);
            Structured("VortSpawns", MaxVortexSpawns * 2, 16, false, dynamic: true);
            // 0.25.0: mesh smoothing
            Structured("VertNbr", Config.VertexCapacity * 6, 4, true);
            Structured("SPos", Config.VertexCapacity, 16, true); Structured("SNrm", Config.VertexCapacity, 16, true);
            Structured("SPosTmp", Config.VertexCapacity, 16, true); Structured("SNrmTmp", Config.VertexCapacity, 16, true);
            Raw("Indices", Config.IndexCapacity * 4);
            _cb = new D3DBuffer(_device, new BufferDescription(Marshal.SizeOf(typeof(Params)), ResourceUsage.Dynamic, BindFlags.ConstantBuffer,
                CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        }

        void AllocateBricks()
        {
            Structured("BrickKeys", _brickTable, 4, true); Structured("BrickSlot", _brickTable, 4, true);
            Structured("BrickCoord", _maxBricks, 4, true);
            Structured("Phi", _maxBricks * 64, 4, true); Structured("CellVert", _maxBricks * 64, 4, true);
            Structured("BrickMark", _maxBricks, 4, true);   // 0.22.0
            _surfaceReset = true;
        }

        void BindParticles()
        {
            _res["Pos"] = _pos; _res["Vel"] = _vel; _res["Pred"] = _pred;
            _res["Pos2"] = _pos2; _res["Vel2"] = _vel2; _res["Pred2"] = _pred2;
        }

        void SwapParticles()
        {
            var a = _pos; _pos = _pos2; _pos2 = a;
            var b = _vel; _vel = _vel2; _vel2 = b;
            var c = _pred; _pred = _pred2; _pred2 = c;
            BindParticles();
        }

        void SetupConstants()
        {
            float s = Spacing;
            float h = 2f * s, h2 = h * h;
            H = h;
            float poly6 = (float)(315.0 / (64.0 * Math.PI * Math.Pow(h, 9)));
            float spiky = (float)(-45.0 / (Math.PI * Math.Pow(h, 6)));
            CollisionRadius = 0.5f * s;
            float cohC = (float)(32.0 / (Math.PI * Math.Pow(h, 9))), cohH6 = (float)(Math.Pow(h, 6) / 64.0);
            float Poly6(float r2) { float d = h2 - r2; return d > 0f ? poly6 * d * d * d : 0f; }
            Vector3 Grad(Vector3 d, float r) { if (!(r > 1e-9f) || r >= h) return Vector3.Zero; float tt = h - r; return d * (spiky * tt * tt / r); }
            float rho = 0f; var grad = Vector3.Zero; float sumSq = 0f;
            int k = (int)Math.Ceiling(h / s);
            for (int x = -k; x <= k; x++)
                for (int y = -k; y <= k; y++)
                    for (int z = -k; z <= k; z++)
                    {
                        var d = new Vector3(x, y, z) * s;
                        float r2 = d.LengthSquared();
                        if (r2 >= h2) continue;
                        rho += Poly6(r2);
                        if (r2 > 0f) { var g = Grad(d, (float)Math.Sqrt(r2)); grad += g; sumSq += g.LengthSquared(); }
                    }
            float eps = 0.05f * (sumSq + grad.LengthSquared()) / (rho * rho);
            float Spline(float r)
            {
                if (r >= h || r <= 0f) return 0f;
                float tt = h - r, a = tt * tt * tt * r * r * r;
                return 2f * r > h ? cohC * a : cohC * (2f * a - cohH6);
            }
            float cmax = 0f;
            for (int i = 1; i < 100; i++) cmax = Math.Max(cmax, Spline(h * i / 100f));
            float cohNorm = cmax > 0f ? 1f / cmax : 0f;

            _p.U0x = (uint)Config.Capacity; _p.U0y = (uint)(_table - 1);
            _p.U1z = (uint)Config.VertexCapacity; _p.U1w = (uint)Config.IndexCapacity;
            _p.U2w = (uint)_blocks;
            _p.U3x = (uint)(_brickTable - 1); _p.U3y = (uint)_maxBricks;
            _p.F0 = new Vector4(h, h2, poly6, spiky);
            _p.F1 = new Vector4(rho, eps, CollisionRadius, 0f);
            _p.F2 = new Vector4(cohC, cohH6, cohNorm, 0.5f * s);
            _p.F3 = new Vector4(Config.MaxSpeed, Config.MaxGain, 0f, s);
            _p.Grav = new Vector4(0f, -9.81f, 0f, 1f);
            float sr = Math.Max(0f, Config.SleepRadius) * s;
            float wake = Config.WakeSpeed * s;
            _p.Sleep = new Vector4(sr * sr, Math.Max(0.05f, Config.SleepAfter), wake * wake, 1f);
            _p.Surf2.Z = Config.ShapeWakeSpeed * Config.ShapeWakeSpeed;
            _p.U5w = Tiles ? 1u : 0u;   // 0.25.0
            SurfaceConstants();
            SetKillBox(new Vector3(-1e4f), new Vector3(1e4f));
        }

        void SurfaceConstants()
        {
            float s = Spacing;
            float R = Config.KernelScale * s, cell = Config.CellScale * s / _detailUsed;
            SurfaceCell = cell;
            _p.SurfP = new Vector4(R, Config.RadiusScale * s, R + cell, _droplet);
            SetLatticeCenter(_latticeCenter);
        }

        public float DropletSize => _droplet;

        /// <summary>0.18.0: how big lone droplets, small clusters and thin sheets are drawn (0.5..3; 1 is their own volume),
        /// fading to the particles' own size in the liquid's body. Live.</summary>
        public void SetDropletSize(float size)
        {
            if (float.IsNaN(size) || float.IsInfinity(size)) size = 1.5f;
            float d = Math.Max(0.5f, Math.Min(3f, size));
            if (d != _droplet) SurfaceChanged();
            _droplet = d;
            _p.SurfP.W = _droplet;
        }

        public float SurfaceDetail => _detail;
        /// <summary>0.19.0: the detail the surface is built at (lower than asked while the asked one does not fit).</summary>
        public float SurfaceDetailUsed => _detailUsed;
        public int MaxBricks => _maxBricks;

        /// <summary>
        /// 0.17.0: how fine the surface is (0.5..3: its grid is CellScale particle sizes / detail). Live: only the surface's
        /// blocks are reallocated (a finer grid needs detail^3 as many for the same liquid, up to 8x MaxBricks). 0.19.0: the
        /// blocks are sized for <paramref name="detail"/>, the grid is built at <paramref name="used"/> (at most that).
        /// </summary>
        public void SetSurfaceDetail(float detail, float used = -1f)
        {
            if (float.IsNaN(detail) || float.IsInfinity(detail)) detail = 1f;
            detail = Math.Max(0.5f, Math.Min(3f, detail));
            if (!(used > 0f) || float.IsInfinity(used)) used = detail;
            used = Math.Max(0.1f, Math.Min(detail, used));
            if (Math.Abs(detail - _detail) < 1e-4f && Math.Abs(used - _detailUsed) < 1e-4f) return;
            _detail = detail; _detailUsed = used;
            SurfaceChanged();
            int want = (int)(Math.Max(256, Config.MaxBricks) * Math.Max(1f, Math.Min(8f, detail * detail * detail)));
            if (want != _maxBricks)
            {
                _maxBricks = want;
                int bt = 1; while (bt < 2 * _maxBricks) bt <<= 1;
                _brickTable = bt;
                AllocateBricks();
                _p.U3x = (uint)(_brickTable - 1); _p.U3y = (uint)_maxBricks;
            }
            SurfaceConstants();
        }

        /// <summary>0.19.0: XSPH passes for viscosity above 1 (the largest liquid's, rounded up; 1..20).</summary>
        public void SetViscosityPasses(int passes)
        {
            _viscPasses = Math.Max(1, Math.Min(20, passes));
            _p.Grav.W = 1f / _viscPasses;
        }

        /// <summary>Liquid outside this box is removed.</summary>
        public void SetKillBox(Vector3 min, Vector3 max)
        {
            _p.KMin = new Vector4(min, 0f); _p.KMax = new Vector4(max, 0f);
        }

        /// <summary>
        /// Where the surface's blocks are counted from: the lattice stays the world's own (its corner moves by whole blocks),
        /// and the blocks reach 2046 x 1022 x 2046 of them around it (164 x 82 x 164 m at the default detail; half that at 2).
        /// </summary>
        public void SetLatticeCenter(Vector3 center)
        {
            if (float.IsNaN(center.X + center.Y + center.Z) || float.IsInfinity(center.X + center.Y + center.Z)) center = Vector3.Zero;
            _latticeCenter = center;
            float brick = 4f * SurfaceCell;
            var c = new Vector3((float)Math.Floor(center.X / brick), (float)Math.Floor(center.Y / brick), (float)Math.Floor(center.Z / brick));
            var o = (c - new Vector3(1023f, 511f, 1023f)) * brick;
            if (o != LatticeOrigin) SurfaceChanged();
            LatticeOrigin = o;
            _p.GMin = new Vector4(o, SurfaceCell);
        }

        /// <summary>Removes every particle.</summary>
        public void Reset()
        {
            _ctx.UpdateSubresource(new uint[StateSize], _res["State"].Buffer);
            _ctx.UpdateSubresource(new uint[Config.MaxSources], _res["SourceCounts"].Buffer);
            foreach (var s in _ring) { if (s != null) { s.Pending = false; s.Frame = -1; } }
            Stats.Frame = -1; Stats.Count = 0; Stats.GasPuffs = 0;
            _surfaceFrame = -1; _lastWakeFrame = _frame + 1; _surfaceReset = true;
            _gasCleared = false; _gasCount = 0; _gasEmitterCount = 0;
            // 0.27.0: the spray, foam and bubbles go with the liquid (and their drawn discs: nothing would clear them while none
            // is alive), and the thick liquid's warm start
            Zero(_res["DPos"]); Zero(_res["DIndices"]); _diffuseEmitFrame = -1; Stats.DiffuseLive = 0; _diffuseScale = 1f;
            Zero(_res["IvGuess"]); _ivNeeded = -1;
            _vortCleared = false; _vortSpawns.Clear(); Stats.Vortices = 0;   // (the vortex particles go with the gas)
        }

        // ------------------------------------------------------------------ inputs

        GpuLiquid[] _liquids;
        int[] _trim;
        // 0.21.0: sleeping
        bool _wakeAll, _movingShapes, _lifetimes;
        uint[] _moving;
        int _lastWakeFrame = int.MaxValue - 1, _lastMoveFrame, _surfaceFrame = -1;
        GpuLiquid[] _liquidsSeen = new GpuLiquid[0];
        /// <summary>0.21.0: the last Step skipped its substeps (everything asleep) / built the surface.</summary>
        public bool SkippedLastFrame { get; private set; }
        public bool SurfaceBuiltLastFrame { get; private set; }

        // 0.25.0: the fixed substeps: the frame time not yet simulated, and the substeps so far
        float _acc;
        long _substeps;

        // ------------------------------------------------------------------ 0.26.0: the gas (DESIGN-gas.md)

        /// <summary>The most puffs spawned in one frame, and the most gas emitters.</summary>
        public const int MaxGasSpawns = 4096, MaxGasEmitters = 64;
        Vector4[] _gasSpawns = new Vector4[0];
        readonly GpuGasEmitter[] _gasEmitters = new GpuGasEmitter[MaxGasEmitters];
        int _gasCount, _gasEmitterCount, _gasHead;
        bool _gasUsed, _gasCleared;
        float _gasTime, _gasRadius = 0.03f;
        Vector3 _gasHeadAt;

        /// <summary>The gas for the next Step: its new puffs (pairs: position and its emitter's slot as uint bits, velocity and
        /// lifetime), its emitters by slot, where the local user's head is (the puffs face it) and its collision radius.</summary>
        public void SetGas(Vector4[] spawns, int spawnCount, GpuGasEmitter[] emitters, int emitterCount, Vector3 head, float radius)
        {
            spawnCount = Math.Max(0, Math.Min(spawnCount, MaxGasSpawns));
            emitterCount = Math.Max(0, Math.Min(emitterCount, MaxGasEmitters));
            if (_gasSpawns.Length < spawnCount * 2) _gasSpawns = new Vector4[Math.Max(spawnCount * 2, 64)];
            if (spawnCount > 0) Array.Copy(spawns, _gasSpawns, spawnCount * 2);
            if (emitterCount > 0) Array.Copy(emitters, _gasEmitters, emitterCount);
            _gasCount = spawnCount; _gasEmitterCount = emitterCount;
            _gasHeadAt = head; _gasRadius = radius > 0f ? radius : 0.03f;
            if (spawnCount > 0 || emitterCount > 0) _gasUsed = true;
        }

        /// <summary>0.26.0: every puff slot (xyz, age; age &lt; 0: empty) and its velocity (xyz, lifetime), waiting for the GPU (tests).</summary>
        public void ReadGas(out Vector4[] pos, out Vector4[] vel)
        {
            int cap = Math.Max(256, Config.GasCapacity);
            pos = ReadAll<Vector4>(_res["GasPos"].Buffer, cap, 16);
            vel = ReadAll<Vector4>(_res["GasVel"].Buffer, cap, 16);
        }

        /// <summary>0.26.0: the gas's drawn vertices (7 a puff) and indices (18 a puff), waiting for the GPU (tests). 0.27.0: 14 floats a
        /// vertex; as quads for the smoke shader 4 vertices and 6 indices a puff, far to near.</summary>
        public void ReadGasMesh(out float[] verts, out uint[] indices)
        {
            int n = Math.Max(0, Math.Min(Stats.GasDrawn, Math.Max(256, Config.GasCapacity)));
            int vp = _gasQuadsDrawn ? 4 : 7, ip = _gasQuadsDrawn ? 6 : 18;
            verts = n > 0 ? ReadAll<float>(_res["GasVerts"].Buffer, n * vp * GasVertexFloats, 4) : new float[0];
            indices = n > 0 ? ReadAll<uint>(_res["GasIndices"].Buffer, n * ip, 4) : new uint[0];
        }

        /// <summary>0.27.0: the first <paramref name="count"/> gas indices, whatever was drawn, waiting for the GPU (tests).</summary>
        public uint[] ReadGasIndices(int count)
        {
            int cap = Math.Max(256, Config.GasCapacity) * 18;
            count = Math.Max(0, Math.Min(count, cap));
            return count > 0 ? ReadAll<uint>(_res["GasIndices"].Buffer, count, 4) : new uint[0];
        }

        // ------------------------------------------------------------------ 0.27.0: the smoke shader (FluidSim.hlsl, GasDrawQuads)

        /// <summary>The density grid's hash table (cells) and the order's distance buckets (as FluidSim.hlsl).</summary>
        public const int GasDensityTable = 262144, GasBuckets = 1024;
        Vector3 _smokeLight;
        bool _gasQuads, _foamQuads, _gasQuadsDrawn, _foamQuadsDrawn;
        float _smokeCell = 0.25f, _smokeShadow = 1f;
        /// <summary>The gas (and the foam) as the last Step drew them: quads for the smoke shader (true) or discs.</summary>
        public bool GasQuads => _gasQuadsDrawn;
        public bool FoamQuads => _foamQuadsDrawn;

        /// <summary>How the renderer draws the gas and the foam: as quads for its smoke shader (with the light and sky reaching each
        /// puff through the smoke, far to near) or as discs for the plugin's Unlit material. <paramref name="towardsLight"/>: towards
        /// the world's main directional light in the gas's space (zero: none); <paramref name="cell"/>: the density grid's cell
        /// (metres); <paramref name="shadow"/>: how strongly the smoke shades itself (1: as its opacity says).</summary>
        public void SetSmoke(Vector3 towardsLight, bool gasQuads, bool foamQuads, float cell, float shadow = 1f)
        {
            float l = towardsLight.Length();
            _smokeLight = l > 1e-3f && Finite(l) ? towardsLight / l : Vector3.Zero;
            _gasQuads = gasQuads; _foamQuads = foamQuads;
            _smokeCell = Finite(cell) ? Math.Max(0.02f, Math.Min(cell, 4f)) : 0.25f;
            _smokeShadow = Finite(shadow) ? Math.Max(0f, Math.Min(shadow, 4f)) : 1f;
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        void RunGas(float dt)
        {
            int cap = Math.Max(256, Config.GasCapacity);
            _p.G0x = (uint)cap;
            if (!_gasCleared) { UpdateParams(); Run("GasClear", cap); _gasCleared = true; _gasHead = 0; }
            _gasTime += dt;
            _p.G0y = (uint)_gasCount; _p.G0z = (uint)_gasHead; _p.G0w = (uint)_gasEmitterCount;
            _p.G1 = new Vector4(_gasHeadAt, _gasTime % 10000f);
            _p.G2 = new Vector4(dt, 0f, 0f, 0f);
            // 0.27.0: quads for the smoke shader, or discs; a switch starts the index buffer over (the tail is counted in the new shape)
            if (_gasQuads != _gasQuadsDrawn) { Zero(_res["GasIndices"]); _gasQuadsDrawn = _gasQuads; }
            _p.S0 = new Vector4(_smokeLight, _gasQuads ? 1f : 0f);
            _p.S1 = new Vector4(_smokeCell, _smokeShadow, _foamQuads ? 1f : 0f, 0f);
            float keep = _p.F1.Z;
            _p.F1.Z = _gasRadius;   // the shapes push puffs out at the gas's radius
            UpdateParams();
            RunGroups("GasBegin", 1);
            if (_gasEmitterCount > 0) Upload("GasEmitters", _gasEmitters, _gasEmitterCount);
            if (_gasCount > 0)
            {
                Upload("GasSpawns", _gasSpawns, _gasCount * 2);
                Run("GasSpawn", _gasCount);
                _gasHead = (_gasHead + _gasCount) % cap;
            }
            RunVortices(dt);   // 0.27.0 (sets _V0 and _V1, which the update reads)
            Run("GasUpdate", cap);
            if (_gasQuads)
            {
                Zero(_res["GasDensity"]); Zero(_res["GasBuckets"]);
                Run("GasSplat", cap);
                RunGroups("GasBucketScan", 1);
                Run("GasDrawQuads", cap);
                Run("GasTailQuads", cap * 6);
            }
            else
            {
                Run("GasDraw", cap);
                Run("GasTail", cap * 18);
            }
            _gasCount = 0;
            _p.F1.Z = keep;
            UpdateParams();
        }

        // ------------------------------------------------------------------ 0.27.0: vortex particles in the gas (DESIGN-fluid.md)

        public const int MaxVortexSpawns = 256;
        public int VortexSlots => Math.Max(128, (Config.VortexCapacity + 127) / 128 * 128);
        readonly List<GpuShape> _movingShapeList = new List<GpuShape>();
        readonly List<Vector4> _vortSpawns = new List<Vector4>();
        int _vortHead;
        bool _vortCleared;
        uint _vortSeed = 0x2545F491u;
        float _vortexStrength;
        /// <summary>0.27.0: vortex rings shed so far (diagnostics).</summary>
        public int VortexRings { get; private set; }

        /// <summary>0.27.0: the gas's vortex strength, its emitters' largest Vortices (0: off). A moving shape in the gas sheds rings
        /// with a circulation of that x its speed x its size; each puff feels them by its own emitter's share of it.</summary>
        public void SetVortices(float strength) => _vortexStrength = float.IsNaN(strength) || float.IsInfinity(strength) ? 0f : Math.Max(0f, Math.Min(10f, strength));
        public float VortexStrength => _vortexStrength;

        float VRand() { _vortSeed = _vortSeed * 1664525u + 1013904223u; return (_vortSeed >> 8) * (1f / 16777216f); }

        /// <summary>0.27.0: every vortex slot (xyz, age; &lt; 0 empty) and its strength (xyz, core radius), waiting for the GPU (tests).</summary>
        public void ReadVortices(out Vector4[] pos, out Vector4[] str)
        {
            pos = ReadAll<Vector4>(_res["VortPos"].Buffer, VortexSlots, 16);
            str = ReadAll<Vector4>(_res["VortStr"].Buffer, VortexSlots, 16);
        }

        /// <summary>0.27.0: one vortex blob for the next frame (tests; the rings in a world come from moving shapes).</summary>
        public void QueueVortex(Vector3 pos, Vector3 strength, float core)
        {
            if (_vortSpawns.Count / 2 >= MaxVortexSpawns) return;
            _vortSpawns.Add(new Vector4(pos, core)); _vortSpawns.Add(new Vector4(strength, 0f));
        }

        /// <summary>0.27.0: a ring of <paramref name="blobs"/> vortex blobs around <paramref name="axis"/> (unit; the way the air goes
        /// through it), radius <paramref name="radius"/>, circulation <paramref name="gamma"/> (m²/s).</summary>
        public void QueueVortexRing(Vector3 center, Vector3 axis, float radius, float gamma, int blobs = 8)
        {
            var t = Math.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var e1 = Vector3.Normalize(Vector3.Cross(axis, t)); var e2 = Vector3.Cross(axis, e1);
            float dl = 2f * (float)Math.PI * radius / blobs;
            for (int k = 0; k < blobs; k++)
            {
                float a = 2f * (float)Math.PI * k / blobs;
                var rr = e1 * (float)Math.Cos(a) + e2 * (float)Math.Sin(a);
                // the core's vorticity runs along the ring: axis x outward (the air goes through the middle along the axis)
                QueueVortex(center + rr * radius, Vector3.Cross(axis, rr) * (gamma * dl), 0.5f * radius);
            }
            VortexRings++;
        }

        /// <summary>A moving shape in the gas (within its bounds, padded) sheds a ring behind it about every size of travel.</summary>
        void ShedVortices(float dt)
        {
            if (!(_vortexStrength > 0f) || !Stats.HasGasBounds) return;
            var lo = Stats.GasMin - new Vector3(0.3f); var hi = Stats.GasMax + new Vector3(0.3f);
            foreach (var sh in _movingShapeList)
            {
                var v = new Vector3(sh.V.X, sh.V.Y, sh.V.Z);
                float speed = v.Length();
                if (speed < 0.2f) continue;
                var bmin = new Vector3(sh.BMin.X, sh.BMin.Y, sh.BMin.Z); var bmax = new Vector3(sh.BMax.X, sh.BMax.Y, sh.BMax.Z);
                if (bmax.X < lo.X || bmin.X > hi.X || bmax.Y < lo.Y || bmin.Y > hi.Y || bmax.Z < lo.Z || bmin.Z > hi.Z) continue;
                // (its bounds include this frame's sweep: the size across the motion is the smaller extents' half)
                var ext = 0.5f * (bmax - bmin);
                float size = Math.Max(0.02f, Math.Min(ext.X, Math.Min(ext.Y, ext.Z)));
                if (VRand() > speed * dt / size) continue;
                var axis = v / speed;
                var c = 0.5f * (bmin + bmax) - axis * (0.5f * size);
                QueueVortexRing(c, axis, 0.9f * size, _vortexStrength * speed * size);
            }
        }

        void RunVortices(float dt)
        {
            int vc = VortexSlots;
            _p.V0x = (uint)vc;
            bool on = _vortexStrength > 0f;
            _p.V0w = on ? 1u : 0u;
            _p.V1 = new Vector4(Config.VortexLife, Config.VortexDecay, on ? 1f / _vortexStrength : 0f, 0f);
            // (off: the update below must see it this frame; the next start clears the old ones first)
            if (!on) { _vortSpawns.Clear(); _vortCleared = false; UpdateParams(); return; }
            if (!_vortCleared) { UpdateParams(); Run("VortClear", vc); _vortCleared = true; _vortHead = 0; }
            ShedVortices(dt);
            int n = Math.Min(MaxVortexSpawns, _vortSpawns.Count / 2);
            _p.V0y = (uint)n; _p.V0z = (uint)_vortHead;
            UpdateParams();
            if (n > 0)
            {
                Upload("VortSpawns", _vortSpawns.ToArray(), n * 2);
                Run("VortSpawn", n);
                _vortHead = (_vortHead + n) % vc;
            }
            _vortSpawns.Clear();
            Run("VortVelocity", vc);
            Run("VortMove", vc);
        }

        // ------------------------------------------------------------------ 0.27.0: spray, foam and bubbles (Ihmsen et al. 2012)

        int _diffuseSlots = 256;
        bool _diffuseAllocated, _hasFoam;
        /// <summary>0.27.0: the diffuse particles' slots (a token 256 until a liquid first asks for foam).</summary>
        public int DiffuseSlots => _diffuseSlots;
        public D3DBuffer DiffuseVertexBuffer => _res["DVerts"].Buffer;
        public D3DBuffer DiffuseIndexBuffer => _res["DIndices"].Buffer;
        int _diffuseEmitFrame = -1;
        uint _diffuseSeed;
        /// <summary>0.27.0: the budget's scale, lowered (from the readbacks, a few frames late) while the liquid asks for more than
        /// FoamSpawnsPerFrame, so a violent splash thins out evenly instead of the first threads taking the whole budget.</summary>
        float _diffuseScale = 1f;
        public float DiffuseScale => _diffuseScale;
        /// <summary>0.27.0: a live liquid makes foam (its amount over 0).</summary>
        public bool HasFoam => _hasFoam;
        /// <summary>0.27.0: the last Step ran the foam's kernels (they are skipped while there is none and none is made).</summary>
        public bool DiffuseRanLastFrame { get; private set; }

        /// <summary>0.27.0: makes the full-size foam buffers now (zeros: nothing drawn) if no liquid has asked for them yet. The
        /// renderer calls it while its foam mesh exists, so what it copies covers the whole mesh.</summary>
        public void EnsureFoamBuffers()
        {
            if (_diffuseAllocated) return;
            AllocateDiffuse(Config.FoamCapacity);
            _diffuseAllocated = true; _diffuseScale = 1f;
        }

        void AllocateDiffuse(int slots)
        {
            _diffuseSlots = Math.Max(256, slots);
            int dc = _diffuseSlots;
            Structured("DPos", dc, 16, true); Structured("DVel", dc, 16, true); Structured("DInfo", dc, 4, true);
            Raw("DVerts", dc * 7 * GasVertexBytes); Raw("DIndices", dc * 18 * 4);
        }

        /// <summary>0.27.0: every diffuse slot (xyz, life left; &lt;= 0: empty), its velocity (xyz, liquid as uint bits) and class
        /// (0 spray, 1 foam, 2 bubble), waiting for the GPU (tests).</summary>
        public void ReadDiffuse(out Vector4[] pos, out Vector4[] vel, out uint[] info)
        {
            int cap = DiffuseSlots;
            pos = ReadAll<Vector4>(_res["DPos"].Buffer, cap, 16);
            vel = ReadAll<Vector4>(_res["DVel"].Buffer, cap, 16);
            info = ReadAll<uint>(_res["DInfo"].Buffer, cap, 4);
        }

        /// <summary>0.27.0: the drawn diffuse discs (7 vertices and 18 indices each; as quads for the smoke shader 4 and 6), waiting
        /// for the GPU (tests).</summary>
        public void ReadDiffuseMesh(out float[] verts, out uint[] indices)
        {
            int n = Math.Max(0, Math.Min(Stats.DiffuseDrawn, DiffuseSlots));
            int vp = _foamQuadsDrawn ? 4 : 7, ip = _foamQuadsDrawn ? 6 : 18;
            verts = n > 0 ? ReadAll<float>(_res["DVerts"].Buffer, n * vp * GasVertexFloats, 4) : new float[0];
            indices = n > 0 ? ReadAll<uint>(_res["DIndices"].Buffer, n * ip, 4) : new uint[0];
        }

        /// <summary>After the liquid's substeps (it emits only when they ran: the neighbour lists are this frame's), before the
        /// surface re-sorts the particles. Skipped when nothing is alive (by the newest readback) and nothing was made since.</summary>
        void RunDiffuse(float dt, bool emit)
        {
            DiffuseRanLastFrame = false;
            if (!_diffuseAllocated || !(dt > 0f)) return;
            if (!_hasFoam) emit = false;
            if (!emit && Stats.DiffuseLive == 0 && Stats.Frame > _diffuseEmitFrame) return;
            if (emit) _diffuseEmitFrame = _frame;
            int cap = _diffuseSlots;
            // (the GPU's hard cap is twice the budget: the scale keeps it near the budget a few frames after a splash starts)
            _p.D0x = (uint)cap; _p.D0y = ++_diffuseSeed; _p.D0z = (uint)Math.Max(0, Math.Min(2 * Config.FoamSpawnsPerFrame, cap)); _p.D0w = emit ? 1u : 0u;
            // (a particle lives its liquid's FoamLifetime x 0.5 to 4/3, longer for stronger splashes: 1.5 to 4 s at the default 3)
            _p.D1 = new Vector4(_diffuseScale, dt, 0.5f, 4f / 3f);
            _p.G1 = new Vector4(_gasHeadAt, _p.G1.W);
            // 0.27.0: quads for the smoke shader, or discs; a switch starts the index buffer over (the tail is counted in the new shape)
            if (_foamQuads != _foamQuadsDrawn) { Zero(_res["DIndices"]); _foamQuadsDrawn = _foamQuads; }
            _p.S1.Z = _foamQuads ? 1f : 0f;
            UpdateParams();
            RunGroups("DiffuseBegin", 1);
            if (emit) { RunIndirect("DiffuseEmit", 0); RunGroups("DiffuseHead", 1); }
            Run("DiffuseUpdate", cap);
            Run("DiffuseDraw", cap);
            Run("DiffuseTail", cap * 18);
            DiffuseRanLastFrame = true;
        }

        // ------------------------------------------------------------------ 0.27.0: the thick liquid (Weiler et al. 2018)

        /// <summary>0.27.0: a thick liquid's Viscosity is held to this. Above it the solve's iterations climb steeply (at 2.5 cm
        /// particles about 31 a solve at 15 and 100 at 20: SoftbodyRnD's RND-REPORT.md, section 6).</summary>
        public const float ThickViscosityCap = FluidChannel.ThickViscosityCap;

        /// <summary>0.27.0: the kinematic viscosity (m²/s) a thick liquid's Viscosity value gives: none (XSPH) at 1 and under, then a
        /// log scale from 0.001 m²/s (glycerine) just over 1, 0.08 at 10 and 0.9 at 15 (to 10 m²/s at 20, past the cap); honey
        /// (about 0.004 m²/s) is about 4.</summary>
        public static float KinematicViscosity(float viscosity) =>
            viscosity > 1f ? (float)Math.Pow(10.0, -3.0 + 4.0 * Math.Min(19.0, viscosity - 1.0) / 19.0) : 0f;

        bool _ivAllocated, _hasThick;
        int _ivNeeded = -1;
        /// <summary>0.27.0: a live liquid is thick (the implicit solve has work).</summary>
        public bool HasThickLiquid => _hasThick;
        /// <summary>0.27.0: the iterations the last solve dispatched.</summary>
        public int IvDispatched { get; private set; }

        void AllocateImplicit(int n)
        {
            int ic = Math.Max(256, n);
            Structured("IvX", ic, 16, true); Structured("IvR", ic, 16, true); Structured("IvP", ic, 16, true); Structured("IvQ", ic, 16, true);
            Structured("IvPinv", 2 * ic, 16, true);
            Structured("IvGuess", ic, 16, true); Structured("IvGuess2", ic, 16, true);
        }

        /// <summary>0.27.0: one implicit viscosity solve over the substep's velocities in Delta, with the slow substeps'
        /// <paramref name="dt"/>. As many iterations are dispatched as the last solves needed plus a margin (the readback is a few
        /// frames late; a converged solve's remaining iterations do nothing), at most ImplicitMaxIterations.</summary>
        void RunImplicitViscosity(float dt)
        {
            int max = Math.Max(1, Config.ImplicitMaxIterations);
            int iters = _ivNeeded < 0 ? max : Math.Min(max, Math.Max(8, _ivNeeded + 8 + _ivNeeded / 4));
            _p.I0 = new Vector4(dt, Math.Max(1e-6f, Config.ImplicitTolerance), 0.01f * H * H, Math.Max(0f, Math.Min(1f, Config.ImplicitWarmStart)));
            _p.I1y = (uint)max;
            UpdateParams();
            RunIndirect("IvBegin", 0);
            RunIndirect("IvDiagonal", 0);
            RunGroups("IvStart", 1);
            RunIndirect("IvMatVec", 0);
            RunIndirect("IvResidual", 0);
            RunGroups("IvInit", 1);
            for (int k = 0; k < iters; k++)
            {
                RunIndirect("IvMatVec", 0);
                RunGroups("IvAlpha", 1);
                RunIndirect("IvUpdate", 0);
                RunGroups("IvBeta", 1);
                RunIndirect("IvDirection", 0);
            }
            Run("IvFinish", Config.Capacity);
            IvDispatched = iters;
        }

        /// <summary>0.27.0 (tests): sets the first <paramref name="count"/> particles' velocities, in the order ReadParticles gives.</summary>
        public void WriteVelocities(Vector4[] vel, int count)
        {
            var all = ReadAll<Vector4>(_vel.Buffer, Config.Capacity, 16);
            Array.Copy(vel, all, Math.Min(count, Math.Min(vel.Length, all.Length)));
            _ctx.UpdateSubresource(all, _vel.Buffer);
            WakeAll();
        }

        /// <summary>0.25.0: the water sleeps a tile at a time (not with the classic solver, or with sleeping off).</summary>
        bool Tiles => !Config.Classic && Config.SleepRadius > 0f;

        /// <summary>0.21.0: wakes every particle in the next step (a liquid's settings changed).</summary>
        public void WakeAll() { _wakeAll = true; _lastWakeFrame = _frame + 1; }

        /// <summary>0.23.0: the most wake boxes in one step (more are merged into their bounds).</summary>
        public const int MaxWakeBoxes = 128;
        readonly Vector4[] _wakeBoxes = new Vector4[2 * MaxWakeBoxes];
        /// <summary>0.23.0: wake boxes asked for so far (each one woke everything before 0.23.0; diagnostics).</summary>
        public long WakeBoxesAsked { get; private set; }
        int _wakeBoxCount;

        static float Volume(Vector4 lo, Vector4 hi) => Math.Max(0f, hi.X - lo.X) * Math.Max(0f, hi.Y - lo.Y) * Math.Max(0f, hi.Z - lo.Z);

        /// <summary>0.23.0: wakes the sleepers inside this box in the next step (where a still shape was and is not now).</summary>
        public void WakeIn(Vector3 lo, Vector3 hi)
        {
            WakeBoxesAsked++;
            if (_wakeBoxCount < MaxWakeBoxes) { _wakeBoxes[2 * _wakeBoxCount] = new Vector4(lo, 0f); _wakeBoxes[2 * _wakeBoxCount + 1] = new Vector4(hi, 0f); _wakeBoxCount++; }
            else
            {
                // full: grow the box that grows least to hold this one too (0.25.0; 0.23.0 grew the last one: a room's wall row of
                // 64 triangles going at once made one box over the whole room, which woke the top of the whole pool)
                int best = 0; float bestGrowth = float.MaxValue;
                var nlo = new Vector4(lo, 0f); var nhi = new Vector4(hi, 0f);
                for (int b = 0; b < MaxWakeBoxes; b++)
                {
                    Vector4 blo = _wakeBoxes[2 * b], bhi = _wakeBoxes[2 * b + 1];
                    Vector4 ulo = Vector4.Min(blo, nlo), uhi = Vector4.Max(bhi, nhi);
                    float growth = Volume(ulo, uhi) - Volume(blo, bhi);
                    if (growth < bestGrowth) { bestGrowth = growth; best = b; }
                }
                _wakeBoxes[2 * best] = Vector4.Min(_wakeBoxes[2 * best], nlo); _wakeBoxes[2 * best + 1] = Vector4.Max(_wakeBoxes[2 * best + 1], nhi);
            }
            _lastWakeFrame = _frame + 1;
        }

        /// <summary>0.21.0: the surface is rebuilt in the next step even if nothing moved (its settings changed).</summary>
        void SurfaceChanged() { _lastMoveFrame = _frame + 1; _surfaceReset = true; }

        public void SetLiquids(GpuLiquid[] liquids, int count)
        {
            count = Math.Min(count, Config.MaxSources);
            SourceCount = count;
            _liquids = liquids;
            // 0.21.0: a changed liquid wakes everything (its own particles may now flow), and liquids with a lifetime keep
            // the substeps running (their particles' ages are only checked there)
            // (0.22.0: a new liquid, a new emitter, wakes nothing: only one that was there and changed)
            bool changed = false;
            _lifetimes = false; _hasThick = false; _hasFoam = false;
            for (int i = 0; i < count; i++)
            {
                var a = liquids[i];
                if (a.Alive != 0 && a.B.W > 0f) _lifetimes = true;
                if (a.Alive != 0 && a.D.Y > 0f) _hasThick = true;   // 0.27.0
                if (a.Alive != 0 && a.D.X > 0f) _hasFoam = true;    // 0.27.0
                if (changed || i >= _liquidsSeen.Length) continue;
                var b = _liquidsSeen[i];
                // (0.27.0: and a thick liquid's viscosity; its foam does not change how it moves)
                if (a.A != b.A || a.B != b.B || a.Color != b.Color || a.Mask != b.Mask || a.Alive != b.Alive || a.D.Y != b.D.Y) changed = true;
            }
            if (_liquidsSeen.Length != count) _liquidsSeen = new GpuLiquid[count];
            Array.Copy(liquids, _liquidsSeen, count);
            if (changed && _frame > 0) WakeAll();
            _p.U3w = (uint)count;
            if (count > 0) Upload("Liquids", liquids, count);
            // 0.27.0: the thick liquid's and the foam's buffers, the first time a liquid asks for them (kept from then on)
            if (_hasThick && !_ivAllocated) { AllocateImplicit(Config.Capacity); _ivAllocated = true; _ivNeeded = -1; }
            if (_hasFoam) EnsureFoamBuffers();
        }

        /// <summary>The colliders and the grid that pairs them with particles (see <see cref="GpuShapeGrid"/>).</summary>
        public void SetShapes(GpuShapeGrid grid)
        {
            _shapeCount = Math.Min(grid.ShapeCount, Config.MaxShapes);
            _haveShapes = _shapeCount > 0;
            // 0.21.0: a moving shape keeps the substeps running (it may wake sleepers). 0.25.0: and they are the only shapes a sleeper
            // checks: one faster than the wake speed (or a user's part) wakes within WAKE_REACH, a slower one (from 2 cm/s) the water
            // it touches (a paddle at 0.5 m/s went through a sleeping pool without moving it)
            int moving = 0;
            if (_moving == null || _moving.Length < Config.MaxShapes) _moving = new uint[Config.MaxShapes];
            _movingShapeList.Clear();   // 0.27.0: what may shed vortex rings in the gas
            for (int i = 0; i < _shapeCount; i++)
            {
                var v = grid.Shapes[i].V;
                if (v.X * v.X + v.Y * v.Y + v.Z * v.Z > 4e-4f) { _moving[moving++] = (uint)i; _movingShapeList.Add(grid.Shapes[i]); }
            }
            _movingShapes = moving > 0;
            _p.U5z = (uint)moving;
            if (moving > 0) Upload("MovingShapes", _moving, moving);
            _p.U0z = (uint)_shapeCount;
            if (!_haveShapes) { _p.U0w = 0; _p.U2x = _p.U2y = _p.U2z = 0; _p.U5z = 0; return; }
            Upload("Shapes", grid.Shapes, _shapeCount);
            _globalCount = Math.Min(grid.GlobalCount, Config.MaxShapes);
            _p.U0w = (uint)_globalCount;
            if (_globalCount > 0) Upload("GlobalShapes", grid.Globals, _globalCount);
            _sgCells = grid.Cells;
            int needed = grid.GridLength;
            if (needed > _res["ShapeGrid"].Elements)
            {
                _sgListCap = Math.Max(needed, _res["ShapeGrid"].Elements * 2);
                Structured("ShapeGrid", _sgListCap, 4, false, dynamic: true);
            }
            if (_sgCells > 0) Upload("ShapeGrid", grid.Grid, needed);
            _p.U2x = (uint)grid.Nx; _p.U2y = (uint)grid.Ny; _p.U2z = (uint)grid.Nz;
            _p.SgMin = new Vector4(grid.Min, grid.Cell);
        }

        void Upload<T>(string name, T[] data, int count) where T : struct
        {
            var buf = _res[name].Buffer;
            var box = _ctx.MapSubresource(buf, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Write(box.DataPointer, data, 0, Math.Min(count, data.Length)); }
            finally { _ctx.UnmapSubresource(buf, 0); }
        }

        void UpdateParams()
        {
            var box = _ctx.MapSubresource(_cb, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Write(box.DataPointer, ref _p); }
            finally { _ctx.UnmapSubresource(_cb, 0); }
        }

        // ------------------------------------------------------------------ dispatching

        void Bind(Kernel k)
        {
            Array.Clear(_uavs, 0, _uavs.Length);
            Array.Clear(_srvs, 0, _srvs.Length);
            foreach (var b in k.Bindings)
            {
                if (b.Type == HlslBindType.CBuffer) continue;
                if (!_res.TryGetValue(b.Name, out var r)) throw new InvalidOperationException($"{k.Name}: no resource {b.Name}");
                if (b.IsUav) { if (b.Slot < 8) _uavs[b.Slot] = r.Uav; else throw new InvalidOperationException($"{k.Name}: UAV {b.Name} in slot {b.Slot}"); }
                else if (b.IsSrv) { if (b.Slot < 8) _srvs[b.Slot] = r.Srv; else throw new InvalidOperationException($"{k.Name}: SRV {b.Name} in slot {b.Slot}"); }
            }
            var cs = _ctx.ComputeShader;
            cs.Set(k.Shader);
            cs.SetConstantBuffer(0, _cb);
            // (0.27.0: the UAVs first. A buffer read here through an SRV (IvDone, the thick liquid's scalars) was a UAV of the last
            // dispatch, and Direct3D 11 forces an SRV to null while its buffer is still bound as a UAV: the solve read zeros)
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

        void RunIndirect(string kernel, int argsOffset)
        {
            Bind(_kernels[kernel]);
            _ctx.DispatchIndirect(_res["Args"].Buffer, argsOffset);
        }

        void Sort()
        {
            Run("ClearCells", _table + 1);
            RunIndirect("CountCells", 0);
            RunGroups("ScanBlocks", _blocks);
            RunGroups("ScanSums", 1);
            Run("AddSums", _table + 1);
            RunIndirect("Scatter", 0);
            RunIndirect("Reorder", 0);
            RunIndirect("ReorderAnchor", 0);
            { var t = _res["Anchor"]; _res["Anchor"] = _res["Anchor2"]; _res["Anchor2"] = t; }
            if (_ivAllocated)
            {
                // 0.27.0: the thick liquid's warm start follows the particles
                RunIndirect("ReorderIvGuess", 0);
                var t = _res["IvGuess"]; _res["IvGuess"] = _res["IvGuess2"]; _res["IvGuess2"] = t;
            }
            SwapParticles();
            RunGroups("SetCount", 1);
        }

        // the compute stage as the renderer had it (restored after each frame, so Unity's own state cache stays right)
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

        /// <summary>
        /// One frame: spawns, the substeps, the per-frame counts (queued for reading back), and the surface if asked. Never
        /// waits for the GPU. With <paramref name="trim"/>, liquids with a Trim count lose that many particles this frame.
        /// </summary>
        public void Step(float dt, GpuSpawn[] spawns, int spawnCount, bool trim, bool buildSurface)
        {
            if (!(dt > 0f) || float.IsNaN(dt) || float.IsInfinity(dt)) dt = 0f;
            float realDt = Math.Min(dt, 1f / 30f);   // 0.26.0: the gas's step (it is not the liquid's fixed substeps)
            // 0.25.0: substeps of a fixed length (Substeps in a 60th of a second), as many as the frame's time holds, at most a 30th
            // of a second's worth (a slower frame runs slow; its excess is dropped, not owed). 4 substeps per frame whatever it
            // took made the water's rest depend on the frame rate (8 % squeezed at 30 fps against 1.7 % at 60, never asleep), and a
            // changing frame rate made a pool breathe up and down by up to 24 cm (FluidSim.Step)
            int subFix = Math.Max(1, Config.Substeps);
            float hFix = 1f / (60f * subFix);
            _acc = Math.Min(_acc + dt, (2 * subFix + 1) * hFix);
            int steps = Math.Min(2 * subFix, (int)Math.Floor(_acc / hFix + 1e-3f));
            _acc = Math.Max(0f, Math.Min(hFix, _acc - steps * hFix));
            dt = steps * hFix;   // the time this frame simulates
            _frame++;
            SaveState();
            var slot = _ring[_ringNext];
            bool readback = !slot.Pending;
            try
            {
                if (readback) { _ctx.Begin(slot.Disjoint); _ctx.End(slot.T0); }
                if (_ivAllocated) RunGroups("IvFrameBegin", 1);   // 0.27.0: the frame's thick-liquid counts
                // 0.25.0: the classic solver switched on or off: everything wakes (the two put water to sleep differently)
                uint tiles = Tiles ? 1u : 0u;
                if (_p.U5w != tiles) { _p.U5w = tiles; WakeAll(); }
                int sub = Math.Max(1, Config.Substeps), iters = Math.Max(1, Config.Iterations);
                // 0.21.0: the slow effects every `period` substeps, with that many substeps' time (as 0.20.0's 2 substeps)
                int period = Math.Max(1, sub / 2);
                _p.Sleep.W = period;
                // 0.21.0: nothing awake (by the newest readback, taken after anything that could wake it) and nothing to
                // wake it now: the substeps are skipped, and so is the surface if it is up to date (Box3D's sleeping islands)
                // (0.25.0: with tiles, also when the only water awake is a few stragglers that stayed put: a drop on a wall or a thin
                // edge never sleeps, so its puddle's holes keep closing, and kept a whole sleeping pool simulating every frame)
                bool idle = Stats.Awake == 0 || (Tiles && Stats.Awake <= Math.Max(64, Stats.Count / 50) && Stats.Unsettled == 0);
                bool skip = dt > 0f && Config.SleepRadius > 0f && Stats.Frame > _lastWakeFrame && idle && Stats.Count > 0 &&
                            spawnCount <= 0 && !trim && !_wakeAll && _wakeBoxCount == 0 && !_movingShapes && !_lifetimes;
                SkippedLastFrame = skip;
                if (!skip && dt > 0f) _lastMoveFrame = _frame;
                if (spawnCount > 0 || trim || _wakeAll) _lastWakeFrame = _frame;
                float h = hFix;
                _p.F1.W = h;
                _p.F3.Z = dt;
                _buildTime += dt;
                _p.U1x = 0; _p.U1y = (uint)Math.Max(0, Math.Min(spawnCount, Config.Capacity)); _p.U3z = 0;
                UpdateParams();
                Run("ClearCounts", Math.Max(1, Config.MaxSources));
                if (_p.U1y > 0)
                {
                    Upload("Spawns", spawns, (int)_p.U1y);
                    Run("SpawnParticles", (int)_p.U1y);
                }
                RunGroups("ParticleArgs", 1);
                if (spawnCount > 0) _lastMoveFrame = _frame;
                if (_wakeBoxCount > 0)
                {
                    Upload("WakeBoxes", _wakeBoxes, 2 * _wakeBoxCount);
                    _p.U5x = (uint)_wakeBoxCount; UpdateParams();
                    RunIndirect("WakeInBoxes", 0);
                    _p.U5x = 0; UpdateParams();
                    _wakeBoxCount = 0;
                }
                if (dt > 0f && !skip)
                {
                    for (int s = 0; s < steps; s++)
                    {
                        // (0.25.0: the slow effects' turn counted across frames)
                        uint slowNow = (++_substeps) % period == 0 ? 1u : 0u, wakeNow = s == 0 && _wakeAll ? 1u : 0u;
                        if (_p.U4x != slowNow || _p.U4y != wakeNow) { _p.U4x = slowNow; _p.U4y = wakeNow; UpdateParams(); }
                        if (s == 0 && trim && _liquids != null)
                        {
                            if (_trim == null) _trim = new int[Config.MaxSources];
                            for (int i = 0; i < _trim.Length; i++) _trim[i] = i < SourceCount && _liquids[i].Trim != 0 ? (int)Math.Min(_liquids[i].Trim, int.MaxValue) : 0;
                            _ctx.UpdateSubresource(_trim, _res["TrimLeft"].Buffer);
                            _p.U3z = 1; UpdateParams();
                        }
                        RunIndirect("Predict", 0);
                        if (_p.U3z != 0 || _p.U4y != 0) { _p.U3z = 0; _p.U4y = 0; UpdateParams(); }
                        Sort();
                        RunIndirect("Neighbours", 0);
                        for (int it = 0; it < iters; it++)
                        {
                            uint last = it == iters - 1 ? 1u : 0u;
                            if (_p.U1x != last) { _p.U1x = last; UpdateParams(); }
                            RunIndirect("Lambdas", 0);
                            RunIndirect("Deltas", 0);
                            RunIndirect("ApplyDeltas", 0);
                            if (_haveShapes) RunIndirect("Collide", 0);
                        }
                        RunIndirect("VelocityA", 0);
                        if (!Config.Classic)
                        {
                            // 0.25.0: the divergence pass (Delta in, Vtmp out)
                            RunIndirect("DivLambdas", 0);
                            RunIndirect("DivDeltas", 0);
                            var t = _res["Delta"]; _res["Delta"] = _res["Vtmp"]; _res["Vtmp"] = t;
                        }
                        if (slowNow != 0)
                        {
                            // 0.27.0: the thick liquids' implicit viscosity, then the extra XSPH passes for the rest
                            if (_hasThick) RunImplicitViscosity(h * period);
                            for (int k = 0; k < _viscPasses - 1; k++)
                            {
                                RunIndirect("Viscosity", 0);
                                var t = _res["Delta"]; _res["Delta"] = _res["Vtmp"]; _res["Vtmp"] = t;
                            }
                            // 0.20.0: surface tension, then the last viscosity pass
                            RunIndirect("Normals", 0);
                            RunIndirect("Tension", 0);
                            { var t = _res["Delta"]; _res["Delta"] = _res["Vtmp"]; _res["Vtmp"] = t; }
                        }
                        RunIndirect("VelocityB", 0);
                        RunIndirect("Settle", 0);
                        _p.U1x = 0; UpdateParams();
                    }
                    _wakeAll = false;
                    if (Tiles)
                    {
                        // 0.25.0: the water sleeps (and wakes) a tile at a time
                        Run("ClearTiles", TileTable);
                        RunIndirect("TileTop", 0);
                        RunIndirect("TileCount", 0);
                        Run("TileDecide", TileTable);
                        RunIndirect("TileApply", 0);
                    }
                }
                // 0.27.0: spray, foam and bubbles (made only from substeps that ran; moved on with the frame's time otherwise)
                bool ran = dt > 0f && !skip && steps > 0;
                RunDiffuse(ran ? dt : realDt, ran);
                if (_gasUsed) RunGas(realDt);   // 0.26.0
                RunIndirect("FrameEnd", 0);
                // 0.21.0: the surface only when something moved since it was last built (or its settings changed)
                if (buildSurface && (_surfaceFrame < 0 || _lastMoveFrame >= _surfaceFrame)) { BuildSurface(); _surfaceFrame = _frame; SurfaceBuiltLastFrame = true; }
                else SurfaceBuiltLastFrame = false;
                if (readback)
                {
                    _ctx.End(slot.T1);
                    _ctx.End(slot.Disjoint);
                    Queue(slot, true);
                }
            }
            finally { RestoreState(); }
            Poll();
        }

        void BuildSurface()
        {
            // 0.22.0: blocks are kept between builds; the field is recomputed only near particles that moved since the last one
            _build++;
            _p.U4z = _build;
            _p.U4w = _surfaceReset || !Config.SurfaceCache ? 1u : 0u;
            _p.U5y = Config.SurfaceNarrowBand ? 1u : 0u;
            _surfaceReset = false;
            float since = (float)(_buildTime + _p.F3.Z);
            _buildTime = 0;
            // (one kernel radius plus a cell: what moved changes its neighbours' smoothing and radius only by about a thirtieth of
            // its move, which the every-8th-build refresh takes up)
            _p.Surf2 = new Vector4(since, _p.SurfP.X + _p.GMin.W, _p.Surf2.Z, 0f);
            UpdateParams();
            RunIndirect("KeysFromPos", 0);
            Sort();
            RunGroups("SurfaceBegin", 1);
            Run("ClearBricks", _brickTable);
            RunIndirect("MarkBricks", 0);
            RunIndirect("MarkDirty", 0);
            RunGroups("SurfaceArgs", 1);
            RunIndirect("Radii", 0);
            RunIndirect("MarkShallow", 0);
            RunIndirect("Field", 12);
            RunIndirect("CellVertices", 12);
            RunGroups("ColorArgs", 1);
            RunIndirect("VertexColors", 24);
            if (!Config.Classic && Config.MeshSmoothing > 0)
            {
                // 0.25.0: Taubin's weighted Laplacian passes (in, out, in, out...)
                RunIndirect("SmoothNbrs", 24);
                for (int k = 0; k < Config.MeshSmoothing; k++)
                {
                    RunIndirect(k % 2 == 0 ? "SmoothIn" : "SmoothOut", 24);
                    { var t = _res["SPos"]; _res["SPos"] = _res["SPosTmp"]; _res["SPosTmp"] = t; }
                    { var t = _res["SNrm"]; _res["SNrm"] = _res["SNrmTmp"]; _res["SNrmTmp"] = t; }
                }
                RunIndirect("SmoothWrite", 24);
            }
            RunIndirect("Quads", 12);
            RunGroups("TailArgs", 1);
            RunIndirect("ClearTail", 36);
        }

        uint _build;
        bool _surfaceReset = true;
        double _buildTime;

        /// <summary>0.22.0: the next surface build recomputes every block (tests; settings that change the field).</summary>
        public void ForgetSurface() { _surfaceReset = true; SurfaceChanged(); }

        void Queue(Slot slot, bool timed)
        {
            // unbind before copying out of them
            _ctx.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[8]);
            _ctx.CopyResource(_res["State"].Buffer, slot.State);
            _ctx.CopySubresourceRegion(_res["SourceCounts"].Buffer, 0, new ResourceRegion(0, 0, 0, Math.Max(1, SourceCount) * 4, 1, 1), slot.Counts, 0);
            _ctx.End(slot.Done);
            slot.Frame = _frame; slot.Pending = true; slot.Timed = timed; slot.DiffuseScale = _diffuseScale;
            _ringNext = (Array.IndexOf(_ring, slot) + 1) % _ring.Length;
        }

        /// <summary>Takes in every finished readback (never waits). True if the stats changed.</summary>
        public bool Poll()
        {
            bool any = false;
            for (int k = 0; k < _ring.Length; k++)
            {
                // oldest first
                var s = _ring[(_ringNext + k) % _ring.Length];
                if (!s.Pending) continue;
                if (!_ctx.IsDataAvailable(s.Done)) break;
                Take(s);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Waits until at most <paramref name="frames"/> frames are queued on the GPU (what a swap chain's frame latency does
        /// to the renderer; the offline tests call it to run like a game does).
        /// </summary>
        public void Throttle(int frames)
        {
            foreach (var s in _ring)
            {
                if (!s.Pending || s.Frame > _frame - frames) continue;
                _ctx.Flush();
                while (!_ctx.IsDataAvailable(s.Done)) System.Threading.Thread.Sleep(0);
                Take(s);
            }
        }

        /// <summary>Waits for the GPU and takes in a readback of the last frame (tests).</summary>
        public void Finish()
        {
            _ctx.Flush();
            for (int k = 0; k < _ring.Length; k++)
            {
                var s = _ring[(_ringNext + k) % _ring.Length];
                if (!s.Pending) continue;
                while (!_ctx.IsDataAvailable(s.Done)) System.Threading.Thread.Sleep(0);
                Take(s);
            }
            if (Stats.Frame < _frame)
            {
                // the last frames found the ring full: read this one now
                var slot = _ring[_ringNext];
                Queue(slot, false);
                _ctx.Flush();
                while (!_ctx.IsDataAvailable(slot.Done)) System.Threading.Thread.Sleep(0);
                Take(slot);
            }
        }

        void Take(Slot s)
        {
            s.Pending = false;
            if (s.Frame < Stats.Frame) return;
            var st = new uint[StateSize];
            var box = _ctx.MapSubresource(s.State, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Read(box.DataPointer, st, 0, StateSize); } finally { _ctx.UnmapSubresource(s.State, 0); }
            int n = Math.Max(1, SourceCount);
            if (Stats.SourceCounts.Length != Config.MaxSources) Stats.SourceCounts = new uint[Config.MaxSources];
            box = _ctx.MapSubresource(s.Counts, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Read(box.DataPointer, Stats.SourceCounts, 0, n); } finally { _ctx.UnmapSubresource(s.Counts, 0); }
            for (int i = n; i < Stats.SourceCounts.Length; i++) Stats.SourceCounts[i] = 0;
            Stats.Frame = s.Frame;
            Stats.Count = (int)st[0];
            Stats.Vertices = (int)Math.Min(st[1], (uint)Config.VertexCapacity);
            Stats.Indices = (int)Math.Min(st[2], (uint)Config.IndexCapacity);
            Stats.VerticesNeeded = (int)Math.Min(st[1], int.MaxValue); Stats.IndicesNeeded = (int)Math.Min(st[2], int.MaxValue); Stats.BricksNeeded = (int)Math.Min(st[20], int.MaxValue);
            Stats.Contacts = (int)st[9];
            Stats.Bricks = (int)st[20];   // 0.22.0: the blocks of that build (st[12] also counts kept ones it no longer used)
            Stats.SurfaceTruncated = st[1] > (uint)Config.VertexCapacity || st[2] > (uint)Config.IndexCapacity || st[12] > (uint)_maxBricks;
            Stats.Removed = (int)st[13];
            Stats.Spawned = (int)st[15];
            Stats.MeanNeighbours = st[0] > 0 ? (float)st[14] / st[0] : 0f;
            Stats.MaxSpeed = AsFloat(st[16]);
            Stats.Awake = (int)st[17];
            Stats.Unsettled = Tiles ? (int)st[25] : int.MaxValue;
            Stats.GasPuffs = (int)st[28]; Stats.GasDrawn = (int)Math.Min(st[29], (uint)Config.GasCapacity);
            Stats.GasMin = new Vector3(FromOrdered(st[30]), FromOrdered(st[31]), FromOrdered(st[32]));
            Stats.GasMax = new Vector3(FromOrdered(st[33]), FromOrdered(st[34]), FromOrdered(st[35]));
            Stats.BricksComputed = (int)st[21];
            Stats.BricksNew = (int)st[22]; Stats.BricksMoved = (int)st[23]; Stats.BricksDeep = (int)st[24];
            Stats.Min = new Vector3(FromOrdered(st[3]), FromOrdered(st[4]), FromOrdered(st[5]));
            Stats.Max = new Vector3(FromOrdered(st[6]), FromOrdered(st[7]), FromOrdered(st[8]));
            // 0.27.0: the vortex particles, the spray, foam and bubbles, and the thick liquid's solves
            Stats.Vortices = _vortexStrength > 0f ? (int)st[26] : 0;
            Stats.DiffuseLive = _diffuseAllocated ? (int)st[42] : 0;
            Stats.DiffuseSpray = (int)st[43]; Stats.DiffuseFoam = (int)st[44]; Stats.DiffuseBubbles = (int)st[45];
            Stats.DiffuseDrawn = (int)Math.Min(st[46], (uint)_diffuseSlots); Stats.DiffuseAsked = (int)st[41]; Stats.DiffuseMakers = (int)st[54];
            Stats.DiffuseMin = new Vector3(FromOrdered(st[48]), FromOrdered(st[49]), FromOrdered(st[50]));
            Stats.DiffuseMax = new Vector3(FromOrdered(st[51]), FromOrdered(st[52]), FromOrdered(st[53]));
            if (_diffuseAllocated && s.Frame >= _diffuseEmitFrame - 8)
            {
                // what the liquid would ask for at the full amount, against the budget (half way there each readback)
                double full = st[41] / Math.Max(1e-3, s.DiffuseScale);
                double want = full > Config.FoamSpawnsPerFrame ? Config.FoamSpawnsPerFrame / full : 1.0;
                _diffuseScale = (float)Math.Max(0.01, Math.Min(1.0, 0.5 * _diffuseScale + 0.5 * want));
            }
            Stats.IvIterationsMax = (int)st[60]; Stats.IvIterationsSum = (int)st[61]; Stats.IvSolves = (int)st[62]; Stats.IvUnconverged = (int)st[63];
            if (_ivAllocated && Stats.IvSolves > 0) _ivNeeded = Stats.IvIterationsMax;
            if (s.Timed && _ctx.IsDataAvailable(s.Disjoint))
            {
                var dj = _ctx.GetData<QueryDataTimestampDisjoint>(s.Disjoint);
                if (!dj.Disjoint && dj.Frequency > 0)
                {
                    long t0 = _ctx.GetData<long>(s.T0), t1 = _ctx.GetData<long>(s.T1);
                    Stats.GpuMs = (t1 - t0) * 1000.0 / dj.Frequency;
                }
            }
        }

        static float FromOrdered(uint u) => AsFloat((u & 0x80000000u) != 0 ? u & 0x7FFFFFFFu : ~u);

        // ------------------------------------------------------------------ reading everything back (tests, debugging)

        D3DBuffer _stagingBig;

        T[] ReadAll<T>(D3DBuffer src, int count, int stride) where T : struct
        {
            int bytes = Math.Max(4, count * stride);
            if (_stagingBig == null || _stagingBig.Description.SizeInBytes < bytes)
            {
                _stagingBig?.Dispose();
                _stagingBig = Staging(Math.Max(bytes, 1 << 20));
            }
            var result = new T[Math.Max(0, count)];
            if (count == 0) return result;
            _ctx.CopySubresourceRegion(src, 0, new ResourceRegion(0, 0, 0, bytes, 1, 1), _stagingBig, 0);
            var box = _ctx.MapSubresource(_stagingBig, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            try { Utilities.Read(box.DataPointer, result, 0, count); } finally { _ctx.UnmapSubresource(_stagingBig, 0); }
            return result;
        }

        /// <summary>Waits for the GPU and reads the particles (positions with their source, velocities with their age).</summary>
        public int ReadParticles(out Vector4[] pos, out Vector4[] vel)
        {
            var st = ReadAll<uint>(_res["State"].Buffer, StateSize, 4);
            int n = (int)Math.Min(st[0], (uint)Config.Capacity);
            pos = ReadAll<Vector4>(_pos.Buffer, n, 16);
            vel = ReadAll<Vector4>(_vel.Buffer, n, 16);
            return n;
        }

        /// <summary>Waits for the GPU and reads the surface built last.</summary>
        public void ReadSurface(out float[] verts, out uint[] indices)
        {
            var st = ReadAll<uint>(_res["State"].Buffer, StateSize, 4);
            int nv = (int)Math.Min(st[1], (uint)Config.VertexCapacity), ni = (int)Math.Min(st[2], (uint)Config.IndexCapacity);
            verts = ReadAll<float>(_res["Verts"].Buffer, nv * 10, 4);
            indices = ReadAll<uint>(_res["Indices"].Buffer, ni, 4);
        }

        public uint[] ReadState() => ReadAll<uint>(_res["State"].Buffer, StateSize, 4);

        public void Dispose()
        {
            foreach (var k in _kernels.Values) k.Shader?.Dispose();
            _kernels.Clear();
            var seen = new HashSet<Res>();
            foreach (var r in _res.Values) if (seen.Add(r)) r.Dispose();
            foreach (var r in new[] { _pos, _vel, _pred, _pos2, _vel2, _pred2 }) if (r != null && seen.Add(r)) r.Dispose();
            _res.Clear();
            _cb?.Dispose(); _cb = null;
            _stagingBig?.Dispose(); _stagingBig = null;
            foreach (var s in _ring)
            {
                if (s == null) continue;
                s.State?.Dispose(); s.Counts?.Dispose(); s.Done?.Dispose(); s.Disjoint?.Dispose(); s.T0?.Dispose(); s.T1?.Dispose();
            }
        }
    }
}
