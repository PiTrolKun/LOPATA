using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

internal sealed class LetItOutConversationSidebar : UserControl
{
    private readonly StackPanel _items = new();
    internal event Action? NewRequested;
    internal event Action<string>? OpenRequested;
    internal event Action<string, bool>? FavoriteRequested;
    internal event Action<string>? RenameRequested;
    internal event Action<string>? DeleteRequested;
    internal event Action<string>? ExportRequested;

    internal LetItOutConversationSidebar()
    {
        AutomationProperties.SetAutomationId(this, "LetItOut.Library");
        var border = new Border { Padding = new(12), CornerRadius = new(14) };
        border.SetResourceReference(Border.BackgroundProperty, "HeaderBackgroundBrush");
        border.Child = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = border;
    }

    internal void Update(IReadOnlyList<LetItOutConversationSummary> topics, string current, Func<string, string> localize)
    {
        _items.Children.Clear();
        var create = new Button { Content = "+  " + localize("LetItOut.Library.New"), Padding = new(12, 10, 12, 10),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 0, 0, 18) };
        create.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        AutomationProperties.SetAutomationId(create, "LetItOut.Library.New");
        create.Click += (_, _) => NewRequested?.Invoke(); _items.Children.Add(create);
        Group("LetItOut.Library.Favorites", topics.Where(topic => topic.Favorite));
        Group("LetItOut.Library.Recent", topics.Where(topic => !topic.Favorite));

        void Group(string key, IEnumerable<LetItOutConversationSummary> source)
        {
            var caption = new TextBlock { Text = localize(key), FontSize = 13, Margin = new(8, 8, 0, 8) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _items.Children.Add(caption);
            var entries = source.ToArray();
            if (entries.Length == 0)
            {
                var empty = new TextBlock { Text = localize("LetItOut.Library.Empty"), FontSize = 12, Margin = new(10, 3, 0, 16) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); _items.Children.Add(empty);
            }
            foreach (var topic in entries)
            {
                var title = string.IsNullOrWhiteSpace(topic.Title) ? localize("LetItOut.Library.Untitled") : topic.Title;
                var text = new TextBlock { Text = (topic.Favorite ? "★  " : "◌  ") + title,
                    TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 15 };
                var row = new Button { Content = text, Tag = topic.Id, ToolTip = title, Padding = new(10, 10, 10, 10),
                    BorderThickness = new(0), Margin = new(0, 2, 0, 2),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
                row.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
                row.Template = LetItOutLibraryStyles.Row; row.FontWeight = FontWeights.Normal;
                row.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                if (topic.Id == current) row.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
                else row.Background = Brushes.Transparent;
                AutomationProperties.SetAutomationId(row, "LetItOut.Topic." + topic.Id);
                AutomationProperties.SetName(row, title);
                row.Click += (_, _) => OpenRequested?.Invoke(topic.Id);
                var menu = new ContextMenu { Template = LetItOutLibraryStyles.Menu };
                menu.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
                menu.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                Add(topic.Favorite ? "LetItOut.Library.Unfavorite" : "LetItOut.Library.Favorite",
                    () => FavoriteRequested?.Invoke(topic.Id, !topic.Favorite));
                Add("LetItOut.Library.Rename", () => RenameRequested?.Invoke(topic.Id));
                Add("LetItOut.Library.Export", () => ExportRequested?.Invoke(topic.Id));
                menu.Items.Add(new Separator { Template = LetItOutLibraryStyles.Separator });
                Add("LetItOut.Library.Delete", () => DeleteRequested?.Invoke(topic.Id));
                row.ContextMenu = menu; _items.Children.Add(row);
                void Add(string action, Action invoke)
                {
                    var item = new MenuItem { Header = localize(action), Template = LetItOutLibraryStyles.Item };
                    AutomationProperties.SetAutomationId(item, action);
                    item.Click += (_, _) => invoke(); menu.Items.Add(item);
                }
            }
        }
    }
}
