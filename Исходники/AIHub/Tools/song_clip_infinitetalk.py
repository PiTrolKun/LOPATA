"""InfiniteTalk CLI: full-song analysis, native window resume, original-audio mux.

All components are prepared/audited by the desktop. This process is owned by its
runner and performs no downloads, installation, licensing or hidden model audit.
"""
import argparse
from datetime import datetime, timezone
from fractions import Fraction
import faulthandler
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_motion_state import (atomic_json, contained, file_hash, object_hash,
                                    operation_lock, read_json, reject_links)
from song_clip_infinitetalk_state import InfiniteState, SOURCE_REVISION, LOOP_SHA256, windows

MODEL = 'infinitetalk'
FPS = 25


def emit(kind, **data):
    print('@infinitetalk ' + json.dumps(dict(kind=kind, **data), ensure_ascii=False, allow_nan=False), flush=True)


def run_media(args, text=False):
    try:
        return subprocess.run(args, capture_output=True, text=text, check=True,
                              creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    except subprocess.CalledProcessError as error:
        details = error.stderr if isinstance(error.stderr, str) else (error.stderr or b'').decode('utf-8', errors='replace')
        raise RuntimeError('InfiniteTalk media operation failed: ' + details[-6000:]) from error


def validate(request):
    if request.get('modelId') != MODEL or request.get('sourceRevision') != SOURCE_REVISION:
        raise ValueError('Unsupported InfiniteTalk model or source revision.')
    for name in ('song', 'reference', 'libraries', 'comfy', 'wrapper', 'ffmpeg', 'ffprobe', 'job'):
        path = Path(request[name])
        if not path.is_absolute():
            raise ValueError('Absolute owned input/component paths are required: ' + name)
        reject_links(path)
        if not path.exists():
            raise FileNotFoundError(name)
    if request.get('extras'):
        path = Path(request['extras'])
        reject_links(path)
        if not path.is_absolute() or not path.is_dir():
            raise ValueError('Invalid InfiniteTalk extra-library directory.')
    for name, low, high in [('width', 256, 1536), ('height', 256, 1536), ('steps', 1, 80),
                            ('seed', -1, 2**53 - 1), ('outputWidth', 256, 3840),
                            ('outputHeight', 256, 3840), ('quality', 1, 100)]:
        if type(request.get(name)) is not int or not low <= request[name] <= high:
            raise ValueError('Invalid InfiniteTalk setting: ' + name)
    if (request['width'] % 32 or request['height'] % 32
            or request['outputWidth'] % 2 or request['outputHeight'] % 2
            or request.get('frameWindow') != 81 or request.get('motionFrames') != 9):
        raise ValueError('Invalid InfiniteTalk spatial or temporal grid.')
    for name, low, high in [('audioCfg', 1, 10), ('vramReserveGb', 0, 32)]:
        value = request.get(name)
        if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
            raise ValueError('Invalid InfiniteTalk setting: ' + name)
    if not re.fullmatch(r'(cpu|cuda(?::\d+)?|xpu(?::\d+)?|mps)', request.get('device', '')):
        raise ValueError('Unsupported managed InfiniteTalk device.')
    if not isinstance(request.get('pins'), dict) or not request['pins']:
        raise ValueError('Audited InfiniteTalk component pins are required.')
    folders = request.get('modelFolders')
    if not isinstance(folders, dict):
        raise ValueError('Audited InfiniteTalk model folders are required.')
    for key, category in [('model', 'diffusion_models'), ('adapter', 'diffusion_models'),
                          ('encoder', 'text_encoders'), ('vae', 'vae'), ('vision', 'clip_vision'), ('wav2vec', 'wav2vec2')]:
        name = request.get(key)
        if not isinstance(name, str) or Path(name).name != name or '/' in name or '\\' in name:
            raise ValueError('InfiniteTalk model names must be basenames.')
        paths = folders.get(category)
        if not isinstance(paths, list) or not paths:
            raise ValueError('Missing InfiniteTalk model folder: ' + category)
        for directory in paths:
            if not Path(directory).is_absolute():
                raise ValueError('Model folders must be absolute.')
            reject_links(directory)
        if not any(contained(root, name).is_file() for root in paths):
            raise FileNotFoundError(name)
    source = Path(request['wrapper']) / 'multitalk' / 'multitalk_loop.py'
    if file_hash(source) != LOOP_SHA256:
        raise ValueError('InfiniteTalk checkpoint compatibility source changed.')


def identity(request, include_plan=True):
    payload = dict(request)
    if not include_plan:
        payload.pop('visualPlan', None)
    return dict(request=object_hash(payload), song=file_hash(request['song']), reference=file_hash(request['reference']),
                loopSource=LOOP_SHA256,
                adapters={name: file_hash(Path(__file__).parent / name) for name in
                    ('song_clip_infinitetalk.py', 'song_clip_infinitetalk_native.py', 'song_clip_infinitetalk_state.py',
                     'song_clip_motion_state.py', 'h3_memory.py')})


def probe(request, path):
    result = run_media([request['ffprobe'], '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(path)], text=True)
    return json.loads(result.stdout)


def audio_duration(request):
    facts = probe(request, request['song'])
    audio = next((stream for stream in facts['streams'] if stream['codec_type'] == 'audio'), None)
    if audio is None:
        raise ValueError('InfiniteTalk requires an audio song file.')
    seconds = float(audio.get('duration', facts['format']['duration']))
    if not math.isfinite(seconds) or not 0 < seconds <= 7200:
        raise ValueError('Invalid InfiniteTalk song duration.')
    return seconds


def analysis_slots(seconds):
    total = math.ceil(seconds * FPS)
    return [dict(index=window['index'], startSeconds=window['startFrame'] / FPS,
                 endSeconds=min(seconds, (window['startFrame'] + 81) / FPS), frames=81,
                 contextFrames=window['contextFrames']) for window in windows(total)]


def analyze(request):
    expected = identity(request, include_plan=False)
    path = contained(request['job'], 'infinitetalk-analysis.json')
    if path.exists():
        previous = read_json(path)
        if previous.get('identity') != expected:
            raise ValueError('InfiniteTalk analysis inputs changed.')
        return previous
    seconds = audio_duration(request)
    data = dict(schema=1, identity=expected, durationSeconds=seconds, fps=FPS,
                frames=math.ceil(seconds * FPS), slots=analysis_slots(seconds))
    atomic_json(path, data)
    emit('analysis', slots=data['slots'], durationSeconds=seconds, fraction=.01)
    return data


def scene_prompts(request, analysis):
    plan = request.get('visualPlan')
    if (not isinstance(plan, dict) or not isinstance(plan.get('scenes'), list)
            or len(plan['scenes']) != len(analysis['slots']) or not str(plan.get('globalStyle', '')).strip()):
        raise ValueError('A complete performer scene plan is required before InfiniteTalk sampling.')
    prompts = []
    continuity = ('A single continuous music performance by the same performer in the input photograph. '
                  'Preserve that performer, facial identity, hair and clothing throughout; natural singing, '
                  'subtle expressive body motion, one coherent camera view. ')
    for slot, scene in zip(analysis['slots'], plan['scenes']):
        if (scene.get('index') != slot['index'] or not str(scene.get('prompt', '')).strip()
                or abs(float(scene.get('startSeconds', -1)) - slot['startSeconds']) > .05
                or abs(float(scene.get('endSeconds', -1)) - slot['endSeconds']) > .05):
            raise ValueError('InfiniteTalk scene timing does not match native audio windows.')
        text = plan['globalStyle'] + '\n' + scene['prompt']
        # H3 token syntax is not an InfiniteTalk API. The photo is conditioned
        # separately by native image/vision nodes, not represented as a tag.
        text = re.sub(r'<Subject\s+\d+>', 'the performer from the reference photograph', text, flags=re.I)
        if any(token in text for token in ('subject_definitions', 'retention_analysis', 'detailed_description')):
            raise ValueError('H3-specific reference payload is not valid for InfiniteTalk.')
        prompts.append(continuity + text.replace('|', ','))
    return prompts


def verify_output(request, path, seconds):
    facts = probe(request, path)
    video = next((s for s in facts['streams'] if s['codec_type'] == 'video'), None)
    audio = next((s for s in facts['streams'] if s['codec_type'] == 'audio'), None)
    if not video or not audio:
        raise ValueError('InfiniteTalk output must contain video and the original song.')
    video_seconds = float(video.get('duration', facts['format']['duration']))
    audio_seconds = float(audio.get('duration', facts['format']['duration']))
    rate = video.get('avg_frame_rate', '').split('/')
    fps = float(rate[0]) / float(rate[1]) if len(rate) == 2 and float(rate[1]) else 0
    if (abs(video_seconds - seconds) > .12 or abs(audio_seconds - seconds) > .12
            or abs(fps - 24) > .001 or int(video['width']) != request['outputWidth']
            or int(video['height']) != request['outputHeight']):
        raise ValueError('InfiniteTalk output does not cover the complete original song or requested canvas.')
    return facts


def encode_video(request, state, seconds, facts):
    """Bundled PyAV supplies x264, just as in the existing SongClip workers.

Keep one decoded picture at a time. Native 25 fps frame-hold sampling at the
24 fps output instants drops only the required frames, never a song segment.
"""
    sys.path[:0] = [p for p in (request.get('extras'), request.get('libraries')) if p]
    import av
    from PIL import Image
    path = contained(state.root, 'infinitetalk-video.pending.mp4')
    width, height = request['outputWidth'], request['outputHeight']
    crf = max(15, min(32, round(32 - (request['quality'] - 1) * 17 / 99)))
    with av.open(str(path), 'w', format='mp4', options={'movflags': '+faststart+use_metadata_tags'}) as output:
        output.metadata['encoded_by'] = 'LOPATA ' + str(facts['programVersion'])
        output.metadata['LOPATA_VIDEO'] = json.dumps(facts, ensure_ascii=False, separators=(',', ':'))
        output.metadata['LOPATA_OPERATION'] = str(facts['operation'])
        stream = output.add_stream('libx264', rate=24)
        stream.width, stream.height, stream.pix_fmt = width, height, 'yuv420p'
        stream.time_base = stream.codec_context.time_base = Fraction(1, 24)
        stream.options = dict(crf=str(crf), preset='medium')
        for index in range(math.ceil(seconds * 24)):
            native_index = min(index * 25 // 24, state.frame_count - 1)
            if native_index < 0:
                raise ValueError('No accepted InfiniteTalk frames are available.')
            source = contained(state.frames, f'frame_{native_index:05d}.png')
            with Image.open(source) as image:
                image = image.convert('RGB')
                image.thumbnail((width, height), Image.Resampling.LANCZOS)
                # thumbnail never enlarges; upscale the photograph when needed.
                ratio = min(width / image.width, height / image.height)
                if ratio != 1:
                    image = image.resize((max(1, round(image.width * ratio)), max(1, round(image.height * ratio))), Image.Resampling.LANCZOS)
                canvas = Image.new('RGB', (width, height))
                canvas.paste(image, ((width - image.width) // 2, (height - image.height) // 2))
                frame = av.VideoFrame.from_image(canvas)
            frame = frame.reformat(width, height, format='yuv420p')
            frame.pts, frame.time_base = index, Fraction(1, 24)
            for packet in stream.encode(frame):
                output.mux(packet)
        for packet in stream.encode():
            output.mux(packet)
    return path


def mux(request, state, seconds):
    output = contained(state.root, 'infinitetalk-output.mp4')
    staging = contained(state.root, 'infinitetalk-output.pending.mp4')
    emit('stage', stage='infinitetalk-assemble', fraction=.94)
    # Only native padding beyond the audio tail is removed. Input 1 is the
    # untouched full song, never an audio chunk or the Wav2Vec mono derivative.
    facts = dict(program='LOPATA', programVersion=request.get('programVersion', 'unknown'),
                 scenario='song-to-clip/infinitetalk', operation=request.get('operationId', state.root.name),
                 createdUtc=datetime.now(timezone.utc).isoformat(), models=request.get('pins', {}))
    video = encode_video(request, state, seconds, facts)
    args = [request['ffmpeg'], '-y', '-v', 'error', '-i', str(video), '-i', request['song'],
            '-map', '0:v:0', '-map', '1:a:0', '-t', format(seconds, '.9f'), '-c:v', 'copy',
            '-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart+use_metadata_tags',
            '-metadata', 'encoded_by=LOPATA ' + str(facts['programVersion']),
            '-metadata', 'LOPATA_VIDEO=' + json.dumps(facts, ensure_ascii=False, separators=(',', ':')),
            '-metadata', 'LOPATA_OPERATION=' + str(facts['operation']), str(staging)]
    run_media(args)
    verify_output(request, staging, seconds)
    os.replace(staging, output)
    return output


def verify(request, analysis, state, request_sha256):
    path = contained(request['job'], 'worker-result.json')
    receipt = read_json(path)
    output = contained(request['job'], 'infinitetalk-output.mp4')
    if (not state.done or receipt.get('schema') != 1 or receipt.get('modelId') != MODEL or receipt.get('sourceRevision') != SOURCE_REVISION
            or receipt.get('requestSha256') != request_sha256 or receipt.get('output') != str(output)
            or receipt.get('sha256') != file_hash(output) or receipt.get('frames') != analysis['frames']
            or receipt.get('fps') != FPS or receipt.get('referenceUsed') is not True
            or receipt.get('sourceSha256') != file_hash(request['song'])
            or receipt.get('referenceSha256') != file_hash(request['reference'])):
        raise ValueError('InfiniteTalk result receipt or accepted native windows are invalid.')
    verify_output(request, output, analysis['durationSeconds'])
    return receipt


def run(request, analysis, request_sha256):
    from song_clip_infinitetalk_native import NativeInfiniteTalk, hooked_source
    prompts = scene_prompts(request, analysis)
    state = InfiniteState(request['job'], identity(request), request['seed'], analysis['frames'])
    result_path = contained(request['job'], 'worker-result.json')
    if result_path.exists():
        result = verify(request, analysis, state, request_sha256)
        emit('complete', **result, fraction=1)
        return result
    state.quarantine_uncommitted()
    source = Path(request['wrapper']) / 'multitalk' / 'multitalk_loop.py'
    hook_hash = __import__('hashlib').sha256(hooked_source(source.read_bytes()).encode('utf-8')).hexdigest()
    if not state.done:
        emit('warning', message='InfiniteTalk uses the complete original mix; vocal separation is not enabled. '
             'Instruments can reduce singing-mouth accuracy. This is a continuous performer animation, not a scene-montage model.')
        native = NativeInfiniteTalk(request, state, emit)
        if native.hook_sha256 != hook_hash:
            raise ValueError('InfiniteTalk derived checkpoint adapter changed.')
        native.run(prompts)
        native = None
    else:
        emit('cached', windows=len(state.chain), frames=state.frame_count, fraction=.92)
    output = mux(request, state, analysis['durationSeconds'])
    result = dict(schema=1, modelId=MODEL, sourceRevision=SOURCE_REVISION, requestSha256=request_sha256,
                  output=str(output), sha256=file_hash(output), frames=analysis['frames'], fps=FPS,
                  referenceUsed=True, referenceSha256=file_hash(request['reference']), sourceSha256=file_hash(request['song']),
                  nativeOutputFrames=state.frame_count, outputFps=24, durationSeconds=analysis['durationSeconds'],
                  loopSourceSha256=LOOP_SHA256, checkpointAdapterSha256=hook_hash)
    atomic_json(result_path, result)
    emit('complete', **result, fraction=1)
    return result


def main():
    faulthandler.enable()
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--analyze', action='store_true')
    mode.add_argument('--run', action='store_true')
    mode.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    request = read_json(args.request)
    request_sha256 = file_hash(args.request)
    validate(request)
    with operation_lock(request['job']):
        analysis = analyze(request)
        if args.analyze:
            return
        if args.verify:
            state = InfiniteState(request['job'], identity(request), request['seed'], analysis['frames'])
            emit('complete', **verify(request, analysis, state, request_sha256), fraction=1)
        else:
            run(request, analysis, request_sha256)


if __name__ == '__main__':
    main()
