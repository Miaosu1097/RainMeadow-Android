#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

tool=tools/rain-meadow/steam/native
work=work/rain-meadow/1.16.3/steam-transport/native
sdk="$work/sdk"
glibc="${PREFIX:?Termux is required}/glibc"
mkdir -p "$TMPDIR" "$sdk"

while IFS=' ' read -r header digest
do
    if ! test -f "$sdk/$header" || [ "$(git hash-object "$sdk/$header")" != "$digest" ]; then
        url="https://raw.githubusercontent.com/ValveSoftware/Proton/proton-9.0-4/lsteamclient/steamworks_sdk_154/$header"
        curl -fsSL --retry 1 --max-time 30 "$url" -o "$sdk/$header.partial"
        test "$(git hash-object "$sdk/$header.partial")" = "$digest"
        mv "$sdk/$header.partial" "$sdk/$header"
    fi
done < "$tool/headers.lock"

set -- -std=c++17 -O2 -Wall -Wextra -Werror -Wno-unused-parameter -fno-exceptions -fno-rtti \
    -isystem "$sdk"
clang++ "$@" --target=aarch64-linux-gnu --sysroot="$glibc" -nostdinc++ -isystem "$glibc/include" \
    -fPIE -pie -fuse-ld=lld -nostdlib -Wl,--dynamic-linker="$glibc/lib/ld-linux-aarch64.so.1" \
    -Wl,-rpath,"$glibc/lib" "$glibc/lib/Scrt1.o" "$glibc/lib/crti.o" "$tool/steam_network_probe.cpp" \
    "$glibc/lib/libc.so.6" "$glibc/lib/crtn.o" -o "$work/steam-network-probe-linux-arm64"
