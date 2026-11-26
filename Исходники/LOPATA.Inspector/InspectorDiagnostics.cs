using System.Globalization;

namespace Lopata.Inspector;

public static class InspectorDiagnostics
{
    public static InspectorPreflightStatus CheckVideoMemory(string? device, long? needed, long? free)
    {
        if (device is "cpu" or "remote") return InspectorPreflightStatus.NotApplicable;
        if (string.IsNullOrWhiteSpace(device) || device == "auto" || needed is null or < 0 || free is null or < 0)
            return InspectorPreflightStatus.Unknown;
        return needed > free ? InspectorPreflightStatus.Insufficient : InspectorPreflightStatus.Sufficient;
    }

    public static string VideoMemoryWarning(long needed, long free, bool russian)
    {
        var culture = CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US");
        string Format(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", culture);
        return russian
            ? $"Для этой попытки нужно около {Format(needed)} ГБ свободной видеопамяти. Сейчас свободно {Format(free)} ГБ."
            : $"This attempt needs about {Format(needed)} GB of free graphics memory. {Format(free)} GB is available now.";
    }

    public static bool IsFailure(InspectorOutcome outcome) => outcome is InspectorOutcome.Failure
        or InspectorOutcome.VideoMemoryAdmissionDenied or InspectorOutcome.OutOfVideoMemory or InspectorOutcome.OutOfMemory
        or InspectorOutcome.DriverFailure or InspectorOutcome.WorkerFailure or InspectorOutcome.Disconnected or InspectorOutcome.SaveFailure;

    public static string? FailureExplanation(InspectorOutcome outcome, bool russian) => (outcome, russian) switch
    {
        (InspectorOutcome.VideoMemoryAdmissionDenied, true) => "Движок остановился перед обработкой. Проверка показала, что свободной видеопамяти недостаточно.",
        (InspectorOutcome.VideoMemoryAdmissionDenied, false) => "The engine stopped before processing because its check found insufficient free graphics memory.",
        (InspectorOutcome.OutOfVideoMemory, true) => "Во время обработки движку не удалось выделить видеопамять.",
        (InspectorOutcome.OutOfVideoMemory, false) => "The engine could not allocate graphics memory during processing.",
        (InspectorOutcome.OutOfMemory, true) => "Для этой попытки не хватило оперативной памяти.",
        (InspectorOutcome.OutOfMemory, false) => "The attempt ran out of system memory.",
        (InspectorOutcome.DriverFailure, true) => "Драйвер видеокарты сообщил о сбое.",
        (InspectorOutcome.DriverFailure, false) => "The graphics driver reported a failure.",
        (InspectorOutcome.WorkerFailure, true) => "Движок аварийно завершил работу. Подробности есть в журнале.",
        (InspectorOutcome.WorkerFailure, false) => "The engine crashed. See the log for details.",
        (InspectorOutcome.Disconnected, true) => "С движком или удалённым сервисом прервалась связь.",
        (InspectorOutcome.Disconnected, false) => "The connection to the engine or remote service was lost.",
        (InspectorOutcome.SaveFailure, true) => "Не удалось записать результат. Проверьте доступ и свободное место в папке сохранения.",
        (InspectorOutcome.SaveFailure, false) => "The result could not be written. Check access and free space in the output folder.",
        (InspectorOutcome.Failure, true) => "Задача завершилась с ошибкой. Точную причину определить не удалось. Подробности есть в журнале.",
        (InspectorOutcome.Failure, false) => "The task failed. Its exact cause could not be determined. See the log for details.",
        (InspectorOutcome.Cancelled, true) => "Попытка отменена.",
        (InspectorOutcome.Cancelled, false) => "The attempt was cancelled.",
        _ => null
    };
}
