#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

sh tools/rain-meadow/steam/native/build-sdr.sh
export RAIN_MEADOW_STEAM_PROBE=1 RAIN_MEADOW_STEAM_GAMEPLAY=1 RAIN_MEADOW_STEAM_SDR=1
export RAIN_MEADOW_STEAM_TRANSPORT=work/rain-meadow/1.16.3/steam-transport/sdr-build/libmeadow_steam_transport.so
sh tools/rain-meadow/stage_candidate.sh \
    "${RAIN_MEADOW_GLES_BUNDLE:-work/rain-meadow/shaders/verified-gles/stage/assetbundles/rainmeadow}" \
    "${RAIN_MEADOW_RELEASE_ZIP:-release/Rain-Meadow/0.1.16.3/RainMeadow-android-steam-sdr.zip}"
