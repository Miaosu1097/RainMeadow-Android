#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
deps=work/rain-meadow/dependencies
roslyn=.cache/rain-meadow/roslyn-5.0.0
mkdir -p "$deps" "$roslyn" libs/rain-meadow

fetch() {
    name=$1
    version=$2
    digest=$3
    file="$deps/$name.$version.nupkg"
    if [ ! -f "$file" ] || ! printf '%s  %s\n' "$digest" "$file" | sha256sum -c >/dev/null; then
        curl -q -fsSL --retry 4 --retry-all-errors --connect-timeout 8 --max-time 90 \
            "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg" -o "$file.part"
        printf '%s  %s\n' "$digest" "$file.part" | sha256sum -c
        mv "$file.part" "$file"
    fi
}

fetch newtonsoft.json 13.0.3 872fc189e638ab1056555b03aaa38f68bcb54286e221aa646eb1129babf63c77
fetch microsoft.net.compilers.toolset 5.0.0 e3f0207e7a8caf2cf79ad8f276eb20b2b880b3a7bb0e8ad27b4d3183d0a81627
unzip -p "$deps/newtonsoft.json.13.0.3.nupkg" lib/net45/Newtonsoft.Json.dll > libs/rain-meadow/Newtonsoft.Json.dll
unzip -p "$deps/newtonsoft.json.13.0.3.nupkg" LICENSE.md > libs/rain-meadow/Newtonsoft.Json.LICENSE.md
unzip -oq "$deps/microsoft.net.compilers.toolset.5.0.0.nupkg" 'tasks/netcore/bincore/*' -d "$roslyn"
