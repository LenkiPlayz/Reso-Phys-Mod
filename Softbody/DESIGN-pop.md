# Pop: a model bursts into pieces (built as 0.34.0; soft pieces and Adhesion in 0.35.0; a Strength range in 0.36.0)

Lenki's goal behind the Cloth Blast: a death effect for a team shooter, where the player model bursts into pieces. Lenki
wanted it named something generic, "pop", and to keep the blast's Shred so the piece size can be chosen. Built
2026-11-14 as 0.34.0, installed at 21:39, and run in the game at 22:03 (see Limits). Lenki then wanted the pieces to
have "some cloth physics to its look", and to stick to surfaces: 0.35.0 makes each piece a little cloth and adds
Adhesion (built 2026-11-15 00:14, installed at 00:45, run in the game at 02:45: "Works good"). Lenki then asked "could
the strength be a random range per particle?": 0.36.0 adds MaxStrength, each piece flying out at its own speed between it
and Strength (built 2026-11-15 03:05, installed at 03:11, on GitHub as the stable v0.36.0 at 03:18, not yet run in the game).

## What it does

- Fire (a button, and an Action for ProtoFlux) bursts every mesh the Target shows into pieces.
- The pieces keep the model's look: its materials, textures, vertex colours, UVs, and the pose it had at the shot, bones
  and blendshapes too. The model's own mesh is never changed.
