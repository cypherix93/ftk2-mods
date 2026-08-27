#!/usr/bin/env python3
"""
LONG-RUN SOAK: does a normal campaign play through NON-BREAKING with the new classes and the
new tile feature?

This is an EXCEPTION DETECTOR, not a feature assertion suite. traits.py already asserts that each
trait does the right thing; this asks the different question the co-op session actually cares
about -- "over a hundred-odd driven actions across a dozen fights, does anything throw?"

HOW IT WORKS
  1. Snapshot the Player.log byte offset ONCE at the start.
  2. Every single driven action goes through Soak.act(), which
       - names the action (and the ability / recipe / config involved),
       - catches ANYTHING it raises so one bad step never aborts the soak,
       - and then reads ONLY the log bytes written since the previous action.
     So a stack trace found in that window is ATTRIBUTED to the action that produced it. An
     unattributed trace in a 200 MB log is nearly useless; this is the whole point of the design.
  3. Known, pre-existing noise is filtered by an explicit named allowlist (NOISE below). Every
     entry was VERIFIED against the live Player.log on 2026-08-25 before being trusted -- counts
     are recorded in each entry's comment. Everything else is signal.

WHAT IT DRIVES
  Pass A party: CF_ORIG_VAMPIRIC / CF_ORIG_PACIFIST / CF_ORIG_TRAINER (Ash) / CF_ORIG_GARY.
  Pass B party: CF_ORIG_CHAOSMAGE swapped in for the Vampiric -- four slots is the hard ceiling
                (crucible_party_set_class takes slot 0..3), so the fifth class needs a second
                pass. Pass B is also where Wild Magic Surge exercises RANDOM_TILE cheaply: it is
                ON_TURN_START at ProcChance 50, so simply taking Chaos Mage turns fires it,
                whereas Ash's Wildfire needs the FIRE charm AND a bond band AND a level gate.

  Per pass: >= --combats fights, each driven for --rounds rounds with every party member taking a
  real turn via crucible_use_ability_auto, partners left to act on their own AI turns, overworld
  traversal between fights, and a town visit. Scripted beats exercise the NEW surface:
    * Ash's partner summons and their HP carrying between fights (TrainerPartnerPersistence)
    * Gary's capture on a PLAYTHING_*-tagged BAT_CAVE_00 with LCK forced high for PERFECT rolls
    * the two capture REFUSAL paths -- a boss and an untagged config -- which must fail gracefully
    * RANDOM_TILE hazards and the tile decal refresh
    * a partner DOWNED, then the town revive

SAFETY / ROBUSTNESS
  * The base fixture is restored from C:\\Users\\ben\\Backups\\ftk2-fixtures\\ before every pass,
    via drive.ensure_fixture / drive.restore_fixture. A run that ENDS rewrites and can DELETE its
    own save, and this soak drives runs hard, so the pristine copy is put back each time.
  * If the game dies, it is relaunched by DIRECT EXE PATH, never steam://rungameid/... . The
    steam: URI is a fire-and-forget handoff that killed a live game earlier today; class_sweep.py
    and to_combat.py both fixed it the same way and this mirrors them.
  * EVERY crucible_* call here supplies EVERY parameter. The game's dispatcher SKIPS the handler
    on a shortfall while still reporting success (drive.COMMAND_ARITY's note). drive.run pads and
    raises now, but this file does not lean on that.

Usage:
    python FTK2.Crucible/tools/soak.py
    python FTK2.Crucible/tools/soak.py --combats 12 --rounds 4 --json soak.json
    python FTK2.Crucible/tools/soak.py --pass A          # only the four-class pass
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time
import traceback
import urllib.error

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive        # noqa: E402
import to_combat    # noqa: E402  (walk_onto_encounter / press_fight -- the measured-working pair)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACK = os.path.join(REPO_ROOT, "FTK2.ClassForge", "data", "ClassPacks", "CF_PACK_ORIGINALS")

# The BepInEx-piped Player.log, same path traits.py and assert_traits.py already read.
PLAYER_LOG = os.path.expandvars(
    r"%USERPROFILE%\AppData\LocalLow\IronOak Games\For The King II\Player.log")

# The sweep's game-written base save. Same id class_sweep.py uses (class_sweep.BASE_RUN_ID) and
# the same one to_combat.DEFAULT_RUN points at -- read from class_sweep rather than re-typed, so
# a rebuilt fixture only has to be updated in one place.
# REPOINTED 2026-08-26. class_sweep.BASE_RUN_ID (bdb1596d-...) is the OLD set_class bed and was
# measured content-corrupted -- a run autosaved a test party over it while fixture_health, which
# only checks SIZE, still reported it green. The bed used now is batch-4class, built through real
# CHARACTER CREATION: slot0 Vampiric / slot1 Pacifist / slot2 Pokemon Trainer / slot3 Gary, with
# genuine display names (confirmed by crucible_party_list after loading, never by fixture_health).
# Its protected copy lives at %USERPROFILE%\Backupstk2-fixtures\<BASE_RUN_ID>.ftk2, which is a
# byte copy of batch-4class.ftk2 under the run-id name drive.restore_fixture expects.
BASE_RUN_ID = "63d7d316-0134-4efa-b462-f3a24dc46922"

GAME_EXE = r"C:\Program Files (x86)\Steam\steamapps\common\For The King II\For The King II.exe"

# ---------------------------------------------------------------------------------------------
# Parties.
#
# Four slots is the ceiling: crucible_party_set_class addresses slots 0..3 and a run carries four
# characters. CF_ORIG_CHAOSMAGE therefore CANNOT ride along with the other four -- hence pass B,
# which swaps it in for the Vampiric (the Vampiric is the one whose surface -- ON_DAMAGE_DEALT
# procs and a counter gate -- is already the most heavily covered by traits.py, so it is the
# cheapest of the four to give up for one pass).
PASS_A = ["CF_ORIG_VAMPIRIC", "CF_ORIG_PACIFIST", "CF_ORIG_TRAINER", "CF_ORIG_GARY"]
PASS_B = ["CF_ORIG_CHAOSMAGE", "CF_ORIG_PACIFIST", "CF_ORIG_TRAINER", "CF_ORIG_GARY"]

# Enemy configs, each chosen for a specific reason and verified against the SHIPPED
# Characters.json (StreamingAssets/Assets/Configs/JSON~/Characters.json) on 2026-08-25:
#   BAT_CAVE_00        Tags carry PLAYTHING_OCCULT_COMMON, no BOSS/SCOURGE -> CAPTURABLE.
#   BANDIT_RANGED_01   PLAYTHING_HUMAN_COMMON -- the ordinary filler enemy, also capturable.
#   BOSS_NECROMANCER_00 Tags carry BOSS and SCOURGE -> the boss REFUSAL path.
#   GOLEM_ROCK_00      No PLAYTHING_* tag at all -> the untagged REFUSAL path. This is the one
#                      whose removal branch would crash (GetEnemyDoll returns null ->
#                      CreateThing(null) -> a dictionary lookup on a null key), so "refused
#                      gracefully" here is the difference between a guard and a hard crash.
FILLER_ENEMY = "BANDIT_RANGED_01"
CAPTURE_ENEMY = "BAT_CAVE_00"
BOSS_ENEMY = "BOSS_NECROMANCER_00"
UNTAGGED_ENEMY = "GOLEM_ROCK_00"

# Gary's capture ball, and the one ability it offers. The recipe that actually captures
# (SKILL_CF_TRAINER_CAPTURE_CATCH) is ON_ABILITY_USED gated on ROLL_TIER EQ PERFECT, so the ball
# throw is the trigger and the roll is the gate -- which is why LCK is forced up below.
CAPTURE_BALL = "ARM_ORIG_TRAINER_BALL_CAPTURE"
CAPTURE_ABILITY = "ONLY_RESISTDOWN_ATTACK"

# Ash's charms. REPAIRED 2026-08-26: this list used to hand over WATER and FIRE as well, because
# FIRE carried SKILL_CF_TRAINER_FIRE_WILDFIRE (the RANDOM_TILE half). The ENTIRE Fire and Water
# partner lines were deleted; ARM_ORIG_TRAINER_BALL_FIRE / _WATER still ship but are EMPTY HUSKS
# ("Interactable.Abilities": {}, localised as "An empty charm ... not implemented yet"), and
# SKILL_CF_TRAINER_FIRE_WILDFIRE no longer exists in skillrecipes.json. Handing them over drove
# nothing; the only surviving RANDOM_TILE source in the pack is the Chaos Mage's Wild Magic Surge,
# which is pass B. GRASS is the one live line: stages
#   SKILL_CF_TRAINER_GRASS_1 -> WOLF_GRASS_08      "Barkling"   bond 0-2
#   SKILL_CF_TRAINER_GRASS_2 -> WOLF_HELLHOUND_09  "Hellhowl"   bond 3-8
#   SKILL_CF_TRAINER_GRASS_3 -> WOLF_CHAOSHOUND_10 "Chaosmutt"  bond 9+
CHARMS = ["ARM_ORIG_TRAINER_BALL_GRASS"]
GRASS_CHARM = "ARM_ORIG_TRAINER_BALL_GRASS"
# The charm's own toolbelt abilities -- the "Send Out" path. Recorded in
# scenarios/iso-ash-commands.json:12 as reporting usable=False and never reaching the action menu,
# which is exactly why the charm is driven here through crucible_use_item (the TOOLBELT ITEM path)
# rather than through crucible_use_ability: an ability that never fires would let this soak
# conclude "no crash" from a code path it never entered.
SEND_OUT_ABILITIES = ["CF_TRAINER_SUMMON_GRASS_T1_ATTACK",
                      "CF_TRAINER_SUMMON_GRASS_T2_ATTACK",
                      "CF_TRAINER_SUMMON_GRASS_T3_ATTACK"]
# The three GRASS-line bodies, by bond band. Used to record WHICH stage actually appeared.
GRASS_STAGES = {"WOLF_GRASS_08": "Barkling (stage 1, bond 0-2)",
                "WOLF_HELLHOUND_09": "Hellhowl (stage 2, bond 3-8)",
                "WOLF_CHAOSHOUND_10": "Chaosmutt (stage 3, bond 9+)"}
VAMPIRIC_BAT = "BAT_VAMPIRE_01"

# The gated content this soak exists to REACH. Listed explicitly so the summary can print
# "NOT REACHED" for anything that did not fire -- an absent key must never read as a pass.
GATES = [
    "vampiric.gorged>=3",
    "vampiric.bat_swarm",
    "trainer.bond>=3",
    "trainer.bond>=9",
    "trainer.WOLF_GRASS_08",
    "trainer.WOLF_HELLHOUND_09",
    "trainer.WOLF_CHAOSHOUND_10",
    "trainer.verdant_charm_healthy",
    "gary.capture",
    "chaosmage.wild_magic_surge",
    "chaosmage.entropy",
    "chaosmage.backlash",
    "chaosmage.resonance",
    "pacifist.field_medic",
    "pacifist.crouching_tiger",
    "pacifist.why_cant_we_be_friends",
    "vampiric.engorged",
    "vampiric.crimson_drain",
    "gary.rivals_edge",
    "gary.smell_ya_later",
]

# Gates whose ONLY honest evidence is a proc line. RecipeDispatcher.cs:481 logs every recipe that
# fires; these have no body on the board and no counter to read, so the log IS the observation.
# Resolved once at summary time rather than polled, because the proc harvest already has them.
PROC_GATES = {
    "chaosmage.entropy": "SKILL_CF_CHAOSMAGE_ENTROPY",
    "chaosmage.backlash": "SKILL_CF_CHAOSMAGE_BACKLASH",
    "chaosmage.resonance": "SKILL_CF_CHAOSMAGE_RESONANCE",
    "chaosmage.wild_magic_surge": "SKILL_CF_CHAOSMAGE_WILD_MAGIC_SURGE",
    "pacifist.field_medic": "SKILL_CF_PACIFIST_FIELD_MEDIC",
    "pacifist.crouching_tiger": "SKILL_CF_PACIFIST_CROUCHING_TIGER",
    "pacifist.why_cant_we_be_friends": "SKILL_CF_PACIFIST_WHY_CANT_WE_BE_FRIENDS",
    "vampiric.engorged": "SKILL_CF_VAMPIRIC_ENGORGED",
    "vampiric.crimson_drain": "SKILL_CF_VAMPIRIC_CRIMSON_DRAIN",
    "vampiric.bat_swarm": "SKILL_CF_VAMPIRIC_BAT_SWARM",
    "gary.rivals_edge": "SKILL_CF_GARY_RIVALS_EDGE",
    "gary.smell_ya_later": "SKILL_CF_GARY_SMELL_YA_LATER",
    "gary.capture": "SKILL_CF_TRAINER_CAPTURE_CATCH",
    "trainer.WOLF_GRASS_08": "SKILL_CF_TRAINER_GRASS_1",
    "trainer.WOLF_HELLHOUND_09": "SKILL_CF_TRAINER_GRASS_2",
    "trainer.WOLF_CHAOSHOUND_10": "SKILL_CF_TRAINER_GRASS_3",
}


# =============================================================================================
# LOG WATCHING -- the core of the test
# =============================================================================================

# ---- SIGNAL --------------------------------------------------------------------------------
# Anything matching one of these, and NOT matching a NOISE entry, is a finding.

# Any "<Something>Exception:" headline. Deliberately the general shape rather than a fixed list,
# so a class of failure nobody predicted still gets caught. The four named below are called out
# separately only so the summary can group by them.
ANY_EXCEPTION_RE = re.compile(r"\b([A-Za-z_][A-Za-z0-9_.`]*Exception)\s*:")
NAMED_EXCEPTIONS = (
    "NullReferenceException", "KeyNotFoundException",
    "IndexOutOfRangeException", "IndexOutOfRange",
    "InvalidOperationException", "InvalidOperation",
)
# BepInEx writes "[Error  :FTK2.ClassForge]" / "[Error  :FTK2 Crucible]" -- our own mods shouting.
MOD_ERROR_RE = re.compile(r"\[Error\s*:\s*(FTK2\.ClassForge|FTK2 Crucible)\]")
# The game's own desync auditor, per the repo notes. NOTE, and this is reported honestly rather
# than quietly: neither token appears anywhere in this repo's sources NOR in the current live
# Player.log (both greps returned 0 on 2026-08-25). These patterns are kept because they cost
# nothing and the auditor may only speak up during a real multiplayer session -- but a clean soak
# is NOT evidence the auditor ran, and the summary says so.
AUDIT_RE = re.compile(r"QuestRewardAudit|mismatch detected")
# NOT signal -- the opposite. RecipeDispatcher.cs:481's proc line, harvested so the soak can say
# WHICH gated recipe actually fired rather than only "nothing threw".
PROC_RE = re.compile(r"\[ClassForge\] proc (\S+) \(")
# "issues=0" is the auditor saying it is happy; any other value is a finding.
ISSUES_RE = re.compile(r"\bissues=(?!0\b)\d+")

# ---- NOISE ---------------------------------------------------------------------------------
# Every entry VERIFIED against the live Player.log (646 KB, post-boot, 2026-08-25) before being
# trusted -- the observed count is recorded so a future reader can re-check rather than believe.
# Each predicate is applied to the HEADLINE line only, never to the whole record: testing the
# record would let a genuine exception that happens to follow a noisy one inherit its excuse.
NOISE = [
    # 423 NullReferenceExceptions, all of the form
    #   [Warning:FTK2 Crucible] RegisterCommand('crucible_ui_dump') failed: NullReferenceException
    # CommandLineHelper.Initialize has not run during plugin Awake, so registration is retried
    # each RouterMono tick until the registry exists. Self-healing by design.
    ("registercommand-retry",
     lambda line: "RegisterCommand(" in line and "NullReferenceException" in line),

    # 3 lines. Unity/Mono probing its own bundled data blobs by DLL name.
    ("unity-mono-fallback",
     lambda line: "Fallback handler could not load library" in line
                  and "MonoBleedingEdge" in line),

    # 30 lines, all on ARM_EOR_STARTER_*. Packs are adds-only; these ids already exist in the live
    # game data and are REFUSED by design. Pre-existing and deliberate. The ARM_EOR_STARTER_
    # qualifier is load-bearing -- a collision on any OTHER id would be a real regression.
    ("known-eor-starter-collision",
     lambda line: "CF_LIVE_ID_COLLISION" in line and "ARM_EOR_STARTER_" in line),

    # 96 lines. Our own authoring/provenance annotation fields on recipe JSON. The brief named
    # `_design`; the live log shows `_source` (the EOR pack's variant). Both are ours, both are
    # ignored by the loader by design, so both are filtered.
    ("authoring-annotation-field",
     lambda line: "W_UNKNOWN_FIELD" in line and ("_design" in line or "_source" in line)),

    # 4 lines. A pack class with no pack-supplied icon falls back to the vanilla atlas path.
    # Benign and logged once per id.
    ("icon-vanilla-fallback",
     lambda line: "not in pack icons" in line),

    # Boot-time only, and the offset snapshot is taken after boot -- kept because a mid-run
    # restart re-emits it and it is not a fault: the command registry is not up yet.
    ("listcommands-startup",
     lambda line: "ListCommands failed" in line),

    # EXACTLY 2 lines per boot, counted on the live Player.log 2026-08-26:
    #   Status 'STATUS_CF_ENCMOD_CURSED'       -> passive 'SKILL_CF_ENCMOD_CURSE_ON_HIT'
    #   Status 'STATUS_CF_ENCMOD_REGENERATING' -> passive 'SKILL_CF_ENCMOD_REGEN_TICK'
    # ClassForge's own PACK-LOAD lint, emitted once while CF_PACK_ENCOUNTER_MODIFIERS is parsed,
    # long before any action this soak drives -- they surface at all only because a mid-run
    # relaunch TRUNCATES the log and re-emits boot output into the first action's window, where
    # the attribution would be a lie. Scoped HARD to that pack: the identical message about
    # CF_PACK_ORIGINALS would be a real regression and is deliberately still signal.
    ("encmod-pack-load-lint",
     lambda line: "grants non-vanilla passive" in line and "CF_PACK_ENCOUNTER_MODIFIERS" in line),
]


def log_size():
    """Byte offset to read new text from later. None if the log cannot be stat'd right now."""
    try:
        return os.path.getsize(PLAYER_LOG)
    except OSError:
        return None


