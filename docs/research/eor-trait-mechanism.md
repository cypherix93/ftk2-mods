# EOR "selectable loadout traits" — complete mechanism (line-cited)

**Date:** 2026-07-25 · **Purpose:** answer `FTK2.ClassForge/SPEC.md` Open Question #1 (how to support
custom trait ids outside the native 17-value `eTraits` enum), using EOR's 20 selectable loadout traits as
worked evidence.

**Sources:**
- EOR decompile: `tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs` (29,415 lines).
  All bare `L####` citations below are into this file unless prefixed otherwise.
- Native game decompile: `tools/out/decompile/FTK2/*.cs`. Citations prefixed with a filename
  (e.g. `CharacterHelper.cs:1989`) are into that native file.
- Context: `docs/research/eor-0760-content-audit.md` §2.4, §7.

**Headline finding:** EOR's 20 traits are not a parallel trait system. They are ordinary `Thing` items
(config `Class = "TRAIT"`, `ConfigName` prefixed `"TRAIT_"`) sitting in the character's normal inventory
list (`CharacterComponent.Things`). The *native* game already has a fully generic, enum-independent trait
substrate keyed purely on the `"TRAIT_"` string prefix — `eTraits` is a vestigial enum that is defined but
**never referenced anywhere at runtime** in the decompiled assembly. EOR simply mints new `ThingConfig`
entries under that string convention and slots them into the vanilla "party loadout" (starting-gear pick)
screen. This is exactly the mechanism ClassForge OQ#1 needs, and it requires no engine changes to `eTraits`
at all.

---

## 0. The 20 traits (for reference)

`SelectableLoadoutTraitKeys` (L5183–5187, 20 entries), display names (L5189–5211), tooltip descriptions
(L5213–5235):

```
TRAIT_PREPARED, TRAIT_PACK_TACTICS, TRAIT_SCAVENGER, TRAIT_BATTLE_RHYTHM, TRAIT_ARCANE_MEMORY,
TRAIT_SHIELDBEARER, TRAIT_FIELDMEDIC, TRAIT_LIGHT_FOOTED, TRAIT_TREASURE_SENSE, TRAIT_ARCANE_FOCUS,
TRAIT_MOMENTUM, TRAIT_TOUGHENED, TRAIT_STEADY_AIM, TRAIT_STREETWISE, TRAIT_WARDBOUND,
TRAIT_DRUNKEN_COURAGE, TRAIT_SCHOLARS_HABIT, TRAIT_GOLD_INSTINCT, TRAIT_MENDERS_TOUCH, TRAIT_KNIFE_EDGE
```

Note the collision: `TRAIT_FIELDMEDIC` is *also* a member name of the native `eTraits` enum
(`eTraits.cs:10`). This is a same-string coincidence, not a code dependency — see §4.

---

## 1. Where is the selected trait stored?

**Answer: as a `Thing` object (not a status, not `CustomData`, not `GameRunData.Stats`) inside the
character's own `CharacterComponent.Things` list — the same list vanilla native traits, inventory items,
and equipment all live in. Persists automatically because `Things` is an ordinary, un-ignored field on a
`System.Text.Json`-serializable component.**

- `CharacterComponent.Things` field: `CharacterComponent.cs:38` — `public List<Thing> Things;`. The class
  (`CharacterComponent.cs:4`) has no `[JsonIgnore]` on `Things` (contrast `CachedFocus`/`RequiredFocus` at
  `CharacterComponent.cs:26-30`, which *are* ignored) — it round-trips through the normal save/load JSON
  serialization used for every other character field. No bespoke EOR save hook is needed or present.
- `Thing` shape: `Thing.cs:6-24` — `Id`, `ParentId`, `ConfigName`, `Type` (`eThingTypes`), `_stackCount`,
  `CustomData`, `BaseStatModifiers`, `PassiveModifiers`, `Expansion`. The trait *identity* is carried
  entirely by `ConfigName` (e.g. `"TRAIT_LIGHT_FOOTED"`); no separate "selected trait id" field exists
  anywhere on the entity.
