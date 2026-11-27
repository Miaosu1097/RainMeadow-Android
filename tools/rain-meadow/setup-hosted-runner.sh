#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

mode=${1:-}
case "$mode" in
    verify|test) set -- mono-devel ;;
    package) set -- mono-devel ffmpeg ;;
    *) printf '%s\n' 'Usage: setup-hosted-runner.sh verify|test|package' >&2; exit 2 ;;
esac
if [ "$mode" = package ]; then
    snapshot="${RAIN_MEADOW_GAME_ARCHIVE%/*}/shaders"
    template="${RAIN_MEADOW_GLES_TEMPLATE:-$snapshot/current-gles-template.asset}"
    if [ -f "$template" ]; then
        set -- "$@" python3-venv vkd3d-compiler spirv-cross spirv-tools glslang-tools
    fi
fi
sudo apt-get update
sudo apt-get install -y --no-install-recommends "$@"
if [ "$mode" != package ]; then
    exit 0
fi

export JAVA_HOME="${JAVA_HOME_17_X64:-${JAVA_HOME:?No JDK found}}"
export PATH="$JAVA_HOME/bin:$PATH"
sdk=${ANDROID_SDK_ROOT:-${ANDROID_HOME:?No Android SDK found}}
ndk_version=29.0.14206865
ndk="$sdk/ndk/$ndk_version"
if [ ! -x "$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/clang++" ]; then
    local_sdk="$WORKSPACE_ROOT/.cache/android-sdk"
    mkdir -p "$local_sdk"
    cp -R "$sdk/licenses" "$local_sdk/"
    sdkmanager --sdk_root="$local_sdk" --install "ndk;$ndk_version"
    ndk="$local_sdk/ndk/$ndk_version"
fi
printf 'JAVA_HOME=%s\nANDROID_NDK_HOME=%s\n' "$JAVA_HOME" "$ndk" >> "$GITHUB_ENV"
printf '%s\n' "$JAVA_HOME/bin" >> "$GITHUB_PATH"

if [ -f "$template" ]; then
    venv="$WORKSPACE_ROOT/.cache/rain-meadow/shader-python"
    python3 -m venv "$venv"
    "$venv/bin/python3" -m pip --disable-pip-version-check install 'UnityPy==1.25.3'
    printf '%s\n' "$venv/bin" >> "$GITHUB_PATH"
fi
