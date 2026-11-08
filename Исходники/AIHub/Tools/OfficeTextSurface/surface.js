import { createDocxEditor } from '@docx-editor.dev/core';
import { packagedFonts } from '@docx-editor.dev/fonts';
import { Spelling } from './spelling.js';
import { SymbolSuggestions } from './symbol-suggestions.js';
import { textCompatibility, compareCompatibility, serializeCompatibilityBaseline, restoreCompatibilityBaseline } from './text-compatibility.js';

const bridge = window.chrome?.webview;
const pane = document.querySelector('#document');
const post = message => bridge?.postMessage(message);
let editor, spelling, suggestions, serial = Promise.resolve(), capture = null, previousFind = null;
let richEdit = false, changeKind = 'text', lastKind = 'text', baseline = 0;
let statsRevision, stats = {words:0,characters:0}, pendingState = false;
let originalFeatures=null, sourceFormat='';
function bytes(base64) {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}
function toBase64(buffer) {
  const data = new Uint8Array(buffer), chunks = [];
  for (let i=0;i<data.length;i+=0x6000) chunks.push(String.fromCharCode(...data.subarray(i,i+0x6000)));
  return btoa(chunks.join(''));
}
const revision = () => editor.surface.session.packageRevision();
const bodyText = () => editor.surface.session.bodyText();
const selectedText = () => editor.surface.selectedText();
function state(includeStats = true) {
  const snapshot = editor.snapshot(), rev = revision();
  if (includeStats && rev !== statsRevision) {
    const text = bodyText();
    stats = {characters:text.length, words:text.match(/\S+/gu)?.length || 0};
    statsRevision = rev;
  }
  return {revision:rev, dirty:rev !== baseline, selected:!snapshot.selectionCollapsed, page:snapshot.page.current,
    pages:snapshot.page.total, canUndo:!!snapshot.canUndo, canRedo:!!snapshot.canRedo,
    richEdit,changeKind:lastKind,fontMeasurement:editor.fontMeasurement().measurer, ...stats};
}
function changed() {
  if (!editor?.surface || pendingState) return;
  pendingState = true;
  requestAnimationFrame(() => {
    pendingState = false;
    if (editor?.surface) post({type:'state',event:'state',...state(false)});
  });
}
function checked(result) {
  if (result?.ok === false) throw new Error(result.reason || result.code);
  return result;
}
const exec = (type, properties = {}) => checked(editor.exec({type,...properties}));
// EigenPal's .dark class inverts the document itself. The workspace theme
// changes only the area around the paper and leaves document colors intact.
function setDark(dark) { document.body.classList.toggle('workspace-dark', !!dark); }
async function open(request) {
  spelling?.destroy();
  suggestions?.destroy();
  editor?.destroy(); pane.replaceChildren();
  setDark(request.dark);
  document.documentElement.lang = request.lang || 'ru';
  capture = previousFind = null; richEdit = false; lastKind=changeKind='text'; statsRevision=undefined;
  editor = createDocxEditor({document:request.base64 ? bytes(request.base64) : 'blank', container:pane,
    locale:document.documentElement.lang, mode:request.readOnly?'view':'edit', fonts:packagedFonts()});
  const deadline = performance.now() + 90000;
  while (editor.snapshot().isLoading || !editor.surface) {
    if (editor.snapshot().parseError) throw new Error(editor.snapshot().parseError);
    if (performance.now() > deadline) throw new Error('OfficeEditor.EngineUnavailable');
    await new Promise(resolve => setTimeout(resolve,12));
  }
  baseline = revision();
  sourceFormat=String(request.sourceFormat || '').replace(/^\./,'').toLowerCase();
  originalFeatures=null;
  if(['txt','md','markdown'].includes(sourceFormat)) {
    originalFeatures=request.compatibilityBaseline==null ? textCompatibility(editor.surface.session.part().root) :
      restoreCompatibilityBaseline(request.compatibilityBaseline);
  }
  editor.on('change', () => { lastKind=changeKind; if (changeKind !== 'text') richEdit=true; changed(); spelling?.schedule(); });
  editor.on('selectionChange', () => {changed();suggestions?.selectionChanged();});
  spelling = new Spelling(editor,document.querySelector('#viewport'),post);
  spelling.schedule();
  suggestions=new SymbolSuggestions(editor,document.querySelector('#viewport'),post,(selection,text)=>{
    changeKind='text';editor.surface.setSelection(selection);exec('pasteWithoutFormatting',{text});
  });
  return state();
}
function command(name) {
  switch (name.replace(/^\.uno:/,'')) {
    case 'Bold': return exec('toggleMark',{mark:'bold'});
    case 'Italic': return exec('toggleMark',{mark:'italic'});
    case 'Underline': return exec('toggleMark',{mark:'underline'});
    case 'LeftPara': return exec('setAlignment',{align:'left'});
    case 'CenterPara': return exec('setAlignment',{align:'center'});
    case 'RightPara': return exec('setAlignment',{align:'right'});
    case 'DefaultBullet': return exec('toggleList',{kind:'bullet'});
    case 'DefaultNumbering': return exec('toggleList',{kind:'ordered'});
    case 'InsertPagebreak': return exec('insertBreak',{kind:'page'});
    case 'Undo': return exec('undo');
    case 'Redo': return exec('redo');
    case 'SelectAll': return exec('selectAll');
    case 'Copy':return exec('copy');
    case 'Cut':return exec('cut');
    default: throw new Error('OfficeEditor.CommandUnavailable');
  }
}
function applyFormat(request) {
  switch(request.name) {
    case 'ParaStyleName': {
      const key=String(request.value).replace(/ /g,'').replace(/^Standard$/,'Normal').toLowerCase();
      const style=editor.getDocumentStyles().find(s => s.type==='paragraph' && [s.styleId,s.name].some(
        value => value?.replace(/ /g,'').toLowerCase()===key));
      if(!style) throw new Error('OfficeEditor.StyleUnavailable');
      return exec('setParagraphStyle',{styleId:style.styleId});
    }
    case 'CharFontName':return exec('setMarkAttr',{mark:'fontFamily',attr:'value',value:request.value});
    case 'CharHeight':return exec('setMarkAttr',{mark:'fontSize',attr:'value',value:Math.round(Number(request.value)*2)});
    case 'CharLocale': {
      const lang = request.value === 'en' ? 'en-US' : 'ru-RU';
      document.documentElement.lang=lang;
      // This selector chooses the proofing dictionary for the active surface.
      // The upstream free command set cannot author w:lang as a run change.
      spelling.schedule(); return {proofingLanguage:lang};
    }
    default:throw new Error('OfficeEditor.CommandUnavailable');
  }
}
function addComment(text) {
  const surface=editor.surface, {anchor,head}=surface.state().selection;
  const scope=surface.storyScope();
  const ids=surface.session.paragraphIdsIn(scope);
  const backwards=ids.indexOf(anchor.paragraphId)>ids.indexOf(head.paragraphId) ||
    (anchor.paragraphId===head.paragraphId && anchor.offset>head.offset);
  const [a,b]=backwards ? [head,anchor] : [anchor,head];
  const author=document.documentElement.lang.startsWith('ru')?'Автор':'Author';
  const id=surface.session.replyToComment(null,{paragraphId:a.paragraphId,start:a.offset,end:b.offset,
    ...(a.paragraphId===b.paragraphId?{}:{endParagraphId:b.paragraphId})}, text,author,new Date().toISOString(),scope);
  if (id===null) throw new Error('OfficeEditor.CommandUnavailable');
  return {id};
}
function collect(request) {
  const text=request.text;
  if (!text?.trim()) throw new Error('OfficeEditor.NoSelection');
  const surface=editor.surface, session=surface.session;
  const previous=capture;
  if(previous?.text===text) {
    const ids=session.paragraphIds();
    if(ids.includes(previous.start.paragraphId)&&ids.includes(previous.end.paragraphId)) {
      surface.setSelection({anchor:previous.start,head:previous.end});
      if(surface.selectedText()===previous.display) {
        const prior=previous.count>1 ? ' {x'+previous.count+'}' : '';
        const suffix=' {x'+(previous.count+1)+'}';
        surface.setSelection({anchor:{...previous.end,offset:previous.end.offset-prior.length},head:previous.end});
        exec('pasteWithoutFormatting',{text:suffix});
        capture={...previous,count:previous.count+1,end:surface.state().selection.head,
          display:previous.display.slice(0,previous.display.length-prior.length)+suffix};
        return {repeat:capture.count};
      }
    }
  }
  surface.navigate('documentEnd');
  if (session.bodyText()) surface.splitParagraph();
  const start=surface.state().selection.head;
  exec('paste',{text,html:request.html || undefined});
  const end=surface.state().selection.head;
  surface.setSelection({anchor:start,head:end});
  capture={text,count:1,start,end,display:surface.selectedText()};
  surface.setSelection({anchor:end,head:end});
  return {repeat:capture.count};
}
function pageText(request) {
  const parts=[];
  function walk(blocks) {
    for(const block of blocks) {
      if (block.kind==='paragraph') parts.push(block.lines.map(l=>l.spans.map(s=>s.text).join('')).join(''));
      else if (block.kind==='table') for(const row of block.rows) for(const cell of row.cells) walk(cell.blocks);
    }
  }
  const pages=editor.surface.layout().pages;
  for(const page of pages.slice(request.first,request.first+request.count)) walk(page.fragments);
  return parts.join('\n');
}
function markKind(request) {
  if(['format','table','image','comment','page'].includes(request.action)) return request.action;
  if(request.action==='command' && !/Undo|Redo|SelectAll|Copy|Cut/.test(request.name)) return 'format';
  if(['paste','capture'].includes(request.action) && request.html) return 'formattedPaste';
  return 'text';
}
async function handle(r) {
  if(r.action==='open') return open(r);
  if(r.action==='theme') {setDark(r.dark);return;}
  if(!editor?.surface) throw new Error('OfficeEditor.EngineUnavailable');
  editor.surface.flushPendingInput();
  if(!editor.snapshot().editable && ['format','image','page','comment','replace','paste','capture','table','deleteSelection','correct'].includes(r.action))
    throw new Error('OfficeEditor.ReadOnly');
  changeKind=markKind(r);
  try {
    switch(r.action) {
      case 'state':return state();
      case 'symbolSuggestions':suggestions?.receive(r);return;
      case 'compatibilityBaseline':return originalFeatures ? serializeCompatibilityBaseline(originalFeatures) : null;
      case 'compatibility':return originalFeatures
        ? compareCompatibility(originalFeatures,editor.surface.session.part().root)
        : {plainCompatible:!richEdit,markdownCompatible:!richEdit,plainReasons:richEdit?['object']:[],markdownReasons:richEdit?['object']:[]};
      case 'mode':editor.setMode(r.readOnly?'view':'edit');return state();
      case 'text':return r.selection ? selectedText() : bodyText();
      case 'clipboard': {
        const flavours=editor.surface.copyFlavours();
        if(flavours.reason)throw new Error(flavours.reason);
        return {text:flavours.text,html:flavours.html};
      }
      case 'comments':return editor.getComments();
      case 'pageText':return pageText(r);
      case 'gotoPage':return editor.surface.revealPage(Math.max(0,r.page-1));
      case 'focus':editor.focus();return;
      case 'zoom':return checked(editor.setZoom(r.value/100));
      case 'command':return command(r.name);
      case 'format':return applyFormat(r);
      case 'table':return exec('insertTable',{rows:r.rows,cols:r.columns});
      case 'paste':return exec('paste',{text:r.text || '',html:r.html || undefined});
      case 'deleteSelection':if(selectedText()) editor.surface.deleteBackward();return;
      case 'correct':
        if(r.stamp && r.stamp!==JSON.stringify({revision:revision(),selection:editor.surface.state().selection}))
          throw new Error('OfficeEditor.SelectionChanged');
        if(r.original !== selectedText())throw new Error('OfficeEditor.SelectSpelling');
        return exec('pasteWithoutFormatting',{text:r.text});
      case 'capture':return collect(r);
      case 'spelling':return spelling.accept(r);
      case 'find': {
        const matches=editor.findMatches(r.text);
        if (!matches.length) return false;
        const index = previousFind?.text===r.text && previousFind.revision===revision() ? (previousFind.index+1)%matches.length : 0;
        checked(editor.selectMatch(matches[index])); previousFind={index,text:r.text,revision:revision()};return true;
      }
      case 'replace': {
        const matches=editor.findMatches(r.text);
        const operations=[...matches].reverse().map(match=>({type:'replaceMatch',match,text:r.replacement}));
        for(const op of operations)checked(editor.can(op));
        const group=editor.beginHistoryGroup();
        let wrote=false;
        try {
          for(const op of operations){
            const result=checked(editor.exec(op,{historyGroup:group}));
            wrote ||= result.changed;
          }
        } catch(error){
          group.end();
          if(wrote)exec('undo');
          throw error;
        } finally { if(group.state==='open')group.end(); }
        return matches.length;
      }
      case 'image': {
        const [header,b64]=r.data.split(',');
        const mime=/^data:(image\/[\w.+-]+);base64$/.exec(header)?.[1];
        if (!mime || !b64) throw new Error('OfficeEditor.InvalidImage');
        const data=bytes(b64), url=URL.createObjectURL(new Blob([data],{type:mime}));
        try { const img=new Image(); img.src=url; await img.decode();
          const width=Math.min(460,img.naturalWidth*0.75);
          return checked(await editor.executeImageCommand({type:'insertImage',data,mime,widthPoints:width,heightPoints:width*img.naturalHeight/img.naturalWidth}));
        } finally { URL.revokeObjectURL(url); }
      }
      case 'comment':return addComment(r.text);
      case 'page': {
        const twips=key => Math.round(r[key]*1440/2540);
        return exec('setPageSetup',{scope:'document',marginLeft:twips('left'),marginRight:twips('right'),marginTop:twips('top'),marginBottom:twips('bottom')});
      }
      case 'export': {
        const before=revision();
        const buffer=await editor.save();
        if(before!==revision())throw new Error('OfficeEditor.ChangedDuringSave');
        return {base64:toBase64(buffer),revision:before};
      }
      default:throw new Error('OfficeEditor.CommandUnavailable');
    }
  } finally {changeKind='text';}
}
bridge?.addEventListener('message',({data})=>{
  serial=serial.then(async()=>{
    try {const result=await handle(data);if(data.id!=null)post({id:data.id,ok:true,result:result??null});}
    catch(error){if(data.id!=null)post({id:data.id,ok:false,error:String(error.message || error)});}
  });
});
window.addEventListener('keydown',e=>{
  if(!e.ctrlKey||e.altKey||e.metaKey)return;
  const name={KeyS:e.shiftKey?'saveAs':'save',KeyN:'new',KeyO:'open',KeyW:'close'}[e.code];
  if(name){e.preventDefault();e.stopImmediatePropagation();post({type:'shortcut',name});}
},true);
post({type:'ready',event:'ready'});
