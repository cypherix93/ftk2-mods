# `crucible.state.v2` — the combat observation surface

Served by `GET /state?schema=v2`. `crucible.state.v1` is still served unchanged at `GET /state`.

> **MCP note (S1 offline scope):** the plan calls for exposing `schema` through the MCP
> `ftk2_state` tool (`FTK2.Crucible/mcp/server.js`). That file is owned by another in-flight agent
> and was deliberately left untouched by this work. Today `ftk2_state` reaches only v1 through MCP;
> v2 is reachable directly via `GET /state?schema=v2`. Wiring the MCP arg through is a small,
> separate follow-up once `server.js` is free.

Every member name below was read out of the retail assembly and is recorded in
`docs/research/crucible-combat-field-map.md`. Two fields are **not** read from the game at all — see
"Synthesized fields".

## Shape

```jsonc
{
  "schema": "crucible.state.v2",
  "instance": "p1",
  "route": "COMBAT",
  "run":     { "present": true, "seed": null, "day": null, "gold": null, "chapter": null },
  "network": { "online": false, "isHost": true, "playerCount": 1 },
  "combat": {
    "active": true,
    "round": 2,
    "wave": 0,
    "turn": 5,
    "phase": "PLAYER",
    "activeId": "<entity guid>",
    "episode": "1a2b3c4d",
    "synthesized": ["turn", "phase"],
    "combatants": [
      { "id": "<entity guid>", "name": "<runtime>", "classId": "CF_EOR_BARD", "isPlayer": true,
        "hp": 34, "maxHp": 40, "alive": true,
        "statuses": [ { "id": "STATUS_ATTACKUP_00", "duration": 2, "initialDuration": 2,
                        "tickDuration": 0, "originEntityId": "<entity guid>" } ],
        "stats": { "STR": 4 } }
    ]
  },
  "console": false,
  "warnings": []
}
```

`run.seed`/`.day`/`.gold`/`.chapter` are shown as `null` above because they are, in practice,
**always** `null` — see "Known-absent run fields" below.

## Where each field comes from

| Field | Source |
|---|---|
| `run.present` | `Env.GameRun` is non-null |
| `run.seed` / `.day` / `.gold` / `.chapter` | `GameRunData.Seed` / `.Day` / `.Gold` / `.Chapter` — **grounded as NOT FOUND**; see below |
| `network.isHost` | `NetworkData.IsHost` |
| `network.playerCount` | count of `NetworkData.PlayerList` |
| `combat.active` | `CombatState.Entities` is non-empty |
| `combat.round` | `CombatState.TotalRounds` |
| `combat.wave` | `CombatState.WaveIndex` |
| `combat.activeId` | `CombatPhase._activeCharacterEntity` → `Entity.Guid` (falls back to `_lastEngagedEntity`) |
| `combatants[]` | `CombatState.Entities`, in list order |
| `.id` | `Entity.Guid` |
| `.name` | `CharacterComponent.DisplayName` |
| `.classId` | `CharacterComponent.ConfigName` |
| `.isPlayer` | presence of a `PlayerComponent` in `Entity.Components` — **derived**, there is no boolean field |
| `.hp` | `CharacterComponent.CurrentHealth` |
| `.maxHp` | `CharacterHelper.GetMaxHealth(Entity, cappedStat: true)` — **computed**, not stored. Grounded (Task 1 addendum) as the one binary overload; `cappedStat: true` matches what the game's own health bar shows. |
| `.alive` | `CharacterHelper.IsDead(Entity)`, negated |
| `.statuses[].id` | key of `StatusEffectComponent.Statuses` |
| `.statuses[].duration` / `.initialDuration` / `.tickDuration` / `.originEntityId` | `StatusEffectInfo` fields of the same name |
| `.stats{}` | `CharacterHelper.GetBaseStats(Entity)`, string-keyed |

## Known-absent run fields

