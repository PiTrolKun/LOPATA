using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public partial class ImageAnalysisWorkspaceControl
{
    private readonly PromptPairStore _promptStore = PromptPairStore.ForUser();
    private List<PromptPairPreset> _promptPairs = [];
    private PromptPairPreset? _selectedPromptPair;
    private bool _customPromptMode;
    private bool _suppressPromptSelection;
    private bool _promptStoreFailed;
    private string _promptError = string.Empty;
    private string _wishes = string.Empty;
    private Func<string> _promptLanguage = () => "ru";
    public event EventHandler? DraftSettingsChanged;

    private bool SupportsCustomPrompts => ImageAnalysisModeCapabilities.UsesOmniConversation(_bundleId);

    private void LoadPromptSettings(ImageAnalysisLiterarySettings settings)
    {
        _wishes = settings.Wishes;
        _customPromptMode = SupportsCustomPrompts && settings.PromptMode == PromptModes.Custom;
        _selectedPromptPair = settings.CustomPrompts is { ContractId: OmniPromptPairAdapter.ContractId } pair ? pair with { } : null;
        ReloadPromptPairs();
        PopulatePromptPairs();
        RefreshPromptControls();
    }

    private bool ReloadPromptPairs()
    {
        try
        {
            _promptPairs = _promptStore.Load();
            _promptStoreFailed = false; _promptError = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _promptStoreFailed = true;
            _promptError = PromptDialogUi.Error(ex, _localize);
            return false;
        }
    }

    private string? SavePromptPairs(IReadOnlyList<PromptPairPreset> next)
    {
        try { _promptStore.Save(next); _promptPairs = next.ToList(); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return PromptDialogUi.Error(ex, _localize); }
    }

    private void PopulatePromptPairs()
    {
        _suppressPromptSelection = true;
        try
        {
            var visible = _promptPairs.Where(p => p.ContractId == OmniPromptPairAdapter.ContractId).ToList();
            // A restored work keeps its own snapshot even if its preset was edited or removed.
            if (_selectedPromptPair is { } snapshot)
            {
                var index = visible.FindIndex(p => p.Id == snapshot.Id);
                if (index >= 0) visible[index] = snapshot;
                else visible.Add(snapshot);
            }
            PromptPairComboBox.ItemsSource = visible;
            PromptPairComboBox.SelectedItem = _selectedPromptPair;
        }
        finally { _suppressPromptSelection = false; }
    }

    private void RefreshPromptControls(bool? interaction = null)
    {
        if (StandardPromptButton is null) return;
        var editable = (interaction ?? !_isBusy) && !_readOnlyMode && !OmniSessionCompatibility.IsLegacyBeta(_session)
            && _session?.Status != ImageAnalysisLiteraryStatuses.Completed && _session?.ContextBlocked != true;
        var custom = SupportsCustomPrompts && _customPromptMode;
        PromptModePanel.Visibility = SupportsCustomPrompts ? Visibility.Visible : Visibility.Collapsed;
        CustomPromptPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        StandardSettingsGrid.IsEnabled = editable && !custom;
        StandardSettingsGrid.Opacity = custom ? 0.45 : 1;
        StandardPromptButton.Content = _localize("PromptPairs.Standard");
        CustomPromptButton.Content = _localize("PromptPairs.Custom");
        StandardPromptButton.SetResourceReference(StyleProperty, custom ? "SecondaryButtonStyle" : "PrimaryButtonStyle");
        CustomPromptButton.SetResourceReference(StyleProperty, custom ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        StandardPromptButton.IsEnabled = CustomPromptButton.IsEnabled = editable;
        PromptPairComboBox.IsEnabled = CreatePromptPairButton.IsEnabled = ManagePromptPairsButton.IsEnabled = editable;
        WishesButton.IsEnabled = editable;
        WishesButton.Content = string.IsNullOrWhiteSpace(_wishes) ? "✎" : "✎ •";
        PromptDialogUi.Label(WishesButton, _localize("ImageAnalysis.Workspace.Settings.Wishes")
            + (string.IsNullOrWhiteSpace(_wishes) ? string.Empty : " — " + _localize("PromptPairs.Filled")));
        PromptDialogUi.Label(PromptPairComboBox, _localize("PromptPairs.Select"));
        PromptDialogUi.Label(CreatePromptPairButton, _localize("PromptPairs.Create"));
        PromptDialogUi.Label(ManagePromptPairsButton, _localize("PromptPairs.Manage"));
        PromptPairStatusText.Text = custom ? _promptStoreFailed ? _promptError
            : _selectedPromptPair is null ? _localize("PromptPairs.Empty") : string.Empty : string.Empty;
        GenerateButton.IsEnabled = editable && _session?.ContextBlocked != true && (!custom || _selectedPromptPair is not null);
        GenerateButton.Opacity = _session?.ContextBlocked == true ? 0.45 : 1;
    }

    private void RememberPromptSettings()
    {
        if (_session is null || _isBusy || _readOnlyMode || _session.Versions.Count > 0) return;
        var settings = ReadSettings();
        settings.LanguageCode = _session.Settings.LanguageCode;
        _session.Settings = settings;
        DraftSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StandardPromptButton_Click(object sender, RoutedEventArgs e)
    { _customPromptMode = false; RefreshPromptControls(); RememberPromptSettings(); }

    private void CustomPromptButton_Click(object sender, RoutedEventArgs e)
    {
        _customPromptMode = true;
        ReloadPromptPairs(); PopulatePromptPairs(); RefreshPromptControls(); RememberPromptSettings();
    }

    private void PromptPairComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPromptSelection) return;
        _selectedPromptPair = PromptPairComboBox.SelectedItem is PromptPairPreset pair ? pair with { } : null;
        RefreshPromptControls(); RememberPromptSettings();
    }

    private void WishesButton_Click(object sender, RoutedEventArgs e)
    {
        var text = PromptDialogUi.EditWishes(Window.GetWindow(this), _wishes, _localize);
        if (text is null) return;
        _wishes = text; RefreshPromptControls(); RememberPromptSettings();
    }

    private void CreatePromptPairButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ReloadPromptPairs()) { RefreshPromptControls(); return; }
        var defaults = OmniPromptPairAdapter.CreateDefault(_promptLanguage());
        var editor = new PromptPairEditorWindow(Window.GetWindow(this), defaults, defaults, _promptPairs, _localize,
            candidate => SavePromptPairs(_promptPairs.Append(candidate).ToList()));
        if (editor.ShowDialog() == true) _selectedPromptPair = editor.Result;
        PopulatePromptPairs(); RefreshPromptControls(); RememberPromptSettings();
    }

    private void ManagePromptPairsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ReloadPromptPairs()) { RefreshPromptControls(); return; }
        var selectedId = _selectedPromptPair?.Id;
        var before = _promptPairs.FirstOrDefault(p => p.Id == selectedId);
        var window = new PromptPairManagerWindow(Window.GetWindow(this), _promptPairs,
            OmniPromptPairAdapter.CreateDefault(_promptLanguage()), _localize, SavePromptPairs);
        window.ShowDialog();
        _promptPairs = window.Items;
        var after = _promptPairs.FirstOrDefault(p => p.Id == selectedId);
        if (before is not null && before != after) _selectedPromptPair = after;
        PopulatePromptPairs(); RefreshPromptControls(); RememberPromptSettings();
    }
}
