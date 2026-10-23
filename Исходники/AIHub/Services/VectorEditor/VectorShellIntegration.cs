using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AIHub.Services.VectorEditor;

/// <summary>Owned HKCU OpenWith entries. Never writes UserChoice or replaces another application's SVG default.</summary>
public sealed class VectorShellIntegration
{
    private const string OwnerValue = "LOPATA.Owner";
    private const string Marker = "vector-editor-v1";
    private readonly string _executable, _root;
    private readonly bool _test;
    public string ProgId { get; }
    public VectorShellIntegration(string executable,string? testRoot=null)
    {
        _executable=Path.GetFullPath(executable);
        if(!Path.IsPathFullyQualified(executable)||executable.IndexOfAny(['"','\r','\n','\0'])>=0)throw new ArgumentException("Invalid executable.");
        if(testRoot is not null&&(!testRoot.StartsWith(@"Software\AIHub.Tests\",StringComparison.Ordinal)||!Guid.TryParseExact(testRoot[@"Software\AIHub.Tests\".Length..],"N",out _)))throw new ArgumentException("Invalid test root.");
        _root=testRoot??@"Software\Classes";_test=testRoot is not null;
        ProgId="LOPATA.Svg."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_executable.ToUpperInvariant())))[..16];
    }
    public void Apply(bool enabled,string title)
    {
        var path=_root+"\\"+ProgId;using(var existing=Registry.CurrentUser.OpenSubKey(path))
        {
            if(existing is not null&&(!Equals(existing.GetValue(OwnerValue),Marker)||!Equals(existing.GetValue("LOPATA.Executable"),_executable)))
                throw new IOException("SVG registration belongs to another application.");
        }
        var before=Capture(path);
        var references=new[]{".svg",".lopataSVG"}.Select(ext=>{
            var key=_root+"\\"+ext+@"\OpenWithProgids";using var current=Registry.CurrentUser.OpenSubKey(key);
            var value=current?.GetValue(ProgId);
            return(Path:key,Value:value,Kind:value is null?(RegistryValueKind?)null:current!.GetValueKind(ProgId));
        }).ToArray();
        var projectPath=_root+@"\.lopataSVG";using var previousProject=Registry.CurrentUser.OpenSubKey(projectPath);
        var defaultBefore=previousProject?.GetValue("");var defaultKind=defaultBefore is null?RegistryValueKind.String:previousProject!.GetValueKind("");
        try
        {
            if(enabled)
            {
                using(var key=Registry.CurrentUser.CreateSubKey(path)){
                    key.SetValue(OwnerValue,Marker);key.SetValue("LOPATA.Executable",_executable);key.SetValue("",title);
                    using var icon=key.CreateSubKey("DefaultIcon");icon.SetValue("",'"'+Path.Combine(Path.GetDirectoryName(_executable)!,"Assets","SvgFileIcon.ico")+'"');
                    using var command=key.CreateSubKey(@"shell\open\command");command.SetValue("",'"'+_executable+"\" --svg -- \"%1\"");
                }
                foreach(var r in references){using var key=Registry.CurrentUser.CreateSubKey(r.Path);key.SetValue(ProgId,"",RegistryValueKind.String);}
                // Our private project extension gets a default only if the user has not already assigned one.
                using var project=Registry.CurrentUser.CreateSubKey(projectPath);
                if(string.IsNullOrEmpty(project.GetValue("") as string))project.SetValue("",ProgId);
            }
            else
            {
                foreach(var r in references){using var key=Registry.CurrentUser.OpenSubKey(r.Path,true);key?.DeleteValue(ProgId,false);}
                using(var project=Registry.CurrentUser.OpenSubKey(projectPath,true))if(Equals(project?.GetValue(""),ProgId))project!.DeleteValue("",false);
                if(before is not null)Registry.CurrentUser.DeleteSubKeyTree(path,false);
            }
            if(!_test)SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
        }
        catch
        {
            Restore(path,before);
            foreach(var r in references){using var key=Registry.CurrentUser.CreateSubKey(r.Path);if(r.Value is null)key.DeleteValue(ProgId,false);else key.SetValue(ProgId,r.Value,r.Kind??RegistryValueKind.String);}
            using var project=Registry.CurrentUser.CreateSubKey(projectPath);if(defaultBefore is null)project.DeleteValue("",false);else project.SetValue("",defaultBefore,defaultKind);
            throw;
        }
    }
    private sealed record Snapshot(Dictionary<string,(object Value,RegistryValueKind Kind)> Values,Dictionary<string,Snapshot> Children);
    private static Snapshot? Capture(string path)
    {using var key=Registry.CurrentUser.OpenSubKey(path);return key is null?null:Read(key);}
    private static Snapshot Read(RegistryKey key)=>new(key.GetValueNames().ToDictionary(n=>n,n=>(key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!,key.GetValueKind(n))),key.GetSubKeyNames().ToDictionary(n=>n,n=>{using var child=key.OpenSubKey(n)!;return Read(child);}));
    private static void Restore(string path,Snapshot? snapshot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(path,false);if(snapshot is null)return;using var key=Registry.CurrentUser.CreateSubKey(path);Write(key,snapshot);
    }
    private static void Write(RegistryKey key,Snapshot value)
    {foreach(var p in value.Values)key.SetValue(p.Key,p.Value.Value,p.Value.Kind);foreach(var p in value.Children){using var child=key.CreateSubKey(p.Key);Write(child,p.Value);}}
    [DllImport("shell32.dll")]private static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);
}
