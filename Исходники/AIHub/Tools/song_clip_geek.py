"""Geekatplay: beat analysis -> N+1 SDXL cards -> H3 FL2VA -> original song.

No web server, auto-downloader or second diffusion implementation. The author
builds both inference graphs; the desktop executes their approved nodes and owns
atomic PNG/MP4 commits. The bundled PyAV encoder replaces UI video-save nodes.
"""
import faulthandler
from fractions import Fraction
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_motion_state import atomic_json, canonical, contained, file_hash, operation_lock, reject_links
from song_clip_geek_state import GeekState, object_hash
from song_clip_geek_native import NativeGeek, load_author
from song_clip_montage_media import join_video, scale_video

REVISION = '0482233d865c23e888e3046c592c40cf61fd5c57'
MODEL = 'geekatplay_music_video'
FPS = 24


def emit(kind, **data):
    print('@motion ' + json.dumps(dict(kind=kind, **data), ensure_ascii=False, allow_nan=False), flush=True)


def validate(request):
    if request.get('geekRevision') != REVISION or request.get('modelId') != MODEL:
        raise ValueError('Invalid Geekatplay revision or model.')
    for name in ['libraries', 'analysisLibraries', 'comfy', 'geekSource', 'sdxl', 'song', 'ffmpeg', 'ffprobe', 'job']:
        path = Path(request[name])
        if not path.is_absolute(): raise ValueError('An absolute owned component/input path is required.')
        reject_links(path)
        if not path.exists(): raise FileNotFoundError(name)
    for key, low, high in [('width', 256, 1536), ('height', 256, 1536), ('steps', 8, 50),
                            ('cardSteps', 8, 40), ('clipSeconds', 5, 10), ('seed', 0, 2**53-1),
                            ('outputWidth', 256, 3840), ('outputHeight', 256, 2160), ('quality', 1, 100)]:
        if type(request.get(key)) is not int or not low <= request[key] <= high:
            raise ValueError('Invalid Geekatplay setting: ' + key)
    if request['width'] % 32 or request['height'] % 32 or request['outputWidth'] % 2 or request['outputHeight'] % 2:
        raise ValueError('Invalid Geekatplay canvas grid.')
    if not isinstance(request.get('pins'), dict) or not request['pins']:
        raise ValueError('The audited model/runtime pins are required.')
    strength = request.get('referenceStrength')
    if isinstance(strength, bool) or not isinstance(strength, (int, float)) or not math.isfinite(strength) or not 0 <= strength <= 1:
        raise ValueError('Invalid reference strength.')
    refs = request.get('references', [])
    if not isinstance(refs, list) or len(refs) > 9: raise ValueError('Invalid references.')
    project = Path(request['job']).resolve().parent.parent
    for ref in refs:
        path = Path(ref['file']); reject_links(path)
        if (ref.get('kind') not in ('performer', 'world', 'object') or not ref.get('description', '').strip()
                or not path.is_file() or not path.resolve().is_relative_to(project)):
            raise ValueError('Reference must be a typed owned project image.')


def identity(request, plan=True):
    data = dict(request)
    if not plan: data.pop('visualPlan', None)
    return {'request': object_hash(data), 'song': file_hash(request['song']),
            'references': [file_hash(ref['file']) for ref in request.get('references', [])],
            'source': file_hash(Path(request['geekSource']) / 'nodes.py'),
            'adapters': {name: file_hash(Path(__file__).parent / name) for name in
                ('song_clip_geek.py', 'song_clip_geek_native.py', 'song_clip_geek_state.py',
                 'song_clip_motion_state.py', 'song_clip_montage_media.py', 'h3_memory.py')}}


