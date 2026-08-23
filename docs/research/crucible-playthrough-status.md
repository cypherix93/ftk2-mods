# Playthrough status — measured live 2026-08-23

The owner's bar (AC0): drive a FULL adventure run to completion over MCP. This records
exactly how far a real run got, and what blocks the rest. Everything marked PROVEN was
executed against the running game, not inferred.

## Proven live

| Capability | How | Evidence |
|---|---|---|
| Escape boot state | `ui_click online-quit-btn` → `back-btn` | modal + lobby cleared |
| Menu navigation | `ui_click campaign-btn`, category buttons | reached adventure select |
| Adventure select | `ui_nav down` + `ui_submit` | The Resistance selected |
| Difficulty | `ui_focus difficulty-inp` + `ui_nav left` | MASTER→JOURNEYMAN→APPRENTICE |
| Party creation | `ui_click add-character-btn` + `crucible_pad a` ×4 | 4 slots filled |
| **Class choice by id** | `ui_focus class-text-selector` + `ui_nav` | `CF_EOR_THIEF`→`CF_EOR_TRICKSHOT` |
| Start run | `party-container-next-btn` → `begin-adventure-btn` → `sys-dialog-ok-btn` | route ADVENTURE |
| Clear load gate | `crucible_pad a` | LoadingUIDocument continue cleared |
| **Advance dialogue** | `ui_click dialogue-container` | whole conversation cleared |
| **Quest read** | `crucible_get RouterHelper.Env.GameRun.ActiveQuests` | 4 quest ids returned |
| **Turn advance** | `ui_click end-turn-btn` | active character cycled Thief→Shepherd |
| **Fixture capture** | `saveUser` | GameRuns 20→21, 4.95 MB |
| State observation | `/state?schema=v2` | combat block + honest warnings |

## Reached

Autumn Forest overworld, Ashhaven, Round 0, party of four ClassForge classes
(Thief / Shepherd / Corsair / Bladedancer) with HP and stats rendering.
Active quest: `STORY_1_1_VISIT_PRAN` — "Go to Pran in the Autumn Forest".

## BLOCKED: overworld movement

Movement is the gap between here and combat, and combat is the gap between here and
testing any of the 60 authored recipes.

- Hexes are 3D world objects, NOT UI elements — `crucible_ui_*` cannot reach them.
- The D-pad PANS THE CAMERA; it does not move a hex cursor. There is no cursor to drive.
- The game's overworld movement is mouse-driven: click a destination hex.
- **The harness has no mouse-input command.** That is the missing primitive.

Two candidate fixes, in preference order:
1. `AdventureDirector._doTeleportAbility` — a game API, no screen coordinates involved,
   and robust to camera position. Being built as a debug verb.
2. A synthetic mouse click at screen coordinates. Works, but requires mapping a world hex
   to a screen point, which is fragile across camera pans and resolutions.

## Not yet reached

- Combat (blocked on movement)
- Any of the 60 authored skill recipes firing (blocked on combat)
- Fixture RELOAD — `saveUser` writes, but round-tripping to this position is unverified
- Run completion

## Open bugs found in passing

- "Field Medic" appears TWICE in the loadout list. `TRAIT_CF_FIELDMEDIC` is the only one of
  20 EOR traits carrying a `CF_` infix; likely cause, unconfirmed.
- Writing `AdventureState.CurrentTimeOfDayIndex` succeeds (`old=0 new=2`) but the HUD still
  reads "Dawn" — direct field writes do NOT propagate. Debug verbs must call game methods
  and verify an observable, not poke state.
- The virtual gamepad registers as a SECOND PLAYER; party slots alternate P1/P2.
