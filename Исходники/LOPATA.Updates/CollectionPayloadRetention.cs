using System.Text.RegularExpressions;

namespace Lopata.Updates;

/// <summary>Explorer may retain a loaded DLL after the application switches payload generations.</summary>
public static class CollectionPayloadRetention
{
    public static bool IsVersioned(UpdateFile file) => file.Root == "app" && Regex.IsMatch(file.Path,
        @"\ACollection/[a-f0-9]{24}(?:-[a-f0-9]{8})?/(?:[A-Za-z0-9][A-Za-z0-9._-]*/)*[A-Za-z0-9][A-Za-z0-9._-]*\z",
        RegexOptions.CultureInvariant);
}
