using System.Text.Json;

namespace StandCleanup;

internal sealed record CleanupItem(Guid Id, string Folder, string Task, string[] Evidence, DateTimeOffset Added,
    long Bytes, int FileCount, List<RecordedEntry> Entries, DateTimeOffset? Completed = null, string? Message = null);

internal sealed record CleanupResult(bool Completed, string Message);

internal sealed record CleanupPlan(bool Ready, long Bytes, int FileCount, string Message);

internal sealed record QueueDocument(int Version, List<CleanupItem> Items);

internal sealed class CleanupQueue(string workspace)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly string temporaryRoot = Path.Combine(workspace, "_tmp");
    private readonly string storage = Path.Combine(workspace, "_backups", "StandCleanup");

    public static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Инструкции", "AGENTS.md")))
                return StandTree.Absolute(directory.FullName);
        throw new IOException("Расположите утилиту внутри папки проекта ЛОПАТА.");
    }

    public CleanupItem Add(string folder, string task, string[] proof)
    {
        folder = ValidateFolder(folder);
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("Укажите название завершённой задачи.");
        var evidence = proof.Select(StandTree.Absolute).Distinct(Paths).ToArray();
        return UseQueue(items =>
        {
            var existing = items.FirstOrDefault(item => item.Completed is null && Paths.Equals(item.Folder, folder));
            if (existing is not null)
            {
                if (!FolderMissing(folder)) return existing;
                Complete(items, existing, "Папка удалена вручную.");
                Save(items);
            }
            if (items.Any(item => item.Completed is null && (StandTree.IsWithin(item.Folder, folder)
                    || StandTree.IsWithin(folder, item.Folder))))
                throw new IOException("Этот путь пересекается с другой папкой в очереди.");

            var entries = WithEvidence(evidence, () => StandTree.Read(folder))
                ?? throw new DirectoryNotFoundException($"Папка стенда отсутствует — {folder}");
            var item = new CleanupItem(Guid.NewGuid(), folder, task.Trim(), evidence, DateTimeOffset.UtcNow,
                entries.Sum(entry => entry.Identity.Bytes), entries.Count(entry => !entry.Identity.Directory), entries);
            items.Add(item);
            Save(items);
            return item;
        });
    }

    public CleanupItem[] List(bool all) => UseQueue(items =>
    {
        var updated = false;
        foreach (var item in items.Where(item => item.Completed is null).ToArray())
        {
            try
            {
                if (!FolderMissing(ValidateFolder(item.Folder))) continue;
                Complete(items, item, "Папка удалена вручную.");
                updated = true;
            }
            catch (IOException)
            {
                // A busy or replaced path still needs attention.
            }
        }
        if (updated) Save(items);
        return items.Where(item => all || item.Completed is null).ToArray();
    });

    public CleanupItem Get(Guid id, string folder) => UseQueue(items => Select(items, id, ValidateFolder(folder)));

    public CleanupPlan Preview(Guid id, string folder)
    {
        folder = ValidateFolder(folder);
        return UseQueue(items =>
        {
            var item = Select(items, id, folder);
            if (item.Completed is not null || FolderMissing(folder))
                return new CleanupPlan(false, 0, 0, "Папка уже удалена.");
            try
            {
                var recorded = item.Entries.ToDictionary(entry => entry.RelativePath, Paths);
                var remaining = WithEvidence(item.Evidence, () => StandTree.Read(folder, recorded));
                return remaining is null
                    ? new CleanupPlan(false, 0, 0, "Папка уже удалена.")
                    : new CleanupPlan(true, remaining.Sum(entry => entry.Identity.Bytes),
                        remaining.Count(entry => !entry.Identity.Directory), "Состав сверён.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                return new CleanupPlan(false, 0, 0, error.Message);
            }
        });
    }

    public CleanupResult Clean(Guid id, string folder)
    {
        folder = ValidateFolder(folder);
        return UseQueue(items =>
        {
            var item = Select(items, id, folder);
            if (item.Completed is not null) return new CleanupResult(true, "Запись уже закрыта.");

            try
            {
                if (FolderMissing(folder))
                {
                    Complete(items, item, "Папка удалена вручную.");
                    Save(items);
                    return new CleanupResult(true, "Папка уже удалена.");
                }

                var recorded = item.Entries.ToDictionary(entry => entry.RelativePath, Paths);
                if (!recorded.TryGetValue("", out var root) || !root.Identity.Directory)
                    throw new InvalidDataException("В очереди отсутствует проверенный состав стенда.");
                WithEvidence(item.Evidence, () =>
                {
                    StandTree.Read(folder, recorded);
                    items[items.IndexOf(item)] = item with { Message = "Очистка начата. При прерывании можно повторить." };
                    Save(items);
                    StandTree.Remove(folder, recorded);
                    if (!FolderMissing(folder)) throw new IOException("Папка ещё существует.");
                    return true;
                });
                Complete(items, item, "Удаление проверено.");
                Save(items);
                return new CleanupResult(true, "Папка удалена.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                var index = items.FindIndex(current => current.Id == id);
                items[index] = item with { Message = error.Message };
                Save(items);
                return new CleanupResult(false, error.Message);
            }
        });
    }

    private string ValidateFolder(string path)
    {
        var full = StandTree.Absolute(path);
        if (Paths.Equals(temporaryRoot, full) || !StandTree.IsWithin(temporaryRoot, full))
            throw new ArgumentException($"Выберите отдельную временную папку внутри {temporaryRoot}.");
        var rooted = Path.Combine(temporaryRoot, full[(temporaryRoot.Length + 1)..]);
        if (!full.Equals(rooted, StringComparison.Ordinal))
            throw new ArgumentException($"Используйте точное имя корневой папки — {temporaryRoot}");
        return full;
    }

    private static bool FolderMissing(string path)
    {
        using var pin = DirectoryPin.Open(Path.GetDirectoryName(path)!);
        if (pin is null) return true;
        using var entry = WindowsEntry.Open(path, FileShare.ReadWrite);
        return entry is null;
    }

    private T WithEvidence<T>(string[] proofs, Func<T> action)
    {
        if (proofs.Length == 0) throw new ArgumentException("Укажите хотя бы один сохранённый файл проверки или результата.");
        var handles = new List<IDisposable>();
        try
        {
            foreach (var proof in proofs)
            {
                var path = StandTree.Absolute(proof);
                if (StandTree.IsWithin(temporaryRoot, path))
                    throw new ArgumentException($"Сохраните результаты или протокол за пределами _tmp — {path}");
                var pin = DirectoryPin.Open(Path.GetDirectoryName(path)!)
                    ?? throw new FileNotFoundException($"Папка с сохранённым результатом отсутствует — {path}");
                handles.Add(pin);
                var file = WindowsEntry.Open(path, FileShare.Read)
                    ?? throw new FileNotFoundException($"Сохранённый результат отсутствует — {path}");
                handles.Add(file);
                if (file.Identity.Directory) throw new ArgumentException($"Укажите файл результата или протокола — {path}");
            }
            return action();
        }
        finally
        {
            for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        }
    }

    private static CleanupItem Select(List<CleanupItem> items, Guid id, string folder)
    {
        var item = items.SingleOrDefault(entry => entry.Id == id);
        if (item is null || !Paths.Equals(item.Folder, folder))
            throw new ArgumentException("Номер записи и полный путь должны совпадать с очередью.");
        return item;
    }

    private static void Complete(List<CleanupItem> items, CleanupItem item, string message)
    {
        var index = items.FindIndex(current => current.Id == item.Id);
        items[index] = item with { Completed = DateTimeOffset.UtcNow, Entries = [], Message = message };
    }

    private T UseQueue<T>(Func<List<CleanupItem>, T> work)
    {
        using var workspacePin = DirectoryPin.Open(workspace) ?? throw new DirectoryNotFoundException(workspace);
        var backups = Path.GetDirectoryName(storage)!;
        using (var stateParent = WindowsEntry.Open(backups, FileShare.ReadWrite))
        {
            if (stateParent is null) Directory.CreateDirectory(backups);
            else if (!stateParent.Identity.Directory) throw new IOException($"Ожидалась папка резервов — {backups}");
        }
        using var backupPin = DirectoryPin.Open(backups) ?? throw new DirectoryNotFoundException(backups);
        using (var stateFolder = WindowsEntry.Open(storage, FileShare.ReadWrite))
        {
            if (stateFolder is null) Directory.CreateDirectory(storage);
            else if (!stateFolder.Identity.Directory) throw new IOException($"Ожидалась папка очереди — {storage}");
        }
        using var statePin = DirectoryPin.Open(storage) ?? throw new DirectoryNotFoundException(storage);
        var lockPath = Path.Combine(storage, "queue.lock");
        using var existing = WindowsEntry.Open(lockPath, FileShare.ReadWrite);
        using var gate = OpenQueueLock(lockPath);
        var path = Path.Combine(storage, "queue.json");
        List<CleanupItem> items;
        using (var present = WindowsEntry.Open(path, FileShare.ReadWrite))
        {
            if (present is null) items = [];
            else
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize<QueueDocument>(stream, Json) is not { Version: 1, Items: not null } data)
                    throw new InvalidDataException("Формат очереди очистки не поддерживается.");
                items = data.Items;
            }
        }
        return work(items);
    }

    private static FileStream OpenQueueLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new IOException("Очистка стендов уже выполняется в другом окне.", error); }
    }

    private void Save(List<CleanupItem> items)
    {
        var path = Path.Combine(storage, "queue.json");
        var temporary = Path.Combine(storage, $"queue-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new QueueDocument(1, items), Json);
                stream.Flush(true);
            }
            using (var check = WindowsEntry.Open(path, FileShare.ReadWrite))
            {
                if (check?.Identity.Directory == true) throw new IOException("На месте очереди появилась папка.");
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