- Big triangles are cut into smaller ones, so there can be more pieces than the model has triangles.
- The pieces fly out, land, and go still. Since 0.35.0 each is a little cloth: it flutters and folds in the air, glides,
  and drapes over what it lands on (Stiffness; at 1 they are 0.34.0's stiff flakes). With Adhesion they stick where they
  hit, on walls and under ceilings too. They hit the world, never players. After Lifetime they are gone, shrinking away
  over the last second.
- With HideTarget the model is hidden while they fly. ShowTarget brings it back early, on every client (for a respawn).

## Fields (inspector order)

| Field | Default | Range | What |
|---|---|---|---|
| Target | the slot it is attached on | | the slot whose meshes pop (it and everything under it) |
| Shred | 0.7 | 0..1 | piece size, in the Cloth Blast's steps: a few big chunks (0) to confetti (1) |
| MaxPieces | 1500 | 1..5000 | about the most pieces: confetti gets no smaller than the surface split that many ways |
| Strength | 4 | -50..50 | m/s out from the model's middle (below 0: in); with MaxStrength, one end of a range |
| MaxStrength | 0 | -50..50 | 0.36.0: the other end of a range from Strength, each piece flying out at its own speed in it (0: no range) |
| Randomness | 0.5 | 0..1 | uneven push: some pieces up to half slower (not with a range), a little sideways, spinning (small ones faster) |
| Drag | 0.5 | 0..10 | how much the air holds the pieces back: for soft pieces how thick the air is (1: real air), for stiff ones per second |
| Stiffness | 0.3 | 0..1 | 0.35.0: from cloth that drapes like silk (0) to stiff (toward 1); at 1, 0.34.0's stiff pieces |
| Adhesion | 2 | 0..10 | 0.35.0: how hard the pieces hold on to what they hit, in their own weights: 1 just holds one on a wall, more holds them under a ceiling |
| Lifetime | 8 | 0.5..600 | seconds the pieces stay (and the hide lasts) |
| HideTarget | on | | hide the model while its pieces fly |
| Shot, ShotTime, ShotAt, Shown | | | the shot record, [HideInInspector] |
| LastFired | | | what the last shot came to on this client (local) |

## How it works

### In a session

- Fire, on the client that calls it, writes ShotTime (world time), ShotAt (where the Target is) and then Shot (a new
  random seed). That is all that is synced.
- Every client sees Shot change (its change event, then once the update that brought it is done, so the record is whole;
  a poll in OnCommonUpdate backs it up). It plays a shot only if it went off less than 2 s ago (and not more than 1 s in
  the future). So a client that joins later, or a world loaded again, does not replay old shots.
- Each client cuts the model as it shows there, with the shot's seed. The clients see the same pop, up to small float
  differences in the pose; nothing has to match exactly, since every client flies its own pieces.
- ShotAt minus where the Target is when the client plays the shot moves the pieces back to where the shot happened. So a
  model teleported away at the same moment (a respawn) still pops where it died.
- The pieces carry each client's own estimate of the Target's speed (smoothed over 0.1 s, at most 20 m/s; a jump over
  2 m in a frame counts as a teleport and resets it).
- ShowTarget writes Shown = Shot. Each client then ends the hide for that shot and every shot before it. A show that
  arrives while the cut is still running means that shot never hides.

### The capture (Plugin/PopBake.cs, world thread)

- Every MeshRenderer and SkinnedMeshRenderer under the Target that shows now: enabled, on an active slot, not on a local
  slot. A renderer this Pop hides already (a second shot while the first one's pieces fly) counts as showing.
- Up to 64 meshes and 600,000 vertices a shot; a mesh past either is left out (LastFired says so).
- The mesh is the shared asset's data (PrivateMeshes.SharedOf), never a softbody's or a cloth's own copy. Its arrays are
  read in place; the triangle lists are copied.
- A material per submesh, as the renderer draws them: a material past the last submesh draws the last submesh again (an
  outline or an overlay pass), and a submesh past the last material is not drawn. Non-triangle submeshes are skipped.
  Property blocks line up with the materials.
- The pose: a skinned mesh by its bones' matrices at that moment (bind poses times the bones, as the cloth and the
  softbodies do), the blendshapes it shows (frame 0, weighted, before skinning: the engine's rule). A mesh without bones,
  or with bindings missing, moves with its slot.

### The cut (Plugin/PopBake.cs and Core/Pop/PopCut.cs, off the world thread)

- The meshes are baked into the world as they were drawn: blendshapes, then skinning, then the shot's offset.
- Cells of seeded cellular noise make the pieces, sized by Shred in the Cloth Blast's steps (ClothBlastMath.Piece and
  Spacing). Triangles longer than the spacing are first cut into a grid, every channel carried along (UVs in all 8
  channels at their own size, colours, normals, tangents).
- A cell that falls apart (two arms, a hand and a leg) is split into the parts that touch.
- Each piece gets back faces 0.3 mm behind its front, so a flake shows from both sides.
- Each piece collides with up to 32 points spread over it while fast, and with all its vertices (up to 128) once slow, so a
  piece lying on something has none of its vertices in it.

### The flight (Core/Pop/PopSolver.cs)

- The push: each piece flies straight out from the model's middle at Strength; Randomness slows it (by up to half at 1),
  turns it a little sideways and spins it (small pieces faster). 0.36.0: with MaxStrength (not 0, not Strength itself)
  each piece picks its own speed between the two instead, evenly, from the shot's seed (so the same on every client), in
  either order; Randomness then only turns and spins it, by its own speed. A soft piece's particles all get its push, so
  it flies off whole. Then the pieces carry the Target's own speed.
- Stiff pieces, XPBD (Müller et al. 2020), 240 steps a second, at most 8 a frame.
- Each point's whole way in a step is tested against triangles and boxes, so a fast piece cannot step over a thin floor.
  A piece that would go deep stops where it first touches.
- Friction and no bounce; a piece that lies still for 0.4 s sleeps and costs nothing more. Pieces only wake from the pop.
- The shapes near the pieces are found once per run of steps, as the cloth's since 0.33.1.
- The plugin gathers the world's colliders (FluidColliders, the world category only) in a box around the pieces still
  awake and where they head, without the Target's own colliders.
- A piece that falls 50 m below the model's middle stops.
- In a world in the background the pieces wait; their Lifetime still runs.

### Soft pieces (0.35.0, Core/Pop/PopSoft.cs and PopSolverSoft.cs)

- Made with the cut, off the world thread, when Stiffness is under 1.
- Each piece gets a few particles. Its vertices are welded by place (seams), and the points farthest apart along its
  surface are picked (Dijkstra over its edges), the first on its rim, until none of its points is further than 0.6
  spacings from one: at least 4 (two triangles that can fold), at most 48. The spacing is about a piece's size over 2.5,
  from 1 to 4 cm, and wider where a cut would get more than about 6,000 particles.
- Each particle stands for the part of the piece nearest to it along the surface, and weighs that part's area at 1 kg/m²
  (a heavy cloth: felt, thin leather). Two parts that touch make an edge; three that meet make a triangle; the two corners
  across an edge that two triangles share make a bending pair, as on the cloth. Parts of a piece that no edge joins (a
  weld this missed, hair cards the cut kept together) are tied to the nearest part. A piece whose triangles share no
  edge (or with a particle hanging off one by a single edge) bends at each particle instead, its neighbours there keeping
  their distance: only a piece cut as a single triangle cannot bend.
- A step is the cloth's: the air on each triangle (drag against where it goes, as much as it faces it, and lift across it:
  the cloth's formula), gravity, the edges twice (a squeezed one pushes back at a tenth, so it buckles rather than
  squashes), the bending pairs (XPBD, compliance from Stiffness, none at 1), then back toward its own shape as far as
  Stiffness asks (shape matching, Müller et al. 2005: none at 0, ten times as far for every 2/7 more; a 30 cm square
  with 10 cm of it over a table's edge hangs 11.3 cm at 0, 8.8 cm at 0.3, 2 cm at 0.6, and not at all at 1), then the
  contacts.
- Each particle collides as a stiff piece's point does: its whole way in a step against triangles and boxes, friction, no
  bounce, 3 mm thick.
- The motion within a piece dies down at 3 a second (Müller et al. 2007, 3.5): its flapping, not its flight or its spin.
  In flight a piece keeps its spin: what a step's straight moves lose of it goes back (a spinning 30 cm square keeps 99 %
  of it over 2 s).
