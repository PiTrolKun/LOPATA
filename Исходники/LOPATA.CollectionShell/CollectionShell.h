#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <propkey.h>
#include <propvarutil.h>
#include <strsafe.h>
#include <wrl/client.h>
#include <atomic>
#include <memory>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

inline constexpr CLSID CLSID_LopataCollectionFolder =
    {0xd4accfb2, 0x2a17, 0x4f52, {0xbb, 0x28, 0x8c, 0xf2, 0xa5, 0x84, 0x36, 0x2a}};
inline constexpr GUID GroupKind =
    {0x5c4823a4, 0xa1c4, 0x4455, {0xb4, 0xee, 0x43, 0xa0, 0x26, 0xbe, 0x12, 0x14}};
inline constexpr GUID GroupScenario =
    {0x72364f8b, 0x6438, 0x44fb, {0x8c, 0x20, 0xea, 0x52, 0x43, 0x18, 0xab, 0x71}};
inline constexpr PROPERTYKEY PKEY_CollectionScenario = {GroupScenario, 2};
inline constexpr wchar_t ClassKey[] =
    L"Software\\Classes\\CLSID\\{D4ACCFB2-2A17-4F52-BB28-8CF2A584362A}";

extern HMODULE Module;
extern std::atomic<long> ModuleObjects;

struct ObjectLifetime {
    ObjectLifetime() { ++ModuleObjects; }
    ~ObjectLifetime() { --ModuleObjects; }
};

struct Entry {
    std::wstring path;
    std::wstring kind;
    std::wstring scenario;
};

struct Settings {
    std::wstring root;
    std::wstring worker;
    bool english = false;
};

Settings ReadSettings();
std::wstring Text(const wchar_t* russian, const wchar_t* english);
std::vector<Entry> ReadSnapshot(const Settings& settings) noexcept;
PIDLIST_RELATIVE CreateEntryId(const Entry& entry) noexcept;
bool ReadEntryId(PCUIDLIST_RELATIVE id, Entry& entry) noexcept;
HRESULT StringResult(const std::wstring& value, STRRET* result) noexcept;
HRESULT OriginalId(const Entry& entry, PIDLIST_ABSOLUTE* id) noexcept;
HRESULT OriginalUi(HWND window, const std::vector<Entry>& entries, REFIID iid, void** result);
HRESULT CreateIdentity(const Entry& entry, REFIID iid, void** result);
HRESULT CreateGroups(REFIID iid, void** result);
HRESULT CreateBackgroundMenu(IShellView* view, REFIID iid, void** result);
void InitializeGrouping(IFolderView2* view);
void RememberGrouping(IFolderView2* view);
HRESULT CreateFolder(REFIID iid, void** result);

// The lease belongs to an open view. The background thread pins the module until it exits.
void AcquireViewLease();
void ReleaseViewLease();
void RequestRefresh();
std::wstring CollectionStatusText();

inline HRESULT CompareResult(int value) {
    return MAKE_HRESULT(SEVERITY_SUCCESS, 0, static_cast<USHORT>(value < 0 ? -1 : value > 0 ? 1 : 0));
}

template<typename T>
ULONG ReleaseObject(T* object, std::atomic<ULONG>& count) {
    const auto remaining = --count;
    if (!remaining) delete object;
    return remaining;
}
