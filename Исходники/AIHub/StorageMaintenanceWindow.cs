using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using Lopata.Updates;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub;

internal sealed class StorageMaintenanceWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly Button _cleanup = new();
    private readonly StackPanel _actions = new() { Margin = new(22) };
    private UpdateStoragePlan _plan;
    private bool _busy;
    private bool _cleaned;

    internal StorageMaintenanceWindow(Window owner, Func<string, string> text, string workspace,
        UpdateStoragePlan plan, IReadOnlyList<LegacyWorkItem> legacy, IReadOnlyList<string> rollback,
        double limit, Func<double, Task<UpdateStoragePlan>> saveLimit,
        Func<UpdateStoragePlan, Task<long>> clean, Func<LegacyWorkItem, Task<long>> move, Action<string> restore)
    {
        Owner = owner;
        Resources = owner.Resources;
        _text = text;
        _plan = plan;
        Title = text("StorageWork.Title");
        Width = 760;
        Height = 600;
        MinWidth = 460;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Content = new ScrollViewer { Content = _actions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _actions.Children.Add(new TextBlock { Text = workspace, TextWrapping = TextWrapping.Wrap });
        _actions.Children.Add(_summary);
        var limitRow = new WrapPanel();
        _actions.Children.Add(limitRow);
        limitRow.Children.Add(new TextBlock { Text = text("StorageWork.Limit"), VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBox { Text = limit.ToString(CultureInfo.CurrentCulture), MinWidth = 60, Margin = new(8, 0, 8, 0) };
        limitRow.Children.Add(value);
        Add(limitRow, new(), text("StorageWork.Recalculate"), () => Run(async () =>
        {
            if (!double.TryParse(value.Text, out var next) || next is < 0.001 or > 100000 || !double.IsFinite(next))
                throw new InvalidDataException(text("StorageWork.InvalidLimit"));
            _plan = await saveLimit(next);
            _cleaned = false;
            Refresh();
        }));
        Add(_actions, _cleanup, "", async () =>
        {
            if (!Confirm(Format("StorageWork.ConfirmCleanup", _plan.Groups.Count, Size(_plan.Reclaimable)))) return;
            await Run(async () =>
            {
                var released = await clean(_plan);
                _cleaned = true;
                _plan = _plan with { Bytes = Math.Max(0, _plan.Bytes - released), Groups = [] };
                Refresh();
                _status.Text = Format("StorageWork.Released", Size(released));
            });
        });
        Refresh();

        if (legacy.Count > 0)
        {
            _actions.Children.Add(new TextBlock { Text = Format("StorageWork.TransferCount", legacy.Count, Size(legacy.Sum(x => x.Bytes))),
                FontWeight = FontWeights.SemiBold, Margin = new(0, 18, 0, 4) });
            var list = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = string.Join(Environment.NewLine + Environment.NewLine,
                    legacy.Select(item => item.Source + Environment.NewLine + "→ " + item.Destination + " · " + Size(item.Bytes))) };
            _actions.Children.Add(list);
            var moveButton = new Button();
            Add(_actions, moveButton, text("StorageWork.Transfer"), async () =>
            {
                if (!Confirm(text("StorageWork.ConfirmTransfer"))) return;
                await Run(async () =>
                {
                    long released = 0;
                    int count = 0;
                    var failed = new List<string>();
                    foreach (var item in legacy)
                    {
                        try { released += await move(item); count++; }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        { failed.Add(item.Source + ": " + error.Message); }
                    }
                    moveButton.IsEnabled = false;
                    _status.Text = Format("StorageWork.TransferResult", count, failed.Count, Size(released))
                        + (failed.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, failed) : "");
                });
            });
        }
        if (rollback.Count > 0)
        {
            var row = new WrapPanel { Margin = new(0, 15, 0, 0) };
            var choices = new ComboBox { ItemsSource = rollback, SelectedIndex = 0, MinWidth = 110 };
            row.Children.Add(choices);
            Add(row, new(), text("StorageWork.Rollback"), () =>
            {
                if (choices.SelectedItem is string selected && Confirm(Format("StorageWork.ConfirmRollback", selected)))
                {
                    Close();
                    restore(selected);
                }
                return Task.CompletedTask;
            });
            _actions.Children.Add(row);
        }
        _actions.Children.Add(_status);
        Add(_actions, new(), text("StorageWork.Later"), () => { Close(); return Task.CompletedTask; });
        Closing += (_, args) => args.Cancel = _busy;
    }

    private void Refresh()
    {
        _summary.Text = Format("StorageWork.UpdateUsage", Size(_plan.Bytes), Size(_plan.Threshold));
        if (_plan.Reserved > 0)
            _summary.Text += Environment.NewLine + Format("StorageWork.Reserved", Size(_plan.Reserved));
        if (_plan.Groups.Count > 0)
        {
            _summary.Text += Environment.NewLine + string.Join(Environment.NewLine, _plan.Groups.SelectMany(group => group.Files)
                .GroupBy(file => Path.GetPathRoot(file.Path)).Select(drive => drive.Key + " " + Size(drive.Sum(file => file.Size))));
            if (_plan.Reclaimable < 5_000_000_000)
                _summary.Text += Environment.NewLine + _text("StorageWork.LessThanTarget");
        }
        else if (_plan.Bytes > _plan.Threshold)
            _summary.Text += Environment.NewLine + _text("StorageWork.Protected");
        _cleanup.Content = Format("StorageWork.Remove", _plan.Groups.Count, Size(_plan.Reclaimable));
        _cleanup.IsEnabled = !_cleaned && _plan.Groups.Count > 0;
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _actions.IsEnabled = false;
        _status.Text = _text("StorageWork.Working");
        try { await action(); if (_status.Text == _text("StorageWork.Working")) _status.Text = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or OperationCanceledException or System.Text.Json.JsonException)
        { _status.Text = _text("StorageWork.Unavailable") + Environment.NewLine + error.Message; }
        finally { _actions.IsEnabled = true; _busy = false; }
    }

    private bool Confirm(string message) => System.Windows.MessageBox.Show(this, message, Title,
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void Add(System.Windows.Controls.Panel panel, Button button, string label, Func<Task> work)
    {
        ApplicationUpdateChoices.Button(button, label);
        button.Margin = new(0, 6, 8, 4);
        button.Click += async (_, _) => await work();
        panel.Children.Add(button);
    }

    private string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, _text(key), args);
    private string Size(long bytes) => (bytes / 1_000_000_000d).ToString("0.##", CultureInfo.CurrentCulture) + " " + _text("Units.Gb");
}
