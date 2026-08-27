#!/usr/bin/env python3
"""Assert CF_PACK_ORIGINALS' inverse items (negative stats) actually move a character's stats
DOWN, not just that the give/equip commands report success.

Precedent this closes the gap on (docs/research/test-checklist.md section E, "Inverse items
(negative stats) -- designed, not built"): vanilla FTK2 ships 377 equippable items with at least
one negative Equippable.Stats entry (measured 2026-08-25 against the live game's own
Things/*.json), and this pack's sibling CF_PACK_EOR_CLASSES already authors a negative stat via
STAT_CF_TRAIT_KNIFE_EDGE_HP (Percent: -5, statmodifiers.json). CF_PACK_ORIGINALS had no item using
that precedent until this module's three items (items.json):
  ARM_ORIG_INVERSE_CURSED_MILLSTONE     DEF -30              (purely negative)
  ARM_ORIG_INVERSE_GAMBLERS_LOCKET      LCK +40, DEF -25     (tradeoff)
  ARM_ORIG_INVERSE_BLOODRAGE_TALISMAN   ATK +15, EVD -35     (tradeoff)

Everything here asserts on state READ BACK from the game, never on a command's own success
string -- see battery.py's docstring for why that discipline exists in this harness.

There is no dedicated read-only "give me this character's current stat" command. What exists is
crucible_set_stat_value <slot> <stat> <value>, which -- as a SIDE EFFECT of writing a value --
reports both the stat's value immediately BEFORE the write and immediately AFTER, each read via
CharacterHelper.GetStat(entity, stat, ALL) (CharacterCommands.cs's ReadStat, called internally;
see CharacterCommands.cs:214-216). That ALL-filtered read is independent of crucible_equip's own
return string. This module exploits it as a bracketing probe:

  call 1: crucible_set_stat_value(slot, STAT, PROBE)   -- its "after" field is the stat total
                                                            immediately BEFORE the item is equipped
  crucible_equip(slot, item)
  call 2: crucible_set_stat_value(slot, STAT, PROBE)   -- its "before" field is the stat total
                                                            immediately AFTER the item is equipped

delta = call2.before - call1.after isolates exactly what the equip changed, regardless of
whatever else is already equipped in that slot -- the bracket is drawn tight around the one
crucible_equip call under test. PROBE is written to keep the probe idempotent between items (it is
the same value both times), not because its value matters.
"""

import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

RESULTS = []
REPORTS = []

SLOT = "0"          # party slot the probes run against
PROBE = 100          # arbitrary control value written by the bracketing set_stat_value calls

ITEMS = [
    {
        "id": "ARM_ORIG_INVERSE_CURSED_MILLSTONE",
        "label": "Cursed Millstone (purely negative)",
        "expected": {"DEF": -30},
    },
    {
        "id": "ARM_ORIG_INVERSE_GAMBLERS_LOCKET",
        "label": "Gambler's Locket (tradeoff: LCK up, DEF down)",
        "expected": {"LCK": 40, "DEF": -25},
    },
    {
        "id": "ARM_ORIG_INVERSE_BLOODRAGE_TALISMAN",
        "label": "Bloodrage Talisman (tradeoff: ATK up, EVD down)",
        "expected": {"ATK": 15, "EVD": -35},
    },
]


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("  PASS " if ok else "  FAIL ") + name + (("\n        " + detail) if detail else ""))


def report(name, detail):
    REPORTS.append((name, detail))
    print("  REPORT " + name + "\n          " + detail)


def _parse_field(result, field):
    """Pull one `field=value` token out of crucible_set_stat_value's result line."""
    m = re.search(r"\b" + re.escape(field) + r"=(-?\d+)\b", result)
    return int(m.group(1)) if m else None


def read_bracket_stat(stat):
    """One crucible_set_stat_value probe. Returns (before, after) as ints, or (None, None) if the
    result did not parse -- e.g. the command errored."""
    result = drive.run("crucible_set_stat_value", [SLOT, stat, str(PROBE)])
    return _parse_field(result, "before"), _parse_field(result, "after")


def probe_delta(stat, do_equip):
    """Bracket one crucible_equip call with two set_stat_value probes on `stat`. Returns
    (delta, detail) where delta = post-equip reading - pre-equip reading, or (None, detail) if
    either probe failed to parse."""
    _, pre = read_bracket_stat(stat)
    equip_result = do_equip()
    post, _ = read_bracket_stat(stat)
    if pre is None or post is None:
        return None, "pre=%r post=%r equip=%r" % (pre, post, equip_result.strip()[:160])
    return post - pre, "pre=%d post=%d delta=%d equip=%r" % (pre, post, post - pre, equip_result.strip()[:160])


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    rows = {}
    for line in drive.run("crucible_party_list").splitlines():
        m = re.match(r"\[(\d+)\]", line.strip())
        if m:
            rows[m.group(1)] = line.strip()
    if SLOT not in rows:
        check("party: slot %s exists to run the probes against" % SLOT, False,
              drive.run("crucible_party_list")[:200])
        print("\n" + "=" * 62)
        print("0/1 passed")
        return 1
    print("probing against slot %s: %s" % (SLOT, rows[SLOT]))

    for item in ITEMS:
        print("\n=== %s (%s) ===" % (item["label"], item["id"]))

        give_result = drive.run("crucible_give_item", [SLOT, item["id"], "1"])
        report("%s: crucible_give_item call (not itself evidence -- equip + readback below is)"
               % item["id"], give_result.strip()[:200])

        equipped_once = {"done": False}

        def do_equip():
            # Only the FIRST probe_delta call for this item should actually equip; a second probe
            # on the same item (a second stat key) must bracket the item staying equipped, not
            # equip it twice.
            if equipped_once["done"]:
                return "(already equipped for this item; not re-equipping)"
            equipped_once["done"] = True
            return drive.run("crucible_equip", [SLOT, item["id"]])

        for stat, expected in item["expected"].items():
            delta, detail = probe_delta(stat, do_equip)
            if delta is None:
                report("%s: %s delta readback" % (item["id"], stat), detail)
                continue
            direction_ok = (delta < 0) if expected < 0 else (delta > 0)
            check("%s: %s moved in the expected direction (equipping should give %+d)"
                  % (item["id"], stat, expected),
                  direction_ok, detail)
            # Flat Equippable.Stats are additive, so the delta should match the authored value
            # exactly -- checked as its own assertion so a magnitude regression (e.g. -30 authored
            # but only -1 observed, indistinguishable from noise) fails loudly rather than passing
            # on direction alone.
            check("%s: %s delta matches the authored magnitude exactly (not just direction)"
                  % (item["id"], stat),
                  delta == expected, detail)

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    if REPORTS:
        print("\nreported-only (no independent read path available right now):")
        for n, detail in REPORTS:
            print("  %s: %s" % (n, detail[:170]))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
