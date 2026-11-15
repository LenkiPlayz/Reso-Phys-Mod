# The live bounce light (GI) in the Unity 2019.4.31f1 editor (its window shows: batch mode is not licensed on this PC). Nothing here
# touches the smoke build (build.ps1): the GI code lives in Assets\SoftbodyGI with its own assemblies, so a GI compile error cannot
# stop the smoke's.
#   1. copies the GI sources in (Softbody\GpuGI and the fluid's HlslCompiler, without their "#nullable" lines: the editor's C# is
#      7.3), and the game renderer's SharpDX DLLs into Assets\Plugins (read only from the game);
#   2. builds the voxelize shader's bundle, Softbody\GpuGI\Shaders\softbody_gi.bundle, and rewrites its D3D11 programs for the
#      game's renderer (fix_d3d11_programs.py: the game is Unity 2019.4.19);
#   3. checks it with the fork's own self-test (a test square drawn into voxels and read back);
#   4. with -Scene: the Cornell box test (the game's own PBS shader; before and after; both modes; timings; the ambient check),
#      PNGs in Softbody\GpuGI\tests\scene.
#   5. with -Probe: which of the voxel camera's calls wait for Unity's render thread (GiSyncProbe; the game's slab draw waited
#      ~5.5 ms a frame in GI test 1).
#   6. with -Pacing: builds and runs a small standalone player (GiPacingProbe) that times the voxel camera's draw at different
#      places in Unity's frame, with vsync, and the loading time a Resonite-like frame gets with the draw in Update (GI test 1) and at
#      the end of the frame (GI test 2) (its window shows for about 45 s); results in PacingProbeBuild\pacing.txt.
#   7. with -Leak: a thin wall lit only by a ramp written into the volume (GiShaderBuild.Leak): its lookup offset (GiLeak) must move
#      what the game's own PBS shader reads, and turn with the head.
#   powershell -ExecutionPolicy Bypass -File build_gi.ps1 [-Scene] [-Leak] [-Probe] [-Pacing] [-NoBuild] [-NoCheck]
# Run it only with Resonite closed (it uses the GPU).
param([switch]$Scene, [switch]$Leak, [switch]$Probe, [switch]$Pacing, [switch]$NoBuild, [switch]$NoCheck,
      [string]$GameShader = 'F:\SteamLibrary\steamapps\common\Resonite\RuntimeData\PreCache\0DGWptA8LQWdUJb3jrav9Q',
      [int]$TimeoutSeconds = 600)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.31f1\Editor\Unity.exe'
$gi = (Resolve-Path (Join-Path $here '..\Softbody\GpuGI')).Path
$fluid = (Resolve-Path (Join-Path $here '..\Softbody\GpuFluid')).Path
$managed = 'F:\SteamLibrary\steamapps\common\Resonite\Renderer\Renderite.Renderer_Data\Managed'
$shaders = Join-Path $gi 'Shaders'
$bundle = Join-Path $shaders 'softbody_gi.bundle'
$hlsl = Join-Path $gi 'Hlsl\SoftbodyGI.hlsl'
$sceneOut = Join-Path $gi 'tests\scene'

$runtime = Join-Path $here 'Assets\SoftbodyGI\Runtime'
$shaderDir = Join-Path $here 'Assets\SoftbodyGI\Shaders'
$plugins = Join-Path $here 'Assets\Plugins'
New-Item -ItemType Directory -Force $runtime, $shaderDir, $plugins, $sceneOut | Out-Null

# 1. sources (C# 7.3: no #nullable), the shader, SharpDX
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($src in @("$gi\Shared\GiTypes.cs", "$gi\D3D\GiCompute.cs", "$gi\Unity\GiUnity.cs", "$fluid\D3D\HlslCompiler.cs")) {
    $lines = [System.IO.File]::ReadAllLines($src) | Where-Object { $_ -notmatch '^\s*#nullable' }
    [System.IO.File]::WriteAllLines((Join-Path $runtime (Split-Path -Leaf $src)), [string[]]$lines, $utf8)
}
Copy-Item "$shaders\SoftbodyGIVoxelize.shader" (Join-Path $shaderDir 'SoftbodyGIVoxelize.shader') -Force
foreach ($dll in @('SharpDX.dll', 'SharpDX.DXGI.dll', 'SharpDX.Direct3D11.dll')) {
    $dest = Join-Path $plugins $dll
    if (-not (Test-Path $dest)) { Copy-Item (Join-Path $managed $dll) $dest }
}

