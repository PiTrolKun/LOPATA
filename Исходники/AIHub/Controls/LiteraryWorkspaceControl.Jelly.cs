using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private bool _jellyEditing;
    private async Task<bool> PrepareJellyAsync(IProgress<LiteraryPreparationProgress> progress, CancellationToken token)
    {
        var layout = new LiteraryProjectLayout(_entry.ProjectPath);
        var mode = LiteraryProjectStore.ReadProject(layout.Root).JellyExecutor;
        return await _runtime.WithJellyExecutorAsync(mode, extract => new LiteraryJellyPreparation(layout, extract, mode).PrepareAsync((batch, commit) =>
        {
            token.ThrowIfCancellationRequested();
            using var log = new LiteraryRequestDiagnostics("JellyReview", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            var result = LiteraryJellyReviewDialog.Show(this, _l,
                batch.Facts.Select(f => new LiteraryJellyReviewItem(f, batch.Number, batch.SourceText)).ToArray(), commit, (kind, data) => log.Write(kind, data));
            return Task.FromResult(result);
        }, progress, token), token);
    }
    private async Task OpenJellyAsync()
    {
        if (Indexing || _runtime.IsBusy || _projectMissing) return;
        _jellyEditing = true;
        _writer.ActionsBlocked = _advisor.ActionsBlocked = true;
        _writer.RefreshAvailability(); _advisor.RefreshAvailability(); _draft.BlockActions(true);
        try
        {
            var layout = new LiteraryProjectLayout(_entry.ProjectPath); var store = new LiteraryJellyStore(layout);
            using var log = new LiteraryRequestDiagnostics("JellyEdit", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            var entries = await Task.Run(store.Read);
            if (entries.Count == 0) { System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Literary.Jelly.Empty"), _l("Literary.Jelly.Title")); return; }
            var items = await Task.Run(() => entries.Select(e => new LiteraryJellyReviewItem(e.Fact, e.Number,
                store.Find(e.PartId, e.Revision)?.SourceText ?? throw new System.IO.IOException("Memory source unavailable."))).ToArray());
            log.Write("opened", entries);
            LiteraryJellyReviewDialog.Show(this, _l, items, async decisions =>
            {
                log.Write("user_edits", decisions);
                try { await Task.Run(() => store.Edit(entries, decisions)); }
                catch (Exception ex) { log.Write("edit_failed", new { ex.Message }); throw; }
                log.Write("edit_committed", new { count = decisions.Count });
            }, (kind, data) => log.Write(kind, data));
        }
        catch (Exception) { System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Literary.Jelly.LoadError"), _l("Literary.Jelly.Title")); }
        finally
        {
            _jellyEditing = false;
            _writer.ActionsBlocked = _advisor.ActionsBlocked = _projectMissing;
            _writer.RefreshAvailability(); _advisor.RefreshAvailability(); _draft.BlockActions(_projectMissing);
        }
    }
}
