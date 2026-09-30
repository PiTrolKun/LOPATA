using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryNavigationControl
{
    private void CreateBackgroundImportPage()
    {
        var page = new LiteraryImportControl(_l, _language, _initialFolder, _store); _import = page;
        page.FirstStepChanged += firstStep => SetValue(IsImportFirstStepProperty, firstStep);
        page.BackRequested += GoBack;
        page.HomeRequested += () => { page.Dispose(); if (_import == page) _import = null; HomeRequested?.Invoke(); };
        page.OpenRequested += entry =>
        {
            page.Dispose(); if (_import == page) _import = null;
            _store.SetActive(entry.Id); LoadProjects(); OpenWorkspace(entry);
        };
    }

    internal void RestoreImportPage(string directory)
    {
        if (_import?.CurrentImportRoot?.Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) == true) return;
        if (_import?.IsBusy == true || _workspace?.CanLeave() == false || _interview?.CanLeave() == false)
            throw new BackgroundOperationWaitingException("workspace_busy");
        _import?.Dispose(); _workspace = null; _interview = null; _choosingCreation = false;
        CreateBackgroundImportPage(); Render();
    }

    internal async Task ResumeBackgroundImportAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (state.Project is null) throw new InvalidDataException("Missing import session.");
        RestoreImportPage(state.Project);
        await _import!.ResumeBackgroundImportAsync(state, token);
    }

    internal async Task<bool> ViewBackgroundImportResultAsync(BackgroundOperationNotice notice, CancellationToken token)
    {
        if (notice.Project is null) return false;
        RestoreImportPage(notice.Project);
        return await _import!.ViewBackgroundImportResultAsync(notice, token);
    }
}
