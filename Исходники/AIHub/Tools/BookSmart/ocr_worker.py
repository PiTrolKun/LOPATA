"""LOPATA glue for upstream OCR. No page segmentation or OCR algorithm lives here.

Usage: python ocr_worker.py --input request.json --output result.json
request: choice(paddle|ovis), sourcePath, serverUrl(loopback /v1),
layoutModelDirectory(required for Paddle), cacheDirectory, page(default 1).
Only a prepared local model/layout is allowed: this worker never installs anything.
"""
import argparse
import base64
import json
import mimetypes
import os
from pathlib import Path
import re
import tempfile
import time
import urllib.parse
import urllib.request

OVIS_PROMPT = '\nExtract all readable content from the image in natural human reading order and output the result as a single Markdown document. For charts or images, represent them using an HTML image tag: <img src="images/bbox_{left}_{top}_{right}_{bottom}.jpg" />, where left, top, right, bottom are bounding box coordinates scaled to [0, 1000). Format formulas as LaTeX. Format tables as HTML: <table>...</table>. Transcribe all other text as standard Markdown. Preserve the original text without translation or paraphrasing.'


def validate(request):
    source = Path(request['sourcePath']).resolve(strict=True)
    if not source.is_file():
        raise ValueError('sourcePath must be an existing image file')
    url = urllib.parse.urlparse(request['serverUrl'])
    if url.scheme != 'http' or url.hostname not in ('127.0.0.1', '::1', 'localhost') or url.username or url.password:
        raise ValueError('Only an owned local HTTP model server may be used')
    if request['choice'] not in ('paddle', 'ovis'):
        raise ValueError('Unknown OCR choice')
    return source, request['serverUrl'].rstrip('/'), int(request.get('page', 1))


def paddle(request, source, server_url, page, scratch):
    layout = Path(request['layoutModelDirectory']).resolve(strict=True)
    for name in ('inference.json', 'inference.pdiparams', 'inference.yml'):
        if not (layout / name).is_file():
            raise ValueError('Prepared PP-DocLayoutV3 is incomplete: ' + name)
    cache = Path(request.get('cacheDirectory', str(scratch / 'cache'))).resolve()
    os.environ['PADDLE_PDX_CACHE_HOME'] = str(cache)
    os.environ['PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK'] = 'True'
    os.environ['HF_HUB_OFFLINE'] = '1'
    os.environ['MODELSCOPE_OFFLINE'] = '1'
    from paddleocr import PaddleOCRVL
    pipeline = PaddleOCRVL(
        pipeline_version='v1.6', layout_detection_model_name='PP-DocLayoutV3',
        layout_detection_model_dir=str(layout), vl_rec_backend='llama-cpp-server',
        vl_rec_server_url=server_url, vl_rec_max_concurrency=1,
        device='cpu', use_doc_orientation_classify=False, use_doc_unwarping=False,
        markdown_ignore_labels=[])
    pages = []
    blocks = []
    issues = []
    for result in pipeline.predict(str(source)):
        result.save_to_json(str(scratch))
        result.save_to_markdown(str(scratch))
    for saved in sorted(scratch.glob('*_res.json')):
        raw = json.loads(saved.read_text(encoding='utf-8'))
        pages.append(raw)
        for block in raw.get('parsing_res_list', []):
            text = block.get('block_content') or ''
            label = block.get('block_label', 'text')
            questionable = not text.strip() and label not in ('image', 'chart')
            if '\ufffd' in text:
                questionable = True
            if questionable:
                issues.append('Empty or undecodable region: page %s, block %s' % (page, block.get('block_id')))
            blocks.append({'id': len(blocks), 'text': text, 'kind': label,
                           'questionable': questionable, 'bbox': block.get('block_bbox'),
                           'page': page, 'order': block.get('block_order')})
    return {'blocks': blocks, 'issues': issues, 'raw': pages,
            'markdown': '\n\n'.join(p.read_text(encoding='utf-8') for p in sorted(scratch.glob('*.md'))),
            'pipeline': 'paddleocr-3.6.0/PaddleOCRVL-v1.6/PP-DocLayoutV3',
            'confidenceAvailable': False}


