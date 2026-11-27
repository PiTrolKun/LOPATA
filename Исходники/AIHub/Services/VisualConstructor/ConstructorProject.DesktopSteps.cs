using System.IO;

namespace AIHub.Services.VisualConstructor;

internal sealed partial class ConstructorProject
{
    private static string DesktopStep(ConstructorStepKind kind, Func<int, string> args, Func<int, string> resolved)
    {
        const string requiresNormalRun = """
            if (System.Environment.GetEnvironmentVariable("LOPATA_CONSTRUCTOR_ISOLATED") == "1")
                        throw new System.InvalidOperationException(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
                            ? "Эта команда доступна при обычном запуске программы." : "This action is available when running normally.");
            """;
        string Text(int index) => $"System.Convert.ToString({resolved(index)}, System.Globalization.CultureInfo.CurrentCulture) ?? \"\"";
        return kind switch
        {
            ConstructorStepKind.OpenWebPage => $$"""
                {{requiresNormalRun}}
                        var address = {{Text(0)}};
                        if (!System.Uri.TryCreate(address, System.UriKind.Absolute, out var url)
                            || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
                            throw new System.FormatException(address);
                        using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                """,
            ConstructorStepKind.StartProgram => $$"""
                {{requiresNormalRun}}
                        var executable = {{Text(0)}};
                        if (!System.IO.Path.IsPathFullyQualified(executable) || !System.IO.File.Exists(executable)
                            || !System.IO.Path.GetExtension(executable).Equals(".exe", System.StringComparison.OrdinalIgnoreCase))
                            throw new System.IO.FileNotFoundException(executable);
                        var start = new System.Diagnostics.ProcessStartInfo(executable)
                        {
                            UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(executable)!
                        };
                        foreach (var argument in {{args(1)}}.Replace("\r\n", "\n", System.StringComparison.Ordinal).Split('\n'))
                            if (argument.Length != 0) start.ArgumentList.Add(argument);
                        using var process = System.Diagnostics.Process.Start(start);
                """,
            ConstructorStepKind.IsProcessRunning => $$"""
                var name = System.IO.Path.GetFileNameWithoutExtension({{Text(0)}});
                        var processes = System.Diagnostics.Process.GetProcessesByName(name);
                        try { ctx.Set({{args(1)}}, processes.Length != 0); }
                        finally { foreach (var process in processes) process.Dispose(); }
                """,
            ConstructorStepKind.Wait => $$"""
                var text = {{Text(0)}};
                        var seconds = double.Parse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture);
                        return System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(seconds));
                """,
            _ => throw new InvalidDataException("VisualConstructor.InvalidStep")
        };
    }
}
