# AGENT BRIEF — read this before driving the game or authoring content

There are 30+ docs in `docs/research/`. This is the entry point. Everything below is a problem that
has ALREADY been solved or already bitten this project. Re-deriving any of it wastes a run.

If you are blocked, assume the harness already handles it and check here first. It usually does.

---

## 1. Window focus is SOLVED — never treat "window not focused" as a blocker

Three layers, all deployed. The first two alone were **verified insufficient** in the game's own
Player.log, which is why the third exists:

| Command | What it does |
|---|---|
| `crucible_input_background <on>` | `Application.runInBackground` + Unity `InputSystem.settings.backgroundBehavior` |
| **`crucible_input_focus_gate <on\|off>`** | The one that matters — see below |
| `crucible_input_state` | Read back whether input is genuinely enabled. Confirm, don't assume. |

FTK2's own `InputController` gates ALL input independently of Unity, via a reference-counted
disable-reason set (`RequestDisable` / `ReleaseDisable`). The log showed **3 `RequestDisable(LOST_FOCUS)`
against 2 `ReleaseDisable`** — one outstanding disable holding input off forever. So an injected
`crucible_pad` press on a focused party slot did nothing even with both Unity settings applied.

The gate installs a Harmony prefix that skips **only** the LOST_FOCUS reason — never `ROUTE_CHANGE`
or `SYSTEM_DIALOG_TRANSITION`, since suppressing those would fire input during real transitions —
plus a per-tick sweep that clears a LOST_FOCUS entry already in the set (a prefix only stops future
adds). It reports loudly if it cannot resolve its target; it never silently no-ops.

**Turn the gate on at the start of every run and confirm with `crucible_input_state`.**
Source: `FTK2.Crucible/src/Crucible.Plugin/InputFocusGateCommands.cs` (class docstring is the writeup).

## 2. Drive UI by ELEMENT NAME, not by keystroke

Raw keys like `P` for the summary are not the interface. Use:
- `crucible_ui_dump - button,label` — see what is actually on screen
- `crucible_ui_focus <exact-name>` then `crucible_key enter` — **the path that actually retires gates.**
  It looks weaker than `crucible_ui_click`, but `continue-label` ignores `UIToolkitHelper.Submit` entirely.
- `crucible_ui_click` — only where Submit IS the mechanism: story dialogue, and the summary's `next-btn`

Never text-match to decide a click. See §6.

## 3. Silent-success traps — a command reporting "ok" is not evidence

- **Arity.** `ParameterInfo.DefaultValue` is `DBNull.Value`, not null, so the dispatcher **skips the
  handler entirely on an under-supplied call while still reporting success.** Tell-tale: 10–16 ms
  duration and no `result` field (a real invoke is 80–250 ms). `drive.run()` pads omitted trailing
  args from a generated 88-command `COMMAND_ARITY` table and raises `CommandDidNotRun`.
  **Call through `drive.run()`, never raw.**
- **`crucible_interact` with AMBUSH / SNEAK / SKILL_TEST can NEVER work.** It dereferences
  `pMenuContext.Layout`, which only the real UI supplies, and throws inside `async void` BEFORE the
  teardown reaching `_tryProceed()` → `_enableInteraction()` — which freezes the overworld. The BUTTON
  is the mechanism. Do not retry these.
- **PowerShell `$Args` is an AUTOMATIC VARIABLE.** A script declaring `param([string[]]$Args)` can
  never have `-Args @(...)` bound to it — PowerShell silently uses its own. `make-fixture.ps1` did
  this, so every RPC it sent went out with **zero arguments** while the script reported progress
  normally; it died at stage 0 for a reason that looked like an RPC fault. Verified in both 5.1 and
  7. Same failure shape as the arity trap above: a call that succeeds while doing nothing.
  (Other reserved names to avoid as parameters: `$Error`, `$Host`, `$Input`, `$Matches`, `$PSItem`.)
- **`crucible_pin_seed` returns ok with `scopesFound=0` immediately after a load.** `run.present` is
  already true but `AdventureDirector` is not resolvable yet, so the pin writes nothing and STILL
  reports success. A caller that trusts that ok gets an UNPINNED run and a confidently false
  "reproducible" result. Retry until a scope is actually found, and treat
  `generator=False` / `scopesChanged=0` as a hard failure — only a replaced generator is a pin.
- **`crucible_ui_dump` presence ≠ on screen.** `hud-action-btn` and `choice-menu-btn` live in their
  documents permanently and read visible+enabled with no prompt up. Key on the LABEL that only appears
  with the real prompt (`loot-description-text`, `"Choose Reward"`), never on the button.

