using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeNativeSlidePreview
{
    internal event Action? StructureChanged;
    private OfficeSlideCanvasModel? _editableCanvas;

    internal WrapPanel CreateAuthoringBar()
    {
        var bar = new WrapPanel();
        void Command(string key, Action action, Func<bool> available)
        {
            var button = new Button { Content = _localize("OfficeEditor." + key), Margin = new Thickness(2), IsEnabled = false };
            AutomationProperties.SetAutomationId(button, "Office.Native." + key);
            button.Click += (_, _) => action();
            SelectionChanged += () =>
            {
                button.IsEnabled = _active && !_disposed && _image.Source != null && available();
            };
            bar.Children.Add(button);
        }
        Command("InsertSlideText", InsertText, () => _editableCanvas?.Insert != null);
        Command("InsertSlidePicture", InsertPicture, () => _editableCanvas?.Insert != null && !_choosingPicture);
        Command("EditSlideText", BeginInlineEdit, () => _overlay.Selected?.TextBlocks != null);
        Command("BringToFront", () => OrderSelected(true), () => _overlay.Selected?.ChangeOrder != null);
        Command("SendToBack", () => OrderSelected(false), () => _overlay.Selected?.ChangeOrder != null);
        Command("RotateLeft", () => RotateSelected(-90), () => _overlay.Selected?.Rotate != null);
        Command("RotateRight", () => RotateSelected(90), () => _overlay.Selected?.Rotate != null);
        Command("RotationAngle", ChooseAngle, () => _overlay.Selected?.Rotate != null);
        Command("DeleteObject", DeleteSelected, () => _overlay.Selected?.Delete != null);
        return bar;
    }

    private void InsertText()
    {
        var text = Prompt("InsertSlideText", "", true, value =>
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try { System.Xml.XmlConvert.VerifyXmlChars(value); return true; }
            catch (System.Xml.XmlException) { return false; }
        });
        if (text == null || _disposed) return;
        _editableCanvas?.Insert?.Invoke(text, null);
        RebindObjects(null, last: true);
    }

    private string? Prompt(string key, string initial, bool multiline, Func<string,bool> valid)
    {
        var dialog = new Window { Owner = Window.GetWindow(this), Title = _localize("OfficeEditor." + key), Width = 430, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(12) };
        var field = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            Height = multiline ? 100 : 32, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var ok = new Button { Content = "OK", IsDefault = !multiline, IsEnabled = valid(initial), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MinWidth = 70, Margin = new Thickness(4) };
        field.TextChanged += (_, _) => ok.IsEnabled = valid(field.Text);
        ok.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(field); panel.Children.Add(ok); dialog.Content = panel;
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) dialog.DialogResult = false; };
        return dialog.ShowDialog() == true ? field.Text : null;
    }

    private async void InsertPicture()
    {
        var canvas = _editableCanvas;
        var owner = Window.GetWindow(this);
        if (canvas?.Insert == null || owner == null || _choosingPicture) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = _localize("OfficeEditor.InsertSlidePicture"),
            Filter = _localize("OfficeEditor.SlideImageFilter") };
        _choosingPicture = true; SelectionChanged?.Invoke();
        try
        {
            if (picker.ShowDialog(owner) != true) return;
            var image = await Task.Run(() => OfficeSlidePicture.Load(picker.FileName));
            if (_disposed || !_active || !ReferenceEquals(canvas, _editableCanvas)) return;
            canvas.Insert(Path.GetFileName(picker.FileName), image);
            RebindObjects(null, true);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { if (!_disposed) System.Windows.MessageBox.Show(owner, _localize("OfficeEditor.SlideInvalidPicture"), picker.Title); }
        finally { _choosingPicture = false; SelectionChanged?.Invoke(); }
    }

    private void ChooseAngle()
    {
        var old = _overlay.Selected?.Angle?.Invoke() ?? 0;
        double angle = 0;
        var input = Prompt("RotationAngle", old.ToString("0.###"), false, value =>
            double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out angle) && double.IsFinite(angle));
        if (input != null) RotateSelected(angle % 360 - old);
    }

    private void RotateSelected(double direction)
    {
        if (_overlay.Selected?.Rotate is not {} rotate) return;
        rotate(direction);
        _overlay.InvalidateVisual();
        ObjectEdited();
    }

    private void DeleteSelected()
    {
        if (_overlay.Selected?.Delete is not {} delete) return;
        delete();
        RebindObjects(null, false);
    }

    private void OrderSelected(bool front)
    {
        if (_overlay.Selected is not { ChangeOrder: {} order } selected) return;
        order(front);
        RebindObjects(selected.Identity, false);
    }

    private void RebindObjects(object? identity, bool last)
    {
        StructureChanged?.Invoke();
        _editableCanvas = _canvas(_page);
        _overlay.SetSlide(_editableCanvas);
        _overlay.Select(last ? _editableCanvas?.Objects.LastOrDefault() : _editableCanvas?.Objects.FirstOrDefault(o => Equals(o.Identity, identity)));
        SelectionChanged?.Invoke();
        ObjectEdited();
    }
}
