"""Measure next-token preference as an observed repeated block is added to the prefix."""
import math
from loop_lab import BASE, OUT
from probe_server import Server, save


def main():
    text = (OUT / 'rune_single_uncached_more/seed_34.txt').read_text(encoding='utf-8')
    cycle = '   - "Антон",\n   - "Лера",\n'
    start = text.index(cycle * 3)
    prefix = text[:start]
    server = Server(OUT / 'next_token_probe', context=16384, extra_args=['-t','8','-tb','8'])
    results = []
    try:
        rendered = server.api('/apply-template', {'messages': BASE['messages'], 'add_generation_prompt': True})['prompt']
        for repeats in [0,1,2,4,8,16]:
            prompt = rendered + prefix + cycle * repeats + '   - "'
            for post in [False, True]:
                body = dict(prompt=prompt, id_slot=0, cache_prompt=False, seed=0,
                            temperature=.5, repeat_penalty=1.05, n_predict=1, n_probs=10,
                            post_sampling_probs=post, stream=False)
                name = f'repeats_{repeats}_post_{post}'
                save(server.folder / (name + '-request.json'), body)
                result = server.api('/completion', body)
                save(server.folder / (name + '-response.json'), result)
                probs = result.get('completion_probabilities', result.get('probs', []))
                first = probs[0] if probs else {}
                top = first.get('top_probs', first.get('top_logprobs', []))
                item = {'repeats': repeats, 'post_sampling': post,
                        'top': [{'token': x.get('token'), 'prob': x.get('prob', math.exp(x.get('logprob', -1000)))} for x in top[:3]]}
                results.append(item)
                print(item, flush=True)
        save(server.folder / 'summary.json', results)
    finally:
        server.close()


if __name__ == '__main__':
    main()
