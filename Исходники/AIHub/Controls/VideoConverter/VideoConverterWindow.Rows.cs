using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoConverter;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls.VideoConverter;

internal sealed partial class VideoConverterWindow
{
    private void Render()
    {
        var inputSelection = Selected(_sources);
        var outputSelection = Selected(_results);
        _sources.Items.Clear();
        _results.Items.Clear();
        var items = (_job?.Items ?? []).ToDictionary(item => item.Input, StringComparer.OrdinalIgnoreCase);
        long originals = 0, estimates = 0, readyBytes = 0;
        var measured = 0;
        var estimated = 0;
        foreach (var path in _paths)
        {
            var name = Path.GetFileName(path);
            items.TryGetValue(path, out var item);
            _media.TryGetValue(path, out var probe);
            var media = probe.Media;
            var source = media is null ? probe.Error ?? T("Reading") :
                $"{Bytes(media.Bytes)} · {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} / {media.Video.Codec.ToUpperInvariant()} · {media.OrientedWidth} × {media.OrientedHeight} · {Elapsed(media.Duration)}";
            AddRow(_sources, path, name, source, probe.Error, inputSelection);

            long sourceBytes = media?.Bytes ?? item?.Source?.Size ?? 0;
            originals += sourceBytes;
            if (sourceBytes > 0) measured++;
            ConversionPlan? plan = null;
            string? error = item?.Error;
            if (media is not null && item?.Status != ConversionStatus.Ready)
            {
                try { plan = ConversionProfiles.Candidates(_options, media).First(); }
                catch (Exception problem) when (problem is InvalidDataException or ConversionCompatibilityException)
                { error ??= problem.Message; }
            }
            long? estimate = item?.Output?.Bytes ?? plan?.Estimate;
            if (estimate is long predicted) { estimates += predicted; estimated++; }
            var waiting = _job is { } current && item is not null && ConversionStore.HasUnpackedOutput(current, item);
            if (item?.Output is { } output && !waiting) readyBytes += output.Bytes;
            var status = waiting && item?.Status == ConversionStatus.Ready
                ? T("WaitingForSave") : T("State." + (item?.Status ?? ConversionStatus.Pending));
            if (probe.Error is not null && item is null) status = T("State.Failed");
            var rowName = item?.Output is { } result ? result.FileName : name;
            var detail = item?.Output is { } complete
                ? $"{status} · {Bytes(sourceBytes)} → {Bytes(complete.Bytes)} · {Savings(sourceBytes, complete.Bytes)} · {Elapsed(complete.Seconds)}"
                : status + Forecast(plan, item);
            var notes = error ?? item?.Output?.Details ?? (plan is not null ? string.Join(" · ", plan.Changes) : probe.Error);
            if (item is { Status: ConversionStatus.OverLimit, Candidate: { } larger })
                notes = string.Join(Environment.NewLine, new[] { notes, larger.Details }.Where(line => !string.IsNullOrEmpty(line)));
            if (item?.RecycleNote is { } recycleNote) notes = string.IsNullOrWhiteSpace(notes) ? recycleNote : notes + Environment.NewLine + recycleNote;
            AddRow(_results, path, rowName, detail, notes, outputSelection);
        }
        if (_paths.Count == 0)
        {
            _sources.Items.Add(new ListBoxItem { Content = PublisherUi.Text(T("NoFiles")), IsEnabled = false });
            _results.Items.Add(new ListBoxItem { Content = PublisherUi.Text(T("NoResults")), IsEnabled = false });
        }
        _percentage.Text = $"{(int)_reduction.Value}%";
        _resolution.Content = _options.ShortEdge == 0 ? T("FrameOriginal") : _options.ShortEdge.ToString();
        var mode = Choice<ConversionMode>(_mode);
        _limitRow.Visibility = mode == ConversionMode.Size ? Visibility.Visible : Visibility.Collapsed;
        _qualityRow.Visibility = mode == ConversionMode.Quality ? Visibility.Visible : Visibility.Collapsed;
        _bitrateRow.Visibility = mode == ConversionMode.Bitrate ? Visibility.Visible : Visibility.Collapsed;
        _reduction.IsEnabled = mode == ConversionMode.Percent;
        _fps.IsEnabled = _motion.IsChecked == true;
        var ffv1 = Choice<ConversionCodec>(_codec) == ConversionCodec.Ffv1;
        _picture.IsEnabled = !ffv1;
        _executor.IsEnabled = !ffv1;
        _folder.Text = _options.OutputDirectory.Length == 0 ? T("DefaultFolder") : _options.OutputDirectory;
        _folder.ToolTip = _folder.Text;

        var tally = string.Format(T("FileCount"), _paths.Count);
        var forecast = _paths.Count == 0 ? T("NoEstimate") : estimated == _paths.Count ? "≈ " + Bytes(estimates) :
            estimated > 0 ? string.Format(T("SomeEstimates"), Bytes(estimates), estimated, _paths.Count) : T("NoEstimate");
        var bytesBefore = measured == 0 ? "—" : (measured < _paths.Count ? "≥ " : "") + Bytes(originals);
        var lines = new List<string> { tally, string.Format(T("FromTo"), bytesBefore, forecast) };
        if (_job is { } job)
        {
            var saved = job.Items.Count(item => item.Status == ConversionStatus.Ready && !ConversionStore.HasUnpackedOutput(job, item));
            lines.Add(string.Format(T("ReadyTotals"), saved, Bytes(readyBytes), job.Items.Count(i => CanRetry(i.Status))));
            if (job.Archive is { } archive)
                lines.Add(string.Format(T("ZipSize"), Bytes(archive.Bytes)));
            if (job.ArchiveError is { } zipError)
                lines.Add(string.Format(T("ZipError"), zipError));
        }
        _totals.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        UpdateButtons();
    }

