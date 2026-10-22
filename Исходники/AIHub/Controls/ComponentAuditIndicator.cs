using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

/// <summary>A persistent audit switch and a non-modal, actionable notice anchored to it.</summary>
public sealed class ComponentAuditIndicator : Button
{
    private readonly Popup _notice = new() { Placement = PlacementMode.Top, StaysOpen = true, AllowsTransparency = true };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 10) };
    private readonly Button _enable = new(), _dismiss = new();
    private readonly Border _frame;
    private Func<string, string> _text = key => key;
    private string? _noticeKey;
    private bool _pendingNotice;

    public event Action? EnableRequested;
    public bool AuditEnabled { get; private set; } = true;
    public bool IsNoticeOpen => _notice.IsOpen;
    public Button EnableAction => _enable;
    internal string NoticeMessage => _message.Text;
    internal FrameworkElement NoticeView => _frame;

    public ComponentAuditIndicator()
    {
        Content = new TextBlock { Text = "👁", FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji"), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(this, "ComponentAudit.Toggle");
        _notice.PlacementTarget = this;
        var body = new StackPanel();
        body.Children.Add(_title); body.Children.Add(_message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _enable.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(_enable); actions.Children.Add(_dismiss); body.Children.Add(actions);
        _frame = new Border { Child = body, Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), MaxWidth = 450, MinWidth = 280, Margin = new Thickness(4) };
        _frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        foreach (var block in new[] { _title, _message }) {
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            block.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize");
        }
        foreach (var action in new[] { _enable, _dismiss }) {
            action.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); action.Padding = new Thickness(10, 6, 10, 6);
            action.MinHeight = 32;
        }
        AutomationProperties.SetAutomationId(_frame, "ComponentAudit.Notice");
        AutomationProperties.SetAutomationId(_enable, "ComponentAudit.Enable");
        AutomationProperties.SetAutomationId(_dismiss, "ComponentAudit.Dismiss");
        _notice.Child = _frame;
        _enable.Click += (_, _) => EnableRequested?.Invoke();
        _dismiss.Click += (_, _) => HideNotice();
        _frame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { HideNotice(); e.Handled = true; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && IsNoticeOpen) { HideNotice(); e.Handled = true; } };
        Loaded += (_, _) => OpenPendingNotice();
        IsVisibleChanged += (_, _) => {
            if (IsVisible) OpenPendingNotice();
            else { _pendingNotice |= _notice.IsOpen; _notice.IsOpen = false; }
        };
        Unloaded += (_, _) => { _pendingNotice |= _notice.IsOpen; _notice.IsOpen = false; };
        ApplyState(true);
    }

    public void Localize(Func<string, string> text)
    {
        _text = text; ApplyState(AuditEnabled); UpdateNoticeText();
    }

    public void ApplyState(bool enabled)
    {
        AuditEnabled = enabled;
        Background = new SolidColorBrush(enabled ? Color.FromRgb(192, 39, 48) : Color.FromRgb(17, 121, 66));
        Foreground = Brushes.White;
        var state = _text(enabled ? "ComponentAudit.Enabled" : "ComponentAudit.Disabled");
        ToolTip = state; AutomationProperties.SetName(this, state); AutomationProperties.SetHelpText(this, state);
        if (enabled && _noticeKey == "ComponentAudit.FailureMessage" || !enabled && _noticeKey == "ComponentAudit.StartupMessage") HideNotice();
    }

    public void ShowStartupNotice() => ShowNotice("ComponentAudit.StartupMessage");
    public void ShowFailureNotice() { if (!AuditEnabled) ShowNotice("ComponentAudit.FailureMessage"); }
    public void ShowSaveFailure() => ShowNotice("ComponentAudit.SaveFailed");

    public void HideNotice()
    {
        _pendingNotice = false; _noticeKey = null; _notice.IsOpen = false;
    }

    private void ShowNotice(string key)
    {
        // One panel replaces another; a nested error never creates a stack of windows.
        _noticeKey = key; _pendingNotice = true; UpdateNoticeText(); OpenPendingNotice();
    }

    private void UpdateNoticeText()
    {
        _title.Text = _text("ComponentAudit.Title");
        _message.Text = _noticeKey is { } key ? _text(key) : string.Empty;
        _enable.Content = _text("ComponentAudit.Enable"); _dismiss.Content = _text("ComponentAudit.Dismiss");
        _enable.Visibility = _noticeKey == "ComponentAudit.FailureMessage" && !AuditEnabled ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_frame, _title.Text + ". " + _message.Text);
        AutomationProperties.SetName(_enable, _text("ComponentAudit.Enable"));
        AutomationProperties.SetName(_dismiss, _text("ComponentAudit.Dismiss"));
    }

    private void OpenPendingNotice()
    {
        if (!_pendingNotice || !IsLoaded || !IsVisible) return;
        if (Window.GetWindow(this) is { } owner) _frame.Resources = owner.Resources;
        _pendingNotice = false; _notice.IsOpen = true;
    }
}
