(() => {
  'use strict';
  const stage = document.getElementById('stage');
  let expanded = false;
  const expand = document.createElement('button'); expand.id = 'expandPreview'; expand.type = 'button';
  expand.style.cssText = 'position:fixed;right:12px;bottom:12px;width:38px;height:38px;border:1px solid #ffffff70;border-radius:7px;background:#101827b8;color:white;z-index:99;cursor:pointer;opacity:0;transition:opacity .15s;pointer-events:none';
  const expandStyle = document.createElement('style'); expandStyle.textContent = 'body:hover #expandPreview,#expandPreview:focus-visible{opacity:1!important;pointer-events:auto!important}#expandPreview:hover{background:#263c59e8!important}'; document.head.append(expandStyle); document.body.append(expand);
  expand.addEventListener('click', () => window.chrome.webview.postMessage({kind:'expanded',value:!expanded}));
  document.addEventListener('keydown', e => { if(e.key==='Escape' && expanded) { e.preventDefault(); window.chrome.webview.postMessage({kind:'expanded',value:false}); } });
  function setExpanded(value) {
    expanded=value.expanded; expand.title=value.title; expand.setAttribute('aria-label',value.title); expand.setAttribute('aria-pressed',String(expanded));
    expand.innerHTML='<svg width="22" height="22" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8"><path d="'+(expanded?'M3 9h6V3M15 3v6h6M21 15h-6v6M9 21v-6H3':'M9 3H3v6M15 3h6v6M21 15v6h-6M9 21H3v-6')+'"/></svg>';
  }
  let data, time = 0, playing = false, last = 0, nodes = [], lastPost = 0;
  const context = new AudioContext();
  const mix = context.createGain(), meter = context.createAnalyser(); mix.connect(meter).connect(context.destination);
  function resize() {
    if (!data) return;
    const p = data.project, scale = Math.min(innerWidth / p.Width, innerHeight / p.Height);
    stage.style.width = p.Width + 'px'; stage.style.height = p.Height + 'px';
    stage.style.left = (innerWidth - p.Width * scale) / 2 + 'px'; stage.style.top = (innerHeight - p.Height * scale) / 2 + 'px';
    stage.style.transform = `scale(${scale})`;
    stage.style.outline = `${1 / scale}px solid ${data.framingClip ? '#38bdf8' : '#566174'}`;
    stage.style.cursor = data.framingEnabled ? 'grab' : 'default';
  }
  function layout(item) {
    const {clip:c, asset:a, crop, element} = item; if (!crop || !data) return;
    const p=data.project, cw=Math.max(1,Math.floor(a.Width*(1-c.CropLeft-c.CropRight))), ch=Math.max(1,Math.floor(a.Height*(1-c.CropTop-c.CropBottom)));
    const factor=Math.min(p.Width/cw,p.Height/ch)*c.Scale;
    const w=Math.max(2,Math.floor(cw*factor/2+.5)*2), h=Math.max(2,Math.floor(ch*factor/2+.5)*2);
    crop.style.width=w+'px'; crop.style.height=h+'px'; crop.style.transform=`translate(${c.X}px,${c.Y}px) rotate(${c.Rotation}deg)`;
    element.style.width=a.Width*w/cw+'px'; element.style.height=a.Height*h/ch+'px';
    element.style.left=-Math.floor(a.Width*c.CropLeft)*w/cw+'px'; element.style.top=-Math.floor(a.Height*c.CropTop)*h/ch+'px';
  }
  const framing = window.lopataCreateFraming(stage, () => ({data,nodes,time,playing}), layout);
  function sync() {
    if (!data) return;
    for (const {clip: c, track: t, node, media, gainNode} of nodes) {
      const active = !t.Hidden && !t.Muted && time >= c.Start && time < c.Start + c.Duration;
      node.style.display = active ? 'flex' : 'none';
      if (media) {
        const target = c.SourceIn + (c.Freeze ? 0 : Math.max(0, time - c.Start) * c.Speed);
        if (active && !media.seeking && media.readyState >= 1 && Math.abs(media.currentTime - target) > (playing ? .15 : .005)) media.currentTime = target;
        if (active && playing && !c.Freeze) { if (media.paused) media.play().catch(() => {}); } else media.pause();
        let gain = 1; if (c.FadeIn > 0) gain = Math.min(gain, (time - c.Start) / c.FadeIn);
        if (c.FadeOut > 0) gain = Math.min(gain, (c.Start + c.Duration - time) / c.FadeOut);
        media.volume = 1;
        if(gainNode) gainNode.gain.value = active ? Math.max(0,c.Volume*t.Volume*gain) : 0;
      }
      let opacity = c.Opacity;
      if (c.FadeIn > 0) opacity *= Math.max(0, Math.min(1, (time - c.Start) / c.FadeIn));
      if (c.FadeOut > 0) opacity *= Math.max(0, Math.min(1, (c.Start + c.Duration - time) / c.FadeOut));
      node.style.opacity = opacity;
    }
    framing.update();
    const sub = document.getElementById('sub');
    sub.textContent = data.subtitles ? data.project.Narration.filter(n => time >= n.Start && time < n.End).map(n => n.Text).join('\n') : '';
  }
  function frame(now) {
    if (playing && data) { time = Math.min(data.project.Duration, time + Math.min(.25, (now - last) / 1000)); if (time >= data.project.Duration) playing = false; }
    last = now; sync();
    if (now - lastPost > 100) { window.chrome.webview.postMessage(time); lastPost = now; }
    requestAnimationFrame(frame);
  }
  window.lopata = {
    expanded: setExpanded,
    load(next) {
      framing.reset();
      playing = false; nodes.forEach(n => { if(n.gainNode) n.gainNode.disconnect(); if(n.audioSource) n.audioSource.disconnect(); if (n.media) { n.media.pause(); n.media.removeAttribute('src'); n.media.load(); } });
      data = next; time = next.at; stage.replaceChildren(); nodes = [];
      const p = data.project;
      const defs = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); defs.style.cssText = 'position:absolute;width:0;height:0'; stage.append(defs);
      for (const t of p.Tracks) for (const c of [...t.Clips].sort((a,b) => a.Start - b.Start)) {
        const a = p.Assets.find(a => a.Id === c.AssetId); if (!a && !c.Text) continue;
        const node = document.createElement('div'); stage.append(node); let media, gainNode, audioSource, crop;
        const element = document.createElement(c.Text ? 'span' : t.Kind === 'audio' ? 'audio' : a.Kind === 'video' ? 'video' : 'img');
        if (c.Text) { element.textContent = c.Text; element.style.cssText = `color:white;font:${Math.max(16,p.Height/20)}px 'Segoe UI';white-space:pre-wrap;text-align:center;-webkit-text-stroke:1px black;`; node.style.paddingTop = p.Height * .7 + 'px'; }
        else {
          element.crossOrigin = 'anonymous'; element.src = (t.Kind === 'audio' ? data.audioUrls?.[a.Id] : null) || data.urls[a.Id] || '';
          if (t.Kind === 'audio' || a.Kind === 'video') {
            media = element; media.preload = 'auto'; media.muted = t.Kind !== 'audio';
            media.playbackRate = Math.max(.0625, Math.min(16, c.Speed)); media.preservesPitch = true;
            if(t.Kind === 'audio') { audioSource=context.createMediaElementSource(media); gainNode=context.createGain(); audioSource.connect(gainNode).connect(mix); }
          }
          if (t.Kind !== 'audio') {
            crop = document.createElement('section'); crop.style.cssText = 'position:relative;overflow:hidden;flex-shrink:0;pointer-events:none';
            element.style.cssText = 'position:absolute;max-width:none;max-height:none';
            const ns = 'http://www.w3.org/2000/svg', f = document.createElementNS(ns,'filter'); f.id = 'color'+c.Id; f.setAttribute('color-interpolation-filters','sRGB');
            const transfer = document.createElementNS(ns,'feComponentTransfer');
            for (const channel of ['R','G','B']) { const func = document.createElementNS(ns,'feFunc'+channel); func.setAttribute('type','linear'); func.setAttribute('slope',c.Contrast); func.setAttribute('intercept',.5-.5*c.Contrast+c.Brightness); transfer.append(func); }
            const sat = document.createElementNS(ns,'feColorMatrix'); sat.setAttribute('type','saturate'); sat.setAttribute('values',c.Saturation); f.append(transfer,sat); defs.append(f); element.style.filter = `url(#${f.id})`;
            crop.append(element); node.append(crop);
          } else node.append(element);
        }
        if(c.Text) { node.style.transform = `translate(${c.X}px,${c.Y}px) scale(${c.Scale}) rotate(${c.Rotation}deg)`; node.append(element); }
        const item={clip:c,track:t,asset:a,node,media,gainNode,audioSource,crop,element}; nodes.push(item); layout(item);
      }
      const sub = document.createElement('aside'); sub.id='sub'; sub.style.fontSize=Math.max(6,p.Height/30)+'px'; stage.append(sub); resize(); sync();
    },
    seek(value) { time = value; sync(); },
    framing(id,enabled) { if(data.framingClip!==id || !enabled) framing.reset(); data.framingClip=id; data.framingEnabled=enabled; resize(); framing.update(); },
    play(value) { playing = value; if(value) context.resume(); last = performance.now(); sync(); },
    volumes(value) { for (const n of nodes) { if (Object.hasOwn(value.tracks,n.track.Id)) n.track.Volume=value.tracks[n.track.Id]; if (Object.hasOwn(value.clips,n.clip.Id)) n.clip.Volume=value.clips[n.clip.Id]; } sync(); },
    diagnostics() { const samples=new Float32Array(meter.fftSize); meter.getFloatTimeDomainData(samples); return {time,playing,audioState:context.state,audioRms:Math.sqrt(samples.reduce((sum,x)=>sum+x*x,0)/samples.length)}; }
  };
  new ResizeObserver(resize).observe(document.body); requestAnimationFrame(frame);
})();
