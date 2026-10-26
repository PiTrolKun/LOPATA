using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.VideoEditor;

internal enum VideoCloseChoice { Cancel, Save, Discard }

internal sealed partial class VideoEditorWindow
{
    private VideoSaveChanges _saveChanges;
    private bool _closeApproved, _closePending;
    internal bool ClosePending => _closePending;
    internal Func<VideoCloseChoice>? ConfirmSaveOnClose { get; set; }
    internal Func<string?>? SelectArchiveDestination { get; set; }
    private void EditorClosing(object? sender, CancelEventArgs e)
    {
        if (_savingArchive) { _localOperationCancel?.Cancel(); e.Cancel = true; _status.Text = T("CancellingArchive"); return; }
        if (_closePending && !_closeApproved) { e.Cancel = true; return; }
        EndSpacing(); CloseVolumePopup(true);
        if (!_closeApproved && _saveChanges.NeedsPrompt(Project, _editing.Revision)) {
            var choice = ConfirmSaveOnClose?.Invoke();
            if (choice == VideoCloseChoice.Cancel) { e.Cancel = true; return; }
            if (choice == VideoCloseChoice.Discard) _closeApproved = true;
            else {
                e.Cancel = true; _closePending = true; _saveTimer.Stop();
                // Defer until WPF has completed this cancelled Closing event before trying Close again.
                _ = Dispatcher.BeginInvoke(() => Safe(() => ConfirmCloseAsync(choice))); return;
            }
        }
        try { VideoProjectStore.Save(Project, VideoProjectStore.Recovery(Project.Id)); }
        catch (Exception error) {
            if (System.Windows.MessageBox.Show(this, T("RecoveryFailed") + "\n" + Error(error), T("Title"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) { e.Cancel = true; _closeApproved = false; }
        }
    }
    private async Task ConfirmCloseAsync(VideoCloseChoice? selected)
    {
        try {
            _playing = false; await _preview.PlayAsync(false); UpdatePosition();
            var choice = selected ?? ShowClosePrompt();
            if (choice == VideoCloseChoice.Cancel) return;
            if (choice == VideoCloseChoice.Save && !await SaveArchiveAsync(false)) return;
            _closeApproved = true; Close();
        }
        finally { _closePending = false; if (!_closed && _dirty) _saveTimer.Start(); }
    }
    private VideoCloseChoice ShowClosePrompt()
    {
        var answer = VideoCloseChoice.Cancel;
        var dialog = new Window { Owner = this, Title = T("SaveBeforeCloseTitle"), Width = 540, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        AutomationProperties.SetAutomationId(dialog, "VideoEditor.ClosePrompt");
        dialog.Resources.MergedDictionaries.Add(Resources); dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = string.Format(T("SaveBeforeCloseText"), Project.Name), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) });
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        void Add(string key, VideoCloseChoice choice) {
            var button = new Button { Content = T(key), Padding = new(12, 7, 12, 7), Margin = new(0, 0, 6, 6), IsDefault = choice == VideoCloseChoice.Save, IsCancel = choice == VideoCloseChoice.Cancel };
            AutomationProperties.SetAutomationId(button, "VideoEditor.Close." + key); AutomationProperties.SetName(button, T(key));
            button.Click += (_, _) => { answer = choice; dialog.DialogResult = choice != VideoCloseChoice.Cancel; }; buttons.Children.Add(button);
        }
        Add("Save", VideoCloseChoice.Save); Add("CloseWithoutSaving", VideoCloseChoice.Discard); Add("CancelClose", VideoCloseChoice.Cancel);
        dialog.ShowDialog(); return answer;
    }
}
