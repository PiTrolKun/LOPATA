using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private string _contextFailureText = "";
    private Border? _contextFailureCard;
    private Popup? _statusPopup;
    public void PublishStatus(string text) => _activity.SetStatus(text);

    private void ClearContextFailure()
    {
        _contextFailureText = "";
        if (_contextFailureCard is not null) _contextFailureCard.Visibility = Visibility.Collapsed;
    }

    private void ShowContextFailure(ImageAnalysisContextExhaustedException error)
    {
        if (!error.OutputTruncated)
        {
            _contextRejected = true;
            if (error.Budget is { } budget) SetContextMeter(new(budget.InputTokens, budget.ContextTokens));
        }
        _contextFailureText = _status.Text = LiteraryContextBudgetMessage.Format(error, _l);
        if (!error.OutputTruncated) _contextFailureText = _status.Text = _contextFailureText + "\n" + _l("Studio.Context.OpenHint");
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

    private Popup BuildStatusPopup(UIElement memoryStatus)
    {
        var details = BuildStatus(memoryStatus);
        var frame = new Border { Child = details, Padding = new Thickness(12), BorderThickness = new Thickness(1),
            MaxWidth = 760, MinWidth = 360 };
        frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _statusPopup = new Popup { PlacementTarget = _activity, Placement = PlacementMode.Top,
            StaysOpen = false, AllowsTransparency = true, Child = frame };
        _activity.DetailsHint = _l("Studio.StatusDetails");
        _activity.Cursor = System.Windows.Input.Cursors.Hand;
        _activity.Focusable = true;
        _activity.MouseLeftButtonUp += (_, _) => _statusPopup.IsOpen = !_statusPopup.IsOpen;
        _activity.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
            { _statusPopup.IsOpen = !_statusPopup.IsOpen; e.Handled = true; }
        };
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        descriptor.AddValueChanged(_status, (_, _) => { if (_status.Text.Length > 0) _activity.SetStatus(_status.Text); });
        descriptor.AddValueChanged(_receipts, (_, _) =>
        {
            if (_receipts.Text.Length > 0) _activity.SetStatus(_receipts.Text.Split('\n').Last());
        });
        if (memoryStatus is System.Windows.Controls.Panel panel)
        {
            var memoryText = panel.Children.OfType<TextBlock>().FirstOrDefault();
            if (memoryText is not null)
            {
                EventHandler changed = (_, _) =>
                {
                    if (memoryText.Tag as string != "summary" && memoryText.Text.Length > 0 && !IsWorking)
                        _activity.SetStatus(memoryText.Text);
                };
                var attached = false;
                Loaded += (_, _) =>
                {
                    if (attached) return;
                    descriptor.AddValueChanged(memoryText, changed); attached = true;
                };
                Unloaded += (_, _) =>
                {
                    if (!attached) return;
                    descriptor.RemoveValueChanged(memoryText, changed); attached = false;
                };
            }
        }
        return _statusPopup;
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
            Content = panel, MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }
}
