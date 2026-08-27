---
title: COMPANION-follower migration for Trainer partners - feasibility assessment
type: research
tags: [ftk2, classforge, summoner, multiplayer, followers, summons, trainer]
repo: C:\Users\ben\repos\ftk2-mods-crucible
date: 2026-08-26
status: assessment-only (nothing implemented)
---

# Can Trainer partners be COMPANION followers instead of mid-combat summons?

Static analysis only. No game process was touched. Sources: `.decompile-scratch/proj/` (vendor),
`MULTIPLAYER-RULES.md` (EOR 0.7.0.66), and this repo.

---

## VERDICT

**FEASIBLE-WITH-CAVEATS, but only in the shape the operator flagged as the fallback: "the partner is
already in the party", NOT "send a specific partner out mid-fight."**

Split, because the two halves have different answers:

| Shape | Verdict |
|---|---|
| Partner is a persistent `COMPANION` follower acquired **out of combat**, present on the board from combat start | **FEASIBLE** (with 3 design losses, below) |
| Partner is **sent out mid-fight** as a follower via `eSummonTypes.AS_FOLLOWER` | **NOT FEASIBLE as authored content**. The vendor path exists but is unusable by us for two independent reasons, either of which alone kills it. |

### The single fact that decides the mid-fight half

`CombatHelper.cs:2255`, inside the `COMPANION && AS_FOLLOWER` branch of `ApplyAction(ADD_CHARACTER)`:

```csharp
int pLevel2 = int.Parse(characterComponent3.ConfigName.Split('_').Last());
```

`characterComponent3` is **`pOrigin`'s** `CharacterComponent` — the summoner. Our Trainer's
`ConfigName` is `CF_ORIG_TRAINER` (`FTK2.ClassForge/data/ClassPacks/CF_PACK_ORIGINALS/classes.json`),
so `Split('_').Last()` is `"TRAINER"` and `int.Parse` throws `FormatException`. Vanilla never hits
this because vanilla only reaches `AS_FOLLOWER` from monster origins, whose config names all end in
`_01` / `_03` / etc. **This path is structurally reserved for numerically-suffixed origins and a
player class can never legally use it.**

Worse than a clean throw: `FollowerHelper.JoinFollower` has already run at `CombatHelper.cs:2247`,
writing `PlayerFollowers[player.Guid]` and `VenueState.VenuePlayerNames` — both **persisted, hashed
state** — while the throw happens *before* `CombatState.Entities.Add` / `RoundEntities.Add` /
`SetInitiative` at `:2287+`. The half-created follower is committed to the save and absent from the
fight. That is a corrupted-save failure mode, not a no-op.

### The second, independent blocker on the mid-fight half

`AS_FOLLOWER` selects by **TAG, not by config name**. `CombatHelper.cs:118-155` routes
`AS_FOLLOWER` through the same block as `RANDOM`:
`CharacterHelper.GetActorsByTags(new List<string>{ pSummonTarget }, pLevel)` → filters →
`CoreHelper.GetRandomWeighted<string>(pRandom, args2)`. Then `CombatHelper.cs:205` runs the result
through `CharacterVisualHelper.TryGetNextConfigValueName(text, valid, pTierValue:false)`, which
**walks up to ten two-digit suffixes looking for any follower row that matches** (the
`Substring(0, len-2) + num.ToString("00")` loop in `CharacterVisualHelper.cs`). So even with a unique
tag per partner stage, the vendor may silently substitute a neighbouring tier. There is **no way to
name an exact creature** through `AS_FOLLOWER`. Our 13 partner stages are each an exact named config
(`JELLY_RED_01`, `DEMON_MELEE_03`, …) and Gary's capture send-out is an exact id read out of
`Thing.CustomData` at runtime. Neither survives a tag-and-weighted-pick selector.

### A correction to the brief's premise, which the operator should see before deciding

> "a mid-combat summon is state the vendor's own detector never inspects"

**This is not true.** `CombatState` is indeed `[JsonIgnore]` on `GameRunData` (`GameRunData.cs:53-54`)
and therefore absent from the *GameRun* MD5 — but the vendor runs a **separate combat checkpoint hash**:

