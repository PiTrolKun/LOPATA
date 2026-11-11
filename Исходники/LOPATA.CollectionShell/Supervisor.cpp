#include "CollectionShell.h"
#include "RecoveryBudget.h"
#include <filesystem>

namespace {
std::atomic<long> Views = 0;
std::atomic<bool> Running = false;
std::atomic<bool> RefreshRequested = false;
void StartMonitor();
void WaitForNextPoll() { for (int i = 0; i < 15 && Views > 0; ++i) Sleep(200); }

ULONGLONG FileTime(const std::wstring& path) {
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes)) return 0;
    return (static_cast<ULONGLONG>(attributes.ftLastWriteTime.dwHighDateTime) << 32) | attributes.ftLastWriteTime.dwLowDateTime;
}
ULONGLONG Now() {
    FILETIME value{}; GetSystemTimeAsFileTime(&value);
    return (static_cast<ULONGLONG>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
}
bool ReadSmall(const std::wstring& path, void* data, DWORD size) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{};
    DWORD read = 0; const bool good = GetFileSizeEx(file, &length) && length.QuadPart == size &&
        ReadFile(file, data, size, &read, nullptr) && read == size;
    CloseHandle(file); return good;
}
void WriteSmall(const std::wstring& path, const void* data, DWORD size) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0; WriteFile(file, data, size, &written, nullptr); CloseHandle(file);
}
void Touch(const std::wstring& path) { const DWORD value = 1; WriteSmall(path, &value, sizeof(value)); }
bool Enabled(const Settings& settings) {
    DWORD value = 0;
    return ReadSmall(settings.root + L"\\enabled.bin", &value, sizeof(value)) && value == 1;
}
bool SameUser(HANDLE process) {
    HANDLE current = nullptr, other = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &current)) return false;
    if (!OpenProcessToken(process, TOKEN_QUERY, &other)) { CloseHandle(current); return false; }
    DWORD currentLength = 0, otherLength = 0;
    GetTokenInformation(current, TokenUser, nullptr, 0, &currentLength);
    GetTokenInformation(other, TokenUser, nullptr, 0, &otherLength);
    std::vector<unsigned char> a(currentLength), b(otherLength);
    const bool equal = GetTokenInformation(current, TokenUser, a.data(), currentLength, &currentLength) &&
        GetTokenInformation(other, TokenUser, b.data(), otherLength, &otherLength) &&
        EqualSid(reinterpret_cast<TOKEN_USER*>(a.data())->User.Sid, reinterpret_cast<TOKEN_USER*>(b.data())->User.Sid);
    CloseHandle(current); CloseHandle(other);
    return equal;
}
std::wstring LockName(const std::wstring& root) {
    unsigned long long hash = 14695981039346656037ull;
    for (const auto c : root) { hash ^= static_cast<unsigned short>(towlower(c)); hash *= 1099511628211ull; }
    return L"Local\\LOPATA.Collection.Launch." + std::to_wstring(hash);
}

