"""One-factor sampler controls on archived Runeweaver failures; no app mutation."""
import argparse
from loop_lab import BASE, OUT, request
from probe_server import Server, save


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--phase', default='sampler_matrix')
    parser.add_argument('--seeds', default='0,20')
    args = parser.parse_args()
    controls = [
        ('baseline_a', {}),
        ('baseline_b', {}),
        ('repeat_window_512', {'repeat_last_n': 512}),
        ('repeat_110', {'repeat_penalty': 1.1}),
        ('dry_080', {'dry_multiplier': .8}),
        ('temperature_080', {'temperature': .8}),
    ]
    server = Server(OUT / args.phase, context=16384, extra_args=['-t', '8', '-tb', '8'])
    results = []
    try:
        for condition, changes in controls:
            for seed in map(int, args.seeds.split(',')):
                body = dict(BASE, seed=seed, id_slot=0, cache_prompt=False)
                body.update(changes)
                result = request(server, f'{condition}_seed_{seed}', body)
                result['condition'] = condition
                results.append(result)
                save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
