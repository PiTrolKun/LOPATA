using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private void RenderCultureChoice()
    {
        var answers = _preparationAnswers!;
        _questionHost.Children.Add(LiteraryUi.Text(_l("Literary.Create.Cultures")));
        var countries = new LiteraryChoiceList(
            LiteraryChoices.CountryCodes.Select(id => (Id: id, Label: _l("Literary.Country." + id)))
                .OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase),
            _l("Literary.Create.SearchCountries"));
        countries.SetSelectedIds(answers.Values.GetValueOrDefault("14.countries", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries));
        countries.SelectionChanged += () =>
        {
            answers.Set("14.countries", string.Join('|', countries.SelectedIds));
            SaveAnswers();
        };
        _questionHost.Children.Add(countries);

        _questionHost.Children.Add(LiteraryUi.Text(_l("Literary.Create.CultureNotes")));
        var notes = new TextBox
        {
            Text = answers.Values.GetValueOrDefault("14.custom",
                answers.Values.ContainsKey("14.countries") ? "" : answers.Values.GetValueOrDefault("14", "")),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 82, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 10)
        };
        notes.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        notes.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        LiterarySpellChecking.Enable(notes, _language);
        notes.TextChanged += (_, _) => { _answersTimer.Stop(); _answersTimer.Start(); };
        _answerInputs["14.custom"] = notes;
        _questionHost.Children.Add(notes);
        _questionHost.Children.Add(LiteraryUi.Text(_l("Literary.Create.CultureHint")));
    }

    private void SaveCultureAnswer()
    {
        if (_preparationAnswers is null) return;
        var countryNames = _preparationAnswers.Values.GetValueOrDefault("14.countries", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => _l("Literary.Country." + id));
        var notes = _preparationAnswers.Values.GetValueOrDefault("14.custom", "").Trim();
        _preparationAnswers.Set("14", string.Join(", ", countryNames.Append(notes).Where(text => text.Length > 0)));
    }
}
