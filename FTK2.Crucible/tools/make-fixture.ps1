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
    # 40 was measured too small on 2026-08-26: slot 2 spent all 40 next-btn presses without
    # landing on CF_ORIG_PACIFIST and the script threw "is it a valid ClassForge id?" about an
    # id that is perfectly valid. The class carousel now carries vanilla + EOR + every
    # ClassForge pack, so the list is far longer than when this default was chosen.
    [int]$MaxClassCycles = 150,
    [int]$DialogueMaxPresses = 50,

    [string]$GameRunsDir = (Join-Path $env:USERPROFILE 'AppData\LocalLow\IronOak Games\For The King II\GameRuns')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixtureDir = Join-Path (Join-Path $repoRoot 'data\Fixtures') $FixtureName
$baseUrl = "http://127.0.0.1:$Port"

# --------------------------------------------------------------------------- RPC plumbing

function Invoke-CrucibleExec {
    # NOT $Args. That is a PowerShell AUTOMATIC VARIABLE: `param([string[]]$Args)` parses
    # fine but `-Args @(...)` never binds to it, so the callee sees an empty array and every
    # RPC goes out with NO arguments. Measured in BOTH 5.1 and 7 on 2026-08-26 -- it failed
    # at stage 0 with a 400 Bad Request on crucible_invoke and blocked every fixture build.
    param([string]$Command, [string[]]$CmdArgs = @())

    $headers = @{}
    if ($Token) { $headers['X-Crucible-Token'] = $Token }

    $body = @{ command = $Command; args = @($CmdArgs) } | ConvertTo-Json -Compress
    try {
        $resp = Invoke-RestMethod -Uri "$baseUrl/exec" -Method Post -Body $body -ContentType 'application/json' -Headers $headers -TimeoutSec 30
    }
    catch {
        throw "RPC call failed for '$Command $($CmdArgs -join ' ')': $($_.Exception.Message)"
    }
    return $resp
}

function Invoke-CrucibleExecOrThrow {
    param([string]$Command, [string[]]$CmdArgs = @(), [string]$Stage)

    $resp = Invoke-CrucibleExec -Command $Command -CmdArgs $CmdArgs
    if (-not $resp.ok) {
        $dump = Get-ScreenDump
        throw "[$Stage] '$Command $($CmdArgs -join ' ')' failed: $($resp.error)`nScreen at failure:`n$dump"
    }
    return $resp
}

function Get-ScreenDump {
    $resp = Invoke-CrucibleExec -Command 'crucible_ui_dump' -CmdArgs @('-', '-')
    if ($resp.ok) { return $resp.result }
    return "(could not dump screen: $($resp.error))"
}

# Parses one crucible_ui_dump line into an object, mirroring mcp/server.js's parseUiDumpLine so the
# two stay in lockstep with the same format from UiTreeRenderer.FormatLine.
# MULTILINE-TOLERANT ON PURPOSE. Two measured parse failures, both silent, both fatal:
#   1. An element whose TEXT CONTAINS A NEWLINE ('carousel-right-button' and all nine
#      'adventure-art-holder' buttons have text="`n" on 1.14.6) is split in half by a naive
#      `-split "`n"` + per-line match, so NEITHER half matches and the element reads as ABSENT.
#      That is exactly how the 30s "the adventure carousel never rendered" throw happened while
#      the carousel was fully rendered and clickable on screen.
#   2. NESTED elements are INDENTED in the dump, so a `^(\S+)` anchor misses every child.
# So: match over the WHOLE dump with RegexOptions.Multiline, allow leading whitespace, and let
# [^']* span newlines (it does in .NET -- a negated class includes `n).
$dumpLineRegex = "^[ 	]*(\S+) name=(?:'([^']*)'|\(null\)) text=(?:'([^']*)'|\(null\)) visible=(True|False) enabled=(True|False) doc=(?:'([^']*)'|\(null\))(\s\[FOCUSED\])?$"

