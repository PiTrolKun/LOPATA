"""Call the author's file converter and profiles; do not launch desktop capture."""
import argparse
import dataclasses
import json
import pathlib
import sys


parser = argparse.ArgumentParser()
parser.add_argument('request', type=pathlib.Path)
args = parser.parse_args()
request = json.loads(args.request.read_text(encoding='utf-8'))
sys.path.insert(0, str(pathlib.Path(request['installation']) / 'app'))
from media_convert import convert_video
from settings_io import PROFILES

params = dict(PROFILES[request['profile']])
params.update(request.get('parameter_overrides', {}))
result = convert_video(
    pathlib.Path(request['input']), pathlib.Path(request['output']), params,
    work_scale=request['work_scale'], nr_small=request['nr_small'],
    nr_passes=request['passes'], flow_preset='fast',
    copy_audio=request.get('copy_audio', True), codec='av1', quality='high')
report = dict(result=dataclasses.asdict(result), parameters=params,
              requested=request)
pathlib.Path(request['report']).write_text(
    json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, default=str), flush=True)
if result.skipped or result.audio == 'dropped':
    raise RuntimeError('Author converter skipped frames or dropped audio; see report')
