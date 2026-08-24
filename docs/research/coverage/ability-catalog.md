# Ability Catalog — shipped patterns to copy from

Source of truth: `Abilities.json` (992 entries, keyed by ability id) and `StatusEffects.json` (189 entries) in
`For The King II_Data\StreamingAssets\Assets\Configs\JSON~\`. All counts below were produced by loading both
files with Python's `json` module (`encoding='utf-8-sig'`, they carry a BOM) and iterating the dicts — see the
"how counted" note under each table. Every ability id and status id quoted below is copy-pasted verbatim from
those files; nothing here is inferred or invented.

Field resolution note: several abilities omit fields like `TargetArea`/`Target`/`TileOccupancy` and rely on
`"Inherits": "DEFAULT_ABILITY"` (or another ability) to supply them. All totals and targeting-combo counts below
were computed by walking the `Inherits` chain to the first ability that actually sets the field (falling back to
`DEFAULT_ABILITY`'s value), so the numbers reflect what a modder would see at runtime, not just what's literally
in the child's JSON block. `DEFAULT_ABILITY` itself is:

```json
{
  "TargetArea": "SINGLE",
  "Target": "ENEMY",
  "TileOccupancy": "FULL",
  "OriginRowPosition": "ANY",
  "TargetRowPosition": "ANY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Chargeable": false,
  "Ammo": 0,
  "AnimationIdentity": "STANDARD",
  "Actions": [],
  "Tendency": "NONE"
}
```

---

## TOTALS

**Total abilities in Abilities.json: 992** (counted as `len(json.load(open('Abilities.json')))`, i.e. the number
of top-level keys, including `DEFAULT_ABILITY` itself and utility abilities like `SKIP_TURN`, `BASIC_MOVE`).

### Verb usage (eCombatActions in `Actions[].Item1`)

Counted by iterating every ability's `Actions` array and tallying `Item1`. An ability with multiple actions (e.g.
damage + status) contributes once to each verb it uses, so these numbers are "how many action-entries use this
verb" — an ability can appear in more than one row.

| Verb | Count |
|---|---|
| ADD_STATUS | 798 |
| CHANGE_STAT | 683 |
| MOVE | 66 |
| VEHICLE_DAMAGE | 34 |
| ADD_CHARACTER | 34 |
| REMOVE_STATUS | 32 |
| FLEE | 2 |
| VOIDWALK | 1 |
| REVIVE_ALLY | 1 |
| VEHICLE_REPAIR | 1 |
| ADD_CHARACTER_INSTANT | 1 |
| GRAB | 0 (never shipped) |
| REVIVE_CHARACTER | 0 (never shipped — all revives ship as `REVIVE_ALLY`) |
| ADD_CHARACTER_SMOKE | 0 (never shipped) |
| EQUIP_WEAPON | 0 as a verb (it's an ability *id*, whose one action is a plain `MOVE`) |

Gotcha: `GRAB`, `REVIVE_CHARACTER`, and `ADD_CHARACTER_SMOKE` are legal `eCombatActions` members per the task
context but do not appear in a single shipped `Actions` entry across all 992 abilities. Don't assume a listed
enum member has shipped precedent — verify before copying its "shape."

### TargetArea (resolved through Inherits)

| TargetArea | Count |
|---|---|
| SINGLE | 547 |
| SPLASH | 164 |
| SELF | 85 |
| AOE | 73 |
| ALL_GROUP | 37 |
| COLUMN | 36 |
| SWIPE | 28 |
| ROW | 14 |
| CHECKERED | 6 |
| NONE | 1 |
| SPLASH_WAVE | 1 |

### Target (resolved through Inherits)

| Target | Count |
|---|---|
| ENEMY | 670 |
| SELF | 179 |
| ALLY_ALL | 97 |
| ALLY | 21 |
| ANY | 21 |
| SELF_PICK | 2 |
| ALLY_NEARBY | 2 |

### TileOccupancy (resolved through Inherits)

| TileOccupancy | Count |
|---|---|
| FULL | 793 |
| ANY | 154 |
| EMPTY | 45 |

### Flags

- `IsMajorAction`: **803 true / 189 false** — counted as a straight tally of the resolved boolean across all 992.
- `RequiresFocus > 0`: **2 abilities / 990 at 0.** Focus-gated abilities are the rare exception, not the norm — don't default a new ability to `RequiresFocus > 0` unless you have a specific reason.

### Tendency (AI targeting bias, resolved through Inherits)

| Tendency | Count | Tendency | Count |
|---|---|---|---|
| NONE | 749 | MOSTEVASION | 5 |
| MOSTHEALTH | 56 | MOSTRESISTANCE | 4 |
| BACKROW | 22 | MOSTGOLD | 4 |
| LEASTHEALTH | 22 | MOSTFOCUS | 4 |
| LEASTARMOR | 22 | NOTHASPOISON | 3 |
| FRONTROW | 20 | MUSTHAVEPOISON | 2 |
| MOSTARMOR | 19 | MUSTNOTHAVEHIVE | 1 |
| MOSTDAMAGE | 15 | MUSTHAVELOWHEALTH | 1 |
| SLOWEST | 10 | | |
| FASTEST | 10 | | |
| LEASTRESISTANCE | 9 | | |
| ISBUFFED | 7 | | |
| LEASTEVASION | 7 | | |

---

## ARCHETYPES

### 1. Plain single-target damage

112 abilities have `Actions == [CHANGE_STAT(HP)]` exactly (one action, no status, resolved `TargetArea == SINGLE`).

`AXE_BASIC_ATTACK`:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": true,
  "Actions": [
    {
      "Item1": "CHANGE_STAT",
      "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false }
    }
  ]
}
```

