using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricVideoStand
{
    internal sealed record StandConfig(string Root, string Song, string Output)
    {
        public string AudioDirectory => Path.Combine(Root, "Runtime", "MusicAudio", "FFmpeg-8.1", "bundle");
        public string VideoDirectory => Path.Combine(Root, "Runtime", "Capture", "FFmpeg-8.1", "bundle");
        public string Version => File.ReadAllText(Path.Combine(Root, "VERSION")).Trim();
        public static StandConfig Parse(string[] args)
        {
            string Value(string key) => Array.IndexOf(args, key) is var i && i >= 0 && i + 1 < args.Length
                ? Path.GetFullPath(args[i + 1]) : throw new ArgumentException("Required argument: " + key);
            var root = Value("--root"); var song = Value("--song");
            if (!File.Exists(Path.Combine(root, "VERSION")) || !File.Exists(song)) throw new IOException("Root or song is missing.");
            return new(root, song, Path.Combine(root, "Тесты", "LyricVideoStand", "out"));
        }
    }

    internal sealed class StandLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        public event Action<string>? Message;
        public StandLog(string directory)
        {
            Directory.CreateDirectory(directory);
            _writer = new(Path.Combine(directory, "stand-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        public void Write(string message)
        {
            var line = DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message;
            lock (_gate) _writer.WriteLine(line);
            Message?.Invoke(line);
        }
        public void Dispose() { lock (_gate) _writer.Dispose(); }
    }

    internal static class StandProcess
    {
        public static ProcessStartInfo Info(string executable, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            return info;
        }
        public static async Task<string> RunAsync(string executable, IEnumerable<string> args, CancellationToken token)
        {
            using var process = new Process { StartInfo = Info(executable, args) };
            if (!process.Start()) throw new IOException("Process did not start.");
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, errors);
                token.ThrowIfCancellationRequested(); throw new TimeoutException("Local runtime timed out.");
            }
            var text = await output; var error = await errors;
            if (process.ExitCode != 0) throw new IOException("Runtime exit " + process.ExitCode + ": " + error[^Math.Min(2000, error.Length)..]);
            if (text.Length > 8 * 1024 * 1024) throw new IOException("Probe output exceeded limit.");
            return text;
        }
        public static async Task VerifyAsync(string directory, string manifestName, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, manifestName), token));
            foreach (var entry in doc.RootElement.GetProperty("Files").EnumerateArray())
            {
                var name = entry.GetProperty("Name").GetString() ?? throw new InvalidDataException();
                var path = Path.GetFullPath(Path.Combine(directory, name));
                if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe runtime entry.");
                using var stream = File.OpenRead(path); var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!hash.Equals(entry.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Runtime hash mismatch: " + name);
            }
        }
    }

    internal sealed record ModelUse(string Id, string Role, string Revision);
    internal sealed record StandProvenance(string ProgramVersion, string Scenario, IReadOnlyList<ModelUse> Models)
    {
        public string Program => "LOPATA";
        public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
        public string ModelHistory => "recorded-input-tags; other-creation-history-unknown";
        public string Json() => JsonSerializer.Serialize(this);
    }
}

namespace AIHub.Services
{
    // Developer-tool adapter for shared pure metadata readers. Uses existing, hash-verified
    // local executables only; no acquisition, license mutation, app-data access or model execution.
    internal sealed class MusicAudioRuntime(string directory)
    {
        public Task<string> RunAsync(bool probe, IEnumerable<string> args, CancellationToken token) =>
            LyricVideoStand.StandProcess.RunAsync(Path.Combine(directory, probe ? "ffprobe.exe" : "ffmpeg.exe"), args, token);
    }
}
