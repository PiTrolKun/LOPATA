using System.Windows.Controls;
using System.Windows.Markup;

namespace AIHub.Controls;

/// <summary>Flat library rows and menus, without the system theme's light icon gutter.</summary>
internal static class LetItOutLibraryStyles
{
    [ThreadStatic] private static ControlTemplate? _row, _menu, _item, _separator;
    internal static ControlTemplate Row => _row ??= Parse("Button", """
        <Border Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}">
          <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/>
        </Border>
        """);
    internal static ControlTemplate Menu => _menu ??= Parse("ContextMenu", """
        <Border Background="{TemplateBinding Background}" BorderBrush="{DynamicResource LineBrush}"
                BorderThickness="1" CornerRadius="7" Padding="4">
          <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle"/>
        </Border>
        """);
    internal static ControlTemplate Item => _item ??= (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type MenuItem}">
          <Border x:Name="Row" Background="Transparent" Padding="12,8" CornerRadius="4">
            <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource LineBrush}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Row" Property="Opacity" Value="0.5"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    internal static ControlTemplate Separator => _separator ??= Parse("Separator", """
        <Border Height="1" Margin="6,4" Background="{DynamicResource LineBrush}"/>
        """);
    private static ControlTemplate Parse(string type, string body) => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type "
        + type + "}'>" + body + "</ControlTemplate>");
}
