# Gas (particle smoke), 0.26.0: design

Lenki, 2026-11-11, after 0.25.2: "Since fluid is in the game would volumetric particles be possible? (Like a super simple gas
sim?)". Two options were offered: particle smoke, or volumetric gas on a 3D grid drawn by Resonite's `VolumeUnlitMaterial`.
Lenki picked option 1, particle smoke. The volumetric gas can come later.

## What it is

- Puffs of gas are GPU particles in the renderer fork. Each puff:
  - rises with buoyancy;
  - slows with drag;
  - swirls in a divergence-free curl-noise field;
  - is pushed out of colliders, and dragged along by moving ones (a hand waved through smoke carries it);
  - grows from StartSize to EndSize and fades in and out over its Lifetime.
- Puffs do not interact with each other (v1). The GPU only; without the renderer fork a GasEmitter says so and does nothing.
- **Drawing:** each live puff is a disc of 7 vertices (a centre and 6 rim vertices; 6 triangles) facing the local user's head.
  - The centre has the puff's colour and opacity; the rim has opacity 0, so vertex colours make it soft and round with no
    texture.
  - The material is Resonite's stock `UnlitMaterial` (UseVertexColors, BlendMode Alpha, ZWrite off, double sided).
  - The plugin attaches it to a local placeholder mesh, and the renderer draws the GPU buffers with it, as the fluid surface.
  - Not sorted (v1): fine for soft, faint puffs.
  - Discs face the head, not each eye, and look tilted in mirrors. Unity's own billboard path (`UseBillboardGeometry`) feeds
    per-billboard sizes from Unity's particle system, which we cannot see from here.

## Pieces

- **Channel (version 7, `GpuFluid/Shared/FluidChannel.cs`):**
  - header: GasEmitterCount, GasMeshAssetId, HeadX/Y/Z;
  - a region of up to 64 `GasEmitterRecord`s (128 bytes) after the colliders;
  - FluidBack: GasPuffs, GasVertices, GasDrawing, gas bounds.
- **GPU (`GpuFluidSim`, kernels in `FluidSim.hlsl`, sharing its constants and shapes):**
  - kernels: `GasSpawn` (a ring of slots), `GasUpdate`, `GasDraw` (appends vertices and indices with atomics), `GasTail`;
  - buffers: GasPos (xyz, age; dead < 0), GasVel (xyz, lifetime), GasEmit (emitter index), GasParams (per emitter),
    GasVerts / GasIndices (40-byte vertices as the surface), GasState (counts, bounds);
  - `StepGas` runs every frame (not skipped when the liquid sleeps).
- **Runtime (`GpuFluidRuntime`):** a gas director turns gas emitter records into spawns (rate x dt, in the nozzle, along the
  emitter's forward, plus its velocity). Colliders are gathered around the liquid's and the gas's bounds together.
- **Renderer host (`SoftbodyFluidHost`):** a second Unity mesh and child renderer for the gas placeholder (found by
  GasMeshAssetId); after the step its buffers are copied into it.
- **Plugin:**
  - a `GasEmitter` component: Emitting, Rate (puffs/s), Speed, NozzleRadius, Lifetime, Buoyancy, Drag, Swirl, StartSize,
    EndSize, Color (alpha = opacity), Collide toggles, MaxPuffs, CullDistance, and a Status;
  - `FluidWorld` registers gas emitters with the liquid's, writes their records, the head position and the gas mesh, and keeps
    a local "Softbody gas" slot with the placeholder mesh and its UnlitMaterial.
  - A new component changes the compatibility hash: everyone in a session needs 0.26.0.
- **Tests:** offline GPU puff rise and lifetime, collisions (none inside a box above the emitter), a moving sphere drags puffs,
  draw counts (7 vertices and 18 indices a live puff), no NaN.

## As built (0.26.0, 2026-11-11)

- **Kernels:** GasClear (once), GasBegin, GasSpawn, GasUpdate, GasDraw, GasTail. They run every frame after the liquid's
  substeps (also when the liquid sleeps or is skipped), with the frame's real time (at most a 30th of a second), not the
  liquid's fixed substeps. The shapes push puffs out at 3 cm (the liquid's collision radius is swapped for that pass and put back).
