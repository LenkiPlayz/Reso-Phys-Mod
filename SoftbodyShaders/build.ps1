# Builds the renderer fork's smoke shader bundle, Softbody\GpuFluid\Shaders\softbody_smoke.bundle, with Unity 2019.4.31f1.
# Unity shows its window for about half a minute: batch mode is not licensed on this PC (the 2019.4 editor cannot use the Hub's
# newer licence service headless), but the windowed editor uses the signed-in Unity Hub.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1                       # build the bundle
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Preview frame.bin out.png [-Material Assets/Softbody/SoftbodyFoam.mat]
param([string]$Preview = '', [string]$Png = '', [string]$Material = '', [int]$TimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$unity = 'C:\Program Files\Unity\Hub\Editor\2019.4.31f1\Editor\Unity.exe'
$shaders = Resolve-Path (Join-Path $here '..\Softbody\GpuFluid\Shaders')
New-Item -ItemType Directory -Force (Join-Path $here 'Assets\Softbody'), (Join-Path $here 'Packages'), (Join-Path $here 'ProjectSettings') | Out-Null
Copy-Item (Join-Path $shaders 'SoftbodySmoke.shader') (Join-Path $here 'Assets\Softbody\SoftbodySmoke.shader') -Force
if ($Preview -ne '') {
    $log = Join-Path $here 'preview.log'
    $args = @('-quit', '-projectPath', "`"$here`"", '-executeMethod', 'SoftbodyShaderBuild.Preview', '-logFile', "`"$log`"",
              '-softbodyFrame', "`"$((Resolve-Path $Preview).Path)`"", '-softbodyPng', "`"$Png`"")
    if ($Material -ne '') { $args += @('-softbodyMaterial', $Material) }
    $marker = 'SOFTBODY_PREVIEW OK'
} else {
    $log = Join-Path $here 'build.log'
    $args = @('-quit', '-projectPath', "`"$here`"", '-executeMethod', 'SoftbodyShaderBuild.Build', '-logFile', "`"$log`"",
              '-softbodyOut', "`"$($shaders.Path)`"")
    $marker = 'SOFTBODY_BUILD OK'
}
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $unity -ArgumentList $args -PassThru
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "Unity did not finish in $TimeoutSeconds s (stopped)" }
$lines = Get-Content $log
$lines | Select-String -Pattern 'SOFTBODY_|error CS|Shader error|^\s*Assets/.*error' | ForEach-Object { $_.Line }
if (-not ($lines | Select-String -SimpleMatch $marker)) { throw "Unity exited $($p.ExitCode) without '$marker' (see $log)" }
