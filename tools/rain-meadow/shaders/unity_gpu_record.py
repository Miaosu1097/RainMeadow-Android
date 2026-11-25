"""Bounded reader/writer for the observed 201806140 Unity GPU records.

Records with structures in parameter groups are deliberately rejected. They do
not occur in the supplied SBCameraScroll shaders or the native Vulkan fixture.
"""
import struct


class Reader:
    def __init__(self, data): self.data, self.pos = data, 0
    def integer(self):
        value = struct.unpack_from('<i', self.data, self.pos)[0]; self.pos += 4; return value
    def blob(self):
        size = self.integer()
        if size < 0 or self.pos + size > len(self.data): raise ValueError('Invalid length')
        value = self.data[self.pos:self.pos+size]; self.pos += size; self.pos = (self.pos+3)&~3; return value
    def string(self): return self.blob().decode('utf8')
    def count(self):
        value = self.integer()
        if not 0 <= value <= 100000: raise ValueError('Invalid count')
        return value


class Writer:
    def __init__(self): self.data = bytearray()
    def integer(self, value): self.data += struct.pack('<I', value & 0xffffffff)
    def blob(self, value):
        self.integer(len(value)); self.data += value
        self.data += bytes((-len(self.data)) % 4)
    def string(self, value): self.blob(value.encode('utf8'))


def read(data):
    r = Reader(data)
    header = [r.integer() for _ in range(6)]
    if header[0] != 201806140: raise ValueError('Unsupported program version')
    result = dict(header=header, keywords=[r.string() for _ in range(r.count())],
                  local_keywords=[r.string() for _ in range(r.count())], code=r.blob())
    result['source_map'] = r.integer()
    result['channels'] = [[r.integer(), r.integer()] for _ in range(r.count())]
    result['groups'] = []
    for _ in range(r.count()):
        group = dict(name=r.string(), size=r.integer(), parameters=[])
        for _ in range(r.count()):
            group['parameters'].append(dict(name=r.string(), values=[r.integer() for _ in range(6)]))
        structures = r.count()
        if structures: raise ValueError('Structured parameters require a separate implementation')
        result['groups'].append(group)
    result['bindings'] = []
    for _ in range(r.count()):
        name, kind = r.string(), r.integer()
        if kind not in [0, 1]: raise ValueError('Unsupported binding kind')
        result['bindings'].append(dict(name=name, kind=kind, values=[r.integer() for _ in range(3 if kind == 0 else 2)]))
    if r.pos != len(data): raise ValueError(f'Unconsumed GPU record: {len(data)-r.pos} bytes')
    return result


def write(record):
    w = Writer()
    for value in record['header']: w.integer(value)
    for key in ['keywords', 'local_keywords']:
        w.integer(len(record[key]))
        for value in record[key]: w.string(value)
    w.blob(record['code'])
    w.integer(record['source_map'])
    w.integer(len(record['channels']))
    for pair in record['channels']:
        for value in pair: w.integer(value)
    w.integer(len(record['groups']))
    for group in record['groups']:
        w.string(group['name']); w.integer(group['size']); w.integer(len(group['parameters']))
        for param in group['parameters']:
            w.string(param['name'])
            for value in param['values']: w.integer(value)
        w.integer(0)
    w.integer(len(record['bindings']))
    for binding in record['bindings']:
        w.string(binding['name']); w.integer(binding['kind'])
        for value in binding['values']: w.integer(value)
    return bytes(w.data)


def read_table(raw):
    count = struct.unpack_from('<I', raw)[0]
    if 4 + 12 * count > len(raw): raise ValueError('Invalid table')
    records = []
    for index in range(count):
        offset, size, segment = struct.unpack_from('<III', raw, 4+index*12)
        if segment != 0 or offset < 4+12*count or offset+size > len(raw): raise ValueError('Invalid table entry')
        records.append(raw[offset:offset+size])
    return records


def write_table(records):
    header = bytearray(struct.pack('<I', len(records)))
    offset = 4 + 12 * len(records)
    for record in records:
        header += struct.pack('<III', offset, len(record), 0); offset += len(record)
    return bytes(header) + b''.join(records)
