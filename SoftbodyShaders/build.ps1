# Builds the renderer fork's smoke shader bundle, Softbody\GpuFluid\Shaders\softbody_smoke.bundle, with Unity 2019.4.31f1.
# Unity shows its window for about half a minute: batch mode is not licensed on this PC (the 2019.4 editor cannot use the Hub's
# newer licence service headless), but the windowed editor uses the signed-in Unity Hub.
# 0.29.2: after a build it rewrites the bundle's D3D11 programs for the game's renderer (Unity 2019.4.19 does not know the header
# 2019.4.31 writes: fix_d3d11_programs.py). Then it starts Unity again to draw a test puff with the result: SoftbodyShaderBuild.Check,
# with the fork's own SmokeSelfTest. -NoCheck skips that check.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1                       # build the bundle, fix it, check it
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Check "a.bundle;b.bundle"   # only the check, on these bundles
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Preview frame.bin -Png out.png [-Material Assets/Softbody/SoftbodyFoam.mat]
#   powershell -ExecutionPolicy Bypass -File build.ps1 -PreviewDir dir      # build (and fix, check), then render every dir\*.bin to dir\*.png
param([string]$Preview = '', [string]$Png = '', [string]$Material = '', [string]$PreviewDir = '', [string]$Check = '', [switch]$NoCheck,
      [int]$TimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.31f1\Editor\Unity.exe'
$shaders = Resolve-Path (Join-Path $here '..\Softbody\GpuFluid\Shaders')
$bundle = Join-Path $shaders.Path 'softbody_smoke.bundle'
New-Item -ItemType Directory -Force (Join-Path $here 'Assets\Softbody'), (Join-Path $here 'Packages'), (Join-Path $here 'ProjectSettings') | Out-Null
Copy-Item (Join-Path $shaders 'SoftbodySmoke.shader') (Join-Path $here 'Assets\Softbody\SoftbodySmoke.shader') -Force
Copy-Item (Join-Path $shaders '..\Unity\SmokeSelfTest.cs') (Join-Path $here 'Assets\Softbody\SmokeSelfTest.cs') -Force

# runs one -executeMethod in the windowed editor; prints the lines that matter; returns the log's lines
function Invoke-Unity([string]$method, [string[]]$more, [string]$logName, [string]$marker) {
    $log = Join-Path $here $logName
    $uargs = @('-quit', '-projectPath', "`"$here`"", '-executeMethod', $method, '-logFile', "`"$log`"") + $more
    Remove-Item $log -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath $unity -ArgumentList $uargs -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "Unity did not finish in $TimeoutSeconds s (stopped)" }
    $lines = Get-Content $log
    $lines | Select-String -Pattern 'SOFTBODY_|error CS|Shader error|^\s*Assets/.*error' | ForEach-Object { Write-Host $_.Line }
    if (-not ($lines | Select-String -SimpleMatch $marker)) { throw "Unity exited $($p.ExitCode) without '$marker' (see $log)" }
    return $lines
}

# the number of bundles that failed the check
function Test-Bundles([string]$list) {
    $lines = Invoke-Unity 'SoftbodyShaderBuild.Check' @('-softbodyCheck', "`"$list`"") 'check.log' 'SOFTBODY_CHECK DONE'
    return @($lines | Select-String -SimpleMatch 'SOFTBODY_CHECK FAIL ').Count
}

if ($Preview -ne '') {
    $more = @('-softbodyFrame', "`"$((Resolve-Path $Preview).Path)`"", '-softbodyPng', "`"$Png`"")
    if ($Material -ne '') { $more += @('-softbodyMaterial', $Material) }
    Invoke-Unity 'SoftbodyShaderBuild.Preview' $more 'preview.log' 'SOFTBODY_PREVIEW OK' | Out-Null
    return
}
if ($Check -ne '') {
    if ((Test-Bundles $Check) -gt 0) { throw 'a bundle failed the check (see check.log)' }
    return
}
if ($PreviewDir -ne '') {
    Invoke-Unity 'SoftbodyShaderBuild.BuildAndPreview' @('-softbodyOut', "`"$($shaders.Path)`"", '-softbodyPreviewDir', "`"$((Resolve-Path $PreviewDir).Path)`"") 'build.log' 'SOFTBODY_PREVIEWS OK' | Out-Null
} else {
    Invoke-Unity 'SoftbodyShaderBuild.Build' @('-softbodyOut', "`"$($shaders.Path)`"") 'build.log' 'SOFTBODY_BUILD OK' | Out-Null
}
& python -I (Join-Path $here 'fix_d3d11_programs.py') $bundle
if ($LASTEXITCODE -ne 0) { throw "fix_d3d11_programs.py failed ($LASTEXITCODE): the bundle is as Unity built it, which the game's renderer can't use" }
if (-not $NoCheck) {
    if ((Test-Bundles $bundle) -gt 0) { throw 'the fixed bundle failed the check (see check.log): do not install it' }
}
