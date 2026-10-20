using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Books;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

public sealed partial class BookConversionUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task DiagnosticsShowLiveStagesAndRuntimeSeparatelyFromFinalReports(string language, bool dark) => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "book-log-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var owner = new Window(); Theme(owner, dark); owner.Show();
        var localizer = new LocalizationService(); localizer.Load(language);
        var window = new BookConversionWindow(owner, localizer.T) { Width = 1150, Height = 840 };
        try
        {
            window.Show(); Click(Find<Button>(window, "Simple"));
            var details = Find<Expander>(window, "Diagnostics"); details.IsExpanded = true;
            var tabs = Find<TabControl>(window, "DiagnosticsTabs");
            Assert.AreEqual(localizer.T("BookConverter.Report"), details.Header);
            CollectionAssert.AreEqual(new[] { localizer.T("BookConverter.Logs"), localizer.T("BookConverter.Reports") },
                tabs.Items.Cast<TabItem>().Select(tab => (string)tab.Header).ToArray());
            Assert.AreEqual(0, tabs.SelectedIndex);
            var logs = (TextBox)((TabItem)tabs.Items[0]).Content;
            var reports = (TextBox)((TabItem)tabs.Items[1]).Content;
            Assert.IsTrue(logs.IsReadOnly && reports.IsReadOnly);
            await Invoke(window, "BeginDiagnostics", false);
            var stage = new BookConversionProgress("structure", 0, 0, "book.epub", Message: "BookConverter.Smart.StructureProgress")
            { Section = 3, SectionCount = 12, Item = 2, ItemCount = 7 };
            await Invoke(window, "UpdateProgress", stage);
            StringAssert.Contains(logs.Text, string.Format(localizer.T("BookConverter.LogSection"), 3, 12));
            StringAssert.Contains(logs.Text, string.Format(localizer.T("BookConverter.LogBatch"), 2, 7));
            Assert.AreEqual("", reports.Text, "In-progress events belong to logs, not the final report.");
            var bar = Desc<System.Windows.Controls.ProgressBar>(window).Single();
            Assert.IsTrue(bar.IsIndeterminate);
            await Invoke(window, "UpdateProgress", new BookConversionProgress("diagnostic", 0, 1, Message: "Document context: input=3798"));
            Assert.IsTrue(bar.IsIndeterminate, "Runtime log messages must not change operation progress.");
            StringAssert.Contains(logs.Text, "Document context: input=3798");
            await Render(window); SaveEvidence(window, "logs-" + language + "-" + dark);
            // A real simple conversion uses the same log/report surface without a model or shared background state.
            var source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "Авторский текст для проверки журнала.");
            await Invoke(window, "AddPathsAsync", (object)new[] { source });
            Find<System.Windows.Controls.ComboBox>(window, "Format").SelectedItem = "FB2";
            Find<TextBox>(window, "OutputDirectory").Text = root;
            await ConvertAndApproveReview(window, root);
            StringAssert.Contains(logs.Text, localizer.T("BookConverter.LogStarted"));
            StringAssert.Contains(logs.Text, localizer.T("BookConverter.Progress.Completed"));
            Assert.IsFalse(logs.Text.Contains("Document context: input=3798", StringComparison.Ordinal), "New conversion starts a new visible log.");
            StringAssert.Contains(reports.Text, string.Format(localizer.T("BookConverter.Summary"), 1, 0, 1, 0));
            Assert.IsFalse(reports.Text.Contains(localizer.T("BookConverter.NoWarnings"), StringComparison.Ordinal), "A real format warning must remain in the report.");
            Assert.AreEqual(1, Directory.GetFiles(root, "*.fb2").Length);
            tabs.SelectedIndex = 1; await Render(window); SaveEvidence(window, "reports-" + language + "-" + dark);
            await Invoke(window, "CompleteDiagnostics", new BookConversionResult { Failures = [new(source, "fixture failure")] });
            StringAssert.Contains(reports.Text, "fixture failure"); StringAssert.Contains(logs.Text, "fixture failure");
            for (var i = 0; i < 50; i++) await Invoke(window, "AppendLog", new string('x', 3900) + "tail-" + i);
            Assert.IsTrue(logs.Text.Length <= 160000); StringAssert.Contains(logs.Text, "tail-49");
        }
        finally { window.Close(); owner.Close(); Directory.Delete(root, true); }
    });
}
