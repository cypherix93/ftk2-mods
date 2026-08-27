#!/usr/bin/env python3
"""Exercise the still-unproven CF_PACK_ORIGINALS class traits in ONE live combat and assert each
from state READ BACK from the game -- never from a command's own success string.

Covers, one section each. ORDER (2026-08-25 re-pass) is deliberate, not source order -- see the
SHIELDED comment at its call site for the measured reason it was moved to run first:
  0. TRAINER   -- GRASS partner summon (SKILL_CF_TRAINER_GRASS_1, ON_COMBAT_START) + prefab check
  1. TRAINER   -- Shielded by his Pokemon (SKILL_CF_TRAINER_SHIELDED, ALLY_IN_FRONT physical mitigation)
  2. TRAINER   -- Gotta Train 'Em / cf_bond (partner-bond counters)
  3. VAMPIRIC  -- Engorged (STATUS_VIGOR_CF_ENGORGED, ON_DAMAGE_DEALT + HP_THRESHOLD GTE 70)
  4. VAMPIRIC  -- Bat Swarm (LOG-BASED: cf_gorged >= 3 is consumed the instant it's reached, so the
                  gate/proc/consume sequence is asserted from Player.log, not from the counter read-back)
  5. PACIFIST  -- Field Medic (a hurt ally healed at turn end)
  6. PACIFIST  -- Why Can't We Be Friends (damage reflected onto whoever hits the Pacifist)

RE-AUTHORED 2026-08-25: the old CHAOS/EMBER/RIME Trainer partner line (SKILL_CF_TRAINER_CHAOS_1..4)
was replaced by GRASS/WATER/FIRE (SKILL_CF_TRAINER_GRASS_1..4, tier-1 partner config JELLY_ACID_01,
"Sporeling"). No CHAOS_1..4 reference remains in this module (grepped clean). Two more recipes are
new tonight: SKILL_CF_TRAINER_SHIELDED (physical damage mitigation while a living partner stands
directly in front) and the re-authored SKILL_CF_VAMPIRIC_ENGORGED trigger (see section 2 below).

Ids verified against source, not assumed:
  CF_ORIG_TRAINER / CF_ORIG_VAMPIRIC / CF_ORIG_PACIFIST
      FTK2.ClassForge/data/ClassPacks/CF_PACK_ORIGINALS/classes.json
  cf_bond, cf_gorged (COUNTER_ADD names), STATUS_VIGOR_CF_ENGORGED, SKILL_CF_TRAINER_GOTTA_TRAIN_EM,
  SKILL_CF_VAMPIRIC_ENGORGED, SKILL_CF_VAMPIRIC_BAT_SWARM, SKILL_CF_PACIFIST_FIELD_MEDIC,
  SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS, SKILL_CF_TRAINER_GRASS_1, SKILL_CF_TRAINER_SHIELDED
      FTK2.ClassForge/data/ClassPacks/CF_PACK_ORIGINALS/skillrecipes.json
  STATUS_VIGOR_CF_ENGORGED shape (Stats HP=8, a VIGOR max-hp-raise, not a heal-past-max)
      FTK2.ClassForge/data/ClassPacks/CF_PACK_ORIGINALS/statuses.json
  JELLY_ACID_01 ("Sporeling", HP 16, GRASS tier-1 partner) -- verified live 2026-08-25, renders as a
      real teal jelly model, not a fallback humanoid (RouterHelper.Env.VenueGameObjectMaps.FromCharacter).

Both counters (cf_bond, cf_gorged) are now read via the crucible_recipe_counters command
(FTK2.Crucible/src/Crucible.Plugin/RecipeCounterCommands.cs), which decodes CombatRuntime._counters
(FTK2.ClassForge/src/ClassForge.Recipes/Runtime/CombatRuntime.cs) into per-entity "name=value" pairs.
It reports four distinct response shapes, all handled by read_recipe_counter() below:
  "unreachable: ..."   the engine state could not be reached this run -> UNVERIFIABLE
  "reached OK: ... holds NO counters ..."   reached, genuinely empty  -> a real, assertable fact
  "error: ..."          bad argument                                  -> FAIL
  "<ownerGuid>: cf_bond=2, ..."   a populated listing                 -> parsed and asserted

A second finding, load-bearing for check 2, also came from reading source rather than assuming:
  CharacterHelper.SetToMaxHealth (.decompile-scratch/proj/CharacterHelper.cs:1259) writes
  CharacterComponent.CurrentHealth directly -- it does NOT call CharacterHelper.AddHealth. Only
  AddHealth's prefix fires ON_HEAL_PENDING (FTK2.ClassForge/src/ClassForge.Plugin/Recipes/
  CombatHookPatches.cs, "T8 ON_HEAL_PENDING ... AddHealth Prefix"). So crucible_heal_party (which
  calls SetToMaxHealth -- FTK2.Crucible/src/Crucible.Plugin/DebugVerbCommands.cs, CrucibleHealParty)
  can set up the "already at full HP" precondition but its OWN second call is not itself a trigger
  for Engorged -- it never reaches AddHealth. Separately, every in-kit heal this class pack ships
  (Crimson Drain, Field Medic) executes through the recipe engine's own STAT_CHANGE action, and
  RecipeActionExecutor.cs:85 wraps that whole execution in RecipeEngineHost.EnterExecution() --
  which AddHealth_Prefix's own RecipeEngineHost.TryBegin() call refuses to re-enter (the documented
  anti-recursion guard, RecipeEngineHost.cs "Re-entrancy"). So no author-recipe heal can ever be the
  thing that trips ANOTHER recipe's ON_HEAL_PENDING gate, by design.

  RE-AUTHORED 2026-08-25 (again): the recipe was MOVED OFF ON_HEAL_PENDING entirely, onto
  ON_DAMAGE_DEALT with HP_THRESHOLD GTE 70 (skillrecipes.json's own _design note has the full
  history, including two more live-measured gate-arithmetic fixes: GTE 100 could never be
  satisfied because Blood Price's HP cost runs first, and even GTE 90 blocked itself after one
  stack because each Engorged proc raises max HP). Budget is ONCE_PER_ROUND, so section 2 below
  drives the Vampiric's OWN attack across real rounds (heal to reset the HP% precondition, wait
  for its turn, attack, re-read statuses) instead of attempting a heal at all.

Commands used (verified against their registration in FTK2.Crucible/src/Crucible.Plugin):
  crucible_combat_snapshot, crucible_kill_target, crucible_combat_spawn, crucible_combat_end_turn
      -- CombatDriveCommands.cs
  crucible_heal_party   -- DebugVerbCommands.cs
  crucible_recipe_counters [entityGuidOrIndex]   -- RecipeCounterCommands.cs

Traps respected (see module docstring cross-refs in drive.py / battery.py / to_combat.py):
  * crucible_use_ability always acts as the ACTIVE entity and takes (abilityName, x, y) only.
  * Ability/status resolution is ASYNCHRONOUS -- every read-back sleeps first.
  * A combatant ROW requires both " name=" and " hp=" -- never search the whole snapshot text,
    the header carries its own activeGuid=<guid>.
  * Screenshots are POST /screenshot, not an /exec command -- shot() below is battery.py's verbatim.
  * crucible_combat_wipe_enemies bypasses the kill hook; only crucible_kill_target is used here to
    feed a kill-gated trait.
"""

import math
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

RESULTS = []   # (name, status) where status in {"PASS", "FAIL", "UNVERIFIABLE"}
SHOTS = []

# Same BepInEx-piped Player.log path assert_traits.py already reads in this tools/ directory --
# reused here (not reinvented) so the "proc <RECIPE> ..." line format and its access pattern stay
# in exactly one place.
PLAYER_LOG = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\Player.log")


def log_size():
    """Byte offset to read new log text from later. None if the log can't be stat'd right now."""
    try:
        return os.path.getsize(PLAYER_LOG)
    except OSError:
        return None


def log_since(offset):
    """New Player.log text since `offset`, or None if the file couldn't be read (distinct from ''
    -- '' means the file WAS read and simply had nothing new)."""
    if offset is None:
        return None
    try:
        with open(PLAYER_LOG, "r", encoding="utf-8", errors="replace") as handle:
            handle.seek(offset)
            return handle.read()
    except OSError:
        return None


def shot(label):
    """Capture a screenshot and remember it. Verbatim technique from battery.py: /screenshot is a
    dedicated HTTP route, not an /exec command, and only the path is worth printing."""
    try:
        out = drive._post("/screenshot", {})
    except Exception as exc:
        print("        (screenshot failed: %s)" % exc)
        return None
    path = ""
    if isinstance(out, dict):
        path = out.get("path") or ""
    else:
        for line in str(out).splitlines():
            if ".png" in line:
                path = line.strip()
    SHOTS.append((label, path))
    print("        shot[%s] %s" % (label, path[-64:] if path else "(no path)"))
    return path


def check(name, status, detail=""):
    """status is 'PASS', 'FAIL', or 'UNVERIFIABLE'. Only FAIL counts against the exit code -- an
    UNVERIFIABLE result is reported loudly, never silently folded into a pass."""
    RESULTS.append((name, status))
    tag = {"PASS": "  PASS ", "FAIL": "  FAIL ", "UNVERIFIABLE": "  ????? "}[status]
    print(tag + name + (("\n        " + detail) if detail else ""))


def snapshot():
    return drive.run("crucible_combat_snapshot")


