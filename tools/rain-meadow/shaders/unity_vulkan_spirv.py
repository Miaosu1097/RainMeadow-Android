"""Inspect Vulkan uniform access and apply Unity's observed clip-space convention."""
import struct


MARKER = 'UnityVulkan_PositionYFlip_v1'


def instruction(opcode, *operands):
    return [((len(operands) + 1) << 16) | opcode, *operands]


def words_for_string(value):
    data = value.encode() + b'\0'
    data += bytes((-len(data)) % 4)
    return list(struct.unpack('<' + 'I' * (len(data) // 4), data))


def parse(data):
    if len(data) % 4 or len(data) < 20:
        raise ValueError('Invalid SPIR-V size')
    words = list(struct.unpack('<' + 'I' * (len(data) // 4), data))
    if words[0] != 0x07230203:
        raise ValueError('Expected SPIR-V')
    operations = []
    offset = 5
    while offset < len(words):
        size, opcode = words[offset] >> 16, words[offset] & 65535
        if size == 0 or offset + size > len(words):
            raise ValueError('Invalid SPIR-V instruction')
        operations.append((opcode, words[offset + 1:offset + size]))
        offset += size
    return words, operations


def nested_uniform_access_chains(data):
    """Count Uniform pointers formed by indexing another access-chain result.

    These valid chains read the wrong component on the tested Maleoon driver.
    SPIRV-Tools' combine-access-chains pass emits equivalent direct accesses.
    """
    _, operations = parse(data)
    pointer_storage = {args[0]: args[1] for op, args in operations if op == 32}
    chains = {args[1]: args for op, args in operations if op in (65, 66)}
    return sum(pointer_storage.get(args[0]) == 2 and args[2] in chains for args in chains.values())


def flip_position_y(data):
    words, operations = parse(data)
    entries = [args for op, args in operations if op == 15]
    if len(entries) != 1 or entries[0][0] != 0:
        raise ValueError('Expected one vertex entry point')
    entry = entries[0][1]
    for op, args in operations:
        if op == 5 and struct.pack('<' + 'I' * len(args[1:]), *args[1:]).split(b'\0')[0].decode() == MARKER:
            raise ValueError('Clip-space Y correction already applied')
    positions = [args[0] for op, args in operations if op == 71 and args[1:] == [11, 0]]
    if len(positions) != 1:
        raise ValueError('Only an inspected direct BuiltIn Position output is supported')
    position = positions[0]
    variables = {args[1]: args for op, args in operations if op == 59}
    types = {args[0]: (op, args[1:]) for op, args in operations if 19 <= op <= 39}
    variable = variables[position]
    op, pointer = types[variable[0]]
    if op != 32 or pointer[0] != 3 or variable[2] != 3:
        raise ValueError('Position must be an Output variable')
    op, vector = types[pointer[1]]
    if op != 23 or vector[1] != 4 or types[vector[0]] != (22, [32]):
        raise ValueError('Position must be a float32 vec4')
    float_type = vector[0]
    next_id = words[3]

    def allocate():
        nonlocal next_id
        result = next_id
        next_id += 1
        return result

    additions = []
    pointer_type = next((identity for identity, (op, value) in types.items()
                         if op == 32 and value == [3, float_type]), None)
    if pointer_type is None:
        pointer_type = allocate()
        additions += instruction(32, pointer_type, 3, float_type)
    integer_type = next((identity for identity, (op, value) in types.items()
                         if op == 21 and value == [32, 0]), None)
    if integer_type is None:
        integer_type = allocate()
        additions += instruction(21, integer_type, 32, 0)
    one = next((args[1] for op, args in operations
                if op == 43 and args[0] == integer_type and args[2:] == [1]), None)
    if one is None:
        one = allocate()
        additions += instruction(43, integer_type, one, 1)
    body = []
    current_function = None
    marker_id = None
    exits = 0
    added_types = False
    for opcode, args in operations:
        if opcode == 54:
            if not added_types:
                body += additions
                added_types = True
            current_function = args[1]
        if current_function == entry and opcode == 254:
            raise ValueError('Vertex entry point returns a value')
        if current_function == entry and opcode == 253:
            address, value, negative = allocate(), allocate(), allocate()
            if marker_id is None:
                marker_id = negative
            body += instruction(65, pointer_type, address, position, one)
            body += instruction(61, float_type, value, address)
            body += instruction(127, float_type, negative, value)
            body += instruction(62, address, negative)
            exits += 1
        body += instruction(opcode, *args)
        if opcode == 56:
            current_function = None
    if exits == 0 or not added_types:
        raise ValueError('Vertex entry point has no inspected return')
    # Debug names precede annotations/types, and do not affect shader semantics.
    position_in_body = 0
    while position_in_body < len(body):
        opcode = body[position_in_body] & 65535
        if opcode in (71, 72, 73, 74, 75) or 19 <= opcode <= 39 or opcode == 54:
            break
        position_in_body += body[position_in_body] >> 16
    name = instruction(5, marker_id, *words_for_string(MARKER))
    body[position_in_body:position_in_body] = name
    result = words[:5] + body
    result[3] = next_id
    return struct.pack('<' + 'I' * len(result), *result)


def verify_position_y_flip(data):
    _, operations = parse(data)
    entries = [args for op, args in operations if op == 15]
    positions = [args[0] for op, args in operations if op == 71 and args[1:] == [11, 0]]
    markers = [args[0] for op, args in operations if op == 5 and
               struct.pack('<' + 'I' * len(args[1:]), *args[1:]).split(b'\0')[0].decode() == MARKER]
    if len(entries) != 1 or entries[0][0] != 0 or len(positions) != 1 or len(markers) != 1:
        raise ValueError('Missing or ambiguous Unity Vulkan position correction')
    ones = {args[1] for op, args in operations if op == 43 and args[2:] == [1]}
    current_function = None
    exits = 0
    negatives = set()
    for index, (opcode, args) in enumerate(operations):
        if opcode == 54:
            current_function = args[1]
        if current_function == entries[0][1] and opcode == 253:
            previous = operations[index - 4:index]
            if [op for op, _ in previous] != [65, 61, 127, 62]:
                raise ValueError('Vertex return without the inspected Y correction')
            access, load, negate, save = [value for _, value in previous]
            if (len(access) != 4 or access[2] != positions[0] or access[3] not in ones or
                load[2] != access[1] or negate[0] != load[0] or negate[2] != load[1] or
                save != [access[1], negate[1]]):
                raise ValueError('Invalid position-Y correction data flow')
            negatives.add(negate[1])
            exits += 1
        if opcode == 56:
            current_function = None
    if not exits or markers[0] not in negatives:
        raise ValueError('Position correction marker does not identify an executed exit correction')
    return exits
