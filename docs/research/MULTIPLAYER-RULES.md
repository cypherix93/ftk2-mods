---
title: FTK2 Multiplayer Rules - derived from a confirmed-working EOR 0.7.0.66 build
type: reference
tags: [ftk2, classforge, multiplayer, determinism, eor, rules]
repo: C:\Users\ben\repos\ftk2-mods-crucible
---

# MULTIPLAYER RULES — stay inside EOR 0.7.0.66's proven envelope

**Position this document encodes:** co-op already works with Enhanced Overhaul Revamped installed.
We are **not** building multiplayer. We are extending the same game with a second mod, and the job
is to stay inside the shape EOR proved. These are the rules that shape is made of.

## Provenance

| | |
|---|---|
| Build | `C:\Users\ben\Downloads\Mod_Only 29 0.7.0.66 2026-08-17T17-38Z 5VstO9EsE` |
| `EOR_PACKAGE.json` | `sirpepperpot.enhanced-overhaul-revamped`, `package_version: 0.7.0.66`, `package_kind: mod-only` |
| Binary | `BepInEx/plugins/EnhancedOverhaulRemix.dll`, 766,976 bytes |
| Decompile | `ilspycmd 8.2.0.7535` (run with `DOTNET_ROLL_FORWARD=LatestMajor`) → `.decompile-scratch/EOR-0766/FTK2.BanditKingPlayable/Plugin.cs`, **33,752 lines, one `Plugin` class** |
| Vendor decompile | `.decompile-scratch/proj/` (existing tree) |

Every `Plugin.cs:NNNN` citation below is that file, reproducible by re-running the same ilspycmd.

**This supersedes `eor-0760-content-audit.md` and `eor-0762-delta-audit.md` where they disagree.**

---

## 0. The one-sentence summary

EOR 0.66 extends FTK2 by **adding data to config dictionaries the game already replicates**, and by
**post-fixing vanilla resolution functions with pure, replicated-input arithmetic**. It creates almost
nothing at runtime; where it must, it hands the vendor a **deterministic UniqueID instead of letting
`Guid.NewGuid()` run**. Its RNG is never a private stream in online play — every `GameRandom` it
constructs passes `pIgnoreMultiplayerStaticSeed: false`, so the vendor **throws the caller seed away
and substitutes the shared multiplayer seed**. It patches **zero** AI/targeting code. And where a
feature could not be made to agree, it is **switched off in multiplayer by name**.

---

## RULES

### RULE 1 — Prefer DATA into a replicated config dictionary over runtime behaviour. Always ask "can this be a JSON row?" first.

**Evidence.** EOR ships **3.42 MB of JSON against a 767 KB DLL** — a 4.5:1 data-to-code ratio by bytes:

| File | Bytes |
|---|---|
| `FTK2_EnhancedPets/Characters.json` | 1,530,579 |
| `EnhancedOverhaulRevamped/CustomItems/Things/EORR_CustomItems.json` | 591,714 |
| `FTK2_EnhancedPets/Abilities.json` | 562,594 |
| `FTK2_EnhancedMercenaries/Followers.json` | 148,515 |
| `FTK2_EnhancedPets/Followers.json` | 94,824 |
| Localization (en/zh-Hans/pl/ru) | 422,219 |
| `StarterWeapons.json` + `VisualFallbacks.json` + rest | ~72,000 |

The merge is a reflective field write straight into the live config object —
`TryMergeConfigDictionaryFromJson` (`Plugin.cs:30053`) resolves `Env.Configs.<field>` by name and
writes rows in. Targets: `Abilities`, `Characters`, `Followers` (`Plugin.cs:29617-29627`).

**What it means for us.** `FTK2.Summoner` (a `Characters.json`/`Followers.json` pack loader,
adds-only, `SMN_` prefix) and `ClassForge`'s `MergePlanner` are already the right shape. Before
writing a new Harmony patch, prove the effect cannot be expressed as a config row that vanilla
already consumes. A row costs zero determinism risk; a patch costs an audit.

---

### RULE 2 — Merges are ADDS-ONLY. Never overwrite a key that already exists.

**Evidence.** EOR's own pet/merc merge is invoked with `allowUpdates: false`
(`Plugin.cs:29617`, `:29622`, `:29627`). Inside `TryMergeConfigDictionaryFromJson`
(`Plugin.cs:30053`) the guard is literally `if ((!flag || allowUpdates) && ...)` where
`flag = DictionaryContainsKey(value, key)` — an existing key is skipped, and the counts are logged
separately as `+added, ~updated`.

**What it means for us.** This is already our discipline (`ClassForge.Core/MergePlanner.cs`,
`Summoner.Core/Merge/MergePlanner.cs` with its `^SMN_[A-Z0-9_]+$` id shape). Keep it absolute.
Overwriting a vanilla key means peer A (mod installed) and peer B (mod broken/absent) resolve the
same id to different content — the worst class of divergence, because it is invisible in every log.

---

### RULE 3 — Never construct a `GameRandom` and expect your seed to survive online. Know which flag you passed and why.

**Evidence — vendor.** `.decompile-scratch/proj/GameRandom.cs`:

```csharp
public GameRandom(int _seed, bool pIgnoreMultiplayerStaticSeed = false)
{
    if (NetworkDebuggingHelper.NetworkData.PlayingOnlineMultiplayer && !pIgnoreMultiplayerStaticSeed)
        Seed = NetworkDebuggingHelper.MultiplayerSeed;   // caller seed DISCARDED
    else
        Seed = _seed;
    random = new System.Random(Seed);
}
```

**Evidence — EOR.** There are exactly **nine** `new GameRandom(...)` sites in 0.66 and
**all nine pass `false`**: `Plugin.cs:8336, 12796, 15393, 18709, 21073, 21340, 26538, 27289, 27550`.
EOR never once opts out of the static-seed override.