`BLADE_BASIC_ATTACK` is the same shape with the same `CHANGE_STAT` block — the two weapon families literally
share the identical action payload; only `Inherits`, animation, and outer weapon config differ. This is the
minimum viable "hit it" ability: inherit `DEFAULT_ABILITY`, set `OriginRowPosition`, and give it one
`CHANGE_STAT` action with `Stat: HP`.

### 2. Damage + status ("the attack that also burns")

443 abilities carry both `CHANGE_STAT(HP)` and `ADD_STATUS` in the same `Actions` array. This is the dominant
authoring pattern (`ADD_STATUS` alone touches 798/992 abilities).

`AXE_BLEED_ATTACK`:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": true,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_BLEED_00" }
  ]
}
```

`AXE_HEAVY_STUN_ATTACK` — same two-action shape, different status:
```json
"Actions": [
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_STUN_00" }
]
```

`BLADE_FIRE_ATTACK` — same shape, elemental status:
```json
"Actions": [
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_FIRE_00" }
]
```

To author "an attack that also burns": copy the two-entry `Actions` array — `CHANGE_STAT(HP)` first, then
`ADD_STATUS` with your chosen status id. **Reminder (from context, load-bearing):** the `ADD_STATUS` entry only
lands on a PERFECT skill roll, so this is a chance-on-crit effect, not a guaranteed one.

### 3. AoE / SPLASH damage

150 abilities combine `TargetArea SPLASH`/`AOE` with a damaging `CHANGE_STAT(HP)`.

`BOOK_AOE_ATTACK` (magic AOE, also hits vehicles):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "AOE",
  "TileOccupancy": "ANY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "VEHICLE_DAMAGE", "Item2": 2 },
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": true, "IsSilent": false } }
  ]
}
```