void StopUnresponsive(const Settings& settings) {
    // Match process creation time as well as PID/image; a stale PID may have been reused.
    unsigned char identity[12]{};
    if (!ReadSmall(settings.root + L"\\worker.pid", identity, sizeof(identity))) return;
    DWORD pid = 0;
    ULONGLONG recordedCreation = 0;
    memcpy(&pid, identity, sizeof(pid));
    memcpy(&recordedCreation, identity + sizeof(pid), sizeof(recordedCreation));
    if (!pid || pid == GetCurrentProcessId()) return;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
    if (!process) return;
    wchar_t executable[32768]; DWORD length = ARRAYSIZE(executable);
    FILETIME creation{}, exit{}, kernel{}, user{};
    const bool times = GetProcessTimes(process, &creation, &exit, &kernel, &user) != FALSE;
    const auto actualCreation = (static_cast<ULONGLONG>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
    if (QueryFullProcessImageNameW(process, 0, executable, &length) &&
        times && actualCreation == recordedCreation && SameUser(process) &&
        CompareStringOrdinal(executable, length, settings.worker.c_str(), -1, TRUE) == CSTR_EQUAL) {
        Touch(settings.root + L"\\quit.request");
        if (WaitForSingleObject(process, 1500) == WAIT_TIMEOUT) TerminateProcess(process, ERROR_TIMEOUT);
    }
    CloseHandle(process);
}

using Attempts = RecoveryAttempts;
void EnsureWorker(const Settings& settings, bool explicitRefresh, bool openedView) {
    const auto now = Now();
    const auto heartbeat = FileTime(settings.root + L"\\heartbeat");
    HANDLE mutex = CreateMutexW(nullptr, FALSE, LockName(settings.root).c_str());
    if (!mutex) return;
    const auto wait = WaitForSingleObject(mutex, 0);
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { CloseHandle(mutex); return; }
    Attempts attempts{};
    const auto recoveryPath = settings.root + L"\\recovery.bin";
    ReadSmall(recoveryPath, &attempts, sizeof(attempts));
    if (BindRecoveryWorker(attempts, now, RecoveryWorkerFingerprint(settings.worker)))
        WriteSmall(recoveryPath, &attempts, sizeof(attempts));
    if (heartbeat && now >= heartbeat && now - heartbeat < 15ull * 10000000) {
        if (attempts.lastStart && now > attempts.lastStart + 30ull * 10000000) {
            ResetRecoveryCounters(attempts, 0);
            WriteSmall(recoveryPath, &attempts, sizeof(attempts));
        }
        ReleaseMutex(mutex); CloseHandle(mutex); return;
    }
    if (explicitRefresh || !attempts.window || now < attempts.window) ResetRecoveryCounters(attempts, now);
    else if (openedView && RearmRecoveryAfterIdle(attempts, now))
        WriteSmall(recoveryPath, &attempts, sizeof(attempts));
    // A common retry window prevents several Explorer processes from launching a storm.
    if (RecoveryAttemptDue(attempts, now)) {
        if (heartbeat && now >= heartbeat && now - heartbeat > 30ull * 10000000) StopUnresponsive(settings);
        std::wstring command = L"\"" + settings.worker + L"\" --root \"" + settings.root + L"\" --serve";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process{};
        if (CreateProcessW(settings.worker.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &startup, &process)) {
            CloseHandle(process.hThread); CloseHandle(process.hProcess);
        }
        ++attempts.count; attempts.lastStart = now;
        WriteSmall(recoveryPath, &attempts, sizeof(attempts));
    }
    ReleaseMutex(mutex); CloseHandle(mutex);
}

DWORD WINAPI Monitor(void* argument) {
    HMODULE pinned = static_cast<HMODULE>(argument);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ++ModuleObjects;
    std::wstring lease;
    ULONGLONG previousSnapshot = 0;
    ULONGLONG previousStatus = 0;
    bool openedView = true;
    while (Views > 0) {
        try {
            const auto settings = ReadSettings();
            if (settings.root.empty() || settings.worker.empty() || !Enabled(settings)) {
                if (!lease.empty()) { DeleteFileW(lease.c_str()); lease.clear(); }
                WaitForNextPoll();
                continue;
            }
            CreateDirectoryW((settings.root + L"\\leases").c_str(), nullptr);
            lease = settings.root + L"\\leases\\" + std::to_wstring(GetCurrentProcessId()) + L".lease";
            Touch(lease);
            const bool refresh = RefreshRequested.exchange(false);
            if (refresh) Touch(settings.root + L"\\refresh.request");
            EnsureWorker(settings, refresh, openedView);
            openedView = false;
            const auto currentSnapshot = FileTime(settings.root + L"\\snapshot.bin");
            const auto currentStatus = FileTime(settings.root + L"\\status.bin");
            if ((currentSnapshot && currentSnapshot != previousSnapshot) || currentStatus != previousStatus) {
                PIDLIST_ABSOLUTE root = nullptr;
                if (SUCCEEDED(SHParseDisplayName(L"::{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}", nullptr, &root, 0, nullptr))) {
                    SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, root, nullptr);
                    CoTaskMemFree(root);
                }
            }
            previousSnapshot = currentSnapshot;
            previousStatus = currentStatus;
        }
        catch (...) { /* A failed background refresh must never escape into Explorer. */ }
        WaitForNextPoll();
    }
    if (!lease.empty()) DeleteFileW(lease.c_str());
    Running = false;
    --ModuleObjects;
    CoUninitialize();
    // A new view may have arrived between observing zero views and releasing the old lease.
    if (Views > 0) StartMonitor();
    FreeLibraryAndExitThread(pinned, 0);
}
void StartMonitor() {
    if (Running.exchange(true)) return;
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(&AcquireViewLease), &pinned)) { Running = false; return; }
    HANDLE thread = CreateThread(nullptr, 0, Monitor, pinned, 0, nullptr);
    if (thread) CloseHandle(thread);
    else { FreeLibrary(pinned); Running = false; }
}
}

void AcquireViewLease() { ++Views; StartMonitor(); }
void ReleaseViewLease() { --Views; }
void RequestRefresh() { RefreshRequested = true; }

std::wstring CollectionStatusText() {
    const auto settings = ReadSettings();
    DWORD status[2]{};
    Attempts attempts{};
    ReadSmall(settings.root + L"\\recovery.bin", &attempts, sizeof(attempts));
    ReadSmall(settings.root + L"\\status.bin", status, sizeof(status));
    const auto heartbeat = FileTime(settings.root + L"\\heartbeat");
    const auto now = Now();
    const bool responsive = heartbeat && now >= heartbeat && now - heartbeat < 15ull * 10000000;
    if ((!responsive && attempts.count >= 3) || status[0] == 4)
        return Text(L"Не удалось обновить коллекцию. Выберите «Обновить» для повтора.", L"Could not update the collection. Choose Refresh to try again.");
    if (status[0] == 3 || status[1])
        return Text(L"Доступна часть результатов. Некоторые расположения или поиск недоступны.", L"Partial results. Some locations or search are unavailable.");
    if (status[0] == 2)
        return ReadSnapshot(settings).empty() ? Text(L"Созданных работ пока не найдено.", L"No created works found yet.")
            : Text(L"Коллекция обновлена.", L"Collection updated.");
    return Text(L"Коллекция обновляется…", L"Updating collection…");
}
