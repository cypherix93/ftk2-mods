<#
.SYNOPSIS
  Builds and runs LiveDataHarness against a real game install, then re-runs it in a second process
  to prove cross-process determinism of the pack dataHash.
.PARAMETER GameDir
  Optional path to the For The King II install. Omit to auto-detect.
.PARAMETER JsonOut
  Where to write the machine-readable report. A second report is written alongside it for the
  cross-process comparison.
#>
param(
    [string]$GameDir = '',
    [string]$JsonOut = 'tools/out/harness-report.json'
)

$ErrorActionPreference = 'Stop'
$proj = 'FTK2.DevKit/sandbox/LiveDataHarness'

dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$argsList = @('run', '--project', $proj, '-c', 'Release', '--no-build', '--')
if ($GameDir) { $argsList += @('--game-dir', $GameDir) }

& dotnet @($argsList + @('--json', $JsonOut))
$exit1 = $LASTEXITCODE

# A fresh process, not a second pass inside the first: ordering that happens to be stable within one
# process can still differ across processes, and it is the cross-process case that decides whether two
# players' installs agree on the pack dataHash.
$second = [System.IO.Path]::ChangeExtension($JsonOut, '.pass2.json')
& dotnet @($argsList + @('--json', $second))
$exit2 = $LASTEXITCODE

if ($exit1 -eq 2 -or $exit2 -eq 2) {
    Write-Host "LiveDataHarness SKIPPED (no game install found)."
    exit 2
}

$h1 = (Get-FileHash $JsonOut -Algorithm SHA256).Hash
$h2 = (Get-FileHash $second  -Algorithm SHA256).Hash
if ($h1 -ne $h2) {
    Write-Host "CROSS-PROCESS DETERMINISM FAILURE: $JsonOut and $second differ."
    exit 1
}

Write-Host "LiveDataHarness: pass1=$exit1 pass2=$exit2, reports byte-identical."
exit ([Math]::Max($exit1, $exit2))
