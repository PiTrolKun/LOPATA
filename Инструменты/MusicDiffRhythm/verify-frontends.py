import hashlib, json, os, pathlib, shutil, sys, urllib.request
import argparse
parser=argparse.ArgumentParser();parser.add_argument('--stage',required=True);args=parser.parse_args()
root=pathlib.Path(args.stage).resolve();workspace=pathlib.Path(__file__).resolve().parents[2]
stage = root/'frontend-source'
shutil.copytree(root/'source',stage,dirs_exist_ok=True,ignore=shutil.ignore_patterns('__pycache__'))
manifest = json.loads((root/'models.json').read_text())
file = next(f for f in manifest['Companions'] if f['RelativePath'].endswith('.onnx'))
target = stage/file['RelativePath']
if not target.is_file():
    with urllib.request.urlopen(file['SourceUrl'],timeout=90) as response, target.open('wb') as out:
        shutil.copyfileobj(response,out)
assert target.stat().st_size == file['SizeBytes']
assert hashlib.sha256(target.read_bytes()).hexdigest() == file['Sha256']
os.environ['HF_HUB_OFFLINE']='1'
os.environ['TRANSFORMERS_OFFLINE']='1'
sys.path[:0]=[str(workspace/'Исходники/AIHub/Tools'),str(stage),str(root/'overlay')]
from diffrhythm2.utils import CNENTokenizer,parse_lyrics
from music_diffrhythm_frontend import ExperimentalFrontend,load_author_tokenizer
front = ExperimentalFrontend(stage,CNENTokenizer)
lyrics = '''Ночью мастер сел за стол,
Старый ящик вдруг ожил.
Он ЛАПАТУ в нём завёл —
И экран заговорил.
Эй, ЛАПАТА, глубже рой!
Под железом клад зарой!
Текст и музыку достань —
И машине волю дай!
В ящик бросил он рассказ,
Пару строчек и портрет.
А ЛАПАТА в тот же час
Прошептала: «Вот ответ».
Только мастер отвернулся —
Что-то грохнуло внутри.
Монитор слегка качнулся:
«Это тест номер три!»
До рассвета шёл их спор,
Кто умнее — человек?
Но ЛАПАТА с этих пор
Роет данные вовек.'''
for text in lyrics.splitlines():
    tokens=front.encode(text)
    assert len(tokens)>0 and all(1<=n<500 for n in tokens)
assert len(front.rows)==20
print('Russian: 20 lines, all phonemes accounted for',flush=True)
for text in ['Music in the night.', '你好世界。']:
    expected=load_author_tokenizer(CNENTokenizer).encode(text)
    actual=front.encode(text)
    assert expected==actual
    print('Author frontend preserved:',text,len(actual),flush=True)
try:
    front._parts('☃')
    raise AssertionError('Unknown phoneme silently accepted')
except ValueError: pass
try:
    ExperimentalFrontend(stage,CNENTokenizer,False).encode('ЛАПАТА')
    raise AssertionError('Disabled Russian accepted')
except ValueError: pass
tokens=parse_lyrics(front,'[verse]\nЛАПАТА роет\n[chorus]\nИ машине волю дай!')
assert any(503 in line for line in tokens) and any(504 in line for line in tokens)
(root/'frontend-receipt.json').write_text(json.dumps(front.rows,ensure_ascii=False,indent=2),encoding='utf-8')
print('Unsupported phones rejected; Russian disable respected; author section parser passed',flush=True)
