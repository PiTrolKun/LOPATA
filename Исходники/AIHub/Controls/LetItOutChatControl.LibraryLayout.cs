using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private readonly LetItOutConversationSidebar _sidebar = new();
    private readonly LetItOutTopicTreePanel _tree = new();
    private readonly Popup _libraryPopup = new() { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom };
    private readonly TextBlock _topicCaption = Label("", 15);
    private Button _topicsButton = null!;
    private Action? _relayoutLibrary;

    private void BuildLibraryLayout(Grid surface, Grid chat, WrapPanel actions, StackPanel heading)
    {
        surface.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        surface.ColumnDefinitions.Add(new());
        surface.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        surface.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(chat, 1); Grid.SetColumn(_stop, 3);
        chat.Margin = new(12, 0, 12, 0);
        _sidebar.Margin = new(0, 0, 12, 0); surface.Children.Add(_sidebar);
        _tree.Margin = new(0, 0, 0, 70); Grid.SetColumn(_tree, 3); surface.Children.Add(_tree);
        _treeToggle=MakeButton("LetItOut.Tree.Toggle",ToggleTree);_treeToggle.Width=28;_treeToggle.MinWidth=0;
        _treeToggle.Padding=new(0);_treeToggle.Height=60;_treeToggle.Margin=new(0);
        _treeToggle.VerticalAlignment=System.Windows.VerticalAlignment.Center;
        Grid.SetColumn(_treeToggle,2);surface.Children.Add(_treeToggle);
        InitializeTree();
        _topicsButton = MakeButton("LetItOut.Library.Topics", () => _libraryPopup.IsOpen = !_libraryPopup.IsOpen);
        _topicsButton.Content = "☰"; _topicsButton.MinWidth = 36; _topicsButton.Width = 36;
        _topicsButton.Padding = new(2); actions.Children.Insert(0, _topicsButton);
        _libraryPopup.PlacementTarget = _topicsButton; surface.Children.Add(_libraryPopup);
        heading.Children.Insert(1, _topicCaption); _topicCaption.Visibility = Visibility.Collapsed;
        _sidebar.NewRequested += () => { _libraryPopup.IsOpen = false; NewConversation(); };
        _sidebar.OpenRequested += id => { if (OpenConversation(id)) _libraryPopup.IsOpen = false; };
        _sidebar.FavoriteRequested += FavoriteConversation;
        _sidebar.RenameRequested += RenameConversation;
        _sidebar.DeleteRequested += DeleteConversation;
        _sidebar.ExportRequested += id => _ = ExportConversationAsync(id);
        surface.SizeChanged += (_, _) => Layout();
        _relayoutLibrary = Layout;
        void Layout()
        {
            var width = surface.ActualWidth;
            var docked = _library is not null && width >= 1050;
            var tree = _library is not null && _treeEnabled && width >= 1150;
            _topicsButton.Visibility = _library is not null && !docked ? Visibility.Visible : Visibility.Collapsed;
            if (docked)
            {
                _libraryPopup.IsOpen = false;
                if (_libraryPopup.Child is not null) _libraryPopup.Child = null;
                if (!surface.Children.Contains(_sidebar)) surface.Children.Add(_sidebar);
                _sidebar.Width = tree ? 250 : 220; _sidebar.Height = double.NaN;
                _sidebar.Visibility = Visibility.Visible;
            }
            else
            {
                surface.Children.Remove(_sidebar);
                if (_library is not null)
                {
                    _sidebar.Width = Math.Min(310, Math.Max(220, width - 36));
                    _sidebar.Height = Math.Max(220, Math.Min(600, surface.ActualHeight - 60));
                    _sidebar.Visibility = Visibility.Visible; _libraryPopup.Child = _sidebar;
                }
            }
            surface.ColumnDefinitions[0].Width = new(docked ? _sidebar.Width : 0);
            surface.ColumnDefinitions[2].Width = new(tree ? 30 : 52);
            surface.ColumnDefinitions[3].Width = new(tree ? 250 : 0);
            _treeToggle.Visibility=_library is null?Visibility.Collapsed:Visibility.Visible;
            chat.MaxWidth=tree?1060:double.PositiveInfinity;
            Grid.SetColumn(_stop,tree?3:2);
            _tree.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;
            _stop.MinWidth = tree ? 80 : 44; _stop.Width = tree ? double.NaN : 44;
            _stop.Height = tree ? double.NaN : 44; _stop.Padding = tree ? new(14, 7, 14, 7) : new(0);
            _stop.Content = tree ? _l("LetItOut.Stop") : "■";
            _stop.ToolTip = _l("LetItOut.Stop"); AutomationProperties.SetName(_stop, _l("LetItOut.Stop"));
        }
    }
}
