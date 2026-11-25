#!/usr/bin/env python3
"""Extract validated DXBC containers and Unity binding metadata without changing a bundle."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
from collections import Counter
from types import SimpleNamespace
import UnityPy
from UnityPy.enums import ShaderCompilerPlatform, ShaderGpuProgramType
from UnityPy.helpers import CompressionHelper
from UnityPy.streams import EndianBinaryReader


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read_programs(raw, unity_version):
    # Same serialized GPU-program layout as UnityPy's ShaderConverter, without
    # importing unrelated audio exporters/native FMOD libraries on Android.
    if tuple(unity_version) < (2019, 3):
        raise ValueError('Only the 2019.3+ program table is admitted')
    reader = EndianBinaryReader(raw, endian='<')
    count = reader.read_int()
    if count < 0 or 4 + 12 * count > len(raw):
        raise ValueError('Invalid GPU program table')
    for index in range(count):
        reader.Position = 4 + index * 12
        offset = reader.read_int()
        if offset < 4 + 12 * count or offset >= len(raw):
            raise ValueError('GPU program table offset out of bounds')
        reader.Position = offset
        version = reader.read_int()
        if version != 201806140:
            raise ValueError('Unsupported GPU subprogram format: ' + str(version))
        kind = ShaderGpuProgramType(reader.read_int())
        reader.Position += 16
        keywords = [reader.read_aligned_string() for _ in range(reader.read_int())]
        local = [reader.read_aligned_string() for _ in range(reader.read_int())]
        code = reader.read_byte_array()
        yield SimpleNamespace(m_ProgramType=kind, m_Keywords=keywords,
                              m_LocalKeywords=local, m_ProgramCode=code)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    environment = UnityPy.load(str(args.bundle))
    report = dict(bundle=str(args.bundle.resolve()), sha256=sha(args.bundle.read_bytes()),
                  unitypy_version=UnityPy.__version__, shaders=[], programs=[], unique_dxbc={})
    for obj in environment.objects:
        if obj.type.name != 'Shader':
            continue
        tree = obj.read_typetree()
        name = tree['m_ParsedForm']['m_Name']
        folder = args.output / re.sub(r'[^A-Za-z0-9_.-]', '_', name)
        folder.mkdir()
        metadata = {k: value for k, value in tree.items() if k != 'compressedBlob'}
        (folder / 'unity-metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')
        version = obj.assets_file.unity_version
        entry = dict(name=name, path_id=obj.path_id, unity_version=version,
                     metadata=str((folder / 'unity-metadata.json').relative_to(args.output)),
                     platforms=[ShaderCompilerPlatform(p).name for p in tree['platforms']], programs=0)
        report['shaders'].append(entry)
        for platform, offset, compressed, decompressed in zip(tree['platforms'], tree['offsets'],
                tree['compressedLengths'], tree['decompressedLengths']):
            # This experiment admits one segment; fail explicitly on layouts not handled here.
            if any(len(values) != 1 for values in [offset, compressed, decompressed]):
                raise ValueError('Multiple GPU program segments require a separate decoder')
            raw = bytes(tree['compressedBlob'])[offset[0]:offset[0] + compressed[0]]
            raw = CompressionHelper.decompress_lz4(raw, decompressed[0])
            for index, sub in enumerate(read_programs(raw, obj.version)):
                code = bytes(sub.m_ProgramCode)
                magic = code.find(b'DXBC')
                if magic < 0:
                    raise ValueError(f'{name} program {index} has no DXBC container')
                size, chunks = struct.unpack_from('<II', code, magic + 24)
                dxbc = code[magic:magic + size]
                if size != len(dxbc) or magic + size != len(code) or size < 32 + chunks * 4:
                    raise ValueError('Invalid DXBC container bounds')
                chunk_names = []
                for chunk in range(chunks):
                    position = struct.unpack_from('<I', dxbc, 32 + 4 * chunk)[0]
                    length = struct.unpack_from('<I', dxbc, position + 4)[0]
                    if position + 8 + length > len(dxbc):
                        raise ValueError('DXBC chunk outside container')
                    chunk_names.append(dxbc[position:position + 4].decode('ascii'))
                identity = sha(dxbc)
                filename = folder / f'{index:03d}-{sub.m_ProgramType.name}.dxbc'
                filename.write_bytes(dxbc)
                item = dict(shader=name, platform=platform, blob_index=index,
                            program_type=sub.m_ProgramType.name,
                            keywords=sub.m_Keywords, local_keywords=sub.m_LocalKeywords,
                            unity_prefix=code[:magic].hex(), dxbc_sha256=identity,
                            size=size, chunks=chunk_names, path=str(filename.relative_to(args.output)))
                report['programs'].append(item)
                report['unique_dxbc'].setdefault(identity, item['path'])
                entry['programs'] += 1
    report['counts'] = dict(shaders=len(report['shaders']), programs=len(report['programs']),
                            unique_dxbc=len(report['unique_dxbc']),
                            program_types=dict(Counter(p['program_type'] for p in report['programs'])))
    (args.output / 'inventory.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report['counts'], indent=2))


if __name__ == '__main__':
    main()
