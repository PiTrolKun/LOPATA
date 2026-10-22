using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace VoiceInputStand;

public sealed class StandLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private static readonly JsonSerializerOptions JsonOptions=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public string DirectoryPath { get; }
    public event Action<string>? Line;
    public StandLog(string data)
    {
        DirectoryPath = Path.Combine(data, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(DirectoryPath);
        _writer = new(Path.Combine(DirectoryPath, "events.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
        Write("stand_start", new { pid=Environment.ProcessId, cpu=Environment.ProcessorCount,
            os=Environment.OSVersion.ToString(), runtime=Environment.Version.ToString(), provider="CPU", full_transcript_logging=true });
    }
    public void Write(string name, object? data = null)
    {
        var line = JsonSerializer.Serialize(new { utc=DateTimeOffset.UtcNow, ticks=Stopwatch.GetTimestamp(), @event=name, data },JsonOptions);
        lock (_gate) _writer.WriteLine(line);
        Line?.Invoke(line);
    }
    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
