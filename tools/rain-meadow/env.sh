# Source this after setting WORKSPACE_ROOT to the workspace's absolute path.
export TMPDIR="$WORKSPACE_ROOT/.cache/tmp"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.cache"
export DOTNET_CLI_HOME="$WORKSPACE_ROOT/.cache/rain-meadow/dotnet"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$WORKSPACE_ROOT/.cache/rain-meadow/dotnet-bundle"
export NUGET_PACKAGES="$WORKSPACE_ROOT/.cache/rain-meadow/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1
