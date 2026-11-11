#pragma once
#include <windows.h>
#include <string>
#include <vector>

struct RecoveryAttempts {
    ULONGLONG window = 0;
    ULONGLONG lastStart = 0;
    DWORD count = 0;
    ULONGLONG workerFingerprint = 0;
};
static_assert(sizeof(RecoveryAttempts) == 32);

inline ULONGLONG RecoveryWorkerFingerprint(const std::wstring& path) {
    const auto length = static_cast<int>(path.size());
    const auto required = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, path.data(), length, nullptr, 0, nullptr, nullptr, 0);
    std::vector<wchar_t> canonical(required > 0 ? required : length);
    if (!required || !LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, path.data(), length,
        canonical.data(), static_cast<int>(canonical.size()), nullptr, nullptr, 0)) canonical.assign(path.begin(), path.end());
    ULONGLONG hash = 14695981039346656037ull;
    for (const auto character : canonical) { hash ^= static_cast<unsigned short>(character); hash *= 1099511628211ull; }
    return hash;
}

inline void ResetRecoveryCounters(RecoveryAttempts& attempts, ULONGLONG window) {
    attempts = {window, 0, 0, attempts.workerFingerprint};
}

inline bool BindRecoveryWorker(RecoveryAttempts& attempts, ULONGLONG now, ULONGLONG fingerprint) {
    if (attempts.workerFingerprint == fingerprint) return false;
    attempts = {now, 0, 0, fingerprint};
    return true;
}

inline bool RecoveryAttemptDue(const RecoveryAttempts& attempts, ULONGLONG now) {
    if (attempts.count >= 3) return false;
    const auto pause = (1ull << attempts.count) * 10000000;
    return !attempts.lastStart || now - attempts.lastStart > pause + 10ull * 10000000;
}

inline bool RearmRecoveryAfterIdle(RecoveryAttempts& attempts, ULONGLONG now) {
    constexpr ULONGLONG idleBeforeReopen = 60ull * 10000000;
    if (attempts.count < 3 || !attempts.lastStart || now < attempts.lastStart ||
        now - attempts.lastStart < idleBeforeReopen) return false;
    ResetRecoveryCounters(attempts, now);
    return true;
}
