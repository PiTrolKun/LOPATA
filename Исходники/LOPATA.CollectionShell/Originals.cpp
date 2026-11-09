#include "CollectionShell.h"
#include <shellapi.h>

namespace {
class Identity final : public IIdentityName {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    Entry entry;
public:
    explicit Identity(Entry value) : entry(std::move(value)) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IRelatedItem || iid == IID_IIdentityName) *result = static_cast<IIdentityName*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP GetItemIDList(PIDLIST_ABSOLUTE* result) override { return OriginalId(entry, result); }
    IFACEMETHODIMP GetItem(IShellItem** result) override {
        if (!result) return E_POINTER;
        return SHCreateItemFromParsingName(entry.path.c_str(), nullptr, IID_PPV_ARGS(result));
    }
};

HRESULT DataObject(const std::vector<Entry>& entries, REFIID iid, void** result) {
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (const auto& entry : entries) {
        PIDLIST_ABSOLUTE id = nullptr;
        const auto hr = OriginalId(entry, &id);
        if (FAILED(hr)) { for (auto previous : ids) CoTaskMemFree(previous); return hr; }
        ids.push_back(id);
    }
    // All absolute IDs are children of the desktop, even across physical parent folders.
    ComPtr<IDataObject> data;
    auto hr = SHCreateDataObject(nullptr, static_cast<UINT>(ids.size()),
        const_cast<PCUITEMID_CHILD_ARRAY>(ids.data()), nullptr, IID_PPV_ARGS(&data));
    for (const auto id : ids) CoTaskMemFree(id);
    if (FAILED(hr)) return hr;
    // The desktop CIDA keeps original identities across different parent folders. Supply
    // HDROP too: Shell's generic CIDA data object does not synthesize it for consumers.
    size_t characters = 1;
    for (const auto& entry : entries) characters += entry.path.size() + 1;
    if (characters > 16 * 1024 * 1024 / sizeof(wchar_t)) return E_INVALIDARG;
    STGMEDIUM medium{}; medium.tymed = TYMED_HGLOBAL;
    medium.hGlobal = GlobalAlloc(GHND, sizeof(DROPFILES) + characters * sizeof(wchar_t));
    if (!medium.hGlobal) return E_OUTOFMEMORY;
    auto drop = static_cast<DROPFILES*>(GlobalLock(medium.hGlobal));
    if (!drop) { GlobalFree(medium.hGlobal); return E_OUTOFMEMORY; }
    drop->pFiles = sizeof(DROPFILES); drop->fWide = TRUE;
    auto target = reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(drop) + sizeof(DROPFILES));
    for (const auto& entry : entries) {
        memcpy(target, entry.path.data(), entry.path.size() * sizeof(wchar_t));
        target += entry.path.size() + 1;
    }
    GlobalUnlock(medium.hGlobal);
    FORMATETC descriptor{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    hr = data->SetData(&descriptor, &medium, TRUE);
    if (FAILED(hr)) { ReleaseStgMedium(&medium); return hr; }
    return data->QueryInterface(iid, result);
}

HRESULT DeleteOriginals(HWND window, const std::vector<Entry>& entries) {
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (const auto& entry : entries) {
        PIDLIST_ABSOLUTE id = nullptr;
        const auto hr = OriginalId(entry, &id);
        if (FAILED(hr)) { for (auto previous : ids) CoTaskMemFree(previous); return hr; }
        ids.push_back(id);
    }
    ComPtr<IShellItemArray> items;
    auto hr = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()),
        const_cast<PCIDLIST_ABSOLUTE*>(ids.data()), &items);
    for (auto id : ids) CoTaskMemFree(id);
    if (FAILED(hr)) return hr;
    ComPtr<IFileOperation> operation;
    hr = CoCreateInstance(CLSID_FileOperation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&operation));
    if (FAILED(hr)) return hr;
    operation->SetOwnerWindow(window);
    operation->SetOperationFlags(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
    hr = operation->DeleteItems(items.Get());
    if (SUCCEEDED(hr)) hr = operation->PerformOperations();
    RequestRefresh();
    return hr;
}

