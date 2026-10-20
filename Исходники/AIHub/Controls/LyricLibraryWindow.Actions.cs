using System.Windows;
using AIHub.Services;
using AIHub.Services.MusicLibrary;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class LyricLibraryWindow
{
    private bool Save(bool newVersion)
    {
        try
        {
            CheckMutation();
            var entry = _fields.Read();
            if (newVersion) entry = entry with { Id = Guid.NewGuid().ToString("N"), Revision = 0, PreviousVersionId = _selected?.Revision > 0 ? _selected.Id : null };
            var result = _store.Save(entry); Refresh(); Select(result.Entry);
            _list.SelectedItem = (_list.ItemsSource as IEnumerable<LyricEntry>)?.FirstOrDefault(e => e.Id == result.Entry.Id);
            _status.Text = T(result.Duplicate ? "Duplicate" : "Saved");
            return true;
        }
        catch (Exception error) { Error(error); return false; }
    }
    private async void AddFiles()
    {
        try { CheckMutation(); } catch (Exception error) { Error(error); return; }
        if (!LeaveEntry()) return;
        var dialog = new OpenFileDialog { Multiselect = true, Filter = T("FileFilter") };
        if (dialog.ShowDialog(this) != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }
    private Task ImportFilesAsync(IReadOnlyList<string> paths) => RunAsync(async token => {
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            var added = 0; var duplicates = 0;
            var staged = new List<LyricEntry>();
            var errors = new List<string>();
            foreach (var path in paths)
            {
                try
                {
                    var entries = await Task.Run(() => LyricLibraryImport.Read(path, token), token);
                    staged.AddRange(entries);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { errors.Add(System.IO.Path.GetFileName(path) + ": " + (error.Message.StartsWith("Music.Library.", StringComparison.Ordinal) ? _l(error.Message) : error.Message)); }
            }
            token.ThrowIfCancellationRequested();
            if (staged.Count > 0)
            {
                var review = new LyricImportWindow(this, _l, staged);
                if (review.ShowDialog() != true || review.Entries is null) { _status.Text = T("Cancelled"); return; }
                foreach (var entry in review.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await Task.Run(() => LyricLibraryImport.Merge(_store, entry), token);
                    if (result.Duplicate) duplicates++; else added++;
                }
            }
            Refresh(); _status.Text = string.Format(T("Added"), added, duplicates)
                + (errors.Count > 0 ? "\n" + T("Failed") + "\n" + string.Join("\n", errors) : "");
        });
    private void Delete()
    {
        try
        {
            CheckMutation();
            if (_selected is not { Revision: > 0 } entry) return;
            if (MessageBox.Show(this, T("DeleteQuestion"), T("Library"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _store.SetDeleted(entry.Id, entry.Revision, true); _deleted = entry with { Revision = entry.Revision + 1 };
            Refresh(); Select(new() { Title = T("Untitled") }); _status.Text = T("Deleted");
        }
        catch (Exception error) { Error(error); }
    }
    private void Restore()
    {
        try
        {
            CheckMutation();
            if (_deleted is null) { _status.Text = T("NothingToRestore"); return; }
            _store.SetDeleted(_deleted.Id, _deleted.Revision, false);
            var restored = _store.Find(_deleted.Id)!; _deleted = null; Refresh(); Select(restored);
        }
        catch (Exception error) { Error(error); }
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "LOPATA (*.json)|*.json", FileName = "lyrics-library.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { LyricLibraryImport.Export(_store, dialog.FileName); _status.Text = T("Exported"); }
        catch (Exception error) { Error(error); }
    }
    private static void CheckMutation()
    {
        if (ApplicationBackgroundOperations.Current?.BlocksNewWork == true)
            throw new InvalidOperationException("Music.Poetry.OtherOperation");
    }
    private Task SmartSearchAsync() => RunAsync(async token => {
        if (!LeaveEntry()) return;
        if (string.IsNullOrWhiteSpace(_query.Text)) { _status.Text = T("EnterQuery"); return; }
        var hits = await new LyricLibrarySearch(_store, LyricLibraryPreparation.ModelsRoot).SearchAsync(_query.Text,
            new Progress<RagBookProgress>(p => _status.Text = T("Indexing") + $" {p.Done}/{p.Total}"), token);
        var entries = _store.Snapshot().Entries.ToDictionary(e => e.Id);
        _list.ItemsSource = hits.DistinctBy(h => h.Id).Where(h => entries.ContainsKey(h.Id)).Select(h => entries[h.Id]).ToArray();
        _status.Text = string.Format(T("Found"), _list.Items.Count);
        if (hits.Length > 0) { _list.SelectedIndex = 0; _source.Text += "\n" + T("MatchedSection") + ": " + hits[0].Section; }
    });
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return; _busy = true;
        _fields.IsEnabled = _list.IsEnabled = _query.IsEnabled = false;
        foreach (var button in _actions) button.IsEnabled = false;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception error) { Error(error); }
        finally
        {
            _busy = false; foreach (var button in _actions) button.IsEnabled = true;
            _fields.IsEnabled = _list.IsEnabled = _query.IsEnabled = true;
            if (_closing) Close();
        }
    }
}
