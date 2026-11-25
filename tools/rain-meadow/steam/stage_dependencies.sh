#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

work=work/rain-meadow/steam
dependencies="$work/dependencies"
stage="$work/package-input"
if [ "${RAIN_MEADOW_STEAM_SDR:-0}" = 1 ]; then
    stage="$work/package-input-sdr"
elif [ "${RAIN_MEADOW_STEAM_GAMEPLAY:-0}" = 1 ]; then
    stage="$work/package-input-gameplay"
fi
licenses="$stage/licenses"
cache="$NUGET_PACKAGES"
mkdir -p "$stage/plugins" "$licenses"

# The remaining framework assemblies are supplied by the Android game runtime.
for name in QRCoder BouncyCastle.Cryptography protobuf-net protobuf-net.Core System.Buffers \
    System.Collections.Immutable System.Drawing.Common System.Memory; do
    cp "$dependencies/$name.dll" "$stage/plugins/"
done

patch_dir="$work/compression-patch"
mkdir -p "$patch_dir"
cp libs/game/Managed/Mono.Cecil.dll "$patch_dir/"
csc -nologo -langversion:latest "-out:$patch_dir/PatchSteamCompression.exe" \
    -r:"$patch_dir/Mono.Cecil.dll" tools/rain-meadow/steam/PatchSteamCompression.cs
MONO_PATH= mono "$patch_dir/PatchSteamCompression.exe" "$dependencies/SteamKit2.dll" \
    libs/game/Managed/ICSharpCode.SharpZipLib.dll "$stage/plugins/SteamKit2.dll"
cp tools/rain-meadow/steam/PatchSteamCompression.cs "$licenses/SteamKit2-Android-Compression.cs"

if [ "${RAIN_MEADOW_STEAM_GAMEPLAY:-0}" = 1 ]; then
    native=${RAIN_MEADOW_STEAM_TRANSPORT:?Path to the ARM64 Steam transport is required}
    python -IB - "$native" <<'PYVALIDATE'
from pathlib import Path
import sys
path = Path(sys.argv[1]).resolve()
if not path.is_relative_to(Path.cwd()) or not path.is_file():
    sys.exit("Steam transport must be an existing file inside the workspace.")
PYVALIDATE
    cp "$native" "$stage/plugins/libmeadow_steam_transport.so"
    cp "$PREFIX/lib/libc++_shared.so" "$stage/plugins/libc++_shared.so"
    cp "$PREFIX/share/doc/libc++/copyright" "$licenses/libc++-LICENSE.txt"
    if [ "${RAIN_MEADOW_STEAM_SDR:-0}" = 1 ]; then
        sdr=work/rain-meadow/1.16.3/steam-transport/sdr-build
        cp "$sdr/libsteamnetworkingsockets.so" "$sdr/rwmeadow-native-loader.dex" "$stage/plugins/"
        cp "$sdr/VALVE-SOURCE.txt" "$licenses/"
    else
        native_sources=work/rain-meadow/1.16.3/steam-transport/active
        cp "$native_sources/gns-source/LICENSE" "$licenses/GameNetworkingSockets-LICENSE.txt"
        cp "$native_sources/protobuf-21.12/LICENSE" "$licenses/protobuf-3.21-LICENSE.txt"
        cp "$PREFIX/share/doc/openssl/copyright" "$licenses/OpenSSL-LICENSE.txt"
    fi
fi
cp "$cache/bouncycastle.cryptography/2.7.0/LICENSE.md" "$licenses/BouncyCastle.Cryptography-LICENSE.md"
cp "$cache/steamkit2/2.5.0/license.txt" "$licenses/SteamKit2-NOTICE.txt"
cp "$cache/steamkit2/2.5.0/3rd party.txt" "$licenses/SteamKit2-THIRD-PARTY.txt"
fetch_license() {
    destination="$licenses/$2"
    if [ -s "$destination" ]; then
        return
    fi
    download=$(mktemp "$licenses/.license.XXXXXX")
    if curl --disable -fsSL --retry 2 --max-time 20 "$1" -o "$download" && [ -s "$download" ]; then
        mv "$download" "$destination"
    else
        rm -f "$download"
        return 1
    fi
}
fetch_license https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt SteamKit2-LGPL-2.1.txt
fetch_license https://raw.githubusercontent.com/codebude/QRCoder/bd980577640c47f8bb881cf24c8443415a579d36/LICENSE.txt QRCoder-LICENSE.txt
fetch_license https://raw.githubusercontent.com/protobuf-net/protobuf-net/9618de94145145228e115e8f134f132522e51f88/Licence.txt protobuf-net-LICENSE.txt
for package in system.buffers/4.5.1 system.collections.immutable/7.0.0 \
    system.drawing.common/5.0.3 system.memory/4.5.5; do
    name=${package%/*}
    cp "$cache/$package/LICENSE.TXT" "$licenses/$name-LICENSE.txt"
    cp "$cache/$package/THIRD-PARTY-NOTICES.TXT" "$licenses/$name-THIRD-PARTY.txt"
done
cat > "$licenses/SteamKit2-SOURCE.txt" <<'TEXT'
SteamKit2 2.5.0 is separately loaded. Android uses the game-provided managed gzip reader for HandleMulti.
The local change is reproduced by SteamKit2-Android-Compression.cs in this directory.
Exact source: https://github.com/SteamRE/SteamKit/tree/0c20a7d2aff9842815bf6b5aec657553e4186dd5
Package: https://www.nuget.org/packages/SteamKit2/2.5.0
TEXT
