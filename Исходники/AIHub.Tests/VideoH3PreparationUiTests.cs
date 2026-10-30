using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3PreparationUiTests
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(); var thread = new Thread(() =>
        { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); ready.SetResult(d); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root)
        .OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task StandardWindowChecksLocallyThenWaitsForExplicitDownload(string language, bool dark) => Ui.Value.InvokeAsync(() =>
    {
        var l = new LocalizationService(); l.Load(language); var owner = new Window();
        owner.Resources["TextPrimaryBrush"] = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        owner.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(24, 33, 49) : Colors.WhiteSmoke);
        owner.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Color.FromRgb(16, 24, 39) : Colors.White);
        owner.Resources["LineBrush"] = new SolidColorBrush(Colors.Gray);
        owner.Resources["SecondaryButtonBackgroundBrush"] = owner.Resources["PanelBrush"];
        owner.Resources["AccentBrush"] = new SolidColorBrush(Colors.RoyalBlue);
        owner.Show();
        var checks = 0; var downloads = 0; var checkedOnce = new TaskCompletionSource();
        var dialog = new VideoPreparationWindow(owner, l.T, "MiniMax H3 + obvpm-timeline", async (download, _, token) =>
        { token.ThrowIfCancellationRequested(); await Task.Delay(20, token); if (download) downloads++; else checks++; checkedOnce.TrySetResult(); return download; }, default,
            string.Format(l.T("VideoPrep.H3AcquisitionDetails"), "test storage", "54 GB") + "\n" + string.Join("\n", VideoH3Catalog.Parts.Select(p => p.Name)));
        async Task ObserveAsync()
        {
            try
            {
                await checkedOnce.Task; await Task.Delay(30); Assert.AreEqual(1, checks); Assert.AreEqual(0, downloads);
                Assert.AreEqual(650d, dialog.Height); var download = Find<Button>(dialog, "VideoEditor.Preparation.Download");
                Assert.IsTrue(download.IsEnabled); dialog.UpdateLayout();
                Assert.IsTrue(download.TransformToAncestor(dialog).Transform(new Point()).Y < dialog.ActualHeight - 35);
                var folder = Environment.GetEnvironmentVariable("LOPATA_H3_IMAGES");
                if (folder is not null)
                {
                    var view = (FrameworkElement)dialog.Content; var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    System.IO.Directory.CreateDirectory(folder); using var output = System.IO.File.Create(System.IO.Path.Combine(folder, language + "-h3-download.png")); encoder.Save(output);
                }
                download.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch { dialog.Close(); throw; }
        }
        Task? observer = null;
        _ = Ui.Value.BeginInvoke(new Action(() => observer = ObserveAsync()));
        try { var result = dialog.ShowDialog(); observer!.GetAwaiter().GetResult(); Assert.IsTrue(result); Assert.AreEqual(1, downloads); }
        finally { dialog.Close(); owner.Close(); }
    }).Task.WaitAsync(TimeSpan.FromSeconds(30));
    [TestMethod]
    public Task ConfiguringTwiceRoutesOnceAndDoesNotReplaceSavedH3Choice() => Ui.Value.InvokeAsync(() =>
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); p.CurrentStep = "finish";
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, -1, ""));
        var path = f.File("p.lopataAIVid"); VideoPreparationArchive.Save(p, path); var session = new VideoPreparationSession(f.File("recovery")); session.Load(path);
        var l = new LocalizationService(); l.Load("ru"); var control = new VideoPreparationControl(l.T, session);
        var calls = 0; control.ConfigureGeneratorPreparation(_ => calls += 100); control.ConfigureGeneratorPreparation(s =>
        { calls++; Assert.AreEqual("minimax-h3", s.ModelId); });
        Assert.IsTrue(control.Next()); Assert.AreEqual(1, calls); Assert.AreEqual("finish", session.Project.CurrentStep);
        Assert.AreEqual("minimax-h3", VideoGeneratorPlan.Selected(session.Project)!.Settings.ModelId);
    }).Task.WaitAsync(TimeSpan.FromSeconds(30));
}
