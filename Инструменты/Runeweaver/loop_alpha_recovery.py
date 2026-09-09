"""Live control for the observed Alpha repetition with changing clock numbers."""
import json
import probe_server
from loop_lab import OUT, request
from probe_server import ROOT, Server, save


def main():
    probe_server.CONFIG['filename'] = str(ROOT / 'Данные_для_внедрения/Модели/Vision/Qwen3.8-4B-Distill-GGUF/796f0c8fbdab/Qwen3.8-4B-Distill.Q5_K_M.gguf')
    base = json.loads((OUT / 'alpha-raw-request.json').read_text(encoding='utf-8'))
    server = Server(OUT / 'alpha_guard_recovery', context=32768,
                    extra_args=['--reasoning-format','none','-t','8','-tb','8'])
    try:
        body = dict(base, id_slot=0, cache_prompt=False, seed=0, max_tokens=8192)
        result = request(server, 'guard_numeric', body, guard=True)
        results = [result]
        if result.get('guard_stopped'):
            results.append(request(server, 'retry_numeric', dict(body, repeat_penalty=1.1)))
        save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
