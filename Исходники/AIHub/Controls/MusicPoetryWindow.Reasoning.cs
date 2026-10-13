using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private string? _reasoningMode;
    private MusicPoetryReasoningPreference _reasoningPreference = new() { ModeId = "on" };
    private bool _reasoningFailed;
    private MusicPoetryReasoningOptions _reasoningOptions = MusicPoetryReasoningOptions.Disabled;

    private void LoadReasoning()
    {
        CloseThoughtScale();
        _reasoning.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _reasoningFailed = false; _reasoningMode = null;
        _reasoningOptions = MusicPoetryReasoningOptions.Disabled;
        if (MusicPoetryReasoning.ModelId(_model) is { } modelId)
        {
            try
            {
                _reasoningPreference = _preferences.Load().Reasoning.GetValueOrDefault(modelId)
                    ?? new() { ModeId = MusicPoetryReasoning.DefaultMode };
                _reasoningMode = _reasoningPreference.ModeId;
                _reasoningOptions = MusicPoetryReasoning.Resolve(_model, _reasoningMode, _reasoningPreference);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _reasoningFailed = true;
                _status.Text = L("ReasoningChoiceUnavailable") + " " + ex.Message;
            }
        }
        ReasoningCaption();
    }

    private void ReasoningCaption()
    {
        var supported = MusicPoetryReasoning.Supports(_model);
        _reasoning.Content = supported && !_reasoningFailed
            ? $"{L("Reasoning")}: {L(_reasoningOptions.Enabled ? "ReasoningOn" : "ReasoningOff")} ▾" : L("Reasoning");
        var hint = _reasoningFailed ? L("ReasoningChoiceUnavailable")
            : L(supported ? MusicPoetryReasoning.Extended(MusicPoetryReasoning.ModelId(_model)) ? "ReasoningExtendedHint"
                : "ReasoningCoreHint" : "ReasoningUnavailable");
        if (supported && !_reasoningFailed && !MusicPoetryReasoning.Extended(MusicPoetryReasoning.ModelId(_model)))
            hint = string.Format(hint, MusicPoetryReasoning.DefaultBudget(MusicPoetryReasoning.ModelId(_model)));
        if (supported && !_reasoningFailed && MusicPoetryReasoning.Extended(MusicPoetryReasoning.ModelId(_model)))
            hint += "\n" + string.Format(L("ReasoningBudgetValue"), _reasoningPreference.Budget ?? 2048)
                + (_reasoningOptions.Effort is null ? "" : " · " + L("ReasoningEffort") + ": " + _reasoningOptions.Effort);
        _reasoning.ToolTip = supported ? hint + "\n" + L("ThoughtFontShortcut") : hint;
        AutomationProperties.SetName(_reasoning, _reasoning.Content.ToString());
        AutomationProperties.SetHelpText(_reasoning, _reasoning.ToolTip.ToString());
    }

    private void ReasoningMenu()
    {
        CloseThoughtScale();
        if (!_reasoning.IsEnabled || _model is null || IsWorking || _background?.IsRunning == true || _background?.HasPending == true) return;
        var modelPath = _model.Path;
        var menu = new ContextMenu { PlacementTarget = _reasoning, Placement = PlacementMode.Top,
            Resources = Resources };
        menu.SetResourceReference(StyleProperty, "Spelling.ContextMenu");
        menu.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        foreach (var mode in new[] { "on", "off" })
        {
            var item = new MenuItem { Header = (_reasoningMode == mode ? "✓  " : "    ") + L(mode == "on" ? "ReasoningEnable" : "ReasoningDisable"),
                IsCheckable = true, IsChecked = _reasoningMode == mode };
            item.SetResourceReference(StyleProperty, "Spelling.MenuItem");
            AutomationProperties.SetName(item, L(mode == "on" ? "ReasoningEnable" : "ReasoningDisable"));
            AutomationProperties.SetAutomationId(item, "Music.Poetry.Reasoning." + mode);
            item.Click += (_, _) => {
                if (_reasoning.IsEnabled && _model?.Path == modelPath) SetReasoning(mode);
            };
            menu.Items.Add(item);
        }
        if (MusicPoetryReasoning.Extended(MusicPoetryReasoning.ModelId(_model)))
        {
            menu.Items.Add(new Separator());
            foreach (var budget in new[] { 2048, 4096 })
            {
                var item = Option(L("ReasoningBudget") + ": " + string.Format(L("ReasoningBudgetValue"), budget), "Budget." + budget,
                    (_reasoningPreference.Budget ?? 2048) == budget, () => SetReasoningOptions(_reasoningPreference with { Budget = budget }));
                item.IsEnabled = _reasoningOptions.Enabled; menu.Items.Add(item);
            }
            if (MusicPoetryReasoning.ModelId(_model) == "gamma")
            {
                foreach (var level in new[] { "medium", "xhigh" })
                {
                    var item = Option(L("ReasoningEffort") + ": " + L("ReasoningEffort." + level), "Effort." + level,
                        (_reasoningPreference.Effort ?? "medium") == level,
                        () => SetReasoningOptions(_reasoningPreference with { Effort = level }));
                    item.IsEnabled = _reasoningOptions.Enabled; menu.Items.Add(item);
                }
            }
            var preserve = Option(L("ReasoningPreserve"), "Preserve", _reasoningPreference.PreserveThoughts ?? true,
                () => SetReasoningOptions(_reasoningPreference with { PreserveThoughts = !(_reasoningPreference.PreserveThoughts ?? true) }));
            preserve.IsEnabled = _reasoningOptions.Enabled; preserve.ToolTip = L("ReasoningPreserveHint");
            menu.Items.Add(preserve);
            if (_reasoningFailed) menu.Items.Add(Option(L("ReasoningReset"), "Reset", false,
                () => SetReasoningOptions(new() { ModeId = "on" })));
        }
        _reasoning.ContextMenu = menu; menu.IsOpen = true;

        MenuItem Option(string header, string id, bool selected, Action save)
        {
            var option = new MenuItem { Header = (selected ? "✓  " : "    ") + header, IsCheckable = true, IsChecked = selected };
            option.SetResourceReference(StyleProperty, "Spelling.MenuItem");
            AutomationProperties.SetName(option, header);
            AutomationProperties.SetAutomationId(option, "Music.Poetry.Reasoning." + id);
            option.Click += (_, _) => { if (_reasoning.IsEnabled && _model?.Path == modelPath) save(); };
            return option;
        }
    }

    private void SetReasoning(string mode)
    {
        if (!_reasoning.IsEnabled || IsWorking || _background?.IsRunning == true || _background?.HasPending == true
            || !MusicPoetryReasoning.Supports(_model)) return;
        try
        {
            // Change the live selection only after the atomic save succeeds.
            _preferences.SetReasoning(MusicPoetryReasoning.ModelId(_model)!, mode);
            LoadReasoning(); Recount(); Availability();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _status.Text = L("Selection.PreferencesError") + " " + ex.Message; }
    }

    private int ReplyReserve() => _model?.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase) == true
        ? 4096 : _reasoningOptions.ReplyReserve(_capacity);

    private void SetReasoningOptions(MusicPoetryReasoningPreference preference)
    {
        if (!_reasoning.IsEnabled || IsWorking || _background?.IsRunning == true || _background?.HasPending == true
            || !MusicPoetryReasoning.Supports(_model)) return;
        try
        {
            _preferences.SetReasoningOptions(MusicPoetryReasoning.ModelId(_model)!, preference);
            LoadReasoning(); Recount(); Availability();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _status.Text = L("Selection.PreferencesError") + " " + ex.Message; }
    }
}