- `CombatPhase.cs:8674` → `NetworkHelper.CreateNewDesyncDetectionTaskFromGameState(..., pIsCombat: true, _env.GameRun.CombatState)`
- → `NetworkHelper.cs:316` `NetworkDebuggingHelper.SerializeCombatDataAndUpdateGuids(pCombatState)`
  (`NetworkDebuggingHelper.cs:235-258`), which serializes `CombatState.Entities`
- → `NetworkHelper.cs:363` `RecordCombatPhaseDataStateAndGetHash` → MD5 → compared against the peer at
  `NetworkHelper.cs:377-388` (`"NETWORK DESYNC!"`).
- `NetworkData.DoMonitorForDesyncs = true` by default (`NetworkData.cs:65`).

So our summons **are** hashed on every peer. The RULE 16 asymmetry is real but much weaker than
stated: it is *which checkpoint* and *how often*, not *hashed vs unhashed*.

Also load-bearing and previously unstated: the hash **launders GUIDs**.
`NetworkDebuggingHelper._convertGuidsOfEntities` (`:138`) + `_getIntIdFromGuid` (`:400`) replace every
`Entity.Guid` and `Thing.Id` with a sequential integer assigned in **first-appearance order over
`GameRun.Entities`** (then that entity's `Things`). Per-peer `Guid.NewGuid()` values are therefore
invisible to the detector **as long as list order agrees**. The cross-peer invariant is *entity list
order*, not GUID value. (`AvatarComponent` and `DisplayName` are in `_ignorePropertyNames`,
`NetworkDebuggingHelper.cs:42-47`, and are not hashed at all.)

**Net:** the multiplayer argument for migrating is real but smaller than the brief assumes, and the
migration itself carries new determinism risk (Q4). It should be made on *design* grounds, not on
"followers are hashed and summons are not."

---

## Q1 — Can a COMPANION follower be brought into combat MID-FIGHT at all?

**Yes in vendor code; no for us.** U1 in `MULTIPLAYER-RULES.md` is now resolved: the path exists, is
complete, and does write `PlayerFollowers` mid-combat.

The path, end to end:

| Step | Site |
|---|---|
| `ADD_CHARACTER` case reads `addCharacterAction.Type` | `CombatHelper.cs:2167` |
| → `TryCreateSummon(..., eSummonTypes2, ...)` | `CombatHelper.cs:2233` |
| `AS_FOLLOWER` branch: tag lookup + weighted pick | `CombatHelper.cs:118-155` |
| resolve follower row, `CreateFollowerCharacter`, `JoinFollower(-1)`, add to `VenuePlayerNames` | `CombatHelper.cs:204-219` |
| back in `ApplyAction`: `JoinFollower` **again**, XP grant, level progression, DisplayName overwrite | `CombatHelper.cs:2242-2262` |
| roster insert + initiative | `CombatHelper.cs:2287-2295` |

Four things make it unusable as authored content for us:

1. **`int.Parse` on the origin's config name** — `CombatHelper.cs:2255`. Hard throw for
   `CF_ORIG_TRAINER`, after persisted state was already mutated. (The deciding fact, above.)
2. **Tag-based, weighted, tier-fuzzy selection** — cannot name a creature (above).
3. **`characterComponent2.DisplayName = characterComponent3.DisplayName;`** (`CombatHelper.cs:2258`)
   — the partner is renamed to **the Trainer's own name**, destroying both the nickname feature
   (`TrainerPartnerNicknames.cs`) and the identity tell that `VERIFICATION-METHOD §2 Rule 1`
   requires for any screenshot evidence.
4. **`JoinFollower` is called twice** on the same pair (`CombatHelper.cs:214` and `:2247`), the
   second time with a different `pGameRandom`. Benign in vanilla, but any patch we hang on
   `JoinFollower` would fire twice per send-out.

**So the answer the operator asked for, plainly:** for a player class, a follower can only join
*outside* combat (or be present from combat start). The design becomes **"the partner is already in
the party."**

Confirmation that "already in the party" actually puts it on the board:
`CombatPhase.cs:273` builds `_allyEntities` from `VenueState.VenuePlayerNames`, which
`VenueHelper.ReconsiderVenuePlayers` (`VenueHelper.cs:157-170`) populates from
`CoreHelper.GetPlayersAndFollowers(...)`. **This also resolves U3** in the affirmative at the code
level — followers are combat-board participants, not caravan-only. (Still unproven on screen; see
Unverified.)

---

## Q2 — What would our Trainer lose or gain?

### GAINED

- **One-at-a-time enforcement is free and absolute.** `PlayerFollowers` is
  `Dictionary<string, FollowerState>` keyed by the **player's** `Entity.Guid`
  (`GameRunData.cs:57`, written at `FollowerHelper.cs:147`). One player character can hold exactly
  one follower, by construction. `TrainerPartnerPersistence.CanonicalBall`'s whole "which ball wins"
  arbitration (`TrainerPartnerPersistence.cs:100-124`) becomes unnecessary.
- **HP persistence is free.** Not via `FollowerState` — see below — but because the follower is a real
  `Entity` living in `GameRun.Entities` (`GameRunData.cs:33-35`, `[JsonInclude]`), so its
  `CharacterComponent.CurrentHealth` is saved and restored like any party member's.
  `TrainerPartnerPatches`' three `AddHealth`/`KillCharacter`/`SetToMaxHealth` mirrors become dead code.
- **Overworld presence, party UI, caravan, follower detail panel** all come for free
  (`FollowerDetailViewHelper.cs`, `AdventureDirector.cs:702-712`, `:4151`, `:12392`).
- **The summon-leak class of bug disappears.** `SummonLeakPatches` exists because
  `_endCombatAsync`'s `cleanUpSummon` pass is skipped on immediate exits. Followers are never
  `SUMMON`-flagged (`CombatHelper.cs:242-245`) and are never in that pass at all — the file's own
  docstring already says so.

### LOST — three, and the first is severe

1. **DOWNED-but-not-deleted is impossible.** `CombatPhase.Initialize`, `CombatPhase.cs:253-268`:

   ```csharp
   Entity playerFollower = CoreHelper.GetPlayerFollower(playerEntity.Guid, ...);
   if (playerFollower != null && CharacterHelper.IsDead(playerFollower))
   {
       _env.GameRun.PlayerFollowers.Remove(playerEntity.Guid);
       if (!FollowerHelper.IsSpecialFollower(playerFollower))
           _env.GameRun.Entities.Remove(playerFollower);
       _env.GameRun.VenueState.VenuePlayerNames.Remove(playerFollower.Guid);
   }
   ```

   A follower at 0 HP is **destroyed at the start of the next combat**. The Trainer's stated design
   — "a partner reduced to 0 HP is DOWNED, is never deleted, and only a TOWN revives it"
   (`TrainerPartnerPersistence.cs:12-16`) — is exactly what vanilla refuses to do. Keeping it means
   patching `CombatPhase.Initialize`, i.e. re-owning follower lifecycle, which forfeits most of the
   "inherit vanilla correctness" argument. `FollowerHelper.IsSpecialFollower` is the only escape
   hatch and would need inspection before being leaned on.

2. **Send-out timing disappears as a player decision.** Today the partner enters when the Trainer
   spends an action (`ON_COMBAT_START` for capture, ability-driven for the charm lines). As a follower
   it is simply present from round 1, placed by `ReconsiderVenuePlayers` and the vendor's positioning.
   "Go, Partner!" stops being a move.

3. **`FollowerState` carries no HP and no custom data.** It is exactly two fields
   (`FollowerState.cs`):

   ```csharp
   public string FollowerID;      // the follower Entity's Guid
   public int RoundsToExpire;
   ```

   So bond points, evolution stage, nickname and the max-HP band have **no home in `FollowerState`**.
   They would move to the follower entity's own `CharacterComponent`/`Things` (still hashed, still
   fine) or stay on the ball item. There is no free ride here.

