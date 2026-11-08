using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AIHub.Controls.OfficeReader;

/// <summary>One document per window, shared by shell, file picker, and internal viewing.</summary>
internal sealed class ReaderWindowGroup(Window? owner, Func<string, string> localize, bool dark)
{
    private static readonly ConditionalWeakTable<Window, ReaderWindowGroup> Groups = new();
    private readonly List<ReaderWindow> _windows = [];
    internal static ReaderWindowGroup For(Window? owner, Func<string, string> localize, bool dark)
        => owner is null ? new(null, localize, dark) : Groups.GetValue(owner, w => new(w, localize, dark));

    internal void Attach(ReaderWindow window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
    }

    internal ReaderWindow Open(string? path = null, ReaderWindow? origin = null)
    {
        path = path is null ? null : Path.GetFullPath(path);
        if (path is not null && string.Equals(Path.GetExtension(path), Services.OfficeEditor.OfficePackage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            if (origin is { CanOpenReaderFile: true } && !_windows.Any(w => string.Equals(w.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                _ = origin.OpenAsync(path);
                return origin;
            }
            return OpenEditor(path);
        }
        var window = path is null ? _windows.LastOrDefault(w => !w.IsEditing)
            : _windows.FirstOrDefault(w => string.Equals(w.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        window ??= origin is { CanOpenReaderFile: true } ? origin : _windows.FirstOrDefault(w => w.CanOpenReaderFile);
        if (window is null)
        {
            window = new ReaderWindow(owner, localize, origin?.IsDarkTheme ?? dark, path, this);
            window.Show();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            if (path is not null && window.SourcePath is null) _ = window.OpenAsync(path);
            window.Activate();
        }
        return window;
    }

    internal void OpenFiles(IEnumerable<string> paths, ReaderWindow origin)
    {
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) Open(path, origin);
    }

    internal ReaderWindow OpenEditor(string? path = null, string? recoveryId = null)
    {
        path = path is null ? null : Path.GetFullPath(path);
        if (path is not null && recoveryId is null &&
            _windows.FirstOrDefault(w => string.Equals(w.SourcePath, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            _ = existing.SwitchToEditingAsync();
            return existing;
        }
        var window = new ReaderWindow(owner, localize, dark, path, this, startEditing: true, recoveryId: recoveryId);
        window.Show();
        return window;
    }

    internal void ApplyTheme(bool value)
    {
        dark = value;
        foreach (var window in _windows.ToArray()) window.ApplyTheme(value);
    }
}
