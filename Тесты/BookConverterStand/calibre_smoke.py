"""Synthetic standalone converter corpus; does not touch user documents or user calibre profiles."""
from pathlib import Path
import base64,hashlib,json,os,struct,subprocess,time,zipfile,zlib
ROOT=Path(__file__).resolve().parents[2]
STAND=Path(__file__).resolve().parent
DATA=STAND/'corpus'; OUTPUT=STAND/'calibre-output'
EXE=ROOT/'Runtime/BookConverter/calibre-9.16.0/Calibre Portable/Calibre/ebook-convert.exe'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def png():
 def chunk(tag,data): return struct.pack('>I',len(data))+tag+data+struct.pack('>I',zlib.crc32(tag+data))
 return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',32,32,8,2,0,0,0))+chunk(b'IDAT',zlib.compress((b'\x00'+bytes([30,100,210])*32)*32))+chunk(b'IEND',b'')
def fixture():
 DATA.mkdir(exist_ok=True); OUTPUT.mkdir(exist_ok=True)
 image=png();(DATA/'illustration.png').write_bytes(image)
 (DATA/'rich.fb2').write_text('''<?xml version="1.0" encoding="utf-8"?><FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink"><description><title-info><genre>prose_contemporary</genre><author><first-name>Анна</first-name><last-name>Проверкина</last-name></author><book-title>Книга проверки — ёж и звёзды</book-title><annotation><p>Контрольная аннотация.</p></annotation><lang>ru</lang></title-info><document-info><author><nickname>Stand</nickname></author><date>2026-10-19</date><id>lopata-stand-001</id><version>1.0</version></document-info></description><body><section id="chapter-one"><title><p>Глава 1. Начало</p></title><p>Редкий ёж бережёт звёзды. Ω — 𝄞 — 中文.</p><p>Текст со <emphasis>значимым выделением</emphasis> и <a l:href="#note-1" type="note">примечанием</a>.</p><image l:href="#image-one"/><poem><stanza><v>Первая строка стихотворения,</v><v>Вторая строка без изменения.</v></stanza></poem><table><tr><td>Колонка А</td><td>Колонка Б</td></tr><tr><td>Число 12</td><td>Число 34</td></tr></table></section><section id="chapter-two"><title><p>Глава 2. Продолжение</p></title><p>Контрольный конец произведения.</p></section></body><body name="notes"><section id="note-1"><title><p>1</p></title><p>Важное примечание о сохранности.</p></section></body><binary id="image-one" content-type="image/png">'''+base64.b64encode(image).decode()+'''</binary></FictionBook>''',encoding='utf-8')
 # DOCX created by the same official converter so no additional fixture-generation dependency.
 run(DATA/'rich.fb2',DATA/'rich.docx')
 (DATA/'plain.txt').write_text('Глава первая\n\nЁж бережёт звёзды.\n\nГлава вторая\n\nКонтрольный конец произведения.\n',encoding='utf-8')
 (DATA/'marked.md').write_text('# Начало\n\nТекст **жирный**, *курсивный*, ёж и звёзды.\n\n## Продолжение\n\nКонтрольный конец произведения.\n',encoding='utf-8')
def run(source,target):
 env=dict(os.environ,CALIBRE_CONFIG_DIRECTORY=str(STAND/'calibre-config'),CALIBRE_TEMP_DIR=str(STAND/'temp'),CALIBRE_WORKER_TEMP_DIR=str(STAND/'temp'))
 (STAND/'temp').mkdir(exist_ok=True)
 started=time.monotonic()
 p=subprocess.run([str(EXE),str(source),str(target),'--language','ru'],capture_output=True,env=env,timeout=180)
 (OUTPUT/(target.name+'.log')).write_bytes(p.stdout+b'\n'+p.stderr)
 return {'input':source.name,'output':target.name,'exitCode':p.returncode,'seconds':round(time.monotonic()-started,2),'bytes':target.stat().st_size if target.exists() else 0}
def main():
 fixture();before={p.name:sha(p) for p in DATA.iterdir() if p.is_file()}; results=[]
 for name,extension in [('rich.docx','fb2'),('rich.docx','epub'),('rich.fb2','docx'),('rich.fb2','epub'),('plain.txt','epub'),('rich.docx','pdf'),('rich.fb2','azw3'),('rich.fb2','mobi'),('rich.fb2','rtf')]:
  results.append(run(DATA/name,OUTPUT/(name.replace('.','_')+'.'+extension)))
 epub=OUTPUT/'rich_fb2.epub'
 if epub.exists(): results.append(run(epub,OUTPUT/'reverse.epub.docx'))
 after={p.name:sha(p) for p in DATA.iterdir() if p.is_file()}
 result={'version':'calibre9.16.0','sourceUnchanged':before==after,'sourcesSha256':before,'results':results,'allPassed':all(x['exitCode']==0 and x['bytes']>0 for x in results)}
 (STAND/'calibre-smoke-result.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps(result,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
