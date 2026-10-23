(() => {
  "use strict"; const E=window.lopataEditor;
  let drag=null,radius=90,strength=.65;
  const points=el=>{
    const tokens=(el.getAttribute("d")||"").match(/[MLZmlz]|[-+]?(?:\d*\.?\d+)(?:[eE][-+]?\d+)?/g)||[];
    const out=[];let i=0,cmd="";
    while(i<tokens.length){if(/[MLZ]/i.test(tokens[i]))cmd=tokens[i++].toUpperCase();
      if(cmd==="Z"){out.push({z:true});cmd="";continue;}
      if(cmd!=="M"&&cmd!=="L")break;
      if(i+1>=tokens.length)break;out.push({x:+tokens[i++],y:+tokens[i++],m:cmd==="M"});cmd="L";
    }return out;
  };
  const write=(el,ps)=>el.setAttribute("d",ps.map(p=>p.z?"Z":(p.m?"M ":"L ")+p.x+" "+p.y).join(" "));
  const paths=()=>E.elements().flatMap(e=>e.matches("path[data-lopata-plastic]")?[e]:Array.from(e.querySelectorAll("path[data-lopata-plastic]"))).filter(e=>!e.closest('[data-lopata-locked="true"]'));
  E.plastic=a=>{radius=Math.max(1,+a.radius||90);strength=Math.max(.01,Math.min(1,+a.strength||.65));E.tool(a.mode||"plastic");};
  E.drawNodes=()=>{
    if(E.mode!=="nodes")return;
    for(const el of paths()){
      const ps=points(el),m=el.getScreenCTM(),r=E.stage.getBoundingClientRect();
      ps.forEach((p,index)=>{if(p.z)return;const s=new DOMPoint(p.x,p.y).matrixTransform(m);
        const n=E.make("circle",{cx:s.x-r.left,cy:s.y-r.top,r:3,class:"node"});n.dataset.node=index;n.dataset.path=el.dataset.lopataId;E.overlay.append(n);});
    }
  };
  E.plasticDown=event=>{
    if(!["plastic","nodes"].includes(E.mode))return false;
    if(E.mode==="nodes"){
      if(!event.target.dataset.path)return true;
      const el=E.find(event.target.dataset.path);drag={node:+event.target.dataset.node,el,ps:points(el)};
    }else{
      const list=paths();if(!list.length)return true;
      drag={x:event.clientX,y:event.clientY,items:list.map(el=>({el,ps:points(el),start:E.point(event.clientX,event.clientY,el)}))};
    }
    event.preventDefault();E.stage.setPointerCapture(event.pointerId);return true;
  };
  E.plasticMove=event=>{
    if(!drag)return false;
    if(drag.el){const p=E.point(event.clientX,event.clientY,drag.el);drag.ps[drag.node]={...drag.ps[drag.node],x:p.x,y:p.y};write(drag.el,drag.ps);}
    else for(const item of drag.items){
      const to=E.point(event.clientX,event.clientY,item.el),matrix=item.el.getScreenCTM(),scale=Math.sqrt(Math.abs(matrix.a*matrix.d-matrix.b*matrix.c))||1;
      const localRadius=radius*E.zoom/scale,dx=(to.x-item.start.x)*strength,dy=(to.y-item.start.y)*strength;
      write(item.el,item.ps.map(p=>{if(p.z)return p;const distance=Math.hypot(p.x-item.start.x,p.y-item.start.y),falloff=Math.max(0,1-distance/localRadius);
        return {...p,x:p.x+dx*falloff*falloff,y:p.y+dy*falloff*falloff};}));
    }E.update();return true;
  };
  E.plasticUp=event=>{
    if(!drag)return false;drag=null;if(E.stage.hasPointerCapture(event.pointerId))E.stage.releasePointerCapture(event.pointerId);E.commit();return true;
  };
})();
