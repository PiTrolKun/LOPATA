"""Sequential, resumable PCM assembly. No full-length waveform is retained in memory."""
import hashlib
import json
import os
from pathlib import Path
import uuid
import wave
from text_chunks import LIMITS, PLAN_VERSION, plan, units


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def owned(root, name):
    path = root / name
    if path.is_symlink() or path.resolve().parent != root:
        raise ValueError('Unsafe narrator checkpoint path.')
    return path


def atomic_json(path, data):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.partial')
    temporary.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    os.replace(temporary, path)


def inspect_audio(path):
    with wave.open(str(path)) as audio:
        if audio.getsampwidth() != 2 or audio.getcomptype() != 'NONE' or audio.getnframes() <= 0:
            raise ValueError('A narrator packet returned invalid PCM16 audio.')
        expected = audio.getnframes()
        frames = 0
        while block := audio.readframes(65536):
            frames += len(block) // (2 * audio.getnchannels())
        if frames != expected:
            raise ValueError('A narrator packet contains truncated PCM data.')
        return dict(frames=expected, rate=audio.getframerate(), channels=audio.getnchannels())


def wrap(data, synthesize, execution, progress=lambda fraction: None):
    def generate(output):
        output = os.path.abspath(output)
        if os.name == 'nt' and not output.startswith('\\\\?\\'):
            output = '\\\\?\\UNC\\' + output[2:] if output.startswith('\\\\') else '\\\\?\\' + output
        output = Path(output)
        original = data['text']
        packets = list(plan(original, data['engine']))
        fingerprint = hashlib.sha256(json.dumps(dict(request=data, planner=PLAN_VERSION), sort_keys=True,
            ensure_ascii=False).encode('utf-8')).hexdigest()
        directory = output.parent / 'chunks' / fingerprint
        if os.path.normcase(os.path.abspath(directory)) != os.path.normcase(str(directory.resolve())):
            raise ValueError('Unsafe narrator checkpoint directory.')
        directory.mkdir(parents=True, exist_ok=True)
        root = directory.resolve()
        if os.path.normcase(os.path.abspath(directory)) != os.path.normcase(str(root)):
            raise ValueError('Unsafe narrator checkpoint directory.')
        partial = owned(output.parent.resolve(), 'assembly-' + uuid.uuid4().hex + '.partial.wav')
        writer = None
        output_format = None
        frames = reused = 0
        trace = owned(output.parent.resolve(), 'chunks.jsonl')
        try:
            with trace.open('w', encoding='utf-8') as journal:
                for index, (start, end, text) in enumerate(packets):
                    receipt_path = owned(root, f'{index:06}.json')
                    audio_path = owned(root, f'{index:06}.wav')
                    text_hash = hashlib.sha256(text.encode('utf-8')).hexdigest()
                    identity = dict(index=index, start=start, end=end, textSha256=text_hash)
                    receipt = None
                    if text.strip():
                        if data.get('repeats', 1) == 1 and receipt_path.is_file() and audio_path.is_file():
                            try:
                                candidate = json.loads(receipt_path.read_text(encoding='utf-8'))
                                if all(candidate.get(k) == v for k, v in identity.items()) and candidate['sha256'] == digest(audio_path):
                                    facts = inspect_audio(audio_path)
                                    if all(candidate.get(k) == v for k, v in facts.items()):
                                        receipt = candidate
                                        reused += 1
                            except (ValueError, OSError, KeyError, EOFError, wave.Error):
                                pass  # Invalid generated checkpoint: regenerate this packet, never bypass a component audit.
                        cached = receipt is not None
                        if receipt is None:
                            data['text'] = text
                            temporary = owned(root, f'{index:06}-{uuid.uuid4().hex}.partial.wav')
                            synthesize(str(temporary))
                            facts = inspect_audio(temporary)
                            receipt = dict(identity, **facts, sha256=digest(temporary))
                            os.replace(temporary, audio_path)
                            atomic_json(receipt_path, receipt)
                        with wave.open(str(audio_path)) as source:
                            current = (source.getnchannels(), source.getsampwidth(), source.getframerate())
                            if writer is None:
                                writer = wave.open(str(partial), 'wb')
                                writer.setnchannels(current[0]); writer.setsampwidth(current[1]); writer.setframerate(current[2])
                                output_format = current
                            if current != output_format:
                                raise ValueError('A narrator changed audio format between packets.')
                            remaining = source.getnframes()
                            while remaining:
                                block = source.readframes(min(65536, remaining))
                                count = len(block) // (current[0] * current[1])
                                if not count:
                                    raise ValueError('A narrator checkpoint contains truncated audio.')
                                if (frames + count) * current[0] * current[1] > 0xffffffff - 44:
                                    raise ValueError('Narration exceeds the WAV container size; use separate outputs.')
                                writer.writeframesraw(block)
                                frames += count
                                remaining -= count
                        journal.write(json.dumps(dict(receipt, reused=cached, inputUnits=units(text))) + '\n')
                        journal.flush()
                    else:
                        journal.write(json.dumps(dict(identity, frames=0, whitespace=True)) + '\n')
                        journal.flush()
                    progress(end / len(original))
            if writer is None or not frames:
                raise ValueError('The narrator returned no audio.')
            writer.close(); writer = None
            if inspect_audio(partial)['frames'] != frames:
                raise ValueError('Incomplete narrator assembly.')
            os.replace(partial, output)
            execution.actual['chunking'] = dict(inputChars=len(original), textSha256=hashlib.sha256(original.encode('utf-8')).hexdigest(),
                packets=len(packets), reused=reused, inputBudget=LIMITS[data['engine']], frames=frames, planVersion=PLAN_VERSION)
        finally:
            data['text'] = original
            if writer is not None:
                writer.close()
    close = getattr(synthesize, 'close', None)
    if close is not None:
        generate.close = close
    return generate
