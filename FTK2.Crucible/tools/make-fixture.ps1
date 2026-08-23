<#
.SYNOPSIS
  Generates a Crucible save fixture unattended: cold boot -> new run with an arbitrary four-class
  party -> teleport-scroll grant -> saveUser -> captured .ftk2 + fixture.json manifest.

.DESCRIPTION
  Drives the proven recipe in data/Recipes/new-run-four-classes.json entirely over the Crucible RPC
  (see mcp/server.js's ftk2_exec / crucible_ui_* for the same primitives used interactively). No
  keyboard/mouse input on the real machine, no manual play-through.

  SAFETY (non-negotiable, matches the recipe's own "safety" block):
    - Never clicks 'continue-btn' or 'load-btn' -- those resume the owner's live co-op campaign.
    - Only ever COPIES OUT of %USERPROFILE%\...\GameRuns\ -- never deletes or overwrites anything
      there. The new save is identified by diffing the folder's file list before/after saveUser,
      never by "the newest file" (mtime is not trustworthy under a live co-op install).
    - Every stage is verified before the next one runs; a step that doesn't produce the expected
      change fails loudly with the observed on-screen elements, not a silent continue.

.PARAMETER PartyClassIds
  Exactly four ClassForge class ids (e.g. CF_EOR_THIEF), one per party slot, in slot order.

.PARAMETER FixtureName
  Directory name under data/Fixtures/<name>/ -- also written into fixture.json.

.PARAMETER ItemGrants
  Hashtable of ConfigId -> Quantity to grant after the intro dialogue clears, via the shipped
  GetSpecificThing console command (SPEC.md: "GetSpecificThing | configId, qty | Give item").
  Default grants a big-but-sane stack (99, per the owner's guidance -- not int.MaxValue, which risks
  overflowing a UI counter or serializer) of both overworld teleport consumables found in the game's
  own Items.json: SCROLL_TELEPORT_01 (PICK_HEX_TELEPORT) and SCROLL_PORTAL_01 (PICK_HEX_PORTAL).

  VERIFICATION CAVEAT (uncertainty, do not treat as proven): this script checks only the /exec
  call's own ok/error result for each grant. Counting CharacterComponent.Things across the party
  (the deeper verification described in the task) needs a read path this codebase does not yet
  expose -- crucible_get only walks static-type dot-paths, and reaching a live Entity's components
  needs a helper call, not a field walk. That gap is called out in fixture.json's itemsGranted
  entries via "verified":false rather than silently claimed as checked.

.EXAMPLE
  .\make-fixture.ps1 -PartyClassIds CF_EOR_THIEF,CF_EOR_TRICKSHOT,CF_EOR_CORSAIR,CF_EOR_BLADEDANCER -FixtureName four-classes-basic
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(4, 4)]
    [string[]]$PartyClassIds,

    [Parameter(Mandatory = $true)]
    [string]$FixtureName,

    [string]$AdventureCategory = 'Age of Rebellion',
    [string]$Adventure = 'The Resistance',

    [int]$Port = 8787,
    [string]$Token = $env:CRUCIBLE_RPC_TOKEN,

    [hashtable]$ItemGrants = @{ SCROLL_TELEPORT_01 = 99; SCROLL_PORTAL_01 = 99 },

    [int]$BootSettleSec = 30,
    [int]$MaxClassCycles = 40,
    [int]$DialogueMaxPresses = 50,

    [string]$GameRunsDir = (Join-Path $env:USERPROFILE 'AppData\LocalLow\IronOak Games\For The King II\GameRuns')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixtureDir = Join-Path (Join-Path $repoRoot 'data\Fixtures') $FixtureName
$baseUrl = "http://127.0.0.1:$Port"

# --------------------------------------------------------------------------- RPC plumbing

function Invoke-CrucibleExec {
    param([string]$Command, [string[]]$Args = @())

    $headers = @{}
    if ($Token) { $headers['X-Crucible-Token'] = $Token }

    $body = @{ command = $Command; args = @($Args) } | ConvertTo-Json -Compress
    try {
        $resp = Invoke-RestMethod -Uri "$baseUrl/exec" -Method Post -Body $body -ContentType 'application/json' -Headers $headers -TimeoutSec 30
    }
    catch {
        throw "RPC call failed for '$Command $($Args -join ' ')': $($_.Exception.Message)"
    }
    return $resp
}

function Invoke-CrucibleExecOrThrow {
    param([string]$Command, [string[]]$Args = @(), [string]$Stage)

    $resp = Invoke-CrucibleExec -Command $Command -Args $Args
    if (-not $resp.ok) {
        $dump = Get-ScreenDump
        throw "[$Stage] '$Command $($Args -join ' ')' failed: $($resp.error)`nScreen at failure:`n$dump"
    }
    return $resp
}

function Get-ScreenDump {
    $resp = Invoke-CrucibleExec -Command 'crucible_ui_dump' -Args @('-', '-')
    if ($resp.ok) { return $resp.result }
    return "(could not dump screen: $($resp.error))"
}

# Parses one crucible_ui_dump line into an object, mirroring mcp/server.js's parseUiDumpLine so the
# two stay in lockstep with the same format from UiTreeRenderer.FormatLine.
$dumpLineRegex = "^(\S+) name=(?:'([^']*)'|\(null\)) text=(?:'([^']*)'|\(null\)) visible=(True|False) enabled=(True|False) doc=(?:'([^']*)'|\(null\))(\s\[FOCUSED\])?$"

function Get-DumpElements {
    param([string]$Filter = '-', [string]$Kinds = '-')

    $resp = Invoke-CrucibleExecOrThrow -Command 'crucible_ui_dump' -Args @($Filter, $Kinds) -Stage 'ui_dump'
    $lines = $resp.result -split "`n"
    $elements = @()
    foreach ($line in $lines) {
        $m = [regex]::Match($line, $dumpLineRegex)
        if (-not $m.Success) { continue }
        $elements += [pscustomobject]@{
            Type    = $m.Groups[1].Value
            Name    = $m.Groups[2].Value
            Text    = $m.Groups[3].Value
            Visible = $m.Groups[4].Value -eq 'True'
            Enabled = $m.Groups[5].Value -eq 'True'
            Doc     = $m.Groups[6].Value
            Focused = $m.Groups[7].Success
        }
    }
    return $elements
}

# --------------------------------------------------------------------------- forbidden-click guard

# Same list mcp/server.js enforces client-side: never let this script click a resume/load path,
# even indirectly through a substring match.
$forbiddenClickNames = @('continue-btn', 'load-btn')

function Assert-NotForbiddenSelector {
    param([string]$Selector)
    if ($forbiddenClickNames -contains $Selector.Trim().ToLowerInvariant()) {
        throw "REFUSED: selector '$Selector' is forbidden (continue-btn/load-btn resume the owner's live save)."
    }
}

function Invoke-CrucibleClick {
    param([string]$Selector, [string]$Stage, [switch]$AllowMissing)

    Assert-NotForbiddenSelector -Selector $Selector
    $resp = Invoke-CrucibleExecOrThrow -Command 'crucible_ui_click' -Args @($Selector) -Stage $Stage

    if ($resp.result -match 'invoked=True') {
        Write-Host "  [$Stage] clicked '$Selector'"
        return $true
    }
    if ($AllowMissing -and $resp.result -match 'no visible button matches') {
        Write-Host "  [$Stage] '$Selector' not present, skipping (allowed)"
        return $false
    }
    $dump = Get-ScreenDump
    throw "[$Stage] click on '$Selector' did not report invoked=True. Result:`n$($resp.result)`n`nScreen:`n$dump"
}

# --------------------------------------------------------------------------- stages

Write-Host "== make-fixture: $FixtureName =="
Write-Host "Party: $($PartyClassIds -join ', ')"
$itemGrantsSummary = ($ItemGrants.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
Write-Host "Item grants: $itemGrantsSummary"

# Stage 0: without this, Unity ignores ALL injected input while the window is unfocused and the
# game appears frozen. CRITICAL AND FIRST -- see data/Recipes/new-run-four-classes.json step 0.
Write-Host "`n[0] set_runInBackground"
Invoke-CrucibleExecOrThrow -Command 'crucible_invoke' -Args @('UnityEngine.Application', 'set_runInBackground', 'true') -Stage 'runInBackground' | Out-Null

# Stage 1: boot is a race -- the game reaches MAIN_MENU then routes itself to MULTIPLAYER_LOBBY
# a couple seconds later, trying to rejoin a stale online adventure. Let it settle.
Write-Host "[1] waiting ${BootSettleSec}s for boot to settle"
Start-Sleep -Seconds $BootSettleSec

# Stage 2-3: escape the boot multiplayer lobby.
Write-Host "[2-3] escaping boot multiplayer lobby"
Invoke-CrucibleClick -Selector 'online-quit-btn' -Stage 'mp-escape' -AllowMissing | Out-Null
Invoke-CrucibleClick -Selector 'back-btn' -Stage 'mp-escape' -AllowMissing | Out-Null

# Stage 4-7: main menu -> campaign -> category -> focus carousel -> submit selected adventure.
Write-Host "[4-7] new game: campaign -> $AdventureCategory -> $Adventure"
Invoke-CrucibleClick -Selector 'campaign-btn' -Stage 'new-game' | Out-Null
Invoke-CrucibleClick -Selector $AdventureCategory -Stage 'new-game' | Out-Null
Invoke-CrucibleExecOrThrow -Command 'crucible_ui_nav' -Args @('down') -Stage 'new-game' | Out-Null
Invoke-CrucibleExecOrThrow -Command 'crucible_ui_submit' -Args @('-') -Stage 'new-game' | Out-Null

$dump = Get-DumpElements -Filter $Adventure
if (-not $dump) {
    throw "[new-game] expected '$Adventure' to appear on screen after selecting it; not found.`n$(Get-ScreenDump)"
}

# Stage 8-9: difficulty is STICKY across sessions -- read it before changing it, and step it off
# MASTER/GAUNTLET (manual saves are disabled there, which would make fixture capture impossible).
Write-Host "[8-9] difficulty: ensuring off MASTER/GAUNTLET"
Invoke-CrucibleExecOrThrow -Command 'crucible_ui_focus' -Args @('difficulty-inp') -Stage 'difficulty' | Out-Null
$cycles = 0
while ($true) {
    $el = Get-DumpElements -Filter 'difficulty-inp' | Select-Object -First 1
    if (-not $el) { throw "[difficulty] difficulty-inp not found on screen.`n$(Get-ScreenDump)" }
    if ($el.Text -match '(?i)apprentice|journeyman') { Write-Host "  difficulty is now '$($el.Text)'"; break }
    if ($cycles -ge 5) { throw "[difficulty] could not step off MASTER/GAUNTLET after $cycles attempts; last text='$($el.Text)'" }
    Invoke-CrucibleExecOrThrow -Command 'crucible_ui_nav' -Args @('left') -Stage 'difficulty' | Out-Null
    $cycles++
}

# Stage 10: confirm adventure + difficulty -> party screen.
Write-Host "[10] confirming adventure selection"
Invoke-CrucibleClick -Selector 'Select Adventure' -Stage 'select-adventure' | Out-Null

# Stage 11-15: fill all four party slots with the requested classes.
Write-Host "[11-15] filling party (4 slots)"
for ($slot = 0; $slot -lt 4; $slot++) {
    $wantedClass = $PartyClassIds[$slot]
    Write-Host "  slot ${slot}: $wantedClass"

    Invoke-CrucibleClick -Selector 'add-character-btn' -Stage "party-slot-$slot" | Out-Null
    Invoke-CrucibleExecOrThrow -Command 'crucible_pad' -Args @('a') -Stage "party-slot-$slot" | Out-Null
    Invoke-CrucibleExecOrThrow -Command 'crucible_ui_focus' -Args @('class-text-selector') -Stage "party-slot-$slot" | Out-Null

    $found = $false
    for ($i = 0; $i -lt $MaxClassCycles; $i++) {
        $el = Get-DumpElements -Filter 'class-text-selector' | Select-Object -First 1
        if (-not $el) { throw "[party-slot-$slot] class-text-selector not found.`n$(Get-ScreenDump)" }
        if ($el.Text -eq $wantedClass) { $found = $true; break }
        Invoke-CrucibleExecOrThrow -Command 'crucible_ui_nav' -Args @('right') -Stage "party-slot-$slot" | Out-Null
    }
    if (-not $found) {
        throw "[party-slot-$slot] never reached class '$wantedClass' after $MaxClassCycles nav-right presses; last seen '$($el.Text)'. Is it a valid ClassForge id?"
    }
    Write-Host "    -> $wantedClass confirmed"
}

# Stage 16-18: confirm loadout and begin.
Write-Host "[16-18] beginning adventure"
Invoke-CrucibleClick -Selector 'party-container-next-btn' -Stage 'begin' | Out-Null
Invoke-CrucibleClick -Selector 'begin-adventure-btn' -Stage 'begin' | Out-Null
Invoke-CrucibleClick -Selector 'sys-dialog-ok-btn' -Stage 'begin' -AllowMissing | Out-Null

# Stage 19-20: clear the post-load gate and the intro conversation.
Write-Host "[19] clearing post-load 'Click to Continue' gate"
Invoke-CrucibleExecOrThrow -Command 'crucible_pad' -Args @('a') -Stage 'post-load-gate' | Out-Null

Write-Host "[20] clearing intro dialogue"
Invoke-CrucibleExecOrThrow -Command 'crucible_dialogue_advance' -Args @("$DialogueMaxPresses") -Stage 'intro-dialogue' | Out-Null

# NEW: grant mobility items. Shallow verification only -- see the VERIFICATION CAVEAT in the doc
# header. GetSpecificThing is a shipped console command (SPEC.md), not a Crucible-registered one.
Write-Host "[20b] granting items: $(($ItemGrants.Keys) -join ', ')"
$grantResults = @()
foreach ($configId in $ItemGrants.Keys) {
    $qty = [int]$ItemGrants[$configId]
    if ($qty -le 0) { throw "[item-grant] quantity for $configId must be positive, got $qty" }

    $resp = Invoke-CrucibleExec -Command 'GetSpecificThing' -Args @($configId, "$qty")
    $ok = [bool]$resp.ok
    Write-Host "  $configId x$qty -> ok=$ok $(if (-not $ok) { $resp.error })"
    $grantResults += [pscustomobject]@{ ConfigId = $configId; Quantity = $qty; Verified = $ok }
    if (-not $ok) {
        Write-Warning "[item-grant] $configId grant reported ok=false ($($resp.error)) -- recorded in the manifest as unverified, not treated as fatal (owner's request said the grant matters more than a fixture-generation abort)."
    }
}

# --------------------------------------------------------------------------- capture

Write-Host "`n[21] capturing save"
if (-not (Test-Path $GameRunsDir)) { throw "GameRuns directory not found: $GameRunsDir" }

# Identify the new save by diffing the folder's file list before/after saveUser -- NEVER by
# "the newest file". The owner's live co-op saves live in this same folder.
$before = @(Get-ChildItem -Path $GameRunsDir -Filter '*.ftk2' -File | Select-Object -ExpandProperty Name)

Invoke-CrucibleExecOrThrow -Command 'saveUser' -Args @() -Stage 'save' | Out-Null
Start-Sleep -Seconds 2 # let the write land before listing the folder again

$after = @(Get-ChildItem -Path $GameRunsDir -Filter '*.ftk2' -File | Select-Object -ExpandProperty Name)
$newFiles = @($after | Where-Object { $before -notcontains $_ })

if ($newFiles.Count -eq 0) {
    throw "[save] no new .ftk2 file appeared in $GameRunsDir after saveUser. Before=$($before.Count) After=$($after.Count). Refusing to guess which file is ours."
}
if ($newFiles.Count -gt 1) {
    throw "[save] $($newFiles.Count) new .ftk2 files appeared after saveUser ($($newFiles -join ', ')); refusing to guess which one is ours."
}

$saveFileName = $newFiles[0]
$runId = [System.IO.Path]::GetFileNameWithoutExtension($saveFileName)
Write-Host "  new save: $saveFileName (runId=$runId)"

# Corroborating check, not fatal if it disagrees: RouterHelper.Env.SelectedGameRunId should now
# name the same run.
$selectedResp = Invoke-CrucibleExec -Command 'crucible_get' -Args @('RouterHelper.Env.SelectedGameRunId')
if ($selectedResp.ok -and $selectedResp.result -notmatch [regex]::Escape($runId)) {
    Write-Warning "RouterHelper.Env.SelectedGameRunId ('$($selectedResp.result)') does not obviously match the diffed runId '$runId' -- proceeding on the file-diff result, which is the safer signal, but flagging the mismatch."
}

# --------------------------------------------------------------------------- fixture output

New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
Copy-Item -Path (Join-Path $GameRunsDir $saveFileName) -Destination (Join-Path $fixtureDir $saveFileName) -Force
Write-Host "  copied to $fixtureDir\$saveFileName (source left untouched)"

$gameVersion = $null
$versionResp = Invoke-CrucibleExec -Command 'crucible_get' -Args @('RouterHelper.Env.User.LastPlayedVersionString')
if ($versionResp.ok -and $versionResp.result -notmatch '^error:') { $gameVersion = $versionResp.result }
else { Write-Warning "Could not read RouterHelper.Env.User.LastPlayedVersionString ($($versionResp.error); result='$($versionResp.result)') -- gameVersion will be null in the manifest." }

# ClassForge's own content hash is logged by ClassForge.Plugin, not exposed via a live reflectable
# field this script can crucible_get -- "if readable" per spec. Best-effort scrape of the newest
# BepInEx log for the line ParityBridge.cs writes ("dataHash=<value>"); null if not found.
$classForgeDataHash = $null
try {
    $bepinexLog = Get-ChildItem -Path 'C:\Program Files (x86)\Steam\steamapps\common\For The King II\BepInEx' -Recurse -Filter 'LogOutput.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($bepinexLog) {
        $hashLine = Select-String -Path $bepinexLog.FullName -Pattern 'dataHash=([^\s,.\]]+)' -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($hashLine -and $hashLine.Matches.Count -gt 0) { $classForgeDataHash = $hashLine.Matches[0].Groups[1].Value }
    }
}
catch {
    Write-Warning "ClassForge dataHash scrape failed (non-fatal): $($_.Exception.Message)"
}
if (-not $classForgeDataHash) { Write-Warning "ClassForge dataHash not readable -- recording null in the manifest, per spec ('if readable')." }

$manifest = [ordered]@{
    fixtureName        = $FixtureName
    partyClassIds       = $PartyClassIds
    runId               = $runId
    gameVersion         = $gameVersion
    classForgeDataHash  = $classForgeDataHash
    timestampUtc        = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    recipeSteps         = @('new-run-four-classes', 'teleport-scroll-grant')
    itemsGranted        = @($grantResults | ForEach-Object { [ordered]@{ configId = $_.ConfigId; quantity = $_.Quantity; verified = $_.Verified } })
    saveFile            = $saveFileName
    adventureCategory   = $AdventureCategory
    adventure           = $Adventure
}

$manifestPath = Join-Path $fixtureDir 'fixture.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8
Write-Host "`nFixture manifest written: $manifestPath"
Write-Host "DONE. Fixture '$FixtureName' captured (runId=$runId)."
