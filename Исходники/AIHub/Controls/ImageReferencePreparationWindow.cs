using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace AIHub.Controls;

/// <summary>Hosts the mother's confirmation control and installation actions without navigating away from the draft.</summary>
public sealed class ImageReferencePreparationWindow : Window
{
    private readonly ImageAnalysisBundleConfirmationControl _control = new();
    private readonly Func<string, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<ImageAnalysisBundleInstallationSnapshot>> _prepare;
    private readonly Func<string, string> _l;
    private readonly Func<ImageAnalysisBundleInstallationSnapshot>? _refresh;
    internal Action<string>? ReportProblem { get; set; }
    private CancellationTokenSource? _cancel;
    private bool _closeAfterCancel;
    public ImageAnalysisBundleInstallationSnapshot Snapshot { get; private set; }
    public ImageReferencePreparationWindow(ImageAnalysisBundleInstallationSnapshot snapshot, Func<string, string> localize,
        Func<string, IProgress<ManagedModelDownloadProgress>, CancellationToken, Task<ImageAnalysisBundleInstallationSnapshot>> prepare,
        Func<ImageAnalysisBundleInstallationSnapshot>? refresh = null)
    {
        Snapshot = snapshot; _prepare = prepare; _l = localize; _refresh = refresh;
        Title = _l("Generation.ReferencePreparation"); Width = 960; Height = 720; MinWidth = 650; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.MainWindow is { } main) Resources.MergedDictionaries.Add(main.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _control.SetPreparationWindowMode();
        _control.Configure(ImageAnalysisBundleCatalog.Create().Single(b => b.Id == ImageAnalysisBundleCatalog.MediumId), snapshot,
            _l, (key, values) => string.Format(_l(key), values));
        _control.ActionRequested += async (_, e) => await PrepareAsync(e.Action);
        _control.BackToWorkStartRequested += (_, _) => Close();
        _control.CancelRequested += (_, _) => _cancel?.Cancel();
        _control.Margin = new Thickness(18); Content = _control;
        Closing += (_, e) => { if (_cancel is not null) { e.Cancel = true; _closeAfterCancel = true; _cancel.Cancel(); } };
    }
    internal async Task PrepareAsync(string action)
    {
        if (_cancel is not null || action is not (ImageAnalysisBundleActions.Download or ImageAnalysisBundleActions.Verify)) return;
        var message = action == ImageAnalysisBundleActions.Download
            ? string.Format(_l("ImageAnalysis.Install.DownloadConfirm"),
                string.Join(Environment.NewLine, Snapshot.Components.Where(c => c.Status != ManagedModelStatuses.Installed && c.Status != ManagedModelStatuses.NeedsVerification).Select(c => "• " + c.DisplayName)),
                ComponentCardViewModel.FormatBytes(Snapshot.MissingBytes), Snapshot.ModelsRoot)
            : _l("ImageAnalysis.Install.HeavyVerifyConfirm");
        if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        await ExecuteAsync(action);
    }
    internal async Task ExecuteAsync(string action)
    {
        if (_cancel is not null) return;
        _cancel = new(); _control.SetBusy(true); var ready = false;
        try
        {
            var updated = await _prepare(action, new Progress<ManagedModelDownloadProgress>(_control.UpdateProgress), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested(); Snapshot = updated; _control.UpdateSnapshot(updated); ready = updated.CanStart;
        }
        catch (OperationCanceledException) { RefreshAfterFailure(); if (!_closeAfterCancel) Problem(_l("Status.ImageAnalysisOperationCancelled")); }
        catch (Exception error) { RefreshAfterFailure(); Problem(_l("Status.ImageAnalysisOperationFailed") + "\n" + error.Message); }
        finally { _cancel.Dispose(); _cancel = null; _control.SetBusy(false); }
        if (ready) { DialogResult = true; }
        else if (_closeAfterCancel) Close();
    }
    private void Problem(string message)
    { if (ReportProblem is { } report) report(message); else MessageBox.Show(this, message, Title); }
    private void RefreshAfterFailure()
    {
        if (_refresh is null) return;
        try
        {
            var refreshed = _refresh();
            Snapshot = refreshed.CanStart ? new ImageAnalysisBundleInstallationSnapshot
            {
                State = ImageAnalysisBundleInstallStates.NeedsVerification, Components = refreshed.Components,
                ModelsRoot = refreshed.ModelsRoot, MissingBytes = refreshed.MissingBytes, AvailableFreeBytes = refreshed.AvailableFreeBytes
            } : refreshed;
            _control.UpdateSnapshot(Snapshot);
        }
        catch { /* Keep the last usable preparation snapshot and allow retry. */ }
    }
}
