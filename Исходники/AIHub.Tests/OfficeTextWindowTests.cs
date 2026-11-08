using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Microsoft.Web.WebView2.Core;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeTextWindowTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ReaderEditorReaderKeepsWindowUndoAndOriginal()
    {
        var sample = Environment.GetEnvironmentVariable("LOPATA_TEXT_EDITOR_SAMPLE");
        if (string.IsNullOrWhiteSpace(sample)) Assert.Inconclusive("Set LOPATA_TEXT_EDITOR_SAMPLE to a local test DOCX.");
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await RoundTripAsync(sample); result.SetResult(); }
                catch (Exception error) { result.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }

    private static async Task RoundTripAsync(string sample)
    {
        var directory = Directory.CreateTempSubdirectory("lopata-editor-test-").FullName;
        var copy = Path.Combine(directory, "template.docx");
        File.Copy(sample, copy);
        var original = SHA256.HashData(File.ReadAllBytes(copy));
        var localization = new LocalizationService();
        localization.Load("ru");
        var group = new ReaderWindowGroup(null, localization.T, false);
        var previousLicenseGate = ComponentLicenseGate.ConfirmAsync;
        var window = group.Open();
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.FromCanceled(new CancellationToken(true));
            var cancelledView = new OfficeTextEditorView(window, localization.T, false);
            string unusedDraft;
            try
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledView.InitializeAsync(copy, CancellationToken.None));
                var work = (OfficeTextWork)typeof(OfficeTextEditorView).GetField("_work",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cancelledView)!;
                unusedDraft = work.TemporaryProjectPath;
            }
            finally { await cancelledView.DisposeAsync(); }
            Assert.IsFalse(File.Exists(unusedDraft), "Cancelling the initial editor setup must not add an untouched source to recovery.");
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            await window.OpenAsync(copy);
            var status = Find<TextBlock>(window, "Office.Reader.Status");
            Assert.AreEqual(localization.T("Reader.ReadOnly"), status?.Text, status?.ToolTip?.ToString());
            var handle = new WindowInteropHelper(window).Handle;
            var timing = Stopwatch.StartNew();
            Find<Button>(window, "Office.Reader.Editor")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.SwitchToEditingAsync();
            var view = Find<OfficeTextEditorView>(window, "Office.Editor.View");
            Assert.IsNotNull(view, Find<TextBlock>(window, "Office.Reader.Status")?.Text);
            var browser = Find<OfficeTextBrowser>(view)!;
            var token = CancellationToken.None;
            string marker = "OfficeIntegrationTest";
            var originalText = (await browser.RequestAsync(new { action = "text" }, token)).GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(originalText));
            await browser.RequestAsync(new { action = "focus" }, token);
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = marker }));
            Assert.IsTrue((await browser.RequestAsync(new { action = "text" }, token)).GetString()!.Contains(marker),
                "Native browser text input must edit the document before any test export.");
            var editMs = timing.ElapsedMilliseconds;
            var evidence = Environment.GetEnvironmentVariable("LOPATA_TEXT_EDITOR_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                await using var png = File.Create(Path.Combine(evidence, "editor-document.png"));
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
            }
            Button read = Find<Button>(view, "Office.Editor.Read")!;
            read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !window.IsEditing);
            Assert.AreEqual(handle, new WindowInteropHelper(window).Handle);
            Assert.AreEqual(copy, window.SourcePath);
            CollectionAssert.AreEqual(original, SHA256.HashData(File.ReadAllBytes(copy)));
            // The reader's actual page search runs against the freshly generated PDF.
            var readerDocument = (ReaderDocument)typeof(ReaderWindow).GetField("_document",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var match = await readerDocument.FindAsync(marker, 0, token);
            Assert.IsNotNull(match.Page, "Read mode must show the uncommitted edit.");
            await window.SwitchToEditingAsync();
            Assert.AreSame(view, Find<OfficeTextEditorView>(window, "Office.Editor.View"));
            await browser.RequestAsync(new { action = "command", name = "Undo" }, token);
            Assert.AreEqual(originalText, (await browser.RequestAsync(new { action = "text" }, token)).GetString());
            Assert.AreEqual(handle, new WindowInteropHelper(window).Handle);
            CollectionAssert.AreEqual(original, SHA256.HashData(File.ReadAllBytes(copy)));
            if (!string.IsNullOrEmpty(evidence))
                await File.WriteAllTextAsync(Path.Combine(evidence, "reader-editor.json"), JsonSerializer.Serialize(new
                { sameWindow = true, actualKeyboardInput = true, changedReadPage = match.Page, originalUnmodified = true,
                  sameEditingSurface = true, undoAfterReading = true, editorOpenAndFirstInputMs = editMs }));
        }
        finally
        {
            try
            {
                // Only discard the throwaway copy, and only through the normal close prompt.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    foreach (Window dialog in window.OwnedWindows)
                        if (Find<Button>(dialog, "Office.Editor.Close.Discard") is { } discard)
                        { discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break; }
                };
                timer.Start();
                try
                {
                    await window.CloseEditorAsync();
                    if (window.IsVisible) window.Close();
                    await UntilAsync(() => !window.IsVisible);
                }
                finally { timer.Stop(); }
            }
            finally
            {
                ComponentLicenseGate.ConfirmAsync = previousLicenseGate;
                File.Delete(copy);
                Directory.Delete(directory);
            }
        }
    }

    private static T? Find<T>(DependencyObject node, string? id = null) where T : DependencyObject
    {
        if (node is T found && (id is null || AutomationProperties.GetAutomationId(node) == id)) return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (Find<T>(VisualTreeHelper.GetChild(node, index), id) is { } child) return child;
        return null;
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(80);
        while (!predicate())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Office window did not reach the requested mode.");
            await Task.Delay(100);
        }
    }
}
