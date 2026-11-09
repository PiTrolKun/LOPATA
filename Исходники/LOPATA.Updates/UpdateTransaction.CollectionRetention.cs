using System.Security.Cryptography;
using System.Text.Json;

namespace Lopata.Updates;

public sealed partial class UpdateTransaction
{
    private string CollectionOwnershipDirectory => Path.Combine(_stateDirectory, "collection-ownership");

    private void RememberCollectionOwnership(SignedManifest? signed)
    {
        if (signed is null || !signed.Verify(_keys).Files.Any(CollectionPayloadRetention.IsVersioned)) return;
        // Retain the signed authority, not an unsigned list that could grant ownership of user files.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(signed, UpdateManifest.JsonOptions);
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.Combine(CollectionOwnershipDirectory, id + ".signed.json");
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) DurableUpdateFiles.WriteJson(path, signed);
    }

    private Dictionary<string, UpdateFile> ReadCollectionOwnership()
    {
        SafeUpdatePath.RejectLinks(CollectionOwnershipDirectory);
        var owned = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(CollectionOwnershipDirectory)) return owned;
        foreach (var path in Directory.EnumerateFiles(CollectionOwnershipDirectory, "*.signed.json"))
        {
            SafeUpdatePath.RejectLinks(path);
            if (new FileInfo(path).Length > SignedManifest.MaximumEnvelopeBytes) throw new InvalidDataException("Oversized collection ownership receipt.");
            var manifest = SignedManifest.Read(File.ReadAllBytes(path)).Verify(_keys);
            foreach (var file in manifest.Files.Where(CollectionPayloadRetention.IsVersioned))
            {
                if (owned.TryGetValue(file.Key, out var previous) && (previous.Sha256 != file.Sha256 || previous.Size != file.Size))
                    throw new InvalidDataException("A collection generation changed its signed content.");
                owned[file.Key] = file;
            }
        }
        return owned;
    }
}
