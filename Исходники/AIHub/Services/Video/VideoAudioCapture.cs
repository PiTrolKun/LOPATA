using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AIHub.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHub.Services;

public sealed record CaptureAudioDevice(string Id, string Name);

/// <summary>WASAPI packets keep their QPC timestamps on the common video clock.
/// Audio is written directly to disk with gaps represented by silence, not by shortening time.</summary>
public sealed class VideoAudioCapture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private readonly VideoFrameStore _store;
    private readonly long _epoch;
    private readonly object _errorLock = new();
    private string _error = "";
    public string Error { get { lock (_errorLock) return _error; } }
    public event Action<Exception>? Failed;
    public VideoAudioCapture(VideoFrameStore store, long epoch) { _store = store; _epoch = epoch; }
    public static IReadOnlyList<CaptureAudioDevice> Devices(bool microphone)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active);
        var result = new List<CaptureAudioDevice>();
        foreach (var device in devices) { using (device) result.Add(new(device.ID, device.FriendlyName)); }
        return result;
    }
    public async Task StartAsync(ScreenCaptureSettings settings)
    {
        if (settings.AudioMode is "pc" or "both") await StartTrackAsync(false, settings.PlaybackDevice);
        if (settings.AudioMode is "mic" or "both") await StartTrackAsync(true, settings.Microphone);
    }
    private async Task StartTrackAsync(bool microphone, string id)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.Add(Task.Run(() =>
        {
            try { RunTrack(microphone, id, ready); }
            catch (Exception error)
            {
                lock (_errorLock) _error += (microphone ? "Microphone: " : "Playback: ") + error.Message + "\n";
                ready.TrySetException(error); Failed?.Invoke(error);
            }
        }));
        await ready.Task;
    }
    private void RunTrack(bool microphone, string id, TaskCompletionSource ready)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = id == "default" ? enumerator.GetDefaultAudioEndpoint(microphone ? DataFlow.Capture : DataFlow.Render, Role.Console) : enumerator.GetDevice(id);
        if (device.State != DeviceState.Active) throw new IOException("The selected audio device is disconnected.");
        using var client = device.AudioClient;
        var format = client.MixFormat.AsStandardWaveFormat();
        var inputFormat = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => "f32le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => "s16le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => "s24le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => "s32le",
            _ => throw new IOException("Unsupported audio device sample format.")
        };
        client.Initialize(AudioClientShareMode.Shared, microphone ? 0 : AudioClientStreamFlags.Loopback, 1_000_000, 0, client.MixFormat, Guid.Empty);
        using var packets = client.AudioCaptureClient;
        var name = microphone ? "microphone.pcm" : "playback.pcm";
        using var file = new FileStream(Path.Combine(_store.DirectoryPath, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        lock (_store) { _store.Manifest.Audio.Add(new(name, inputFormat, format.SampleRate, format.Channels, format.BlockAlign)); _store.Save(); }
        var silence = new byte[65536]; var buffer = new byte[Math.Max(65536, client.BufferSize * format.BlockAlign)];
        double previousFlush = 0;
        client.Start(); ready.TrySetResult();
        var stopped = false;
        try
        {
            while (true)
            {
                if (_stop.IsCancellationRequested && !stopped) { client.Stop(); stopped = true; }
                if (device.State != DeviceState.Active) throw new IOException("The audio device was disconnected during recording.");
                var available = packets.GetNextPacketSize();
                if (available == 0) { if (stopped) break; _stop.Token.WaitHandle.WaitOne(10); continue; }
                while (available > 0)
                {
                    var ptr = packets.GetBuffer(out var frames, out var flags, out _, out var qpc);
                    var length = checked(frames * format.BlockAlign);
                    try
                    {
                        if (length > buffer.Length) throw new IOException("Audio packet exceeds the negotiated device buffer.");
                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, length);
                        else Marshal.Copy(ptr, buffer, 0, length);
                    }
                    finally { packets.ReleaseBuffer(frames); }
                    // Never hold the WASAPI device buffer while waiting on disk I/O.
                    var now = (Stopwatch.GetTimestamp() - _epoch) / (double)Stopwatch.Frequency;
                    var at = (flags & AudioClientBufferFlags.TimestampError) != 0 ? now - frames / (double)format.SampleRate
                        : qpc / 10_000_000d - _epoch / (double)Stopwatch.Frequency;
                    // Device timestamps are monotonic QPC positions in 100 ns units.
                    if (!double.IsFinite(at) || at > now + 0.5 || at < -0.5)
                    { at = Math.Max(0, now - frames / (double)format.SampleRate); lock (_errorLock) { if (!_error.Contains("timestamp")) _error += "Invalid audio device timestamp; arrival time was used.\n"; } }
                    var position = Math.Max(0, (long)Math.Round(at * format.SampleRate)) * format.BlockAlign;
                    if (position > file.Position + format.AverageBytesPerSecond * 0.01)
                    { while (file.Position < position) file.Write(silence, 0, (int)Math.Min(silence.Length, position - file.Position)); }
                    file.Write(buffer, 0, length);
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && now > 0.5)
                        lock (_errorLock) { if (!_error.Contains("discontinuity")) _error += "Audio discontinuity detected; gaps are preserved as silence.\n"; }
                    if (now - previousFlush >= 0.25) { file.Flush(true); previousFlush = now; }
                    if (_stop.IsCancellationRequested && !stopped) { client.Stop(); stopped = true; }
                    available = packets.GetNextPacketSize();
                }
            }
        }
        finally { if (!stopped) client.Stop(); file.Flush(true); }
    }
    public async Task StopAsync() { _stop.Cancel(); await Task.WhenAll(_tasks); }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
