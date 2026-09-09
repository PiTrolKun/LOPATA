"""Reference Alpha controls: identical user text, then repeat penalty, then reasoning switch."""
import json
import probe_server
from loop_lab import OUT, request
from probe_server import ROOT, Server, save


def main():
    entry = ROOT / 'Данные_для_внедрения/Модели/Vision/Qwen3.8-4B-Distill-GGUF/796f0c8fbdab/Qwen3.8-4B-Distill.Q5_K_M.gguf'
    probe_server.CONFIG['filename'] = str(entry)
    base = json.loads((OUT / 'alpha-raw-request.json').read_text(encoding='utf-8'))
    server = Server(OUT / 'alpha_reference', context=32768,
                    extra_args=['--reasoning-format','none','-t','8','-tb','8'])
    results = []
    try:
        for label, changes in [
            ('raw_thinking', {}),
            ('repeat110_thinking', {'repeat_penalty': 1.1}),
            ('raw_no_thinking', {'chat_template_kwargs': {'enable_thinking': False}}),
        ]:
            for seed in [0,1]:
                body = dict(base, id_slot=0, cache_prompt=False, seed=seed, max_tokens=8192)
                body.update(changes)
                results.append(request(server, f'{label}_seed_{seed}', body))
                save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
