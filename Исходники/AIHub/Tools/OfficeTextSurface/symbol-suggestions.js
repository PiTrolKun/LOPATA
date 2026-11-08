import { paragraphTextOf } from '@docx-editor.dev/core/store';

// The host searches the bundled dictionary. Only an explicit choice changes the word.
export class SymbolSuggestions {
  constructor(editor,pane,post,replace) {
    this.editor=editor;this.pane=pane;this.post=post;this.replace=replace;this.serial=0;this.typing=false;
    this.input=e=>{if(e.inputType==='insertText' && !e.isComposing && e.data)this.schedule();};
    this.composition=()=>this.schedule();
    this.pointer=e=>{if(!this.menu?.contains(e.target))this.close();};
    this.scroll=()=>this.close();this.key=e=>this.keydown(e);
    this.blur=()=>this.close();window.addEventListener('blur',this.blur);
    pane.addEventListener('beforeinput',this.input,true);pane.addEventListener('compositionend',this.composition,true);
    pane.addEventListener('keydown',this.key,true);pane.addEventListener('scroll',this.scroll,true);
    document.addEventListener('pointerdown',this.pointer,true);
  }
  context() {
    const surface=this.editor.surface,{head,anchor}=surface.state().selection;
    if(!this.editor.snapshot().editable || head.paragraphId!==anchor.paragraphId || head.offset!==anchor.offset)return null;
    const text=paragraphTextOf(surface.session.part(),head.paragraphId);
    if(typeof text!=='string')return null;
    const prefix=text.slice(Math.max(0,head.offset-64),head.offset);
    const match=prefix.match(/(?::-?\)|=\)|:-?D|=D|XD|:-?\(|;-?\)|:-?P|<3|[\p{L}\p{M}]{2,40})$/iu);
    if(!match)return null;
    const start=head.offset-match[0].length;
    if(start>0 && /[\p{L}\p{M}\p{N}@/:_]/u.test(text[start-1]))return null;
    return {head:{...head},word:match[0],start,stamp:JSON.stringify({head,revision:surface.session.packageRevision()})};
  }
  schedule() {
    this.close();this.typing=true;
    this.timer=setTimeout(()=>{
      this.typing=false;const context=this.context();if(!context)return;
      this.pending={...context,key:String(++this.serial)};
      this.post({type:'symbolSuggest',word:context.word,key:this.pending.key});
    },80);
  }
  selectionChanged() {if(!this.typing && this.pending?.stamp!==this.context()?.stamp)this.close();}
  receive(reply) {
    const context=this.context();
    if(!this.pending || reply.key!==this.pending.key || context?.stamp!==this.pending.stamp || context.word!==this.pending.word)return;
    if(!reply.items?.length)return;
    this.items=reply.items;this.index=-1;
    const menu=document.createElement('div');menu.className='lopata-symbol-suggestions';menu.setAttribute('role','listbox');
    menu.setAttribute('aria-label',document.documentElement.lang.startsWith('ru')?'Подсказки символов':'Symbol suggestions');
    for(const [index,item] of this.items.entries()) {
      const button=document.createElement('button');button.type='button';button.textContent=item.text;button.title=item.name;
      button.dataset.suggestion=item.text;button.setAttribute('role','option');
      button.addEventListener('pointerdown',e=>{e.preventDefault();e.stopPropagation();});
      button.addEventListener('click',()=>this.choose(index));menu.append(button);
    }
    const hint=document.createElement('small');hint.textContent=document.documentElement.lang.startsWith('ru')?'Tab — выбрать · Esc — скрыть':'Tab to select · Esc to dismiss';
    menu.append(hint);this.menu=menu;document.body.append(menu);
    const caret=this.pane.querySelector('.docx-editor-one-surface__caret')?.getBoundingClientRect();
    if(!caret){this.close();return;}
    menu.style.left=Math.max(4,Math.min(caret.left,innerWidth-menu.offsetWidth-4))+'px';
    const above=caret.top-menu.offsetHeight-5;
    menu.style.top=Math.max(4,Math.min(above>=4?above:caret.bottom+5,innerHeight-menu.offsetHeight-4))+'px';
  }
  choose(index) {
    const context=this.context(),pending=this.pending,item=this.items?.[index];
    if(!item || !pending || context?.stamp!==pending.stamp || context.word!==pending.word){this.close();return;}
    const selection={anchor:{...context.head,offset:context.start},head:context.head};
    this.close();this.replace(selection,item.text);this.editor.focus();
  }
  keydown(e) {
    if(this.menu && ['ArrowDown','ArrowUp','Tab','Enter','Escape'].includes(e.key)) {
      if(e.key==='Enter' && this.index<0 || e.key==='Tab' && e.shiftKey)return;
      e.preventDefault();e.stopImmediatePropagation();
      if(e.key==='Escape'){this.close();return;}
      if(e.key==='Tab' || e.key==='Enter'){this.choose(Math.max(0,this.index));return;}
      this.index=this.index<0?(e.key==='ArrowDown'?0:this.items.length-1):(this.index+(e.key==='ArrowDown'?1:-1)+this.items.length)%this.items.length;
      [...this.menu.querySelectorAll('button')].forEach((button,index)=>button.classList.toggle('active',index===this.index));
      return;
    }
    if(['ArrowLeft','ArrowRight','Home','End','Enter',' ','Tab','Escape'].includes(e.key) || e.ctrlKey || e.metaKey)this.close();
  }
  close() {clearTimeout(this.timer);this.typing=false;this.pending=null;this.menu?.remove();this.menu=null;this.items=null;}
  destroy() {
    this.close();this.pane.removeEventListener('beforeinput',this.input,true);this.pane.removeEventListener('compositionend',this.composition,true);
    this.pane.removeEventListener('keydown',this.key,true);this.pane.removeEventListener('scroll',this.scroll,true);
    document.removeEventListener('pointerdown',this.pointer,true);
    window.removeEventListener('blur',this.blur);
  }
}