# runs one -executeMethod in the windowed editor; prints the lines that matter; returns the log's lines
function Invoke-Unity([string]$method, [string[]]$more, [string]$logName, [string]$marker) {
    $log = Join-Path $here $logName
    $uargs = @('-quit', '-projectPath', "`"$here`"", '-executeMethod', $method, '-logFile', "`"$log`"") + $more
    Remove-Item $log -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $unity -ArgumentList $uargs -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "Unity did not finish in $TimeoutSeconds s (stopped)" }
    $lines = Get-Content $log
    $lines | Select-String -Pattern 'SOFTBODY_GI|error CS|Shader error|^\s*Assets/.*error' | ForEach-Object { Write-Host $_.Line }
    if (-not ($lines | Select-String -SimpleMatch $marker)) { throw "Unity exited $($p.ExitCode) without '$marker' (see $log)" }
    return $lines
}

# 2. the bundle, fixed for the game's renderer
if (-not $NoBuild) {
    Invoke-Unity 'GiShaderBuild.Build' @('-giOut', "`"$shaders`"") 'gi_build.log' 'SOFTBODY_GI_BUILD OK' | Out-Null
    & python -I (Join-Path $here 'fix_d3d11_programs.py') $bundle
    if ($LASTEXITCODE -ne 0) { throw "fix_d3d11_programs.py failed ($LASTEXITCODE): the bundle is as Unity built it, which the game's renderer can't use" }
}

# 3. the fork's self-test with it
if (-not $NoCheck) {
    $lines = Invoke-Unity 'GiShaderBuild.Check' @('-giBundle', "`"$bundle`"", '-giHlsl', "`"$hlsl`"") 'gi_check.log' 'SOFTBODY_GI_CHECK DONE'
    if (-not ($lines | Select-String -SimpleMatch 'SOFTBODY_GI_CHECK PASS')) { throw 'the bundle failed the self-test (see gi_check.log): do not install it' }
}

# 4. the scene
if ($Scene) {
    Invoke-Unity 'GiShaderBuild.Scene' @('-giBundle', "`"$bundle`"", '-giHlsl', "`"$hlsl`"", '-giGameShader', "`"$GameShader`"", '-giOut', "`"$sceneOut`"") 'gi_scene.log' 'SOFTBODY_GI_SCENE DONE' | Out-Null
}

# 7. the lookup offset through the game's shader
if ($Leak) {
    $lines = Invoke-Unity 'GiShaderBuild.Leak' @('-giBundle', "`"$bundle`"", '-giHlsl', "`"$hlsl`"", '-giGameShader', "`"$GameShader`"") 'gi_leak.log' 'SOFTBODY_GI_LEAK DONE'
    if (-not ($lines | Select-String -SimpleMatch 'SOFTBODY_GI_LEAK PASS')) { throw 'the lookup offset does not move what the game shader reads (see gi_leak.log)' }
}

# 5. the render-thread waits
if ($Probe) {
    Invoke-Unity 'GiSyncProbe.Run' @('-giBundle', "`"$bundle`"", '-giHlsl', "`"$hlsl`"") 'gi_probe.log' 'SOFTBODY_GI_SYNC DONE' | Out-Null
}

# 6. where in the frame the draw waits (a standalone player)
if ($Pacing) {
    $pacingDir = Join-Path $here 'PacingProbeBuild'
    $pacingExe = Join-Path $pacingDir 'PacingProbe.exe'
    Invoke-Unity 'GiPacingBuild.Build' @('-giPacingOut', "`"$pacingExe`"") 'gi_pacing_build.log' 'SOFTBODY_GI_PACING_BUILD OK' | Out-Null
    $result = Join-Path $pacingDir 'pacing.txt'
    Remove-Item $result -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $pacingExe -ArgumentList @('-logFile', "`"$(Join-Path $pacingDir 'player.log')`"") -PassThru
    if (-not $p.WaitForExit(120 * 1000)) { Stop-Process -Id $p.Id -Force; throw 'the pacing player did not finish in 120 s (stopped)' }
    if (-not (Test-Path $result)) { throw "the pacing player wrote no $result (see player.log)" }
    Get-Content $result | ForEach-Object { Write-Host "SOFTBODY_GI_PACING $_" }
}
