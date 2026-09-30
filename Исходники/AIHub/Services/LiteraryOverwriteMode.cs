using System.Reflection;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using TextBox = System.Windows.Controls.TextBox;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace AIHub.Services;

/// <summary>WPF has no public overtype-state property. Read defensively; never write its internals.</summary>
public static class LiteraryOverwriteMode
{
    private const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly PropertyInfo? Editor = typeof(TextBoxBase).GetProperty("TextEditor", HiddenInstance);
    private static readonly PropertyInfo? Mode = typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditor")?
        .GetProperty("_OvertypeMode", HiddenInstance);

    public static bool? Read(TextBox input)
    {
        if (Editor is null || Mode?.PropertyType != typeof(bool)) return null;
        try { return Editor.GetValue(input) is { } editor ? (bool?)Mode.GetValue(editor) : null; }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    public static bool Disable(TextBox input)
    {
        if (Read(input) == false) return true;
        if (Read(input) != true || input.IsReadOnly || !input.IsEnabled) return false;
        EditingCommands.ToggleInsert.Execute(null, input);
        return Read(input) == false;
    }
}
