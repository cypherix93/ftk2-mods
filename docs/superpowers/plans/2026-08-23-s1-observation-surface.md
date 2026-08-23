# S1 Observation Surface (`crucible.state.v2`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a combat-level observation surface — `crucible.state.v2` — so a scenario can prove an ability actually did something. Today's snapshot knows route, run, and network; it knows nothing about a combatant's hp, statuses, or whose turn it is. v2 adds combatants, and it **synthesizes** the two fields the engine does not store as data (`turn`, `phase`).

**Architecture:** All shaping, sorting, warning-collection, reflection-overload-selection, and the turn/phase state machine live in `Crucible.Core` (`netstandard2.0`, no Unity, no game reference) and are unit-tested with no game running. `Crucible.Plugin` (`net472`) holds only the code that actually touches game objects: a reflection reader (`CombatReader`) and two Harmony postfixes (`TurnHooks`). `StateReader` v1 is **not modified** — v2 is a second reader served from the same endpoint under `?schema=v2`, so nothing in flight breaks and no existing digest changes.

**Tech Stack:** C# · `netstandard2.0` (Core) / `net472` (Plugin) · `System.Reflection` · HarmonyLib · BCL only, no NuGet · repo console-runner test pattern (`FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs`)

**Spec:** `docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` (§2 grounding rule, §3 architecture + error posture, §4 the v2 schema, §10 testing standard, §11 build order)

**Field map (source of truth for every game member name):** `docs/research/crucible-combat-field-map.md`

**Prerequisite:** S0 is complete — `TypeProbe` exists at `FTK2.DevKit/sandbox/TypeProbe` and the `ben` branch is the trunk. Work this plan in a worktree: `git worktree add ../ftk2-wt-s1 -b s1/observation-surface ben`.

---

## Global Constraints

- **No NuGet `PackageReference` anywhere.** BCL + `ProjectReference` + `HintPath` refs only.
- **`Crucible.Core` stays clean.** No `UnityEngine`, no `BepInEx`, no `FTK2` reference, no `HarmonyLib`. Every new Core type compiles and tests on a machine with no game installed.
- **No NuGet test framework.** `Crucible.Core.Tests` is a `net10.0` console runner; exit 0 = pass.
- **Every check has a negative control.** A test that cannot fail is a plan failure (spec §10).
- **Grounding rule (spec §2).** No game member name enters code until it is in `docs/research/crucible-combat-field-map.md`. Task 1 extends that map with the four members this plan needs that the map does not yet carry. Task 6 must not be started before Task 1 lands.
- **Reflection misses must be LOUD (spec §2 corollary).** Every reflective read in the v2 path takes a `WarningSink` and reports into the snapshot's `warnings` array. This is the mechanism that would have caught `NetworkData.PlayerCount` in a day instead of months.
- **Error posture (spec §3).** Reflection failure degrades to `null` + a warning, never an exception. A snapshot that throws takes down the oracle exactly when a game update makes it most needed.
- **v1 is frozen.** `StateReader.cs` is not edited by this plan. Adding warnings to v1 would change v1's digest (`warnings` is inside the v1 digest — `RpcServer.HandleState` redacts only `instance`), which would make every in-flight cross-peer comparison report a false desync. v2 supersedes the v1 warning suppression rather than fixing it in place.
- **Verified game path:** `C:\Program Files (x86)\Steam\steamapps\common\For The King II`. Managed dir: `<game>\For The King II_Data\Managed`.

---

## Deviations from spec §4 (the field map wins)

The spec's §4 sample was written before the field map was measured. Five fields in it do not survive contact with the assembly. Each deviation below is a decision, not an omission.

| §4 as written | Reality (field map) | Decision |
|---|---|---|
| `"turn": 1` | No turn counter exists anywhere in the assembly. `PlayerComponent.TurnsPlayed` is per-combatant, not global. | **Synthesize.** A Harmony postfix on `CombatPhase._nextTurn` increments an ordinal; the ordinal resets per combat episode. Marked in the payload as synthesized. |
| `"phase": "PLAYER"` | No intra-combat phase enum exists. `ePhases`/`VenueState.PhaseIndex` are dungeon-room level (COMBAT vs REST vs TREASURE). | **Synthesize.** A Harmony postfix on `CombatPhase._engageActiveEntity` reads the engaged entity and derives `PLAYER`/`ENEMY` from `PlayerComponent` presence. `NONE` outside combat, `UNKNOWN` when the hooks failed to install. |
| `"stats": { … }` enum-keyed | `eStats` does not exist. Every stats dictionary in the assembly is string-keyed. | `stats{}` is a **string-keyed** map of `string -> int`. |
| `"stacks": 1` on a status | `StatusEffectInfo` has `Duration`, `InitialDuration`, `OriginEntityId`, `TickDuration` — **no** stacks/count field. | **Omit `stacks` entirely.** See "The stacks decision" below. |
| `"round": 2` implied monotonic | `CombatState.TotalRounds` initialises to `-1` and is reset to `-1` **per wave**. | Ship `round`, and document non-monotonicity in the schema doc so assertions never rely on it. `turn` is the monotonic axis; `wave` (`CombatState.WaveIndex`) is exposed so consumers can segment. |
| `"partyClassIds": [...]` on `run` | No grounded member reaches a party roster outside combat. | **Omit in S1.** Inside combat it is a redundant projection of `combat.combatants[?isPlayer].classId`. Outside combat it needs a `GameRunData` party member nobody has dumped. Deferred to S3's `set_party` spike, which needs that root anyway. Open question #1. |
| `"rng": { … }` | Nothing in the field map covers `GameRandom`. | **Omit in S1.** `rng` is S5's `RngWatch` signal; S5 adds the key additively. Building an empty stub now would be speculative code that reports zeros as if they were measurements. Open question #2. |

### The stacks decision (and the reasoning)

**`statuses[].stacks` is omitted, not derived.**

The field map records that `StatusEffectInfo` carries no stack count, and that stacking *appears* to be encoded as distinct dictionary keys with suffixed ids (`STATUS_ATTACKUP_00`, `_01`). "Appears" is the operative word — the map itself flags this UNRESOLVED and says confirming it requires reading `Configs.StatusEffects` JSON, which was out of scope for a type-level probe.

Deriving a count by parsing a `_NN` suffix would mean writing a rule about game data that nobody has verified, into the one component every assertion depends on. That is the exact shape of the `NetworkData.PlayerCount` failure: a plausible-looking value, confidently served, wrong. A derived `stacks: 1` on a status whose suffix means "tier", not "stack", would be worse than no field at all — it would make a scenario pass for the wrong reason.

Instead v2 emits every field `StatusEffectInfo` really has (`duration`, `initialDuration`, `tickDuration`, `originEntityId`) keyed by the real dictionary key as `id`. Assertions match on ids, which is exactly what the spec §6 example scenario already does (`contains "STATUS_ATTACKUP_00"`). If a scenario later needs "how many attack-up tiers are on this unit", it can count matching ids in the array with the existing `contains` machinery — a count the consumer derives from raw data it can see, rather than one the oracle invents.

Recorded as open question #3: confirm the suffix convention against `Configs.StatusEffects` during S6 (LiveDataHarness already walks that data), then decide whether a derived field is warranted.

---

## File Structure

| File | Responsibility | Tier |
|---|---|---|
| `FTK2.Crucible/src/Crucible.Core/WarningSink.cs` | **New, pure.** Deduplicating warning collector with typed helpers (`MemberMissing`, `MemberThrew`, `Ambiguous`, `TypeMissing`, `Note`). | Core |
| `FTK2.Crucible/src/Crucible.Core/MemberResolver.cs` | **New, pure.** Field-then-property lookup walking base types; unary static overload selection with most-derived tie-break; component lookup by type name; scalar coercion. Every entry point takes a `WarningSink`. | Core |
| `FTK2.Crucible/src/Crucible.Core/TurnTracker.cs` | **New, pure.** The synthesized turn/phase state machine plus its transition-event queue. | Core |
| `FTK2.Crucible/src/Crucible.Core/CombatViews.cs` | **New, pure.** Plain DTOs: `RunView`, `NetworkView`, `CombatView`, `CombatantView`, `StatusView`. | Core |
| `FTK2.Crucible/src/Crucible.Core/SnapshotShape.cs` | **New, pure.** `BuildV2(...)` — the only place the v2 JSON shape is defined. Sorting and null-vs-empty rules live here. | Core |
| `FTK2.Crucible/src/Crucible.Core/Redactions.cs` | **New, pure.** `Redactions.V2` glob list used for the v2 digest. | Core |
| `FTK2.Crucible/src/Crucible.Core.Tests/WarningTests.cs` | **New.** 4 tests. | Tests |
| `FTK2.Crucible/src/Crucible.Core.Tests/ResolverTests.cs` | **New.** 14 tests + fake types. | Tests |
| `FTK2.Crucible/src/Crucible.Core.Tests/TurnTrackerTests.cs` | **New.** 11 tests. | Tests |
| `FTK2.Crucible/src/Crucible.Core.Tests/SnapshotShapeTests.cs` | **New.** 14 tests. | Tests |
| `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs` | **Modify.** Four added `RunAll()` calls. | Tests |
| `FTK2.Crucible/src/Crucible.Plugin/CombatReader.cs` | **New.** Reflection: `CombatPhase` → `CombatState` → `Entity` → components → views. | Plugin |
| `FTK2.Crucible/src/Crucible.Plugin/CharacterHelperBridge.cs` | **New.** Cached unary-static dispatch into `CharacterHelper`. | Plugin |
| `FTK2.Crucible/src/Crucible.Plugin/TurnHooks.cs` | **New.** Two Harmony postfixes; owns the process-wide `TurnTracker`; drains transition events into the trace. | Plugin |
| `FTK2.Crucible/src/Crucible.Plugin/StateReaderV2.cs` | **New.** Assembles the v2 snapshot. | Plugin |
| `FTK2.Crucible/src/Crucible.Plugin/CruciblePlugin.cs` | **Modify.** One line: `TurnHooks.Initialize(_harmony, _log);`. | Plugin |
| `FTK2.Crucible/src/Crucible.Plugin/RpcServer.cs` | **Modify.** `HandleState` branches on `?schema=v2`. | Plugin |
| `FTK2.Crucible/mcp/server.js` | **Modify.** `ftk2_state` gains an optional `schema` argument. | MCP |
| `FTK2.Crucible/data/Redactions.json` | **Modify.** Document the v2 globs alongside the v1 ones. | Data |
| `docs/research/crucible-combat-field-map.md` | **Modify.** Task 1 appends an "S1 addendum" section. | Docs |
| `docs/research/crucible-state-v2-schema.md` | **New.** The v2 contract, including the non-monotonic `round` caveat and the synthesized-field notes. | Docs |

**Why the split is where it is.** The hard parts of this feature are *not* the reflection calls — they are overload selection, warning discipline, turn/phase bookkeeping across combat boundaries, and shape/sort/null rules that the digest depends on. All four are pure functions over ordinary CLR objects, so all four live in Core and are driven by fake types in the test assembly with deliberately wrong inputs. What remains in Plugin is a thin, boring translation layer whose only untestable-offline property is "does the real game object have this member" — which is precisely what the `warnings` array reports at runtime.

---

## Offline / live boundary

| Task | How it is verified | Needs |
|---|---|---|
| 1 — Grounding sweep | `TypeProbe` exit codes + the appended field-map tables | Game **files** on disk. No running game. |
| 2 — `WarningSink` | `dotnet run --project …/Crucible.Core.Tests -c Release` → exit 0 | Nothing |
| 3 — `MemberResolver` | Same | Nothing |
| 4 — `TurnTracker` | Same | Nothing |
| 5 — Views + `SnapshotShape` + `Redactions` | Same | Nothing |
| 6 — `CombatReader` + `CharacterHelperBridge` | `dotnet build` against the installed refs | Game **files** on disk |
| 7 — `TurnHooks` | `dotnet build` | Game **files** on disk |
| 8 — RPC + MCP + schema doc | `dotnet build`, `node --check` | Nothing beyond a build |
| **9 — Live verification** | **A driven game in a real fight** | **PARKED** |

**The boundary is between Task 8 and Task 9.** Tasks 1–8 are executable start to finish right now and end with a green offline test suite and a clean build. Task 9 is written out in full so it can be run the moment a driven game is available, and it is the only thing standing between "v2 compiles and its logic is proven" and "v2 is done" per spec §10 ("a verb with no live evidence is not done" — the same bar applies to a reader). **Do not mark S1 complete after Task 8.** Mark it *offline-complete, live-verification parked*.

---

### Task 1: Ground the four members the field map does not yet carry

**Files:**
- Modify: `docs/research/crucible-combat-field-map.md` (append an "S1 addendum" section)

**Interfaces:**
- Consumes: `FTK2.DevKit/sandbox/TypeProbe` (built in S0).
- Produces: verified member tables for `GameRunData`, `NetworkData`, `RouterHelper` (methods), `CombatPhase` (methods), and `CharacterHelper` (methods) — the inputs Tasks 6 and 7 write code against.

**Why this task exists.** The existing field map resolves 14 of the 17 snapshot fields. Four names this plan needs are **not** in its tables:

| Name | Where it currently comes from | Risk |
|---|---|---|
| `GameRunData.CombatState` | Field-map *prose* only, citing `docs/research/eor-rehost-mp-review.md` | It is the fallback path to `CombatState`. |
| `GameRunData.Seed` / `.Day` / `.Gold` / `.Chapter` | Inherited from `StateReader` v1, never dumped | v1 suppresses warnings on these reads (`GetMember(gameRun, "Seed", null)`), so a wrong name here would be **silent** — the exact `PlayerCount` failure mode. |
| `RouterHelper.GetCurrentRoute` | v1 code; the field map's `RouterHelper` table lists fields/props only | Proven to return live data (spec §0) but never dumped. |
| `CombatPhase._nextTurn` / `._engageActiveEntity` | Field-map *prose* ("driven procedurally through `CombatPhase._engageActiveEntity` / `_nextTurn`"); the `CombatPhase` table was trimmed to fields/props | These are the two Harmony targets the whole synthesized-turn design rests on. |
| `CharacterHelper.GetMaxHealth` / `IsDead` / `GetBaseStats` parameter types | The map lists names and return types, not signatures | Determines whether the unary call takes an `Entity` or a `CharacterComponent`. |

- [ ] **Step 1: Dump the types**

```bash
cd /c/Users/ben/repos/ftk2-wt-s1
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- GameRunData NetworkData
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- --methods RouterHelper
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- --methods CombatPhase
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- --methods CharacterHelper
```

Expected: exit 0 for each. Exit 2 means the game assembly could not be loaded — report blocked, do not proceed to Task 6.

- [ ] **Step 2: Record the results verbatim**

Append to `docs/research/crucible-combat-field-map.md` a new section, copied from actual stdout with nothing paraphrased:

