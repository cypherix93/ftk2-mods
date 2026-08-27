#!/usr/bin/env python3
"""
Drive the running game over Crucible's RPC.

Encodes the sequences that are easy to get subtly wrong by hand:

  boot          wait until the plugin's main-thread pump is alive
  load          MAIN_MENU -> campaign -> load a run by id -> clear the post-load gate
  gate          clear a "Click to Continue" gate wherever one is showing
  state         print route / combat / party in one line
  exec          run any crucible_* command
  enter-combat  from anywhere on the overworld into a LIVE fight  (drive.enter_combat)
  exit-combat   end the fight and get back to the overworld       (drive.exit_combat)

`run()` NEVER returns "" for a command that did not execute -- it raises CommandError or
CommandDidNotRun instead. See COMMAND_ARITY for why that distinction had to exist.

Two facts this exists to encode, both measured 2026-08-23:
  * `_loadGameRun` needs ADVENTURE_SELECTION to exist. Called from MAIN_MENU it silently
    does nothing, so the campaign screen must be opened first.
  * A post-load `Click to Continue` gate (continue-label in LoadingUIDocument) blocks EVERY
    overworld interaction while it is up. Twenty-five consecutive end-turn calls reported
    "changed=False" purely because of it.

Usage:
    python FTK2.Crucible/tools/drive.py state
    python FTK2.Crucible/tools/drive.py load <runId>
    python FTK2.Crucible/tools/drive.py exec crucible_party_list
"""

import json
import re
import subprocess
import sys
import os
import shutil
import time
import urllib.error
import urllib.request

PORT = 8787
BASE = "http://127.0.0.1:%d" % PORT


def _post(path, payload, attempts=3):
    """
    Retries transient failures. The pump answers 503 while it is busy and 400 during the
    window where a command is not yet registered, and both are normal during boot -- a single
    attempt turns an ordinary race into a spurious hard failure mid-sequence.
    """
    body, error = _post_raw(path, payload, attempts)
    if body is None:
        raise RuntimeError("POST %s failed after %d attempts: %s" % (path, attempts, error))
    return body


