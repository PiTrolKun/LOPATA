using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SystemColors = System.Windows.SystemColors;

namespace AIHub.Controls;

internal sealed partial class VideoH3WorkspaceControl
{
    private void BuildScheduleMenu()
    {
        var menu = new ContextMenu();
        foreach (var delayed in new[] { false, true })
        {
            var item = new MenuItem { Header = T(delayed ? "H3ScheduleAfter" : "H3ScheduleAt") };
            item.Click += (_, _) => { if (!_externalBusy && GenerationAvailable && TryCommit()) ScheduleDialog(delayed); };
            menu.Items.Add(item);
        }
        _generate.ContextMenu = menu;
    }
    private void ScheduleDialog(bool delayed)
    {
        var dialog = new Window { Title = T("H3ScheduleTitle"), Owner = Window.GetWindow(this), Width = 500,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "SecondaryButtonBackgroundBrush", "AccentBrush", "UiBodyFontSize", "UiSectionFontSize" })
            if (TryFindResource(key) is { } resource) dialog.Resources[key] = resource;
        if (TryFindResource("PanelBrush") is { } panelBrush) dialog.Resources[SystemColors.WindowBrushKey] = panelBrush;
        if (TryFindResource("TextPrimaryBrush") is { } textBrush) dialog.Resources[SystemColors.WindowTextBrushKey] = textBrush;
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(dialog, "VideoH3.Schedule.Window");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label(T(delayed ? "H3ScheduleAfter" : "H3ScheduleAt")));
        var suggested = DateTime.Now.AddMinutes(5);
        var date = new DatePicker { SelectedDate = suggested.Date, Margin = new Thickness(0, 4, 0, 12) };
        var time = new TextBox { Text = suggested.ToString("HH:mm", CultureInfo.InvariantCulture), Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6), MaxLength = 5 };
        AutomationProperties.SetAutomationId(date, "VideoH3.Schedule.Date"); AutomationProperties.SetAutomationId(time, "VideoH3.Schedule.Time");
        var delay = new TextBox { Text = "30", Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6), MaxLength = 5 };
        AutomationProperties.SetAutomationId(delay, "VideoH3.Schedule.Delay");
        foreach (System.Windows.Controls.Control input in new System.Windows.Controls.Control[] { date, time, delay })
        {
            input.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush");
            input.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush");
            input.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "LineBrush");
        }
        if (delayed) { panel.Children.Add(Label(T("H3ScheduleDelay"))); panel.Children.Add(delay); }
        else { panel.Children.Add(Label(T("H3ScheduleDate"))); panel.Children.Add(date); panel.Children.Add(Label(T("H3ScheduleTime"))); panel.Children.Add(time); }
        var error = Label(""); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DateTimeOffset? selected = null;
        var cancel = new Button { Content = T("FinishCancel"), IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        var apply = new Button { Content = T("H3ScheduleApply"), IsDefault = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        cancel.SetResourceReference(StyleProperty, "SettingsActionButtonStyle"); apply.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        AutomationProperties.SetAutomationId(apply, "VideoH3.Schedule.Apply");
        apply.Click += (_, _) =>
        {
            if (delayed && int.TryParse(delay.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10080)
                selected = DateTimeOffset.Now.AddMinutes(minutes);
            else if (!delayed && date.SelectedDate is { } day && TimeSpan.TryParseExact(time.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var clock))
            {
                var local = DateTime.SpecifyKind(day.Date + clock, DateTimeKind.Unspecified);
                if (!TimeZoneInfo.Local.IsInvalidTime(local)) selected = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            }
            if (selected is not { } due || due <= DateTimeOffset.Now || due > DateTimeOffset.Now.AddDays(366))
            { error.Text = T("H3ScheduleInvalid"); selected = null; return; }
            dialog.DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); panel.Children.Add(actions); dialog.Content = panel;
        if (dialog.ShowDialog() == true && selected is { } schedule) ScheduleGenerationRequested?.Invoke(schedule);
    }
}
