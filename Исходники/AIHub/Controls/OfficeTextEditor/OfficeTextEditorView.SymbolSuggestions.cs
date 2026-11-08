using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private bool _symbolSuggestionsPermitted, _symbolSuggestionFailed;
    private readonly DispatcherTimer _nativeSymbolDelay = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly Popup _nativeSymbols = new() { StaysOpen = true, AllowsTransparency = true, Placement = PlacementMode.Custom };
    private TextBox? _typingField;
    private OfficeUnicodeSymbol[] _nativeChoices = [];
    private int _nativeChoice = -1, _nativeRequest;

    private async Task PrepareSymbolSuggestionsAsync(CancellationToken token)
    {
        try { await ComponentLicenseGate.EnsureAsync(OfficeUnicodeCatalog.Component, token); _symbolSuggestionsPermitted = true; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { _symbolSuggestionsPermitted = false; }
    }

    private async Task<OfficeUnicodeSymbol[]> FindSymbolSuggestionsAsync(string word)
    {
        if (!_symbolSuggestionsPermitted || _symbolSuggestionFailed || word.Length > 40) return [];
        try
        {
            var catalog = await OfficeUnicodeCatalog.LoadAsync(_lifetime.Token);
            return await Task.Run(() => catalog.Suggestions.Find(word), _lifetime.Token);
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _symbolSuggestionFailed = true; ComponentAuditFailures.Report(error);
            if (!_closed) _status.Text = _l("OfficeSymbols.DataMissing");
            return [];
        }
    }

    private async Task SendSymbolSuggestionsAsync(JsonElement message, OfficeTextBrowser browser)
    {
        if (!_ready || _closing || _closed) return;
        var word = message.GetProperty("word").GetString(); var key = message.GetProperty("key").GetString();
        if (word == null || key == null || word.Length > 40 || key.Length > 30) return;
        var choices = await FindSymbolSuggestionsAsync(word);
        if (!_closed && ReferenceEquals(browser, _browser) && IsTextSheet)
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { action = "symbolSuggestions", key,
                items = choices.Select(s => new { text = s.Text, name = IsRussian && s.RussianName.Length > 0 ? s.RussianName : s.Name }) }));
    }

    private void BuildNativeSymbolSuggestions()
    {
        _sheetPanel.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, e) =>
        {
            if (e.Text.Length == 0 || !_symbolSuggestionsPermitted) return;
            var field = TextField(e.OriginalSource as DependencyObject);
            if (field is not { IsReadOnly: false }) return;
            CloseNativeSymbolSuggestions(); _typingField = field; _nativeSymbolDelay.Start();
        }), true);
        _sheetPanel.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(NativeSymbolKey), true);
        _root.PreviewMouseDown += (_, _) => CloseNativeSymbolSuggestions();
        _owner.Deactivated += HideNativeSymbolSuggestions;
        _nativeSymbolDelay.Tick += (_, _) => { _nativeSymbolDelay.Stop(); if (_typingField is { } field) _ = ShowNativeSymbolsAsync(field); };
    }

    private static TextBox? TextField(DependencyObject? node)
    {
        while (node != null && node is not TextBox) node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as TextBox;
    }

    private async Task ShowNativeSymbolsAsync(TextBox field)
    {
        if (!field.IsKeyboardFocused || field.SelectionLength != 0) return;
        if (_workbook != null && field.Text.StartsWith('=')) return;
        var text = field.Text; var caret = field.CaretIndex; var token = OfficeSymbolSuggestions.TokenAt(text, caret);
        if (token == null) return;
        var request = ++_nativeRequest;
        var choices = await FindSymbolSuggestionsAsync(token.Text);
        if (_closed || request != _nativeRequest || !field.IsKeyboardFocused || field.Text != text || field.CaretIndex != caret || choices.Length == 0) return;
        _nativeChoices = choices; _nativeChoice = -1; _typingField = field;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = new Button { Content = choices[i].Text, ToolTip = choices[i].Hint, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                FontSize = 27, MinWidth = 42, Margin = new(3), Focusable = false, ClickMode = ClickMode.Press };
            button.Click += (_, _) => AcceptNativeSymbol(index, text, caret, token); row.Children.Add(button);
        }
        var frame = new Border { Child = row, Padding = new(5), CornerRadius = new(9), BorderThickness = new(1), Background = (System.Windows.Media.Brush)Resources["ReaderPanel"], BorderBrush = (System.Windows.Media.Brush)Resources["ReaderLine"] };
        frame.Resources.MergedDictionaries.Add(Resources);
        _nativeSymbols.Child = frame; _nativeSymbols.PlacementTarget = field;
        var rect = field.GetRectFromCharacterIndex(Math.Max(0, caret - 1), true);
        _nativeSymbols.CustomPopupPlacementCallback = (size, target, offset) => [
            new(new System.Windows.Point(rect.Right, rect.Top - size.Height - 5), PopupPrimaryAxis.Horizontal),
            new(new System.Windows.Point(rect.Right, rect.Bottom + 5), PopupPrimaryAxis.Horizontal)];
        _nativeSymbols.Tag = (text, caret, token); _nativeSymbols.IsOpen = true;
    }

    private void AcceptNativeSymbol(int index, string text, int caret, OfficeSymbolToken token)
    {
        var field = _typingField;
        if (field == null || field.Text != text || field.CaretIndex != caret || field.SelectionLength != 0) { CloseNativeSymbolSuggestions(); return; }
        var replacement = _nativeChoices[index].Text;
        CloseNativeSymbolSuggestions(); field.Select(token.Start, token.Length); field.SelectedText = replacement; field.Focus();
    }

    private void NativeSymbolKey(object sender, KeyEventArgs e)
    {
        if (!_nativeSymbols.IsOpen) return;
        if (e.Key is Key.Down or Key.Up)
        {
            _nativeChoice = _nativeChoice < 0 ? (e.Key == Key.Down ? 0 : _nativeChoices.Length - 1)
                : (_nativeChoice + (e.Key == Key.Down ? 1 : -1) + _nativeChoices.Length) % _nativeChoices.Length;
            if (_nativeSymbols.Child is Border { Child: StackPanel row })
                for (var i = 0; i < row.Children.Count; i++) ((Button)row.Children[i]).Opacity = i == _nativeChoice ? 1 : .55;
            e.Handled = true; return;
        }
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None || e.Key == Key.Enter && _nativeChoice >= 0)
        {
            var context = ((string Text, int Caret, OfficeSymbolToken Token))_nativeSymbols.Tag;
            AcceptNativeSymbol(Math.Max(0, _nativeChoice), context.Text, context.Caret, context.Token); e.Handled = true; return;
        }
        if (e.Key is Key.Escape or Key.Left or Key.Right or Key.Home or Key.End or Key.Enter or Key.Space
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        { CloseNativeSymbolSuggestions(); if (e.Key == Key.Escape) e.Handled = true; }
    }

    private void CloseNativeSymbolSuggestions()
    { _nativeRequest++; _nativeSymbolDelay.Stop(); _nativeSymbols.IsOpen = false; _nativeChoices = []; }

    private void HideNativeSymbolSuggestions(object? sender, EventArgs e) => CloseNativeSymbolSuggestions();
}
