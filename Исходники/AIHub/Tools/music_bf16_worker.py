"""LOPATA adapter for the pinned upstream YuE2 inference API. Never accesses the network."""
import json, sys, os
from pathlib import Path

def memory_demand(config, data, prefix_tokens):
    """Conservative request estimate, not a measured hardware minimum."""
    context = min(24576, prefix_tokens + data['abc_sampling']['max_tokens'] + 2 * data['semantic_sampling']['max_tokens'] + 8)
    guidance = data['cfg_scale'] if data['cfg_scale'] is not None else (1.01 if data['cot']=='off' else 1.0)
    kv = 2 * config['num_hidden_layers'] * config['num_key_value_heads'] * config['head_dim'] * 2 * context
    if guidance != 1.0: kv *= 2
    model = 7261441640
    # Main weights, key/value cache, attention/flow buffers and decoder staging.
    device = model + kv + 3 * 2**30
    host = model * 2 + 530512720 + 2 * 2**30
    return device, host

def execute(overlay, stage, model, vae, request_file, output, device):
    sys.path.insert(0, str(Path(overlay).resolve()))
    os.environ['HF_HUB_OFFLINE'] = '1'
    os.environ['TRANSFORMERS_OFFLINE'] = '1'
    import torch, numpy as np, soundfile as sf, psutil
    from yue2 import YuE2Pipeline
    from yue2.pipeline import SymbolicPlan
    from yue2.protocol import GenerationConfig, Sampling, SongRequest, token_prefixes
    from yue2.modeling_yue2 import YuE2ForCausalLM
    from yue2.modeling_vae import YuE2VAE
    if not torch.__version__.startswith('2.10.'):
        raise RuntimeError('YuE2 BF16 requires the prepared PyTorch 2.10 hardware profile')
    data = json.loads(Path(request_file).read_text(encoding='utf-8'))
    request = SongRequest(style=data['style'], lyrics=data['lyrics'], cot=data['cot'], seed=data['seed'], cfg_scale=data['cfg_scale'])
    from yue2.tokenization_yue2 import YuE2TextTokenizer
    model_config = json.loads((Path(model)/'config.json').read_text(encoding='utf-8'))
    tokenizer = YuE2TextTokenizer(Path(model)/'qwen.tiktoken')
    saved = json.loads(data['abc']) if stage == 'synth' and request.cot != 'off' else None
    prefix_tokens = len(token_prefixes(request, tokenizer, saved['abc_ids'] if saved else None))
    plan_reserve = data['abc_sampling']['max_tokens'] if request.cot != 'off' and saved is None else 0
    if prefix_tokens + plan_reserve + data['semantic_sampling']['max_tokens'] + 2 > 23347:
        raise ValueError('Music request exceeds the context budget including exact plan tokens')
    required_device, required_host = memory_demand(model_config,data,prefix_tokens)
    free_ram = psutil.virtual_memory().available
    if device=='cpu': required_host=max(required_host,required_device+2*2**30)
    if free_ram < required_host:
        raise MemoryError(f'YuE2 BF16: available RAM {free_ram/2**30:.1f} GiB; estimated request needs {required_host/2**30:.1f} GiB')
    budget = 24.0
    if device.startswith('cuda'):
        torch.cuda.set_device(device)
        if not torch.cuda.is_bf16_supported():
            raise RuntimeError('Selected GPU does not support BF16; no automatic quantization is applied')
        free_gpu, total = torch.cuda.mem_get_info(device)
        if free_gpu < required_device + 2*2**30:
            raise MemoryError(f'YuE2 BF16: free VRAM {free_gpu/2**30:.1f} GiB; request estimate with reserve {(required_device+2*2**30)/2**30:.1f} GiB')
        budget = min(24.0, free_gpu / 2**30)
        name = torch.cuda.get_device_name(device)
    else:
        name = device
    # Validate an actual operation on the selected backend, including CUDA, CPU and XPU.
    x = torch.ones((8, 8), device=device, dtype=torch.bfloat16)
    if not torch.isfinite((x @ x).float()).all(): raise RuntimeError('BF16 backend operation failed')
    del x
    print(f'[Hardware] {device}: {name}; BF16; torch={torch.__version__}; freeRAM={free_ram}; budgetGiB={budget}; estimatedDevice={required_device}; estimatedHost={required_host}', flush=True)
    if stage == 'probe':
        Path(output).write_text(json.dumps({'device':device,'dtype':'bfloat16','runtime':'yue2-infer-0.1.5'}),encoding='utf-8')
        return
    abc_sampling = Sampling(**data['abc_sampling'])
    semantic_sampling = Sampling(**data['semantic_sampling'])
    config = GenerationConfig(abc=abc_sampling, semantic=semantic_sampling, ode_steps=data['steps'])
    # Eager PyTorch avoids optional Linux-only fast libraries; no precision reduction.
    with YuE2Pipeline.from_pretrained(model, vae=vae, local_files_only=True, device=device,
            memory_budget_gib=budget, backend='torch-eager', generation_config=config, progress=True,
            quantization='none', offload_ar=True) as pipe:
        if stage == 'plan':
            print('[ABC] Creating musical plan', flush=True)
            plan = pipe.plan(request=request, abc_sampling=abc_sampling,
                on_token=lambda phase, value: None)
            if not plan.abc or not plan.abc.strip(): raise ValueError('Empty musical plan')
            Path(output).write_text(json.dumps({'abc':plan.abc,'abc_ids':plan.abc_ids},ensure_ascii=False),encoding='utf-8')
            return
        if stage != 'synth': raise ValueError('Unknown music stage')
        if request.cot != 'off':
            ids = saved['abc_ids']
            plan = SymbolicPlan(request, saved['abc'], ids, token_prefixes(request,pipe.tokenizer,ids))
        else: plan = pipe.plan(request=request)
        print('[AR] Creating musical sequence', flush=True)
        count = [0]
        def progress(phase,value):
            count[0] += 1
            if count[0] % 100 == 0: print(f'[AR] Semantic {count[0]}/{semantic_sampling.max_tokens}',flush=True)
        semantic = pipe.generate_semantic(plan,sampling=semantic_sampling,on_token=progress)
        print('[NAR] Forming sound',flush=True)
        latents = pipe.synthesize(semantic)
        print('[VAE] Decoding audio',flush=True)
        audio = pipe.decode(latents)
        if len(audio)==0 or not np.isfinite(audio).all(): raise ValueError('Invalid generated audio')
        sf.write(output,audio,48000,format='WAV',subtype='PCM_16')

if __name__ == '__main__':
    execute(*sys.argv[1:])
