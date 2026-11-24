using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    public LivingArcadeControl? LivingArcade { get; private set; }
    public bool IsLivingArcade { get; private set; }

    public void ShowLivingArcade(LivingArcadeControl control)
    {
        SelectedDirectionId = ScenarioNavigationCatalog.Experiments;
        IsSandboxLanding = IsVideoPreparation = IsSongClip = IsLetItOutChat = false;
        PlaceholderScenarioId = null;
        LivingArcade = control;
        IsLivingArcade = true;
        Render();
    }

    private Button CreateArcadeEntrance() => CreateHomeShortcut("gamepad", "Arcade.Title", "Arcade.Open", ScenarioNavigationCatalog.LivingArcade);

    private Button CreateHomeShortcut(string glyph, string titleKey, string automationId, string scenarioId)
    {
        var icon = ScenarioNavigationIcon.Create(glyph);
        icon.Width = icon.Height = 30;
        var button = new Button
        {
            Content = icon, Width = 58, Height = 52, Margin = new Thickness(8, 14, 0, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, ToolTip = _localize(titleKey),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0)
        };
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, _localize(titleKey));
        button.Click += (_, _) => OpenScenario(scenarioId);
        return button;
    }
}
