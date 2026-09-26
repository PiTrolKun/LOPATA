using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private string _contextFailureText = "";
    private Border? _contextFailureCard;

    private void ClearContextFailure()
    {
        _contextFailureText = "";
        if (_contextFailureCard is not null) _contextFailureCard.Visibility = Visibility.Collapsed;
    }

    private void ShowContextFailure(ImageAnalysisContextExhaustedException error)
    {
        _contextFailureText = _status.Text = LiteraryContextBudgetMessage.Format(error, _l);
    }

    private void RenderContextFailure()
    {
        _contextFailureCard = null;
        if (_contextFailureText.Length == 0) return;
        // This is request feedback, never an assistant answer or part of model context.
        _contextFailureCard = LiteraryWorkspaceParts.Card(LiteraryUi.Text(_contextFailureText));
        _contextFailureCard.Margin = new Thickness(0, 8, 6, 8);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_contextFailureCard, "Studio.ContextFailure");
        _messages.Children.Add(_contextFailureCard);
    }

    public void ClearRequestStatus()
    {
        if (IsWorking) return;
        ClearContextFailure();
        _status.Text = _tokens.Text = _receipts.Text = "";
        _receipts.ToolTip = null;
    }

    private FrameworkElement BuildStatus(UIElement memoryStatus)
    {
        // The main window hosts these live controls outside the route card.
        // Keep long errors and source receipts accessible without taking over the editor.
        foreach (var text in new[] { _status, _tokens, _receipts })
        {
            text.SetResourceReference(TextBlock.FontSizeProperty, "UiSmallFontSize");
            text.Margin = new Thickness(0, 2, 0, 2);
            var style = new Style(typeof(TextBlock));
            var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
            empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            style.Triggers.Add(empty); text.Style = style;
        }
        var current = new DockPanel();
        _tokens.Margin = new Thickness(16, 2, 0, 2);
        DockPanel.SetDock(_tokens, Dock.Right); current.Children.Add(_tokens);
        current.Children.Add(_status);
        var panel = new StackPanel();
        panel.Children.Add(current); panel.Children.Add(memoryStatus); panel.Children.Add(_receipts);
        return new ScrollViewer
        {
            Content = panel, MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }
}
