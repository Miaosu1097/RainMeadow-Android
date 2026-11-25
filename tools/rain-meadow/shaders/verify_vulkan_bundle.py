#!/usr/bin/env python3
"""Verify native record/metadata consistency, then optionally create real Vulkan pipelines."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import UnityPy
from UnityPy.helpers import CompressionHelper
from unity_gpu_record import read,read_table
from make_vulkan_bundle import instructions
from unity_vulkan_spirv import verify_position_y_flip, nested_uniform_access_chains
from source_constant_audit import audit as audit_source_constants


def declared_resources(record):
    result={'vert':set(),'frag':set()}
    def add(value):
        if value < 0:raise ValueError('Invalid negative resource binding')
        stage={4:'vert',8:'frag'}.get(value>>24)
        if stage is None:raise ValueError('Unsupported packed shader stage')
        result[stage].add(((value>>16)&255,value&65535))
    for binding in record['bindings']:
        add(binding['values'][0])
        if binding['kind']==0 and binding['values'][1]!=-1:add(binding['values'][1])
    return result


def spirv_resources(module):
    decorations={}
    for _,opcode,args in instructions(module):
        if opcode==71 and args[1] in (33,34):decorations.setdefault(args[0],{})[args[1]]=args[2]
    result=set()
    for identity,values in decorations.items():
        if 33 not in values:continue
        if 34 not in values:raise ValueError('Descriptor binding without set')
        pair=(values[34],values[33])
        if pair in result:raise ValueError('Duplicate descriptor binding in one stage')
        result.add(pair)
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('converted',type=Path);p.add_argument('output',type=Path)
    p.add_argument('--smolv',required=True);p.add_argument('--probe')
    p.add_argument('--independent-smolv',help='Optional second decoder supporting Unity legacy encoding')
    p.add_argument('--library-path',help='Explicit runtime library search path for the native probe only')
    p.add_argument('--bundle',type=Path,help='Optional retargeted bundle; program artifacts still come from converted')
    p.add_argument('--source-bundle',type=Path,help='Original bundle for independent constant-layout coverage checks')
    p.add_argument('--unity-version',default='2020.3.45f1')
    p.add_argument('--file-format',type=int,default=22)
    args=p.parse_args();args.output.mkdir(parents=True,exist_ok=False)
    report=dict(shaders=0,pairs=0,program_records=0,decoded_modules=0,pipelines=[],engine_executed=False,draw_submitted=False)
    conversion=json.loads((args.converted/'conversion.json').read_text())
    report['uniform_access_chains_verified']=bool(conversion.get('uniform_access_chains_combined'))
    if conversion.get('gpu_constants_merged') and not args.source_bundle:
        raise ValueError('Merged GPU constants require --source-bundle for independent verification')
    if args.source_bundle:
        constants=audit_source_constants(args.source_bundle,args.bundle or args.converted/'modded_shaders.vulkan.experimental',conversion)
        (args.output/'source-constant-audit.json').write_text(json.dumps(constants,indent=2)+'\n')
        if not constants['passed']:
            raise ValueError('Original constant parameters missing or changed: '+str(len(constants['missing'])))
        report['source_constants']={k:v for k,v in constants.items() if k!='missing'}
    shader_records={}
    env=UnityPy.load(str(args.bundle or args.converted/'modded_shaders.vulkan.experimental'))
    for obj in env.objects:
        if obj.type.name!='Shader':continue
        tree=obj.read_typetree();name=tree['m_ParsedForm']['m_Name']
        if name in conversion['unconverted']:continue
        assert tree['platforms']==[18] and int(obj.assets_file.target_platform)==13
        assert obj.assets_file.unity_version==args.unity_version and obj.assets_file.header.version==args.file_format
        blob=CompressionHelper.decompress_lz4(bytes(tree['compressedBlob']),tree['decompressedLengths'][0][0])
        records=[read(x) for x in read_table(blob)];report['program_records']+=len(records)
        pairs=[]
        for subshader in tree['m_ParsedForm']['m_SubShaders']:
            for shader_pass in subshader['m_Passes']:
                if 'm_Platforms' in shader_pass:assert shader_pass['m_Platforms']==[18]
                names={index:value for value,index in shader_pass['m_NameIndices']}
                for vertex,fragment in zip(shader_pass['progVertex']['m_SubPrograms'],shader_pass['progFragment']['m_SubPrograms']):
                    assert vertex['m_GpuProgramType']==fragment['m_GpuProgramType']==25
                    native=records[vertex['m_BlobIndex']];dummy=records[fragment['m_BlobIndex']]
                    assert native['header'][1]==dummy['header'][1]==25 and not dummy['code']
                    assert native['source_map']==vertex['m_Channels']['m_SourceMap']
                    assert native['channels']==[[x['source'],x['target']] for x in vertex['m_Channels']['m_Channels']]
                    params=vertex.get('m_Parameters',vertex)
                    groups={g['name']:g for g in native['groups'] if g['name']}
                    for cb in params['m_ConstantBuffers']:
                        group=groups[names[cb['m_NameIndex']]]
                        assert group['size']==cb['m_Size']
                        expected=[]
                        for field in cb['m_VectorParams']:
                            expected.append((names[field['m_NameIndex']],(field['m_Type'],1,field['m_Dim'],0,field['m_ArraySize'],field['m_Index'])))
                        for field in cb['m_MatrixParams']:
                            expected.append((names[field['m_NameIndex']],(field['m_Type'],field['m_RowCount'],4,1,field['m_ArraySize'],field['m_Index'])))
                        assert sorted(expected)==sorted((field['name'],tuple(field['values'])) for field in group['parameters'])
                    expected_bindings=[]
                    for binding in params['m_ConstantBufferBindings']:
                        expected_bindings.append(dict(name=names[binding['m_NameIndex']],kind=1,values=[binding['m_Index'],0]))
                    for binding in params['m_TextureParams']:
                        expected_bindings.append(dict(name=names[binding['m_NameIndex']],kind=0,values=[binding['m_Index'],binding['m_SamplerIndex'],4]))
                    assert sorted(map(lambda x:json.dumps(x,sort_keys=True),expected_bindings))==sorted(map(lambda x:json.dumps(x,sort_keys=True),native['bindings']))
                    pairs.append(native)
        shader_records[name]=pairs;report['shaders']+=1;report['pairs']+=len(pairs)
    unique={};decoded={}
    for shader in conversion['converted']:
        for item,native in zip(shader['pairs'],shader_records[shader['shader']]):
            pair=args.converted/item['path'];lines=[];stage_modules={}
            for slot,stage in [(0,'vert'),(1,'frag')]:
                offset,size=struct.unpack_from('<II',native['code'],4+slot*8)
                encoded=native['code'][offset:offset+size];identity=hashlib.sha256(encoded).hexdigest()
                if identity not in decoded:
                    source=args.output/(identity+'.smolv');target=args.output/(identity+'.spv');source.write_bytes(encoded)
                    subprocess.run([args.smolv,'decode',str(source),str(target)],check=True)
                    subprocess.run(['spirv-val','--target-env','vulkan1.0',str(target)],check=True)
                    if args.independent_smolv:
                        second=args.output/(identity+'-independent.spv')
                        subprocess.run([args.independent_smolv,'decode',str(source),str(second)],check=True)
                        assert second.read_bytes()==target.read_bytes()
                    decoded[identity]=target.read_bytes();report['decoded_modules']+=1
                assert decoded[identity]==(pair/(stage+'.spv')).read_bytes()
                if conversion.get('uniform_access_chains_combined') and nested_uniform_access_chains(decoded[identity]):
                    raise ValueError('Nested Uniform access chain in final GPU program')
                if stage=='vert' and conversion.get('clip_y')=='unity':verify_position_y_flip(decoded[identity])
                expected=declared_resources(native)[stage]
                actual=spirv_resources(decoded[identity])
                if actual!=expected:
                    raise ValueError('SPIR-V/Unity resource coverage mismatch: shader='+shader['shader']+' stage='+stage+
                        ' missing='+repr(sorted(actual-expected))+' extra='+repr(sorted(expected-actual)))
                stage_modules[stage]=decoded[identity]
                for resource in item['resources'][stage]:lines.append(f"{resource['kind']} {resource['set']} {resource['binding']} {1 if stage=='vert' else 16} 0")
            inputs=json.loads((pair/'vert-reflection.json').read_text())['inputs']
            for value in inputs:lines.append(f"input {value['location']} {int(value['type'][-1]) if value['type'].startswith('vec') else 1}")
            text='\n'.join(lines)+'\n'
            identity=hashlib.sha256(stage_modules['vert']+stage_modules['frag']+text.encode()).hexdigest()
            if identity in unique:
                unique[identity]['variants']+=1;continue
            entry=dict(shader=shader['shader'],pair=item['pair'],variants=1,path=str(pair),pipeline_created=None)
            unique[identity]=entry
            if args.probe:
                plan=args.output/(identity+'.tsv');plan.write_text(text)
                env=dict(os.environ)
                if args.library_path:env['LD_LIBRARY_PATH']=args.library_path
                command=[args.probe,str(pair/'vert.spv'),str(pair/'frag.spv'),str(plan)]
                run=subprocess.run(command,capture_output=True,text=True,env=env)
                (args.output/(identity+'.log')).write_text(run.stdout+run.stderr)
                entry.update(pipeline_created=run.returncode==0 and 'PASS Vulkan graphics pipeline created' in run.stdout,
                             exit_code=run.returncode,command=command)
    report['pipelines']=list(unique.values());report['unique_pipelines']=len(unique)
    report['pipeline_successes']=sum(e['pipeline_created'] is True for e in unique.values())
    (args.output/'verification.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k!='pipelines'},indent=2))
    if args.probe and report['pipeline_successes']!=report['unique_pipelines']:return 1
    return 0


if __name__=='__main__':raise SystemExit(main())
