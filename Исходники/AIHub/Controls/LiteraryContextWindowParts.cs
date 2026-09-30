using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

internal static class LiteraryContextWindowParts
{
    public static void Configure(Window window, Window? owner, string title)
    {
        window.Owner = owner; window.Title = title; window.Width = 820; window.Height = 660;
        window.MinWidth = 430; window.MinHeight = 350;
        window.MaxHeight = Math.Max(350, SystemParameters.WorkArea.Height * .94);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main && main != window)
            window.Resources.MergedDictionaries.Add(main.Resources);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        window.SetResourceReference(Control.FontSizeProperty, "UiBodyFontSize");
    }
    public static string Meter(StudioContextMeter? meter, Func<string, string> l) => meter is null ? l("Studio.Context.Unknown")
        : string.Format(l("Studio.Context.Meter"), meter.Input, meter.Available, meter.Free, meter.UsedRatio * 100)
            + "\n" + l(meter.Estimate ? "Studio.Context.Estimate" : "Studio.Context.Reserve");
    public static Button Button(string title, string id, Action click, bool primary = false)
    {
        var button = LiteraryUi.Button(title, click, primary); button.MinWidth = 0;
        button.Height = double.NaN; button.MinHeight = 42;
        button.Padding = new Thickness(14, 9, 14, 9); button.Margin = new Thickness(0, 0, 8, 0);
        button.Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, id);
        System.Windows.Automation.AutomationProperties.SetName(button, title); return button;
    }
    public static ControlTemplate ChoiceTemplate()
    {
        // Common button styles center the presenter regardless of HorizontalContentAlignment.
        // Choice cards need a full-width presenter so short and long descriptions start alike.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }
    public static void Error(TextBlock status, Exception error, Func<string, string> l) => status.Text = error switch
    {
        OperationCanceledException => l("Paragraph.Cancelled"),
        ImageAnalysisContextExhaustedException budget => LiteraryContextBudgetMessage.Format(budget, l),
        LiteraryGpuContextUnavailableException => l("Literary.MemoryRecovery.GpuUnavailable"),
        LiteraryRamReserveException => l("Literary.MemoryRecovery.RamUnavailable"),
        _ => error.Message.StartsWith("Studio.Context.", StringComparison.Ordinal) ? l(error.Message)
            : l("Paragraph.Failure") + " " + error.Message
    };
}

public sealed class LiteraryContextManagerWindow : Window
{
    public LiteraryContextManagerWindow(Window? owner, Func<string, string> l, StudioContextPlan plan,
        StudioContextMeter? meter, bool rejected,
        Func<IReadOnlySet<string>, string?, CancellationToken, Task<StudioContextMeter?>> measure,
        Func<StudioContextMethod, double, IProgress<StudioCompactionProgress>, CancellationToken, Task<StudioCompactionResult>> generate,
        Func<IReadOnlySet<string>, string?, StudioContextMethod, CancellationToken, Task<bool>> apply)
    {
        LiteraryContextWindowParts.Configure(this, owner, l("Studio.Context.Title"));
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel();
        header.Children.Add(LiteraryUi.Text(Title, true));
        header.Children.Add(LiteraryUi.Text(l(rejected ? "Studio.Context.Rejected" : "Studio.Context.Explanation")));
        header.Children.Add(LiteraryUi.Text(LiteraryContextWindowParts.Meter(meter, l)));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = LiteraryUi.Text(l("Studio.Context.ArchiveHint")); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var choices = new StackPanel(); root.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var recommended = meter?.Recommended(rejected) ?? (rejected ? StudioContextMethod.Smart : (StudioContextMethod?)null);
        foreach (var method in Enum.GetValues<StudioContextMethod>())
        {
            var title = l("Studio.Context.Method." + method);
            var button = LiteraryContextWindowParts.Button(title, "Studio.Context.Choose." + method, () =>
            {
                if (method == StudioContextMethod.Manual)
                {
                    var manual = new LiteraryContextManualWindow(this, l, plan, measure,
                        (ids, ct) => apply(ids, null, method, ct));
                    if (manual.ShowDialog() == true) DialogResult = true;
                }
                else
                {
                    var preview = new LiteraryContextPreviewWindow(this, l, method,
                        (ratio, progress, ct) => generate(method, ratio, progress, ct), measure,
                        (ids, text, ct) => apply(ids, text, method, ct));
                    if (preview.ShowDialog() == true) DialogResult = true;
                }
            }, method == recommended);
            var text = new StackPanel(); text.Children.Add(LiteraryUi.Text(title, true));
            text.Children.Add(LiteraryUi.Text(l("Studio.Context.MethodHint." + method)));
            if (method == recommended) text.Children.Add(LiteraryUi.Text(l("Studio.Context.Recommended")));
            button.Content = text; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Template = LiteraryContextWindowParts.ChoiceTemplate();
            button.Margin = new Thickness(0, 8, 0, 0); button.IsEnabled = plan.Items.Count > 0; choices.Children.Add(button);
        }
    }
}