def log_since(offset):
    """(text, newOffset) for the Player.log bytes written since `offset`.

    Read in BINARY and decoded afterwards, deliberately. traits.py's text-mode read is fine for
    one window, but this file advances the offset thousands of times across a soak, and a
    text-mode offset is a decoder cookie -- errors='replace' can change the character count
    relative to the byte count, so a text-mode arithmetic advance drifts. Binary + len(raw) is
    exact, which is what makes "only the new text since the previous action" trustworthy.

    text is None when the file could NOT be read at all, which is a different finding from ""
    (read fine, had nothing new) -- the caller must not treat an unreadable log as a clean one.
    """
    if offset is None:
        return None, None
    try:
        with open(PLAYER_LOG, "rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            if size < offset:
                # The log was TRUNCATED under us -- the game restarted. Start again from the top
                # rather than seeking past the end and reading nothing forever.
                offset = 0
            handle.seek(offset)
            raw = handle.read()
        return raw.decode("utf-8", "replace"), offset + len(raw)
    except OSError:
        return None, None


def _classify(line):
    """The finding KIND this line represents, or None. Signal only -- noise is filtered upstream."""
    for name in NAMED_EXCEPTIONS:
        if name in line:
            return name
    match = ANY_EXCEPTION_RE.search(line)
    if match:
        return match.group(1).split(".")[-1]
    if AUDIT_RE.search(line):
        return "DesyncAudit"
    if ISSUES_RE.search(line):
        return "DesyncAudit(issues)"
    if MOD_ERROR_RE.search(line):
        return "ModError"
    return None


_FRAME_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s*)?(?:stack:\s*)?(at\s+.+)$")


def _frames(lines, start, limit=3, lookahead=14):
    """The first `limit` stack frames following the headline at `lines[start]`.

    Handles the two shapes BepInEx emits: bare `  at Foo.Bar (...)` continuation lines, and
    Crucible's own `[Warning:FTK2 Crucible]   stack:   at Foo.Bar (...)` prefixed variant.
    """
    out = []
    for line in lines[start + 1:start + 1 + lookahead]:
        if len(out) >= limit:
            break
        match = _FRAME_RE.match(line)
        if match:
            out.append(re.sub(r"\s+", " ", match.group(1)).strip()[:200])
            continue
        # A blank/continuation line does not end the trace; a new bracketed log record does.
        if line.strip() and re.match(r"^\[(Info|Warning|Error|Debug|Message)", line.strip()):
            break
    return out


def scan(text):
    """Findings in one window of new log text. Returns a list of dicts (no attribution yet)."""
    if not text:
        return []
    lines = text.splitlines()
    findings = []
    for index, line in enumerate(lines):
        kind = _classify(line)
        if kind is None:
            continue
        noise = next((name for name, test in NOISE if _safe_test(test, line)), None)
        if noise:
            continue
        findings.append({
            "kind": kind,
            "headline": re.sub(r"\s+", " ", line).strip()[:400],
            "frames": _frames(lines, index),
        })
    return findings


def _safe_test(test, line):
    try:
        return bool(test(line))
    except Exception:
        return False


