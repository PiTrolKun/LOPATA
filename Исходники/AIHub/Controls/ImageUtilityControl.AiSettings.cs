using System.Globalization;
using System.Windows.Controls;
using ListBox = System.Windows.Controls.ListBox;
using SelectionMode = System.Windows.Controls.SelectionMode;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class ImageUtilityControl
{
    private void RenderCompactAiSettings(StackPanel panel)
    {
        var id = Options.MethodId;
        foreach (var setting in ImageUtilityAiCatalog.Settings(id))
        {
            if (setting.Key is "device" or "tile" or "threads" or "overlap") continue;
            var choices = SupportedAiChoices(setting.Key, setting.Choices ?? []);
            if (setting.Key == "tta") Check(panel, setting.NameKey, Parameter("tta", "false") == "true", x => SetParameter("tta", x ? "true" : "false"));
            else if (choices.Length == 1)
            {
                var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L(setting.NameKey)));
                group.Children.Add(ImageUtilityUi.Text(AiChoiceName(setting.Key, choices[0])));
            }
            else Choice(panel, setting.NameKey, Parameter(setting.Key, setting.DefaultValue),
                choices.Select(x => new ImageUtilityUi.Choice(x, AiChoiceName(setting.Key, x))).ToArray(), x =>
                {
                    SetParameter(setting.Key, x); NormalizeAiChoices(); RenderMethodSettings();
                });
            SettingHint(panel, setting.DescriptionKey);
        }
        RenderAiDevices(panel);
    }

    private string AiChoiceName(string key, string value)
    {
        if (key == "noise") return L("Noise." + value);
        if (key == "syncgap") return L("Sync." + value);
        if (key == "scale") return "×" + value;
        return L("Ai.Value." + value);
    }

    private string[] SupportedAiChoices(string key, string[] original)
    {
        var model = Parameter("model", Options.MethodId == "real-cugan" ? "models-se" : "realesrgan-x4plus");
        var scale = Parameter("scale", "2");
        if (Options.MethodId == "real-esrgan" && key == "scale" && model != "realesr-animevideov3") return ["4"];
        if (Options.MethodId != "real-cugan") return original;
        if (key == "scale") return model switch { "models-nose" => ["2"], "models-pro" => ["2", "3"], _ => original };
        if (key == "noise") return model == "models-nose" ? ["0"] : model == "models-pro" || scale != "2" ? ["-1", "0", "3"] : original;
        if (key == "syncgap" && model == "models-nose") return ["0"];
        return original;
    }

    private void NormalizeAiChoices()
    {
        foreach (var setting in ImageUtilityAiCatalog.Settings(Options.MethodId).Where(x => x.Choices is not null))
        {
            var allowed = SupportedAiChoices(setting.Key, setting.Choices!);
            if (!allowed.Contains(Parameter(setting.Key, setting.DefaultValue))) Options.Parameters[setting.Key] = allowed[0];
        }
        SavePreferences();
    }

    private void RenderAiDevices(StackPanel panel)
    {
        var swinir = Options.MethodId == "swinir";
        var devices = Parameter("device", "auto").Split(',');
        var deviceKey = swinir ? "Ai.SwinirDevice" : "Ai.NcnnDevice";
        var group = SettingGroup(panel); group.Children.Add(ImageUtilityUi.Text(L(deviceKey)));
        var button = ActionButton("SelectDevices", () => SelectAiDevices(swinir));
        button.Content = devices[0] == "auto" ? L("Ai.Value.auto") + " ▾" : string.Join(", ", devices.Select(DeviceLabel)) + " ▾";
        group.Children.Add(button); SettingHint(panel, deviceKey + "Help");
        var tileValues = Parameter("tile", swinir ? "256" : "0").Split(',');
        var threadGroups = Parameter("threads", "1:2:2").Split(':');
        for (var index = 0; index < (swinir ? 1 : devices.Length); index++)
        {
            var slot = index;
            var raw = tileValues.ElementAtOrDefault(index) ?? "0";
            var key = swinir ? "Ai.SwinirTile" : "Ai.NcnnTile";
            var value = int.TryParse(raw, out var parsed) ? parsed : swinir ? 256 : 0;
            Check(panel, swinir ? "WholeImage" : "AutomaticTile", value == 0, selected =>
            { SetTile(slot, selected ? 0 : swinir ? 256 : 128, devices.Length); RenderMethodSettings(); });
            if (value > 0)
            {
                Number(panel, key, value.ToString(CultureInfo.InvariantCulture), key + "Help", swinir ? 8 : 32, 32768,
                    x => { var tile = int.Parse(x, CultureInfo.InvariantCulture); tile = swinir ? Math.Max(8, tile / 8 * 8) : tile; SetTile(slot, tile, devices.Length);
                        if (swinir && int.Parse(Parameter("overlap", "32"), CultureInfo.InvariantCulture) >= tile) { SetParameter("overlap", Math.Max(0, tile - 1).ToString(CultureInfo.InvariantCulture)); RenderMethodSettings(); } }, true);
            }
            else SettingHint(panel, key + "Help");
            if (devices.Length > 1) SettingHintText(panel, DeviceLabel(devices[index]));
            if (!swinir)
            {
                var processThreads = threadGroups.ElementAtOrDefault(1)?.Split(',') ?? ["2"];
                Number(panel, "ProcessingThreads", processThreads.ElementAtOrDefault(index) ?? "2", "Ai.ThreadsHelp", 1, 256,
                    x => { var parts = Parameter("threads", "1:2:2").Split(':'); var values = Enumerable.Range(0, devices.Length).Select(i => parts[1].Split(',').ElementAtOrDefault(i) ?? "2").ToArray(); values[slot] = x;
                        SetParameter("threads", parts[0] + ":" + string.Join(',', values) + ":" + parts[2]); }, true);
            }
        }
        if (swinir)
        {
            var tile = int.Parse(Parameter("tile", "256"), CultureInfo.InvariantCulture);
            if (tile > 0) Number(panel, "Ai.Overlap", Parameter("overlap", "32"), "Ai.OverlapHelp", 0, tile - 1, x => SetParameter("overlap", x), true);
        }
        else
        {
            Number(panel, "ReadThreads", threadGroups[0], "Ai.ThreadsHelp", 1, 256, x => UpdateThreadEnd(0, x), true);
            Number(panel, "WriteThreads", threadGroups[2], "Ai.ThreadsHelp", 1, 256, x => UpdateThreadEnd(2, x), true);
        }
    }
    private void UpdateThreadEnd(int index, string value) { var parts = Parameter("threads", "1:2:2").Split(':'); parts[index] = value; SetParameter("threads", string.Join(':', parts)); }
    private void SetTile(int index, int value, int count)
    {
        var old = Parameter("tile", "0").Split(',');
        var values = Enumerable.Range(0, count).Select(i => old.ElementAtOrDefault(i) ?? "0").ToArray();
        values[index] = value.ToString(CultureInfo.InvariantCulture); SetParameter("tile", string.Join(',', values));
    }
    private string DeviceLabel(string value) => value is "cpu" or "-1" ? L("Cpu") : value == "auto" ? L("Ai.Value.auto")
        : value.StartsWith("hip", StringComparison.Ordinal) ? "AMD GPU " + (value.Split(':').ElementAtOrDefault(1) ?? "0")
        : value.StartsWith("xpu", StringComparison.Ordinal) ? "Intel GPU " + (value.Split(':').ElementAtOrDefault(1) ?? "0")
        : value.StartsWith("cuda", StringComparison.Ordinal) ? "NVIDIA GPU " + (value.Split(':').ElementAtOrDefault(1) ?? "0") : "GPU " + value;
    private void SettingHintText(StackPanel panel, string text) { if (_lastSettingGroups.TryGetValue(panel, out var group)) group.Children.Add(ImageUtilityUi.Text(text)); }

    private void SelectAiDevices(bool swinir)
    {
        var window = new System.Windows.Window { Title = L("SelectDevices"), Owner = System.Windows.Window.GetWindow(this), Width = 380, Height = 420, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(16) };
        var list = new ListBox { SelectionMode = swinir ? SelectionMode.Single : SelectionMode.Multiple, Height = 260 };
        var choices = new List<ImageUtilityUi.Choice> { new("auto", L("Ai.Value.auto")) };
        if (swinir || Options.MethodId == "real-cugan") choices.Add(new(swinir ? "cpu" : "-1", L("Cpu")));
        list.ItemsSource = choices;
        var current = Parameter("device", "auto").Split(',');
        if (swinir) list.SelectedItem = choices.FirstOrDefault(x => current.Contains(x.Id)) ?? choices[0];
        else foreach (var choice in choices.Where(x => current.Contains(x.Id))) list.SelectedItems.Add(choice);
        panel.Children.Add(list);
        panel.Children.Add(ImageUtilityUi.Text(L(swinir ? "Ai.SwinirDeviceHelp" : "DeviceIndexHint")));
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        {
            var checking = ImageUtilityUi.Text(L("Ai.ProbingDevices")); panel.Children.Add(checking);
            window.Closed += (_, _) => cancellation.Cancel();
            window.Loaded += async (_, _) =>
            {
                try
                {
                    if (swinir)
                    {
                        var devices = await ManagedPythonRuntime.ListGpuDevicesAsync(token);
                        choices.AddRange(devices.Select(device => new ImageUtilityUi.Choice(device.Device,
                            DeviceLabel(device.Device) + " · " + device.Name)));
                    }
                    else
                    {
                        var devices = await _ai.ListNativeDevicesAsync(Options.MethodId, token);
                        choices.AddRange(devices.Select(device => new ImageUtilityUi.Choice(
                            device.Index.ToString(CultureInfo.InvariantCulture), "GPU " + device.Index + " · " + device.Name)));
                    }
                    token.ThrowIfCancellationRequested();
                    list.ItemsSource = null; list.ItemsSource = choices;
                    if (swinir) list.SelectedItem = choices.FirstOrDefault(choice => current.Contains(choice.Id)) ?? choices[0];
                    else foreach (var choice in choices.Where(choice => current.Contains(choice.Id))) list.SelectedItems.Add(choice);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) { if (!token.IsCancellationRequested) Error(error); }
                finally { checking.Visibility = System.Windows.Visibility.Collapsed; }
            };
        }
        panel.Children.Add(ImageUtilityUi.Button(L("Apply"), "ApplyDevices", () =>
        {
            var selected = list.SelectedItems.Cast<ImageUtilityUi.Choice>().Select(x => x.Id).ToArray();
            if (selected.Length == 0) return Task.CompletedTask;
            if (selected.Contains("auto")) selected = ["auto"];
            if (selected.Contains("-1")) selected = ["-1"];
            SetParameter("device", string.Join(',', selected));
            if (!swinir) { SetParameter("tile", string.Join(',', selected.Select(_ => "0"))); SetParameter("threads", "1:" + string.Join(',', selected.Select(_ => "2")) + ":2"); }
            window.Close(); RenderMethodSettings(); return Task.CompletedTask;
        }, Error));
        window.SetResourceReference(System.Windows.Window.BackgroundProperty, "PanelBrush");
        window.Content = panel; window.ShowDialog();
    }
}
