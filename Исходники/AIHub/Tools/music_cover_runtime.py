"""Small offline input adapter to the pinned ACE API, not an inference replacement."""
import hashlib
from pathlib import Path
import types


def verify_inputs(task):
    if not task:
        return
    inputs = list(task.get('references', []))
    if task.get('source'):
        inputs.append(task['source'])
    for item in inputs:
        path = Path(item['Path'])
        with path.open('rb') as stream:
            actual = hashlib.file_digest(stream, 'sha256').hexdigest()
        if actual.lower() != item['Sha256'].lower():
            raise ValueError('Music.Transform.InputChanged')


def attach_references(handler, task):
    """The public request accepts one file; the official embedding path accepts N per song."""
    if not task or not task.get('references'):
        return []
    references = task['references']
    prepared = []
    for item in references:
        audio = handler.process_reference_audio(item['Path'])
        if audio is None:
            raise ValueError('Music.Transform.SilentInput')
        prepared.append(audio)
    # Official infer_refer_latent packs tensors on batch dimension; the standard
    # processor returns 30 s. Reject differing lengths rather than drop examples.
    if any(a.shape != prepared[0].shape for a in prepared):
        raise ValueError('Reference windows must have the same prepared shape.')
    original = handler._prepare_reference_and_source_audio

    def prepare(this, *args, **kwargs):
        refs, source, error = original(*args, **kwargs)
        if error is not None:
            return refs, source, error
        batch = kwargs.get('actual_batch_size', args[3] if len(args) > 3 else 1)
        return [list(prepared) for _ in range(batch)], source, None

    handler._prepare_reference_and_source_audio = types.MethodType(prepare, handler)
    return [{'sha256': i['Sha256'], 'role': i['Role'], 'method': 'official packed timbre conditioning'} for i in references]


def public_request(request):
    result = dict(request)
    result['params'] = {k: v for k, v in request['params'].items() if k not in ('src_audio', 'reference_audio')}
    if result.get('audio_task'):
        result['audio_task'] = result['audio_task']['public_receipt']
    result['loras'] = [{k: v for k, v in item.items() if k != 'directory'} for item in request.get('loras', [])]
    return result
