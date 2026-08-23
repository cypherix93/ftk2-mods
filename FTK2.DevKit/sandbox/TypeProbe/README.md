# TypeProbe

Dumps verified member lists for game types with **no game running**, so the grounding rule
(spec §2) can be enforced: no game member name enters code until it has been read out of the
retail assembly.

## One command

    dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- NetworkData CombatState

Options: `--game <dir>` (defaults to the Steam install), `--methods` (include methods).

Output is a markdown table, ready to paste into a `docs/research/*.md` field map.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every requested type was found |
| 1 | at least one type was not found |
| 2 | the game assembly could not be loaded (skipped, not failed) |

## What it cannot do

- It does **not** run the game, so it cannot tell you whether a member is *populated* at runtime,
  only that it exists. `MultiplayerDemoQuickCombat` is registered and callable and still throws
  `NotImplementedException`; only running it revealed that.
- It reports a partial type list when Unity types fail to resolve outside the player. This is
  expected and does not affect plain data types.
- It reads only. It never writes to the game directory.