- Detection reads directly off that list: `HasSelectedTrait` (L22638-22645) —
  `component.Things.Any(thing => string.Equals(thing.ConfigName, traitKey, ...))`.
- This is not an EOR invention. The **native** trait accessor works identically:
  `CharacterHelper.GiveTrait(Entity, string)` (`CharacterHelper.cs:1984-1987`) creates a `Thing` via
  `InventoryHelper.CreateThing(pTraitName)` and calls `GiveTrait(Entity, Thing)` (`CharacterHelper.cs:1989`)
  which does exactly `pCharacter.Get<CharacterComponent>().Things.Add(pTrait)`. `RemoveAllTraits`
  (`CharacterHelper.cs:1994-2000`) finds every `Thing` whose `ConfigName.StartsWith("TRAIT_")` in `Things`
  and removes it. `InventoryHelper.GetTraits(List<Thing>)` (`InventoryHelper.cs:1263-1266`) is literally
  `pThings.FindAll(t => t.ConfigName.StartsWith("TRAIT_"))`. The native `eInventoryFilters.TRAITS` filter
  (`InventoryHelper.cs:251-252`) dispatches to that same method.
- Where the trait `ThingConfig` (as opposed to the instance `Thing`) lives: `Env.Configs.Things[traitKey]`,
  minted at runtime by `EnsureSelectableLoadoutTraitConfigs` (L18554-18587) → `CreateSelectableLoadoutTraitConfig`
  (L18697-18757). This dictionary is **not** itself save-persisted; it's re-minted deterministically every
  session (same 20 keys, same stats every time — see §6 for why this is safe, unlike EOR's affix-variant
  minting).

**Save/load survival:** because the trait is just an entry in `Things`, it survives exactly as well as any
other inventory item or vanilla trait — no special-case save code exists or is needed. `Thing.Id` for
injected trait things is a **deterministic stable hash** (`AssignDeterministicThingId`, L17276-17284,
`"EOR_" + ComputeStableHash("EOR_THING_ID|" + parts)`) rather than a random GUID — deliberately, so the
same trait `Thing` gets the same `Id` on every peer/session (relevant to §5).

---

## 2. How is the trait SELECTED — which UI, how is it captured/applied?

**Answer: no custom trait-picker UI exists. EOR injects the 20 trait `Thing`s into the pool used by the
*vanilla* party-creation "loadout" screen (the same screen where players spend Load-Out Points on starting
weapons/gear before an adventure), tagged as free (cost 0). The player picks them there, and the vanilla
picker code performs the exact same `Things.Add` as any other loadout item.**

- Injection point: `LootDropHelper.GetAdventureLoadOut(string, GameRandom)` — a **native** vanilla method
  (`LootDropHelper.cs:336-377`) that already exists to build the party-creation Thing pool from
  `Env.Configs.LoreStore` entries tagged `Category == LOADOUT || Category == TRAIT`
  (`LootDropHelper.cs:343-347`), each replicated `StatsHelper.GetStat(...)` times. EOR patches it with a
  Harmony **postfix**: `LootDropHelper_GetAdventureLoadOut_Postfix` (L15763-15816), registered at
  L8672/L9455. For each of the 20 trait keys not already present in `__result`, it does
  `InventoryHelper.CreateThing(traitKey)`, stamps a deterministic `Id`, sets stack 1, and appends it to the
  pool (L15789-15796).
