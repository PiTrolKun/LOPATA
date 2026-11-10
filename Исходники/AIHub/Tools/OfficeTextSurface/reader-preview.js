// A read-only print view of the FREE core's fully painted pages, including pages
// outside its interactive viewport. Chromium supplies PDF printing, not Pro.
export async function readerPreview(editor,request) {
  if (editor.snapshot().editable) throw new Error('OfficeEditor.ReadOnly');
  while (editor.fontMeasurement().resolving) await new Promise(resolve => setTimeout(resolve, 12));
  await document.fonts.ready;
  const layout = editor.surface.layout();
  const root = document.createElement('div'); root.id = 'reader-print';
  const pages=[];
  // Preserve the editor's validated image URLs and registered font aliases.
  // Its lower-level painter alone has no access to that private resource port.
  for(let i=0;i<layout.pages.length;i++) {
    editor.surface.revealPage(i,{block:'start'});
    let source;
    const deadline=performance.now()+10000;
    do {
      await new Promise(resolve=>setTimeout(resolve,12));
      source=document.querySelector(`#document .docx-page[data-page-index="${i}"][data-materialized="true"]`);
      if(performance.now()>deadline)throw new Error('Reader.RenderFailed');
    } while(!source);
    await Promise.all([...source.querySelectorAll('img')].map(img=>img.decode().catch(()=>{})));
    const clone=source.cloneNode(true);
    for(const image of clone.querySelectorAll('img[src^="blob:"]')) {
      const blob=await (await fetch(image.src)).blob();
      image.src=await new Promise((resolve,reject)=>{
        const reader=new FileReader();reader.onload=()=>resolve(reader.result);reader.onerror=()=>reject(reader.error);reader.readAsDataURL(blob);
      });
    }
    pages.push(clone);root.append(clone);
  }
  if(!pages.length)throw new Error('Reader.RenderFailed');
  document.body.append(root);
  const rules = [];
  for (let i=0; i<pages.length; i++) {
    const box=layout.pages[i].box, page=pages[i], sheet=document.createElement('section');
    sheet.className='reader-sheet'; sheet.style.page=`reader-sheet-${i}`;
    sheet.style.width=`${box.width*96/72}px`; sheet.style.height=`${box.height*96/72}px`;
    page.replaceWith(sheet); sheet.append(page);
    page.style.left=page.style.top='0px';
    rules.push(`@page reader-sheet-${i}{size:${box.width}pt ${box.height}pt;margin:0}`);
  }
  const style=document.createElement('style'); style.textContent=rules.join('\n'); document.head.append(style);
  document.body.classList.add('reader-printing');
  await Promise.all([...root.querySelectorAll('img')].map(img=>img.decode().catch(()=>{})));
  await document.fonts.ready;
  // Force geometry after fonts/images settle, before normalised hit boxes are read.
  root.getBoundingClientRect();
  const pageOf=new Map(), fragments=new Map();
  for(let i=0;i<pages.length;i++) for(const element of pages[i].querySelectorAll('.docx-paragraph-fragment[data-paragraph-id]')) {
    const id=element.dataset.paragraphId;
    if(!pageOf.has(id))pageOf.set(id,i);
    if(!fragments.has(id))fragments.set(id,[]);
    fragments.get(id).push({page:i,element});
  }
  const bookmarks=new Map(), notes=new Map(), comments=new Map((request.comments||[]).map(c=>[c.id,c.text]));
  const attr=(node,name)=>node.attributes?.find(a=>a.localName===name)?.value;
  function visit(node,paragraph) {
    if(node.localName==='p')paragraph=node.id;
    if(node.localName==='bookmarkStart' && paragraph)bookmarks.set(attr(node,'name'),paragraph);
    if(['commentRangeStart','commentReference'].includes(node.localName) && paragraph) {
      const note=comments.get(attr(node,'id'));
      if(note)notes.set(paragraph,[...new Set([...(notes.get(paragraph)||[]),note])]);
    }
    for(const child of node.children||[])visit(child,paragraph);
  }
  visit(editor.surface.session.part().root,null);
  const links=pages.map(()=>[]);
  function link(page,element,target,note) {
    const outer=pages[page].getBoundingClientRect(), rect=element.getBoundingClientRect();
    if(rect.width>0 && rect.height>0)links[page].push({Left:(rect.left-outer.left)/outer.width,
      Top:(rect.top-outer.top)/outer.height,Width:rect.width/outer.width,Height:rect.height/outer.height,Page:target??null,Note:note??null});
  }
  for(let i=0;i<pages.length;i++)for(const anchor of pages[i].querySelectorAll('a[href^="#"]')) {
    const name=decodeURIComponent(anchor.getAttribute('href').slice(1));
    const target=pageOf.get(bookmarks.get(name));
    if(target!==undefined)link(i,anchor,target,null);
  }
  for(const [id,text] of notes)for(const {page,element} of fragments.get(id)||[])link(page,element,null,text.join('\n'));
  return {Pages:layout.pages.map(p=>({Width:p.box.width,Height:p.box.height})),
    Headings:editor.getOutline().filter(h=>pageOf.has(h.blockId)).map(h=>({Title:h.text,Page:pageOf.get(h.blockId),Level:Math.max(0,h.level-1)})),Links:links};
}
