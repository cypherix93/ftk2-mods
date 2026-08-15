# Harness evidence

Transcripts that cannot be regenerated on demand, kept because the condition they capture is destroyed by
fixing it.

## `ac2-contaminated-install.txt`

Captured 2026-08-15 against `E:\Games\Steam\steamapps\common\For The King II` with the shipped gate logic.

Proof that the pristine-baseline gate rejects a contaminated install rather than passing vacuously. At
capture time the install carried a third-party overhaul that had written two item catalogs —
`Configs/JSON~/Things/ARM_EOR_ITEMS.json` and `ARM_EOR_STARTERS.json` — directly into the game's own
config tree.

- Command: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
- Exit code: **1**
- Result: `install is a pristine baseline` **FAIL**, 417 offenders, every one an `ARM_EOR_*` id reported
  as *contains third-party marker `EOR_`*. Console output truncates the list at 15 plus a count.

Two properties of this transcript are the point of keeping it:

- **No false positives.** Not one vanilla id appears, though the same `Things` dictionary holds hundreds of
  legitimate `ARM_*` entries from `ARM_CATALOG_*.json` and `ARM_FORGE_CURATED.json`.
- **It catches contamination a prefix rule cannot see.** Every offender is filed under the *vanilla* `ARM_`
  prefix; only matching the marker anywhere in the id surfaces them.

The run also shows the two content findings the harness reports against pack data — a status id that does
not exist, and a tag nothing consumes — which are tracked separately and deliberately left failing.

### Why the gate does not simply key on `ARM_`

An earlier revision treated `ARM_` as a third-party prefix. That produced 533 offenders against this same
install, the overwhelming majority of them legitimate vanilla items such as `ARM_BRAMBLE_MACE`, and would
have left a pristine install permanently red. `ARM_` is a vanilla prefix; the overhaul merely files its own
items under it.

The survey that originally judged `ARM_` safe grepped a top-level `Things.json`, which does not exist —
`Configs.Things` is assembled from a `Things/` subdirectory of catalog files, so the survey matched nothing
and reported clean. Any future prefix audit must read the loaded `Configs`, not the top level of the config
folder.

### Restoring a contaminated install

Steam → *Verify integrity of game files*. Note that this does not remove a mod's plugin DLL from
`BepInEx/plugins`, so a mod that writes on launch can re-contaminate the install the next time the game
runs.
