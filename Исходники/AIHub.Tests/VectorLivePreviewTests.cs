using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorLivePreviewTests
{
    private const string Root = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>";

    [TestMethod]
    public async Task ThinkingUsesSeparateStreamAndCannotBecomeSvgEvenWhenItContainsSvgTags()
    {
        var thought = new StringBuilder(); var drawing = new StringBuilder();
        var input = Delta(new { reasoning_content = "Consider <svg>not a drawing</svg>" })
            + Delta(new { reasoning_content = " then compose", content = Root })
            + Delta(new { content = "<circle r='20'/></svg>" })
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var answer = await LiteraryRawProtocol.ReadAsync(stream,
            new InlineProgress<ModelStreamChunk>(c => drawing.Append(c.Text)), _ => { }, CancellationToken.None,
            requireComplete: true, includeReasoning: false,
            reasoningProgress: new InlineProgress<ModelStreamChunk>(c => thought.Append(c.Text)));
        Assert.AreEqual("Consider <svg>not a drawing</svg> then compose", thought.ToString());
        Assert.AreEqual(Root + "<circle r='20'/></svg>", answer);
        Assert.AreEqual(answer, drawing.ToString()); _ = VectorSvg.Parse(answer);
    }

    [TestMethod]
    public void PrefixFramesKeepGroupsTextAndDefinitionsWithoutAcceptingIncompleteAttributes()
    {
        Assert.IsNull(VectorDraftSvg.TryCreate("Thinking…"));
        Assert.IsNull(VectorDraftSvg.TryCreate("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0"));
        var defs = "<defs><linearGradient id='g'><stop stop-color='green'/></linearGradient></defs>";
        Assert.IsNull(VectorDraftSvg.TryCreate(Root + defs));
        var prefix = Root + defs + "<g fill='url(#g)'><circle cx='40' cy='50' r='20'/><text x='10' y='15'>Кот &amp; 1</text>";
        var first = VectorDraftSvg.TryCreate(prefix)!;
        Assert.IsNotNull(first); Assert.AreEqual(2d, first.Aspect);
        StringAssert.Contains(first.Svg, "Кот &amp; 1"); StringAssert.Contains(first.Svg, "linearGradient");
        // A quoted '>' must not be treated as an XML boundary; no partly received path enters a frame.
        var partial = VectorDraftSvg.TryCreate(prefix + "<path data-note='a>b' d='M10 20 L")!;
        Assert.AreEqual(first.Svg, partial.Svg);
        var second = VectorDraftSvg.TryCreate(prefix + "<path d='M10 20 L30 40'/>")!;
        Assert.IsNotNull(second); StringAssert.Contains(second.Svg, "M10 20 L30 40");
        _ = VectorSvg.Parse(second.Svg);
        Assert.AreEqual(first.Svg, VectorDraftSvg.TryCreate(prefix + "<!-- unfinished")!.Svg);
    }

    [TestMethod]
    public void EveryDraftUsesTheFinishedSvgSafetyPolicy()
    {
        foreach (var attack in new[] { "<script>alert(1)</script>", "<image href='https://example.com/x.png'/>",
            "<rect onload='run()'/>", "<rect fill='url(file:///C:/test)'/>", "<foreignObject/>",
            "<style>@import url(https://example.com);</style>", "<g xml:base='file:///C:/test'>", "<use href='#x'/>" })
            Assert.IsNull(VectorDraftSvg.TryCreate(Root + "<circle r='20'/>" + attack), attack);
        Assert.IsNull(VectorDraftSvg.TryCreate(Root.Replace("200 100", "NaN 100") + "<circle/>"));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => VectorDraftSvg.TryCreate(Root + "<circle/>", cancel.Token));
    }

    [TestMethod]
    public void TransientStateKeepsStreamsAcrossViewsAndResetsBetweenInferenceAttempts()
    {
        var state = new VectorLiveState(); var text = new string('x', 100000);
        state.Append(new(VectorStreamKind.Reasoning, text)); state.Append(new(VectorStreamKind.Svg, Root));
        var first = state.Read(-1, 0, 0, true);
        Assert.AreEqual(text, first.Reasoning); Assert.AreEqual(Root, first.Answer);
        state.Append(new(VectorStreamKind.Reasoning, " next"));
        var delta = state.Read(first.Epoch, first.ReasoningLength, first.SvgLength, true);
        Assert.AreEqual(" next", delta.Reasoning); Assert.IsNull(delta.Answer);
        Assert.AreEqual(text + " next", state.Read(-1, 0, 0, false).Reasoning);
        state.Append(new(VectorStreamKind.Reset, "")); state.Append(new(VectorStreamKind.Reasoning, "New attempt"));
        var reset = state.Read(first.Epoch, first.ReasoningLength, first.SvgLength, true);
        Assert.AreNotEqual(first.Epoch, reset.Epoch); Assert.AreEqual("New attempt", reset.Reasoning); Assert.AreEqual("", reset.Answer);
    }

    [TestMethod]
    public void SavedHeavyModelResultProducesSafeProgressiveFramesAndPreservesFinalGeometry()
    {
        var original = VectorExamples.Svg(VectorExamples.All.Single(x => x.Id == "workshop"));
        var frames = 0; string? last = null;
        for (var length = 512; length < original.Length; length += 512)
        {
            var frame = VectorDraftSvg.TryCreate(original[..length]);
            if (frame is null || frame.Svg == last) continue;
            _ = VectorSvg.Parse(frame.Svg); last = frame.Svg; frames++;
        }
        Assert.IsTrue(frames >= 10, "Expected multiple real intermediate frames, including gradients and grouped shapes.");
        var final = VectorDraftSvg.TryCreate(original)!;
        Assert.IsNotNull(final);
        var expected = VectorSvg.Parse(original); var actual = VectorSvg.Parse(final.Svg);
        Assert.AreEqual(expected.Root!.Attribute("viewBox")!.Value, actual.Root!.Attribute("viewBox")!.Value);
        Assert.AreEqual(expected.Descendants().Count(), actual.Descendants().Count());
        CollectionAssert.AreEqual(expected.Descendants().Attributes("d").Select(x => x.Value).ToArray(),
            actual.Descendants().Attributes("d").Select(x => x.Value).ToArray());
        Console.WriteLine("Safe progressive frames from preserved heavy SVG: " + frames);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task NativeLiveViewShowsThinkingThenUpdatesSvgWithoutNavigatingOrLosingLastGoodFrame(bool fullAudit)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var folder = Path.Combine(Path.GetTempPath(), "lopata-live-" + Guid.NewGuid().ToString("N"));
                using var audit = ComponentAudit.UseTestStore(folder, fullAudit);
                var previous = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
                var localizer = new LocalizationService(); localizer.Load(fullAudit ? "en" : "ru");
                var state = new VectorLiveState(); state.Append(new(VectorStreamKind.Reasoning, "Обдумываю композицию кота…"));
                using var view = new VectorLivePreview(state, key => localizer.T("Vector." + key));
                var window = new Window { Content = view, Width = 900, Height = 650, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    window.Show();
                    await Until(() => Reasoning(view).Text.Length > 0);
                    Assert.AreEqual("Обдумываю композицию кота…", Reasoning(view).Text);
                    Assert.AreEqual(Visibility.Visible, Reasoning(view).Visibility);
                    var svg = Root + "<rect width='200' height='100' fill='white'/><circle cx='40' cy='50' r='30' fill='red'/>";
                    state.Append(new(VectorStreamKind.Svg, svg));
                    await Until(() => Reasoning(view).Visibility == Visibility.Collapsed);
                    var browser = Browser(view); var source = browser.CoreWebView2.Source; var navigations = 0;
                    browser.CoreWebView2.NavigationStarting += (_, _) => navigations++;
                    await ImageReady(browser); var first = await Capture(browser);
                    state.Append(new(VectorStreamKind.Svg, "<circle cx='140' cy='50' r='30' fill='blue'/>"));
                    await UntilAsync(async () => (await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('vector').src")).Contains("?v=", StringComparison.Ordinal));
                    await ImageReady(browser); var second = await Capture(browser);
                    Assert.AreNotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)),
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(second)));
                    Assert.AreSame(browser, Browser(view)); Assert.AreEqual(source, browser.CoreWebView2.Source); Assert.AreEqual(0, navigations);
                    var url = await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('vector').src");
                    state.Append(new(VectorStreamKind.Svg, "<script>alert(1)</script>")); await Task.Delay(1500);
                    Assert.AreEqual(url, await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('vector').src"));
                    Assert.AreEqual(Visibility.Collapsed, Reasoning(view).Visibility);
                    var output = Path.Combine(Path.GetTempPath(), "lopata-live-ui"); Directory.CreateDirectory(output);
                    await File.WriteAllBytesAsync(Path.Combine(output, fullAudit ? "en-full.png" : "ru-fast.png"), second);
                    state.Append(new(VectorStreamKind.Reset, "")); state.Append(new(VectorStreamKind.Reasoning, "Новая попытка"));
                    await Until(() => Reasoning(view).Visibility == Visibility.Visible && Reasoning(view).Text == "Новая попытка");
                    view.Dispose(); state.Append(new(VectorStreamKind.Svg, svg)); // A closed view cannot start another browser.
                    await Task.Delay(300); Assert.AreEqual(0, ScenarioNavigationTests.LogicalDescendants(view).OfType<VectorBrowserView>().Count());
                    complete.SetResult();
                }
                catch (Exception error) { complete.SetException(error); }
                finally
                {
                    window.Close(); ComponentLicenseGate.ConfirmAsync = previous;
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(50));
    }
    private static TextBox Reasoning(VectorLivePreview view) => ScenarioNavigationTests.LogicalDescendants(view).OfType<TextBox>().Single();
    private static VectorBrowserView Browser(VectorLivePreview view) => ScenarioNavigationTests.LogicalDescendants(view).OfType<VectorBrowserView>().Single();
    private static string Delta(object delta) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta } } }) + "\n\n";
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 350; i++) { if (condition()) return; await Task.Delay(50); } Assert.Fail("Live view state did not arrive."); }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    { for (var i = 0; i < 100; i++) { if (await condition()) return; await Task.Delay(50); } Assert.Fail("Browser frame did not arrive."); }
    private static Task ImageReady(VectorBrowserView browser) => UntilAsync(async () => await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('vector').complete && document.getElementById('vector').naturalWidth > 0") == "true");
    private static async Task<byte[]> Capture(VectorBrowserView browser)
    {
        using var stream = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
        return stream.ToArray();
    }
}
