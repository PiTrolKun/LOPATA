using System.IO;
using System.Net.Http;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private PublisherCoordinator? _publisher;
    private PublisherGitHubClient? _publisherGitHub;
    private PublisherVkClient? _publisherVk;
    private HttpClient? _publisherHttp;
    private PublisherWindow? _publisherWindow;
    private string _publisherFailure = "Publisher.StoreUnavailable", _publisherShortened = "";

    private void InitializePublisher()
    {
        if (_publisher is not null) return;
        PublisherStore? store = null;
        try
        {
            store = new PublisherStore(Path.Combine(AppDataPaths.BaseDirectory, "Publisher"));
            _publisherHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
            _publisherGitHub = new(_publisherHttp); _publisherVk = new(_publisherHttp);
            _publisher = new(store, _publisherGitHub, _publisherVk);
            _publisherShortened = L("Publisher.Shortened");
            _publisher.ShortenedLabel = () => _publisherShortened;
            _publisher.Start();
        }
        catch (Exception error)
        {
            store?.Dispose(); _publisherHttp?.Dispose(); _publisherHttp = null;
            _publisherFailure = error is AIHub.Models.PublisherException known ? known.Key : "Publisher.StoreUnavailable";
            // Never record exception bodies from a credential-bearing request.
        }
    }
    private void OpenPublisherScenario()
    {
        InitializePublisher();
        if (_publisher is null || _publisherVk is null || _publisherGitHub is null)
        { System.Windows.MessageBox.Show(this, L(_publisherFailure), L("Publisher.Title")); return; }
        _publisherShortened = L("Publisher.Shortened");
        if (_publisherWindow is not null) { _publisherWindow.Show(); _publisherWindow.Activate(); return; }
        _publisherWindow = new(this, L, _publisher, _publisherVk, _publisherGitHub);
        _publisherWindow.Closed += (_, _) => _publisherWindow = null;
        _publisherWindow.Show();
    }
    private async Task StopPublisherAsync()
    {
        try { if (_publisher is not null) await _publisher.DisposeAsync(); }
        catch (Exception) { /* In-flight attempts are already durable; recover as uncertain next launch. */ }
        finally { _publisherHttp?.Dispose(); }
    }
}
