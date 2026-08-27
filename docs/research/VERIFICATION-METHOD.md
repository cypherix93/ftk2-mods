---
title: FTK2 Verification Method - how to prove a feature actually works
type: reference
tags: [ftk2, crucible, classforge, verification, testing, game-dev]
repo: C:\Users\ben\repos\ftk2-mods-crucible
---

# FTK2 Verification Method

How to prove a game feature works, for For The King II modding. Written because this project
repeatedly reported features as working when they were not, in **both** directions — code said
it fired while nothing happened on screen, and screenshots were filed under names the image did
not support. Both are false positives. Both were expensive.

Companion to `ftk2-mods/AGENT-BRIEF.md` (the global instruction set). This doc is the *how do I
know it works* half.

---

## 1. The rule: PAIRED evidence, never one alone

**Every player-facing feature needs BOTH a log proof it fired AND a screenshot proof the player
can see it.**

The two instruments fail in *opposite* directions, which is exactly why neither alone suffices:
- **Log only** → the "invisible mechanic" false positive. The mechanic exists in state and the
  player never sees it, which for a class feature means it does not work.
- **Screenshot only** → cannot say *why*, and invites mislabelling (three incidents here,
  including a vanilla Shepherd filed as our Pacifist, which voided an entire run).

| Log `proc` | Visible on screen | Verdict |
|---|---|---|
| yes | yes | **PASS** |
| yes | no | **FAIL - invisible mechanic** |
| no | yes | **FAIL - something else caused it.** Do not credit the skill. |
| no | no | did not fire - check the gate/seed before calling it broken |

## 2. On-screen TELLS - at least two, one must be identity

A single tell gets misread.

**Rule 1 - IDENTITY tell is mandatory.** Prove the right CLASS is acting before crediting it:
party HUD card name, active-character banner, action-menu header, or the ability names
themselves (unique to our weapons: "Here's a Shield", "Nerf Gun, Literally", "EVERYBODY HYPE").
Our classes display as **Vampiric / Pacifist / Pokemon Trainer / Gary / Chaos Mage**.
**"Shepherd" / "Thief" / "Corsair" / "Bladedancer" are VANILLA** - seeing one means the party was
built with `set_class` (which rewrites `ConfigName` but NOT the name) and nothing in that frame
is evidence.

**Rule 2 - an EFFECT tell matched to the claim:**

| Skill shape | Effect tells |
|---|---|
| applies a status | status icon on the target portrait + duration counting down across rounds |
| changes a stat | the stat value in the portrait card (e.g. armour `1 -> 6`) |
| damages / heals | HP bar AND the numeric `hp/maxHp` under the portrait |
| deals NO damage | target HP unchanged AND the preview panel showing no damage line, where a normal ability shows e.g. `0-9 Magic Damage` |
| summons | the new body on the board AND its card in the ally/minion HUD |
| captures | enemy gone from the enemy side AND present in the ally minion panel next fight |
| tile effect | the decal on the specific tile |
| ally command / group buff | the icon on EVERY affected ally, not only the caster |

**Rule 3** - the ability preview panel (name, target scope, "On Perfect", per-slot/perfect %) is
itself a tell, and the cheapest way to confirm the UI explains the skill.

**Rule 4** - capture tells in ONE frame where possible; three crops from mismatched moments are
not corroboration.

**Rule 5** - before/after tells (HP delta, stat change, duration decay) need both frames from the
same fixture with a pinned seed, or the pair is not comparable.

## 3. Tile-level reading - where the hard features actually live

AoE footprint, the Chaos Mage hazard tile, summon placement and capture all manifest per-tile.

**What EXISTS:** `crucible_combat_snapshot` reports `tile=(x,y)` per combatant, plus a grid dump
of up to 30 tiles with `TilePosition`, `group=`, `row=`.

