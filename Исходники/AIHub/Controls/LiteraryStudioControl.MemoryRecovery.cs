using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    public Func<CancellationToken, Task<LiteraryMemoryRecoveryChoice>>? MemoryRecoveryAsync { get; set; }

    private async Task<ParagraphReply> RequestWithMemoryRecoveryAsync(StudioRequest request,
        Func<StudioRequest, Task<ParagraphReply>> send, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return await send(request); }
            catch (Exception error) when (CanOfferMemoryRecovery(error))
            {
                NotifyRequestNeedsAttention();
                var choice = MemoryRecoveryAsync is not null ? await MemoryRecoveryAsync(token)
                    : LiteraryMemoryRecoveryDialog.Show(this, _l, token);
                token.ThrowIfCancellationRequested();
                if (choice == LiteraryMemoryRecoveryChoice.Cancel) throw new OperationCanceledException(token);
                request = request with { RuntimeOptions = new(UseRamReserve: choice == LiteraryMemoryRecoveryChoice.UseRam, RefreshMemory: true) };
                _status.Text = _l(choice == LiteraryMemoryRecoveryChoice.UseRam ? "Literary.MemoryRecovery.UsingRam" : "Literary.MemoryRecovery.Checking");
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }

    private static bool CanOfferMemoryRecovery(Exception error) => error is ImageAnalysisContextExhaustedException
        { OutputTruncated: false, Budget.CanUseRamReserve: true }
        || error is LiteraryGpuContextUnavailableException { CanUseRamReserve: true };
}
