<#
.SYNOPSIS
    Packages FFT Treasure Master (the in-process runtime DLL + baked treasure dataset) for release.
.DESCRIPTION
    Production counterpart to BuildLinked.ps1 (which deploys straight into the live
    Reloaded Mods folder). Mirrors the sibling FFTItemOverhaul mod's BuildLinked / Publish split.

    Bakes the treasure dataset (self-test gate), runs the unit-test gate, builds the
    runtime DLL, then stages the deliverables (ModConfig.json, optional preview.png,
    FFTTreasureMaster.dll + deps + treasure.json) into a build folder named after the
    ModId and zips it with a single top-level wrapper folder so Reloaded-II / Nexus /
    Vortex extract to the expected path.

    The bake + test + DLL-build steps are SHARED with BuildLinked.ps1 (dot-sourced from
    tools/pipeline.ps1) so a local .\Publish.ps1 produces the same vetted artifacts a
    deploy would. The package is verified before it's considered shippable: any missing
    required file (including FFTTreasureMaster.dll) makes the script exit 1.
.PARAMETER Version
    Version number for the mod. Default: reads ModVersion from mod/ModConfig.json.
.PARAMETER OutputPath
    Where to save the final ZIP. Default: "." under GitHub Actions, else Downloads.
.PARAMETER NexusModId
    Nexus mod ID for the archive filename convention. Placeholder 0 until registered.
.PARAMETER SkipGenerate
    Skip the treasure-db bake (package the committed treasure.json as-is). Use only
    when you've already baked + gated this session.
#>

[cmdletbinding()]
param (
    [string]$Version = "",
    [string]$OutputPath = "",
    [int]$NexusModId = 0,
    [switch]$SkipGenerate
)

$ErrorActionPreference = "Stop"

## => Configuration <= ##
# The build folder's NAME becomes the wrapper folder INSIDE the zip so Vortex's FFT IC
# extension treats the archive as well-formed and doesn't double-nest the install.
$ModId           = "prawl.fft.treasuremaster"
$SourceModPath   = "mod"
$BuildOutputPath = "Publish/$ModId"
$SourceModConfig = "$SourceModPath/ModConfig.json"
$SourcePreview   = "$SourceModPath/preview.png"

if (-not $OutputPath) {
    if ($env:GITHUB_ACTIONS) { $OutputPath = "." }
    else { $OutputPath = "C:\Users\ptyRa\Downloads" }
}

## => Shared pipeline (the bake, the shared gates, the DLL publish, $RequiredModFiles) <= ##
. "$PSScriptRoot\tools\pipeline.ps1"

## => Functions <= ##
function Write-Status { param($Message, $Color = "Green") Write-Host "`n==> $Message" -ForegroundColor $Color }

function Write-ErrorMessage {
    # Throws instead of `exit 1` so the main catch owns the exit code (an `exit` from a
    # function unwinds through finally and can mask a red gate as exit 0).
    param($Message)
    throw $Message
}

function Get-ModVersion {
    param([string]$RequestedVersion)
    if (-not [string]::IsNullOrEmpty($RequestedVersion)) {
        Write-Host "  -> Using version from -Version parameter: $RequestedVersion"
        return $RequestedVersion
    }
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "No version specified and ModConfig.json not found at: $SourceModConfig"
    }
    $config = Get-Content $SourceModConfig -Raw | ConvertFrom-Json
    $modVersion = $config.ModVersion
    if ([string]::IsNullOrEmpty($modVersion)) {
        Write-ErrorMessage "ModConfig.json has no ModVersion field at: $SourceModConfig"
    }
    Write-Host "  -> Using version from ModConfig.json: $modVersion"
    return $modVersion
}

function Clean-BuildDirectories {
    Write-Status "Cleaning build directory..." "Yellow"
    if (Test-Path $BuildOutputPath) {
        Remove-Item "$BuildOutputPath\*" -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
    } else {
        New-Item $BuildOutputPath -ItemType Directory -Force | Out-Null
    }
}

function Copy-ModAssets {
    Write-Status "Staging mod deliverables..." "Cyan"
    if (-not (Test-Path $SourceModConfig)) {
        Write-ErrorMessage "ModConfig.json not found at: $SourceModConfig"
    }
    Write-Host "  -> Copying ModConfig.json..."
    Copy-Item $SourceModConfig -Destination $BuildOutputPath -Force

    # preview.png is OPTIONAL (it's the ModIcon; the mod runs fine without one).
    if (Test-Path $SourcePreview) {
        Write-Host "  -> Copying preview.png..."
        Copy-Item $SourcePreview -Destination $BuildOutputPath -Force
    } else {
        Write-Host "  -> No preview.png (optional ModIcon); skipping." -ForegroundColor Yellow
    }
}

