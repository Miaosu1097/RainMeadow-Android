# Source this after setting WORKSPACE_ROOT to the workspace's absolute path.
export MONO_FRAMEWORK_REFS="${MONO_FRAMEWORK_REFS:-${PREFIX:-/usr}/lib/mono/4.8-api}"
export RAIN_MEADOW_GAME_MANAGED="${RAIN_MEADOW_GAME_MANAGED:-libs/game/Managed}"
export RAIN_MEADOW_SOURCE="${RAIN_MEADOW_SOURCE:-src/mods/Rain-Meadow-1.16.3}"
export TMPDIR="$WORKSPACE_ROOT/.cache/tmp"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.cache"
export DOTNET_CLI_HOME="$WORKSPACE_ROOT/.cache/rain-meadow/dotnet"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$WORKSPACE_ROOT/.cache/rain-meadow/dotnet-bundle"
export NUGET_PACKAGES="$WORKSPACE_ROOT/.cache/rain-meadow/nuget"
export NUGET_HTTP_CACHE_PATH="$WORKSPACE_ROOT/.cache/rain-meadow/nuget-http"
export NUGET_PLUGINS_CACHE_PATH="$WORKSPACE_ROOT/.cache/rain-meadow/nuget-plugins"
export NUGET_SCRATCH="$TMPDIR"
mkdir -p "$TMPDIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
