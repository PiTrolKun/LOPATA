using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services.MusicLibrary;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

/// <summary>Optional, nonmodal offer. It never saves or enables retrieval by itself.</summary>
public sealed class LyricLibraryOffer : StackPanel
{
    private readonly Button _save, _later;
    private readonly DispatcherTimer _timer;
    private Func<string, string> _l = key => key;
    private string _text = "", _dismissed = "";
    public Func<LyricEntry>? Draft { get; set; }
    public LyricLibraryOffer()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal; Visibility = Visibility.Collapsed;
        _save = MusicWishUi.Button("", "Music.Library.Offer.Save", Save);
        _later = MusicWishUi.Button("×", "Music.Library.Offer.Later", () => { _dismissed = _text; Visibility = Visibility.Collapsed; });
        _save.FontSize = _later.FontSize = 12; _save.Padding = new(6, 3, 6, 3); _later.Padding = new(6, 3, 6, 3);
        Children.Add(_save); Children.Add(_later);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop(); Loaded += (_, _) => SetText(_text);
    }
    public void Localize(Func<string, string> l) { _l = l; _save.Content = l("Music.Library.OfferSave"); _later.ToolTip = l("Music.Library.OfferLater"); }
    public void SetText(string text) { _text = text; _timer.Stop(); if (IsLoaded) _timer.Start(); }
    private void Refresh()
    {
        if (Draft is null || string.IsNullOrWhiteSpace(_text) || _text == _dismissed) { Visibility = Visibility.Collapsed; return; }
        try { Visibility = LyricLibraryStore.Default.ContainsText(_text) ? Visibility.Collapsed : Visibility.Visible; }
        catch { Visibility = Visibility.Visible; } // A failed optional lookup must not interrupt editing.
    }
    private void Save()
    {
        if (Draft is null) return;
        try { new LyricLibraryWindow(Window.GetWindow(this), _l, allowImport: false, draft: Draft() with { Text = _text }).ShowDialog(); Refresh(); }
        catch (Exception e) { System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Music.Library.Failed") + " " + e.Message, _l("Music.Library.Library")); }
    }
}
