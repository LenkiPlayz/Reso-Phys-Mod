// 0.16.0: the Softbody liquid on the GPU (Direct3D 11, cs_5_0), compiled at runtime by the renderer fork (and by the
// offline tests). The same method as the CPU version (Core/Fluid/FluidSim.cs): Position Based Fluids with per-liquid
// settings, a density constraint that only pushes apart (number density, corrections by inverse mass), neighbours found
// once per substep (at most 48), collisions with the colliders the plugin sends (meshes arrive as triangles), XSPH
// viscosity, attraction-only cohesion, friction and adhesion, and the same guards (half a spacing per iteration, the way
// it came out of boxes, no bounce, at most MaxGain). The surface is the same smoothed distance field and surface nets on
// a sparse grid (4 x 4 x 4 blocks of one world lattice, found through a hash table), written straight in the vertex
// layout of the renderer's mesh.
//
// Particles live in the first State[0] slots. Pos.w and Pred.w hold the particle's liquid (its source, as uint bits) and
// Vel.w its age, so a particle is three float4s and every kernel binds at most 8 UAVs (Direct3D 11.0's limit). Each
// substep the particles are counting-sorted into a hashed grid; removed ones get the key DEAD (= T) and sort to the end,
// which is how particles are removed, so the count never leaves the GPU.

#define GROUP 128
#define SCAN_GROUP 512           // threads per scan group (each scans 1024 entries)
#define MAXNB 48
#define FAR 1e9
#define EMPTY 0xFFFFFFFFu
#define NONE 0xFFFFFFFFu
// 0.20.0: surface tension and the drawn surface (the same constants as FluidSim.CohesionStrength / CurvatureStrength and
// FluidSurface.DropletFade / Smoothing on the CPU)
#define COHESION_STRENGTH 4.5
#define CURVATURE_STRENGTH 0.2
#define DROPLET_FADE 12.0
#define SMOOTHING 0.5
// 0.21.0: pressure-wave damping (FluidSim.PressureDamping)
#define PRESSURE_DAMPING 1.0
// 0.21.0: a shape faster than the wake speed wakes the sleepers within this many particle sizes of it (FluidSettings.WakeReach)
#define WAKE_REACH 8.0
// 0.21.0: pressed (FluidSim.PressedDensity)
#define PRESSED_DENSITY 1.01
// 0.21.0: never asleep under this share of rest density (FluidSim.SleepDensity)
#define SLEEP_DENSITY 0.75
#define SLEEP_NEIGHBOURS 20
// 0.25.0: water that is not to sleep on its own stops this far short of sleeping (FluidSim.SettledGap)
#define SETTLED_GAP 0.01
// 0.25.0: tiles (FluidSim.TileSleep): their width in particle sizes; the share of a tile's water that must have kept to its spot
// for the sleep time before the tile sleeps; the share of the water near its top (within TILE_BAND particle sizes) that left its
// spot within a frame that makes it restless
#define TILE_SIZE 8.0
#define TILE_STILL 0.7
#define TILE_MOVING 0.05
#define TILE_BAND 4.0
#define TILE_TABLE 65536
#define TILE_PROBES 32
#define TILE_GRACE 0.25
// 0.25.0: the divergence pass acts from this share of rest density up (FluidSim.DivDensity)
#define DIV_DENSITY 0.95
// 0.25.0: mesh smoothing (FluidSurface.MeshSmoothing): a vertex with this many particles within the kernel radius or fewer is not
// smoothed, fully from the second; Taubin's two steps
#define MESH_SMOOTH_FROM 4.0
#define MESH_SMOOTH_FULL 14.0
#define TAUBIN_IN 0.5
#define TAUBIN_OUT -0.53
// 0.24.0: the narrow band: a particle with this many neighbours within the surface's kernel is deep (a surface one has well under it)
#define DEEP_COUNT 20
#define DEEP_SKEW 0.15
// the field written into a block only deep particles reach (inside: no surface there)
#define INTERIOR -1e8
// 0.22.0: the surface cache: slower than this (m/s) counts as not moved, and every block is recomputed every this many builds
#define SURFACE_STILL 1.2   // 0.23.0: particle sizes per second (3 cm/s at 2.5 cm)
// 0.22.0: pressure-wave damping between slow pairs too (FluidSim.SlowPair)
#define SLOW_PAIR 4.0   // 0.23.0: particle sizes per second (0.1 m/s at 2.5 cm)
#define SURFACE_DENSITY 0.9
#define SURFACE_REFRESH 8

cbuffer Params : register(b0)
{
    uint4 _U0;     // x capacity, y table mask (table size T = mask + 1; DEAD = T), z shape count, w global shape count
    uint4 _U1;     // x last iteration (1/0), y spawn count, z vertex capacity, w index capacity
    uint4 _U2;     // xyz shape grid dims, w block count of the scan
    uint4 _U3;     // x brick table mask, y max bricks, z 1: trim this substep, w source count
    float4 _F0;    // x h, y h², z poly6 constant, w spiky gradient constant
    float4 _F1;    // x rest density, y relaxation, z collision radius, w substep dt
    float4 _F2;    // x cohesion C, y cohesion h^6/64, z cohesion normalisation, w max step per iteration
    float4 _F3;    // x max speed, y max gain (m/s²), z frame dt, w spacing
    float4 _KMin;  // kill box min
    float4 _KMax;  // kill box max
    float4 _SgMin; // shape grid min, w cell
    float4 _GMin;  // surface lattice origin (a multiple of 4 cells), w cell
    float4 _SurfP; // x kernel radius R, y lone drop radius, z reach of a particle into the blocks (R + cell), w DropletSize
    float4 _Grav;  // gravity (m/s²), w 1 / the viscosity passes (0.19.0)
    float4 _Sleep; // 0.21.0: x sleep radius² (m², 0: never sleep), y seconds before sleeping, z wake speed², w the slow effects' dt in substeps
    uint4 _U4;     // 0.21.0: x 1: a slow substep (viscosity, tension, adhesion), y 1: wake every particle in this Predict;
                   // 0.22.0: z this surface build's number (from 1), w 1: forget the cached surface blocks
    float4 _Surf2; // 0.22.0: x seconds since the last surface build (plus a frame), y how far a moved particle reaches into the field;
                   // 0.23.0: z a shape's wake speed² (m/s, whatever the particle size; _Sleep.z is the particles', by their size)
    uint4 _U5;     // 0.23.0: x how many wake boxes; 0.24.0: y 1: narrow band (blocks only deep particles reach get no field);
                   // 0.25.0: z how many moving shapes (MovingShapes), w 1: tiles (water sleeps a tile at a time)
    uint4 _G0;     // 0.26.0 gas: x the ring's slots, y puffs to spawn, z the ring's next slot, w the gas emitters
    float4 _G1;    // 0.26.0 gas: xyz the local user's head (puffs face it), w the clock (seconds)
    float4 _G2;    // 0.26.0 gas: x the frame's seconds
    uint4 _D0;     // 0.27.0 spray, foam and bubbles: x slots, y frame seed, z most spawns this frame, w 1: emit this frame
    float4 _D1;    // 0.27.0 spray, foam and bubbles: x the budget's scale, y this frame's seconds, zw a life's share of the
                   // liquid's FoamLifetime for the weakest and the strongest splash
    uint4 _V0;     // 0.27.0 vortex particles: x slots, y spawns this frame, z the ring's next slot, w 1: on
    float4 _V1;    // 0.27.0 vortex particles: x lifetime (s), y decay time (s), z 1 / the strongest emitter's Vortices, w unused
    float4 _I0;    // 0.27.0 thick liquid: x this solve's dt (the slow substeps'), y relative tolerance, z 0.01 h², w the warm start's share
    uint4 _I1;     // 0.27.0 thick liquid: x unused, y most iterations, zw unused
    float4 _S0;    // 0.27.0 smoke shader: xyz towards the world's main light (the gas's space; 0: none), w 1: the gas as quads for it
    float4 _S1;    // 0.27.0 smoke shader: x the density grid's cell (m), y self-shadow strength, z 1: spray and foam as quads, w unused
};

struct Liquid
{
    float4 a;      // viscosity, cohesion, adhesion, friction
    float4 b;      // density, gravity, damping, lifetime
    float4 color;  // linear RGBA
    uint4 c;       // x collide mask, y alive (0: its particles are removed), z 1 if it loses TrimLeft particles, w unused
    float4 d;      // 0.27.0: x foam amount (0: none), y kinematic viscosity for the implicit solve (m²/s; 0: XSPH, as before),
                   // z foam lifetime (seconds), w unused
};

struct Shape
{
    float4 A;      // xyz, w radius
    float4 B;      // xyz, w kind
    float4 C;      // xyz, w two-sided
    float4 Q;      // rotation (box)
    float4 V;      // velocity xyz, w category (as uint bits)
    float4 N;      // triangle normal xyz, w plane offset
    float4 BMin;   // bounds
    float4 BMax;
};

struct Spawn { float4 pos; float4 vel; };   // pos.w: source (uint bits)

RWStructuredBuffer<float4> Pos;     // xyz, w source
RWStructuredBuffer<float4> Vel;     // xyz, w age
RWStructuredBuffer<float4> Pred;    // xyz, w source
RWStructuredBuffer<float4> Pos2;
RWStructuredBuffer<float4> Vel2;
RWStructuredBuffer<float4> Pred2;
RWStructuredBuffer<float4> Delta;   // xyz (also the velocities between the two velocity passes)
RWStructuredBuffer<float4> Vtmp;    // 0.19.0: the velocities after an extra viscosity pass
RWStructuredBuffer<float4> Contact; // xyz normal, w 1 if touching
RWStructuredBuffer<float>  Lambda;
RWStructuredBuffer<float>  Rho;         // 0.20.0: number density (from the last density iteration)
RWStructuredBuffer<float4> Nrm;         // 0.20.0: surface normal (Akinci), xyz
RWStructuredBuffer<float4> Smooth;      // 0.20.0: each particle as drawn (moved towards its neighbours' mean), xyz
RWStructuredBuffer<float4> Anchor;      // 0.21.0: the spot it is resting at (xyz) and for how long (w, seconds; asleep from _Sleep.y)
RWStructuredBuffer<float4> Anchor2;     // 0.21.0: the sort's copy
RWStructuredBuffer<uint>   Keys;
RWStructuredBuffer<uint>   CellCount;   // T + 1 counts, then (after the scan) the starts
RWStructuredBuffer<uint>   CellCursor;
RWStructuredBuffer<uint>   BlockSums;
RWStructuredBuffer<uint>   Order;
RWStructuredBuffer<uint>   Nbr;         // MAXNB per particle
RWStructuredBuffer<uint>   NbCount;
RWStructuredBuffer<uint>   State;       // see below
RWStructuredBuffer<uint>   SourceCounts;
RWStructuredBuffer<int>    TrimLeft;    // per source: how many particles it still loses this frame (a lowered MaxParticles)
RWByteAddressBuffer        Args;        // indirect dispatch arguments: 0 particles, 12 surface blocks
StructuredBuffer<Liquid>   Liquids;
StructuredBuffer<Shape>    Shapes;
StructuredBuffer<uint>     ShapeGrid;   // cell starts (cells + 1) then the lists
StructuredBuffer<uint>     GlobalShapes;
StructuredBuffer<Spawn>    Spawns;
StructuredBuffer<uint>     MovingShapes; // 0.25.0: the shapes faster than a shape's wake speed (the only ones a sleeper checks)
StructuredBuffer<float4>   WakeBoxes;   // 0.23.0: min, max pairs: the sleepers inside are woken (where a still shape was)
RWStructuredBuffer<uint>   BrickKeys;   // the hash table of blocks: key, or EMPTY
RWStructuredBuffer<uint>   BrickSlot;   // its block's slot (or NONE past the cap)
RWStructuredBuffer<uint>   BrickCoord;  // the key of each slot's block
RWStructuredBuffer<float>  Phi;         // 64 per block (0.22.0: kept between builds; recomputed only where something moved)
RWStructuredBuffer<uint>   BrickMark;   // 0.22.0: per block slot: 4 x the last build it was in, | 1 its field must be recomputed,
                                        // | 2 (0.24.0) a particle near the surface reaches it (else it is deep inside: no field)
RWStructuredBuffer<float>  Rad;         // 0.18.0: each particle's radius in the surface
// 0.26.0 gas (DESIGN-gas.md)
struct GasEmitter
{
    float4 a;      // x buoyancy (m/s² up), y drag (1/s), z swirl (m/s), w spread
    float4 b;      // x start size, y end size, z lifetime, w collide mask (uint bits)
    float4 color;  // linear rgba (a: opacity)
    float4 d;      // 0.27.0: x how strongly its puffs feel the vortex particles (its Vortices), yzw unused
};
// 0.27.0: spray, foam and bubbles (Ihmsen et al. 2012), a ring of slots
RWStructuredBuffer<float4> DPos;        // xyz, w life left (seconds; <= 0: an empty slot)
RWStructuredBuffer<float4> DVel;        // xyz, w its liquid (uint bits)
RWStructuredBuffer<uint>   DInfo;       // 0 spray, 1 foam, 2 bubble (as classified in the last DiffuseUpdate)
RWByteAddressBuffer        DVerts;      // 56 bytes each, as the gas's: 7 a particle (as quads for the smoke shader: 4)
RWByteAddressBuffer        DIndices;    // 18 a particle (quads: 6)
// 0.27.0: vortex particles for the gas (Selle et al. 2005's idea, grid-free: vortex blobs), a ring of slots
RWStructuredBuffer<float4> VortPos;     // xyz, w age (seconds; < 0: an empty slot)
RWStructuredBuffer<float4> VortStr;     // xyz strength (vorticity times volume, m^3/s), w core radius (m)
RWStructuredBuffer<float4> VortVel;     // xyz the velocity the others give it this frame
StructuredBuffer<float4>   VortSpawns;  // pairs: position (w core radius), strength (w unused)
// 0.27.0: the thick liquid's implicit viscosity (Weiler, Koschier, Brand, Bender 2018), a matrix-free conjugate gradient
RWStructuredBuffer<float4> IvX;         // the solution (velocity)
RWStructuredBuffer<float4> IvR;         // the residual
RWStructuredBuffer<float4> IvP;         // the search direction; w: 1 in the system (awake, thick), 0 not (its row is the identity),
                                        // -1 asleep (held at 0: a wall for its thick neighbours)
RWStructuredBuffer<float4> IvQ;         // A p
RWStructuredBuffer<float4> IvPinv;      // 2 per particle: the inverse of its 3x3 diagonal block: (xx, yy, zz, xy), (xz, yz, -, -)
RWStructuredBuffer<float4> IvGuess;     // the last solve's change (x - b), the warm start; follows the particles through the sort
RWStructuredBuffer<float4> IvGuess2;    // the sort's copy
RWStructuredBuffer<float>  IvPartial;   // 2 per group: its partial sums
RWStructuredBuffer<float>  IvScalars;   // 0 r.z, 1 alpha, 2 beta, 3 |b|² over the thick rows, 4 r.r, 5 done (1), 6 iterations
StructuredBuffer<float>    IvDone;      // IvScalars again, read-only (an SRV: the matrix product already binds 8 UAVs)
RWStructuredBuffer<float4> GasPos;      // 0.26.0: xyz, w age (seconds; < 0: an empty slot)
RWStructuredBuffer<float4> GasVel;      // 0.26.0: xyz, w lifetime (seconds)
RWStructuredBuffer<uint>   GasEmit;     // 0.26.0: its emitter's slot in GasEmitters
RWByteAddressBuffer        GasVerts;    // 0.26.0: 7 a puff; 0.27.0: 56 bytes each (position, normal, colour, texcoord0), as quads 4 a puff
RWByteAddressBuffer        GasIndices;  // 0.26.0: 18 a puff (0.27.0 quads: 6)
RWStructuredBuffer<uint>   GasDensity;  // 0.27.0 smoke shader: the gas's density on a hashed grid (fixed point), GAS_DENSITY_TABLE cells
RWStructuredBuffer<uint>   GasBuckets;  // 0.27.0 smoke shader: GAS_BUCKETS counts by distance from the head, then where each starts
StructuredBuffer<GasEmitter> GasEmitters;
StructuredBuffer<float4>   GasSpawns;   // 0.26.0: pairs: position xyz + w its emitter's slot (uint bits); velocity xyz + w lifetime
RWStructuredBuffer<uint>   TileKeys;    // 0.25.0: the tiles of this frame: key (x | z << 16), or EMPTY
RWStructuredBuffer<uint>   TileData;    // 0.25.0: 8 per tile: awake (that can sleep), still, moved this frame near the top, asleep,
                                        // near the top, the top (an ordered uint), 2 unused
RWStructuredBuffer<float>  LambdaV;     // 0.25.0: the divergence pass's lambdas (Lambda keeps the position step's, for TileApply)
RWStructuredBuffer<uint>   TileFlags;   // 0.25.0: 1 restless, 2 settled
RWStructuredBuffer<int>    VertNbr;     // 0.25.0: 6 per vertex: the vertices it shares an edge with (or -1)
RWStructuredBuffer<float4> SPos;        // 0.25.0: the smoothing passes: position, w weight; normal (the driver swaps them with
RWStructuredBuffer<float4> SNrm;        //         the Tmp ones after each pass)
RWStructuredBuffer<float4> SPosTmp;
RWStructuredBuffer<float4> SNrmTmp;
RWStructuredBuffer<int>    CellVert;    // 64 per block
RWByteAddressBuffer        Verts;       // 40 bytes each: position xyz, normal xyz, colour rgba
RWByteAddressBuffer        Indices;     // 32-bit indices (raw: copied into the mesh's index buffer)

