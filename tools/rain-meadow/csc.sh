#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"
compiler=${ROSLYN_CSC:-.cache/rain-meadow/roslyn-5.0.0/tasks/netcore/bincore/csc.dll}
framework=$MONO_FRAMEWORK_REFS
exec dotnet "$compiler" -nologo -noconfig -nostdlib+ \
    -r:"$framework/mscorlib.dll" -r:"$framework/System.dll" -r:"$framework/System.Core.dll" "$@"
