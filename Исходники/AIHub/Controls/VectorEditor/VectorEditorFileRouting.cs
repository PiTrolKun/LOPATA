using System.Windows;

namespace AIHub.Controls.VectorEditor;

internal static class VectorEditorFileRouting
{
    internal static async Task<bool> TryAddSheetAsync(IReadOnlyList<VectorEditorWindow> windows,
        VectorEditorWindow? recent, string svg, string name)
    {
        var target = windows.FirstOrDefault(w => w.IsActive)
            ?? (recent is not null && windows.Contains(recent) ? recent : windows.LastOrDefault());
        if (target is null) return false;
        await target.AddSheetAsync(svg, name);
        Show(target);
        return true;
    }

    internal static void Show(VectorEditorWindow window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
