using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length == 5 && args[0] == "--verify")
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(path) ? System.Reflection.Assembly.LoadFrom(path) : null;
    };
    var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[1]));
    var parse = assembly.GetType("AIHub.Services.SpeechAssets.SpeechAssetMarkdown", true)!
        .GetMethod("Parse", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    using var registry = JsonDocument.Parse(File.ReadAllText(args[2]));
    var expected = registry.RootElement.GetProperty("entries").EnumerateArray().ToArray();
    foreach (var (language, path) in new[] { ("ru", args[3]), ("en", args[4]) })
    {
        var rows = ((System.Collections.IEnumerable)parse.Invoke(null, [File.ReadAllText(path)])!).Cast<object>().ToArray();
        if (rows.Length != expected.Length) throw new InvalidDataException($"{language}: expected {expected.Length} rows, got {rows.Length}");
        for (var index = 0; index < rows.Length; index++)
        {
            var type = rows[index].GetType();
            var text = (string)type.GetProperty("Text")!.GetValue(rows[index])!;
            var group = (string)type.GetProperty("Group")!.GetValue(rows[index])!;
            if (text != expected[index].GetProperty(language).GetString() || !group.Contains(expected[index].GetProperty("clip_id").GetString()!))
                throw new InvalidDataException($"{language}: row {index + 1} differs from the registry: actual={JsonSerializer.Serialize(text)}, expected={JsonSerializer.Serialize(expected[index].GetProperty(language).GetString())}, group={group}");
        }
        Console.WriteLine($"{language}: {rows.Length} rows imported by the actual utility parser; text and clip IDs match.");
    }
    return;
}

