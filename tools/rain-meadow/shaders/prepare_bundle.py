#!/usr/bin/env python3
"""Fill the empty GoldenSkin pass from verified equivalent upstream bytecode."""

import argparse
import copy
import hashlib
import json
from pathlib import Path

import UnityPy

GOLDEN = "RM_GoldenSkin"
NIGHT = "RM_NightSkySkin"
DECLARATIONS = (
    "sampler2D _RM_NightSky_glow;\n"
    "float4 _RM_NightSky_ST;\n"
    "float4 _RM_NightSky_TexelSize;\n"
)


def pass_metadata(tree):
    [subshader] = tree["m_ParsedForm"]["m_SubShaders"]
    [shader_pass] = subshader["m_Passes"]
    return shader_pass


def comparable_pass(shader_pass):
    result = copy.deepcopy(shader_pass)
    for key in ("m_EditorDataHash", "m_NameIndices", "progVertex", "progFragment"):
        del result[key]
    del result["m_State"]["gpuProgramID"]
    return result


def verify_source(folder):
    target = (folder / f"{GOLDEN}.shader").read_text(encoding="utf-8")
    reference = (folder / f"{NIGHT}.shader").read_text(encoding="utf-8")
    expected = reference.replace(f'Shader "{NIGHT}"', f'Shader "{GOLDEN}"', 1)
    expected = expected.replace(DECLARATIONS, "", 1)
    if expected != target:
        raise ValueError("GoldenSkin differs from the verified shader source beyond missing declarations")
    return {"golden_sha256": hashlib.sha256(target.encode()).hexdigest(),
            "nightsky_sha256": hashlib.sha256(reference.encode()).hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("shader_sources", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.bundle.resolve() == args.output.resolve():
        parser.error("Output must differ from the original bundle")
    source_hashes = verify_source(args.shader_sources)

    env = UnityPy.load(str(args.bundle))
    objects = {}
    for obj in env.objects:
        if obj.type.name == "Shader":
            shader = obj.read_typetree()
            objects[shader["m_ParsedForm"]["m_Name"]] = (obj, shader)
    golden_obj, golden = objects[GOLDEN]
    _, night = objects[NIGHT]
    golden_pass, night_pass = pass_metadata(golden), pass_metadata(night)
    if comparable_pass(golden_pass) != comparable_pass(night_pass):
        raise ValueError("GoldenSkin and NightSkySkin pass state differs")
    if golden["platforms"] != [4] or night["platforms"] != [4]:
        raise ValueError("Expected upstream D3D11 source bundle")
    programs = ("progVertex", "progFragment")
    for stage in programs:
        if golden_pass[stage]["m_SubPrograms"] or not night_pass[stage]["m_SubPrograms"]:
            raise ValueError(f"Unexpected shader source status for {stage}")
    for key in (*programs, "m_NameIndices"):
        golden_pass[key] = copy.deepcopy(night_pass[key])
    for key in ("offsets", "compressedLengths", "decompressedLengths", "compressedBlob"):
        golden[key] = copy.deepcopy(night[key])
    golden_obj.save_typetree(golden)

    file = next(iter(env.files.values()))
    data = file.save(packer="lz4")
    with args.output.open("xb") as output:
        output.write(data)
    print(json.dumps({
        "upstream_bundle_sha256": hashlib.sha256(args.bundle.read_bytes()).hexdigest(),
        "prepared_bundle_sha256": hashlib.sha256(data).hexdigest(),
        "filled": GOLDEN,
        "equivalent_programs_from": NIGHT,
        "source_declarations_restored_for_equivalence_check": DECLARATIONS.splitlines(),
        **source_hashes,
    }, indent=2))


if __name__ == "__main__":
    main()
