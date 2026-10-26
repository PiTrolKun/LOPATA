using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private void LoadSelectedText()
    {
        if (_loadingText) return; var n = _segments.SelectedItems.Cast<NarrationSegment>().FirstOrDefault(n => n.Id == _selectedSegment) ?? _segments.SelectedItem as NarrationSegment; _selectedSegment = n?.Id;
        _loadingText = true; _script.Text = _segments.SelectedItems.Count <= 1 ? n?.Text ?? "" : ""; _script.IsEnabled = n is not null && _segments.SelectedItems.Count == 1; _loadingText = false;
        UpdateVoiceSummary();
        if (_segments.SelectedItems.Count > 1) { _status.Text = string.Format(T("SelectedElements"), _segments.SelectedItems.Count); return; }
        if (n is not null) { var cue = VideoSubtitles.Cues(Project).Single(c => c.Id == n.Id); _status.Text = $"{VideoExport.N(cue.Start)}–{VideoExport.N(cue.End)} s · " + n.Provider + (n.Stale ? " · " + T("SpeechStale") : ""); }
    }
    private void ScriptChanged()
    {
        if (_loadingText || _busy || _selectedSegment is null) return;
        var id = _selectedSegment; var text = _script.Text;
        _editing.Edit(p => { var n = p.Narration.Single(n => n.Id == id); n.Text = text; n.Stale = n.AudioAssetId is not null; });
        _dirty = true; _saveTimer.Stop(); _saveTimer.Start(); UpdateTitle();
        if (Project.Narration.First(n => n.Id == id).Stale) _status.Text = T("SpeechStale");
    }
    private void HighlightNarration()
    {
        if (!_playing || _follow.IsChecked != true || _script.IsKeyboardFocusWithin || _timeline.Selection.Roots.Count > 1) return;
        var cue = VideoSubtitles.Cues(Project).FirstOrDefault(n => _position >= n.Start && _position < n.End); if (cue is null) return;
        var n = Project.Narration.First(n => n.Id == cue.Id);
        if (_selectedSegment != n.Id) { _segments.SelectedItem = n; _segments.ScrollIntoView(n); }
        var words = System.Text.RegularExpressions.Regex.Matches(n.Text, @"\S+"); if (words.Count == 0) return;
        var index = Math.Clamp((int)((_position - cue.Start) / (cue.End - cue.Start) * words.Count), 0, words.Count - 1);
        _script.Select(words[index].Index, words[index].Length);
    }
    private void AddFragment() => Edit(p => p.Narration.Add(new() { Start = _position, End = Math.Min(p.Duration + 5, _position + 5),
        Provider = p.DefaultVoice, Language = p.DefaultLanguage, Rate = p.DefaultRate, VoiceOptions = p.DefaultVoiceOptions }));
    private void FragmentSettings()
    {
        var n = Project.Narration.FirstOrDefault(n => n.Id == _selectedSegment); if (n is null) return;
        var cue = VideoSubtitles.Cues(Project).Single(c => c.Id == n.Id);
        var values = Ask("FragmentSettings", [("Start", VideoExport.N(cue.Start)), ("To", VideoExport.N(cue.End)), ("SubtitleTitle", n.SubtitleTitle)]); if (values is null) return;
        Edit(p => { var item = p.Narration.Single(x => x.Id == n.Id); VideoSubtitles.SetRange(p, n.Id, Number(values["Start"]), Number(values["To"])); item.SubtitleTitle = values["SubtitleTitle"]; });
    }
    private void SplitFragment()
    {
        if (SelectedFragments().Length > 1) { SplitSelection(); return; }
        var n = Project.Narration.FirstOrDefault(n => n.Id == _selectedSegment); if (n is null) return;
        var cue = VideoSubtitles.Cues(Project).Single(c => c.Id == n.Id);
        var at = _position > cue.Start + .05 && _position < cue.End - .05 ? _position : (cue.Start + cue.End) / 2;
        var caret = _script.CaretIndex > 0 && _script.CaretIndex < n.Text.Length ? _script.CaretIndex : n.Text.Length / 2;
        Edit(p => VideoSelectionEditing.Split(p, [new(n.Id, true)], at, caret));
    }
    private void MergeFragment()
    {
        var selected = SelectedFragments().ToHashSet(); var rows = Project.Narration.Where(n => selected.Contains(n.Id)).OrderBy(n => n.Start).ToArray();
        if (rows.Length == 1) { var next = Project.Narration.Where(n => TrackKey(n) == TrackKey(rows[0]) && n.Start > rows[0].Start).OrderBy(n => n.Start).FirstOrDefault(); if (next is null) return; rows = [rows[0], next]; }
        if (rows.Length < 2) return; if (rows.Select(TrackKey).Distinct().Count() > 1) throw new InvalidOperationException("VideoEditor.SameSubtitleTrack");
        var ids = rows.Select(n => n.Id).ToHashSet(); var cues = VideoSubtitles.Cues(Project).Where(n => ids.Contains(n.Id)).ToArray();
        Edit(p => { var a = p.Narration.Single(n => n.Id == rows[0].Id); a.Text = string.Join(" ", rows.Select(n => n.Text)); a.Start = cues.Min(n => n.Start); a.End = cues.Max(n => n.End); a.SubtitleStart = a.Start; a.SubtitleEnd = a.End;
            a.SeriesNumbers = rows.SelectMany(n => n.SeriesNumbers).Distinct().Order().ToList(); a.Models = rows.SelectMany(n => n.Models).Distinct().ToArray(); a.Stale = rows.Any(n => n.AudioAssetId is not null); p.Narration.RemoveAll(n => n.Id != a.Id && ids.Contains(n.Id)); });
    }
    private void VoiceSettings()
    {
        if (_busy) return;
        _playing = false; Safe(() => _preview.PlayAsync(false));
        UpdateVoiceSummary();
        var selected = Project.Narration.FirstOrDefault(n => n.Id == _selectedSegment);
        var profile = selected is null ? new VideoSpeechProfile(Project.DefaultVoice, Project.DefaultLanguage, Project.DefaultRate, Project.DefaultVoiceOptions)
            : !selected.Stale && VideoVoiceSummary.Recorded(Project, selected) is {} recorded ? recorded : VideoVoiceSummary.Planned(selected);
        var initial = new VideoNarratorChoice(profile.Provider, profile.Language, profile.Rate, profile.Options);
        var ids = SelectedFragments().ToHashSet();
        var window = new VideoVoiceWindow(this, _l, initial, selected is null, PrepareModels);
        if (window.ShowDialog() != true || window.Choice is not {} choice) return;
        Edit(p => VideoVoiceProfiles.Apply(p, choice, window.Global, ids));
    }
}
