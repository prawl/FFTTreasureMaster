# BuildLinked.ps1 builds prawl.fft.treasuremaster and deploys it into Reloaded-II.
#
# The dev half of the pipeline. Publish.ps1 is the release half and builds the zip. The steps
# both share (the bake, the shared gates, the DLL publish) live in tools/pipeline.ps1, and this
# file keeps what only a deploy needs. The DLL loads on the next game launch.

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

    # --- [1/5] Bake the treasure dataset (self-test gate) ---
    Write-Host "`n[1/5] Baking treasure dataset..." -ForegroundColor Yellow
    Invoke-TablePipeline -FailVerb DEPLOY

    # --- [2/5] The shared gates, inside their own temp folder ---
    Write-Host "[2/5] Running the shared gates..." -ForegroundColor Yellow
    $gateTemp = Enter-GateTemp
    try {
        Invoke-ManifestGate -FailVerb DEPLOY
        Invoke-FormatGate -FailVerb DEPLOY
        Invoke-UnitTestGate -FailVerb DEPLOY
        Invoke-HealthGate -FailVerb DEPLOY
        Invoke-TempLeftoverGate -FailVerb DEPLOY -GateTemp $gateTemp
    }
    finally { Exit-GateTemp -State $gateTemp }

    # --- [3/5] Clean the live mod folder, then publish the DLL and stage the manifest ---
    Write-Host "[3/5] Cleaning $dest and publishing the DLL..." -ForegroundColor Yellow
    if (Test-Path $dest) {
        # Keep the Vortex marker and the flight archives. Filtering the top level entries is
        # needed because -Exclude with -Recurse still wipes an excluded folder's contents.
        Get-ChildItem $dest -Force |
            Where-Object { $_.Name -ne "__folder_managed_by_vortex" -and $_.Name -ne "flight" } |
            Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    } else {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
    }

    Invoke-TreasureMasterPublish -OutDir $dest -CleanFirst

    Copy-Item "$root\mod\ModConfig.json" $dest -Force   # our manifest wins over any published one
    if (Test-Path "$root\mod\preview.png") { Copy-Item "$root\mod\preview.png" $dest -Force }

    # --- [4/5] Verify the deployment ---
    Write-Host "`n[4/5] Verifying deployment..." -ForegroundColor Cyan
    $errs = @()
    foreach ($file in $RequiredModFiles) {
        if (-not (Test-Path (Join-Path $dest $file))) { $errs += "$file missing" }
    }
    if ($errs.Count -gt 0) {
        Write-Host "`nDEPLOY VERIFICATION FAILED:" -ForegroundColor Red
        $errs | ForEach-Object { Write-Host "  X $_" -ForegroundColor Red }
        exit 1
    }

    # --- [5/5] Done ---
    Write-Host "`n[5/5] Deployed FFTTreasureMaster.dll + treasure.json -> $dest" -ForegroundColor Green
    Write-Host "Launch the game to apply (the DLL loads on next launch)." -ForegroundColor Green
}
catch {
    Write-Host "`n$_" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
