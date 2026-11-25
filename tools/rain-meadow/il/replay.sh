#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
output=work/rain-meadow/il-replay
managed=$RAIN_MEADOW_GAME_MANAGED
mkdir -p "$output"
for library in Mono.Cecil MonoMod.Utils MonoMod.Common MonoMod.RuntimeDetour BepInEx; do
    cp "$managed/$library.dll" "$output/"
done
sh tools/rain-meadow/csc.sh -out:"$output/Replay.exe" -reference:"$output/Mono.Cecil.dll" -reference:"$output/MonoMod.Utils.dll" -reference:"$output/BepInEx.dll" -reference:"$output/MonoMod.RuntimeDetour.dll" tools/rain-meadow/il/Replay.cs tools/rain-meadow/il/BatchJit.cs
if [ "${1:-}" = "--generate" ]; then
    MONO_PATH= mono "$output/Replay.exe" "$@"
    exit
fi
if [ "${1:-}" = "--jit-all" ]; then
    ulimit -c 0
    MONO_PATH= mono "$output/Replay.exe" --jit-all "$managed" "$2" libs/rain-meadow "$3"
    exit
fi
if [ "${1:-}" = "--startup" ]; then
    MONO_PATH= mono "$output/Replay.exe" --startup "$managed" "$2" libs/rain-meadow
    exit
fi
MONO_PATH= mono "$output/Replay.exe" "$managed" "${1:-work/rain-meadow/build/bin/Rain Meadow.dll}" libs/rain-meadow "${2:-tools/rain-meadow/il/replay-cases.tsv}"
