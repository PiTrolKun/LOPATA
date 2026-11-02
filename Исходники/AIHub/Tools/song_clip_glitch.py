"""LOPATA adapter for the pinned, unmodified Glitchframe pipeline.

Only local audited model paths are accepted. No Gradio server, updater, implicit
model download, or RIFE/WhisperX dependency is started by this adapter.
"""
from dataclasses import replace
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time


def emit(stage, fraction, message, **extra):
    print(json.dumps(dict(stage=stage, fraction=fraction, message=message, **extra), ensure_ascii=False), flush=True)


def validate_visual_plan(request, duration=None):
    if 'visualPlan' not in request:
        return None
    visual = request['visualPlan']
    if (not isinstance(visual, dict) or type(visual.get('version')) is not int or visual['version'] != 1
            or not isinstance(visual.get('globalStyle'), str) or not visual['globalStyle'].strip()
            or not isinstance(visual.get('scenes'), list) or not 1 <= len(visual['scenes']) <= 256):
        raise ValueError('Invalid Glitchframe visual plan.')
    scenes = visual['scenes']
    for index, scene in enumerate(scenes, 1):
        if (not isinstance(scene, dict) or type(scene.get('index')) is not int or scene['index'] != index
                or not isinstance(scene.get('prompt'), str) or not scene['prompt'].strip()):
            raise ValueError('Invalid Glitchframe visual scene or prompt.')
        for name in ('startSeconds', 'endSeconds'):
            value = scene.get(name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise ValueError('Invalid Glitchframe visual scene timing.')
        if (scene['endSeconds'] < scene['startSeconds']
                or scene['endSeconds'] == scene['startSeconds'] and index != len(scenes)):
            raise ValueError('Only the last Glitchframe anchor may have zero duration.')
    if duration is not None:
        interval = request.get('interval')
        if (isinstance(interval, bool) or not isinstance(interval, (int, float)) or not math.isfinite(interval)
                or interval <= 0 or not math.isfinite(duration) or duration <= 0):
            raise ValueError('Invalid Glitchframe visual-plan duration or interval.')
        count = max(1, math.ceil(duration / interval))
        if len(scenes) != count:
            raise ValueError('Glitchframe visual scene count does not match the native keyframes.')
        for offset, scene in enumerate(scenes):
            start = duration * offset / (count - 1) if count > 1 else 0.0
            end = duration * min(offset + 1, count - 1) / (count - 1) if count > 1 else duration
            if abs(scene['startSeconds'] - start) > .05 or abs(scene['endSeconds'] - end) > .05:
                raise ValueError('Glitchframe visual scene times do not match the native keyframes.')
    return visual


def cache_root(job, request):
    visual = validate_visual_plan(request)
    if visual is None:
        return job / 'cache'
    digest = hashlib.sha256(json.dumps(visual, sort_keys=True, separators=(',', ':'),
                                      ensure_ascii=False, allow_nan=False).encode('utf-8')).hexdigest()
    # Upstream resumes individual PNGs even on manifest mismatch. A different plan
    # therefore needs its own background cache, including interrupted generations.
    return job / 'cache' / ('visual-' + digest)


def planned_background_class(base, request):
    """Override only the instance plan hook; the audited upstream source stays intact."""
    visual = validate_visual_plan(request)
    if visual is None:
        return base

    class PlannedBackground(base):
        def _plan(self, analysis):
            entries = super()._plan(analysis)
            validate_visual_plan(request, analysis.get('duration_sec'))
            if len(entries) != len(visual['scenes']):
                raise ValueError('The upstream keyframe timeline differs from the reviewed visual plan.')
            expected_times = [scene['startSeconds'] for scene in visual['scenes']]
            if len(entries) == 1:
                expected_times = [analysis['duration_sec'] / 2]
            result = []
            for offset, (entry, scene, at) in enumerate(zip(entries, visual['scenes'], expected_times)):
                if entry.index != offset or not math.isfinite(entry.t_sec) or abs(entry.t_sec - at) > .05:
                    raise ValueError('The upstream keyframe positions differ from the reviewed visual plan.')
                result.append(replace(entry, prompt=visual['globalStyle'].strip() + '\n' + scene['prompt'].strip()))
            self._plans = result
            return result

    return PlannedBackground


def run(request):
    started = time.monotonic()
    validate_visual_plan(request)
    libraries = Path(request['libraries']).resolve(strict=True)
    source = Path(request['source']).resolve(strict=True)
    sys.path[:0] = [str(libraries), str(source)]
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1',
                      GLITCHFRAME_FFMPEG=request['ffmpeg'], GLITCHFRAME_FFPROBE=request['ffprobe'])
    job = Path(request['job']).resolve()
    job.mkdir(parents=True, exist_ok=True)
    os.environ['NUMBA_CACHE_DIR'] = str(job / 'numba-cache')
    import torch
    from pipeline.audio_ingest import ingest_audio_file
    from pipeline.audio_analyzer import analyze_song
    from pipeline.background_stills import BackgroundStills
    from pipeline.compositor import CompositorConfig, render_full_video
    import pipeline.compositor as compositor
    from pipeline.kinetic_typography import AlignedWord
    # FFmpeg 8 interprets bare '-' as fd: on Windows. LOPATA's narrow bundle
    # intentionally enables file/pipe only; request the supported stdin protocol.
    original_command = compositor._build_ffmpeg_cmd
    def pipe_command(**kwargs):
        command = original_command(**kwargs)
        return ['pipe:0' if argument == '-' else argument for argument in command]
    compositor._build_ffmpeg_cmd = pipe_command
    if not torch.cuda.is_available():
        raise RuntimeError('Glitchframe upstream SDXL background generator requires a CUDA-compatible GPU.')
    torch.cuda.set_device(request.get('device', 'cuda:0'))
    torch.cuda.reset_peak_memory_stats()
    # The work directory is already unique to the immutable attempt snapshot.
    # Upstream keyframe checkpoints are therefore never shared across changed requests.
    emit('ingest', 0.01, 'Decoding original song')
    ingested = ingest_audio_file(request['song'], cache_dir_root=cache_root(job, request))
    validate_visual_plan(request, ingested.duration_sec)
    analysis = analyze_song(ingested.cache_dir, fps=request['fps'], separate_vocals=False,
                            progress=lambda p, m: emit('analysis', .03 + p * .12, m))
    emit('background', .15, 'Preparing SDXL backgrounds')
    background_type = planned_background_class(BackgroundStills, request)
    background = background_type(ingested.cache_dir, preset_id=request.get('preset', 'lopata'),
        preset_prompt=request['prompt'], width=request['width'], height=request['height'],
        gen_width=request['genWidth'], gen_height=request['genHeight'],
        keyframe_interval=request['interval'], model_id=str(Path(request['model']).resolve(strict=True)),
        num_inference_steps=request['steps'], guidance_scale=request.get('guidance', 7.5),
        seed=request['seed'], ken_burns=True, rife_morph=False)
    try:
        manifest = background.ensure_keyframes(progress=lambda p, m: emit('background', .15 + p * .55, m), apply_rife=False)
        # Release diffusion weights before compositing. Cached images remain resident.
        background._dispose_sdxl_pipeline()
        words = [AlignedWord.from_dict(w) for w in request.get('words', [])]
        config = CompositorConfig(width=request['width'], height=request['height'], fps=request['fps'],
            shader_name=request.get('shader', 'spectral_milkdrop'), intensity=request.get('intensity', .7),
            title_text='', video_codec='h264_nvenc',
            logo_path=Path(request['reference']) if request.get('reference') else None,
            font_size=max(16, request['height'] * .055))
        result = render_full_video(ingested.cache_dir, background=background, analysis=analysis.analysis,
            aligned_words=words, audio_path=ingested.original_wav, outputs_dir=job / 'renders', run_id='render',
            config=config, progress=lambda p, m: emit('render', .7 + p * .28, m))
        receipt = dict(output=str(result.output_mp4), duration=ingested.duration_sec, frames=result.frame_count,
            keyframes=manifest.num_keyframes, seconds=time.monotonic()-started,
            peakAllocated=torch.cuda.max_memory_allocated(), peakReserved=torch.cuda.max_memory_reserved(),
            sourceRevision='a9660508fdbbfbc6243f570d71bbd2af8ee5b111',
            modelRevision='462165984030d82259a11f4367a4eed129e94a7b')
        temporary = job / 'worker-result.json.tmp'
        temporary.write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
        temporary.replace(job / 'worker-result.json')
        emit('complete', 1, 'Glitchframe render complete', **receipt)
    finally:
        background.close()


if __name__ == '__main__':
    try:
        run(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
    except Exception as error:
        emit('error', 0, str(error))
        raise