**CLOSED 2026-08-26 - state-v2 now carries the board.** Both gaps below were real and are fixed;
see `crucible-state-v2-schema.md` for the full shape.
- **`VenueTileComponent.AuraStatuses` is now read**, in the new `combat.tiles[]` array
  (`x`, `y`, `groupIndex`, `rowPositionsType`, `auraStatuses[]`, `ordinal`, `occupantId`,
  `occupantOrdinal`). A hazard tile (Fire/Water/Shock/Acid) is assertable in state, so a tile
  feature is no longer screenshot-only. Note a null `AuraStatuses` VALUE is a clean tile (`[]`);
  only an absent MEMBER serializes as `null`, so a renamed field cannot read as a clean tile.
- **state-v2 combatants now carry `tile: {x, y}`**, plus `groupIndex` (0 = player, 1 = enemy),
  `ordinal` (the roster index the determinism layer keys on), `isSummon`, `isTile`, `customData{}`
  and `things[]` (per-item `CF_POKE_*` state). Positional and custom-feature assertions can be
  written against v2.

**`x` is the ROW/DEPTH axis and `y` is lateral** (`VenueHelper.cs:988/998`); the snapshot emits
named keys rather than a tuple because that has been documented backwards before.

Rule 1 still stands: state readback is the LOG half of the paired evidence, never a substitute for
the screenshot. Adding the tile view does not make a tile feature verifiable without a decal.

**v2 digests moved when these fields landed.** Any pre-change v2 baseline is stale.

Grid facts that make tile assertions meaningful (`ftk2-mods/coverage/combat.md`, `venue-grids.md`):
every group is exactly **2 columns wide** (4 rows on `VenueMap1`, 6 on `ExtendedVenueMap1`;
the Kraken enemy side is the sole 4-wide exception). So `AOE` (a 3x3 clipped to the group) is
**6 tiles**, `SPLASH` is 4, and `ALL_GROUP` is the whole board. Counting affected tiles in a
screenshot is how AoE scoping is verified.

## 4. Reproducibility - fixtures and seed pinning

A test you cannot re-run is not a test. Every scenario gets a saved, named fixture plus a
scenario manifest (fixture, party, enemies, pinned seed, abilities to fire, expected tells,
checklist item closed) so the whole battery re-runs with one command.

**Two shapes:** *isolation* (one class, minimal enemies, one skill - for debugging a failure) and
*batch* (four classes in ONE fight - the default, four times the coverage per run).

**Seed search once, pin forever.** Luck-gated outcomes (a PERFECT-roll refusal, a 15% proc) do
not have to be waited for: search for a seed that produces the outcome once, record it in the
manifest, and it reproduces on demand. This is how "we can never test this, it's luck-gated"
items get closed without a test-only bypass that changes the code path.

### `GameRandom` gotchas (decompile-verified, cost real time)

```csharp
public readonly int Seed;                 // a RECORD of the seed
private readonly System.Random random;    // the ACTUAL generator, built once from Seed
```

- **Writing `Seed` does nothing.** It is readonly and never read after construction. The original
  `crucible_pin_seed` wrote only `Seed`, so it changed a label and affected **zero** future draws -
  a pinned-seed test built on it is reproducible in name only. Fixed 2026-08-26 to replace the
  private `random` field with `new System.Random(seed)` and reset `_nextCount`.
- **`_nextCount` only increments while `LogCalls(true)` is on** (`if (_logCalls) { ...; _nextCount++; }`),
  which is why `NextCount` is documented elsewhere as an unreliable draw counter.
- **`LogCalls` never calls `random.Next()`** - it sets a flag and allocates a StringBuilder, so it
  cannot advance the stream.

### Fixture hygiene

- Build parties through **CHARACTER CREATION**, never `set_class` - `set_class` leaves stale
  vanilla names and makes every screenshot unusable for identity.
