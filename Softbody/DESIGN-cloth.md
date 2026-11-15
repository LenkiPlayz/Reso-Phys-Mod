# Cloth: thin sheets that tear (design; phase 1 built as 0.29.0, phase 2 as 0.30.0, the Cloth Blast as 0.31.0, effect cloth as 0.32.0, copies and a mend timer as 0.33.0, fast pieces as 0.33.1)

2026-11-12. Lenki: "Plan cloth first, with tearing". A friend asked for rigid bodies, which come later. Then: "World cloth
first". Phase 1 (world cloth) is built as 0.29.0; see "Built in 0.29.0" below for what changed from this plan. It has not
run in game yet. 2026-11-14, Lenki: "Id do skinned cloth first then id build more on the cloth explosion stuff": phase 2
(avatar cloth) is built as 0.30.0, see "Built in 0.30.0". Not yet run in game either. Then, the same day, "Yes, start
it" to the cloth explosion work: the Cloth Blast is built as 0.31.0, see "Built in 0.31.0" (installed, not yet run in
game). Then effect cloth, a sheet that sleeps until a blast wakes it, and a Target on the blast: 0.32.0, see "Built in
0.32.0". Then a mend timer, and the torn pieces a Mend leaves behind as a copy: 0.33.0, see "Built in 0.33.0". Then pieces that fly fast without slowing the
game down or going through floors: 0.33.1, see "Built in 0.33.1".

It rests on three surveys made the same day: the SkinnedSoftbody pipeline, the mesh upload path, and outside research.
The links are at the end.

## Built in 0.29.0 (world cloth)

Files: Core/Cloth (ClothMesh, ClothSolver, ClothSettings with ClothWindField, ClothDebugView), Plugin/Cloth.cs,
Plugin/ClothInstance.cs, Plugin/ClothWind.cs, tests/ClothTests.cs. Plugin only; the fork is unchanged.

Decisions made while building, and where it differs from the plan below:
- **Stretch: two passes a step, and squeezed edges push back at a tenth of the force** (`ClothSolver.StretchPasses`,
  `CompressionScale`). Found by the hanging-sheet test: with one pass and edges as hard in compression as in tension, a
  41 by 41 sheet buckled in its own plane soon after it was let go, folded its triangles over each other and never came to
  rest. More steps made it worse; constraint order did not fix it. Two passes with soft compression are stable from 21 by
  21 to 81 by 81, flat or not, at Bend 0 and 0.3. The bending pairs keep one pass.
- **Bending** stays the distance between the two points across each edge. It is weak for large shapes: a 40 cm strip held
  flat droops 37 cm at Bend 1 and 38 cm at Bend 0. Bend mostly changes how sharp folds are. A better model (the isometric
  bending of Bergou et al., or dihedral angles) is a follow-up if stiff materials are wanted.
- **MaxDistance** defaults to 0 (no limit) for world cloth; above 0 a free point stays within its brightness times it of
  where it was modelled. (0.15 m stays the plan for avatars.)
- **Anchors** are captured when the cloth is built, within `AnchorRadius` (5 cm); the nearest anchor wins. An anchor that
  is missing for a frame holds its points where they were.
- **Colliders**: `Collide` (solid world colliders) and `CollidePlayers` (bodies and hands) are separate toggles. Colliders on
  the cloth's own slot and its anchors (and under them) are skipped (`FluidColliders.Exclude`).
- **Caps**: 8,000 points per cloth and a separate 24,000-point cloth cap per client (not the flesh cap).
- **Who decides tears**: the host, unless it is headless; then the other user with the lowest allocation ID. That client
  finds tears and appends (point, other end) to `Tears`; everyone replays the list in order. An entry whose value has not
  arrived waits up to 2 s. A shorter list rebuilds the cloth (empty: "mended").
- **The paint** is not written into the mesh's colours (the mesh keeps its look); `DebugCloth` shows pinned, anchored and
  free points.
- **On screen**: a private dynamic copy of the mesh (PrivateMeshes, with the Dynamic upload hint), vertex streams each
  frame, a full upload when a tear changes the topology. New vertices for tears are DuplicateVertex copies, in a fixed
  order, so every client gets the same indices.
- **Open questions answered**: world cloth came first; Stretch maps to compliance 10^(-3 + 2.5 Stretch) (0 at 0), Bend to
  10^(2 - 7 Bend) (0 at 1), Damping to 10 Damping per second.
- **Not done yet**: the cost benchmark (`tests -- cloth-cost`), and the in-game test.

## Built in 0.30.0 (avatar cloth)

Files: Core/Cloth/ClothSkinRegion.cs (new), ClothSolver (avatar mode), ClothSettings (`Inertia`, `Backstop`), ClothMesh
(`MaxRenderVertices`), Core/BodyShape.cs (`Own`, `SkipSphere`); Plugin/ClothSkinCopy.cs and Plugin/ClothInstance.Skinned.cs
(new), Cloth.cs, ClothInstance.cs, PartialEncode.cs (topology in place), SoftbodyRuntime (the v2 check), PrivateMeshes
(`SharedOf`), BodyShapes.cs; the fork's MeshAsset.cs (v2); tests/AvatarClothTests.cs.

