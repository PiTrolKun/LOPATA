using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIHub.Services.OfficeReader;

internal static class ReaderProcess
{
    internal static Process Start(string executable, IEnumerable<string> arguments, bool input = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // The embedded Python runtime is part of the signed-inventory office tree.
        // Importing a reader helper must not rewrite the distributed bytecode.
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        if (Path.GetFileName(executable).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add("-B");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return OwnedProcessRegistry.Shared.Start(start, "Office.Reader");
    }

    internal static async Task RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        using var process = Start(executable, arguments);
        using var cancel = token.Register(() => Kill(process));
        var output = DrainAsync(process.StandardOutput); var error = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                OwnedProcessRegistry.Log("reader_process_failed", "Office.Reader", process.Id,
                    Path.GetFileName(executable) + "; exit=" + process.ExitCode + "; " + await error + await output);
                throw new InvalidDataException("Reader.RenderFailed");
            }
        }
        finally { Kill(process); await process.WaitForExitAsync(); await Task.WhenAll(output, error); }
    }

    internal static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int length;
        while ((length = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(length, 8192 - text.Length));
        return text.ToString();
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
