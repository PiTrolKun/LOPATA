using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lopata.Updates;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(SignedManifest))]
internal partial class UpdateJsonContext : JsonSerializerContext
{
}
