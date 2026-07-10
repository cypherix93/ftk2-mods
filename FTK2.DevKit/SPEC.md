# FTK2.DevKit — SPEC

Plugin GUID: `ftk2mods.devkit` · Content id prefix: `DK_` · Priority: **P0-dev** (build first, alongside FTK2.WarBrain)

---

## 1. Purpose & scope

FTK2.DevKit is modder tooling, not a gameplay mod — **with one exception that is now load-bearing for the
whole repo.** Per `docs/MULTIPLAYER.md` (co-op multiplayer is a hard requirement, project decision
2026-07-10), DevKit hosts the **ParityService**: the shared implementation of MULTIPLAYER.md's R1 ("all
peers run the same mods + the same data"). Every other `ftk2mods.*` mod registers with it and depends on it
to detect desync-causing divergence before it corrupts a session. This makes DevKit's build order and
correctness a hard blocker for every sibling mod's MP posture, not just a nice-to-have dev convenience.

Aside from that, DevKit remains modder tooling: it exists to make every other mod in this repo faster to
build and safer to verify. It provides: JSON hot-reload (so the edit→observe loop in `docs/CONVENTIONS.md`
doesn't require a game restart), dump commands that snapshot the *post-merge* `Configs` registry and live
combat/venue/AI state to disk, a small in-game command console with data-driven macros for sibling mods'
test plans, a shared structured-logging service other mods' engines (WarBrain's decisions, ActionPoints'
spend events, ClassForge/Forge/Questsmith's merge results) can log into, a startup health check that
verifies every Harmony patch target registered across all `ftk2mods.*` plugins actually resolved, and (new,
repo-critical) the **ParityService** that every `ftk2mods.*` mod registers with for cross-peer mod+data
parity verification.

**Deliberately out of scope:** no gameplay balance changes, no content (no new abilities/items/statuses),
no UI beyond a minimal on-screen console/table, no telemetry/phone-home (contrast with EOR's GitHub-issue
auto-filer — DevKit never talks to the network). DevKit has no save-file footprint (see §9).

## 2. Player-facing behavior

"Player" here means the modder/tester running a dev build:

- Press a hotkey (default unbound-safe, configurable, e.g. `F9`) → all JSON configs reload from disk without
  restarting the game. A one-line on-screen toast reports success/failure and elapsed time. If combat is
  active, the hotkey is a no-op with a "blocked: in combat" toast (default-safe behavior, see §3).
- Optionally enable auto-watch mode → a debounced `FileSystemWatcher` on the JSON source folders triggers the
  same reload automatically after edits settle.
- Press a dump hotkey (default `F10`) → writes the current `Configs` registry (or a single named table),
  current combat state, venue/grid layout, and AI decision inputs to timestamped JSON files under a
  configurable dump folder. A toast reports the file paths written.
- Open the console (bound key, default backtick-adjacent) and type `dk_` commands to give items, spawn test
  encounters, force a win/flee, set a stat, grant XP/gold, start a quest, reload configs, or dump a table —
  or type `dk_run_macro <name>` to fire a whole named sequence from `data/Macros.json`.
- Press a log-dump hotkey (default `F11`) → writes the last N buffered structured log entries (from DevKit
  itself and any sibling mod logging through it) to a JSONL file, for post-mortem review of an AI decision
  or an AP spend sequence.
- Press a health-check hotkey (default `F12`, also runs once automatically near startup) → prints a
  one-screen PASS/FAIL table of every registered Harmony patch target across all `ftk2mods.*` plugins.
- In multiplayer, all *mutating* commands (give/spawn/set-stat/win/flee/xp/gold/quest-start/reload-that-
  changes-shared-config) are hard-disabled by default with an on-screen "disabled in MP" message; read-only
  dumps and the health check remain available (see §9).
- **Multiplayer parity, silent when healthy.** On session host/join, every registered `ftk2mods.*` mod's
  `(guid, version, dataHash, enabledFeatures)` is exchanged automatically — no player action required, no
  UI shown if everything matches. On mismatch, a prominent on-screen banner names the exact mod and which
  part diverged (version / data / enabled features), and the affected mod's own features enter SafeMode (or
  the session is blocked, or it's a warn-only note — see `[Multiplayer] OnParityMismatch` in §5). A joining
  client that connects mid-session automatically re-requests a fresh parity snapshot rather than trusting
  stale state.
- Run `dk_dump_parity` (or fire the `parity-check` macro) → prints/writes every currently-registered mod's
  guid/version/dataHash/enabledFeatures and the most recent mismatch result, for MP desync triage — the
  in-game analogue of EOR's "Print Sync-Relevant Data Hash" debug command.

## 3. Architecture

**Engine vs data split.** DevKit's mechanics (hotkey handling, file watching, console parsing, ring-buffer
logging, reflection-based dumping, patch-health aggregation) are pure C#. The only content DevKit loads from
JSON is its own tooling data: `data/Macros.json` (command-sequence aliases) and `data/LogConfig.json`
(log category/level table). Neither file describes gameplay content — they describe *DevKit's own* behavior,
consistent with "engine hardcodes no content."

**Runtime flow:**
1. `Awake()` — read BepInEx config knobs (§5), load `data/LogConfig.json` and `data/Macros.json`, install
   Harmony patches (§6), initialize the ring-buffer logger and the patch-health registry.
2. Near-startup (first `RouterMono.Update` tick, or manually via `F12`/`dk_health_check`) — run the
   patch-target health check once all sibling `ftk2mods.*` plugins have had a chance to register
   (see §6, open question on load-order guarantees).
3. Every frame — poll hotkeys; if auto-watch is enabled, the `FileSystemWatcher` callback (debounced) queues
   a reload request consumed on the main thread.
4. On reload request — check the combat-state gate (see below); if clear, call
   `ConfigsHelper.ReloadConfigs` (full) or `ConfigsHelper.ProcessDirectory`/`ProcessJsonFile` (per-source,
   if the per-source path is verified safe — see §11) and log the result + elapsed time.
5. On console input / macro run — parse `dk_<verb> <args>` tokens, dispatch to the matching command handler,
   enforce the unsafe-commands gate and MP guard (§5, §9) before any mutating handler executes.
6. On dump request — reflect over the `Configs` static class's fields (or the requested single field),
   serialize with the game's own `JsonHelper`/`System.Text.Json`, write to
   `<DumpFolderPath>/configs_<table>_<yyyyMMdd_HHmmss>.json`; combat/venue/AI dumps walk the current
   `CombatState`/entity components the same way (see §4).

