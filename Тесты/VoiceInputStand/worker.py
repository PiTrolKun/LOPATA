"""One persistent CPU recognizer per process. UTF-8 JSONL protocol on stdout only."""
from pathlib import Path
import argparse, json, os, sys, threading, time, traceback, wave
import numpy as np
import psutil

LOCK = threading.Lock()
def emit(event, **data):
    with LOCK:
        print(json.dumps(dict(event=event, utc=time.time(), monotonic=time.perf_counter(), **data)), flush=True)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', required=True)
    parser.add_argument('--engine', choices=['parakeet'], required=True)
    parser.add_argument('--threads', type=int, default=4)
    args = parser.parse_args()
    data = Path(args.data)
    models = json.loads((data / 'models.json').read_text(encoding='utf-8'))
    model = models[args.engine]
    directory = Path(model['directory'])
    def monitor():
        process = psutil.Process()
        last_time = time.perf_counter()
        initial = process.cpu_times()
        last_cpu = initial.user + initial.system
        while True:
            try:
                children = process.children(recursive=True)
                current_time = time.perf_counter()
                cpu = sum(p.cpu_times().user+p.cpu_times().system for p in [process, *children])
                percent = 100*max(0,cpu-last_cpu)/max(current_time-last_time,0.001)
                emit('resources', rss=sum(p.memory_info().rss for p in [process, *children]),
                     private=sum(getattr(p.memory_info(), 'private', 0) for p in [process, *children]),
                     cpu_seconds=cpu, cpu_percent_one_core=percent, cpu_percent_machine=percent/(os.cpu_count() or 1),
                     pids=[p.pid for p in [process, *children]])
                last_time, last_cpu = current_time, cpu
            except (psutil.Error, OSError):
                pass
            time.sleep(0.5)
    threading.Thread(target=monitor, daemon=True).start()
    begin = time.perf_counter()
    emit('load_begin', engine=args.engine, revision=model['revision'], threads=args.threads, provider='cpu')
    import sherpa_onnx
    recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
        encoder=str(directory/'encoder.int8.onnx'), decoder=str(directory/'decoder.int8.onnx'),
        joiner=str(directory/'joiner.int8.onnx'), tokens=str(directory/'tokens.txt'),
        num_threads=args.threads, feature_dim=128, model_type='nemo_transducer', provider='cpu')
    emit('ready', engine=args.engine, load_ms=(time.perf_counter()-begin)*1000,
         mode='offline snapshots')
    for raw in sys.stdin:
        request = json.loads(raw)
        if request.get('command') == 'exit':
            break
        ident = request['id']
        begin = time.perf_counter()
        emit('decode_begin', id=ident, path=request['path'])
        with wave.open(request['path'], 'rb') as wav:
            assert wav.getnchannels()==1 and wav.getsampwidth()==2 and wav.getframerate()==16000
            samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype='<i2').astype(np.float32)/32768
        stream = recognizer.create_stream()
        stream.accept_waveform(16000, samples)
        recognizer.decode_stream(stream)
        emit('result', id=ident, text=stream.result.text, decode_ms=(time.perf_counter()-begin)*1000)

if __name__ == '__main__':
    try:
        main()
    except Exception:
        emit('error', traceback=traceback.format_exc())
        raise
