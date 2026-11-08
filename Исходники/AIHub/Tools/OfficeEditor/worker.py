"""Private UNO text-document host. Only the fixed command protocol is executable.

LOK on our Windows build paints and accepts keys but cannot dispatch Paste/UNO
commands (OfficeEditor_20261108/probe.py). UNO is the documented reserve path.
The parent HWND belongs to our WPF host; no user office profile is opened.
"""
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

import uno
from com.sun.star.beans import PropertyValue

from document import EditorDocument


def main():
    program, workspace = (Path(p).resolve() for p in sys.argv[1:3])
    workspace.mkdir(parents=True, exist_ok=True)
    profile = workspace / "profile"
    # This isolated engine is versioned and updated by LOPATA's component system.
    # Never let the interactive LibreOffice updater replace the pinned binaries.
    user_profile = profile / "user"
    user_profile.mkdir(parents=True, exist_ok=True)
    (user_profile / "registrymodifications.xcu").write_text('''<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema">
<item oor:path="/org.openoffice.Setup/Office"><prop oor:name="ooSetupInstCompleted" oor:op="fuse" oor:type="xs:boolean"><value>true</value></prop><prop oor:name="MigrationCompleted" oor:op="fuse" oor:type="xs:boolean"><value>true</value></prop></item>
<item oor:path="/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job['UpdateCheck']/Arguments"><prop oor:name="AutoCheckEnabled" oor:op="fuse" oor:type="xs:boolean"><value>false</value></prop><prop oor:name="AutoDownloadEnabled" oor:op="fuse" oor:type="xs:boolean"><value>false</value></prop></item>
<item oor:path="/org.openoffice.Office.Update/Update"><prop oor:name="Enabled" oor:op="fuse" oor:type="xs:boolean"><value>false</value></prop></item>
</oor:items>''', encoding="utf-8")
    pipe = "lopata_editor_" + uuid.uuid4().hex
    # This is an interactive native view: --headless accepts input/UNO edits but
    # suppresses painting on Windows, leaving an apparently empty white surface.
    proc = subprocess.Popen([
        str(program / "soffice.exe"), "-env:UserInstallation=" + profile.as_uri(),
        "--nologo", "--nodefault", "--norestore", "--nofirststartwizard",
        "--accept=pipe,name=" + pipe + ";urp;StarOffice.ComponentContext"],
        cwd=program, stdout=subprocess.DEVNULL, stderr=sys.stderr,
        creationflags=0x08000000)
    desktop = None
    document = None
    try:
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
        deadline = time.monotonic() + 60
        while True:
            try:
                context = resolver.resolve("uno:pipe,name=" + pipe + ";urp;StarOffice.ComponentContext")
                break
            except Exception:
                if time.monotonic() >= deadline:
                    print("editor: launcher exit=" + str(proc.poll()), file=sys.stderr, flush=True)
                    raise RuntimeError("OfficeEditor.EngineUnavailable")
                time.sleep(.1)
        desktop = context.ServiceManager.createInstanceWithContext("com.sun.star.frame.Desktop", context)
        document = EditorDocument(context, desktop, workspace)
        print(json.dumps({"ready": True, "pid": proc.pid, "engine": "LibreOffice UNO"}), flush=True)
        initial = json.loads(sys.stdin.readline())
        result = document.execute(initial)
        print(json.dumps({"ok": True, "result": result}, ensure_ascii=False), flush=True)
        requests = queue.Queue()

        def read():
            for line in sys.stdin:
                requests.put(line)
            requests.put(None)
        threading.Thread(target=read, daemon=True).start()
        while True:
            try:
                line = requests.get(timeout=.3)
            except queue.Empty:
                document.autosave()
                continue
            if line is None:
                break
            request = json.loads(line)
            try:
                result = document.execute(request)
                document.autosave()
                print(json.dumps({"ok": True, "result": result}, ensure_ascii=False), flush=True)
            except Exception as error:
                import traceback
                traceback.print_exc(file=sys.stderr)
                print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False), flush=True)
    finally:
        if document is not None:
            try:
                document.close()
            except Exception as error:
                print(str(error), file=sys.stderr)
        if desktop is not None:
            try:
                desktop.terminate()
            except Exception:
                pass
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.terminate()


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        main()
    except Exception as error:
        print(json.dumps({"ok": False, "error": str(error)}), flush=True)
        sys.exit(1)
