using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AIHub.Services.Dictation;

namespace AIHub.Controls;

internal sealed partial class LetItOutComposerBrowser : IDictationTargetProvider
{
    private readonly Func<string> _scope;
    private int _dictationEpoch;
    private string _documentScope = "";
    private string DictationScope => _scope() + ":" + _dictationEpoch;
    bool IDictationTargetProvider.CanDictate => IsReady && IsVisible && IsEnabled && !_buffer.IsReadOnly;
    bool IDictationTargetProvider.EditorFocused => IsKeyboardFocusWithin;
    IDictationTextTarget IDictationTargetProvider.CreateDictationTarget() => new ComposerTarget(this);

    private string SetScript(string text)
    {
        _documentScope = DictationScope;
        return "composer.set(" + JsonSerializer.Serialize(text) + "," + JsonSerializer.Serialize(_documentScope) + ")";
    }

    private sealed class ComposerTarget : IAsyncDictationTextTarget
    {
        private readonly LetItOutComposerBrowser _editor;
        private readonly string _scope;
        private string _recording = Guid.NewGuid().ToString("N");
        internal ComposerTarget(LetItOutComposerBrowser editor)
        {
            _editor = editor; _scope = editor.DictationScope;
            WindowHandle = new WindowInteropHelper(Window.GetWindow(editor)).Handle;
        }
        public nint WindowHandle { get; }
        public bool Available => ((IDictationTargetProvider)_editor).CanDictate && _scope == _editor.DictationScope;
        public void Reset() => _recording = Guid.NewGuid().ToString("N");
        public void Apply(int segment, string text, bool final) => throw new InvalidOperationException("Use asynchronous dictation delivery.");
        public async Task ApplyAsync(int segment, string text, bool final, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Available) throw new InvalidOperationException("Dictation.TargetLost");
            var payload = JsonSerializer.Serialize(new { scope = _scope, recording = _recording, segment, text, final });
            // Do not cancel an already dispatched edit: the recognition pump must know its real outcome.
            var result = await _editor.ExecuteScriptAsync("composer.dictate(" + payload + ")");
            if (result != "true") throw new InvalidOperationException("Dictation.TargetLost");
        }
    }
}
