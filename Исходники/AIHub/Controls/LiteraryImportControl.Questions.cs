using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class LiteraryImportControl
{
    private static readonly string[] PreparationQuestionKeys =
    ["3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "13.2", "14",
     "17", "21", "22", "25", "28", "29", "30", "31", "32", "33", "34"];

    private StackPanel _questionHost = new();
    private int _questionPosition;

    private bool HasOriginalSource()
    {
        var answer = _preparationAnswers?.Values.GetValueOrDefault("9", "").Trim() ?? "";
        return answer.Length > 0 && !answer.StartsWith("нет", StringComparison.OrdinalIgnoreCase)
            && !answer.StartsWith("no", StringComparison.OrdinalIgnoreCase)
            && !answer.Equals(_l("Literary.Interview.Basis.original"), StringComparison.OrdinalIgnoreCase);
    }

    private string[] VisibleQuestionKeys() => HasOriginalSource()
        ? PreparationQuestionKeys
        : PreparationQuestionKeys.Where(key => key is not ("10" or "11")).ToArray();

    private void RestoreQuestionPosition()
    {
        var saved = _preparationAnswers?.Values.GetValueOrDefault("ui.current-question", "") ?? "";
        var keys = VisibleQuestionKeys();
        var index = Array.IndexOf(keys, saved);
        if (saved == "end") _questionPosition = keys.Length;
        else if (index >= 0) _questionPosition = index;
        else _questionPosition = Math.Clamp(_questionPosition, 0, keys.Length);
    }

    private void MoveQuestion(int direction)
    {
        SaveAnswers();
        var keys = VisibleQuestionKeys();
        if (direction > 0 && _questionPosition < keys.Length && keys[_questionPosition] == "10"
            && _referenceFiles.Items.Count == 0)
        {
            _questionHost.Children.Add(LiteraryUi.Text(I("Выберите хотя бы один файл первоисточника.",
                "Choose at least one original-source file.")));
            return;
        }
        _questionPosition = Math.Clamp(_questionPosition + direction, 0, keys.Length);
        RenderCurrentQuestion();
    }

    private void RenderCurrentQuestion()
    {
        if (_preparationAnswers is null) return;
        _questionHost.Children.Clear();
        _answerInputs.Clear();
        var keys = VisibleQuestionKeys();
        _questionPosition = Math.Clamp(_questionPosition, 0, keys.Length);
        var key = _questionPosition == keys.Length ? "end" : keys[_questionPosition];
        _preparationAnswers.Set("ui.current-question", key);
        if (key == "end")
        {
            _questionHost.Children.Add(LiteraryUi.Text(I("Вопросы закончились. Анализ продолжается; ответы можно изменить.",
                "The questions are finished. Analysis continues; you can revise your answers."), true));
        }
        else
        {
            _questionHost.Children.Add(LiteraryUi.Text(QuestionTitle(key), true));
            if (key == "10") RenderSourceFiles();
            else RenderAnswer(key);
        }
        var navigation = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (_questionPosition > 0)
            navigation.Children.Add(LiteraryUi.Button(I("Предыдущий вопрос", "Previous question"), () => MoveQuestion(-1)));
        if (_questionPosition < keys.Length)
            navigation.Children.Add(LiteraryUi.Button(I("Следующий вопрос", "Next question"), () => MoveQuestion(1), true));
        _questionHost.Children.Add(navigation);
    }

    private string QuestionTitle(string key) => key switch
    {
        "4" => I("Напишите кратко, что уже написано в проекте. Опишите кратко, как планируете развивать рассказ дальше.",
            "Briefly describe what is written and how you plan to continue the story."),
        "9" => I("Есть ли у мира первоисточник (как в фанфиках)?", "Does the world have an original source, as in fan fiction?"),
        "10" => I("Файлы первоисточника для будущей работы (сейчас не обрабатываются)",
            "Original-source files for future work (not processed now)"),
        "13.2" => I("Время, на котором заканчивается уже написанный рассказ",
            "Time at which the written story ends"),
        "21" => I("Какие правила уже установлены в написанной части мира?",
            "Which rules are already established in the written world?"),
        "22" => I("Какие ограничения и запреты уже действуют в написанной части?",
            "Which limits already apply in the written part?"),
        _ => _l("Literary.Interview.Q" + key)
    };

    private void RenderAnswer(string key)
    {
        if (key == "3") { RenderMemoryChoice(); return; }
        if (key == "14") { RenderCultureChoice(); return; }
        var value = _preparationAnswers!.Values.GetValueOrDefault(key, "");
        var box = new TextBox
        {
            Text = key == "6" ? _preparationAnswers.Values.GetValueOrDefault("6.custom",
                _preparationAnswers.Values.ContainsKey("6.selected") ? "" : value) : value,
            AcceptsReturn = key is not ("5" or "9" or "13.2"), TextWrapping = TextWrapping.Wrap,
            MinHeight = key is "5" or "9" or "13.2" ? 36 : 82,
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 10)
        };
        box.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        LiterarySpellChecking.Enable(box, _language);
        box.TextChanged += (_, _) => { _answersTimer.Stop(); _answersTimer.Start(); };
        _answerInputs[key == "6" ? "6.custom" : key] = box;
        _questionHost.Children.Add(box);

        if (key == "5") RenderCatalogChoice(box);
        else if (key == "6") RenderGenres();
        else if (key == "9")
        {
            var buttons = new WrapPanel();
            buttons.Children.Add(LiteraryUi.Button(I("Есть первоисточник", "Has an original source"), () => box.Text =
                I("Да, есть первоисточник", "Yes, there is an original source")));
            buttons.Children.Add(LiteraryUi.Button(I("Нет первоисточника", "No original source"), () => box.Text =
                I("Нет", "No")));
            _questionHost.Children.Add(buttons);
        }
        else if (int.TryParse(key, out var number))
        {
            var options = LiteraryInterviewCatalog.Get(number).Options ?? [];
            if (options.Length == 0) return;
            var buttons = new WrapPanel();
            foreach (var option in options)
            {
                if (option == "NoSource") continue;
                var text = _l("Literary.Interview.Option." + option);
                buttons.Children.Add(LiteraryUi.Button(text, () => box.Text = text));
            }
            _questionHost.Children.Add(buttons);
        }
    }

    private void RenderMemoryChoice()
    {
        _questionHost.Children.Add(LiteraryUi.Text(I(
            "Память произведения помогает помощникам учитывать героев, мир и события при дальнейшей работе. Здесь вы выбираете модель, которая позже будет извлекать для неё факты из текста. На текущий анализ импортируемой книги выбор не влияет.",
            "Project memory helps the assistants keep characters, the world, and events in mind during later work. Here you choose which model will later extract facts from the text. This choice does not affect the current import analysis.")));
        var ids = new[] { "runeweaver", "gliner", "nuextract" };
        var selectedId = _preparationAnswers!.Values.GetValueOrDefault("3.selection", "");
        var previous = _preparationAnswers.Values.GetValueOrDefault("3", "");
        if (selectedId.Length == 0)
            selectedId = ids.FirstOrDefault(id => previous == _l("Literary.MemorySetup." + id)) ?? "";
        foreach (var id in ids)
        {
            var label = _l("Literary.MemorySetup." + id);
            var button = LiteraryUi.Button(label, () =>
            {
                _preparationAnswers.Set("3.selection", id);
                _preparationAnswers.Set("3", label);
                RenderCurrentQuestion();
            }, primary: id == selectedId);
            button.Margin = new Thickness(0, 14, 0, 2);
            button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            _questionHost.Children.Add(button);
            _questionHost.Children.Add(LiteraryUi.Text(_l("Literary.MemorySetup." + id + ".Hint")));
        }
        if (previous.Length > 0 && selectedId.Length == 0)
            _questionHost.Children.Add(LiteraryUi.Text(I("Прежний текстовый ответ сохранён: ",
                "Previous text answer is preserved: ") + previous));
    }

    private void RenderCatalogChoice(TextBox box)
    {
        var ids = LiteraryChoices.Forms;
        const string prefix = "Literary.Form.";
        var selectedId = _preparationAnswers!.Values.GetValueOrDefault("5.selection", "");
        var choice = new ComboBox { MinHeight = 36, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var id in ids) choice.Items.Add(new ComboBoxItem { Content = _l(prefix + id), Tag = id });
        choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selectedId);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is not ComboBoxItem item) return;
            box.Text = item.Content.ToString() ?? "";
            _preparationAnswers.Set("5.selection", (string)item.Tag);
        };
        _questionHost.Children.Add(choice);
    }

    private void RenderGenres()
    {
        var selected = _preparationAnswers!.Values.GetValueOrDefault("6.selected", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var choices = new WrapPanel();
        foreach (var id in LiteraryInterviewCatalog.Genres)
        {
            var box = new CheckBox { Content = _l("Literary.Interview.Genre." + id),
                ToolTip = _l("Literary.Interview.GenreHint." + id), IsChecked = selected.Contains(id),
                Margin = new Thickness(4, 6, 16, 6) };
            box.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            box.Checked += (_, _) => { selected.Add(id); SaveGenreSelection(selected); };
            box.Unchecked += (_, _) => { selected.Remove(id); SaveGenreSelection(selected); };
            choices.Children.Add(box);
        }
        _questionHost.Children.Add(choices);
    }

    private void SaveGenreSelection(HashSet<string> selected)
    {
        _preparationAnswers!.Set("6.selected", string.Join('|', selected.Order(StringComparer.Ordinal)));
        SaveAnswers();
    }

    private void SaveGenreAnswer()
    {
        if (_preparationAnswers is null) return;
        var selected = _preparationAnswers.Values.GetValueOrDefault("6.selected", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => _l("Literary.Interview.Genre." + id));
        var custom = _preparationAnswers.Values.GetValueOrDefault("6.custom", "").Trim();
        _preparationAnswers.Set("6", string.Join(", ", selected.Append(custom).Where(text => text.Length > 0)));
    }

    private void RenderSourceFiles()
    {
        _referenceFiles.Items.Clear();
        foreach (var file in _preparationAnswers!.Values.GetValueOrDefault("10", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries))
            _referenceFiles.Items.Add(file);
        _questionHost.Children.Add(_referenceFiles);
        _questionHost.Children.Add(LiteraryUi.Button(I("Добавить файлы", "Add files"), ChooseReferenceFiles));
    }
}
