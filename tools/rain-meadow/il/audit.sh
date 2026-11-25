#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
sources=$RAIN_MEADOW_SOURCE
managed=$RAIN_MEADOW_GAME_MANAGED
work=work/rain-meadow/il-audit
mkdir -p "$work"
cp "$managed/Mono.Cecil.dll" "$work/"
sh tools/rain-meadow/csc.sh -out:"$work/Audit.exe" -reference:"$managed/Mono.Cecil.dll" tools/rain-meadow/il/Audit.cs

python3 -IB - "$sources" "$work" <<'PY'
import re
import sys
from pathlib import Path

source, work = map(Path, sys.argv[1:])
registration = re.compile(r"^\s*(IL\.[\w.]+)\s*\+=\s*([^;]+);")
reflective = re.compile(r"\bnew\s+ILHook\s*\(")
subscriptions = []
dynamic = []
for path in sorted(source.rglob("*.cs")):
    for n, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        location = f"{path.relative_to(source)}:{n}"
        found = registration.match(line)
        if found:
            hook, handler = found.groups()
            subscriptions.append(f"{location}\t{hook}\t{handler}")
        if reflective.search(line) and not line.lstrip().startswith("//"):
            dynamic.append(f"{location}\t{line.strip()}")

if not subscriptions:
    sys.exit(f"No static IL subscriptions found under {source}")
(work / "subscriptions.tsv").write_text("\n".join(subscriptions) + "\n")
(work / "reflective.tsv").write_text("\n".join(dynamic) + "\n")
print(f"{len(subscriptions)} static subscriptions; {len(dynamic)} explicit reflective registrations")
PY

MONO_PATH= mono "$work/Audit.exe" "$managed/Assembly-CSharp.dll" "$managed/HOOKS-Assembly-CSharp.dll" "$work/subscriptions.tsv" "$@" > "$work/hooks.tsv"
awk -F '\t' 'NR>1 { status[$4]++ } END { for (key in status) print key, status[key] }' "$work/hooks.tsv"