## 4. Things that wedge the harness

- **Post-combat loot modal** — "Amongst the fallen you find…", `modal-blocker`, `hud-action-btn`="Take".
  Wedged a soak for 45 min presenting as a crash: no result file, ~0 CPU, and a **pure-black
  screenshot** (the modal blocker dims the venue). `drive.take_loot()` handles it, wired into
  `ensure_interactive`.
- **Unanswered reward prompts** — they QUEUE one per completed quest and quest resolution AWAITS them.
  Presents as a hang with a healthy thread and a fully rendered overworld. `drive.pick_reward()`.
- **Undismissed story dialogue** — `_resolveQuests` is awaited BEFORE the `AdventureEndTrigger==WIN`
  check, so a dialogue on screen stops the run from ever ending.
- `drive.ensure_interactive()` is the 3-rung ladder (gates/rewards/loot → `crucible_encounter_leave`
  → force turn over). Use it instead of sleeping or re-clicking.

## 5. Evidence rules — screenshots are the only proof for game features

**Game features are validated by MCP UI actions + screenshots ONLY, never by state readback.**

- **OPEN every screenshot and describe what is in it.** Do not infer from the filename or the
  preceding command. Three separate incidents came from not looking: partners reported as jellies
  when they rendered as humans; a pure-black loading frame filed as evidence; and
  `inv02_pacifist_full.png`, which showed a **vanilla Shepherd** and voided a whole run.
- **Identify content ON SCREEN by display name before claiming a screenshot shows it:**
  `CF_ORIG_VAMPIRIC`=Vampiric · `CF_ORIG_PACIFIST`=Pacifist · `CF_ORIG_TRAINER`=Pokemon Trainer ·
  `CF_ORIG_GARY`=Gary · `CF_ORIG_CHAOSMAGE`=Chaos Mage.
  "Shepherd" / "Thief" / "Corsair" / "Bladedancer" are **VANILLA** — an all-vanilla party is not
  evidence about our classes.
- The **full frame is orientation ONLY** — a creature is ~30px at 1577x981. Identify art from a CROP.
- `evidence.py` blank-checks each requested CROP (not the full frame — a bright loading element
  lifts the full-frame mean while the board region is black) and stamps `<-- BLANK, NOT EVIDENCE`.
  Never file a stamped capture.
- If you cannot show it on screen, report **UNVERIFIED**. An honest UNVERIFIED beats an optimistic pass.
- **`drive.fixture_health()` only checks file SIZE, never CONTENT.** It reported the shared class
  fixture `bdb1596d…` healthy for a whole session while the game had autosaved a test party over it
  (slot 3 `CF_EOR_RUNEMAGE` → `CF_ORIG_GARY`, and saved MID-COMBAT, which is the likely cause of the
  "Travelling to a new area…" wedge on the next combat entry). A run you drive MUTATES its fixture
  via autosave, not only when it ends. Back the fixture up BEFORE the run, and confirm the party with
  `crucible_party_list` after loading — never take a green `fixture_health` as proof the bed is clean.
  Measured 2026-08-26; pristine copies live in `%USERPROFILE%\Backups\ftk2-fixtures`.

## 5b. Things that LOOK like bugs and are not — check before reporting

- **Partner named "Sparky" / "Bubbles" / "Blaze" is the NICKNAME FEATURE working.** Defaults are
  `GRASS=Sparky, WATER=Bubbles, FIRE=Blaze` (`TrainerPartnerNicknames.cs`). `TrainerNicknamePatches`
  deliberately overrides the species display name, and `Lang.__t` returning the key unchanged on a
  miss is precisely why a literal nickname renders verbatim. Reported as a bug once — it is not one.
- **Identical staff models across several classes** is intended: `visualfallbacks.json` →
  `LONGSTAFF_MONK_BASIC_00`.
- **A character at `ap=0`** is normal per-turn AP, not a broken class.
- **A grave/cross portrait** means genuinely downed. `godmode` restores HP WITHOUT clearing the
  downed flag, so it can look like a rendering bug.

## 5c. Harness commands that will ruin a run

- **`crucible_kill_all` kills BOTH sides.** It wipes your own party and can write a DEFEAT into
  whatever run is loaded. Use `crucible_kill_target`.
- **`crucible_force_combat` is a guaranteed wedge** — `to_combat.py` documents this. Use the
  spawn-and-walk path in `to_combat.py` instead.
- **Never test against a fixture you care about.** Save a safety copy first. Do NOT rely on
  `fixture_health` to tell you it stayed clean — it only checks SIZE, see §5. Confirm the party
  with `crucible_party_list` after loading.
