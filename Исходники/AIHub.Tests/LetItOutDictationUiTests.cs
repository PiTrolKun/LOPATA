using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AIHub.Controls;
using AIHub.Services.Dictation;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod]
    public Task NativeComposerAcceptsDictationWithoutLosingDraftOrCrossingTopics() => Sta(() =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_LETITOUT_COMPOSER_NATIVE") != "1")
            Assert.Inconclusive("Opt in to the installed WebView2 editor.");
        using var fixture = new Fixture("ru", true);
        fixture.Control.ConfigureFlowInput(); fixture.Window.UpdateLayout();
        var browser = fixture.Descendants.OfType<LetItOutComposerBrowser>().Single();
        Pump(() => browser.IsReady, 60000);
        var handle = new WindowInteropHelper(fixture.Window).Handle;
        Assert.AreEqual(1, RegisteredDictationTarget.In(fixture.Window).Count);
        Assert.AreEqual(0, DictationTextTarget.Editors(fixture.Window).Count, "History and the hidden buffer are not editable dictation targets.");
        Script("composer.set('До старое после');composer.focus();const r=document.createRange();r.setStart(document.getElementById('edit').firstChild,3);r.setEnd(document.getElementById('edit').firstChild,9);getSelection().removeAllRanges();getSelection().addRange(r);true");
        var target = DictationTargetPicker.Automatic(fixture.Window, handle, null) as IAsyncDictationTextTarget;
        Assert.IsNotNull(target);
        target.Reset();
        Apply(target, 0, "прив", false); Apply(target, 0, "привет", false); Apply(target, 0, "привет, мир", true);
        Assert.AreEqual("До привет, мир после", Value());
        Pump(() => fixture.Input.Text == "До привет, мир после");
        Apply(target, 0, "устаревшее", false);
        Assert.AreEqual("До привет, мир после", Value());

        Script("composer.focus();true"); target.Reset();
        Apply(target, 0, "снова", true);
        Assert.AreEqual("До привет, мир после снова", Value());
        Script("document.execCommand('undo');true");
        Assert.AreEqual("До привет, мир после", Value(), "Dictation participates in native Undo.");

        Script("composer.set('Начало: ');composer.focus();true"); target.Reset();
        Apply(target, 0, "слово", false);
        Script("const first=document.getElementById('edit').firstChild;const r2=document.createRange();r2.setStart(first,0);r2.collapse(true);getSelection().removeAllRanges();getSelection().addRange(r2);document.execCommand('insertText',false,'РУЧНО ');composer.focus();true");
        Apply(target, 0, "слово дальше", true);
        Assert.AreEqual("РУЧНО Начало: слово дальше", Value());

        Script("composer.set('Строка 1\\nСтрока 2');composer.focus();true"); target.Reset();
        Apply(target, 0, "ещё", false); Apply(target, 0, "ещё слова", true);
        Assert.AreEqual("Строка 1\nСтрока 2 ещё слова", Value());
        Pump(() => fixture.Input.Text == Value());
        Assert.AreEqual(0, fixture.Runtime.Requests.Count, "Dictation never sends a message.");

        // Native hit testing used by the crosshair must resolve the registered browser too.
        var point = browser.TranslatePoint(new Point(20, 20), fixture.Window);
        var hit = fixture.Window.InputHitTest(point) as DependencyObject;
        Assert.IsNotNull(RegisteredDictationTarget.From(hit) ?? RegisteredDictationTarget.AtPoint(fixture.Window, browser.PointToScreen(new Point(20, 20))));
        Assert.IsNull(RegisteredDictationTarget.AtPoint(fixture.Window, browser.PointToScreen(new Point(-20, -20))));
        fixture.Input.Text = "Новый черновик";
        Pump(() => Value() == "Новый черновик");
        Assert.IsFalse(target.Available);
        Assert.ThrowsExactly<InvalidOperationException>(() => Apply(target, 1, "поздно", true));
        Assert.AreEqual("Новый черновик", Value());
        target = (IAsyncDictationTextTarget)DictationTargetPicker.Automatic(fixture.Window, handle, target)!;
        Assert.IsTrue(target.Available);
        fixture.Click("LetItOut.New");
        Pump(() => Value() == "");
        Assert.IsFalse(target.Available, "A target cannot follow the user into another topic.");
        var replacement = DictationTargetPicker.Automatic(fixture.Window, handle, target)!;
        fixture.Control.Visibility = Visibility.Collapsed; fixture.Window.UpdateLayout();
        Assert.IsFalse(replacement.Available);
        Assert.AreEqual(0, RegisteredDictationTarget.In(fixture.Window).Count);

        string Script(string source)
        {
            var task = browser.ExecuteScriptAsync(source); Pump(() => task.IsCompleted, 30000);
            return task.GetAwaiter().GetResult();
        }
        string Value() => JsonSerializer.Deserialize<string>(Script("document.getElementById('edit').innerText"))!;
        void Apply(IAsyncDictationTextTarget destination, int segment, string text, bool final)
        {
            var task = destination.ApplyAsync(segment, text, final, CancellationToken.None);
            Pump(() => task.IsCompleted, 10000); task.GetAwaiter().GetResult();
        }
    });
}