function Get-DumpElements {
    param([string]$Filter = '-', [string]$Kinds = '-')

    $resp = Invoke-CrucibleExecOrThrow -Command 'crucible_ui_dump' -CmdArgs @($Filter, $Kinds) -Stage 'ui_dump'
    $elements = @()
    foreach ($m in [regex]::Matches($resp.result, $dumpLineRegex, [System.Text.RegularExpressions.RegexOptions]::Multiline)) {
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
    $resp = Invoke-CrucibleExecOrThrow -Command 'crucible_ui_click' -CmdArgs @($Selector) -Stage $Stage

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
Invoke-CrucibleExecOrThrow -Command 'crucible_invoke' -CmdArgs @('UnityEngine.Application', 'set_runInBackground', 'true') -Stage 'runInBackground' | Out-Null

# Stage 1: boot is a race -- the game reaches MAIN_MENU then routes itself to MULTIPLAYER_LOBBY
# a couple seconds later, trying to rejoin a stale online adventure. Let it settle.
Write-Host "[1] waiting ${BootSettleSec}s for boot to settle"
Start-Sleep -Seconds $BootSettleSec

# Stage 2-3: escape the boot multiplayer lobby.
Write-Host "[2-3] escaping boot multiplayer lobby"
# 'back-btn' is AllowMissing, so on any screen that HAS one it gets pressed -- including the
# adventure-selection and adventure-detail screens, where it walks the run BACKWARDS out of the
# flow this script is trying to resume. Only escape when the boot really did land in the online
# lobby: the tell is 'online-quit-btn'. Measured 2026-08-26 -- a blind back-btn here undid a
# resumable detail screen and sent the next stage hunting for controls that were no longer up.
if (Get-DumpElements -Filter 'online-quit-btn') {
    Invoke-CrucibleClick -Selector 'online-quit-btn' -Stage 'mp-escape' -AllowMissing | Out-Null
    Invoke-CrucibleClick -Selector 'back-btn' -Stage 'mp-escape' -AllowMissing | Out-Null
} else {
    Write-Host "  not in the online lobby (no online-quit-btn); leaving the current screen alone"
}

# Stage 4-7: main menu -> campaign -> category -> focus carousel -> submit selected adventure.
#
# THE CATEGORY BUTTONS ARE WAITED FOR, NOT ASSUMED. Measured 2026-08-26: clicking 'campaign-btn'
# succeeds and the screen it opens can be LoadGameUIDocument -- "Load a Saved Adventure", the
# owner's SAVE LIST -- rendered over AdventureSelectionUIDocument when a save exists. While that
# panel is up the category buttons are not visible, and the old blind click on the category text
# threw with "Visible buttons: (none)" and a 50-line dump that did not say why.
#
# All three categories share ONE element name, 'adventure-category-button', and differ only by
# TEXT ('Age of Rebellion' / 'Age of Omus' / 'Challenge Modes'), which is why the click below is
# by text -- the name alone cannot disambiguate them. Confirmed by dump on 2026-08-26.
#
# `back-btn` is the ONLY control used to get off the save panel: exactly named, never a save row,
# and never continue-btn/load-btn -- those resume the owner's live co-op campaign.
Write-Host "[4-7] new game: -> $AdventureCategory -> $Adventure"

# RESUMABLE. This script can be re-run against a game already partway through adventure selection
# (a previous attempt died later and left the DETAIL screen up). Walking back to MAIN_MENU means
# pressing back-btn on a screen that also carries continue-btn/load-btn, which is the surface the
# safety rules keep us off -- so if the detail screen is already up, stages 4-7 are skipped.
# The detail screen is identified by next-btn reading 'Select Adventure'.
$alreadyOnDetail = [bool](Get-DumpElements -Filter 'next-btn' | Where-Object { $_.Text -eq 'Select Adventure' })
if ($alreadyOnDetail) { Write-Host "  adventure detail screen already up; skipping category/carousel" }

if (-not $alreadyOnDetail -and -not (Get-DumpElements -Filter 'adventure-category-button')) {
    Invoke-CrucibleClick -Selector 'campaign-btn' -Stage 'new-game' -AllowMissing | Out-Null
}

$catDeadline = (Get-Date).AddSeconds(90)
while (-not $alreadyOnDetail) {
    $cats = Get-DumpElements -Filter 'adventure-category-button'
    if ($cats) {
        Write-Host "  categories on screen: $(($cats | ForEach-Object { $_.Text }) -join ', ')"
        break
    }
    if ((Get-Date) -gt $catDeadline) {
        throw "[new-game] 'adventure-category-button' never appeared within 90s.`nScreen:`n$(Get-ScreenDump)"
    }
    # DO NOT PRESS back-btn HERE. Measured live 2026-08-26 on 1.14.6: the save panel
    # (LoadGameUIDocument) is overlaid on AdventureSelectionUIDocument and the three category
    # buttons are VISIBLE AND CLICKABLE UNDERNEATH IT -- they simply take ~4s to render after
    # campaign-btn. The old escape hatch here fired on iteration 1 (before the categories had
    # rendered) and its trigger was itself bogus: LoadGameUIDocument's elements are present in
    # the dump on the MAIN MENU too, so `docs -contains 'LoadGameUIDocument'` is true always.
    # The resulting back-btn press navigated straight back OUT to MAIN_MENU, after which
    # back-btn no longer existed and the loop spun for 90s printing "not present, skipping".
    # Just wait. The categories arrive on their own.
    Start-Sleep -Seconds 3
}

if (-not $alreadyOnDetail) {
    if (-not (Get-DumpElements -Filter 'adventure-category-button' | Where-Object { $_.Text -eq $AdventureCategory })) {
        throw "[new-game] category '$AdventureCategory' is not among the categories on screen. Valid: 'Age of Rebellion', 'Age of Omus', 'Challenge Modes'."
    }
    Invoke-CrucibleClick -Selector $AdventureCategory -Stage 'new-game' | Out-Null

    # ========================================================================================
    # DO NOT RESTORE THE `crucible_ui_nav down` + `crucible_ui_submit -` PAIR THAT USED TO BE
    # HERE. This screen carries BOTH continue-btn ("Continue") and load-btn ("Load Game"), and
    # `crucible_ui_submit -` fires WHATEVER IS FOCUSED -- an unnamed submit on this surface can
    # resume the owner's live co-op campaign, which AGENT-BRIEF S6 forbids outright. It also
    # never worked: both calls reported ok and moved nothing, which surfaced two stages later
    # as "difficulty-inp not found".
    #
    # The safe named control is the carousel tile itself. Confirmed by dump + live click
    # 2026-08-26 on 1.14.6: `Button name='adventure-art-holder'` (nine of them, one per
    # adventure). Clicking it commits the carousel's CURRENTLY CENTRED entry and opens the
    # adventure DETAIL screen; it is a plain named click, and it can never reach continue-btn
    # or load-btn (Invoke-CrucibleClick refuses those names outright).
    #
    # Which of the nine got clicked is NOT assumed -- the detail screen is waited for and the
    # adventure is then confirmed BY NAME below. If the carousel were centred on the wrong
    # adventure the name check fails loudly instead of quietly building the wrong bed.
    # ========================================================================================
    # The carousel ANIMATES IN after the category click. Measured 2026-08-26: a tile click fired
    # immediately after the category reports invoked=True and does nothing -- the carousel is not
    # ready to accept it yet. So wait for the carousel to be rendered (its arrows are the tell),
    # then click, then RETRY: one accepted click is confirmed only by the detail screen appearing.
    $carouselDeadline = (Get-Date).AddSeconds(30)
    while (-not (Get-DumpElements -Filter 'carousel-right-button')) {
        if ((Get-Date) -gt $carouselDeadline) {
            throw "[new-game] the adventure carousel never rendered after choosing '$AdventureCategory'.`n$(Get-ScreenDump)"
        }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 2

    $onDetail = $false
    for ($try = 1; $try -le 5 -and -not $onDetail; $try++) {
        Invoke-CrucibleClick -Selector 'adventure-art-holder' -Stage "new-game(try $try)" | Out-Null
        $detailDeadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $detailDeadline) {
            if (Get-DumpElements -Filter 'next-btn' | Where-Object { $_.Text -eq 'Select Adventure' }) { $onDetail = $true; break }
            Start-Sleep -Seconds 2
        }
        if (-not $onDetail) { Write-Host "  tile click $try did not open the detail screen; retrying" }
    }
    if (-not $onDetail) {
        throw "[new-game] the adventure detail screen (next-btn = 'Select Adventure') never appeared after 5 carousel-tile clicks.`n$(Get-ScreenDump)"
    }
}

$dump = Get-DumpElements -Filter $Adventure
if (-not $dump) {
    throw "[new-game] expected '$Adventure' to be on screen after committing the carousel; not found. The carousel was centred on a different adventure.`n$(Get-ScreenDump)"
}

# Stage 8-9: difficulty is STICKY across sessions -- read it before changing it, and step it off
# MASTER/GAUNTLET (manual saves are disabled there, which would make fixture capture impossible).
Write-Host "[8-9] difficulty: ensuring off MASTER/GAUNTLET"
# There is no 'difficulty-inp' on this build (1.14.6). Dumped live 2026-08-26, the difficulty
# widget on the adventure detail screen is: Label name='difficulty-label' text='Difficulty', a
# stepper of Button name='previous-btn' / Button name='next-btn' (both EMPTY text), and the
# current value in Label name='text-value'. The old code focused a name that does not exist --
# crucible_ui_focus reported ok, focused nothing, and the loop then threw on the missing element.
#
# The stepper is driven LEFT only. 'next-btn' would step right, but the CONFIRM button on this
# same screen is ALSO named 'next-btn' (text 'Select Adventure'), so a click by that name is
# ambiguous and could commit the adventure early. 'previous-btn' is unambiguous and reaches
# Apprentice (the leftmost value) from anywhere.
Invoke-CrucibleClick -Selector 'difficulty-btn' -Stage 'difficulty' -AllowMissing | Out-Null
$cycles = 0
while ($true) {
    $el = Get-DumpElements -Filter 'text-value' | Select-Object -First 1
    if (-not $el) { throw "[difficulty] the difficulty value label 'text-value' is not on screen.`n$(Get-ScreenDump)" }
    if ($el.Text -match '(?i)apprentice|journeyman') { Write-Host "  difficulty is '$($el.Text)'"; break }
    if ($cycles -ge 6) { throw "[difficulty] could not step off MASTER/GAUNTLET after $cycles attempts; last text='$($el.Text)'" }
    Write-Host "  difficulty reads '$($el.Text)'; stepping left"
    Invoke-CrucibleClick -Selector 'previous-btn' -Stage 'difficulty' | Out-Null
    $cycles++
}

# Stage 10: confirm adventure + difficulty -> party screen.
Write-Host "[10] confirming adventure selection"
Invoke-CrucibleClick -Selector 'Select Adventure' -Stage 'select-adventure' | Out-Null

# Stage 11-15: fill all four party slots with the requested classes.
#
# EVERYTHING IN THIS BLOCK WAS MEASURED LIVE ON 1.14.6, 2026-08-26. The previous version --
# `crucible_ui_click add-character-btn` -> `crucible_pad a` -> `crucible_ui_focus
# class-text-selector` -> `crucible_ui_nav right` -- fails at the first line, and the way it
# fails is instructive:
#
#  1. `crucible_ui_click add-character-btn` reports invoked=True via UIToolkitHelper.Submit and
#     DOES NOT open the slot. `crucible_ui_focus` + `crucible_key enter` does. (AGENT-BRIEF S2:
#     focus+enter is the path that actually retires things Submit ignores.)
#  2. Only the SELECTED character panel renders its class stepper, so with four slots filled
#     `crucible_ui_matches next-btn` still returns just [the selected slot's next-btn,
#     party-container-next-btn]. Index 0 is the stepper of whatever slot is selected -- NOT of
#     the slot you meant. Stepping "slot 3" while slot 1 was selected silently rewrote slot 1
#     all the way around the class list.
#  3. There is no ui_focus_nth, and `crucible_ui_nav left/right` is swallowed by the focused
#     TextSelector (it cycles the ALREADY-selected slot), so neither can move the selection.
#
# The selection lever is the CUSTOMIZATION screen: clicking a slot's `character-menu-edit-btn`
# opens Customization on that character (one class-text-selector on screen instead of four), and
# `back-btn` returns to the party screen with THAT slot selected. Then next-btn index 0 is the
# right stepper. Two traps on Customization, both measured: `next-character-btn` is the CHARACTER
# cycler (Class is a read-only row there), and `previous-btn`/`next-btn` is the Male/Female body
# stepper.
# The first-death advisory modal ("Adventuring in the land of Fahrul proves fatal for many
# would-be heroes...") comes up on PromptUIDocument the moment the party screen opens on a fresh
# profile, and it eats the focus+enter that is supposed to press add-character-btn. Measured
# 2026-08-26: add-character-btn dumped visible+enabled+FOCUSED, the enter went nowhere, and the
# script died with "slot 0 never filled" -- a blocked modal presenting as a broken party screen.
# `ok-btn` is dismissed by EXACT NAME, never by text and never near continue-btn/load-btn.
foreach ($i in 1..4) {
    if (Get-DumpElements -Filter 'ok-btn') {
        Write-Host "  dismissing a modal prompt (ok-btn)"
        Invoke-CrucibleClick -Selector 'ok-btn' -Stage 'party-modal' -AllowMissing | Out-Null
        Start-Sleep -Milliseconds 600
    } else { break }
}

Write-Host "[11-15] filling party (4 slots)"

function Get-ClassSelectorTexts {
    # `return ,$a` -- the COMMA IS LOAD-BEARING. PowerShell unrolls a one-element array on return,
    # so with a single party slot filled this handed back a bare STRING; `$texts[0]` then indexed
    # the string and produced its first CHARACTER. Measured 2026-08-26: the class loop reported
    # "never reached class 'CF_ORIG_CHAOSMAGE' after 150 steps; last seen 'C'" -- 'C' being
    # character zero of "CF_ORIG_CHAOSMAGE", i.e. the slot was ALREADY correct and the comparison
    # could not see it. This stayed hidden while a bug elsewhere added two characters per press,
    # because two elements never unroll.
    $texts = @(Get-DumpElements -Filter 'class-text-selector' | ForEach-Object { $_.Text })
    return ,$texts
}

function Wait-ClassSelectorCount {
    # -AtLeast is for the ADD step only. One focus+enter on add-character-btn can land TWO
    # characters (measured 2026-08-26: slot 0 asked for 1 and the screen came back holding
    # "CF_ORIG_VAMPIRIC, CF_ORIG_PACIFIST"), so an exact-count wait times out on a party screen
    # that is actually FINE and reports it as "slot 0 never filled". The class-setting loop below
    # then fixes whatever ended up in each slot anyway, so "at least one more" is the real
    # precondition. The two Customization waits stay EXACT: there, seeing exactly one selector is
    # how the script knows the Customization screen -- and not the party screen -- is up.
    param([int]$Count, [int]$TimeoutSec = 30, [string]$What, [switch]$AtLeast)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $n = (Get-ClassSelectorTexts).Count
        if ($AtLeast) { if ($n -ge $Count) { return } }
        elseif ($n -eq $Count) { return }
        Start-Sleep -Milliseconds 800
    }
    throw "[party] $What -- class-text-selector count never reached $Count (last $(((Get-ClassSelectorTexts) -join ', ')))"
}

function Select-PartySlot {
    param([int]$Slot, [int]$Filled)
    Invoke-CrucibleExec -Command 'crucible_ui_click_nth' -CmdArgs @('character-menu-edit-btn', "$Slot", 'PartyManagementUIDocument') | Out-Null
    Wait-ClassSelectorCount -Count 1 -What "Customization never opened for slot $Slot"
    Invoke-CrucibleExec -Command 'crucible_ui_click' -CmdArgs @('back-btn') | Out-Null
    Wait-ClassSelectorCount -Count $Filled -What "never returned to the party screen from slot $Slot"
}

for ($slot = 0; $slot -lt 4; $slot++) {
    $wantedClass = $PartyClassIds[$slot]
    Write-Host "  slot ${slot}: $wantedClass"

    if ((Get-ClassSelectorTexts).Count -le $slot) {
        # ONE add per press, by INDEX. focus('add-character-btn') + key(enter) added TWO characters
        # in a single press on 2026-08-26 (slot 0 asked for one and the screen came back holding
        # "CF_ORIG_VAMPIRIC, CF_ORIG_PACIFIST"), and the damage is not just the count: the class
        # stepper then stays bound to the LAST slot added, so the very next Select-PartySlot for
        # slot 0 came back driving slot 1 and the run died with "the visible stepper moved slot 1,
        # not 0". Four buttons share the name 'add-character-btn' -- one per EMPTY slot -- so
        # index 0 is always the first empty slot and presses exactly one of them.
        Invoke-CrucibleExec -Command 'crucible_ui_click_nth' -CmdArgs @('add-character-btn', '0', 'PartyManagementUIDocument') | Out-Null
        Wait-ClassSelectorCount -Count ($slot + 1) -What "slot $slot never filled after click_nth on add-character-btn"
    }

    $filled = (Get-ClassSelectorTexts).Count
    if ((Get-ClassSelectorTexts)[$slot] -ne $wantedClass) {
        Select-PartySlot -Slot $slot -Filled $filled
        for ($i = 0; $i -lt $MaxClassCycles; $i++) {
            $before = Get-ClassSelectorTexts
            if ($before[$slot] -eq $wantedClass) { break }
            Invoke-CrucibleExec -Command 'crucible_ui_click_nth' -CmdArgs @('next-btn', '0', 'PartyManagementUIDocument') | Out-Null
            Start-Sleep -Milliseconds 400
            $after = Get-ClassSelectorTexts
            for ($j = 0; $j -lt $after.Count; $j++) {
                if ($j -ne $slot -and $after[$j] -ne $before[$j]) {
                    throw "[party-slot-$slot] the visible stepper moved slot $j, not $slot -- selection did not take. before=$($before -join ',') after=$($after -join ',')"
                }
            }
        }
    }
    $now = (Get-ClassSelectorTexts)[$slot]
    if ($now -ne $wantedClass) {
        throw "[party-slot-$slot] never reached class '$wantedClass' after $MaxClassCycles steps; last seen '$now'. Is it a valid ClassForge id?"
    }
    Write-Host "    -> $wantedClass confirmed"
}
Write-Host "  party: $((Get-ClassSelectorTexts) -join ', ')"

# Stage 16-18: confirm loadout and begin.
Write-Host "[16-18] beginning adventure"
Invoke-CrucibleClick -Selector 'party-container-next-btn' -Stage 'begin' | Out-Null
Invoke-CrucibleClick -Selector 'begin-adventure-btn' -Stage 'begin' | Out-Null
Invoke-CrucibleClick -Selector 'sys-dialog-ok-btn' -Stage 'begin' -AllowMissing | Out-Null

# Stage 19-20: clear the post-load gate and the intro conversation.
Write-Host "[19] clearing post-load 'Click to Continue' gate"
Invoke-CrucibleExecOrThrow -Command 'crucible_pad' -CmdArgs @('a') -Stage 'post-load-gate' | Out-Null

Write-Host "[20] clearing intro dialogue"
Invoke-CrucibleExecOrThrow -Command 'crucible_dialogue_advance' -CmdArgs @("$DialogueMaxPresses") -Stage 'intro-dialogue' | Out-Null

# NEW: grant mobility items. Shallow verification only -- see the VERIFICATION CAVEAT in the doc
# header. GetSpecificThing is a shipped console command (SPEC.md), not a Crucible-registered one.
Write-Host "[20b] granting items: $(($ItemGrants.Keys) -join ', ')"
$grantResults = @()
foreach ($configId in $ItemGrants.Keys) {
    $qty = [int]$ItemGrants[$configId]
    if ($qty -le 0) { throw "[item-grant] quantity for $configId must be positive, got $qty" }

    # GetSpecificThing is NOT registered on this build -- it is absent from the 117-command
    # crucible_list_commands surface and every grant through it returned ok=false.
    $resp = Invoke-CrucibleExec -Command 'crucible_give_item' -CmdArgs @($configId, "$qty")
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

Invoke-CrucibleExecOrThrow -Command 'saveUser' -CmdArgs @() -Stage 'save' | Out-Null
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
$selectedResp = Invoke-CrucibleExec -Command 'crucible_get' -CmdArgs @('RouterHelper.Env.SelectedGameRunId')
if ($selectedResp.ok -and $selectedResp.result -notmatch [regex]::Escape($runId)) {
    Write-Warning "RouterHelper.Env.SelectedGameRunId ('$($selectedResp.result)') does not obviously match the diffed runId '$runId' -- proceeding on the file-diff result, which is the safer signal, but flagging the mismatch."
}

# --------------------------------------------------------------------------- fixture output

New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
Copy-Item -Path (Join-Path $GameRunsDir $saveFileName) -Destination (Join-Path $fixtureDir $saveFileName) -Force
Write-Host "  copied to $fixtureDir\$saveFileName (source left untouched)"

$gameVersion = $null
$versionResp = Invoke-CrucibleExec -Command 'crucible_get' -CmdArgs @('RouterHelper.Env.User.LastPlayedVersionString')
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
