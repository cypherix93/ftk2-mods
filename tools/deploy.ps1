# ftk2-mods deploy script — build, stage, install, package, uninstall.
#
# Runs in two modes, auto-detected:
#   REPO mode    (this file at <repo>\tools\deploy.ps1): builds plugins, stages a payload
#                folder, then installs to -GameDir and/or zips a shareable package (-Package).
#   PAYLOAD mode (this file sitting next to payload.json inside an extracted package):
#                installs/uninstalls from the payload only — no repo, no dotnet needed.
#
# The payload (and therefore the zip) is laid out as a GAME-DIRECTORY OVERLAY: every file
# sits at its real path relative to "...\steamapps\common\For The King II". Users can either
# extract the zip straight into the game folder (merge/overwrite, EOR-style — installs ALL
# mods, no manifest) or run install.ps1/install.bat for a selective, manifest-tracked,
# uninstallable install. Keep it that way: install.ps1 assumes payload path == target path.
#
# Every file written to the game directory is recorded in
#   <game>\ftk2mods-deploy-manifest.json
# and any pre-existing file it overwrites is backed up under <game>\ftk2mods-backup\.
# -Uninstall reverses the install from that manifest (per-mod or everything).
#
# Examples:
#   .\tools\deploy.ps1                                   # build + install default mods (devkit, classforge, summoner, wardrobe)
#   .\tools\deploy.ps1 -Mods devkit,classforge           # choose mods
#   .\tools\deploy.ps1 -Package -StageOnly               # build the shareable zip for friends, install nothing
#   .\tools\deploy.ps1 -DryRun                           # show what would happen
#   .\install.ps1 -Uninstall                             # (payload mode) remove all installed mods, restore backups
#   .\install.ps1 -Uninstall -All                        # ...including the BepInEx core files this script installed
#
# Windows PowerShell 5.1 compatible (friends won't have pwsh 7).

[CmdletBinding()]
param(
    [ValidateSet('devkit', 'classforge', 'summoner', 'wardrobe', 'warbrain', 'armory', 'blessings', 'crucible')]
    [string[]]$Mods = @('devkit', 'classforge', 'summoner', 'wardrobe'),

    [string]$GameDir = '',

    [switch]$Uninstall,     # reverse a previous install using the manifest
    [switch]$All,           # with -Uninstall: also remove BepInEx core files we installed
    [switch]$DryRun,        # print actions, write nothing
    [switch]$SkipBuild,     # repo mode: reuse existing bin\Release outputs
    [switch]$Package,       # repo mode: zip the staged payload for distribution
    [switch]$StageOnly,     # repo mode: stage/package but do not touch the game dir
    [switch]$NoBepInEx,     # never install BepInEx core files
    [switch]$IncludeBaldurs, # stage the CF_PACK_BALDURS demo pack (off by default: localization gaps)

    # Where BepInEx 5.4.23 core files come from when staging (repo mode).
    [string]$BepInExSource = 'D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN'
)

$ErrorActionPreference = 'Stop'
$ScriptFullPath = $MyInvocation.MyCommand.Path
$ScriptDir = Split-Path -Parent $ScriptFullPath
$ExplicitMods = $PSBoundParameters.ContainsKey('Mods')

# ---------------------------------------------------------------- mode detect
$PayloadMode = Test-Path (Join-Path $ScriptDir 'payload.json')
if ($PayloadMode) {
    $PayloadDir = $ScriptDir
    $RepoRoot = $null
} else {
    $RepoRoot = Split-Path -Parent $ScriptDir   # tools\ -> repo root
    if (-not (Test-Path (Join-Path $RepoRoot 'FTK2.DevKit'))) {
        throw "Can't find repo root (no FTK2.DevKit next to tools\) and no payload.json next to script. Nothing to do."
    }
    $PayloadDir = Join-Path $RepoRoot 'tools\out\deploy\payload'
}

function Log([string]$msg)  { Write-Host $msg }
function Act([string]$msg)  { if ($DryRun) { Write-Host "[dry-run] $msg" } else { Write-Host $msg } }

