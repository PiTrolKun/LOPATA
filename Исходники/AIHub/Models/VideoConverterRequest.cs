using System.IO;
using AIHub.Services.VideoConverter;

namespace AIHub.Models;

internal sealed record VideoConverterRequest(string RequestId, IReadOnlyList<string> Paths)
{
    internal const string Argument = "--shell-video-convert";
    internal const byte Marker = 8;

    internal VideoConverterRequest Validate()
    {
        var guarded = new ImageShellRequest(RequestId, ImageShellOperation.WebP, Paths).Validate();
        if (guarded.Paths.Any(path => !ConversionOptions.Accepts(path)))
            throw new InvalidDataException("Неподдерживаемый файл в запросе конвертера.");
        return this with { RequestId = guarded.RequestId, Paths = guarded.Paths };
    }

    internal static VideoConverterRequest? Parse(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf(Argument);
        if (index < 0) return null;
        for (int i = 0; i < index; i++)
        {
            if (args[i] is "--background" or "--launched-by-updater") continue;
            if (args[i] == "--update-health" && i + 1 < index && Guid.TryParseExact(args[++i], "N", out _)) continue;
            throw new InvalidDataException("Некорректный запуск конвертера из Проводника.");
        }
        if (args.Count < index + 3 || args[index + 1] != "--") throw new InvalidDataException("Нет списка видео для конвертера.");
        return new VideoConverterRequest(Guid.NewGuid().ToString("N"), args.Skip(index + 2).ToArray()).Validate();
    }
}
