(() => {
    'use strict';
    const scroll = document.getElementById('scroll'), edit = document.getElementById('edit');
    const exclude = document.getElementById('exclude');
    const send = document.getElementById('send');
    const text = () => edit.innerText.replace(/\r\n/g, '\n');
    let scope = '', dictation = null;
    const post = message => chrome.webview.postMessage({...message, scope});
    let composing = false;
    function layout() {
        // The exclusion belongs to layout, outside the editable span, so selection, undo and
        // paste cannot remove it. Only the bottom visible lines give up their right edge.
        exclude.style.height = Math.max(0, scroll.scrollTop + scroll.clientHeight - 16) + 'px';
    }
    function end() {
        edit.focus();
        const range = document.createRange(); range.selectNodeContents(edit); range.collapse(false);
        const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
    }
    edit.addEventListener('input', () => { post({type:'input', text:text()}); layout(); });
    edit.addEventListener('compositionstart', () => composing = true);
    edit.addEventListener('compositionend', () => composing = false);
    edit.addEventListener('keydown', e => {
        if (e.key === 'Enter' && !e.shiftKey && !e.altKey && !e.metaKey && !composing && !e.isComposing) {
            e.preventDefault();
            if (!e.repeat) post({type:'send'});
        }
    });
    edit.addEventListener('paste', e => {
        e.preventDefault();
        if (e.clipboardData.files.length) { post({type:'pasteFiles'}); return; }
        // Paste only text. Native insertText preserves browser undo and selection semantics.
        document.execCommand('insertText', false, e.clipboardData.getData('text/plain'));
    });
    document.addEventListener('dragover', e => { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect='copy'; });
    document.addEventListener('drop', e => {
        e.preventDefault();
        if (e.dataTransfer.files.length) chrome.webview.postMessageWithAdditionalObjects({type:'dropFiles', scope}, Array.from(e.dataTransfer.files));
    });
    send.addEventListener('click', () => { if (!send.disabled) post({type:'send'}); });
    scroll.addEventListener('scroll', layout);
    scroll.addEventListener('mousedown', e => { if (e.target !== edit && !edit.contains(e.target)) { e.preventDefault(); end(); } });
    new ResizeObserver(layout).observe(scroll);
    window.composer = {
        focus: end,
        set(value, context) {
            if (context !== undefined && context !== scope) { scope = context; dictation = null; }
            if (text() === value.replace(/\r\n/g, '\n')) return;
            edit.textContent = value; layout();
            if (document.activeElement === edit) end();
        },
        dictate(chunk) {
            if (chunk.scope !== scope || composing) return false;
            const selection = getSelection();
            function selected() {
                if (selection.rangeCount && edit.contains(selection.getRangeAt(0).commonAncestorContainer))
                    return selection.getRangeAt(0).cloneRange();
                const range = document.createRange(); range.selectNodeContents(edit); range.collapse(false); return range;
            }
            if (!dictation || dictation.recording !== chunk.recording || dictation.segment !== chunk.segment)
                dictation = {recording:chunk.recording, segment:chunk.segment, range:selected(), owned:'', hypothesis:'', ignored:0, final:false};
            const d = dictation;
            if (d.final) return true;
            const current = selected();
            if (d.hypothesis && (d.range.toString() !== d.owned || !current.collapsed ||
                current.compareBoundaryPoints(Range.END_TO_END, d.range) !== 0)) {
                d.ignored = d.hypothesis.length; d.range = current; d.owned = '';
            }
            d.hypothesis = chunk.text;
            const remaining = chunk.text.slice(Math.min(d.ignored, chunk.text.length));
            if (!remaining && !d.owned) { d.final = chunk.final; return true; }
            const before = document.createRange(); before.selectNodeContents(edit);
            before.setEnd(d.range.startContainer, d.range.startOffset);
            const previous = before.toString().slice(-1);
            const needsSpace = d.owned.startsWith(' ') || (!d.owned && /[\p{L}\p{N}]/u.test(previous) && /^[\p{L}\p{N}]/u.test(remaining));
            const replacement = (needsSpace ? ' ' : '') + remaining;
            edit.focus(); selection.removeAllRanges(); selection.addRange(d.range);
            const start = d.range.cloneRange(); start.collapse(true);
            // Native editing preserves multiline plain text, selection and browser Undo.
            if (!document.execCommand('insertText', false, replacement)) return false;
            const inserted = start.cloneRange();
            inserted.setEnd(selection.focusNode, selection.focusOffset);
            d.range = inserted; d.owned = replacement; d.final = chunk.final;
            layout(); return true;
        },
        theme(theme) {
            document.body.style.color = theme.color;
            document.body.style.backgroundColor = theme.background;
            document.body.style.fontFamily = theme.font;
            document.body.style.fontSize = theme.size + 'px';
            edit.setAttribute('aria-label', theme.label);
            send.disabled = !theme.enabled;
            send.setAttribute('aria-label', theme.send); send.title = theme.send;
            document.getElementById('hint').textContent = theme.hint;
            layout();
        }
    };
    layout();
})();
