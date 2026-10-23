using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.VectorGraphics;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Controls;

internal sealed class VectorRasterExportWindow : VectorDialog
{
    internal VectorRasterOptions? Value { get; private set; }
    internal VectorRasterExportWindow(Window? owner, Func<string, string> l, int width, int height)
        : base(owner, l, "Export.Title", "Vector.Export.Window")
    {
        var format = Choose("Export.Format", ["png", "jpeg", "webp"], "png", "Format", "Export.Format");
        var w = Input("Export.Width", width.ToString(CultureInfo.InvariantCulture), "Export.Width");
        var h = Input("Export.Height", height.ToString(CultureInfo.InvariantCulture), "Export.Height");
        var locked = new CheckBox { Content = T("Export.LockRatio"), IsChecked = true, Margin = new(0, 8, 0, 6) };
        AutomationProperties.SetAutomationId(locked, "Vector.Export.LockRatio"); Body.Children.Add(locked);
        var aspect = width / (double)height; var syncing = false;
        void Sync(bool fromWidth)
        {
            if (syncing || locked.IsChecked != true) return;
            var source = fromWidth ? w : h; var target = fromWidth ? h : w;
            if (!int.TryParse(source.Text, out var number) || number <= 0) return;
            var next = Math.Round(fromWidth ? number / aspect : number * aspect);
            if (next < 1 || next > int.MaxValue) return;
            syncing = true; target.Text = ((int)next).ToString(CultureInfo.InvariantCulture); syncing = false;
        }
        w.TextChanged += (_, _) => Sync(true); h.TextChanged += (_, _) => Sync(false);
        locked.Checked += (_, _) => Sync(true);
        var quality = Input("Export.Quality", "95", "Export.Quality");
        var background = Choose("Export.Background", ["transparent", "color"], "transparent", "Background", "Export.Background");
        var color = Input("Settings.BackgroundColor", "#FFFFFF", "Export.BackgroundColor");
        void Refresh()
        {
            quality.IsEnabled = Pick(format) != "png"; color.IsEnabled = Pick(background) == "color";
        }
        format.SelectionChanged += (_, _) => Refresh(); background.SelectionChanged += (_, _) => Refresh(); Refresh();
        Note("Export.Hint");
        Finish(() =>
        {
            var options = new VectorRasterOptions(Number(w, "Vector.Error.ExportSettings"), Number(h, "Vector.Error.ExportSettings"),
                Pick(format), Number(quality, "Vector.Error.ExportSettings"), Pick(background), color.Text.Trim());
            options.Validate(); Value = options; return true;
        }, "Export.ChooseFile");
    }
}
