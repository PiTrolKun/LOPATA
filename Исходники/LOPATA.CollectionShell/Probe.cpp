#include "CollectionShell.h"
#include "RecoveryBudget.h"
#include <shellapi.h>
#include <cstdio>

HMODULE Module = nullptr;
std::atomic<long> ModuleObjects = 0;
void RequestRefresh() {} // The isolated original-file tests do not create a view or worker.

namespace {
void Require(bool good, const char* name) {
    if (!good) { std::fprintf(stderr, "FAIL: %s\n", name); ExitProcess(1); }
    std::printf("PASS: %s\n", name);
}
void Append(std::vector<unsigned char>& buffer, const void* data, size_t count) {
    const auto p = static_cast<const unsigned char*>(data);
    buffer.insert(buffer.end(), p, p + count);
}
void String(std::vector<unsigned char>& buffer, const std::wstring& text) {
    const auto length = static_cast<unsigned int>(text.size());
    Append(buffer, &length, 4); Append(buffer, text.data(), text.size() * 2);
}
void Save(const std::wstring& root, const std::vector<Entry>& entries, bool truncate = false) {
    std::vector<unsigned char> bytes;
    const unsigned int magic = 0x3143504c, version = 1, count = static_cast<unsigned int>(entries.size());
    const long long generation = 1;
    Append(bytes, &magic, 4); Append(bytes, &version, 4); Append(bytes, &generation, 8); Append(bytes, &count, 4);
    for (const auto& entry : entries) { String(bytes, entry.path); String(bytes, entry.kind); String(bytes, entry.scenario); }
    if (truncate && !bytes.empty()) bytes.resize(bytes.size() - 1);
    HANDLE file = CreateFileW((root + L"\\snapshot.bin").c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    Require(file != INVALID_HANDLE_VALUE, "fixture open");
    DWORD written = 0;
    Require(WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size(), "fixture write");
    CloseHandle(file);
}
}

int wmain(int count, wchar_t** arguments) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    if (count < 3 || count > 4) { std::fprintf(stderr, "usage: Probe <scratch-directory> <DLL> [--registered|--open|--protocol]\n"); return 2; }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Settings settings; settings.root = arguments[1];
    for (auto& character : settings.root) if (character == L'/') character = L'\\';
    Require(CreateDirectoryW(settings.root.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS, "scratch directory");
    Entry original{L"C:\\Проверка\\résumé 😀.png", L"Изображение", L"Захват экрана"};
    Save(settings.root, {original});
    const auto first = ReadSnapshot(settings);
    Require(first.size() == 1 && first[0].path == original.path && first[0].scenario == original.scenario, "UTF-16 snapshot");
    Save(settings.root, {original}, true);
    Require(ReadSnapshot(settings).size() == 1, "last good snapshot after truncation");
    Save(settings.root, {});
    Require(ReadSnapshot(settings).empty(), "valid empty snapshot");
    auto id = CreateEntryId(original); Entry decoded;
    Require(id && ReadEntryId(id, decoded) && decoded.path == original.path && decoded.kind == original.kind, "stable item identifier");
    reinterpret_cast<unsigned short*>(id)[1] = 0;
    Require(!ReadEntryId(id, decoded), "invalid item signature");
    CoTaskMemFree(id);
    Require(!CreateEntryId(Entry{L"relative.png", L"", L""}), "relative path rejected");
    const auto oldWorker = RecoveryWorkerFingerprint(L"C:\\Исходники\\OLD\\worker.exe");
    const auto newWorker = RecoveryWorkerFingerprint(L"C:\\Исходники\\NEW\\worker.exe");
    Require(oldWorker == RecoveryWorkerFingerprint(L"c:\\ИСХОДНИКИ\\old\\WORKER.EXE"), "recovery worker identity ignores Unicode path case");
    RecoveryAttempts attempts{1, 1, 3, oldWorker};
    Require(!BindRecoveryWorker(attempts, 10000000000, oldWorker) && !RecoveryAttemptDue(attempts, 10000000000), "three attempts remain exhausted for the same worker");
    Require(!RearmRecoveryAfterIdle(attempts, 590000001) && attempts.count == 3, "reopening immediately does not restart a failing worker");
    Require(RearmRecoveryAfterIdle(attempts, 600000001) && RecoveryAttemptDue(attempts, 600000001), "reopening after idle gets a fresh bounded budget");
    attempts = {1, 1, 3, oldWorker};
    Require(BindRecoveryWorker(attempts, 10000000000, newWorker) && attempts.count == 0 && RecoveryAttemptDue(attempts, 10000000000), "a changed worker gets a new bounded budget");
    attempts.count = 1; attempts.lastStart = 10000000000;
    Require(!BindRecoveryWorker(attempts, 10000000001, newWorker) && attempts.count == 1 && !RecoveryAttemptDue(attempts, 10000000001), "the same new worker does not reset its budget repeatedly");
    ResetRecoveryCounters(attempts, 0);
    Require(attempts.workerFingerprint == newWorker && attempts.count == 0, "healthy reset preserves the desired worker identity");
    std::vector<Entry> originals;
    for (const auto parent : {L"one", L"two"}) {
        const auto directory = settings.root + L"\\" + parent;
        Require(CreateDirectoryW(directory.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS, "original parent fixture");
        const auto path = directory + L"\\résumé 😀.txt";
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        Require(file != INVALID_HANDLE_VALUE, "original file fixture"); CloseHandle(file);
        originals.push_back({path, L"Text", L"Test"});
    }
    ComPtr<IDataObject> originalData;
    const auto originalResult = OriginalUi(nullptr, originals, IID_PPV_ARGS(&originalData));
    std::printf("original cross-parent result: %08lx\n", static_cast<unsigned long>(originalResult));
    Require(SUCCEEDED(originalResult), "original cross-parent data object");
    FORMATETC dropFormat{CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    STGMEDIUM originalMedium{};
    Require(SUCCEEDED(originalData->GetData(&dropFormat, &originalMedium)), "original cross-parent HDROP");
    Require(DragQueryFileW(static_cast<HDROP>(originalMedium.hGlobal), 0xffffffff, nullptr, 0) == originals.size(), "original HDROP file count");
    for (UINT index = 0; index < originals.size(); ++index) {
        wchar_t path[32768]{};
        DragQueryFileW(static_cast<HDROP>(originalMedium.hGlobal), index, path, ARRAYSIZE(path));
        Require(originals[index].path == path, "original HDROP preserves absolute Unicode path");
    }
    ReleaseStgMedium(&originalMedium);
    FORMATETC shellFormat{static_cast<CLIPFORMAT>(RegisterClipboardFormatW(CFSTR_SHELLIDLIST)), nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
    Require(SUCCEEDED(originalData->QueryGetData(&shellFormat)), "original shell identities remain available");
    originalData.Reset();
    HMODULE library = LoadLibraryW(arguments[2]);
    Require(library != nullptr, "DLL load without registration");
    using FactoryEntry = HRESULT(__stdcall*)(REFCLSID, REFIID, void**);
    auto get = reinterpret_cast<FactoryEntry>(GetProcAddress(library, "DllGetClassObject"));
    Require(get != nullptr, "class factory export");
    ComPtr<IClassFactory> factory;
    Require(SUCCEEDED(get(CLSID_LopataCollectionFolder, IID_PPV_ARGS(&factory))), "class factory");
    ComPtr<IShellFolder2> folder;
    Require(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&folder))), "folder instance");
    if (count == 4) {
    ComPtr<IShellFolder2> activated;
    const auto activation = CoCreateInstance(CLSID_LopataCollectionFolder, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&activated));
    std::printf("registered activation HRESULT: %08lx\n", static_cast<unsigned long>(activation));
    PIDLIST_ABSOLUTE registeredRoot = nullptr;
    const auto parsed = SHParseDisplayName(L"::{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}", nullptr, &registeredRoot, 0, nullptr);
    std::printf("registered root parsing HRESULT: %08lx\n", static_cast<unsigned long>(parsed));
    if (wcscmp(arguments[3], L"--protocol") == 0) {
        std::puts("before shell protocol parsing");
        PIDLIST_ABSOLUTE protocolRoot = nullptr;
        const auto protocolParsed = SHParseDisplayName(L"shell:::{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}", nullptr, &protocolRoot, 0, nullptr);
        std::printf("shell protocol parsing HRESULT: %08lx\n", static_cast<unsigned long>(protocolParsed));
        CoTaskMemFree(protocolRoot);
    }
    if (SUCCEEDED(parsed)) {
        ComPtr<IShellFolder> desktop, bound;
        auto hr = SHGetDesktopFolder(&desktop);
        PWSTR parsingName = nullptr;
        hr = SHGetNameFromIDList(registeredRoot, SIGDN_DESKTOPABSOLUTEPARSING, &parsingName);
        if (SUCCEEDED(hr)) { std::wprintf(L"desktop-absolute parsing name: %s\n", parsingName); CoTaskMemFree(parsingName); }
        if (wcscmp(arguments[3], L"--open") == 0 && desktop) {
            SHChangeNotify(SHCNE_MKDIR, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, registeredRoot, nullptr);
            const USHORT namespaceDesktop = 0;
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, &namespaceDesktop, nullptr);
            PIDLIST_ABSOLUTE desktopId = nullptr;
            if (SUCCEEDED(SHGetKnownFolderIDList(FOLDERID_Desktop, 0, nullptr, &desktopId))) {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, desktopId, nullptr);
                CoTaskMemFree(desktopId);
            }
            ComPtr<IEnumIDList> roots;
            if (SUCCEEDED(desktop->EnumObjects(nullptr, SHCONTF_FOLDERS | SHCONTF_INCLUDEHIDDEN, &roots)) && roots) {
                PITEMID_CHILD child = nullptr;
                while (roots->Next(1, &child, nullptr) == S_OK) {
                    STRRET name{}; wchar_t buffer[32768]{};
                    if (SUCCEEDED(desktop->GetDisplayNameOf(child, SHGDN_FORPARSING, &name)) &&
                        SUCCEEDED(StrRetToBufW(&name, child, buffer, ARRAYSIZE(buffer)))) {
                        if (wcsstr(buffer, L"D4ACCFB2") || wcsstr(buffer, L"21CBE242")) {
                            SFGAOF attributes = SFGAO_FOLDER | SFGAO_BROWSABLE;
                            desktop->GetAttributesOf(1, const_cast<PCUITEMID_CHILD_ARRAY>(&child), &attributes);
                            std::wprintf(L"desktop custom root: %s attrs:%08lx\n", buffer, static_cast<unsigned long>(attributes));
                        }
                    }
                    CoTaskMemFree(child);
                }
            }
        }
        if (SUCCEEDED(hr)) hr = desktop->BindToObject(registeredRoot, nullptr, IID_PPV_ARGS(&bound));
        std::printf("registered root bind HRESULT: %08lx\n", static_cast<unsigned long>(hr));
        if (count == 4 && wcscmp(arguments[3], L"--open") == 0) {
            SHELLEXECUTEINFOW execute{sizeof(execute)};
            execute.fMask = SEE_MASK_IDLIST | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
            execute.lpIDList = registeredRoot; execute.nShow = SW_SHOWNORMAL;
            const bool executed = ShellExecuteExW(&execute) != FALSE;
            std::printf("registered root ShellExecute IDLIST: %d error:%lu\n", executed, executed ? 0 : GetLastError());
            const auto opened = SHOpenFolderAndSelectItems(registeredRoot, 0, nullptr, 0);
            std::printf("registered root open HRESULT: %08lx\n", static_cast<unsigned long>(opened));
        }
        if (SUCCEEDED(hr)) {
            ComPtr<IEnumIDList> enumItems;
            hr = bound->EnumObjects(nullptr, SHCONTF_NONFOLDERS, &enumItems);
            std::printf("registered root enumerate HRESULT: %08lx\n", static_cast<unsigned long>(hr));
            if (enumItems) {
                unsigned int countItems = 0; PITEMID_CHILD child = nullptr;
                while (enumItems->Next(1, &child, nullptr) == S_OK) { ++countItems; CoTaskMemFree(child); }
                std::printf("registered root items: %u\n", countItems);
            }
        }
        CoTaskMemFree(registeredRoot);
    }
    activated.Reset();
    }
    ComPtr<ICategoryProvider> grouping;
    Require(SUCCEEDED(folder->CreateViewObject(nullptr, IID_PPV_ARGS(&grouping))), "category provider");
    GUID group{}; SHCOLUMNID key{};
    Require(SUCCEEDED(grouping->GetDefaultCategory(&group, &key)) && group == GroupKind, "default file-type grouping");
    ComPtr<ICategorizer> categorizer;
    Require(SUCCEEDED(grouping->CreateCategory(&GroupScenario, IID_PPV_ARGS(&categorizer))), "scenario categorizer");
    categorizer.Reset(); grouping.Reset(); folder.Reset(); factory.Reset();
    using UnloadEntry = HRESULT(__stdcall*)();
    auto unload = reinterpret_cast<UnloadEntry>(GetProcAddress(library, "DllCanUnloadNow"));
    Require(unload && unload() == S_OK, "COM objects released");
    FreeLibrary(library); CoUninitialize();
    return 0;
}