- On screen each vertex follows three particles, of the triangle it lies on best: it sits where its barycentric weights
  there put it among them as they are now, and its way off that triangle (out of its plane, or past its rim) turns as
  they turn (a particle turns as its triangles have since the cut). So a piece bends smoothly between its particles and
  keeps its look: its triangles, UVs and every other channel stay as cut. One that crumples as it lands moves each
  vertex only as far as its particles go.
- Only the particles collide, so each vertex is also kept out of what its piece is near: pushed back along the way from
  the particle it follows most (which lies outside everything). Without that, a piece landing edge first at 6 m/s showed
  its rim 25 mm in the floor for a frame (the rim reaches past the particles). A sleeping piece's are put out once, when
  it goes to sleep.
- A piece sleeps once its fastest particle has been under 5 cm/s for 0.4 s while it touches something.

### Adhesion (0.35.0)

- A particle (soft) or a collision point (stiff) that touched something holds on to it while it is within 1 cm of it
  (beyond its thickness): each step it is pulled back to it and held along it, each by at most Adhesion times how far
  gravity moves it in a step from rest. So at 1 a piece on a wall just holds, and above 1 one holds under a ceiling. While
  it touches, the grip adds to the friction.
- A piece lets go when it gets more than 1 cm away (a fast bounce does at once). A piece held still sleeps there and stays
  until Lifetime is up.
- A stiff piece's friction and grip move it as a whole, so a hold at one point swings the others. After them its points
  are pushed out again, without friction, until none moves: none ends a step through a surface (the next step's sweep
  starts where the last ended, and on a two-sided mesh a point past its middle goes out the back). Without that, at
  Adhesion 10, 82 of 848 stiff confetti pieces ended up to 53 mm in a two-sided mesh floor; now none does.

