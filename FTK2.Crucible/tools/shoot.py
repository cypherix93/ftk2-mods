#!/usr/bin/env python3
"""
Capture a screenshot into artifacts/screenshots/<feature>/<name>.png.

One folder, split by feature, so evidence for a given command lives together rather than in a flat
pile of timestamped files. The plugin also writes its own copy under BepInEx/crucible-artifacts;
this decodes the base64 the RPC already returns so the repo copy is named for what it proves.

Usage:
    python FTK2.Crucible/tools/shoot.py <feature> <name>
"""

import base64
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHOTS = os.path.join(ROOT, "artifacts", "screenshots")


def capture(feature, name):
    request = urllib.request.Request(
        "http://127.0.0.1:8787/screenshot",
        data=json.dumps({"label": "%s-%s" % (feature, name)}).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        payload = json.loads(response.read().decode("utf-8"))

    if not payload.get("ok"):
        print("FAILED: %s" % payload.get("error"))
        return 1
    if not payload.get("base64"):
        print("FAILED: no image data (%s)" % payload.get("readError"))
        return 1

    folder = os.path.join(SHOTS, feature)
    if not os.path.isdir(folder):
        os.makedirs(folder)
    out = os.path.join(folder, name + ".png")
    with open(out, "wb") as handle:
        handle.write(base64.b64decode(payload["base64"]))
    print("wrote %s" % out)
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(capture(sys.argv[1], sys.argv[2]))
