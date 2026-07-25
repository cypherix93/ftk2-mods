# Summoner M0 (followers/characters pack loader) + `tools/eor_import.py` — implementation design

**Date:** 2026-07-25 · **Charter:** `docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md` D5 + D6
(Wave 1 design → Wave 2 implementation) · **Status:** authoritative. Implementation agents build from this
document without further design authority. Every section ends in a `DECISION:` line; there are no open options.

**Grounding sources actually read for this design** (not cited from memory):

| Source | Used for |
|---|---|
| `FTK2.Summoner/SPEC.md` §3, §4.6, §9 | loader responsibilities, `Followers.json` shape, MP posture |
| `docs/research/eor-0760-content-audit.md` §2.2, §2.3, §6 | defect inventory, re-host verdicts |
| `docs/research/game-patch-surface-notes.md` §7, §9 | `ConfigsHelper.LoadConfigs`/`ReloadConfigs`, `GameRunData` |
| `docs/research/enum-ground-truth.md` §2, §5, §11 | `eItemRarities`, `Class` free-string 63-value list, `eCharacterTypes`/`eLootScales`/`eAiBehaviours` |
| `tools/manifest_schema.md`, `tools/validate_pack.py`, `tools/compile_pack.py` | output contract the converter must satisfy |
| `docs/research/build-template-notes.md` §3, §7 | csproj/`FtkRefsDir`/layout/logging/GUID conventions |
| `docs/MULTIPLAYER.md` R1–R5 | parity, determinism, host authority, SafeMode |
| `FTK2.ClassForge/SPEC.md` §3, §4.1, §4.2, §4.7 | pack-manifest shape reused verbatim by Summoner |
| Decompile `tools/out/decompile/FTK2/*.cs` | `FollowerCharacterConfig`, `CharacterConfig`, `ThingConfig`, `Interactable`, `SkillRollData`, `CharacterHelper.GetAbilityBag`, `InventoryHelper.GetSkillRollData`, `CombatPhase` juggle path |
| EOR package (read-only) `D:\temp\mods\Release 29 0.7.0.60 …\BepInEx\plugins\` | every field mapping and defect count below, measured not assumed |

---

## 0. Measurements taken from the real EOR package (supersede audit approximations where they differ)

All numbers below were computed directly against the package this session. Where they disagree with
`eor-0760-content-audit.md`, **this document is authoritative** and the delta is called out.

### 0.1 Followers

| Quantity | Measured | Audit said |
|---|---|---|
| `FTK2_EnhancedPets/Followers.json` keys | **149** | "106 new pets" |
| `FTK2_EnhancedMercenaries/Followers.json` keys | **249** | "100 + 106 + 22" |
| Pets file ⊂ mercs file | **yes, exactly** (`pets − mercs = ∅`) | implied |
| Union | **249** | 206 + 22 |
| `COMPANION_PLUS_*` (genuinely new pets) | **100** | 106 |
| `MERC_PLUS_*` (genuinely new mercs) | **100** | 100 |
| `SHEPHERD_SHEEP_*` with dangling `SHEEP_*_00` ConfigName | **8** (`SHEEP_GENERIC_00`, `_ICE_00`, `_FIRE_00`, `_SHOCK_00`, `_POISON_00`, `_WATER_00`, `_EVASIVE_00`, `_HEAL_00`) | 6 |
| Entries with **no `ConfigName` field at all** | **1** (`COMPANION_REFLECTION`) | not reported |
| Other vanilla-id entries EOR redefines (18 `COMPANION_*` + 20 `MERC_*` + `CURSE_HAG_01`, `CURSE_GHOST_01`, `JEREMY_KING_00`) | **41** (incl. `COMPANION_REFLECTION`) | "22 modified" |
| Of those, values that differ between the pets copy and the mercs copy | **21** (all `ContractRounds`→999, some `Rarity`/`ContractPrice` shifts) | "22 modified" |
| Followers with an `en.json` name key | **18 / 249** | 18/206 ✓ |
| Followers with an `_DESCRIPTION` key | **9 / 249** | — |
| `Behaviour` values observed | `DEFAULT` ×249 (only) | ✓ |
| `Subtitle`/`JoinParty`/`LeaveParty` keys present in EOR `en.json` | **none** — they are **vanilla** loc keys resolved by the game's own `Langs` | not reported |
| ConfigName resolution vs the package's 2,126-entry `Characters.json` | **240/249 resolve**; the 9 failures are the 8 sheep + `COMPANION_REFLECTION` | "100/106 resolve" |

Two real EOR follower entries, quoted verbatim (these are the field mappings the converter is written against):

```json
"COMPANION_PLUS_GHOST_GENERIC_00": {
  "Type": "COMPANION", "ConfigName": "GHOST_GENERIC_00",
  "ClassName": "COMPANION_PLUS_GHOST_GENERIC_00",
  "ContractRounds": 0, "MinTier": 0, "MaxTier": 3,
  "Rarity": "COMMON", "ContractPrice": "VERY_LOW", "Behaviour": "DEFAULT",
  "Subtitle": "CREATURE_SUBTITLE", "JoinParty": "CREATURE_JOIN_PARTY",
  "LeaveParty": "CREATURE_LEAVE_PARTY",
  "Tags": ["COMPANION", "PETSHOP"], "CaravanStatValue": 0,
  "AppendableStats": { "HP": 48, "MAG": 14, "RES": 8, "EVD": 18, "CRT": 5 },
  "Expansion": "BASE"
}
```

```json
"MERC_PLUS_BANDIT_HEAVY_00": {
  "Type": "MERCENARY", "ConfigName": "BANDIT_HEAVY_00",
  "ClassName": "MERC_PLUS_BANDIT_HEAVY_00",
  "ContractRounds": 999, "MinTier": 0, "MaxTier": 3,
  "Rarity": "COMMON", "ContractPrice": "TINY", "Behaviour": "DEFAULT",
  "Subtitle": "MERCENARY_SUBTITLE", "JoinParty": "MERC_HEAVY_JOIN_PARTY",
  "LeaveParty": "MERC_HEAVY_LEAVE_PARTY",
  "Tags": ["MERCENARY"], "CaravanStatValue": 0, "Expansion": "BASE"
}
```

```json
"SHEPHERD_SHEEP_ICE_00": {
  "Type": "INANIMATE", "ConfigName": "SHEEP_ICE_00",
  "ClassName": "SHEPHERD_SHEEP_ICE_00", "ContractRounds": 0,
  "MinTier": 0, "MaxTier": 3, "Rarity": "COMMON", "ContractPrice": "AVERAGE",
  "Behaviour": "DEFAULT", "Subtitle": "CREATURE_SUBTITLE",
  "JoinParty": "CREATURE_JOIN_PARTY", "LeaveParty": "SHEEP_LEAVE_PARTY",
  "Tags": ["INANIMATE"], "CaravanStatValue": 0, "Expansion": "BASE"
}
```

`FollowerCharacterConfig` (decompile, `tools/out/decompile/FTK2/FollowerCharacterConfig.cs`, whole file) has
exactly 18 fields: `Type, ConfigName, ClassName, ContractRounds, MinTier, MaxTier, Rarity, ContractPrice,
Behaviour, Subtitle, JoinParty, LeaveParty, Rescued, GiveDeed, Tags, CaravanStatID, CaravanStatValue,
AppendableStats, Expansion`. EOR uses all of them; `Rescued` appears on 14 entries, `GiveDeed` on 13,
`CaravanStatID` on 10, `AppendableStats` on 102. **`SPEC.md` §4.6's field list is correct.**

### 0.2 Items (421 across three files)

| Quantity | Measured |
|---|---|
| `EORR_CustomItems.json` / `StarterWeapons.json` / `Examples.json` | 386 / 31 / 4 |
| Items whose **`Class`** is not in the live 63-value vocabulary | **17** — `SWORD`×4, `LUTE`×3, `BOW`×3, `POLEARM`×3, `LANCE`×1, `BOOK`×1, `CHESTARMOR`×1, `HELMET`×1 (14 in StarterWeapons, 3 in Examples) |
| Unknown ability references (`Interactable.Abilities` keys **and** both bags, vs live `Abilities`) | **0** |
| Unknown `Passives` | **0** |
| Unknown `Slots` / `StatKeys` / `Rarity` / `Material` | **0** |
| Id collisions vs live `AllIds` | **0** |
| Unknown `Tags` occurrences | **1261**, all from 17 distinct tokens: `EOR_CUSTOM`(421), `EORR_CUSTOM`(386), `ENDGAME_GEAR`(163), `STRONGER_GEAR`(94), `WEAKER_GEAR`(93), `UTILITY_GEAR`(36), `EOR_STARTER_WEAPON`(31), bare stat tags `AWR/SPD/VIT/INT/STR/TAL/LCK`(31 total), `EOR_EXAMPLE`(4), `CHESTARMOR`(1), `HELMET`(1) |
| Items with **no** `VisualFallbacks.json` entry | **57** = 26 katanas + **all 31** StarterWeapons |
| Items with **no** `en.json` name key | **26** — all katanas |
| Items with neither loc nor fallback | **26** — the katanas exactly (audit §2.2 defect 1 ✓) |
| Items declaring `Interactable.Abilities` with empty/missing `Interactable.AbilityFillBag` | **262** (audit defect 2 ✓) |
| Items whose bag ids are absent from that item's own `Abilities` dict | **144** (audit defect 3 ✓) |
| Items carrying **top-level** `AbilityBag`/`AbilityFillBag`/`ShuffleAbilityBag` (not `ThingConfig` fields) | **246** |
| Of those 246, top-level `AbilityBag` ⊆ own `Abilities` keys | **246 / 246 — all of them** |
| Undeclared-bag mismatches **after** preferring the top-level bags | **0** |
| Items with `Interactable` but no bag in either position | **16** (all `SHIELD`) |

### 0.3 The single most important finding in this document

`ThingConfig` (decompile, whole file) has **no** top-level `AbilityBag`/`AbilityFillBag`/`ShuffleAbilityBag`
field — those live on `Interactable` only:

```csharp
public class Interactable {
    public Dictionary<string, SkillRollData> Abilities;
    public List<string> AbilityBag;
    public bool ShuffleAbilityBag;
    public List<string> AbilityFillBag;
}
```

EOR emits the bags **twice**: a stale copy inside `Interactable` and a corrected copy at the top level of the
`ThingConfig` object, where the game's deserializer silently discards it. Measured: the **top-level** copy is
self-consistent for all 246 items that have one (every id in it is a declared ability), while the
`Interactable` copy is the one that produces all 144 mismatches. Example — `EORR_NIGHT_SHADOW_BLADE` declares
`{KNIFE_BASIC_ATTACK, BLADE_SHADOW_AOE}`, its `Interactable.AbilityBag` references `KNIFE_BLEED_ATTACK`
(undeclared), and its top-level `AbilityBag` references `BLADE_SHADOW_AOE` (declared).

Why this matters and is not cosmetic — `InventoryHelper.GetSkillRollData` (`InventoryHelper.cs` L811, default
case at L836):

```csharp
default:
    if (pThing != null) { return GetInteractable(pThing.ConfigName).Abilities[pAbility]; }
