# Crucible Autopilot — Design

> **Status:** design approved 2026-08-23, pending owner review. No implementation has started.
> **Goal:** test every FTK2 mod feature automatically — including unattended overnight runs — without
> playing the game by hand, and without ever risking the live co-op install.

---

## 0. Ground truth (verified 2026-08-23 against the running retail build)

Everything in this section was measured this session, not assumed. Design decisions below cite it.

| Fact | Evidence | Consequence |
|---|---|---|
| Crucible's RPC + MCP bridge works end-to-end | `health`, `state`, `list_commands`, `exec`, `read_trace`, `screenshot`, `list_instances`, `compare_state` all returned live data | The transport layer is done. Build on it, do not rebuild it. |
| The game needs Steam running | Launched exe-direct with Steam closed: live process, ticking `RouterMono.Update`, black window, `route=NONE` forever | Any launcher must ensure Steam is up first. This symptom is indistinguishable from a harness bug. |
| `steam_appid.txt` ships in the game dir | `Test-Path` = True | Exe-direct launch works while Steam runs, bypassing Steam's one-instance-per-appid rule. This is how the test copy launches. |
| **A second instance cannot run on one machine** | Second process exited code 0 within 40s; port 8788 never opened; one process survived | No local two-peer testing. MP correctness must be inferred single-peer (§6) and proven on two machines. |
| **`MultiplayerDemoQuickCombat` throws `NotImplementedException`** | `MainMenuDirector+<>c.<Initialize>b__46_7(String,String,String)` in `Player.log` | SPEC §0 called this "the single load-bearing assumption of §9/M4". It is **false**. One instance cannot drive multiple player slots. Delete that path from M4. |
| Command registration is contextual | 2 commands at `route=NONE`, 21 at `MAIN_MENU` | Never probe the registry before reaching a known route. Verb availability is route-dependent. |
| **No mod in this repo registers a console command** | `grep -rn RegisterCommand --include=*.cs` → zero hits | Crucible SPEC §2/§3 document `crucible_*` commands that do not exist. S3 builds them for the first time. |
| `NetworkData` has no `PlayerCount` | Reflection dump of retail `FTK2.dll`; members are `IsHost`, `UserName`, `PlayerList`, `PlayingOnlineMultiplayer`, … | Fixed this session (`PlayerList.Count`). **This class of bug is the reason for the grounding rule in §2.** |
| Unity save path is identity-derived | `app.info` = `IronOak Games` / `For The King II` | Copying the install does **not** isolate saves. Both copies write to the same `LocalLow` folder. |
| Game install is 9.22 GB; C: has 111.5 GB free; no D: drive | `Get-ChildItem`/`Get-PSDrive` | The test copy fits on C:. |
| Live install is EOR-contaminated by design | `EOR_PACKAGE.json` present; `Characters` 2126 vs pristine 2095 | LiveDataHarness AC1/AC2 must not demand a pristine baseline (§7). |
| 35 authored classes exist; 31 are deployed | `CF_PACK_EOR_CLASSES` (31) + `CF_PACK_BALDURS` (4, `enabled:true` but never deployed) | The test matrix is 35, not 31. The 4 Baldur's classes have never been loaded by the game. |

---

## 1. Scope

**In scope.** A verb layer registered into the game's own console; a combat-level observation surface; a
data-driven scenario runner with assertions; an MP-safety system; offline data validation of every
authored class; and an unattended overnight runner with crash recovery and hard isolation from the live
co-op install.

**Out of scope.** Headless combat simulation — FTK2's combat is Unity-coupled (`RouterMono`,
`MonoBehaviour`, UniTask). This is a permanent boundary, not a later milestone. Gameplay or balance
changes: every verb here is dev tooling and ships disabled by default. Any outbound network beyond
loopback.

**Non-negotiable safety property.** No component may write to the live install at
`C:\Program Files (x86)\Steam\steamapps\common\For The King II` or to the live save folder during an
automated run. §8 is how this is enforced.

---

## 2. The grounding rule

> **No game member name may appear in code or spec text until it has been dumped from the retail
> assembly and recorded in a field map.**

This exists because `StateReader` read `NetworkData.PlayerCount` — a plausible, wrong name — and returned
`null` while the log filled with resolution failures, for months, undetected. The same session proved
`MultiplayerDemoQuickCombat` was registered, callable, and returned `ok: true` while doing nothing at all.

Two failure modes, one cause: a name that looks right is not evidence.

Every sub-project therefore begins with a **grounding task** whose output is a table of verified members
committed to `docs/research/`. `TypeProbe` (§9) is the tool. A task that cannot ground its targets reports
that and stops; it does not guess.

