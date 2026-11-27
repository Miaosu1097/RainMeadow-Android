"""Build and verify file-level Android mod update packages."""

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile, ZipInfo


FULL_ZIP = "RainMeadow-android.zip"
LEGACY_ZIP = "RainMeadow-android-steam-sdr.zip"
DELTA_ZIP = "RainMeadow-android-delta.zip"
DELTA_MANIFEST = "RainMeadow-android-delta.json"
UPDATE_FRAGMENT = "RainMeadow-android-update.json"
TAG = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_.-]*")
SHA = re.compile(r"[0-9a-f]{64}")
REQUIRED = {"modinfo.json", "plugins/Rain Meadow.dll"}


def digest(stream):
    sha = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        sha.update(chunk)
    return sha.hexdigest()


def file_digest(path):
    with path.open("rb") as stream:
        return digest(stream)


def valid_path(name):
    return (
        all(part and not part.startswith(".") and part == part.rstrip(". ")
            for part in name.split("/"))
        and not any(ord(char) < 32 or char in '\\:*?"<>|' for char in name)
    )


def inventory(archive):
    files = {}
    seen = set()
    for entry in archive.infolist():
        name = entry.filename
        if entry.is_dir():
            if not valid_path(name[:-1]):
                raise ValueError(f"Unsafe ZIP directory: {name!r}")
            continue
        if (
            not valid_path(name) or name.casefold() in seen
            or stat.S_ISLNK(entry.external_attr >> 16)
        ):
            raise ValueError(f"Unsafe or duplicate ZIP entry: {name!r}")
        seen.add(name.casefold())
        with archive.open(entry) as stream:
            files[name] = digest(stream)
    return files


def operations(base, target):
    paths_by_case = {}
    for name in base.keys() | target.keys():
        if not valid_path(name):
            raise ValueError(f"Unsafe update path: {name!r}")
        previous = paths_by_case.setdefault(name.casefold(), name)
        if previous != name:
            raise ValueError(f"Case-only file name change requires a full install: {previous}, {name}")
    for name in paths_by_case:
        parent = name.rpartition("/")[0]
        while parent:
            if parent in paths_by_case:
                raise ValueError(f"File/directory layout change requires a full install: {parent}")
            parent = parent.rpartition("/")[0]
    return (
        sorted(name for name, sha in target.items() if base.get(name) != sha),
        sorted(base.keys() - target.keys()),
    )


def validate_tag(tag):
    if len(tag) > 128 or not TAG.fullmatch(tag) or ".." in tag or tag.endswith((".", ".lock")):
        raise ValueError(f"Unsupported release tag: {tag!r}")


def validate_full(archive, files):
    if not REQUIRED.issubset(files):
        raise ValueError("Full install ZIP lacks root modinfo.json or the main plugin")
    if json.loads(archive.read("modinfo.json")).get("id") != "henpemaz_rainmeadow":
        raise ValueError("Full install ZIP is for a different mod")


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
                    encoding="utf-8")


def make_delta(base_zip, target_zip, output, revision, base_tag, target_tag):
    for tag in (revision, base_tag, target_tag):
        validate_tag(tag)
    diff_tag = f"{target_tag}-delta-{revision}"
    validate_tag(diff_tag)
    if base_tag == target_tag:
        raise ValueError("The base and target releases must be different")
    with ZipFile(base_zip) as before, ZipFile(target_zip) as after:
        base = inventory(before)
        target = inventory(after)
        validate_full(before, base)
        validate_full(after, target)
        changed, removed = operations(base, target)
        if not changed and not removed:
            print("The two install ZIPs contain the same files. No update package is needed.")
            return None

        output.mkdir(parents=True, exist_ok=True)
        patch = output / DELTA_ZIP
        with ZipFile(patch, "w") as archive:
            for name in changed:
                entry = ZipInfo(name)
                entry.compress_type = ZIP_STORED if name.endswith((".ogg", ".png")) else ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                with after.open(name) as content, archive.open(entry, "w") as destination:
                    shutil.copyfileobj(content, destination)
    manifest = output / DELTA_MANIFEST
    write_json(manifest, {
        "schema": 1,
        "game_revision": revision,
        "base_tag": base_tag,
        "target_tag": target_tag,
        "archive_sha256": file_digest(patch),
        "base_files": base,
        "target_files": target,
    })
    write_json(output / UPDATE_FRAGMENT, {
        "delta": {"tag": diff_tag, "manifest_sha256": file_digest(manifest)},
    })
    verify_delta(base_zip, patch, manifest, target_zip)
    print(f"{len(changed)} added or changed, {len(removed)} removed, {patch.stat().st_size} bytes.")
    return diff_tag


