// Live bounce light (GI) for the renderer fork, compiled at run time (cs_5_0) like the fluid's kernels.
//
// What is around the head is kept as voxels (albedo, normal, emission: the voxelize shader writes them a slab at a time, Resolve
// averages them). Inject lights each voxel: the world's lights (shadowed ones trace a ray through the voxels), plus the bounce light
// already in the probes (so light bounces again each round). TraceProbes shoots rays from a grid of probes through the voxels (a
// miss sees the sky; a hit sees the voxel's light and its glow) and keeps an L1 spherical harmonic per probe; glowing voxels also
// get shadow rays aimed at them (see MaxGlowSamples), so a small bright lamp lights the probes steadily. WriteVolume lays the probes out the way
// Resonite's own lit shaders read a light probe proxy volume (unity_ProbeVolumeSH), so every lit material gets the bounce light
// per pixel without a shader change (GiProbeTest in SoftbodyShaders showed that path works from a MaterialPropertyBlock).
//
// Units are the shaders': a light adds albedo * colour * N.L * attenuation; a probe holds irradiance / pi as a0 + a1.n, which is
// what ShadeSH9 returns (a0 = mean radiance over the rays, a1 = 2 * mean(radiance * direction)).

cbuffer GiParams : register(b0)
{
    float4 VoxOrigin;      // xyz: the world corner of voxel (0,0,0); w: voxel size (m)
    int4   VoxDim;         // xyz: voxels; w: all of them
    int4   SlabOrigin;     // xyz: the first voxel of the slab (Resolve, ClearAccum, BuildBricks); w: unused
    int4   SlabDim;        // xyz: the slab's voxels; w: all of them
    float4 ProbeOrigin;    // xyz: the world corner of probe cell (0,0,0) (probe i sits at ProbeOrigin + (i + 0.5) * spacing); w: spacing
    int4   ProbeDim;       // xyz: probes; w: all of them
    int4   ProbeRange;     // x: the first probe traced this frame; y: how many; z: frame; w: unused
    int4   InjectRange;    // x: the first voxel slice (z) lit this frame; y: slices; z: lights; w: unused
    int4   Shift;          // xyz: the new grid's cell c was the old grid's cell c + Shift; w: unused
    float4 Misc;           // x: the least a probe follows a new result; y: emission scale; z: back-face share that makes a probe
                           //    invalid; w: how far a probe ray goes (m)
    float4 Rot0, Rot1, Rot2;   // this frame's random rotation of the rays (rows)
    float4 SkyRad[7];      // the sky's radiance, as the polynomial ShadeSH9 evaluates (Ar Ag Ab Br Bg Bb C)
    float4 SkyVal[7];      // the world's plain ambient as its shader constants (unity_SHAr ... unity_SHC)
    float4 WinOrigin;      // xyz: the world corner of the window's probe cell (0,0,0); w: edge fade (probes)
    int4   WinDim;         // xyz: the window's probes (the volume texture is 4 * x wide); w: unused
    int4   Glow;           // x: shadow rays a probe aims at glowing voxels (0: none, the rays alone see the glow); y: the most
                           //    glowing bricks a probe weighs (a random run of the list when there are more); zw: unused
};

struct GiLight
{
    float4 PosRange;   // xyz: position; w: range
    float4 DirType;    // xyz: the way it shines; w: 0 directional, 1 point, 2 spot
    float4 Color;      // rgb: linear colour * intensity, as the shaders get it; w: 1 when it casts shadows
    float4 Spot;       // x: cos of the outer angle; y: cos of where the fall-off starts; z: 1 / range^2; w: unused
};

StructuredBuffer<uint> Accum;          // the voxelize shader's sums for the slab: count, albedo rgb, emission rgb, normal xyz
RWStructuredBuffer<uint> AccumRW;
RWStructuredBuffer<uint4> Voxels;      // albedo rgba8 (a: 255 when filled), normal snorm8 xyz + coherence, emission rgb9e5, the
                                       // light it sends back (rgb9e5; its glow is not in it: the rays add that, see TraceProbes)
StructuredBuffer<uint4> VoxelsIn;      // the old grid (ShiftVoxels)
RWStructuredBuffer<uint> Bricks;       // per 4x4x4 voxels: 1 when any is filled
RWStructuredBuffer<float4> BrickGlow;  // per 4x4x4 voxels: xyz the glow-weighted world centre of its glowing voxels, w their glow
                                       // (luminance) added up; all 0 when it hardly glows (GlowMin)
RWStructuredBuffer<uint> GlowList;     // [0]: how many bricks glow; [1 + i]: the i-th one's index (in no set order)
RWStructuredBuffer<float4> Probes;     // per probe: red (a1.x a1.y a1.z a0), green, blue, meta (valid, luminance, rounds, 0)
StructuredBuffer<float4> ProbesIn;     // the old grid (ShiftProbes)
StructuredBuffer<GiLight> Lights;
RWTexture3D<float4> Volume;            // SHAr | SHAg | SHAb | occlusion, each WinDim.x wide

