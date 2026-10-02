using System.Windows.Media.Imaging;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ScreenshotDeliveryResult(IReadOnlyList<string> Files, bool ClipboardWritten, bool TextKept);

/// <summary>Shared output policy: insure the frame before asking to replace clipboard text.
/// The clipboard adapter only reports presence/version, never reads text.</summary>
public sealed class ScreenshotDelivery(Func<bool> containsText, Func<uint> sequence,
    Action<BitmapSource> setImage, Func<bool> confirmReplace)
{
    public async Task<ScreenshotDeliveryResult> DeliverAsync(ScreenCaptureSettings settings, CaptureSource source,
        BitmapSource original, BitmapSource? scaled, CancellationToken token)
    {
        var output = settings.Outputs[source];
        if (!output.File && !output.Clipboard) throw new InvalidOperationException("No screenshot output selected.");
        IReadOnlyList<string> saved = [];
        try
        {
            if (output.File) saved = await Task.Run(() => ScreenshotFiles.Save(settings, source, original, scaled), token);
            if (!output.Clipboard) return new(saved, false, false);
            while (containsText())
            {
                if (saved.Count == 0) saved = await Task.Run(() => ScreenshotFiles.Save(settings, source, original, scaled), token);
                token.ThrowIfCancellationRequested();
                var before = sequence();
                if (!confirmReplace()) return new(saved, false, true);
                if (before == sequence()) break;
            }
            token.ThrowIfCancellationRequested();
            setImage(scaled ?? original);
            return new(saved, true, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new ScreenshotDeliveryException(saved, error); }
    }
}

public sealed class ScreenshotDeliveryException(IReadOnlyList<string> files, Exception inner)
    : Exception(inner.Message, inner)
{ public IReadOnlyList<string> Files { get; } = files; }
