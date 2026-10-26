// Pointer gestures stay in canvas coordinates; only completed gestures change the project.
window.lopataCreateFraming = (stage, state, layout) => {
  'use strict';
  let drag, wheelTimer, wheelItem, box;
  const post = value => window.chrome.webview.postMessage(value);
  const commit = item => { const c=item.clip; post({kind:'framing',clipId:c.Id,x:c.X,y:c.Y,scale:c.Scale,rotation:c.Rotation}); };
  const active = n => n.crop && !n.track.Locked && !n.track.Hidden && !n.track.Muted && state().time>=n.clip.Start && state().time<n.clip.Start+n.clip.Duration;
  const selected = () => state().nodes.find(n=>n.clip.Id===state().data?.framingClip && active(n));
  const factor = () => stage.getBoundingClientRect().width/state().data.project.Width;
  function point(e) { const r=stage.getBoundingClientRect(), p=state().data.project, f=factor(); return {x:(e.clientX-r.left)/f-p.Width/2,y:(e.clientY-r.top)/f-p.Height/2}; }
  function hit(n,p) {
    const c=n.clip, a=-c.Rotation*Math.PI/180, x=p.x-c.X, y=p.y-c.Y;
    const lx=x*Math.cos(a)-y*Math.sin(a), ly=x*Math.sin(a)+y*Math.cos(a), w=parseFloat(n.crop.style.width), h=parseFloat(n.crop.style.height);
    if(c.Opacity<=0 || Math.abs(lx)>w/2 || Math.abs(ly)>h/2) return false;
    if(n.element.tagName!=='IMG') return true;
    if(!n.element.complete || !n.element.naturalWidth) return false;
    // Transparent padding in PNG/SVG must not steal clicks from visible layers underneath.
    try {
      if(!n.hitMask) {
        const canvas=document.createElement('canvas'), f=Math.min(1,512/n.asset.Width,512/n.asset.Height);
        canvas.width=Math.max(1,Math.ceil(n.asset.Width*f)); canvas.height=Math.max(1,Math.ceil(n.asset.Height*f));
        const context=canvas.getContext('2d',{willReadFrequently:true}); context.drawImage(n.element,0,0,canvas.width,canvas.height); n.hitMask={canvas,context};
      }
      const cw=Math.max(1,Math.floor(n.asset.Width*(1-c.CropLeft-c.CropRight))), ch=Math.max(1,Math.floor(n.asset.Height*(1-c.CropTop-c.CropBottom)));
      const sx=(lx+w/2)*cw/w+Math.floor(n.asset.Width*c.CropLeft), sy=(ly+h/2)*ch/h+Math.floor(n.asset.Height*c.CropTop);
      const mask=n.hitMask, px=Math.max(0,Math.min(mask.canvas.width-1,Math.floor(sx*mask.canvas.width/n.asset.Width))), py=Math.max(0,Math.min(mask.canvas.height-1,Math.floor(sy*mask.canvas.height/n.asset.Height)));
      return mask.context.getImageData(px,py,1,1).data[3]>16;
    } catch { return true; }
  }
  function flushWheel() { clearTimeout(wheelTimer); if(wheelItem) { commit(wheelItem); wheelItem=null; post({kind:'framingGesture',value:false}); } }
  function update() {
    if(!box?.isConnected) {
      box=document.createElement('section'); box.id='framingBox'; box.style.cssText='position:absolute;pointer-events:none;box-sizing:border-box;transform-origin:center;z-index:20';
      for(const [key,x,y] of [['nw',0,0],['ne',100,0],['sw',0,100],['se',100,100],['rotate',50,0]]) {
        const h=document.createElement('button'); h.type='button'; h.dataset.frameHandle=key;
        h.title=state().data?.framingLabels?.[key==='rotate'?'rotate':'resize']||key;
        h.setAttribute('aria-label',h.title); h.style.cssText=`position:absolute;left:${x}%;top:${y}%;padding:0;pointer-events:auto;background:white;border:1px solid #0284c7;box-sizing:border-box;cursor:${key==='rotate'?'crosshair':key==='nw'||key==='se'?'nwse-resize':'nesw-resize'}`;
        box.append(h);
      }
      stage.append(box);
    }
    const item=selected(); box.style.display=item&&!state().playing?'block':'none'; if(!item) return;
    const c=item.clip, f=factor(), w=parseFloat(item.crop.style.width), h=parseFloat(item.crop.style.height);
    box.dataset.clipId=c.Id; box.style.width=w+'px'; box.style.height=h+'px'; box.style.left=(state().data.project.Width/2+c.X-w/2)+'px'; box.style.top=(state().data.project.Height/2+c.Y-h/2)+'px'; box.style.transform=`rotate(${c.Rotation}deg)`;
    box.style.border=`${1/f}px solid #38bdf8`;
    for(const handle of box.children) {
      const rotation=handle.dataset.frameHandle==='rotate'; handle.style.width=handle.style.height=(rotation?16:12)/f+'px'; handle.style.borderRadius=rotation?'50%':'2px';
      handle.style.transform=`translate(-50%,${rotation?-36/f+'px':'-50%'})`;
    }
  }
  stage.addEventListener('pointerdown',e=>{
    const s=state(); if(!s.data?.framingEnabled || s.playing || e.button!==0) return;
    flushWheel(); let item=selected(); const handle=e.target.dataset?.frameHandle; const p=point(e);
    if(!handle) {
      const target=[...s.nodes].reverse().find(n=>active(n)&&hit(n,p));
      if(target && target!==item) { item=target; s.data.framingClip=item.clip.Id; post({kind:'framingSelect',clipId:item.clip.Id}); }
    }
    if(!item) return;
    const c=item.clip;
    drag={item,mode:handle==='rotate'?'rotate':handle?'resize':'move',p,x:c.X,y:c.Y,scale:c.Scale,rotation:c.Rotation};
    drag.radius=Math.hypot(p.x-c.X,p.y-c.Y); drag.angle=Math.atan2(p.y-c.Y,p.x-c.X);
    stage.setPointerCapture(e.pointerId); post({kind:'framingGesture',value:true}); e.preventDefault(); update();
  });
  stage.addEventListener('pointermove',e=>{
    if(!drag) return; const p=point(e), c=drag.item.clip;
    if(drag.mode==='move') { c.X=drag.x+p.x-drag.p.x; c.Y=drag.y+p.y-drag.p.y; }
    else if(drag.mode==='resize') c.Scale=Math.max(.01,Math.min(20,drag.scale*Math.hypot(p.x-drag.x,p.y-drag.y)/Math.max(.001,drag.radius)));
    else {
      const angle=Math.atan2(p.y-drag.y,p.x-drag.x)-drag.angle;
      c.Rotation=drag.rotation+Math.atan2(Math.sin(angle),Math.cos(angle))*180/Math.PI;
      if(e.shiftKey) c.Rotation=Math.round(c.Rotation/15)*15;
    }
    layout(drag.item); update();
  });
  function finish(e,cancel=false) {
    if(!drag) return; const d=drag; drag=null;
    if(cancel) { Object.assign(d.item.clip,{X:d.x,Y:d.y,Scale:d.scale,Rotation:d.rotation}); layout(d.item); }
    else if(d.item.clip.X!==d.x||d.item.clip.Y!==d.y||d.item.clip.Scale!==d.scale||d.item.clip.Rotation!==d.rotation) commit(d.item);
    if(e?.pointerId!==undefined && stage.hasPointerCapture(e.pointerId)) stage.releasePointerCapture(e.pointerId);
    post({kind:'framingGesture',value:false}); update();
  }
  stage.addEventListener('pointerup',e=>finish(e)); stage.addEventListener('pointercancel',e=>finish(e,true)); stage.addEventListener('lostpointercapture',e=>finish(e,true));
  document.addEventListener('keydown',e=>{ if(e.key==='Escape' && drag) { e.preventDefault(); e.stopImmediatePropagation(); finish(null,true); } },true);
  stage.addEventListener('wheel',e=>{
    const item=selected(); if(!item||state().playing||drag) return; e.preventDefault();
    const c=item.clip,p=point(e),next=Math.max(.01,Math.min(20,c.Scale*Math.pow(1.12,-e.deltaY/100))),ratio=next/c.Scale;
    c.X=p.x-(p.x-c.X)*ratio; c.Y=p.y-(p.y-c.Y)*ratio; c.Scale=next; layout(item); update();
    post({kind:'framingGesture',value:true}); wheelItem=item; clearTimeout(wheelTimer); wheelTimer=setTimeout(flushWheel,250);
  },{passive:false});
  // A reload may be an undo or a different project: never commit stale browser state to it.
  return {update,reset() { finish(null,true); clearTimeout(wheelTimer); if(wheelItem) post({kind:'framingGesture',value:false}); wheelItem=null; box?.remove(); box=null; }};
};