**Corollary — reflection misses must be loud.** Every reflective read reports into the snapshot's
`warnings` array. A member that vanishes in a game update surfaces in the next assertion, not in a log
nobody reads.

---

## 3. Architecture

```
                        MCP (Node, stdio)          ← Claude
                              │  thin pipe, no logic
                        RPC (HttpListener, loopback)
                              │
   ┌──────────────────────────┼──────────────────────────┐
   │                    Crucible.Plugin (net472)         │
   │   VerbRegistry ──registers──► CommandLineHelper     │  S3
   │   StateReader  ──reflects──► CombatState/Entity     │  S1
   │   RngWatch     ──patches───► GameRandom             │  S5
   │   ModFeed      ──reads─────► DevKit decision log    │  S2
   └──────────────────────────┬──────────────────────────┘
                              │ pure data
                    Crucible.Core (netstandard2.0)
                    ScenarioRunner · Assertions · StateDigest · TraceWriter   S4
                              │
                    Overnight runner (net10 console)                          S8
```

**Two seams hold this together.**

1. **Every verb is a registered console command.** `CommandLineHelper.RegisterCommand` is public and
   verified. Registering there means each verb works from the in-game console *and* over MCP, with one
   implementation. MCP stays a dumb pipe — it adds no behavior, so it cannot drift from the game.
2. **Every observation is a pure serializable snapshot.** Assertions are data comparisons over that
   snapshot, living in `Crucible.Core` (`netstandard2.0`, no Unity, no game reference). The entire
   assertion engine unit-tests with no game running — the pattern the existing 36 Core tests already use.

**Error posture, inherited from the existing code and reinforced.** Reflection failures degrade to a null
plus a warning, never an exception — a snapshot that throws takes down the oracle exactly when a game
update makes it most needed. Verbs fail closed: an unresolvable target refuses rather than silently
no-ops. **`ok` must never mean "dispatched".** `MultiplayerDemoQuickCombat` returned `ok: true` while
throwing internally; every verb therefore reports a post-condition, not a dispatch receipt.

---

## 4. S1 — Observation surface (`crucible.state.v2`)

The keystone: nothing can assert until this exists. Today's snapshot carries route, run seed/day/gold/
chapter, and network — nothing about a combatant.

```jsonc
{
  "schema": "crucible.state.v2",
  "route": "COMBAT",
  "run":    { "present": true, "seed": 12345, "day": 3, "gold": 120,
              "chapter": "<runtime value>", "partyClassIds": ["CF_EOR_BARD", "CF_EOR_WARDEN"] },
  "combat": {
    "active": true, "round": 2, "turn": 1, "phase": "PLAYER",
    "combatants": [
      { "id": "<runtime entity id>", "name": "<runtime>", "classId": "CF_EOR_BARD", "isPlayer": true,
        "hp": 34, "maxHp": 40, "alive": true,
        "statuses": [ { "id": "STATUS_CF_ENCMOD_CURSED", "stacks": 1, "duration": 2 } ],
        "stats": { "<stat keys pending §4 grounding>": 0 } }
    ]
  },
  "rng":      { "gameplayDraws": 17, "visualDraws": 4 },
  "warnings": []
}
```

`v1` remains served so nothing in flight breaks; `v2` is additive.

**Field map is a prerequisite, not an implementation detail.** Grounding task dumps `CombatState`,
`Entity`, `CombatComponent`, and the status-effect type, and records verified members. No field above is
final until that lands — the shape is the contract, the member names are pending evidence.

`rng` counters serve double duty: they make scenarios deterministic and they are S5's raw signal.

Existing `StateDigest` + `Redactions.json` extend to `v2` unchanged — float quantization and redaction
already work and are covered by tests.

---

## 5. S3 — Verb layer

All four categories are in scope. Every verb registers as `crucible_<name>` and is exposed 1:1 over MCP.

| Category | Verbs | Basis |
|---|---|---|
| Combat | `advance_turn`, `set_hp` | **Wrap shipped commands** (`EndPhase`, `SetPlayerHealth`) — low risk |
| Combat | `kill_all`, `force_win`, `force_lose`, `use_ability` | **Unverified** — needs Harmony + grounding |
| Party | `give`, `equip`, `set_stat` | **Wrap shipped commands** — low risk |
| Party | `set_party`, `set_level` | **Unverified** — `set_party` is likely UI-bound |
| World | `jump_encounter`, `set_terrain`, `advance_days`, `teleport` | **All unverified** |
| RNG | `set_tarot`, `force_roll` | **Wrap shipped commands** (`SetTarot`, `SetSlotRollResult`) |
| RNG | `pin_seed` | **Unverified** |

