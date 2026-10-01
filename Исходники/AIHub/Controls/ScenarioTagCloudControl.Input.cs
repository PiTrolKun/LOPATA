using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace AIHub.Controls;

public sealed partial class ScenarioTagCloudControl
{
    private readonly CloudPointerGesture _gesture = new();
    private Point _previousPointer;

    private void InitializePointerInput()
    {
        _canvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(_canvas);
            _gesture.Begin(point.X, point.Y, HitTag(point)); _previousPointer = point;
            if (!_canvas.CaptureMouse()) _gesture.Cancel();
            SyncAnimation(); e.Handled = true;
        };
        _canvas.PreviewMouseMove += (_, e) =>
        {
            if (!_gesture.IsPressed) return;
            var point = e.GetPosition(_canvas); _gesture.Move(point.X, point.Y);
            if (_gesture.IsDrag)
            {
                _yaw += (point.X - _previousPointer.X) * .006;
                _pitch = Math.Clamp(_pitch + (point.Y - _previousPointer.Y) * .006, -Math.PI / 2, Math.PI / 2);
                RefreshPositions();
            }
            _previousPointer = point; e.Handled = true;
        };
        _canvas.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_gesture.IsPressed) return;
            var point = e.GetPosition(_canvas);
            var clicked = _gesture.End(point.X, point.Y, HitTag(point));
            _canvas.ReleaseMouseCapture(); SyncAnimation(); e.Handled = true;
            if (clicked is not null) RequestDescription(ScenarioNavigationCatalog.GetTag(clicked));
        };
        _canvas.LostMouseCapture += (_, _) => { _gesture.Cancel(); SyncAnimation(); };
    }

    private string? HitTag(Point point)
    {
        // Resolve the visual under the actual pointer, not the captured event source.
        DependencyObject? current = _canvas.InputHitTest(point) as DependencyObject;
        while (current is not null && current != _canvas)
        {
            if (current is Button { Tag: string id }) return id;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
