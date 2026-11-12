# Softbody 0.19.0 (FrooxEngine plugin)

A selective softbody **component** for Resonite. One mesh, where the painted regions squish and wobble and
the rest stays firm. It uses no bones and no blendshapes: the plugin moves the mesh vertices directly.

0.27.0 (fluid and gas): **smoke swirls, foam and thick liquid.** These are the three solver prototypes Lenki picked from
SoftbodyRnD (see its RND-REPORT.md). All three are off by default, so existing worlds look the same. They run on the GPU only
(the Softbody renderer fork); on the CPU they do nothing, and the status says so.
- **`GasEmitter.Vortices`** (0 to 10, default 0; 1 is the usual amount): a hand or an object moving through the gas leaves
  small vortex rings behind it. They travel and keep stirring the smoke for a few seconds. The effect is strongest in smoke that
  lingers (a cloud, fog); a rising plume replaces the stirred smoke within about a second.
- **`FluidEmitter.Foam`** (0 to 10, default 0; 1 is the usual amount) and **`FoamLifetime`** (seconds, default 3): spray, foam
  and bubbles where the liquid splashes, breaks into waves or traps air. They are drawn as small soft discs, whiter than the
  liquid. Calm liquid makes none. At 2.5 cm particles it is mostly foam and bubbles; spray is rare.
- **`FluidEmitter.ThickLiquid`** (default off): a `Viscosity` above 1 becomes a real viscosity (an implicit solve) instead of
  extra smoothing passes. A thick liquid then keeps a spin and a rounded shape, sags and folds instead of slumping into a ramp,
  and is equally thick at every particle size. About 4 is honey, 10 is 0.08 m²/s, and it is held to 15 (0.9 m²/s). Above
  that the solve gets too slow. Water (1 and under) is untouched.
  - It costs GPU time: at Viscosity 15, about 10 ms a frame more than today's thick liquid for 100,000 particles of it
    (RTX 4070, SoftbodyRnD).
- **The smoke shader** (renderer fork only): the gas, and the spray, foam and bubbles, are drawn with the fork's own shader
  instead of Resonite's Unlit.
  - The smoke is lit by the world's sun, sky and lamps, and thick smoke shades itself.
  - Each puff is round and wispy, its wisps change as it ages, and it fades where it meets walls and floors (when the camera
    has a depth texture) and right next to your eyes.
  - It faces each eye, so it also looks right in mirrors and cameras, and is drawn far to near.
  - It comes as a shader bundle that the fork loads from `StreamingAssets\Softbody`. Without the bundle, the smoke is drawn as
    the old Unlit discs.
  - Nothing synced changed. Details: DESIGN-gas.md, "0.27.0: the smoke shader".
- **Install the plugin and the renderer fork together.** The channel is version 8. A 0.26.x fork and a 0.27.0 plugin (or the
  other way round) ignore each other: the liquid runs on the CPU, the gas is off, and the plugin retries the GPU every 30 s or so
  (the liquid restarts each time).
- New synced fields, so a new compatibility hash (`0/uDXxcV+UVEn6+RnAsMVg==`): everyone in a session needs 0.27.0.
- 0.27.0 also carries 0.26.6's SkinnedSoftbody fix (skin points flying after sudden moves and hits).
- Details and offline results: DESIGN-fluid.md, "0.27.0".

0.26.6 (SkinnedSoftbody): **no flying skin after sudden moves and hits.** Lenki, in game: after sudden movements or
collider hits, a few vertices flew around for about a quarter of a second. The logs had skin points up to 0.88 m off on
0.26.5, and up to 4.3 m on 0.26.2, so it is not new. Three causes, each with its own fix:
- **A collider's push turned into speed:** a point pushed out of a collider flew off. Now a point never leaves a contact
  faster along the push than the collider itself moves.
- **Sudden moves left the flesh behind:** a snap turn, a locomotion snap or a tracking jump short of the teleport reset
  left the flesh to whip back. Now any motion of the animation faster than 5 m/s (scaled with the avatar) carries the
  flesh along, as blendshape changes already do. The avatar's own bone colliders are capped the same way.
- **The avatar's own bone colliders plowed through lagging flesh:** they wedged points between the hips and a leg,
  where they stayed. Now they push the flesh along or out of the body only where they reach the skin's proper place.
  Pressing in still works: an arm against the side dents it as before.
- **Offline on Kitavali** (`tests/FleshJitterTests.cs`): a 0.5 m locomotion snap went from 190 mm and 17 m/s (with
  204 inverted tets, and a point left stuck 67 mm off) to 58 mm and 6.5 m/s, settling back. An arm slammed against the
  body went from 36 to 25 mm. A hand hitting at 4 m/s dents slightly deeper, 28 to 34 mm, because the skin follows it in
  instead of springing off.
- Built separately from 0.27.0, which has the same fix. No synced change and no channel change: the same hash as
  0.26.0 and the same renderer, so 0.26.0 to 0.26.6 can share a session.

0.26.5 (SkinnedSoftbody): **a saved layer keeps its colour.** Lenki's friend saved an avatar with its second layer set
to orange, and it came back yellow. Each new hue layer picked the first hue the other layers did not use, in its OnAwake.
OnAwake runs on every client that builds the layer (loading it, copying it, or receiving it over the network), before its
saved or synced values arrive. A client receiving the avatar builds every layer before it gets any values, so there the
pick saw the others still orange and moved on to yellow. Now OnAwake only sets orange. A free hue is picked only for a
layer added by hand, on the client of whoever added it, once the softbody has started there.
- Friends need 0.26.5 too: the wrong pick happened on the clients that received the avatar. A layer already saved with
  the wrong colour stays that way; set it back once.
- No synced change: the same hash as 0.26.0, so 0.26.0 to 0.26.5 can share a session.

0.26.4 (SkinnedSoftbody): **skin no longer sticks to flat boxes.** Lenki: the skin points got stuck on the mostly flat
boxes Resonite's colliders are made of, pulling the softbody away from the mesh until it moved very fast. A thin box is a
wall from both sides. Once the body's animated place had passed through one (a wall walked into and back out of, a seat sat
through), or a box had gone through the skin (pressed past the shell's limit, then pulled back out), the points stayed on
the old side and were pushed back there every substep. Now a contact that does not press into the body lets a point go
when it would hold it more than half a collision radius out of the body, or when the point's animated place is past the
collider (outside it, behind the face that pushes). The point then passes through the collider.
- Offline on Kitavali (boards walked through and back, a seat sat through, a box pressed 6 cm in and pulled out, an arm
  swept through the belly): 0.26.3 held the skin 4 to 11 cm out of the body, and up to 14 points were still up to 15 cm
  off the mesh a second later. 0.26.4 holds it out at most 1.5 cm, and a second later everything is back within 2 mm.
  A point pushed out through a board's face can still overshoot by up to 2.5 cm for a moment before it settles.
- Ordinary contacts are untouched: a box dent and a ball sliding across the belly come out bit-identical to 0.26.3, and
  the box, ball and squish tests give 0.26.3's numbers.
- The stats line shows how many points were let go ("N let go").
- No synced change: the same hash as 0.26.0, so 0.26.0 to 0.26.4 can share a session.

0.26.3 (SkinnedSoftbody): **blendshape normals.** Lenki's "blendshape test" (a skinned plane whose shape key "Cast" bends
it a long way) shaded wrong on the shaped part, and much worse with the softbody on. The softbody's part is fixed:
- The turned normals were measured on the mesh without its blendshapes, while the wobble is relative to the shaped mesh.
  On a strongly shaped part a bulge turned the normals about the wrong axis. Now they are measured on the shape the
  blendshapes make, and the turn is applied to what the renderer would show there (it adds the blendshape deltas itself).
- The tangents now turn with the normals and go up with them (`Positions|Normals|Tangents`), on meshes that have tangents.
  That only matters for normal maps, whose frame was skewed wherever the flesh moved.
- Offline, on a plane rolled to a half cylinder with a 3 cm bump on it: the shown normals are within 0.54 degrees of the
  real surface (0.26.2: 10.9), and the tangent stays at a right angle to the normal (0.26.2: skewed by up to 10.5 degrees).
- The rest is how blendshapes work, in Resonite as in Unity: the shown normal is the rest normal plus the weight times the
  shape's normal delta, a straight blend. "Cast" turns some normals by up to 139.5 degrees, so its in-between weights
  shade off: at 0.5 the normals are 21.9 degrees off the real surface on average (64.5 at most), fully on only 1.5
  (22.8 at most). The StaticMesh inspector's **Recalculate Blendshape Normals** only remakes the fully-on normals, so the
  blend in between stays straight. For normal maps, **Recalculate Blendshape Tangents (Mikktspace)** adds the tangents
  Resonite leaves out of an imported mesh's blendshapes (a new mesh asset; the softbody rebuilds once for it).
- No synced change: the same hash as 0.26.0, so 0.26.0 to 0.26.3 can share a session.

0.26.2: no more engine error when a world loses focus (switching worlds, closing Resonite). Softbody and SkinnedSoftbody
called `base.OnFocusChanged`, and the three components `base.OnWorldDestroy`; the engine's own versions of these throw by
design (only overrides may run), so the engine logged an error each time, and the focus handler itself never ran (the
softbody still stopped, one frame later). The base calls are gone, and a plugin check now refuses them. Same hash as 0.26.0.

0.26.1 (SkinnedSoftbody): fixes the **endless rebuild** Lenki saw on their avatar (2026-11-11, the first time 0.14.0's
instancing ran in game). About 2 s after every start the log said "the renderer's mesh changed: rebuilding", and the body
blinked. A StaticMesh hands out only an asset that counts as loaded, and the softbody's own copy of the mesh (a dynamic
asset) never did, so right after the swap the avatar's mesh was handed nothing. The copy now stands in for the shared
asset the way the engine checks: loaded, with the shared asset's URL, and relocated to it, so a StaticMesh that lets go of
it (destroyed, another URL) releases the shared asset normally instead of failing inside the engine. A softbody that has to
rebuild 6 times within a minute now pauses with a status instead of looping. No synced change: the same hash as 0.26.0
(`yqJCXkkC/dkxHMi0+m5P3g==`), so 0.26.0 and 0.26.1 can share a session. Later versions (0.20.0 to 0.26.0) are listed in
DESIGN-fluid.md.

0.19.0: Lenki's round of fluid requests and bugs. The GPU fluid ran in game on 0.18.0 (its start-up fix worked).
- **`RandomVelocity`** (new FluidEmitter field, m/s, default 0): every new particle, poured or filled, gets a random push
  in a random direction, up to that speed (3/4 of it on average). A Volume with a high value bursts like an explosion of
  water; a Pour sprays. Offline, a 30 cm volume at 4 m/s has burst 2.5 m out after half a second.
- **Higher limits:**
  - `Rate` up to 5000 L/s;
  - `ParticleSize` 0.5 to 50 cm (was 1 to 10 cm);
  - `Viscosity` up to 20 (was 1);
  - a world holds up to 262,144 particles on the GPU (was 131,072).
- **Viscosity above 1** takes extra XSPH passes per substep: each pass is at most a full average of the neighbours'
  velocities, so the liquid never overshoots.
  - Shear in a floating cube dies down much faster: 0.67 m/s left after 0.1 s at viscosity 1, 0.21 at 5, nothing at 20.
  - It does not make a block hold its shape: slumping comes from the pressure solve, which viscosity does not slow (a
    20 cm block spreads 16.6 cm at viscosity 1, 13.3 at 20).
  - Friction against colliders uses the viscosity only up to 1.
- **The surface lowers its own detail when it runs out of room** (Lenki: a patchwork of flat pieces at SurfaceDetail 3
  with 27,000 particles):
  - a thin sheet at detail 3 needed far more vertices than the surface holds, and what missed out left holes;
  - now the detail drops just enough to fit, and climbs back a tenth at a time while it uses under half the room;
  - a newly asked-for detail applies at once; the Status says "surface detail lowered to X to fit";
  - offline, a 20,000-particle sheet over 6.4 x 4.8 m at detail 3 builds at 2.27, with all of it covered (CPU: 1.54);
  - the GPU surface holds 400,000 vertices (renderer fork rebuilt after 0.19.0's install, at Lenki's request; was 131,072).
    The mesh draws all of its indices every frame (the unused ones as empty triangles), so a bigger cap costs a little GPU
    time even for a small puddle.
