using System.Windows;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private OfficeNativeSlidePreview? _draftCanvas;
    private ListBox? _draftSlideList;

    private Grid WithDraftSlideCanvas(List<OfficeSheetSection> sections, ListBox list, DockPanel textFields)
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition());
        var canvas = new OfficeNativeSlidePreview(".pptx", () => sections.Count,
            token => OfficeDraftSlideCanvas.SnapshotAsync(sections, token),
            index => OfficeDraftSlideCanvas.Open(sections[index], SheetText("Title"), T("SlideBody")), _l);
        _draftSlideList = list;
        _draftCanvas = canvas;
        canvas.Edited += SheetEdited;
        panel.IsVisibleChanged += (_, _) => canvas.SetActive(panel.IsVisible);
        panel.Children.Add(canvas.CreateAuthoringBar());
        Grid.SetRow(canvas, 1); panel.Children.Add(canvas);
        Grid.SetRow(textFields, 2);
        panel.Children.Add(textFields);
        return panel;
    }

    private async Task CloseDraftCanvasAsync()
    {
        var canvas = _draftCanvas;
        _draftCanvas = null;
        _draftSlideList = null;
        if (canvas != null) await canvas.DisposeAsync();
    }
}