// State: 0 count, 1 vertices, 2 indices, 3-8 bounds (ordered uints), 9 contacts, 10 previous index count,
// 11 count before the sort, 12 blocks, 13 removed this frame, 14 neighbour total, 15 spawned this frame,
// (0.26.0 gas: 28 live puffs, 29 puffs drawn, 30-35 the gas's bounds (ordered uints), 36 the last frame's puffs drawn)
// 16 the highest speed (float bits: positive floats order as uints), 25 (0.25.0) awake particles not still for half the sleep time

uint Count() { return State[0]; }
// 0.21.0: asleep: stays put, costs (almost) nothing, holds up its awake neighbours like a wall
bool Asleep(uint i) { return _Sleep.x > 0 && Anchor[i].w >= _Sleep.y; }
uint TableSize() { return _U0.y + 1; }

int3 CellOf(float3 p) { return int3(floor(p / _F0.x)); }
uint HashCell(int3 c) { return ((uint)c.x * 73856093u ^ (uint)c.y * 19349663u ^ (uint)c.z * 83492791u) & _U0.y; }

float Poly6(float r2) { float d = _F0.y - r2; return d > 0 ? _F0.z * d * d * d : 0; }
// 0.27.0: a thick liquid (the implicit solve's) takes no XSPH (its Viscosity still sets its friction on colliders)
float XsphVisc(Liquid l) { return l.d.y > 0 ? 0 : l.a.x; }
float3 SpikyGrad(float3 d, float r) { if (r <= 1e-9 || r >= _F0.x) return 0; float t = _F0.x - r; return d * (_F0.w * t * t / r); }
float CohesionSpline(float r)
{
    if (r >= _F0.x || r <= 0) return 0;
    float t = _F0.x - r;
    float a = t * t * t * r * r * r;
    return 2 * r > _F0.x ? _F2.x * a : _F2.x * (2 * a - _F2.y);
}

uint OrderedUint(float f) { uint u = asuint(f); return (u & 0x80000000u) ? ~u : (u | 0x80000000u); }

uint Mix(uint x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
float Rand01(uint x) { return (Mix(x) >> 8) * (1.0 / 16777216.0); }

bool Bad(float3 v) { return any(isnan(v)) || any(isinf(v)); }

// ---------------------------------------------------------------------------------------------------------------- frame

[numthreads(GROUP, 1, 1)]
void ClearCounts(uint3 id : SV_DispatchThreadID)
{
    if (id.x < _U3.w) SourceCounts[id.x] = 0;
    if (id.x == 0)
    {
        State[3] = 0xFFFFFFFFu; State[4] = 0xFFFFFFFFu; State[5] = 0xFFFFFFFFu;
        State[6] = 0; State[7] = 0; State[8] = 0;
        State[9] = 0; State[13] = 0; State[14] = 0; State[15] = 0; State[16] = 0; State[17] = 0;
        State[25] = 0;
    }
}

[numthreads(GROUP, 1, 1)]
void SpawnParticles(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _U1.y) return;
    Spawn s = Spawns[id.x];
    if (Bad(s.pos.xyz)) return;
    uint i;
    InterlockedAdd(State[0], 1, i);
    if (i >= _U0.x) { InterlockedAdd(State[0], 0xFFFFFFFFu); return; }  // full: give the slot back
    InterlockedAdd(State[15], 1);
    Pos[i] = s.pos;
    Vel[i] = float4(Bad(s.vel.xyz) ? float3(0, 0, 0) : s.vel.xyz, 0);
    Contact[i] = 0;
    Anchor[i] = float4(s.pos.xyz, 0);
}

// the indirect dispatch for the particles (after spawning: the count only falls from here on in a frame)
[numthreads(1, 1, 1)]
void ParticleArgs(uint3 id : SV_DispatchThreadID)
{
    uint n = min(State[0], _U0.x);
    Args.Store3(0, uint3(max(1u, (n + GROUP - 1) / GROUP), 1, 1));
}

[numthreads(1, 1, 1)]
void SubstepBegin(uint3 id : SV_DispatchThreadID)
{
    State[9] = 0;
}

// ---------------------------------------------------------------------------------------------------------------- predict

[numthreads(GROUP, 1, 1)]
void Predict(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 x = Pos[i];
    uint src = asuint(x.w);
    Liquid l = Liquids[src];
    float4 vv = Vel[i];
    if (_U4.y != 0) Anchor[i] = float4(x.xyz, 0);
    float3 v = 0, p = x.xyz;
    if (!Asleep(i))
    {
        v = vv.xyz + _Grav.xyz * (l.b.y * _F1.w);
        v *= exp(-l.b.z * _F1.w);
        p = x.xyz + v * _F1.w;
    }
    Vel[i] = float4(v, vv.w);
    Pred[i] = float4(p, x.w);
    Contact[i] = 0;
    bool dead = l.c.y == 0 || any(x.xyz < _KMin.xyz) || any(x.xyz > _KMax.xyz) || Bad(x.xyz) || Bad(p) || (l.b.w > 0 && vv.w > l.b.w);
    // a lowered MaxParticles: exactly the particles over it go (which ones is up to the order the threads run in)
    if (!dead && _U3.z != 0 && l.c.z != 0)
    {
        int prev;
        InterlockedAdd(TrimLeft[src], -1, prev);
        if (prev > 0) dead = true;
    }
    Keys[i] = dead ? TableSize() : HashCell(CellOf(p));
}

// keys from the current positions (the grid the surface reads, at the end of a frame); nothing is removed here
[numthreads(GROUP, 1, 1)]
void KeysFromPos(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 x = Pos[i];
    Pred[i] = x;
    Keys[i] = HashCell(CellOf(x.xyz));
}

// ---------------------------------------------------------------------------------------------------------------- the sort

[numthreads(GROUP, 1, 1)]
void ClearCells(uint3 id : SV_DispatchThreadID)
{
    if (id.x > TableSize()) return;
    CellCount[id.x] = 0;
    CellCursor[id.x] = 0;
}

[numthreads(GROUP, 1, 1)]
void CountCells(uint3 id : SV_DispatchThreadID)
{
    if (id.x == 0) State[11] = Count();
    if (id.x >= Count()) return;
    InterlockedAdd(CellCount[Keys[id.x]], 1);
}

groupshared uint _scan[SCAN_GROUP * 2];

// an exclusive scan of 1024 entries per group (Blelloch), each group's total into BlockSums
[numthreads(SCAN_GROUP, 1, 1)]
void ScanBlocks(uint3 gid : SV_GroupID, uint3 tid : SV_GroupThreadID)
{
    uint n = TableSize() + 1;
    uint base = gid.x * SCAN_GROUP * 2;
    uint t = tid.x;
    _scan[2 * t] = base + 2 * t < n ? CellCount[base + 2 * t] : 0;
    _scan[2 * t + 1] = base + 2 * t + 1 < n ? CellCount[base + 2 * t + 1] : 0;
    uint offset = 1;
    uint d;
    [unroll] for (d = SCAN_GROUP; d > 0; d >>= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        if (t < d) { uint ai = offset * (2 * t + 1) - 1, bi = offset * (2 * t + 2) - 1; _scan[bi] += _scan[ai]; }
        offset <<= 1;
    }
    if (t == 0) { BlockSums[gid.x] = _scan[SCAN_GROUP * 2 - 1]; _scan[SCAN_GROUP * 2 - 1] = 0; }
    [unroll] for (d = 1; d <= SCAN_GROUP; d <<= 1)
    {
        offset >>= 1;
        GroupMemoryBarrierWithGroupSync();
        if (t < d) { uint ai = offset * (2 * t + 1) - 1, bi = offset * (2 * t + 2) - 1; uint tmp = _scan[ai]; _scan[ai] = _scan[bi]; _scan[bi] += tmp; }
    }
    GroupMemoryBarrierWithGroupSync();
    if (base + 2 * t < n) CellCount[base + 2 * t] = _scan[2 * t];
    if (base + 2 * t + 1 < n) CellCount[base + 2 * t + 1] = _scan[2 * t + 1];
}

// the block totals, scanned by one group (up to 1024 blocks: a table of up to a million cells)
[numthreads(SCAN_GROUP, 1, 1)]
void ScanSums(uint3 tid : SV_GroupThreadID)
{
    uint n = _U2.w;
    uint t = tid.x;
    _scan[2 * t] = 2 * t < n ? BlockSums[2 * t] : 0;
    _scan[2 * t + 1] = 2 * t + 1 < n ? BlockSums[2 * t + 1] : 0;
    uint offset = 1;
    uint d;
    [unroll] for (d = SCAN_GROUP; d > 0; d >>= 1)
    {
        GroupMemoryBarrierWithGroupSync();
        if (t < d) { uint ai = offset * (2 * t + 1) - 1, bi = offset * (2 * t + 2) - 1; _scan[bi] += _scan[ai]; }
        offset <<= 1;
    }
    if (t == 0) _scan[SCAN_GROUP * 2 - 1] = 0;
    [unroll] for (d = 1; d <= SCAN_GROUP; d <<= 1)
    {
        offset >>= 1;
        GroupMemoryBarrierWithGroupSync();
        if (t < d) { uint ai = offset * (2 * t + 1) - 1, bi = offset * (2 * t + 2) - 1; uint tmp = _scan[ai]; _scan[ai] = _scan[bi]; _scan[bi] += tmp; }
    }
    GroupMemoryBarrierWithGroupSync();
    if (2 * t < n) BlockSums[2 * t] = _scan[2 * t];
    if (2 * t + 1 < n) BlockSums[2 * t + 1] = _scan[2 * t + 1];
}

[numthreads(GROUP, 1, 1)]
void AddSums(uint3 id : SV_DispatchThreadID)
{
    if (id.x > TableSize()) return;
    CellCount[id.x] += BlockSums[id.x / (SCAN_GROUP * 2)];
}

[numthreads(GROUP, 1, 1)]
void Scatter(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= State[11]) return;
    uint k = Keys[i], slot;
    InterlockedAdd(CellCursor[k], 1, slot);
    Order[CellCount[k] + slot] = i;
}

[numthreads(GROUP, 1, 1)]
void Reorder(uint3 id : SV_DispatchThreadID)
{
    uint j = id.x;
    if (j >= State[11]) return;
    uint i = Order[j];
    Pos2[j] = Pos[i]; Vel2[j] = Vel[i]; Pred2[j] = Pred[i];
}

// 0.23.0: wakes the sleepers inside the wake boxes: where a still shape near the liquid was last frame and is not now (a floor
// taken away). Waking everything instead (0.21.0, 0.22.0) woke a pool in a room every frame: the room's triangles are sent only
// near the liquid, and those at the edge of its bounds came and went as its top moved
[numthreads(GROUP, 1, 1)]
void WakeInBoxes(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count() || !Asleep(i)) return;
    float3 p = Pos[i].xyz;
    for (uint b = 0; b < _U5.x; b++)
    {
        float3 lo = WakeBoxes[b * 2].xyz, hi = WakeBoxes[b * 2 + 1].xyz;
        // (0.25.0: with tiles, with the grace a tile's wake gives: its settling is not motion; a wall row coming and going above a
        // room's pool woke its edge, whose settling then woke the tiles around it: 56 % of the pool awake)
        if (all(p >= lo) && all(p <= hi)) { Anchor[i] = float4(p, _U5.w != 0 ? -TILE_GRACE : 0); return; }
    }
}

// 0.21.0: the sleep state follows its particle through the sort (a kernel of its own: Reorder already binds 8 buffers)
[numthreads(GROUP, 1, 1)]
void ReorderAnchor(uint3 id : SV_DispatchThreadID)
{
    uint j = id.x;
    if (j >= State[11]) return;
    Anchor2[j] = Anchor[Order[j]];
}

// after the reorder: the alive particles are those before the DEAD bucket
[numthreads(1, 1, 1)]
void SetCount(uint3 id : SV_DispatchThreadID)
{
    uint alive = CellCount[TableSize()];
    State[13] += State[11] - alive;
    State[18] += State[11] - alive;   // 0.22.0: removed since the last surface build (the cached blocks are then forgotten)
    State[0] = alive;
}

// the range of a hashed cell (keys below DEAD only), at most 128 particles: a cell holds 8 to 16 normally, and a scan over
// thousands (everything squeezed into one spot) could run long enough for Windows to reset the GPU
void CellRange(uint k, out uint s, out uint e) { s = CellCount[k]; e = min(CellCount[k + 1], s + 128); }

// ---------------------------------------------------------------------------------------------------------------- neighbours

[numthreads(GROUP, 1, 1)]
void Neighbours(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    // 0.21.0: a sleeper searches nothing (Box3D: only what moved is queried); a fast awake particle wakes the sleepers it finds
    if (Asleep(i)) { NbCount[i] = 0; return; }
    float3 p = Pred[i].xyz;
    float3 vi = Vel[i].xyz;
    bool waker = _Sleep.x > 0 && dot(vi, vi) > _Sleep.z;
    uint si = asuint(Pred[i].w); float di = Liquids[si].b.x;
    bool mixed = false;
    int3 c = CellOf(p);
    uint cnt = 0, base = i * MAXNB;
    for (int dx = -1; dx <= 1; dx++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dz = -1; dz <= 1; dz++)
    {
        int3 q = c + int3(dx, dy, dz);
        uint s, e; CellRange(HashCell(q), s, e);
        for (uint j = s; j < e; j++)
        {
            if (j == i) continue;
            float3 pj = Pred[j].xyz;
            if (any(CellOf(pj) != q)) continue;
            float3 d = p - pj;
            if (dot(d, d) >= _F0.y) continue;
            if (waker && Asleep(j)) Anchor[j] = float4(Anchor[j].xyz, 0);   // awake from the next substep on
            // beside a liquid of another density it stays awake, and so does that one (oil rising through water starts slowly)
            uint sj = asuint(Pred[j].w);
            if (sj != si && Liquids[sj].b.x != di) { mixed = true; if (Asleep(j)) Anchor[j] = float4(Anchor[j].xyz, 0); }
            if (cnt < MAXNB) { Nbr[base + cnt] = j; cnt++; }
        }
    }
    NbCount[i] = cnt;
    if (mixed) Anchor[i] = float4(Anchor[i].xyz, 0);
    InterlockedAdd(State[14], cnt);
}

// ---------------------------------------------------------------------------------------------------------------- density

[numthreads(GROUP, 1, 1)]
void Lambdas(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Lambda[i] = 0; Rho[i] = _F1.x; return; }
    float4 pi = Pred[i];
    float rho = Poly6(0);
    float3 gi = 0; float sum = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        float4 pj = Pred[Nbr[base + q]];
        float3 d = pi.xyz - pj.xyz;
        float r2 = dot(d, d);
        rho += Poly6(r2);
        float3 gr = SpikyGrad(d, sqrt(r2));
        gi += gr;
        if (!Asleep(Nbr[base + q])) sum += (1.0 / Liquids[asuint(pj.w)].b.x) * dot(gr, gr);   // 0.21.0: a sleeper does not give way
    }
    Rho[i] = rho;
    float cst = rho / _F1.x - 1;
    if (!(cst > 0)) { Lambda[i] = 0; return; }   // only pushes apart: a free surface is not pulled in
    float wi = 1.0 / Liquids[asuint(pi.w)].b.x;
    float denom = (sum + wi * dot(gi, gi)) / (_F1.x * _F1.x) + _F1.y;
    Lambda[i] = -cst / denom;
}

[numthreads(GROUP, 1, 1)]
void Deltas(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Delta[i] = 0; return; }
    float4 pi = Pred[i];
    float li = Lambda[i];
    float3 dp = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        // 0.21.0: a sleeper is a wall (left out of this particle's denominator), so this particle takes the whole correction:
        // its own lambda once (0.21.0 to 0.24.0 added it twice: water on sleeping water bobbed, the bounce). 0.25.0: with tiles,
        // plus the sleeper's pressure from when it fell asleep (in its Anchor.x): without it, water beside or on a sleeper got
        // half its push and sank into it (6 % over rest density, in pits), and woken it settled back with a jolt
        float lsum = li + (Asleep(j) ? (_U5.w != 0 ? Anchor[j].x : 0) : Lambda[j]);
        if (lsum == 0) continue;
        float3 d = pi.xyz - Pred[j].xyz;
        dp += SpikyGrad(d, length(d)) * lsum;
    }
    dp *= (1.0 / Liquids[asuint(pi.w)].b.x) / _F1.x;
    float l2 = dot(dp, dp);
    if (l2 > _F2.w * _F2.w) dp *= _F2.w / sqrt(l2);
    Delta[i] = float4(dp, 0);
}

[numthreads(GROUP, 1, 1)]
void ApplyDeltas(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 p = Pred[i];
    Pred[i] = float4(p.xyz + Delta[i].xyz, p.w);
}

// ---------------------------------------------------------------------------------------------------------------- colliders

float3 Rotate(float4 q, float3 v) { float3 t = 2 * cross(q.xyz, v); return v + q.w * t + cross(q.xyz, t); }
float3 RotateInv(float4 q, float3 v) { return Rotate(float4(-q.xyz, q.w), v); }

float3 ClosestOnSegment(float3 a, float3 b, float3 p)
{
    float3 ab = b - a; float l2 = dot(ab, ab);
    if (l2 <= 1e-20) return a;
    return a + ab * saturate(dot(p - a, ab) / l2);
}

bool PushOut(inout float3 x, float3 c, float r)
{
    float3 d = x - c; float l2 = dot(d, d);
    if (!(l2 < r * r)) return false;
    float l = sqrt(l2);
    x = l > 1e-9 ? c + d * (r / l) : c + float3(0, r, 0);
    return true;
}

