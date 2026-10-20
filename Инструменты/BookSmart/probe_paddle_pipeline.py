import os,sys,time,json,traceback
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];RUNTIME=ROOT/'Runtime'/'BookSmartStand';OUT=ROOT/'Тесты'/'BookSmartStand'
os.environ['PADDLE_PDX_CACHE_HOME']=str(RUNTIME/'paddle-cache')
os.environ['PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK']='True'
os.environ['HF_HOME']=str(RUNTIME/'hf-cache')
os.environ['MODELSCOPE_CACHE']=str(RUNTIME/'modelscope-cache')
from paddleocr import PaddleOCRVL
start=time.perf_counter()
try:
 p=PaddleOCRVL(pipeline_version='v1.6',vl_rec_backend='llama-cpp-server',vl_rec_server_url=sys.argv[1],device='cpu',use_doc_orientation_classify=False,use_doc_unwarping=False,markdown_ignore_labels=[])
 for result in p.predict(str(OUT/'russian-two-columns.png')):
  result.save_to_json(str(OUT/'paddle-full-pipeline'))
  result.save_to_markdown(str(OUT/'paddle-full-pipeline'))
 print('PIPELINE_SECONDS',time.perf_counter()-start,flush=True)
except Exception:
 traceback.print_exc();sys.exit(1)
