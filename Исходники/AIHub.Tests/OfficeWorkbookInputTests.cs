using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AIHub.Controls.OfficeTextEditor;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public class OfficeWorkbookInputTests
{
    [TestMethod]
    public void ClipboardPreservesTabsNewlinesQuotesAndEmptyCells()
    {
        string[] values = ["текст", "", "две\tколонки", "две\nстроки", "5\""];
        var data = string.Join("\t",values.Select(WorkbookClipboard.Quote))+"\r\n\r\n";
        var rows = WorkbookClipboard.Parse(data);
        CollectionAssert.AreEqual(values,rows[0]);
        CollectionAssert.AreEqual(new[]{""},rows[1]);
        Assert.ThrowsExactly<InvalidDataException>(() => WorkbookClipboard.Parse("\"missing end"));
    }

    [TestMethod, TestCategory("Native")]
    public Task TimberFormulaCanBeBuiltWithPointerAndReopened()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_TIMBER_INPUT");
        if (string.IsNullOrEmpty(source)) Assert.Inconclusive("Set LOPATA_TIMBER_INPUT to the timber XLSX.");
        return OfficeProjectUiThread.RunAsync(() => Exercise(source));
    }

    private static async Task Exercise(string source)
    {
        var original = File.ReadAllBytes(source);
        var loc = new LocalizationService(); loc.Load("ru");
        var temp = Directory.CreateTempSubdirectory("lopata-timber-input-");
        var copy = Path.Combine(temp.FullName,Path.GetFileName(source)); File.Copy(source,copy);
        var group = new ReaderWindowGroup(null,loc.T,false);
        var window = group.OpenEditor(copy);
        GetCursorPos(out var old);
        try
        {
            window.WindowState=WindowState.Normal; window.Left=35; window.Top=30; window.Width=1050; window.Height=650;
            window.Topmost=true; window.Activate();
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!Find<OfficeWorkbookView>(window).Any()) { Assert.IsTrue(DateTime.UtcNow < until); await Task.Delay(90); }
            var view = Find<OfficeWorkbookView>(window).Single();
            var grid = Find<DataGrid>(view).Single();
            var formula = Find<TextBox>(view).Single(x => AutomationProperties.GetAutomationId(x)=="Office.Workbook.Formula");
            async Task<DataGridCell> Cell(int row,int col)
            {
                var item=grid.Items[row-1]; var column=grid.Columns[col-1];
                grid.ScrollIntoView(item,column); grid.UpdateLayout(); await Task.Delay(70);
                return Find<DataGridCell>(grid).Single(c=>c.Column==column && c.DataContext==item);
            }
            async Task Aim(DataGridCell cell)
            {
                var p=cell.PointToScreen(new Point(cell.ActualWidth/2,cell.ActualHeight/2));
                Assert.IsTrue(SetCursorPos((int)p.X,(int)p.Y)); await Task.Delay(70);
                Assert.IsTrue(cell.IsMouseOver,"Pointer must be over the requested cell");
                GetCursorPos(out var actual);
                Assert.AreEqual(new WindowInteropHelper(window).Handle,GetAncestor(WindowFromPoint(actual),2));
            }
            async Task Click(int row,int col)
            {
                await Aim(await Cell(row,col)); mouse_event(2,0,0,0,0); mouse_event(4,0,0,0,0); await Task.Delay(55);
            }
            void Type(string text)
            {
                var focus=(IInputElement)Keyboard.FocusedElement!;
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current,focus,text));
            }
            async Task Enter()
            {
                keybd_event(13,0,0,0); keybd_event(13,0,2,0); await Task.Delay(80);
            }
            void Command(string name) => Find<Button>(view).Single(b=>AutomationProperties.GetAutomationId(b)=="Office.Workbook."+name)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Click(2,11); Type("=");
            Assert.IsTrue(formula.IsKeyboardFocused); Assert.AreEqual("=",formula.Text);
            await Click(2,5); Type("*"); await Click(2,6); Type("+"); await Click(2,10);
            Assert.AreEqual("=E2*F2+J2",formula.Text);
            await Enter();
            WorkbookCell Read(byte[] bytes, int row,int col)
            {
                using var book=new OfficeWorkbook(bytes,"Лист1");book.Select(book.Sheets[0]);return book.CellAt(new(row,col));
            }
            var saved=await view.ExportWorkbookAsync();
            Assert.AreEqual("=E2*F2+J2",Read(saved,2,11).Input);
            Assert.AreEqual(1720d,double.Parse(Read(saved,2,11).Display));
            Command("Undo");Assert.AreEqual("",Read(await view.ExportWorkbookAsync(),2,11).Input);
            Command("Redo");Assert.AreEqual("=E2*F2+J2",Read(await view.ExportWorkbookAsync(),2,11).Input);

            await Click(3,11); Command("SUM");
            await Aim(await Cell(2,6)); mouse_event(2,0,0,0,0);
            try { await Task.Delay(50); await Aim(await Cell(4,6)); }
            finally { mouse_event(4,0,0,0,0); }
            await Task.Delay(80);
            Assert.AreEqual("=SUM(F2:F4)",formula.Text); await Enter();
            var bytes=await view.ExportWorkbookAsync();
            Assert.AreEqual(29d,double.Parse(Read(bytes,3,11).Display));

            await Click(4,11); Type("="); await Click(2,5);
            keybd_event(27,0,0,0); keybd_event(27,0,2,0); await Task.Delay(80);
            Assert.AreEqual("",Read(await view.ExportWorkbookAsync(),4,11).Input);
            var targetCell=await Cell(4,11); targetCell.Focus();
            Assert.IsTrue(grid.BeginEdit());
            await Task.Delay(50);
            var inline=Find<TextBox>(targetCell).Single();
            inline.Focus(); inline.SelectAll(); Type("="); await Click(2,5); Type("-"); await Click(2,6);
            Assert.AreEqual("=E2-F2",inline.Text); await Enter();
            Assert.AreEqual(6d,double.Parse(Read(await view.ExportWorkbookAsync(),4,11).Display));
            Command("Undo");
            await Click(4,11);
            view.PasteTable("10\t20\r\n30\t40");
            Assert.AreEqual("40",Read(await view.ExportWorkbookAsync(),5,12).Input);
            view.PasteTable("999\t=");
            Assert.AreEqual("10",Read(await view.ExportWorkbookAsync(),4,11).Input,"Invalid paste must leave the entire block intact.");
            Command("Undo"); Assert.AreEqual("",Read(await view.ExportWorkbookAsync(),5,12).Input);
            Command("Redo"); Assert.AreEqual("10",Read(await view.ExportWorkbookAsync(),4,11).Input);
            Command("Clear"); Assert.AreEqual("",Read(await view.ExportWorkbookAsync(),4,11).Input);
            Command("Undo"); Assert.AreEqual("10",Read(await view.ExportWorkbookAsync(),4,11).Input);
            Find<Button>(window).Single(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.Save")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            until=DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(copy+".bak") || !view.IsEnabled) { Assert.IsTrue(DateTime.UtcNow < until); await Task.Delay(80); }
            var disk=File.ReadAllBytes(copy);
            Assert.AreEqual("=E2*F2+J2",Read(disk,2,11).Input);
            Assert.AreEqual("=SUM(F2:F4)",Read(disk,3,11).Input);
            if(Environment.GetEnvironmentVariable("LOPATA_TIMBER_RESULT") is {} path) File.WriteAllBytes(path,disk);
            CollectionAssert.AreEqual(original,File.ReadAllBytes(source));
            CollectionAssert.AreEqual(original,File.ReadAllBytes(copy+".bak"));
        }
        finally
        {
            var discard = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
            discard.Tick += (_,_) => { foreach(Window dialog in window.OwnedWindows) Find<Button>(dialog)
                .FirstOrDefault(b=>AutomationProperties.GetAutomationId(b)=="Office.Editor.Close.Discard")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            discard.Start();
            try { await window.CloseEditorAsync(); }
            finally { discard.Stop(); SetCursorPos(old.X,old.Y); temp.Delete(true); }
        }
    }

    private static IEnumerable<T> Find<T>(DependencyObject p) where T : DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++)
        {
            var child=VisualTreeHelper.GetChild(p,i);if(child is T match)yield return match;
            foreach(var sub in Find<T>(child))yield return sub;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Pixel { public int X,Y; }
    [DllImport("user32")] private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32")] private static extern bool GetCursorPos(out Pixel p);
    [DllImport("user32")] private static extern nint WindowFromPoint(Pixel p);
    [DllImport("user32")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32")] private static extern void mouse_event(uint flags,int x,int y,uint data,nuint extra);
    [DllImport("user32")] private static extern void keybd_event(byte key,byte scan,uint flags,nuint extra);
}
