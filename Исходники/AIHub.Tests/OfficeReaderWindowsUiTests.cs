using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls.OfficeReader;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderWindowsUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task FormatWindowPersistsManualChoicesWithoutWritingWindowsAssociations(string language, bool dark)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(() =>
            {
                var owner = new Window(); ReaderWindowsSettingsWindow? window = null;
                try
                {
                    var localization = new LocalizationService(); localization.Load(language);
                    var settings = new FileViewerSettings(); var saves = 0;
                    owner.Show();
                    window = new(owner, localization.T, dark, settings, () => saves++, null); window.Show(); window.UpdateLayout();
                    Assert.AreEqual(localization.T("Reader.Windows.Formats"), window.Title);
                    var choices = Desc<CheckBox>(window).ToArray(); Assert.AreEqual(ReaderFileRequest.Extensions.Count, choices.Length);
                    var pdf = choices.Single(c => AutomationProperties.GetAutomationId(c) == "Office.Reader.Windows.pdf");
                    Assert.IsNull(pdf.IsChecked); pdf.IsChecked = true; Assert.IsTrue(settings.PreferInternalByExtension[".pdf"]);
                    pdf.IsChecked = false; Assert.IsFalse(settings.PreferInternalByExtension[".pdf"]);
                    pdf.IsChecked = null; Assert.IsFalse(settings.PreferInternalByExtension.ContainsKey(".pdf")); Assert.AreEqual(3, saves);
                    Assert.AreEqual(localization.T("Reader.Windows.Automatic"), pdf.Content);
                    Assert.AreEqual(1, Desc<Button>(window).Count(b => AutomationProperties.GetAutomationId(b) == "Office.Reader.Windows.DefaultApps"));
                    window.Close(); window = new(owner, localization.T, dark, settings, () => saves++, null); window.Show(); window.UpdateLayout();
                    Assert.IsNull(Desc<CheckBox>(window).Single(c => AutomationProperties.GetAutomationId(c) == "Office.Reader.Windows.pdf").IsChecked);
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally { window?.Close(); owner.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
}
