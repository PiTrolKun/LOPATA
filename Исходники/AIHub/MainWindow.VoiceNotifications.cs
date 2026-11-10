using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using AIHub.Controls.VoiceNotifications;
using AIHub.Services.VoiceNotifications;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Slider = System.Windows.Controls.Slider;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub;

public partial class MainWindow
{
    private VoiceNotificationService? _voiceNotifications;
    private VoiceNotificationStore? _voiceNotificationStore;
    private VoiceNotificationSettingsControl? _voiceNotificationSettings;
    private VoiceStatusObserver? _voiceStatusObserver;
    private readonly ConcurrentQueue<VoiceEventRequest> _voicePending = new();
    private int _voiceDispatchScheduled;
    private Button? _voiceNotificationButton;
    private bool? _voiceIconEnabled;
    private bool _voiceBlinking;
    private Func<VoiceEventRequest, bool>? _voiceDelivery;

    private void InitializeVoiceNotifications()
    {
        _voiceNotificationStore = new();
        _voiceNotifications = new(_appSettings.VoiceNotifications, _voiceNotificationStore, Dispatcher);
        _voiceStatusObserver = new();
        _voiceDelivery = request => {
            if (_voiceNotifications is null || !_voiceNotifications.Settings.Enabled || Dispatcher.HasShutdownStarted) return false;
            if (Dispatcher.CheckAccess()) return _voiceNotifications.Publish(request);
            if (_voicePending.Count >= 1024) _voicePending.TryDequeue(out _);
            _voicePending.Enqueue(request);
            if (Interlocked.Exchange(ref _voiceDispatchScheduled, 1) == 0) Dispatcher.BeginInvoke(DrainVoiceNotifications);
            return true;
        };
        VoiceNotificationEvents.Deliver = _voiceDelivery;
        VoiceNotificationEvents.CanSpeak = (key, contexts) => _voiceNotifications?.CanSpeak(key, contexts) == true;
        VoiceNotificationEvents.KnowsKey = key => _voiceNotifications?.KnowsKey(key) == true;
        _voiceNotificationButton = new Button { Style = (Style)FindResource("ThemeButtonStyle"), Margin = new(0, 0, 10, 0) };
        AutomationProperties.SetAutomationId(_voiceNotificationButton, "VoiceNotifications.Toggle");
        _voiceNotificationButton.Click += (_, _) => { _appSettings.VoiceNotifications.Enabled = !_appSettings.VoiceNotifications.Enabled; SaveVoiceNotificationSettings(); };
        _voiceNotificationButton.ContextMenuOpening += (_, _) => BuildVoiceNotificationMenu();
        var voiceMenu = new ContextMenu();
        voiceMenu.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/AIHub;component/Controls/VoiceNotifications/VoiceNotificationMenuResources.xaml", UriKind.Relative) });
        _voiceNotificationButton.ContextMenu = voiceMenu;
        if (ThemeToggleButton.Parent is StackPanel header) header.Children.Insert(header.Children.IndexOf(ThemeToggleButton), _voiceNotificationButton);
        _voiceNotifications.Changed += RefreshVoiceNotificationUi;
        ComponentAudit.Changed += VoiceNotificationAuditChanged;
        RefreshVoiceNotificationUi();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(async () => {
            if (_voiceNotifications is { } voice) await voice.SetLanguageAsync(_localizationService.CurrentLanguageCode);
        }));
        Closed += (_, _) => {
            ComponentAudit.Changed -= VoiceNotificationAuditChanged;
            if (ReferenceEquals(VoiceNotificationEvents.Deliver, _voiceDelivery)) { VoiceNotificationEvents.Deliver = null; VoiceNotificationEvents.CanSpeak = null; VoiceNotificationEvents.KnowsKey = null; }
            _voiceStatusObserver?.Dispose(); _voiceNotifications?.Dispose(); _voiceNotifications = null;
            while (_voicePending.TryDequeue(out _)) { }
        };
    }
    private void DrainVoiceNotifications()
    {
        for (var count = 0; count < 64 && _voicePending.TryDequeue(out var request); count++) _voiceNotifications?.Publish(request);
        Interlocked.Exchange(ref _voiceDispatchScheduled, 0);
        if (!_voicePending.IsEmpty && !Dispatcher.HasShutdownStarted && Interlocked.Exchange(ref _voiceDispatchScheduled, 1) == 0)
            Dispatcher.BeginInvoke(DrainVoiceNotifications);
    }
    private void VoiceNotificationAuditChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(async () => {
            if (_voiceNotifications is { } voice) await voice.SetLanguageAsync(_localizationService.CurrentLanguageCode);
        });
    }
    private FrameworkElement CreateVoiceNotificationSettings()
    {
        _voiceNotificationSettings = new(_voiceNotifications!, L, SaveVoiceNotificationSettings,
            () => _ = ImportVoiceNotificationPackageAsync(), () => _ = ExportVoiceNotificationTextsAsync());
        return _settingsCards!.Card(_voiceNotificationSettings);
    }
    private void SaveVoiceNotificationSettings()
    { _appSettingsStore.Save(_appSettings); _voiceNotifications?.Configure(); RefreshVoiceNotificationUi(); }
    private void RefreshVoiceNotificationUi()
    {
        if (_voiceNotificationButton is not { } button || _voiceNotifications is not { } service) return;
        var enabled = service.Settings.Enabled;
        if (_voiceIconEnabled != enabled)
        { button.Content = new Image { Source = VoiceNotificationIcon.Create(enabled), Width = 33, Height = 30 }; _voiceIconEnabled = enabled; }
        AutomationProperties.SetName(button, L(enabled ? "VoiceNotifications.TurnOff" : "VoiceNotifications.TurnOn"));
        var blink = enabled && service.PendingCount > 2;
        if (blink != _voiceBlinking)
        {
            _voiceBlinking = blink;
            button.BeginAnimation(OpacityProperty, blink ? new DoubleAnimation(1, .65, TimeSpan.FromMilliseconds(800))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever } : null);
        }
        _voiceNotificationSettings?.Refresh();
    }
    private void BuildVoiceNotificationMenu()
    {
        if (_voiceNotificationButton?.ContextMenu is not { } menu) return;
        menu.Items.Clear(); var volume = new MenuItem { Header = L("VoiceNotifications.Volume") };
        var slider = new Slider { Minimum = 0, Maximum = 1, Width = 160, Value = _appSettings.VoiceNotifications.Volume,
            IsMoveToPointEnabled = true, Margin = new(8) };
        AutomationProperties.SetAutomationId(slider, "VoiceNotifications.HeaderVolume");
        slider.ValueChanged += (_, _) => { _appSettings.VoiceNotifications.Volume = slider.Value; SaveVoiceNotificationSettings(); };
        volume.Items.Add(new MenuItem { Header = slider, StaysOpenOnClick = true }); menu.Items.Add(volume);
        var settings = new MenuItem { Header = L("VoiceNotifications.Settings") };
        settings.Click += (_, _) => { ShowSettingsPage(); SettingsNavigator.RestoreSection("voice"); }; menu.Items.Add(settings);
    }
    private void LocalizeVoiceNotifications()
    {
        _voiceNotificationSettings?.Localize();
        RefreshVoiceNotificationUi();
        if (_voiceNotifications is { } voice && voice.Language != _localizationService.CurrentLanguageCode)
        { while (_voicePending.TryDequeue(out _)) { } _ = voice.SetLanguageAsync(_localizationService.CurrentLanguageCode); }
    }
}
