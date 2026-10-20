using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.Books;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

/// <summary>Review of the private converted artifact before its final, non-overwriting publication.</summary>
public sealed class BookConversionReviewWindow : Window
{
    private readonly BookReviewSession _session;
    private readonly Func<string, string> _l;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FlowDocumentScrollViewer _viewer = new() { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _save, _edit, _skip;
    private readonly TextBlock _evidenceNotice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Dictionary<string, FileViewerWindow> _originalWindows = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy, _closed, _closeRequested, _allowClose;
    public BookReviewDecision Decision { get; private set; } = BookReviewDecision.Cancel;

    public BookConversionReviewWindow(Window owner, BookReviewSession session, Func<string, string> localize)
    {
        _session = session; _l = localize; Owner = owner;
        MusicWishUi.PrepareWindow(this, T("Title"), "BookConverter.Review.Window", 1040);
        Resources.MergedDictionaries.Insert(0, owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/BookConversionResources.xaml", UriKind.Relative) });
        Height = 820; MinWidth = 720; MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(MusicWishUi.Text(string.Format(T("ResultHeading"), Path.ChangeExtension(session.SourceName, session.OutputFormat), session.OutputFormat.ToUpperInvariant()), true));
        heading.Children.Add(MusicWishUi.Text(T("Hint")));
        heading.Children.Add(MusicWishUi.Text(T("StructureHint")));
        AddOriginalButton(heading);
        if (_session.Evidence.Marks.Length > 0) heading.Children.Add(MusicWishUi.Text(T("EvidenceLegend")));
        AutomationProperties.SetAutomationId(_evidenceNotice, "BookConverter.Review.EvidenceNotice");
        heading.Children.Add(_evidenceNotice);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(_status);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed };
        footer.Children.Add(progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        _edit = MusicWishUi.Button(T("Edit"), "BookConverter.Review.Edit", async () =>
        {
            if (_busy || _closed) return;
            var editor = new BookDocumentEditorWindow(this, _session.Document, _l, _session.OutputFormat);
            if (editor.ShowDialog() != true || editor.EditedDocument is null || _closed) return;
            _busy = true; progress.Visibility = Visibility.Visible; _status.Text = T("Refreshing"); RefreshButtons();
            try
            {
                _session.SetDocument(editor.EditedDocument);
                await Task.Run(() => _session.RefreshPreviewAsync(_lifetime.Token), _lifetime.Token);
                if (!_closed) { Render(); _status.Text = ""; }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_closed) _status.Text = T("Error") + " " + (ex.Message.StartsWith("BookConverter.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message);
            }
            finally
            {
                _busy = false;
                progress.Visibility = Visibility.Collapsed;
                if (_closeRequested) { _allowClose = true; Close(); }
                else if (!_closed) RefreshButtons();
            }
        });
        _save = MusicWishUi.Button(T("Save"), "BookConverter.Review.Save", () =>
        {
            if (_busy || _session.IsDirty || _closed) return;
            Decision = BookReviewDecision.Save; DialogResult = true;
        });
        _skip = MusicWishUi.Button(T("Skip"), "BookConverter.Review.Skip", () => { if (!_busy) { Decision = BookReviewDecision.Skip; DialogResult = false; } });
        var cancel = MusicWishUi.Button(T("Cancel"), "BookConverter.Review.Cancel", RequestCancel);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; RequestCancel(); } };
        buttons.Children.Add(_edit); buttons.Children.Add(_skip); buttons.Children.Add(cancel); buttons.Children.Add(_save); footer.Children.Add(buttons);
        AutomationProperties.SetAutomationId(_viewer, "BookConverter.Review.Document"); root.Children.Add(_viewer);
        Closing += (_, e) =>
        {
            if (_busy && !_allowClose)
            {
                // Keep the review callback and its private work directory alive until the writer really exits.
                e.Cancel = true; _closeRequested = true; Decision = BookReviewDecision.Cancel;
                _lifetime.Cancel(); _status.Text = T("Cancelling"); return;
            }
            _closed = true; _lifetime.Cancel();
        };
        Render(); RefreshButtons();
    }

    private string T(string key) => _l("BookConverter.Review." + key);
    private void RequestCancel()
    {
        Decision = BookReviewDecision.Cancel;
        if (_busy) Close(); else DialogResult = false;
    }
    private void Render()
    {
        var evidence = _session.Evidence;
        _viewer.Document = BookReviewFlowRenderer.Create(_session.Document, _l, evidence, _session.IsEvidenceCurrent, out var unresolved);
        _evidenceNotice.Text = unresolved == 0 ? "" : !_session.IsEvidenceCurrent ? T("EvidenceStale") : string.Format(T("EvidenceUnmatched"), unresolved);
        _evidenceNotice.Visibility = unresolved == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddOriginalButton(StackPanel heading)
    {
        var paths = BookReviewEvidenceResolver.OriginalSources(_session.Evidence);
        if (paths.Length == 0) return;
        var button = MusicWishUi.Button(T("OpenOriginal"), "BookConverter.Review.OpenOriginal", () => { });
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        if (paths.Length == 1) button.Click += (_, _) => OpenOriginal(paths[0]);
        else
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.SetResourceReference(ContextMenu.BackgroundProperty, "PanelBrush");
            menu.SetResourceReference(ContextMenu.ForegroundProperty, "TextPrimaryBrush");
            foreach (var path in paths)
            {
                var entry = new MenuItem { Header = Path.GetFileName(path), ToolTip = path, Tag = path };
                AutomationProperties.SetAutomationId(entry, "BookConverter.Review.Original." + menu.Items.Count);
                entry.Click += (_, _) => OpenOriginal(path); menu.Items.Add(entry);
            }
            button.ContextMenu = menu; button.Click += (_, _) => menu.IsOpen = true;
            button.ToolTip = T("ChooseOriginal");
        }
        heading.Children.Add(button);
    }

    private void OpenOriginal(string path)
    {
        if (!File.Exists(path)) { _status.Text = T("OriginalUnavailable"); return; }
        try
        {
            if (_originalWindows.TryGetValue(path, out var existing))
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate(); return;
            }
            var background = TryFindResource("WindowBackgroundBrush") as SolidColorBrush;
            var color = background?.Color ?? Colors.White;
            var dark = .2126 * color.R + .7152 * color.G + .0722 * color.B < 128;
            var original = new FileViewerWindow(path, dark, _l, originalPdfPages: true) { Owner = this };
            _originalWindows[path] = original;
            original.Closed += (_, _) => _originalWindows.Remove(path);
            original.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { _status.Text = T("OriginalUnavailable"); }
    }
    private void RefreshButtons()
    {
        _save.IsEnabled = !_busy && !_session.IsDirty;
        _skip.IsEnabled = !_busy;
        var editable = _session.Document.Blocks.Any(b => !string.IsNullOrWhiteSpace(b.Text));
        _edit.IsEnabled = !_busy && editable;
        _edit.ToolTip = editable ? null : T("NoEditableText");
        if (!editable && !_busy) _status.Text = T("NoEditableText");
    }
}
