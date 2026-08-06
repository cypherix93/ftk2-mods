# SPEC — Host-decided loot-grant sync verb (`CF_SYNC_LOOT_GRANT_V1`)

**Date:** 2026-08-05 · **Status:** design draft for review (implements the SPEC-DELTA §7.1 unlock) ·
**Owner engine:** FTK2.ClassForge (recipe vocabulary + grant engine) · **Transport host:** FTK2.DevKit
(ParityService transport, generalized) · **Applies to:** `FTK2.ClassForge/SPEC.md` §4.6/§7.1/§9/§11,
`FTK2.DevKit/SPEC.md` §3/§5/§6, `docs/research/game-patch-surface-notes.md` (PSN — gains a new section),
`docs/research/eor-rehost-coverage-matrix.md` §1/§2/§3.

**Grounding sources.** Live-DLL decompile produced for this spec:
`<scratchpad>/decomp/LootDropHelper.cs` + `<scratchpad>/decomp/GameRandom.cs` (ilspycmd 10.1.1 against
`E:\Games\Steam\steamapps\common\For The King II\For The King II_Data\Managed\FTK2.dll`; line numbers below
match the repo's standing decompile `tools/out/decompile/FTK2/*.cs` — cross-checked, e.g.
`GetLootDropsFromEnemies` is L1204 in both). Also: `tools/out/decompile/FTK2/CombatPhase.cs`,
`tools/out/decompile/FTK2/GameplayDirectorBase.cs`, EOR decompile
`tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs` (**EOR**), `docs/MULTIPLAYER.md`
(**R1–R5**), `docs/research/eor-0760-content-audit.md` (**AUD**), `FTK2.ClassForge/SPEC-DELTA-v1.1.md`
(**SPEC-DELTA**), `docs/research/eor-rehost-coverage-matrix.md` (**CM**),
`FTK2.DevKit/src/DevKit.Plugin/ParityTransport.cs` / `ParityPatches.cs` (**DK-src**).

**Binding constraints inherited:** MULTIPLAYER.md R1–R5; engine-charter rules 1–7
(`docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md`); SPEC-DELTA §5.2 determinism invariants.
House rule restated: anything below not verifiable in a cited decompile line is marked **[UNVERIFIED]** and
carried as an open question, not silently assumed.

---

## 0. What this verb is, and what it unlocks

One shared capability: **a deterministic, versioned, idempotent post-combat loot *delta*** — computed at a
single verified point in the vanilla loot pipeline, applied identically on every peer, and pushed by the
host as an authoritative `CF_SYNC_LOOT_GRANT_V1` record over DevKit's existing network transport.

It is the stated unlock (SPEC-DELTA §7.1; `FTK2.ClassForge/SPEC.md`:458-461) for six parked mechanics:

| # | Consumer | Parked at | Shape (see §6) |
|---|---|---|---|
| 1 | `TRAIT_SCAVENGER` loot half (25% → gold 8–20 **or** COMMON `HERB`) | CM §2 / SPEC-DELTA §7.1 | recipe: `ON_COMBAT_LOOT` + `PickOneEffect` |
| 2 | `TRAIT_TREASURE_SENSE` loot half (20% → gold 8–20) | CM §2 | recipe: `GOLD_GRANT` |
| 3 | `TRAIT_SCHOLARS_HABIT` loot half (20% → `SCROLL`) | CM §2 | recipe: `ITEM_TAG_GRANT` |
| 4 | `OF_SCAVENGING` affix loot half (10% → gold 5–10 or COMMON `HERB`) | CM §3 row 16 | same recipe family, affix grantor |
| 5 | Affix **drop-time rolling** (roll `_OF_X` pre-minted variants onto drops) | CM §3 row 22 | `REPLACE_ITEM` delta op (reserved, M-LG4) |
| 6 | Reward halves of future encounter-modifier / Nemesis systems (e.g. EOR `NEMESIS_WEALTHY` "+50% gold on defeat" L5177, Veteran L5180; encounter-modifier Gold/XP% L16643-16644) | AUD §6 row 9 (dropped systems; re-spec later) | `SCALE_STACK` / `ADD_ITEM` ops + Mode H (reserved) |

EOR's evidence numbers for consumers 1–4 are verbatim from EOR L16357-16419 (chances, ranges, tags). All
four are *additive, append-only* rewards; consumer 5 is a *replace* on a dropped item; consumer 6 is
*scaling* of currency/XP stacks plus additive items (EOR L1548-1573, L16636-16661). The delta-op vocabulary
in §3.2 is exactly this observed set — nothing speculative.

---

## 1. Verified game surface (PSN-style write-ups — to be merged into PSN at M-LG1)

### 1.1 `LootDropHelper.GetLootDropsFromEnemies` — the hook

```csharp
// <scratchpad>/decomp/LootDropHelper.cs L1204 (live DLL, 2026-08-05)
// == tools/out/decompile/FTK2/LootDropHelper.cs:1204 (unchanged since the 2026-07-25 decompile)
public static List<Thing> GetLootDropsFromEnemies(List<Entity> pParty, List<Entity> pEnemies,
    int pMaxMaterialTier, Env pEnv, GameRandom pGameRandom)
```

Semantics (L1206-1291): iterates `pEnemies`; `RewardEncounterComponent` holders yield configured reward
strings (L1212-1221); STANDARD/BOSS non-summons resolve `CharacterConfig.LootID` →
`Env.Configs.LootDrops` → `GetDrops(...)` (L1224-1233), with an optional `EXTRA_LOOT` world-modifier roll
from `pGameRandom` (L1234-1238). It then **aggregates currencies**: sums `XP` / `CURRENCY_LORE` /
`CURRENCY_ADVENTURE` stacks, strips them from the list (L1242-1260), applies world-modifier multipliers
(L1261-1262), divides XP by party size, and re-inserts them at **index 0** as `PARTY_XP`,
`CURRENCY_ADVENTURE`, `CURRENCY_LORE` `Thing`s (L1267-1278). Returns the list.

**Exactly one caller in the whole game assembly** (repo-wide grep):
`tools/out/decompile/FTK2/CombatPhase.cs:2578`, inside `_endCombatAsync`. There is no other path; hooking
this method postfix covers all enemy-combat loot and nothing else.

