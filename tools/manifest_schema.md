# Item manifest contract (packs consumed by validate_pack / compile_pack / iconforge)

One pack = one JSON file: `{"PackId": "ARM_<NAME>", "Items": [ <entry>, ... ]}`.
Hand-crafted and forge-generated items are the same kind of thing — same schema, same
validation, same compilation. Provenance records which is which.

## Entry schema (annotated example — a real catalog item)

```jsonc
{
  // UPPERCASE_UNDERSCORE, ARM_ prefix, unique within pack AND vs every live game id.
  "Id": "ARM_BRAMBLELORD_CUIRASS",

  // Full ThingConfig exactly as the game's Things\*.json files use it.
  // Enum ground truth: Class is a free string (63 live values, see vocab-summary.md);
  // Slots from eEquipmentSlots (MAIN_HAND OFF_HAND HELMET ARMOR GLOVES BOOTS TRINKET PIPE);
  // Rarity from eItemRarities (COMMON UNCOMMON RARE ARTIFACT + special values).
  "Thing": {
    "ConsumableType": "NONE",
    "Value": 120,                    // market price basis
    "MinTier": 1, "MaxTier": 3,      // loot/market tier gating (like live items)
    "Class": "ARMOR_BODY",
    "Rarity": "RARE",
    "Material": "METAL",
    "Hidden": false,
    "Stacks": false,
    "Equippable": {
      "Slots": ["ARMOR"],            // never empty (EOR loader rejects)
      "Stats": {"DEF": 3, "THRN": 4, "VIT": 5},   // keys must exist on live equipment
      "Passives": ["SKILL_TAUNTINGBLOW"],          // must exist in live SkillConfigs/ids
      "MaxCharges": -1
    },
    // Optional; every ability referenced (keys, AbilityBag, AbilityFillBag) must exist
    // in the live Abilities.json — EOR's loader hard-skips the item otherwise.
    "Interactable": null,
    "Tags": ["DROPPABLE", "TOWN_MARKET", "ARMOR", "ARMOR_BODY", "RARE", "MEDIUM"],
    "Expansion": "BASE"
  },

  // REQUIRED. Live item id whose model/icon this item borrows. Items without a valid
  // fallback get quarantined by EOR (loses DROPPABLE/market tags). Pick from
  // vocab-index VisualDonors[Class] — nearest silhouette.
  "VisualFallback": "CHESTARMOR_MILITIA_HEAVY_01",

  // Compiled to localization keys <Id> and <Id>_DESCRIPTION.
  "Loc": {
    "Name": "Bramblelord Cuirass",
    "Description": "Every blow against its wearer is answered in kind; the brambles do not forgive."
  },

  // Optional icon recipe. Base = key in tools/out/sprite-index.json (PRERENDER_* sprites
  // are the game's own item card renders — ideal bases). Transform vocabulary:
  // HSV_SHIFT{H,S,V} PALETTE_MAP{From,To,Tolerance} GLOW{Color,Radius,Strength}
  // OVERLAY{Key,Anchor,Scale,Alpha} TINT_SILHOUETTE{Color,Alpha} COMPOSITE{Key,Anchor,Scale}
  // OUTLINE{Color,Width}. Rendered to <Id>.png (EOR ItemIcons exact-id convention).
  // NOTE: under EOR's current pipeline these PNGs display in-game for TRINKET-type items;
  // other classes show the VisualFallback donor's art (see eor-loader-notes.md §2).
  "Icon": {
    "Base": "PRERENDER_CHESTARMOR_MILITIA_HEAVY_01",
    "Transforms": [
      {"Op": "HSV_SHIFT", "H": 0.25, "S": 0.1, "V": -0.05},
      {"Op": "GLOW", "Color": "#3fae4a", "Radius": 5, "Strength": 0.8}
    ]
  },

  // Hand-crafted items: which verified game-mechanics claim(s) the design exploits.
  // Cite claim ids from docs/research/game-mechanics.md.
  "DesignNote": "Thorns tank anchor: C-STAT-11 (THRN procs even on BLOCKED melee hits, unreduced).",

  // Source: "handcrafted" | "forge". Forge entries carry Profile + Seed for reproducibility.
  "Provenance": {"Source": "handcrafted"}
}
```

## Validation rules (validate_pack.py)

ERROR-level: bad/duplicate id (vs pack + live game), unknown tag/stat-key/slot/class/rarity/
material/passive, unknown ability reference, empty `Equippable.Slots`, missing or unknown
`VisualFallback`, missing `Loc.Name`/`Description`, unknown `Icon.Base` (when a sprite index is
supplied), stat > observed-max × `fail_mult`.
WARN-level: stat above observed p50 × `warn_mult` and above observed max; `Rarity: "NONE"` on an
equippable. Thresholds in `tools/validator-config.json`. `--strict` promotes WARN to failing.

Tags beginning with `ARM_` are exempt from the unknown-tag check (our own namespace, e.g. a
future set-bonus plugin reads them).
