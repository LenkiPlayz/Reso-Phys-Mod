# Solver research notes (2026-11-11)

Lenki asked: "look up if theres any unique solvers that may be faster, have unique functionality or both."
This complements RESEARCH-fluid.md, which covered the liquid's speed-ups for 0.25 (tile sleep, the divergence pass,
regional time stepping, heightfield hybrids, Narrow Band FLIP).

What the mod uses now:
- **The liquid:** GPU position-based fluid particles (PBF style), with tile sleep and a divergence pass.
- **The gas:** GPU puff particles with buoyancy, drag and curl noise.
- **SkinnedSoftbody:** an XPBD tet shell under the skin.
- **Softbody:** a lattice cage.

Speed-ups below are the authors' own numbers unless noted. Nothing here was benchmarked by us.

## Avatar flesh and soft props

1. **Vertex Block Descent (VBD).** Chen, Liu, Yang, Yuksel, SIGGRAPH 2024 (Utah and Roblox).
   - **Faster.** Each vertex is solved on its own with a small Newton step, in parallel by colour group. It is unconditionally stable, and you can cap the iteration count to a budget.
   - **For us:** a replacement for the XPBD loop in the tet shell. It would be stiffer for the same cost, and less sensitive to substeps. It is already in DESIGN-skinned.md's references.
2. **Augmented VBD (AVBD).** Giles, Diaz, Yuksel, SIGGRAPH 2025 (TOG 44(4)).
   - **Both.** It is VBD plus an augmented Lagrangian, so it handles truly hard constraints (infinite stiffness) without blowing up. The authors show contacts, stacking, friction, joints and attachments in real time on the GPU.
   - **For us:** a pinned inner layer and firm contacts without stiffness tricks. It could also simulate rigid props in the same solver.
3. **JGS2.** Lan et al., SIGGRAPH 2025 (arXiv 2506.06494).
   - **Faster.** Each iteration costs about the same as a plain Jacobi step, but it converges almost like Newton's method (the authors report 50-100x better convergence than other GPU methods).
   - **For us:** stiff flesh in a few iterations. It needs a precompute step (cubature), so it is more work than VBD.
4. **MGPBD (multigrid XPBD).** Li et al., SIGGRAPH 2025 (arXiv 2505.13390, code on GitHub).
   - **Faster for big, stiff meshes,** where XPBD stalls.
   - **Limits:** it needs a sparse matrix, can't tear, and needs extra damping. It is only worth it for meshes far denser than our shells.
5. **Fast Complementary Dynamics via Skinning Eigenmodes.** Benchekroun et al., SIGGRAPH 2023.
   - **Much faster.** Secondary motion for rigged characters runs in a small, rig-aware subspace, so the cost does not depend on mesh resolution. One case ran 108x faster than the full simulation, and they show 26 creatures with 330k vertices in real time.
   - **For us:** whole-avatar jiggle almost for free. Local dents would be weaker than with our tet shell.
6. **FastLSM (lattice shape matching).** Rivers & James, SIGGRAPH 2007.
   - **Faster and very robust.** Shape matching over lattice regions takes linear time, so it cannot explode. It ran many dozens of soft bodies in real time on a 2007 CPU.
   - **For us:** a cheap, unbreakable mode for the lattice Softbody, good for many props at once.
7. **Position and Orientation Based Cosserat Rods.** Kugelstadt & Schömer, SCA 2016.
   - **New feature:** rods that bend and twist, for tails, ears, hair, ropes and cables. It is more than 10x faster than earlier position-based rods.
   - **For us:** a tail or hair softbody component, in the same PBD style as the shell.
8. **OmniBody.** Shin & Park, KAIST, SIGGRAPH 2026 poster.
   - **New feature:** cutting, peeling and tearing soft bodies. It uses oriented ellipsoid particles and only local XPBD constraints. To cut, a constraint is switched off, and there is no remeshing.
   - **Speed:** a news write-up quotes about 3 ms for a 64k-particle object on an RTX 3080 Ti. No paper or code was found.
   - **For us:** jelly or props you can slice or break.

## The liquid

9. **Implicit Position-Based Fluids (IPBF).** Diaz, Hsu, Montalvo-Ruiz, Giles, Yuksel, SIGGRAPH Asia 2025.
   - **Steadier at the same cost.** It is PBF rebuilt as a fully implicit solve, with VBD-like relaxed Jacobi iterations.
   - **Results:** at equal compute time it compresses about as little as DFSPH, where PBF compresses much more. It recovers from a compression stress test in which DFSPH explodes.
   - **Speed:** 1M particles take 159 ms per frame on an RTX 4090, so not real time at that size.
   - **For us:** the most direct upgrade of our PBF. It could mean less bounce and squish per iteration.
10. **Unified Spray, Foam and Bubbles.** Ihmsen, Akinci, Akinci, Teschner, The Visual Computer 2012.
    - **New feature, and cheap.** Diffuse particles come from the liquid as a post-process and are sorted by where they are: spray above the surface, foam on it, bubbles under it. They are not part of the full solve. SPlisHSPlasH ships it.
    - **For us:** a big visual win on top of the GPU liquid. The gas puff discs could draw the spray and foam.
11. **Implicit viscosity.** Weiler, Koschier, Brand, Bender, Eurographics 2018.
    - **New feature:** very thick liquids (honey and caramel coil when poured), and melting objects through viscosity that varies with temperature.
    - **For us:** a Viscosity field on FluidEmitter, and maybe a melt or lava mode.
12. **MLS-MPM.** Hu et al., SIGGRAPH 2018.
    - **New feature:** one solver for water, snow, sand, jelly and mixed materials, with no neighbour search. The authors report about 2x faster than earlier MPM, and easier to implement.
    - **For us:** a separate "material" mode (snow, sand, goo) rather than a replacement for the liquid (see RESEARCH-fluid.md). It is a big rewrite.
