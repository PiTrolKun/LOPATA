namespace Lopata.Collection;

/// <summary>The manifest selected another immutable worker generation; this is a normal handoff.</summary>
public sealed class CollectionWorkerSupersededException()
    : Exception("A newer collection worker generation has been selected.");