Decisions made while building, and where it differs from the plan:
- **What moves (the region)**: vertices are welded by exact position (UV seams), and a welded group is free when every
  copy of it is painted at least half bright. The cloth is every triangle with a free corner; their other corners are the
  held ring, which sits on the animation. A vertex painted free later, outside the moving set, rebuilds the cloth 0.3 s
  after the paint stops (that rebuild doesn't count toward the rebuild limit). With nothing free the cloth waits for
  paint and costs nothing.
- **One bone**: in the renderer's copy, the moving vertices are bound fully to one bone (the one most of their weight is
  on) and lose their blendshape deltas; the cloth writes them in that bone's bind space every frame. The held ring and the
  rest of the mesh keep the engine's own skinning and blendshapes, so the seam stays closed: offline, under the engine's
  CPU skinning, the shown moving vertices are within 0.1 µm of the cloth and the ring within 0.3 µm.
- **Targets**: the region's vertices with the renderer's blendshapes, skinned on the CPU by their own weights (as the
  flesh rig does). Held points sit on them; free points stay within brightness × `MaxDistance` of them (0.15 m by
  default on a SkinnedMeshRenderer).
- **Inertia**: each frame the cloth moves along with (1 − `Inertia`) of the bone's motion, as a turn about the bone and a
  move: its points, their velocities, and the frame's start (where the held points come from and where the free ones were
  animated). A turn of over 25° in one frame is carried whole; a jump of 0.75 m or more starts it again at the animation.
  - Found by the tests: moving only the free points left the held ring to come the whole way during the substeps, and it
    dragged the cloth back every frame (2 cm behind a walk at `Inertia` 0; a 45° snap turn left it 16 cm off).
  - On the test skirt, in a sudden 1.5 m/s walk, the free cloth trails at most 0 cm at `Inertia` 0, 2.9 cm at 0.75 and
    3.8 cm at 1 (a tube held at its waist can't swing back like a pendulum).
- **Backstop** (0.02 m): a free point goes no further than this behind its animated place, along the animated normal.
  Which side is out comes from the region's normals voted against the bone's head (`OutwardSign`). Offline: 2.00 cm deep
  at most under a 3 g sideways push, 14.4 cm without it.
- **Colliders**: the wearer's own body shapes push a point no further out than its target (offline: a body 1 cm wider than
  a skirt pushes it out 0.7 cm when it's someone else's, 0 when it's the wearer's). Spheres fitted against the cloth on
  the same renderer are skipped (they sit inside it). The wearer's own colliders are skipped.
- **Body shapes read the shared mesh** (`PrivateMeshes.SharedOf`), never the cloth's copy: no new fit when the copy swaps in,
  and the spheres stay in the region's bind space.
- **Tears**: like world cloth, but in place. The copy has spare vertices after the mesh's own (half the moving part, 64 to
  20,000), bound like the moving ones. A tear points the region's triangles at spares and gives them the UVs and colours of
  the vertex they copy; the upload carries Geometry, the UV channels and Colors with the streams, encoded by the engine's
  own `FillIndexData` into the index layout the renderer has. Every client uses the same spare count, so tears replay the
  same. Only with the v2 fork (`Local\Softbody.RendererPatch.SkinnedInPlace.v2`); without it an avatar's cloth doesn't
  tear and `Status` says so (the tears list is left alone).
- **The fork (v2)**: MeshAsset.cs's in-place path also takes colours (Float32, bytes, or halves widened to floats), Float32
  UV channels and the triangles, when every submesh keeps its index range and the format and count stay. All or nothing;
  the plugin's `CheckLayout` refuses the same cases before sending. Built offline, not installed.
- **Size (world cloth too)**: rest lengths and masses are measured through the linear part of the rest-to-world map (the
  slot, or the bone), and again when it changes scale. 0.29.x measured them in the mesh's own units, so a sheet on a slot
  scaled 2x pulled itself to half its size.
- **Not done yet**: layered garments (an inner shell bound to the outer one); the in-game test; the cost on a real avatar.

## Built in 0.31.0 (Cloth Blast)

2026-11-14, Lenki: "Yes, start it", to a Cloth Blast component: one outward push from its slot that can also free and rip
the cloth near it. It answers three things missing for explosions: the wind only blows one way, freed cloth starts still,
and a cloth could only gain a quarter more points from tears, so it ripped into strips, not confetti. Plugin only; the
fork is unchanged.

Files: Core/Cloth/ClothBlast.cs (new: `ClothKick`, `ClothShred`, `ClothBlastMath`, `ClothShredder`), ClothMesh (`Cut`,
`OriginalNeighbours`, `RestCentroids`, `MeanEdge`, room up to 5 times the points), ClothSolver (`SetReleases`, the
frame's `Kicks`, `Cut`, `Kind` and `Mass` as of now); Plugin/ClothBlast.cs (new), Cloth.cs (`Blast`, `Blasts`,
`WorldBox`), ClothInstance.cs and ClothInstance.Skinned.cs, SoftbodyRuntime (`CollectCloths`); tests/ClothBlastTests.cs.

What it does:
- **Fire** (a button, or an Action) adds one `Cloth.Blast` (id, centre in the renderer slot's space, radius, strength,
  randomness, release, shred) to every Cloth whose box is within `Radius` of the slot. A cloth keeps up to 128; Mend
  clears them with the tears.
- **The push**: once on each client, at the first step after the blast arrives; a client that joins later doesn't get
  it. (1 - (d/R)^2)^2 times `Strength` (-50..50 m/s), outward from the centre, on the points that move. `Randomness`
  scales it by smooth noise (cells of R/3, at least 5 cm) and turns it a little.
