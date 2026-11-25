#!/usr/bin/env python3
"""Link compiled GLES AssetBundle records with Android EGL in a pbuffer."""

import argparse
import ctypes as C
import hashlib
import json
from pathlib import Path
import re

import UnityPy

from build_gles_bundle import GLES_PLATFORM, ROOT, get_records

EGL_PATH = "/system/lib64/libEGL.so"
GLES_PATH = "/system/lib64/libGLESv2.so"
INT, UINT, PTR = C.c_int, C.c_uint, C.c_void_p
P_INT, P_PTR = C.POINTER(INT), C.POINTER(PTR)


def bind(library, name, result, *args):
    function = getattr(library, name)
    function.restype = result
    function.argtypes = args
    return function


def source_stages(record):
    segments = re.fullmatch(rb"#ifdef VERTEX\n(.*?)#endif\n#ifdef FRAGMENT\n(.*?)#endif\n",
                            record["code"], flags=re.DOTALL)
    if not segments:
        raise ValueError("Unexpected GLES record shader stage wrapper")
    return segments.groups()


def metadata_uniforms(record):
    names = set()
    for group in record["groups"]:
        for parameter in group["parameters"]:
            name = parameter["name"]
            matrix = parameter["values"][3]
            names.add("hlslcc_mtx4x4" + name if matrix else name)
    names.update(binding["name"] for binding in record["bindings"])
    return names


