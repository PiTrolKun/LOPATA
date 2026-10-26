using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;

namespace AIHub.Controls.VideoEditor;

internal static class VideoEditorCommands
{
    private static readonly Dictionary<string, string> Icons = new() {
        ["CreateBackgroundMusic"] = "M8,17 V5 L20,3 V15 M8,5 V9 L20,7 M8,17 C3,14 1,20 5,21 C8,22 9,19 8,17 M20,15 C15,12 13,18 17,19 C20,20 21,17 20,15",
        ["Undo"] = "M10,5 L4,10 10,15 M4,10 H15 C22,10 22,20 16,20",
        ["Redo"] = "M14,5 L20,10 14,15 M20,10 H9 C2,10 2,20 8,20",
        ["Split"] = "M7,16 A3,3 0 1 0 7,22 A3,3 0 1 0 7,16 M17,16 A3,3 0 1 0 17,22 A3,3 0 1 0 17,16 M8,17 L19,2 M16,17 L5,2",
        ["Delete"] = "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
        ["Copy"] = "M8,7 H21 V21 H8 Z M4,17 H3 V3 H17 V4",
        ["Paste"] = "M8,5 H4 V22 H20 V5 H16 M8,3 H16 V8 H8 Z M8,12 H16 M8,16 H16",
        ["Duplicate"] = "M8,7 H21 V21 H8 Z M4,17 H3 V3 H17 V4 M11,14 H18 M14.5,10.5 V17.5",
        ["Unlink"] = "M10,8 L7,5 C3,1 -2,7 3,11 L6,14 M14,16 L17,19 C21,23 26,17 21,13 L18,10 M3,21 L21,3",
        ["CloseGaps"] = "M2,12 H9 M5,8 L9,12 5,16 M22,12 H15 M19,8 L15,12 19,16 M12,3 V21",
        ["Freeze"] = "M4,4 H20 V20 H4 Z M9,8 V16 M15,8 V16",
        ["TitleClip"] = "M3,4 H21 M12,4 V21 M7,21 H17",
        ["AddVideo"] = "M2,5 H16 V19 H2 Z M16,10 L22,7 V17 L16,14 M5,12 H13 M9,8 V16",
        ["AddAudio"] = "M10,18 V4 L20,2 V16 M10,6 L20,4 M10,18 C10,23 2,23 2,19 C2,16 10,15 10,18 M20,16 C20,21 12,21 12,17 C12,14 20,13 20,16",
        ["Beginning"] = "M4,4 V20 M20,4 L8,12 20,20 Z",
        ["PreviousFrame"] = "M4,4 V20 M19,5 L8,12 19,19 Z",
        ["NextFrame"] = "M20,4 V20 M5,5 L16,12 5,19 Z",
        ["Play"] = "M6,3 L21,12 6,21 Z",
        ["PauseGlyph"] = "M7,3 V21 M17,3 V21",
        ["FrameFill"] = "M3,3 H21 V21 H3 Z M7,10 V7 H10 M14,7 H17 V10 M17,14 V17 H14 M10,17 H7 V14",
        ["FrameFit"] = "M3,6 H21 V18 H3 Z M2,2 L7,7 M5,7 H7 V5 M22,2 L17,7 M17,5 V7 H19 M2,22 L7,17 M5,17 H7 V19 M22,22 L17,17 M17,19 V17 H19",
        ["Detect"] = "M3,4 H21 V20 H3 Z M8,4 V20 M16,4 V20 M1,12 H23",
        ["AddFragment"] = "M4,2 H16 L21,7 V22 H4 Z M16,2 V7 H21 M8,14 H17 M12.5,10 V18",
        ["SplitFragment"] = "M4,2 H20 V22 H4 Z M2,12 H22 M8,6 H16 M8,18 H16",
        ["MergeFragment"] = "M3,3 H10 V21 H3 Z M14,3 H21 V21 H14 Z M7,12 H17 M10,9 L13,12 10,15",
        ["AnalyzeSelected"] = "M4,3 H17 L21,7 V21 H4 Z M8,8 H13 M8,12 H17 M8,16 H17",
        ["AnalyzeAll"] = "M7,6 H21 V22 H7 Z M3,18 V2 H17 M10,10 H18 M10,14 H18 M10,18 H18",
        ["VoiceSettings"] = "M9,2 A4,4 0 1 0 9,10 A4,4 0 1 0 9,2 M2,22 V18 C2,11 16,11 16,18 V22 M19,6 C23,9 23,13 19,16",
        ["SpeakSelected"] = "M3,9 H7 L12,4 V20 L7,15 H3 Z M16,8 C20,10 20,14 16,16 M20,3 H23 V6",
        ["SpeakFragments"] = "M2,9 H6 L11,4 V20 L6,15 H2 Z M15,5 H22 M15,12 H22 M15,19 H22",
        ["SpeakWhole"] = "M2,9 H6 L11,4 V20 L6,15 H2 Z M15,8 C19,10 19,14 15,16 M14,22 H23",
        ["Zoom"] = "M10,2 A8,8 0 1 0 10,18 A8,8 0 1 0 10,2 M16,16 L23,23",
    };
    internal static FrameworkElement? Icon(string key, Button owner)
    {
        if (!Icons.TryGetValue(key, out var data)) return null;
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground") { Source = owner });
        return new Viewbox { Width = 19, Height = 19, Child = path, Stretch = Stretch.Uniform };
    }
    internal static string Tip(string key, Func<string, string> text)
    {
        var tip = text("Tip." + key); return tip.EndsWith("Tip." + key, StringComparison.Ordinal) ? text(key) : tip;
    }
}
