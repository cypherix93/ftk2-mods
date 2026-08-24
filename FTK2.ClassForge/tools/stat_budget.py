#!/usr/bin/env python3
"""
Report the stat budget of every class in a pack, so a new class can be placed in the SAME power
band as the ones already shipping rather than eyeballed.

"Budget" is the sum of the seven scaling stats (STR VIT INT AWR TAL SPD LCK). HP, FOC, CRT, DEF,
EVD, RES, PA and SA are reported separately because they are not on that scale and a single point
of DEF is worth far more than a single point of LCK.

Usage:
    python FTK2.ClassForge/tools/stat_budget.py [packDir] [--class ID ...]
"""

import argparse
import json
import io
import os
import sys

SCALING = ["STR", "VIT", "INT", "AWR", "TAL", "SPD", "LCK"]
FLAT = ["HP", "FOC", "CRT", "DEF", "EVD", "RES", "PA", "SA"]

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_PACK = os.path.join(REPO, "FTK2.ClassForge", "data", "ClassPacks", "CF_PACK_EOR_CLASSES")


def load(pack):
    with io.open(os.path.join(pack, "classes.json"), encoding="utf-8") as handle:
        return json.load(handle)


def budget(stats):
    return sum(int(stats.get(name, 0)) for name in SCALING)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pack", nargs="?", default=DEFAULT_PACK)
    parser.add_argument("--extra", action="append", default=[],
                        help="another pack dir to compare against the first")
    options = parser.parse_args()

    classes = load(options.pack)
    rows = sorted(((budget(c["Stats"]), name, c["Stats"]) for name, c in classes.items()))

    print("%-28s %6s  %s" % ("class", "budget", "  ".join("%3s" % s for s in FLAT)))
    for total, name, stats in rows:
        print("%-28s %6d  %s" % (
            name, total, "  ".join("%3s" % stats.get(s, 0) for s in FLAT)))

    totals = [r[0] for r in rows]
    print("\n%s: n=%d  min=%d  max=%d  mean=%.1f  median=%d" % (
        os.path.basename(options.pack), len(totals), min(totals), max(totals),
        sum(totals) / float(len(totals)), sorted(totals)[len(totals) // 2]))

    for name in FLAT:
        values = [int(s.get(name, 0)) for _, _, s in rows]
        print("  %-4s min=%-4d max=%-4d mean=%.1f" % (
            name, min(values), max(values), sum(values) / float(len(values))))

    for extra in options.extra:
        extra_classes = load(extra)
        if not extra_classes:
            print("\n%s: (empty)" % os.path.basename(extra))
            continue
        print("\n%-28s %6s  %s" % (os.path.basename(extra), "budget", "  ".join("%3s" % s for s in FLAT)))
        inband = 0
        for name, c in sorted(extra_classes.items()):
            total = budget(c["Stats"])
            ok = min(totals) <= total <= max(totals)
            inband += 1 if ok else 0
            print("%-28s %6d  %s   %s" % (
                name, total, "  ".join("%3s" % c["Stats"].get(s, 0) for s in FLAT),
                "in band" if ok else "OUT OF BAND (%d..%d)" % (min(totals), max(totals))))
        print("%d/%d in band" % (inband, len(extra_classes)))
        if inband != len(extra_classes):
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