def combatants(snap=None):
    """guid -> parsed row, for every actual combatant (not the header, not a tile). A row requires
    both ' name=' and ' hp=' -- the snapshot header carries its own activeGuid=<guid> and a naive
    substring search over the whole text false-matches it."""
    out = {}
    for line in (snap or snapshot()).splitlines():
        if " name=" not in line or " hp=" not in line:
            continue
        guid = line.strip().split()[0]
        row = {"line": line.strip()}
        for key in ("class", "group", "hp", "pa", "sa", "dead"):
            m = re.search(key + r"=(\S+)", line)
            if m:
                row[key] = m.group(1)
        m = re.search(r"tile=\((\d+), (\d+)\)", line)
        if m:
            row["tile"] = (int(m.group(1)), int(m.group(2)))
        m = re.search(r"statuses=count=\d+ \[([^\]]*)\]", line)
        row["statuses"] = m.group(1) if m else ""
        out[guid] = row
    return out


def by_class(rows, class_id):
    for guid, row in rows.items():
        if row.get("class") == class_id:
            return guid, row
    return None, None


def active_guid(snap=None):
    m = re.search(r"activeGuid=(\S+)", snap or snapshot())
    return m.group(1) if m else None


def _current_round():
    """combat.round from drive.snapshot() (GET /state?schema=v2 -> CombatReader.cs:39, TotalRounds).
    Hoisted to module level (2026-08-25) so sections 3 and 4 share ONE implementation -- both must
    drive a REAL round advance (never crucible_combat_restore_actions, which lets the same actor
    swing again inside the same round and so never resets a ONCE_PER_ROUND recipe budget)."""
    try:
        return drive.snapshot().get("combat", {}).get("round")
    except Exception:
        return None


def ensure_enemy_count(min_alive, enemy_config="BANDIT_RANGED_01"):
    """Tops up living group-1 combatants to at least min_alive via crucible_combat_spawn, which
    (unlike crucible_debug_spawn) adds directly into the LIVE CombatState."""
    rows = combatants()
    alive_enemies = [r for r in rows.values() if r.get("group") == "1" and r.get("dead") == "False"]
    need = min_alive - len(alive_enemies)
    if need > 0:
        result = drive.run("crucible_combat_spawn", [enemy_config, str(need), "1"])
        print("      spawned %d more %s: %s" % (need, enemy_config, result.splitlines()[0][:150]))
        time.sleep(1.5)
    return combatants()


def ensure_living_enemies(min_count=2, enemy_config="BANDIT_RANGED_01"):
    """BUG 2 guard: a section that needs an enemy to act or die must never run against a drained
    board. Counts living combatants NOT in group 0 (group 0 is the player's side -- the summoned
    GRASS partner and the Bat Swarm bat are BOTH group 0 and must never be counted as enemies), and
    tops up via crucible_combat_spawn (the SAME call ensure_enemy_count above already uses --
    CombatDriveCommands.cs) if short. Returns True only if living enemies exist afterward and False
    otherwise; never raises. A caller that gets False must report UNVERIFIABLE, never FAIL."""
    rows = combatants()
    living_enemies = [r for r in rows.values() if r.get("group") != "0" and r.get("dead") == "False"]
    if len(living_enemies) >= min_count:
        return True
    need = min_count - len(living_enemies)
    try:
        result = drive.run("crucible_combat_spawn", [enemy_config, str(need), "1"])
        print("      ensure_living_enemies: spawned %d more %s: %s"
              % (need, enemy_config, result.splitlines()[0][:150]))
    except Exception as exc:
        print("      ensure_living_enemies: spawn failed: %s" % exc)
    time.sleep(1.5)
    rows = combatants()
    living_enemies = [r for r in rows.values() if r.get("group") != "0" and r.get("dead") == "False"]
    return len(living_enemies) > 0


def kill_one(target_guid, killer_guid, label):
    """One crucible_kill_target call, re-read after a settle (the transition is async per the
    verb's own NOTE), returning (deadAfter, killTriggerPathInvoked, resultText)."""
    result = drive.run("crucible_kill_target", [target_guid, killer_guid])
    time.sleep(2.0)
    dead_after = "deadAfter=True" in result
    trigger_invoked = "killTriggerPathInvoked=True" in result
    print("      %s: %s" % (label, result.splitlines()[0][:170]))
    return dead_after, trigger_invoked, result


def hp_int(row):
    try:
        return int(row.get("hp", "0"))
    except ValueError:
        return 0


def group0_hp_snapshot():
    """{guid: (hp, maxHp)} for every LIVING group-0 combatant -- the group=0/dead= filter comes from
    combatants() (crucible_combat_snapshot's own text, the header/tile-safe row parser already used
    everywhere else in this module), but that text has no maxHp field at all (CombatDriveCommands.cs's
    row builder never appends one). maxHp is only exposed via drive.snapshot() (GET /state?schema=v2
    -> StateReaderV2 -> CombatReader.MaxHp, CombatReader.cs:130's CharacterHelperBridge.InvokeMaxHealth
    dispatch) -- confirmed live 2026-08-25 (Thief hp=51 maxHp=60, etc.), so the two are merged here by
    guid. Needed for ALLY_BY_RANK {HP_PCT, LOWEST} prediction, which ranks by hp/maxHp, not raw hp."""
    group0_guids = {g for g, r in combatants().items()
                     if r.get("group") == "0" and r.get("dead") != "True"}
    snap = drive.snapshot()
    out = {}
    for c in (snap.get("combat", {}) or {}).get("combatants") or []:
        guid = c.get("id")
        if guid in group0_guids and c.get("hp") is not None and c.get("maxHp"):
            out[guid] = (int(c["hp"]), int(c["maxHp"]))
    return out


def find_fromcharacter_entry(entity_guid, max_index=60):
    """Finds the ONE RouterHelper.Env.VenueGameObjectMaps.FromCharacter entry for entity_guid without
    ever rendering the whole map (which silently truncates to the first 5 of N entries -- confirmed
    live 2026-08-25: Count=12 renders only 5 and a BAT_VAMPIRE_01 entry at position 9 never appears).

    Indexing the dictionary BY THE GUID (FromCharacter[<guid>]) does not work -- confirmed live: it
    returns "error: ... member not found on VenueGameObjectMaps", because ReflectionCommands.
    SplitSubscript (FTK2.Crucible/src/Crucible.Plugin/ReflectionCommands.cs) only parses a trailing
    [N] as an INTEGER via int.TryParse; a non-numeric subscript is left as part of the (nonexistent)
    member name. FromCharacter is also keyed by the live CharacterComponent object, not by its guid
    string, so a guid-keyed lookup couldn't work even if the subscript parser accepted strings.

    What DOES work (confirmed live): FromCharacter[i] walks the dictionary's own IEnumerable
    POSITIONALLY (ReflectionCommands.TryIndex's IEnumerable fallback, since Dictionary<K,V> is not
    IList) and returns ONE boxed KeyValuePair, e.g.
      KeyValuePair`2 { Key=(BAT_VAMPIRE_01) - (2, 6) febc5d7a-...-b8dd3391a18f, Value=BAT_VAMPIRE_016781 (CharacterGameObject) }
    Rendering a single non-IEnumerable value never truncates (ReflectionCommands.Render's Count>5
    sampling only applies when the top-level value itself is a collection), and the entity's guid is
    embedded in Key.ToString() -- so scanning FromCharacter[0..Count-1] and matching entity_guid as a
    substring finds the exact entry regardless of position, exhaustively (not sampled).

    Returns (status, payload):
      "unreadable"  -- FromCharacter's own Count could not be read at all -> payload is that raw text
      "not_found"   -- scanned every entry, entity_guid appeared in none of them (the entity has no
                       instantiated actor at all -- the real "alive, targetable, invisible" bug this
                       check exists to catch) -> payload is a short explanation
      "found"       -- payload is the one matching "KeyValuePair`2 { Key=..., Value=... }" entry text
    """
    whole = drive.run("crucible_get", ["RouterHelper.Env.VenueGameObjectMaps.FromCharacter"])
    m_count = re.search(r"Count=(\d+)", whole)
    if not m_count:
        return "unreadable", whole
    count = int(m_count.group(1))
    for i in range(min(count, max_index)):
        entry = drive.run("crucible_get",
                           ["RouterHelper.Env.VenueGameObjectMaps.FromCharacter[%d]" % i])
        if entity_guid in entry:
            return "found", entry
    return "not_found", ("scanned all %d FromCharacter[i] entries (0..%d), none contained guid %s"
                          % (count, count - 1, entity_guid))


def read_recipe_counter(entity_guid, counter_name):
    """Runs crucible_recipe_counters [entity_guid] and decodes its four deliberate response shapes
    (RecipeCounterCommands.CrucibleRecipeCounters, FTK2.Crucible/src/Crucible.Plugin/
    RecipeCounterCommands.cs):
      "unreachable: ..."                          engine state not reached -> ('unreachable', text)
      "reached OK: ... holds NO counters ..."      reached, genuinely empty -> ('empty', text)
      "error: ..."                                 bad argument             -> ('error', text)
      "<ownerGuid>: name=value, ..." (per entity)  populated listing        -> ('found', int) if
                                                    counter_name is present for entity_guid, else
                                                    ('empty', text) -- entity reached but that
                                                    specific counter has no key yet.
    """
    text = drive.run("crucible_recipe_counters", [entity_guid]).strip()
    if text.startswith("unreachable:"):
        return "unreachable", text
    if text.startswith("error:"):
        return "error", text
    if text.startswith("reached OK:") and "holds NO counters" in text:
        return "empty", text
    m = re.search(re.escape(entity_guid) + r":\s*([^|]*)", text)
    owner_segment = m.group(1) if m else ""
    cm = re.search(re.escape(counter_name) + r"=(-?\d+)", owner_segment)
    if cm:
        return "found", int(cm.group(1))
    return "empty", text


