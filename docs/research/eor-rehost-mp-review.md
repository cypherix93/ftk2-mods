# Adversarial MP-correctness review — `engine/eor-rehost`

**Date:** 2026-07-25 · **Scope:** all engine code new since `6c46d23` (Waves 0–3, HEAD `d61bc05`)
**Contracts:** `docs/MULTIPLAYER.md` R1–R5 · charter engineering rules
(`docs/superpowers/plans/2026-07-25-eor-rehost-engine-charter.md` §"Engineering rules") ·
`FTK2.ClassForge/SPEC-DELTA-v1.1.md` determinism invariants ·
`docs/research/eor-0760-content-audit.md` §3 failure catalog.
**Posture:** adversarial. Every finding below was verified against the code and, where it concerns a game
surface, against `tools/out/decompile/FTK2/`. Claims I could not turn into a concrete divergence story are
filed as NOTE.

---

## 0. Verification of record

Because several findings contradict assumptions written into the code's own doc-comments, here is what was
checked directly against the decompile rather than taken from a design doc:

| Assumption in new code | Where asserted | Verdict against decompile |
|---|---|---|
| `_handleNetworkAction` carries string-keyed custom payloads | `MULTIPLAYER.md` L10-12; `ParityPatches.cs:24` | **False.** `AdventureDirector.cs:15166` — one declaration, `protected override async Task _handleNetworkAction(GameAction pGameAction)`. |
| `_trySendNetworkAction` has string parameters to carry an action key + payload | `ParityTransport.cs:114-127` | **False.** 8 overloads at `AdventureDirector.cs:15100-15135`; **zero** string parameters in any of them. |
| No host/session flag has been identified | `ParityCoordinator.cs:14-20, 55`; DevKit SPEC §11.8 | **False.** `NetworkData.IsHost` (`NetworkData.cs:26`) and `NetworkData.PlayingOnlineMultiplayer` (`:61`) are plain public bools, read in `AdventureDirector`, `AdventureSelectionDirector`, `CombatPhase`. |
| Trait pick is serialized as a raw pool **index** | `TraitLoadoutPatches.cs:26-34` | **True, and worse than stated.** `PartyManagementDirector.cs:3901` writes `IndexOf`; `:4184/4186/4201/4203` read `_loadOutItemThingsPool[thingIndex]` unguarded. |
| `CombatState.Random` is the shared lockstep stream | `GameAdapters.cs:466-483` | **True, and the game polices it.** `GameAction.DesyncDetectionData{Hash, GameRandomNextInt}`; `NetworkData.DoMonitorForDesyncs / HasADesyncBeenDetected / LatestGameRandomDesyncIndex / DebugDesyncGameRandomData`. A separate visual stream exists (`GameRandom.NextFloatVisual/NextChanceVisual`) — the engine correctly uses only the gameplay entry points. |

The last row matters for severity calibration throughout: **the game itself compares `GameRandom` draw
results across peers per action.** Any asymmetric draw this engine takes is not a silent drift — it trips
the vendor's own desync detector.

---

## BLOCKERS

### B0 — Sibling `dataHash` values are missing the `sha256:` prefix, so every comparison is a forced mismatch.

**Files:** `FTK2.DevKit/src/DevKit.Core/DataHasher.cs:52, 81-95, 151`;
`FTK2.ClassForge/src/ClassForge.Core/DataHasher.cs:44`;
`FTK2.Summoner/src/Summoner.Core/Parity/DataHasher.cs:48`

DevKit's canonical hash is `HashPrefix + ToHex(...)` = `sha256:` + 64 hex (`:151`), and
`IsWellFormedHash` requires **both** the prefix and `hash.Length == HashPrefix.Length + 64`
(`:84-86`). `ParityRegistration.HasWellFormedDataHash` (`:67-70`) delegates to it, and
`ParityComparer.CompareOne:191-192` turns that into a hard verdict:

```csharp
bool hashUnusable = !l.HasWellFormedDataHash || !r.HasWellFormedDataHash;
bool dataDiffers  = hashUnusable || !string.Equals(l.DataHash, r.DataHash, StringComparison.Ordinal);
```

Both sibling hashers return **bare 64-char hex with no prefix** (ClassForge `:44` `return hex.ToString();`,
Summoner `:48` `return ToHex(hash);`). A repo-wide grep finds the literal `sha256:` **only inside DevKit
and DevKit's own tests** — and those tests use hardcoded constants
(`ComparerTests.cs:7-8`, `HandshakeTests.cs:8-9`) rather than a real hasher output, which is exactly why
the suite is green while the integration is broken.

**Failure scenario:** Host and client both run ClassForge 0.1.0 with byte-identical `ClassPacks/`. The
handshake compares two *identical* strings, `hashUnusable` is `true` because neither is 71 chars, so
`dataDiffers` is forced true and the verdict is `DataMismatch`. DevKit dispatches `ParityFailed` →
`ParityBridge.cs:146-147` latches `_blocked` → `FeaturesActive` goes false → the recipe engine, class-select
injection, trait-loadout injection and all further merges shut off. **ClassForge is non-functional in
correctly-configured multiplayer**, and because only the peer that *receives* a snapshot latches (M3), it
shuts off on one side only — one peer keeps mutating combat state through `RecipeActionExecutor` while the
other has stopped.

Note the interaction: B1/B2 currently mask this (nothing is ever compared), so B0 is latent *today* and
fires the instant the transport is fixed. It must be fixed in the same change as B1/B2, not after.

**Minimal fix:** one line in each sibling hasher — emit `"sha256:" + hex`. Better: have both call
`ParityService.ComputeDataHashForFolder` by reflection; that BCL-typed surface exists precisely so siblings
do not reinvent this (`ParityBridge` already demonstrates the no-compile-time-dependency pattern). Add an
`IsWellFormedHash`-shaped assertion to both mods' hasher tests, and one DevKit test that round-trips a
*real* hasher output rather than a constant.

---

### B1 — The parity handshake cannot transmit. R1 is unenforced end-to-end.

**File:** `FTK2.DevKit/src/DevKit.Plugin/ParityTransport.cs:101-127`

`Resolve()` looks up `AdventureDirector._trySendNetworkAction` with no `argumentTypes`, then scans the
resolved method's parameters for the first two `string`s:

```csharp
if (_sendParameters[i].ParameterType != typeof(string)) continue;
...
if (_actionKeyParameterIndex < 0) { ... _sendDisabled = true; return false; }
```

All eight overloads (`AdventureDirector.cs:15100-15135`) are shaped
`(eAdventureActions, Entity, …)` / `(eTownServiceTypes, Entity)` / `(int)`. **None has a `string`
parameter.** So `_actionKeyParameterIndex` stays `-1`, the "Target found but unusable" branch fires, and
`_sendDisabled = true` for the process. Separately, `AccessTools.Method(type, name)` against eight
overloads is an ambiguous lookup and is not a stable way to reach any of them.

**Failure scenario:** Two peers, different `ClassPacks/` folders. Both call
`ParityCoordinator.OnSessionStarted` → `ParityTransport.Send(snapshot)` → returns `false` → each logs
"this peer is invisible to the parity handshake this session" and returns *before* even sending the
REQUEST. Neither peer ever receives a foreign snapshot, so `ParityService.HandleSnapshot` never runs, no
`ParityVerdict` is ever produced, `ParityBridge.OnParityFailed` never fires, `ParityBridge.Blocked` stays
`false`. Both peers play on with divergent `Configs`, divergent trait pools and divergent recipe books.
Silence is indistinguishable from "parity OK".

**Minimal fix:** The wire format is a closed ProtoBuf hierarchy — `GameActionDataBase` is
`[ProtoInclude(1..23)]` with no runtime extension point — so a bespoke string action is not available.
Route the payload through an existing carrier's free-form slot instead: `_createAdventureAction(...)`
passes `object pResultArgs` into `AdventureActionData.Create`, and `_trySendNetworkAction(eAdventureActions,
Entity, object pData)` (`:15115`) is the reachable overload. Bind that overload **by explicit
`argumentTypes`**, pick a benign `eAdventureActions` member, and carry the payload in `pData`. Until that
is proven on a live session, DevKit must **fail loud, not quiet**: if `CanSend` is false while
`NetworkData.PlayingOnlineMultiplayer` is true, every registered mod should be driven into its mismatch
path rather than left at "no verdict".