- **Swirl:** the curl of two octaves of drifting sines (divergence-free). Moving shapes within 0.3 m add their velocity to the air.
- **FluidBack:** GasPuffs, GasDrawing and the gas's bounds (no vertex count).
- **GasEmitter** also has Spread (0..1) and WorldPuffs (all the world's live puffs; the renderer does not count per emitter).
  Defaults: 30 puffs/s, 0.4 m/s, 3 cm nozzle, spread 0.15, 5 s, buoyancy 0.5, drag 1.5, swirl 0.3, 0.1 to 0.6 m, a faint
  grey at opacity 0.25, collides with everything, MaxPuffs 2000, CullDistance 30 m. The world holds 32,768 puffs (a ring:
  past it the oldest go first).
- **FluidWorld:**
  - A world with only gas runs on the GPU too.
  - It has no liquid renderer while it has no FluidEmitter (its refraction would cost every frame).
  - The gas renderer is kept once made, so the last puffs live out their lives after their emitters go.
  - An emitter's velocity comes from its slot's motion between updates (not across a jump of over half a metre a frame).
  - If a FluidEmitter asks for the CPU, the whole world is on the CPU and the gas is off; its Status says why.
- **Director:**
  - A stopped or removed emitter's collide mask is kept with its slot (a minute), so its last puffs still collide.
  - The colliders' box is clipped to the liquid's kill box joined with 50 m around the gas emitters, so a world with only gas
    is not clipped to an old liquid's box.
- **Tests** (`tests/GasTests.cs`, `gas` on its own, 21 checks, RTX 4070):
  - A column at 300 puffs/s for 2 s keeps 594 alive (the GPU counts the same), rises to 0.97 m, none into the slab above it.
  - 593 of 594 drawn, 7 vertices and 18 indices each, centres at the gas's opacity, clear rims, facing the head.
  - About 0.24 ms of GPU a frame with no liquid.
  - Stopped: none left after 2.6 s, still kept out of the slab.
  - A hand swept through the column: 0.59 m/s against 0.06 without.
  - A heavy gas sinks to a mean 0.15 m, never through the floor.
  - A moving emitter's young puffs carry 0.87 of its speed.
  - Water and smoke together: the water still rests at its own 1.25 cm radius.
  - The director's rate cap, slots and NaN guard, and the channel's gas block.
- **Plugin check:** GasEmitter's 20 fields in order, guarded callbacks, the woven members and the new compatibility hash.

## 0.27.0: vortex particles

`GasEmitter.Vortices` (0 = off, the default): a hand or an object moving through the gas sheds small vortex rings that travel and
keep stirring the smoke for a few seconds. The design, the per-emitter rule and the tests are in DESIGN-fluid.md, "0.27.0"; the
prototype and its measurements are in SoftbodyRnD's RND-REPORT.md, section 3.

## 0.27.0: the smoke shader

Lenki, 2026-11-12: "I do think we need a better volumetric material than resonites base 'unlit' one", and picked a custom shader
in the renderer fork (over a raymarched 3D volume). Resonite loads shaders only from Unity-built AssetBundles; Unity batch mode
is not licensed on Lenki's PC, but the windowed 2019.4.31f1 editor builds bundles through the signed-in Hub (a window shows for
about half a minute).

