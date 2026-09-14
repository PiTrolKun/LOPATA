using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryCalibrationAnalysisPanel : UserControl
{
    private readonly List<Button> _buttons=[];
    private readonly TextBox _request;
    private readonly Button _send, _stop;
    private readonly TextBlock _status;
    public event Action<string,string>? Requested;
    public event Action? StopRequested;
    public void SetState(bool busy,bool current,string message="")
    { foreach(var b in _buttons)b.IsEnabled=!busy;_send.IsEnabled=!busy;_stop.Visibility=current?Visibility.Visible:Visibility.Collapsed;if(message.Length>0)_status.Text=message; }
    public LiteraryCalibrationAnalysisPanel(Func<string,string> l, string language, bool wholeProject)
    {
        var body=new StackPanel();
        var border=new Border { Child=body, BorderThickness=new Thickness(0,1,0,0), Padding=new Thickness(0,16,0,0), Margin=new Thickness(0,4,0,0) };
        border.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); Content=border;
        var title=new TextBlock { Text=l(wholeProject?"Literary.Analysis.All":"Literary.Analysis.Group"), FontWeight=FontWeights.SemiBold, Margin=new Thickness(0,0,0,10) };
        title.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush"); title.SetResourceReference(TextBlock.FontSizeProperty,"UiBodyFontSize"); body.Children.Add(title);
        string[][] rows=wholeProject
            ? [["Meaning","Coherence","Retelling","Contradictions"],["Names","Roles","Chronology","Causality"],["Rules","Statuses","Repetition","Clarity"]]
            : [["Meaning","Coherence","Retelling","Contradictions"]];
        foreach(var row in rows)
        {
            var buttons=new UniformGrid { Columns=4, Margin=new Thickness(0,0,0,8) }; body.Children.Add(buttons);
            foreach(var id in row)
            {
                var label=new TextBlock { Text=l("Literary.Analysis."+id), TextWrapping=TextWrapping.Wrap, TextAlignment=TextAlignment.Center };
                var button=new Button { Content=label, Margin=new Thickness(0,0,6,0), MinWidth=0, Height=double.NaN, MinHeight=38, Padding=new Thickness(6), ToolTip=l("Literary.Analysis."+id+"Hint") };
                button.Click+=(_,_)=>Requested?.Invoke(id,"");_buttons.Add(button);
                button.SetResourceReference(StyleProperty,"SecondaryButtonStyle"); ToolTipService.SetShowOnDisabled(button,true);
                System.Windows.Automation.AutomationProperties.SetName(button,label.Text); buttons.Children.Add(button);
            }
        }
        var requestRow=new Grid(); requestRow.ColumnDefinitions.Add(new ColumnDefinition()); requestRow.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto }); body.Children.Add(requestRow);
        var request=_request=new TextBox { MinHeight=44, AcceptsReturn=true, TextWrapping=TextWrapping.Wrap, MaxHeight=120, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Padding=new Thickness(10), Tag="CalibrationAnalysisRequest", ToolTip=l("Literary.Analysis.RequestHint") };
        request.SetResourceReference(TextBox.BackgroundProperty,"WindowBackgroundBrush"); request.SetResourceReference(TextBox.ForegroundProperty,"TextPrimaryBrush"); request.SetResourceReference(TextBox.BorderBrushProperty,"LineBrush"); request.SetResourceReference(TextBox.FontSizeProperty,"UiBodyFontSize");
        System.Windows.Automation.AutomationProperties.SetName(request,l("Literary.Analysis.Request")); LiterarySpellChecking.Enable(request,language); requestRow.Children.Add(request);
        var placeholder=new TextBlock { Text=l("Literary.Analysis.Request"), Margin=new Thickness(12,10,12,0), IsHitTestVisible=false };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush"); requestRow.Children.Add(placeholder);
        request.TextChanged+=(_,_)=>placeholder.Visibility=request.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        var send=_send=new Button { Content="➤", Width=44, MinWidth=0, Height=44, Padding=new Thickness(0), Margin=new Thickness(8,0,0,0), VerticalAlignment=VerticalAlignment.Top, ToolTip=l("Literary.Analysis.Send") };
        send.Click+=(_,_)=> { if(!string.IsNullOrWhiteSpace(_request.Text))Requested?.Invoke("Manual",_request.Text); };
        send.SetResourceReference(StyleProperty,"PrimaryButtonStyle"); ToolTipService.SetShowOnDisabled(send,true); System.Windows.Automation.AutomationProperties.SetName(send,l("Literary.Analysis.Send")); Grid.SetColumn(send,1); requestRow.Children.Add(send);
        _stop=new Button{Content=l("Literary.Analysis.Stop"),Visibility=Visibility.Collapsed,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,Margin=new Thickness(0,8,0,0)};
        _stop.SetResourceReference(StyleProperty,"SecondaryButtonStyle");_stop.Click+=(_,_)=>StopRequested?.Invoke();body.Children.Add(_stop);
        var note=_status=new TextBlock { Text=l("Literary.Analysis.Ready"), Margin=new Thickness(0,6,0,0), TextWrapping=TextWrapping.Wrap };
        note.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush"); note.SetResourceReference(TextBlock.FontSizeProperty,"UiSmallFontSize"); body.Children.Add(note);
    }
}
