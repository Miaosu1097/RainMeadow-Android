"""Fetch pinned build inputs and stage them inside the checkout."""

import argparse
from hashlib import sha256
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
from tempfile import TemporaryDirectory, mkdtemp
from zipfile import BadZipFile, ZipFile

ROOT = Path(__file__).resolve().parents[2]
TOOL = Path(__file__).resolve().parent
CACHE = ROOT / '.cache/rain-meadow/inputs'
WORK = ROOT / 'work/rain-meadow/inputs'
UPSTREAM_COMMIT = '59bbcf55c3815b6d23ca564780cf299cf63599a7'
UPSTREAM_INPUT_SHA256 = '45c7a8b3878130da3ef142d982d7cffa8b49515143251053f224776540cb8ccb'
UPSTREAM_FOLDERS = (('Mod',), ('Shaders',))
VALVE_ARCHIVE = 'bins_androidarm64_linuxarm64.zip.c4ba08337562d6397c6c459a8e0b7d6f0ea04bb7'
VALVE_SHA256 = 'ad6b9a0653302f8818c43fe8da4f5ff68e2ead6994069e5d93c4021ce2281d91'


def stream_hash(file):
    digest = sha256()
    for block in iter(lambda: file.read(1024 * 1024), b''):
        digest.update(block)
    return digest


def file_hash(path):
    with path.open('rb') as file:
        return stream_hash(file).hexdigest()


def verify(path, expected):
    if file_hash(path) != expected:
        raise ValueError(f'SHA-256 mismatch: {path}')


def game_snapshot(revision):
    if revision in ('.', '..') or not re.fullmatch(r'[a-zA-Z0-9._-]+', revision):
        raise ValueError(f'Invalid game revision: {revision!r}')
    for line in (TOOL / 'game-inputs.tsv').read_text().splitlines():
        if line.strip() and not line.startswith('#'):
            name, commit, digest = line.split('\t')
            if name == revision:
                if not re.fullmatch(r'[0-9a-f]{40}', commit) or not re.fullmatch(r'[0-9a-f]{64}', digest):
                    raise ValueError(f'Invalid pinned game input for {revision}')
                return commit, digest
    raise ValueError(f'Unpinned game revision: {revision}')


def download(url, name, check):
    CACHE.mkdir(parents=True, exist_ok=True)
    destination = CACHE / name
    if destination.is_file():
        try:
            check(destination)
        except (ValueError, BadZipFile):
            pass
        else:
            return destination
    with TemporaryDirectory(dir=CACHE) as temporary:
        incoming = Path(temporary) / name
        subprocess.run(['curl', '-q', '-fsSL', '--retry', '2', '--connect-timeout', '15',
                        '--max-time', '360', url, '-o', str(incoming)], check=True)
        check(incoming)
        incoming.replace(destination)
    return destination


def extract(archive, destination, *, prefix='', folders=(), flat=False):
    extracted = set()
    with ZipFile(archive) as source:
        for entry in source.infolist():
            if entry.is_dir() or not entry.filename.startswith(prefix):
                continue
            name = entry.filename[len(prefix):]
            relative = PurePosixPath(name)
            if folders and relative.parts[:1] not in folders:
                continue
            if not relative.parts or relative.is_absolute() or '..' in relative.parts or '\\' in name:
                raise ValueError(f'Unsafe archive path: {entry.filename}')
            if relative in extracted or (flat and len(relative.parts) != 1):
                raise ValueError(f'Unexpected archive path: {entry.filename}')
            extracted.add(relative)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with source.open(entry) as data, target.open('xb') as file:
                shutil.copyfileobj(data, file)
    if not extracted:
        raise ValueError(f'No matching build inputs in {archive}')


