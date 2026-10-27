using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIHub.Services.VideoPreparation;

internal static class VideoStoryboardHtml
{
    public static string Create(IReadOnlyList<VideoTransferFrame> frames, string title, string? audio, OutputProvenance provenance)
    {
        var data = JsonSerializer.Serialize(new { frames, audio }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'>"
            + "<meta name='lopata.provenance' content='" + HtmlEncoder.Default.Encode(provenance.Json()) + "'><title>" + HtmlEncoder.Default.Encode(title) + "</title>" + """
            <style>body{margin:0;background:#101827;color:#edf2fa;font:17px system-ui}main{max-width:1100px;margin:auto;padding:24px}h1{font-size:24px}#stage{height:52vh;background:#080c14;display:flex;align-items:center;justify-content:center}img{max-width:100%;max-height:100%;object-fit:contain}button,select{font:inherit;padding:10px;background:#22334d;color:inherit;border:1px solid #7386a0;border-radius:7px}input{width:100%;margin:18px 0}p{white-space:pre-wrap}audio{width:100%;margin-top:15px}#note{opacity:.8}section{display:flex;gap:12px;flex-wrap:wrap}</style>
            <main><h1 id="heading"></h1><div id="stage"><img id="picture" alt=""><span id="empty">Text-only shot / План без изображения</span></div>
            <h2 id="shot"></h2><p id="action"></p><p id="note"></p><section><button id="play">▶ Play / Смотреть</button><button id="stop">■ Stop / Стоп</button><select id="select" aria-label="Shot / План"></select><span id="time"></span></section>
            <input id="seek" type="range" step=".05" min="0" value="0" aria-label="Time / Время"><audio id="audio" controls></audio><p>LOPATA · Still storyboard / Неподвижная раскадровка. No video generation / Без генерации движения.</p></main>
            <script>
            const data=
            """ + data + ";document.getElementById('heading').textContent=" + JsonSerializer.Serialize(title) + ";" + """
            const frames=data.frames, duration=frames.length?frames[frames.length-1].end:0;
            const image=document.getElementById('picture'),empty=document.getElementById('empty'),audio=document.getElementById('audio'),seek=document.getElementById('seek'),select=document.getElementById('select'),play=document.getElementById('play');
            let running=false, position=0, mark=0, current=-1, soundFailed=false, runGeneration=0;
            image.onerror=()=>{image.hidden=true;empty.hidden=false;empty.textContent='Image unavailable / Изображение недоступно';};
            if(data.audio){audio.src=data.audio;}else audio.hidden=true;
            frames.forEach((f,i)=>{const o=document.createElement('option');o.value=i;o.textContent=(i+1)+'. '+f.name;select.appendChild(o);});seek.max=duration;
            function show(){let i=frames.findIndex(f=>position<f.end);if(i<0)i=frames.length-1;const f=frames[i];if(!f)return;
            if(current!==f.order){current=f.order;image.hidden=!f.image;empty.hidden=!!f.image;empty.textContent='Text-only shot / План без изображения';if(f.image)image.src=f.image;
            document.getElementById('shot').textContent=f.scene+' · '+f.name;document.getElementById('action').textContent=f.prompt;document.getElementById('note').textContent=f.notes;select.value=String(f.order-1);}
            seek.value=position;document.getElementById('time').textContent=position.toFixed(1)+' / '+duration.toFixed(1)+' s';}
            function pause(){runGeneration++;running=false;audio.pause();play.textContent='▶ Play / Смотреть';}
            async function start(){const generation=++runGeneration;if(position>=duration)position=0;running=true;play.textContent='…';
            if(data.audio&&!soundFailed){try{audio.currentTime=position;await audio.play();}catch(e){soundFailed=true;}}
            if(generation!==runGeneration){if(!running)audio.pause();return;}mark=performance.now()-position*1000;play.textContent='Ⅱ Pause / Пауза';tick();}
            function tick(){if(!running)return;position=Math.min(duration,(performance.now()-mark)/1000);show();if(position>=duration){pause();return;}requestAnimationFrame(tick);}
            function set(value){position=Math.max(0,Math.min(duration,value));mark=performance.now()-position*1000;if(data.audio&&!soundFailed&&position<audio.duration)audio.currentTime=position;show();}
            play.onclick=()=>running?pause():start();document.getElementById('stop').onclick=()=>{pause();set(0);};seek.oninput=()=>set(Number(seek.value));select.onchange=()=>set(frames[Number(select.value)].start);show();
            </script></html>
            """;
    }
}