class Watch(object):
    """Owns the log offset and the accumulated findings, keyed so one repeat cannot flood."""

    def __init__(self):
        self.offset = log_size()
        self.unreadable_windows = 0
        self.findings = {}          # signature -> record
        self.total_hits = 0
        # Signatures ALREADY in the log before the soak took its baseline.
        #
        # This is not a back door for widening the noise list. It exists because the log carries
        # real, unfiltered signal that PREDATES this soak, and a soak must not fail the build for
        # something it did not cause. Measured on the live log 2026-08-25: after filtering, three
        # records survive, all one NullReferenceException in
        # OnScreenKeyboardTextFieldHelper._onSubmitText -- a vanilla on-screen-keyboard fault that
        # the harness's own focus+Enter gate-clearing very likely re-triggers, so it WILL reappear
        # during the soak. Such findings are still printed in full, under their own heading; they
        # simply do not decide PASS/FAIL. A NEW signature does.
        self.preexisting = set()
        # Recipe procs seen since the baseline. RecipeDispatcher.cs:481 emits exactly
        #   [ClassForge] proc <RecipeId> (<VerboseLogTag>) owner=<guid> ...
        # for every recipe that actually FIRES. That is the LOG half of the paired evidence for
        # "did the gated content fire", and it costs nothing because drain() is already reading
        # every byte of new log text. It is deliberately kept OUT of the pass/fail verdict -- this
        # file is an exception detector and a proc is not an exception.
        self.procs = {}
        text, _next = log_since(0)
        if text:
            for item in scan(text):
                self.preexisting.add((item["kind"], item["headline"][:220]))

    def drain(self, action, context):
        """Read the window written since the last drain and attribute anything in it to `action`."""
        text, next_offset = log_since(self.offset)
        if text is None:
            self.unreadable_windows += 1
            self.offset = log_size()
            return []
        self.offset = next_offset
        for match in PROC_RE.finditer(text):
            self.procs[match.group(1)] = self.procs.get(match.group(1), 0) + 1
        found = scan(text)
        for item in found:
            # Signature is kind + headline, so 40 repeats of one NRE are ONE reported finding with
            # count=40 and the FIRST attribution kept -- the first occurrence is the one whose
            # preceding action is actually informative.
            signature = (item["kind"], item["headline"][:220])
            self.total_hits += 1
            record = self.findings.get(signature)
            if record is None:
                self.findings[signature] = {
                    "kind": item["kind"],
                    "headline": item["headline"],
                    "frames": item["frames"],
                    "count": 1,
                    "firstAction": action,
                    "firstContext": dict(context or {}),
                    "alsoAfter": [],
                    "preexisting": signature in self.preexisting,
                }
            else:
                record["count"] += 1
                if action not in record["alsoAfter"] and action != record["firstAction"] \
                        and len(record["alsoAfter"]) < 8:
                    record["alsoAfter"].append(action)
                if not record["frames"] and item["frames"]:
                    record["frames"] = item["frames"]
        return found

    def resync(self):
        """After a game restart the log is TRUNCATED -- re-baseline instead of reading garbage."""
        self.offset = log_size()


# =============================================================================================
# THE SOAK
# =============================================================================================

