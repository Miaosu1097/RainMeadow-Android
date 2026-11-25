#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
if [ "$#" -ne 1 ]; then
    printf '%s\n' 'Usage: verify_targets.sh REPORT.tsv' >&2
    exit 2
fi
il=tools/rain-meadow/il
manifest="$il/android-targets.tsv"
revision=${RAIN_MEADOW_GAME_REVISION:-}
if [ -n "$revision" ] && [ -f "$il/baselines/$revision.tsv" ]; then
    manifest="$il/baselines/$revision.tsv"
fi
work=work/rain-meadow/il-audit
mkdir -p "$work"
cp "$RAIN_MEADOW_GAME_MANAGED/Mono.Cecil.dll" "$work/"
sh tools/rain-meadow/csc.sh -out:"$work/Audit.exe" -r:"$work/Mono.Cecil.dll" tools/rain-meadow/il/Audit.cs
MONO_PATH= mono "$work/Audit.exe" --fingerprints "$RAIN_MEADOW_GAME_MANAGED/Assembly-CSharp.dll" "$manifest" > "$1"
if ! diff -u "$manifest" "$1"; then
    printf '%s\n' 'Game IL targets changed. Review the indicated methods and patches before updating the baseline.' >&2
    exit 1
fi
