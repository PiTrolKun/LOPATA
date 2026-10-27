using System.IO;
using System.Windows.Controls;
using ProgressBar = System.Windows.Controls.ProgressBar;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool _finishingBusy;
    private string _finishOperationKey = "ExportWorking";
    private CancellationTokenSource? _finishingCancel;
    private void BuildBoardAudio(StackPanel page)
    {
        var p = Session.Project; var b = VideoStoryboard.Ensure(p);
        var row = new WrapPanel(); row.Children.Add(Action("BoardAudioImport", () => _ = ImportBoardAudioAsync()));
        if (b.Audio is not null)
        {
            row.Children.Add(Action("BoardAudioRemove", () => { VideoStoryboard.Capture(p); b.Audio = null; Changed(); Checkpoint(); Render(); }));
            row.Children.Add(Action("BoardFitAudio", () =>
            {
                var total = VideoStoryboard.Duration(p); if (total <= 0 || b.Frames.Count == 0) { _statusKey = "BoardAudioTooShort"; RefreshStatus(); return; }
                VideoStoryboard.Capture(p); var scale = b.Audio.Seconds / total;
                if (b.Frames.Any(f => !double.IsFinite(f.Seconds * scale) || f.Seconds * scale is <= 0 or > 86400)) { _statusKey = "BoardAudioTooShort"; RefreshStatus(); return; }
                foreach (var f in b.Frames) { f.Seconds *= scale; f.Estimated = false; } Changed(); Checkpoint(); Render();
            }));
        }
        page.Children.Add(row);
        if (b.Audio is { } audio)
        {
            var caption = Text("BoardAudio"); caption.Text = audio.Name + " · " + audio.Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s"; page.Children.Add(caption);
            if (audio.MetadataNote != "") page.Children.Add(Text(audio.MetadataNote));
            if (audio.TimedLyrics is { } lyrics)
            { var content = Text("BoardTimedLyrics"); content.Text = lyrics.SourceLyrics;
                var expander = new Expander { Header = T("BoardTimedLyrics"), Content = content };
                expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); page.Children.Add(expander); }
        }
        if (_finishingBusy) { page.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5 }); page.Children.Add(Action("FinishCancel", () => _finishingCancel?.Cancel())); }
    }
    private async Task ImportBoardAudioAsync()
    {
        if (ConceptBusy || PendingConcept) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = T("BoardAudioImport"), Filter = T("BoardAudioFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;
        var p = Session.Project; _finishingBusy = true; _finishOperationKey = "BoardAudioPreparing";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken); _finishingCancel = cancel; Render();
        try
        {
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            var imported = await VideoBoardAudioImport.ImportAsync(dialog.FileName, Path.Combine(Session.RecoveryRoot, "Media", p.Id, "audio"), cancel.Token);
            if (!ReferenceEquals(p, Session.Project)) throw new InvalidDataException("VideoPrep.AiWrongProject");
            VideoStoryboard.Capture(p); p.Attachments.Add(new(imported.Path, imported.Audio.Role)); VideoStoryboard.Ensure(p).Audio = imported.Audio;
            Session.Changed(); Checkpoint();
        }
        catch (OperationCanceledException) { _statusKey = "FinishCanceled"; }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ShowError(e); }
        finally { _finishingBusy = false; _finishingCancel = null; Render(); }
    }
}