---

### B2 — The parity receive hook can never observe a payload.

**File:** `FTK2.DevKit/src/DevKit.Plugin/ParityPatches.cs:24-43`

```csharp
string candidate = __args[i] as string;
if (string.IsNullOrEmpty(candidate)) continue;
ParityCoordinator.OnNetworkPayloadObserved(candidate);
```

`_handleNetworkAction` takes exactly one argument, a ProtoBuf `GameAction`
(`AdventureDirector.cs:15166`, `GameAction.cs`). `__args[0] as string` is always `null`.

**Failure scenario:** Even if B1 were fixed on the send side, the receiver would drop every payload. The
handshake is dead in both directions independently; fixing one without the other changes nothing.

**Minimal fix:** Inspect `pGameAction.Data` (a `GameActionDataBase`) and reach the free-form member the
send side chose — for `AdventureActionData` that is the deserialized `pResultArgs` slot. Keep the prefix
`void` (that part is right, and is the correct answer to the EOR `_handleNetworkAction`-returns-`false`
hazard in audit §3.4).

---

### B3 — Trait-loadout injection is structurally un-gateable by the handshake, and the pick is index-serialized.

**Files:** `FTK2.ClassForge/src/ClassForge.Plugin/TraitLoadoutPatches.cs:60-142`;
`FTK2.DevKit/src/DevKit.Plugin/DevKitPlugin.cs:110`; `ParityPatches.cs:45-56`

The injection postfix hangs on `LootDropHelper.GetAdventureLoadOut`, whose only callers are
`PartyManagementDirector.cs:176` (party-setup screen build) and `:4270` (join-in-progress rebuild). DevKit
runs the entire handshake from an `AdventureDirector.Initialize` postfix — i.e. **after** party setup has
already built and serialized the pool. `ClassForgePlugin.FeaturesActive` therefore cannot be `false` at the
moment the pool is constructed, no matter how well the handshake works.

The consequence is not cosmetic. `PartyManagementDirector.cs:3901`:

```csharp
((PartyManagementActionData.TakeUntakeItemActionData)actionBase).ThingIndex = _loadOutItemThingsPool.IndexOf(pThing);
```

and the receiving side, `:4184` / `:4186` / `:4201` / `:4203`:

```csharp
_loadOutItemThingsPool[thingIndex2]      // raw index, no bounds check
```

**Failure scenario:** Peer A has `CF_PACK_EOR_CLASSES` (20 `TRAIT_*` ids injected), peer B does not. A's
pool is length `n+20`, B's is length `n`. A clicks the trait at pool index `n+7`; the action broadcasts
`ThingIndex = n+7`; B evaluates `_loadOutItemThingsPool[n+7]` → **`ArgumentOutOfRangeException` inside
`_handleNetworkAction`** on B. In the milder case (B has *some* pack traits but a different set, so lengths
happen to match) B silently equips a completely different Thing than A did, and the parties diverge from
turn one.

Note the postfix's own determinism work is correct in isolation — ordinal sort, unconditional append, no
draws taken (the vanilla body consumes `pRandom`; the postfix does not), deterministic `Thing.Id`. The
defect is that its correctness rests entirely on R1, and R1 has no enforcement point that runs early
enough.

**Minimal fix:** Gate the injection on a check that is available *before* party setup, not on the
post-hoc handshake: read `Env.NetworkData.PlayingOnlineMultiplayer` and refuse to inject unless parity has
positively succeeded (not merely "not yet failed"). Move the handshake to a session-entry point that
precedes `PartyManagementDirector` — `AdventureSelectionDirector` already branches on
`PlayingOnlineMultiplayer && IsHost` at `:137/:169/:351` and is the natural anchor.

---

### B4 — `enabledFeatures` omits every feature knob, so a knob difference reports "Parity OK".

**File:** `FTK2.ClassForge/src/ClassForge.Core/ParityRegistrationBuilder.cs:14-27`

```csharp
EnabledFeatures = result.EnabledOrderedPacks.Select(p => p.Id) ... .ToArray()
```

The tuple carries **enabled pack ids only**. `EnableTraitLoadoutInjection`,
`EnableRecipeEngine`, `EnableClassSelectInjection`, `EnableIconFallback` and the master `Enabled`
(`ClassForgePlugin.cs:32-50`) are all absent. `ParityComparer.CompareOne`
(`ParityComparer.cs:186-200`) therefore returns `Match` for two peers whose runtime behaviour differs.

Per-pack `<PackId>.Enabled` knobs *are* covered (they change `EnabledOrderedPacks`, which changes both the
feature list and `DataHash`) — this finding is specific to the five feature knobs.

**Failure scenario (recipes, the sharper one):** Peer A and B have byte-identical packs. B set
`[Skills] EnableRecipeEngine = false` to debug something and forgot. Handshake (once B1/B2 are fixed)
compares `{guid, version, dataHash, [CF_PACK_EOR_CLASSES]}` on both sides → `Match` → no warning.
In combat, a `ProcChance: 35` recipe becomes eligible on A: `RecipeDispatcher.EvaluateRecipe`
(`RecipeDispatcher.cs:341`) calls `_rng.NextChance(0.35m)` → **one draw from
`CombatState.Random`**. `RecipeEngineHost.TryBegin:75` short-circuits on B → **zero draws**. The shared
stream is now off by one call on A for the rest of the combat. Every subsequent vanilla roll differs, and
the game's own `GameAction.DesyncDetectionData.GameRandomNextInt` comparison flags it — but only *after*
the divergence has already propagated.

**Failure scenario (traits):** same setup with `EnableTraitLoadoutInjection` → pool lengths differ →
exactly the B3 index crash, now reachable with matching pack sets.

**Minimal fix:** Include the feature knobs in `EnabledFeatures` (they are already a sorted `string[]`;
append `"TraitLoadout"`, `"RecipeEngine"`, `"ClassSelect"`, `"IconFallback"` when on). `EnableIconFallback`
is genuinely R4-exempt and could be excluded — but it costs nothing to include and the R4 exemption is
easier to argue in the SPEC than to re-derive at a bug report.

---

### B5 — Emitted pack content is a function of an uncommitted, install-derived vocab snapshot.

**Files:** `tools/eor_import.py:1107-1111` (`--vocab`, default `tools/out/vocab-index.json`), `:133`
(`vocab_missing` ∈ `_ABORT_KINDS`), `.gitignore:18` (`tools/out/` ignored)

The vocab index is generated by `tools/extract_vocab.py` from a **live Steam install**
(`docs/research/enum-ground-truth.md:11`) and is not in the repo. It is not merely a validation input — it
changes the emitted bytes and the resulting gameplay. Measured against the real 417-item corpus:

| Consumer | Line | Install-dependent effect |
|---|---|---|
| `pick_visual_fallback` | `:358-385` | **57 of 417** items take `VisualFallback` from `sorted(vocab["VisualDonors"])[0]` |
| `filter_tags` | `:404-425` | **5,100 tag instances (166 distinct)** survive only because the tag is in `vocab["Tags"]` |
| `repair_class` | `:343-355` | a Class absent from `vocab["Classes"]` and from `CLASS_REMAP` → **item dropped entirely** |

**Failure scenario:** Operator A (all DLC, EOR still installed — the module docstring at `:11-16` concedes
the snapshot may itself be EOR-contaminated) regenerates the packs and gets 417 items with tag set X.
Operator B (base game only) regenerates: their `vocab["Tags"]` lacks the DLC tags so `filter_tags` strips
them, their thinner `VisualDonors` list makes `sorted(...)[0]` select different donors, and Classes missing
from their install drop items outright. Tags drive loot pools and queries, so this is a gameplay
divergence, not just a byte divergence — and it produces a different `dataHash`, so with a working
handshake it also produces a permanent parity mismatch between two operators who both "just ran the
converter". `sorted()` makes the *selection* deterministic; the *list* is the non-determinism.

