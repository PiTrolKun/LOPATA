using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed record ConstructorProjectSummary(string Data, string Revision);

internal sealed partial class ConstructorProject
{
    internal ConstructorProjectSummary ReadGemmaSummary(ConstructorEventSite site, bool includeCode)
    {
        var forms = ReadForms();
        var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var entries = new List<string>();
        var size = 0;
        foreach (var form in forms.OrderBy(form => form == site.Form ? 0 : 1).Take(14))
        {
            var layout = new ConstructorLayout(ReadForm(form));
            var elements = layout.Elements.Take(18).ToArray();
            var occurrences = elements.Cast<XElement?>().Prepend(null).SelectMany(element =>
                ConstructorSteps.Events(form, element).Where(name => name != "Startup" && (element ?? layout.Root).Attribute(name) is not null)
                    .Select(name => new ConstructorEventSite(form, element is null ? null : ConstructorLayout.Name(element), name)));
            var data = JsonSerializer.Serialize(new
            {
                form.Id,
                Title = ShortGemmaText(FormTitle(form), 100),
                Elements = elements.Select(element => new { Id = ConstructorLayout.Name(element), Kind = ConstructorLayout.Kind(element).ToString(),
                    Caption = ShortGemmaText(ConstructorLayout.Caption(element), 70) }),
                BoundEvents = occurrences.Take(8).Select(binding => new
                {
                    binding.Element, binding.Event,
                    Steps = ReadChain(binding).Steps.Take(5).Select(step =>
                        step.Spec is { } spec ? spec.Kind.ToString() + "(" + ShortGemmaText(string.Join(", ", spec.Arguments), 150) + ")" : "manual C#")
                })
            }, json);
            if (size + data.Length > 4900)
            {
                // Retain later small forms even if an earlier form's excerpt is too large.
                continue;
            }
            entries.Add(data);
            size += data.Length;
        }
        var code = includeCode ? ReadCurrentFormCode() : null;
        var snapshot = "{\"Forms\":[" + string.Join(",", entries) + "],\"TotalForms\":" + forms.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"CurrentCodeBehindExcerpt\":" + JsonSerializer.Serialize(code, json) + "}";
        return new(snapshot, Fingerprint([snapshot]));

        string? ReadCurrentFormCode()
        {
            var path = (site.IsStartup ? ApplicationPath() : FormPath(site.Form)) + ".cs";
            SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) return null;
            using var reader = File.OpenText(path);
            var buffer = new char[2600];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, count);
        }
    }

    private static string ShortGemmaText(string value, int maximum) => value[..Math.Min(value.Length, maximum)];
}
