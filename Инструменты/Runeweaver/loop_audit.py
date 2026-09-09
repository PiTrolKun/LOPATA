"""Summarize saved lab output and replay a conservative literal-repeat detector."""
import json
from loop_lab import OUT, analyze
from probe_server import save


def replay(events):
    text, checked = '', 0
    for event in events:
        data = event['data']
        for choice in data.get('choices', []):
            delta = choice.get('delta', {})
            text += (delta.get('reasoning_content') or '') + (delta.get('content') or '')
        if len(text) - checked < 64:
            continue
        checked = len(text)
        stats = analyze(text)
        if stats['loop']:
            return {'seconds': event['seconds'], 'chars': len(text), 'periodic': stats['periodic'],
                    'numeric_loop': stats['numeric_loop'], 'numeric_periodic': stats['numeric_periodic']}
    return None


def main():
    phases, cases = [], []
    for folder in sorted(p for p in OUT.iterdir() if p.is_dir()):
        results = []
        for path in sorted(folder.glob('*-result.json')):
            result = json.loads(path.read_text(encoding='utf-8'))
            if result['name'].startswith('warm_'):
                continue
            result['phase'] = folder.name
            text = path.with_name(path.name.replace('-result.json', '.txt')).read_text(encoding='utf-8')
            result.update(analyze(text))
            # Only a research flag: legitimate user-requested word lists may match it.
            result['format_flag'] = result['short_quoted_list_run'] >= 24
            events_path = path.with_name(path.name.replace('-result.json', '-events.json'))
            if result['loop'] and events_path.exists():
                result['repeat_guard_replay'] = replay(json.loads(events_path.read_text(encoding='utf-8')))
            results.append(result)
        if not results:
            continue
        phases.append({'phase': folder.name, 'requests': len(results),
            'stop': sum(x.get('finish') == 'stop' for x in results),
            'length': sum(x.get('finish') == 'length' for x in results),
            'literal_loops': sum(x['literal_loop'] for x in results),
            'numeric_loops': sum(x['numeric_loop'] for x in results),
            'format_flags_without_literal_loop': sum(x['format_flag'] and not x['loop'] for x in results),
            'errors': sum(bool(x.get('error')) for x in results)})
        cases += results
    save(OUT / 'audit.json', {'phases': phases, 'cases': cases})
    for phase in phases:
        print(json.dumps(phase, ensure_ascii=False))


if __name__ == '__main__':
    main()
