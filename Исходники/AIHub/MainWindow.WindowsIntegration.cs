using System.Reflection;
using System.Windows;
using AIHub.Services;
using AIHub.Services.Books;

namespace AIHub;

public partial class MainWindow
{
    private System.Windows.Controls.CheckBox? _imageShellIntegrationSwitch;
    private System.Windows.Controls.CheckBox? _bookShellIntegrationSwitch;
    private bool _imageShellIntegrationInitialized;
    private string? _imageShellIntegrationLanguage;

    private ImageShellIntegration? ImageShellRegistration()
    {
        // UI render tests and tool hosts must never register their own testhost/dotnet executable.
        if (Assembly.GetEntryAssembly()?.GetName().Name != typeof(MainWindow).Assembly.GetName().Name) return null;
        return Environment.ProcessPath is { Length: > 0 } executable ? new ImageShellIntegration(executable) : null;
    }
    private AudioShellIntegration? AudioShellRegistration()
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name != typeof(MainWindow).Assembly.GetName().Name) return null;
        return Environment.ProcessPath is { Length: > 0 } executable ? new AudioShellIntegration(executable) : null;
    }
    private BookShellIntegration? BookShellRegistration()
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name != typeof(MainWindow).Assembly.GetName().Name) return null;
        return Environment.ProcessPath is { Length: > 0 } executable ? new BookShellIntegration(executable) : null;
    }
    private void ApplyImageShellIntegration()
    {
        _imageShellIntegrationInitialized = true;
        try
        {
            ImageShellRegistration()?.Apply(_appSettings.Behavior.ImageShellIntegrationEnabled,
                L("WindowsIntegration.Menu"), L("WindowsIntegration.WebP"), L("WindowsIntegration.Upscale2"));
            AudioShellRegistration()?.Apply(_appSettings.Behavior.ImageShellIntegrationEnabled,
                L("WindowsIntegration.Menu"), L("AudioShell.Title"));
            _imageShellIntegrationLanguage = _appSettings.LanguageCode;
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("image_shell_registration_failed", "WindowsIntegration", detail: error.GetType().Name);
            StatusText.Text = L("WindowsIntegration.Failed");
        }
        ApplyBookShellIntegration();
    }
    private void SetImageShellIntegration(bool enabled)
    {
        if (_refreshingSettingsWorkspace) return;
        var previous = _appSettings.Behavior.ImageShellIntegrationEnabled;
        var registration = ImageShellRegistration();
        try
        {
            registration?.Apply(enabled, L("WindowsIntegration.Menu"), L("WindowsIntegration.WebP"), L("WindowsIntegration.Upscale2"));
            AudioShellRegistration()?.Apply(enabled, L("WindowsIntegration.Menu"), L("AudioShell.Title"));
            _appSettings.Behavior.ImageShellIntegrationEnabled = enabled;
            _appSettingsStore.Save(_appSettings);
        }
        catch (Exception error)
        {
            _appSettings.Behavior.ImageShellIntegrationEnabled = previous;
            try
            {
                registration?.Apply(previous, L("WindowsIntegration.Menu"), L("WindowsIntegration.WebP"), L("WindowsIntegration.Upscale2"));
                AudioShellRegistration()?.Apply(previous, L("WindowsIntegration.Menu"), L("AudioShell.Title"));
            }
            catch (Exception restoreError) { OwnedProcessRegistry.Log("image_shell_restore_failed", "WindowsIntegration", detail: restoreError.GetType().Name); }
            OwnedProcessRegistry.Log("image_shell_setting_failed", "WindowsIntegration", detail: error.GetType().Name);
            _refreshingSettingsWorkspace = true;
            try { RefreshImageShellIntegrationSetting(); } finally { _refreshingSettingsWorkspace = false; }
            System.Windows.MessageBox.Show(this, L("WindowsIntegration.Failed"), L("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void RefreshImageShellIntegrationSetting()
    {
        if (_imageShellIntegrationSwitch is not null) _imageShellIntegrationSwitch.IsChecked = _appSettings.Behavior.ImageShellIntegrationEnabled;
        RefreshBookShellIntegrationSetting();
        if (_imageShellIntegrationInitialized && _imageShellIntegrationLanguage != _appSettings.LanguageCode) ApplyImageShellIntegration();
    }

    private void ApplyBookShellIntegration()
    {
        var registration = BookShellRegistration();
        if (registration is null) return;
        try
        {
            var enabled = BookShellIntegration.ResolveEnabled(_appSettings.Behavior.BookShellIntegrationEnabled, registration.IsEnabled);
            // Re-register enabled owned keys to refresh the old menu title and current language.
            registration.Apply(enabled, L);
            if (_appSettings.Behavior.BookShellIntegrationEnabled is null)
            {
                _appSettings.Behavior.BookShellIntegrationEnabled = enabled;
                _appSettingsStore.Save(_appSettings);
            }
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("book_shell_registration_failed", "WindowsIntegration", detail: error.GetType().Name);
            StatusText.Text = L("WindowsIntegration.Failed");
        }
    }

    private void SetBookShellIntegration(bool enabled)
    {
        if (_refreshingSettingsWorkspace) return;
        var previous = _appSettings.Behavior.BookShellIntegrationEnabled;
        var registration = BookShellRegistration();
        var previousRegistration = registration?.IsEnabled ?? false;
        try
        {
            registration?.Apply(enabled, L);
            _appSettings.Behavior.BookShellIntegrationEnabled = enabled;
            _appSettingsStore.Save(_appSettings);
        }
        catch (Exception error)
        {
            _appSettings.Behavior.BookShellIntegrationEnabled = previous;
            try { registration?.Apply(previousRegistration, L); }
            catch (Exception restoreError) { OwnedProcessRegistry.Log("book_shell_restore_failed", "WindowsIntegration", detail: restoreError.GetType().Name); }
            OwnedProcessRegistry.Log("book_shell_setting_failed", "WindowsIntegration", detail: error.GetType().Name);
            _refreshingSettingsWorkspace = true;
            try { RefreshBookShellIntegrationSetting(); } finally { _refreshingSettingsWorkspace = false; }
            System.Windows.MessageBox.Show(this, L("WindowsIntegration.Failed"), L("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshBookShellIntegrationSetting()
    {
        if (_bookShellIntegrationSwitch is not null)
            _bookShellIntegrationSwitch.IsChecked = BookShellIntegration.ResolveEnabled(
                _appSettings.Behavior.BookShellIntegrationEnabled, BookShellRegistration()?.IsEnabled ?? false);
    }
}