**Honest split: 7 of 18 wrap commands proven to work today; 11 depend on game APIs nobody has confirmed.**
Each unverified verb gets a grounding probe *before* it is planned. A verb whose API does not exist is
reported as infeasible and dropped — not stubbed, not faked.

**`set_party` gets a spike ahead of all other S3 work.** Testing 35 classes means composing parties
programmatically; if class selection is reachable only through character-creation UI, the whole matrix
strategy changes. This is the highest-value and highest-risk verb, so it is answered first and cheaply.

Verbs are refused during an online session unless `AllowMutationsInMP` is set — the existing
`CommandGate` already implements this and is covered by tests.

---

## 6. S4 — Scenario runner

Scenarios are data, executed by `Crucible.Core.ScenarioRunner` against an `ICommandSink`.

A real example, grounded in authored content rather than invented. `SKILL_CF_BARD_CRESCENDO` triggers on
`ON_ABILITY_USED` when the ability's stat is `TAL` and the roll tier is `>= SUCCESS`; it increments a
`CRESCENDO` counter (max 3) and, at counter 1, applies `STATUS_ATTACKUP_00` to `ALLY_ALL` for 2 turns:

```jsonc
{
  "name": "bard-crescendo-grants-attackup-at-one-stack",
  "pin":  { "seed": 12345 },
  "setup": [ { "do": "set_party", "args": { "classIds": ["CF_EOR_BARD"] } },
             { "do": "jump_encounter", "args": { "id": "<encounter id pending §5 grounding>" } } ],
  "steps": [
    { "wait_for": { "path": "combat.active", "equals": true, "timeoutMs": 30000 } },
    { "do": "force_roll", "args": { "tier": "SUCCESS" } },
    { "do": "use_ability", "args": { "id": "<a TAL-stat ability, pending §4 grounding>" } },
    { "do": "advance_turn" },
    { "expect": { "path": "combat.combatants[?isPlayer].statuses[*].id",
                  "op": "contains", "value": "STATUS_ATTACKUP_00" } },
    { "expect": { "path": "combat.combatants[0].statuses[?id=STATUS_ATTACKUP_00].duration",
                  "op": "eq", "value": 2 } },
    { "expect": { "path": "rng.visualDraws", "op": "unchanged" } }
  ]
}
```

Three primitives, deliberately: `do`, `wait_for`, `expect`. `wait_for` closes a real gap found this
session — detecting `MAIN_MENU` required polling `/state` with curl from outside the harness.

This example also shows why the RNG verbs are load-bearing rather than a convenience: the recipe's
`ROLL_TIER` condition means the assertion is meaningless unless the roll is pinned first. And the final
`visualDraws unchanged` assertion is the S5 rule expressed as an ordinary test — a gameplay recipe that
quietly consumed the visual RNG stream would fail here, on one machine, long before it desynced anyone.

The runner is pure and host-agnostic: it unit-tests against a fake sink with scripted snapshots, no game.
MCP surfaces `ftk2_run_scenario` and `ftk2_list_scenarios`.

---

## 7. S5 — MP-safety (all four mechanisms)

The owner's constraint: *"I don't want to build into a hole where multiplayer won't work."* Distribution is
a zip every player installs identically, so both the code and the build must be provably identical.

Single-player correctness does **not** imply multiplayer correctness. The gap is RNG discipline and host
authority — invisible in SP, because SP never disagrees with itself. Hence a dedicated layer.

1. **Runtime RNG attribution (primary).** Harmony patches on the gameplay *and* visual `GameRandom` entry
   points; each draw is attributed to the calling mod. Rules enforced: gameplay rolls never use
   `NextFloatVisual`/`NextChanceVisual`; mod code never draws from the shared gameplay stream outside a
   replicated path. This catches the dominant desync cause **on one machine**.
2. **Static scan.** Mod DLLs scanned for visual-stream calls on gameplay paths and un-host-gated
   mutations. Runs with no game, cheap enough for every build.
3. **Build parity.** Release zip carries a manifest + `dataHash`; `ParityService`'s existing handshake
   enforces peer equality and engages SafeMode on mismatch — catching the stale-zip case directly.
4. **Two-machine acceptance gate.** Crucible on both PCs, `ftk2_compare_state` diffing digests. Manual and
   milestone-only, but it is the only real proof, and §0 shows nothing local substitutes for it.

This **merges** the existing `DesyncWatch` (per-channel attribution, 17 tests) and determinism harness
(12 tests, 6 negative controls) from `feat/desync-detector`. It does not replace them.

**Every rule ships with a fixture that deliberately violates it.** A lint that cannot fail reports green
forever — the exact reasoning already recorded in the determinism harness's own commit message.

---

## 8. S6 — LiveDataHarness, corrected