### Neutral / changed

- **Evolution tiers** become "swap the follower entity's `ConfigName`", or remove-and-rejoin a new
  follower row. `TryGetNextConfigValueName`'s two-digit-suffix convention means a stage ladder wants
  `_00`/`_01`/`_02` naming if any vendor tier logic is ever to be reused.
- **Board slots are capped at 8 and companions are evicted first.**
  `VenueHelper.MakeRoomForSupportCharacters` (`VenueHelper.cs:144-155`) trims `VenuePlayerNames` to
  `pGridSize = 8`, ordering `COMPANION descending` — companions go first. A four-player party where
  everyone has a follower will drop some. Deterministic, but a real design constraint.
- **Capture creating a new follower**: see Q5.

---

## Q3 — Does `FTK2.Summoner` already do most of this?

**It does the DATA half completely and none of the RUNTIME half.**

What exists (`FTK2.Summoner/`):

- `Summoner.Core/Merge/MergePlanner.cs:18` — hard `^SMN_[A-Z0-9_]+$` id shape, adds-only, skips any id
  already in the live pre-pack `Configs` snapshot. Characters planned before followers so a pack
  satisfies its own `ConfigName` references (`MergePlanner.cs:25-42`). Order-stable
  (`.OrderBy(k => k.Key, StringComparer.Ordinal)`).
