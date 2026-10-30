using System.IO;
using System.Security.Cryptography;
using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private CancellationTokenSource? _copyCancel;
    private async Task ChooseVideoExportAsync()
    {
        if (!TryCommit() || SelectedVideo is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "MP4|*.mp4", DefaultExt = ".mp4", AddExtension = true, FileName = S.ExportName + ".mp4" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await ExportVideoToAsync(picker.FileName);
    }
    // Exact copies preserve the runner's embedded provenance, quality and dimensions.
    // A later settings edit cannot silently change an already completed result.
    internal async Task<bool> ExportVideoToAsync(string fileName, CancellationToken token = default)
    {
        if (!TryCommit() || SelectedVideo is not { } source || SelectedResult is not { } selected) return false;
        string? temporary = null;
        try
        {
            var target = Path.GetFullPath(fileName); source = Path.GetFullPath(source);
            if (!Path.GetExtension(target).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new IOException("VideoPrep.VaceInvalidResult");
            Lopata.Updates.SafeUpdatePath.RejectLinks(source); Lopata.Updates.SafeUpdatePath.RejectLinks(target);
            var receipt = selected.Run.Files.SingleOrDefault(file => file.Role == selected.Result.VideoRole)
                ?? throw new IOException("VideoPrep.VaceInvalidResult");
            if (_project.Attachments.Any(a => target.Equals(Path.GetFullPath(a.Path), StringComparison.OrdinalIgnoreCase)) && !target.Equals(source, StringComparison.OrdinalIgnoreCase))
                throw new IOException("VideoPrep.ProtectedFile");
            _copyCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
            SetBusy(true);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
            {
                if (input.Length != receipt.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, _copyCancel.Token)).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("VideoPrep.VaceInvalidResult");
                _copyCancel.Token.ThrowIfCancellationRequested();
                if (target.Equals(source, StringComparison.OrdinalIgnoreCase)) { Notify(T("VideoExported")); return true; }
                temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
                input.Position = 0;
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                await input.CopyToAsync(output, _copyCancel.Token); await output.FlushAsync(_copyCancel.Token);
            }
            _copyCancel.Token.ThrowIfCancellationRequested(); File.Move(temporary!, target, overwrite: true); temporary = null;
            Notify(T("VideoExported")); return true;
        }
        catch (OperationCanceledException) { Notify(T("Cancelled")); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ComponentAuditFailures.Report(error); Notify(T("ExportFailed")); return false; }
        finally
        {
            _copyCancel?.Dispose(); _copyCancel = null;
            if (temporary is not null && File.Exists(temporary))
            { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ComponentAuditFailures.Report(error); } }
            SetBusy(false); RefreshResultActions();
        }
    }
}
