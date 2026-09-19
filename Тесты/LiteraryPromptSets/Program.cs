using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

internal static partial class Program
{
    private static int _checks;
    private static string _output = "";
    private static Application _app = null!;
    [STAThread] private static void Main()
    {
        _output = Path.GetFullPath("Тесты/LiteraryPromptSets/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(_output);
        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = XDocument.Load("Исходники/AIHub/MainWindow.xaml");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = new XElement(ns + "ResourceDictionary", xml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration));
        foreach (var element in xml.Root.Element(ns + "Window.Resources")!.Elements())
            if (element.Name.LocalName is "Double" or "Thickness" or "SolidColorBrush" ||
                new[] { "PrimaryButtonStyle", "SecondaryButtonStyle" }.Contains((string?)element.Attribute(x + "Key"))) dictionary.Add(new XElement(element));
        foreach (var dark in new[] { true, false }) foreach (var lang in new[] { "ru", "en" })
        {
            var resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
            foreach (var pair in new[] { ("WindowBackgroundBrush", dark ? "#101827" : "#F5F6F9"),
                ("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8"), ("PanelBrush", dark ? "#172033" : "#FFFFFF"),
                ("TextPrimaryBrush", dark ? "#F8FAFC" : "#1F1F1F"), ("TextSecondaryBrush", dark ? "#AAB4C4" : "#5D6470"),
                ("LineBrush", dark ? "#2D374B" : "#DADDE3") }) resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            var owner = new Window { Resources = resources, ShowInTaskbar = false, Width = 300, Height = 200 }; owner.Show();
            var translations = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText($"Исходники/AIHub/Localization/{lang}.json"))!;
            string L(string key) => translations.TryGetValue(key, out var value) ? value : throw new Exception("Missing translation: " + key);
            var prefix = lang + "-" + dark;
            Editor(owner, L, prefix);
            Selection(owner, L, prefix);
            Manager(owner, L, prefix);
            owner.Close();
        }
        var summary = $"PASS: {_checks} checks, RU/EN and dark/light; editor, selector, manager, project snapshot and failed save. Synthetic data only; no model calls.";
        File.WriteAllText(Path.Combine(_output,"result.txt"), summary); Console.WriteLine(summary + "\n" + _output); _app.Shutdown();
    }

    private static void Editor(Window owner, Func<string,string> l, string prefix)
    {
        var original = LiteraryPromptSets.Defaults("Test set"); LiteraryPromptSet? saved = null;
        var legacy = new PromptPairPreset { Name = "Legacy", ContractId = LiteraryPromptSets.LegacyPrefix + "Discuss", AnalysisPrompt = "OLD ROLE", ComposePrompt = "OLD ACTION" };
        var window = new LiteraryPromptSetEditorWindow(owner, original, [], l, p => { saved = p; return null; }, [legacy]);
        Inspect(() =>
        {
            Check(All<Expander>(window).Count() == 14, "two roles and twelve actions");
            Check(All<Expander>(window).All(e => !e.IsExpanded), "both levels initially collapsed");
            Capture(window, prefix + "-collapsed");
            foreach (var action in LiteraryPromptSets.Actions)
            {
                Check(Get<TextBox>(window,action.Id + ".Role").Text == original.Actions[action.Id].Role, "role copied for " + action.Id);
                Check(Get<TextBox>(window,action.Id + ".Action").Text == original.Actions[action.Id].Action, "action copied for " + action.Id);
            }
            Get<Expander>(window,"Role.Advisor").IsExpanded = true;
            Get<Expander>(window,"Action.Discuss").IsExpanded = true; Pump();
            Check(!Get<Expander>(window,"Role.Writer").IsExpanded && !Get<Expander>(window,"Action.Options").IsExpanded, "independent expanders");
            var area = Get<TextBox>(window,"Discuss.Action"); var before = area.Text;
            area.SelectAll(); area.SelectedText = "CUSTOM ACTION search target";
            Click(window,"Discuss.Action.Undo"); Check(area.Text == before, "undo edit");
            Click(window,"Discuss.Action.Redo"); Check(area.Text.StartsWith("CUSTOM ACTION"), "redo edit");
            Get<TextBox>(window,"Discuss.Action.Search").Text = "target"; Click(window,"Discuss.Action.Find");
            Check(area.SelectedText == "target", "find text in field");
            var wrap = Get<CheckBox>(window,"Discuss.Action.Wrap"); wrap.IsChecked = false; wrap.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Check(area.TextWrapping == TextWrapping.NoWrap, "toggle wrapping");
            ConfirmNext(true); Click(window,"Discuss.Action.Restore"); Check(area.Text == before, "restore affects only custom field");
            ConfirmNext(true); Click(window,"ImportLegacy");
            Check(Get<TextBox>(window,"Discuss.Role").Text == "OLD ROLE" && area.Text == "OLD ACTION", "old action imported into set");
            Check(Get<TextBox>(window,"Continue.Action").Text == original.Actions["Continue"].Action, "other actions unchanged by import");
            Capture(window,prefix + "-expanded");
            Get<TextBox>(window,"SetName").Text = ""; Click(window,"SaveSet"); Check(saved is null && window.IsVisible, "missing name rejected");
            Get<TextBox>(window,"SetName").Text = "Saved set";
            var other = Get<TextBox>(window,"Tone.Role"); var otherBefore = other.Text; other.Text = "";
            Click(window,"SaveSet");
            Check(Get<Expander>(window,"Role.Writer").IsExpanded && Get<Expander>(window,"Action.Tone").IsExpanded, "missing field expanded for correction");
            other.Text = otherBefore;
            ConfirmNext(false); window.Close(); Check(window.IsVisible, "cancel closing preserves unsaved work");
            Click(window,"SaveSet");
        });
        Check(window.ShowDialog() == true && saved?.Name == "Saved set", "full set saved");
        Check(saved!.Actions["Discuss"].Action == "OLD ACTION" && original.Actions["Discuss"].Action != "OLD ACTION", "defaults untouched");
        var reopen = new LiteraryPromptSetEditorWindow(owner, saved, [saved], l, _ => null);
        Inspect(() => { Check(All<Expander>(reopen).All(e => !e.IsExpanded), "reopen resets collapse state"); Click(reopen,"CancelSet"); }); reopen.ShowDialog();
    }

    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static IEnumerable<T> All<T>(DependencyObject value)
    {
        if (value is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>()) foreach (var found in All<T>(child)) yield return found;
    }
    private static T Get<T>(Window window,string id) where T : FrameworkElement => All<T>(window).Single(e => AutomationProperties.GetAutomationId(e) == id);
    private static void Click(Window window,string id) { Get<Button>(window,id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Inspect(Action action) => _app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(() =>
    { try { action(); } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); } }));
    private static void Pump()
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) }; timer.Tick += (_,_) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Capture(FrameworkElement element,string name)
    { var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(_output,name + ".png")); png.Save(file); }
}
