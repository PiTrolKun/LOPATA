using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AIHub.Services;
using Lopata.Collection;
using MessageBox = System.Windows.MessageBox;

namespace AIHub;

public partial class MainWindow
{
    private System.Windows.Controls.CheckBox? _collectionSwitch;
    private System.Windows.Controls.Button? _collectionOpen;
    private bool _collectionChanging;
    private string CollectionDirectory => Path.Combine(AppDataPaths.BaseDirectory, "Collection");

    private CollectionShellRegistration? CollectionRegistration() =>
        Assembly.GetEntryAssembly()?.GetName().Name == typeof(MainWindow).Assembly.GetName().Name
        && Environment.ProcessPath is { Length: > 0 } executable ? new(executable) : null;

    private CollectionConfig CollectionConfiguration(bool enabled, string? workerPath = null)
    {
        if (CollectionRegistration() is { CanManageRegistration: false })
            throw new IOException("Another installation owns the collection.");
        return CollectionConfigurationFactory.Create(_appSettings, _storageSettings,
            CollectionConfigurationPaths.Current, enabled, workerPath);
    }

    private async void ApplyCollectionIntegration()
    {
        if (_collectionChanging || CollectionRegistration() is not { } registration) return;
        _collectionChanging = true;
        RefreshCollectionSetting();
        CollectionConfig? previous = null;
        try
        {
            if (_appSettings.Behavior.CollectionIntegrationEnabled)
            {
                var payload = await CollectionComponent.PrepareAsync();
                previous = CollectionConfig.Load(CollectionDirectory);
                SaveCollectionConfiguration(CollectionConfiguration(true, payload.Worker));
                registration.Enable(payload.Native, payload.Worker, CollectionDirectory, _appSettings.LanguageCode);
                TakeCollectionAuditFailure(report: false);
            }
            else if (registration.CanManageRegistration
                && (registration.HasOwnedRegistration || File.Exists(Path.Combine(CollectionDirectory, "config.json"))))
            {
                DisableCollectionRegistration(registration);
            }
        }
        catch (Exception error)
        {
            try { if (registration.CanManageRegistration) previous?.Save(CollectionDirectory); }
            catch (Exception restoreError)
            {
                OwnedProcessRegistry.Log("collection_restore_failed", "Collection", detail: restoreError.GetType().Name);
            }
            OwnedProcessRegistry.Log("collection_registration_failed", "Collection", detail: error.GetType().Name);
            StatusText.Text = L("Collection.Failed");
        }
        finally
        {
            _collectionChanging = false;
            RefreshCollectionSetting();
        }
    }

