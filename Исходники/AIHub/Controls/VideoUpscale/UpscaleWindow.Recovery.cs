namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    internal double RecoveryPreviewPosition => _at;

    internal async Task RestoreRecoveryPreviewAsync(double position)
    {
        await Ready.Task.WaitAsync(_lifetime.Token);
        if (!_closed && !_busy && double.IsFinite(position) && position > 0 && System.IO.File.Exists(_project.Source))
            await Seek(Math.Min(position, _project.PreviewLength));
    }
}
