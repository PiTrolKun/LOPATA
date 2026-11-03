namespace AIHub.Models;

public sealed record LetItOutAttachment(string Id, string Name, string Extension, string Kind,
    long Bytes, string Sha256, string Preview, DateTimeOffset AddedUtc);
