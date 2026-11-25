#!/usr/bin/env python3
"""Extract the installed game's small GLES-layout fixture into the workspace."""

import argparse
import hashlib
from pathlib import Path
from zipfile import ZipFile

import UnityPy

from build_gles_bundle import ROOT, verify_template

DATA_PATH = "assets/bin/Data/"
SHADER = "shaders/ripplespawnbody"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path, help="Read-only Android APK input")
    parser.add_argument("output", type=Path, help="New file within the workspace work directory")
    parser.add_argument("--managed", type=Path, default=ROOT / "archives/game/Managed.zip",
                        help="Workspace Managed.zip to verify the APK identity")
    args = parser.parse_args()

    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "work"):
        parser.error("Output must be in the workspace work directory")
    managed_sha = hashlib.sha256(args.managed.read_bytes()).hexdigest()
    with ZipFile(args.apk) as apk:
        if hashlib.sha256(apk.read("assets/Mono/Managed.zip")).hexdigest() != managed_sha:
            raise ValueError("APK's Managed.zip differs from the workspace assembly archive")
        manager_env = UnityPy.load(apk.read(DATA_PATH + "globalgamemanagers"))
        manager = next(obj for obj in manager_env.objects if obj.type.name == "ResourceManager")
        [pointer] = [pointer for name, pointer in manager.read().m_Container if name == SHADER]
        external = manager.assets_file.externals[pointer.m_FileID - 1].path
        data = apk.read(DATA_PATH + external)

    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as file:
        file.write(data)
    digest = verify_template(output)
    print(f"Verified GLES3 template from APK Managed.zip {managed_sha}")
    print(f"Serialized player shader {external} -> {output} (SHA-256 {digest})")


if __name__ == "__main__":
    main()