**Combat-state gate (safety default for reload).** Hot-reload of the merged `Configs` registry mid-combat
risks a mismatch between what `CombatState`/`CombatComponent`/`AIComponent` instances already captured from
the pre-reload data and what a reloaded `Abilities`/`StatusEffects`/`Characters` table now says (e.g. an
in-flight `CombatDecisionData.Ability` reference, or a `StatusEffectInfo` whose `StatusEffectConfig.Duration`
just changed). **Default: reload is only permitted outside combat.** DevKit tracks a simple `InCombat` flag
flipped by two Postfix hooks (`CombatPhase._addEntityToCombat` → true, `CombatPhase._endCombatAsync` → false)
so this needs no guesswork about which sub-systems are "safe" — it blocks the whole class of risk. Per-file
granularity (only reload the JSON source that actually changed, via `ConfigsHelper.ProcessDirectory`/
`ProcessJsonFile`) is a stretch goal for M3 gated on confirming those methods don't leave `Configs` in a
half-updated state on a parse error (§11).

**MP interaction.** A successful reload (DevKit's own, or any sibling mod's) changes that mod's `dataHash`.
In a detected MP session, every successful reload automatically triggers a fresh ParityService
`FTK2MODS_PARITY_V1` handshake (see "ParityService" below) immediately after it completes. If every peer
applied the identical change, the re-handshake is silent; if not, the mismatch flow fires and the diverged
mod's SafeMode engages until all peers reload the same content — hot-reload in MP is therefore a
coordinate-with-your-peers action, not a solo one (per R5, `docs/MULTIPLAYER.md`).

**State lifecycle.**
- Per-battle: the `InCombat` flag and the AI-decision capture buffer (from the `AIHelper` Postfixes) reset
  on `_endCombatAsync`.
- Per-run: none. DevKit does not read or write `GameRunData`.
- Persistent: none in the save file. BepInEx config (knobs) and the two data JSONs persist on disk as normal
  mod files; the ring-buffer log is in-memory only unless `PersistToDisk` is set in `LogConfig.json`, in
  which case it appends to a rolling log file for the session.

**Structured logging service (shared API).** A static class (e.g. `FTK2Mods.DevKit.DevKitLog`) exposing
`Log(string category, LogLevel level, string message, object data = null)`. Internally: a single ring buffer
of fixed capacity (`RingBufferSize` from `LogConfig.json`) storing `{Timestamp, Category, Level, ModGuid,
Message, Data}` entries; entries below a category's configured level are dropped before insertion. Sibling
mods (WarBrain, ActionPoints, ClassForge, Forge, Questsmith, Runeworks, Summoner, Venue) call this instead of
their own `BepInEx.Logging.ManualLogSource` when they want an entry to be F-key-dumpable and category
filterable across the whole mod stack. Because DevKit and its siblings are separate BepInEx plugins with no
guaranteed load order, the calling convention is a soft dependency: callers resolve
`Type.GetType("FTK2Mods.DevKit.DevKitLog, ftk2mods.devkit")` via reflection and no-op if DevKit isn't loaded,
rather than taking a compile-time reference (open question in §11: whether a tiny shared contracts DLL would
be preferable).

**Patch-target health-check registry.** A static class (e.g. `FTK2Mods.DevKit.PatchRegistry`) with
`Report(string pluginGuid, string targetDescription, System.Reflection.MethodBase resolvedOrNull)`. Every
`ftk2mods.*` plugin calls this once per `AccessTools.Method(...)` lookup it performs for its own Harmony
patches (the exact same "Target found: X" log line convention from `docs/CONVENTIONS.md`, just also mirrored
into a table DevKit can render). DevKit aggregates by plugin GUID and prints one PASS/FAIL row per target on
the health-check trigger. Same reflection-based soft-dependency calling convention as the logging service.

**ParityService (R1 implementation — repo-critical, `docs/MULTIPLAYER.md`).** A static class (e.g.
`FTK2Mods.DevKit.ParityService`) that is the shared enforcement point for MULTIPLAYER.md's R1 ("all peers
run the same mods + the same data"). Every `ftk2mods.*` plugin — including DevKit's own gameplay-adjacent
siblings, not DevKit's dev-tooling itself — registers with it at `Awake()`.

