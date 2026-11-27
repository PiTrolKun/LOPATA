using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed partial class ConstructorProject
{
    private const string ProjectVersion = "LopataVisualProjectVersion";
    internal const string Scenario = "visual_constructor";
    internal const string WindowFile = "MainWindow.xaml";
    internal static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> BuildFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs" };

    private ConstructorProject(string file, string assemblyName)
    {
        FilePath = file;
        AssemblyName = assemblyName;
    }

    internal string FilePath { get; }
    internal string DirectoryPath => Path.GetDirectoryName(FilePath)!;
    internal string Name => Path.GetFileNameWithoutExtension(FilePath);
    internal string AssemblyName { get; }
    internal string WindowPath => Path.Combine(DirectoryPath, WindowFile);

    internal static ConstructorProject Open(string path)
    {
        path = Path.GetFullPath(path);
        SafeUpdatePath.RejectLinks(path);
        if (!Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("VisualConstructor.UnsupportedProject");

        var project = XDocument.Load(path).Root;
        var properties = project?.Elements("PropertyGroup").SelectMany(group => group.Elements()).ToArray();
        if (project?.Name != "Project" || properties is null
            || properties.FirstOrDefault(property => property.Name == ProjectVersion)?.Value != "1")
            throw new InvalidDataException("VisualConstructor.UnsupportedProject");

        var assemblyName = properties.FirstOrDefault(property => property.Name == "AssemblyName")?.Value
            ?? Path.GetFileNameWithoutExtension(path);
        ValidateName(assemblyName);
        var result = new ConstructorProject(path, assemblyName);
        SafeUpdatePath.RejectLinks(result.WindowPath);
        if (XDocument.Load(result.WindowPath).Root?.Name != Wpf + "Window")
            throw new InvalidDataException("VisualConstructor.UnsupportedProject");
        return result;
    }

    internal static ConstructorProject Create(string parent, string name)
    {
        ValidateName(name);
        var destination = Path.Combine(Path.GetFullPath(parent), name);
        using var staged = ConstructorDirectory.Create(parent);
        var folder = staged.FullPath;
        var fileName = name + ".csproj";
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("OutputType", "WinExe"),
                new XElement("UseWPF", "true"),
                new XElement("TargetFramework", "net10.0-windows"),
                new XElement("Nullable", "enable"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("RootNamespace", "VisualApp"),
                new XElement("AssemblyName", name),
                new XElement(ProjectVersion, "1")),
            new XElement("ItemGroup",
                Metadata("LOPATA", ApplicationBuildVersion.Value),
                Metadata("LOPATA.Scenario", Scenario),
                Metadata("LOPATA.CreatedUtc", DateTimeOffset.UtcNow.ToString("O")),
                Metadata("LOPATA.ExportedUtc", "$(LopataExportedUtc)", "'$(LopataExportedUtc)' != ''"),
                Metadata("LOPATA.ExportVersion", "$(LopataExportVersion)", "'$(LopataExportVersion)' != ''")),
            new XElement("Target", new XAttribute("Name", "CopyRuntimeNotices"), new XAttribute("AfterTargets", "Publish"),
                new XAttribute("Condition", "'$(SelfContained)' == 'true'"),
                CopyRuntimeNotice("LICENSE"), CopyRuntimeNotice("LICENSE.TXT"), CopyRuntimeNotice("THIRD-PARTY-NOTICES.TXT")));
        project.Save(Path.Combine(folder, fileName));
        new XElement(Wpf + "Application", new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XAttribute(Xaml + "Class", "VisualApp.App"), new XAttribute("StartupUri", WindowFile))
            .Save(Path.Combine(folder, "App.xaml"));
        File.WriteAllText(Path.Combine(folder, "App.xaml.cs"),
            "namespace VisualApp;\n\npublic partial class App : System.Windows.Application { }\n");
        new XElement(Wpf + "Window", new XAttribute(XNamespace.Xmlns + "x", Xaml),
            new XAttribute(Xaml + "Class", "VisualApp.MainWindow"), new XAttribute("Title", XamlText(name)),
            new XAttribute("Width", "740"), new XAttribute("Height", "460"),
            new XAttribute("MinWidth", "320"), new XAttribute("MinHeight", "240"),
            new XElement(Wpf + "Grid")).Save(Path.Combine(folder, WindowFile));
        File.WriteAllText(Path.Combine(folder, "MainWindow.xaml.cs"), """
            namespace VisualApp;

            public partial class MainWindow : System.Windows.Window
            {
                public MainWindow() => InitializeComponent();
            }
            """ + "\n");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "/bin/\n/obj/\n/.vs/\n*.csproj.user\n");
        _ = Open(Path.Combine(folder, fileName));
        staged.MoveTo(destination);
        return Open(Path.Combine(destination, fileName));
    }

    internal ConstructorProject SaveAs(string parent, string name)
    {
        ValidateName(name);
        var destination = Path.Combine(Path.GetFullPath(parent), name);
        if (ConstructorDirectory.IsWithin(DirectoryPath, destination)
            || ConstructorDirectory.IsWithin(DirectoryPath, parent))
            throw new InvalidDataException("VisualConstructor.CopyInsideProject");

        using var staged = ConstructorDirectory.Create(parent);
        CopySources(DirectoryPath, staged.FullPath);
        var file = Open(Path.Combine(staged.FullPath, Path.GetFileName(FilePath)));
        var fileName = name + ".csproj";
        if (!string.Equals(Path.GetFileName(file.FilePath), fileName, StringComparison.OrdinalIgnoreCase))
            File.Move(file.FilePath, Path.Combine(staged.FullPath, fileName));
        staged.MoveTo(destination);
        return Open(Path.Combine(destination, fileName));
    }

    internal string ReadWindow() => ReadForm(MainForm);

    internal void SaveWindow(string previous, XDocument window) => SaveForm(MainForm, previous, window);

    internal static string XamlText(string value) => value.StartsWith('{') ? "{}" + value : value;

    private static XElement Metadata(string key, string value, string? condition = null)
    {
        var element = new XElement("AssemblyMetadata", new XAttribute("Include", key), new XAttribute("Value", value));
        if (condition is not null) element.Add(new XAttribute("Condition", condition));
        return element;
    }

    private static XElement CopyRuntimeNotice(string name)
    {
        var source = $"%(ResolvedRuntimePack.PackageDirectory)/{name}";
        return new XElement("Copy", new XAttribute("SourceFiles", source),
            new XAttribute("DestinationFolder", "$(PublishDir)Licenses/%(ResolvedRuntimePack.NuGetPackageId)"),
            new XAttribute("Condition", $"'%(ResolvedRuntimePack.PackageDirectory)' != '' and Exists('{source}')"));
    }

    private static void ValidateName(string name)
    {
        if (name.Length is 0 or > 100 || name.Trim() != name || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['$', '%', '@', ';', ',', '\'']) >= 0
            || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("VisualConstructor.InvalidName");
    }

    internal static void CopySources(string source, string destination, Func<string, bool>? include = null)
    {
        SafeUpdatePath.RejectLinks(source);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (include is not null && !include(file)) continue;
            SafeUpdatePath.RejectLinks(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: include is not null);
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(folder);
            if (BuildFolders.Contains(name)) continue;
            SafeUpdatePath.RejectLinks(folder);
            var target = Directory.CreateDirectory(Path.Combine(destination, name)).FullName;
            CopySources(folder, target, include);
        }
    }
}

internal sealed class ConstructorSaveConflict(string draftPath) : InvalidOperationException("VisualConstructor.ExternalChange")
{
    internal string DraftPath { get; } = draftPath;
}
