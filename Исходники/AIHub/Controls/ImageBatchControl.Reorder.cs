using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;
using DragDropEffects = System.Windows.DragDropEffects;

namespace AIHub.Controls;

public sealed partial class ImageBatchControl
{
    private sealed record RowDrag(ImageBatchControl Owner, ImageBatchJob Job, ImageBatchItem Item);
    private void InitializeReordering()
    {
        _list.AllowDrop = true;
        _list.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(RowDrag))) return; // External files use the existing importer.
            e.Handled = true;
            var drag = e.Data.GetData(typeof(RowDrag)) as RowDrag;
            e.Effects = drag?.Owner == this && AcceptsInput && !drag.Job.Started ? DragDropEffects.Move : DragDropEffects.None;
            if (e.Effects == DragDropEffects.Move && FindScroll(_list) is { } scroll)
            {
                var y = e.GetPosition(_list).Y;
                if (y < 28) scroll.LineUp(); else if (y > _list.ActualHeight - 28) scroll.LineDown();
            }
        };
        _list.Drop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(typeof(RowDrag))) return;
            e.Handled = true;
            if (e.Data.GetData(typeof(RowDrag)) is not RowDrag drag || drag.Owner != this || !AcceptsInput || drag.Job.Started) return;
            var target = ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (target?.Tag == drag.Item) return;
            var items = drag.Job.Items;
            int at = target?.Tag is ImageBatchItem item ? items.IndexOf(item) : items.Count;
            if (target is not null && e.GetPosition(target).Y > target.ActualHeight / 2) at++;
            int from = items.IndexOf(drag.Item);
            if (from < 0) return;
            items.RemoveAt(from); if (at > from) at--;
            items.Insert(Math.Clamp(at, 0, items.Count), drag.Item);
            RenderItems(drag.Job);
            Action?.Invoke("reordered");
        };
    }
    private void AttachReordering(ListBoxItem entry, ImageBatchItem item, ImageBatchJob job)
    {
        Point start = default; bool armed = false;
        entry.PreviewMouseLeftButtonDown += (_, e) =>
        {
            armed = true;
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != entry; node = VisualTreeHelper.GetParent(node))
                if (node is System.Windows.Controls.Primitives.ButtonBase) { armed = false; break; }
            start = e.GetPosition(entry);
        };
        entry.MouseMove += (_, e) =>
        {
            if (!armed || !AcceptsInput || job.Started || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(entry);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            armed = false;
            System.Windows.DragDrop.DoDragDrop(entry, new System.Windows.DataObject(typeof(RowDrag), new RowDrag(this, job, item)), DragDropEffects.Move);
        };
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
