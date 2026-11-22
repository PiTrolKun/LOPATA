namespace Lopata.PcInspector;

internal static class InspectorMessages
{
    internal static string Get(string code, string? program, string? stage, bool during, bool russian)
    {
        var stageName = StageName(stage, russian);
        return (russian, code, during, program) switch
        {
            (true, "ram_pressure", _, _) => "Свободной оперативной памяти может не хватить для этой задачи.",
            (true, "gpu_pressure", true, not null) => $"Генерация продолжается. {program} использует ту же видеокарту, поэтому обработка может занять больше времени.",
            (true, "gpu_pressure", false, not null) => $"{program} использует выбранную видеокарту. Закройте её или запустите задачу позже, если хотите освободить ресурсы.",
            (true, "gpu_pressure", _, _) => "На видеокарте мало свободной памяти. Генерация может занять больше времени.",
            (true, "long_stage", _, _) => $"Генерация продолжается. Текущий этап — {stageName}.",
            (true, "disconnected", _, _) => "Проверяем состояние задачи. Движок пока не ответил.",
            (true, "cpu_retry", _, _) => "Попытка на видеокарте завершилась ошибкой. ЛОПАТА повторяет задачу на процессоре.",
            (true, "out_of_video_memory", _, _) => "Движку не хватило видеопамяти для этой попытки.",
            (true, "out_of_memory", _, _) => "Движку не хватило оперативной памяти для этой попытки.",
            (true, "worker_failed", _, _) => stageName is null ? "Движок завершился с ошибкой." : $"Движок завершился с ошибкой на этапе «{stageName}».",
            (true, "task_failed", _, _) => "Задача завершилась с ошибкой.",
            (true, "save_failed", _, _) => "Задача обработана, но сохранить результат не удалось.",
            (true, "not_released", _, _) => "Пока не подтверждено освобождение ресурсов задачи.",
            (true, "report_ready", _, _) => "Отчёт инспектора готов.",
            (true, "report_core_unavailable", _, _) => "Ядро сейчас недоступно. Сводка инспектора сохранена.",
            (false, "ram_pressure", _, _) => "This task may need more available RAM.",
            (false, "gpu_pressure", true, not null) => $"Generation is still running. {program} is using the same graphics card, so processing may take longer.",
            (false, "gpu_pressure", false, not null) => $"{program} is using the selected graphics card. Free up resources by closing it or running the task later.",
            (false, "gpu_pressure", _, _) => "The graphics card is low on available memory. Generation may take longer.",
            (false, "long_stage", _, _) => $"Generation is still running. Current stage: {stageName}.",
            (false, "disconnected", _, _) => "Checking the task. The engine has not responded yet.",
            (false, "cpu_retry", _, _) => "The graphics attempt failed. LOPATA is trying again on the CPU.",
            (false, "out_of_video_memory", _, _) => "The engine ran out of graphics memory for this attempt.",
            (false, "out_of_memory", _, _) => "The engine ran out of RAM for this attempt.",
            (false, "worker_failed", _, _) => stageName is null ? "The engine stopped with an error." : $"The engine stopped with an error during {stageName}.",
            (false, "task_failed", _, _) => "The task ended with an error.",
            (false, "save_failed", _, _) => "The task was processed, but the result could not be saved.",
            (false, "not_released", _, _) => "The inspector has not yet verified release of task resources.",
            (false, "report_ready", _, _) => "The inspector report is ready.",
            (false, "report_core_unavailable", _, _) => "The Core is unavailable. The inspector summary was saved.",
            _ => russian ? "Состояние задачи обновилось." : "The task status changed."
        };
    }

    private static string? StageName(string? stage, bool russian) => (stage, russian) switch
    {
        ("generating", true) => "генерация",
        ("image_decoding", true) => "сборка изображения",
        ("model_loading", true) => "загрузка модели",
        ("output_write", true) => "сохранение результата",
        ("worker_connect", true) => "подключение к движку",
        ("preparing", true) => "подготовка",
        ("generating", false) => "generation",
        ("image_decoding", false) => "decoding the image",
        ("model_loading", false) => "model loading",
        ("output_write", false) => "saving the result",
        ("worker_connect", false) => "connecting to the engine",
        ("preparing", false) => "preparation",
        _ => null
    };
}
