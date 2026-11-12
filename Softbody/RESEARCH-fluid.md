# Fluid research notes (2026-11-11)

Why: before more trial and error on 0.25's sleep, check what published work says about real-time fluid speed and calm water.

Where we are: GPU Position Based Fluids (D3D11), 4 substeps x 2 iterations. Still particles sleep and act as walls. The surface is surface nets on a sparse block grid, with a cache and a narrow band.
- A settled pool costs 2.4-2.6 ms at 200k particles and 3.1 ms at 262k. Most of that is the surface build (MarkBricks, Radii, Field, VertexColors); the solver is a small part.
- Open bugs:
  - Pools never fully settle under 0.25's top-down sleep: 79% still awake after 25 s.
  - Frozen bumps make the surface look like gel.
  - Waves die against sleeping water.

## Best fits, in the order I'd try them

### 1. Weighted smoothing of the surface mesh (fixes the gel look)
Löschner, Böttcher, Jeske, Bender, "Weighted Laplacian Smoothing for Surface Reconstruction of Particle-based Fluids", VMV 2023. Open source in splashsurf.
- It smooths the mesh after meshing. Flat, full areas get smoothed strongly; splashes and lone particles only weakly, so they keep their shape. It also adds a normal-smoothing pass.
- For us: a GPU pass over the surface-nets vertices, each averaging its neighbours, a few iterations. The weights come from how surrounded the nearby particles are.
  - This removes particle-sized bumps whether or not the water under them is frozen. The order in which particles fall asleep then matters much less.
  - That would let us go back to 0.24's simple bottom-up sleep, which did converge, and drop the top-down experiment.

### 2. A divergence-free velocity pass (stops the slow churn)
Bender & Koschier, "Divergence-Free Smoothed Particle Hydrodynamics", SCA 2015; journal version TVCG 2017.
- It solves two things: constant density on positions, and zero velocity divergence (no compression or expansion) on velocities.
- They report better stability, larger time steps, and fewer pressure iterations.
- For us:
  - Our pressure-wave damping (damping the relative normal velocity of pressed pairs) is a rough hand-made version of this.
  - The jitter that keeps pools awake is compression and expansion, which is divergence. Waves and flowing water are mostly divergence-free, so a proper pass should remove the jitter without killing waves.
  - It is likely the principled fix for pools that never settle, and for the bounce.
  - Cost: one or two extra neighbour loops, per frame rather than per substep.

### 3. Regional time stepping (Lenki's 1:10 idea, as published)
Goswami & Batty, "Regional Time Stepping for SPH", Eurographics 2014 short paper. Extended in "Asynchronous Liquids: Regional Time Stepping for Faster SPH and PCISPH", arXiv 2020.
- **Blocks:** the domain is split into blocks two particle spacings wide. Each block takes the largest step allowed by:
  - a speed limit, the CFL condition;
  - a force limit;
  - a cap on how far its particles may move per step: a share of the kernel radius.
- **Buffer at the edges:** a block next to a faster block takes the faster step, so the step size changes gradually.
- **Slow particles are not frozen.** Every base step they are moved along on their last velocity and acceleration, with no neighbour work. At the end of their own step they get a second-order correction (Serna et al.).
  - Without that correction, particles "quickly push apart and the smooth surface is disrupted".
  - This answers my worry that a paused particle acts as a wall and then overshoots: it keeps moving on its own momentum instead.
- **Results:** about 2x faster, even in highly dynamic scenes (1.5M and 2.2M particles; CPU WCSPH). The 2020 version also varies the number of pressure iterations per region (PCISPH) and is again about 2x.
- **For us:** a softer middle state between awake and asleep. Waves would walk into slow water through the buffer blocks instead of hitting frozen walls. That is a cleaner version of 0.25's approach wake.

