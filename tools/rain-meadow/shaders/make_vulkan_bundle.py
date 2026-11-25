#!/usr/bin/env python3
"""Experimental platform-only conversion; preserves the input Unity/file versions."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import UnityPy
from UnityPy.helpers import CompressionHelper
from unity_gpu_record import read, read_table, write, write_table
from unity_vulkan_spirv import flip_position_y, nested_uniform_access_chains


PARAMS = ['m_VectorParams','m_MatrixParams','m_TextureParams','m_BufferParams',
          'm_ConstantBuffers','m_ConstantBufferBindings','m_UAVParams','m_Samplers']


def empty(): return {key: [] for key in PARAMS}
def packed(stage, descriptor_set, binding): return ((4 if stage=='vert' else 8)<<24)|(descriptor_set<<16)|binding
def variant(p): return (tuple(p['m_GlobalKeywordIndices']),tuple(p['m_LocalKeywordIndices']),p['m_ShaderHardwareTier'])


def merge_gpu_constants(params, groups, names, add_name):
    buffers={names[cb['m_NameIndex']]:cb for cb in params['m_ConstantBuffers']}
    recovered=[]
    for group in groups:
        if not group['parameters']:continue
        cb=buffers.get(group['name'])
        if cb is None:raise ValueError('GPU constants without a serialized constant buffer: '+group['name'])
        if cb['m_Size']!=group['size']:raise ValueError('Constant-buffer sizes disagree: '+group['name'])
        existing={names[field['m_NameIndex']]:(kind,field) for kind in ['m_VectorParams','m_MatrixParams'] for field in cb[kind]}
        for parameter in group['parameters']:
            value_type,rows,columns,matrix,array_size,offset=parameter['values']
            if value_type not in (0,1) or array_size<0 or offset<0 or offset%4:
                raise ValueError('Unsupported GPU constant shape: '+parameter['name'])
            if matrix==0 and rows==1 and 1<=columns<=4:
                kind='m_VectorParams';shape=dict(m_Dim=columns);size=columns*4
                size+=max(0,array_size-1)*16
            elif matrix==1 and columns==4 and 1<=rows<=4:
                kind='m_MatrixParams';shape=dict(m_RowCount=rows);size=rows*16*max(1,array_size)
            else:raise ValueError('Unsupported GPU constant dimensions: '+parameter['name'])
            if offset+size>cb['m_Size']:raise ValueError('GPU constant exceeds its buffer: '+parameter['name'])
            field=dict(m_NameIndex=add_name(parameter['name']),m_Index=offset,m_ArraySize=array_size,m_Type=value_type,**shape)
            old=existing.get(parameter['name'])
            if old is not None:
                if old[0]!=kind or old[1]!=field:raise ValueError('Serialized/GPU constants disagree: '+parameter['name'])
                continue
            cb[kind].append(field);existing[parameter['name']]=(kind,field)
            recovered.append(dict(buffer=group['name'],name=parameter['name'],values=parameter['values']))
    return recovered


def instructions(data):
    words=list(struct.unpack('<'+str(len(data)//4)+'I',data));position=5
    while position<len(words):
        size,opcode=words[position]>>16,words[position]&65535
        if size==0 or position+size>len(words):raise ValueError('Invalid SPIR-V')
        yield position,opcode,words[position+1:position+size]
        position+=size


def decorate(data, resources, inputs):
    names={};decorations={}
    for _,opcode,args in instructions(data):
        if opcode==5:names[args[0]]=struct.pack('<'+'I'*(len(args)-1),*args[1:]).split(b'\0')[0].decode()
        if opcode==71 and args[1] in [30,33,34]:decorations.setdefault(args[0],{})[args[1]]=args[2]
    words=list(struct.unpack('<'+str(len(data)//4)+'I',data));mapped=[]
    by_id={}
    for identity,values in decorations.items():
        if 33 not in values:continue
        name=names.get(identity,'')
        match=re.fullmatch(r'cb(\d+)_\d+',name)
        kind,register=('cb',int(match[1])) if match else (name[:1],int(name[1:]))
        target=resources[(kind,register)]
        by_id[identity]=target; mapped.append(dict(name=name,kind=kind,register=register,set=target[0],binding=target[1]))
    for position,opcode,args in instructions(data):
        if opcode!=71:continue
        identity,decoration=args[:2]
        if identity in by_id and decoration in [33,34]:words[position+3]=by_id[identity][0 if decoration==34 else 1]
        if identity in names and names[identity] in inputs and decoration==30:words[position+3]=inputs[names[identity]]
    return struct.pack('<'+'I'*len(words),*words),mapped


def signatures(data):
    for i in range(struct.unpack_from('<I',data,28)[0]):
        offset=struct.unpack_from('<I',data,32+4*i)[0]
        if data[offset:offset+4]!=b'ISGN':continue
        chunk=data[offset+8:];result={}
        for j in range(struct.unpack_from('<I',chunk)[0]):
            name,index,system,kind,register,mask=struct.unpack_from('<6I',chunk,8+24*j)
            semantic=chunk[name:chunk.index(0,name)].decode()
            source={'POSITION':0,'NORMAL':1,'TANGENT':2,'COLOR':3}.get(semantic)
            if semantic=='TEXCOORD':source=4+index
            result[register]=dict(semantic=semantic,index=index,source=source)
        return result
    raise ValueError('No input signature')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('bundle',type=Path);p.add_argument('extracted',type=Path);p.add_argument('converted',type=Path);p.add_argument('output',type=Path)
    p.add_argument('--smolv',required=True);p.add_argument('--spirv-cross',required=True)
    p.add_argument('--shader',action='append',help='Only convert named shaders; other objects remain unchanged')
    p.add_argument('--clip-y',choices=('unity','unchanged'),default='unity',help='Use the native Unity Vulkan clip-space Y convention')
    args=p.parse_args();args.output.mkdir(parents=True,exist_ok=False)
    inventory=json.loads((args.extracted/'inventory.json').read_text())
    input_hash=hashlib.sha256(args.bundle.read_bytes()).hexdigest()
    if inventory['sha256']!=input_hash:raise ValueError('Extracted inventory belongs to a different bundle')
    baseline_report=args.converted/'conversion-report.json'
    if baseline_report.exists() and json.loads(baseline_report.read_text())['input_sha256']!=input_hash:
        raise ValueError('SPIR-V conversion cache belongs to a different bundle')
    baseline_manifest=args.converted/'baseline-manifest.json'
    if baseline_manifest.exists():
        manifest=json.loads(baseline_manifest.read_text())
        if manifest['input_bundle_sha256']!=input_hash:raise ValueError('Baseline manifest bundle mismatch')
        for item in manifest['files']:
            if hashlib.sha256((args.converted/item['path']).read_bytes()).hexdigest()!=item['sha256']:
                raise ValueError('SPIR-V baseline digest mismatch: '+item['path'])
    env=UnityPy.load(str(args.bundle));report=dict(input_sha256=hashlib.sha256(args.bundle.read_bytes()).hexdigest(),
        version_preserved=True,converted=[],unconverted=[],clip_y=args.clip_y,gpu_constants_merged=True,
        recovered_constants=[],uniform_access_chains_combined=True,uniform_access_chain_changes=[],
        engine_tested=False,visual_tested=False)
    buffer_layouts={}
    def run(command):
        result=subprocess.run(list(map(str,command)),capture_output=True,text=True)
        if result.returncode:raise RuntimeError(str(command)+'\n'+result.stdout+result.stderr)
        return result.stdout
    for obj in env.objects:
        if obj.type.name!='Shader':continue
        tree=obj.read_typetree();name=tree['m_ParsedForm']['m_Name']
        if args.shader and name not in args.shader:report['unconverted'].append(name);continue
        if tree['platforms'] != [4] or len(tree['offsets'][0]) != 1:
            raise ValueError('Only the inspected single-segment D3D11 input is supported')
        raw=bytes(tree['compressedBlob'])
        start=tree['offsets'][0][0];size=tree['compressedLengths'][0][0]
        source_records=[read(record) for record in read_table(CompressionHelper.decompress_lz4(
            raw[start:start+size],tree['decompressedLengths'][0][0]))]
        folder=args.output/re.sub(r'[^A-Za-z0-9_.-]','_',name);folder.mkdir()
        programs={x['blob_index']:x for x in inventory['programs'] if x['shader']==name}
        vertex_records=[];fragment_records=[];work_items=[]
        def add_name(shader_pass,value):
            names={v:k for k,v in shader_pass['m_NameIndices']}
            if value in names.values():return next(k for k,v in names.items() if v==value)
            index=max(names,default=-1)+1;shader_pass['m_NameIndices'].append([value,index]);return index
        for subshader in tree['m_ParsedForm']['m_SubShaders']:
            for shader_pass in subshader['m_Passes']:
                shader_pass['m_Platforms']=[18]
                vertices=shader_pass['progVertex'];fragments=shader_pass['progFragment']
                old_fragment={variant(v):v for v in fragments['m_SubPrograms']}
                new_vertices=[];new_fragments=[]
                for vertex in vertices['m_SubPrograms']:
                    fragment=old_fragment[variant(vertex)];pair_index=len(vertex_records)
                    combined=empty();all_bindings=[];all_groups=[dict(name='',size=0,parameters=[])]
                    all_modules={};counters={0:0,1:0};channels=[];stage_resources={}
                    pairdir=folder/f'pair-{pair_index:03}';pairdir.mkdir()
                    for stage,meta,program in [('vert',vertex,vertices),('frag',fragment,fragments)]:
                        source=programs[meta['m_BlobIndex']];identity=source['dxbc_sha256']
                        if hashlib.sha256((args.extracted/source['path']).read_bytes()).hexdigest()!=identity:
                            raise ValueError('Original DXBC digest mismatch')
                        spv=args.converted/identity/f'shader.{stage}.spv'
                        optimized=pairdir/f'{stage}.spv'
                        before=nested_uniform_access_chains(spv.read_bytes())
                        run(['spirv-opt','--combine-access-chains','--remove-unused-interface-variables','--eliminate-dead-variables',spv,'-o',optimized])
                        after=nested_uniform_access_chains(optimized.read_bytes())
                        if after:raise ValueError('Nested uniform accesses remain after normalization')
                        report['uniform_access_chain_changes'].append(dict(shader=name,pair=pair_index,stage=stage,before=before,after=after))
                        reflect=json.loads(run([args.spirv_cross,optimized,'--reflect']))
                        params=empty()
                        for key in PARAMS:params[key]=copy.deepcopy(program.get('m_CommonParameters',{}).get(key,[]))+copy.deepcopy(meta.get('m_Parameters',{}).get(key,[]))
                        if any(params[k] for k in ['m_BufferParams','m_UAVParams','m_Samplers']):raise ValueError('Unsupported resource kind')
                        source_names={index:value for value,index in shader_pass['m_NameIndices']}
                        recovered=merge_gpu_constants(params,source_records[meta['m_BlobIndex']]['groups'],
                            source_names,lambda value:add_name(shader_pass,value))
                        report['recovered_constants'].extend(dict(shader=name,pair=pair_index,stage=stage,**field) for field in recovered)
                        # Unity's PC player data may omit texture parameters from the
                        # serialized metadata while retaining them in the GPU record.
                        # Both carriers must be read before changing the backend.
                        serialized_textures={tex['m_Index']:tex for tex in params['m_TextureParams']}
                        if len(serialized_textures) != len(params['m_TextureParams']):
                            raise ValueError('Duplicate serialized texture register')
                        for native_binding in source_records[meta['m_BlobIndex']]['bindings']:
                            if native_binding['kind'] != 0:continue
                            register,sampler,dimension=native_binding['values']
                            if dimension != 4:
                                raise ValueError('Only inspected GPU-record 2D texture bindings are supported')
                            texture=dict(m_NameIndex=add_name(shader_pass,native_binding['name']),m_Index=register,
                                m_SamplerIndex=sampler,m_MultiSampled=False,m_Dim=2)
                            previous=serialized_textures.get(register)
                            if previous is not None and any(previous[key] != texture[key] for key in texture):
                                raise ValueError('Serialized/GPU-record texture bindings disagree')
                            if previous is None:
                                params['m_TextureParams'].append(texture);serialized_textures[register]=texture
                        names={index:value for value,index in shader_pass['m_NameIndices']}
                        # Determine original DXBC register numbers from the compiler's OpName records.
                        decorated_ids={};opnames={}
                        for _,opcode,operands in instructions(optimized.read_bytes()):
                            if opcode==5:opnames[operands[0]]=struct.pack('<'+'I'*(len(operands)-1),*operands[1:]).split(b'\0')[0].decode()
                            if opcode==71 and operands[1]==33:decorated_ids[operands[0]]=operands[2]
                        active=[]
                        for identity_id in decorated_ids:
                            variable=opnames[identity_id];match=re.fullmatch(r'cb(\d+)_\d+',variable)
                            active.append(('cb',int(match[1])) if match else (variable[:1],int(variable[1:])))
                        resources={}
                        for kind,register in sorted(active):
                            descriptor_set=1 if kind=='cb' else 0
                            resources[kind,register]=(descriptor_set,counters[descriptor_set]);counters[descriptor_set]+=1
                        admitted=set()
                        for register_binding in params['m_ConstantBufferBindings']:
                            register=register_binding['m_Index']
                            if ('cb',register) not in resources:continue
                            if register_binding.get('m_ArraySize',1) != 1:raise ValueError('Constant-buffer descriptor arrays are not supported')
                            admitted.add(('cb',register))
                            cb=copy.deepcopy(next(x for x in params['m_ConstantBuffers'] if x['m_NameIndex']==register_binding['m_NameIndex']))
                            if cb.get('m_StructParams'):raise ValueError('Structured constant buffer')
                            layout=dict(shader=name,stage=stage,register=register,size=cb['m_Size'])
                            for fields in ['m_VectorParams','m_MatrixParams']:
                                layout[fields]=[{**{k:v for k,v in field.items() if k!='m_NameIndex'},
                                    'name':names[field['m_NameIndex']]} for field in cb[fields]]
                            digest=hashlib.sha256(json.dumps(layout,sort_keys=True).encode()).digest()
                            cb_name=('V' if stage=='vert' else 'P')+'Globals'+str(int.from_bytes(digest[:4],'little'))
                            if cb_name in buffer_layouts and buffer_layouts[cb_name]!=digest.hex():
                                raise ValueError('Constant-buffer layout name collision')
                            buffer_layouts[cb_name]=digest.hex()
                            cb['m_NameIndex']=add_name(shader_pass,cb_name);combined['m_ConstantBuffers'].append(cb)
                            binding=copy.deepcopy(register_binding);binding['m_NameIndex']=cb['m_NameIndex'];binding['m_Index']=packed(stage,*resources['cb',register]);combined['m_ConstantBufferBindings'].append(binding)
                            group=dict(name=cb_name,size=cb['m_Size'],parameters=[])
                            for field in cb['m_VectorParams']:
                                group['parameters'].append(dict(name=names[field['m_NameIndex']],values=[field['m_Type'],1,field['m_Dim'],0,field['m_ArraySize'],field['m_Index']]))
                            for field in cb['m_MatrixParams']:
                                group['parameters'].append(dict(name=names[field['m_NameIndex']],values=[field['m_Type'],field['m_RowCount'],4,1,field['m_ArraySize'],field['m_Index']]))
                            all_groups.append(group);all_bindings.append(dict(name=cb_name,kind=1,values=[binding['m_Index'],0]))
                        for texture in params['m_TextureParams']:
                            register=texture['m_Index']
                            if ('t',register) not in resources:continue
                            admitted.add(('t',register))
                            if texture['m_Dim']!=2:raise ValueError('Only 2D sampled textures admitted')
                            tex=copy.deepcopy(texture);tex['m_Index']=packed(stage,*resources['t',register]);sampler=texture['m_SamplerIndex']
                            tex['m_SamplerIndex']=packed(stage,*resources['s',sampler]) if ('s',sampler) in resources else -1
                            if ('s',sampler) in resources:admitted.add(('s',sampler))
                            combined['m_TextureParams'].append(tex)
                            all_bindings.append(dict(name=names[texture['m_NameIndex']],kind=0,values=[tex['m_Index'],tex['m_SamplerIndex'],4]))
                        if admitted != set(resources):
                            raise ValueError('SPIR-V resources without Unity bindings: '+repr(sorted(set(resources)-admitted)))
                        input_locations={}
                        if stage=='vert':
                            sig=signatures((args.extracted/source['path']).read_bytes())
                            for location,entry in enumerate(reflect.get('inputs',[])):
                                register=entry['location'];source_channel=sig[register]['source']
                                if source_channel is None:raise ValueError('Unknown vertex input semantic')
                                input_locations[entry['name']]=location;channels.append([source_channel,13+location])
                        module,mapping=decorate(optimized.read_bytes(),resources,input_locations)
                        if stage=='vert' and args.clip_y=='unity':module=flip_position_y(module)
                        optimized.write_bytes(module)
                        run(['spirv-val','--target-env','vulkan1.0',optimized]);stage_resources[stage]=mapping
                        (pairdir/f'{stage}-reflection.json').write_text(run([args.spirv_cross,optimized,'--reflect']))
                        smol=pairdir/f'{stage}.smolv';run([args.smolv,'encode',optimized,smol])
                        decoded=pairdir/f'{stage}-roundtrip.spv';run([args.smolv,'decode',smol,decoded])
                        if decoded.read_bytes()!=module:raise ValueError('SMOL-V roundtrip mismatch')
                        all_modules[stage]=smol.read_bytes()
                    # Unity's native six-stage table: slot 0 vertex, slot 1 fragment.
                    code=bytearray(struct.pack('<13I',*([0]*13)))
                    for slot,stage in [(0,'vert'),(1,'frag')]:
                        compressed=all_modules[stage];struct.pack_into('<II',code,4+slot*8,len(code),len(compressed));code+=compressed
                    keyword_sources=[programs[vertex['m_BlobIndex']],programs[fragment['m_BlobIndex']]]
                    record=dict(header=[201806140,25,0,0,0,0],keywords=sorted({k for s in keyword_sources for k in s['keywords']}),
                        local_keywords=sorted({k for s in keyword_sources for k in s['local_keywords']}),code=bytes(code),
                        source_map=sum(1<<source for source,_ in channels),channels=channels,groups=all_groups,bindings=all_bindings)
                    vertex_records.append(write(record))
                    dummy=dict(header=[201806140,25,0,0,0,0],keywords=record['keywords'],local_keywords=record['local_keywords'],code=b'',source_map=0,channels=[],groups=[dict(name='',size=0,parameters=[])],bindings=[])
                    fragment_records.append(write(dummy))
                    v=copy.deepcopy(vertex);f=copy.deepcopy(fragment)
                    for target in [v,f]:target['m_GpuProgramType']=25
                    v['m_Channels']={'m_SourceMap':record['source_map'],'m_Channels':[dict(source=s,target=t) for s,t in channels]}
                    v['m_Parameters']=combined;v['m_BlobIndex']=pair_index
                    f['m_Parameters']=empty();f['m_BlobIndex']=pair_index
                    new_vertices.append(v);new_fragments.append(f)
                    work_items.append(dict(pair=pair_index,path=str(pairdir.relative_to(args.output)),resources=stage_resources,channels=channels))
                vertices['m_SubPrograms']=new_vertices;fragments['m_SubPrograms']=new_fragments
                vertices['m_CommonParameters']=empty();fragments['m_CommonParameters']=empty()
        # Fragment records follow all vertex records, just as in the native fixture.
        for subshader in tree['m_ParsedForm']['m_SubShaders']:
            for shader_pass in subshader['m_Passes']:
                for f in shader_pass['progFragment']['m_SubPrograms']:f['m_BlobIndex']+=len(vertex_records)
        blob=write_table(vertex_records+fragment_records);compressed=CompressionHelper.compress_lz4(blob)
        tree.update(platforms=[18],offsets=[[0]],compressedLengths=[[len(compressed)]],decompressedLengths=[[len(blob)]],compressedBlob=list(compressed))
        obj.save_typetree(tree)
        report['converted'].append(dict(shader=name,pairs=work_items,records=len(vertex_records)+len(fragment_records)))
    for file in env.files.values():
        for serialized in file.files.values():
            if hasattr(serialized,'target_platform'):
                serialized.target_platform=13
                serialized._m_target_platform=13
    bundle=args.output/'modded_shaders.vulkan.experimental'
    file=next(iter(env.files.values()));bundle.write_bytes(file.save(packer='lz4'))
    report['output_sha256']=hashlib.sha256(bundle.read_bytes()).hexdigest()
    report['constant_buffer_layouts']=buffer_layouts
    (args.output/'conversion.json').write_text(json.dumps(report,indent=2)+'\n')
    print('Wrote experimental bundle:',bundle,'shaders',len(report['converted']))


if __name__=='__main__':main()
