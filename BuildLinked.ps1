# BuildLinked.ps1 - local build + deploy of prawl.fft.treasuremaster into Reloaded-II.
#
# Local-dev counterpart to Publish.ps1 (which builds the production release zip).
# Mirrors the sibling FFTItemOverhaul mod's BuildLinked / Publish split:
#   BuildLinked.ps1 -> deploy straight into the live Reloaded Mods folder (this file)
#   Publish.ps1     -> stage + zip a distributable package
#
# The shared pipeline prefix (bake -> unit tests -> DLL publish) lives in
# tools/pipeline.ps1; this file keeps the deploy-specific half: mods-folder
# resolution, the clean, and deploy verification. The DLL loads on next game launch;
# treasure.json is read by the DLL at startup.

$ErrorActionPreference = "Stop"
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

. "$PSScriptRoot\tools\pipeline.ps1"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "   FFT Treasure Master - BUILD (linked)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

try {
    $root    = $PSScriptRoot
    $modId   = "prawl.fft.treasuremaster"
    $modsDir = $env:RELOADEDIIMODS
    if (-not $modsDir) {
        $modsDir = "C:\program files (x86)\steam\steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles\Reloaded\Mods"
    }
    $dest = Join-Path $modsDir $modId

    # --- [1/4] Bake the treasure dataset (self-test gate) ---
    Write-Host "`n[1/4] Baking treasure dataset..." -ForegroundColor Yellow
    Invoke-TablePipeline -FailVerb DEPLOY

    # --- [2/4] Unit tests (TDD gate) ---
    Write-Host "[2/4] Running unit tests (FFTTreasureMaster.Tests)..." -ForegroundColor Yellow
    Invoke-UnitTestGate -FailVerb DEPLOY

    # --- [3/4] Clean the live mod folder, then publish the DLL + stage the manifest ---
    Write-Host "[3/4] Cleaning $dest and publishing the DLL..." -ForegroundColor Yellow
    if (Test-Path $dest) {
        # Keep the Vortex marker so Vortex doesn't treat the folder as orphaned.
        Remove-Item "$dest\*" -Exclude "__folder_managed_by_vortex" -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
    }

    Invoke-TreasureMasterPublish -OutDir $dest -CleanFirst

    Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our manifest wins over any published one
    if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }

    # --- [4/4] Verify the deployment (fail loud on missing pieces; no silent drift) ---
    Write-Host "`n[4/4] Verifying deployment..." -ForegroundColor Cyan
    $errs = @()
    foreach ($file in $RequiredModFiles) {
        if (-not (Test-Path (Join-Path $dest $file))) { $errs += "$file missing" }
    }
    if ($errs.Count -gt 0) {
        Write-Host "`nDEPLOY VERIFICATION FAILED:" -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  X $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host "`nDeployed FFTTreasureMaster.dll + treasure.json -> $dest" -ForegroundColor Green
    Write-Host "Launch the game to apply (the DLL loads on next launch)." -ForegroundColor Green
}
catch {
    Write-Host "`n$_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
