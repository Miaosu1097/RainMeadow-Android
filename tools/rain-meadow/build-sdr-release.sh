#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$WORKSPACE_ROOT"

sh tools/rain-meadow/steam/native/build-sdr.sh
export RAIN_MEADOW_SOURCE=src/mods/Rain-Meadow-1.16.3
export RAIN_MEADOW_STEAM_PROBE=1 RAIN_MEADOW_STEAM_GAMEPLAY=1 RAIN_MEADOW_STEAM_SDR=1
export RAIN_MEADOW_STEAM_TRANSPORT=work/rain-meadow/1.16.3/steam-transport/sdr-build/libmeadow_steam_transport.so
sh tools/rain-meadow/stage_candidate.sh \
    work/rain-meadow/shaders/verified-gles/stage/assetbundles/rainmeadow \
    release/Rain-Meadow/0.1.16.3/RainMeadow-android-steam-sdr.zip
