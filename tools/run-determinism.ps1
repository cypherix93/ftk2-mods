# Pack determinism check — no game, no second peer, no Unity.
#
# Runs the harness twice in SEPARATE processes and requires byte-identical reports. Two processes
# matter: .NET string hashing and dictionary iteration order can differ between runs of the same
# binary, so a hash that is stable within one process can still differ between two peers. That is
# exactly the failure this catches, and it is invisible to any single-process test.
#
#   .\tools\run-determinism.ps1
#
# Exit codes: 0 = deterministic, 1 = determinism failure, 2 = skipped (no packs found).

[CmdletBinding()]
param(
    [string]$OutDir = 'tools\out\determinism',
    [string[]]$Roots = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $project = 'FTK2.DevKit\sandbox\DeterminismHarness'
    $first = Join-Path $OutDir 'run-1.json'
    $second = Join-Path $OutDir 'run-2.json'

    # Pass absolute roots rather than relying on the harness walking up to find them: `dotnet run`
    # does not start the app in this directory, and a silent SKIP reads exactly like a pass.
    if ($Roots.Count -gt 0) {
        $argsCommon = @($Roots)
    } else {
        $argsCommon = @(
            (Join-Path $repoRoot 'FTK2.ClassForge\data\ClassPacks'),
            (Join-Path $repoRoot 'FTK2.Blessings\data\ClassPacks')
        ) | Where-Object { Test-Path $_ }
    }

    if (-not $argsCommon -or $argsCommon.Count -eq 0) {
        Write-Host 'SKIP: no pack roots found under the repo.'
        exit 2
    }

    Write-Host '== run 1 =='
    & dotnet run --project $project -c Release --nologo -- @argsCommon --report $first
    $exit1 = $LASTEXITCODE
    if ($exit1 -eq 2) { Write-Host 'SKIP: no packs found.'; exit 2 }

    Write-Host ''
    Write-Host '== run 2 (separate process) =='
    & dotnet run --project $project -c Release --nologo -- @argsCommon --report $second
    $exit2 = $LASTEXITCODE

    if ($exit1 -ne 0 -or $exit2 -ne 0) {
        Write-Host ''
        Write-Host "DETERMINISM FAILURE: harness reported failing checks (exit $exit1 / $exit2)."
        exit 1
    }

    $h1 = (Get-FileHash $first -Algorithm SHA256).Hash
    $h2 = (Get-FileHash $second -Algorithm SHA256).Hash

    Write-Host ''
    if ($h1 -ne $h2) {
        Write-Host 'CROSS-PROCESS DETERMINISM FAILURE: the two reports differ.'
        Write-Host "  $first  $h1"
        Write-Host "  $second  $h2"
        Write-Host ''
        Write-Host 'Diff them. A differing check list is a real finding: the same pack files produced'
        Write-Host 'two different results in two processes, so two peers will disagree on dataHash and'
        Write-Host 'report a parity mismatch despite identical installs.'
        exit 1
    }

    Write-Host "Reports byte-identical across processes ($h1)."
    Write-Host 'DETERMINISTIC.'
    exit 0
}
finally {
    Pop-Location
}
