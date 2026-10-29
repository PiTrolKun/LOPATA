using System.IO;
using System.Windows;
using AIHub.Services;
using System.Security.Cryptography;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private CancellationTokenSource? _copyCancel;
    private void RefreshResultActions()
    {
        var result = SelectedVideo is { } path && File.Exists(path);
        _exportVideo.IsEnabled = result; _sendEditor.IsEnabled = result && SendEditorRequested is not null;
    }
    private async Task ExportVideoAsync()
    {
        if (!TryCommit() || SelectedVideo is not { } source) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "MP4|*.mp4", DefaultExt = ".mp4", AddExtension = true, FileName = S.ExportName + ".mp4" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        var target = Path.GetFullPath(picker.FileName);
        if (!Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) { Notify(T("H3InvalidResult")); return; }
        if (target.Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (_project.Attachments.Any(a => target.Equals(Path.GetFullPath(a.Path), StringComparison.OrdinalIgnoreCase))) throw new IOException("VideoPrep.ProtectedFile");
            Lopata.Updates.SafeUpdatePath.RejectLinks(target); SetBusy(true);
            _copyCancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var selected = (ResultItem)_timeline.SelectedItem;
                var file = _state.Runs.SelectMany(r => r.Files).Single(f => f.Role == selected.Result.VideoRole);
                if (input.Length != file.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, _copyCancel.Token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("VideoPrep.H3InvalidResult");
                input.Position = 0; await input.CopyToAsync(output, _copyCancel.Token); await output.FlushAsync(_copyCancel.Token);
            }
            _copyCancel.Token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true); Notify(T("H3VideoExported"));
        }
        catch (OperationCanceledException) { Notify(T("H3Cancelled")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ComponentAuditFailures.Report(error); Notify(T("H3Failed")); }
        finally { _copyCancel?.Dispose(); _copyCancel = null; if (File.Exists(temporary)) File.Delete(temporary); SetBusy(false); }
    }
}
