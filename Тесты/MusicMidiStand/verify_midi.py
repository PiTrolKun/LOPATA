"""Independent SMF wire reader for the exported fixture; no DryWetMIDI code/imports."""
import json, struct, sys
from pathlib import Path

data = Path(sys.argv[1]).read_bytes()
assert data[:4] == b'MThd'
length, kind, tracks, ppq = struct.unpack('>IHHH', data[4:14])
assert length == 6 and kind == 0 and tracks == 1 and ppq == 480
assert data[14:18] == b'MTrk'
size = struct.unpack('>I', data[18:22])[0]
assert size == len(data)-22
at, tick, running, active, notes, meta = 22, 0, 0, {}, [], []

def varint():
    global at
    value = 0
    for _ in range(4):
        b = data[at]; at += 1; value = value*128+(b&127)
        if not b&128: return value
    raise AssertionError('oversized varint')

while at < len(data):
    tick += varint()
    status = data[at]
    if status&128: at += 1
    else: status = running
    if status == 255:
        typ = data[at]; at += 1
        n = varint(); body = data[at:at+n]; at += n
        if typ == 1: meta.append(json.loads(body.decode('utf8')))
        if typ == 81: assert int.from_bytes(body,'big') == 600000
        continue
    assert 128 <= status <= 239
    running = status
    n = 1 if status&240 in (192,208) else 2
    values = data[at:at+n]; at += n
    if status&240 == 144 and values[1]: active[(status&15,values[0])] = tick
    elif status&240 in (128,144):
        start = active.pop((status&15,values[0]))
        notes.append((values[0],start,tick-start))

assert not active and len(notes) == 16 and tick == 7680
assert [n[0] for n in notes] == [60,64,67,64,62,65,69,67,64,67,72,71,69,67,64,60]
assert all(n[2] == 480 for n in notes)
assert meta[0]['app'] == 'LOPATA' and meta[0]['version'] and meta[0]['operation'] == 'music.melody.export' and meta[0]['created']
print(json.dumps({'independent_smf_reader':True,'notes':len(notes),'ppq':ppq,'metadata':meta[0]},ensure_ascii=False,indent=2))
