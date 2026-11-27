"""Package a staged Rain Meadow Android candidate without editing the source tree."""

import argparse
import shutil
from pathlib import Path
from tempfile import NamedTemporaryFile
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile, ZipInfo

ROOT = Path(__file__).resolve().parents[2]
AUDIO_DIRS = (Path("music/songs"), Path("soundeffects"))
BUNDLE = Path("assetbundles/rainmeadow")


def workspace_path(value):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT):
        raise argparse.ArgumentTypeError(f"Path is outside the workspace: {value}")
    return path


def files_in(root):
    for path in sorted(root.rglob("*")):
        if path.is_file() and path.name.casefold() != "desktop.ini":
            if path.is_symlink() or not path.resolve().is_relative_to(root):
                raise ValueError(f"Linked package input: {path}")
            yield path.relative_to(root), path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mod", type=workspace_path, required=True)
    parser.add_argument("--overlay-mod", type=workspace_path, action="append", default=[])
    parser.add_argument("--audio", type=workspace_path, required=True)
    parser.add_argument("--shader", type=workspace_path, required=True)
    parser.add_argument("--plugin", type=workspace_path, required=True)
    parser.add_argument("--steam-probe", type=workspace_path)
    parser.add_argument("output", type=workspace_path)
    args = parser.parse_args()

    replacements = {
        BUNDLE: args.shader,
        Path("plugins/Rain Meadow.dll"): args.plugin,
        Path("plugins/Newtonsoft.Json.dll"): ROOT / "libs/rain-meadow/Newtonsoft.Json.dll",
        Path("Newtonsoft.Json.LICENSE.md"): ROOT / "libs/rain-meadow/Newtonsoft.Json.LICENSE.md",
        Path("LICENSE"): ROOT / "LICENSE",
        Path("licenses/MIT.txt"): ROOT / "LICENSES/MIT.txt",
        Path("licenses/JZlib.txt"): ROOT / "src/mods/Rain-Meadow-1.16.3/Utils/zlib/LICENSE.jzlib.txt",
    }
    if args.steam_probe:
        for folder, destination in (("plugins", "plugins/steam-probe"), ("licenses", "licenses/steam-probe")):
            staged = {Path(destination) / path: source for path, source in files_in(args.steam_probe / folder)}
            if not staged:
                parser.error(f"Missing Steam probe package files: {folder}")
            replacements.update(staged)

    for folder in AUDIO_DIRS:
        normalized = {folder / path: source for path, source in files_in(args.audio / folder)}
        if not normalized:
            parser.error(f"Missing staged audio directory: {folder}")
        replacements.update(normalized)

    files = {}
    for mod in (args.mod, *args.overlay_mod):
        if not mod.is_dir():
            parser.error(f"Missing mod asset directory: {mod}")
        files.update({
            path: source for path, source in files_in(mod)
            if path.parts[0] not in {"plugins", "patchers"}
            and path != BUNDLE.with_suffix(".manifest")
            and not any(path.is_relative_to(folder) for folder in AUDIO_DIRS)
        })
    if Path("modinfo.json") not in files:
        parser.error("The upstream Mod folder must contain modinfo.json")
    files.update(replacements)
    required = set(replacements.values())
    for source in files.values():
        if not source.is_file() or (source in required and source.stat().st_size == 0):
            parser.error(f"Missing candidate input: {source}")
    inputs = (args.mod, *args.overlay_mod, args.audio)
    if args.output in files.values() or any(args.output.is_relative_to(folder) for folder in inputs):
        parser.error("Choose a package path outside the input asset directories")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(prefix=".package-", dir=args.output.parent, delete=False) as temporary:
        candidate = Path(temporary.name)
    try:
        with ZipFile(candidate, "w") as archive:
            for path, source in sorted(files.items()):
                entry = ZipInfo(path.as_posix())
                entry.compress_type = ZIP_STORED if path.suffix in {".ogg", ".png"} else ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                with source.open("rb") as original, archive.open(entry, "w") as packed:
                    shutil.copyfileobj(original, packed)
        with ZipFile(candidate) as archive:
            failure = archive.testzip()
            if failure:
                raise ValueError(f"Failed ZIP CRC validation: {failure}")
        candidate.replace(args.output)
    finally:
        candidate.unlink(missing_ok=True)
    print(f"{len(files)} files: {args.output.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
