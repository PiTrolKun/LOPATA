using System.Diagnostics;
using System.IO;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed class LlamaCliRuntimeService
{
    public string ExpectedExecutablePath { get; } = LlamaBackendPaths.CliExecutablePath;

    public bool IsAvailable => File.Exists(ExpectedExecutablePath);

    private readonly CoreIdentityService _coreIdentityService;

    public LlamaCliRuntimeService(UserContextService userContextService)
    {
        _coreIdentityService = new CoreIdentityService(userContextService);
    }

    public async Task<string> GenerateAsync(
        DebugModelInfo model,
        IReadOnlyList<DebugChatMessage> history,
        string userMessage,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        if (!IsAvailable)
        {
            throw new FileNotFoundException("llama-cli.exe was not found.", ExpectedExecutablePath);
        }

        var promptPath = CreatePromptFile(model, history, userMessage);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExpectedExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(ExpectedExecutablePath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            AddArgument(startInfo, "-m");
            AddArgument(startInfo, model.Path);
            AddArgument(startInfo, "--file");
            AddArgument(startInfo, promptPath);
            AddArgument(startInfo, "--predict");
            AddArgument(startInfo, "-1");
            AddArgument(startInfo, "--ctx-size");
            AddArgument(startInfo, CoreContextRuntimeLimits.CurrentBackendContextLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddArgument(startInfo, "--n-gpu-layers");
            AddArgument(startInfo, "99");
            AddArgument(startInfo, "--temp");
            AddArgument(startInfo, "0.2");
            AddArgument(startInfo, "--simple-io");
            AddArgument(startInfo, "--no-display-prompt");
            AddArgument(startInfo, "--no-warmup");
            AddArgument(startInfo, "--single-turn");
            AddArgument(startInfo, "--reasoning");
            AddArgument(startInfo, "off");
            AddArgument(startInfo, "--reasoning-budget");
            AddArgument(startInfo, "0");
            AddArgument(startInfo, "--offline");
            AddArgument(startInfo, "--log-disable");

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            log($"Starting llama-cli: {Path.GetFileName(model.Path)}");
            OwnedProcessRegistry.Shared.Start(process, "LlamaCliRuntimeService");

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort cancellation for debug tooling.
                }
            });

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;
            if (!string.IsNullOrWhiteSpace(error))
            {
                log(error.Trim());
            }

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException($"llama-cli exited with code {process.ExitCode}.");
            }

            return CleanOutput(output);
        }
        finally
        {
            TryDelete(promptPath);
        }
    }

    private string CreatePromptFile(DebugModelInfo model, IReadOnlyList<DebugChatMessage> history, string userMessage)
    {
        Directory.CreateDirectory(AppDataPaths.BaseDirectory);
        var path = Path.Combine(AppDataPaths.BaseDirectory, $"debug-prompt-{Guid.NewGuid():N}.txt");
        var builder = new StringBuilder();
        builder.AppendLine(_coreIdentityService.BuildSystemPrompt(model, CoreInteractionMode.PlainChat, "llama.cpp llama-cli"));
        builder.AppendLine();

        foreach (var message in SelectHistoryForPrompt(history, 8))
        {
            if (IsMemoryRole(message.Role))
            {
                builder.AppendLine($"Служебная память AI HUB: {message.Text}");
                continue;
            }

            builder.AppendLine($"{message.Role}: {message.Text}");
        }

        builder.AppendLine($"Пользователь: {userMessage}");
        builder.AppendLine("Модель:");
        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        return path;
    }

    private static IEnumerable<DebugChatMessage> SelectHistoryForPrompt(IReadOnlyList<DebugChatMessage> history, int recentCount)
    {
        return history
            .Where(message => IsMemoryRole(message.Role))
            .TakeLast(1)
            .Concat(history.Where(message => !IsMemoryRole(message.Role)).TakeLast(recentCount));
    }

    private static bool IsMemoryRole(string role) =>
        role.Contains("memory", StringComparison.OrdinalIgnoreCase)
        || role.Contains("память", StringComparison.OrdinalIgnoreCase);

    private static string CleanOutput(string output)
    {
        var allLines = output
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var linesAfterPrompt = allLines
            .SkipWhile(line => !line.StartsWith("> ", StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .ToList();

        var sourceLines = linesAfterPrompt.Count > 0 ? linesAfterPrompt : allLines;
        var lines = sourceLines
            .Where(line => !IsBackendNoiseLine(line))
            .Where(line => !IsPromptEchoLine(line))
            .ToList();

        return lines.Count == 0
            ? "(empty response)"
            : string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsBackendNoiseLine(string line)
    {
        return line.Contains("llama.cpp", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Loading model", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("build", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("model", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("modalities", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("available commands", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[ Prompt:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Exiting", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("/", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Ctrl+C", StringComparison.OrdinalIgnoreCase)
            || line.Contains("▀", StringComparison.OrdinalIgnoreCase)
            || line.Contains("▄", StringComparison.OrdinalIgnoreCase)
            || line.Contains("█", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPromptEchoLine(string line)
    {
        return line.StartsWith(">", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Ты диагностический чат AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Ты диагностическое ядро AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Служебная идентичность AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Паспорт текущей модели", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Паспорт компьютера пользователя AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Карточка пользователя AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Если пользователь спрашивает", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Правила обычного чата", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("У тебя нет доступа", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("У тебя нет прямого доступа", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Служебный контекст AI HUB", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Используй дату", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Не утверждай", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Текущая локальная", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("UTC-время", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Часовой пояс", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Примерное местоположение", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Источник местоположения", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Пользователь:", StringComparison.OrdinalIgnoreCase)
            || line.Equals("Модель:", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value)
    {
        startInfo.ArgumentList.Add(value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Debug temp cleanup is best-effort.
        }
    }
}
