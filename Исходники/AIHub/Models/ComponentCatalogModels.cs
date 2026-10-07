using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIHub.Models;

public static class ComponentKinds
{
    public const string Processing = "processing";
    public const string Viewer = "viewer";
}

public static class ComponentDeliveryKinds
{
    public const string BuiltIn = "built_in";
    public const string Archive = "archive";
    public const string File = "file";
    public const string PythonProfile = "python_profile";
    public const string SystemInstaller = "system_installer";
    public const string Planned = "planned";
}

public static class ComponentInstallStatuses
{
    public const string BuiltIn = "built_in";
    public const string NotInstalled = "not_installed";
    public const string Downloading = "downloading";
    public const string Downloaded = "downloaded";
    public const string Installed = "installed";
    public const string NeedsVerification = "needs_verification";
    public const string Failed = "failed";
    public const string Planned = "planned";
}

public sealed class ComponentCatalogEntry
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = ComponentKinds.Processing;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string DeliveryKind { get; init; } = ComponentDeliveryKinds.Archive;

    public string DownloadUrl { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long DownloadSizeBytes { get; init; }

    public long InstalledSizeBytes { get; init; }

    public string License { get; init; } = string.Empty;

    // Composite runtimes reuse the original licenses of their actual bundled dependencies.
    public IReadOnlyList<string> LicenseIds { get; init; } = [];

    public string Source { get; init; } = string.Empty;

    public string Architecture { get; init; } = "x64";

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IReadOnlyList<string> Extensions { get; init; } = [];

    public string HealthCheckRelativePath { get; init; } = string.Empty;

    public bool IsVisibleToAi => Kind == ComponentKinds.Processing
        && Capabilities.Count > 0;

    public bool IsBuiltIn => DeliveryKind == ComponentDeliveryKinds.BuiltIn;

    public bool IsPlanned => DeliveryKind == ComponentDeliveryKinds.Planned;
}

public sealed class ComponentInstallationRecord
{
    public string ComponentId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Status { get; set; } = ComponentInstallStatuses.NotInstalled;

    public string InstallPath { get; set; } = string.Empty;

    public string DownloadPath { get; set; } = string.Empty;

    public long DownloadedBytes { get; set; }

    public long TotalBytes { get; set; }

    public string ComputedSha256 { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset? DownloadedAt { get; set; }

    public DateTimeOffset? InstalledAt { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class ComponentStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<ComponentInstallationRecord> Components { get; set; } = [];
}

public sealed class ComponentStatusSnapshot
{
    public required ComponentCatalogEntry Entry { get; init; }

    public required ComponentInstallationRecord Record { get; init; }

    public bool IsInstalled { get; init; }

    public bool IsHealthy { get; init; }

    public bool DependenciesAvailable { get; set; } = true;

    public bool IsSelfAvailable => Entry.IsBuiltIn || IsInstalled && IsHealthy;

    public bool IsAvailable => IsSelfAvailable && DependenciesAvailable;
}

public sealed record ComponentDownloadProgress(
    string ComponentId,
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    string Stage);

public sealed class ComponentAcquisitionItem
{
    public string ComponentId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool AlreadyAvailable { get; set; }

    public bool Required { get; set; }

    public long DownloadSizeBytes { get; set; }

    public string Reason { get; set; } = string.Empty;
}

public sealed class ComponentAcquisitionPlan
{
    public List<ComponentAcquisitionItem> Items { get; set; } = [];

    public long TotalDownloadBytes => Items
        .Where(item => !item.AlreadyAvailable)
        .Sum(item => item.DownloadSizeBytes);

    public bool IsReady => Items.All(item => !item.Required || item.AlreadyAvailable);
}

public sealed class ComponentCardViewModel : INotifyPropertyChanged
{
    private string _status = ComponentInstallStatuses.NotInstalled;
    private bool _isDownloading;
    private bool _isProgressIndeterminate;
    private double _progressPercent;
    private string _progressText = string.Empty;

    public required ComponentCatalogEntry Entry { get; init; }

    public string DescriptionText { get; init; } = string.Empty;

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetField(ref _isDownloading, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set => SetField(ref _isProgressIndeterminate, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetField(ref _progressText, value);
    }

    public bool IsSelected { get; set; }

    public bool CanDownload { get; set; }

    public bool CanRemove { get; set; }

    public bool PreferInternal { get; set; }

    public string PreferInternalLabel { get; set; } = string.Empty;

    public string DownloadLabel { get; set; } = string.Empty;

    public string VerifyLabel { get; set; } = string.Empty;

    public string RemoveLabel { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.Version)
        ? Entry.Name
        : $"{Entry.Name} {Entry.Version}";

    public string SizeText => Entry.DownloadSizeBytes <= 0
        ? string.Empty
        : FormatBytes(Entry.DownloadSizeBytes);

    public string CapabilityText => string.Join(", ", Entry.Capabilities);

    public string ExtensionText => string.Join(", ", Entry.Extensions);

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var suffix = 0;
        var display = (double)value;
        while (display >= 1024 && suffix < suffixes.Length - 1)
        {
            display /= 1024;
            suffix++;
        }

        return $"{display:0.##} {suffixes[suffix]}";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ComponentCatalogViewModel
{
    public ObservableCollection<ComponentCardViewModel> Processing { get; } = [];

    public ObservableCollection<ComponentCardViewModel> Viewers { get; } = [];
}