**Minimal fix:** commit the vocab as a versioned, reviewed input (`tools/vocab/vocab-index-<gamever>.json`,
un-ignored), record its SHA-256 in each pack's `provenance.json`, and have `eor_import` refuse to run
against an unpinned or mismatched vocab.

---

### B6 — The committed ClassForge pack is not what the converter produces; regenerating destroys gameplay data.

**File:** `tools/eor_import.py:919-926` (`emit_class_pack`), `:906-916` (`pack.json` writer)

Regenerating from the real EOR package reproduces the Armory packs (2 files) and the Summoner packs
(8 files) **byte-identically**. It does not reproduce ClassForge — all four converter-written files differ:

| File | Committed | Regenerated | Loss |
|---|---|---|---|
| `classes.json` | 24,927 B | 22,250 B | **28 of 31 classes lose their `SKILL_CF_*` signature passives — 38 dropped** |
| `localization/en.json` | 7,165 B | 4,528 B | **40 `TRAIT_*` loc keys deleted** — all 20 traits become unnamed |
| `provenance.json` | 19,917 B | 3,619 B | 1 entry deleted |
| `pack.json` | v1.1.0 | v1.0.0 | description reverted (`:906-916` hardcodes v1.0.0) |

`traits.json` and `skillrecipes.json` are hand-authored and the converter never writes them — but it does
clobber the `classes.json` `Passives` arrays and the localization keys those two files depend on.
`emit_class_pack` is a blind overwrite with no merge and no drift check.

**Failure scenario:** Operator B follows the documented workflow and runs the converter. Their pack now has
28 classes missing their signature skills and 20 unnamed traits, while `skillrecipes.json` still defines
the now-orphaned `SKILL_CF_*` recipes — so `RecipeDispatcher.Holds` (`RecipeDispatcher.cs:266-274`) never
matches an owner and every one of those recipes silently never fires. Meanwhile `classes.json`,
`pack.json` and `provenance.json` all shift, so B's `dataHash` differs from A's: a guaranteed R1 mismatch
on top of a silent gameplay regression.

**Minimal fix:** stop the converter from owning files that carry hand-authored content — either
read-modify-write to preserve `Passives`/loc keys, or move the authored layer into a separate pack the
converter never touches. Add a CI test asserting the committed packs equal a fresh regeneration; its
absence is exactly why this went unnoticed.

---

### B7 — Summoner ships no parity enforcement at all; its `OnParityMismatch = Block` knob is decorative.

**Files:** `FTK2.Summoner/src/Summoner.Plugin/SummonerPlugin.cs:53-56, 64-67, 142-148`;
`FTK2.Summoner/src/Summoner.Plugin/Parity/ParityRegistration.cs:19-22, 41-49`

`SummonerPlugin` binds `[Multiplayer] OnParityMismatch` defaulting to `"Block"`. Its **only** consumer
(`:64-67`) logs a warning when the value is *not* `Block`. The value is never parsed, never handed to
`ParityService`, never acted on. `Parity/ParityRegistration.cs:41-49` deliberately resolves the
callback-less `Register(string,string,string,string[])` and never `RegisterWithCallback`; `:19-22`
documents the decision as "Summoner has no ParityFailed pathway". So Summoner cannot learn that it
diverged, cannot enter SafeMode, and cannot block.

DevKit's own `Block` is equally inert: `_sessionBlocked` (`ParityService.cs:60`, set at `:560`) is exposed
only via `IsSessionBlocked()` (`:348`), which has **zero non-test callers** repo-wide. Charter rule 5
requires every engine to register *and* the SPEC'd policy to be enforced; Summoner satisfies neither.

Registration is also inside `MergeInto` (`:142-148`), which `ConfigsMergePatches.cs:18` short-circuits when
`[General] Enabled=false` — so a peer with Summoner installed but disabled registers **nothing** and is
invisible rather than reported as divergent.

**Failure scenario:** Peer A runs Summoner with `SMN_PACK_EOR_MERCS`; peer B has it installed but
`Enabled=false`. On A, `ConfigsSink.cs:33` writes `SMN_MERC_*` into `configs.Followers`. A hires that
follower in town; the hire replicates through the vanilla pipeline as a config-id string; B looks up
`Configs.Followers["SMN_MERC_…"]` and finds nothing. The handshake would report `MissingRemote` — and then
print a log line and continue. R1's "Enforcement, not hope" is hope.

**Minimal fix:** use `RegisterWithCallback` and latch a `Blocked` flag on any non-`Match` row; register
from `Awake()` unconditionally (with `DataHash = "(disabled)"` when the mod is off) so a disabled peer
still appears in the handshake; and either wire `IsSessionBlocked()` into a real session gate in DevKit or
remove `Block` from the policy enum rather than shipping an option that does nothing.

---

## MAJORS

### M0 — ClassForge has no adds-only enforcement at merge time; a pack can overwrite vanilla content.

**Files:** `FTK2.ClassForge/src/ClassForge.Core/MergePlanner.cs:60-69`;
`FTK2.ClassForge/src/ClassForge.Plugin/ConfigMergePatches.cs:113-144`

`MergeCategory` warns only on **pack-vs-pack** collisions and then writes unconditionally:

```csharp
target[kv.Key] = new MergeOp(kv.Key, kv.Value, packId);   // :68 — no vanilla check, no id-shape gate
```

`ApplyPlan` then does `configs.Characters[op.Id] = …` / `configs.Things[op.Id] = …` /
`configs.Abilities[op.Id] = …` (`:119/:129/:139`) with no check against what is already there. The only
prefix check anywhere in ClassForge.Core is `ManifestParser.cs:37`, and it validates the **pack** id
(`CF_PACK_`), not entry ids. **A ClassForge pack containing the id `KNIGHT` silently replaces the vanilla
Knight `CharacterConfig`.**

Compare Summoner, which gets this right: `Summoner.Core/Merge/MergePlanner.cs:50-60` enforces a hard
`^SMN_[A-Z0-9_]+$` id shape plus a `known.Contains(id)` skip. Armory has no C# at all and therefore no
enforcement of any kind.

The convert-time gate (`eor_import.py:975-991`, `check_id_collisions` against
`vocab["AllIds"] | vocab["ItemIds"]`) runs against the same uncommitted, install-derived, self-admittedly
contaminated snapshot as B5, and it only covers ids this converter emits — a hand-authored pack
(`traits.json` and `skillrecipes.json` prove those exist) bypasses it entirely.

**Failure scenario:** a pack ships an entry named `KNIGHT`. Peer A has the pack, peer B does not. A's
vanilla Knight now has different `Stats`/`Passives`/`Things`, so identical combat inputs produce different
outputs on the two peers from the first Knight action. `dataHash` does cover the pack file, so a working
handshake would catch it — but per B1/B2 no handshake works, and per M2 Block would not unmerge it anyway.

**Minimal fix:** port Summoner's `PlanEntry` gate into `ClassForge.Core/MergePlanner.cs` — reject ids not
matching `^(CF_|TRAIT_|SKILL_CF_)` and skip ids already present in the live `Configs`, emitting a
`Finding.Error` for each.

---

### M1 — `ParityBridge.Blocked` is process-latching while DevKit's block flag is per-session.

**File:** `FTK2.ClassForge/src/ClassForge.Plugin/ParityBridge.cs:46, 146-149`

`_blocked` is set once and never cleared; there is no reset path anywhere in ClassForge. DevKit's own
`ParityService.ResetSession()` (`ParityService.cs:357-369`) explicitly clears `_sessionBlocked`,
`SafeModeGuids` and `RemoteSnapshots` on every `OnSessionStarted`. The two lifetimes disagree.