**EOR precedent at this same surface — the anti-pattern.** EOR L16339:
`LootDropHelper_GetLootDropsFromEnemies_Postfix(List<Entity> __0, List<Entity> __1, GameRandom __4, ref List<Thing> __result)`
mutates `__result` in place per peer, taking up to 8 extra `NextChance`/`NextInt` draws **per player per
combat from the shared `GameRandom` `__4`** (L16357-16419), stacked on further shared draws for
encounter-modifier, Nemesis, Scourge and affix rolls (L16347-16350, L16421), all behind an MP guard that is
hardcoded `return false` (AUD §3.1) and gated on per-process dictionaries that are not replicated
(AUD §3.6). This is the charter rule-2 + rule-3 violation the verb exists to replace (AUD §3.2, §3.3).

### 1.2 `CombatPhase._endCombatAsync` — the surrounding sequence

```csharp
// tools/out/decompile/FTK2/CombatPhase.cs:2343
private async Task _endCombatAsync(bool pIsImmediate = false)
```

The load-bearing sequence, with line anchors:

| Line | Event |
|---|---|
| :2488, :2503-2507 | `giveLootDrops` computed from `hasWon`; **suppressed** when a live tagged BOSS remains |
| :2570-2575 | **Branch A:** `isAllCombatsOver && VenueState.LootTableArg` non-empty → `LootDropHelper.GetDrops(...)` (scripted venue loot). `GetLootDropsFromEnemies` does **not** run |
| :2578 | **Branch B (the verb's path):** `lootDrop = LootDropHelper.GetLootDropsFromEnemies(alivePlayers, list2, currentProgressionData.ItemTier, _env, _combatState.Random)` |
| :2580-2587 | Steal-history items inserted at front |
| :2588-2591 | `_additionalDrops` (private per-combat list, CombatPhase.cs:90, reset :222) inserted at front |
| :2594 | `await Task.Delay(1500)` |
| :2617 | `await _distributeRewardsAsync(lootDrop, list3, ..., pIsCombat: true)` |
| :2666 | `int pRandomSeed = _combatState.Random.NextInt(0, 1000000);` — **vanilla draws from the shared combat stream immediately after loot** |

Two decompile findings that **correct the scouting brief's premises**:

1. **Loot generation is not host-only.** `_endCombatAsync` is part of the lockstep combat simulation and
   runs on **every peer**, each computing the identical `lootDrop` list from the shared
   `_combatState.Random` (the lockstep premise is confirmed by `FTK2.WarBrain/SPEC.md:715-720` via
   MULTIPLAYER.md, and by the vendor's own per-action desync detector,
   `GameAction.DesyncDetectionData {Hash, GameRandomNextInt}` — MULTIPLAYER.md "What we know", 2026-07-25
   correction). Clients already "roll the whole list locally" in vanilla; there is nothing for a pushed
   delta to be the *sole* source of.
2. **A host-only draw from `CombatState.Random` is impossible to do safely.** CombatPhase.cs:2666 draws
   from the shared stream right after the loot call on every peer; a host that took *any* extra draw in
   between desynchronizes that draw, and the vendor's desync monitor compares `GameRandomNextInt` across
   peers per action. The brief's option "rolling from the shared CombatState.Random ONLY on the host" is
   therefore **refuted by the decompile**; the verb uses the brief's other option — "deterministically from
   replicated state" (§4).

### 1.3 `GameplayDirectorBase._distributeRewardsAsync` — where list parity must already hold

```csharp
// tools/out/decompile/FTK2/GameplayDirectorBase.cs:701
protected async Task _distributeRewardsAsync(List<Thing> pItems, List<Entity> pPlayerEntities,
    string pLootDescription, QuestState pQuest = null, int pCurrentActivePlayerIndex = -1,
    List<(eAbilityResults, object)> pResults = null, bool pIsTarot = false, bool pIsCombat = false)
```

Order of operations: `LootDropHelper.ProcessLootDrops(pItems, ...)` (:712 — bumps a
`CURRENCY_ADVENTURE` stack up to ≥ party count, LootDropHelper.cs:1448-1457);
`ProgressionHelper.ProcessXPLoot(...)` (:714 — consumes and removes XP things); UI built and
`LootDistributionViewHelper.StartLootDrops(..., pItems, _onTakeLootItem, ...)` starts the interactive
distribution (:777); **only then** is the adventure network-action queue explicitly pumped:
`if (_env.NetworkData.PlayingOnlineMultiplayer && doTryPlayNetworkAction) _tryPlayNextNetworkAction();`
(:779-782). Takes flow through
`_onTakeLootItem(Entity pCharacter, List<Entity> pAllCharacters, Thing pTakeThing, eLootActions pAction, ...)`
(:815), which operates on the **concrete `Thing` instance from each peer's local list**.

Consequences: (a) the loot list must be **content-, order- and identity-identical on every peer no later
than :712**; (b) the explicit queue pump *after* `StartLootDrops` is direct evidence that a network payload
sent at loot-generation time is **not guaranteed to be handled before the client's list is consumed** —
the delivery-race that rules out verbatim-apply-from-wire as the v1 mechanism (§4.1, V-2).

### 1.4 `GameRandom` — the two API facts the RNG design stands on

```csharp
// <scratchpad>/decomp/GameRandom.cs (live DLL)
public readonly int Seed;                                          // L11
public int NextCount => _nextCount;                                // L23 — draws taken so far, public
public GameRandom(int _seed, bool pIgnoreMultiplayerStaticSeed = false)  // L45
//   L47-51: if (NetworkDebuggingHelper.NetworkData.PlayingOnlineMultiplayer && !pIgnoreMultiplayerStaticSeed)
//              Seed = NetworkDebuggingHelper.MultiplayerSeed;   // <-- silently overrides the seed in MP!
public bool NextChance(decimal _chance)                            // L193
public int NextInt(int _min, int _max, bool pMaxInclusive = false) // L79
public T GetRandomElementFromList<T>(List<T> _list)                // L251
```

- **Footgun (new finding):** any `GameRandom` constructed with a derived seed in an online-MP session is
  **silently re-seeded to the session's `MultiplayerSeed`** unless `pIgnoreMultiplayerStaticSeed: true` is
  passed (L45-58). A grant stream built without that flag would be the *same stream every combat* and
  correlated with the combat stream. §4 mandates the flag.
- `NextCount` is a public draw counter — it gives us (a) session-unique per-combat entropy that is a pure
  function of replicated lockstep history, and (b) a cheap **zero-shared-draw assertion** (§4.3, §8).
- The visual-only entry points (`NextFloatVisual` L93/L141, `NextChanceVisual` L188) are never used for
  grants (MULTIPLAYER.md: gameplay uses the non-visual stream).

Note: SPEC-DELTA §5.2#1's "constructing a fresh `GameRandom` is forbidden" governs **recipe rolls taken in
parallel on every peer against the shared stream**. The grant stream in §4 is a different, explicitly
carved-out case: a *private derived* stream whose seed is a pure function of replicated state, used at a
single point, with the host's digest as cross-peer audit. The prohibited EOR pattern —
`TryCreateSharedSeededGameplayRandom` falling back to ad-hoc seeds *when the shared stream is missing*
(SPEC-DELTA §5.2#2) — is a fallback-on-absence; §4 is a designed primary with a deterministic seed and a
verification channel. The distinction is recorded here because it is the one place this spec relaxes a
v1.1 invariant, and it is relaxed with an audit mechanism attached.

### 1.5 Transport — reused verbatim from DevKit (no new wire mechanism)

Everything in this section is already implemented and documented in DK-src; the verb adds a payload, not a
channel. Send: the 13-arg root overload `AdventureDirector._trySendNetworkAction`
(`tools/out/decompile/FTK2/AdventureDirector.cs:15135`), payload boxed as `ValueTuple<string,int>` into
`DebugGetSpecificThingActionData.ThingData` via the `eAdventureActions.DEBUG_GET_SPECIFIC_THING` case of
`AdventureActionData.Create` (`AdventureActionData.cs:275`, field :179) — ParityTransport.cs:36-68,
313-334. Receive: void (cannot-skip) prefix on `AdventureDirector._handleNetworkAction(GameAction)`
(`AdventureDirector.cs:15166`), walking `GameAction → AdventureActionData → leaf` exactly as
ParityPatches.cs:81-102. `DEBUG_GET_SPECIFIC_THING` has **no case** in `_handleNetworkAction`'s switch and
falls to the `default:` arm (`AdventureDirector.cs:15389-15392`) which logs one cosmetic line and advances
the queue — zero simulation side effects (DevKit SPEC §Status). Host/session detection:
`NetworkData.IsHost` / `NetworkData.PlayingOnlineMultiplayer`, public bools (`NetworkData.cs:26,61`).

**Channel-discipline assessment (as tasked).** Is a gameplay-affecting payload acceptable on the debug
channel? Verdict: **yes for v1, and the question is less loaded than it sounds.** In v1 (Mode M, §4.1) the
payload is *verification traffic* — losing or filtering it can never change gameplay, only mute an audit.
Even in the reserved Mode H, what we need from the *vanilla* handler is precisely inertness — the mod-side
receive hook does the work, and a vanilla handler that acted on the action would be a bug, not a feature.
So the DebugThing channel's "no case in the switch" property is a requirement, not a smell. The residual
risk — a future game build discarding unhandled action types before `_handleNetworkAction` — is already
hedged by DevKit's `[Multiplayer] ParityChannel = EorTownServices` escape hatch (DevKit SPEC §5), which the
verb inherits for free by riding the same sender. **This does not force the EorTownServices question now**;
it becomes live only if Mode H ships *and* a game update filters the debug action (§9 OQ-5).

---

## 2. Design overview — one compute point, one delta, two application modes

```
                       every peer (lockstep)                          host only
  ┌────────────────────────────────────────────────────┐   ┌───────────────────────────┐
  │ CombatPhase._endCombatAsync                        │   │                           │
  │   └ GetLootDropsFromEnemies(...)  [vanilla rolls,  │   │                           │
  │        shared CombatState.Random, untouched]       │   │                           │
  │   POSTFIX (the verb):                              │   │                           │
  │     1. snapshot DrawMark = Random.NextCount        │   │                           │
  │     2. derive grant stream  (§4.2)  [private RNG]  │   │                           │
  │     3. compute Ops = f(recipes, replicated state,  │   │                           │
  │            grant stream)            (§3.2, §4.4)   │   │                           │
  │     4. apply Ops to __result        (§3.3)         │   │                           │
  │     5. record {GrantKey, OpsHash} for this combat  │   │ 6. encode + send          │
  │     6'. assert Random.NextCount == DrawMark        │   │    CF_SYNC_LOOT_GRANT_V1  │
  └────────────────────────────────────────────────────┘   └───────────────────────────┘
              clients, on receive (whenever it arrives): compare payload OpsHash vs local
              record → match: silent · mismatch: banner + loot-grant SafeMode (§7.4)
```

**Mode M — mirrored-deterministic (v1, ships).** Every peer computes and applies the identical delta at
step 3–4, because every input is replicated and the stream is derived (§4). The host's pushed payload is
the **authoritative record and audit digest**: any client whose local computation disagrees has, by
definition, already failed R1-class parity, and the payload converts that silent drift into a loud,
attributed failure (§7.4). Dropped/late/duplicated payloads cannot affect gameplay (§7.5).

**Mode H — host-authoritative push (reserved, M-LG4).** Clients apply the pushed `Ops` verbatim, zero
local computation. Required for future consumers whose inputs are host-private (Nemesis state, encounter
modifier assignments — consumer 6), where deterministic mirroring is impossible. Mode H inherits the
delivery race documented in §1.3 (payload must be *handled* before `_distributeRewardsAsync`:712 on every
peer, and the explicit pump at :779-782 fires *after* the list is consumed), so it is **gated on
verification item V-2** and not part of v1. The payload schema carries `Mode` from day one so Mode H is a
consumer change, not a wire change.

**Why this deviates — openly — from the scouting brief's "clients apply the pushed delta verbatim, zero
local rolls."** Two decompile facts forced the refinement: (a) host-only draws from the shared stream are
refuted outright (§1.2 finding 2), which the brief itself anticipated with its "or deterministically from
replicated state" alternative; and once the delta is a pure function of replicated state, every peer can
compute it; (b) verbatim-apply-from-wire requires delivery before a point (`:712`) that the decompiled
queue-pump order (`:777` vs `:779`) says is not guaranteed, and a Harmony patch cannot block an async
vanilla method to wait for it. What the brief's design was *for* is fully preserved: **zero draws from the
shared stream by any peer, zero asymmetric execution, one deterministic compute point, an idempotent
versioned host-pushed action, and no client ever independently inventing loot** — a client's delta is
byte-identical to the host's or the session is loudly flagged. If the owner prefers strict
verbatim-apply anyway, Mode H is the fully-specced fallback, activated by flipping one payload field once
V-2 verifies the delivery window empirically.

**Non-goals (v1).** Branch A scripted venue loot (CombatPhase.cs:2570-2575) — grants do not fire there
(the hook never runs; documented limitation, §9 OQ-4). Combat-*time* gold (`CHANGE_STAT{Stat:"GLD"}`)
remains parked exactly per SPEC-DELTA §7.1 — this verb is post-combat only. The mastery system and full
Nemesis/encounter-modifier systems remain dropped (AUD §6 row 9, CM §4); only the verb's *capacity* to
serve their reward halves is provided.

---

## 3. The verb: payload schema, versioning, idempotency, delta ops

### 3.1 Wire payload

Rides DevKit's codec convention (JSON with a leading `"Action"` key, cf. `FTK2MODS_PARITY_V1` in DevKit
SPEC §4; `ParityPayloadCodec.PeekAction` dispatches on it):

```jsonc
{
  "Action": "CF_SYNC_LOOT_GRANT_V1",
  "SchemaVersion": 1,
  "Mode": "MIRROR",                    // "MIRROR" (v1) | "HOST_PUSH" (reserved, M-LG4)
  "GrantKey": "a3f09c2e51b7d804",      // idempotency key, §3.4
  "CombatSeed": 1846397,               // CombatState.Random.Seed (GameRandom.cs L11)
  "DrawMark": 412,                     // CombatState.Random.NextCount at the postfix (L23)
  "Ops": [                             // authored order == computation order == application order
    { "Op": "ADD_GOLD",   "Amount": 14, "Source": "SKILL_CF_TRAIT_SCAVENGER|<ownerGuid>" },
    { "Op": "ADD_ITEM",   "ConfigName": "HERB_LUCKY_00", "Stack": 1,
      "ThingId": "cf-7c1de2a90b34", "Source": "SKILL_CF_TRAIT_SCAVENGER|<ownerGuid>" },
    { "Op": "SCALE_STACK", "ConfigName": "PARTY_XP", "Percent": 20, "Source": "..." },
    { "Op": "REPLACE_ITEM", "ThingId": "<existing>", "NewConfigName": "SWORD_IRON_OF_FURY",
      "Source": "..." }                // reserved: rejected by the v1 validator (M-LG4)
  ],
  "OpsHash": "sha256:<64 hex>"         // hash of the canonically-serialized Ops[] (§3.4)
}
```

`Source` is diagnostic only (recipe id + owner guid) — never used in application logic; it exists so a
mismatch banner and the offline harness can attribute a divergent op. Item ids in examples are
placeholders pending the vocab re-index (AUD §1's contamination warning; the converter/validator owns real
ids).

**Size.** Worst observed case (4 players × 2 grant sources + scaling ops) is well under 1 KiB; the payload
inherits DevKit's `MaxParityPayloadBytes` cap (8192, DevKit SPEC §5). Over-cap (should be impossible):
degrade to digest-only — `Ops` omitted, `GrantKey`/`OpsHash` kept — so verification still functions
(mirrors the parity truncated-snapshot precedent, DevKit SPEC §Status).

**Versioning.** `_V1` suffix + `SchemaVersion` per MULTIPLAYER.md's payload-versioning rule. A receiver
that cannot parse the version logs once and ignores the payload — in Mode M that degrades verification
only. Breaking changes ship as `CF_SYNC_LOOT_GRANT_V2`; peers on different verb versions already fail R1
(different ClassForge `dataHash`/version), so the version field is diagnostic, not a compatibility layer.

### 3.2 Delta ops (closed vocabulary, v1)

| Op | Fields | Semantics (against the pending `List<Thing>`) | Verified need |
|---|---|---|---|
| `ADD_GOLD` | `Amount` (int > 0) | If a `CURRENCY_ADVENTURE` `Thing` exists (vanilla inserts it at index 0, LootDropHelper.cs:1271-1274), increase its stack; else create one (deterministic id, §3.3) and **append at list end** | EOR `AddTraitGoldReward` (L16363, L16382, L16405); Nemesis gold (L1560) |
| `ADD_ITEM` | `ConfigName`, `Stack` (default 1), `ThingId` | `InventoryHelper.CreateThing(ConfigName)`, set id = `ThingId`, increase stack, **append at list end** | EOR tagged herb/scroll rewards (L16370, L16389, L16412); Nemesis/enc-mod extra item (L1566-1569, L16651-16654) |
| `SCALE_STACK` | `ConfigName` ∈ {`PARTY_XP`, `XP`, `CURRENCY_ADVENTURE`, `CURRENCY_LORE`}, `Percent` (int, may be negative) | Multiply the matching `Thing`'s stack by `(100+Percent)/100`, round-to-int, min 1; no-op if absent | EOR `IncreaseLootStackByPercent` on XP/gold (L1561, L16643-16644); AUD adaptive-threat rewards (Plugin.cs L16447-16449) |
| `REPLACE_ITEM` | `ThingId` (of an item already in the list), `NewConfigName` | Swap the `Thing`'s config for a pre-minted affix variant, preserving id/stack. **Reserved**: v1 validator rejects it; ships with CM §3 row 22 in M-LG4 | EOR `ApplyAffixesToGeneratedItems` (L16421) — re-hosted per CM §3 row 21's pre-mint rule |

Append-only placement (end of list) for additive ops is deliberate: it can never disturb the relative
order of vanilla-generated items, so even a hypothetical partially-degraded peer disagrees only about a
suffix. `SCALE_STACK`'s whitelist matches exactly the currency `Thing`s the vanilla aggregator creates
(§1.1) and precedes `ProcessXPLoot`'s consumption of them (§1.3 ordering).

Interaction footnote: `ProcessLootDrops` (LootDropHelper.cs:1448-1457) later raises a
`CURRENCY_ADVENTURE` stack to at least party size. A pure-grant gold stack smaller than the party count is
therefore silently raised by vanilla — identically on every peer. Documented, not fought.

### 3.3 Deterministic `Thing` identity

Every `Thing` minted by an op gets `Id = "cf-" + first 12 hex of SHA256("CF_LOOT_THING|" + GrantKey + "|" +
opIndex)` (invariant culture) — never a runtime GUID — mirroring SPEC-DELTA OQ#1 item 5 and EOR's own
`AssignDeterministicThingId` at this exact surface (L1567, L16652: EOR got this part right). Required
because `_onTakeLootItem` operates on concrete `Thing` instances (§1.3) and the take must resolve to the
same item on every peer regardless of how takes are represented on the wire (V-3).

### 3.4 Idempotency and the single-slot pending context

```
GrantKey  = first 16 hex of SHA256("CF_SYNC_LOOT_GRANT_V1|" + CombatSeed + "|" + DrawMark
                                   + "|" + join(",", ownerGuids sorted ordinal))
OpsHash   = "sha256:" + 64 hex of SHA256(canonical serialization of Ops[]: authored order,
            invariant culture, "\n" joined "Op|field=value|..." rows)   // same shape rules as
                                                                        // ParityService.ComputeDataHash
```

`CombatSeed` alone is **not** combat-unique in online MP — the seeded ctor override (§1.4) pins it to
`MultiplayerSeed` for the session, which is also why SPEC-DELTA §6's `CombatKey` pairs it with object
identity. `DrawMark` (the shared stream's draw count at the postfix) supplies the per-combat entropy and
is identical on every peer iff lockstep held — which is the game's own maintained invariant (the desync
monitor compares exactly this counter's outputs).

Application discipline, mirroring the ClassForge single-slot per-battle model (SPEC-DELTA §6): the engine
holds exactly **one** `(GrantKey, PendingGrant)` record — `{listRef, localOps, localOpsHash, applied,
verified}`. A new postfix invocation with a different `GrantKey` drops the old record wholesale before
doing anything. Rules:

- **Apply at most once:** ops are applied in the postfix (step 4) and never again; there is no
  apply-from-wire path in Mode M.
- **Duplicate payload received** (same `GrantKey`, already verified): no-op.
- **Payload for an unknown/stale `GrantKey`:** discard + one debug log (it is a payload for a combat this
  peer never armed or already dropped — e.g. a late joiner, §7.3).
- **Payload before the local postfix ran** (host faster than client): held in the single-slot inbox
  (capacity 1, keyed by `GrantKey`) and compared when the local record forms; overwritten by any newer
  payload.

---

## 4. RNG discipline

### 4.1 The invariant that rules them all

> **No peer — host included — takes any draw from `Env.GameRun.CombatState.Random` on the loot-grant
> path. Ever.**

Enforced mechanically: the postfix snapshots `CombatState.Random.NextCount` (public, GameRandom.cs L23) on
entry and asserts equality on exit (step 6'). In dev builds the assertion failure is loud (log +
`DevKitLog` entry); in release it logs once per session. This turns the whole class of EOR's L16357-16419
bug into a self-reporting condition. (The assertion also cheaply catches any *future consumer* that
wrongly reaches for the shared stream.)

### 4.2 The grant stream

```
grantSeed   = unchecked((int)(first 4 bytes of SHA256("CF_LOOT_GRANT_V1|" + CombatSeed
                              + "|" + DrawMark)))          // invariant culture, big-endian
grantRandom = new GameRandom(grantSeed, pIgnoreMultiplayerStaticSeed: true)   // §1.4 footgun: the
                                                            // flag is MANDATORY (GameRandom.cs L45-58)
```

- Derived from **replicated state only** (`CombatSeed` = `GameRandom.Seed`, public readonly, L11;
  `DrawMark` per §3.4) — the brief's "deterministically from replicated state" branch, satisfying
  SPEC-DELTA §7.1's unlock condition (2) with the refinement argued in §2.
- `GameRandom` (not `System.Random`) is used so `NextChance(decimal)` / `NextInt(min,max,inclusive)` /
  `GetRandomElementFromList` semantics match the EOR-observed distributions verbatim (L193, L79, L251) —
  the ported chances (25%/20%/10%, 8–20 gold) mean the same thing they meant in EOR.
- Constructed fresh per combat, used only inside the postfix, discarded with the pending record. It never
  escapes to any other system, so it cannot become an EOR-style ambient fallback stream.

### 4.3 Draw-sequence invariants (the §5.2 mirror, restated for a private stream)

SPEC-DELTA §5.2#3's "draw count is a pure function of replicated state" exists to protect a *shared*
stream from asymmetric advancement. The grant stream is private, so the requirement transposes to: **the
draw *sequence* is a pure function of replicated state**, so that every peer's mirrored computation walks
the identical sequence. Binding rules:

1. **Fixed iteration order everywhere** (verbatim §5.2#4): owners = the entities in the postfix's `pParty`
   argument (alive players — matching EOR's observed "alive players only" semantics, L16351 iterating
   `__0`), sorted ascending ordinal `Entity.Guid`; per owner, grant recipes sorted ascending
   (`Priority`, ordinal recipe id); per recipe, effects in authored array order. Never dictionary order.
2. **Evaluation order per recipe:** `Enabled` → trigger match → `Conditions` → **proc roll** →
   (`PickOneEffect` roll) → per-effect draws. `ProcChance == 100` takes zero draws (§5.2#3 verbatim). A
   failed proc takes no further draws for that recipe. Branch-dependent draw counts are permitted (unlike
   on the shared stream) because every peer takes the same branch — same stream, same inputs.
3. **Item-pool draws are one `GetRandomElementFromList` over a canonically-sorted candidate list**:
   candidates filtered from `Env.Configs.Things` (parity-hashed under R1) by tag/rarity/droppability,
   then sorted ordinal by `ConfigName` before the draw. The vanilla query helpers that *manage cooldown
   state or consume RNG themselves* (`GetRandomItemClassForPlayers`, LootDropHelper.cs:562, mutates
   favored-class cooldowns) are **not** used; pure filtering only. [Filter semantics to be pinned against
   `FilterLootNames` L47 / `QueryItemsByTags` L397 at M-LG1.]
4. **Conditions never draw** (verbatim §3 of SPEC-DELTA): the v1 condition subset for this trigger is
   pure reads of replicated state (§6.1).

### 4.4 Where each number comes from (worked example — SCAVENGER)

For an owner with the SCAVENGER recipe: draw 1 `NextChance(0.25m)` (proc); if passed, draw 2
`NextInt(0, 1, inclusive)` (PickOneEffect selector); branch gold → draw 3 `NextInt(8, 20, inclusive)`;
branch herb → draw 3 `GetRandomElementFromList(sortedHerbCandidates)`. Identical to EOR's draw shape
(L16357-16377) — except taken from the private grant stream instead of the shared one, which is the entire
fix.

---

## 5. Hook points (complete patch inventory for this feature)

| Target | Kind | Role | Grounding |
|---|---|---|---|
| `LootDropHelper.GetLootDropsFromEnemies` | **Postfix** (`ref List<Thing> __result` mutated; postfix cannot skip vanilla) | The single compute+apply point (§2 steps 1–6') | §1.1; sole caller CombatPhase.cs:2578 |
| `AdventureDirector._handleNetworkAction` | existing DevKit **void Prefix** (observe-only, cannot return false) | Receive `CF_SYNC_LOOT_GRANT_V1`; route by `Action` key to the grant verifier | §1.5; ParityPatches.cs:9-15, 57-74 |
| `AdventureDirector._trySendNetworkAction` (13-arg root overload) | existing DevKit **direct reflective call** | Host send | §1.5; ParityTransport.cs |
| `CombatPhase._endCombatAsync` / `_distributeRewardsAsync` | **no patch** | — deliberately. The verb needs no second hook in v1: apply happens inside the loot postfix, synchronously, before the list is ever consumed. Mode H would add a `_distributeRewardsAsync` Prefix as its window-close flush point (GameplayDirectorBase.cs:701) — reserved with V-2 | §1.2, §1.3 |

Every lookup logs the `docs/CONVENTIONS.md` "Target found: X" line and registers with DevKit's
`PatchRegistry` (DevKit SPEC §3). Rule-1 fail-safe: if `GetLootDropsFromEnemies` fails to resolve after a
game update, the entire loot-grant engine disables itself loudly; nothing else in ClassForge is affected.

Auditable safety property (extends SPEC-DELTA §5.2#6): the verb adds **zero prefixes that can skip a
vanilla method** and **zero writes to any state other than the pending loot list** (which vanilla itself
hands to every peer for exactly this kind of pre-distribution shaping — cf. vanilla's own `_additionalDrops`
merge at CombatPhase.cs:2588-2591). No entity, inventory, `GameRunData` or `Configs` mutation anywhere.

---

## 6. Consumer API surface (ClassForge recipe vocabulary additions)

### 6.1 New trigger: `ON_COMBAT_LOOT`

| Property | Value |
|---|---|
| Hook | `LootDropHelper.GetLootDropsFromEnemies` Postfix (§5) |
| Owner | each entity in `pParty` (alive players) holding the recipe, ascending `Entity.Guid` ordinal |
| Fires | once per won enemy-loot combat (Branch B only, §1.2) |
| Datum bindings | defeated enemies = `pEnemies`; the pending loot list (not exposed to conditions) |
| Restricted vocabulary | Effects: **only** §6.2 grant effects. Conditions: `HP_THRESHOLD`, `CHARACTER_TYPE` (with `Of: SELF`), `Negate` — the pure-replicated-read subset; the validator rejects combat-turn conditions (`ROLL_TIER`, `FOCUS_SPENT`, …) whose context does not exist at combat end. `Budget`/`Cooldown` are rejected (the trigger is intrinsically once-per-combat). `AiProcChance` ignored (owners are players) |
| MP posture | `[SYNCED]` — computed identically everywhere (Mode M), host-audited; RNG = grant stream only (§4) |

New recipe-level field (this trigger only): **`PickOneEffect: true`** — after the proc roll passes,
exactly one entry of `Effects[]` is selected uniformly (one grant-stream draw), mirroring EOR's nested
50/50 shape (L16360) and the `StatusOneOf` one-draw precedent (SPEC-DELTA §4.1).

### 6.2 New effects

```jsonc
// GOLD_GRANT — emits one ADD_GOLD op
{ "Type": "GOLD_GRANT", "MinGold": 8, "MaxGold": 20 }     // MinGold==MaxGold → zero-draw flat grant

// ITEM_TAG_GRANT — emits one ADD_ITEM op
{ "Type": "ITEM_TAG_GRANT", "Tag": "HERB", "Rarity": "COMMON",   // Rarity optional (null = any)
  "Stack": 1 }

// LOOT_SCALE — emits one SCALE_STACK op; zero draws
{ "Type": "LOOT_SCALE", "ConfigName": "CURRENCY_ADVENTURE", "Percent": 50 }

// AFFIX_ROLL — emits REPLACE_ITEM ops; RESERVED (validator-rejected until M-LG4, CM §3 row 22)
{ "Type": "AFFIX_ROLL", "ChancePct": 15, "Table": "CF_AFFIX_TABLE_STANDARD" }
```

`Rarity` validates against `eItemRarities` (EGT §2); `Tag`/`ConfigName` against the post-merge `Configs`
registry at load. All effect schemas hard-fail validation on any other trigger.

### 6.3 The six consumers, concretely

```jsonc
// 1. SCAVENGER loot half (EOR L16357-16377) — trait's SKILL_ passive carries the recipe
"SKILL_CF_TRAIT_SCAVENGER_LOOT": {
  "SchemaVersion": "1.2", "Enabled": true,
  "Trigger": "ON_COMBAT_LOOT", "ProcChance": 25, "PickOneEffect": true,
  "Effects": [
    { "Type": "GOLD_GRANT", "MinGold": 8, "MaxGold": 20 },
    { "Type": "ITEM_TAG_GRANT", "Tag": "HERB", "Rarity": "COMMON" } ] }

// 2. TREASURE_SENSE loot half (L16379-16388)
"SKILL_CF_TRAIT_TREASURE_SENSE_LOOT": {
  "Trigger": "ON_COMBAT_LOOT", "ProcChance": 20,
  "Effects": [ { "Type": "GOLD_GRANT", "MinGold": 8, "MaxGold": 20 } ] }

// 3. SCHOLARS_HABIT loot half (L16389-16396; Rarity null = any, matching EOR)
"SKILL_CF_TRAIT_SCHOLARS_HABIT_LOOT": {
  "Trigger": "ON_COMBAT_LOOT", "ProcChance": 20,
  "Effects": [ { "Type": "ITEM_TAG_GRANT", "Tag": "SCROLL" } ] }

// 4. OF_SCAVENGING affix loot half (L16397-16419) — alternate-grantor pattern per CM §3:
//    the pre-minted _OF_SCAVENGING variant items carry this SKILL_ in Equippable.Passives
"SKILL_CF_AFFIX_OF_SCAVENGING_LOOT": {
  "Trigger": "ON_COMBAT_LOOT", "ProcChance": 10, "PickOneEffect": true,
  "Effects": [
    { "Type": "GOLD_GRANT", "MinGold": 5, "MaxGold": 10 },
    { "Type": "ITEM_TAG_GRANT", "Tag": "HERB", "Rarity": "COMMON" } ] }

// 5. Affix drop-rolling (CM §3 row 22) — M-LG4, engine-owned (not per-owner), Mode M:
//    one AFFIX_ROLL evaluation over the generated item list, REPLACE_ITEM ops onto
//    pre-minted variants (CM §3 row 21's deterministic pre-mint registry). Sketch only here.

// 6. Future encounter-modifier / Nemesis reward halves (systems themselves remain unspecced,
//    AUD §6 row 9) — the verb-side shape they will use, per EOR evidence:
//    NEMESIS_WEALTHY (+50% gold, L5177/L1558-1560):  LOOT_SCALE {CURRENCY_ADVENTURE, +50}
//    Nemesis XP bonus (L1561):                       LOOT_SCALE {PARTY_XP, +20}
//    enc-modifier gold/XP% (L16643-16644):           LOOT_SCALE ops
//    Nemesis "extra copy of a dropped item" (L1562-1569): ADD_ITEM (config chosen by the
//      host-side system → Mode H, because Nemesis state is host-private under R3)
```

Consumers 1–4 flip from PARK to **PORT** in the coverage matrix; consumer 5 flips row 22 to
PORT-MODIFIED-pending-M-LG4; consumer 6 stays dropped as a *system* — only its verb dependency is
dissolved.

---

## 7. Multiplayer posture (MULTIPLAYER.md §9 mandatory answers)

1. **Parity class: `ALL_PEERS`.** The recipes are ClassForge pack data — parity-hashed under R1; a peer
   without the identical grant recipes computes a different delta and is caught by both ParityService
   (dataHash) and the per-combat OpsHash audit.
2. **Feature table.**

   | Feature | Label | Authority |
   |---|---|---|
   | Grant computation + application (Mode M) | `[SYNCED]` | every peer, identically; host's digest is authoritative on dispute |
   | `CF_SYNC_LOOT_GRANT_V1` push | `[SYNCED]` | host only sends (`NetworkData.IsHost`); all peers receive/verify |
   | Per-combat pending record / verification state | `[LOCAL]` | per peer; single-slot, dropped on next combat |
   | Grant logging (`CLASSFORGE_LOOT` DevKitLog category) | `[LOCAL]` | R4-exempt |
3. **Determinism inventory.** One generated artifact (the delta) = pure function of (parity-hashed recipe
   data, replicated `pParty`/trait state, `CombatSeed`, `DrawMark`) via the derived grant stream — §4.
   Zero shared-stream draws, asserted at runtime (§4.1). Deterministic `Thing.Id`s (§3.3).
4. **Sync surface.** One custom action, `CF_SYNC_LOOT_GRANT_V1` (schema §3.1), host → all, fired once per
   won enemy-loot combat, idempotent by `GrantKey` (§3.4). **No `_REQUEST_V1` companion**: the payload's
   meaning expires with the combat that produced it; a late joiner has nothing to re-request because every
   grant it missed is already embodied in vanilla-replicated inventories/gold from the loot distribution
   it also missed. Everything downstream of the list (takes, gold/XP credit) is untouched vanilla
   replication (`_onTakeLootItem` flow, §1.3).
5. **SafeMode definition.** Two layers: (a) ClassForge session SafeMode (parity mismatch,
   SPEC-DELTA §5.3) already turns the whole recipe engine off all-or-nothing — grants included, on every
   peer symmetrically (DevKit SPEC §8 item 11 verifies banner symmetry). (b) **Loot-grant SafeMode**
   (verb-local): on an OpsHash mismatch or a failed zero-draw assertion, grant computation disables for
   the remainder of the session on the peer(s) that detect it *and* the host broadcasts nothing further;
   the current combat's already-applied delta is **not** retro-mutated (the lists are already on screen —
   retro-mutation is exactly the EOR-class hazard, AUD §3.3). Detection is the deliverable; the mismatch
   banner names the recipe via `Source`.
6. **MP test plan.** §8.2.

**Join-in-progress.** No verb state outlives a combat; ParityService's existing
`FTK2MODS_PARITY_REQUEST_V1` re-query covers the R1 half (DevKit SPEC §3). A peer that joins *between*
combats needs nothing. A peer joining *mid-combat-end* (if the game even permits it — **[UNVERIFIED]**,
OQ-6) at worst receives a payload for a `GrantKey` it never armed → discarded by rule §3.4; its own list
came from the host's... it has no list; it missed the loot screen entirely, which is vanilla's problem and
vanilla's behavior, not ours.

**Failure-mode matrix (mandated).**

| Failure | Mode M consequence |
|---|---|
| Payload dropped / transport unresolved (`ParityTransport.CanSend == false`) | Grants identical on all peers anyway (deterministic mirror); that combat is logged "unverified"; zero gameplay effect |
| Payload duplicated | `GrantKey` match → no-op (§3.4) |
| Payload late (after loot screen) | Verification still completes against the retained pending record; apply-from-wire never happens in Mode M |
| Payload reordered across combats | Stale `GrantKey` → discard + log |
| Payload malformed / unknown `SchemaVersion` | Discard + one warning; combat unverified |
| Payload over size cap | Digest-only degrade (§3.1); verification via `OpsHash` still runs |
| OpsHash mismatch (real divergence) | Banner naming mod + recipe (`Source`), loot-grant SafeMode engages (§7.5b) — the silent-drift → loud-failure conversion that is this verb's whole point |
| Game update renames the hook | Rule-1 fail-safe: "Target NOT found", grant engine off, everything else runs |

---

## 8. Single-player fast path, verification & test plan

### 8.0 SP fast path

`NetworkData.PlayingOnlineMultiplayer == false` (NetworkData.cs:61): identical compute+apply path (§2
steps 1–6'), minus send, minus verification bookkeeping. No transport resolution is attempted. The grant
stream derivation is unchanged (seeded ctor override doesn't trigger offline, GameRandom.cs L47), so an SP
combat with the same seed/state produces the same grants as an MP one — which is what makes the offline
harness (§8.1) representative of MP behavior.

### 8.1 Offline harness additions (extends DevKit.Core.Tests-style pure-C# tests)

1. **Determinism:** identical `(recipes, party, CombatSeed, DrawMark)` → identical `Ops`/`OpsHash` across
   1k randomized scenarios; any input perturbation → different `GrantKey`.
2. **Draw-sequence lock:** golden-file test of the exact draw sequence for the four shipped consumers
   (§4.4 shape), so a refactor that reorders draws fails loudly.
3. **Zero-shared-draw assertion:** simulated shared stream's `NextCount` unchanged across compute+apply.
4. **Idempotency:** duplicate/late/stale/malformed/over-cap payload handling per §7's matrix.
5. **Codec round-trip:** payload encode → decode → `OpsHash` re-verify; digest-only degrade path.
6. **Validator:** `AFFIX_ROLL`/`REPLACE_ITEM` rejected in v1; `Budget` on `ON_COMBAT_LOOT` rejected;
   effect schemas rejected on other triggers; `PickOneEffect` with < 2 effects rejected.
7. **Op application:** gold merge-vs-append, `SCALE_STACK` whitelist + rounding + min-1, append-only
   ordering, deterministic `Thing.Id` derivation.

### 8.2 In-game verification (extends DevKit SPEC §8 / ClassForge SPEC §8 smoke tests)

- **SP smoke:** win a combat with a SCAVENGER-trait character; confirm grant appears on the loot screen,
  is takeable, and the shared stream's `NextCount` delta across the postfix is zero (dev assertion +
  `dk_dump_combat`-style log line).
- **2-peer smoke additions (to DevKit SPEC §8 item 11's two-instance protocol):**
  (a) both peers + SCAVENGER: win a combat → identical loot lists (dump both, byte-compare), payload
  arrives, "verified" logged on both, no vendor desync warning across 5+ consecutive combats
  (`DoMonitorForDesyncs` active);
  (b) **deliberate divergence:** hand-edit the client's grant recipe (`ProcChance` 25→100) mid-session →
  next combat must produce the OpsHash-mismatch banner + loot-grant SafeMode on detection, *and*
  ParityService's own dataHash re-handshake should already have flagged the edit (belt and suspenders
  observed working together);
  (c) transport kill-switch: block sending (dev knob) → grants still identical, combats logged unverified;
  (d) empirical **V-1/V-2 measurements** (below) recorded in the test log.

**Verification items (empirical, decompile cannot answer):**

| # | Question | Gates |
|---|---|---|
| V-1 | Is `CombatState.Random.NextCount` bit-identical across peers at the loot postfix in real 2-peer sessions (i.e., does `DrawMark`-keyed `GrantKey` agree)? Expected yes (it is the lockstep invariant); measure anyway | M-LG3 default-on |
| V-2 | Wall-clock delivery + *handling* latency of a `DEBUG_GET_SPECIFIC_THING` payload sent at loot-generation time, relative to the client reaching `_distributeRewardsAsync`:712 — does the adventure queue pump during combat end at all, or only at :779-782? | Mode H (M-LG4) only |
| V-3 | How does a loot take replicate on the wire (index vs `Thing.Id` vs config name)? Determines whether §3.3's deterministic ids are load-bearing or belt-and-suspenders | M-LG3 |

---

## 9. Open questions

1. **DevKit transport API surface.** ParityTransport/`ParityPayloadCodec` are `internal` today (DK-src).
   The verb needs a send/receive-registration surface for sibling mods:
   `TransportService.Send(string payloadJson)` + `RegisterReceiver(string actionKey, Action<string>)`,
   exposed via the same reflection soft-dependency convention as `ParityService.Register` (DevKit SPEC §3)
   — or this finally tips DevKit SPEC §11.9's contracts-DLL question. Decide at M-LG2 design review.
2. **Item-pool filter semantics** for `ITEM_TAG_GRANT` (which tag/droppable/expansion predicates replicate
   EOR's `TryAddTraitTaggedReward` pool exactly) — pin against `FilterLootNames`/`QueryItemsByTags`
   (LootDropHelper.cs:47/397) plus EOR's helper body at M-LG1.
3. **`RewardEncounterComponent` combats** (L1212-1221 branch): grants currently fire there too (same
   method). Correct? EOR's postfix also did. Keep, unless playtest says reward-encounter "combats"
   (chests/scripted rewards) shouldn't proc traits — then add a condition on `pEnemies` composition.
4. **Branch A scripted venue loot** (CombatPhase.cs:2570-2575) bypasses the hook — final-phase venue
   combats with a `LootTableArg` produce no grants. Acceptable v1 gap (EOR had the same gap); revisit if
   playtesting surfaces it.
5. **Mode H channel choice** — revisit DebugThing vs EorTownServices only if Mode H ships (§1.5 verdict).
6. **Can a peer join mid-combat / mid-combat-end?** [UNVERIFIED] — affects only the JIP footnote in §7.
7. **`GameRunData` gold/XP credit replication** (MULTIPLAYER.md open question #2) — the verb rides the
   vanilla distribution flow so it inherits vanilla's answer, but the 2-peer smoke should confirm granted
   gold lands identically on both peers' party totals.

---

## 10. Milestones

- **M-LG1 — PSN merge + offline core.** Merge §1's signatures into `game-patch-surface-notes.md` (closes
  SPEC-DELTA §7.1 unlock condition (1)). Implement delta computer, grant stream, payload codec,
  validator rules, single-slot pending model — pure C#, no game refs, harness §8.1 green. Resolve OQ-2.
- **M-LG2 — hooks + SP.** `GetLootDropsFromEnemies` postfix, zero-draw assertion, SP fast path; ship
  consumers 1–4 recipes in the EOR port pack, **dark** behind `[Skills] EnableLootGrants = false`
  (charter rule 3's disabled-by-default carrier while unproven). ClassForge SPEC §7.1 → ADOPTED (v1.2),
  coverage-matrix rows updated. DevKit transport-surface decision (OQ-1).
- **M-LG3 — MP wire-up.** Host send, receive/verify, failure-mode matrix behaviors, `dk_dump`-visible
  grant state; 2-peer smoke §8.2 including V-1/V-3; flip `EnableLootGrants` default to `true`.
- **M-LG4 — reserved consumers.** Affix drop-rolling (`AFFIX_ROLL`/`REPLACE_ITEM`, CM §3 row 22) on the
  pre-mint registry; Mode H design finalized against V-2 measurements — prerequisite for any future
  Nemesis/encounter-modifier reward system, which remains separately unspecced (AUD §6 row 9).

---

## 11. Document deltas on adoption (checklist)

1. `docs/research/game-patch-surface-notes.md` — new §12 from this spec's §1 (LootDropHelper,
   CombatPhase combat-end sequence, `_distributeRewardsAsync`, GameRandom ctor/NextCount).
2. `FTK2.ClassForge/SPEC.md` — §4.6 gains `ON_COMBAT_LOOT` + §6.2 effects; §7.1 park row → adopted with
   pointer here; §9 tables gain §7's rows; §11 closes OQ#6's successor.
3. `docs/research/eor-rehost-coverage-matrix.md` — §1 SCAVENGER → PORT; §2/§3 loot-half rows
   (TREASURE_SENSE, SCHOLARS_HABIT, OF_SCAVENGING) → PORT; §3 row 22 → PORT-MODIFIED (M-LG4); recount
   totals.
4. `FTK2.DevKit/SPEC.md` — transport-surface API (per OQ-1 outcome) + §6 row noting the shared receive
   hook now dispatches two action families.
5. `docs/MULTIPLAYER.md` — add `CF_SYNC_LOOT_GRANT_V1` to the standard-spec-language example list of
   versioned snapshot actions (R3), with the Mode M "mirror + audit" pattern noted as the third posture
   alongside "host-decides + vanilla-replicates" and "custom `_SYNC_` display snapshots".
