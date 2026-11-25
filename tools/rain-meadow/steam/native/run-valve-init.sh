#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

tool=tools/rain-meadow/steam/native
transport="$WORKSPACE_ROOT/work/rain-meadow/1.16.3/steam-transport"
original="$transport/native/androidarm64/libsteamnetworkingsockets.so"
probe_dir="$transport/valve-init"
probe_lib="$probe_dir/no-unused-android/libsteamnetworkingsockets.so"
sdr_config="$transport/active/rain-world-sdr-config.json"
sdk="$transport/native/sdk"

mkdir -p "$TMPDIR" "$probe_dir/no-unused-android"
python "$tool/prepare_valve_android.py" "$original" "$probe_lib"
clang++ -std=c++17 -O2 -Wall -Wextra -Werror -isystem "$sdk" \
    "$tool/valve_init_probe.cpp" -ldl -o "$probe_dir/valve-init-probe"
ulimit -c 0

status=0
env -u LD_LIBRARY_PATH -u LD_PRELOAD timeout 30s \
    "$probe_dir/valve-init-probe" "$probe_lib" "$sdr_config" > "$probe_dir/valve-init.log" 2>&1 || status=$?
cat "$probe_dir/valve-init.log"
exit "$status"
