using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services;
using AIHub.Services.Shorts;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

internal sealed partial class ShortsControl
{
    private readonly TextBox _exportPath = new() { IsReadOnly = true };
    private CancellationTokenSource? _referenceLoads;
    private string? _suggestedOutput;

    private string SelectedOutput => _draft.OutputPath ?? (_suggestedOutput ??= ShortsJob.SuggestOutputPath());

    private void AddOutputSettings()
    {
        var label = Label("Shorts.Output");
        label.FontWeight = FontWeights.SemiBold;
        _inputs.Children.Add(label);
        var row = new DockPanel();
        var change = Action("Shorts.ChangeOutput", "ChangeOutput", ChooseOutputAsync);
        change.Margin = new(8, 0, 0, 0);
        DockPanel.SetDock(change, Dock.Right);
        row.Children.Add(change);
        AutomationProperties.SetAutomationId(_exportPath, "Shorts.OutputPath");
        AutomationProperties.SetLabeledBy(_exportPath, label);
        row.Children.Add(_exportPath);
        _inputs.Children.Add(row);
    }

    private void SetOutputDisplay(string? activePath = null)
    {
        var path = activePath ?? SelectedOutput;
        _exportPath.Text = path;
        _exportPath.ToolTip = path;
    }

    private Task ChooseOutputAsync()
    {
        var path = _exportPath.Text;
        var folder = Path.GetDirectoryName(path)!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = _localize("Shorts.ChangeOutput"),
            Filter = _localize("Shorts.OutputFilter"),
            DefaultExt = ".mp4", AddExtension = true, OverwritePrompt = false,
            FileName = Path.GetFileName(path),
            InitialDirectory = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        var selected = ShortsJob.ValidateOutputPath(dialog.FileName);
        if (_draft.JobId is { } id && Pending is null)
        {
            var job = ShortsJob.Load(id);
            if (!File.Exists(job.Output))
            {
                (job with { Output = selected }).Save();
                _draft = _draft with { OutputPath = selected };
                SaveDraft();
                SetOutputDisplay(selected);
                return Task.CompletedTask;
            }
        }
        Edit(_draft with { OutputPath = selected });
        SetOutputDisplay(selected);
        return Task.CompletedTask;
    }

    private void ReloadImages()
    {
        _referenceLoads?.Cancel();
        _imageRows.Children.Clear();
        var previews = new List<(string Path, Image Image, TextBlock Fallback)>();
        for (var index = 0; index < _draft.Images.Length; index++)
        {
            var position = index;
            var reference = _draft.Images[index];
            var row = new DockPanel { Margin = new(0, 5, 0, 5) };
            var image = new Image { Stretch = Stretch.Uniform };
            var fallback = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var tile = new Grid();
            tile.Children.Add(image);
            tile.Children.Add(fallback);
            var frame = new Border { Width = 112, Height = 94, Margin = new(0, 0, 10, 0),
                BorderThickness = new(1), CornerRadius = new(5), Child = tile, ToolTip = Path.GetFileName(reference.Path) };
            frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            DockPanel.SetDock(frame, Dock.Left);
            row.Children.Add(frame);
            AutomationProperties.SetAutomationId(image, $"Shorts.Reference.{index + 1}.Preview");
            AutomationProperties.SetName(image, Path.GetFileName(reference.Path));
            previews.Add((reference.Path, image, fallback));

            var remove = new Button { Content = _localize("Shorts.Remove"), Margin = new(8, 0, 0, 0),
                Padding = new(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            remove.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) =>
            {
                Edit(_draft with { References = _draft.Images.Where((_, current) => current != position).ToArray() });
                ReloadImages();
            };
            row.Children.Add(remove);
            var form = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = $"{index + 1}. {Path.GetFileName(reference.Path)}",
                Margin = new(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            form.Children.Add(label);
            if (_draft.FromSong)
            {
                var description = new TextBox { Text = reference.Description, ToolTip = _localize("Shorts.Performer") };
                AutomationProperties.SetName(description, _localize("Shorts.Performer"));
                description.TextChanged += (_, _) =>
                {
                    var images = _draft.Images.ToArray();
                    images[position] = images[position] with { Description = description.Text };
                    Edit(_draft with { References = images });
                };
                form.Children.Add(description);
            }
            row.Children.Add(form);
            _imageRows.Children.Add(row);
        }
        if (IsLoaded && previews.Count > 0)
        {
            var loads = _referenceLoads = new CancellationTokenSource();
            _ = LoadReferencePreviewsAsync(previews, loads);
        }
    }

    private async Task LoadReferencePreviewsAsync(
        List<(string Path, Image Image, TextBlock Fallback)> previews, CancellationTokenSource loads)
    {
        try
        {
            foreach (var (path, image, fallback) in previews)
            {
                try
                {
                    var preview = await Task.Run(() => ReadReferencePreview(path), loads.Token);
                    loads.Token.ThrowIfCancellationRequested();
                    image.Source = preview;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                    or NotSupportedException or FileFormatException or ArgumentException)
                {
                    loads.Token.ThrowIfCancellationRequested();
                    fallback.Text = _localize("Shorts.ImagePreviewUnavailable");
                    fallback.Visibility = Visibility.Visible;
                }
            }
        }
        catch (OperationCanceledException) when (loads.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_referenceLoads, loads)) _referenceLoads = null;
            loads.Dispose();
        }
    }

    internal static ImageSource ReadReferencePreview(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length is <= 0 or > VideoReferenceFiles.MaxBytes) throw new InvalidDataException("Shorts.ImagePreviewUnavailable");
        var data = new byte[(int)input.Length];
        input.ReadExactly(data);
        using var png = new MemoryStream(FileMetadataPreview.Thumbnail(data, VideoReferenceFiles.MaxBytes));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = png;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
