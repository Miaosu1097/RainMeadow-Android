"""Compare original PC constant layouts with the emitted native Vulkan records."""
import hashlib
import UnityPy
from UnityPy.helpers import CompressionHelper
from unity_gpu_record import read, read_table


def shaders(path):
    result = {}
    for obj in UnityPy.load(str(path)).objects:
        if obj.type.name != 'Shader':
            continue
        tree = obj.read_typetree()
        offset, size = tree['offsets'][0][0], tree['compressedLengths'][0][0]
        blob = CompressionHelper.decompress_lz4(
            bytes(tree['compressedBlob'])[offset:offset + size], tree['decompressedLengths'][0][0])
        passes = [p for s in tree['m_ParsedForm']['m_SubShaders'] for p in s['m_Passes']]
        result[tree['m_ParsedForm']['m_Name']] = (passes, [read(r) for r in read_table(blob)])
    return result


def key(meta):
    return (tuple(meta.get('m_KeywordIndices', [])), tuple(meta.get('m_GlobalKeywordIndices', [])),
            tuple(meta.get('m_LocalKeywordIndices', [])), meta['m_ShaderHardwareTier'])


def audit(source, target, conversion):
    if hashlib.sha256(source.read_bytes()).hexdigest() != conversion['input_sha256']:
        raise ValueError('Constant audit source digest mismatch')
    originals, outputs = shaders(source), shaders(target)
    report = dict(pairs=0, buffers=0, parameters=0, gpu_tail_parameters=0, inactive_parameters=0, missing=[])
    for converted in conversion['converted']:
        name = converted['shader']
        source_passes, source_records = originals[name]
        target_passes, target_records = outputs[name]
        if len(source_passes) != len(target_passes):
            raise ValueError('Constant audit pass count mismatch: ' + name)
        pair_index = 0
        for old_pass, new_pass in zip(source_passes, target_passes):
            names = {i: n for n, i in old_pass['m_NameIndices']}
            fragments = {key(m): m for m in old_pass['progFragment']['m_SubPrograms']}
            old_vertices = old_pass['progVertex']['m_SubPrograms']
            new_vertices = new_pass['progVertex']['m_SubPrograms']
            if len(old_vertices) != len(new_vertices):
                raise ValueError('Constant audit variant count mismatch: ' + name)
            for vertex, output in zip(old_vertices, new_vertices):
                item = converted['pairs'][pair_index]
                if item['pair'] != pair_index or key(vertex) != key(output):
                    raise ValueError('Constant audit variant identity mismatch: ' + name)
                native = target_records[output['m_BlobIndex']]
                target_groups = {g['name']: g for g in native['groups']}
                target_bindings = {b['values'][0]: b['name'] for b in native['bindings'] if b['kind'] == 1}
                for stage, program, meta in (
                        ('vert', old_pass['progVertex'], vertex),
                        ('frag', old_pass['progFragment'], fragments[key(vertex)])):
                    common = program.get('m_CommonParameters', {})
                    own = meta.get('m_Parameters', meta)
                    bindings = {names[b['m_NameIndex']]: b['m_Index']
                                for b in common.get('m_ConstantBufferBindings', []) + own.get('m_ConstantBufferBindings', [])}
                    expected = {}
                    for cb in common.get('m_ConstantBuffers', []) + own.get('m_ConstantBuffers', []):
                        fields = {}
                        for field in cb['m_VectorParams']:
                            fields[names[field['m_NameIndex']]] = [field['m_Type'], 1, field['m_Dim'], 0,
                                                                   field['m_ArraySize'], field['m_Index']]
                        for field in cb['m_MatrixParams']:
                            fields[names[field['m_NameIndex']]] = [field['m_Type'], field['m_RowCount'], 4, 1,
                                                                   field['m_ArraySize'], field['m_Index']]
                        expected[names[cb['m_NameIndex']]] = dict(size=cb['m_Size'], fields=fields)
                    for group in source_records[meta['m_BlobIndex']]['groups']:
                        if not group['name']:
                            if group['parameters']:
                                raise ValueError('Unnamed source GPU constants require explicit handling')
                            continue
                        cb = expected.setdefault(group['name'], dict(size=group['size'], fields={}))
                        if cb['size'] != group['size']:
                            raise ValueError('Conflicting original constant buffer size')
                        for field in group['parameters']:
                            report['gpu_tail_parameters'] += 1
                            old = cb['fields'].setdefault(field['name'], field['values'])
                            if old != field['values']:
                                raise ValueError('Conflicting original constant parameter')
                    resources = {r['register']: r for r in item['resources'][stage] if r['kind'] == 'cb'}
                    for buffer_name, cb in expected.items():
                        register = bindings.get(buffer_name)
                        if register is None:
                            raise ValueError('Original buffer without register: ' + buffer_name)
                        if register not in resources:
                            report['inactive_parameters'] += len(cb['fields'])
                            continue
                        resource = resources[register]
                        packed = ((4 if stage == 'vert' else 8) << 24) | (resource['set'] << 16) | resource['binding']
                        group = target_groups[target_bindings[packed]]
                        if group['size'] != cb['size']:
                            raise ValueError('Target constant buffer size mismatch')
                        fields = {f['name']: f['values'] for f in group['parameters']}
                        report['buffers'] += 1
                        for field_name, values in cb['fields'].items():
                            report['parameters'] += 1
                            if fields.get(field_name) != values:
                                report['missing'].append(dict(shader=name, pair=pair_index, stage=stage,
                                    buffer=buffer_name, name=field_name, expected=values, actual=fields.get(field_name)))
                pair_index += 1
                report['pairs'] += 1
        if pair_index != len(converted['pairs']):
            raise ValueError('Constant audit conversion count mismatch')
    report['passed'] = not report['missing']
    return report
