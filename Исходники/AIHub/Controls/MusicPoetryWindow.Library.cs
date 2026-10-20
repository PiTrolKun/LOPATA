using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private ToggleButton? _libraryToggle;
    private System.Windows.Controls.Button? _libraryExamples;
    private bool _syncingLibrary;
    private void RecordLibraryUse(string turn, LyricExample[] examples)
    {
        if (examples.Length == 0) return;
        _session.LibraryUses.Add(new(turn, DateTimeOffset.UtcNow, examples));
        if (_session.LibraryUses.Count > 200) _session.LibraryUses.RemoveAt(0);
    }
    private void InitializeLyricLibrary(System.Windows.Controls.Panel panel)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/AIHub;component/Controls/MusicPoetryLibraryResources.xaml", UriKind.Relative) });
        _libraryToggle = new ToggleButton { Content = LyricLibraryIcon.Create(), Padding = new(6), Margin = new(3),
            ToolTip = _l("Music.Library.ChatHint") };
        _libraryToggle.SetResourceReference(StyleProperty, "MusicLibraryToggleStyle");
        AutomationProperties.SetAutomationId(_libraryToggle, "Music.Poetry.Library");
        AutomationProperties.SetName(_libraryToggle, _l("Music.Library.Library"));
        _libraryToggle.Click += (_, _) => {
            if (_syncingLibrary || IsWorking) return;
            _session.UsePersonalLyricsLibrary = _libraryToggle.IsChecked == true;
            _session.Persistent = true; Persist(); RefreshLibraryState();
        };
        panel.Children.Add(_libraryToggle);
        _libraryExamples = new System.Windows.Controls.Button { Content = LyricLibraryIcon.CreateExamples(),
            ToolTip = _l("Music.Library.UsedExamples"), Margin = new(3), Padding = new(6), MinWidth = 40 };
        _libraryExamples.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        ToolTipService.SetShowOnDisabled(_libraryExamples, true);
        AutomationProperties.SetAutomationId(_libraryExamples, "Music.Poetry.LibraryExamples");
        AutomationProperties.SetName(_libraryExamples, _l("Music.Library.UsedExamples"));
        _libraryExamples.Click += (_, _) => ShowText(_l("Music.Library.UsedExamples"), string.Join("\n\n",
            _session.LibraryUses.Select(use => use.At.ToLocalTime().ToString("g") + "\n" +
                string.Join("\n\n", use.Examples.Select(e => e.Title + (string.IsNullOrEmpty(e.Artist) ? "" : " · " + e.Artist)
                    + " · " + e.Section + "\n" + e.Text)))), "LibraryExamples");
        panel.Children.Add(_libraryExamples); RefreshLibraryState();
    }
    private void RefreshLibraryState()
    {
        if (_libraryToggle is null) return;
        _syncingLibrary = true; _libraryToggle.IsChecked = _session.UsePersonalLyricsLibrary; _syncingLibrary = false;
        _libraryToggle.Opacity = _session.UsePersonalLyricsLibrary ? 1 : .55;
        _libraryToggle.BorderThickness = new(_session.UsePersonalLyricsLibrary ? 2 : 1);
        _libraryToggle.SetResourceReference(ToggleButton.BorderBrushProperty, _session.UsePersonalLyricsLibrary ? "AccentBrush" : "LineBrush");
        _libraryToggle.IsEnabled = !IsWorking;
        if (_libraryExamples is not null) _libraryExamples.IsEnabled = _session.LibraryUses.Count > 0;
    }
    private async Task PrepareLibraryExamplesAsync(string query, CancellationToken token)
    {
        _session.PendingLibraryExamples = [];
        _session.PendingLibraryArtist = null;
        if (!_session.UsePersonalLyricsLibrary) return;
        _status.Text = _l("Music.Library.FindingExamples");
        var artist = LyricArtistSelection.Resolve(LyricLibraryStore.Default.Snapshot().Entries, query);
        _session.PendingLibraryArtist = artist?.Status;
        var root = _storage.Models.Locations.FirstOrDefault()?.Path ?? AppDataPaths.ComponentModelsDirectory;
        _session.PendingLibraryExamples = await new LyricLibrarySearch(LyricLibraryStore.Default, root, sharedOperation: true)
            .SearchAsync(LyricExampleContext.Query(query), new Progress<RagBookProgress>(p =>
                _status.Text = _l("Music.Library.FindingExamples") + $" {p.Done}/{p.Total}"), token, artist);
    }
}
