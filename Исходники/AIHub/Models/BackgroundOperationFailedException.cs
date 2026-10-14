namespace AIHub.Models;

/// <summary>The executor has definitively failed; inputs/checkpoints remain, but the operation no longer owns the gate.</summary>
public sealed class BackgroundOperationFailedException(string reason) : Exception(reason);
