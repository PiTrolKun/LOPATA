using System.Windows;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private void BuildLayout(ContentControl editor, UIElement memoryStatus, UIElement navigation)
    {
        var root = new Grid(); root.ColumnDefinitions.Add(new() { Width = new GridLength(41, GridUnitType.Star), MinWidth = 300 });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(10) }); root.ColumnDefinitions.Add(new() { Width = new GridLength(59, GridUnitType.Star), MinWidth = 420 });
        root.RowDefinitions.Add(new() { Height = new GridLength(1,GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root; Grid.SetRow(editor,0); root.Children.Add(editor);
        Grid.SetRow(navigation,1); root.Children.Add(navigation);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(BackgroundProperty, "LineBrush"); Grid.SetColumn(splitter,1); root.Children.Add(splitter);
        var right = new Grid(); Grid.SetColumn(right,2); Grid.SetRowSpan(right,2); root.Children.Add(right);
        right.RowDefinitions.Add(new() { Height = new GridLength(6, GridUnitType.Star), MinHeight = 130 });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new GridLength(4, GridUnitType.Star), MinHeight = 140 });
        // Growing input consumes response space above it; the controls below stay anchored.
        right.SizeChanged += (_,_) =>
        {
            // Match WPF's device-pixel layout grid. A half-pixel fixed row beside
            // Auto/Star rows can keep rounding in opposite directions on each pass.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(right).DpiScaleY;
            var height = Math.Round(Math.Max(140,right.ActualHeight*.5)*dpi)/dpi;
            if (right.RowDefinitions[2].Height.Value != height)
                right.RowDefinitions[2].Height = new GridLength(height);
        };
        var conversation = new DockPanel(); var heading = new WrapPanel();
        _role.VerticalAlignment = VerticalAlignment.Center; _role.Margin = new Thickness(10,0,15,5);
        _role.FontWeight = FontWeights.SemiBold; _role.TextWrapping = TextWrapping.NoWrap;
        _role.Background = System.Windows.Media.Brushes.Transparent; heading.Children.Add(_role);
        foreach (var button in new[] { _archive, _clear, _stop }) { Compact(button); heading.Children.Add(button); }
        _archive.Content = _l("Studio.Archive"); _clear.Content = _l("Paragraph.Clear"); _stop.Content = _l("Paragraph.Stop");
        DockPanel.SetDock(heading, Dock.Top); conversation.Children.Add(heading);
        _scroll.Content = _messages; conversation.Children.Add(_scroll); right.Children.Add(LiteraryWorkspaceParts.Card(conversation));

        var compose = new StackPanel { Margin = new Thickness(0,8,0,8) };
        compose.Children.Add(_quotes);
        var inputHeading = new DockPanel();
        _activity.Margin = new Thickness(0,0,5,0); DockPanel.SetDock(_activity,Dock.Right); inputHeading.Children.Add(_activity);
        _inputLabel.TextWrapping = TextWrapping.NoWrap; _inputLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        inputHeading.Children.Add(_inputLabel); compose.Children.Add(inputHeading);
        var row = new DockPanel(); var sends = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var button in new[] { _send, _sendWriter })
        {
            Compact(button,true); button.Width = 38; button.Padding = new Thickness(6);
            button.Content = button == _send ? "➤" : "✎"; button.FontSize = 18;
            button.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol");
            System.Windows.Automation.AutomationProperties.SetName(button,_l(button == _send ? "Studio.SendAdvisor" : "Studio.SendWriter"));
            sends.Children.Add(button);
        }
        // Local accent resources preserve the common button template and hover behaviour.
        _sendWriter.Resources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200,45,58));
        _sendWriter.Resources["AccentDarkBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(166,30,42));
        ToolTipService.SetShowOnDisabled(_sendWriter,true);
        DockPanel.SetDock(sends,Dock.Right); row.Children.Add(sends);
        _input.MinHeight = 0; _input.Height = double.NaN; _input.MinLines = 1; _input.MaxLines = 6;
        _input.FontSize = 16; _input.Padding = new Thickness(9); _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        LiterarySpellChecking.Enable(_input,_language); row.Children.Add(_input); compose.Children.Add(row);
        Grid.SetRow(compose,1); right.Children.Add(compose);

        var lower = new Grid(); Grid.SetRow(lower,2); right.Children.Add(lower);
        lower.ColumnDefinitions.Add(new() { Width = new GridLength(.8,GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new() { Width = new GridLength(1.25,GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
        lower.Children.Add(LiteraryWorkspaceParts.Card(new ScrollViewer { Content = _actions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }));
        var sourcePanel = new DockPanel(); var refresh = ActionButton(_l("Studio.Sources"), () => RunUi(RefreshSources));
        refresh.ToolTip = _l("Paragraph.Refresh"); DockPanel.SetDock(refresh,Dock.Top); sourcePanel.Children.Add(refresh);
        var sourceScroll = new ScrollViewer { Content = _sources, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        sourcePanel.Children.Add(sourceScroll); var sourceCard = LiteraryWorkspaceParts.Card(sourcePanel); sourceCard.Margin = new Thickness(5,0,5,0);
        Grid.SetColumn(sourceCard,1); lower.Children.Add(sourceCard);
        var progressCard = BuildRouteCard();
        Grid.SetColumn(progressCard,2); lower.Children.Add(progressCard);
        StatusContent = BuildStatus(memoryStatus);
    }
    private void BuildActions()
    {
        foreach (var button in _actions.Children.OfType<Button>()) _buttons.Remove(button);
        _actions.Children.Clear(); _actions.RowDefinitions.Clear(); _actions.ColumnDefinitions.Clear();
        for (var column=0; column<2; column++) _actions.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
        var title = LiteraryUi.Text(_l("Studio.Actions"),true); Grid.SetColumnSpan(title,2); _actions.Children.Add(title);
        var actions = State.Role == LiteraryChatProfile.Advisor ? LiteraryStudioPrompts.Advisor : LiteraryStudioPrompts.Writer;
        var actionRows = (actions.Count+1)/2;
        for (var row=0; row<actionRows+2; row++) _actions.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var index = 0;
        foreach (var action in actions)
        {
            var button = ActionButton(_l("Studio.Action." + action.Id), async () =>
            {
                State.Action = action.Id; State.WriterComment = false; BuildActions(); RenderMessages(); Availability(); _dirty = true;
                if (action.RequiresInput) { _input.Focus(); StartHint(); }
                else await SendAsync();
            }, State.Action == action.Id);
            button.Content = new TextBlock { Text = _l("Studio.Action."+action.Id), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button,_l("Studio.Action."+action.Id));
            button.Height = double.NaN; button.MinHeight = 40; button.Padding = new Thickness(6);
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Margin = new Thickness(index%2==0 ? 0 : 3,0,index%2==0 ? 3 : 0,5);
            Grid.SetRow(button,1+index/2); Grid.SetColumn(button,index%2); index++;
            button.ToolTip = _l("Studio.Hint." + action.Id);
            if (action.Id == "Continue")
            {
                var menu = new ContextMenu();
                foreach (var chat in new[] { false, true })
                {
                    var item = new MenuItem { Header = _l(chat ? "Studio.FromChat" : "Studio.FromEditor"), IsCheckable = true, IsChecked = State.ContinueFromChat == chat };
                    item.Click += (_,_) => { State.ContinueFromChat = chat; _dirty = true; BuildActions(); RenderMessages(); Availability(); };
                    menu.Items.Add(item);
                }
                button.ContextMenu = menu;
            }
            _actions.Children.Add(button);
        }
        var fullWidth = State.Role == LiteraryChatProfile.Advisor
            ? ActionButton(_l("Studio.FreeChat"),OpenFreeChat)
            : ActionButton(_l("Studio.Comment"), () => { State.WriterComment = true; _dirty = true; Availability(); _input.Focus(); StartHint(); });
        fullWidth.Margin = new Thickness(0,0,0,5);
        Grid.SetRow(fullWidth,actionRows+1); Grid.SetColumnSpan(fullWidth,2); _actions.Children.Add(fullWidth);
    }
    public void OpenTool(string tool)
    {
        if (IsWorking || _blocked()) return;
        if (tool == "Prompts") EditPrompts();
        else if (tool == "Anchor") RunUi(() => LiteraryPlotAnchorDialog.Open(this, _l,
            new LiteraryPlotAnchorStore(new(_directory), State.Role), State.Role));
    }
    private void StartHint() => _input.BeginAnimation(OpacityProperty, new DoubleAnimation(1,.45,TimeSpan.FromMilliseconds(600))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) });
    private void StopHint() => _input.BeginAnimation(OpacityProperty,null);
    private void RunUi(Action action)
    { try { action(); } catch (Exception ex) { _status.Text = _l("Paragraph.Failure") + " " + ex.Message; } }
    private void OpenFreeChat()
    {
        if (_freeChat is not null) { _freeChat.Activate(); return; }
        _freeChat = new(_runtime,_l,_language) { Owner = Window.GetWindow(this) };
        _freeChat.Closed += (_,_) => _freeChat = null; _freeChat.Show();
    }
}
