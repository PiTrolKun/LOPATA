using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls.VideoConverter;
using AIHub.Models;
using AIHub.Services.VideoConverter;

namespace AIHub;

public partial class MainWindow
{
    private VideoConverterWindow? _videoConverter;

    private VideoConverterWindow OpenVideoConverter(IEnumerable<string>? inputs = null)
    {
        if (_videoConverter is not { } window)
        {
            window = new(this, L, EnsureApplicationStartupAsync);
            _videoConverter = window;
            window.Closed += (_, _) => { if (_videoConverter == window) _videoConverter = null; };
        }
        if (inputs is not null) window.AddPaths(inputs);
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return window;
    }

    internal void AcceptVideoConverterRequest(VideoConverterRequest request) => OpenVideoConverter(request.Validate().Paths);

    private void RegisterVideoConverterOperation() => _backgroundOperations!.Register(ConversionBatch.Kind, async (state, token) =>
    {
        var checkpoint = state.Input.Deserialize<ConversionCheckpoint>() ?? throw new InvalidDataException("Некорректная контрольная точка конвертера.");
        var job = ConversionStore.ReadJob(checkpoint.JobId) ?? throw new FileNotFoundException("Очередь конвертера недоступна в рабочей папке.");
        await OpenVideoConverter().RestoreAsync(job, checkpoint, state, token);
    });
}
