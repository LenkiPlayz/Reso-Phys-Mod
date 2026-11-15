# Real-time GI research for the live bounce light (2026-11-14)

The parent session asked, for Lenki: find real-time global illumination papers and shipped techniques that could improve the
live bounce light (GI) in the renderer fork. This is research only: nothing was built or run on the GPU, and nothing in
Softbody\GpuGI or RenderiteSrc changed. The one thing run was a read-only scan of the game's cached shaders (item 1). Read it with
DESIGN-gi.md.

Update, later on 2026-11-14: Lenki said to port what this suggests. Items 1 and 5 are in "GI test 3" (built, not installed, its
offline GPU checks pass); each item says what was built and how it differs from the plan here.

What the GI does now (DESIGN-gi.md, GpuGI\Hlsl\SoftbodyGI.hlsl, GpuGI\Unity\GiUnity.cs):
- **Voxels:** 128 x 48 x 128 voxels of 25 cm around the head. An orthographic camera draws one slab of 8 slices a frame, at the end of the frame, so the whole window takes 16 frames. Main thread about 0.1 ms in the editor; not yet measured in game.
- **Probes:** 64 x 24 x 64 L1 probes at 50 cm, at the cell centres. 4096 probes trace 32 rays a frame, so each probe is traced every 24 frames.
  - Hysteresis: at least 15 % new, faster above a 25 % change.
  - Firefly cap: a ray brings at most 8x the light already there.
  - Dering: |a1| <= 0.8 a0.
  - A probe that sees more than 30 % back faces is invalid. WriteVolume gives it the mean of its valid 6-neighbours, or the sky's L1 if it has none.
- **GPU:** 0.3 to 0.8 ms a frame (RTX 4070, offline).
- **The hook:** Unity's light probe proxy volume (LPPV) lookup, which Resonite's lit shaders already have. We feed it per renderer through MaterialPropertyBlocks, L0 and L1 only (the block zeroes L2).

What decides the verdicts:
- Unity 2019.4, built-in forward, D3D11, VR and desktop. Worlds are user-made and change, so nothing can be baked.
- The game is CPU-bound. Main-thread work or a sync every frame is expensive; GPU work is close to free.
- Resonite's shaders can't change. A method whose receivers must do more than Unity's LPPV lookup is out, however good it is.

How to read this:
- **Port now:** the next GI test after GI test 2 passes in game. **Later:** worth having after those, or only if a measurement asks for it. **No:** doesn't fit.
- Costs marked "estimate" are mine, scaled from our own 0.3 to 0.8 ms. Nothing here was measured. Published numbers are the authors' own, on their hardware.
- "LPPV: yes" means the result reaches the receivers through today's hook, with no shader change.

## Verdicts at a glance

