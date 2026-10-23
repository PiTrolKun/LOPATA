using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal sealed class VectorSettingsWindow : VectorDialog
{
    internal VectorGenerationOptions Value { get; private set; }
    internal VectorSettingsWindow(Window? owner, Func<string, string> l, VectorGenerationOptions value, string section)
        : base(owner, l, "Settings." + section, "Vector.Settings." + section)
    {
        Value = value; Func<VectorGenerationOptions> read;
        if (section == "Canvas")
        {
            var ratios = new[] { "1:1", "4:3", "3:4", "16:9", "9:16", "3:2", "2:3", "custom" };
            var ratio = Choose("Settings.Ratio", ratios, ratios.Contains(value.CanvasWidth + ":" + value.CanvasHeight) ? value.CanvasWidth + ":" + value.CanvasHeight : "custom", "Ratio", "Settings.Ratio");
            var width = Input("Settings.CanvasWidth", value.CanvasWidth.ToString(CultureInfo.InvariantCulture), "Settings.CanvasWidth");
            var height = Input("Settings.CanvasHeight", value.CanvasHeight.ToString(CultureInfo.InvariantCulture), "Settings.CanvasHeight");
            var choosing = false;
            ratio.SelectionChanged += (_, _) =>
            {
                var parts = Pick(ratio).Split(':'); if (parts.Length != 2) return;
                choosing = true; width.Text = parts[0]; height.Text = parts[1]; choosing = false;
            };
            width.TextChanged += (_, _) => { if (!choosing) ratio.SelectedValue = "custom"; };
            height.TextChanged += (_, _) => { if (!choosing) ratio.SelectedValue = "custom"; };
            Note("Settings.CanvasHint");
            read = () => value with { CanvasWidth = Number(width), CanvasHeight = Number(height) };
        }
        else if (section == "Style")
        {
            var purpose = Choose("Settings.Purpose", VectorGenerationOptions.Purposes, value.Purpose, "Purpose", "Settings.Purpose");
            var style = Choose("Settings.StyleOnly", VectorGenerationOptions.Styles, value.Style, "Style", "Settings.Style");
            read = () => value with { Purpose = Pick(purpose), Style = Pick(style) };
        }
        else if (section == "Detail")
        {
            var detail = Choose("Settings.Detail", VectorGenerationOptions.Details, value.Detail, "Detail", "Settings.Detail");
            Note("Settings.DetailHint"); read = () => value with { Detail = Pick(detail) };
        }
        else if (section == "Palette")
        {
            var palette = Choose("Settings.Palette", VectorGenerationOptions.Palettes, value.Palette, "Palette", "Settings.Palette");
            var colors = Input("Settings.Colors", value.Colors, "Settings.Colors");
            void Refresh() => colors.IsEnabled = Pick(palette) == "custom";
            palette.SelectionChanged += (_, _) => Refresh(); Refresh(); Note("Settings.ColorsHint");
            read = () => value with { Palette = Pick(palette), Colors = colors.Text.Trim() };
        }
        else
        {
            var background = Choose("Settings.Background", VectorGenerationOptions.Backgrounds, value.Background, "Background", "Settings.Background");
            var color = Input("Settings.BackgroundColor", value.BackgroundColor, "Settings.BackgroundColor");
            void Refresh() => color.IsEnabled = Pick(background) == "color";
            background.SelectionChanged += (_, _) => Refresh(); Refresh(); Note("Settings.BackgroundHint");
            read = () => value with { Background = Pick(background), BackgroundColor = color.Text.Trim() };
        }
        Finish(() => { var updated = read(); updated.Validate(); Value = updated; return true; });
    }
}