// ------------------------------------------------------------------------------------------------------------------- packing

uint PackUnorm4(float4 v)
{
    uint4 u = (uint4)round(saturate(v) * 255.0);
    return u.x | (u.y << 8) | (u.z << 16) | (u.w << 24);
}

float4 UnpackUnorm4(uint p)
{
    return float4(p & 255u, (p >> 8) & 255u, (p >> 16) & 255u, p >> 24) / 255.0;
}

uint PackNormal(float3 n, float coherence) { return PackUnorm4(float4(n * 0.5 + 0.5, coherence)); }

float4 UnpackNormal(uint p)
{
    float4 v = UnpackUnorm4(p);
    return float4(v.xyz * 2.0 - 1.0, v.w);
}

// shared-exponent HDR colour (9 bits each, 5 bits exponent)
uint PackRGB9E5(float3 rgb)
{
    rgb = clamp(rgb, 0.0, 65408.0);
    float m = max(rgb.r, max(rgb.g, rgb.b));
    if (m < 1e-7) return 0;
    int e = max(-16, (int)floor(log2(m))) + 16;
    float denom = exp2((float)(e - 15 - 9));
    if ((uint)floor(m / denom + 0.5) >= 512u) { e++; denom *= 2.0; }
    uint3 q = min((uint3)floor(rgb / denom + 0.5), 511u);
    return q.r | (q.g << 9) | (q.b << 18) | ((uint)e << 27);
}

float3 UnpackRGB9E5(uint v)
{
    float scale = exp2((float)((int)(v >> 27) - 15 - 9));
    return float3(v & 511u, (v >> 9) & 511u, (v >> 18) & 511u) * scale;
}

// ------------------------------------------------------------------------------------------------------------------- indices

uint VoxIndex(int3 c) { return ((uint)c.z * (uint)VoxDim.y + (uint)c.y) * (uint)VoxDim.x + (uint)c.x; }
int3 VoxCoord(uint i) { return int3(i % (uint)VoxDim.x, (i / (uint)VoxDim.x) % (uint)VoxDim.y, i / ((uint)VoxDim.x * (uint)VoxDim.y)); }
int3 BrickDim() { return VoxDim.xyz >> 2; }
uint BrickIndex(int3 b) { int3 d = BrickDim(); return ((uint)b.z * (uint)d.y + (uint)b.y) * (uint)d.x + (uint)b.x; }
int3 BrickCoord(uint i) { int3 d = BrickDim(); return int3(i % (uint)d.x, (i / (uint)d.x) % (uint)d.y, i / ((uint)d.x * (uint)d.y)); }
uint ProbeIndex(int3 c) { return ((uint)c.z * (uint)ProbeDim.y + (uint)c.y) * (uint)ProbeDim.x + (uint)c.x; }
int3 ProbeCoord(uint i) { return int3(i % (uint)ProbeDim.x, (i / (uint)ProbeDim.x) % (uint)ProbeDim.y, i / ((uint)ProbeDim.x * (uint)ProbeDim.y)); }
bool Filled(uint4 v) { return (v.x >> 24) != 0; }
float Lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// random numbers: a hash per step (the probes' shadow rays)
uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float Rand(inout uint s) { s = Hash(s); return (float)(s >> 8) * (1.0 / 16777216.0); }

// ------------------------------------------------------------------------------------------------------------------- sh

float3 SH9(float4 ar, float4 ag, float4 ab, float4 br, float4 bg, float4 bb, float4 c, float3 n)
{
    float4 n1 = float4(n, 1.0);
    float3 x1 = float3(dot(ar, n1), dot(ag, n1), dot(ab, n1));
    float4 vb = n.xyzz * n.yzzx;
    float3 x2 = float3(dot(br, vb), dot(bg, vb), dot(bb, vb));
    float vc = n.x * n.x - n.y * n.y;
    return x1 + x2 + c.rgb * vc;
}

float3 SkyRadiance(float3 d) { return max(0.0, SH9(SkyRad[0], SkyRad[1], SkyRad[2], SkyRad[3], SkyRad[4], SkyRad[5], SkyRad[6], d)); }
float3 SkyValue(float3 n) { return max(0.0, SH9(SkyVal[0], SkyVal[1], SkyVal[2], SkyVal[3], SkyVal[4], SkyVal[5], SkyVal[6], n)); }