def duration(request):
    result = subprocess.run([request['ffprobe'], '-v', 'error', '-show_entries', 'format=duration',
        '-of', 'json', request['song']], capture_output=True, text=True, check=True,
        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    seconds = float(json.loads(result.stdout)['format']['duration'])
    if not math.isfinite(seconds) or not 0 < seconds <= 7200: raise ValueError('Invalid song duration.')
    return seconds


def analyze(request):
    job = Path(request['job'])
    expected = identity(request, plan=False)
    path = contained(job, 'geek-analysis.json')
    if path.exists():
        data = json.loads(path.read_text(encoding='utf-8'))
        if data.get('identity') != expected: raise ValueError('Geekatplay analysis input changed.')
        return data
    os.environ['NUMBA_CACHE_DIR'] = str(contained(job, 'numba-cache'))
    os.environ['PATH'] = str(Path(request['ffprobe']).parent) + os.pathsep + os.environ.get('PATH', '')
    sys.path[:0] = [request['analysisLibraries'], request['libraries']]
    emit('stage', stage='geek-analysis', fraction=.01)
    author = load_author(request)
    board = json.loads(author.AudioLyricsSegmenter().segment_audio(request['clipSeconds'],
        'none (auto-split by duration & custom lyrics)', audio_path=request['song'], custom_lyrics='', force_rerun=False)[0])
    seconds = duration(request)
    segments = board.get('segments', [])
    if not 1 <= len(segments) <= 256: raise ValueError('The full song exceeds the supported storyboard size.')
    # Quantize beat boundaries once to the actual 24 fps. Preserve the final tail,
    # including sub-centisecond source duration rounded in upstream's UI JSON.
    bounds = [0] + [round(float(seg['start_time']) * FPS) for seg in segments[1:]] + [math.ceil(seconds * FPS)]
    if any(b <= a for a, b in zip(bounds, bounds[1:])): raise ValueError('Invalid beat-aligned segment order.')
    slots = []
    for index, (a, b) in enumerate(zip(bounds, bounds[1:]), 1):
        if (b - a) / FPS > 15: raise ValueError('A Geekatplay segment exceeds the H3 clip window.')
        slots.append(dict(index=index, startSeconds=a/FPS, endSeconds=b/FPS, frames=b-a))
    data = dict(schema=1, identity=expected, durationSeconds=seconds, slots=slots, storyboard=board)
    atomic_json(path, data)
    emit('analysis', slots=slots, fraction=.04)
    return data


def validate_plan(request, analysis):
    plan = request.get('visualPlan')
    slots = analysis['slots']
    if (not isinstance(plan, dict) or plan.get('version') != 1 or not plan.get('globalStyle', '').strip()
            or not isinstance(plan.get('scenes'), list) or len(plan['scenes']) != len(slots)
            or not plan.get('assistants')):
        raise ValueError('A complete approved Qwen scene plan is required.')
    for scene, slot in zip(plan['scenes'], slots):
        if (scene.get('index') != slot['index'] or abs(scene['startSeconds'] - slot['startSeconds']) > .05
                or abs(scene['endSeconds'] - min(analysis['durationSeconds'], slot['endSeconds'])) > .05
                or not scene.get('prompt', '').strip()):
            raise ValueError('The scene plan does not match the accepted music analysis.')
    roles = plan.get('referenceRoles') or []
    if len(roles) != len(request.get('references', [])): raise ValueError('Missing reference role translation.')
    for index, (role, ref) in enumerate(zip(roles, request.get('references', [])), 1):
        if role.get('index') != index or role.get('kind') != ref['kind'] or not role.get('description', '').strip():
            raise ValueError('Reference role mismatch.')
    return plan


def card_spec(request, plan, index):
    scenes = plan['scenes']; scene = scenes[min(index, len(scenes)-1)]
    text = plan['globalStyle'] + '\n' + scene['prompt']
    reference = None
    roles = plan.get('referenceRoles') or []
    for offset, role in enumerate(roles, 1):
        if re.search(r'<Subject\s+' + str(offset) + r'>', scene['prompt'], re.I) and reference is None:
            reference = request['references'][offset-1]
        text = re.sub(r'<Subject\s+' + str(offset) + r'>', role['description'], text, flags=re.I)
    # SDXL receives a visible photographic moment, not a multi-action video script.
    cue = 'Opening cinematic keyframe. ' if index < len(scenes) else 'Closing cinematic keyframe showing the completed final action. '
    return cue + text + '\nSingle coherent image, no text or reference-sheet panels.', reference


def facts(request, plan, index=None, stage='transitions'):
    return dict(program='LOPATA', programVersion=request['programVersion'], scenario='song-to-clip/geekatplay',
        operation=Path(request['job']).name,
        createdUtc=__import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat(),
        models={key: value for key, value in request['pins'].items() if key in
            ({'song-clip-glitch-sdxl'} if stage == 'cards' else {'song-clip-glitch-sdxl', 'song-clip-geek-models', 'song-clip-geek-shared-models'})},
        components=request['pins'], assistants=plan['assistants'], stage=stage, index=index)


def save_png(tensor, path, facts):
    from PIL import Image, PngImagePlugin
    import numpy as np
    values = tensor[0].numpy()
    if not np.isfinite(values).all(): raise ValueError('A generated keyframe contains non-finite pixels.')
    pixels = np.clip(values * 255, 0, 255).astype('uint8')
    tags = PngImagePlugin.PngInfo(); tags.add_text('LOPATA', canonical(facts))
    temporary = path.with_suffix('.installing.png')
    path.parent.mkdir(exist_ok=True)
    Image.fromarray(pixels).save(temporary, pnginfo=tags)
    os.replace(temporary, path)


def encode_shot(tensor, target, count, width, height, provenance):
    import av
    import numpy as np
    if len(tensor) < 1: raise ValueError('No decoded H3 frames.')
    if not np.isfinite(tensor.numpy()).all(): raise ValueError('H3 decoded non-finite pixels.')
    temporary = target.with_suffix('.installing.mp4'); target.parent.mkdir(exist_ok=True)
    with av.open(str(temporary), 'w', format='mp4', options={'movflags': '+faststart+use_metadata_tags'}) as output:
        output.metadata.update(encoded_by='LOPATA ' + provenance['programVersion'], LOPATA_VIDEO=canonical(provenance))
        stream = output.add_stream('libx264', rate=FPS)
        stream.width, stream.height, stream.pix_fmt = width, height, 'yuv420p'
        stream.time_base = stream.codec_context.time_base = Fraction(1, FPS)
        stream.options = {'crf': '18', 'preset': 'medium'}
        for index in range(count):
            pixels = np.clip(tensor[min(index, len(tensor)-1)].numpy() * 255, 0, 255).astype('uint8')
            frame = av.VideoFrame.from_ndarray(pixels, format='rgb24')
            frame.pts, frame.time_base = index, Fraction(1, FPS)
            for packet in stream.encode(frame): output.mux(packet)
        for packet in stream.encode(): output.mux(packet)
    with av.open(str(temporary)) as check:
        if sum(1 for _ in check.decode(video=0)) != count: raise ValueError('Incomplete encoded H3 segment.')
    os.replace(temporary, target)


def last_encoded_frame(path, expected_count, torch):
    # The next shot follows the frame that survived MP4 encoding, not a raw
    # tensor beyond the final trim point. No new image model is involved.
    import av
    import numpy as np
    final, count = None, 0
    with av.open(str(path)) as source:
        for frame in source.decode(video=0):
            final = frame.to_ndarray(format='rgb24'); count += 1
    if count != expected_count or final is None: raise ValueError('Invalid handoff frame sequence.')
    return torch.from_numpy(final.astype(np.float32) / 255)[None]


def run(request, action):
    validate(request)
    job = Path(request['job'])
    with operation_lock(job):
        analysis = analyze(request)
        if action == '--analyze': return
        plan = validate_plan(request, analysis)
        state = GeekState(job, identity(request))
        slots = analysis['slots']; count = len(slots)
        state.validate_counts(count+1, count)
        native = None
        for index in range(count+1):
            prompt, ref = card_spec(request, plan, index)
            expected = object_hash(dict(prompt=prompt, reference=file_hash(ref['file']) if ref else None, index=index))
            if state.accepted('cards', index, expected): continue
            if action == '--verify': raise ValueError('Missing accepted storyboard card.')
            native = native or NativeGeek(request, job, emit)
            emit('stage', stage='geek-cards', fraction=.1+.22*index/(count+1))
            image = native.card(prompt, ref, index, count+1)
            relative = f'cards/card_{index:03d}.png'; path = contained(job, relative)
            save_png(image, path, facts(request, plan, index, stage='cards'))
            state.commit('cards', index, expected, [relative]); del image
        if action == '--cards': return
        for index, slot in enumerate(slots):
            first = f'cards/card_{index:03d}.png' if index == 0 else f'clips/handoff_{index-1:03d}.png'
            last = f'cards/card_{index+1:03d}.png'
            prompt = plan['globalStyle'] + '\n' + plan['scenes'][index]['prompt']
            for offset, role in enumerate(plan.get('referenceRoles') or [], 1):
                prompt = re.sub(r'<Subject\s+' + str(offset) + r'>', role['description'], prompt, flags=re.I)
            expected = object_hash(dict(index=index, slot=slot, prompt=prompt, first=file_hash(contained(job, first)), last=file_hash(contained(job,last))))
            if state.accepted('clips', index, expected):
                emit('cached', index=index+1, total=count, fraction=.33+.58*(index+1)/count); continue
            if action == '--verify': raise ValueError('Missing accepted video segment.')
            native = native or NativeGeek(request, job, emit)
            emit('chunk-start', index=index+1, total=count, fraction=.33+.58*index/count)
            decoded = native.segment(contained(job,first), contained(job,last), prompt, slot['frames']/FPS, index, count)
            video = f'clips/clip_{index:03d}.mp4'; handoff = f'clips/handoff_{index:03d}.png'
            encode_shot(decoded, contained(job,video), slot['frames'], request['width'], request['height'], facts(request, plan, index))
            final = last_encoded_frame(contained(job,video), slot['frames'], native.torch)
            save_png(final, contained(job,handoff), facts(request, plan, index))
            state.commit('clips', index, expected, [video,handoff], frames=slot['frames'], first=first, last=last)
            del decoded, final
            emit('chunk', index=index+1, total=count, fraction=.33+.58*(index+1)/count)
        if native is not None: native.reset()
        output = contained(job,'geek-raw.mp4'); receipt = contained(job,'worker-result.json')
        if receipt.exists():
            saved = json.loads(receipt.read_text(encoding='utf-8'))
            if saved.get('identity') != state.identity or saved.get('sha256') != file_hash(output):
                raise ValueError('Completed Geekatplay result changed.')
            emit('complete', fraction=.98); return
        if action == '--verify': raise ValueError('Missing completed Geekatplay result.')
        emit('stage', stage='geek-assembly', fraction=.93)
        joined = contained(job,'joined.installing.mp4')
        join_video([(contained(job,f'clips/clip_{index:03d}.mp4'),slot['frames']) for index,slot in enumerate(slots)],
            joined,FPS,request['width'],request['height'])
        scaled = contained(job,'scaled.installing.mp4')
        scale_video(joined, scaled, FPS, request['outputWidth'], request['outputHeight'],request['quality'])
        temporary = contained(job,'geek-raw.installing.mp4')
        subprocess.run([request['ffmpeg'],'-nostdin','-v','error','-y','-i',str(scaled),'-i',request['song'],
            '-map','0:v:0','-map','1:a:0','-c:v','copy','-c:a','aac','-b:a','256k',
            '-metadata','encoded_by=LOPATA ' + request['programVersion'],'-metadata','LOPATA_OPERATION='+job.name,
            '-metadata','LOPATA_VIDEO='+canonical(facts(request, plan)),
            '-movflags','+faststart+use_metadata_tags',str(temporary)],check=True,
            creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        os.replace(temporary, output)
        atomic_json(receipt,dict(schema=1,modelId=MODEL,sourceRevision=REVISION,identity=state.identity,
            requestSha256=file_hash(contained(job,'request.json')),output=str(output),sha256=file_hash(output),
            cards=count+1,clips=count,frames=sum(slot['frames'] for slot in slots)))
        emit('complete',fraction=.98)


if __name__ == '__main__':
    faulthandler.enable(); sys.dont_write_bytecode=True
    action = sys.argv[1] if len(sys.argv)==3 else '--run'
    if action not in ('--run','--analyze','--cards','--verify'): raise ValueError('Unknown worker action.')
    request = json.loads(Path(sys.argv[-1]).read_text(encoding='utf-8-sig'))
    try: run(request,action)
    except Exception as error:
        emit('error',message=str(error)); raise
