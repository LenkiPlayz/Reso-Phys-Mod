# Skinned softbody: tet shell + XPBD (design, not started)

Lenki's direction (2026-11-10). This is a new component for SkinnedMeshRenderers. The lattice `Softbody` stays for static meshes, where it works.

## Why the lattice doesn't fit avatars

What the 0.10.0 debug views showed on the winged, tailed avatar:

- **A box grid ignores the body's shape.** Cells cross the tail, wings and torso, so each lattice point blends bones from parts that aren't connected. That's what bent the lattice. Inverting those blended matrices is also ill-conditioned (the "394 mm" displacement).
- **Resolution is spread over the whole bounding box,** not concentrated where the paint is.
- **The spline embedding links vertices across gaps,** such as between the legs or between an arm and the torso.
- **0.10.1 only patches the symptoms.**

## The idea

- A separate component, working name `SkinnedSoftbody`, on a SkinnedMeshRenderer.
- **Tets are built only from the painted (vertex colour) region**, as a soft shell of a given thickness.
- **Tet vertices copy the bone weights and blendshape deltas of real mesh vertices.**
- **The tets are simulated with XPBD,** one simulation per renderer.
- **Tets rebuild only when the mesh, the paint, or a structural setting changes.** Every frame after that is just moving vertices.
- **Both the tets and the vertex warp live inside the paint bounds,** so all the detail goes where it matters.

## Build (cached, on a worker thread)

1. **Region.**
   - Vertices with softness > ε come from the existing PaintLayer, channel and hue bands.
   - A 1–2 ring band of firm vertices around them becomes the pinned border.
   - Vertices split on UV seams are welded by position first, or the sim tears along the seams.
2. **Sim surface.**
   - The region is decimated to a target edge length (`Resolution`) by half-edge collapse. That keeps original vertices, so **every outer sim vertex is a real mesh vertex**: it copies that vertex's 4 bone weights and blendshape deltas exactly, with no averaging across body parts.
   - Open edges and non-manifold bits are fine. The shell just has a border there.
3. **Shell.**
   - **Inner surface:** the outer surface moved inward along smoothed normals by `Thickness`. It's clamped to a fraction of the distance to each vertex's main bone, so it never passes the bone and thins out on fingers and ears.
   - **Prisms to tets:** 1–2 layers. Each triangle × layer prism becomes 3 tets, with the diagonals picked by global vertex index so neighbouring prisms agree.
   - **Inverted tets:** at build time, any inverted tet locally reduces the thickness, and the build retries.
   - **Inner vertices:** they take their outer partner's blendshape deltas, and its bone weights or weights moved toward the nearest bone (to be decided).
4. **Material per vertex.**
   - Softness comes from the paint.
   - `BoneDensity` scales stiffness by distance to the bone: firm near it, soft toward the skin.
   - Mass = tet volume × density.
5. **Embedding.**
   - Each render vertex in the region gets smooth weights over nearby outer sim vertices: k nearest within about 2 edge lengths, a Wendland kernel, normalised, skipping opposite-facing normals like the blur brush does.
   - **We interpolate displacements, not positions.** A render vertex keeps its own exact skinned and blendshaped position and only adds a smooth offset. That's the 0.2.1 crease lesson: no piecewise-linear tet barycentrics on the visible surface.
   - Chaos Flesh uses barycentric tet bindings with a surface-projection fallback; offsets let us afford something smoother.
   - The fade from firm to soft is the softness gain per render vertex, as now.

## Per frame

1. **Targets:** `t_i = LBS_i(rest_i + Σ_k w_k Δ_ik)`, using the renderer's bone matrices (`SkinMatrices`) and only the active blendshape weights (sparse `BlendshapeDeltas`).
2. **The rest shape follows the animation.**
   - Each tet's rest matrix is rebuilt from its target positions: one 3×3 inverse per tet, or only when blendshapes move.
   - Elasticity then means "keep the animated, blendshaped shape", so it never fights a bent elbow or a blendshape.
   - This is a cheap version of Complementary Dynamics, where physics only adds the secondary motion.
