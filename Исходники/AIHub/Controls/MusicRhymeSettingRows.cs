using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Controls;

internal static class MusicRhymeSettingRows
{
    public static Func<double> AddSlider(Panel parent, MusicRhymeSetting setting, double value,
        Func<string, string> l, bool percentage = false)
    {
        var row = new StackPanel { Margin = new(0, 8, 0, 10) };
        var label = l("Music.Rhyme.Settings.Simple." + setting.Id);
        var hint = l("Music.Rhyme.Settings.Simple." + setting.Id + ".Hint");
        row.Children.Add(Text(label, "TextPrimaryBrush"));
        row.Children.Add(Text(hint, "TextSecondaryBrush", 12));
        var scale = percentage ? 100d : 1d;
        var range = new Grid(); range.ColumnDefinitions.Add(new()); range.ColumnDefinitions.Add(new() { Width = new(75) });
        var slider = new Slider { Minimum = setting.Minimum * scale, Maximum = setting.Maximum * scale, Value = value * scale,
            TickFrequency = setting.Step * scale, SmallChange = setting.Step * scale, IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 4, 12, 4), ToolTip = hint };
        AutomationProperties.SetAutomationId(slider, "Music.Rhyme.Setting." + setting.Id);
        AutomationProperties.SetName(slider, label);
        var display = Text(Format(value * scale), "TextPrimaryBrush"); display.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(display, 1); range.Children.Add(slider); range.Children.Add(display); row.Children.Add(range);
        row.Children.Add(Text(string.Format(CultureInfo.CurrentCulture, l("Music.Rhyme.Settings.Range"),
            Format(slider.Minimum), Format(slider.Maximum), Format(setting.Default * scale)), "TextSecondaryBrush", 11));
        slider.ValueChanged += (_, _) => display.Text = Format(slider.Value); parent.Children.Add(row);
        return () => {
            var result = Math.Round(slider.Value / scale, 6);
            if (!setting.Accepts(result)) throw new ArgumentException(l(setting.LabelKey));
            return result;
        };
        string Format(double number) => number.ToString("0.##", CultureInfo.CurrentCulture) + (percentage ? "%" : "");
    }

    public static Func<double> Add(Panel parent, MusicRhymeSetting setting, double value, Func<string, string> l)
    {
        var row = new Grid { Margin = new(0, 5, 0, 9) };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(130) });
        var description = new StackPanel { Margin = new(0, 0, 16, 0) }; row.Children.Add(description);
        description.Children.Add(Text(l(setting.LabelKey), "TextPrimaryBrush"));
        description.Children.Add(Text(l(setting.HintKey), "TextSecondaryBrush", 12));
        if (setting.Kind == MusicRhymeSettingKind.Information)
        {
            parent.Children.Add(row);
            return () => setting.Default;
        }
        if (setting.Kind == MusicRhymeSettingKind.Toggle)
        {
            var check = new CheckBox { IsChecked = value != 0, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, ToolTip = l(setting.HintKey) };
            AutomationProperties.SetName(check, l(setting.LabelKey));
            AutomationProperties.SetAutomationId(check, "Music.Rhyme.Setting." + setting.Id);
            Grid.SetColumn(check, 1); row.Children.Add(check); parent.Children.Add(row);
            return () => check.IsChecked == true ? 1 : 0;
        }
        var input = new TextBox { Text = value.ToString("0.####", CultureInfo.CurrentCulture), Padding = new(6),
            VerticalAlignment = VerticalAlignment.Top, ToolTip = l(setting.HintKey) };
        input.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetName(input, l(setting.LabelKey));
        AutomationProperties.SetAutomationId(input, "Music.Rhyme.Setting." + setting.Id);
        var controls = new StackPanel(); controls.Children.Add(input);
        controls.Children.Add(Text(string.Format(CultureInfo.CurrentCulture, l("Music.Rhyme.Settings.Range"),
            setting.Minimum, setting.Maximum, setting.Default), "TextSecondaryBrush", 11));
        Grid.SetColumn(controls, 1); row.Children.Add(controls); parent.Children.Add(row);
        return () => {
            if (!double.TryParse(input.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !setting.Accepts(parsed)) throw new ArgumentException(l(setting.LabelKey));
            return parsed;
        };
    }

    internal static TextBlock Text(string text, string brush, double? size = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 1, 0, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (size is { } value) block.FontSize = value;
        return block;
    }
}