`BLUNT_GROUND_ATTACK` (SPLASH melee ground-slam, damage + daze, targets backrow):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "SPLASH",
  "TileOccupancy": "ANY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": true,
  "Tendency": "BACKROW",
  "Actions": [
    { "Item1": "VEHICLE_DAMAGE", "Item2": 2 },
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_DAZE_00" }
  ]
}
```

`BOOK_FIRE_AOE_ATTACK` — same AOE shape as `BOOK_AOE_ATTACK` plus a status:
```json
"Actions": [
  { "Item1": "VEHICLE_DAMAGE", "Item2": 4 },
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": true, "IsSilent": false } },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_FIRE_00" }
]
```

`AOE` abilities in this dataset consistently pair with `TileOccupancy: "ANY"` and `IsRanged: true`; `SPLASH`
abilities appear with both `TileOccupancy: "ANY"` and `"FULL"` depending on whether they can hit an empty tile.

### 4. Pure heal, and heal-over-time / REGEN

`BASIC_HEAL_01` — self heal via a negative-`HP` `CHANGE_STAT` with `FlatPercent` (recall: HP sign is inverted,
so a **negative** value here is healing, not damage):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "SELF",
  "Target": "SELF",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": false, "IsSilent": false, "FlatPercent": -50 } }
  ]
}
```

`MEDIC_HEAL_01` — party-wide heal, identical `CHANGE_STAT` block, only `Target: "ALLY_ALL"` differs:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ALLY_ALL",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": false, "IsSilent": false, "FlatPercent": -50 } }
  ]
}
```

Heal-over-time as a direct `CHANGE_STAT` tick, `TICK_REGEN_00` (`Type: "REGEN"`, negative `FlatValue`):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ANY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "REGEN", "IsBlockable": false, "IsSilent": false, "FlatValue": -5 } }
  ]
}
```

Heal-over-time as an attached status instead of a direct tick, `FISH_REGEN_01` (this is the shape to copy when
you want a lingering regen buff rather than an instant heal):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "SELF",
  "Target": "SELF",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "ADD_STATUS", "Item2": "STATUS_REGEN_01" }
  ]
}
```
(`STATUS_REGEN_01` itself: `Type REGEN, Duration 8, TickFrequency 1, TickOverworld true, TickCombat true` — see STATUS_CATALOG.)

26 abilities in total have `HEAL` or `REGEN` in their id (`MAGIC_HEAL_ATTACK`, `POTION_HEAL_ATTACK`,
`POTION_HEAL_SPLASH_ATTACK`, `BASIC_HEAL_02/03`, `BASIC_HEAL_FULL_01`, `SKILL_PARTYHEAL_01`, etc.) — a good
starting search list if you need more heal variants to copy from.

### 5. Buff ally / debuff enemy with no damage — the ONLY_* family

Confirmed: 30 `ONLY_*` abilities exist and none of them carry a `CHANGE_STAT` action — every one is
`Actions: [ADD_STATUS]` only (checked directly: no `ONLY_*` id has `CHANGE_STAT` in its `Actions`). These are the
canonical "status only, no damage" attacks, split between self/ally buffs and enemy debuffs by `Target`.

`ONLY_ATTACKUP_ATTACK` (self buff, `Target: SELF`, biased by tendency toward the lowest-armor ally):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "SELF",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Tendency": "LEASTARMOR",
  "Actions": [
    { "Item1": "ADD_STATUS", "Item2": "STATUS_ATTACKUP_00" }
  ]
}
```

`ONLY_RESISTDOWN_ATTACK` (enemy debuff, `Target` unset → resolves to `ENEMY` via `DEFAULT_ABILITY`):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Tendency": "MOSTRESISTANCE",
  "Actions": [
    { "Item1": "ADD_STATUS", "Item2": "STATUS_RESISTANCEDOWN_00" }
  ]
}
```

`ONLY_STUN_ATTACK` (pure control debuff, no damage, no `Target` override — resolves `ENEMY`):
```json
"Actions": [
  { "Item1": "ADD_STATUS", "Item2": "STATUS_STUN_00" }
]
```

### 6. Summon a creature (ADD_CHARACTER)

34 abilities use `ADD_CHARACTER`. **Every single shipped one uses `"Type": "RANDOM"` — none use `"SPECIFIC"`**
(checked directly: zero abilities have `ADD_CHARACTER.Item2.Type == "SPECIFIC"`). The task context distinguishes
RANDOM vs SPECIFIC as a real fork in the schema, but only RANDOM has shipped precedent — if you need SPECIFIC,
you're off the beaten path with no shipped example to copy.

`SUMMON_WOLF_ATTACK`:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ALLY_ALL",
  "TileOccupancy": "EMPTY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "ADD_CHARACTER", "Item2": { "Type": "RANDOM", "Value": "WOLF" } }
  ]
}
```