// the probes' bounce light at a world position for a normal (trilinear over the valid probes around it; the sky where none is)
float3 ProbeValue(float3 p, float3 n)
{
    float3 g = (p - ProbeOrigin.xyz) / ProbeOrigin.w - 0.5;
    int3 i0 = clamp((int3)floor(g), 0, ProbeDim.xyz - 2);
    float3 t = saturate(g - (float3)i0);
    float4 r = 0, gr = 0, b = 0;
    float wsum = 0;
    [unroll] for (int k = 0; k < 8; k++)
    {
        int3 o = int3(k & 1, (k >> 1) & 1, k >> 2);
        uint pi = ProbeIndex(i0 + o);
        float4 meta = Probes[pi * 4 + 3];
        if (meta.x < 0.5) continue;
        float3 w3 = o == 1 ? t : 1.0 - t;
        float w = w3.x * w3.y * w3.z;
        r += Probes[pi * 4] * w; gr += Probes[pi * 4 + 1] * w; b += Probes[pi * 4 + 2] * w;
        wsum += w;
    }
    if (wsum < 1e-4) return SkyValue(n);
    float4 n1 = float4(n, 1.0);
    return max(0.0, float3(dot(r, n1), dot(gr, n1), dot(b, n1)) / wsum);
}

// ------------------------------------------------------------------------------------------------------------------- rays

// Walks the voxels from o along d (unit) for at most maxT metres, skipping empty 4x4x4 bricks. True when it hits a filled voxel.
bool March(float3 o, float3 d, float maxT, out uint hitVoxel, out float hitT)
{
    hitVoxel = 0; hitT = 0;
    float vs = VoxOrigin.w;
    float3 po = (o - VoxOrigin.xyz) / vs;
    float3 dd = float3(abs(d.x) < 1e-6 ? 1e-6 : d.x, abs(d.y) < 1e-6 ? 1e-6 : d.y, abs(d.z) < 1e-6 ? 1e-6 : d.z);
    float3 inv = 1.0 / dd;
    float3 t0 = -po * inv, t1 = ((float3)VoxDim.xyz - po) * inv;
    float3 tn = min(t0, t1), tf = max(t0, t1);
    float t = max(max(tn.x, tn.y), max(tn.z, 0.0));
    float tEnd = min(min(tf.x, tf.y), min(tf.z, maxT / vs));
    float3 side = float3(dd.x > 0 ? 1.0 : 0.0, dd.y > 0 ? 1.0 : 0.0, dd.z > 0 ? 1.0 : 0.0);
    [loop] for (int it = 0; it < 384 && t < tEnd; it++)
    {
        float3 p = po + dd * (t + 1e-3);
        int3 c = clamp((int3)floor(p), 0, VoxDim.xyz - 1);
        int3 b = c >> 2;
        if (Bricks[BrickIndex(b)] == 0)
        {
            float3 tb = ((float3)(b * 4) + side * 4.0 - po) * inv;
            t = max(t + 1e-3, min(min(tb.x, tb.y), tb.z));
            continue;
        }
        uint vi = VoxIndex(c);
        if (Filled(Voxels[vi])) { hitVoxel = vi; hitT = t * vs; return true; }
        float3 tv = ((float3)c + side - po) * inv;
        t = max(t + 1e-3, min(min(tv.x, tv.y), tv.z));
    }
    return false;
}

// ------------------------------------------------------------------------------------------------------------------- voxels

[numthreads(64, 1, 1)]
void ClearAccum(uint3 id : SV_DispatchThreadID)
{
    if (id.x < (uint)SlabDim.w * 10u) AccumRW[id.x] = 0;
}

[numthreads(64, 1, 1)]
void Resolve(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)SlabDim.w) return;
    int3 c = int3(id.x % (uint)SlabDim.x, (id.x / (uint)SlabDim.x) % (uint)SlabDim.y, id.x / ((uint)SlabDim.x * (uint)SlabDim.y));
    int3 g = c + SlabOrigin.xyz;
    if (any(g < 0) || any(g >= VoxDim.xyz)) return;
    uint vi = VoxIndex(g);
    uint a = id.x * 10u;
    uint count = Accum[a];
    if (count == 0) { Voxels[vi] = uint4(0, 0, 0, 0); return; }
    float inv = 1.0 / (float)count;
    float3 alb = float3(Accum[a + 1], Accum[a + 2], Accum[a + 3]) * (inv / 255.0);
    float3 emi = float3(Accum[a + 4], Accum[a + 5], Accum[a + 6]) * (inv / 256.0);
    float3 n = float3(asint(Accum[a + 7]), asint(Accum[a + 8]), asint(Accum[a + 9])) * (inv / 64.0);
    float coherence = saturate(length(n));
    n = coherence > 1e-3 ? n / coherence : float3(0, 1, 0);
    uint4 old = Voxels[vi];
    Voxels[vi] = uint4(PackUnorm4(float4(alb, 1.0)), PackNormal(n, coherence), PackRGB9E5(emi * Misc.y), Filled(old) ? old.w : 0u);
}

// A brick glows when its voxels' glow (luminance) adds up to more than this: only those get shadow rays (TraceProbes)
static const float GlowMin = 1e-3;

