# Launch N For The King II instances for multiplayer harness testing.
#
# Each process gets its own Crucible RPC port and instance name via environment variables
# (CRUCIBLE_RPC_PORT / CRUCIBLE_INSTANCE) — they cannot come from the BepInEx config, because all
# instances on one machine share a single config file and would fight over the same port.
#
# The game is launched from the exe directly rather than through Steam: Steam enforces one running
# instance per app, and the game boots via BepInEx doorstop (winhttp.dll) off the exe anyway.
#
# Examples:
#   .\launch-peers.ps1                  # 2 peers on 8787/8788
#   .\launch-peers.ps1 -Count 1         # single instance
#   .\launch-peers.ps1 -BasePort 9000   # different ports

[CmdletBinding()]
param(
    [int]$Count = 2,
    [int]$BasePort = 8787,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\For The King II',
    # -GRAPHICS_LOW / -THREADING_LOW are deliberately NOT default: applying them throws
    # ArgumentNullException in SettingsViewHelper._applyTextureQuality during
    # AppConfigManager.SetConfigFromLaunchParameters, which aborts RouterMono.StartInternalAsync and
    # leaves the process running with a blank window. Pass them explicitly only if a future build fixes it.
    [string[]]$GameArgs = @('-SKIPSPLASH', '-WINDOWED')
)

$exe = Join-Path $GameDir 'For The King II.exe'
if (-not (Test-Path $exe)) { throw "Game exe not found: $exe" }

$launched = @()
for ($i = 0; $i -lt $Count; $i++) {
    $name = 'p' + ($i + 1)
    $port = $BasePort + $i

    $env:CRUCIBLE_INSTANCE = $name
    $env:CRUCIBLE_RPC_PORT = "$port"

    Write-Host "Launching $name on port $port ..."
    $p = Start-Process -FilePath $exe -ArgumentList $GameArgs -PassThru
    $launched += [pscustomobject]@{ Instance = $name; Port = $port; Pid = $p.Id }

    # Stagger: two Unity instances racing through startup on one GPU is a reliable way to hang both.
    if ($i -lt $Count - 1) { Start-Sleep -Seconds 20 }
}

Remove-Item Env:\CRUCIBLE_INSTANCE -ErrorAction SilentlyContinue
Remove-Item Env:\CRUCIBLE_RPC_PORT -ErrorAction SilentlyContinue

$launched | Format-Table -AutoSize

$spec = ($launched | ForEach-Object { "$($_.Instance)=$($_.Port)" }) -join ','
Write-Host ""
Write-Host "Point the MCP server at these peers with:"
Write-Host "  CRUCIBLE_INSTANCES=$spec"
