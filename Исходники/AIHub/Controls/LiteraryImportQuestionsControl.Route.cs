using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Binding = System.Windows.Data.Binding;

namespace AIHub.Controls;

public sealed partial class LiteraryImportQuestionsControl
{
    private ImportRouteDraft? _route;
    private ObservableCollection<ImportRouteRow>? _routeRows;
    private DataGrid? _routeGrid;
    private CancellationTokenSource? _outlineCancellation;
    private TextBlock? _outlineStatus;
    private void CloseRoute()
    {
        _outlineCancellation?.Cancel(); _outlineCancellation = null;
        _chapterPreviewCancellation?.Cancel(); _chapterPreviewCancellation = null;
        _route = null; _routeRows = null; _routeGrid = null;
    }

    private void RenderRoute()
    {
        // Malformed saved table is preserved on disk; fall back to the old answer without replacing it.
        try { _route = ImportRouteDraft.Parse(_questions.RouteDraft, _questions.Draft); }
        catch (Exception)
        {
            _body.Children.Add(LiteraryUi.Text(T("RouteUnreadable")));
            _body.Children.Add(LiteraryUi.Button(T("Previous"), () => Change(() => _questions.Previous())));
            return;
        }
        _body.Children.Add(LiteraryUi.Text(T("RouteTitle"), true));
        _body.Children.Add(LiteraryUi.Text(T("RouteHint")));
        _routeRows = new(_route.Rows); RenumberRoute();
        _routeGrid = new DataGrid
        {
            ItemsSource = _routeRows, AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, CanUserSortColumns = false, CanUserReorderColumns = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true,
            HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 420, MinHeight = 120, FontSize = 14,
            Margin = new Thickness(0, 12, 0, 8), RowHeaderWidth = 0, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        _routeGrid.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _routeGrid.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _routeGrid.SetResourceReference(DataGrid.RowBackgroundProperty, "WindowBackgroundBrush");
        _routeGrid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "LineBrush");
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("PanelBrush")));
        header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(8)));
        _routeGrid.ColumnHeaderStyle = header;
        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("WindowBackgroundBrush")));
        cell.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("AccentBrush")));
        selected.Setters.Add(new Setter(ForegroundProperty, System.Windows.Media.Brushes.White));
        cell.Triggers.Add(selected); _routeGrid.CellStyle = cell;
        _routeGrid.Columns.Add(new DataGridTextColumn { Header = "№", Binding = new Binding("Number"), Width = 44, IsReadOnly = true });
        AddChapterLinkColumn();
        _routeGrid.Columns.Add(new DataGridCheckBoxColumn { Header = T("RouteWritten"), Binding = new Binding("Existing"), Width = 110 });
        AddRouteColumn(T("RouteName"), "Title", 2);
        AddRouteColumn(T("RouteDescription"), "Description", 3);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_routeGrid, "Import.PostQuestions.Route");
        _routeGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        { if (!_closed && _route is not null) { _save.Stop(); _save.Start(); } }));
        _routeGrid.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => { _save.Stop(); _save.Start(); }));
        _body.Children.Add(_routeGrid);
        var actions = new WrapPanel();
        actions.Children.Add(LiteraryUi.Button(T("RouteAdd"), () =>
        {
            if (!TrySaveDraft()) return;
            var row = new ImportRouteRow(); _routeRows.Add(row); RenumberRoute(); _routeGrid.Items.Refresh();
            _routeGrid.SelectedItem = row; _routeGrid.ScrollIntoView(row); TrySaveDraft();
        }));
        actions.Children.Add(LiteraryUi.Button(T("RouteRemove"), () =>
        {
            if (_routeGrid.SelectedItem is not ImportRouteRow row || !TrySaveDraft()) return;
            _routeRows.Remove(row); RenumberRoute(); _routeGrid.Items.Refresh(); TrySaveDraft();
        }));
        actions.Children.Add(LiteraryUi.Button(T("RouteExtract"), () => _ = ExtractRouteAsync()));
        _body.Children.Add(actions);
        _outlineStatus = LiteraryUi.Text(""); _body.Children.Add(_outlineStatus);
        _answer = new TextBox { Text = _route.Notes, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 60, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _answer.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _answer.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _answer.TextChanged += (_, _) => { _save.Stop(); _save.Start(); };
        var notes = new Expander { Header = T("RouteNotes"), Content = _answer, IsExpanded = _answer.Text.Length > 0 };
        notes.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); _body.Children.Add(notes);
        var previous = _questions.Suggestion(_bookRevision);
        if (previous.Length > 0)
        {
            var proposal = new Expander { Header = T("ProposalTitle"), Content = LiteraryUi.Text(previous) };
            proposal.SetResourceReference(ForegroundProperty, "TextPrimaryBrush"); _body.Children.Add(proposal);
        }
        var nav = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (_questions.Position > 0) nav.Children.Add(LiteraryUi.Button(T("Previous"), () => Change(() => _questions.Previous())));
        nav.Children.Add(LiteraryUi.Button(T("Confirm"), () =>
        {
            if (!TrySaveDraft()) return;
            var text = _route.Answer(_language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = T("AnswerRequired"); return; }
            Change(() => _questions.Confirm(text, _route.Serialize()));
        }, true));
        nav.Children.Add(LiteraryUi.Button(T("Skip"), () =>
        {
            if (!TrySaveDraft()) return;
            // Keep the table as a draft even when the answer is deliberately left unconfirmed.
            Change(() => _questions.Confirm("", _route.Serialize()));
        }));
        _body.Children.Add(nav);
        if (!_route.Scanned && _route.Rows.Count == 0 && string.IsNullOrWhiteSpace(_route.Notes))
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            { if (!_closed && _route is { Scanned: false }) _ = ExtractRouteAsync(); }));
    }

    private void AddRouteColumn(string title, string property, int width)
    {
        var text = new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        text.Setters.Add(new Setter(MarginProperty, new Thickness(6)));
        var edit = new Style(typeof(TextBox));
        edit.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("WindowBackgroundBrush")));
        edit.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        edit.Setters.Add(new Setter(TextBox.TextWrappingProperty, TextWrapping.Wrap));
        _routeGrid!.Columns.Add(new DataGridTextColumn { Header = title,
            Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(width, DataGridLengthUnitType.Star), ElementStyle = text, EditingElementStyle = edit });
    }
    private void RenumberRoute() { for (var i = 0; i < _routeRows!.Count; i++) _routeRows[i].Number = i + 1; }
    private async Task ExtractRouteAsync()
    {
        if (_outlineCancellation is not null || _route is null || !TrySaveDraft()) return;
        using var cancellation = new CancellationTokenSource(); _outlineCancellation = cancellation;
        var draft = _route; _outlineStatus!.Text = T("RouteReading");
        try
        {
            var found = await ImportChapterOutline.ReadAsync(_projectRoot, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(_route, draft)) return;
            foreach (var item in found.Items)
                if (!_routeRows!.Any(r => r.SourceId == item.SourceId))
                    _routeRows!.Add(new() { SourceId = item.SourceId, SourceRevision = item.Revision, Existing = true,
                        Title = item.Title.Length > 0 ? item.Title : string.Format(T("RouteChapter"), _routeRows.Count + 1) });
            draft.Scanned = true; RenumberRoute(); _routeGrid!.Items.Refresh(); TrySaveDraft();
            _outlineStatus.Text = T(found.Failed ? "RouteFailed" : found.Limited ? "RouteLimited" : found.Items.Length == 0 ? "RouteEmpty" : "RouteFound");
        }
        catch (Exception) { if (!_closed && ReferenceEquals(_route, draft)) _outlineStatus!.Text = T("RouteFailed"); }
        finally { if (ReferenceEquals(_outlineCancellation, cancellation)) _outlineCancellation = null; }
    }
}