// the bricks over the slab (or, after a shift, all of them): filled or not, and how much they glow and where
[numthreads(64, 1, 1)]
void BuildBricks(uint3 id : SV_DispatchThreadID)
{
    int3 sd = max(1, SlabDim.xyz >> 2);
    if (id.x >= (uint)(sd.x * sd.y * sd.z)) return;
    int3 b = (SlabOrigin.xyz >> 2) + int3(id.x % (uint)sd.x, (id.x / (uint)sd.x) % (uint)sd.y, id.x / (uint)(sd.x * sd.y));
    if (any(b >= BrickDim())) return;
    uint filled = 0;
    float glow = 0;
    float3 centre = 0;
    [loop] for (int z = 0; z < 4; z++)
        [loop] for (int y = 0; y < 4; y++)
            [unroll] for (int x = 0; x < 4; x++)
            {
                int3 c = b * 4 + int3(x, y, z);
                uint4 v = Voxels[VoxIndex(c)];
                if (!Filled(v)) continue;
                filled = 1;
                float l = Lum(UnpackRGB9E5(v.z));
                glow += l;
                centre += l * (float3)c;
            }
    uint bi = BrickIndex(b);
    Bricks[bi] = filled;
    BrickGlow[bi] = glow > GlowMin ? float4(VoxOrigin.xyz + (centre / glow + 0.5) * VoxOrigin.w, glow) : 0;
}

// the list of glowing bricks, made again after BuildBricks: ClearGlow, then ListGlow over all bricks
[numthreads(1, 1, 1)]
void ClearGlow(uint3 id : SV_DispatchThreadID)
{
    GlowList[0] = 0;
}

[numthreads(64, 1, 1)]
void ListGlow(uint3 id : SV_DispatchThreadID)
{
    int3 bd = BrickDim();
    if (id.x >= (uint)(bd.x * bd.y * bd.z) || !(BrickGlow[id.x].w > 0)) return;
    uint slot;
    InterlockedAdd(GlowList[0], 1u, slot);
    GlowList[1 + slot] = id.x;
}

[numthreads(64, 1, 1)]
void Inject(uint3 id : SV_DispatchThreadID)
{
    uint slice = (uint)VoxDim.x * (uint)VoxDim.y;
    if (id.x >= slice * (uint)InjectRange.y) return;
    uint z = ((uint)InjectRange.x + id.x / slice) % (uint)VoxDim.z;
    uint r = id.x % slice;
    int3 c = int3(r % (uint)VoxDim.x, r / (uint)VoxDim.x, z);
    uint vi = VoxIndex(c);
    uint4 v = Voxels[vi];
    if (!Filled(v)) return;
    float3 alb = UnpackUnorm4(v.x).rgb;
    float4 nc = UnpackNormal(v.y);
    bool coherent = nc.w > 0.35;
    float3 nrm = normalize(nc.xyz + 1e-6);
    float vs = VoxOrigin.w;
    float3 p = VoxOrigin.xyz + ((float3)c + 0.5) * vs;
    float3 direct = 0;
    [loop] for (int li = 0; li < InjectRange.z; li++)
    {
        GiLight L = Lights[li];
        float3 toL;
        float atten = 1.0;
        float dist = 1e6;
        if (L.DirType.w < 0.5) toL = -L.DirType.xyz;
        else
        {
            float3 dl = L.PosRange.xyz - p;
            float d2 = dot(dl, dl);
            float x = d2 * L.Spot.z;
            if (x >= 1.0) continue;
            dist = sqrt(d2);
            toL = dl / max(dist, 1e-4);
            // Unity's built-in fall-off (its attenuation texture): 1 / (1 + 25 x), fading to 0 over the last part of the range
            atten = saturate((1.0 - x) * 5.0) / (1.0 + 25.0 * x);
            if (L.DirType.w > 1.5) atten *= saturate((dot(-toL, L.DirType.xyz) - L.Spot.x) / max(1e-4, L.Spot.y - L.Spot.x));
        }
        float ndl = coherent ? saturate(dot(nrm, toL)) : 0.5;
        if (ndl * atten <= 0.0) continue;
        if (L.Color.w > 0.5)
        {
            uint hv; float ht;
            float3 o = p + (coherent ? nrm : toL) * (vs * 0.87);
            if (March(o, toL, dist - vs, hv, ht)) continue;
        }
        direct += L.Color.rgb * (atten * ndl);
    }
    float3 indirect = coherent ? ProbeValue(p + nrm * (vs * 0.5), nrm) : 0.5 * (ProbeValue(p, nrm) + ProbeValue(p, -nrm));
    // the light it sends back; its glow stays apart (v.z), as the probes count it two ways (TraceProbes)
    Voxels[vi] = uint4(v.x, v.y, v.z, PackRGB9E5(alb * (direct + indirect)));
}

// ------------------------------------------------------------------------------------------------------------------- probes

