using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Control = System.Windows.Controls.Control;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

internal static class ApplicationUpdateChoices
{
    public static RadioButton Direction(Func<string, string> text, bool beta)
    {
        var title = text(beta ? "Updates.BetaTitle" : "Updates.StableTitle");
        var description = text(beta ? "Updates.BetaDescription" : "Updates.StableDescription");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = description, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) });
        var radio = new RadioButton
        {
            Content = content, GroupName = "UpdateDirection", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new(0, 0, 0, 12), Padding = new(18, 14, 18, 14), MinHeight = 80,
            BorderThickness = new(2), Cursor = System.Windows.Input.Cursors.Hand,
            BorderBrush = new SolidColorBrush(Color.FromRgb(137, 147, 164))
        };
        radio.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        if (beta) radio.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        AutomationProperties.SetName(radio, title + ". " + description);
        radio.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="RadioButton">
              <Border x:Name="Frame" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      Background="{DynamicResource SecondaryButtonBackgroundBrush}"
                      BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                      CornerRadius="8" Padding="{TemplateBinding Padding}">
                <Grid>
                  <Grid.ColumnDefinitions><ColumnDefinition Width="28"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Grid Width="18" Height="18" VerticalAlignment="Center" HorizontalAlignment="Left">
                    <Ellipse Stroke="{DynamicResource TextSecondaryBrush}" StrokeThickness="1.5"/>
                    <Ellipse x:Name="Selected" Width="10" Height="10" Fill="{DynamicResource AccentBrush}" Visibility="Collapsed"/>
                  </Grid>
                  <ContentPresenter Grid.Column="1" VerticalAlignment="Center"/>
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Selected" Property="Visibility" Value="Visible"/>
                  <Setter TargetName="Frame" Property="Background" Value="{DynamicResource StepBadgeBrush}"/>
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderThickness" Value="3"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.55"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        return radio;
    }

    public static void Button(Button button, string label, bool primary = false)
    {
        button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 280 };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        button.Height = double.NaN; button.MinHeight = 44; button.MinWidth = 140;
        button.FontSize = 16; button.Padding = new(18, 10, 18, 10); button.Margin = new(6, 6, 0, 0);
        AutomationProperties.SetName(button, label);
    }
}
