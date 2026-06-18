# tools/pipeline.ps1 - the shared pipeline prefix for BuildLinked.ps1 (dev deploy)
# and Publish.ps1 (release zip). Dot-source it; everything here lands in the
# caller's scope.
#
# Mirrors the sibling FFTItemOverhaul mod's split: one copy of bake -> test ->
# dotnet publish, two callers, no drift.
#
# Step order is load-bearing: gen_treasure_db.py must run BEFORE the unit tests
# (TreasureSchemaTests reads the build-generated FFTTreasureMaster/treasure.json),
# so call Invoke-TablePipeline first and Invoke-UnitTestGate second.

# Repo root, resolved from this file's own location so everything works no
# matter what cwd the caller happens to be in when it dot-sources us.
$PipelineRepoRoot = Split-Path -Parent $PSScriptRoot

# Required-file manifest shared by BuildLinked's deploy verification and Publish's
# Verify-Package: the mod manifest, the runtime (DLL + deps.json for the Reloaded
# loader + Newtonsoft), and the baked treasure dataset. ModConfig.json declares
# "ModDll": "FFTTreasureMaster.dll", so the DLL is non-optional. Paths are
# forward-slash relative to the mod root (zip-entry style).
$RequiredModFiles = @(
    "ModConfig.json",
    "FFTTreasureMaster.dll",
    "FFTTreasureMaster.deps.json",
    "Newtonsoft.Json.dll",
    "treasure.json"
)

function Invoke-TablePipeline {
    # Bake the treasure tile address dataset. The bake self-tests (pinned FNV-1a64
    # vectors + masked-hash vector) and exits 1 on failure -- which refuses
    # deploy/package the same way the item mod's dominance gate does. Missing python
    # is a hard failure, not a skip.
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

function Invoke-UnitTestGate {
    # The TDD gate. ONE canonical flag set, so a test that passes locally passed
    # under the same conditions everywhere.
    param(
        [Parameter(Mandatory = $true)][ValidateSet('DEPLOY', 'PACKAGE')]
        [string]$FailVerb
    )

    & dotnet test "$PipelineRepoRoot\FFTTreasureMaster.Tests\FFTTreasureMaster.Tests.csproj" --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "REFUSING TO ${FailVerb}: unit tests failed (see above)."
    }
}

function Invoke-TreasureMasterPublish {
    # Build the Treasure Master runtime into $OutDir. The framework-dependent publish
    # emits FFTTreasureMaster.dll, Newtonsoft.Json.dll, FFTTreasureMaster.deps.json
    # (the Reloaded loader reads it), and treasure.json (copied via the csproj).
    #
    # -CleanFirst forces a FULL recompile: MSBuild's incremental up-to-date check
    # can ship a stale Release DLL with a fresh timestamp (the copy step re-dates the
    # file even when CoreCompile is skipped). The clean costs seconds and deletes the
    # failure class.
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
