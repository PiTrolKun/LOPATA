using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls.MailHub;

internal sealed partial class MailHubWindow
{
    private FrameworkElement SchedulePanel()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var preference = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var bound = new ComboBox { ItemsSource = new[] { T("BindHost"), T("BindIsland") },
            SelectedIndex = _settings.BindToIsland ? 1 : 0, Width = 230, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        bound.SelectionChanged += (_, _) => Save(_settings with { BindToIsland = bound.SelectedIndex == 1 });
        preference.Children.Add(new TextBlock { Text = T("Binding"), Margin = new Thickness(0, 0, 0, 4) });
        preference.Children.Add(bound);
        var enabled = new CheckBox { Content = T("ScheduleEnabled"), IsChecked = _settings.ScheduleEnabled,
            Margin = new Thickness(0, 8, 0, 0) };
        enabled.Click += (_, _) => Save(_settings with { ScheduleEnabled = enabled.IsChecked == true });
        preference.Children.Add(enabled);
        DockPanel.SetDock(preference, Dock.Top);
        root.Children.Add(preference);

        var weekdays = new[] { T("Monday"), T("Tuesday"), T("Wednesday"), T("Thursday"), T("Friday"), T("Saturday"), T("Sunday") };
        var grid = new Grid { MinWidth = 440 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(76) });
        for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        for (var day = 0; day < 7; day++)
        {
            var label = new TextBlock { Text = weekdays[day], Margin = new Thickness(4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(label, day + 1);
            grid.Children.Add(label);
        }
        for (var hour = 0; hour < 24; hour++)
        {
            int currentHour = hour;
            string time = currentHour.ToString("00", CultureInfo.InvariantCulture) + ":00";
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var row = new ToggleButton[7];
            var everyDay = new Button { Content = time, Margin = new Thickness(2), Padding = new Thickness(3) };
            AutomationProperties.SetName(everyDay, string.Format(T("EveryDayAt"), time));
            everyDay.Click += (_, _) =>
            {
                Save(_settings with { Hours = _settings.Hours.Concat(Enumerable.Range(0, 7).Select(day => day * 24 + currentHour))
                    .Distinct().Order().ToArray() });
                for (var day = 0; day < row.Length; day++)
                    row[day].IsChecked = _settings.Hours.Contains(day * 24 + currentHour);
            };
            Grid.SetRow(everyDay, currentHour + 1);
            grid.Children.Add(everyDay);

            for (var day = 0; day < 7; day++)
            {
                int slot = day * 24 + currentHour;
                var cell = new ToggleButton { Margin = new Thickness(2), MinHeight = 26,
                    IsChecked = _settings.Hours.Contains(slot) };
                cell.Content = cell.IsChecked == true ? time : "";
                AutomationProperties.SetName(cell, weekdays[day] + " " + time);
                AutomationProperties.SetAutomationId(cell, "MailHub.Schedule." + slot.ToString(CultureInfo.InvariantCulture));
                cell.Click += (_, _) =>
                {
                    var hours = _settings.Hours.Where(value => value != slot).ToList();
                    if (cell.IsChecked == true) hours.Add(slot);
                    Save(_settings with { Hours = hours.Order().ToArray() });
                    cell.IsChecked = _settings.Hours.Contains(slot);
                };
                cell.Checked += (_, _) => cell.Content = time;
                cell.Unchecked += (_, _) => cell.Content = "";
                Grid.SetRow(cell, currentHour + 1);
                Grid.SetColumn(cell, day + 1);
                grid.Children.Add(cell);
                row[day] = cell;
            }
        }
        root.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }
}
