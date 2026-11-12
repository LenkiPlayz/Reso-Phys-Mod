# Fluid: a GWater2-like liquid for Resonite (design)

Lenki's request (2026-11-10): a third physics type next to the lattice `Softbody` and the tet-shell `SkinnedSoftbody`.
It should be a real-time particle fluid in the spirit of GWater2 for Garry's Mod (github.com/meetric1/gwater2), which runs
NVIDIA FleX (position based fluids on the GPU) and draws the water with custom shaders.

Lenki's decisions so far:
- **Both a CPU and a GPU version.** Lenki picks the better one afterwards and deprecates the other or keeps it as the
  fallback. Lenki leans towards the GPU one.
- **First version:**
  - liquids are set per emitter, not by global sim settings;
  - an emitter / volume component;
  - users' hands and bodies push the water.

## What GWater2 and Gelly show

- Both are built on NVIDIA FleX, a unified particle solver; its fluids are Position Based Fluids (Macklin & Müller 2013).
- GWater2's features:
  - a water gun;
  - emitters, a hose, rain, a shower head, a drain, forcefields, a black hole and spawnable cubes of liquid;
  - foam and bubble particles, swimming, and reaction forces;
  - settings synced in multiplayer.
- Gelly's rendering idea is per-channel Beer-Lambert absorption for the colour (blood, water, "glunk"), plus roughness and
  specular transmission. Neither README gives solver numbers or render passes.

## Shared by both versions (engine side, `Plugin/`)

- **`FluidEmitter` component** on any slot:
  - it either pours, or fills a block of liquid once;
  - each emitter carries its own liquid, and its particles keep that emitter's liquid index.
- **Liquid settings per emitter:**
  - flow: viscosity, cohesion (surface tension), adhesion (stickiness to surfaces), friction;
  - density (relative: oil floats on water), gravity, damping;
  - lifetime and max particles;
  - look: colour (absorption), opacity, roughness;
  - collisions: collide toggles (world, own avatar, others; head, body, hands as for SkinnedSoftbody) and a cull distance.
- **Emitter settings:**
  - a mode (Pour or Volume), a rate in litres per second, a speed and a nozzle radius;
  - for Volume mode, a block size and a Refill button.
- **One shared simulation per world on each client** (`FluidRuntime`, not a component):
  - all emitters' particles live together, so liquids meet, mix and push each other;
  - local only: emitters and their settings are synced, but every client simulates its own splashes (as GWater2 does),
    and nothing synced is written.
- **Colliders:** the SkinnedSoftbody collider gathering, reused:
  - solid world colliders (CharacterCollider);
  - users' bone colliders and dynamic-bone spheres, so hands and bodies push the water, one way;
  - a box around the water, searched 10 times a second, with moving colliders swept through the substeps.
- **Particle size:** one resolution per world (default 2.5 cm), since every particle shares one neighbour grid. Everything
  else is per emitter.

## CPU version (`Core/Fluid/`, engine-free, tested offline)

- **Solver: Position Based Fluids, on the thread pool, one frame behind (as the softbodies):**
  - predict with gravity;
  - a spatial hash grid by counting sort;
  - 2 to 4 density iterations, with per-particle rest density (mass) and an artificial pressure term (s_corr) against
    clumping;
  - collision projection against the shared `FleshCollider` shapes (`XpbdSolver.Resolve`: sphere, capsule, box,
    cylinder, cone, triangle, mesh);
  - velocity update, then XSPH viscosity (per pair, from both liquids);
  - cohesion and surface tension (Akinci 2013), adhesion to colliders, optional vorticity confinement.
- **Data and threading:** structure of arrays, `Parallel.For` over chunks. Budget: about 10 to 20 thousand particles
  within a few ms on the worker.
- **Surface:** marching cubes over a smoothed density field, on a sparse grid around the particles, with vertex colours
  from the nearby liquids. Shown on a local mesh with Resonite's `RefractMaterial` (screen-space refraction) plus a glossy
  transparent PBS layer for highlights.
- **Debug:** a particle view (points coloured by liquid).
- **Tests:**
  - a dam break: volume kept, nothing escapes the tank;
  - the emitter rate;
  - honey flows slower than water;
  - a droplet holds together;
  - oil floats on water;
  - no particle inside a box collider;
  - a moving hand sphere pushes the water;
  - the surface mesh is closed and bounded;
  - particles per ms.

## GPU version (the renderer fork, `RenderiteSrc/Renderite.Unity/Fluid/`)

- **Solver:** the same PBF in compute shaders, sized for about 100 thousand particles:
  - grid build by counting sort;
  - density, lambda and position passes;
  - collisions against a collider buffer.
- **Rendering:** two candidates.
  - GPU marching cubes with `DrawProceduralIndirect`, as real geometry, which works for every camera and both VR eyes;
  - or screen-space fluid rendering (sphere depth, bilateral smoothing, normals, thickness, Beer-Lambert absorption,
    refraction), closer to GWater2's look but harder in VR.
