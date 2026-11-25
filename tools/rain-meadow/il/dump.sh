#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
tool="$WORKSPACE_ROOT/work/rain-meadow/il-dump"
cecil="$RAIN_MEADOW_GAME_MANAGED/Mono.Cecil.dll"
mkdir -p "$tool"
cp "$cecil" "$tool/"
sh tools/rain-meadow/csc.sh -out:"$tool/Dump.exe" -reference:"$cecil" "$WORKSPACE_ROOT/tools/rain-meadow/il/Dump.cs"
MONO_PATH= mono "$tool/Dump.exe" "$@"
