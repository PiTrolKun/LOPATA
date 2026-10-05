using System.Windows;
using System.Windows.Controls;
using AIHub.Models;

namespace AIHub.Controls;

public sealed class MusicPerformersWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly List<MusicPerformer> _draft;
    private readonly StackPanel _list = new();
    public MusicPerformer[]? AcceptedPerformers { get; private set; }
    public MusicPerformersWindow(Func<string, string> l, IEnumerable<MusicPerformer> current)
    {
        _l = l; _draft = current.Select(x => x.Copy()).ToList();
        MusicWishUi.PrepareWindow(this, L("performers"), "Music.Performers");
        var dock = new DockPanel { Margin = new(18) }; var header = new StackPanel();
        header.Children.Add(MusicWishUi.Text(Title, true)); header.Children.Add(MusicWishUi.Text(L("PerformerHint")));
        header.Children.Add(MusicWishUi.Button(L("NewPerformer"), "Music.Performer.New", () => Edit(null)));
        DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        var footer = MusicWishUi.Footer(l, () => { AcceptedPerformers = _draft.Select(x => x.Copy()).ToArray(); DialogResult = true; }, () => DialogResult = false);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock; Render();
    }
    private string L(string key) => _l("Music.Wishes." + key);
    private void Edit(MusicPerformer? current)
    {
        var window = new MusicPerformerEditorWindow(_l, _draft, current) { Owner = this };
        if (window.ShowDialog() != true || window.Result is not { } updated) return;
        var index = _draft.FindIndex(x => x.Id == updated.Id);
        if (index < 0) _draft.Add(updated); else _draft[index] = updated; Render();
    }
    private void Render()
    {
        _list.Children.Clear();
        foreach (var performer in _draft)
        {
            var card = new StackPanel { Margin = new(8) };
            card.Children.Add(MusicWishUi.Text(performer.Name, true));
            var description = new List<string>();
            foreach (var (category, id) in new[] { ("voice", performer.Voice), ("range", performer.Range), ("language", performer.Language) })
                if (Services.MusicWishCatalog.Group(category).FirstOrDefault(x => x.Id == id) is { } choice) description.Add(_l(choice.NameKey));
            card.Children.Add(MusicWishUi.Text(string.Join(" · ", description)));
            var buttons = new WrapPanel(); buttons.Children.Add(MusicWishUi.Button(L("Edit"), "Music.Performer.Edit." + performer.Id, () => Edit(performer)));
            buttons.Children.Add(MusicWishUi.Button(L("Remove"), "Music.Performer.Remove." + performer.Id, () => { _draft.Remove(performer); Render(); })); card.Children.Add(buttons);
            var frame = new Border { Child = card, BorderThickness = new(1), CornerRadius = new(8), Margin = new(0, 3, 0, 6) };
            frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); _list.Children.Add(frame);
        }
        if (_draft.Count == 0) _list.Children.Add(MusicWishUi.Text(L("Empty")));
    }
}