13. **Rigid-fluid coupling.** Akinci et al., SIGGRAPH 2012 (from memory, not re-checked).
    - **New feature:** floating objects that also push the liquid back. A 2026 paper (Mathematics 14(11), 1845) does two-way rigid coupling through SDFs on the GPU. It is the "4 million particles in Unity" paper that was blocked last time; its claims are still unchecked.

## The gas

14. **GPU grid smoke (Stable Fluids on 3D textures).** GPU Gems 3, chapter 30 (Crane, Llamas, Tariq), from NVIDIA's "Smoke in a Box" demo and Hellgate: London.
    - **New feature:** real volumetric smoke that flows around obstacles and is ray-marched. The same grid can do fire and water.
    - **For us:** the "volumetric gas" option we talked about, as a grid in a box around each emitter, in D3D11 compute.
15. **Advection-reflection.** Zehnder, Narain, Thomaszewski, SIGGRAPH 2018.
    - **More detail for almost the same cost.** It loses about 100x less energy than the usual advection-projection step, and is a small change to an existing grid solver.
    - **For us:** only together with 14.
16. **Vortex particles.** Selle, Rasmussen, Fedkiw, SIGGRAPH 2005 (from memory).
    - **New feature:** swirls that persist and react to motion, carried by a few particles.
    - **For us:** a hand swipe could leave a lasting curl in our particle smoke. Curl noise is fixed noise and doesn't react.

## All-in-one

17. **Unified Particle Physics (NVIDIA FleX).** Macklin, Müller, Chentanez, Kim, SIGGRAPH 2014 (from memory).
    - **Both:** one position-based particle solver for liquid, gas, cloth, ropes, soft and rigid bodies, and they all interact.
    - **For us:** our liquid is already PBF. Adding shape-matched particle clusters (soft or rigid blobs) and cloth to the same GPU pass would give two-way liquid-softbody play.

## Looked at, poor fit for us

- **Lattice Boltzmann (FluidX3D):** very fast free-surface CFD on the GPU, but it only works inside a fixed grid box, needs a lot of memory, and doesn't give our particle look.
- **GIPC / IPC (2024):** guarantees nothing passes through anything, but it is too heavy for real-time avatars.
- **Particle flow maps (2024) and vortex particle flow maps (SIGGRAPH 2025):** vortices last much longer (smoke rings), but they are research-speed, not real time.
- **Heterogeneous Subspace Corrections (SIGGRAPH 2026):** a GPU Newton solver for mixed soft and stiff multibody scenes. It is aimed at accuracy, not game speed.
- **Unity SPH frameworks (Applied Sciences 2025, 15(17), 9706):** over 30 fps with up to 500k particles. They are a benchmark point, not a new solver.

## Sources

- [Vertex Block Descent (Roblox)](https://about.roblox.com/publications/vertex-block-descent)
- [Augmented VBD project page](https://graphics.cs.utah.edu/research/projects/avbd/), [paper](https://cemyuksel.com/research/papers/Augmented_VBD-SIGGRAPH25.pdf)
- [JGS2 (arXiv 2506.06494)](https://arxiv.org/abs/2506.06494v1)
- [MGPBD project page](https://chunleili.github.io/project-page-mgpbd), [arXiv 2505.13390](https://arxiv.org/abs/2505.13390)
- [Fast Complementary Dynamics via Skinning Eigenmodes](https://www.dgp.toronto.edu/projects/fast_complementary_dynamics_site)
- [FastLSM (SIGGRAPH history)](https://history.siggraph.org/?p=115391)
- [Position and Orientation Based Cosserat Rods](https://www.vci.rwth-aachen.de/publication/0550/)
- [OmniBody (80.lv)](https://80.lv/articles/this-soft-body-simulation-can-cut-peel-break-objects-in-real-time/)
- [Implicit Position-Based Fluids (PDF)](https://graphics.cs.utah.edu/research/projects/ipbf/ipbf.pdf), [ACM](https://dl.acm.org/doi/10.1145/3757377.3764005)
- [Unified Spray, Foam and Bubbles (PDF)](https://cg.informatik.uni-freiburg.de/publications/2012_CGI_sprayFoamBubbles.pdf), [SPlisHSPlasH FoamGenerator](https://splishsplash.readthedocs.io/en/latest/FoamGenerator.html)
- [Implicit Viscosity Solver (PDF)](https://animation.rwth-aachen.de/media/papers/2018-EG-ImplicitVisco.pdf)
- [MLS-MPM project page](https://yuanming.taichi.graphics/publication/2018-mlsmpm/)
- [Two-way SDF fluid-rigid coupling (Mathematics 2026)](https://www.mdpi.com/2227-7390/14/11/1845)
- [GPU Gems 3, chapter 30](https://developer.nvidia.com/gpugems/gpugems3/part-v-physics-simulation/chapter-30-real-time-simulation-and-rendering-3d-fluids)
- [Advection-Reflection (SIGGRAPH history)](https://history.siggraph.org/?p=100744)
- [FluidX3D thesis (Bayreuth)](https://epub.uni-bayreuth.de/5400/)
- [GIPC](https://hku-cg.github.io/publication/gipc-2024)
- [Vortex Particle Flow Maps (arXiv 2505.21946)](https://arxiv.org/abs/2505.21946)
- [Heterogeneous Subspace Corrections](https://wanghmin.github.io/publication/guo-2026-hsc/)
- [Large-scale particle fluids in Unity (Applied Sciences 2025)](https://www.mdpi.com/2076-3417/15/17/9706)