- `Summoner.Plugin/Adapters/ConfigsSink.cs` writes into `Configs.Characters` / `Configs.Followers`.
- `Summoner.Plugin/Parity/ParityRegistration.cs` — `OnParityMismatch = Block`, wired to a real
  `ParityFailed` callback that latches and stops further merges (`FTK2.Summoner/SPEC.md`, M0 section).
- **200 followers imported, not 249**: `SMN_PACK_EOR_PETS/followers.json` = 100 `COMPANION`,
  `SMN_PACK_EOR_MERCS/followers.json` = 100 `MERCENARY`. Both `"enabled": true`.
- **The exact row shape a migration needs already ships.** Imported pets point their `ConfigName` at
  **vanilla character configs**:
  `SMN_FOL_GHOST_GENERIC_00 → { "Type":"COMPANION", "ConfigName":"GHOST_GENERIC_00", "ContractRounds":0, "Tags":["COMPANION","PETSHOP"] }`.
  A Trainer partner row is the same thing with `"ConfigName":"JELLY_RED_01"`. **No new loader, no new
  schema, no code.** This is the strongest argument in favour of the migration.

What does NOT exist:

- **No runtime.** Nothing in `FTK2.Summoner` ever calls `FollowerHelper.JoinFollower`,
  `CharacterHelper.CreateFollowerCharacter`, or touches `PlayerFollowers`. The acquisition step is
  100% new work.
- **No `characters.json` in either pack** — the packs add follower rows only. Fine for pointing at
  vanilla configs; not fine if a partner stage ever needs a bespoke character config.
- **A latent data bug worth flagging:** the hand-authored `data/Followers/SMN_Followers.json` writes
  `"GiveDeed": false, "Rescued": false` as **booleans**, while `FollowerCharacterConfig.cs` types both
  as `string` (and `SPEC.md` says the pack model was corrected to string). That file sits outside the
  loader's `FollowerPacks/` tree, so it may simply be unloaded — but if it is ever loaded it will fail
  to deserialize. Not caused by this migration; noted because a migration would touch it.

**Estimate:** the merge/parity/id-discipline machinery is done. The new work is the acquisition and
lifecycle glue, which is where all the risk lives.

---

## Q4 — Draw-count profile of each path

This matters more than the brief expects, because the follower path is **strictly draw-heavier and its
draw count varies with the creature**.

### Correction to the brief's premise

> "Summon placement currently takes ZERO draws"

**Placement** is draw-free — confirmed. `RecipeActionExecutor.FindFreeTileForGroup` collects candidate
tiles and picks with `TileOrder.SelectPlacement`, a single MIN scan, no RNG. But **creation is not**:
even `SPECIFIC` runs `CharacterHelper.CreateCharacterEntity` (inner overload, `CharacterHelper.cs`
~`:1834`), which draws:

- `pGameRandom.NextChance(variantChance(...))` for character variants, and on a hit a further
  `CoreHelper.GetRandomWeighted` — a **conditional draw count keyed on whether the config has a
  `CharacterVariants` entry**;
- `LootDropHelper.TryPostProcessNewThingData(...)` once **per Thing in the config's starting kit**.

So the current path is *not* zero-draw; it is zero-draw *for placement* and data-dependent for
creation. Both peers use the same config data, so it agrees today.

### What `AS_FOLLOWER` adds on top