def verify_delta(base_zip, patch_zip, manifest_file, target_zip):
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    if manifest.get("schema") != 1 or file_digest(patch_zip) != manifest["archive_sha256"]:
        raise ValueError("Unsupported or corrupt update package")
    base = manifest["base_files"]
    target = manifest["target_files"]
    changed, removed = operations(base, target)
    if not all(SHA.fullmatch(sha) for sha in (*base.values(), *target.values())):
        raise ValueError("Invalid file hash in update manifest")
    with ZipFile(base_zip) as before, ZipFile(patch_zip) as patch, ZipFile(target_zip) as after:
        if inventory(before) != base or inventory(after) != target:
            raise ValueError("Update manifest does not match the base or target install ZIP")
        if inventory(patch) != {name: target[name] for name in changed}:
            raise ValueError("Update ZIP is missing files or contains unexpected entries")
    reconstructed = {name: sha for name, sha in base.items() if name not in removed}
    reconstructed.update({name: target[name] for name in changed})
    if reconstructed != target:
        raise ValueError("The update does not reconstruct the target install ZIP")


def gh(*arguments):
    return subprocess.check_output(["gh", *arguments], text=True)


def find_base(repository, revision, target_tag):
    lines = gh("api", "--paginate", f"repos/{repository}/releases?per_page=100", "--jq",
               '.[] | select(.draft == false and .prerelease == false)')
    candidates = []
    for line in lines.splitlines():
        release = json.loads(line)
        if (not release["published_at"] or release["tag_name"] == target_tag
                or not (release.get("body") or "").startswith(
                    f"Rain Meadow Android ARM64 build for game snapshot {revision}.")):
            continue
        names = {item["name"] for item in release["assets"]}
        for zip_name in (FULL_ZIP, LEGACY_ZIP):
            if {zip_name, zip_name + ".sha256"}.issubset(names):
                candidates.append((datetime.fromisoformat(release["published_at"].replace("Z", "+00:00")),
                                   release["tag_name"], zip_name))
                break
    return max(candidates)[1:] if candidates else None


def draft_delta(repository, revision, target_tag, target_zip):
    published = find_base(repository, revision, target_tag)
    if published is None:
        print(f"No published full release for game snapshot {revision}. The full draft is ready.")
        return
    base_tag, filename = published
    work = Path("work/rain-meadow")
    work.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="delta-", dir=work) as folder:
        root = Path(folder)
        gh("release", "download", base_tag, "--repo", repository, "--dir", str(root),
           "--pattern", filename, "--pattern", filename + ".sha256")
        base = root / filename
        expected = (root / (filename + ".sha256")).read_text(encoding="utf-8").split()
        if expected != [file_digest(base), filename]:
            raise ValueError(f"Could not verify full ZIP downloaded from {base_tag}")

        output = root / "update"
        tag = make_delta(base, target_zip, output, revision, base_tag, target_tag)
        if tag is None:
            return
        notes = (f"Android file update for game snapshot {revision}.\n\n"
                 f"Base full release: `{base_tag}`. Target full release: `{target_tag}`.\n")
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
        print(gh("release", "create", "--repo", repository, "--draft", "--prerelease",
                 "--target", commit, "--title", f"Android delta {base_tag} to {target_tag}",
                 "--notes", notes, "--", tag, str(output / DELTA_ZIP),
                 str(output / DELTA_MANIFEST), str(output / UPDATE_FRAGMENT)).strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("create")
    build.add_argument("base_zip", type=Path)
    build.add_argument("target_zip", type=Path)
    build.add_argument("output", type=Path)
    build.add_argument("revision")
    build.add_argument("base_tag")
    build.add_argument("target_tag")
    verify = commands.add_parser("verify")
    verify.add_argument("base_zip", type=Path)
    verify.add_argument("patch_zip", type=Path)
    verify.add_argument("manifest", type=Path)
    verify.add_argument("target_zip", type=Path)
    upload = commands.add_parser("draft")
    upload.add_argument("repository")
    upload.add_argument("revision")
    upload.add_argument("target_tag")
    upload.add_argument("target_zip", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "create":
            make_delta(args.base_zip, args.target_zip, args.output, args.revision,
                       args.base_tag, args.target_tag)
        elif args.command == "verify":
            verify_delta(args.base_zip, args.patch_zip, args.manifest, args.target_zip)
        else:
            draft_delta(args.repository, args.revision, args.target_tag, args.target_zip)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
