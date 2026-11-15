# Live bounce light (GI) in the renderer fork (design; "GI test 1" ran 2026-11-14: panels stalled; "GI test 2" installed 2026-11-14 12:10, never run; "GI test 3" (GI test 2 plus glow rays and thin walls read in front of themselves) installed 2026-11-14 13:52, not yet run in game)

2026-11-13. Lenki: Resonite forces all lighting to be realtime, nothing can be baked, so worlds look flat ("placeholders"); the
game is CPU-bound. Of the realtime options offered, Lenki picked "3 first and we'll see how it performs": live bounce light in
the renderer fork, measured offline first, then in game after asking to install. See also memory: resonite-lighting-findings.

## What it does

Light bounces once more each frame, on the GPU: walls take on each other's colour, rooms get darker where the sky can't reach,
glowing things light what is near them. It changes only Unity's ambient term (the light probe part of the lit shaders). Direct
light, shadows and reflections stay Resonite's. No shader of Resonite's is changed: the lit shaders already sample a light probe
proxy volume per pixel when `unity_ProbeVolumeParams.x` is 1, and the fork hands them our volume through property blocks.

## The pipeline

Grid: 128 x 48 x 128 voxels of 25 cm around the head (32 x 12 x 32 m), probes every 2 voxels (64 x 24 x 64, 50 cm). It
re-centres in whole probes when the head is more than a quarter of the grid from the middle; a jump of the whole grid starts over.

Each frame (main thread: `SoftbodyGIHost.Update`, from `RenderingManager.HandleUpdate` next to the GPU fluid, only reads the
settings and sweeps the renderers; steps 1 to 3 run at the end of the frame, after its cameras, in `SoftbodyGIHost.EndOfFrame`:
see "In game" below for why):
1. `GiPlanner` plans the frame: one slab of 8 z-slices to voxelize, 16 slices to light again, 4096 probes x 32 rays to trace.
2. `GiUnity` draws the slab with its own orthographic camera and `SoftbodyGIVoxelize.shader` as the replacement shader: each
   triangle is laid flat along the axis it faces most (geometry shader) and each pixel adds its albedo, glow and normal into
   the slab buffer (a Unity ComputeBuffer bound as random-write target u1). Avatars (skinned meshes) are hidden from it by
   default. Mirrors, portals and Gaussian splats skip this camera (`GiUnity.Voxelizing`).
   - A replacement shader gets the drawn material's values. A texture that material's shader does not have is Unity's grey
     (0.21 in linear colour) with a zero `_ST`: only textures with a non-zero `_ST` count, and a colour the material lacks
     ((0, 0, 0, 0)) counts as white. (Before this, white walls came out at a fifth of their colour: `_MainTex` x `_Tex`, one
     of them grey. Editor read-back, 2026-11-14.)
   - A face right on a voxel boundary (floors at y = 0, walls at round metres: most worlds) goes into the voxel in front of it
     (a hundredth of a voxel along its normal). Before, rounding split it between both sides, so a thin wall's front and back
     mixed at random and left blotches.
3. It packs the world's ambient (`RenderSettings.ambientProbe`, as Unity's shader constants) and up to 64 of the world's lights
   (directional first, then by brightness near the head), and queues the frame on the render thread. At most one frame waits;
   while one does, the main thread skips planning and drawing.
