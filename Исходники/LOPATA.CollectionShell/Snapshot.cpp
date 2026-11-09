#include "CollectionShell.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include <mutex>

namespace {
constexpr size_t MaximumSnapshot = 16 * 1024 * 1024;
constexpr size_t MaximumEntries = 20000;
constexpr unsigned short ItemSignature = 0x4c43;

std::wstring RegistryString(HKEY key, const wchar_t* name) {
    DWORD length = 0;
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, nullptr, &length) != ERROR_SUCCESS ||
        length > 65536 || length < sizeof(wchar_t)) return {};
    std::vector<wchar_t> buffer(length / sizeof(wchar_t) + 1);
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, buffer.data(), &length) != ERROR_SUCCESS) return {};
    return buffer.data();
}

class Cursor {
    const unsigned char* current;
    size_t remaining;
public:
    Cursor(const void* data, size_t length) : current(static_cast<const unsigned char*>(data)), remaining(length) {}
    template<typename T> bool Read(T& result) {
        if (remaining < sizeof(T)) return false;
        memcpy(&result, current, sizeof(T));
        current += sizeof(T); remaining -= sizeof(T);
        return true;
    }
    bool String(std::wstring& value) {
        unsigned int characters = 0;
        if (!Read(characters) || characters > 32760 || characters > remaining / sizeof(wchar_t)) return false;
        value.resize(characters);
        memcpy(value.data(), current, characters * sizeof(wchar_t));
        current += characters * sizeof(wchar_t); remaining -= characters * sizeof(wchar_t);
        return std::none_of(value.begin(), value.end(), [](wchar_t c) { return c < 32; });
    }
    bool Empty() const { return remaining == 0; }
};

bool Valid(const Entry& entry) {
    return !entry.path.empty() && !PathIsRelativeW(entry.path.c_str()) &&
        entry.path.size() < 32760 && entry.kind.size() <= 512 && entry.scenario.size() <= 512;
}

void AppendString(std::vector<unsigned char>& bytes, const std::wstring& value) {
    const unsigned int count = static_cast<unsigned int>(value.size());
    auto p = reinterpret_cast<const unsigned char*>(&count);
    bytes.insert(bytes.end(), p, p + sizeof(count));
    p = reinterpret_cast<const unsigned char*>(value.data());
    bytes.insert(bytes.end(), p, p + value.size() * sizeof(wchar_t));
}
}

Settings ReadSettings() {
    Settings result;
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, ClassKey, 0, KEY_QUERY_VALUE, &key) != ERROR_SUCCESS) return result;
    result.root = RegistryString(key, L"DataDirectory");
    result.worker = RegistryString(key, L"WorkerPath");
    result.english = RegistryString(key, L"Language") == L"en";
    RegCloseKey(key);
    if (result.root.empty() || PathIsRelativeW(result.root.c_str())) result.root.clear();
    if (result.worker.empty() || PathIsRelativeW(result.worker.c_str())) result.worker.clear();
    return result;
}

std::wstring Text(const wchar_t* russian, const wchar_t* english) {
    return ReadSettings().english ? english : russian;
}

static bool ReadSnapshotFile(const Settings& settings, std::vector<Entry>& output) noexcept {
    try {
        if (settings.root.empty()) return false;
        const auto path = settings.root + L"\\snapshot.bin";
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || length.QuadPart < 20 || length.QuadPart > MaximumSnapshot) {
            CloseHandle(file); return false;
        }
        std::vector<unsigned char> bytes(static_cast<size_t>(length.QuadPart));
        DWORD read = 0;
        const auto good = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr);
        CloseHandle(file);
        if (!good || read != bytes.size()) return false;
        Cursor cursor(bytes.data(), bytes.size());
        unsigned int magic = 0, version = 0, count = 0;
        long long generation = 0;
        if (!cursor.Read(magic) || magic != 0x3143504c || !cursor.Read(version) || version != 1 ||
            !cursor.Read(generation) || !cursor.Read(count) || count > MaximumEntries) return false;
        std::vector<Entry> results;
        results.reserve(count);
        for (unsigned int i = 0; i < count; ++i) {
            Entry entry;
            if (!cursor.String(entry.path) || !cursor.String(entry.kind) || !cursor.String(entry.scenario) || !Valid(entry)) return false;
            results.push_back(std::move(entry));
        }
        if (!cursor.Empty()) return false;
        output = std::move(results);
        return true;
    }
    catch (...) { return false; }
}

std::vector<Entry> ReadSnapshot(const Settings& settings) noexcept {
    static std::mutex sync;
    static std::wstring cachedRoot;
    static std::vector<Entry> lastGood;
    try {
        std::lock_guard<std::mutex> lock(sync);
        if (cachedRoot != settings.root) { cachedRoot = settings.root; lastGood.clear(); }
        std::vector<Entry> latest;
        if (ReadSnapshotFile(settings, latest)) lastGood = std::move(latest);
        return lastGood;
    }
    catch (...) { return {}; }
}

PIDLIST_RELATIVE CreateEntryId(const Entry& entry) noexcept {
    try {
        if (!Valid(entry)) return nullptr;
        std::vector<unsigned char> bytes(sizeof(USHORT) * 2);
        memcpy(bytes.data() + sizeof(USHORT), &ItemSignature, sizeof(ItemSignature));
        AppendString(bytes, entry.path); AppendString(bytes, entry.kind); AppendString(bytes, entry.scenario);
        if (bytes.size() > std::numeric_limits<USHORT>::max()) return nullptr;
        const auto length = static_cast<USHORT>(bytes.size());
        memcpy(bytes.data(), &length, sizeof(length));
        bytes.resize(bytes.size() + sizeof(USHORT), 0);
        auto id = static_cast<PIDLIST_RELATIVE>(CoTaskMemAlloc(bytes.size()));
        if (id) memcpy(id, bytes.data(), bytes.size());
        return id;
    }
    catch (...) { return nullptr; }
}

bool ReadEntryId(PCUIDLIST_RELATIVE id, Entry& entry) noexcept {
    try {
        if (!id || id->mkid.cb < 16) return false;
        const auto bytes = reinterpret_cast<const unsigned char*>(id);
        USHORT signature = 0;
        memcpy(&signature, bytes + sizeof(USHORT), sizeof(USHORT));
        if (signature != ItemSignature) return false;
        Cursor cursor(bytes + 4, id->mkid.cb - 4);
        return cursor.String(entry.path) && cursor.String(entry.kind) && cursor.String(entry.scenario) && cursor.Empty() && Valid(entry);
    }
    catch (...) { return false; }
}

HRESULT StringResult(const std::wstring& value, STRRET* result) noexcept {
    if (!result) return E_POINTER;
    result->uType = STRRET_WSTR;
    return SHStrDupW(value.c_str(), &result->pOleStr);
}

HRESULT OriginalId(const Entry& entry, PIDLIST_ABSOLUTE* id) noexcept {
    if (!id) return E_POINTER;
    *id = nullptr;
    return SHParseDisplayName(entry.path.c_str(), nullptr, id, 0, nullptr);
}
