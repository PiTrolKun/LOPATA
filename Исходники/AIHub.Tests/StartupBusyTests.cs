using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class StartupBusyTests
{
    [TestMethod]
    public Task BusyRingAnimatesWhileWorkerRunsAndStopsWhenHidden() => ScenarioNavigationTests.Sta(() =>
    {
        var localization = new LocalizationService(); localization.Load("ru");
        var busy = new StartupBusyControl { Message = localization.T("Startup.Checking") };
        var window = new Window { Content = busy, Width = 760, Height = 540, ShowInTaskbar = false, Left = -10000 };
        window.Resources["PanelBrush"] = Brushes.White;
        window.Resources["LineBrush"] = Brushes.LightGray;
        window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["TextPrimaryBrush"] = Brushes.Black;
        window.Resources["UiCardTitleFontSize"] = 20d;
        try
        {
            window.Show();
            var rotation = (RotateTransform)busy.FindName("SpinnerRotation");
            var before = rotation.Angle;
            var worker = Task.Run(async () => await Task.Delay(250));
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
            Assert.IsTrue(worker.IsCompletedSuccessfully);
            Assert.IsTrue(rotation.HasAnimatedProperties);
            Assert.IsTrue(Math.Abs(rotation.Angle - before) > 10, "A visible ring must actually advance.");
            busy.Visibility = Visibility.Collapsed;
            Assert.IsFalse(rotation.HasAnimatedProperties);
            busy.Visibility = Visibility.Visible;
            Assert.IsTrue(rotation.HasAnimatedProperties);
            window.Close();
            Assert.IsFalse(rotation.HasAnimatedProperties);
        }
        finally { window.Close(); }
    });
}