class MultiMenu final : public IContextMenu {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    HWND window;
    std::vector<Entry> entries;
public:
    MultiMenu(HWND owner, std::vector<Entry> values) : window(owner), entries(std::move(values)) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IContextMenu) *result = static_cast<IContextMenu*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT position, UINT first, UINT last, UINT flags) override {
        if (flags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (last < first || last - first < 5) return E_INVALIDARG;
        const auto names = {Text(L"Открыть", L"Open"), Text(L"Открыть расположение файла", L"Open file location"),
            Text(L"Копировать", L"Copy"), Text(L"Вырезать", L"Cut"), Text(L"Удалить", L"Delete"), Text(L"Свойства", L"Properties")};
        UINT offset = 0;
        for (const auto& name : names) InsertMenuW(menu, position++, MF_BYPOSITION | MF_STRING, first + offset++, name.c_str());
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 6);
    }
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override {
        if (!info) return E_POINTER;
        UINT action = 0;
        if (!HIWORD(info->lpVerb)) action = LOWORD(info->lpVerb);
        else {
            const char* verbs[] = {"open", "opencontaining", "copy", "cut", "delete", "properties"};
            for (; action < ARRAYSIZE(verbs); ++action) if (_stricmp(info->lpVerb, verbs[action]) == 0) break;
        }
        if (action > 5) return E_INVALIDARG;
        if (action == 5) {
            ComPtr<IDataObject> properties;
            const auto hr = DataObject(entries, IID_PPV_ARGS(&properties));
            return SUCCEEDED(hr) ? SHMultiFileProperties(properties.Get(), 0) : hr;
        }
        if (action == 4) return DeleteOriginals(window, entries);
        if (action >= 2) {
            ComPtr<IDataObject> data;
            auto hr = DataObject(entries, IID_PPV_ARGS(&data));
            if (FAILED(hr)) return hr;
            const auto format = static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_PREFERREDDROPEFFECT));
            FORMATETC descriptor{format, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
            STGMEDIUM medium{}; medium.tymed = TYMED_HGLOBAL;
            medium.hGlobal = GlobalAlloc(GHND, sizeof(DWORD));
            if (!medium.hGlobal) return E_OUTOFMEMORY;
            auto effect = static_cast<DWORD*>(GlobalLock(medium.hGlobal));
            if (!effect) { GlobalFree(medium.hGlobal); return E_OUTOFMEMORY; }
            *effect = action == 2 ? DROPEFFECT_COPY : DROPEFFECT_MOVE;
            GlobalUnlock(medium.hGlobal);
            hr = data->SetData(&descriptor, &medium, TRUE);
            if (FAILED(hr)) ReleaseStgMedium(&medium);
            if (SUCCEEDED(hr)) hr = OleSetClipboard(data.Get());
            return hr;
        }
        for (const auto& entry : entries) {
            if (action == 0) {
                SHELLEXECUTEINFOW execute{sizeof(execute)};
                execute.hwnd = window; execute.lpVerb = L"open"; execute.lpFile = entry.path.c_str(); execute.nShow = SW_SHOWNORMAL;
                if (!ShellExecuteExW(&execute)) return HRESULT_FROM_WIN32(GetLastError());
            }
            else {
                PIDLIST_ABSOLUTE id = nullptr;
                const auto hr = OriginalId(entry, &id);
                if (FAILED(hr)) return hr;
                const auto opened = SHOpenFolderAndSelectItems(id, 0, nullptr, 0);
                CoTaskMemFree(id);
                if (FAILED(opened)) return opened;
            }
        }
        return S_OK;
    }
    IFACEMETHODIMP GetCommandString(UINT_PTR id, UINT flags, UINT*, LPSTR value, UINT count) override {
        const wchar_t* verbs[] = {L"open", L"opencontaining", L"copy", L"cut", L"delete", L"properties"};
        if (id >= ARRAYSIZE(verbs)) return E_INVALIDARG;
        if (flags == GCS_VERBW) return StringCchCopyW(reinterpret_cast<LPWSTR>(value), count, verbs[id]);
        if (flags == GCS_VERBA) return WideCharToMultiByte(CP_ACP, 0, verbs[id], -1, value, count, nullptr, nullptr) ? S_OK : E_FAIL;
        return E_NOTIMPL;
    }
};
}

HRESULT CreateIdentity(const Entry& entry, REFIID iid, void** result) {
    auto object = new(std::nothrow) Identity(entry); if (!object) return E_OUTOFMEMORY;
    const auto hr = object->QueryInterface(iid, result); object->Release(); return hr;
}

HRESULT OriginalUi(HWND window, const std::vector<Entry>& entries, REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (entries.empty()) return E_INVALIDARG;
    if (iid == IID_IDataObject) return DataObject(entries, iid, result);
    if (entries.size() > 1) {
        if (iid != IID_IContextMenu) return E_NOINTERFACE;
        auto menu = new(std::nothrow) MultiMenu(window, entries); if (!menu) return E_OUTOFMEMORY;
        const auto hr = menu->QueryInterface(iid, result); menu->Release(); return hr;
    }
    PIDLIST_ABSOLUTE original = nullptr;
    auto hr = OriginalId(entries.front(), &original);
    if (FAILED(hr)) return hr;
    ComPtr<IShellFolder> parent;
    PCUITEMID_CHILD child = nullptr;
    hr = SHBindToParent(original, IID_PPV_ARGS(&parent), &child);
    if (SUCCEEDED(hr)) hr = parent->GetUIObjectOf(window, 1, &child, iid, nullptr, result);
    CoTaskMemFree(original);
    return hr;
}
