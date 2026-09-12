"""Small bilingual extraction probe; no application or project data is modified."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['TRANSFORMERS_OFFLINE'] = '1'
os.environ['HF_HOME'] = str(HERE / 'cache')
os.environ['TOKENIZERS_PARALLELISM'] = 'false'
os.environ['HF_HUB_DISABLE_PROGRESS_BARS'] = '1'
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
sys.dont_write_bytecode = True


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, path)


def verify_files():
    manifest = json.loads((ROOT / 'Инструменты/JellyModels/config.json').read_text(encoding='utf-8-sig'))
    checked = []
    for model in manifest['models']:
        for entry in model['files']:
            path = HERE / 'models' / model['directory'] / entry['filename']
            if path.stat().st_size != entry['size']:
                raise ValueError(f'Wrong size: {path}')
            if 'sha256' in entry:
                with path.open('rb') as stream:
                    checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
                expected = entry['sha256']
            else:
                data = path.read_bytes()
                checksum = hashlib.sha1(f'blob {len(data)}\0'.encode('ascii') + data).hexdigest()
                expected = entry['git_blob_sha1']
            if checksum != expected:
                raise ValueError(f'Wrong checksum: {path}')
            checked.append(dict(model=model['repository'], file=entry['filename'],
                                size=entry['size'], checksum=checksum))
            print('Verified', model['directory'], entry['filename'], flush=True)
    write_json(HERE / 'results' / 'verification.json', checked)


def probe(name):
    sys.path.insert(0, str(HERE / 'deps' / name))
    import torch
    import transformers
    torch.set_num_threads(6)
    torch.manual_seed(42)
    started = time.perf_counter()
    print('Loading', name, 'PID', os.getpid(), flush=True)
    if name == 'gliner':
        from gliner2 import AutoExtractor
        model = AutoExtractor.from_pretrained(str(HERE / 'models' / 'GLiNER2.5-Multi'),
                                             map_location='cpu')
        model.eval()
        device = 'cpu'
    else:
        from transformers import AutoTokenizer, AutoModelForImageTextToText
        directory = str(HERE / 'models' / 'NuExtract3')
        tokenizer = AutoTokenizer.from_pretrained(directory, local_files_only=True)
        model = AutoModelForImageTextToText.from_pretrained(
            directory, dtype=torch.bfloat16, local_files_only=True,
            attn_implementation='sdpa').to('cuda').eval()
        device = 'cuda'
    report = dict(model=name, pid=os.getpid(), device=device, torch=torch.__version__,
                  transformers=transformers.__version__, load_seconds=time.perf_counter()-started,
                  schema_language='English for both input languages', cases=[])
    print('Loaded in', round(report['load_seconds'], 2), 'seconds', flush=True)
    cases = json.loads((HERE / 'cases.json').read_text(encoding='utf-8'))
    for case in cases:
        started = time.perf_counter()
        row = dict(id=case['id'], lang=case['lang'], text=case['text'],
                   schema=case['schema'], expected=case['expected'])
        try:
            with torch.inference_mode():
                if name == 'gliner':
                    if case['kind'] == 'entities':
                        result = model.extract_entities(case['text'], list(case['schema']))
                        output = result.get('entities', result)
                    else:
                        output = model.classify_text(case['text'], case['schema'])
                    row['output'] = output
                else:
                    instructions = ('Extract only what the document states. Use the provided choices. '
                                    'Distinguish a character belief from what actually happened.')
                    inputs = tokenizer.apply_chat_template(
                        [{'role':'user', 'content':case['text']}],
                        template=json.dumps(case['schema'], ensure_ascii=False),
                        instructions=instructions, enable_thinking=False,
                        add_generation_prompt=True, tokenize=True, return_dict=True,
                        return_tensors='pt').to(device)
                    row['rendered_prompt'] = tokenizer.decode(inputs['input_ids'][0])
                    generated = model.generate(**inputs, max_new_tokens=160, max_time=25,
                                               do_sample=False)
                    tokens = generated[0, inputs['input_ids'].shape[1]:]
                    raw = tokenizer.decode(tokens, skip_special_tokens=True).strip()
                    row.update(raw=raw, output_tokens=len(tokens),
                               stopped_with_eos=int(tokens[-1]) in
                               [model.generation_config.eos_token_id, tokenizer.eos_token_id])
                    row['output'] = json.loads(raw)
            row['matches_expected'] = row['output'] == case['expected']
        except Exception as error:
            row['error'] = f'{type(error).__name__}: {error}'
            row['matches_expected'] = False
        row['seconds'] = time.perf_counter()-started
        report['cases'].append(row)
        write_json(HERE / 'results' / f'{name}.json', report)
        print(row['id'], json.dumps(row.get('output', row.get('error')), ensure_ascii=False),
              'PASS' if row['matches_expected'] else 'CHECK', round(row['seconds'],2), 's', flush=True)
    print('Finished', name, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['verify','gliner','nuextract'])
    arguments = parser.parse_args()
    if arguments.mode == 'verify':
        verify_files()
    else:
        probe(arguments.mode)
