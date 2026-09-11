using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

static class UiProbe
{
    public static void Run(string root, string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var runtime = new LiteraryChatRuntime(root);
                var words = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText("Исходники/AIHub/Localization/ru.json"))!;
                string L(string key) => words.GetValueOrDefault(key, key);
                var panel = new StackPanel(); var owner = new Window { Content = panel, Width = 960, Height = 750, ShowInTaskbar = false };
                void Brush(string key, string color) => owner.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                Brush("PanelBrush", "#172033"); Brush("SecondaryButtonBackgroundBrush", "#111827");
                Brush("TextPrimaryBrush", "#F8FAFC"); Brush("TextSecondaryBrush", "#A8B6CF"); Brush("ChatUserTextBrush", "#93C5FD");
                Brush("LineBrush", "#34435C"); Brush("AccentBrush", "#2563EB"); owner.Resources["UiBodyFontSize"] = 16d;
                owner.Show();
                foreach (var role in new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor })
                {
                    var layout = new LiteraryProjectLayout(root); var store = new LiteraryPlotAnchorStore(layout, role);
                    var dialogs = new LiteraryDialogueStore(layout, role); var dialog = dialogs.Load();
                    dialog.Messages = [new(true, "Кот"), new(false, "Предложение")]; dialogs.Save(dialog);
                    var chat = new LiteraryChatControl(L, runtime, role, () => "", LiteraryProjectStore.ReadProject(root), directory: root) { Height = 350 };
                    panel.Children.Clear(); panel.Children.Add(chat); owner.UpdateLayout();
                    var history = (List<ImageAnalysisHiddenMessage>)typeof(LiteraryChatControl).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chat)!;
                    Check(history.Count == 2 && history[0].Content == "Кот", "Chat did not restore context");
                    var anchor = Walk(chat).OfType<Button>().Single(b => Equals(b.Content, L("Literary.Anchor.Title")));
                    chat.ActionsBlocked = true; chat.RefreshAvailability(); Check(!anchor.IsEnabled, "Anchor enabled during indexing");
                    chat.ActionsBlocked = false; chat.RefreshAvailability();
                    void Edit(Action<Window, TextBox, Button, Button> action)
                    {
                        Exception? error = null;
                        owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                        {
                            var window = app.Windows.Cast<Window>().Single(w => w != owner);
                            try
                            {
                                var input = Walk(window).OfType<TextBox>().Single();
                                var save = Walk(window).OfType<Button>().Single(b => Equals(b.Content, L("Literary.Anchor.Save")));
                                var cancel = Walk(window).OfType<Button>().Single(b => b.IsCancel);
                                action(window, input, save, cancel);
                            }
                            catch (Exception ex) { error = ex; }
                            finally { if (window.IsVisible) window.Close(); }
                        }));
                        Click(anchor); if (error is not null) throw error;
                    }
                    var plan = role + ": Герой находит кота.\nПродолжить сцену в доме.";
                    Edit((window, input, save, cancel) =>
                    {
                        input.Text = new string('я', 3001); Check(input.Text.Length == 3001 && !save.IsEnabled, "Long paste was truncated or allowed");
                        input.Text = plan; Check(save.IsEnabled, "Save did not recover");
                        window.UpdateLayout();
                        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var file = File.Create(Path.Combine(output, role + ".png"))) encoder.Save(file);
                        Click(save);
                    });
                    Check(store.Load().Text == plan, "Save failed");
                    Edit((window, input, save, cancel) => { Check(input.Text == plan, "Reopen failed"); input.Text = "Cancelled"; Click(cancel); });
                    Check(store.Load().Text == plan, "Cancel changed disk");
                    Edit((window, input, save, cancel) =>
                    {
                        input.Text = "Unsaved edit"; store.Save("External edit", store.Load().Revision); Click(save);
                        Check(window.IsVisible && input.Text == "Unsaved edit", "Error lost editor");
                        Check(Walk(window).OfType<TextBlock>().Any(t => t.Text == L("Literary.Anchor.SaveError")), "Error is not visible");
                        Click(cancel);
                    });
                    Click(Walk(chat).OfType<Button>().Single(b => Equals(b.Content, L("Literary.Writer.Clear"))));
                    Check(history.Count == 0 && dialogs.Load().Messages.Count == 0, "Clear kept history");
                    Check(store.Load().Text == "External edit", "Clear erased anchor");
                    Console.WriteLine("PASS UI " + role + ": restore, block, save, cancel, error, clear.");
                }
                owner.Close(); app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    static IEnumerable<DependencyObject> Walk(DependencyObject element)
    {
        yield return element;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