- Always load a **COPY**. A driven run autosaves over its fixture mid-session, not only at the end.
- **`fixture_health` only checks file SIZE.** It certified a content-corrupted fixture as healthy
  for an entire session. Confirm the party with `crucible_party_list` after loading.

## 5. Fail-safety sweep - the hardening test

The governing acceptance criterion is *classes playable, game does not break, everything fails
safely* - not correctness of magnitude. A skill doing 5 damage instead of 6 is fine; a skill that
throws mid-combat and ends the session is not.

Fire every ability/skill/item in the packs and assert on the log: zero unhandled exceptions, zero
NREs, combat still advancing, turn still progressing.
`[ClassForge] Recipe effect failed (skipped, rest of plan continues)` is a **PASS for fail-safety
and a FAIL for correctness** - report the two separately.

**Baseline the log first.** Known-benign noise (boot `RegisterCommand` NREs, EOR id collisions,
HarmonyX `AccessTools.Field` misses) must be filtered or real errors drown - that happened here,
producing 11 "new" exceptions that were mostly noise.

## 6. Determinism self-check - without a second machine

Run the same fixture **twice** with a genuinely pinned seed and diff:
- the `ftk2_state` deterministic digest, turn by turn (`StateDigest.Compute`, `RpcServer.cs:386`)
- the `GameRandom` draw sequence (`LogCalls(true)`)

Identical inputs must produce identical sequences. **Any divergence between two runs of the same
input is precisely the class of bug that desyncs peers** - process-local state, dictionary
ordering, unstable sorts, per-peer branches. This is a reproducible determinism test on ONE
machine, and it would have caught the `_dioramaTurn` process-static counter immediately.

Run it as a gate after every change: cheap, exact, and unlike a screenshot it cannot be misread.

## 7. Why this matters more here than in most codebases

`CombatState` is `[JsonIgnore]` on `GameRunData`, so **combat state is not in the vendor's desync
MD5**. A combat-side divergence is invisible to the game's own detector. There is no safety net;
correctness has to be by construction, and this method is the substitute for the detector the
game does not give us.


### CORRECTION (verified firsthand 2026-08-26): the combat desync check is DEAD CODE

Two agents disagreed on whether combat state is hashed. Settled by reading
`.decompile-scratch/proj/NetworkDebuggingHelper.cs`:

```csharp
public static async Task<SortedDictionary<string, object>> CreateCopyOfSyncCheckCombatPhaseData(...)
{
    SortedDictionary<string, object> copy = new SortedDictionary<string, object>();   // never written
    Task task = new Task(delegate {
        SortedDictionary<string, object> pCopy = new SortedDictionary<string, object>();  // LOCAL
        _copyAndConvertGuidsToIntIds(obj,  pCopy);
        _copyAndConvertGuidsToIntIds(obj2, pCopy);        // everything lands in pCopy...
    });
    task.Start(); await task;
    return copy;                                          // ...and the EMPTY one is returned
}
```
The caller then does `thing["GameActionAtCreation"] = pLatestGameAction; GetHashOfObject(thing)`.

**So the combat "desync check" hashes `{"GameActionAtCreation": N}` and nothing else.** The
plumbing runs, `DoMonitorForDesyncs` defaults true, and peers do compare — the comparison is
simply vacuous.

**Why the distinction matters.** The earlier framing ("`CombatState` is `[JsonIgnore]`, so combat
divergence is outside the hash") reaches the right conclusion by the wrong route, and the wrong
route is falsifiable — someone will find `SerializeCombatDataAndUpdateGuids(CombatState)` and
reasonably conclude we DO have a combat safety net. We do not. State it as: **the combat check
exists, executes, and is empty.**

Corollary that survives either framing: the hasher launders GUIDs into sequential ints by
first-appearance order over `GameRun.Entities`, so per-peer `Guid.NewGuid()` values are invisible
to it and **entity list ORDER is the real cross-peer invariant** — which is exactly why roster
ordinal is the identity the determinism layer uses.
