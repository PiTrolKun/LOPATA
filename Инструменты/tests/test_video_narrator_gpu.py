"""GPU provider guards and session parameter changes without native model weights."""
import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch
sys.dont_write_bytecode = True
ROOT = Path(__file__).parents[2] / 'Исходники/AIHub/Tools/VideoNarrators'
sys.path.insert(0, str(ROOT))


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


class GpuGuards(unittest.TestCase):
    def test_onnx_adapter_preserves_session_type_and_refuses_provider_fallback(self):
        class Session:
            def __init__(self, *args, **kwargs): self.providers = kwargs['providers']
            def get_providers(self): return ['CPUExecutionProvider']
        ort = types.ModuleType('onnxruntime')
        ort.__spec__ = importlib.util.spec_from_loader('onnxruntime', loader=None)
        ort.InferenceSession = Session
        ort.SessionOptions = lambda: types.SimpleNamespace()
        with patch.dict(sys.modules, {'onnxruntime': ort, 'torch': types.SimpleNamespace()}):
            execution = module('runtime_execution').Execution('cuda:1', 'unused.wav')
            execution.configure_onnx()
            self.assertTrue(issubclass(ort.InferenceSession, Session))
            with self.assertRaisesRegex(RuntimeError, 'refusing a false GPU'):
                ort.InferenceSession('model.onnx')

    def test_cpu_control_forces_cpu_even_when_upstream_requests_cuda(self):
        class Session:
            def __init__(self, *args, **kwargs): self.providers = kwargs['providers']
            def get_providers(self): return self.providers
        ort = types.ModuleType('onnxruntime')
        ort.__spec__ = importlib.util.spec_from_loader('onnxruntime', loader=None)
        ort.InferenceSession, ort.SessionOptions = Session, lambda: types.SimpleNamespace()
        with patch.dict(sys.modules, {'onnxruntime': ort}):
            module('runtime_execution').Execution('cpu', 'unused.wav').configure_onnx()
            session = ort.InferenceSession('model.onnx', providers=['CUDAExecutionProvider'])
            self.assertEqual(['CPUExecutionProvider'], session.get_providers())
            self.assertIsInstance(session, ort.InferenceSession)

    def test_orpheus_configures_the_constructor_local_llama_import(self):
        calls = {}
        llama = types.ModuleType('llama_cpp')
        llama.LLAMA_SPLIT_MODE_NONE = 0
        llama.Llama = lambda **kw: calls.update(kw)
        upstream = types.ModuleType('orpheus_cpp.model')
        class Orpheus:
            def __init__(self, **kwargs):
                from llama_cpp import Llama
                self._llm = Llama(n_ctx=0, **kwargs)
        upstream.OrpheusCpp = Orpheus
        package = types.ModuleType('orpheus_cpp'); package.__path__ = []; package.model = upstream
        report = types.SimpleNamespace(loaded=lambda **kw: None)
        probe = types.SimpleNamespace(vulkan_device=lambda: dict(available=True, index=1))
        with patch.dict(sys.modules, {'llama_cpp': llama, 'orpheus_cpp': package, 'orpheus_cpp.model': upstream,
                                     'runtime_execution': probe, 'numpy': types.SimpleNamespace(), 'soundfile': types.SimpleNamespace()}):
            module('extra_engines').load(dict(engine='orpheus3b', model='local', text='Hello', context_tokens=4096), 'vulkan:1', report)
        self.assertEqual(4096, calls['n_ctx'])
        self.assertEqual((-1, 1, 0), tuple(calls[k] for k in ('n_gpu_layers', 'main_gpu', 'split_mode')))
        self.assertTrue(calls['offload_kqv'] and calls['op_offload'])

    def test_orpheus_cpu_disables_automatic_attention_and_operation_offload(self):
        calls = {}
        llama = types.ModuleType('llama_cpp')
        llama.Llama = lambda **kw: calls.update(kw)
        upstream = types.ModuleType('orpheus_cpp.model')
        class Orpheus:
            def __init__(self, **kwargs):
                from llama_cpp import Llama
                self._llm = Llama(n_ctx=0, **kwargs)
        upstream.OrpheusCpp = Orpheus
        package = types.ModuleType('orpheus_cpp'); package.__path__ = []; package.model = upstream
        with patch.dict(sys.modules, {'llama_cpp': llama, 'orpheus_cpp': package, 'orpheus_cpp.model': upstream,
                                     'runtime_execution': types.SimpleNamespace(vulkan_device=lambda: self.fail('CPU must not choose a GPU')),
                                     'numpy': types.SimpleNamespace(), 'soundfile': types.SimpleNamespace()}):
            module('extra_engines').load(dict(engine='orpheus3b', model='local', text='Hello', context_tokens=4096), 'cpu', types.SimpleNamespace(loaded=lambda **kw: None))
        self.assertFalse(calls['offload_kqv'] or calls['op_offload'])
        self.assertEqual(0, calls['n_gpu_layers'])


if __name__ == '__main__': unittest.main()
