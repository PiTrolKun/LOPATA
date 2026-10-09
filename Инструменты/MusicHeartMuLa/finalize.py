"""Deliver the verified staging metadata and retain upstream license notices."""
import argparse, hashlib, json, shutil, zipfile
from pathlib import Path
parser=argparse.ArgumentParser(); parser.add_argument('--stage',required=True); args=parser.parse_args()
stage=Path(args.stage).resolve(); root=Path(__file__).resolve().parents[2]
app=root/'Исходники/AIHub'; texts=app/'Licenses/texts'
source=stage/'source'
files={p.relative_to(source).as_posix():hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in sorted(source.rglob('*')) if p.is_file() and p.name!='manifest.json'}
manifest=json.dumps({'Revision':'5858ca8f1ffe58d62be7c007ea55f1033c5ca456','Files':files},ensure_ascii=False,indent=2).encode()
(source/'manifest.json').write_bytes(manifest)
bundle=root/'Runtime/MusicHeartMuLa'; bundle.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(bundle/'source.zip','w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
 for p in sorted(source.rglob('*')):
  if p.is_file():
   info=zipfile.ZipInfo(p.relative_to(source).as_posix(),(2026,10,8,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; z.writestr(info,p.read_bytes())
print('Source archive SHA256',hashlib.sha256((bundle/'source.zip').read_bytes()).hexdigest().upper())
for name,target in [('models.json','heartmula-models.json'),('python-files.json','heartmula-python-files.json')]:
 shutil.copy2(stage/name,app/'Tools'/target)
shutil.copy2(stage/'source/LICENSE',texts/'heartmula-Apache-2.0.txt')
notices=['texts/heartmula-Apache-2.0.txt']; packages=[]
plan=json.loads((stage/'pip-report.json').read_text(encoding='utf-8'))
for row in plan['install']:
 if row['metadata']['name'].lower()=='torch': continue
 packages.append({'name':row['metadata']['name'],'version':row['metadata']['version'],'license':row['metadata'].get('license_expression',row['metadata'].get('license','declared in original notices'))})
for wheel in sorted((stage/'wheels').glob('*.whl')):
 with zipfile.ZipFile(wheel) as z:
  for entry in z.infolist():
   if entry.is_dir() or not any(word in entry.filename.rsplit('/',1)[-1].upper() for word in ('LICENSE','LICENCE','COPYING','NOTICE')): continue
   data=z.read(entry)
   try: data.decode('utf-8')
   except UnicodeDecodeError: continue
   name='heartmula-'+hashlib.sha256(data).hexdigest()[:16]+'-'+entry.filename.rsplit('/',1)[-1].replace(':','_')+'.txt'
   (texts/name).write_bytes(data)
   notices.append('texts/'+name)
(texts/'heartmula-PACKAGES.json').write_text(json.dumps(packages,ensure_ascii=False,indent=2),encoding='utf-8')
notices.append('texts/heartmula-PACKAGES.json')
revision='5858ca8f1ffe58d62be7c007ea55f1033c5ca456'
entries=[]
for id,name,license,terms in [
 ('music-heartmula3b','HeartMuLa 3B happy-new-year','Apache-2.0',['texts/heartmula-Apache-2.0.txt']),
 ('music-heartmula-codec','HeartCodec 20260123','Apache-2.0',['texts/heartmula-Apache-2.0.txt']),
 ('runtime-music-heartmula','HeartMuLa Python libraries','Apache-2.0; MIT; BSD; upstream package notices',sorted(set(notices)))]:
 entries.append(dict(Id=id,Name=name,Version=revision,Author='HeartMuLa Team and package authors',License=license,
  Source='https://github.com/HeartMuLa/heartlib/tree/'+revision,Checked='2026-10-10',Basic=False,Delivery='download',Terms='',Texts=terms,
  Ru='Закреплённый официальный конвейер и веса; условия Apache-2.0 заявлены авторами. Библиотеки скачиваются отдельно, их оригинальные уведомления сохранены. Адаптер Windows: eager вместо torch.compile, освобождение памяти по устройству и запись WAV через soundfile. Сэмплер и декодер авторские. Качество русского и расход памяти требуют практического теста.',
  En='Pinned official pipeline and weights; the authors declare Apache-2.0. Libraries are downloaded separately with original notices retained. Windows adapter: eager instead of torch.compile, device-aware release and soundfile WAV writing. The author sampler and codec remain unchanged. Russian quality and memory use need practical testing.'))
(stage/'license-entries.json').write_text(json.dumps(entries,ensure_ascii=False,indent=2),encoding='utf-8')
print('Delivered manifests; wheel notices',len(set(notices)),'packages',len(packages))