- **Registration API.**
  `Register(string pluginGuid, string version, string dataHash, string[] enabledFeatures, Action<ParityMismatch> onParityFailed = null)`.
  Same reflection-based soft-dependency calling convention as `DevKitLog`/`PatchRegistry` (§3 above): callers
  resolve `Type.GetType("FTK2Mods.DevKit.ParityService, ftk2mods.devkit")` via reflection and no-op if DevKit
  isn't loaded, rather than taking a compile-time reference. This is deliberate — R1 must not become a hard
  build dependency for sibling mods, mirroring the existing `PatchRegistry`/`DevKitLog` soft-dependency
  approach (see open question in §11 about a shared contracts DLL, which now also covers this API).
  `onParityFailed` is the mod's `ParityFailed` callback (see "Mismatch flow" below); a mod that omits it still
  gets the on-screen warning and the policy-driven session effect (SafeMode/Block) but has no chance to react
  itself (e.g. to also disable a feature DevKit doesn't know about by name).
- **`dataHash` computation.** DevKit exposes a helper,
  `ParityService.ComputeDataHash(IEnumerable<string> filePaths, Func<string,bool> excludePredicate = null)`,
  so every mod computes its hash the same way (a per-mod-invented hashing scheme would itself be a parity
  risk). Rule: **SHA-256** over the mod's own data files, with:
  - files enumerated in **sorted order** (ordinal string sort on the path relative to the mod's own folder)
    so peer disk/filesystem ordering can never affect the hash;
  - each file's bytes read as UTF-8 and **line endings normalized** (`\r\n` → `\n`) before hashing, so a
    Windows-vs-non-Windows checkout of the same content still hashes identically;
  - all string comparisons **invariant-culture**;
  - **localization files excluded** by default (matched by a configurable filename/path pattern, e.g.
    `*.lang.json`, `Localization/**`) — text-only files don't affect gameplay state (EOR precedent,
    `docs/MULTIPLAYER.md` R1).
  The resulting hash is a single hex string (`sha256:<64 hex chars>`) the mod passes to `Register`.
- **Transport: `FTK2MODS_PARITY_V1` over `AdventureDirector._handleNetworkAction`.** This is the same
  verified transport EOR's own `EOR_VER/EOR_CFG/EOR_DAT/EOR_SYS/EOR_DEF/EOR_SIG` handshake piggybacks
  (`docs/research/game-code-reference.md` §7). On session host/join, ParityService gathers every mod's
  current registration on the local peer and sends one `FTK2MODS_PARITY_V1` action containing the full list;
  the host aggregates every peer's list and diffs by `pluginGuid` (payload sketch in §4).
- **Mismatch flow.** For each `pluginGuid` present on more than one peer with a differing `version`,
  `dataHash`, or `enabledFeatures` set, ParityService:
  1. Shows a prominent on-screen warning naming the exact mod (by guid, resolved to a display name if the
     mod registered one) and which part diverged (`Version` / `Data` / `Features`).
  2. Invokes that mod's `ParityFailed(ParityMismatch info)` callback, if one was registered, with the kind of
     divergence and the local vs. remote values.
  3. Applies the configured policy (`[Multiplayer] OnParityMismatch`, §5): `WarnOnly` (banner only, session
     continues unmodified), `WarnAndSafeMode` (**default** — banner + the affected mod's SafeMode is engaged,
     per that mod's own §9 SafeMode definition, disabling state-mutating features while keeping
     presentation-only ones per R4), or `Block` (the session refuses to start/continue at all).
- **Late-join re-query: `FTK2MODS_PARITY_REQUEST_V1`.** A client joining an already-running session sends
  this action once connected; the host responds with a fresh `FTK2MODS_PARITY_V1` snapshot of every peer's
  *current* registrations, mirroring EOR's town-snapshot request/response pattern
  (`docs/research/game-code-reference.md` §7) — a late joiner never has to trust stale or assumed state.
- **Hot-reload interaction (R5).** If DevKit's own hot-reload (or any sibling mod's config reload) fires
  while in an MP session, the reloading mod's `dataHash` changes; ParityService automatically re-runs the
  `FTK2MODS_PARITY_V1` handshake immediately afterward. If every peer reloaded the identical change, hashes
  still match and nothing is shown. If they didn't (e.g. only the host had the hotkey pressed), the mismatch
  flow above fires normally — in practice this means an uncoordinated hot-reload in MP almost always lands
  the reloading mod in SafeMode until every peer catches up.
- **Versioning.** The action key is suffixed `_V1`; per `docs/MULTIPLAYER.md`'s guidance, peers on different
  mod *versions* already fail R1 on their own, but the versioned payload keeps the failure diagnosable rather
  than silent. A future breaking payload change ships as `_V2` with `_V1` support carried for one version
  window if feasible.

## 4. Data file formats

### `data/Macros.json`

A dictionary of macro name → definition. Each definition is a description (for the on-screen console) plus
an ordered list of `dk_`-command strings executed in sequence (a failed command logs and continues to the
next, it does not abort the macro — a test macro should be resilient to a missing dependency mod).

```json
{
  "forge-test": {
    "Description": "Give the FTK2.Forge upgrade orb + a disposable test blade to party slot 1, for orb-consumption testing.",
    "RequiresMods": ["ftk2mods.forge"],
    "Commands": [
      "dk_give_item PARTY1 FRG_ORB_WHETSTONE 10",
      "dk_give_item PARTY1 BLADE_IRON 1"
    ]
  },
  "warbrain-arena": {
    "Description": "Spawn a small mixed encounter (melee + caster + support) to exercise WarBrain's tactical scoring across ability categories.",
    "RequiresMods": ["ftk2mods.warbrain"],
    "Commands": [
      "dk_spawn_encounter GOBLIN_GRUNT,GOBLIN_ARCHER,GOBLIN_SHAMAN"
    ]
  },
  "summoner-farm": {
    "Description": "Repeatedly spawn and win a trivial encounter, to farm the FTK2.Summoner kill counter for evolution-threshold testing.",
    "RequiresMods": ["ftk2mods.summoner"],
    "Commands": [
      "dk_spawn_encounter GOBLIN_GRUNT",
      "dk_win_combat",
      "dk_spawn_encounter GOBLIN_GRUNT",
      "dk_win_combat",
      "dk_spawn_encounter GOBLIN_GRUNT",
      "dk_win_combat"
    ]
  }
}
```

Field notes:
- `Description` (string) — shown by `dk_list_macros`.
- `RequiresMods` (string[], optional) — plugin GUIDs this macro assumes are loaded; DevKit checks
  `BepInEx.Bootstrap.Chainloader.PluginInfos` (or equivalent) and prefixes a warning toast if missing, but
  still attempts the commands.
- `Commands` (string[]) — literal `dk_` console command lines, executed in order via the same parser as
  manual console input.
- **Character/party target selectors** (`PARTY1`..`PARTY4`, `ALL_PARTY`, `SELECTED`, `FIRST_ENEMY`,
  `ALL_ENEMIES`) are DevKit's own console DSL, not a game API — resolving them to actual party/entity
  references is an implementation detail behind an open question (§11) about which game-side accessor
  exposes "current party members"/"current combat entities" cleanly.
- The exact ids above (`FRG_ORB_WHETSTONE`, `BLADE_IRON`, `GOBLIN_*`) are **illustrative placeholders**: the
  Forge id is taken verbatim from this spec's assignment brief; the Goblin/blade ids are examples only —
  none of FTK2.Forge/WarBrain/Summoner has a shipped SPEC/id registry yet, and `Characters.json`'s real
  entries were not enumerated by name in `docs/research/data-schemas.md`. Swap in real ids once the sibling
  mod's data ships, or once `Characters.json` is name-searched for a suitable trivial-enemy id family.

### `data/LogConfig.json`

Controls the shared logging service (§3): ring-buffer capacity, optional disk persistence, and a per-category
level table. Categories are free-form strings so any `ftk2mods.*` plugin can register its own without
DevKit needing to know about it in advance (a registry, per `docs/CONVENTIONS.md`'s "data-driven registry"
rule) — unknown categories default to `Info`.

