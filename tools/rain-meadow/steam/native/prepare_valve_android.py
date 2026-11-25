"""Prepare the verified Valve Android ARM64 library for the local SDR probe."""

import argparse
import hashlib
from pathlib import Path
from struct import Struct

VALVE_BUILD_SHA256 = "e85d8e5029c24167f0564a214336d06e0707912c302a19fdf6225e629dcafac7"
ELF_HEADER = Struct("<16sHHIQQQIHHHHHH")
SECTION_HEADER = Struct("<IIQQQQIIQQ")
DYNAMIC_ENTRY = Struct("<qQ")
SHT_DYNAMIC = 6
DT_NULL = 0
DT_NEEDED = 1


def remove_unused_android_dependency(data):
    header = ELF_HEADER.unpack_from(data)
    if header[0][:6] != b"\x7fELF\x02\x01" or header[2] != 183:
        raise ValueError("Expected a little-endian AArch64 ELF")
    section_offset, section_size, section_count = header[6], header[11], header[12]
    if section_size != SECTION_HEADER.size:
        raise ValueError("Unrecognized ELF section header size")
    sections = [
        SECTION_HEADER.unpack_from(data, section_offset + i * section_size)
        for i in range(section_count)
    ]
    dynamic_section, = (section for section in sections if section[1] == SHT_DYNAMIC)
    _, _, _, _, start, size, strings_index, _, _, entry_size = dynamic_section
    if entry_size != DYNAMIC_ENTRY.size:
        raise ValueError("Unrecognized ELF dynamic entry size")
    strings_offset, strings_size = sections[strings_index][4:6]
    strings = data[strings_offset:strings_offset + strings_size]
    dependencies = []
    for offset in range(start, start + size, entry_size):
        tag, value = DYNAMIC_ENTRY.unpack_from(data, offset)
        if tag == DT_NULL:
            terminator = offset
            break
        if tag == DT_NEEDED:
            end = strings.index(0, value)
            if strings[value:end] == b"libandroid.so":
                dependencies.append(offset)
    else:
        raise ValueError("ELF dynamic table is missing its terminator")
    if len(dependencies) != 1:
        raise ValueError("Expected one libandroid.so dependency")

    # Shift only the dynamic table and retain its original size and terminator.
    dependency, = dependencies
    data[dependency:terminator] = data[dependency + entry_size:terminator + entry_size]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        parser.error("Write the probe library to a separate file")

    data = bytearray(args.source.read_bytes())
    if hashlib.sha256(data).hexdigest() != VALVE_BUILD_SHA256:
        parser.error("The input does not match the verified Valve Android build")
    remove_unused_android_dependency(data)
    args.output.write_bytes(data)


if __name__ == "__main__":
    main()
