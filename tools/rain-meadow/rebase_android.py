"""Reapply a local Rain Meadow port to a new upstream source ZIP."""

import argparse
import stat
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory
from zipfile import ZipFile
from zlib import crc32

ROOT = Path(__file__).resolve().parents[2]


def workspace_path(value):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT):
        raise argparse.ArgumentTypeError(f"Path is outside the workspace: {value}")
    return path


def members(archive):
    result = {}
    for item in archive.infolist():
        if item.is_dir():
            continue
        path = Path(item.filename)
        if path.is_absolute() or ".." in path.parts or len(path.parts) < 2 or stat.S_ISLNK(item.external_attr >> 16):
            raise ValueError(f"Unsafe archive member: {item.filename}")
        relative = Path(*path.parts[1:])
        if relative in result:
            raise ValueError(f"Duplicate archive member: {relative}")
        result[relative] = item
    return result


def fingerprint(info):
    return (info.file_size, info.CRC) if info else None


def merge(path, scratch, ported, previous, upstream):
    inputs = [scratch / name for name in ("android", "base", "upstream")]
    for filename, contents in zip(inputs, (ported, previous, upstream)):
        filename.write_bytes(contents)
    result = subprocess.run(["git", "merge-file", "-p", *map(str, inputs)], capture_output=True)
    if result.returncode:
        raise ValueError(f"Review upstream and Android changes in {path} before rebasing.")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("previous", type=workspace_path)
    parser.add_argument("upstream", type=workspace_path)
    parser.add_argument("port", type=workspace_path)
    parser.add_argument("output", type=workspace_path)
    args = parser.parse_args()
    if args.output.exists() or args.output.is_relative_to(args.port):
        parser.error("Choose a new directory beside the existing port.")

    temp_root = ROOT / ".cache/tmp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with ZipFile(args.previous) as previous, ZipFile(args.upstream) as upstream, TemporaryDirectory(dir=temp_root) as temporary:
        base, updated = members(previous), members(upstream)
        scratch = Path(temporary)
        destination = scratch / "source"
        destination.mkdir()
        local = {}
        for path in args.port.rglob("*"):
            if path.is_symlink():
                raise ValueError(f"Linked port file: {path}")
            if path.is_file():
                local[path.relative_to(args.port)] = path

        carried = []
        for path in sorted(base.keys() | updated.keys() | local.keys()):
            old, new, current = base.get(path), updated.get(path), local.get(path)
            ported = current.read_bytes() if current else None
            local_key = (len(ported), crc32(ported)) if ported is not None else None
            if local_key == fingerprint(old):
                content = upstream.read(new) if new else None
            elif fingerprint(new) in (fingerprint(old), local_key):
                content = ported
                carried.append(path)
            elif old and new and ported is not None and path.suffix == ".cs":
                content = merge(path, scratch, ported, previous.read(old), upstream.read(new))
                carried.append(path)
            else:
                raise ValueError(f"Review upstream and Android changes in {path} before rebasing.")

            if content is not None:
                file = destination / path
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_bytes(content)

        destination.replace(args.output)
        for path in carried:
            print(f"Carried Android change: {path}")


if __name__ == "__main__":
    main()