**Consequence, stated plainly.** In online multiplayer every EOR-derived stream collapses to the
*same* shared `MultiplayerSeed`. That is not "per-context variety" — it is "every peer builds an
identical `System.Random`". EOR is desync-safe here **by construction, not by seed hygiene**: the
contents of its seed strings become irrelevant the moment the session is online.

**What it means for us.** `LootGrantPatches.cs:97` constructs
`new GameRandom(grantSeed, pIgnoreMultiplayerStaticSeed: true)`. That is a **deliberate step OUTSIDE
EOR's envelope** and it is the more demanding of the two patterns: with `true`, the seed *is* live in
MP, so *every* input to `grantSeed` must be replicated or every peer rolls different loot. It is
allowed, it is documented in `AGENT-BRIEF §8`, and it is currently correct — but any new site must
justify `true` in writing, or pass `false` and inherit EOR's guarantee for free.

---

### RULE 4 — Never seed a private `GameRandom`, and never seed a draw-free hash, from an `Entity.Guid`.

**EOR 0.66 still does this. It was NOT fixed. Do not copy it.**

**Evidence — GUIDs are per-peer.** `.decompile-scratch/proj/Entity.cs:123` —
`Entity.Create()` sets `Guid = System.Guid.NewGuid().ToString()`.

**Evidence — EOR still feeds them in.** `CreateDeterministicClassSkillRandom` (`Plugin.cs:8291`)
builds a 17-element seed array in which `obj[6] = origin.Guid` and `obj[7] = target.Guid`
(`Plugin.cs:8323-8326`). `Plugin.cs:13972` seeds a quest-board regenerate from
`encounterEntity.Guid`; `Plugin.cs:26523` seeds an encounter-modifier effect from `enemy.Guid`;
`GetMarketCustomSeed` (`Plugin.cs:21368`) embeds `encounterEntity.Guid`.

**Why EOR gets away with it:** every one of those feeds a `new GameRandom(..., false)` whose seed is
thrown away online (RULE 3). The GUID contamination is **inert in multiplayer and only supplies
singleplayer variety**.

**Where EOR does NOT get away with it — the live bug.** `ShouldShieldbearerMitigate`
(`Plugin.cs:28596`) is a **draw-free** FNV-1a verdict:

```csharp
string text = $"{target?.Guid}|{TotalRounds}|{health}|{finalDamage}|SHIELDBEARER";
uint num2 = 2166136261u;
for (...) { num2 ^= text[i]; num2 *= 16777619; }
return num2 % 100 < 20;
```

There is no `GameRandom` here to launder the GUID. Two peers hash different strings and reach
different verdicts for the same hit — one player's Shieldbearer mitigates 25% damage and the other's
does not. Because it takes **zero draws**, the vendor's `GameRandom NextInt` desync probe cannot see
it; and because `CombatState` is `[JsonIgnore]` on `GameRunData`
(`.decompile-scratch/proj/GameRunData.cs:54-55`), the vendor's state MD5 cannot see it either.

**What it means for us.** Our `STATE_HASH_CHANCE` was ported from this exact EOR function and
**inherited the bug**; it was fixed on 2026-08-26 —
`ClassForge.Recipes/Runtime/StateHashChance.cs` now resolves `SELF_GUID` /
`TRIGGER_SOURCE_GUID` / `TRIGGER_TARGET_GUID` to `PeerOrder.KeyOf(...)`, the roster ordinal
(`"E<ordinal>"`), not `Entity.Guid`. **Keep it that way.** "EOR does X and its MP works" is not a
defence for X here: EOR's MP works *despite* this, invisibly, in a place no detector looks.

---

### RULE 5 — `Entity.Guid` is a same-peer round trip ONLY.

**Evidence.** EOR uses GUIDs freely as dictionary keys it both writes and reads on the same peer —
`RiskyBlessingOffersByEncounterGuid`, `TownSpecialistsByTownGuid`,
`ConsumedForbiddenSpoilsEncounterGuids`, `SteadyAimUsedThisCombat.Remove(__0.Guid)`
(`Plugin.cs:26648`), `_activeCombatEnemyGuid` (`Plugin.cs:1424`), and it compares against
`Env.VenueResults.FleePlayerGUIDs` (`Plugin.cs:26732`) — all local-lifetime lookups. It **never**
sends a raw GUID as a cross-peer identity claim except in the town-snapshot payload, where the GUID
is the *addressing* of an encounter both peers generated deterministically, not a seed.

**What it means for us.** A GUID may key a dictionary this peer writes and reads. It may never be a
seed input, an ordering key, a hash input, or a persisted cross-peer key. The cross-peer key is the
roster **Ordinal** — see `ClassForge.Core/Rng/EntityKey.cs` for why `ConfigName`, `GroupIndex` and
`(Y,X)` were all rejected.

---

### RULE 6 — If you create an entity, mint its GUID deterministically. Never let `Guid.NewGuid()` run for content both peers must agree on.

**Evidence.** Every map entity EOR creates gets a content-derived id.
`BuildDeterministicEncounterUniqueId(prefix, encounterToken, mapKey, coordinate)`
(`Plugin.cs`, defined immediately above `SanitizeUniqueIdToken`) returns
`"{prefix}{TOKEN}_{MAPKEY}_{x}_{y}"`, which is assigned to `EncounterGenData.UniqueID` and handed to
`MapGenHelper.CreateEncounterEntity` (`Plugin.cs:19740`, `:27361`, `:27592`). The vendor honours it:
`Entity.Create(string UniqueName)` sets `Guid = UniqueName` when non-empty
(`.decompile-scratch/proj/Entity.cs:107-117`) and only falls through to `Guid.NewGuid()` otherwise.
Constant prefixes: `EOR_WORLD_EVENT_ENTITY_` (`Plugin.cs:6404`), `EOR_DEBUG_SANCTUM_`.
Items get the same treatment: `AssignDeterministicThingId` sets
`thing.Id = "EOR_" + ComputeStableHash("EOR_THING_ID|" + parts)` (`Plugin.cs:20459`).

