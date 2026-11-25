#!/usr/bin/env python3
"""Repack paired GLSL ES stages in the Unity 2020.3 GLES3 GPU record layout."""

import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import subprocess

import UnityPy
from UnityPy.helpers import CompressionHelper

from make_vulkan_bundle import empty, merge_gpu_constants, signatures, variant
from unity_gpu_record import read, read_table, write, write_table

GLES_PLATFORM = 9
GLES_PROGRAM = 4
GLES_PREFIX = b"#ifdef VERTEX\n#version 300 es\n"
STAGE_DELIMITER = b"#endif\n#ifdef FRAGMENT\n"
BLOCK = re.compile(r"layout\(std140\) uniform \w+\s*\{\s*(?:highp )?vec4 _m0\[(\d+)\];\s*\}\s*(cb(\d+)_\d+(?:_\d+)?);")
SAMPLER = re.compile(r"\bSPIRV_Cross_Combinedt(\d+)s(\d+)\b")
ROOT = Path(__file__).resolve().parents[3]


def get_records(tree, platform):
    index = tree["platforms"].index(platform)
    for key in ("offsets", "compressedLengths", "decompressedLengths"):
        if len(tree[key][index]) != 1:
            raise ValueError("Only single-segment shader GPU records are supported")
    [offset], [size], [length] = (tree[key][index] for key in
                                 ("offsets", "compressedLengths", "decompressedLengths"))
    contents = bytes(tree["compressedBlob"][offset:offset + size])
    return [read(record) for record in read_table(CompressionHelper.decompress_lz4(contents, length))]


def find_shader(environment, name):
    return next(tree for obj in environment.objects if obj.type.name == "Shader"
                for tree in [obj.read_typetree()] if tree["m_ParsedForm"]["m_Name"] == name)


def verify_template(path):
    probes = UnityPy.load(str(path))
    tree = find_shader(probes, "Futile/RippleSpawnBody")
    shaders = [obj for obj in probes.objects if obj.type.name == "Shader"]
    if not shaders or any(obj.assets_file.unity_version != "2020.3.45f1"
                          or int(obj.assets_file.target_platform) != 13 for obj in shaders):
        raise ValueError("Expected the Android Unity 2020.3.45f1 fixture")
    records = get_records(tree, GLES_PLATFORM)
    empty_program = next(p for s in tree["m_ParsedForm"]["m_SubShaders"]
                         for p in s["m_Passes"] if p["m_Type"] == 2)
    if empty_program["m_Platforms"]:
        raise ValueError("Unexpected fixture GrabPass platform list")
    for sub in tree["m_ParsedForm"]["m_SubShaders"]:
        for p in sub["m_Passes"]:
            vertices = [v for v in p["progVertex"]["m_SubPrograms"]
                        if v["m_GpuProgramType"] == GLES_PROGRAM]
            if not vertices:
                continue
            if p["progFragment"]["m_SubPrograms"] or {v["m_ShaderHardwareTier"] for v in vertices} != {0, 1, 2}:
                raise ValueError("Unexpected fixture GLES stage layout")
            for vertex in vertices:
                record = records[vertex["m_BlobIndex"]]
                if (record["header"] != [201806140, GLES_PROGRAM, 0, 0, 0, 0]
                        or not record["code"].startswith(GLES_PREFIX)
                        or b"#ifdef FRAGMENT\n#version 300 es" not in record["code"]
                        or [g["name"] for g in record["groups"]] != ["", "$Globals", "$Globals"]
                        or record["channels"] or vertex["m_Channels"]["m_Channels"]
                        or record["source_map"] != vertex["m_Channels"]["m_SourceMap"]
                        or any(vertex["m_Parameters"].values())):
                    raise ValueError("GLES fixture GPU record differs from supported layout")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def replace_token(text, name, replacement):
    return re.sub(r"\b" + re.escape(name) + r"\b", replacement, text)


