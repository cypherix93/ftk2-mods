# LiveDataHarness — runs every mod's .Core logic against the REAL game's Configs, loaded
# out-of-process by reflection. No Unity, no BepInEx, no running game.
#
# Runs the harness twice in SEPARATE processes and requires byte-identical reports, for the same
# reason run-determinism.ps1 does: a result stable within one process can still differ between two
# peers, and that is exactly the parity failure this catches.
#
#   .\tools\run-harness.ps1
#   .\tools\run-harness.ps1 -GameDir "D:\Steam\steamapps\common\For The King II"
#
# Exit codes: 0 = all checks passed, 1 = at least one Error finding, 2 = skipped (no loadable install).
#
# The game directory is only ever READ. This script never writes to it and is safe to run while the
# game is open. It never instructs a Steam "Verify integrity of game files" — that deletes mod
# content the co-op saves depend on.

[CmdletBinding()]
param(
    [string]$GameDir = '',
    [string]$JsonOut = 'tools\out\harness-report.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $project = 'FTK2.DevKit\sandbox\LiveDataHarness'

    & dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'LiveDataHarness build failed' }

    $common = @('run', '--project', $project, '-c', 'Release', '--no-build', '--nologo', '--')
    if ($GameDir) { $common += @('--game-dir', $GameDir) }

    $second = [System.IO.Path]::ChangeExtension($JsonOut, '.pass2.json')

    Write-Host ''
    Write-Host '== pass 1 =='
    & dotnet @($common + @('--json', $JsonOut))
    $exit1 = $LASTEXITCODE

    Write-Host ''
    Write-Host '== pass 2 (separate process) =='
    & dotnet @($common + @('--json', $second))
    $exit2 = $LASTEXITCODE

    if ($exit1 -eq 2 -or $exit2 -eq 2) {
        Write-Host 'LiveDataHarness SKIPPED (no loadable For The King II install found).'
        exit 2
    }

    $h1 = (Get-FileHash $JsonOut -Algorithm SHA256).Hash
    $h2 = (Get-FileHash $second  -Algorithm SHA256).Hash

    Write-Host ''
    if ($h1 -ne $h2) {
        Write-Host "CROSS-PROCESS DETERMINISM FAILURE: $JsonOut and $second differ."
        Write-Host "  $JsonOut  $h1"
        Write-Host "  $second  $h2"
        Write-Host 'Diff them. A timing field or absolute path leaking into the report is a report bug;'
        Write-Host 'a differing failure list is a real determinism finding — two peers would disagree on'
        Write-Host 'dataHash and report a parity mismatch despite identical installs.'
        exit 1
    }

    Write-Host "LiveDataHarness: pass1=$exit1 pass2=$exit2, reports byte-identical ($h1)."
    exit ([Math]::Max($exit1, $exit2))
}
finally {
    Pop-Location
}
