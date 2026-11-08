using System.Windows.Threading;

namespace AIHub.Tests;

// Native file dialogs and Windows text services keep apartment callbacks after a
// window closes. Keep their STA pumping through the suite, as the real app does.
internal sealed class OfficeProjectUiThread
{
    private static readonly Lazy<OfficeProjectUiThread> Shared = new(() => new());
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    private OfficeProjectUiThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Office project test UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    internal static Task RunAsync(Func<Task> action) =>
        Shared.Value._dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromMinutes(3));

    internal static void Close()
    {
        if (!Shared.IsValueCreated) return;
        var ui = Shared.Value;
        ui._dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
        if (!ui._thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Office project UI did not close.");
    }
}