| # | Item | Verdict | Cost (estimate) |
|---|---|---|---|
| 1 | Per-renderer sampling offset through the LPPV's own matrix (leaks) | **ported** (GI test 3: thin renderers, toward the head); offline and editor tests pass | GPU 0; main thread a few float tests per thin renderer a frame |
| 2 | Least-squares fit of the probe values (leaks, also in single-mesh worlds) | later | GPU +0.05 to 0.2 ms |
| 3 | DDGI's and Unity APV's probe-side fixes (classification, relocation, virtual offset, dilation, sleeping) | no new port, we have the parts that apply; probe sleeping later | - |
| 4 | Visibility in the receiver (DDGI, light field probes, Call of Duty, APV) | no: shader changes | - |
| 5 | Glowing voxels as lights: shadow rays at emitters (flicker, the cap) | **ported** (GI test 3; offline: a lone lamp to 1%, no flicker) | within the timing noise offline (median 0.69 ms with 4 shadow rays, 0.74 without) |
| 6 | Light trees, lightcuts | later, only with tens of thousands of glowing voxels | GPU +0.05 to 0.1 ms |
| 7 | ReSTIR | per-probe reservoirs later, if 5 still flickers; ReSTIR GI no | GPU +0.02 ms |
| 8 | Prefiltered glow (emission mips and cones) | later, a fallback for 5 | GPU about +0.05 ms |
| 9 | Guided probe rays (Lumen style) | no | - |
| 10 | Contact detail | Resonite's AO already does it | - |
| 11 | A finer 25 cm probe cascade near the head | later, and limited | GPU +0.1 to 0.3 ms |
| 12 | Screen-space GI | no for the volume | - |
| 13 | Avatars as analytic occluders, with a second volume for avatars | later | GPU +0.02 to 0.05 ms; microseconds a frame per avatar |
| 14 | Voxelizing avatars | no | - |
| 15 | Compute voxelization from mesh data (no camera) | later, only if GI test 2's slab draw costs much in busy worlds | GPU +0.1 to 0.3 ms; main thread nothing a frame |
| 16 | Voxel clipmaps and cone tracing (VXGI, The Tomorrow Children, SEGI) | per-pixel cones no; a coarser outer cascade later | GPU +0.2 to 0.4 ms, plus slab draws over 16x the area |
| 17 | Distance fields and surface caches (Lumen, Godot SDFGI and HDDAGI, Brixelizer GI) | no | - |
| 18 | Surfels (EA SEED's GIBS) | no | - |
| 19 | Radiance cascades | no | - |
| 20 | Light propagation volumes | no | - |

## The four problems, answered

1. **Light leaks through thin walls.** Every published fix for a surface that sits between two valid probes on either side of a thin wall works in the receiver's shader (DDGI, light field probes, Call of Duty's depth maps, Unity APV's leak reduction), except a least-squares solve. Two things work without a shader change:
   - Item 1: Unity's LPPV lookup already moves the pixel's position by a matrix from the renderer's block (`unity_ProbeVolumeWorldToObject`, used when `unity_ProbeVolumeParams.y` is 1). With it, each renderer can read the volume 25 to 50 cm in front of its surfaces. All the game's lit programs already read that matrix (checked). It is our own idea and untested in a scene; no published use was found.
   - Item 2: solve for probe values that give each surface its own side's light under Unity's plain trilinear lookup (Kavan et al. 2011; Activision's light grid, Iwanicki and Sloan 2017).
   - DDGI's classification and Unity APV's dilation are what we already have (item 3). They only help probes buried in geometry.
2. **Small, very bright emitters flicker, and the cap under-lights them.** Item 5: each probe also sends a few shadow rays straight at glowing voxels, picked by brightness over distance squared (next-event estimation, with ReGIR-style candidate lists). Ordinary rays then stop counting glow, so nothing counts twice. Glow is no longer a one-in-32 lottery, the cap only touches bounce light, and the under-lighting goes. Light trees and ReSTIR only pay with far more emitters (items 6 and 7).
3. **50 cm probes blur contact detail.** No probe method gives contact detail without the receiver's help. Resonite's own AO post effect already darkens contacts per pixel (item 10). A finer cascade near the head is possible, but big floors would keep the coarse volume (item 11).
4. **Avatars are hidden from the voxels.** Keep them out of the voxels: drawing them every frame costs main thread, and 2019.4 can't reach skinned vertices from compute (item 14). Add them instead as analytic occluders, applied every frame, with a second copy of the volume for the avatars' own renderers so they don't darken themselves (item 13; The Last of Us 2013, Unreal's capsule shadows).

## Leaks (topic b)

1. **Per-renderer sampling offset (ours).** Port now.
   - **What:** Unity's lookup (`SHEvalLinearL0L1_SampleProbeVolume` in UnityCG.cginc, 2019.4.31f1) takes the pixel's world position. When `unity_ProbeVolumeParams.y` is 1, it first multiplies that position by `unity_ProbeVolumeWorldToObject`. Both come from the renderer's block, like the rest of the volume. Today we send Params.y = 0 and an identity matrix (`GiUnity.Switch` and `Fill`). A moved matrix makes a renderer read the volume somewhere else, with no shader change:
     - **Flat renderers** (floors, ceilings, single walls, quads): a shift of 25 to 50 cm toward the side they face. One shared block per direction (six, and none) keeps today's cost.
     - **Thin boxes,** such as a wall with two faces: a stretch along the thin axis about the box's middle, so each face reads 25 to 50 cm in front of itself.
     - **Props:** a stretch along each of their axes, so the faces read the air outside them, not the dilated probes inside.
     - **Room shells** (faces pointing inward): a squeeze toward the middle, so the floor, walls and ceiling read 25 to 50 cm into the room.
     - **Which kind a mesh is:** the area-weighted mean of whether its faces point away from its middle (+1 a prop, -1 a room shell, near 0 mixed), worked out once per mesh upload, off the main thread. Unity also takes a block per material slot, so a mesh split by material could get a matrix per part.
   - **How much it fixes:**
     - A 25 cm shift leaves at most half the weight on a probe plane up to 25 cm behind the surface. Such a probe usually sees the surface's back and is replaced by dilation anyway.
     - A 50 cm shift puts both probe planes in front.
     - The floor at y = 0 shifted 25 cm reads exactly the plane at 0.25 m, so its leak from below goes.
     - It should also lower the corner glow in `GpuGI/tests/scene/compare_PerRenderer_corner.png`, which DESIGN-gi.md puts down to the floor and walls reading probes on their far side.
   - **What it can't fix:** one mesh holding both sides of many walls, such as a whole imported building (item 2 is for those). Renderers that move keep the identity.
   - **Cost:**
     - GPU nothing. Main thread nothing a frame.
     - In a sweep, the shared-block version only picks a block (the sweep already reads each renderer's bounds for `GiUnity.Covers`).
     - Per-renderer stretches need a block per renderer, filled once per sweep. Not measured; GiProbeTest C11 measured 1.19 ms for 2000 block sets, and the sweep caps sets at 300 a frame.
   - **D3D11 and 2019.4:** yes, it is Unity's own lookup. **LPPV:** yes.
   - **Checked (read-only, 2026-11-14):** all 2280 D3D11 programs of the 15 lit families that read `unity_ProbeVolumeSH` also list `unity_ProbeVolumeWorldToObject`, `Params`, `Min` and `SizeInv`. This is a scan of the cached bundles in RuntimeData\PreCache, by the same method as on 2026-11-13 (each program's tail lists the parameters it uses). So the moved lookup is compiled into every lit shader the game has.
   - **Still to do (done, see below):** an editor test with a thin wall between a lit room and a dark one, to see the moved lookup at work.
   - **Built (GI test 3, 2026-11-14):** simpler than the plan above, and with no per-mesh work.
     - Which renderers: thin ones by their bounds alone, at most 0.6 m along an axis and at least 1 m along another (walls,
       floors, ceilings, beams). Props and room shells keep the plain lookup, because telling them apart needs the mesh's faces.
     - Which way: toward the head, by `leak_offset` (0.5 m) along each thin axis, instead of a stretch about the middle. One
       translation reaches both faces of a thin box only for the face you look at; the other face reads the wrong side, and only
       a mirror or a camera behind the wall shows it. 0.5 m, a whole probe spacing, so both probe planes it reads are in front
       (25 cm can leave half the weight behind, as above).
     - Cost: 27 shared blocks (each direction, and none), so the sweep only picks one. `FollowHead` runs a few float tests per
       thin renderer a frame and sets a block only when the head crosses one's middle; it checks 32 thin renderers' bounds a
       frame.
     - Offline `leak` check (passes): two rooms, a lamp in one, the wall 0.5 m thick; the dark room's face of the wall must take
       less than 15 % of its plain lookup's light, and the lit face keep at least 95 %. It took 0.67 plain and 0.00 read in
       front; the lit face 2.01 plain, 2.70 in front.
     - Editor `build_gi.ps1 -Leak` (passes): a thin wall lit only by a ramp written into the volume, drawn with the game's own
       PBS shader; what each face shows must follow the ramp when the offset moves the lookup, and FollowHead must turn it. It
       showed x 1.411 and x 0.496 where the ramp gives x 1.417 and x 0.500.
     - Seen in the editor's Cornell box: near a light the moved lookup reads a little nearer the light than the surface is (the
       back wall, 0.6 m behind the glowing cube's middle, reads the light at the cube's own depth), so such a patch comes out bright and, at 50 cm
       probes, a little square.
     - Left for later: rotated walls (their bounds aren't thin), stretches for two-faced walls seen from both sides, props and
       room shells, single-mesh buildings (item 2).
   - **Precedent:** none found. Unity APV's virtual offset moves the capture point at bake time, and its normal and view bias move the sampling point inside the shader. This does the second per renderer, from outside the shader. Unity's own advice for leaks is walls about as thick as the probe spacing; this makes each renderer read the volume as if its walls were.

2. **Least-squares fit of the probe values.** Later.
   - **What:** Kavan, Bargteil and Sloan (2011) pick per-vertex values so that the interpolation between vertices matches the real signal in the least-squares sense, plus a regulariser. Activision solves its light grid the same way in Call of Duty: Infinite Warfare (Iwanicki and Sloan 2017): super-sampled points, only the probes that see them, and a smoothness term. They warn that linear SH rings without regularisation.
   - **For us, at run time:**
     - For the surface voxels around the probes traced this frame, the target is the light from the probes on the surface's front side only. Our compute shader can drop the probes behind a surface and renormalise; Unity's lookup can't.
     - A few relaxation sweeps a frame then move the stored L1 values, so that Unity's blind trilinear lookup at those points gives the targets.
     - A pull toward the traced values keeps the light in mid-air sane for avatars and props, and the dering clamp still applies.
   - **What it fixes:** what item 1 can't (single-mesh buildings, both faces of one renderer). It reduces a thin-wall leak but doesn't remove it. One L1 probe pair has to serve both faces, so with a lit room next to a dark one, the best fit leaves the lit wall a little dark or that room's probes a little bright.
   - **Cost:**
     - GPU +0.05 to 0.2 ms (estimate): a few sweeps over the 4096 traced probes and their neighbours, starting from last frame's values.
     - About 1 to 2 MB for the targets. Main thread nothing.
   - **D3D11:** yes (compute). **LPPV:** yes, only the stored values change.
   - **Verdict:** later: after item 1, if leaks remain in single-mesh worlds. Prototype it offline in the GpuGI tests first.

3. **DDGI's and Unity APV's probe-side fixes.** No new port; probe sleeping later.
   - **DDGI** (Majercik et al. 2019) and its production follow-up (2021) add:
     - **Classification:** a probe whose rays hit more than 25 % back faces is inside geometry and switched off. Ours does the same at 30 %.
     - **Relocation:** probes move out of walls, by up to a share of the spacing. The LPPV is a fixed grid, so ours can't move.
     - **Hysteresis:** changes above 25 % lower it, above 80 % drop it. Ours ramps the new share from 0.15 to 0.8 above a 25 % change.
     - **Sleeping:** probes far from surfaces and changes update less often; they report 30 to 50 % faster. For us, this would trace the probes near surfaces (the ones receivers read) more often for the same rays: later, for convergence.
     - Its leak fix proper (a Chebyshev visibility test and a normal bias) is in the receiver (item 4).
   - **Unity APV** (Unity 6):
     - validity, the same back-face test;
     - virtual offset: trace from a point moved out of the geometry, store at the grid node;
     - dilation: invalid probes take nearby valid ones;
     - adjustment volumes that force probes invalid by hand. Unity says these fix most leaks but not all. Its other fixes (leak reduction mode, normal and view bias, layer masks) are in the shader.
   - All of these fix probes buried in geometry. None fixes a surface between two valid probes on either side of a thin wall, which is our leak.
   - A small tweak our code suggests: a probe with no valid neighbour falls back to the sky's L1 (WriteVolume). A second dilation pass would give probes deep inside thick walls nearby room light instead. Surfaces rarely read probes that deep, so only if a test shows sky light inside thick walls.

4. **Visibility in the receiver.** No: each needs the receiver's shader.
   - DDGI's per-probe depth and Chebyshev test (2019, 2021).
   - Light field probes (McGuire et al. 2017).
   - Call of Duty's per-probe depth over its tetrahedra (Iwanicki and Sloan 2017).
   - Local reconstruction from sparse probes (Silvennoinen and Lehtinen 2017).
   - Unity APV's leak reduction mode, normal and view bias, and rendering-layer masks.
   - Godot SDFGI's occlusion option.
   - VRC Light Volumes (its own sampling code in each shader).
   - One idea carries over: APV's layer masks keep interior objects from reading exterior probes. Our binding is per renderer, so a second volume with the outdoor probes replaced by indoor ones (and the reverse) could do the same, if renderers can be sorted into indoor and outdoor. Item 1 covers most of it more cheaply.
   - Nothing was found on feeding Unity's LPPV from one's own GI. As far as the searches went, our hook is unusual: Unity fills the LPPV from baked probes only, and VRC Light Volumes ships its own shaders instead.

## Small bright emitters (topic c)

5. **Glowing voxels as lights in the probe trace.** Port now.
   - **What:** next-event estimation. Besides its 32 random rays, each probe sends a few rays straight at emitters and adds their light with the right weight. Glow that a random ray happens to hit is then not counted (or counted with MIS weights), so nothing counts twice.
   - **Picking the emitters:**
     - a list of glowing voxels, rebuilt as Inject lights the slices (it already visits them);
     - per brick (4 x 4 x 4 voxels), the summed glow, so a pick takes two steps: a brick by glow over distance squared, then a voxel in it;
     - ReGIR (Boksansky et al. 2021) keeps a few candidates per grid cell, resampled each frame by resampled importance sampling (as in ReSTIR, Bitterli et al. 2020), and points in that cell draw from them. For us, a cell is a brick or a probe cell.
   - **What changes:**
     - A lone tiny glow is picked at every update, so its light is steady instead of a one-in-32 hit.
     - The cap no longer touches glow, so a glowing thing lights a room beyond 1 m at its real strength.
     - The voxels it lights carry it on through the next bounce, as now.
   - **Extension:** the list can also take the brightest lit voxels (a spotlit patch), which have the same lottery problem.
   - **Cost:** GPU +0.05 to 0.15 ms (estimate): 2 to 4 shadow rays a probe, so 8,000 to 16,000 a frame against 131,000 now, and shorter ones, plus building the list. Main thread nothing.
   - **D3D11:** yes, plain compute (append and structured buffers, shader model 5). The DDGI papers use DXR; nothing here needs it.
   - **LPPV:** yes, it ends up in the probe values.
   - **Precedent:** nothing found that drives probe updates with explicit emitter rays or ReSTIR. The nearest is DDGI resampling (Majercik et al. 2021), which runs ReSTIR at pixels with DDGI as the fallback. A probe is a point receiver, which is the case these light-sampling methods are built for.
   - **Built (GI test 3, 2026-11-14):**
     - The list: per brick, the summed glow (luminance) and the glow-weighted middle, made in `BuildBricks`; `ListGlow` lists
       the bricks that glow. No candidates kept from frame to frame (no ReGIR cells): each probe weighs the list itself.
     - The pick: each probe weighs up to 1024 glowing bricks (a random window of the list when more glow, scaled back up) by
       glow over distance squared (no closer than 0.5 m), one reservoir per lane, merged. Each of its `glow_rays` shadow rays
       (default 4) then picks a voxel in its brick by glow (64 reads) and a point on a face of that voxel turned toward the
       probe, by the face's projected area (faces a coherent voxel hides inside count 5 %).
     - Weights: the power heuristic between the 32 rays and the shadow rays (MIS), so a ray that hits glow keeps only its
       share, and nothing counts twice. The balance heuristic came first; with it a rare ray that hit a small lamp still brought
       a quarter of the lamp's light in one round, and `lamp` flickered 8%. With the power heuristic: 0%. Glow in a brick that isn't listed stays the ray's alone.
     - The firefly cap now touches the 32 rays only.
     - Offline checks (all pass, 2026-11-14): `lamp`, a single glowing voxel in a black room, must give the analytic light
       within 12 %, from the right direction, with under 8 % flicker over 60 frames (the rays alone run too, to compare);
       `panel`, a 2 x 2 m glowing panel, within 10 % of the analytic light at four spots with the shadow rays, and without them
       at the spots nearer than 1.5 m; `time` measures the cost with 4 shadow rays and with none.

6. **Light trees and lightcuts.** Later, only with tens of thousands of glowing voxels.
   - **What:** a tree over the lights; a pick walks down it by each node's estimated share.
     - Lightcuts (Walter et al. 2005) handles hundreds of thousands of point lights with a few hundred shadow rays.
     - Stochastic lightcuts (Yuksel 2019) and real-time stochastic lightcuts (Lin and Yuksel 2020) rebuild the tree on the GPU every frame.
     - Conty Estevez and Kulla (2018) split the tree adaptively; Tokuyoshi et al. (2024) use spherical-Gaussian nodes.
   - **For us:** the bricks already make a two-level tree. A deeper one only pays when glowing voxels are many and spread wide.
   - **Cost:** GPU +0.05 to 0.1 ms to rebuild over at most 12,288 bricks (estimate). **D3D11:** yes. **LPPV:** yes.

7. **ReSTIR.** Per-probe reservoirs later, if item 5 still flickers; ReSTIR GI no.
   - **What:**
     - ReSTIR (Bitterli et al. 2020) keeps a reservoir of light candidates per pixel and reuses it across neighbours and frames. The authors reach equal error 6 to 60 times faster, with up to 3.4 million glowing triangles in under 50 ms.
     - ReSTIR GI (Ouyang et al. 2021) does the same with indirect paths.
     - World-space versions exist: a hash grid of reservoirs (Boisse 2021), and a cheaper rewrite for production (Wyman and Panteleev 2021).
   - **For us:** a reservoir per probe (about 3 MB) would keep each probe's best emitter samples from one update to the next. Our hysteresis already averages over time, so this is a second step. ReSTIR GI's reuse is between screen pixels; for probes, the hysteresis does its job.
   - **Cost:** GPU +0.02 ms (estimate). **D3D11:** yes. **LPPV:** yes.

8. **Prefiltered glow (emission mips and cones).** Later, as a fallback for item 5.
   - **What:** a mip chain of the voxels' glow and opacity, which cones march to gather glow without noise (Crassin et al. 2011). The Tomorrow Children traced 16 fixed cone directions through cascaded voxel textures (McLaren 2014, 2015).
   - **For us:** a deterministic far term for glow. But coarse mips blur thin walls into half-transparent ones, so glow leaks through them, and small emitters smear.
   - **Cost:** GPU about +0.05 ms (estimate: the mips plus 6 to 16 cones for the 4096 probes). **D3D11:** yes. **LPPV:** yes.

9. **Guided probe rays.** No.
   - Lumen sends its screen probes' rays where last frame's light was bright (Wright et al. 2022). An L1 probe keeps too little direction to guide by, and a tiny emitter is still missed until a ray first finds it. Item 5 aims at emitters directly.

## Contact detail

10. **Resonite's ambient occlusion.** Nothing to port.
    - Resonite already has an AO post effect (PostProcessingConfig). It darkens contacts per pixel, which no probe grid can. Our GI changes only the ambient term and leaves the AO alone. Worth a look in game with both on.

11. **A finer probe cascade near the head.** Later, and limited.
    - **What:** 25 cm probes over about 8 x 6 x 8 m around the head (32 x 24 x 32 = 24,576 probes), bound to the renderers that fit inside it. The binding is per renderer, so a second volume is possible.
    - **Limit:** a renderer reads one volume, so big floors and walls, where contact detail shows most, keep the coarse one.
    - **Cost:** GPU +0.1 to 0.3 ms (estimate). Main thread: renderers change volume as the head moves, so more sweeps.

12. **Screen-space GI.** No for the volume.
    - **What:** indirect light from the depth buffer. SSILVB (Therrien et al. 2023) adds a visibility bitmask; the authors put its occlusion cost near earlier AO methods, and its indirect light above that.
    - **For us:** it can't write probes and only sees what is on screen. It needs a camera hook per camera (set once), normals (rebuilt from depth in forward), and a pass per eye in VR. As its own post effect it is a separate project, and its contact part overlaps the AO Resonite already has.

## Avatars

13. **Avatars as analytic occluders.** Later.
    - **What:**
      - The Last of Us darkened the ambient light around characters with analytic occlusion from ellipsoids (Iwanicki 2013).
      - Unreal's indirect capsule shadows do the same from a character's physics capsules, to ground characters in indirectly lit areas; the cost grows with the number of capsules.
      - Quilez gives the closed-form occlusion of a sphere.
    - **For us:**
      - Every frame, WriteVolume dims each probe's light from the directions its nearby occluders cover.
      - Occluders: an ellipsoid per avatar renderer from its bounds, or capsules from bones. The 0.29.2 body spheres would be better, but they live in the engine-side plugin and would need a channel to the renderer.
      - Avatars read a second copy of the volume without occluders, so they don't darken themselves. The binding is per renderer, so this is a second texture, not a shader change.
      - Every frame, so no lag. At 50 cm it is a soft dark patch about a metre across around and under a person, not a sharp shadow.
    - **Cost:**
      - GPU +0.02 to 0.05 ms, and a second 3D texture of a few MB (estimates).
      - Main thread: a bounds read per avatar renderer in the window a frame, a few microseconds per avatar (estimate), or nothing if the plugin sends spheres.
    - **D3D11:** yes. **LPPV:** yes.

14. **Voxelizing avatars.** No.
    - Drawing skinned meshes into the voxels every frame costs main thread. A compute path needs their skinned vertices: Unity added GPU access to those (`SkinnedMeshRenderer.GetVertexBuffer`, with `Mesh.GetVertexBuffer`) only in 2021.2. 2019.4 has `Mesh.GetNativeVertexBufferPtr` for plain meshes only. Drawing them every few frames smears, as now.

## Scene representations with little main-thread cost (topic a)

15. **Compute voxelization from mesh data.** Later: decide on GI test 2's in-game numbers.
    - **What:** compute shaders put each triangle into the voxels it touches, straight from the mesh buffers, with no camera (Schwarz and Seidel 2010). AMD's Brixelizer (2023) builds distance-field bricks from triangles in compute; dynamic objects are rebuilt every frame, and the cost scales with the triangles.
    - **For us:**
      - Unity 2019.4 gives a plain mesh's D3D11 vertex buffer through `Mesh.GetNativeVertexBufferPtr`, but the call syncs with the render thread (Unity's docs say to make it at init). Resonite makes and changes meshes all the time, so that would be a sync per new mesh.
      - The fork could instead keep its own GPU copy when it uploads a mesh, plus each renderer's matrix (from the engine's transform updates) and each material's mean colour.
      - Skinned meshes stay out either way (item 14).
    - **Cost:** GPU +0.1 to 0.3 ms (estimate: a sixteenth of the window's triangles a frame). Main thread nothing a frame, but it is a large rewrite, and the mesh copies take GPU memory.
    - **D3D11:** yes (compute). **LPPV:** unchanged; only the way voxels are made changes.
    - **Verdict:** GI test 2's stats line ("end of frame ... ms (slab ...)") gives the slab draw's real cost. It grows with the renderers in each slab, so a busy world is the test. If it stays near the editor's 0.1 ms, keeping the camera is cheaper than this rewrite.

16. **Voxel clipmaps and cone tracing.** Per-pixel cone tracing no; a coarser outer cascade later.
    - **What:**
      - VXGI (Panteleev 2014): a 3D clipmap of voxels around the camera, cone traced per pixel.
      - The Tomorrow Children (McLaren 2014, 2015): cascaded voxel textures, 16 fixed cone directions, direct light plus 3 bounces at 30 Hz on PS4.
      - SEGI (Sonic Ether, open source, Unity built-in): a per-pixel image effect with its own voxelizing camera every frame; its repository was last updated in 2018.
    - **For us:** cone tracing per pixel needs the receiver's shader. A coarser second voxel grid (1 m voxels, 128 m across) would add occlusion and glow beyond our 32 m window. But it has to be voxelized too: 16 times the area, so many more renderers per slab on the main thread, unless item 15 comes first or it updates very slowly.
    - **Cost:** GPU +0.2 to 0.4 ms for the outer grid (estimate). **D3D11:** yes. **LPPV:** the outer grid yes; per-pixel cones no.

17. **Distance fields and surface caches.** No.
    - **What:**
      - Lumen (Wright, Narkowicz and Kelly 2022) traces mesh distance fields for the first 2 m, then a global one. It caches light on surface cards and gathers per pixel from screen probes. Epic budgets 4 ms at 60 fps and 8 ms at 30 fps for GI and reflections on consoles.
      - Godot 4's SDFGI: cascaded distance fields plus probes. Its docs say moving lights work, but not moving occluders or glow. HDDAGI, an upgrade proposed in 2024, swaps the distance field for a hierarchical march over voxels.
      - Brixelizer GI (2024): a GI sample built on Brixelizer's bricks.
    - **For us:** the receiver side (G-buffer, per-pixel gather) is out. Mesh distance fields need work per mesh, and cards need captures. We already have SDFGI's and HDDAGI's core: voxels, a two-level march over bricks, a probe grid.
    - **One piece to keep:** a distance field over our own voxels (jump flooding, about 0.05 to 0.1 ms, estimate) would let rays skip empty space faster than bricks do. Later, only if the ray cost matters.

18. **Surfels (EA SEED's GIBS).** No.
    - Halén et al. (2021) spawn surfels from the G-buffer, ray trace their light, and apply it per pixel. We have neither a G-buffer nor per-pixel application.

19. **Radiance cascades.** No.
    - Sannikov (2023) made 2D screen-space GI cheap with cascades of probes that trace longer intervals at lower resolution (used in Path of Exile 2).
    - Split Radiance Cascades (Freeman, Sannikov and Pitt-Francis 2026) take it to 3D with sparse probes near visible surfaces, gathered per pixel. The paper gives 11.5 to 13.2 ms a frame on San Miguel (laptop RTX 3080).
    - That is about ten times our budget, and per pixel. One idea to keep: split rays by length (dense probes trace short rays, sparse probes the long ones) if long rays ever dominate our trace.

20. **Light propagation volumes.** No.
    - Kaplanyan (2009) and Kaplanyan and Dachsbacher (2010): light goes into an SH grid from reflective shadow maps, then spreads cell to cell; a few milliseconds on consoles, by the abstract.
    - It writes SH straight into a grid like ours, and our lit voxels could feed it instead of shadow maps (GPU only, 0.05 to 0.1 ms, estimate). But it leaks through thin walls, smears light, and needs many steps to carry light far. Our traced probes are already better, and item 5 handles glow more accurately.

## GI in social VR and user-made worlds (topic d)

- **VRChat:**
  - Worlds are Unity projects uploaded by their creators, so everything is baked in the editor: lightmaps (Unity's or Bakery), light probes, baked reflection probes.
  - Community guides warn that realtime reflection probes are costly, and place light probes room by room because they leak between rooms.
  - VRC Light Volumes (REDSIM, MIT, 2025) is a voxel SH volume that replaces light probes: baked, plus up to 128 runtime lights. Shaders must be written for it; VRChat's own Toon Standard got support in SDK 3.8.1. No costs published.
- **Roblox:** worlds change live. Lighting was computed per voxel in grids around the camera, then shadow maps and per-pixel lights were added ("Future", Kapoulkine 2019). "Unified Lighting" (2025) maps the old settings onto lighting styles. No bounce light shipped, as far as found.
- **Dreams (PS4):** Evans's 2015 talk surveys the renderers Media Molecule tried and dropped. The slides were too large to read here, so its final GI method isn't covered.
- **Rec Room:** nothing found.
- **Meta Horizon Worlds:** a mobile-first forward renderer with a built-in light baker for static lights and probes for moving objects. At most 10 dynamic lights, without shadows (community creator manual). No live GI found.
- **Fortnite (UEFN):** Lumen wherever the platform can run it, a sky light instead below that. Epic's console budget is 4 to 8 ms of GPU for GI and reflections.
- **Second Life (PBR viewer):**
  - Reflection probes, placed by users or automatically, double as its GI. They rebake at least every 30 s, at a cost; probe updates take 1 to 3 s.
  - Users report light bleeding through walls into nearby rooms.
  - Separate light probes were requested and marked "tracked" (2025).
- **Resonite:**
  - Issue 220 (light baking, open since 2023): replies say probes, and maybe lightmapping, are being considered for the new renderer, and that the collaborative workflow is the hard part.
  - Issue 5140 (light probes, 2025) is open.
  - No official GI plan found. A comment mentions a third-party "Lumos" made with ResoniteLink (not checked).

**For us:**
- Nobody ships live bounce light for user-made worlds on a CPU budget like ours. The closest are Roblox (live voxel lighting, no bounce) and Fortnite (Lumen at 4 to 8 ms of GPU, off on weak platforms). Ours, at 0.3 to 0.8 ms of GPU and about 0.1 ms of main thread, is at the cheap end.
- Leaks through walls are the complaint wherever probes are used (VRChat, Second Life). That makes problem 1 the one to fix first.
- VRChat's newest answer (Light Volumes) is the same idea as ours, an SH volume read per pixel, but it needs shaders written for it. Ours needs none.

## Looked at, poor fit for us
- **AMD GI-1.0 (2022):** screen probes plus a world-space hash cache, 1.93 to 3.12 ms on an RX 6900 XT by its report. The screen probes need a G-buffer.
- **NVIDIA SHaRC (RTXGI 2.0):** a hash-grid radiance cache for path tracing, not a light sampler. Our probe grid already is a world-space cache.
- **Far Cry 3's deferred radiance transfer volumes** (Gilabert and Stefanov 2012): precomputed transfer, so baking.
- **Tetrahedral probe interpolation** (Cupisz 2012): the LPPV is a grid.

## What I'd do, in order
1. Run GI test 2 in game. Its stats line decides item 15, and how it looks shows how bad the leaks are.
2. Item 1: an editor test with a thin wall, then floors and walls first (the shared shift blocks), stretches second.
3. Item 5, checked with the offline glow and spark tests.
4. If leaks remain in single-mesh worlds: item 2. For avatars: item 13.

(Items 1 and 5 were then built together as GI test 3, at Lenki's word, before GI test 2 ran in game. Its GPU checks come next,
then the editor scene and leak test, then the game after asking.)

## Sources

Leaks and probes:
- Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields. Majercik, Guertin, Nowrouzezahrai, McGuire, JCGT 8(2), 2019. https://research.nvidia.com/publication/2019-05_dynamic-diffuse-global-illumination-ray-traced-irradiance-fields
- Scaling Probe-Based Real-Time Dynamic Global Illumination for Production. Majercik, Marrs, Spjut, McGuire, JCGT 10(2), 2021. https://jcgt.org/published/0010/02/01/paper.pdf (also https://arxiv.org/abs/2009.10796)
- Real-Time Global Illumination using Precomputed Light Field Probes. McGuire, Mara, Nowrouzezahrai, Luebke, I3D 2017. https://research.nvidia.com/publication/2017-02_real-time-global-illumination-using-precomputed-light-field-probes
- Least Squares Vertex Baking. Kavan, Bargteil, Sloan, Computer Graphics Forum 30(4), EGSR 2011. https://users.cs.utah.edu/~ladislav/kavan11least/kavan11least.html
- Precomputed Lighting in Call of Duty: Infinite Warfare. Iwanicki, Sloan, SIGGRAPH 2017 Advances in Real-Time Rendering. https://advances.realtimerendering.com/s2017/Precomputed%20Lighting%20in%20CoD%20IW_20_PPS.pdf
- Real-time Global Illumination by Precomputed Local Reconstruction from Sparse Radiance Probes. Silvennoinen, Lehtinen, SIGGRAPH Asia 2017. https://arisilvennoinen.github.io/Projects/RTGI/index.html
- Light Probe Interpolation Using Tetrahedral Tessellations. Cupisz, GDC 2012. https://gdcvault.com/play/1015312/Light-Probe-Interpolation-Using-Tetrahedral
- Deferred Radiance Transfer Volumes. Gilabert, Stefanov, GDC 2012. https://gdcvault.com/play/1015957/Deferred-Radiance-Transfer-Volumes-Global
- Unity 6 manual, Adaptive Probe Volumes: light leaks https://docs.unity3d.com/Manual/urp/probevolumes-troubleshoot-light-leaks.html and probe validity https://docs.unity3d.com/Manual/urp/probevolumes-light-probe-validity.html
- Unity 2019.4 manual, Light Probe Proxy Volume: https://docs.unity3d.com/ru/2019.4/Manual/class-LightProbeProxyVolume.html. The lookup itself: `SHEvalLinearL0L1_SampleProbeVolume` in UnityCG.cginc, Unity 2019.4.31f1 (Editor\Data\CGIncludes, installed on this PC).
- Godot 4.4 docs, Using SDFGI: https://docs.godotengine.org/en/4.4/tutorials/3d/global_illumination/using_sdfgi.html

Emitters:
- Spatiotemporal reservoir resampling for real-time ray tracing with dynamic direct lighting (ReSTIR). Bitterli, Wyman, Pharr, Shirley, Lefohn, Jarosz, SIGGRAPH 2020. https://cs.dartmouth.edu/wjarosz/publications/bitterli20spatiotemporal.html
- ReSTIR GI: Path Resampling for Real-Time Path Tracing. Ouyang, Liu, Kettunen, Pharr, Pantaleoni, HPG 2021. https://diglib.eg.org/handle/10.1111/cgf14378
- Rendering Many Lights with Grid-Based Reservoirs (ReGIR). Boksansky, Jukarainen, Wyman, Ray Tracing Gems II, 2021. https://boksajak.github.io/files/RTG2_ReGIRGridBasedReservoirs.pdf
- Rearchitecting Spatiotemporal Resampling for Production. Wyman, Panteleev, HPG 2021. https://diglib.eg.org/handle/10.2312/hpg20211281
- World-Space Spatiotemporal Reservoir Reuse for Ray-Traced Global Illumination. Boisse, SIGGRAPH Asia 2021. https://gpuopen.com/download/SA2021_WorldSpace_ReSTIR.pdf
- Dynamic Diffuse Global Illumination Resampling. Majercik et al., 2021. https://arxiv.org/pdf/2108.05263
- Lightcuts: a scalable approach to illumination. Walter, Fernandez, Arbree, Bala, Donikian, Greenberg, SIGGRAPH 2005. https://www.cs.cornell.edu/~kb/projects/lightcuts/
- Stochastic Lightcuts. Yuksel, HPG 2019; Real-Time Stochastic Lightcuts. Lin, Yuksel, I3D 2020. https://www.cemyuksel.com/research/stochasticlightcuts
- Importance Sampling of Many Lights with Adaptive Tree Splitting. Conty Estevez, Kulla, HPG 2018. https://fpsunflower.github.io/ckulla/data/many-lights-hpg2018.pdf
- Hierarchical Light Sampling with Accurate Spherical Gaussian Lighting. Tokuyoshi, Ikeda, Kulkarni, Harada, SIGGRAPH Asia 2024. https://gpuopen.com/download/publications/Hierarchical_Light_Sampling_with_Accurate_Spherical_Gaussian_Lighting.pdf
- Interactive Indirect Illumination Using Voxel Cone Tracing. Crassin, Neyret, Sainz, Green, Eisemann, Pacific Graphics 2011. https://research.nvidia.com/labs/rtr/publication/crassin2011givoxels/
- Lumen: Real-time Global Illumination in Unreal Engine 5. Wright, Narkowicz, Kelly, SIGGRAPH 2022 Advances. https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Lumen-Wright%20et%20al.pdf

Contact detail and avatars:
- Screen Space Indirect Lighting with Visibility Bitmask. Therrien, Levesque, Gilet, The Visual Computer, 2023. https://arxiv.org/pdf/2301.11376
- Lighting Technology of The Last of Us. Iwanicki, SIGGRAPH 2013 talk. https://history.siggraph.org/learning/lighting-technology-of-the-last-of-us-by-iwanicki
- Capsule Shadows (Unreal Engine docs). https://dev.epicgames.com/documentation/unreal-engine/capsule-shadows-overview-in-unreal-engine
- Sphere ambient occlusion. Quilez (web article). https://iquilezles.org/articles/sphereao
- Unity 2021.2 scripting reference, Mesh.GetVertexBuffer: https://docs.unity3d.com/2021.2/Documentation/ScriptReference/Mesh.GetVertexBuffer.html

Scene representations:
- Fast Parallel Surface and Solid Voxelization on GPUs. Schwarz, Seidel, SIGGRAPH Asia 2010. https://history.siggraph.org/?p=197306
- AMD FidelityFX Brixelizer (2023): https://gpuopen.com/learn/introducing-amd-fidelityfx-brixelizer/; Brixelizer GI sample (2024): https://gpuopen.com/manuals/fidelityfx_sdk/samples/brixelizer-gi/
- Unity 2019.4 scripting reference, Mesh.GetNativeVertexBufferPtr: https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Mesh.GetNativeVertexBufferPtr.html
- Practical Real-Time Voxel-Based Global Illumination for Current GPUs (VXGI). Panteleev, GTC 2014 (slides, university mirror). https://cgvr.cs.uni-bremen.de/theses/finishedtheses/VoxelConeTracing/S4552-rt-voxel-based-global-illumination-gpus.pdf
- The Tomorrow Children: Lighting and Mining with Voxels. McLaren, Yang, SIGGRAPH 2015 talk. https://history.siggraph.org/learning/the-tomorrow-children-lighting-and-mining-with-voxels; Cascaded Voxel Cone Tracing slides, McLaren, CEDEC 2014: https://fumufumu.q-games.com/archives/Cascaded_Voxel_Cone_Tracing_final.pdf
- SEGI, Sonic Ether (open-sourced; repository last pushed 2018): https://cgpress.org/archives/segi-voxel-based-gi-for-unity-now-open-source.html
- Lumen technical details (Epic docs): https://dev.epicgames.com/documentation/unreal-engine/lumen-technical-details-in-unreal-engine; Lumen in Fortnite Chapter 4 (Epic tech blog, 2022): https://www.unrealengine.com/tech-blog/lumen-brings-real-time-global-illumination-to-fortnite-battle-royale-chapter-4
- Godot rendering priorities, January 2024 (HDDAGI): https://godotengine.org/article/rendering-priorities-january-2024/
- Global Illumination Based on Surfels. Halén et al., EA SEED, SIGGRAPH 2021 Advances. https://www.ea.com/seed/news/siggraph21-global-illumination-surfels
- Radiance Cascades. Sannikov, 2023 (the paper is a preprint; overview): https://80.lv/articles/radiance-cascades-new-approach-to-calculating-global-illumination
- Split Radiance Cascades: Real-Time Global Illumination via Sparse Radiance Probes. Freeman, Sannikov, Pitt-Francis, arXiv, 2026. https://arxiv.org/pdf/2607.20384
- Light Propagation Volumes in CryEngine 3. Kaplanyan, SIGGRAPH 2009 Advances course. https://advances.realtimerendering.com/s2009/Light_Propagation_Volumes.pdf
- Cascaded Light Propagation Volumes for Real-Time Indirect Illumination. Kaplanyan, Dachsbacher, I3D 2010. https://www.realtimerendering.com/blog/cascaded-light-propagation-volumes-for-indirect-illumination/
- A Fast and Scalable Two-level Radiance Caching Scheme for Real-time Global Illumination (GI-1.0). AMD, 2022. https://gpuopen.com/download/GPUOpen2022_GI1_0.pdf
- NVIDIA SHaRC (RTXGI 2.0): https://github.com/NVIDIA-RTX/SHARC

Social VR and user-made worlds:
- VRChat world lighting guides (community): https://uhiyama-lab.com/en/notes/vrchat/world-light-probes/ and https://uhiyama-lab.com/en/notes/vrchat/world-lighting-bake/
- VRC Light Volumes (REDSIM, 2025): https://github.com/REDSIM/VRCLightVolumes; https://wiki.vrchat.com/wiki/Community:VRCLightVolumes; Toon Standard support in SDK 3.8.1: https://feedback.vrchat.com/sdk-bug-reports/p/sdk-381-beta4-support-vrc-light-volumes-in-toon-standard
- The Future Is Bright: Updating Lighting Systems in Roblox. Kapoulkine, 2019. https://medium.com/@zeuxcg/the-future-is-bright-updating-lighting-systems-in-roblox-27dcc85cc391; Unified Lighting (Roblox DevForum, 2025): https://devforum.roblox.com/t/let-there-be-unified-light-unified-lighting-is-fully-live/3401512
- Learning from Failure: a Survey of Promising, Unconventional and Mostly Abandoned Renderers for "Dreams PS4". Evans, SIGGRAPH 2015 Advances. https://advances.realtimerendering.com/s2015/AlexEvans_SIGGRAPH-2015-sml.pdf
- Meta Horizon Engine at a glance (Meta developer blog): https://developers.meta.com/horizon/blog/studio/meta-horizon-engine-at-a-glance/; community creator manual: https://mhcpcreators.github.io/worlds-documentation
- UEFN lighting and Lumen quick start: https://dev.epicgames.com/documentation/fortnite/lighting-and-lumen-quick-start-guide-in-unreal-editor-for-fortnite; Lumen performance guide: https://dev.epicgames.com/documentation/unreal-engine/lumen-performance-guide-for-unreal-engine
- Second Life: content creation meeting notes on reflection probes (2022): https://modemworld.me/2022/05/20/2022-ccug-meeting-week-20-summary-reflection-probes-update/; light probe request (2025): https://feedback.secondlife.com/feature-requests/p/support-for-light-probes-separate-from-reflection-probes; probe update bug: https://feedback.secondlife.com/bug-reports/p/megaprim-probes-stop-all-probe-updates
- Resonite issues: https://github.com/Yellow-Dog-Man/Resonite-Issues/issues/220 (light baking) and https://github.com/Yellow-Dog-Man/Resonite-Issues/issues/5140 (light probes)