def prepare_game(revision, archive):
    _, digest = game_snapshot(revision)
    verify(archive, digest)
    root = WORK / 'game'
    root.mkdir(parents=True, exist_ok=True)
    staging = Path(mkdtemp(prefix=f'{revision}-', dir=root))
    try:
        managed = staging / 'Managed'
        extract(archive, managed, flat=True)
    except Exception:
        shutil.rmtree(staging)
        raise
    return managed.relative_to(ROOT)


def upstream_prefix(source):
    if source.comment != UPSTREAM_COMMIT.encode():
        raise ValueError('Unexpected Rain Meadow source commit')
    root, _, _ = source.namelist()[0].partition('/')
    if not root.startswith('Rain-Meadow-'):
        raise ValueError('Unexpected Rain Meadow source archive directory')
    return root + '/'


def check_upstream(archive):
    # GitHub may regenerate the ZIP with different compression or a tag-named root.
    # Hash relative paths and file contents for the folders used by the build.
    digest = sha256()
    with ZipFile(archive) as source:
        prefix = upstream_prefix(source)
        names = sorted(name for name in source.namelist()
                       if name.startswith((prefix + 'Mod/', prefix + 'Shaders/')) and not name.endswith('/'))
        for name in names:
            digest.update(name[len(prefix):].encode() + b'\0')
            with source.open(name) as file:
                digest.update(stream_hash(file).digest())
    if digest.hexdigest() != UPSTREAM_INPUT_SHA256:
        raise ValueError(f'Rain Meadow Mod or Shaders contents do not match {UPSTREAM_COMMIT}: {archive}')


def prepare_upstream(archive):
    if archive is None and os.environ.get('RAIN_MEADOW_UPSTREAM_ARCHIVE'):
        archive = Path(os.environ['RAIN_MEADOW_UPSTREAM_ARCHIVE'])
    if archive is None:
        archive = download(f'https://codeload.github.com/henpemaz/Rain-Meadow/zip/{UPSTREAM_COMMIT}',
                           f'upstream-{UPSTREAM_COMMIT}.zip', check_upstream)
    else:
        check_upstream(archive)
    destination = WORK / f'upstream-{UPSTREAM_COMMIT}'
    if not destination.is_dir():
        WORK.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=WORK) as temporary:
            unpacked = Path(temporary) / 'checkout'
            with ZipFile(archive) as source:
                prefix = upstream_prefix(source)
            extract(archive, unpacked, prefix=prefix, folders=UPSTREAM_FOLDERS)
            if not (unpacked / 'Mod/modinfo.json').is_file() or not (unpacked / 'Shaders').is_dir():
                raise ValueError('Upstream archive is missing Mod or Shaders')
            unpacked.rename(destination)
    return destination.relative_to(ROOT)


def prepare_valve():
    archive = download(f'https://client-update.steamstatic.com/{VALVE_ARCHIVE}', VALVE_ARCHIVE,
                       lambda path: verify(path, VALVE_SHA256))
    destination = WORK / 'valve/libsteamnetworkingsockets.so'
    destination.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(archive) as source, TemporaryDirectory(dir=destination.parent) as temporary:
        staged = Path(temporary) / destination.name
        with source.open(f'androidarm64/{destination.name}') as data, staged.open('wb') as file:
            shutil.copyfileobj(data, file)
        staged.replace(destination)
    return destination.relative_to(ROOT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('game-commit').add_argument('revision')
    game = commands.add_parser('game')
    game.add_argument('revision')
    game.add_argument('archive', type=Path)
    commands.add_parser('upstream').add_argument('archive', nargs='?', type=Path)
    commands.add_parser('valve')
    args = parser.parse_args()
    try:
        if args.command == 'game-commit':
            result, _ = game_snapshot(args.revision)
        elif args.command == 'game':
            result = prepare_game(args.revision, args.archive)
        elif args.command == 'upstream':
            result = prepare_upstream(args.archive)
        else:
            result = prepare_valve()
    except (OSError, ValueError, BadZipFile, subprocess.CalledProcessError) as error:
        parser.exit(1, f'{error}\n')
    print(result)


if __name__ == '__main__':
    main()
