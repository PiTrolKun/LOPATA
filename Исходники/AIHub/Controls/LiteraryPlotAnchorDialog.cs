using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

internal static class LiteraryPlotAnchorDialog
{
    public static void Open(FrameworkElement owner, Func<string, string> l, LiteraryPlotAnchorStore store, LiteraryChatProfile role)
    {
        LiteraryPlotAnchor anchor;
        try { anchor = store.Load(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { System.Windows.MessageBox.Show(Window.GetWindow(owner), l("Literary.Anchor.LoadError"), l("Literary.Anchor.Title")); return; }
        var panel = new DockPanel();
        var hint = LiteraryUi.Text(l("Literary.Anchor.Hint")); DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var count = LiteraryUi.Text(""); bottom.Children.Add(count);
        var status = LiteraryUi.Text(""); bottom.Children.Add(status);
        var row = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right }; bottom.Children.Add(row);
        var input = LiteraryWorkspaceParts.TextArea(false); input.Text = anchor.Text; input.MinHeight = 120; panel.Children.Add(input);
        var window = LiteraryEditorDialogs.Create(owner, l("Literary.Anchor.Title") + " — " + l(role == LiteraryChatProfile.Writer ? "Literary.Workspace.Writer" : "Literary.Workspace.Advisor"), panel, 760);
        window.SizeToContent = SizeToContent.Manual; window.Height = Math.Min(570, window.MaxHeight); window.MinWidth = 420; window.MinHeight = 320;
        var save = LiteraryUi.Button(l("Literary.Anchor.Save"), () =>
        {
            try { store.Save(input.Text, anchor.Revision); window.DialogResult = true; }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
            { status.Text = l("Literary.Anchor.SaveError"); }
        }, true);
        row.Children.Add(save);
        var cancel = LiteraryUi.Button(l("Literary.Editor.Cancel"), () => window.DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        void Update() { count.Text = string.Format(l("Literary.Anchor.Count"), input.Text.Length, LiteraryPlotAnchorStore.MaxCharacters); save.IsEnabled = input.Text.Length <= LiteraryPlotAnchorStore.MaxCharacters; }
        input.TextChanged += (_, _) => Update(); Update();
        window.Loaded += (_, _) => input.Focus();
        try { window.ShowDialog(); } catch { window.Close(); throw; }
    }
}