EOR then uses those ids as **idempotency keys** — `TryInjectWorldEvents` (`Plugin.cs:27256`) scans
`GameRun.Entities` for an existing `EOR_WORLD_EVENT_ENTITY_*` carrying this map's token and bails if
found, so a reload cannot double-inject.

**What it means for us.** Our `SUMMON` path calls
`CombatHelper.ApplyAction(eCombatActions.ADD_CHARACTER, ...)`
(`RecipeActionExecutor.cs:616`), whose `ADD_CHARACTER` case calls `TryCreateSummon` with
`pGuid = null` → `CreateCharacterEntity(..., pGuid: null, ...)` → `Guid.NewGuid()`.
**Our summons therefore carry per-peer GUIDs.** That is tolerable only while nothing derives a
cross-peer value from them. `FindNewSummon(before)` is a roster diff (positional, fine) and
`TrainerPartnerPersistence.RegisterSummon` is a same-peer binding (fine, RULE 5) — but the moment a
summon's identity has to cross the wire or seed anything, it must be minted deterministically.

---

### RULE 7 — Postfix vanilla resolution with PURE arithmetic over replicated inputs. That is the whole runtime pattern.

**Evidence.** 85 `harmony.Patch` sites / 98 patch methods (`Plugin.cs:11493-11833`). Rough shape:

| Class | Count | Examples |
|---|---|---|
| UI / view / localization / asset (no gameplay state) | ~50 | `CharacterCustomizationViewHelper_*`, `QuestBoardMenuViewHelper_*`, `Lang_t_Postfix`, `AssetLoader_*`, `UIToolkitHelper_RenderListView_*`, `VenueCameraController_CombatView_Postfix` |
| Combat-state mutating | ~14 | `CombatHelper_SetInitiative_Postfix`, `CombatHelper_PerformAbility_Prefix/Postfix`, `CombatHelper_ApplyAction_Postfix`, `CombatHelper_TickActiveEntityCharacterStatus_Postfix`, `InteractableHelper_ApplyStatus_*`, `InteractableHelper_ApplyStatChange_*`, `InteractableHelper_CalculateFinalDamage_Postfix`, `CharacterHelper_AddHealth_Prefix` |
| Loot / market / quest / map generation | ~20 | `LootDropHelper_*`, `AdventureHelper_FillMarket_Postfix`, `QuestHelper_*`, `MapGenHelper_GenerateMapEncountersFromConfigs_Postfix`, `ProgressionHelper_ProcessXPLoot_*` |
| Network / lifecycle | 4 | `AdventureDirector_HandleNetworkAction_Prefix`, `GameRunData_Create_Postfix`, `AdventureDirector_Initialize_Prefix`, `RouterMono_*` |

The state-mutating ones are almost all *pure* transforms of `ref` parameters:

- `CharacterHelper_AddHealth_Prefix` (`Plugin.cs:28449`) — `__1 += ceil(__1 * 0.25f)` when the
  character has `TRAIT_FIELDMEDIC`. Zero draws, zero lookups outside replicated state.
- `LootDropHelper_CreateMercs_Prefix` (`Plugin.cs:20600`) — `__0 *= 5`, `*= 2` again under a mutator.
- `CharacterHelper_GetEnemiesForCombat_EnemySet_Prefix` (`Plugin.cs:20708`) — `__3++`, then two
  bounded `ref int` level bumps.
- `LootDropHelper_CreatePets_Postfix` (`Plugin.cs:20643`) — dedup by config key, order-stable
  `RemoveAt` sweep, no randomness.

**What it means for us.** Our recipe engine hooks the same seam
(`Recipes/CombatHookPatches.cs` → `RecipeActionExecutor` → `CombatHelper.ApplyAction`). Keep every
effect a pure function of replicated state. Anything that reads process-local, wall-clock, machine,
config-file-outside-the-parity-hash, or UI state is a fork.

---

### RULE 8 — Never make the SHARED stream's draw COUNT a function of anything unreplicated.

**Evidence — EOR spends shared draws, and gates them on replicated state only.**
`LootDropHelper_GetLootDropsFromEnemies_Postfix` (`Plugin.cs:19458`) takes a **variable** number of
draws from the vendor's own `pGameRandom` (`__4`): `__4.NextChance(0.25m)` per `TRAIT_SCAVENGER`
party member, then a nested `NextChance(0.5m)` and `NextInt(8,20,true)`. The draw count varies — but
only with the party roster and selected traits, both replicated, and it iterates `__0` (the party
list) in its given order.

`NemesisManager.TryApplyToEncounterEnemy` (`Plugin.cs:1363`) is the canonical gate: a
`_spawnDecisionResolved` latch, **reset by reference-identity change of `combatState.Random`**
(`Plugin.cs:1369-1376`), ensures the `combatState.Random.NextChance(0.1m)` at `Plugin.cs:1400` runs
**at most once per combat**, and only after `IsValidEncounter` and persisted `NemesisData` checks —
all replicated.

**What it means for us.** `AGENT-BRIEF §8`'s two sanctioned patterns stand. The failure mode is
**draw-count divergence, not value divergence**. Watch specifically for:
- a chance `>= 100` costing zero draws while `< 100` costs one (vendor precedent at
  `ScourgeHelper.cs:82`, which gates on `>= 1m`, not `100`);
