#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

tool=tools/rain-meadow/steam/native
transport=work/rain-meadow/1.16.3/steam-transport
work="$transport/sdr-build"
sdk="$work/sdk165"
mkdir -p "$TMPDIR" "$sdk"

sdk_commit=df2baabf574a738ef1ea90a7e89339107fc0a279
while read -r digest name
do
    destination="$sdk/$name"
    if [ -f "$destination" ] && printf '%s  %s\n' "$digest" "$destination" | sha256sum -c --status; then
        continue
    fi
    case "$name" in
        steamnetworkingcustomsignaling.h)
            url="https://raw.githubusercontent.com/ValveSoftware/GameNetworkingSockets/v1.6.1/include/steam/$name"
            ;;
        *)
            url="https://raw.githubusercontent.com/rlabrecque/SteamworksSDK/$sdk_commit/public/steam/$name"
            ;;
    esac
    curl --disable -fsSL --retry 2 --max-time 25 "$url" -o "$destination.partial"
    printf '%s  %s\n' "$digest" "$destination.partial" | sha256sum -c --status
    mv "$destination.partial" "$destination"
done < "$tool/sdr-headers.lock"

valve=${RAIN_MEADOW_VALVE_ANDROID_SO:-"$transport/native/androidarm64/libsteamnetworkingsockets.so"}
if [ ! -s "$valve" ]; then
    valve=$(python3 -IB tools/rain-meadow/ci_inputs.py valve)
fi
python3 -IB "$tool/prepare_valve_android.py" "$valve" "$work/libsteamnetworkingsockets.so"
java_home=${JAVA_HOME:?Set JAVA_HOME to the JDK used by javac}
if [ -n "${ANDROID_NDK_HOME:-}" ]; then
    set -- "$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/clang++" --target=aarch64-linux-android24
else
    : "${PREFIX:?Set ANDROID_NDK_HOME on Linux or use Termux}"
    set -- clang++ -fno-termux-rpath
fi
"$@" -std=c++17 -O2 -Wall -Wextra -Werror -fPIC -fvisibility=hidden -shared -Wl,--no-undefined \
    -DSTEAMNETWORKINGSOCKETS_STANDALONELIB -DMEADOW_VALVE_SDR -isystem "$sdk" \
    -isystem "$java_home/include" -isystem "$java_home/include/linux" \
    "$tool/meadow_transport.cpp" "$tool/meadow_native_loader.cpp" -ldl -o "$work/libmeadow_steam_transport.so"

d8="$WORKSPACE_ROOT/.cache/rain-meadow/android/r8-8.3.37.jar"
d8_sha=59753e70a74f918389cc87f1b7d66b5c0862932559167425708ded159e3de439
mkdir -p "$(dirname -- "$d8")" "$work/java"
if ! printf '%s  %s\n' "$d8_sha" "$d8" | sha256sum -c --status 2>/dev/null; then
    curl --disable -fsSL --retry 2 --max-time 60 \
        https://dl.google.com/dl/android/maven2/com/android/tools/r8/8.3.37/r8-8.3.37.jar -o "$d8.partial"
    printf '%s  %s\n' "$d8_sha" "$d8.partial" | sha256sum -c --status
    mv "$d8.partial" "$d8"
fi
javac -J-Djava.io.tmpdir="$TMPDIR" -Xlint:-options --release 8 \
    -d "$work/java" "$tool/java/NativeSteamLoader.java"
java -Djava.io.tmpdir="$TMPDIR" -cp "$d8" com.android.tools.r8.D8 --release --min-api 24 --output "$work/java" \
    "$work/java/rwmeadow/steam/NativeSteamLoader.class"
cp "$work/java/classes.dex" "$work/rwmeadow-native-loader.dex"

cat > "$work/VALVE-SOURCE.txt" <<'TEXT'
Valve libsteamnetworkingsockets.so for Android ARM64
Original SHA-256: e85d8e5029c24167f0564a214336d06e0707912c302a19fdf6225e629dcafac7
Source: https://client-update.steamstatic.com/bins_androidarm64_linuxarm64.zip.c4ba08337562d6397c6c459a8e0b7d6f0ea04bb7
Compatibility change: remove the unused libandroid.so DT_NEEDED dependency.
The bridge is compiled against SteamNetworkingSockets013, SteamNetworkingMessages002 and SteamNetworkingUtils004.
TEXT
