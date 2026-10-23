(() => {
  "use strict";
  const E = window.lopataEditor = {}, ns = "http://www.w3.org/2000/svg";
  const stage = document.getElementById("stage"), paper = document.getElementById("paper"), overlay = document.getElementById("overlay");
  Object.assign(E, { ns, stage, paper, overlay, svg: null, sheet: "", selected: [], mode: "select", zoom: 1, panX: 0, panY: 0 });
  let paint = { fill: "#3A9FF9", stroke: "#173448", width: 2 };
  E.drawing = a => {
    if (!a || !/^(?:#[0-9a-fA-F]{6}|none)$/.test(a.fill) || !/^(?:#[0-9a-fA-F]{6}|none)$/.test(a.stroke) || !Number.isFinite(a.width) || a.width < 0)
      throw new TypeError("Invalid drawing paint");
    paint = { fill: a.fill, stroke: a.stroke, width: a.width };
  };
  E.drawPaint = (closed = true) => ({ fill: closed ? paint.fill : "none", stroke: paint.stroke, "stroke-width": paint.width });
  E.send = value => chrome.webview.postMessage({ sheet: E.sheet, ...value });
  E.point = (x,y,element = E.svg) => new DOMPoint(x,y).matrixTransform(element.getScreenCTM().inverse());
  E.find = id => Array.from(E.svg.querySelectorAll("[data-lopata-id]")).find(e => e.dataset.lopataId === id);
  E.elements = () => E.selected.map(E.find).filter(Boolean).filter(e => !E.selected.some(id => { const p=E.find(id); return p!==e && p?.contains(e); }));
  E.make = (tag, attrs = {}) => { const e=document.createElementNS(ns,tag); for(const [k,v] of Object.entries(attrs)) e.setAttribute(k,v); return e; };
  E.id = () => Array.from(crypto.getRandomValues(new Uint8Array(16)),b=>b.toString(16).padStart(2,"0")).join("");
  E.label = el => {const name=el.getAttribute("data-lopata-name");if(name?.startsWith("base64:")){try{return new TextDecoder().decode(Uint8Array.from(atob(name.slice(7)),c=>c.charCodeAt(0)));}catch{}}return name||el.getAttribute("id")||el.localName;};
  window.addEventListener("error",()=>{if(E.svg)E.send({kind:"error",key:"Script"});});
  E.xml = () => new XMLSerializer().serializeToString(E.svg);
  E.screenBox = e => {
    const b=e.getBBox(), m=e.getScreenCTM(), r=stage.getBoundingClientRect();
    const pts=[[b.x,b.y],[b.x+b.width,b.y],[b.x,b.y+b.height],[b.x+b.width,b.y+b.height]].map(p=>new DOMPoint(...p).matrixTransform(m));
    return {x:Math.min(...pts.map(p=>p.x))-r.left,y:Math.min(...pts.map(p=>p.y))-r.top,
      width:Math.max(...pts.map(p=>p.x))-Math.min(...pts.map(p=>p.x)),height:Math.max(...pts.map(p=>p.y))-Math.min(...pts.map(p=>p.y))};
  };
  E.update = () => {
    paper.style.transform="translate("+E.panX+"px,"+E.panY+"px) scale("+E.zoom+")";
    overlay.replaceChildren();
    for(const e of E.elements()){
      try{
        const b=E.screenBox(e); overlay.append(E.make("rect",{...b,class:"box"}));
        if(E.mode==="select") for(const [hx,hy] of [[0,0],[1,0],[0,1],[1,1]]) {
          const h=E.make("rect",{x:b.x+hx*b.width-4,y:b.y+hy*b.height-4,width:8,height:8,class:"handle"});
          h.dataset.resize=e.dataset.lopataId; h.dataset.hx=hx;h.dataset.hy=hy; overlay.append(h);
        }
      }catch{}
    }
    if(E.drawNodes) E.drawNodes();
  };
  E.state = (changed=false,viewportOnly=false) => {
    if(!E.svg)return;
    if(viewportOnly){E.send({kind:"viewport",zoom:E.zoom,panX:E.panX,panY:E.panY});return;}
    const nodes=Array.from(E.svg.querySelectorAll("[data-lopata-id]")).filter(e=>!e.closest("defs")).map(e=>({
      id:e.dataset.lopataId,parent:e.parentElement?.closest("[data-lopata-id]")?.dataset.lopataId||"",
      tag:e.localName,name:E.label(e),
      hidden:e.getAttribute("display")==="none",locked:e.getAttribute("data-lopata-locked")==="true"
    }));
    const selected=E.elements(), first=selected[0]; let bounds=null;
    if(first){const b=E.screenBox(first);const a=E.point(b.x+stage.getBoundingClientRect().left,b.y+stage.getBoundingClientRect().top);
      bounds={x:a.x,y:a.y,width:b.width/E.zoom,height:b.height/E.zoom,fill:first.getAttribute("fill")||"",stroke:first.getAttribute("stroke")||"",opacity:first.getAttribute("opacity")||"1",strokeWidth:first.getAttribute("stroke-width")||"1",
        text:first.localName==="text"?first.textContent:null,fontSize:getComputedStyle(first).fontSize.replace("px",""),fontFamily:getComputedStyle(first).fontFamily};}
    E.send({kind:changed?"commit":"state",svg:changed?E.xml():undefined,nodes,selected:E.selected,bounds,zoom:E.zoom,panX:E.panX,panY:E.panY,mode:E.mode});
  };
  E.select = ids => { E.selected=(ids||[]).filter(id=>E.find(id));E.update();E.state(); };
  E.fit = () => {
    if(!E.svg)return; const b=E.svg.viewBox.baseVal, r=stage.getBoundingClientRect();
    E.zoom=Math.min((r.width-64)/b.width,(r.height-64)/b.height); E.zoom=Math.max(.01,Math.min(128,E.zoom));
    E.panX=(r.width-b.width*E.zoom)/2;E.panY=(r.height-b.height*E.zoom)/2;E.update();E.state();
  };
  E.load = a => {
    E.sheet=a.sheet;E.selected=[];
    const doc=new DOMParser().parseFromString(a.svg,"image/svg+xml");E.svg=document.importNode(doc.documentElement,true);
    const b=E.svg.viewBox.baseVal; paper.style.width=b.width+"px";paper.style.height=b.height+"px";
    paper.replaceChildren(E.svg);E.zoom=a.zoom;E.panX=a.panX;E.panY=a.panY;E.mode="select";
    if(a.fit)E.fit();else{E.update();E.state();}
  };
  E.tool = mode => { E.mode=mode;stage.style.cursor=mode==="hand"?"grab":mode==="select"?"default":"crosshair";E.update();E.state(); };
  E.theme = a => {stage.style.backgroundColor=a.background;};
  E.get = () => ({svg:E.xml(),selected:E.selected});
  E.commit = () => { E.update();E.state(true); };
  E.applyMatrix = (el, matrix) => { el.setAttribute("transform","matrix("+[matrix.a,matrix.b,matrix.c,matrix.d,matrix.e,matrix.f].join(" ")+")"); };
  E.matrix = el => el.transform.baseVal.consolidate()?.matrix || new DOMMatrix();
  const copyMatrix = m => new DOMMatrix([m.a,m.b,m.c,m.d,m.e,m.f]);
  const locked = el => !!el.closest('[data-lopata-locked="true"]');
  let drag=null;
  stage.addEventListener("pointerdown",event=>{
    if(!E.svg || event.button===2)return;stage.focus();
    if(E.mode==="plastic"||E.mode==="nodes"){if(E.plasticDown?.(event))return;}
    const start=E.point(event.clientX,event.clientY);
    if(event.button===1||E.mode==="hand"||event.altKey){drag={kind:"pan",x:event.clientX,y:event.clientY,px:E.panX,py:E.panY};}
    else if(event.target.dataset.resize){
      const el=E.find(event.target.dataset.resize);if(locked(el))return;
      const b=E.screenBox(el),r=stage.getBoundingClientRect();
      // Snapshot numeric matrices: SVGMatrix cannot be passed to the DOMMatrix string constructor.
      drag={kind:"resize",el,base:copyMatrix(E.matrix(el)),parent:copyMatrix(el.parentElement.getScreenCTM()),
        box:{...b,x:b.x+r.left,y:b.y+r.top},x:event.clientX,y:event.clientY,hx:+event.target.dataset.hx,hy:+event.target.dataset.hy};
    } else if(E.mode!=="select"){
      if(E.mode==="text"){E.send({kind:"text",x:start.x,y:start.y});return;}
      const el=E.make(E.mode==="ellipse"?"ellipse":E.mode==="line"||E.mode==="pen"?"path":E.mode==="triangle"||E.mode==="star"?"polygon":"rect",{"data-lopata-id":E.id(),...E.drawPaint(E.mode!=="line"&&E.mode!=="pen")});
      E.svg.append(el); E.selected=[el.dataset.lopataId];drag={kind:"draw",el,start,points:[start],tool:E.mode};
    }else{
      let el=event.target.closest("[data-lopata-id]");
      if(el && !event.ctrlKey) while(el.parentElement!==E.svg && el.parentElement.closest("[data-lopata-id]"))el=el.parentElement.closest("[data-lopata-id]");
      if(!el||locked(el)){E.select([]);return;}
      const id=el.dataset.lopataId;
      if(event.shiftKey) {E.select(E.selected.includes(id)?E.selected.filter(x=>x!==id):[...E.selected,id]);return;}
      if(!E.selected.includes(id))E.select([id]);
      drag={kind:"move",x:event.clientX,y:event.clientY,items:E.elements().map(e=>({el:e,base:E.matrix(e),p:E.point(event.clientX,event.clientY,e.parentElement)}))};
    }
    event.preventDefault();stage.setPointerCapture(event.pointerId);
  });
  stage.addEventListener("pointermove",event=>{
    if(E.plasticMove?.(event))return;
    if(!drag)return;
    if(drag.kind==="pan"){E.panX=drag.px+event.clientX-drag.x;E.panY=drag.py+event.clientY-drag.y;}
    else if(drag.kind==="move") for(const item of drag.items){const p=E.point(event.clientX,event.clientY,item.el.parentElement);E.applyMatrix(item.el,new DOMMatrix().translate(p.x-item.p.x,p.y-item.p.y).multiply(item.base));}
    else if(drag.kind==="resize"){
      const {el,box:b,base,parent,hx,hy}=drag;
      const ox=hx?b.x:b.x+b.width,oy=hy?b.y:b.y+b.height;
      const sx=b.width?1+(event.clientX-drag.x)*(hx?1:-1)/b.width:1;
      const sy=b.height?1+(event.clientY-drag.y)*(hy?1:-1)/b.height:1;
      // Handles surround the screen-aligned box, including rotated/nested objects.
      const scale=new DOMMatrix().translate(ox,oy).scale(sx,sy).translate(-ox,-oy);
      if(Number.isFinite(sx)&&Number.isFinite(sy))E.applyMatrix(el,parent.inverse().multiply(scale).multiply(parent).multiply(base));
    }else if(drag.kind==="draw"){
      const p=E.point(event.clientX,event.clientY),s=drag.start,x=Math.min(s.x,p.x),y=Math.min(s.y,p.y),w=Math.abs(p.x-s.x),h=Math.abs(p.y-s.y),el=drag.el;
      if(drag.tool==="ellipse")for(const[k,v]of Object.entries({cx:x+w/2,cy:y+h/2,rx:w/2,ry:h/2}))el.setAttribute(k,v);
      else if(drag.tool==="line")el.setAttribute("d","M "+s.x+" "+s.y+" L "+p.x+" "+p.y);
      else if(drag.tool==="pen"){drag.points.push(p);el.setAttribute("d",drag.points.map((a,i)=>(i?"L ":"M ")+a.x+" "+a.y).join(" "));}
      else if(drag.tool==="triangle")el.setAttribute("points",[[x+w/2,y],[x+w,y+h],[x,y+h]].map(a=>a.join(",")).join(" "));
      else if(drag.tool==="star"){const pts=[];for(let i=0;i<10;i++){const a=-Math.PI/2+i*Math.PI/5,r=i%2?.4:1;pts.push([x+w/2+Math.cos(a)*w/2*r,y+h/2+Math.sin(a)*h/2*r]);}el.setAttribute("points",pts.map(a=>a.join(",")).join(" "));}
      else for(const[k,v]of Object.entries({x,y,width:w,height:h}))el.setAttribute(k,v);
    }E.update();
  });
  const up=event=>{
    if(E.plasticUp?.(event))return;
    if(!drag)return;const d=drag;drag=null;
    if(stage.hasPointerCapture(event.pointerId))stage.releasePointerCapture(event.pointerId);
    if(d.kind==="pan")E.state(false,true);else E.commit();
  };
  stage.addEventListener("pointerup",up);stage.addEventListener("pointercancel",up);
  stage.addEventListener("wheel",event=>{
    event.preventDefault();if(!E.svg)return;
    const r=stage.getBoundingClientRect(),x=event.clientX-r.left,y=event.clientY-r.top,old=E.zoom;
    E.zoom=Math.max(.01,Math.min(128,old*Math.exp(-event.deltaY*.0015)));
    E.panX=x-(x-E.panX)*E.zoom/old;E.panY=y-(y-E.panY)*E.zoom/old;E.update();E.state(false,true);
  },{passive:false});
  stage.addEventListener("dblclick",event=>{const e=event.target.closest("[data-lopata-id]");if(e)E.select([e.dataset.lopataId]);else E.fit();});
  stage.addEventListener("contextmenu",e=>e.preventDefault());
  stage.addEventListener("keydown",event=>{
    if(event.key==="Escape"){E.tool("select");E.select([]);return;}
    const mod=event.ctrlKey||event.metaKey;
    const command=mod?({z:event.shiftKey?"redo":"undo",y:"redo",c:"copy",x:"cut",v:"paste",s:"save",a:"all"})[event.key.toLowerCase()]:event.key==="Delete"?"delete":null;
    if(command){event.preventDefault();E.send({kind:"shortcut",command});}
    if(event.key.startsWith("Arrow")){event.preventDefault();E.command({name:"move",x:event.key==="ArrowRight"?(event.shiftKey?10:1):event.key==="ArrowLeft"?(event.shiftKey?-10:-1):0,y:event.key==="ArrowDown"?(event.shiftKey?10:1):event.key==="ArrowUp"?(event.shiftKey?-10:-1):0});}
  });
  new ResizeObserver(()=>E.update()).observe(stage);
})();