- **Release** (world cloth): points whose modelled place is inside a blast's sphere move freely from then on: the paint,
  the anchors and MaxDistance let go of them. Points a tear makes from them are free too. Every client, late joiners
  included, frees the same ones; a Mend holds them again.
- **Shred** (world cloth, Tearable): below.
- **Avatar cloth** gets only the push. Its points are never at their modelled place, and its spare vertices (half the
  moving part) would not go far.

Decisions made while building:
- **Cuts, not splits.** A shred cuts between two triangles that share an edge (`ClothMesh.Cut`, by their stable indices).
  A point opens only once the cuts around it divide its fan, so a line of cuts across the cloth parts it, and a slit of k
  edges opens at its k - 1 inner points. Cuts read the topology only, never positions, so they come out the same on every
  client whatever the folds.
- **The pieces.** Each triangle takes the label of the nearest jittered cell centre (Worley, seeded by the blast's id) to
  its rest centre; a pair is cut when one of the two is within the radius and their labels differ (every pair at Shred
  1). The cell size goes from the radius at Shred 0 down to the mean edge at 1, in steps of 1.25. Shred 0.5 with a 30 cm
  radius on a 1 m sheet (a 2.5 cm grid) gives 48 pieces of about 14 triangles; Shred 1 gives confetti.
- **The same on every client.** Only +, -, *, /, sqrt, floor and integer hashes, and a sort with no ties (distance from
  the centre, then the indices), so the cut list is the same everywhere. It is applied 256 cuts at a time. The client
  that decides tears makes one part of each shredding blast a frame and appends (-id, part) to `Tears`; everyone replays
  splits and parts in list order and gets the same mesh, index for index (tested with splits between the parts). A
  split never looks like a part: blast ids are 1 to 2^30, parts 0 or more.
- **When it shreds.** Only within 2 s of the blast arriving on the deciding client, so a blast from long ago doesn't
  start shredding when someone else becomes the decider. Once started it carries on, under a new decider too. A client
  that has a part but not yet the blast waits up to 2 s, like a tear whose value hasn't arrived.
- **Room.** World cloth has room for 5 times its points (at most 24,000 more); a point's fan list is made only when the
  point is. A shred that runs out of room stops there, and the cloth stays valid.
- **The push keeps its momentum.** The edges share it out over the sheet: offline, the middle of a sheet kicked at 7.4
  m/s moves at 3 m/s after one frame, and the sheet keeps the kick's momentum (less a frame of damping). A closed skirt
  held at the waist mostly crumples: its edges neither stretch nor shear, so it can't fly off as a whole.
- **Release goes by the modelled place**, in the renderer slot's space (the mesh's own). A cloth dragged far from where
  it was modelled is freed where it was modelled, not where it is. A sheet hanging flat in its own plane hardly drops
  when its top corner is freed (it can only shear); the push is what gets it moving.
- **Cost** (offline, with a game open): a 51 by 51 sheet to confetti takes 10 ms to make the cut list (once), at most
  8.7 ms a part (29 parts, one a frame), and 21 ms for the first step after (the edges are rebuilt).
- **Not done yet**: the in-game test; Release and Shred on avatar cloth.

## Built in 0.32.0 (effect cloth: WaitForBlast and Target)

2026-11-14, Lenki: "make it a target field, and then make a cloth sim subset thats used for effects, mainly tearing and
stuff. and only triggered to be enbaled by things like the cloth blast. like for example a tough sheet that doesnt have
any sim on, but as soon as a blast hits it. it activates and tears apart." Plugin only; the fork is unchanged.

Files: Plugin/Cloth.cs (`WaitForBlast`, the asleep branch of `Frame`, `HasBlast`, `Asleep`), ClothInstance.cs
(`TakeBlastsAsNew`), ClothBlast.cs (`Target`, `OnAttach`); tests/PluginCheck.cs.

- **The subset is a mode of Cloth, not a new component.** A separate effect component would repeat the paint, the
  anchors, the settings and the tear sync; a mode reuses all of them, and the Cloth Blast already finds every Cloth.
- **Asleep means nothing is built**: no instance, no private mesh copy, no solver. The renderer keeps its shared mesh, and
  the component only checks each frame whether a blast has come. It can't be painted while asleep (the brush needs a
  running cloth).
- **Awake is "has a blast".** The blasts list is synced and saved, so every client wakes when a blast arrives, a Mend
  (which empties the list) puts it back to sleep, and a saved world loads it torn. No new synced state was needed.
- **Who gets the push**: a client that saw the cloth asleep, in focus, takes the blasts that wake it as new: they push at
  the first step, and the client that decides tears starts their shred (its 2 s window counts from the wake). A client
  that loads it already blasted, or was in another world when the blast came, builds it torn without the push, as with
  0.31.0's blasts.
- **Target**: a reference that is set is the only cloth a blast may hit, even when the cloth it points to is gone (then
  the blast does nothing, rather than hitting everything). It fills itself in when the blast is attached, from the blast's
  slot or the nearest slot above it with a Cloth; never from children, since a blast on the root would grab any cloth.
- **Not done yet**: the in-game test; going back to sleep once the pieces settle (a woken cloth simulates until a Mend).

## Built in 0.33.0 (MendTimer, DuplicateOnMend and CopyLifetime)

