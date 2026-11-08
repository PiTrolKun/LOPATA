// Windows checks the visible text. Use the engine's UTF-16 ranges to mark the painted
// paragraphs. A response for a paragraph whose text has changed is never applied.
export class Spelling {
  constructor(editor, pane, post) {
    this.editor=editor; this.pane=pane; this.post=post;
    this.paragraphs=new Map(); this.pending=new Map(); this.number=0;
    this.generation=crypto.randomUUID();
    this.scroll=()=>this.schedule();
    this.context=e=>this.showMenu(e);
    this.close=()=>this.menu?.remove();
    pane.addEventListener('scroll',this.scroll,true);
    pane.addEventListener('contextmenu',this.context,true);
    document.addEventListener('pointerdown',this.close);
  }
  schedule() {
    clearTimeout(this.timer);
    this.timer=setTimeout(()=>this.scan(),340);
  }
  scan() {
    const surface=this.editor.surface;
    if(!surface || !this.editor.snapshot().editable) return;
    const pages=surface.layout().pages, current=surface.currentPage('viewport');
    const visible=new Map(), lang=document.documentElement.lang;
    function gather(blocks) {
      for(const b of blocks) {
        if(b.kind==='table') { for(const row of b.rows) for(const cell of row.cells) gather(cell.blocks);continue; }
        if(b.kind!=='paragraph')continue;
        let chars=visible.get(b.paragraphId) || '';
        for(const line of b.lines) for(const span of line.spans) {
          const {start,end}=span.range;
          if(span.text.length===end-start && start>=chars.length && end<=65536)
            chars=chars.padEnd(start,' ')+span.text;
        }
        visible.set(b.paragraphId,chars);
      }
    }
    for(const page of pages.slice(Math.max(0,current-1),current+2)) gather(page.fragments);
    for(const [id,entry] of this.paragraphs) if(!visible.has(id)) {
      this.pending.delete(entry.key); this.paragraphs.delete(id);
    }
    for(const [id,text] of visible) {
      const prior=this.paragraphs.get(id);
      if(prior?.text===text && prior.lang===lang)continue;
      if(prior)this.pending.delete(prior.key);
      const key=this.generation+'-'+(++this.number);
      const entry={key,id,text,lang,ranges:[]};
      this.paragraphs.set(id,entry);
      if(text.trim()){this.pending.set(key,entry);this.post({type:'spellCheck',key,text,lang});}
    }
    this.paint();
  }
  accept({key,issues}) {
    const entry=this.pending.get(key);
    if(!entry)return;
    this.pending.delete(key);
    entry.ranges=(issues || []).filter(i=>Number.isInteger(i.offset) && i.offset>=0 && i.length>0 && i.offset+i.length<=entry.text.length)
      .map(i=>({blockId:entry.id,start:i.offset,length:i.length,expectedText:entry.text.slice(i.offset,i.offset+i.length),suggestions:i.suggestions || []}));
    this.paint();
  }
  paint() {
    const ranges=[...this.paragraphs.values()].flatMap(e=>e.ranges);
    this.editor.setHighlights('lopataSpelling',ranges,{color:'transparent',className:'lopata-misspelled',priority:1});
  }
  showMenu(e) {
    if(!this.editor.snapshot().editable)return;
    const selected=this.editor.surface.selectedText();
    const hit=this.editor.getHighlightsAt(e.clientX,e.clientY).find(h=>h.name==='lopataSpelling');
    const source=hit && this.paragraphs.get(hit.range.blockId)?.ranges.find(r=>r.expectedText===hit.range.expectedText && r.start===hit.range.start);
    if(!selected.trim() && !source?.suggestions.length)return;
    e.preventDefault();e.stopImmediatePropagation();this.menu?.remove();
    const menu=document.createElement('div');menu.className='lopata-spelling-menu';menu.setAttribute('role','menu');
    menu.style.left=Math.min(e.clientX,Math.max(0,innerWidth-170))+'px';
    menu.style.top=Math.min(e.clientY,Math.max(0,innerHeight-150))+'px';
    for(const suggestion of (source?.suggestions || []).slice(0,6)) {
      const button=document.createElement('button');button.type='button';button.textContent=suggestion;
      button.addEventListener('pointerdown',e=>e.stopPropagation());
      button.addEventListener('click',()=>{
        const match=this.editor.findMatches(source.expectedText,{matchCase:true}).find(m=>m.blockId===source.blockId&&m.start===source.start);
        if(match)this.editor.exec({type:'replaceMatch',match,text:suggestion});
        menu.remove();this.editor.focus();
      });
      menu.append(button);
    }
    if(selected.trim()) {
      const stamp=JSON.stringify({revision:this.editor.surface.session.packageRevision(),selection:this.editor.surface.state().selection});
      const russian=document.documentElement.lang.startsWith('ru');
      const snapshot=this.editor.snapshot();
      for(const [command,ru,en,enabled] of [
        ['Undo','Отменить','Undo',snapshot.canUndo],['Redo','Повторить','Redo',snapshot.canRedo],
        ['Cut','Вырезать','Cut',true],['Copy','Копировать','Copy',true],['Paste','Вставить','Paste',true],
        ['PastePlain','Вставить только текст','Paste as plain text',true],['Delete','Удалить','Delete',true],
        ['SelectAll','Выделить всё','Select all',true]
      ]) {
        const button=document.createElement('button');button.type='button';button.textContent=russian?ru:en;
        button.dataset.command=command;button.disabled=!enabled;
        button.addEventListener('pointerdown',e=>e.stopPropagation());
        button.addEventListener('click',()=>{menu.remove();this.post({type:'selectionAction',action:command,text:selected,stamp});});
        menu.append(button);
      }
      menu.append(document.createElement('hr'));
      for(const [action,label] of [['rephrase',russian?'Сделать переформулировку':'Rephrase text'],['search',russian?'Найти в интернете':'Search the web']]) {
        const button=document.createElement('button');button.type='button';button.textContent=label;
        button.dataset.action=action;
        button.addEventListener('pointerdown',e=>e.stopPropagation());
        button.addEventListener('click',()=>{menu.remove();this.post({type:'selectionAction',action,text:selected,stamp});});
        menu.append(button);
      }
    }
    this.menu=menu;document.body.append(menu);
    menu.style.left=Math.max(0,Math.min(e.clientX,innerWidth-menu.offsetWidth))+'px';
    menu.style.top=Math.max(0,Math.min(e.clientY,innerHeight-menu.offsetHeight))+'px';
    menu.onkeydown=e=>{if(e.key==='Escape'){menu.remove();this.editor.focus();}};
    menu.querySelector('button:not(:disabled)')?.focus();
  }
  destroy() {
    clearTimeout(this.timer);this.menu?.remove();
    this.pane.removeEventListener('scroll',this.scroll,true);
    this.pane.removeEventListener('contextmenu',this.context,true);
    document.removeEventListener('pointerdown',this.close);
  }
}
