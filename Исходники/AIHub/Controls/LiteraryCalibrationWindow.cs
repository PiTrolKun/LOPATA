using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed class LiteraryCalibrationWindow : Window
{
    public LiteraryCalibrationWindow(Func<string,string> l, string initial, string language, Action<string> save, Func<CalibrationRequest,CancellationToken,Task<CalibrationResult>>? analyze=null, Action<string,object>? log=null)
    {
        Title=l("Literary.Calibration.Title"); Width=1060; Height=800; MinWidth=600; MinHeight=400;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main) Resources=main.Resources;
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        var root=new Grid { Margin=new Thickness(20) }; Content=root;
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var hint=new TextBlock { Text=l("Literary.Calibration.GroupHint"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,16) };
        hint.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush"); root.Children.Add(hint);
        var document = new LiteraryCalibrationDocument(initial);
        var coordinator = new LiteraryCalibrationCoordinator(this,l,language,analyze,log);
        var fieldLabels = new LiteraryCalibrationLabels(l);
        var groups = new StackPanel { MaxWidth = 1080, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        var scroll = new ScrollViewer { Content = groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0,0,14,0) };
        Grid.SetRow(scroll,1); root.Children.Add(scroll);
        var footer=new StackPanel { Margin=new Thickness(0,12,0,0) }; Grid.SetRow(footer,2); root.Children.Add(footer);
        var status=new TextBlock { Text=string.IsNullOrEmpty(initial)?l("Literary.Calibration.Empty"):"", TextWrapping=TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush"); footer.Children.Add(status);
        var actions=new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, Margin=new Thickness(0,8,0,0) }; footer.Children.Add(actions);
        var saveButton=new Button { Content=l("Literary.Anchor.Save"), Margin=new Thickness(0,0,10,0) }; saveButton.SetResourceReference(StyleProperty,"PrimaryButtonStyle"); actions.Children.Add(saveButton);
        var close=new Button { Content=l("Literary.Calibration.Close") }; close.SetResourceReference(StyleProperty,"SecondaryButtonStyle"); actions.Children.Add(close);
        var saved=initial;
        TextBlock Label(string text, bool heading = false)
        {
            var label = new TextBlock { Text=text, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,heading?18:6), FontWeight=heading?FontWeights.Bold:FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
            label.SetResourceReference(TextBlock.FontSizeProperty,heading?"UiSectionFontSize":"UiBodyFontSize");
            return label;
        }
        void Input(StackPanel panel, string value, Action<string> update, int topic, string label)
        {
            var input=new TextBox { Text=value, AcceptsReturn=true, AcceptsTab=true, TextWrapping=TextWrapping.Wrap,
                MinHeight=64, Padding=new Thickness(12), Margin=new Thickness(0,0,0,18), IsUndoEnabled=true };
            input.SetResourceReference(TextBox.BackgroundProperty,"WindowBackgroundBrush"); input.SetResourceReference(TextBox.ForegroundProperty,"TextPrimaryBrush");
            input.SetResourceReference(TextBox.BorderBrushProperty,"LineBrush"); input.SetResourceReference(TextBox.FontSizeProperty,"UiBodyFontSize");
            LiterarySpellChecking.Enable(input,language); panel.Children.Add(coordinator.AddField(input,topic,label));
            input.TextChanged+=(_,_)=> { update(input.Text); status.Text=document.Serialize()==saved?"":l("Literary.Draft.Unsaved"); };
        }
        if (document.Structured)
        {
            foreach (var group in document.Fields.GroupBy(f=>f.Topic).OrderBy(g=>g.Key))
            {
                var panel=new StackPanel();
                var card=new Border { Child=panel, Padding=new Thickness(20), Margin=new Thickness(0,0,0,20), CornerRadius=new CornerRadius(8), BorderThickness=new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty,"PanelBrush"); card.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); groups.Children.Add(card);
                panel.Children.Add(Label(group.Key is >=0 and <=9?l("Literary.Interview.Topic"+group.Key):l("Literary.Calibration.Other"),true));
                foreach(var field in group)
                {
                    var title=fieldLabels.Get(field);
                    if(group.Key==8 && field.Label.StartsWith("route-title:",StringComparison.Ordinal)) title=string.Format(l("Literary.Calibration.RouteTitle"),field.Label.Split(':')[1]);
                    else if(group.Key==8 && field.Label.StartsWith("route-description:",StringComparison.Ordinal)) title=l("Literary.Calibration.RouteDescription");
                    if(title.Length>0)panel.Children.Add(Label(title));
                    Input(panel,field.Value,field.Update,group.Key,title);
                }
                panel.Children.Add(coordinator.Panel(group.Key));
            }
        }
        else { groups.Children.Add(Label(l("Literary.Calibration.General"),true)); Input(groups,initial,document.SetPlain,0,l("Literary.Calibration.General")); groups.Children.Add(coordinator.Panel(0)); }
        var overall=new Border { Child=coordinator.Panel(null), Padding=new Thickness(20), Margin=new Thickness(0,0,0,20), CornerRadius=new CornerRadius(8), BorderThickness=new Thickness(1) };
        overall.SetResourceReference(Border.BackgroundProperty,"PanelBrush"); overall.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); groups.Children.Add(overall);
        saveButton.Click+=(_,_)=> { try { var text=document.Serialize(); save(text); saved=text; status.Text=l("Literary.Calibration.Saved"); } catch(Exception) { status.Text=l("Literary.Calibration.SaveError"); } };
        close.Click+=(_,_)=>Close();
        Closing+=(_,e)=> { if(!coordinator.RequestClose()){e.Cancel=true;return;} if(document.Serialize()!=saved && System.Windows.MessageBox.Show(this,l("Literary.Calibration.Discard"),Title,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)e.Cancel=true; };
    }
}