2026-11-14, Lenki: "add two extra things for the tearing, a boolean for duplicate mesh (clicking reform on the mesh or
turning it off would leave the extra cloth on the ground but the original mesh would reappear too) a Mend Timer and a
Cloth Lifeteam (only really matters if duplicating the original mesh matters)". Plugin only; the fork is unchanged.

Files:
- Plugin/Cloth.cs: the three fields, `MendTimerFrame`, `LeaveCopy`, and the wait for the hand-over in `Frame`.
- Plugin/ClothDebris.cs (new): the copy's component.
- Plugin/ClothInstance.Copy.cs (new): the instance as a copy.
- Plugin/ClothInstance.cs: `StartStep`, and the upload and teardown for a copy.
- Core/Cloth/ClothPieces.cs (new): finding the loose pieces.
- tests/ClothCopyTests.cs (new) and tests/PluginCheck.cs.

- **MendTimer**: the client that decides the tears (for an avatar cloth, its wearer) calls Mend that many seconds after it
  first saw the cloth torn or blasted. The emptied lists sync to everyone.
  - The time counts in world time from the first damage. More tears don't restart it, so a cloth that keeps tearing
    still mends.
  - It runs in a world that is out of focus too.
- **What a copy is**: the running simulation, handed over instead of torn down. Each client keeps its own, since each one
  simulates its own folds anyway. Nothing is synced, so a copy costs no network traffic and a late joiner sees none.
- **Which pieces**: the loose ones. A piece is a set of triangles joined by shared points.
  - A piece with a pinned or anchored point belongs to the original, which comes back whole in its place, so the copy
    hides it. Its triangles become empty (all three corners on one vertex), so the index count and the work mesh stay
    the same.
  - A free blanket is one loose piece: it stays where it fell, and the original comes back as modelled.
  - If nothing is loose, nothing is left.
- **Showing it**: the copy's local slot gets its own StaticMesh with the same URL, and a disabled MeshRenderer with the
  same materials.
  - Once that StaticMesh has loaded the shared asset (a frame or two, since it is already loaded), the copy's StaticMesh
    takes the cloth's private mesh and the original's StaticMesh gets the shared one back. Both happen in the same frame
    (PrivateMeshes.Swap, as in instancing), so there is no gap and no doubled cloth.
  - Until then, the original renderer shows the torn cloth standing still, and the Cloth waits before it starts again. A
    cloth built during that time would read the torn copy as its mesh.
  - If the StaticMesh hasn't loaded within 5 s, the copy is dropped and the original gets its mesh back.
- **Running it**: a ClothDebris component on the copy's slot steps it, because a disabled Cloth gets no updates.
  - It reads nothing from its Cloth: no tears, blasts, paint or settings. MaxDistance is dropped, so a piece tied near
    its place falls.
  - It steps in its own slot's space, which is set to the renderer's at the hand-over. It honours CullDistance and stops
    in a world that is out of focus.
  - Once every loose point has moved slower than 5 cm/s for a second, or after 20 s of simulation, it stops and stays as
    it is: no more steps and no more uploads.
- **When a copy is left**:
  - On a Mend: the button, MendTimer, or going back to sleep with WaitForBlast.
  - When the component is turned off (Enabled). The engine calls OnDisabled only for that, not when a slot is deleted.
  - Not on a rebuild for changed settings, losing focus, an error or a Budget pause.
  - Only in the focused world, and only for world cloth.
- **How long it stays**: CopyLifetime, counted from when the copy was left (0 = until the world closes). At most 4 copies
  per cloth; a fifth removes the oldest. Removing the Cloth removes its copies.
- **Not done yet**: the in-game test; fading a copy out instead of having it vanish; blasting a copy.

## Built in 0.33.1 (fast pieces)

2026-11-14, Lenki, after the 0.33.0 install: "sometimes at low drag and high strength the cloth sim starts to act up and
drop the frame rate to about 10 fps". Plugin only; the fork is unchanged. No synced member changed, so the hash stays
0.33.0's (`z3JAnL1n9ZsPDsV9CfabPA==`) and the two can share a session.
- **The cause** (the game log, then offline): BuildPairs gave all points one reach, the fastest point's speed times the
  frame's time (dt, up to 0.1 s), and every free point paired with every shape within it. After a blast at Drag 0.02
  that is metres: next to six players, 3.9 million pairs a frame at 144 fps and 5.1 million at 10 fps (230 ms a frame),
  each gone through in every step. Slower frames meant a longer dt, a longer reach and more pairs: a spiral down to
  about 10 fps.
- **Each point its own reach, along its way**: the box from where it is to where its velocity x 1.5 takes it in the time
  the frame's steps simulate (steps x h, at most 24 steps = 33 ms; not dt), plus m0 all round (twice the thickness,
  gravity's drop and 1 cm). A shape pairs with the points whose box it overlaps (`Reaches`).