- **Engine to renderer data:**
  - Resonite's IPC protocol cannot be extended from the engine side, so the Softbody plugin writes each frame into its own
    named shared-memory block (`Local\Softbody.Fluid.v1`): emitters (pose, liquid, spawn counts), colliders, and the
    world's render-space mapping;
  - the forked renderer reads it, simulates and draws;
  - a handshake (like the mesh patch's) tells the plugin the GPU fluid is there, otherwise it runs the CPU version.
- **Shaders:**
  - Unity cannot compile shaders at runtime, so the compute and water shaders go into an AssetBundle built with the Unity
    Editor (Lenki has 2019.4.31f1; the renderer is 2019.4.19f1, the same LTS line), loaded by the fork;
  - built from a small separate Unity project with no packages, so nothing is downloaded.
- **Testing:** not offline. The CPU version is the reference to compare it against.

## Status

- **2026-11-10: the CPU version (0.15.0) is built, with all 315 checks passing, not installed.** It has `Core/Fluid/`
  (FluidSim, FluidSurface), `Plugin/FluidEmitter.cs`, `Plugin/FluidWorld.cs` (with FluidColliders), `Plugin/FluidSurfaceMesh.cs`
  and `tests/FluidTests.cs`.
- Settings that held up in the tests:
  - 2 substeps and 3 iterations (1 substep with 4 iterations let particles through seams, and oil sank into the floor);
  - the density constraint uses number density, with corrections by inverse mass (mass-weighted density blew up the light
    liquid);
  - cohesion is attraction only, at 1.5 m/s² per neighbour at the spline's peak (the full Akinci spline at 60 exploded);
  - viscosity also raises the friction on colliders.
- Guards against blow-ups:
  - at most half a spacing per iteration;
  - a particle leaves a box the way it came;
  - no bounce;
  - at most 10 g of speed gained.

- **0.15.1 (the freeze in Lenki's first test):**
  - Cause: streams fell through single-sided mesh floors. The water at the kill height made the dense surface grid 100 m
    tall, and the world thread waited for each step.
  - Fixes:
    - swept triangle collision;
    - a kill box around the emitters;
    - a sparse surface grid (4^3 blocks on a world lattice, with a hashed particle grid);
    - mesh triangles taken per occupied cell (at most 8000), big shapes in the global list;
    - a world thread that never waits (joins and leaves applied between steps);
    - surface throttling.
  - Diagnosis tool: `tests/FluidPourDiag.cs` (`-- fluid-pour`).

- **0.15.2 (Lenki's notes: friction, MaxParticles, Refill):**
  - Causes:
    - friction and adhesion only applied when the last iteration pushed a particle out, but a resting particle is pushed
      out by the first one;
    - a `RawOutput` can only be written from its own component's update, so the status stayed empty;
    - a lowered MaxParticles removed nothing.
  - Fixes:
    - contact counts from any iteration (CPU and HLSL);
    - `FluidSim.Trim` (the oldest go first);
    - `Core/Fluid/FluidDirector`, the emitter logic, engine-free and tested offline (a cleared Volume stays empty until
      Refill);
    - the emitter writes its status in its own `OnCommonUpdate`.
  - 323 checks pass, same hash.

- **0.16.0: the GPU version (runtime D3D11, in the renderer fork).** Unity batch mode isn't licensed, so the HLSL is
  compiled at runtime with d3dcompiler_47 and run through SharpDX on Unity's D3D11 device on the render thread.
  - Kernels (31, all at most 8 UAVs, D3D 11.0's limit):
    - a particle is three float4s (its source rides in Pos.w and Pred.w, its age in Vel.w);
    - neighbours are found once per substep (at most 48), like the CPU's;
    - the sort is a counting sort over a hashed grid, with dead particles sorted to the end;
    - the surface uses the CPU's sparse 4^3 blocks, in a GPU hash table (keys 11/10/11 bits from the kill box's corner).
  - The driver (`GpuFluidSim`) dispatches the counts indirectly from the GPU's own particle count. It reads back 8 frames
    late at most, without waiting. It saves and restores the compute stage around each frame, so Unity's state cache stays
    right.
  - The director (`GpuFluidDirector`) counts what it poured since the last readback, so caps hold despite the lag. Sources
    are retired and replaced on Clear, Refill and removal (their particles all die in the next Predict). A lowered cap
    removes exactly the excess through a per-source counter.
  - The collider grid covers the readback's bounds, grown by how far the fastest particle can have travelled since (the
    tests let the GPU run far ahead and caught this).
  - The renderer copies the surface straight into a Unity mesh's native buffers (CopySubresourceRegion; they must be
    DEFAULT usage, which is checked at start). The mesh draws all its indices: changing its draw range could make Unity
    upload its own empty copy over the GPU's.
  - Box seams: a particle inside a box that was outside it when the substep began now leaves by its entry face (the last
    slab it entered). The GPU tests with 50k particles found water escaping under tank walls: the nearest face of the
    wall was its bottom, into the floor. Ported to the CPU (`FluidSim.BoxEntry`).
  - Each grid cell scan is capped at 128 particles, so a pile-up cannot run a kernel long enough for Windows to reset the GPU.
  - 0.16.0 installed with the rebuilt fork on 2026-11-10; not tried in game yet.

- **0.17.0: ParticleSize and SurfaceDetail** on the emitter. The world takes the largest of each among its emitters.
  - On the CPU, a new size rebuilds FluidSim and FluidDirector between steps. Detail sets FluidSurface.CellScale =
    0.8 / detail and MaxBricks = 24000 x min(detail^3, 4).
  - On the GPU, a new size makes the renderer rebuild GpuFluidRuntime (the liquid starts over). Detail is live: SetSurfaceDetail
    reallocates only the surface's blocks (32768 x min(detail^3, 8)).
  - The GPU lattice is now centred on the emitters: 1023 / 511 / 1023 blocks each way. Before, it was counted from the
    kill box's corner, and a finer grid's 11/10/11-bit keys would not have reached the far side.
  - Channel version 3 carries SurfaceDetail; the reply carries ParticleSize and SurfaceTruncated.
  - Tests (`-- fluid-size`): 5 cm particles hold a dam break at rest density and pour the right litres, CPU and GPU.
    Detail 2 gives about 3.9x the vertices, a flat pool and a closed drop, and the GPU keeps every particle through the
    change.

- **0.18.0:**
  - **The handshake deadlock:** in Lenki's first 0.17.0 session the liquid ran on the CPU. The renderer opened the
    handshake after its first step of a plugin frame, but the plugin only writes frames once the handshake exists. Now
    `SoftbodyFluidHost` queues an empty warm-up job (0 emitters, dt 0, with the copy into the Unity mesh and its usage
    check) once the kernels compile, then opens the handshake. The offline tests drove the runtime directly and could not
    see this; the host's protocol needs a game.
  - **DropletSize** (Lenki: "a particle combine radius so small droplets aren't entirely invisible or tiny"):
    - a per-particle radius from the neighbours within R: `t = smoothstep(n / 20)`, `r = rad * lerp(DropletSize, 1, t)`,
      with at least `0.9 * cell * (1 - t)`;
    - the field is Zhu–Bridson with weighted radii, `|p - x̄| - r̄` (r̄ weighted like x̄);
    - on the GPU this is the new `Radii` kernel after the surface sort (the `Rad` buffer); on the CPU, `FluidSurface.DropletSize`;
    - a lone particle's sphere (0.6 s) was under half a cell's diagonal (0.87 x 0.8 s), so some fell between grid points
      and vanished;
    - Lenki's screenshot was at ParticleSize 10 cm: a monolayer sheet with holes, and droplets as 8 cm voxel blobs.
  - Channel version 4 (DropletSize in the header).
  - **Thin box floors** (Lenki: 12,000 particles sank through a "very very thin box collider"):
    - the pile's pressure moved bottom particles more than the box's thickness in one substep, and the box test saw them
      wholly below;
    - fix: `FluidSim.BoxSwept` / HLSL `SweptBox`. A slab test of the substep's path (x0 to p) against the box itself; if
      it entered (and x0 was outside), p goes onto the entry face plus r, keeping its tangential motion. It replaces
      0.16.0's BoxEntry;
    - test `-- fluid-floor`: 11,638 particles on a 2 mm box, at 2.5 and 10 cm, CPU and GPU. Before the fix about half sank,
      from the landing frame.

## Build order (proposed)

1. Shared engine side and the CPU version, end to end: component, runtime, colliders, solver, surface, tests.
2. GPU version: shared-memory channel and handshake, compute solver, rendering, the shader bundle.
3. Lenki compares them in game. The better one stays, the other becomes the fallback or is removed.

- **0.19.0:**
  - **In game on 0.18.0, the GPU path worked:** handshake, DEFAULT-usage Unity buffers, drawing with the fluid renderer.
  - **RandomVelocity:** an even random vector in the ball of that speed, added at spawn (`FluidSim.RandomBall`, the
    director's `RandomBall`).
  - **Viscosity 0..20:** P = ceil(the largest live viscosity) XSPH passes per substep, each with c_ij / P (at most a
    full average). The extra passes run before VelocityB: CPU ping-pongs _dp/_vt; the GPU `Viscosity` kernel swaps
    Delta/Vtmp. `_Grav.w` = 1/P. Friction uses min(viscosity, 1).
  - **Surface overflow:** GpuFluidRuntime lowers the effective detail by sqrt(0.85 / vertex ratio), sqrt for the index
    ratio, or cbrt(0.85 / block ratio), using readbacks newer than the last change. It climbs back x1.1 while every ratio
    is under 0.5. Buffers are sized for the asked detail, the grid uses the effective one. CPU: `FluidSurface.NextDetail`
    (x0.8 when truncated, x1.1 back under 40 % of the blocks), once per build.
  - **Zeroed buffers:** ClearUnorderedAccessView on every new UAV buffer. D3D11 leaves them undefined, and a rebuilt
    simulation showed old index data as long spikes.
  - **Particle size debounce** in FluidWorld: 0.5 s, and 0 keeps the last size.
  - **Collision order:** moving shapes first (users' parts, or anything that moved over 2 cm/s), still ones last, and the
    grid's lists before the global one, on both CPU and GPU. FluidColliders now tracks world colliders' velocities.
    Tests: `FluidMovingBoxTests` (in `-- fluid-floor`) and `FluidV019Tests` (in `-- fluid-size`).

- **0.20.0: surface tension and a smoother drawn surface** (Lenki in game on 0.19.0, GPU, Cohesion and Adhesion about
  0.5 to 1): a puddle's edge broke into blobs, some particles "never merge into the main puddle", particles sitting on the
  sheet showed as cones with a dent around them ("weird forcefields"), and a filled cup showed lines.
  - **Cause, solver:** a puddle spread to one particle thick and tore into holes and blobs. In the sheet the particles
    squeezed to about 0.6 of their spacing (some to 0.3), because the density constraint only pushes when a particle is
    over rest density, which a sheet never is, and cohesion alone (attraction only, at 1.5 m/s² per neighbour) gathered
    it into clumps. A particle on top of the packed sheet had nowhere to go.
  - **Fix, solver:** Akinci et al. 2013 surface tension, after the extra viscosity passes and before the last one (CPU
    and GPU in the same order):
    - normals `n_i = h Σ ∇W_ij / max(ρ_j, 0.1 ρ0)` (CPU `_nrm`, GPU `Normals` kernel; ρ from the last density iteration,
      CPU `_rho`, GPU `Rho`);
    - cohesion (still attraction only) and the curvature term `-Σ (n_i - n_j)`, both times `K_ij = 2 ρ0 / max(ρi + ρj,
      0.2 ρ0)` and the pair's Cohesion (CPU tension pass, GPU `Tension` kernel, Delta to Vtmp, swapped);
    - `FluidSim.CohesionStrength` 4.5 m/s² (was 1.5), `CurvatureStrength` 0.2 (0.4 and up pulled sheets apart into single
      particles; either sign of the term above 1 blew the sheet apart).
  - **Dead end:** a minimum distance between particles (0.85 s) kept the sheet's spacing but splashed the cup out and the
    puddle still tore.
  - **Cause, drawing:** the Zhu–Bridson field averages the particles near each grid point, so a particle on a thin sheet
    lifts the average: a cone above it, and the sheet's underside lifted around it (the dent, sometimes through to the
    floor). DropletSize (faded out only at 20 neighbours) drew that particle bigger still.
  - **Fix, drawing:** `FluidSurface.Smoothing` 0.5: the field uses each particle moved halfway to its neighbours' mean
    (weights 1 - (r/R)³; Yu and Turk 2013 use 0.9), still looked up in the grid of where it is (GPU: the `Radii` kernel
    writes `Smooth`, `Field` reads it). `DropletFade` 12 neighbours (was 20). Same constants in the HLSL defines.
  - **Offline (20,000 particles, Cohesion and Adhesion 1, 10 s on a floor):** inner holes 375 → 18, loose pieces 68 → 4;
    at the water defaults (0.3) 721 → 88 and 236 → 23. A sheet with particles on it: the highest point of the drawn top
    1.74 → 1.28 particle sizes, and the underside stays below the floor line. A poured cup keeps all its particles.
  - The cup's lines did not reproduce offline (a poured and a filled tank came out smooth); the smoothing should even
    out the particles' layering that would show as lines.
  - No synced field changed: the compatibility hash stays J/MtZVeY7e3nbFO3WIKftg==, the channel stays version 5. The
    renderer fork must be rebuilt for the GPU half (a 0.19.0 fork with a 0.20.0 plugin still works, without the fix on the
    GPU).
  - Tests: `FluidTensionTests` (`-- fluid-tension`).

- **0.21.0: a calm pool, sleeping, and skipped work** (Lenki: make larger bodies of fluid cheaper, from the ideas in Box3D,
  Erin Catto's engine, as Garry's Mod's VPhysics-Box3D uses it):
  - **Found first:** a deep pool never came to rest. With 2 substeps of 3 Jacobi iterations its pressure waves kept it at
    10 to 45 cm/s (median), with a 1.6 m/s current along the floor that climbed the walls and threw particles over a
    1.9 m tank holding 1.2 m. It squeezed 10 % (density 1.105). The same on 0.19.0. Ruled out: the box colliders (planes
    the same), friction, cohesion, the half-spacing clamp, a stiffer relaxation, over-relaxation. Warm-started
    (accumulated) lambdas fixed the density but oscillated; a split (velocity-free) correction let particles through
    walls. 8 iterations calmed it at three times the cost.
  - **Calm:** 4 substeps of 2 iterations (Box3D's substepping) and pressure-wave damping: every substep, each particle takes
    on its neighbours' relative motion along the line between them (`FluidSim.PressureDamping` 1, HLSL `PRESSURE_DAMPING`),
    never the sideways part. A deep pool settles to about 3 cm/s, density 1.02. A dam break spreads about a tenth less far.
  - **The slow effects on a cadence:** viscosity (all its passes), cohesion and curvature, and adhesion run every
    `Substeps / 2` substeps with that many substeps' time, so a frame gets as much of them as 0.20.0's 2 substeps gave.
    Friction stays every substep (it scales with dt).
  - **Sleeping** (Box3D's island sleep, per particle): a particle within `SleepRadius` (0.25 spacing) of one spot for
    `SleepAfter` (1 s) sleeps. A speed test failed: a settled pool's particles still move a few cm/s, back and forth.
    - A sleeper keeps its place, searches no neighbours, solves nothing, and is not pushed by colliders. It still counts in
      its awake neighbours' density, and pushes back like a wall (their lambda pair uses their own lambda twice; it adds
      nothing to their denominator). Without that, the awake liquid sank into a sleeping layer, squeezed it (1.087), and the
      squeeze burst out when it woke: every particle awake again within a second.
    - It wakes when an awake particle faster than `WakeSpeed` (0.5 m/s) finds it as a neighbour, when a shape faster than
      that would push it, when a still shape near the liquid is gone or moved without a velocity (CPU: the sleepers in its
      old bounds; GPU: everything), and when a liquid's settings change.
    - CPU: `_calm` and `_anchor` per particle, sorted and compacted with the rest. GPU: `Anchor` (xyz the spot, w seconds),
      sorted by `ReorderAnchor` (Reorder already binds 8 buffers); `Settle` writes Pos and the anchor after `VelocityB`;
      `Normals` marks sleepers in Nrm.w for `Tension` (which cannot bind Anchor either); FrameEnd counts the awake ones
      (State[17], `GpuFluidStats.Awake`).
  - **Skipped work** (Box3D's move buffer: only what moved is queried): with nothing awake (the CPU knows; the GPU by a
    readback newer than anything that could have woken it, no spawns, no trim, no fast shape, no liquid with a lifetime)
    the substeps, the grid and the neighbours are skipped, and so is the surface unless its settings changed
    (`FluidSim.Moved`, `FluidSurface.SettingsChanged`, `GpuFluidSim.SkippedLastFrame` / `SurfaceBuiltLastFrame`).
  - **Offline (19,200 particles in a 1.2 m deep tank, CPU):** 45 ms a step at first, 3.4 ms once 98 % sleep, density 1.017.
    A drop into the sleeping pool joins it (the surface rose 2.5 cm of the 3 expected), a sphere moved through at 1 m/s
    has nothing inside it, and a wall taken away lets it all pour out.
  - **GPU (226k particles):** a game was using the GPU, so absolute times were off. Under the same load 4 x 2 with sleeping
    ran 69 ms a frame against 96 ms for the old 2 x 3, and 26 ms once three quarters slept.
  - **Changed after the first full test run** (12 checks failed):
    - Pressure-wave damping acts only where one of the pair is pressed (over `PressedDensity` 1.01 of rest density; a
      sleeper counts as pressed) and only within one liquid. Everywhere it stopped a kicked drop and a RandomVelocity
      burst from flying apart, and slowed oil rising through water.
    - Beside a liquid of another density a particle never sleeps, and wakes the sleepers there (oil rising starts too
      slowly to keep it awake). Oil still overturns more slowly: at 4 s the two were level, so the tests wait 6 s.
    - A shape faster than WakeSpeed wakes the sleepers within `WakeReach` (8 particle sizes) of its bounds, so a hand
      wakes the water ahead of it (that water moves slower than WakeSpeed and would be pressed against a sleeping wall).
      Waking on squeeze instead (density over 1.06) kept a deep pool's bottom awake (65 % to 70 %).
    - Under `SleepDensity` (0.75 of rest density: a thin sheet, a puddle, a pool's top layer) a particle never sleeps:
      a puddle's holes close slower than any sleep radius, and sleeping froze them open (111 holes against 40 awake).
      So a thin puddle stays awake; a pool's body sleeps (8 % of a 0.8 m pool stays awake).
    - Costs: the CPU path takes about twice 0.20.0's time while the liquid moves (10,000 splashing: 17 ms with a game
      running, limit raised to 25); a sticky puddle has 50 to 85 holes between runs (0.20.0: 24 to 39; 0.19.0: hundreds).
  - Not done: caching the surface per block (only the whole surface is skipped), a sleeping particle's own density and
    normal (it uses rest density and no curvature), islands (a splash wakes a pool as a front, a neighbourhood per substep).
  - No synced field changed: the compatibility hash stays J/MtZVeY7e3nbFO3WIKftg==. The channel stays version 5, but the
    plugin now asks the renderer for 4 substeps of 2 iterations (FluidGpuBridge), so install the plugin and the fork together.
  - Tests: `FluidSleepTests` (`-- fluid-sleep`).
  - For softbodies later, if ever needed: colour the tets for parallel Gauss-Seidel XPBD, and let a still avatar's shell
    sleep.

- **0.22.0: the surface cache (GPU), and calmer slow water** (Lenki, before trying 0.21.0 in game: "options 1 and 2"):
  - **Surface passes sized to use:** VertexColors and ClearTail ran over the buffers' whole size every build (400,000 vertices,
    2.4 million indices). Now `ColorArgs` / `TailArgs` write indirect dispatches (Args grew to 48 bytes: 24 vertices,
    36 the index tail).
  - **Blocks kept between builds:** the block hash table is no longer cleared each build. `BrickMark[slot]` holds 2 x the
    last build the block was in, + 1 if its field must be recomputed (new, back after an absent build, or marked).
    `FindBrick` only returns blocks of this build (`_U4.z` the build number). Field, CellVertices and Quads skip blocks not
    in it; Field keeps the Phi of an unmarked block.
    - `MarkDirty`: a particle that moved since the last build (awake and over `SURFACE_STILL` 3 cm/s, or asleep for less than
      the time since it) marks the blocks within the kernel radius plus a cell (plus how far it can have come) of it.
      Twice the kernel would be exact (its neighbours' smoothing and radii change), but that is about a thirtieth of its move.
    - Every block is also recomputed every `SURFACE_REFRESH` (8) builds, so slow creep lags at most that many builds.
    - Everything is recomputed (the table cleared) when asked (`ForgetSurface`, any surface setting or the lattice origin
      changing), when particles were removed since the last build (State[18]; their blocks are not known), or when the table
      is half full of blocks the liquid has left.
    - A sleeper's timer now keeps counting after it falls asleep (the surface reads how long it has been still).
    - `GpuFluidStats.Bricks` is now the blocks of that build; `BricksComputed` / `BricksNew` / `BricksMoved` say why.
  - **Calmer surface water:** pressure-wave damping now also acts between two surface particles of a liquid (under
    `SurfaceDensity` 0.9 of rest density) moving slower than `SlowPair` (0.1 m/s) against each other. A pool's top layer was
    not pressed and jittered at up to 20 cm/s, which kept 15 % of a settled pool awake and marked most of the surface.
    Damping every slow pair (inside too) calmed it further (a quarter of the blocks recomputed) but slowed oil rising through
    water (level at 6 s) and water pushed by a hand; so only the surface's. A thin sheet (under `SleepDensity` 0.75) is left
    out too: damping its slow pairs held a puddle's holes open (108 on the GPU).
  - **A new emitter no longer wakes everything** (`GpuFluidSim.SetLiquids`): only a liquid that was there and changed does.
  - **Offline (GPU, an 80 x 80 cm pool 60 cm deep, 23k particles):** after 12 s a build recomputes about half of 1296 blocks
    (52 % to 58 %; a whole rebuild 100 %), with a splash spreading about the same. The kept surface matches a whole rebuild to
    0.01 mm (worst vertex). What is left is water just under the surface moving a few cm/s.
  - The CPU path keeps 0.21.0's whole-surface skip (no per-block cache).
  - A sticky puddle's holes vary a lot between runs: 0.20.0 24 to 39, 0.21.0 50 to 85, 0.22.0 75 to 108 (0.19.0: hundreds).
    The step from 0.20.0 came with 4 substeps of 2; worth a look if puddles show holes in game.
  - Same compatibility hash; channel 5. Tests: `FluidSurfaceCacheTests` (in `-- fluid-sleep`).

- **0.23.0: no more self-waking** (Lenki in game on 0.22.0, the Tori world: a Pour emitter at 10,000 L/s, 7.5 cm particles,
  100,000 at most, everything else default, poured into a room whose floor, walls and glass are mesh colliders: "the large
  puddles seem to bounce up and down no matter what", and it ran slower than before):
  - **Cause 1, the bounce:** a mesh reaches the liquid as its triangles within the liquid's bounds (the plugin's query, then
    the runtime's overlap). As the pool's top moved, the wall rows at the edge of those bounds came and went, and 0.21.0 and
    0.22.0 woke every particle whenever a still shape near the liquid was gone. The sleeping pool, slightly squeezed, sprang up
    each time. Offline in a 4 x 4 m room of triangles, 768 triangles went in 10 s.
  - **Fix:** a still shape that is gone wakes only the sleepers within its old bounds plus a reach (r + h), like the CPU path.
    A triangle that left the set at the edge of the bounds has no particle near it. GPU: `GpuFluidSim.WakeIn(lo, hi)` (up to
    32 boxes a step, more merged into the last), the `WakeInBoxes` kernel, run before the substeps.
    `GpuFluidRuntime` keeps each still shape's bounds. `WakeAll` remains only for a changed liquid.
  - **Cause 2, the cost:** the wake thresholds were speeds in m/s, but a liquid's jitter grows with its particles. At 7.5 cm
    a settled 7-layer pool kept particles over 0.5 m/s (35 % over 0.1 m/s) and stayed about 75 % awake. Never asleep, it paid
    for 4 substeps every frame: more than 0.19.0's 2.
  - **Fix:** `WakeSpeed` is now in particle sizes per second (20: 0.5 m/s at 2.5 cm, 1.5 m/s at 7.5 cm), and so are
    `SURFACE_STILL` (1.2) and `SlowPair` (4). A shape still wakes at `ShapeWakeSpeed` 0.5 m/s whatever the particle size. At
    2.5 cm everything is as in 0.22.0.
  - **Offline:** the room pool (17.5k particles of 7.5 cm) is fully asleep by 20 s. With the wall row above the water coming
    and going every third of a second (384 times in 4 s), it stays 18 % awake (along the walls) and its top moves 8 mm.
  - Seen but not fixed: in the room test's first second, a 2.6 m block dropped in collapsed at up to 20 m/s and some particles
    left through the triangle walls. The test sent only triangles within its 3-frame-old bounds plus 0.5 m; worth checking how
    far the plugin pads.
  - Tests: `FluidRoomTests` (in `-- fluid-sleep`).
  - Also in 0.23.0's line (shipped with 0.24.0): a gone still shape wakes within its bounds plus r + one spacing (r + h woke a
    19 cm band along the walls every flip, and the room pool's top moved 8 to 10 mm; now 1 % awake, 2.9 mm).

- **0.24.0: the narrow band and distance detail** (Lenki: "1 and 2", after asking about Blender's liquid, which keeps particles
  only in a narrow band at the surface; Mantaflow can, because its grid carries the pressure. Ours is all particles, so the
  deep ones stay; a calm pool's sleep already does the rest):
  - **Narrow-band surface (GPU):** the `Radii` kernel marks a particle deep (Smooth.w) when it has `DEEP_COUNT` (20) neighbours
    within the kernel all round it (their weighted mean within `DEEP_SKEW` 0.08 R of it). The count alone marked a packed top
    layer deep and drew a false surface (8,654 vertices against 7,331). `MarkShallow`: every other particle marks the blocks
    within R + a cell + half a spacing of it (BrickMark bit 2; the build is now mark >> 2, bit 1 recompute). `Field` writes
    INTERIOR (-1e8) into the other blocks instead of computing them. A block that turns shallow recomputes its INTERIOR
    points.
  - Offline it draws the same surface (0.01 mm). It skips 8 % of a 60 cm pool's blocks and 12 % of a 1.5 m pool's in a
    4 x 4 m room: particles along the floor and walls have neighbours on one side only, so a band along them stays computed.
    `GpuFluidConfig.SurfaceNarrowBand`; `GpuFluidStats.BricksDeep`.
  - **Distance detail (plugin):** `FluidWorld.ViewDetail` is the world's SurfaceDetail times 1 within 5 m of the liquid's
    bounds, 0.7 to 10 m, 0.5 beyond (from the local user's head; steps with half a metre of give). Both paths. A change in
    detail rebuilds the surface whole, hence the steps. Shown in the emitter's status. Not testable offline (needs the
    engine).
  - Lenki's idea, slow particles on a slower clock (1:10): it is local time stepping. In PBF a paused particle is a wall to its
    moving neighbours, and catching up takes N steps of gravity at once (the boiling of 0.20.0). Sleep is its 1:infinity
    case, which needed the wall push. A 1:2 tier for slow awake particles could be tried, measured.
  - **Puddle holes again:** a sticky puddle had 50 to 124 holes from 0.21.0 to 0.23.0 (0.20.0: 24 to 39). With sleeping off it
    had 36: cohesion squeezes a one-particle sheet up to rest density, so the density test (`SleepDensity`) let it fall asleep
    with its holes open. Now a particle with under `SleepNeighbours` (20) neighbours never sleeps either (the body has about
    33). Holes: 35 (CPU) and 29 (GPU); the check is back at 60. Pools still sleep (0.8 m: 10 % awake; the room: 2 %).

- **0.25.0: water sleeps a tile at a time, keeps its pressure, and a no-squeezing pass; mesh smoothing; the classic solver kept
  as a backup** (Lenki in game on 0.24.0: the pool still bounced, frozen water cost 13.6 ms, players could not wake it, a
  settled pool looked like gel, and waves hitting sleeping water died "like water hitting almost melted ice". Then, after the
  paper search in `RESEARCH-fluid.md`: "all 3. prioritizing which one may give the most performance over quality reduction,
  Dont delete the original solver, but just keep it in the backround just as an emergency backup"):
  - **Fixes kept from the first 0.25 work:** a sleeper is pushed against once, not twice (the bounce); a sleeper checks only the
    moving shapes (the 13.6 ms: every sleeper looped over a room's 938 triangles every iteration); the plugin tracks bones'
    velocities (players woke nothing). Dropped, never shipped: top-down sleep (`SettleOrder`), the approach wake and
    still-pair damping: pools stayed 70 to 90 % awake.
  - **Why the gel:** particle by particle, water froze while the water around it still settled. Measured on a 7,200-particle
    pool (2.5 cm): bottom-up it kept pits up to 8 cm deep, top-down a lid with 15 cm holes; awake the same pool is flat to
    2 mm. Freezing must wait until a region is calm and take it whole.
  - **Tiles (`FluidSim.TileSleep`; GPU `ClearTiles`, `TileTop`, `TileCount`, `TileDecide`, `TileApply`):** columns of the
    liquid 8 particle sizes square (a hash table of 65,536, cleared every frame). A tile is settled when 70 % of its water that
    can sleep has kept within the sleep radius for the sleep time; it sleeps whole once it and its 8 neighbours are settled and
    none is restless. Restless: 5 % of its water near its top (within 4 sizes of its body's highest particle) left its spot
    this frame; it wakes its own and its neighbours' sleepers. These are the blocks of regional time stepping (Goswami and
    Batty 2014) with sleep as the slowest step, and Box2D's islands as columns.
  - **What it took** (each measured, 12,000 and 20,000 particles): speeds are noise (the position solve turns a 1 mm
    correction into 24 cm/s), so stillness is judged by place; only water that can sleep counts for stillness (a thin edge on a
    sleeping tile kept it and its neighbours unsettled); only motion near the top counts as restless (deep down the packing
    slips now and then); woken water gets 0.25 s of grace (counted as motion, a woken tile woke the next, and on across the
    pool); and above all **a sleeper keeps its pressure** (its last lambda, in `_lamSleep` / GPU `Anchor.x`). Without it the
    water beside or on a sleeper got half its push: classic pools sit 6 % over rest density in pits, and woken frozen water
    settled back with a jolt that woke the water around it. With it: 12k and 20k pools sleep by 15 s, flat to 1.2 mm.
  - **Stragglers:** a drop on a wall or a thin edge never sleeps (so a puddle's holes keep closing) and kept a sleeping pool
    simulating every frame. Now the step is skipped when the only awake water is at most 2 % (or 64 particles) and all of it has
    been still for half the sleep time (not "none left its spot this frame": a small blob drifting slower than that stopped).
  - **Waking:** a collider moving at all (from 2 cm/s) wakes the water it touches; faster than `ShapeWakeSpeed`, or a user's
    part, within `WakeReach` as before (a paddle at exactly 0.5 m/s went through a sleeping pool without moving it). A wave
    from it now rises 64.7 mm at the far end of a 1.6 m tank through sleeping water, against 54.1 mm through awake water (0 mm
    before the collider fix). A still user's part no longer counts as moving, so a pool beside a standing player can skip.
  - **Wake boxes (GPU):** 128 (from 32), and one that does not fit grows the box that grows least (0.23.0 grew the last: a
    room's wall row of 64 triangles going at once made one box over the whole room, which woke the top 10 cm of the whole pool,
    22 % of it, at every flip). Water woken where a still collider went gets the tiles' grace too.
  - **The divergence pass (`DivLambdas`, `DivDeltas`; CPU in `Velocities`):** Bender and Koschier's divergence-free SPH as a
    velocity step of the density constraint, once a substep after the motion: inside the liquid (20+ neighbours) and from 95 %
    of rest density up (`DivDensity`), neighbours' motion towards each other is taken out. A pool settles in 8 s instead of
    13 s; its median jitter halves. It does not replace the pressure-wave damping (without that: 92 % awake). Everywhere it
    undid cohesion (a kicked blob did not pull back together: 4.7 cm against 4.7 cm loose); from rest density exactly it left
    half a pool's jitter (12,000 particles slept 10 s later). Lambdas in `LambdaV` (`Lambda` keeps the position step's for
    `TileApply`).
  - **Mesh smoothing (`FluidSurface.SmoothMesh`; GPU `SmoothNbrs`, `SmoothIn`, `SmoothOut`, `SmoothWrite`):** Löschner et al.
    2023's weighted Laplacian smoothing, 4 Taubin passes (in 0.5, out -0.53; plain passes shrank a pool 1 % in 4 and rounded it
    away from the walls; Taubin keeps it: 95.46 to 95.49 L). Weight by the particles within the kernel radius (none at 4,
    full at 14), so drops and sheets keep their shape. Neighbours: the six cells' vertices whose shared face the surface
    crosses (not across a thin sheet). Normals smooth too (a pitted surface's worst normal 0.82 to 0.88).
  - **The classic solver (backup):** a file named `Softbody.classic` next to the plugin's DLL (checked every 2 s; live, the liquid
    wakes up and carries on) runs 0.24.0's solver with the fixes above: no tiles, no kept pressure, no divergence pass, no mesh
    smoothing. This client's own choice: a synced emitter field would have changed the compatibility hash (0.19.0 to 0.25.0 still
    share a session). The plugin sends it in the channel header's new `Flags` (version 6).
  - **Room for 524,288 particles** (from 262,144): the renderer makes room for the world's emitters' MaxParticles together in
    steps of 65,536, 131,072, 262,144 and 524,288 (a new step starts the liquid over); 0.16.0 to 0.24.0 always made room for
    the most. The cell table stays at most 2^19 (the scan's limit).
  - **500,000 particles (RTX 4070, a 3.1 m tank 0.8 m deep, filled at once):** 50 to 59 ms a frame while all awake; falling with
    the awake share as it settles (19 %: 17 ms, 4 %: 9.4 ms); all asleep at 80 s, then the frame is skipped: 0.3 ms. CPU and
    GPU sleep times for smaller pools: 7,200 particles 4 s, 12,000 15 to 17 s, 20,000 19 s, 48,000 26 s; level within 2 mm.
  - **0.25.1: fixed substeps** (Lenki in game on 0.25.0: "the bouncing gets worse the lower the framerate is"): 4 substeps per frame
    whatever the frame took made a substep at 30 fps twice as long, and gravity squeezed the water 4 times as far each one: a
    pool sat 8 % over rest density instead of 1.7 % and 28 mm lower, and never slept; with the frame time jumping between 20
    and 90 fps it breathed up and down by 24 cm (classic too: 15 cm). Now a substep is always a 60th of a second over
    `Substeps` (1/240 s), and a frame runs as many as its time holds (an accumulator), at most a 30th of a second's worth (a
    slower frame runs slow; its excess is dropped, not owed); the slow effects' turn counts across frames. The same pool now
    rests at 568.7 mm and sleeps at 60 fps, at 30 fps and at random frame times. A slow frame costs more substeps, but at a
    low frame rate the water used to stay awake for ever, which cost more. Both paths (FluidSim.Step, GpuFluidSim.Step).
  - **0.25.2: cohesion up to 5, adhesion up to 10** (Lenki: "cohesion and adhesion is limited from 0-1"; a friend of Lenki's:
    swap to a high-viscosity solver above those): cohesion is a force between particles, explicit, and past about 5 it
    overshot against the density solve: a 20 cm block on a floor shook at 10 (92 cm/s) and at 20 threw water through the
    floor; a kicked zero-gravity blob shook apart. Tried: capping each particle's tension step (non-monotonic, and the blob
    drifted: the cap broke momentum), tension every substep (no better), damping every pair's squeeze (worse). Viscosity holds
    it, so above 1 a liquid is at least 0.75 x (cohesion - 1) viscous (`FluidLiquid.CohesionViscosity`; 5: 3), and cohesion
    stops at 5 (at 10 to 20 it still churned at 60 to 120 cm/s even thick). Now: cohesion 1 spreads a block to 45 cm, 2 beads
    it to 14 cm, 3 to 13 cm and 5 to 11 cm, and from 3 up it settles fully. Adhesion only takes away motion off a surface (up to
    2 m/s per unit), so 10 cannot overshoot: water stays on what it touches even when flung. Both paths (FluidLiquid.Sanitize,
    GpuFluidDirector).
  - **Not done yet:** the in-between rates of regional time stepping (slow awake tiles solved every 2nd or 4th substep), and
    making a partly awake pool's cost follow its awake water (the sort, the tile passes and the surface still run over all of
    it: 4 % awake still costs 9.4 ms at 500,000).
  - Tests: `FluidSettleTests` (level when asleep; the paddle wave), `FluidRoomTests` (the toggled wall row is now the one just
    above the water: a pool that settles level no longer reaches 0.5 m), `FluidSleepTests` and the rest unchanged.
  - **0.26.0: particle smoke** (Lenki: "would volumetric particles be possible? (Like a super simple gas sim?)", option 1): a
    `GasEmitter` component whose puffs run on the GPU beside the liquid, in the same frame, channel (version 7) and colliders.
    See DESIGN-gas.md. A new component: the compatibility hash changes, so everyone in a session needs 0.26.0.
  - **0.26.1: not fluid.** The SkinnedSoftbody instancing fix (the avatar rebuilt every 2 s); see README.md and
    DESIGN-skinned.md. Same hash as 0.26.0.
  - **0.26.2:** no base calls to the engine's OnFocusChanged / OnWorldDestroy (they throw by design; FluidEmitter had one
    in OnWorldDestroy). Same hash as 0.26.0.
  - **0.26.3: not fluid.** SkinnedSoftbody shading on blendshapes (normals measured on the blendshaped shape, tangents
    turned with them); see README.md and DESIGN-skinned.md. Same hash as 0.26.0.
  - **0.26.4: not fluid.** SkinnedSoftbody skin points let go of colliders on the wrong side (stuck on flat boxes the body
    passed through); see README.md and DESIGN-skinned.md. Same hash as 0.26.0.
  - **0.26.5: not fluid.** A SkinnedSoftbody hue layer keeps its saved hue (OnAwake no longer picks one); see README.md
    and DESIGN-skinned.md. Same hash as 0.26.0.
  - **0.27.0: smoke swirls, foam and thick liquid** (the three SoftbodyRnD prototypes Lenki picked): see the section below.
    New synced fields, so a new hash (`0/uDXxcV+UVEn6+RnAsMVg==`); channel version 8. Its flesh code includes 0.26.6's fix
    (skin points flying after sudden moves and hits), which ships first, on its own, from a copy without the fluid changes.

## 0.27.0: smoke swirls, foam and thick liquid

Lenki picked three of the solver prototypes in `SoftbodyRnD` (2026-11-12, about 13:00), in this order: vortex particles for the gas,
spray, foam and bubbles for the liquid, and implicit viscosity for thick liquids. The prototypes, how they were measured and the
numbers are in `SoftbodyRnD\RND-REPORT.md`, sections 3, 1 and 6. This is the port. All three are options that are off by default,
so a world saved before 0.27.0 looks the same. They run on the GPU only (the renderer fork). On the CPU they do nothing, and the
liquid's status says so.

**The options:**

| component | field | default | what it does |
|---|---|---|---|
| GasEmitter | `Vortices` (0..10) | 0 (off) | things moving through the gas shed vortex rings that keep stirring it; 1 the usual amount |
| FluidEmitter | `ThickLiquid` | off | Viscosity above 1 is a real viscosity (the implicit solve), held to 15 |
| FluidEmitter | `Foam` (0..10) | 0 (off) | spray, foam and bubbles where it splashes, breaks or traps air; 1 the usual amount |
| FluidEmitter | `FoamLifetime` (0.1..60 s) | 3 | how long its foam lasts (a particle lives 0.5 to 4/3 of it, longer after bigger splashes) |

**The channel (version 8):**
- `FluidEmitterRecord` gains `Foam`, `FoamLifetime` and `Thick` (160 of its 192 bytes).
- `GasEmitterRecord.Reserved0` became `Vortices` (still 128 bytes).
- `FluidBack` gains `FoamParticles`, `FoamDrawing`, `ThickIterations`, `ThickUnconverged` and `Vortices` (132 of 256 bytes).
- The header is unchanged. The renderer makes the foam mesh once a record asks for foam.
- A 0.26.x fork and a 0.27.0 plugin (or the other way round) ignore each other, as with every channel bump. The renderer rejects
  a frame of the other version, so the plugin hears nothing back for 5 s and moves the liquid to the CPU for 30 s, then tries
  again. The liquid restarts at each switch, and the gas (GPU only) is off.

**Vortex particles** (`GpuFluidSim`: `RunVortices`, `ShedVortices`; kernels `VortClear`, `VortSpawn`, `VortVelocity`, `VortMove`
and the tile loop in `GasUpdate`):
- The prototype's design, unchanged: vortex blobs with a strength and a core; the air they induce is the regularised Biot–Savart
  sum, read by every puff in 128-wide shared-memory tiles.
- A moving shape faster than 0.2 m/s inside the gas's bounds sheds a ring of 8 blobs behind it, about once per own size of travel.
- **Per emitter:** the rings are shed with the strongest Vortices of the world's gas emitters, and each puff feels them by its
  own emitter's share of that. One emitter at 1 is exactly the prototype at strength 1; a puff of an emitter at 0 feels nothing.
  A gone emitter's value stays with its slot for a minute, while its last puffs live (`GpuGasDirector.Vortices`).
- 512 slots (a ring), 6 s life, 2.5 s decay. With every emitter at 0 nothing runs: no kernels and no reading in `GasUpdate`.

**Spray, foam and bubbles** (`RunDiffuse`; kernels `DiffuseBegin`, `DiffuseEmit`, `DiffuseHead`, `DiffuseUpdate`, `DiffuseDraw`,
`DiffuseTail`):
- The prototype's emission (trapped air and wave crests, Ihmsen et al. 2012), classification and motion, unchanged.
- **Per liquid:** a fluid particle makes diffuse particles by its liquid's `Foam` (in `GpuLiquid.D.x`). Their lifetime comes from
  its `FoamLifetime` (`D.z`), and their colour from the liquid's, mixed 85 % towards white.
- **Two fixes from the report's port list:**
  1. Each new particle is swept from its fluid particle against the shapes, so none starts inside a wall or past it.
  2. While the ring is full, nothing new is made. The frame's budget is at most the free slots, so live foam is no longer
     overwritten early: it popped at 500k particles in the prototype.
- 65,536 slots, at most 2 x 2,048 new ones a frame. The scale that spreads a big splash's budget evenly is the prototype's.
- **Memory only when used:** a token of 256 slots until a liquid first asks for foam. Then 25 MB on the GPU: 18 MB of vertices,
  4.7 MB of indices and the particles.
- **Drawing:** the prototype's discs (7 vertices, 18 indices, facing the head; spray small and sharp, foam bigger, bubbles small
  and faint, all fading over their last half second).
  - They go into their own buffers (`DVerts`, `DIndices`), and the renderer copies them into a third mesh, "Softbody GPU foam".
  - That mesh hangs under the gas placeholder's renderer and draws with its unlit material. The plugin makes that placeholder
    (the local "Softbody gas" slot) for foam too, even in a world with no gas.
  - It draws one sorting step after the gas. Not checked in game yet: where foam sorts against the water's refraction.
  - The foam mesh is made the first time a world asks for foam, and copied every frame from then on (23 MB, about 0.05 ms).

**The thick liquid** (`RunImplicitViscosity`; kernels `IvFrameBegin`, `IvBegin`, `IvDiagonal`, `IvStart`, `IvMatVec`,
`IvResidual`, `IvInit`, `IvAlpha`, `IvUpdate`, `IvBeta`, `IvDirection`, `IvFinish`, `ReorderIvGuess`):
- The prototype's solver (Weiler et al. 2018), unchanged: on the two slow substeps a frame, a conjugate gradient with the 3x3
  block-Jacobi preconditioner, half warm start, tolerance 0.01, at most 150 iterations, the convergence flag kept on the GPU.
- **Per liquid:** with `ThickLiquid` on and a Viscosity over 1, the director gives the liquid
  ν = 10^(−3 + 4 (V − 1) / 19) m²/s in `GpuLiquid.D.y`, with V held to 15 (`FluidChannel.ThickViscosityCap`). Cohesion's
  thickening counts, as with XSPH. The XSPH passes then skip it (`XsphVisc`), and the passes count only the other liquids'
  viscosity. A liquid with `ThickLiquid` off, or water, is as before.
- **Memory only when used:** 160 bytes a particle (8 MB at 65,536, 84 MB at 524,288), made the first time a liquid is thick.
  The warm start then follows the particles through every sort.
- **`Bind()` binds the UAVs before the SRVs.** The solve reads its scalars through a second, read-only view, and Direct3D 11
  nulls an SRV whose buffer is still bound as a UAV from the dispatch before. The prototype found it: the solve read zeros.
- Why it is capped at 15: the iterations depend on the viscosity, not the particle count. About 31 a solve at 15 (+10 ms a frame
  over XSPH's 20 passes at 100,000 particles), about 100 at 20 (+52 ms).

**Tests** (`tests/GasVortexTests.cs`, `FoamTests.cs`, `ThickLiquidTests.cs`; modes `vortex`, `foam`, `thick`, all three
`fluid-0270`, and `fluid-0270-cpu` for the parts with no GPU):
- No GPU (all pass):
  - the channel carries the new fields whole, and a version 7 frame is not read;
  - the gas director carries each emitter's Vortices, keeps a gone one's for a minute, and turns NaN into 0;
  - the mapping: 10 is 0.08 m²/s, 15 is 0.89, 20 is held to 15, 4 is about honey (0.004); off, or 1 and under, stays XSPH.
- GPU: the prototype's checks on the ported code, plus new ones for the per-emitter and per-liquid parts:
  - vortex: Vortices 0 runs nothing; a swipe sheds rings; the wake still stirs a still cloud 1 and 2 s later; it carries smoke
    along; a cloud at 0 beside an emitter at 1 is not stirred; a lone ring travels and keeps its size;
  - foam: a dam break makes spray, foam and bubbles, none leaves the tank, and it dies away; spray is mostly above the water and
    bubbles below; every live particle is drawn as a disc facing the head; a calm pool makes none, and the kernels are skipped; a
    pour foams where it lands; a liquid at 0 beside one at 1 makes none; no particle outlives 4/3 of its FoamLifetime, and 6 s
    keeps more alive than 1 s; Foam 0 allocates nothing;
  - thick: a blob keeps its spin (XSPH stops it) without spinning up; the dam break is stable, converges and spreads less at 15
    than XSPH's 20 passes; 20 is held to 15; water with ThickLiquid on runs no solve and moves the same.
  - **Results** (2026-11-12 from 14:55, RTX 4070, no game open): the three new suites pass.
    - Vortex: a hand swiped at 1.5 m/s through a still cloud. The smoke near its path still moves 0.244 and 0.129 m/s sideways
      1 and 2 s later with Vortices 1, against 0.136 and 0.030 with 0 (1.8x and 4.3x). The swipe shed 7 rings (56 vortex
      particles). A cloud at 0 beside an emitter at 1 moves exactly as with 0. A lone 15 cm ring travels at 0.20 m/s and stays
      15 cm across.
    - Foam: a dam break (1.6 m tank) peaks at 804 diffuse particles (567 foam, 237 bubbles, spray at most 11). None are left at
      10 s, and none leave the tank. At 1.5 s, 322 of 524 bubbles are under the water. A calm pool makes none in 15 s and skips
      the kernels. A 4 L/s pour from 0.8 m makes 243, 201 of them within 20 cm of where it lands; its 5 spray are all above the
      water. Two dam breaks side by side, Foam 1 and 0: 789 and 0. FoamLifetime 1 s leaves none at 4 s; 6 s leaves 781, the
      longest with 4.69 s to go (at most 8). Foam 0 keeps the 256-slot token and runs nothing.
    - Foam, rerun at 15:20 with two checks fixed (they had looked when nothing was alive, so they passed on nothing): at 1.5 s
      all 825 live particles are drawn as discs facing the head, each disc's triangles use only its own 7 vertices, and nothing
      is left drawn at 10 s. Looking every half second, no particle had more than 4/3 of its FoamLifetime left (1 s: at most
      1.00 s; 6 s: at most 6.82 s).
    - Thick: a 30 cm blob spinning at 2 rad/s (no gravity) keeps 97 % of its spin after 3 s at Viscosity 15 with ThickLiquid;
      XSPH at 20 stops it within 0.5 s, and water keeps 37 %. A dam break's front at 4 s: water +0.59 m, XSPH 20 +0.46,
      thick 10 +0.53 (9 iterations a solve), thick 15 +0.31 (33, at most 35), thick 20 the same as 15. No solve hit the
      150 cap. Water with ThickLiquid on runs no solve and moves the same (within 2 mm).
    - The older suites on the same build pass: gpu-fluid, gas, fluid, fluid-floor, fluid-size, fluid-tension and flesh (with
      0.26.6's fix). GPU times are as 0.26.0's or lower (97,336 particles with the surface: 10.3 ms median against 12.3; the gas
      column 0.44 ms against 1.03), so binding the UAVs first costs nothing.
    - fluid-sleep failed one check once: after a splash, the kept surface was 3.187 mm off a whole rebuild (the limit is 2.5 mm).
      The rerun passed at 0.050 mm, and older runs measured 0.005 to 0.055 mm. Nothing 0.27.0 changed runs differently for plain
      water, so it is a rare flake of the splash (the GPU's atomics make each run a little different).
- The plugin check pins the new fields in order, the version and the new hash.

**Later:**
- A stronger preconditioner (multigrid) or one solve a frame, to make thick liquids past 15 affordable.
- A foam ring sized to the liquid (65,536 is fixed: enough to about 150,000 moving particles).
- Shedding only from users' hands, so a moving prop doesn't fill the vortex slots.
- Checking in game where foam sorts against the water's refraction.
- Temperature and melting: a ν per particle (the harmonic mean already handles neighbours with different ν).