- `ProcChance` / `AiProcChance` drifting apart — an asymmetric pair is a draw-count fork keyed on a
  per-unit property.
`AiDrawNeutrality` exists precisely to hold this line for the targeting path.

---

### RULE 9 — Draw-free deterministic selection is a first-class MP-safe technique. Use it when you only need variety.

**Evidence.** `ApplyQuestArchetypes` (`Plugin.cs:17976`) branches on session type:

```csharp
int num2 = IsOnlineMultiplayerSession()
    ? (ComputeDeterministicSeed(string.Join("|", "EOR_MP_QUEST_ARCHETYPE", legendary?"L":"N",
        i, data.QuestType, data.SideQuestType, data.ID, data.BannerDisplayName)) % list2.Count)
    : random.NextInt(0, list2.Count - 1, true);
```

Online: a hash-mod pick, **zero draws**, all inputs replicated authored data. Offline: a real roll.

**What it means for us.** This is exactly `STATE_HASH_CHANCE`. Two caveats the spec already carries:
its verdict is **correlated across re-evaluation with identical inputs**, so include a
per-evaluation-varying replicated input (`TRIGGER_DAMAGE`, `COMBAT_ROUND`); and its input token set
is **closed** — adding a token is a spec change that must argue that token's replication.

---

### RULE 10 — When a feature cannot be made to agree, TURN IT OFF IN MULTIPLAYER BY NAME. Do not ship it hoping.

**Evidence — EOR's negative space is explicit and self-documenting in its own log strings:**

- **Town specialists — off.** `TryGetTownSpecialistOffers` (`Plugin.cs:14792`) does
  `TownSpecialistsByTownGuid.Remove(townGuid)` and logs
  *"Town specialists are disabled in online multiplayer to avoid desync risk."* Three call sites log
  the same string (`Plugin.cs:14715`, `:14801`, `:16624`).
- **Custom quest board / legendary contracts / quest UI overlays — off.**
  `ShouldDisableCustomQuestBoardRuntime` (`Plugin.cs:17697-17706`) ends in
  `return IsOnlineMultiplayerSession();` and logs *"Multiplayer safety: custom quest board
  generation, legendary contracts, and quest UI overlays are disabled; using vanilla quest boards."*
- **Quest pruning — off.** `Plugin.cs:13612` returns early when online.
- **Debug mutation actions — off by default.** `ShouldAllowDebugMutationAction` (`Plugin.cs:24272`)
  blocks unless `AllowOnlineMutationActions` is explicitly enabled; the config description is
  *"Allow destructive/mutating debug actions while in online multiplayer. Keep OFF to reduce desync
  risk."* (`Plugin.cs:9425`).
- **Market mutation — degraded, not mutated.** `Plugin.cs:21488`:
  *"Market audit ... No mutation applied in multiplayer."*

**What it means for us.** `ClassForge.Plugin/NetworkSessionState.IsOnlineMultiplayer()` already
exists and **fails closed** (any reflective resolution failure returns `true` = assume online). Any
feature we cannot prove agrees gets gated on it. Shipping an ungated maybe-safe feature is the one
move EOR never makes.

---

### RULE 11 — Where a feature MUST vary per session and cannot be derived, make the HOST authoritative and send it.

**Evidence.** EOR runs a small host-authoritative transport on top of the vendor's action pipeline:

- It prefixes `AdventureDirector._handleNetworkAction` (`Plugin.cs:10784` discovery,
  `:11621` patch, `:12913` handler) and dispatches three custom payload types:
  `HandleSyncedSanctumPilgrimNetworkActionAsync`, `HandleSyncedTownSnapshotRequestNetworkActionAsync`,
  `HandleSyncedTownSnapshotNetworkActionAsync` (`Plugin.cs:12919-12929`).
- It sends via the vendor's own `AdventureDirector._trySendNetworkAction`
  (resolved at `Plugin.cs:15137`, `:15473`) — **it does not open its own socket.**
- Payloads are versioned strings: `"EOR_SYNC_TOWN_SNAPSHOT_REQUEST_V1|<guid>|<guid>|<hash>"`
  (`Plugin.cs:15241`).
- The protocol: *"Multiplayer town snapshot sync active: host opens build local state; non-host town
  opens request host-authored state."* (`Plugin.cs:15209`). Host-only guards at `Plugin.cs:13868`,
  `:15377`, `:10007`.
- Config that would otherwise diverge is pulled from the host, not assumed:
  `TrySynchronizeCampaignMutatorsFromHost` (called from `GameRunData_Create_Postfix`
  `Plugin.cs:12815` and from the sync guard at `Plugin.cs:9638`).

**What it means for us.** `Recipes/LootGrantTransportBridge.cs` is our analogue. Rules to inherit:
ride the vendor's action pipeline (never a side channel); version every payload id; and gate the
producing side on `IsLocalOnlineHost()`-equivalent so two peers never both author.

---

### RULE 12 — Mod parity is a FINGERPRINT you publish and compare — and it is DETECT-ONLY. Do not rely on the README.

**Evidence.** `README_INSTALL.txt` says *"For multiplayer, every player must install the exact same
release archive."* That instruction is backed by real code, but the code **only warns**:

- `BuildLocalSyncSnapshot` (`Plugin.cs:9715`) composes four hashes and a signature:
  `ComputeStableHash("0.7.0.66|" + configHash + "|" + dataHash + "|" + systemsHash + "|" + defsHash)`.
- `BuildConfigFingerprintSource` — 11 gameplay-affecting config knobs (`EnableQuestBoardOverhaul`,
  `EnableMarketOverhaul`, `EnableAffixSystem`, the four affix chances, the four affix-allow flags).
