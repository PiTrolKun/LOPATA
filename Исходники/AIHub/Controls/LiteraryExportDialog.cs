using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

internal static class LiteraryExportDialog
{
    private sealed record Format(string Label, string Extension, Action<IReadOnlyList<LiteraryExportChapter>, string> Write);
    private static readonly Format[] Formats = [new("DOCX (.docx)", "docx", LiteraryDocxExporter.Export)];

    public static async Task ShowAsync(FrameworkElement owner, Func<string, string> l, string directory, string title, LiteraryDraftControl? draft = null)
    {
        var panel = new StackPanel(); panel.Children.Add(LiteraryUi.Text(l("Literary.Format")));
        var combo = new ComboBox { ItemsSource = Formats, DisplayMemberPath = nameof(Format.Label), SelectedIndex = 0, MinHeight = 32 };
        panel.Children.Add(combo);
        var dialog = LiteraryEditorDialogs.Create(owner, l("Literary.Export"), panel, 400);
        var choose = LiteraryUi.Button(l("Literary.Editor.Next"), () => dialog.DialogResult = true, true);
        choose.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(choose);
        if (dialog.ShowDialog() != true) return;
        var format = (Format)combo.SelectedItem;
        var save = new Microsoft.Win32.SaveFileDialog { FileName = LiteraryChapterFiles.SafeTitle(title) + "." + format.Extension,
            DefaultExt = "." + format.Extension, Filter = format.Label + "|*." + format.Extension, OverwritePrompt = true };
        if (save.ShowDialog(Window.GetWindow(owner)) != true) return;
        // Do not let export overwrite any source in this project (including its index/backups).
        if (Path.GetFullPath(save.FileName).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(save.FileName), "." + format.Extension, StringComparison.OrdinalIgnoreCase))
        { System.Windows.MessageBox.Show(Window.GetWindow(owner), l("Literary.Editor.ExportError"), l("Literary.Export")); return; }
        if (draft is not null && !await draft.SaveAsync())
        { System.Windows.MessageBox.Show(Window.GetWindow(owner), l("Literary.Draft.SaveError"), l("Literary.Export")); return; }
        owner.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                var store = draft?.Store ?? new LiteraryChapterStore(directory);
                if (draft is null) store.Open();
                format.Write(store.Snapshot(), save.FileName);
            });
            System.Windows.MessageBox.Show(Window.GetWindow(owner), l("Literary.Editor.Exported") + "\n" + save.FileName, l("Literary.Export"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Text.DecoderFallbackException or InvalidOperationException or System.Xml.XmlException or ArgumentException)
        { System.Windows.MessageBox.Show(Window.GetWindow(owner), l(ex.Message == "Literary.Editor.ExportEmpty" ? ex.Message : "Literary.Editor.ExportError") + "\n" + ex.Message, l("Literary.Export")); }
        finally { owner.IsEnabled = true; }
    }
}
