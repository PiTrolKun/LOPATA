using System.Windows;
using System.Windows.Input;
using Cursors = System.Windows.Input.Cursors;

namespace AIHub.Controls;

internal static class ModalDialogPointer
{
    internal static void Configure(Window dialog)
    {
        // Native browser focus may leave no current cursor when its owner is disabled.
        // Give this WPF window its own visible default and refresh on activation/render.
        // Child controls retain their normal hand/text cursors; no global override.
        dialog.Cursor = Cursors.Arrow;
        dialog.Activated += Refresh;
        dialog.ContentRendered += Refresh;
        static void Refresh(object? sender, EventArgs args)
        {
            Mouse.UpdateCursor();
            // Refresh may still hit the disabled native browser while WPF is entering
            // the modal loop. Set the current pointer shape for this transition too.
            Mouse.SetCursor(Cursors.Arrow);
        }
    }
}