# ---------------------------------------------------------------- game dir
function Resolve-GameDir {
    param([string]$Requested)
    $candidates = @()
    if ($Requested) { $candidates += $Requested }
    $candidates += 'E:\Games\Steam\steamapps\common\For The King II'
    $candidates += 'C:\Program Files (x86)\Steam\steamapps\common\For The King II'
    $candidates += 'C:\Program Files\Steam\steamapps\common\For The King II'
    # Join-Path THROWS DriveNotFoundException on a candidate whose drive is absent, which would
    # abort the sweep on the FIRST dead candidate and never reach a valid later one. A candidate
    # list exists precisely so unreachable entries are skipped, so each probe is guarded.
    foreach ($c in $candidates) {
        $probe = ''
        try { $probe = Join-Path $c 'For The King II_Data\Managed\FTK2.dll' } catch { continue }
        if ($probe -and (Test-Path $probe)) { return $c }
    }
    if ($Requested) {
        throw "Game not found at '$Requested' (need For The King II_Data\Managed\FTK2.dll). Pass -GameDir <path to For The King II>."
    }
    throw "Couldn't auto-detect the game. Pass -GameDir 'X:\...\steamapps\common\For The King II'."
}

# ---------------------------------------------------------------- mod table
# sub : the mod's game-relative path. It is BOTH where the mod is staged inside the payload
#       and where it lands under the game root — the payload is a game-directory overlay,
#       which is what makes the packaged zip extract-into-game-folder installable.
$ModDefs = [ordered]@{
    devkit = @{
        sub  = 'BepInEx\plugins\ftk2mods.devkit'
        note = 'MP parity engine — required by classforge/summoner parity registration'
    }
    classforge = @{
        sub  = 'BepInEx\plugins\ftk2mods.classforge'
        note = '31 EOR classes + 3 Baldur''s classes, 20 traits, 48 skill recipes'
    }
    summoner = @{
        sub  = 'BepInEx\plugins\ftk2mods.summoner'
        note = '200 EOR mercs/pets in recruitment'
    }
    wardrobe = @{
        sub  = 'BepInEx\plugins\ftk2mods.wardrobe'
        note = 'all non-DLC cosmetics + class models selectable at character creation (pure client)'
    }
    warbrain = @{
        sub  = 'BepInEx\plugins\FTK2.WarBrain'
        note = 'enemy battle AI — ALL peers need identical files+config or MP desyncs'
    }
    armory = @{
        sub  = 'For The King II_Data\StreamingAssets\Assets\Configs\JSON~\Things'
        note = '533 items via game-owned config folder (no EOR visual-fallback layer: some items may show placeholder/no art)'
    }
    blessings = @{
        sub  = 'BepInEx\plugins\ftk2mods.blessings'
        note = 'Risky Blessings (15-blessing EOR table). Needs classforge installed AND its ' +
               '[Packs] AdditionalRoots pointed at BepInEx\plugins\ftk2mods.blessings\ClassPacks ' +
               'so the BLSS_PACK_EOR_BLESSINGS content pack is discovered (off by default: [Blessings] Mode=Disabled).'
    }
    crucible = @{
        payloadSub = 'plugins\ftk2mods.crucible'
        targetSub  = 'BepInEx\plugins\ftk2mods.crucible'
        note       = 'DEV TOOLING ONLY — testing/automation harness (console, screenshots, loopback RPC, MCP). ' +
                     'Never in the default set and inert until [General] Enabled=true.'
    }
}

# BepInEx core pieces, game-relative — staged at these exact paths too. The core is installed
# by install.ps1 only when the game has no BepInEx (copy-paste installs simply merge it in).
$BepInExCoreItems = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx\core')