**Failure scenario:** Peer A joins session 1 with a stale pack, blocks. Everyone fixes their packs. Session
2 starts in the same process. DevKit clears its session state and reports `Match` for every mod. But
`ParityBridge.Blocked` is still `true` on A, so A runs with the recipe engine off, no trait injection and
no class-list injection, while every peer that relaunched runs with all of them on — and the handshake now
actively certifies that they match. This is worse than the original mismatch: it is an undetected
asymmetry blessed by a green verdict.

**Minimal fix:** Have `ParityBridge` reset `_blocked` on session start (DevKit can expose the transition,
or ClassForge can hook the same `AdventureDirector.Initialize` postfix), and re-derive it from the new
session's verdicts.

---

### M2 — Block never prevents a merge; it only produces a half-shutdown, and asymmetrically.

**File:** `FTK2.ClassForge/src/ClassForge.Plugin/ConfigMergePatches.cs:39-48`

```csharp
if (ParityBridge.Blocked) { ...skipped... return; }
```

with the comment at `:40-41` conceding "Content already merged earlier in the session stays merged". Given
B3's ordering fact, this is stronger than a concession: the merge runs from the `ConfigsHelper.LoadConfigs`
postfix at boot, `ConfigsHelper.ReloadConfigs` has **no vanilla caller** (verified: the only occurrence in
the decompile is the declaration at `ConfigsHelper.cs:398`), and Block can only latch at
`AdventureDirector.Initialize`. **The Block branch is unreachable in practice.**

So Block does not do what `ParityBridge.cs:162-169`'s banner says it does. It leaves `Configs` fully merged
and turns off only the runtime patches. SPEC-DELTA-v1.1 §5.3 argues at length that a partial shutdown is
exactly the asymmetric execution the invariants exist to prevent — and that is what Block produces, except
across peers rather than within one process.

**Failure scenario:** A blocks, B does not (one-sided detection is the normal case per M3). A's `Configs`
still contain every pack class and `TRAIT_*` ThingConfig, so a saved character using `CF_EOR_HEXBLADE`
loads fine — but A's recipe engine is off, so `SKILL_*` passives on that character no-op on A and proc on
B. Divergent combat state within one round.

**Minimal fix:** Make Block honest — either it must be able to prevent the merge (which requires the
handshake to run before `LoadConfigs`, i.e. a launch-time exchange, not a session-time one), or the banner
and SPEC must say plainly that Block = "runtime features off, content stays merged" and the SafeMode
definition in the SPEC §9.5 table must be rewritten to match.

---

### M3 — Host detection hardcoded to `false`, killing the late-join path and making Block one-sided.

**File:** `FTK2.DevKit/src/DevKit.Plugin/ParityCoordinator.cs:55` (`ParityService.SetIsHost(false)`),
rationale at `:14-20`.

