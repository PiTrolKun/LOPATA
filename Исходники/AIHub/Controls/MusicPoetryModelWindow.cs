using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Songwriting selection with visible provenance and separate user/technical assessments.</summary>
public sealed partial class MusicPoetryModelWindow : Window
{
    private readonly Dictionary<string, string> _dirtyComments = [];
    private readonly DispatcherTimer _saveComments = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Func<bool> _flushComments;
    public bool SaveForExit() { _downloadCancel?.Cancel(); return _flushComments(); }
    public MusicPoetryModelWindow(IReadOnlyList<MusicPoetryModelChoice> choices, string? currentPath,
        Func<string, string> localize, Action<DebugModelInfo> select, MusicPoetryPreferences? preferences = null,
        Func<MusicPoetryModelCandidate, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<DebugModelInfo>>? download = null,
        Func<MusicPoetryModelCandidate, long>? remove = null, Func<MusicPoetryModelCandidate, bool>? hasFiles = null)
    {
        string L(string key) => localize("Music.Poetry.Selection." + key);
        _download = download; _downloadLocalize = L;
        _remove = remove; _hasFiles = hasFiles;
        _selectedPath = currentPath;
        _localizeDownloadError = message => message.StartsWith("Music.", StringComparison.Ordinal) ? localize(message) : message;
        Title = L("Title"); Width = 850; Height = 710; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(this, "Music.Poetry.ModelSelection");
        var root = new DockPanel { Margin = new(16) }; Content = root;
        var header = Text(L("Hint")); header.Margin = new(0, 0, 0, 12);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var downloadFooter = new StackPanel(); DockPanel.SetDock(downloadFooter, Dock.Bottom); root.Children.Add(downloadFooter);
        _downloadStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _downloadStatus.TextWrapping = TextWrapping.Wrap;
        downloadFooter.Children.Add(_downloadStatus); downloadFooter.Children.Add(_downloadProgress);
        var close = MakeButton(L("Close"), "Close"); close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var status = Text(""); status.Margin = new(0, 4, 12, 0); footer.Children.Add(status);
        var store = preferences ?? MusicPoetryPreferences.Default;
        MusicPoetryPreferenceState state;
        var readable = true;
        try { state = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { state = new(); readable = false; status.Text = L("PreferencesError") + " " + ex.Message; }
        var favorite = state.FavoriteId;
        var installed = choices.Where(choice => choice.Model is not null).Select(choice => choice.Candidate.Id).ToHashSet();
        var stars = new Dictionary<string, Button>();
        var expanders = new Dictionary<string, Expander>();
        var cards = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        foreach (var choice in choices.Where(choice => MusicPoetryModelCatalog.All.Contains(choice.Candidate)
            && (!choice.Candidate.InstalledOnly || choice.Model is not null)))
        {
            var candidate = choice.Candidate;
            var available = choice.Model;
            var current = choice.Model is not null && choice.Model.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase);
            var body = new StackPanel();
            var name = Text(candidate.DisplayName ?? (candidate.Id == "executor" ? "sm54/Qwen3.6-27B-Q4_K_M-GGUF" : candidate.FileName)
                + (candidate.Id == "core" ? " · " + L("Core") : ""));
            name.FontWeight = FontWeights.SemiBold; name.FontSize = 18; name.Margin = new(0, 0, 52, 0);
            var cardHeader = new StackPanel(); cardHeader.Children.Add(name);
            if (candidate.SpecializationKey is { } specialization)
            {
                var label = Text(localize("Music.Poetry.Specialization." + specialization), true);
                label.Margin = new(0, 0, 52, 0);
                AutomationProperties.SetAutomationId(label, "Music.Poetry.Specialization." + candidate.Id);
                cardHeader.Children.Add(label);
            }
            if (candidate.Id == "core") body.Children.Add(Text(L("Purpose.Core"), true));
            body.Children.Add(Text(candidate.DownloadBytes > 0 ? localize("Music.Poetry.Profile." + candidate.ReviewKey) : L("Review." + candidate.ReviewKey)));
            if (candidate.DownloadBytes > 0) body.Children.Add(Text(string.Format(L("DownloadSize"),
                FormatDownloadSize(candidate.DownloadBytes)), true));
            var unavailable = Text(L(candidate.DownloadBytes > 0 ? "ProfileUnavailable" : "Unavailable"), true);
            unavailable.Visibility = choice.Model is null ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(unavailable);
            body.Children.Add(Text(L("Comment"), true));
            var comment = new System.Windows.Controls.TextBox { Text = state.Comments.GetValueOrDefault(candidate.Id, ""),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 8), IsReadOnly = !readable };
            comment.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            comment.SetResourceReference(BackgroundProperty, "InputBrush");
            comment.SetResourceReference(System.Windows.Controls.TextBox.CaretBrushProperty, "TextPrimaryBrush");
            comment.SetResourceReference(System.Windows.Controls.TextBox.BorderBrushProperty, "LineBrush");
            AutomationProperties.SetAutomationId(comment, "Music.Poetry.ModelComment." + candidate.Id);
            AutomationProperties.SetName(comment, L("Comment") + ": " + candidate.FileName);
            comment.TextChanged += (_, _) => { _dirtyComments[candidate.Id] = comment.Text; _saveComments.Stop(); _saveComments.Start(); };
            body.Children.Add(comment);
            var button = MakeButton(L(current ? "Current" : "Select"), candidate.Id);
            button.IsEnabled = choice.Model is not null && !current;
            button.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            button.Click += (_, _) => { if (available is not null && !IsDownloading) { select(available); RefreshSelection(available.Path); } };
            _modelActions.Add(button);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Button? downloadButton = null;
            if (candidate.DownloadBytes > 0 && download is not null)
            {
                downloadButton = MakeButton(L("Download"), "Download." + candidate.Id);
                downloadButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                downloadButton.Visibility = available is null ? Visibility.Visible : Visibility.Collapsed;
                downloadButton.Click += async (_, _) => await DownloadAsync(candidate, downloadButton, model => {
                    available = model; installed.Add(candidate.Id); unavailable.Visibility = Visibility.Collapsed;
                    button.Content = L("Select"); button.IsEnabled = true; downloadButton.Visibility = Visibility.Collapsed;
                    RefreshStars(); RefreshRemoveButtons(); RefreshSelection(_selectedPath);
                });
                _downloadButtons.Add(downloadButton); actions.Children.Add(downloadButton);
            }
            if (MusicPoetryProfileCatalog.Find(candidate.Id) is not null && remove is not null)
            {
                var removeButton = MakeButton("🗑", "Remove." + candidate.Id);
                removeButton.ToolTip = localize("Music.Models.Remove.Action");
                AutomationProperties.SetName(removeButton, (string)removeButton.ToolTip);
                removeButton.Margin = new(8, 4, 8, 0); _modelActions.Add(removeButton);
                removeButton.Click += (_, _) => RemoveProfile(candidate, localize, () => {
                    if (available?.Path.Equals(_selectedPath, StringComparison.OrdinalIgnoreCase) == true) _selectedPath = null;
                    available = null; installed.Remove(candidate.Id); unavailable.Visibility = Visibility.Visible;
                    button.Content = L("Select"); button.IsEnabled = false;
                    if (downloadButton is not null) downloadButton.Visibility = Visibility.Visible;
                    if (favorite == candidate.Id) { store.SetFavorite(null); favorite = null; }
                    RefreshStars(); RefreshSelection(_selectedPath);
                });
                _removeButtons.Add((candidate, removeButton)); actions.Children.Add(removeButton);
            }
            actions.Children.Add(button); body.Children.Add(actions);
            var expander = new Expander { Header = cardHeader, Content = body, IsExpanded = favorite == candidate.Id,
                Tag = candidate.SpecializationKey };
            expander.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(expander, "Music.Poetry.ModelCard." + candidate.Id);
            expanders[candidate.Id] = expander;
            var card = new Grid(); card.Children.Add(expander);
            var star = MakeButton("☆", "Favorite." + candidate.Id);
            star.Width = star.Height = 36; star.Padding = new(0); star.FontSize = 24;
            star.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; star.VerticalAlignment = VerticalAlignment.Top;
            star.Margin = new(0, 16, 16, 0); star.IsEnabled = readable && (choice.Model is not null || favorite == candidate.Id);
            star.Click += (_, _) => {
                var next = favorite == candidate.Id ? null : candidate.Id;
                try {
                    store.SetFavorite(next); favorite = next; status.Text = "";
                    foreach (var (id, section) in expanders) section.IsExpanded = id == favorite;
                    RefreshStars();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { status.Text = L("PreferencesError") + " " + ex.Message; }
            };
            stars[candidate.Id] = star; card.Children.Add(star);
            var frame = new Border { Child = card, CornerRadius = new(12), Margin = new(0, 0, 0, 12), BorderThickness = new(2) };
            AutomationProperties.SetAutomationId(frame, "Music.Poetry.SelectedCard." + candidate.Id);
            _selectionCards.Add((frame, name, button, expander, () => available)); cards.Children.Add(frame);
        }
        RefreshStars(); RefreshRemoveButtons(); RefreshSelection(currentPath);
        _flushComments = SaveComments;
        _saveComments.Tick += (_, _) => SaveComments();
        Closing += (_, e) => {
            if (!SaveComments()) { e.Cancel = true; return; }
            if (IsDownloading) { _closeAfterDownload = true; _downloadCancel?.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) => _saveComments.Stop();
        bool SaveComments()
        {
            _saveComments.Stop();
            if (_dirtyComments.Count == 0) return true;
            try { store.SetComments(_dirtyComments); _dirtyComments.Clear(); status.Text = ""; return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { status.Text = L("PreferencesError") + " " + ex.Message; return false; }
        }
        void RefreshStars()
        {
            foreach (var (id, star) in stars)
            {
                star.Content = favorite == id ? "★" : "☆";
                star.IsEnabled = readable && (installed.Contains(id) || favorite == id);
                star.ToolTip = favorite == id ? L("Unfavorite") : L("Favorite");
                AutomationProperties.SetName(star, (string)star.ToolTip);
                star.SetResourceReference(ForegroundProperty, favorite == id ? "AccentBrush" : "TextSecondaryBrush");
            }
        }
        TextBlock Text(string text, bool secondary = false)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
            label.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
            return label;
        }
        Button MakeButton(string label, string id)
        {
            var button = new Button { Content = label, Padding = new(14, 8, 14, 8), Margin = new(0, 4, 0, 0), MinHeight = 36 };
            button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
            AutomationProperties.SetAutomationId(button, "Music.Poetry.SelectModel." + id);
            AutomationProperties.SetName(button, label); return button;
        }
    }
}