// a component by number without indexing a register (a dynamic index forces loops around it to unroll)
float Comp(float3 v, int a) { return a == 0 ? v.x : (a == 1 ? v.y : v.z); }
float3 WithComp(float3 v, int a, float x) { return float3(a == 0 ? x : v.x, a == 1 ? x : v.y, a == 2 ? x : v.z); }

bool PushOutOfBox(inout float3 x, float3 center, float4 q, float3 half, float r, float3 prefer, float3 x0)
{
    float3 local = RotateInv(q, x - center);
    if (any(abs(local) >= half + r)) return false;
    float3 cl = clamp(local, -half, half);
    float3 d = local - cl;
    float l2 = dot(d, d);
    if (l2 > 1e-18)
    {
        if (l2 >= r * r) return false;
        local = cl + d * (r / sqrt(l2));
    }
    else
    {
        float3 pen = half - abs(local);
        int axis = pen.x <= pen.y && pen.x <= pen.z ? 0 : (pen.y <= pen.z ? 1 : 2);
        float side = Comp(local, axis) < 0 ? -1 : 1;
        // 0.16.0: a particle that was outside the box when the substep began leaves by the face it came in through (the
        // last slab it entered along its path). Pushed out by the nearest face instead, a fast one hitting a wall near the
        // floor left through the wall's bottom, into the floor, and slid out under the wall.
        float3 o = RotateInv(q, x0 - center), H = half + r, dl = local - o;
        float tb = -1;
        [unroll] for (int a = 0; a < 3; a++)
        {
            float oa = Comp(o, a), da = Comp(dl, a), ha = Comp(H, a);
            if (abs(oa) >= ha - 1e-5 && oa * da < 0)
            {
                float t = (abs(oa) - ha) / abs(da);
                if (t > tb) { tb = t; axis = a; side = oa < 0 ? -1 : 1; }
            }
        }
        if (tb < 0 && any(prefer != 0))
        {
            float3 pl = RotateInv(q, prefer);
            float3 ap = abs(pl);
            int pa = ap.x >= ap.y && ap.x >= ap.z ? 0 : (ap.y >= ap.z ? 1 : 2);
            float pv = Comp(pl, pa);
            if (abs(pv) > 0.5)
            {
                float ps = pv < 0 ? -1 : 1;
                float ex = Comp(half, pa) - ps * Comp(local, pa);
                if (ex <= min(pen.x, min(pen.y, pen.z)) + 2 * r) { axis = pa; side = ps; }
            }
        }
        local = WithComp(local, axis, side * (Comp(half, axis) + r));
    }
    x = center + Rotate(q, local);
    return true;
}

void AxisFrame(float3 x, float3 a, float3 b, out float3 u, out float len, out float h, out float rho, out float3 dir)
{
    float3 ab = b - a; len = length(ab);
    u = len > 1e-9 ? ab / len : float3(0, 1, 0);
    float3 d = x - a;
    h = dot(d, u);
    float3 radial = d - u * h;
    rho = length(radial);
    if (rho > 1e-9) dir = radial / rho;
    else { float3 t = abs(u.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0); dir = normalize(cross(u, t)); }
}

bool PushOutOfCylinder(inout float3 x, float3 a, float3 b, float R, float r)
{
    float3 u, dir; float len, h, rho;
    AxisFrame(x, a, b, u, len, h, rho, dir);
    float ch = clamp(h, 0, len), cr = min(rho, R);
    bool inside = h > 0 && h < len && rho < R;
    float nh, nr;
    if (!inside)
    {
        float dh = h - ch, dr = rho - cr;
        float d2 = dh * dh + dr * dr;
        if (d2 >= r * r) return false;
        float d = sqrt(d2);
        if (d > 1e-9) { nh = ch + dh * (r / d); nr = cr + dr * (r / d); } else { nh = ch; nr = R + r; }
    }
    else
    {
        float side = R - rho, bottom = h, top = len - h;
        if (side <= bottom && side <= top) { nh = h; nr = R + r; }
        else if (bottom <= top) { nh = -r; nr = rho; }
        else { nh = len + r; nr = rho; }
    }
    x = a + u * nh + dir * nr;
    return true;
}

float2 ClosestOnSegment2(float2 a, float2 b, float2 p)
{
    float2 ab = b - a; float l2 = dot(ab, ab);
    if (l2 <= 1e-20) return a;
    return a + ab * saturate(dot(p - a, ab) / l2);
}

bool PushOutOfCone(inout float3 x, float3 a, float3 b, float R, float r)
{
    float3 u, dir; float H, h, rho;
    AxisFrame(x, a, b, u, H, h, rho, dir);
    if (H <= 1e-9 || R <= 0) return false;
    float2 q = float2(rho, h);
    float2 cb = ClosestOnSegment2(float2(0, 0), float2(R, 0), q);
    float2 cs = ClosestOnSegment2(float2(R, 0), float2(0, H), q);
    bool inside = h > 0 && h < H && rho < R * (1 - h / H);
    float2 db2 = q - cb, ds2 = q - cs;
    float db = dot(db2, db2), ds = dot(ds2, ds2);
    float2 cp = db <= ds ? cb : cs;
    float2 slant = normalize(float2(H, R));
    if (inside) q = cp + (db <= ds ? float2(0, -1) : slant) * r;
    else
    {
        float d = sqrt(min(db, ds));
        if (d >= r) return false;
        q = d > 1e-9 ? cp + (q - cp) * (r / d) : cp + (db <= ds ? float2(0, -1) : slant) * r;
    }
    x = a + u * q.y + dir * max(0, q.x);
    return true;
}

float3 ClosestOnTriangle(float3 p, float3 a, float3 b, float3 c)
{
    float3 ab = b - a, ac = c - a, ap = p - a;
    float d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (d1 <= 0 && d2 <= 0) return a;
    float3 bp = p - b;
    float d3 = dot(ab, bp), d4 = dot(ac, bp);
    if (d3 >= 0 && d4 <= d3) return b;
    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
    float3 cp = p - c;
    float d5 = dot(ab, cp), d6 = dot(ac, cp);
    if (d6 >= 0 && d5 <= d6) return c;
    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
    float va = d3 * d6 - d5 * d4;
    if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
    float denom = 1.0 / (va + vb + vc);
    return a + ab * (vb * denom) + ac * (vc * denom);
}

bool PushOutOfTriangle(inout float3 x, float3 a, float3 b, float3 c, bool twoSided, float r)
{
    float3 cp = ClosestOnTriangle(x, a, b, c);
    float3 d = x - cp;
    float d2 = dot(d, d);
    float3 n = cross(b - a, c - a);
    float nl = length(n);
    if (nl <= 1e-20) return false;
    n /= nl;
    float s = dot(x - a, n);
    if (twoSided || s >= 0)
    {
        if (d2 >= r * r) return false;
        float dl = sqrt(d2);
        x = dl > 1e-9 ? cp + d * (r / dl) : cp + n * (s >= 0 ? r : -r);
        return true;
    }
    if (s < -2 * r) return false;
    float3 onPlane = x - n * s;
    float3 o = onPlane - cp;
    if (dot(o, o) > 1e-10) return false;
    x = onPlane + n * r;
    return true;
}

bool Side(float3 q, float3 a, float3 b, float3 n, float margin)
{
    float3 e = b - a; float l = length(e);
    if (l <= 1e-9) return true;
    return dot(cross(n, e), q - a) >= -margin * l;
}

// a particle that crossed a triangle's front this substep is stopped there (the CPU version's Swept)
bool Swept(inout float3 p, float3 x0, Shape c, float r)
{
    float3 n = c.N.xyz;
    if (all(n == 0)) return false;
    float s0 = dot(x0, n) - c.N.w, s1 = dot(p, n) - c.N.w;
    float sgn = 1;
    if (!(s0 >= 0.5 * r && s1 < r))
    {
        if (c.C.w == 0 || !(s0 <= -0.5 * r && s1 > -r)) return false;
        sgn = -1; s0 = -s0; s1 = -s1;
    }
    float denom = s0 - s1;
    float t = denom > 1e-9 ? saturate((s0 - r) / denom) : 0;
    float3 q = x0 + (p - x0) * t;
    if (!(Side(q, c.A.xyz, c.B.xyz, n, r) && Side(q, c.B.xyz, c.C.xyz, n, r) && Side(q, c.C.xyz, c.A.xyz, n, r))) return false;
    p += n * (sgn * (r - s1));
    return true;
}

// 0.18.0: a swept box (the CPU's BoxSwept). A particle whose path this substep went into the box itself or clean through it
// is put back on the face it came in by (the last slab it entered), keeping its sliding motion: on a very thin box floor a
// pile's pressure pushed particles wholly through in one substep, where the plain push no longer saw them.
bool SweptBox(inout float3 p, float3 x0, Shape c, float r)
{
    float3 o = RotateInv(c.Q, x0 - c.A.xyz), l = RotateInv(c.Q, p - c.A.xyz), half = abs(c.B.xyz), d = l - o;
    float tmin = -1e-7, tmax = 1, side = 1;
    int axis = -1;
    [unroll] for (int a = 0; a < 3; a++)
    {
        float oa = Comp(o, a), da = Comp(d, a), ha = Comp(half, a);
        if (abs(da) < 1e-12)
        {
            if (abs(oa) >= ha) return false;
        }
        else
        {
            float t1 = (-ha - oa) / da, t2 = (ha - oa) / da;
            float tn = min(t1, t2), tf = max(t1, t2);
            if (tn > tmin) { tmin = tn; axis = a; side = da < 0 ? 1 : -1; }
            tmax = min(tmax, tf);
        }
    }
    if (axis < 0 || tmin > tmax) return false;
    l = WithComp(l, axis, side * (Comp(half, axis) + r));
    p = c.A.xyz + Rotate(c.Q, l);
    return true;
}

void PushShape(inout float3 p, float3 x0, Shape c, uint mask, float3 prefer)
{
    uint cat = asuint(c.V.w);
    if (cat == 0) cat = 1;
    if ((cat & mask) == 0) return;
    float r = _F1.z;
    int kind = (int)c.B.w;
    if (kind != 0)
    {
        // the bounds of this substep's whole motion, so a fast particle still meets the triangle it crossed
        float3 lo = min(p, x0), hi = max(p, x0);
        if (any(hi < c.BMin.xyz - r) || any(lo > c.BMax.xyz + r)) return;
    }
    switch (kind)
    {
        case 0: { float d = dot(p - c.A.xyz, c.B.xyz) - r; if (d < 0) p -= c.B.xyz * d; break; }
        case 1: PushOut(p, c.A.xyz, c.A.w + r); break;
        case 2: PushOut(p, ClosestOnSegment(c.A.xyz, c.B.xyz, p), c.A.w + r); break;
        case 3: if (!SweptBox(p, x0, c, r)) PushOutOfBox(p, c.A.xyz, c.Q, c.B.xyz, r, prefer, x0); break;
        case 4: PushOutOfCylinder(p, c.A.xyz, c.B.xyz, c.A.w, r); break;
        case 5: PushOutOfCone(p, c.A.xyz, c.B.xyz, c.A.w, r); break;
        case 6:
        {
            if (Swept(p, x0, c, r)) break;
            float s = dot(p, c.N.xyz) - c.N.w;
            if (s >= r || s <= (c.C.w != 0 ? -r : -2 * r)) break;
            PushOutOfTriangle(p, c.A.xyz, c.B.xyz, c.C.xyz, c.C.w != 0, r);
            break;
        }
    }
}

uint ShapeCellOf(float3 p)
{
    uint3 dim = _U2.xyz;
    int3 g = int3(floor((p - _SgMin.xyz) / _SgMin.w));
    g = clamp(g, int3(0, 0, 0), int3(dim) - 1);
    return ((uint)g.z * dim.y + (uint)g.y) * dim.x + (uint)g.x;
}

[numthreads(GROUP, 1, 1)]
void Collide(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    bool last = _U1.x != 0;
    float4 pr = Pred[i];
    uint mask = Liquids[asuint(pr.w)].c.x;
    float3 p = pr.xyz, before = p, x0 = Pos[i].xyz;
    if (Asleep(i))
    {
        // 0.21.0: not pushed; a shape moving faster than the wake speed within WAKE_REACH particle sizes wakes it. 0.25.0: only
        // the moving shapes (a list from the CPU, usually empty), once a substep: every shape (938 triangles of a room) for every
        // sleeper, every iteration, was most of a frozen pool's cost
        if (!last) return;
        bool wake = false;
        for (uint q = 0; q < _U5.z && !wake; q++)
        {
            Shape sh = Shapes[MovingShapes[q]];
            // (0.25.0: a fast one or a user's part within WAKE_REACH; a slower one only the water it touches)
            uint cat = asuint(sh.V.w);
            float reach = dot(sh.V.xyz, sh.V.xyz) > _Surf2.z || (cat != 0 && cat != 1) ? WAKE_REACH * _F3.w : _F0.x + _F1.z;
            if (all(p >= sh.BMin.xyz - reach) && all(p <= sh.BMax.xyz + reach)) wake = true;
        }
        if (wake) Anchor[i] = float4(Anchor[i].xyz, 0);
        return;
    }
    // a particle inside a box leaves the way it came (where it was at the start of the substep)
    float3 back = x0 - p; float bl = length(back);
    float3 prefer = bl > 1e-6 ? back / bl : 0;
    // 0.19.0: the grid's lists, then the global one; moving shapes come first in each (the runtime adds them first), so a
    // floor has the last say over a box pressed into the liquid
    uint cells = _U2.x * _U2.y * _U2.z;
    if (cells > 0)
    {
        uint ci = ShapeCellOf(p);
        uint s = ShapeGrid[ci], e = ShapeGrid[ci + 1];
        for (uint q = s; q < e; q++) PushShape(p, x0, Shapes[ShapeGrid[cells + 1 + q]], mask, prefer);
    }
    for (uint g = 0; g < _U0.w; g++) PushShape(p, x0, Shapes[GlobalShapes[g]], mask, prefer);
    if (any(p != before))
    {
        // any iteration's push counts (0.15.2: a resting particle is pushed out in the first one only)
        Pred[i] = float4(p, pr.w);
        float3 d = p - before; float l = length(d);
        Contact[i] = float4(l > 1e-9 ? d / l : float3(0, 1, 0), 1);
    }
    if (last && Contact[i].w > 0) InterlockedAdd(State[9], 1);
}

// ---------------------------------------------------------------------------------------------------------------- velocities

// this substep's motion as a velocity (into Delta), without bouncing off colliders and with the speed gain capped
[numthreads(GROUP, 1, 1)]
void VelocityA(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Delta[i] = float4(0, 0, 0, 1); return; }   // w: pressed (a sleeper counts as pressed)
    float3 v = (Pred[i].xyz - Pos[i].xyz) / _F1.w;
    float4 ct = Contact[i];
    if (ct.w > 0) { float vn = dot(v, ct.xyz); if (vn > 0) v -= ct.xyz * (vn * 0.9); }
    float was = length(Vel[i].xyz), now = length(v);
    float cap = min(_F3.x, was + _F3.y * _F1.w);
    if (!(now <= cap)) v = (now > 0 && !isnan(now) && !isinf(now)) ? v * (cap / now) : 0;
    // 0.21.0: w 1 = pressed (over rest density); 0.22.0: -1 = at the surface (under SURFACE_DENSITY)
    // (a thin sheet under SLEEP_DENSITY is not a surface particle: its holes close slowly, and damping held them open)
    float rr = Rho[i] / _F1.x;
    Delta[i] = float4(v, rr > PRESSED_DENSITY ? 1 : (rr < SURFACE_DENSITY && rr >= SLEEP_DENSITY ? -1 : 0));
}

// 0.19.0: viscosity above 1 takes more XSPH passes (each at most a full average of the neighbours' velocities): all but the
// last here (Delta in, Vtmp out; the driver swaps them), the last in VelocityB
[numthreads(GROUP, 1, 1)]
void Viscosity(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Vtmp[i] = float4(0, 0, 0, 1); return; }
    float4 pi = Pred[i];
    Liquid li = Liquids[asuint(pi.w)];
    float3 v = Delta[i].xyz;
    float3 visc = 0; float wsum = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float4 pj = Pred[j];
        float3 d = pi.xyz - pj.xyz;
        float w = Poly6(dot(d, d));
        visc += (Delta[j].xyz - v) * (w * 0.5 * (XsphVisc(li) + XsphVisc(Liquids[asuint(pj.w)])) * _Grav.w);
        wsum += w;
    }
    if (wsum > 0) v += visc / (wsum + Poly6(0));
    Vtmp[i] = float4(v, Delta[i].w);
}

// 0.20.0: surface tension (Akinci et al. 2013), before the last viscosity pass. First each particle's surface normal,
// n_i = h * sum of grad W_ij / rho_j (zero inside the liquid, longest at its surface)
[numthreads(GROUP, 1, 1)]
void Normals(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Nrm[i] = float4(0, 0, 0, 1); return; }   // 0.21.0: w 1 = asleep (Tension cannot bind Anchor)
    float3 pi = Pred[i].xyz;
    float3 n = 0;
    float rf = 0.1 * _F1.x;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float3 d = pi - Pred[j].xyz;
        n += SpikyGrad(d, length(d)) / max(Rho[j], rf);
    }
    Nrm[i] = float4(n * _F0.x, 0);
}

