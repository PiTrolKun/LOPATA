#include "CollectionShell.h"
#include <algorithm>

namespace {
bool KindKey(const SHCOLUMNID* key) { return key && IsEqualPropertyKey(*key, PKEY_ItemTypeText); }
bool ScenarioKey(const SHCOLUMNID* key) { return key && IsEqualPropertyKey(*key, PKEY_CollectionScenario); }

class Categorizer final : public ICategorizer {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    bool scenario;
    std::vector<std::wstring> names;
    std::wstring Label(const Entry& entry) const {
        auto value = scenario ? entry.scenario : entry.kind;
        return value.empty() ? Text(L"Не указано", L"Unspecified") : value;
    }
public:
    explicit Categorizer(bool byScenario) : scenario(byScenario) {
        for (const auto& entry : ReadSnapshot(ReadSettings())) names.push_back(Label(entry));
        std::sort(names.begin(), names.end());
        names.erase(std::unique(names.begin(), names.end()), names.end());
    }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_ICategorizer) *result = static_cast<ICategorizer*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP GetDescription(LPWSTR name, UINT count) override {
        return StringCchCopyW(name, count, Text(scenario ? L"Сценарий создания" : L"Тип файла", scenario ? L"Creation scenario" : L"File type").c_str());
    }
    IFACEMETHODIMP GetCategory(UINT count, PCUITEMID_CHILD_ARRAY ids, DWORD* categories) override {
        if ((count && !ids) || !categories) return E_POINTER;
        for (UINT i = 0; i < count; ++i) {
            Entry entry;
            if (!ReadEntryId(ids[i], entry)) return E_INVALIDARG;
            const auto label = Label(entry);
            const auto found = std::find(names.begin(), names.end(), label);
            if (found == names.end()) { names.push_back(label); categories[i] = static_cast<DWORD>(names.size() - 1); }
            else categories[i] = static_cast<DWORD>(found - names.begin());
        }
        return S_OK;
    }
    IFACEMETHODIMP GetCategoryInfo(DWORD id, CATEGORY_INFO* info) override {
        if (!info) return E_POINTER;
        if (id >= names.size()) return E_INVALIDARG;
        *info = {};
        info->cif = CATINFO_NORMAL;
        return StringCchCopyNW(info->wszName, ARRAYSIZE(info->wszName), names[id].c_str(), ARRAYSIZE(info->wszName) - 1);
    }
    IFACEMETHODIMP CompareCategory(CATSORT_FLAGS, DWORD first, DWORD second) override {
        if (first >= names.size() || second >= names.size()) return E_INVALIDARG;
        return CompareResult(names[first].compare(names[second]));
    }
};

class GuidEnumerator final : public IEnumGUID {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    ULONG index = 0;
public:
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IEnumGUID) *result = static_cast<IEnumGUID*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP Next(ULONG count, GUID* values, ULONG* fetched) override {
        if (!values || (!fetched && count != 1)) return E_POINTER;
        ULONG copied = 0;
        while (copied < count && index < 2) values[copied++] = index++ == 0 ? GroupKind : GroupScenario;
        if (fetched) *fetched = copied;
        return copied == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override { const auto old = index; index = (count > 2 - index) ? 2 : index + count; return index - old == count ? S_OK : S_FALSE; }
    IFACEMETHODIMP Reset() override { index = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumGUID** result) override {
        if (!result) return E_POINTER;
        auto clone = new(std::nothrow) GuidEnumerator(); if (!clone) return E_OUTOFMEMORY;
        clone->index = index; *result = clone; return S_OK;
    }
};

class CategoryProvider final : public ICategoryProvider {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
public:
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_ICategoryProvider) *result = static_cast<ICategoryProvider*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP CanCategorizeOnSCID(const SHCOLUMNID* key) override { return KindKey(key) || ScenarioKey(key) ? S_OK : S_FALSE; }
    IFACEMETHODIMP GetDefaultCategory(GUID* group, SHCOLUMNID* key) override {
        if (!group || !key) return E_POINTER;
        *group = GroupKind; *key = PKEY_ItemTypeText; return S_OK;
    }
    IFACEMETHODIMP GetCategoryForSCID(const SHCOLUMNID* key, GUID* group) override {
        if (!key || !group) return E_POINTER;
        if (KindKey(key)) { *group = GroupKind; return S_OK; }
        if (ScenarioKey(key)) { *group = GroupScenario; return S_OK; }
        return E_INVALIDARG;
    }
    IFACEMETHODIMP EnumCategories(IEnumGUID** result) override {
        if (!result) return E_POINTER;
        *result = new(std::nothrow) GuidEnumerator(); return *result ? S_OK : E_OUTOFMEMORY;
    }
    IFACEMETHODIMP GetCategoryName(const GUID* group, LPWSTR value, UINT count) override {
        if (!group) return E_POINTER;
        if (*group != GroupKind && *group != GroupScenario) return E_INVALIDARG;
        return StringCchCopyW(value, count, Text(*group == GroupKind ? L"Тип файла" : L"Сценарий создания",
            *group == GroupKind ? L"File type" : L"Creation scenario").c_str());
    }
    IFACEMETHODIMP CreateCategory(const GUID* group, REFIID iid, void** result) override {
        if (!group || !result) return E_POINTER;
        *result = nullptr;
        if (*group != GroupKind && *group != GroupScenario) return E_INVALIDARG;
        try {
            auto object = new Categorizer(*group == GroupScenario);
            const auto hr = object->QueryInterface(iid, result); object->Release(); return hr;
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
};
}

HRESULT CreateGroups(REFIID iid, void** result) {
    auto provider = new(std::nothrow) CategoryProvider();
    if (!provider) return E_OUTOFMEMORY;
    const auto hr = provider->QueryInterface(iid, result); provider->Release(); return hr;
}
