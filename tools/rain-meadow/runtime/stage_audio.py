"""Audit and stage Rain Meadow audio paths for the Android AssetManager lookup."""

import argparse
import json
import os
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
LOOKUP_ROOTS = ("music/songs", "soundeffects")
AUDIO_SUFFIXES = {".ogg", ".wav"}


def workspace_path(value):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT):
        raise argparse.ArgumentTypeError(f"Path must be inside the workspace: {value}")
    return path


def inspect(path, environment, decode):
    metadata = subprocess.run(
        [
            "ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries",
            "stream=codec_name,sample_rate,channels,duration", "-of", "json", str(path),
        ],
        capture_output=True, text=True, env=environment, check=True,
    )
    streams = json.loads(metadata.stdout).get("streams", [])
    if not streams:
        raise ValueError(f"No audio stream in {path}")
    if decode:
        subprocess.run(
            [
                "ffmpeg", "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-threads", "1",
                "-i", str(path), "-map", "0:a:0", "-f", "null", "-",
            ],
            capture_output=True, text=True, env=environment, check=True,
        )
    return streams[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mod", type=workspace_path, help="Rain Meadow's Mod asset root")
    parser.add_argument("--stage", type=workspace_path, help="New directory for the audio asset tree")
    parser.add_argument("--decode", action="store_true", help="Decode each audio asset through FFmpeg")
    args = parser.parse_args()
    if args.stage and (args.stage.exists() or args.stage.is_relative_to(args.mod)):
        parser.error("--stage must be a new directory outside the source assets")

    entries = {}
    for name in LOOKUP_ROOTS:
        folder = args.mod / name
        if not folder.is_dir() or folder.resolve() != folder:
            parser.error(f"Missing or linked asset directory: {folder}")
        for file in sorted(folder.rglob("*")):
            if file.is_symlink():
                parser.error(f"Linked assets are unsupported: {file}")
            if not file.is_file():
                continue
            relative = file.relative_to(args.mod).as_posix()
            lower = relative.lower()
            if lower in entries:
                parser.error(f"Case collision: {entries[lower]} versus {file}")
            entries[lower] = file

    audio_paths = [path for path in entries.values() if path.suffix.lower() in AUDIO_SUFFIXES]
    if not audio_paths:
        parser.error("No audio assets found")
    temp_path = ROOT / ".cache/tmp"
    temp_path.mkdir(parents=True, exist_ok=True)
    environment = {**os.environ, "TMPDIR": str(temp_path), "XDG_CACHE_HOME": str(ROOT / ".cache")}
    try:
        with ThreadPoolExecutor(max_workers=3) as pool:
            streams = list(pool.map(lambda path: inspect(path, environment, args.decode), audio_paths))
    except subprocess.CalledProcessError as error:
        parser.exit(1, f"Audio validation failed on {error.cmd}: {error.stderr}")
    metadata = dict(zip(audio_paths, streams))

    changed = []
    audio = []
    for lower, file in entries.items():
        original = file.relative_to(args.mod).as_posix()
        if original != lower:
            changed.append({"source": original, "android": lower})
        if file in metadata:
            audio.append({"path": lower, **metadata[file]})

    if args.stage:
        args.stage.mkdir(parents=True)
        for lower, file in entries.items():
            destination = args.stage / lower
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(file, destination)

    print(json.dumps({
        "assets": len(entries),
        "case_mismatches": changed,
        "audio_streams": audio,
        "fully_decoded": args.decode,
    }, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
