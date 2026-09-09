"""Controlled repetition experiments. One server at a time, exact archived requests."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.request
import probe_server
from probe_server import Server, save, ROOT

SOURCE = ROOT / 'Тесты/Runeweaver/loop_20260909_1727'
OUT = ROOT / 'Тесты/Runeweaver/loop_lab_20260909'
BASE = json.loads(next(SOURCE.glob('20260909_102701*.request.json')).read_text(encoding='utf-8-sig'))

def period(words):
    best = {'words': 0, 'unit': '', 'copies': 0}
    for width in range(1, min(129, len(words) // 3 + 1)):
        run = 0
        for i in range(width, len(words)):
            run = run + 1 if words[i] == words[i-width] else 0
            if run >= width * 2 and run + width > best['words']:
                best = {'words': run + width, 'unit': ' '.join(words[i-width+1:i+1]), 'copies': (run + width) // width}
    return best


def analyze(text):
    words = re.findall(r'\w+', text.lower())
    lines = [re.sub(r'^[\s\-*\d.]+', '', x.strip()).strip('" ,') for x in text.splitlines() if x.strip()]
    repeated = Counter(lines).most_common(3)
    best = period(words)
    literal_loop = ((best['words'] >= 48 and best['copies'] >= 6)
            or (best['words'] >= 192 and best['copies'] >= 3))
    numeric = period(['#' if word.isdecimal() else word for word in words]) if any(w.isdecimal() for w in words) else best
    numeric_loop = not literal_loop and numeric['words'] >= 128 and numeric['copies'] >= 6
    loop = literal_loop or numeric_loop
    short_list_run = longest_short_list = 0
    for line in text.splitlines():
        short_item = re.match(r'^\s*[-*]\s+["«]', line) and len(re.findall(r'\w+', line)) <= 3
        short_list_run = short_list_run + 1 if short_item else 0
        longest_short_list = max(longest_short_list, short_list_run)
    return {'loop': loop, 'literal_loop': literal_loop, 'numeric_loop': numeric_loop,
            'detector_version': 3, 'periodic': best, 'numeric_periodic': numeric,
            'repeated_lines': repeated, 'words': len(words),
            'short_quoted_list_run': longest_short_list}

def request(server, name, body, cap_seconds=120, guard=False):
    server.phase = name
    save(server.folder / (name + '-request.json'), body)
    started = time.monotonic()
    result = {'name': name, 'pid': server.process.pid, 'seed': body.get('seed'), 'slot': body.get('id_slot')}
    pieces, events = [], []
    checked_chars = 0
    req = urllib.request.Request(server.url + '/v1/chat/completions', json.dumps(body, ensure_ascii=False).encode('utf-8'), {'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=cap_seconds) as response:
            for line in response:
                elapsed = time.monotonic() - started
                if elapsed > cap_seconds:
                    raise TimeoutError('Per-request wall limit exceeded')
                line = line.decode('utf-8').strip()
                if not line.startswith('data:'):
                    continue
                value = line[5:].strip()
                if value == '[DONE]':
                    result['done'] = True
                    break
                data = json.loads(value)
                events.append({'seconds': round(elapsed, 4), 'data': data})
                if data.get('usage'): result['usage'] = data['usage']
                if data.get('timings'): result['timings'] = data['timings']
                for choice in data.get('choices', []):
                    delta = choice.get('delta', {})
                    for key in ('reasoning_content', 'content'):
                        if delta.get(key):
                            result.setdefault('first_seconds', elapsed)
                            pieces.append(delta[key])
                    if choice.get('finish_reason'): result['finish'] = choice['finish_reason']
                if guard:
                    current = ''.join(pieces)
                    if len(current) - checked_chars >= 64:
                        checked_chars = len(current)
                        if analyze(current)['loop']:
                            result['guard_stopped'] = True
                            result['guard_seconds'] = elapsed
                            break
    except Exception as error:
        result['error'] = repr(error)
        if hasattr(error, 'read'): result['error_body'] = error.read().decode('utf-8', errors='replace')
    text = ''.join(pieces)
    result.update(seconds=round(time.monotonic()-started, 3), chars=len(text), sha256=hashlib.sha256(text.encode()).hexdigest(), **analyze(text))
    (server.folder / (name + '.txt')).write_text(text, encoding='utf-8')
    save(server.folder / (name + '-result.json'), result)
    save(server.folder / (name + '-events.json'), events)
    # Wait for cancellation/disconnect to be observed before any following request.
    for _ in range(50):
        if all(not slot['is_processing'] for slot in server.api('/slots')): break
        time.sleep(.1)
    else: raise TimeoutError('Server did not become idle')
    save(server.folder / (name + '-slots.json'), server.api('/slots'))
    print(json.dumps({k: result.get(k) for k in ('name','seed','seconds','finish','loop','periodic','error')}, ensure_ascii=False), flush=True)
    return result

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--phase', required=True)
    parser.add_argument('--seeds', default='0,1,2,3,4,5,6,7,8,9,10,11')
    parser.add_argument('--slots', type=int, default=1)
    parser.add_argument('--context', type=int, default=16384)
    parser.add_argument('--cache', action='store_true')
    parser.add_argument('--unified', action='store_true')
    parser.add_argument('--cpu', action='store_true')
    parser.add_argument('--model')
    parser.add_argument('--backend')
    parser.add_argument('--request-file')
    parser.add_argument('--overrides', default='{}')
    parser.add_argument('--warm', action='store_true')
    parser.add_argument('--extra-args', default='[]')
    args = parser.parse_args()
    if args.model: probe_server.CONFIG['filename'] = str(Path(args.model).resolve())
    if args.backend: probe_server.CONFIG['backend'] = str(Path(args.backend).resolve())
    extra = ['-t','8','-tb','8']
    if args.unified: extra += ['-kvu','--no-cache-idle-slots']
    if args.cpu: extra += ['-ngl','0','--device','none','--no-op-offload']
    extra += json.loads(args.extra_args)
    server = Server(OUT / args.phase, context=args.context, slots=args.slots, extra_args=extra)
    base = json.loads(Path(args.request_file).read_text(encoding='utf-8-sig')) if args.request_file else BASE
    results = []
    try:
        for seed in map(int, args.seeds.split(',')):
            slot = 1 if args.slots > 1 else 0
            if args.warm:
                # Synthetic warm-slot control, not an exact replay of the historical KV state.
                preceding = json.loads(next(SOURCE.glob('20260909_102700*.request.json')).read_text(encoding='utf-8-sig'))
                preceding.update(seed=seed, cache_prompt=False, max_tokens=32)
                request(server, f'warm_writer_{seed}', preceding)
                prefix = dict(base, messages=base['messages'][:-2], id_slot=slot, seed=seed, cache_prompt=False, max_tokens=8)
                request(server, f'warm_advisor_{seed}', prefix)
            body = dict(base, id_slot=slot, seed=seed, cache_prompt=args.cache)
            body.update(json.loads(args.overrides))
            results.append(request(server, f'seed_{seed}', body))
            save(server.folder / 'summary.json', results)
        save(server.folder / 'final-slots.json', server.api('/slots'))
    finally:
        server.close()

if __name__ == '__main__': main()