groupshared float3 gsL[32];
groupshared float3 gsX[32];
groupshared float3 gsY[32];
groupshared float3 gsZ[32];
groupshared uint gsBack;
groupshared float gsLum[32];
groupshared float gsMedian;
groupshared float gsGlowW[32];
groupshared uint gsGlowPick[32];
groupshared uint gsGlowSel[8];
groupshared float gsGlowTotal;

// Fireflies: a small, very bright thing (a glowing cube across the room) is hit by one ray in 32 now and then; that one ray lit the
// whole probe for a while and left flickering coloured blobs on the walls (editor test 2026-11-14). So a ray brings at most this
// many times the light already there: the most of the probe's running value, its six neighbours' and this round's median ray. A
// rare hit then adds a quarter at most (under the fast-follow threshold); a probe right by a glowing patch (a third of its rays)
// still takes the patch's light in, and lifts its neighbours' cap, so the light spreads out over a few rounds. A glowing voxel's
// light now comes mostly through the shadow rays (MaxGlowSamples), which the cap leaves alone.
static const float FireflyRatio = 8.0;
static const float FireflyFloor = 0.01;   // (luminance; so a probe in the dark can still take light in)

// Glowing voxels as lights (next-event estimation). The cap above kept a small bright lamp across the room from flickering, but
// also took most of its light away. So each probe also aims Glow.x shadow rays at glowing voxels: it weighs the glowing bricks by
// their glow over the distance squared, picks a brick that way, a voxel in it by its glow, and a point on one of that voxel's faces
// turned to the probe (by the face's share of the voxel's outline, the outward face of a surface favoured: a flat glowing panel
// hides its voxels' other faces in their neighbours); the shadow ray says whether that point is in sight. Rays and shadow rays
// share each glow by multiple importance sampling, so a big glowing wall nearby still comes in mostly through the rays, a small
// bright lamp far away through the shadow rays, and nothing is counted twice. The power heuristic (each counts it by the square of
// its density over the sum of both squares): with the balance heuristic a rare ray that hit a small lamp still brought a quarter
// of the lamp's light in one round, and the lamp flickered 8% (offline lamp test, 2026-11-14).
static const uint MaxGlowSamples = 8;
static const float GlowNear2 = 0.25;   // (m^2) a brick nearer than half a metre weighs as if it were half a metre away

// how the shadow rays favour the faces of a voxel turned to the probe (w0: from the probe to the voxel's centre)
float3 GlowFaceBias(float3 w0, float4 nc)
{
    return nc.w > 0.35 ? 0.05 + saturate(-sign(w0) * nc.xyz) : 1.0;
}

// the shadow rays' density (per solid angle) at a point of face 'axis' of a voxel with glow l (luminance) in a brick with glow bg,
// seen from p: the voxel's chance (its brick's weight over all the probe weighed, then its share of the brick's glow) times the
// face's, spread over the face's solid angle
float GlowDensity(float3 p, float l, float4 bg, float3 bias, float3 w0, float dc2, uint axis, float total)
{
    float3 db = bg.xyz - p;
    float pv = l / (max(dot(db, db), GlowNear2) * total);
    float3 fw = abs(w0) * bias;
    float ba = axis == 0 ? bias.x : axis == 1 ? bias.y : bias.z;
    float vs = VoxOrigin.w;
    return pv * ba * dc2 / (vs * vs * max(fw.x + fw.y + fw.z, 1e-6));
}

