# LiveDataHarness

Runs every mod's host-agnostic `.Core` logic against the **real game's `Configs`**, loaded
out-of-process by reflection — no Unity, no BepInEx, no running game, no Harmony. It
`Assembly.LoadFrom`s the install's `Managed/FTK2.dll`, invokes `ConfigsHelper.LoadConfigs(basePath)`,
and validates every authored class, trait, item, skill recipe, blessing, status and encounter
modifier in this repo against the live data. Roughly three seconds, one command.

There is **no compile-time reference** to `FTK2.dll`, `UnityEngine*.dll` or `BepInEx.dll`, and no
NuGet package, so the project builds on a machine with no game installed and degrades to a clear
skip at runtime.

## Why it exists

It catches, offline and in seconds, the failure class that repeatedly ships **invisible** content in
this repo — content that loads without an error and simply does nothing:

- a `CharacterConfig` id the game does not define — the spawned entity is alive, targetable, and
  rendered as nothing or a generic humanoid;
- a status id that is not a real `Configs.StatusEffects` **key** — the effect silently does nothing.
  `"CURSE"` is the canonical example: it is an `eStatusEffectTypes` member, not a key;
- an id that collides with live content and is **refused at merge** (`CF_LIVE_ID_COLLISION`), so the
  file someone edits is never actually loaded;
- a class, trait or item with no localization key — it renders as its raw id.

Each of those is an **Error** here, named with its pack, key and id.

## The one command

```powershell
pwsh -File tools/run-harness.ps1
pwsh -File tools/run-harness.ps1 -GameDir "D:\Steam\steamapps\common\For The King II"
```

The wrapper builds, runs the harness twice in **separate processes**, and requires the two JSON
reports to be byte-identical — the same cross-process posture as `tools/run-determinism.ps1`, because
a result stable within one process can still differ between two peers, and that is precisely the
multiplayer parity failure worth catching.

Direct invocation, without the determinism pass:

```bash
dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release -- --json tools/out/harness-report.json
```

Arguments: `--game-dir <path>` (default: auto-detect), `--json <path>` (optional report).

## Exit codes

| Code | Meaning |
|---|---|
| `0` | every check passed |
| `1` | at least one Error finding |
| `2` | no loadable For The King II install — **skipped, not failed** |

`Warning` findings never fail the run, matching `ClassForge.PackCheck`.

## Check inventory

| Family | File | What it asserts |
|---|---|---|
| Live config sanity | `Program.cs` | `Configs` loaded and populated; an invented id is absent |
| Game vocabulary | `GameVocabulary.cs` | enum member names + learned `BaseType`/`BodyType`/tag/class/rarity/status-group vocabularies |
| Install provenance | `InstallProvenance.cs` | expected packs present · every live id accounted for · no repo id baked onto disk · version drift (Warning) |
| Adds-only | `Checks/AddsOnlyChecks.cs` | pack load produces zero Errors; no pack id collides with a live id |
| Reference integrity | `Checks/ReferenceChecks.cs` | every id a pack points at resolves; recipe `CharacterConfig` spawns resolve; status-group prefixes (Warning) |
| Localization | `Checks/LocalizationChecks.cs` | name + tooltip coverage; no vanilla shadowing |
| Blessings roster gate | `Checks/BlessingsChecks.cs` | offline replica of the fail-closed `EnsureRosterResolvesAgainstConfigs` gate |
| Summoner | `Checks/SummonerChecks.cs` | follower packs adds-only vs live `Configs` |
| Inventory | `Checks/InventoryChecks.cs` | per-pack contribution counts, so a dropped pack cannot hide |
| Recipes | `Checks/RecipeChecks.cs` | recipes validate; status references resolve; modifier tables generate |
| Determinism | `Checks/DeterminismChecks.cs` | two loads agree on `dataHash` and merge ordering |

Every family carries a **negative control** or a deliberately-broken fixture
(`fixtures/collide/`, `fixtures/dangling/`), and every collection-scanning check asserts a non-zero
subject count so a vacuous pass is impossible. Both fixtures have been observed failing when
inverted — a fixture never seen failing is decoration.

## The recorded install baseline

Measured 2026-08-23 and re-confirmed 2026-08-25:

| Dictionary | Live count | Vanilla baseline | Recorded pack contribution |
|---|---|---|---|
| `Characters` | 2126 | 2095 | `EOR_*` × 31 |
| `Things` | 2380 | 1847 | `ARM_*` × 533 |
| `Abilities` | 992 | 992 | — |
| `SkillConfigs` | 68 | 68 | — |
| `StatusEffects` | 189 | 189 | — |
| `Followers` | 48 | 48 | — |
| `Langs["en"]` | 9707 keys | 9707 | — |

**This install is deliberately contaminated and that is correct.** The co-op saves depend on the
Enhanced Overhaul Revamped content, and this repo's own FTK2.Armory writes the `ARM_*` Things.
Steam's *Verify integrity of game files* **deletes** both. Never run it to make this harness green —
the provenance gate exists to detect exactly that damage, not to demand purity. If content is
missing, restore from `C:\Users\ben\Backups\ftk2-2026-08-23\`.

## The two field-map traps

1. **`Passives` resolve against `Configs.SkillConfigs` (68 entries), not `Configs.Abilities`.**
   Checking `Abilities` produced **100 false dangling findings** (2026-08-08).
2. **`Passives` must ALSO resolve against the packs' own `skillrecipes.json` ids**, which are absent
   from `ClassForge`'s `MergePlan` entirely. Omitting them produced **56 false findings**
   (2026-08-23). `AuthoredContent` runs a separate `RecipeParser` pass for exactly this reason, and
   derives the two engine-synthesized modifier recipe ids with
   `ModifierRecipeGenerator.DeriveApplyId` rather than hard-coding them.

A third trap, measured 2026-08-25 while building this: **"a custom status id must start with a
vanilla `eStatusEffectsGroups` member" is not a hard rule.** `eStatusEffectsGroups` has 95 members
and **35 of the 189 vanilla status ids do not start with any of them** (`AURA_ATTACK_00`,
`STATUS_CURSE_00`, `STATUS_GUARD_00`, `STATUS_SANCTUM_*`, …), and `StatusEffectConfig` has no
`Group` field. It is therefore reported as a **Warning**, not an Error. Escalating it would fire on
18% of the base game.

## What it cannot catch

Everything that needs a running game: Harmony patch application, UI injection (the class-select and
trait-pick lists), combat behaviour, the multiplayer handshake, save/load, and visual correctness of
a model or icon. Those need the operator smoke script and the Crucible S1/S3/S4 components. This
harness reduces what must be checked by hand; it does not replace in-game evidence.

It also does not apply the merge to `Configs` — that is `ClassForge.Plugin`'s
`ConfigMergePatches.ApplyPlan`, a private method in a net472 BepInEx-coupled assembly.

## Re-grounding

When a member name in `GameData.cs` or `GameVocabulary.cs` stops resolving, the answer comes from
`dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- <TypeName>`, not from memory.