```markdown
---

## S1 addendum (probed <date>)

Added because S1 needs five members the tables above do not carry. Same rule as the rest of this
document: every table is verbatim TypeProbe stdout.

### GameRunData

<verbatim table>

### NetworkData

<verbatim table>

### RouterHelper (methods)

<verbatim table>

### CombatPhase (methods)

<verbatim table>

### CharacterHelper (methods)

<verbatim table>

### S1 resolutions

| S1 need | Resolved member | Status |
|---|---|---|
| path to `CombatState` (fallback) | `GameRunData.CombatState` | <RESOLVED / CORRECTED to X / NOT FOUND> |
| `run.seed` / `.day` / `.gold` / `.chapter` | `GameRunData.Seed` / `.Day` / `.Gold` / `.Chapter` | <one row each> |
| `route` | `RouterHelper.GetCurrentRoute` | <…> |
| turn-advance hook | `CombatPhase._nextTurn` — return type `<…>`, `<n>` parameters | <…> |
| turn-begin hook | `CombatPhase._engageActiveEntity` — return type `<…>`, `<n>` parameters | <…> |
| `maxHp` | `CharacterHelper.GetMaxHealth` — `<n>` overloads | <…> |
| `alive` | `CharacterHelper.IsDead` — `<n>` overloads | <…> |
| `stats{}` | `CharacterHelper.GetBaseStats` — `<n>` overloads | <…> |
```

- [ ] **Step 3: Reconcile before writing any Plugin code**

Three outcomes, three responses — no fourth option:

1. **Every name confirmed.** Proceed; Tasks 6 and 7 use the names exactly as written in this plan.
2. **A name is different.** Correct it in the addendum **and** in the Task 6/7 code before writing it. The addendum is the source of truth, not this plan.
3. **`_nextTurn` or `_engageActiveEntity` does not exist under that name.** Stop and report. The synthesized-turn design has no fallback that produces a real number — Task 7's degrade path (`turn: null`, `phase: "UNKNOWN"`, a loud warning) ships, but S1 then delivers no turn axis and S4 needs to know that before it plans assertions.

Note the return type of `_nextTurn` in the addendum. If it is `UniTask`/`Task`, a Harmony postfix fires when the async state machine is *created*, not when the turn completes. That is still exactly one fire per advance in call order, which is all the ordinal needs — but Task 9 Step 4 verifies the ordering empirically rather than assuming it.

- [ ] **Step 4: Commit**

```bash
git add docs/research/crucible-combat-field-map.md
git commit -m "Field map: S1 addendum for GameRunData, CombatPhase hooks, and CharacterHelper signatures"
```

---

### Task 2: Core — `WarningSink`, the loudness mechanism

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/WarningSink.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/WarningTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public sealed class FTK2Mods.Crucible.WarningSink`
  - `void Add(string)`, `void MemberMissing(string typeName, string memberName)`, `void MemberThrew(string typeName, string memberName, string detail)`, `void Ambiguous(string typeName, string memberName, int candidates, string argTypeName)`, `void TypeMissing(string typeName)`, `void Note(string)`
  - `int Count { get; }`, `string[] ToArray()`, `List<object> ToJsonList()`

**Why deduplication is the design.** A snapshot walks every combatant. A single renamed field on `CharacterComponent` would otherwise emit one warning per combatant — twelve identical lines in a fight with twelve entities, and a `warnings` array that grows with party size instead of with problems. Deduplicated, one miss is one line, which is what keeps the array readable enough for an assertion to carry it into a failure report.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/WarningTests.cs`:

```csharp
namespace FTK2Mods.Crucible.Tests
{
    internal static class WarningTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("WarningSink");

            TestHarness.Run("records a member miss with type and member name", delegate
            {
                WarningSink sink = new WarningSink();
                sink.MemberMissing("NetworkData", "PlayerCount");
                TestHarness.Equal(1, sink.Count, "one warning");
                TestHarness.Equal("member_missing: NetworkData.PlayerCount", sink.ToArray()[0], "text");
            });

            TestHarness.Run("deduplicates identical messages", delegate
            {
                WarningSink sink = new WarningSink();
                for (int i = 0; i < 12; i++) sink.MemberMissing("CharacterComponent", "DisplayName");
                TestHarness.Equal(1, sink.Count, "twelve combatants, one warning");
            });

            // Negative control: dedupe must not collapse genuinely different problems.
            TestHarness.Run("keeps distinct messages", delegate
            {
                WarningSink sink = new WarningSink();
                sink.MemberMissing("CharacterComponent", "DisplayName");
                sink.MemberMissing("CharacterComponent", "ConfigName");
                sink.MemberThrew("Entity", "Guid", "boom");
                sink.Ambiguous("CharacterHelper", "GetStat", 3, "Entity");
                sink.TypeMissing("CombatPhase");
                sink.Note("something else");
                TestHarness.Equal(6, sink.Count, "six distinct warnings");
            });

            TestHarness.Run("ignores null and empty messages", delegate
            {
                WarningSink sink = new WarningSink();
                sink.Add(null);
                sink.Add("");
                sink.Note(null);
                TestHarness.Equal(0, sink.Count, "nothing recorded");
            });
        }
    }
}
```

- [ ] **Step 2: Implement**

`FTK2.Crucible/src/Crucible.Core/WarningSink.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Collects reflection failures for the snapshot's <c>warnings</c> array.
    ///
    /// This exists because <c>StateReader</c> read <c>NetworkData.PlayerCount</c> — a plausible,
    /// wrong name — and returned null while the log filled with resolution failures, for months,
    /// undetected (SPEC §2). Warnings ride inside the snapshot so a member that vanishes in a game
    /// update surfaces in the next assertion, not in a log nobody reads.
    ///
    /// Deduplicating: a snapshot walks every combatant, so one renamed field on
    /// <c>CharacterComponent</c> would otherwise emit one identical line per entity.
    /// </summary>
    public sealed class WarningSink
    {
        private readonly List<string> _items = new List<string>();
        private readonly object _lock = new object();

        public int Count
        {
            get { lock (_lock) { return _items.Count; } }
        }

        public void Add(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i], message, StringComparison.Ordinal)) return;
                }
                _items.Add(message);
            }
        }

        public void MemberMissing(string typeName, string memberName)
        {
            Add("member_missing: " + typeName + "." + memberName);
        }

        public void MemberThrew(string typeName, string memberName, string detail)
        {
            Add("member_threw: " + typeName + "." + memberName + ": " + detail);
        }

        public void Ambiguous(string typeName, string memberName, int candidates, string argTypeName)
        {
            Add("member_ambiguous: " + typeName + "." + memberName + " has "
                + candidates.ToString(CultureInfo.InvariantCulture)
                + " unary overloads accepting " + argTypeName);
        }

        public void TypeMissing(string typeName)
        {
            Add("type_missing: " + typeName);
        }

        public void Note(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Add("note: " + message);
        }

        public string[] ToArray()
        {
            lock (_lock) { return _items.ToArray(); }
        }

        /// <summary>Snapshot-ready form: <c>MiniJson</c> serializes <c>List&lt;object&gt;</c> only.</summary>
        public List<object> ToJsonList()
        {
            List<object> result = new List<object>();
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++) result.Add(_items[i]);
            }
            return result;
        }
    }
}
```

- [ ] **Step 3: Register the suite and run it**

In `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`, add `WarningTests.RunAll();` immediately after `DigestTests.RunAll();`.

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, 40 tests (36 existing + 4).

---

### Task 3: Core — `MemberResolver`, reflection with a warning contract

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/MemberResolver.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/ResolverTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: `WarningSink`.
- Produces (`public static class FTK2Mods.Crucible.MemberResolver`):
  - `object GetMember(object instance, string memberName, WarningSink warnings)`
  - `FieldInfo FindField(Type type, string name)` / `PropertyInfo FindProperty(Type type, string name)`
  - `MethodInfo FindUnaryStatic(Type declaring, string methodName, object arg, WarningSink warnings)`
  - `object InvokeStatic(MethodInfo method, object arg, string label, WarningSink warnings)`
  - `object FindComponentByTypeName(object componentMap, string typeName, WarningSink warnings)`
  - `bool TypeNameMatches(Type type, string typeName)`
  - `int? AsInt(object)`, `string AsString(object)`, `bool? AsBool(object)`

**Why this is in Core and not Plugin.** `System.Reflection` ships in `netstandard2.0`; nothing here needs Unity or the game. Putting it in Core means the two genuinely subtle behaviours — walking base types for non-public members, and choosing among overloads — are driven by fake types with deliberately wrong inputs. `CharacterHelper.GetStat` has **8 overloads** (field map); an overload picker with no negative control is a picker nobody has proven can refuse.

Two field-map facts shape the implementation directly. `Entity` exposes `Guid` as a **property** whose backing field is named `<Guid>k__BackingField`, so a field-only lookup finds nothing and must fall through to properties. `CombatPhase._activeCharacterEntity` is a **property**, not a field, despite the leading underscore. Hence: field first, then property, both non-public, both walking the base chain.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/ResolverTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;

namespace FTK2Mods.Crucible.Tests
{
    // ---- fakes standing in for game types (no game, no Unity) ----

    internal interface IFirst { }
    internal interface ISecond { }

    internal class FakeBase
    {
        private int _baseOnlyField = 7;
        public int ReadBaseOnlyField() { return _baseOnlyField; }
    }

    internal sealed class FakeEntity : FakeBase, IFirst, ISecond
    {
        public string Name = "goblin";
        private string BackingOnly { get { return "prop-value"; } }
        public string Guid { get { return "e-1"; } }
        public string ReadBackingOnly() { return BackingOnly; }
    }

    internal sealed class ThrowingHolder
    {
        public int Boom { get { throw new InvalidOperationException("kaboom"); } }
    }

    internal sealed class CharacterComponent { public string DisplayName = "Bard"; }
    internal class ComponentBase { }
    internal sealed class PlayerComponent : ComponentBase { }
    internal sealed class PlayerComponentExtra { }

    internal static class FakeStatics
    {
        public static int Unary(FakeBase x) { return 1; }
        public static int Unary(FakeEntity x) { return 2; }
        public static int Ambiguous(IFirst x) { return 1; }
        public static int Ambiguous(ISecond x) { return 2; }
        public static int Binary(FakeEntity x, int y) { return 3; }
        public static int Throws(FakeEntity x) { throw new InvalidOperationException("nope"); }
    }

    internal static class ResolverTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("MemberResolver — members");

            TestHarness.Run("reads a public field", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "Name", w);
                TestHarness.Equal("goblin", v as string, "field value");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            TestHarness.Run("falls through to a property when no field matches", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "Guid", w);
                TestHarness.Equal("e-1", v as string, "property value");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            TestHarness.Run("reads a non-public property", delegate
            {
                object v = MemberResolver.GetMember(new FakeEntity(), "BackingOnly", null);
                TestHarness.Equal("prop-value", v as string, "private property");
            });

            TestHarness.Run("reads a private field declared on a base type", delegate
            {
                object v = MemberResolver.GetMember(new FakeEntity(), "_baseOnlyField", null);
                TestHarness.Equal(7, MemberResolver.AsInt(v).Value, "base-declared private field");
            });