class Context:
    def __init__(self, egl_path, gles_path):
        self.egl = C.CDLL(egl_path)
        self.gl = C.CDLL(gles_path)
        self.display = self.surface = self.context = None

    def check(self, value, location):
        if not value:
            err = bind(self.egl, "eglGetError", UINT)()
            raise RuntimeError(f"{location} returned EGL error 0x{err:x}")
        return value

    def open(self):
        egl = self.egl
        self.display = self.check(bind(egl, "eglGetDisplay", PTR, PTR)(None), "eglGetDisplay")
        major, minor = INT(), INT()
        init = bind(egl, "eglInitialize", UINT, PTR, P_INT, P_INT)
        self.check(init(self.display, C.byref(major), C.byref(minor)), "eglInitialize")
        self.check(bind(egl, "eglBindAPI", UINT, UINT)(0x30A0), "eglBindAPI")
        config, count = PTR(), INT()
        select = bind(egl, "eglChooseConfig", UINT, PTR, P_INT, P_PTR, INT, P_INT)
        settings = (INT * 13)(0x3033, 1, 0x3040, 0x40, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3038)
        self.check(select(self.display, settings, C.byref(config), 1, C.byref(count)) and count.value, "eglChooseConfig")
        create_surface = bind(egl, "eglCreatePbufferSurface", PTR, PTR, PTR, P_INT)
        surface_spec = (INT * 5)(0x3057, 1, 0x3056, 1, 0x3038)
        self.surface = self.check(create_surface(self.display, config, surface_spec), "eglCreatePbufferSurface")
        create_context = bind(egl, "eglCreateContext", PTR, PTR, PTR, PTR, P_INT)
        context_spec = (INT * 3)(0x3098, 3, 0x3038)
        self.context = self.check(create_context(self.display, config, None, context_spec), "eglCreateContext")
        self.make_current = bind(egl, "eglMakeCurrent", UINT, PTR, PTR, PTR, PTR)
        self.check(self.make_current(self.display, self.surface, self.surface, self.context), "eglMakeCurrent")
        get_string = bind(self.gl, "glGetString", C.c_char_p, UINT)
        return dict(egl=f"{major.value}.{minor.value}", vendor=get_string(0x1F00).decode(),
                    renderer=get_string(0x1F01).decode(), gl_version=get_string(0x1F02).decode())

    def close(self):
        e = self.egl
        if self.context:
            self.make_current(self.display, None, None, None)
            bind(e, "eglDestroyContext", UINT, PTR, PTR)(self.display, self.context)
        if self.surface:
            bind(e, "eglDestroySurface", UINT, PTR, PTR)(self.display, self.surface)
        if self.display:
            bind(e, "eglTerminate", UINT, PTR)(self.display)

    def check_compilation(self, program, stage, status_kind):
        get = bind(self.gl, f"glGet{stage}iv", None, UINT, UINT, P_INT)
        status, size = INT(), INT()
        get(program, status_kind, C.byref(status))
        get(program, 0x8B84, C.byref(size))
        buffer = C.create_string_buffer(max(size.value, 1))
        log = bind(self.gl, f"glGet{stage}InfoLog", None, UINT, INT, P_INT, C.c_char_p)
        log(program, len(buffer), None, buffer)
        if not status.value:
            raise ValueError(f"GLES {stage} compile or link failed: {buffer.value.decode(errors='replace')}")

    def active_names(self, program, name, count_code, size_code):
        gl = self.gl
        get = bind(gl, "glGetProgramiv", None, UINT, UINT, P_INT)
        read = bind(gl, f"glGetActive{name}", None, UINT, UINT, INT, P_INT, P_INT, C.POINTER(UINT), C.c_char_p)
        count, width = INT(), INT()
        get(program, count_code, C.byref(count))
        get(program, size_code, C.byref(width))
        result = set()
        for i in range(count.value):
            buffer = C.create_string_buffer(max(width.value, 1))
            read(program, i, len(buffer), C.byref(INT()), C.byref(INT()), C.byref(UINT()), buffer)
            result.add(buffer.value.decode().removesuffix("[0]"))
        return result

    def probe(self, record):
        gl = self.gl
        create_shader = bind(gl, "glCreateShader", UINT, UINT)
        set_source = bind(gl, "glShaderSource", None, UINT, INT, C.POINTER(C.c_char_p), P_INT)
        compile_shader = bind(gl, "glCompileShader", None, UINT)
        delete_shader = bind(gl, "glDeleteShader", None, UINT)
        attach = bind(gl, "glAttachShader", None, UINT, UINT)
        program = bind(gl, "glCreateProgram", UINT)()
        handles = []
        try:
            for kind, source in zip((0x8B31, 0x8B30), source_stages(record)):
                shader = create_shader(kind)
                handles.append(shader)
                set_source(shader, 1, (C.c_char_p * 1)(source), None)
                compile_shader(shader)
                self.check_compilation(shader, "Shader", 0x8B81)
                attach(program, shader)
            bind(gl, "glLinkProgram", None, UINT)(program)
            self.check_compilation(program, "Program", 0x8B82)
            uniforms = self.active_names(program, "Uniform", 0x8B86, 0x8B87)
            unknown = uniforms - metadata_uniforms(record)
            if unknown:
                raise ValueError(f"Linked uniform has no Unity metadata: {sorted(unknown)}")
            attributes = self.active_names(program, "Attrib", 0x8B89, 0x8B8A)
            attribute_bits = {"in_POSITION0": 0, "in_NORMAL0": 1, "in_TANGENT0": 2,
                              "in_COLOR0": 3, **{f"in_TEXCOORD{x}": x + 4 for x in range(8)}}
            for attr in attributes:
                if not record["source_map"] & (1 << attribute_bits[attr]):
                    raise ValueError(f"Missing source channel for linked attribute {attr}")
            return dict(active_uniforms=sorted(uniforms), active_attributes=sorted(attributes))
        finally:
            bind(gl, "glDeleteProgram", None, UINT)(program)
            for handle in handles:
                delete_shader(handle)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("--egl", default=EGL_PATH)
    parser.add_argument("--gles", default=GLES_PATH)
    args = parser.parse_args()
    if not args.report.resolve().is_relative_to(ROOT / "work"):
        parser.error("Report must be in the workspace")
    context = Context(args.egl, args.gles)
    report = dict(bundle_sha256=hashlib.sha256(args.bundle.read_bytes()).hexdigest(),
                  engine_executed=False, draw_submitted=False, links=[], failures=[])
    try:
        report["device"] = context.open()
        results = {}
        for obj in UnityPy.load(str(args.bundle)).objects:
            if obj.type.name != "Shader":
                continue
            tree = obj.read_typetree()
            name = tree["m_ParsedForm"]["m_Name"]
            for i, record in enumerate(get_records(tree, GLES_PLATFORM)):
                digest = hashlib.sha256(record["code"]).hexdigest()
                try:
                    if digest not in results:
                        results[digest] = context.probe(record)
                    report["links"].append(dict(shader=name, variant=i, sha256=digest, **results[digest]))
                except (ValueError, KeyError) as exc:
                    report["failures"].append(dict(shader=name, variant=i, sha256=digest, error=str(exc)))
        report["unique_linked_programs"] = len(results)
    finally:
        context.close()
        with args.report.open("x") as file:
            json.dump(report, file, indent=2)
            file.write("\n")
    print(f"{len(report['links'])} GLES variants linked on {report['device']['renderer']}. "
          f"Unique programs: {report['unique_linked_programs']}. Failures: {len(report['failures'])}.")
    return 1 if report["failures"] or not report["links"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