```json
{
  "RingBufferSize": 500,
  "PersistToDisk": false,
  "LogFolderPath": "logs",
  "DumpLastNCount": 200,
  "Categories": {
    "DK_CORE": "Info",
    "DK_CONSOLE": "Info",
    "DK_HOTRELOAD": "Info",
    "DK_HEALTHCHECK": "Info",
    "WARBRAIN_DECISION": "Debug",
    "ACTIONPOINTS_SPEND": "Debug",
    "CLASSFORGE_MERGE": "Info",
    "FORGE_MERGE": "Info",
    "RUNEWORKS_SOCKET": "Info",
    "SUMMONER_EVOLUTION": "Info",
    "QUESTSMITH_TRIGGER": "Info",
    "VENUE_GRID": "Info"
  }
}
```

Field notes:
- `RingBufferSize` (int) — max entries kept in memory across *all* categories combined (oldest evicted first).
- `PersistToDisk` (bool) — if true, every accepted entry is also appended to a rolling file under
  `LogFolderPath` (relative to the dump folder, §5), independent of the F-key dump.
- `DumpLastNCount` (int) — how many of the most recent entries the F-key dump command writes.
- `Categories` (dictionary, string → one of `Debug|Info|Message|Warning|Error|Fatal`, matching
  `BepInEx.Logging.LogLevel` names) — entries logged below a category's configured level are dropped before
  ever entering the ring buffer. Levels are per-category so, e.g., WarBrain's decision log can run at
  `Debug` while general DevKit chatter stays at `Info`, exactly mirroring the `VerboseLogging` knob pattern
  in `docs/CONVENTIONS.md`.

### ParityService wire payloads (not a data file — the `_handleNetworkAction` payload shape)

Unlike `Macros.json`/`LogConfig.json`, these are not loaded from disk; they're the JSON payloads
ParityService sends/receives over `AdventureDirector._handleNetworkAction` (§3, §6). Documented here because
they're still a "data format" sibling mods' authors need to reason about, per this section's remit.

`FTK2MODS_PARITY_V1` (host↔client, sent on session start/join, and again after any hot-reload in MP):

```json
{
  "Action": "FTK2MODS_PARITY_V1",
  "SenderPeerId": "host",
  "Registrations": [
    {
      "Guid": "ftk2mods.warbrain",
      "Version": "1.0.0",
      "DataHash": "sha256:9f2c...a3",
      "EnabledFeatures": ["AIDecisionLogging", "TacticalScoring"]
    },
    {
      "Guid": "ftk2mods.devkit",
      "Version": "1.0.0",
      "DataHash": "sha256:1b7e...c0",
      "EnabledFeatures": []
    }
  ]
}
```

`FTK2MODS_PARITY_REQUEST_V1` (client → host, sent once on connect for a mid-session/late join):

```json
{ "Action": "FTK2MODS_PARITY_REQUEST_V1", "RequestingPeerId": "client-2" }
```

Field notes:
- `Registrations` is every mod currently registered with ParityService **on the sending peer** — DevKit's
  own registration is always included (with an empty `EnabledFeatures` unless DevKit itself grows
  parity-relevant features).
