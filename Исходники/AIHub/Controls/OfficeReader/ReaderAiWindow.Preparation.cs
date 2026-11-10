using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.OfficeReader;

internal sealed partial class ReaderAiWindow
{
    private Task<bool> PrepareAsync(ReaderAiModel model, bool speech, CancellationToken token, System.Windows.Window? owner = null)
    {
        var root = string.IsNullOrWhiteSpace(model.Card.ModelsRoot) ? Root : model.Card.ModelsRoot;
        if (!Path.IsPathFullyQualified(root)) throw new InvalidOperationException("VideoEditor.StorageRequired");
        using var prepare = new VideoModelPreparation(_library);
        using var text = new ReaderTextPreparation(_library);
        var connections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
        prepare.ParallelConnections = connections; text.ParallelConnections = connections;
        var need = new VideoModelNeed(speech ? "speech" : "reader-text", speech ? [model.Id] : []);
        async Task<bool> Run(bool download, IProgress<AIHub.Models.ManagedModelDownloadProgress> progress, CancellationToken ct)
        {
            try { return speech ? await prepare.PrepareAsync(root, need, download, progress, ct)
                : await text.PrepareAsync(model.Card, download, progress, ct); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Reader.Ai.", StringComparison.Ordinal))
            { throw new InvalidOperationException(_l(error.Message), error); }
        }
        var window = new VideoPreparationWindow(owner ?? this, _l, model.Name,
            Run, token);
        return Task.FromResult(window.ShowDialog() == true);
    }
    private void ConfigureVoice(string id)
    {
        try
        {
            var selected = _settings.Speech.Selected; _settings.Speech.Selected = id;
            var initial = _settings.Speech.Choice(); _settings.Speech.Selected = selected;
            var profiles = _settings.Speech.Profiles.ToDictionary(p => p.Key,
                p => new VideoNarratorChoice(p.Key, p.Value.Language, p.Value.Rate, p.Value.Options));
            profiles[id] = initial;
            VideoVoiceWindow? window = null;
            async Task<VideoPreparedModels?> Prepare(VideoModelNeed need, CancellationToken ct)
            {
                var model = ReaderAiModelCatalog.SpeechModels(Root, _library.LoadAll()).Single(m => m.Id == need.Voices[0]);
                return await PrepareAsync(model, true, ct, window) ? new(string.IsNullOrEmpty(model.Card.ModelsRoot) ? Root : model.Card.ModelsRoot) : null;
            }
            var audioRoot = Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "Speech");
            var extension = new VideoVoiceWindowExtension(T("VoiceSettings"), "", language => _l(language == "ru" ? "Reader.Ai.TestRu" : "Reader.Ai.TestEn"),
                async (choice, text, ct) => {
                    if (ApplicationBackgroundOperations.Current?.HasPending == true) throw new InvalidOperationException("ChatSpeech.Busy");
                    await ApplicationBackgroundOperations.RetireModelsAsync();
                    if (await Prepare(new("speech", [choice.Id]), ct) is null) throw new OperationCanceledException(ct);
                    var message = new ChatSpeechMessage(Guid.NewGuid().ToString("N"), ChatSpeechWork.Hash(text), text);
                    var result = await new ChatSpeechWork(_library, audioRoot).GenerateAsync(new(Guid.NewGuid().ToString("N"), message, choice, T("VoiceSettings")), ct);
                    return result.Files[0];
                }, () => { Directory.CreateDirectory(audioRoot); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { audioRoot }, UseShellExecute = true }); },
                () => ApplicationBackgroundOperations.RetireModelsAsync(), profiles, ShowToggle: false, DefaultModelId: ReaderAiSettings.DefaultSpeechModelId,
                ModelSize: modelId => ReaderAiModelCatalog.Size((_library.Load(VideoNarratorCatalog.ArtifactId(modelId)) ?? VideoNarratorCatalog.Card(Root, modelId)).TotalBytes),
                FilesPresent: modelId => ReaderAiModelCatalog.Downloaded(_library.Load(VideoNarratorCatalog.ArtifactId(modelId)) ?? VideoNarratorCatalog.Card(Root, modelId)));
            window = new VideoVoiceWindow(this, _l, initial, false, Prepare, _library, extension);
            if (window.ShowDialog() == true && window.Choice is { } voice)
            {
                foreach (var profile in window.Profiles.Values) _settings.Speech.Apply(profile);
                _settings.Speech.Apply(voice);
            }
            Populate();
        }
        catch (Exception error) { _status.Text = Error(error); }
    }
    private void ApplySharedTheme(bool dark)
    {
        System.Windows.Media.SolidColorBrush Color(string value) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        Resources["WindowBackgroundBrush"] = Background = Color(dark ? "#0D1523" : "#EDF0F6");
        Resources["TextPrimaryBrush"] = Foreground = Color(dark ? "#F1F4FA" : "#202735");
        Resources["TextSecondaryBrush"] = Color(dark ? "#A9B8CD" : "#516076");
        Resources["PanelBrush"] = Resources["InputBrush"] = Color(dark ? "#172132" : "#FFFFFF");
        Resources["SecondaryButtonBackgroundBrush"] = Resources["PanelBrush"];
        Resources["LineBrush"] = Color(dark ? "#3A465C" : "#CCD3DF");
        Resources["AccentBrush"] = Color("#2563EB"); Resources["UiBodyFontSize"] = 14d;
        // The picker also opens from MainWindow, which has no Reader palette or styles.
        Resources["ReaderText"] = Resources["TextPrimaryBrush"];
        Resources["ReaderMuted"] = Resources["TextSecondaryBrush"];
        Resources["ReaderPanel"] = Resources["PanelBrush"];
        Resources["ReaderLine"] = Resources["LineBrush"];
        Resources["ReaderHover"] = Color(dark ? "#293954" : "#E7EFFF");
    }
}
