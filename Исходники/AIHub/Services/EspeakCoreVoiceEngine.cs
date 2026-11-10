using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed class EspeakCoreVoiceEngine : ICoreVoiceEngine
{
    private const int AudioOutputSynchronous = 2;
    private const int EspeakCharsUtf8 = 1;
    private const int EspeakEndPause = 0x1000;
    private const int EventListTerminated = 0;
    private const int EventWord = 1;
    private const int ParameterRate = 1;
    private const int ParameterVolume = 2;
    private const int ParameterPitch = 3;
    private const int ParameterRange = 4;

    private readonly CoreVoiceRuntimeLocator _runtimeLocator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _nativeSync = new();
    private CoreVoiceRuntime? _runtime;
    private IntPtr _library;
    private bool _initialized;
    private bool _disposed;
    private int _sampleRate;
    private NativeApi? _api;
    private NativeApi.SynthCallback? _callback;
    private SynthesisSession? _activeSynthesis;
    private NAudio.Wave.WaveOutEvent? _activePlayer;

    public EspeakCoreVoiceEngine(CoreVoiceRuntimeLocator? runtimeLocator = null)
    {
        _runtimeLocator = runtimeLocator ?? new CoreVoiceRuntimeLocator();
        _runtime = _runtimeLocator.Find();
    }

    public bool IsAvailable => !_disposed && (_runtime ??= _runtimeLocator.Find()) is not null;

    public async Task<CoreSpeechPresentationResult> SpeakAsync(
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var composition = CoreSpeechTextService.Compose(request.Segments);
        if (string.IsNullOrWhiteSpace(composition.Text))
        {
            progress.Report(CreateProgress(composition, composition.Text.Length, true, false));
            return new CoreSpeechPresentationResult(true, false, false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureInitialized();
            cancellationToken.ThrowIfCancellationRequested();

            var synthesis = await Task.Run(
                () => Synthesize(composition, request.LanguageCode, request.Settings, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            await PlayAndRevealAsync(synthesis, request.Settings.Rate, progress, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(CreateProgress(composition, composition.Text.Length, true, synthesis.WordEvents.Count > 0));
            return new CoreSpeechPresentationResult(true, false, synthesis.WordEvents.Count > 0);
        }
        catch (OperationCanceledException)
        {
            return new CoreSpeechPresentationResult(false, true, false, "cancelled");
        }
        finally
        {
            lock (_nativeSync)
            {
                _activeSynthesis = null;
                _activePlayer = null;
            }

            _gate.Release();
        }
    }

    public void Cancel()
    {
        lock (_nativeSync)
        {
            _activeSynthesis?.Cancel();
            _activePlayer?.Stop();
            if (_initialized && !_disposed)
            {
                _api?.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_nativeSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeSynthesis?.Cancel();
            _activePlayer?.Stop();
            if (_initialized)
            {
                _api?.Cancel();
                _api?.Terminate();
                _initialized = false;
            }

            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }

        _gate.Dispose();
    }

    private SynthesisSession Synthesize(
        CoreSpeechComposition composition,
        string languageCode,
        CoreVoiceSettings settings,
        CancellationToken cancellationToken)
    {
        var voice = SelectVoice(languageCode, settings);
        SetVoice(voice, languageCode);
        SetParameter(ParameterRate, Math.Clamp(settings.Rate, 80, 450));
        SetParameter(ParameterVolume, Math.Clamp(settings.Volume, 0, 200));
        SetParameter(ParameterPitch, languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? 38 : 55);
        SetParameter(ParameterRange, languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? 30 : 45);

        var session = new SynthesisSession(composition, cancellationToken);
        lock (_nativeSync)
        {
            _activeSynthesis = session;
        }

        var sessionHandle = GCHandle.Alloc(session);
        var textBytes = Encoding.UTF8.GetBytes(composition.Text + '\0');
        var textPointer = Marshal.AllocHGlobal(textBytes.Length);
        try
        {
            Marshal.Copy(textBytes, 0, textPointer, textBytes.Length);
            uint uniqueIdentifier = 0;
            var result = _api!.Synth(
                textPointer,
                (nuint)textBytes.Length,
                0,
                1,
                0,
                EspeakCharsUtf8 | EspeakEndPause,
                ref uniqueIdentifier,
                GCHandle.ToIntPtr(sessionHandle));
            if (result != 0 && !cancellationToken.IsCancellationRequested)
            {
                throw new CoreVoiceException("synth_failed", $"eSpeak NG returned error {result}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(textPointer);
            sessionHandle.Free();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (session.Samples.Count == 0)
        {
            throw new CoreVoiceException("empty_audio", "eSpeak NG returned no audio data.");
        }

        return session;
    }

    private async Task PlayAndRevealAsync(
        SynthesisSession synthesis,
        int rate,
        IProgress<CoreSpeechProgress> progress,
        CancellationToken cancellationToken)
    {
        await using var waveStream = CreateWaveStream(synthesis.Samples, _sampleRate);
        waveStream.Position = 0;
        using var reader = new NAudio.Wave.WaveFileReader(waveStream);
        using var player = new NAudio.Wave.WaveOutEvent { DesiredLatency = 100 };
        player.Init(reader);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) => { if (e.Exception is { } error) ended.TrySetException(error); else ended.TrySetResult(); };
        lock (_nativeSync)
        {
            _activePlayer = player;
        }

        using var registration = cancellationToken.Register(() => { player.Stop(); ended.TrySetCanceled(cancellationToken); });
        var durationMilliseconds = (int)Math.Ceiling(synthesis.Samples.Count * 1000d / _sampleRate);
        var cues = BuildRevealCues(synthesis, rate, durationMilliseconds);
        using var speech = VoiceNotifications.VoicePlaybackHub.RegisterSpeech(player.Pause, player.Play);
        if (!speech.IsSuspended) player.Play();

        foreach (var cue in cues)
        {
            while (!ended.Task.IsCompleted && cue.TimeMilliseconds > reader.CurrentTime.TotalMilliseconds)
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);

            progress.Report(CreateProgress(
                synthesis.Composition,
                cue.VisibleCharacters,
                false,
                synthesis.WordEvents.Count > 0));
        }

        await ended.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<SpeechRevealCue> BuildRevealCues(
        SynthesisSession synthesis,
        int rate,
        int durationMilliseconds)
    {
        if (synthesis.WordEvents.Count == 0)
        {
            var estimated = SpeechTimelineBuilder.BuildEstimated(synthesis.Composition.Text, rate);
            var lastTime = Math.Max(1, estimated[^1].TimeMilliseconds);
            var scale = durationMilliseconds / (double)lastTime;
            return estimated
                .Select(cue => new SpeechRevealCue(
                    Math.Min(durationMilliseconds, (int)Math.Round(cue.TimeMilliseconds * scale)),
                    cue.VisibleCharacters))
                .ToArray();
        }

        var events = synthesis.WordEvents;
        var cues = new List<SpeechRevealCue>(events.Count + 1);
        if (events.Count <= 2)
        {
            foreach (var word in events)
            {
                var end = CoreSpeechTextService.NativeCharacterPositionToUtf16Index(
                    synthesis.Composition.Text,
                    word.TextPosition + Math.Max(0, word.Length));
                cues.Add(new SpeechRevealCue(Math.Max(0, word.AudioPosition), end));
            }
        }
        else
        {
            for (var index = 1; index < events.Count; index++)
            {
                var word = events[index];
                var start = CoreSpeechTextService.NativeCharacterPositionToUtf16Index(
                    synthesis.Composition.Text,
                    word.TextPosition);
                cues.Add(new SpeechRevealCue(Math.Max(0, word.AudioPosition), start));
            }
        }

        cues.Add(new SpeechRevealCue(durationMilliseconds, synthesis.Composition.Text.Length));
        return cues;
    }

    private static MemoryStream CreateWaveStream(IReadOnlyList<short> samples, int sampleRate)
    {
        var stream = new MemoryStream(44 + samples.Count * sizeof(short));
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            const short channelCount = 1;
            const short bitsPerSample = 16;
            var byteRate = sampleRate * channelCount * bitsPerSample / 8;
            var dataLength = samples.Count * sizeof(short);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channelCount);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)(channelCount * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (var sample in samples)
            {
                writer.Write(sample);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private void EnsureInitialized()
    {
        lock (_nativeSync)
        {
            if (_initialized)
            {
                return;
            }

            _runtime ??= _runtimeLocator.Find();
            if (_runtime is null)
            {
                throw new CoreVoiceException("runtime_missing", "The bundled eSpeak NG runtime was not found.");
            }

            try
            {
                _library = NativeLibrary.Load(_runtime.LibraryPath);
                _api = NativeApi.Load(_library);
                _callback = HandleSynthCallback;
                _sampleRate = _api.Initialize(AudioOutputSynchronous, 60, _runtime.DirectoryPath, 0);
                if (_sampleRate <= 0)
                {
                    throw new CoreVoiceException("runtime_init_failed", "eSpeak NG could not initialize synthesis.");
                }

                _api.SetSynthCallback(_callback);
                _initialized = true;
            }
            catch (CoreVoiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CoreVoiceException("runtime_init_failed", "eSpeak NG initialization failed.", ex);
            }
        }
    }

    private int HandleSynthCallback(IntPtr wav, int sampleCount, IntPtr eventsPointer)
    {
        SynthesisSession? session = null;
        if (eventsPointer != IntPtr.Zero)
        {
            var firstEvent = Marshal.PtrToStructure<EspeakEvent>(eventsPointer);
            if (firstEvent.UserData != IntPtr.Zero)
            {
                try
                {
                    session = GCHandle.FromIntPtr(firstEvent.UserData).Target as SynthesisSession;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        session ??= _activeSynthesis;
        if (session is null)
        {
            return 0;
        }

        if (wav != IntPtr.Zero && sampleCount > 0)
        {
            var buffer = new short[sampleCount];
            Marshal.Copy(wav, buffer, 0, sampleCount);
            session.Samples.AddRange(buffer);
        }

        if (eventsPointer != IntPtr.Zero)
        {
            var eventSize = Marshal.SizeOf<EspeakEvent>();
            for (var index = 0; ; index++)
            {
                var item = Marshal.PtrToStructure<EspeakEvent>(IntPtr.Add(eventsPointer, index * eventSize));
                if (item.Type == EventListTerminated)
                {
                    break;
                }

                if (item.Type == EventWord)
                {
                    session.WordEvents.Add(new NativeWordEvent(item.TextPosition, item.Length, item.AudioPosition));
                }
            }
        }

        return session.IsCancelled ? 1 : 0;
    }

    private void SetVoice(string preferredVoice, string languageCode)
    {
        var result = _api!.SetVoiceByName(preferredVoice);
        if (result == 0)
        {
            return;
        }

        var fallback = languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        result = _api.SetVoiceByName(fallback);
        if (result != 0)
        {
            throw new CoreVoiceException("voice_missing", $"eSpeak NG voice '{preferredVoice}' is unavailable.");
        }
    }

    private void SetParameter(int parameter, int value)
    {
        var result = _api!.SetParameter(parameter, value, 0);
        if (result != 0)
        {
            throw new CoreVoiceException("parameter_failed", $"eSpeak NG rejected parameter {parameter}.");
        }
    }

    private static string SelectVoice(string languageCode, CoreVoiceSettings settings) =>
        languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? settings.EnglishVoice
            : settings.RussianVoice;

    private static CoreSpeechProgress CreateProgress(
        CoreSpeechComposition composition,
        int visibleCharacters,
        bool complete,
        bool usesNativeWordEvents) =>
        new(
            CoreSpeechTextService.MapVisibleCharacters(composition, visibleCharacters),
            complete,
            usesNativeWordEvents);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [StructLayout(LayoutKind.Sequential)]
    private struct EspeakEvent
    {
        public int Type;
        public uint UniqueIdentifier;
        public int TextPosition;
        public int Length;
        public int AudioPosition;
        public int Sample;
        public IntPtr UserData;
        public long Id;
    }

    private sealed class SynthesisSession(CoreSpeechComposition composition, CancellationToken cancellationToken)
    {
        private int _cancelled;

        public CoreSpeechComposition Composition { get; } = composition;

        public List<short> Samples { get; } = [];

        public List<NativeWordEvent> WordEvents { get; } = [];

        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0 || cancellationToken.IsCancellationRequested;

        public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    }

    private sealed record NativeWordEvent(int TextPosition, int Length, int AudioPosition);

    private sealed class NativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SynthCallback(IntPtr wav, int sampleCount, IntPtr eventsPointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int InitializeDelegate(int output, int bufferLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int options);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SetSynthCallbackDelegate(SynthCallback callback);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetVoiceByNameDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string voiceName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SetParameterDelegate(int parameter, int value, int relative);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SynthDelegate(
            IntPtr text,
            nuint size,
            uint position,
            int positionType,
            uint endPosition,
            uint flags,
            ref uint uniqueIdentifier,
            IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int SimpleErrorDelegate();

        private NativeApi(
            InitializeDelegate initialize,
            SetSynthCallbackDelegate setSynthCallback,
            SetVoiceByNameDelegate setVoiceByName,
            SetParameterDelegate setParameter,
            SynthDelegate synth,
            SimpleErrorDelegate cancel,
            SimpleErrorDelegate terminate)
        {
            Initialize = initialize;
            SetSynthCallback = setSynthCallback;
            SetVoiceByName = setVoiceByName;
            SetParameter = setParameter;
            Synth = synth;
            Cancel = cancel;
            Terminate = terminate;
        }

        public InitializeDelegate Initialize { get; }
        public SetSynthCallbackDelegate SetSynthCallback { get; }
        public SetVoiceByNameDelegate SetVoiceByName { get; }
        public SetParameterDelegate SetParameter { get; }
        public SynthDelegate Synth { get; }
        public SimpleErrorDelegate Cancel { get; }
        public SimpleErrorDelegate Terminate { get; }

        public static NativeApi Load(IntPtr library) => new(
            Load<InitializeDelegate>(library, "espeak_Initialize"),
            Load<SetSynthCallbackDelegate>(library, "espeak_SetSynthCallback"),
            Load<SetVoiceByNameDelegate>(library, "espeak_SetVoiceByName"),
            Load<SetParameterDelegate>(library, "espeak_SetParameter"),
            Load<SynthDelegate>(library, "espeak_Synth"),
            Load<SimpleErrorDelegate>(library, "espeak_Cancel"),
            Load<SimpleErrorDelegate>(library, "espeak_Terminate"));

        private static T Load<T>(IntPtr library, string exportName) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, exportName));
    }
}
