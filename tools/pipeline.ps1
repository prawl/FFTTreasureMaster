# tools/pipeline.ps1 holds the steps BuildLinked.ps1 (dev deploy) and Publish.ps1 (release zip)
# share. Dot-source it; everything here lands in the caller's scope.
#
# The shared gates load first from gates/pipeline/gates.ps1. The bake must run before the unit
# tests, because TreasureSchemaTests reads the treasure.json the bake writes.

. "$PSScriptRoot\..\gates\pipeline\gates.ps1"

# Resolved from this file's own location, so the caller's working folder does not matter.
$PipelineRepoRoot = Split-Path -Parent $PSScriptRoot

# The files a deploy and a package must both contain. ModConfig.json declares the DLL, so it is
# never optional. Paths are forward slash and relative to the mod root, the way zip entries are.
$RequiredModFiles = @(
    "ModConfig.json",
    "FFTTreasureMaster.dll",
    "FFTTreasureMaster.deps.json",
    "Newtonsoft.Json.dll",
    "treasure.json"
)

function Invoke-TablePipeline {
    # Bakes the treasure tile dataset. The bake self tests and exits 1 on failure, which refuses
    # the deploy or the package. Missing python is a hard failure, not a skip.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE')]
        [string]$FailVerb
    )

    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "REFUSING TO ${FailVerb}: python not found on PATH (the treasure-db bake + self-test cannot run)."
    }

    Write-Host "  -> tools/gen_treasure_db.py (treasure_addrs.json + map_trap_formation.json -> treasure.json)..."
    & python "$PipelineRepoRoot\tools\gen_treasure_db.py"
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: treasure-db gen/self-test failed (exit $LASTEXITCODE)."
    }

    Write-Host "  -> Baked + self-tested OK." -ForegroundColor Green
}

function Invoke-TreasureMasterPublish {
    # Builds the runtime into $OutDir. The framework dependent publish emits the DLL, Newtonsoft,
    # the deps.json the Reloaded loader reads, and treasure.json through the csproj.
    #
    # -CleanFirst forces a full recompile. MSBuild's up to date check can ship a stale Release
    # DLL with a fresh timestamp, and the clean costs seconds.
    param(
        [Parameter(Mandatory = $true)][string]$OutDir,
        [switch]$CleanFirst
    )

    if ($CleanFirst) {
        Remove-Item -Recurse -Force "$PipelineRepoRoot\FFTTreasureMaster\obj\Release", "$PipelineRepoRoot\FFTTreasureMaster\bin\Release" -ErrorAction SilentlyContinue
    }

    & dotnet publish "$PipelineRepoRoot\FFTTreasureMaster\FFTTreasureMaster.csproj" -c Release -o $OutDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE)."
    }
}
