using System.IO;
using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private CancellationTokenSource? _exportCancel;
    internal Func<string, string?>? ExportDestination { get; set; }
    internal async Task ExportConversationAsync(string id)
    {
        if (_library is null || _exportCancel is not null || _disposed) return;
        if (_background?.BlocksNewWork == true || IsWorking || IsWarming)
        { _statusKey = "LetItOut.Busy"; Availability(); return; }
        try
        {
            if (id == _sessionId && !SaveConversationForExit()) return;
            var topic = _library.Load(id); if (topic is null) return;
            var title = TopicName(topic.Summary);
            string? path;
            if (ExportDestination is not null) path = ExportDestination(title);
            else
            {
                var safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = _l("LetItOut.Library.Export"),
                    Filter = _l("LetItOut.Export.Filter"), DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true,
                    FileName = "LOPATA - " + safe[..Math.Min(100, safe.Length)] + ".zip" };
                path = dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
            }
            if (path is null) return;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
            _exportCancel = cancel; _statusKey = "LetItOut.Export.Working"; Availability();
            await LetItOutConversationExport.RunAsync(_library, new(id, path, _language()), cancel.Token);
            _statusKey = "LetItOut.Export.Saved";
        }
        catch (OperationCanceledException) { _statusKey = "LetItOut.Stopped"; }
        catch (Exception error)
        {
            _statusKey = "LetItOut.Export.Failed";
            OwnedProcessRegistry.Log("chat_export_failed", "LetItOut.Export", detail: error.GetType().Name);
        }
        finally { _exportCancel = null; if (!_disposed) Availability(); }
    }
}
