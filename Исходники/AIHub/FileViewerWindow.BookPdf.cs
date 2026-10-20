using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub;

/// <summary>Read-only original PDF pages for book comparison, rendered by the existing local Windows PDF tool.</summary>
public partial class FileViewerWindow
{
    private readonly CancellationTokenSource _bookPdfLifetime = new();
    private Button? _bookPdfPrevious, _bookPdfNext;
    private TextBlock? _bookPdfCounter;
    private int _bookPdfPage = 1, _bookPdfCount;

    private async Task ShowBookPdfAsync()
    {
        try
        {
            StatusText.Text = T("BookConverter.Review.PdfLoading");
            _bookPdfCount = await Task.Run(() =>
            { using var pdf = UglyToad.PdfPig.PdfDocument.Open(_path); return pdf.NumberOfPages; }, _bookPdfLifetime.Token);
            if (_bookPdfLifetime.IsCancellationRequested) return;
            if (_bookPdfCount < 1) throw new InvalidDataException("Empty PDF");
            HideAll();
            var host = (Grid)ImageViewerScroll.Parent; host.Children.Remove(ImageViewerScroll);
            var panel = new DockPanel(); host.Children.Add(panel);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
            _bookPdfPrevious = new Button { Content = "◀", Padding = new Thickness(14, 6, 14, 6), ToolTip = T("BookConverter.Review.PreviousPage") };
            _bookPdfNext = new Button { Content = "▶", Padding = new Thickness(14, 6, 14, 6), ToolTip = T("BookConverter.Review.NextPage") };
            _bookPdfCounter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Foreground = FileNameText.Foreground };
            AutomationProperties.SetAutomationId(_bookPdfPrevious, "BookConverter.Original.PreviousPage");
            AutomationProperties.SetAutomationId(_bookPdfNext, "BookConverter.Original.NextPage");
            AutomationProperties.SetAutomationId(_bookPdfCounter, "BookConverter.Original.PageCounter");
            _bookPdfPrevious.Click += async (_, _) => { if (_bookPdfPage > 1) await RenderBookPdfPageAsync(_bookPdfPage - 1); };
            _bookPdfNext.Click += async (_, _) => { if (_bookPdfPage < _bookPdfCount) await RenderBookPdfPageAsync(_bookPdfPage + 1); };
            toolbar.Children.Add(_bookPdfPrevious); toolbar.Children.Add(_bookPdfCounter); toolbar.Children.Add(_bookPdfNext);
            panel.Children.Add(ImageViewerScroll); ImageViewerScroll.Visibility = Visibility.Visible;
            ImageViewer.Stretch = Stretch.Uniform; ImageViewer.VerticalAlignment = VerticalAlignment.Top;
            ImageViewer.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            ImageViewerScroll.SizeChanged += (_, _) => ImageViewer.Width = Math.Max(1, ImageViewerScroll.ActualWidth - 24);
            await RenderBookPdfPageAsync(1);
        }
        catch (OperationCanceledException) when (_bookPdfLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_bookPdfLifetime.IsCancellationRequested) StatusText.Text = T("BookConverter.Review.PdfError") + " " + error.Message;
        }
    }

    private async Task RenderBookPdfPageAsync(int page)
    {
        var output = Path.Combine(Path.GetTempPath(), "LOPATA-original-pdf-" + Guid.NewGuid().ToString("N") + ".png");
        _bookPdfPrevious!.IsEnabled = false; _bookPdfNext!.IsEnabled = false;
        StatusText.Text = T("BookConverter.Review.PdfLoading");
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "Tools", "rag_pdf_render.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException(T("BookConverter.Review.PdfError"));
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            await FileMetadataProcess.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-File", script, "-InputPdf", _path,
                "-Page", page.ToString(CultureInfo.InvariantCulture), "-OutputPng", output], _bookPdfLifetime.Token);
            _bookPdfLifetime.Token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(output);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            ImageViewer.Source = bitmap; ImageViewer.Width = Math.Max(1, ImageViewerScroll.ActualWidth - 24);
            ImageViewerScroll.ScrollToTop(); _bookPdfPage = page;
            _bookPdfCounter!.Text = string.Format(T("BookConverter.Review.PageCounter"), page, _bookPdfCount);
            StatusText.Text = T("BookConverter.Review.PdfReadOnly");
        }
        catch (OperationCanceledException) when (_bookPdfLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_bookPdfLifetime.IsCancellationRequested) StatusText.Text = T("BookConverter.Review.PdfError") + " " + error.Message;
        }
        finally
        {
            if (File.Exists(output)) { try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (!_bookPdfLifetime.IsCancellationRequested)
            { _bookPdfPrevious.IsEnabled = _bookPdfPage > 1; _bookPdfNext.IsEnabled = _bookPdfPage < _bookPdfCount; }
        }
    }
}
