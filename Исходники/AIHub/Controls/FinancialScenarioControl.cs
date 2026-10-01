using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

public sealed partial class FinancialScenarioControl : UserControl
{
    private Func<string, string> _l = key => key;
    private string _language = "ru", _root = "";
    private UserContextService? _context;
    private FinancialModelRuntime? _runtime;
    private IReadOnlyList<DebugModelInfo> _models = [];
    private DebugModelInfo? _selectedModel;
    private FinancialPerson _person = new();
    private string _unit = "";
    private bool _currencyExplicit;
    private bool _profileInitialized;
    private FinancialPeriod _period = FinancialPeriod.Month;
    private Action? _captureDraft;
    private readonly Dictionary<string, QuestionDraft> _drafts = [];
    private readonly Dictionary<string, FinancialAnswer> _answers = [];
    private int _question = -1;
    private bool _workspace, _busy;
    private string _stage = "", _displayStage = "answers";
    private int _completed;
    private FinancialPeriod _displayPeriod = FinancialPeriod.Month;
    private FinancialRunStore? _store;
    private StackPanel _body = new();
    private TextBlock? _status;
    private TextBox? _output;
    private TextBox? _calculationOutput;
    private Grid? _resultsGrid;
    private CancellationTokenSource? _cancel;
    public event Action? Changed;
    public FinancialScenarioControl()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SizeChanged += (_, _) => ArrangeResults();
        Render();
    }
    public void Configure(Func<string, string> localize, string language, StorageSettings settings, UserProfile profile, UserContextService context, IReadOnlyList<DebugModelInfo>? models = null)
    {
        _l = localize; _language = language; _context = context;
        if (!_currencyExplicit) _unit = FinancialCurrencies.Default(language);
        _root = Path.Combine(settings.Results.Locations.FirstOrDefault()?.Path ?? AppDataPaths.BaseDirectory, "Financial");
        if (models is not null) _models = models;
        else if (_models.Count == 0) _models = FinancialModelDiscovery.Discover(settings);
        _selectedModel ??= _models.FirstOrDefault();
        if (!_profileInitialized)
        {
            _captureDraft = null;
            _person = _person with { Name = profile.DisplayName, Location = string.Join(", ", new[] { profile.Location.City, profile.Location.Region, profile.Location.Country }.Where(v => !string.IsNullOrWhiteSpace(v))) };
            _profileInitialized = true;
        }
        Render();
    }
    public void Localize(Func<string, string> localize, string language)
    {
        _l = localize; _language = language;
        if (!_currencyExplicit) _unit = FinancialCurrencies.Default(language);
        Render();
    }
    private string L(string key) => _l("Finance." + key);
    private void Render()
    {
        _captureDraft?.Invoke(); _captureDraft = null;
        _body = new StackPanel { Margin = new Thickness(12) };
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _body.Children.Add(Text(L("Title"), true));
        if (_workspace) RenderWorkspace();
        else if (_question < 0) RenderSetup();
        else RenderQuestion();
        ArrangeResults(); Changed?.Invoke();
    }
    private void RenderSetup()
    {
        _body.Children.Add(Text(L("Model")));
        var models = ModelCombo(); _body.Children.Add(models);
        _body.Children.Add(Text(_models.Count == 0 ? L("NoModels") : L("ModelHint"), secondary: true));
        var name = Input(_person.Name, 120); var age = Input(_person.Age?.ToString() ?? "", 3); var location = Input(_person.Location, 200);
        Field("Name", name); Field("Age", age); Field("Location", location);
        var status = Choices(FinancialQuestions.Statuses.Select(s => (s, L("Status." + s))), _person.Status);
        Field("Status", status);
        var period = Choices(Enum.GetNames<FinancialPeriod>().Select(p => (p, L("Period." + p))), _period.ToString());
        period.SelectionChanged += (_, _) => ChangePeriod(Enum.Parse<FinancialPeriod>(((Choice)period.SelectedItem).Id));
        AutomationProperties.SetAutomationId(period, "Finance.Period"); Field("Period", period);
        var unit = FinancialCurrencies.Resolve(_unit, _language);
        var currencyOptions = FinancialCurrencies.All.Select(c => (c.Code, c.Code + " — " + c.Name(_language))).ToList();
        // Retain old free-form units when reopening earlier questionnaires.
        if (FinancialCurrencies.Find(unit) is null) currencyOptions.Add((unit, unit));
        var currency = Choices(currencyOptions, unit);
        currency.IsTextSearchEnabled = true;
        currency.SelectionChanged += (_, _) => { _unit = ((Choice)currency.SelectedItem).Id; _currencyExplicit = true; };
        Field("Currency", currency);
        _captureDraft = () =>
        {
            int? years = int.TryParse(age.Text, out var value) && value is >= 1 and <= 120 ? value : null;
            _person = new(name.Text.Trim(), years, location.Text.Trim(), ((Choice)status.SelectedItem).Id);
        };
        _status = Text(""); _body.Children.Add(_status);
        var actions = new WrapPanel(); _body.Children.Add(actions);
        AddButton(actions, "Begin", () =>
        {
            int? years = null;
            if (!string.IsNullOrWhiteSpace(age.Text)) { if (!int.TryParse(age.Text, out var value) || value is < 1 or > 120) { _status.Text = L("InvalidAge"); return; } years = value; }
            _person = new(name.Text.Trim(), years, location.Text.Trim(), ((Choice)status.SelectedItem).Id);
            ChangePeriod(Enum.Parse<FinancialPeriod>(((Choice)period.SelectedItem).Id));
            _question = 0; Render();
        }, primary: true);
        AddButton(actions, "Load", LoadFolder);
        AddButton(actions, "Folder", OpenFolder);
    }
    private ComboBox ModelCombo()
    {
        var combo = new ComboBox { ItemsSource = _models, DisplayMemberPath = "Name", SelectedItem = _selectedModel, MinHeight = 36, Margin = new Thickness(0, 4, 0, 8), IsEnabled = !_busy };
        combo.SelectionChanged += (_, _) => _selectedModel = combo.SelectedItem as DebugModelInfo;
        combo.SetResourceReference(ComboBox.FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(combo, "Finance.Model"); return combo;
    }
    private FinancialInput CurrentInput() => _store?.Load().Input ?? new() { Schema = 2, Person = _person, Unit = _unit, Language = _language, Answers = FinancialQuestions.All.Where(q => _answers.ContainsKey(q.Id)).Select(q => _answers[q.Id]).ToList() };
    private void LoadFolder()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "input.json|input.json", AddToRecent = false, InitialDirectory = Directory.Exists(_root) ? _root : "", Title = L("Load") };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Restore(Path.GetDirectoryName(picker.FileName)!); }
        catch { if (_status is not null) _status.Text = L("LoadFailed"); }
    }
    public void Restore(string path)
    {
        var store = new FinancialRunStore(path); var run = store.Load();
        _captureDraft = null; _drafts.Clear();
        _store = store; _person = run.Input.Person; _unit = FinancialCurrencies.Resolve(run.Input.Unit, run.Input.Language); _currencyExplicit = true; _profileInitialized = true;
        if (!string.IsNullOrWhiteSpace(run.Model.Path))
        {
            _selectedModel = run.Model;
            if (_models.All(m => m.Path != run.Model.Path)) _models = _models.Append(run.Model).ToArray();
        }
        _period = run.Input.Answers.FirstOrDefault()?.Period ?? FinancialPeriod.Month;
        _displayPeriod = _period;
        _answers.Clear(); foreach (var a in run.Input.Answers) _answers[a.Id] = FinancialCalculator.ForPersonalInput(a, run.Input.Schema);
        _completed = FinancialAnalysisPlan.Stages.Count(s => store.ReadCurrentStage(s.Id) is not null);
        _stage = _completed == FinancialAnalysisPlan.Stages.Count ? "done" : "";
        _displayStage = _stage == "done" ? "final" : "answers";
        _workspace = true; _question = FinancialQuestions.All.Count - 1; Render();
    }
    private void OpenFolder()
    {
        try { var path = _store?.DirectoryPath ?? _root; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { if (_status is not null) _status.Text = L("FolderFailed"); }
    }
    private void Field(string key, FrameworkElement element)
    {
        if (string.IsNullOrEmpty(AutomationProperties.GetAutomationId(element))) AutomationProperties.SetAutomationId(element, "Finance." + key);
        _body.Children.Add(Text(L(key))); _body.Children.Add(element);
    }
    private static TextBlock Text(string value, bool title = false, bool secondary = false)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8), FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(TextBlock.FontSizeProperty, title ? "UiSectionFontSize" : "UiBodyFontSize");
        text.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush"); return text;
    }
    private static TextBox Input(string value, int max = 40)
    {
        var input = new TextBox { Text = value, MaxLength = max, Padding = new Thickness(8), MinHeight = 36, Margin = new Thickness(0, 0, 0, 10) };
        input.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush"); input.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
        input.SetResourceReference(TextBox.BorderBrushProperty, "LineBrush"); input.SetResourceReference(TextBox.FontSizeProperty, "UiBodyFontSize"); return input;
    }
    private sealed record Choice(string Id, string Text);
    private static ComboBox Choices(IEnumerable<(string Id, string Text)> choices, string selected)
    {
        var values = choices.Select(v => new Choice(v.Id, v.Text)).ToArray();
        var combo = new ComboBox { ItemsSource = values, DisplayMemberPath = "Text", SelectedItem = values.First(v => v.Id == selected), MinHeight = 36, Margin = new Thickness(0, 0, 0, 10) };
        combo.SetResourceReference(ComboBox.FontSizeProperty, "UiBodyFontSize"); return combo;
    }
    private Button AddButton(Panel panel, string key, Action action, bool primary = false, bool enabled = true)
    {
        var button = new Button { Content = L(key), Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(14, 8, 14, 8), IsEnabled = enabled };
        button.SetResourceReference(Button.StyleProperty, primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");
        button.SetResourceReference(Button.FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(button, "Finance." + key); button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
}
