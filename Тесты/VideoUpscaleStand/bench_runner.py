"""Serial author-tool benchmark runner; stores completed jobs and native failures."""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import subprocess
import time


def write_json(path, value):
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    temp.replace(path)


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def gpu_sample():
    try:
        result = subprocess.run(['nvidia-smi', '--query-gpu=memory.used,utilization.gpu',
            '--format=csv,noheader,nounits'], capture_output=True, text=True,
            timeout=10, creationflags=subprocess.CREATE_NO_WINDOW)
        memory, utilization = result.stdout.strip().splitlines()[0].split(',')
        return {'global_gpu_memory_mib': int(memory), 'global_gpu_utilization_percent': int(utilization)}
    except Exception as error:
        return {'sample_error': type(error).__name__}


def execute(root, job, ffmpeg, ffprobe):
    folder = root / 'jobs' / job['id']
    folder.mkdir(parents=True, exist_ok=True)
    result_path = folder / 'result.json'
    output = pathlib.Path(job['native_output'])
    final = folder / 'result.mkv'
    if result_path.exists():
        previous = json.loads(result_path.read_text(encoding='utf-8'))
        print('EXISTING', job['id'], previous['status'], flush=True)
        return previous
    write_json(folder / 'request.json', job)
    env = os.environ.copy()
    env.update(job.get('environment', {}))
    output.parent.mkdir(parents=True, exist_ok=True)
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    baseline = gpu_sample()
    samples = []
    start = time.monotonic()
    timeout = False
    print('RUN', job['id'], flush=True)
    with (folder / 'stdout.log').open('w', encoding='utf-8') as stdout, (folder / 'stderr.log').open('w', encoding='utf-8') as stderr:
        process = subprocess.Popen(job['command'], cwd=job.get('cwd'), env=env,
            stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
        while process.poll() is None:
            samples.append({'seconds': round(time.monotonic() - start, 3), **gpu_sample()})
            if time.monotonic() - start > job.get('timeout_seconds', 240):
                timeout = True
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                               capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
                break
            time.sleep(.4)
        code = process.wait()
    elapsed = time.monotonic() - start
    result = {'id': job['id'], 'engine': job['engine'], 'settings': job['settings'],
        'started_utc': started, 'exit_code': code, 'timeout': timeout,
        'elapsed_wall_seconds': elapsed, 'baseline': baseline, 'samples': samples,
        'gpu_memory_scope': 'whole GPU, includes desktop and other resident applications',
        'status': 'native_failed' if code or timeout else 'validation_failed'}
    if samples:
        result['peak_global_gpu_memory_mib'] = max(s.get('global_gpu_memory_mib', 0) for s in samples)
    try:
        if code or timeout:
            raise RuntimeError('Native process failed or timed out; see logs')
        if not output.is_file() or output.stat().st_size < 1024:
            raise RuntimeError('Native output is missing or empty')
        metadata = {'application': 'LOPATA', 'version': '0.7.21-dev',
            'operation': 'VideoUpscaleStand', 'created_utc': started, 'engine': job['engine'],
            'engine_release': job.get('engine_release'), 'settings': job['settings'],
            'runtime_identity': job.get('runtime_identity', 'not independently identified')}
        remux = subprocess.run([str(ffmpeg), '-hide_banner', '-loglevel', 'error', '-n',
            '-i', str(output), '-map', '0', '-c', 'copy', '-metadata',
            'comment=' + json.dumps(metadata, ensure_ascii=False, separators=(',', ':')), str(final)],
            capture_output=True, encoding='utf-8', errors='replace', timeout=90,
            creationflags=subprocess.CREATE_NO_WINDOW)
        if remux.returncode:
            raise RuntimeError('Provenance remux failed: ' + remux.stderr[-1000:])
        probe = json.loads(subprocess.check_output([str(ffprobe), '-v', 'error', '-count_frames',
            '-show_streams', '-show_format', '-of', 'json', str(final)], encoding='utf-8',
            timeout=120, creationflags=subprocess.CREATE_NO_WINDOW))
        video = next(s for s in probe['streams'] if s['codec_type'] == 'video')
        assert int(video['nb_read_frames']) == job['expected_frames'], ('frame count', video.get('nb_read_frames'))
        assert [video['width'], video['height']] == job['expected_size'], ('geometry', video['width'], video['height'])
        assert json.loads(probe['format']['tags']['COMMENT'])['application'] == 'LOPATA'
        result.update(status='complete', output=str(final), output_bytes=final.stat().st_size,
            output_sha256=file_hash(final), probe=probe, provenance=metadata)
    except Exception as error:
        result['error'] = str(error)
    write_json(result_path, result)
    print('RESULT', job['id'], result['status'], round(elapsed, 2), 'seconds', flush=True)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=pathlib.Path)
    parser.add_argument('plan', type=pathlib.Path)
    args = parser.parse_args()
    root = args.root.resolve()
    plan = json.loads(args.plan.read_text(encoding='utf-8'))
    results = []
    for job in plan['jobs']:
        try:
            results.append(execute(root, job, pathlib.Path(plan['ffmpeg']), pathlib.Path(plan['ffprobe'])))
        except Exception as error:
            results.append({'id': job['id'], 'status': 'runner_failed', 'error': str(error)})
            print('RUNNER FAILED', job['id'], str(error), flush=True)
        write_json(root / (args.plan.stem + '.results.json'), results)
    print('DONE', len(results), 'jobs;', sum(r['status'] == 'complete' for r in results), 'complete', flush=True)


if __name__ == '__main__':
    main()