### On screen (Plugin/PopDebris.cs and PopDebrisMesh.cs)

- The first shot on a client makes a PopDebris on a local slot under the world's root (never synced or saved). Each shot's
  pieces get a local slot under it, with a PopDebrisMesh (a ProceduralMesh) and a MeshRenderer with the model's materials,
  in the cut's order, and the first renderer's shadow, motion vector and sorting settings.
- The mesh goes up in full once (layout, triangles, every channel), then only positions, normals and tangents, and only on
  frames where a piece moved.
- When nothing is left, the PopDebris removes its slot.

### The hide (Plugin/PopDebris.cs)

- Local drives (FieldDrive<bool> in the PopDebris's own SyncList, local like its slot) on each renderer's Enabled. A
  renderer whose Enabled something else drives is hidden through its slot's Active instead, when that slot has no
  children and its Active is free. Otherwise it stays shown, and the Status counts it.
- A drive is granted by the engine in the next update; only then is anything written through it (written without a grant,
  the value would sync).
- The hide starts 2 frames after the pieces' mesh is first built, so there is a frame with both rather than one with
  neither.
- Before a drive lets go, its field is put back as it was, through the drive. The value to put back is the field's own when
  the drive took it, or whatever reached it since (from the others or from this client). Letting go while it holds false
  would be wrong on the session's host: the host sends a released field's value to everyone.
- The hide ends with the shot's Lifetime, with ShowTarget, or when HideTarget is turned off. A newer shot keeps the hide of
  an older one while it is still being cut.

## Engine facts it relies on (read in the installed engine's IL with Cecil)

- LinkBase.set_Target refuses a field that already has a direct link (it warns), so Take checks IsLinked first.
- Links are granted in World.LinkManager.GrantLinks, once per update; IsLinkValid needs the grant. There is no check
  between local and synced elements, so a local component's drive can hold a synced field on its own client.
- An incoming value is applied to a driven field too (Sync.InternalDecodeFull sets it with change events). That is how the
  hide learns what to put back.
- On the authority, a released drive makes SessionSyncManager send the field's value as a correction.
- ProceduralMesh resets the upload hint before UpdateMeshData and adds Dynamic and Readable itself.
- SyncField.LocalFilter is a filter on writes, not a local value, so it cannot hide anything.
- OnFocusChanged and OnWorldDestroy must not call their base (the engine throws by design); PopDebris's OnWorldDestroy
  does not, and Pop overrides neither.

## Limits and open points

- In the game (2026-11-14 22:03): five pops of an avatar's body (one mesh, 39,766 vertices) made 896 to 961 pieces each,
  cut in 85 to 163 ms, at 117 to 146 fps (144 without), with no warnings. The log does not say whether the hide's drives
  were granted. Lenki: closer to the goal, but the pieces should look more like cloth (they tumble as stiff flakes).
- In the game with 0.35.0 (2026-11-15 02:45): 49 pops of the same body, 19 to 4,511 soft pieces each (771 to 28,125
  particles), no warnings; fps median 136, the tenth 92, the lowest 30 with two or three big pops flying at once. Lenki:
  "Works good". The cut (with the soft build) took 112 to 293 ms, but 393 to 970 ms for the pops of 19 to 27 big pieces:
  they burst that much after Fire (0.34.0's stiff cut took 85 to 163 ms).
- A world saved by its host while a world object's pop hides it saves that renderer as disabled (driven values save as
  they are). Avatars are not saved with the world.
- Fire writes to the Pop's fields. On an avatar protected against other users' changes, a Pop fired by another user may
  be refused; the Pop can sit on a game-controlled slot with Target set to the avatar.
- If the game disables the model's renderers in the same update as Fire, the clients see them disabled when they cut, and
  there is nothing to pop. Fire first, and leave the hiding to HideTarget (or hide it a moment later).
- A Lifetime shorter than the respawn time shows the model again where it died, unless the game moves it away.
- Soft pieces: only their particles collide (the vertices are kept out of what is near, but push nothing), so a piece
  bent over a sharp edge can cut the edge's corner between two of them. One landing edge first at speed squashes for a
  frame or two before it springs back (its particles stop one at a time).
- A sleeping piece stays where it is: nothing but a new pop wakes it (a piece stuck to a door that opens stays in the air).

## Tests

- `pop` (39 checks, Core): the cut's piece counts by Shred, the capsule player with hair cards, two meshes 1 cm apart,
  a flake landing on a corner, a player popped over a floor (outward speed, no piece in the floor at rest, all still,
  no uploads once still, the awake box, the shrink), confetti at 60 m/s onto thin floors (two-sided, front-only, a 2 cm
  box; 30 and 144 fps), and the cost over a 12,800-triangle mesh floor.
- 0.35.0 adds 84 to `pop` (`pop-soft` runs them alone, tests/PopSoftTests.cs): the soft build of a square (one piece,
  confetti), the capsule (Shred 0.8, confetti) and the capsule with 400 hair cards (every piece 3 particles or more and
  tied together, every vertex on three of its own, the model exactly as cut, the bounds), the same build twice, a piece
  only the weld joins; a soft square spinning free (its shape, 99 % of its spin, its path); a square over a table's edge
  at Stiffness 0, 0.3, 0.6 and 1 (the softer, the more it drapes; never in the table); a square through air and none; a
  square thrown at a wall and up at a ceiling, soft and stiff, Adhesion 2 and 0; a player popped into soft pieces over a
  floor (they bend on the way, every piece of 4 particles or more can bend, no vertex in the floor landing or lying, all
  still, the bounds and the awake box, the shrink); soft confetti at 60 m/s onto thin floors; and soft and stiff
  confetti over a two-sided mesh floor at Adhesion 2 and 10 (none in it), with the cost.
- 0.36.0 adds 27 to `pop` (`pop-strength` runs them alone, tests/PopStrengthTests.cs): a capsule player's pieces kicked
  at Strength 2 and MaxStrength 8 (every piece between the two, over the whole range, each quarter of it 21 to 29 % of
  them, all outward), the range in either order, the same seed and another, no range at MaxStrength 0 or at Strength
  (exactly the push without one; Randomness 0.5 still slows by up to a quarter), a range with Randomness 0 and 1 (the
  same speeds, still sideways and spinning), an inward range (-2 to -8), soft pieces (as fast as stiff ones, each flying
  off whole); and the player popped over a floor at 1 to 10 m/s against 5.5 alone, stiff and soft (nine in ten lie 0.22
  to 3.5 m out, against 0.23 to 2.3 stiff and 2.4 soft; all down and still; none in the floor).
- `plugin-check` (179): Pop's fields (MaxStrength after Strength), buttons and guarded callbacks; the shot record hidden;
  PopDebris's Status and its protected drive list; PopDebrisMesh as a ProceduralMesh with no sync members; the weave; the
  28 registered types; the new compatibility hash ZDD4CvWVbK3FMGZ8lQdbCA== (0.35.0's was NFGzc83XbWSzcSAsQyVTfQ==,
  0.34.0's LA9nNgL8dW4qhffBF6g70Q==).
- `cloth` (187) and `body` (30) still pass.
- Cost (offline, Deadlock open, 0.34.0): a capsule of 6,816 triangles cut into 848 pieces in 10 ms; 1.8 ms a frame on
  average while they move (worst 5.7), next to nothing once still.
- Cost (offline, SteamVR open, no game, 0.35.0): the same 848 pieces as soft ones (7,718 particles) built in 4 ms after
  the cut (the first build of a run about 40 ms), off the main thread; 2.2 ms a frame on average while they move (worst
  7.3), against 1.2 ms (worst 4.2) for them stiff; a player at Shred 0.8 (381 pieces, 5,780 particles) 0.6 ms.
