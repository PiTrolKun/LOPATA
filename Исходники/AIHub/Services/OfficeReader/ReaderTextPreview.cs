using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Services.OfficeReader;

/// <summary>Uses the editor's packaged, read-only document engine. No office process
/// is involved in DOCX/TXT rendering. A private STA keeps WebView2 off the reader UI.</summary>
internal static class ReaderTextPreview
{
    internal const string MetadataName = "preview.reader.json";
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    internal static readonly string[] Extensions = [".docx", ".txt", ".doc", ".odt", ".rtf", ".md", ".markdown", ".html", ".htm"];

    internal static async Task<string> ExportAsync(string original, string source, string workspace, string extension, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        // Markup resolves permitted local images relative to its original directory.
        var input = extension is ".md" or ".markdown" or ".html" or ".htm" ? original : source;
        var bytes = await OfficeTextFormats.ImportAsync(input, workspace, deadline.Token);
        OfficeTextFormats.InspectDocx(bytes);
        var pdf = Path.Combine(workspace, "preview.pdf");
        await RenderGate.WaitAsync(deadline.Token);
        try { await RenderAsync(bytes, pdf, deadline.Token); }
        finally { RenderGate.Release(); }
        OwnedProcessRegistry.Log("reader_text_preview", "Office.Reader", detail: extension + "; engine=office.text-surface; pages=verified");
        return pdf;
    }

    private static Task RenderAsync(byte[] bytes, string pdf, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                OfficeTextBrowser? browser = null;
                Window? window = null;
                Exception? failure = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    browser = new OfficeTextBrowser();
                    window = new Window { Content = browser, ShowActivated = false, ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                        Left = -32000, Top = -32000, Width = 1100, Height = 900 };
                    window.Show();
                    await browser.InitializeSurfaceAsync(token);
                    await browser.RequestAsync(new { action = "open", base64 = Convert.ToBase64String(bytes), readOnly = true, lang = "en" }, token);
                    using var package = WordprocessingDocument.Open(new MemoryStream(bytes, false), false);
                    var comments = package.MainDocumentPart?.WordprocessingCommentsPart?.Comments?.Elements<Comment>()
                        .Select(c => new { id = c.Id?.Value, text = string.Join("\n", c.Elements<Paragraph>().Select(p => p.InnerText)) }).ToArray();
                    var info = await browser.RequestAsync(new { action = "readerPreview", comments }, token);
                    await ReaderBrowserPdf.WriteAsync(browser.CoreWebView2, pdf, token);
                    var metadata = JsonSerializer.Deserialize<ReaderPreviewInfo>(info.GetRawText()) ?? throw new InvalidDataException("Reader.RenderFailed");
                    using (var document = UglyToad.PdfPig.PdfDocument.Open(pdf))
                        metadata.Validate(document.NumberOfPages);
                    await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(pdf)!, MetadataName), info.GetRawText(), token);
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { browser?.CloseSurface(); window?.Close(); }
                    catch (Exception error) { failure ??= error; }
                    // Complete only after WebView2 releases its file handles.
                    if (failure is OperationCanceledException) completion.TrySetCanceled(token);
                    else if (failure is not null) completion.TrySetException(failure);
                    else completion.TrySetResult();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "LOPATA reader document preview" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}

internal sealed record ReaderPreviewInfo(ReaderPage[] Pages, ReaderHeading[] Headings, ReaderLink[][] Links)
{
    internal void Validate(int count)
    {
        if (Pages is null || Headings is null || Links is null || count != Pages.Length || count != Links.Length ||
            Pages.Any(p => !double.IsFinite(p.Width) || !double.IsFinite(p.Height) || p.Width <= 0 || p.Height <= 0) ||
            Headings.Any(h => h.Page < 0 || h.Page >= count) || Links.Any(page => page is null || page.Any(l =>
                l.Page is int target && (target < 0 || target >= count) || !double.IsFinite(l.Left + l.Top + l.Width + l.Height))))
            throw new InvalidDataException("Reader.RenderFailed");
    }
}
