"""App-shaped serial two-slot control, with recorded history and interleaved Writer."""
import json
from loop_lab import BASE, OUT, SOURCE, request
from probe_server import Server, save


def main():
    server = Server(OUT / 'shared_serial', context=24576, slots=2,
                    extra_args=['-kvu', '--no-cache-idle-slots', '-t', '8', '-tb', '8'])
    writer = json.loads(next(SOURCE.glob('20260909_102700*.request.json')).read_text(encoding='utf-8-sig'))
    results = []
    try:
        for penalty in [1.05, 1.1]:
            for seed in [20,24,34]:
                results.append(request(server, f'writer_{penalty}_{seed}',
                    dict(writer, seed=seed, id_slot=0, cache_prompt=True)))
                results.append(request(server, f'advisor_{penalty}_{seed}',
                    dict(BASE, seed=seed, id_slot=1, cache_prompt=True, repeat_penalty=penalty)))
                save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
