using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private bool _calibrationOpen;
    public void OpenCalibration()
    {
        if (_calibrationOpen || Indexing || _runtime.IsBusy || _projectMissing) return;
        try
        {
            _calibrationOpen = true;
            var layout = new LiteraryProjectLayout(_entry.ProjectPath);
            using var diagnostics = new LiteraryRequestDiagnostics("CalibrationUi", _ => { }, layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            var store = new LiteraryCalibrationStore(_entry.ProjectPath);
            var text = store.Read();
            var dialog = new LiteraryCalibrationWindow(_l, text, _project.LanguageCode, updated =>
            {
                store.Save(updated);
                _project.CreationBrief = updated;
            }, _runtime.CalibrateAsync, (kind,data)=>diagnostics.Write(kind,data)) { Owner = Window.GetWindow(this) };
            dialog.ContentRendered += (_,_) => { try { store.MarkOpened(); } catch (Exception ex) { diagnostics.Write("marker_failure",ex.Message); } };
            dialog.ShowDialog();
        }
        catch (Exception)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), _l("Literary.Calibration.ReadError"),
                _l("Literary.Calibration.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _calibrationOpen = false; }
    }
    private void ScheduleFirstCalibration()
    {
        Dispatcher.BeginInvoke(new Action(() => {
            if (!IsVisible || Indexing || _runtime.IsBusy || _projectMissing || _calibrationOpen) return;
            if (!new LiteraryCalibrationStore(_entry.ProjectPath).HasOpened) OpenCalibration();
        }));
    }
}
