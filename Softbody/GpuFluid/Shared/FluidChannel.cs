// 0.16.0: the shared-memory channel between the Softbody plugin (FrooxEngine side) and the GPU fluid in the Softbody
// renderer fork (Renderite.Unity). Plain blittable structs and offsets only, no engine or Unity types, so both sides compile
// this same file (the plugin on .NET 10, the renderer on netstandard2.0 / Mono).
//
// The plugin writes the frame (emitters, colliders) under a sequence lock: Seq is odd while it writes and even when the
// frame is whole; the renderer copies the block and keeps it only if Seq was the same even number before and after. The
// renderer writes its own block (particle counts, the liquid's bounds, timings) the same way. The renderer opens the
// handshake event once its GPU fluid has compiled and runs; without it the plugin simulates on the CPU. In the plugin these
// types are internal (SOFTBODY_PLUGIN), so the engine registers nothing new from them.
#nullable disable
using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Softbody.FluidGpu
{
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    static class FluidChannel
    {
        public const string MapName = @"Local\Softbody.Fluid.v1";
        public const string HandshakeName = @"Local\Softbody.Fluid.Gpu.v1";
        public const int Magic = 0x46534253;   // "SBSF"
        public const int Version = 8;   // 0.17.0: SurfaceDetail, the renderer's ParticleSize / SurfaceTruncated; 0.18.0: DropletSize; 0.19.0: RandomVelocity, SurfaceDetailUsed; 0.25.0: Flags; 0.26.0: gas; 0.27.0: foam, thick liquid, gas vortices

        public const int MaxEmitters = 256;
        public const int MaxColliders = 8192;

        // layout of the block
        public const int HeaderOffset = 0;
        public const int HeaderSize = 256;
        public const int BackOffset = HeaderOffset + HeaderSize;              // the renderer's block
        public const int BackCountsOffset = BackOffset + 256;                 // (id, count) pairs
        public const int BackSize = 256 + MaxEmitters * 8;
        public const int EmittersOffset = BackOffset + BackSize;
        public const int EmitterSize = 192;
        public const int CollidersOffset = EmittersOffset + MaxEmitters * EmitterSize;
        public const int ColliderSize = 112;
        // 0.26.0: the gas emitters
        public const int MaxGasEmitters = 64;
        public const int GasEmittersOffset = CollidersOffset + MaxColliders * ColliderSize;
        public const int GasEmitterSize = 128;
        public const int TotalSize = GasEmittersOffset + MaxGasEmitters * GasEmitterSize;

        // collider kinds (the same numbers as the plugin's FleshColliderKind; meshes arrive as their triangles)
        public const int KindPlane = 0, KindSphere = 1, KindCapsule = 2, KindBox = 3, KindCylinder = 4, KindCone = 5, KindTriangle = 6;

        // collider categories (a liquid's CollideMask picks which it hits)
        public const uint CategoryWorld = 1, CategoryHead = 2, CategoryBody = 4, CategoryHands = 8;

        /// <summary>0.27.0: a thick liquid's Viscosity is held to this (past it the implicit solve's iterations climb steeply).</summary>
        public const float ThickViscosityCap = 15f;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    struct FluidHeader
    {
        public int Magic;
        public int Version;
        public int Seq;               // odd while the plugin writes
        public int Frame;             // the plugin's frame number
        public int Active;            // 1: simulate and show this frame (the focused world has emitters on the GPU)
        public int Owner;             // which world writes (a new owner clears the liquid)
        public int EmitterCount;
        public int ColliderCount;
        public float Dt;
        public float Time;            // the plugin's clock (seconds), so frames the renderer missed still count
        public float ParticleSize;
        public int Substeps;
        public int Iterations;
        public int MaxParticles;      // the world's cap (the GPU's capacity)
        public int MeshAssetId;       // the plugin's placeholder surface mesh: the renderer draws with its renderer's materials
        public float KillMinX, KillMinY, KillMinZ;
        public float KillMaxX, KillMaxY, KillMaxZ;
        public float SurfaceDetail;   // 0.17.0: how fine the surface is (0.5..3)
        public float DropletSize;     // 0.18.0: how big lone droplets and thin sheets are drawn (0.5..3)
        public int Flags;             // 0.25.0: 1 the classic solver (0.24.0's, an emergency backup)
        public int GasEmitterCount;   // 0.26.0
        public int GasMeshAssetId;    // 0.26.0: the plugin's placeholder gas mesh (drawn with its renderer's material)
        public float HeadX, HeadY, HeadZ;   // 0.26.0: the local user's head (the gas's puffs face it)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    struct FluidBack
    {
        public int Seq;               // odd while the renderer writes
        public int Frame;             // the plugin frame it last simulated
        public int Ready;             // 1 while the GPU fluid runs
        public int Particles;
        public float BoundsMinX, BoundsMinY, BoundsMinZ;
        public float BoundsMaxX, BoundsMaxY, BoundsMaxZ;
        public float StepMs;          // GPU time of the last frame read back
        public float CpuMs;           // the render thread's time preparing it
        public int SurfaceVertices;
        public int Error;             // nonzero: see the renderer's log (Player.log)
        public int Owner;             // the owner it simulates
        public int Drawing;           // 1 when it found the placeholder renderer and draws the surface
        public int EmitterCountsN;    // how many (id, count) pairs follow at BackCountsOffset
        public float ParticleSize;    // 0.17.0: the particle size it simulates with
        public int SurfaceTruncated;  // 0.17.0: 1 when the surface ran out of vertices or blocks
        public float SurfaceDetailUsed; // 0.19.0: the detail the surface is built at (lower while the asked one does not fit)
        public int GasPuffs;          // 0.26.0: live puffs
        public int GasDrawing;        // 0.26.0: 1 when it found the gas's placeholder renderer and draws it
        public float GasMinX, GasMinY, GasMinZ, GasMaxX, GasMaxY, GasMaxZ;   // 0.26.0: the gas's bounds (min > max: none)
        public int FoamParticles;     // 0.27.0: live spray, foam and bubble particles
        public int FoamDrawing;       // 0.27.0: 1 when it draws them (with the gas's placeholder renderer's material)
        public int ThickIterations;   // 0.27.0: the most iterations a thick-liquid solve took in that frame (0: none ran)
        public int ThickUnconverged;  // 0.27.0: solves in that frame that stopped at the iteration cap
        public int Vortices;          // 0.27.0: live vortex particles in the gas
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    struct FluidEmitterRecord
    {
        public int Id;                // stable per emitter (its component's reference id)
        public int Mode;              // 0 pour, 1 volume
        public int Pouring;
        public int RefillCount;
        public int ClearCount;
        public int MaxParticles;
        public float Rate, Speed, NozzleRadius;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float VelX, VelY, VelZ;
        public float HalfX, HalfY, HalfZ;   // volume: half the block (scaled)
        // the liquid
        public float Viscosity, Cohesion, Adhesion, Friction, Density, Gravity, Damping, Lifetime;
        public uint CollideMask;
        public float ColR, ColG, ColB, ColA;
        public int Active;            // the emitter's slot is active and the component enabled
        public float RandomVelocity;  // 0.19.0: m/s, a random push for each new particle
        public float Foam;            // 0.27.0: how much spray, foam and bubbles it makes (0: none; 1 the usual amount)
        public float FoamLifetime;    // 0.27.0: seconds a foam particle lasts (spray and bubbles age a tenth as fast)
        public int Thick;             // 0.27.0: 1: its Viscosity above 1 is a real (implicit) viscosity, up to 15
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    struct FluidColliderRecord
    {
        public int Kind;
        public uint Category;
        public float AX, AY, AZ;
        public float BX, BY, BZ;
        public float CX, CY, CZ;
        public float R;
        public float QX, QY, QZ, QW;
        public int TwoSided;
        public float VelX, VelY, VelZ;
    }

    /// <summary>0.26.0: a gas emitter (puffs of smoke or steam: see DESIGN-gas.md).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    struct GasEmitterRecord
    {
        public int Id;                // stable per emitter (its component's reference id)
        public int Active;            // enabled and in an active slot
        public int Emitting;
        public int MaxPuffs;
        public float Rate;            // puffs a second
        public float Speed;           // m/s out of the nozzle (along its +Z)
        public float NozzleRadius;
        public float Lifetime;        // seconds
        public float Buoyancy;        // m/s² upwards (negative: it sinks)
        public float Drag;            // 1/s: how fast it takes on the air's motion
        public float Swirl;           // m/s of the swirling air
        public float StartSize, EndSize;   // a puff's width at birth and at the end of its life (metres)
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float VelX, VelY, VelZ;
        public float ColR, ColG, ColB, ColA;   // linear; A the opacity
        public uint CollideMask;
        public float Spread;          // 0..1: how far a puff's direction strays from the nozzle's
        public float Vortices;        // 0.27.0: how strongly things moving through it leave lasting swirls (vortex rings; 0: none)
        public int Reserved1, Reserved2;
    }

    /// <summary>Reading and writing the channel (both sides; each side only writes its own blocks).</summary>
#if SOFTBODY_PLUGIN
    internal
#else
    public
#endif
    sealed class FluidChannelIO : IDisposable
    {
        readonly MemoryMappedFile _map;
        readonly MemoryMappedViewAccessor _view;
        int _seq;

        FluidChannelIO(MemoryMappedFile map) { _map = map; _view = map.CreateViewAccessor(0, FluidChannel.TotalSize); }

        public static FluidChannelIO Open(string name = FluidChannel.MapName) => new FluidChannelIO(MemoryMappedFile.CreateOrOpen(name, FluidChannel.TotalSize));

        // ---- the plugin's side

        /// <summary>Writes a whole frame (header, emitters, colliders) under the sequence lock.</summary>
        public void WriteFrame(FluidHeader header, FluidEmitterRecord[] emitters, int emitterCount, FluidColliderRecord[] colliders, int colliderCount,
                               GasEmitterRecord[] gas = null, int gasCount = 0)
        {
            emitterCount = Math.Max(0, Math.Min(emitterCount, FluidChannel.MaxEmitters));
            colliderCount = Math.Max(0, Math.Min(colliderCount, FluidChannel.MaxColliders));
            gasCount = gas == null ? 0 : Math.Max(0, Math.Min(gasCount, FluidChannel.MaxGasEmitters));
            int seq = _view.ReadInt32(FluidChannel.HeaderOffset + 8);
            if ((seq & 1) != 0) seq++;
            _view.Write(FluidChannel.HeaderOffset + 8, seq + 1);
            Thread.MemoryBarrier();
            header.Magic = FluidChannel.Magic; header.Version = FluidChannel.Version; header.Seq = seq + 1;
            header.EmitterCount = emitterCount; header.ColliderCount = colliderCount; header.GasEmitterCount = gasCount;
            _view.Write(FluidChannel.HeaderOffset, ref header);
            if (emitterCount > 0) _view.WriteArray(FluidChannel.EmittersOffset, emitters, 0, emitterCount);
            if (colliderCount > 0) _view.WriteArray(FluidChannel.CollidersOffset, colliders, 0, colliderCount);
            if (gasCount > 0) _view.WriteArray(FluidChannel.GasEmittersOffset, gas, 0, gasCount);
            Thread.MemoryBarrier();
            _view.Write(FluidChannel.HeaderOffset + 8, seq + 2);
        }

        /// <summary>Marks the channel inactive (no world simulates on the GPU now).</summary>
        public void WriteInactive(int owner)
        {
            var h = ReadHeaderUnsafe();
            h.Active = 0; h.Owner = owner;
            WriteFrame(h, null, 0, null, 0);
        }

        FluidHeader ReadHeaderUnsafe() { _view.Read(FluidChannel.HeaderOffset, out FluidHeader h); return h; }

        /// <summary>The renderer's block and its (id, count) pairs; false if it was being written.</summary>
        public bool TryReadBack(out FluidBack back, int[] pairs)
        {
            int s1 = _view.ReadInt32(FluidChannel.BackOffset);
            _view.Read(FluidChannel.BackOffset, out back);
            if ((s1 & 1) != 0) return false;
            int n = Math.Max(0, Math.Min(back.EmitterCountsN, FluidChannel.MaxEmitters));
            if (pairs != null && n > 0) _view.ReadArray(FluidChannel.BackCountsOffset, pairs, 0, Math.Min(pairs.Length, n * 2));
            Thread.MemoryBarrier();
            return _view.ReadInt32(FluidChannel.BackOffset) == s1;
        }

        // ---- the renderer's side

        /// <summary>A whole frame from the plugin; false if it was being written (try again next frame) or is not one.</summary>
        public bool TryReadFrame(out FluidHeader header, ref FluidEmitterRecord[] emitters, ref FluidColliderRecord[] colliders)
        {
            GasEmitterRecord[] none = null;
            return TryReadFrame(out header, ref emitters, ref colliders, ref none);
        }

        /// <summary>0.26.0: the same with the gas emitters.</summary>
        public bool TryReadFrame(out FluidHeader header, ref FluidEmitterRecord[] emitters, ref FluidColliderRecord[] colliders, ref GasEmitterRecord[] gas)
        {
            int s1 = _view.ReadInt32(FluidChannel.HeaderOffset + 8);
            _view.Read(FluidChannel.HeaderOffset, out header);
            if ((s1 & 1) != 0 || header.Magic != FluidChannel.Magic || header.Version != FluidChannel.Version) return false;
            int ne = Math.Max(0, Math.Min(header.EmitterCount, FluidChannel.MaxEmitters));
            int nc = Math.Max(0, Math.Min(header.ColliderCount, FluidChannel.MaxColliders));
            if (emitters == null || emitters.Length < ne) emitters = new FluidEmitterRecord[Math.Max(ne, 16)];
            if (colliders == null || colliders.Length < nc) colliders = new FluidColliderRecord[Math.Max(nc, 256)];
            int ng = Math.Max(0, Math.Min(header.GasEmitterCount, FluidChannel.MaxGasEmitters));
            if (ne > 0) _view.ReadArray(FluidChannel.EmittersOffset, emitters, 0, ne);
            if (nc > 0) _view.ReadArray(FluidChannel.CollidersOffset, colliders, 0, nc);
            if (gas == null || gas.Length < ng) gas = new GasEmitterRecord[Math.Max(ng, 4)];
            if (ng > 0) _view.ReadArray(FluidChannel.GasEmittersOffset, gas, 0, ng);
            Thread.MemoryBarrier();
            if (_view.ReadInt32(FluidChannel.HeaderOffset + 8) != s1) return false;
            header.EmitterCount = ne; header.ColliderCount = nc; header.GasEmitterCount = ng;
            return true;
        }

        /// <summary>Writes the renderer's block and its (id, count) pairs under its own sequence lock.</summary>
        public void WriteBack(FluidBack back, int[] pairs, int pairCount)
        {
            pairCount = Math.Max(0, Math.Min(pairCount, FluidChannel.MaxEmitters));
            _seq = _view.ReadInt32(FluidChannel.BackOffset);
            if ((_seq & 1) != 0) _seq++;
            _view.Write(FluidChannel.BackOffset, _seq + 1);
            Thread.MemoryBarrier();
            back.Seq = _seq + 1;
            back.EmitterCountsN = pairCount;
            _view.Write(FluidChannel.BackOffset, ref back);
            if (pairCount > 0) _view.WriteArray(FluidChannel.BackCountsOffset, pairs, 0, pairCount * 2);
            Thread.MemoryBarrier();
            _view.Write(FluidChannel.BackOffset, _seq + 2);
        }

        public void Dispose() { _view?.Dispose(); _map?.Dispose(); }
    }
}