function Create-Package {
    param([string]$ModVersion)
    Write-Status "Creating ZIP package..." "Green"

    $versionDashed = $ModVersion -replace '\.', '-'
    if ($NexusModId -gt 0) {
        $unixTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $packageName = "FFTTreasureMaster-$NexusModId-$versionDashed-$unixTimestamp.zip"
    } else {
        Write-Host "  -> Stable name (no -NexusModId); pass -NexusModId for the Vortex-parseable name before a Nexus upload." -ForegroundColor Yellow
        $packageName = "FFTTreasureMaster-$ModVersion.zip"
    }
    $packagePath = Join-Path $OutputPath $packageName

    if (Test-Path $packagePath) { Remove-Item $packagePath -Force }
    if (-not (Test-Path $OutputPath)) { New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null }
    if (-not (Test-Path $BuildOutputPath)) { Write-ErrorMessage "Build output directory not found: $BuildOutputPath" }

    # ZipArchiveMode lives in System.IO.Compression, which the FileSystem assembly
    # does not pull in for a bare type expression, so both loads are needed.
    try {
        Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction Stop
        Add-Type -Assembly System.IO.Compression -ErrorAction Stop
    }
    catch {
        [Reflection.Assembly]::LoadWithPartialName("System.IO.Compression.FileSystem") | Out-Null
        [Reflection.Assembly]::LoadWithPartialName("System.IO.Compression") | Out-Null
    }

    try {
        $absoluteBuildPath   = (Get-Item $BuildOutputPath).FullName
        $absolutePackagePath = [System.IO.Path]::GetFullPath($packagePath)
        Write-Host "  -> Source: $absoluteBuildPath"
        Write-Host "  -> Target: $absolutePackagePath"

        # Hand rolled instead of ZipFile.CreateFromDirectory. Under Windows PowerShell
        # 5.1 that call writes backslashes into entry names, which Windows tolerates but
        # Linux unzip keeps as literal file names, so the mod folder never appears.
        # The 1.6.0 Nexus upload shipped that way. The wrapper folder named after the
        # build dir is what Reloaded and the Vortex FFT IC extension expect.
        $wrapperName = Split-Path $absoluteBuildPath -Leaf
        $archive = [System.IO.Compression.ZipFile]::Open(
            $absolutePackagePath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in Get-ChildItem -LiteralPath $absoluteBuildPath -Recurse -File) {
                $relative  = $item.FullName.Substring($absoluteBuildPath.Length).TrimStart('\', '/')
                $entryName = "$wrapperName/" + ($relative -replace '\\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $item.FullName, $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        finally {
            $archive.Dispose()
        }

        if (Test-Path $absolutePackagePath) {
            $sizeMB = [math]::Round((Get-Item $absolutePackagePath).Length / 1MB, 2)
            Write-Host "  -> Package created ($sizeMB MB): $absolutePackagePath" -ForegroundColor Green
            return $absolutePackagePath
        }
        Write-ErrorMessage "Package was not created at: $absolutePackagePath"
    }
    catch {
        Write-Host "`n[ERROR] Failed to create ZIP package: $_" -ForegroundColor Red
        return $null
    }
}

function Verify-Package {
    param([string]$PackagePath)
    # Returns $true iff the package contains every required file. Caller MUST honor the
    # return value; this is the gate that catches "the zip exists but is empty / wrong".
    Write-Status "Verifying package contents..." "Cyan"
    if (-not $PackagePath -or -not (Test-Path $PackagePath)) {
        Write-Host "  -> Package not found for verification" -ForegroundColor Red
        return $false
    }
    Add-Type -Assembly System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $missingCount = 0
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)

        # Checked on the raw names before the normalization below would hide it. A
        # backslash in an entry name is the Linux flat-extract bug coming back.
        $backslashEntries = @($zip.Entries | Where-Object { $_.FullName -match '\\' })
        if ($backslashEntries.Count -gt 0) {
            Write-Host ("  [FAIL] {0} entries use backslash separators; Linux unzips them as flat literal names." -f $backslashEntries.Count) -ForegroundColor Red
            Write-Host ("         first offender: {0}" -f $backslashEntries[0].FullName) -ForegroundColor Red
            $zip.Dispose()
            return $false
        }

        # Normalize entry paths and strip the single wrapper folder.
        $entryPaths = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
        $firstSegments = @($entryPaths | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        if ($firstSegments.Count -eq 1 -and $firstSegments[0]) {
            $wrapper = $firstSegments[0]
            $entryPaths = @($entryPaths | ForEach-Object {
                if ($_.StartsWith("$wrapper/")) { $_.Substring($wrapper.Length + 1) } else { $_ }
            })
            Write-Host "  -> Wrapper folder: $wrapper" -ForegroundColor Gray
        }
        foreach ($file in $RequiredModFiles) {
            if ($entryPaths -contains $file) { Write-Host "  [OK] $file" -ForegroundColor Green }
            else { Write-Host "  [MISSING] $file" -ForegroundColor Red; $missingCount++ }
        }
        $zip.Dispose()
    }
    catch {
        Write-Host "`n[ERROR] Failed to verify package: $_" -ForegroundColor Red
        return $false
    }
    if ($missingCount -gt 0) {
        Write-Host "`n[FAIL] Verification failed: $missingCount required entries missing." -ForegroundColor Red
        return $false
    }
    Write-Host "`n[PASS] All required entries present." -ForegroundColor Green
    return $true
}

function Write-HealthReport {
    # The release's code health record. Runs after the package is verified, so only a shippable
    # package gets one. In GitHub Actions it runs for tag pushes only. A failure here warns and
    # never fails the release; the build reminder chases it later.
    param([string]$ModVersion)

    if ($env:GITHUB_ACTIONS -and $env:IS_RELEASE -ne 'true') { return }
    Write-Status "Writing the code health report..." "Cyan"
    $healthArgs = @('report', '--version', $ModVersion, '--write')
    if ($env:GITHUB_STEP_SUMMARY) { $healthArgs += @('--summary', $env:GITHUB_STEP_SUMMARY) }
    & python "$PSScriptRoot\gates\tools\health.py" @healthArgs
    if ($LASTEXITCODE -ne 0) {
        $msg = "The code health report for $ModVersion could not be written (exit $LASTEXITCODE). Run: python gates/tools/health.py report --version $ModVersion --write"
        Write-Host "[WARN] $msg" -ForegroundColor Yellow
        if ($env:GITHUB_ACTIONS) { Write-Host "::warning title=Code health::$msg" }
    }
    elseif (-not $env:GITHUB_ACTIONS) {
        Write-Host "  -> Commit docs/HEALTH.md and docs/health/v$ModVersion.json with the release." -ForegroundColor Yellow
    }
}

## => Main Script <= ##
Write-Host "`n=====================================" -ForegroundColor Magenta
Write-Host "    FFT Treasure Master - Publisher   " -ForegroundColor Magenta
Write-Host "=====================================" -ForegroundColor Magenta

$originalLocation = Get-Location
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

# Default to FAILURE so an early/unexpected exit never reports success.
$exitCode = 1

try {
    $finalVersion = Get-ModVersion -RequestedVersion $Version

    if (-not $SkipGenerate) {
        Write-Status "Baking treasure dataset (self-test gate)..." "Cyan"
        Invoke-TablePipeline -FailVerb PACKAGE
    } else {
        Write-Host "  -> -SkipGenerate set; packaging committed treasure.json as-is." -ForegroundColor Yellow
    }

    Write-Status "Running the shared gates..." "Cyan"
    $gateTemp = Enter-GateTemp
    try {
        Invoke-ManifestGate -FailVerb PACKAGE
        Invoke-FormatGate -FailVerb PACKAGE
        Invoke-UnitTestGate -FailVerb PACKAGE
        Invoke-HealthGate -FailVerb PACKAGE
        Invoke-TempLeftoverGate -FailVerb PACKAGE -GateTemp $gateTemp
    }
    finally { Exit-GateTemp -State $gateTemp }

    Clean-BuildDirectories

    Write-Status "Building Treasure Master DLL into the package..." "Cyan"
    Invoke-TreasureMasterPublish -OutDir $BuildOutputPath -CleanFirst

    # Drop debug symbols (keep deps.json -- the Reloaded loader reads it).
    Get-ChildItem $BuildOutputPath -Filter *.pdb -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host "  -> DLL build complete." -ForegroundColor Green

    Copy-ModAssets

    $packagePath = Create-Package -ModVersion $finalVersion

    if ($packagePath) {
        $verifyOk = Verify-Package -PackagePath $packagePath
        if (-not $verifyOk) {
            Write-Status "Publishing failed - package verification failed" "Red"
            $exitCode = 1
        }
        else {
            if ($env:GITHUB_OUTPUT) {
                $zipFilename = Split-Path $packagePath -Leaf
                Add-Content -Path $env:GITHUB_OUTPUT -Value "zip=$zipFilename"
                Write-Host "  -> Set GHA output: zip=$zipFilename" -ForegroundColor Cyan
            }
            Write-Status "Publishing completed successfully!" "Green"
            Write-Host "Package ready at: $packagePath" -ForegroundColor Yellow
            Write-Host "Version: $finalVersion" -ForegroundColor Yellow
            $exitCode = 0

            Write-HealthReport -ModVersion $finalVersion
        }
    }
    else {
        Write-Status "Publishing failed - package creation unsuccessful" "Red"
        $exitCode = 1
    }
}
catch {
    Write-Host "`n[ERROR] $_" -ForegroundColor Red
    Write-Host "Stack Trace: $($_.ScriptStackTrace)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Pop-Location
    Set-Location $originalLocation
    exit $exitCode
}
