# The renderer patch

Resonite draws with Renderite ([Yellow-Dog-Man/Renderite](https://github.com/Yellow-Dog-Man/Renderite), MIT). The GPU liquid
and smoke run inside the renderer, and the softbody's and the avatar cloth's GPU paths need changes to how meshes upload, so
the mod ships a patched `Renderite.Unity.dll`. These are the changes:

- `Assets/Mesh/MeshAsset.cs`: vertex-only updates of skinned meshes are applied in place. Since Softbody 0.30.0 (v2) their
  triangles, UV channels and vertex colours are too, so a tear in avatar cloth doesn't rebuild the mesh.
- `Assets/AssetManager.cs`: a `TryGetAsset` lookup.
- `RenderingManager.cs`: runs the GPU liquid and smoke each frame.
- `Renderite.Unity.csproj`: links the GPU liquid's sources from `Softbody/GpuFluid`.
- The GI build only: the live bounce light, a test. It is built with `-c ReleaseGI` and sits under `#if SOFTBODY_GI`, so a
  plain build leaves all of it out:
  - `Renderite.Unity.csproj` links its sources from `Softbody/GpuGI`;
  - `RenderingManager.cs` runs it each frame;
  - `RenderableManagers/MeshRendererManager.cs` and `Renderables/Renderable.cs` give it each renderer's property block;
  - `Rendering/CameraPortal.cs` and `Rendering/GaussianSplats/GaussianSplatRendererManager.cs` skip its voxel camera.

`softbody-renderite.diff` is against Renderite commit `64bd84d14c68faaddd7347f606b6b830eaae85a7`. `files/` holds the eight
changed files whole. Renderite's license is in `LICENSE-Renderite.txt`.

## Building the patched renderer

1. Clone Renderite into the repo's root folder as `RenderiteSrc` (it is ignored by git), next to `Softbody`. Check out the
   commit your installed Resonite renderer was built from. `make_softbody_renderer.py` prints it, and it must match exactly.
2. Apply the patch: `git -C RenderiteSrc apply ../renderite-patch/softbody-renderite.diff`, or copy `files/` over it.
3. The shader bundles aren't in this repo. Build them with Unity 2019.4.31f1: `SoftbodyShaders/build.ps1` makes the smoke's,
   and `SoftbodyShaders/build_gi.ps1` makes the bounce light's (only needed for the GI build). Without the smoke's bundle,
   the smoke and foam are drawn as the plugin's plain unlit discs.
4. Set `GAME` in `make_softbody_renderer.py` to your Resonite folder, then run `python make_softbody_renderer.py` (add `--gi`
   for the GI build). It builds Renderite.Unity, copies your Renderer folder to `RendererSoftbody`, swaps in the patched DLL,
   and puts the shader bundles (and, for the GI build, `gi.ini`) in its `StreamingAssets\Softbody`.
5. Start Resonite with `-RendererPath "<that folder>\RendererSoftbody\Renderite.Renderer.exe"`.

Run it again after every Resonite update: the renderer has to match the game's version.
