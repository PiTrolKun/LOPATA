"""One-time migration for this PC; production code never references the old private stand."""
import pathlib,json,os,shutil,hashlib,urllib.request,concurrent.futures
root=pathlib.Path(__file__).resolve().parents[2]
stand=root/'Runtime/VideoUpscaleStand'
app=root/'Исходники/AIHub/Tools/VideoUpscale'
settings=json.loads((pathlib.Path(os.environ['LOCALAPPDATA'])/'AI_HUB/storage-settings.json').read_text(encoding='utf-8-sig'))
models=pathlib.Path(settings['models']['locations'][0]['path'])/'VideoUpscale'
def digest(path):
    sha=hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda:f.read(8*1024*1024),b''):sha.update(chunk)
    return sha.hexdigest()
def install(folder,item,source=None):
    path=folder/item['RelativePath']; path.parent.mkdir(parents=True,exist_ok=True)
    if path.exists() and path.stat().st_size==item['SizeBytes'] and digest(path)==item['Sha256']:return
    temp=path.with_name(path.name+'.migration-partial')
    if temp.exists() and temp.stat().st_size==item['SizeBytes'] and digest(temp)==item['Sha256']:
        temp.replace(path); print('READY',path.name,flush=True);return
    if source:
        if source.stat().st_size!=item['SizeBytes'] or digest(source)!=item['Sha256']:raise ValueError(source)
        shutil.copyfile(source,temp)
    else:
        print('DOWNLOAD',path.name,flush=True)
        with urllib.request.urlopen(item['SourceUrl'],timeout=120) as response,temp.open('wb') as out:shutil.copyfileobj(response,out,1024*1024)
    if temp.stat().st_size!=item['SizeBytes'] or digest(temp)!=item['Sha256']:raise ValueError(path)
    temp.replace(path);print('READY',path.name,flush=True)
runtime=models/'video-upscale-flash-runtime/python-3.10.18-torch-2.7-cu128-1'
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    list(pool.map(lambda f:install(runtime,f),json.loads((app/'runtime.json').read_text())))
install(models/'video-upscale-flash-source/ac60d1e3a5dc0a6a24c4057b672e22b588145cce',dict(RelativePath='source.zip',SizeBytes=17340225,Sha256='15804913d2429097a98c22e43b7268d798cc0757febd7752daf513f70fde131c'),stand/'downloads/flashvsr-t8-source.zip')
for quant,revision,role in [('int8','56c806f1357d90c8a0b4b054e21bcefe3cb9ac1d','turbo_int8_convrot'),('float','46b79520946ac67dec94d1566490690953c7debc','turbo_dit')]:
    for item in json.loads((app/'weights.json').read_text()):
        if item['Role'] not in [role,'turbo_lq','tiny']:continue
        source=stand/'tools/flashvsr-t8/ckpts'/item['RelativePath']
        if not source.exists():source=stand/'tools/flashvsr-t8/ckpts-int8'/item['RelativePath']
        install(models/f'video-upscale-flash-turbo-{quant}'/revision,item,source)
asset=next(a for a in json.loads((stand/'assets.json').read_text()) if a['repo']=='Merserk/dlss5-visual-enhancer')
install(models/'video-upscale-visual-enhancer/21.0',dict(RelativePath=asset['asset'],SizeBytes=asset['size'],Sha256=asset['sha256']),stand/'downloads'/asset['asset'])
print('COMPONENTS SEEDED',models,flush=True)
