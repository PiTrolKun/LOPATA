using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.ExternalAgents;

internal static class AgentDiagnosticsProjection
{
    // Message, stdout/stderr, URLs, prompts, paths and arbitrary names can contain personal data.
    // Return only catalogued values, fixed codes and numeric status fields, including for errors.
    private static readonly HashSet<string> ErrorTypes = new(StringComparer.Ordinal)
    {
        "IOException", "FileNotFoundException", "DirectoryNotFoundException", "UnauthorizedAccessException",
        "HttpRequestException", "SocketException", "TimeoutException", "TaskCanceledException",
        "OperationCanceledException", "InvalidDataException", "JsonException", "InvalidOperationException",
        "NotSupportedException", "OutOfMemoryException", "Win32Exception", "BadImageFormatException",
        "DllNotFoundException", "TypeInitializationException", "ArgumentException", "FormatException"
    };

    private static readonly HashSet<string> ProcessComponents = new(StringComparer.Ordinal)
    {
        "Application", "StartupPreparation", "ComputerPassport", "EngineSets", "HardwareReadiness",
        "ModelStorage", "AudioShell", "ImageShell", "MailHub", "Collection", "VoiceInput", "Shorts",
        "GameSounds", "Music.Removal", "Generation.Removal", "Books.Smart", "WindowsIntegration",
        "AnimatedAssets.WebM", "Video.H3.Generate", "Video.VACE.Generate", "VideoUpscale.FFmpeg",
        "SongClip.InfiniteTalk", "Music.Video.Probe", "Music.Video.Encode", "Music.ACE.XL", "Music.Audio",
        "Music.YuE2.BF16", "Music.YuE2", "Music.Cover.Score", "Music.DiffRhythm2", "Music.HardwareProbe",
        "Music.HeartMuLa", "Music.Score.Parser", "Music.Cover.Lyrics", "Music.Studio", "Qwen25OmniRuntimeService"
    };
    private static readonly Lazy<HashSet<string>> CatalogIds = new(() =>
    {
        // A broken installation can be missing a catalog. Its existing errors are still readable.
        try { return ComponentCatalog.All.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal); }
        catch (TypeInitializationException) { return []; }
    });

    internal static AgentDiagnosticEvent? Project(string source, string fileKey, long offset, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        string? stage = null, outcome = null, component = null, group = null, relation = null, scenario = null;
        string? session = null, type = null, reason = null;
        int? exitCode = null;
        string? operation;
        if (RecordTime(json) is not { } time) return null;
        switch (source)
        {
            case "components":
                var data = Field(json, "data");
                operation = String(json, "event");
                if (String(data, "id") is { Length: > 0 } id)
                {
                    component = CatalogIds.Value.Contains(id) ? id : null;
                    group = Hash(id);
                    relation = "same_component";
                }
                switch (operation)
                {
                    case "component_download_started": stage = "download"; outcome = "started"; break;
                    case "component_download_completed": stage = "download"; outcome = "completed"; break;
                    case "component_verified":
                        stage = "verification";
                        outcome = String(data, "status") switch
                        {
                            ComponentInstallStatuses.Installed or ComponentInstallStatuses.BuiltIn => "completed",
                            ComponentInstallStatuses.NeedsVerification or ComponentInstallStatuses.Failed => "failed",
                            _ when Field(data, "computedSha256").ValueKind == JsonValueKind.String => "completed",
                            _ => "recorded"
                        };
                        break;
                    case "component_install_failed": stage = "install"; outcome = "failed"; break;
                    case "component_cleanup_failed": stage = "cleanup"; outcome = "failed"; break;
                    default: return null;
                }
                type = KnownError(String(data, "errorType"));
                reason = KnownReason(String(data, "message", "lastError"));
                if (type is "TaskCanceledException" or "OperationCanceledException") outcome = "cancelled";
                break;
            case "recovery":
                operation = String(json, "action");
                stage = "application";
                outcome = operation switch
                {
                    "unexpected_exit" or "restart_limit" or "redirect_timed_out" => "failed",
                    "interrupted_exit" => "interrupted",
                    "expected_exit" => "completed",
                    "launch" or "watching" or "restart" or "restart_safe" => "started",
                    _ => null
                };
                if (outcome == null) return null;
                var detail = Field(json, "detail");
                exitCode = Number(detail) ?? Number(Field(detail, "ExitCode", "exitCode"));
                if (Number(Field(json, "pid")) is { } pid) { group = $"pid-{pid}"; relation = "same_reported_pid"; }
                break;
            case "processes":
                var kind = String(json, "kind");
                var label = String(json, "component");
                if (label != null && ProcessComponents.Contains(label)) component = label;
                scenario = component switch
                {
                    "Shorts" => "shorts", "GameSounds" => "game_sounds", "MailHub" => "mail",
                    "AnimatedAssets.WebM" => "animated_assets", "Video.H3.Generate" or "Video.VACE.Generate" => "video",
                    "VideoUpscale.FFmpeg" => "video_upscale",
                    "VideoConverter.FFmpeg" or "VideoConverter.Probe" or "VideoConverter" => "video_converter",
                    "SongClip.InfiniteTalk" => "song_clip",
                    _ when component?.StartsWith("Music.", StringComparison.Ordinal) == true => "music",
                    _ => null
                };
                var processDetail = String(json, "detail");
                type = KnownError(processDetail);
                reason = KnownReason(processDetail);
                switch (kind)
                {
                    case "exited":
                        if (!int.TryParse(processDetail, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)) return null;
                        operation = kind; stage = "worker"; exitCode = code; outcome = code == 0 ? "completed" : "failed"; break;
                    case "startup_check_failed": case "startup_language_failed": case "recovery_supervisor_failed":
                    case "reader_file_startup_failed": case "video_file_startup_failed": case "book_shell_startup_failed":
                    case "reader_file_launch_failed": case "video_file_launch_failed": case "vector_file_launch_failed":
                    case "audio_shell_launch_failed": case "image_shell_launch_failed":
                    case "audio_shell_startup_failed": case "image_shell_startup_failed":
                        operation = kind; stage = "startup"; outcome = "failed"; break;
                    case "startup_enter": case "startup_window_construct_begin": case "startup_check_started":
                        operation = kind; stage = "startup"; outcome = "started"; break;
                    case "startup_check_finished": case "startup_window_construct_end": case "startup_window_visible": case "startup_window_background":
                        operation = kind; stage = "startup"; outcome = "completed"; break;
                    default:
                        if (!FailureName(kind)) return null;
                        operation = "process_error"; stage = "runtime"; outcome = "failed"; break;
                }
                if (Number(Field(json, "owner")) is { } owner)
                {
                    group = $"owner-{owner}"; relation = "same_reported_owner_pid";
                }
                break;
            case "updates":
                if (String(json, "stage") != "prepare" || Field(json, "error").ValueKind != JsonValueKind.String) return null;
                operation = "update_prepare_failed"; stage = "prepare"; outcome = "failed";
                var updateError = String(json, "error");
                type = KnownError(updateError); reason = KnownReason(updateError);
                break;
            case "literary":
                operation = String(json, "kind"); scenario = "literary";
                var envelope = Field(json, "data");
                switch (operation)
                {
                    case "startup_failure": case "startup_ready": case "startup_stage":
                        var startupData = Field(envelope, "data");
                        stage = String(envelope, "stage") switch
                        {
                            "license_check" or "model_resolution" or "memory_placement" or "backend_check" or
                            "gpu_inventory" or "cache_preparation" or "process_start" or "health_check" or "capacity_read" => String(envelope, "stage"),
                            _ => "model_startup"
                        };
                        type = KnownError(String(startupData, "exception"));
                        var startupReason = String(startupData, "reason");
                        reason = startupReason is "cancelled" or "startup_timeout" or "process_exited" ? startupReason : null;
                        exitCode = Number(Field(Field(startupData, "process"), "ExitCode"));
                        outcome = operation switch { "startup_ready" => "completed", "startup_stage" => "started",
                            _ when reason == "cancelled" => "cancelled", _ => "failed" };
                        break;
                    case "attempt_complete": stage = "request"; outcome = "completed"; break;
                    case "failure":
                        stage = "request"; outcome = Field(envelope, "userCancelled").ValueKind == JsonValueKind.True ? "cancelled" : "failed";
                        type = KnownError(String(envelope, "exception")); break;
                    case "truncated": case "disabled": stage = "diagnostic_recording"; outcome = "interrupted"; break;
                    default: return null;
                }
                session = fileKey; group = session; relation = "same_request_log";
                break;
            case "llama":
                operation = String(json, "kind"); component = "llama"; stage = "model_startup";
                outcome = operation switch
                {
                    "hybrid_plan_failure" or "preference_write_failed" or "vram_monitor_unavailable" => "failed",
                    "ready" => "completed", "begin" or "loading" or "launch" => "started", _ => null
                };
                if (outcome == null) return null;
                if (outcome == "failed") type = KnownError(String(json, "data"));
                session = fileKey; group = session; relation = "same_startup_log";
                break;
            case "core": case "debug": case "uncertainty":
                operation = String(json, "type", "Type");
                scenario = source;
                var payload = Field(json, "payload", "Payload");
                type = KnownError(String(payload, "errorType", "ErrorType"));
                reason = KnownReason(String(payload, "message", "Message"));
                (stage, outcome) = operation switch
                {
                    "executor_download_started" => ("download", "started"),
                    "executor_download_completed" => ("download", "completed"),
                    "executor_download_cancelled" => ("download", "cancelled"),
                    "executor_download_error" => ("download", "failed"),
                    "catalog_startup_sync_started" => ("catalog", "started"),
                    "catalog_startup_sync_error" => ("catalog", "failed"),
                    "catalog_startup_sync_cancelled" => ("catalog", "cancelled"),
                    "scenario_restore_failed" => ("restore", "failed"),
                    "session_checkpoint_failed" or "session_completion_checkpoint_failed" or "session_pause_checkpoint_failed" => ("save", "failed"),
                    "executor_artifact_error" or "executor_final_result_failed" => ("export", "failed"),
                    "executor_failed" or "executor_tool_request_failed" or "scenario_structure_error" or "tool_error" => ("execution", "failed"),
                    "scenario_request_cancelled" => ("execution", "cancelled"),
                    _ => (null, null)
                };
                if (outcome == null)
                {
                    if (!FailureName(operation)) return null;
                    operation = "session_error"; stage = "session"; outcome = "failed";
                }
                session = String(json, "sessionId", "SessionId") is { } sessionId ? Hash(sessionId) : fileKey;
                group = session; relation = "same_session";
                break;
            default:
                return null;
        }
        var eventId = string.Create(CultureInfo.InvariantCulture, $"{source}:{fileKey}:{offset}:{Hash(json.GetRawText())}");
        return new AgentDiagnosticEvent(eventId, time, source, operation!, stage!, outcome!, scenario, component,
            session, exitCode, type, reason, group, relation);
    }

    internal static DateTimeOffset? RecordTime(JsonElement json)
    {
        var stamp = Field(json, "utcTime", "UtcTime", "localTime", "LocalTime", "timestamp", "at", "utc");
        return stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTimeOffset(out var time) ? time : null;
    }

    internal static string FileKey(string path)
    {
        // Both names refer to the same chronological journal across rotation.
        var origin = path.EndsWith(".previous", StringComparison.OrdinalIgnoreCase) ? path[..^9] : path;
        return Hash(origin.ToUpperInvariant());
    }

    private static bool FailureName(string? kind) => kind != null &&
        (kind.EndsWith("_failed", StringComparison.Ordinal) || kind.EndsWith("_error", StringComparison.Ordinal));

    private static string? KnownReason(string? message)
    {
        if (message == null) return null;
        if (message.StartsWith("Access to the path ", StringComparison.OrdinalIgnoreCase) && message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Отказано в доступе к ", StringComparison.OrdinalIgnoreCase)) return "access_denied";
        if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("используется другим процессом", StringComparison.OrdinalIgnoreCase)) return "file_in_use";
        if (message.StartsWith("The SSL connection could not be established", StringComparison.Ordinal))
            return "tls_connection_failed";
        if (message.StartsWith("Not enough free space for ", StringComparison.Ordinal)) return "insufficient_disk_space";
        if (message.StartsWith("Downloaded size ", StringComparison.Ordinal) &&
            message.Contains("differs from catalog size", StringComparison.Ordinal)) return "download_size_mismatch";
        if (message.StartsWith("Downloaded component SHA-256 does not match", StringComparison.Ordinal))
            return "checksum_mismatch";
        if (message.StartsWith("Installed component did not pass its health-check", StringComparison.Ordinal) ||
            message.StartsWith("Health-check did not find the expected installed artifact", StringComparison.Ordinal))
            return "component_health_check_failed";
        const string http = "Response status code does not indicate success: ";
        var start = message.IndexOf(http, StringComparison.Ordinal);
        if (start >= 0 && message.Length >= start + http.Length + 3 &&
            int.TryParse(message.AsSpan(start + http.Length, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var status) &&
            status is >= 400 and <= 599)
            return $"http_{status}";
        return null;
    }

    private static string? KnownError(string? value)
    {
        if (value == null) return null;
        var separator = value.IndexOfAny([':', ' ', '\r', '\n']);
        var qualified = separator < 0 ? value : value[..separator];
        var name = qualified[(qualified.LastIndexOf('.') + 1)..];
        return ErrorTypes.Contains(name) ? name : null;
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static int? Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    private static string? String(JsonElement json, params ReadOnlySpan<string> keys)
    {
        var value = Field(json, keys);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement Field(JsonElement json, params ReadOnlySpan<string> keys)
    {
        if (json.ValueKind == JsonValueKind.Object)
            foreach (var key in keys)
                if (json.TryGetProperty(key, out var value)) return value;
        return default;
    }
}
