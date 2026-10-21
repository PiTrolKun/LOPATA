using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Controls;

public sealed partial class MusicAudioTaskWindow
{
    private void AddOriginalLyrics(StackPanel body, string text, Func<string, LyricEntry>? draft)
    {
        body.Children.Add(Label("OriginalLyrics"));
        body.Children.Add(MusicWishUi.Text(T("SourceLyricsHint")));
        _originalLyrics.Text = text; Id(_originalLyrics, "SourceLyrics"); body.Children.Add(_originalLyrics);
        var actions = new WrapPanel();
        actions.Children.Add(MusicWishUi.Button(T("PasteSourceLyrics"), "Music.Transform.PasteSourceLyrics", PasteSourceWords));
        actions.Children.Add(MusicWishUi.Button(T("OpenSourceText"), "Music.Transform.OpenSourceText", OpenSourceWords));
        actions.Children.Add(MusicWishUi.Button(T("UseSourceLyrics"), "Music.Transform.UseSourceLyrics", CopySourceWords));
        body.Children.Add(actions);
        var offer = new LyricLibraryOffer { Draft = () => draft?.Invoke(_originalLyrics.Text) ?? new LyricEntry {
            Title = _l("Music.Library.Untitled"), Text = _originalLyrics.Text, Origin = "source lyrics reviewed by user; model history unknown" } };
        offer.Localize(_l); _originalLyrics.TextChanged += (_, _) => offer.SetText(_originalLyrics.Text);
        offer.SetText(_originalLyrics.Text); body.Children.Add(offer);
    }
    private void ReplaceSourceWords(string text)
    {
        MusicSourceText.Validate(text);
        _originalLyrics.BeginChange();
        try { _originalLyrics.SelectAll(); _originalLyrics.SelectedText = text; _originalLyrics.Select(0, 0); }
        finally { _originalLyrics.EndChange(); }
    }
    private bool ReplaceSourceAllowed() => string.IsNullOrWhiteSpace(_originalLyrics.Text)
        || System.Windows.MessageBox.Show(this, T("ReplaceSourceLyrics"), T("Title"), MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    private void PasteSourceWords()
    {
        if (_readingLyrics) return;
        try {
            if (!System.Windows.Clipboard.ContainsText()) { _status.Text = T("NoClipboardText"); return; }
            var text = System.Windows.Clipboard.GetText(); MusicSourceText.Validate(text);
            if (ReplaceSourceAllowed()) { ReplaceSourceWords(text); _status.Text = T("ReviewSourceLyrics"); }
        } catch (Exception error) { _status.Text = error.Message.StartsWith("Music.") ? _l(error.Message) : T("SourceTextFailed"); }
    }
    private void OpenSourceWords()
    {
        if (_readingLyrics) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Text (*.txt;*.md)|*.txt;*.md" };
        if (dialog.ShowDialog(this) != true) return;
        try { var text = MusicSourceText.Read(dialog.FileName); if (ReplaceSourceAllowed()) { ReplaceSourceWords(text); _status.Text = T("ReviewSourceLyrics"); } }
        catch (Exception error) { _status.Text = error.Message.StartsWith("Music.") ? _l(error.Message) : T("SourceTextFailed"); }
    }
    private void CopySourceWords()
    {
        if (string.IsNullOrWhiteSpace(_originalLyrics.Text)) return;
        if (!string.IsNullOrWhiteSpace(_lyrics.Text) && System.Windows.MessageBox.Show(this, T("ReplaceLyrics"), T("Title"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _lyrics.BeginChange();
        try { _lyrics.SelectAll(); _lyrics.SelectedText = _originalLyrics.Text; _lyrics.Select(0, 0); }
        finally { _lyrics.EndChange(); }
    }
    private void RefreshInputUsage()
    {
        _inputUsage.Text = T(_enabled.IsChecked != true ? "UsageOff" : _tabs.SelectedIndex == 1 ? "UsageReferences" : "UsageSource");
        var mode = SelectedMode();
        _sourceRole.Text = T(mode == MusicAudioTaskMode.NewSong ? "SourceUnusedRole"
            : mode == MusicAudioTaskMode.Restore ? "SourceProcessingRole"
            : _variation == MusicStudioRuntime.Variation ? "StudioSourceRole" : "AceSourceRole");
        _referenceRole.Text = _l(MusicAudioInputPolicy.ReferenceHintKey(_variation, mode));
        _referenceControls.IsEnabled = MusicAudioInputPolicy.AllowsReferences(_variation, mode);
    }
    private MusicAudioTaskMode SelectedMode() => _tabs.SelectedIndex == 1 ? MusicAudioTaskMode.NewSong
        : (MusicAudioTaskMode)((ComboBoxItem)(_tabs.SelectedIndex == 2 ? _editMode : _coverMode).SelectedItem).Tag;
    private async Task LoadLyricsAsync(bool recognize)
    {
        if (_readingLyrics || !File.Exists(_source.Text)) return;
        _readingLyrics = true; _status.Text = T("ReadingLyrics");
        try {
            if (recognize && !await MusicAudioTaskPreparationWindow.EnsureRecognizerAsync(this, _l, _lifetime.Token)) return;
            if (recognize) await ApplicationBackgroundOperations.RetireModelsAsync();
            var source = _source.Text; var input = new MusicAudioInput(source) { Start = Read(_start), End = ReadOptional(_end) }; input.Validate();
            var hash = await MusicAudioTaskFiles.HashAsync(source, _lifetime.Token);
            var text = recognize ? await MusicSourceLyrics.RecognizeAsync(source, _lifetime.Token, input.Start, input.End) : await MusicSourceLyrics.ReadEmbeddedAsync(source, _lifetime.Token);
            if (_source.Text != source || hash != await MusicAudioTaskFiles.HashAsync(source, _lifetime.Token)) throw new InvalidDataException("Music.Transform.InputChanged");
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = T("NoLyrics"); return; }
            if (!string.IsNullOrWhiteSpace(_originalLyrics.Text) && System.Windows.MessageBox.Show(this, T("ReplaceSourceLyrics"), T("Title"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            ReplaceSourceWords(text); _status.Text = T("ReviewSourceLyrics");
        } catch (OperationCanceledException) { _status.Text = _l("Music.Lora.Cancelled"); }
        catch (Exception e) { _status.Text = e.Message.StartsWith("Music.") ? _l(e.Message) : e.Message; }
        finally { _readingLyrics = false; if (_closing) Close(); }
    }
}
