using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Controls.OfficeReader;
using AIHub.Services;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeNativeDraftExportWindowTests
{
    [TestMethod, DataRow(".epub"), DataRow(".pptx"), TestCategory("Native")]
    public async Task FileExportFromAnExistingChapterOrSlideDraftWritesItsNativeFormat(string extension)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_NATIVE_SECTION_UI") != "1") Assert.Inconclusive("Opt in to native Office UI tests.");
        await OfficeProjectUiThread.RunAsync(async () =>
        {
            var folder = Directory.CreateTempSubdirectory("lopata-native-export-").FullName;
            var project = Path.Combine(folder, "Проект" + OfficePackage.Extension);
            var output = Path.Combine(folder, "Материал" + extension);
            var kind = extension == ".epub" ? OfficeSheetKind.Book : OfficeSheetKind.Presentation;
            var work = await OfficeTextWork.OpenAsync(null, CancellationToken.None, firstKind: kind);
            try
            {
                var draft = new OfficeSectionDraft(1, [new() { Title = "Первая часть", Body = "Из окна ЛОПАТЫ" }, new() { Title = "Вторая часть", Body = "Продолжение" }]);
                await work.WriteSheetAsync(OfficeSheets.PrimaryId, JsonSerializer.SerializeToUtf8Bytes(draft), CancellationToken.None);
                await work.SaveAsync(project, CancellationToken.None);
            }
            finally { work.Close(true); }
            var loc = new LocalizationService(); loc.Load("ru");
            var group = new ReaderWindowGroup(null, loc.T, false);
            var window = group.Open(project);
            try
            {
                var until = DateTime.UtcNow.AddSeconds(25);
                while (!Desc<TextBox>(window).Any(b => AutomationProperties.GetAutomationId(b) == "Office.Editor.Sheet.SectionBody"))
                {
                    if (DateTime.UtcNow > until) Assert.Fail("Draft editing controls did not load.");
                    await Task.Delay(100);
                }
                var export = Desc<Menu>(window).Single().Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>())
                    .Single(m => Equals(m.Header, loc.T("OfficeEditor.Export")));
                var dialog = OfficeSheetWindowTests.CommonDialog(window, output);
                export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await dialog;
                until = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(output))
                {
                    if (DateTime.UtcNow > until) Assert.Fail("Native export did not reach the selected file.");
                    await Task.Delay(80);
                }
                var reopened = OfficeNativeDocument.Open(await File.ReadAllBytesAsync(output), extension);
                Assert.AreEqual(2, reopened.Pages.Count);
                Assert.IsTrue(reopened.Pages[0].Blocks.Any(b => b.Text == "Из окна ЛОПАТЫ"));
                Assert.IsTrue(reopened.Pages[1].Blocks.Any(b => b.Text == "Продолжение"));
                Assert.AreEqual(project, window.SourcePath);
            }
            finally
            {
                await window.CloseEditorAsync();
                foreach (var path in new[] { project, output, project + ".bak" }) File.Delete(path);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        });
    }
    private static IEnumerable<T> Desc<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Desc<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
