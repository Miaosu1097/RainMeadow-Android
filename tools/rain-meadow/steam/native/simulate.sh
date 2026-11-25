#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

tool=tools/rain-meadow/steam/native
work=work/rain-meadow/1.16.3/steam-transport/native
glibc="$PREFIX/glibc"
sh "$tool/build.sh"
stage=$(mktemp -d "$work/simulation.XXXXXX")

clang++ --target=aarch64-linux-gnu --sysroot="$glibc" -nostdinc++ -isystem "$glibc/include" \
    -isystem "$work/sdk" -std=c++17 -O2 -fno-exceptions -fno-rtti -fPIC -shared \
    -fuse-ld=lld -nostdlib "$tool/mock_steam_client.cpp" "$glibc/lib/libc.so.6" \
    -o "$stage/steamclient-mock.so"

probe="$WORKSPACE_ROOT/$work/steam-network-probe-linux-arm64"
cd "$stage"
library="$PWD/steamclient-mock.so"
ulimit -c 0
RM_MOCK_ROLE=listen timeout 15s env -u LD_PRELOAD -u LD_LIBRARY_PATH "$probe" "$library" \
    listen 76561197960265729 > listener.log 2>&1 &
receiver=$!
trap 'kill "$receiver" 2>/dev/null || true' EXIT
ready=0
for attempt in 1 2 3 4 5 6 7 8 9 10
do
    if test -S mock-b.sock; then
        ready=1
        break
    fi
    sleep 0.1
done
test "$ready" = 1

RM_MOCK_ROLE=send timeout 10s env -u LD_PRELOAD -u LD_LIBRARY_PATH "$probe" "$library" \
    ping 76561197960265730 > sender.log 2>&1
wait "$receiver"
trap - EXIT

test ! -e mock-a.sock
test ! -e mock-b.sock
grep -Fxq 'Steam peer round trip verified.' sender.log
grep -Fxq 'Probe request received and echo queued.' listener.log

if RM_MOCK_ROLE=send RM_MOCK_WRONG_APP=1 timeout 10s env -u LD_PRELOAD -u LD_LIBRARY_PATH \
    "$probe" "$library" check > wrong-app.log 2>&1; then
    exit 1
else
    test "$?" = 5
fi
grep -Fxq 'Native Steam did not provide the Rain World application context.' wrong-app.log
printf 'Simulated Steam request, reply, app-ID check and cleanup passed. Logs: %s\n' "$stage"
