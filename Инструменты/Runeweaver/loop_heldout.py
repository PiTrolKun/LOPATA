"""Check a candidate on different prompts, including deliberate repetition and quotation."""
import json
from loop_lab import BASE, OUT, request
from probe_server import ROOT, Server, save


def main():
    fixtures = ROOT / 'Тесты/Runeweaver/suite_20260909/single_8192'
    cases = {name: json.loads((fixtures / (name + '-request.json')).read_text(encoding='utf-8'))
             for name in ['writer_scene', 'writer_continue', 'advisor_errors', 'advisor_clean']}
    for name, text in {
        'exact_quote': 'Перепиши без изменений эту фразу и ничего больше: «Антон сидел на старом, потрескавшемся деревянном скамейке».',
        'intentional_repeat': 'Напиши ровно десять строк. Каждая строка должна содержать только фразу: «Мы вернёмся домой». Повтор здесь намеренный, это припев.'
    }.items():
        cases[name] = dict(BASE, messages=[{'role': 'user', 'content': text}], max_tokens=400)
    results = []
    server = Server(OUT / 'heldout_repeat110', context=16384, extra_args=['-t', '8', '-tb', '8'])
    try:
        for penalty in [1.05, 1.1]:
            for name, original in cases.items():
                body = dict(original, id_slot=0, cache_prompt=False, seed=431, repeat_penalty=penalty)
                results.append(request(server, f'{name}_repeat{penalty}', body))
                save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
