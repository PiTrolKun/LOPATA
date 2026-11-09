#include "CollectionShell.h"

namespace {
bool ReadMode(DWORD& mode) {
    const auto settings = ReadSettings();
    if (settings.root.empty()) return false;
    HANDLE file = CreateFileW((settings.root + L"\\view.bin").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD read = 0; const bool good = ReadFile(file, &mode, sizeof(mode), &read, nullptr) && read == sizeof(mode) && mode <= 2;
    CloseHandle(file); return good;
}
void SaveMode(DWORD mode) {
    DWORD previous = 0;
    if (ReadMode(previous) && previous == mode) return;
    const auto settings = ReadSettings();
    if (settings.root.empty()) return;
    const auto path = settings.root + L"\\view.bin";
    const auto temporary = path + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    const bool saved = WriteFile(file, &mode, sizeof(mode), &written, nullptr) && written == sizeof(mode);
    CloseHandle(file);
    if (saved) MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    DeleteFileW(temporary.c_str());
}
class BackgroundMenu final : public IContextMenu {
    ObjectLifetime lifetime;
    std::atomic<ULONG> references{1};
    ComPtr<IShellView> view;
public:
    explicit BackgroundMenu(IShellView* current) : view(current) {}
    IFACEMETHODIMP QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IContextMenu) *result = static_cast<IContextMenu*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++references; }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseObject(this, references); }
    IFACEMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT first, UINT last, UINT flags) override {
        if (flags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (last < first || last - first < 5) return E_INVALIDARG;
        InsertMenuW(menu, index++, MF_BYPOSITION | MF_STRING, first, Text(L"Обновить", L"Refresh").c_str());
        HMENU grouping = CreatePopupMenu();
        InsertMenuW(grouping, 0, MF_BYPOSITION | MF_STRING, first + 1, Text(L"По типу файла", L"By file type").c_str());
        InsertMenuW(grouping, 1, MF_BYPOSITION | MF_STRING, first + 2, Text(L"По сценарию создания", L"By creation scenario").c_str());
        InsertMenuW(grouping, 2, MF_BYPOSITION | MF_STRING, first + 3, Text(L"Без группировки", L"No grouping").c_str());
        InsertMenuW(menu, index++, MF_BYPOSITION | MF_POPUP, reinterpret_cast<UINT_PTR>(grouping), Text(L"Группировать", L"Group by").c_str());
        HMENU modes = CreatePopupMenu();
        InsertMenuW(modes, 0, MF_BYPOSITION | MF_STRING, first + 4, Text(L"Крупные значки", L"Large icons").c_str());
        InsertMenuW(modes, 1, MF_BYPOSITION | MF_STRING, first + 5, Text(L"Таблица", L"Details").c_str());
        InsertMenuW(menu, index, MF_BYPOSITION | MF_POPUP, reinterpret_cast<UINT_PTR>(modes), Text(L"Вид", L"View").c_str());
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 6);
    }
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* command) override {
        if (!command || HIWORD(command->lpVerb)) return E_INVALIDARG;
        const auto action = LOWORD(command->lpVerb);
        if (!view) return E_UNEXPECTED;
        if (!action) { RequestRefresh(); return view->Refresh(); }
        ComPtr<IFolderView2> folder;
        auto hr = view.As(&folder);
        if (FAILED(hr)) return hr;
        switch (action) {
        case 1: hr = folder->SetGroupBy(PKEY_ItemTypeText, TRUE); if (SUCCEEDED(hr)) SaveMode(1); return hr;
        case 2: hr = folder->SetGroupBy(PKEY_CollectionScenario, TRUE); if (SUCCEEDED(hr)) SaveMode(2); return hr;
        case 3: hr = folder->SetGroupBy(PKEY_Null, TRUE); if (SUCCEEDED(hr)) SaveMode(0); return hr;
        case 4: return folder->SetViewModeAndIconSize(FVM_ICON, 96);
        case 5: return folder->SetViewModeAndIconSize(FVM_DETAILS, 16);
        default: return E_INVALIDARG;
        }
    }
    IFACEMETHODIMP GetCommandString(UINT_PTR id, UINT flags, UINT*, LPSTR buffer, UINT count) override {
        if (id != 0) return E_NOTIMPL;
        if (flags == GCS_VERBW) return StringCchCopyW(reinterpret_cast<LPWSTR>(buffer), count, L"refresh");
        if (flags == GCS_VERBA) return StringCchCopyA(buffer, count, "refresh");
        return E_NOTIMPL;
    }
};
}

void InitializeGrouping(IFolderView2* view) {
    if (!view) return;
    DWORD mode = 0;
    if (ReadMode(mode)) {
        view->SetGroupBy(mode == 0 ? PKEY_Null : mode == 2 ? PKEY_CollectionScenario : PKEY_ItemTypeText, TRUE);
        return;
    }
    PROPERTYKEY current{}; BOOL ascending = TRUE;
    if (FAILED(view->GetGroupBy(&current, &ascending)) || IsEqualPropertyKey(current, PKEY_Null))
        view->SetGroupBy(PKEY_ItemTypeText, TRUE);
}

void RememberGrouping(IFolderView2* view) {
    if (!view) return;
    PROPERTYKEY key{}; BOOL ascending = TRUE;
    if (FAILED(view->GetGroupBy(&key, &ascending))) return;
    if (IsEqualPropertyKey(key, PKEY_Null)) SaveMode(0);
    else if (IsEqualPropertyKey(key, PKEY_ItemTypeText)) SaveMode(1);
    else if (IsEqualPropertyKey(key, PKEY_CollectionScenario)) SaveMode(2);
}

HRESULT CreateBackgroundMenu(IShellView* view, REFIID iid, void** result) {
    auto object = new(std::nothrow) BackgroundMenu(view); if (!object) return E_OUTOFMEMORY;
    const auto hr = object->QueryInterface(iid, result); object->Release(); return hr;
}
