using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoUpscale;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    private static readonly (string Name, int Width, int Height)[] Resolutions = [
        ("SD", 854, 480), ("HD", 1280, 720), ("Full HD", 1920, 1080),
        ("QHD", 2560, 1440), ("4K UHD", 3840, 2160), ("8K UHD", 7680, 4320)
    ];
    private void ResolutionPicker(Panel panel, UpscaleStep step, bool manual)
    {
        panel.Children.Add(MusicWishUi.Text(T("Resolution"), true));
        var options = new List<UpscaleSettingOption>();
        foreach (var vertical in new[] { false, true }) foreach (var resolution in Resolutions) {
            var w = vertical ? resolution.Height : resolution.Width; var h = vertical ? resolution.Width : resolution.Height;
            options.Add(new(w + "x" + h, resolution.Name, T(vertical ? "Portrait" : "Landscape") + Environment.NewLine + $"{w} × {h}", w / (double)h));
        }
        var current = step.Width + "x" + step.Height;
        if (options.All(o => o.Value != current)) options.Add(new(current, T("CurrentSize"), $"{step.Width} × {step.Height}", step.Width / (double)step.Height));
        var custom = new StackPanel(); Panel? selector = null;
        selector = UpscaleSettingControls.Choices("VideoUpscale.Resolution", options, current, value => {
            var pair = value.Split('x'); step.Width = int.Parse(pair[0], CultureInfo.InvariantCulture); step.Height = int.Parse(pair[1], CultureInfo.InvariantCulture); Save();
            var fields = custom.Children.OfType<System.Windows.Controls.TextBox>().ToArray();
            if (fields.Length == 2) { fields[0].Text = step.Width.ToString(); fields[1].Text = step.Height.ToString(); }
        }, true); panel.Children.Add(selector);
        panel.Children.Add(MusicWishUi.Text(T("Resolution.Help")));
        if (manual) {
            Field(custom, "Width", step.Width.ToString(), text => { step.Width = Dimension(text); UpscaleSettingControls.Select(selector, step.Width + "x" + step.Height); }, () => step.Width.ToString());
            Field(custom, "Height", step.Height.ToString(), text => { step.Height = Dimension(text); UpscaleSettingControls.Select(selector, step.Width + "x" + step.Height); }, () => step.Height.ToString());
            panel.Children.Add(new Expander { Header = T("CustomSize"), Content = custom, Margin = new(0, 4, 0, 8) });
        }
        static int Dimension(string text) { var number = int.Parse(text, CultureInfo.InvariantCulture); return number is >= 128 and <= 16384 && number % 2 == 0 ? number : throw new FormatException(); }
    }
    private Panel EnginePicker(UpscaleEngine current, Action<UpscaleEngine> changed) => UpscaleSettingControls.Choices("VideoUpscale.EngineChoice",
        Enum.GetValues<UpscaleEngine>().Select(engine => new UpscaleSettingOption(engine.ToString(), T("Engine." + engine), T("Engine." + engine + ".Help"))), current.ToString(), value => changed(Enum.Parse<UpscaleEngine>(value)));
    private void Level(Panel parent, string key, double value, double min, double max, double increment, Action<double> changed, bool percent = false)
    {
        parent.Children.Add(UpscaleSettingControls.Range("VideoUpscale." + key, T(key), value, min, max, increment, number => { if (_busy) return; changed(number); Save(); }, percent));
    }
    private void Parameter(Panel parent, UpscaleStep step, UpscaleParameter field, bool manual, Action rebuild)
    {
        var row = new StackPanel(); parent.Children.Add(row);
        var key = "Param." + field.Key;
        if (field.Choices is {} choices) Choice(row, key, choices, step.Get(field.Key, field.Default), value => step.Parameters[field.Key] = value);
        else if (field.Min is {} min && field.Max is {} max && field.Key != "seed") {
            var integer = field.Key is "decode_every" or "ffn_chunk_tokens" or "color_correction_levels" or "color_correction_chunk_size" or "nr_passes" or "upscale_vsr_quality" or "vfx_vsr_mode";
            Level(row, key, UpscaleCatalog.Number(step, field.Key, field.Default), min, max, field.Key == "decode_every" ? 4 : integer ? 1 : .01,
                value => step.Parameters[field.Key] = value.ToString("0.###", CultureInfo.InvariantCulture));
        } else Field(row, key, step.Get(field.Key, field.Default), text => step.Parameters[field.Key] = text);
        parent.Children.Add(MusicWishUi.Text(T(key + ".Help") + (field.Min is null ? "" : $" [{field.Min}–{field.Max}]")));
        if (manual) Add(parent, "ResetParameter", () => { step.Parameters.Remove(field.Key); Save(); rebuild(); });
    }
    private void Choice(Panel parent, string key, string[] values, string current, Action<string> changed)
    {
        parent.Children.Add(MusicWishUi.Text(T(key), true));
        parent.Children.Add(UpscaleSettingControls.Choices("VideoUpscale." + key, values.Select(value => {
            var label = key == "Algorithm" ? T("Algorithm." + value) : key == "Codec" ? T("Codec." + value) : ChoiceLabel(value);
            var helpKey = key + "." + value + ".Help"; var help = T(helpKey);
            return new UpscaleSettingOption(value, label, help == "VideoUpscale." + helpKey ? "" : help);
        }), current, value => { if (_busy) return; changed(value); Save(); }));
    }
    private string ChoiceLabel(string value)
    {
        var translated = T("Choice." + value); return translated == "VideoUpscale.Choice." + value ? value : translated;
    }
    private void RemoveStep(UpscaleStep step) { if (_busy) return; _project.Steps.Remove(step); RefreshSteps(); Save(); }
}