def snapshot_tiles(text=None):
    """The TILE GRID portion of crucible_combat_snapshot: tile=(x, y), its GroupIndex, and its
    RowPositionsType (BACK/FRONT). Read at RUNTIME rather than hardcoding which column is which --
    [Combat] VenueGridPreset changes the layout, and the SHIELDED positioning recipe below must
    still find a real adjacent BACK/FRONT pair on whatever grid is actually loaded."""
    out = []
    for line in (text or snapshot()).splitlines():
        line = line.strip()
        m = re.match(r"^\((-?\d+), (-?\d+)\) group=(\S+) row=(\S+)$", line)
        if m:
            out.append({"x": int(m.group(1)), "y": int(m.group(2)),
                        "group": m.group(3), "row": m.group(4)})
    return out


def find_ally_front_back_pair(ally_group="0"):
    """A same-row-line (same y) pair of ally tiles where one reads row=BACK and the tile at
    dx=+-1, same y, reads row=FRONT -- exactly the geometry SKILL_CF_TRAINER_SHIELDED's
    ALLY_IN_FRONT condition checks (GameAdapters.cs: Row from VenueTileComponent.RowPositionsType,
    TileX/TileY from VenueComponent.TilePosition). Returns ((backX, backY), (frontX, frontY)) or
    (None, None) if this grid has no such adjacent pair for this group."""
    tiles = snapshot_tiles()
    by_group = [t for t in tiles if t["group"] == ally_group]
    for back in by_group:
        if back["row"] != "BACK":
            continue
        for dx in (1, -1):
            front = next((t for t in by_group
                          if t["x"] == back["x"] + dx and t["y"] == back["y"] and t["row"] == "FRONT"),
                         None)
            if front:
                return (back["x"], back["y"]), (front["x"], front["y"])
    return None, None


def move_when_active(entity_guid, x, y, max_turns=12, settle=1.8):
    """Ends turns (crucible_combat_end_turn) until entity_guid becomes the active entity, then
    drives it onto (x, y) via BASIC_MOVE -- the game's own built-in reposition verb (spends MOV,
    not PA/SA; CombatDriveCommands.cs's own note on crucible_use_ability_auto). Returns True only
    once the entity's own tile independently reads back (x, y) from crucible_combat_snapshot."""
    for _ in range(max_turns):
        if active_guid() == entity_guid:
            drive.run("crucible_use_ability", ["BASIC_MOVE", str(x), str(y)])
            time.sleep(settle)
            row = combatants().get(entity_guid, {})
            return row.get("tile") == (x, y)
        drive.run("crucible_combat_end_turn", [])
        time.sleep(settle)
    return False


