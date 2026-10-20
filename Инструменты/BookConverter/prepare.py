"""Maintainer-only: prepare exact official calibre binaries, notices and matching source; no system installation."""
from pathlib import Path
import hashlib, json, lzma, shutil, struct, sys, urllib.request, zipfile, zlib

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / 'Runtime/BookConverter'
DEST = RUNTIME / 'calibre-9.16.0'
DOWNLOADS = RUNTIME / 'downloads'
RELEASE = 'https://github.com/kovidgoyal/calibre/releases/download/v9.16.0/'
ARTIFACTS = {
 'calibre-portable-installer-9.16.0.exe': ('be3b24228133ec64148acf5e6a3244dbc88a9b509ae8992b7922c425da0f1aae',217118472),
 'calibre-9.16.0.tar.xz': ('9948a9aba2a4ee1f458aa791ca6308528017f9270fb2980fdf01637967f7c9e5',50532360),
}

def digest(path):
 with path.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()

def prepare():
 DOWNLOADS.mkdir(parents=True,exist_ok=True)
 for name,(sha,size) in ARTIFACTS.items():
  path=DOWNLOADS/name
  if not path.exists() or path.stat().st_size!=size or digest(path)!=sha:
   part=path.with_suffix(path.suffix+'.part')
   with urllib.request.urlopen(RELEASE+name) as src,part.open('wb') as dst:
    shutil.copyfileobj(src,dst)
   if part.stat().st_size!=size or digest(part)!=sha: raise ValueError('Official package checksum mismatch: '+name)
   part.replace(path)
  print('VERIFIED',name,flush=True)
 if not DEST.exists():
  data=(DOWNLOADS/'calibre-portable-installer-9.16.0.exe').read_bytes()[244040:244040+216858487]
  if data[:6]!=b'LZIP\x00\x17': raise ValueError('Unexpected pinned resource')
  decoder=lzma.LZMADecompressor(format=lzma.FORMAT_RAW,filters=[{'id':lzma.FILTER_LZMA1,'dict_size':8388608,'lc':3,'lp':0,'pb':2}])
  unpacked=decoder.decompress(data[6:])
  crc,size=struct.unpack('<IQ',data[-12:])
  if len(unpacked)!=size or zlib.crc32(unpacked)!=crc: raise ValueError('Portable payload CRC mismatch')
  temporary=DOWNLOADS/'portable.zip'
  temporary.write_bytes(unpacked)
  stage=RUNTIME/'calibre-9.16.0.preparing'
  if stage.exists(): raise RuntimeError('Inspect incomplete preparation before retrying: '+str(stage))
  stage.mkdir()
  with zipfile.ZipFile(temporary) as z:
   if z.testzip() is not None: raise ValueError('ZIP CRC mismatch')
   for entry in z.infolist():
    out=(stage/entry.filename).resolve()
    if not out.is_relative_to(stage.resolve()) or (entry.external_attr>>16)&0xf000==0xa000: raise ValueError('Unsafe archive path')
   z.extractall(stage)
  stage.replace(DEST)
  temporary.unlink()
 shutil.copy2(DOWNLOADS/'calibre-9.16.0.tar.xz',DEST/'calibre-9.16.0.tar.xz')
 licenses=ROOT/'Исходники/AIHub/Licenses/texts'
 for name in ['LICENSE','COPYRIGHT']:
  src='https://raw.githubusercontent.com/kovidgoyal/calibre/v9.16.0/'+name
  text=urllib.request.urlopen(src).read()
  output=licenses/('books-calibre-'+name+'.txt')
  if output.exists() and output.read_bytes()!=text: raise RuntimeError('Back up changed license before replacing: '+str(output))
  output.write_bytes(text)
  if name=='COPYRIGHT': (DEST/'COPYRIGHT').write_bytes(text)
 files=[{'Name':f.relative_to(DEST).as_posix(),'Bytes':f.stat().st_size,'Sha256':digest(f)} for f in sorted((DEST/'Calibre Portable').rglob('*')) if f.is_file()]
 manifest={'Version':'9.16.0','ReleaseDate':'2026-10-09','Source':'https://github.com/kovidgoyal/calibre/releases/tag/v9.16.0','InstallerSha256':ARTIFACTS['calibre-portable-installer-9.16.0.exe'][0],'SourceArchiveSha256':ARTIFACTS['calibre-9.16.0.tar.xz'][0],'Files':files}
 text=json.dumps(manifest,ensure_ascii=False,indent=2)
 (DEST/'provenance.json').write_text(text,encoding='utf-8')
 output=ROOT/'Исходники/AIHub/Tools/book-converter-files.json'
 if output.exists() and output.read_text(encoding='utf-8')!=text: raise RuntimeError('Back up changed manifest before replacing: '+str(output))
 output.write_text(text,encoding='utf-8')
 print('READY',DEST,'files=',len(files),flush=True)

if __name__=='__main__':
 if '--prepare' not in sys.argv: raise SystemExit('Explicit maintainer preparation required: python prepare.py --prepare')
 prepare()
