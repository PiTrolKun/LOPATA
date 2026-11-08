using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeSheetWindowTests
{
    [TestMethod, TestCategory("Native")]
    public async Task FileBecomesProjectAndTypedSheetsSurviveReopen()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_OFFICE_SHEETS_UI") != "1")
            Assert.Inconclusive("Set LOPATA_OFFICE_SHEETS_UI=1 to drive the native project window.");
        await OfficeProjectUiThread.RunAsync(RunAsync);
    }

    [AssemblyCleanup]
    public static void CloseProjectUiThread() => OfficeProjectUiThread.Close();

    private static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(),"LOPATA-sheet-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var artifact = Path.Combine(folder,"Исходник.bin");
        byte[] original = [3, 17, 0, 234, 255];
        await File.WriteAllBytesAsync(artifact,original);
        var target = Path.Combine(folder,"Вложения" + OfficePackage.Extension);
        var loc = new LocalizationService(); loc.Load("ru");
        var owner = new Window { ShowInTaskbar = false, Width = 820, Height = 590 };
        var view = new OfficeTextEditorView(owner,loc.T,true);
        var host = new Grid();
        host.Children.Add(view); owner.Content = host; owner.Show();
        try
        {
            // Start with a real file and explicitly decline project creation once.
            await view.InitializeAsync(artifact,CancellationToken.None);
            var sourceSession = Work(view);
            Assert.IsFalse(Get<FrameworkElement>(view,"Office.Editor.SheetStrip").IsVisible);
            Assert.IsNull(view.DetachProjectStripForReader());
            var dialog = CommonDialog(owner,null);
            await ProjectCommandAsync(view);
            await dialog;
            Assert.IsFalse(Get<FrameworkElement>(view,"Office.Editor.SheetStrip").IsVisible);
            dialog = CommonDialog(owner,target);
            await ProjectCommandAsync(view);
            await dialog;
            Assert.IsTrue(File.Exists(target));
            Assert.IsTrue(Get<FrameworkElement>(view,"Office.Editor.SheetStrip").IsVisible);
            Assert.AreEqual(OfficeSheetKind.Archive,sourceSession.Sheets[0].Kind);
            Assert.AreEqual(OfficeSheetKind.ActivityLog,sourceSession.Sheets[1].Kind);
            Assert.IsFalse(sourceSession.Sheets.Any(s => s.Kind == OfficeSheetKind.AiLog));
            var extra = Path.Combine(folder,"Данные.dat");
            await File.WriteAllBytesAsync(extra,[187, 26, 88]);
            dialog = CommonDialog(owner,extra);
            Click(view,"Office.Editor.Sheet.AddFiles");
            await dialog;
            await Until(() => sourceSession.Sheets[0].Files.Length == 2);
            // Both the '+' and type buttons are the real controls.
            var choice = Choose(owner,"Office.Editor.SheetType.Book");
            Click(view,"Office.Editor.AddSheet"); await choice;
            await Until(() => Find(view,"Office.Editor.Sheet.SectionBody") != null && view.IsEnabled);
            Get<TextBox>(view,"Office.Editor.Sheet.SectionTitle").Text = "Введение";
            Get<TextBox>(view,"Office.Editor.Sheet.SectionBody").Text = "Проверка сохранения главы.";
            Click(view,"Office.Editor.Sheet.AddSection");
            Get<TextBox>(view,"Office.Editor.Sheet.SectionTitle").Text = "Конец";
            Get<TextBox>(view,"Office.Editor.Sheet.SectionBody").Text = "Последняя фраза.";
            var book = view.CurrentSheetId!;
            // The action journal is protected, the first AI entry creates its own sheet.
            var ai = new OfficeAiEntry("sheet-test",book,DateTimeOffset.UtcNow,"Тест перевода","Привет",
                "Переведи на английский.","Hello","completed",[new("sample.model","Test model","abc","translation")]);
            await sourceSession.UpsertAiEntryAsync(ai,CancellationToken.None);
            Click(view,"Office.Editor.Sheet.activity");
            await Until(() => view.CurrentSheetId == "activity" && Find(view,"Office.Editor.Journal.Entries") != null);
            Click(view,"Office.Editor.Sheet.ai");
            await Until(() => Find(view,"Office.Editor.Journal.SystemPrompt") != null);
            var prompt = Get<Expander>(view,"Office.Editor.Journal.SystemPrompt");
            Assert.IsFalse(prompt.IsExpanded);
            prompt.IsExpanded = true;
            Assert.AreEqual(ai.SystemPrompt,((TextBox)prompt.Content).Text);
            Click(view,"Office.Editor.Save");
            await Until(() => !sourceSession.IsDirty);
            Assert.IsTrue(await view.ConfirmCloseAsync());
            await view.DisposeAsync();
            host.Children.Remove(view);
            view = new(owner,loc.T,false); host.Children.Add(view);
            await view.InitializeAsync(target,CancellationToken.None);
            Assert.IsTrue(Get<FrameworkElement>(view,"Office.Editor.SheetStrip").IsVisible);
            await Until(() => Find(view,"Office.Editor.Sheet." + book) != null);
            Click(view,"Office.Editor.Sheet." + book);
            await Until(() => Find(view,"Office.Editor.Sheet.SectionBody") != null);
            Assert.AreEqual("Введение",Get<TextBox>(view,"Office.Editor.Sheet.SectionTitle").Text);
            Get<ListBox>(view,"Office.Editor.Sheet.Sections").SelectedIndex = 1;
            Assert.AreEqual("Последняя фраза.",Get<TextBox>(view,"Office.Editor.Sheet.SectionBody").Text);
            var previewHost = new Grid();
            host.Children.Add(previewHost);
            var strip = view.DetachProjectStripForReader()!;
            previewHost.Children.Add(strip); view.Visibility = Visibility.Collapsed;
            view.ReturnFromReading = () => Task.FromResult(false);
            Click(strip,"Office.Editor.Sheet.activity");
            Assert.AreEqual(book,view.CurrentSheetId);
            Assert.AreSame(previewHost,strip.Parent);
            var returns = 0;
            view.ReturnFromReading = () =>
            {
                returns++;
                view.RestoreProjectStrip(); view.Visibility = Visibility.Visible;
                return Task.FromResult(true);
            };
            Click(strip,"Office.Editor.Sheet." + book);
            Assert.AreEqual(1,returns);
            Assert.AreEqual(book,view.CurrentSheetId);
            Assert.AreEqual(3,Grid.GetRow(strip));
            previewHost.Children.Add(view.DetachProjectStripForReader()!); view.Visibility = Visibility.Collapsed;
            Click(strip,"Office.Editor.Sheet.activity");
            await Until(() => view.CurrentSheetId == "activity");
            Assert.AreEqual(2,returns);
            host.Children.Remove(previewHost);
            var extracted = Path.Combine(folder,"извлечённый.bin");
            var originals = Work(view).Sheets[0].Files;
            Assert.AreEqual(2,originals.Length);
            await Work(view).ExportMaterialAsync(originals.Single(f => f.Name == Path.GetFileName(artifact)),extracted,CancellationToken.None);
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(extracted));
            CollectionAssert.AreEqual(original,await File.ReadAllBytesAsync(artifact));
            await Work(view).ExportMaterialAsync(originals.Single(f => f.Name == Path.GetFileName(extra)),extracted,CancellationToken.None);
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(extra),await File.ReadAllBytesAsync(extracted));
            Assert.IsTrue(await view.ConfirmCloseAsync());
        }
        finally { await view.DisposeAsync(); owner.Close(); }
    }

    private static Task ProjectCommandAsync(OfficeTextEditorView view)
        => (Task)typeof(OfficeTextEditorView).GetMethod("CreateProjectAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null)!;
    private static OfficeTextWork Work(OfficeTextEditorView view)
        => (OfficeTextWork)typeof(OfficeTextEditorView).GetField("_work",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
    private static T Get<T>(DependencyObject root,string id) where T : DependencyObject
        => Find(root,id) as T ?? throw new InvalidOperationException(id);
    private static DependencyObject? Find(DependencyObject root,string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root,i),id) is { } child) return child;
        return null;
    }
    private static void Click(DependencyObject root,string id) =>
        Get<Button>(root,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(50); }
    }
    private static async Task Choose(Window owner,string id)
    {
        await Task.Yield();
        await Until(() => owner.OwnedWindows.OfType<Window>().Any(w => Find(w,id) != null));
        Click(owner.OwnedWindows.OfType<Window>().Single(w => Find(w,id) != null),id);
    }
    private delegate bool EnumerateWindow(nint handle,nint data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,EnumerateWindow callback,nint data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window,EnumerateWindow callback,nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint handle,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint handle);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint window,int id);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetWindowText(nint handle,string text);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint wParam,nint lParam);
    private static string ClassName(nint window)
    {
        var name = new System.Text.StringBuilder(80);
        GetClassName(window,name,name.Capacity);
        return name.ToString();
    }
    internal static Task CommonDialog(Window owner,string? filename)
    {
        var thread = GetWindowThreadProcessId(new WindowInteropHelper(owner).Handle,out _);
        return Task.Run(async () =>
        {
            nint modal = 0;
            try
            {
                await Until(() =>
                {
                    EnumThreadWindows(thread,(w,_) =>
                    {
                        if (ClassName(w) != "#32770" || !IsWindowVisible(w)) return true;
                        modal = w; return false;
                    },0);
                    return modal != 0;
                });
                if (filename != null)
                {
                    nint edit = 0;
                    await Until(() =>
                    {
                        EnumChildWindows(modal,(w,_) =>
                        {
                            if (GetDlgCtrlID(w) is not (1001 or 1148) || ClassName(w) != "Edit") return true;
                            edit = w; return false;
                        },0);
                        return edit != 0;
                    });
                    if (!SetWindowText(edit,filename)) throw new InvalidOperationException("Native file name rejected.");
                }
                var button = GetDlgItem(modal,filename == null ? 2 : 1);
                if (button == 0 || !PostMessage(button,0x00F5,0,0)) throw new InvalidOperationException("Native dialog button missing.");
            }
            catch { if (modal != 0) PostMessage(modal,0x10,0,0); throw; }
        });
    }
}