4. Render thread (`GiGpu`, `GiCompute` on Unity's own D3D11 device): resolve the slab into voxels, light voxels again (direct
   light with voxel shadows + last frame's bounce), trace probes against the voxel bricks (sky where rays escape), blend them
   (hysteresis, at least 15% new), and write the volumes; then copy them into Unity's 3D render textures. In the game the
   frame goes through `AssetIntegrator.EnqueueRenderThreadProcessing` (queued at the end of a frame, it runs in the next
   frame's `RunRenderThreadUploads`); the editor tests use `EditorRenderThread`
   (`GL.IssuePluginEvent`). Running it on the main thread instead raced Unity's render thread on the immediate context and
   crashed the NVIDIA driver (editor, 2026-11-14).
   - Fireflies: a ray brings at most 8x the light already there (the most of the probe's running value, its six neighbours'
     and the round's median ray). A tiny, very bright thing hit by one ray in 32 left flickering coloured blobs; now a rare hit
     adds a quarter at most. A probe right by a glowing patch still takes its light in and lifts its neighbours, so the light
     spreads out (offline `glow` and `spark` tests). Cost about 0.07 ms.
   - L1 rings: `WriteVolume` keeps |a1| <= 0.8 a0 per channel, so a probe lit hard from one side does not go below nothing on
     the other.
   - Glow rays (GI test 3; RESEARCH-gi.md item 5): besides its 32 rays, each probe aims `glow_rays` (gi.ini, default 4, at
     most 8) shadow rays at glowing voxels. `BuildBricks` keeps each brick's summed glow and its glow-weighted middle;
     `ListGlow` lists the bricks that glow. A probe weighs up to 1024 of them (a random window when more glow) by glow over
     distance squared, one reservoir per lane, merged; each shadow ray takes one brick, a voxel in it by its glow, and a point
     on a face of that voxel turned toward the probe. A ray that hits glow and the shadow rays share it by the power heuristic
     (MIS), so nothing counts twice; with the balance heuristic a rare ray that hit a small lamp still brought a quarter of its
     light in one round, and the lamp flickered 8% (offline `lamp`). The firefly cap applies to the 32 rays only: a small bright thing now lights its room at
     its real strength, and steadily. The voxels keep only the light they reflect (`Inject`); the trace adds the glow.
5. The renderers read the volume through `unity_ProbeVolumeSH`, `Min`, `SizeInv`, `WorldToObject` and `Params`, all in each
   renderer's own block. Shader globals do not work: Unity binds its own probe volume (a grey default) for every draw
   (GiProbeTest C8 to C10 gave a uniform grey; C4, C5, C5b, C7b work). Setting 2000 blocks costs 1.19 ms (C11).
   - Thin renderers (GI test 3, `GiLeak`; RESEARCH-gi.md item 1): a renderer whose bounds are at most 0.6 m along an axis and
     at least 1 m along another (walls, floors, ceilings, beams) reads the volume `leak_offset` (gi.ini, 0.5 m: a whole probe
     spacing, so both probe planes it reads are in front) away from itself along each thin axis, on the head's side:
     `Params.y` = 1 and a translation in `unity_ProbeVolumeWorldToObject`, which every lit program of the game already reads
     (scan of 2026-11-14). The face you look at no longer takes light from probes in or behind the wall. 27 shared blocks (one
     per direction, and none), so no block per renderer. `FollowHead` turns a thin renderer to the head's side when the head
     crosses its middle (a few float tests each a frame; a block only when one turns) and checks 32 thin renderers' bounds again
     each frame (they may move).
6. The same block zeroes the ambient's quadratic part (`unity_SHB*`, `unity_SHC`; a block's values win over Unity's even
   with BlendProbes, GiProbeTest C12), and the volume holds the whole light to L1. With the sky's quadratic part added on top,
   faces it dims went below nothing indoors (the tall block's front came out black). The outer probes fade to the sky's L1
   part. Renderers that do not reach into the window past that fade (`GiUnity.Covers`) get no block and keep the plain
   ambient exactly, so far away the world looks as before.

A re-centring is a pass over every renderer (the sweep), and the old window stays written until two whole passes are done.
A world's own MaterialPropertyBlocks override a renderer's block, so they get the same values (`PrepareShared` before
`MeshRendererManager` sets one; the sweep also fills the renderer's per-material copies). A block cannot drop a value again,
so a world's block the GI wrote into is marked (`_SoftbodyGIMark`) and gets the plain ambient's quadratic part back when the
GI is off or the renderer is outside.

The sweep: a few hundred renderers a frame (4000 checks, 300 sets), each stamped with the epoch it got (`Renderable.SoftbodyGIEpoch`);
whether a renderer reaches into the window, and whether it is thin, is decided then (`GiUnity.ApplyAt`, with the head). A world's
shared block set on a thin renderer gets its offset too (`PrepareShared` with the renderer). On a world change everything goes
back to the plain ambient until the new world's first window is written.

## Files

- `GpuGI/Shared/GiTypes.cs`: config, lights, sky, planner (engine-free).
- `GpuGI/Hlsl/SoftbodyGI.hlsl` + `GpuGI/D3D/GiCompute.cs`: the GPU side (SharpDX, run-time compiled like the fluid's kernels).
- `GpuGI/Unity/GiUnity.cs`: the Unity side without Resonite (also compiled in the SoftbodyShaders editor project).
- `GpuGI/Fork/SoftbodyGIHost.cs`: Resonite's side (fork only).
- `GpuGI/Shaders/SoftbodyGIVoxelize.shader` -> `softbody_gi.bundle` (SoftbodyShaders/build_gi.ps1, then fix_d3d11_programs.py).
- `GpuGI/gi.ini`: settings, read again within a second of a save (enabled, avatars, voxelize_every, stats; GI test 3 adds
  glow_rays and leak_offset).
- `GpuGI/tests`: the offline GPU checks (`dotnet run -c Release`, or name tests: sky floor volume room glow spark lamp panel
  leak shift time).
- `SoftbodyShaders/build_gi.ps1 -Leak` (`GiShaderBuild.Leak`): a thin wall lit only by a ramp written into the volume; its
  lookup offset must move what the game's own PBS shader reads, and turn with the head.
- `SoftbodyShaders/build_gi.ps1 -Probe` (`GiSyncProbe`: which of the voxel camera's calls wait for the render thread, in the
  editor) and `-Pacing` (`GiPacingProbe`: where in Unity's frame the draw waits, and the loading a Resonite-like frame keeps;
  a standalone player).
- Fork hooks, all under `#if SOFTBODY_GI` (`-c ReleaseGI`, its own bin folder): RenderingManager (Update), MeshRendererManager
  (PrepareShared), Renderable (the epoch field), CameraPortal and GaussianSplatRendererManager (skip the voxel camera).
- Install: `python make_softbody_renderer.py --gi --park <label>` (Resonite closed; keeps the old fork as
  RendererSoftbody.before-<label>).

## Safety

- The voxelize shader is used only after it drew a test square far below the world into the slab buffer and read it back
  (`GiUnity.StartSelfTest`: 64 samples, white, normal -z). A "D3D shader create error" or "ShaderProgram is unsupported" line
  in the renderer's log turns it off (`GiShaderWatch`), as the smoke does since 0.29.2.
- Not Direct3D 11, no bundle, a failed compile, or a GPU error: the bounce light stays off and the log says why.
- `enabled = 0` in gi.ini sends every renderer back to the plain ambient within a few frames.

## Measured so far

- GPU side alone (offline, RTX 4070, full grid): median 0.3 to 0.8 ms, p95 1.2 to 2.2 ms a frame depending on the GPU's clock
  state (same build, different days); render-thread CPU 0.02 ms. 24 offline checks pass (2026-11-14).
- Editor, Cornell box with the game's own PBS shader (2026-11-14): main thread 0.10 ms a frame (the slab draw 0.08 of it),
  render thread 0.02 to 0.03 ms, GPU 0.08 to 0.25 ms. Bounce light and colour bleeding on the ceiling and floor, darker
  tucked-in corners; "off" gives back the exact plain picture. PNGs in `GpuGI/tests/scene` (`compare_PerRenderer.png`;
  `compare_PerRenderer_corner.png` looks over the short block at the glowing cube it hides: its glow sits higher and further
  from the corner than real light would, because the floor and walls there take half their light from probes on their far side).
- The bundle builds in the 2019.4.31 editor and converts to the game's D3D11 program header (v1); the self-test passes
  (64 samples, white, normal -z).
- GI test 3 (2026-11-14 13:40): 34 offline checks pass (RTX 4070); the fork builds (`bin\GI-test-3`, Renderite.Unity.dll
  sha256 c421dff2...); the editor scripts compile as C# 7.3 against the 2019.4.31 DLLs.
  - `lamp` (one glowing voxel in a black room): the analytic light to 1%, from its direction, 0% flicker over 60 frames. The
    rays alone get none of it (the cap) and flicker 334%.
  - `panel` (a 2 x 2 m glowing panel): within 7% of the analytic light at 0.75 to 3.75 m. The rays alone: 1% at 0.75 m, 23%
    low at 1.75 m, almost nothing at 3.25 m (the cap).
  - `leak` (two rooms, lamp in one, wall 0.5 m): the dark room's face of the wall read 0.67 of the lit room's light with the
    plain lookup, 0.00 read in front; the lit face 2.01 plain, 2.70 in front.
  - `spark` and `glow` as before. `time`: GPU median 0.69 ms with 4 shadow rays, 0.74 ms without (the clock's noise is larger
    than their cost); render-thread CPU 0.23 ms.
  - Editor (`build_gi.ps1 -NoBuild -Scene -Leak`, 13:40): the self-test passes. Cornell box: main thread 0.091 ms a frame (the
    slab draw 0.079), render thread 0.027 ms, GPU 0.146 ms; 5 thin renderers (the walls, floor and ceiling); "off" gives back
    the plain picture exactly. The glowing cube now lights the floor round it and the back wall behind it at its full
    strength, and the room comes out warmer (`compare_PerRenderer_corner.png`; GI test 2's glow sat higher, and further from
    the corner). Near a light the moved lookup reads a little nearer the light than the surface is: the back wall's patch
    behind the cube is bright and, at 50 cm probes, a little square.
  - `-Leak` passes: with the volume overwritten by a ramp, the game's own PBS shader showed x 1.411 and x 0.496 on the two
    faces of a thin wall where the ramp gives x 1.417 and x 0.500, with the same scale on both faces, and FollowHead turned
    the wall to the head's side. So the moved lookup reaches the game's shaders through the block.

## In game

GI test 1 (installed 2026-11-14 11:29; desktop, 144 Hz): opening any panel stopped for about 5 s, where it used to open
smoothly. The renderer log gave the slab draw 5.1 to 6 ms a frame (spikes to 11 ms) while the main thread was mostly idle, and
the GPU side 0.2 to 0.5 ms.
- Cause: the first camera draw of a frame takes Unity's frame-pacing wait (the last frame reaching the screen). No single call
  is slow: in the editor nothing waits (`build_gi.ps1 -Probe`). From Update, the slab draw was the frame's first draw, so the
  wait moved into the GI hook, just before `RunAssetIntegration`. Its loop is the only place the main thread integrates assets,
  and it runs only while the renderer waits for the engine's frame. That frame came in during the wait, so loading got almost
  no time.
- `build_gi.ps1 -Pacing` (a standalone player, vsync at 144 Hz, 2026-11-14), the slab draw's median: Update 6.57 ms,
  LateUpdate 6.57, the main camera's onPreCull 0.10, onPostRender 0.09, the end of the frame 0.09; a plugin event in Update
  (as the fluid and the uploads issue) 0.00. A Resonite-like frame (an engine thread with 4 ms of work a frame, loading only
  while waiting for it): loading 4.06 ms a frame without GI, 0.00 with the slab drawn in Update, 4.01 with it drawn at the end
  of the frame.
- GI test 2 (installed 2026-11-14 12:10, replaced by GI test 3 at 13:52 before it ran): the slab draw and the GPU's share
  run at the end of the frame (`EndOfFrame`); Update keeps the settings, the sweep and the stats line, which now gives both
  ("update … ms; end of frame … ms (slab …)").
- GI test 3 (installed 2026-11-14 13:52, not yet run in game): GI test 2 plus the glow rays and the thin-wall offset; the
  stats line adds "N thin (M turned so far); glow rays X, leak offset Y m".

## Known limits

- Light leaks through thin walls: a surface halfway between two probe planes (a floor at y = 0 lies between probes at -0.25
  and 0.25) takes half its light from the probes on the far side. Unity's proxy volume has no visibility weighting. GI test 3
  moves the lookup of axis-aligned thin renderers in front of them; still leaking: walls not along the axes, one mesh holding
  many walls (a whole imported building), walls a single voxel thick that are lit from both sides (their voxels mix the two),
  and a strip of about 15 cm where a floor meets a wall. The face turned away from the head reads the head's side, which a
  mirror or a camera behind the wall can show.
- A renderer moving into the window while the head stays still keeps the plain ambient until the next re-centring (its
  in-or-out is decided once an epoch). A huge mesh reaching into the window loses the sky's quadratic part on its far parts.
- Small, very bright glowing things light their surroundings less than they should beyond a metre or so (the firefly cap).
  GI test 3's glow rays fix this offline (`lamp`, `panel`); not yet seen in game.

## Still to test

In game (GI test 3 is installed; it carries GI test 2's end-of-frame draw): panels open as smoothly as without GI; the
"update" and "end of frame" costs in real worlds; property blocks and dynamic batching; light leaks; how it looks.

GI test 3 (offline and editor checks pass; installed 2026-11-14 13:52): in game, glowing things steady and bright enough;
walls and floors next to a lit room no longer glowing through; the stats line's "thin" count and turns. `glow_rays = 0` and
`leak_offset = 0` in gi.ini give GI test 2's light back within a second, to compare.

Ideas for later: per-probe light reservoirs if glow still flickers (RESEARCH-gi.md item 7), a least-squares fit of the probes
for leaks the offset can't reach (item 2), area lights, a second cascade for far light, avatars voxelized at a lower rate
instead of hidden.
