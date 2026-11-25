"""Reject tracked binary files before obtaining external build inputs."""

from pathlib import Path
import os
import subprocess
import sys


def main():
    paths = subprocess.check_output(['git', 'ls-files', '-z']).split(b'\0')[:-1]
    binaries = []
    for encoded in paths:
        file = Path(os.fsdecode(encoded))
        if file.is_symlink():
            binaries.append(file)
            continue
        data = file.read_bytes()
        try:
            data.decode('utf-8')
        except UnicodeDecodeError:
            binaries.append(file)
            continue
        if b'\0' in data:
            binaries.append(file)
    if binaries:
        print('Move tracked binary inputs out of the source repository:', file=sys.stderr)
        for file in binaries:
            print(f'  {file}', file=sys.stderr)
        return 1
    print(f'Checked {len(paths)} tracked source files.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
