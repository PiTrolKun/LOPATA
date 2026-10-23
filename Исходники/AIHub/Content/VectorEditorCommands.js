(() => {
  "use strict"; const E=window.lopataEditor;
  E.fragment=()=>{
    const selected=E.elements();if(!selected.length)return null;
    const clone=E.svg.cloneNode(true),keep=new Set(selected.map(e=>e.dataset.lopataId));
    function prune(el){
      for(const child of Array.from(el.children)){
        if(["defs","metadata","title","desc"].includes(child.localName))continue;
        if(keep.has(child.dataset.lopataId))continue;
        if(Array.from(child.querySelectorAll("[data-lopata-id]")).some(e=>keep.has(e.dataset.lopataId)))prune(child);else child.remove();
      }
    }
    prune(clone); return new XMLSerializer().serializeToString(clone);
  };
  const transform=(el,m)=>E.applyMatrix(el,m.multiply(E.matrix(el)));
  const around=(el,fn)=>{const b=el.getBBox(),m=E.matrix(el);E.applyMatrix(el,m.translate(b.x+b.width/2,b.y+b.height/2).multiply(fn).translate(-b.x-b.width/2,-b.y-b.height/2));};
  const colors=new RegExp("^(?:#[0-9a-fA-F]{3,8}|none|transparent|[a-zA-Z]{1,24})$");
  E.command=a=>{
    if(!E.svg)return;
    const list=E.elements().filter(e=>!e.closest('[data-lopata-locked="true"]'));
    switch(a.name){
      case "all":E.select(Array.from(E.svg.children).filter(e=>e.dataset.lopataId).map(e=>e.dataset.lopataId));return;
      case "fragment":return E.fragment();
      case "delete":list.forEach(e=>e.remove());E.selected=[];break;
      case "duplicate":E.send({kind:"duplicate",svg:E.fragment()});return;
      case "move":list.forEach(e=>transform(e,new DOMMatrix().translate(+a.x||0,+a.y||0)));break;
      case "rotate":list.forEach(e=>around(e,new DOMMatrix().rotate(+a.value||0)));break;
      case "flipX":case "flipY":list.forEach(e=>around(e,new DOMMatrix().scale(a.name==="flipX"?-1:1,a.name==="flipY"?-1:1)));break;
      case "bounds":{
        for(const el of list){const b=E.screenBox(el),r=E.stage.getBoundingClientRect(),p=E.point(b.x+r.left,b.y+r.top,el.parentElement);
          if(a.key==="x"||a.key==="y"){
            const world=E.point(b.x+r.left,b.y+r.top),destination=new DOMPoint(a.key==="x"?+a.value:world.x,a.key==="y"?+a.value:world.y);
            const screen=destination.matrixTransform(E.svg.getScreenCTM()),local=E.point(screen.x,screen.y,el.parentElement);
            transform(el,new DOMMatrix().translate(local.x-p.x,local.y-p.y));
          } else {const size=a.key==="width"?b.width/E.zoom:b.height/E.zoom;if(size>0&&+a.value>0)around(el,new DOMMatrix().scale(a.key==="width"?+a.value/size:1,a.key==="height"?+a.value/size:1));}
        }break;
      }
      case "style":{
        if(!["fill","stroke","opacity","stroke-width","font-size","font-family"].includes(a.key))return;
        if(a.key==="font-family"&&/[<>{};\r\n\\]/.test(a.value))return;
        if(["fill","stroke"].includes(a.key)&&!colors.test(a.value))return;
        if(["opacity","stroke-width","font-size"].includes(a.key)&&(!Number.isFinite(+a.value)||+a.value<0||a.key==="opacity"&&+a.value>1))return;
        for(const el of list){
          el.setAttribute(a.key,a.value);el.style.removeProperty(a.key);
          // A compound object may have explicit paint on its descendants. Recolor its actual shapes too.
          if(a.key==="fill"||a.key==="stroke")for(const child of el.querySelectorAll("path,rect,circle,ellipse,line,polyline,polygon,text")) {
            if(child.closest("defs,clipPath,mask,filter")||child.closest('[data-lopata-locked="true"]'))continue;child.setAttribute(a.key,a.value);child.style.removeProperty(a.key);
          }
        }break;
      }
      case "text":{const el=E.make("text",{x:a.x,y:a.y,"font-size":48,...E.drawPaint(),"data-lopata-id":E.id()});el.textContent=a.value;E.svg.append(el);E.selected=[el.dataset.lopataId];break;}
      case "textContent":for(const el of list)if(el.localName==="text")el.textContent=a.value;break;
      case "rename":if(list[0])list[0].setAttribute("data-lopata-name","base64:"+btoa(Array.from(new TextEncoder().encode(a.value),b=>String.fromCharCode(b)).join("")));break;
      case "hide":{const el=E.find(a.id);if(el)el.getAttribute("display")==="none"?el.removeAttribute("display"):el.setAttribute("display","none");break;}
      case "lock":{const el=E.find(a.id);if(el)el.setAttribute("data-lopata-locked",el.getAttribute("data-lopata-locked")==="true"?"false":"true");break;}
      case "front":for(const e of list)e.parentElement.append(e);break;
      case "back":for(const e of [...list].reverse())e.parentElement.prepend(e);break;
      case "group":{
        if(list.length<2)return;const parent=list[0].parentElement;
        if(list.some(e=>e.parentElement!==parent)){E.send({kind:"error",key:"SameParent"});return;}
        const group=E.make("g",{"data-lopata-id":E.id()});parent.insertBefore(group,list[0]);for(const e of list)group.append(e);E.selected=[group.dataset.lopataId];break;
      }
      case "ungroup":{
        const ids=[];
        for(const e of list){
          if(e.localName!=="g"||["filter","clip-path","mask","opacity","style"].some(k=>e.hasAttribute(k))){E.send({kind:"error",key:"Ungroup"});return;}
          for(const c of Array.from(e.children)){
            if(c instanceof SVGGraphicsElement){
              E.applyMatrix(c,E.matrix(e).multiply(E.matrix(c)));
              for(const k of ["fill","stroke","stroke-width","fill-rule","font-family","font-size"])if(e.hasAttribute(k)&&!c.hasAttribute(k))c.setAttribute(k,e.getAttribute(k));
              ids.push(c.dataset.lopataId);
            }
            e.parentElement.insertBefore(c,e);
          } e.remove();
        }E.selected=ids.filter(Boolean);break;
      }
      case "canvas":{
        if(!Number.isFinite(+a.width)||!Number.isFinite(+a.height)||+a.width<=0||+a.height<=0||+a.width>1e9||+a.height>1e9)return;
        E.svg.setAttribute("viewBox","0 0 "+a.width+" "+a.height);E.paper.style.width=a.width+"px";E.paper.style.height=a.height+"px";break;
      }
      default:return;
    }E.commit();
  };
})();
