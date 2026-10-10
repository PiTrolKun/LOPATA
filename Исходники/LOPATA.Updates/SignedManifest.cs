using System.Security.Cryptography;
using System.Text.Json;

namespace Lopata.Updates;

public sealed record SignedManifest(string KeyId, string Payload, string Signature)
{
    public const int MaximumEnvelopeBytes = 24 * 1024 * 1024;

    public static SignedManifest Sign(UpdateManifest manifest, string keyId, ECDsa key)
    {
        manifest.Validate();
        if (key.KeySize != 256) throw new InvalidDataException("Expected a P-256 signing key.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJsonContext.Default.UpdateManifest);
        return new(keyId, Convert.ToBase64String(bytes), Convert.ToBase64String(
            key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    public UpdateManifest Verify(IReadOnlyDictionary<string, string> publicKeys)
    {
        if (KeyId is null || !publicKeys.TryGetValue(KeyId, out var pem)
            || Payload is null || Payload.Length > MaximumEnvelopeBytes || Signature is null || Signature.Length > 128)
            throw new InvalidDataException("Untrusted manifest signing key or invalid envelope.");
        try
        {
            var bytes = Convert.FromBase64String(Payload);
            var signature = Convert.FromBase64String(Signature);
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256 || signature.Length != 64
                || !key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidDataException("Manifest signature verification failed.");
            RejectDuplicateProperties(bytes);
            var manifest = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateManifest)
                ?? throw new InvalidDataException("Empty manifest.");
            manifest.Validate();
            return manifest;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("Invalid signed manifest.", ex);
        }
    }

    public static SignedManifest Read(byte[] bytes)
    {
        if (bytes.Length > MaximumEnvelopeBytes) throw new InvalidDataException("Manifest is too large.");
        RejectDuplicateProperties(bytes);
        return JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.SignedManifest)
            ?? throw new InvalidDataException("Empty signed manifest.");
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate JSON property.");
        }
    }
}
