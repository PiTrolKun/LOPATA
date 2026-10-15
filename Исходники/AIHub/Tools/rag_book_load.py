"""Book worker resource budgets. One model, bounded batches, sampled total Windows CPU."""
import ctypes
import os
import time
from runtime_hardware import system_memory, torch_device_api

GIB = 1024 ** 3


class CpuProbe:
    def __init__(self):
        self.previous = None

    def read(self):
        idle, kernel, user = (ctypes.c_ulonglong() for _ in range(3))
        if not ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
            return None
        current = idle.value, kernel.value + user.value
        old, self.previous = self.previous, current
        if old is None or current[1] <= old[1] or current[0] < old[0]:
            return None
        return max(0., min(100., 100 * (1 - (current[0] - old[0]) / (current[1] - old[1]))))


def batch_capacity(device, available, total):
    reserve = max(2 * GIB, total // 10) if device == 'cpu' else GIB
    return max(1, min(8 if device == 'cpu' else 16, (available - reserve) // (256 * 1024**2)))


class EmbeddingLoad:
    def __init__(self, torch, device, target, emit, probe=None, sleep=time.sleep):
        self.torch, self.device = torch, device
        self.target = max(10, min(90, target))
        self.emit, self.sleep = emit, sleep
        self.probe = probe or CpuProbe()
        self.probe.read()
        self.average, self.next_sample = None, time.monotonic() + 1
        self.batch, self.ceiling = 2, 16
        self.maximum_threads = max(1, min(32, int((os.cpu_count() or 1) * self.target / 100)))
        self.threads = min(4, self.maximum_threads)
        self.torch.set_num_threads(self.threads)

    def before(self):
        while time.monotonic() >= self.next_sample:
            cpu = self.probe.read()
            self.next_sample = time.monotonic() + 1
            if cpu is None:
                self.batch = min(self.batch, 2)
                if self.device == 'cpu' and self.threads != 1:
                    self.torch.set_num_threads(1)
                    self.threads = 1
                break
            self.average = cpu if self.average is None else self.average * .65 + cpu * .35
            if self.device == 'cpu':
                threads = self.threads
                if self.average > self.target:
                    threads = max(1, threads - 1)
                elif self.average < self.target - 5:
                    threads = min(self.maximum_threads, threads + 1)
                if threads != self.threads:
                    self.torch.set_num_threads(threads)
                    self.threads = threads
            self.emit('Resource', detail=f'CPU {self.average:.0f}%; target {self.target}%; threads {self.threads}; batch {self.batch}')
            if self.average <= self.target + 3:
                break
            # Owned worker cancellation terminates the process; no uninterruptible native batch starts here.
            self.sleep(1)

    def capacity(self):
        api = torch_device_api(self.torch, self.device)
        if api is None:
            total, available = system_memory()
        else:
            available, total = api.mem_get_info(self.device)
        return min(self.ceiling, batch_capacity(self.device, int(available), int(total)))

    def success(self):
        self.batch = min(self.batch + 1, self.capacity())

    def shrink(self):
        if self.batch <= 1:
            return False
        self.ceiling = max(1, self.batch // 2)
        self.batch = self.ceiling
        api = torch_device_api(self.torch, self.device)
        if api is not None:
            api.empty_cache()
        return True