| Extra cost | Site |
|---|---|
| **+1 shared draw, always** — `GetRandomWeighted` calls either `NextDecimal()` (weights sum > 0) or `NextInt` (sum == 0) | `CoreHelper.cs:1522-1552`, reached from `CombatHelper.cs:154` |
| **+N draws** for `CharacterVisualHelper.CreateTierAvatarComponent(entity, pUseRandomValues:true, pRandom)` — one `NextFloatVisual` for scale, a **conditional second** if the body is `BAT`/`BIRD`, plus colour-scheme picks | `CharacterHelper.cs:2600` → `CharacterVisualHelper.cs:1855` → `CreateAvatarComponent` |
| **+N draws** `ProgressionHelper.EntityGainXP` (twice: `CharacterHelper.cs:2612` and `CombatHelper.cs:2250`) | |
| **+N draws** `CharacterHelper.TryProgressCompanionEntityToLevel(pSummonEntity, pLevel2, thing3, CombatState.Random)` | `CombatHelper.cs:2256` |

**The trap.** `GameRandom.NextFloatVisual` is a naming lie: it calls `random.NextDouble()` on the **same
`System.Random` instance** as every gameplay draw (`GameRandom.cs:88-100`). It is not a separate visual
stream. So avatar randomisation **advances the shared stream**, and its draw count branches on
`actorSkinPart.BodyTypes.Contains(eActorBodies.BAT) || …BIRD` and on `pComposition.ColorScheme != null`
(`CharacterVisualHelper.cs:1885-1905`) — i.e. on **prefab/asset state**, which is the input class
RULE 7 calls out as a fork risk. Note `AvatarComponent` is *excluded from the hash*
(`NetworkDebuggingHelper.cs:42-47`), so a divergence introduced here is **invisible in the state MD5
and only surfaces as draw-count drift later** — the hardest class to diagnose.

**Conclusion for Q4:** migrating to `AS_FOLLOWER` is a **material determinism change that increases
draw count and makes it depend on visual asset composition**. It would require the RULE 21 two-run diff
before anyone could claim it is safer. The **out-of-combat** join is better here: `JoinFollower` itself
takes **zero draws** (`FollowerHelper.cs:105-155` — no RNG in the body at all), and
`CreateFollowerCharacter`'s draws come from whichever `GameRandom` the caller supplies, which outside
combat is the adventure stream, not `CombatState.Random`.

---

## Q5 — Capture

**Would a follower be simpler AND replicated? Simpler in one respect, riskier in another, and it does
not fix a problem we actually have.**

Today (`RecipeActionExecutor.ExecCapture`, `RecipeActionExecutor.cs:741-813`): write
`CF_POKE_CONFIG` / `CF_POKE_HP` / `CF_POKE_MAXHP` / `CF_POKE_DOWNED` into the ball `Thing`'s
`CustomData`, then `results.Add((eAbilityResults.PLAYTHINGED, target))`.

**Is that safe today?** Yes, and the brief's reasoning holds. `Thing.CustomData` **is** inside the
hash — `Thing` is serialized under `CharacterComponent.Things` and `CustomData` is not in
`_ignorePropertyNames`. Every value written is derived from replicated state (`cc.ConfigName` of a
replicated enemy; the rest are literals). And the **capture decision is already draw-free** —
`SKILL_CF_TRAINER_CAPTURE_CATCH` gates on `ROLL_TIER EQ PERFECT` plus tag/type predicates
(`CF_PACK_ORIGINALS/skillrecipes.json`), i.e. player input and replicated data, no RNG. That already
satisfies `MULTIPLAYER-RULES §B`'s minimum safe pattern.

**The follower version.** The elegant form is not "create a new follower" but
**convert the enemy entity itself**:
`FollowerHelper.JoinFollower(trainer, targetEnemy, env, random, -1, false)`. The enemy is already in
`GameRun.Entities` at an identical index on both peers, `JoinFollower` takes zero draws, and no new
entity — hence no `Guid.NewGuid()` — is minted. `JoinFollower` adds to `Entities` only
`if (!Contains)` (`FollowerHelper.cs:139-142`), so list order is untouched. That is genuinely clean.

But it buys less than it looks:

