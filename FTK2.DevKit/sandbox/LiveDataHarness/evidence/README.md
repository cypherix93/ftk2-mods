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

This is the negative control for the gate. The equivalent positive run — the same command
exiting `0` against a restored install reading 2095 Characters — is reproducible at any
time and so is not archived here.

The two `No parser found for config ...` lines are emitted by the game's own loader for
`EncyclopediaDatas` and `Materials`; they occur on a pristine install too and are unrelated
to contamination.
