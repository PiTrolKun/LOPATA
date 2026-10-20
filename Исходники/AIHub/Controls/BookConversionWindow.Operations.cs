using System.Diagnostics;
using System.IO;
using System.Windows;
using AIHub.Services.Books;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private async Task AddFilesDialogAsync()
    {
        var filter = string.Join(";", BookConversionFormats.InputExtensions.Select(value => "*." + value));
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true,
            Title = L("AddFiles"), Filter = L("SupportedFiles") + "|" + filter + "|" + L("AllFiles") + "|*.*" };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FileNames);
    }

    private async Task AddFolderDialogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L("AddFolder"), Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddPathsAsync(dialog.FolderNames);
    }

    private async Task AddPathsAsync(string[] paths)
    {
        if (_busy) return;
        ShowMode(_smartMode);
        var allowed = new HashSet<string>(BookConversionFormats.InputExtensions.Select(value => "." + value), StringComparer.OrdinalIgnoreCase);
        SetBusy(true); _work = new CancellationTokenSource(); var token = _work.Token;
        try
        {
            var expanded = await Task.Run(() =>
            {
                var found = new List<string>();
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        foreach (var file in Directory.EnumerateFiles(path, "*", options))
                        { token.ThrowIfCancellationRequested(); if (allowed.Contains(Path.GetExtension(file))) found.Add(file); }
                    }
                    else if (File.Exists(path) && allowed.Contains(Path.GetExtension(path))) found.Add(Path.GetFullPath(path));
                }
                return found.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            }, token);
            var existing = new HashSet<string>(_entries.Select(row => row.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in expanded) if (existing.Add(path))
            {
                var entry = new Entry(path) { Status = L("Queued") };
                entry.PropertyChanged += SourceEntryChanged; _entries.Add(entry);
            }
            _status.Text = expanded.Length == 0 ? L("NoSupportedFiles") : string.Format(L("FilesAdded"), _entries.Count);
            RefreshFormats();
        }
        catch (OperationCanceledException) { _status.Text = L("Cancelled"); }
        catch (Exception error) { ShowDiagnosticsError(error); }
        finally { FinishWork(); }
    }

    private async Task ConvertAsync()
    {
        if (_busy) return;
        var selectedEntries = _entries.Where(row => row.IsChecked).ToArray();
        if (selectedEntries.Length == 0) return;
        if (_smartMode && !await PrepareSmartAsync()) return;
        var format = (_format.SelectedItem as string ?? "EPUB").ToLowerInvariant();
        var request = new BookConversionRequest
        {
            Smart = _smartMode ? _smartSelection : null,
            Sources = selectedEntries.Select(row => row.Path).ToArray(), OutputFormat = format,
            OutputDirectory = _directory.Text.Trim(), Merge = _merge.IsChecked == true,
            Title = _merge.IsChecked == true ? EmptyAsNull(_title.Text) : null,
            Author = _merge.IsChecked == true ? EmptyAsNull(_author.Text) : null,
            Language = _merge.IsChecked == true ? EmptyAsNull(_language.Text) : null,
            CoverPath = _merge.IsChecked == true && format is "epub" or "fb2" or "docx" or "pdf" ? _coverPath : null,
            Options = _options with
            {
                GenerateTableOfContents = format != "epub" || _options.GenerateTableOfContents,
                FontSize = format is "pdf" or "html" or "epub" ? _options.FontSize : 12,
                MarginMillimeters = format is "pdf" or "html" ? _options.MarginMillimeters : 15,
                ImageQuality = format == "cbz" ? _options.ImageQuality : 90,
                TextEncoding = selectedEntries.Any(row => Path.GetExtension(row.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) ? _options.TextEncoding : "utf-8"
            }
        };
        BeginDiagnostics();
        foreach (var row in selectedEntries) { row.Status = L("Queued"); row.Output = ""; }
        SetBusy(true);
        _work = new CancellationTokenSource();
        try
        {
            var progress = new Progress<BookConversionProgress>(UpdateProgress);
            var token = _work.Token;
            // Reading, package rewriting and rendering also contain CPU-bound work.
            var result = await Task.Run(() => _smartMode ? RunSmartAsync(request, progress, token)
                : new BookConversionService(ReviewBeforeSaveAsync).ConvertAsync(request, progress, token), token);
            foreach (var failure in result.Failures)
            {
                var row = _entries.FirstOrDefault(value => value.Path.Equals(failure.Source, StringComparison.OrdinalIgnoreCase));
                if (row is not null) row.Status = L("Failed");
            }
            if (request.Merge && result.Outputs.Length > 0)
                foreach (var row in selectedEntries) { row.Status = L("Done"); row.Output = result.Outputs[0]; }
            foreach (var row in selectedEntries.Where(row => result.Skipped.Contains(row.Path, StringComparer.OrdinalIgnoreCase)
                || request.Merge && result.Skipped.Length > 0)) row.Status = L("SkippedShort");
            if (result.Cancelled)
                foreach (var row in selectedEntries.Where(row => row.Output.Length == 0 && row.Status != L("Failed") && row.Status != L("SkippedShort"))) row.Status = L("CancelledShort");
            CompleteDiagnostics(result);
        }
        finally { FinishWork(); }
    }
    private static string? EmptyAsNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void UpdateProgress(BookConversionProgress value)
    {
        if (value.Phase == "diagnostic") { AppendLog(value.Message); return; }
        _progress.IsIndeterminate = value.Total <= 0;
        if (value.Total > 0) _progress.Value = Math.Clamp((double)value.Completed / value.Total, 0, 1);
        _status.Text = DescribeProgress(value);
        if (_status.Text != _lastProgressLog) { AppendLog(_status.Text); _lastProgressLog = _status.Text; }
        var row = _entries.FirstOrDefault(entry => entry.Path.Equals(value.Source, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            row.Status = L(value.Phase == "failed" ? "Failed" : value.OutputPath.Length > 0 ? "Done" : "Working");
            if (value.OutputPath.Length > 0) row.Output = value.OutputPath;
        }
    }

    private void FinishWork()
    {
        _work?.Dispose(); _work = null; SetBusy(false);
        if (_closeWhenDone) { _allowClose = true; Close(); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; _settings.IsEnabled = _back.IsEnabled = !busy;
        SetValue(AreSourcesEditableProperty, !busy);
        if (_checkAll is not null) _checkAll.IsEnabled = !busy && _entries.Count > 0;
        foreach (var button in _editButtons) button.IsEnabled = !busy;
        _progress.IsIndeterminate = busy; RefreshActions();
    }
    private void RefreshActions()
    {
        if (_start is null) return;
        _start.IsEnabled = !_busy && _entries.Any(row => row.IsChecked);
        if (_removeSources is not null) _removeSources.IsEnabled = _start.IsEnabled;
        _cancel.IsEnabled = _busy;
        var exists = _entries.Any(row => row.IsChecked && File.Exists(row.Output));
        _open.IsEnabled = _showFolder.IsEnabled = !_busy && exists;
    }
    private void OpenResult(bool folder)
    {
        var path = _entries.Where(row => row.IsChecked).Select(row => row.Output).FirstOrDefault(File.Exists);
        if (path is null) return;
        if (folder) OpenPath(Path.GetDirectoryName(path)!); else OpenPath(path);
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { _status.Text = L("OpenFailed"); _report.Text = Explain(error.Message); AppendLog(_report.Text); }
    }
}