- It does **not** make capture more replicated. Capture already is.
- It **immediately inherits the delete-on-death rule** (Q2 loss #1): the captured creature is gone
  forever the first time it drops.
- It caps the Trainer at **one captured creature held**, since `PlayerFollowers` is one-per-player.
  Gary's "capturing again replaces it" rule survives; a future "box" does not.
- It must happen **out of combat or at combat end**, because the mid-fight join is blocked (Q1).
  Capture becomes "tag it now, it joins when the fight ends" — a design change, not a port.

**Recommendation:** keep `CF_POKE_*` for capture. It is not the risky part of this system.

---

## Q6 — What breaks

Assuming the feasible shape (persistent follower, out-of-combat acquisition):

| File | Fate |
|---|---|
| `TrainerPartnerPersistence.cs` (738 lines) | **Mostly deleted.** `CanonicalBall` arbitration, `ResolveSlot`, the HP/MAXHP/DOWNED record and `RegisterSummon` are replaced by `PlayerFollowers` + the follower entity's own `CurrentHealth`. `KeyConfig` may survive as the "which line is loaded" marker. |
| `TrainerPartnerPatches.cs` (228 lines) | **Deleted**, except the town-revive half — and that half becomes *harder*, because there may be nothing left to revive (Q2 loss #1). |
| `SummonLeakPatches.cs` (420 lines) | **Deleted** for partners. Its docstring already notes followers are exempt: `TryCreateSummon` does not set `eActorProperties.SUMMON` for `COMPANION` (`CombatHelper.cs:242-245`). **But Vampiric's bat swarm still needs it** — do not delete outright. |
| `SummonVisuals.cs` (403 lines) | **Probably deleted for partners.** `DrawNewSummon` exists because `ADD_CHARACTER` looks up an actor GameObject the caller must have created; a follower present at combat start is built by `CombatPhase.Initialize`'s normal ally path. **Verify before deleting** — the single most likely place for an invisible-creature regression. Also still needed by Vampiric. |
| `TrainerPartnerNicknames.cs` (377 lines) | **Survives, re-targeted** — it currently overrides a summon's species display name; it would override a follower's. `DisplayName` is in `_ignorePropertyNames`, so hash-free either way. |
| `TrainerPartnerAutonomy.cs` (752 lines) | **Needs review.** A follower has vendor AI (`FollowerCharacterConfig.Behaviour`, `eAiBehaviours`). Whether our autonomy layer composes with or fights that is unassessed, and it is adjacent to `AiDrawNeutrality` — RULE 14 territory. |
| `TrainerPartnerPanel.cs` (254 lines) | **Likely deleted** in favour of `FollowerDetailViewHelper`. |
| `TrainerCaptureRules.cs` / `ExecCapture` | **Unchanged** (Q5). |
| `SUMMON` recipes in `CF_PACK_ORIGINALS/skillrecipes.json` | The file has **14**, not 15. 13 are Trainer (`FIRE/GRASS/WATER _1.._4` plus `CAPTURE_1`) and become follower grants — a different primitive, not a `SummonType` edit. **`SKILL_CF_VAMPIRIC_BAT_SWARM` is a genuine disposable summon and must stay `SUMMON`.** |
| `visualfallbacks.json` | The 12 `UNARMED_*` rows exist to give the borrowed monster configs a weapon model. Those configs are unchanged by the migration, so **these rows stay as-is**. |
| `RecipeActionExecutor.ExecSummon` | Keeps its `SUMMON` path for Vampiric; the Trainer branch (`partnerSlot`, `RegisterSummon`, `ResolveConfigFromItem`) comes out. A **new** `GRANT_FOLLOWER`-style primitive is added — new surface that must be argued against every rule in the checklist. |
| **New:** ~13 follower rows | `Type: COMPANION`, `ContractRounds: 0`, `ConfigName` = the existing monster config, `SMN_`/`CF_`-prefixed ids. Pure data, RULE 1's preferred shape. |

Rough shape: **~1,500-1,800 lines deleted, ~13 JSON rows added, one new runtime primitive and one
lifecycle patch (`CombatPhase.Initialize`) added.** The lifecycle patch is where the argument for
migrating starts eating itself.

---

## If you go ahead — staged path, smallest risk first

Each stage is independently shippable and independently revertible.

**Stage 0 — decide the DOWNED question first.** Everything else is contingent. Either (a) accept that a
partner at 0 HP is permanently lost — a real, arguably thematic downside, and zero patches; or (b)
patch `CombatPhase.Initialize` to exempt our partners, and accept owning follower lifecycle. Do not
start until this is answered. *Verify:* nothing to verify; this is a product decision.

**Stage 1 — data only, no behaviour.** Add the ~13 `COMPANION` follower rows through the existing
Summoner pack loader. Nothing references them. *Verify:* plugin log shows adds-only merge counts;
`Configs.Followers` contains the ids; parity `DataHash` changes on both peers (RULE 12); a run boots
and plays identically. Zero gameplay risk.

**Stage 2 — grant one partner out of combat, behind a config knob, default OFF.** A single
`FollowerHelper.JoinFollower(trainer, CreateFollowerCharacter(...), env, adventureRandom, -1, false)`
at a controlled moment. *Verify:* screenshot of the partner on the overworld **and** on the combat
board with correct species art and name (`VERIFICATION-METHOD §2` — identity tell + effect tell, one
frame); `PlayerFollowers` populated in state-v2; **RULE 21 two-run same-seed diff green**, because
`CreateFollowerCharacter` draws.

**Stage 3 — HP across fights, and the downed outcome chosen in Stage 0.** *Verify:* paired
before/after frames from one pinned-seed fixture showing carried HP; then deliberately kill the partner
and screenshot what happens on the next combat entry. This is where (a) vs (b) proves out.

**Stage 4 — migrate ONE line (say GRASS) off `SUMMON`.** Leave FIRE and WATER on the summon path as a
live control. *Verify:* both classes of partner in one fixture; two-run determinism diff; fail-safety
sweep.

**Stage 5 — migrate the rest, then delete.** Only after Stage 4 has held for a full battery. Delete
`TrainerPartnerPatches` and most of `TrainerPartnerPersistence` **last**, and keep
`SummonLeakPatches` / `SummonVisuals` for Vampiric.

**Never do:** switch a recipe's `SummonType` to `AS_FOLLOWER`. That is the one move this document
rules out outright (Q1).

---

## `unverified` — blunt

- **U-A. Nothing here was observed on screen.** This is static analysis of a decompile. Per
  `feedback_ftk2_ui_validation`, none of it is proven until an MCP screenshot shows it. In particular
  **U3 is resolved in code and still unproven visually**: `CombatPhase.cs:273` puts followers in
  `_allyEntities`, but no one has looked at a fight with a follower in it.
- **U-B. The `int.Parse` throw at `CombatHelper.cs:2255` is read, not executed.** It is an unguarded
  `int.Parse` on `"TRAINER"` and I am confident, but the deciding fact of this document rests on a
  decompiled line, not a stack trace. **Cheapest confirmation, and it should happen before the
  operator commits:** one throwaway `AS_FOLLOWER` recipe fired once from `CF_ORIG_TRAINER` on a
  scratch fixture, expecting a `FormatException` in the log.
- **U-C. `FollowerHelper.IsSpecialFollower` is unexamined.** It is the only exemption from the
  delete-on-death rule (`CombatPhase.cs:262`, `FollowerHelper.cs:206`). If it is data-driven it might
  make Stage 0 option (b) cheap. I did not read it.
- **U-D. Exact draw counts are not measured, only enumerated.** Q4 says the follower path draws *more*
  and *variably*; it does not say how many. Only the RULE 21 two-run diff with `LogCalls(true)` gives
  real numbers.
- **U-E. Whether `AvatarComponent` creation can differ between installs is unknown.** The Q4 concern
  (prefab body-type and colour-scheme branching the draw count) assumes prefabs could differ across
  peers with different mod sets. Unverified — and it applies to the **existing** summon path too if
  anything on it builds an avatar.
- **U-F. `TrainerPartnerAutonomy` vs `FollowerCharacterConfig.Behaviour` is unassessed.** 752 lines
  next to the AI layer, and RULE 14 says AI is the highest residual risk area we have.
- **U-G. The double `JoinFollower` (`CombatHelper.cs:214` and `:2247`) was not traced for side
  effects.** Only relevant if the mid-fight path is ever revisited.
- **U-H. Whether the combat checkpoint hash fires often enough to catch a summon divergence is
  unmeasured.** `CombatPhase.cs:8674` sits in one specific ability-resolution path; I did not
  establish that every summon is followed by a checkpoint before the next divergence-sensitive draw.
  "Summons are hashed" is true; "summons are hashed promptly" is not established.
- **U-I. Party-slot behaviour with 4 followers was not exercised.**
  `MakeRoomForSupportCharacters`'s 8-slot trim is read, not observed.