            TestHarness.Run("a missing member warns and returns null", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "PlayerCount", w);
                TestHarness.True(v == null, "null");
                TestHarness.Equal("member_missing: FakeEntity.PlayerCount", w.ToArray()[0], "loud");
            });

            // Negative control: a member that IS there must not be reported as missing.
            TestHarness.Run("an existing member emits no warning", delegate
            {
                WarningSink w = new WarningSink();
                MemberResolver.GetMember(new FakeEntity(), "Name", w);
                TestHarness.Equal(0, w.Count, "silent on success");
            });

            TestHarness.Run("a throwing property warns and returns null", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new ThrowingHolder(), "Boom", w);
                TestHarness.True(v == null, "null");
                TestHarness.True(w.ToArray()[0].StartsWith("member_threw: ThrowingHolder.Boom"), "loud");
            });

            TestHarness.Run("a null instance is not an error", delegate
            {
                WarningSink w = new WarningSink();
                TestHarness.True(MemberResolver.GetMember(null, "Name", w) == null, "null in, null out");
                TestHarness.Equal(0, w.Count, "no warning for an absent parent");
            });

            TestHarness.Section("MemberResolver — static overloads");

            TestHarness.Run("picks the most derived unary overload", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Unary", new FakeEntity(), w);
                TestHarness.True(m != null, "resolved");
                TestHarness.Equal("FakeEntity", m.GetParameters()[0].ParameterType.Name, "most derived wins");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            // Negative control: unrelated interface overloads are genuinely ambiguous and must refuse.
            TestHarness.Run("refuses unrelated ambiguous overloads and says so", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Ambiguous", new FakeEntity(), w);
                TestHarness.True(m == null, "refused");
                TestHarness.True(w.ToArray()[0].StartsWith("member_ambiguous: FakeStatics.Ambiguous"), "loud");
            });

            TestHarness.Run("ignores methods of the wrong arity", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Binary", new FakeEntity(), w);
                TestHarness.True(m == null, "a two-parameter overload is not unary");
                TestHarness.True(w.ToArray()[0].StartsWith("member_missing: FakeStatics.Binary"), "loud");
            });

            TestHarness.Run("reports a throwing target instead of propagating", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Throws", new FakeEntity(), w);
                object v = MemberResolver.InvokeStatic(m, new FakeEntity(), "FakeStatics.Throws", w);
                TestHarness.True(v == null, "null");
                TestHarness.True(w.ToArray()[0].StartsWith("invoke_failed: FakeStatics.Throws"), "unwrapped");
            });

            TestHarness.Section("MemberResolver — components");

            TestHarness.Run("matches a component by exact type name", delegate
            {
                object v = MemberResolver.FindComponentByTypeName(Components(), "CharacterComponent", null);
                TestHarness.True(v is CharacterComponent, "found");
            });

            TestHarness.Run("matches a component by a base type name", delegate
            {
                object v = MemberResolver.FindComponentByTypeName(Components(), "ComponentBase", null);
                TestHarness.True(v is PlayerComponent, "found via base type");
            });

            // Negative control: a similarly-named type must not satisfy the check.
            TestHarness.Run("does not match a similarly named type", delegate
            {
                Dictionary<string, object> map = new Dictionary<string, object>();
                map["x"] = new PlayerComponentExtra();
                TestHarness.True(MemberResolver.FindComponentByTypeName(map, "PlayerComponent", null) == null,
                    "PlayerComponentExtra is not PlayerComponent");
            });

            TestHarness.Run("warns when the component map is not a dictionary", delegate
            {
                WarningSink w = new WarningSink();
                TestHarness.True(MemberResolver.FindComponentByTypeName("not a map", "PlayerComponent", w) == null,
                    "null");
                TestHarness.True(w.ToArray()[0].StartsWith("note: component map unavailable"), "loud");
            });
        }

        private static Dictionary<string, object> Components()
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["character"] = new CharacterComponent();
            map["player"] = new PlayerComponent();
            return map;
        }
    }
}
```

`ReadBaseOnlyField` and `ReadBackingOnly` exist so the compiler does not warn the private members are unused; they are not otherwise called.

- [ ] **Step 2: Implement**

`FTK2.Crucible/src/Crucible.Core/MemberResolver.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Reflection primitives with a warning contract: every lookup that fails reports into a
    /// <see cref="WarningSink"/> rather than returning a quiet null (SPEC §2 corollary).
    ///
    /// Host-agnostic on purpose — <c>System.Reflection</c> is part of netstandard2.0, so the two
    /// genuinely subtle behaviours (walking base types for non-public members, and choosing among
    /// overloads) are unit-testable against fake types with no game running.
    /// </summary>
    public static class MemberResolver
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Field first, then property. Both halves matter to real game types: <c>Entity.Guid</c> is a
        /// property whose backing field is named <c>&lt;Guid&gt;k__BackingField</c>, so a field-only
        /// lookup finds nothing; <c>CombatPhase._activeCharacterEntity</c> is a property despite the
        /// leading underscore.
        /// </summary>
        public static object GetMember(object instance, string memberName, WarningSink warnings)
        {
            if (instance == null || string.IsNullOrEmpty(memberName)) return null;
            Type type = instance.GetType();
            try
            {
                FieldInfo field = FindField(type, memberName);
                if (field != null) return field.GetValue(instance);

                PropertyInfo prop = FindProperty(type, memberName);
                if (prop != null && prop.CanRead) return prop.GetValue(instance, null);

                if (warnings != null) warnings.MemberMissing(type.Name, memberName);
                return null;
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.MemberThrew(type.Name, memberName, Unwrap(ex).Message);
                return null;
            }
        }

        /// <summary>DeclaredOnly, one level at a time: BindingFlags.FlattenHierarchy does not surface
        /// non-public members of base types, and game state classes inherit plenty of them.</summary>
        public static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f != null) return f;
            }
            return null;
        }

        public static PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, AnyInstance);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>
        /// Finds the one static method named <paramref name="methodName"/> taking a single parameter
        /// that accepts <paramref name="arg"/>. Selection is by runtime assignability, never by a
        /// written signature, so the caller only needs to have grounded the member *name*.
        ///
        /// <c>CharacterHelper.GetStat</c> has 8 overloads (field map), so refusing is a real outcome:
        /// two unrelated candidates yield null plus a <c>member_ambiguous</c> warning rather than a
        /// coin flip.
        /// </summary>
        public static MethodInfo FindUnaryStatic(Type declaring, string methodName, object arg, WarningSink warnings)
        {
            if (declaring == null || string.IsNullOrEmpty(methodName) || arg == null) return null;

            List<MethodInfo> matches = new List<MethodInfo>();
            for (Type t = declaring; t != null; t = t.BaseType)
            {
                MethodInfo[] all = t.GetMethods(AnyStatic);
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo m = all[i];
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                    if (m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(arg)) continue;
                    matches.Add(m);
                }
            }

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
            {
                if (warnings != null)
                    warnings.MemberMissing(declaring.Name,
                        methodName + "(<one parameter accepting " + arg.GetType().Name + ">)");
                return null;
            }

            MethodInfo best = MostDerived(matches);
            if (best != null) return best;

            if (warnings != null)
                warnings.Ambiguous(declaring.Name, methodName, matches.Count, arg.GetType().Name);
            return null;
        }

        /// <summary>
        /// The candidate whose parameter type is assignable to every other candidate's — the most
        /// derived one. Null when no single candidate dominates (two unrelated interfaces), which is
        /// an ambiguity the caller must refuse rather than guess at.
        /// </summary>
        private static MethodInfo MostDerived(List<MethodInfo> candidates)
        {
            MethodInfo best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                Type ci = candidates[i].GetParameters()[0].ParameterType;
                bool dominatesAll = true;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j) continue;
                    Type cj = candidates[j].GetParameters()[0].ParameterType;
                    if (!cj.IsAssignableFrom(ci)) { dominatesAll = false; break; }
                }
                if (!dominatesAll) continue;
                if (best != null) return null;
                best = candidates[i];
            }
            return best;
        }

        public static object InvokeStatic(MethodInfo method, object arg, string label, WarningSink warnings)
        {
            if (method == null) return null;
            try
            {
                return method.Invoke(null, new object[] { arg });
            }
            catch (Exception ex)
            {
                if (warnings != null) warnings.Add("invoke_failed: " + label + ": " + Unwrap(ex).Message);
                return null;
            }
        }

        /// <summary>
        /// Finds a component by type name inside <c>Entity.Components</c>.
        ///
        /// Scans values, not keys: the field map records <c>Components</c> as a <c>Dictionary`2</c>
        /// without telling us what the key is, and the values carry their own types. Base-type names
        /// match too, so a subclassed component still resolves.
        /// </summary>
        public static object FindComponentByTypeName(object componentMap, string typeName, WarningSink warnings)
        {
            IDictionary map = componentMap as IDictionary;
            if (map == null)
            {
                if (warnings != null)
                    warnings.Note("component map unavailable (expected IDictionary, got "
                        + (componentMap == null ? "null" : componentMap.GetType().Name) + ")");
                return null;
            }

            foreach (object value in map.Values)
            {
                if (value == null) continue;
                if (TypeNameMatches(value.GetType(), typeName)) return value;
            }

            if (warnings != null) warnings.Note("component not present: " + typeName);
            return null;
        }

        public static bool TypeNameMatches(Type type, string typeName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (string.Equals(t.Name, typeName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static int? AsInt(object value)
        {
            if (value == null) return null;
            if (value is int) return (int)value;
            if (value is long || value is short || value is byte || value is uint || value is Enum)
            {
                try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
                catch (Exception) { return null; }
            }
            return null;
        }

        public static string AsString(object value)
        {
            if (value == null) return null;
            string s = value as string;
            return s != null ? s : value.ToString();
        }

        public static bool? AsBool(object value)
        {
            if (value is bool) return (bool)value;
            return null;
        }

        private static Exception Unwrap(Exception ex)
        {
            TargetInvocationException tie = ex as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : ex;
        }
    }
}
```

- [ ] **Step 3: Register the suite and run it**

In `Program.cs`, add `ResolverTests.RunAll();` immediately after `WarningTests.RunAll();`.

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, 56 tests (40 + 16).

- [ ] **Step 4: Commit**

```bash
git add FTK2.Crucible/src/Crucible.Core/WarningSink.cs \
        FTK2.Crucible/src/Crucible.Core/MemberResolver.cs \
        FTK2.Crucible/src/Crucible.Core.Tests
git commit -m "Crucible.Core: warning sink and reflection resolver with overload selection"
```

---

### Task 4: Core — `TurnTracker`, the synthesized turn/phase state machine

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/TurnTracker.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/TurnTrackerTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public sealed class FTK2Mods.Crucible.TurnEvent { public string Kind; public int Turn; public string Phase; public string EntityId; public string Episode; }`
  - `public sealed class FTK2Mods.Crucible.TurnTracker`
    - `TurnTracker(int maxEvents)`
    - `public bool HooksInstalled;`
    - `int DroppedEvents { get; }`
    - `void OnObserved(bool combatActive, int episodeKey)`
    - `void OnTurnAdvanced()`
    - `void OnEntityEngaged(string entityId, bool isPlayer)`
    - `void Read(out bool active, out int turn, out string phase, out string entityId, out string episode)`
    - `TurnEvent[] DrainEvents()`
    - constants `PhaseNone` = `"NONE"`, `PhasePlayer` = `"PLAYER"`, `PhaseEnemy` = `"ENEMY"`, `PhaseUnknown` = `"UNKNOWN"`

**This is the heart of the S1 deviation, so state its contract precisely.**

`turn` is **an ordinal of observed turn advances within one combat episode**, not a value the game holds. It starts at `0` when an episode begins and increments once per `CombatPhase._nextTurn`. It is `-1` when no combat is active (the reader renders that as JSON `null`). It is the one **monotonic** axis in the combat block — `round` is not, because `CombatState.TotalRounds` resets to `-1` per wave.

`phase` is **derived from the engaged entity**: `PLAYER` when the entity engaged by `CombatPhase._engageActiveEntity` carries a `PlayerComponent`, `ENEMY` when it does not, `NONE` between episodes or before the first engagement, `UNKNOWN` when the Harmony hooks never installed.

**Three reset paths, deliberately.** The engine gives no combat-start or combat-end callback that anyone has grounded, so the tracker cannot wait to be told:

1. **Observation says combat stopped** → `OnObserved(false, _)` ends the episode. This is the normal path: the reader calls it on every snapshot.
2. **Observation says a *different* combat is running** → `OnObserved(true, newKey)` where `newKey != _episodeKey` starts a fresh episode. The reader derives the key from the identity of the live `CombatState` object.
3. **A hook fires while the tracker thinks nothing is running** → implicit start. Hooks can fire before the first `/state` call of a fight ever happens; treating that as "turn 0 of a new episode" is strictly better than dropping it or counting it into a stale episode.

**Known limitation, recorded not papered over:** episode keys are object-identity hashes, so a fight that reuses the same `CombatState` instance produces the same key. Reset path 1 covers that (the fight has to stop being active in between), but a wave transition that keeps the object alive and never reads as inactive will keep counting turns upward across waves — which is exactly the behaviour the contract asks for anyway ("monotonic within the encounter"). Task 9 Step 5 measures this against a real multi-wave fight.

**Why events are queued rather than logged directly.** Core cannot reference `TraceWriter`'s host or BepInEx logging, and more importantly the spec (§4/§9) notes the trace currently only ever records `exec`. The transitions this tracker computes are the missing half: a trace that shows a command going in and *nothing* about what the fight did in response is not a trace anyone can debug from. The queue is bounded and counts what it drops, because "silent truncation is forbidden" (§9) applies to traces as much as to scenario reports.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/TurnTrackerTests.cs`:

```csharp
namespace FTK2Mods.Crucible.Tests
{
    internal static class TurnTrackerTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("TurnTracker — counting");

            TestHarness.Run("turn starts at 0 when combat is first observed", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                TestHarness.Equal(0, Turn(t), "first turn is 0");
                TestHarness.True(Active(t), "active");
            });

            TestHarness.Run("each advance increments the turn", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                TestHarness.Equal(3, Turn(t), "three advances");
            });

            TestHarness.Run("turn resets when the episode key changes", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnObserved(true, 222);
                TestHarness.Equal(0, Turn(t), "new fight, new count");
            });

            // Negative control: the SAME episode key must NOT reset the count.
            TestHarness.Run("re-observing the same episode does not reset the turn", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnTurnAdvanced();
                t.OnObserved(true, 111);
                t.OnObserved(true, 111);
                TestHarness.Equal(2, Turn(t), "count survives repeated observation");
            });

            TestHarness.Run("combat end clears turn and phase", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);
                TestHarness.False(Active(t), "inactive");
                TestHarness.Equal(-1, Turn(t), "turn cleared");
                TestHarness.Equal(TurnTracker.PhaseNone, Phase(t), "phase cleared");
            });

            TestHarness.Run("a fresh episode after an end starts at 0 again", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);
                t.OnObserved(true, 111);
                TestHarness.Equal(0, Turn(t), "restarted");
            });

            TestHarness.Run("an advance before any observation implies a combat start", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnTurnAdvanced();
                TestHarness.True(Active(t), "implicit start");
                TestHarness.Equal(0, Turn(t), "counted as turn 0");
            });

            TestHarness.Section("TurnTracker — phase");

            TestHarness.Run("phase follows the engaged entity", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                TestHarness.Equal(TurnTracker.PhasePlayer, Phase(t), "player turn");
                t.OnEntityEngaged("e-2", false);
                TestHarness.Equal(TurnTracker.PhaseEnemy, Phase(t), "enemy turn");
                TestHarness.Equal("e-2", EntityId(t), "active entity recorded");
            });

            TestHarness.Section("TurnTracker — events");

            TestHarness.Run("transitions are emitted in order and drained once", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnEntityEngaged("e-1", true);
                t.OnTurnAdvanced();
                t.OnObserved(false, 0);

                TurnEvent[] events = t.DrainEvents();
                TestHarness.Equal(4, events.Length, "four transitions");
                TestHarness.Equal("combat_start", events[0].Kind, "0");
                TestHarness.Equal("phase", events[1].Kind, "1");
                TestHarness.Equal("turn", events[2].Kind, "2");
                TestHarness.Equal("combat_end", events[3].Kind, "3");
                TestHarness.Equal(0, t.DrainEvents().Length, "drain empties the queue");
            });

            TestHarness.Run("re-engaging the same entity emits no duplicate phase event", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.DrainEvents();
                t.OnEntityEngaged("e-1", true);
                t.OnEntityEngaged("e-1", true);
                TestHarness.Equal(1, t.DrainEvents().Length, "one phase event");
            });

            // Negative control: a genuine change must still emit.
            TestHarness.Run("engaging a different entity does emit a phase event", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.DrainEvents();
                t.OnEntityEngaged("e-1", true);
                t.OnEntityEngaged("e-2", true);
                TestHarness.Equal(2, t.DrainEvents().Length, "two phase events");
            });

            TestHarness.Run("an overfull queue drops the oldest and counts the drops", delegate
            {
                TurnTracker t = new TurnTracker(2);
                t.OnObserved(true, 111);          // combat_start
                t.OnTurnAdvanced();               // turn 1
                t.OnTurnAdvanced();               // turn 2  -> drops combat_start
                TurnEvent[] events = t.DrainEvents();
                TestHarness.Equal(2, events.Length, "capped at 2");
                TestHarness.Equal(1, events[0].Turn, "oldest dropped");
                TestHarness.Equal(1, t.DroppedEvents, "drop counted, not silent");
            });

            // Negative control: under capacity nothing may be reported as dropped.
            TestHarness.Run("nothing is dropped under capacity", delegate
            {
                TurnTracker t = new TurnTracker(64);
                t.OnObserved(true, 111);
                t.OnTurnAdvanced();
                TestHarness.Equal(0, t.DroppedEvents, "no drops");
            });
        }

        private static bool Active(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return active;
        }

        private static int Turn(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return turn;
        }

        private static string Phase(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return phase;
        }

        private static string EntityId(TurnTracker t)
        {
            bool active; int turn; string phase; string id; string episode;
            t.Read(out active, out turn, out phase, out id, out episode);
            return id;
        }
    }
}
```

- [ ] **Step 2: Implement**

`FTK2.Crucible/src/Crucible.Core/TurnTracker.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>One synthesized combat transition, destined for the JSONL trace.</summary>
    public sealed class TurnEvent
    {
        public string Kind;       // combat_start | combat_end | turn | phase
        public int Turn;
        public string Phase;
        public string EntityId;
        public string Episode;
    }

    /// <summary>
    /// Synthesizes <c>combat.turn</c> and <c>combat.phase</c>, neither of which exists as readable
    /// data anywhere in the assembly (field map: "turn and intra-combat phase are genuinely absent…
    /// it's driven procedurally through CombatPhase._engageActiveEntity / _nextTurn").
    ///
    /// The plugin feeds it from two Harmony postfixes and from each snapshot read; this class holds
    /// no game reference at all, so the whole state machine — including every reset path — is
    /// unit-testable with no game running.
    ///
    /// Contract:
    ///  - <c>turn</c> is an ordinal of observed advances within one episode, 0-based, -1 when idle.
    ///    It is the only monotonic axis in the combat block: <c>CombatState.TotalRounds</c> resets to
    ///    -1 per wave, so <c>round</c> is not.
    ///  - <c>phase</c> is PLAYER/ENEMY derived from the engaged entity, NONE when idle.
    /// </summary>
    public sealed class TurnTracker
    {
        public const string PhaseNone = "NONE";
        public const string PhasePlayer = "PLAYER";
        public const string PhaseEnemy = "ENEMY";
        public const string PhaseUnknown = "UNKNOWN";

        private readonly object _lock = new object();
        private readonly Queue<TurnEvent> _events = new Queue<TurnEvent>();
        private readonly int _maxEvents;

        private bool _active;
        private int _episodeKey;
        private int _turn = -1;
        private string _phase = PhaseNone;
        private string _entityId;
        private int _dropped;

        public TurnTracker(int maxEvents)
        {
            _maxEvents = maxEvents < 1 ? 1 : maxEvents;
        }

        /// <summary>
        /// False when the Harmony hooks never installed, so the reader can report
        /// <c>turn: null</c> / <c>phase: "UNKNOWN"</c> plus a warning instead of serving a
        /// permanently-zero counter as if it were a measurement.
        /// </summary>
        public bool HooksInstalled;

        public int DroppedEvents
        {
            get { lock (_lock) { return _dropped; } }
        }

        /// <summary>Called once per snapshot with what the reader can see right now.</summary>
        public void OnObserved(bool combatActive, int episodeKey)
        {
            lock (_lock)
            {
                if (!combatActive)
                {
                    if (_active) EndLocked();
                    return;
                }
                if (!_active || episodeKey != _episodeKey) StartLocked(episodeKey);
            }
        }

        /// <summary>Postfix on <c>CombatPhase._nextTurn</c>.</summary>
        public void OnTurnAdvanced()
        {
            lock (_lock)
            {
                if (!_active) StartLocked(_episodeKey);   // hook fired before any snapshot: turn 0
                else _turn++;
                EmitLocked("turn");
            }
        }

        /// <summary>Postfix on <c>CombatPhase._engageActiveEntity</c>.</summary>
        public void OnEntityEngaged(string entityId, bool isPlayer)
        {
            lock (_lock)
            {
                if (!_active) StartLocked(_episodeKey);
                string phase = isPlayer ? PhasePlayer : PhaseEnemy;
                bool changed = !string.Equals(phase, _phase) || !string.Equals(entityId, _entityId);
                _phase = phase;
                _entityId = entityId;
                if (changed) EmitLocked("phase");
            }
        }

        public void Read(out bool active, out int turn, out string phase, out string entityId, out string episode)
        {
            lock (_lock)
            {
                active = _active;
                turn = _turn;
                phase = _phase;
                entityId = _entityId;
                episode = EpisodeLocked();
            }
        }

        public TurnEvent[] DrainEvents()
        {
            lock (_lock)
            {
                TurnEvent[] all = _events.ToArray();
                _events.Clear();
                return all;
            }
        }

        private void StartLocked(int episodeKey)
        {
            _active = true;
            _episodeKey = episodeKey;
            _turn = 0;
            _phase = PhaseNone;
            _entityId = null;
            EmitLocked("combat_start");
        }

        private void EndLocked()
        {
            _active = false;
            _turn = -1;
            _phase = PhaseNone;
            _entityId = null;
            EmitLocked("combat_end");
        }

        private string EpisodeLocked()
        {
            return _episodeKey.ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Bounded queue: an undrained tracker must not grow without limit. Drops are counted rather
        /// than swallowed — silent truncation is forbidden (SPEC §9).
        /// </summary>
        private void EmitLocked(string kind)
        {
            TurnEvent e = new TurnEvent();
            e.Kind = kind;
            e.Turn = _turn;
            e.Phase = _phase;
            e.EntityId = _entityId;
            e.Episode = EpisodeLocked();
            _events.Enqueue(e);
            while (_events.Count > _maxEvents) { _events.Dequeue(); _dropped++; }
        }
    }
}
```

- [ ] **Step 3: Register the suite and run it**

In `Program.cs`, add `TurnTrackerTests.RunAll();` after `ResolverTests.RunAll();`.

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, 69 tests (56 + 13).

- [ ] **Step 4: Commit**

```bash
git add FTK2.Crucible/src/Crucible.Core/TurnTracker.cs \
        FTK2.Crucible/src/Crucible.Core.Tests/TurnTrackerTests.cs \
        FTK2.Crucible/src/Crucible.Core.Tests/Program.cs
git commit -m "Crucible.Core: synthesized turn/phase tracker with bounded transition events"
```

---

### Task 5: Core — views, `SnapshotShape.BuildV2`, and the v2 redaction set

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Core/CombatViews.cs`
- Create: `FTK2.Crucible/src/Crucible.Core/SnapshotShape.cs`
- Create: `FTK2.Crucible/src/Crucible.Core/Redactions.cs`
- Create: `FTK2.Crucible/src/Crucible.Core.Tests/SnapshotShapeTests.cs`
- Modify: `FTK2.Crucible/src/Crucible.Core.Tests/Program.cs`
- Modify: `FTK2.Crucible/data/Redactions.json`

**Interfaces:**
- Consumes: `WarningSink`, `TurnTracker` (constants only).
- Produces:
  - `public sealed class StatusView { string Id; int? Duration; int? InitialDuration; int? TickDuration; string OriginEntityId; }`
  - `public sealed class CombatantView { string Id; string Name; string ClassId; bool IsPlayer; int? Hp; int? MaxHp; bool? Alive; bool StatusesAvailable; List<StatusView> Statuses; bool StatsAvailable; Dictionary<string,int> Stats; }`
  - `public sealed class CombatView { bool Active; int? Round; int? Wave; int? Turn; string Phase; string ActiveEntityId; string Episode; bool CombatantsAvailable; List<CombatantView> Combatants; }`
  - `public sealed class RunView { bool Present; object Seed; object Day; object Gold; object Chapter; }`
  - `public sealed class NetworkView { bool Online; bool? IsHost; int? PlayerCount; }`
  - `public static class SnapshotShape` — `const string SchemaV2 = "crucible.state.v2"`, `Dictionary<string,object> BuildV2(string instance, string route, RunView run, NetworkView network, CombatView combat, bool consoleShowing, WarningSink warnings)`
  - `public static class Redactions` — `static readonly string[] V2`

**Three rules this task exists to enforce, all of them digest-relevant.**

1. **Unavailable is `null`; empty is `[]`.** A combatant with no statuses serializes `"statuses": []`. A combatant whose `StatusEffectComponent` could not be read serializes `"statuses": null` plus a warning. Collapsing those two would let a reflection failure read as "the ability applied nothing" — a scenario passing because the oracle went blind is the single worst outcome S1 can have.
2. **Order is normalized where reflection does not guarantee it.** `StatusEffectComponent.Statuses` is a `Dictionary`, whose enumeration order is not contractual, so statuses are sorted by id (ordinal). Combatants are **not** re-sorted: `CombatState.Entities` is a `List`, which is the game's own replicated order, and re-sorting by a redacted id would produce a stable-but-meaningless permutation. Object keys are already ordinal-sorted by `MiniJson.Write`.
3. **Runtime-unique identifiers stay out of the digest.** Entity guids, the episode key, and `warnings` are redacted for v2. Two healthy peers must agree on hp, statuses, stats, turn, and phase; they have no obligation to agree on an object-identity hash, and a peer that emitted one extra warning is not a desync.

`combat.synthesized` is emitted as `["turn","phase"]` — a machine-readable marker so a consumer of the snapshot cannot mistake a derived ordinal for a value the game reported. It is a constant, so it costs nothing in the digest.

- [ ] **Step 1: Write the failing tests**

`FTK2.Crucible/src/Crucible.Core.Tests/SnapshotShapeTests.cs`:

```csharp
using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class SnapshotShapeTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("SnapshotShape — shape");

            TestHarness.Run("schema is crucible.state.v2", delegate
            {
                Dictionary<string, object> snap = Build(Combat());
                TestHarness.Equal("crucible.state.v2", snap["schema"] as string, "schema");
            });

            // Negative control: v2 must not be mistakable for v1.
            TestHarness.Run("schema is not the v1 string", delegate
            {
                Dictionary<string, object> snap = Build(Combat());
                TestHarness.NotEqual("crucible.state.v1", snap["schema"] as string, "distinct");
            });

            TestHarness.Run("synthesized marker names turn and phase", delegate
            {
                List<object> marker = (List<object>)Combatless(Build(Combat()))["synthesized"];
                TestHarness.Equal(2, marker.Count, "two synthesized fields");
                TestHarness.Equal("turn", marker[0] as string, "turn");
                TestHarness.Equal("phase", marker[1] as string, "phase");
            });

            TestHarness.Run("statuses are sorted by id", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_01", 3));
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                c.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));

                List<object> statuses = (List<object>)FirstCombatant(Build(c))["statuses"];
                TestHarness.Equal("STATUS_ATTACKUP_00", Str(statuses[0], "id"), "0");
                TestHarness.Equal("STATUS_ATTACKUP_01", Str(statuses[1], "id"), "1");
                TestHarness.Equal("STATUS_CF_ENCMOD_CURSED", Str(statuses[2], "id"), "2");
            });

            TestHarness.Run("a combatant with no statuses serializes an empty array", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                List<object> statuses = (List<object>)FirstCombatant(Build(c))["statuses"];
                TestHarness.Equal(0, statuses.Count, "empty, not null");
            });

            // Negative control for rule 1: unreadable must NOT look like empty.
            TestHarness.Run("unreadable statuses serialize as null, not an empty array", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = false;
                TestHarness.True(FirstCombatant(Build(c))["statuses"] == null, "null");
            });

            TestHarness.Run("available stats serialize as an object", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatsAvailable = true;
                c.Combatants[0].Stats["STR"] = 4;
                Dictionary<string, object> stats =
                    (Dictionary<string, object>)FirstCombatant(Build(c))["stats"];
                TestHarness.Equal(4, (int)stats["STR"], "value");
            });

            TestHarness.Run("unavailable stats serialize as null, not an empty object", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatsAvailable = false;
                TestHarness.True(FirstCombatant(Build(c))["stats"] == null, "null");
            });

            TestHarness.Run("unreadable combatants serialize as null, not an empty array", delegate
            {
                CombatView c = Combat();
                c.CombatantsAvailable = false;
                TestHarness.True(Combatless(Build(c))["combatants"] == null, "null");
            });

            TestHarness.Run("a null combat view still yields an inactive combat object", delegate
            {
                Dictionary<string, object> combat = Combatless(Build(null));
                TestHarness.Equal(false, (bool)combat["active"], "inactive");
                TestHarness.True(combat["combatants"] == null, "no combatants");
            });

            TestHarness.Run("warnings are carried into the snapshot", delegate
            {
                WarningSink w = new WarningSink();
                w.MemberMissing("CharacterComponent", "DisplayName");
                Dictionary<string, object> snap = SnapshotShape.BuildV2(
                    "p1", "COMBAT", Run(), Network(), Combat(), false, w);
                List<object> warnings = (List<object>)snap["warnings"];
                TestHarness.Equal(1, warnings.Count, "one warning");
                TestHarness.Equal("member_missing: CharacterComponent.DisplayName",
                    warnings[0] as string, "text");
            });

            TestHarness.Run("the v2 snapshot round-trips through MiniJson", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                c.Combatants[0].StatsAvailable = true;
                c.Combatants[0].Stats["STR"] = 4;

                object parsed; string error;
                TestHarness.True(MiniJson.TryParse(MiniJson.Write(Build(c)), out parsed, out error),
                    "parses: " + error);
                TestHarness.True(MiniJson.AsObject(parsed) != null, "object at the root");
            });

            TestHarness.Section("SnapshotShape — v2 digest");

            TestHarness.Run("entity ids do not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                CombatView b = Combat();
                b.Combatants[0].Id = "a-totally-different-guid";
                b.ActiveEntityId = "a-totally-different-guid";
                b.Episode = "deadbeef";
                TestHarness.Equal(Digest(a), Digest(b), "runtime ids redacted");
            });

            // Negative control: real state changes MUST move the digest.
            TestHarness.Run("an hp change does affect the v2 digest", delegate
            {
                CombatView a = Combat();
                CombatView b = Combat();
                b.Combatants[0].Hp = 12;
                TestHarness.NotEqual(Digest(a), Digest(b), "hp is in the digest");
            });

            TestHarness.Run("warnings do not affect the v2 digest", delegate
            {
                WarningSink noisy = new WarningSink();
                noisy.MemberMissing("CharacterComponent", "DisplayName");
                string quiet = StateDigest.Compute(
                    SnapshotShape.BuildV2("p1", "COMBAT", Run(), Network(), Combat(), false, new WarningSink()),
                    Redactions.V2);
                string loud = StateDigest.Compute(
                    SnapshotShape.BuildV2("p2", "COMBAT", Run(), Network(), Combat(), false, noisy),
                    Redactions.V2);
                TestHarness.Equal(quiet, loud, "warnings and instance redacted");
            });

            TestHarness.Run("status order does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].StatusesAvailable = true;
                a.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                a.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));

                CombatView b = Combat();
                b.Combatants[0].StatusesAvailable = true;
                b.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));
                b.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));

                TestHarness.Equal(Digest(a), Digest(b), "sorted before hashing");
            });

            // Negative control for the sort: a different status set must still differ.
            TestHarness.Run("a different status set does affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].StatusesAvailable = true;
                a.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));

                CombatView b = Combat();
                b.Combatants[0].StatusesAvailable = true;
                b.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_01", 2));

                TestHarness.NotEqual(Digest(a), Digest(b), "different ids, different digest");
            });
        }

        // ---- builders ----

        private static Dictionary<string, object> Build(CombatView combat)
        {
            return SnapshotShape.BuildV2("p1", "COMBAT", Run(), Network(), combat, false, new WarningSink());
        }

        private static string Digest(CombatView combat)
        {
            return StateDigest.Compute(Build(combat), Redactions.V2);
        }

        private static RunView Run()
        {
            RunView r = new RunView();
            r.Present = true;
            r.Seed = 12345;
            r.Day = 3;
            r.Gold = 120;
            r.Chapter = "CHAPTER_1";
            return r;
        }

        private static NetworkView Network()
        {
            NetworkView n = new NetworkView();
            n.Online = false;
            n.IsHost = true;
            n.PlayerCount = 1;
            return n;
        }

        private static CombatView Combat()
        {
            CombatantView c = new CombatantView();
            c.Id = "e-1";
            c.Name = "Bard";
            c.ClassId = "CF_EOR_BARD";
            c.IsPlayer = true;
            c.Hp = 34;
            c.MaxHp = 40;
            c.Alive = true;

            CombatView v = new CombatView();
            v.Active = true;
            v.Round = 2;
            v.Wave = 0;
            v.Turn = 1;
            v.Phase = TurnTracker.PhasePlayer;
            v.ActiveEntityId = "e-1";
            v.Episode = "0000002a";
            v.CombatantsAvailable = true;
            v.Combatants.Add(c);
            return v;
        }

        private static StatusView Status(string id, int duration)
        {
            StatusView s = new StatusView();
            s.Id = id;
            s.Duration = duration;
            s.InitialDuration = duration;
            s.TickDuration = 0;
            s.OriginEntityId = "e-9";
            return s;
        }

        // ---- accessors ----

        private static Dictionary<string, object> Combatless(Dictionary<string, object> snap)
        {
            return (Dictionary<string, object>)snap["combat"];
        }

        private static Dictionary<string, object> FirstCombatant(Dictionary<string, object> snap)
        {
            List<object> combatants = (List<object>)Combatless(snap)["combatants"];
            return (Dictionary<string, object>)combatants[0];
        }

        private static string Str(object node, string key)
        {
            return ((Dictionary<string, object>)node)[key] as string;
        }
    }
}
```

`Combatless` returns the `combat` sub-object; the name is a small joke about it being the object *around* the combatants, and it is used by three tests.

- [ ] **Step 2: Implement the views**

`FTK2.Crucible/src/Crucible.Core/CombatViews.cs`:

```csharp
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Plain transport between the plugin's reflection reader and <see cref="SnapshotShape"/>.
    ///
    /// Deliberately dumb: no game types, no reflection, no logic. The reader fills these in; the
    /// shape builder decides how they serialize. That split is what lets every shape/sort/null rule
    /// be tested with no game running.
    ///
    /// Note the *Available flags. "Could not read it" and "there is none" must serialize
    /// differently — null versus an empty collection — or a reflection failure would read as
    /// "the ability applied nothing", and a scenario would pass because the oracle went blind.
    /// </summary>
    public sealed class StatusView
    {
        public string Id;
        public int? Duration;
        public int? InitialDuration;
        public int? TickDuration;
        public string OriginEntityId;
        // No Stacks: StatusEffectInfo has no stack count, and the suffixed-id convention
        // (STATUS_ATTACKUP_00/_01) is unverified. See the plan's "stacks decision".
    }

    public sealed class CombatantView
    {
        public string Id;
        public string Name;
        public string ClassId;
        public bool IsPlayer;
        public int? Hp;
        public int? MaxHp;
        public bool? Alive;

        public bool StatusesAvailable;
        public readonly List<StatusView> Statuses = new List<StatusView>();

        public bool StatsAvailable;
        /// <summary>String-keyed: <c>eStats</c> does not exist anywhere in the assembly.</summary>
        public readonly Dictionary<string, int> Stats = new Dictionary<string, int>();
    }

    public sealed class CombatView
    {
        public bool Active;
        /// <summary>CombatState.TotalRounds. NOT monotonic — resets to -1 per wave.</summary>
        public int? Round;
        /// <summary>CombatState.WaveIndex, so a consumer can segment a non-monotonic round.</summary>
        public int? Wave;
        /// <summary>Synthesized ordinal. Null when the hooks did not install.</summary>
        public int? Turn;
        /// <summary>Synthesized. PLAYER / ENEMY / NONE / UNKNOWN.</summary>
        public string Phase = TurnTracker.PhaseNone;
        public string ActiveEntityId;
        public string Episode;

        public bool CombatantsAvailable;
        /// <summary>CombatState.Entities order, preserved: it is the game's own replicated order.</summary>
        public readonly List<CombatantView> Combatants = new List<CombatantView>();
    }

    public sealed class RunView
    {
        public bool Present;
        public object Seed;
        public object Day;
        public object Gold;
        public object Chapter;
    }

    public sealed class NetworkView
    {
        public bool Online;
        public bool? IsHost;
        public int? PlayerCount;
    }
}
```

- [ ] **Step 3: Implement the shape builder**

`FTK2.Crucible/src/Crucible.Core/SnapshotShape.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// The one place the <c>crucible.state.v2</c> JSON shape is defined.
    ///
    /// Emits only <c>Dictionary&lt;string,object&gt;</c> / <c>List&lt;object&gt;</c> / primitives,
    /// which is exactly what <see cref="MiniJson"/> serializes — anything else would silently
    /// stringify. Object keys need no sorting here (MiniJson sorts them ordinally); arrays do,
    /// wherever the source was a dictionary whose enumeration order is not contractual.
    /// </summary>
    public static class SnapshotShape
    {
        public const string SchemaV2 = "crucible.state.v2";

        public static Dictionary<string, object> BuildV2(string instance, string route, RunView run,
            NetworkView network, CombatView combat, bool consoleShowing, WarningSink warnings)
        {
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaV2;
            root["instance"] = instance;
            root["route"] = route;
            root["run"] = BuildRun(run);
            root["network"] = BuildNetwork(network);
            root["combat"] = BuildCombat(combat);
            root["console"] = consoleShowing;
            root["warnings"] = warnings == null ? new List<object>() : warnings.ToJsonList();
            return root;
        }

        private static Dictionary<string, object> BuildRun(RunView run)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["present"] = run != null && run.Present;
            if (run != null && run.Present)
            {
                d["seed"] = Scalar(run.Seed);
                d["day"] = Scalar(run.Day);
                d["gold"] = Scalar(run.Gold);
                d["chapter"] = Scalar(run.Chapter);
            }
            return d;
        }

        private static Dictionary<string, object> BuildNetwork(NetworkView net)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["online"] = net != null && net.Online;
            d["isHost"] = net == null ? null : Box(net.IsHost);
            d["playerCount"] = net == null ? null : Box(net.PlayerCount);
            return d;
        }

        private static Dictionary<string, object> BuildCombat(CombatView c)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["synthesized"] = Synthesized();

            if (c == null)
            {
                d["active"] = false;
                d["round"] = null;
                d["wave"] = null;
                d["turn"] = null;
                d["phase"] = TurnTracker.PhaseNone;
                d["activeId"] = null;
                d["episode"] = null;
                d["combatants"] = null;
                return d;
            }

            d["active"] = c.Active;
            d["round"] = Box(c.Round);
            d["wave"] = Box(c.Wave);
            d["turn"] = Box(c.Turn);
            d["phase"] = c.Phase;
            d["activeId"] = c.ActiveEntityId;
            d["episode"] = c.Episode;
            d["combatants"] = c.CombatantsAvailable ? (object)BuildCombatants(c.Combatants) : null;
            return d;
        }

        private static List<object> BuildCombatants(List<CombatantView> combatants)
        {
            List<object> result = new List<object>();
            for (int i = 0; i < combatants.Count; i++)
            {
                CombatantView c = combatants[i];
                if (c == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = c.Id;
                d["name"] = c.Name;
                d["classId"] = c.ClassId;
                d["isPlayer"] = c.IsPlayer;
                d["hp"] = Box(c.Hp);
                d["maxHp"] = Box(c.MaxHp);
                d["alive"] = Box(c.Alive);
                d["statuses"] = c.StatusesAvailable ? (object)BuildStatuses(c.Statuses) : null;
                d["stats"] = c.StatsAvailable ? (object)BuildStats(c.Stats) : null;
                result.Add(d);
            }
            return result;
        }

        /// <summary>
        /// Sorted by id: the source is <c>StatusEffectComponent.Statuses</c>, a Dictionary whose
        /// enumeration order is not contractual, and an unsorted array would make two identical
        /// peers produce different digests.
        /// </summary>
        private static List<object> BuildStatuses(List<StatusView> statuses)
        {
            List<StatusView> sorted = new List<StatusView>(statuses);
            sorted.Sort(CompareStatus);

            List<object> result = new List<object>();
            for (int i = 0; i < sorted.Count; i++)
            {
                StatusView s = sorted[i];
                if (s == null) continue;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = s.Id;
                d["duration"] = Box(s.Duration);
                d["initialDuration"] = Box(s.InitialDuration);
                d["tickDuration"] = Box(s.TickDuration);
                d["originEntityId"] = s.OriginEntityId;
                result.Add(d);
            }
            return result;
        }

        private static int CompareStatus(StatusView a, StatusView b)
        {
            string x = a == null || a.Id == null ? string.Empty : a.Id;
            string y = b == null || b.Id == null ? string.Empty : b.Id;
            return string.CompareOrdinal(x, y);
        }

        private static Dictionary<string, object> BuildStats(Dictionary<string, int> stats)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            foreach (KeyValuePair<string, int> kv in stats) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>
        /// Machine-readable honesty: these two fields are computed by Crucible, not read from the
        /// game, because the engine holds neither as data (field map).
        /// </summary>
        private static List<object> Synthesized()
        {
            List<object> l = new List<object>();
            l.Add("turn");
            l.Add("phase");
            return l;
        }

        private static object Box(int? v) { return v.HasValue ? (object)v.Value : null; }
        private static object Box(bool? v) { return v.HasValue ? (object)v.Value : null; }

        /// <summary>Primitives stay primitive so the digest can quantize; everything else
        /// stringifies rather than being walked as an arbitrary object graph.</summary>
        private static object Scalar(object value)
        {
            if (value == null) return null;
            if (value is bool || value is int || value is long || value is double || value is float) return value;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
```

- [ ] **Step 4: Implement the redaction set**

`FTK2.Crucible/src/Crucible.Core/Redactions.cs`:

```csharp
using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Field-path globs excluded from the v2 state digest.
    ///
    /// These are values that legitimately differ between healthy peers or that tick on their own;
    /// leaving them in would make every cross-peer comparison report a false desync.
    ///
    /// Paths are dotted and array elements inherit their array's path (see
    /// <c>StateDigest.RedactNode</c>), so <c>combat.combatants.id</c> covers every combatant's id.
    ///
    /// Redacting a field does NOT hide it: it is still in the snapshot body that humans and
    /// assertions read. It is only excluded from the cross-peer hash.
    /// </summary>
    public static class Redactions
    {
        public static readonly string[] V2 = new string[]
        {
            "instance",                                    // peer identity, by definition different
            "warnings",                                    // one peer noticing a miss is not a desync
            "console",                                     // local UI state
            "network.isHost",                              // exactly one peer is the host
            "combat.episode",                              // object-identity hash, local to a process
            "combat.activeId",                             // entity guid; 'phase' carries the meaning
            "combat.combatants.id",                        // entity guids
            "combat.combatants.statuses.originEntityId"    // entity guids
        };

        /// <summary>Defensive copy: callers must not be able to mutate the shared set.</summary>
        public static string[] CopyV2()
        {
            string[] copy = new string[V2.Length];
            Array.Copy(V2, copy, V2.Length);
            return copy;
        }
    }
}
```

- [ ] **Step 5: Mirror the globs into the documented data file**

`FTK2.Crucible/data/Redactions.json` is documentation today — nothing loads it at runtime (`RpcServer.HandleState` passes its globs inline). That is pre-existing debt and is **not** fixed here; wiring it is a separate change with its own failure modes. Keep the file honest instead by adding the v2 set alongside the v1 set:

```json
{
  "_comment": [
    "Field-path globs excluded from the state digest (SPEC §4).",
    "These are values that legitimately differ between healthy peers or tick on their own;",
    "leaving them in would make every cross-peer comparison report a false desync.",
    "Paths are dotted (e.g. 'network.playerCount'); glob syntax matches DevKit's GlobMatcher.",
    "NOT loaded at runtime: the authority is Crucible.Core/Redactions.cs (v2) and the inline",
    "array in RpcServer.HandleState (v1). This file documents both; keep them in step."
  ],
  "Globs": [
    "instance",
    "warnings",
    "console",
    "network.isHost",
    "*elapsed*",
    "*frameCount*",
    "*timestamp*",
    "*Timestamp*"
  ],
  "GlobsV2": [
    "instance",
    "warnings",
    "console",
    "network.isHost",
    "combat.episode",
    "combat.activeId",
    "combat.combatants.id",
    "combat.combatants.statuses.originEntityId"
  ]
}
```

- [ ] **Step 6: Register the suite and run it**

In `Program.cs`, add `SnapshotShapeTests.RunAll();` after `TurnTrackerTests.RunAll();`. The finished `Main` body reads:

```csharp
            JsonTests.RunAll();
            CommandTests.RunAll();
            TraceTests.RunAll();
            DigestTests.RunAll();
            WarningTests.RunAll();
            ResolverTests.RunAll();
            TurnTrackerTests.RunAll();
            SnapshotShapeTests.RunAll();
```

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
```

Expected: exit 0, 86 tests (69 + 17).

- [ ] **Step 7: Commit**

```bash
git add FTK2.Crucible/src/Crucible.Core FTK2.Crucible/src/Crucible.Core.Tests FTK2.Crucible/data/Redactions.json
git commit -m "Crucible.Core: v2 snapshot shape, combat views, and the v2 redaction set"
```

---

### Task 6: Plugin — `CombatReader` and `CharacterHelperBridge`

**Blocked on Task 1.** Every member named below is in the field map; the four that Task 1 adds (`GameRunData.CombatState`, `GameRunData.Seed`/`.Day`/`.Gold`/`.Chapter`, `RouterHelper.GetCurrentRoute`, `CharacterHelper` signatures) must be confirmed or corrected in the addendum before this task is written.

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/CharacterHelperBridge.cs`
- Create: `FTK2.Crucible/src/Crucible.Plugin/CombatReader.cs`
- Create: `FTK2.Crucible/src/Crucible.Plugin/StateReaderV2.cs`

**Interfaces:**
- Consumes: `MemberResolver`, `WarningSink`, `TurnTracker`, `CombatView`/`CombatantView`/`StatusView`/`RunView`/`NetworkView`, `SnapshotShape.BuildV2`, `GameBridge`, `TurnHooks.Tracker` (Task 7).
- Produces:
  - `internal static object CharacterHelperBridge.Invoke(string methodName, object entity, object character, WarningSink warnings)`
  - `internal static CombatView CombatReader.Read(object gameRun, TurnTracker tracker, WarningSink warnings)`
  - `internal static Dictionary<string,object> StateReaderV2.Snapshot()`

**Every game member this task touches, and where the field map says it lives:**

| Member | Field-map entry | Used for |
|---|---|---|
| `CombatPhase` (type) | `### CombatPhase` table | root of the live fight |
| `CombatPhase._combatState` | `prop \| CombatState \| _combatState` | primary path to `CombatState` |
| `CombatPhase._activeCharacterEntity` | `prop \| Entity \| _activeCharacterEntity` | `combat.activeId` (map resolves `combat.active` here) |
| `CombatPhase._lastEngagedEntity` | `field \| Entity \| _lastEngagedEntity` | fallback for the above |
| `CombatState.Entities` | `field \| List\`1 \| Entities` | `combat.combatants[]` |
| `CombatState.TotalRounds` | `field \| Int32 \| TotalRounds` | `combat.round` (non-monotonic) |
| `CombatState.WaveIndex` | `field \| Int32 \| WaveIndex` | `combat.wave` |
| `Entity.Guid` | `prop \| String \| Guid` | `combatant.id` |
| `Entity.Components` | `field \| Dictionary\`2 \| Components` | component lookup |
| `PlayerComponent` (type) | `### PlayerComponent` table | `combatant.isPlayer`, by presence |
| `CharacterComponent` (type) | `### CharacterComponent` table | name/class/hp |
| `CharacterComponent.DisplayName` | `field \| String \| DisplayName` | `combatant.name` |
| `CharacterComponent.ConfigName` | `field \| String \| ConfigName` | `combatant.classId` |
| `CharacterComponent.CurrentHealth` | `field \| Int32 \| CurrentHealth` | `combatant.hp` |
| `StatusEffectComponent` (type) | `### StatusEffectComponent` table | statuses |
| `StatusEffectComponent.Statuses` | `field \| Dictionary\`2 \| Statuses` | `combatant.statuses[]` |
| `StatusEffectInfo.Duration` | `field \| Int32 \| Duration` | `status.duration` |
| `StatusEffectInfo.InitialDuration` | `field \| Int32 \| InitialDuration` | `status.initialDuration` |
| `StatusEffectInfo.TickDuration` | `field \| Int32 \| TickDuration` | `status.tickDuration` |
| `StatusEffectInfo.OriginEntityId` | `field \| String \| OriginEntityId` | `status.originEntityId` |
| `CharacterHelper.GetMaxHealth` | `method \| Int32 \| GetMaxHealth` | `combatant.maxHp` (computed, not stored) |
| `CharacterHelper.IsDead` | `method \| Boolean \| IsDead` | `combatant.alive`, negated |
| `CharacterHelper.GetBaseStats` | `method \| Dictionary\`1 \| GetBaseStats` | `combatant.stats{}` |
| `Env.GameRun` | `field \| GameRunData \| GameRun` | run block, fallback to `CombatState` |
| `Env.NetworkData` | `field \| NetworkData \| NetworkData` | network block (via existing `GameBridge`) |

**Two design notes that are not obvious from the code.**

*`stats{}` is base stats, deliberately.* `CharacterHelper.GetStat` has **8 overloads** and no grounded signature, so choosing one would be a guess. `GetBaseStats` is a single named entry point returning a dictionary, which the resolver can dispatch to by arity and assignability without knowing its signature. That gives real, string-keyed stat data now; effective/modified stats need `GetStat` grounded first and are open question #4. The schema doc says which one `stats{}` is, so no assertion can mistake one for the other.

*Absence versus failure is decided per member.* A missing `PlayerComponent` means "this is an enemy" — normal, no warning, so the sink is passed `null` for that lookup. A missing `CharacterComponent` on a combatant is odd but survivable — a `note:`. A `Statuses` field that will not read as a dictionary is a real failure — `StatusesAvailable` stays false and the field serializes `null`, which is visibly different from "no statuses".

- [ ] **Step 1: `CharacterHelperBridge`**

`FTK2.Crucible/src/Crucible.Plugin/CharacterHelperBridge.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Dispatch into <c>CharacterHelper</c>'s static helpers without knowing their signatures.
    ///
    /// The field map records these members by name and return type only, and <c>GetStat</c> alone
    /// has 8 overloads — so the overload is chosen at runtime by which argument the parameter type
    /// actually accepts (an <c>Entity</c> or a <c>CharacterComponent</c>), and the choice is cached
    /// per method name. That keeps the grounding requirement down to the member *name*, which is
    /// what the field map actually proves.
    /// </summary>
    internal static class CharacterHelperBridge
    {
        private static Type _type;
        private static bool _typeProbed;
        private static readonly Dictionary<string, MethodInfo> Resolved = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, bool> UsesCharacter = new Dictionary<string, bool>();

        internal static object Invoke(string methodName, object entity, object character, WarningSink warnings)
        {
            Type type = ResolveType(warnings);
            if (type == null) return null;

            MethodInfo method;
            bool useCharacter;

            if (!Resolved.TryGetValue(methodName, out method))
            {
                method = MemberResolver.FindUnaryStatic(type, methodName, entity, warnings);
                useCharacter = false;

                if (method == null && character != null)
                {
                    method = MemberResolver.FindUnaryStatic(type, methodName, character, warnings);
                    useCharacter = true;
                }

                // Cache only a decisive answer. If this combatant had no CharacterComponent then the
                // CharacterComponent overload was never actually tested, so leave the entry unresolved
                // and retry on the next combatant rather than poisoning the cache with a null.
                if (method != null || (entity != null && character != null))
                {
                    Resolved[methodName] = method;
                    UsesCharacter[methodName] = useCharacter;
                }
            }
            else
            {
                useCharacter = UsesCharacter[methodName];
            }

            if (method == null) return null;

            object arg = useCharacter ? character : entity;
            if (arg == null) return null;

            return MemberResolver.InvokeStatic(method, arg, "CharacterHelper." + methodName, warnings);
        }

        private static Type ResolveType(WarningSink warnings)
        {
            if (!_typeProbed)
            {
                _typeProbed = true;
                _type = AccessTools.TypeByName("CharacterHelper");
            }
            if (_type == null && warnings != null) warnings.TypeMissing("CharacterHelper");
            return _type;
        }
    }
}
```

- [ ] **Step 2: `CombatReader`**

`FTK2.Crucible/src/Crucible.Plugin/CombatReader.cs`:

```csharp
using System;
using System.Collections;
using System.Runtime.CompilerServices;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reflects the live fight into a <see cref="CombatView"/>. The only file in S1 that touches
    /// game objects for combat data; everything it produces is plain data that Core shapes.
    ///
    /// Error posture (SPEC §3): nothing here throws outward. A member that has been renamed by a
    /// game update yields a null plus a warning inside the snapshot, so the next assertion reports
    /// it instead of a log nobody reads.
    /// </summary>
    internal static class CombatReader
    {
        private static Type _combatPhaseType;
        private static bool _typeProbed;

        internal static CombatView Read(object gameRun, TurnTracker tracker, WarningSink warnings)
        {
            CombatView view = new CombatView();

            object combatPhase = FindCombatPhase(warnings);

            // Primary path: the live phase object holds the fight. Fallback: the run data root.
            object combatState = combatPhase == null
                ? null
                : MemberResolver.GetMember(combatPhase, "_combatState", warnings);
            if (combatState == null && gameRun != null)
                combatState = MemberResolver.GetMember(gameRun, "CombatState", warnings);

            IList entities = null;
            if (combatState != null)
            {
                view.Round = MemberResolver.AsInt(MemberResolver.GetMember(combatState, "TotalRounds", warnings));
                view.Wave = MemberResolver.AsInt(MemberResolver.GetMember(combatState, "WaveIndex", warnings));
                entities = MemberResolver.GetMember(combatState, "Entities", warnings) as IList;
            }

            bool active = entities != null && entities.Count > 0;
            view.Active = active;

            // Object identity keys the episode: the engine offers no grounded combat-start callback,
            // so a different CombatState instance is the signal that this is a different fight.
            tracker.OnObserved(active, combatState == null ? 0 : RuntimeHelpers.GetHashCode(combatState));
            ApplyTracker(view, tracker, warnings);

            // A direct read beats the hook's cached value when the live phase object is reachable —
            // the field map resolves the active combatant to CombatPhase._activeCharacterEntity.
            if (combatPhase != null)
            {
                object activeEntity = MemberResolver.GetMember(combatPhase, "_activeCharacterEntity", warnings);
                if (activeEntity == null)
                    activeEntity = MemberResolver.GetMember(combatPhase, "_lastEngagedEntity", null);
                if (activeEntity != null)
                    view.ActiveEntityId = MemberResolver.AsString(
                        MemberResolver.GetMember(activeEntity, "Guid", warnings));
            }

            if (combatState == null) return view;          // not in a fight: 'active' says so already

            if (entities == null)
            {
                warnings.Note("combat.combatants unavailable: CombatState.Entities did not read as a list");
                return view;                                // CombatantsAvailable stays false -> null
            }

            view.CombatantsAvailable = true;
            for (int i = 0; i < entities.Count; i++)
            {
                CombatantView combatant = ReadCombatant(entities[i], warnings);
                if (combatant != null) view.Combatants.Add(combatant);
            }
            return view;
        }

        private static void ApplyTracker(CombatView view, TurnTracker tracker, WarningSink warnings)
        {
            bool active; int turn; string phase; string entityId; string episode;
            tracker.Read(out active, out turn, out phase, out entityId, out episode);

            view.Episode = episode;
            view.ActiveEntityId = entityId;

            if (!tracker.HooksInstalled)
            {
                // Serving a permanently-zero counter as if it were a measurement is the failure this
                // whole plan exists to prevent. Say the value is unavailable instead.
                view.Turn = null;
                view.Phase = TurnTracker.PhaseUnknown;
                warnings.Note("combat.turn/phase unavailable: CombatPhase hooks are not installed");
                return;
            }

            view.Turn = turn < 0 ? (int?)null : turn;
            view.Phase = phase;
        }

        private static CombatantView ReadCombatant(object entity, WarningSink warnings)
        {
            if (entity == null) return null;

            CombatantView c = new CombatantView();
            c.Id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", warnings));

            object components = MemberResolver.GetMember(entity, "Components", warnings);

            // isPlayer has no boolean field anywhere (field map) — it is component presence. Absence
            // is the normal answer for an enemy, so no sink: that is information, not a miss.
            c.IsPlayer = MemberResolver.FindComponentByTypeName(components, "PlayerComponent", null) != null;

            object character = MemberResolver.FindComponentByTypeName(components, "CharacterComponent", null);
            if (character == null)
            {
                warnings.Note("combatant has no CharacterComponent: " + (c.Id == null ? "<no guid>" : c.Id));
            }
            else
            {
                c.Name = MemberResolver.AsString(MemberResolver.GetMember(character, "DisplayName", warnings));
                c.ClassId = MemberResolver.AsString(MemberResolver.GetMember(character, "ConfigName", warnings));
                c.Hp = MemberResolver.AsInt(MemberResolver.GetMember(character, "CurrentHealth", warnings));
            }

            // maxHp is computed, not stored (field map: no MaxHealth field on CharacterComponent).
            c.MaxHp = MemberResolver.AsInt(
                CharacterHelperBridge.Invoke("GetMaxHealth", entity, character, warnings));
            c.Alive = Negate(MemberResolver.AsBool(
                CharacterHelperBridge.Invoke("IsDead", entity, character, warnings)));

            ReadStatuses(c, components, warnings);
            ReadStats(c, entity, character, warnings);
            return c;
        }

        private static void ReadStatuses(CombatantView c, object components, WarningSink warnings)
        {
            object statusComponent =
                MemberResolver.FindComponentByTypeName(components, "StatusEffectComponent", null);
            if (statusComponent == null)
            {
                // No component means no statuses. That is a real answer, so the array is empty —
                // NOT null, which is reserved for "could not read".
                c.StatusesAvailable = true;
                return;
            }

            IDictionary map = MemberResolver.GetMember(statusComponent, "Statuses", warnings) as IDictionary;
            if (map == null)
            {
                warnings.Note("StatusEffectComponent.Statuses did not read as a dictionary");
                return;   // StatusesAvailable stays false -> serializes null
            }

            c.StatusesAvailable = true;
            foreach (DictionaryEntry entry in map)
            {
                StatusView s = new StatusView();
                // The dictionary key IS the status id (e.g. STATUS_ATTACKUP_00). No stacks field
                // exists on StatusEffectInfo and none is invented here — see the plan's stacks decision.
                s.Id = entry.Key == null ? null : entry.Key.ToString();
                object info = entry.Value;
                s.Duration = MemberResolver.AsInt(MemberResolver.GetMember(info, "Duration", warnings));
                s.InitialDuration = MemberResolver.AsInt(MemberResolver.GetMember(info, "InitialDuration", warnings));
                s.TickDuration = MemberResolver.AsInt(MemberResolver.GetMember(info, "TickDuration", warnings));
                s.OriginEntityId = MemberResolver.AsString(MemberResolver.GetMember(info, "OriginEntityId", warnings));
                c.Statuses.Add(s);
            }
        }

        /// <summary>
        /// Base stats, string-keyed: <c>eStats</c> does not exist (field map). <c>GetStat</c>'s 8
        /// overloads have no grounded signature, so effective/modified stats are out of S1's scope.
        /// </summary>
        private static void ReadStats(CombatantView c, object entity, object character, WarningSink warnings)
        {
            object raw = CharacterHelperBridge.Invoke("GetBaseStats", entity, character, warnings);
            IDictionary map = raw as IDictionary;
            if (map == null) return;   // StatsAvailable stays false -> serializes null

            c.StatsAvailable = true;
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key == null) continue;
                int? value = MemberResolver.AsInt(entry.Value);
                if (!value.HasValue) continue;
                c.Stats[entry.Key.ToString()] = value.Value;
            }
        }

        private static bool? Negate(bool? value)
        {
            return value.HasValue ? (bool?)(!value.Value) : null;
        }

        private static object FindCombatPhase(WarningSink warnings)
        {
            if (!_typeProbed)
            {
                _typeProbed = true;
                _combatPhaseType = AccessTools.TypeByName("CombatPhase");
            }
            if (_combatPhaseType == null)
            {
                warnings.TypeMissing("CombatPhase");
                return null;
            }

            try
            {
                // CombatPhase carries UIDocument/GameObject/camera members (field map), i.e. it is a
                // Unity component, so the live instance is found through the scene. FindObjectOfType
                // returns only enabled objects — a disabled CombatPhase would read as "no combat",
                // which Task 9 Step 3 checks against a real fight rather than assuming.
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(_combatPhaseType);
                return found == null ? null : (object)found;
            }
            catch (Exception ex)
            {
                warnings.Note("CombatPhase instance lookup failed: " + ex.Message);
                return null;
            }
        }
    }
}
```

- [ ] **Step 3: `StateReaderV2`**

`FTK2.Crucible/src/Crucible.Plugin/StateReaderV2.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The <c>crucible.state.v2</c> reader. Additive: <see cref="StateReader"/> v1 is untouched and
    /// still served, so nothing in flight breaks and no existing digest moves.
    ///
    /// Difference that matters: every reflective read here carries a <see cref="WarningSink"/>.
    /// v1 passes <c>null</c> for the run block (<c>GetMember(gameRun, "Seed", null)</c>), which is
    /// why a wrong name there would be silent — the same class of bug as
    /// <c>NetworkData.PlayerCount</c>.
    /// </summary>
    internal static class StateReaderV2
    {
        internal static Dictionary<string, object> Snapshot()
        {
            WarningSink warnings = new WarningSink();
            RunView run = new RunView();
            NetworkView network = new NetworkView();
            CombatView combat = null;
            string route = null;
            bool console = false;
            string instance = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.InstanceName : null;

            try
            {
                // Drain any transitions the hooks queued since the last read, so the trace carries
                // them even in a session where nothing else called into the hooks.
                TurnHooks.FlushEvents();

                object env = GameBridge.GetEnv();
                if (env == null)
                {
                    warnings.Note("RouterHelper.Env unavailable (game may still be loading)");
                    return SnapshotShape.BuildV2(instance, null, run, network, null, false, warnings);
                }

                route = MemberResolver.AsString(ReadRoute(warnings));
                console = GameBridge.IsConsoleShowing();

                object gameRun = MemberResolver.GetMember(env, "GameRun", warnings);
                run.Present = gameRun != null;
                if (gameRun != null)
                {
                    // Names confirmed by the Task 1 addendum. Unlike v1, a wrong one is LOUD.
                    run.Seed = MemberResolver.GetMember(gameRun, "Seed", warnings);
                    run.Day = MemberResolver.GetMember(gameRun, "Day", warnings);
                    run.Gold = MemberResolver.GetMember(gameRun, "Gold", warnings);
                    run.Chapter = MemberResolver.GetMember(gameRun, "Chapter", warnings);
                }

                network.Online = GameBridge.IsOnlineSession();
                object networkData = GameBridge.GetNetworkData();
                if (networkData == null)
                {
                    warnings.Note("NetworkData unavailable");
                }
                else
                {
                    network.IsHost = MemberResolver.AsBool(
                        MemberResolver.GetMember(networkData, "IsHost", warnings));
                    ICollection players =
                        MemberResolver.GetMember(networkData, "PlayerList", warnings) as ICollection;
                    network.PlayerCount = players == null ? (int?)null : players.Count;
                }

                combat = CombatReader.Read(gameRun, TurnHooks.Tracker, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("snapshot_failed: " + ex.Message);
            }

            return SnapshotShape.BuildV2(instance, route, run, network, combat, console, warnings);
        }

        /// <summary>
        /// The same call v1 makes, with the warning sink v1 does not pass. Parameterless and static;
        /// v1's live output proves it returns real route values (SPEC §0).
        /// </summary>
        private static object ReadRoute(WarningSink warnings)
        {
            try
            {
                Type type = AccessTools.TypeByName("RouterHelper");
                if (type == null) { warnings.TypeMissing("RouterHelper"); return null; }

                MethodInfo method = AccessTools.Method(type, "GetCurrentRoute");
                if (method == null || method.GetParameters().Length != 0)
                {
                    warnings.MemberMissing("RouterHelper", "GetCurrentRoute()");
                    return null;
                }
                return method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                warnings.Add("invoke_failed: RouterHelper.GetCurrentRoute: " + ex.Message);
                return null;
            }
        }
    }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build FTK2.Crucible/src/Crucible.Plugin -c Release
```

Expected: build succeeds. This is the offline ceiling for Task 6 — the compiler proves the code is well-formed and that `UnityEngine.Object.FindObjectOfType(Type)` exists in this Unity version; it proves nothing about whether the members resolve at runtime. That is Task 9.

- [ ] **Step 5: Commit** (after Task 7, which supplies `TurnHooks` — the two tasks compile together)

---

### Task 7: Plugin — `TurnHooks`, the two Harmony patches

**Files:**
- Create: `FTK2.Crucible/src/Crucible.Plugin/TurnHooks.cs`
- Modify: `FTK2.Crucible/src/Crucible.Plugin/CruciblePlugin.cs` (one added line)

**Interfaces:**
- Consumes: `TurnTracker`, `MemberResolver`, `CruciblePlugin.Trace`, `DevKitBridge.ReportTarget`.
- Produces:
  - `internal static readonly TurnTracker TurnHooks.Tracker`
  - `internal static void TurnHooks.Initialize(Harmony harmony, ManualLogSource log)`
  - `internal static void TurnHooks.FlushEvents()`
  - postfixes `NextTurnPostfix()` and `EngagePostfix(object __instance)`

**What this task buys beyond `turn` and `phase`.** The trace today records only `exec` (spec §4/§9). A trace showing a command going in and nothing about what the fight did in response is not a trace anyone can debug from. These hooks are the natural place to fix that: every `combat_start`, `turn`, `phase`, and `combat_end` transition is written as its own JSONL entry, correlated by episode, so `ftk2_read_trace` shows the fight, not just the commands aimed at it.

**Why the postfixes pass a null warning sink.** A hook has no snapshot to report into and no sink of its own to own. A miss inside `EngagePostfix` surfaces two ways regardless: `phase` stays `NONE`, and `CombatReader` reads the same members on the next snapshot *with* a sink. Allocating a sink here that nobody drains would be a warning that goes nowhere — the exact failure mode this plan is built to avoid.

- [ ] **Step 1: Write `TurnHooks`**

`FTK2.Crucible/src/Crucible.Plugin/TurnHooks.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Synthesizes <c>combat.turn</c> and <c>combat.phase</c>, which the engine holds as control
    /// flow rather than data (field map: no turn counter and no intra-combat phase enum exist
    /// anywhere in the assembly — combat runs procedurally through
    /// <c>CombatPhase._engageActiveEntity</c> / <c>_nextTurn</c>).
    ///
    /// Also the source of the trace's combat transitions. Before this, the trace only ever recorded
    /// <c>exec</c>: what an agent asked for, never what the fight did.
    ///
    /// Degrades loudly: if either target cannot be patched, <c>HooksInstalled</c> stays false and
    /// the snapshot reports <c>turn: null</c> / <c>phase: "UNKNOWN"</c> plus a warning, rather than
    /// serving a counter stuck at zero as if it were a measurement.
    /// </summary>
    internal static class TurnHooks
    {
        internal static readonly TurnTracker Tracker = new TurnTracker(256);
        private static ManualLogSource _log;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            Type combatPhase = AccessTools.TypeByName("CombatPhase");
            if (combatPhase == null)
            {
                log.LogWarning("Target NOT found: CombatPhase — combat.turn/phase disabled.");
                DevKitBridge.ReportTarget("CombatPhase", null);
                return;
            }

            MethodInfo nextTurn = AccessTools.Method(combatPhase, "_nextTurn");
            MethodInfo engage = AccessTools.Method(combatPhase, "_engageActiveEntity");
            Report("CombatPhase._nextTurn", nextTurn);
            Report("CombatPhase._engageActiveEntity", engage);

            try
            {
                if (nextTurn != null)
                {
                    harmony.Patch(nextTurn, null,
                        new HarmonyMethod(AccessTools.Method(typeof(TurnHooks), "NextTurnPostfix")));
                }
                if (engage != null)
                {
                    harmony.Patch(engage, null,
                        new HarmonyMethod(AccessTools.Method(typeof(TurnHooks), "EngagePostfix")));
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("TurnHooks patching failed: " + ex.Message + " — combat.turn/phase disabled.");
                return;
            }

            Tracker.HooksInstalled = nextTurn != null && engage != null;
            log.LogInfo("TurnHooks installed: " + (Tracker.HooksInstalled ? "yes" : "partial — turn/phase disabled"));
        }

        private static void Report(string description, MethodBase resolved)
        {
            if (resolved != null) _log.LogInfo("Target found: " + description);
            else _log.LogWarning("Target NOT found: " + description + " (feature disabled)");
            DevKitBridge.ReportTarget(description, resolved);
        }

        /// <summary>
        /// One turn advance. If <c>_nextTurn</c> is async this fires when the state machine is
        /// created rather than when the turn completes — still exactly once per advance, in call
        /// order, which is all an ordinal needs. Task 9 Step 4 measures that rather than assuming it.
        /// </summary>
        internal static void NextTurnPostfix()
        {
            try
            {
                Tracker.OnTurnAdvanced();
                FlushEvents();
            }
            catch (Exception ex)
            {
                Warn("NextTurnPostfix", ex);
            }
        }

        /// <summary>
        /// A combatant takes the floor. <c>object __instance</c> rather than the real type: this
        /// plugin holds no compile-time game types (SPEC §3), so the CombatPhase instance arrives
        /// boxed and its members are read reflectively.
        /// </summary>
        internal static void EngagePostfix(object __instance)
        {
            try
            {
                object entity = MemberResolver.GetMember(__instance, "_activeCharacterEntity", null);
                if (entity == null) entity = MemberResolver.GetMember(__instance, "_lastEngagedEntity", null);

                string id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", null));
                object components = MemberResolver.GetMember(entity, "Components", null);
                bool isPlayer = MemberResolver.FindComponentByTypeName(components, "PlayerComponent", null) != null;

                Tracker.OnEntityEngaged(id, isPlayer);
                FlushEvents();
            }
            catch (Exception ex)
            {
                Warn("EngagePostfix", ex);
            }
        }

        /// <summary>
        /// Writes queued transitions to the JSONL trace. Drains unconditionally — if tracing is off
        /// the events are discarded here rather than accumulating in a queue nobody reads.
        /// </summary>
        internal static void FlushEvents()
        {
            TurnEvent[] events = Tracker.DrainEvents();
            if (events.Length == 0) return;
            if (CruciblePlugin.Trace == null) return;

            string instance = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.InstanceName : null;
            int dropped = Tracker.DroppedEvents;

            for (int i = 0; i < events.Length; i++)
            {
                TurnEvent e = events[i];
                Dictionary<string, object> fields = new Dictionary<string, object>();
                fields["turn"] = e.Turn;
                fields["phase"] = e.Phase;
                fields["entityId"] = e.EntityId;
                fields["episode"] = e.Episode;
                fields["instance"] = instance;
                // Reported, never swallowed: silent truncation is forbidden (SPEC §9).
                if (dropped > 0) fields["droppedEvents"] = dropped;
                CruciblePlugin.Trace.Write(e.Kind, "combat-" + e.Episode, fields);
            }
        }

        private static void Warn(string where, Exception ex)
        {
            if (_log != null) _log.LogWarning("TurnHooks." + where + " threw: " + ex.Message);
        }
    }
}
```

- [ ] **Step 2: Install the hooks at startup**

In `FTK2.Crucible/src/Crucible.Plugin/CruciblePlugin.cs`, in `Awake()`, add one line immediately after the existing `MainThreadPump.OnTick = PollHotkeys;`:

```csharp
            _harmony = new Harmony(PluginGuid);
            GameBridge.Initialize(_log);
            MainThreadPump.Initialize(_harmony, _log);
            MainThreadPump.OnTick = PollHotkeys;
            TurnHooks.Initialize(_harmony, _log);
```

That is the only edit to an existing plugin file in Tasks 6–7.

- [ ] **Step 3: Build**

```bash
dotnet build FTK2.Crucible/src/Crucible.Plugin -c Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add FTK2.Crucible/src/Crucible.Plugin
git commit -m "Crucible.Plugin: combat reflection reader, CharacterHelper dispatch, and turn/phase hooks"
```

---

### Task 8: Serve v2 over RPC and MCP, and write the schema contract

**Files:**
- Modify: `FTK2.Crucible/src/Crucible.Plugin/RpcServer.cs` (`HandleState` only)
- Modify: `FTK2.Crucible/mcp/server.js` (`ftk2_state` tool definition and dispatch)
- Create: `docs/research/crucible-state-v2-schema.md`

**Interfaces:**
- Consumes: `StateReaderV2.Snapshot()`, `Redactions.V2`, `StateDigest.Compute`.
- Produces: `GET /state?schema=v2` → `{ ok, instance, snapshot, digest }`; MCP `ftk2_state({ schema: "v2" })`.

**Why a query parameter and not a new endpoint.** `Handle` routes on `AbsolutePath`, which excludes the query string, so `/state` keeps matching and the v1 path is byte-for-byte what it was. One endpoint, two schemas, one digest call site. `ftk2_compare_state` stays on v1 deliberately: it is the live desync oracle, and changing what it hashes is a separate decision with its own risk, not a side effect of adding a reader. Open question #5.

- [ ] **Step 1: Branch `HandleState` on the schema**

Replace the body of `HandleState` in `FTK2.Crucible/src/Crucible.Plugin/RpcServer.cs` with:

```csharp
        private void HandleState(HttpListenerContext ctx)
        {
            // Routing happens on AbsolutePath, which excludes the query string, so v1 keeps its
            // exact behaviour and v2 is purely additive.
            string schema = ctx.Request.QueryString["schema"];
            bool v2 = string.Equals(schema, "v2", StringComparison.OrdinalIgnoreCase);

            object result;
            string error;
            bool pumped;
            if (v2)
            {
                pumped = MainThreadPump.Run(delegate { return StateReaderV2.Snapshot(); }, 5000, out result, out error);
            }
            else
            {
                pumped = MainThreadPump.Run(delegate { return StateReader.Snapshot(); }, 5000, out result, out error);
            }

            if (!pumped)
            {
                Respond(ctx, 503, Error(error));
                return;
            }

            Dictionary<string, object> snapshot = result as Dictionary<string, object>;

            // The instance name is part of the snapshot for readability, but it MUST NOT feed the
            // digest — otherwise two healthy peers would never agree and every comparison would
            // report a desync. v2 redacts more: entity guids and the episode key are per-process
            // runtime values, and one peer noticing a reflection miss is not a desync.
            string[] redactions = v2 ? Redactions.CopyV2() : new string[] { "instance" };

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            d["instance"] = CruciblePlugin.Instance.InstanceName;
            d["snapshot"] = snapshot;
            d["digest"] = StateDigest.Compute(snapshot, redactions);
            Respond(ctx, 200, d);
        }
```

`RpcServer.cs` already has `using FTK2Mods.Crucible;` and `using System;`, so no new usings are needed.

- [ ] **Step 2: Expose the schema through MCP**

In `FTK2.Crucible/mcp/server.js`, replace the `ftk2_state` tool definition:

```js
  { name: 'ftk2_state',
    description: 'Read a snapshot of the current run state plus a deterministic digest. ' +
      'schema:"v2" adds the combat observation surface: combatants with hp/maxHp/alive/statuses/stats, ' +
      'plus synthesized turn and phase.',
    inputSchema: { type: 'object', properties: {
      schema: { type: 'string', enum: ['v1', 'v2'], description: 'Snapshot schema (default v1)' },
      ...INSTANCE_ARG } } },
```

and its dispatch line:

```js
    case 'ftk2_state':
      return textResult(await rpc(inst, 'GET', '/state' + (args.schema === 'v2' ? '?schema=v2' : '')));
```

Nothing else in the MCP server changes — it stays a dumb pipe, which is what keeps it from drifting from the game (spec §3).

```bash
node --check FTK2.Crucible/mcp/server.js
```

Expected: no output, exit 0.

- [ ] **Step 3: Write the schema contract**

`docs/research/crucible-state-v2-schema.md`:

````markdown
# `crucible.state.v2` — the combat observation surface

Served by `GET /state?schema=v2` (MCP: `ftk2_state({ schema: "v2" })`). `crucible.state.v1` is still
served unchanged at `GET /state`.

Every member name below was read out of the retail assembly and is recorded in
`docs/research/crucible-combat-field-map.md`. Two fields are **not** read from the game at all — see
"Synthesized fields".

## Shape

```jsonc
{
  "schema": "crucible.state.v2",
  "instance": "p1",
  "route": "COMBAT",
  "run":     { "present": true, "seed": 12345, "day": 3, "gold": 120, "chapter": "<runtime>" },
  "network": { "online": false, "isHost": true, "playerCount": 1 },
  "combat": {
    "active": true,
    "round": 2,
    "wave": 0,
    "turn": 5,
    "phase": "PLAYER",
    "activeId": "<entity guid>",
    "episode": "1a2b3c4d",
    "synthesized": ["turn", "phase"],
    "combatants": [
      { "id": "<entity guid>", "name": "<runtime>", "classId": "CF_EOR_BARD", "isPlayer": true,
        "hp": 34, "maxHp": 40, "alive": true,
        "statuses": [ { "id": "STATUS_ATTACKUP_00", "duration": 2, "initialDuration": 2,
                        "tickDuration": 0, "originEntityId": "<entity guid>" } ],
        "stats": { "STR": 4 } }
    ]
  },
  "console": false,
  "warnings": []
}
```

## Where each field comes from

| Field | Source |
|---|---|
| `combat.active` | `CombatState.Entities` is non-empty |
| `combat.round` | `CombatState.TotalRounds` |
| `combat.wave` | `CombatState.WaveIndex` |
| `combat.activeId` | `CombatPhase._activeCharacterEntity` → `Entity.Guid` (falls back to `_lastEngagedEntity`) |
| `combatants[]` | `CombatState.Entities`, in list order |
| `.id` | `Entity.Guid` |
| `.name` | `CharacterComponent.DisplayName` |
| `.classId` | `CharacterComponent.ConfigName` |
| `.isPlayer` | presence of a `PlayerComponent` in `Entity.Components` — **derived**, there is no boolean field |
| `.hp` | `CharacterComponent.CurrentHealth` |
| `.maxHp` | `CharacterHelper.GetMaxHealth(...)` — **computed**, not stored |
| `.alive` | `CharacterHelper.IsDead(...)`, negated |
| `.statuses[].id` | key of `StatusEffectComponent.Statuses` |
| `.statuses[].duration` / `.initialDuration` / `.tickDuration` / `.originEntityId` | `StatusEffectInfo` fields of the same name |
| `.stats{}` | `CharacterHelper.GetBaseStats(...)`, string-keyed |

## Caveats an assertion must respect

**`round` is NOT monotonic.** `CombatState.TotalRounds` initialises to `-1` and is reset to `-1`
again **per wave**. A multi-wave fight rewinds it. Never assert `round` increases; assert on `turn`,
which is monotonic within an episode, and use `wave` to segment.

**`stats{}` is BASE stats.** It comes from `CharacterHelper.GetBaseStats`, not from the 8-overload
`GetStat`, whose signature has never been grounded. Do not assert that a buff moved a value here.

**There is no `stacks` field.** `StatusEffectInfo` has no stack count. Stacking appears to be
encoded as distinct suffixed ids (`STATUS_ATTACKUP_00`, `_01`), which is unverified, so nothing is
derived from it. Assert on ids; count matching ids yourself if you need a tier count.

**`null` and `[]` mean different things.** `"statuses": []` is "this combatant has no statuses".
`"statuses": null` is "Crucible could not read them" — always accompanied by an entry in `warnings`.
The same holds for `combatants` and `stats`. An assertion that treats null as empty will pass while
the oracle is blind.

**Read `warnings` on every failure.** Every reflective read reports there. A member renamed by a
game update shows up as `member_missing: <Type>.<Member>` in the very next snapshot.

## Synthesized fields

`turn` and `phase` do not exist as readable data anywhere in the assembly — combat runs
procedurally through `CombatPhase._engageActiveEntity` / `_nextTurn`. Crucible synthesizes both from
Harmony postfixes on those two methods, and says so in `combat.synthesized`.

- `turn` — an ordinal of observed advances within one combat episode, 0-based. `null` outside
  combat, and `null` when the hooks failed to install.
- `phase` — `PLAYER` / `ENEMY` from whether the engaged entity has a `PlayerComponent`; `NONE`
  between episodes; `UNKNOWN` when the hooks failed to install.
- `episode` — an object-identity key for the current `CombatState`. Local to a process; redacted
  from the digest. Use it to tell "turn 3 of this fight" from "turn 3 of the last one".

The same hooks emit `combat_start` / `turn` / `phase` / `combat_end` entries into the JSONL trace,
correlated as `combat-<episode>`, so `ftk2_read_trace` shows what the fight did and not only what
was asked of it.

## Digest

`GET /state?schema=v2` returns a digest computed with `Redactions.V2`: `instance`, `warnings`,
`console`, `network.isHost`, `combat.episode`, `combat.activeId`, `combat.combatants.id`, and
`combat.combatants.statuses.originEntityId` are excluded. Everything else — hp, statuses, stats,
turn, phase, round, wave — is in the hash. `ftk2_compare_state` still compares v1 digests.
````

- [ ] **Step 4: Full offline verification**

```bash
dotnet run   --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
dotnet build --project FTK2.Crucible/src/Crucible.Plugin     -c Release
node --check FTK2.Crucible/mcp/server.js
```

Expected: 86 tests passing (exit 0), a clean plugin build, and a clean JS parse.

- [ ] **Step 5: Commit**

```bash
git add FTK2.Crucible/src/Crucible.Plugin/RpcServer.cs FTK2.Crucible/mcp/server.js \
        docs/research/crucible-state-v2-schema.md
git commit -m "Serve crucible.state.v2 over RPC and MCP, and document the schema contract"
```

---

### Task 9: Live verification — **PARKED**

> **This is the boundary.** Everything above is executable now and ends with a green suite and a
> clean build. Nothing below can run without a game being driven through a real fight, and the
> operator is away. Do not mark S1 complete on the strength of Tasks 1–8: mark it
> **offline-complete, live verification parked**, and run this task when a driven game is available.

**Files:** none. This task produces evidence, not code — except where it finds a bug, which goes
back through Tasks 6–7.

**Setup.** Deploy the built plugin, enable `[General] Enabled` and `[Rpc] Enabled`, start the game
with Steam running, and reach a combat encounter.

- [ ] **Step 1: v1 has not regressed**

```bash
curl -s http://127.0.0.1:8787/state | head -c 400
```

Expected: `"schema":"crucible.state.v1"`, the same fields as before, and a digest. If the v1 digest
changed for identical game state, stop — v2 was supposed to be additive and something touched v1.

- [ ] **Step 2: v2 answers outside combat**

At `MAIN_MENU`:

```bash
curl -s "http://127.0.0.1:8787/state?schema=v2"
```

Expected: `schema` is `crucible.state.v2`; `combat.active` is `false`; `combat.combatants` is
`null`; `combat.turn` is `null`; `combat.phase` is `NONE` or `UNKNOWN`. **Read `warnings`.** Any
`member_missing:` line naming a `GameRunData` member means Task 1's addendum recorded a wrong name
— fix the name, not the warning.

- [ ] **Step 3: v2 answers inside combat**

In a fight:

```bash
curl -s "http://127.0.0.1:8787/state?schema=v2"
```

Expected, and each is a separate pass/fail to record:

| Check | Expected |
|---|---|
| `combat.active` | `true` |
| `combat.combatants` | a non-empty array, one entry per visible combatant |
| `isPlayer` | true for exactly the party members |
| `hp` / `maxHp` | match the health bars on screen |
| `alive` | true for everyone standing |
| `classId` | the authored ids, e.g. `CF_EOR_BARD` |
| `stats` | a non-empty string-keyed object (not `null`) |
| `statuses` | `[]` for a clean unit, not `null` |
| `warnings` | **empty** |

A non-empty `warnings` array here is the whole point of the mechanism working — read it and fix the
named member. A `null` where an array was expected means a read failed; the warning says which.

- [ ] **Step 4: the synthesized turn actually advances**

```bash
curl -s "http://127.0.0.1:8787/state?schema=v2" | grep -o '"turn":[0-9-]*'
curl -s -X POST http://127.0.0.1:8787/exec -H 'Content-Type: application/json' \
     -d '{"command":"EndPhase","args":[]}'
curl -s "http://127.0.0.1:8787/state?schema=v2" | grep -o '"turn":[0-9-]*'
```

Expected: `turn` increases by exactly one per advance, and `phase` tracks whose turn it is. This is
the step that empirically settles the async-postfix question from Task 1 Step 3: if `turn` jumps by
two, or increments before the turn visibly changes, the postfix is firing at state-machine creation
in a way the ordinal cannot absorb — record the observed behaviour and revisit the hook choice.

- [ ] **Step 5: the multi-wave case**

Fight an encounter with more than one wave. Record, for each wave:

- does `round` reset to `-1`? (the field map says it will)
- does `wave` increment?
- does `turn` keep increasing, or reset?

Then update the "Caveats" section of `docs/research/crucible-state-v2-schema.md` with what actually
happened. This is the one behaviour in the whole schema that the offline tests can only assert about
the *tracker*, never about the game.

- [ ] **Step 6: statuses are observable — the S1 acceptance test**

Apply a status (any ability that inflicts one), then snapshot.

Expected: the affected combatant's `statuses` array gains an entry whose `id` is the status id and
whose `duration` counts down on subsequent turns. **This is the keystone claim of S1**: a scenario
can now prove an ability did something. If this step fails, S1 has not delivered its purpose no
matter how green the offline suite is.

Also confirm the stacking question while a stacking status is applied twice: do two ids appear
(`_00` and `_01`), or does one entry change? Record the answer in the schema doc and close open
question #3.

- [ ] **Step 7: the trace shows the fight**

```bash
curl -s "http://127.0.0.1:8787/trace?n=50"
```

Expected: entries of kind `combat_start`, `phase`, `turn`, and `combat_end` interleaved with `exec`,
all sharing a `combat-<episode>` correlation id. Before this, the trace only ever recorded `exec`.

- [ ] **Step 8: record the results**

Append a "Live verification" section to `docs/research/crucible-state-v2-schema.md` with the date,
the game build, and a pass/fail line per check above — including the `warnings` array verbatim if it
was not empty. Per spec §10, a component that has not been verified live is not done; this section
is what makes the claim checkable by someone who was not there.

---

## Negative controls

Spec §10: every check has one. A lint that cannot fail reports green forever.

| Check family | Positive check | Negative control |
|---|---|---|
| Warning dedupe | 12 identical misses collapse to 1 | 6 distinct messages all survive |
| Member lookup | a missing member warns | an existing member emits **no** warning |
| Overload selection | most-derived overload wins | two unrelated interface overloads are refused with `member_ambiguous` |
| Overload arity | unary overload found | a two-parameter overload is **not** treated as unary |
| Component lookup | `PlayerComponent` matches, and matches via a base type | `PlayerComponentExtra` does **not** match `PlayerComponent` |
| Turn reset | a new episode key resets the count to 0 | the **same** episode key does not reset it |
| Event dropping | an overfull queue drops the oldest and counts it | under capacity, `DroppedEvents` is 0 |
| Phase events | a changed entity emits a phase event | re-engaging the same entity emits **no** duplicate |
| Schema identity | schema is `crucible.state.v2` | schema is **not** `crucible.state.v1` |
| Null vs empty | no statuses serializes `[]` | unreadable statuses serialize `null`, not `[]` |
| Digest redaction | entity ids do not move the digest; warnings do not move the digest | an hp change **does** move it |
| Digest sorting | status order does not move the digest | a different status **set** does move it |
| Live: v2 additive | v2 returns combatants | v1's digest is **unchanged** for identical state (Task 9 Step 1) |

---

## Open questions

1. **Where does the party live outside combat?** `run.partyClassIds` (spec §4) has no grounded
   source. S3's `set_party` spike needs the same root; whoever grounds it should add
   `partyClassIds` to v2 at that point.
2. **When do the `rng` counters land?** S5's `RngWatch` owns `gameplayDraws`/`visualDraws`. v2
   reserves the key and emits nothing until then; the spec §6 scenario's
   `expect rng.visualDraws unchanged` cannot run before S5.
3. **Is stacking really suffixed ids?** Task 9 Step 6 answers it empirically; S6's LiveDataHarness
   walks `Configs.StatusEffects` and can confirm it offline. Only then is a derived `stacks` field
   even a candidate.
4. **What is `CharacterHelper.GetStat`'s signature?** 8 overloads, none grounded. Until one is,
   `stats{}` is base stats. If S4 needs effective stats to assert a buff, this becomes blocking.
5. **Should `ftk2_compare_state` move to v2?** v2 digests far more state, which makes the desync
   oracle sharper — and also makes it sensitive to fields that may legitimately differ between
   peers in ways nobody has measured. Decide with two machines (spec §7 mechanism 4), not before.
6. **Does `FindObjectOfType` see a disabled `CombatPhase`?** It does not, by Unity's contract. If a
   real fight ever reads as `active: false` with a live `CombatState`, the fallback through
   `GameRunData.CombatState` is already in place — but the direct `_activeCharacterEntity` read
   would be lost, and `activeId` would fall back to the hook's cached value. Task 9 Step 3 catches it.

---

## Self-Review

**Spec coverage.** §4 v2 schema → Tasks 5–8, with seven documented deviations where the field map
contradicts the spec sample. §2 grounding rule → Task 1 (nothing is written against an ungrounded
name) and its corollary → `WarningSink` threaded through every v2 read (Tasks 2, 6). §3 architecture
→ all logic in `Crucible.Core`, reflection confined to `Crucible.Plugin`, MCP unchanged as a dumb
pipe. §3 error posture → every reflective path degrades to null + warning; `CombatReader` and both
postfixes catch outward. §10 testing standard → 50 new offline tests with 13 negative controls, plus
Task 9's live evidence requirement.

**Deliberately out of scope.** S3 verbs, S4 scenario runner, S5 RNG counters, S6 LiveDataHarness,
S8 overnight runner. Also out of scope: fixing v1's warning suppression (it would move v1 digests),
and wiring `data/Redactions.json` at runtime (pre-existing debt, separate failure modes).

**Placeholder scan.** No `TBD`, no `TODO`, no "implement later", no "add error handling". Every code
step is complete and runnable. The one templated section is Task 1's field-map addendum, whose
`<verbatim table>` slots are *outputs of a command in the same step*, not decisions deferred.

**Type consistency.** `WarningSink` is constructed in Tasks 2, 3, 5, 6 and consumed with the same
six methods everywhere. `MemberResolver.GetMember(object, string, WarningSink)` /
`FindUnaryStatic(Type, string, object, WarningSink)` / `InvokeStatic(MethodInfo, object, string,
WarningSink)` / `FindComponentByTypeName(object, string, WarningSink)` have identical signatures at
every call site in Tasks 3, 6, 7. `TurnTracker.Read(out bool, out int, out string, out string, out
string)` matches its four test accessors and `CombatReader.ApplyTracker`. `SnapshotShape.BuildV2`'s
seven parameters are the same in Task 5's tests and Task 6's `StateReaderV2`. `Redactions.CopyV2()`
in `RpcServer` and `Redactions.V2` in the tests are the same array. `TurnHooks.Tracker` is the
`TurnTracker` that `StateReaderV2` passes to `CombatReader.Read`.

**Test-count arithmetic.** 36 existing → 40 (Task 2, +4) → 56 (Task 3, +16) → 69 (Task 4, +13) →
86 (Task 5, +17).

**Known risks.**
1. *`_nextTurn` may be async.* A postfix then fires at state-machine creation. The ordinal survives
   that (one fire per advance, in order), but Task 1 Step 3 records the return type and Task 9
   Step 4 measures the behaviour rather than trusting the argument.
2. *Episode keys are identity hashes.* A fight that reuses one `CombatState` across waves produces
   one episode. That matches the stated contract (`turn` monotonic within an encounter), and Task 9
   Step 5 records what actually happens.
3. *`GetBaseStats` may not be unary.* Then `stats` serializes `null` with a `member_missing` warning
   naming it — a loud, correct degrade, and open question #4 becomes blocking for S4 rather than
   silently wrong for everyone.
