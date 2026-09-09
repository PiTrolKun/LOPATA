"""Live proof: detect a repeated block, cancel, then retry from the same clean input."""
from loop_lab import BASE, OUT, request
from probe_server import Server, save


def main():
    server = Server(OUT / 'guard_recovery', context=16384, extra_args=['-t','8','-tb','8'])
    results = []
    try:
        for seed, penalty in [(20,1.05),(24,1.05),(34,1.05),(8,1.1),(16,1.1),(33,1.1)]:
            original = dict(BASE, seed=seed, id_slot=0, cache_prompt=False, repeat_penalty=penalty)
            stopped = request(server, f'guard_seed_{seed}', original, guard=True)
            results.append(stopped)
            if stopped.get('guard_stopped'):
                results.append(request(server, f'retry_seed_{seed}', dict(original, repeat_penalty=1.1,
                    temperature=.8 if penalty == 1.1 else .5)))
            save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
