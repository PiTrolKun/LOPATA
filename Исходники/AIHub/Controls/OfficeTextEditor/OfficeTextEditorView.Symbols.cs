using System.Windows;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private OfficeSymbolsWindow? _symbolsWindow;

    private void OpenSymbols()
    {
        if (_symbolsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate(); return;
        }
        var window = new OfficeSymbolsWindow(_owner, _l, Resources, Background, Foreground);
        _symbolsWindow = window;
        window.LibraryReady += () => { _symbolSuggestionsPermitted = true; _symbolSuggestionFailed = false; };
        window.Closed += (_, _) => _symbolsWindow = null;
        window.Show();
    }
}
