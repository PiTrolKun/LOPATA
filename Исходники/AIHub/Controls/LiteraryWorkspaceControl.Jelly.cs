using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private bool _jellyEditing;
    private async Task<bool> PrepareJellyAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken token, string? preparationMode = null)
    {
        var layout = new LiteraryProjectLayout(_entry.ProjectPath);
        var mode = LiteraryProjectStore.ReadProject(layout.Root).JellyExecutor;
        return await _runtime.WithJellyExecutorAsync(mode, extract => new LiteraryJellyPreparation(layout, extract, mode).PrepareAsync(async (batch, commit) =>
        {
            token.ThrowIfCancellationRequested();
            using var log = new LiteraryRequestDiagnostics("JellyReview", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            var summary = await Task.Run(() => AIHub.Services.LiteraryImport.ImportProjectStatus.Read(layout.Root), token);
            token.ThrowIfCancellationRequested();
            var result = LiteraryJellyReviewDialog.Show(this, _l,
                batch.Facts.Select(f => new LiteraryJellyReviewItem(f, batch.Number, batch.SourceText)).ToArray(), commit, (kind, data) => log.Write(kind, data), language:_project.LanguageCode,
                summary:summary.Describe(_l),
                acceptWarnings:preparationMode is null ? null : decisions=>Task.Run(()=>
                {
                    token.ThrowIfCancellationRequested();
                    new LiteraryJellyStore(layout).ConfirmPrepared(batch,decisions,"manual",true);
                },token));
            return result;
        }, progress, token, mode:preparationMode, technicalAttempts:preparationMode is null ? 3 : 1), token);
    }
    private async Task OpenJellyAsync()
    {
        if (Indexing || _runtime.IsBusy || _projectMissing) return;
        _jellyEditing = true;
        _writer.ActionsBlocked = _advisor.ActionsBlocked = true;
        _writer.RefreshAvailability(); _advisor.RefreshAvailability(); _draft.BlockActions(true);
        var previousStatus = _memoryStatus.Text;
        _memoryStatus.Text = _l("Literary.Jelly.Loading");
        _memoryProgress.IsIndeterminate = true; _memoryProgress.Visibility = Visibility.Visible;
        try
        {
            var layout = new LiteraryProjectLayout(_entry.ProjectPath); var store = new LiteraryJellyStore(layout);
            using var log = new LiteraryRequestDiagnostics("JellyEdit", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            var entries = await Task.Run(store.Read);
            if (entries.Count == 0) { System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Literary.Jelly.Empty"), _l("Literary.Jelly.Title")); return; }
            var items = await Task.Run(() =>
            {
                var sources = new Dictionary<(string Part, string Revision), string>();
                return entries.Select(e =>
                {
                    var key = (e.PartId, e.Revision);
                    if (!sources.TryGetValue(key, out var text))
                        sources[key] = text = store.Find(e.PartId, e.Revision)?.SourceText
                            ?? throw new System.IO.IOException("Memory source unavailable.");
                    return new LiteraryJellyReviewItem(e.Fact, e.Number, text);
                }).ToArray();
            });
            _memoryProgress.IsIndeterminate = false; _memoryProgress.Visibility = Visibility.Collapsed;
            _memoryStatus.Text = previousStatus;
            log.Write("opened", entries);
            LiteraryJellyReviewDialog.Show(this, _l, items, async decisions =>
            {
                log.Write("user_edits", decisions);
                try { await Task.Run(() => store.Edit(entries, decisions)); }
                catch (Exception ex) { log.Write("edit_failed", new { ex.Message }); throw; }
                log.Write("edit_committed", new { count = decisions.Count });
            }, (kind, data) => log.Write(kind, data), (source,text)=>_studio?.AttachQuote(source,text),_project.LanguageCode);
            _studio?.ClearRequestStatus();
            await RefreshImportExportAsync();
        }
        catch (Exception) { System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Literary.Jelly.LoadError"), _l("Literary.Jelly.Title")); }
        finally
        {
            _memoryProgress.IsIndeterminate = false; _memoryProgress.Visibility = Visibility.Collapsed;
            if (_memoryStatus.Text == _l("Literary.Jelly.Loading")) _memoryStatus.Text = previousStatus;
            _jellyEditing = false;
            _writer.ActionsBlocked = _advisor.ActionsBlocked = _projectMissing;
            _writer.RefreshAvailability(); _advisor.RefreshAvailability(); _draft.BlockActions(_projectMissing);
        }
    }
}
