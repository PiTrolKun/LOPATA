using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Models;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class LiteraryProjectStartControl : System.Windows.Controls.UserControl
{
    public LiteraryProjectStartControl(LiteraryProjectEntry? active, Func<string,string> l, Action create,
        Action? resume, Action select, Action selectActive, Action export, string notice = "")
    {
        MaxWidth = 1160; HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        var root = new StackPanel(); Content = root;
        var hero = new StackPanel();
        var tag = LiteraryUi.Text(l("Literary.Active")); tag.Margin = new Thickness(0,0,0,2); hero.Children.Add(tag);
        var title = LiteraryUi.Text(active?.Title ?? l("Literary.Start.NoActive"),true);
        hero.Children.Add(title);
        hero.Children.Add(LiteraryUi.Text(l(active is null ? "Literary.Start.NoActiveHint" : "Literary.Start.ResumeHint")));
        if (active is not null)
        {
            var path = LiteraryUi.Text(active.ProjectPath); path.FontSize = 13; path.TextWrapping = TextWrapping.NoWrap;
            path.TextTrimming = TextTrimming.CharacterEllipsis; path.ToolTip = active.ProjectPath; hero.Children.Add(path);
        }
        var continueButton = CompactButton(l("Literary.Continue"),resume,true);
        System.Windows.Automation.AutomationProperties.SetAutomationId(continueButton,"ProjectStart.Continue"); hero.Children.Add(continueButton);
        var heroCard = Card(hero,true); heroCard.Margin = new Thickness(0,0,0,8); root.Children.Add(heroCard);
        var cards = new UniformGrid { Columns = 2 };
        cards.Children.Add(ActionCard("＋","Literary.Start.NewTitle","Literary.Start.NewHint","Literary.New",create,"New",active is null));
        cards.Children.Add(ActionCard("▤","Literary.Start.LibraryTitle","Literary.Start.LibraryHint","Literary.Select",select,"Select"));
        cards.Children.Add(ActionCard("★","Literary.Start.ActiveTitle","Literary.Start.ActiveHint","Literary.SelectActive",selectActive,"Active"));
        cards.Children.Add(ActionCard("↗","Literary.Start.ExportTitle","Literary.Start.ExportHint","Literary.Export",export,"Export"));
        root.Children.Add(cards);
        SizeChanged += (_,_) => { var columns = ActualWidth < 740 ? 1 : 2; if (cards.Columns != columns) cards.Columns = columns; };
        if (notice.Length > 0) root.Children.Add(LiteraryUi.Text(notice));

        Border ActionCard(string icon,string titleKey,string hintKey,string buttonKey,Action action,string id,bool primary = false)
        {
            var panel = new DockPanel();
            var button = CompactButton(l(buttonKey),action,primary);
            System.Windows.Automation.AutomationProperties.SetAutomationId(button,"ProjectStart." + id);
            DockPanel.SetDock(button,Dock.Bottom); panel.Children.Add(button);
            var text = new StackPanel(); var heading = new StackPanel { Orientation = Orientation.Horizontal };
            var symbol = LiteraryUi.Text(icon,true); symbol.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");
            symbol.Width = 36; symbol.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"); heading.Children.Add(symbol);
            heading.Children.Add(LiteraryUi.Text(l(titleKey),true)); text.Children.Add(heading);
            text.Children.Add(LiteraryUi.Text(l(hintKey))); panel.Children.Add(text);
            var card = Card(panel); card.Margin = new Thickness(0,0,8,8); return card;
        }
    }

    private static System.Windows.Controls.Button CompactButton(string text, Action? action, bool primary)
    {
        var button = LiteraryUi.Button(text,action,primary);
        // Size to the scaled text instead of reserving the main navigation button height.
        button.Height = double.NaN; button.MinHeight = 32;
        button.Padding = new Thickness(12,6,12,6);
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        button.Margin = new Thickness(0,8,0,0);
        return button;
    }

    private static Border Card(UIElement content,bool accent = false)
    {
        var card = new Border { Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty,"PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty,accent ? "AccentBrush" : "LineBrush"); return card;
    }
}
