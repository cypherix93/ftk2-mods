# Enum / vocabulary ground truth (decompile-verified)

Answers FTK2.Forge SPEC Open Question #5 and the EOR content-conversion validation needs.
Every enum-backed list below is copied **verbatim** from the C# decompile at
`D:\src\mods\ftk2-mods\tools\out\decompile\FTK2\<File>.cs` (file + line-anchored). Every
JSON-observed list states its source file and observation count. Where a prior doc
(`docs/research/data-schemas.md`, `docs/research/vocab-summary.md`, `tools/manifest_schema.md`)
disagrees, it's called out in **Corrections**, not silently overridden.

Decompile root: `tools\out\decompile\FTK2\`. Live-data cross-checks used
`tools\out\vocab-index.json` (extracted 2026-07-16 from the live Steam install's
`Configs\JSON~` tree via `tools/extract_vocab.py`; confirmed **not** EOR-contaminated for the
lists used here — see "vocab-index.json contamination check" below) and the mod package's
`Characters.json` at `D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN\...\JSON~\`
(2,126 entries = 2,095 vanilla + 31 `EOR_*`, vanilla-only stats used throughout). The game
install itself (`E:\Games\Steam\...`) was **not** read directly per instructions (mid-verify).

## vocab-index.json contamination check

`tools/extract_vocab.py` builds `Classes`/`Materials`/`Rarities`/`Slots`/`StatKeys` purely from
`Things\*.json` (item configs). `AllIds` additionally merges `Characters.json`/`Abilities.json`/
`StatusEffects.json`/`SkillConfigs.json` keys, which is where the 31 `EOR_*` ids live (verified:
`ItemIds` has 0 ids starting `EOR`; `AllIds` has 31, e.g. `EOR_ARCANIST`, `EOR_ASSASSIN`, …).
**Conclusion: the Classes/Materials/Rarities/Slots/StatKeys lists used in this report are clean
of EOR contamination.**

---

## 1. eTraits — all 17 values [enum-backed, but see finding below]

Source: `eTraits.cs` (verbatim, 17 members):

```
TRAIT_TACTICIAN, TRAIT_THINKER, TRAIT_GIFTED, TRAIT_NIMBLE, TRAIT_EAGER, TRAIT_NAVIGATOR,
TRAIT_LONGLEGS, TRAIT_FIELDMEDIC, TRAIT_PHOENIX, TRAIT_BERSERKER, TRAIT_BOSSKILLER,
TRAIT_CHOSENONE, TRAIT_GRILLMASTER, TRAIT_COURIER, TRAIT_VETERANDISCOUNT, TRAIT_CATNAP,
TRAIT_LONEWOLF
```

**Important finding — this enum does not gate anything and is stale vs. live data.**
`Grep`ping the whole decompile for qualified use (`eTraits.XXX`) returns **zero hits** outside
`eTraits.cs` itself. The actual runtime check for "is this Thing a trait" is a free-string
prefix test in `EquipmentHelper.cs:322`: `thing.ConfigName.StartsWith("TRAIT_")`. Live vanilla
data (`tools/out/vocab-index.json` → `ItemIds` filtered to `TRAIT_*` prefix) has **18** trait
items, not 17, and the two sets don't match:

- In the enum but **not observed live**: `TRAIT_LONGLEGS`
- Observed live but **not in the enum**: `TRAIT_LUCKY`, `TRAIT_SUPPORTIVE`

Practical conclusion for ClassForge/EOR validators: **do not gate trait ids against `eTraits`.**
The legal vocabulary is "any Thing id matching `TRAIT_*` with `Class:"TRAIT"`" (free string,
observed set below), because that's what the shipping code actually checks.

JSON-observed (source: `tools/out/vocab-index.json` → `ItemIds`, prefix `TRAIT_`, 18 ids):
```
TRAIT_BERSERKER, TRAIT_BOSSKILLER, TRAIT_CATNAP, TRAIT_CHOSENONE, TRAIT_COURIER, TRAIT_EAGER,
TRAIT_FIELDMEDIC, TRAIT_GIFTED, TRAIT_GRILLMASTER, TRAIT_LONEWOLF, TRAIT_LUCKY, TRAIT_NAVIGATOR,
TRAIT_NIMBLE, TRAIT_PHOENIX, TRAIT_SUPPORTIVE, TRAIT_TACTICIAN, TRAIT_THINKER,
TRAIT_VETERANDISCOUNT
```

---

## 2. eItemRarities — all 13 values [enum-backed]

Source: `eItemRarities.cs` (verbatim):

```
NONE = -1, COMMON, UNCOMMON, RARE, ARTIFACT, QUEST, LORE, SKIN, LOCKED, MERCENARY, CHARACTER,
LOCATION, MYSTERY, SEASONAL
```

JSON-observed on **items** (`tools/out/vocab-index.json` → `Rarities`, from `Things\*.json`, 8
values): `ARTIFACT, COMMON, LOCKED, LORE, NONE, QUEST, RARE, UNCOMMON`.

JSON-observed on **characters** (mod-package `Characters.json`, vanilla-only 2,095 entries, field
`Rarity`, 5 distinct values): `NONE` (1,125), `COMMON` (612), `UNCOMMON` (242), `RARE` (107),
`ARTIFACT` (9). Zero occurrences of `QUEST`/`LORE`/`LOCKED` on characters.

`SKIN`, `MERCENARY`, `CHARACTER`, `LOCATION`, `MYSTERY`, `SEASONAL` are legal enum members not
observed on either Things or Characters in the data sampled here — by name they most plausibly
gate cosmetics/follower-contract/location/mystery-box/seasonal-event content in configs not
present in the accessible input set (no `Skins.json`/`Locations.json` found — see **not_found**).

---

## 3. eEquipmentSlots — all 10 values [enum-backed]

Source: `eEquipmentSlots.cs` (verbatim):

```
NONE = -1, MAIN_HAND, OFF_HAND, HELMET, ARMOR, GLOVES, BOOTS, TRINKET, BACKPACK, PIPE
```

Field type confirmed at `Equippable.cs:5`: `public eEquipmentSlots[] Slots;` — a true enum array,
not free strings.

JSON-observed (`tools/out/vocab-index.json` → `Slots`, from `Things\*.json` `Equippable.Slots`,
8 values): `ARMOR, BOOTS, GLOVES, HELMET, MAIN_HAND, OFF_HAND, PIPE, TRINKET`. **`BACKPACK` is
legal but never observed on a live equippable item** — it likely exists only for the cosmetics
system (see §12, `eSkinCosmetics` also has a `BACKPACK` member) or is reserved/unused.

---

## 4. Material vocabulary [enum-backed: `eItemMaterialFamilies`]

`ThingConfig.Material` is `eItemMaterialFamilies` (confirmed at `ThingConfig.cs:17`), **not** a
free string. Source: `eItemMaterialFamilies.cs` (verbatim, 6 values):

```
NONE, WOOD, METAL, GLASS, CLOTH, LEATHER
```

(Note: there is a separate, unrelated `eMaterials` enum at `eMaterials.cs` with only `{WEP, ATT}`
— that's the weapon/attire split used by `Materials.json`'s `ATT_*`/`WEP_*` id-prefix convention
per `data-schemas.md` line 91, **not** the per-item material family. Don't confuse the two.)

JSON-observed on items (`tools/out/vocab-index.json` → `Materials`, from `Things\*.json`, 5
values): `CLOTH, LEATHER, METAL, NONE, WOOD`. **`GLASS` is legal (and used by `Materials.json`'s
`Type` field per `data-schemas.md` line 90) but never observed as a live equippable item's**
**`Material` value** — flag as enum-legal/unobserved-on-Things, not illegal.

---

## 5. ThingConfig.Class — free string, 63 observed vanilla values [free-string]

Source: `ThingConfig.cs:13`: `public string Class;` — confirmed free string, no backing enum.

JSON-observed (`tools/out/vocab-index.json` → `Classes`, from `Things\*.json`, 63 values,
matches prior doc's "63 live Class values" claim exactly — **confirmed, no correction needed**):

```
ALCOHOL, ARMOR_BODY, ARMOR_FEET, ARMOR_HANDS, ARMOR_HEAD, ARMOR_PIPE, ARMOR_TRINKET, AXE, AXE_2H,
BEAST_2H, BIRD_2H, BLADE, BLADE_2H, BLUNT, BLUNT_2H, BOMB, BOMB_2H, BOOK_2H, BOOMERANG,
BOOMERANG_2H, BOW_2H, CANDY, CANDYAPPLE, CANNON, CURRENCY, DAGGER, DEBUG, DEED, DOLL,
ETHEREAL_2H, FISH, FOOD, GUN, HANDBOW, HARPOON_2H, HERB, HOTDOG, INSECT_2H, KATANA, LANCE_2H,
LONGSTAFF, LUTE_2H, MISC, MONSTER_2H, ORB, ORB_2H, POLEARM_2H, QUEST, RAPIER, RIFLE, SCROLL,
SHIELD, SHOTGUN, SPEAR, STAFF, STAFF_2H, TAROT, TOOL, TRAIT, UNARMED, UNARMED_2H, WAND, WHIP
```

---

## 6. CHANGE_STAT stat tokens + Type tokens (resolves the WATER audit note) [enum-backed]

`CombatAbilityConfig.Actions` is `List<(eCombatActions, object)>` (`CombatAbilityConfig.cs:38`);
for `Item1 == eCombatActions.CHANGE_STAT`, `Item2` deserializes to `ChangeStatAction`
(`ChangeStatAction.cs`):

```csharp
public class ChangeStatAction {
    public string Stat;        // free string, validated against eCharacterStats member names
    public eDamageType Type;   // true enum
    public bool IsBlockable;
    public bool IsSilent;
    public int? FlatValue;
    public int? FlatPercent;
}
```

**`Stat`** is a free string in the struct, but in practice must match a member of
`eCharacterStats` (`eCharacterStats.cs`, 53 members, verbatim):

```
RND = -2, NONE, STR, VIT, INT, AWR, TAL, SPD, LCK, DEF, RES, EVD, PA, SA, MXFOC, FOC, MXHP, HP,
XP, ACC, ATK, MAG, PHY, PRW, CRT, CRTD, GLD, XPM, SKL, HRG, LOP, THRN, MOV, LBM, WBM, FIND, PSTR,
PVIT, PINT, PAWR, PTAL, PSPD, PLCK, PDEF, PRES, PEVD, PFOC, PGLD, DAM, PARTY_XP, CURRENT_FOCUS
```

**`Type`** is the enum `eDamageType` (`eDamageType.cs`, verbatim, 11 members incl. `NONE`):

```
NONE = -1, PHYSICAL, MAGICAL, FIRE, BLEED, POISON, RATTLED, STRENGTH, INFINITE_FIRE, CHAOS, REGEN
```

**WATER resolution (the audit's open question):** `WATER` is a member of `eStatusEffectTypes`
(§9 below — `StatusEffectConfig.Type`) but **is not, and cannot be**, a `CHANGE_STAT.Type` value
— `eDamageType` has no `WATER` member. The two enums are structurally distinct types used by
different config fields (`ChangeStatAction.Type` vs. `StatusEffectConfig.Type`); water damage in
combat must route through `ADD_STATUS` referencing a `WATER`-typed status effect, not through a
`CHANGE_STAT` action with `Type: "WATER"` (which would fail to deserialize). **Confirmed, audit
note was correct.**

---

## 7. eAbilityCategories — confirmed 14 values [enum-backed]

Source: `eAbilityCategories.cs` (verbatim, count confirmed = 14):

```
ATTACK, DEBUFF, SUPPORT_SELF, SUPPORT_ALLY, SUPPORT_GROUP, TAUNT, USE_ITEM, MOVE, MOVE_ENEMY,
SUMMON, FLEE, SKIP_TURN, REVIVE_ALLY, REPAIR_VEHICLE
```

---

## 8. eCombatActions — ALL action verbs (bounds what recipes can emit) [enum-backed]

Source: `eCombatActions.cs` (verbatim, 15 members):

```
MOVE, VOIDWALK, CHANGE_STAT, ADD_STATUS, REMOVE_STATUS, ADD_CHARACTER, ADD_CHARACTER_SMOKE,
ADD_CHARACTER_INSTANT, FLEE, REVIVE_ALLY, REVIVE_CHARACTER, EQUIP_WEAPON, GRAB, VEHICLE_DAMAGE,
VEHICLE_REPAIR
```

`Item2` payload shape per verb (from usage sites in `AdventureHelper.cs`, `CombatHelper.cs`,
`InteractableHelper.cs`, `AIHelper.cs`, `CombatViewHelper.cs`, `ItemCardViewHelper.cs`,
`SlotRollHelper.cs`):
- `CHANGE_STAT` → `ChangeStatAction` (§6)
- `ADD_STATUS`/`REMOVE_STATUS` → status id string
- `ADD_CHARACTER`/`ADD_CHARACTER_SMOKE`/`ADD_CHARACTER_INSTANT` → summon params
- `MOVE`, `VOIDWALK`, `FLEE`, `REVIVE_ALLY`, `REVIVE_CHARACTER`, `EQUIP_WEAPON`, `GRAB`,
  `VEHICLE_DAMAGE`, `VEHICLE_REPAIR` → verb-specific payloads (not the focus of this pass; see
  `data-schemas.md` lines 40-43 for the previously-recorded shapes, still consistent).

---

## 9. StatusEffectConfig.Type — all 57 legal values (58 incl. NONE) [enum-backed]

`StatusEffectConfig.Type` is `eStatusEffectTypes` (`StatusEffectConfig.cs:5`). Source:
`eStatusEffectTypes.cs` (verbatim, 58 members incl. `NONE`):

```
NONE = -1, AURA, ACID, BLEED, CONFUSE, CURSE, STUN, DAZE, ENTANGLE, INFINITE_FIRE, FIRE, ICE,
SHOCK, WATER, POISON, PETRIFY, BUFF, DEBUFF, IMMUNITY, PURIFY, REGEN, TAUNT, PROTECT,
INFINITE_PROTECT, DEATHSAVE, SCARE, REFLECT, DRAIN, GRAB, MOVE, STEAL, ROWDY, GUARD, INK,
DEATHMARK, SANCTUM, BARRIER, DECOY, IMBUE_FIRE, IMBUE_ICE, IMBUE_SHOCK, IMBUE_WATER, STATSUP,
DISTRACT, STAR_SHIELD, LINK, CONCENTRATION, PROWESS, RATTLED, VIGOR, HASTE, CHANNEL_MAGIC,
CHANNEL_PHYSIC, MARKED, CHARGE, BADWEATHER, POLLINATE, LONEWOLF
```

No accessible copy of live `StatusEffects.json` was available to compute an observed subset
(only vanilla `Characters.json` and `tools/out/vocab-index.json`'s status **id** list — not
per-status `Type` field values — were reachable; see **not_found**). The enum list above is
still verbatim-complete and authoritative per the acceptance criteria.

---

## 10. Tendency / TargetArea / Target on AbilityConfig [enum-backed]

`Abilities.json` deserializes to `CombatAbilityConfig` (confirmed at `ConfigsHelper.cs:154,455,638-650`
— `configs.Abilities = SerializedSortedDictionary<string, CombatAbilityConfig>`).

**`TargetArea`** is `eTileTargetAreas` (`CombatAbilityConfig.cs:8`). Source: `eTileTargetAreas.cs`
(verbatim, 15 members):
```
NONE, SELF, SINGLE, AOE, SPLASH, ROW, COLUMN, SWIPE, ALL_GROUP, ALL_TOTAL, SPLASH_WAVE, BEHIND,
CHECKERED, DEFAULT, EXPAND
```

**`Target`** is `eTargets` (`CombatAbilityConfig.cs:10`). Source: `eTargets.cs` (verbatim, 8
members):
```
ANY, SELF, SELF_PICK, ALLY, ALLY_ALL, ALLY_NEARBY, ALLY_NEARBY_ALL, ENEMY
```

**`Tendency`** is `eAiTendencies` (`CombatAbilityConfig.cs:40`). Source: `eAiTendencies.cs`
(verbatim, 24 members — this resolves `data-schemas.md`'s "25-value enum … NONE …" claim; the
enum in fact has 24 named members total including `NONE`, and here is the full list the prior
doc truncated with "…"):
```
NONE, HASSANCTUM, NOSANCTUM, MOSTARMOR, LEASTARMOR, MOSTRESISTANCE, LEASTRESISTANCE, MOSTEVASION,
LEASTEVASION, MOSTDAMAGE, LEASTDAMAGE, FASTEST, SLOWEST, MOSTHEALTH, LEASTHEALTH, MOSTGOLD,
MOSTFOCUS, ISBUFFED, FRONTROW, BACKROW, COLUMNSTACK, NOTHASPOISON, MUSTHAVEPOISON,
MUSTNOTHAVEHIVE, MUSTHAVELOWHEALTH
```
(Count check: 24 tokens listed above, i.e. `NONE` + 23 named tendencies = 24 total members. Prior
doc's "25-value" figure over-counts by one; the verbatim decompile list is authoritative.)

---

## 11. Follower schema enums: Type, ContractPrice, Behaviour [enum-backed]

`FollowerCharacterConfig.cs` (loaded from `Followers.json` per `ConfigsHelper.cs:254-260`:
`configs.Followers = SerializedSortedDictionary<string, FollowerCharacterConfig>`):

```csharp
public eCharacterTypes Type;
public eLootScales ContractPrice;
public eAiBehaviours Behaviour;
```

**`Type`** — `eCharacterTypes.cs` (verbatim, 8 members incl. `NONE`):
```
NONE = -1, STANDARD, PROP, FORCED_FIGHT, MERCENARY, CURSE, COMPANION, BOSS, INANIMATE
```

**`ContractPrice`** — `eLootScales.cs` (verbatim, 9 members incl. `NONE`):
```
NONE, TINY, VERY_LOW, LOW, AVERAGE, HIGH, VERY_HIGH, HUGE, MASSIVE
```

**`Behaviour`** — `eAiBehaviours.cs` (verbatim, 5 members):
```
DEFAULT, DPS, SUPPORT, TANK, CURSE
```

No vanilla `Followers.json` was reachable in the accessible input set (only `Characters.json` +
`ServerNames.json` ship in the mod package's `JSON~` folder — `Followers.json` isn't present
there; see **not_found**). As a **secondary, non-authoritative** cross-check, two third-party
BepInEx plugin data dumps structurally identical to `FollowerCharacterConfig`
(`FTK2_EnhancedMercenaries\Followers.json`, `FTK2_EnhancedPets\Followers.json` — **mod content,
not vanilla files**) were inspected for plausibility only:
- `Type` observed: `COMPANION` (119), `MERCENARY` (120), `CURSE` (2), `INANIMATE` (8) — the 4
  values expected for follower-type content (`STANDARD`/`PROP`/`FORCED_FIGHT`/`BOSS` are
  Characters.json-only types, not used for followers).
- `ContractPrice` observed: `AVERAGE` (95), `LOW` (7), `VERY_LOW` (40), `HIGH` (10), `TINY` (97).
- `Behaviour` observed: `DEFAULT` (249) only, in this sample.

Do not cite the third-party numbers as vanilla ground truth — they're included only because they
corroborate the enum shape and field names decompiled above.

---

## 12. eSkinCosmetics [enum-backed, low priority]

Source: `eSkinCosmetics.cs` (verbatim, 5 members):
```
NONE, CHARACTER, HELMET, ARMOR, BACKPACK
```
No JSON cross-check attempted (explicitly low priority per task; no `Skins.json` reachable in
the input set).

---

## Corrections to prior docs

1. **`docs/research/data-schemas.md` line 37-39 (CHANGE_STAT Type list is missing `CHAOS`).**
   Prior doc: `Type ∈ {PHYSICAL MAGICAL BLEED FIRE INFINITE_FIRE POISON RATTLED REGEN STRENGTH}`
   (9 values). Decompile `eDamageType` (§6) has 10 non-NONE members — `CHAOS` was omitted.

2. **`data-schemas.md` line 27-28 (Abilities.json `TargetArea` list is incomplete).** Prior doc:
   `SINGLE/AOE/ROW/COLUMN/CHECKERED/SPLASH/SPLASH_WAVE/SWIPE/ALL_GROUP/SELF/NONE` (11 values).
   Decompile `eTileTargetAreas` (§10) has 15 members — missing `ALL_TOTAL`, `BEHIND`, `DEFAULT`,
   `EXPAND`.

3. **`data-schemas.md` line 28 (`Target` list is incomplete).** Prior doc:
   `ENEMY/ALLY/ALLY_ALL/ALLY_NEARBY/ANY/SELF/SELF_PICK` (7 values). Decompile `eTargets` (§10)
   has 8 members — missing `ALLY_NEARBY_ALL`.

4. **`data-schemas.md` line 31-34 (`Tendency` claimed "25-value enum" but only lists ~21 with a
   trailing "…").** Decompile `eAiTendencies` (§10) verbatim list has 24 members total
   (`NONE` + 23 named tendencies). The previously-hidden members behind "…" are `HASSANCTUM`,
   `NOSANCTUM`, `LEASTDAMAGE`, `MUSTHAVELOWHEALTH`. Treat this report's list as authoritative;
   the "25" figure in the prior doc appears to be an off-by-one miscount.

5. **`data-schemas.md` line 56-63 (`StatusEffects.json` Types list undercounts, labeled "~40
   Types").** Decompile `eStatusEffectTypes` (§9) has 57 non-NONE members. Missing from the prior
   doc's list: `PETRIFY`, `STEAL`, `BARRIER`, `STATSUP`, `DISTRACT`, `CHANNEL_PHYSIC`,
   `BADWEATHER`.

6. **`data-schemas.md` line 50-54 (Behaviours.json "Shipped: DEFAULT, DPS, SUPPORT, CURSE" —
   missing `TANK`).** Decompile `eAiBehaviours` (§11) has 5 members; `TANK` was omitted from the
   "shipped" list.

7. **`eTraits` enum is dead/stale code, not a validation source (new finding, not a
   contradiction of a specific prior doc, but material to ClassForge SPEC Open Question #5).**
   See §1. Any validator currently planning to gate trait ids against the `eTraits` C# enum
   should instead gate against the free-string `TRAIT_*`-prefixed `Class:"TRAIT"` Thing-id space,
   because that's what `EquipmentHelper.cs` actually checks, and because the enum is missing two
   live trait ids (`TRAIT_LUCKY`, `TRAIT_SUPPORTIVE`) while including one non-existent one
   (`TRAIT_LONGLEGS`).

8. **`tools/manifest_schema.md` lines 15-17 and `vocab-summary.md`'s "63 Class values / Slots
   enum / Rarity enum" claims — confirmed, no correction.** `Classes` = 63 (§5, exact match).
   `Slots` (observed) = 8, matching `eEquipmentSlots` minus `NONE` and unobserved `BACKPACK`
   (§3 — `BACKPACK` is enum-legal but unobserved, worth a note, not a contradiction).
   `Rarity` ladder `COMMON → UNCOMMON → RARE → ARTIFACT` plus `NONE/QUEST/LORE/LOCKED` — confirmed
   as the **observed-on-items** subset; the full `eItemRarities` enum has 9 more legal values
   not used on Things (§2).

9. **`vocab-summary.md` §5 "Materials: CLOTH, LEATHER, METAL, WOOD, NONE" — confirmed as the
   observed-on-items subset, but incomplete as an enum statement.** The backing enum
   `eItemMaterialFamilies` has a 6th legal member, `GLASS` (§4), used by `Materials.json`'s
   `Type` field per `data-schemas.md` line 90 but never observed on a live equippable `Thing`.

---

## Not found / out of reach for this pass

- Live `StatusEffects.json`, `Abilities.json`, `Followers.json`, `Materials.json`,
  `ItemMaterials.json`, `Skins.json`/cosmetics config, `Behaviours.json` — none were present in
  the accessible mod-package `JSON~` folder (`Characters.json` + `ServerNames.json` only) or
  elsewhere under `D:\temp` as vanilla files. `tools/out/vocab-index.json` covers `Things\*.json`
  fully (source of Classes/Materials/Rarities/Slots/StatKeys) but not the other config families'
  per-record field values. The game's live Steam install was intentionally not read (mid-verify,
  per task instructions).
- `eSkinCosmetics` observed-usage cross-check (deprioritized per task).
- Rarity values `SKIN`/`MERCENARY`/`CHARACTER`/`LOCATION`/`MYSTERY`/`SEASONAL` — enum-legal but
  no reachable config file to confirm which content family actually emits them.