// then cohesion (attraction only) and the curvature term, both scaled by K_ij = 2 rho0 / (rho_i + rho_j), so a thin sheet
// holds together and its bumps sink back in (Delta in, Vtmp out; the driver swaps them)
[numthreads(GROUP, 1, 1)]
void Tension(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Nrm[i].w > 0) { Vtmp[i] = float4(0, 0, 0, 1); return; }
    float4 pi = Pred[i];
    uint si = asuint(pi.w);
    float ci = Liquids[si].a.y;
    float3 ni = Nrm[i].xyz;
    float rhoi = Rho[i], kf = 0.2 * _F1.x;
    float3 pull = 0, curv = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float4 pj = Pred[j];
        uint sj = asuint(pj.w);
        float3 d = pi.xyz - pj.xyz;
        float r = length(d);
        if (!(r > 1e-9)) continue;
        float gamma = si == sj ? ci : 0.25 * min(ci, Liquids[sj].a.y);
        if (!(gamma > 0)) continue;
        float k = gamma * 2 * _F1.x / max(rhoi + Rho[j], kf);
        float cs = CohesionSpline(r);
        if (cs > 0) pull -= d * (k * cs / r);
        float4 nj = Nrm[j];
        if (nj.w == 0) curv -= (ni - nj.xyz) * k;   // a sleeper's normal is not known
    }
    // 0.21.0: on the slow substeps only, with their time
    float4 di0 = Delta[i];
    Vtmp[i] = float4(di0.xyz + (pull * (COHESION_STRENGTH * _F2.z) + curv * CURVATURE_STRENGTH) * (_F1.w * _Sleep.w), di0.w);
}

// XSPH viscosity from the neighbours' velocities, friction and adhesion on colliders; the new position
[numthreads(GROUP, 1, 1)]
void VelocityB(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { Vel[i] = float4(0, 0, 0, Vel[i].w); return; }
    float4 pi = Pred[i];
    uint si = asuint(pi.w);
    Liquid li = Liquids[si];
    float4 dI = Delta[i];
    float3 v = dI.xyz;
    bool pressed = dI.w > 0, surfaceI = dI.w < 0;
    bool slow = _U4.x != 0;
    float3 visc = 0, squeeze = 0; float wsum = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float4 pj = Pred[j];
        float3 d = pi.xyz - pj.xyz;
        float r2 = dot(d, d);
        float w = Poly6(r2);
        float4 dj = Delta[j];
        float3 rel = dj.xyz - v;
        if (slow) visc += rel * (w * 0.5 * (XsphVisc(li) + XsphVisc(Liquids[asuint(pj.w)])) * _Grav.w);
        // 0.21.0: pressure-wave damping, every substep: only the relative motion along the line between them, and only where
        // one of them is pressed (a splash or a kicked drop flies apart freely)
        // (0.22.0: and between two of its surface particles moving slowly against each other: a pool's top layer jittered at up
        // to 20 cm/s; inside the liquid that slowed buoyancy and pushed water)
        if (r2 > 1e-12 && asuint(pj.w) == si)
        {
            float3 dn = d * rsqrt(r2); float rn = dot(rel, dn);
            if (pressed || dj.w > 0 || (surfaceI && dj.w < 0 && abs(rn) < SLOW_PAIR * _F3.w)) squeeze += dn * (rn * w);
        }
        wsum += w;
    }
    if (wsum > 0) v += (visc + squeeze * PRESSURE_DAMPING) / (wsum + Poly6(0));
    float4 ct = Contact[i];
    if (ct.w > 0)
    {
        // colliders: friction against the surface, and adhesion pulling it back onto it
        float3 n = ct.xyz;
        float vn = dot(v, n);
        float3 vt = v - n * vn;
        v = n * vn + vt * exp(-max(li.a.w, min(li.a.x, 1)) * 30 * _F1.w);
        if (slow && li.a.z > 0 && vn > 0) v -= n * min(vn, li.a.z * 2);
    }
    Vel[i] = float4(v, Vel[i].w);
}

// 0.21.0: the new position, and how long the particle has stayed near its spot (it sleeps after _Sleep.y seconds within
// the sleep radius; a woken one starts again from 0, from where it is)
[numthreads(GROUP, 1, 1)]
void Settle(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 pi = Pred[i];
    Pos[i] = pi;
    float4 an = Anchor[i];
    // 0.22.0: a sleeper's time keeps counting (the surface knows by it how long it has not moved)
    if (Asleep(i)) { Anchor[i] = float4(an.xyz, an.w + _F1.w); return; }
    float3 d = pi.xyz - an.xyz;
    // (a thin sheet or the top layer, under SLEEP_DENSITY of rest density, never sleeps: sleeping froze a puddle's holes open)
    // (0.24.0: nor with under SLEEP_NEIGHBOURS neighbours: cohesion squeezes a one-particle sheet to rest density)
    // (0.25.0: with tiles, nothing sleeps on its own: a tile puts its water to sleep together, see TileApply)
    bool sleepable = Rho[i] >= SLEEP_DENSITY * _F1.x && NbCount[i] >= SLEEP_NEIGHBOURS;
    // (0.25.0: just woken by its tile (negative): a grace of TILE_GRACE, in which it follows its spot; its settling into the water
    // around it is not motion: counted as such, a woken tile woke the tiles around it, and on over the pool)
    if (an.w < 0) Anchor[i] = float4(pi.xyz, min(an.w + _F1.w, 0));
    else if (an.w > 0 && dot(d, d) < _Sleep.x)
    {
        float c = an.w + _F1.w;
        if (!sleepable || _U5.w != 0) c = min(c, _Sleep.y - SETTLED_GAP);
        Anchor[i] = float4(an.xyz, c);
    }
    // (0.25.0: with tiles, one just woken or new (0) starts a frame on, so it does not count as having left its spot this frame:
    // a woken tile's water counted as moving, made it restless, and the wake swept the whole pool a tile a frame)
    else Anchor[i] = float4(pi.xyz, an.w <= 0 && _U5.w != 0 ? _F1.w + _F3.z : _F1.w);
}

// 0.25.0: tiles (FluidSim.TileSleep): water sleeps a column TILE_SIZE particle sizes square at a time, once TILE_STILL of its
// awake water has kept to its spot for the sleep time and the tiles around it are settled too; a tile where TILE_MOVING of the
// water left its spot within the frame wakes its own and its neighbours' sleepers. Particle by particle, water froze while the
// water around it still settled (pits up to 8 cm deep, the "gel" look); a tile freezes it as it is when it is calm.
uint TileKeyOf(int2 t) { return (uint)clamp(t.x + 32768, 0, 65534) | ((uint)clamp(t.y + 32768, 0, 65534) << 16); }
int2 TileOf(float3 p) { return int2(floor(p.xz / (TILE_SIZE * _F3.w))); }

uint FindTile(uint key)
{
    uint h = Mix(key) & (TILE_TABLE - 1);
    for (uint probe = 0; probe < TILE_PROBES; probe++)
    {
        uint k = TileKeys[h];
        if (k == key) return h;
        if (k == EMPTY) return NONE;
        h = (h + 1) & (TILE_TABLE - 1);
    }
    return NONE;
}

[numthreads(GROUP, 1, 1)]
void ClearTiles(uint3 id : SV_DispatchThreadID)
{
    uint s = id.x;
    if (s >= TILE_TABLE) return;
    TileKeys[s] = EMPTY; TileFlags[s] = 0;
    [unroll] for (uint k = 0; k < 8; k++) TileData[s * 8 + k] = 0;
}

float FromOrdered(uint u) { return asfloat((u & 0x80000000u) ? (u & 0x7FFFFFFFu) : ~u); }
bool CanSleep(uint i) { return Rho[i] >= SLEEP_DENSITY * _F1.x && NbCount[i] >= SLEEP_NEIGHBOURS; }

// each particle's tile (made here), and the tile's top: the highest of its liquid's body (not a drop or a thin sheet above it)
[numthreads(GROUP, 1, 1)]
void TileTop(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 x = Pos[i].xyz;
    uint key = TileKeyOf(TileOf(x)), h = Mix(key) & (TILE_TABLE - 1), s = NONE;
    for (uint probe = 0; probe < TILE_PROBES && s == NONE; probe++)
    {
        uint prev;
        InterlockedCompareExchange(TileKeys[h], EMPTY, key, prev);
        if (prev == EMPTY || prev == key) s = h;
        else h = (h + 1) & (TILE_TABLE - 1);
    }
    if (s == NONE) return;   // the table is full: this water does not count (and does not sleep, see TileApply)
    if (Anchor[i].w >= _Sleep.y || CanSleep(i)) InterlockedMax(TileData[s * 8 + 5], OrderedUint(x.y));
}

[numthreads(GROUP, 1, 1)]
void TileCount(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 x = Pos[i].xyz;
    uint s = FindTile(TileKeyOf(TileOf(x)));
    if (s == NONE) return;
    // (motion counts near the top only, where it shows: deep down the packing sometimes slips into a closer one, which woke the
    // tiles around it every few seconds)
    bool near = x.y > FromOrdered(TileData[s * 8 + 5]) - TILE_BAND * _F3.w;
    if (near) InterlockedAdd(TileData[s * 8 + 4], 1);
    float c = Anchor[i].w;
    if (c >= _Sleep.y) { InterlockedAdd(TileData[s * 8 + 3], 1); return; }
    if (near && c > 0 && c <= _F3.z * 1.001) InterlockedAdd(TileData[s * 8 + 2], 1);   // left its spot this frame
    if (c < 0.5 * _Sleep.y) InterlockedAdd(State[25], 1);   // not settled: not still for half the sleep time
    // (stillness only of the water that can sleep: a thin sheet or a lone drop never does, and its jitter on a sleeping tile kept
    // that tile and so its neighbours from ever counting as settled)
    if (!CanSleep(i)) return;
    InterlockedAdd(TileData[s * 8], 1);
    if (c >= _Sleep.y - 1.5 * SETTLED_GAP) InterlockedAdd(TileData[s * 8 + 1], 1);
}

[numthreads(GROUP, 1, 1)]
void TileDecide(uint3 id : SV_DispatchThreadID)
{
    uint s = id.x;
    if (s >= TILE_TABLE || TileKeys[s] == EMPTY) return;
    float awake = TileData[s * 8], still = TileData[s * 8 + 1], moved = TileData[s * 8 + 2], band = TileData[s * 8 + 4];
    TileFlags[s] = (moved >= max(3.0, TILE_MOVING * band) ? 1u : 0u) | (still >= TILE_STILL * awake ? 2u : 0u);
}

[numthreads(GROUP, 1, 1)]
void TileApply(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 x = Pos[i].xyz;
    int2 t = TileOf(x);
    bool restless = false, settled = true;
    for (int dz = -1; dz <= 1; dz++)
    for (int dx = -1; dx <= 1; dx++)
    {
        uint s = FindTile(TileKeyOf(t + int2(dx, dz)));
        if (s == NONE) { if (dx == 0 && dz == 0) settled = false; continue; }
        uint f = TileFlags[s];
        if ((f & 1) != 0) restless = true;
        if ((f & 2) == 0) settled = false;
    }
    float4 an = Anchor[i];
    if (an.w >= _Sleep.y) { if (restless) Anchor[i] = float4(x, -TILE_GRACE); return; }
    // (a thin sheet or a lone drop never sleeps)
    // (it keeps its pressure, in Anchor.x: see Deltas)
    if (!restless && settled && Rho[i] >= SLEEP_DENSITY * _F1.x && NbCount[i] >= SLEEP_NEIGHBOURS) Anchor[i] = float4(Lambda[i], an.yz, _Sleep.y);
}

// 0.25.0: the divergence pass (Bender and Koschier's divergence-free SPH, as a velocity-level step of the density constraint):
// where neighbours inside the liquid move towards each other, that squeezing part of their motion is taken out, as the position
// step takes out the squeeze itself. Waves and flow move water without squeezing it and keep going; a resting pool's jitter
// (pushed apart, falling back) is squeezing, and settles. Only inside the liquid (at least SLEEP_NEIGHBOURS neighbours, as
// DFSPH), so drops and splashes still land and merge. Velocities in Delta; the lambdas in LambdaV.
[numthreads(GROUP, 1, 1)]
void DivLambdas(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    // (and only from DIV_DENSITY of rest density up, where coming closer is squeezing: well under it, as in a kicked drop that
    // cohesion pulls back together, the pass undid cohesion; at rest density exactly it left half a resting pool's jitter)
    if (Asleep(i) || NbCount[i] < SLEEP_NEIGHBOURS || Rho[i] < DIV_DENSITY * _F1.x) { LambdaV[i] = 0; return; }
    float4 pi = Pred[i];
    float3 vi = Delta[i].xyz;
    float cdot = 0, sum = 0; float3 gi = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float4 pj = Pred[j];
        if (asuint(pj.w) != asuint(pi.w)) continue;   // (within one liquid: oil has to slide up past water)
        float3 d = pi.xyz - pj.xyz;
        float3 gr = SpikyGrad(d, length(d));
        cdot += dot(gr, vi - Delta[j].xyz);
        gi += gr;
        if (!Asleep(j)) sum += (1.0 / Liquids[asuint(pj.w)].b.x) * dot(gr, gr);   // a sleeper is a wall
    }
    cdot /= _F1.x;
    if (!(cdot > 0)) { LambdaV[i] = 0; return; }   // only squeezing is taken out
    float wi = 1.0 / Liquids[asuint(pi.w)].b.x;
    LambdaV[i] = -cdot / ((sum + wi * dot(gi, gi)) / (_F1.x * _F1.x) + _F1.y);
}

// (Delta in, Vtmp out; the driver swaps them)
[numthreads(GROUP, 1, 1)]
void DivDeltas(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 di = Delta[i];
    if (Asleep(i)) { Vtmp[i] = di; return; }
    float4 pi = Pred[i];
    float li = LambdaV[i];
    float3 dv = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float4 pj = Pred[j];
        if (asuint(pj.w) != asuint(pi.w)) continue;
        float lsum = li + (Asleep(j) ? 0 : LambdaV[j]);
        if (lsum == 0) continue;
        float3 d = pi.xyz - pj.xyz;
        dv += SpikyGrad(d, length(d)) * lsum;
    }
    Vtmp[i] = float4(di.xyz + dv * ((1.0 / Liquids[asuint(pi.w)].b.x) / _F1.x), di.w);
}

// once a frame: ages, the count of each liquid, the bounds
[numthreads(GROUP, 1, 1)]
void FrameEnd(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float4 vv = Vel[i];
    Vel[i] = float4(vv.xyz, vv.w + _F3.z);
    if (!Asleep(i)) InterlockedAdd(State[17], 1);
    float sp = length(vv.xyz);
    if (sp > 0 && !isinf(sp)) InterlockedMax(State[16], asuint(sp));
    float4 x = Pos[i];
    InterlockedAdd(SourceCounts[asuint(x.w)], 1);
    InterlockedMin(State[3], OrderedUint(x.x)); InterlockedMin(State[4], OrderedUint(x.y)); InterlockedMin(State[5], OrderedUint(x.z));
    InterlockedMax(State[6], OrderedUint(x.x)); InterlockedMax(State[7], OrderedUint(x.y)); InterlockedMax(State[8], OrderedUint(x.z));
}

// ---------------------------------------------------------------------------------------------------------------- the surface

// a block's key: 11 bits x, 10 bits y, 11 bits z of its place on the lattice from _GMin (never EMPTY)
bool BrickKey(int3 b, out uint key)
{
    key = EMPTY;
    if (any(b < 0) || b.x >= 2047 || b.y >= 1023 || b.z >= 2047) return false;
    key = (uint)b.x | ((uint)b.y << 11) | ((uint)b.z << 21);
    return true;
}

int3 BrickOfKey(uint key) { return int3(key & 2047u, (key >> 11) & 1023u, key >> 21); }

uint BrickHash(uint key) { return Mix(key) & _U3.x; }

uint FindBrick(int3 b)
{
    uint key;
    if (!BrickKey(b, key)) return NONE;
    uint h = BrickHash(key);
    for (uint probe = 0; probe < 64; probe++)
    {
        uint k = BrickKeys[h];
        if (k == key)
        {
            // 0.22.0: the table keeps blocks between builds; only the ones in this build count
            uint s = BrickSlot[h];
            return s != NONE && (BrickMark[s] >> 2) == _U4.z ? s : NONE;
        }
        if (k == EMPTY) return NONE;
        h = (h + 1) & _U3.x;
    }
    return NONE;
}

int3 LocalOf(uint l) { return int3(l & 3u, (l >> 2) & 3u, l >> 4); }
uint LocalIndex(int3 l) { return (uint)l.x | ((uint)l.y << 2) | ((uint)l.z << 4); }

[numthreads(GROUP, 1, 1)]
void SurfaceBegin(uint3 id : SV_DispatchThreadID)
{
    if (id.x == 0)
    {
        State[10] = State[2]; State[1] = 0; State[2] = 0;
        // 0.22.0: the blocks and their fields are kept between builds, and forgotten (all recomputed) when asked (settings
        // changed), when particles were removed since the last build (their blocks are not known), or when the table is
        // half full of blocks the liquid has left
        bool reset = _U4.w != 0 || State[18] != 0 || State[12] > _U3.y / 2;
        State[19] = reset ? 1 : 0;
        if (reset) State[12] = 0;
        State[18] = 0; State[20] = 0; State[21] = 0; State[22] = 0; State[23] = 0; State[24] = 0;
    }
}

[numthreads(GROUP, 1, 1)]
void ClearBricks(uint3 id : SV_DispatchThreadID)
{
    if (id.x <= _U3.x && State[19] != 0) { BrickKeys[id.x] = EMPTY; BrickSlot[id.x] = NONE; }
}