`SUMMON_UNDEAD_ATTACK` and `SUMMON_HELLHOUND_ATTACK` and `SUMMON_BANDIT_ATTACK` are byte-identical in shape,
only `Item2.Value` differs (`"SKELETON"`, `"HELLHOUND"`, `"BANDIT"`) — this is the copy-paste template for "add
a new creature type to the summon roster": `Target: ALLY_ALL`, `TileOccupancy: EMPTY` (an open tile on your own
side, see TARGETING_COOKBOOK), one `ADD_CHARACTER` action with `Type: RANDOM`.

`ADD_CHARACTER_INSTANT` has exactly one shipped user, `COMPANION_JELLY_SKELLY_DEATH` — a death-trigger spawn,
distinct from the turn-taken `ADD_CHARACTER` summon abilities above.

### 7. Movement / repositioning

66 abilities carry `MOVE`. Three sub-shapes:

Self-repositioning with no combat payload, `BASIC_MOVE` (the party's basic movement ability — `Target: SELF_PICK`, `TileOccupancy: EMPTY`):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "TargetArea": "AOE",
  "Target": "SELF_PICK",
  "TileOccupancy": "EMPTY",
  "IsMajorAction": false,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [ { "Item1": "MOVE" } ]
}
```
(`EQUIP_WEAPON` and `BASIC_RELOAD` are the same one-action `{"Item1": "MOVE"}` shape with `Target: SELF`,
`TileOccupancy: EMPTY` — MOVE with no `Item2` is used generically for "this ability repositions/reconfigures the
user," not only literal walking.)

Push (damage + status + shove the target back), `BLUNT_PUSH_ATTACK`:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": true,
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_DAZE_00" },
    { "Item1": "MOVE", "Item2": "PUSH" }
  ]
}
```

Pull (drag the target toward you), `BOOMERANG_PULL_ATTACK` and `MAGIC_PULL_ATTACK` — identical shape, `Item2:
"PULL"` on the `MOVE` action:
```json
"Actions": [
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_DAZE_00" },
  { "Item1": "MOVE", "Item2": "PULL" }
]
```
So `MOVE`'s `Item2` string is the direction/mode selector: absent for self-repositioning, `"PUSH"` or `"PULL"`
for forced target displacement — always as the last action after damage+status in the shipped push/pull kit.

### 8. Revive

Only one ability ships with a `REVIVE_ALLY` action across the whole file: `REVIVE_ALLY` itself.
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ALLY_ALL",
  "TileOccupancy": "EMPTY",
  "IsMajorAction": true,
  "RequiresSkillRoll": false,
  "RequiresFocus": 0,
  "IsFocusable": false,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Actions": [
    { "Item1": "REVIVE_ALLY", "Item2": "" }
  ]
}
```
`REVIVE_CHARACTER` is a distinct `eCombatActions` member but has zero shipped uses — `REVIVE_ALLY` covers the
entire revive archetype in this dataset. Note the target: `ALLY_ALL` + `TileOccupancy: EMPTY`, i.e. you're
targeting the empty/downed slot, same occupancy convention as summon abilities (see TARGETING_COOKBOOK).

### 9. Multi-action ability (more than one entry in Actions)

130 abilities have more than two action entries. Representative 3-entry example, `BITE_ARMOR_RESISTANCEDOWN_ATTACK`
(damage + two different debuffs stacked on one hit):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Tendency": "MOSTARMOR",
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_ARMORDOWN_00" },
    { "Item1": "ADD_STATUS", "Item2": "STATUS_RESISTANCEDOWN_00" }
  ]
}
```
A 4-entry example, `INANIMATE_POWDERKEG_EXPLODE` (damage + remove one status + add another status + spawn a new
keg — chained cause/effect in a single ability):
```json
"Actions": [
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false, "FlatValue": 20 } },
  { "Item1": "REMOVE_STATUS", "Item2": "STATUS_WATER_00" },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_FIRE_00" },
  { "Item1": "ADD_CHARACTER", "Item2": { "Type": "RANDOM", "Value": "POWDERKEG" } }
]
```

