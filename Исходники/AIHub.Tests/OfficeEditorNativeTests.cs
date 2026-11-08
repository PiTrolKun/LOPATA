using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls.OfficeEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeEditorNativeTests
{
    [TestMethod, TestCategory("Native")]
    public Task CaptureCanSwitchOnlyAfterUserChoice() => Sta(async () => {
        if (Environment.GetEnvironmentVariable("LOPATA_EDITOR_NATIVE") != "1") Assert.Inconclusive("Explicit native editor test required.");
        var localizer = new LocalizationService(); localizer.Load("ru");
        var previous = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        EditorWindow? first = null, second = null;
        void Toggle(EditorWindow target) => target.GetType().GetMethod("ToggleCapture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, null);
        try
        {
            first = new(null, localizer.T, true); first.Show(); await Started(first);
            second = new(null, localizer.T, true); second.Show(); await Started(second);
            Toggle(first); Assert.AreSame(first, EditorWindow.CaptureOwner);
            void Choose(string id)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                var deadline = DateTime.UtcNow.AddSeconds(5);
                timer.Tick += (_, _) => {
                    foreach (Window dialog in second.OwnedWindows)
                    {
                        if (Find(dialog, id) is not System.Windows.Controls.Button button) continue;
                        timer.Stop(); button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); return;
                    }
                    if (DateTime.UtcNow > deadline) { timer.Stop(); foreach (Window dialog in second.OwnedWindows) dialog.Close(); }
                };
                timer.Start(); Toggle(second); timer.Stop();
            }
            Choose("Office.Editor.Capture.Cancel"); Assert.AreSame(first, EditorWindow.CaptureOwner);
            Choose("Office.Editor.Capture.Switch"); Assert.AreSame(second, EditorWindow.CaptureOwner);
            Assert.AreEqual(WindowState.Minimized, second.WindowState);
            Assert.IsNull(first.SavedPath); Assert.IsNull(second.SavedPath);
            second.WindowState = WindowState.Normal; Assert.AreSame(second, EditorWindow.CaptureOwner);
            Toggle(second); Assert.IsNull(EditorWindow.CaptureOwner);
        }
        finally
        {
            if (second is not null) { second.Close(); await Until(() => !second.IsVisible, 20); }
            if (first is not null) { first.Close(); await Until(() => !first.IsVisible, 20); }
            ComponentLicenseGate.ConfirmAsync = previous;
        }
    });

    private static DependencyObject? Find(DependencyObject root, string id)
    {
        if (System.Windows.Automation.AutomationProperties.GetAutomationId(root) == id) return root;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find(System.Windows.Media.VisualTreeHelper.GetChild(root, index), id) is { } match) return match;
        return null;
    }

    [TestMethod, TestCategory("Native")]
    public Task ExternalReadOnlySelectionPersistsWithoutStealingFocus() => Sta(async () => {
        if (Environment.GetEnvironmentVariable("LOPATA_EDITOR_NATIVE") != "1") Assert.Inconclusive("Explicit native editor test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeEditor_20261108", "selection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var localizer = new LocalizationService(); localizer.Load("ru");
        var previous = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        EditorWindow? editor = null; System.Diagnostics.Process? source = null;
        try
        {
            editor = new(null, localizer.T, true); editor.Show(); await Started(editor);
            editor.GetType().GetMethod("ToggleCapture", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
            Assert.IsNull(editor.SavedPath, "Enabling capture must not create a native file.");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "-NoProfile", "-STA", "-File", Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeEditor_20261108", "selection-source.ps1"), "-Folder", root }) start.ArgumentList.Add(argument);
            source = System.Diagnostics.Process.Start(start)!; await Until(() => File.Exists(Path.Combine(root, "ready")), 20); await Task.Delay(500);
            source.Refresh();
            // UI Automation belongs on an MTA worker, never on the host WPF
            // dispatcher: cross-process focus can otherwise wait for that same dispatcher.
            var sourceHwnd = source.MainWindowHandle;
            await Task.Run(() => {
                var foreignField = System.Windows.Automation.AutomationElement.FromHandle(sourceHwnd).FindFirst(
                    System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Edit));
                foreignField.SetFocus();
            }).WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(root, "reselect"), ""); await Task.Delay(500);
            var foreground = GetForegroundWindow(); var collector = Field<SelectionCapture>(editor, "_collector");
            var selected = await Task.Run(SelectionCapture.ReadSelectedText).WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(root, "uia.json"), JsonSerializer.Serialize(new { foreground = foreground.ToInt64(), sourceHwnd = sourceHwnd.ToInt64(), selected }));
            Assert.IsFalse(string.IsNullOrEmpty(selected), "The external fixture must expose an actual selection before testing collection.");
            Task Capture() => (Task)collector.GetType().GetMethod("CaptureAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collector, null)!;
            await Capture(); await Capture();
            var session = Field<EditorSession>(editor, "_session");
            var text = (await session.RequestAsync(new { action = "text" })).GetString()!;
            StringAssert.Contains(text, "Чужое выделение. Только текущий фрагмент. {x2}");
            Assert.IsNotNull(editor.SavedPath); Assert.IsTrue(File.Exists(editor.SavedPath));
            Assert.AreEqual(WindowState.Minimized, editor.WindowState); Assert.AreEqual(foreground, GetForegroundWindow());
            File.WriteAllText(Path.Combine(root, "clear"), ""); await Task.Delay(300); await Capture();
            Assert.AreEqual(text, (await session.RequestAsync(new { action = "text" })).GetString(), "An empty selection cannot paste old clipboard content.");
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { text, path = editor.SavedPath, unchangedForeground = true, noSelectionIgnored = true }));
        }
        finally
        {
            if (source is not null) { File.WriteAllText(Path.Combine(root, "close"), ""); try { await source.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { source.Kill(true); } source.Dispose(); }
            if (editor is not null) { editor.Close(); await Until(() => !editor.IsVisible, 20); }
            Assert.IsNull(EditorWindow.CaptureOwner); ComponentLicenseGate.ConfirmAsync = previous;
        }
    });
    [TestMethod, TestCategory("Native")]
    public Task RealDocumentEditingCaptureDraftAndReopen() => Sta(async () => {
        if (Environment.GetEnvironmentVariable("LOPATA_EDITOR_NATIVE") != "1") Assert.Inconclusive("Explicit native editor test required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeEditor_20261108", "wpf"); Directory.CreateDirectory(root);
        var localizer = new LocalizationService(); localizer.Load("ru");
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        EditorWindow? window = null;
        try
        {
            window = new(null, localizer.T, true); window.Show();
            await Started(window);
            var session = Field<EditorSession>(window, "_session");
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            await session.RequestAsync(new { action = "focus" }); await Task.Delay(200);
            var native = Field<EditorNativeHost>(window, "_host").DocumentHandle;
            var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
            Assert.IsTrue(GetGUIThreadInfo(GetWindowThreadProcessId(native, out _), ref info));
            foreach (var ch in "Native keyboard ") PostMessage(info.Focus, 0x102, (nint)ch, 0);
            await Task.Delay(300);
            Assert.IsTrue((await session.RequestAsync(new { action = "text" })).GetString()!.Contains("Native keyboard"), "Native view must accept actual keyboard messages.");
            await session.RequestAsync(new { action = "insert", text = "Тестовый документ.\nПривет, мир!" });
            Assert.IsTrue((await session.RequestAsync(new { action = "text" })).GetString()!.Contains("Привет, мир!"));
            await session.RequestAsync(new { action = "capture", text = "Повтор {x2}" });
            await session.RequestAsync(new { action = "capture", text = "Повтор {x2}" });
            await session.RequestAsync(new { action = "capture", text = "Повтор {x2}" });
            var text = (await session.RequestAsync(new { action = "text" })).GetString()!;
            Assert.IsTrue(text.Contains("Повтор {x2} {x3}"), text);
            var path = Path.Combine(root, "result.LOPATAdoxAI"); await session.SavePackageAsync(path);
            Assert.IsTrue(File.Exists(session.Draft.Payload)); Assert.IsTrue(File.Exists(path));
            window.Topmost = true; await Task.Delay(1000);
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var bounds);
            using (var shot = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            using (var graphics = System.Drawing.Graphics.FromImage(shot))
            {
                var dc = graphics.GetHdc(); try { PrintWindow(hwnd, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                shot.Save(Path.Combine(root, "editor.png"));
            }
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { text, state = await session.RequestAsync(new { action = "state" }) }));
            window.Close(); await Until(() => !window.IsVisible, 20);
            var savedHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            window = new(null, localizer.T, true, path); window.Show(); await Started(window);
            session = Field<EditorSession>(window, "_session");
            Assert.AreEqual(text, (await session.RequestAsync(new { action = "text" })).GetString(), "Reopening the native container must preserve the exact document text.");
            await session.RequestAsync(new { action = "insert", text = " Несохранённая правка." });
            CollectionAssert.AreEqual(savedHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)), "Editing a private draft cannot overwrite the opened source.");
            File.WriteAllText(Path.Combine(root, "reopen.json"), JsonSerializer.Serialize(new { exactText = true, unchangedSource = true, draft = session.Draft.Payload }));
            window.Close(); await Until(() => !window.IsVisible, 20);
            CollectionAssert.AreEqual(savedHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)), "Closing preserves the draft and cannot silently save over the source.");
        }
        finally
        {
            if (window is not null) { window.Close(); await Until(() => !window.IsVisible, 20); }
            ComponentLicenseGate.ConfirmAsync = previous;
        }
    });
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static async Task Started(EditorWindow window)
    {
        await Task.Delay(200); await Field<Task>(window, "_startup").WaitAsync(TimeSpan.FromSeconds(100));
        Assert.IsTrue(Field<bool>(window, "_ready"), Field<System.Windows.Controls.TextBlock>(window, "_status").Text);
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { public int Size, Flags; public nint Active, Focus, Capture, Menu, Move, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, int flags);
    private static async Task Until(Func<bool> condition, int seconds)
    { var end = DateTime.UtcNow.AddSeconds(seconds); while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("Editor state timed out"); await Task.Delay(100); } }
    private static Task Sta(Func<Task> run)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () => {
                try { await run(); result.SetResult(); } catch (Exception error) { result.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }
}