- `BuildMajorDataFilesFingerprintSource` — `ComputeStableHash(File.ReadAllBytes(...))` over six
  files: EnhancedPets `Abilities/Characters/Followers/ServerNames.json` and EnhancedMercenaries
  `Followers/ServerNames.json`. Missing dir/file/read-error each hash to a distinct sentinel
  (`<missing-dir>`, `<missing-file>`, `<err:Type>`) — `AppendMajorDataFileFingerprint`.
- `BuildEnabledSystemsFingerprintSource` — 13 system toggles plus `ActiveCampaignMutators`.
- `BuildDefinitionsFingerprintSource` — every class key, extra class key, trait key, and every
  affix / risky-blessing / nemesis-trait definition with its tier, weight and flags.
- Publish: `connection.UpdateOwnPlayerProperties(...)` with keys `EOR_SIG` / `EOR_MUT`
  (`Plugin.cs:6460`, `:9990`). Compare: read the host's snapshot, then every peer's if local is host
  (`Plugin.cs:9649-9684`).
- On mismatch: `LogMultiplayerSyncMismatch` → **`log.LogWarning`, nothing more**
  (`Plugin.cs:10200`). `BuildMismatchDetails` (`Plugin.cs:10158`) names which of the five dimensions
  drifted: *"Mod version, Config values, Major data files, Enabled/disabled systems,
  Class/pet/trait definitions"*. No kick, no block, no SafeMode.

**What it means for us.** We already go further: `ParityBridge.cs` implements three
`OnParityMismatch` policies (`WarnOnly` / `WarnAndSafeMode` / `Block`) over
`FTK2Mods.DevKit.ParityService`, and `FTK2.Summoner` sets `Block`. **Keep policies stricter than
EOR's.** Also copy two details EOR got right: hash the **file bytes**, not a parsed summary; and give
missing/unreadable files a **distinct sentinel** so "absent" never hashes equal to "present".

---

### RULE 13 — Config knobs that change gameplay MUST be in the parity hash. Cosmetic ones must not be.

**Evidence.** EOR's config fingerprint contains only gameplay-affecting knobs. Localization is
explicitly excluded and says so in `README_TRANSLATORS.txt`, embedded verbatim at `Plugin.cs:2486`:
*"Text only; localization files do not affect gameplay state or multiplayer sync."*

**What it means for us.** `ClassForge.Core/ParityKnobRegistry.cs` is the list. Every new knob gets
classified at the moment it is added. A gameplay knob outside the hash is a silent divergence; a
cosmetic knob inside it is a false alarm that trains people to ignore real ones.

---

### RULE 14 — Do not patch AI or targeting. EOR does not, at all.

**Evidence.** Exhaustive grep of `Plugin.cs` for `AIHelper`, `ForceAiDecision`, `GetPreferredTarget`,
`AIComponent`, `eAiTendenc`, `FocusFire`: **zero matches.** Not one of EOR's 98 patch methods touches
the AI, targeting, or decision layer.

**What it means for us.** AI targeting is **entirely outside EOR's proven envelope.** We have no
upstream precedent to lean on. What we do have is our own model: `AiDrawNeutrality` holds one
`ForceAiDecision` call that runs the targeting path to **exactly one shared-stream draw** for target
preference, regardless of tendency and regardless of whether a Focus Fire order skipped
`GetPreferredTarget`; the model and its exhaustive tests are `ClassForge.Core/Rng/AiTargetingDraws.cs`.
That model, not EOR, is the only thing standing between us and a desync here. Treat any change to it
as a determinism change requiring the §6 self-check.

---

### RULE 15 — Never touch tile aura state. EOR has no tile precedent whatsoever.

**Evidence.** Grep of `Plugin.cs` for `VenueTileComponent`, `AuraStatuses`, `TilePosition`,
`SetTilePosition`: **zero matches.**

