# Vocabulary index summary (live game data, extracted 2026-07-16)

Produced by `python tools/extract_vocab.py` → `tools/out/vocab-index.json` (gitignored;
re-run after game updates). All facts below are **[verified: data]** — read directly from the
live `Configs\JSON~` tree. Where they contradict `docs/research/data-schemas.md`, **this file
wins** (the schemas doc described a subset).

## Counts

| Category | Count |
|---|---|
| Item ids (all `Things\*.json`) | 1,847 |
| Distinct tags | 488 |
| `SKILL_*` ids (SkillConfigs.json) | 68 |
| Status ids (StatusEffects.json) | 189 |
| Ability ids (Abilities.json) | 992 |
| Stat-curve cells (Class×Rarity with equippable stats) | 130 |

## Corrections to prior docs (important for all content design)

1. **Item `Class` has 63 observed values, not 23.** Weapons come in 1H/2H pairs (`AXE`/`AXE_2H`,
   `BLADE`/`BLADE_2H`, …) plus exotic classes (`LUTE_2H`, `BEAST_2H`, `ETHEREAL_2H`,
   `MONSTER_2H`, `LANCE_2H`, `HARPOON_2H`, …) and non-equipment classes (`FOOD`, `HERB`,
   `SCROLL`, `TAROT`, `TOOL`, `CURRENCY`, `DEED`, `QUEST`, `DEBUG`, …). Armor uses `ARMOR_BODY`,
   `ARMOR_HEAD`, `ARMOR_HANDS`, `ARMOR_FEET`, `ARMOR_TRINKET`, `ARMOR_PIPE` (EOR's README example
   `CHESTARMOR`/`HELMET` is EOR's own naming in *their* example text, not what live data uses).
2. **`Equippable.Slots` tokens (resolves FTK2.Forge SPEC Open Question #2):**
   `ARMOR, BOOTS, GLOVES, HELMET, MAIN_HAND, OFF_HAND, PIPE, TRINKET`.
3. **Rarity ladder is `COMMON → UNCOMMON → RARE → ARTIFACT`.** There is no EPIC/LEGENDARY.
   Special rarities also observed: `NONE`, `QUEST`, `LORE`, `LOCKED` (non-loot). Anywhere this
   repo's specs say "legendary", the game-data term is **ARTIFACT**.
4. **50 stat keys appear on equipment, not 20.** Beyond the documented 20:
   - `CRTD` — crit damage (distinct from `CRT`)
   - `DAM_ARMOR, DAM_BEAST, DAM_BOSS, DAM_CONSTRUCT, DAM_ETHEREAL, DAM_FIRE, DAM_FLYING,
     DAM_HUMAN, DAM_ICE, DAM_LIGHTNING, DAM_POISON, DAM_SCOURGE, DAM_UNDEAD, DAM_WATER` —
     damage bonuses vs enemy families/elements (e.g. trinkets with `DAM_UNDEAD: 25`)
   - `FIND, GLD, XPM` — loot-find / gold / XP economy stats
   - `MOV` — movement; `SKL`, `PHY`, `MAG` — roll/damage-school stats
   - `PAWR, PDEF, PEVD, PINT, PLCK, PRES, PSPD, PSTR, PTAL, PVIT` — P-prefixed family
     (party-wide aura variants — meaning to be confirmed in `game-mechanics.md`)
   These are *observed on live items*, so items built with them are schema-safe; their exact
   runtime semantics are tracked as claims in `game-mechanics.md`.
5. Materials: `CLOTH, LEATHER, METAL, WOOD, NONE`.

## Stat-curve examples (power grounding for itemforge)

- `BLADE|COMMON` (10 items): ATK 6–26 (p50 14.5), CRT 5–8, Value 5–227 (p50 40.5).
- `ARMOR_TRINKET|RARE` (21 items): CRT 5–15, DAM_UNDEAD 25, FIND 1, FOC 2, GLD ≤10 — trinkets
  are where the game already puts exotic economy/anti-type stats; our catalog leans into this.
- Curve cells per rarity: COMMON 35, UNCOMMON 28, RARE 24, ARTIFACT 8, NONE 34, QUEST 1 —
  ARTIFACT cells are sparse (few artifacts per class), so the forge's ARTIFACT budgets
  interpolate from RARE→ARTIFACT ratios on classes where both exist.

## Market/loot tag reality check

Top tags by frequency: `DROPPABLE` 1094, `DUNGEON_MARKET` 1010, `PHYSICAL` 872, `HAND_EQUIP` 798,
`WEAPON` 750, …, `TOWN_MARKET` 541, `NIGHT_MARKET` 381. Rarity is *also* mirrored as a tag
(`COMMON` 332, `RARE` 308, …) — catalog items must carry their rarity tag like live items do.

## Anomalies / watch-list

- `Rarity: "NONE"` covers 34 curve cells — many are consumables/quest items; the validator treats
  NONE-rarity equippables as WARN (unusual but legal).
- `ARMOR_PIPE` + `PIPE` slot is a real (small) equipment family — fun catalog target.
- Tag vocabulary is huge (488); many are faction/proc markers. The validator only requires that
  tags *exist in live vocab*, it does not gate on semantics.