- Guard: `ShouldSkipCustomAdventureLoadoutInjection` (L15818-15837) — skips injection when
  `GameRunData.ConfigName` contains `"DUNGEON_CRAWL"` (a separate one-off game mode with its own vanilla
  loadout progression the author didn't want to disturb). In every normal campaign start, the 20 traits are
  injected into the pool alongside real starting gear.
- Cost mechanism: each trait `ThingConfig.Tags` carries `"LOADOUT_0"` (L18576-18577, L18754). The **native**
  `LootDropHelper.GetLoadOutValue` (`LootDropHelper.cs:379-395`) reads the `"LOADOUT_"`-prefixed tag as the
  point cost of a pool item in the picker UI — `0` means the trait is free to take, so a player can select
  every one of the 20 without spending their gear budget.
- Consumer / actual picker: **native** `PartyManagementDirector` — `_loadOutItemThingsPool =
  LootDropHelper.GetAdventureLoadOut(...)` (`PartyManagementDirector.cs:176`, re-run at
  `PartyManagementDirector.cs:4270` on `JIP_UPDATE_STATS`). Player pick/unpick is
  `PartyManagementActionData.TakeUntakeItemActionData` → `_takeAndEquip`/`_untakeAndUnequip`
  (`PartyManagementDirector.cs:4184-4203`), which is what actually does
  `characterComponent.Things.Add(thing)` (`PartyManagementDirector.cs:3461`, also `:629`) — i.e. **capture
  and application are one and the same vanilla call**; there is no separate "apply the choice" step for EOR
  to hook.
- EOR touches no UI rendering code for trait selection. (The `CharacterCustomizationViewHelper_*` patches
  at L19342-19563 the task anchors point to are unrelated — they handle the class list / species-label
  rendering on the character-creation screen, not trait selection. This is worth flagging: **the anchor was
  a red herring for this question**; the real selection surface is `PartyManagementDirector`'s loadout
  screen, reached via the `LootDropHelper.GetAdventureLoadOut` patch.)

---

## 3. How are trait EFFECTS applied — every path, and how "has trait X" is detected

**Detection primitive (used by every path below):** `HasSelectedTrait(CharacterComponent, string)`
(L22638-22645) = `component.Things.Any(t => string.Equals(t.ConfigName, traitKey, OrdinalIgnoreCase))`.
Pure string scan of the same `Things` list described in §1 — no enum, no status check, no equip-slot check.

### 3a. Stat-only traits (9) — zero DLL code needed once the config exists
`TRAIT_LIGHT_FOOTED, TRAIT_TREASURE_SENSE, TRAIT_TOUGHENED, TRAIT_STEADY_AIM, TRAIT_STREETWISE,
TRAIT_WARDBOUND, TRAIT_SCHOLARS_HABIT, TRAIT_GOLD_INSTINCT, TRAIT_KNIFE_EDGE` each get a flat
`Equippable.Stats` dict populated in the `CreateSelectableLoadoutTraitConfig` switch (L18701-18732), e.g.
`TRAIT_LIGHT_FOOTED → EVD +10` (L18703-18705), `TRAIT_TOUGHENED → HP +10, DEF +2` (L18709-18712),
`TRAIT_KNIFE_EDGE → CRT +10, HP -3` (L18728-18731).

These stats apply automatically through **native** code, with no EOR patch involved:
`CharacterHelper.GetStat(...)` (`CharacterHelper.cs:422-562`) builds its equipped-things hash set via
`EquipmentHelper.GetEquippedThingsNonAlloc(pCharacterEntity, hashSet2, pVisualOnly: false, pIncludeTraits:
true)` (`CharacterHelper.cs:440`). That native method (`EquipmentHelper.cs:312-339`) contains the load-bearing
line:

```csharp
foreach (Thing thing in characterComponent.Things) {
    if (pIncludeTraits && thing.ConfigName.StartsWith("TRAIT_")) { pOutput.Add(thing); continue; }
    ... // else: only Things actually in an equipment slot are added
}
```
(`EquipmentHelper.cs:320-326`)

Any `Thing` whose `ConfigName` starts with `"TRAIT_"` is treated as "equipped" for stat purposes **regardless
of equip slot** — which is why EOR's trait `ThingConfig.Equippable.Slots = Array.Empty<eEquipmentSlots>()`
(L18747) still works: the stat contribution comes from `InventoryHelper.GetThingStat(item, pStat)`
(`CharacterHelper.cs:477`) once the item is in the (trait-inclusive) equipped set, not from any slot
assignment. This is 100% native game plumbing that predates EOR — it's the same mechanism that makes
vanilla `TRAIT_TACTICIAN` etc. grant their stats without an equip step.

### 3b. Conditional-but-still-`GetStat` traits (2)
`ApplySelectableTraitConditionalStats` (L22616-22626), called from the Harmony postfix
`CharacterHelper_GetStat_Core_Postfix` (L22518-22526, patch target is the native `GetStat` core):
- `TRAIT_PACK_TACTICS`: `statKey == "PHY"` and `HasSelectedTrait(..., "TRAIT_PACK_TACTICS")` and the party
  has a pet/mercenary → `+2` (L22618-22621).
- `TRAIT_ARCANE_FOCUS`: `statKey == "MAG"` and `HasSelectedTrait(..., "TRAIT_ARCANE_FOCUS")` and
  `CurrentFocus >= 2` → `+2` (L22622-22625).

### 3c. Bespoke Harmony patches (9 traits — enumerated with exact hook + detection)
| Trait | Hook (method / kind) | Line | Effect |
|---|---|---|---|
| `TRAIT_PREPARED` | `CombatHelper.SetInitiative` postfix | L22849 (fn starts L22830) | `+1` Focus at combat start once (via `HasSelectedTrait`) |
| `TRAIT_STEADY_AIM` | `CombatHelper.PerformAbility` prefix (delegate-wraps the stat-getter) | L22876 (fn starts L22871) | first ranged attack this combat gets `+10` CRT via wrapped `__6` delegate; budgeted per-combat by `SteadyAimUsedThisCombat` HashSet (declared L4823) |
| `TRAIT_SCAVENGER` | `LootDropHelper.GetLootDropsFromEnemies` postfix | L16357 (fn starts L16339) | 25% chance → gold or common herb reward |
| `TRAIT_TREASURE_SENSE` | same postfix | L16379 | 20% chance → gold reward (on top of its flat LCK stat from §3a) |
| `TRAIT_SCHOLARS_HABIT` | same postfix | L16389 | 20% chance → random scroll reward (on top of flat INT stat) |
| `TRAIT_ARCANE_MEMORY` | `InventoryHelper.Consume` prefix | L24462 (fn starts L24454) | 30% chance to cancel scroll consumption (`return false`) |
| `TRAIT_FIELDMEDIC` | `CharacterHelper.AddHealth` prefix | L24495 (fn starts L24486) | +25% to healing done or received |
| `TRAIT_MENDERS_TOUCH` | same prefix | L24502 | consumable healing items get flat `+10` HP |
| `TRAIT_BATTLE_RHYTHM` | `InteractableHelper.ApplyStatChange` postfix | L24556 (fn starts L24541) | after landing damage, applies vanilla status `STATUS_EVADEUP_00` |
| `TRAIT_MOMENTUM` | same postfix | L24565 | on kill, applies vanilla status `STATUS_ATTACKUP_00` |
| `TRAIT_SHIELDBEARER` | `InteractableHelper.CalculateFinalDamage` postfix | L24608 (fn starts L24604) | 25% chance, incoming physical damage `-2` (min 0) |
| `TRAIT_DRUNKEN_COURAGE` | `InteractableHelper.PerformConsumableAbility` postfix | L24632 (fn starts L24628) | after consuming a `DRINK_*` item, applies `STATUS_ATTACKUP_00` |
| `TRAIT_WARDBOUND` | `InteractableHelper.ApplyStatus` (target overload) prefix | L24780 (fn starts L24772) | 20% chance to cancel an incoming CURSE/DEBUFF status (`return false`) |

All the % chance rolls funnel through `RollSelectableTraitChance(GameRandom?, decimal)` (L24432-24452),
which resolves a `GameRandom` via `TryResolveGameplayRandom` and calls `.NextChance(chance)` — i.e. every
roll draws from the shared deterministic combat RNG stream, not `System.Random`/`UnityEngine.Random` (good
RNG-sourcing discipline, per the parent audit doc §3 point 2 — but see §6 for why the *volume* of draws is
still an MP hazard).

---

## 4. Does EOR touch the native `eTraits` enum / `GiveTrait` surface?

**No — and more importantly, at runtime *neither does the native game*.**

- `eTraits` (`eTraits.cs:1-20`) is a 17-value enum (`TRAIT_TACTICIAN` … `TRAIT_LONEWOLF`, including
  `TRAIT_FIELDMEDIC` at index 7).
- A repo-wide search of the decompiled native assembly for `eTraits.<Member>` usages (i.e. actual enum-value
  references, as opposed to substring hits like `pIncludeTraits`) returns **zero results**. The enum type is
  declared and otherwise dead in this assembly — no `GiveTrait(Entity, eTraits)` overload exists;
  `CharacterHelper.GiveTrait` only takes `string` or `Thing` (`CharacterHelper.cs:1984`, `:1989`). The
  *entire* runtime trait substrate (storage, `HasTrait`-equivalent checks, `GetFirstTrait`,
  `RemoveAllTraits`, the `TRAITS` inventory filter) is driven purely by the `"TRAIT_"` string-prefix
  convention on `Thing.ConfigName`, described in §1 and §3a.
- Consequence: EOR's `TRAIT_*` things are **not a parallel system bolted on beside the real one** — they use
  literally the same storage list, the same detection convention, and the same stat-aggregation code path as
  vanilla traits. The only EOR-authored pieces are (a) minting the 20 `ThingConfig` entries, (b) injecting
  them into the loadout-picker pool, and (c) a handful of bespoke Harmony patches for the 11 traits whose
  effect isn't a flat stat (§3c).
- **They do show up in native trait UI surfaces**, because those surfaces are also just `"TRAIT_"`-prefix
  scans: `eInventoryFilters.TRAITS` / `InventoryHelper.GetTraits` (`InventoryHelper.cs:251-252,
  1258-1266`) will list them in the inventory "Traits" tab, and `CharacterHelper.GetFirstTrait`
  (`CharacterHelper.cs:1974-1982`, used by `AdventureSummaryViewHelper.cs:2508`,
  `CharacterSummaryViewHelper.cs:247`, `InventoryViewHelper.cs:1507`, `UIHelper.cs:561` for the
  character-sheet/summary "trait" display) returns `traits[0].ConfigName` from
  `InventoryHelper.GetTraits(pCharacterComponent.Things)` — i.e. the *first* `TRAIT_`-prefixed item in
  `Things` order wins the single-trait display slot. Practically: if a character has both a vanilla trait
  and one or more EOR traits, whichever was added to `Things` first is what the character-summary "Trait:"
  line shows; the others are silently present (contributing stats/effects) but not shown in that one-line
  UI. `RemoveAllTraits` (`CharacterHelper.cs:1994-2000`) would strip *all* of them indiscriminately (native
  and EOR) since it matches on prefix alone, not on `eTraits` membership.
- The `eTraits` enum, being unused, imposes **no practical ceiling of 17** — the "custom trait ids outside
  eTraits" problem doesn't actually need solving at the storage layer; it was never gated by the enum in the
  first place. ClassForge can mint arbitrary `TRAIT_<name>` `ThingConfig`s the same way and they will be
  fully functional without touching `eTraits.cs`.

---

## 5. Multiplayer interaction — replicated or client-local?

**The storage substrate is replicated (it's part of the standard entity-serialization/action-log path); the
combat-time trait *effects* are not synced and are a live MP hazard, matching the parent audit's findings.**

- **Selection is synced.** Trait pick/unpick happens through
  `PartyManagementActionData.TakeUntakeItemActionData`, dispatched as a normal networked game action and
  applied via `PartyManagementDirector._handleNetworkAction` (`PartyManagementDirector.cs:4184-4203`,
  `:4270`). The pool itself (`_loadOutItemThingsPool`) and per-player assignment
  (`_loadoutIndicesForPlayers`) are explicitly carried in `PartyManagementDirector`'s sync payload
  (`PartyManagementDirector.cs:142` restores `pSyncData.LoadoutIndicesForPlayers`; `:3924-3925` writes it
  back out) and re-broadcast on join-in-progress (`JIP_UPDATE_STATS`, `PartyManagementDirector.cs:4256-4276`).
  EOR's deterministic `Thing.Id` hashing (L17276-17284, §1) exists specifically so the trait items in that
  pool resolve to the same index/identity on every peer — a deliberate synchronization concession the author
  got right.
- **Once selected, the `Thing` lives in `CharacterComponent.Things`**, which is ordinary entity state that
  replicates the same way the rest of a character's inventory does (part of `Entity`/`GameRunData` state
  sync, not a side channel). `HasSelectedTrait` reads this replicated state directly, so *whether* a
  character has a trait is consistent across peers.
- **The combat-time bespoke effects (§3c) are not gated for MP and draw extra shared-RNG rolls per client.**
  `RollSelectableTraitChance` checks `ShouldDisableVolatileCombatMutationsForMultiplayer()`
  (L24434-24437) and `CombatHelper_SetInitiative_Postfix`'s Prepared-focus branch checks
  `ShouldDisablePreparedFocusForMultiplayer()` (L22849) — but both gates are **stubbed to always return
  `false`** (L14913-14921), so they never actually disable anything in MP. This matches
  `eor-0760-content-audit.md` §3 point 1 verbatim. Worse, two of the bespoke patches are outright
  **client-local suppression of an authoritative change**: `TRAIT_ARCANE_MEMORY` (`InventoryHelper.Consume`
  prefix, L24462, `return false` cancels item consumption) and `TRAIT_WARDBOUND`
  (`InteractableHelper.ApplyStatus` target prefix, L24780, `return false` cancels an incoming
  curse/debuff) — each peer rolls its own die and can unilaterally decide "this authoritative state change
  didn't happen" on its own client, with no host arbitration or sync of the roll outcome.
- Net effect for a clean-room design: **replicate trait selection via the normal entity-state channel (safe
  to copy)**; **do not copy EOR's per-client-RNG combat-effect execution model** — those effects need to be
  host-resolved (or otherwise made deterministic across peers) before application, not rolled locally by
  whichever client's code path happens to run first.

---

## 6. Clean-room reimplementation — minimal patch set, storage decision, and hazards NOT to copy

### What proved reusable without modification
The native substrate already does everything needed for "custom trait ids outside `eTraits`":
1. Storage: `CharacterComponent.Things : List<Thing>` (already exists, already serialized).
2. Naming convention: any `Thing.ConfigName` starting with `"TRAIT_"` is automatically:
   - included in `GetStat`'s equipped-set even with no equipment slot
     (`EquipmentHelper.GetEquippedThingsNonAlloc(..., pIncludeTraits: true)`, `EquipmentHelper.cs:322-326`),
   - surfaced by the `TRAITS` inventory filter (`InventoryHelper.cs:251-252`),
   - picked up by `GetFirstTrait` / `RemoveAllTraits` (`CharacterHelper.cs:1974-2000`).
3. Detection for bespoke (non-stat) effects: a one-line `Things.Any(t => t.ConfigName == traitKey)` scan —
   no engine change, no enum extension.

### Minimal patch set for a from-scratch (non-EOR) implementation
- **Config minting**: one startup routine that ensures N `ThingConfig` entries exist in `Env.Configs.Things`
  with `ConfigName = "TRAIT_<id>"`, `Class = "TRAIT"`, `Hidden = true`, `Equippable.Slots = []`,
  `Equippable.Stats = {...}` for stat-only traits. (Mirrors `EnsureSelectableLoadoutTraitConfigs` /
  `CreateSelectableLoadoutTraitConfig`, L18554-18757 — this part is safe to imitate structurally.)
- **Selection surface** (pick your own — EOR's choice is one valid option, not mandatory):
  - Reuse the vanilla party-loadout screen the way EOR does (`LootDropHelper.GetAdventureLoadOut` postfix +
    `"LOADOUT_0"` tag for free pick), **or**
  - Build ClassForge's own selection UI and directly call the native
    `CharacterHelper.GiveTrait(Entity, string)` (`CharacterHelper.cs:1984`) — this is a supported, public,
    already-networked-safe entry point and avoids depending on EOR's loadout-pool injection trick entirely.
- **Stat-only traits**: zero patches — just the config with `Equippable.Stats` populated. This is the
  primitive win from the parent audit doc's §7 table applied at the trait layer: no engine primitive needed
  at all for the 9-13 stat-only trait class.
- **Bespoke-effect traits**: one Harmony (or engine-native trigger) per effect family, gated behind a single
  shared `HasTrait(component, traitKey)` helper identical in shape to `HasSelectedTrait` (L22638-22645).
  Map each of EOR's 11 non-stat traits to the trigger/condition/effect primitives already catalogued in
  `eor-0760-content-audit.md` §7 (`ON_COMBAT_START`, `ON_STAT_CHANGED`, `ONCE_PER_COMBAT`,
  `DAMAGE_TAKEN_MULT`, `HEAL_RECEIVED/GIVEN_MULT`, `SUPPRESS_CONSUME`, `STATUS_APPLY_RESIST`, outgoing
  crit/damage buff window, gold grant) rather than porting EOR's per-feature Harmony-patch style.

### EOR bugs/hazards to explicitly NOT copy
1. **Client-local suppression of authoritative changes.** `TRAIT_ARCANE_MEMORY` (L24462) and
   `TRAIT_WARDBOUND` (L24780) each let a single client's local RNG roll decide to silently cancel a state
   change (item consumption / curse application) with no host arbitration — different peers can end up with
   different outcomes for the same authoritative event. Any "suppress with probability" effect must be
   host-resolved and the *result* (not the raw chance) replicated.
2. **Un-gated MP stubs.** `ShouldDisablePreparedFocusForMultiplayer` and
   `ShouldDisableVolatileCombatMutationsForMultiplayer` (L14913-14921) are hardcoded `return false` — dead
   guards that give the illusion of MP-awareness while doing nothing. Don't ship a feature-flag scaffold that
   isn't wired to a real check; either implement the gate or don't build the guard function at all.
3. **Per-process static combat-budget state not cleared on new run.** `SteadyAimUsedThisCombat` (a static
   `HashSet<string>` keyed by entity Guid, declared L4823, used L22848/L22876/L22890) is not cleared by
   `GameRunData.Create`'s postfix per the parent audit (§3 point 6) — stale entries can leak across
   runs/combats in the same process. Any "once per combat" budget needs to be combat-state-scoped (e.g. keyed
   off `CombatState`/`GameRandom` identity, as EOR itself does correctly for the *encounter-modifier* budget
   at L22679-22686), not a bare static collection.
4. **"Unsafe" custom statuses (the three the task flagged).** `UnsafeSelectableTraitCustomStatuses = {
   "STATUS_EOR_BATTLE_RHYTHM", "STATUS_EOR_MOMENTUM", "STATUS_EOR_DRUNKEN_COURAGE" }` (L4399) are actively
   stripped from any entity's `StatusEffectComponent` at four call sites: on load-stability sanitize
   (`SanitizeGameRunForLoadStability`, L10662), on every combat-entry `SetInitiative` postfix (L22835), on
   every successful-attack `ApplyStatChange` postfix (L24554), and after every `DRINK_*` consumable use
   (L24635). **Why they're unsafe:** these three keys are never minted as `StatusEffectConfig` entries
   anywhere in this build (`Env.Configs.StatusEffects` has no entry for them — confirmed by grep, zero
   matches outside the L4399 array literal). `EnsureSelectableLoadoutTraitStatusConfigs`
   (L18589-18615) shows the mod's *current* Battle-Rhythm/Momentum/Drunken-Courage effects were
   deliberately redirected to point at pre-existing **vanilla** statuses instead
   (`STATUS_EVADEUP_00`, `STATUS_ATTACKUP_00` — used at L24558, L24567, and via
   `InteractableHelper_PerformConsumableAbility_Postfix`) — i.e. the custom `STATUS_EOR_*` keys are leftover
   from an earlier version of the mod. A status key referenced by a save file, a replicated action, or a
   `StatusEffectComponent` entry that has **no matching config** in the current build's `Env.Configs`
   is a real hazard class: depending on how the engine's status-tick/stat-aggregation code handles a missing
   config lookup, it can silently no-op, throw, or (worse) leave a permanently-stuck status the game can
   never expire because `Duration`/`TickExpire` can't be read. EOR's defensive stripping is a symptom of
   this exact hazard, retained across a version bump rather than fixed at the source (i.e., they never
   guaranteed old custom status ids stay valid across updates). **Lesson: never invent a new
   `StatusEffectConfig` id for a versioned/patchable mod if an existing vanilla status already has the
   wanted shape (duration/stat mix) — reuse it, the way EOR's own later code does — and if a custom status
   id is ever retired, provide a real migration (strip-and-replace on load) rather than an ad hoc "unsafe
   list" scattered across four unrelated patch sites.**
5. **Runtime-only config minting is fine for traits specifically because it's deterministic — don't
   generalize that safety to other EOR systems.** `EnsureSelectableLoadoutTraitConfigs` re-mints the same 20
   fixed `ThingConfig`s every session (L18554-18757), which is safe: the *content* never varies, so
   `Env.Configs.Things` not being save-persisted is a non-issue. This is **not** true of EOR's runtime affix
   variant minting (`TryEnsureAffixVariantConfig`, flagged in the parent audit §2.5/§3.6 as needing a whole
   restore/repair layer) — that system mints *instance-specific* configs that vary per roll and therefore
   does need persistence or regeneration-with-identical-inputs, which EOR doesn't guarantee. Keep the
   trait-minting pattern; do not extend it to anything whose content isn't a fixed, version-pinned set.

---

## Answers at a glance

| # | Question | Answer |
|---|---|---|
| 1 | Storage | `Thing` (ConfigName = trait key) inside `CharacterComponent.Things`; persists via ordinary component serialization (`CharacterComponent.cs:38`, `Thing.cs:6`). |
| 2 | Selection UI | Vanilla party-loadout ("starting gear") screen; EOR injects free-cost trait `Thing`s into `LootDropHelper.GetAdventureLoadOut`'s pool (L15763-15816); vanilla `PartyManagementDirector` take/untake action captures and applies the pick in one step (`PartyManagementDirector.cs:3461`, `:4184-4203`). |
| 3 | Effects | 9 pure-stat via native `GetStat`+`Equippable.Stats` (no patch); 2 conditional via `GetStat` postfix (L22616-22626); 9 bespoke Harmony patches, all gated by `HasSelectedTrait` (L22638-22645) — full table in §3c. |
| 4 | Native `eTraits` usage | No — and neither does the base game at runtime (enum is dead code); everything runs off the `"TRAIT_"` `ConfigName` prefix convention, so EOR traits fully interoperate with native trait UI/filters/removal. |
| 5 | MP visibility | Selection is replicated (standard party-creation action sync + entity state); combat-time bespoke effects are not MP-gated (stubs always return false) and two patches let a single client cancel authoritative changes locally. |
| 6 | Reimplementation | Reuse `Things`/`"TRAIT_"` storage and `CharacterHelper.GiveTrait` as-is; mint deterministic fixed configs; gate bespoke effects behind a real MP check; never invent new status ids when a vanilla one fits; never let a versioned status id go unmigrated. |
