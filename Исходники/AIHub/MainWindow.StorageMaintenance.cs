using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using AIHub.Models;
using AIHub.Services;
using Lopata.Updates;

namespace AIHub;

public partial class MainWindow
{
    private bool _storageMaintenanceOpen, _storageMaintenanceReady;
    private bool CanMaintainInstalledStorage => _updateService?.Installation is not null
        && ApplicationReleaseVersion.Parse(GetAppVersion()) is { Channel: not "dev" };

    private async Task CheckStorageMaintenanceAsync(bool manual = false)
    {
        if (!CanMaintainInstalledStorage || _storageMaintenanceOpen || _updateLifetime.IsCancellationRequested) return;
        if (!manual && (!_storageMaintenanceReady || !IsVisible)) return;
        var installation = _updateService!.Installation!;
        var transaction = new UpdateTransaction(installation.Roots(), UpdateReleaseKeys.Trusted, installation.StateDirectory);
        var transfer = new LegacyWorkTransfer(ManagedWorkStorage.Current);
        _storageMaintenanceOpen = true;
        try
        {
            var workspace = installation.WorkspaceDirectory;
            var settings = _appSettings.Updates;
            var limitGb = settings.StorageLimitGb is > 0 and <= 100000 ? settings.StorageLimitGb : 10;
            var plan = await Task.Run(() => transaction.PlanStorageCleanup(workspace, ToBytes(limitGb)), _updateLifetime.Token);
            var legacy = await Task.Run(transfer.Plan, _updateLifetime.Token);
            if (!manual)
            {
                if (plan.Bytes <= plan.Threshold && legacy.Count == 0) return;
                var state = string.Join("|", plan.Groups.Select(g => g.Id).Concat(legacy.Select(x => x.Id)).Order())
                    + "|" + plan.Bytes / 1_000_000_000;
                var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
                if (settings.LastStorageOffer == signature) return;
                settings.LastStorageOffer = signature;
                _appSettingsStore.Save(_appSettings);
                var choice = System.Windows.MessageBox.Show(this, L("StorageWork.Offer"), L("StorageWork.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (choice != MessageBoxResult.Yes) return;
            }
            var versions = await Task.Run(transaction.RollbackVersions, _updateLifetime.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            var window = new StorageMaintenanceWindow(_updateWindow ?? (Window)this, L, workspace, plan, legacy, versions,
                limitGb, async limit =>
                {
                    var next = await Task.Run(() => transaction.PlanStorageCleanup(workspace, ToBytes(limit)), _updateLifetime.Token);
                    settings.StorageLimitGb = limit;
                    _appSettingsStore.Save(_appSettings);
                    return next;
                },
                consent => Task.Run(() => transaction.CleanStorageAsync(workspace, consent, _updateLifetime.Token)),
                item => Task.Run(() => transfer.MoveAsync(item, _updateLifetime.Token)),
                ScheduleStorageRollback);
            window.ShowDialog();
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or CryptographicException)
        {
            OwnedProcessRegistry.Log("storage_maintenance_unavailable", "update", detail: error.GetType().Name);
            if (manual) System.Windows.MessageBox.Show(this, L("StorageWork.Unavailable") + Environment.NewLine + error.Message,
                L("StorageWork.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _storageMaintenanceOpen = false; }
    }

    private static long ToBytes(double gb) => checked((long)(gb * 1_000_000_000d));

    private void ScheduleStorageRollback(string version)
    {
        var start = ApplicationUpdateStartup.Launcher("--rollback-to");
        start.ArgumentList.Add(version);
        ApplicationUpdateStartup.AddWaitForThisProcess(start);
        _applicationUpdateStart = start;
        _updateWindow?.Close();
        Close();
    }
}
