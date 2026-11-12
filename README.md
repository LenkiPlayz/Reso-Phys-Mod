# Reso-Phys-Mod

Physics for [Resonite](https://resonite.com): soft bodies, flesh on avatars, a particle liquid and particle smoke. It is a
Resonite plugin (`Softbody.dll`), a patched copy of Resonite's renderer for the GPU liquid and smoke, and a small manager that
installs and updates both.

**Downloads** are on the [Releases](https://github.com/LenkiPlayz/Reso-Phys-Mod/releases) page. Take the manager
(`SoftbodyManager.exe`). It installs the plugin and the renderer, switches the plugin off for public sessions, and updates
everything from here.

Everyone in a session needs the same version: a plugin changes the session's compatibility hash.

## What's here

| Folder | What it is |
|---|---|
| `Softbody/` | The plugin: `Softbody` and `SkinnedSoftbody` (soft meshes and avatar flesh), `SoftbodyBrush` (paint the softness), `FluidEmitter` (liquid, CPU and GPU), `GasEmitter` (smoke, GPU), and the offline tests. Its `README.md` has every version's notes, and the `DESIGN-*.md` files explain how each part works. |
| `Softbody/GpuFluid/` | The GPU liquid and smoke (Direct3D 11 compute), built into the patched renderer. |
| `SoftbodyManager/` | The installer and updater (Windows), and the release tools. |
| `SoftbodyShaders/` | The Unity project that builds the smoke shader. |
| `renderite-patch/`, `make_softbody_renderer.py` | The changes to Resonite's renderer, and the script that builds the patched copy. |

## Building

- The plugin needs the .NET 10 SDK and a Resonite install. It builds against the game's own DLLs, which aren't in this repo:
  `dotnet build Softbody/Softbody.csproj -c Release -p:ResonitePath="C:\path\to\Resonite\"`
- The tests, after the plugin: `dotnet build Softbody/tests/Softbody.Tests.csproj -c Release -p:BuildProjectReferences=false`,
  then `Softbody/tests/bin/Release/net10.0/Softbody.Tests.exe <mode>`. The modes are in `Softbody/tests/Program.cs`. Without
  that flag the build also makes `Softbody/Renderer`, the older BepInEx renderer plugin, which needs BepInEx 5. The GPU tests
  need a Direct3D 11 GPU. The flesh tests need a skinned avatar body in `Softbody/tests/data/`, which isn't included.
- The renderer: see [renderite-patch/README.md](renderite-patch/README.md).

## License

MIT, see [LICENSE](LICENSE). The renderer patch changes Yellow Dog Man Studios' Renderite, which is also MIT
([renderite-patch/LICENSE-Renderite.txt](renderite-patch/LICENSE-Renderite.txt)).
