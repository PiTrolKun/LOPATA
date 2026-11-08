using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Image = System.Windows.Controls.Image;

namespace AIHub.Controls.OfficeTextEditor;

// The text editor remains interactive while the reader prepares a private PDF.
// Page navigation reuses that PDF. An edit invalidates it after typing pauses.
internal sealed partial class OfficeNativeSlidePreview : Grid, IAsyncDisposable
{
    private readonly string _extension;
    private readonly Func<int> _pageCount;
    private readonly Func<CancellationToken, Task<byte[]>> _snapshot;
    private readonly Func<int, OfficeSlideCanvasModel?> _canvas;
    private readonly Func<string,string> _localize;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly TextBlock _status = new()
    {
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8)
    };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private readonly OfficeSlideObjectOverlay _overlay;
    private Task _worker = Task.CompletedTask;
    private ReaderDocument? _reader;
    private CancellationTokenSource? _current;
    private int _page, _revision, _readerRevision = -1, _selection;
    private int _canvasPage = -1;
    private bool _active, _pumping, _queued, _disposed, _choosingPicture;

    internal OfficeNativeSlidePreview(OfficeNativeDocument model, Func<string,string> localize)
        : this(model.Extension, () => model.Pages.Count, model.SaveAsync, model.SlideCanvas, localize) { }

    internal OfficeNativeSlidePreview(string extension, Func<int> pageCount, Func<CancellationToken, Task<byte[]>> snapshot,
        Func<int, OfficeSlideCanvasModel?> canvas, Func<string,string> localize)
    {
        _extension = extension; _pageCount = pageCount; _snapshot = snapshot; _canvas = canvas; _localize = localize;
        _overlay = new OfficeSlideObjectOverlay(localize);
        _overlay.Edited += ObjectEdited;
        _overlay.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Delete && _overlay.Selected?.Delete != null)
            { e.Handled = true; DeleteSelected(); }
        };
        _overlay.ReplaceRequested += ChoosePicture;
        _overlay.EditRequested += BeginInlineEdit;
        _overlay.SelectionChanged += () => SelectionChanged?.Invoke();
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "ReaderPanel");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "ReaderText");
        Children.Add(_image); Children.Add(_status); Children.Add(_overlay);
        Children.Add(_inlineLayer);
        SizeChanged += (_, _) => UpdateViewport();
        AutomationProperties.SetAutomationId(this, "Office.Editor.Native.Preview");
        AutomationProperties.SetAutomationId(_image, "Office.Editor.Native.Preview.Image");
        AutomationProperties.SetAutomationId(_status, "Office.Editor.Native.Preview.Status");
        _timer.Tick += StartScheduled;
    }

    internal event Action? Edited;
    internal event Action? SelectionChanged;
    internal bool CanReplacePicture => !_choosingPicture && _image.Source != null && _overlay.Selected?.CanReplacePicture == true;

    private void ObjectEdited()
    {
        InvalidateContent();
        Edited?.Invoke();
    }

    internal async void ChoosePicture()
    {
        if (_overlay.Selected is not { CanReplacePicture: true } selected || !CanReplacePicture) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = _localize("OfficeEditor.SlideReplacePicture"),
            Filter = _localize("OfficeEditor.SlideImageFilter"), CheckFileExists = true
        };
        var owner = Window.GetWindow(this);
        var page = _page;
        _choosingPicture = true;
        SelectionChanged?.Invoke();
        try
        {
            if (owner == null || picker.ShowDialog(owner) != true) return;
            var picture = await Task.Run(() => OfficeSlidePicture.Load(picker.FileName));
            if (_disposed || !_active || _page != page || !ReferenceEquals(selected, _overlay.Selected)) return;
            selected.ReplacePicture(picture);
            ObjectEdited();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_disposed && owner != null && owner.IsVisible)
                System.Windows.MessageBox.Show(owner, _localize(error.Message == "OfficeEditor.InvalidPackage"
                    ? "OfficeEditor.SlidePicturePackageLimit" : "OfficeEditor.SlideInvalidPicture"),
                    picker.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _choosingPicture = false; SelectionChanged?.Invoke(); }
    }

    private void UpdateViewport()
    {
        if (_image.Source is not BitmapSource image || image.PixelWidth <= 0 || image.PixelHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _overlay.SetViewport(Rect.Empty);
            return;
        }
        var scale = Math.Min(ActualWidth / image.PixelWidth, ActualHeight / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        _overlay.SetViewport(new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height));
        PositionInlineEditor();
    }

    private void ClearImage()
    {
        EndInlineEdit();
        _canvasPage = -1;
        _image.Source = null;
        _overlay.SetSlide(null);
        _editableCanvas = null;
        _overlay.SetBitmap(null);
        _overlay.SetViewport(Rect.Empty);
        SelectionChanged?.Invoke();
    }

    internal void ShowPage(int index)
    {
        if (_disposed || _page == index) return;
        _page = index;
        ClearImage();
        if (_active) Schedule(70);
    }

    internal void InvalidateContent()
    {
        if (_disposed) return;
        _revision++;
        if (!_active) return;
        _current?.Cancel();
        Schedule(800);
    }

    internal void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active) Schedule(10);
        else
        {
            _timer.Stop(); _selection++;
            ClearImage();
            _current?.Cancel();
            StartWorker(); // Release the PDF and renderer even when the user keeps editing.
        }
    }

    private void Schedule(int delay)
    {
        _selection++;
        _queued = false;
        _status.Text = _image.Source == null ? _localize("Reader.Loading") : "";
        _timer.Stop(); _timer.Interval = TimeSpan.FromMilliseconds(delay); _timer.Start();
    }

    private void StartScheduled(object? sender, EventArgs args)
    {
        _timer.Stop(); StartWorker();
    }

    private void StartWorker()
    {
        _queued = true;
        if (_pumping || _disposed) return;
        _pumping = true;
        _worker = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (_queued && !_disposed)
            {
                _queued = false;
                using var attempt = new CancellationTokenSource();
                _current = attempt;
                var revision = _revision;
                try
                {
                    if (!_active || _readerRevision != revision) await ReleaseReaderAsync();
                    if (!_active || _disposed) continue;
                    if (_page < 0 || _page >= _pageCount()) { _status.Text = ""; continue; }
                    if (_reader == null)
                    {
                        _reader = await OpenSnapshotAsync(attempt.Token);
                        _readerRevision = revision;
                        if (_reader.Pages.Count != _pageCount()) throw new InvalidDataException("Reader.RenderFailed");
                    }
                    var page = _page; var selection = _selection;
                    attempt.Token.ThrowIfCancellationRequested();
                    var width = Math.Clamp((int)Math.Ceiling(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX), 640, 1800);
                    var file = await _reader.Backend.RenderAsync(page, width, attempt.Token);
                    var image = await Task.Run(() => LoadImage(file), attempt.Token);
                    if (!_disposed && _active && revision == _revision && selection == _selection)
                    {
                        _image.Source = image;
                        if (_canvasPage != page)
                        {
                            _editableCanvas = _canvas(page);
                            _overlay.SetSlide(_editableCanvas);
                            _canvasPage = page;
                        }
                        _overlay.SetBitmap(image);
                        SelectionChanged?.Invoke();
                        UpdateViewport();
                        AutomationProperties.SetName(_image, _localize("OfficeEditor.Sheet.Slide") + " " + (page + 1));
                        _status.Text = "";
                    }
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    // Cancelling a reader page request terminates its private worker.
                    await ReleaseReaderAsync();
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    await ReleaseReaderAsync();
                    if (!_disposed && _active && revision == _revision)
                    {
                        ClearImage();
                        ComponentAuditFailures.Report(error);
                        _status.Text = _localize(error.Message.StartsWith("Reader.", StringComparison.Ordinal) ? error.Message : "Reader.RenderFailed");
                        OwnedProcessRegistry.Log("office_slide_preview_failed", "Office.Editor", detail: error.GetType().Name);
                    }
                }
                finally { _current = null; }
            }
        }
        finally { _pumping = false; }
    }

    private async Task<ReaderDocument> OpenSnapshotAsync(CancellationToken token)
    {
        var bytes = await _snapshot(token);
        var path = Path.Combine(Path.GetTempPath(), "LOPATA-slide-" + Guid.NewGuid().ToString("N") + _extension);
        try
        {
            var snapshot = await Task.Run(() => OfficeSlideFormat.ForEditorPreview(bytes, _extension), token);
            await File.WriteAllBytesAsync(path, snapshot, token);
            return await Task.Run(() => ReaderPreparation.OpenAsync(path, null, token, useCache: false), token);
        }
        finally { File.Delete(path); }
    }

    private static BitmapImage LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = stream; result.EndInit(); result.Freeze();
        return result;
    }

    private async Task ReleaseReaderAsync()
    {
        var reader = _reader; _reader = null; _readerRevision = -1;
        if (reader != null) await reader.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Tick -= StartScheduled;
        _current?.Cancel(); _image.Source = null;
        await _worker;
        await ReleaseReaderAsync();
    }
}
