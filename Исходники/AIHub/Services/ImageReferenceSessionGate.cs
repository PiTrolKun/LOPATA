using AIHub.Models;

namespace AIHub.Services;

/// <summary>Readiness is cached for the current application session, separately for each generation chat.</summary>
public sealed class ImageReferenceSessionGate
{
    private readonly Dictionary<string, ImageAnalysisBundleInstallationSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);
    public ImageAnalysisBundleInstallationSnapshot Get(string session, Func<ImageAnalysisBundleInstallationSnapshot> check)
    {
        if (!_sessions.TryGetValue(session, out var snapshot))
            _sessions[session] = snapshot = check();
        return snapshot;
    }
    public void Update(string session, ImageAnalysisBundleInstallationSnapshot snapshot) => _sessions[session] = snapshot;
}