- **Long stray triangles after changing a setting** (GPU, Lenki's screenshot): a rebuilt GPU simulation (a new particle
  size) could get the old one's memory. Its index buffer past the surface's end then drew old triangles over the new
  vertices. Every GPU buffer now starts as zeros.
- **Typing a particle size no longer restarts the liquid at every key:**
  - typing "0.5" went through 0 (the default 2.5 cm), and every key restarted the liquid;
  - now a new size applies once it has stayed the same for half a second, and 0 keeps the last size;
  - the renderer log showed the liquid restarting between 2.5 and 10 cm dozens of times.
- **A moving box pushed liquid through the floor** (Lenki: "about 90% of the particles despawned"):
  - a box pressed into a puddle pushed particles down through the floor, and below it they fell and were removed;
  - each collision pass took the floor first and the box last, so the box had the last say;
  - now moving colliders go first and still ones last, so the floor always wins;
  - the plugin now tracks how world colliders move (the fluid had seen every world collider as standing still);
  - offline, a 20 cm box swept through a puddle, pressed into it and lifted, removes nothing on a 20 cm or a 2 mm floor
    (before: 10 and 61 of 16,384, CPU and GPU alike).
- Channel version 5, and a new compatibility hash: install the plugin and the renderer fork together.

0.18.0: Lenki's first try of the GPU fluid, and droplets.
- **The GPU fluid never started in 0.16.0 and 0.17.0** (fixed in the renderer fork):
  - the renderer opened its ready signal only after its first step of a plugin frame;
  - but the plugin sends frames only once it sees that signal, so neither ever began, and the liquid stayed on the CPU;
  - the renderer now runs an empty warm-up step as soon as its kernels compile, and then opens the signal.
- **`DropletSize`** (new FluidEmitter field, 0.5 to 3, default 1.5): Lenki asked for a "particle combine radius so small
  droplets aren't entirely invisible or tiny in comparison".
  - Particles with few neighbours (lone droplets, small clusters, thin sheets) are drawn that many times their own size,
    fading to their real size at 20 neighbours, so the liquid's body keeps its size. Bigger droplets also join up with
    their neighbours sooner.
  - Whatever the setting, no particle is drawn smaller than the surface grid can show. A lone particle's sphere (0.6 of
    its size) is smaller than the grid's half diagonal (0.87 of a cell), so before this, some droplets fell between the
    grid points and vanished.
  - Offline, 24 lone particles at awkward places are all drawn on both the CPU and the GPU (twice as big at 2), a cluster of
    4 doubles, and a settled pool's top does not move.
  - The world uses the largest of its emitters' values. Live.
- **12,000 particles sank through a very thin box floor** (Lenki, on 0.17.0):
  - a pile's pressure pushed particles wholly through the 2 mm box in one substep, where the plain push no longer saw
    them; the layer above lost its support and the pile followed;
  - boxes now have a swept test, like triangles: a particle whose path this substep went into a box, or clean through it,
    is put back on the face it came in by (CPU and GPU);
  - offline, 11,638 particles dropped on a 2 mm box floor now all stay on it for 10 s, at 2.5 and at 10 cm (before, half
    of them sank).
- **`SkinnedSoftbody.Simulating`** (added from the parent session): Lenki's smooth on/off fade for the skinned softbody,
  from 0 to 1, on the component and on each hue layer. See the SkinnedSoftbody table.
- The channel is version 4, and there is a new compatibility hash: install the plugin and the renderer fork together.

0.17.0: **particle size and surface detail**, two new FluidEmitter fields (CPU and GPU).
- `ParticleSize`: metres between particles at rest, 0.01 to 0.1 (default 0.025, 15 ml a particle; 0.05 is 125 ml). The
  world uses the largest of its emitters', and a new size starts the world's liquid over (Volume emitters fill again).
  - Bigger particles hold more liquid for the same cost, but the liquid is coarser: a stream or a drop is never thinner
    than a particle, so keep `NozzleRadius` at least one particle size.
  - `MaxParticles` counts particles, so the same cap holds more litres.
- `SurfaceDetail`: how fine the surface mesh is, 0.5 to 3 (default 1, a grid of 0.8 particle sizes; 2 is twice as fine,
  with about 4x the vertices). The world uses the largest of its emitters'. Live: the liquid stays.
  - On the CPU, a finer surface costs more of the worker's time (a settled pool's surface goes from 16 to 23 ms at 2).
  - On the GPU, the surface holds at most 400,000 vertices (131,072 until 0.19.0). Past that, it lowers its own detail
    to fit (0.19.0).
- The channel to the renderer is now version 3: a renderer fork from 0.16.0 and a 0.17.0 plugin (or the other way
  round) ignore each other, and the liquid runs on the CPU until both are 0.17.0.
- New compatibility hash: everyone in a session needs 0.17.0.

0.16.0: **the liquid on the GPU.** With the Softbody renderer fork (rebuilt with `make_softbody_renderer.py`), the focused
world's liquid is simulated on the graphics card, inside the renderer, and its surface is drawn straight from there. The
method, the liquids and the emitters are the CPU version's (see *Fluid on the GPU*).
- **New field:** `FluidEmitter.Solver` (Auto / CPU / GPU). Auto and GPU use the GPU when this client's renderer has it;
  CPU keeps the world's liquid on the CPU (any emitter set to CPU does).
- **Bigger limits:** up to 131,072 particles in a world on the GPU (20,000 on the CPU). On an RTX 4070, 20,000 particles
  with their surface take about 1 ms of GPU time a frame.
- **Falls back by itself:** without the fork, or if its GPU fluid stops answering, the liquid runs on the CPU as before.
- **New compatibility hash:** the new field means everyone in a session needs 0.16.0.
- **Seams between boxes (CPU too):** a particle that hits a box now leaves by the face it came in through. Before, a fast
  one hitting a wall near the floor could leave through the wall's bottom face and slide out under the wall. The GPU tests
  with 50,000 particles showed it.

0.15.2: Lenki's notes from the first fluid test.
- **Friction did nothing:** contact with a surface was only recorded when the last iteration pushed a particle out. A
  particle lying on a floor is pushed out by the first iteration, so friction and adhesion never applied to it. Any
  iteration's push counts now. Offline, a block of water sliding at 2 m/s goes 26 cm with Friction 1 and 202 cm with none.
- **MaxParticles** is a live cap: pouring stops at it, and lowering it removes the oldest particles over it.
- **ClearLiquid** leaves a Volume emitter empty until **Refill**, which fills it again where the emitter is now.
- **The emitter's status** (Status, Particles, WorldParticles, StepMs, SurfaceMs) showed nothing: a component's outputs
  can only be written from its own update, so the emitter now shows what the fluid runtime last reported.
- The emitters' logic (fills, Refill, ClearLiquid, caps, removal, kill box) moved into the engine-free
  `Core/Fluid/FluidDirector`, tested offline.
- Same hash as 0.15.0.

0.15.1: fixes the fluid freeze in Lenki's first test.
- **What happened:** a stream poured from a height fell through mesh floors (single-sided triangles only caught a particle
  within two radii of their plane, and a falling particle moves further than that per substep). The water fell to the kill
  height 100 m down, and the surface's grid stretched over all of it. The world thread waited for each step, so the game
  froze.
- **Swept collision:** a particle that crosses a triangle's front during a substep is stopped there.
- **Removed liquid:** liquid more than 50 m from every emitter (20 m below the lowest) is removed.
- **A sparse surface grid:** small blocks only where particles are, so its cost follows the particle count, never the spread.
- **Mesh triangles:** they are taken only from cells holding particles (at most 8,000).
- **The world thread:** it never waits for a step; the surface is rebuilt less often while steps run long.
- **Offline:** pouring for 5 s from 1.5 m onto a single-sided mesh floor leaves nothing below it, with the worst frame 17 ms. A
  stream poured into nothing keeps the surface under 3 ms.
- Same hash as 0.15.0.

0.15.0: a third physics type, **FluidEmitter**: a particle liquid in the spirit of GWater2 (see *Fluid*). The CPU version
comes first; a GPU version in the renderer fork comes next (DESIGN-fluid.md). New component: the hash is
`YL0Byl04USJdHANaxlmwzQ==`, everyone in a session needs 0.15.0.

