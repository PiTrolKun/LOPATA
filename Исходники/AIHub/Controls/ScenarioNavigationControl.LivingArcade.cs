using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Brushes = System.Windows.Media.Brushes;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class ScenarioNavigationControl
{
    public LivingArcadeControl? LivingArcade { get; private set; }
    public bool IsLivingArcade { get; private set; }
    private string _suggestedAgents = "";
    private WeakReference<Button>? _agentEntrance;

    internal void SetUnconnectedAgents(string names)
    {
        if (_suggestedAgents == names) return;
        _suggestedAgents = names;
        if (_agentEntrance?.TryGetTarget(out var button) == true) UpdateAgentEntrance(button);
    }

    private void UpdateAgentEntrance(Button button)
    {
        var hint = _suggestedAgents.Length == 0 ? _localize("ExternalAgents.Title")
            : string.Format(_localize("ExternalAgents.Unconnected"), _suggestedAgents);
        button.ToolTip = hint;
        AutomationProperties.SetHelpText(button, hint);
        var pulse = _suggestedAgents.Length > 0 && button.IsVisible && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(1, .48, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null;
        ((FrameworkElement)button.Content).BeginAnimation(OpacityProperty, pulse);
    }

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
        icon.Width = icon.Height = 56;
        var button = new Button
        {
            Content = icon, Width = 90, Height = 82, Margin = new Thickness(8, 14, 0, 8),
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
        if (scenarioId == ScenarioNavigationCatalog.ExternalAgents)
        {
            _agentEntrance = new(button);
            button.IsVisibleChanged += (_, _) => UpdateAgentEntrance(button);
            UpdateAgentEntrance(button);
        }
        button.Click += (_, _) => OpenScenario(scenarioId);
        return button;
    }
}