- **BUILDING A FIXTURE IS A SAVE-CREATING OPERATION, and a FAILED attempt still leaves a save
  behind.** `make-fixture.ps1` starts a real campaign before it ever reaches the party screen, so
  every attempt that dies on that screen — a blocked modal, a class the stepper cannot reach —
  leaves a half-built run in the operator's `GameRuns\` and therefore in his load list. Nothing in
  the harness cleans it up. Measured 2026-08-26: three failed attempts, and `GameRuns` gained
  `953d9aad-...` at 16:47. Budget one save per attempt, say so when you report, and **never delete
  or move anything in `GameRuns` to tidy up** — that folder is the operator's, and the rule is
  copies OUT only (`make-fixture.ps1`'s own safety block says so).
- **Judge `GameRuns` by CreationTime, NEVER by mtime.** A failed attempt may also WRITE AN EXISTING
  run id rather than create a new one. Same session: `3d2fb325-...` was *last written* 2026-08-26
  16:25, inside the window of a `make-fixture` attempt that never got past the party screen — and
  it was CREATED 2026-08-25 19:51, a day before the session started. Correlated, not proven (12
  runs were written that day across several agents), so treat the overwrite as a hazard to check
  rather than a fact. The mtime-vs-CreationTime part is not in doubt: mtime alone said "a stray I
  just made" about a save that predated the session, and acting on that description instead of on
  the evidence is how somebody else's save gets moved.

## 5c-2. ONE agent drives the game at a time. Detect contention and STOP.

The game is a single instance and cannot be shared. When two agents drive it, the failures look
like game or harness bugs and are not. Measured here: three of one agent's four runs died from
contention — its script was rewritten on disk mid-run, the game process restarted twice underneath
it, a process it did not launch appeared, and a run died at stage 0 with
`Unable to connect to the remote server`. Nothing was wrong with the game.

**Tell-tales you are not alone — if you see any of these, stop and report contention:**
- a script you are running changes mtime mid-run
- the game PID changes without you restarting it
- `Player-prev.log` ends mid-transition you did not initiate
- an RPC that worked a moment ago returns `Unable to connect to the remote server`
- a driver process appears that you did not launch (check with a **self-excluding** filter, §5d)

A clean stop with "contention detected" is the correct outcome. Do NOT retry into it — retries
compound the interference and produce evidence that looks like a real failure.

This is an ORCHESTRATION duty as much as an agent one: whoever dispatches must serialise game
access. Static analysis, doc work and unit-test work parallelise freely; **anything that drives the
game does not.**

## 5d. Never declare the game unattended from a naive process check

Two ways an "all clear" lied here, on the same day:

- **A process filter that matches its own command line.** A query searching for `make-fixture`
  matches the querying process itself (its command line contains that string), and the tool chain
  running it. It reported a nonexistent orphan once and three phantom hits. **Exclude `$PID` and
  filter to real process names**, e.g.
  `Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.Name -match 'python|pwsh|powershell' -and $_.CommandLine -match '<driver names>' }`
- **A single point-in-time check.** A killed driver is not reliably dead on this machine. A
  `build_fixture.py` + `make-fixture.ps1` pair outlived a kill (or respawned) and **navigated the
  game back onto the adventure-selection screen** — the one carrying `continue-btn` and `load-btn` —
  AFTER the session had been declared clean. Re-verify after the work reports finished, not before.

Why this matters more than normal tidiness: an unattended driver on that screen is one blind submit
away from resuming the operator's live co-op campaign. Confirm with `ftk2_screen` that the visible
buttons contain no `continue-btn` / `load-btn` and that `focused` is null or safely named.

## 6. Hard safety constraints — violating one is worse than failing the task

- **NEVER press `continue-btn`, `load-btn`, `load-game-btn`, and never text-match "Continue" to decide
  a click.** Those resume the operator's real co-op saves. Exact element names only.
- **NEVER Steam-verify the install** — it wipes EOR content the operator's co-op saves depend on.
- **NEVER `taskkill node.exe`** — it kills the Claude session. PID-specific kills, game process only.
- **NO git state-changing commands** — no commit/push/checkout, and **no `git stash` / `git stash pop`**
  (shared tree; stashing during concurrent work already caused one incident).
- Never open market / town-services / quest-board encounters — none of those three branches calls
  `_closeEncounterMenuAsync`, so they never close themselves. Grant gold/items directly instead.

## 7. Authoring content — the reuse gate is mandatory

Read `reuse-before-authoring.md` and answer its questions **in writing** before authoring anything.
Never invent monsters, statuses, abilities, or primitives before proving nothing existing can be reused.

The hazard behind the rule: `GetCharacterAbilityRecord` is dereferenced **unguarded** at
`CharacterVisualHelper.cs:2567` and ~18 further sites in `CombatViewHelper.cs` / `CombatPhase.cs`, so a
pack-authored ability id that resolves onto a body is a hard NRE crash in combat.

Two more that have each cost a session:
- **Resolve the `Inherits` chain before concluding what an ability targets.** Child blocks omit
  inherited fields. But do not over-apply this either — the `ONLY_*` family DOES author its own
  `Target`/`TargetArea`, and assuming otherwise produced a false "enemy-buffing bug" report.
- **Ability names lie about their area.** `PLANT_ENTANGLE_GROUP_ATTACK` is `AOE`;
  `MUSIC_EVADEDOWN_AOE_ATTACK` is actually `ALL_GROUP`. Always resolve, never read the id.

## 8. Determinism — anything with a random roll

Co-op is **deterministic lockstep**; AI is recomputed on every peer, not host-authoritative. The
failure mode is **draw-count divergence**, not value divergence.

- A chance `>= 100` costing **zero** draws while `< 100` costs **one** makes draw count a function of a
  tunable number. Live in vendor code at `ScourgeHelper.cs:82` (gates on `>= 1m`, not `100`).
- `ProcChance` and `AiProcChance` must move together — an asymmetric pair is a draw-count fork keyed
  on a per-unit property, i.e. a desync introduced by a balance tweak.
- **Entity GUIDs are LOCAL** (`Entity.Create()` → `Guid.NewGuid()`), so a GUID is never a valid seed
  input, ordering key, hash input or persisted key. The cross-peer key is the roster **Ordinal** alone —
  see `ClassForge.Core/Rng/EntityKey.cs` for why `ConfigName`, `GroupIndex` and `(Y,X)` were all rejected.
  A GUID is still fine as a *same-peer round trip* (a dictionary this peer both writes and reads).
- **Do NOT write a new PRNG.** An earlier `ClassForge.Core/Rng/` side-stream (`CFRandom`, `CFSeedInputs`,
  `SplitMix64`) was **deleted 2026-08-26** — it never had a call site and it is not how this codebase rolls.
  There are exactly two sanctioned patterns, both already in the tree:
  1. **Roll from the shared stream.** `CombatState.Random`, wrapped by `GameRandomSource`
     (`GameAdapters.cs:952`); `RecipeActionExecutor.cs:1038` passes it through, and
     `RecipeEngineHost.cs:94` refuses to evaluate rather than substitute one.
  2. **Zero-shared-draw derived stream.** Read `pGameRandom.Seed` (a field read, **zero draws**), derive a
     seed from it plus other replicated inputs, and roll from
     `new GameRandom(seed, pIgnoreMultiplayerStaticSeed: true)` — `LootGrantPatches.cs:77-97`.
     The derived seed's inputs must ALL be replicated; seeding one from entity GUIDs gives every peer a
     different stream (that bug shipped and was fixed on 2026-08-26).
  `GameAdapters.cs:948-950` forbids `System.Random`, `UnityEngine.Random` and a freshly-constructed
  `GameRandom` as roll sources.
- **AI targeting draw counts are held constant by `AiDrawNeutrality`.** One `ForceAiDecision` call that
  runs the targeting path costs exactly one shared-stream draw for target preference, whatever the
  tendency and whether or not a Focus Fire order skipped `GetPreferredTarget`. Model + exhaustive tests:
  `ClassForge.Core/Rng/AiTargetingDraws.cs`.

## 9. Where to look next

| Topic | Doc |
|---|---|
| Full harness command surface | `coverage/harness-api.md` |
| UI driving specifics | `crucible-ui-driving.md`, `crucible-ui-overlay-system.md` |
| What a verb can/can't do | `crucible-verb-feasibility.md` |
| Game rules & mechanics | `game-mechanics.md`, `enum-ground-truth.md` |
| Ability data | `coverage/ability-catalog.md` |
| Authoring gate | `reuse-before-authoring.md` |
| **How to prove a feature works** | **`VERIFICATION-METHOD.md`** - paired log+screenshot rule,
  on-screen tells, tile reading, fixtures, seed pinning, fail-safety sweep |
| Screenshot protocol | `visual-verification-protocol.md` |
| **Rules for staying MP-safe** | **`MULTIPLAYER-RULES.md`** - 21 numbered, checkable rules derived from a
  confirmed-working EOR **0.7.0.66** decompile, plus "where our mod is outside EOR's proven envelope"
  (summons, capture, AI targeting, tiles, loot) and a blunt unverified list |
| Multiplayer/desync | `../MULTIPLAYER.md` |
