using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;

Exception? failure = null;
var ui = new Thread(() =>
{
    try
    {
        var app = new Application();
        var panel = new StackPanel { Width = 900 };
        void Brush(string key, string color) => panel.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Brush("SecondaryButtonBackgroundBrush", "#111827"); Brush("TextPrimaryBrush", "#F8FAFC");
        Brush("ChatUserTextBrush", "#93C5FD"); Brush("LineBrush", "#2D374B");
        panel.Resources["UiBodyFontSize"] = 16d;
        foreach (var role in new[] { "Писатель", "Советник" })
        {
            var box = new LiteraryTranscript { Height = 260, Margin = new Thickness(8) };
            panel.Children.Add(box);
            box.BeginReply([(true, "Продолжи сцену на вокзале.\nСохрани атмосферу загадки."), (false, "Антон взглянул на часы: стрелки замерли.")], "Вы", role);
            using var stream = new LiteraryStreamDisplay(box);
            stream.Report(new ModelStreamChunk("Частичный ответ"));
            stream.Reset(() => box.BeginReply([(true, "Продолжи сцену на вокзале.\nСохрани атмосферу загадки."), (false, "Частичный ответ [Незавершённый ответ]")], "Вы", role));
            var answer = "Лера прислушалась. За стеной кто-то осторожно повернул ключ.\n— Ты тоже это слышал?";
            foreach (var c in answer) stream.Report(new ModelStreamChunk(c.ToString()));
            Pump(stream.CompleteAsync());
            stream.Report(new ModelStreamChunk("LATE"));
            var plain = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
            Check(plain.Replace("\r\n", "\n").Contains(answer) && !plain.Contains("LATE"), "Stream text changed");
            Check(stream.Snapshot() == answer, "Retry buffer mixed");
            box.SelectAll(); Check(box.Selection.Text == plain, "Whole transcript is not selectable");
            var answerRun = ((Paragraph)box.Document.Blocks.LastBlock).Inlines.OfType<Run>().Last();
            box.Selection.Select(answerRun.ContentStart, answerRun.ContentStart.GetPositionAtOffset(4)!);
            var selected = box.Selection.Text;
            box.AppendResponse(" Продолжение.");
            Check(box.Selection.Text == selected, "Streaming disturbed selection");
            box.Selection.Select(box.Document.ContentStart, box.Document.ContentStart);
        }
        foreach (var dark in new[] { true, false })
        {
            var user = dark ? "#93C5FD" : "#1D4ED8";
            var model = dark ? "#F8FAFC" : "#1F1F1F";
            Brush("ChatUserTextBrush", user); Brush("TextPrimaryBrush", model);
            Brush("SecondaryButtonBackgroundBrush", dark ? "#111827" : "#F8F8F8");
            panel.Measure(new Size(900, 600)); panel.Arrange(new Rect(0, 0, 900, 600)); panel.UpdateLayout();
            foreach (LiteraryTranscript box in panel.Children)
            {
                var blocks = box.Document.Blocks.Cast<Paragraph>().ToArray();
                Check(blocks.Length == 3, "Streaming created unthemed paragraphs");
                Check(blocks[0].Foreground.ToString() == "#FF" + user[1..], "User theme color");
                Check(blocks[^1].Foreground.ToString() == "#FF" + model[1..], "Model theme color");
                Check(blocks[^1].Inlines.OfType<Run>().All(r => r.Foreground.ToString() == "#FF" + model[1..]), "Stream inherits incorrect color");
            }
            var image = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); image.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine("Тесты/LiteraryChatColors", dark ? "dark.png" : "light.png")); encoder.Save(file);
        }
        foreach (LiteraryTranscript box in panel.Children) { box.Clear(); Check(box.Document.Blocks.Count == 0, "Clear"); }
        var stress = (LiteraryTranscript)panel.Children[0];
        stress.BeginReply([], "Вы", "Писатель");
        using (var stream = new LiteraryStreamDisplay(stress))
        {
            var inputServed = false;
            for (int i = 0; i < 10000; i++) stream.Report(new ModelStreamChunk("тест "));
            stress.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => inputServed = true));
            Pump(stream.CompleteAsync());
            Check(inputServed && stream.Snapshot().Length == 50000, "UI priority or large stream failed");
        }
        app.Shutdown(); Console.WriteLine("PASS: both chats, stream/retry, selection, live dark/light colors, clear.");
    }
    catch (Exception e) { failure = e; }
});
ui.SetApartmentState(ApartmentState.STA); ui.Start(); ui.Join();
if (failure is not null) throw failure;
static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
static void Pump(Task task)
{
    while (!task.IsCompleted)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    task.GetAwaiter().GetResult();
}
