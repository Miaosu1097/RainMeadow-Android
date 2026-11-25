#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
tool="$WORKSPACE_ROOT/work/rain-meadow/il-dump"
cecil="$WORKSPACE_ROOT/libs/game/Managed/Mono.Cecil.dll"
mkdir -p "$tool"
cp "$cecil" "$tool/"
csc -nologo -out:"$tool/Dump.exe" -reference:"$cecil" "$WORKSPACE_ROOT/tools/rain-meadow/il/Dump.cs"
MONO_PATH= mono "$tool/Dump.exe" "$@"