### 4. Fewer solver iterations deep down
Köster & Krüger, "Adaptive Position-Based Fluids: Improving Performance of Fluid Simulations for Real-Time Applications", IJCGA 2016 (arXiv 1608.04721).
- Each particle gets its own iteration count. It is set from its depth below the visible surface, or from its distance to the camera; depth gave better results. Counts blend linearly so neighbours differ only a little.
- Once a particle's count is used up, it stays fixed for the rest of that solve, and its neighbours treat it as fixed. That is the same as our sleepers. The other particles make up for its error.
- **Results:** 40-77% faster than full-iteration PBF on 216k-673k particles (GTX 680, Radeon HD7850). Average density stays within 1.5-4%.
- **Caveat:** low-iteration particles get pushed out of colliders poorly, so they added a contact pre-pass.
- **For us:**
  - We already flag deep particles (the narrow band's deep flag), so deep water could run 1 iteration per substep and surface water 2.
  - This helps awake water (pours, waves). It does nothing for a settled pool.

### 5. Inactive particles (confirms what we do)
- **Goswami & Pajarola, "Time Adaptive Approximate SPH", VRIPHYS 2011:** splits the fluid into active and inactive particles and reports up to 7x on up to 1M particles. Later papers describe it as freezing very slow particles, or interpolating them to the next step.
  - The PDF was blocked (403), so the details are unread.
- **PhysX and Flex:** they sleep particles that stay under a speed threshold for a few frames; net forces wake them.
- Sleeping is the right idea and the biggest win. The papers above are about making its edges soft.

## Long-term, for volumes far beyond 500k
### 6. Heightfield or grid for the calm bulk, particles only near the action
- **Chentanez & Müller, "Real-time Simulation of Large Bodies of Water with Small Scale Details", SCA 2010:**
  - The calm water is a 2D heightfield (shallow-water equations), so its cost grows with surface area, not volume.
  - Splash and spray particles break off and return to the heightfield when they land.
  - The heightfield step took 0.75-2.2 ms on a GTX 480 for large open scenes.
- **Chentanez, Müller & Kim, "Coupling 3D Eulerian, Heightfield and Particle Methods...", SCA 2014:**
  - PBF particles only near the surface, a 3D grid underneath, and a heightfield beyond.
  - A tank that needs 1.9M particles alone took 180k particles plus a 64^3 grid. Particles that sink too deep are deleted into the grid, and new ones are made where the surface lacks them.
- **Related grid work:** tall cells (Chentanez & Müller, SIGGRAPH 2011); quadtree tall cells (Narita et al., SIGGRAPH 2025); adaptive optical layers (Narita & Kanai, Eurographics 2026).
- **Blender:** the trick Lenki remembered is Narrow Band FLIP (Ferstl, Ando, Wojtan, Westermann, Thuerey, Eurographics 2016; used in Mantaflow). It keeps particles only near the surface and uses a grid inside.
- **Limits:**
  - A heightfield can't hold overhangs or water on top of water.
  - It needs a new solver plus coupling, and the papers aim at open water.
  - It is a big project, worth it only for much larger volumes than now.

### 7. Bigger particles inside
- Winchenbach, Hochstetter, Kolb, "Infinite Continuous Adaptivity for Incompressible SPH", SIGGRAPH 2017. Deep particles are merged into larger ones and split again near the surface.
- Complex (mass redistribution, blending), and their own later work hit data-structure limits.

## Looked at, poor fit for us
- **Screen-space fluid rendering** (van der Laan, Green, Sainz 2009; Truong & Yuksel narrow-range filter, i3D 2018; Oliveira & Paiva narrow-band SSFR, CGF 2022, 2.4x faster than plain SSFR):
  - No mesh at all.
  - But it costs work per view, and Resonite has VR eyes, mirrors and cameras. It would also need a new render pass in the renderer fork.
- **Compressed neighbour lists** (Thaler & Keller, arXiv 2602.19873, 2026): mostly saves memory, and needs warp shuffles, which D3D11 lacks. Our neighbour pass is only about 0.25 ms anyway.
- **MLS-MPM** (community WebGPU projects; self-reported ~100k particles on an iGPU, ~300k on decent GPUs): no neighbour search, but a full solver rewrite with a different feel.
- **Chebyshev acceleration** (Wang, SIGGRAPH Asia 2015; fluids in Sommer et al.): speeds up convergence over many Jacobi iterations. We run only 2 per substep, so there's little to gain.
- **"4 million SPH particles at 54 fps in Unity"** (Mathematics 2026, 14(11), 1845): the page was blocked (403), so the method is unknown and the claim unverified.

## What this means for 0.25 and the 400-500k goal
- **Settled pools:** cost is mostly the surface build, so the existing plan stands:
  - surface work only near moving water;
  - keep sleepers' smoothing;
  - an awake list;
  - a cap of 524,288 particles.
- **Gel look:** weighted mesh smoothing (1), then go back to the simple bottom-up sleep.
- **Pools never settle, and the bounce:** a divergence-free velocity pass (2).
- **Waves into sleeping water:** buffer blocks and slow-but-moving particles from regional time stepping (3), instead of per-pair wake rules.
- **Awake water cost:** fewer iterations deep down (4).

## Sources
- https://diglib.eg.org:443/handle/10.2312/vmv20231245 (weighted Laplacian smoothing); code: https://www.github.com/InteractiveComputerGraphics/splashsurf
- https://animation.rwth-aachen.de/publication/051/ and https://animation.rwth-aachen.de/media/papers/2015-SCA-DFSPH.pdf (DFSPH)
- https://cs.uwaterloo.ca/~c2batty/papers/GoswamiBatty2014/RTS_2014.pdf and https://arxiv.org/abs/2009.14514 (regional time stepping)
- https://arxiv.org/pdf/1608.04721 (adaptive PBF)
- https://diglib.eg.org/items/32fa0c9c-28e9-409e-a11a-e326e28568ca (time adaptive approximate SPH)
- https://docs.derivative.ca/NVIDIA_Flex_Solver_COMP and https://arxiv.org/pdf/1703.07395 (Flex/PhysX sleep)
- https://matthias-research.github.io/pages/publications/hfFluid.pdf and https://matthias-research.github.io/pages/publications/hybridsim_preprinted.pdf (heightfield hybrids)
- https://graphics.c.u-tokyo.ac.jp/hp/en/archives/3252 (quadtree tall cells) and https://graphics.c.u-tokyo.ac.jp/projects/Adaptive-Optical-Layers
- https://www.cs.cit.tum.de/en/cg/research/publications/2016/narrow-band-flip-for-liquid-simulations/ (narrow band FLIP)
- https://history.siggraph.org/?p=102416 (infinite continuous adaptivity)
- https://www.cemyuksel.com/research/papers/narrowrangefilter.pdf and https://diglib.eg.org/handle/10.1111/cgf14510 (screen-space rendering)
- https://arxiv.org/html/2602.19873v1 (compressed neighbour lists)
- https://tympanus.net/codrops/2025/02/26/webgpu-fluid-simulations-high-performance-real-time-rendering/ (MLS-MPM in WebGPU)
- https://wanghmin.github.io/publication/wang-2015-csi and https://dspace5old.zcu.cz/bitstream/11025/38434/1/Sommer.pdf (Chebyshev)
