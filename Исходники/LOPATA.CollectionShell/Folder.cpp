#include "CollectionShell.h"
#include <algorithm>

namespace {
class ItemEnumerator final : public IEnumIDList {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    std::vector<Entry> entries;
    size_t position = 0;
public:
    explicit ItemEnumerator(std::vector<Entry> values) : entries(std::move(values)) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IEnumIDList) *result = static_cast<IEnumIDList*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP Next(ULONG count, PITEMID_CHILD* ids, ULONG* fetched) override {
        if (!ids || (!fetched && count != 1)) return E_POINTER;
        ULONG copied = 0;
        while (copied < count && position < entries.size()) {
            ids[copied] = CreateEntryId(entries[position]);
            if (!ids[copied]) {
                for (ULONG i = 0; i < copied; ++i) CoTaskMemFree(ids[i]);
                if (fetched) *fetched = 0;
                return E_OUTOFMEMORY;
            }
            ++copied; ++position;
        }
        if (fetched) *fetched = copied;
        return copied == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override {
        const auto available = entries.size() - position;
        position += std::min<size_t>(count, available);
        return count <= available ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Reset() override { position = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumIDList** result) override {
        if (!result) return E_POINTER;
        try { auto copy = new ItemEnumerator(entries); copy->position = position; *result = copy; return S_OK; }
        catch (...) { *result = nullptr; return E_OUTOFMEMORY; }
    }
};

class ViewCallback final : public IShellFolderViewCB, public IObjectWithSite {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    PIDLIST_ABSOLUTE root = nullptr;
    bool leased = false;
    IShellView** activeView;
    ComPtr<IShellFolder2> owner;
    ComPtr<IUnknown> site;
    HRESULT UpdateStatus() {
        if (!view) return E_UNEXPECTED;
        try {
            const auto text = CollectionStatusText();
            ComPtr<IFolderView2> folderView;
            if (SUCCEEDED(view->QueryInterface(IID_PPV_ARGS(&folderView)))) {
                folderView->SetText(FVST_EMPTYTEXT, text.c_str());
                RememberGrouping(folderView.Get());
            }
            ComPtr<IShellBrowser> browser;
            if (site && SUCCEEDED(IUnknown_QueryService(site.Get(), SID_STopLevelBrowser, IID_PPV_ARGS(&browser))))
                return browser->SetStatusTextSB(text.c_str());
            return E_NOTIMPL;
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
public:
    IShellView* view = nullptr;
    ViewCallback(PCIDLIST_ABSOLUTE id, IShellView** current, IShellFolder2* folder)
        : root(ILCloneFull(id)), activeView(current), owner(folder) {}
    ~ViewCallback() { if (*activeView == view) *activeView = nullptr; if (leased) ReleaseViewLease(); CoTaskMemFree(root); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IShellFolderViewCB) *result = static_cast<IShellFolderViewCB*>(this);
        else if (iid == IID_IObjectWithSite) *result = static_cast<IObjectWithSite*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP SetSite(IUnknown* current) override { site = current; return S_OK; }
    IFACEMETHODIMP GetSite(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        return site ? site->QueryInterface(iid, result) : E_NOINTERFACE;
    }
    IFACEMETHODIMP MessageSFVCB(UINT message, WPARAM wparam, LPARAM lparam) override {
        switch (message) {
        case SFVM_WINDOWCREATED:
            if (!leased) { leased = true; AcquireViewLease(); }
            if (view) {
                ComPtr<IFolderView2> folderView;
                if (SUCCEEDED(view->QueryInterface(IID_PPV_ARGS(&folderView)))) {
                    InitializeGrouping(folderView.Get());
                }
            }
            UpdateStatus();
            return S_OK;
        case SFVM_UPDATESTATUSBAR:
            return UpdateStatus();
        case SFVM_DEFVIEWMODE:
            if (!lparam) return E_POINTER;
            *reinterpret_cast<FOLDERVIEWMODE*>(lparam) = FVM_ICON;
            return S_OK;
        case SFVM_GETNOTIFY:
            if (!wparam || !lparam) return E_POINTER;
            *reinterpret_cast<PCIDLIST_ABSOLUTE*>(wparam) = root;
            *reinterpret_cast<LONG*>(lparam) = SHCNE_UPDATEDIR | SHCNE_UPDATEITEM | SHCNE_DELETE | SHCNE_RENAMEITEM;
            return S_OK;
        default: return E_NOTIMPL;
        }
    }
};

PROPERTYKEY ColumnKey(UINT column) {
    switch (column) {
    case 0: return PKEY_ItemNameDisplay;
    case 1: return PKEY_ItemTypeText;
    case 2: return PKEY_CollectionScenario;
    default: return PKEY_ItemPathDisplay;
    }
}
const wchar_t* ColumnText(const Entry& entry, UINT column) {
    switch (column) {
    case 0: return PathFindFileNameW(entry.path.c_str());
    case 1: return entry.kind.c_str();
    case 2: return entry.scenario.c_str();
    default: return entry.path.c_str();
    }
}

class CollectionFolder final : public IShellFolder2, public IPersistFolder2 {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    PIDLIST_ABSOLUTE root = nullptr;
    IShellView* activeView = nullptr;
public:
    ~CollectionFolder() { CoTaskMemFree(root); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IShellFolder || iid == IID_IShellFolder2) *result = static_cast<IShellFolder2*>(this);
        else if (iid == IID_IPersist || iid == IID_IPersistFolder || iid == IID_IPersistFolder2) *result = static_cast<IPersistFolder2*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP GetClassID(CLSID* clsid) override { if (!clsid) return E_POINTER; *clsid = CLSID_LopataCollectionFolder; return S_OK; }
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE id) override {
        if (!id) return E_INVALIDARG;
        auto copy = ILCloneFull(id); if (!copy) return E_OUTOFMEMORY;
        CoTaskMemFree(root); root = copy; return S_OK;
    }
    IFACEMETHODIMP GetCurFolder(PIDLIST_ABSOLUTE* result) override {
        if (!result) return E_POINTER;
        *result = root ? ILCloneFull(root) : nullptr;
        return *result ? S_OK : E_UNEXPECTED;
    }
    IFACEMETHODIMP ParseDisplayName(HWND, IBindCtx*, LPWSTR name, ULONG* eaten, PIDLIST_RELATIVE* result, ULONG*) override {
        if (!name || !result) return E_POINTER;
        *result = nullptr;
        try {
            for (const auto& entry : ReadSnapshot(ReadSettings())) {
                if (CompareStringOrdinal(name, -1, entry.path.c_str(), -1, TRUE) == CSTR_EQUAL) {
                    *result = CreateEntryId(entry); if (eaten) *eaten = static_cast<ULONG>(wcslen(name));
                    return *result ? S_OK : E_OUTOFMEMORY;
                }
            }
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP EnumObjects(HWND, SHCONTF flags, IEnumIDList** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (!(flags & SHCONTF_NONFOLDERS)) return S_FALSE;
        try { *result = new ItemEnumerator(ReadSnapshot(ReadSettings())); return S_OK; }
        catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP BindToObject(PCUIDLIST_RELATIVE id, IBindCtx*, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        Entry entry;
        if (!ReadEntryId(id, entry)) return E_INVALIDARG;
        if (iid == IID_IIdentityName || iid == IID_IRelatedItem) return CreateIdentity(entry, iid, result);
        return SHCreateItemFromParsingName(entry.path.c_str(), nullptr, iid, result);
    }
    IFACEMETHODIMP BindToStorage(PCUIDLIST_RELATIVE id, IBindCtx*, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        Entry entry;
        if (!ReadEntryId(id, entry)) return E_INVALIDARG;
        ComPtr<IShellItem> item;
        auto hr = SHCreateItemFromParsingName(entry.path.c_str(), nullptr, IID_PPV_ARGS(&item));
        return SUCCEEDED(hr) ? item->BindToHandler(nullptr, BHID_Stream, iid, result) : hr;
    }
    IFACEMETHODIMP CompareIDs(LPARAM parameter, PCUIDLIST_RELATIVE first, PCUIDLIST_RELATIVE second) override {
        Entry a, b;
        if (!ReadEntryId(first, a) || !ReadEntryId(second, b)) return E_INVALIDARG;
        const auto column = static_cast<UINT>(parameter & SHCIDS_COLUMNMASK);
        const auto primary = CompareStringOrdinal(ColumnText(a, column), -1, ColumnText(b, column), -1, TRUE);
        if (primary != CSTR_EQUAL) return CompareResult(primary - CSTR_EQUAL);
        return CompareResult(CompareStringOrdinal(a.path.c_str(), -1, b.path.c_str(), -1, TRUE) - CSTR_EQUAL);
    }
    IFACEMETHODIMP CreateViewObject(HWND, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_ICategoryProvider) return CreateGroups(iid, result);
        if (iid == IID_IContextMenu && activeView) return CreateBackgroundMenu(activeView, iid, result);
        if (iid != IID_IShellView) return E_NOINTERFACE;
        auto callback = new(std::nothrow) ViewCallback(root, &activeView, this);
        if (!callback) return E_OUTOFMEMORY;
        SFV_CREATE create{sizeof(create)}; create.pshf = this; create.psfvcb = callback;
        ComPtr<IShellView> view;
        auto hr = SHCreateShellFolderView(&create, &view);
        if (SUCCEEDED(hr)) { callback->view = view.Get(); activeView = view.Get(); hr = view->QueryInterface(iid, result); }
        callback->Release();
        return hr;
    }
    IFACEMETHODIMP GetAttributesOf(UINT count, PCUITEMID_CHILD_ARRAY ids, SFGAOF* attributes) override {
        if (!attributes || (count && !ids)) return E_POINTER;
        if (!count) { *attributes &= SFGAO_FOLDER | SFGAO_BROWSABLE; return S_OK; }
        SFGAOF available = SFGAO_FILESYSTEM | SFGAO_CANCOPY | SFGAO_CANMOVE | SFGAO_CANDELETE | SFGAO_CANRENAME | SFGAO_CANLINK | SFGAO_HASPROPSHEET;
        for (UINT i = 0; i < count; ++i) {
            Entry entry;
            if (!ReadEntryId(ids[i], entry)) return E_INVALIDARG;
            ComPtr<IShellItem> original;
            const auto hr = SHCreateItemFromParsingName(entry.path.c_str(), nullptr, IID_PPV_ARGS(&original));
            SFGAOF actual = 0;
            if (SUCCEEDED(hr)) original->GetAttributes(available, &actual);
            available &= actual;
        }
        *attributes &= available;
        return S_OK;
    }
    IFACEMETHODIMP GetUIObjectOf(HWND window, UINT count, PCUITEMID_CHILD_ARRAY ids, REFIID iid, UINT*, void** result) override {
        if (!result || (count && !ids)) return E_POINTER;
        *result = nullptr;
        if (!count && iid == IID_ICategoryProvider) return CreateGroups(iid, result);
        try {
            std::vector<Entry> entries;
            for (UINT i = 0; i < count; ++i) { Entry entry; if (!ReadEntryId(ids[i], entry)) return E_INVALIDARG; entries.push_back(std::move(entry)); }
            return OriginalUi(window, entries, iid, result);
        }
        catch (...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP GetDisplayNameOf(PCUITEMID_CHILD id, SHGDNF flags, STRRET* result) override {
        if (!id || id->mkid.cb == 0)
            return StringResult(flags & SHGDN_FORPARSING ? L"::{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}"
                : Text(L"ЛОПАТА", L"LOPATA"), result);
        Entry entry;
        if (!ReadEntryId(id, entry)) return E_INVALIDARG;
        return StringResult(flags & SHGDN_FORPARSING ? entry.path : PathFindFileNameW(entry.path.c_str()), result);
    }
    IFACEMETHODIMP SetNameOf(HWND window, PCUITEMID_CHILD id, LPCWSTR name, SHGDNF, PITEMID_CHILD* result) override {
        if (result) *result = nullptr;
        Entry entry;
        if (!name || !ReadEntryId(id, entry)) return E_INVALIDARG;
        ComPtr<IShellItem> item;
        auto hr = SHCreateItemFromParsingName(entry.path.c_str(), nullptr, IID_PPV_ARGS(&item));
        if (FAILED(hr)) return hr;
        ComPtr<IFileOperation> operation;
        hr = CoCreateInstance(CLSID_FileOperation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&operation));
        if (FAILED(hr)) return hr;
        operation->SetOwnerWindow(window);
        hr = operation->RenameItem(item.Get(), name, nullptr);
        if (SUCCEEDED(hr)) hr = operation->PerformOperations();
        RequestRefresh(); return hr;
    }
    IFACEMETHODIMP GetDefaultSearchGUID(GUID*) override { return E_NOTIMPL; }
    IFACEMETHODIMP EnumSearches(IEnumExtraSearch** result) override { if (result) *result = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetDefaultColumn(DWORD, ULONG* sort, ULONG* display) override {
        if (!sort || !display) return E_POINTER;
        *sort = 0; *display = 0; return S_OK;
    }
    IFACEMETHODIMP GetDefaultColumnState(UINT column, SHCOLSTATEF* state) override {
        if (!state) return E_POINTER;
        if (column > 3) return E_INVALIDARG;
        *state = SHCOLSTATE_TYPE_STR | (column < 2 ? SHCOLSTATE_ONBYDEFAULT : 0); return S_OK;
    }
    IFACEMETHODIMP GetDetailsEx(PCUITEMID_CHILD id, const SHCOLUMNID* key, VARIANT* value) override {
        if (!key || !value) return E_POINTER;
        VariantInit(value);
        Entry entry; if (!ReadEntryId(id, entry)) return E_INVALIDARG;
        for (UINT column = 0; column < 4; ++column) {
            if (IsEqualPropertyKey(*key, ColumnKey(column))) {
                value->vt = VT_BSTR; value->bstrVal = SysAllocString(ColumnText(entry, column));
                return value->bstrVal ? S_OK : E_OUTOFMEMORY;
            }
        }
        ComPtr<IShellItem2> original;
        auto hr = SHCreateItemFromParsingName(entry.path.c_str(), nullptr, IID_PPV_ARGS(&original));
        if (FAILED(hr)) return hr;
        PROPVARIANT property{};
        hr = original->GetProperty(*key, &property);
        if (SUCCEEDED(hr)) hr = PropVariantToVariant(&property, value);
        PropVariantClear(&property);
        return hr;
    }
    IFACEMETHODIMP GetDetailsOf(PCUITEMID_CHILD id, UINT column, SHELLDETAILS* details) override {
        if (!details) return E_POINTER;
        if (column > 3) return E_INVALIDARG;
        *details = {}; details->fmt = LVCFMT_LEFT; details->cxChar = column == 3 ? 50 : 25;
        if (id) { Entry entry; if (!ReadEntryId(id, entry)) return E_INVALIDARG; return StringResult(ColumnText(entry, column), &details->str); }
        const auto headers = {Text(L"Имя", L"Name"), Text(L"Тип файла", L"File type"), Text(L"Сценарий создания", L"Creation scenario"), Text(L"Расположение", L"Location")};
        return StringResult(*(headers.begin() + column), &details->str);
    }
    IFACEMETHODIMP MapColumnToSCID(UINT column, SHCOLUMNID* key) override {
        if (!key) return E_POINTER;
        if (column > 3) return E_INVALIDARG;
        *key = ColumnKey(column); return S_OK;
    }
};
}

HRESULT CreateFolder(REFIID iid, void** result) {
    auto folder = new(std::nothrow) CollectionFolder(); if (!folder) return E_OUTOFMEMORY;
    const auto hr = folder->QueryInterface(iid, result); folder->Release(); return hr;
}
