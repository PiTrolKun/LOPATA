using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIHub.Services.VoiceNotifications;

internal static class VoiceNotificationContext
{
    private sealed class Identity { internal string Id { get; } = "surface:" + Guid.NewGuid().ToString("N"); }
    private sealed record Binding(Func<string?> Project);
    private static readonly ConditionalWeakTable<object, Identity> Ids = new();
    private static readonly ConditionalWeakTable<DependencyObject, Binding> Bindings = new();
    internal static string Surface(object owner) => Ids.GetValue(owner, _ => new()).Id;
    internal static string[] Project(string? value) => string.IsNullOrWhiteSpace(value) ? [] : ["project:" + value.Trim().Replace('\\', '/').ToLowerInvariant()];
    internal static string[] File(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        try { return ["file:" + Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant(), .. Project(path)]; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return []; }
    }
    internal static void Bind(DependencyObject owner, Func<string?> project)
    { Bindings.Remove(owner); Bindings.Add(owner, new(project)); }
    internal static string[] Element(DependencyObject? element)
    {
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DependencyObject? surface = null;
        for (var current = element; current is not null; current = Parent(current))
        {
            if (Bindings.TryGetValue(current, out var binding)) foreach (var item in Project(binding.Project())) contexts.Add(item);
            if (current is System.Windows.Controls.UserControl) surface = current;
            if (current is Window window) { if (surface is null && window is not MainWindow) surface = window; break; }
        }
        if (surface is not null) contexts.Add(Surface(surface));
        return contexts.ToArray();
    }
    private static DependencyObject? Parent(DependencyObject value) => value is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(value) ?? LogicalTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
}
