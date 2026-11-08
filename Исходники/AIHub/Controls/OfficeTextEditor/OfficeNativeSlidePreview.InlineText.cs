using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.OfficeEditor;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeNativeSlidePreview
{
    private readonly Canvas _inlineLayer = new() { ClipToBounds = true };
    private ScrollViewer? _inlineEditor;
    private OfficeSlideObject? _inlineObject;

    private void BeginInlineEdit()
    {
        if (_overlay.Selected is not { TextBlocks: {} read } item) return;
        var blocks = read();
        if (blocks.Count == 0) return;
        EndInlineEdit();
        var panel = new StackPanel();
        var editor = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = System.Windows.Media.Brushes.White, RenderTransformOrigin = new Point(.5,.5) };
        _inlineObject = item;
        _inlineEditor = editor;
        _inlineLayer.Children.Add(editor);
        PositionInlineEditor();
        var rect = _overlay.ObjectRect(item);
        var scale = rect.Width / item.Bounds.Width * (_extension == ".pptx" ? 12700 : 1);
        foreach (var block in blocks)
        {
            var field = new TextBox { Text = block.Text, AcceptsReturn = block.AllowLineBreaks,
                TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Black,
                Background = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0),
                FontSize = Math.Clamp((block.FontSize ?? 14)*scale, 8, 128),
                FontWeight = block.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = block.Italic ? FontStyles.Italic : FontStyles.Normal };
            AutomationProperties.SetAutomationId(field, "Office.Native.InlineText");
            field.TextChanged += (_, _) =>
            {
                if (_inlineEditor != editor) return;
                if (field.Text.ReplaceLineEndings("\n") == block.Text) return;
                try { block.SetText(field.Text); }
                catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
                { field.Text = block.Text; return; }
                StructureChanged?.Invoke();
                ObjectEdited();
            };
            panel.Children.Add(field);
        }
        editor.LostKeyboardFocus += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_inlineEditor == editor && !editor.IsKeyboardFocusWithin) EndInlineEdit();
        });
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape && !(e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) return;
            e.Handled = true; EndInlineEdit(); _overlay.Focus();
        };
        editor.UpdateLayout();
        var fieldAtPointer = panel.Children.OfType<TextBox>().FirstOrDefault(f => f.IsMouseOver);
        var active = fieldAtPointer ?? (TextBox)panel.Children[0];
        active.Focus();
        if (fieldAtPointer != null) active.CaretIndex = Math.Max(0, active.GetCharacterIndexFromPoint(Mouse.GetPosition(active), true));
        else active.SelectAll();
    }

    private void PositionInlineEditor()
    {
        if (_inlineEditor == null || _inlineObject == null) return;
        var rect = _overlay.ObjectRect(_inlineObject);
        if (rect.IsEmpty) return;
        Canvas.SetLeft(_inlineEditor, rect.X); Canvas.SetTop(_inlineEditor, rect.Y);
        _inlineEditor.Width = Math.Max(12,rect.Width); _inlineEditor.Height = Math.Max(24,rect.Height);
        _inlineEditor.RenderTransform = new RotateTransform(_inlineObject.Angle?.Invoke() ?? 0);
    }

    private void EndInlineEdit()
    {
        var editor = _inlineEditor;
        _inlineEditor = null; _inlineObject = null;
        if (editor != null) _inlineLayer.Children.Remove(editor);
    }
}