def _post_raw(path, payload, attempts=3):
    """
    POST that hands the SERVER'S OWN error body back instead of throwing it away.

    urllib raises HTTPError for 4xx, and the old code caught it, retried, and then reported the
    bare exception. That discarded the plugin's `{"ok":false,"error":"..."}` payload, so a
    denied-not-allowlisted, an unknown_command and a command_threw all surfaced as the same
    opaque "HTTP Error 400". Returns (parsed_body_or_None, last_error).
    """
    last = None
    for attempt in range(attempts):
        request = urllib.request.Request(
            BASE + path,
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.loads(response.read().decode("utf-8")), None
        except urllib.error.HTTPError as error:
            last = error
            try:
                parsed = json.loads(error.read().decode("utf-8"))
            except Exception:
                parsed = None
            # A structured refusal is an ANSWER, not a transport failure: retrying it just
            # repeats the same refusal three times and then reports the wrong reason. Only the
            # genuinely transient shapes are worth another attempt.
            if isinstance(parsed, dict):
                reason = str(parsed.get("error") or "")
                transient = error.code == 503 or reason == "unknown_command"
                if not transient or attempt == attempts - 1:
                    return parsed, error
            time.sleep(2 + 2 * attempt)
        except (urllib.error.URLError, OSError) as error:
            last = error
            time.sleep(2 + 2 * attempt)
    return None, last


def _get(path, attempts=3):
    last = None
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(BASE + path, timeout=30) as response:
                return json.loads(response.read().decode("utf-8"))
        except (urllib.error.HTTPError, urllib.error.URLError, OSError) as error:
            last = error
            time.sleep(2 + 2 * attempt)
    raise RuntimeError("GET %s failed after %d attempts: %s" % (path, attempts, last))


class CommandError(RuntimeError):
    """The plugin REFUSED or FAILED the command: ok=false with a reason."""


class CommandDidNotRun(RuntimeError):
    """
    The plugin reported ok=true but the command never actually executed.

    See COMMAND_ARITY below for the mechanism. This is raised rather than returned as "" because
    "" is indistinguishable from a legitimate empty result, and every caller in this repo treats
    "" as a real answer -- which is how three separate verification attempts were lost to a
    command that had silently never run.
    """


# ---------------------------------------------------------------------------------------------
# COMMAND ARITY -- the fix for the "drive.run returns '' while the MCP wrapper works" bug.
#
# ROOT CAUSE, with evidence. The game's own dispatcher, CommandLineHelper.ExecuteCommand
# (.decompile-scratch/proj/CommandLineHelper.cs:85-319), marshals args positionally into the
# handler's parameters. When FEWER args are supplied than the handler declares, index i >=
# pArgs.Length falls into:
#
#     else if (parameters2[i].DefaultValue != null) { flag3 = false; }   // line 269-272
#     ...
#     if (!flag3) break;                                                 // line 277-280
#     ...
#     if (flag2) { ...Invoke... }                                        // line 290-317
#     Debug.LogError("Command not found '" + pName + "'");               // line 318
#
# ParameterInfo.DefaultValue for a plain `string` parameter with no default is DBNull.Value --
# NOT null -- so flag3 goes false, the loop breaks, flag2 stays false, and THE HANDLER IS NEVER
# INVOKED. ExecuteCommand returns void either way, so GameBridge.Exec (which only checks that the
# name is in the registry) reports success, RpcServer answers 200 ok=true, and because the handler
# never ran its LastResult is still the null RpcServer cleared before dispatch -- so the response
# carries no `result` field at all and the old `payload.get("result") or ""` handed back "".
#
# Evidence, all captured live on 2026-08-25/26:
#   Player.log       "Command not found 'crucible_map_encounters'", "...'crucible_move'",
#                    "...'crucible_loc'", "...'crucible_debug_spawn'", "...'crucible_interact'",
#                    "...'crucible_ui_dump'" -- every one of them a REGISTERED command.
#   crucible traces  {"args":[],"command":"crucible_map_encounters","durationMs":16}   no result
#                    {"args":["-"],...,"durationMs":89,"result":"mapId=STORY_1_1 ..."}
#                    {"args":["28","41"],"command":"crucible_move","durationMs":10}    no result
#                    {"args":["27","41","false","false","true"],...,"result":"goal=..."}
#                    {"args":["BANDIT_RANGED_01"],"command":"crucible_debug_spawn","durationMs":13}
#                    {"args":["enemies","BANDIT_RANGED_01"],...,"result":"kind=enemies ..."}
# The ~10-16ms duration is the signature of the no-invoke path; a real invoke costs 80-250ms.
#
# WHY THE MCP WRAPPERS "WORKED": they are not luckier, they are just complete. server.js always
# fills every slot -- crucible_map_encounters [args.filter || '-'], crucible_ui_dump
# ['-', 'button'] -- so they never hit the shortfall. drive.run(cmd, []) did.
#
# THE FIX, in two halves:
#   1. Pad. Trailing args the caller omitted are filled with ARG_ABSENT (see its own note for
#      why that is "" and not "-"), which reproduces each handler's documented default.
#   2. Detect. If a crucible_* command still comes back with NO result field, it did not run --
#      every Crucible handler assigns LastResult on every path including its catch -- so raise
#      CommandDidNotRun instead of returning "".
#
# Generated from the handler signatures in src/Crucible.Plugin/*.cs. Regenerate when a command's
# signature changes; an entry that is merely MISSING costs nothing but the padding.
COMMAND_ARITY = {
    "crucible_chaos_advance": 1,
    "crucible_chaos_freeze": 1,
    "crucible_chaos_state": 0,
    "crucible_class_config": 1,
    "crucible_combat_end_turn": 0,
    "crucible_combat_restore_actions": 1,
    "crucible_combat_snapshot": 0,
    "crucible_combat_spawn": 3,
    "crucible_combat_wipe_enemies": 2,
    "crucible_debug_spawn": 2,
    "crucible_dialogue_advance": 1,
    "crucible_dialogue_choose": 1,
    "crucible_diorama_list": 1,
    "crucible_encounter_leave": 0,
    "crucible_encounters_clear": 1,
    "crucible_end_phase": 1,
    "crucible_endadventure_watch": 2,
    "crucible_engage_encounter": 0,
    "crucible_equip": 2,
    "crucible_fixture_list": 0,
    "crucible_fixture_save": 1,
    "crucible_force_combat": 3,
    "crucible_force_encounter": 0,
    "crucible_get": 1,
    "crucible_give": 2,
    "crucible_give_item": 3,
    "crucible_godmode": 1,
    "crucible_heal_party": 0,
    "crucible_hex_info": 2,
    "crucible_input_background": 1,
    "crucible_input_devices": 0,
    "crucible_input_focus_gate": 1,
    "crucible_input_state": 0,
    "crucible_interact": 1,
    "crucible_invoke": 3,
    "crucible_key": 1,
    "crucible_kill_all": 0,
    "crucible_kill_target": 2,
    "crucible_list_abilities": 1,
    "crucible_list_targets": 2,
    "crucible_load_run": 1,
    "crucible_loc": 1,
    "crucible_map_encounters": 1,
    "crucible_mouse_click": 2,
    "crucible_move": 5,
    "crucible_overworld_end_turn": 0,
    "crucible_overworld_state": 0,
    "crucible_pad": 1,
    "crucible_pad_hold": 2,
    "crucible_pad_pair": 1,
    "crucible_pad_stick": 2,
    "crucible_party_abilities": 1,
    "crucible_party_gain_xp": 2,
    "crucible_party_list": 0,
    "crucible_party_set_class": 2,
    "crucible_party_set_hex": 2,
    "crucible_path_preview": 2,
    "crucible_pin_seed": 1,
    "crucible_quest_activate": 1,
    "crucible_quest_complete": 2,
    "crucible_quest_complete_objective": 2,
    "crucible_quest_remove": 1,
    "crucible_quest_state": 0,
    "crucible_recipe_counters": 1,
    "crucible_refresh_saves": 0,
    "crucible_reveal_map": 1,
    "crucible_run_status": 0,
    "crucible_set": 2,
    "crucible_set_diorama": 1,
    "crucible_set_level": 2,
    "crucible_set_stat_value": 3,
    "crucible_status_add": 3,
    "crucible_status_config": 1,
    "crucible_summary_dismiss": 0,
    "crucible_thing_config": 1,
    "crucible_time_advance": 1,
    "crucible_tutorials_suppress": 0,
    "crucible_ui_cancel": 1,
    "crucible_ui_click": 1,
    "crucible_ui_click_nth": 3,
    "crucible_ui_matches": 2,
    "crucible_ui_dump": 2,
    "crucible_ui_focus": 1,
    "crucible_ui_nav": 1,
    "crucible_ui_press": 1,
    "crucible_ui_submit": 1,
    "crucible_ui_where": 1,
    "crucible_use_ability": 3,
    "crucible_use_ability_auto": 0,
    # <itemConfigNameOrThingId> <x> <y> [abilityName] -- the 4th is optional and pads to "".
    "crucible_use_item": 4,
    "crucible_win_combat": 0,
}

# Remembers reported shortfalls so one wrong call site is warned about once, not once per call.
_ARITY_WARNED = set()

# The token used for an omitted trailing argument.
#
# Deliberately the EMPTY STRING, not "-". Both are accepted wherever "-" is the documented
# sentinel (UiTreeRenderer.cs:69 `IsNullOrEmpty(filter) || filter == "-"`;
# UiKindsFilter.cs:21-24; CrucibleMapEncounters' `!IsNullOrEmpty(filter) && filter.Trim() != "-"`),
# but only "" also means "absent" to the handlers that test with IsNullOrEmpty or `?? default`:
#   crucible_endadventure_watch   verb "-" has Length > 0 and prints a usage ERROR; "" falls
#                                 through to the status read the caller wanted.
#   crucible_move                 ("" ?? "true") -> consumeAP=true, canBeAmbushed=false,
#                                 showEncounterMenu=false -- exactly the documented defaults.
#   crucible_loc / _set_diorama   "" produces the handler's own usage message, which is the right
#                                 answer for an argument that really was required.
ARG_ABSENT = ""


def pad_args(command, args):
    """Fills omitted TRAILING args with ARG_ABSENT so the game's dispatcher will actually invoke."""
    args = list(args or [])
    arity = COMMAND_ARITY.get(command)
    if arity is None or len(args) >= arity:
        return args
    key = (command, len(args))
    if key not in _ARITY_WARNED:
        _ARITY_WARNED.add(key)
        sys.stderr.write(
            "drive: %s takes %d arg(s), %d given -- padding with %r. The game's dispatcher "
            "SKIPS the handler entirely on a shortfall, so this call would otherwise have "
            "silently done nothing.\n" % (command, arity, len(args), ARG_ABSENT))
    return args + [ARG_ABSENT] * (arity - len(args))


def run(command, args=None, strict=True):
    """
    One console command. Returns its result string.

    strict=True (the default) means this NEVER returns "" for a command that did not execute:
      * ok=false                  -> CommandError, carrying the plugin's own reason
      * crucible_* with no result -> CommandDidNotRun (see COMMAND_ARITY)
    Pass strict=False to get "" back instead, for the rare caller that genuinely wants to
    tolerate it. Non-crucible_* commands (the game's own EndPhase, SetStat, ...) never stash a
    result, so their empty reply is legitimate and is returned as "" in both modes.
    """
    padded = pad_args(command, args)
    payload, http_error = _post_raw("/exec", {"command": command, "args": padded})
    if payload is None:
        raise CommandError("%s: transport failed: %s" % (command, http_error))

    if not payload.get("ok"):
        message = "%s(%s) refused: %s" % (command, ", ".join(padded), payload.get("error"))
        if strict:
            raise CommandError(message)
        return ""

    if "result" in payload:
        return payload.get("result") or ""

    if not command.startswith("crucible_"):
        return ""   # a shipped game command; it has no LastResult to report.

    detail = (
        "%s(%s) reported ok=true but produced NO result. Every crucible_* handler assigns "
        "LastResult on every path, so this means the handler never ran -- almost always an "
        "arity shortfall in CommandLineHelper.ExecuteCommand (look for \"Command not found "
        "'%s'\" in the game's Player.log). Declared arity here: %s."
        % (command, ", ".join(padded), command, COMMAND_ARITY.get(command, "unknown")))
    if strict:
        raise CommandDidNotRun(detail)
    sys.stderr.write("drive: " + detail + "\n")
    return ""


def run_soft(command, args=None):
    """(ok, text, reason) for callers that want to branch instead of catching."""
    try:
        return True, run(command, args), None
    except (CommandError, CommandDidNotRun) as exc:
        return False, "", str(exc)



# ---------------------------------------------------------------------------------------------
# Fixture protection.
#
# A run that ENDS -- win OR lose -- rewrites and can delete its own save
# (SaveGameHelper.DeleteSave). That destroyed two fixtures in one session: the class sweep's old
# base was left as `_<guid>.json` (soft-deleted), and the main fixture was rewritten from 5.34 MB
# down to 552 KB by defeat testing and stopped loading into a playable run. Neither was in the
# 2026-08-23 backup because both post-date it.
#
# So: keep a pristine copy OUTSIDE the game's own GameRuns folder, and restore from it rather than
# rebuilding a party by hand. The backup folder is deliberately NOT the owner's
# Backupstk2-2026-08-23 -- those 20 originals must stay byte-identical.

GAME_RUNS = os.path.join(os.environ.get("USERPROFILE", ""),
                         "AppData", "LocalLow", "IronOak Games", "For The King II", "GameRuns")
FIXTURE_BACKUPS = os.path.join(os.environ.get("USERPROFILE", ""), "Backups", "ftk2-fixtures")

# A healthy run save is multi-megabyte. The destroyed one was 552 KB and still had a .ftk2 name,
# so existence is NOT the test -- size is the cheap signal that it is still a real save.
MIN_HEALTHY_FIXTURE_BYTES = 2 * 1024 * 1024


def fixture_path(run_id):
    return os.path.join(GAME_RUNS, run_id + ".ftk2")


def fixture_health(run_id):
    """(ok, detail) for a fixture in the game's own save folder."""
    path = fixture_path(run_id)
    if not os.path.exists(path):
        soft = os.path.join(GAME_RUNS, "_" + run_id + ".json")
        if os.path.exists(soft):
            return False, "SOFT-DELETED (present as _%s.json) -- a run ending deleted it" % run_id
        return False, "missing from %s" % GAME_RUNS
    size = os.path.getsize(path)
    if size < MIN_HEALTHY_FIXTURE_BYTES:
        return False, ("only %d bytes -- a healthy run save is multi-megabyte, so this was very "
                       "likely rewritten by a run ending" % size)
    return True, "%d bytes" % size


def backup_fixture(run_id):
    """Copies a fixture to the protected folder. Call this BEFORE driving a run to an ending."""
    ok, detail = fixture_health(run_id)
    if not ok:
        return False, "refusing to back up an unhealthy fixture: " + detail
    try:
        os.makedirs(FIXTURE_BACKUPS, exist_ok=True)
        shutil.copy2(fixture_path(run_id), os.path.join(FIXTURE_BACKUPS, run_id + ".ftk2"))
        return True, "backed up (%s)" % detail
    except OSError as exc:
        return False, "backup failed: %s" % exc


def restore_fixture(run_id):
    """Puts the pristine copy back. Returns (restored, detail)."""
    src = os.path.join(FIXTURE_BACKUPS, run_id + ".ftk2")
    if not os.path.exists(src):
        return False, "no backup at %s" % src
    try:
        shutil.copy2(src, fixture_path(run_id))
        return True, "restored from %s" % src
    except OSError as exc:
        return False, "restore failed: %s" % exc


def ensure_fixture(run_id, verbose=True):
    """Health-checks a fixture and restores it from backup if it has been damaged.

    Call this before load_run. It turns 'the loader is broken' -- which is how a destroyed fixture
    presents, since load_run just reports run.present never became true -- into a self-healing
    one-line message.
    """
    ok, detail = fixture_health(run_id)
    if ok:
        return True, detail
    restored, rdetail = restore_fixture(run_id)
    if verbose:
        print("  fixture %s is UNHEALTHY (%s); %s" % (run_id[:8], detail, rdetail))
    if not restored:
        return False, "%s; %s" % (detail, rdetail)
    return fixture_health(run_id)


def boot(timeout_seconds=180):
    """Waits for the pump, not merely for the port: the RPC answers before the game can act."""
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        try:
            health = _get("/health", attempts=1)
            if health.get("pumpAlive"):
                return True
        except (urllib.error.URLError, OSError, RuntimeError):
            # RuntimeError too: _get now raises it after exhausting retries, and during boot a
            # refused connection is the normal case rather than a failure.
            pass
        time.sleep(5)
    return False


def snapshot():
    payload = _get("/state?schema=v2")
    return payload.get("snapshot", {})


def describe():
    snap = snapshot()
    combat = snap.get("combat", {})
    combatants = combat.get("combatants")
    # null and [] are different findings: null means unreadable and always comes with a warning.
    if combatants is None:
        who = "combatants=None(unreadable)"
    else:
        who = "combatants=%d" % len(combatants)
    return "route=%s run=%s combat.active=%s %s round=%s turn=%s" % (
        snap.get("route"),
        (snap.get("run") or {}).get("present"),
        combat.get("active"),
        who,
        combat.get("round"),
        combat.get("turn"),
    )


def visible_docs():
    """
    Names of the UIDocuments with something visible on screen. Screen identity must come from
    the UI tree: route has reported MAIN_MENU while a multiplayer browser and a modal were
    actually on screen.
    """
    dump = run("crucible_ui_dump", ["-", "button"])
    found = set()
    for part in dump.split("doc='")[1:]:
        name = part.split("'")[0]
        if name and name.endswith("UIDocument"):
            found.add(name)
    return sorted(found)


# Elements that block progress and are safe to dismiss, in priority order.
#
# Every entry is an EXACT element name, never a text match. "Continue" as text appears on
# several unrelated controls including continue-btn, which resumes the owner's live co-op
# campaign - so text matching here would eventually load somebody's real save.
#
# ok-btn / royal-tutor-container were found blocking adventure selection: a tutorial modal
# ("Understood") plus the Royal Tutor overlay sit on top of the screen and silently prevent a
# fixture load, which presents as "the load did not take".
DISMISSABLE = [
    ("continue-label", "post-load gate and each intro story page"),
    # Quest resolution AWAITS the dialogue it opens. _resolveQuests is awaited by
    # _tryCompleteQuests BEFORE the AdventureEndTrigger==WIN check, so an undismissed dialogue
    # does not merely sit there -- it stops the run from ever ending. Measured 2026-08-24: the
    # WIN quest completed, and _endAdventure never fired, because this was on screen.
    ("dialogue-container", "story dialogue page; blocks _resolveQuests"),
    ("ok-btn", "tutorial/system prompt (Understood)"),
    ("royal-tutor-container", "Royal Tutor tutorial overlay"),
    ("sys-dialog-ok-btn", "system dialog confirm"),
    ("online-quit-btn", "boot-time online error modal"),
]


def visible_names(names):
    """Which of the given element names are currently on screen."""
    dump = run("crucible_ui_dump", ["-", "button"])
    return [name for name in names if ("name='%s'" % name) in dump]


def clear_gates(max_rounds=12):
    """
    Dismisses every known blocking element until none remain.

    Loops rather than pressing once: the intro is several pages, and dismissing one modal
    frequently reveals another behind it.
    """
    pressed = []
    for _ in range(max_rounds):
        present = visible_names([name for name, _reason in DISMISSABLE])
        if not present:
            return {"ok": True, "pressed": pressed}

        for name in present:
            # Focus + Enter is the path that actually retires these. It looks weaker than
            # crucible_ui_click (which reports whether the element ACTED) but it is what works:
            # continue-label ignores UIToolkitHelper.Submit entirely. Where Submit IS needed -- the
            # story dialogue and the summary's next-btn -- callers use crucible_ui_click directly.
            run("crucible_ui_focus", [name])
            run("crucible_key", ["enter"])
            pressed.append(name)
            time.sleep(1.5)

    remaining = visible_names([name for name, _reason in DISMISSABLE])
    return {"ok": not remaining, "pressed": pressed, "remaining": remaining}


# Every loot-prompt variant LootDistributionViewHelper can raise, by the localization key it
# renders into the ONE shared loot-description-text label (LootDistributionViewHelper.cs:19-29:
# LOOT_DESCRIPTION_COMBAT / _ENCOUNTER / _QUEST_COMPLETE / _TREASURE / _FATE, plus the gym string).
# Resolved live against Lang.__translations 2026-08-26 with `ftk2_loc *DESCRIPTION*`; the two seen
# on screen this session are marked. They differ ONLY in text -- the element name is identical -- so
# take_loot must never key on the wording.
LOOT_PROMPT_TEXTS = [
    "Amongst the fallen you find",     # LOOT_UI_COMBAT_DESCRIPTION      (seen: post-combat, VenueUIDocument)
    "From this encounter you receive", # LOOT_UI_ENCOUNTER_DESCRIPTION   (seen: Guarded Treasure, AdventureUIDocument)
    "Within the chest you find",       # LOOT_UI_TREASURE_DESCRIPTION    (route=TREASURE)
]


def _loot_prompt_state():
    """(is_up, item_name, action_buttons) for the loot prompt, from one dump.

    is_up keys on the loot-description-text LABEL only. item_name is the currently offered item,
    used as the PROGRESS signal -- see take_loot.
    """
    dump = run("crucible_ui_dump", ["-", "button,label"], strict=False) or ""
    up = "name='loot-description-text'" in dump
    item = None
    m = re.search(r"name='item-name-label' text='([^']*)'", dump)
    if m:
        item = m.group(1)
    actions = re.findall(r"name='hud-action-btn' text='([^']*)'", dump)
    return up, item, actions, dump


def take_loot(max_pages=12):
    """
    Clears EVERY post-combat / post-encounter / treasure loot prompt by taking each page.

    WHY THIS EXISTS. A soak run wedged here for ~45 minutes with a live game, a healthy process
    and near-zero CPU: it had won a fight, the loot modal had opened, and nothing in the harness
    answered it. Symptoms were misleading in three directions at once -- no result file, an idle
    python process that looked crashed, and a SCREENSHOT THAT WAS PURE BLACK, because modal-blocker
    dims the venue behind the prompt. None of those said "loot".

    ONE PROMPT, SEVERAL WORDINGS, TWO DOCUMENTS. There is a single loot UI --
    LootDistributionViewHelper -- and it renders all five of its descriptions into the SAME
    `loot-description-text` label (LootDistributionViewHelper.cs:15). Only the words change:
    "Amongst the fallen you find..." after combat, "From this encounter you receive...." after an
    encounter, "Within the chest you find..." at route=TREASURE (see LOOT_PROMPT_TEXTS). Verified
    live 2026-08-26: the combat prompt renders in VenueUIDocument and the encounter prompt in
    AdventureUIDocument, with the SAME element names in both. So the label name is the key and the
    wording and the document are NOT -- an earlier version that keyed on one wording would have
    missed the other two.

    Keyed on the LABEL, never on hud-action-btn. hud-action-btn lives in VenueUIDocument's tree
    permanently and is "visible+enabled" even with no prompt on screen, so keying on the button
    clicks forever and reports progress it did not make -- the identical trap documented on
    pick_reward for choice-menu-btn.

    THE ACTION BUTTONS ARE A LIST, NOT ONE BUTTON. CharacterHudController.HudActionButtons is
    `Query<Button>("hud-action-btn")` (CharacterHudController.cs:204) -- several buttons sharing one
    name -- and the loot code fills them positionally: Take into [0], then Use/Equip/Share into [1]
    (LootDistributionViewHelper.cs:376-410). Observed live on the treasure/encounter prompt:
    hud-action-btn 'Take' AND hud-action-btn 'Equip', both visible. `crucible_ui_click
    hud-action-btn` takes the FIRST visible match, which is Take -- but if it ever is not, or if the
    matched one belongs to a non-acting character hud, the click is a silent no-op.

    So this now VERIFIES PROGRESS instead of assuming it: item-name-label is read before and after
    each click, and a click that changes neither the item nor the prompt's presence is treated as a
    failure, not a page. It then retries the remaining action buttons by their exact text before
    giving up, and returns stalled=True with the dump -- an honest failure a caller can branch on,
    rather than a success-shaped {"taken": [...]} that leaves the modal on screen.
    """
    taken = []
    stalled = False
    detail = None
    for _ in range(max_pages):
        up, item, actions, dump = _loot_prompt_state()
        if not up:
            break

        clicked = run("crucible_ui_click", ["hud-action-btn"], strict=False) or ""
        progressed = "invoked=True" in clicked
        if progressed:
            time.sleep(2)
            up_after, item_after, _actions_after, _d = _loot_prompt_state()
            progressed = (not up_after) or (item_after != item)

        if not progressed:
            # The name-matched button did nothing. Fall back to the OTHER action buttons this
            # prompt is actually offering, by their exact text, in the order the game assigns them.
            for label in actions:
                if not label:
                    continue
                run("crucible_ui_click", [label], strict=False)
                time.sleep(2)
                up_after, item_after, _a, _d = _loot_prompt_state()
                if (not up_after) or (item_after != item):
                    progressed = True
                    taken.append(label)
                    break

        if not progressed:
            stalled = True
            detail = ("loot prompt still up and no action button moved it. item=%r actions=%r"
                      % (item, actions))
            break

        if not taken or taken[-1] not in actions:
            taken.append("hud-action-btn")

    up, _item, _actions, _dump = _loot_prompt_state()
    result = {"taken": taken, "stalled": stalled, "lootPromptStillUp": up}
    if detail:
        result["detail"] = detail
    return result


def pick_reward(max_prompts=8):
    """
    Answers queued "Choose Reward" prompts by taking the first option.

    Deliberately NOT part of clear_gates. choice-menu-btn is present in AdventureUIDocument's tree
    even when no prompt is on screen, so a blanket dismiss loop clicks it forever and reports
    "remaining" every time -- which is exactly what happened when it was in DISMISSABLE. The panel
    title is the reliable signal, so this looks for that first and does nothing when it is absent.

    Answering matters: quest resolution AWAITS these, they QUEUE one per completed quest, and a
    stack of unanswered prompts presents as a hang with a healthy game thread and a fully rendered
    overworld.
    """
    answered = []
    for _ in range(max_prompts):
        dump = run("crucible_ui_dump", ["-", "button,label"])
        if "Choose Reward" not in dump:
            break
        run("crucible_ui_click", ["choice-menu-btn"])
        answered.append("choice-menu-btn")
        time.sleep(2)
        run("crucible_ui_click", ["dialogue-container"])
        time.sleep(2)
    return {"answered": len(answered), "rewardPromptStillUp": "Choose Reward" in run("crucible_ui_dump", ["-", "button,label"])}


def interaction_enabled():
    """
    The AUTHORITATIVE readiness signal: AdventureDirector._enableInteraction has run.

    Screen-shape checks are not enough on their own. LoadingUIDocument lingers as an orphaned
    overlay after a load and can report present when the game is perfectly playable, and it can be
    absent while a reward prompt still has interaction switched off. This field is what the game
    itself consults.
    """
    for line in run("crucible_overworld_state").splitlines():
        if line.startswith("interactionEnabled="):
            return line.split()[0] == "interactionEnabled=True"
    return False


def wait_until(predicate, timeout_seconds=60, interval=0.4):
    """Poll `predicate` until it returns truthy, or the deadline passes; returns its value or None.

    Replaces the fixed `time.sleep(n)` calls this harness used to pace itself with. Those were
    padding chosen to be safely LONGER than the slowest observed transition, which meant every run
    paid the worst case every time even when the game was ready immediately. Polling pays the
    ACTUAL cost instead, and a whole boot-to-combat cycle is dominated by these waits.
    """
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(interval)
    return None


def _settle(timeout_seconds=8):
    """Wait for interaction to come back after dismissing a gate, rather than a flat sleep."""
    return wait_until(interaction_enabled, timeout_seconds=timeout_seconds, interval=0.3)


def wait_ready(timeout_seconds=180, verbose=False):
    """
    Drives the game to a genuinely interactive overworld, clearing whatever is in the way.

    Call this after EVERY transition -- load, spawn, combat exit -- not just after a load. The
    blockers reappear: a "Click to Continue" gate comes back after a debug spawn, the intro is
    several pages, quest resolution queues one reward prompt per completed quest, and each of those
    silently holds interaction off while the overworld looks completely normal in a screenshot.

    Returns a dict describing what it cleared and whether the game is ready, rather than raising,
    so a caller can report the real reason instead of failing later somewhere unrelated.
    """
    cleared = []
    deadline = time.time() + timeout_seconds

    while time.time() < deadline:
        if interaction_enabled():
            return {"ready": True, "cleared": cleared, "docs": visible_docs()}

        dump = run("crucible_ui_dump", ["-", "button,label"])

        if "name='continue-label'" in dump:
            # FOCUS + ENTER, not crucible_ui_click. UIToolkitHelper.Submit returns true on this
            # element and advances nothing -- measured 2026-08-24 at 48 consecutive "successful"
            # presses with the gate still up. The keyboard path clears the same gate in two.
            run("crucible_ui_focus", ["continue-label"])
            run("crucible_key", ["enter"])
            cleared.append("continue-label")
            _settle()
            continue

        if "Choose Reward" in dump:
            run("crucible_ui_click", ["choice-menu-btn"])
            cleared.append("reward")
            _settle()
            continue

        if "name='dialogue-container'" in dump:
            run("crucible_ui_click", ["dialogue-container"])
            cleared.append("dialogue")
            _settle()
            continue

        if "name='ok-btn'" in dump:
            run("crucible_ui_click", ["ok-btn"])
            cleared.append("ok-btn")
            _settle()
            continue

        # Nothing recognised is in the way but interaction is still off -- the game is mid
        # transition. Wait rather than hammering it.
        if verbose:
            print("  waiting: docs=%s" % ", ".join(visible_docs()))
        time.sleep(0.4)

    return {
        "ready": False,
        "cleared": cleared,
        "docs": visible_docs(),
        "reason": "interactionEnabled never became true within %ds" % timeout_seconds,
    }


GAME_EXE = os.path.join(
    os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
    "Steam", "steamapps", "common", "For The King II", "For The King II.exe")


def restart_game(timeout_seconds=240):
    """
    Kills the game by PID and relaunches it through Steam.

    The only reliable exit from the stale-overlay state: once LoadingUIDocument is orphaned, its
    continue-label has no handler wired, so pressing it succeeds and changes nothing -- measured
    2026-08-24 at 48 consecutive presses with interaction still disabled. Nothing short of a
    restart clears it.

    PID-scoped on purpose. A blanket kill on the process name would also take out unrelated
    processes, and this machine runs the agent that is driving the test.
    """
    subprocess.run([
        "powershell", "-NoProfile", "-Command",
        "Get-Process | Where-Object { $_.Path -like '*For The King II\\For The King*' } "
        "| ForEach-Object { Stop-Process -Id $_.Id -Force }",
    ], capture_output=True)
    time.sleep(6)

    # Launch the EXE, not steam://rungameid. Measured 2026-08-26: the steam: URL handler returned
    # immediately, Steam was already running, and the game process never appeared -- restart_game()
    # then sat out its whole boot timeout and returned False, which reads exactly like a game that
    # failed to start. Launching the exe directly is what works here; the steam: URL is kept only
    # as a fallback for an install this path does not find.
    if os.path.isfile(GAME_EXE):
        subprocess.Popen([GAME_EXE], cwd=os.path.dirname(GAME_EXE))
    else:
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Start-Process 'steam://rungameid/1676840'"], capture_output=True)
    return boot(timeout_seconds=timeout_seconds)


def load_run(run_id, attempts=3):
    """
    MAIN_MENU -> campaign -> load -> clear gate, verified at each step.

    Each step is CONFIRMED rather than assumed. Boot is a race (the game reaches MAIN_MENU and
    can route itself to MULTIPLAYER_LOBBY about two seconds later), the campaign click can land
    before the menu is interactive, and _loadGameRun silently does nothing when the adventure
    selection screen is not up. An unverified sequence fails several steps later, somewhere
    that looks unrelated.
    """

    # Self-heal a destroyed fixture before blaming the loader. A fixture rewritten by a run ending
    # still has a .ftk2 name and simply never loads, which presents as "run.present never became
    # true within 60s" -- indistinguishable from a loader bug, and it cost a whole sweep to spot.
    healthy, detail = ensure_fixture(run_id)
    if not healthy:
        return "FAILED: fixture %s is unusable and could not be restored (%s)" % (run_id, detail)

    steps = []

    # Wait for the UI to exist, not merely for the RPC pump. The pump answers while the game is
    # still on the splash screen, where a dump returns no documents at all and every navigation
    # click lands on nothing -- which reads as "did not reach adventure selection".
    for _ in range(30):
        if visible_docs():
            break
        time.sleep(3)
    else:
        return "FAILED: no UI documents appeared; the game never finished loading"

    for attempt in range(attempts):
        snap = snapshot()
        steps.append("attempt %d: route=%s" % (attempt + 1, snap.get("route")))

        # A CLEAN screen is required, but "clean" must not mean "restart on the very screen the
        # loader needs". AdventureSelectionUIDocument is a VALID starting point -- attempt 2
        # lands here after attempt 1's load, and refusing to proceed from it turns one flaky
        # load into a hard stop every time. What is genuinely dangerous is the specific stale-
        # overlay pattern this guard exists to catch: forcing RouterMono.Route(MAIN_MENU) does
        # not tear down the previous screen's UIDocuments, so the main menu can end up rendered
        # ON TOP OF a still-live adventure selection (or venue) screen, and _loadGameRun silently
        # does nothing against that mixed state -- the file resolves, no error is logged, and the
        # load simply never happens. So refuse only on (a) an unrecognised doc, or (b) that
        # specific main-menu-over-campaign overlay; a restart is still the only reliable reset for
        # either.
        docs_now = visible_docs()
        safe_docs = ("MainMenuUIDocument", "PromptUIDocument", "TutorialUIDocument", "AdventureSelectionUIDocument")
        dirty = [d for d in docs_now if d not in safe_docs]
        stale_overlay = "MainMenuUIDocument" in docs_now and "AdventureSelectionUIDocument" in docs_now
        if dirty or stale_overlay:
            reason = ", ".join(dirty) if dirty else "MainMenuUIDocument over AdventureSelectionUIDocument (stale overlay)"
            steps.append("  screen is dirty (%s); a restart is required" % reason)
            return "\n".join(steps) + "\nFAILED: dirty screen, restart the game first"

        # Escape whatever boot left on screen, including the multiplayer lobby it routes itself to.
        for _ in range(6):
            # Dismiss modals EVERY iteration, not only after loading. A tutorial prompt or the
            # Royal Tutor overlay sits on top of adventure selection and swallows the clicks that
            # would get us there, so clearing only afterwards never runs at all.
            clear_gates(max_rounds=4)
            docs = visible_docs()
            if "AdventureSelectionUIDocument" in docs:
                break
            if "MainMenuUIDocument" in docs:
                run("crucible_ui_click", ["campaign-btn"])
                time.sleep(5)
                continue
            if "MultiplayerUIDocument" in docs or "OnlineUIDocument" in docs:
                run("crucible_ui_click", ["online-quit-btn"])
                run("crucible_ui_click", ["back-btn"])
                time.sleep(4)
                continue
            time.sleep(4)

        docs = visible_docs()
        if "AdventureSelectionUIDocument" not in docs:
            steps.append("  did not reach adventure selection (docs=%s)" % ", ".join(docs))
            continue
        steps.append("  adventure selection is up")

        # Already loaded? A prior attempt's async load can complete just after that attempt's
        # own poll window closed -- checking before invoking again avoids firing _loadGameRun a
        # second time against the same director while the first call may still be in flight.
        if (snapshot().get("run") or {}).get("present"):
            steps.append("  already loaded from a prior attempt: %s" % describe())
            steps.append("  gates: %s" % clear_gates())
            steps.append("  final: %s" % describe())
            return "\n".join(steps)

        # Env.GameRuns is cached at startup, so a file copied in while the game runs is invisible
        # and _loadGameRun silently does nothing. Refresh first.
        run("crucible_refresh_saves", [])
        # _loadGameRun is `async void` on AdventureSelectionDirector -- crucible_invoke's reflected
        # call returns as soon as the method yields at its first await, well before the load
        # completes, so this string is NOT a success/failure verdict. It IS the only signal that
        # exists for a call that failed SYNCHRONOUSLY -- wrong overload resolved, an arg coercion
        # error, "no instance resolvable", or a thrown exception before the first await -- any of
        # which previously vanished silently because this return value was discarded. Log it.
        invoke_result = run("crucible_invoke", ["AdventureSelectionDirector", "_loadGameRun", "%s -" % run_id])
        steps.append("  invoke: %s" % invoke_result)

        loaded = False
        for _ in range(15):
            # A DLC-restriction dialog, a stray tutorial prompt, or a queued reward can appear
            # DURING the async load and swallow every subsequent click/poll for the rest of the
            # window -- clear_gates already runs before the invoke (for what was on screen on the
            # way in) but nothing previously ran WHILE waiting for the load itself to land.
            clear_gates(max_rounds=2)
            if (snapshot().get("run") or {}).get("present"):
                loaded = True
                break
            time.sleep(4)
        if not loaded:
            steps.append(
                "  load did not take: run.present never became true within 60s "
                "(invoke=%s, docs=%s)" % (invoke_result, ", ".join(visible_docs()))
            )
            continue

        steps.append("  loaded: %s" % describe())
        steps.append("  gates: %s" % clear_gates())
        steps.append("  final: %s" % describe())
        return "\n".join(steps)

    steps.append("FAILED to load %s after %d attempts" % (run_id, attempts))
    return "\n".join(steps)


# =============================================================================================
# OVERWORLD -> COMBAT -> OVERWORLD
#
# NOT THE PRIMARY ROUTE INTO A FIGHT. to_combat.py's spawn -> walk onto the roaming encounter ->
# press Fight is the measured-working path and is always tried first; enter_combat() below is the
# FALLBACK for when that yields no menu. Replacing it once was a regression and is not to be
# repeated.
#
# Built out of the pieces that were already measured to work, rather than a fresh mechanism:
#   * rotate_check.reach_fight()      walk onto UNTRIED encounters, close the panel, try another
#   * rotate_check.end_fight()        crucible_win_combat + wait + clear_gates + pick_reward
#   * overworld.close_open_panel()    close-btn -> crucible_encounter_leave -> clear_gates
#   * overworld.offered_actions()     crucible_interact NONE as a SAFE ActionList probe
#   * lifecycle.py's "back on the map" loop  crucible_end_phase <route> until route==ADVENTURE
# All five lived inside test scripts, so nothing else could reuse them and each new caller
# re-derived a weaker version. They are consolidated here and the gaps are closed.
#
# THE THREE THINGS THAT MADE THIS UNRELIABLE, and what is done about each:
#
# 1. interactionEnabled lapses. crucible_move answers ok=true and the party does not budge
#    because AdventureDirector._enableInteraction has been switched back off by a panel, a
#    reward prompt or a transition. It is re-established before EVERY attempt, not once up front.
#
# 2. There is no universal "Fight" button, and the ActionList cannot be driven instead. A roaming
#    encounter's button reads "Attempt", "Ambush", "Fight" or a bespoke label depending on its
#    SkillEncounterConfig.ButtonName (EncounterMenuViewHelper2.cs:559-563). The obvious fix --
#    drive eEncounterActions through crucible_interact and ignore the button -- DOES NOT WORK for
#    the fight actions, and wedges the game when tried. See FIGHT_ACTIONS_UNDRIVEABLE below for
#    the decompiled reason. The button is the mechanism; the ActionList is only used to SKIP nodes
#    that cannot fight at all.
#
# 3. A peaceful resolution is normal, not a failure. A skill encounter that rolls Treasure Chest
#    ends with no fight and leaves the party standing on a spent node -- and AMBUSH resolves on
#    that same roll, so it is no more deterministic than the rest. The node is remembered as tried
#    and the search moves on, which is why this retries ACROSS encounters rather than hammering one.
#
# crucible_force_combat is deliberately NOT used anywhere here: it routes to COMBAT without
# initialising the phase and leaves the camera under the map with no way back but a restart.

# ---------------------------------------------------------------------------------------------
# WHY crucible_interact CANNOT START A FIGHT -- the 2026-08-25 finding that corrected this module.
#
# The first version of enter_combat() drove `crucible_interact AMBUSH`, on the assumption that
# AMBUSH was the deterministic fight action. It is not, and worse, driving it that way WEDGES THE
# GAME. Both halves come out of AdventureDirector._performEncounterAction:
#
#   case eEncounterActions.AMBUSH:
#       AudioPlayHelper.Play("SFX_ENCOUNTER_AMBUSH_CUE");
#       goto case eEncounterActions.SNEAK;              // AdventureDirector.cs:10022-10024
#   case eEncounterActions.SNEAK:
#   case eEncounterActions.SKILL_TEST:
#       ...
#       SlotRollViewHelper.RenderRollPreview(
#           pMenuContext.Layout.CachedQ("slot-rolls-container"), ...);   // line ~10074
#
# 1. AMBUSH is NOT deterministic. It falls THROUGH into the SNEAK/SKILL_TEST body and resolves on
#    the same slot roll, via EncounterHelper.GetSkillConfig + _processSlotRollRewardAsync. A camp
#    that offers AMBUSH can absolutely resolve without a fight.
#
# 2. That body dereferences `pMenuContext.Layout`, a VisualElement field of EncounterActionContext
#    (EncounterMenuViewHelper2.cs:16). crucible_interact builds the context by hand and sets only
#    EncounterEntity / ViewingCharacterEntity / ActiveCharacterEntity / ProxyEntity / ViewOnly /
#    Action -- it never sets Layout, because only the live menu has one. So Layout is null and
#    `Layout.CachedQ(...)` throws a NullReferenceException inside `[SendReportOnException] private
#    async void _performEncounterAction` (line 9904): swallowed, no route change, no combat.
#    Observed exactly that, by hand: action=AMBUSH, routeBefore=ADVENTURE routeAfter=ADVENTURE,
#    no combat in 90s.
#
# 3. AND IT IS WHY INTERACTION NEVER CAME BACK. The throw happens BEFORE the branch's
#    `_closeEncounterMenuAsync` / `_stopEncounterAsync` tail (lines 10194-10199). _stopEncounterAsync
#    is what calls `_tryProceed()` (line 9888), and _tryProceed is what reaches
#    `_enableInteraction()`. Kill the handler mid-flight and `_interactionEnabled` stays false with
#    NOTHING on screen to dismiss -- which is precisely the "clear_gates presses nothing, rewards
#    are clear, ensure_interactive times out at 90s" symptom. One bug, both failures.
#
# `proxy=(none)` in that hand-run output is a RED HERRING: _proxyEncounterEntity is only non-null
# for an encounter that stands in for another one, and the AMBUSH branch's only use of it is
# `if (_proxyEncounterEntity != null)` to add a cooldown id (line ~10105). A plain roaming
# encounter legitimately has no proxy. Layout is the field that was actually missing.
#
# CONCLUSION: the button IS the mechanism. Only the real UI supplies a Layout, which is why
# to_combat.py's walk-and-press-Fight works and this did not.
FIGHT_ACTIONS_UNDRIVEABLE = ("AMBUSH", "SNEAK", "SKILL_TEST")

# Actions that CAN lead to a fight, used only to skip nodes that plainly cannot (shops, camps,
# loot). VENUE is in here because on a monster camp it is the fight itself; the guard that keeps
# it off a town lives in _try_start_fight.
COMBAT_CAPABLE_ACTIONS = {"AMBUSH", "SKILL_TEST", "VENUE", "DUNGEON"}

# Button labels that start a fight. The text is the encounter's SkillEncounterConfig.ButtonName
# resolved through Lang ("ENCOUNTER_UI_" + ButtonName), defaulting to ENCOUNTER_UI_ATTEMPT --
# EncounterMenuViewHelper2.cs:559-563 -- so "Fight" (ENCOUNTER_UI_FIGHT), "Ambush"
# (ENCOUNTER_UI_AMBUSH) and "Attempt" (ENCOUNTER_UI_ATTEMPT) are all real, verified in
# StreamingAssets/Assets/Configs/JSON~/Langs/en.json. Ordered most-specific first.
# Sneak / Avoid / Escape / Leave are deliberately ABSENT: those are the peaceful branches.
FIGHT_BUTTON_TEXTS = ("Fight", "Ambush", "Attempt", "Challenge")


def interact(action):
    """crucible_interact with a guard on the actions that CANNOT be driven this way.

    Refuses rather than firing, because firing is not a no-op: it throws inside an async void
    handler part-way through, which skips the encounter teardown and leaves interactionEnabled
    stuck false until the game restarts. See FIGHT_ACTIONS_UNDRIVEABLE above.
    """
    if action.upper() in FIGHT_ACTIONS_UNDRIVEABLE:
        raise CommandError(
            "refusing crucible_interact %s: that branch dereferences EncounterActionContext.Layout,"
            " which crucible_interact cannot populate. It throws inside async void"
            " _performEncounterAction, never reaches _stopEncounterAsync/_tryProceed, and wedges"
            " interactionEnabled. Press the encounter's own button instead." % action.upper())
    return run_soft("crucible_interact", [action])


def route():
    return snapshot().get("route")


def in_combat():
    """True once the fight is live.

    route==COMBAT and combat.active disagree at the edges -- combat.active is derived from
    CombatState.Entities being non-empty (CombatReader.cs:45), so it stays true across an
    in-venue phase change -- so both are consulted.
    """
    snap = snapshot()
    return snap.get("route") == "COMBAT" or bool((snap.get("combat") or {}).get("active"))


def ensure_interactive(timeout_seconds=90, verbose=False):
    """
    Re-establishes interactionEnabled, escalating through the game's OWN re-enable paths.

    Returns (ok, detail).

    WHY AN ESCALATION AND NOT JUST clear_gates + wait_ready. The first version of this only
    cleared gates and waited, and it timed out at 90s every time once an encounter had gone wrong
    -- with clear_gates pressing nothing and no reward prompt on screen, because nothing was on
    screen. Interaction was off for a reason no amount of waiting could fix.

    WHAT ACTUALLY TURNS IT BACK ON (AdventureDirector, decompiled):
      * `_enableInteraction()` (line 13401) sets `_interactionEnabled = true`. Nothing else does.
      * After an encounter, the caller is `_stopEncounterAsync` (line 9862), whose non-view-only
        tail is `await _tryCompleteQuests(); _tryProceed(); _doRefreshUIAll();` (line 9886-9891).
        So the re-enable is reached through `_tryProceed`, and only if `_stopEncounterAsync` runs
        to completion.
      * `_tryProceed(false)` (line 5012) falls through to `_enableInteraction()` at line 5063 --
        but only when the character still has ActionPoints. `_tryProceed(true)` always takes the
        `_doEndTurn()` branch at line 5039 instead, which hands the turn to the next character and
        re-enables interaction for THEM.

    So the ladder below is: clear what is on screen -> run the encounter teardown that calls
    `_tryProceed` -> force the turn over, which reaches `_enableInteraction` by the other route.
    Ending the turn ROTATES the active character; that is a real cost, and it is the last rung.
    """
    def note(message):
        if verbose:
            print("  ensure_interactive: " + message)

    if interaction_enabled():
        return True, "already interactive"

    tried = []

    # Rung 1 -- something dismissable is on screen. Cheap, and the common case after a load.
    gates = clear_gates()
    rewards = pick_reward()
    loot = take_loot()
    tried.append("clear_gates=%s pick_reward=%s take_loot=%s%s"
                 % (gates.get("pressed") or [], rewards.get("answered"), loot.get("taken"),
                    (" STALLED(%s)" % loot.get("detail")) if loot.get("stalled") else ""))
    ready = wait_ready(timeout_seconds=min(timeout_seconds, 30))
    if ready.get("ready"):
        note("recovered at rung 1 (gates/rewards)")
        return True, "cleared %s" % (ready.get("cleared") or gates.get("pressed") or "nothing")

    # Rung 2 -- an encounter is still open, or one died part-way through and never tore itself
    # down. crucible_encounter_leave runs _closeEncounterMenuAsync then
    # _stopEncounterAsync(null, active, ...), and it is _stopEncounterAsync that calls _tryProceed.
    # NOTE it is passed a NULL encounter entity, and _stopEncounterAsync dereferences
    # pEncounterEntity.Get<AdventureComponent>() before reaching _tryProceed (line 9885), so on a
    # null it can throw into an unawaited Task and never re-enable anything. That is exactly why
    # this is not the last rung.
    run("crucible_ui_click", ["close-btn"], strict=False)
    leave = run("crucible_encounter_leave", [], strict=False)
    tried.append("encounter_leave=%s" % (leave.splitlines()[0][:80] if leave else "(no result)"))
    clear_gates()
    if wait_until(interaction_enabled, timeout_seconds=15, interval=0.5):
        note("recovered at rung 2 (encounter teardown)")
        return True, "recovered via crucible_encounter_leave (_stopEncounterAsync -> _tryProceed)"

    # Rung 3 -- force the turn over. crucible_overworld_end_turn invokes _tryProceed(true), which
    # takes the _doEndTurn branch and re-enables interaction for the NEXT character. This changes
    # whose turn it is, so it is deliberately last.
    ended = run("crucible_overworld_end_turn", [], strict=False)
    tried.append("overworld_end_turn=%s" % (ended.splitlines()[0][:80] if ended else "(no result)"))
    clear_gates()
    pick_reward()
    take_loot()
    if wait_until(interaction_enabled, timeout_seconds=min(timeout_seconds, 60), interval=0.5):
        note("recovered at rung 3 (forced end of turn)")
        return True, "recovered via crucible_overworld_end_turn (_tryProceed(true) -> _doEndTurn)"

    return False, ("interactionEnabled never returned. Tried, in order: %s. "
                   "Nothing short of a restart re-enables it from here -- the usual cause is an "
                   "async void handler that threw part-way and so never reached _stopEncounterAsync"
                   % " | ".join(tried))


def encounter_actions():
    """
    The ActionList of the encounter the party is STANDING ON, read without side effects.

    NONE is eEncounterActions' zero/sentinel and is never a real offering, so crucible_interact
    refuses it every time and prints the real list while performing nothing -- overworld.py's
    trick, promoted here. Returns (set_or_None, raw_text).
    """
    ok, result, reason = run_soft("crucible_interact", ["NONE"])
    if not ok:
        return None, reason
    match = re.search(r"actions:\s*(.+)", result)
    if not match:
        return None, result
    raw = match.group(1).strip()
    inner = re.search(r"\[(.*?)\]", raw)
    if inner:
        raw = inner.group(1)
    actions = {a.strip().strip("[]").upper() for a in raw.split(",") if a.strip().strip("[]")}
    actions.discard("")
    return actions, result


def close_btn_present():
    return "name='close-btn'" in run("crucible_ui_dump", ["-", "button"], strict=False)


def close_encounter_panel():
    """close-btn -> crucible_encounter_leave -> clear_gates, the only sequence measured to work.

    crucible_encounter_leave ALONE leaves a slot-outcome panel up, and the nearest-first search
    then picks the very same node again forever (rotate_check.py's note).
    """
    run("crucible_ui_click", ["close-btn"], strict=False)
    time.sleep(0.8)
    run("crucible_encounter_leave", [], strict=False)
    time.sleep(0.8)
    run("crucible_summary_dismiss", [], strict=False)
    clear_gates()
    return not close_btn_present()


def hero_hex():
    match = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", run("crucible_overworld_state"))
    return (int(match.group(1)), int(match.group(2))) if match else None


def roaming_encounters(skip=()):
    """Bare-GUID encounters (monsters and search nodes) nearest first, excluding the party's own
    hex and anything in `skip`. Named ids (TAVERN, AF_TOWN_B, ...) are venues and open a shop."""
    hero = hero_hex()
    if not hero:
        return []
    px, py = hero
    found = []
    for line in run("crucible_map_encounters", ["-"]).splitlines():
        match = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
        if not match:
            continue
        x, y, name = int(match.group(1)), int(match.group(2)), match.group(3)
        if (x, y) == (px, py) or (x, y) in skip:
            continue
        if not re.match(r"^[0-9a-f]{8}-", name):
            continue
        found.append((max(abs(x - px), abs(y - py)), x, y, name))
    found.sort()
    return [(x, y, name) for _d, x, y, name in found]


def _wait_for_combat(timeout_seconds=90):
    return bool(wait_until(in_combat, timeout_seconds=timeout_seconds, interval=0.5))


def _step_onto(x, y, gather=True):
    """Moves the ACTIVE character onto a hex with the encounter menu enabled. Returns (ok, detail).

    consumeAP=false gives the pathfinder unlimited range (the game's own free-move path), so a
    distant encounter is still reachable in one call.
    """
    # NO PRE-MOVE GATHER HERE. It was tried on 2026-08-26 and it is a NET LOSS: teleporting the
    # whole party onto the destination first does fix "only one ally in the fight", but it also
    # suppresses the ambush-on-arrival that is what actually starts the fight on this route -- six
    # consecutive encounters then reported "Fight pressed and accepted, no combat within 90s".
    # The party-completeness problem is solved upstream instead, by bench's spawn-and-Fight entry
    # (to_combat.py's proven path: gather on the party's OWN hex, crucible_debug_spawn, press
    # Fight), which never walks anywhere and therefore never splits the party.
    ok, result, reason = run_soft("crucible_move", [str(x), str(y), "false", "false", "true"])
    if not ok:
        return False, reason
    if "move invoked" not in result:
        tail = result.strip().splitlines()
        return False, tail[-1][:160] if tail else "(no result)"
    wait_ready(timeout_seconds=40)
    if gather:
        # Only the ACTIVE character walks; an encounter captures whoever is standing on it, so the
        # rest of the party has to be teleported on before the fight starts or they sit it out.
        run("crucible_party_set_hex", [str(x), str(y)], strict=False)
        time.sleep(1.5)
    return True, "moved"


def _press_fight_button(timeout_seconds=12):
    """Fallback for an unreadable ActionList: click whichever fight-ish button is on screen."""
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        dump = run("crucible_ui_dump", ["-", "button"], strict=False)
        for text in FIGHT_BUTTON_TEXTS:
            if "text='%s'" % text in dump:
                if "invoked=True" in run("crucible_ui_click", [text], strict=False):
                    return True, text
        time.sleep(0.5)
    return False, None


def enter_combat(max_attempts=6, timeout_seconds=90, gather_party=True, verbose=True):
    """
    FALLBACK route into a fight, for when to_combat.walk_onto_encounter() + press_fight() has
    already failed. That pair is the PRIMARY path and is measured working; this is not a
    replacement for it.

    What this adds over walking blindly: it reads each candidate's ActionList first and skips the
    nodes that cannot start a fight at all, it retries across several encounters when one resolves
    peacefully, and it recovers interactionEnabled between attempts.

    Returns {"ok", "reason", "attempts", "tried", "route"}. Success is never taken from a
    command's own ok=true -- it is combat actually being live, read back from /state.
    """
    def say(message):
        if verbose:
            print("  enter_combat: " + message)

    if in_combat():
        return {"ok": True, "reason": "already in combat", "attempts": 0, "tried": [],
                "route": route()}

    tried = []
    last_reason = "no attempt was made"

    for attempt in range(1, max_attempts + 1):
        # Re-established EVERY attempt: it lapses after a panel, a reward or a transition, and a
        # lapsed gate is precisely the "crucible_move says ok and nothing moves" symptom.
        ok, detail = ensure_interactive(verbose=verbose)
        if not ok:
            return {"ok": False, "reason": "not interactive: " + detail, "attempts": attempt,
                    "tried": tried, "route": route()}

        candidates = roaming_encounters(skip={(x, y) for x, y, _n in tried})
        if not candidates:
            last_reason = "no untried roaming encounter is on the map (tried %d)" % len(tried)
            break

        x, y, name = candidates[0]
        tried.append((x, y, name))
        say("attempt %d -> (%d,%d) %s" % (attempt, x, y, name[:8]))

        moved, move_detail = _step_onto(x, y, gather=gather_party)
        if not moved:
            last_reason = "move onto (%d,%d) refused: %s" % (x, y, move_detail)
            say("  " + last_reason)
            continue

        # Walking on can start the fight by itself (an ambush fires during the move).
        if _wait_for_combat(timeout_seconds=6):
            return {"ok": True, "reason": "ambushed on arrival at (%d,%d)" % (x, y),
                    "attempts": attempt, "tried": tried, "route": route()}

        actions, raw = encounter_actions()
        say("  actions=%s" % (sorted(actions) if actions else "(unreadable: %s)" % str(raw)[:80]))

        # Cheap skip: a node whose ActionList carries none of the fight-capable actions is a shop,
        # a camp service or a pure loot node. Walking to it and staring at it is wasted time.
        if actions and not (actions & COMBAT_CAPABLE_ACTIONS):
            last_reason = ("(%d,%d) cannot start a fight -- ActionList is %s"
                           % (x, y, sorted(actions)))
            say("  " + last_reason)
            close_encounter_panel()
            continue

        started, how, observed = _try_start_fight(x, y, actions, timeout_seconds)
        if started:
            return {"ok": True, "reason": how, "attempts": attempt, "tried": tried,
                    "route": route()}

        # Report the mechanism ACTUALLY driven and what was actually observed. The earlier version
        # printed SKILL_TEST reasoning regardless of which action ran, which hid a real AMBUSH
        # failure behind a plausible-sounding sentence about outcome tables.
        last_reason = "%s at (%d,%d): %s" % (how, x, y, observed)
        say("  " + last_reason)
        # Leave the node closed AND remembered, or the nearest-first search picks it again.
        close_encounter_panel()

    return {"ok": False, "reason": last_reason, "attempts": len(tried), "tried": tried,
            "route": route()}


# ------------------------------------------------------------------ WHOLE-PARTY combat entry

_STAGING_OFFSETS = ((1, 0), (-1, 0), (0, 1), (0, -1), (-1, 1), (-1, -1), (1, 1), (1, -1))


def _overworld_active_name():
    text = run("crucible_overworld_state", strict=False) or ""
    match = re.search(r"\[0\] (.+?) guid=", text.split("turnOrder")[-1])
    return match.group(1).strip() if match else None


def party_hexes():
    """{display name: (x, y)} for every party member, from crucible_overworld_state."""
    text = run("crucible_overworld_state", strict=False) or ""
    return {m.group(2).strip(): (int(m.group(3)), int(m.group(4)))
            for m in re.finditer(r"\[(\d+)\] (.+?) hex=\((-?\d+), (-?\d+)\) ap=", text)}


def party_display_names():
    names = []
    for line in (run("crucible_party_list", strict=False) or "").splitlines():
        match = re.search(r"name=(.+?) hp=", line)
        if match:
            names.append(match.group(1).strip())
    return names


def _make_overworld_active(name, max_turns=8):
    """End overworld turns until `name` holds the turn. Only the ACTIVE character can move."""
    for _ in range(max_turns):
        if _overworld_active_name() == name:
            return True
        run("crucible_overworld_end_turn", [], strict=False)
        wait_ready(timeout_seconds=45)
    return _overworld_active_name() == name


def _stage_party_beside(tx, ty):
    """Teleport the WHOLE party to a hex exactly ONE legal step from (tx, ty). Returns the hex.

    Why teleport at all when the walk is what matters: crucible_move is AP-clamped (a 22-hex goal
    walks ~3-5 hexes and stops, measured 2026-08-26), so walking four characters across the map
    costs dozens of turns. crucible_party_set_hex has no arrival logic -- which is exactly why it
    cannot join an encounter -- but that makes it a perfectly safe way to move the party to a
    NEUTRAL hex next door. The one step that actually matters is then a real crucible_move, which
    does run arrival logic, once per character.
    """
    occupied = set()
    for line in (run("crucible_map_encounters", ["-"], strict=False) or "").splitlines():
        match = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
        if match:
            occupied.add((int(match.group(1)), int(match.group(2))))
    for dx, dy in _STAGING_OFFSETS:
        nx, ny = tx + dx, ty + dy
        if (nx, ny) in occupied:
            continue            # never park the party on another encounter
        run("crucible_party_set_hex", [str(nx), str(ny)], strict=False)
        preview = run("crucible_path_preview", [str(tx), str(ty)], strict=False) or ""
        if "isValidMove=True" in preview and "pathSteps=1 " in preview:
            return (nx, ny)
    return None


def enter_combat_whole_party(max_encounters=4, timeout_seconds=120, verbose=True):
    """Reach a live fight with EVERY party member in it. Returns {"ok", "reason", "missing"}.

    THE BUG THIS FIXES. Every other route into combat leaves three of four classes on the map:

      * crucible_move walks the ACTIVE character and nobody else, and an encounter captures only
        whoever is standing on it, so a batch scenario became a one-ally fight and every step for
        the other three reported SETUP-FAILED with a DIFFERENT member active each time -- which
        reads exactly like a class bug and is a staging bug.
      * crucible_party_set_hex writes HexPosition WITHOUT running arrival logic, so teleporting
        the stragglers on afterwards does not add them: the encounter has already captured its
        participants.
      * Teleporting the party onto the DESTINATION before the walk fixes the roster and kills the
        entry instead -- with nobody left to ambush, six consecutive encounters reported "Fight
        pressed and accepted, no combat within 90s".

    THE FIX, measured working 2026-08-26 (4/4 group=0 combatants: CF_ORIG_VAMPIRIC, _PACIFIST,
    _TRAINER, _GARY): stage the whole party ONE hex away, then make each character active in turn
    with crucible_overworld_end_turn and walk EACH ONE onto the encounter hex with a real
    crucible_move. Four real arrivals means the encounter genuinely captures all four. Only then
    press the fight button.
    """
    def say(message):
        if verbose:
            print("  enter_combat_whole_party: " + message)

    if in_combat():
        return {"ok": True, "reason": "already in combat", "missing": []}

    names = party_display_names()
    if not names:
        return {"ok": False, "reason": "crucible_party_list returned no party", "missing": []}

    tried = set()
    last_reason = "no encounter was attempted"
    for _attempt in range(max_encounters):
        ok, detail = ensure_interactive(verbose=verbose)
        if not ok:
            return {"ok": False, "reason": "not interactive: " + detail, "missing": names}

        candidates = roaming_encounters(skip=tried)
        if not candidates:
            last_reason = "no untried roaming encounter is on the map"
            break
        tx, ty, node = candidates[0]
        tried.add((tx, ty))
        say("target (%d,%d) %s" % (tx, ty, node[:8]))

        staged = _stage_party_beside(tx, ty)
        if not staged:
            last_reason = "no free one-step staging hex beside (%d,%d)" % (tx, ty)
            say("  " + last_reason)
            continue
        say("  staged the party at (%d,%d)" % staged)

        # ONE REAL ARRIVAL PER CHARACTER. This is the whole fix.
        walked = []
        for who in names:
            if in_combat():
                break
            if not _make_overworld_active(who):
                say("  could not make %s the active character" % who)
                continue
            run("crucible_move", [str(tx), str(ty), "false", "false", "true"], strict=False)
            wait_ready(timeout_seconds=60)
            if party_hexes().get(who) == (tx, ty):
                walked.append(who)
            else:
                say("  %s did not reach (%d,%d)" % (who, tx, ty))
        say("  walked on: %s" % (", ".join(walked) or "nobody"))

        if not in_combat():
            pressed, text = _press_fight_button(timeout_seconds=20)
            if pressed:
                say("  pressed %r" % text)
            _wait_for_combat(timeout_seconds=timeout_seconds)

        if in_combat():
            missing = _party_missing_from_fight()
            return {"ok": not missing,
                    "reason": ("all %d party members are in the fight" % len(names)) if not missing
                              else "combat is live but %s never joined" % ", ".join(missing),
                    "missing": missing}

        last_reason = ("walked %d/%d onto (%d,%d) but no combat started within %ds"
                       % (len(walked), len(names), tx, ty, timeout_seconds))
        say("  " + last_reason)
        close_encounter_panel()

    return {"ok": False, "reason": last_reason, "missing": names}


def _party_missing_from_fight():
    """Party display names that are NOT group=0 combatants in the live fight."""
    text = run("crucible_combat_snapshot", strict=False) or ""
    present = set(re.findall(r"name=(.+?) class=\S+ group=0", text))
    return [n for n in party_display_names() if n not in present]


def _try_start_fight(x, y, actions, timeout_seconds):
    """
    Starts the fight at the encounter the party is standing on. Returns (ok, how, observed).

    `how` names the mechanism actually driven and `observed` says what was actually seen, so a
    failure never gets described in terms of a mechanism that did not run.

    ORDER, and why:

    1. THE UI BUTTON, always first. It is the only route the game itself uses, and the only one
       that supplies a live `Layout` (see the note on FIGHT_ACTIONS_UNDRIVEABLE below). Its text is
       whatever the encounter's SkillEncounterConfig.ButtonName resolves to -- "Fight", "Ambush",
       "Attempt", or a bespoke label (EncounterMenuViewHelper2.cs:559-563).
    2. crucible_interact VENUE, only for an encounter that also offers AMBUSH or RETREAT. On a
       monster camp the VENUE action IS the fight: _performVenueAction reads
       `pEncounterEntity.Get<CombatEncounterComponent>().Combats` and builds the battlefield
       (AdventureDirector.cs:9944 -> _performVenueAction). Its branch in _performEncounterAction
       never touches pMenuContext.Layout, so crucible_interact CAN drive it -- and already does,
       proven live on TAVERN by overworld.py. The AMBUSH/RETREAT guard is what keeps this off a
       shop: a bare VENUE with no combat actions alongside it is a town, and
       .Get<CombatEncounterComponent>() on one would throw inside an async void.
    """
    # ---- 1. the UI button -------------------------------------------------------------------
    pressed, text = _press_fight_button()
    if pressed:
        if _wait_for_combat(timeout_seconds=timeout_seconds):
            return True, "pressed the %r button" % text, "combat became live"
        # A skill encounter's button rolls a slot table; Treasure Chest / Coins resolve peacefully.
        return False, "the %r button" % text, (
            "it was pressed and accepted, but no combat started within %ds -- for a skill "
            "encounter that is a peaceful roll outcome, not a fault" % timeout_seconds)

    # ---- 2. crucible_interact VENUE on a combat camp ----------------------------------------
    if actions and "VENUE" in actions and (actions & {"AMBUSH", "RETREAT"}):
        ok, _result, reason = interact("VENUE")
        if not ok:
            return False, "crucible_interact VENUE", "the command was refused: %s" % reason
        if _wait_for_combat(timeout_seconds=timeout_seconds):
            return True, "crucible_interact VENUE at (%d,%d)" % (x, y), "combat became live"
        return False, "crucible_interact VENUE", (
            "it returned ok but no combat started within %ds" % timeout_seconds)

    return False, "no driveable fight mechanism", (
        "no fight button rendered and the ActionList (%s) has no VENUE-on-a-combat-camp to drive"
        % (sorted(actions) if actions else "unreadable"))


def exit_combat(max_rounds=8, timeout_seconds=120, verbose=True):
    """
    Ends the current fight and returns the party to the OVERWORLD, or says exactly why not.

    Three things had to be handled, because each alone leaves the harness stuck:
      * crucible_win_combat can leave the fight ALIVE when the encounter has another wave -- the
        wave replenishes and combat.active goes straight back to true. So this loops and, when a
        wave is still standing, wipes it before winning again.
      * combat.active is NOT the phase. A venue can advance COMBAT -> TREASURE with entities still
        on the board, so "the fight is over" is asserted on the ROUTE, never on combat.active.
      * route can settle somewhere that is neither COMBAT nor ADVENTURE (TREASURE, ENCOUNTER).
        crucible_end_phase <route> plus crucible_encounter_leave walks it the rest of the way --
        lifecycle.py's own "back on the map" loop, promoted here.

    Returns {"ok", "reason", "route", "rounds"}.
    """
    def say(message):
        if verbose:
            print("  exit_combat: " + message)

    for round_index in range(1, max_rounds + 1):
        here = route()
        if here == "ADVENTURE" and not (snapshot().get("combat") or {}).get("active"):
            ok, detail = ensure_interactive()
            return {"ok": ok, "reason": "on the overworld (%s)" % detail, "route": route(),
                    "rounds": round_index - 1}

        if here == "COMBAT":
            say("round %d: winning the fight (route=COMBAT)" % round_index)
            run("crucible_win_combat", [], strict=False)
            left = wait_until(lambda: route() != "COMBAT",
                              timeout_seconds=min(timeout_seconds, 60), interval=0.5)
            if not left:
                # Still COMBAT: almost always a replenished wave. Wipe it, then win again.
                say("  still in COMBAT -- wiping the enemy group and ending the phase")
                run("crucible_combat_wipe_enemies", ["1"], strict=False)
                time.sleep(2)
                run("crucible_win_combat", [], strict=False)
                wait_until(lambda: route() != "COMBAT", timeout_seconds=30, interval=0.5)
            if route() == "COMBAT":
                run("crucible_end_phase", ["combat"], strict=False)
                wait_until(lambda: route() != "COMBAT", timeout_seconds=30, interval=0.5)
        else:
            # Neither COMBAT nor ADVENTURE: an in-venue phase (TREASURE, ENCOUNTER, ...).
            say("round %d: route=%s -- ending that phase" % (round_index, here))
            run("crucible_end_phase", [str(here).lower()], strict=False)
            run("crucible_encounter_leave", [], strict=False)

        run("crucible_summary_dismiss", [], strict=False)
        clear_gates()
        pick_reward()
        wait_until(lambda: route() == "ADVENTURE", timeout_seconds=45, interval=0.5)

    return {"ok": False,
            "reason": "route never returned to ADVENTURE after %d rounds" % max_rounds,
            "route": route(), "rounds": max_rounds}


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    action = sys.argv[1]

    if action == "boot":
        print("pump alive" if boot() else "TIMED OUT waiting for pump")
        return 0
    if action == "state":
        print(describe())
        print("docs: %s" % ", ".join(visible_docs()))
        return 0
    if action == "gate":
        print(clear_gates())
        return 0
    if action == "load":
        if len(sys.argv) < 3:
            print("usage: drive.py load <runId>")
            return 2
        print(load_run(sys.argv[2]))
        return 0
    if action == "exec":
        if len(sys.argv) < 3:
            print("usage: drive.py exec <command> [args...]")
            return 2
        print(run(sys.argv[2], sys.argv[3:]))
        return 0
    if action == "enter-combat":
        outcome = enter_combat()
        print(outcome)
        return 0 if outcome["ok"] else 1
    if action == "exit-combat":
        outcome = exit_combat()
        print(outcome)
        return 0 if outcome["ok"] else 1

    print("unknown action %r" % action)
    return 2


if __name__ == "__main__":
    sys.exit(main())
