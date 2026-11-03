using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task ComposerFailurePreservesDraftAndUsableNativeSend() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true);
        fixture.Input.Text = "Draft kept when the optional editor cannot start.";
        var previous = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.FromException(new IOException("Synthetic composer setup failure."));
            fixture.Control.ConfigureFlowInput(); fixture.Window.UpdateLayout();
            var browser = fixture.Descendants.OfType<LetItOutComposerBrowser>().Single();
            Pump(() => browser.Visibility == Visibility.Collapsed);
            Assert.IsTrue(fixture.Input.IsVisible);
            Assert.IsTrue(fixture.Button("LetItOut.Send").IsVisible);
            fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
            Assert.AreEqual("Draft kept when the optional editor cannot start.", fixture.Runtime.Requests[0].Messages[^1].Content);
            fixture.Runtime.Complete(0, "Complete."); Pump(() => !fixture.Control.IsWorking);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    });

    [TestMethod]
    public Task ComposerSharesBottomEdgeWithStopOnWideAndNarrowWindows() => Sta(() =>
    {
        using var fixture = new Fixture("ru", true);
        foreach (var width in new[] { 1600d, 620d })
        {
            fixture.Window.Width = width; fixture.Window.UpdateLayout();
            var input = fixture.Input;
            var stop = fixture.Button("LetItOut.Stop");
            var inputBottom = input.TranslatePoint(new Point(0, input.ActualHeight), fixture.Control).Y;
            var stopBottom = stop.TranslatePoint(new Point(0, stop.ActualHeight), fixture.Control).Y;
            Assert.IsTrue(Math.Abs(fixture.Control.ActualHeight - stopBottom) < 12);
            Assert.IsTrue(Math.Abs(inputBottom - stopBottom) < 32, "STOP must not create an empty row beneath the editor.");
            var inputRight = input.TranslatePoint(new Point(input.ActualWidth, 0), fixture.Control).X;
            var stopLeft = stop.TranslatePoint(new Point(0, 0), fixture.Control).X;
            Assert.IsTrue(inputRight < stopLeft, "A compact window must keep STOP outside the composer.");
        }
    });

    [TestMethod]
    public Task NativeComposerWrapsAroundSendAndPreservesPlainTextEditing() => Sta(() =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_LETITOUT_COMPOSER_NATIVE") != "1")
            Assert.Inconclusive("Opt in to the installed WebView2 integration with LOPATA_LETITOUT_COMPOSER_NATIVE=1.");
        using var fixture = new Fixture("ru", true);
        var captures = Path.Combine(Path.GetTempPath(), "lopata-let-it-out-display-ui");
        Directory.CreateDirectory(captures);
        fixture.Control.ConfigureFlowInput(); fixture.Window.UpdateLayout();
        var browser = fixture.Descendants.OfType<LetItOutComposerBrowser>().Single();
        Pump(() => browser.IsReady, 60000);
        var draft = new string('а', 5000);
        fixture.Input.Text = draft;
        Script("composer.focus(); document.getElementById('scroll').scrollTop=0; true");
        Assert.AreEqual(draft, JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")));
        // Inspect actual glyph positions, not just the width of the input rectangle.
        var geometry = Script("""
            (()=>{const edit=document.getElementById('edit'),s=document.getElementById('scroll');
            const t=edit.firstChild, box=s.getBoundingClientRect();let upper=0,lower=0,maxLower=0;
            for(let i=0;i<t.length;i++){const r=document.createRange();r.setStart(t,i);r.setEnd(t,i+1);
            const b=r.getBoundingClientRect();if(b.top>=box.bottom||b.bottom<=box.top)continue;
            if(b.bottom<box.bottom-48)upper=Math.max(upper,b.right);
            if(b.top>box.bottom-40){lower++;maxLower=Math.max(maxLower,b.right);}}
            return {upper,lower,maxLower,right:box.right};})()
            """);
        using (var positions = JsonDocument.Parse(geometry))
        {
            var p = positions.RootElement;
            Assert.IsTrue(p.GetProperty("lower").GetInt32() > 0, "Text must reach the bottom beside Send.");
            Assert.IsTrue(p.GetProperty("upper").GetDouble() > p.GetProperty("right").GetDouble() - 50, "Rows above Send use the available full width.");
            Assert.IsTrue(p.GetProperty("maxLower").GetDouble() < p.GetProperty("right").GetDouble() - 50, "Bottom rows avoid the Send button.");
        }
        var editResult = Script("composer.set('Первая строка\\nВторая строка');composer.focus();({inserted:document.execCommand('insertText',false,'!'),value:document.getElementById('edit').innerText,active:document.activeElement.id})");
        File.WriteAllText(Path.Combine(captures, "composer-edit-debug.json"), editResult);
        Pump(() => fixture.Input.Text.EndsWith('!'));
        Script("document.execCommand('undo');true");
        Pump(() => fixture.Input.Text == "Первая строка\nВторая строка");
        Script("document.execCommand('redo');true");
        Pump(() => fixture.Input.Text == "Первая строка\nВторая строка!");
        var multiline = Script("document.execCommand('insertText',false,'\\nЕщё одна строка');({value:document.getElementById('edit').innerText,html:document.getElementById('edit').innerHTML})");
        File.WriteAllText(Path.Combine(captures, "composer-multiline-debug.json"), multiline);
        Pump(() => fixture.Input.Text.EndsWith("Ещё одна строка"));
        Assert.AreEqual("Первая строка\nВторая строка!\nЕщё одна строка", fixture.Input.Text);
        Script("composer.set('Начало: ');composer.focus();const data=new DataTransfer();data.setData('text/plain','раз\\nдва');data.setData('text/html','<b>НЕ ТЕКСТ</b>');document.getElementById('edit').dispatchEvent(new ClipboardEvent('paste',{clipboardData:data,bubbles:true,cancelable:true}));true");
        Pump(() => fixture.Input.Text == "Начало: раз\nдва");
        Assert.IsFalse(Script("document.getElementById('edit').innerHTML").Contains("<b>"));
        Script("document.execCommand('undo');true"); Pump(() => fixture.Input.Text == "Начало: ");
        Assert.AreEqual("true", Script("document.getElementById('edit').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',shiftKey:true,bubbles:true,cancelable:true}))"), "Shift+Enter remains available for native newline insertion.");
        Assert.AreEqual(0, fixture.Runtime.Requests.Count);
        foreach (var width in new[] { 1100d, 620d })
        {
            fixture.Window.Width = width; fixture.Window.UpdateLayout();
            fixture.Input.Text = draft;
            Script("composer.focus();document.getElementById('scroll').scrollTop=999999;true");
            Assert.AreEqual(draft, JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")));
            Screenshot(fixture.Window, "composer-" + width + "-dark.png");
            BrowserShot("browser-" + width + "-dark.png");
        }
        fixture.Control.Resources["TextPrimaryBrush"] = Brushes.Black;
        fixture.Window.Background = Brushes.White; fixture.Control.Resources["HeaderBackgroundBrush"] = Brushes.White;
        fixture.Language = "en"; fixture.Control.Configure(fixture.Localize); fixture.Window.UpdateLayout();
        Pump(() => JsonSerializer.Deserialize<string>(Script("document.getElementById('hint').textContent")) == fixture.Localize("LetItOut.InputHint"));
        Assert.AreEqual("rgb(0, 0, 0)", JsonSerializer.Deserialize<string>(Script("getComputedStyle(document.body).color")));
        Screenshot(fixture.Window, "composer-620-light.png");
        BrowserShot("browser-620-light.png");
        fixture.Input.Text = "A real multiline\nrequest.";
        Script("document.getElementById('edit').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));true");
        Pump(() => fixture.Runtime.Requests.Count == 1);
        Assert.AreEqual("A real multiline\nrequest.", fixture.Runtime.Requests[0].Messages[^1].Content);
        Pump(() => JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")) == "");
        fixture.Runtime.Complete(0, "Complete."); Pump(() => !fixture.Control.IsWorking);
        fixture.Input.Text = "Second turn from the arrow.";
        Pump(() => Script("document.getElementById('send').disabled") == "false");
        Script("document.getElementById('send').click();true"); Pump(() => fixture.Runtime.Requests.Count == 2);
        fixture.Input.Text = "Keep the next draft.";
        fixture.Runtime.Complete(1, "Second answer."); Pump(() => !fixture.Control.IsWorking);
        Assert.AreEqual("Keep the next draft.", JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")));
        var reloading = false; var cancelled = false;
        browser.CoreWebView2.NavigationStarting += (_, e) => { reloading = true; cancelled = e.Cancel; };
        browser.CoreWebView2.Reload(); Pump(() => reloading);
        Assert.IsTrue(cancelled, "A browser reload must not replace the user's draft document.");
        Assert.AreEqual("Keep the next draft.", JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText")));

        string Script(string source)
        {
            var task = browser.ExecuteScriptAsync(source);
            Pump(() => task.IsCompleted, 30000);
            return task.GetAwaiter().GetResult();
        }
        void BrowserShot(string name)
        {
            using var output = File.Create(Path.Combine(captures, name));
            var task = browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, output);
            Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
        }
    });
}
