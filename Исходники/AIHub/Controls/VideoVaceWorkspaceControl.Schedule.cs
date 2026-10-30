using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using SystemColors = System.Windows.SystemColors;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private void BuildScheduleMenu()
    {
        var menu = new ContextMenu();
        foreach (var delayed in new[] { false, true })
        {
            var item = new MenuItem { Header = T(delayed ? "ScheduleAfter" : "ScheduleAt") };
            item.Click += (_, _) => { if (GenerationAvailable && !IsBusy && TryCommit()) ScheduleDialog(delayed); }; menu.Items.Add(item);
        }
        _generate.ContextMenu = menu;
    }
    private void ScheduleDialog(bool delayed)
    {
        var dialog = new Window { Title = T("ScheduleTitle"), Owner = Window.GetWindow(this), Width = 500, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "WindowBackgroundBrush", "PanelBrush", "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "SecondaryButtonBackgroundBrush", "AccentBrush", "UiBodyFontSize", "UiSectionFontSize" })
            if (TryFindResource(key) is { } resource) dialog.Resources[key] = resource;
        if (TryFindResource("PanelBrush") is { } panel) dialog.Resources[SystemColors.WindowBrushKey] = panel;
        if (TryFindResource("TextPrimaryBrush") is { } text) dialog.Resources[SystemColors.WindowTextBrushKey] = text;
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(dialog, "VideoVace.Schedule.Window");
        var content = new StackPanel { Margin = new Thickness(20) }; content.Children.Add(Label(T(delayed ? "ScheduleAfter" : "ScheduleAt")));
        var suggestion = DateTime.Now.AddMinutes(5);
        var date = new DatePicker { SelectedDate = suggestion.Date, Margin = new Thickness(0, 4, 0, 12) };
        var time = new TextBox { Text = suggestion.ToString("HH:mm", CultureInfo.InvariantCulture), Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 12), MaxLength = 5 };
        var delay = new TextBox { Text = "30", Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 12), MaxLength = 5 };
        AutomationProperties.SetAutomationId(date, "VideoVace.Schedule.Date"); AutomationProperties.SetAutomationId(time, "VideoVace.Schedule.Time"); AutomationProperties.SetAutomationId(delay, "VideoVace.Schedule.Delay");
        foreach (System.Windows.Controls.Control input in new System.Windows.Controls.Control[] { date, time, delay })
        { input.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "PanelBrush"); input.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimaryBrush"); input.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "LineBrush"); }
        if (delayed) { content.Children.Add(Label(T("ScheduleDelay"))); content.Children.Add(delay); }
        else { content.Children.Add(Label(T("ScheduleDate"))); content.Children.Add(date); content.Children.Add(Label(T("ScheduleTime"))); content.Children.Add(time); }
        var error = Label(""); content.Children.Add(error); DateTimeOffset? selected = null;
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Button { Content = T("Cancel"), IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        var apply = new Button { Content = T("ScheduleApply"), IsDefault = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        cancel.SetResourceReference(StyleProperty, "SettingsActionButtonStyle"); apply.SetResourceReference(StyleProperty, "SettingsActionButtonStyle"); AutomationProperties.SetAutomationId(apply, "VideoVace.Schedule.Apply");
        apply.Click += (_, _) =>
        {
            selected = null;
            if (delayed && int.TryParse(delay.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes is >= 1 and <= 10080) selected = DateTimeOffset.Now.AddMinutes(minutes);
            else if (!delayed && date.SelectedDate is { } day && TimeSpan.TryParseExact(time.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var clock))
            { var local = DateTime.SpecifyKind(day.Date + clock, DateTimeKind.Unspecified); if (!TimeZoneInfo.Local.IsInvalidTime(local)) selected = new(local, TimeZoneInfo.Local.GetUtcOffset(local)); }
            if (selected is not { } due || due <= DateTimeOffset.Now || due > DateTimeOffset.Now.AddDays(366)) { error.Text = T("ScheduleInvalid"); selected = null; return; }
            dialog.DialogResult = true;
        };
        actions.Children.Add(cancel); actions.Children.Add(apply); content.Children.Add(actions); dialog.Content = content;
        if (dialog.ShowDialog() == true && selected is { } due) ScheduleGenerationRequested?.Invoke(due);
    }
}
