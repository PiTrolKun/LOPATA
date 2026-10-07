using System.Diagnostics;
using System.IO;

namespace AIHub.Services;

/// <summary>Keep isolated imports and Windows DLL loads valid beyond MAX_PATH.</summary>
internal static class ManagedPythonLaunch
{
    internal const string Prelude = "import sys,os\n"
        // The pinned ROCm 7.2.1 distribution provides only the 'custom' package.
        // Disallow external SDK overrides and its stdout-writing offload-arch discovery.
        + "for k in list(os.environ):\n if k.startswith('ROCM_SDK_'): del os.environ[k]\n"
        + "os.environ['ROCM_SDK_TARGET_FAMILY']='custom'\n"
        + "sys.stdout.reconfigure(encoding='utf-8',write_through=True)\n"
        + "sys.stderr.reconfigure(encoding='utf-8',write_through=True)\n"
        + "sys.path[:]=[p if p.startswith('\\\\\\\\?\\\\') else ('\\\\\\\\?\\\\UNC\\\\'+p[2:] if p.startswith('\\\\\\\\') else '\\\\\\\\?\\\\'+os.path.abspath(p)) for p in sys.path]\n";

    internal static void ScriptArguments(ProcessStartInfo info, IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        if (values.Length >= 2 && values[0] == "-c")
        {
            info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
            info.ArgumentList.Add(Prelude + values[1]);
            foreach (var value in values.Skip(2)) info.ArgumentList.Add(value);
            return;
        }
        if (values.Length >= 2 && values[0] == "-m"
            && System.Text.RegularExpressions.Regex.IsMatch(values[1], @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z"))
        {
            info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
            info.ArgumentList.Add(Prelude + "import runpy\nmodule=sys.argv[1]; sys.argv=sys.argv[1:]; runpy.run_module(module,run_name='__main__',alter_sys=True)\n");
            foreach (var value in values.Skip(1)) info.ArgumentList.Add(value);
            return;
        }
        if (values.Length == 0 || !Path.IsPathFullyQualified(values[0]))
            throw new ArgumentException("A fully qualified Python script path is required.", nameof(arguments));
        info.ArgumentList.Add("-I"); info.ArgumentList.Add("-B"); info.ArgumentList.Add("-c");
        info.ArgumentList.Add(Prelude + "import runpy\nscript=sys.argv[1]; sys.argv=sys.argv[1:]; runpy.run_path(script,run_name='__main__')\n");
        foreach (var argument in values) info.ArgumentList.Add(argument);
    }

    internal static string OrdinaryPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
