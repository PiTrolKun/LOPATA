"""Fixed document actions; input text is never interpreted as a UNO command."""
import html
import difflib
from pathlib import Path
import re
import uuid

import uno
from com.sun.star.beans import PropertyValue


class Editing:
    COMMANDS = {".uno:Undo", ".uno:Redo", ".uno:Bold", ".uno:Italic", ".uno:Underline",
                ".uno:Strikeout", ".uno:LeftPara", ".uno:CenterPara", ".uno:RightPara", ".uno:JustifyPara",
                ".uno:DefaultBullet", ".uno:DefaultNumbering", ".uno:IncrementIndent", ".uno:DecrementIndent",
                ".uno:Copy", ".uno:Cut", ".uno:Paste", ".uno:SelectAll", ".uno:Print", ".uno:InsertPagebreak",
                ".uno:InsertAnnotation", ".uno:DeleteAnnotation", ".uno:TrackChanges", ".uno:ShowTrackedChanges",
                ".uno:SpellOnline", ".uno:LanguageStatus", ".uno:ControlCodes"}

    def command(self, name, arguments=None):
        if name not in self.COMMANDS:
            raise RuntimeError("OfficeEditor.Unsupported")
        args = []
        for key, item in (arguments or {}).items():
            value = item["value"]
            if item["type"] in ("short", "long", "boolean", "string"):
                value = uno.Any(item["type"], value)
            args.append(PropertyValue(key, 0, value, 0))
        dispatcher = self.manager.createInstanceWithContext("com.sun.star.frame.DispatchHelper", self.context)
        dispatcher.executeDispatch(self.frame, name, "", 0, tuple(args))

    def text_range(self, first, count):
        if first == 0 and count >= self.doc.CurrentController.PageCount:
            return self.doc.Text.String
        view = self.doc.CurrentController.ViewCursor
        old = self.doc.Text.createTextCursorByRange(view.Start)
        old.gotoRange(view.End, True)
        try:
            if not view.jumpToPage(first + 1):
                raise RuntimeError("Reader.Speech.InvalidRange")
            if first == 0:
                view.gotoStart(False)
            else:
                view.jumpToStartOfPage()
            start = self.doc.Text.createTextCursorByRange(view.Start)
            if view.jumpToPage(first + count + 1):
                view.jumpToStartOfPage()
                end = view.Start
            else:
                end = self.doc.Text.End
            cursor = self.doc.Text.createTextCursorByRange(start)
            cursor.gotoRange(end, True)
            return cursor.String
        finally:
            view.gotoRange(old.Start, False)
            view.gotoRange(old.End, True)

    def capture_text(self, request):
        text = request["text"]
        if not text or text.isspace():
            raise RuntimeError("OfficeEditor.NoSelection")
        previous = self.capture
        bookmarks = self.doc.Bookmarks
        if previous and previous["text"] == text and bookmarks.hasByName(previous["bookmark"]):
            anchor = bookmarks.getByName(previous["bookmark"]).Anchor
            if anchor.String == previous["display"]:
                previous["count"] += 1
                suffix = " {x" + str(previous["count"]) + "}"
                # Append/change only the suffix; the original rich fragment stays intact.
                cursor = self.doc.Text.createTextCursorByRange(anchor.End)
                old_suffix = previous.get("suffix", "")
                if old_suffix:
                    cursor.goLeft(len(old_suffix), True)
                cursor.String = suffix
                full = self.doc.Text.createTextCursorByRange(anchor.Start)
                full.gotoRange(cursor.End, True)
                bookmarks.getByName(previous["bookmark"]).dispose()
                bookmark = self.doc.createInstance("com.sun.star.text.Bookmark")
                bookmark.Name = previous["bookmark"]
                self.doc.Text.insertTextContent(full, bookmark, True)
                previous.update(display=full.String, suffix=suffix)
                self.autosave(True)
                return {"repeat": previous["count"]}
        was_empty = not self.doc.Text.String
        cursor = self.doc.Text.createTextCursor()
        cursor.gotoEnd(False)
        if self.doc.Text.String:
            self.doc.Text.insertControlCharacter(cursor, 0, False)
        start = self.doc.Text.createTextCursorByRange(cursor.End)
        if not was_empty:
            start.goLeft(1, False)
        rich = request.get("html")
        if rich:
            fragment = self.workspace / ("capture-" + uuid.uuid4().hex + ".html")
            try:
                fragment.write_text('<!doctype html><meta charset="utf-8">' + rich, encoding="utf-8")
                cursor.insertDocumentFromURL(fragment.as_uri(), (PropertyValue("FilterName", 0, "HTML (StarWriter)", 0),
                    PropertyValue("MacroExecutionMode", 0, 0, 0), PropertyValue("UpdateDocMode", 0, 0, 0)))
            finally:
                fragment.unlink(missing_ok=True)
        else:
            self.doc.Text.insertString(cursor, text.replace("\r\n", "\n").replace("\r", "\n"), False)
        full = self.doc.Text.createTextCursorByRange(start.Start)
        if was_empty:
            full.gotoStart(False)
        else:
            full.goRight(1, False)
        full.gotoEnd(True)
        if rich:
            # HTML import adds its own final paragraph separator. A repeat label
            # belongs to the fragment, before that separator, not on another line.
            value = full.String
            trailing = len(value[len(value.rstrip('\r\n')):].replace('\r\n', '\n'))
            if trailing:
                end = self.doc.Text.createTextCursor()
                end.gotoEnd(False)
                end.goLeft(trailing, False)
                full.collapseToStart()
                full.gotoRange(end.Start, True)
        bookmark = self.doc.createInstance("com.sun.star.text.Bookmark")
        bookmark.Name = "LOPATA_capture_" + uuid.uuid4().hex
        self.doc.Text.insertTextContent(full, bookmark, True)
        self.capture = {"text": text, "count": 1, "bookmark": bookmark.Name, "display": full.String}
        self.autosave(True)
        return {"repeat": 1}

    def edit(self, request):
        action = request["action"]
        view = self.doc.CurrentController.ViewCursor
        if action == "command":
            self.command(request["name"], request.get("arguments"))
        elif action == "insert":
            view.Text.insertString(view, request["text"], True)
            view.collapseToEnd()
        elif action == "correct":
            original = request["original"].replace('\r\n', '\n').replace('\r', '\n')
            replacement = request["text"].replace('\r\n', '\n').replace('\r', '\n')
            if view.String.replace('\r\n', '\n').replace('\r', '\n') != original:
                raise RuntimeError("OfficeEditor.SelectionChanged")
            start = view.Text.createTextCursorByRange(view.Start)
            undo = self.doc.UndoManager
            undo.enterUndoContext("Spelling")
            try:
                # Apply only changed ranges, backwards. The untouched rich text,
                # paragraph styles, hyperlinks, and document undo remain intact.
                for tag, a, b, c, d in reversed(difflib.SequenceMatcher(None, original, replacement, autojunk=False).get_opcodes()):
                    if tag == 'equal': continue
                    cursor = start.Text.createTextCursorByRange(start.Start)
                    cursor.goRight(len(original[:a].encode('utf-16-le')) // 2, False)
                    cursor.goRight(len(original[a:b].encode('utf-16-le')) // 2, True)
                    cursor.String = replacement[c:d]
            finally:
                undo.leaveUndoContext()
        elif action == "capture":
            return self.capture_text(request)
        elif action == "text":
            return view.String if request.get("selection") else self.doc.Text.String
        elif action == "pageText":
            return self.text_range(request["first"], request["count"])
        elif action == "format":
            allowed = {"CharFontName", "CharHeight", "CharWeight", "CharPosture", "CharColor",
                       "ParaStyleName", "ParaAdjust", "CharLocale", "ParaLeftMargin", "ParaRightMargin"}
            name = request["name"]
            if name not in allowed:
                raise RuntimeError("OfficeEditor.Unsupported")
            value = request["value"]
            if name == "CharLocale":
                parts = value.split('-')
                value = uno.createUnoStruct("com.sun.star.lang.Locale", parts[0], parts[1] if len(parts) > 1 else ("RU" if value == "ru" else "US"), "")
            view.setPropertyValue(name, value)
        elif action == "zoom":
            self.doc.CurrentController.ViewSettings.ZoomType = 3
            self.doc.CurrentController.ViewSettings.ZoomValue = max(30, min(400, request["value"]))
        elif action == "find":
            descriptor = self.doc.createSearchDescriptor()
            descriptor.SearchString = request["text"]
            descriptor.SearchCaseSensitive = request.get("case", False)
            found = self.doc.findNext(view.End, descriptor)
            if found is None:
                found = self.doc.findFirst(descriptor)
            if found:
                self.doc.CurrentController.select(found)
            return found is not None
        elif action == "replace":
            descriptor = self.doc.createReplaceDescriptor()
            descriptor.SearchString, descriptor.ReplaceString = request["text"], request["replacement"]
            descriptor.SearchCaseSensitive = request.get("case", False)
            return self.doc.replaceAll(descriptor)
        elif action == "table":
            table = self.doc.createInstance("com.sun.star.text.TextTable")
            table.initialize(max(1, min(30, request["rows"])), max(1, min(20, request["columns"])))
            view.Text.insertTextContent(view, table, False)
        elif action == "image":
            graphic = self.doc.createInstance("com.sun.star.text.TextGraphicObject")
            provider = self.manager.createInstanceWithContext("com.sun.star.graphic.GraphicProvider", self.context)
            graphic.Graphic = provider.queryGraphic((PropertyValue("URL", 0, Path(request["path"]).resolve().as_uri(), 0),))
            size = graphic.Graphic.SizePixel
            if size.Width <= 0 or size.Height <= 0:
                size = graphic.Graphic.Size100thMM
            ratio = size.Height / max(1, size.Width)
            graphic.Width, graphic.Height = 8000, max(100, int(8000 * ratio))
            graphic.AnchorType = uno.Enum("com.sun.star.text.TextContentAnchorType", "AS_CHARACTER")
            view.Text.insertTextContent(view, graphic, False)
        elif action == "comment":
            note = self.doc.createInstance("com.sun.star.text.TextField.Annotation")
            note.Content = request["text"]
            view.Text.insertTextContent(view, note, False)
        elif action == "page":
            style = self.doc.StyleFamilies.getByName("PageStyles").getByName(view.PageStyleName)
            values = {"LeftMargin": request["left"], "RightMargin": request["right"],
                      "TopMargin": request["top"], "BottomMargin": request["bottom"]}
            for name, value in values.items():
                style.setPropertyValue(name, max(0, min(8000, int(value))))
        else:
            raise RuntimeError("OfficeEditor.Unsupported")
        return True
