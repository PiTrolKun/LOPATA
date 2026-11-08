"""Read-only office preview through the stable LibreOfficeKit C ABI.

Run with the Python shipped in the verified LibreOffice component. The caller
owns a private source copy, profile and PDF; no original or user profile is saved.
"""
import ctypes as C
import json
import os
import sys
from contextlib import contextmanager
from pathlib import Path


def method(obj, index, result, *arguments):
    table = C.cast(obj, C.POINTER(C.c_void_p))[0]
    size = C.cast(table, C.POINTER(C.c_size_t))[0]
    if size < (index + 1) * C.sizeof(C.c_void_p):
        raise RuntimeError("LibreOfficeKit ABI is not supported")
    address = C.cast(table, C.POINTER(C.c_void_p))[index]
    if not address:
        raise RuntimeError("LibreOfficeKit function is unavailable")
    return C.CFUNCTYPE(result, C.c_void_p, *arguments)(address)


@contextmanager
def office(program, profile):
    def stage(value):
        print(json.dumps({"stage": value}), file=sys.stderr, flush=True)
    (profile / "user").mkdir(parents=True, exist_ok=True)
    (profile / "user" / "registrymodifications.xcu").write_text(
        '<?xml version="1.0"?><oor:items xmlns:oor="http://openoffice.org/2001/registry">'
        '<item oor:path="/org.openoffice.Office.Common/Security/Scripting">'
        '<prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>'
        '<prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>'
        '</item></oor:items>', encoding="utf-8")
    os.environ["SAL_USE_VCLPLUGIN"] = "svp"
    os.chdir(program)
    with os.add_dll_directory(str(program)):
        library = C.CDLL(str(program / "mergedlo.dll"))
        hook = library.libreofficekit_hook_2
        hook.restype = C.c_void_p
        hook.argtypes = [C.c_char_p, C.c_char_p]
        stage("initialize")
        kit = hook(str(program).encode("utf-8"), profile.as_uri().encode("utf-8"))
        if not kit:
            raise RuntimeError("LibreOfficeKit initialization failed")
        try:
            yield kit
        finally:
            stage("close")
            method(kit, 1, None)(kit)


def export_document(kit, source, target):
    # LOK's default is NEVER_EXECUTE; make this explicit as well.
    doc = method(kit, 4, C.c_void_p, C.c_char_p, C.c_char_p)(
        kit, source.as_uri().encode("utf-8"),
        b"Language=en-US,EnableMacrosExecution=false,MacroSecurityLevel=3,Batch=true")
    if not doc:
        raise RuntimeError("LibreOffice could not open this document")
    try:
        if not method(doc, 2, C.c_int, C.c_char_p, C.c_char_p, C.c_char_p)(
                doc, target.as_uri().encode("utf-8"), b"pdf", b""):
            raise RuntimeError("LibreOffice could not render this document")
    finally:
        method(doc, 1, None)(doc)
    if not target.is_file() or target.stat().st_size == 0:
        raise RuntimeError("Empty preview")


if __name__ == "__main__":
    try:
        if sys.argv[1] == "--server":
            sys.stdin.reconfigure(encoding="utf-8")
            sys.stdout.reconfigure(encoding="utf-8")
            with office(*(Path(value).resolve() for value in sys.argv[2:4])) as kit:
                print(json.dumps({"ready": True}), flush=True)
                for line in sys.stdin:
                    request = json.loads(line)
                    export_document(kit, Path(request["source"]).resolve(), Path(request["target"]).resolve())
                    print(json.dumps({"ok": True}), flush=True)
        else:
            program, source, target = (Path(value).resolve() for value in sys.argv[1:4])
            with office(program, target.parent / "office-profile") as kit:
                export_document(kit, source, target)
            print(json.dumps({"ok": True, "engine": "LibreOfficeKit"}), flush=True)
    except Exception as error:
        print(json.dumps({"ok": False, "error": str(error)}), flush=True)
        sys.exit(1)
