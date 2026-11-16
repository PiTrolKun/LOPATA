using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed partial class BackgroundOperationExecution
{
    private FileStream? _processOwnership;
    private int _ownershipUsers;

    internal IDisposable? TryOwnProcess(bool checkCurrent = true)
    {
        lock (_gate)
        {
            if (_processOwnership is null)
            {
                var ownership = _store.TryOwnOperation();
                if (ownership is null) return null;
                try
                {
                    if (checkCurrent && !MatchesDurableState())
                    {
                        ownership.Dispose();
                        return null;
                    }
                    _processOwnership = ownership;
                }
                catch
                {
                    ownership.Dispose();
                    throw;
                }
            }
            ++_ownershipUsers;
            return new Ownership(this);
        }
    }

    private bool MatchesDurableState()
    {
        var durable = _store.Load();
        if (durable is null)
            return _state is null || _state is { Private: true } && IsFinal(_state.Phase);
        return _state is not null && durable.Id == _state.Id && durable.UpdatedUtc == _state.UpdatedUtc;
    }

    internal IDisposable OwnProcess() =>
        TryOwnProcess() ?? throw new BackgroundOperationWaitingException("Tray.OperationHeldElsewhere");

    internal void SaveMirror(BackgroundOperationStore mirror, BackgroundOperationState snapshot)
    {
        using var ownership = TryOwnProcess();
        if (ownership is not null) mirror.Save(snapshot);
    }

    private sealed class Ownership(BackgroundOperationExecution owner) : IDisposable
    {
        private BackgroundOperationExecution? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } execution) return;
            lock (execution._gate)
            {
                if (--execution._ownershipUsers != 0) return;
                var stream = execution._processOwnership;
                execution._processOwnership = null;
                stream?.Dispose();
            }
        }
    }
}