### 10. Turn-order manipulation (RUSH / INTERRUPT statuses)

`MAGIC_RUSH_ATTACK` — gives an ally a `RUSH` status, `Tendency: SLOWEST` biases the AI to pick its slowest ally:
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "Target": "ALLY",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": true,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Tendency": "SLOWEST",
  "Actions": [
    { "Item1": "ADD_STATUS", "Item2": "RUSH" }
  ]
}
```
`MAGIC_INTERRUPT_ATTACK` — damage plus `INTERRUPT` on the enemy, `Tendency: FASTEST` (interrupt the fastest
threat):
```json
{
  "Inherits": "DEFAULT_ABILITY",
  "OriginRowPosition": "FRONT",
  "DamageAgainstRow": "NONE",
  "IsMajorAction": true,
  "RequiresSkillRoll": true,
  "RequiresFocus": 0,
  "IsFocusable": true,
  "IsRanged": false,
  "Kamikaze": false,
  "Ammo": 0,
  "Chargeable": false,
  "Tendency": "FASTEST",
  "Actions": [
    { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "MAGICAL", "IsBlockable": true, "IsSilent": false } },
    { "Item1": "ADD_STATUS", "Item2": "INTERRUPT" }
  ]
}
```
`WHIP_PULL_ATTACK` combines pull + interrupt in one ability (damage, daze, interrupt, then pull):
```json
"Actions": [
  { "Item1": "CHANGE_STAT", "Item2": { "Stat": "HP", "Type": "PHYSICAL", "IsBlockable": true, "IsSilent": false } },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_DAZE_00" },
  { "Item1": "ADD_STATUS", "Item2": "INTERRUPT" },
  { "Item1": "MOVE", "Item2": "PULL" }
]
```
`WHIP_RUSH_ATTACK` stacks RUSH with a buff:
```json
"Actions": [
  { "Item1": "ADD_STATUS", "Item2": "RUSH" },
  { "Item1": "ADD_STATUS", "Item2": "STATUS_ATTACKUP_00" }
]
```

**Gotcha (see GOTCHAS below):** `"RUSH"` and `"INTERRUPT"` do **not** exist as keys in `StatusEffects.json` —
they are hardcoded/special-cased status ids, not data-driven entries with `Type`/`Duration`/`Stats` like every
other status. You cannot look up their tuning in the JSON the way you can for `STATUS_STUN_00`.

---

## TARGETING_COOKBOOK

Combinations actually observed (via the same `Inherits`-resolved walk used for TOTALS), ranked by frequency,
with what each means in play:

| Target | TileOccupancy | TargetArea | OriginRow | TargetRow | Count | Meaning |
|---|---|---|---|---|---|---|
| ENEMY | FULL | SINGLE | ANY | ANY | 220 | Any enemy, occupied tile only — the default melee/ranged single-target hit |
| ENEMY | FULL | SINGLE | FRONT | ANY | 154 | Same, but caster must be in the front row (melee reach) |
| SELF | FULL | SELF | ANY | ANY | 83 | Self-only, no tile targeting — buffs/heals on the caster |
| ENEMY | FULL | SPLASH | ANY | ANY | 69 | Occupied enemy tile, splash to neighbors |
| ENEMY | ANY | AOE | ANY | ANY | 45 | Whole enemy field, empty tiles included — full AOE nuke |
| SELF | FULL | SINGLE | ANY | ANY | 42 | Self-targeted single-slot ability (buffs that read as "SINGLE" not "SELF" area) |
| ALLY_ALL | FULL | SINGLE | ANY | ANY | 33 | Pick any ally (occupied), e.g. targeted heals |
| SELF | FULL | SPLASH | ANY | ANY | 31 | Self-centered splash (auras, self-triggered AOE) |
| **ALLY_ALL** | **EMPTY** | **SINGLE** | ANY | ANY | 29 | **A free tile on your own side — the summon shape** (also used by revive) |
| ANY | FULL | SINGLE | ANY | ANY | 21 | Any occupied tile, friend or foe |
| ENEMY | ANY | SPLASH | ANY | ANY | 20 | Enemy splash that can also hit empty tiles |
| ALLY | FULL | SINGLE | ANY | ANY | 20 | **An ally** (excludes self) — e.g. RUSH/support abilities |
| ENEMY | FULL | SPLASH | FRONT | ANY | 18 | Front-row-only splash |
| ENEMY | FULL | AOE | ANY | ANY | 16 | All-enemy AOE that requires an occupied origin |
| ENEMY | ANY / FULL | COLUMN | ANY | ANY | 14+14 | **All enemies in a column** (perpendicular to "row") |
| ALLY_ALL | FULL | SPLASH | ANY | ANY | 13 | Party-wide splash heal/buff |
| ENEMY | ANY | ROW | ANY | ANY | 6 | **All enemies in a row** |

Quick lookups the task asked for explicitly (all confirmed shipped combos above):

- **"A free tile on my own side" (summon shape):** `Target: ALLY_ALL`, `TileOccupancy: EMPTY`, `TargetArea: SINGLE` — 29 shipped uses (all `SUMMON_*` abilities plus `REVIVE_ALLY`).
- **"Any enemy":** `Target: ENEMY`, `TileOccupancy: FULL`, `TargetArea: SINGLE` — 220 shipped uses (the modal case in the whole file).
- **"All enemies in a row":** `Target: ENEMY`, `TargetArea: ROW` (`TileOccupancy: ANY`) — 6 shipped uses.
- **"An ally":** `Target: ALLY`, `TileOccupancy: FULL`, `TargetArea: SINGLE` — 20 shipped uses (distinct from `ALLY_ALL`, which includes self).
- **"Myself":** `Target: SELF`, `TargetArea: SELF` or `SINGLE`, `TileOccupancy: FULL` — 83 + 42 shipped uses.

Other confirmed area shapes: `SWIPE` (28, melee front-row arc), `ALL_GROUP` (37, whole side incl. empty in some
cases), `CHECKERED` (6, alternating-tile pattern), and one unique `SPLASH_WAVE` and one `NONE` (`SKIP_TURN`,
which has an empty `Actions` array and no meaningful target).

---

## STATUS_CATALOG

189 status entries in `StatusEffects.json`, grouped by `Type`. Counted by tallying the `Type` field across every
key in the dict. Full group-size table (all 45 distinct `Type` values) is in the "how counted" script output;
below are the groups relevant to authoring, with one representative id and its full tuning per group.

| Group | Representative id(s) | Type | Duration | TickCombat | Stats |
|---|---|---|---|---|---|
| **Damage over time** | `STATUS_BLEED_00` | BLEED | 4 | true | `{}` |
| | `STATUS_FIRE_00` | FIRE | 3 | true | `{}` |
| | `STATUS_POISON_01` | POISON | 3 | false (ticks overworld, not combat) | `STR:-10, VIT:-10, INT:-10, SPD:-10, TAL:-10, AWR:-10` |
| | `STATUS_ACID_00` | ACID | (1 entry, see JSON) | — | — |
| **Heal over time** | `STATUS_REGEN_01` | REGEN | 8 | true | `{}` (heal amount is data-driven elsewhere, not in `Stats`) |
| **Stat change (buff)** | `STATUS_ARMORUP_00` | BUFF | 2 | true | `DEF: 5` |
| | `STATUS_ATTACKUP_00`, family (49 BUFF entries total) | BUFF | varies | varies | ATK/DEF/etc. positive deltas |
| **Stat change (debuff)** | `STATUS_ARMORDOWN_00` | DEBUFF | 2 | true | `DEF: -10` |
| | `STATUS_ATTACKDOWN_00` (16 DEBUFF entries total) | DEBUFF | 2 | true | `ATK: -5` |
| **Control — stun** | `STATUS_STUN_00` | STUN | 1 | true | `{}` |
| **Control — daze** | `STATUS_DAZE_00` | DAZE | 1 | true | `{}` |
| **Control — entangle** | `STATUS_ENTANGLE_ROOTS_00` (5 ENTANGLE variants: ROOTS/CHAIN/SNAKE/WEB/GALLOWS) | ENTANGLE | 2 | true | `{}` (`TileSync: true` — pins the target's tile) |
| **Control — confuse** | `STATUS_CONFUSE_00` | CONFUSE | 2 | true | `{}` |
| **Control — grab** | `STATUS_GRAB_00` | GRAB | 2 | false | `{}` |
| **Control — curse** | `STATUS_CURSE_00` + 7 named curses (`STATUS_CURSE_LETHARGIC`, `_UNWELL`, `_FEEBLE`, `_UNLUCKY`, `_BLIND`, `_CLUMSY`, `_FOOLISH`) | CURSE | 0 | false | `{}` (8 CURSE entries total) |
| **Protection** | `STATUS_PROTECT_00` | PROTECT | 5 | true | `{}` |
| | `STATUS_INFINITE_PROTECT_00`, `STATUS_PROTECT_WHITE_00` | PROTECT | varies | — | — |
| **Reflection** | `STATUS_REFLECT_00` | REFLECT | 2 | true | `{}` |
| **Immunity** | `STATUS_IMMUNITY_STUN` | IMMUNITY | -1 (permanent) | false | `{}` (21 IMMUNITY entries: ACID, BLEED, CONFUSE, CURSE, STUN, ENTANGLE, FIRE, ICE, SHOCK, WATER, PETRIFY, MOVE, POISON, AMBUSH, DEATHMARK, DAZE, STEAL, SCARE, DISTRACT, BADWEATHER, ALL) |
| **Turn order — haste** | `STATUS_HASTE_00` | HASTE | 5 | true | `SA: 2, MOV: 2` (data-driven speed-agility buff) |
| **Turn order — rush/interrupt** | `RUSH`, `INTERRUPT` | **not in StatusEffects.json at all** | — | — | — |
| **Guard / taunt / aggro** | `STATUS_GUARD_00`, `STATUS_TAUNT_00` | GUARD, TAUNT | — | — | — |

45 distinct `Type` values exist total; the ones not itemized above (AURA, CHANNEL_MAGIC, CHARGE, CONCENTRATION,
DEATHMARK, DEATHSAVE, DECOY, LINK, LONEWOLF, MARKED, POLLINATE, PROWESS, PURIFY, RATTLED, ROWDY, SANCTUM,
SCARE, SHOCK, STAR_SHIELD, VIGOR, WATER, ICE, IMBUE_FIRE/ICE/SHOCK/WATER, INFINITE_FIRE, INK, ATONEMENT) are
mechanic-specific one-offs or class-kit statuses — grep `StatusEffects.json` for the `Type` you need before
inventing a new one; there's a good chance it already exists.

---

## GOTCHAS

1. **`ADD_STATUS` only lands on a PERFECT skill roll** (given context, load-bearing for every damage+status
   ability above — e.g. `AXE_BLEED_ATTACK`'s bleed is not guaranteed just because the attack hit).

2. **HP sign is inverted on `CHANGE_STAT`.** Positive = damage, negative = heal. Verified directly in shipped
   data: `BASIC_HEAL_01` and `MEDIC_HEAL_01` both use `"FlatPercent": -50` on a `Stat: "HP"` action to heal, and
   `TICK_REGEN_00` uses `"FlatValue": -5` for its regen tick. Meanwhile `INANIMATE_POWDERKEG_EXPLODE` uses
   `"FlatValue": 20` (positive) to deal damage. Copy the sign from the nearest analogous shipped ability, don't
   assume "positive is good."

3. **The `ONLY_*` family (30 abilities, confirmed) carries no damaging `CHANGE_STAT`.** Every one of them is
   `Actions: [ADD_STATUS]` only. If you want a pure buff/debuff ability with zero damage roll, this is the
   pattern to copy — don't add a `CHANGE_STAT` "just in case," none of the shipped `ONLY_*` abilities do.

4. **`ShuffleAbilityBag` is not an ability field — it lives on the weapon/Thing config**, not in
   `Abilities.json`. Confirmed present in `Things/Weapons.json` and several `Things/ARM_CATALOG_*.json` files
   (e.g. `ARM_CATALOG_S13.json` has `"ShuffleAbilityBag": false`; `Weapons.json` entries have it `true`). Per
   the task context this only affects enemy AI ability selection — setting it on a player-usable ability's own
   JSON block does nothing because that's the wrong file to put it in.

5. **`ADD_CHARACTER` in shipped data is always `"Type": "RANDOM"`.** Zero of the 34 shipped uses have
   `"Type": "SPECIFIC"`. If the engine supports `SPECIFIC` (per task context), it has no shipped precedent to
   copy — you're extrapolating past what's proven to work.

6. **`RUSH` and `INTERRUPT` are not entries in `StatusEffects.json`.** Every other status referenced by
   `ADD_STATUS` (e.g. `"STATUS_BLEED_00"`, `"STATUS_STUN_00"`) resolves to a real key with `Type`/`Duration`/
   `Stats` you can inspect and tune. `"RUSH"` and `"INTERRUPT"` (used directly, e.g. in `MAGIC_RUSH_ATTACK` and
   `MAGIC_INTERRUPT_ATTACK` above) return `null` when looked up the same way — confirmed by direct lookup. They
   are almost certainly hardcoded turn-order signals handled in code rather than data, so don't expect to retune
   their duration/magnitude via `StatusEffects.json` the way you would any other status.

7. **Three `eCombatActions` verbs never shipped at all: `GRAB`, `REVIVE_CHARACTER`, `ADD_CHARACTER_SMOKE`.**
   Confirmed zero occurrences across all 992 abilities' `Actions` arrays. They may well be implemented in code
   (the task context lists them as legal enum members) but there is no shipped JSON shape to copy from — you'd
   be authoring blind if you used them.

8. **`RequiresFocus > 0` is nearly unused (2/992).** Don't reach for focus-gating as a default balancing lever;
   it's a special-case mechanic in this dataset, not a common one.

9. **Inherited fields are invisible in the child's JSON block.** Many abilities (e.g. `ONLY_STUN_ATTACK`,
   `ONLY_RESISTDOWN_ATTACK`) omit `Target`/`TileOccupancy`/`TargetArea` entirely and silently inherit `ENEMY` /
   `FULL` / `SINGLE` from `DEFAULT_ABILITY`. Reading only the child block will make an ability look like it has
   no target config at all — always resolve through the `Inherits` chain (or diff against `DEFAULT_ABILITY`)
   before concluding a field is "unset" or "not needed."

FILE_WRITTEN: C:\Users\ben\repos\ftk2-mods-crucible\docs\research\coverage\ability-catalog.md
