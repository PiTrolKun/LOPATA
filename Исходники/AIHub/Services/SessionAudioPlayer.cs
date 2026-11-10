using System.IO;
using System.Windows.Media;

namespace AIHub.Services;

public sealed class SessionAudioPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private OutputProvenance? _provenance;
    private bool _deleteOnClear = true;
    private VoiceNotifications.VoicePlaybackHub.SpeechLease? _notificationSpeech;
    public string AudioPath { get; private set; } = string.Empty;
    public bool IsPlaying { get; private set; }
    public bool HasAudio => File.Exists(AudioPath);
    public event Action? Changed;
    public event Action<string>? Failed;

    public SessionAudioPlayer()
    {
        _player.MediaEnded += (_, _) =>
        {
            // Seeking while still playing restarts the file. Stop first, then notify the UI.
            _player.Stop();
            IsPlaying = false;
            _notificationSpeech?.SetActive(false);
            Changed?.Invoke();
        };
        _player.MediaFailed += (_, e) => { IsPlaying = false; _notificationSpeech?.SetActive(false); Failed?.Invoke(e.ErrorException.Message); Changed?.Invoke(); };
    }

    public void Open(string path, bool deleteOnClear = true, OutputProvenance? provenance = null)
    {
        Clear();
        _provenance = provenance;
        _deleteOnClear = deleteOnClear;
        AudioPath = path;
        _player.Open(new Uri(path, UriKind.Absolute));
        _player.Volume = 1;
        _notificationSpeech = VoiceNotifications.VoicePlaybackHub.RegisterSpeech(_player.Pause, _player.Play);
        if (!_notificationSpeech.IsSuspended) _player.Play();
        IsPlaying = true;
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (!HasAudio) return;
        if (IsPlaying) _player.Pause();
        _notificationSpeech?.SetActive(!IsPlaying);
        if (!IsPlaying && _notificationSpeech?.IsSuspended != true) _player.Play();
        IsPlaying = !IsPlaying;
        Changed?.Invoke();
    }

    public void Pause()
    {
        _player.Pause();
        _notificationSpeech?.SetActive(false);
        IsPlaying = false;
        Changed?.Invoke();
    }

    public void Export(string destination) => File.Copy(AudioPath, destination, overwrite: true);

    public Task ExportDescriptionAsync(string destination) => OutputWaveMetadata.ExportAsync(AudioPath, destination,
        _provenance ?? new OutputProvenance { Scenario = "image-description/speech", ModelHistory = "unknown" });

    public void Clear()
    {
        _notificationSpeech?.Dispose(); _notificationSpeech = null;
        _player.Close();
        IsPlaying = false;
        if (_deleteOnClear && File.Exists(AudioPath)) File.Delete(AudioPath);
        AudioPath = string.Empty;
        Changed?.Invoke();
    }

    public void Dispose() => Clear();
}
