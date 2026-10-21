using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCloudWebViewTests
{
    [TestMethod]
    public async Task DeepSeekAdapterRoundTripUsesFinalTextAndStopsPartialReply()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CLOUD_WEBVIEW_SMOKE") != "1")
        { Assert.Inconclusive("Opt in with LOPATA_CLOUD_WEBVIEW_SMOKE=1; local fixture only, no account or network."); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () => {
                try { await FixtureAsync(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public async Task LiveDeepSeekUsesManualSignInThenReceivesOneReply()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_CLOUD_LIVE") != "1")
        { Assert.Inconclusive("Opt-in live site test; manual sign-in, one synthetic request."); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpoints = new List<string>();
        string received = "";
        var thread = new Thread(() => {
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () => {
                CloudAssistantBrowserWindow? browser = null;
                try
                {
                    browser = new(key => key, MusicCloudProviders.DeepSeek);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                    await browser.EnsureAsync(timeout.Token);
                    await browser.RequireSignInAsync(timeout.Token);
                    var answer = await browser.AskAsync("Техническая проверка подключения ЛОПАТЫ. Напиши только: Связь работает.",
                        checkpoints.Add, _ => { }, timeout.Token);
                    received = answer.Text;
                    var proof = Environment.GetEnvironmentVariable("LOPATA_CLOUD_UI_PROOF");
                    if (!string.IsNullOrWhiteSpace(proof))
                    {
                        Directory.CreateDirectory(proof);
                        var web = (WebView2)typeof(CloudAssistantBrowserWindow).GetField("_web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
                        await using var output = File.Create(Path.Combine(proof, "deepseek-live.png"));
                        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    }
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { browser?.Dispose(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(13));
        Assert.IsFalse(string.IsNullOrWhiteSpace(received));
        Assert.AreEqual("sent", checkpoints.Single());
    }

    private static async Task FixtureAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-cloud-webview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        // A rendered-page fixture; uses the actual application browser and actual bundled adapter.
        // Off-origin loads are blocked by virtual-host mapping. No user browser profile is read.
        File.WriteAllText(Path.Combine(folder, "fixture.html"), """
            <html><body><div id="messages">
            <div data-virtual-list-item-key="1"><div class="ds-message d29f3d7d">old question</div></div>
            <div data-virtual-list-item-key="2"><div class="ds-message"><div class="ds-assistant-message-main-content">OLD</div></div></div></div>
            <textarea></textarea><div role="button" class="_52c986b"><svg><path d="M8.3125 0.980206"/></svg></div>
            <script>
            let turn=2; const field=document.querySelector('textarea'), control=document.querySelector('[role="button"]');
            control.onclick=()=>{
              if(control.querySelector('path').getAttribute('d').startsWith('M2 4.88')) {
                control.innerHTML='<svg><path d="M8.3125 0.980206"/></svg>'; return;
              }
              const question=field.value; field.value=''; document.getElementById('messages').innerHTML='';
              const user=document.createElement('div'); user.setAttribute('data-virtual-list-item-key',String(++turn));
              user.innerHTML='<div class="ds-message d29f3d7d"></div>';user.firstChild.textContent=question;
              document.getElementById('messages').append(user);
              const answer=document.createElement('div');answer.setAttribute('data-virtual-list-item-key',String(++turn));
              answer.innerHTML='<div class="ds-message"><div>PRIVATE_REASONING</div><div class="ds-assistant-message-main-content"><p>FINAL</p><a href="https://example.org/source">Source</a></div></div>';
              document.getElementById('messages').append(answer);
              if(question==='stop test') control.innerHTML='<svg><path d="M2 4.88"/></svg>';
            };
            </script></body></html>
            """);
        var browser = new CloudAssistantBrowserWindow(key => key, MusicCloudProviders.DeepSeek);
        try
        {
            var web = (WebView2)typeof(CloudAssistantBrowserWindow).GetField("_web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(folder, "profile"));
            browser.Left = -10000; browser.Top = -10000; browser.Show();
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("chat.deepseek.com", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            using var source = typeof(MusicPoetryWindow).Assembly.GetManifestResourceStream("AIHub.DeepSeekChatDom")!;
            using var reader = new StreamReader(source);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(await reader.ReadToEndAsync());
            var loaded = new TaskCompletionSource(); web.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            web.CoreWebView2.Navigate("https://chat.deepseek.com/fixture.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue((await browser.SnapshotAsync()).Ready);
            var checkpoints = new List<string>(); var partials = new List<string>();
            var answer = await browser.AskAsync("fixture question", checkpoints.Add, partials.Add, CancellationToken.None);
            Assert.AreEqual("sent", checkpoints.Single()); Assert.IsTrue(answer.Text.Contains("FINAL"));
            Assert.IsFalse(answer.Text.Contains("OLD")); Assert.IsFalse(answer.Text.Contains("PRIVATE_REASONING"));
            CollectionAssert.AreEqual(new[] { "https://example.org/source" }, answer.Links);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            await Assert.ThrowsAsync<OperationCanceledException>(() => browser.AskAsync("stop test", _ => { }, partials.Add, cancellation.Token));
            var stopped = await browser.SnapshotAsync();
            Assert.IsFalse(stopped.Generating); Assert.IsTrue(stopped.Interrupted);
            // A manual draft is never overwritten or sent by a subsequent application call.
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('textarea').value='manual draft'");
            await Assert.ThrowsExactlyAsync<IOException>(() => browser.NavigateChatAsync("", CancellationToken.None));
            Assert.AreEqual("manual draft", (await browser.SnapshotAsync()).InputText);
            await Assert.ThrowsExactlyAsync<IOException>(() => browser.AskAsync("new request", _ => Assert.Fail("Must not send"), _ => { }, CancellationToken.None));
            Assert.AreEqual("manual draft", (await browser.SnapshotAsync()).InputText);
        }
        finally { browser.Dispose(); /* Profile teardown is asynchronous; retain this isolated fixture for inspection. */ }
    }
}
