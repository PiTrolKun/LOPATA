using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Panel = System.Windows.Controls.Panel;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace AIHub.Controls;

public sealed partial class RagBookWorkspaceControl
{
    private readonly System.Windows.Controls.ComboBox _searchLayer = new() { SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) };
    private bool LexiconSearch => _searchLayer.SelectedIndex == 1;
    private void AddLexiconAction(Panel tools)
    {
        var button = ActionButton(T("AddLexicon"), ChooseLexicon, "RagBooks.AddLexicon");
        _sourceActions.Add(button); tools.Children.Add(button);
    }
    private void BuildSearchLayer(Panel panel)
    {
        panel.Children.Add(Text(T("SearchLayer")));
        _searchLayer.Items.Add(new ComboBoxItem { Content = T("SearchBooks") });
        _searchLayer.Items.Add(new ComboBoxItem { Content = T("SearchLexicon") });
        AutomationProperties.SetAutomationId(_searchLayer, "RagBooks.SearchLayer");
        AutomationProperties.SetName(_searchLayer, T("SearchLayer"));
        _searchLayer.SelectionChanged += (_, _) => { _hits.Clear(); RefreshProcessingState(); };
        panel.Children.Add(_searchLayer);
    }
    private void ChooseLexicon()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true,
            Title = T("AddLexicon"), Filter = T("LexiconFilter") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            foreach (var path in dialog.FileNames) { using var passport = RagLexiconPackage.Read(path); }
            foreach (var path in dialog.FileNames)
                if (!_books.Contains(path, StringComparer.OrdinalIgnoreCase)) _books.Add(path);
            RefreshBooks(); SaveDraft();
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { _status.Text = T("InvalidLexicon"); }
    }
    private string LexiconPreview(string path)
    {
        using var doc = RagLexiconPackage.Read(path);
        return T("LexiconHint") + "\n\n" + string.Join("\n\n", doc.RootElement.GetProperty("sources").EnumerateArray().Select(s =>
            $"{s.GetProperty("id").GetString()} · {s.GetProperty("license").GetString()}\n{s.GetProperty("attribution").GetString()}\n{s.GetProperty("source_url").GetString()}\n{s.GetProperty("revision").GetString()}"));
    }
    private string LexiconSummary()
    {
        var (_, manifest) = RagLexiconReader.Open(_library!, CancellationToken.None, verifyDatabase: false);
        return string.Format(T("LexiconSummary"), manifest.GetProperty("records").GetInt64(),
            manifest.GetProperty("excluded").GetInt64(), manifest.GetProperty("sources").GetArrayLength()) + "\n" + T("LexiconHint");
    }
    private async Task FindLexiconAsync(CancellationToken token)
    {
        var query = _query.Text;
        var hits = await Task.Run(() => RagLexiconReader.Search(_library!, query, token), token);
        _hits.Text = hits.Count == 0 ? T("NoLexiconHits") : string.Join("\n\n", hits.Select(h =>
        {
            var lines = new List<string> { h.Word + " · " + T("LexiconKind." + h.Kind) };
            if (h.RequiresContext) lines.Add(T("RequiresContext"));
            foreach (var key in new[] { "variants", "phonemes", "ipa", "definitions", "translations", "synonyms" })
                if (h.Data.TryGetProperty(key, out var value) && value.GetArrayLength() > 0)
                    lines.Add(T("LexiconField." + key) + ": " + string.Join(key == "phonemes" ? " " : "; ", value.EnumerateArray().Select(v => v.GetString())));
            if (h.Data.TryGetProperty("senses", out var senses))
                lines.AddRange(senses.EnumerateArray().Select(s => string.Join("; ", s.GetProperty("glosses").EnumerateArray().Select(g => g.GetString()))));
            if (h.Data.TryGetProperty("forms", out var forms))
                lines.Add(T("LexiconField.forms") + ": " + string.Join("; ", forms.EnumerateArray().Select(f => f.GetProperty("form").GetString()).Distinct()));
            lines.Add($"{h.Source} · {h.License}\n{h.Attribution}\n{h.Locator}");
            return string.Join("\n", lines);
        }));
    }
}
