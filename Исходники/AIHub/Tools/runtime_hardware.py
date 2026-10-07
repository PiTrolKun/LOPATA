"""Hardware policy for isolated PyTorch workers. No imports of model packages here."""
import ctypes
import json
import math
import os
import re
import struct

GIB = 1024 ** 3


def require_supported_torch(torch):
    """Only immutable runtime profiles tested by the component installer are accepted."""
    if torch.__version__ not in (
            '2.10.0+cpu', '2.10.0+cu126', '2.10.0+cu128', '2.10.0+xpu',
            '2.9.1+rocm7.2.1'):
        raise RuntimeError('Unexpected shared PyTorch runtime version')


def system_memory():
    class MemoryStatus(ctypes.Structure):
        _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [
            (name, ctypes.c_ulonglong) for name in (
                'total', 'available', 'page_total', 'page_available',
                'virtual_total', 'virtual_available', 'extended')]
    memory = MemoryStatus()
    memory.length = ctypes.sizeof(memory)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(memory)):
        raise OSError('Physical RAM inventory is unavailable')
    return int(memory.total), int(memory.available)


def cpu_model_budget(directory, total_ram):
    """Float32 tensors, source shards, compute buffers and system reserve; no paging assumption."""
    if total_ram <= 0:
        raise ValueError('Physical RAM inventory is required')
    parameters = 0
    source_bytes = 0
    for name in os.listdir(directory):
        if not name.endswith('.safetensors'):
            continue
        path = os.path.join(directory, name)
        source_bytes += os.path.getsize(path)
        with open(path, 'rb') as stream:
            raw = stream.read(8)
            if len(raw) != 8:
                raise ValueError('Invalid safetensors header')
            length = struct.unpack('<Q', raw)[0]
            if length > 16 * 1024 * 1024:
                raise ValueError('Oversized safetensors metadata')
            header = json.loads(stream.read(length))
        for key, tensor in header.items():
            if key == '__metadata__':
                continue
            shape = tensor.get('shape')
            if not isinstance(shape, list) or len(shape) > 16 or any(
                    not isinstance(n, int) or isinstance(n, bool) or n < 0 or n > 10 ** 9 for n in shape):
                raise ValueError('Invalid tensor dimensions')
            parameters += math.prod(shape)
            if parameters > 10 ** 12:
                raise ValueError('Unreasonable parameter count')
    if parameters == 0:
        raise ValueError('Verified safetensors weights are required for CPU memory planning')
    return parameters * 4 + source_bytes + 2 * GIB + max(4 * GIB, total_ram // 10)


def torch_device_api(torch, device):
    """CUDA and HIP share the CUDA API; Intel uses its own XPU API."""
    if device == 'cpu':
        return None
    family = device.split(':', 1)[0]
    if family not in ('cuda', 'xpu'):
        raise ValueError('Unsupported PyTorch device')
    return getattr(torch, family)


def torch_backend_name(torch, device):
    if device.startswith('cuda:') and getattr(torch.version, 'hip', None):
        return 'hip'
    return device.split(':', 1)[0]


def torch_inference_dtype(torch, device):
    api = torch_device_api(torch, device)
    if api is None:
        return torch.float32
    with api.device(int(device.split(':')[1])):
        return torch.bfloat16 if api.is_bf16_supported() else torch.float16


def torch_is_out_of_memory(torch, error):
    # New backends share the top-level exception; keep older CUDA/HIP wheels valid.
    types = [getattr(torch, 'OutOfMemoryError', None)]
    types += [getattr(getattr(torch, name, None), 'OutOfMemoryError', None)
              for name in ('cuda', 'xpu')]
    return any(isinstance(kind, type) and isinstance(error, kind) for kind in types)


def select_torch_device(torch, requested, discovered=None):
    if not re.fullmatch(r'auto|cpu|(?:cuda|xpu)(?::[0-9]{1,2})?', requested):
        raise ValueError('Unsupported device policy')
    if requested == 'cpu':
        return 'cpu', None, 'Requested CPU fallback'
    candidates = []
    for family in ('cuda', 'xpu'):
        if requested != 'auto' and requested.split(':')[0] != family:
            continue
        api = getattr(torch, family, None)
        if api is None:
            continue
        try:
            if not api.is_available():
                continue
            for index in range(api.device_count()):
                if ':' in requested and index != int(requested.split(':')[1]):
                    continue
                try:
                    with api.device(index):
                        # Probe an actual operation: a missing exact SM label may still
                        # work via PTX, and HIP uses different architecture names.
                        probe = torch.ones((2, 2), device=f'{family}:{index}')
                        probe = probe @ probe
                        api.synchronize(index)
                        del probe
                        api.empty_cache()
                        free, total = api.mem_get_info(index)
                        name = api.get_device_name(index)
                    if 0 < free <= total:
                        candidates.append((int(free), family, index, int(total), name))
                except (RuntimeError, AssertionError, OSError):
                    continue
        except (RuntimeError, AssertionError, OSError):
            continue
    if discovered is not None:
        discovered.extend(candidates)
    if not candidates:
        if requested != 'auto':
            raise RuntimeError('The explicitly requested GPU is unavailable')
        return 'cpu', None, 'No compatible PyTorch GPU'
    free, family, index, total, name = max(candidates, key=lambda item: item[0])
    getattr(torch, family).set_device(index)
    return f'{family}:{index}', (free, total, name), 'Compatible PyTorch GPU'
