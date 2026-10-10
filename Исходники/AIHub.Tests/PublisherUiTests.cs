using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class PublisherUiTests
{
    [TestMethod]
    public async Task TablesAndWizardRenderInBothLanguagesAndThemesAtNarrowWidth()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "lopata-publisher-ui-" + Guid.NewGuid().ToString("N"));
            PublisherCoordinator? coordinator = null;
            try
            {
                var store = new PublisherStore(directory);
                coordinator = new PublisherCoordinator(store, new GitHub(), new Vk());
                var direction = new PublisherDirection { Repository = "owner/repo", CommunityId = 42, CommunityName = "Community" };
                coordinator.AddAsync(direction, "FAKE_UI_TEST_TOKEN").GetAwaiter().GetResult();
                foreach (var language in new[] { "ru", "en" })
                foreach (var dark in new[] { false, true })
                {
                    var localizer = new LocalizationService(); localizer.Load(language);
                    var owner = new Window { Width = 480, Height = 560, ShowInTaskbar = false, ShowActivated = false };
                    owner.Resources["PanelBrush"] = dark ? Brushes.Black : Brushes.White;
                    owner.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
                    owner.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.WhiteSmoke;
                    owner.Resources["LineBrush"] = Brushes.Gray; owner.Resources["UiBodyFontSize"] = 14.0;
                    owner.Resources["InputBrush"] = dark ? Brushes.DarkSlateGray : Brushes.White;
                    owner.Resources["StepBadgeBrush"] = dark ? Brushes.MidnightBlue : Brushes.LightBlue;
                    owner.Resources["AccentBrush"] = Brushes.DodgerBlue;
                    owner.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DarkSlateGray;
                    owner.Show();
                    var window = new PublisherWindow(owner, localizer.T, coordinator, new Vk(), new GitHub()) { Width = 480, Height = 560 };
                    window.Show(); window.UpdateLayout();
                    Assert.IsTrue(Descendants(window).Any(d => AutomationProperties.GetAutomationId(d) == "Publisher.Directions"));
                    Assert.IsTrue(Descendants(window).OfType<WrapPanel>().Any());
                    Assert.IsTrue(Descendants(window).OfType<ScrollViewer>().Any());
                    Assert.IsFalse(Descendants(window).OfType<TextBlock>().Any(t => t.Text.StartsWith("Publisher.", StringComparison.Ordinal)));
                    VerifyTableTheme(Descendants(window).OfType<DataGrid>().Single(), owner);
                    var menu = Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Publisher.Actions").ContextMenu;
                    menu!.IsOpen = true; menu.UpdateLayout();
                    Assert.AreEqual(((SolidColorBrush)owner.Resources["PanelBrush"]).Color, ((SolidColorBrush)menu.Background).Color);
                    var menuItem = (MenuItem)menu.Items[0]; menuItem.ApplyTemplate();
                    Assert.AreEqual(((SolidColorBrush)owner.Resources["TextPrimaryBrush"]).Color, ((SolidColorBrush)menuItem.Foreground).Color);
                    Assert.IsNotNull(menuItem.Template); menu.IsOpen = false;
                    var releases = new PublisherReleasesWindow(window, localizer.T, coordinator, direction.Id) { Width = 480, Height = 560 };
                    releases.Show(); releases.UpdateLayout();
                    var releaseTable = Descendants(releases).OfType<DataGrid>().Single();
                    Assert.AreEqual(0, releaseTable.Items.Count);
                    Descendants(releases).OfType<CheckBox>().Single().IsChecked = true;
                    releases.UpdateLayout(); Assert.AreEqual(1, releaseTable.Items.Count);
                    VerifyTableTheme(releaseTable, owner);
                    Assert.IsNotNull(((DataGridCheckBoxColumn)releaseTable.Columns[0]).ElementStyle.BasedOn);
                    releases.Close();
                    var wizard = new PublisherWizardWindow(window, localizer.T, coordinator, new Vk(), new GitHub()) { Width = 480, Height = 560 };
                    wizard.Show(); wizard.UpdateLayout();
                    Assert.IsTrue(Descendants(wizard).OfType<PasswordBox>().Any());
                    Assert.IsTrue(Descendants(wizard).OfType<ScrollViewer>().Any());
                    var cancel = Descendants(wizard).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Publisher.Wizard.Cancel");
                    var position = cancel.TranslatePoint(new Point(0, 0), wizard);
                    Assert.IsTrue(position.Y + cancel.ActualHeight <= wizard.ActualHeight);
                    var secret = Descendants(wizard).OfType<PasswordBox>().Single(); secret.Password = "FAKE_UI_TEST_TOKEN";
                    Descendants(wizard).OfType<TextBox>().Single().Text = "https://vk.ru/club42";
                    var next = Descendants(wizard).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Publisher.Wizard.Next");
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); wizard.UpdateLayout();
                    Descendants(wizard).OfType<TextBox>().Single().Text = "https://github.com/owner/repo/releases";
                    next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); wizard.UpdateLayout();
                    Assert.IsTrue(Descendants(wizard).OfType<CheckBox>().Any());
                    Assert.IsFalse(Descendants(wizard).OfType<TextBlock>().Any(t => t.Text.StartsWith("Publisher.", StringComparison.Ordinal)));
                    wizard.Close(); window.Close(); owner.Close();
                }
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
            finally
            {
                coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                var path = Path.GetFullPath(directory);
                if (path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(path).StartsWith("lopata-publisher-ui-", StringComparison.Ordinal) && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void VerifyTableTheme(DataGrid table, Window owner)
    {
        table.SelectedIndex = 0; table.UpdateLayout();
        var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.IsNotNull(row);
        var cells = VisualDescendants(row).OfType<DataGridCell>().ToArray();
        Assert.IsTrue(cells.Length > 0);
        foreach (var cell in cells)
        {
            Assert.AreEqual(((SolidColorBrush)owner.Resources["StepBadgeBrush"]).Color, ((SolidColorBrush)cell.Background).Color);
            Assert.AreEqual(((SolidColorBrush)owner.Resources["TextPrimaryBrush"]).Color, ((SolidColorBrush)cell.Foreground).Color);
        }
        table.SelectedIndex = -1; table.UpdateLayout();
        Assert.AreEqual(((SolidColorBrush)owner.Resources["PanelBrush"]).Color, ((SolidColorBrush)cells[0].Background).Color);
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        foreach (var child in VisualDescendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private sealed class GitHub : IPublisherGitHubClient
    { public Task<List<PublisherRelease>> ReadAsync(string repo, CancellationToken token) => Task.FromResult(new List<PublisherRelease>
        { new(1, "v1", "Release 1", "Changes", DateTimeOffset.UtcNow, "https://github.com/owner/repo/releases/tag/v1") }); }
    private sealed class Vk : IPublisherVkClient
    {
        public Task<(long Id, string Name)> CheckAsync(string address, string secret, CancellationToken token) => Task.FromResult((42L, "Community"));
        public Task<long> PostAsync(long community, string secret, string message, string guid, CancellationToken token) => throw new InvalidOperationException("UI tests must never post.");
    }
}
