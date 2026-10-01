using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls;

public sealed partial class FinancialScenarioControl
{
    private sealed record QuestionDraft(string Amount, FinancialCoverage Coverage, FinancialPeriod Period);

    private void RenderQuestion()
    {
        var q = FinancialQuestions.All[_question];
        var header = Text($"{L("Question." + q.Id)} [{L("PerPeriod." + _period)}] · {_question + 1} / {FinancialQuestions.All.Count} · {L("Category." + q.Category)}", true);
        AutomationProperties.SetAutomationId(header, "Finance.Question"); _body.Children.Add(header);
        _answers.TryGetValue(q.Id, out var answer);
        _drafts.TryGetValue(q.Id, out var draft);
        var amount = Input(draft?.Amount ?? (answer is { Kind: FinancialAnswerKind.Known }
            ? FinancialCalculator.ForPeriod(FinancialCalculator.Monthly(answer.Amount, answer.Period), _period).ToString(CultureInfo.InvariantCulture) : "0"));
        AutomationProperties.SetAutomationId(amount, "Finance.Amount");
        var mode = q.Income ? FinancialCoverage.Self : draft?.Coverage ?? answer?.Coverage ?? FinancialCoverage.Self;
        var amountPanel = new StackPanel();
        amountPanel.Children.Add(Text(L(q.Income ? "Amount" : "PersonalAmount"))); amountPanel.Children.Add(amount);
        void UpdateCoverage() => amountPanel.Visibility = mode == FinancialCoverage.External ? Visibility.Collapsed : Visibility.Visible;
        if (!q.Income)
        {
            _body.Children.Add(Text(L("Coverage")));
            var choices = new WrapPanel(); _body.Children.Add(choices);
            var group = "FinanceCoverage" + Guid.NewGuid().ToString("N");
            foreach (var value in Enum.GetValues<FinancialCoverage>())
            {
                var option = new RadioButton { Content = L("Coverage." + value), GroupName = group,
                    IsChecked = value == mode, Margin = new Thickness(0, 4, 22, 12), Padding = new Thickness(4) };
                option.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
                option.SetResourceReference(RadioButton.FontSizeProperty, "UiBodyFontSize");
                AutomationProperties.SetAutomationId(option, "Finance.Coverage." + value);
                option.Checked += (_, _) => { mode = value; UpdateCoverage(); };
                choices.Children.Add(option);
            }
        }
        _body.Children.Add(amountPanel); UpdateCoverage();
        _captureDraft = () => _drafts[q.Id] = new(amount.Text, mode, _period);
        _status = Text(""); _body.Children.Add(_status);
        var actions = new WrapPanel(); _body.Children.Add(actions);
        AddButton(actions, "Next", () =>
        {
            decimal value = 0;
            if (mode != FinancialCoverage.External && !FinancialCalculator.TryAmount(amount.Text, out value))
            { _status.Text = L("InvalidPersonalAmount"); return; }
            _answers[q.Id] = new(q.Id, FinancialAnswerKind.Known, value, _period, mode);
            _question++;
            if (_question >= FinancialQuestions.All.Count) { _workspace = true; _question = FinancialQuestions.All.Count - 1; }
            _displayPeriod = _period;
            Render();
        }, primary: true);
    }

    public bool GoBack()
    {
        if (_busy || HasPendingFinance()) return false;
        _captureDraft?.Invoke(); _captureDraft = null;
        if (_workspace) BeginEdit(FinancialQuestions.All.Count - 1);
        else if (_question >= 0) { _question--; Render(); }
        else return false;
        return true;
    }

    private void BeginEdit(int question = -1)
    {
        _captureDraft = null; _store = null; _workspace = false;
        _question = question; _stage = ""; _completed = 0; _displayStage = "answers";
        ChangePeriod(_period); Render();
    }

    private void NewSurvey()
    {
        _captureDraft = null; _answers.Clear(); _drafts.Clear(); _unit = FinancialCurrencies.Default(_language); _currencyExplicit = false;
        BeginEdit();
    }

    private void ChangePeriod(FinancialPeriod period)
    {
        foreach (var (id, answer) in _answers.ToArray())
            _answers[id] = answer with { Amount = FinancialCalculator.ForPeriod(FinancialCalculator.Monthly(answer.Amount, answer.Period), period), Period = period };
        foreach (var (id, draft) in _drafts.ToArray())
            if (draft.Period != period && FinancialCalculator.TryAmount(draft.Amount, out var value))
                _drafts[id] = draft with { Amount = FinancialCalculator.ForPeriod(FinancialCalculator.Monthly(value, draft.Period), period).ToString(CultureInfo.InvariantCulture), Period = period };
        _period = period;
    }
}
