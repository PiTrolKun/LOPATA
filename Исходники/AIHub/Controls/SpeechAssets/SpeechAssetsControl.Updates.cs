using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.SpeechAssets;

namespace AIHub.Controls.SpeechAssets;

internal sealed partial class SpeechAssetsControl
{
    private readonly ConcurrentDictionary<string, byte> _changedRows = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Task _updatingRows = Task.CompletedTask;
    private string? _pendingStatus, _boundProject;

    private void InitializeUpdates()
    {
        _updateTimer.Tick += (_, _) =>
        {
            if (_updatingRows.IsCompleted && !_confirming) _updatingRows = ApplyPendingUpdatesAsync();
        };
    }

    private void QueueRowUpdate(string id) => _changedRows[id] = 0;
    private void QueueStatus(string text) => Interlocked.Exchange(ref _pendingStatus, text);

    private async Task ApplyPendingUpdatesAsync()
    {
        if (Interlocked.Exchange(ref _pendingStatus, null) is { } status) _status.Text = status;
        var ids = _changedRows.Keys.ToHashSet();
        if (ids.Count == 0) return;
        foreach (var id in ids) _changedRows.TryRemove(id, out _);
        var project = _session.Id;
        try
        {
            var latest = await Task.Run(() => _store.Load(project));
            if (_session.Id != project) return;
            if (_confirming) { foreach (var id in ids) QueueRowUpdate(id); return; }
            MergeSession(latest, ids); Counts();
        }
        catch (Exception error) { Report(error); }
    }

    private async Task FinishUpdatesAsync()
    {
        _updateTimer.Stop();
        await _updatingRows;
        Interlocked.Exchange(ref _pendingStatus, null);
        _changedRows.Clear();
    }

    // Preserve row and list identities, focus and scroll position. Only realized changed rows rebuild their content.
    private void MergeSession(SpeechAssetSession incoming, HashSet<string>? changed = null)
    {
        if (_session.Id != incoming.Id || _session.Rows.Count != incoming.Rows.Count
            || !_session.Rows.Select(r => r.Id).SequenceEqual(incoming.Rows.Select(r => r.Id)))
        { _session = incoming; Refresh(); return; }
        var rows = _session.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            var source = incoming.Rows[i];
            if (changed is not null && !changed.Contains(source.Id)) continue;
            var target = rows[i];
            target.Group = source.Group; target.SourceText = source.SourceText; target.Text = source.Text;
            target.FileName = source.FileName; target.Included = source.Included; target.Confirmed = source.Confirmed;
            target.Takes = source.Takes; target.LastError = source.LastError; target.FailedAttempts = source.FailedAttempts;
        }
        incoming.Rows = rows; _session = incoming;
        RefreshVisibleRows(changed);
    }

    private void RefreshVisibleRows(HashSet<string>? changed = null)
    {
        foreach (var row in VisibleRows(_rows))
            if (changed is null || row.DataContext is SpeechAssetRow data && changed.Contains(data.Id)) row.Render();
        static IEnumerable<SpeechAssetRowView> VisibleRows(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is SpeechAssetRowView row) yield return row;
                else foreach (var nested in VisibleRows(child)) yield return nested;
            }
        }
    }

    private void BindRows()
    {
        if (_boundProject != _session.Id || !ReferenceEquals(_rows.ItemsSource, _session.Rows))
        { _rows.ItemsSource = _session.Rows; _boundProject = _session.Id; }
        else RefreshVisibleRows();
    }
}
