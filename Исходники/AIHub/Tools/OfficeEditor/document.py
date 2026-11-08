"""UNO owns layout, input, selection, undo, and the loss-preserving document model."""
import io
import json
import os
from pathlib import Path
import time
import sys
import ctypes
import zipfile
from datetime import datetime, timezone
from pathlib import PureWindowsPath

import uno
import unohelper
from com.sun.star.beans import PropertyValue
from com.sun.star.util import XModifyListener

from editing import Editing
from native_commands import NativeCommands
from windows_spelling import install as install_spelling


def properties(**values):
    return tuple(PropertyValue(name, 0, value, 0) for name, value in values.items())


class Changes(unohelper.Base, XModifyListener):
    def __init__(self, owner):
        self.owner = owner

    def modified(self, event):
        self.owner.revision += 1
        self.owner.changed = time.monotonic()

    def disposing(self, event):
        pass


class EditorDocument(Editing):
    def __init__(self, context, desktop, workspace):
        self.context, self.desktop, self.workspace = context, desktop, workspace
        self.manager = context.ServiceManager
        self.doc = self.frame = self.peer = None
        self.revision = self.saved_revision = 0
        self.changed = 0
        self.draft_error = ""
        self.provenance = ""
        self.capture = None
        self.listener = Changes(self)
        self.native_commands = NativeCommands()
        self.spelling = install_spelling(context)

    def open(self, request):
        def stage(name):
            print("editor: " + name, file=sys.stderr, flush=True)
        stage("load document")
        source = request.get("source")
        url = Path(source).resolve().as_uri() if source else "private:factory/swriter"
        self.doc = self.desktop.loadComponentFromURL(url, "_blank", 0, properties(
            Hidden=False, ReadOnly=False, MacroExecutionMode=0, UpdateDocMode=0,
            AsTemplate=False))
        if self.doc is None or not self.doc.supportsService("com.sun.star.text.TextDocument"):
            raise RuntimeError("OfficeEditor.TextOnly")
        self.frame = self.doc.CurrentController.Frame
        self.frame.LayoutManager.setVisible(False)
        self.dismiss_macro_notice()
        self.peer = self.frame.ContainerWindow
        handle = self.peer.getWindowHandle((), 1)
        user = ctypes.windll.user32
        user.SetParent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        user.GetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int]
        user.SetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_long]
        style = user.GetWindowLongW(handle, -16)
        user.SetWindowLongW(handle, -16, (style & ~0x80CF0000) | 0x40000000 | 0x10000000)
        user.SetParent(handle, request["hwnd"])
        self.peer.setPosSize(0, 0, request["width"], request["height"], 15)
        self.peer.setVisible(True)
        stage("view settings")
        view = self.doc.CurrentController.ViewSettings
        for key, value in {"ShowRulers": True, "ShowHoriRuler": True, "ShowVertRuler": True,
                           "ZoomValue": 100, "ShowOnlineLayout": False, "ShowNonprintingCharacters": False}.items():
            if view.PropertySetInfo.hasPropertyByName(key):
                view.setPropertyValue(key, value)
        facts = json.loads(request["provenance"])
        props = self.doc.DocumentProperties.UserDefinedProperties
        if props.PropertySetInfo.hasPropertyByName('LOPATA.Provenance'):
            try:
                previous = json.loads(props.getPropertyValue('LOPATA.Provenance'))
                models = previous.get('Models', [])
                if len(models) > 100: raise ValueError('too many model identities')
                for model in models:
                    for key in ('Id', 'Name', 'Revision', 'Role'):
                        value = model[key]
                        if not isinstance(value, str) or len(value) > 1024 or PureWindowsPath(value).is_absolute() or value.startswith('/') or any(ord(c) < 32 for c in value):
                            raise ValueError('invalid public model identity')
                for key in ('CreatedUtc', 'ModelHistory', 'ModelScope', 'Models'):
                    if key in previous: facts[key] = previous[key]
            except (ValueError, KeyError, TypeError):
                facts['ModelHistory'] = 'unknown'
        elif source:
            facts['ModelHistory'] = 'unknown'
        self.provenance = json.dumps(facts, ensure_ascii=False)
        self.doc.addModifyListener(self.listener)
        self.frame.registerDispatchProviderInterceptor(self.native_commands)
        stage("spelling")
        if not source:
            self.doc.CurrentController.ViewCursor.CharFontName = 'Arial'
            self.doc.CurrentController.ViewCursor.CharHeight = 11
        self.command(".uno:SpellOnline", {"SpellOnline": {"type": "boolean", "value": True}})
        self.command(".uno:LanguageStatus", {"Language": {"type": "string", "value": "Default_Russian" if request.get("ru", True) else "Default_English (USA)"}})
        self.revision = 1
        stage("first draft")
        self.autosave(force=True)
        stage("ready")
        return {"hwnd": self.peer.getWindowHandle((), 1), "provenance": self.provenance, **self.state()}

    def state(self):
        if self.doc is None:
            return {}
        self.dismiss_macro_notice()
        cursor = self.doc.CurrentController.ViewCursor
        return {"revision": self.revision, "savedRevision": self.saved_revision,
                "draftError": self.draft_error, "page": cursor.getPage(),
                "pages": self.doc.CurrentController.PageCount,
                "selected": bool(cursor.String), "capture": bool(self.capture),
                "spellingLanguages": self.spelling[3],
                "action": self.native_commands.pop()}

    def dismiss_macro_notice(self):
        # MacroExecutionMode remains NEVER_EXECUTE. Only the embedded frame's
        # redundant banner is dismissed; it overlaps Writer's ruler when its
        # normal surrounding office chrome is absent.
        controller = self.doc.CurrentController
        if controller.hasInfobar('macro'):
            controller.removeInfobar('macro')

    def stamp(self):
        props = self.doc.DocumentProperties.UserDefinedProperties
        if props.PropertySetInfo.hasPropertyByName("LOPATA.Provenance"):
            if props.getPropertyValue("LOPATA.Provenance") != self.provenance:
                props.setPropertyValue("LOPATA.Provenance", self.provenance)
        else:
            props.addProperty("LOPATA.Provenance", 0, self.provenance)

    def store(self, path, kind):
        facts = json.loads(self.provenance)
        facts['ExportedUtc'] = datetime.now(timezone.utc).isoformat()
        self.provenance = json.dumps(facts, ensure_ascii=False)
        self.stamp()
        filters = {"odt": "writer8", "docx": "Office Open XML Text", "rtf": "Rich Text Format",
                   "pdf": "writer_pdf_Export", "txt": "Text (encoded)"}
        if kind not in filters:
            raise RuntimeError("OfficeEditor.Unsupported")
        metadata = self.doc.DocumentProperties
        subject = metadata.Subject
        try:
            if kind == 'pdf':
                metadata.Subject = (subject + '\nLOPATA.Provenance=' + self.provenance).strip()
            self.doc.storeToURL(Path(path).resolve().as_uri(), properties(
                FilterName=filters[kind], Overwrite=True, FilterOptions="UTF8,LF" if kind == "txt" else ""))
        finally:
            if kind == 'pdf':
                metadata.Subject = subject

    def autosave(self, force=False):
        if self.doc is None or (not force and (self.saved_revision == self.revision or time.monotonic() - self.changed < .45)):
            return
        try:
            target = self.workspace / "document.odt"
            temporary = self.workspace / "document.next.odt"
            self.store(temporary, "odt")
            revision = self.revision
            with zipfile.ZipFile(temporary) as archive:
                if "content.xml" not in archive.namelist():
                    raise RuntimeError("OfficeEditor.DraftFailed")
            if target.exists():
                os.replace(target, self.workspace / "previous.odt")
            os.replace(temporary, target)
            self.saved_revision = revision
            self.draft_error = ""
        except Exception as error:
            self.draft_error = str(error)
            if force:
                raise

    def execute(self, request):
        action = request["action"]
        if action == "open":
            return self.open(request)
        if self.doc is None:
            raise RuntimeError("OfficeEditor.EngineUnavailable")
        if action == "state":
            return self.state()
        if action == "spellingProbe":
            from com.sun.star.lang import Locale
            language, country = request["language"].split('-')
            locale = Locale(language, country, '')
            checker = self.spelling[2].getSpellChecker()
            # This dispatcher exposes deprecated XSpellChecker1 as well; PyUNO
            # introspection chooses its short-language overload by method name.
            language_id = 1049 if language == 'ru' else 1033
            valid = checker.isValid(request['word'], language_id, ())
            result = checker.spell(request['word'], language_id, ())
            return {"valid": valid,
                    "suggestions": list(result.getAlternatives()) if result else [],
                    "services": list(self.spelling[2].getConfiguredServices('com.sun.star.linguistic2.SpellChecker', locale))}
        if action == "save":
            self.autosave(True)
            return self.state()
        if action == "export":
            self.store(request["path"], request["kind"])
            return True
        if action == "resize":
            self.peer.setPosSize(0, 0, request["width"], request["height"], 12)
            return True
        if action == "focus":
            self.frame.activate()
            self.doc.CurrentController.Frame.ComponentWindow.setFocus()
            return True
        return self.edit(request)

    def close(self):
        if self.doc is not None:
            self.autosave(True)
            self.doc.removeModifyListener(self.listener)
            self.doc.setModified(False)
            self.doc.close(True)
            self.doc = None
        self.spelling[1].dispose()
        self.manager.remove(self.spelling[0])
