using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private readonly TextBlock _voiceSummary = new() {
        Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 280, MaxHeight = 44, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static string VoiceCaption(string provider, VideoVoiceOptions options)
    {
        var model = VideoNarratorCatalog.Models.FirstOrDefault(m => m.Id == provider);
        var name = model?.Name ?? provider;
        var speaker = string.IsNullOrEmpty(options.Speaker) ? model?.DefaultSpeaker : options.Speaker;
        return provider.StartsWith("kokoro-", StringComparison.Ordinal) || string.IsNullOrEmpty(speaker) ? name : name + " · " + speaker;
    }

    private void UpdateVoiceSummary()
    {
        var ids = SelectedFragments().ToHashSet();
        var selected = Project.Narration.Where(n => ids.Contains(n.Id)).ToArray();
        var common = VoiceCaption(Project.DefaultVoice, Project.DefaultVoiceOptions);
        var state = VideoVoiceSummary.Resolve(Project, ids, _playing, _position);
        var captions = state.Profiles.Select(p => p is null ? T("Voices.UnknownRecorded") : VoiceCaption(p.Provider, p.Options)).Distinct().ToArray();
        var caption = captions.Length switch { 0 => T("Voices.Silent"), 1 => captions[0], _ => T("Voices.Mixed") };
        _voiceSummary.Text = state.Playback ? string.Format(T("Voices.Playing"), caption)
            : state.Recorded ? string.Format(T("Voices.Recorded"), caption) : string.Format(T("Voices.CommonProfile"), common);
        var selectedDetails = selected.Select(n => VoiceCaption(n.Provider, n.VoiceOptions) + " · " + n.Language).Distinct();
        _voiceSummary.ToolTip = (selected.Length == 0 ? "" : string.Format(T("Voices.SelectedProfile"), string.Join("\n", selectedDetails)) + "\n\n")
            + string.Format(T("Voices.CommonProfile"), common + " · " + Project.DefaultLanguage)
            + (state.Recorded ? "\n\n" + string.Format(T("Voices.Recorded"), caption) : "");
        AutomationProperties.SetName(_voiceSummary, _voiceSummary.Text);
    }
}