0.14.0 (SkinnedSoftbody): **instancing**. Two avatars wearing the same model (every StaticMesh with the same URL shares
one mesh asset) each run and show their own softbody: each running SkinnedSoftbody gives its avatar's StaticMesh a private
copy of the mesh on your client and sends its jiggle there, so the shared asset, and everyone else wearing that model, is
never touched (before, the second softbody waited and showed the first one's jiggle). The copy costs one more upload of
the mesh when the softbody starts and its GPU memory while it runs; it goes back to the shared mesh when the softbody
stops. Only two SkinnedSoftbodies on the same StaticMesh still wait for each other. No synced change: the hash is
0.13.0's, so 0.13.0 and 0.14.0 clients can be in one session.

0.13.0 (SkinnedSoftbody): **hue layers**. `HueLayers` is a list of more soft layers on the same mesh, each a separate
simulation with its own settings (shell, stiffness, follow, damping, gravity, collision toggles, and the rest), for the parts
painted in its hue. See *Hue layers*. The brush's menu gets a **Hue** item that cycles the seven hues (and none), with Soft / Half / Firm in the
current hue. New types, so a new compatibility hash, `yzYN/NzZG2/y5sxvhmW6nw==`: everyone in a session needs 0.13.0.

0.12.1 (SkinnedSoftbody): skin pressed against a collider is **shown against it** (Lenki's screenshots: a belly pressed on a
box showed a gap, a bowed and creased patch, and spikes at the box's edge). See *Shown against the collider* under *How
contacts push*. Offline, a 10 cm box face pressed 1 cm into Kitavali's belly: the patch sat 3.1 mm in front of the face, bowed
by 2.0 mm (std), shaded 1.1 degrees off it; now 0.4 mm (just behind the face), 0.8 mm, 0.5 degrees. A skin point inside a box
leaves through the face toward the body when that face is about as near as the nearest one. No new fields: same data model
and hash as 0.12.0, so friends on 0.12.0 can join.

0.12.0 (SkinnedSoftbody):
- **Collision toggles for players**, in two sets: `CollideSelf` with `SelfHead` / `SelfBody` / `SelfHands` for the avatar's own
  bone colliders, and `CollideOthers` with `OthersHead` / `OthersBody` / `OthersHands` for other users'. A bone collider is Head
  under the VRIK head bone, Hands under a hand bone (fingers too), Body otherwise (torso, arms, legs, feet). `Collide` is still
  the switch for everything.
- **`VolumePreservation`** (0..1, default 0.5): the volume a dent pushes in comes back out as a bulge in the skin around it.
- **`CullDistance`** (metres, default 20, 0 = never): past it from you, your client stops simulating and colliding the
  softbody and shows it at rest.
- New fields: the compatibility hash is `FngJCj2v4KqIbOI76K4nzQ==`, everyone in a session needs 0.12.0.

0.11.5: dents are lit like the thing pressing them (render normals of pressed skin blend toward the inverse of the collider's
normal), and the push direction blends smoothly between the collider's normal and the skin's. Same data model as 0.11.3.

0.11.4: contacts squish instead of sliding (Lenki's screenshot: a ball pressed into an inflated belly shoved the skin points
around it and folded the skin at its rim). See *How contacts push*. Inflating blendshapes now thicken the soft shell. Same
data model as 0.11.3.

0.11.3:
- **Crash fix:** a big tet build (SimPoints near 4000, especially with more Layers) overflowed a worker thread's stack and
  took Resonite down. A `stackalloc` sat inside a per-tet loop; it is gone, and that compiler warning (CA2014) is now a
  build error. Tested: 4000 points and 3 layers (about 60,000 tets) build on a 256 KB stack.
- **Collision spheres follow the skin:** each one is half the spacing of its neighbours where the animation puts them this
  frame, so they scale with the avatar and follow blendshapes.
- **SpawnBrush** button on both components' inspectors: a brush appears in front of whoever presses it.
- **Brush colour from its own context menu:** Change color (Resonite's colour picker, live), Hue (0.13.0: cycles the
  ROYGBIV presets) and Soft / Half / Firm presets in that hue;
  the four mode items became one cycling Mode item.
- The new buttons are SyncMethods, which the engine counts in the data model: the compatibility hash is now
  `av8ILA4eNKIB6oty9S34pw==`, so everyone in a session needs 0.11.3 (not 0.11.0 to 0.11.2).

0.11.2: `SkinnedSoftbody` collides with other users' bone colliders (the capsules VRIKAvatar keeps on every major bone) and
with the avatar's **own** bone colliders (self-collision), each ignoring the flesh it already touches in the bind pose. Same
data model as 0.11.0.

0.11.1: `SkinnedSoftbody` collides only with solid things: world colliders marked `CharacterCollider` (every collider shape,
mesh colliders included, through cached triangle trees) and users' colliders, culled per point with bounding boxes; the ground
ray is gone. Same data model as 0.11.0.

0.11.0 adds **`SkinnedSoftbody`** (`[Softbody]Softbody.SkinnedSoftbody`, under **Physics**): soft flesh for avatars. The
painted part of a SkinnedMeshRenderer becomes a shell of tetrahedra under the skin, simulated with XPBD, instead of a box
lattice. See *SkinnedSoftbody* below and `DESIGN-skinned.md`. It is a new component type, so the data model changed
again: everyone in a session needs 0.11.0. The lattice `Softbody` is unchanged and stays the one for static meshes.

0.10.1 fixes what the debug views showed on an avatar: an all-firm mesh no longer builds a full pinned box, the floor
no longer pushes the body out of its rest shape, cage points follow the bones of the soft vertices around them, and
bind-space offsets are no longer amplified where bones are turned far apart. Same data model as 0.10.0.

0.10.0 adds **debug views**: `DebugCells` and `DebugColliders` toggles on the component draw the cage and what it
collides with. See *Debug views* below. 0.10.0 changes the data model (new fields and a new type), so everyone in a
session needs 0.10.0, not an older build.

0.9.0 keeps skinned meshes **GPU-skinned**: with the renderer-side plugin **SoftbodyRenderer** (a BepInEx 5 plugin for
Resonite's Unity renderer, in `Renderer/`), an avatar's mesh stays a normal skinned mesh and the plugin only uploads the
wobble, in bind space. Without it, skinned Softbodies use 0.8.0's CPU path, as before. See *Skinned meshes* and
*Renderer patch* below.

0.8.0 adds **skinned meshes (experimental)**: a Softbody on an avatar's `SkinnedMeshRenderer`; the cage follows the
skeleton and the painted parts wobble on top of the bones' motion. See *Skinned meshes* below; read its limits first.

0.4.0 adds an **in-game paint brush** (`[Softbody]Softbody.SoftbodyBrush`, under **Tools**): paint the softness
map onto a running Softbody in the world, no Blender round trip. See *Paint brush* below.

From 0.2.0 this is a real component, `[Softbody]Softbody.Softbody`, under **Physics** in the component
browser, and every setting lives on it in the inspector. It is a plugin loaded with `-LoadAssembly`, not an
RML mod. The trade-off: **everyone in a session hosted by a plugin user needs the plugin too**. See *Join
compatibility* below.

## Fluid (0.15.0)

A liquid made of particles (Position Based Fluids, the method NVIDIA FleX and so GWater2 use), simulated on your client
off the world thread, with its surface rebuilt as a mesh every frame and drawn with refraction and a glossy layer in each
liquid's colour.

**FluidEmitter** (Physics in the component browser), on any slot:
- `Mode`: **Pour** sends liquid out along the slot's +Z (the blue arrow) at `Speed`, `Rate` litres a second, across a nozzle
  `NozzleRadius` wide, while `Pouring` is on; **Volume** fills a `VolumeSize` block centred on the slot (scaled with it)
  when it starts and when you press **Refill**. **ClearLiquid** removes this emitter's liquid. Both buttons act on every
  client.
- `MaxParticles` (4000): this emitter's cap; a world holds at most 20,000 particles on your client. At the default size a
  particle is about 15 ml (64 per litre).
- **Its own liquid**, live: `Viscosity` (water 0.02, honey 0.6), `Cohesion` (how it holds together: water 0.3, slime 0.9),
  `Adhesion` (stickiness), `Friction`, `Density` (relative to water: a lighter liquid floats on a heavier one), `Gravity`,
  `Damping`, `Lifetime` (seconds, 0 = until cleared) and `Color` (alpha is how opaque it is).
- `Collide` with solid world colliders (CharacterCollider on), and `CollideHead` / `CollideBody` / `CollideHands`: users'
  bone colliders (or their dynamic-bone spheres) push the water around, one way (the water never pushes anything back).
- `CullDistance` (30 m): further than this from every emitter, your client stops simulating the liquid (it stays shown).
- `Solver` (0.16.0): Auto / CPU / GPU, see *Fluid on the GPU*.
- `ParticleSize` (0.025 m) and `SurfaceDetail` (1) (0.17.0): the world's resolution and the surface's (the largest of
  its emitters'; a new particle size starts the liquid over, the detail is live).
- `DropletSize` (1.5) (0.18.0): how big lone droplets, small clusters and thin sheets are drawn, fading to their real size
  in the liquid's body (the largest of its emitters'; live).
- `RandomVelocity` (0 m/s) (0.19.0): a random push for every new particle, up to that speed (a Volume explodes, a Pour
  sprays).
- `ThickLiquid` (off), `Foam` (0) and `FoamLifetime` (3 s) (0.27.0, GPU only): a real viscosity for thick liquids, and spray,
  foam and bubbles (see 0.27.0 at the top). `GasEmitter.Vortices` (0) is the gas's part.
- Status: `Status`, `Particles` (this emitter's), `WorldParticles`, `StepMs` and `SurfaceMs` (on a worker thread).

**How it works:**
- **One simulation per world:** every emitter's particles share it, so liquids from different emitters meet, mix, layer by
  density and push each other. A pair of different liquids barely mixes: a quarter of the cohesion, the mean viscosity.
- **Local:** emitters and their settings are synced, but each client simulates its own splashes, so two people see the
  same pour but not the same drops. Nothing synced is written.
- **Each frame:** gravity and damping, a prediction, then a spatial hash grid that also sorts the particles by cell.
  Then three iterations of a density constraint per substep (two substeps), with corrections shared by inverse mass, so
  light liquids rise (buoyancy). After each, a collision pass against the same shapes the softbodies use (mesh colliders
  as their triangles near the liquid). Then the velocity, XSPH viscosity, cohesion (attraction only), and friction and
  adhesion on colliders.
- **Against blow-ups:** nothing moves further than half a particle per iteration, a particle pushed into a box leaves the
  way it came (so water pressed into the seam between a floor and a wall stays in the tank), water does not bounce off
  walls, and speed grows by at most 10 g.
- **The surface:** a smoothed distance field (Zhu and Bridson), so a lone drop is a bead and a pool has a flat top. It is
  turned into a mesh by surface nets, every frame, on the worker. It shows on a local mesh with a `RefractMaterial` and a
  `PBS_VertexColorMetallic` (alpha blend).

**Offline** (16 threads):
- a 36 L dam break in a 60 x 40 cm tank stays in it, at 1.01 of rest density;
- pouring 2 L/s for a second gives 1.95 L;
- a 20 cm block of water spreads to 69 cm, honey to 26 cm;
- a cohesive blob kicked apart stays 7 cm across, against 26 cm without cohesion;
- oil (density 0.6) under water rises above it;
- poured water never ends inside a solid box;
- a 10 cm hand sphere sweeping through a tank pushes the water ahead of it;
- the surface of a still pool is flat on top and wound outward, and a lone drop's surface is closed.

**Cost:**

| Particles | Step | Surface |
|---|---|---|
| 5,000 | 3.5 ms | 4 to 6 ms |
| 10,000 | 6.5 ms | 4 to 6 ms |
| 20,000 | 14 ms | about 9 ms |

All of it runs on worker threads. The GPU version is meant for bigger amounts.

**Limits:**
- Boxes are solid: a tank or a cup needs walls (several boxes) or a mesh collider.
- Water pushes nothing.
- There is no foam or spray, no debug view yet, and one particle size for everything (2.5 cm).

## Fluid on the GPU (0.16.0)

The same liquid, simulated by the Softbody renderer fork on the graphics card (Direct3D 11 compute, on Unity's own device,
on the render thread). It needs the fork (`RendererSoftbody`, rebuilt with `make_softbody_renderer.py`, started with the
`-RendererPath` launch option); without it, nothing changes.

**How it fits together:**
- **The channel:** the plugin and the renderer share a block of memory (`Local\Softbody.Fluid.v1`). Each update the focused
  world writes its emitters (position, settings, liquid) and the colliders near its liquid. Meshes go in as their triangles
  within the liquid's bounds, at most 8,192 shapes. The renderer writes back the particle counts per emitter, the liquid's
  bounds and its timings.
- **Choosing the GPU:** the renderer opens the event `Local\Softbody.Fluid.Gpu.v1` once its kernels have compiled and the
  first step ran. While it exists and no emitter is set to `Solver` CPU, the plugin stops simulating that world on the CPU.
  If the renderer reports an error, or stops answering for 5 s, the liquid goes back to the CPU for 30 s.
- **In the renderer** (`GpuFluid/`, linked into Renderite.Unity):
  - `Hlsl/FluidSim.hlsl` holds 31 compute kernels, compiled at start-up with Windows' `d3dcompiler_47.dll` (about 3 s, off
    the main thread).
  - `D3D/GpuFluidSim.cs` drives them through SharpDX.
  - `D3D/GpuFluidDirector.cs` does what the emitters ask: fills, pours, Refill, ClearLiquid, caps and removal.
  - `D3D/GpuShapes.cs` builds the colliders' grid.
  - `Unity/SoftbodyFluidHost.cs` reads the channel and draws.
- **Drawing:** the renderer finds this world's fluid renderer (the plugin's local "Softbody fluid" slot) by its mesh and
  hangs a child under it. The child draws the GPU's surface with that renderer's own materials (refraction and gloss). The
  surface is copied from the compute buffers straight into the child mesh's vertex and index buffers, every frame, with no
  trip through the CPU.
- **Differences from the CPU version:**
  - the counts the emitters show are a few frames old;
  - a lowered `MaxParticles` removes exactly the particles over it, but not the oldest ones;
  - the surface is rebuilt every frame (no throttling: about 0.3 ms);
  - only the focused world simulates, and switching worlds starts the GPU's liquid over (Volume emitters fill again).

**Offline** (`dotnet run -c Release --project tests -- gpu-fluid`, on the machine's own GPU, fed the same records the
plugin writes):
- the CPU version's checks pass, from the dam break to the closed drop;
- a block of water sliding at 2 m/s goes 203 cm with no friction and 26 cm with Friction 1;
- a stream onto a single-sided mesh floor of 3,200 triangles stays on it, at 0.07 ms of render-thread time a frame;
- a frame written by the plugin's side reaches the renderer's side whole, and the counts come back per emitter.

**Cost** (RTX 4070, surface every frame, median GPU time; it varies with the card's clocks, which the tests let drop):

| Particles | GPU |
|---|---|
| 10,000 | 0.5 to 4 ms |
| 20,000 | 0.8 to 3 ms |
| 50,000 | 2 to 5 ms |
| 100,000 | 4 to 5 ms |

In all of these, preparing a frame on the render thread takes about 0.1 ms. Nothing gets out of a 2 m tank, even with
100,000 particles.

## SkinnedSoftbody (0.11.0)

Soft flesh on an avatar, for the parts you paint. Put it on the avatar's body slot (it picks the `SkinnedMeshRenderer` on
its slot, or under it), then paint the parts that should move with the Softbody brush. It starts **all firm**
(`StartAs AllFirm`): nothing moves until something is painted bright. With `StartAs MeshColors`, the mesh's own vertex
colours are the softness map.

**Needs the Softbody renderer**: the `-RendererPath` fork (or the SoftbodyRenderer patch). Without it the component says so
in `Status` and does nothing; it never falls back to replacing the avatar's mesh.

How it works (details in `DESIGN-skinned.md`):
- **The shell.** The painted region (welded across UV seams) is reduced to `SimPoints` surface points by edge collapses that
  keep original vertices, so every point is a real vertex with its own bone weights and blendshapes. Each point gets a
  partner pushed inward toward its own bones, `Depth` of the way (never past the bone; flaps and fluff whose bone lies
  beside them get a thin membrane); `Layers` layers of tets fill the gap. The inner layer and a ring around the paint are
  pinned to the bones, and softness eases in over 3 rings from that border.
- **The step.** Every frame each shell vertex's target is its point's own skinning (after its blendshapes). The tets'
  rest shape is that animated shape, so the flesh never fights a bent joint or a blendshape; XPBD (`Substeps` small steps,
  Neo-Hookean shape and volume constraints, follow springs scaled by softness, damping relative to the animation)
  adds the jiggle. A jump of the body further than 0.75 m in one frame (respawn, teleport) resets it.
- **The output.** Each skin point's offset goes back to bind space through its bones' rotation and scale, is blended
  smoothly onto the painted render vertices, and normals are turned to match (each vertex follows the triangles on its own
  side, so cards and fins keep both faces). Only those positions and normals are uploaded; the renderer skins them with
  everything else.
- **Threads.** The tet build and every step run on worker threads. A step started in one frame is collected and uploaded
  the next (one frame behind the pose, which the renderer applies itself). `FrameMs` is what the world thread pays,
  `StepMs` what a worker pays.
- **Rebuilds.** The tets are rebuilt only when the mesh, the paint (0.3 s after the last dab), the renderer, the channel or
  `SimPoints`/`Depth`/`Layers` change. Everything else is live.

| Field | Default | What it does |
|---|---|---|
| `Renderer` | the SkinnedMeshRenderer on this slot or under it | what to make soft |
| `SoftnessChannel`, `InvertSoftness` | Value, off | as on Softbody |
| `StartAs` | AllFirm | what the paint starts from |
| `Simulating` | 1 | 0..1, how much of the softbody is shown on top of the paint: 1 full, 0.5 half the jiggle and dents, 0 off (the mesh as animated, nothing simulated). Live and free to change every frame (it does not wait for the running step like the other settings), so fade it, by hand or with a drive, to turn the softbody on or off smoothly. Every hue layer follows it (0.18.0) |
| `SimPoints` | 800 | soft surface points (50..4000); 800 steps in about 2 ms on a worker |
| `Depth` | 0.5 | shell depth, as a fraction of the way from the skin to the bone |
| `Layers` | 1 | tet layers through the shell (1..3) |
| `Stiffness` | 8 kPa | Young's modulus; lower = wobblier |
| `Volume` | 0.45 | Poisson ratio: how strongly it keeps its volume |
| `VolumePreservation` | 0.5 | 0..1: how much of a dent's volume bulges back out in the skin around it (0.12.0) |
| `BoneDensity` | 0.5 | deep flesh is (1 + 3 x this) times stiffer than at the skin |
| `Follow` | 1.5 Hz | pull back to the animated place (firmer paint pulls harder) |
| `Damping` | 4 /s | how fast the wobble dies down |
| `Gravity` | 1 | x 9.81 |
| `BlendshapeInertia` | 0 | 0: blendshapes carry the flesh; 1: it lags behind them too |
| `Substeps` | 8 | solver steps per frame |
| `Collide` | on | collide at all: solid world colliders (`CharacterCollider` on) and the players below; see *What it collides with*. Each skin point's sphere is half its animated neighbour spacing (0.11.3: follows scale and blendshapes) |
| `CollideSelf`, `SelfHead`, `SelfBody`, `SelfHands` | on | self-collision with this avatar's own bone colliders, by part (0.12.0) |
| `CollideOthers`, `OthersHead`, `OthersBody`, `OthersHands` | on | other users' colliders, by part (0.12.0) |
| `Budget` | 4.5 ms | pauses itself if the world thread pays more than this per frame |
| `HueLayers` | empty | more soft layers, one per hue, each its own simulation with its own settings; see *Hue layers* (0.13.0) |
| `CullDistance` | 20 m | past this distance from you, your client stops simulating and colliding it and shows it at rest; resumes inside 90 % of it (0 = never) (0.12.0) |
| `DebugTets`, `DebugColliders` | off | draw the tets / collision spheres and colliders (local, from each client's own sim) |

Status: `Status`, `Points`, `Tets`, `MovingVertices`, `StepMs`, `FrameMs`, `Dabs`, `LastError` (local RawOutputs).

### What it collides with (0.11.1)

Only solid things, and nothing is raycast:
- **World colliders with `CharacterCollider` on** (what users can stand on and bump into), of type Static or Active, not
  part of the avatar or the component's slot. Floors are just colliders like any other. Every shape is exact: Box, Sphere,
  Capsule, Cylinder, Cone, Triangle, and Mesh colliders (Front or DualSided, through a triangle tree built once per mesh on a
  worker and shared by every softbody); a ConvexHull collider uses its mesh's faces (exact for convex meshes).
- **Other users' bone colliders** (0.11.2): the capsules, head sphere and hand/foot boxes VRIKAvatar puts on every major bone
  of an avatar (on the bone slots, not visible). The engine enables them only while users are close; far away a user has a
  single big collider, which is never used. They are recognised exactly through the avatar's own list of them. Users without
  them fall back to the spheres the engine keeps for dynamic bones (head, body, limbs, hands).
- **The avatar's own bone colliders: self-collision** (0.11.2), whether the engine has them enabled or not. When the tets are
  built, each one is taken into the mesh's bind pose through its bone and tested against the shell; the skin points it already
  touches there are stored as a bit per point and never paired with it (no cost per frame). So the torso's own capsules do not
  push its flesh out, while an arm that is clear of the chest at rest presses into it. Offline on Kitavali: without the masks
  the own capsules push the resting torso out by 62 mm; with them 0.003 mm; an arm swung down against the side presses the
  flesh in, and only the arm does.

#### Hue layers (0.13.0)

One SkinnedSoftbody can run several soft regions with different settings, a jiggly chest and a firmer belly for instance.
The settings above are the **base layer**. Each entry of `HueLayers` is one more layer:
- `Hue` (Red, Orange, Yellow, Green, Blue, Indigo, Violet) picks its paint: the parts painted in that hue are its region, and
  the paint's brightness is how soft they are (black is firm, full brightness fully soft). The brush's **Hue** menu item
  paints exactly these hues; by hand: orange is (1, 0.5, 0), yellow (1, 1, 0), green (0, 1, 0), blue (0, 0, 1), indigo
  (0.5, 0, 1), violet (1, 0, 1), in the mesh's colour space. A hue between two band centres is shared by both
  layers, and a colour with less saturation is shared with the base layer, so a gradient where two regions meet hands one
  over to the other smoothly (each simulation pins the edge of its own region, so two regions painted edge to edge without a
  gradient meet at a firm seam). Grey, white and black, and hues no layer owns, stay with the base layer, whatever its
  `SoftnessChannel`; the hue layers always read brightness.
- `Enabled`: off is as if the layer were not in the list (its hue goes back to the base layer).
- `Simulating` (0..1, 0.18.0): this layer's own fade, times the component's `Simulating`: fade it to turn just this colour's
  region on or off smoothly. At 0 the layer is not stepped and its region shows as animated; it starts again from rest.
- Its own `SimPoints` (400 by default), `Depth`, `Layers`, `Stiffness`, `Volume`, `VolumePreservation`, `BoneDensity`,
  `Follow`, `Damping`, `Gravity`, `BlendshapeInertia`, `Substeps` and collision toggles (`Collide`, `CollideSelf`,
  `SelfHead`/`SelfBody`/`SelfHands`, `CollideOthers`, `OthersHead`/`OthersBody`/`OthersHands`), meaning the same as above.
- A new entry takes the first hue no other entry uses, starting at orange. A hue listed twice: the first entry gets it.
- `Budget` and `CullDistance` stay on the component (they are about the whole avatar).

Each layer is a separate tet shell and solver: the layers do not collide with each other, and their costs add up (the
point cap counts every layer's `SimPoints`). They are stepped one after another in the same worker task, the colliders are
searched for once and handed to each, and their offsets are merged into the one upload: a vertex one layer moves shows
exactly that layer, a vertex two layers share the sum of both. Changing a layer's `Hue`, `Enabled` or a shell setting
rebuilds the tets; a paint change rebuilds only the layers whose region changed. Offline, on Kitavali: a white belly
(stiffness 20 kPa, follow 3 Hz) and an orange chest (1 kPa, 0.6 Hz) lifted 5 cm together lag by 7 mm and 26 mm, 81 vertices
in the gradient between them moved by both.

#### How contacts push (0.11.4)

A collider used to push a skin point straight out along its own surface, which near the rim of a pressing ball is sideways:
the points slid around the ball, bunched up at its edge and folded the skin. Now:
- **Moving colliders sweep** through the substeps (from where they were last frame to where they are), instead of jumping
  once per frame.
- **Friction** (Coulomb, 0.8): relative to the collider's own motion, a touched point grips it and moves with it, unless it
  is pushed hard sideways. A ball pressing in drags the skin in with it.
- **Pushes go into the body:** a smooth blend (0.11.5) of the collider's push and the skin's animated inward direction:
  mostly inward where the skin faces the collider, the collider's own push where it grazes it, as far as it takes to clear it.
- **Dents are lit like the collider (0.11.5):** skin pressed against something takes its shape, so its normal there is the
  inverse of the collider's normal. Each touching skin point records it (fading over about 0.1 s after release), it goes to
  bind space through the point's bones and is spread onto the render vertices like the offsets, and their normals blend
  toward it. Offline, inside a 3 cm dent from a 6 cm ball, the normals were 47 degrees off the ball's shape on average from
  the squeezed geometry alone; blended, 8. A per-contact cost of a few nanoseconds.
- **No deeper than half the shell:** a point stops at half the shell's animated thickness and is then not pushed at all; a
  collider pressing deeper goes through the skin there. Deeper dents turned the shell inside out against its pinned inner layer.
  `Depth` sets how thick the shell is, so how deep a dent can get.
- **Inflating blendshapes thicken the shell:** the inner layers do not follow the outward part of a blendshape, so an
  inflated belly gets a thick, squishy shell instead of carrying a thin one outward.
- **Shown against the collider (0.12.1):** the simulation keeps every skin point a collision radius (about 8 mm on Kitavali)
  away from colliders, and the render surface between the points is a smooth blend of their offsets, so a pressed patch
  hovered in front of a flat face and kept the belly's roundness. Three steps, all on what is shown (the simulation is
  unchanged), and all smooth fields spread through the same weights as the offsets, so none can put steps between
  neighbouring vertices (snapping each render vertex onto the collider did that: spikes at a box's edge):
  1. each touching point's shown offset moves its distance to the collider closer, along its contact normal, faded by its
     contact weight and by how squarely the collider faces the skin;
  2. the rest shape's roundness between the points, the part standing out toward the collider, is taken out where the skin
     is fully pressed (a navel stays a dimple; nothing is flattened where a collider is beside the skin rather than on it);
  3. a smooth fit: each pressed render vertex's shown place is measured against the collider its points touched, the
     distances are averaged onto the points and spread back, at most one collision radius. This fixes round colliders,
     which the flattening alone left sagging into them (a ball pressed 1 cm: 2.6 mm), and leaves real gaps at a patch's rim.
  Offline: a box face pressed flat, see 0.12.1 above; a 6 cm ball pressed 1 cm into fully soft skin, the deepest render
  vertex 0.8 mm inside it (0.12.0: 2.4 mm); a box's edge across the belly, no mesh edge near it changes length more than in
  0.12.0 (4.1 mm at worst, in a navel the edge crosses). Painted partly firm skin still shows a collider sinking in by its
  firmness, as before. Costs about 0.1 ms per frame for a hand on the belly, 0.7 ms with the whole belly against a wall, on
  the worker.

- **Volume preservation (0.12.0):** after each step the volume the touching skin points were pushed in is handed to the free
  skin points up to three neighbour rings around the dent (weights 1, 0.6, 0.3), scaled by `VolumePreservation`, as an outward
  offset of their animated place (eased over about 0.05 s, capped at half the shell's thickness). Offline, with 1: a 55 cm3
  dent gives a 53 cm3 bulge, the skin around it rising 2.3 mm on average and up to 7 mm.

Offline, a 6 cm ball pressed 3 cm into Kitavali's belly: the old push slid points up to 54 mm sideways and turned 61 tets
inside out; now 17 mm and one tet, and the worst normal turns 21 degrees instead of 35.

Each frame the world's broadphase (10 times a second) and the users' sphere list give the colliders in a box around the body;
their shapes are rebuilt from their slots every frame, so moving ones push the same frame. On the worker, at the start of each
step, every skin point gets its reach (two radii plus how far it can travel this frame); a grid over the points pairs each
nearby shape only with the points that can reach it, a mesh collider only contributes the triangles near each point (through its
tree), and every substep first checks a pair's bounding box (and a triangle's plane) before any real geometry. Offline, a
usual scene (floor box and a 28,800-triangle floor mesh at the feet, a table edge, two hands on the belly) adds 0.02 ms per
frame (0.12.1: about 0.15 ms, with showing the contacts); the worst case tested (the whole belly front pressed into a finely
tessellated wall while bobbing) adds about 3 ms, on the worker. The flesh does not push anything back.

Limits: one SkinnedSoftbody per StaticMesh (0.13.0: several regions with their own settings go into `HueLayers`; 0.14.0:
avatars sharing one mesh asset each get their own copy; the lattice Softbody is still one per mesh asset); paint shows as colour only on meshes that have vertex colours (it always sets the softness); contact friction is tuned only
offline; self-collision only through the avatar's bone colliders (a part already touched by a collider at rest is never
pushed by that collider). Offline it is tested on a real
avatar body (`tests/data`, see *Build / test*); it has not been run in game yet.

## Install (Resonite closed)

1. Copy `bin\Release\net10.0\Softbody.dll` into `Resonite\Libraries\`, next to `ResoniteModLoader.dll`.
   - Copy only the DLL; its symbols are embedded.
   - The file must stay writable. The engine rewrites ("weaves") it in place on the first start, which is
     the log line `POSTX Processed Assembly: Libraries/Softbody.dll`.
2. Add a second `-LoadAssembly` to the Steam launch options, keeping RML's:

       -LoadAssembly Libraries/ResoniteModLoader.dll -LoadAssembly Libraries/Softbody.dll

   - The path is relative to the Resonite folder.
   - A wrong path is ignored without any message. If the `Loaded Extra Assembly: Libraries/Softbody.dll`
     line is missing from the log, the path is wrong.
3. Do **not** put this DLL, or the 0.1.0 RML build of it, into `rml_mods`. Both are named `Softbody`.
4. Optional, for GPU-skinned avatars: install the renderer patch (*Renderer patch* below). Everything else works without it.
5. Everyone who wants to join a session hosted by a plugin user needs **the same build** of the DLL. A build
   with different fields has a different compatibility hash.
   - 0.9.0 and 0.8.0 change no field or type, so their hash is 0.7.0's: `WoM0oawSj2JVus+1HkcIzQ==`. 0.7.0, 0.8.0 and 0.9.0 clients can be in one session; only 0.8.0+ clients run skinned softbodies (0.7.0 shows "not supported" for them). Each client picks the GPU or CPU path for itself.
   - 0.7.0 added the `Budget` field (0.6.0 added `StartAs`), so its hash was new: `WoM0oawSj2JVus+1HkcIzQ==` (0.6.0 was `bFsqpaiEsdcMTagr/7F9cg==`).
     The earlier hashes were 0.5.0 `3xw6vkS/HV0PL09GrVu1vw==`, 0.4.0 `fpuUQOVGqhyd/L5M3AatrA==` and 0.3.0 `QFTH7I5BN7vLyNKRjq74pw==`.
     Friends need 0.7.0, 0.8.0 or 0.9.0.

## Use

1. Put a plain `MeshRenderer` with a `StaticMesh` on a slot (or use an avatar's `SkinnedMeshRenderer`: *Skinned meshes*).
2. Paint softness into the vertex colours. By default the softness is the colour's **HSV value** (brightness, max(r, g, b)), so hue and saturation are free and you can use as many colours as you like:
   - black (value 0) is firm and follows the slot exactly;
   - darker colours are stiffer;
   - any fully bright colour (value 1) is fully squishy.

   A mesh without vertex colours is all squishy. `SoftnessChannel` can switch to a single channel (Red, Green, Blue or Alpha) instead.
3. Attach **Physics > Softbody**. `Renderer` defaults to the MeshRenderer on the same slot, but it can point
   at a renderer on any slot.

| field | default | range | change |
|---|---|---|---|
| `Renderer` | MeshRenderer on this slot (a plain one first, else a SkinnedMeshRenderer) | MeshRenderer or SkinnedMeshRenderer + StaticMesh | rebuild |
| `SoftnessChannel` | Value | Value (HSV brightness), Red, Green, Blue, Alpha | rebuild |
| `InvertSoftness` | false | 1 - softness | rebuild |
| `StartAs` | MeshColors | what the paint starts from: **MeshColors** (the mesh's own colours, or white without any), **AllSoft** (plain white: paint black to make parts firm), **AllFirm** (plain black: paint bright colours to make parts squishy) | rebuild |
| `Cells` | 4 | 1..32 (0.8.0; was 1..16) cells along the longest side. Only cells around soft parts, plus a 1-cell ring, get points: firm regions cost nothing, so you can raise this for detail where it squishes | rebuild |
| `Pin` | 0.02 | 0..0.5; cage points at or below this softness are pinned | rebuild |
| `Radius` | 0.2 | 0.02..0.5 collision sphere per point, fraction of a cell | rebuild |
| `Stiffness` | 6 | 0.2..60 Hz edge springs in squishy regions | **live** |
| `Volume` | 0 | 0 = 2 x Stiffness, else 0.2..60 Hz | **live** |
| `Damping` | 0.15 | 0.01..4 damping ratio (lower = wobblier) | **live** |
| `Follow` | 2.5 | 0.05..60 Hz pull back to the rest place on the slot | **live** |
| `Firm` | 30 | 1..60 Hz springs for nearly-firm parts | **live** |
| `Mass` | 2 | 0.01..1000 kg, whole cage | **live** |
| `Gravity` | 1 | -10..10 multiplier | **live** |
| `Drag` | 0.2 | 0..50 linear damping, 1/s | **live** |
| `Friction` | 0.8 | 0..4 against the ground and proxies | **live** |
| `Iterations` | 8 | 1..32 solver iterations | **live** |
| `Collide` | true | ground ray + nearby Box/Sphere colliders | **live** |
| `Budget` | 4.5 | 0.5..50 ms: auto shut-off. If this softbody's average cost per frame on a client goes over this, it pauses there (Status says why). Raise it, or change any setting, to retry | **live** |
| `DebugCells` | false | draws the cage (see *Debug views*) | **live** |
| `DebugColliders` | false | draws the collision spheres and what they collide with (see *Debug views*) | **live** |
| `Enabled` | true | off = original mesh back | stop/start |
| `Red` ... `Violet` | ×1 each | per hue band: `Stiffness`, `Damping`, `Follow` multipliers, 0.05..20 | **live** |
| `Paint` (hidden) | empty | up to 4000 paint dabs (see *Paint brush*) | colours at once, cage 0.3 s later |
| `ClearPaint` (button) | | removes every dab; the mesh's own colours come back | colours at once, cage 0.3 s later |

**Hue bands (0.3.0).** There are seven groups on the component: Red, Orange, Yellow, Green, Blue, Indigo and Violet. Each one multiplies `Stiffness`, `Damping` and `Follow` for the parts painted in that hue.
- **Hue picks the band.** The band centres are at 0°, 30°, 60°, 120°, 240°, 270° and 300°. Hues in between blend their two neighbours, so cyan is half Green and half Blue, and gradients don't seam.
- **Saturation sets how strongly.** A half-saturated pastel is half banded and half plain. Grey, white and black use the plain settings.
- **Brightness still sets the softness.** For example, Red ×4 stiffness with Blue ×0.3 gives a springy red arm and a floppy blue one, both painted at full brightness.
- **Live tuning:** changes apply on the next frame, with no rebuild.

- **live** means the change applies to the running cage on the next frame, with no rebuild:
  - springs are re-applied with `Solver.ApplyDescription`;
  - mass goes through `SetLocalInertia`, and iterations through `Solver.IterationCount`;
  - gravity, drag, friction and collide are read by the callbacks every step.
- **rebuild** stops the cage, restores the mesh, and builds a new cage.
- Values outside the range are clamped. `Effective` shows what is actually used.

**Status fields** are `RawOutput`s: `Status`, `Points`, `Vertices`, `MovingVertices`, `SimMs`, `FrameMs`,
`Resets`, `Tunes`, `Dabs`, `Effective` and `LastError`.
- They are local to each client. They are never synced or saved, and are written at most twice a second.
- Each client shows its own simulation in them.

## Debug views (0.10.0)

Two toggles on the component. Each client draws the views from its own simulation, as one overlay mesh on a **local
slot** under the world root (`Softbody debug view (local): <slot name>`). That slot is never synced or saved and is
removed when both toggles are off, the softbody stops, or the component goes. The toggles themselves are synced, so
everyone in the session with the plugin sees the views while they are on.

**`DebugCells`** shows the cage:
- the cells' edges, coloured by softness: blue (firm but simulated) to orange (fully squishy); pinned parts grey and faint;
- a small marker on every cage point (grey = pinned, coloured = simulated);
- in **red**, points the untangler is pulling back (a tet was pushed inside out), with their edges;
- a thin **white** line from where a simulated point wants to be to where it is, when it is pushed or sagging away.

On an avatar the cage follows the skeleton: each point follows the bones of the soft vertices around it. With few
`Cells` and the whole avatar soft, one cell can hold torso, tail and wing at once, so the lattice bends wherever those
are posed differently from the bind pose (a hanging tail, folded wings). That is the real cage, not a drawing error:
paint only what should squish (the rest stays out of the cage) and raise `Cells`.

**`DebugColliders`** shows the collision:
- each simulated point's collision sphere (`Radius`): green, **yellow** while it touches the ground or a collider;
- the Box and Sphere colliders it reacts to, as **magenta** wireframes;
- the ground under it as a **cyan** patch, with the downward ray that found it. Only floor-like hits count (normal
  within about 53° of up). If the body's rest shape reaches into the floor, the simulation lowers the floor under it so
  it never pushes the body out of shape; the patch is drawn where the simulation has it, joined to the real hit;
- the box searched for colliders (grey). A collider inside it that is not drawn magenta is ignored (mesh, capsule,
  cylinder, cone or convex hull colliders are not supported; see *Known limits*), or it belongs to the softbody itself.

Notes:
- The material is an overlay: the parts inside or behind the mesh are drawn dimmer instead of hidden.
- The mesh is rebuilt on the engine's background mesh thread, at most 30 times a second and slower for very big cages.
  The world-thread cost is one copy of the point positions per update.
- It is capped at 150,000 vertices. Past that, collision spheres are left out first; the log says so once.
- With `Collide` off the colliders view is empty, because nothing collides.

## Paint brush (0.4.0)

The softness map no longer has to come from the mesh file. Each Softbody has a synced list of **paint dabs**
(`Paint`), and the **SoftbodyBrush** tool adds and removes them in the world.

- **Non-destructive.** The mesh asset is never changed. Dabs live on the component: they sync to everyone at once,
  save with the object, and erasing them brings back the mesh's own colours exactly.
- **Effective colour** of a vertex: the mesh's own colour (white when it has none, so an uncoloured mesh starts all
  soft), then every dab in list order, `c = lerp(c, dab.Color, Strength × falloff(distance / Radius))`, measured
  on the **rest** mesh in the renderer slot's local space (the paint moves with the object). The falloff is 1 out
  to `Hardness × Radius`, then a smoothstep down to 0 at the rim.
- Everything that read the vertex colours now reads the effective colours: softness (channel and invert as set)
  and the hue bands.
- **Live display.** The effective colours are written into this client's private mesh copy and uploaded with the
  next frame (`Positions|Normals|Colors`, still one upload in flight per asset). A vertex-colour material such as
  `PBS_VertexColorMetallic` shows the paint while you paint.
- **Cage rebuild.** The softness changes which cage points are pinned, so the cage is rebuilt, **in place** (same
  mesh copy, no restore and no flicker), **0.3 s after the last paint change**. Until then the jelly keeps running
  on the old cage; after the rebuild it starts again from rest. The log says `cage rebuilt from the paint in N ms`.
- **Cost** (offline, 10k vertices, 4000 dabs): painting is about 0.05 ms per frame (p99 0.09 ms); erasing under a
  5 cm brush is about 0.3 ms per frame (only the vertices under the removed dabs are recomputed; 5 dabs scattered
  over the whole mesh, about 1 ms); a full recompute is about 5 ms (only after big list changes, e.g. ClearPaint);
  the cage rebuild after a stroke is about 6 ms, once.
- **Blur (0.5.0).** A blur dab ignores its colour. It moves each vertex inside the brush (by strength × falloff, as usual) towards the tent-weighted average colour of its surface neighbourhood: max(0.35 × radius, 1.5 × the mean vertex spacing).
  - Neighbours facing the other way are left out, so the two sides of a thin part don't mix.
  - It works on anything under it: model colours, paint, or earlier blur.
  - Offline, three passes of an 8 cm blur along a hard black|white edge turn a 1.0 step into a smooth, monotonic ramp: the largest step between neighbours is 0.17, with no ringing. A linear gradient under a blur is unchanged (the largest change is 0.0000), and vertices outside the brush are untouched.
  - Painting a blur dab costs about 0.04 ms (p99 0.08 ms) at 10k vertices.
  - **Erasing or undoing while any blur dab exists recomputes everything.** A blur reads its neighbours, so the regional shortcut isn't exact. That's about 48 ms for 2000 blur dabs, and such expensive updates are rate-limited to about 20% of the time.
- **Cap:** 4000 dabs per Softbody. The brush spaces dabs 0.3 × radius apart along a stroke.
- **Colour space.** Dab colours are converted to the mesh's vertex-colour space (`MeshX.Profile`; usually Linear)
  before blending, so the paint looks like the picked colour. Black and white are exact, but a mid grey picked as
  sRGB 0.5 is softness about 0.21; for softness 0.5 pick sRGB about 0.73.
- `Paint` is hidden in the inspector (thousands of entries would build a huge UI); `Dabs` shows the count, and the
  `ClearPaint` button (also `callSyncMethod ClearPaint` over ResoniteLink) empties it.

**The brush** is a FrooxEngine `Tool`, built like the engine's ColorTool and WigglerApplier. Attaching it (or
spawning it with `softbody_test.py brush`) builds a handle with a grab collider, a tip bubble that shows the
radius and colour, and a hit marker; `Tool.OnAttach` adds the `ObjectRoot` and `Grabbable`.

| field | default | meaning |
|---|---|---|
| `Color` | black | paint colour: brightness = softness (Value channel: black = firm), hue = hue band |
| `Radius` | 0.05 | metres at the brush's own scale 1 (scale the brush to scale it), 0.002..2 |
| `Strength` | 0.7 | 0..1 per dab |
| `Hardness` | 0.5 | 0..1, fraction of the radius at full strength |
| `Mode` | Paint | Paint; **Blur** (softens transitions: see below); Erase (removes dabs centred inside the brush); Pick (takes the colour under the brush, then back to Paint) |
| `Spacing` | 0.3 | dab distance along a stroke, fraction of the radius |
| `Status` | | local: what the brush is over or did last |

**Controls.**
- **VR:** grab the brush with the grip; a physical grab equips it (engine `GripEquip`). The trigger paints. The
  secondary button cycles Paint → Blur → Erase → Pick. **Resize (0.6.0):** push the thumbstick up to grow the brush and down to shrink it. Only clearly vertical pushes count, and full deflection is about 3.3× per second. On desktop, `]` grows and `[` shrinks (also keypad `+` and `-`): a tap is one 20% step, and holding keeps going. The keys are ignored while a text field has the keyboard. The context menu also has **Bigger brush** and **Smaller brush** (×1.5). The radius goes from 0.5 cm to 2 m. It paints where the laser points, or, when the tip is within
  `Radius` of the surface, right where the tip touches.
- **Desktop:** grab it (right mouse), then open the context menu (middle mouse) and pick **Equip**; it then paints
  where the cursor points (left click), and `R` cycles the mode. Holding it without equipping also works
  (`CanUseWhenHolding`): left click paints along the holding laser.
- **Context menu** while holding or equipped: **Mode** (one item that cycles Paint, Blur, Erase, Pick), **Bigger brush** /
  **Smaller brush**, **Change color** (0.11.3: opens Resonite's colour picker bound to the brush colour, live while you
  drag; brightness is the softness with the default Value channel, the hue picks the hue band), **Hue** (0.13.0: one item
  that cycles none, Red, Orange, Yellow, Green, Blue, Indigo, Violet, shown in its colour; it paints the exact hue a
  SkinnedSoftbody hue layer or a Softbody hue band picks, keeping the brightness, so the softness; none is grey/white, the
  base layer), **Soft**, **Half** and **Firm (black)** (0.13.0: in the current hue; Half is brightness 0.5 in the colour
  space of the mesh you paint, 0.5 linear on most imports). Picking a colour any other way (Change color, Pick) moves Hue
  to the nearest one.
  **Undo last stroke** (removes the dabs of your last stroke) and **Clear all paint** (on the Softbody you are pointing at
  or painted last).
- **Spawning one (0.11.3):** press **SpawnBrush** in a Softbody's or SkinnedSoftbody's inspector, and a brush appears half a
  metre in front of you. Or save a brush to your inventory once (hold it, Inventory, Save Held).
- The laser ends on the soft mesh's surface (`OverrideTargetPoint`), and the marker bubble shows where and how big
  the dab will be. The aim is the **rest** mesh, so on a wobbling jelly it is off by the current wobble.
- Only Softbodies **running on your client** can be painted (enabled, in the focused world, not paused).
- Undo: the brush's own *Undo last stroke*, not Resonite's undo system (that would add one undo component per dab).

## Skinned meshes (0.8.0, experimental; GPU path 0.9.0)

Point `Renderer` at an avatar's `SkinnedMeshRenderer` (a SkinnedMeshRenderer is a MeshRenderer, so the field type is
unchanged). The rest space stays the mesh's bind space: the lattice, softness, paint and dabs are where they always were.

**How it works.**
- **Skin convention (IL, checked offline against the engine's own CPU skinning):** a bind pose maps mesh (bind) space to
  the bone's local space, and `world(v) = sum w_i * BoneSlot_i.LocalToGlobal * BindPose_i * v` (`Elements.Assets.BoneBinding.TransformPosition`;
  the Unity renderer uses a real `UnityEngine.SkinnedMeshRenderer` whose bones mirror the bone slots, so the renderer slot's own
  transform plays no part). Blendshapes are added to the base positions before skinning. Missing bones follow the renderer slot,
  unweighted vertices too.
- **The cage is skinned.** Every cage point gets the bone weights of the vertices it carries (accumulated like softness, the 4
  strongest kept, normalised; empty points inherit from neighbours). Each frame its target is its linear-blend-skinned rest
  position, interpolated across the substeps; firm points follow it exactly. The edge and volume springs take the skinned shape
  as their rest state (only where it changed), so bending an elbow is not fought by the cage.
- **Back to bind space.** Each point's offset from its target goes through the inverse of its blended skin matrix
  (singular matrices give no offset), and the spline field moves the soft vertices in bind space, times their softness.
- **GPU path (0.9.0, needs SoftbodyRenderer in the renderer).** The private copy keeps all of its skin data (bones, bind
  poses, bone weights, blendshapes). Each frame the soft vertices' bind-space positions (rest + bind-space offset x softness)
  and normals are written into it and sent as a **true partial upload** (`Positions|Normals`, plus `Colors` after paint):
  - Engine side (`Plugin/PartialEncode.cs`, `Plugin/GpuUpload.cs`): `Mesh.SetFromMeshX` cannot do it, because
    `MeshBufferGenerator.EnsureValidVertexAttributes` reports a layout change for every mesh with bones and the caller then
    sets `VertexLayout` and every stream. So the plugin does what `Mesh.UpdateMeshData` does minus that step: the engine's own
    `FillVertexData` rewrites only the flagged streams in the asset's existing shared-memory `MeshBuffer` (layout, indices,
    bone and blendshape regions untouched), `FillMeshBufferDefinition` describes the same buffer, and the upload goes out
    through `RenderSystem.SendAssetUpdate` with the asset's `onAssetUpdated` callback. The asset keeps its original MeshX and
    `Version`, so nothing in the engine (SkinnedMeshRenderer's bone/blendshape setup, bone metadata) is redone per frame.
    Three private engine members are used by reflection; if one is missing the plugin uses the CPU path.
  - Renderer side (SoftbodyRenderer): such an upload on a skinned mesh is applied to the existing Unity mesh in place (see
    *Renderer patch*); Unity adds the blendshapes and skins it on the GPU.
  - No CPU skinning, no full-mesh work: cost is the cage, the soft vertices and the engine's encode, and **no upload at all
    while the jelly is still** (walking at constant speed included: the renderer follows the bones by itself).
  - **Choosing the path.** A skinned Softbody starts on the GPU path only when the renderer holds SoftbodyRenderer's
    handshake event open (`Local\Softbody.RendererPatch.SkinnedInPlace.v1`, Windows), so the stock renderer never receives a
    vertex-only upload on a skinned mesh (it would destroy the mesh). `SOFTBODY_GPU_SKIN=off` forces the CPU path,
    `=probe` tries the GPU path without the handshake.
  - **Fallback.** Every GPU-path upload is checked by its result: `instanceChanged == false` confirms it (logged once:
    `skinned: GPU path (renderer patch active)`); `true` means the renderer did not apply it in place (no patch, or the patch
    refused this mesh, which leaves the old mesh untouched): that Softbody switches to the CPU path (its copy is stripped as
    0.8.0 does, one full unskinned upload, renderers re-bound with `QueueAssetCreated`) and says so in the log. After two such
    fallbacks with no confirmation, new skinned Softbodies start on the CPU path for the rest of the session.
  - **First frames.** The first GPU-path upload is itself a partial one (in place: the avatar never disappears). If the copy
    gained a stream the mesh lacked (normals, or vertex colours from `StartAs`/paint), one full upload *with* skin data goes
    first (the renderer makes a new instance, renderers re-bind: one possible flicker, as in 0.8.0), then partials.
  - **Restore.** When the asset still holds the original MeshX, the original's streams are put back the same way (in place,
    no flicker); otherwise with a full upload, as before.
- **CPU path (0.8.0; without the renderer patch).** `Renderite.Unity.MeshAsset.Upload` destroys and recreates the Unity mesh on
  every upload that follows a skinned one (`lastIsSkinned`), so on the stock renderer a partial upload would leave a new mesh
  with no layout, indices, bones or blendshapes, and full uploads every frame would flicker (renderers hold the destroyed mesh
  until the engine re-binds them) and cost far too much.
  So while a skinned Softbody runs on this path, this client computes the shown mesh itself: bind-space base + the renderer's current
  blendshape weights (frame 0, the engine's own CPU rule) skinned into the renderer slot's space, positions, normals and
  tangents, and uploads it as an **unskinned** mesh (the private copy has no bones and no blendshape frames, but keeps the
  blendshapes' names so the synced `BlendShapeWeights` list is never resized). From the second upload on, uploads are
  partial `Positions|Normals(|Tangents)(|Colors)` again. The first upload and the restore make the renderer create a new
  mesh instance; the plugin then re-notifies the asset's references (`WorldAssetManager.QueueAssetCreated`) so renderers re-bind.
- **Frame order (IL, `World.RefreshStep`):** input and user poses, then every `OnCommonUpdate` by update order (VRIK at -5000,
  most components at 0, Softbody 0.8.0 at 1000), then dynamic bones, then ProtoFlux, then change events, then the render
  update. The cage sees this frame's VRIK and Animator pose; bones moved by dynamic bones or ProtoFlux are one frame behind.
- **Teleport/respawn:** a mean target jump of more than max(5 m, 20 x the cage size) in one frame resets the cage.
- **Bounds:** culling of a skinned renderer uses the SkinnedMeshRenderer's own bone-based bounds (`BoundsComputeMethod`),
  which cover the skeleton, so the shown mesh is culled where the body is. If a big wobble pops at screen edges, set
  `BoundsComputeMethod` to `SlowRealtimeAccurate` (a change to the avatar, saved with it).
- **Brush:** rays and the touch tip hit the shown (CPU-skinned) mesh, refreshed at most 10 times a second while the brush
  aims near it (BVH refitted, not rebuilt); the hit is mapped to bind space through the hit triangle's barycentric
  coordinates, and the dab radius is divided by the skinning's scale there, so it matches the brush's world size. On the GPU
  path the plugin skins the copy's positions itself for this (no blendshapes), only at those refreshes.

**Costs** (offline, ~60k-vertex body, 7 bones, soft forearm, walking):
- GPU path: cage step ~0.25 ms, bind-space deform of the soft vertices ~0.35 ms, `Positions|Normals` encode into the skinned
  buffer ~0.07 ms: **~0.65 ms per frame** while it wobbles, nothing once it has settled. The renderer copies the whole vertex
  buffer (stream 0) per upload, as for static meshes. The private copy keeps the mesh's blendshape frames (memory: one more
  copy of them).
- CPU path (2 blendshapes active): cage step 0.2-0.6 ms, bind-space deform 0.4 ms, blendshapes + CPU skin of every vertex
  0.3-0.5 ms (thread pool; 0.6-1.5 ms on one thread), engine encode 0.25-0.4 ms: **1.2-1.6 ms per frame** (p95 2.3 ms). The
  renderer re-uploads the whole vertex buffer (2.3 MB here) on every changed frame, and while an avatar moves at all, every
  frame changes. The private copy costs one mesh copy (12 ms once).

**Limits and unknowns (need an in-game run).**
- GPU path: whether Unity 2019.4's SkinnedMeshRenderer re-skins an in-place vertex update as expected (the patch calls
  `Mesh.MarkModified` after it; `NotifyRenderers` in the renderer plugin's config turns that off). The wobble is one engine
  frame + one upload behind the bones, the bones themselves are not.
- CPU path: the GPU no longer skins the mesh while it runs: the body shows the pose of the frame it was computed in, about 1-3
  frames behind GPU-skinned things (other meshes, held items) while moving.
- CPU path: Unity is handed an unskinned mesh on a SkinnedMeshRenderer that still gets one bone (its own slot) and blendshape weights:
  whether Unity draws it at the slot without complaint, and whether it logs out-of-range `SetBlendShapeWeight` errors, is not
  known offline.
- Every client runs this for every skinned Softbody it sees (the cost above, per avatar).
- Two avatars sharing one mesh asset (two users wearing the same avatar): the second Softbody waits ("already driven"). On
  the CPU path the first one's CPU-skinned mesh is what *both* renderers show; on the GPU path both bodies show their own
  pose with the first one's wobble. Give each its own mesh URL (re-import) to avoid it.
- Default settings are very wobbly for limbs (up to 15 cm lag in the walk test): start with `Follow` 8-12, `Stiffness` 10-20,
  `Damping` 0.4, `Gravity` 0-0.3.

**In-game checklist (avatar).**
1. Back up the avatar. In the inspector of the body's slot (the one with the `SkinnedMeshRenderer`), attach **Physics >
   Softbody**; `Renderer` fills in by itself. Set `StartAs` to **AllFirm** first (everything firm), `Follow` 10, `Stiffness` 15,
   `Damping` 0.4, `Gravity` 0.2, `Cells` 16-24.
2. Log: `[Softbody] '<slot>' started on renderer ...: ... skinned (GPU-skinned by the renderer (SoftbodyRenderer patch): ...): N bones (M move the cage) ...`
   (or `skinned (CPU-skinned on this client, EXPERIMENTAL; <why>)` without the patch) and `Status: running (skinned, N bones,
   GPU-skinned)`. The avatar should look unchanged (GPU path: no flicker at all, unless the copy gained colours or normals).
   GPU path: `[Softbody] skinned: GPU path (renderer patch active)` right after (see *Renderer patch* for the full list).
3. Equip the SoftbodyBrush, paint the arms white (or any bright colour). 0.3 s after each stroke: `cage rebuilt from the paint`.
   The marker sits on the arm where you point, even with the arm raised.
4. Walk, wave, bend the elbows: the painted parts should lag and wobble and settle; the head and torso stay firm. Talk / blink:
   visemes and blendshapes must still move the face. Watch `FrameMs` (should stay well under `Budget`).
5. The 5 s stats line: `skin ... ms (N bones; ... constraint rest updates per frame, 0 singular points ...)`, then on the GPU path
   `GPU path: N true partial uploads, N applied in place, 0 recreated by the renderer` and `path: GPU-skinned, true partial:
   Positions|Normals, skin data kept, no layout/index upload` (CPU path: `path: CPU-skinned, partial: Positions|Normals...`),
   acks about equal to uploads, resets 0. GPU path: standing still, `idle` should climb and uploads drop to 0.
6. Disable the Softbody: the original skinned mesh comes back (possible one-frame flicker). Search the logs (Resonite and
   the renderer's Player.log) for `SetBlendShapeWeight`, `bindpose`, `RenderSystem`, `NullReference`, `[Softbody]`.

## Renderer patch: SoftbodyRenderer (0.9.0)

`Renderer/` builds `SoftbodyRenderer.dll`, a BepInEx 5 plugin (net472) for Resonite's renderer process
(`Renderite.Renderer`, Unity 2019.4, Mono). It is loaded by BepInExRenderer, which RenderiteHook starts: RenderiteHook
(a BepisLoader plugin in the engine) copies `BepInEx\plugins\RenderiteHook\Doorstop\` (`winhttp.dll`, `doorstop_config.ini`)
into `Renderer\` when the engine starts the renderer and passes the engine's command line on; Unity Doorstop then runs
`Renderer\BepInEx\core\BepInEx.Preloader.dll`, whose chainloader loads the DLLs under `Renderer\BepInEx\plugins\`.

**What it patches** (Harmony prefix/postfix/finalizer on `Renderite.Unity.MeshAsset/<Upload>d__21.MoveNext`; no IL rewritten):
- The coroutine's first step destroys and recreates the Unity mesh when `!mesh.isReadable || lastIsSkinned || hint[BindPoses]`,
  then clears `lastIsSkinned`. Everything after only does what the hint flags (`VertexLayout`, vertex streams to stream 0,
  `SubmeshLayout`/`Geometry`, `BoneWeights`, `Blendshapes`), plus the submesh/index-format sanitizing and `mesh.bounds`.
- For an upload with **only vertex-stream flags** (`Positions`, `Normals`, `Tangents`, `Colors`, `UV0`-`UV7`, plus
  `Dynamic`/`Readable`) on a mesh that is **still skinned** (`lastIsSkinned`), the prefix checks that the Unity mesh's stream 0
  has exactly the upload's layout (same attributes, formats, dimensions, order) and vertex count, then clears `lastIsSkinned`
  for this one step: the stock code skips the recreate and re-uploads stream 0 (`SetVertexBufferData`) into the existing mesh.
  Bind poses, bone weights (`SetBoneWeights`, kept outside the declared stream 0), blendshapes, submeshes and indices are not
  touched; `lastIsSkinned` is set back by the finalizer; the result says `instanceChanged = false`. The postfix then calls
  `Mesh.MarkModified` (config `NotifyRenderers`), because the stock upload uses `DontNotifyMeshUsers`.
- If the layout does not match, the upload is **refused**: dropped before it can destroy the mesh, finished like the coroutine's
  own tail, result `instanceChanged = true`, so the engine side falls back to its CPU path while the old mesh stays visible.
- Every other upload, skinned or not, runs the stock code unchanged. FrooxEngine itself never sends a vertex-only upload on a
  mesh with bones (it widens them), so only Softbody's GPU path reaches the new code (and skinned renderers whose mesh has
  only blendshapes, which the stock renderer would also break).
- **Verified IL only.** Before patching, it hashes the coroutine's IL skeleton (every member reference and int constant, in
  order) and refuses to patch unless it equals the build checked offline (`UploadPatchSpec.VerifiedSkeletonHash`,
  `6945e5eb0eccf8b3`, Renderite.Unity `1.0.0+64bd84d1`). After a renderer update that changes the coroutine it logs that and
  stays off; Softbody then uses its CPU path. Rebuild against the new renderer and re-run the tests to re-verify.
- While patched it holds the named event `Local\Softbody.RendererPatch.SkinnedInPlace.v1` open: the engine plugin's handshake.

**Install** (Resonite closed; BepisLoader, BepInExResoniteShim, RenderiteHook and BepInExRenderer already installed):
1. Copy `Renderer\bin\Release\net472\SoftbodyRenderer.dll` to
   `Resonite\Renderer\BepInEx\plugins\SoftbodyRenderer\SoftbodyRenderer.dll` (create the folders).
2. Install the 0.9.0 `Softbody.dll` (*Install* above).
3. Launch with `--hookfxr-enable` added to the Steam launch options (keep the `-LoadAssembly` ones), or set `enable=true`
   in `hookfxr.ini`.

**Logs to check:**
- `Resonite\BepInEx\LogOutput.log` (engine BepInEx): RenderiteHook's `Copying Doorstop files ...`, `Doorstop files copied
  successfully` and `Starting renderer with args: ...`.
- `Resonite\Renderer\BepInEx\LogOutput.log` (renderer BepInEx): `Loading [SoftbodyRenderer 0.9.0]`, then
  `SoftbodyRenderer 0.9.0 patched MeshAsset.Upload`, `verified <Upload>d__21.MoveNext: 613 instructions, skeleton 294 entries,
  hash 6945e5eb0eccf8b3` and `handshake event ... open`. With an avatar Softbody running:
  `first in-place skinned vertex upload: mesh asset N, V vertices, B bind poses, S blendshapes kept`. Any `refused a
  vertex-only upload` warning names the reason.
- The Resonite log: `[Softbody] Softbody 0.9.0 plugin ready ... Skinned meshes: GPU path (SoftbodyRenderer patch found in the
  renderer)`, `[Softbody] skinned: GPU path (renderer patch active)`. Fallbacks: `skinned: CPU path (<why>)` or `... switching
  this Softbody to the CPU path`.
- The renderer's `Player.log` (`%USERPROFILE%\AppData\LocalLow\Yellow Dog Man Studios\Renderite.Renderer\Player.log`; BepInEx's
  log also mirrors Unity's): no `SetVertexBufferData`, `bindpose`, `NullReference` or `Exception integrating asset` lines.

## How it works

- **Per client.** Every client runs the component's `OnCommonUpdate` in its own copy of the world, so each
  one simulates from the synced settings and the renderer slot's pose. Nothing about the simulation is
  synced, and no field is written or driven.
- **Focused world only.** Unfocused worlds stop and restore the mesh.
- **Simulation.** Each softbody gets a private Bepu `Simulation` from the game's own BepuPhysics.dll:
  - one sphere per cage point;
  - `CenterDistanceConstraint` on tet edges and a `VolumeConstraint` per tet;
  - a `OneBodyLinearServo` per point toward its rest place;
  - firm points are kinematic.
- **World collision.**
  - A ground ray runs at 10 Hz.
  - Nearby Box and Sphere colliders are found at 10 Hz, within a search box 0.6 m larger than the mesh.
  - Each found collider becomes a kinematic proxy whose pose is updated **every frame** and swept across the substeps with matching velocity. A moving collider (a held sphere, say) pushes the jelly the same frame, which is about 17 ms in the offline test (0.2.0 refreshed proxies only every 0.5 s).
  - Colliders on the renderer's or the component's slot are ignored.
- **Smooth embedding (0.2.1).**
  - Render vertices follow the cage through a quadratic B-spline (27 nearest points, with linearly extrapolated ghost layers), not the tetrahedra.
  - The surface has no creases at cell or tet boundaries. In the offline bump test, the worst crease is 2°, against 21° with the old linear embedding.
  - Affine motion and the rest pose stay exact.
- **Untangling (0.3.0).** A hard push can turn cage tetrahedra inside out, especially with fine cells, weak `Volume` and strong `Stiffness`. Edge springs are just as happy with a mirrored tet, so the jelly used to stay folded with permanent creased dents.
  - The cage now checks every tet each frame.
  - If one stays inside out for 0.15 s, its points get a strong, critically damped pull back to their rest place: 1.5 × max(Stiffness, Follow), 8..60 Hz. The pull lasts until the tet is the right way round again, plus 0.3 s.
  - The offline drag test with Lenki's settings (cells 12, stiffness 35, volume 1, firm 1) used to end about 70 mm folded. It now springs fully back.
- **Mesh output.**
  - The StaticMesh asset's MeshX is swapped for a private copy on this client.
  - Only Positions|Normals are re-uploaded (`Mesh.SetFromMeshX`), plus Colors when the paint changed, with one
    upload in flight per asset. The first paint on a mesh without vertex colours adds a colour stream (one full
    upload).
  - The original is uploaded back when the component is disabled or removed, when its slot is inactive or
    removed, or when the world loses focus or closes. Restores waiting for a renderer ack are pumped from a
    background loop.
- **Saving** writes only the component's settings. The deformed mesh is asset data and is never saved.
  RawOutputs aren't saved.
- **Budget.** It is 3 ms per softbody per frame (env `SOFTBODY_BUDGET_MS`). Over budget, or after an error,
  the cage stops on this client and `Status` says why.
  - It never touches `Enabled`.
  - Change any setting or toggle Enabled to retry.
- **Cap.** At most 4000 cage points for all softbodies on one client.
- **Stats.** A log line every 5 s per softbody (`[Softbody] '<slot>': ...`), and
  `GET http://127.0.0.1:23801/status` (loopback only; env `SOFTBODY_PORT`).

## Join compatibility (checked in IL, FrooxEngine 2026.11.6.1136)

The plugin is `[assembly: DataModelAssembly(Core)]`. Core is the only type whose components can exist in a
normal world.

| type | worlds whose `TypeManager` allows it | in session manifest | host has it, joiner doesn't | joiner has it, host doesn't |
|---|---|---|---|---|
| Core | every world this client starts (`World.StartSession`/`LocalWorld` default to `CoreAssemblies`) + Userspace | yes | refused: "Session uses assembly Softbody which is not available on this end." | joins; Softbody is unavailable in that world |
| UserspaceCore | Userspace only (`Userspace.SetupUserspace`) | no | joins | joins |
| Optional | none: no engine path adds it to a world | no | joins | joins, but the component can't exist anywhere |
| Dependency | n/a (no hash) | no | - | - |

- Loading the plugin does not change `SystemCompatibilityHash`. You can still join vanilla sessions, and they
  are still listed as compatible.
- The cost is on the host side: every session a plugin user hosts, including their home, lists `Softbody`.
- Without the plugin, saved items and worlds containing a Softbody load the component as `MissingComponent`.
  The data is kept and re-saved, and the mesh stays static.

## Build / test

    dotnet build -c Release                       # -> bin/Release/net10.0/Softbody.dll (not installed)
    dotnet build -c Release Renderer              # -> Renderer/bin/Release/net472/SoftbodyRenderer.dll (not installed)
    dotnet run -c Release --project tests         # offline tests (also builds both plugins first)
    dotnet run -c Release --project tests -- flesh                    # only the SkinnedSoftbody (flesh) tests
    dotnet run -c Release --project tests -- flesh-export <file>      # + python -I tools/flesh_preview.py <file> <folder>: pictures

The flesh tests need a skinned test model in `tests/data/kitavali_body.sbm`, made from an FBX by
`python -I tools/fbx_to_sbm.py <model.fbx> <mesh name> <out.sbm>` (standard library only); they are skipped without it.
The model is someone else's avatar: keep it local. While the BepisLoader stack is parked outside the game, build with
`-p:RendererBepInEx=<Testresolink>\parked_addons\Renderer\BepInEx\core\`.

The tests:
- flesh (0.11.0) on a real avatar body: welding, reducing the painted region, the shell (no inverted tets, inner points closer
  to the bone), the embedding (partition of unity, smooth), rest (no drift), gravity, a fast bend (lags, then no fighting),
  blendshapes (carried; with inertia, lags and settles), a jump (jiggles, keeps volume, settles), bind-space round trip on a
  scaled and turned avatar, normals of the moved vertices, teleport reset, live tuning, a box collider, the debug view, timing;
- run Core against the game's Bepu build, including the live-tuning tests;
- skinned (0.8.0): the plugin's matrix conversion + Core skinning against the engine's own CPU skinning; a two-bone arm at rest
  (exact), moved rigidly (settles to 0, firm vertices never move), bent 90 deg at the elbow (wobbles, settles, no resets or
  inverted tets); the shown surface against skinned rest + the simulated offsets; blendshapes stacking with the wobble; the
  brush on the bent arm; the engine encoder on a skinned mesh and on the plugin's unskinned copy; CPU blendshapes + skinning
  against the engine's own; timing on a 60k-vertex body;
- check the paint layer: falloff, strength and order; incremental, regional (erase/insert/change) and full
  recompute are bit-identical; erasing restores the colours exactly; the vertex grid equals brute force; ray and
  closest-point queries (BVH against brute force on 3000 rays); timing at 10k vertices and 4000 dabs; painting the
  top black pins the top of a rebuilt cage;
- encode a `Positions|Normals|Colors` partial upload with the engine's own encoder and compare it to a full one;
- check the built DLL with Cecil;
- run the engine's own startup weaver on a **copy** of it;
- run the engine's `AssemblyTypeRegistry` on it, which prints the `Data Model Assembly:` line and the
  compatibility hash Resonite should log;
- check the brush: a `FrooxEngine.Tool` with the expected fields, woven members and overrides, each matching a
  `Tool` virtual;
- GPU path (0.9.0), with the engine's own encoder on a skinned mesh as the engine loaded it: the partial hint is exactly
  `Positions|Normals` (the engine's own encoder widens the same hint), only position and normal bytes change (everything
  else, including the bone and blendshape regions, stays byte-identical), the encoded positions are exactly rest + bind-space
  offset, the vertex region equals a full encode, the upload message describes the same buffer, the copy's skin data is
  untouched, skinning the uploaded positions gives exactly the CPU path's mesh; paint (`|Colors`), layout growth, the in-place
  restore; timing on the 60k-vertex body and no uploads once settled while carried;
- the renderer plugin: built against the installed renderer DLLs (every member it uses resolves), BepInEx entry point and log
  line, Harmony argument names; the patch target in the installed `Renderite.Unity.dll` (types, fields, flag values); the
  coroutine's IL skeleton hash read with Cecil and decoded with System.Reflection (what Harmony hands the plugin) both equal
  the verified one; and the IL facts the in-place path relies on (one `lastIsSkinned` read, in the destroy check; every
  structural step behind its flag; stream 0 only; bone weights apart). Unity itself cannot run offline.

`CopyToLibraries` stays false; installing is a separate step. `legacy/mod-0.1.0/` is the old RML mod's
source, for reference only. It is not built.

## In-game test checklist

1. **Log at startup.** Look for these lines:
   - `POSTX Processed Assembly: Libraries/Softbody.dll` (first start only)
   - `Loaded Extra Assembly: Libraries/Softbody.dll`
   - `Data Model Assembly: Softbody, Type Count: 7 (total: 72), Compatibility Hash: WoM0oawSj2JVus+1HkcIzQ==, Type: Core` (0.9.0, same hash as 0.7.0/0.8.0; the offline check prints the same line)
2. **Spawn.** Set `RESONITELINK_PORT`, then run `python softbody_test.py spawn`.
   - The log should show `[Softbody] Softbody 0.3.0 plugin ready ...`, then
     `[Softbody] 'Claude_jelly_…' started on renderer ...: 125 points (26 pinned…), 10086 verts … (mean 0.48)`.
   - The inspector should show `Status: running` and the counts.
3. **Live tuning.**
   - Run `python softbody_test.py set stiffness 18` and `... set damping 0.6`. The log should say
     `tuned live: ...` with no `stopped`/`started` lines, `Tunes` should go up, and the wobble should get
     tighter and die faster.
   - Then run `set cells 5`. That one should log `stopped: settings changed: rebuilding`, then `started`.
4. **Shake.** Run `python softbody_test.py shake`, or grab the cube.
   - The top should wobble while the bottom stays planted.
   - The stats line should show `partial: Positions|Normals`, with acks about equal to uploads.
5. **Status / stats.** Run `python softbody_test.py status` and `python softbody_test.py stats`.
6. **Cleanup.** Each of these should restore the original mesh: `set enabled false`, saving the world while
   it runs (check the saved copy is static), and `remove`.
7. **Search the log** for `RenderSystem`, `NullReference`, `[Softbody]` and `MissingComponent`.
8. **Paint brush** (0.4.0). Spawn a canvas: `python softbody_test.py spawn --canvas` (white, all soft, Value
   channel, vertex-colour material), then `python softbody_test.py brush`.
   - **Spawn:** the brush appears 0.5 m in front of you: a dark handle, a small cone and a black see-through bubble
     at the tip. The log line `plugin ready` names the brush type.
   - **Equip:** VR grip-grab (it should snap into the hand as a tool), or desktop grab + context menu > Equip.
     `Status` on the brush says what it points at.
   - **Paint:** point at the cube and hold primary. The marker bubble sits on the surface, the laser ends there,
     and black appears on the cube at once. The log has no `stopped`/`started` lines (no restore), and the stats
     line says `partial: Positions|Normals|Colors`.
   - **Rebuild:** about 0.3 s after you let go, the log says `cage rebuilt from the paint in N ms`, and the painted
     part stops wobbling when you shake the cube (`softbody_test.py shake`).
   - **Erase:** secondary (desktop `R`) to Erase (red rim), stroke over the black: the white comes back, then the
     cage rebuilds again.
   - **Pick / colours:** Pick on a painted spot sets `Color`; paint a red patch, set `Red` stiffness ×4, and the
     patch should behave stiffer (hue bands).
   - **Undo / clear:** context menu > Undo last stroke, and > Clear all paint (or `softbody_test.py paint-clear`).
   - **Without the brush:** `softbody_test.py paint-demo` sends dabs over ResoniteLink (top black, one red side).
   - **Others:** a second user in the session should see the same paint and the same pinned top.
   - **Save/load:** save the cube and spawn it again: the paint comes back with it.

## Known limits

- **One asset per URL.** A StaticMesh asset is shared engine-wide by every StaticMesh with the same URL.
  - Two Softbody components on the same mesh: the second one waits and says so in `Status`.
  - Any other renderer using that mesh deforms too. A duplicated slot shares the mesh; give the copy its own URL.
- **Skinned meshes are experimental** (GPU-skinned with the renderer patch, else CPU-skinned while running; see *Skinned meshes*). Procedural meshes are not supported.
- **Paint:** at most 4000 dabs per Softbody; aimed on the rest mesh; only running Softbodies can be painted; the
  cage restarts from rest after each stroke's rebuild.
- **Collision is approximate:** ground ray plus Box/Sphere proxies only.
- **Snapshots of the mesh.** Exporting or baking the mesh while it runs reads the deformed copy.
