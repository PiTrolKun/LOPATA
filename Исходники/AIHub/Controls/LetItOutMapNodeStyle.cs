using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button=System.Windows.Controls.Button;
using Control=System.Windows.Controls.Control;

namespace AIHub.Controls;

internal static class LetItOutMapNodeStyle
{
    internal static Button Create(string title,string id,double fontSize=13)
    {
        var text=new TextBlock{Text=title,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,FontSize=fontSize};
        text.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush");
        var button=new Button{Content=text,Padding=new(7),Cursor=System.Windows.Input.Cursors.Hand,FontWeight=FontWeights.Normal,
            HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch,VerticalContentAlignment=System.Windows.VerticalAlignment.Center};
        button.SetResourceReference(Control.ForegroundProperty,"TextPrimaryBrush");
        button.SetResourceReference(Control.BackgroundProperty,"PanelBrush");
        button.SetResourceReference(Control.BorderBrushProperty,"LineBrush");
        var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty,new Thickness(1.2));
        border.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding(nameof(Control.Background)){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        border.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding(nameof(Control.BorderBrush)){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.MarginProperty,new Thickness(7));
        content.SetBinding(ContentPresenter.ContentProperty,new System.Windows.Data.Binding(nameof(ContentControl.Content)){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        content.SetValue(ContentPresenter.VerticalAlignmentProperty,System.Windows.VerticalAlignment.Center);border.AppendChild(content);
        button.Template=new ControlTemplate(typeof(Button)){VisualTree=border};
        AutomationProperties.SetAutomationId(button,id);AutomationProperties.SetName(button,title);return button;
    }
}
