using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageAnalysisSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string GetProjectsDirectory(StorageSettings storageSettings)
    {
        ArgumentNullException.ThrowIfNull(storageSettings);
        var configuredRoot = storageSettings.Results.Locations
            .Select(location => location.Path?.Trim())
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? AppDataPaths.RuntimeDirectory
            : Path.Combine(configuredRoot, "AI_HUB");
        return Path.Combine(root, "Scenarios", "ImageAnalysis", "Projects");
    }

    public void Save(ImageAnalysisLiterarySession session, StorageSettings storageSettings)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Batch voice shares presentation data; the durable job belongs to ImageBatchStore.
        if (session.SubscenarioId == "literary_batch") return;
        ValidateSessionId(session.SessionId);
        session.UpdatedAt = DateTimeOffset.Now;
        var sessionDirectory = GetSessionDirectory(session.SessionId, storageSettings);
        Directory.CreateDirectory(sessionDirectory);
        SaveAtomic(Path.Combine(sessionDirectory, "session.json"), session);
    }

    public ImageAnalysisLiterarySession? Load(string sessionId, StorageSettings storageSettings)
    {
        ValidateSessionId(sessionId);
        var path = Path.Combine(GetSessionDirectory(sessionId, storageSettings), "session.json");
        return LoadPath(path);
    }

    public string SaveOmniResponse(
        ImageAnalysisLiterarySession session,
        StorageSettings storageSettings,
        string stage,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation,
        string rawProtocol)
    {
        ValidateSessionId(session.SessionId);
        if (stage is not ("analyze" or "compose" or "revise"))
            throw new ArgumentOutOfRangeException(nameof(stage));
        var directory = Path.Combine(GetSessionDirectory(session.SessionId, storageSettings), "OmniResponses");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{stage}_{Guid.NewGuid():N}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            session.SessionId, stage, capturedAt = DateTimeOffset.Now,
            session.ModelId, session.ModelRevision, session.RuntimeVersion,
            promptVersion = session.PipelineVersion,
            session.File, session.Settings, conversation, rawProtocol
        }, JsonOptions);
        // A unique append-only artifact is independent of parsing and session versions.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
        return path;
    }

    public IReadOnlyList<ImageAnalysisLiterarySession> LoadAll(StorageSettings storageSettings)
    {
        try
        {
            var projectsDirectory = GetProjectsDirectory(storageSettings);
            if (!Directory.Exists(projectsDirectory))
            {
                return [];
            }

            var sessions = new List<ImageAnalysisLiterarySession>();
            foreach (var path in Directory.EnumerateFiles(
                projectsDirectory,
                "session.json",
                SearchOption.AllDirectories))
            {
                var session = LoadPath(path);
                if (session is not null)
                {
                    sessions.Add(session);
                }
            }

            return sessions
                .OrderByDescending(session => session.UpdatedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async Task CreateInternalBackupAsync(
        ImageAnalysisLiterarySession session,
        StorageSettings storageSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.File is null)
        {
            throw new FileNotFoundException("The source image is unavailable for the internal backup.");
        }
        var version = session.GetSelectedVersion()
            ?? throw new InvalidOperationException("There is no literary description to back up.");

        var backupDirectory = Path.Combine(
            GetSessionDirectory(session.SessionId, storageSettings),
            "Backup");
        Directory.CreateDirectory(backupDirectory);
        var extension = Path.GetExtension(session.File.SourcePath);
        if (string.IsNullOrWhiteSpace(extension)
            || extension.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
        {
            extension = ".img";
        }
        var imagePath = Path.Combine(backupDirectory, "source" + extension.ToLowerInvariant());
        var descriptionPath = Path.Combine(backupDirectory, "description.md");

        if (session.File.StorageKind == ImageAssetKinds.External && File.Exists(session.File.SourcePath))
        {
            await using (var source = new FileStream(
                session.File.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                useAsync: true))
            await using (var destination = new FileStream(
                imagePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

        }
        else
        {
            imagePath = session.File.StorageKind == ImageAssetKinds.Saved && File.Exists(session.File.SourcePath)
                ? session.File.SourcePath : string.Empty;
        }

        await File.WriteAllTextAsync(
            descriptionPath,
            BuildBackupMarkdown(session, version),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        session.InternalImageCopyPath = imagePath;
        session.InternalDescriptionCopyPath = descriptionPath;
        Save(session, storageSettings);
    }

    private string GetSessionDirectory(string sessionId, StorageSettings storageSettings)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(GetProjectsDirectory(storageSettings), sessionId);
    }

    private static ImageAnalysisLiterarySession? LoadPath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var session = JsonSerializer.Deserialize<ImageAnalysisLiterarySession>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions);
            if (session is null || string.IsNullOrWhiteSpace(session.SessionId))
            {
                return null;
            }
            session.Settings ??= new ImageAnalysisLiterarySettings();
            if (session.SchemaVersion <= 2)
            {
                session.BundleId = ImageAnalysisBundleCatalog.MediumId;
                session.PipelineId = ImageAnalysisPipelineIds.Legacy;
                session.PipelineVersion = ImageAnalysisPipelineIds.LegacyVersion;
                session.ContractVersion = ImageAnalysisPipelineIds.ContractVersion;
                session.RuntimeId = ImageAnalysisRuntimeIds.Legacy;
                session.SchemaVersion = 3;
            }
            session.HiddenConversation ??= [];
            session.Placement ??= new ImageAnalysisPlacementInfo();
            session.RuntimeMetrics ??= new ImageAnalysisRuntimeMetrics();
            session.SpeechResult ??= new ImageAnalysisSpeechResult();
            session.Versions ??= [];
            session.Observations ??= [];
            session.ReviewSummary ??= new ImageAnalysisReviewSummary();
            session.ReviewSummary.Items ??= [];
            session.ReviewSummary.Uncertainties ??= [];
            session.Events ??= [];
            session.ExportedFiles ??= [];
            return session;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveAtomic(string path, ImageAnalysisLiterarySession session)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(session, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildBackupMarkdown(
        ImageAnalysisLiterarySession session,
        ImageAnalysisLiteraryVersion version)
    {
        var isEnglish = session.Settings.LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var title = isEnglish ? "Literary image description" : "Литературное описание изображения";
        var sourceLabel = isEnglish ? "Source file" : "Исходный файл";
        var sessionLabel = isEnglish ? "LOPATA session" : "Сессия ЛОПАТА";
        var versionLabel = isEnglish ? "Text version" : "Версия текста";
        return $"# {title}{Environment.NewLine}{Environment.NewLine}"
            + version.Text.Trim()
            + $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}"
            + $"{sourceLabel}: {session.File?.DisplayName}{Environment.NewLine}"
            + $"{sessionLabel}: {session.SessionId}{Environment.NewLine}"
            + $"{versionLabel}: {version.Number}{Environment.NewLine}";
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("The image analysis session ID contains unsafe characters.");
        }
    }
}