// every particle adds the blocks whose points are within reach of it
[numthreads(GROUP, 1, 1)]
void MarkBricks(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 g = (Pos[i].xyz - _GMin.xyz) / _GMin.w;
    float reach = _SurfP.z / _GMin.w;
    int3 lo = int3(floor((g - reach) / 4)), hi = int3(floor((g + reach) / 4));
    for (int z = lo.z; z <= hi.z; z++)
    for (int y = lo.y; y <= hi.y; y++)
    for (int x = lo.x; x <= hi.x; x++)
    {
        uint key;
        if (!BrickKey(int3(x, y, z), key)) continue;
        uint h = BrickHash(key);
        for (uint probe = 0; probe < 64; probe++)
        {
            uint orig;
            InterlockedCompareExchange(BrickKeys[h], EMPTY, key, orig);
            if (orig == EMPTY)
            {
                uint s;
                InterlockedAdd(State[12], 1, s);
                InterlockedAdd(State[20], 1);
                if (s < _U3.y) { BrickCoord[s] = key; BrickMark[s] = _U4.z * 4 + 1; }   // new: computed
                InterlockedAdd(State[22], 1);
                BrickSlot[h] = s < _U3.y ? s : NONE;
                break;
            }
            if (orig == key)
            {
                // 0.22.0: a block from an earlier build: in this one too (recomputed if it was not in the last one)
                uint s = BrickSlot[h];
                if (s != NONE && s < _U3.y)
                {
                    uint prev;
                    InterlockedMax(BrickMark[s], _U4.z * 4, prev);
                    if ((prev >> 2) < _U4.z) { InterlockedAdd(State[20], 1); if ((prev >> 2) + 1 < _U4.z) InterlockedOr(BrickMark[s], 1); }
                }
                break;
            }
            h = (h + 1) & _U3.x;
        }
    }
}

// 0.18.0: each particle's radius in the surface. A particle with few neighbours (a lone droplet, a small cluster, a thin
// sheet) is drawn DropletSize times its own size, fading to its own size at 20 neighbours (the liquid's body), and never
// smaller than the grid can show (a radius under ~0.87 of a cell can fall between the grid points: the drop vanishes)
[numthreads(GROUP, 1, 1)]
void Radii(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 p = Pos[i].xyz;
    int3 c = CellOf(p);
    float R2 = _SurfP.x * _SurfP.x, invR = 1 / _SurfP.x;
    uint n = 0;
    float3 acc = 0; float ws = 0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        int3 q = c + int3(dx, dy, dz);
        uint s, e; CellRange(HashCell(q), s, e);
        for (uint j = s; j < e; j++)
        {
            float3 pj = Pos[j].xyz;
            if (any(CellOf(pj) != q)) continue;
            float3 d = p - pj;
            float r2 = dot(d, d);
            if (r2 >= R2) continue;
            // 0.20.0: the drawn position, towards the neighbours' mean (weights 1 - (r/R)^3, itself included)
            float u = sqrt(r2) * invR, w = 1 - u * u * u;
            acc += pj * w; ws += w;
            if (j != i) n++;
        }
    }
    // 0.24.0: w 1 = deep inside: many neighbours, all round it (their mean on top of it; a surface particle's lean to one side,
    // and a packed top layer can have as many): it cannot make a surface near itself
    float3 mean = ws > 0 ? acc / ws : p;
    bool deep = n >= DEEP_COUNT && length(mean - p) < DEEP_SKEW * _SurfP.x;
    Smooth[i] = float4(lerp(p, mean, SMOOTHING), deep ? 1 : 0);
    float t = saturate(n / DROPLET_FADE);
    t = t * t * (3 - 2 * t);
    float r = _SurfP.y * lerp(_SurfP.w, 1, t);
    Rad[i] = max(r, 0.9 * _GMin.w * (1 - t));
}

// 0.22.0: a particle that moved since the last build (awake and faster than SURFACE_STILL, or asleep for less time than has
// passed since it) marks the blocks within the field's reach of it as to be recomputed (the kernel radius and a cell, plus how
// far it can have come; its neighbours' smoothing and radii change by about a thirtieth of its move, left to the refresh)
[numthreads(GROUP, 1, 1)]
void MarkDirty(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (_U4.w != 0 || State[19] != 0) return;   // everything is recomputed anyway
    float4 an = Anchor[i];
    if (_Sleep.x > 0 && an.w >= _Sleep.y + _Surf2.x) return;   // asleep since before the last build: not moved
    // nearly still (a pool's awake top layer jitters well under a millimetre a frame): not moved either; every block is
    // recomputed every SURFACE_REFRESH builds anyway, so slow creep lags at most that many builds
    float3 vI = Vel[i].xyz;
    float still = SURFACE_STILL * _F3.w;
    if (dot(vI, vI) < still * still) return;
    float3 x = Pos[i].xyz;
    float reach = (_Surf2.y + length(Vel[i].xyz) * _Surf2.x + 0.5 * _F3.w) / _GMin.w;
    float3 g = (x - _GMin.xyz) / _GMin.w;
    int3 lo = int3(floor((g - reach) / 4)), hi = int3(floor((g + reach) / 4));
    for (int z = lo.z; z <= hi.z; z++)
    for (int y = lo.y; y <= hi.y; y++)
    for (int xx = lo.x; xx <= hi.x; xx++)
    {
        uint s = FindBrick(int3(xx, y, z));
        if (s != NONE) { uint pm; InterlockedOr(BrickMark[s], 1, pm); if ((pm & 1) == 0) InterlockedAdd(State[23], 1); }
    }
}

// 0.24.0: a particle near the surface (not deep) marks the blocks within the field's reach of it as near the surface; the
// others (only deep particles reach them: inside the liquid, where no surface can be) get no field (Blender's narrow band)
[numthreads(GROUP, 1, 1)]
void MarkShallow(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (_U5.y != 0 && Smooth[i].w > 0) return;   // deep
    float3 g = (Pos[i].xyz - _GMin.xyz) / _GMin.w;
    float reach = (_SurfP.x + _GMin.w + 0.5 * _F3.w) / _GMin.w;
    int3 lo = int3(floor((g - reach) / 4)), hi = int3(floor((g + reach) / 4));
    for (int z = lo.z; z <= hi.z; z++)
    for (int y = lo.y; y <= hi.y; y++)
    for (int x = lo.x; x <= hi.x; x++)
    {
        uint s = FindBrick(int3(x, y, z));
        if (s != NONE) InterlockedOr(BrickMark[s], 2);
    }
}

// 0.22.0: the vertex colours and the index tail run over what is used, not the buffers' whole size (400,000 vertices and
// 2.4 million indices every build before)
[numthreads(1, 1, 1)]
void ColorArgs(uint3 id : SV_DispatchThreadID)
{
    uint n = min(State[1], _U1.z);
    Args.Store3(24, uint3(max(1u, (n + GROUP - 1) / GROUP), 1, 1));
}

[numthreads(1, 1, 1)]
void TailArgs(uint3 id : SV_DispatchThreadID)
{
    uint now = min(State[2], _U1.w), was = min(State[10], _U1.w);
    uint n = was > now ? was - now : 0;
    Args.Store3(36, uint3(max(1u, (n + GROUP - 1) / GROUP), 1, 1));
}

// the indirect dispatch over the blocks' points (64 each)
[numthreads(1, 1, 1)]
void SurfaceArgs(uint3 id : SV_DispatchThreadID)
{
    uint n = min(State[12], _U3.y) * 64;
    Args.Store3(12, uint3(max(1u, (n + GROUP - 1) / GROUP), 1, 1));
}

uint BrickCount() { return min(State[12], _U3.y); }

[numthreads(GROUP, 1, 1)]
void Field(uint3 id : SV_DispatchThreadID)
{
    uint slot = id.x >> 6, l = id.x & 63;
    if (slot >= BrickCount()) return;
    // 0.22.0: a block not in this build is skipped; one in it whose particles did not move keeps its field
    uint mark = BrickMark[slot];
    if ((mark >> 2) != _U4.z) return;
    bool dirty = (mark & 1) != 0 || (slot + _U4.z) % SURFACE_REFRESH == 0;
    // 0.24.0: deep inside: no field to compute, only "inside"
    if ((mark & 2) == 0)
    {
        if (dirty) { Phi[id.x] = INTERIOR; if (l == 0) InterlockedAdd(State[24], 1); }
        return;
    }
    // near the surface: kept, unless something moved or it was deep inside before
    if (!dirty && Phi[id.x] > 0.5 * INTERIOR) return;
    if (l == 0) InterlockedAdd(State[21], 1);
    int3 g = BrickOfKey(BrickCoord[slot]) * 4 + LocalOf(l);
    float3 p = _GMin.xyz + float3(g) * _GMin.w;
    float R = _SurfP.x, R2 = R * R;
    int3 c = CellOf(p);
    float ws = 0, wr = 0; float3 wx = 0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        int3 q = c + int3(dx, dy, dz);
        uint s, e; CellRange(HashCell(q), s, e);
        for (uint j = s; j < e; j++)
        {
            if (any(CellOf(Pos[j].xyz) != q)) continue;
            float3 pj = Smooth[j].xyz;   // 0.20.0: as drawn (still found in the grid of where it really is)
            float3 d = p - pj;
            float r2 = dot(d, d);
            if (r2 >= R2) continue;
            float t = 1 - r2 / R2;
            float w = t * t * t;
            ws += w; wx += pj * w; wr += Rad[j] * w;
        }
    }
    // Zhu and Bridson's field with the particles' own radii, weighted like their positions
    Phi[id.x] = ws > 1e-6 ? length(p - wx / ws) - wr / ws : FAR;
}

// the field at a lattice point (FAR where no block is: that is away from the liquid)
float PhiAt(int3 g, uint ownSlot, int3 ownOrigin)
{
    int3 l = g - ownOrigin;
    if (all(l >= 0) && all(l < 4)) return Phi[ownSlot * 64 + LocalIndex(l)];
    int3 b = int3(floor(float3(g) / 4));
    uint s = FindBrick(b);
    if (s == NONE) return FAR;
    return Phi[s * 64 + LocalIndex(g - b * 4)];
}

static const uint EdgeA[12] = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
static const uint EdgeB[12] = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

[numthreads(GROUP, 1, 1)]
void CellVertices(uint3 id : SV_DispatchThreadID)
{
    uint slot = id.x >> 6, l = id.x & 63;
    if (slot >= BrickCount()) return;
    CellVert[id.x] = -1;
    if ((BrickMark[slot] >> 2) != _U4.z) return;   // 0.22.0: not in this build
    int3 origin = BrickOfKey(BrickCoord[slot]) * 4;
    int3 g = origin + LocalOf(l);
    float v[8];
    uint inside = 0; bool far = false;
    [unroll] for (uint k = 0; k < 8; k++)
    {
        float f = PhiAt(g + int3(k & 1, (k >> 1) & 1, (k >> 2) & 1), slot, origin);
        v[k] = f;
        if (f >= FAR) far = true;
        if (f < 0) inside |= 1u << k;
    }
    if (inside == 0 || inside == 255) return;
    float3 acc = 0; float crossings = 0;
    [unroll] for (uint e = 0; e < 12; e++)
    {
        uint a = EdgeA[e], b = EdgeB[e];
        bool ia = (inside & (1u << a)) != 0, ib = (inside & (1u << b)) != 0;
        if (ia == ib) continue;
        float fa = v[a], fb = v[b];
        float t = (fa >= FAR || fb >= FAR || fa == fb) ? 0.5 : saturate(fa / (fa - fb));
        float3 pa = float3(a & 1, (a >> 1) & 1, (a >> 2) & 1), pb = float3(b & 1, (b >> 1) & 1, (b >> 2) & 1);
        acc += pa + (pb - pa) * t;
        crossings += 1;
    }
    float3 wp = _GMin.xyz + (float3(g) + acc / crossings) * _GMin.w;
    float3 n = float3(0, 1, 0);
    if (!far)
    {
        float3 gr = float3(v[1] - v[0] + v[3] - v[2] + v[5] - v[4] + v[7] - v[6],
                           v[2] - v[0] + v[3] - v[1] + v[6] - v[4] + v[7] - v[5],
                           v[4] - v[0] + v[5] - v[1] + v[6] - v[2] + v[7] - v[3]);
        float gl = length(gr);
        if (gl > 1e-9 && !isinf(gl)) n = gr / gl;
    }
    uint vi;
    InterlockedAdd(State[1], 1, vi);
    if (vi >= _U1.z) return;
    CellVert[id.x] = (int)vi;
    uint o = vi * 40;
    Verts.Store3(o, asuint(wp));
    Verts.Store3(o + 12, asuint(n));
    Verts.Store4(o + 24, asuint(float4(0.6, 0.8, 1, 0.35)));
}

// each vertex's colour from the liquids near it
[numthreads(GROUP, 1, 1)]
void VertexColors(uint3 id : SV_DispatchThreadID)
{
    uint vi = id.x;
    if (vi >= min(State[1], _U1.z)) return;
    uint o = vi * 40;
    float3 p = asfloat(Verts.Load3(o));
    float R = _SurfP.x, R2 = R * R;
    int3 c = CellOf(p);
    float4 col = 0; float ws = 0; float near = 0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        int3 q = c + int3(dx, dy, dz);
        uint s, e; CellRange(HashCell(q), s, e);
        for (uint j = s; j < e; j++)
        {
            float4 pj = Pos[j];
            if (any(CellOf(pj.xyz) != q)) continue;
            float3 d = p - pj.xyz;
            float r2 = dot(d, d);
            if (r2 >= R2) continue;
            float t = 1 - r2 / R2;
            float w = t * t * t;
            col += Liquids[asuint(pj.w)].color * w; ws += w;
            near += 1;
        }
    }
    if (ws > 1e-9) Verts.Store4(o + 24, asuint(col / ws));
    // 0.25.0: the smoothing's start: how surrounded by liquid the vertex is
    SPos[vi] = float4(p, saturate((near - MESH_SMOOTH_FROM) / (MESH_SMOOTH_FULL - MESH_SMOOTH_FROM)));
    SNrm[vi] = float4(asfloat(Verts.Load3(o + 12)), 0);
}

// the vertex of a cell (or -1)
int CellVertex(int3 c)
{
    int3 b = int3(floor(float3(c) / 4));
    uint s = FindBrick(b);
    if (s == NONE) return -1;
    int v = CellVert[s * 64 + LocalIndex(c - b * 4)];
    return v >= (int)_U1.z ? -1 : v;
}

void EmitQuad(int3 a, int3 b, int3 c, int3 d, bool flip)
{
    int va = CellVertex(a), vb = CellVertex(b), vc = CellVertex(c), vd = CellVertex(d);
    if (va < 0 || vb < 0 || vc < 0 || vd < 0) return;
    uint o;
    InterlockedAdd(State[2], 6, o);
    if (o + 6 > _U1.w) return;
    uint ob = o * 4;
    if (flip) { Indices.Store3(ob, uint3(va, vb, vc)); Indices.Store3(ob + 12, uint3(va, vc, vd)); }
    else { Indices.Store3(ob, uint3(va, vc, vb)); Indices.Store3(ob + 12, uint3(va, vd, vc)); }
}

// 0.25.0: mesh smoothing (FluidSurface.SmoothMesh): Taubin's weighted Laplacian passes over the finished mesh (Löschner, Böttcher,
// Jeske and Bender 2023). A vertex's neighbours are the vertices of the six cells next to its own whose shared face the surface
// crosses (they share an edge; the face test keeps the two sides of a thin sheet from being pulled together).
float PhiAtPoint(int3 g)
{
    int3 b = int3(floor(float3(g) / 4));
    uint s = FindBrick(b);
    return s == NONE ? FAR : Phi[s * 64 + LocalIndex(g - b * 4)];
}

[numthreads(GROUP, 1, 1)]
void SmoothNbrs(uint3 id : SV_DispatchThreadID)
{
    uint vi = id.x;
    if (vi >= min(State[1], _U1.z)) return;
    // its cell: the one its (unsmoothed) position lies in (on a cell's upper face, the cell below)
    float3 lp = (asfloat(Verts.Load3(vi * 40)) - _GMin.xyz) / _GMin.w;
    int3 c = int3(floor(lp));
    if (CellVertex(c) != (int)vi)
    {
        int3 alt = c - int3(frac(lp) < 1e-3);
        c = CellVertex(alt) == (int)vi ? alt : int3(-100000, 0, 0);
    }
    [unroll] for (int d = 0; d < 6; d++)
    {
        int axis = d >> 1, sgn = (d & 1) == 0 ? -1 : 1;
        int3 dir = int3(axis == 0 ? sgn : 0, axis == 1 ? sgn : 0, axis == 2 ? sgn : 0);
        int u = c.x > -100000 ? CellVertex(c + dir) : -1;
        if (u >= 0)
        {
            int3 f = c + max(dir, 0);   // the shared face: its four lattice points
            int3 e1 = axis == 0 ? int3(0, 1, 0) : int3(1, 0, 0), e2 = axis == 2 ? int3(0, 1, 0) : int3(0, 0, 1);
            uint ins = (PhiAtPoint(f) < 0 ? 1u : 0u) + (PhiAtPoint(f + e1) < 0 ? 1u : 0u) + (PhiAtPoint(f + e2) < 0 ? 1u : 0u) + (PhiAtPoint(f + e1 + e2) < 0 ? 1u : 0u);
            if (ins == 0 || ins == 4) u = -1;
        }
        VertNbr[vi * 6 + d] = u;
    }
}

void SmoothPass(uint vi, float k)
{
    if (vi >= min(State[1], _U1.z)) return;
    float4 sp = SPos[vi]; float4 sn = SNrm[vi];
    float3 sum = 0, nsum = 0; float c = 0;
    if (sp.w > 0)
        [unroll] for (int d = 0; d < 6; d++)
        {
            int u = VertNbr[vi * 6 + d];
            if (u < 0) continue;
            sum += SPos[u].xyz; nsum += SNrm[u].xyz; c += 1;
        }
    if (c == 0) { SPosTmp[vi] = sp; SNrmTmp[vi] = sn; return; }
    SPosTmp[vi] = float4(sp.xyz + (sum / c - sp.xyz) * (k * sp.w), sp.w);
    float3 nn = sn.xyz + (nsum / c - sn.xyz) * (TAUBIN_IN * sp.w);
    float l = length(nn);
    SNrmTmp[vi] = float4(l > 1e-9 ? nn / l : sn.xyz, 0);
}

