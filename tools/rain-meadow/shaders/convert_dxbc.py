#!/usr/bin/env python3
"""Translate extracted DXBC into independently validated SPIR-V and GLSL ES."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from collections import Counter


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('extracted', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--vkd3d', required=True)
    p.add_argument('--spirv-cross', required=True)
    p.add_argument('--spirv-val', default='spirv-val')
    p.add_argument('--glslang', default='glslangValidator')
    args = p.parse_args()
    inventory = json.loads((args.extracted / 'inventory.json').read_text())
    args.output.mkdir(parents=True, exist_ok=False)
    report = dict(input_sha256=inventory['sha256'], programs=[], commands=[],
                  unity_bundle_created=False, game_rendering_verified=False)

    def run(command, log):
        result = subprocess.run(list(map(str, command)), text=True, capture_output=True)
        log.write_text(result.stdout + result.stderr)
        report['commands'].append(dict(command=list(map(str, command)), exit_code=result.returncode,
                                       log=str(log.relative_to(args.output))))
        return result.returncode == 0

    for identity, relative in inventory['unique_dxbc'].items():
        sample = next(p for p in inventory['programs'] if p['dxbc_sha256'] == identity)
        stage = 'vert' if 'Vertex' in sample['program_type'] else 'frag'
        folder = args.output / identity
        folder.mkdir()
        source = args.extracted / relative
        if hashlib.sha256(source.read_bytes()).hexdigest() != identity:
            raise ValueError('Extracted program digest mismatch: ' + str(source))
        spirv = folder / ('shader.' + stage + '.spv')
        entry = dict(dxbc_sha256=identity, shader=sample['shader'], stage=stage,
                     spirv=False, spirv_valid=False, gles={}, reflection=None)
        report['programs'].append(entry)
        entry['spirv'] = run([args.vkd3d, '-x', 'dxbc-tpf', '-b', 'spirv-binary',
                              '-o', spirv, source], folder / 'dxbc-to-spirv.log')
        if not entry['spirv']:
            continue
        entry['spirv_valid'] = run([args.spirv_val, '--target-env', 'vulkan1.0', spirv], folder / 'spirv-validation.log')
        reflection = folder / 'reflection.json'
        if run([args.spirv_cross, spirv, '--reflect', '--output', reflection], folder / 'reflection.log'):
            entry['reflection'] = str(reflection.relative_to(args.output))
        for version in [300, 310]:
            glsl = folder / f'es{version}.{stage}'
            converted = run([args.spirv_cross, spirv, '--es', '--version', str(version), '--output', glsl],
                            folder / f'glsl-es{version}.log')
            valid = converted and run([args.glslang, '-S', stage, glsl], folder / f'validate-es{version}.log')
            entry['gles'][str(version)] = dict(converted=converted, syntax_valid=valid,
                                               path=str(glsl.relative_to(args.output)) if converted else None)
    report['counts'] = dict(unique_dxbc=len(report['programs']),
        spirv_converted=sum(p['spirv'] for p in report['programs']),
        spirv_valid=sum(p['spirv_valid'] for p in report['programs']),
        gles_valid={str(v): sum(p['gles'].get(str(v), {}).get('syntax_valid', False) for p in report['programs']) for v in [300, 310]},
        stages=dict(Counter(p['stage'] for p in report['programs'])))
    (args.output / 'conversion-report.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report['counts'], indent=2))


if __name__ == '__main__':
    main()