# ---------------------------------------------------------------- staging (repo mode)
function Copy-Tree([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force $To | Out-Null
    Copy-Item -Path (Join-Path $From '*') -Destination $To -Recurse -Force
}

function Stage-Payload {
    Log "== Staging payload -> $PayloadDir"
    if (Test-Path $PayloadDir) { Remove-Item -Recurse -Force $PayloadDir }
    New-Item -ItemType Directory -Force $PayloadDir | Out-Null

    $refs = Join-Path $RepoRoot 'tools\bin\refs'
    if (-not $SkipBuild) {
        Log '== Building plugins (Release)'
        $builds = @(
            @('FTK2.DevKit\src\DevKit.Plugin',         $true),
            @('FTK2.ClassForge\src\ClassForge.Plugin', $true),
            @('FTK2.Summoner\src\Summoner.Plugin',     $false),
            @('FTK2.Wardrobe\src\Wardrobe.Plugin',     $false),
            @('FTK2.WarBrain\src\WarBrain.Plugin',     $true),
            @('FTK2.Blessings\src\Blessings.Plugin',   $false),
            # $false: Crucible references UnityEngine.ScreenCaptureModule/InputLegacyModule, which the
            # tools\bin\refs snapshot does not carry — it builds against the live game's Managed dir.
            @('FTK2.Crucible\src\Crucible.Plugin',     $false)
        )
        foreach ($b in $builds) {
            $proj = Join-Path $RepoRoot $b[0]
            $args = @('build', $proj, '-c', 'Release', '--nologo', '-v', 'q')
            if ($b[1]) { $args += @("-p:ManagedDir=$refs", "-p:BepInExDir=$refs") }
            & dotnet @args
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $($b[0])" }
        }
    }

    # BepInEx core (bootstrap files + core folder only — never the source package's plugins),
    # staged at its REAL game-relative paths so the zip overlays cleanly.
    # Join-Path THROWS on a path whose drive does not exist (DriveNotFoundException), so a
    # stale machine-specific default aborts the whole deploy instead of degrading to the
    # warning below. Resolve defensively: an unreachable source must be a miss, not a crash.
    $bepInExCore = ''
    try { $bepInExCore = Join-Path $BepInExSource 'BepInEx\core\BepInEx.dll' } catch { $bepInExCore = '' }
    if ($bepInExCore -and (Test-Path $bepInExCore)) {
        New-Item -ItemType Directory -Force (Join-Path $PayloadDir 'BepInEx') | Out-Null
        Copy-Item (Join-Path $BepInExSource 'winhttp.dll')          $PayloadDir
        Copy-Item (Join-Path $BepInExSource 'doorstop_config.ini')  $PayloadDir
        if (Test-Path (Join-Path $BepInExSource '.doorstop_version')) {
            Copy-Item (Join-Path $BepInExSource '.doorstop_version') $PayloadDir
        }
        Copy-Item (Join-Path $BepInExSource 'BepInEx\core') (Join-Path $PayloadDir 'BepInEx') -Recurse
    } else {
        Log "WARNING: BepInEx source not found at '$BepInExSource' — payload will not carry BepInEx core. Installs will require BepInEx 5.4.23 to be present already."
    }

    # devkit: plugin+core dlls side by side, data\ subfolder next to the dll
    # (ParityCoordinator hashes <plugin>\data\ — flattening the files produces an empty dataHash
    # and a guaranteed parity mismatch on every peer)
    $d = Join-Path $PayloadDir $ModDefs.devkit.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.DevKit\src\DevKit.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.DevKit\data') (Join-Path $d 'data')

    # classforge: all three dlls + ClassPacks at plugin-folder root (loader expects <plugin>\ClassPacks\)
    $d = Join-Path $PayloadDir $ModDefs.classforge.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.ClassForge\src\ClassForge.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.ClassForge\data\ClassPacks') (Join-Path $d 'ClassPacks')
    if (-not $IncludeBaldurs) {
        # Demo/crossover pack parked by default (2026-08-05): its granted skills lack the
        # SKILL_CF_* / UI_ENCYCLOPEDIA_* localization entries the loadout panel renders.
        Remove-Item -Recurse -Force (Join-Path $d 'ClassPacks\CF_PACK_BALDURS') -ErrorAction SilentlyContinue
    }

    # summoner: dlls + data\FollowerPacks (loader expects <plugin>\data\FollowerPacks\)
    $d = Join-Path $PayloadDir $ModDefs.summoner.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.Summoner\src\Summoner.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.Summoner\data') (Join-Path $d 'data')

    # wardrobe: single plugin dll, no data
    $d = Join-Path $PayloadDir $ModDefs.wardrobe.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.Wardrobe\src\Wardrobe.Plugin\bin\Release\net472\*.dll') $d

    # warbrain: dlls + data next to dll
    $d = Join-Path $PayloadDir $ModDefs.warbrain.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.WarBrain\src\WarBrain.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.WarBrain\data') (Join-Path $d 'data')

    # crucible: plugin+core dlls side by side, data\ subfolder, and the MCP server next to them so
    # the harness ships as one self-contained folder (the MCP server is plain Node, no npm install)
    $d = Join-Path $PayloadDir $ModDefs.crucible.payloadSub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.Crucible\src\Crucible.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.Crucible\data') (Join-Path $d 'data')
    Copy-Tree (Join-Path $RepoRoot 'FTK2.Crucible\mcp')  (Join-Path $d 'mcp')

    # armory: data-only json packs
    $d = Join-Path $PayloadDir $ModDefs.armory.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.Armory\data\Things\*.json') $d

    # blessings: plugin dll + its own ClassPacks\BLSS_PACK_EOR_BLESSINGS\ (same root layout as
    # ClassForge's own <plugin>\ClassPacks\ -- ClassForge does not own this pack, so it is discovered
    # via [Packs] AdditionalRoots pointed at <this plugin's folder>\ClassPacks (see ModDefs.blessings.note
    # and the payload README below). Blessings.Plugin also reads blessings.json straight out of this
    # same staged copy (BlessingsPlugin.LoadRegistry) -- there is exactly one copy of the file on disk,
    # not a plugin-side duplicate plus a pack-side duplicate.
    $d = Join-Path $PayloadDir $ModDefs.blessings.sub
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item (Join-Path $RepoRoot 'FTK2.Blessings\src\Blessings.Plugin\bin\Release\net472\*.dll') $d
    Copy-Tree (Join-Path $RepoRoot 'FTK2.Blessings\data\ClassPacks') (Join-Path $d 'ClassPacks')

    # installer entry points + metadata
    Copy-Item $ScriptFullPath (Join-Path $PayloadDir 'install.ps1')
    @'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
'@ | Set-Content (Join-Path $PayloadDir 'install.bat') -Encoding ascii
    @'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
pause
'@ | Set-Content (Join-Path $PayloadDir 'uninstall.bat') -Encoding ascii

    $meta = @{
        created = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        commit  = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
        mods    = @($ModDefs.Keys)
    }
    $meta | ConvertTo-Json | Set-Content (Join-Path $PayloadDir 'payload.json') -Encoding utf8

    @"
FTK2 mods — install package (built $($meta.created), commit $($meta.commit))
============================================================================

EVERY PLAYER IN A MULTIPLAYER SESSION MUST INSTALL THIS SAME ZIP THE SAME WAY.
Mismatched installs are detected and the mods switch off on the mismatched peer.

OPTION 1 — copy-paste (simplest, installs ALL mods):
    1. Open your game folder:  ...\Steam\steamapps\common\For The King II\
    2. Extract EVERYTHING from this zip into it.
    3. Allow files to merge / overwrite when prompted.
    4. Launch the game.
  The zip is laid out exactly like the game folder, so it drops straight in.
  Note: uninstall.bat can NOT undo a copy-paste install (no manifest is
  written) — to remove the mods, verify game files in Steam and delete the
  BepInEx folder.

OPTION 2 — installer (selective mods, clean uninstall):
    double-click install.bat        (default set: devkit + classforge +
                                     summoner + wardrobe)
  or, from PowerShell, choosing mods:
    .\install.ps1 -Mods devkit,classforge,summoner,wardrobe
    .\install.ps1 -Mods devkit,classforge,summoner,wardrobe,warbrain,armory   # everything
  If your game is not at the default Steam path:
    .\install.ps1 -GameDir "D:\SteamLibrary\steamapps\common\For The King II"
  Uninstall (installer-mode installs only; restores originals from backup):
    double-click uninstall.bat            (removes mods, leaves BepInEx)
    .\install.ps1 -Uninstall -All         (also removes BepInEx core files)

What's in here:
  devkit     - required base: multiplayer safety/parity engine
  classforge - 34 new playable classes, 20 traits, 48 skill effects
  summoner   - 200 new hireable mercs & pets
  wardrobe   - every non-DLC cosmetic + class model selectable at character
               creation, free (visual only, safe to mix)
  warbrain   - (optional) smarter enemy AI. If ANYONE installs this, EVERYONE must.
  armory     - (optional) 533 new items. Installs into game config folder; some
               items may show placeholder art. If anyone installs it, everyone must.
  blessings  - (optional) Risky Blessings: EOR's 15-blessing run-start table.
               Ships in this package either way but stays OFF by default
               ([Blessings] Mode=Disabled). Needs classforge + its [Packs]
               AdditionalRoots pointed at BepInEx\plugins\ftk2mods.blessings\
               ClassPacks (see that mod's own ClassPacks folder after install).

First launch after install: a console window / BepInEx log appears; the first
load takes a little longer. That is normal.
"@ | Set-Content (Join-Path $PayloadDir 'README.txt') -Encoding utf8

    Log "== Payload staged ($((Get-ChildItem -Recurse -File $PayloadDir | Measure-Object).Count) files)"
}

# ---------------------------------------------------------------- manifest helpers
function Get-Manifest([string]$Game) {
    $p = Join-Path $Game 'ftk2mods-deploy-manifest.json'
    if (Test-Path $p) {
        $raw = Get-Content $p -Raw | ConvertFrom-Json
        $entries = @{}
        foreach ($e in $raw.entries) { $entries[$e.rel] = @{ rel = $e.rel; mod = $e.mod; backup = $e.backup } }
        return $entries
    }
    return @{}
}

function Save-Manifest([string]$Game, [hashtable]$Entries) {
    $p = Join-Path $Game 'ftk2mods-deploy-manifest.json'
    if ($Entries.Count -eq 0) {
        if (Test-Path $p) { Remove-Item $p }
        return
    }
    @{ updated = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
       entries = @($Entries.Values | Sort-Object { $_.rel }) } |
        ConvertTo-Json -Depth 5 | Set-Content $p -Encoding utf8
}

# ---------------------------------------------------------------- install
function Install-Files {
    param([string]$Game, [string]$FromDir, [string]$TargetSub, [string]$ModName, [hashtable]$Manifest, [string]$BackupRoot)
    # $FromDir may also be a single FILE (BepInEx bootstrap pieces live at the payload root).
    if (Test-Path $FromDir -PathType Leaf) {
        $files = @(Get-Item $FromDir)
        $base = Split-Path -Parent $FromDir
    } else {
        $files = Get-ChildItem -Recurse -File $FromDir
        $base = $FromDir
    }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($base.Length + 1)
        if ($TargetSub) { $rel = Join-Path $TargetSub $rel }
        $dest = Join-Path $Game $rel
        # Skip byte-identical files: a reinstall while the game is running would otherwise die on
        # its memory-mapped (unchanged) DLLs before ever reaching the changed data files.
        if ((Test-Path $dest) -and $Manifest.ContainsKey($rel) -and
            (Get-FileHash $dest).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }
        $backup = $null
        if ($Manifest.ContainsKey($rel)) {
            # re-install of a file we own: keep the ORIGINAL backup reference
            $backup = $Manifest[$rel].backup
        } elseif (Test-Path $dest) {
            $backup = Join-Path 'ftk2mods-backup' $rel
            Act "backup   $rel"
            if (-not $DryRun) {
                $bdir = Split-Path -Parent (Join-Path $Game $backup)
                New-Item -ItemType Directory -Force $bdir | Out-Null
                Copy-Item $dest (Join-Path $Game $backup) -Force
            }
        }
        Act "install  $rel"
        if (-not $DryRun) {
            New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
            Copy-Item $f.FullName $dest -Force
            $Manifest[$rel] = @{ rel = $rel; mod = $ModName; backup = $backup }
        }
    }
}

function Do-Install {
    $game = Resolve-GameDir $GameDir
    Log "== Installing to: $game"
    $manifest = Get-Manifest $game

    # BepInEx core first — staged at real game-relative paths (see $BepInExCoreItems)
    $bepPresent = Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll')
    if (-not $bepPresent -and -not $NoBepInEx) {
        if (-not (Test-Path (Join-Path $PayloadDir 'BepInEx\core\BepInEx.dll'))) {
            throw 'Game has no BepInEx and this payload carries none. Install BepInEx 5.4.23 first or restage with a valid -BepInExSource.'
        }
        Log '-- BepInEx core (game had none)'
        foreach ($item in $BepInExCoreItems) {
            $src = Join-Path $PayloadDir $item
            if (-not (Test-Path $src)) { continue }
            $sub = if (Test-Path $src -PathType Leaf) { '' } else { $item }
            Install-Files -Game $game -FromDir $src -TargetSub $sub -ModName 'bepinex' -Manifest $manifest -BackupRoot $game
        }
    } elseif ($bepPresent) {
        Log '-- BepInEx already present, leaving it alone'
    }

    foreach ($m in $Mods) {
        $def = $ModDefs[$m]
        # Most mods stage at their real game-relative path, so one 'sub' serves as both source and
        # target. Crucible deliberately does NOT: it stages under 'plugins' so a copy-paste extract
        # into the game folder cannot install dev tooling, and only a manifest install remaps it to
        # BepInEx\plugins\. Reading only .sub gave crucible a NULL source and target, which made
        # Join-Path return the payload ROOT and Install-Files' Substring trim one character too many
        # -- so the whole payload installed into garbage-named siblings ('epInEx', 'lugins')
        # while the mod itself never updated.
        $payloadSub = if ($def.payloadSub) { $def.payloadSub } else { $def.sub }
        $targetSub  = if ($def.targetSub)  { $def.targetSub }  else { $def.sub }
        $src = Join-Path $PayloadDir $payloadSub
        if (-not (Test-Path $src)) { Log "-- $m : NOT IN PAYLOAD, skipped"; continue }
        if ($m -eq 'armory') {
            $thingsDir = Join-Path $game $targetSub
            if (-not (Test-Path $thingsDir)) { Log "-- armory: game Things config folder not found at '$targetSub' - SKIPPED (game layout drifted?)"; continue }
        }
        Log "-- $m : $($def.note)"
        Install-Files -Game $game -FromDir $src -TargetSub $targetSub -ModName $m -Manifest $manifest -BackupRoot $game
    }

    if (-not $DryRun) { Save-Manifest $game $manifest }
    Log '== Install done.'
    Log "   Manifest: $(Join-Path $game 'ftk2mods-deploy-manifest.json')"
    Log '   Reverse anytime with: -Uninstall (add -All to remove BepInEx too)'
}

# ---------------------------------------------------------------- uninstall
function Do-Uninstall {
    $game = Resolve-GameDir $GameDir
    $manifest = Get-Manifest $game
    if ($manifest.Count -eq 0) { Log "Nothing to uninstall (no manifest at $game)."; return }

    # which mods to remove: explicit -Mods wins; otherwise everything except bepinex (unless -All)
    $targets = @($manifest.Values | ForEach-Object { $_.mod } | Sort-Object -Unique)
    if ($ExplicitMods) { $targets = $Mods }
    elseif (-not $All) { $targets = $targets | Where-Object { $_ -ne 'bepinex' } }

    Log "== Uninstalling from: $game  (mods: $($targets -join ', '))"
    $dirs = New-Object System.Collections.Generic.HashSet[string]
    foreach ($e in @($manifest.Values)) {
        if ($targets -notcontains $e.mod) { continue }
        $dest = Join-Path $game $e.rel
        if ($e.backup) {
            Act "restore  $($e.rel)"
            if (-not $DryRun) {
                $b = Join-Path $game $e.backup
                if (Test-Path $b) { Copy-Item $b $dest -Force; Remove-Item $b }
                else { Log "   WARNING: backup missing for $($e.rel), leaving installed file in place" }
            }
        } else {
            Act "remove   $($e.rel)"
            if (-not $DryRun) { Remove-Item $dest -Force -ErrorAction SilentlyContinue }
        }
        [void]$dirs.Add((Split-Path -Parent $dest))
        if (-not $DryRun) { $manifest.Remove($e.rel) }
    }

    if (-not $DryRun) {
        # prune now-empty directories we may have created (walk each up to game root)
        foreach ($d in $dirs) {
            $cur = $d
            while ($cur -and $cur.Length -gt $game.Length) {
                if ((Test-Path $cur) -and -not (Get-ChildItem -Force $cur | Select-Object -First 1)) {
                    Remove-Item $cur -Force
                } else { break }
                $cur = Split-Path -Parent $cur
            }
        }
        # prune empty backup tree
        $broot = Join-Path $game 'ftk2mods-backup'
        if ((Test-Path $broot) -and -not (Get-ChildItem -Recurse -File $broot | Select-Object -First 1)) {
            Remove-Item -Recurse -Force $broot
        }
        Save-Manifest $game $manifest
    }
    Log '== Uninstall done.'
}

# ---------------------------------------------------------------- main
if ($Uninstall) {
    Do-Uninstall
    return
}

if (-not $PayloadMode) {
    Stage-Payload
    if ($Package) {
        # Date+time so every packaged build gets a distinct file name — testers must never be left
        # guessing whether a re-shared zip is actually a newer build than the one they have.
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $commit = & git -C $RepoRoot rev-parse --short HEAD 2>$null
        $zip = Join-Path $RepoRoot "tools\out\deploy\ftk2mods-$stamp-$commit.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Log "== Zipping package -> $zip"
        Compress-Archive -Path (Join-Path $PayloadDir '*') -DestinationPath $zip
        Log "== Package ready: $zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
    }
    if ($StageOnly) { return }
}

Do-Install