Already fully specced with AC1–AC10; zero code written. Two acceptance criteria are wrong for this setup:

- **AC1** demands a pristine install reading `Characters` 2095. The live install is deliberately
  EOR-contaminated at 2126 and the co-op saves depend on that content. AC1 becomes: *the expected pack set
  is present and every authored id resolves*, asserted against a recorded manifest rather than vanilla
  purity.
- **AC2** manufactures a contaminated install to prove the provenance gate fires. Contamination is now the
  normal state, so the gate inverts: it asserts the **expected** packs are present and flags **unexpected**
  ids.

Everything else stands. This is the cheapest broad win: it validates all **35** classes' data — dangling
references, missing localization, id drift — in one command with no game running, and it is the only
component that would have caught the undeployed Baldur's pack.

---

## 9. Overnight runner and isolation

**Test install.** The game dir is copied to a second location with its own BepInEx, mods, and configs,
launched exe-direct (`steam_appid.txt` makes this work) with Steam running. The live install is never
written to.

**Save isolation — junction swap.** A copied install shares `%USERPROFILE%\AppData\LocalLow\IronOak Games\
For The King II\`, so the copy alone is insufficient. Before a soak the real folder is moved aside and a
directory junction points at a per-run scratch dir; teardown restores it.

The swap is the one genuinely dangerous operation in this design, so it is guarded:

- A **marker file** records that a swap is in progress, with the real path.
- **Startup repair** runs before anything else: a marker found at start means a previous run crashed
  mid-swap, and the original is restored before work begins.
- Teardown is idempotent and safe to run twice.
- A verified backup already exists at `C:\Users\ben\Backups\ftk2-2026-08-23\` with a restore note.

**Crash recovery.** Unity will fall over during a long soak. The runner detects a dead RPC port,
relaunches, and resumes at the next scenario — otherwise one crash wastes the remaining hours.

**Serial by necessity.** One instance per machine (§0). 35 classes × N scenarios is hours, so the runner
takes a wall-clock budget, checkpoints after each scenario, and is resumable.

**Screenshots require a rendering window** — unminimized, machine not asleep. The runner asserts this at
start rather than producing black images, as observed this session.

**Artifacts.** Per-run directory: JSONL trace, snapshots, screenshots on failure, and a summary report
with per-scenario pass/fail. Silent truncation is forbidden — a skipped or budget-dropped scenario is
reported as skipped, never omitted.

**`TypeProbe`.** The reflection probe built this session — loads `FTK2.dll` via `Assembly.LoadFrom` with an
`AssemblyResolve` fallback and dumps any type's members with no game running — is promoted into the repo
as `FTK2.DevKit/sandbox/TypeProbe`. It is the instrument the §2 grounding rule depends on. Same technique
LiveDataHarness is specced on, so it doubles as that project's proving ground.

---

## 10. Testing standard

- All logic lives in `.Core` (`netstandard2.0`) and is unit-tested with no game — matching the existing
  36 Crucible / 17 DesyncWatch / 12 determinism tests.
- **Every check has a negative control.** The bar the determinism harness already set.
- Verbs are verified live by a smoke scenario; a verb with no live evidence is not "done".
- No component reports success it has not verified. `RpcServer` once logged "RPC listening" for a listener
  that never served; `ftk2_read_trace` returns `ok: true` for an empty trace. Both are the same bug.

---

## 11. Build order

| Step | Why here |
|---|---|
| **S0 — Consolidate branches** | Four checkouts, three unmerged branches, `FTK2.Crucible` duplicated. Everything else assumes one trunk. |
| **S6 — LiveDataHarness** | Independent, offline, already specced. Validates all 35 classes immediately. |
| **S1 — Observation surface** | Keystone. S4 and S5 both depend on it. |
| **S3 — Verb layer** | `set_party` spike first; it gates the 35-class matrix. |
| **S4 — Scenario runner** | Needs S1 + S3. |
| **S5 — MP-safety** | Needs S1's RNG counters; merges the desync branches. |
| **S2 — Mod-internals feed** | Diagnosis, not detection. Valuable once things fail. |
| **S8 — Overnight runner** | Wraps S4 once scenarios are real. |

---

## 12. Open questions

1. **Is `set_party` reachable without UI?** Gates the 35-class matrix. Spiked first.
2. **Which world verbs exist at all?** `set_terrain`, `jump_encounter`, `teleport` are entirely unverified.
3. **Is enemy AI host-only?** Carried forward from `MULTIPLAYER.md` open question #1, still unresolved, and
   it determines whether AI-side mods can desync.
4. **Does the vendor desync detector fire in a 2-player session with our mods?** Answerable only on two
   machines; it is the S5 acceptance gate.
