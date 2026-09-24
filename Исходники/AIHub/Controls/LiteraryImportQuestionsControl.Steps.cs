using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryImportQuestionsControl
{
    private bool RenderPreparationStep()
    {
        if (_screen == "questions" || _importSession is null) return false;
        _questionRuntime?.Dispose(); _questionRuntime = null;
        _activity.Children.Clear();
        if (_screen == "rag")
        {
            _rag = new LiteraryImportRagControl(_importSession, _answers, _language, _l, () => NavigateStep("parts"));
            _body.Children.Add(_rag);
        }
        else if (_screen == "parts")
        {
            _workingParts = new LiteraryImportWorkingPartsControl(_importSession, _l, () => NavigateStep("jelly"));
            _body.Children.Add(_workingParts);
        }
        else if (_screen == "jelly")
        {
            _jelly = new LiteraryImportJellyControl(_importSession, _language, _l, () => NavigateStep("workspace"));
            _body.Children.Add(_jelly);
        }
        else
        {
            _workspace = new LiteraryImportWorkspaceControl(_importSession, _answers, _l, () => OpenWorkspaceRequested?.Invoke());
            _body.Children.Add(_workspace);
        }
        return true;
    }

    private void NavigateStep(string screen)
    {
        if (_closed || IsBusy || !TrySaveDraft()) return;
        if (screen == "parts" && _importSession?.State.RagStatus != "ready") return;
        if (screen == "workspace" && _importSession?.State.MemoryStatus != "ready") return;
        try
        {
            _answers.Set("post-review.screen", screen);
            _screen = screen; Render();
            // A later step must open at its top even if the previous screen was scrolled.
            for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer scroll) { scroll.ScrollToTop(); break; }
        }
        catch (Exception ex) { Error(ex); }
    }

    public bool TryGoBackStep()
    {
        if (_screen == "questions") return false;
        if (_importSession?.State.Stage == "workspace-ready") return true;
        NavigateStep(_screen == "workspace" ? "jelly" : _screen == "jelly" ? "parts" : _screen == "parts" ? "rag" : "questions");
        return true;
    }
}
