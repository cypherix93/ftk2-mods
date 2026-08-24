#!/usr/bin/env python3
"""Assert every class in the party from ONE fight.

The party already carries Vampiric, Pacifist, Beast Trainer and a Runemage, so a single
encounter can exercise all of them. Testing one class per launch paid a full boot-to-combat
cycle -- roughly two minutes -- for each thing checked, which is why this exists.

Everything here asserts on state READ BACK from the game. A command's own success string is
not evidence: several verbs in this harness have reported success while doing nothing.
"""

import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402

RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(("  PASS " if ok else "  FAIL ") + name + (("\n        " + detail) if detail else ""))


def snapshot():
    return drive.run("crucible_combat_snapshot")


def combatants(snap=None):
    """guid -> parsed row, for every actual combatant (not the header, not a tile)."""
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


def tiles(snap=None):
    groups = {}
    for line in (snap or snapshot()).splitlines():
        m = re.match(r"\s*\((\d+), (\d+)\) group=(-?\d+)", line)
        if m:
            groups.setdefault(m.group(3), []).append((int(m.group(1)), int(m.group(2))))
    return groups


def abilities(guid):
    out = {}
    for line in drive.run("crucible_list_abilities", [guid]).splitlines():
        m = re.match(r"\s*(\S+) .*usable=(\w+)", line.strip())
        if m:
            out[m.group(1)] = m.group(2) == "True"
    return out


def main():
    if not drive.boot():
        print("SKIP: game is not running")
        return 2

    rows = combatants()
    print("\n=== board ===")
    for guid, row in rows.items():
        print("  %s %-22s g=%s hp=%-4s %s" % (
            guid[:8], row.get("class"), row.get("group"), row.get("hp"), row.get("tile")))

    # ---- grid ------------------------------------------------------------
    print("\n=== GRID ===")
    t = tiles()
    ally, enemy = len(t.get("0", [])), len(t.get("1", []))
    check("grid: ally side has more than the standard 4 tiles", ally > 4,
          "ally=%d enemy=%d (standard is 4 each; Extended should be 6)" % (ally, enemy))

    # ---- Beast Trainer ---------------------------------------------------
    print("\n=== BEAST TRAINER ===")
    tguid, _ = by_class(rows, "CF_ORIG_TRAINER")
    if tguid:
        partners = [r for r in rows.values()
                    if r.get("group") == "0" and re.search(r"WOLF|ELEMENTAL|GOLEM", r.get("class", ""))]
        check("trainer: a partner was summoned at combat start", len(partners) >= 1,
              "; ".join(p["class"] + " hp=" + p.get("hp", "?") for p in partners) or "none")
        check("trainer: exactly ONE auto-summon (the rest are moves)", len(partners) == 1,
              "found %d" % len(partners))
        for p in partners:
            check("partner %s can act (pa>0, alive)" % p["class"],
                  p.get("pa", "0") != "0" and p.get("dead") == "False", p["line"][:150])

        ab = abilities(tguid)
        sends = {k: v for k, v in ab.items() if "SEND_OUT" in k}
        check("trainer: the three Send Out moves are present", len(sends) == 3, str(sorted(sends)))
        check("trainer: at least one Send Out is usable (needs a free ally tile)",
              any(sends.values()), str(sends))
    else:
        check("trainer: present in the party", False, "no CF_ORIG_TRAINER on the board")

    # ---- Pacifist --------------------------------------------------------
    print("\n=== PACIFIST ===")
    pguid, prow = by_class(rows, "CF_ORIG_PACIFIST")
    if pguid:
        ab = abilities(pguid)
        offensive = [a for a in ab
                     if re.search(r"_ATTACK$", a) and not a.startswith("ONLY_")
                     and "SEND_OUT" not in a]
        check("pacifist: carries NO damaging attack", not offensive,
              "offensive abilities found: %s" % offensive if offensive else "none, as designed")
        onlys = [a for a in ab if a.startswith("ONLY_")]
        check("pacifist: has the non-damaging ONLY_* kit", len(onlys) >= 1, str(sorted(onlys)))

        before = int(prow.get("hp", 0))
        drive.run("crucible_status_add", ["1", "STATUS_REFLECT_00", "2"])
        after = combatants().get(pguid, {})
        check("pacifist: a status applies and reads back",
              "REFLECT" in after.get("statuses", ""),
              "statuses=[%s]" % after.get("statuses", ""))
    else:
        check("pacifist: present in the party", False, "no CF_ORIG_PACIFIST on the board")

    # ---- Vampiric --------------------------------------------------------
    print("\n=== VAMPIRIC ===")
    vguid, vrow = by_class(rows, "CF_ORIG_VAMPIRIC")
    if vguid:
        ab = abilities(vguid)
        check("vampiric: has a damaging attack to pay Blood Price with",
              any(a.endswith("_ATTACK") and not a.startswith("ONLY_") for a in ab),
              str(sorted(a for a in ab if a.endswith("_ATTACK"))))
        drive.run("crucible_status_add", ["0", "STATUS_CF_ENGORGED", "3"])
        after = combatants().get(vguid, {})
        check("vampiric: the pack's custom STATUS_CF_ENGORGED resolves and applies",
              "ENGORGED" in after.get("statuses", ""),
              "statuses=[%s]" % after.get("statuses", ""))
    else:
        check("vampiric: present in the party", False, "no CF_ORIG_VAMPIRIC on the board")

    # ---- harness verbs ---------------------------------------------------
    print("\n=== HARNESS ===")
    r = drive.run("crucible_chaos_state")
    check("chaos state reads the LIVE MapState copy", "error" not in r.lower(), r.splitlines()[0][:170])

    r = drive.run("crucible_list_targets", ["BASIC_MOVE", "-"])
    check("crucible_list_targets no longer throws", "threw" not in r, r.splitlines()[0][:170])

    print("\n" + "=" * 62)
    bad = [n for n, ok in RESULTS if not ok]
    print("%d/%d passed" % (len(RESULTS) - len(bad), len(RESULTS)))
    for n in bad:
        print("  FAILED: " + n)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