if (args.Length != 3) throw new ArgumentException("Usage: CatalogScan <source> <localization.json> <output.json>");
var source = Path.GetFullPath(args[0]);
var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!.Keys.ToHashSet();
var sink = new Regex("status|progress|notif|notice|attention|warn|alert|toast|balloon|stage|phase|report|checkpoint|error|fail", RegexOptions.IgnoreCase);
var model = new Regex("model|runtime|worker|engine|inference|narrator|download|memory|execution|background", RegexOptions.IgnoreCase);
var items = new List<object>(); var enums = new List<object>(); var events = new List<object>(); var dynamicSinks = new List<object>();
var trees = new List<(string File, SyntaxNode Root)>();
foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
    if (relative.Split('/').Any(p => p is "bin" or "obj" or "node_modules" or "Vendor" or "AIHub.CatalogProbe" || p.EndsWith(".Tests"))) continue;
    trees.Add((relative, CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot()));
}
string Class(SyntaxNode node) => string.Join(".", node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(n => n.Identifier.ValueText));
string Name(InvocationExpressionSyntax invocation) => invocation.Expression switch
{
    IdentifierNameSyntax id => id.Identifier.ValueText,
    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
    _ => ""
};
string? Prefix(SyntaxNode node)
{
    var result = node.DescendantNodes().OfType<BinaryExpressionSyntax>().FirstOrDefault(b => b.IsKind(SyntaxKind.AddExpression)
        && b.Left is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.EndsWith('.'));
    return (result?.Left as LiteralExpressionSyntax)?.Token.ValueText;
}
var methods = trees.SelectMany(t => t.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
    .GroupBy(m => (Class(m), m.Identifier.ValueText)).ToDictionary(g => g.Key, g => g.Select(Prefix).Where(p => p is not null).Distinct().ToArray());
foreach (var (file, root) in trees)
{
    foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        if (Regex.IsMatch(assignment.Left.ToString(), "(status|progress|notice|warning|stage|phase).*(Text|Message|Detail)$", RegexOptions.IgnoreCase)
            && assignment.Right is not LiteralExpressionSyntax)
            dynamicSinks.Add(new { File = file, Line = root.SyntaxTree.GetLineSpan(assignment.Span).StartLinePosition.Line + 1,
                Sink = assignment.Left.ToString(), Expression = assignment.Right.ToString() });
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        if (Regex.IsMatch(Name(invocation), "^(Notify|SetStatus|UpdateStatus|ShowStatus|ReportStatus|Report|Emit)$", RegexOptions.IgnoreCase)
            && !invocation.ArgumentList.Arguments.Any(a => a.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)))
            dynamicSinks.Add(new { File = file, Line = root.SyntaxTree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1,
                Sink = invocation.Expression.ToString(), Expression = invocation.ArgumentList.ToString() });
    foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>().Where(n =>
        n is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) || n is InterpolatedStringExpressionSyntax))
    {
        if (expression.Ancestors().Any(n => n is InterpolatedStringExpressionSyntax)) continue;
        var text = expression is LiteralExpressionSyntax literal ? literal.Token.ValueText
            : string.Concat(((InterpolatedStringExpressionSyntax)expression).Contents.Select(n => n is InterpolatedStringTextSyntax part ? part.TextToken.ValueText : "{value}"));
        var line = root.SyntaxTree.GetLineSpan(expression.Span).StartLinePosition.Line + 1;
        var ancestors = expression.Ancestors().ToArray();
        var assignment = ancestors.OfType<AssignmentExpressionSyntax>().FirstOrDefault()?.Left.ToString() ?? "";
        var invocations = ancestors.OfType<InvocationExpressionSyntax>().ToArray();
        var call = invocations.FirstOrDefault();
        var constructor = ancestors.OfType<ObjectCreationExpressionSyntax>().FirstOrDefault()?.Type.ToString() ?? "";
        var statusContext = sink.IsMatch(assignment) || invocations.Any(i => sink.IsMatch(Name(i))
            || Regex.IsMatch(i.Expression.ToString(), "MessageBox\\.Show|ShowMessage|ShowError|ShowWarning", RegexOptions.IgnoreCase))
            || sink.IsMatch(constructor) || constructor.EndsWith("Exception");
        var references = new HashSet<string>();
        if (keys.Contains(text)) references.Add(text);
        if (call is not null && call.ArgumentList.Arguments.FirstOrDefault()?.Expression == expression)
        {
            var outer = ancestors.OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var local = outer?.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(f => f.Identifier.ValueText == Name(call)).Select(Prefix).Where(p => p is not null) ?? [];
            var candidates = local.Concat(methods.GetValueOrDefault((Class(expression), Name(call))) ?? []);
            foreach (var prefix in candidates) if (keys.Contains(prefix + text)) references.Add(prefix + text);
        }
        if (references.Count > 0 || statusContext && Regex.IsMatch(text, @"\p{L}.*\s|\s.*\p{L}"))
            items.Add(new { File = file, Line = line, Text = text, Keys = references.Order().ToArray(), StatusContext = statusContext,
                Assignment = assignment, Calls = invocations.Take(4).Select(Name).ToArray(), Constructor = constructor });
        if (model.IsMatch(file) && invocations.Any(i => Name(i) is "Log" or "SetState" or "SetStage" or "SetPhase")
            && Regex.IsMatch(text, "^[a-z][a-z0-9_.-]{2,100}$"))
            events.Add(new { File = file, Line = line, Code = text });
    }
    foreach (var declaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        if (Regex.IsMatch(declaration.Identifier.ValueText, "State|Status|Phase|Stage"))
            foreach (var member in declaration.Members)
                enums.Add(new { File = file, Line = root.SyntaxTree.GetLineSpan(member.Span).StartLinePosition.Line + 1,
                    Type = declaration.Identifier.ValueText, Member = member.Identifier.ValueText });
}
File.WriteAllText(args[2], JsonSerializer.Serialize(new { Files = trees.Count, Items = items, Enums = enums, Events = events, DynamicSinks = dynamicSinks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Scanned {trees.Count} C# files: {items.Count} string occurrences, {enums.Count} enum states, {events.Count} model event codes.");
