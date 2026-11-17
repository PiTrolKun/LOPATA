using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using AIHub.Services.VisualScenario;

namespace AIHub.Controls.VisualScenario;

internal sealed partial class VisualScenarioControl
{
    private System.Windows.Controls.ComboBox? _advisorChoice, _artistChoice;
    private Popup? _senseNovaHint;
    internal bool UsesResidentModel(string id) => id == VisualSenseNova.Id && _senseNova.IsRunning;

    private void ShowSenseNovaHint(string id, bool artist)
    {
        if (_senseNovaHint is not null) _senseNovaHint.IsOpen = false;
        var target = artist ? _artistChoice : _advisorChoice;
        if (target is not null) _senseNovaHint = VisualSenseNovaHint.Show(target, _localize, id,
            artist ? _project.Advisor : _project.Artist, artist);
    }

    internal Task ReleaseModelAsync(string id, bool artist)
    {
        if (id == VisualSenseNova.Id) return _senseNova.RetireAsync();
        if (id != (artist ? _project.Artist : _project.Advisor)) return Task.CompletedTask;
        return artist ? _artistSession.RetireAsync() : _advisor.RetireAsync();
    }

    private DockPanel WithModelRemoval(System.Windows.Controls.ComboBox combo, string id, bool artist)
    {
        if (artist) _artistChoice = combo;
        else _advisorChoice = combo;
        var button = MusicWishUi.Button(_localize("Music.Models.Remove.Action"),
            artist ? "Visual.RemoveArtist" : "Visual.RemoveAdvisor", () => RunUi(async () =>
            {
                if (_busy || _changingModel || Pending is not null) return;
                _changingModel = true;
                UpdateCommands();
                try { await VisualModelRemoval.RemoveAsync(OwnerWindow, _localize, _components, id, artist,
                    () => ReleaseModelAsync(id, artist)); }
                finally { _changingModel = false; RenderLeft(); }
            }));
        button.IsEnabled = !_busy && _components.HasRemovableFiles(id, artist);
        button.ToolTip = string.Format(_localize("Generation.Remove.Tooltip"),
            artist ? ImageGenerationCatalog.DisplayName(id) : _components.AdvisorName(id));
        var row = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(combo);
        return row;
    }
}
