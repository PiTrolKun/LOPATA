using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VoiceNotifications;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using Slider = System.Windows.Controls.Slider;

namespace AIHub.Controls.VoiceNotifications;

internal sealed class VoiceNotificationSettingsControl : StackPanel
{
    private readonly SettingsCardFactory _factory;
    private readonly VoiceNotificationService _service;
    private readonly Func<string, string> _l;
    private readonly Action _save;
    private readonly CheckBox _enabled, _errors, _statuses, _details;
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, TickFrequency = .05, IsMoveToPointEnabled = true, Width = 190 };
    private readonly Slider _speed = new() { Minimum = .75, Maximum = 1.25, TickFrequency = .05, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, Width = 190 };
    private readonly TextBlock _volumeValue = new(), _speedValue = new(), _status = new();
    private readonly System.Windows.Controls.Button _example;
    private bool _refreshing;
    private string? _localizedTitle;
    internal VoiceNotificationSettingsControl(VoiceNotificationService service, Func<string, string> localize, Action save, Action import, Action export)
    {
        _service = service; _l = localize; _save = save; _factory = new(localize);
        AutomationProperties.SetAutomationId(this, "VoiceNotifications.Settings");
        Children.Add(_factory.Label("VoiceNotifications.Title", true));
        Children.Add(_factory.Label("VoiceNotifications.Help"));
        _enabled = Switch("Enabled", value => service.Settings.Enabled = value);
        _errors = Switch("Errors", value => service.Settings.Errors = value);
        _statuses = Switch("Statuses", value => service.Settings.Statuses = value);
        _details = Switch("Details", value => service.Settings.Details = value);
        Children.Add(SliderRow("Volume", _volume, _volumeValue));
        Children.Add(SliderRow("Speed", _speed, _speedValue));
        _volume.ValueChanged += (_, _) => Change(() => service.Settings.Volume = _volume.Value);
        _speed.ValueChanged += (_, _) => Change(() => service.Settings.Speed = _speed.Value);
        AutomationProperties.SetAutomationId(_volume, "VoiceNotifications.Volume");
        AutomationProperties.SetAutomationId(_speed, "VoiceNotifications.Speed");
        _example = _factory.Action("VoiceNotifications.Example", "VoiceNotifications.Example", (_, _) =>
            service.Publish(new("Tray.ResultReady", "notification-example", [], Family: "example")));
        Children.Add(_example);
        Children.Add(_factory.Action("VoiceNotifications.Import", "VoiceNotifications.Import", (_, _) => import()));
        Children.Add(_factory.Action("VoiceNotifications.ExportTexts", "VoiceNotifications.ExportTexts", (_, _) => export()));
        _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new(0, 10, 0, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(_status, "VoiceNotifications.PackageStatus"); Children.Add(_status);
        Refresh();
    }
    private CheckBox Switch(string key, Action<bool> set)
    {
        var control = _factory.Switch("VoiceNotifications." + key, "VoiceNotifications." + key, value => Change(() => set(value)));
        Children.Add(control); return control;
    }
    private StackPanel SliderRow(string key, Slider slider, TextBlock value)
    {
        var panel = new StackPanel { Margin = new(0, 5, 0, 8) }; panel.Children.Add(_factory.Label("VoiceNotifications." + key));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        value.Margin = new(10, 0, 0, 0); value.VerticalAlignment = VerticalAlignment.Center;
        value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); row.Children.Add(slider); row.Children.Add(value); panel.Children.Add(row); return panel;
    }
    private void Change(Action set)
    { if (_refreshing) return; set(); _save(); _service.Configure(); Refresh(); }
    internal void Localize() { _localizedTitle = null; Refresh(); }
    internal void Refresh()
    {
        _refreshing = true;
        try
        {
            var title = _l("VoiceNotifications.Title");
            if (_localizedTitle != title) { _factory.RefreshLocalization(); _localizedTitle = title; }
            var settings = _service.Settings;
            _enabled.IsChecked = settings.Enabled; _errors.IsChecked = settings.Errors; _statuses.IsChecked = settings.Statuses; _details.IsChecked = settings.Details;
            _volume.Value = settings.Volume; _speed.Value = settings.Speed;
            _volumeValue.Text = Math.Round(settings.Volume * 100).ToString(CultureInfo.CurrentCulture) + "%";
            _speedValue.Text = settings.Speed.ToString("0.00", CultureInfo.CurrentCulture) + "×";
            _example.IsEnabled = _service.CanSpeak("Tray.ResultReady", []);
            _status.Text = _service.PackageError is { } key ? _l(key) : _service.Ready ? _l("VoiceNotifications.PackageReady") : _l("VoiceNotifications.Preparing");
        }
        finally { _refreshing = false; }
    }
}