def ovis(request, source, server_url, page, scratch):
    mime = mimetypes.guess_type(str(source))[0] or 'image/png'
    payload = {'messages': [{'role': 'user', 'content': [
        {'type': 'image_url', 'image_url': {'url': 'data:' + mime + ';base64,' + base64.b64encode(source.read_bytes()).decode('ascii')}},
        {'type': 'text', 'text': OVIS_PROMPT}]}], 'temperature': 0,
        'max_tokens': 16384, 'stream': False, 'chat_template_kwargs': {'enable_thinking': False}}
    req = urllib.request.Request(server_url + '/chat/completions', json.dumps(payload).encode('utf-8'), {'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=600) as response:
        raw = json.load(response)
    choice = raw['choices'][0]
    text = choice['message']['content']
    truncated = choice.get('finish_reason') != 'stop'
    # Preserve the original output including footnotes, HTML tables and image tags.
    # No regex-based "repair" of the author's wording or invented confidence scores.
    blocks = [{'id': 0, 'text': text, 'kind': 'markdown', 'page': page,
               'questionable': truncated or '\ufffd' in text, 'bbox': None}]
    issues = ['Recognition stopped before the end of the page'] if truncated else []
    return {'blocks': blocks, 'issues': issues, 'raw': raw, 'markdown': text,
            'pipeline': 'OvisOCR2/page/llama.cpp/community-GGUF', 'confidenceAvailable': False}


def preserve_visual_regions(result, source, page, output):
    """Crop only upstream-provided image boxes; never infer a new page layout."""
    regions = []
    for block in result['blocks']:
        if block['kind'] in ('image', 'chart') and block.get('bbox'):
            regions.append((block['bbox'], 'pixels', block['id']))
    for index, coords in enumerate(re.findall(r'<img\s+src="images/bbox_(\d+)_(\d+)_(\d+)_(\d+)\.jpg"\s*/?>', result.get('markdown', ''))):
        regions.append(([int(v) for v in coords], 'normalized', 0))
    if not regions:
        result['assets'] = []
        return
    from PIL import Image
    assets = []
    with Image.open(source) as original:
        width, height = original.size
        for region_index, (box, unit, block_id) in enumerate(regions):
            left, top, right, bottom = box
            if unit == 'normalized':
                left, right = left * width / 1000, right * width / 1000
                top, bottom = top * height / 1000, bottom * height / 1000
            box = [max(0, min(width, round(left))), max(0, min(height, round(top))),
                   max(0, min(width, round(right))), max(0, min(height, round(bottom)))]
            if box[2] <= box[0] or box[3] <= box[1]:
                result['issues'].append('Invalid visual region returned by OCR')
                continue
            directory = output.parent / (output.stem + '-assets')
            directory.mkdir(exist_ok=True)
            path = directory / ('page-%d-block-%d-region-%d.png' % (page, block_id, region_index))
            original.crop(tuple(box)).convert('RGB').save(path, 'PNG')
            assets.append({'path': str(path), 'bbox': box, 'page': page, 'blockId': block_id})
    result['assets'] = assets


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    request = json.loads(Path(args.input).read_text(encoding='utf-8-sig'))
    started = time.perf_counter()
    try:
        source, server_url, page = validate(request)
        with tempfile.TemporaryDirectory(prefix='ocr-', dir=str(output.parent)) as temp:
            scratch = Path(temp)
            result = (paddle if request['choice'] == 'paddle' else ovis)(request, source, server_url, page, scratch)
            preserve_visual_regions(result, source, page, output)
        result.update({'schemaVersion': 1, 'status': 'ok', 'seconds': time.perf_counter() - started})
    except Exception as error:
        result = {'schemaVersion': 1, 'status': 'error', 'error': str(error), 'blocks': [],
                  'issues': [str(error)], 'seconds': time.perf_counter() - started}
    pending = output.with_suffix(output.suffix + '.pending')
    pending.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    pending.replace(output)
    if result['status'] != 'ok':
        raise SystemExit(1)


if __name__ == '__main__':
    main()
