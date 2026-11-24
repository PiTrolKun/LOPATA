using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace AIHub.Services.AnimatedAssets;

internal static partial class AnimationRenderer
{
    private const int TelegramMaxBytes = 256_000;
    private static readonly int[] TelegramQuality = [32, 38, 44, 50, 56, 62];

    public static async Task ExportAsync(string path, AnimationProject project, AnimationCard card, string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(path);
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var format = Path.GetExtension(destination).ToLowerInvariant();
        if (format is not (".png" or ".webp" or ".webm" or ".gif"))
            throw new InvalidDataException("AnimatedAssets.Error.Format");
        var telegram = format == ".webm" && project.ExportProfile is "telegram-sticker" or "telegram-emoji";
        if (telegram && card.Duration > TelegramMaximumDuration)
            throw new InvalidDataException("AnimatedAssets.Error.TelegramDuration");
        if (format == ".webm")
        {
            await AnimationModelService.PrepareBaseAsync(cancellationToken);
            if (!Path.GetFullPath(ffmpegPath).Equals(Path.GetFullPath(VideoEncoder.Executable), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("AnimatedAssets.Error.EncoderMissing");
        }

        await Task.Run(async () =>
        {
            var facts = AnimationOutputProvenance.Create(project, card, format[1..]);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                if (format == ".png")
                    ExportPng(temporary, project, card, facts, cancellationToken);
                else
                {
                    var emoji = telegram && project.ExportProfile == "telegram-emoji";
                    using var sequence = new FrameSequence(project, card, emoji ? 100 : ExportLongEdge, emoji,
                        format == ".webm", GetFramesPerSecond(card, telegram), cancellationToken);
                    if (format == ".webp")
                        ExportWebp(temporary, sequence, facts, cancellationToken);
                    else if (format == ".gif")
                        ExportGif(temporary, sequence, card.Duration, facts, cancellationToken);
                    else
                        await ExportWebmAsync(temporary, sequence, ffmpegPath, telegram, facts, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
                File.Move(temporary, destination, overwrite: true);
                CollectionExportJournal.Record(destination, facts.Scenario);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }, cancellationToken);
    }

    private static void ExportPng(string output, AnimationProject project, AnimationCard card, OutputProvenance facts,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var selected = card.Poses.Find(pose => pose.Id == project.PngPoseId);
        using var source = LoadSource(selected?.Image ?? card.Still ?? BaseImage(project, card));
        using var png = source.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("AnimatedAssets.Error.Encode");
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            png.SaveTo(stream);
        PngMetadataWriter.Write(output, new Dictionary<string, string>
        {
            ["Software"] = "LOPATA",
            ["LOPATA"] = facts.Json()
        }, token);
    }

    private static void ExportWebp(string output, FrameSequence sequence, OutputProvenance facts, CancellationToken token)
    {
        using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            AnimatedWebpWriter.Write(stream, sequence.Size, Frames(), 1000 / sequence.FramesPerSecond, token);
        OutputImageMetadata.WriteXmp(output, "webp", sequence.Size.Width, sequence.Size.Height, facts.Xmp(), token);

        IEnumerable<SKBitmap> Frames()
        {
            for (var index = 0; index < sequence.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                yield return sequence.Draw(index);
            }
        }
    }

    private static async Task ExportWebmAsync(string output, FrameSequence sequence, string ffmpegPath, bool telegram,
        OutputProvenance facts, CancellationToken token)
    {
        foreach (var quality in telegram ? TelegramQuality : [32])
        {
            await EncodeWebmAsync(output, sequence, ffmpegPath, quality, facts, token);
            var size = new FileInfo(output).Length;
            if (size == 0)
                throw new IOException("AnimatedAssets.Error.Encode");
            if (!telegram || size <= TelegramMaxBytes)
                return;
        }
        throw new IOException("AnimatedAssets.Error.TelegramSize");
    }

    private static async Task EncodeWebmAsync(string output, FrameSequence sequence, string ffmpegPath, int quality,
        OutputProvenance facts, CancellationToken token)
    {
        var width = sequence.Size.Width;
        var height = sequence.Size.Height;
        var info = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-filter_threads", "1",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", FormattableString.Invariant($"{width}x{height}"),
            "-framerate", sequence.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0", "-map", "0:v:0",
            "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-deadline", "good", "-cpu-used", "5", "-row-mt", "1",
            "-threads", "2", "-b:v", "0", "-crf", quality.ToString(CultureInfo.InvariantCulture), "-an",
            "-metadata", "comment=" + facts.Json(), "-metadata", "encoded_by=LOPATA " + facts.ProgramVersion,
            "-metadata", "creation_time=" + (facts.CreatedUtc ?? facts.ExportedUtc).ToString("O", CultureInfo.InvariantCulture),
            "-f", "webm", output
        ];
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(info, "AnimatedAssets.WebM");
        var diagnostics = process.StandardError.ReadToEndAsync();
        try
        {
            using var raw = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bytes = new byte[raw.ByteCount];
            for (var index = 0; index < sequence.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                using var frame = sequence.Draw(index).PeekPixels();
                if (!frame.ReadPixels(raw.Info, raw.GetPixels(), raw.RowBytes))
                    throw new InvalidDataException("AnimatedAssets.Error.Encode");
                Marshal.Copy(raw.GetPixels(), bytes, 0, bytes.Length);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                stall.CancelAfter(TimeSpan.FromSeconds(30));
                await process.StandardInput.BaseStream.WriteAsync(bytes, stall.Token);
            }
            process.StandardInput.Close();
            using var finish = CancellationTokenSource.CreateLinkedTokenSource(token);
            finish.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(finish.Token);
            var errors = await diagnostics;
            if (process.ExitCode != 0)
                throw new IOException("AnimatedAssets.Error.Encode", new IOException(errors));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("AnimatedAssets.Error.EncoderTimeout");
        }
        catch (IOException error) when (!error.Message.StartsWith("AnimatedAssets.", StringComparison.Ordinal))
        {
            throw new IOException("AnimatedAssets.Error.Encode", error);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await diagnostics;
        }
    }
}
