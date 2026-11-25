#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

work=work/rain-meadow/steam
tools=tools/rain-meadow/steam
dependencies="$work/dependencies"
source=$RAIN_MEADOW_SOURCE
framework=$MONO_FRAMEWORK_REFS
mkdir -p "$TMPDIR" "$work/packages"

dotnet build "$tools/Dependencies.csproj" --nologo \
    -p:RestoreLockedMode=true \
    -p:BaseIntermediateOutputPath="$WORKSPACE_ROOT/$work/obj/" \
    --output "$dependencies" \
    --source "$WORKSPACE_ROOT/$work/packages" \
    --source https://api.nuget.org/v3/index.json

sh tools/rain-meadow/csc.sh -langversion:latest -nullable:enable -warnaserror -define:MEADOW_STEAM_PROBE \
    -out:"$dependencies/SteamProbe.exe" -r:"$dependencies/SteamKit2.dll" \
    -r:"$dependencies/QRCoder.dll" -r:"$framework/Facades/netstandard.dll" \
    "$tools/ProbeRunner.cs" "$source/Online/Steam/SteamLobbyProbe.cs"
