namespace AIHub.Services.OneC;

internal sealed record OneCKnowledgeItem(string Id, string Version, string Text);
internal interface IOneCKnowledgeProvider { IReadOnlyList<OneCKnowledgeItem> Read(); }

/// <summary>Small versioned syntax reference. Contains no assumed customer configuration metadata.</summary>
internal sealed class OneCLanguageReference : IOneCKnowledgeProvider
{
    public IReadOnlyList<OneCKnowledgeItem> Read() => [new("lopata.bsl.syntax", "1", Syntax)];
    internal const string Syntax = "BSL syntax reference (text examples, not execution results): "
        + "Keywords are indivisible: КонецФункции, КонецПроцедуры, КонецЕсли, КонецЦикла. "
        + "Never write 'Конец Функции' or 'Конец Процедуры'. Terminate statements with semicolons. "
        + "A standalone exported function: Функция Сложить(А, Б) Экспорт\n    Возврат А + Б;\nКонецФункции\n"
        + "A conditional: Если А > Б Тогда\n    Возврат А;\nИначе\n    Возврат Б;\nКонецЕсли;\n"
        + "An array loop: Для Каждого Элемент Из Массив Цикл\n    Итог = Итог + Элемент;\nКонецЦикла;\n"
        + "Do not invent helper functions or configuration objects. State essential missing metadata explicitly. ";
}