```

A raw dictionary index. A bag entry that is not a key of that item's own `Abilities` dict throws
`KeyNotFoundException` the first time the ability is rolled. **Defect 3 is a crash bug**, and hoisting the
top-level bags into `Interactable` fixes defects 2 and 3 in a single rule (measured: 144 → 0 residual).

Supporting reads, so the implementation agent does not have to re-derive them:
- `CharacterHelper.GetAbilityBag` (`CharacterHelper.cs` L2762, local `fillAbilityList` L2818-2841): if
  `Interactable.AbilityBag` is null/empty the game falls back to `Abilities.Keys`. **An empty `AbilityBag` is
  therefore safe** — that is why the 16 bag-less shields need no repair.
- `Interactable.AbilityFillBag` is read at exactly one site, `CombatPhase.cs` L3958, inside the
  `JuggleState` branch — it only matters for *juggle weapons*. `InventoryHelper.IsJuggleWeapon`
  (`InventoryHelper.cs` L218) returns true only when `Abilities.Keys.First()` is itself a **Thing id**.
  Measured: **0 of the 421 EOR items are juggle-shaped**, so `AbilityFillBag` is functionally inert for this
  corpus. It is still populated by the hoist because it is free and correct.

### 0.4 Classes (31)

31 `EOR_*` entries appended to the package's `Characters.json` (2,126 total = 2,095 vanilla + 31). Each
carries exactly **11** fields: `Stats, Things, Passives, Rarity, Level, Threat, BaseType, DefaultBodyType,
Tags, Expansion, LocKey`. Vanilla player classes carry the same 11 **minus `LocKey`** — `LocKey` is the only
EOR addition and it is a genuine `CharacterConfig` field (decompile, `CharacterConfig.cs`).

- `Things` starting gear: **29 distinct ids, all live vanilla item ids, 0 unknown.** None of them is an
  `EOR_STARTER_*` weapon — the DLL applies starter kits at runtime (audit §2.1, code table L5762), so the
  class pack has **no** dependency on the Armory starter-weapons pack.
- `Passives`: **37 distinct `SKILL_*`, 0 unknown.**
- Stat envelope, measured (min/max/mean over 23 vanilla vs 31 EOR player classes):

| Stat | Vanilla min/max/mean | EOR min/max/mean | Verdict |
|---|---|---|---|
| LCK | 50 / 50 / 50.0 | 50 / **95** / 54.7 | power creep |
| CRT | 5 / 5 / 5.0 | 5 / **9** / 5.8 | power creep |
| FOC | 3 / 4 / 3.3 | 3 / **5** / 3.6 | power creep |
| SPD | 58 / 78 / 66.4 | 60 / **84** / 70.3 | creep |
| AWR | 48 / 78 / 68.0 | 58 / **84** / 69.3 | creep |
| TAL | 48 / 78 / 63.6 | 56 / **82** / 68.3 | creep |
| INT | 48 / 78 / 61.3 | 50 / **86** / 62.6 | creep |
| DEF | 1 / 2 / 1.3 | **0** / 3 / 0.9 | mixed |
| EVD | 2 / 8 / 4.0 | **0** / 6 / 2.4 | below vanilla |
| HP / PA / SA | 30-40 / 1 / 1 | 30-40 / 1 / 1 | parity |

**DECISION (§0):** these measured numbers, not the audit's approximations, are the converter's contract. The
converter asserts them at runtime (see §14 `assert_corpus_invariants`) so a different EOR build fails loudly
instead of silently emitting garbage.

---

# PART A — Summoner M0: the followers/characters pack loader

## A1. Project layout, GUID, and id policy

### A1.1 Layout

Copies the WarBrain template verbatim (`build-template-notes.md` §7), with the `FtkRefsDir` redirect from §3
so the build never needs the live game install.

```
FTK2.Summoner/
  SPEC.md                                  # exists
  README.md                                # NEW — build/install/knobs summary
  data/
    FollowerPacks/<PackId>/…                # NEW — see A2
    Characters/  Abilities/  Things/  …     # EXISTING SMN_PACK_STARTERS flat data; untouched by M0
  src/
    Summoner.Core/     Summoner.Core.csproj       # netstandard2.0, LangVersion 9.0, RootNamespace Summoner.Core
    Summoner.Plugin/   Summoner.Plugin.csproj     # net472, AssemblyName FTK2.Summoner, RootNamespace Summoner.Plugin
  tests/
    Summoner.Core.Tests/ Summoner.Core.Tests.csproj  # net8.0 xunit, references ONLY Summoner.Core
```

`Summoner.Core.csproj` is byte-for-byte the shape of `WarBrain.Core.csproj`: `netstandard2.0`,
`LangVersion 9.0`, `RootNamespace Summoner.Core`, **zero `<Reference>` and zero `<PackageReference>`**.
`Summoner.Plugin.csproj` declares references via `$(FtkRefsDir)` (default
`$(MSBuildThisFileDirectory)..\..\..\tools\bin\refs`, overridable with `-p:FtkRefsDir=…`), `Private=false` on
every one, per `build-template-notes.md` §3. Required refs: `BepInEx`, `0Harmony`, `FTK2`, `UnityEngine`,
`UnityEngine.CoreModule`, `System.Text.Json`, `SerializedSortedDictionary`.

**The plugin build is parked** (charter: reference assemblies were not yet snapshotted). Wave 2 must land
`Summoner.Core` + `Summoner.Core.Tests` building and passing green with no game DLLs, and land
`Summoner.Plugin` source complete but with its build known-failing on `MSB3245` until `tools/bin/refs/` is
populated. Do not stub game types to make the plugin compile.

**DECISION (A1.1):** `Summoner.Core` (netstandard2.0, zero external references) + `Summoner.Plugin` (net472,
thin Harmony adapter, build parked) + `Summoner.Core.Tests` (net8.0 xunit, Core-only). GUID constant
`public const string Guid = "ftk2mods.summoner";` used in both `[BepInPlugin]` and `new Harmony(Guid)`.

### A1.2 Id policy — re-prefix everything to `SMN_`

The alternative considered was preserving EOR's `COMPANION_PLUS_*` / `MERC_PLUS_*` ids on the grounds that it
keeps `VisualFallbacks` simple. **That argument does not apply to followers at all**: a `FollowerCharacterConfig`
has no visual field. Follower appearance is inherited entirely through `ConfigName` → the *unmodified vanilla*
`Characters.json` entry (audit §2.3, confirmed here — 240/249 `ConfigName`s resolve against vanilla). Renaming
the follower key changes nothing visual. The counter-argument is empty.

Against preservation, three things that are not empty:

1. **Silent collision with a co-installed EOR.** EOR's own loaders write the identical keys into
   `Configs.Followers`. Two mods writing the same key means the winner is decided by BepInEx plugin load order,
   which is not guaranteed identical across peers → two peers can end up with *different*
   `COMPANION_PLUS_GHOST_GENERIC_00` configs while both report a matching Summoner `dataHash`. That is a
   parity hole R1 cannot see.
2. **It makes the adds-only rule mechanically enforceable.** With a hard `^SMN_[A-Z0-9_]+$` id gate, a pack
   *cannot* express a vanilla override even by accident — the loader rejects the entry before merge. Preserving
   ids would mean 41 of the 249 entries are vanilla ids, i.e. the pack format would silently permit exactly the
   thing §A3 forbids.
3. **`dataHash` cleanliness.** The hash covers our authored bytes. Content whose id namespace is another
   mod's is content whose provenance is ambiguous in a mismatch report.

Mapping rules (pure, total, deterministic — the converter's `map_follower_id`):

| Input | Output | Count |
|---|---|---|
| `COMPANION_PLUS_<REST>` | `SMN_FOL_<REST>` | 100 |
| `MERC_PLUS_<REST>` | `SMN_MRC_<REST>` | 100 |
| anything else | *not emitted* (see A2.3 / B4) | 49 |

`ClassName` is rewritten in lockstep to the new id (EOR's own convention is `ClassName == key`; measured true
for all 249). `ConfigName`, `Subtitle`, `JoinParty`, `LeaveParty`, `Tags` are **never** rewritten — they point
at vanilla content.

**DECISION (A1.2):** re-prefix. `COMPANION_PLUS_*` → `SMN_FOL_*`, `MERC_PLUS_*` → `SMN_MRC_*`, `ClassName`
rewritten to match the new id, all vanilla-pointing fields preserved verbatim. The loader enforces
`^SMN_[A-Z0-9_]+$` on every key in `followers.json` and `characters.json` and rejects (ERROR, entry skipped,
pack continues) anything else.

## A2. Data folder contract

### A2.1 Directory shape

Deliberately identical in structure to `FTK2.ClassForge/data/ClassPacks/<PackName>/` (ClassForge SPEC §4.1,
§4.2, §4.7) so one manifest concept, one load-order algorithm, and one hashing rule serve both engines.

```
FTK2.Summoner/data/FollowerPacks/<PackId>/
  pack.json            REQUIRED  manifest, shape identical to ClassForge §4.1
  followers.json       REQUIRED  { "<SMN_ id>": <FollowerCharacterConfig>, … }
  characters.json      OPTIONAL  { "<SMN_ id>": <CharacterConfig>, … }
  localization/en.json OPTIONAL  flat { "<ID>": name, "<ID>_DESCRIPTION": desc }
  provenance.json      OPTIONAL  { "<SMN_ id>": {"Source":…, "EorId":…, "PackageVersion":…} }
