using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.NeuroConstructor;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

public partial class NeuroConstructorControl
{
    private string? _image, _firstAudio, _secondAudio;
    private void ClearDraft()
    {
        _image = _firstAudio = _secondAudio = null;
        Prompt.Clear();
        ImageName.Text = FirstAudioName.Text = SecondAudioName.Text = MarkHint.Text = "";
        Picture.Source = null;
        Preview.Visibility = Quadrants.Visibility = Visibility.Collapsed;
    }
    private void ChooseImage(object sender, RoutedEventArgs e) => Try(() =>
    {
        var path = SelectFile("ImageFilter", "ChooseImage");
        if (path is not null) SetImage(path);
    });
    private void SetImage(string path)
    {
        var bitmap = new BitmapImage();
        using (var file = File.OpenRead(path))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.StreamSource = file;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        _image = path;
        ImageName.Text = Path.GetFileName(path);
        Picture.Source = bitmap;
        Preview.Visibility = Visibility.Visible;
        Quadrants.Visibility = Visibility.Collapsed;
        MarkHint.Text = "";
    }
    private void FirstWavClicked(object sender, RoutedEventArgs e) => AudioFile(first: true);
    private void SecondWavClicked(object sender, RoutedEventArgs e) => AudioFile(first: false);
    private void AudioFile(bool first) => Try(() =>
    {
        var path = SelectFile("WavFilter", first ? "FirstWav" : "SecondWav");
        if (path is null) return;
        if (first) { _firstAudio = path; FirstAudioName.Text = Path.GetFileName(path); }
        else { _secondAudio = path; SecondAudioName.Text = Path.GetFileName(path); }
    });
    private void ClearAudio(object sender, RoutedEventArgs e)
    {
        _firstAudio = _secondAudio = null;
        FirstAudioName.Text = SecondAudioName.Text = "";
    }
    private string[] AudioInput()
    {
        if (_project?.Composition is not ("trio" or "medium" or "heavy") || _firstAudio is null && _secondAudio is null) return [];
        if (_firstAudio is null || _secondAudio is null) throw new InvalidDataException(T("NeedBothWav"));
        return [_firstAudio, _secondAudio];
    }
    private (string Image, string Text, string[] Audio) Input()
    {
        if (_image is null) throw new InvalidDataException(T("NeedImage"));
        if (string.IsNullOrWhiteSpace(Prompt.Text)) throw new InvalidDataException(T("NeedPrompt"));
        return (_image, Prompt.Text.Trim(), AudioInput());
    }
    private void BeginMarking(object sender, RoutedEventArgs e) => Try(() =>
    {
        _ = Input();
        MarkHint.Text = T("ClickCell");
        Quadrants.Children.Clear();
        for (int slot = 0; slot < 4; slot++)
        {
            var position = slot;
            var button = new Button
            {
                Background = new SolidColorBrush(Color.FromArgb(14, 86, 153, 255)),
                BorderThickness = new Thickness(1),
                ToolTip = T("Slot" + slot),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.SetResourceReference(BorderBrushProperty, "AccentBrush");
            AutomationProperties.SetName(button, T("Slot" + slot));
            AutomationProperties.SetAutomationId(button, "Neuro.Slot" + slot);
            button.Click += async (_, _) => await StoreExample(position);
            Quadrants.Children.Add(button);
        }
        Quadrants.Visibility = Visibility.Visible;
        Notice.Text = "";
    });
    private Task StoreExample(int slot) => Perform(async () =>
    {
        if (_project is not { } p) return;
        var (image, text, audio) = Input();
        var split = Selected(Split);
        await Task.Run(() => NeuroService.AddExample(p, image, text, slot, audio, split));
        if (!ReferenceEquals(p, _project)) return;
        _reportStamp = null;
        Quadrants.Visibility = Visibility.Collapsed;
        MarkHint.Text = T("ExampleSaved", T("Slot" + slot), T("Option." + split));
        RefreshExamples();
    });
    private void RefreshExamples()
    {
        if (_project is not { } p) return;
        SavedExamples.ItemsSource = p.Examples.Select((example, i) => new Choice(i.ToString(),
            T("ExampleOption", i+1, example.Text,
                T("Slot" + example.Slot), T("Option." + example.Split)))).ToArray();
        SavedExamples.SelectedIndex = -1;
    }
    private void ReadExample(object sender, SelectionChangedEventArgs e) => Try(() =>
    {
        if (_filling || _project is not { } p || SavedExamples.SelectedIndex < 0) return;
        var example = p.Examples[SavedExamples.SelectedIndex];
        SetImage(example.Image);
        Prompt.Text = example.Text;
        _firstAudio = example.Audio?.ElementAtOrDefault(0);
        _secondAudio = example.Audio?.ElementAtOrDefault(1);
        FirstAudioName.Text = Path.GetFileName(_firstAudio);
        SecondAudioName.Text = Path.GetFileName(_secondAudio);
        Options(Split, example.Split, "train", "dev", "test");
        MarkHint.Text = T("ExampleSaved", T("Slot" + example.Slot), T("Option." + example.Split));
    });
    private async void PredictClicked(object sender, RoutedEventArgs e) => await Perform(async () =>
    {
        var (image, text, audio) = Input();
        var path = SelectFile("ModelFilter", "Predict");
        if (path is null) return;
        var output = await NeuroService.PredictAsync(path, image, text, audio, InferenceDevice,projectId:_project?.Id);
        using var doc = JsonDocument.Parse(output);
        var slot = doc.RootElement.GetProperty("slot").GetInt32();
        MarkHint.Text = T("Prediction", T("Slot" + slot));
        Notice.Text = "";
    }, "Checking");
}