`GameRunData` has **no** `Seed`, `Day`, `Gold`, or `Chapter` member — confirmed by directly probing
the type with TypeProbe (`docs/research/crucible-combat-field-map.md`, "S1 addendum"). `StateReader`
v1 has read these same four names since it was written, with the warning sink passed as `null`
(`GetMember(gameRun, "Seed", null)`), so every one of those reads has silently returned `null` for
the life of the mod — the same failure class as `NetworkData.PlayerCount`.

v2 does not invent replacement names. It reads the same four names v1 does, but with the warning
sink wired in, so every `/state?schema=v2` call carries `member_missing: GameRunData.Seed`,
`.Day`, `.Gold`, and `.Chapter` in `warnings` — loud instead of silent. **Do not assert on
`run.seed`/`.day`/`.gold`/`.chapter`; they are always `null` today.** Grounding a real replacement
(the closest analogs found while probing are `GameRunData.MapGenSeed`, `.RoundCount`, and
`.GameStageIndex` — none confirmed to mean the same thing) is future work, not part of S1.

## Caveats an assertion must respect

**`round` is NOT monotonic.** `CombatState.TotalRounds` initialises to `-1` and is reset to `-1`
again **per wave**. A multi-wave fight rewinds it. Never assert `round` increases; assert on `turn`,
which is monotonic within an episode, and use `wave` to segment.

**`stats{}` is BASE stats.** It comes from `CharacterHelper.GetBaseStats`, not from the 8-overload
`GetStat`, whose signature has never been grounded. Do not assert that a buff moved a value here.

**There is no `stacks` field.** `StatusEffectInfo` has no stack count. Stacking appears to be
encoded as distinct suffixed ids (`STATUS_ATTACKUP_00`, `_01`), which is unverified, so nothing is
derived from it. Assert on ids; count matching ids yourself if you need a tier count.

**`null` and `[]` mean different things.** `"statuses": []` is "this combatant has no statuses".
`"statuses": null` is "Crucible could not read them" — always accompanied by an entry in `warnings`.
The same holds for `combatants` and `stats`. An assertion that treats null as empty will pass while
the oracle is blind.

**Read `warnings` on every failure.** Every reflective read reports there. A member renamed by a
game update shows up as `member_missing: <Type>.<Member>` in the very next snapshot. Expect (and
ignore, for now) the four `GameRunData` run-field misses documented above.

## Synthesized fields

`turn` and `phase` do not exist as readable data anywhere in the assembly — combat runs
procedurally through `CombatPhase._engageActiveEntity` / `_nextTurn`. Crucible synthesizes both from
Harmony postfixes on those two methods, and says so in `combat.synthesized`.

- `turn` — an ordinal of observed advances within one combat episode, 0-based. `null` outside
  combat, and `null` when the hooks failed to install.
- `phase` — `PLAYER` / `ENEMY` from whether the engaged entity has a `PlayerComponent`; `NONE`
  between episodes; `UNKNOWN` when the hooks failed to install.
- `episode` — an object-identity key for the current `CombatState`. Local to a process; redacted
  from the digest. Use it to tell "turn 3 of this fight" from "turn 3 of the last one".

`_nextTurn` returns `Task` (Task 1 addendum) and takes one `Boolean` parameter — the postfix fires
when the async state machine is *created*, not when the turn visibly completes. That is still
exactly one fire per advance, in call order, which is all the ordinal needs; `_engageActiveEntity`
is synchronous (`Void`, no parameters), so its postfix has no such caveat.

The same hooks emit `combat_start` / `turn` / `phase` / `combat_end` entries into the JSONL trace,
correlated as `combat-<episode>`, so `ftk2_read_trace` shows what the fight did and not only what
was asked of it.

## Digest

`GET /state?schema=v2` returns a digest computed with `Redactions.V2`: `instance`, `warnings`,
`console`, `network.isHost`, `combat.episode`, `combat.activeId`, `combat.combatants.id`, and
`combat.combatants.statuses.originEntityId` are excluded. Everything else — hp, statuses, stats,
turn, phase, round, wave — is in the hash. `ftk2_compare_state` still compares v1 digests.
