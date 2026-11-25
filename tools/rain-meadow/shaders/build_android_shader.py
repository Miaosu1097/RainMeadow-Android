#!/usr/bin/env python3
"""Build and verify Rain Meadow's Android Shader AssetBundle in one run."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


TOOL_DIR = Path(__file__).resolve().parent
ROOT = TOOL_DIR.parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path, help="Rain Meadow source snapshot")
    parser.add_argument("output", type=Path, help="New work directory inside the workspace")
    parser.add_argument("--vkd3d", required=True)
    parser.add_argument("--spirv-cross", required=True)
    parser.add_argument("--backend", choices=("gles", "vulkan"), default="gles")
    parser.add_argument("--android-template", type=Path, help="Current Android GLES serialized shader fixture")
    parser.add_argument("--device-probe", action="store_true", help="Link with the native Android EGL GLES driver")
    parser.add_argument("--smolv", help="Codec with Unity's legacy SMOL-V format for Vulkan")
    parser.add_argument("--probe", help="Native Android Vulkan pipeline test executable")
    parser.add_argument("--probe-library-path", help="Library path used by the native graphics probe")
    args = parser.parse_args()
    if args.backend == "gles" and args.android_template is None:
        parser.error("--android-template is required for GLES")
    if args.backend == "vulkan" and args.smolv is None:
        parser.error("--smolv is required for Vulkan")

    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "work"):
        parser.error("Run output must be in the workspace work directory")
    checkout = args.checkout.resolve()
    if not checkout.is_relative_to(ROOT):
        parser.error("Checkout must be inside the workspace")
    bundle = checkout / "Mod/assetbundles/rainmeadow"
    if not bundle.is_file():
        parser.error(f"Shader bundle not found at {bundle}")

    temp = (ROOT / ".cache/tmp").resolve()
    if not temp.is_relative_to(ROOT):
        parser.error("Workspace temporary directory resolves outside the workspace")
    temp.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, TMPDIR=str(temp), TMP=str(temp), TEMP=str(temp),
               XDG_CACHE_HOME=str(ROOT / ".cache"), PYTHONDONTWRITEBYTECODE="1",
               MESA_SHADER_CACHE_DISABLE="1")
    output.mkdir(parents=True, exist_ok=False)

    def run(script, *arguments):
        command = [sys.executable, "-B", str(TOOL_DIR / f"{script}.py"), *map(str, arguments)]
        log_path = output / f"{script}.log"
        with log_path.open("x", encoding="utf-8") as log:
            completed = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
        if completed.returncode:
            details = log_path.read_text(encoding="utf-8")
            raise SystemExit(f"{script} exited {completed.returncode}. See {log_path}\n{details[-4000:]}")

    prepared = output / "prepared.d3d11"
    programs = output / "programs"
    spirv = output / "spirv"
    vulkan = output / "vulkan"
    run("prepare_bundle", bundle, checkout / "Shaders", prepared)
    run("extract_shader_programs", prepared, programs)
    run("convert_dxbc", programs, spirv, "--vkd3d", args.vkd3d, "--spirv-cross", args.spirv_cross)
    counts = json.loads((spirv / "conversion-report.json").read_text())["counts"]
    if counts["spirv_valid"] != counts["unique_dxbc"] or counts["unique_dxbc"] == 0:
        raise SystemExit(f"Incomplete SPIR-V validation. See {spirv / 'conversion-report.json'}")
    if args.backend == "gles":
        gles = output / "gles"
        run("build_gles_bundle", prepared, programs, spirv, gles, "--spirv-cross", args.spirv_cross,
            "--android-template", args.android_template)
        candidate = gles / "stage/assetbundles/rainmeadow"
        report = gles / "report.json"
        if args.device_probe:
            report = gles / "egl-verification.json"
            if args.probe_library_path:
                env["LD_LIBRARY_PATH"] = args.probe_library_path
            run("probe_gles_bundle", candidate, report)
    else:
        run("make_vulkan_bundle", prepared, programs, spirv, vulkan, "--smolv", args.smolv,
            "--spirv-cross", args.spirv_cross)
        verification = output / "verification"
        probe = ["--probe", args.probe] if args.probe else []
        if args.probe_library_path:
            probe.extend(["--library-path", args.probe_library_path])
        run("verify_vulkan_bundle", vulkan, verification, "--source-bundle", prepared,
            "--smolv", args.smolv, *probe)
        candidate = vulkan / "modded_shaders.vulkan.experimental"
        report = verification / "verification.json"

    target = output / "stage/assetbundles/rainmeadow"
    target.parent.mkdir(parents=True)
    shutil.copyfile(candidate, target)
    print(f"Android {args.backend} AssetBundle verified and staged at {target}")
    print(f"Verification report {report}")


if __name__ == "__main__":
    main()