- **The shader** (`GpuFluid/Shaders/SoftbodySmoke.shader`, "Softbody/Smoke"):
  - each puff is a quad whose 4 vertices all hold its centre; the vertex shader turns it to face each eye (single-pass stereo's
    own camera position and view), so both eyes, mirrors and cameras see it right (0.26.0's discs faced the head);
  - the pixel shader makes it a soft round puff broken into wisps by a 3D noise (two scales) that changes over its life, and turns
    it by its own angle;
  - lit as a soft ball (the light wraps round it; against the light its thin parts glow: Henyey-Greenstein), by the world's main
    directional light (ForwardBase), the sky (spherical harmonics), up to 4 vertex lights, and other per-pixel lights (ForwardAdd,
    added on);
  - darkened by the smoke between it and the main light and the sky (from the GPU, below);
  - faded where it meets a surface when the camera renders a depth texture (`_SoftbodyDepthOK`, set per camera by the fork), and
    next to the eye (walking through smoke);
  - premultiplied alpha, no depth writes, both sides, Unity fog.
- **The bundle** (`softbody_smoke.bundle`, built by `Testresolink\SoftbodyShaders\build.ps1`): the shader, a 32³ tileable noise and
  two materials (smoke; spray and foam: rounder, hardly wispy). The build turns VR on with single-pass stereo as the game's renderer
  project does (so the shader gets its `UNITY_SINGLE_PASS_STEREO` variants), keeps every fog mode, and logs each variant it compiles.
  `build.ps1 -Preview <frame> -Png <file>` renders a frame dumped by the tests (`gas-preview`) to a picture.
- **The GPU** (`FluidSim.hlsl`, with the shader: `_S0.w` 1): `GasSplat` counts the live puffs and their bounds, puts each puff to
  draw in a bucket of its log distance from the head (1024 buckets: about 1 cm apart at 1 m) and spreads its extinction
  (sigma = -ln(1 - alpha) / 2r, times its volume) over the 8 cells around it on a hashed grid (262,144 cells of 0.72 of the
  largest emitter's mean puff size, so a line through a puff reads back about its own optical depth); `GasBucketScan` turns the
  buckets into starts; `GasDrawQuads` writes each puff far to near, with the light reaching it (a march of 12 steps, each a
  quarter longer, towards the main light) and the sky reaching it (6 steps up); `GasTailQuads` clears what is past it. Without the
  shader the discs are drawn as before.
  - **Tuning (from the previews):** first try, the plain e^-tau of a dense plume (900 puffs) left half its puffs under 0.1 % of the
    light and every sky term at its floor: a dark grey smudge. Now each puff counts 0.6 of a full sphere of its opacity (the
    shader draws it softer: `GAS_DRAWN_SHARE`), and the shading sees a quarter of the optical depth (white smoke scatters on most
    of the light it stops: `GAS_SCATTER`): light 0.6 e^-tau + 0.4 e^-tau/4, sky 0.4 + 0.6 e^-tau. In that plume the bottom third
    now gets 0.08 of a light from above and the top third 0.39.
  - **Wisps:** the noise eats into each puff from its edge (where it is low the thin outer parts go, the middle stays), at about
    5 noise cells across a puff; multiplying by it (the first try) averaged away over hundreds of overlapping puffs.
- **The vertex** (56 bytes, the gas's and the foam's): position (the centre), normal (radius, light, sky), colour (rgb, opacity),
  texcoord0 (corner, a random number from its slot, its age as a share of its life). The foam's quads are unshaded (light and
  sky 1). A switch between discs and quads clears the index buffer.
- **The renderer** (`SoftbodyFluidHost`): loads the bundle from `StreamingAssets\Softbody` (`make_softbody_renderer.py` copies it
  there) the first time a world has gas or foam; draws the gas and foam with its materials, else with the plugin's Unlit; finds
  the brightest directional light on the gas's layer twice a second and sends the direction to it in the gas's space; sets the
  gas mesh's bounds to the gas (3 m round, from the readback and the emitters) so Unity picks the lights near it.
- **Nothing in the plugin changed** (no new fields, same compatibility hash): friends without the new fork see the old discs.
- **Tests** (`smoke`, also at the end of `gas`; the foam's in `foam`): quads hold their centre, radius, light, sky, colour, corner,
  random number and age; far to near from the head; light from above shades the bottom of a column (and from below the top);
  no main light, no shading; discs then quads leave no disc indices; the foam as quads. RTX 4070 (with a game open): 849 puffs
  with their density grid, order and shading in 0.21 ms of GPU a frame, every quad in order.
- **The build:** 112 variants (56 for single-pass stereo), no shader warnings; the bundle is 130 KB.
- **Not verified in game yet:** that the bundle (Unity 2019.4.31) loads in the game's 2019.4.19 renderer, that single-pass stereo
  draws both eyes right, Resonite's ambient (its skybox's spherical harmonics) on the smoke, and the depth texture's soft edges.
  (It did not draw at all in game: see 0.29.2 below.)

## 0.29.2: the smoke shader on the game's renderer

Tried in game on 2026-11-12 at 22:53, with 0.29.0. The bundle loaded and the fork logged "smoke shader loaded". Then Direct3D
refused every program: "D3D shader create error for vertex shader [0x80070057]", then "ShaderProgram is unsupported, but because
jobified rendering is enabled the ShaderProgram can not be removed." on every frame that drew smoke. By 00:47 that was 1.2 million
lines and a 168 MB Player.log, and no smoke showed.

- **Why:** Unity puts a small header before the DXBC container in each D3D11 program's code.
  - The editor that builds the bundle, 2019.4.31, writes version 2: the byte 2, five bytes as before, then 32 more bytes (all zero
    here).
  - The game's renderer, 2019.4.19, knows only version 1: the byte 1, the same five bytes, then the DXBC. All of the roughly 10,900
    programs in its own resources.assets look like that.
  - So it handed Direct3D the 32 zero bytes as the start of each shader.
  - Nothing else differs:
    - the Shader, Material and Texture3D type trees hash the same;
    - the bind info after the code reads the same;
    - each program's DXBC creates on D3D11 when passed in directly.
  - Read the way 2019.4.19 reads them, all 112 programs fail with 0x80070057 offline too, the game's error.
- **The fix:** `SoftbodyShaders\fix_d3d11_programs.py` rewrites each program to version 1. It drops the 32 bytes (which must be
  zero) and shortens the code and its sub-program by 32. Then it rebuilds the blob's table, recompresses the blob (LZ4), and writes
  the bundle back.
  - It stops on anything it doesn't recognise, and leaves version 1 programs alone, so running it twice changes nothing.
  - `build.ps1` runs it after every build. `make_softbody_renderer.py` refuses a bundle that still needs it.
  - Checked:
    - rewriting with nothing to change gives back the same file;
    - after the rewrite, all 112 programs (56 vertex, 56 pixel) create on D3D11 the way 2019.4.19 reads them.
  - The bundle is now 168 KB (plain LZ4 instead of Unity's LZ4HC).
- **The fallback (fork), `GpuFluid/Unity/SmokeSelfTest.cs`:**
  - Before using the shader, the fork draws one test puff with a copy of the smoke material into a 32 x 32 render texture. A
    camera of its own sits far below the world and sees only the renderer's Temp layer, which no other camera draws.
  - It reads the picture back: the middle should hold the puff's opacity (0.5), the corners nothing. Only then does the fork use
    the shader. Otherwise the gas and foam keep the Unlit discs, and the log says why.
  - After that it watches the log. If the renderer reports that it could not make one of the shader's programs (a variant the
    test didn't draw, such as a stereo one), the fork goes back to the discs for the session (`DropSmoke`).
  - The editor showed that the "D3D shader create error" line only goes to the log file: scripts never see it. In game the watch
    relies on the "ShaderProgram is unsupported" line, which Unity logs as an error with a file and line, the kind scripts do get.
    Not yet seen working.
  - The cost: one 32 x 32 render and its read-back, once a session, when a world first has gas or foam.
- **Checked in the 2019.4.31 editor** (`build.ps1 -Check "a;b;c"`, with the fork's own test):
  - the bundle as built: test puff 0.49, corners 0.00 (2019.4.31 knows version 2);
  - rewritten: 0.49 and 0.00 (2019.4.31 reads version 1 too, and takes the LZ4 the script writes);
  - a copy with the version byte set to 1 but the 32 bytes kept (what the game's renderer sees): 0.00. The editor logged the
    game's own "D3D shader create error ... [0x80070057]" lines, and the test failed as it should.
- **Not verified in game yet:** the smoke drawing in the game's renderer, in both eyes. The fork logs the test's result
  ("smoke shader loaded ...; test puff: ...").
- Nothing in the plugin changed, so the hash is unchanged. The fork and the bundle go together: `make_softbody_renderer.py` copies
  both.

## Later

The volumetric version (a 3D grid sim, raymarched); puffs pushing each other apart; steam from hot water; self-shadowing for
the other lights; a glow (emissive) look for fire.