[numthreads(GROUP, 1, 1)]
void SmoothIn(uint3 id : SV_DispatchThreadID) { SmoothPass(id.x, TAUBIN_IN); }

[numthreads(GROUP, 1, 1)]
void SmoothOut(uint3 id : SV_DispatchThreadID) { SmoothPass(id.x, TAUBIN_OUT); }

[numthreads(GROUP, 1, 1)]
void SmoothWrite(uint3 id : SV_DispatchThreadID)
{
    uint vi = id.x;
    if (vi >= min(State[1], _U1.z)) return;
    Verts.Store3(vi * 40, asuint(SPos[vi].xyz));
    Verts.Store3(vi * 40 + 12, asuint(SNrm[vi].xyz));
}

// a quad across every lattice edge the surface crosses, between the four cells around it
[numthreads(GROUP, 1, 1)]
void Quads(uint3 id : SV_DispatchThreadID)
{
    uint slot = id.x >> 6, l = id.x & 63;
    if (slot >= BrickCount()) return;
    if ((BrickMark[slot] >> 2) != _U4.z) return;   // 0.22.0: not in this build
    int3 origin = BrickOfKey(BrickCoord[slot]) * 4;
    int3 g = origin + LocalOf(l);
    bool in0 = Phi[id.x] < 0;
    if ((PhiAt(g + int3(1, 0, 0), slot, origin) < 0) != in0) EmitQuad(g + int3(0, -1, -1), g + int3(0, 0, -1), g, g + int3(0, -1, 0), in0);
    if ((PhiAt(g + int3(0, 1, 0), slot, origin) < 0) != in0) EmitQuad(g + int3(-1, 0, -1), g + int3(-1, 0, 0), g, g + int3(0, 0, -1), in0);
    if ((PhiAt(g + int3(0, 0, 1), slot, origin) < 0) != in0) EmitQuad(g + int3(-1, -1, 0), g + int3(0, -1, 0), g, g + int3(-1, 0, 0), in0);
}

// the indices past this surface's end (up to the last one's) become degenerate, so the mesh draws only this surface
[numthreads(GROUP, 1, 1)]
void ClearTail(uint3 id : SV_DispatchThreadID)
{
    uint now = min(State[2], _U1.w), was = min(State[10], _U1.w);
    uint k = now + id.x;
    if (k < was) Indices.Store(k * 4, 0);
}

// ---------------------------------------------------------------------------------------------------------------- gas (0.26.0)

// how far a moving shape stirs the air around it (metres); a puff fades in over GAS_FADE_IN seconds and out over the last
// GAS_FADE_OUT of its life
#define GAS_REACH 0.3
#define GAS_FADE_IN 0.15
#define GAS_FADE_OUT 0.3

[numthreads(GROUP, 1, 1)]
void GasClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x < _G0.x) GasPos[id.x] = float4(0, 0, 0, -1);
}

[numthreads(1, 1, 1)]
void GasBegin(uint3 id : SV_DispatchThreadID)
{
    State[36] = State[29];
    State[28] = 0; State[29] = 0; State[26] = 0;   // (0.27.0: 26 the live vortex particles)
    State[30] = 0xFFFFFFFFu; State[31] = 0xFFFFFFFFu; State[32] = 0xFFFFFFFFu;
    State[33] = 0; State[34] = 0; State[35] = 0;
}

[numthreads(GROUP, 1, 1)]
void GasSpawn(uint3 id : SV_DispatchThreadID)
{
    uint k = id.x;
    if (k >= _G0.y) return;
    uint slot = (_G0.z + k) % _G0.x;
    float4 p = GasSpawns[k * 2], v = GasSpawns[k * 2 + 1];
    GasPos[slot] = float4(p.xyz, 0);
    GasVel[slot] = v;
    GasEmit[slot] = asuint(p.w);
}

// swirling air: the curl of a smooth vector potential (two octaves of drifting sines), so it neither gathers nor thins the
// smoke; about 1 m/s at its strongest
float3 SwirlAt(float3 p, float t)
{
    float3 v = 0;
    float amp = 1;
    float k = 5.0;
    [unroll] for (int o = 0; o < 2; o++)
    {
        float3 ph = float3(0.7, 0.5, 0.6) * t * (1 + o) + float3(1.3, 2.1, 0.4) * (1 + o);
        float c1 = 0.8, c2 = 0.9, c3 = 1.1;
        float sy = sin(k * p.y + ph.x), cy = cos(k * p.y + ph.x);
        float sz = sin(k * p.z + ph.y), cz = cos(k * p.z + ph.y);
        float sx = sin(k * p.x + ph.z), cx = cos(k * p.x + ph.z);
        // A = (sin(k y + a) cos(c1 k z), sin(k z + b) cos(c2 k x), sin(k x + c) cos(c3 k y))
        float dAx_dy = cy * cos(c1 * k * p.z), dAx_dz = -c1 * sy * sin(c1 * k * p.z);
        float dAy_dz = cz * cos(c2 * k * p.x), dAy_dx = -c2 * sz * sin(c2 * k * p.x);
        float dAz_dx = cx * cos(c3 * k * p.y), dAz_dy = -c3 * sx * sin(c3 * k * p.y);
        v += amp * float3(dAz_dy - dAy_dz, dAx_dz - dAz_dx, dAy_dx - dAx_dy);
        amp *= 0.5; k *= 2.3;
    }
    return v * 0.5;
}

// ---- 0.27.0: vortex particles (vortex blobs). Each carries a strength alpha (its vorticity times its volume) and a core radius
// sigma; the air they induce is the regularised Biot-Savart sum u(x) = 1/(4 pi) sum alpha x r / (|r|² + sigma²)^(3/2), r = x - x_p,
// which is divergence-free like the curl noise but moves with them and lasts as long as they do. A moving shape in the gas sheds a
// small ring of them behind it (the runtime decides where); they carry each other along (a ring travels on its own), and fade.
#define VORT_INV4PI 0.0795774715

float3 VortexAir(float3 x, float4 p, float4 s)
{
    float3 r = x - p.xyz;
    float d2 = dot(r, r) + s.w * s.w;
    return cross(s.xyz, r) * rsqrt(d2 * d2 * d2);
}

[numthreads(GROUP, 1, 1)]
void VortClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x < _V0.x) { VortPos[id.x] = float4(0, 0, 0, -1); VortStr[id.x] = 0; VortVel[id.x] = 0; }
}

[numthreads(GROUP, 1, 1)]
void VortSpawn(uint3 id : SV_DispatchThreadID)
{
    uint k = id.x;
    if (k >= _V0.y) return;
    uint slot = (_V0.z + k) % _V0.x;
    float4 p = VortSpawns[k * 2], s = VortSpawns[k * 2 + 1];
    if (Bad(p.xyz) || Bad(s.xyz)) return;
    VortPos[slot] = float4(p.xyz, 0);
    VortStr[slot] = float4(s.xyz, max(p.w, 1e-3));
}

// each one moves with what the others induce there (O(n²): a few hundred at most)
[numthreads(GROUP, 1, 1)]
void VortVelocity(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _V0.x) return;
    float4 p = VortPos[i];
    if (p.w < 0) return;
    float3 u = 0;
    for (uint j = 0; j < _V0.x; j++)
    {
        if (j == i) continue;
        float4 q = VortPos[j];
        if (q.w < 0) continue;
        u += VortexAir(p.xyz, q, VortStr[j]);
    }
    VortVel[i] = float4(u * VORT_INV4PI, 0);
}

[numthreads(GROUP, 1, 1)]
void VortMove(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _V0.x) return;
    float4 p = VortPos[i];
    if (p.w < 0) return;
    float dt = _G2.x;
    float age = p.w + dt;
    float3 x = p.xyz + VortVel[i].xyz * dt;
    float4 s = VortStr[i];
    s.xyz *= exp(-dt / max(_V1.y, 1e-3));
    if (age >= _V1.x || Bad(x)) { VortPos[i] = float4(x, -1); return; }
    VortPos[i] = float4(x, age);
    VortStr[i] = s;
    InterlockedAdd(State[26], 1);
}

groupshared float4 _vortP[GROUP];
groupshared float4 _vortS[GROUP];

// a puff: rises (buoyancy), takes on the air's motion (drag; the air swirls, and moving shapes near it push it along: a hand
// waved through smoke carries it), and is pushed out of the colliders (the liquid's shapes, at _F1.z, set for this pass)
// (0.27.0: plus the vortex particles' air, by its emitter's share of the strongest Vortices; every thread of a group reads them in
// tiles, so an empty slot returns only after that)
[numthreads(GROUP, 1, 1)]
void GasUpdate(uint3 id : SV_DispatchThreadID, uint3 tid : SV_GroupThreadID)
{
    uint i = id.x;
    bool live = i < _G0.x;
    float4 x = live ? GasPos[i] : float4(0, 0, 0, -1);
    live = live && x.w >= 0;
    float3 vortexAir = 0;
    if (_V0.w != 0)
    {
        for (uint tile = 0; tile < _V0.x; tile += GROUP)
        {
            uint j = tile + tid.x;
            _vortP[tid.x] = j < _V0.x ? VortPos[j] : float4(0, 0, 0, -1);
            _vortS[tid.x] = j < _V0.x ? VortStr[j] : float4(0, 0, 0, 1);
            GroupMemoryBarrierWithGroupSync();
            if (live)
                for (uint k = 0; k < GROUP; k++)
                {
                    float4 q = _vortP[k];
                    if (q.w >= 0) vortexAir += VortexAir(x.xyz, q, _vortS[k]);
                }
            GroupMemoryBarrierWithGroupSync();
        }
    }
    if (!live) return;
    float4 vv = GasVel[i];
    GasEmitter e = GasEmitters[GasEmit[i]];
    float dt = _G2.x;
    float age = x.w + dt;
    if (age >= vv.w || Bad(x.xyz) || Bad(vv.xyz)) { GasPos[i] = float4(x.xyz, -1); return; }
    float3 air = SwirlAt(x.xyz, _G1.w) * e.a.z + vortexAir * (VORT_INV4PI * _V1.z * e.d.x);
    for (uint q = 0; q < _U5.z; q++)
    {
        Shape sh = Shapes[MovingShapes[q]];
        float3 c = clamp(x.xyz, sh.BMin.xyz, sh.BMax.xyz);
        float d = length(x.xyz - c);
        if (d < GAS_REACH) air += sh.V.xyz * (1 - d / GAS_REACH);
    }
    float3 v = vv.xyz;
    v.y += e.a.x * dt;
    v += (air - v) * (1 - exp(-e.a.y * dt));
    float3 x0 = x.xyz;
    float3 p = x0 + v * dt;
    uint mask = asuint(e.b.w);
    if (mask != 0)
    {
        float3 before = p;
        float3 back = x0 - p; float bl = length(back);
        float3 prefer = bl > 1e-6 ? back / bl : 0;
        uint cells = _U2.x * _U2.y * _U2.z;
        if (cells > 0)
        {
            uint ci = ShapeCellOf(p);
            uint s = ShapeGrid[ci], en = ShapeGrid[ci + 1];
            for (uint q2 = s; q2 < en; q2++) PushShape(p, x0, Shapes[ShapeGrid[cells + 1 + q2]], mask, prefer);
        }
        for (uint g = 0; g < _U0.w; g++) PushShape(p, x0, Shapes[GlobalShapes[g]], mask, prefer);
        if (any(p != before))
        {
            float3 n = p - before; float nl = length(n);
            if (nl > 1e-9) { n /= nl; float vn = dot(v, n); if (vn < 0) v -= n * vn; }
        }
    }
    GasPos[i] = float4(p, age);
    GasVel[i] = float4(v, vv.w);
}

// each live puff: a disc of 7 vertices (its centre in its colour and opacity, 6 around it clear) facing the head
[numthreads(GROUP, 1, 1)]
void GasDraw(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _G0.x) return;
    float4 x = GasPos[i];
    if (x.w < 0) return;
    InterlockedAdd(State[28], 1);
    InterlockedMin(State[30], OrderedUint(x.x)); InterlockedMin(State[31], OrderedUint(x.y)); InterlockedMin(State[32], OrderedUint(x.z));
    InterlockedMax(State[33], OrderedUint(x.x)); InterlockedMax(State[34], OrderedUint(x.y)); InterlockedMax(State[35], OrderedUint(x.z));
    float4 vv = GasVel[i];
    GasEmitter e = GasEmitters[GasEmit[i]];
    float life = max(vv.w, 1e-3), t = saturate(x.w / life);
    float size = lerp(e.b.x, e.b.y, t);
    float alpha = e.color.a * saturate(x.w / GAS_FADE_IN) * saturate((life - x.w) / (GAS_FADE_OUT * life));
    if (!(alpha > 0.002) || !(size > 0)) return;
    uint d;
    InterlockedAdd(State[29], 1, d);
    if (d >= _G0.x) return;
    float3 f = _G1.xyz - x.xyz; float fl = length(f);
    f = fl > 1e-5 ? f / fl : float3(0, 0, 1);
    float3 r = cross(float3(0, 1, 0), f);
    r = dot(r, r) > 1e-8 ? normalize(r) : float3(1, 0, 0);
    float3 u = cross(f, r);
    float rad = 0.5 * size;
    uint vb = d * 7, o = vb * 56;
    GasVerts.Store3(o, asuint(x.xyz));
    GasVerts.Store3(o + 12, asuint(f));
    GasVerts.Store4(o + 24, asuint(float4(e.color.rgb, alpha)));
    [unroll] for (uint k = 0; k < 6; k++)
    {
        float a = k * 1.0471976;
        uint ok = o + (k + 1) * 56;
        GasVerts.Store3(ok, asuint(x.xyz + (r * cos(a) + u * sin(a)) * rad));
        GasVerts.Store3(ok + 12, asuint(f));
        GasVerts.Store4(ok + 24, asuint(float4(e.color.rgb, 0)));
        GasIndices.Store3((d * 18 + k * 3) * 4, uint3(vb, vb + 1 + k, vb + 1 + (k + 1) % 6));
    }
}

// the indices past this frame's puffs (up to the last frame's) become degenerate
[numthreads(GROUP, 1, 1)]
void GasTail(uint3 id : SV_DispatchThreadID)
{
    uint now = min(State[29], _G0.x) * 18, was = min(State[36], _G0.x) * 18;
    uint k = now + id.x;
    if (k < was) GasIndices.Store(k * 4, 0);
}

// ------------------------------------------------------------------------------------------- 0.27.0: the smoke shader's puffs
// When the renderer fork loaded its smoke shader (GpuFluid/Shaders/SoftbodySmoke.shader; _S0.w 1) each live puff is a quad of 4
// vertices that all hold its centre (the shader turns it to face each eye), and carries what the shader needs to light it: how much of
// the world's main light reaches it through the smoke (_S0.xyz points towards that light; 0: none) and how much of the sky (from
// above), both from the gas's own density on a hashed grid of cells of _S1.x (each puff's extinction spread over the 8 cells around
// its centre, read back the same way). The quads are written far to near from the head (a counting sort on GAS_BUCKETS buckets of log
// distance; the order within a bucket is the GPU's), so the smoke blends in order. Without the shader the discs above are drawn.
#define GAS_DENSITY_TABLE 262144
#define GAS_DENSITY_SCALE 4096.0
#define GAS_BUCKETS 1024
#define GAS_SORT_FAR 64.0          // metres: farther puffs share the last bucket
#define GAS_LIGHT_STEPS 12         // the march towards the light: cells, each 1.25 times the last (about 54 cells in all)
#define GAS_SKY_STEPS 6            // and up, for the sky

// a live puff as drawn: its centre, radius, colour and opacity, and its age (a share of its life); false: nothing to draw
bool GasLook(uint i, out float3 p, out float rad, out float4 col, out float t)
{
    float4 x = GasPos[i];
    p = x.xyz; rad = 0; col = 0; t = 0;
    if (x.w < 0) return false;
    float4 vv = GasVel[i];
    GasEmitter e = GasEmitters[GasEmit[i]];
    float life = max(vv.w, 1e-3);
    t = saturate(x.w / life);
    float size = lerp(e.b.x, e.b.y, t);
    float alpha = e.color.a * saturate(x.w / GAS_FADE_IN) * saturate((life - x.w) / (GAS_FADE_OUT * life));
    rad = 0.5 * size;
    col = float4(e.color.rgb, alpha);
    return alpha > 0.002 && size > 0;
}

// far first: the bucket of its distance from the head (log scale: about 1 cm apart at 1 m, 5 cm at 10 m)
uint GasBucket(float3 p)
{
    float d = min(length(p - _G1.xyz), GAS_SORT_FAR);
    uint b = min((uint)(log2(1 + d) * ((GAS_BUCKETS - 1) / log2(1 + GAS_SORT_FAR))), GAS_BUCKETS - 1);
    return GAS_BUCKETS - 1 - b;
}

uint GasDensityCell(int3 c)
{
    return ((uint)c.x * 73856093u ^ (uint)c.y * 19349663u ^ (uint)c.z * 83492791u) & (GAS_DENSITY_TABLE - 1);
}

