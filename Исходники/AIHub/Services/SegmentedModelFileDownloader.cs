using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class SegmentedModelFileDownloader
{
    private const int BufferSize = 1024 * 1024;
    private const long DefaultMinimumSegmentBytes = 64L * 1024 * 1024;
    private const int MaximumSupportedConnections = 8;
    private const int MaximumAttempts = 4;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly long _minimumSegmentBytes;

    public SegmentedModelFileDownloader(
        HttpClient httpClient,
        long minimumSegmentBytes = DefaultMinimumSegmentBytes)
    {
        _httpClient = httpClient;
        _minimumSegmentBytes = Math.Max(1, minimumSegmentBytes);
    }

    public int MaximumParallelConnections { get; set; }

    public async Task DownloadAsync(
        ManagedModelArtifactCard card,
        ManagedModelArtifactFile file,
        string targetPath,
        long completedCardBytes,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(file.SourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Managed model files must use an absolute HTTPS source.");
        }

        var partialPath = targetPath + ".part";
        var existingBytes = GetFileLength(partialPath);
        if (existingBytes > file.SizeBytes)
        {
            throw new InvalidDataException("The partial model file is larger than the pinned artifact.");
        }
        if (existingBytes == file.SizeBytes)
        {
            return;
        }

        var requestedConnections = ResolveConnectionCount(file.SizeBytes - existingBytes);
        if (requestedConnections <= 1
            || !await SupportsRangeRequestsAsync(source, existingBytes, cancellationToken))
        {
            await DownloadSequentialAsync(
                source,
                partialPath,
                file.SizeBytes,
                card,
                file,
                completedCardBytes,
                progress,
                cancellationToken);
            return;
        }

        var manifest = LoadOrCreateManifest(source, partialPath, file, requestedConnections);
        var initialStoredBytes = manifest.PrefixBytes + manifest.Segments
            .Where(segment => segment.EndExclusive > manifest.PrefixBytes)
            .Sum(segment => Math.Min(segment.Length, GetFileLength(ResolveSegmentPath(partialPath, segment))));
        var reporter = new AggregateProgressReporter(
            card,
            file,
            completedCardBytes,
            initialStoredBytes,
            progress);
        var pending = manifest.Segments
            .Where(segment => segment.EndExclusive > manifest.PrefixBytes)
            .ToList();
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(requestedConnections, pending.Count),
                CancellationToken = cancellationToken
            },
            async (segment, token) => await DownloadSegmentWithRetriesAsync(
                source,
                partialPath,
                segment,
                reporter,
                token));

        reporter.Report("assembling", file.RelativePath);
        await AssembleAsync(partialPath, manifest, cancellationToken);
    }

    public static long GetStoredBytes(string targetPath, long expectedSize)
    {
        if (File.Exists(targetPath))
        {
            return Math.Min(expectedSize, GetFileLength(targetPath));
        }
        return GetPartialStoredBytes(targetPath, expectedSize);
    }

    public static long GetPartialStoredBytes(string targetPath, long expectedSize)
    {
        var partialPath = targetPath + ".part";
        var stored = GetFileLength(partialPath);
        var manifest = TryLoadManifest(GetManifestPath(partialPath));
        if (manifest is not null)
        {
            stored += manifest.Segments
                .Where(segment => segment.EndExclusive > manifest.PrefixBytes)
                .Sum(segment => Math.Min(segment.Length, GetFileLength(ResolveSegmentPath(partialPath, segment))));
        }
        return Math.Min(expectedSize, stored);
    }

    public long GetAssemblyReserveBytes(string targetPath, long expectedSize)
    {
        var partialPath = targetPath + ".part";
        var manifest = TryLoadManifest(GetManifestPath(partialPath));
        if (manifest is not null)
        {
            return manifest.Segments
                .Where(segment => segment.EndExclusive > manifest.PrefixBytes)
                .Select(segment => segment.Length)
                .DefaultIfEmpty(0)
                .Max();
        }
        var remainingBytes = Math.Max(0, expectedSize - GetFileLength(partialPath));
        var connections = ResolveConnectionCount(remainingBytes);
        return connections <= 1 ? 0 : (long)Math.Ceiling(remainingBytes / (double)connections);
    }

    public static IReadOnlyList<string> GetPartialArtifactPaths(string targetPath)
    {
        var partialPath = targetPath + ".part";
        var directory = Path.GetDirectoryName(partialPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [partialPath, GetManifestPath(partialPath)];
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            partialPath,
            GetManifestPath(partialPath)
        };
        if (Directory.Exists(directory))
        {
            var prefix = Path.GetFileName(partialPath) + ".segment.";
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                paths.Add(path);
            }
        }
        return paths.ToList();
    }

    private int ResolveConnectionCount(long remainingBytes)
    {
        var desired = MaximumParallelConnections switch
        {
            1 or 2 or 4 or 8 => MaximumParallelConnections,
            _ when remainingBytes >= 2L * 1024 * 1024 * 1024 => 8,
            _ when remainingBytes >= 512L * 1024 * 1024 => 4,
            _ when remainingBytes >= 128L * 1024 * 1024 => 2,
            _ => 1
        };
        var usefulSegments = Math.Max(1, (int)Math.Ceiling(remainingBytes / (double)_minimumSegmentBytes));
        return Math.Clamp(Math.Min(desired, usefulSegments), 1, MaximumSupportedConnections);
    }

    private async Task<bool> SupportsRangeRequestsAsync(
        Uri source,
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(source);
        request.Headers.Range = new RangeHeaderValue(offset, offset);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            return false;
        }
        return response.Content.Headers.ContentRange?.From is not { } from || from == offset;
    }

    private async Task DownloadSequentialAsync(
        Uri source,
        string partialPath,
        long expectedSize,
        ManagedModelArtifactCard card,
        ManagedModelArtifactFile file,
        long completedCardBytes,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(GetManifestPath(partialPath))
            || GetPartialArtifactPaths(partialPath[..^5]).Any(path => path.Contains(".segment.", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("A segmented resume map exists, but the server no longer accepts range requests.");
        }
        var existingBytes = GetFileLength(partialPath);
        using var request = CreateRequest(source);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (existingBytes > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            if (response.Content.Headers.ContentLength is { } fullLength && fullLength != expectedSize)
                throw new InvalidDataException("The complete replacement response has an unexpected size.");
            var restart = new AggregateProgressReporter(card, file, completedCardBytes, 0, progress);
            restart.Report("restarting", file.RelativePath);
            await ModelDownloadRestart.ReplacePartialAsync(partialPath, file,
                staging => CopyResponseAsync(response, staging, expectedSize, restart, cancellationToken), cancellationToken);
            return;
        }
        if (response.StatusCode == HttpStatusCode.PartialContent
            && (response.Content.Headers.ContentRange is not { } range || range.From != existingBytes
                || range.To != expectedSize - 1 || range.Length != expectedSize))
            throw new InvalidDataException("The source returned an unexpected continuation range.");
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            throw new InvalidDataException("The source did not return a file response.");
        var reporter = new AggregateProgressReporter(card, file, completedCardBytes, existingBytes, progress);
        await CopyResponseAsync(response, partialPath, expectedSize - existingBytes, reporter, cancellationToken);
        if (GetFileLength(partialPath) != expectedSize)
        {
            throw new EndOfStreamException("The model source closed before the expected file size was received.");
        }
    }

    private async Task DownloadSegmentWithRetriesAsync(
        Uri source,
        string partialPath,
        SegmentedDownloadPart segment,
        AggregateProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadSegmentOnceAsync(source, partialPath, segment, reporter, cancellationToken);
                return;
            }
            catch (Exception error) when (
                error is HttpRequestException or IOException or TaskCanceledException
                && attempt < MaximumAttempts
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }

    private async Task DownloadSegmentOnceAsync(
        Uri source,
        string partialPath,
        SegmentedDownloadPart segment,
        AggregateProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var segmentPath = ResolveSegmentPath(partialPath, segment);
        var existingBytes = GetFileLength(segmentPath);
        if (existingBytes > segment.Length)
        {
            throw new InvalidDataException("A partial download segment is larger than its pinned range.");
        }
        if (existingBytes == segment.Length)
        {
            return;
        }
        using var request = CreateRequest(source);
        var rangeStart = segment.Start + existingBytes;
        request.Headers.Range = new RangeHeaderValue(rangeStart, segment.EndExclusive - 1);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException("The source stopped supporting the pinned byte range.");
        }
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From is { } from && from != rangeStart)
        {
            throw new InvalidDataException("The source returned a different byte range than requested.");
        }
        if (contentRange?.To is { } to && to != segment.EndExclusive - 1)
        {
            throw new InvalidDataException("The source returned a different byte range end than requested.");
        }
        await CopyResponseAsync(response, segmentPath, segment.Length - existingBytes, reporter, cancellationToken);
        if (GetFileLength(segmentPath) != segment.Length)
        {
            throw new EndOfStreamException("A model segment ended before its pinned range was complete.");
        }
    }

    private static async Task CopyResponseAsync(
        HttpResponseMessage response,
        string destinationPath,
        long remainingBytes,
        AggregateProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(
            destinationPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        var written = 0L;
        while (written < remainingBytes)
        {
            var requested = (int)Math.Min(buffer.Length, remainingBytes - written);
            var read = await remote.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                break;
            }
            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            reporter.Add(read);
        }
        await local.FlushAsync(cancellationToken);
    }

    private SegmentedDownloadManifest LoadOrCreateManifest(
        Uri source,
        string partialPath,
        ManagedModelArtifactFile file,
        int connections)
    {
        var manifestPath = GetManifestPath(partialPath);
        if (File.Exists(manifestPath))
        {
            var existing = TryLoadManifest(manifestPath)
                ?? throw new InvalidDataException("The segmented download resume map is damaged.");
            ValidateManifest(existing, source, partialPath, file);
            RecoverInterruptedAssembly(partialPath, existing, manifestPath);
            return existing;
        }
        if (GetPartialArtifactPaths(partialPath[..^5]).Any(path => path.Contains(".segment.", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Download segments exist without a valid resume map.");
        }
        var prefixBytes = GetFileLength(partialPath);
        var manifest = new SegmentedDownloadManifest
        {
            SourceUrl = source.AbsoluteUri,
            ExpectedSize = file.SizeBytes,
            Sha256 = file.Sha256,
            InitialPrefixBytes = prefixBytes,
            PrefixBytes = prefixBytes,
            Segments = CreateSegments(partialPath, prefixBytes, file.SizeBytes, connections)
        };
        SaveManifest(manifestPath, manifest);
        return manifest;
    }

    private static List<SegmentedDownloadPart> CreateSegments(
        string partialPath,
        long start,
        long endExclusive,
        int count)
    {
        var remaining = endExclusive - start;
        var baseSize = remaining / count;
        var extra = remaining % count;
        var cursor = start;
        var segments = new List<SegmentedDownloadPart>(count);
        for (var index = 0; index < count; index++)
        {
            var length = baseSize + (index < extra ? 1 : 0);
            var end = cursor + length;
            segments.Add(new SegmentedDownloadPart
            {
                Index = index,
                Start = cursor,
                EndExclusive = end,
                FileName = Path.GetFileName(partialPath) + $".segment.{index:D2}"
            });
            cursor = end;
        }
        return segments;
    }

    private static async Task AssembleAsync(
        string partialPath,
        SegmentedDownloadManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(partialPath);
        RecoverInterruptedAssembly(partialPath, manifest, manifestPath);
        foreach (var segment in manifest.Segments.Where(item => item.EndExclusive > manifest.PrefixBytes).OrderBy(item => item.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment.Start != manifest.PrefixBytes)
            {
                throw new InvalidDataException("The segmented download map is not contiguous.");
            }
            var segmentPath = ResolveSegmentPath(partialPath, segment);
            if (GetFileLength(segmentPath) != segment.Length)
            {
                throw new InvalidDataException("A segment is incomplete and cannot be assembled.");
            }
            await using (var source = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, BufferSize, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            manifest.PrefixBytes = segment.EndExclusive;
            SaveManifest(manifestPath, manifest);
            File.Delete(segmentPath);
        }
        if (GetFileLength(partialPath) != manifest.ExpectedSize)
        {
            throw new InvalidDataException("The assembled model file has an unexpected size.");
        }
        File.Delete(manifestPath);
    }

    private static void RecoverInterruptedAssembly(
        string partialPath,
        SegmentedDownloadManifest manifest,
        string manifestPath)
    {
        var actualPrefix = GetFileLength(partialPath);
        if (actualPrefix < manifest.PrefixBytes)
        {
            throw new InvalidDataException("The contiguous partial model file is shorter than its resume map.");
        }
        if (actualPrefix > manifest.PrefixBytes)
        {
            using var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(manifest.PrefixBytes);
        }
        foreach (var segment in manifest.Segments.Where(segment => segment.EndExclusive <= manifest.PrefixBytes))
        {
            var path = ResolveSegmentPath(partialPath, segment);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        SaveManifest(manifestPath, manifest);
    }

    private static void ValidateManifest(
        SegmentedDownloadManifest manifest,
        Uri source,
        string partialPath,
        ManagedModelArtifactFile file)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(manifest.SourceUrl, source.AbsoluteUri, StringComparison.Ordinal)
            || manifest.ExpectedSize != file.SizeBytes
            || !string.Equals(manifest.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
            || manifest.InitialPrefixBytes < 0
            || manifest.PrefixBytes < manifest.InitialPrefixBytes
            || manifest.PrefixBytes > manifest.ExpectedSize
            || manifest.Segments.Count == 0)
        {
            throw new InvalidDataException("The segmented download resume map does not match the pinned artifact.");
        }
        var cursor = manifest.InitialPrefixBytes;
        foreach (var segment in manifest.Segments.OrderBy(segment => segment.Start))
        {
            if (segment.Start != cursor
                || segment.EndExclusive <= segment.Start
                || segment.EndExclusive > manifest.ExpectedSize
                || segment.FileName != Path.GetFileName(partialPath) + $".segment.{segment.Index:D2}")
            {
                throw new InvalidDataException("The segmented download resume map contains an unsafe range.");
            }
            _ = ResolveSegmentPath(partialPath, segment);
            cursor = segment.EndExclusive;
        }
        if (cursor != manifest.ExpectedSize)
        {
            throw new InvalidDataException("The segmented download resume map does not cover the artifact.");
        }
    }

    private static HttpRequestMessage CreateRequest(Uri source)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd("AI_HUB/0.0.86");
        return request;
    }

    private static string GetManifestPath(string partialPath) => partialPath + ".segments.json";

    private static string ResolveSegmentPath(string partialPath, SegmentedDownloadPart segment)
    {
        var directory = Path.GetDirectoryName(partialPath)
            ?? throw new InvalidDataException("The partial model path has no directory.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, segment.FileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), segment.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A segmented download path escapes the managed directory.");
        }
        return path;
    }

    private static SegmentedDownloadManifest? TryLoadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SegmentedDownloadManifest>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveManifest(string path, SegmentedDownloadManifest manifest)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions));
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

    private static long GetFileLength(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private sealed class AggregateProgressReporter
    {
        private readonly ManagedModelArtifactCard _card;
        private readonly ManagedModelArtifactFile _file;
        private readonly long _completedCardBytes;
        private readonly IProgress<ManagedModelDownloadProgress>? _progress;
        private readonly object _sync = new();
        private long _downloaded;
        private long _lastBytes;
        private long _lastTimestamp = Stopwatch.GetTimestamp();
        private double _smoothedSpeed;

        public AggregateProgressReporter(
            ManagedModelArtifactCard card,
            ManagedModelArtifactFile file,
            long completedCardBytes,
            long initialDownloaded,
            IProgress<ManagedModelDownloadProgress>? progress)
        {
            _card = card;
            _file = file;
            _completedCardBytes = completedCardBytes;
            _downloaded = initialDownloaded;
            _lastBytes = initialDownloaded;
            _progress = progress;
        }

        public void Add(int bytes)
        {
            Interlocked.Add(ref _downloaded, bytes);
            Report("downloading", _file.RelativePath);
        }

        public void Report(string stage, string fileName)
        {
            if (_progress is null)
            {
                return;
            }
            lock (_sync)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
                if (stage == "downloading" && elapsed < TimeSpan.FromMilliseconds(500))
                {
                    return;
                }
                var downloaded = Interlocked.Read(ref _downloaded);
                var currentSpeed = stage == "downloading"
                    ? (downloaded - _lastBytes) / Math.Max(0.001, elapsed.TotalSeconds)
                    : 0;
                _smoothedSpeed = currentSpeed <= 0
                    ? _smoothedSpeed
                    : _smoothedSpeed <= 0 ? currentSpeed : (_smoothedSpeed * 0.7) + (currentSpeed * 0.3);
                _lastBytes = downloaded;
                _lastTimestamp = now;
                _progress.Report(new ManagedModelDownloadProgress(
                    _card.ModelArtifactId,
                    fileName,
                    _completedCardBytes + downloaded,
                    _card.TotalBytes,
                    stage == "downloading" ? _smoothedSpeed : 0,
                    stage));
            }
        }
    }
}

internal sealed class SegmentedDownloadManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string SourceUrl { get; set; } = string.Empty;

    public long ExpectedSize { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public long InitialPrefixBytes { get; set; }

    public long PrefixBytes { get; set; }

    public List<SegmentedDownloadPart> Segments { get; set; } = [];
}

internal sealed class SegmentedDownloadPart
{
    public int Index { get; set; }

    public long Start { get; set; }

    public long EndExclusive { get; set; }

    public string FileName { get; set; } = string.Empty;

    public long Length => EndExclusive - Start;
}
