using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace Lopata.MailHub;

internal sealed class MailHubPlayback(MailHubFiles files, Window owner, Func<string, string> text,
    Action<string> showStatus, Action<Window>? applyDialogTheme = null) : IDisposable
{
    private MediaPlayer? _player;
    internal Guid? PlayingId { get; private set; }
    internal bool Paused { get; private set; }
    internal event Action? Changed;

    internal void Toggle(MailHubReport report)
    {
        if (_player is { } player && PlayingId == report.Id)
        {
            Paused = !Paused;
            if (Paused) player.Pause();
            else player.Play();
            Changed?.Invoke();
            return;
        }
        Stop();
        var newPlayer = new MediaPlayer();
        _player = newPlayer;
        PlayingId = report.Id;
        newPlayer.MediaEnded += (_, _) =>
        {
            if (_player != newPlayer) return;
            Stop();
            Try(() =>
            {
                files.MarkListened(report.Id);
                Changed?.Invoke();
                if (File.Exists(files.ReportPath(report.Id))) Ask(report);
            });
        };
        newPlayer.MediaFailed += (_, _) =>
        {
            if (_player != newPlayer) return;
            Stop();
            showStatus("MailHub.AudioFailed");
        };
        try
        {
            newPlayer.Open(new Uri(files.ReportPath(report.Id)));
            newPlayer.Play();
            Changed?.Invoke();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UriFormatException)
        {
            Stop();
            showStatus("MailHub.AudioFailed");
        }
    }

    internal void Stop()
    {
        var old = _player;
        _player = null;
        PlayingId = null;
        Paused = false;
        old?.Close();
        Changed?.Invoke();
    }

    internal void Delete(MailHubReport report)
    {
        if (PlayingId == report.Id) Stop();
        Try(() => files.Delete(report.Id));
        Changed?.Invoke();
    }

    private void Ask(MailHubReport report)
    {
        var dialog = new Window
        {
            Owner = owner, Title = text("MailHub.Title"), SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Background = owner.Background, Foreground = owner.Foreground
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        applyDialogTheme?.Invoke(dialog);
        var container = new StackPanel { Margin = new Thickness(20) };
        container.Children.Add(new TextBlock { Text = text("MailHub.RecordingDecision"), Margin = new Thickness(0, 0, 0, 14) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        int selected = 0;
        void Add(string key, int choice)
        {
            var action = new Button { Content = text(key), MinWidth = 100, Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 6, 12, 6), IsDefault = choice == 2 };
            action.Click += (_, _) => { selected = choice; dialog.DialogResult = true; };
            actions.Children.Add(action);
        }
        Add("MailHub.Delete", 1);
        Add("MailHub.Save", 2);
        container.Children.Add(actions);
        dialog.Content = container;
        if (dialog.ShowDialog() != true) return;
        if (selected == 1) Delete(report);
        if (selected != 2) return;
        var save = new SaveFileDialog
        {
            Filter = "WAV|*.wav", DefaultExt = ".wav", AddExtension = true, OverwritePrompt = true,
            FileName = $"LOPATA-{report.Created:yyyy-MM-dd-HHmm}.wav"
        };
        if (save.ShowDialog(owner) == true) Try(() => files.CopyReport(report.Id, save.FileName));
    }

    private void Try(Action update)
    {
        try { update(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { showStatus("MailHub.FileFailed"); }
    }

    public void Dispose() => Stop();
}