**What it means for us.** Chaos Mage hazard tiles and any tile decal/aura manipulation are ours
alone. Minimum safe pattern: tile selection must be a deterministic function of replicated inputs
(use `ClassForge.Recipes/Abstractions/TileOrder.cs`'s fixed order, never a dictionary/LINQ order),
and tile writes must go through `CombatHelper.ApplyAction` so the vendor's own TileSync path runs —
do not write `VenueTileComponent` fields directly. Verify per `VERIFICATION-METHOD §3`
(`combat.tiles[]` in state-v2 is the log half; the decal is the screenshot half).

---

### RULE 16 — Combat entities created mid-fight live OUTSIDE the vendor's desync detector. Persisted party state lives INSIDE it. Prefer the latter.

**Evidence.** `.decompile-scratch/proj/GameRunData.cs`:

```csharp
[JsonIgnore] public CombatState CombatState;                              // line 54-55
public Dictionary<string, FollowerState> PlayerFollowers;                 // line 58
```

`CombatState` is excluded from the vendor's serialized state — so it is **not in the desync MD5**.
`PlayerFollowers` is included.

**What it means for us.** A COMPANION follower is *persisted, hashed, vendor-replicated party state*.
A mid-combat summon is *unhashed, unreplicated-by-the-detector combat state*. That asymmetry is the
single strongest argument in this document, and it is why RULE 17 exists.

---

### RULE 17 — EOR's "pets" are FOLLOWERS, not summons. Confirmed. That is the recommended shape for persistent creatures.

**Evidence — the data.** `FTK2_EnhancedPets/Followers.json` holds 149 entries:

| `Type` | Count |
|---|---|
| `COMPANION` | 119 |
| `MERCENARY` | 20 |
| `INANIMATE` | 8 |
| `CURSE` | 2 |

Tag histogram: `COMPANION` ×119, **`PETSHOP` ×115**, `MERCENARY` ×20, `INANIMATE` ×8, `CURSE` ×2.
`FTK2_EnhancedMercenaries/Followers.json` is the same 119 companions plus 120 mercenaries (249 total).
A representative row:

```json
{ "Type": "COMPANION", "ConfigName": "COMPANION_WOLF_BASIC_00", "ClassName": "COMPANION_WOLF_01",
  "ContractRounds": 0, "MinTier": 0, "MaxTier": 3, "Rarity": "COMMON",
  "ContractPrice": "AVERAGE", "Behaviour": "DEFAULT", "Tags": ["COMPANION","PETSHOP"] }
```

`ContractRounds: 0` = permanent, not a timed contract.

**Evidence — the code path.** EOR **never creates one of these itself.** Its three touches are all
parameter tweaks on the vendor's own generators:

- `LootDropHelper_CreatePets_Prefix` (`Plugin.cs:20623`) — `__0 *= 2; __1++;` under the Wildlands
  mutator. It changes *how many* the vendor makes.
- `LootDropHelper_CreatePets_Postfix` (`Plugin.cs:20643`) — dedups the vendor's returned list.
- `FollowerHelper_JoinFollower_Prefix` (`Plugin.cs:20848`) — `pRoundsToExpire = -1` for
  mercenary-likes. It changes *how long* a vendor-created follower lasts.

**Evidence — the vanilla asymmetry the operator flagged.** `.decompile-scratch/proj/CombatHelper.cs`,
end of `TryCreateSummon`:

```csharp
characterComponent.GroupIndex = pGroupIndex;
if (characterComponent.CharacterType != eCharacterTypes.COMPANION)
    CharacterHelper.ActorAddProperty(eActorProperties.SUMMON, pSummonEntity);
```

**Confirmed.** A COMPANION reaching combat is not flagged `SUMMON` — it is a party member on the
board, replicated by the party system, persisted in `PlayerFollowers`.

**What it means for us — the recommendation.** Our Trainer partners should be expressible as
**COMPANION followers with a `PETSHOP`-style acquisition**, not as mid-combat `ADD_CHARACTER`
summons, wherever the partner is meant to be *persistent*. `FTK2.Summoner` already has the loader for
exactly this shape (`Characters.json` + `Followers.json`, adds-only, `SMN_` prefix), and
`TrainerPartnerPersistence` already wants partner state to survive a fight. Expressed as followers,
partners inherit RULE 16's guarantee by construction: they are in `PlayerFollowers`, inside the
vendor's hash, replicated by machinery that predates us. Expressed as summons, every fight re-creates
them outside the detector and we own the correctness ourselves.

*This is a recommendation with a caveat, not a verified migration.* See "Unverified" §U1: whether a
COMPANION follower can be **recruited mid-combat** (as capture requires) is untested, and the
follower path's own draw profile has not been measured.

---

### RULE 18 — Runtime injection must be IDEMPOTENT and keyed on replicated identity.

**Evidence.** `TryInjectWorldEvents` (`Plugin.cs:27256`) does three things before creating anything:
scans `GameRun.Entities` for a persisted `EOR_WORLD_EVENT_ENTITY_*` bearing this map's sanitized
token; consults an in-memory `WorldEventInjectedMapKeys` set; and derives its stream from
`string.Join("|", "EOR_WORLD_EVENTS", GameRun.MapGenSeed, ConfigName, ActiveMapID,
playerStartPosition.x, playerStartPosition.y)` — **every input replicated, no GUIDs.** Same shape at
`Plugin.cs:27550` for debug sanctums.

**What it means for us.** Any grant, spawn or injection needs a replicated idempotency key so a
reload, a rejoin, or a double-fired trigger cannot double-apply on one peer and not the other. Our
`PendingGrantStore` / `LootGrantKey` pair is this; keep the key derived from roster ordinals and the
combat seed, never from GUIDs.

---

### RULE 19 — Fail SOFT and fail SILENT-BUT-LOGGED, identically on every peer.

**Evidence.** Effectively every EOR patch body is wrapped `try { ... } catch (Exception ex) {
_log?.LogError(...) }` and returns. `CombatPhase_OnTryFocus_Prefix` (`Plugin.cs:23248`) returns
`true` (run original) on *every* path including its catch, and logs
*"Combat focus guard failed open to vanilla focus handling."* `TryResolveGameplayRandom`
(`Plugin.cs:12746`) refuses rather than substitutes:
*"no deterministic GameRandom source was available. Skipping this random roll to avoid multiplayer
desync."* `ApplyAffixesToGeneratedItems` (`Plugin.cs:19898`) does the same.

**Caution — the trap in this rule.** A `try/catch` around a **draw** is a draw-count fork if the
exception is peer-dependent. EOR's catches are around *whole features*, and its refusals
(`TryResolveGameplayRandom` returning false) are decided **before** any draw is taken.

**What it means for us.** `[ClassForge] Recipe effect failed (skipped, rest of plan continues)` is a
**PASS for fail-safety and a FAIL for correctness** — report the two separately
(`VERIFICATION-METHOD §5`). And audit every catch that could straddle a roll.

---

### RULE 20 — Sanitize your own custom state at combat entry. Assume a stale save carries junk.

**Evidence.** `StripUnsafeSelectableTraitStatuses` (`Plugin.cs:27839`) runs from
`CombatHelper_SetInitiative_Postfix` (`Plugin.cs:26635`) — i.e. on **every** combatant at combat
start, before any of EOR's own logic and before the MP guard — and removes three specific custom
statuses: `STATUS_EOR_BATTLE_RHYTHM`, `STATUS_EOR_MOMENTUM`, `STATUS_EOR_DRUNKEN_COURAGE`.
`SanitizeMarketBoardRuntimeState` (`Plugin.cs:21522`) does the equivalent for market items.

**What it means for us.** Custom statuses persisted into a save can survive into a session where the
other peer's mod state differs. A deny-list sweep at combat entry costs nothing and closes a whole
category. Note the ordering EOR chose: the sweep is *unconditional*, above the MP switch.

---

### RULE 21 — Ship a determinism diagnostic. You will need it and you cannot add it retroactively.

**Evidence.** EOR's `DiagnosticReporter` (`Plugin.cs:2597`) tails the log for 20 sync-relevant tokens
(`Plugin.cs:3084`): `NetworkHelper`, `DESYNC`, `DESYNC_NOTIFICATION`, `NETWORK DESYNC`,
`GameRandom NextInt`, `GameState Hash`, `TryGetNextGameAction`, `TryPlayNetworkServerAction`,
`GetThingFromThingData`, `Mismatch config name`, `CombatPhase._performAbility`,
`GameActionIndexOfDesync`, `ActionType`, `Now playing network game action`,
`PhotonConnectionAdapter.OnEvent`, `orderedGameActionMessageCount`, `WaitForPlayersAction`,
`AdventureAction`, `CombatAction`, `DialogueAction`. It emits a
*"Desync Focus Runtime Snapshot"* section (`Plugin.cs:3771`) alongside the local sync snapshot
(`Plugin.cs:4165`) and peer snapshots (`Plugin.cs:4152`), and scrubs local user paths from the report
via three regexes.

**What it means for us.** See `mp-tripwire-and-dev-harness-plan.md`. Our single-machine substitute is
`VERIFICATION-METHOD §6`: run the same fixture twice with a genuinely pinned seed and diff the
`ftk2_state` digest turn-by-turn plus the `GameRandom` draw sequence under `LogCalls(true)`. Any
divergence between two runs of the same input **is** the desync class. Run it as a gate after every
determinism-touching change — it is cheap, exact, and unlike a screenshot it cannot be misread.

Two decompile-verified gotchas that make or break this: `GameRandom.Seed` is `readonly` and never
read after construction, so writing it changes a label and **zero** future draws (fixed 2026-08-26 —
`crucible_pin_seed` now replaces the private `random` field); and `_nextCount` only increments while
`LogCalls(true)` is on, so `NextCount` is not a draw counter otherwise.

---

## Where our mod is OUTSIDE EOR's proven envelope

Ranked by exposure. "Precedent" means EOR 0.66 does something structurally equivalent in a shipping,
MP-exercised path.

### A. Mid-combat summons (Trainer partners) — **NO PRECEDENT**

EOR creates **no combat entity at runtime**. Its only entity creations are overworld encounters
during map generation (`MapGenHelper.CreateEncounterEntity`, RULE 6), and even those get
deterministic UniqueIDs. Its creatures are vanilla COMPANION followers recruited through the vanilla
pet shop (RULE 17). Our `ADD_CHARACTER` path (`RecipeActionExecutor.cs:535-628`) is genuinely novel.

**Minimum safe pattern if we keep summons:**
1. **Preferred: express persistent partners as COMPANION followers instead** (RULE 17), so the party
   system replicates them and `PlayerFollowers` hashes them (RULE 16).
2. If a true mid-combat summon must remain: go through `CombatHelper.ApplyAction(ADD_CHARACTER)` —
   never construct the entity ourselves — as we already do.
3. Creature id, tile choice and count must all be pure functions of replicated state. Placement
   currently costs draws inside vanilla; those draws must be identical on both peers, which means the
   *inputs* to placement (free-tile set, group index, row preference) must be identical — assert this.
4. Nothing downstream may derive a cross-peer value from the summon's `Entity.Guid` (RULE 5).
   `TrainerPartnerPersistence.RegisterSummon` is same-peer and therefore fine; a wire payload would
   not be.
5. Gate on `NetworkSessionState.IsOnlineMultiplayer()` until the two-run determinism diff (RULE 21)
   is green for a summon-heavy fixture.

### B. Capture (Trainer) — **NO PRECEDENT**

EOR has no mechanic that moves an entity from the enemy side to the ally side. Nearest neighbours are
*recruitment* (pet shop, `FollowerHelper.JoinFollower`) and *escape* (`NemesisManager`
flee handling), neither of which is capture.

**Minimum safe pattern:** the capture *decision* must be draw-free
(`STATE_HASH_CHANCE` with roster-ordinal inputs, RULE 4/9) or take exactly one shared draw gated on
replicated state (RULE 8). The captured creature's persistence should land in `PlayerFollowers`
(RULE 16) — i.e. capture should *produce a follower*, which is precisely the EOR-shaped answer and
another argument for RULE 17. Treat mid-combat follower recruitment as **unverified** (§U1).

### C. AI targeting commands — **NO PRECEDENT, HIGHEST RESIDUAL RISK**

Zero EOR patches touch AI (RULE 14). We are alone here. `AiDrawNeutrality` +
`ClassForge.Core/Rng/AiTargetingDraws.cs` are the whole safety story: one `ForceAiDecision` that runs
the targeting path costs exactly one shared-stream draw, whatever the tendency and whether or not a
Focus Fire order skipped `GetPreferredTarget`. **Minimum safe pattern:** never add a targeting path
that is not covered by the exhaustive draw-count tests; treat any edit to those tests as a
determinism change requiring the RULE 21 gate.

### D. Tile manipulation (Chaos Mage hazard tiles) — **NO PRECEDENT**

Zero EOR references to tile components (RULE 15). **Minimum safe pattern:** fixed `TileOrder`
iteration, writes through `ApplyAction` so vendor TileSync runs, no direct `VenueTileComponent`
mutation, and paired verification (`combat.tiles[].auraStatuses` for the log half, the decal for the
screenshot half).

### E. Loot grants — **PRECEDENT EXISTS, BUT WE USE A DIFFERENT RNG DISCIPLINE**

EOR *does* append items post-combat: `LootDropHelper_GetLootDropsFromEnemies_Postfix`
(`Plugin.cs:19458`) adds gold/herbs/affixed items, and mints deterministic `Thing.Id`s
(`AssignDeterministicThingId`, `Plugin.cs:20459`). So the *feature* is inside the envelope.

The *mechanism* is not. EOR draws from the **shared** `pGameRandom`, with draw count varying by
replicated party traits. We take **zero shared draws** and derive a private stream with
`pIgnoreMultiplayerStaticSeed: true` (`LootGrantPatches.cs:97`). Both can be correct; ours is the
stricter contract and the more fragile one, because `true` keeps our seed live online. **Minimum safe
pattern:** every input to `grantSeed`/`grantKey` must be replicated — roster ordinals
(`RosterKeysOf`), the combat seed read as a **field** (`pGameRandom.Seed`, zero draws), and the
pre-grant list digest. Never a GUID. Adopt EOR's deterministic item-id minting for anything whose id
crosses the wire.

### F. Class/trait/status content and stat modifiers — **STRONG PRECEDENT, LOWEST RISK**

EOR's whole trait system lives on this seam (`InteractableHelper_*`, `CharacterHelper_AddHealth_Prefix`,
`CombatHelper_PerformAbility_*`), and it runs **in multiplayer** — the two historical kill switches
`ShouldDisableVolatileCombatMutationsForMultiplayer` (`Plugin.cs:17739`) and
`ShouldDisablePreparedFocusForMultiplayer` (`Plugin.cs:17734`) are now hard `return false` in 0.66,
i.e. combat trait mutations are live online. Our `StatModifierEngine` / `RecipeDispatcher` for
non-summon, non-tile, non-AI effects sit squarely inside the proven envelope. Keep them there.