- `DataHash` is always the `sha256:` prefix + 64 lowercase hex chars from `ParityService.ComputeDataHash`
  (§3's hash rules: sorted files, normalized line endings, invariant culture, localization excluded).
- `EnabledFeatures` is caller-defined free-form strings (a mod's own feature-flag names); ParityService
  only compares the set for equality, it doesn't interpret the names.
- On receipt of `FTK2MODS_PARITY_REQUEST_V1`, the host responds with a fresh `FTK2MODS_PARITY_V1` reflecting
  every currently-registered peer's latest state (EOR town-snapshot request/response precedent,
  `docs/research/game-code-reference.md` §7) — never a cached copy from the original session start.

## 5. Knobs

`[General]`
- `Enabled` (bool, `true`) — master switch; DevKit installs no patches and shows no UI if false.
- `VerboseLogging` (bool, `false`) — DevKit's own internal chatter at `LogLevel.Debug`.

`[HotReload]`
- `Hotkey` (KeyboardShortcut, `F9`) — manual reload trigger.
- `AllowDuringCombat` (bool, `false`) — safe default per §3; when false, reload requests during combat are
  rejected with a toast instead of queued.
- `AutoWatchEnabled` (bool, `false`) — enable the `FileSystemWatcher` auto mode.
- `AutoWatchDebounceMs` (int, `750`) — quiet period after the last detected file change before reloading.
- `PerSourceReload` (bool, `false`) — attempt `ConfigsHelper.ProcessDirectory`/`ProcessJsonFile` targeted
  reload instead of full `ReloadConfigs`; gated off by default pending §11 verification.

`[Dump]`
- `DumpFolderPath` (string, `BepInEx/plugins/ftk2mods.devkit/dumps`) — root folder for all dump commands.
- `Hotkey` (KeyboardShortcut, `F10`) — dumps `Configs` (all tables) + combat + venue + AI in one press.

`[Console]`
- `EnableConsoleCommands` (bool, `true`) — master switch for the `dk_` console.
- `ConsoleToggleKey` (KeyboardShortcut, unbound by default) — opens/closes the console input box.
- `UnsafeCommandsEnabled` (bool, `false`) — gate for commands flagged unsafe (win/flee-combat, set-stat,
  spawn-encounter — anything that can corrupt an in-progress run state if misused); off by default.
- `MacrosFilePath` (string, `data/Macros.json`) — reloadable independently of the main config hot-reload.

`[Logging]`
- `LogConfigPath` (string, `data/LogConfig.json`).
- `DumpLastNHotkey` (KeyboardShortcut, `F11`).

`[HealthCheck]`
- `RunOnStartup` (bool, `true`).
- `Hotkey` (KeyboardShortcut, `F12`) — re-run on demand.
- `AlsoWriteDumpFile` (bool, `true`) — additionally write the PASS/FAIL table to the dump folder.

`[Multiplayer]` (renamed from `[MultiplayerGuard]` — now covers both the R5 mutation guard and R1
ParityService policy)
- `DisableMutationsInMP` (bool, `true`) — hard default per §9/R5; cannot be bypassed by `UnsafeCommandsEnabled`
  alone.
- `ForceAllowInMP` (bool, `false`) — explicit, loudly-logged escape hatch for a coordinated dev session where
  every peer runs DevKit and accepts desync risk. Requires `UnsafeCommandsEnabled = true` as well. Even when
  set, unlocked mutating commands execute **host-only** (a client-side `ForceAllowInMP` has no mutation
  effect, only suppresses the local "disabled in MP" toast) and every use logs a **session-visible** warning
  broadcast to all peers, not just DevKit's local log — per R5, the friction is deliberate and shared.
- `OnParityMismatch` (enum: `WarnAndSafeMode` | `WarnOnly` | `Block`, default `WarnAndSafeMode`) — policy
  ParityService applies when the `FTK2MODS_PARITY_V1` handshake finds a divergent mod (§3, §4). Mirrors
  `docs/MULTIPLAYER.md` R1 verbatim: `WarnOnly` shows the banner only; `WarnAndSafeMode` also engages the
  diverged mod's SafeMode; `Block` refuses to start/continue the session.
- `RehandshakeOnHotReload` (bool, `true`) — after any successful hot-reload while in a detected MP session,
  automatically re-run the `FTK2MODS_PARITY_V1` handshake (§3 "MP interaction"/"ParityService"). Turning this
  off is not recommended — it exists only to isolate a suspected handshake bug during DevKit's own
  development.
- `ParityRequestTimeoutMs` (int, `5000`) — how long a late-joining client waits for the host's
  `FTK2MODS_PARITY_V1` reply to its `FTK2MODS_PARITY_REQUEST_V1` before logging a timeout warning and
  retrying once.

## 6. Patch targets & integration points

Two categories: **direct calls** (public static game helper methods invoked outright — no behavior change,
just calling the game's own machinery) and **Harmony patches** (actual interception). Per
`docs/CONVENTIONS.md`, every patch/lookup logs a "Target found: X" line, and lookups feed §3's
`PatchRegistry` health-check table alongside every other `ftk2mods.*` plugin's own registrations.

| Target | Kind | Why |
|---|---|---|
| `ConfigsHelper.ReloadConfigs` | Direct call | Core hot-reload trigger (M1). |
| `ConfigsHelper.ProcessDirectory` / `ProcessJsonFile` | Direct call | Per-source reload, gated behind `PerSourceReload` knob pending §11 verification (M3). |
| `CombatPhase._addEntityToCombat` | Postfix | Sets `InCombat = true` — feeds the reload/mutation safety gate (M1). |
| `CombatPhase._endCombatAsync` | Postfix | Sets `InCombat = false`, resets the AI-decision capture buffer (M1). |
| `AIHelper.BehaviourAiDecision` | Postfix | Captures `CombatDecisionData {Ability, Position, FocusUsed}` for `dk_dump_ai` / the logging service, without altering the decision — coexists with WarBrain's own Prefix takeover of the same method (M3). |
| `AIHelper.StandardAiDecision` | Postfix | Same, for the fallback decision path (M3). |
| `CommandLineHelper.ExecuteCommand` | Prefix | Intercepts `dk_`-prefixed input, dispatches to the console handler, returns `false` to short-circuit when handled or `true` to fall through to vanilla/EOR handling otherwise. EOR already patches this method — patch ordering between EOR and DevKit needs verification (M2, §11). |
| `QuestHelper.AddQuestToGameRun` | Direct call | `dk_start_quest <id>` (M1). |
| `InteractableHelper.ApplyStatChange` | Direct call | `dk_set_stat` — synthesizes a `CHANGE_STAT`-shaped mutation using the game's own applier rather than writing to `CharacterConfig.Stats` directly (M1). |
| `CharacterHelper.GetStat` | Direct call | Read-back/confirmation after `dk_set_stat`, and general stat inspection for dumps (M1). |
| `AppConfigManager.Initialize` **or** `RouterMono.Update` (first tick) | Postfix (candidate) | Timing hook for "run health check once all sibling plugins have registered." Exact choice depends on BepInEx plugin load-order guarantees (§11); `RouterMono.Update`'s first tick is the safer bet since all plugins' `Awake()` calls precede any `Update()` call (M3). |
| `AdventureDirector._handleNetworkAction` | Prefix (receive) + direct call (send) | ParityService transport (M1, R1): sends/receives `FTK2MODS_PARITY_V1` on session host/join and after any hot-reload in MP, and `FTK2MODS_PARITY_REQUEST_V1` for a late-joining client's re-query. Verified precedented hook — this is exactly where EOR's own `EOR_VER/EOR_CFG/EOR_DAT/EOR_SYS/EOR_DEF/EOR_SIG` handshake and `EOR_SYNC_TOWN_SNAPSHOT_V1` piggyback (`docs/research/game-code-reference.md` §7). |
| `AdventureDirector.Initialize` | Postfix (candidate) | Candidate "session started/joined" trigger point to kick off the initial ParityService handshake; exact trigger and host-vs-client detection is an open question shared with §11 item 8 (M1). |

Same reflection-based soft-dependency calling convention as `DevKitLog`/`PatchRegistry` (§3) applies to
`ParityService.Register` — sibling mods resolve it via `Type.GetType(...)` and no-op if DevKit isn't loaded,
so R1 enforcement never becomes a hard build dependency.

**Investigated but blocked on decompile** (do not block the rest of M1 — ship these commands as documented
stubs until resolved, see §11):

| Target area | Candidate game surface | Command blocked |
|---|---|---|
| Grant an item/orb/gem to a character's inventory | `InventoryHelper.GetThingConfig`/`HasInteractable`, `InventoryController.CharacterFeed` (none confirmed as the actual grant method) | `dk_give_item` / `dk_give_orb` / `dk_give_gem` |
| Start an ad-hoc encounter outside normal adventure/quest flow | `CharacterHelper.GetEnemiesForCombat(EnemySet)` (confirmed injection point for enemy *composition*, not confirmed as a combat-start entry point) | `dk_spawn_encounter` |
| Force a flee to succeed | `eCombatActions.FLEE` roll object `{MinValue,MaxValue,ACC,Stat,Rolls,Ammo}` (no confirmed bypass) | `dk_flee_combat` |
| Force a combat win | `CombatPhase._processCombatResults` (confirmed to exist, not confirmed as safely re-triggerable on demand) | `dk_win_combat` — planned approach: zero every enemy's `HP` via the already-confirmed `InteractableHelper.ApplyStatChange` and let the game's own death/end-of-combat flow detect the wipe, avoiding a direct patch on `_processCombatResults` |

## 7. Example starting dataset

`data/Macros.json` — four illustrative macros. Three exercise sibling-mod test flows (`forge-test`,
`warbrain-arena`, `summoner-farm` — `dk_give_item`, `dk_spawn_encounter`, and `dk_win_combat` respectively),
each declaring its `RequiresMods` dependency so `dk_list_macros` can warn if the target mod isn't loaded.
The fourth, **`parity-check`**, is DevKit-native (no `RequiresMods` — ParityService is core, not a sibling
dependency): it runs `dk_dump_parity`, printing/writing every currently-registered mod's
guid/version/dataHash/enabledFeatures plus the most recent mismatch result, the in-game analogue of EOR's
"Print Sync-Relevant Data Hash" debug command (§2, §3) — the fastest way to triage an MP desync report.
Demonstrates the macro schema and gives sibling-mod authors a copy-paste starting point for their own test
plans. Placeholder ids are called out in §4 — swap for real ids as sibling mods ship their own SPECs/data.

`data/LogConfig.json` — a starting category table covering DevKit's own categories plus one placeholder
category per sibling mod (`WARBRAIN_DECISION`, `ACTIONPOINTS_SPEND`, `CLASSFORGE_MERGE`, `FORGE_MERGE`,
`RUNEWORKS_SOCKET`, `SUMMONER_EVOLUTION`, `QUESTSMITH_TRIGGER`, `VENUE_GRID`) at sensible default levels, so
those mods can start calling `DevKitLog.Log("WARBRAIN_DECISION", ...)` on day one without editing this file.

## 8. Testing plan

Each command exercised (target: all of the below in under 15 minutes with the shipped example data):

1. **Hot-reload cycle, timed.** Outside combat: edit a value in a shipped JSON (e.g. a `Behaviours.json`
   weight), press the reload hotkey, confirm the toast reports success and stopwatch the elapsed time
   (record it — this number is the whole point of the mod). Repeat with a deliberately malformed JSON
   (trailing comma) — confirm the engine logs loudly, leaves the previous `Configs` state untouched, and does
   not crash.
2. **Combat gate.** Enter combat, press the reload hotkey — confirm it's rejected with a "blocked: in
   combat" toast and no reload occurs. End combat, press again — confirm it now succeeds.
3. **Auto-watch mode.** Enable `AutoWatchEnabled`, edit and save a JSON file with a text editor, confirm the
   debounced reload fires once (not once per intermediate autosave) within `AutoWatchDebounceMs` of the last
   write.
4. **Config dump.** Run `dk_dump_configs ALL` and `dk_dump_configs Characters`; confirm both a full-registry
   file and a single-table file appear in the dump folder with a correct timestamp, and that a value known
   to be merged in by a sibling mod (once one ships JSON) is present in the dump — this is the mod's core
   verification promise for ClassForge/Forge/Questsmith runtime merges.
5. **Combat/venue/AI dumps.** Mid-combat, run `dk_dump_combat`, `dk_dump_venue`, `dk_dump_ai`; open each file
   and confirm per-entity `CombatComponent`/`StatusEffectComponent`/`VenueComponent`/`AIComponent` fields are
   populated and not all-default/zero.
6. **Console commands.** Exercise each shipped `dk_` command at least once: `reload_configs`, `dump_configs`,
   `dump_combat`, `dump_venue`, `dump_ai`, `dump_parity`, `set_stat`, `start_quest`, `list_macros`,
   `run_macro`. For each of the four blocked commands (§6 table), confirm the stub responds with a clear
   "not yet implemented — see SPEC §11" message rather than silently failing.
7. **Macros.** Run each shipped macro, including `parity-check`; confirm the `RequiresMods` warning appears
   when the referenced mod isn't installed (for the three sibling-mod macros), that the macro still attempts
   (and logs) each command, and that `parity-check` runs with no sibling mods installed at all (ParityService
   is core, not a sibling dependency).
8. **Logging service.** From a throwaway test call (or once a sibling mod exists), log entries at a level
   below and at/above a category's configured `LogConfig.json` level; confirm only the latter appear in the
   F-key dump. Confirm the dump file is valid JSONL and the entry count matches `DumpLastNCount` (or fewer if
   the buffer hasn't filled).
9. **Health check.** With DevKit alone installed, confirm the startup/`F12` table shows only its own
   targets, all PASS. Simulate a FAIL by temporarily pointing one lookup at a nonexistent method name locally
   and confirm the table renders a clear FAIL row instead of throwing.
10. **MP guard.** In a (simulated, if real MP session isn't available at test time) multiplayer context,
    confirm every mutating command is refused by default, and that `dk_dump_*`/health-check remain available.
    Confirm `ForceAllowInMP` + `UnsafeCommandsEnabled` together, and only together, unlock mutations, with a
    loud warning banner logged.
11. **MP parity smoke test (two real instances).** Launch two game instances, one hosting, one joining, both
    with the identical mod set — establish a baseline session with **no** warning shown (`dk_dump_parity` on
    each peer shows matching hashes for every mod). Then, with the session still running, deliberately break
    parity on the client only: edit one shipped JSON that feeds a registered mod's `dataHash` (or, if no
    sibling mod ships data yet, temporarily hand-edit `data/Macros.json` and re-register a synthetic test
    hash) and trigger a re-handshake (hot-reload on the client only, or restart the client's registration).
    Confirm: (a) the on-screen banner appears on both peers naming the correct mod and divergence kind
    (`Data`), (b) with `OnParityMismatch = WarnAndSafeMode` (default) the diverged mod's SafeMode engages —
    confirm via `dk_dump_parity`'s mismatch record and (once a sibling mod exists) its own SafeMode-gated
    behavior, (c) switching to `WarnOnly` shows the banner with no SafeMode effect, and `Block` prevents the
    session from continuing, (d) a third instance joining *after* the mismatch was introduced receives the
    *current* (mismatched) state via `FTK2MODS_PARITY_REQUEST_V1` — not a stale pre-mismatch snapshot.

Edge cases to cover explicitly: malformed JSON on reload, missing dump-folder-path (auto-create it), a
console command with an unknown id argument (graceful error, no crash), a macro referencing a missing mod's
id (graceful skip + warning, per above), a health-check target that a sibling plugin never registers because
it loaded after DevKit's check ran (documents the load-order open question in practice), a ParityService
registration with a `null`/empty `dataHash` (treat as a guaranteed mismatch rather than a silent pass), and
two peers with the same mod set but out-of-order `EnabledFeatures` arrays (must compare as sets, not
sequences, or ordering alone would false-positive a mismatch).

## 9. Save & multiplayer considerations

**Persistence.** DevKit writes nothing to the save file and does not touch `GameRunData`. Its only on-disk
footprint is BepInEx config, `data/Macros.json`, `data/LogConfig.json`, the dump folder, and (optionally) a
rolling log file. Uninstalling DevKit has zero save-compat risk. This is unchanged by MP — DevKit still owns
no save-file state — but DevKit's *runtime* MP posture is now split in two, per `docs/MULTIPLAYER.md`, and
the six points below answer that doc's mandated §9 structure.

1. **Parity class.** DevKit is dual-natured and both halves matter for MP:
   - DevKit's own dev-tooling (hot-reload, console commands, dumps, logging, health check) is **`LOCAL`** —
     it is pure presentation/tooling for whoever is running it and carries no parity requirement of its own
     (R4). A peer without DevKit installed loses nothing but tooling convenience.
   - The **ParityService it hosts is `ALL_PEERS`**: R1 only works if every peer that has any `ftk2mods.*` mod
     installed also has DevKit installed, because sibling mods' `Register` calls no-op silently if DevKit
     isn't present (§3) — meaning a peer without DevKit is invisible to the parity handshake entirely, not
     merely "assumed fine." Practically: DevKit is a soft *build* dependency for every sibling mod but a hard
     *install* dependency for R1 enforcement to mean anything in a session. This is called out explicitly
     because it's easy to under-read "soft dependency" as "optional in practice."
2. **Feature table.**

   | Feature | Label | Authority |
   |---|---|---|
   | Hot-reload (manual/auto-watch) | `[LOCAL]` | Whichever peer presses the hotkey/has the watcher; R5 governs whether it's allowed in MP at all |
   | Console (`dk_` commands, macros) | `[LOCAL]`, mutating subset R5-gated | Local peer issuing the command; mutating commands are host-only when `ForceAllowInMP` |
   | Dumps (`dump_configs`/`dump_combat`/`dump_venue`/`dump_ai`/`dump_parity`) | `[LOCAL]` | Local peer; reflects only that peer's local/replicated view |
   | Structured logging (`DevKitLog`) | `[LOCAL]` | Local peer; ring buffer is per-instance, not shared |
   | Patch-health check (`PatchRegistry`) | `[LOCAL]` | Local peer; each peer verifies its own patch resolution |
   | **ParityService** — registration, `FTK2MODS_PARITY_V1`/`REQUEST_V1` handshake, mismatch detection/policy | **`[SYNCED]`** | Host aggregates and decides; all peers register and exchange |
3. **Determinism inventory.** DevKit itself generates and rolls nothing gameplay-relevant (no content, no
   RNG-consuming decisions — see §1 scope). The one place R2 still binds DevKit is indirect but load-bearing:
   `ParityService.ComputeDataHash` **must itself be a deterministic pure function** of a mod's data-file
   bytes (§3's hash rules — sorted file order, normalized line endings, invariant-culture comparisons,
   localization excluded). If the hash function were non-deterministic across peers (e.g. depended on
   filesystem enumeration order, culture-sensitive string ops, or wall-clock/OS-specific line endings), every
   `ftk2mods.*` mod's R1 guarantee would be built on a false positive/negative generator. This is why hashing
   is centralized in DevKit rather than left to each mod to reimplement.
4. **Sync surface.** All ParityService traffic rides `AdventureDirector._handleNetworkAction` (verified
   EOR-precedented transport, `docs/research/game-code-reference.md` §7):
   - `FTK2MODS_PARITY_V1` — host↔client, full registration list, payload sketch in §4; fired on session
     host/join and again after any hot-reload in a detected MP session (§3 "MP interaction"). Idempotent:
     receiving it just replaces the sender's last-known registration set, no cumulative state.
   - `FTK2MODS_PARITY_REQUEST_V1` — client→host, fired once on a late/mid-session join; host replies with a
     fresh (not cached) `FTK2MODS_PARITY_V1`. Idempotent for the same reason.
   No other DevKit feature has a sync surface — everything else is `[LOCAL]` per the table above.
5. **SafeMode definition.** DevKit's own SafeMode set is trivial because its mutating commands are *already*
   hard-gated off in MP by default regardless of parity status (R5, §5 `[Multiplayer]`) — there is nothing
   further for DevKit itself to disable on a parity mismatch. What ParityService *does* on mismatch, exactly:
   invoke the diverged mod's own `ParityFailed` callback (§3) and apply the configured
   `[Multiplayer] OnParityMismatch` policy (`WarnOnly` / `WarnAndSafeMode` / `Block`). The actual SafeMode
   feature set for any given sibling mod is that mod's own responsibility to define in its own SPEC §9 point
   5 — DevKit only delivers the signal and enforces the policy, it does not know which of a sibling mod's
   features are state-mutating.
6. **MP test plan.** §8 item 11 (two real instances, deliberate `dataHash` mismatch introduced mid-session):
   confirm the banner names the correct mod + divergence kind on both peers, confirm SafeMode/WarnOnly/Block
   policy behavior, and confirm a late joiner receives current (not stale) parity state via
   `FTK2MODS_PARITY_REQUEST_V1`. §8 item 10 covers the R5 mutation-guard smoke test independently.

## 10. Milestones

- **M1 — hot-reload + dumps + basic commands + ParityService.** ParityService moved into M1 (was M2/M3-
  adjacent tooling before MP became a hard requirement): the `Register`/`ComputeDataHash` reflection API,
  the `FTK2MODS_PARITY_V1`/`FTK2MODS_PARITY_REQUEST_V1` handshake over `AdventureDirector._handleNetworkAction`,
  mismatch detection + on-screen warning + `ParityFailed` callback dispatch, and the
  `[Multiplayer] OnParityMismatch` policy knob (`WarnAndSafeMode`/`WarnOnly`/`Block`) all ship in M1 — **every
  sibling mod needs this from day one** to register at all, and a sibling mod that ships gameplay content
  before ParityService exists has no way to satisfy R1. Alongside it: manual hotkey reload with the
  combat-state gate (now also triggering the M1 re-handshake per `RehandshakeOnHotReload`); `Configs`
  registry dump (all/single table) via reflection; combat/venue/AI/parity state dumps; direct-call commands
  that don't depend on an open question (`reload_configs`, `set_stat`, `start_quest`, `dump_*`); the four
  decompile-blocked commands ship as clearly-labeled stubs (§6, §8).
- **M2 — console/macros + logging service + ParityService refinement.** Full `dk_` console parser wired
  through `CommandLineHelper.ExecuteCommand`; `Macros.json` loading + `run_macro`/`list_macros` (including
  the M1-shipped `parity-check` macro); the shared `DevKitLog` ring-buffer API with per-category levels from
  `LogConfig.json` and the F-key last-N dump; `UnsafeCommandsEnabled` and R5 mutation-guard gating fully
  wired for every mutating command (host-only + session-visible warning under `ForceAllowInMP`); ParityService
  refinements that aren't correctness-blocking for M1 — configurable localization-exclusion patterns for
  `ComputeDataHash`, `ParityRequestTimeoutMs` retry/backoff tuning, and a friendlier mod-guid → display-name
  resolution for the mismatch banner.
- **M3 — health check + watcher.** `PatchRegistry` registration API + startup/`F12` PASS/FAIL table (plus
  optional dump-to-file); `FileSystemWatcher` auto-reload mode with debounce; per-source reload via
  `ConfigsHelper.ProcessDirectory`/`ProcessJsonFile` if §11's safety questions resolve favorably; the two
  `AIHelper` decision-capture Postfixes feeding `dump_ai` with live-observed decisions instead of only static
  `AIComponent` state.

## 11. Open questions

1. **Vanilla console grammar.** What commands does `CommandLineHelper.ExecuteCommand` already recognize, and
   what does EOR's own patch of it look like? Needs a decompile pass over `FTK2.dll` (string-literal/switch
   scan) and `EnhancedOverhaulRemix.dll` before finalizing DevKit's Prefix/fallthrough behavior and confirming
   the `dk_` prefix can't collide with anything vanilla or EOR-defined.
2. **Method accessibility.** Every direct-call target in §6 is assumed `public static` because the game
   helper classes are described as "static" in `docs/research/game-code-reference.md`, but accessibility
   (public vs internal/private) isn't stated per-method. Any non-public member needs `AccessTools`-mediated
   invocation instead of a direct C# call — verify each before implementation.
3. **Per-source reload safety.** Does `ConfigsHelper.ProcessDirectory`/`ProcessJsonFile` leave the `Configs`
   registry in a consistent state on a parse error mid-directory, or can it partially update? Blocks
   `PerSourceReload` (M3).
4. **Hot-reload safety beyond "not in combat."** Is the combat-state gate sufficient, or does the overworld/
   town also cache derived state from `Configs` at scene-load that a reload wouldn't refresh (e.g. cached
   `CharacterConfig` lookups on already-spawned NPCs)? Needs a playtesting matrix per config table, not just
   an assumption.
5. **Inventory grant method.** No confirmed method grants an item/orb/gem into a character's inventory
   (`InventoryHelper.*`/`InventoryController.CharacterFeed` are candidates, not confirmed) — blocks
   `dk_give_item`/`dk_give_orb`/`dk_give_gem` and the character/party target-selector resolution used by
   `Macros.json` examples (§4).
6. **Ad-hoc encounter start.** No confirmed entry point starts a combat outside the normal adventure/quest
   flow with an arbitrary character-id set — blocks `dk_spawn_encounter`.
7. **Forced flee/win.** No confirmed low-risk bypass for `eCombatActions.FLEE`'s roll, and
   `CombatPhase._processCombatResults` isn't confirmed safely re-triggerable on demand — blocks
   `dk_flee_combat`/`dk_win_combat` (a zero-enemy-HP workaround is proposed for win in §6, unverified).
8. **MP-session detection.** No networking/session-state flag was identified for "is this session
   multiplayer" / "am I the host" — blocks a real implementation of the MP guard (§9) *and* now also blocks
   ParityService's own handshake trigger (§3, §6: what fires "session started/joined" to kick off the
   initial `FTK2MODS_PARITY_V1` exchange, and how a peer knows whether it's host or client for aggregation
   purposes). Currently spec'd to fail closed (treat unknown as MP) until an API is found. This is the same
   open problem as `docs/MULTIPLAYER.md`'s repo-wide open-question list items 1–5 (host-only AI decisions,
   `GameRunData` replication scope, `CombatState.GridType` sync, party-rebuild propagation, and
   `_handleNetworkAction` payload shape/size limits) — see that doc rather than duplicating the list here;
   item 5 in particular (payload size limits) directly bounds how many mods' registrations can fit in one
   `FTK2MODS_PARITY_V1` message before ParityService needs to chunk it.