```

`pack.json` fields, verbatim from ClassForge §4.1 (same parser shape, `SMN_PACK_` id convention):

```json
{ "id": "SMN_PACK_EOR_PETS", "name": "Enhanced Pets (re-hosted)", "version": "1.0.0",
  "author": "ftk2mods", "description": "...", "loadOrder": 100, "dependencies": [], "enabled": true }
```

`followers.json` and `characters.json` hold the **raw native config shape**, exactly as ClassForge's
`classes.json` does — no wrapper object, no per-entry metadata. That is why provenance is a sidecar (A2.2).

Why `characters.json` exists even though the EOR packs ship none: the *only* thing making EOR's 200 followers
portable is that they reuse vanilla `Characters.json` entries. Any future pack that ships an original creature
(Summoner's own `SMN_SALAMANDER_*`, a re-hosted mod with real new monsters) needs a place to put the
`CharacterConfig` in the same load/merge/hash pass, or the follower's `ConfigName` will dangle exactly the way
the sheep do. Declaring the slot now costs one `TryLoad` call and one merge branch; retrofitting it later
means a pack-format version bump.

### A2.2 Provenance placement

`FollowerCharacterConfig` has 18 fields and none of them is `Provenance`. The merged dictionary is consumed by
the game's own `System.Text.Json` deserializer; adding an unknown key relies on a serializer option we do not
control. Provenance therefore lives in `provenance.json`, keyed by the emitted id, and is **never merged into
`Configs`**. It **is** included in the `dataHash` (it is authored data, not localization).

**DECISION (A2.2):** provenance is a per-pack sidecar `provenance.json`, id → `{Source, EorId,
PackageVersion}`; loaded into `FollowerPack.Provenance` for logging/diagnostics; never merged into `Configs`;
included in `dataHash`.

### A2.3 Merge semantics — adds-only, no vanilla overrides in M0

EOR ships two override behaviours (audit §4): the pets loader is `allowUpdates:false` (adds only), the mercs
loader has a dedicated path that **does** rewrite vanilla followers (L25707). The measurable effect is the
21 entries where the two EOR copies disagree — all of them push `ContractRounds` to `999` and several shift
`Rarity`/`ContractPrice` (`MERC_CULTIST`: `Rarity UNCOMMON→COMMON`, `ContractRounds 6→999`, `ContractPrice
AVERAGE→HIGH`).

That is exactly the class of change that must not ship as an override:

- `ContractRounds` is consumed as a **countdown baked into saved follower state** (audit §2.3: EOR's own
  per-level bonuses are "baked into saved followers … so saves drift between SP and MP"). A peer without the
  pack resolves the vanilla `6` while a peer with it resolves `999`. Both peers keep simulating; the contract
  expires on one and not the other. There is no exception, no null reference, no log line — it is a **silent
  numeric divergence with no sync surface**, the worst failure shape under MULTIPLAYER.md R1.
- Adds-only produces the opposite, and much better, failure: a peer without the pack cannot resolve
  `SMN_MRC_BANDIT_HEAVY_00` **at all**. Loud, immediate, diagnosable — and pre-empted entirely by the `Block`
  parity policy (A5).
- There is also no ground truth available to implement overrides correctly right now: the vanilla
  `Followers.json` is not on disk (the game install is empty; the package's `JSON~` folder ships only
  `Characters.json` and `ServerNames.json`). We would be diffing one mod copy against another mod copy.

The content is not lost. §B4 emits nothing for the 41 vanilla-id entries into the pack, and writes them to a
`vanilla-overrides.json` sidecar with the measured pets-vs-mercs field deltas, so the M1 override path has its
input ready the day the vanilla file is readable.

**DECISION (A2.3):** M0 is **adds-only, hard-enforced**. `MergePlanner` emits an `Add` only when the id is
absent from the live `Configs.Followers`/`Configs.Characters` key set *and* matches `^SMN_[A-Z0-9_]+$`;
otherwise a `Skip` (id already present) or `Reject` (bad id shape), both logged at ERROR with the pack id and
the id. No `AllowVanillaOverrides` knob ships in M0 — an inert knob is worse than none. Vanilla overrides are
parked to Summoner M1 and must arrive with their own §9 MP analysis and a `dataHash`-visible
`enabledFeatures` entry.

## A3. Load / merge timing, idempotency, parity registration

### A3.1 Hooks

`ConfigsHelper` (game-patch-surface-notes §7) exposes exactly two entry points and **neither accepts extra
source directories** — `basePath` is a single root whose `Configs/JSON~` subtree is walked. A postfix merge
into the freshly-built `Configs` object is the only viable approach, and it is the one ClassForge already uses.

```csharp
// Summoner.Plugin/Patches/ConfigsMergePatches.cs
// Target 1: public static Configs LoadConfigs(string basePath)                  → Postfix, merge into __result
// Target 2: public static void ReloadConfigs(ref Configs configs, string basePath) → Postfix, merge into configs
```

Both are `public static`, both are resolvable with `AccessTools.Method(typeof(ConfigsHelper), "LoadConfigs")` /
`"ReloadConfigs"`. Per charter rule 1 each patch logs `Target found: ConfigsHelper.LoadConfigs` or
`Target NOT found: …` and, on not-found, leaves the feature off with vanilla untouched.

### A3.2 Idempotency

`LoadConfigs` and `ReloadConfigs` both call `CreateConfigs()` first and build a **brand-new `Configs`
instance** every time (game-patch-surface-notes §7, "Notable surprises" #5). The merge therefore always runs
against a fully-vanilla-populated, pack-free dictionary. Combined with adds-only semantics this gives
idempotency for free — but it must be idempotent *by construction*, not by luck:

- The in-memory `IReadOnlyList<FollowerPack>` is built **once** in `Awake()` and never mutated. The postfix
  recomputes a `MergePlan` from that immutable model plus the live key set, and applies it. Same inputs →
  same plan → same result.
- Disk is **not** re-read on `ReloadConfigs`. A hot-reload that changed pack bytes mid-session would change
  `dataHash` after the parity handshake already completed, which MULTIPLAYER.md R5 forbids without a re-run of
  the handshake. M0 does not ship a re-scan.
- `MergePlanner.Plan(packs, existingFollowerIds, existingCharacterIds)` is a **pure function** and its unit
  test asserts `Plan(p, ids) == Plan(p, ids)` and that applying a plan twice to the same sink is a no-op.

### A3.3 Order determinism (R2)

Discovery sorts directory names with `StringComparer.Ordinal` before anything else (never trusts filesystem
enumeration order), then topologically sorts by `(LoadOrder asc, Id ordinal asc)`; dependency cycles and
missing dependencies skip the pack with a loud log. Identical to ClassForge SPEC §3's rule, deliberately, so
both engines' `dataHash` is a pure function of *which packs are enabled*.

### A3.4 ParityService registration (R1)

Registered by reflection (no hard build dependency on DevKit) immediately after the first successful merge:

- `guid` = `ftk2mods.summoner`
- `version` = plugin assembly version
- `dataHash` = SHA-256 over every file under each **enabled** pack's `FollowerPacks/<PackId>/` tree,
  **excluding `localization/**`** (R1: localization is parity-exempt), computed over sorted pack ids then
  sorted ordinal relative paths, with `\r\n` → `\n` normalization and invariant culture. `provenance.json`
  **is** included.
- `enabledFeatures` = sorted list of enabled pack ids (a *pack*, not an individual id, is the parity unit).

`DataHasher` lives in `Summoner.Core` and takes `IReadOnlyList<(string PackId, IReadOnlyList<(string RelPath,
byte[] Bytes)> Files)>` — pure, no filesystem, fully unit-testable. `System.Security.Cryptography.SHA256` is
part of netstandard2.0 and is not a game dependency.

**DECISION (A3):** Postfix `ConfigsHelper.LoadConfigs` (merge into `__result`) and
`ConfigsHelper.ReloadConfigs` (merge into the `ref configs` parameter). Packs are read from disk once in
`Awake()`; the postfix only re-plans and re-applies from the immutable in-memory model. `MergePlanner.Plan` is
pure and idempotent. ParityService registration as specified above, `localization/**` excluded from the hash,
`provenance.json` included.

## A4. Core / Plugin split

**Rule: `Summoner.Core` contains zero game-DLL dependencies and zero JSON-library dependencies.** WarBrain.Core
sets the precedent (no external refs at all; its plugin does the deserialization). Summoner.Core needs to hold
the parsing *policy* but not the parser, so it takes both the filesystem and the JSON codec as interfaces.

### A4.1 `Summoner.Core` module list

| File | Contents |
|---|---|
| `Model/FollowerEntry.cs` | POCO mirroring `FollowerCharacterConfig`'s 18 fields, with **`string`** for `Type`/`Rarity`/`ContractPrice`/`Behaviour`/`Expansion` (enums stay game-side), `Dictionary<string,int> AppendableStats`, `List<string> Tags` |
| `Model/CharacterEntry.cs` | POCO mirroring `CharacterConfig`'s 15 fields (`Stats`, `Things`, `Passives`, `CampQuery`, `SwarmQuery`, `LootID`, `LocKey`, `Rarity`, `Level`, `Threat`, `BaseType`, `DefaultBodyType`, `Tags`, `OnDeathAbility`, `Expansion`) |
| `Model/ProvenanceEntry.cs` | `{ string Source; string EorId; string PackageVersion; }` |
| `Packs/PackManifest.cs` | `Id, Name, Version, Author, Description, LoadOrder, Dependencies[], Enabled` |
| `Packs/FollowerPack.cs` | `Manifest`, `Followers`, `Characters`, `Localization`, `Provenance`, `SourcePath`, `Files` (rel-path → bytes, for hashing) |
| `Packs/IPackFileSource.cs` | `IEnumerable<string> ListPackDirectories(string root); bool Exists(string rel); byte[] ReadAllBytes(string rel); IEnumerable<string> ListFilesRecursive(string packDir);` |
| `Packs/IJsonCodec.cs` | `T Deserialize<T>(string json);` — one method |
| `Packs/PackLoader.cs` | discovery → ordinal sort → parse via `IJsonCodec` → topo sort by `(LoadOrder, Id)` → returns `LoadResult { Packs[], Findings[] }`. Every per-file parse is individually try/caught: one bad file logs and disables **that pack only**, the rest still load (SPEC §8 item 10's requirement, resolved here in favour of per-pack granularity) |
| `Validation/Vocabulary.cs` | `static readonly HashSet<string>` for `CharacterTypes` (`NONE STANDARD PROP FORCED_FIGHT MERCENARY CURSE COMPANION BOSS INANIMATE`), `LootScales` (`NONE TINY VERY_LOW LOW AVERAGE HIGH VERY_HIGH HUGE MASSIVE`), `AiBehaviours` (`DEFAULT DPS SUPPORT TANK CURSE`), `ItemRarities` (13 values), `CharacterStats` (53 values) — all copied verbatim from `enum-ground-truth.md` §11, §2, §6, each with a comment naming the decompile file it came from |
| `Validation/PackValidator.cs` | pure. Checks: id shape `^SMN_[A-Z0-9_]+$`; duplicate id within/across packs; `Type`/`Rarity`/`ContractPrice`/`Behaviour` ∈ vocabulary; `AppendableStats` keys ∈ `CharacterStats`; **`ConfigName` non-empty and present in a supplied `ISet<string> knownCharacterIds`** (the pack's own `characters.json` ids ∪ the live `Configs.Characters` keys); `MinTier ≤ MaxTier`; loc key present for every emitted id. Returns `List<Finding>` |
| `Merge/MergePlan.cs` | `{ List<MergeAdd> FollowerAdds; List<MergeAdd> CharacterAdds; List<MergeSkip> Skips; List<Finding> Rejects; }` |
| `Merge/MergePlanner.cs` | pure `Plan(IReadOnlyList<FollowerPack>, ISet<string> existingFollowerIds, ISet<string> existingCharacterIds) → MergePlan`. Characters are planned **before** followers so a pack's own `characters.json` satisfies its own `ConfigName` references |
| `Parity/DataHasher.cs` | pure SHA-256 as specified in A3.4 |
| `Diagnostics/Finding.cs` | `{ Severity (Error/Warn/Info); PackId; EntityId; Check; Message; }` |
| `Diagnostics/ILog.cs` | `void Info(string); void Warn(string); void Error(string); void Debug(string);` |

### A4.2 `Summoner.Plugin` module list

| File | Contents |
|---|---|
| `SummonerPlugin.cs` | `[BepInPlugin(Guid, Name, Version)]`; binds knobs; builds the pack model once; installs patches via a `Patch(type, method, prefix, postfix)` helper that logs found/not-found; registers with ParityService; emits the startup summary line `"{Name} {Version} loaded. Enabled={…}, packs={…}, followers={n}, dataHash={…}"` |
| `Adapters/GameJsonCodec.cs` | `IJsonCodec` over the game's `System.Text.Json` |
| `Adapters/FileSystemPackSource.cs` | `IPackFileSource` over `System.IO`, rooted at the plugin folder + `[Packs] AdditionalRoots` |
| `Adapters/BepInExLog.cs` | `ILog` over `ManualLogSource` |
| `Adapters/ConfigsSink.cs` | applies a `MergePlan` to `Env.Configs.Followers` / `Env.Configs.Characters`; the **only** file that knows `SerializedSortedDictionary`, `FollowerCharacterConfig`, `CharacterConfig` exist; maps `FollowerEntry`/`CharacterEntry` → the game types, parsing the string enum fields with `Enum.TryParse` (a parse failure at this point is impossible — `PackValidator` already gated it — but is logged and skipped rather than thrown) |
| `Patches/ConfigsMergePatches.cs` | the two Harmony postfixes from A3.1, each wrapped in try/catch that logs and defers to vanilla |
| `Parity/ParityRegistration.cs` | reflection-based DevKit registration |

**DECISION (A4):** Core = POCOs, `PackLoader`, `PackValidator`, `MergePlanner`, `DataHasher`, `Vocabulary`,
`Finding` — all pure, all unit-testable with no game DLLs, JSON supplied through `IJsonCodec`, filesystem
through `IPackFileSource`. Plugin = BepInEx lifecycle, two Harmony postfixes, four adapters, parity
registration. No gameplay decision logic in the Plugin.

## A5. Knobs, SafeMode, parity posture

```
[General]
Enabled           (bool,   true)  — master switch; false ⇒ no scan, no patch, vanilla untouched
VerboseLogging    (bool,   false) — per-pack/per-entry merge decisions at LogLevel.Debug

[Packs]
AdditionalRoots   (string, "")    — comma-separated absolute dirs scanned in addition to <plugin>/data/FollowerPacks
<PackId>.Enabled  (bool,   true)  — one dynamically-bound entry per discovered pack

[Multiplayer]
OnParityMismatch  (enum,   Block) — WarnAndSafeMode | WarnOnly | Block
```

**SafeMode for M0 is a no-op, and that is the argument.** M0's entire deliverable is a load-time merge into
`Configs`. By the time a parity mismatch can be detected (session join), the merge has already happened on
every peer that has the pack, and cannot have happened on a peer that does not. There is no state-mutating
runtime feature to switch off. `WarnAndSafeMode`'s guarantee — "features that were already resolved keep
working" — degenerates to "nothing changes at all", i.e. the divergent peers keep playing with divergent
`Configs.Followers`. Compounding it: a follower is a *persistent roster character*; a save carrying
`SMN_MRC_BANDIT_HEAVY_00` loaded on a peer without the pack yields an unresolvable character reference, which
is SPEC §9.5's exact stated reasoning for `Block`.

**DECISION (A5):** **confirm `Block`.** Summoner's `[Multiplayer] OnParityMismatch` default is `Block`,
overriding the repo-wide `WarnAndSafeMode`, for M0 and for every later milestone. `SafeMode` for the M0 loader
is documented as "no effect — the merge is load-time; see §A5", and `WarnAndSafeMode` is explicitly *not*
recommended.

## A6. MP surface notes M0 must carry into its SPEC §9 delta

1. **Parity class `ALL_PEERS`.** Unchanged from SPEC §9.1; the loader merges into the `Configs` the game
   simulates from.
2. **No custom `_SYNC_` action.** M0 adds no runtime state. `SMN_SYNC_EVOLUTION_V1` belongs to M1.
3. **New risk to record — `AS_FOLLOWER` pool widening.** `CombatHelper.TryCreateSummon`
   (`CombatHelper.cs` L112, branch at L117-118) resolves `eSummonTypes.AS_FOLLOWER` by taking a
   **weighted random pick over characters carrying a tag**, then matching the result against
   `Env.Configs.Followers[*].ConfigName`. Our 200 added followers carry vanilla `Tags`
   (`COMPANION`/`MERCENARY`/`PETSHOP`) and vanilla `ConfigName`s. Any existing ability that summons
   `AS_FOLLOWER` therefore draws from a **larger** pool on a peer that has the pack — and, because that draw
   comes from the shared `CombatState.Random`, a peer with a different pack set does not merely get a different
   creature, it **desynchronises the RNG stream** for the rest of combat (audit §3.2's exact failure class).
   R1 + `Block` prevents the mixed-pack case; this must nonetheless be stated in the SPEC §9 delta as the
   reason `Block` is not negotiable, and re-checked if `WarnOnly` is ever offered.
4. **`GameRunData` untouched by M0** (`GameRunData.Stats` is `Dictionary<string,int>` per
   game-patch-surface-notes §9; M1 uses it, M0 does not).

---

# PART B — `tools/eor_import.py`

## B1. Shape, CLI, and general rules

Single module `tools/eor_import.py`, matching the flat style of `validate_pack.py` / `compile_pack.py` (which
tests import as `from validate_pack import validate_pack` after `sys.path.insert(0, tools/)`). Standard
library only plus whatever `tools/requirements.txt` already provides; no new dependency.

```
python tools/eor_import.py --source "<EOR pkg>/BepInEx/plugins" --repo-root . [--vocab tools/out/vocab-index.json] [--only items,followers,classes] [--report-dir tools/out/eor-import] [--package-version 0.7.0.60] [--dry-run]
```

- `--source` — the EOR `BepInEx/plugins` directory. **Read-only. The tool never writes under `--source`, never
  writes to any game directory** (`tools/README.md` rule).
- `--repo-root` — where outputs land (defaults to the repo root inferred from `__file__`).
- `--vocab` — `tools/out/vocab-index.json`; supplies `Classes`, `Tags`, `Skills`, `Slots`, `Rarities`,
  `Materials`, `StatKeys`, `Abilities`, `ItemIds`, `AllIds`, `VisualDonors`. Also the **id-collision vocabulary
  snapshot** (§B7).
- `--only` — comma-separated subset of `items,followers,classes`; default all three.
- `--dry-run` — compute and report, write nothing.
- Exit code `1` if any converter reports a `blocking` finding (an unmapped defect that would produce an
  invalid pack); `0` otherwise. Non-blocking policy applications are reported, not failed.

The `--source` tree also carries the package's `Characters.json` at
`<pkg>/For The King II_Data/StreamingAssets/Assets/Configs/JSON~/Characters.json`; the tool resolves it
relative to `--source/../..` and uses it as the **character-id set** for `ConfigName` resolution and as the
source of the 31 classes. If it is absent, the followers and classes converters abort with a blocking finding
(they cannot validate `ConfigName` without it).

**Determinism rules, applied everywhere:** all output dicts written with `sort_keys=True, indent=2,
ensure_ascii=False, newline="\n"` and a trailing `\n` (exactly `compile_pack._merge_json`'s convention); all
iteration over `sorted(...)`; no `set` iteration ever reaches output; no wall-clock, no randomness, no
`os.listdir` order. `--package-version` is an explicit argument, never derived from a file mtime.

**DECISION (B1):** single-module `tools/eor_import.py`; CLI as above; stdlib-only; read-only on `--source`;
byte-identical output for identical input by construction.

## B2. Module layout (sections in one file, in this order)

```python
# ── 1. constants & policy tables ────────────────────────────────────────────
PACKAGE_VERSION_DEFAULT = "0.7.0.60"
CLASS_REMAP        : dict[str, str]        # §B3.2
DONOR_CLASS_ALIAS  : dict[str, str]        # §B3.3
TAG_DROP           : frozenset[str]        # §B3.4
TAG_RENAME         : dict[str, str]        # §B3.4
ITEM_ID_RULES / FOLLOWER_ID_RULES / CLASS_ID_RULE
DESC_TEMPLATES     : dict[str, str]        # §B4.3

# ── 2. io helpers ───────────────────────────────────────────────────────────
def read_json(path)                       # utf-8-sig, matches compile_pack
def write_json(path, obj)                 # sort_keys/indent=2/LF/trailing newline
def load_vocab(path) -> dict
def load_sources(source_dir) -> EorSources # NamedTuple of every input document

# ── 3. shared pure helpers ──────────────────────────────────────────────────
def humanize(raw_id, *, strip_prefixes=(), drop_tokens=("GENERIC",)) -> str
def provenance(eor_id, package_version) -> dict
def make_report() / def record(report, kind, **fields)

# ── 4. items converter ──────────────────────────────────────────────────────
def map_item_id(eor_id) -> str | None
def hoist_ability_bags(thing) -> tuple[dict, list[str]]
def repair_class(thing, report) -> dict
def filter_tags(tags, report) -> list[str]
def pick_visual_fallback(item_id, thing, vf_map, vocab, report) -> str | None
def synth_item_loc(item_id, eor_id, en, report) -> dict
def convert_item(eor_id, thing, ctx) -> dict | None
def convert_items(ctx) -> dict[str, dict]     # PackId -> pack document

# ── 5. followers converter ──────────────────────────────────────────────────
def map_follower_id(eor_id) -> str | None
def classify_follower(eor_id, entry, char_ids) -> str  # emit | drop_dangling | drop_no_config | park_vanilla
def convert_follower(eor_id, entry, ctx) -> tuple[str, dict]
def synth_follower_loc(new_id, eor_id, entry, en) -> dict[str, str]
def convert_followers(ctx) -> dict[str, PackDoc]

# ── 6. classes converter ────────────────────────────────────────────────────
NATIVE_CHARACTER_FIELDS : tuple[str, ...]
def map_class_id(eor_id) -> str
def strip_to_native(cfg) -> dict
def convert_classes(ctx) -> PackDoc
def build_stats_report(vanilla, eor) -> dict     # sidecar, NO rebalancing

# ── 7. corpus invariants ────────────────────────────────────────────────────
def assert_corpus_invariants(sources, report)     # §B8

# ── 8. emit ─────────────────────────────────────────────────────────────────
def emit_item_packs / emit_follower_packs / emit_class_pack / emit_reports

# ── 9. cli ──────────────────────────────────────────────────────────────────
def build_context(args) -> Ctx
def main()
```

`Ctx` is a frozen dataclass carrying `vocab`, `en`, `vf_map`, `char_ids`, `package_version`, `report`. Every
`convert_*` is a pure function of `(input, ctx)` — no globals mutated except `ctx.report`, which is an
append-only list.

## B3. Converter 1 — items → Armory manifest packs

### B3.1 Output shape and the contract it must satisfy

Two packs written to `FTK2.Armory/packs/`:

| File | `PackId` | Items |
|---|---|---|
| `eor_items.pack.json` | `ARM_EOR_ITEMS` | 386 (from `EORR_CustomItems.json`) |
| `eor_starters.pack.json` | `ARM_EOR_STARTERS` | 31 (from `StarterWeapons.json`) |

`Examples.json` (4 items, `EOR_EXAMPLE_*`) is **not** converted — they are the loader's own documentation
samples, they carry the `EOR_EXAMPLE` tag, and 3 of the 17 illegal-`Class` items are among them.

Pack document shape is exactly what `compile_pack.compile_pack` reads: `{"PackId": …, "Items": [entry, …]}`
(`compile_pack.py` L52-53 reads `pack["PackId"]` and `pack.get("Items", [])`). Each entry carries
`Id`, `Thing`, `VisualFallback`, `Loc{Name,Description}`, and `Provenance` — the exact keys
`compile_pack` consumes at L54 (`e["Thing"]`), L62 (`e["VisualFallback"]`), L66-67 (`e["Loc"]["Name"]`,
`e["Loc"]["Description"]`), and the exact keys `validate_pack.validate_pack` checks at L71 (prefix), L77
(`Thing`), L143 (`VisualFallback`), L150 (`Loc`), L154 (`Icon`, optional — we emit none).

`Provenance` is not read by either tool (`manifest_schema.md` documents it as `{"Source": "handcrafted"}` /
forge; the validator never touches it), so adding `Source: "eor-import"` is schema-compatible.

The `ARM_` id prefix is **not a choice** — `validate_pack.py` L71-72 hard-errors on anything else:

```python
if not item_id.startswith(ID_PREFIX) or item_id != item_id.upper():
    err("bad_prefix", f"id must be UPPERCASE and start with {ID_PREFIX}")
```

Id rules: `EORR_<REST>` → `ARM_EOR_<REST>`; `EOR_STARTER_<REST>` → `ARM_EOR_STARTER_<REST>`;
`EOR_EXAMPLE_*` → `None` (skip). No other input shapes exist in the corpus (asserted, §B8).

### B3.2 Defect policy — illegal `Class` on 14 starter weapons

`ThingConfig.Class` is `public string Class;` — a free string (enum-ground-truth §5), and the 63 live values
are the validator's `classes` set. All 14 offenders are `Slots: ["MAIN_HAND"]` + tag `WEAPON_ONE_HAND`, so the
remap targets are chosen for **one-handed** legal classes wherever one exists:

| EOR `Class` | → | Rationale (each target verified present in the 63-value live list) |
|---|---|---|
| `SWORD` (×4) | `BLADE` | the game's 1H sword class; the 4 items are a sabre, a hexsaber, a shieldblade, a knife |
| `POLEARM` (×3) | `SPEAR` | 1H polearm; `POLEARM_2H` is two-handed. Two of the three are literally named "spear" |
| `LANCE` (×1) | `SPEAR` | same; the halberd's abilities are `POLEARM_BASIC_ATTACK` + `LANCE_TAUNT_ATTACK` |
| `BOW` (×2) | `HANDBOW` | the 1H bow class; `BOW_2H` is two-handed |
| `BOOK` (×1) | `STAFF` | the item is `EOR_STARTER_RUNEMAGE_CHALK_RUNE_STAFF`, abilities `MAGIC_BASIC_ATTACK` + `MAGIC_FOCUS_ADD_ATTACK_00` |
| `LUTE` (×3) | `LUTE_2H` | **no 1H lute class exists.** Abilities are `MUSIC_*`, so `LUTE_2H` is the semantically correct family. `Equippable.Slots` and the `WEAPON_ONE_HAND` tag are left untouched — hand count comes from `Slots`/tags, not from the `Class` string |
| `CHESTARMOR`, `HELMET` | — | only on `EOR_EXAMPLE_*`, which are not converted |

Every remap is written to the report as a `class_remap` row (`{id, from, to}`) so a human can review the
LUTE case, which is the only one where the target family's hand-count convention disagrees with the item.

**DECISION (B3.2):** apply `CLASS_REMAP = {SWORD→BLADE, POLEARM→SPEAR, LANCE→SPEAR, BOW→HANDBOW,
BOOK→STAFF, LUTE→LUTE_2H}`; a test asserts every value in the table is in `vocab["Classes"]`. Any `Class`
that is neither legal nor in the table is a **blocking** finding (exit 1) — the tool must never silently emit
an item the validator will reject.

### B3.3 Defect policy — the 26 katanas (no loc, no VisualFallback) and the 31 starters (no VisualFallback)

**Include all 57, do not exclude.** Excluding would drop the katanas (a coherent 26-item weapon line) and every
starter weapon, for a defect that is fully repairable from data we already have.

- **VisualFallback** — `vocab["VisualDonors"]` is a `Class → sorted list of live item ids` map covering 46
  classes. Selection is `sorted(vocab["VisualDonors"][cls])[0]` on the item's **post-remap** `Class`. Verified
  targets: `KATANA → KATANA_MILITIA_MEDIUM_00` (6 donors available), `BLADE → BLADE_CULTIST_MEDIUM_00` (60),
  `SPEAR → POLEARM_BEASTMAN_LIGHT_00` (22), `HANDBOW → HANDBOW_FORTUNETELLER_BASIC_00` (9),
  `STAFF → STAFF_HAG_CURSE_00` (14), `LUTE_2H → LUTE_BONE_HEAVY_00` (56).
  Fallback chain when a legal `Class` has no donor list: `Class` → `DONOR_CLASS_ALIAS[Class]` → `Class + "_2H"`
  → `Class` with a `_2H` suffix removed → **blocking finding**. `DONOR_CLASS_ALIAS = {"ORB": "ORB_2H"}` is the
  only entry needed today: `EOR_STARTER_ORACLE_CLOUDED_EYE_SLING` is `Class: ORB`, which is legal but has zero
  donors, while `ORB_2H` has 20.
  Items that already have an `EnhancedOverhaulRevamped/CustomItems/VisualFallbacks.json` entry keep it
  verbatim, after checking it is in `vocab["ItemIds"]` (measured: 0 failures across all 364 entries).
- **Loc** — the 26 katanas get `Name = humanize(eor_id, strip_prefixes=("EORR_",))`, e.g.
  `EORR_RIVET_SAMURAI_S_KATANA` → `"Rivet Samurai S Katana"`. That reads badly for possessive `_S_`, so the
  humanizer applies one extra rule: a standalone `S` token immediately following another token collapses into
  `'s` on the previous word (`Rivet Samurai's Katana`). `Description` is generated from a per-`Class` template
  table, e.g. `KATANA` → `"A curved blade of foreign make, kept keen by long habit."`; the table is a fixed
  constant, deterministic, and one entry per `Class` present in the corpus with a generic fallback
  `"A {name}, of no recorded provenance."`. Every synthesized loc is reported as a `loc_synth` row.

**DECISION (B3.3):** include all 57. VisualFallback = existing EOR entry if valid, else deterministic
class-donor pick with the alias/suffix fallback chain; no donor at all is blocking. Loc = EOR's `en.json`
entry if present, else `humanize()` name + per-`Class` template description. Nothing is excluded for lacking
loc or a fallback.

### B3.4 Defect policy — `AbilityFillBag` and undeclared bag ids (one rule, both defects)

`hoist_ability_bags(thing)`:

1. Pop the non-schema top-level `AbilityBag`, `AbilityFillBag`, `ShuffleAbilityBag` off the `ThingConfig`
   object (they are not `ThingConfig` fields; the game discards them anyway).
2. If `Interactable` is `None`, discard them and return.
3. Otherwise, for each of `AbilityBag` / `AbilityFillBag` / `ShuffleAbilityBag`: **if the popped top-level
   value is non-empty, it wins** and is written into `Interactable`; else the existing `Interactable` value is
   kept.
4. Recompute `undeclared = set(Interactable.AbilityBag) | set(Interactable.AbilityFillBag) −
   set(Interactable.Abilities)`. Measured across the whole corpus: **empty**. If non-empty (a different EOR
   build), drop the undeclared ids from the bags, preserving order and multiplicity of the survivors, and emit
   a `bag_undeclared_dropped` report row per id.
5. If neither position has a bag (the 16 shields), leave `Interactable.AbilityBag`/`AbilityFillBag` **absent**.
   `CharacterHelper.GetAbilityBag`'s `fillAbilityList` explicitly falls back to `Abilities.Keys` when
   `AbilityBag` is null-or-empty, so this is the game's own intended default and needs no synthesis.

This is the "copy `AbilityBag` into `AbilityFillBag` per starter-weapon precedent" option, but strictly better:
the precedent is *already in the file* as the top-level copy, and using it yields a self-consistent bag for
all 246 items instead of guessing. The 33 items that already had a correct `Interactable.AbilityFillBag`
(starters + examples) are untouched by rule 3 because their top-level copy is absent or identical.

**DECISION (B3.4):** hoist top-level bags into `Interactable`, top-level wins on conflict, drop the
non-schema top-level keys from the emitted `Thing`; residual undeclared bag ids are dropped and reported;
bag-less items keep no bag and rely on the game's `Abilities.Keys` fallback. This resolves both the
`AbilityFillBag` defect (262 items) and the bag-not-declared defect (144 items).

### B3.5 Tag policy

`validate_pack.py` L88-89 exempts tags beginning with `ARM_`; every other tag must be in `vocab["Tags"]`.

- `TAG_DROP = {EOR_CUSTOM, EORR_CUSTOM, EOR_EXAMPLE, CHESTARMOR, HELMET, AWR, SPD, VIT, INT, STR, TAL, LCK}` —
  EOR's own namespace markers plus the bare-stat tags on starter weapons (no vanilla consumer; they duplicate
  `Equippable.Stats`) plus two stray class-name tags on the un-converted examples.
- `TAG_RENAME = {ENDGAME_GEAR: ARM_ENDGAME_GEAR, STRONGER_GEAR: ARM_STRONGER_GEAR, WEAKER_GEAR:
  ARM_WEAKER_GEAR, UTILITY_GEAR: ARM_UTILITY_GEAR, EOR_STARTER_WEAPON: ARM_STARTER_WEAPON}` — these carry real
  tier/role intent worth preserving, and the `ARM_` namespace is exactly what `manifest_schema.md` reserves
  for "our own namespace, e.g. a future set-bonus plugin reads them".
- Any other unknown tag is dropped with a `tag_dropped` report row. Order is preserved; duplicates removed
  keeping first occurrence.

**DECISION (B3.5):** drop `TAG_DROP`, rename `TAG_RENAME` into the `ARM_` namespace, drop-and-report anything
else unknown, preserve all known vanilla tags verbatim in original order.

### B3.6 Everything else passes through untouched

`ConsumableType`, `Value`, `MinTier`, `MaxTier`, `Rarity`, `Material`, `Hidden`, `Stacks`, `Ammo`,
`Equippable` (`Slots`, `Stats`, `Passives`, `MaxCharges`), `Interactable.Abilities` (the `SkillRollData`
dicts), `Expansion` — all verbatim. Measured: 0 unknown abilities, passives, slots, stat keys, rarities or
materials across all 421 items, so no repair is needed and none is invented. **The converter never edits stats
or values.** Power-budget findings are the validator's job (`validate_pack.py` L112-129) and any WARN it
produces is a human balance decision, not a converter one.

## B4. Converter 2 — followers → Summoner packs

### B4.1 Classification (total, deterministic)

For each of the 249 union entries (pets file merged under the mercs file so the mercs copy wins where they
differ — 21 entries; the mercs copy is EOR's own later, permanent-contract-corrected version):

| Class | Rule | Count | Outcome |
|---|---|---|---|
| `emit` | id starts `COMPANION_PLUS_` or `MERC_PLUS_` **and** `ConfigName` resolves | **200** | into a pack |
| `drop_dangling` | `ConfigName` present but not in the character-id set | **8** (all `SHEPHERD_SHEEP_*`) | `dropped.json` |
| `drop_no_config` | no `ConfigName` field at all | **1** (`COMPANION_REFLECTION`) | `dropped.json` |
| `park_vanilla` | id has neither `*_PLUS_` prefix (i.e. it is a vanilla follower id EOR redefines) | **40** | `vanilla-overrides.json` |

(`COMPANION_REFLECTION` is itself a vanilla id, so it is counted once, under `drop_no_config`; 200 + 8 + 1 + 40
= 249. The 41-entry vanilla-id group breaks down as 18 `COMPANION_*` — one of which is `COMPANION_REFLECTION` —
20 `MERC_*`, and 3 unprefixed: `CURSE_HAG_01`, `CURSE_GHOST_01` (`Type: CURSE`) and `JEREMY_KING_00`.
Note that `Type: CURSE` is also outside the M0 `PackValidator`'s `COMPANION`/`MERCENARY` allow-list, so those
two are doubly excluded.)

### B4.2 The sheep decision

Drop them; do **not** remap to a "nearest vanilla sheep-alike".

- The referenced configs — `SHEEP_GENERIC_00`, `SHEEP_ICE_00`, `SHEEP_FIRE_00`, `SHEEP_SHOCK_00`,
  `SHEEP_POISON_00`, `SHEEP_WATER_00`, `SHEEP_EVASIVE_00`, `SHEEP_HEAL_00` — exist **nowhere** in the
  package's full 2,126-entry `Characters.json`. There is no sheep to point at; a "sheep-alike" substitution
  would be inventing a creature EOR never shipped and attributing it to the import.
- All 8 are `Type: "INANIMATE"` with `Tags: ["INANIMATE"]` — props tied to EOR's DLL-side shepherd mechanic,
  which is not being ported (audit §6 row 9 drops the specialist/system layer). They are not recruitable
  content whose absence a player would notice.
- They were already dead in EOR: an unresolvable `ConfigName` is the follower equivalent of the quarantined
  katanas.
- `Type: INANIMATE` would also fail the M0 `PackValidator` restriction to `COMPANION`/`MERCENARY` (A4.1).

Same reasoning covers `COMPANION_REFLECTION`, which has no `ConfigName` field whatsoever.

**DECISION (B4.2):** drop all 8 `SHEPHERD_SHEEP_*` and `COMPANION_REFLECTION`; write all 9 to
`tools/out/eor-import/dropped.json` with `{eor_id, reason, config_name}` so the exclusion is auditable, per
charter acceptance criterion 6 ("dangling refs fixed or excluded with note").

### B4.3 Field mapping, verified against the §0.1 quotes

| EOR field | Emitted | Transform |
|---|---|---|
| *(key)* | *(key)* | `COMPANION_PLUS_X` → `SMN_FOL_X`; `MERC_PLUS_X` → `SMN_MRC_X` |
| `Type` | `Type` | verbatim; must be `COMPANION` or `MERCENARY` |
| `ConfigName` | `ConfigName` | **verbatim** — points at vanilla `Characters.json`; this is what carries stats/abilities/visuals |
| `ClassName` | `ClassName` | rewritten to the new key (EOR's invariant `ClassName == key`, measured true 249/249) |
| `ContractRounds` | `ContractRounds` | verbatim (`999` on mercs = EOR's permanent-contract intent, now data instead of DLL patch L25838) |
| `MinTier` / `MaxTier` | same | verbatim int |
| `Rarity` | `Rarity` | verbatim; ∈ `eItemRarities` |
| `ContractPrice` | `ContractPrice` | verbatim; ∈ `eLootScales` |
| `Behaviour` | `Behaviour` | verbatim; ∈ `eAiBehaviours` (all `DEFAULT` in this corpus) |
| `Subtitle`, `JoinParty`, `LeaveParty` | same | **verbatim** — these are *vanilla* loc keys (measured: none of them appears in EOR's `en.json`), resolved by the game's own `Langs`. Do not rewrite, do not synthesize |
| `Tags` | `Tags` | verbatim (`COMPANION`, `MERCENARY`, `PETSHOP` — all vanilla) |
| `AppendableStats` | `AppendableStats` | verbatim; keys validated ∈ `eCharacterStats` |
| `CaravanStatValue`, `CaravanStatID`, `Rescued`, `GiveDeed` | same | verbatim when present |
| `Expansion` | `Expansion` | verbatim |
| — | `provenance.json` sidecar | `{Source:"eor-import", EorId:<original key>, PackageVersion:"0.7.0.60"}` |

**Not ported (audit §2.3 DLL-only):** permanent-contract enforcement beyond the `ContractRounds` number,
per-level stat bonuses written into saved followers (`ApplyMercenaryLevelBonuses` L25926 —
offline-only, save-drifting, an R1/R3 violation), kibble feeding, training manuals, shop dedup. The
`AppendableStats` block that EOR ships on 102 entries **is** carried across as data; SPEC §11.8 flags its
consumption path as unconfirmed, so a `WARN` report row records that these values may be inert until that
question is resolved.

### B4.4 Localization synthesis

EOR provides names for 18/249 and descriptions for 9/249, and every one of those 9 descriptions is the branded
template `"A custom {X} companion added by Enhanced Overhaul."`.

- **Name:** if `en.json` has the original key, copy it **verbatim** under the new id. Otherwise
  `humanize(eor_id, strip_prefixes=("COMPANION_PLUS_", "MERC_PLUS_"), drop_tokens=("GENERIC",))` with the
  trailing `_\d+` index stripped. This humanizer is **validated against EOR's own output**:
  `COMPANION_PLUS_PIXIE_GENERIC_00` → `"Pixie"` (EOR: `"Pixie"` ✓) and
  `COMPANION_PLUS_SKRAEVIN_CHAOS_MELEE_00` → `"Skraevin Chaos Melee"` (EOR: `"Skraevin Chaos Melee"` ✓).
- **Description:** always synthesized, never copied — the 9 that exist carry EOR branding we do not ship.
  `COMPANION` → `"A {name} that can be coaxed into travelling with your party."`;
  `MERCENARY` → `"A {name} willing to fight alongside you, for coin."`

**DECISION (B4.4):** copy names when EOR has them, humanize otherwise (humanizer validated against two real
EOR strings); always synthesize descriptions from the two `Type`-keyed templates; never touch
`Subtitle`/`JoinParty`/`LeaveParty`, which are vanilla keys.

### B4.5 Output

```
FTK2.Summoner/data/FollowerPacks/SMN_PACK_EOR_PETS/   pack.json  followers.json(100)  localization/en.json  provenance.json
FTK2.Summoner/data/FollowerPacks/SMN_PACK_EOR_MERCS/  pack.json  followers.json(100)  localization/en.json  provenance.json
tools/out/eor-import/dropped.json                     9 entries
tools/out/eor-import/vanilla-overrides.json           40 entries + pets-vs-mercs field deltas
```

`loadOrder` 100 for pets, 110 for mercs (deterministic, no dependency between them). Neither pack ships a
`characters.json` — every `ConfigName` is vanilla, which is the whole reason these 200 are portable.

## B5. Converter 3 — classes → ClassForge pack

Output: `FTK2.ClassForge/data/ClassPacks/CF_PACK_EOR_CLASSES/` with `pack.json`, `classes.json` (31),
`localization/en.json`, and the sidecar `tools/out/eor-import/eor-class-stats-report.md`.

- **Id:** `EOR_<REST>` → `CF_EOR_<REST>` (e.g. `EOR_ARCANIST` → `CF_EOR_ARCANIST`). Same collision reasoning
  as A1.2: a co-installed EOR *overwrites the game's own `Characters.json`* with entries under the exact
  `EOR_*` keys (audit §1), so keeping them guarantees a silent, load-order-dependent conflict.
- **`LocKey`:** rewritten to the new id. This is the field the task flags; it is a genuine `CharacterConfig`
  member (decompile) that vanilla player classes omit and all 31 EOR classes set to their own id.
- **Field stripping:** emit only `NATIVE_CHARACTER_FIELDS = (Stats, Things, Passives, CampQuery, SwarmQuery,
  LootID, LocKey, Rarity, Level, Threat, BaseType, DefaultBodyType, Tags, OnDeathAbility, Expansion)`,
  preserving only those actually present on the source entry. Measured, all 31 carry exactly 11 of these and
  nothing else, so the strip is a no-op safety net rather than a repair — but it must exist so a different EOR
  build cannot smuggle a foreign field into `Configs.Characters`.
- **Tags:** verbatim (`PLAYER` plus role tags), with `CF_PACK_EOR_CLASSES` appended per ClassForge §4.2's own
  example; unknown tags dropped and reported.
- **Verified clean, no repair needed:** all 29 `Things` ids and all 37 `Passives` resolve against live vocab.
  None of the `Things` is an `EOR_STARTER_*` weapon, so the class pack has **no dependency** on the Armory
  starter pack.
- **Not ported:** the 31 hardcoded signature class skills, the mastery meta-progression, and the starting-kit
  code table — all DLL-only (audit §2.1) and owned by ClassForge M3 recipes (charter D4). The converter writes
  a `parked` report row per class naming its DLL-only skill so the coverage matrix (charter D8) can consume it.

### B5.1 The stats report sidecar — report only, never rebalance

`build_stats_report(vanilla_player_classes, eor_classes)` emits, per stat, the vanilla min/max/p50/mean over
the 23 vanilla `PLAYER`-tagged classes and the same over the 31 EOR classes, plus a per-class row flagging
every stat that exceeds the vanilla max (the §0.4 table is exactly this output). It emits **no suggested
values and mutates nothing**. Rebalancing is a later human/design pass; the converter's contract is faithful
transcription plus visibility.

**DECISION (B5):** re-prefix to `CF_EOR_*`, rewrite `LocKey`, strip to native `CharacterConfig` fields, copy
`Things`/`Passives`/`Stats` **verbatim**, emit `eor-class-stats-report.md` flagging LCK 95 / CRT 9 / FOC 5 /
SPD 84 / AWR 84 / TAL 82 / INT 86 as above-vanilla-max, and **make no balance change whatsoever**.

## B6. Provenance

Every emitted entry, in all three converters:

```json
{"Source": "eor-import", "EorId": "<original EOR id>", "PackageVersion": "0.7.0.60"}
```

- **Items:** inline as the entry's `Provenance` key inside the manifest pack (the manifest schema already has
  this field; neither `validate_pack` nor `compile_pack` reads it, so it is inert-but-preserved and it is what
  distinguishes converted from hand-crafted entries in `FTK2.Armory/packs/`).
- **Followers:** in the pack's `provenance.json` sidecar (A2.2 — `FollowerCharacterConfig` has no such field
  and `followers.json` is merged straight into `Configs`).
- **Classes:** in a `provenance.json` sidecar in the ClassForge pack folder, same reasoning
  (`CharacterConfig` has no such field).

`PackageVersion` comes from `--package-version` (default `0.7.0.60`), never from a filename or mtime.

## B7. Id-collision detection against the vocab snapshot

Before writing anything, every emitted id in all three converters is checked against
`set(vocab["AllIds"]) | set(vocab["ItemIds"])` **and** against the set of ids emitted so far in this run. A hit
is a **blocking** finding (exit 1, nothing written).

Caveat that must be in the tool's `--help` and in the report header: `tools/out/vocab-index.json` is currently
**contaminated** — the game install was overwritten by EOR (audit §1), so `AllIds` contains 31 `EOR_*` class
ids that are not vanilla. Since every id this tool emits is `ARM_`/`SMN_`/`CF_`-prefixed, the contamination
cannot cause a false positive here; but the report states the vocab file's provenance and the run is re-required
after the Steam verify completes (charter risk 1 / acceptance criterion 6).

## B8. Corpus invariants (`assert_corpus_invariants`)

Run first, before any conversion. Each is a measured fact from §0; a mismatch is a **blocking** finding with a
message naming the expected and actual value, so pointing the tool at a different EOR build fails loudly
instead of emitting subtly wrong packs.

1. `EORR_CustomItems.json` has 386 entries; `StarterWeapons.json` 31; `Examples.json` 4.
2. Every top-level key of the three item files matches `^(EORR_|EOR_STARTER_|EOR_EXAMPLE_)`.
3. After `hoist_ability_bags`, **zero** items have bag ids outside their own `Abilities` dict.
4. Zero items reference an ability id absent from `vocab["Abilities"]`.
5. The pets `Followers.json` key set is a subset of the mercs key set.
6. Exactly 100 `COMPANION_PLUS_*` and 100 `MERC_PLUS_*`.
7. `ClassName == key` for every follower.
8. The package `Characters.json` has exactly 31 `EOR_*` player-tagged entries.
9. Every EOR class `Things` id ∈ `vocab["ItemIds"]` and every `Passives` id ∈ `vocab["Skills"] ∪ AllIds`.

## B9. Determinism

- Byte-identical output for identical input, enforced by a test that runs `main()` twice into two temp dirs
  and compares every file's bytes.
- Every dict written via `write_json` → `json.dump(obj, f, sort_keys=True, indent=2, ensure_ascii=False)` +
  `f.write("\n")`, file opened `newline="\n"` — identical to `compile_pack._merge_json` so converted and
  hand-crafted output are diff-comparable.
- Item entries inside a pack's `Items` array are sorted by `Id`; report rows sorted by `(kind, id)`.
- No `datetime`, no `uuid`, no `random`, no `hash()` of a str, no set iteration reaching output.

---

## C. Test plan

### C.1 `tools/tests/test_eor_import.py` — 35 tests

Style follows `tools/tests/test_compile_pack.py`: `sys.path.insert(0, tools/)` then
`from eor_import import …`. Fixtures are **small hand-written dicts shaped like the real entries quoted in
§0**, not the 600 KB package — the suite must run without `D:\temp`. Three integration tests are marked
`@pytest.mark.skipif(not SOURCE.exists())` and run against the real package when present.

**Shared helpers (3)**
1. `test_humanize_reproduces_eor_pixie` — `COMPANION_PLUS_PIXIE_GENERIC_00` → `"Pixie"`
2. `test_humanize_reproduces_eor_skraevin` — → `"Skraevin Chaos Melee"`
3. `test_humanize_collapses_possessive_s` — `EORR_RIVET_SAMURAI_S_KATANA` → `"Rivet Samurai's Katana"`

**Items — id & tags (5)**
4. `test_map_item_id_eorr_prefix`
5. `test_map_item_id_starter_prefix`
6. `test_map_item_id_example_returns_none`
7. `test_tag_policy_drops_eor_namespace_and_bare_stat_tags`
8. `test_tag_policy_renames_gear_bands_into_arm_namespace`

**Items — bag hoist (5)**
9. `test_hoist_prefers_top_level_bag_over_interactable` (NIGHT_SHADOW_BLADE fixture)
10. `test_hoist_removes_non_schema_top_level_keys_from_thing`
11. `test_hoist_populates_fillbag_from_top_level`
12. `test_hoist_leaves_bagless_item_without_bags` (shield fixture)
13. `test_residual_undeclared_bag_ids_are_dropped_and_reported`

**Items — class & fallback (6)**
14. `test_class_remap_table_targets_all_present_in_vocab`
15. `test_class_remap_sword_to_blade` / parametrized over all 6 table rows
16. `test_unmapped_illegal_class_is_blocking`
17. `test_donor_pick_is_first_sorted_donor_for_class`
18. `test_donor_alias_orb_falls_back_to_orb_2h`
19. `test_class_without_any_donor_is_blocking`

**Items — loc & provenance (3)**
20. `test_katana_gets_synthesized_name_and_description`
21. `test_existing_eor_loc_is_copied_verbatim_under_new_id`
22. `test_every_item_entry_carries_provenance_with_eor_id_and_version`

**Followers (7)**
23. `test_follower_id_map_companion_and_merc`
24. `test_sheep_entries_classified_drop_dangling`
25. `test_entry_without_configname_classified_drop_no_config`
26. `test_vanilla_id_entries_parked_not_emitted`
27. `test_follower_field_passthrough_preserves_vanilla_loc_keys` (Subtitle/JoinParty/LeaveParty untouched)
28. `test_follower_classname_rewritten_to_new_id`
29. `test_follower_enum_values_validated_against_ground_truth_sets`

**Classes (4)**
30. `test_class_id_and_lockey_rewritten`
31. `test_class_stripped_to_native_character_fields`
32. `test_class_stats_and_things_copied_verbatim` (no rebalancing)
33. `test_stats_report_flags_lck_95_above_vanilla_max`

**Cross-cutting (2)**
34. `test_id_collision_against_vocab_snapshot_is_blocking`
35. `test_two_runs_produce_byte_identical_output`

**Integration, skipped without the package (3 of the above are the integration variants of 34/35 plus):**
`test_real_package_item_packs_pass_validate_pack` — runs `validate_pack.validate_pack()` on both emitted item
packs against the real `vocab-index.json` and asserts **zero ERROR-level findings**. This is the acceptance
gate for Part B.

### C.2 `FTK2.Summoner/tests/Summoner.Core.Tests` — 14 tests (xunit, net8.0, Core-only)

1. `PackLoader_SortsPackIdsOrdinally_NotFilesystemOrder`
2. `PackLoader_TopoSortsByLoadOrderThenIdOrdinal`
3. `PackLoader_MissingDependency_SkipsPackAndReportsFinding`
4. `PackLoader_DependencyCycle_SkipsPacksAndReportsFinding`
5. `PackLoader_MalformedJsonInOnePack_DisablesOnlyThatPack`
6. `Validator_RejectsIdWithoutSmnPrefix`
7. `Validator_RejectsUnknownFollowerType`  (`INANIMATE` and `CURSE` both rejected in M0)
8. `Validator_RejectsUnknownContractPriceAndBehaviour`
9. `Validator_RejectsAppendableStatKeyNotInCharacterStats`
10. `Validator_RejectsUnresolvableConfigName_AgainstSuppliedIdSet`
11. `Validator_AcceptsConfigNameSatisfiedByPacksOwnCharactersJson`
12. `MergePlanner_SkipsIdAlreadyPresentInLiveConfigs` (adds-only)
13. `MergePlanner_IsIdempotent_SamePlanTwiceIsNoOp`
14. `DataHasher_IsStableAcrossFileOrder_AndExcludesLocalization`

Total planned: **49** (35 pytest + 14 xunit).

---

## D. Build order for Wave 2

Two independent units; neither blocks the other.

**Unit D5 (Summoner M0)** — `Summoner.Core` + tests first (green with no game DLLs), then `Summoner.Plugin`
source (build parked on `MSB3245` until `tools/bin/refs/` exists), then the SPEC §3/§4/§5/§9 delta recording
the M0 loader, the `FollowerPacks` format, adds-only, and the confirmed `Block` posture.

**Unit D6 (`eor_import.py`)** — constants + shared helpers + items converter + its 22 tests, then followers +
7, then classes + 4, then cross-cutting + integration. `assert_corpus_invariants` lands with the first
converter, not last.

**Wave 3 consumes both:** run the converter → 2 Armory packs (417 items), 2 Summoner follower packs (200
entries), 1 ClassForge class pack (31), 3 report sidecars.

---

## E. Risks and parked items

1. **`vocab-index.json` is EOR-contaminated** (audit §1: the game install was overwritten). Class/tag/stat
   vocabularies are verified clean (enum-ground-truth's contamination check), but `AllIds` carries 31 `EOR_*`
   ids. All emitted ids are prefixed, so no false positive is possible today. **The converter must be re-run
   and the packs re-validated after the Steam verify.** Charter acceptance criterion 6 already parks this.
2. **`AS_FOLLOWER` summon-pool widening** (A6.3): adding 200 followers enlarges a weighted pool drawn from the
   shared `CombatState.Random`. Mixed-pack peers would desynchronise the RNG stream, not merely disagree on a
   creature. Mitigated by `Block`; must be restated in the SPEC §9 delta and re-examined if `WarnOnly` is ever
   offered.
3. **`AppendableStats` consumption path is unconfirmed** (SPEC §11.8). 102 of the 200 emitted followers carry
   it. Values are transcribed faithfully; they may be inert. A `WARN` report row records this per entry.
4. **`LUTE → LUTE_2H`** is the one class remap whose target family's hand-count convention disagrees with the
   item (`Slots: [MAIN_HAND]`, tag `WEAPON_ONE_HAND`). `Class` is a free string used for donor/loot grouping,
   not hand-count, so this is believed harmless — but it is reported for human review and is the first thing
   to check if those three starters misbehave in-game.
5. **40 vanilla-follower overrides are parked**, not ported. If the M1 override path is never built, EOR's
   permanent-contract tuning of the *vanilla* mercenaries is simply absent (the `_PLUS_` clones retain it).
   Acceptable; recorded in `vanilla-overrides.json`.
6. **31 DLL-only class signature skills, mastery, and starting kits are not ported by this converter** — they
   are ClassForge M3 recipe territory (charter D4). The class pack ships stats + gear + vanilla passives only.
7. **Plugin build remains parked** until `tools/bin/refs/` is populated (`build-template-notes.md` §0/§6).
   `Summoner.Core` must be fully green regardless — that is the point of the split.
8. **Katana descriptions are synthesized flavour text.** They are ours, not EOR's, and they are template-driven
   rather than authored. A later hand-pass over the 26 is a reasonable content task, not a blocker.
