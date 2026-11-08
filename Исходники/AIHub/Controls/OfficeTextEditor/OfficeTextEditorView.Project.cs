using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private readonly StackPanel _projectNotice = new() { Margin = new Thickness(12,4,12,4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _projectDetails = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private void BuildProjectNotice(DockPanel footer)
    {
        var create = Action(T("CreateProject"), "CreateProject", () => _ = GuardAsync(async () =>
        {
            if (_saving || _closing) return;
            _saving = true;
            try { await SaveDocumentAsync(false, true); RefreshProjectNotice(); }
            finally { _saving = false; }
        }));
        create.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        _projectNotice.Children.Add(_projectDetails);
        _projectNotice.Children.Add(create);
        AutomationProperties.SetAutomationId(_projectNotice,"Office.Editor.ProjectNotice");
        DockPanel.SetDock(_projectNotice, Dock.Top); footer.Children.Insert(0,_projectNotice);
    }

    private void RefreshProjectNotice()
    {
        if (_work == null || _closed) return;
        RefreshSheetStrip();
        var shouldOffer = ProjectPromptNeeded;
        _projectNotice.Visibility = shouldOffer ? Visibility.Visible : Visibility.Collapsed;
        _projectDetails.Text = shouldOffer ? T("ProjectRequired") + Environment.NewLine + ProjectReasons() : "";
    }

    internal bool ProjectPromptNeeded => _ready && !_closed && Work.NeedsProject && !HasProject && (SourcePath != null || TargetPath != null);

    internal async Task OfferProjectSaveAsync()
    {
        if (!ProjectPromptNeeded || _saving || _closing) return;
        _saving=true;
        try
        {
            var dialog=Dialog(T("ProjectRequired"),out var panel);
            panel.Children.Add(new TextBlock { Text=T("ProjectRequired")+Environment.NewLine+ProjectReasons(), TextWrapping=TextWrapping.Wrap });
            panel.Children.Add(Action(T("CreateProject"),"Reader.ProjectSave",()=>dialog.DialogResult=true));
            panel.Children.Add(Action(T("Cancel"),"Reader.ProjectCancel",()=>dialog.DialogResult=false));
            if(dialog.ShowDialog()==true) await SaveDocumentAsync(false,true);
        }
        finally { _saving=false; }
    }

    internal Task RecordAiActionAsync(string reason) => RecordAiActionAsync(reason, null);
    internal async Task RecordAiActionAsync(string reason, OutputModelUse? model)
    {
        if (!_ready || _closed) return;
        await Work.AddProjectReasonAsync(reason, _lifetime.Token, model);
        RefreshProjectNotice();
    }

    private async Task AddMaterialAsync()
    {
        if (!_ready || _closed || _closing) return;
        var picker = new OpenFileDialog { Title = T("AddMaterial"), Multiselect = true };
        if (picker.ShowDialog(_owner) != true) return;
        await WithStorageAsync(async () =>
        {
            await CheckpointAsync();
            foreach (var path in picker.FileNames) await Work.AddSheetFromFileAsync(path,_lifetime.Token);
        });
        RefreshProjectNotice();
        _status.Text = T("DraftSaved");
    }

    private void ShowMaterials()
    {
        if (!_ready || _closing) return;
        var dialog = Dialog(T("Materials"), out var panel);
        var files = Work.Materials.Concat(Work.Sheets.SelectMany(sheet => sheet.Files)).Distinct();
        var materials=Work.OriginalMaterial is { } original ? files.Prepend(original) : files;
        if (!materials.Any()) panel.Children.Add(new TextBlock { Text = T("NoMaterials") });
        foreach (var material in materials)
        {
            var name = material.Name;
            var label=material==Work.OriginalMaterial ? T("OriginalMaterial") + " · " + name : name;
            panel.Children.Add(Action(label,"Material." + material.Entry, () => _ = GuardAsync(async () =>
            {
                var picker = new Microsoft.Win32.SaveFileDialog { FileName = name, Title = T("Export"), OverwritePrompt = true };
                if (picker.ShowDialog(dialog) == true)
                    await Work.ExportMaterialAsync(material,picker.FileName,_lifetime.Token);
            }), T("Export") + " · " + name));
        }
        dialog.ShowDialog();
    }

    private bool CheckSourceCompatibility => IsTextSheet && CurrentSheetId == Work.Sheets[0].Id && !HasProject && Path.GetExtension(TargetPath ?? SourcePath)?.ToLowerInvariant()
        is ".txt" or ".md" or ".markdown";
    private readonly SemaphoreSlim _compatibility = new(1,1);
    private long _checkedRevision = -1;
    private async Task UpdateCompatibilityAsync(long revision)
    {
        if (!_ready || !CheckSourceCompatibility || _closed) return;
        await _compatibility.WaitAsync(_lifetime.Token);
        try
        {
            if (revision <= _checkedRevision) return;
            if (Work.CompatibilityBaselineUnavailable)
            {
                // Older recovery files do not carry the original import's formatting signature.
                // Treating the recovered document as its own baseline would silently waive losses.
                await Work.SetFormatReasonAsync(Work.FormatReason ?? T("RecoveryCompatibilityUnknown"),_lifetime.Token);
                _checkedRevision = revision;
                RefreshProjectNotice();
                return;
            }
            var state = await _browser.RequestAsync(new { action = "compatibility" },_lifetime.Token);
            bool plain = string.Equals(Path.GetExtension(TargetPath ?? SourcePath),".txt",StringComparison.OrdinalIgnoreCase);
            bool supported = state.GetProperty(plain ? "plainCompatible" : "markdownCompatible").GetBoolean();
            var reason = T("IncompatibleFormatReason");
            if (!supported && state.TryGetProperty(plain ? "plainReasons" : "markdownReasons",out var losses)
                && losses.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var items=losses.EnumerateArray().Select(x=>x.GetString()).Where(x=>x is
                    "font" or "emphasis" or "paragraph" or "list" or "table" or "image" or "link" or "comment" or "page" or "object")
                    .Distinct(StringComparer.Ordinal).Select(x=>T("Loss." + x)).ToArray();
                if(items.Length>0)reason += " " + string.Join(", ",items) + ".";
            }
            await Work.SetFormatReasonAsync(supported ? null : reason,_lifetime.Token);
            _checkedRevision = revision;
            RefreshProjectNotice();
        }
        finally { _compatibility.Release(); }
    }
}
