namespace Lopata.Collection;

/// <summary>Reads embedded music fields without decoding audio or assuming a project exists.</summary>
public static class CollectionMusicMetadata
{
    public static IReadOnlyDictionary<string, string> ReadTags(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return CollectionAudioOrigin.ReadTags(input, includeMusic: true);
    }
}