def buffer_fields(buffer, names):
    fields = []
    for key in ("m_VectorParams", "m_MatrixParams"):
        for field in buffer[key]:
            if field["m_Type"] != 0 or field["m_ArraySize"] != 0:
                raise ValueError(f"Unsupported GLSL uniform type or array: {names[field['m_NameIndex']]}")
            fields.append((names[field["m_NameIndex"]], field, key == "m_MatrixParams"))
    return fields


def unpack_buffer(glsl, block, buffer, names):
    vectors, instance, _ = block.groups()
    slots = [["0.0"] * 4 for _ in range(int(vectors))]
    declarations = {}
    stage_parameters = {}

    for name, field, matrix in buffer_fields(buffer, names):
        offset = field["m_Index"]
        if matrix:
            rows = field["m_RowCount"]
            if offset % 16 or rows != 4:
                raise ValueError(f"Unsupported GLSL matrix shape for {name}")
            shader_name = "hlslcc_mtx4x4" + name
            declarations[name] = f"vec4 {shader_name}[4]"
            stage_parameters[name] = [0, rows, 4, 1, 0, offset]
            for row in range(rows):
                if (offset // 16) + row < len(slots):
                    slots[(offset // 16) + row] = [f"{shader_name}[{row}].{component}" for component in "xyzw"]
        else:
            dim = field["m_Dim"]
            if offset % 4 or (offset % 16) // 4 + dim > 4:
                raise ValueError(f"Unexpected GLSL uniform alignment for {name}")
            declaration = "float" if dim == 1 else f"vec{dim}"
            declarations[name] = f"{declaration} {name}"
            stage_parameters[name] = [0, 1, dim, 0, 0, offset]
            if offset // 16 < len(slots):
                for index in range(dim):
                    component = "" if dim == 1 else "." + "xyzw"[index]
                    slots[offset // 16][(offset % 16) // 4 + index] = name + component

    glsl = glsl.replace(block.group(), "", 1)
    pattern = re.compile(r"\b" + re.escape(instance) + r"\._m0\[(\d+)u?\](?:\.([xyzw]{1,4}))?")

    def extract(match):
        index = int(match[1])
        if index >= len(slots) or all(value == "0.0" for value in slots[index]):
            raise ValueError(f"GLSL reads an unbound Unity constant register {instance}[{index}]")
        selected = [slots[index]["xyzw".index(ch)] for ch in match[2] or "xyzw"]
        if len(selected) == 1:
            return selected[0]
        return f"vec{len(selected)}({', '.join(selected)})"

    glsl = pattern.sub(extract, glsl)
    if re.search(r"\b" + re.escape(instance) + r"\b", glsl):
        raise ValueError(f"Unsupported GLSL access into uniform block {instance}")
    active = {name: value for name, value in declarations.items()
              if re.search(r"\b" + re.escape(value.split()[1].split("[")[0]) + r"\b", glsl)}
    group = dict(name=names[buffer["m_NameIndex"]], size=buffer["m_Size"],
                 parameters=[dict(name=name, values=stage_parameters[name]) for name in active])
    return glsl, active, group


def convert_stage(source, mapping, params, names, record, stage, path, spirv_cross):
    optimized = path / f"{stage}.spv"
    subprocess.run(["spirv-opt", "--combine-access-chains", "--remove-unused-interface-variables",
                    "--eliminate-dead-variables", source, "-o", optimized], check=True)
    reflection = json.loads(subprocess.check_output([spirv_cross, optimized, "--reflect"]))
    glsl = subprocess.check_output([spirv_cross, optimized, "--es", "--version", "300"], text=True)
    glsl = glsl.replace("precision mediump float;", "precision highp float;")
    buffers = {buffer["m_NameIndex"]: buffer for buffer in params["m_ConstantBuffers"]}
    by_register = {binding["m_Index"]: buffers[binding["m_NameIndex"]]
                   for binding in params["m_ConstantBufferBindings"]}
    declared = {}
    groups = []
    for match in list(BLOCK.finditer(glsl)):
        glsl, active, group = unpack_buffer(glsl, match, by_register[int(match[3])], names)
        for key, value in active.items():
            if key in declared and declared[key] != value:
                raise ValueError(f"Inconsistent GLSL declaration for {key}")
            declared[key] = value
        if group["parameters"]:
            groups.append(group)
    if "layout(std140) uniform" in glsl:
        raise ValueError("Unexpected unresolved GLES uniform block")

    texture_map = {p["m_Index"]: (names[p["m_NameIndex"]], p["m_SamplerIndex"])
                   for p in params["m_TextureParams"]}
    for binding in record["bindings"]:
        if binding["kind"] == 0:
            register, sampler, dimension = binding["values"]
            if dimension != 4 or texture_map.setdefault(register, (binding["name"], sampler)) != (binding["name"], sampler):
                raise ValueError("Disagreement in texture metadata")
    textures = {}
    for match in list(SAMPLER.finditer(glsl)):
        register, sampler = map(int, match.groups())
        name, source_sampler = texture_map[register]
        if source_sampler != sampler:
            raise ValueError(f"Unsupported multiple sampler states for {name}")
        glsl = replace_token(glsl, match.group(), name)
        textures[register] = name
    if re.search(r"\bSPIRV_Cross_Combined", glsl):
        raise ValueError("Unresolved GLSL sampler")
    if len(set(textures.values())) != len(textures):
        raise ValueError("Texture name appears in multiple active registers")

    direction = "outputs" if stage == "vert" else "inputs"
    for variable in reflection.get(direction, []):
        glsl = replace_token(glsl, variable["name"], f"rm_varying_{variable['location']}")
    source_map = 0
    if stage == "vert":
        for entry in reflection.get("inputs", []):
            sem = mapping[entry["location"]]
            if sem["source"] is None:
                raise ValueError(f"Unknown vertex semantic {sem['semantic']}")
            name = f"in_{sem['semantic']}{sem['index']}"
            source_map |= 1 << sem["source"]
            glsl = replace_token(glsl, entry["name"], name)
            glsl = re.sub(r"layout\(location\s*=\s*\d+\)\s+(?=in\s+\w+\s+" + name + r"\b)", "", glsl)

    for _, declaration in sorted(declared.items(), reverse=True):
        glsl = glsl.replace("#version 300 es\n", f"#version 300 es\nuniform highp {declaration};\n", 1)
    (path / f"shader.{stage}").write_text(glsl)
    return glsl, source_map, declared, groups, list(dict.fromkeys(textures.values()))


def loose_globals(groups):
    """Translate named DXBC buffer fields into a native GLES stage's loose uniforms."""
    layout = {}
    offset = 0
    for group in groups:
        for parameter in group["parameters"]:
            name = parameter["name"]
            shape = parameter["values"][:5]
            if name in layout:
                if layout[name]["values"][:5] != shape:
                    raise ValueError(f"Conflicting uniform shape in the same GLES stage: {name}")
                continue
            _, rows, columns, matrix, _ = shape
            alignment = 16 if matrix or columns >= 3 else columns * 4
            offset = (offset + alignment - 1) // alignment * alignment
            layout[name] = dict(name=name, values=[*shape, offset])
            offset += rows * 16 if matrix else columns * 4
    parameters = sorted(layout.values(), key=lambda p: (p["values"][3], p["values"][-1]))
    return [dict(name="$Globals", size=offset, parameters=parameters)] if parameters else []


def build_program(vertex, fragment, source_programs, names, records, source_paths, args, path):
    stages = {}
    names_list = list(names)
    new_names = {name: number for number, name in names.items()}

    def get_name(name):
        if name not in new_names:
            new_names[name] = max(names_list, default=-1) + 1
            names_list.append(new_names[name])
            names[new_names[name]] = name
        return new_names[name]

    for stage, meta, program in zip(("vert", "frag"), (vertex, fragment), source_programs):
        record = records[meta["m_BlobIndex"]]
        specimen = source_paths[meta["m_BlobIndex"]]
        source = args.conversion / specimen["dxbc_sha256"] / f"shader.{stage}.spv"
        mapping = signatures((args.extracted / specimen["path"]).read_bytes()) if stage == "vert" else {}
        params = {key: copy.deepcopy(program["m_CommonParameters"][key] + meta["m_Parameters"][key])
                  for key in empty()}
        merge_gpu_constants(params, record["groups"], names, get_name)
        stages[stage] = convert_stage(source, mapping, params, names, record, stage, path, args.spirv_cross)

    vert, frag = stages["vert"], stages["frag"]
    for name in set(vert[2]) & set(frag[2]):
        if vert[2][name] != frag[2][name]:
            raise ValueError(f"GLSL stage uniform declarations disagree: {name}")
    textures = list(dict.fromkeys(vert[4] + frag[4]))
    keywords = records[vertex["m_BlobIndex"]]
    return dict(header=[201806140, GLES_PROGRAM, 0, 0, 0, 0],
                keywords=keywords["keywords"], local_keywords=keywords["local_keywords"],
                code=(f"#ifdef VERTEX\n{vert[0]}#endif\n#ifdef FRAGMENT\n{frag[0]}#endif\n").encode(),
                source_map=vert[1], channels=[],
                groups=[dict(name="", size=0, parameters=[])] + loose_globals(frag[3]) + loose_globals(vert[3]),
                bindings=[dict(name=name, kind=0, values=[i, i, 4]) for i, name in enumerate(textures)])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("bundle", type=Path, help="Prepared D3D11 bundle")
    p.add_argument("extracted", type=Path, help="Bound DXBC program inventory")
    p.add_argument("conversion", type=Path, help="Validated SPIR-V intermediate output")
    p.add_argument("output", type=Path, help="New run directory inside work/")
    p.add_argument("--spirv-cross", required=True)
    p.add_argument("--android-template", type=Path, required=True, help="Unity 2020.3.45f1 serialized Futile/RippleSpawnBody fixture")
    args = p.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(ROOT / "work"):
        p.error("Output must be inside the workspace work directory")

    template_sha = verify_template(args.android_template)
    original_hash = hashlib.sha256(args.bundle.read_bytes()).hexdigest()
    inventory = json.loads((args.extracted / "inventory.json").read_text())
    compiled = json.loads((args.conversion / "conversion-report.json").read_text())
    if original_hash != inventory["sha256"] or original_hash != compiled["input_sha256"]:
        raise ValueError("DXBC and SPIR-V input hashes do not match the bundle")
    args.output.mkdir(parents=True)
    report = dict(source_sha256=original_hash, android_template_sha256=template_sha,
                  unity_version="2020.3.45f1", backend="GLES3x", shaders=[])
    env = UnityPy.load(str(args.bundle))

    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        tree = obj.read_typetree()
        name = tree["m_ParsedForm"]["m_Name"]
        source_records = get_records(tree, 4)
        source_paths = {p["blob_index"]: p for p in inventory["programs"] if p["shader"] == name}
        gl_records, variants = [], []
        for sub in tree["m_ParsedForm"]["m_SubShaders"]:
            for shader_pass in sub["m_Passes"]:
                vertex_prog, frag_prog = shader_pass["progVertex"], shader_pass["progFragment"]
                vertices, fragments = vertex_prog["m_SubPrograms"], frag_prog["m_SubPrograms"]
                if not vertices and not fragments:
                    if shader_pass["m_Type"] != 2:
                        raise ValueError(f"Empty non-GrabPass in {name}")
                    shader_pass["m_Platforms"] = []
                    continue
                shader_pass["m_Platforms"] = [GLES_PLATFORM]
                frag_by_variant = {variant(f): f for f in fragments}
                if set(frag_by_variant) != {variant(v) for v in vertices}:
                    raise ValueError(f"Vertex/fragment keyword variants differ in {name}")
                new_vertices = []
                for vertex in vertices:
                    if vertex["m_ShaderHardwareTier"] != 3:
                        raise ValueError("Expected one PC tier per keyword variant")
                    pair_num = len(gl_records)
                    folder = args.output / name / f"variant-{pair_num:03}"
                    folder.mkdir(parents=True)
                    names = {index: label for label, index in shader_pass["m_NameIndices"]}
                    record = build_program(vertex, frag_by_variant[variant(vertex)], (vertex_prog, frag_prog),
                                           names, source_records, source_paths, args, folder)
                    link = subprocess.run(["glslangValidator", "-l", folder / "shader.vert", folder / "shader.frag"],
                                          capture_output=True, text=True)
                    (folder / "link.log").write_text(link.stdout + link.stderr)
                    if link.returncode:
                        raise ValueError(f"GLES shader pair did not link: {folder}\n{link.stdout}{link.stderr}")
                    gl_records.append(write(record))
                    variants.append(dict(path=str(folder.relative_to(args.output)),
                                         global_keywords=record["keywords"], local_keywords=record["local_keywords"],
                                         source_map=record["source_map"], inputs=[g["name"] for g in record["groups"]],
                                         textures=[t["name"] for t in record["bindings"]],
                                         program_sha256=hashlib.sha256(record["code"]).hexdigest()))
                    for tier in (0, 1, 2):
                        gpu_meta = copy.deepcopy(vertex)
                        gpu_meta.update(m_BlobIndex=pair_num, m_GpuProgramType=GLES_PROGRAM,
                                        m_ShaderHardwareTier=tier, m_Parameters=empty(),
                                        m_Channels=dict(m_SourceMap=record["source_map"], m_Channels=[]))
                        new_vertices.append(gpu_meta)
                vertex_prog["m_SubPrograms"] = new_vertices
                frag_prog["m_SubPrograms"] = []
                vertex_prog["m_CommonParameters"] = frag_prog["m_CommonParameters"] = empty()
        gl_blob = write_table(gl_records)
        compressed = CompressionHelper.compress_lz4(gl_blob)
        tree.update(platforms=[GLES_PLATFORM], offsets=[[0]],
                    compressedLengths=[[len(compressed)]], decompressedLengths=[[len(gl_blob)]],
                    compressedBlob=list(compressed))
        obj.save_typetree(tree)
        report["shaders"].append(dict(name=name, path_id=obj.path_id, variants=variants))

    file = next(iter(env.files.values()))
    for serialized in file.files.values():
        if hasattr(serialized, "target_platform"):
            serialized.target_platform = serialized._m_target_platform = 13
    output = args.output / "stage/assetbundles/rainmeadow"
    output.parent.mkdir(parents=True)
    output.write_bytes(file.save(packer="lz4"))
    reopened = UnityPy.load(str(output))
    for shader in report["shaders"]:
        tree = find_shader(reopened, shader["name"])
        records = get_records(tree, GLES_PLATFORM)
        assert tree["platforms"] == [GLES_PLATFORM]
        if len(records) != len(shader["variants"]):
            raise ValueError(f"Round-trip lost variants for {shader['name']}")
        for record, expected in zip(records, shader["variants"]):
            if (hashlib.sha256(record["code"]).hexdigest() != expected["program_sha256"]
                    or record["source_map"] != expected["source_map"]
                    or record["keywords"] != expected["global_keywords"]
                    or record["local_keywords"] != expected["local_keywords"]):
                raise ValueError(f"GLES GPU record failed round-trip for {shader['name']}")
    if any(int(obj.assets_file.target_platform) != 13 for obj in reopened.objects):
        raise ValueError("Android platform tag did not survive serialization")
    report.update(output_sha256=hashlib.sha256(output.read_bytes()).hexdigest(),
                  compiled_variants=sum(len(s["variants"]) for s in report["shaders"]))
    (args.output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"Staged {len(report['shaders'])} GLES shaders with {report['compiled_variants']} compiled variants: {output}")


if __name__ == "__main__":
    main()
