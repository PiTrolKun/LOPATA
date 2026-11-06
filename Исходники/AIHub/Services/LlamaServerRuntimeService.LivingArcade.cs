namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    // A failed level stage is resumed explicitly, never silently repeated after native failure.
    private bool _disableAutomaticInferenceRetry;
    internal void ConfigurePrivateNoRetry() => _disableAutomaticInferenceRetry = true;
}