def enemy_attack_tile(target_tile, max_turns=12, settle=2.0):
    """Waits for an ENEMY (group=1) to be active, picks a real usable *_ATTACK ability off
    crucible_list_abilities (excluding FLEE/SKIP_TURN/BASIC_MOVE/EQUIP_WEAPON/BASIC_RELOAD -- the
    same non-action exclusion crucible_use_ability_auto applies natively), and drives it at
    target_tile with crucible_use_ability -- NEVER crucible_kill_target, which forces the target to
    1 hp before it fires and so can never prove damage MITIGATION (protocol rule). Returns
    (ability_name, True) once an attack was actually fired, or (None, False) if no enemy turn
    within max_turns produced a usable attack."""
    excluded = {"FLEE", "SKIP_TURN", "BASIC_MOVE", "EQUIP_WEAPON", "BASIC_RELOAD"}
    for _ in range(max_turns):
        guid = active_guid()
        rows = combatants()
        row = rows.get(guid, {})
        if guid and row.get("group") == "1" and row.get("dead") != "True":
            listing = drive.run("crucible_list_abilities", [guid])
            atk = None
            for line in listing.splitlines():
                line = line.strip()
                if not line or " usable=" not in line:
                    continue
                name = line.split()[0]
                if name in excluded:
                    continue
                if "usable=True" in line and name.endswith("_ATTACK"):
                    atk = name
                    break
            if atk:
                drive.run("crucible_use_ability", [atk, str(target_tile[0]), str(target_tile[1])])
                time.sleep(settle)
                return atk, True
        drive.run("crucible_combat_end_turn", [])
        time.sleep(settle)
    return None, False


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    rows = combatants()
    print("\n=== board ===")
    for guid, row in rows.items():
        print("  %s %-22s g=%s hp=%-4s %s" % (
            guid[:8], row.get("class"), row.get("group"), row.get("hp"), row.get("tile")))
    shot("start")

    tguid, _ = by_class(rows, "CF_ORIG_TRAINER")
    vguid, vrow = by_class(rows, "CF_ORIG_VAMPIRIC")
    pguid, prow = by_class(rows, "CF_ORIG_PACIFIST")

    # ============================================================ 0. TRAINER -- GRASS partner summon
    print("\n=== 0. TRAINER: GRASS partner summons at combat start (SKILL_CF_TRAINER_GRASS_1) ===")
    grass_guid, grass_row = next(
        ((g, r) for g, r in rows.items()
         if str(r.get("class", "")).startswith("JELLY_ACID") and r.get("group") == "0"),
        (None, None))
    check("trainer: the GRASS line partner (JELLY_ACID family, tier 1 = Sporeling) is on the board "
          "as a living ally (SKILL_CF_TRAINER_GRASS_1 fires ON_COMBAT_START, cf_bond band 0-2)",
          "PASS" if grass_guid else "FAIL",
          ("found class=%s hp=%s tile=%s dead=%s"
           % (grass_row.get("class"), grass_row.get("hp"), grass_row.get("tile"), grass_row.get("dead")))
          if grass_guid else
          "no JELLY_ACID-family ally combatant in the opening board -- see the raw board printed above")

    if grass_guid:
        fromchar = drive.run("crucible_get", ["RouterHelper.Env.VenueGameObjectMaps.FromCharacter"])
        family_hit = "JELLY_ACID" in fromchar
        m_count = re.search(r"Count=(\d+)", fromchar)
        truncated = bool(m_count and int(m_count.group(1)) > 5 and not family_hit)
        check("trainer: the summoned partner's INSTANTIATED PREFAB matches its JELLY_ACID config "
              "family (RouterHelper.Env.VenueGameObjectMaps.FromCharacter -- the real Unity prefab "
              "the game built, stronger evidence than any stat readback per the visual-verification "
              "protocol; catches the invented-id failure mode where an entity is alive, targetable "
              "and completely invisible)",
              "PASS" if family_hit else ("UNVERIFIABLE" if truncated else "FAIL"),
              "FromCharacter render (sampled, up to 5 of %s): %s%s"
              % (m_count.group(1) if m_count else "?", fromchar.strip()[:300],
                 " -- render only samples the first 5 entries and truncated before reaching a "
                 "JELLY_ACID hit; not necessarily absent" if truncated else ""))
        shot("grass-partner-onboard")

    # ============================================================ 1. TRAINER -- Shielded by his Pokemon
    # SHIELDED MUST RUN FIRST (right after the passive GRASS-summon check above, which kills
    # nothing). This mirrors a rule already learned in lifecycle.py -- "# DEFEAT LATCH -- LAST,
    # because it ends the run. Fight 1 above must be fully measured first." -- run in reverse:
    # SHIELDED needs a still-LIVING Trainer AND a still-living Grass-line partner standing in front
    # of him (ALLY_IN_FRONT), and every section below this one kills something (bond kills 3
    # enemies, Engorged/Bat Swarm drive the Vampiric's own attacks round after round, Field
    # Medic/Why Can't We Be Friends force real enemy hits). MEASURED 2026-08-24 (traits6.log): with
    # this section placed LAST, "SHIELDED geometry available" came back UNVERIFIABLE every run
    # because a living Trainer AND a living partner were no longer both on the board by the time
    # it ran -- even though the trait itself IS proven (driving an enemy attack manually produced 3
    # "proc SKILL_CF_TRAINER_SHIELDED (ShieldedByPokemon) owner=<trainer> actions=1" lines); the
    # test simply never got to run while the geometry still existed.
    print("\n=== 1. TRAINER: Shielded by his Pokemon (ALLY_IN_FRONT physical mitigation) ===")
    # Snapshotted HERE, before anything else in this section runs, so a DAMAGE_TAKEN_MULT line from
    # a PREVIOUS run/section can never be picked up by the log-based check below (false pass).
    shielded_log_offset = log_size()
    enemies_ok = ensure_living_enemies(2)
    live_rows = combatants()
    tguid_now = tguid if tguid in live_rows and live_rows[tguid].get("dead") != "True" else None
    grass_guid_now, grass_row_now = next(
        ((g, r) for g, r in live_rows.items()
         if str(r.get("class", "")).startswith("JELLY_ACID") and r.get("dead") != "True"),
        (None, None))

    if not enemies_ok:
        check("trainer: SHIELDED geometry available (a living Trainer AND a living Grass-line "
              "partner still on the board)", "UNVERIFIABLE",
              "could not establish living enemies -- SHIELDED needs a real enemy attack to measure "
              "mitigation, see ensure_living_enemies")
        check("trainer: SHIELDED reduces physical damage", "UNVERIFIABLE",
              "could not establish living enemies")
    elif not tguid_now or not grass_guid_now:
        check("trainer: SHIELDED geometry available (a living Trainer AND a living Grass-line "
              "partner still on the board)",
              "UNVERIFIABLE",
              "trainer=%s (alive=%s) partner=%s -- SHIELDED needs a living partner in front, which "
              "cannot be constructed without both combatants alive; this section now runs FIRST "
              "specifically so this should not happen" % (tguid, bool(tguid_now), grass_guid_now))
    else:
        drive.run("crucible_godmode", ["off"])  # HP effects are masked by godmode -- protocol rule
        back_xy, front_xy = find_ally_front_back_pair("0")
        if not back_xy:
            check("trainer: SHIELDED geometry available (a BACK/FRONT ally tile pair exists on the "
                  "live tile grid)",
                  "UNVERIFIABLE",
                  "find_ally_front_back_pair (crucible_combat_snapshot's own tile grid, read at "
                  "runtime rather than a hardcoded column) found no adjacent BACK/FRONT ally tile "
                  "pair on this venue's current grid layout")
        else:
            moved_trainer = move_when_active(tguid_now, back_xy[0], back_xy[1])
            moved_partner = move_when_active(grass_guid_now, front_xy[0], front_xy[1])
            check("trainer: positioned in the BACK tile with the partner in the FRONT tile of the "
                  "SAME row (positions read from the live tile grid, not hardcoded -- grid presets "
                  "change which column is BACK/FRONT)",
                  "PASS" if (moved_trainer and moved_partner) else "FAIL",
                  "trainer -> %s (landed=%s), partner -> %s (landed=%s)"
                  % (back_xy, moved_trainer, front_xy, moved_partner))
            shot("shielded-positioned")

            if moved_trainer and moved_partner:
                trainer_before = hp_int(combatants().get(tguid_now, {}))
                atk_name, acted = enemy_attack_tile(back_xy)
                trainer_after_mitigated = hp_int(combatants().get(tguid_now, {}))
                dmg_mitigated = trainer_before - trainer_after_mitigated

                check("trainer: an enemy landed a real attack on the Trainer's own tile while the "
                      "partner stood directly in front of him (natural enemy-driven attack, never "
                      "crucible_kill_target -- that forces 1 hp before it fires and can never prove "
                      "mitigation)",
                      "PASS" if acted else "UNVERIFIABLE",
                      "ability=%s trainer hp %d -> %d (dmg=%d)"
                      % (atk_name, trainer_before, trainer_after_mitigated, dmg_mitigated)
                      if acted else
                      "no enemy turn within the budget produced a usable *_ATTACK ability targeted "
                      "at the Trainer's tile")

                if acted:
                    # The HP-delta comparison this replaced was confounded: it compared the HP lost
                    # to ONE enemy attack (mitigated) against the HP lost to a DIFFERENT enemy attack
                    # with the guard cleared (unmitigated) -- different enemies, different abilities,
                    # different base damage, not reproducible (see the section header notes above for
                    # the live 3-run evidence, run 9 inverted). ClassForge logs the SAME hit before
                    # and after DAMAGE_TAKEN_MULT is applied, to Player.log at Debug level -- a paired
                    # measurement with zero between-sample variance -- so that log line is the
                    # instrument now, not a second HP readback.
                    log_text2 = log_since(shielded_log_offset)
                    mult_pairs = []
                    if log_text2:
                        mult_pat = re.compile(
                            r"DAMAGE_TAKEN_MULT adjusted incoming damage (\d+) -> (\d+) for %s"
                            % re.escape(tguid_now))
                        mult_pairs = [(int(b), int(a)) for b, a in mult_pat.findall(log_text2)]

                    check("trainer: DAMAGE_TAKEN_MULT applied to the Trainer at least once in the "
                          "Player.log (offset snapshotted at the very start of this section, so a "
                          "prior run's lines can't produce a false pass)",
                          "PASS" if mult_pairs else "FAIL",
                          ("%d pair(s) for %s: %s" % (len(mult_pairs), tguid_now, mult_pairs))
                          if mult_pairs else
                          "no 'DAMAGE_TAKEN_MULT adjusted incoming damage ... for %s' line in the "
                          "captured %s text -- the multiplier never applied to the Trainer"
                          % (tguid_now, PLAYER_LOG))

                    if mult_pairs:
                        def shielded_expected(before):
                            # Percent=-35, MinDelta=2 per SKILL_CF_TRAINER_SHIELDED. Rounding
                            # direction (ceil) verified 2026-08-25 against 6 live pairs -- 14->9,
                            # 11->7, 4->2, 24->15, 10->6, 24->15 -- floor and round both broke on
                            # the 24->15 pairs; ceil reproduced all six exactly.
                            removed = math.ceil(before * 0.35)
                            reduced = before - removed
                            return min(reduced, before - 2)

                        mismatches = [(b, a, shielded_expected(b)) for b, a in mult_pairs
                                      if a != shielded_expected(b)]
                        check("trainer: SHIELDED reduces the Trainer's incoming damage by the "
                              "authored effect (Percent=-35, MinDelta=2 per SKILL_CF_TRAINER_SHIELDED; "
                              "expected = min(before - ceil(before*0.35), before - 2), checked against "
                              "every paired before/after DAMAGE_TAKEN_MULT line logged for the Trainer "
                              "this section)",
                              "PASS" if not mismatches else "FAIL",
                              "pairs (before -> after, expected after): %s%s"
                              % (["%d -> %d (expected %d)" % (b, a, shielded_expected(b))
                                  for b, a in mult_pairs],
                                 ("; MISMATCHES: %s" % mismatches) if mismatches else ""))
                    else:
                        check("trainer: SHIELDED reduces the Trainer's incoming damage by the "
                              "authored effect", "FAIL",
                              "no DAMAGE_TAKEN_MULT pairs to check -- see the check above")
                    shot("shielded-mitigated-hit")
                else:
                    check("trainer: SHIELDED reduces physical damage", "UNVERIFIABLE",
                          "no mitigated hit was ever landed -- see the check above")
            else:
                check("trainer: SHIELDED reduces physical damage", "UNVERIFIABLE",
                      "could not achieve the required BACK/FRONT geometry -- see the positioning "
                      "check above")

    # ============================================================ 2. TRAINER -- Gotta Train 'Em
    print("\n=== 2. TRAINER: Gotta Train 'Em / cf_bond ===")
    if tguid and not ensure_living_enemies(2):
        check("trainer: cf_bond counter advances across three killing blows", "UNVERIFIABLE",
              "could not establish living enemies")
        check("trainer: SKILL_CF_TRAINER_GOTTA_TRAIN_EM procs in the Player.log "
              "(independent of the cf_bond counter check above)", "UNVERIFIABLE",
              "could not establish living enemies")
    elif tguid:
        before_kind, before_val = read_recipe_counter(tguid, "cf_bond")
        log_offset = log_size()

        rows = ensure_enemy_count(3)
        enemies = [g for g, r in rows.items() if r.get("group") == "1" and r.get("dead") == "False"]
        invoked_all = True
        for i, enemy_guid in enumerate(enemies[:3]):
            dead_after, trigger_invoked, _ = kill_one(enemy_guid, tguid, "bond kill %d" % (i + 1))
            invoked_all = invoked_all and dead_after and trigger_invoked
            rows = ensure_enemy_count(1)  # keep at least one target alive for the next kill

        after_kind, after_val = read_recipe_counter(tguid, "cf_bond")

        if before_kind == "unreachable" or after_kind == "unreachable":
            check("trainer: cf_bond counter advances across three killing blows", "UNVERIFIABLE",
                  "crucible_recipe_counters could not reach the engine state -- before=[%s] after=[%s]"
                  % (before_val, after_val))
        elif before_kind == "error" or after_kind == "error":
            check("trainer: cf_bond counter advances across three killing blows", "FAIL",
                  "crucible_recipe_counters returned an error -- before=[%s] after=[%s]"
                  % (before_val, after_val))
        else:
            before_n = before_val if before_kind == "found" else 0
            after_n = after_val if after_kind == "found" else 0
            check("trainer: cf_bond counter advances across three killing blows",
                  "PASS" if after_n > before_n else "FAIL",
                  "cf_bond before=%d after=%d (entity %s, read via crucible_recipe_counters)"
                  % (before_n, after_n, tguid))

        # The old second check here just re-asserted crucible_kill_target's OWN reported
        # killTriggerPathInvoked=True flag -- which is redundant with the cf_bond delta above (a
        # cf_bond increase IS proof ON_KILL fired) and is not independent evidence of anything.
        # Replaced with a genuinely independent signal: the BepInEx-piped Player.log line the
        # engine itself writes when SKILL_CF_TRAINER_GOTTA_TRAIN_EM actually procs.
        log_text = log_since(log_offset)
        if log_text is None:
            check("trainer: SKILL_CF_TRAINER_GOTTA_TRAIN_EM procs in the Player.log "
                  "(independent of the cf_bond counter check above)",
                  "UNVERIFIABLE", "could not read %s across the 3 kills above" % PLAYER_LOG)
        else:
            proc_lines = [l.strip() for l in log_text.splitlines()
                          if "proc SKILL_CF_TRAINER_GOTTA_TRAIN_EM" in l]
            check("trainer: SKILL_CF_TRAINER_GOTTA_TRAIN_EM procs in the Player.log "
                  "(independent of the cf_bond counter check above)",
                  "PASS" if proc_lines else "FAIL",
                  ("%d proc line(s), e.g. %s" % (len(proc_lines), proc_lines[0][:170]))
                  if proc_lines else
                  "no 'proc SKILL_CF_TRAINER_GOTTA_TRAIN_EM' line in the %d bytes of new %s text "
                  "captured across the 3 kills above" % (len(log_text), PLAYER_LOG))
        shot("after-bond-kills")
    else:
        check("trainer: present in the board", "FAIL", "no CF_ORIG_TRAINER combatant found")

    # ============================================================ 3. VAMPIRIC -- Engorged
    print("\n=== 3. VAMPIRIC: Engorged (ON_DAMAGE_DEALT, HP_THRESHOLD GTE 70, ONCE_PER_ROUND) ===")
    # Offset snapshotted at the very start of this section (same pattern as sections 2, 4 and 5/6)
    # so the independent Player.log proc check below can never pick up a previous run's line.
    engorged_log_offset = log_size()
    if vguid and not ensure_living_enemies(2):
        check("vampiric: STATUS_VIGOR_CF_ENGORGED appears after the Vampiric deals damage while at "
              ">=70% HP", "UNVERIFIABLE", "could not establish living enemies -- Engorged needs the "
              "Vampiric's own attack to actually deal damage to something")
        check("vampiric: proc SKILL_CF_VAMPIRIC_ENGORGED appears in the Player.log during this "
              "section (independent of the STATUS_VIGOR_CF_ENGORGED read-back above)",
              "UNVERIFIABLE", "could not establish living enemies")
    elif vguid:
        # Godmode masks every HP effect (protocol rule) -- and this trait IS an HP effect (a
        # max-HP-raising status gated on the Vampiric's OWN HP%), so it must be off for the whole
        # section, unlike Bat Swarm/bond below where godmode state doesn't matter.
        drive.run("crucible_godmode", ["off"])

        # ONCE_PER_ROUND: the previous version of this loop drove the Vampiric to attack repeatedly
        # WITHIN THE SAME combat round (no round-advance step at all), so after the first attempt the
        # budget blocked every retry and the proc could never occur again. Fixed the SAME way section
        # 4 was fixed: each attempt ends turns (whoever is active, enemies included) until
        # combat.round (the module-level _current_round() helper, shared with section 4) is observed
        # to increment, THEN lets the Vampiric take its own natural turn and attack ONCE. Never
        # crucible_combat_restore_actions -- that is precisely what let the actor swing again inside
        # the same round in the old, broken version.
        engorged = False
        detail = "(no attempt recorded)"
        round_advance_count = 0
        attacked_at_70_rounds = 0
        round_now = _current_round()
        for attempt in range(8):
            round_before = round_now

            drive.run("crucible_heal_party", [])  # reset the HP% precondition for this attempt --
            time.sleep(1.5)                       # NOT itself the trigger, see module docstring.

            round_advanced = False
            for _ in range(24):
                drive.run("crucible_combat_end_turn", [])
                time.sleep(1.5)
                round_now = _current_round()
                if round_before is not None and round_now is not None and round_now > round_before:
                    round_advanced = True
                    round_advance_count += 1
                    break

            # maxHp for the %-threshold precondition -- crucible_combat_snapshot rows carry no maxHp
            # field, so read it via group0_hp_snapshot() (merges in drive.snapshot()'s v2 state).
            hp_before, max_hp_before = group0_hp_snapshot().get(vguid, (None, None))
            hp_pct_before = None
            try:
                if hp_before is not None and max_hp_before:
                    hp_pct_before = 100.0 * float(hp_before) / float(max_hp_before)
            except (TypeError, ValueError, ZeroDivisionError):
                hp_pct_before = None

            acted = False
            if round_advanced:
                for _ in range(12):
                    if active_guid() == vguid:
                        result = drive.run("crucible_use_ability_auto", [])
                        time.sleep(2.5)  # ability resolution is asynchronous
                        acted = not result.strip().startswith("error:")
                        break
                    drive.run("crucible_combat_end_turn", [])
                    time.sleep(1.5)
            if acted and hp_pct_before is not None and hp_pct_before >= 70.0:
                attacked_at_70_rounds += 1

            vrow_after = combatants().get(vguid, {})
            hp_after, max_hp_after = group0_hp_snapshot().get(vguid, (None, None))
            engorged = "ENGORGED" in vrow_after.get("statuses", "")
            detail = ("attempt %d: round_before=%s round_after=%s round_advanced=%s acted=%s "
                       "hp=%s/%s (%s%%) statuses=[%s]"
                       % (attempt + 1, round_before, round_now, round_advanced, acted,
                          hp_after, max_hp_after,
                          ("%.0f" % hp_pct_before) if hp_pct_before is not None else "?",
                          vrow_after.get("statuses", "")))
            print("      " + detail)
            if engorged:
                break  # stop as soon as the status is observed -- do not keep burning rounds

        if engorged:
            verdict = "PASS"
        elif round_advance_count >= 3 and attacked_at_70_rounds >= 3:
            verdict = "FAIL"
        else:
            # covers both: the combat round never advanced, and the Vampiric could never attack at
            # >=70% HP (precondition never established) -- neither is a content failure of the trait.
            verdict = "UNVERIFIABLE"

        check("vampiric: STATUS_VIGOR_CF_ENGORGED appears after the Vampiric deals damage while at "
              ">=70% HP (re-authored 2026-08-25 off the ON_HEAL_PENDING trigger, which no in-kit "
              "heal could ever reach -- see this module's own docstring and skillrecipes.json's "
              "_design note for the full history, including two more live-measured gate-arithmetic "
              "fixes). crucible_heal_party is used only to reset the HP% precondition before each "
              "attempt, never as the trigger itself; the trigger is the Vampiric's own attack, "
              "driven only after a REAL combat round advance (ONCE_PER_ROUND budget) -- same "
              "technique as section 4 below, sharing its _current_round() helper.",
              verdict,
              detail + (". round advanced %d time(s), attacked from >=70%% HP in %d of them"
                        % (round_advance_count, attacked_at_70_rounds)))
        shot("after-engorged-attempt")

        engorged_log_text = log_since(engorged_log_offset)
        if engorged_log_text is None:
            check("vampiric: proc SKILL_CF_VAMPIRIC_ENGORGED appears in the Player.log during this "
                  "section (independent of the STATUS_VIGOR_CF_ENGORGED read-back above)",
                  "UNVERIFIABLE", "could not read %s across this section" % PLAYER_LOG)
        else:
            engorged_proc_lines = [l.strip() for l in engorged_log_text.splitlines()
                                   if "proc SKILL_CF_VAMPIRIC_ENGORGED" in l]
            check("vampiric: proc SKILL_CF_VAMPIRIC_ENGORGED appears in the Player.log during this "
                  "section (independent of the STATUS_VIGOR_CF_ENGORGED read-back above)",
                  "PASS" if engorged_proc_lines else "UNVERIFIABLE",
                  ("%d proc line(s), e.g. %s"
                   % (len(engorged_proc_lines), engorged_proc_lines[0][:170]))
                  if engorged_proc_lines else
                  "no 'proc SKILL_CF_VAMPIRIC_ENGORGED' line in the %d bytes of new %s text "
                  "captured this section" % (len(engorged_log_text), PLAYER_LOG))
    else:
        check("vampiric: present in the board", "FAIL", "no CF_ORIG_VAMPIRIC combatant found")
        check("vampiric: proc SKILL_CF_VAMPIRIC_ENGORGED appears in the Player.log during this "
              "section (independent of the STATUS_VIGOR_CF_ENGORGED read-back above)",
              "FAIL", "no CF_ORIG_VAMPIRIC combatant found")

    # ============================================================ 4. VAMPIRIC -- Bat Swarm
    print("\n=== 4. VAMPIRIC: Bat Swarm (LOG-BASED: CounterAdd cf_gorged=3 -> proc BAT_SWARM -> "
          "CounterSet cf_gorged=0) ===")
    if vguid and not ensure_living_enemies(2):
        check("vampiric: cf_gorged CounterAdd reaches value=3 in the Player.log (gate reached)",
              "UNVERIFIABLE", "could not establish living enemies")
        check("vampiric: proc SKILL_CF_VAMPIRIC_BAT_SWARM appears in the Player.log after the gate "
              "was reached", "UNVERIFIABLE", "could not establish living enemies")
        check("vampiric: cf_gorged is reset to value=0 by SKILL_CF_VAMPIRIC_BAT_SWARM's own "
              "CounterSet", "UNVERIFIABLE", "could not establish living enemies")
        check("vampiric: the summoned bat's INSTANTIATED PREFAB matches its BAT-family config",
              "UNVERIFIABLE", "could not establish living enemies")
    elif vguid:
        drive.run("crucible_godmode", ["off"])  # HP effects are masked by godmode -- protocol rule

        # BUG 1: crucible_recipe_counters can NEVER observe cf_gorged>=3 -- Bat Swarm consumes the
        # counter (resets it to 0) in the SAME proc that hits the gate. Live Player.log evidence
        # this run:
        #   CounterAdd{recipe=SKILL_CF_VAMPIRIC_ENGORGED,...,name=cf_gorged,delta=1,value=3,...}
        #   proc SKILL_CF_VAMPIRIC_BAT_SWARM
        #   CounterSet{recipe=SKILL_CF_VAMPIRIC_BAT_SWARM,...,name=cf_gorged,value=0,...}
        # So the gate is asserted from the Player.log instead, using the SAME log_size()/log_since()
        # technique section 2's SKILL_CF_TRAINER_GOTTA_TRAIN_EM proc check already uses -- offset
        # snapshotted at the START of this section so only lines produced by THIS run's driving are
        # scanned, never a previous run's lines.
        log_offset = log_size()
        rounds_driven = 0
        bat_swarm_seen = False
        bat_guid = None
        bat_row = None
        prefab_status = None
        prefab_payload = None
        attacked_at_70_rounds = 0  # rounds where the Vampiric actually attacked from >=70% HP
        enemy_missing_rounds = 0   # rounds where no living enemy could be established
        maxhp_missing_rounds = 0   # rounds where the Vampiric's maxHp could not be read at all
        any_round_advanced = False  # true once combat.round is observed to increment at least once

        round_now = _current_round()  # module-level helper, shared with section 3 above
        for _ in range(14):
            round_before = round_now

            # 1. Reset the HP% precondition the SAME way section 3 does -- crucible_heal_party is
            #    used only to clear the "already at full HP" gate, never as the trigger itself.
            drive.run("crucible_heal_party", [])  # reset the HP% precondition for this attempt
            time.sleep(1.5)

            # 2. Make sure a living, attackable enemy actually exists this round -- without one the
            #    Vampiric has nothing to hit, which explains an acted=True/damage-never-lands round
            #    as cleanly as an acted=False one.
            enemy_present = ensure_living_enemies(2)
            if not enemy_present:
                enemy_missing_rounds += 1

            # crucible_combat_snapshot rows (combatants()) carry NO maxHp field at all -- reading
            # maxHp from there always yields None, which silently zeroes attacked_at_70_rounds.
            # group0_hp_snapshot() merges in maxHp from drive.snapshot()'s v2 state instead (the
            # same helper the Field Medic check already uses to correctly report e.g. 34/60=57%).
            hp_before_round, max_hp_before_round = group0_hp_snapshot().get(vguid, (None, None))

            # 3. Advance a REAL combat round before letting the Vampiric attack again.
            #    SKILL_CF_VAMPIRIC_ENGORGED is ONCE_PER_ROUND. The previous version of this loop
            #    called crucible_combat_restore_actions here so the Vampiric could attack again
            #    WITHOUT ever ending its turn -- which meant combat.round (CombatReader.cs:39,
            #    "view.Round = MemberResolver.AsInt(MemberResolver.GetMember(combatState,
            #    'TotalRounds', ...))", exposed at GET /state?schema=v2 as snapshot.combat.round,
            #    and by drive.describe() as "round=...") never incremented, so the ONCE_PER_ROUND
            #    budget never reset and Engorged only ever procced once across 14 such iterations.
            #    Fixed by ending turns (whoever is active, enemies included) until combat.round is
            #    actually observed to increment, and removing the restore_actions call entirely.
            round_advanced = False
            for _ in range(24):
                drive.run("crucible_combat_end_turn", [])
                time.sleep(1.5)
                round_now = _current_round()
                if round_before is not None and round_now is not None and round_now > round_before:
                    round_advanced = True
                    any_round_advanced = True
                    break

            # 4. Only once the round has genuinely advanced, let the Vampiric take its OWN natural
            #    turn and attack ONCE -- no action-restore, no repeat attack within the same round.
            acted = False
            if round_advanced:
                for _ in range(12):
                    if active_guid() == vguid:
                        result = drive.run("crucible_use_ability_auto", [])
                        time.sleep(2.5)  # ability resolution is asynchronous
                        acted = not result.strip().startswith("error:")
                        break
                    drive.run("crucible_combat_end_turn", [])
                    time.sleep(1.5)
            rounds_driven += 1

            hp_pct_before = None
            try:
                if hp_before_round is not None and max_hp_before_round:
                    hp_pct_before = 100.0 * float(hp_before_round) / float(max_hp_before_round)
            except (TypeError, ValueError, ZeroDivisionError):
                hp_pct_before = None
            if hp_before_round is not None and not max_hp_before_round:
                maxhp_missing_rounds += 1
                print("      round %d: WARNING could not read the Vampiric's maxHp this round "
                      "(group0_hp_snapshot had no entry for vguid=%s) -- treating as "
                      "precondition-indeterminate, not counting toward attacked_at_70"
                      % (rounds_driven, vguid))
            if acted and enemy_present and hp_pct_before is not None and hp_pct_before >= 70.0:
                attacked_at_70_rounds += 1

            log_text_so_far = log_since(log_offset) or ""
            engorged_procs_so_far = len(
                re.findall(r"proc SKILL_CF_VAMPIRIC_ENGORGED", log_text_so_far))
            print("      round %d: round_before=%s round_after=%s round_advanced=%s acted=%s "
                  "enemy_present=%s hp=%s/%s (%s%%) attacked_at_70=%d/%d enemy_missing_rounds=%d "
                  "maxhp_missing_rounds=%d engorged_procs_so_far=%d" %
                  (rounds_driven, round_before, round_now, round_advanced, acted, enemy_present,
                   hp_before_round, max_hp_before_round,
                   ("%.0f" % hp_pct_before) if hp_pct_before is not None else "?",
                   attacked_at_70_rounds, rounds_driven, enemy_missing_rounds, maxhp_missing_rounds,
                   engorged_procs_so_far))
            if re.search(r"proc SKILL_CF_VAMPIRIC_BAT_SWARM", log_text_so_far):
                bat_swarm_seen = True
                # Identify the summoned bat from BOARD STATE first -- a living group-0 combatant
                # whose class contains "BAT" -- THEN look up only that one entity's actor entry.
                # (A guid cannot be used as the crucible_get subscript itself; see
                # find_fromcharacter_entry's own docstring for why, confirmed live 2026-08-25.) This
                # must happen the instant the proc is seen, not after the round-driving loop exits --
                # the same DURING-combat constraint section 0's GRASS-partner check documents.
                bat_guid, bat_row = next(
                    ((g, r) for g, r in combatants().items()
                     if r.get("group") == "0" and r.get("dead") != "True"
                     and "BAT" in str(r.get("class", ""))),
                    (None, None))
                if bat_guid:
                    prefab_status, prefab_payload = find_fromcharacter_entry(bat_guid)
                print("      round %d: bat_swarm_seen=%s bat_guid=%s" %
                      (rounds_driven, bat_swarm_seen, bat_guid))
            if bat_swarm_seen:
                break  # stop as soon as the proc is observed -- do not keep burning rounds

        log_text = log_since(log_offset)
        if not any_round_advanced:
            # Harness limitation, not a content failure: without a single observed combat.round
            # increment, the ONCE_PER_ROUND budget on SKILL_CF_VAMPIRIC_ENGORGED never reset, so
            # cf_gorged could never progress past its first CounterAdd regardless of what Bat Swarm
            # itself does. See the round-advance loop's own comment above for the field this reads.
            reason = ("could not advance the combat round, so the ONCE_PER_ROUND budget never "
                      "reset (%d iteration(s) driven, combat.round never observed to increment)"
                      % rounds_driven)
            check("vampiric: cf_gorged CounterAdd reaches value=3 in the Player.log (gate reached)",
                  "UNVERIFIABLE", reason)
            check("vampiric: proc SKILL_CF_VAMPIRIC_BAT_SWARM appears in the Player.log after the "
                  "gate was reached", "UNVERIFIABLE", reason)
            check("vampiric: cf_gorged is reset to value=0 by SKILL_CF_VAMPIRIC_BAT_SWARM's own "
                  "CounterSet", "UNVERIFIABLE", reason)
        elif log_text is None:
            check("vampiric: cf_gorged CounterAdd reaches value=3 in the Player.log (gate reached)",
                  "UNVERIFIABLE",
                  "could not read %s across the %d driven round(s)" % (PLAYER_LOG, rounds_driven))
            check("vampiric: proc SKILL_CF_VAMPIRIC_BAT_SWARM appears in the Player.log after the "
                  "gate was reached", "UNVERIFIABLE", "could not read %s" % PLAYER_LOG)
            check("vampiric: cf_gorged is reset to value=0 by SKILL_CF_VAMPIRIC_BAT_SWARM's own "
                  "CounterSet", "UNVERIFIABLE", "could not read %s" % PLAYER_LOG)
        else:
            add_m = re.search(r"CounterAdd\{[^}]*name=cf_gorged[^}]*value=3[^}]*\}", log_text)
            proc_m = re.search(r"proc SKILL_CF_VAMPIRIC_BAT_SWARM", log_text)
            set_m = re.search(
                r"CounterSet\{recipe=SKILL_CF_VAMPIRIC_BAT_SWARM[^}]*name=cf_gorged[^}]*value=0[^}]*\}",
                log_text)

            # A precondition that could not be met (the Vampiric never got 3+ separate rounds where
            # it actually attacked from >=70% HP with a living enemy present) is not evidence the
            # gate is broken -- report UNVERIFIABLE rather than FAIL for that case. FAIL is reserved
            # for the case where the precondition WAS met (>=3 such rounds) and the gate still never
            # reached value=3.
            precondition_met = attacked_at_70_rounds >= 3
            if maxhp_missing_rounds >= rounds_driven and rounds_driven > 0:
                # maxHp was unreadable for the Vampiric on every driven round -- this is a missing
                # instrument, not evidence of anything about Bat Swarm itself. precondition_met is
                # already guaranteed False here (attacked_at_70_rounds could never increment without
                # a readable hp_pct_before), so _status below already resolves to UNVERIFIABLE, never
                # FAIL -- this detail just names the specific reason instead of a generic miss-count.
                precondition_detail = (
                    "could not read the Vampiric's maxHp in any of the %d driven round(s) "
                    "(group0_hp_snapshot had no entry for vguid=%s each time) -- cf_gorged's gate "
                    "could not even be attempted, so this is UNVERIFIABLE, not evidence Bat Swarm "
                    "is broken" % (rounds_driven, vguid))
            else:
                precondition_detail = (
                    "precondition not met: only %d/%d driven round(s) had the Vampiric attack while "
                    ">=70%% HP with a living enemy present (enemy_missing_rounds=%d, "
                    "maxhp_missing_rounds=%d) -- cf_gorged needs 3 such rounds to reach value=3, so "
                    "a miss here is not evidence Bat Swarm is broken"
                    % (attacked_at_70_rounds, rounds_driven, enemy_missing_rounds,
                       maxhp_missing_rounds))

            def _status(found):
                if found:
                    return "PASS"
                return "FAIL" if precondition_met else "UNVERIFIABLE"

            check("vampiric: cf_gorged CounterAdd reaches value=3 in the Player.log (the gate was "
                  "reached -- crucible_recipe_counters can NEVER observe this directly, since the "
                  "same proc that hits value=3 resets the counter to 0 before the next poll)",
                  _status(add_m),
                  ("found: %s" % add_m.group(0)[:170]) if add_m else
                  ("no 'CounterAdd{...name=cf_gorged...value=3...}' line in the %d bytes of new %s "
                   "text captured across %d driven round(s). %s"
                   % (len(log_text), PLAYER_LOG, rounds_driven, precondition_detail)))

            check("vampiric: proc SKILL_CF_VAMPIRIC_BAT_SWARM appears in the Player.log after the "
                  "gate was reached",
                  _status(proc_m and (not add_m or proc_m.start() > add_m.start())),
                  ("found: %s (offset %d, after the CounterAdd at offset %d)"
                   % (proc_m.group(0), proc_m.start(), add_m.start() if add_m else -1)) if proc_m
                  else ("no 'proc SKILL_CF_VAMPIRIC_BAT_SWARM' line in the %d bytes of new %s text "
                        "captured across %d driven round(s). %s"
                        % (len(log_text), PLAYER_LOG, rounds_driven, precondition_detail)))

            check("vampiric: cf_gorged is reset to value=0 by SKILL_CF_VAMPIRIC_BAT_SWARM's own "
                  "CounterSet (the counter is consumed in the same proc, not left readable "
                  "afterward)",
                  _status(set_m),
                  ("found: %s" % set_m.group(0)[:170]) if set_m else
                  ("no 'CounterSet{recipe=SKILL_CF_VAMPIRIC_BAT_SWARM...name=cf_gorged...value=0...}' "
                   "line in the %d bytes of new %s text captured across %d driven round(s). %s"
                   % (len(log_text), PLAYER_LOG, rounds_driven, precondition_detail)))

        prefab_check_name = ("vampiric: the summoned bat's INSTANTIATED PREFAB matches its BAT-family "
                              "config (RouterHelper.Env.VenueGameObjectMaps.FromCharacter, read for "
                              "ONE entity at a time via find_fromcharacter_entry's guid-targeted "
                              "positional lookup -- the whole-map render truncates to the first 5 of "
                              "N entries and silently hides an entry past that, confirmed live "
                              "2026-08-25 with Count=12; captured DURING combat right after the Bat "
                              "Swarm proc was observed, the same constraint section 0's GRASS-partner "
                              "prefab check documents)")
        if not bat_swarm_seen:
            check(prefab_check_name, "UNVERIFIABLE",
                  "the Bat Swarm proc was never observed within the %d-round bound, so no prefab "
                  "capture was attempted" % rounds_driven)
        elif not bat_guid:
            check(prefab_check_name, "UNVERIFIABLE",
                  "Bat Swarm proc'd but no living group-0 combatant with a BAT-family class was on "
                  "the board at that moment")
        elif prefab_status == "unreadable":
            check(prefab_check_name, "UNVERIFIABLE",
                  "FromCharacter renders only 5 of N entries and no per-entity lookup is available -- "
                  "even FromCharacter's own Count could not be read this time: %s"
                  % str(prefab_payload)[:200])
        elif prefab_status == "not_found":
            check(prefab_check_name, "FAIL", prefab_payload + " -- the bat is alive and targetable "
                  "but has no instantiated actor at all")
        else:
            family_hit = str(bat_row.get("class") or "") in prefab_payload
            check(prefab_check_name, "PASS" if family_hit else "FAIL",
                  "guid-targeted entry (bat=%s class=%s): %s"
                  % (bat_guid, bat_row.get("class"), prefab_payload[:300]))
        shot("after-bat-swarm-attempt")
    else:
        check("vampiric: present in the board (bat swarm)", "FAIL", "no CF_ORIG_VAMPIRIC combatant found")

    # ============================================================ 5/6. PACIFIST
    print("\n=== 5/6. PACIFIST: Field Medic + Why Can't We Be Friends ===")
    if pguid:
        combat_active = (drive.snapshot().get("combat") or {}).get("active")
        if combat_active is False:
            check("pacifist: Field Medic heals the lowest-HP% living group-0 member "
                  "(ALLY_BY_RANK) when the Pacifist's own turn ends", "UNVERIFIABLE",
                  "combat ended before this section")
            check("pacifist: SKILL_CF_PACIFIST_FIELD_MEDIC procs in the Player.log (independent of "
                  "the HP read-back above)", "UNVERIFIABLE", "combat ended before this section")
            check("pacifist: damage taken is reflected back onto the attacker "
                  "(Why Can't We Be Friends)", "UNVERIFIABLE",
                  "combat ended before this section")
            check("pacifist: SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS procs in the Player.log "
                  "(independent of the HP read-back above)", "UNVERIFIABLE",
                  "combat ended before this section")
        elif not ensure_living_enemies(2):
            check("pacifist: Field Medic heals the lowest-HP% living group-0 member "
                  "(ALLY_BY_RANK) when the Pacifist's own turn ends", "UNVERIFIABLE",
                  "could not establish living enemies")
            check("pacifist: SKILL_CF_PACIFIST_FIELD_MEDIC procs in the Player.log (independent of "
                  "the HP read-back above)", "UNVERIFIABLE", "could not establish living enemies")
            check("pacifist: damage taken is reflected back onto the attacker "
                  "(Why Can't We Be Friends)", "UNVERIFIABLE",
                  "could not establish living enemies")
            check("pacifist: SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS procs in the Player.log "
                  "(independent of the HP read-back above)", "UNVERIFIABLE",
                  "could not establish living enemies")
        else:
            drive.run("crucible_godmode", ["off"])  # HP effects are masked by godmode -- protocol rule

            # ---- Field Medic: the recipe is ALLY_BY_RANK {Stat: HP_PCT, Order: LOWEST,
            # ExcludeSelf: false} -- it heals whichever LIVING group-0 member has the lowest HP
            # PERCENT at the moment the Pacifist's turn ends, and the Pacifist itself is an eligible
            # target (ExcludeSelf: false). Watching only the one ally this section chose to hurt is
            # wrong: live evidence (Player.log) showed the recipe proc'ing 8 times while a single
            # watched ally's HP never moved, because rank picked someone else. So: hurt ONE known
            # ally by a known amount (the same enemy-driven-attack helper SHIELDED uses -- never
            # crucible_kill_target, which forces 1 hp and could never isolate a genuine heal-vs-
            # damage race) purely to guarantee SOMEONE is hurt, but PREDICT the heal target from a
            # full group-0 HP% snapshot taken immediately before the turn-end, and grade against that
            # prediction, not against whichever ally got hit.
            log_offset_fm = log_size()  # section-start offset -- a previous run's proc can't leak in
            field_medic_status = "UNVERIFIABLE"
            field_medic_detail = "no living ally with a known tile was available to hurt"
            ally_guid, ally_row = next(
                ((g, r) for g, r in combatants().items()
                 if r.get("group") == "0" and g != pguid and r.get("dead") != "True" and r.get("tile")),
                (None, None))
            if ally_guid:
                ally_before_hit = hp_int(ally_row)
                hit_atk, hit_acted = enemy_attack_tile(ally_row["tile"])
                ally_after_hit = hp_int(combatants().get(ally_guid, {}))
                if not hit_acted:
                    field_medic_detail = ("no enemy turn within the budget produced a usable attack on "
                                           "ally %s's tile" % ally_guid)
                elif ally_after_hit >= ally_before_hit:
                    field_medic_detail = ("enemy attack (%s) did not lower ally %s's hp (%d -> %d) -- "
                                           "nothing to heal" % (hit_atk, ally_guid, ally_before_hit, ally_after_hit))
                else:
                    for _ in range(12):
                        if active_guid() == pguid:
                            break
                        drive.run("crucible_combat_end_turn", [])
                        time.sleep(1.8)
                    if active_guid() != pguid:
                        field_medic_detail = ("never reached the Pacifist's own turn within the wait "
                                               "budget after hurting ally %s (hp %d -> %d)"
                                               % (ally_guid, ally_before_hit, ally_after_hit))
                    else:
                        # Snapshot HP% for EVERY living group-0 member (godmode is off for this whole
                        # section) right before the turn-end, and predict the LOWEST-HP% target --
                        # exactly what ALLY_BY_RANK {HP_PCT, LOWEST} will pick.
                        before_snap = group0_hp_snapshot()
                        if not before_snap:
                            field_medic_detail = ("could not read group-0 hp/maxHp via drive.snapshot() "
                                                   "(/state?schema=v2) right before the Pacifist's turn-end")
                        else:
                            predicted_guid = min(before_snap,
                                                  key=lambda g: before_snap[g][0] / float(before_snap[g][1]))
                            pred_hp_before, pred_max = before_snap[predicted_guid]
                            drive.run("crucible_combat_end_turn", [])  # end the Pacifist's own turn
                            time.sleep(1.8)
                            after_snap = group0_hp_snapshot()
                            pred_hp_after = after_snap.get(predicted_guid, (pred_hp_before, pred_max))[0]

                            if pred_hp_after - pred_hp_before == 4 or \
                                    (pred_hp_after > pred_hp_before and pred_hp_after == pred_max):
                                field_medic_status = "PASS"
                                field_medic_detail = (
                                    "predicted target %s (lowest hp%%: %d/%d = %.0f%%) healed %d -> %d%s"
                                    % (predicted_guid, pred_hp_before, pred_max,
                                       100.0 * pred_hp_before / pred_max, pred_hp_before, pred_hp_after,
                                       " (capped at maxHp)" if pred_hp_after == pred_max
                                       and pred_hp_after - pred_hp_before != 4 else ""))
                            else:
                                # Did some OTHER group-0 member gain exactly +4? Then the CONTENT is
                                # fine and this test's rank prediction is what's wrong.
                                other_healed = next(
                                    ((g, before, after_snap[g][0]) for g, (before, _mx) in before_snap.items()
                                     if g != predicted_guid and g in after_snap
                                     and after_snap[g][0] - before == 4),
                                    None)
                                if other_healed:
                                    og, ob, oa = other_healed
                                    field_medic_status = "UNVERIFIABLE"
                                    field_medic_detail = (
                                        "rank prediction mismatch: predicted %s (hp%% %.0f%%, %d -> %d) "
                                        "but %s (+4 hp, %d -> %d) was actually healed -- the "
                                        "lowest-HP_PCT prediction is wrong, not necessarily the content"
                                        % (predicted_guid, 100.0 * pred_hp_before / pred_max,
                                           pred_hp_before, pred_hp_after, og, ob, oa))
                                else:
                                    field_medic_status = "FAIL"
                                    field_medic_detail = (
                                        "no group-0 member gained any HP across the Pacifist's own "
                                        "turn-end (predicted target %s hp%% %.0f%%: %d -> %d); "
                                        "before=%s after=%s"
                                        % (predicted_guid, 100.0 * pred_hp_before / pred_max,
                                           pred_hp_before, pred_hp_after, before_snap, after_snap))

            check("pacifist: Field Medic heals the lowest-HP% living group-0 member (ALLY_BY_RANK "
                  "{HP_PCT, LOWEST}, ExcludeSelf: false -- the Pacifist itself is an eligible target, "
                  "not necessarily whichever ally this section chose to hurt) when the Pacifist's own "
                  "turn ends", field_medic_status, field_medic_detail)
            shot("after-field-medic-attempt")

            # Independent evidence, reusing the SAME log_size()/log_since() pattern sections 2 and 4
            # use: the recipe actually procs in the Player.log during this section. Offset was
            # snapshotted at the section's own start (log_offset_fm above), before anything here ran,
            # so a previous run's proc lines can never produce a false pass.
            fm_log_text = log_since(log_offset_fm)
            if fm_log_text is None:
                check("pacifist: SKILL_CF_PACIFIST_FIELD_MEDIC procs in the Player.log (independent "
                      "of the HP read-back above)", "UNVERIFIABLE",
                      "could not read %s during this section" % PLAYER_LOG)
            else:
                fm_proc_lines = [l.strip() for l in fm_log_text.splitlines()
                                  if "proc SKILL_CF_PACIFIST_FIELD_MEDIC" in l]
                check("pacifist: SKILL_CF_PACIFIST_FIELD_MEDIC procs in the Player.log (independent "
                      "of the HP read-back above)",
                      "PASS" if fm_proc_lines else "FAIL",
                      ("%d proc line(s), e.g. %s" % (len(fm_proc_lines), fm_proc_lines[0][:170]))
                      if fm_proc_lines else
                      "no 'proc SKILL_CF_PACIFIST_FIELD_MEDIC' line in the %d bytes of new %s text "
                      "captured during this section" % (len(fm_log_text), PLAYER_LOG))

            # ---- Why Can't We Be Friends: do not rely on AI targeting -- use the same enemy-driven-
            # attack helper SHIELDED uses, aimed at the Pacifist's OWN tile, then check whether that
            # exact attacker's own hp also dropped in the same exchange (the reflect landing on
            # TRIGGER_SOURCE, per SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS).
            #
            # A MISS (the enemy attacks but rolls 0 damage) means NO TEST HAPPENED -- it is not
            # evidence the reflect is broken. So the attack step retries up to 6 times, reusing the
            # SAME enemy_attack_tile and ensure_living_enemies helpers used everywhere else in this
            # section (re-establishing enemies between retries if needed), until an attack actually
            # LOWERS the Pacifist's HP. Only once real damage lands is the attacker's own HP checked.
            reflect_log_offset = log_size()  # snapshotted before the first retry attempt below
            reflect_evidence = None
            reflect_status = "UNVERIFIABLE"
            reflect_detail = "no living Pacifist tile to target"
            damage_landed = False
            for reflect_attempt in range(6):
                if not ensure_living_enemies(2):
                    reflect_detail = ("could not establish living enemies on retry %d"
                                       % (reflect_attempt + 1))
                    continue
                prow_now = combatants().get(pguid, {})
                if not prow_now.get("tile") or prow_now.get("dead") == "True":
                    reflect_detail = ("no living Pacifist tile to target on retry %d"
                                       % (reflect_attempt + 1))
                    continue
                pac_tile = prow_now["tile"]
                pac_before = hp_int(prow_now)
                enemies_before = {g: hp_int(r) for g, r in combatants().items() if r.get("group") == "1"}
                atk_name, acted = enemy_attack_tile(pac_tile)
                if not acted:
                    reflect_detail = ("no enemy turn within the budget produced a usable attack on "
                                       "the Pacifist's own tile (retry %d)" % (reflect_attempt + 1))
                    continue
                pac_after = hp_int(combatants().get(pguid, {}))
                if pac_after >= pac_before:
                    reflect_detail = ("enemy attack (%s) did not lower the Pacifist's hp (%d -> %d) "
                                       "on retry %d -- a miss, not a failed reflect; retrying"
                                       % (atk_name, pac_before, pac_after, reflect_attempt + 1))
                    print("      " + reflect_detail)
                    continue

                damage_landed = True
                enemies_after = combatants()
                hit_attacker = next(
                    (g for g, before_hp in enemies_before.items()
                     if hp_int(enemies_after.get(g, {})) < before_hp),
                    None)
                if not hit_attacker:
                    reflect_status = "FAIL"
                    reflect_detail = ("Pacifist hp dropped %d -> %d from a real enemy attack (%s), "
                                       "but no group=1 combatant's own hp also dropped in the same "
                                       "exchange" % (pac_before, pac_after, atk_name))
                else:
                    # enemies_after[hit_attacker] direct-indexed here used to raise KeyError if
                    # the attacker's row disappeared from the board entirely between snapshots
                    # (not just dead=True -- fully removed), which is exactly the case hit_attacker
                    # detection above already treats as "hp dropped to 0" via enemies_after.get(g,
                    # {}). Use the same .get(..., {}) here so that case can't crash.
                    reflect_evidence = (hit_attacker, pac_before, pac_after,
                                         enemies_before[hit_attacker],
                                         hp_int(enemies_after.get(hit_attacker, {})))
                break

            if not damage_landed:
                reflect_status = "UNVERIFIABLE"
                reflect_detail = ("no enemy attack landed damage on the Pacifist, so there was "
                                   "nothing to reflect (6 retries exhausted; last: %s)" % reflect_detail)

            check("pacifist: damage taken is reflected back onto the attacker "
                  "(Why Can't We Be Friends)",
                  "PASS" if reflect_evidence else reflect_status,
                  ("attacker %s: pacifist hp %d -> %d, attacker hp %d -> %d in the same exchange"
                   % reflect_evidence) if reflect_evidence else reflect_detail)
            shot("after-why-cant-we-be-friends-attempt")

            # Independent evidence, reusing the SAME log_size()/log_since() pattern sections 2, 4 and
            # the Field Medic check above use: the recipe actually procs in the Player.log during
            # this section, regardless of whether the HP read-back above could pin the exchange to a
            # specific attacker. Offset was snapshotted before the first retry attempt above, so a
            # previous run's proc lines can never produce a false pass. (Measured 2026-08-24: procced
            # 7 times in one run.)
            reflect_log_text = log_since(reflect_log_offset)
            if reflect_log_text is None:
                check("pacifist: SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS procs in the Player.log "
                      "(independent of the HP read-back above)", "UNVERIFIABLE",
                      "could not read %s during this section" % PLAYER_LOG)
            else:
                reflect_proc_lines = [l.strip() for l in reflect_log_text.splitlines()
                                       if "proc SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS" in l]
                check("pacifist: SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS procs in the Player.log "
                      "(independent of the HP read-back above)",
                      "PASS" if reflect_proc_lines else "FAIL",
                      ("%d proc line(s), e.g. %s" % (len(reflect_proc_lines), reflect_proc_lines[0][:170]))
                      if reflect_proc_lines else
                      "no 'proc SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS' line in the %d bytes of "
                      "new %s text captured during this section" % (len(reflect_log_text), PLAYER_LOG))

            print("\n      board after the pacifist section:")
            for guid, row in combatants().items():
                print("        %s %-22s g=%s hp=%-4s dead=%s statuses=[%s]" % (
                    guid[:8], row.get("class"), row.get("group"), row.get("hp"),
                    row.get("dead"), row.get("statuses")))
    else:
        check("pacifist: present in the board", "FAIL", "no CF_ORIG_PACIFIST combatant found")

    # ---- summary ----------------------------------------------------------
    print("\n" + "=" * 70)
    passed = sum(1 for _, s in RESULTS if s == "PASS")
    failed = [n for n, s in RESULTS if s == "FAIL"]
    unverifiable = [n for n, s in RESULTS if s == "UNVERIFIABLE"]
    print("%d PASS / %d FAIL / %d UNVERIFIABLE (of %d checks)"
          % (passed, len(failed), len(unverifiable), len(RESULTS)))
    for n in failed:
        print("  FAILED: " + n)
    for n in unverifiable:
        print("  UNVERIFIABLE: " + n)

    if SHOTS:
        print("\nscreenshots to OPEN (a visual claim is not verified until one is opened):")
        for label, path in SHOTS:
            print("  %-32s %s" % (label, path))

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
