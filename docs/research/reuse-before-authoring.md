# Reuse before authoring — the pre-research gate

**Rule (Ben, 2026-08-25): never invent monsters, statuses, abilities, or primitives. Search the
game and this repo first. Authoring is the last resort, not the first move.**

This is not style advice. Every item below is a real thing that already existed in the code while
we were building — or had already built — a replacement for it.

## Why: the scoreboard from one day

| We were about to author | It already existed | Cost of not checking |
|---|---|---|
| ~~Custom creature configs for 12 partners~~ **(SEE CORRECTION BELOW - this one was NOT an authoring fault)** | - | - |
| A custom WET status | `STATUS_WATER_00`, plus **41 vanilla abilities** that apply it *and* deal damage in one roll | would have been a second, invisible status |
| A random-effect mechanism | `ADD_STATUS "RANDOM_ABILITY:a,b,c"` (`CombatHelper.cs:1181-1188`), with shipped precedent `SHEEP_RANDOM_BUFF` — literally "a random ally buff spell" | the plan doc had written this off as impossible |
| Autonomous partner AI | partners **already had `AIComponent`** and already took their own turns (`CombatPhase.cs:1918`; `CharacterHelper.cs:1875`) | a checklist item sat "unbuilt" for weeks describing a feature that worked |
| A "make partners focus a target" system | `AIComponent.PriorityTargets`, drained before normal targeting (`AIHelper.cs:516-537`) | a command would have shipped as flavour |
| Art for pack-authored classes | `VisualRemapPatches.cs` — resolves pack ids to a vanilla DONOR record | the mechanism was RIGHT THERE and unused for creatures |
| An offline config validator | `LiveDataHarness` — fully specced in `docs/superpowers/plans/`, never implemented | 10 tasks of design sitting unread |

## CORRECTION (2026-08-25, same day) — the jelly case was OUR PATCH, not our authoring

The original headline example here was wrong and is retracted. Partners rendered as human
adventurers, but **not because the ids were invented.**

- The pack authors' reasoning was **correct and grounded**: use a REAL shipped family stem with a
  FREE two-digit suffix, so the game's own tier walk finds sibling art.
  `GetCharacterTierRecord` -> `TryGetNextConfigValueName` (`CharacterVisualHelper.cs:298-331`)
  halves the tier and walks the ladder, resolving `JELLY_ACID_01` -> `JELLY_ACID_03`. That works.
- **`VisualRemapPatches` was suppressing it.** `ConfigMergePatches.cs:77` feeds it EVERY authored
  character id, not just playable classes. With no case for the creatures it fell to a default
  donor list (`ASTRONOMER, SCHOLAR, BLACKSMITH`) and `return false`, skipping the original method
  that would have resolved correctly. We overrode a working mechanism with a humanoid.
- `BAT_VAMPIRE_01` rendered correctly **not because it is shipped** but because it is not authored
  in any pack, so the patch never touched it. The real distinction is **intercepted vs not**.
- Fix: `VanillaCanResolve()` — let vanilla resolve anything it can already resolve. No donor table,
  no data change. Verified on screen: a blue gelatinous cube and a red jelly on the board.

**Why the wrong diagnosis survived so long:** the "no donor resolvable" warning was structurally
unreachable — `LastResort = {ASTRONOMER, SCHOLAR, BLACKSMITH, HOBO}` is appended to every candidate
list, so `TryResolveDonor` returns true for any input. We had instrumented a MISSING donor; the
actual failure was a WRONG donor, which nothing logged. Both are loud now.

**The rule below still stands** — the other six rows are real, and "check before authoring" would
still have found `VisualRemapPatches` and prompted the question "does this already handle creatures?"
But do not cite the jellies as an authoring failure. They were an INTERCEPTION failure.

**The one that proves the rule:** `SKILL_CF_VAMPIRIC_BAT_SWARM` summons `BAT_VAMPIRE_01`, a
**shipped** config — and it renders correctly as an actual bat, confirmed on screen twice. Every
partner that renders as a human is one where we invented the id instead. The working pattern and
the broken pattern were in the same pack, at the same time.

## The gate — run this BEFORE authoring anything

Answer all five in writing. "I didn't find one" is only valid after you have actually looked.

1. **Does the GAME already ship this?**
   Search `For The King II_Data\StreamingAssets\Assets\Configs\JSON~\` —
   `Characters.json`, `Abilities.json`, `StatusEffects.json`, `Things/*.json`.
   For a creature: does a config with this exact id exist? **A pack-authored creature id that is
   absent from shipped `Characters.json` has NO `dCharacter` record — no prefab, no portrait — and
   will fall back to generic humanoid art.**
2. **Does the ENGINE already do this?** Search `.decompile-scratch/proj/` for the verb, not the
   noun: "how does the game make something random / target something / remove a status".
3. **Does THIS REPO already do this?** Search `src/`, and search `docs/superpowers/plans/` for a
   spec that was written and never built.
4. **Is there a PRECEDENT to copy?** A vanilla ability, a shipped status, an existing patch in this
   plugin. Copying a working pattern beats authoring a parallel one.
5. **If you must author: what makes it VISIBLE?** A new id needs art, localization, and — for a
   pack class — a donor via `VisualRemapPatches`. Name all three or it ships invisible.

## Evidence rules

- **Ground every id in shipped configs or the decompile, with file + line.** Never from plausibility.
- **Say "unverified" rather than reasoning from plausibility.** A confident guess is worse than a
  flagged gap, because nobody re-checks a confident claim.
- **A GameObject named after a config does NOT prove the right art loaded.**
  `FromCharacter` returning `JELLY_ACID_016777 (CharacterGameObject)` was treated all session as
  proof the mesh resolved. It is not — the object is merely *named* after the config id, and it
  reads identically whether a jelly or a human rendered. **Only a screenshot discriminates art.**
- **Read both sides before theorising about either.** From the co-op diagnosis in Ben's mindex
  notes: two confident wrong calls (version mismatch, then config mismatch) were made from one
  log while the answering log sat on disk. Same failure recurred here — `VisualRemapPatches`
  logs `no donor dCharacter record resolvable for pack class '<id>'`, and nobody grepped it.

## Put this in every dispatch

Any brief that authors content must carry:

> RESEARCH FIRST: search shipped configs, the decompile, and this repo for something that already
> does this. Report what you found and why it does or does not serve, BEFORE authoring anything.
> Every id must be verified present in shipped data (or verified genuinely new AND wired to art +
> localization + a donor). Invent nothing. Say "unverified" rather than guessing.
