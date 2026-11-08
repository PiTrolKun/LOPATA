namespace AIHub.Controls.OfficeTextEditor;

internal static class OfficeSymbolsBrowserPage
{
    internal const string Html = """
<!doctype html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
<style>
html,body{height:100%;margin:0;overflow:hidden;color:var(--text,#202735);background:var(--panel,#fff);font:14px 'Segoe UI',sans-serif}
body{display:flex;flex-direction:column}*{box-sizing:border-box}
#viewport{flex:1;overflow:auto;position:relative;min-height:0;border:1px solid var(--line,#ccd3df)}
#spacer{position:relative;width:100%}#rows{position:absolute;left:0;right:0;display:grid;grid-template-columns:repeat(8,minmax(0,1fr));}
button{height:80px;min-width:0;margin:3px;padding:3px;cursor:pointer;color:inherit;background:transparent;border:1px solid var(--line,#ccd3df);border-radius:6px;overflow:hidden}
button:hover,button:focus-visible{background:#6699ff33;outline:2px solid #6499e0;outline-offset:-2px}
.glyph{display:block;font-family:var(--glyph-font,'Segoe UI Emoji');font-size:30px;height:42px;line-height:42px;text-align:center;white-space:nowrap}
.code{display:block;font-size:10px;white-space:nowrap;text-overflow:ellipsis;overflow:hidden;text-align:center}
#selected{display:none;font-family:var(--glyph-font,'Segoe UI Emoji');font-size:26px;line-height:38px;white-space:pre;overflow-x:auto;flex-shrink:0;max-height:52px;padding:2px 5px}
</style></head><body>
<main id="viewport"><div id="spacer"><div id="rows" role="list"></div></div></main>
<div id="selected" role="img"></div>
<script>
'use strict';
const viewport=document.getElementById('viewport'),spacer=document.getElementById('spacer'),rows=document.getElementById('rows'),selected=document.getElementById('selected');
const rowHeight=86,columns=8;let revision=0,count=0,requested='',frame=0;
function post(message){chrome.webview.postMessage(message)}
function range(){
  const start=Math.max(0,Math.floor(viewport.scrollTop/rowHeight)-2)*columns;
  const length=Math.min(256,(Math.ceil(viewport.clientHeight/rowHeight)+5)*columns);
  const key=revision+':'+start+':'+length;if(key===requested)return;requested=key;
  if(count)post({type:'range',revision,start,count:length});
}
function schedule(){cancelAnimationFrame(frame);frame=requestAnimationFrame(range)}
viewport.addEventListener('scroll',schedule,{passive:true});new ResizeObserver(schedule).observe(viewport);
chrome.webview.addEventListener('message',event=>{
  const message=event.data;
  if(message.type==='style'){
    document.documentElement.lang=message.language;
    for(const [name,value] of [['text',message.foreground],['panel',message.background],['line',message.line]])document.documentElement.style.setProperty('--'+name,value);
    document.documentElement.style.setProperty('--glyph-font',message.fonts.map(f=>JSON.stringify(f)).join(','));
    selected.setAttribute('aria-label',message.selectedLabel);return;
  }
  if(message.type==='selection'){
    selected.textContent=message.text;selected.style.display=message.text?'block':'none';selected.scrollLeft=selected.scrollWidth;schedule();return;
  }
  if(message.type==='library'){
    revision=message.revision;count=message.count;requested='';viewport.scrollTop=0;rows.replaceChildren();
    spacer.style.height=Math.ceil(count/columns)*rowHeight+'px';document.body.dataset.count=count;schedule();return;
  }
  if(message.type!=='rows'||message.revision!==revision)return;
  const start=Math.max(0,Math.floor(viewport.scrollTop/rowHeight)-2)*columns;
  if(start!==message.start)return;
  rows.style.top=Math.floor(start/columns)*rowHeight+'px';rows.replaceChildren();
  for(const [offset,item] of message.items.entries()){
    const button=document.createElement('button');button.type='button';button.title=item.hint;button.dataset.index=message.start+offset;button.dataset.symbol=item.text;button.setAttribute('aria-label',item.hint);
    const glyph=document.createElement('span');glyph.className='glyph';glyph.textContent=item.text;
    const code=document.createElement('span');code.className='code';code.textContent=item.code;
    button.append(glyph,code);button.addEventListener('click',()=>post({type:'choose',revision,start:message.start+offset}));rows.append(button);
  }
});
post({type:'ready'});
</script></body></html>
""";
}
