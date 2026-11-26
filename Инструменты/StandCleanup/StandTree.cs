using System.Diagnostics;

namespace StandCleanup;

internal sealed record RecordedEntry(string RelativePath, EntryIdentity Identity, string? Commit);

internal static class StandTree
{
    private const StringComparison WindowsPath = StringComparison.OrdinalIgnoreCase;

    public static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':')
            throw new ArgumentException("Укажите полный путь на локальном диске Windows.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var part in full[3..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            ValidateName(part);
        return full;
    }

    public static bool IsWithin(string parent, string path) =>
        path.Equals(parent, WindowsPath) || path.StartsWith(Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar, WindowsPath);

    public static List<RecordedEntry>? Read(string folder, IReadOnlyDictionary<string, RecordedEntry>? approved = null)
    {
        using var ancestors = DirectoryPin.Open(Path.GetDirectoryName(folder)!);
        if (ancestors is null) return null;
        using var root = WindowsEntry.Open(folder, FileShare.ReadWrite);
        if (root is null) return null;
        if (!root.Identity.Directory) throw new IOException($"На месте стенда появился файл — {folder}");

        var entries = new List<RecordedEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(folder, "", root.Identity);
        return entries;

        void Visit(string path, string relative, EntryIdentity identity)
        {
            if (!seen.Add(relative)) throw new IOException($"Совпадают имена путей — {path}");
            var saved = approved is null ? null : CheckApproved(approved, path, relative, identity);
            string? commit = null;
            if (Path.GetFileName(path).Equals(".git", WindowsPath))
            {
                if (identity.Directory) throw new IOException($"В стенде находится самостоятельный репозиторий — {path}");
                commit = CheckWorktree(Path.GetDirectoryName(path)!, folder, saved?.Commit);
            }
            entries.Add(new RecordedEntry(relative, identity, commit));
            if (!identity.Directory) return;

            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                var childRelative = ChildRelative(relative, child);
                using var file = WindowsEntry.Open(child, FileShare.ReadWrite)
                    ?? throw new IOException($"Содержимое стенда меняется — {child}");
                Visit(child, childRelative, file.Identity);
            }
        }
    }

    public static void Remove(string folder, IReadOnlyDictionary<string, RecordedEntry> approved)
    {
        using var ancestors = DirectoryPin.Open(Path.GetDirectoryName(folder)!);
        if (ancestors is null) return;
        RemoveEntry(folder, "");

        void RemoveEntry(string path, string relative)
        {
            if (!approved.TryGetValue(relative, out var saved))
                throw new IOException($"В папке появился новый элемент — {path}");

            if (saved.Identity.Directory)
            {
                using var hold = WindowsEntry.Open(path, FileShare.ReadWrite);
                if (hold is null) return;
                CheckApproved(approved, path, relative, hold.Identity);

                // Keep the worktree marker until its other entries have been removed.
                var children = Directory.GetFileSystemEntries(path)
                    .OrderBy(child => Path.GetFileName(child).Equals(".git", WindowsPath));
                foreach (var child in children) RemoveEntry(child, ChildRelative(relative, child));
            }

            // Validate and remove the same open object while its parent is pinned.
            using var entry = WindowsEntry.Open(path, FileShare.None, deleting: true);
            if (entry is null) return;
            CheckApproved(approved, path, relative, entry.Identity);
            entry.RemoveOnClose(path);
        }
    }

    private static RecordedEntry CheckApproved(IReadOnlyDictionary<string, RecordedEntry> approved, string path,
        string relative, EntryIdentity actual)
    {
        if (!approved.TryGetValue(relative, out var recorded) || !recorded.Identity.Matches(actual))
            throw new IOException($"Новый или изменившийся элемент — {path}");
        return recorded;
    }

    private static string ChildRelative(string parent, string child)
    {
        var name = Path.GetFileName(child);
        ValidateName(name);
        return parent.Length == 0 ? name : Path.Combine(parent, name);
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Путь содержит неоднозначное имя — {name}");
    }

    private static string CheckWorktree(string worktree, string stand, string? approvedCommit)
    {
        var locations = Git(worktree, "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (locations.Length != 3 || !Absolute(locations[0]).Equals(worktree, WindowsPath))
            throw new IOException($"Не удалось подтвердить рабочую копию Git — {worktree}");
        var admin = Absolute(locations[1]);
        var shared = Absolute(locations[2]);
        if (IsWithin(stand, shared) || !string.Equals(Path.GetDirectoryName(admin), Path.Combine(shared, "worktrees"), WindowsPath))
            throw new IOException($"Рабочая копия Git использует собственное хранилище — {worktree}");

        var commit = Git(worktree, "rev-parse", "HEAD").Trim();
        if (approvedCommit is not null && approvedCommit != commit)
            throw new IOException($"Коммит рабочей копии изменился — {worktree}");
        var tracked = Git(worktree, "ls-files", "-v", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (tracked.Any(file => !file.StartsWith("H ", StringComparison.Ordinal)))
            throw new IOException($"В рабочей копии Git есть скрытые от обычной проверки файлы — {worktree}");
        var changes = Git(worktree, "status", "--porcelain=v1", "-z", "-uno", "--ignore-submodules=none")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (changes.Any(change => approvedCommit is null || !change.StartsWith(" D ", StringComparison.Ordinal)))
            throw new IOException($"В рабочей копии Git есть локальные правки — {worktree}");
        if (string.IsNullOrWhiteSpace(Git(worktree, "for-each-ref", "--count=1", "--format=%(refname)",
                $"--contains={commit}", "refs/remotes")))
            throw new IOException($"Коммит рабочей копии отсутствует в известных удалённых ветках — {worktree}");
        return commit;
    }

    private static string Git(string worktree, params string[] arguments)
    {
        var start = new ProcessStartInfo("git.exe")
        {
            WorkingDirectory = worktree,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" })
            start.Environment.Remove(variable);
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Git для сверки стенда.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            throw new IOException($"Git не завершил проверку — {worktree}");
        }
        if (process.ExitCode != 0) throw new IOException($"Git не подтвердил рабочую копию — {worktree}\n{stderr.GetAwaiter().GetResult().Trim()}");
        return stdout.GetAwaiter().GetResult();
    }
}