// 32 rays per probe, one group per probe
[numthreads(32, 1, 1)]
void TraceProbes(uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
{
    uint pi = ((uint)ProbeRange.x + gid.x) % (uint)ProbeDim.w;
    int3 pc = ProbeCoord(pi);
    float3 pos = ProbeOrigin.xyz + ((float3)pc + 0.5) * ProbeOrigin.w;
    float vs = VoxOrigin.w;
    uint frameSeed = Hash((uint)ProbeRange.z * 0x9e3779b9u + 0x632be5abu);
    uint seed = Hash(pi * 32u + gi + frameSeed);
    if (gi == 0) gsBack = 0;

    // the glowing bricks (see MaxGlowSamples): each lane weighs a share of them and keeps one by its weight
    uint glowing = Glow.x > 0 ? GlowList[0] : 0u;
    uint K = glowing > 0 ? min((uint)Glow.x, MaxGlowSamples) : 0u;
    uint weighed = min(glowing, (uint)max(Glow.y, 32));
    if (K > 0)
    {
        uint start = weighed < glowing ? Hash(pi ^ frameSeed) % glowing : 0u;
        float wl = 0;
        uint pick = 0;
        [loop] for (uint i = gi; i < weighed; i += 32)
        {
            uint e = start + i;
            if (e >= glowing) e -= glowing;
            uint bi = GlowList[1 + e];
            float4 bg = BrickGlow[bi];
            float3 db = bg.xyz - pos;
            float wb = bg.w / max(dot(db, db), GlowNear2);
            wl += wb;
            if (Rand(seed) * wl < wb) pick = bi;
        }
        gsGlowW[gi] = wl;
        gsGlowPick[gi] = pick;
    }
    GroupMemoryBarrierWithGroupSync();
    if (gi < K)
    {
        // a pick from all lanes' (a lane's by its weight): a brick by its weight over all the probe weighed
        float run = 0;
        uint sel = 0;
        [loop] for (uint ln = 0; ln < 32; ln++)
        {
            float wm = gsGlowW[ln];
            if (!(wm > 0)) continue;
            run += wm;
            if (Rand(seed) * run < wm) sel = gsGlowPick[ln];
        }
        gsGlowSel[gi] = sel;
        // (a random run of the list stands for all of it)
        if (gi == 0) gsGlowTotal = run * ((float)glowing / (float)max(weighed, 1u));
    }
    GroupMemoryBarrierWithGroupSync();
    float total = K > 0 ? gsGlowTotal : 0.0;
    float misK = 12.566371 * (float)K;   // the shadow rays' density against the rays' (32 over 4 pi each): 4 pi K

    // a Fibonacci sphere, turned a new way each frame
    float z = 1.0 - (2.0 * gi + 1.0) / 32.0;
    float rr = sqrt(saturate(1.0 - z * z));
    float phi = gi * 2.39996323;
    float3 d0 = float3(rr * cos(phi), rr * sin(phi), z);
    float3 d = normalize(float3(dot(Rot0.xyz, d0), dot(Rot1.xyz, d0), dot(Rot2.xyz, d0)));

    float3 L;
    uint hv; float ht;
    if (March(pos, d, Misc.w, hv, ht))
    {
        uint4 v = Voxels[hv];
        float4 nc = UnpackNormal(v.y);
        if (nc.w > 0.35 && dot(nc.xyz, d) > 0.1) { InterlockedAdd(gsBack, 1u); L = 0; }   // the back of a surface
        else
        {
            float3 glow = UnpackRGB9E5(v.z);
            float lg = Lum(glow);
            if (K > 0 && lg > 0)
            {
                // its glow by the rays' share (all of it when the shadow rays could not have picked it: a brick that hardly glows)
                int3 c = VoxCoord(hv);
                float4 bg = BrickGlow[BrickIndex(c >> 2)];
                if (bg.w > 0)
                {
                    float3 ctr = VoxOrigin.xyz + ((float3)c + 0.5) * vs;
                    float3 w0 = ctr - pos;
                    float dc2 = max(dot(w0, w0), 1e-8);
                    w0 *= rsqrt(dc2);
                    float3 ah = abs(pos + d * ht - ctr);   // the face the ray came in through: the one it is on
                    uint axis = ah.x >= ah.y && ah.x >= ah.z ? 0u : (ah.y >= ah.z ? 1u : 2u);
                    float qr = misK * GlowDensity(pos, lg, bg, GlowFaceBias(w0, nc), w0, dc2, axis, total);   // (32 rays: 32 against qr)
                    glow *= 1024.0 / (1024.0 + qr * qr);
                }
            }
            L = UnpackRGB9E5(v.w) + glow;
        }
    }
    else L = SkyRadiance(d);

    // the firefly cap (see FireflyRatio): this round's median ray, then the cap against it and the probe's running value
    float lumRay = dot(L, float3(0.2126, 0.7152, 0.0722));
    gsLum[gi] = lumRay;
    GroupMemoryBarrierWithGroupSync();
    uint rank = 0;
    [unroll] for (uint k = 0; k < 32; k++) { float o = gsLum[k]; rank += (o < lumRay || (o == lumRay && k < gi)) ? 1u : 0u; }
    if (rank == 16) gsMedian = lumRay;
    GroupMemoryBarrierWithGroupSync();
    float4 metaNow = Probes[pi * 4 + 3];
    float had = metaNow.x > 0.5 && metaNow.z >= 1.0 ? metaNow.y : 0.0;
    [unroll] for (int q = 0; q < 6; q++)
    {
        int3 o = q == 0 ? int3(1, 0, 0) : q == 1 ? int3(-1, 0, 0) : q == 2 ? int3(0, 1, 0) : q == 3 ? int3(0, -1, 0) : q == 4 ? int3(0, 0, 1) : int3(0, 0, -1);
        int3 nc = pc + o;
        if (any(nc < 0) || any(nc >= ProbeDim.xyz)) continue;
        float4 mn = Probes[ProbeIndex(nc) * 4 + 3];
        if (mn.x > 0.5 && mn.z >= 1.0) had = max(had, mn.y);
    }
    float cap = FireflyRatio * max(max(had, gsMedian), FireflyFloor);
    if (lumRay > cap) L *= cap / lumRay;

    // this lane's shadow ray at a glowing voxel (lanes 0 to K - 1; see MaxGlowSamples)
    float3 G = 0, gd = 0;
    if (gi < K)
    {
        uint bi = gsGlowSel[gi];
        int3 b0 = BrickCoord(bi) * 4;
        float run = 0, lv = 0;
        uint vi = 0;
        [loop] for (uint j = 0; j < 64; j++)
        {
            uint vj = VoxIndex(b0 + int3(j & 3u, (j >> 2) & 3u, j >> 4));
            uint4 vv = Voxels[vj];
            if (!Filled(vv)) continue;
            float lj = Lum(UnpackRGB9E5(vv.z));
            if (!(lj > 0)) continue;
            run += lj;
            if (Rand(seed) * run < lj) { vi = vj; lv = lj; }
        }
        if (lv > 0)
        {
            uint4 v = Voxels[vi];
            float4 nc = UnpackNormal(v.y);
            float3 ctr = VoxOrigin.xyz + ((float3)VoxCoord(vi) + 0.5) * vs;
            float3 w0 = ctr - pos;
            float dc2 = max(dot(w0, w0), 1e-8);
            w0 *= rsqrt(dc2);
            float3 bias = GlowFaceBias(w0, nc);
            float3 fw = abs(w0) * bias;
            float u = Rand(seed) * (fw.x + fw.y + fw.z);
            uint axis = u < fw.x ? 0u : (u < fw.x + fw.y ? 1u : 2u);
            float2 st = float2(Rand(seed), Rand(seed)) * 0.96 - 0.48;
            float3 face = -0.5 * sign(w0);   // the faces turned to the probe
            float3 off = axis == 0 ? float3(face.x, st.x, st.y) : axis == 1 ? float3(st.x, face.y, st.y) : float3(st.x, st.y, face.z);
            float3 dir = ctr + off * vs - pos;
            float dist = length(dir);
            dir /= max(dist, 1e-6);
            // in sight: not the back of a surface (as a ray would see it), and nothing in the way up to the face
            uint hs; float ts;
            bool seen = dist > 0.1 * vs && !(nc.w > 0.35 && dot(nc.xyz, dir) > 0.1) && !March(pos, dir, dist - 0.01 * vs, hs, ts);
            if (seen)
            {
                // (its glow over its density, K samples, as a ray's 32nd share: 32 / qn; times its weight qn^2 / (32^2 + qn^2))
                float qn = misK * GlowDensity(pos, lv, BrickGlow[bi], bias, w0, dc2, axis, total);
                G = UnpackRGB9E5(v.z) * (32.0 * qn / (1024.0 + qn * qn));
                gd = dir;
            }
        }
    }

    gsL[gi] = L + G; gsX[gi] = L * d.x + G * gd.x; gsY[gi] = L * d.y + G * gd.y; gsZ[gi] = L * d.z + G * gd.z;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint s = 16; s > 0; s >>= 1)
    {
        if (gi < s) { gsL[gi] += gsL[gi + s]; gsX[gi] += gsX[gi + s]; gsY[gi] += gsY[gi + s]; gsZ[gi] += gsZ[gi + s]; }
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi != 0) return;

    uint b = pi * 4;
    float4 meta = Probes[b + 3];
    if ((float)gsBack > 32.0 * Misc.z)
    {
        // inside something: not used (WriteVolume fills it from its neighbours)
        Probes[b + 3] = float4(0, meta.y, meta.z, 0);
        return;
    }
    float3 a0 = gsL[0] / 32.0;
    float3 ax = gsX[0] * (2.0 / 32.0), ay = gsY[0] * (2.0 / 32.0), az = gsZ[0] * (2.0 / 32.0);
    float4 nr = float4(ax.r, ay.r, az.r, a0.r);
    float4 ng = float4(ax.g, ay.g, az.g, a0.g);
    float4 nb = float4(ax.b, ay.b, az.b, a0.b);
    float lum = dot(a0, float3(0.2126, 0.7152, 0.0722));
    // a fresh probe takes the result as it is; after that it follows a change of more than a quarter (a light switched, a
    // door opened) faster, while the rays' own noise (32 a round) stays under that and is averaged out
    float rel = abs(lum - meta.y) / (meta.y + 0.05);
    float alpha = meta.x < 0.5 || meta.z < 0.5 ? 1.0 : Misc.x + (0.8 - Misc.x) * saturate((rel - 0.25) / 0.75);
    Probes[b] = lerp(Probes[b], nr, alpha);
    Probes[b + 1] = lerp(Probes[b + 1], ng, alpha);
    Probes[b + 2] = lerp(Probes[b + 2], nb, alpha);
    Probes[b + 3] = float4(1, lerp(meta.y, lum, alpha), min(meta.z + 1.0, 1e6), 0);
}

