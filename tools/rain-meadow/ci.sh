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
export RAIN_MEADOW_GAME_MANAGED="${RAIN_MEADOW_GAME_MANAGED:-libs/game/$revision/Managed}"
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
inputs=tools/rain-meadow/ci_inputs.py
if [ -n "${RAIN_MEADOW_GAME_ARCHIVE:-}" ]; then
    RAIN_MEADOW_GAME_MANAGED=$(python3 -IB "$inputs" game "$revision" "$RAIN_MEADOW_GAME_ARCHIVE")
fi
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
game_inputs="libs/game/$revision"
shader_inputs="$game_inputs/shaders"
mod_overlay="$game_inputs/mod-overlay"
if [ -n "${RAIN_MEADOW_GAME_ARCHIVE:-}" ]; then
    snapshot=$(dirname -- "$RAIN_MEADOW_GAME_ARCHIVE")
    if [ -d "$snapshot/shaders" ]; then
        shader_inputs="$snapshot/shaders"
    fi
    if [ -d "$snapshot/mod-overlay" ]; then
        mod_overlay="$snapshot/mod-overlay"
    fi
fi
if [ -d "$mod_overlay" ]; then
    export RAIN_MEADOW_MOD_OVERLAY="${RAIN_MEADOW_MOD_OVERLAY:-$mod_overlay}"
fi
export RAIN_MEADOW_VALVE_ANDROID_SO="${RAIN_MEADOW_VALVE_ANDROID_SO:-libs/valve/android-arm64/libsteamnetworkingsockets.so}"
export RAIN_MEADOW_GLES_BUNDLE="${RAIN_MEADOW_GLES_BUNDLE:-$shader_inputs/rainmeadow}"
export RAIN_MEADOW_RELEASE_ZIP="${RAIN_MEADOW_RELEASE_ZIP:-release/Rain-Meadow/$revision/RainMeadow-android.zip}"
template=${RAIN_MEADOW_GLES_TEMPLATE:-$shader_inputs/current-gles-template.asset}
if [ -f "$template" ]; then
    RAIN_MEADOW_ASSET_SOURCE=$(python3 -IB "$inputs" upstream)
    export RAIN_MEADOW_ASSET_SOURCE
    mkdir -p work/rain-meadow
    shader_work=$(mktemp -d work/rain-meadow/shader.XXXXXX)/build
    python3 -B tools/rain-meadow/shaders/build_android_shader.py "$RAIN_MEADOW_ASSET_SOURCE" "$shader_work" \
        --android-template "$template" --vkd3d "${RAIN_MEADOW_VKD3D:-vkd3d-compiler}" \
        --spirv-cross "${RAIN_MEADOW_SPIRV_CROSS:-spirv-cross}"
    RAIN_MEADOW_GLES_BUNDLE="$shader_work/stage/assetbundles/rainmeadow"
elif [ ! -s "$RAIN_MEADOW_GLES_BUNDLE" ]; then
    printf '%s\n' "Provide the Android GLES template $template or a verified bundle at $RAIN_MEADOW_GLES_BUNDLE." >&2
    exit 1
fi
exec sh tools/rain-meadow/build-sdr-release.sh
