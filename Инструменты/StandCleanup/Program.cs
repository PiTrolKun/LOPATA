using System.Text;
using System.Text.Json;

namespace StandCleanup;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        var interactive = args.Length == 0 || args is ["interactive"];
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Утилита работает в Windows.");
            if (args is ["--help"] or ["help"])
            {
                PrintHelp();
                return 0;
            }

            var queue = new CleanupQueue(CleanupQueue.FindWorkspace());
            if (interactive) return RunInteractive(queue);
            switch (args)
            {
                case ["add", var folder, var task, .. var files] when files.Length > 0:
                    Display(queue.Add(folder, task, files));
                    return 0;
                case ["list", .. var options] when options.All(option => option is "--all" or "--json"):
                    var items = queue.List(options.Contains("--all"));
                    if (options.Contains("--json"))
                        Console.WriteLine(JsonSerializer.Serialize(items.Select(item => new
                        {
                            id = item.Id, folder = item.Folder, task = item.Task, bytes = item.Bytes, files = item.FileCount,
                            evidence = item.Evidence, added = item.Added, completed = item.Completed, message = item.Message,
                        })));
                    else DisplayList(items);
                    return 0;
                case ["clean", var id, var folder, .. var flags] when Guid.TryParse(id, out var number)
                    && (flags.Length == 0 || flags is ["--apply"]):
                    var item = queue.Get(number, folder);
                    Display(item);
                    if (flags.Length == 0)
                    {
                        var plan = queue.Preview(item.Id, item.Folder);
                        ShowPlan(plan);
                        return plan.Ready || item.Completed is not null ? 0 : 2;
                    }
                    return Apply(queue, item) ? 0 : 2;
                default:
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.ComponentModel.Win32Exception or JsonException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            if (interactive && !Console.IsInputRedirected)
            {
                Console.WriteLine("Нажмите Enter, чтобы закрыть окно.");
                Console.ReadLine();
            }
        }
    }

    private static int RunInteractive(CleanupQueue queue)
    {
        if (!Console.IsOutputRedirected) Console.Title = "Очистка стендов";
        Console.WriteLine("Очистка стендов\n");
        var items = queue.List(all: false);
        DisplayList(items);
        if (items.Length == 0) return 0;

        Console.WriteLine("Введите номера через пробел или «все». Пустая строка закрывает список.");
        var selection = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(selection)) return 0;
        var selected = selection.Equals("все", StringComparison.OrdinalIgnoreCase) ? items : SelectItems(items, selection);
        var ready = new List<CleanupItem>();
        foreach (var item in selected)
        {
            Console.WriteLine($"\n{item.Folder}");
            var plan = queue.Preview(item.Id, item.Folder);
            ShowPlan(plan);
            if (plan.Ready) ready.Add(item);
        }
        if (ready.Count == 0) return 2;

        Console.WriteLine("\nВведите «да», чтобы удалить папки, прошедшие проверку.");
        if (!string.Equals(Console.ReadLine()?.Trim(), "да", StringComparison.OrdinalIgnoreCase)) return 0;
        var completed = 0;
        foreach (var item in ready)
        {
            Console.WriteLine($"\n{item.Folder}");
            if (Apply(queue, item)) completed++;
        }
        Console.WriteLine($"\nУдалено папок — {completed} из {selected.Length}.");
        return completed == selected.Length ? 0 : 2;
    }

    private static CleanupItem[] SelectItems(CleanupItem[] items, string selection)
    {
        var numbers = new HashSet<int>();
        foreach (var part in selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var number) || number < 1 || number > items.Length)
                throw new ArgumentException($"Такого номера в списке нет — {part}");
            numbers.Add(number);
        }
        return numbers.Order().Select(number => items[number - 1]).ToArray();
    }

    private static bool Apply(CleanupQueue queue, CleanupItem item)
    {
        var result = queue.Clean(item.Id, item.Folder);
        Console.WriteLine(result.Message);
        if (!result.Completed) Console.WriteLine("Папка осталась в очереди.");
        return result.Completed;
    }

    private static void ShowPlan(CleanupPlan plan)
    {
        Console.WriteLine(plan.Ready
            ? $"Готово к удалению — {plan.FileCount:N0} файлов · {Size(plan.Bytes)}"
            : plan.Message);
    }

    private static void DisplayList(CleanupItem[] items)
    {
        if (items.Length == 0)
        {
            Console.WriteLine("Очередь пуста.");
            return;
        }
        var pending = items.Where(item => item.Completed is null).ToArray();
        Console.WriteLine($"Ожидают очистки — {pending.Length} · объём по списку {Size(pending.Sum(item => item.Bytes))}\n");
        for (var index = 0; index < items.Length; index++)
        {
            Console.Write($"{index + 1}. ");
            Display(items[index]);
            Console.WriteLine();
        }
    }

    private static void Display(CleanupItem item)
    {
        var status = item.Completed is not null ? "Закрыта" : item.Message is null ? "В очереди" : "Требует внимания";
        Console.WriteLine($"{item.Task} · {Size(item.Bytes)} · {status}");
        Console.WriteLine($"   {item.Folder}");
        Console.WriteLine($"   Запись {item.Id:N}");
        foreach (var evidence in item.Evidence) Console.WriteLine($"   Сохранено — {evidence}");
        if (item.Message is not null) Console.WriteLine($"   {item.Message}");
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} ГиБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N2} МиБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N2} КиБ",
        _ => $"{bytes:N0} Б",
    };

    private static void PrintHelp() => Console.WriteLine("""
        Очистка стендов

        StandCleanup interactive
            Открыть список и выбрать папки.
        StandCleanup add "<папка>" "<задача>" "<файл протокола>" [другие файлы]
            Записать проверенную временную папку.
        StandCleanup list [--all] [--json]
            Показать очередь. --all добавляет закрытые записи.
        StandCleanup clean <номер записи> "<полный путь>" [--apply]
            Проверить папку и показать план. --apply выполняет удаление.
        """);
}
