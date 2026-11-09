using System.IO;
using System.Reflection;
using AIHub.Models;
using Lopata.Collection;

namespace AIHub.Services;

/// <summary>Initializes Explorer without a main window, engine setup, model loading or startup dialogs.</summary>
internal static class CollectionInitializationService
{
    internal static Task InitializeCurrentAsync(CancellationToken token = default)
    {
        // A tool host or an ordinary unit test may never register its executable in the real shell.
        if (Assembly.GetEntryAssembly()?.GetName().Name != typeof(CollectionInitializationService).Assembly.GetName().Name
            || Environment.ProcessPath is not { Length: > 0 } executable)
            throw new InvalidOperationException("Collection initialization requires the installed LOPATA application.");
        var registration = new CollectionShellRegistration(executable);
        if (!registration.CanManageRegistration) throw new IOException("Another LOPATA installation owns the Explorer collection.");
        var settings = new AppSettingsStore().LoadOrCreate();
        var storage = new StorageSettingsStore().LoadOrCreate();
        return InitializeAsync(settings, storage, CollectionConfigurationPaths.Current, registration, token);
    }

    internal static async Task InitializeAsync(AppSettings settings, StorageSettings storage,
        CollectionConfigurationPaths paths, CollectionShellRegistration registration, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!registration.CanManageRegistration) throw new IOException("Another LOPATA installation owns the Explorer collection.");
        ComponentAudit.Configure(settings.Behavior.ComponentAuditEnabled);
        using var operation = ComponentAudit.CaptureOperation();
        if (!settings.Behavior.CollectionIntegrationEnabled)
        {
            if (registration.HasOwnedRegistration || File.Exists(Path.Combine(paths.CollectionDirectory, "config.json")))
            {
                try
                {
                    if (CollectionDeactivation.Disable(paths.CollectionDirectory) is { } error)
                        OwnedProcessRegistry.Log("collection_config_unreadable", "Collection", detail: error);
                }
                finally { registration.Disable(); }
            }
            return;
        }

        var payload = await CollectionComponent.PrepareAsync(token, Path.Combine(paths.ApplicationDirectory, "Collection"));
        if (!registration.CanManageRegistration) throw new IOException("Another LOPATA installation owns the Explorer collection.");
        var previous = CollectionConfig.Load(paths.CollectionDirectory);
        try
        {
            // Publish the expected audited worker before the namespace becomes available.
            CollectionConfigurationFactory.SaveIfChanged(paths.CollectionDirectory,
                CollectionConfigurationFactory.Create(settings, storage, paths, true, payload.Worker));
            registration.Enable(payload.Native, payload.Worker, paths.CollectionDirectory, settings.LanguageCode);
        }
        catch
        {
            try { if (registration.CanManageRegistration) previous.Save(paths.CollectionDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("collection_restore_failed", "Collection", detail: error.GetType().Name); }
            throw;
        }
    }
}