// the gas's extinction here (per metre), trilinear between the cells' centres
float GasDensityAt(float3 p)
{
    float3 g = p / _S1.x - 0.5;
    float3 b = floor(g);
    float3 f = g - b;
    int3 bi = (int3)b;
    float s = 0;
    [unroll] for (uint k = 0; k < 8; k++)
    {
        float3 o = float3(k & 1, (k >> 1) & 1, k >> 2);
        float3 w = lerp(1 - f, f, o);
        s += w.x * w.y * w.z * (float)GasDensity[GasDensityCell(bi + (int3)o)];
    }
    return s * (1.0 / GAS_DENSITY_SCALE);
}

// the optical depth from p along dir (cells growing by a quarter each step), times the self-shadow strength
float GasDepthAlong(float3 p, float3 dir, uint steps)
{
    float tau = 0, s = 0, ds = _S1.x;
    for (uint k = 0; k < steps; k++)
    {
        tau += GasDensityAt(p + dir * (s + 0.5 * ds)) * ds;
        s += ds;
        ds *= 1.25;
    }
    return tau * _S1.y;
}

// every slot: counts the live puffs and their bounds (as GasDraw); each puff to draw counts in its distance's bucket and spreads its
// extinction (so a ray through its middle loses its opacity: sigma = -ln(1 - alpha) / 2r, times its volume) over the 8 cells around it
[numthreads(GROUP, 1, 1)]
void GasSplat(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _G0.x) return;
    float3 p; float rad; float4 col; float t;
    bool draw = GasLook(i, p, rad, col, t);
    if (GasPos[i].w < 0) return;
    InterlockedAdd(State[28], 1);
    InterlockedMin(State[30], OrderedUint(p.x)); InterlockedMin(State[31], OrderedUint(p.y)); InterlockedMin(State[32], OrderedUint(p.z));
    InterlockedMax(State[33], OrderedUint(p.x)); InterlockedMax(State[34], OrderedUint(p.y)); InterlockedMax(State[35], OrderedUint(p.z));
    if (!draw) return;
    InterlockedAdd(GasBuckets[GasBucket(p)], 1);
    float c = _S1.x;
    float sigma = -log(1 - min(col.a, 0.995)) / (2 * rad);
    float amount = min(sigma * 4.18879 * rad * rad * rad / (c * c * c), 2e5) * GAS_DENSITY_SCALE;
    float3 g = p / c - 0.5;
    float3 b = floor(g);
    float3 f = g - b;
    int3 bi = (int3)b;
    [unroll] for (uint k = 0; k < 8; k++)
    {
        float3 o = float3(k & 1, (k >> 1) & 1, k >> 2);
        float3 w = lerp(1 - f, f, o);
        uint add = (uint)(amount * w.x * w.y * w.z + 0.5);
        if (add > 0) InterlockedAdd(GasDensity[GasDensityCell(bi + (int3)o)], add);
    }
}

groupshared uint _gasScan[GAS_BUCKETS];

// one group: where each bucket's quads start (an exclusive scan), and how many are drawn
[numthreads(GAS_BUCKETS, 1, 1)]
void GasBucketScan(uint3 tid : SV_GroupThreadID)
{
    uint k = tid.x;
    uint v = GasBuckets[k];
    _gasScan[k] = v;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint off = 1; off < GAS_BUCKETS; off <<= 1)
    {
        uint add = k >= off ? _gasScan[k - off] : 0;
        GroupMemoryBarrierWithGroupSync();
        _gasScan[k] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    GasBuckets[GAS_BUCKETS + k] = _gasScan[k] - v;
    if (k == GAS_BUCKETS - 1) State[29] = _gasScan[k];
}

// each puff to draw: its place in the order, the light and sky reaching it, and its quad
[numthreads(GROUP, 1, 1)]
void GasDrawQuads(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= _G0.x) return;
    float3 p; float rad; float4 col; float t;
    if (!GasLook(i, p, rad, col, t)) return;
    uint slot;
    InterlockedAdd(GasBuckets[GAS_BUCKETS + GasBucket(p)], 1, slot);
    if (slot >= _G0.x) return;
    float lit = 1;
    if (dot(_S0.xyz, _S0.xyz) > 0.25)
    {
        // (light scattered on through the smoke keeps some of it lit: a second, weaker extinction for a third of it)
        float tau = GasDepthAlong(p, normalize(_S0.xyz), GAS_LIGHT_STEPS);
        lit = 0.65 * exp(-tau) + 0.35 * exp(-0.3 * tau);
    }
    float sky = 0.25 + 0.75 * exp(-0.6 * GasDepthAlong(p, float3(0, 1, 0), GAS_SKY_STEPS));
    // its own random number, from its slot (the same all its life)
    uint h = i * 747796405u + 2891336453u;
    h = ((h >> ((h >> 28) + 4)) ^ h) * 277803737u;
    h = (h >> 22) ^ h;
    float seed = (h & 0xFFFFFF) / 16777216.0;
    uint vb = slot * 4, o = vb * 56;
    [unroll] for (uint k = 0; k < 4; k++)
    {
        float2 corner = float2(k == 1 || k == 2 ? 1 : -1, k >= 2 ? 1 : -1);
        uint ok = o + k * 56;
        GasVerts.Store3(ok, asuint(p));
        GasVerts.Store3(ok + 12, asuint(float3(rad, lit, sky)));
        GasVerts.Store4(ok + 24, asuint(col));
        GasVerts.Store4(ok + 40, asuint(float4(corner, seed, t)));
    }
    GasIndices.Store3(slot * 24, uint3(vb, vb + 1, vb + 2));
    GasIndices.Store3(slot * 24 + 12, uint3(vb, vb + 2, vb + 3));
}

// the indices past this frame's quads (up to the last frame's) become degenerate
[numthreads(GROUP, 1, 1)]
void GasTailQuads(uint3 id : SV_DispatchThreadID)
{
    uint now = min(State[29], _G0.x) * 6, was = min(State[36], _G0.x) * 6;
    uint k = now + id.x;
    if (k < was) GasIndices.Store(k * 4, 0);
}

// ------------------------------------------------------------------------------------------------ 0.27.0: spray, foam and bubbles
// Ihmsen, Akinci, Akinci, Teschner, "Unified Spray, Foam and Bubbles for Particle-Based Fluids" (CGI 2012), as a post-process once a
// frame after the liquid's substeps, for the liquids whose Foam is over 0. A fluid particle with enough kinetic energy makes diffuse
// particles where it traps air (neighbours closing in fast) or rides a wave crest (a convex surface moving outward), in the cylinder
// of its path ahead (this frame's velocity). They do not act on the liquid. Each one is classified by the fluid particles around it
// (under DIFF_SPRAY: spray, over DIFF_BUBBLE: a bubble, else foam) and moved by that: spray flies (gravity), foam goes with the
// liquid's mean velocity, a bubble rises (buoyancy) and is dragged along. Foam ages; spray and bubbles age a tenth as fast (they turn
// into foam). State: 40 the ring's head, 41 spawns asked this frame, 42 live, 43 spray, 44 foam, 45 bubbles, 46 drawn, 47 the last
// frame's drawn, 48-53 bounds (ordered uints), 54 fluid particles that made some this frame, 55 this frame's budget.
// The paper's thresholds (tau) are kept for trapped air and wave crests (their units: speeds times the 1 - r/h weights, which sum
// to about 8 at our 2-spacing kernel); its kinetic energy (0.5 m v^2, 5..50) depends on its scene's mass, so it is per unit mass
// here, from 1 m/s (0.5) to 3.2 m/s (5). The k's are far below the paper's offline 1500 and 2500 samples a second.
// (SoftbodyRnD prototyped and measured it: RND-REPORT.md, section 1.)
#define DIFF_TA_MIN 5.0
#define DIFF_TA_MAX 20.0
#define DIFF_WC_MIN 2.0
#define DIFF_WC_MAX 8.0
#define DIFF_K_MIN 0.5
#define DIFF_K_MAX 5.0
#define DIFF_KTA 40.0
#define DIFF_KWC 60.0
#define DIFF_SPRAY 6
#define DIFF_BUBBLE 20
#define DIFF_BUOYANCY 2.0
#define DIFF_DRAG 0.8
#define DIFF_NRM_SURF 0.25
#define DIFF_MAX_EACH 8

float Clamp01Range(float x, float lo, float hi) { return (min(x, hi) - min(x, lo)) / (hi - lo); }

[numthreads(1, 1, 1)]
void DiffuseBegin(uint3 id : SV_DispatchThreadID)
{
    // 55: this frame's budget, at most the slots the last frame left free: while the ring is full no new ones are made (SoftbodyRnD
    // overwrote live foam early at 500k particles, so it popped)
    State[55] = min(_D0.z, _D0.x - min(State[42], _D0.x));
    State[47] = State[46];
    State[41] = 0; State[42] = 0; State[43] = 0; State[44] = 0; State[45] = 0; State[46] = 0; State[54] = 0;
    State[48] = 0xFFFFFFFFu; State[49] = 0xFFFFFFFFu; State[50] = 0xFFFFFFFFu;
    State[51] = 0; State[52] = 0; State[53] = 0;
}

// over the liquid, with the last substep's neighbour lists (still in the same order: the next sort is the next substep's or the
// surface's). A sleeper's velocity is zero, so it makes nothing (Asleep is not read: it would be a ninth buffer).
[numthreads(GROUP, 1, 1)]
void DiffuseEmit(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count() || (_D0.w & 1) == 0) return;
    float3 vi = Vel[i].xyz;
    float Ik = Clamp01Range(0.5 * dot(vi, vi), DIFF_K_MIN, DIFF_K_MAX);
    if (!(Ik > 0)) return;
    float4 pi = Pos[i];
    Liquid li = Liquids[min(asuint(pi.w), max(_U3.w, 1u) - 1)];
    float amount = li.d.x;
    if (!(amount > 0)) return;   // its liquid makes no foam
    float h = _F0.x, invH = 1 / h;
    float4 ni4 = Nrm[i];
    float nl = length(ni4.xyz);
    bool surf = ni4.w == 0 && nl > DIFF_NRM_SURF;
    float3 ni = surf ? -ni4.xyz / nl : float3(0, 0, 0);   // outward (Nrm sums the gradients towards the neighbours: inward)
    float vdiff = 0, kappa = 0;
    uint cnt = NbCount[i], base = i * MAXNB;
    for (uint q = 0; q < cnt; q++)
    {
        uint j = Nbr[base + q];
        float3 d = pi.xyz - Pos[j].xyz;
        float r = length(d);
        if (!(r > 1e-9) || r >= h) continue;
        float w = 1 - r * invH;
        float3 xij = d / r;
        float3 vij = vi - Vel[j].xyz;
        float vl = length(vij);
        // trapped air (eq. 2): relative speed, most for particles moving towards each other
        if (vl > 1e-6) vdiff += vl * (1 - dot(vij / vl, xij)) * w;
        // wave crest (eqs. 4 to 6): how far the surface normals spread, from the neighbours behind the surface only (convex)
        if (surf)
        {
            float4 nj4 = Nrm[j];
            float njl = length(nj4.xyz);
            if (nj4.w == 0 && njl > DIFF_NRM_SURF && dot(-xij, ni) < 0) kappa += (1 - dot(ni, -nj4.xyz / njl)) * w;
        }
    }
    float sp = length(vi);
    float dvn = surf && sp > 1e-6 && dot(vi / sp, ni) >= 0.6 ? 1 : 0;   // eq. 7: moving outward
    float Ita = Clamp01Range(vdiff, DIFF_TA_MIN, DIFF_TA_MAX);
    float Iwc = Clamp01Range(kappa * dvn, DIFF_WC_MIN, DIFF_WC_MAX);
    float nd = Ik * (DIFF_KTA * Ita + DIFF_KWC * Iwc) * _D1.y * _D1.x * amount;   // eq. 8
    uint seed = Mix(i * 9781u + _D0.y * 6271u + 17u);
    uint n = (uint)floor(nd) + (Rand01(seed) < frac(nd) ? 1u : 0u);
    n = min(n, (uint)DIFF_MAX_EACH);
    if (n == 0) return;
    InterlockedAdd(State[54], 1);
    uint first;
    InterlockedAdd(State[41], n, first);
    uint budget = State[55];
    if (first >= budget) return;   // the frame's budget is spent (or the ring is full)
    n = min(n, budget - first);
    // Fig. 3: uniformly in the cylinder of radius r_V (half a spacing) from the particle to where it goes next (x + h v^, h up to
    // |v| dt): ahead of it, as the paper. A crest moving out of the liquid puts them past the surface, so they start as spray. (Sampled
    // behind it, along the path just taken, they started inside the liquid: always foam or bubbles, and no spray at all.) Each is
    // swept from the fluid particle against the shapes, so none starts inside a wall or past it.
    uint mask = li.c.x, cells = _U2.x * _U2.y * _U2.z;
    float3 ax = sp > 1e-6 ? vi / sp : float3(0, 1, 0);
    float3 tt = abs(ax.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0);
    float3 e1 = normalize(cross(ax, tt)), e2 = cross(ax, e1);
    // bigger potentials, longer lived (the paper's clusters): its liquid's FoamLifetime x 0.5 to 4/3
    float life = lerp(_D1.z, _D1.w, saturate(Ik * max(Ita, Iwc))) * max(li.d.z, 0.1);
    float rv = 0.5 * _F3.w, path = sp * _D1.y;
    uint head = State[40];
    for (uint k = 0; k < n; k++)
    {
        uint s1 = Mix(seed + (k + 1) * 0x9E3779B9u), s2 = Mix(s1 ^ 0x85ebca6bu), s3 = Mix(s2 ^ 0xc2b2ae35u);
        float rr = sqrt(Rand01(s1)), th = Rand01(s2) * 6.2831853, hh = Rand01(s3);
        float3 radial = cos(th) * e1 + sin(th) * e2;
        float3 x = pi.xyz + radial * (rv * rr) + ax * (hh * path);
        if (mask != 0)
        {
            float3 back = pi.xyz - x; float bl = length(back);
            float3 prefer = bl > 1e-6 ? back / bl : 0;
            if (cells > 0)
            {
                uint ci = ShapeCellOf(x);
                uint s0 = ShapeGrid[ci], en = ShapeGrid[ci + 1];
                for (uint q2 = s0; q2 < en; q2++) PushShape(x, pi.xyz, Shapes[ShapeGrid[cells + 1 + q2]], mask, prefer);
            }
            for (uint gq = 0; gq < _U0.w; gq++) PushShape(x, pi.xyz, Shapes[GlobalShapes[gq]], mask, prefer);
        }
        // (the paper adds the radial offset itself as a velocity, about 1 cm/s here; scaled to the speed instead, so splashes fan out)
        float3 v = vi + radial * (rr * 0.15 * sp);
        uint slot = (head + first + k) % _D0.x;
        DPos[slot] = float4(x, life);
        DVel[slot] = float4(v, pi.w);
    }
}

[numthreads(1, 1, 1)]
void DiffuseHead(uint3 id : SV_DispatchThreadID)
{
    State[40] = (State[40] + min(State[41], State[55])) % _D0.x;
}

// over every slot: classify, move, collide, age
[numthreads(GROUP, 1, 1)]
void DiffuseUpdate(uint3 id : SV_DispatchThreadID)
{
    uint s = id.x;
    if (s >= _D0.x) return;
    float4 x = DPos[s];
    if (!(x.w > 0)) return;
    float4 vv = DVel[s];
    float3 p = x.xyz, v = vv.xyz;
    uint src = min(asuint(vv.w), max(_U3.w, 1u) - 1);
    Liquid l = Liquids[src];
    float dt = _D1.y;
    float h2 = _F0.y;
    // the fluid around it, from the last sort's grid (the liquid has moved at most a substep since; no cell check, so a rare hash
    // clash counts a cell twice)
    int3 c = CellOf(p);
    float ws = 0; float3 wv = 0; uint nf = 0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        uint b, e; CellRange(HashCell(c + int3(dx, dy, dz)), b, e);
        for (uint j = b; j < e; j++)
        {
            float3 d = p - Pos[j].xyz;
            float r2 = dot(d, d);
            if (r2 >= h2) continue;
            float k = Poly6(r2);
            ws += k; wv += Vel[j].xyz * k; nf++;
        }
    }
    uint type = nf < DIFF_SPRAY ? 0u : (nf > DIFF_BUBBLE ? 2u : 1u);
    float3 g = _Grav.xyz * l.b.y;
    float3 vf = ws > 0 ? wv / ws : v;
    float age = dt;
    if (type == 0) { v += g * dt; age *= 0.1; }                       // spray: ballistic
    else if (type == 1) v = vf;                                         // foam: the liquid's mean velocity
    else
    {
        v += -DIFF_BUOYANCY * g * dt;                                   // a bubble: buoyancy, and dragged along (k_d per 50th s)
        v += (vf - v) * (1 - pow(abs(1 - DIFF_DRAG), dt / 0.02));
        age *= 0.1;
    }
    float3 x0 = p;
    p += v * dt;
    uint mask = l.c.x;
    float3 before = p;
    float3 back = x0 - p; float bl = length(back);
    float3 prefer = bl > 1e-6 ? back / bl : 0;
    uint cells = _U2.x * _U2.y * _U2.z;
    if (cells > 0)
    {
        uint ci = ShapeCellOf(p);
        uint s0 = ShapeGrid[ci], en = ShapeGrid[ci + 1];
        for (uint q2 = s0; q2 < en; q2++) PushShape(p, x0, Shapes[ShapeGrid[cells + 1 + q2]], mask, prefer);
    }
    for (uint gq = 0; gq < _U0.w; gq++) PushShape(p, x0, Shapes[GlobalShapes[gq]], mask, prefer);
    if (any(p != before))
    {
        float3 n = p - before; float nl = length(n);
        if (nl > 1e-9) { n /= nl; float vn = dot(v, n); if (vn < 0) v -= n * vn; v *= 0.5; }
        if (type == 0) age = 2 * dt;   // spray that lands on a dry surface splatters and goes in its lifetime / 2
    }
    float life = x.w - age;
    if (any(p < _KMin.xyz) || any(p > _KMax.xyz) || Bad(p) || Bad(v)) life = 0;
    DPos[s] = float4(p, life);
    DVel[s] = float4(v, vv.w);
    DInfo[s] = type;
    if (life > 0)
    {
        InterlockedAdd(State[42], 1);
        InterlockedAdd(State[43 + type], 1);
        InterlockedMin(State[48], OrderedUint(p.x)); InterlockedMin(State[49], OrderedUint(p.y)); InterlockedMin(State[50], OrderedUint(p.z));
        InterlockedMax(State[51], OrderedUint(p.x)); InterlockedMax(State[52], OrderedUint(p.y)); InterlockedMax(State[53], OrderedUint(p.z));
    }
}