9. **Contracts DLL vs reflection.** Should the `DevKitLog`/`PatchRegistry`/**`ParityService`** shared APIs be
   a tiny compile-time "contracts" assembly referenced by every `ftk2mods.*` plugin (type-safe, but adds a
   build dependency to every mod in the repo), or stay reflection-based soft dependencies (zero build
   coupling, slower/uglier call sites, silent no-op if DevKit is missing or renamed)? Decide before sibling
   mods start integrating — now higher-stakes than before, since a silent `ParityService.Register` no-op
   means that peer's mod is invisible to R1 enforcement, not just missing a logging convenience.
10. **BepInEx load-order guarantee.** Does `RouterMono`'s first `Update()` tick reliably fall after every
    `ftk2mods.*` plugin's `Awake()` has run and registered its patch targets, or does DevKit need an explicit
    `[BepInDependency]`/soft-dependency ordering convention across the repo to guarantee the health check
    doesn't false-negative a plugin that simply hasn't loaded yet?
11. **FileSystemWatcher reliability.** Editor save behavior (atomic replace vs. in-place write, multiple
    write events per save) varies by tool; `AutoWatchDebounceMs`'s default (750ms) is a guess and needs
    empirical tuning against whatever editor sibling-mod authors actually use.
12. **ParityFailed callback contract.** Should `ParityFailed` be a delegate passed at `Register` time (as
    currently spec'd in §3), or should DevKit instead look up a well-known static method name
    (`FTK2Mods.<Mod>.OnParityFailed`) by reflection the same way `DevKitLog`/`PatchRegistry` are resolved?
    The delegate approach is simpler for the registering mod but means DevKit holds a live reference across
    an assembly boundary for the session's duration — worth confirming this doesn't complicate BepInEx
    plugin unload/hot-swap scenarios before M1 ships.
