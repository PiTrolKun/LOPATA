using System.IO;
using System.Windows.Media;

namespace AIHub.Services.VoiceNotifications;

internal interface IVoiceNotificationAudio : IDisposable
{
    TimeSpan LeadIn => TimeSpan.Zero;
    Task PlayAsync(VoiceNotificationPackage package, VoiceAudioItem item, double volume, double speed, CancellationToken token);
    void Configure(double volume, double speed);
}

/// <summary>Files are decoded lazily on a worker; MediaPlayer and its events stay on the UI dispatcher.</summary>
internal sealed class VoiceNotificationAudio(string cacheRoot) : IVoiceNotificationAudio
{
    internal const int LeadInMilliseconds = 300;
    public TimeSpan LeadIn => TimeSpan.FromMilliseconds(LeadInMilliseconds);
    private MediaPlayer? _player;
    private double _volume = .75, _speed = 1;
    public void Configure(double volume, double speed)
    { _volume = volume; _speed = speed; if (_player is { } player) { player.Volume = volume; player.SpeedRatio = speed; } }
    public async Task PlayAsync(VoiceNotificationPackage package, VoiceAudioItem item, double volume, double speed, CancellationToken token)
    {
        Configure(volume, speed);
        var folder = Path.Combine(cacheRoot, "Playback", package.Language, package.Revision);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, item.ClipId + "-lead300.wav");
        if (!File.Exists(target))
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var decoded = temporary + ".wav";
            try
            {
                await Task.Run(async () => {
                    await MusicAudioRuntime.Default.DecodeAsync(package.Resolve(item), decoded, token);
                    AddLeadIn(decoded, temporary);
                    token.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
                }, token);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(decoded)) File.Delete(decoded); }
        }
        token.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new MediaPlayer { Volume = _volume, SpeedRatio = _speed }; _player = player;
        player.MediaOpened += (_, _) => { if (ReferenceEquals(_player, player) && !token.IsCancellationRequested) { player.Volume = _volume; player.SpeedRatio = _speed; player.Play(); } };
        player.MediaEnded += (_, _) => done.TrySetResult();
        player.MediaFailed += (_, e) => done.TrySetException(e.ErrorException);
        try
        {
            player.Open(new Uri(target, UriKind.Absolute));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, item.Seconds / .75 + 10)));
            await done.Task.WaitAsync(timeout.Token);
        }
        finally { player.Close(); if (ReferenceEquals(_player, player)) _player = null; }
    }
    public void Dispose() { _player?.Close(); _player = null; }
    internal static void AddLeadIn(string source, string destination)
    {
        using var input = new NAudio.Wave.WaveFileReader(source);
        using var output = new NAudio.Wave.WaveFileWriter(destination, input.WaveFormat);
        var length = (int)Math.Ceiling(input.WaveFormat.AverageBytesPerSecond * LeadInMilliseconds / 1000d);
        length = (length + input.WaveFormat.BlockAlign - 1) / input.WaveFormat.BlockAlign * input.WaveFormat.BlockAlign;
        output.Write(new byte[length], 0, length);
        input.CopyTo(output);
    }
}
