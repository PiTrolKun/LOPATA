using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class AboutHelpTests
{
    [TestMethod]
    public async Task HelpSectionsRenderLocalizedContentInBothLanguagesAndThemes()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var language in new[] { "ru", "en" })
                foreach (var dark in new[] { false, true })
                {
                    var localizer = new LocalizationService(); localizer.Load(language);
                    var owner = new Window { Width = 460, Height = 360, ShowActivated = false, ShowInTaskbar = false };
                    owner.Resources["PanelBrush"] = dark ? Brushes.Black : Brushes.White;
                    owner.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
                    owner.Show();
                    var help = new AboutWindow(owner, "LOPATA", "0.3.32-dev", localizer.T, () => { });
                    var sections = Descendants(help).OfType<Expander>().ToArray();
                    foreach (var key in new[] { "Navigation", "Generation", "ImageUtility", "Shell", "Capture", "Finance", "Import", "Background", "Updates" })
                    {
                        var title = localizer.T("About.Guide." + key + "Title");
                        var section = sections.Single(s => s.Header is TextBlock heading && heading.Text == title);
                        section.IsExpanded = true;
                        Assert.AreEqual(localizer.T("About.Guide." + key), ((TextBlock)section.Content).Text);
                        Assert.IsFalse(((TextBlock)section.Content).Text.StartsWith("About.", StringComparison.Ordinal));
                        Assert.AreEqual(owner.Resources["TextPrimaryBrush"], ((TextBlock)section.Content).Foreground);
                    }
                    help.Measure(new Size(760, 640)); help.Arrange(new Rect(0, 0, 760, 640)); help.UpdateLayout();
                    Assert.IsTrue(Descendants(help).OfType<ScrollViewer>().Any());
                    help.Close(); owner.Close();
                }
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
