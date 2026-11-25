#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$WORKSPACE_ROOT"

case "$#:${1:-}" in
    2:verify|2:package) ;;
    *) printf '%s\n' 'Usage: ci.sh verify|package GAME_REVISION' >&2; exit 2 ;;
esac
revision=$2
case "$revision" in
    ''|.|..|*[!a-zA-Z0-9._-]*) printf '%s\n' 'Use a game revision such as 1.10 or 1.10.1-android.' >&2; exit 2 ;;
esac

export RAIN_MEADOW_GAME_REVISION="$revision"
export RAIN_MEADOW_GAME_MANAGED="libs/game/$revision/Managed"
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
for library in Assembly-CSharp HOOKS-Assembly-CSharp BepInEx Mono.Cecil MonoMod.Utils MonoMod.Common MonoMod.RuntimeDetour; do
    if [ ! -s "$RAIN_MEADOW_GAME_MANAGED/$library.dll" ]; then
        printf '%s\n' "Missing game build input: $RAIN_MEADOW_GAME_MANAGED/$library.dll" >&2
        exit 1
    fi
done
if [ ! -f "$MONO_FRAMEWORK_REFS/Facades/netstandard.dll" ]; then
    printf '%s\n' 'Set MONO_FRAMEWORK_REFS to the Mono 4.8-api reference directory.' >&2
    exit 1
fi
export RAIN_MEADOW_STEAM_PROBE=1 RAIN_MEADOW_STEAM_GAMEPLAY=1 RAIN_MEADOW_STEAM_SDR=1

if [ "$1" = verify ]; then
    exec sh tools/rain-meadow/stage_candidate.sh --verify-only
fi
export RAIN_MEADOW_VALVE_ANDROID_SO=libs/valve/android-arm64/libsteamnetworkingsockets.so
export RAIN_MEADOW_GLES_BUNDLE="libs/game/$revision/shaders/rainmeadow"
export RAIN_MEADOW_RELEASE_ZIP="release/Rain-Meadow/$revision/RainMeadow-android-steam-sdr.zip"
for input in "$RAIN_MEADOW_VALVE_ANDROID_SO" "$RAIN_MEADOW_GLES_BUNDLE"; do
    if [ ! -s "$input" ]; then
        printf '%s\n' "Missing package input: $input" >&2
        exit 1
    fi
done
exec sh tools/rain-meadow/build-sdr-release.sh
