namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    private long _recommendedGpuSpare;
    public long? RecommendedGpuSpareBytes
    {
        get
        {
            try
            {
                var bytes = Volatile.Read(ref _recommendedGpuSpare);
                return _process is { HasExited: false } && ContextCapacity > 0 && bytes > 0 ? bytes : null;
            }
            catch (InvalidOperationException) { return null; }
        }
    }
}
