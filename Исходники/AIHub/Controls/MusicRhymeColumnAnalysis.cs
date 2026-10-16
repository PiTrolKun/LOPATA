using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>Only the cheap lexical analyzer follows typing. It never launches a model.</summary>
internal sealed class MusicRhymeColumnAnalysis : IDisposable
{
    private readonly TextBox _editor;
    private readonly MusicRhymeAdorner _adorner;
    private readonly MusicRhymeController _owner;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private CancellationTokenSource? _cancel;
    private MusicRhymeAnalysis? _analysis;
    private bool _disposed;
    public MusicRhymeColumnAnalysis(TextBox editor, AdornerLayer layer, MusicRhymeController owner)
    {
        _editor = editor; _owner = owner; _adorner = new(editor); layer.Add(_adorner);
        editor.TextChanged += Edited; _timer.Tick += async (_, _) => { _timer.Stop(); await AnalyzeAsync(); };
    }
    private void Edited(object sender, TextChangedEventArgs e)
    { _cancel?.Cancel(); _analysis = null; _timer.Stop(); _timer.Interval = TimeSpan.FromMilliseconds(_owner.Options.Tuning.Integer("history.reanalysis_delay")); if (_owner.AutoAnalyze) _timer.Start(); }
    public void Refresh(bool force)
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_owner.Options.Tuning.Integer("history.reanalysis_delay"));
        _adorner.Set(_analysis, _owner.Color);
        if (!_owner.AutoAnalyze && !force) { _timer.Stop(); _cancel?.Cancel(); }
        if (force) { _timer.Stop(); _timer.Start(); }
    }
    private async Task AnalyzeAsync()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_editor.Text) || !File.Exists(Path.Combine(MusicRhymeCorpus.DirectoryPath, "corpus.json"))) return;
        using var cancel = new CancellationTokenSource(); _cancel = cancel; var text = _editor.Text;
        var tuning = _owner.Options.Tuning;
        try
        {
            var analysis = await Task.Run(() => {
                using var lexicon = new MusicRhymeLexicon(MusicRhymeCorpus.DirectoryPath, cancel.Token, tuning);
                return MusicRhymeAnalyzer.Analyze(text, lexicon.Lookup, cancel.Token, tuning: tuning);
            }, cancel.Token);
            if (!_disposed && !cancel.IsCancellationRequested && text == _editor.Text)
            { _analysis = analysis; _adorner.Set(_analysis, _owner.Color); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed) _editor.ToolTip = _owner.Error(error.Message); }
        finally { if (_cancel == cancel) _cancel = null; }
    }
    public void Set(MusicRhymeAnalysis analysis, string source)
    { if (_editor.Text != source) return; _analysis = analysis; _adorner.Set(analysis, _owner.Color); }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _timer.Stop(); _cancel?.Cancel(); _editor.TextChanged -= Edited; _adorner.Dispose(); }
}
