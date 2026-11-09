#include "CollectionShell.h"

HMODULE Module = nullptr;
std::atomic<long> ModuleObjects = 0;

namespace {
class Factory final : public IClassFactory {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
public:
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory) *result = static_cast<IClassFactory*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        return outer ? CLASS_E_NOAGGREGATION : CreateFolder(iid, result);
    }
    IFACEMETHODIMP LockServer(BOOL lock) override {
        if (lock) ++ModuleObjects; else --ModuleObjects;
        return S_OK;
    }
};
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        Module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return ModuleObjects == 0 ? S_OK : S_FALSE; }

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != CLSID_LopataCollectionFolder) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const auto hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
