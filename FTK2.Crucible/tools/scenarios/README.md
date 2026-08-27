# `crucible.scenario.v1` — the scenario manifest format

One JSON file per scenario. `bench.py` loads it and runs it end to end:

    load a COPY of the fixture -> pin the seed -> verify the party on screen -> spawn enemies
    -> run setup -> fire the steps -> capture the tells -> evaluate the asserts -> emit a verdict

The format exists so that a class skill can be re-tested **on demand, in isolation, repeatably**,
and so that a luck-gated outcome is pinned once and never waited for again.

Read `docs/research/VERIFICATION-METHOD.md` first. This file is the encoding of that method;
the method is the reason for every field below.

---

## The verdict rule this format is built around

Every player-facing step reports a **PAIR** — never one half alone.

| log `proc` | visible on screen | verdict |
|---|---|---|
| yes | yes | **PASS** |
| yes | no | **FAIL — invisible mechanic** |
| no | yes | **FAIL — something else caused it** |
| no | no | **NO-FIRE** — did not fire; check the gate/seed before calling it broken |
| yes/no | *not yet reviewed by a human* | **PENDING-EYES** |

`PENDING-EYES` is deliberately **not a pass**. `bench.py` cannot look at a picture. It captures
the crops, blank-checks them, and stops. A human (or a vision-capable agent) runs
`bench.py attest` to record what each crop actually showed, and only then can a step read PASS.
This is the exact false positive that already shipped in this project — skills reported working
because the code said they proc'd while nothing happened on screen — so the runner refuses to
manufacture the missing half.

---

## Top-level shape

```jsonc
{
  "schema": "crucible.scenario.v1",
  "name": "iso-vampiric-batswarm",
  "kind": "isolation",                       // "isolation" | "batch"
  "checklist_item": "G. Classes / Vampiric / Bat Swarm",
  "description": "One sentence: what this scenario proves.",

  "fixture": { ... },        // which saved game, and how to load it safely
  "party":   { ... },        // asserted AFTER load, from crucible_party_list
  "seed":    123456,         // pinned. null = unpinned (only legal if seed_search is present)
  "seed_search": { ... },    // optional: how to FIND a seed, once, for a luck-gated outcome

  "setup":   [ ... ],        // ops run after load, before combat
  "enemies": [ ... ],        // spawned into the fight
  "steps":   [ ... ],        // the player-facing actions, each with its own tells/asserts
  "tells":   [ ... ],        // scenario-level tells (>=2, >=1 of kind "identity")
  "asserts": [ ... ]         // scenario-level state predicates, evaluated at the end
}
```

### `fixture`

```jsonc
"fixture": {
  "run_id": "bdb1596d-86ee-4783-9d8f-fb49934a0770",   // the GUID the game loads by
  "backup": "iso-vampiric-batswarm.ftk2",             // file under %USERPROFILE%\Backups\ftk2-fixtures
  "status": "built",                                  // "built" | "not-built"
  "notes": "the BUILD RECIPE for this bed, in full"
}
```