- **Near cells** (1 m, `NearCell`): the cells the points' boxes touch. A shape goes in only if its box is in one. A mesh
  collider is searched cell by cell (up to 512 cells, else in the cloth's whole box), so a map's floor under pieces 40 m
  apart gives only the triangles under them. Anything too big to look up counts as near.
- **Body spheres** (`PairSpheres`): the spheres go into the cells of their box plus m0, and each point looks in the cells
  along its way, at most 15 cm (`MaxBodyReach`), taking a pair only in the first cell the two share. A fast piece mostly
  passes through a player, finding the spheres further on in the next run or frame (players are not floors).
- **Runs** (`PairRunsFor`): when the fastest point would get further than `MaxReach` (1 m) in the frame's steps, the
  steps go in up to `MaxPairRuns` (4) runs, the pairs found again before each. A point that would still get further in a
  run slows to MaxReach a run (in BuildPairs): about 80 m/s at 30 fps and below (4 runs of 6 steps), 160 at 60 and 240
  at 90. A 0.5 m reach cost about the same, with twice the runs and a 40 m/s cap.
- **Thin triangles** (`CrossedTriangle`): a triangle was tested only where a step ended, within the thickness r (1 cm).
  A point moving more than 2r in a step (14 m/s at 720 steps a second) could step over a thin floor; and two-sided, a
  point that had just gone through was pushed out on the side it ended on. Now a triangle is tested against the step's
  whole way, from P to X: a point that crossed it (two-sided: either way; front only: from the front), where the way meets
  the plane or within r of it, goes back to the side it came from, r off the plane. This was already so in 0.33.0, at any
  frame rate. Without the fix, the new tests' pieces end 24 to 64 m under the floor.
- **Stats**: `PairCount` is now the most pairs in one run, `PairRuns` the runs. Both stats lines show "N pairs (R runs)".
- **Numbers** (offline, `cloth-fast`, tests/ClothFastTests.cs): a 1.6 m sheet of 33 x 33 points, shredded into 3,376
  points and blasted at 50 m/s with Drag 0.02.

  | | before | 0.33.1 |
  |---|---|---|
  | next to six players (1,808 spheres each), 10 fps | 5,073,678 pairs, 230 ms a frame | 4,685 pairs, 10.2 ms |
  | the same, 144 fps | 3,935,227 pairs, 11.5 ms | 78,581 pairs, 4.1 ms (the blast's own frame 19 ms) |
  | over a mesh floor (12,800 triangles), 10 fps | 838,866 pairs, 68 ms | 9,824 pairs, 14.5 ms |
  | the same, 144 fps | 77,036 pairs | 14,422 pairs, 5.3 ms |

  A small sheet thrown down at 100 m/s at a thin floor 3 m below stays on it: two-sided and front only, at 30 fps (4 runs,
  slowed to 78 m/s) and at 144 (2 runs, 99 m/s).
- **Not done yet**: the in-game test. The plugin's GatherColliders still asks the physics world for everything in the
  cloth's whole box (plus 0.25 m) once a frame; if the main thread shows a cost in game, that is next. A collider thinner
  than a step at speed (a 5 cm box at 50 m/s) can still be stepped through: only triangles are swept.

## What it's for

- **Avatar clothing:** skirts, dresses, capes, coat tails, scarves, hoods, loose sleeves, ribbons and hair cards. They
  swing and fold. Your legs and arms, and other players', push them, and they drape over a seat.
- **World cloth:** flags, banners, sails, curtains, tablecloths, blankets, hammocks and tents.
- **Tearing in real time:** everyone sees the same rip, it saves with the object, and it can be mended.
- **No rigging:** you paint it with the Softbody brush, like the softbodies.

Not in the first versions:
- The same folds on every client. Each client simulates its own; only the tears are synced.
- Cloth hitting itself, or two garments hitting each other.

## How it's used

1. Attach **Physics > Cloth** next to a MeshRenderer (world cloth) or a SkinnedMeshRenderer (avatar). It starts all
   pinned, so nothing moves.
2. Paint it with the brush:
   - White hangs free and black stays put.
   - On an avatar, grey can swing only part of `MaxDistance` from where the animation puts it.
   - Material targeting (0.28.0) paints just the skirt when it shares a mesh with the body, and Mirror paints both sides.
3. Add the extras you need:
   - **Physics > Cloth Wind** blows on every cloth it reaches. It has a strength, gusts and turbulence, along its slot's
     forward axis.
   - **Anchors** (world cloth) are slots that hold the points near them. Put a Grabbable on one to drag a corner, or put
     two on poles to hang a banner. Anchors move through Resonite's own sync, so every client's cloth follows them.
   - Turn on **Tearable** to let it rip. **Mend** puts it back together.

### Fields (draft)

| field | default | meaning |
|---|---|---|
| `Renderer` | renderer on this slot | MeshRenderer or SkinnedMeshRenderer |
| `StartAs` | AllPinned | AllPinned, AllFree or MeshColors: what the paint starts from |
| `Weight` | 200 | g/m². Silk is under 100, shirting 100–150, denim 250–350 |
| `Stretch` | 0 | 0: doesn't stretch; 1: rubbery (XPBD compliance) |
| `Bend` | 0.3 | 0: drapes like silk; 1: stiff like leather |
| `Damping` | 0.1 | |
| `Drag`, `Lift` | 1, 0.5 | air, per triangle |
| `Gravity` | 1 | multiplier |
| `Friction` | 0.8 | |
| `Thickness` | 0.01 m | how close it gets to colliders |
| `StepRate` | 720 Hz | fixed step length; 240–1440 |
| `MaxDistance` | 0.15 m | avatars: how far a white point can swing from its animated place |
| `Inertia` | 0.75 | avatars: how much of the avatar's own motion the cloth feels (1 = all of it) |
| `Anchors` | empty | world cloth: slots that hold the points near them, plus a radius |
| `Collide` | true | world colliders and players |
| `Tearable` | false | |
| `TearAt` | 1.5 | an edge tears once it is longer than this × its rest length |
| `TearSpread` | 0.3 | weakens the edges next to a tear, so a rip runs along a line instead of fraying |
| `Budget`, `CullDistance`, `Simulating`, debug views, status outputs | | as on SkinnedSoftbody |
| `Paint` (hidden) | | the paint dabs, as now |
| `Tears` (hidden) | | the synced tear list |
| `Mend` (button) | | empties `Tears` |

Settings apply live, except those that change the mesh: Renderer, StartAs, the paint and the tears.

## Reused from the softbodies

- **Reading the mesh:**
  - Submeshes are joined, and each triangle keeps its material (Plugin/FleshInstance.cs:271-284).
  - UV-seam duplicates are welded by exact position (Core/Flesh/SimSurface.cs:59-77).
- **Animated targets:** bind space, blendshapes, then CPU skinning of the simulated points (Core/Flesh/FleshRig.cs:337-366).
- **Colliders:**
  - Your own VRIK capsules, head sphere, and hand and foot boxes (Plugin/FleshColliders.cs:172-189).
  - Other players' VRIK colliders and the engine's dynamic-bone player spheres (62-135).
  - World colliders, gathered 10 times a second: Box, Sphere, Capsule, Cylinder, Cone, Triangle, Mesh and ConvexHull
    (FleshInstance.cs:944-1032, 1127-1195).
  - Friction, sweeping of moving colliders, and the pair grid (Core/Flesh/XpbdCollision.cs).
- **Paint:**
  - The brush and PaintLayer, through a new IPaintable/IBrushTarget (Plugin/PaintTargets.cs).
  - Cloth must be added to `SoftbodyRuntime.CollectPaintTargets`, which lists the two types by hand (SoftbodyRuntime.cs:124-131).
- **Running it:**
  - The private mesh copy and the upload scaffolding.
  - One task per cloth per frame, collected the next frame.
  - The Gate lock, Budget, culling and pausing.

## New

### The solver (Core/Cloth)

- **Steps:** XPBD in small steps of fixed length (1/720 s, so 12 per 60 Hz frame) with an accumulator, at most 24 a frame.
  - Each step makes one pass over the constraints; that beats extra iterations ("Small Steps", 2019).
  - The steps have a fixed length because the fluid bounced when its steps followed the frame rate (0.25.1).
- **Mass by area:** Weight × a third of each touching triangle's area. Pinned points have no mass and follow their target.
- **Rest lengths are fixed at build.** The flesh re-rests to the animation every frame, but cloth must keep its size.
- **Constraints:**
  - **Stretch:** one distance constraint per edge, with its compliance from `Stretch` (0 for woven cloth).
  - **Bending:** a distance constraint between the two points across each shared edge (Müller's cheap version), with its
    compliance from `Bend`. Isometric bending (Bergou 2006) can replace it later if folds look too soft.
  - **Avatars:**
    - Max distance: each point stays inside a sphere of brightness × MaxDistance around its animated place.
    - Backstop: a point can't go behind the skin, measured along the animated normal (Unity, Unreal, PhysX and Obi all
      do this).
  - **Collisions:** XpbdCollision's shapes, kept at `Thickness`, with friction.
- **Air:** per-triangle drag and lift from the velocity relative to the wind (NvCloth's model).
  - Drag acts along the flow, and lift across it.
  - Both scale with area × speed².
  - Lift is zero when the triangle faces the flow head-on or edge-on.
- **Avatar motion:**
  - The cloth is simulated in the avatar's space, carrying (1 − Inertia) of its motion each frame (as Unity, NvCloth and
    Chaos do; Chaos defaults to 0.75).
  - The flesh's per-point 5 m/s carry would flatten a cape in a fast spin, so it isn't used.
  - A jump of more than 0.75 m in one frame resets the cloth to the animated pose, as the flesh does now.
- **Own capsules:** a point that starts inside one at bind pose (a tight skirt over thick thighs) gets that depth as an
  allowance.
  - The flesh instead ignores colliders a point touches at rest. That would let a leg pass right through the skirt.
- **Long-range tethers are not used:**
  - NVIDIA holds a patent on them, US9070220. Google Patents lists it as active until about 2033, which isn't a legal
    conclusion. The source is public under MIT.
  - FleX and Obi turn tethers off for tearable cloth anyway.
  - Small steps, the max distance and the backstop do their job here.

### Tearing

- **When:** an edge tears when it is longer than `TearAt` × its rest length. That is FleX's maxStrain and PhysX's tear
  factor. Hue bands can later make painted areas weaker or stronger.
- **How it splits** (as in FleX and the 2007 Position Based Dynamics paper):
  1. Take one end of the edge.
  2. A plane through that point, across the edge, sorts its triangles by which side their centre is on. The centres are
     taken in the rest mesh, so every client sorts the same way.
  3. One side moves to a new point. If either side would be empty, skip the split.
  4. The torn edge loses its bending constraint. Edges that became borders keep their stretch constraint.
  5. A point left joining two separate fans of triangles (a "bowtie") splits again.
- **Which end:** the one with more triangles, then the lower index. There is no random choice, so every client gets the
  same mesh.
- **TearSpread** weakens the edges next to a fresh tear, so a rip runs instead of fraying. This is Obi's "tear
  debilitation"; Obi recommends 0.2–0.6.
- **Limits:**
  - At most 8 splits a frame.
  - Room for a fixed number of new points, say 25% of the cloth's points. When that is full, it stops tearing.
- **Rips follow the triangles,** so a coarse mesh tears in blocky lines. Cutting through triangles would need remeshing,
  which is too slow for real time.

### Tears in multiplayer

- Each client simulates on its own, so one client decides the tears.
  - **Who:** the wearer for avatar cloth, and the host for world cloth.
  - That client adds each split to the synced `Tears` list as (point, edge).
  - The others never tear on their own; they apply the list in order.
  - Teardown syncs breaks the same way: ordered commands, replayed for anyone who joins late.
- The list saves with the object, and a late joiner replays it when its cloth builds. `Mend` empties it.
- A client that doesn't decide may see the cloth stretch for a moment before the tear arrives.

### Getting it on screen

- **World cloth (MeshRenderer):**
  - A private mesh copy goes through the engine's normal upload, like the lattice Softbody, so no fork is needed
    (Plugin/AssetUploads.cs:62-85).
  - On a tear, the whole mesh is sent with its new vertex count and triangles.
  - The stock renderer keeps the same Unity mesh for that, so nothing flickers
    (RenderiteSrc/Renderite.Unity/Assets/Mesh/MeshAsset.cs:405, 429-472).
- **Avatar cloth (SkinnedMeshRenderer):**
  - It uses the private copy and the fork's in-place path, like SkinnedSoftbody, so it needs the fork.
  - **One bone per simulated vertex:**
    - In the private copy, every simulated vertex is weighted fully to one bone (the hips), with its blendshapes removed.
      The plugin writes its exact position through that bone.
    - Pinned vertices keep their own weights and blendshapes.
    - This avoids the flesh path's shrink where a vertex's bones turn apart: (ΣwR)(ΣwRᵀ)u. That is harmless for
      millimetres of flesh but large for a skirt between spreading legs.
  - **Room for tears:**
    - Spare vertices are reserved in the private copy, on the same bone and unused at first. Bone weights and
      blendshapes can't change later without the renderer recreating the mesh.
    - A tear then rewrites some triangles.
    - The fork's in-place path takes vertex streams only (MeshAsset.cs:245-255). It needs a small change: take new
      triangles in place when the vertex count, index count and submeshes match.
    - The change goes behind a new handshake, `Local\Softbody.RendererPatch.SkinnedInPlace.v2`, next to v1. Without it,
      avatar cloth still runs but doesn't tear.
  - **Normals and tangents** are recomputed from the cloth's own triangles. The flesh rotates its normals instead, which
    is fine for millimetres but not for folds.
  - **Risks to check in game:**
    - The in-place path re-sends the whole vertex buffer every frame. If the skirt is part of a 60k-vertex body mesh, that
      costs about 0.65 ms, like SkinnedSoftbody.
    - Skinned bounds follow the bones, so a cape flying far out might be culled.
    - Writing indices doesn't tell the renderers that use the mesh, so check that the skinned mesh shows the new
      triangles.

## Found while planning: avatars with vertex colours (likely bug)

- **Cause:**
  1. When an avatar mesh has vertex colours, SkinnedSoftbody includes Colors in its first in-place upload
     (Plugin/FleshInstance.cs:354, 822-823).
  2. The fork's in-place test leaves Colors out (MeshAsset.cs:245-255). So that upload takes the stock path, which
     destroys and recreates a skinned mesh (405-411).
  3. The plugin then stops that softbody (FleshInstance.cs:851-861).
- **Expected symptom (not tested in game):** a SkinnedSoftbody on a mesh with vertex colours stops right after it starts,
  and its stats count a recreated mesh.
- **Fix:**
  - The plugin stops sending colours on the avatar path; avatars don't show the paint anyway. **Done in 0.29.0**, also for
    the lattice Softbody's skinned GPU path, which had the same flaw (its first upload carried the colours).
  - The 0.30.0 fork change can also take colours in place.

## Phases

Each phase is its own version, and no number is reused.

1. **World cloth, 0.29.0.** No fork change.
   - Cloth on static meshes, Cloth Wind and anchors.
   - The solver, with world and player colliders.
   - The brush.
   - Tearing, with the synced list and Mend.
   - Debug views and offline tests.
2. **Avatar cloth, 0.30.0** (built 2026-11-14, except layered garments; see "Built in 0.30.0").
   - Skinned meshes, with the one-bone private copy.
   - Max distance, the backstop and inertia.
   - Your own and other players' colliders.
   - Double-sided cloth: two triangles over the same three points are simulated once and drawn twice.
   - Layered garments: an inner shell is bound to the outer one.
   - The fork change for tearing on avatars, which also takes colours in place.
3. **Cloth Blast, 0.31.0** (built 2026-11-14; see "Built in 0.31.0").
   - A push from a point, which can free the cloth there and shred it, the same on every client.
4. **Effect cloth, 0.32.0** (built 2026-11-14; see "Built in 0.32.0").
   - WaitForBlast: asleep, with nothing built, until a blast wakes it; a Target on the Cloth Blast.
5. **Copies and a mend timer, 0.33.0** (built 2026-11-14; see "Built in 0.33.0").
   - MendTimer; DuplicateOnMend leaves the loose pieces behind as a local copy while the original comes back whole;
     CopyLifetime.
   - Fast pieces, 0.33.1 (built 2026-11-14; see "Built in 0.33.1"): no 10 fps spiral after a strong blast, and no
     stepping through thin floors.
6. **Fabrics and polish, 0.37.0** (moved from 0.34.0, which became Pop, the death effect, from 0.35.0, which became
   Pop's soft pieces and Adhesion, and from 0.36.0, which became Pop's Strength range: DESIGN-pop.md).
   - Fabric presets: silk, cotton, denim, leather, rubber and chainmail.
   - Hue bands for fabric regions and weak seams.
   - Self-collision, with a spatial hash (Müller's method):
     - the thickness equals the point spacing;
     - pairs that are close at rest are skipped;
     - speed is capped per step so nothing passes through.
   - World cloth that sleeps when it is still.
   - Grabbing anywhere, and freezing the cloth and saving its shape as a normal mesh.
7. **Later.**
   - Running on the GPU.
   - Inflatables (air pressure).
   - Smoke and liquid pushing cloth, and wet cloth.
   - A knife tool.
   - Rigid bodies.

## Offline tests

- **A hanging sheet:** how much it stretches, and that it stays still at rest.
- **Draping over a sphere and a box:** it never goes deeper than `Thickness`, and friction holds it.
- **A flag in wind** at a frame rate jumping between 30 and 144 fps: the same look, and no blow-ups.
- **Tearing:**
  - It tears at `TearAt`, with at most 8 splits a frame.
  - The mesh stays valid.
  - Replaying the list on a fresh build gives the same mesh.
  - Mend gives back the original.
- **Avatar (phase 2):**
  - The skirt on screen matches the simulation exactly.
  - A leg pushes it but doesn't pass through.
  - A teleport resets it.
- **Cloth Blast (0.31.0):** cut lines and slits, piece sizes, confetti, the replay with splits between shred parts, seams
  and double-sided cloth, running out of room, release, the push's momentum, avatar cloth, and the time a part takes.
- **Plugin check:** the new types and fields, and the new hash.
- **Cost:** ms per frame for 1,000 to 8,000 points.

## Cost guess (to be measured)

- The flesh solver runs 800 points (3,570 tets) at 8 substeps in 2.1 ms on one core.
- Cloth constraints are simpler, but it takes more steps. A guess: 1 to 2 ms for 2,000 points at 12 steps, plus
  collisions. Spreading the work over cores, or the GPU, comes later.
- Draft caps: 8,000 points per cloth, the shared 12,000-point client cap, and the Budget auto-off.

## Open questions

- (Answered in 0.29.0, see "Built in 0.29.0": world cloth first; who decides tears; the Stretch and Bend mappings.)
- The tear decider while its world isn't focused: a cloth stops on a client whose world isn't focused, so while the host
  is in another world nothing tears for anyone. Moving the decision (for example to the present user with the lowest
  allocation ID, `User.IsPresentInWorld`) needs care: two deciders for a moment could append tears in an order that
  differs from what each applied.

## Links (from the 2026-11-12 research pass)

- XPBD (Macklin, Müller, Chentanez 2016): https://matthias-research.github.io/pages/publications/XPBD.pdf
- Small Steps in Physics Simulation (Macklin et al. 2019): https://matthias-research.github.io/pages/publications/smallsteps.pdf
- Position Based Dynamics (Müller et al. 2007), including tearing: https://matthias-research.github.io/pages/publications/posBasedDyn.pdf
- Ten Minute Physics cloth and self-collision: https://github.com/matthias-research/pages/blob/master/tenMinutePhysics/14-cloth.html,
  https://github.com/matthias-research/pages/blob/master/tenMinutePhysics/15-selfCollision.html
- Quadratic (isometric) bending (Bergou et al. 2006): http://www.cs.columbia.edu/cg/quadratic/
- Long-range attachments (Kim, Chentanez, Müller 2012): https://matthias-research.github.io/pages/publications/sca2012cloth.pdf;
  the patent: https://patents.google.com/patent/US9070220
- PhysX cloth manual: https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/physx/guide/Manual/Cloth.html
- NvCloth guide and its air model: https://archive.docs.nvidia.com/gameworks/content/gameworkslibrary/physx/nvCloth/UserGuide/Index.html,
  https://github.com/NVIDIAGameWorks/NvCloth/blob/master/NvCloth/src/SwSolverKernel.cpp
- FleX cloth tearing: https://github.com/NVIDIAGameWorks/FleX/blob/master/extensions/flexExtCloth.cpp,
  https://github.com/NVIDIAGameWorks/FleX/blob/master/core/cloth.h
- Obi Cloth tearing: http://obi.virtualmethodstudio.com/manual/7.0/clothtearing.html
- Unity Cloth: https://docs.unity.com/en-us/engine/current/manual/physics-section/physics-overview/class-cloth
- Chaos Cloth (inertia and teleport): https://dev.epicgames.com/documentation/en-us/unreal-engine/python-api/class/ChaosClothingInteractor
- Frostbite cloth self-collision (GDC 2018): https://media.contentapi.ea.com/content/dam/eacom/frostbite/files/gdc2018-chrislewin-clothselfcollisionwithpredictivecontacts.pdf
- Teardown multiplayer (synced breaks): https://blog.voxagon.se/2026/03/13/teardown-multiplayer.html
- Blender cloth presets: https://github.com/blender/blender/tree/main/scripts/presets/cloth
