"""Bounded whole-source-line search in independent CTC acoustic probabilities.

The model does not see lyrics. Missing matches are never interpolated. Local
Viterbi refinement uses numpy instead of a platform-specific Cython extension.
"""
import re
import unicodedata

import numpy as np
import torch


def normalized(text):
    text = unicodedata.normalize('NFC', text).lower().replace('ё', 'е')
    text = text.replace('\u0301', '')
    return ' '.join(re.findall(r'[^\W\d_]+', text, flags=re.UNICODE))


def distance(left, right):
    previous = list(range(len(right)+1))
    for i, a in enumerate(left, 1):
        current = [i]
        for j, b in enumerate(right, 1):
            current.append(min(current[-1]+1, previous[j]+1, previous[j-1]+(a != b)))
        previous = current
    return previous[-1]


def refine(probs, ids, blank, dt, offset):
    """Free prefix/suffix, but every target character must have acoustic evidence."""
    symbols = np.full(2*len(ids)+1, blank, dtype=np.int64)
    symbols[1::2] = ids
    count = len(symbols)
    allowed_skip = np.zeros(count, dtype=bool)
    allowed_skip[2:] = (symbols[2:] != blank) & (symbols[2:] != symbols[:-2])
    scores = np.full(count, -np.inf, dtype=np.float64)
    scores[0] = 0
    pointers = np.zeros((len(probs), count), dtype=np.uint8)
    best_score = -np.inf
    best_end = best_state = -1
    for t, frame in enumerate(probs):
        transitions = np.stack([scores,
            np.concatenate(([-np.inf], scores[:-1])),
            np.concatenate(([-np.inf, -np.inf], scores[:-2]))])
        transitions[2, ~allowed_skip] = -np.inf
        step = transitions.argmax(0)
        pointers[t] = step
        scores = transitions[step, np.arange(count)] + frame[symbols]
        scores[0] = 0  # skip preceding audio, not target text
        final_state = count-2 if scores[-2] >= scores[-1] else count-1
        if scores[final_state] > best_score:
            best_score = scores[final_state]
            best_end, best_state = t, final_state
    if not np.isfinite(best_score):
        return None
    t, state = best_end, best_state
    frames = []
    visited = set()
    while t >= 0 and state > 0:
        if state % 2:
            frames.append(t)
            visited.add(state//2)
        state -= int(pointers[t, state])
        t -= 1
    if len(visited) != len(ids) or not frames:
        return None
    return offset+min(frames)*dt, offset+(max(frames)+1)*dt


def greedy(probs, chars, blank):
    ids = probs.argmax(-1)
    return ''.join(chars[token] for i, token in enumerate(ids)
                   if token != blank and (i == 0 or token != ids[i-1])).strip()


def choose_path(rows):
    """Maximum supported ordered occurrences. Skips never receive fabricated times."""
    nodes = []
    for row in rows:
        for candidate in row['candidates']:
            options = [n for n in nodes if n['index'] < row['index']
                       and candidate['start'] >= n['candidate']['end']-.1]
            previous = max(options, key=lambda n:(n['count'], -n['cost'])) if options else None
            nodes.append({'index':row['index'], 'candidate':candidate,
                          'count':1+(previous['count'] if previous else 0),
                          'cost':candidate['cost']+(previous['cost'] if previous else 0),
                          'previous':previous})
    if not nodes:
        return []
    at = max(nodes, key=lambda n:(n['count'], -n['cost']))
    result = []
    while at:
        result.append({'index':at['index'], **at['candidate']})
        at = at['previous']
    result.reverse()
    for left, right in zip(result, result[1:]):
        if left['end'] > right['start']:
            # Resolve at most 100ms overlap at the shared boundary, preserving
            # the acoustic interval envelope and source occurrence order.
            boundary = (left['end']+right['start'])/2
            left['end'] = boundary
            right['start'] = boundary
    return result


def search(probs, chars, blank, dt, source, log):
    vocab = {char:i for i, char in enumerate(chars)}
    targets = []
    for row in source:
        text = normalized(row['text'])
        unsupported = sorted(set(text)-vocab.keys())
        targets.append({**row, 'normalized':text, 'unsupported':unsupported,
                        'ids':[vocab[c] for c in text] if not unsupported else [],
                        'pool':[], 'candidates':[]})
    # Match the successful bench window grid. Keep only one batch in memory.
    for seconds in [2, 3, 4, 6, 8, 12]:
        count = round(seconds/dt)
        starts = np.arange(0, len(probs)-count+1, max(1, round(.4/dt)))
        with torch.inference_mode():
            for offset in range(0, len(starts), 64):
                locations = starts[offset:offset+64]
                block = torch.from_numpy(np.stack([probs[s:s+count] for s in locations]))
                n = len(block)
                competitors = block.max(-1).values.sum(-1)
                for row in targets:
                    ids = row['ids']
                    if not ids:
                        continue
                    losses = torch.nn.functional.ctc_loss(
                        block.transpose(0, 1), torch.tensor(ids)[None].expand(n, -1).contiguous(),
                        torch.full((n,), count, dtype=torch.long),
                        torch.full((n,), len(ids), dtype=torch.long), blank=blank, reduction='none')
                    costs = (losses+competitors)/len(ids)
                    row['pool'].extend((float(value), int(start), count)
                                       for value, start in zip(costs, locations) if torch.isfinite(value))
        log(f'[Lyrics] Phrase windows: {seconds}s')
    for row in targets:
        picked = []
        for value, start, count in sorted(row['pool']):
            if value > 3.5:  # excludes bench's unrelated whole-phrase controls
                break
            if any(max(start, p[1]) < min(start+count, p[1]+p[2]) for p in picked):
                continue
            picked.append((value, start, count))
            if len(picked) == 8:  # allow repeated choruses without forcing the first occurrence
                break
        for value, start, count in picked:
            region = probs[start:start+count]
            bounds = refine(region, row['ids'], blank, dt, start*dt)
            if bounds is None:
                continue
            a, b = [round(t/dt) for t in bounds]
            readback = greedy(probs[a:b], chars, blank)
            mismatch = distance(row['normalized'], readback)/max(1, len(row['normalized']))
            if mismatch > .45:
                continue
            row['candidates'].append({'start':bounds[0], 'end':bounds[1], 'cost':value,
                                      'readback':readback, 'mismatch':mismatch})
        del row['pool']
        del row['ids']
    return choose_path(targets), targets
