using AIHub.Models;

namespace AIHub;

public partial class MainWindow
{
    private AudioConversionWindow? _audioConversionWindow;
    internal void AcceptAudioShellRequest(AudioShellRequest request)
    {
        request = request.Validate();
        if (_audioConversionWindow is null)
        {
            _audioConversionWindow = new(this, L);
            _audioConversionWindow.Closed += (_, _) => _audioConversionWindow = null;
            _audioConversionWindow.AddPaths(request.Paths);
            _audioConversionWindow.Show();
        }
        else
        {
            _audioConversionWindow.AddPaths(request.Paths);
            if (_audioConversionWindow.WindowState == System.Windows.WindowState.Minimized)
                _audioConversionWindow.WindowState = System.Windows.WindowState.Normal;
            _audioConversionWindow.Activate();
        }
    }
}