`bench.py` **always** copies `backup` over `GameRuns\<run_id>.ftk2` before loading, and never
loads the backup in place. A driven run autosaves over its own fixture mid-session — not only at
the end — so the pristine copy has to live outside `GameRuns\`.

There is no `load_copy: false`. It is not an option.

`status: "not-built"` is a first-class state, not a placeholder. A manifest whose bed does not
exist yet is still worth having -- it carries the build recipe in `notes` -- but it has to **say
so**, because a missing fixture otherwise presents as `run.present never became true`, which is
indistinguishable from a loader bug and has cost a whole sweep. `bench.py run` stops immediately
on a `not-built` fixture and prints the recipe; `bench.py list` shows the status per scenario.

### `party`

```jsonc
"party": {
  "expect_classes": ["Vampiric"],   // display names that MUST appear in crucible_party_list
  "min_size": 1,
  "forbid_names": ["Shepherd", "Thief", "Corsair", "Bladedancer"]   // optional; this is the default
}
```

`expect_classes` is checked against the **display name**, not `ConfigName`, because that is what a
screenshot can show. If a fixture was built with `crucible_party_set_class`, `ConfigName` is right
and the name is a stale vanilla one — the party gate fails, loudly, and the run is aborted before
a single useless screenshot is taken. `forbid_names` defaults to the four vanilla classes above
and exists to catch exactly that.

`fixture_health` is **not** the party gate. It only checks file SIZE and has certified a
content-corrupted fixture as healthy for a whole session.

### `seed` and `seed_search`

```jsonc
"seed": 20260826,
"seed_search": {
  "candidates": { "start": 1, "count": 400 },   // or "list": [11, 22, 33]
  "success_assert": "gary_refused",             // the id of an entry in `asserts`
  "found_note": "free text recorded when the search lands"
}
```

`bench.py --seed-search` re-runs the whole scenario across candidate seeds until
`success_assert` evaluates true, then prints the seed to write into `"seed"`. After that the
outcome reproduces on demand and nobody waits on a 15% roll again.

**Seed pinning must be verified before any seeded scenario is trusted.** `crucible_pin_seed`
wrote a `readonly` label -- a *record* of the seed that the generator never reads again -- and
changed **zero** draws, until it was fixed to replace the private `System.Random` itself. A
pinned-seed test built on that is reproducible in name only.

`verify_pin.py` is the check, and it does not trust the plugin's own success string. It runs the
same driven sequence three times from byte-identical state and requires **both**: the same seed
reproduces, *and* a different seed diverges. The second half is the one that matters -- without
it, a sequence that consumes no randomness looks perfectly "pinned", which is exactly how the
broken implementation passed unnoticed.

### `setup` ops

Run in order after load and (where noted) after combat starts.

| `op` | fields | what it does |
|---|---|---|
| `exec` | `command`, `args[]` | any `crucible_*` command through `drive.run()` (arity-padded) |
| `godmode` | `value`: `"on"`/`"off"` | godmode ON masks every HP effect — the manifests turn it OFF |
| `custom_data` | `selector`, `key`, `value` | sets a `CF_*` key so a counter-gated skill can be put "near" its threshold |
| `wipe_enemies` | — | `crucible_combat_wipe_enemies`; clears the field before spawning the scenario's own |
| `end_turn` | `times` | `crucible_combat_end_turn` |
| `restore_actions` | — | never run this *before* a measurement; it is here only for multi-step scenarios |

Each op may carry a `why` string. It is documentation, and it is the field that stops the next
reader from "tidying up" a line that is load-bearing (`godmode: off` is the usual victim).

### `enemies`

```jsonc
"enemies": [ { "config": "MONSTER_BAT_01", "count": 1, "group": 1, "role": "killable" } ]
```

`group` 1 is the enemy side, 0 the player side. `role` is documentation only — it names why the
enemy is in the scenario (`killable`, `boss`, `capturable`, `cannot-act`, `damaged-ally`).

`crucible_combat_spawn` must be asserted from `crucible_combat_snapshot`, never from a screenshot:
the 3D model arrives through a separate hand-off and a spawn can be real in state and absent on
the board.

### `steps`

```jsonc
{
  "id": "s1",
  "label": "Vampiric attacks the bat; Bat Swarm should proc",
  "actor": "class:CF_ORIG_VAMPIRIC",      // asserted to be the ACTIVE entity before firing
  "ability": "BLADE_BASIC_ATTACK",        // the ability CONFIG ID, never the localized name
  "target": { "select": "enemy:0" },      // resolved to a tile (x,y) from the snapshot
  "log": {
    "proc": ["SKILL_CF_VAMPIRIC_BAT_SWARM"],       // ALL must appear in the new Player.log text
    "forbid": ["Recipe effect failed", "NullReferenceException"]
  },
  "tells": [ ... ],       // captured immediately after this step
  "asserts": [ ... ]      // evaluated immediately after this step
}
```

Optional step fields:

| field | meaning |
|---|---|
| `player_facing: false` | an **advance-only** step -- it moves the fight on (`advance: {"end_turn": N}`) so a `ONCE_PER_ROUND` budget re-arms, and claims nothing. It needs no `log.proc`, gets the verdict `ADVANCE`, and is excluded from the summary counts. A step that asserts nothing must never contribute a PASS. |
| `action: "end_turn"` | the player action IS ending the turn. `ON_TURN_END` / `ON_TURN_START` recipes (Field Medic, Crouching Tiger, Wild Magic Surge) are not fired by an ability, so they get an explicit step with a real proc expectation and real tells, rather than being smuggled in as an advance step. |
| `action: "observe"` | fire nothing; read what already happened. |
| `log_since: "combat_start"` | read the log from the byte offset taken **before** the fight began, not from this step's start. Anything on `ON_COMBAT_START` -- a Trainer partner summon -- has already been written by the time step 1 runs, and would otherwise read as "did not fire". |
| `settle_seconds` | how long to wait before measuring. Default 3. `_performAiDecision` returns a Task that is **not awaited**, so an immediate read races the continuation and a real effect reads as a no-fire. |

`crucible_use_ability` has **no entity parameter** — it always fires as
`CombatPhase._activeCharacterEntity`. `actor` is therefore an assertion, not a selection: if the
named actor is not the active entity the step is reported `SETUP-FAILED`, never quietly fired by
whoever happened to be up.

`ability` is the **config id**, matched case-insensitively. `FireAbility`
(`CombatDriveCommands.cs:571-596`) compares the string against `AbilityAction.AbilityName`, which
is the config key -- so `MAGIC_DARK_ATTACK`, never `"Dark Bolt"`. On a miss it prints the
character's available `AbilityName`s, which is the fastest way to find the right one.

`log.proc` matches against the `[ClassForge] proc <RecipeId> (<tag>) owner=<guid> actions=<n>`
lines the recipe dispatcher writes, read from the byte offset captured at the start of the step.
`log.forbid` is the fail-safety half — a `Recipe effect failed (skipped, rest of plan continues)`
line is a **PASS for fail-safety and a FAIL for correctness**, and is reported as both, separately.

### `tells` — at least two, at least one `identity`

```jsonc
{
  "id": "identity_hud",
  "kind": "identity",                 // "identity" | "effect" | "preview"
  "region": "party",                  // a region name from evidence.py
  "expect": "the party HUD card reads Vampiric",
  "invalidated_by": ["Shepherd", "Thief", "Corsair", "Bladedancer"]
}
```

**Every scenario needs `>= 2` tells and `>= 1` of `kind: "identity"`.** `bench.py` refuses to run
a manifest that does not, because a single tell gets misread and an effect tell with no identity
tell cannot say which class produced it. Our classes display as
**Vampiric / Pacifist / Pokemon Trainer / Gary / Chaos Mage**; *Shepherd / Thief / Corsair /
Bladedancer* are **vanilla** and invalidate the frame.

`expect` is written **in advance**, in the manifest, so the reviewer is checking a stated claim
rather than narrating whatever the picture happens to contain.

Regions come from `evidence.py`: `full` (orientation only — a creature is ~30px at 1577x981),
`board`, `portraits`, `party`, `enemy`, `toolbelt`, `panel`, `tooltip`.

### `asserts` — the LOG half, from state

```jsonc
{ "id": "bat_died", "kind": "alive", "selector": "enemy:0", "op": "eq", "value": false }
```

| `kind` | fields | reads |
|---|---|---|
| `hp` | `selector`, `op`, `value` | `combatants[].hp` |
| `hp_delta` | `selector`, `op`, `value` | hp now minus hp at the baseline for this step |
| `max_hp` | `selector`, `op`, `value` | `combatants[].maxHp` |
| `alive` | `selector`, `op`, `value` (bool) | `combatants[].alive` |
| `status_present` | `selector`, `status` | `combatants[].statuses[].id` |
| `status_absent` | `selector`, `status` | as above, negated |
| `status_duration` | `selector`, `status`, `op`, `value` | the matching status's `duration` |
| `custom_data` | `selector`, `key`, `op`, `value` | `combatants[].customData{}` |
| `thing_custom_data` | `selector`, `thing_config`, `key`, `op`, `value` | `combatants[].things[].customData{}` |
| `tile_aura` | `tile`: `{x,y}` **or the literal `"any"`**, `status`, optional `op: "not_contains"` | `combat.tiles[].auraStatuses[]` |
| `count` | `selector` (a filter form), `op`, `value` | how many combatants match |
| `stat` | `selector`, `stat`, `op`, `value` | `combatants[].stats{}` — **BASE stats; a buff does not move these** |

Ops: `eq`, `ne`, `lt`, `lte`, `gt`, `gte`, `contains`, `not_contains`.

`tile_aura`'s `status` may be a **list**, and for `StatusOneOf` effects it must be. Wild Magic
Surge draws one of `STATUS_FIRE_00 / WATER / SHOCK / ACID` per proc, so naming a single id would
fail three good surges out of four -- the claim is that a hazard landed, not which element it was.
The report prints which id actually landed on which tile.

`tile: "any"` is not laziness. `SKILL_CF_CHAOSMAGE_WILD_MAGIC_SURGE` targets `RANDOM_TILE`, so no
coordinate is knowable in advance; the assert is "the hazard landed *somewhere*" and the
screenshot tell is what pins it to the right decal on the right tile. The scan refuses to report a
false absence: if **any** tile reports `auraStatuses: null` the whole assert comes back
`UNREADABLE`, because a blind tile could be the one holding the hazard.

**Null is not empty.** `"statuses": null` means Crucible could not read them and always comes with
a `warnings` entry; `"statuses": []` means there are none. Every assert here reports
`UNREADABLE` — not `FAIL`, and never `PASS` — when the field it needs is `null`, and the run
carries the matching `member_missing:` warning into the report. An assert that treated null as
empty would pass while the oracle was blind. This matters most for `tiles[].auraStatuses`, which
the game leaves null until the first aura lands.

### Selectors

| form | resolves to |
|---|---|
| `class:<name-or-configId>` | first live non-tile combatant whose `name` or `classId` matches (case-insensitive substring on name, exact on classId) |
| `ally:<n>` / `enemy:<n>` | nth non-tile combatant with `groupIndex` 0 / 1, in roster order |
| `ordinal:<n>` | the combatant with that roster `ordinal` — the **peer-stable** identity |
| `active` | `combat.activeId` |
| `summon:<n>` | nth combatant with `isSummon: true` |
| `name:<text>` | first non-tile combatant whose display name contains `text` |

Entity **GUIDs are local** (`Guid.NewGuid()` per peer) so they are never written into a manifest.
`ordinal` is the cross-peer key.

---

## Running it

    python FTK2.Crucible/tools/bench.py list
    python FTK2.Crucible/tools/bench.py run  iso-vampiric-batswarm
    python FTK2.Crucible/tools/bench.py run  --all
    python FTK2.Crucible/tools/bench.py run  iso-gary-refusal --twice        # determinism self-check
    python FTK2.Crucible/tools/bench.py seed-search iso-gary-refusal
    python FTK2.Crucible/tools/bench.py verify-pin
    python FTK2.Crucible/tools/bench.py attest <run-dir>                     # record what the crops showed
    python FTK2.Crucible/tools/bench.py verdict <run-dir>                    # recompute the pair

## Building a fixture

    python FTK2.Crucible/tools/build_fixture.py batch-4class \
        --classes CF_ORIG_VAMPIRIC,CF_ORIG_PACIFIST,CF_ORIG_TRAINER,CF_ORIG_GARY

`build_fixture.py` wraps `make-fixture.ps1` -- the already-proven cold-boot-through-real-character-
creation recipe -- and adds the three things the bench needs around it:

1. **Character creation, never `crucible_party_set_class`.** Measured on the existing shared
   fixture, 2026-08-26:

       [0] classId=CF_ORIG_VAMPIRIC name=Thief
       [1] classId=CF_ORIG_PACIFIST name=Shepherd
       [2] classId=CF_ORIG_TRAINER  name=Corsair
       [3] classId=CF_EOR_RUNEMAGE  name=Bladedancer

   Four vanilla names on a party of our classes. `set_class` rewrote `ConfigName` and left the
   display name stale, so every screenshot from that bed is worthless for identity. This is not a
   hypothetical -- it is what the party gate found on the first real load.

2. **Proves the party before accepting it**, with `crucible_party_list` *and* a party-HUD crop.
   A fixture that fails the gate is rejected and the manifest is left at `not-built`, which is the
   honest state.

3. **Copies the capture outside `GameRuns\`** and flips `fixture.status` to `built` with the real
   run id. Outside `GameRuns\` matters: a run that ENDS -- win or lose -- rewrites and can delete
   its own save, and a driven run autosaves over it mid-session.

`make-fixture.ps1` takes exactly four class ids. For an isolation bed, put the subject in slot 1
and fill the rest; extra party members do not invalidate an identity tell that names the subject.

### Arming a counter-gated skill -- read this before writing a new manifest

**Only a counter marked `Persistent: true` exists in state at all.** `CF_COUNTER_<name>` is written
to `CharacterComponent.CustomData` by `ExecPersistCounter` (`RecipeActionExecutor.cs:216`) and
nowhere else; a non-persistent counter lives only in the in-memory `CombatRuntime._counters`.

In `CF_PACK_ORIGINALS` exactly **one** counter is persistent: `cf_bond`, on
`SKILL_CF_TRAINER_GOTTA_TRAIN_EM`. In particular `cf_gorged` -- the counter that gates the
Vampiric's Bat Swarm -- is **not**, so it can be neither pre-set by a `custom_data` setup op nor
read by a `custom_data` assert. `iso-vampiric-batswarm` therefore *earns* the counter in-scenario
across three rounds, and the summon is the only observable proof the gate passed. Check which
kind you are dealing with before designing around a counter.

### Proving the instrument itself

    python FTK2.Crucible/tools/bench_selftest.py     # offline, no game needed
    python FTK2.Crucible/tools/verify_pin.py --run-id <guid> --seed 20260826

`bench_selftest.py` exercises the pure layer against hand-built snapshots: the verdict table,
`UNREADABLE` vs `FAIL`, the manifest rules, the digest redaction. bench.py is a **measuring
instrument**, and this project has twice been burned by an instrument that was wrong in a way
nobody could see (`pin_seed` wrote a readonly label; `fixture_health` only checks file size) -- so
the parts that decide PASS or FAIL are tested before they are ever pointed at the game.

`verify_pin.py` is the seed check, and it deliberately does not trust the plugin's own success
string. See its docstring for why both halves -- same seed reproduces, a different seed diverges --
are needed and why either one alone proves nothing.

### Validation

`bench.py validate` (also run automatically before every scenario) checks the manifest against the
rules above — the two-tell / one-identity rule, `seed` vs `seed_search`, unknown assert kinds,
unresolvable assert ids in `seed_search.success_assert`, and `load_copy` tampering — offline, with
no game running.
