import contextlib
import json
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, sys.argv.pop(1))
from runtime_hardware import (cpu_model_budget, select_torch_device, system_memory, GIB,
    torch_device_api, torch_backend_name, torch_inference_dtype, torch_is_out_of_memory)


class FakeCuda:
    def __init__(self):
        self.current = 0

    def is_available(self): return True
    def device_count(self): return 3
    @contextlib.contextmanager
    def device(self, index):
        self.current = index
        yield
    def mem_get_info(self, index=None): return [(9, 10), (3, 4), (7, 8)][self.current if index is None else index]
    def synchronize(self, index): pass
    def empty_cache(self): pass
    def set_device(self, index): self.current = index
    def get_device_name(self, index): return 'GPU ' + str(index)
    def is_bf16_supported(self): return self.current == 2


class FakeTensor:
    def __matmul__(self, other): return self


class FakeTorch:
    def __init__(self): self.cuda = FakeCuda()
    def ones(self, shape, device):
        if device == 'cuda:0': raise RuntimeError('Unsupported operation')
        return FakeTensor()


class HardwareTests(unittest.TestCase):
    def test_xpu_survives_broken_cuda_inventory(self):
        class BrokenCuda(FakeCuda):
            def is_available(self): raise OSError('Driver unavailable')
        torch = FakeTorch()
        torch.cuda = BrokenCuda()
        torch.xpu = FakeCuda()
        device, memory, _ = select_torch_device(torch, 'auto')
        self.assertEqual('xpu:0', device)
        self.assertEqual((9, 10, 'GPU 0'), memory)
        self.assertIs(torch.xpu, torch_device_api(torch, device))

    def test_best_free_memory_across_backends(self):
        class Xpu(FakeCuda):
            def mem_get_info(self, index=None): return (11, 12)
        torch = FakeTorch()
        torch.xpu = Xpu()
        self.assertEqual('xpu:0', select_torch_device(torch, 'auto')[0])
        torch.xpu.mem_get_info = lambda index=None: (13, 12)
        self.assertEqual('cuda:2', select_torch_device(torch, 'auto')[0])

    def test_backend_dtype_and_common_oom(self):
        torch = FakeTorch()
        torch.version = type('Version', (), {'hip': '7.2.1'})()
        torch.float32, torch.float16, torch.bfloat16 = 'fp32', 'fp16', 'bf16'
        torch.OutOfMemoryError = type('TorchOOM', (RuntimeError,), {})
        self.assertEqual('hip', torch_backend_name(torch, 'cuda:2'))
        self.assertEqual('xpu', torch_backend_name(torch, 'xpu:0'))
        self.assertEqual('bf16', torch_inference_dtype(torch, 'cuda:2'))
        self.assertEqual('fp16', torch_inference_dtype(torch, 'cuda:1'))
        self.assertEqual('fp32', torch_inference_dtype(torch, 'cpu'))
        self.assertTrue(torch_is_out_of_memory(torch, torch.OutOfMemoryError()))
        self.assertFalse(torch_is_out_of_memory(torch, ValueError()))

    def test_actual_operation_and_runtime_ordinal(self):
        device, memory, reason = select_torch_device(FakeTorch(), 'auto')
        self.assertEqual('cuda:2', device)
        self.assertEqual((7, 8, 'GPU 2'), memory)
        self.assertEqual(('cpu', None, 'Requested CPU fallback'), select_torch_device(FakeTorch(), 'cpu'))

    def test_unknown_policy_fails(self):
        with self.assertRaises(ValueError): select_torch_device(FakeTorch(), 'CUDA0')

    def test_explicit_device_does_not_switch_to_a_faster_gpu(self):
        torch = FakeTorch(); torch.xpu = FakeCuda()
        self.assertEqual('cuda:1', select_torch_device(torch, 'cuda:1')[0])
        self.assertEqual('xpu:2', select_torch_device(torch, 'xpu:2')[0])
        with self.assertRaises(RuntimeError): select_torch_device(torch, 'cuda:0')
        with self.assertRaises(RuntimeError): select_torch_device(torch, 'cuda:99')
        with self.assertRaises(ValueError): select_torch_device(torch, 'cuda:-1')

    def test_cpu_budget_and_bad_dimensions(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, 'model.safetensors')
            header = json.dumps({'weight': {'shape': [100, 200]}}).encode()
            with open(path, 'wb') as stream: stream.write(struct.pack('<Q', len(header)) + header)
            self.assertEqual(80000 + os.path.getsize(path) + 6 * GIB, cpu_model_budget(folder, 16 * GIB))
            header = json.dumps({'weight': {'shape': [-1]}}).encode()
            with open(path, 'wb') as stream: stream.write(struct.pack('<Q', len(header)) + header)
            with self.assertRaises(ValueError): cpu_model_budget(folder, 16 * GIB)
            with open(path, 'wb') as stream: stream.write(struct.pack('<Q', 17 * 1024 * 1024))
            with self.assertRaises(ValueError): cpu_model_budget(folder, 16 * GIB)

    def test_empty_weights_and_ram_inventory(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError): cpu_model_budget(folder, 16 * GIB)
            with self.assertRaises(ValueError): cpu_model_budget(folder, 0)
        total, available = system_memory()
        self.assertGreater(total, 0)
        self.assertGreaterEqual(available, 0)
        self.assertLessEqual(available, total)


unittest.main()
