using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using Forms = System.Windows.Forms;

namespace AIHub;

public partial class MainWindow
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private double _effectiveTypographyScale = 1;
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;

    private void InitializeInterfaceSettings()
    {
        _appSettings.Interface ??= new InterfaceSettings();
        _appSettings.Interface.LastWindowPlacement ??= new RememberedWindowPlacement();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                _lastNonMinimizedWindowState = WindowState;
            }
        };
        ApplyInterfaceResources();
    }

    private void LoadInterfaceSettingsIntoControls()
    {
        _isApplyingInterfaceSettings = true;
        try
        {
            InterfaceTextScaleSlider.Value = _appSettings.Interface.TextScalePercent;
            PopulateWindowStartupModeComboBox();
            UpdateInterfaceTextScaleValue();
            UpdateWindowStartupHelp();
        }
        finally
        {
            _isApplyingInterfaceSettings = false;
        }

    }

    private void ApplyInterfaceLocalization()
    {
        if (SettingsInterfaceTitleText is null)
        {
            return;
        }

        SettingsInterfaceTitleText.Text = L("Settings.InterfaceTitle");
        SettingsInterfaceHelpText.Text = L("Settings.InterfaceHelp");
        SettingsTextSizeText.Text = L("Settings.TextSize");
        InterfaceTextScalePreviewText.Text = L("Settings.TextSizePreview");
        ResetInterfaceTextScaleButton.Content = L("Settings.TextSizeReset");
        SettingsWindowStartupText.Text = L("Settings.WindowStartup");
        PopulateWindowStartupModeComboBox();
        UpdateWindowStartupHelp();
    }

    private void PopulateWindowStartupModeComboBox()
    {
        if (WindowStartupModeComboBox is null)
        {
            return;
        }

        var wasApplying = _isApplyingInterfaceSettings;
        _isApplyingInterfaceSettings = true;
        try
        {
            var options = new[]
            {
                new WindowStartupModeOption(
                    WindowStartupModes.RememberLast,
                    L("Settings.WindowStartupRemember")),
                new WindowStartupModeOption(
                    WindowStartupModes.Maximized,
                    L("Settings.WindowStartupMaximized")),
                new WindowStartupModeOption(
                    WindowStartupModes.HalfScreen,
                    L("Settings.WindowStartupHalfScreen"))
            };
            WindowStartupModeComboBox.ItemsSource = options;
            WindowStartupModeComboBox.SelectedItem = options.First(option =>
                string.Equals(
                    option.Id,
                    _appSettings.Interface.WindowStartupMode,
                    StringComparison.Ordinal));
        }
        finally
        {
            _isApplyingInterfaceSettings = wasApplying;
        }
    }

    private void InterfaceTextScaleSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingInterfaceSettings)
        {
            return;
        }

        _appSettings.Interface.TextScalePercent =
            (int)Math.Round(InterfaceTextScaleSlider.Value / 5) * 5;
        _appSettingsStore.Save(_appSettings);
        ApplyInterfaceResources();
        UpdateInterfaceTextScaleValue();
        StatusText.Text = LF(
            "Status.TextSizeSaved",
            _appSettings.Interface.TextScalePercent);
    }

    private void ResetInterfaceTextScaleButton_Click(object sender, RoutedEventArgs e)
    {
        InterfaceTextScaleSlider.Value = InterfaceSettings.DefaultTextScalePercent;
    }

    private void WindowStartupModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isApplyingInterfaceSettings
            || WindowStartupModeComboBox.SelectedItem is not WindowStartupModeOption option)
        {
            return;
        }

        _appSettings.Interface.WindowStartupMode = option.Id;
        _appSettingsStore.Save(_appSettings);
        UpdateWindowStartupHelp();
        StatusText.Text = LF("Status.WindowStartupSaved", option.DisplayName);
    }

    private void UpdateInterfaceTextScaleValue()
    {
        if (InterfaceTextScaleValueText is not null)
        {
            InterfaceTextScaleValueText.Text =
                $"{_appSettings.Interface.TextScalePercent}%";
        }
    }

    private void UpdateWindowStartupHelp()
    {
        if (SettingsWindowStartupHelpText is null)
        {
            return;
        }

        SettingsWindowStartupHelpText.Text = _appSettings.Interface.WindowStartupMode switch
        {
            WindowStartupModes.Maximized => L("Settings.WindowStartupMaximizedHelp"),
            WindowStartupModes.HalfScreen => L("Settings.WindowStartupHalfScreenHelp"),
            _ => L("Settings.WindowStartupRememberHelp")
        };
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyInterfaceResources();

    private void ApplyInterfaceResources()
    {
        if (!IsInitialized)
        {
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var sizeClass = _interfaceLayoutService.GetSizeClass(width, height);
        var userScale = _appSettings.Interface.TextScalePercent / 100d;
        var automaticScale = _interfaceLayoutService.GetAutomaticTypographyScale(width, height);
        _effectiveTypographyScale = userScale * automaticScale;

        SetScaledResource("UiBodyFontSize", 14);
        SetScaledResource("UiSmallFontSize", 12);
        SetScaledResource("UiTinyFontSize", 11);
        SetScaledResource("UiSectionFontSize", 18);
        SetScaledResource("UiCardTitleFontSize", 20);
        SetScaledResource("UiPageTitleFontSize", 32);
        SetScaledResource("UiHeaderTitleFontSize", 18);
        SetScaledResource("UiHeaderSubtitleFontSize", 12);
        SetScaledResource("UiButtonFontSize", 14);
        SetScaledResource("UiFont10", 10);
        SetScaledResource("UiFont10_5", 10.5);
        SetScaledResource("UiFont11", 11);
        SetScaledResource("UiFont12", 12);
        SetScaledResource("UiFont13", 13);
        SetScaledResource("UiFont14", 14);
        SetScaledResource("UiFont15", 15);
        SetScaledResource("UiFont16", 16);
        SetScaledResource("UiFont17", 17);
        SetScaledResource("UiFont18", 18);
        SetScaledResource("UiFont19", 19);
        SetScaledResource("UiFont20", 20);
        SetScaledResource("UiFont21", 21);
        SetScaledResource("UiFont22", 22);
        SetScaledResource("UiFont26", 26);
        SetScaledResource("UiFont30", 30);
        SetScaledResource("UiFont32", 32);
        SetScaledResource("UiFont34", 34);
        SetScaledResource("UiLineHeight16", 16);
        SetScaledResource("UiLineHeight17", 17);
        SetScaledResource("UiLineHeight18", 18);
        SetScaledResource("UiLineHeight19", 19);
        SetScaledResource("UiLineHeight20", 20);
        SetScaledResource("UiLineHeight21", 21);
        SetScaledResource("UiLineHeight22", 22);
        SetScaledResource("UiLineHeight24", 24);

        var controlScale = Math.Max(1, Math.Min(_effectiveTypographyScale, 1.4));
        Resources["UiButtonHeight"] = 40d * controlScale;
        Resources["UiThemeButtonSize"] = 42d * controlScale;
        Resources["UiBundleCardMinHeight"] = 408d * Math.Min(controlScale, 1.22);
        Resources["UiPreviousWorkMaxHeight"] = sizeClass switch
        {
            InterfaceSizeClass.Compact => 260d,
            InterfaceSizeClass.Wide => 520d,
            _ => 360d
        };
        Resources["UiPrimaryButtonMinWidth"] = 156d * Math.Min(controlScale, 1.25);
        Resources["UiSecondaryButtonMinWidth"] = 96d * Math.Min(controlScale, 1.25);
        HeaderRow.Height = new GridLength(64d * controlScale);

        var (pageMargin, imageMargin, panelPadding, cardPadding, contentWidth, bundleWidth) =
            sizeClass switch
            {
                InterfaceSizeClass.Compact => (
                    new Thickness(24, 20, 24, 18),
                    new Thickness(20, 16, 20, 18),
                    new Thickness(16),
                    new Thickness(18),
                    1160d,
                    1450d),
                InterfaceSizeClass.Wide => (
                    new Thickness(72, 48, 72, 42),
                    new Thickness(64, 36, 64, 38),
                    new Thickness(24),
                    new Thickness(26),
                    1760d,
                    2100d),
                _ => (
                    new Thickness(56, 38, 56, 32),
                    new Thickness(42, 26, 42, 28),
                    new Thickness(20),
                    new Thickness(24),
                    1480d,
                    1900d)
            };

        Resources["UiPageMargin"] = pageMargin;
        Resources["UiImagePageMargin"] = imageMargin;
        Resources["UiPanelPadding"] = panelPadding;
        Resources["UiCardPadding"] = cardPadding;
        Resources["UiMainContentMaxWidth"] = contentWidth;
        Resources["UiBundleContentMaxWidth"] = bundleWidth;
        WorkStartContentRow.Height = sizeClass == InterfaceSizeClass.Wide || height >= 850
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        ImageAnalysisBundleSelectorPage?.UpdateResponsiveLayout(width);
    }

    private void SetScaledResource(string key, double baseValue) =>
        Resources[key] = baseValue * _effectiveTypographyScale;

    private void ApplyWindowStartupPreference()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var settings = _appSettings.Interface;
        var remembered = settings.LastWindowPlacement;
        var screen = settings.WindowStartupMode == WindowStartupModes.RememberLast
            ? FindRememberedScreen(remembered.MonitorDeviceName, handle)
            : Forms.Screen.FromHandle(handle);
        var workArea = ToRect(screen.WorkingArea);
        var dpi = VisualTreeHelper.GetDpi(this);
        var minimumWidth = MinWidth * dpi.DpiScaleX;
        var minimumHeight = MinHeight * dpi.DpiScaleY;

        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;

        if (settings.WindowStartupMode == WindowStartupModes.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        var bounds = settings.WindowStartupMode == WindowStartupModes.HalfScreen
            ? _interfaceLayoutService.CalculateHalfScreenBounds(
                workArea,
                minimumWidth,
                minimumHeight)
            : _interfaceLayoutService.CalculateRememberedBounds(
                remembered,
                workArea,
                minimumWidth,
                minimumHeight);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(bounds.Left),
            (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            SwpNoZOrder | SwpNoActivate);

        if (settings.WindowStartupMode == WindowStartupModes.RememberLast
            && remembered.WasMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!LiteraryPage.CanLeave()) { e.Cancel = true; base.OnClosing(e); return; }
        SaveCurrentWindowPlacement();
        base.OnClosing(e);
    }

    private void SaveCurrentWindowPlacement()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null)
            {
                return;
            }

            var normalBoundsPixels = RestoreBounds;
            normalBoundsPixels.Transform(source.CompositionTarget.TransformToDevice);
            var center = new System.Drawing.Point(
                (int)Math.Round(normalBoundsPixels.Left + (normalBoundsPixels.Width / 2)),
                (int)Math.Round(normalBoundsPixels.Top + (normalBoundsPixels.Height / 2)));
            var screen = Forms.Screen.FromPoint(center);
            _appSettings.Interface.LastWindowPlacement = _interfaceLayoutService.CapturePlacement(
                normalBoundsPixels,
                ToRect(screen.WorkingArea),
                screen.DeviceName,
                WindowState == WindowState.Maximized
                || (WindowState == WindowState.Minimized
                    && _lastNonMinimizedWindowState == WindowState.Maximized));
            _appSettingsStore.Save(_appSettings);
        }
        catch
        {
            // Закрытие приложения не должно блокироваться из-за сохранения геометрии окна.
        }
    }

    private static Forms.Screen FindRememberedScreen(string deviceName, IntPtr handle) =>
        Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
        ?? Forms.Screen.FromHandle(handle)
        ?? Forms.Screen.PrimaryScreen
        ?? Forms.Screen.AllScreens[0];

    private static Rect ToRect(System.Drawing.Rectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
