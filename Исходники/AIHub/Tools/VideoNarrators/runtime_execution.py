"""Device evidence and timings for the owned, offline narrator process."""
import ctypes
import json
from pathlib import Path
import sys
import time


def vulkan_device():
    import llama_cpp.llama_cpp as low
    low.llama_backend_init()
    if not low.llama_supports_gpu_offload():
        return dict(available=False)
    root = Path(low.__file__).parent / 'lib'
    registry = ctypes.CDLL(str(root / 'ggml.dll'))
    lib = ctypes.CDLL(str(root / 'ggml-base.dll'))
    registry.ggml_backend_dev_count.restype = ctypes.c_size_t
    registry.ggml_backend_dev_get.argtypes = [ctypes.c_size_t]
    registry.ggml_backend_dev_get.restype = ctypes.c_void_p
    lib.ggml_backend_dev_type.argtypes = [ctypes.c_void_p]
    lib.ggml_backend_dev_type.restype = ctypes.c_int
    lib.ggml_backend_dev_description.argtypes = [ctypes.c_void_p]
    lib.ggml_backend_dev_description.restype = ctypes.c_char_p
    lib.ggml_backend_dev_memory.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_size_t)]
    devices = []
    for i in range(registry.ggml_backend_dev_count()):
        dev = registry.ggml_backend_dev_get(i)
        if lib.ggml_backend_dev_type(dev) not in (1, 2):
            continue
        free, total = ctypes.c_size_t(), ctypes.c_size_t()
        lib.ggml_backend_dev_memory(dev, ctypes.byref(free), ctypes.byref(total))
        devices.append(dict(index=len(devices), name=lib.ggml_backend_dev_description(dev).decode(), freeBytes=free.value, totalBytes=total.value))
    if not devices:
        return dict(available=False)
    return dict(available=True, **max(devices, key=lambda d: d['freeBytes']))


class Execution:
    def __init__(self, device, output, repeats=1):
        self.device, self.output, self.repeats = device, Path(output), repeats
        self.started = time.perf_counter()
        self.sessions = []
        self.actual = {}
        self.runs = []
        self.nodes = {}
        self.profile_finished = False

    def reset(self, output, repeats):
        self.output, self.repeats = Path(output), repeats
        self.load_seconds, self.runs = 0.0, []

    def configure_onnx(self, gpu=True):
        self.ort_gpu = False
        import importlib.util
        if importlib.util.find_spec('onnxruntime') is None:
            if gpu:
                raise RuntimeError('The required ONNX runtime is unavailable.')
            return
        import onnxruntime as ort
        original = ort.InferenceSession
        cuda = gpu and self.device.startswith('cuda')
        self.ort_gpu = cuda
        providers = [('CUDAExecutionProvider', {'device_id': int(self.device.split(':')[1]) if ':' in self.device else 0}), 'CPUExecutionProvider'] if cuda else ['CPUExecutionProvider']
        owner = self
        class Session(original):
            def __init__(self, *args, **kwargs):
                options = kwargs.pop('sess_options', None) or ort.SessionOptions()
                options.intra_op_num_threads = 8
                if owner.repeats > 1:
                    options.enable_profiling = True
                    options.profile_file_prefix = str(owner.output.parent / ('onnx-' + str(len(owner.sessions))))
                kwargs['sess_options'], kwargs['providers'] = options, providers
                super().__init__(*args, **kwargs)
                if cuda and 'CUDAExecutionProvider' not in self.get_providers():
                    raise RuntimeError('CUDA ONNX provider failed to load; refusing a false GPU result.')
                owner.sessions.append(self)
        ort.InferenceSession = Session
        if cuda:
            # PyTorch supplies the already-managed CUDA/cuDNN DLLs; no hidden download.
            import torch
            if hasattr(ort, 'preload_dlls'):
                ort.preload_dlls()

    def loaded(self, **facts):
        self.load_seconds = time.perf_counter() - self.started
        self.actual.update(facts)

    def run(self, synthesize):
        import wave
        for _ in range(self.repeats):
            start = time.perf_counter()
            synthesize(str(self.output))
            elapsed = time.perf_counter() - start
            with wave.open(str(self.output)) as sound:
                seconds = sound.getnframes() / sound.getframerate()
            self.runs.append(dict(seconds=elapsed, audioSeconds=seconds, rtf=elapsed / max(seconds, 0.001)))
            # One profiled inference proves the provider; warm timings have no tracing overhead.
            if self.repeats > 1 and not self.profile_finished:
                for session in self.sessions:
                    for event in json.loads(Path(session.end_profiling()).read_text()):
                        provider = event.get('args', {}).get('provider')
                        if provider:
                            self.nodes[provider] = self.nodes.get(provider, 0) + 1
                self.profile_finished = True
        providers = [s.get_providers() for s in self.sessions]
        nodes = self.nodes
        if self.ort_gpu and self.sessions and self.repeats > 1 and nodes.get('CUDAExecutionProvider', 0) == 0:
            raise RuntimeError('ONNX GPU benchmark executed no CUDA nodes.')
        memory = 0
        torch = sys.modules.get('torch')
        if torch and self.device.startswith('cuda'):
            actual_cuda = self.actual.get('device', self.device)
            if not actual_cuda.startswith('cuda'):
                actual_cuda = self.device
            memory = torch.cuda.max_memory_allocated(actual_cuda)
            self.actual['cudaName'] = torch.cuda.get_device_name(actual_cuda)
            self.actual['cudaVisibleDevices'] = __import__('os').environ.get('CUDA_VISIBLE_DEVICES')
        is_gpu = self.actual.get('device', self.device) != 'cpu' and (memory > 0 or any('CUDAExecutionProvider' in p for p in providers) or self.actual.get('gpu', {}).get('available', False))
        record = dict(pid=__import__('os').getpid(), requestedDevice=self.device, loadSeconds=self.load_seconds, runs=self.runs,
                      isGpu=is_gpu, providers=providers, executedNodes=nodes, torchPeakAllocatedBytes=memory, actual=self.actual)
        (self.output.parent / 'execution.json').write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
