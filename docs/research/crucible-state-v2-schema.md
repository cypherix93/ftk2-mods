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
      { "id": "<entity guid>", "name": "<runtime>", "classId": "CF_ORIG_TRAINER", "isPlayer": true,
        "hp": 34, "maxHp": 40, "alive": true,
        "tile": { "x": 3, "y": 1 }, "groupIndex": 0, "ordinal": 17,
        "isSummon": false, "isTile": false,
        "statuses": [ { "id": "STATUS_ATTACKUP_00", "duration": 2, "initialDuration": 2,
                        "tickDuration": 0, "originEntityId": "<entity guid>" } ],
        "stats": { "STR": 4 },
        "customData": { "CF_COUNTER_RAGE": "3", "CF_TRAINER_STARTER": "GRASS" },
        "things": [ { "id": "<thing guid>", "configName": "ARM_ORIG_TRAINER_BALL_GRASS",
                      "customData": { "CF_POKE_HP": "8", "CF_POKE_MAXHP": "12",
                                      "CF_POKE_DOWNED": "0", "CF_POKE_CONFIG": "...",
                                      "CF_POKE_STAGE": "1", "CF_POKE_NICKNAME": "Sparky" } } ] }
    ],
    "tiles": [
      { "x": 3, "y": 1, "groupIndex": 0, "rowPositionsType": "FRONT",
        "auraStatuses": ["STATUS_FIRE_00"], "ordinal": 4,
        "occupantId": "<entity guid>", "occupantOrdinal": 17 }
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
| `.tile.x` / `.tile.y` | `VenueComponent.TilePosition` (`VenueComponent.cs:5`, declared `(int x, int y)`). **`x` is the ROW/DEPTH axis, `y` is lateral** — `VenueHelper.cs:988/998` takes Min/Max of `.x` to find the front and back rows. Read reflectively as `Item1`/`Item2`; a ValueTuple's element names are compiler metadata, not members. `null` when the entity carries no `VenueComponent` (i.e. is not on the board) |
| `.groupIndex` | `CharacterComponent.GroupIndex` — 0 = player side, 1 = enemy (`CharacterHelper.GetGroupIndex`, `CharacterHelper.cs:331-334`) |
| `.ordinal` | index in `CombatState.Entities` — the **peer-stable identity** the determinism layer keys on (`ClassForge.Core/Rng/EntityKey.cs`). `null` when the entity was not located in the roster |
| `.isSummon` | `eActorProperties.SUMMON` in `CharacterComponent.Properties`, which is exactly what `CharacterHelper.ActorHasProperty` does (`CharacterHelper.cs:2010-2017`). Set by `TryCreateSummon` for non-COMPANION summons (`CombatHelper.cs:626`) |
| `.isTile` | the entity carries a `VenueTileComponent`. Tiles live in `CombatState.Entities` alongside actors, so they hold roster ordinals and appear in `combatants[]` too |
| `.customData{}` | `CharacterComponent.CustomData`, **verbatim and unfiltered** — see "Custom state" below |
| `.things[]` | `CharacterComponent.Things`, restricted to entries whose `Thing.CustomData` is non-empty |
| `tiles[]` | entities in `CombatState.Entities` carrying a `VenueTileComponent` (`CombatHelper.cs:648`) |
| `tiles[].x` / `.y` | the tile entity's own `VenueComponent.TilePosition`, same axis convention |
| `tiles[].groupIndex` / `.rowPositionsType` | `VenueTileComponent.GroupIndex` / `.RowPositionsType` (`eTileRowPositions`, stringified) |
| `tiles[].auraStatuses[]` | `VenueTileComponent.AuraStatuses` — **where tile effects live** |
| `tiles[].occupantId` / `.occupantOrdinal` | the actor standing on the tile, matched by POSITION alone, which is what the game itself does (`CombatHelper.cs:901`). A large actor's `VenueComponent.OccupiedTiles` are all registered, so every covered tile names it |

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

## Custom state (`customData` / `things[].customData`)

Every ClassForge class feature stores its state in a `Dictionary<string,string> CustomData` — on
`CharacterComponent` for per-character state and on `Thing` for per-item state. Both are emitted
**verbatim, with no allow-list**, so a new feature's state becomes assertable without another
harness change. Keys observed in the tree today (grep `FTK2.ClassForge/src` for `SetCustomData`):

| Key | Owner | Source |
|---|---|---|
| `CF_POKE_CONFIG` / `CF_POKE_HP` / `CF_POKE_MAXHP` / `CF_POKE_DOWNED` / `CF_POKE_STAGE` | ball `Thing` | `TrainerPartnerPersistence.cs:49-68` |
| `CF_POKE_NICKNAME` | ball `Thing` | `TrainerPartnerNicknames.cs:44` |
| `CF_COUNTER_<name>` | `CharacterComponent` | `RecipeEngineHost.cs:164`, written at `RecipeActionExecutor.cs:216` |
| `CF_TRAINER_STARTER` / `CF_TRAINER_LEVEL_SEEN` | `CharacterComponent` | `TrainerCharmProgression.cs:116/120` |
| `SUMMONED_BY` | `CharacterComponent` | vanilla; holds an entity GUID |

**There is no `CF_CHARM_*` key.** Charm progression stores its state under `CF_TRAINER_STARTER` and
`CF_TRAINER_LEVEL_SEEN`; a `CF_CHARM_*` prefix appears nowhere in the tree. This is exactly why the
emission is not allow-listed.

`SUMMONED_BY` holds an `Entity.Guid`, which is **LOCAL** (`EntityKey.cs`) — two healthy peers
disagree on it by construction — so it is redacted from the digest, as is `things[].id`
(`Thing.Id` is `Guid.NewGuid()`, `InventoryHelper.cs:83`).

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
The same holds for `combatants`, `stats`, `tiles`, `customData`, `things` and
`tiles[].auraStatuses`. An assertion that treats null as empty will pass while the oracle is blind.

That distinction is load-bearing for `auraStatuses` in particular: `VenueTileComponent.AuraStatuses`
is a `List<string>` the game leaves **null** until the first aura lands, so a null VALUE is a clean
tile (`[]`) while an ABSENT member — a game update renaming the field — is `null` plus a
`member_missing: VenueTileComponent.AuraStatuses` warning. The reader tells them apart by checking
whether the member is *declared*, not by whether its value is null.

**Tiles are also combatants.** Venue tile entities share `CombatState.Entities` with actors, so they
appear in `combatants[]` with `"isTile": true`, a `tile` position, no `CharacterComponent`, and a
`combatant has no CharacterComponent` warning. Filter on `isTile` when iterating actors.

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
`console`, `network.isHost`, `combat.episode`, `combat.activeId`, `combat.combatants.id`,
`combat.combatants.statuses.originEntityId`, `combat.combatants.things.id`,
`combat.combatants.customData.SUMMONED_BY`, `combat.combatants.things.customData.SUMMONED_BY` and
`combat.tiles.occupantId` are excluded. Everything else — hp, statuses, stats, turn, phase, round,
wave, **and now position, group, roster ordinal, custom data and tile auras** — is in the hash.
`ftk2_compare_state` still compares v1 digests.

**v2 digests MOVED when the board fields landed.** Any recorded v2 baseline from before this change
is stale and must be re-taken. Emission order is deterministic, so the two-run determinism check
(`VERIFICATION-METHOD.md` §6) stays valid: MiniJson writes object keys ordinal-sorted, `tiles[]` is
sorted by `(x, y, groupIndex)`, `auraStatuses[]` ordinally, `things[]` by
`(configName, custom-data content, id)` with the local GUID as a last-resort tiebreak only, and
`combatants[]` keeps `CombatState.Entities` order, which is the game's own replicated order.