// How much of an L1 probe's direction is kept: its light (a0 + a1.n) stays at (1 - DeringRatio) of the mean or more in every
// direction (|a1| <= DeringRatio * a0).
static const float DeringRatio = 0.8;

float Dering(float4 c)
{
    float l = length(c.xyz);
    return l > 1e-6 ? min(1.0, DeringRatio * max(c.w, 0.0) / l) : 1.0;
}

// The window of probes the renderers read, in Unity's proxy volume layout. The renderers that read it get no quadratic part from
// the ambient (their block zeroes unity_SHB and unity_SHC), so it holds the whole light to L1. The outermost probes are the sky's
// (to L1: sampling clamps there), fading in to the live probes over WinOrigin.w; renderers wholly beyond that edge get no block
// and keep the plain ambient.
[numthreads(4, 4, 4)]
void WriteVolume(uint3 id : SV_DispatchThreadID)
{
    if (any(id >= (uint3)WinDim.xyz)) return;
    // the sky's L1 part as the shaders take it (unity_SHA* hold its constant less the quadratic part's share, which comes back here)
    float4 sr = SkyVal[0] + float4(0, 0, 0, SkyVal[3].z / 3.0);
    float4 sg = SkyVal[1] + float4(0, 0, 0, SkyVal[4].z / 3.0);
    float4 sb = SkyVal[2] + float4(0, 0, 0, SkyVal[5].z / 3.0);
    float4 r = sr, g = sg, b = sb;
    float3 pos = WinOrigin.xyz + ((float3)id + 0.5) * ProbeOrigin.w;
    int3 pc = (int3)round((pos - ProbeOrigin.xyz) / ProbeOrigin.w - 0.5);
    if (all(pc >= 0) && all(pc < ProbeDim.xyz))
    {
        uint pi = ProbeIndex(pc);
        float4 pr = 0, pg = 0, pb = 0;
        float w = 0;
        if (Probes[pi * 4 + 3].x > 0.5) { pr = Probes[pi * 4]; pg = Probes[pi * 4 + 1]; pb = Probes[pi * 4 + 2]; w = 1; }
        else
        {
            // inside something or not traced yet: the mean of the valid neighbours
            [unroll] for (int k = 0; k < 6; k++)
            {
                int3 o = k == 0 ? int3(1, 0, 0) : k == 1 ? int3(-1, 0, 0) : k == 2 ? int3(0, 1, 0) : k == 3 ? int3(0, -1, 0) : k == 4 ? int3(0, 0, 1) : int3(0, 0, -1);
                int3 q = pc + o;
                if (any(q < 0) || any(q >= ProbeDim.xyz)) continue;
                uint qi = ProbeIndex(q);
                if (Probes[qi * 4 + 3].x < 0.5) continue;
                pr += Probes[qi * 4]; pg += Probes[qi * 4 + 1]; pb += Probes[qi * 4 + 2]; w += 1;
            }
            if (w > 0) { pr /= w; pg /= w; pb /= w; }
        }
        if (w > 0)
        {
            // L1 rings: a probe with bright light on one side works out to less than nothing on the other, which the shaders clamp
            // to black (the fronts of things facing a dark opening). Keep the darkest side at a fifth of the mean at least.
            pr.xyz *= Dering(pr); pg.xyz *= Dering(pg); pb.xyz *= Dering(pb);
            r = pr; g = pg; b = pb;
        }
    }
    int3 e3 = min((int3)id, WinDim.xyz - 1 - (int3)id);
    float f = saturate((float)min(e3.x, min(e3.y, e3.z)) / max(1.0, WinOrigin.w));
    r = lerp(sr, r, f); g = lerp(sg, g, f); b = lerp(sb, b, f);
    uint W = (uint)WinDim.x;
    Volume[uint3(id.x, id.y, id.z)] = r;
    Volume[uint3(id.x + W, id.y, id.z)] = g;
    Volume[uint3(id.x + 2 * W, id.y, id.z)] = b;
    Volume[uint3(id.x + 3 * W, id.y, id.z)] = float4(1, 1, 1, 1);
}

// ------------------------------------------------------------------------------------------------------------------- moving

[numthreads(64, 1, 1)]
void ShiftVoxels(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)VoxDim.w) return;
    int3 s = VoxCoord(id.x) + Shift.xyz;
    uint4 v = 0;
    if (all(s >= 0) && all(s < VoxDim.xyz)) v = VoxelsIn[VoxIndex(s)];
    Voxels[id.x] = v;
}

[numthreads(64, 1, 1)]
void ShiftProbes(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)ProbeDim.w) return;
    int3 s = ProbeCoord(id.x) + Shift.xyz;
    uint b = id.x * 4;
    if (all(s >= 0) && all(s < ProbeDim.xyz))
    {
        uint o = ProbeIndex(s) * 4;
        Probes[b] = ProbesIn[o]; Probes[b + 1] = ProbesIn[o + 1]; Probes[b + 2] = ProbesIn[o + 2]; Probes[b + 3] = ProbesIn[o + 3];
    }
    else { Probes[b] = 0; Probes[b + 1] = 0; Probes[b + 2] = 0; Probes[b + 3] = 0; }
}