class Soak(object):

    def __init__(self, options):
        self.options = options
        self.watch = Watch()
        self.actions = 0
        self.failed_actions = []     # (action, error) -- the driver failed, distinct from a log hit
        self.combats = 0
        self.notes = []              # (label, text) observations, printed at the end
        self.restarts = 0
        self.aborted = None
        # The point of the REPAIR, as distinct from the point of the soak. "Nothing threw" was
        # already true of round-1 fights; what has never been reached is the GATED content. Each
        # key is set only when the thing is actually observed, so an absent key reports as
        # UNREACHED rather than quietly passing.
        self.reached = {}
        self.turns_by_class = {}     # configName -> real turns driven through use_ability_auto
        self.shots = {}              # gate key -> screenshot path taken at the moment it fired
        self.stalls_dumped = 0       # capped: a wedge that repeats must not flood the transcript

    # ---- the one chokepoint every driven action goes through --------------------------------

    def act(self, action, function, **context):
        """
        Drive one action, then attribute whatever the game logged during it.

        NEVER raises. A failed action is recorded and the soak continues -- the whole point of a
        soak is that it survives its own findings. Returns (ok, value).
        """
        self.actions += 1
        label = "%-4d %s" % (self.actions, action)
        detail = " ".join("%s=%s" % (k, v) for k, v in sorted((context or {}).items()))
        print("  [%s]%s" % (label, (" " + detail) if detail else ""))
        ok, value = True, None
        try:
            value = function()
        except (drive.CommandError, drive.CommandDidNotRun) as exc:
            ok = False
            self.failed_actions.append((action, "%s: %s" % (type(exc).__name__, str(exc)[:300])))
            print("        ! %s: %s" % (type(exc).__name__, str(exc).splitlines()[0][:200]))
        except (RuntimeError, urllib.error.URLError, OSError) as exc:
            # Transport-shaped: the game may have died. Recover, then carry on.
            ok = False
            self.failed_actions.append((action, "%s: %s" % (type(exc).__name__, str(exc)[:300])))
            print("        ! transport %s: %s" % (type(exc).__name__, str(exc)[:200]))
            self.recover()
        except Exception as exc:                                # noqa: BLE001 - deliberate
            ok = False
            self.failed_actions.append(
                (action, "%s: %s" % (type(exc).__name__, str(exc)[:300])))
            print("        ! unexpected %s: %s" % (type(exc).__name__, str(exc)[:200]))
            if self.options.trace:
                traceback.print_exc()
            # A wait_ready that came back NOT ready is the shape every wedge this project has hit
            # presents as -- the "Travelling to a new area..." stall wedged a previous run twice,
            # reproducibly, and nobody has ever diagnosed it because every agent restarted first
            # and looked afterwards. The dump is taken HERE, before anything recovers, and costs
            # one RPC on a path that is already failing.
            if isinstance(value, dict) and value.get("ready") is False:
                self.diagnose_stall(action, value.get("reason"))
        finally:
            hits = self.watch.drain(action, context)
            for hit in hits:
                print("        >> %s  %s" % (hit["kind"], hit["headline"][:170]))
                for frame in hit["frames"]:
                    print("             %s" % frame[:150])
        return ok, value

    def diagnose_stall(self, action, reason):
        """Capture what is ON SCREEN during a stall, BEFORE anything tries to recover.

        Deliberately read-only: crucible_ui_dump and a screenshot, nothing pressed. Never a blind
        submit and never a text match -- the buttons that resume the operator's real co-op saves
        live on exactly the screens a confused recovery wanders onto.
        """
        if self.stalls_dumped >= 6:
            return
        self.stalls_dumped += 1
        self.note("STALL after %s" % action, "wait_ready reason=%s" % reason)
        ok, dump = self.act("stall.ui_dump", lambda: drive.run(
            "crucible_ui_dump", ["-", "button,label"]), after=action)
        if ok and dump:
            for line in str(dump).splitlines()[:40]:
                if line.strip():
                    self.note("STALL ui", line.strip()[:180])
        self.act("stall.route", lambda: drive.describe(), after=action)
        try:
            import evidence
            image = evidence.grab(("full",), wait_seconds=8)
            path, _size = evidence.save(image, "soak_stall_%d" % self.stalls_dumped, "full")
            self.note("STALL shot", path)
        except Exception as exc:                                  # noqa: BLE001 - best effort
            self.note("STALL shot", "failed: %s: %s" % (type(exc).__name__, str(exc)[:120]))

    def mark(self, key, evidence):
        """Record that a piece of gated content actually fired. First observation wins.

        A screenshot is grabbed at the MOMENT of the first observation, because the whole reason
        Hellhowl / Chaosmutt / the bat / a Wild Magic tile are interesting is that nobody has ever
        SEEN them, and a snapshot row is only the log half of the paired evidence. It is best
        effort: a soak must never die because a capture failed, and evidence.grab already refuses
        to hand back a blank mid-transition frame silently.
        """
        if key in self.reached:
            return
        self.reached[key] = str(evidence)[:220]
        print("        * REACHED %s: %s" % (key, str(evidence)[:160]))
        try:
            import evidence
            image = evidence.grab(("full",), wait_seconds=12)
            path, _size = evidence.save(image, "soak_reached_" + re.sub(r"[^A-Za-z0-9]+", "_", key),
                                        "full")
            self.shots[key] = path
            print("          shot: %s" % path)
        except Exception as exc:                                  # noqa: BLE001 - best effort
            print("          (no screenshot: %s: %s)" % (type(exc).__name__, str(exc)[:120]))

    def note(self, label, text):
        self.notes.append((label, text))
        print("        . %s: %s" % (label, str(text)[:220]))

    # ---- game lifecycle ---------------------------------------------------------------------

    @staticmethod
    def _game_running():
        check = subprocess.run([
            "powershell", "-NoProfile", "-Command",
            r"if (Get-Process | Where-Object { $_.Path -like '*For The King II\For The King*' }) "
            "{ 'yes' } else { 'no' }",
        ], capture_output=True, text=True)
        return "yes" in (check.stdout or "")

    def relaunch(self, timeout=240):
        """
        PID-scoped kill, then a DIRECT EXE LAUNCH.

        Never steam://rungameid/1676840. That URI is a fire-and-forget handoff to Steam: if Steam
        is slow or swallows it the process never appears, boot() burns its whole timeout, and the
        caller then "restarts" a game that was actually still alive -- which killed a live game on
        2026-08-25. class_sweep.restart_game and to_combat.restart_game both fixed it exactly this
        way; this mirrors them rather than calling drive.restart_game(), which still uses the URI.
        """
        self.restarts += 1
        subprocess.run([
            "powershell", "-NoProfile", "-Command",
            r"Get-Process | Where-Object { $_.Path -like '*For The King II\For The King*' } "
            "| ForEach-Object { Stop-Process -Id $_.Id -Force }; Start-Sleep -Seconds 6; "
            "Start-Process '" + GAME_EXE + "'",
        ], capture_output=True)
        up = drive.boot(timeout_seconds=timeout)
        # A relaunch TRUNCATES Player.log. Re-baseline or every later window reads nonsense.
        self.watch.resync()
        if not up:
            print("  relaunch: pump never came up within %ds (process running=%s); retrying once"
                  % (timeout, self._game_running()))
            subprocess.run([
                "powershell", "-NoProfile", "-Command",
                "Start-Process '" + GAME_EXE + "'",
            ], capture_output=True)
            up = drive.boot(timeout_seconds=timeout)
            self.watch.resync()
        return up

    def recover(self):
        """Bring the game back and reload the fixture after a transport failure."""
        try:
            drive._get("/health", attempts=1)
            return True                       # RPC is fine; it was a one-off refusal.
        except Exception:                     # noqa: BLE001
            pass
        print("  recovering: the RPC is unreachable -- relaunching the game")
        if not self.relaunch():
            self.aborted = "the game did not come back after a relaunch"
            return False
        return self.load_fixture()

    def load_fixture(self):
        """Restore the pristine base save, load it, and clear whatever is on screen."""
        ok, detail = drive.ensure_fixture(BASE_RUN_ID)
        if not ok:
            print("  base fixture unusable: %s" % detail)
            return False
        restored, rdetail = drive.restore_fixture(BASE_RUN_ID)
        print("  base fixture: %s" % (rdetail if restored else detail))

        transcript = drive.load_run(BASE_RUN_ID)
        if "FAILED" in transcript:
            print("  load failed; relaunching once and retrying")
            if not self.relaunch():
                self.aborted = "the game did not come back after a relaunch"
                return False
            drive.restore_fixture(BASE_RUN_ID)
            transcript = drive.load_run(BASE_RUN_ID)
            if "FAILED" in transcript:
                print(transcript)
                return False

        drive.run("crucible_tutorials_suppress", [], strict=False)
        drive.clear_gates()
        drive.pick_reward()
        ready = drive.wait_ready(timeout_seconds=180)
        if not ready.get("ready"):
            print("  not interactive after load: %s" % ready.get("reason"))
            return False
        return True

    # ---- party -------------------------------------------------------------------------------

    def build_party(self, classes, class_data):
        """
        Swap all four slots, THEN hand out each class's Things, THEN equip.

        Order is not cosmetic. Swapping a class rebuilds the character's kit from its class config,
        so an equip performed before the last swap is silently overwritten (class_sweep.py's note).
        And class-config `Things` are NOT applied retroactively to a swapped character -- the live
        charm-bootstrap bug on 2026-08-25 was exactly this: a class-swapped Trainer holds nothing,
        so the whole partner feature set is unreachable. Everything is therefore handed over by
        hand here rather than assumed.
        """
        # crucible_party_set_class is ACCEPTABLE in a soak and only in a soak. It leaves stale
        # vanilla DISPLAY names, which voids every screenshot for IDENTITY -- but this file is an
        # exception detector, and "does anything throw across 200 actions" does not depend on what
        # the HUD calls the character. What it DOES cost is nothing, so the swap is skipped where
        # it is unnecessary: the batch-4class bed was built through real character creation and
        # already holds pass A exactly, so pass A now keeps its genuine names (Vampiric / Pacifist
        # / Pokemon Trainer / Gary) and only pass B's single Chaos Mage slot is rewritten.
        current = self.party_slots()
        for slot, class_id in enumerate(classes):
            if slot < len(current) and current[slot] == class_id:
                self.note("party.slot%d" % slot,
                          "already %s from the fixture -- set_class SKIPPED, real display name kept"
                          % class_id)
                continue
            self.act("party.set_class", lambda s=slot, c=class_id: drive.run(
                "crucible_party_set_class", [str(s), c]), slot=slot, cls=class_id)

        for slot, class_id in enumerate(classes):
            things = (class_data.get(class_id) or {}).get("Things") or {}
            for thing, qty in sorted(things.items()):
                self.act("party.give_item", lambda s=slot, t=thing, q=qty: drive.run(
                    "crucible_give_item", [str(s), t, str(int(q))]),
                    slot=slot, thing=thing)

        for slot, class_id in enumerate(classes):
            starter = starter_weapon(class_data, class_id)
            if not starter:
                continue
            # Abilities come from the EQUIPPED WEAPON, not the class. A class swapped without its
            # weapon offers the PREVIOUS class's abilities -- which looks like a working class and
            # is not, and would make every use_ability_auto in this soak test the wrong kit.
            self.act("party.equip_starter", lambda s=slot, w=starter: drive.run(
                "crucible_equip", [str(s), w]), slot=slot, weapon=starter)

        trainer_slot = index_of(classes, "CF_ORIG_TRAINER")
        if trainer_slot is not None:
            for charm in CHARMS:
                # The GRASS charm normally arrives with the class Things above, but a class-swapped
                # Trainer holds nothing (the 2026-08-25 charm-bootstrap bug), so it is handed over
                # unconditionally -- a second copy is harmless, a missing one silently disables the
                # whole partner line. FIRE and WATER are NOT handed over any more: both lines were
                # deleted and their charms are empty husks (see CHARMS above).
                self.act("trainer.give_charm", lambda s=trainer_slot, c=charm: drive.run(
                    "crucible_give_item", [str(s), c, "1"]), slot=trainer_slot, charm=charm)

        gary_slot = index_of(classes, "CF_ORIG_GARY")
        if gary_slot is not None:
            self.act("gary.give_capture_ball", lambda s=gary_slot: drive.run(
                "crucible_give_item", [str(s), CAPTURE_BALL, "1"]),
                slot=gary_slot, thing=CAPTURE_BALL)
            # SKILL_CF_TRAINER_CAPTURE_CATCH is gated ROLL_TIER EQ PERFECT. LCK drives the roll,
            # so it is forced to the ceiling -- without this a capture is a coin-flip the soak
            # cannot rely on observing, and the refusal paths below would never be reached either.
            self.act("gary.raise_luck", lambda s=gary_slot: drive.run(
                "crucible_set_stat_value", [str(s), "LCK", "999"]), slot=gary_slot, stat="LCK")

        for slot in range(len(classes)):
            # SELF_LEVEL gates the partner tiers. Level 5 opens stage 2 without rushing stage 4.
            self.act("party.set_level", lambda s=slot: drive.run(
                "crucible_set_level", [str(s), "5"]), slot=slot)

        ok, listing = self.act("party.list", lambda: drive.run("crucible_party_list", []))
        if ok and listing:
            for line in listing.splitlines():
                if line.strip().startswith("["):
                    self.note("party", line.strip()[:200])

    # ---- combat ------------------------------------------------------------------------------

    def combatants(self):
        """guid -> row, from crucible_combat_snapshot. Same parser shape traits.py uses: a row
        needs BOTH ' name=' and ' hp=', because the header carries its own activeGuid=<guid> and a
        naive substring search false-matches it."""
        ok, text = self.act("combat.snapshot", lambda: drive.run("crucible_combat_snapshot", []))
        if not ok or not text:
            return {}, ""
        rows = {}
        for line in text.splitlines():
            if " name=" not in line or " hp=" not in line:
                continue
            guid = line.strip().split()[0]
            row = {"line": line.strip()}
            for key in ("class", "name", "group", "hp", "dead"):
                match = re.search(key + r"=(\S+)", line)
                if match:
                    row[key] = match.group(1)
            match = re.search(r"tile=\((\d+), (\d+)\)", line)
            if match:
                row["tile"] = (int(match.group(1)), int(match.group(2)))
            match = re.search(r"statuses=count=\d+ \[([^\]]*)\]", line)
            row["statuses"] = match.group(1) if match else ""
            rows[guid] = row
        return rows, text

    @staticmethod
    def active_guid(text):
        match = re.search(r"activeGuid=(\S+)", text or "")
        return match.group(1) if match else None

    def tile_hazards(self, text):
        """Board tiles carrying a status, from the snapshot's own tile grid. A tile row has
        tile=(x, y) and no hp= -- that is exactly what separates it from a combatant row."""
        out = []
        for line in (text or "").splitlines():
            if " hp=" in line or "tile=(" not in line:
                continue
            match = re.search(r"statuses=count=(\d+) \[([^\]]*)\]", line)
            if match and match.group(1) != "0":
                coords = re.search(r"tile=\((\d+), (\d+)\)", line)
                out.append(("(%s,%s)" % (coords.group(1), coords.group(2)) if coords else "(?,?)",
                            match.group(2)))
        return out

    def ensure_enemies(self, minimum=2, config=None):
        """Top up living non-group-0 combatants. Group 0 is OUR side -- Ash's partner and the
        Vampiric's bat are both group 0 and must never be counted as enemies."""
        config = config or FILLER_ENEMY
        rows, _text = self.combatants()
        alive = [r for r in rows.values() if r.get("group") != "0" and r.get("dead") != "True"]
        need = minimum - len(alive)
        if need > 0:
            self.act("combat.spawn_enemies", lambda: drive.run(
                "crucible_combat_spawn", [config, str(need), "1"]), config=config, count=need)
            time.sleep(1.5)
        return need <= 0

    def drive_rounds(self, rounds, tag):
        """
        Take real turns. Every party member acts through crucible_use_ability_auto; partner and
        enemy turns are simply ended so their own AI resolves them.

        Deliberately NOT crucible_combat_restore_actions: that lets the same actor swing again
        inside one round, which never resets a ONCE_PER_ROUND recipe budget -- so Wild Magic Surge
        and the Wildfire tile effect would fire once and then never again for the whole fight.
        """
        for round_index in range(1, rounds + 1):
            if not drive.in_combat():
                self.note(tag, "combat ended after %d round(s)" % (round_index - 1))
                return
            self.ensure_enemies(2)
            rows, text = self.combatants()
            active = self.active_guid(text)
            row = rows.get(active) or {}
            who = row.get("class") or row.get("name") or "?"
            if row.get("group") == "0":
                self.act("combat.use_ability_auto",
                         lambda: drive.run("crucible_use_ability_auto", []),
                         round=round_index, actor=who, fight=tag)
                # A soak that never actually gives a class the clock proves nothing about it, and
                # "combat.use_ability_auto" in the transcript does not say WHO swung. Counted here
                # so the summary can report turns-per-class as a number rather than an impression.
                config = row.get("class") or "?"
                self.turns_by_class[config] = self.turns_by_class.get(config, 0) + 1
            else:
                self.act("combat.let_ai_act", lambda: None,
                         round=round_index, actor=who, fight=tag)
            self.act("combat.end_turn", lambda: drive.run("crucible_combat_end_turn", []),
                     round=round_index, actor=who, fight=tag)
            time.sleep(0.8)

            hazards = self.tile_hazards(text)
            if hazards:
                self.note("%s tile hazards" % tag,
                          ", ".join("%s %s" % (xy, st) for xy, st in hazards[:6]))

    def start_combat(self, tag):
        """
        Into a live fight.

        PRIMARY is the debug spawn plus to_combat.press_fight -- the pair that reached combat ~8
        times in one session and is the measured-working route. drive.enter_combat() is the
        FALLBACK it was written to be: it rescans, skips ActionLists that cannot start a fight,
        and recovers interactionEnabled between attempts.
        """
        if drive.in_combat():
            return True

        # If an encounter panel is ALREADY up, the fight is one press away and every walking
        # routine below would first have to close it. Press it.
        if drive.close_btn_present() or "Fight" in (drive.run(
                "crucible_ui_dump", ["-", "button"], strict=False) or ""):
            ok, pressed = self.act("overworld.press_fight_open_panel",
                                   lambda: to_combat.press_fight(timeout_seconds=40), fight=tag)
            if ok and pressed and drive.in_combat():
                return True

        # PRIMARY: get ALL FOUR into ONE fight. A soak whose fights are fought by the active
        # character alone tests a quarter of what it claims to.
        ok, result = self.act("overworld.enter_combat_all", lambda: self.enter_combat_all(tag),
                              fight=tag)
        if (ok and result and result.get("ok")) or drive.in_combat():
            return True

        # drive.enter_combat_whole_party is kept as a BOUNDED fallback only -- see
        # enter_combat_all's docstring for the measured wedge that demoted it. max_encounters=1
        # caps the damage at one attempt instead of four.
        ok, result = self.act("overworld.enter_combat_whole_party",
                              lambda: drive.enter_combat_whole_party(max_encounters=1,
                                                                     timeout_seconds=45,
                                                                     verbose=True), fight=tag)
        if (ok and result and result.get("ok")) or drive.in_combat():
            return True

        self.act("overworld.gather_party", self._gather_party, fight=tag)
        self.act("overworld.spawn_enemy", lambda: drive.run(
            "crucible_debug_spawn", ["enemies", FILLER_ENEMY]), fight=tag, config=FILLER_ENEMY)
        ok, pressed = self.act("overworld.press_fight",
                               lambda: to_combat.press_fight(timeout_seconds=40), fight=tag)
        if ok and pressed and drive.in_combat():
            return True

        self.act("overworld.walk_onto_encounter",
                 lambda: to_combat.walk_onto_encounter(), fight=tag)
        self.act("overworld.press_fight_2",
                 lambda: to_combat.press_fight(timeout_seconds=60), fight=tag)
        if drive.in_combat():
            return True

        ok, result = self.act("overworld.enter_combat_fallback",
                              lambda: drive.enter_combat(max_attempts=4, verbose=True), fight=tag)
        return bool(ok and result and result.get("ok")) or drive.in_combat()

    def enter_combat_all(self, tag):
        """Walk EVERY party member onto one encounter WITHOUT opening the encounter panel.

        MEASURED WEDGE, 2026-08-26 -- this is the diagnosis the repo did not have.
        drive.enter_combat_whole_party() stages the party one hex away and then walks each member
        on with

            crucible_move <x> <y> false false TRUE          # 5th arg = showEncounterMenu

        The FIRST arrival pops the encounter panel, and that panel is modal: it holds
        interactionEnabled at False for as long as it is up. Captured live while the soak sat in
        this call for 45 minutes on one fight:

            interactionEnabled=False
            turnOrder (index 0 is ACTIVE):  [0] Pacifist  [1] Pokemon Trainer  [2] Gary
            party: [0] Vampiric hex=(40,40) ap=3 hasMoved=True turnsPlayed=1
                   [1] Pacifist hex=(40,40) ap=5 hasMoved=True turnsPlayed=0
                   [2] Pokemon Trainer hex=(41,40) ap=0 hasMoved=False turnsPlayed=0
                   [3] Gary          hex=(41,40) ap=0 hasMoved=False turnsPlayed=0

        Two walked on, then the panel came up and the other two were stranded at the staging hex
        on 0 AP with input disabled. Every remaining iteration then burned its full
        _make_overworld_active budget plus a 60s wait_ready that could never succeed, four
        characters deep, four encounters wide. It is not a hang -- it is a bounded loop with an
        unaffordable bound, and it presents exactly like a hang.

        The fix is one argument: walk on with showEncounterMenu=FALSE so no panel opens until
        every member has arrived, then press Fight once at the end. Nothing here presses a button
        by text match beyond to_combat.press_fight, which targets the encounter-button element.
        """
        if drive.in_combat():
            return {"ok": True, "reason": "already in combat"}
        names = drive.party_display_names()
        if not names:
            return {"ok": False, "reason": "crucible_party_list returned no party"}

        ok, detail = drive.ensure_interactive(verbose=True)
        if not ok:
            return {"ok": False, "reason": "not interactive: " + detail}

        candidates = drive.roaming_encounters()
        if not candidates:
            return {"ok": False, "reason": "no roaming encounter on the map"}
        tx, ty, node = candidates[0]
        print("  enter_combat_all: target (%d,%d) %s" % (tx, ty, node[:8]))

        staged = drive._stage_party_beside(tx, ty)
        if not staged:
            return {"ok": False, "reason": "no free staging hex beside (%d,%d)" % (tx, ty)}
        print("  enter_combat_all:   staged the party at (%d,%d)" % staged)

        walked = []
        for index, who in enumerate(names):
            if drive.in_combat():
                break
            if not drive._make_overworld_active(who):
                print("  enter_combat_all:   could not make %s active" % who)
                continue
            # consumeAP=false gives unlimited range. showEncounterMenu is FALSE for everyone but
            # the LAST arrival: the panel is modal and holds interactionEnabled off, so opening it
            # early strands whoever has not walked yet. Opening it on the last arrival gives the
            # Fight button with the whole party already standing on the hex.
            last = index == len(names) - 1
            drive.run("crucible_move",
                      [str(tx), str(ty), "false", "false", "true" if last else "false"],
                      strict=False)
            time.sleep(1.2 if not last else 2.5)
            if drive.party_hexes().get(who) == (tx, ty):
                walked.append(who)
        print("  enter_combat_all:   walked on: %s" % (", ".join(walked) or "nobody"))

        if not drive.in_combat():
            self.act("overworld.press_fight_all",
                     lambda: to_combat.press_fight(timeout_seconds=45), fight=tag,
                     walkedOn=len(walked))
            # press_fight returns as soon as the CLICK lands; the fight starts asynchronously.
            # Checking in_combat() immediately reported False while combat was spinning up, and
            # this function then fell through to the whole_party fallback -- which calls
            # crucible_party_set_hex, i.e. it would have teleported the party mid-combat. WAIT.
            drive._wait_for_combat(timeout_seconds=60)
        if drive.in_combat():
            missing = drive._party_missing_from_fight()
            self.note("%s roster" % tag,
                      "in the fight: %d/%d%s"
                      % (len(names) - len(missing), len(names),
                         ("; missing " + ", ".join(missing)) if missing else ""))
            # A fight with SOME of the party is still a real fight and still detects exceptions;
            # it is reported honestly rather than refused.
            return {"ok": True, "reason": "combat live", "missing": missing}
        return {"ok": False, "reason": "walked %d/%d onto (%d,%d) but no combat started"
                                       % (len(walked), len(names), tx, ty)}

    def _gather_party(self):
        """Only the ACTIVE character walks, and an encounter captures whoever is standing on it --
        so the rest of the party has to be teleported on before the fight or they sit it out, and
        a soak that fights with one character tests a quarter of what it claims to."""
        state = drive.run("crucible_overworld_state", [])
        lead = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
        if not lead:
            return "no lead hex"
        return drive.run("crucible_party_set_hex", [lead.group(1), lead.group(2)])

    def end_combat(self, tag):
        ok, result = self.act("combat.exit", lambda: drive.exit_combat(verbose=True), fight=tag)
        if not (ok and result and result.get("ok")):
            self.note("%s exit" % tag, (result or {}).get("reason", "exit_combat failed"))
        self.act("combat.settle", lambda: drive.wait_ready(timeout_seconds=120), fight=tag)

    def traverse(self, tag):
        """Overworld movement between fights -- the half of a campaign that is not combat, and the
        half where the encounter/quest/reward machinery actually runs."""
        self.act("overworld.end_turn", lambda: drive.run("crucible_overworld_end_turn", []),
                 leg=tag)
        self.act("overworld.settle", lambda: drive.wait_ready(timeout_seconds=90), leg=tag)
        hero = None
        ok, state = self.act("overworld.state", lambda: drive.run("crucible_overworld_state", []),
                             leg=tag)
        if ok and state:
            match = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
            if match:
                hero = (int(match.group(1)), int(match.group(2)))
        if hero:
            target = (hero[0] + 1, hero[1])
            # consumeAP=false gives the pathfinder unlimited range (the game's own free-move path).
            self.act("overworld.move", lambda t=target: drive.run(
                "crucible_move", [str(t[0]), str(t[1]), "false", "false", "true"]),
                leg=tag, to="%d,%d" % target)
            self.act("overworld.after_move", lambda: drive.wait_ready(timeout_seconds=90), leg=tag)

    # ---- the new surface ---------------------------------------------------------------------

    def read_trainer_state(self, tag):
        """The durable partner store and the charm ledger, both recomputed on every read."""
        for member in ("TrainerCharmProgression.CharmSummary",
                       "TrainerPartnerPersistence.StateSummary"):
            ok, text = self.act("read.classforge", lambda m=member: drive.run(
                "crucible_get", [m]), member=member.split(".")[-1], at=tag)
            if ok and text:
                self.note("%s %s" % (tag, member.split(".")[-1]), text.strip().splitlines()[0])

    def partner_guids(self, rows):
        """Group-0 combatants that are NOT one of our four party classes -- i.e. summons.

        BAT_VAMPIRE_* is excluded on purpose: the Vampiric's Bat Swarm bat is ALSO group 0, and
        downing it would exercise a completely different feature while the summary claimed the
        Trainer's partner-persistence path had been tested.
        """
        party = set(PASS_A) | set(PASS_B)
        return [g for g, r in rows.items()
                if r.get("group") == "0" and (r.get("class") or "") not in party
                and not (r.get("class") or "").startswith("BAT_VAMPIRE")
                and r.get("dead") != "True"]

    # ---- GATED CONTENT -- the surface that had never been reached ---------------------------
    #
    # Everything below exists because "the abilities do not crash" was already established and the
    # gates were not. Each driver buys ONE gate with the cheapest legal currency:
    #   Bat Swarm     needs cf_gorged >= 3 AND a kill, in ONE fight (cf_gorged is NOT Persistent).
    #   Hellhowl      needs cf_bond >= 3;  Chaosmutt needs cf_bond >= 9. cf_bond IS Persistent and
    #                 rises 1 per KILL BY THE TRAINER, so it is bought across fights.
    #   Verdant Charm is driven through crucible_use_item, never crucible_use_ability, because the
    #                 Send Out abilities report usable=False and never reach the action menu.

    def counters(self, guid=None):
        """{name: int} of ClassForge recipe counters for one owner (or the whole board).

        Reads crucible_recipe_counters, whose format is
          ClassForge recipe counters (N entity/ies): | <guid>: cf_bond=3, cf_gorged=1
        Anything else -- "unreachable: ..." when no combat runtime exists yet -- parses to {}.
        """
        ok, text = self.act("read.recipe_counters", lambda: drive.run(
            "crucible_recipe_counters", [guid or "-"]), who=(guid or "-")[:8])
        out = {}
        if not ok or not text or "counters (" not in text:
            return out
        for chunk in text.split("|")[1:]:
            if ":" not in chunk:
                continue
            owner, values = chunk.split(":", 1)
            owner = owner.strip()
            for pair in values.split(","):
                if "=" not in pair:
                    continue
                name, value = pair.split("=", 1)
                try:
                    out.setdefault(owner, {})[name.strip()] = int(value.strip())
                except ValueError:
                    pass
        if guid:
            for owner, values in out.items():
                if owner.lower() == guid.lower():
                    return values
            return {}
        merged = {}
        for values in out.values():
            for name, value in values.items():
                merged[name] = max(merged.get(name, 0), value)
        return merged

    def party_slots(self):
        """[classId] in slot order, from crucible_party_list."""
        ok, listing = self.act("party.slots", lambda: drive.run("crucible_party_list", []))
        if not ok or not listing:
            return []
        return [m.group(1) for m in re.finditer(r"classId=(\S+)", listing)]

    def party_guids(self):
        """{classId: guid} from crucible_party_list -- the roster, not the combat board."""
        ok, listing = self.act("party.guids", lambda: drive.run("crucible_party_list", []))
        out = {}
        if not ok or not listing:
            return out
        for line in listing.splitlines():
            match = re.search(r"classId=(\S+).*guid=(\S+)", line)
            if match:
                out[match.group(1)] = match.group(2)
        return out

    def board_guid(self, rows, class_id):
        return next((g for g, r in rows.items()
                     if r.get("class") == class_id and r.get("dead") != "True"), None)

    def record_bodies(self, rows, tag):
        """Note WHICH summoned bodies are standing on the board. This is how Hellhowl, Chaosmutt
        and the Vampiric's bat get credited -- by their config appearing on OUR side of a live
        fight, not by a recipe merely being legal."""
        for row in rows.values():
            if row.get("group") != "0":
                continue
            config = row.get("class") or ""
            if config in GRASS_STAGES:
                self.mark("trainer.%s" % config,
                          "%s on the board in %s (%s)"
                          % (GRASS_STAGES[config], tag, row.get("line", "")[:110]))
            elif config.startswith("BAT_VAMPIRE"):
                self.mark("vampiric.bat_swarm",
                          "%s on OUR side in %s (%s)" % (config, tag, row.get("line", "")[:110]))

    def trainer_kill(self, tag):
        """One kill CREDITED TO THE TRAINER, which is the only thing that raises cf_bond.

        crucible_kill_target does NOT poke a health field: it picks one of the KILLER's own
        abilities whose legal tiles reach the target, rejects the zero-damage ones by
        GetMinAndMaxDamageOfAbilityForCharacter, and fires it until the target drops. So ON_KILL
        fires for the killer exactly as it would in play -- which is what makes this a legitimate
        way to buy a bond band rather than a state poke that would prove nothing.
        """
        if not drive.in_combat():
            return None
        self.ensure_enemies(1)
        rows, _text = self.combatants()
        trainer = self.board_guid(rows, "CF_ORIG_TRAINER")
        if trainer is None:
            self.note("%s bond" % tag, "the Trainer is not on this board -- no kill to credit")
            return None
        victim = next((g for g, r in rows.items()
                       if r.get("group") != "0" and r.get("dead") != "True"), None)
        if victim is None:
            return None
        self.act("bond.trainer_kill", lambda: drive.run(
            "crucible_kill_target", [victim, trainer]),
            victim=(rows[victim].get("class") or victim[:8]), killer="CF_ORIG_TRAINER", fight=tag)
        time.sleep(1.0)
        bond = self.counters(trainer).get("cf_bond")
        if bond is not None:
            self.note("%s cf_bond" % tag, bond)
            if bond >= 3:
                self.mark("trainer.bond>=3", "cf_bond=%d (Hellhowl band open) at %s" % (bond, tag))
            if bond >= 9:
                self.mark("trainer.bond>=9", "cf_bond=%d (Chaosmutt band open) at %s" % (bond, tag))
        return bond

    def pump_bond(self, tag, kills):
        """`kills` Trainer kills in this fight, topping the enemy side up between them."""
        bond = None
        for index in range(kills):
            if not drive.in_combat():
                self.note("%s bond" % tag, "combat ended after %d bond kill(s)" % index)
                break
            bond = self.trainer_kill(tag)
        return bond

    def gorge_and_swarm(self, tag):
        """Buy Bat Swarm: three Engorged procs, then a kill BY THE VAMPIRIC.

        cf_gorged carries NO Persistent flag, so unlike cf_bond it resets every combat -- all
        three gorges and the kill have to happen in ONE fight. Engorged is ON_DAMAGE_DEALT,
        ONCE_PER_ROUND, gated HP_THRESHOLD GTE 70%, and Blood Price bleeds the Vampiric 3 HP on
        every hostile action, so the party is healed between rounds to keep the gate open. That is
        a fixture convenience, not a bypass: the gate is still evaluated, it is simply satisfied.
        """
        if not drive.in_combat():
            return
        rows, _text = self.combatants()
        vamp = self.board_guid(rows, "CF_ORIG_VAMPIRIC")
        if vamp is None:
            self.note("%s bat-swarm" % tag, "the Vampiric is not on this board")
            return
        for round_index in range(1, 10):
            if not drive.in_combat():
                break
            self.ensure_enemies(2)
            self.act("swarm.heal_party", lambda: drive.run("crucible_heal_party", []),
                     why="keep HP_THRESHOLD>=70pct open for Engorged", round=round_index)
            rows, text = self.combatants()
            victim = next((g for g, r in rows.items()
                           if r.get("group") != "0" and r.get("dead") != "True"), None)
            if victim is None:
                continue
            gorged = self.counters(vamp).get("cf_gorged", 0)
            self.note("%s cf_gorged" % tag, gorged)
            if gorged >= 3:
                self.mark("vampiric.gorged>=3", "cf_gorged=%d at %s" % (gorged, tag))
                # The gate is open; the SUMMON is ON_KILL, and CombatHelper's ADD_CHARACTER case
                # only places on a tile a body just vacated -- so the kill IS the summon.
                self.act("swarm.vampiric_kill", lambda: drive.run(
                    "crucible_kill_target", [victim, vamp]),
                    killer="CF_ORIG_VAMPIRIC", why="ON_KILL -> SKILL_CF_VAMPIRIC_BAT_SWARM")
                time.sleep(2.0)
                rows, _t = self.combatants()
                self.record_bodies(rows, tag)
                return
            # Not gorged enough yet: swing for damage (ON_DAMAGE_DEALT is what Engorged watches).
            self.act("swarm.vampiric_hit", lambda: drive.run(
                "crucible_kill_target", [victim, vamp]),
                killer="CF_ORIG_VAMPIRIC", round=round_index, why="ON_DAMAGE_DEALT -> Engorged")
            time.sleep(1.2)
            self.act("swarm.end_turn", lambda: drive.run("crucible_combat_end_turn", []),
                     round=round_index)
            time.sleep(0.8)
        self.note("%s bat-swarm" % tag,
                  "cf_gorged never reached 3 in this fight -- Bat Swarm NOT reached here")

    def send_out_charm(self, tag):
        """THE OPEN CRASH QUESTION: does the Verdant Charm throw when the partner is HEALTHY?

        A guard shipped for the DOWNED case. The healthy case was never driven, and it matters
        because the Send Out abilities (CF_TRAINER_SUMMON_GRASS_T*_ATTACK) are PACK-AUTHORED ids
        on a body -- the exact shape AGENT-BRIEF s7 / checklist A2 calls a hard NRE hazard, since
        GetCharacterAbilityRecord is dereferenced unguarded at CharacterVisualHelper.cs:2567.

        Driven through crucible_use_item, NOT crucible_use_ability: iso-ash-commands.json:12
        records those abilities reporting usable=False and never appearing in the action menu, so
        a use_ability route would report a clean "no crash" from a path that never ran.

        Returns "SAFE" / "CRASHES" / "UNANSWERED".
        """
        if not drive.in_combat():
            return "UNANSWERED"
        # crucible_use_item resolves the item on the ACTIVE COMBATANT. MEASURED 2026-08-26: driving
        # it while anyone else held the turn returned
        #   "error: the active combatant is not carrying 'ARM_ORIG_TRAINER_BALL_GRASS'"
        # and the first version of this driver scored that as SAFE -- exactly the false negative
        # the brief warns about, a "no crash" verdict from a path that never ran. So the Trainer is
        # put on the clock FIRST, and a result that starts with "error:" is UNANSWERED, never SAFE.
        if not self.make_active("CF_ORIG_TRAINER", tag):
            self.note("%s charm" % tag,
                      "could not put the Trainer on the clock -- crucible_use_item resolves the "
                      "item on the ACTIVE combatant, so the charm was NOT driven and the "
                      "healthy-partner question is UNANSWERED")
            return "UNANSWERED"
        rows, _text = self.combatants()
        # A HEALTHY partner is the whole question -- if the partner is downed or absent this is
        # the already-guarded case and answers nothing.
        partner = next((r for r in rows.values()
                        if (r.get("class") or "") in GRASS_STAGES and r.get("dead") != "True"),
                       None)
        if partner is None:
            self.note("%s charm" % tag,
                      "no LIVING GRASS partner on the board -- the healthy-partner question is "
                      "NOT answered by this attempt")
            return "UNANSWERED"
        tile = partner.get("tile") or (0, 0)
        before = len(self.watch.findings)
        ran = False
        for ability in SEND_OUT_ABILITIES:
            ok, result = self.act("charm.use_item", lambda a=ability, t=tile: drive.run(
                "crucible_use_item", [GRASS_CHARM, str(t[0]), str(t[1]), a]),
                item=GRASS_CHARM, ability=ability, partner=partner.get("class"),
                partnerHp=partner.get("hp"), question="healthy-partner charm crash")
            time.sleep(1.5)
            text = (result or "(no result)")
            self.note("%s charm(%s)" % (tag, ability), text)
            if ok and not str(text).strip().lower().startswith("error:"):
                ran = True
        if len(self.watch.findings) > before:
            verdict = "CRASHES"
        elif ran:
            verdict = "SAFE"
        else:
            verdict = "UNANSWERED"
        self.mark("trainer.verdant_charm_healthy", "%s (partner %s hp=%s)"
                  % (verdict, partner.get("class"), partner.get("hp")))
        return verdict

    def drive_class_turns(self, class_id, ability, turns, tag):
        """Give ONE class the clock `turns` times and fire a NAMED ability each time.

        drive_rounds takes whoever happens to be up, which for a six-body board means a given
        class swings roughly once every six turns -- far too thin to buy a 15%-per-round proc or
        a CRIT_FAIL. This is the targeted version, used for the Chaos Mage, whose entire surface
        (Entropy on PERFECT, Backlash on CRIT_FAIL, Resonance, Wild Magic Surge at 15%/round) is
        bought with repeated rolls and nothing else.
        """
        fired = 0
        for index in range(1, turns + 1):
            if not drive.in_combat():
                break
            self.ensure_enemies(2)
            if not self.make_active(class_id, tag):
                self.note("%s %s" % (tag, class_id),
                          "never got the clock in %d passes -- %d turn(s) driven" % (14, fired))
                break
            rows, text = self.combatants()
            victim = next((r for g, r in rows.items()
                           if r.get("group") != "0" and r.get("dead") != "True"), None)
            if victim is None:
                continue
            tile = victim.get("tile") or (0, 0)
            ok, result = self.act("class.use_ability", lambda a=ability, t=tile: drive.run(
                "crucible_use_ability", [a, str(t[0]), str(t[1])]),
                cls=class_id, ability=ability, turn=index, fight=tag)
            if ok and not str(result or "").strip().lower().startswith("error:"):
                fired += 1
                self.turns_by_class[class_id] = self.turns_by_class.get(class_id, 0) + 1
            time.sleep(1.2)
            self.act("class.end_turn", lambda: drive.run("crucible_combat_end_turn", []),
                     cls=class_id, turn=index)
            time.sleep(0.8)
            _rows, snap = self.combatants()
            hazards = self.tile_hazards(snap)
            if hazards:
                self.note("%s tile hazards" % tag,
                          ", ".join("%s %s" % (xy, st) for xy, st in hazards[:6]))
                if class_id == "CF_ORIG_CHAOSMAGE":
                    self.mark("chaosmage.wild_magic_surge",
                              "tile hazard after a Chaos Mage turn in %s: %s"
                              % (tag, ", ".join("%s %s" % (xy, st) for xy, st in hazards[:4])))
        self.note("%s %s turns" % (tag, class_id), "%d ability/ies actually fired" % fired)
        return fired

    def make_active(self, class_id, tag, max_passes=14):
        """End turns until `class_id` holds the clock. Returns True once it does.

        Needed by anything that acts THROUGH a specific character -- crucible_use_item and
        crucible_use_ability both resolve against the active combatant, so driving them on someone
        else's turn produces a refusal that reads exactly like a clean run.
        """
        for _ in range(max_passes):
            if not drive.in_combat():
                return False
            rows, text = self.combatants()
            active = self.active_guid(text)
            if active and (rows.get(active) or {}).get("class") == class_id:
                return True
            self.act("turn.pass_to", lambda: drive.run("crucible_combat_end_turn", []),
                     waitingFor=class_id, fight=tag)
            time.sleep(0.9)
        return False

    def try_capture(self, config, expectation, tag):
        """
        Spawn `config`, put Gary on the clock, and throw the capture ball at it.

        `expectation` is "capture" or "refusal". A refusal is NOT a failure of this soak -- it is
        the path being tested. What would be a failure is a stack trace, which the act() wrapper
        picks up either way. The untagged case matters most: with no PLAYTHING_* tag,
        GetEnemyDoll returns null and CreateThing(null) reaches a dictionary lookup on a null key,
        so "refused cleanly" here is the difference between a guard and a hard crash.
        """
        self.act("capture.spawn_target", lambda: drive.run(
            "crucible_combat_spawn", [config, "1", "1"]), config=config, expect=expectation)
        time.sleep(1.5)

        for attempt in range(1, self.options.capture_attempts + 1):
            if not drive.in_combat():
                self.note("%s capture" % tag, "combat ended before the throw")
                return
            rows, text = self.combatants()
            target = next((r for g, r in rows.items()
                           if r.get("class") == config and r.get("dead") != "True"), None)
            if target is None:
                self.note("%s capture" % tag, "%s is not on the board" % config)
                return
            gary = next((g for g, r in rows.items() if r.get("class") == "CF_ORIG_GARY"), None)
            if gary is None:
                self.note("%s capture" % tag, "Gary is not in this fight -- SKILL_CF_TRAINER_"
                                              "CAPTURE_CATCH is OWNED, so it correctly cannot fire")
                return
            if self.active_guid(text) != gary:
                self.act("capture.pass_turn", lambda: drive.run("crucible_combat_end_turn", []),
                         attempt=attempt, waitingFor="CF_ORIG_GARY")
                time.sleep(0.8)
                continue

            tile = target.get("tile") or (0, 0)
            self.act("capture.throw_ball", lambda t=tile: drive.run(
                "crucible_use_ability", [CAPTURE_ABILITY, str(t[0]), str(t[1])]),
                ability=CAPTURE_ABILITY, recipe="SKILL_CF_TRAINER_CAPTURE_CATCH",
                target=config, expect=expectation, attempt=attempt)
            time.sleep(1.5)
            self.act("capture.end_turn", lambda: drive.run("crucible_combat_end_turn", []),
                     attempt=attempt)

            ok, ball = self.act("capture.read_record", lambda: drive.run(
                "crucible_get", ["TrainerPartnerPersistence.StateSummary"]),
                target=config, expect=expectation)
            if ok and ball:
                head = ball.strip().splitlines()[0]
                self.note("%s capture(%s -> %s)" % (tag, config, expectation), head)
                if config in head:
                    if expectation == "capture":
                        self.mark("gary.capture",
                                  "%s is bound to %s: %s" % (config, CAPTURE_BALL, head[:150]))
                    else:
                        # A config that was supposed to be REFUSED showing up in the ball is a
                        # real finding, not a success -- say so instead of marking a gate.
                        self.note("%s CAPTURE GUARD BREACH" % tag,
                                  "%s was expected to be REFUSED and is in the ball: %s"
                                  % (config, head[:150]))
                    return
        self.note("%s capture(%s)" % (tag, config),
                  "no capture observed in %d throw(s) -- expected for %s"
                  % (self.options.capture_attempts, expectation))

    def down_a_partner(self, tag):
        """
        Kill Ash's partner and confirm the run survives it.

        DOWNED is recorded through the KillCharacter hook (SetToMaxHealth and KillCharacter write
        CurrentHealth DIRECTLY and bypass the AddHealth funnel, which is why that hook exists at
        all). The partner must then NOT be sent out again until a town revives it -- so this is
        driven before the town visit, not after.
        """
        rows, _text = self.combatants()
        partners = self.partner_guids(rows)
        if not partners:
            self.note("%s partner-down" % tag, "no partner on the board to down")
            return
        enemy = next((g for g, r in rows.items()
                      if r.get("group") != "0" and r.get("dead") != "True"), None)
        if enemy is None:
            self.note("%s partner-down" % tag, "no living enemy to credit the kill to")
            return
        victim = partners[0]
        self.act("partner.kill", lambda: drive.run(
            "crucible_kill_target", [victim, enemy]),
            victim=(rows[victim].get("class") or victim[:8]), killer=enemy[:8])
        time.sleep(2.0)
        self.read_trainer_state("%s after-partner-down" % tag)

    def visit_town(self, tag):
        """
        Walk to a NAMED (non-GUID) encounter and open its service menu.

        Named ids -- TAVERN, AF_TOWN_B and friends -- are venues; the bare-GUID ones are roaming
        monsters. The town revive hooks ServiceMenuViewHelper.Show gated to eEncounterTypes.TOWN,
        so opening the menu IS the trigger for the partner revive path.
        """
        ok, listing = self.act("town.map_encounters", lambda: drive.run(
            "crucible_map_encounters", ["-"]), leg=tag)
        if not ok or not listing:
            return
        hero = None
        ok, state = self.act("town.state", lambda: drive.run("crucible_overworld_state", []),
                             leg=tag)
        if ok and state:
            match = re.search(r"\[0\] \S+ hex=\((-?\d+), (-?\d+)\)", state)
            if match:
                hero = (int(match.group(1)), int(match.group(2)))
        towns = []
        for line in listing.splitlines():
            match = re.match(r"\[(-?\d+),(-?\d+)\] (\S+)", line.strip())
            if not match:
                continue
            name = match.group(3)
            if re.match(r"^[0-9a-f]{8}-", name):
                continue                                  # a roaming monster, not a venue
            x, y = int(match.group(1)), int(match.group(2))
            distance = max(abs(x - hero[0]), abs(y - hero[1])) if hero else 0
            towns.append((distance, x, y, name))
        towns.sort()
        if not towns:
            self.note("%s town" % tag, "no named venue on this map -- town visit NOT covered")
            return
        _d, x, y, name = towns[0]
        self.act("town.move", lambda: drive.run(
            "crucible_move", [str(x), str(y), "false", "false", "true"]),
            venue=name, to="%d,%d" % (x, y))
        self.act("town.settle", lambda: drive.wait_ready(timeout_seconds=120), venue=name)
        # drive.interact refuses the branches that dereference EncounterActionContext.Layout;
        # VENUE is not one of them, and on a town it opens the service menu.
        self.act("town.open_services", lambda: drive.interact("VENUE"), venue=name)
        time.sleep(2.0)
        self.read_trainer_state("%s in-town" % tag)
        self.act("town.leave", lambda: drive.close_encounter_panel(), venue=name)
        self.act("town.after", lambda: drive.wait_ready(timeout_seconds=120), venue=name)

    # ---- one pass ----------------------------------------------------------------------------

    def run_pass(self, name, classes, class_data):
        print("\n" + "=" * 92)
        print("PASS %s -- %s" % (name, ", ".join(classes)))
        print("=" * 92)

        if not self.load_fixture():
            self.failed_actions.append(("pass.%s.load" % name, "fixture load failed"))
            return
        self.build_party(classes, class_data)
        self.read_trainer_state("pass%s start" % name)

        chaos = "CF_ORIG_CHAOSMAGE" in classes
        target = self.options.combats
        for index in range(1, target + 1):
            if self.aborted:
                return
            tag = "%s#%d" % (name, index)
            print("\n--- fight %s (%d/%d) ---" % (tag, index, target))
            if not self.start_combat(tag):
                self.note(tag, "could not reach combat; traversing and trying the next one")
                self.traverse(tag)
                continue
            self.combats += 1

            # WHICH BODIES ARE ACTUALLY STANDING THERE. Read at the START of every fight,
            # because the GRASS partner arrives ON_COMBAT_START and the bond band that decides
            # WHICH of Barkling / Hellhowl / Chaosmutt is sent is evaluated at that instant. This
            # is the only thing that can credit the two stages that had never appeared in a game.
            rows, _text = self.combatants()
            self.record_bodies(rows, tag)

            # Fight 1 -- baseline. Ash's partners are summoned ON_COMBAT_START, so this is where
            # the summon path first runs and where the starting HP for the carry-over check comes
            # from.
            if index == 1:
                self.read_trainer_state("%s combat-start" % tag)

            # Fight 2 -- the carry-over. The partner's HP is stored on the ball's CustomData and
            # is supposed to persist between fights, so a partner that ended fight 1 hurt must
            # come back hurt rather than fresh.
            if index == 2:
                self.read_trainer_state("%s carry-over" % tag)
                # THE CHARM QUESTION GOES FIRST. Driven after drive_rounds it kept reporting
                # "no LIVING GRASS partner on the board" -- six driven rounds is long enough for a
                # 16 HP stage-1 partner to die, and a dead partner is the case the shipped guard
                # already covers. At combat start it is at full HP, which is the case nobody has
                # ever driven.
                verdict = self.send_out_charm(tag)
                self.note("%s VERDANT CHARM (healthy partner)" % tag, verdict)

            self.drive_rounds(self.options.rounds, tag)

            # ---- THE GATED CONTENT, bought in the order the gates allow -----------------------
            # Fight 2 -- the open crash question. The partner summoned at combat start is at full
            # HP here, which is exactly the HEALTHY case nobody had ever driven.
            if index == 2:
                # Buy the Hellhowl band, so the NEXT fight's ON_COMBAT_START sends stage 2.
                self.pump_bond(tag, 4)
            # Fight 3 -- Hellhowl should be on the board above; now buy the Chaosmutt band.
            if index == 3:
                self.pump_bond(tag, 6)
                self.try_capture(CAPTURE_ENEMY, "capture", tag)

            # Fight 4 -- capture a BOSS. A refusal is the correct outcome; a crash is not.
            if index == 4:
                self.try_capture(BOSS_ENEMY, "refusal", tag)
            # Fight 5 -- capture an UNTAGGED config, the branch whose unguarded form crashes.
            if index == 5:
                self.try_capture(UNTAGGED_ENEMY, "refusal", tag)
            # Fight 6 -- RANDOM_TILE. Chaos Mage's Wild Magic Surge is the pack's ONLY surviving
            # RANDOM_TILE source: ON_TURN_START, ProcChance 15, ONCE_PER_ROUND, so it is bought
            # with ROUNDS. On pass A there is nothing to observe at all -- the Fire line that
            # carried SKILL_CF_TRAINER_FIRE_WILDFIRE was deleted -- and the note says so rather
            # than reporting a clean board as a passing tile test.
            if index == 6:
                self.drive_rounds(self.options.rounds + 4,
                                  "%s %s" % (tag, "wild-magic" if chaos else "extra-rounds"))
                _rows, text = self.combatants()
                hazards = self.tile_hazards(text)
                if hazards:
                    self.note("%s RANDOM_TILE" % tag,
                              "%d hazard tile(s): " % len(hazards)
                              + ", ".join("%s %s" % (xy, st) for xy, st in hazards[:8]))
                    if chaos:
                        self.reached["chaosmage.wild_magic_surge"] = (
                            "tile hazard(s) on the board: "
                            + ", ".join("%s %s" % (xy, st) for xy, st in hazards[:4]))
                elif chaos:
                    self.note("%s RANDOM_TILE" % tag, "no tile hazard observed this fight")
                else:
                    self.note("%s RANDOM_TILE" % tag,
                              "pass A has NO RANDOM_TILE source at all (the Fire line and "
                              "SKILL_CF_TRAINER_FIRE_WILDFIRE were deleted) -- not a tile test")
            # Fight 7 -- down a partner, then leave. The town visit after this fight is what
            # should revive it.
            if index == 7:
                self.down_a_partner(tag)
            # Fight 8 -- BAT SWARM, the Vampiric gate that has never fired. cf_gorged is NOT
            # persistent, so the three gorges and the kill all have to land inside this one fight.
            # Pass B has no Vampiric, so the driver reports it cannot be answered there.
            if index == 8:
                if "CF_ORIG_VAMPIRIC" in classes:
                    self.gorge_and_swarm(tag)
                else:
                    self.note("%s bat-swarm" % tag,
                              "pass B has no Vampiric -- Bat Swarm is a pass A question")
            # Fight 9 -- a long fight. Chaos Mage's Wild Magic Surge is 15%/round ONCE_PER_ROUND
            # and Backlash needs a CRIT_FAIL, so both are bought with rounds and nothing else.
            if index == 9:
                if chaos:
                    # MAGIC_DARK_ATTACK is the hostile INT ability on the Entropy Rod (a STAFF),
                    # which is the exact shape Entropy's conditions require: WEAPON_CLASS STAFF,
                    # ABILITY_STAT INT, HOSTILE_ACTION. Every one of these rolls is also a chance
                    # at a CRIT_FAIL for Backlash, and every turn START is a 15% Wild Magic Surge.
                    self.drive_class_turns("CF_ORIG_CHAOSMAGE", "MAGIC_DARK_ATTACK",
                                           self.options.rounds + 8, "%s chaos" % tag)
                else:
                    self.drive_rounds(self.options.rounds + 6, "%s long" % tag)
                rows, _t = self.combatants()
                self.record_bodies(rows, tag)

            self.end_combat(tag)
            self.traverse(tag)

            if index == 7:
                self.visit_town(tag)
                self.read_trainer_state("%s post-town" % tag)
            if index in (2, 8):
                self.read_trainer_state("%s between-fights" % tag)

        self.read_trainer_state("pass%s end" % name)
        self.act("quest.state", lambda: drive.run("crucible_quest_state", []), at="pass%s end" % name)

    # ---- reporting ----------------------------------------------------------------------------

    def summary(self):
        print("\n" + "=" * 92)
        print("SOAK SUMMARY")
        print("=" * 92)
        print("combats driven : %d" % self.combats)
        print("actions driven : %d" % self.actions)
        print("game restarts  : %d" % self.restarts)
        print("driver failures: %d" % len(self.failed_actions))
        print("log hits       : %d raw, %d distinct" % (self.watch.total_hits, len(self.watch.findings)))
        if self.watch.unreadable_windows:
            print("WARNING: %d action window(s) could not read %s -- those windows were NOT scanned"
                  % (self.watch.unreadable_windows, PLAYER_LOG))

        # A proc line is the LOG half of the paired evidence, and for a gate with no body and no
        # counter it is the only half there is. Recorded WITHOUT a screenshot and labelled as such,
        # so it can never be mistaken for the on-board observations above.
        for key, recipe in sorted(PROC_GATES.items()):
            count = self.watch.procs.get(recipe)
            if count and key not in self.reached:
                self.reached[key] = "proc-log only: %s fired x%d (no screenshot)" % (recipe, count)

        print("\n--- GATED CONTENT REACHED ---")
        for key in sorted(GATES):
            hit = self.reached.get(key)
            print("  %-38s %s" % (key, hit if hit else "NOT REACHED"))
            if self.shots.get(key):
                print("  %-38s   shot: %s" % ("", self.shots[key]))
        extra = sorted(k for k in self.reached if k not in GATES)
        for key in extra:
            print("  %-38s %s" % (key, self.reached[key]))

        print("\n--- REAL TURNS DRIVEN, BY BODY ---")
        if not self.turns_by_class:
            print("  none. No class ever took a driven turn, which voids every class claim below.")
        for config in sorted(self.turns_by_class, key=lambda c: -self.turns_by_class[c]):
            print("  %-28s %d" % (config, self.turns_by_class[config]))

        print("\n--- RECIPES THAT ACTUALLY FIRED (ClassForge proc lines) ---")
        if not self.watch.procs:
            print("  none. Either nothing procced, or the recipe engine never logged -- which is")
            print("  itself a finding, since RecipeDispatcher.cs:481 logs EVERY proc.")
        else:
            for name in sorted(self.watch.procs, key=lambda n: -self.watch.procs[n]):
                print("  x%-6d %s" % (self.watch.procs[name], name))

        if self.notes:
            print("\n--- observations ---")
            for label, text in self.notes:
                print("  %-34s %s" % (label[:34], str(text)[:190]))

        if self.failed_actions:
            print("\n--- driver failures (a refused/failed COMMAND, not necessarily a game bug) ---")
            counts = {}
            for action, error in self.failed_actions:
                counts.setdefault(action, []).append(error)
            for action in sorted(counts):
                print("  %-34s x%d" % (action[:34], len(counts[action])))
                print("      %s" % counts[action][0][:200])

        new = [r for r in self.watch.findings.values() if not r["preexisting"]]
        old = [r for r in self.watch.findings.values() if r["preexisting"]]

        print("\n--- NEW EXCEPTIONS, by type, attributed ---")
        if not new:
            print("  none. Every log window after every driven action was clean of everything")
            print("  outside the known-noise allowlist and the pre-existing baseline.")
        else:
            _print_findings(new)

        if old:
            print("\n--- PRE-EXISTING (already in the log before the soak began) ---")
            print("  These are real, unfiltered signal, but they PREDATE this run, so they do not")
            print("  decide the verdict. They are printed in full because they still deserve a fix.")
            _print_findings(old)

        print("\n--- coverage caveats ---")
        print("  * QuestRewardAudit / 'mismatch detected' / issues= are grepped for, but neither")
        print("    token exists in this repo's sources nor appeared in the live Player.log")
        print("    (both counts 0 on 2026-08-25). A clean soak is NOT evidence that auditor ran.")
        print("  * This is single-player. Replication/desync between PEERS is not exercised at all;")
        print("    RANDOM_TILE's seeded-RNG contract can only be proven with two clients.")
        print("  * Feature CORRECTNESS is traits.py's job. A PASS here means nothing THREW.")

        clean = not new
        enough = self.combats >= self.options.combats
        verdict = clean and enough and not self.aborted
        print("\n%s" % ("PASS" if verdict else "FAIL"))
        if not clean:
            print("  reason: %d NEW unfiltered exception/error signature(s) above"
                  % len(new))
        if not enough:
            print("  reason: only %d combat(s) driven, wanted at least %d"
                  % (self.combats, self.options.combats))
        if self.aborted:
            print("  reason: %s" % self.aborted)
        return verdict

    def report(self):
        return {
            "combats": self.combats,
            "actions": self.actions,
            "restarts": self.restarts,
            "aborted": self.aborted,
            "unreadableLogWindows": self.watch.unreadable_windows,
            "driverFailures": [{"action": a, "error": e} for a, e in self.failed_actions],
            "notes": [{"label": l, "text": str(t)} for l, t in self.notes],
            "reached": dict(self.reached),
            "unreached": [k for k in GATES if k not in self.reached],
            "procs": dict(self.watch.procs),
            "turnsByClass": dict(self.turns_by_class),
            "shots": dict(self.shots),
            "findings": sorted(self.watch.findings.values(), key=lambda r: -r["count"]),
        }