The stated rationale — "no networking/session-state flag has been identified yet" — is refuted by the
repo's own decompile (`NetworkData.cs:26` `public bool IsHost;`, `:61` `public bool
PlayingOnlineMultiplayer;`). Downstream:

- `ParityService.HandleRequest:507-517` — `if (!isHost) { log("ignored (not host)"); return null; }`. The
  `FTK2MODS_PARITY_REQUEST_V1` path is dead code on every peer.
- `ParityService.HandleSnapshot:604` — `if (isHost && !alreadyReplied) return BuildSnapshotPayload();`
  never fires, so no peer ever answers another peer's snapshot.

The only way a peer learns about another is that other peer's own `AdventureDirector.Initialize`
broadcast.

**Failure scenario:** The game supports join-in-progress (`NetworkData.JoinInProgressData
.IsJoinInProgressPlayer`, `JoinInProgressGameActionIndexOffset`, `QueuedJoinInProgressSaveEvents`). C joins
an in-flight session with a divergent pack set. C's `Initialize` fires and C broadcasts; A and B receive it,
compare, and Block. A and B already ran `Initialize` long ago and never re-broadcast, and nobody answers
C's REQUEST because nobody thinks it is host. **C never blocks.** A and B now have the recipe engine and
trait injection off; C has them on. Per M2 nothing was unmerged. The session is in exactly the asymmetric
state the whole mechanism exists to prevent, and C's log says nothing at all.

**Minimal fix:** `ParityService.SetIsHost(_env.NetworkData.IsHost)`, refreshed on session start, and gate
the whole handshake on `PlayingOnlineMultiplayer` so single-player takes no code path at all. Correct the
DevKit SPEC §11.8 open question — it is answered.

---

### M4 — `_healOrigin` is a process-global gameplay static with an exception-shaped leak (charter rule 4).

**File:** `FTK2.ClassForge/src/ClassForge.Plugin/Recipes/CombatHookPatches.cs:48`, written at `:267-268`,
restored at `:281`.

It is the only gameplay-relevant static in the new code that is neither per-combat-keyed nor reset. (The
per-battle runtime is genuinely well handled — `RecipeEngineHost._cachedState/_cachedSeed/_cachedDispatcher`
at `:46-48` drop-and-reallocate on a new CombatKey (`SyncCombat:119-135`), and `RecipeStateStore.Sync`
(`CombatRuntime.cs:163-173`) does the same one level down. That is a real, structural fix for the EOR
`SteadyAimUsedThisCombat` class — audit §3.)

The save/restore discipline relies on `ApplyStatChange_Postfix` running. **Harmony does not run a postfix
when the original method throws.** `InteractableHelper.ApplyStatChange` is a large method with plenty of
throw surface.

**Failure scenario:** `ApplyStatChange` throws once (a malformed status config, a null component on an
edge-case entity). The postfix never runs, `_healOrigin` stays pinned to that combat's origin `Entity`
forever — across combat end, across the run, for the process lifetime. Every later `AddHealth`
(`AddHealth_Prefix:381,387`) resolves `Healer` to that stale entity, so a `HEAL_MODIFIER {Scope: GIVEN}`
recipe matches on the wrong healer and mutates `ref int pValue` for heals it should not touch. On a peer
where the exception did not occur, it does not. Divergent HP on the next heal.

**Minimal fix:** Move the healer slot onto `CombatRuntime` (keyed by CombatKey, so it dies with the
combat), or at minimum clear `_healOrigin` inside `RecipeEngineHost.SyncCombat` whenever the CombatKey
changes. A `try/finally` inside the prefix does not help — the restore has to survive the *original*
throwing, which a prefix cannot see.

---

### M4b — One transport failure blinds a peer for the whole process, and the payload is uncapped.

**Files:** `FTK2.DevKit/src/DevKit.Plugin/ParityTransport.cs:36, 79-82, 88`;
`FTK2.DevKit/src/DevKit.Core/ParityPayloadCodec.cs:62`

Any exception from the reflection invoke sets `_sendDisabled = true` **for the process** (`:79-82`), and
`Resolve()` short-circuits on it forever (`:88`). `ParityCoordinator.cs:71-77` logs one warning and returns.
There is no per-session retry. Separately, `EncodeSnapshot` sizes its `StringBuilder` at
`128 + count*160` but caps nothing; `EnabledFeatures` carries the full pack-id list for both ClassForge
(`ParityRegistrationBuilder.cs:21-25`) and Summoner (`SummonerPlugin.cs:144-145`). `MULTIPLAYER.md` open
question 5 (payload size limits) is still open.

**Failure scenario:** 30 mods, ClassForge with 40 packs → roughly a 10 KB payload. If the game's transport
rejects or throws on an oversized action, `_sendDisabled` latches and that peer is invisible to the
handshake for the rest of the process — and **everything then looks healthy**, because no snapshot ever
arrives from it to compare against. Silent failure of the whole R1 mechanism in exactly the configuration
(many mods, many packs) where parity matters most. This is the same "silence == OK" failure mode as B1,
reachable even after B1 is fixed.

**Minimal fix:** cap the payload (~4 KB) and chunk with `Index`/`Total` members, or replace the feature
list with a hash of it. Do not latch `_sendDisabled` for the process — retry per session, and treat
"cannot broadcast" as a parity **failure with a banner**, not a log line.

---

### M5 — Two divergent `dataHash` implementations; ClassForge's hashes PNGs as UTF-8 text.

**Files:** `FTK2.DevKit/src/DevKit.Core/DataHasher.cs:24-27, 57-68`;
`FTK2.ClassForge/src/ClassForge.Core/DataHasher.cs:18-49`; `ClassForge.Core/IO/FileSystemFileSource.cs:16`

DevKit's hasher documents itself as "the single, shared implementation… a per-mod-invented hashing scheme
would itself be a parity risk." ClassForge then invents a second one with a different canonical stream and
a different exclusion set:

| | DevKit | ClassForge |
|---|---|---|
| Stream | `FTK2MODS_DATAHASH_V1\n` + `path\n content\n` | `packId\|rel\|content\n`, no version tag |
| Excludes | `**/Localization/**`, `**/*.png/.jpg/.ogg/.wav/.md` | only `localization/` at pack root (`:48-49`) |

So ClassForge hashes all **82 PNGs** in `CF_PACK_EOR_CLASSES` (`icons/`, `portraits/`) through
`File.ReadAllText(path, Encoding.UTF8)` — a lossy U+FFFD decode of binary — and then applies
`.Replace("\r\n","\n").Replace("\r","\n")` to the decoded text (`:33`), mutating it further. It also
hashes `provenance.json`, which is pure metadata.

**Failure scenario:** an operator re-exports one icon with a different PNG encoder (visually identical,
different bytes), or re-imports from a newer EOR build so `provenance.json`'s `PackageVersion` bumps. Zero
gameplay change, but ClassForge's `dataHash` flips and every peer takes a parity mismatch → under
ClassForge's `Block` policy the entire engine switches off for a presentation-only or metadata-only edit.
DevKit's exclusion set exists precisely to make that a non-event. Secondarily, the lossy decode means the
hash is not sound even over the files it does include (distinct PNGs can collapse to the same decoded
text), and it is only peer-stable because every peer runs the same Mono decoder — a C# test host on
.NET would compute a different hash for the same pack.

**Minimal fix:** delete `ClassForge.Core/DataHasher.cs` and call `DevKit.DataHasher` by reflection — the
`ParityBridge` already demonstrates the no-compile-time-dependency pattern. Failing that, adopt
`GetDefaultExclusionGlobs()` and add `provenance.json`.

---

### M6 — Armory's 417 items reach `Configs` with no parity coverage at all.

**Files:** `FTK2.Armory/` (contains no `.cs`), `FTK2.Armory/INSTALL.md:16-18, 40-41`

Armory ships `data/Things/*.json` that operators **copy by hand** into
`EnhancedOverhaulRevamped\CustomItems\Things\` or the native `Configs\JSON~\Things\`. These become live
`Configs.Things` entries and are simulated from. Nothing registers them with `ParityService`.

**Failure scenario:** peer A copied the files, peer B copied an older revision or skipped the step. Both
peers simulate combat against different `ThingConfig` dictionaries. R1's detection never fires because
Armory registers nothing — this is exactly the "config-data divergence is the #1 desync source" case R1
was written for, with the detection simply absent.

**Minimal fix:** a minimal Armory plugin (or a DevKit-side registration hook for manually-installed data
roots) that computes `DevKit.DataHasher.ComputeFolderHash` over the Armory data folder and registers
`(guid, version, dataHash)`.

---

### M7 — The only tests that exercise the real corpus are hardcoded to one workstation.

**File:** `tools/tests/test_eor_import.py:19-21`

```python
REAL_SOURCE = Path(r"D:\temp\mods\Release 29 0.7.0.60 2026-07-18T18-42Z H0bovQUbN\BepInEx\plugins")
```

Absolute, machine-specific, with an opaque per-download suffix; `REAL_VOCAB` points into gitignored
`tools/out/`. `REAL_PACKAGE_AVAILABLE` (`:572`) is therefore `False` on every machine except the author's
and in CI, so `test_real_package_invariants_pass`,
`test_real_package_item_packs_pass_validate_pack` and `test_real_package_two_runs_byte_identical`
(`:576-619`) all silently **skip**. `test_class_remap_table_targets_all_present_in_vocab` (`:167`) reads
`REAL_VOCAB` unguarded and so *errors* rather than skips on a clean clone.

The determinism tests that do run are real — `test_two_runs_produce_byte_identical_output:395-421` does a
full `read_bytes()` comparison over `rglob` — but both runs happen in the same process, so they share
`PYTHONHASHSEED`, locale and OS and structurally cannot catch hash-order leakage. (I closed that gap
out-of-band: the converter was run in two separate processes with `PYTHONHASHSEED=1` and `99999` over the
real 417-item corpus and produced **byte-identical output across all 14 files**. R2 holds for the
converter core.) The gap that matters is the missing **golden-file** test — nothing asserts that the
committed packs equal a fresh regeneration, which is why B6 went undetected.

**Minimal fix:** make both paths env-overridable (`FTK2_EOR_SOURCE`, `FTK2_VOCAB`), guard `:167` with the
same `skipif`, check the vocab fixture in (B5's fix), and add the golden-file assertion.

---

## MINORS

| # | File:line | Issue | Fix |
|---|---|---|---|
| m1 | `DevKit.Core/ParityPolicy.cs:141-144`, `ParityService.cs:348-351` | `BlockSession` / `IsSessionBlocked()` are computed and stored, but nothing in DevKit acts on them. The banner promises "the session will not start/continue"; it does. | Either implement the refusal or reword the footer to "each Block-policy mod disables itself". |
| m2 | `ClassForge.Plugin/ConfigMergePatches.cs:84-85` | `RegisterParity` is only reached on the success path. A throw from `loader.Load` leaves ClassForge **unregistered** while `ApplyPlan` may already have partially mutated `Configs`. `ParityComparer` then yields `MissingLocal`, which `ParityService.cs:570` explicitly skips (`no entry → no callback`), so the peer that failed never blocks. | Register in a `finally`, with an explicit `"(merge-failed)"` dataHash so it is a guaranteed mismatch. |
| m3 | `ClassForge.Plugin/LocalizationPatches.cs:16` | Gates on `Enabled.Value`, not `FeaturesActive` — localization keeps merging under Block. Correct per R4 (loc is excluded from `dataHash`), but directly contradicts the Block banner at `ParityBridge.cs:162-169`. | Reword the banner; keep the behaviour. |
| m4 | `ClassForge.Plugin/ParityBridge.cs:143-147` | ClassForge blocks on any non-`Match` row **regardless** of the operator's `[Multiplayer] OnParityMismatch` setting, including `WarnOnly`. Intentional per SPEC §9.5, but it makes the DevKit knob a silent no-op for ClassForge. | State it in the knob's own description text, not only in ClassForge's SPEC. |
| m5 | `DevKit.Plugin/ParityCoordinator.cs:72-77` | When the broadcast fails, `OnSessionStarted` returns early and therefore also skips `BuildRequestPayload()`. A peer that cannot send is both invisible *and* silent. | Send the REQUEST regardless; it is the one message a send-broken peer most needs. |
| m6 | `DevKit.Plugin/ParityCoordinator.cs:105-117` | `OnHotReloadCompleted` has no caller anywhere in the branch. R5's "JSON hot-reload in MP requires a re-run of the parity handshake" is unimplemented, and `RehandshakeOnHotReload` is a knob with no effect. | Mark the knob `(not yet wired)` in its description until M1's hot-reload lands. |
| m7 | `ClassForge.Plugin/ConfigMergePatches.cs:25-29` + `RecipeEngineHost.cs:235-238` | The `ReloadConfigs` postfix rebuilds the recipe book and calls `ResetCombat()`, dropping every per-battle budget and cooldown. Currently latent (`ConfigsHelper.ReloadConfigs` has no vanilla caller — only the declaration at `ConfigsHelper.cs:398`), but any sibling mod that calls it mid-combat desyncs the caller's peer instantly. | Refuse the re-merge when `Env.GameRun.CombatState != null`. |
| m8 | `ClassForge.Plugin/AssetPatches.cs:45-46` | `Cache` / `Missing` statics mutated from Harmony prefixes with no synchronisation, and `LoadTexture` does `File.ReadAllBytes` + `LoadImage` inside a prefix on a per-row rebind path. | R4-exempt (presentation), so correctness is fine; note the main-thread I/O. |
| m9 | `ClassForge.Recipes/Runtime/RecipeDispatcher.cs:100-122` | `OnEnemyAbilityResolved` calls `HoldsAnyRecipeFor` per owner, which walks the whole book and reads `ICombatEntity.Passives` — an `EquipmentHelper.GetEquippedThings` call per entity per ability resolution. Correct (results are ordinal-sorted and cached per adapter, `GameAdapters.cs:185-225`), but O(entities × recipes) on a hot path. | Hoist the trigger→recipe index out of the per-owner loop. |
| m10 | all three `DataHasher`s (`DevKit:145-148`, `ClassForge:34`, `Summoner:39-42`) | No hash stream is **length-prefixed**. DevKit writes `<rel>\n<content>\n`, ClassForge `<packId>\|<rel>\|<content>\n`, Summoner `FILE:<rel>\n<bytes>/FILE\n`. Pack content that embeds the framing makes two genuinely different pack trees hash identically, so a divergent pack passes the handshake. Needs crafted content, hence MINOR. | Emit the byte length before each path and each content blob. |
| m11 | `Summoner.Plugin/Adapters/ConfigsSink.cs:27, 33` | Unconditional indexer writes (`configs.Characters[add.Id] = converted`) with no `ContainsKey` guard and no null guard, while `MergeInto` (`SummonerPlugin.cs:132-133`) carefully null-guards the same properties. Adds-only holds today only because `MergePlanner.PlanEntry:56-63` skips known ids against a live snapshot re-taken every call — a correct invariant living in a different assembly from the write. An NRE here is caught at `ConfigsMergePatches.cs:23` and leaves a **partially merged** `Configs` (characters in, followers not). | Two lines: `ContainsKey` guard + null guards in the sink. |
| m12 | `Summoner.Plugin/Parity/ParityRegistration.cs:12-15` vs `SummonerPlugin.cs:40, 142-148` | The doc-comment promises the DevKit type is re-resolved "every call … so DevKit installed later in the same session still gets registered", but `_parityRegistered` makes the first `MergeInto` the only attempt — and it latches even when `Register` no-ops. The `bool` return from `ParityService.Register` is discarded (`:49`). | Drop the latch, or latch only on a `true` return. |
| m13 | `DevKit.Core/ParityService.cs:565-573` | `ParityFailed` is re-dispatched on **every** duplicate snapshot from the same peer. ClassForge guards with its `first` flag (`ParityBridge.cs:146-149`); a sibling mod that doesn't gets notified indefinitely. Also `RemoteSnapshots`/`RepliedPeers` are keyed by the **sender-supplied** `SenderPeerId` (`:531, 542`), so a peer rotating that field grows both dictionaries until `ResetSession`. Neither corrupts state. | Dispatch only on a verdict *transition*; bound the peer dictionaries. |
| m14 | `Summoner.Plugin/SummonerPlugin.cs:183-197` | `harmony.Patch` is not wrapped in try/catch and `ApplyPatches()` is not guarded — unlike ClassForge (`ClassForgePlugin.cs:221-241`) and DevKit (`DevKitPlugin.cs:123-151`). A signature mismatch throws out of `Awake` *after* `LoadPacks()` has run. Fail-safe by luck (no patches → no merge), not by design; charter rule 1 wants it by design. | Mirror the sibling `Patch` helpers' try/catch. |
| m15 | `ClassForge.Core/Json/JsonParser.cs:27, 46, 73` | No recursion depth limit, unlike `DevKit.Core/MiniJson.cs:120` (cap 32). Only ever parses local pack files, never a network payload, so it is not hostile-reachable — but a pathological `pack.json` StackOverflows the process *uncatchably*, defeating `ManifestParser.cs:14-22`'s fail-safe. | Mirror MiniJson's depth counter. |

---

## NOTES

- **N1 — `CombatIdentity` mixes a process-local value into the combat key.**
  `GameAdapters.cs:254-256` builds `_identity` from `RuntimeHelpers.GetHashCode(state) + "|" + seed`. The
  identity hash differs per peer. It is used only by `RecipeStateStore.Sync` as a change-detector, is never
  hashed into parity data and is never transmitted, so R2 is not violated. It is also redundant:
  `RecipeEngineHost.SyncCombat:122` already gates on `ReferenceEquals(_cachedState, state) && _cachedSeed
  == seed` and reallocates the dispatcher (and with it the store) on any change. `seed` alone would be the
  peer-portable key. No divergence story constructed — filed as NOTE, not MAJOR.

- **N2 — `Round` is not monotonic.** `CombatState.TotalRounds` initialises to `-1`
  (`CombatState.cs:65`) and is reset to `-1` again at `CombatPhase.cs:2665`. `ICombatContext.Round`
  (`GameAdapters.cs:318-321`) reads it directly, and `CombatRuntime` keys `ONCE_PER_ROUND` budgets
  (`IsBudgetAvailable:68-69`) and cooldown expiry (`IsCooldownReady:88-93`) against it. On a multi-wave
  fight a cooldown started at round 5 becomes unavailable for five more rounds after the counter rewinds.
  Identical on every peer, so this is a gameplay-correctness note, not a desync. It does undercut the
  comment's claim that reading `TotalRounds` is "strictly safer than mirroring it into plugin state" — a
  `NextTurn`-postfix mirror would have followed the same rewind. It is a wash, not a strict improvement.

- **N3 — `ApplyAction`'s ADD_STATUS can silently swallow a recipe effect.** `CombatHelper.cs:1890`:
  `if (pResults.Any(x => x.Item1 == BLOCKED && ((AvoidHitAnimationData)x.Item2).ReactingEntity == pTarget)) break;`.
  `RecipeActionExecutor.ApplyAction` passes the *triggering hook's live* `pResults` list, so a BLOCKED entry
  appended by the ability that caused the proc suppresses the recipe's own status. Deterministic across
  peers. Worth documenting because it will read as a bug in play-testing.

- **N4 — `IMMUNITY_FALLBACK` is the engine's one conditional extra native call.**
  `RecipeActionExecutor.cs:138-145` takes a second `ApplyAction` iff the first appended no `STATUS_ADDED`.
  Both branches are pure functions of replicated state today, so draw counts stay symmetric. It is the one
  place where a future non-replicated input would silently become a draw-count divergence — worth a comment
  in the file itself.

- **N5 — The RNG discipline inside the engine holds up.** Audited against the charter's rule 2 and
  SPEC-DELTA §5.2 invariants 1–4:
  - The only two draws are `RecipeDispatcher.cs:341` (`NextChance`, ProcChance) and `:504` (`NextInt`,
    `StatusOneOf`), both through `GameRandomSource` (`GameAdapters.cs:472-483`) wrapping
    `CombatState.Random` only. No `System.Random`, no `UnityEngine.Random`, no constructed `GameRandom`
    anywhere in the new code (grepped).
  - Evaluation order `Conditions → Cooldown → Budget → roll` (`EvaluateRecipe:320-353`) puts the draw last,
    so nothing draws on a path that a gate can skip.
  - `chance >= 100` takes **zero** draws; every other value takes exactly one. The branch selector is
    `t.Owner.IsAiControlled` → `!CharacterHelper.IsFriendly(Native)` → `GroupIndex == 0`
    (verified `CharacterHelper.cs:1483`), a field on the serialized `CharacterComponent`. Peer-consistent,
    so the zero-vs-one-draw branch is a pure function of replicated state. **This one was the sharpest
    candidate for an in-engine draw-count divergence and it survives.**
  - Every condition (`Evaluation.cs:78-223`) reads replicated state or a hook parameter; none draws.
    `EntitySets`/`TargetResolver` sort ordinal by `Entity.Guid` throughout; `EntityAdapter.Statuses` and
    `.Passives` (`GameAdapters.cs:72-92, 185-225`) materialise `Dictionary`/`HashSet` sources into
    ordinal-sorted lists before the engine sees them; `RecipeSet.Add` re-sorts on `(Priority, ordinal Id)`
    with unique ids, so `List.Sort`'s instability is harmless.
  - The "unconditional-once-per-eligible-evaluation" claim is accurate **at the call sites too**: all 15
    hooks reach the engine only through `RecipeEngineHost.TryBegin` (`:69-112`), which refuses on a null
    stream and never substitutes an ad-hoc `GameRandom`. The re-entrancy guard (`_executionDepth`, released
    via `using` in `RecipeActionExecutor.Execute:72`) is itself a function of the plan being executed.

  The engine's determinism is therefore sound **conditional on identical feature state and identical
  packs** — which is precisely what B1/B2/B4 fail to guarantee.

- **N6 — All 15 trigger hooks are all-peers methods.** Checked against
  `docs/research/game-patch-surface-notes.md` and the decompile. The one that needed real checking is the
  turn hook: `CombatPhase._performSkillAbilityProcs` is called at `CombatPhase.cs:2096` (START_TURN),
  `:4854` and `:4880` (END_TURN), and **none of those call sites is inside an `IsRemotePlayer` branch** —
  `IsRemotePlayer` is used throughout `CombatPhase` (`:540, 625, 1661, 2201, 2709, 2780`) only to gate input
  and UI, never simulation. The prefix observes before the first `await`, so the dispatch is synchronous at
  the call site and the draw ordering relative to the caller is fixed even though the method body later
  awaits the *local* visual stack. Correct as designed.

- **N7 — Prefix inventory.** Seven prefixes in the new code. Four are `void` and structurally cannot skip
  their original: `PerformAbility_Prefix` (swaps the `pGetStat` delegate parameter for one call —
  int/decimal arithmetic only, `CombatHookPatches.cs:161-192`, no RNG, pure function of replicated state ✔),
  `ApplyStatChange_Prefix` (`__state` capture + the M4 static), `AddHealth_Prefix` (mutates `ref int pValue`
  — RNG-free by validator construction, `RecipeValidator.cs:202-204` rejects a chance-gated `HEAL_MODIFIER`;
  arithmetic is int/decimal ✔), `PerformSkillAbilityProcs_Prefix` (observe-only). One is `void` and
  observe-only: `ParityPatches.HandleNetworkActionPrefix` — deliberately incapable of swallowing traffic,
  which is the correct answer to audit §3.4. Two return `bool` and can skip: `AssetPatches.GetImage_Prefix`
  and `GetRender_Prefix` — both texture substitution only, R4-exempt, and they `return true` on every error
  path. **No prefix in the new code can suppress authoritative combat state.** The two sanctioned mutations
  (`ref pValue`, delegate swap) are verified deterministic pure functions of replicated state.

- **N8 — What R1 *does* cover.** Per-pack `[Packs] <PackId>.Enabled` toggles are correctly caught: a
  disabled pack drops out of `EnabledOrderedPacks`, changing both `DataHash` and `EnabledFeatures`. Worth
  recording so the B4 fix does not over-correct.

- **N9 — Class pick replication.** `ClassSelectPatches.RenderClassList_Prefix` mutates
  `pPlayableCharacters` in place and is `[LOCAL]` presentation *only as far as the list rendering goes* —
  but the pick that follows flows into `PartyManagementDirector`, which is index-serialized in the same
  family as B3 (`_getLoadoutIndicesForPlayers`, `PartyManagementActionData`). The injection is
  ordinal-sorted and gated on `CharacterConfig` presence + `PLAYER` tag + non-null `Stats/Things/Passives`
  (`:76-92`), so it is a pure function of parity-hashed data — the same conditional-on-R1 correctness as
  B3, with the same missing enforcement point. Filed here rather than as its own finding because the fix is
  B3's fix.

---

## Spec corrections in `ClassForge.Plugin` — verified against `tools/out/decompile/FTK2/`

### 1. Turn-phase hook re-anchoring — **CORRECT**
`CombatHookPatches.cs:499-519` claims SPEC-DELTA-v1.1 §2.1 named the wrong method. Verified:
- `CombatHelper._onCombatSkillProc(CombatState, Entity, eSkills, int, bool, List<(eAbilityResults,object)>, bool)`
  — `CombatHelper.cs:1818`. **No phase parameter.** ✔
- `eSkills` contains no `START_TURN`/`END_TURN`/`EVENT_PROC` member (grep count 0). ✔
- `eSkillEventProcs` is exactly `{NONE, START_TURN, END_TURN, ON_KILL, ON_DAMAGE_GIVEN}` — matches the
  comment verbatim. ✔
- `CombatPhase._performSkillAbilityProcs(Entity pEntity, eSkillEventProcs pProcEvent)` at
  `CombatPhase.cs:4921`, `private async Task`; called with `START_TURN` at `:2096` and `END_TURN` at
  `:4854`/`:4880` — all three line numbers exact. ✔
- The `private` + `async Task` caveats and the fail-safe (`ClassForgePlugin.cs:199-204` →
  `WarnTurnHookMissing`) are both present and honest.

The re-anchoring is correct and better-evidenced than the delta it replaces.

### 2. `ApplyAction` payload shapes — **CORRECT**
`RecipeActionExecutor.cs:38-54`. Verified against `CombatHelper.ApplyAction` (`:1871`):
- `ADD_STATUS` (`:1883`): `if (pRollData.Status != eRollStatus.PERFECT) break;` then
  `((pActionArgs is JsonElement) ? ((JsonElement)pActionArgs).GetString() : ((string)pActionArgs))`
  — bare string, PERFECT gate. ✔
- `REMOVE_STATUS` (`:2010`): identical shape + PERFECT gate. ✔
- `CHANGE_STAT` (`:2030`): `JsonHelper.Deserialize<ChangeStatAction>(((JsonElement)pActionArgs).GetRawText())`
  — **unconditional** cast, so a raw instance would throw. ✔
- `ADD_CHARACTER` (`:2143`): same unconditional `JsonElement` cast. ✔
- Field names: `ChangeStatAction { string Stat; eDamageType Type; bool IsBlockable; bool IsSilent;
  int? FlatValue; int? FlatPercent; }` — exactly what `BuildChangeStatJson:203-216` emits, including the
  `IsBlockable` (not `Blockable`) trap the comment flags. ✔ `AddCharacterAction { eSummonTypes Type;
  string Value; }` — exactly what `ExecSummon:250-251` emits. ✔
- `eDamageType` has no `WATER`; the normalisation at `:225-236` is real, not defensive theatre. ✔

One omission, not an error: **`ADD_CHARACTER` also has a `PERFECT` gate** (`:2140-2143`) which the class
remarks attribute only to ADD_STATUS/REMOVE_STATUS. The executor supplies `PERFECT` unconditionally
(`:270-277`) so behaviour is correct; the doc-comment is merely incomplete.

### 3. `TotalRounds` — **CORRECT on the facts, over-claimed in the conclusion**
`GameAdapters.cs:310-317`. Verified:
- `public int TotalRounds;` at `CombatState.cs:8`. ✔
- Incremented at `CombatHelper.cs:809`, inside the `pIsNewRound = true` branch, immediately after the
  `"## NEW ROUND"` log at `:805`. The cited range `793-838` contains both. ✔

The factual claim is right. The *conclusion* — "strictly safer than mirroring it into plugin state: a pure
read of replicated state cannot drift if a hook is ever missed" — is over-claimed: `TotalRounds` is reset
to `-1` at `CombatPhase.cs:2665` and initialised to `-1` at `CombatState.cs:65`, so the value is not a
monotonic round counter, and a `NextTurn`-postfix mirror would have tracked exactly the same rewind. See
N2. Reading the field directly is *simpler*, not *safer*.

---

## Compliance matrix — R1–R5 × component

| Component | R1 parity | R2 determinism | R3 host authority / vanilla replication | R4 presentation exemption | R5 dev-mutation lockout |
|---|---|---|---|---|---|
| **DevKit.Core** (`ParityService`, `Comparer`, `Policy`, `Codec`, `DataHasher`) | **ISSUES** — logic is sound and well-tested, but `IsHost` is never true (M3) so the host-reply and late-join branches are unreachable; `BlockSession` is inert (m1) | ok — ordinal sorts, invariant culture, no RNG | ok — pure decision routine, no authority claimed | ok — banner/log only | ok — no mutation surface |
| **DevKit.Plugin** (transport + patches) | **BLOCKED** — B1 (cannot send) + B2 (cannot receive). R1 is unenforced for every mod in the repo | n/a | ok — receive hook is a `void` prefix, structurally cannot swallow traffic (correct answer to audit §3.4) | ok | **ISSUES** — m6: hot-reload re-handshake unwired, knob has no effect |
| **ClassForge.Core** (loader/merge/hash) | **ISSUES** — B4: `enabledFeatures` omits the feature knobs | ok — ordinal ordering, content-derived ids, no wall-clock | n/a | n/a | n/a |
| **ClassForge.Plugin — merge + loc** | ISSUES — M2: Block branch unreachable, merge always happens | ok | ok — merges into `Configs`, which replicates for free (the preferred pattern per MULTIPLAYER.md "Practical guidance") | ok — loc excluded from hash, keeps running under Block (m3) | ok |
| **ClassForge.Plugin — trait loadout** | **BLOCKED** — B3: injection precedes any possible handshake; pick is index-serialized → crash or wrong-item on a peer with a different pool | ok in isolation — unconditional, ordinal-sorted, no draws, deterministic `Thing.Id` | ok — rides the vanilla loadout pool | n/a | ok |
| **ClassForge.Plugin — class select** | ISSUES — same enforcement gap as B3 (N9) | ok — ordinal-sorted, gated on parity-hashed data | ok — rides `PartyManagementDirector` | partial — list render is `[LOCAL]`, the pick is not | ok |
| **ClassForge.Plugin — asset fallback** | n/a — R4-exempt | n/a | n/a | **ok** — correctly claimed and correctly implemented; the two `return false` prefixes substitute a texture and nothing else | ok |
| **ClassForge.Recipes** (engine core) | n/a | **ok** — single shared stream, draw last in the gate order, zero-vs-one-draw branch is a pure function of `GroupIndex`, all iteration ordinal-sorted, no static state in the engine assembly (N5) | ok — no bespoke `_SYNC_`; every effect rides `CombatHelper.ApplyAction` | n/a | ok |
| **ClassForge.Plugin — recipe hooks/executor** | **ISSUES** — B4: `EnableRecipeEngine` is not parity-covered, so an asymmetric engine is certified as "Match" | **ISSUES** — M4: `_healOrigin` static leaks past combat on an exception path (charter rule 4) | ok — all 15 hooks verified all-peers (N6); no prefix suppresses authoritative state (N7); the two sanctioned mutations are deterministic | n/a | ok |
| **ClassForge.PackCheck** | n/a | ok | n/a | n/a | **ok** — `net10.0` `<OutputType>Exe</OutputType>`, references only Core + Recipes, no BepInEx/Harmony/game assemblies, so it **cannot** be loaded into the Mono/net472 game process. `Program.cs` reads (`:132-134`) and writes stdout only; it never writes a file and never touches `Configs`. |
| **Summoner.Core** (loader/validator/merge/hash) | **ISSUES** — B0: unprefixed hash | ok — ordinal sorts, invariant culture, immutable `static readonly` vocabulary, no RNG | n/a | ok — localization excluded | ok |
| **Summoner.Plugin** | **BLOCKED** — B7: no `ParityFailed` pathway, `Block` knob inert, disabled peer registers nothing | ok — `LoadPacks()` runs once in `Awake` and `MergeInto` re-plans from the immutable in-memory list, so no mid-session disk drift is possible (**the pattern ClassForge should adopt — see m7**) | ok — two postfixes, **zero prefixes**; neither reassigns its `ref` parameter (m14 aside) | ok | ok |
| **`tools/eor_import.py`** | **BLOCKED** — B5 (install-derived vocab changes emitted gameplay data), B6 (regeneration destroys committed content) | **ok, empirically** — converter core proven byte-identical across two processes at `PYTHONHASHSEED=1` and `99999` over the real 417-item corpus; all 21 emitted-data iterations `sorted()`; `write_json:156-162` uses `sort_keys=True, newline="\n"`; content-derived ids only; no wall-clock, no RNG, no filesystem enumeration | n/a | n/a | n/a |
| **Emitted packs** (Armory / ClassForge / Summoner data) | **ISSUES** — M6: Armory's 417 items reach `Configs` with zero parity registration | ok — zero absolute paths, usernames, timestamps or non-ASCII across all pack JSON; zero CR bytes on disk | n/a | ok | n/a |

**R5 summary.** The reviewed surface is clean: DevKit M1 ships parity wiring only, PackCheck is an
out-of-process CLI, and there is no give/spawn/force-merge/config-rewrite path anywhere. The one mutation
path reachable while a session is live is m7 (ClassForge's `ReloadConfigs` disk re-scan) — and because it
is not a dev *command*, no `ForceAllowInMP` gate would catch it. The R5 clause that is outright
unimplemented is "hot-reload requires a re-run of the parity handshake" (m6).

---

## Recommended order of work

The first four are one change set. Fixing any of them alone leaves the mechanism non-functional, and
fixing B1/B2 without B0 turns a silent no-op into a session-killing false positive.

1. **B0 + B1 + B2 together.** B0 is a one-line fix per sibling hasher but is currently *masked* by the dead
   transport — repair the transport and it fires immediately on correctly-configured peers. Prove the whole
   path on a live two-peer session before anything else is trusted. Add a DevKit test that round-trips a
   **real** hasher output rather than a prefixed constant.
2. **B3 + N9** — move the handshake anchor earlier than `PartyManagementDirector`; the
   `AdventureSelectionDirector` MP branches (`:137/:169/:351`) are the natural site. Make injection require
   a *positive* parity success, not merely the absence of a failure.
3. **B4** — one line in `ParityRegistrationBuilder`, and it is what makes the mechanism catch the most
   likely real-world divergence: a knob, not a pack.
4. **B7 + M3** — give Summoner a real callback/Block path (or delete the knob), and set
   `SetIsHost(_env.NetworkData.IsHost)`. Correct DevKit SPEC §11.8 — the open question is answered by the
   repo's own decompile.
5. **B5 + B6** — commit a pinned vocab, stop the converter clobbering hand-authored `Passives`/loc keys,
   and add the golden-file regeneration test whose absence hid B6.
6. **M0, M1, M4, M4b, M5** — the silent-asymmetry generators: no adds-only gate, a block flag that outlives
   its session, a static that outlives its combat, a transport that outlives its failure, and a hash that
   fires on a re-exported PNG.
7. **M2, M6, M7** — decide what Block actually means and make code + SPEC agree; give Armory a parity
   registration; unhardcode the converter tests.

**What not to touch.** Nothing in the recipe engine's determinism core needs changing (N5, N6, N7). Single
shared stream, draw taken last in the gate order, zero-vs-one-draw branch proven to be a pure function of
replicated `GroupIndex`, ordinal ordering everywhere, all 15 hooks verified all-peers, no prefix capable of
suppressing authoritative state, and a per-battle state model that is structurally immune to the EOR
`SteadyAimUsedThisCombat` bug. The determinism work is the strongest part of this branch. The enforcement
layer it depends on is the weakest, and every BLOCKER above lives in that layer.
