"""Drive the author's unchanged Lunarc render queue without opening its UI."""
import argparse
import json
import os
import pathlib
import sys
import time


parser = argparse.ArgumentParser()
parser.add_argument('request', type=pathlib.Path)
args = parser.parse_args()
request = json.loads(args.request.read_text(encoding='utf-8'))
resources = pathlib.Path(request['resources'])
os.environ['LUNARC_DATA_DIR'] = request['data_dir']
os.environ['LUNARC_DLSSNR_DLL'] = request['nr_dll']
pathlib.Path(request['data_dir']).mkdir(parents=True, exist_ok=True)
os.environ['PATH'] = str(resources / 'native-deps') + os.pathsep + request['ffmpeg_dir'] + os.pathsep + os.environ['PATH']
dependency_directory = os.add_dll_directory(str(resources / 'native-deps'))
sys.path.insert(0, str(resources))
from dlss_service import DLSSService

service = DLSSService()
service.add_job({'input_path': request['input'], 'output_path': request['output'],
    'codec': 'av1_nvenc', 'container': 'mkv', 'cq': 14, 'preset': 'p7',
    'with_audio': True, 'total_frames': request['frames'], 'settings': request['settings']})
service.start_queue()
deadline = time.monotonic() + 180
while service._queue_thread.is_alive():
    if time.monotonic() > deadline:
        raise TimeoutError('Author render queue exceeded 180 seconds')
    time.sleep(.2)
result = service.jobs[0]
pathlib.Path(request['report']).write_text(json.dumps(result, ensure_ascii=False, indent=2, default=str), encoding='utf-8')
print(json.dumps(result, ensure_ascii=False, default=str), flush=True)
if result['status'] != 'completed':
    sys.exit(1)