# =============================================================================================

def _print_findings(records):
    """One findings block, grouped by exception type, heaviest type first."""
    by_kind = {}
    for record in records:
        by_kind.setdefault(record["kind"], []).append(record)
    for kind in sorted(by_kind, key=lambda k: -sum(r["count"] for r in by_kind[k])):
        group = by_kind[kind]
        print("\n  %s  (%d distinct, %d total)"
              % (kind, len(group), sum(r["count"] for r in group)))
        for record in sorted(group, key=lambda r: -r["count"]):
            context = " ".join("%s=%s" % (k, v)
                               for k, v in sorted(record["firstContext"].items()))
            print("    x%-4d after: %s%s"
                  % (record["count"], record["firstAction"],
                     ("  [%s]" % context) if context else ""))
            print("          %s" % record["headline"][:200])
            for frame in record["frames"]:
                print("            %s" % frame[:170])
            if record["alsoAfter"]:
                print("          also after: %s" % ", ".join(record["alsoAfter"]))


def load_json(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def starter_weapon(class_data, class_id):
    """The class's ARM_ORIG_STARTER_* Thing. Read from the pack rather than from
    class-starter-weapons.json, which carries the EOR classes only and has no CF_ORIG_* rows."""
    things = (class_data.get(class_id) or {}).get("Things") or {}
    for name in sorted(things):
        if name.startswith("ARM_ORIG_STARTER_"):
            return name
    return None


def index_of(classes, class_id):
    return classes.index(class_id) if class_id in classes else None


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("--combats", type=int, default=10,
                        help="fights to drive PER PASS (default 10; the scripted beats occupy "
                             "fights 1-7)")
    parser.add_argument("--rounds", type=int, default=4,
                        help="turns driven inside each fight (default 4)")
    parser.add_argument("--capture-attempts", type=int, default=4,
                        help="ball throws per capture scenario (default 4)")
    parser.add_argument("--pass", dest="which", choices=["A", "B", "both"], default="both",
                        help="A = the four-class party; B = Chaos Mage swapped in for the "
                             "Vampiric (four slots is the hard ceiling); default both")
    parser.add_argument("--json", dest="json_out", help="write the full report here")
    parser.add_argument("--trace", action="store_true",
                        help="print a python traceback for unexpected driver errors")
    options = parser.parse_args()

    if not drive.boot():
        print("SKIP: the game is not running (no RPC pump on %s)" % drive.BASE)
        return 2

    class_data = load_json(os.path.join(PACK, "classes.json"))
    missing = [c for c in set(PASS_A) | set(PASS_B) if c not in class_data]
    if missing:
        print("SKIP: these classes are not in %s: %s" % (PACK, ", ".join(sorted(missing))))
        return 2

    # A run that ENDS -- win OR lose -- rewrites and can DELETE its own save, and this soak drives
    # runs hard for hours. If no protected copy exists yet, make one BEFORE the first pass; after
    # that, restore_fixture has something to put back. drive.backup_fixture refuses to copy an
    # already-damaged fixture, so a bad save can never overwrite a good backup.
    backup = os.path.join(drive.FIXTURE_BACKUPS, BASE_RUN_ID + ".ftk2")
    if not os.path.exists(backup):
        made, detail = drive.backup_fixture(BASE_RUN_ID)
        print("soak: no protected copy existed -- %s" % detail)
        if not made:
            print("soak: REFUSING to start. The base fixture is not backed up and not healthy, so")
            print("      a pass that ends the run would destroy it with nothing to restore from.")
            return 2

    soak = Soak(options)
    print("soak: log baseline at byte %s of %s" % (soak.watch.offset, PLAYER_LOG))
    print("soak: base fixture %s, %d combat(s) x %d round(s) per pass"
          % (BASE_RUN_ID, options.combats, options.rounds))

    passes = []
    if options.which in ("A", "both"):
        passes.append(("A", PASS_A))
    if options.which in ("B", "both"):
        passes.append(("B", PASS_B))

    for name, classes in passes:
        if soak.aborted:
            break
        try:
            soak.run_pass(name, classes, class_data)
        except Exception as exc:                              # noqa: BLE001 - never abort the soak
            soak.failed_actions.append(("pass.%s" % name, "%s: %s" % (type(exc).__name__, exc)))
            print("pass %s blew up: %s: %s" % (name, type(exc).__name__, exc))
            if options.trace:
                traceback.print_exc()

    verdict = soak.summary()

    if options.json_out:
        with open(options.json_out, "w", encoding="utf-8") as handle:
            json.dump(soak.report(), handle, indent=1, sort_keys=True, default=str)
        print("wrote %s" % options.json_out)

    return 0 if verdict else 1


if __name__ == "__main__":
    sys.exit(main())
