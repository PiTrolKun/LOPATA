using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services.VisualConstructor;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private Window? _researchWindow;
    private ConstructorResearcherPanel? _researcher;
    private (string Project, ConstructorReference Source)? _pendingResearchSource;

    private Task OpenResearcherAsync()
    {
        if (_researchWindow is { IsVisible: true } current && _researcher?.Project == _project?.FilePath)
        {
            current.Activate();
            return Task.CompletedTask;
        }
        ResearcherDialog(_project?.FilePath).Show();
        return Task.CompletedTask;
    }

    internal async Task ResumeResearcherAsync(BackgroundOperationState state, CancellationToken token)
    {
        var request = state.Input.Deserialize<ConstructorResearchRequest>()
            ?? throw new InvalidDataException("VisualConstructor.InvalidGemmaReply");
        var window = _researchWindow is { IsVisible: true } active && _researcher?.Project == request.Project
            ? active : ResearcherDialog(request.Project);
        if (!window.IsVisible) window.Show();
        await _researcher!.ResumeAsync(state, token);
    }

    private Window ResearcherDialog(string? project)
    {
        _researchWindow?.Close();
        Action<ConstructorReference>? transfer = null;
        if (project is not null && project == _project?.FilePath)
            transfer = source =>
            {
                if (_project?.FilePath != project) return;
                _pendingResearchSource = (project, source);
                _state.Text = string.Format(T("ResearchAttached"), source.Title);
                _researchWindow?.Close();
            };
        var researcher = new ConstructorResearcherPanel(_localize, Path.GetDirectoryName(_recentFile)!, project, transfer, prepareModel: PrepareGemmaAsync);
        var dialog = EditDialog(T("Researcher"), researcher, out var buttons, width: 1110, height: 740);
        buttons.Children.Add(PromptDialogUi.Button(T("CloseProposal"), dialog.Close));
        dialog.Closed += (_, _) =>
        {
            researcher.Cancel();
            if (ReferenceEquals(_researchWindow, dialog)) { _researchWindow = null; _researcher = null; }
        };
        _researcher = researcher;
        _researchWindow = dialog;
        return dialog;
    }
}
