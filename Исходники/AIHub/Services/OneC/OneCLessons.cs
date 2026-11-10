namespace AIHub.Services.OneC;

internal sealed record OneCLesson(string Id, string Key, string Attempt, string Solution);

/// <summary>Reviewed text exercises, not claimed platform executions. Model guidance uses the ordinary task pipeline.</summary>
internal static class OneCLessons
{
    internal static IReadOnlyList<OneCLesson> All { get; } =
    [
        new("sum", "LessonSum", "Функция Сумма(Первое, Второе) Экспорт\n    // Верните сумму двух аргументов.\nКонецФункции\n",
            "Функция Сумма(Первое, Второе) Экспорт\n    Возврат Первое + Второе;\nКонецФункции\n"),
        new("string", "LessonString", "Функция Приветствие(Имя) Экспорт\n    // Верните приветствие с именем.\nКонецФункции\n",
            "Функция Приветствие(Имя) Экспорт\n    Возврат \"Привет, \" + Имя + \"!\";\nКонецФункции\n"),
        new("array", "LessonArray", "Функция СуммаМассива(Числа) Экспорт\n    // Сложите элементы массива.\nКонецФункции\n",
            "Функция СуммаМассива(Числа) Экспорт\n    Итог = 0;\n    Для Каждого Число Из Числа Цикл\n        Итог = Итог + Число;\n    КонецЦикла;\n    Возврат Итог;\nКонецФункции\n"),
        new("date", "LessonDate", "Функция ПервыйДеньМесяца(ЗначениеДаты) Экспорт\n    // Используйте штатную функцию работы с датой.\nКонецФункции\n",
            "Функция ПервыйДеньМесяца(ЗначениеДаты) Экспорт\n    Возврат НачалоМесяца(ЗначениеДаты);\nКонецФункции\n")
    ];
}
