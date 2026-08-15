# LiveDataHarness

Runs every shipped mod pack's host-agnostic `.Core` logic against the **real game's `Configs`**, loaded
out-of-process — no Unity, no BepInEx, no running game, no deployed mods. Id drift, dangling references,
missing localization, fail-closed data gates and non-determinism are caught by one command instead of by a
35-step manual in-game checklist.

The game is reached entirely by reflection: `Assembly.LoadFrom` on the install's `Managed/FTK2.dll` plus an
`AssemblyResolve` fallback to the same folder, then `ConfigsHelper.LoadConfigs(basePath)`. There is no
compile-time reference to `FTK2.dll`, `UnityEngine*.dll` or `BepInEx.dll` and no NuGet package, so this
project builds on a machine with no game installed and degrades to a clear skip at runtime.

## The one command

```
pwsh -File tools/run-harness.ps1
```

Optional: `-GameDir "X:\...\steamapps\common\For The King II"` to point at a specific install (otherwise
auto-detected), `-JsonOut <path>` to relocate the report.

The wrapper builds, runs the suite twice in **two separate processes**, and compares the two JSON reports
byte-for-byte. That cross-process comparison is the real determinism test: ordering that happens to be
stable inside one process can still differ across processes, and it is the cross-process case that decides
whether two players' installs agree on the pack `dataHash`.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | every check passed |
| `1` | at least one check failed, or the two processes' reports differed |
| `2` | no usable game install found — **skipped, not failed** |

## Reading the game safely

The harness only ever reads the game directory. `ConfigsHelper.LoadConfigs` was verified not to write:
a full run against a copied config tree left all 1278 files byte-identical. It is therefore safe to run
while the game is open, and it never requires the mods to be deployed — pack content is read from the
repo's `data/` folders, so a clean install is the correct setup.

Reading is not the same as trusting. `-GameDir` / `--game-dir` is a code-execution boundary: the harness
`Assembly.LoadFrom`s that install's `Managed/FTK2.dll` — and, through the resolve fallback, its neighbours
in that folder — then invokes `ConfigsHelper.LoadConfigs`, which runs that assembly's static initializers
and loader code inside this process. Point it only at an install you trust. The read-only guarantee covers
what the harness itself writes, not what the code it loads is capable of.

The report path is the one caller-controlled write, and it is refused if it resolves outside the repo.

## The pristine-baseline gate

Every other check is gated on the install being free of third-party content, because a mod that writes into
the game's own shipped JSON silently redefines what "a live id" means: a genuine collision gets masked, and
a legitimately new id gets reported as a collision.

Two things make this harder than a prefix scan, both learned the hard way:

- **`ARM_` is a vanilla prefix, not a third-party one.** The game ships `Configs/JSON~/Things/ARM_CATALOG_*.json`
  and `ARM_FORGE_CURATED.json`, so hundreds of legitimate ids (`ARM_BRAMBLE_MACE`) start with it. Gating on
  it produces 518 false offenders.
- **Contamination hides mid-id.** A mod can file its content under a vanilla prefix — `ARM_EOR_ITEMS.json`
  and `ARM_EOR_STARTERS.json` are real examples — where no rule anchored at position zero can see it. The
  gate therefore matches markers anywhere in an id as well as prefixes at the start.

`Configs.Things` is assembled from a `Things/` subdirectory of catalog files, **not** from a top-level
`Things.json`. Any survey that greps for the latter silently matches nothing and reports clean.

To restore a contaminated install: Steam → *Verify integrity of game files*. Note that this does not remove
a mod's plugin DLL from `BepInEx/plugins`, so a mod that writes on launch can re-contaminate the install the
next time the game runs.

## Check inventory

| Section | What it proves |
|---|---|
| Live config sanity | the config load populated every dictionary the checks depend on, and `Langs["en"]` exists |
| Pristine baseline | no third-party content is present on disk (hard gate — see above) |
| Game vocabulary | the enum + learned-value allowlists built, so reference checks are not vacuous |
| ClassForge pack load | shipped packs load with zero Error findings and no id collides with a live vanilla id |
| Reference integrity | every `Passives` / `Things` / `BaseType` / `DefaultBodyType` / `Rarity` / `Expansion` / `Tags` value on a merged class resolves |
| Localization coverage | every merged class has a name and tooltip key, every trait has a name key, and no pack key shadows a vanilla one |
| Blessings roster gate | every roster `TraitId` resolves, so the plugin will not fail-closed and disable itself for the session |
| Summoner adds-only | no follower or character id collides with live `Configs`, and at least one add is actually planned |
| Recipes | every `skillrecipes.json` parses and validates, every applied `StatusEffect` id resolves, and modifier tables generate valid recipes |
| Determinism | `dataHash`, merge-op ordering and pack load order are identical across two loads |

Every check family carries a negative control or a deliberately-broken fixture (under `fixtures/`), so that
"it passed" means something. Inverting a fixture must flip its case to FAIL.

## The field map, and the trap in it

For a merged class, a value is valid when it is in the named set:

| Field | Resolves against |
|---|---|
| `Passives` | `Configs.SkillConfigs` ∪ merged `Abilities` ∪ merged `StatusEffects` ∪ **skill ids defined in any pack's `skillrecipes.json`** |
| `Things` (keys) | `Configs.Things` ∪ merged `Things` |
| `BaseType` | learned vanilla `BaseType` vocabulary |
| `DefaultBodyType` | learned vanilla `DefaultBodyType` vocabulary |
| `Rarity`, `Expansion` | `FTK2.dll` enum member names |
| `Tags` | learned vanilla tag vocabulary ∪ enum members ∪ loaded pack ids |

**`Passives` resolve against `SkillConfigs`, not `Abilities`.** Checking `Abilities` produces 100 false
dangling findings. This is the single trap a future maintainer is most likely to re-fall into.

`Passives` also resolve against skill ids a pack defines in its own `skillrecipes.json`, which is not a
merge category and appears nowhere in the merge plan. Omitting that source reports all 41 `SKILL_CF_*`
signature skills as dangling.

`BaseType` and `DefaultBodyType` are plain strings with a closed de-facto vocabulary, not dictionary keys;
checking them against `Configs.Characters` produces 31 more false findings.

The `Rarity`, `Expansion` and `Tags` rows are membership tests against one flat enum vocabulary, not against
the specific enum each field is typed as — see "What this cannot catch".

## What this cannot catch

The harness stops where the game engine begins. It does **not** exercise:

- Harmony patch application, or any patched method's behaviour
- the merge actually being applied to `Configs` (that path lives in a BepInEx-coupled assembly)
- UI injection — class-select and trait-pick lists, tooltips as rendered
- combat behaviour, damage numbers, status application at runtime
- multiplayer: the parity handshake, desync detection, join-in-progress
- anything requiring a running game at all
- enum-value typos that land on the wrong enum: `Rarity`, `Expansion` and `Tags` are checked against one
  flat set of every member name across all 395 `FTK2.dll` enums, because the merge plan carries no type
  information for a `JsonValue` field. `Rarity: "MELEE"` — a weapon-type value — therefore passes

Those still need a game launch. The in-game checks live in the operator smoke script under
`docs/superpowers/plans/`. This harness reduces what must be checked by hand; it does not replace the
in-game evidence for UI, combat, or multiplayer.
