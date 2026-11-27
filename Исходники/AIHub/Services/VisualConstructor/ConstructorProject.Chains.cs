using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed record ConstructorChainStep(string Id, string FilePath, string Source, ConstructorStepSpec? Spec);
internal sealed record ConstructorChain(ConstructorEventSite Site, string? Handler, string Source, bool Opaque,
    IReadOnlyList<ConstructorChainStep> Steps, string Revision, string MarkupSource);

internal sealed partial class ConstructorProject
{
    private const string ChainHeader = "// LOPATA CHAIN 1 ";
    private const string StepHeader = "// LOPATA STEP 1 ";

    internal ConstructorChain ReadChain(ConstructorEventSite site)
    {
        var markup = ReadSite(site);
        var handler = (string?)markup.Node.Attribute(site.Event);
        if (string.IsNullOrEmpty(handler)) return Result("", false, []);
        var owner = ChainOwner(site);
        var shared = markup.Document.Root!.DescendantsAndSelf().Attributes().Count(attribute => attribute.Value == handler) > 1;
        if (IsActionId(handler))
        {
            var action = ReadAction(owner, handler)!;
            var spec = action.Kind is { } kind ? new ConstructorStepSpec((ConstructorStepKind)kind,
                kind == ConstructorActionKind.Back ? [] : [action.Target!]) : null;
            return Result(action.Source, shared, [new(handler, action.FilePath, action.Source, spec)]);
        }

        var path = IsChainId(handler) ? ChainPath(site, handler)
            : (site.IsStartup ? ApplicationPath() : FormPath(site.Form)) + ".cs";
        SafeUpdatePath.RejectLinks(path);
        var source = File.Exists(path) ? File.ReadAllText(path) : "";
        var firstLine = source.Split('\n', 2)[0].TrimEnd('\r');
        if (IsChainId(handler) && firstLine.StartsWith(ChainHeader, StringComparison.Ordinal))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<string[]>(firstLine[ChainHeader.Length..]);
                if (ids is { Length: > 0 } && ids.All(id => id is not null && (IsStepId(id) || IsActionId(id)))
                    && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length
                    && Normalize(ChainSource(site, handler, ids)) == Normalize(source))
                    return Result(source, shared, ids.Select(id => ReadStep(site, id)).ToArray());
            }
            catch (JsonException) { }
        }
        return Result(source, true, [new(handler, path, source, null)]);

        ConstructorChain Result(string source, bool opaque, IReadOnlyList<ConstructorChainStep> steps) =>
            new(site, handler, source, opaque, steps, Fingerprint([markup.Text, source, .. steps.Select(step => step.Source)]), markup.Text);
    }

    internal ConstructorChainStep CreateStep(ConstructorEventSite site, ConstructorStepSpec spec)
    {
        ConstructorSteps.Validate(spec, site, this, new ConstructorLayout(ReadForm(site.Form)));
        var id = "Step_" + Guid.NewGuid().ToString("N");
        return new ConstructorChainStep(id, ChainPath(site, id), StepSource(site, id, spec), spec);
    }

    internal void CommitChain(ConstructorChain expected, IReadOnlyList<ConstructorChainStep> steps)
    {
        var site = expected.Site;
        var current = ReadChain(site);
        if (current.Revision != expected.Revision) throw new InvalidOperationException("VisualConstructor.ProposalChanged");
        if (current.Opaque) throw new InvalidOperationException("VisualConstructor.ManualAction");

        var oldIds = current.Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        var newIds = steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        if (steps.Count != newIds.Count) throw new InvalidDataException("VisualConstructor.InvalidStep");
        var markup = ReadSite(site, current.MarkupSource);
        if (steps.Count == 0)
        {
            if (current.Handler is null) return;
            markup.Node.SetAttributeValue(site.Event, null);
            SaveSite(site, markup);
            if (IsChainId(current.Handler)) ArchiveCompiledSource(ChainPath(site, current.Handler));
        }
        else
        {
            EnsureNavigationSource();
            EnsureActionsRuntime();
            var newlyWritten = new List<(string Path, string Source)>();
            var chained = current.Handler is { } existing && IsChainId(existing);
            var handler = chained ? current.Handler! : "Chain_" + Guid.NewGuid().ToString("N");
            var source = ChainSource(site, handler, steps.Select(step => step.Id).ToArray());
            try
            {
                foreach (var step in steps.Where(step => !oldIds.Contains(step.Id)))
                {
                    if (step.Spec is null || !IsStepId(step.Id) || step.FilePath != ChainPath(site, step.Id))
                        throw new InvalidDataException("VisualConstructor.InvalidStep");
                    Stage(step.FilePath, step.Source);
                }
                var wrapper = ChainPath(site, handler);
                if (chained)
                {
                    if (ReadSite(site).Text != markup.Text) throw new InvalidOperationException("VisualConstructor.ProposalChanged");
                    WriteSource(wrapper, current.Source, source);
                }
                else
                {
                    Stage(wrapper, source);
                    markup.Node.SetAttributeValue(site.Event, handler);
                    SaveSite(site, markup);
                }
            }
            catch
            {
                foreach (var item in newlyWritten)
                {
                    if (File.Exists(item.Path) && File.ReadAllText(item.Path) == item.Source)
                        ArchiveCompiledSource(item.Path);
                }
                throw;
            }

            void Stage(string path, string content)
            {
                if (File.Exists(path))
                {
                    if (File.ReadAllText(path) == content) return;
                    throw new InvalidOperationException("VisualConstructor.ProposalChanged");
                }
                WriteSource(path, null, content);
                newlyWritten.Add((path, content));
            }
        }

        var files = ActionSourceFiles(ChainOwner(site)).ToArray();
        foreach (var removed in current.Steps.Where(step => !newIds.Contains(step.Id)))
        {
            if (files.Any(path => !path.Equals(removed.FilePath, StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(path).Contains(removed.Id, StringComparison.Ordinal))) continue;
            ArchiveCompiledSource(removed.FilePath);
        }
    }

    internal void ArchiveBinding(ConstructorForm owner, string handler)
    {
        if (IsActionId(handler)) { ArchiveAction(owner, handler); return; }
        if (!IsChainId(handler)) return;
        var path = ChainPath(new ConstructorEventSite(owner, null, "Loaded"), handler);
        if (!File.Exists(path)) return;
        var source = File.ReadAllText(path);
        ArchiveCompiledSource(path);
        var sources = ActionSourceFiles(owner).ToArray();
        foreach (var step in sources.Where(file => IsStepId(Path.GetFileNameWithoutExtension(file))
            || IsActionId(Path.GetFileNameWithoutExtension(file))))
        {
            var id = Path.GetFileNameWithoutExtension(step);
            if (source.Contains(id, StringComparison.Ordinal) && sources.All(file => file == step || !File.ReadAllText(file).Contains(id, StringComparison.Ordinal)))
                ArchiveCompiledSource(step);
        }
    }

    internal bool ReferencesElements(ConstructorForm form, string[] ids, string[] removedHandlers)
    {
        if (ids.Length == 0) return false;
        var layout = new ConstructorLayout(ReadForm(form));
        IEnumerable<(XElement? Element, string Event)> sites = layout.Elements.Cast<XElement?>().Prepend(null)
            .SelectMany(element => ConstructorSteps.Events(form, element).Where(name => name != "Startup")
                .Select(name => (element, name)));
        foreach (var (node, name) in sites)
        {
            var binding = (string?)(node ?? layout.Root).Attribute(name);
            if (binding is null || removedHandlers.Contains(binding)) continue;
            var chain = ReadChain(new(form, node is null ? null : ConstructorLayout.Name(node), name));
            foreach (var step in chain.Steps)
                if (ids.Any(id => step.Source.Contains(id, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    internal bool HasPreviousCode(ConstructorChainStep step)
    {
        var path = step.FilePath + ".previous";
        SafeUpdatePath.RejectLinks(path);
        return File.Exists(path);
    }

    internal void CommitStepCode(ConstructorChainStep step, string source)
    {
        if (source == step.Source) return;
        if (!ConstructorDirectory.IsWithin(DirectoryPath, step.FilePath) || !File.Exists(step.FilePath))
            throw new FileNotFoundException("VisualConstructor.CodeMissing");
        var revision = step.FilePath + ".previous";
        SafeUpdatePath.RejectLinks(revision);
        WriteSource(step.FilePath, step.Source, source, revision);
    }

    internal void RevertStepCode(ConstructorChainStep step)
    {
        if (!HasPreviousCode(step)) throw new FileNotFoundException("VisualConstructor.CodeMissing");
        CommitStepCode(step, File.ReadAllText(step.FilePath + ".previous"));
    }

    private ConstructorChainStep ReadStep(ConstructorEventSite site, string id)
    {
        if (IsActionId(id))
        {
            var action = ReadAction(ChainOwner(site), id)!;
            var spec = action.Kind is { } kind ? new ConstructorStepSpec((ConstructorStepKind)kind,
                kind == ConstructorActionKind.Back ? [] : [action.Target!]) : null;
            return new ConstructorChainStep(id, action.FilePath, action.Source, spec);
        }
        var path = ChainPath(site, id);
        var source = File.Exists(path) ? File.ReadAllText(path) : "";
        var firstLine = source.Split('\n', 2)[0].TrimEnd('\r');
        ConstructorStepSpec? result = null;
        if (firstLine.StartsWith(StepHeader, StringComparison.Ordinal))
        {
            try
            {
                var spec = JsonSerializer.Deserialize<ConstructorStepSpec>(firstLine[StepHeader.Length..]);
                if (spec is { Arguments: not null } && Enum.IsDefined(spec.Kind)
                    && spec.Arguments.Length == ConstructorSteps.Arguments(spec.Kind).Count && spec.Arguments.All(a => a is not null)
                    && (spec.Kind is not (ConstructorStepKind.Inside or ConstructorStepKind.Separate) || IsTarget(ConstructorActionKind.Inside, spec.Arguments[0]))
                    && Normalize(StepSource(site, id, spec)) == Normalize(source)) result = spec;
            }
            catch (JsonException) { }
        }
        return new ConstructorChainStep(id, path, source, result);
    }

    private sealed record SiteMarkup(string Text, XDocument Document, XElement Node);

    private SiteMarkup ReadSite(ConstructorEventSite site, string? knownSource = null)
    {
        var text = knownSource ?? (site.IsStartup ? File.ReadAllText(ApplicationPath()) : ReadForm(site.Form));
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = document.Root!;
        var node = site.Element is null ? root
            : root.Descendants().SingleOrDefault(element => ConstructorLayout.IsElement(element) && ConstructorLayout.Name(element) == site.Element);
        if (node is null || !ConstructorSteps.Events(site.Form, site.Element is null ? null : node).Contains(site.Event, StringComparer.Ordinal)
            || (string?)root.Attribute(Xaml + "Class") != "VisualApp." + site.ClassName)
            throw new InvalidDataException("VisualConstructor.InvalidEvent");
        return new SiteMarkup(text, document, node);
    }

    private void SaveSite(ConstructorEventSite site, SiteMarkup markup)
    {
        if (site.IsStartup) WriteSource(ApplicationPath(), markup.Text, markup.Document.ToString(SaveOptions.DisableFormatting));
        else SaveForm(site.Form, markup.Text, markup.Document);
    }

    private static ConstructorForm ChainOwner(ConstructorEventSite site) => site.IsStartup ? MainForm : site.Form;

    private string ChainPath(ConstructorEventSite site, string id)
    {
        if (!IsChainId(id) && !IsStepId(id)) throw new InvalidDataException("VisualConstructor.CodeMissing");
        var path = Path.Combine(ActionDirectory(ChainOwner(site)), id + ".cs");
        SafeUpdatePath.RejectLinks(path);
        return path;
    }

    private static bool IsChainId(string id) => IsTemplateId(id, "Chain_");
    private static bool IsStepId(string id) => IsTemplateId(id, "Step_");
    private static bool IsTemplateId(string id, string prefix) => id.StartsWith(prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(id[prefix.Length..], "N", out _);

    private static string Fingerprint(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    internal static string WrapGemmaCode(ConstructorEventSite site, ConstructorChainStep step, string body)
    {
        if (!IsStepId(step.Id) || body.Length > 12_000 || string.IsNullOrWhiteSpace(body))
            throw new InvalidDataException("VisualConstructor.InvalidGemmaReply");
        var lines = string.Join("\n", body.ReplaceLineEndings("\n").Split('\n').Select(line => "        " + line));
        return $$"""
            namespace VisualApp;

            public partial class {{site.ClassName}}
            {
                private async System.Threading.Tasks.Task {{step.Id}}(ActionContext ctx)
                {
                    await System.Threading.Tasks.Task.CompletedTask;
            {{lines}}
                }
            }
            """ + "\n";
    }

    private static void ArchiveCompiledSource(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        if (File.Exists(path)) File.Move(path, path + $"-{Guid.NewGuid():N}.draft");
    }

    private void EnsureActionsRuntime()
    {
        var path = Path.Combine(DirectoryPath, "FormActions.cs");
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) WriteSource(path, null, ConstructorActionsRuntime.Source);
    }

    private static string ChainSource(ConstructorEventSite site, string handler, IReadOnlyList<string> ids)
    {
        var body = new StringBuilder();
        if (site.Event == "KeyDown")
            body.AppendLine("        if (args is not System.Windows.Input.KeyEventArgs { Key: System.Windows.Input.Key.Enter }) return;");
        body.AppendLine($"        await FormActions.ExecuteAsync(this, sender, args, \"{handler}\", async ctx =>");
        body.AppendLine("        {");
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            body.AppendLine($"            ctx.StepNumber = {i + 1};");
            if (IsActionId(id)) body.AppendLine($"            {id}(sender, (System.Windows.RoutedEventArgs)args);");
            else body.AppendLine($"            await {id}(ctx);");
            body.AppendLine("            if (ctx.Stopped) return;");
        }
        if (ids.All(IsActionId)) body.AppendLine("            await System.Threading.Tasks.Task.CompletedTask;");
        body.AppendLine("        });");
        return ChainHeader + JsonSerializer.Serialize(ids) + "\n" +
            $$"""
            namespace VisualApp;

            public partial class {{site.ClassName}}
            {
                private async void {{handler}}(object sender, System.EventArgs args)
                {
            {{body.ToString().TrimEnd()}}
                }
            }
            """ + "\n";
    }

    private static string StepSource(ConstructorEventSite site, string id, ConstructorStepSpec spec)
    {
        string A(int index) => JsonSerializer.Serialize(spec.Arguments[index]);
        string V(int index) => $"ctx.Resolve({A(index)})";
        var call = spec.Kind switch
        {
            ConstructorStepKind.Inside or ConstructorStepKind.Separate =>
                $"FormNavigation.{spec.Kind}(ctx.Window ?? ctx.Sender, static () => new {spec.Arguments[0]}());",
            ConstructorStepKind.Back => "FormNavigation.Back(ctx.Window ?? ctx.Sender);",
            ConstructorStepKind.ReadValue => $"ctx.Set({A(1)}, ctx.Read({A(0)}));",
            ConstructorStepKind.SetValue => $"ctx.Write({A(0)}, {V(1)});",
            ConstructorStepKind.Variable => $"ctx.Set({A(0)}, {V(1)});",
            ConstructorStepKind.Formula => $"ctx.Set({A(1)}, ctx.Calculate({A(0)}));",
            ConstructorStepKind.Require => $"ctx.Require({V(0)}, {A(1)}, {V(2)}, {A(3)});",
            ConstructorStepKind.Message => $"ctx.Show({V(0)});",
            ConstructorStepKind.PlayErrorSound => "System.Media.SystemSounds.Hand.Play();",
            ConstructorStepKind.ChooseFile => $"ctx.ChooseTextFile({A(0)});",
            ConstructorStepKind.ReadFile => $"return ctx.ReadTextFileAsync({A(0)}, {A(1)});",
            ConstructorStepKind.Code => "// Write the action here using ctx.Get, ctx.Set, ctx.Read, or ctx.Write.",
            _ => DesktopStep(spec.Kind, args: A, resolved: V)
        };
        var exit = spec.Kind is ConstructorStepKind.ReadFile or ConstructorStepKind.Wait ? "" : "\n        return System.Threading.Tasks.Task.CompletedTask;";
        return StepHeader + JsonSerializer.Serialize(spec) + "\n" +
            $$"""
            namespace VisualApp;

            public partial class {{site.ClassName}}
            {
                private System.Threading.Tasks.Task {{id}}(ActionContext ctx)
                {
                    {{call}}{{exit}}
                }
            }
            """ + "\n";
    }
}
