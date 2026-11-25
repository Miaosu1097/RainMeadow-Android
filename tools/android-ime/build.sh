#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

managed=libs/game/Managed
source=src/mods/Android-IME
version=0.1.1
artifact=artifacts/mods/Android-IME/$version
release=release/Android-IME/$version
build=work/android-ime/$version
cache=.cache/android-ime
mkdir -p "$artifact" "$release" "$build/java" "$cache" "$TMPDIR"

fetch() {
    file="$cache/$1"
    if ! printf '%s  %s\n' "$2" "$file" | sha256sum -c --status 2>/dev/null; then
        curl --disable -fLsS --retry 2 --max-time 60 "$3" -o "$file.partial"
        printf '%s  %s\n' "$2" "$file.partial" | sha256sum -c --status
        mv "$file.partial" "$file"
    fi
}

fetch android-4.1.1.4.jar 84072541cbb711eff89f7277100ff854929a446dba7ceb1b195c340e0b4fd3cb \
    https://repo.maven.apache.org/maven2/com/google/android/android/4.1.1.4/android-4.1.1.4.jar
fetch r8-8.3.37.jar 59753e70a74f918389cc87f1b7d66b5c0862932559167425708ded159e3de439 \
    https://dl.google.com/dl/android/maven2/com/android/tools/r8/8.3.37/r8-8.3.37.jar

android_api="$cache/android-4.1.1.4.jar"
javac -J-Djava.io.tmpdir="$TMPDIR" -Xlint:-options --release 8 -cp "$android_api" \
    -d "$build/java" "$source/AndroidTextEditor.java"
java -Djava.io.tmpdir="$TMPDIR" -cp "$cache/r8-8.3.37.jar" com.android.tools.r8.D8 \
    --release --min-api 24 --lib "$android_api" --output "$build" \
    "$build/java/rwandroid/ime/AndroidTextEditor.class"
cp "$build/classes.dex" "$artifact/rwandroid-ime.dex"

# Reference the game with an alias to avoid its embedded BepInEx shim.
csc -nologo -langversion:latest -nullable:enable -warnaserror -target:library \
    "-out:$artifact/AndroidIME.dll" "-resource:$artifact/rwandroid-ime.dex,rwandroid-ime.dex" \
    -r:game="$managed/Assembly-CSharp.dll" \
    -r:"$managed/BepInEx.dll" -r:"$managed/HOOKS-Assembly-CSharp.dll" \
    -r:"$managed/UnityEngine.dll" -r:"$managed/UnityEngine.CoreModule.dll" -r:"$managed/UnityEngine.AndroidJNIModule.dll" \
    "$source/AndroidImePlugin.cs" "$source/TextInput.cs"
python -IB - "$source" "$artifact" "$release" <<'PY'
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

source, artifact, release = map(Path, sys.argv[1:])
destination = release / "Android-IME.zip"
with ZipFile(destination, "w", compression=ZIP_DEFLATED) as archive:
    archive.write(source / "modinfo.json", "modinfo.json")
    archive.write(artifact / "AndroidIME.dll", "plugins/AndroidIME.dll")
with ZipFile(destination) as archive:
    if (failed := archive.testzip()) is not None:
        raise RuntimeError(f"Corrupt ZIP entry: {failed}")
print(destination)
PY
