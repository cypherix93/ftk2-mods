# Multiplayer architecture (repo-wide)

**Decision (2026-07-10, project owner): co-op multiplayer is a hard requirement.** The game is played
primarily with friends; every mod in this repo must be designed MP-first, not SP-first with an MP
caveat. This doc defines the shared architecture; each SPEC's §9 states how the mod applies it.

## What we know about FTK2 netcode (evidence + gaps)

Verified/precedented (from the EOR mod's decompilable implementation and FTK2.dll surface):
- Custom network actions can piggyback **`AdventureDirector._handleNetworkAction`** with string-keyed
  payloads (EOR: `EOR_SYNC_TOWN_SNAPSHOT_V1`, `EOR_MP_QUEST_ARCHETYPE`, …). This is our transport.
- EOR runs a **mod-parity handshake** hashing mod version / config values / major data files /
  enabled systems / definitions (`EOR_VER / EOR_CFG / EOR_DAT / EOR_SYS / EOR_DEF / EOR_SIG`) and
  warns "Multiplayer desync likely" on mismatch. Proven pattern; we generalize it.
- EOR uses **shared deterministic RNG** (`EOR_SHARED_RNG|…`) and skips rolls when no deterministic
  `GameRandom` is available — the game has a deterministic-random facility we must reuse for any
  roll whose outcome peers must agree on.
- EOR implements **host-authoritative town snapshots** — host authority over derived state, clients
  receive snapshots.
- `CombatComponent` (PA/SA, CanSummon, …) is a serialized component; combat actions replicate through
  the vanilla action pipeline.

Open questions to resolve in the decompile pass (tracked here, not per-spec):
1. Is enemy AI decision-making host-only in vanilla (i.e. does `AIHelper` run only on host, with
   resulting actions replicated)? Strong prior: yes. Every AI-side design assumes it; verify first.
2. Does `GameRunData` custom state replicate to clients or live host-side only?
3. Does `CombatState.GridType` sync natively or is it computed per-peer?
4. Does `PartyManagementDirector._rebuildCharactertAsNewConfigType` propagate to peers natively?
5. Exact payload shape/size limits of `_handleNetworkAction` custom actions.

## The five rules

### R1 — Parity: all peers run the same mods + the same data
Config-data divergence is the #1 desync source (the game simulates from `Configs`, and our mods merge
into `Configs`). Enforcement, not hope:
- Every `ftk2mods.*` plugin registers `(guid, version, dataHash, enabledFeatures)` with the shared
  **ParityService** (lives in FTK2.DevKit; tiny reflection-based registration so there is no hard
  build dependency).
- On session join/host, ParityService exchanges registrations via `_handleNetworkAction`
  (`FTK2MODS_PARITY_V1`) and compares.
- On mismatch: prominent in-game warning naming the exact mod + which part diverged (version vs data
  vs features), and each mod receives a `ParityFailed` callback. Default policy knob
  `[Multiplayer] OnParityMismatch = WarnAndSafeMode` (alternatives: `WarnOnly`, `Block`).
  **SafeMode** means the mod disables its state-mutating features for the session but keeps
  presentation-only features.
- `dataHash` = SHA-256 over the mod's data files, computed with sorted file order,
  normalized line endings, invariant culture. Localization files are excluded (EOR precedent:
  text-only files don't affect gameplay state).

### R2 — Determinism: generated content must be identical on every peer
Anything a mod *generates* from data (Forge item ladders + recipes, Runeworks socketed-item variant
configs, WarBrain profile resolution, Questsmith board injections) must be a **pure function of the
shared data**: sorted/stable iteration order, invariant-culture string handling, no wall-clock, no
unseeded `Random`. Generated ids must be reproducible (derive from content, e.g.
`RW_VAR_<baseId>_<gemhash>`), never from insertion order or GUIDs minted at runtime.
Any *roll* whose outcome peers must agree on goes through the game's deterministic `GameRandom`
(EOR `EOR_SHARED_RNG` pattern) or is decided host-side and synced — never local `System.Random`.

### R3 — Host authority for decisions; vanilla pipeline for effects
Decision-making state and logic run **host-side**: AI brains and memory (WarBrain), evolution
counters and triggers (Summoner), custom quest-verb evaluation (Questsmith), grid-rule evaluation
(Venue). The *effects* of decisions must flow through mechanisms the game already replicates
(combat actions, quest state, config-defined content) whenever possible — that way clients don't
need bespoke sync. Custom state that clients must *display* (bond levels, memory-driven telegraphs,
AP pools if vanilla doesn't cover them) syncs as versioned snapshot actions
(`<PREFIX>_SYNC_<NAME>_V1`), host → clients, idempotent to apply.

### R4 — Presentation-only features are parity-exempt
Logging, decision-breakdown dumps, camera/UI cosmetics, localization: no parity requirement, may
run on any subset of peers. Each SPEC labels its features `[SYNCED]` or `[LOCAL]`.

### R5 — Dev mutations are disabled in MP
DevKit's mutating commands (give item, win combat, set stat, force reload mid-session) are
hard-gated off in MP sessions unless `ForceAllowInMP` is set — and even then they run host-only and
log a session-visible warning. JSON hot-reload in MP requires a re-run of the parity handshake
afterward (all peers must reload the same change; otherwise SafeMode).

## Standard spec language

Every SPEC §9 must answer:
1. **Parity class**: `ALL_PEERS` (mod + data required everywhere) / `HOST_ONLY` (host-only install
   is sufficient and safe) / `LOCAL` (pure presentation).
2. **Feature table**: each feature marked `[SYNCED]` / `[LOCAL]`, with its authority (host/all).
3. **Determinism inventory**: what the mod generates or rolls, and how R2 is satisfied for each.
4. **Sync surface**: which vanilla mechanisms carry its effects; any custom `_SYNC_` actions
   (name, payload sketch, when fired, idempotency).
5. **SafeMode definition**: exactly which features turn off on parity mismatch.
6. **MP test plan**: a host+client smoke test exercising the mod's core loop and checking for
   desync (leverage EOR's "Print Sync-Relevant Data Hash" debug precedent / DevKit dump-compare).

## Practical guidance

- Prefer **config-shaped content over runtime state**: an item that exists as a real (deterministic,
  parity-hashed) `ThingConfig` on all peers syncs for free; a sidecar dictionary does not. This is
  why Forge/Runeworks favor generated-config architectures.
- Prefer **host-decides + vanilla-replicates** over custom sync. Custom `_SYNC_` actions are a last
  resort for display state.
- Mid-session joins (if the game supports rejoin): every `_SYNC_` snapshot must be requestable
  (`…_REQUEST_V1` pattern — EOR does exactly this for town snapshots).
- Version every payload (`_V1`) — peers on different mod versions already fail R1, but versioned
  payloads make the failure diagnosable.
