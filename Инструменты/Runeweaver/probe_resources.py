"""Windows process-only RAM and GPU counters for the experimental stand."""
import ctypes as c
from ctypes import wintypes as w


class Memory(c.Structure):
    _fields_ = [('cb', w.DWORD), ('PageFaultCount', w.DWORD)] + [
        (name, c.c_size_t) for name in ('PeakWorkingSetSize', 'WorkingSetSize',
        'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
        'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]


class CounterValue(c.Structure):
    _fields_ = [('status', w.DWORD), ('value', c.c_double)]


class CounterItem(c.Structure):
    _fields_ = [('name', w.LPWSTR), ('value', CounterValue)]


class Resources:
    def __init__(self, pid):
        self.pid = pid
        self.kernel = c.WinDLL('kernel32', use_last_error=True)
        self.kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
        self.kernel.OpenProcess.restype = w.HANDLE
        self.kernel.CloseHandle.argtypes = [w.HANDLE]
        self.handle = self.kernel.OpenProcess(0x410, False, pid)
        self.psapi = c.WinDLL('psapi')
        self.psapi.GetProcessMemoryInfo.argtypes = [w.HANDLE, c.POINTER(Memory), w.DWORD]
        self.pdh = c.WinDLL('pdh')
        self.pdh.PdhOpenQueryW.argtypes = [w.LPCWSTR, c.c_size_t, c.POINTER(w.HANDLE)]
        self.pdh.PdhAddEnglishCounterW.argtypes = [w.HANDLE, w.LPCWSTR, c.c_size_t, c.POINTER(w.HANDLE)]
        self.pdh.PdhCollectQueryData.argtypes = [w.HANDLE]
        self.pdh.PdhGetFormattedCounterArrayW.argtypes = [w.HANDLE, w.DWORD,
            c.POINTER(w.DWORD), c.POINTER(w.DWORD), c.c_void_p]
        self.pdh.PdhCloseQuery.argtypes = [w.HANDLE]
        self.query = w.HANDLE()
        self.counters = {}
        self.status = self.pdh.PdhOpenQueryW(None, 0, c.byref(self.query))
        if not self.status:
            for name, field in [('dedicated', 'Dedicated Usage'), ('shared', 'Shared Usage')]:
                handle = w.HANDLE()
                status = self.pdh.PdhAddEnglishCounterW(self.query,
                    f'\\GPU Process Memory(*)\\{field}', 0, c.byref(handle))
                if status == 0:
                    self.counters[name] = handle
            self.pdh.PdhCollectQueryData(self.query)

    def sample(self):
        result = {'pid': self.pid}
        memory = Memory()
        memory.cb = c.sizeof(memory)
        if self.handle and self.psapi.GetProcessMemoryInfo(self.handle, c.byref(memory), memory.cb):
            result.update(working_set=memory.WorkingSetSize, private_bytes=memory.PrivateUsage)
        if self.query:
            self.pdh.PdhCollectQueryData(self.query)
            for name, counter in self.counters.items():
                size, count = w.DWORD(), w.DWORD()
                self.pdh.PdhGetFormattedCounterArrayW(counter, 0x200, c.byref(size), c.byref(count), None)
                if not size.value:
                    result[name] = None
                    continue
                buffer = c.create_string_buffer(size.value)
                status = self.pdh.PdhGetFormattedCounterArrayW(counter, 0x200,
                    c.byref(size), c.byref(count), buffer)
                values = {}
                if status == 0:
                    items = c.cast(buffer, c.POINTER(CounterItem))
                    for i in range(count.value):
                        item = items[i]
                        if item.name.startswith(f'pid_{self.pid}_') and item.value.status <= 1:
                            values[item.name] = item.value.value
                result[name] = sum(values.values()) if values else None
                result[name + '_instances'] = values
        return result

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
        if self.query:
            self.pdh.PdhCloseQuery(self.query)
