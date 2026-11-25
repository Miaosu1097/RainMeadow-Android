#!/bin/sh
set -eu

workspace="$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd -P)"
tools="$workspace/tools/rain-meadow/runtime"
game="$workspace/libs/game/Managed"
output="$workspace/work/rain-meadow/runtime"
export TMPDIR="$workspace/.cache/tmp"
export XDG_CACHE_HOME="$workspace/.cache"
mkdir -p "$output" "$TMPDIR"

csc -nologo -target:library -out:"$output/RainMeadowRuntimeProbe.dll" \
    -r:Game="$game/Assembly-CSharp.dll" \
    -r:"$game/BepInEx.dll" \
    -r:"$game/UnityEngine.dll" \
    -r:"$game/UnityEngine.CoreModule.dll" \
    -r:"$game/UnityEngine.AudioModule.dll" \
    -r:"$game/UnityEngine.UnityWebRequestModule.dll" \
    -r:"$game/UnityEngine.UnityWebRequestAudioModule.dll" \
    "$tools/UnityRuntimeProbe.cs" "$tools/LanProbe.cs" "$tools/AndroidLanAddresses.cs"

csc -nologo -out:"$output/LanProbe.exe" \
    "$tools/LanProbe.cs" "$tools/AndroidLanAddresses.cs"
mono "$output/LanProbe.exe" > "$output/lan-host.txt"