3. **XPBD with small steps** (many substeps, 1 iteration each):
   - **Inner layer:** kinematic, with infinite mass, set to its targets. This is Chaos Flesh's kinematic constraint.
   - **Outer layer:** weak follow springs (Chaos's "weak constraint"), with compliance from `Follow` × softness.
   - **Tets:** Stable Neo-Hookean split into a deviatoric constraint and a volume constraint (Macklin & Müller 2021). `Stiffness` and `Volume` map onto those two.
   - **Damping:** on velocity relative to the target's velocity, so a running avatar's flesh doesn't lag as a whole.
4. **Collisions:**
   - A sphere per outer sim vertex, sized by the local edge length.
   - Tested against the ground, the world proxies (existing discovery code), and **bone capsules fitted from the firm mesh**, for self-contact such as an arm against the chest or a thigh against the belly.
   - They're inequality constraints in the same XPBD loop.
   - There's no Bepu sim: a flat-array solver beats one Bepu body per point. Bepu and Resonite are only used for world queries.
5. **Output:**
   - Per sim vertex, the offset is `x − t`, in world space.
   - The embedding gives each render vertex its offset. That goes into bind space through the vertex's own skin matrix (`Lbs.TryInvertSimilar`).
   - The GPU path then uploads it in place, and the renderer adds the blendshapes and skins.

## Instancing

- **Build data** (topology, rest shape, weights, embedding) never changes, so it's shared per (mesh, paint, structural settings) key.
- **Sim state** is per renderer.
- **The hard part is output.** Two renderers wearing the same avatar share one mesh asset, and today's GPU path writes into that asset, so they'd show each other's wobble. Options to look into:
  - **A.** A private local mesh asset per instance: the first upload is full (bones and blendshapes), then in place. Needs a way to point the renderer at it locally without a synced write.
  - **B.** We own the renderer fork, so per-renderer offsets could go through a named shared-memory block (like the handshake event), and the fork adds them per SkinnedMeshRenderer with no mesh upload at all. Cleaner and faster. It depends on how the fork identifies renderers.
- **As built (0.14.0): option A.** Each running SkinnedSoftbody makes a dynamic mesh copy, uploads it in full, then swaps it into its own StaticMesh's private `_asset` field and re-notifies the renderers (`Plugin/PrivateMeshes.cs`).
- **Fixed in 0.26.1, after the first in-game run (2026-11-11):** the avatar rebuilt every 2 s ("the renderer's mesh changed"). What the engine does, read from its IL (build 2026.11.11.1148):
  - `StaticAssetProvider.Asset` returns the held asset only if `IsAssetAvailable`: it must be loaded (LoadState PartiallyLoaded or FullyLoaded), and the URL must be unchanged. A dynamic mesh's LoadState stays `Created` forever, because only static variant loading sets it. So after the swap the provider handed out null: the renderers dropped the body, and the softbody's own check saw a changed mesh.
  - `UpdateAssetAsync` compares the held asset's `AssetURL` with the provider's URL. The copy had none, so the provider's next asset update would have dropped it.
  - `FreeAsset` (destroy, a URL or variant change, no references left) calls `RemoveRequest` on whatever it holds. On a dynamic asset that throws "Asset instance isn't Static" inside the engine. The engine catches it per component, but the provider is then left undisposed and its request on the shared asset stays.
  - **The fix (`PrivateMeshes.StandIn`, right before the swap):**
    - The copy's LoadState is set to FullyLoaded through the raw setter. `SetLoadState` would call `VariantManager.VariantLoaded`, which is for the engine's own variants.
    - It gets the shared asset's `AssetURL` and `VariantManager`, so its metadata is the shared one.
    - It becomes a static asset relocated to the shared one (`Asset<V>.relocatedAsset`), so `RemoveRequest` on the copy goes to the shared asset. That is the engine's own path for relocated variants.
    - `Retire` undoes all of this before the copy is unloaded.
    - Teardown no longer removes the provider's request by hand; the relocation does it.
  - `PluginCheck.InstancingStandIn` runs these on real engine objects (a Mesh pair and a StaticMesh built without a world). Before the fix, the StaticMesh hands out nothing and RemoveRequest throws; after it, it hands the copy out and the request leaves the shared asset.
  - **Guard:** a SkinnedSoftbody that has to rebuild 6 times within a minute (mesh, asset or bones changed under it) pauses with a status instead of looping.
  - **Still not tried in game with two avatars on one model.**

## Reused vs new

- **Reused:**
  - PaintLayer, the brush, SoftnessMap and HueBands;
  - SkinData, BlendshapeDeltas, Lbs and SkinMatrices;
  - GpuUpload and the renderer fork;
  - the debug mesh (a tet view replaces the cell view);
  - world proxy queries, stats and Budget.
- **New:**
  - `SimSurface` (weld, decimate), `TetShell`, `Embedding`, `XpbdSolver`, `BoneCapsules`;
  - the `SkinnedSoftbody` component.
- **The new component is a data-model change:** a new compatibility hash, so friends need the new build.

**Draft fields:**
- Renderer;
- Channel, Invert and StartAs (paint, as now);
- Resolution, Thickness, Layers;
- BoneDensity, Stiffness, Volume, Damping, Follow, Gravity;
- Collide, SelfCollide;
- Budget;
- DebugTets, DebugColliders.

## Phases

1. **Core, offline:** weld and decimate, shell, embedding, XPBD, plus tests and a perf bench. The tests:
   - a bent arm with a soft blob doesn't fight at rest;
   - no inverted tets;
   - volume stays within a set tolerance;
   - an inflating blendshape is followed;
   - offsets stay smooth across the region border.

   Real avatar data offline would help (an exported mesh); otherwise the tests use synthetic meshes.
2. **The plugin component on the current GPU path,** with debug views, then an in-game test on Lenki's avatar. That needs an install OK, Resonite closed, and the parked add-ons moved back.
3. **Per-renderer output (instancing).**
4. **Extras:**
   - painted thickness;
   - self-collision capsules;
   - density tuning;
   - possibly a Vertex Block Descent solver, if XPBD isn't stable enough at the stiffness we want.

## Phase 1 status (2026-11-10): done, offline

- **Code:** in `Core/Flesh/` (engine-free; it also compiles into the plugin, with no data-model change and the same hash):
  - `FleshTypes` (input and settings), `SimSurface`, `TetShell`, `FleshEmbedding`, `XpbdSolver`, `FleshRig`;
  - `BlendshapeDeltas.GetShape` was added to `Skinning.cs`.
- **Test model:**
  - Lenki's Kitavali FBX, converted by `tools/fbx_to_sbm.py` (stdlib-only binary FBX reader `tools/fbxbin.py`) to `tests/data/kitavali_body.sbm`. It's local test data only.
  - The torso is painted by bone weight (Hips/Spine/Chest).
- **Commands:**
  - `dotnet run -c Release --project tests -- flesh` runs only these tests;
  - `-- flesh-export <file>` plus `tools/flesh_preview.py` produce the pictures in `Testresolink/renders/flesh_*.png`.
  - While the add-ons are parked, add `-p:RendererBepInEx=<Testresolink>\parked_addons\Renderer\BepInEx\core\`.
- **Results** (defaults: 800 soft points, 1 layer, 8 substeps):

  | What | Result |
  |---|---|
  | Region | 5,951 soft vertices → 800 points plus 192 pinned |
  | Tets | 3,570 (6 dropped) |
  | Build time | about 170 ms |
  | Step time | 2.1 ms per frame, single-threaded (400 points: 0.9 ms; 1,500 points: 4.0 ms) |
  | At rest | stays put (0.003 mm) |
  | Gravity | sags 11 mm and settles |
  | Fast 25° spine/chest bend | lags 5 mm, then returns to the bent shape |
  | 8 cm jump | jiggles up to 25 mm, keeps volume within 0.3 % |
  | Bind-space round trip | 1.1 % error |

  All 234 suite checks pass.
- **Changes from the plan above:**
  - The tet constraints are offset to be zero at rest (`|F| − √3`, `det F − 1`) rather than Macklin & Müller's γ form, so a resting body doesn't alternate big corrections.
  - The shell depth uses only the inward part of the way to the bone, with a thin floor. Flaps and fluff whose bone lies beside them get a thin membrane, not a sideways push.
  - New `BlendshapeInertia` setting (default 0): blendshape changes carry the flesh without adding velocity. Without it, HideChestFluff (8 cm) flung the fluff 10 cm.
- **Next (phase 2):**
  - the `SkinnedSoftbody` component on the current GPU path, with the step run off the main thread (one frame of latency);
  - debug views of the tets;
  - paint from PaintLayer;
  - then the in-game test.

## Phase 2 status (2026-11-10): built, tested offline, not yet in game

- **Component:** `SkinnedSoftbody` (`Plugin/SkinnedSoftbody.cs`) plus `FleshInstance` (`Plugin/FleshInstance.cs`), plugin 0.11.0.
  - The data model changed: compatibility hash `ac1XUmq1SPmxIl/KxjAspg==`, 9 types.
  - All 248 suite checks pass, including the new component's fields, overrides, weaving and registration.
- **GPU path only:** it refuses to start without the Softbody renderer (fork or patch) and never replaces the avatar's mesh.
- **Threads:** the build and the steps run on the thread pool. Steps have one frame of latency: collected at the start of the next frame, then uploaded.
- **Paint and brush:**
  - the same dab type and brush (the brush now paints any `IPaintable`);
  - `StartAs AllFirm` is the default;
  - rebuilds happen 0.3 s after the paint stops changing, on a worker, while the old rig keeps running.
- **Colliders:** the ground (lowered under the animated skin) plus Box/Sphere colliders as XPBD push-outs. A box under the belly was tested offline.
- **Debug view:** `DebugTets` and `DebugColliders` reuse `SoftbodyDebugMesh` (`SubmitFlesh`, Core `FleshDebugView`).
- **Normals:** each moved vertex is turned by its own side's triangles (`FleshNormals`), so cards and fins keep both faces.
  - 0.26.3: the turn is measured on the blendshaped shape (rest positions plus the active blendshapes' deltas, for the
    vertices around the moving ones), and applied to the normal and tangent the renderer would show there (own value plus
    blendshape deltas); the output takes those deltas off again, since the renderer adds them. Tangents turn with the
    normals (same rotation, w kept) and are uploaded with them; the contact shading turns both on the shown frame
    (`TurnToward`), and the layer merge carries tangents like normals.
  - Not built: the renderer blends blendshape normals in a straight line (rest plus weight times delta), which is far
    off part way through a shape that turns normals a lot (Lenki's "Cast": 21.9 degrees on average at weight 0.5).
    Turning the moving region's rest normals by the real shape's face turn would fix it there (offline: 1.4 to 3.5
    degrees on average, with outliers at folds), but it changes the region's look at rest and could show a seam where
    the region ends.
- **Found offline while building this:**
  - the embedding's facing filter was too strict, which folded small detail triangles; it now rejects only clearly opposite points;
  - hard paint edges strain the shell; softness now eases in over 3 rings from the pinned border;
  - a few vertices in a deep groin crease still turn hard during a 3 g hop (33 of 6,088). Watch for this in game.
- **Not done yet:**
  - the in-game test (needs the 0.11.0 install OK, Resonite closed, and the launch options);
  - per-renderer output (phase 3);
  - self-collision capsules;
  - painted thickness.

## 0.11.1 (2026-11-10): colliders

Lenki asked to drop the ground check and collide only with what is solid.
- **World colliders:** `CharacterCollider` on, Static or Active, not the avatar's own.
- **Users' colliders:** the engine's dynamic-bone player spheres (`World.DynamicBones`, private `colliders` array read through
  compiled accessors). The own user contributes only its hands.
- **Shapes:** every collider type is exact (`Core/Flesh/XpbdCollision.cs`). Mesh colliders use `TriangleBvh`, cached per mesh
  data (`MeshBvhCache`).
- **Culling:** per point, with bounding boxes (grid over points, tree per point, bounds and plane checks per substep). It is
  verified bit-identical against pairing everything.
- **Cost:** a usual scene adds 0.02 ms per frame; the worst case tested adds about 2 ms on the worker.
- **Version:** same data model and hash as 0.11.0.

## 0.11.2 (2026-11-10): avatar bone colliders and self-collision

- **Where they come from:** VRIKAvatar.GenerateOptimizedColliders puts colliders on every major bone. Each sits on a child slot of its bone, with type Static and CharacterCollider off:
  - capsules from `SetupCapsuleBone`;
  - a head sphere;
  - hand boxes (`SetupTerminalBoxColliderBone`) and foot boxes.

  Their Enabled is driven through `_rigCollidersEnabledStates`. The engine switches between them and one simplified whole-body capsule, depending on whether users are close.
- **Other users:** a collider counts when it is in that list (an exact match). The dynamic-bone spheres are used only for users without one.
- **Own avatar (Lenki's idea):** "ignore what touches at build". Each own bone collider is taken into bind space (slot relative to its bone, then the inverse bind pose) and tested against the shell rest (`FleshRig.TouchingAtRest`). The result is stored as a bit mask per collider (`FleshCollider.Ignore`), skipped at pair building, and recomputed on each tet build.
- **Offline on Kitavali:** without the masks the own capsules push the resting torso out by 62 mm; with them 0.003 mm. An arm swung down presses the side in, and only the arm does.
- **Version:** same hash as 0.11.0.

## 0.26.4 (2026-11-12): letting go of colliders on the wrong side

- **Lenki's report:** skin points got stuck on the mostly flat boxes Resonite's colliders are made of, pulling the softbody
  away from the mesh, and only let go when it moved at very high speed.
- **Cause:** a collider could push a skin point into the body only to the depth limit, but nothing limited pushing it out
  of the body or far along the skin. A thin box is a wall from both sides. Once the body's animated place has passed
  through it, or a box has gone through the skin past the depth limit and is pulled back out, the points stay on the old
  side. They are pushed back there every substep and only get through when one substep's jump is bigger than a radius.
- **Rule (`XpbdSolver.KeepInBody`):** contacts pushing within 60 degrees of the skin's inward direction keep the dent rules.
  For the others:
  - out of the body: a contact may leave a point at most `OutwardSlack` (0.5) radii out beyond its animated place. Past
    that it loses its outward part, and also its part along the skin when it pushed mostly outward;
  - across: when the point's animated place is more than a radius behind the surface it was pushed out of (tested where
    the collider ends the frame) and outside the collider (`Across`: the collider is between them), the contact does
    nothing. Inside a solid it stays an ordinary contact, however deep: a pressed ball pushes the skin around its rim
    sideways. A plane, and a mesh's front-only face, count as the inside of a solid: such a face already lets through a
    point more than two radii behind it, and a bumpy mesh's own face planes say nothing about the solid. A two-sided
    triangle is a sheet, so it is between them when the animated place is straight behind it.
  - Two earlier tries were dropped. Letting go of everything more than a radius behind the surface cut the rim of a 3 cm
    ball dent. Also letting go deeper inside than the depth limit still changed that dent's shading, and none of the
    stuck cases needed it.
  A point let go is not counted or shaded as a contact. `LetGo` counts them, and the stats line shows it.
- **Offline (`tests/FleshStuckTests.cs`, Kitavali):** 1 cm and 4 cm boards walked through and back, a 2 cm seat sat
  through, a box pressed 6 cm in and pulled out, a 4 cm capsule swept through the belly.
  - 0.26.3 held the skin out by 41 to 106 mm, and 7 to 14 points stayed 7.6 to 14.8 cm off the mesh.
  - 0.26.4 holds it out by 8 to 15 mm, and everything is within 2 mm a second later.
  - A box dent and a sliding ball are bit-identical to 0.26.3; the full flesh suite was compared with the rule off.
- **Left:** a point pushed out through a board's face from inside gets that jump as velocity and can overshoot by up to
  2.5 cm for a moment. 0.26.3 does the same.

## 0.26.5 (2026-11-12): a saved layer keeps its hue

- **Report (Lenki's friend):** an avatar saved with its second softbody layer set to orange loaded as yellow.
- **Cause:** `HueLayer.OnAwake` picked the first hue the other layers did not use. OnAwake runs on every client that
  builds the layer, before its saved or synced values arrive, so the engine expects it to set the same values everywhere.
  Read from FrooxEngine's IL:
  - `SyncElementList.InternalInsert` creates the element, `Initialize` runs `OnAwake`, and only then is it put in the list;
  - `InternalLoad` inserts each saved element and loads its values right after, so a loading client saw the earlier
    layers' real hues;
  - `InternalDecodeFull` (a client receiving the avatar) inserts all elements first; their values arrive afterwards, so
    there the pick saw the earlier layers still orange and moved on to yellow;
  - `InternalCopy` adds each copy and copies its values right after.
- **Fix:** `HueLayer.OnAwake` sets constants only (orange). The free-hue pick moved to `SkinnedSoftbody.LayersAdded`
  (`HueLayers.ElementsAdded`). It acts only once the component has started on this client (loads, copies and the first
  sync all happen before `OnStart`), and only for layers this client's user created (`RefID.User == LocalUser.AllocationID`).
  The pick is then an ordinary synced change.
- **Tests:** `PluginCheck.LayerHueChecks` checks the built DLL: OnAwake reads no other layer, parent or value; the
  component subscribes in OnAwake; the pick checks the user and the started flag, and catches its exceptions. The
  engine paths can't run offline (a SyncList needs a World), so the load and sync behaviour is to be confirmed in game.

## 0.26.6 (2026-11-12): no flying skin after sudden moves and hits

- **Report (Lenki, in game on 0.26.5):** after sudden movements or collider hits, a few vertices fly around for about a
  quarter of a second.
- **Logs (the stats line's max offset):**
  - 0.26.5 session, 13:23 to 13:32: 6 of 62 samples over 10 cm, worst 0.88 m, with up to 53 inverted tets. Only the
    avatar's own bone colliders and one world collider were near.
  - 0.26.2, the night before: 35 % of the 'Body' samples over 10 cm, worst 4.3 m. So this is not new.
- **Causes, offline** (`tests/FleshJitterTests.cs`: Kitavali, its own bone capsules, gravity on):
  - the velocity update turns a contact's position correction into speed, so a point pushed out of a collider flew off;
  - a sudden whole-body move (a 0.5 m locomotion snap, under `TeleportDistance`) left the flesh behind, and the own
    bone capsules swept the whole jump through it. Result: 190 mm off, 17 m/s, 204 inverted tets, 33 frames with a point
    over 10 cm, and one point left stuck 67 mm off between the Hips and a leg capsule.
- **Fix: three rules, all in Core:**
  - `XpbdSolver.NoBounce`: a point never leaves a contact faster along its push than the collider moves along it,
    unless it was that fast without the contact's displacement. Two earlier tries were dropped. Removing all separation
    relative to the collider dragged skin out after a retreating hand. Giving every pushed point the collider's speed
    drove depth-limited points deeper.
  - `FleshRig.MaxInertialSpeed` (5 m/s at the modelled size, scaled with the avatar): when a point's animated place
    moves faster, the flesh is carried along, as blendshape changes are. The own bone colliders' sweep is capped the
    same way; swept through the whole jump, they plowed through the carried flesh.
  - `XpbdSolver.OwnNoPlow`: the avatar's own bone colliders press the flesh in, but push it along or out of the body
    only where they reach the skin's animated place. A first try, own colliders press only, lost the dent of an arm
    pressed against the side.
- **Results, 0.26.3 → 0.26.6:**

  | event | worst offset | fastest point | other |
  |---|---|---|---|
  | locomotion snap 0.5 m | 190 → 58 mm | 17 → 6.5 m/s | inverted tets 204 → 16; frames over 10 cm 33 → 0; settles (was stuck at 67 mm) |
  | arm slammed against the body | 36 → 25 mm | 3.3 → 2.0 m/s | |
  | spine bent 30° in a frame | 49 → 51 mm | 5.4 → 4.9 m/s | inverted tets 6 → 2 |
  | hand hitting at 4 m/s | 28 → 34 mm | 2.5 → 1.6 m/s | the skin follows the hand in |
  | snap turn 45° | unchanged | unchanged | |

  The flesh, box, squish, stuck and self-collision tests all pass. Their numbers move by about a millimetre, and an arm
  pressed against the side still dents (10.3 mm, was 10.2). The culling test turns NoBounce off, as it does Squish,
  friction and the let-go, because NoBounce lowers the speeds the reach is estimated from.
- **Diagnostics:** `XpbdSolver.Unbounced` and `FleshRig.Carried`, per step.

## Cost guess (before any code)

- Fast Projective Skinning's notes suggest 3–5k simulated vertices for a whole body (on the GPU). We only simulate the painted region.
- Estimated case: 1–2k sim vertices, 1 layer, 6–12k tets, 6–8 substeps.
- My guess is about 1 ms single-threaded C#. This is unmeasured.
- It can be parallelised by graph colouring.

## References

- [Chaos Flesh quickstart](https://dev.epicgames.com/documentation/unreal-engine/chaos-flesh-quickstart): kinematic (infinite mass) and weak (stiffness, slip) constraints on tet vertices, bound to joints.
- [GenerateSurfaceBindings](https://dev.epicgames.com/documentation/unreal-engine/API/Plugins/ChaosFleshNodes/FGenerateSurfaceBindings): barycentric render-surface bindings used by the FleshDeformer. Unbound render points just skin. Bindings regenerate when the surface or tet topology changes.
- Komaritzan & Botsch, [Fast Projective Skinning](https://cg.cs.tu-dortmund.de/publications/2019-skinning.pdf) (MIG 2019) and [Projective Skinning](https://cg.cs.tu-dortmund.de/publications/2018-skinning.pdf) (2018): rigid bones plus an elastic soft-tissue layer computed from the surface, with upsampling to the render mesh.
- Zhang et al., [Complementary Dynamics](https://arxiv.org/pdf/2009.02462) (2020): physics adds only what the rig doesn't do.
- Macklin et al., XPBD (2016) and "Small Steps in Physics Simulation" (2019).
- Macklin & Müller, "A Constraint-based Formulation of Stable Neo-Hookean Materials" (2021).
- Chen et al., "Vertex Block Descent" (2024).
