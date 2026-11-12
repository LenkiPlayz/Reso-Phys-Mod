# The renderer patch

Resonite draws with Renderite ([Yellow-Dog-Man/Renderite](https://github.com/Yellow-Dog-Man/Renderite), MIT). The GPU liquid
and smoke run inside the renderer, and the softbody's GPU path needs one change to how meshes upload, so the mod ships a patched
`Renderite.Unity.dll`. These are the changes:

- `Assets/Mesh/MeshAsset.cs`: vertex-only updates of skinned meshes are applied in place.
- `Assets/AssetManager.cs`: a `TryGetAsset` lookup.
- `RenderingManager.cs`: runs the GPU liquid and smoke each frame.
- `Renderite.Unity.csproj`: links the GPU liquid's sources from `Softbody/GpuFluid`.

`softbody-renderite.diff` is against Renderite commit `64bd84d14c68faaddd7347f606b6b830eaae85a7`. `files/` holds the four
changed files whole. Renderite's license is in `LICENSE-Renderite.txt`.

## Building the patched renderer

1. Clone Renderite into the repo's root folder as `RenderiteSrc` (it is ignored by git), next to `Softbody`. Check out the
   commit your installed Resonite renderer was built from. `make_softbody_renderer.py` prints it, and it must match exactly.
2. Apply the patch: `git -C RenderiteSrc apply ../renderite-patch/softbody-renderite.diff`, or copy `files/` over it.
3. Set `GAME` in `make_softbody_renderer.py` to your Resonite folder, then run `python make_softbody_renderer.py`. It builds
   Renderite.Unity, copies your Renderer folder to `RendererSoftbody` and swaps in the patched DLL.
4. Start Resonite with `-RendererPath "<that folder>\RendererSoftbody\Renderite.Renderer.exe"`.

Run it again after every Resonite update: the renderer has to match the game's version.