    private string Forecast(ConversionPlan? plan, ConversionItem? item)
    {
        List<string> parts = [];
        if (plan is not null)
        {
            parts.Add($"{plan.Width} × {plan.Height}");
            if (plan.Limit is long cap) parts.Add(string.Format(T("TargetSize"), Bytes(cap)));
            if (plan.Estimate is long estimate) parts.Add(string.Format(T("EstimatedSize"), Bytes(estimate)));
        }
        if (item?.Candidate is { } candidate) parts.Add(string.Format(T("MeasuredSize"), Bytes(candidate.Bytes)));
        if (item is { Seconds: > 0 }) parts.Add(Elapsed(item.Seconds));
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }

    private static void AddRow(ListBox list, string path, string name, string detail, string? notes, string? selected)
    {
        var layout = new StackPanel { Margin = new(6, 4, 5, 4) };
        layout.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        var info = PublisherUi.Text(detail);
        info.Margin = new(0, 0, 0, 2);
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        layout.Children.Add(info);
        if (notes is { Length: > 0 })
        {
            var note = PublisherUi.Text(notes);
            note.Margin = new(0, 0, 0, 1);
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            layout.Children.Add(note);
        }
        var item = new ListBoxItem { Content = layout, Tag = path, ToolTip = path, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        list.Items.Add(item);
        if (path.Equals(selected, StringComparison.OrdinalIgnoreCase)) item.IsSelected = true;
    }

    private void UpdateButtons()
    {
        if (_begin is null || _recycle is null) return;
        var idle = !Busy;
        var selected = ResultSelected();
        var items = _job?.Items ?? [];
        _settingsCard.IsEnabled = idle;
        _archive.IsEnabled = idle;
        _selectFolder.IsEnabled = idle;
        _begin.IsEnabled = idle && !_reading && (_paths.Count > items.Length
            || items.Any(i => i.Status is ConversionStatus.Pending or ConversionStatus.Running)
            || _options.Archive && items.Any(i => i.Status == ConversionStatus.Ready)
            || _job is { } job && items.Any(i => ConversionStore.HasUnpackedOutput(job, i)));
        _cancel.IsEnabled = _run is not null;
        _retry.IsEnabled = idle && !_reading && (selected is null ? items.Any(i => CanRetry(i.Status)) : CanRetry(selected.Status));
        _larger.Visibility = selected?.Status == ConversionStatus.OverLimit ? Visibility.Visible : Visibility.Collapsed;
        _larger.IsEnabled = idle;
        _reveal.IsEnabled = selected?.Output is { ArchiveName: null };
        _openZip.Visibility = _job?.Archive is null ? Visibility.Collapsed : Visibility.Visible;
        _log.IsEnabled = _job is not null;
        _remove.IsEnabled = idle && Selected(_sources) is not null;
        _reset.IsEnabled = idle && _paths.Count > 0;
        _recycle.Visibility = _delete.IsChecked == true && items.Any(i => i.Status == ConversionStatus.Ready) ? Visibility.Visible : Visibility.Collapsed;
        _recycle.IsEnabled = idle;
    }

    private static bool CanRetry(ConversionStatus status) => status is ConversionStatus.Failed or ConversionStatus.OverLimit or ConversionStatus.Canceled;
    private static string Bytes(long size) => size >= 1_000_000_000 ? $"{size / 1_000_000_000d:0.##} GB"
        : size >= 1_000_000 ? $"{size / 1_000_000d:0.##} MB" : $"{size / 1000d:0.##} KB";
    private static string Elapsed(double seconds) => TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2))
        .ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss\.f");
    private string Savings(long original, long final) => original < 1 ? Bytes(final)
        : final <= original ? string.Format(T("Saved"), (original - final) / (double)original)
        : string.Format(T("Grew"), (final - original) / (double)original);
}
