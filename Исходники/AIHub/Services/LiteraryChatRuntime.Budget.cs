using System.IO;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    private int _contextCapacity;
    private bool _resourcesChecked, _resizeAttempted;
    public int ContextCapacity => Volatile.Read(ref _contextCapacity);
    private void BeginBudgetOperation() { _resourcesChecked = false; _resizeAttempted = false; }
    private async Task CheckLoadedMemoryAsync(CancellationToken ct)
    {
        if (_resourcesChecked) return;
        _resourcesChecked = true;
        var free = await LiteraryGpuBudget.FreeBytesAsync(ct)
            ?? throw new IOException("Physical GPU memory inventory is unavailable.");
        _diagnostics?.Write("gpu_before_request", new { free, context = ContextCapacity });
        // Only a material loss of the safety reserve triggers unloading, never small fluctuations.
        if (free < 512 * LiteraryAutomaticBudget.MiB) StopProcess();
    }
    private async Task ReadCapacityAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(Server, "slots"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var slots = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var capacity = slots.RootElement[0].GetProperty("n_ctx").GetInt32();
        if (capacity <= LiteraryAutomaticBudget.SafetyTokens + LiteraryAutomaticBudget.MinimumReply)
            throw new IOException("The backend reported an unusable context capacity.");
        Volatile.Write(ref _contextCapacity, capacity);
        _diagnostics?.Write("automatic_context", new { capacity });
        Log("Automatic context capacity: " + capacity);
    }
    private async Task<int> AvailableReplyAsync(int input, CancellationToken ct)
    {
        if ((long)input + LiteraryAutomaticBudget.SafetyTokens + LiteraryAutomaticBudget.MinimumReply > ContextCapacity && !_resizeAttempted)
        {
            _resizeAttempted = true;
            // Messages and editor are authoritative; implicit slot history is disabled.
            StopProcess(); await PrepareAsync(ct).ConfigureAwait(false);
        }
        var reply = LiteraryAutomaticBudget.Reply(ContextCapacity, input);
        _diagnostics?.Write("automatic_budget", new { input, reply, context = ContextCapacity, reserve = LiteraryAutomaticBudget.SafetyTokens });
        return reply;
    }
}