    private async void SetCollectionIntegration(bool enabled)
    {
        if (_refreshingSettingsWorkspace || _collectionChanging || CollectionRegistration() is not { } registration) return;
        var previous = _appSettings.Behavior.CollectionIntegrationEnabled;
        CollectionConfig? configuration = null;
        var registrationChanged = false;
        _collectionChanging = true;
        RefreshCollectionSetting();
        try
        {
            try { configuration = CollectionConfig.Load(CollectionDirectory); }
            catch (Exception error) when (!enabled && error is JsonException or InvalidDataException)
            {
                OwnedProcessRegistry.Log("collection_config_unreadable", "Collection", detail: error.GetType().Name);
            }
            if (enabled)
            {
                var payload = await CollectionComponent.PrepareAsync();
                SaveCollectionConfiguration(CollectionConfiguration(true, payload.Worker));
                registration.Enable(payload.Native, payload.Worker, CollectionDirectory, _appSettings.LanguageCode);
            }
            else
            {
                DisableCollectionRegistration(registration);
            }
            registrationChanged = true;
            _appSettings.Behavior.CollectionIntegrationEnabled = enabled;
            _appSettingsStore.Save(_appSettings);
            if (enabled) TakeCollectionAuditFailure(report: false);
        }
        catch (Exception error)
        {
            _appSettings.Behavior.CollectionIntegrationEnabled = previous;
            try
            {
                if (registration.CanManageRegistration)
                {
                    if (registrationChanged && previous)
                    {
                        var payload = await CollectionComponent.PrepareAsync();
                        ((configuration ?? CollectionConfiguration(true)) with { ExpectedWorkerPath = payload.Worker }).Save(CollectionDirectory);
                        registration.Enable(payload.Native, payload.Worker, CollectionDirectory, _appSettings.LanguageCode);
                    }
                    else
                    {
                        configuration?.Save(CollectionDirectory);
                        if (registrationChanged) registration.Disable();
                    }
                }
            }
            catch (Exception restoreError)
            {
                OwnedProcessRegistry.Log("collection_restore_failed", "Collection", detail: restoreError.GetType().Name);
            }
            OwnedProcessRegistry.Log("collection_setting_failed", "Collection", detail: error.GetType().Name);
            MessageBox.Show(this, L("Collection.Failed"), L("Collection.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _collectionChanging = false;
            _refreshingSettingsWorkspace = true;
            try { RefreshCollectionSetting(); }
            finally { _refreshingSettingsWorkspace = false; }
        }
    }

    private void RefreshCollectionSetting()
    {
        var wasRefreshing = _refreshingSettingsWorkspace;
        _refreshingSettingsWorkspace = true;
        try
        {
            if (_collectionSwitch is not null)
            {
                _collectionSwitch.IsEnabled = !_collectionChanging;
                _collectionSwitch.IsChecked = _appSettings.Behavior.CollectionIntegrationEnabled;
            }
            if (_collectionOpen is not null)
                _collectionOpen.IsEnabled = !_collectionChanging && _appSettings.Behavior.CollectionIntegrationEnabled;
        }
        finally { _refreshingSettingsWorkspace = wasRefreshing; }
    }

    private void DisableCollectionRegistration(CollectionShellRegistration registration)
    {
        if (!registration.CanManageRegistration) throw new IOException("Another installation owns the collection.");
        try
        {
            if (CollectionDeactivation.Disable(CollectionDirectory) is { } failure)
                OwnedProcessRegistry.Log("collection_config_unreadable", "Collection", detail: failure);
        }
        finally { registration.Disable(); }
    }

    private void SaveCollectionConfiguration(CollectionConfig configuration)
        => CollectionConfigurationFactory.SaveIfChanged(CollectionDirectory, configuration);

    private void ChooseCollectionFolders(object? sender, RoutedEventArgs args)
    {
        var previous = _appSettings.Behavior.CollectionFolders;
        try
        {
            var window = new AIHub.Controls.CollectionFoldersWindow(previous, L) { Owner = this };
            if (window.ShowDialog() != true) return;
            _appSettings.Behavior.CollectionFolders = window.Folders;
            if (CollectionRegistration() is { CanManageRegistration: true })
                SaveCollectionConfiguration(CollectionConfiguration(_appSettings.Behavior.CollectionIntegrationEnabled));
            _appSettingsStore.Save(_appSettings);
        }
        catch (Exception error)
        {
            _appSettings.Behavior.CollectionFolders = previous;
            try
            {
                if (CollectionRegistration() is { CanManageRegistration: true })
                    CollectionConfiguration(_appSettings.Behavior.CollectionIntegrationEnabled).Save(CollectionDirectory);
            }
            catch (Exception restoreError)
            {
                OwnedProcessRegistry.Log("collection_restore_failed", "Collection", detail: restoreError.GetType().Name);
            }
            OwnedProcessRegistry.Log("collection_folders_failed", "Collection", detail: error.GetType().Name);
            MessageBox.Show(this, L("Collection.Failed"), L("Collection.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenCollection(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (!_appSettings.Behavior.CollectionIntegrationEnabled) return;
            ReportCollectionAuditFailure();
            CollectionFiles.WriteAtomic(Path.Combine(CollectionDirectory, "refresh.request"),
                System.Text.Encoding.ASCII.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("shell:::" + CollectionShellRegistration.ClassId);
            _ = Process.Start(start) ?? throw new IOException("Explorer did not start.");
        }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("collection_open_failed", "Collection", detail: error.GetType().Name);
            MessageBox.Show(this, L("Collection.Failed"), L("Collection.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReportCollectionAuditFailure() => TakeCollectionAuditFailure(report: true);

    private void TakeCollectionAuditFailure(bool report)
    {
        if (!_appSettings.Behavior.CollectionIntegrationEnabled
            || CollectionRegistration() is not { CanManageRegistration: true }) return;
        var path = Path.Combine(CollectionDirectory, "component-audit-failure.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
            using var marker = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = marker.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("component", out var component)
                || component.GetString() != "explorer-collection") return;
            // A successful audit/registration replaces an older failure. Only a later failure
            // is forwarded from the next collection action, never the superseded worker's notice.
            if (report) ComponentAuditFailures.Report(new IOException(L("Collection.Failed")));
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            OwnedProcessRegistry.Log("collection_audit_notice_failed", "Collection", detail: error.GetType().Name);
        }
    }
}
