using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

/// <summary>Recipe browsing is a draft; only Apply changes the effective request.</summary>
public sealed class MusicRecipeWindow : Window
{
    private sealed record Choice(string Name, string Id, MusicTuningRecipe? Recipe = null, ModelExpertPreset? Preset = null);
    private readonly ListBox _choices = new() { DisplayMemberPath = "Name", MinWidth = 140 };
    private readonly StackPanel _details = new();
    private readonly Func<string, string> _l;
    private readonly ModelExpertPresets _store;
    private MusicExpertSettings _current;
    public MusicExpertSettings Result { get; private set; }
    public MusicRecipeWindow(MusicExpertSettings settings, Func<string, string> localize, ModelExpertPresets? store = null)
    {
        _l = localize; _store = store ?? ModelExpertPresets.For(settings.Variation, "Simple"); _current = settings.Snapshot(); Result = settings.Snapshot();
        MusicWishUi.PrepareWindow(this, L("Recipes") + " · " + MusicModelVariants.Name(settings.Variation), "Music.Recipes.Window", 940);
        var root = new DockPanel { Margin = new(16) };
        var footer = new DockPanel();
        var library = MusicWishUi.Button(L("Library"), "Music.Recipes.Library", OpenLibrary);
        footer.Children.Add(library);
        var actions = MusicWishUi.Footer(_l, () => { Result = SelectedSettings(); DialogResult = true; }, () => DialogResult = false);
        actions.HorizontalAlignment = System.Windows.HorizontalAlignment.Right; footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var hint = MusicWishUi.Text(MusicAceCatalog.IsAce(settings.Variation) ? _l("Music.Ace.Tuning.RecipesHint")
            : L("RecipesHint") + (settings.Variation == MusicModelVariants.Bf16 ? "\n" + _l("Music.Models.yue2.bf16.RecipeWarning") : "")); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        _choices.Margin = new(0, 0, 14, 0); columns.Children.Add(_choices);
        var scroll = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); columns.Children.Add(scroll); root.Children.Add(columns); Content = root;
        AutomationProperties.SetAutomationId(_choices, "Music.Recipes.Choices");
        _choices.SelectionChanged += (_, _) => Describe(); Reload(_current.Tuning?.SimplePreset);
    }
    private string L(string key) => _l("Music.Tuning." + key);
    private void Reload(string? selected)
    {
        _choices.Items.Clear(); _choices.Items.Add(new Choice(L("Ordinary"), "Ordinary"));
        foreach (var recipe in MusicTuningRecipes.For(_current.Variation)) _choices.Items.Add(new Choice(L("Recipe." + recipe.Id), recipe.Id, recipe));
        foreach (var preset in _store.Load()) _choices.Items.Add(new Choice(preset.Name, "User:" + preset.Name, Preset: preset));
        _choices.SelectedItem = _choices.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selected) ?? _choices.Items[0];
    }
    private MusicExpertSettings SelectedSettings()
    {
        if (_choices.SelectedItem is not Choice choice) return _current.Snapshot();
        if (choice.Preset is { } preset) return preset.Settings.Snapshot() with { Tuning = MusicTuningProfile.State(preset.Settings) with {
            SimplePreset = choice.Id, SimpleModified = false, ExpertModified = true } };
        return choice.Recipe?.Apply(_current) ?? MusicTuningRecipes.Ordinary(_current);
    }
    private void Describe()
    {
        _details.Children.Clear(); if (_choices.SelectedItem is not Choice choice) return;
        _details.Children.Add(MusicWishUi.Text(choice.Name, true));
        var description = choice.Preset is not null ? L("FullSnapshot") : choice.Recipe is not null
            ? L("Experimental") + " " + L("Description." + choice.Id) : MusicAceCatalog.IsAce(_current.Variation) ? _l("Music.Ace.Tuning.OrdinaryHint") : L("OrdinaryHint");
        _details.Children.Add(MusicWishUi.Text(description));
        var result = SelectedSettings();
        var keys = MusicAceCatalog.IsAce(result.Variation) ? choice.Recipe?.Values.Keys ?? (choice.Preset is not null ? result.Values.Keys : MusicAceTuningProfile.Fields)
            : choice.Recipe?.Values.Keys ?? (choice.Preset is not null ? result.Values.Keys :
            result.Values.Keys.Where(k => k.StartsWith("abc_sampling.", StringComparison.Ordinal) || k.StartsWith("semantic_sampling.", StringComparison.Ordinal))
                .Where(k => k.EndsWith("temperature", StringComparison.Ordinal) || k.EndsWith("top_p", StringComparison.Ordinal) || k.EndsWith("top_k", StringComparison.Ordinal)).Append("cfg_scale"));
        _details.Children.Add(MusicWishUi.Text(L("Fields"), true));
        foreach (var key in keys) {
            if (MusicAceCatalog.IsAce(result.Variation)) {
                _details.Children.Add(MusicWishUi.Text(_l("Music.Ace.Parameter." + key) + ": " + result.Get(key).ToString("0.######", CultureInfo.CurrentCulture)));
                _details.Children.Add(MusicWishUi.Text(_l("Music.Ace.Hint." + key))); continue;
            }
            var field = MusicExpertCatalog.Parameters.Single(p => p.Key == key);
            var suffix = key[(key.LastIndexOf('.') + 1)..];
            var label = MusicWishUi.Text(_l("Music.Expert.Group." + field.Group) + " · " +
                _l("Music.Expert.Parameter." + suffix) + ": " + result.Get(key).ToString("0.######", CultureInfo.CurrentCulture));
            label.ToolTip = key; _details.Children.Add(label);
            _details.Children.Add(MusicWishUi.Text(_l("Music.Expert.Description." + suffix)));
        }
        if (MusicAceCatalog.IsAce(result.Variation))
            foreach (var key in choice.Recipe?.TextValues.Keys ?? (choice.Preset is not null ? result.TextValues.Keys : [])) {
                _details.Children.Add(MusicWishUi.Text(_l("Music.Ace.Parameter." + key) + ": " + result.TextValues[key]));
                _details.Children.Add(MusicWishUi.Text(_l("Music.Ace.Hint." + key)));
            }
        if (choice.Recipe is { } recipe) {
            _details.Children.Add(MusicWishUi.Text(L("Source"), true)); _details.Children.Add(MusicWishUi.Text(recipe.Source));
            // The localized description states the supported adaptation; original details remain inspectable.
            _details.Children.Add(MusicWishUi.Text(L("Original"), true)); _details.Children.Add(MusicWishUi.Text(
                MusicAceCatalog.IsAce(result.Variation) ? L("Original." + recipe.Id) : recipe.Adaptation));
        } else if (choice.Preset?.Recipe is { } origin) {
            _details.Children.Add(MusicWishUi.Text(L("Source"), true)); _details.Children.Add(MusicWishUi.Text(origin.Source + "\n" + origin.Adaptation));
        }
    }
    private void OpenLibrary()
    {
        try {
            var window = new ModelExpertWindow(SelectedSettings(), _l, _store) { Owner = this };
            if (window.ShowDialog() == true) _current = window.Result.Snapshot();
            Reload(_current.Tuning?.SimplePreset);
        } catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            MessageBox.Show(this, L("Invalid") + "\n" + error.Message, L("Recipes"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
