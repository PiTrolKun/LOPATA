using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private readonly SemaphoreSlim _spellWorkers = new(2,2);
    private readonly HashSet<string> _missingDictionaries = [];
    private void ScheduleSpelling(JsonElement message)
    {
        var key = message.GetProperty("key").GetString();
        var text = message.GetProperty("text").GetString();
        if (key is null || key.Length > 100 || text is null || text.Length > 65536 || _closed) return;
        var browser = _browser;
        var language = message.TryGetProperty("lang",out var lang) && lang.GetString()?.StartsWith("en", StringComparison.OrdinalIgnoreCase)==true
            ? "en-US" : "ru-RU";
        _ = GuardAsync(async () =>
        {
            var token = _lifetime.Token;
            await _spellWorkers.WaitAsync(token);
            try
            {
                OfficeTextSpelling.Result result;
                try { result = await Task.Run(() => OfficeTextSpelling.Check(text, language, token), token); }
                catch (COMException) { result = new([], [language]); }
                if (_closed || !IsTextSheet || !ReferenceEquals(_browser,browser)) return;
                _missingDictionaries.UnionWith(result.Missing);
                if (_missingDictionaries.Count != 0)
                    _status.Text = string.Format(T("SpellingUnavailable"),string.Join(", ",_missingDictionaries));
                browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { action = "spelling", key, issues = result.Issues }));
            }
            finally { _spellWorkers.Release(); }
        });
    }
}