// each live diffuse particle: a disc of 7 vertices facing the head, as GasDraw (whiter than its liquid; spray small and sharp,
// foam bigger, bubbles small and faint; fading over its last half second)
[numthreads(GROUP, 1, 1)]
void DiffuseDraw(uint3 id : SV_DispatchThreadID)
{
    uint s = id.x;
    if (s >= _D0.x) return;
    float4 x = DPos[s];
    if (!(x.w > 0)) return;
    uint type = min(DInfo[s], 2u);
    uint src = min(asuint(DVel[s].w), max(_U3.w, 1u) - 1);
    float4 col = Liquids[src].color;
    float size = (type == 0 ? 0.35 : (type == 1 ? 0.6 : 0.3)) * _F3.w;
    float alpha = (type == 0 ? 0.8 : (type == 1 ? 0.7 : 0.35)) * saturate(x.w / 0.5);
    float3 rgb = lerp(col.rgb, float3(1, 1, 1), 0.85);
    uint d;
    InterlockedAdd(State[46], 1, d);
    if (d >= _D0.x) return;
    float rad = 0.5 * size;
    if (_S1.z > 0.5)
    {
        // 0.27.0: a quad for the smoke shader (as GasDrawQuads: no shading through the smoke, its own random number from its slot)
        uint h = s * 747796405u + 2891336453u;
        h = ((h >> ((h >> 28) + 4)) ^ h) * 277803737u;
        h = (h >> 22) ^ h;
        float seed = (h & 0xFFFFFF) / 16777216.0;
        uint qb = d * 4, qo = qb * 56;
        [unroll] for (uint q = 0; q < 4; q++)
        {
            float2 corner = float2(q == 1 || q == 2 ? 1 : -1, q >= 2 ? 1 : -1);
            uint oq = qo + q * 56;
            DVerts.Store3(oq, asuint(x.xyz));
            DVerts.Store3(oq + 12, asuint(float3(rad, 1, 1)));
            DVerts.Store4(oq + 24, asuint(float4(rgb, alpha)));
            DVerts.Store4(oq + 40, asuint(float4(corner, seed, 0)));
        }
        DIndices.Store3(d * 24, uint3(qb, qb + 1, qb + 2));
        DIndices.Store3(d * 24 + 12, uint3(qb, qb + 2, qb + 3));
        return;
    }
    float3 f = _G1.xyz - x.xyz; float fl = length(f);
    f = fl > 1e-5 ? f / fl : float3(0, 0, 1);
    float3 r = cross(float3(0, 1, 0), f);
    r = dot(r, r) > 1e-8 ? normalize(r) : float3(1, 0, 0);
    float3 u = cross(f, r);
    uint vb = d * 7, o = vb * 56;
    DVerts.Store3(o, asuint(x.xyz));
    DVerts.Store3(o + 12, asuint(f));
    DVerts.Store4(o + 24, asuint(float4(rgb, alpha)));
    [unroll] for (uint k = 0; k < 6; k++)
    {
        float a = k * 1.0471976;
        uint ok = o + (k + 1) * 56;
        DVerts.Store3(ok, asuint(x.xyz + (r * cos(a) + u * sin(a)) * rad));
        DVerts.Store3(ok + 12, asuint(f));
        DVerts.Store4(ok + 24, asuint(float4(rgb, 0)));
        DIndices.Store3((d * 18 + k * 3) * 4, uint3(vb, vb + 1 + k, vb + 1 + (k + 1) % 6));
    }
}

// the indices past this frame's particles (up to the last frame's) become degenerate (0.27.0: 6 a quad, 18 a disc)
[numthreads(GROUP, 1, 1)]
void DiffuseTail(uint3 id : SV_DispatchThreadID)
{
    uint per = _S1.z > 0.5 ? 6 : 18;
    uint now = min(State[46], _D0.x) * per, was = min(State[47], _D0.x) * per;
    uint k = now + id.x;
    if (k < was) DIndices.Store(k * 4, 0);
}

// ------------------------------------------------------------------------------------------- 0.27.0: the thick liquid's viscosity
// Weiler, Koschier, Brand, Bender, "A Physically Consistent Implicit Viscosity Solver for SPH Fluids" (Eurographics 2018), as in
// SPlisHSPlasH's Viscosity_Weiler2018 (MIT licence; written from its description, not copied). On the slow substeps, for the
// liquids with a kinematic viscosity nu (Liquid.d.y: ThickLiquid on), the new velocities v solve (I - dt nu L) v = v*, with the SPH
// Laplacian
//   (L v)_i = 10 sum_j (m_j / rho_j) (rho0 / rho_i) ((v_i - v_j) . x_ij) / (|x_ij|² + 0.01 h²) grad W_ij
// (10 = 2 (d + 2) in 3D). A pair's force is along x_ij, so it conserves linear and angular momentum, and a rigid motion has no
// v_ij . x_ij: a spinning blob keeps spinning. With number densities n and the spiky gradient grad W_ij = x_ij _F0.w (h - r)² / r:
//   (A p)_i = p_i + sum_j c_ij x_ij (x_ij . (p_i - p_j)),  c_ij = 10 dt nu_ij n0 (-_F0.w) (h - r)² / (n_i n_j r (r² + 0.01 h²)) >= 0,
// symmetric and positive definite. nu_ij is the pair's harmonic mean (0 next to water: no coupling to it). An asleep particle is held
// at 0, a wall for its thick neighbours. Conjugate gradient with the 3x3 block-Jacobi preconditioner, warm started with half the last
// solve's change (IvGuess, kept through the sorts), until |r| <= tol |b| (plus a floor of 0.1 mm/s a particle). Each reduction is
// the groups' partial sums, then one group; nothing waits for the CPU, and once converged the remaining iterations do nothing.
// (SoftbodyRnD prototyped and measured it: RND-REPORT.md, section 6.)
#define IV_MIN_DENSITY 0.25

groupshared float _ivA[GROUP], _ivB[GROUP];

float IvNu(uint src) { return Liquids[min(src, max(_U3.w, 1u) - 1)].d.y; }
float IvDensity(uint i) { return max(Rho[i], IV_MIN_DENSITY * _F1.x); }

float IvPair(float r2, float nui, float nuj, float ni, float nj)
{
    if (!(nui > 0 && nuj > 0)) return 0;
    float r = sqrt(r2);
    if (!(r > 1e-9) || r >= _F0.x) return 0;
    float t = _F0.x - r;
    float nu = 2 * nui * nuj / (nui + nuj);
    return 10 * _I0.x * nu * _F1.x * (-_F0.w) * t * t / (ni * nj * r * (r2 + _I0.z));
}

float3 IvPrecond(uint i, float3 r)
{
    float4 a = IvPinv[2 * i], b = IvPinv[2 * i + 1];
    return float3(a.x * r.x + a.w * r.y + b.x * r.z, a.w * r.x + a.y * r.y + b.y * r.z, b.x * r.x + b.y * r.y + a.z * r.z);
}

// a group's two partial sums into IvPartial[2 g] and [2 g + 1] (every thread of the group calls it)
void IvReduce(uint tid, uint gid, float a, float b)
{
    _ivA[tid] = a; _ivB[tid] = b;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = GROUP / 2; s > 0; s >>= 1)
    {
        if (tid < s) { _ivA[tid] += _ivA[tid + s]; _ivB[tid] += _ivB[tid + s]; }
        GroupMemoryBarrierWithGroupSync();
    }
    if (tid == 0) { IvPartial[2 * gid] = _ivA[0]; IvPartial[2 * gid + 1] = _ivB[0]; }
}

// one group: the sums of every group's partials, into _ivA[0] and _ivB[0]
void IvSumPartials(uint tid)
{
    uint groups = (Count() + GROUP - 1) / GROUP;
    float a = 0, b = 0;
    for (uint k = tid; k < groups; k += GROUP) { a += IvPartial[2 * k]; b += IvPartial[2 * k + 1]; }
    _ivA[tid] = a; _ivB[tid] = b;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = GROUP / 2; s > 0; s >>= 1)
    {
        if (tid < s) { _ivA[tid] += _ivA[tid + s]; _ivB[tid] += _ivB[tid + s]; }
        GroupMemoryBarrierWithGroupSync();
    }
}

// State 60 the most iterations of a solve this frame, 61 their sum, 62 the solves, 63 the solves that did not converge
[numthreads(1, 1, 1)]
void IvFrameBegin(uint3 id : SV_DispatchThreadID)
{
    State[60] = 0; State[61] = 0; State[62] = 0; State[63] = 0;
}

// each row's kind, and the start: b (this substep's velocity, in Delta) plus a share of the last solve's change
[numthreads(GROUP, 1, 1)]
void IvBegin(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    if (Asleep(i)) { IvX[i] = 0; IvP[i] = float4(0, 0, 0, -1); return; }
    bool thick = IvNu(asuint(Pred[i].w)) > 0;
    float3 x = Delta[i].xyz + (thick ? _I0.w * IvGuess[i].xyz : 0);
    IvX[i] = float4(x, 0);
    IvP[i] = float4(x, thick ? 1 : 0);
}

// the inverse of each thick row's diagonal block, I + sum_j c_ij x_ij x_ij^T (the block-Jacobi preconditioner)
[numthreads(GROUP, 1, 1)]
void IvDiagonal(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count()) return;
    float3 dg = float3(1, 1, 1), od = 0;   // the diagonal (xx, yy, zz) and off it (xy, xz, yz)
    if (IvP[i].w > 0)
    {
        float4 pi = Pred[i];
        float nui = IvNu(asuint(pi.w)), ni = IvDensity(i);
        uint cnt = NbCount[i], base = i * MAXNB;
        for (uint q = 0; q < cnt; q++)
        {
            uint j = Nbr[base + q];
            float4 pj = Pred[j];
            float3 x = pi.xyz - pj.xyz;
            float c = IvPair(dot(x, x), nui, IvNu(asuint(pj.w)), ni, IvDensity(j));
            dg += c * x * x;
            od += c * float3(x.x * x.y, x.x * x.z, x.y * x.z);
        }
    }
    float a00 = dg.y * dg.z - od.z * od.z, a01 = od.y * od.z - od.x * dg.z, a02 = od.x * od.z - od.y * dg.y;
    float a11 = dg.x * dg.z - od.y * od.y, a12 = od.x * od.y - dg.x * od.z, a22 = dg.x * dg.y - od.x * od.x;
    float det = dg.x * a00 + od.x * a01 + od.y * a02;
    float inv = det > 1e-20 ? 1 / det : 0;
    IvPinv[2 * i] = float4(a00, a11, a22, a01) * inv;
    IvPinv[2 * i + 1] = float4(a02, a12, 0, 0) * inv;
}

[numthreads(1, 1, 1)]
void IvStart(uint3 id : SV_DispatchThreadID)
{
    IvScalars[0] = 0; IvScalars[1] = 0; IvScalars[2] = 0; IvScalars[3] = 0; IvScalars[4] = 0; IvScalars[5] = 0; IvScalars[6] = 0;
}

// q = A p, and each group's sum of p . q (a converged solve skips the work)
[numthreads(GROUP, 1, 1)]
void IvMatVec(uint3 id : SV_DispatchThreadID, uint3 tid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint i = id.x;
    float pq = 0;
    if (IvDone[5] == 0 && i < Count())
    {
        float4 p = IvP[i];
        float3 q = 0;
        if (p.w > 0)
        {
            q = p.xyz;
            float4 pi = Pred[i];
            float nui = IvNu(asuint(pi.w)), ni = IvDensity(i);
            uint cnt = NbCount[i], base = i * MAXNB;
            for (uint k = 0; k < cnt; k++)
            {
                uint j = Nbr[base + k];
                float4 pj = Pred[j];
                float3 x = pi.xyz - pj.xyz;
                float c = IvPair(dot(x, x), nui, IvNu(asuint(pj.w)), ni, IvDensity(j));
                if (c > 0) q += x * (c * dot(x, p.xyz - IvP[j].xyz));
            }
            pq = dot(p.xyz, q);
        }
        IvQ[i] = float4(q, 0);
    }
    IvReduce(tid.x, gid.x, pq, 0);
}

// the start's residual r = b - A x0, then z = M r and p = z, and each group's r . z and |b|² (thick rows only)
[numthreads(GROUP, 1, 1)]
void IvResidual(uint3 id : SV_DispatchThreadID, uint3 tid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint i = id.x;
    float rz = 0, bb = 0;
    if (i < Count())
    {
        float4 p = IvP[i];
        if (p.w > 0)
        {
            float3 b = Delta[i].xyz;
            float3 r = b - IvQ[i].xyz;
            float3 z = IvPrecond(i, r);
            IvR[i] = float4(r, 0);
            IvP[i] = float4(z, p.w);
            rz = dot(r, z); bb = dot(b, b);
        }
        else { IvR[i] = 0; IvP[i] = float4(0, 0, 0, p.w); }
    }
    IvReduce(tid.x, gid.x, rz, bb);
}

[numthreads(GROUP, 1, 1)]
void IvInit(uint3 tid : SV_GroupThreadID)
{
    IvSumPartials(tid.x);
    if (tid.x == 0)
    {
        IvScalars[0] = _ivA[0]; IvScalars[3] = _ivB[0];
        IvScalars[5] = _ivA[0] > 1e-30 ? 0 : 1;
    }
}

[numthreads(GROUP, 1, 1)]
void IvAlpha(uint3 tid : SV_GroupThreadID)
{
    IvSumPartials(tid.x);
    if (tid.x == 0 && IvScalars[5] == 0)
    {
        float pq = _ivA[0];
        if (pq > 1e-30) IvScalars[1] = IvScalars[0] / pq;
        else { IvScalars[1] = 0; IvScalars[5] = 1; }
    }
}

// x += alpha p, r -= alpha q, and each group's r . z (z = M r) and r . r
[numthreads(GROUP, 1, 1)]
void IvUpdate(uint3 id : SV_DispatchThreadID, uint3 tid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint i = id.x;
    float rz = 0, rr = 0;
    if (IvDone[5] == 0 && i < Count())
    {
        float4 p = IvP[i];
        if (p.w > 0)
        {
            float alpha = IvDone[1];
            float3 r = IvR[i].xyz - alpha * IvQ[i].xyz;
            IvX[i] = float4(IvX[i].xyz + alpha * p.xyz, 0);
            IvR[i] = float4(r, 0);
            rz = dot(r, IvPrecond(i, r)); rr = dot(r, r);
        }
    }
    IvReduce(tid.x, gid.x, rz, rr);
}

[numthreads(GROUP, 1, 1)]
void IvBeta(uint3 tid : SV_GroupThreadID)
{
    IvSumPartials(tid.x);
    if (tid.x == 0 && IvScalars[5] == 0)
    {
        float rz = _ivA[0], rr = _ivB[0];
        IvScalars[2] = IvScalars[0] > 1e-30 ? rz / IvScalars[0] : 0;
        IvScalars[0] = rz; IvScalars[4] = rr;
        IvScalars[6] += 1;
        float floorSq = 1e-8 * (float)Count();   // (0.1 mm/s)² a particle
        if (rr <= _I0.y * _I0.y * IvScalars[3] + floorSq || IvScalars[6] >= (float)_I1.y) IvScalars[5] = 1;
    }
}

// p = M r + beta p
[numthreads(GROUP, 1, 1)]
void IvDirection(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= Count() || IvDone[5] != 0) return;
    float4 p = IvP[i];
    if (!(p.w > 0)) return;
    IvP[i] = float4(IvPrecond(i, IvR[i].xyz) + IvDone[2] * p.xyz, p.w);
}

// the solution into Delta (thick rows), its change kept for the next solve's start; past the particles the change is cleared (new
// particles start from none). Thread 0 counts the solve into State 60 to 63.
[numthreads(GROUP, 1, 1)]
void IvFinish(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i == 0)
    {
        uint it = (uint)IvScalars[6];
        InterlockedMax(State[60], it); InterlockedAdd(State[61], it); InterlockedAdd(State[62], 1);
        if (IvScalars[5] == 0 || it >= _I1.y) InterlockedAdd(State[63], 1);
    }
    if (i >= _U0.x) return;
    if (i >= Count()) { IvGuess[i] = 0; return; }
    float4 p = IvP[i];
    if (p.w > 0)
    {
        float4 d = Delta[i];
        float3 x = IvX[i].xyz;
        IvGuess[i] = float4(x - d.xyz, 0);
        Delta[i] = float4(x, d.w);
    }
    else IvGuess[i] = 0;
}

// the warm start follows its particle through the sort
[numthreads(GROUP, 1, 1)]
void ReorderIvGuess(uint3 id : SV_DispatchThreadID)
{
    uint j = id.x;
    if (j >= State[11]) return;
    IvGuess2[j] = IvGuess[Order[j]];
}

