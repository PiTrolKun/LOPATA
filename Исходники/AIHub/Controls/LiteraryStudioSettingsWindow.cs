using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed class LiteraryStudioSettingsWindow : Window
{
    public LiteraryStudioSettingsWindow(Window owner, LiteraryStudioState state, Func<string,string> l, Func<bool> persist)
    {
        Owner = owner; Resources = owner.Resources; Title = l("Studio.TransferSettings");
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        var root = new StackPanel { Margin = new Thickness(20) }; Content = root;
        var mode = new CheckBox { IsChecked = state.DirectRequest, Margin = new Thickness(0,4,0,12) };
        mode.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        mode.SetResourceReference(FontSizeProperty,"UiBodyFontSize");
        var explanation = LiteraryUi.Text(""); var error = LiteraryUi.Text("");
        root.Children.Add(mode); root.Children.Add(explanation);
        var keyHeading = LiteraryUi.Text(l("Studio.SendKeys"),true); keyHeading.Margin = new Thickness(0,18,0,8); root.Children.Add(keyHeading);
        AddKeyChoice("Enter", () => state.EnterAction, value => state.EnterAction = value);
        AddKeyChoice("Ctrl+Enter", () => state.ControlEnterAction, value => state.ControlEnterAction = value);
        root.Children.Add(LiteraryUi.Text(l("Studio.SendKeysHint"))); root.Children.Add(error);
        var close = LiteraryUi.Button(l("Common.Close"),Close); close.IsCancel = true;
        close.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; close.Margin = new Thickness(0,14,0,0);
        root.Children.Add(close);
        void Refresh()
        {
            mode.Content = l(state.DirectRequest ? "Studio.DirectRequest" : "Studio.ShowTask");
            explanation.Text = l(state.DirectRequest ? "Studio.DirectRequestHint" : "Studio.ShowTaskHint");
        }
        mode.Click += (_,_) =>
        {
            var previous = state.DirectRequest; state.DirectRequest = mode.IsChecked == true;
            if (persist()) error.Text = "";
            else { state.DirectRequest = previous; mode.IsChecked = previous; error.Text = l("Paragraph.SaveError"); }
            Refresh();
        };
        Refresh();

        void AddKeyChoice(string label, Func<StudioSendKeyAction> read, Action<StudioSendKeyAction> write)
        {
            var row = new Grid { Margin = new Thickness(0,0,0,10) };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(125) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1,GridUnitType.Star) });
            var caption = LiteraryUi.Text(label); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
            var choice = new ComboBox { MinHeight = 32 };
            System.Windows.Automation.AutomationProperties.SetName(choice,label);
            foreach (var value in Enum.GetValues<StudioSendKeyAction>())
                choice.Items.Add(new ComboBoxItem { Content = l("Studio.KeyAction."+value), Tag = value });
            choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => (StudioSendKeyAction)item.Tag == read());
            var restoring = false;
            choice.SelectionChanged += (_,_) =>
            {
                if (restoring || choice.SelectedItem is not ComboBoxItem selected) return;
                var previous = read(); write((StudioSendKeyAction)selected.Tag);
                if (persist()) error.Text = "";
                else
                {
                    write(previous); restoring = true;
                    choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => (StudioSendKeyAction)item.Tag == previous);
                    restoring = false; error.Text = l("Paragraph.SaveError");
                }
            };
            Grid.SetColumn(choice,1); row.Children.Add(choice); root.Children.Add(row);
        }
    }
}
