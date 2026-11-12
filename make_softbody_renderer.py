"""Builds the Softbody renderer fork: a copy of Resonite's own Unity renderer with a patched Renderite.Unity.dll.

  python make_softbody_renderer.py           # check, copy and swap (Resonite should be closed while you test it)

What it does:
  1. Reads which Renderite commit the installed renderer was built from (Renderite.Unity.dll's informational version).
  2. Checks that RenderiteSrc (github.com/Yellow-Dog-Man/Renderite, MIT) is at that commit and carries the Softbody patch
     (MeshAsset.cs: vertex-only updates of skinned meshes are applied in place; 0.16.0: the GPU fluid, whose sources are
     linked from Softbody/GpuFluid), then builds Renderite.Unity.
  3. Copies Resonite's Renderer folder to RendererSoftbody (without BepInEx / Doorstop files) and swaps in the patched DLL.

Then start Resonite with:  -RendererPath "<this folder>\\RendererSoftbody\\Renderite.Renderer.exe"
After every Resonite update, run this again (the renderer must match the engine's version exactly).
"""
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = r"F:\SteamLibrary\steamapps\common\Resonite"
OFFICIAL = os.path.join(GAME, "Renderer")
SRC = os.path.join(HERE, "RenderiteSrc")
OUT = os.path.join(HERE, "RendererSoftbody")
MANAGED = os.path.join("Renderite.Renderer_Data", "Managed", "Renderite.Unity.dll")
SKIP = {"BepInEx", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "uDesktopDuplication.log"}
MARKER = "Softbody fork: a vertex-streams-only update"
FLUID_MARKER = "Softbody.FluidGpu.SoftbodyFluidHost.Update();"   # 0.16.0: the GPU fluid
# 0.27.0: the smoke shader (SoftbodyShaders\build.ps1 builds it); SoftbodyFluidHost loads it from StreamingAssets\Softbody
SMOKE_BUNDLE = os.path.join(HERE, "Softbody", "GpuFluid", "Shaders", "softbody_smoke.bundle")
SMOKE_DEST = os.path.join("Renderite.Renderer_Data", "StreamingAssets", "Softbody", "softbody_smoke.bundle")


def commit_of(dll):
    data = open(dll, "rb").read()
    m = re.search(rb"1\.0\.0\+([0-9a-f]{40})", data)
    return m.group(1).decode() if m else None


def main():
    official_dll = os.path.join(OFFICIAL, MANAGED)
    want = commit_of(official_dll)
    if not want:
        sys.exit("could not read the installed renderer's Renderite commit")
    have = subprocess.run(["git", "-C", SRC, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    print(f"installed renderer built from Renderite {want[:10]}; RenderiteSrc is at {have[:10]}")
    if have != want:
        sys.exit("RenderiteSrc is at a different commit: check out the installed one and re-apply the Softbody patch first")
    mesh_asset = os.path.join(SRC, "Renderite.Unity", "Assets", "Mesh", "MeshAsset.cs")
    if MARKER not in open(mesh_asset, encoding="utf-8").read():
        sys.exit("MeshAsset.cs does not carry the Softbody patch")
    manager = os.path.join(SRC, "Renderite.Unity", "RenderingManager.cs")
    if FLUID_MARKER not in open(manager, encoding="utf-8-sig").read():
        sys.exit("RenderingManager.cs does not carry the Softbody GPU fluid hook")

    r = subprocess.run(["dotnet", "build", os.path.join(SRC, "Renderite.Unity", "Renderite.Unity.csproj"), "-c", "Release",
                        "-p:UnityVersion=2019.4.31f1", "-nologo", "-v", "q"], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-3000:])
        sys.exit("building Renderite.Unity failed")
    built = os.path.join(SRC, "Renderite.Unity", "bin", "Release", "netstandard2.0", "Renderite.Unity.dll")
    if commit_of(built) != want:
        sys.exit("the built DLL carries a different commit stamp")

    if os.path.exists(OUT):
        shutil.rmtree(OUT)
    shutil.copytree(OFFICIAL, OUT, ignore=lambda d, names: [n for n in names if d == OFFICIAL and n in SKIP])
    shutil.copy2(built, os.path.join(OUT, MANAGED))
    if os.path.exists(SMOKE_BUNDLE):
        os.makedirs(os.path.dirname(os.path.join(OUT, SMOKE_DEST)), exist_ok=True)
        shutil.copy2(SMOKE_BUNDLE, os.path.join(OUT, SMOKE_DEST))
        print("smoke shader:", os.path.join(OUT, SMOKE_DEST))
    else:
        print("no smoke shader bundle (run SoftbodyShaders\\build.ps1 first): the smoke keeps the Unlit discs")
    print("renderer copy:", OUT)
    print('launch option:  -RendererPath "%s"' % os.path.join(OUT, "Renderite.Renderer.exe"))


if __name__ == "__main__":
    main()
