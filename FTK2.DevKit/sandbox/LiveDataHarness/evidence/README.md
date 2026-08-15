# Harness evidence

Transcripts that cannot be regenerated on demand, kept because the condition they capture
is destroyed by fixing it.

## `ac2-contaminated-install.txt`

Captured 2026-08-15 against `E:\Games\Steam\steamapps\common\For The King II`.

Proof that the pristine-baseline gate actually rejects a contaminated install rather than
passing vacuously. At capture time the install carried a third-party overhaul mod that had
written 31 `EOR_*` classes directly into
`For The King II_Data\StreamingAssets\Assets\Configs\JSON~\Characters.json`, taking the
top-level key count from the vanilla 2095 to 2126.

- Command: `dotnet run --project FTK2.DevKit/sandbox/LiveDataHarness -c Release`
- Exit code: **1**
- Result: `live Configs are populated` PASS, `install is a pristine baseline` **FAIL**,
  naming all 31 offending ids (console output truncates the list at 15 plus a count).

This is the negative control for the gate.

Two corrections learned after this transcript was captured, both folded into the gate:

- **`ARM_` is a vanilla prefix, not a third-party one.** The game ships
  `Configs/JSON~/Things/ARM_CATALOG_{S13,S46,S79,UNIQ}.json` and `ARM_FORGE_CURATED.json`,
  so hundreds of legitimate ids (`ARM_BRAMBLE_MACE` among them) start with `ARM_`. Gating on
  it produced 518 false offenders and would have kept the baseline permanently red.
- **Contamination is not confined to top-level id prefixes.** The same overhaul also ships
  `Things/ARM_EOR_ITEMS.json` and `Things/ARM_EOR_STARTERS.json` — item ids filed under the
  vanilla `ARM_` prefix, invisible to any rule anchored at position zero. The gate therefore
  matches the `EOR_` marker anywhere in an id as well as matching prefixes at the start.

The corresponding positive run — exit `0` with both checks passing — was produced against a
baseline reconstructed from the install by removing those two catalogs, since restoring the
real install was out of scope at the time.

The two `No parser found for config ...` lines are emitted by the game's own loader for
`EncyclopediaDatas` and `Materials`; they occur on a pristine install too and are unrelated
to contamination.
