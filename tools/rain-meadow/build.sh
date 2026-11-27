#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

work=${RAIN_MEADOW_BUILD_DIR:-work/rain-meadow/build}
managed=$RAIN_MEADOW_GAME_MANAGED
sources=$RAIN_MEADOW_SOURCE
overlay=${RAIN_MEADOW_OVERLAY:-}
steam_probe=${RAIN_MEADOW_STEAM_PROBE_DEPS:-}
python3 -IB - "$work" "$sources" "$overlay" "$steam_probe" <<'PYCHECK'
import sys
from pathlib import Path

root = Path.cwd()
for value in filter(None, sys.argv[1:]):
    if not Path(value).resolve().is_relative_to(root):
        sys.exit(f"Path is outside the workspace: {value}")
PYCHECK

# Use installed compiler tools, with the game and framework references explicit.
framework=$MONO_FRAMEWORK_REFS
compiler=${ROSLYN_CSC:-.cache/rain-meadow/roslyn-5.0.0/tasks/netcore/bincore/csc.dll}
test -f "$compiler"
test -f "$framework/mscorlib.dll"
mkdir -p "$work/refs" "$work/bin"
sh tools/rain-meadow/csc.sh -target:exe -out:"$work/Publicize.exe" -reference:"$managed/Mono.Cecil.dll" tools/rain-meadow/il/Publicize.cs
cp "$managed/Mono.Cecil.dll" "$work/Mono.Cecil.dll"
MONO_PATH= mono "$work/Publicize.exe" "$managed/Assembly-CSharp.dll" "$work/refs/Assembly-CSharp.dll"

python3 -IB - "$sources" "$work" "$framework" "$overlay" "$steam_probe" "$managed" <<'PY'
import os
import sys
from pathlib import Path

source, work, framework = map(Path, sys.argv[1:4])
overlay = Path(sys.argv[4]) if sys.argv[4] else None
steam_probe = Path(sys.argv[5]) if sys.argv[5] else None
managed = Path(sys.argv[6])
references = [
    work / "refs/Assembly-CSharp.dll",
    managed / "HOOKS-Assembly-CSharp.dll",
    Path("libs/rain-meadow/Newtonsoft.Json.dll"),
]
references.extend(
    path for path in sorted(managed.glob("*.dll"))
    if path.name.startswith(("Unity", "Rewired", "MonoMod", "Mono.Cecil"))
    or path.stem in {
        "Assembly-CSharp-firstpass", "0Harmony", "BepInEx",
        "BepInEx.Harmony", "HarmonyXInterop", "com.rlabrecque.steamworks.net",
        "ICSharpCode.SharpZipLib", "GalaxyCSharp",
    }
)
references.extend(framework / f"{name}.dll" for name in (
    "mscorlib", "System", "System.Core", "System.Configuration", "System.Drawing",
    "System.IO.Compression", "System.Numerics", "System.Runtime.Serialization", "System.Xml", "System.Xml.Linq",
))
references.append(framework / "Facades/netstandard.dll")
if steam_probe:
    references.extend(steam_probe / f"{name}.dll" for name in (
        "SteamKit2", "QRCoder", "BouncyCastle.Cryptography", "protobuf-net", "protobuf-net.Core", "System.Memory",
    ))
for reference in references:
    if not reference.is_file():
        sys.exit(f"Missing build reference: {reference}")
def project_files(root):
    return {
        path.relative_to(root): path
        for path in root.rglob("*.cs")
        if not {"bin", "obj"}.intersection(path.relative_to(root).parts)
    }

inputs = project_files(source)
if not inputs:
    sys.exit(f"No C# sources found: {source}")
if overlay:
    replacements = project_files(overlay)
    if not replacements:
        sys.exit(f"No C# sources found in overlay: {overlay}")
    inputs.update(replacements)
flags = [
    "-nologo", "-nostdlib+", "-target:library", "-langversion:latest",
    "-nullable:enable", "-unsafe+", "-define:TRACE", "-optimize+", "-debug:portable",
    "-deterministic+", f'-pathmap:"{source.resolve()}=/"', f'-out:"{work}/bin/Rain Meadow.dll"',
]
if steam_probe:
    flags.append("-define:MEADOW_STEAM_PROBE")
    if os.environ.get("RAIN_MEADOW_STEAM_GAMEPLAY") == "1":
        flags.append("-define:MEADOW_STEAM_GAMEPLAY")
        if os.environ.get("RAIN_MEADOW_STEAM_SDR") == "1":
            flags.append("-define:MEADOW_STEAM_SDR")
flags.extend(f'-reference:"{path}"' for path in references)
# The PC build discovers .cs files with case-insensitive ordering.
# Rain Meadow assigns its built-in RPC IDs in metadata order.
flags.extend(f'"{inputs[key]}"' for key in sorted(inputs, key=lambda path: (path.as_posix().casefold(), path)))
(work / "compiler.rsp").write_text("\n".join(flags) + "\n", encoding="utf-8")
PY

dotnet "$compiler" @"$work/compiler.rsp"