---

## Unverified — blunt list

**U1.** Whether a `COMPANION` follower can be recruited **mid-combat** (what capture needs) at all.
EOR only ever recruits at a pet shop, out of combat, via `LootDropHelper.CreatePets` →
`FollowerHelper.JoinFollower`. The vanilla `eSummonTypes.AS_FOLLOWER` branch of `TryCreateSummon`
does call `CreateFollowerCharacter` + `JoinFollower` mid-combat, so a path exists in vendor code —
**but it is unexercised by EOR and untested by us.** RULE 17's recommendation rests on this.

**U2.** The **draw profile of the follower path** vs the summon path. Not measured. Migrating
partners to followers changes which streams are consumed and how often; that is itself a determinism
change and needs the RULE 21 two-run diff before it can be called safer.

**U3.** Whether EOR's `AS_FOLLOWER`/pet content is ever actually **on the combat board** in the
operator's co-op saves, or only in the caravan/party UI. The COMPANION-not-SUMMON asymmetry in
`TryCreateSummon` implies board presence is intended, but this was not observed on screen.
Per `feedback_ftk2_ui_validation`, that means it is not proven.

**U4.** Whether `NetworkDebuggingHelper.MultiplayerSeed` is genuinely identical on every peer for the
whole session (RULE 3's entire guarantee depends on it). It is the vendor's documented mechanism and
EOR bets its whole RNG story on it, but it was not independently verified in this pass.

**U5.** Which of EOR's 98 patches are actually **exercised** in the operator's co-op sessions.
"Confirmed working in multiplayer" is an observation about one party's play, not proof that every
code path above agreed. In particular the Shieldbearer divergence (RULE 4) would be **invisible** to
that observation.

**U6.** Whether `_spawnDecisionResolved` and the other process-static latches in `NemesisManager`
survive a mid-combat save/reload identically on both peers. EOR resets them on
`CombatState.Random` reference change, which is a same-peer signal; a rejoin is not covered.

**U7.** No side-by-side two-machine test was run. Everything here is static analysis of a decompile
plus the operator's report that the build works.

**U8.** The exact behaviour of `Env.NetworkData.PlayingOnlineMultiplayer` during the
lobby→adventure transition, which is when EOR publishes its sync snapshot. A feature gated on
`IsOnlineMultiplayer()` that runs *before* the flag is set would be ungated in practice. Our
`NetworkSessionState` fails **closed** (assume online), which is the right side of that unknown.

---

## Checklist — hold a change to this before merging

- [ ] Could this be a JSON row instead of code? (R1)
- [ ] Merge is adds-only, prefixed id, no vanilla key overwritten. (R2)
- [ ] Every `new GameRandom` justifies its `pIgnoreMultiplayerStaticSeed` value in a comment. (R3)
- [ ] No `Entity.Guid` in any seed, hash, ordering key, or persisted cross-peer key. (R4, R5)
- [ ] Any entity created gets a deterministic id, not `Guid.NewGuid()`. (R6)
- [ ] Effect is a pure function of replicated state. (R7)
- [ ] Shared-stream draw COUNT does not depend on anything unreplicated. (R8)
- [ ] If it only needs variety, it is draw-free. (R9)
- [ ] If it cannot be proven to agree, it is gated on `NetworkSessionState.IsOnlineMultiplayer()`. (R10)
- [ ] Any cross-peer message rides the vendor action pipeline with a versioned payload id. (R11)
- [ ] New gameplay config knob is in `ParityKnobRegistry`; new cosmetic knob is not. (R12, R13)
- [ ] No AI/targeting change outside `AiTargetingDraws`'s covered model. (R14)
- [ ] No direct `VenueTileComponent` write. (R15)
- [ ] Persistent creature state lands in `PlayerFollowers`, not `CombatState`. (R16, R17)
- [ ] Injection is idempotent on a replicated key. (R18)
- [ ] No `try/catch` straddles a draw. (R19)
- [ ] Custom statuses swept at combat entry. (R20)
- [ ] Two-run same-seed determinism diff is green. (R21)


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
